using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 双击开锁：双击带锁的物资箱，自动拿身上对应的钥匙卡打开。
    ///
    /// 原版必须把钥匙卡拖到箱子上。这里只自动化「找卡 + 确认」两步：
    /// 真正的开锁还是调用钥匙卡自己的 <c>onItemTargetItemFunc</c>，
    /// 也就是 <c>LockHelper.TryOpenLock</c> —— 消耗卡、换开箱贴图、
    /// 清违禁/赃物标记、播开锁音效，全都走原版，不会漏步骤。
    ///
    /// 不凭空造卡：卡必须是玩家容器里真实存在的一张，贴图还得对得上锁的类型；
    /// 万能卡（command_card）可以开任何锁。确认框点「是」之后会再校验一次，
    /// 防止弹窗期间卡被卖掉/丢掉/用掉。
    /// </summary>
    internal static class QuickUnlock
    {
        private const string HarmonyId = "local.qol.quick_unlock";

        private const string UniversalCard = "command_card";

        /// <summary>lockId → 钥匙卡的贴图名（游戏里各种卡就是按贴图区分的）。</summary>
        private static readonly Dictionary<string, string> CardSprites = new Dictionary<string, string>
        {
            { "LOCK_ID_MED", "med_card" },
            { "LOCK_ID_SEC", "sec_card" },
            { "LOCK_ID_SCI", "sci_card" },
            { "LOCK_ID_ENG", "eng_card" },
            { "LOCK_ID_SUP", "sup_card" },
            { "LOCK_ID_SER", "ser_card" },
            { "LOCK_ID_COMMAND", UniversalCard },
        };

        /// <summary>缺卡提示用的本地化键，和游戏物品名同一份。</summary>
        private static readonly Dictionary<string, string> CardNameKeys = new Dictionary<string, string>
        {
            { "LOCK_ID_MED", "item_med_keycard_name" },
            { "LOCK_ID_SEC", "item_sec_keycard_name" },
            { "LOCK_ID_SCI", "item_sci_keycard_name" },
            { "LOCK_ID_ENG", "item_eng_keycard_name" },
            { "LOCK_ID_SUP", "item_sup_keycard_name" },
            { "LOCK_ID_SER", "item_ser_keycard_name" },
            { "LOCK_ID_COMMAND", "item_cmd_keycard_name" },
        };

        private static Harmony _harmony;

        /// <summary>确认框开着的时候，别让第二次双击再叠一层。</summary>
        private static bool _dialogOpen;

        // ---------------------------------------------------------------
        // 挂载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            if (_harmony != null) return;

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(ItemMouseDoubleClickHandler), "DoubleClickAction",
                    new[] { typeof(GameItem), typeof(Vector2) });

                if (target == null)
                {
                    QLog.Warn("[双击开锁] 找不到 ItemMouseDoubleClickHandler.DoubleClickAction，功能未启用");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(QuickUnlock), nameof(DoubleClickPrefix)));
                QLog.Info("[双击开锁] 已挂载");
            }
            catch (Exception e)
            {
                QLog.Warn("[双击开锁] 挂载失败：" + e);
                _harmony = null;
            }
        }

        internal static void Remove()
        {
            try
            {
                if (_harmony != null) _harmony.UnpatchAll(HarmonyId);
            }
            catch
            {
                // 卸载阶段出错无所谓
            }

            _harmony = null;
            _dialogOpen = false;
        }

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                if (settings == null || settings.QuickUnlockEnabled == null) return false;
                return settings.QuickUnlockEnabled.Value;
            }
        }

        // ---------------------------------------------------------------
        // 双击入口
        // ---------------------------------------------------------------

        /// <summary>返回 false = 不执行原版双击（这次由我们接管）。</summary>
        private static bool DoubleClickPrefix(GameItem newItem, Vector2 mousePosition)
        {
            try
            {
                if (!Enabled) return true;
                if (newItem == null) return true;

                // 没上锁的箱子（以及所有普通物品）完全交给原版，双击照常打开
                if (!LockHelper.IsCurrentlyLocked(newItem)) return true;

                if (_dialogOpen)
                {
                    // 面板已经被关掉（或被别的功能复用）就复位，别把双击永久吃掉
                    GenericConfirmationUIManager current = GenericConfirmationUIManager.Instance;
                    if (current == null || current.panel == null || !current.panel.activeSelf)
                    {
                        _dialogOpen = false;
                    }
                    else
                    {
                        return false;
                    }
                }

                string lockId = LockHelper.GetLock(newItem);
                if (string.IsNullOrEmpty(lockId)) return true;

                GameItem card = FindCard(lockId);
                if (card == null)
                {
                    Notify("缺少 " + CardName(lockId));
                    return false;
                }

                ShowConfirm(card, newItem, lockId, mousePosition);
                return false;
            }
            catch (Exception e)
            {
                QLog.Warn("[双击开锁] 处理双击失败：" + e);
                return true;
            }
        }

        // ---------------------------------------------------------------
        // 找卡
        // ---------------------------------------------------------------

        private static GameItem FindCard(string lockId)
        {
            string wanted;
            if (!CardSprites.TryGetValue(lockId, out wanted) || string.IsNullOrEmpty(wanted)) return null;

            GameItem universal = null;
            List<GameItem> all = CollectPlayerItems();

            for (int i = 0; i < all.Count; i++)
            {
                GameItem item = all[i];
                if (item == null) continue;
                if (!item.IsGameItemType("ACCESS_CARD")) continue;
                if (!GeneralHelper.IsItemOwned(item)) continue;

                string sprite = item.spritePath;
                if (string.IsNullOrEmpty(sprite)) continue;

                if (sprite == wanted) return item;
                if (universal == null && sprite == UniversalCard) universal = item;
            }

            return universal;
        }

        /// <summary>玩家身上、后仓、前厅、地下的所有物品（含背包等嵌套容器）。</summary>
        private static List<GameItem> CollectPlayerItems()
        {
            var result = new List<GameItem>();

            EmporiumEntry store = EmporiumEntry.Instance;
            if (store == null) return result;

            GameInventory[] roots =
            {
                store.invElement,
                store.backInvinvElement,
                store.frontInvinvElement,
                store.afterhourInventory,
            };

            for (int i = 0; i < roots.Length; i++)
            {
                GameInventory root = roots[i];
                if (root == null) continue;

                try
                {
                    result.AddRange(GraphUtils.FindAllChildrenType<GameItem>(root));
                }
                catch (Exception e)
                {
                    QLog.Warn("[双击开锁] 遍历容器失败：" + e.Message);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------
        // 确认与开箱
        // ---------------------------------------------------------------

        private static void ShowConfirm(GameItem card, GameItem box, string lockId, Vector2 position)
        {
            GenericConfirmationUIManager panel = GenericConfirmationUIManager.Instance;
            if (panel == null)
            {
                Notify("当前场景不能开箱");
                return;
            }

            string body = "确定要打开「" + DisplayName(box) + "」吗？\n\n将消耗 1 张 " + DisplayName(card) + "。";

            _dialogOpen = true;
            panel.OpenPanel(
                body,
                "是",
                "否",
                delegate
                {
                    _dialogOpen = false;
                    panel.ClosePanel();
                    Confirm(card, box, lockId, position);
                },
                delegate
                {
                    _dialogOpen = false;
                    panel.ClosePanel();
                });
        }

        private static void Confirm(GameItem card, GameItem box, string lockId, Vector2 position)
        {
            try
            {
                // 弹窗期间卡可能被卖掉、丢掉或用掉 —— 再确认一次，绝不凭空开锁
                if (card == null || !GeneralHelper.IsItemOwned(card) || card.parentInventory == null)
                {
                    Notify("钥匙卡已经不在了");
                    return;
                }

                if (box == null)
                {
                    return;
                }

                if (!LockHelper.IsCurrentlyLocked(box))
                {
                    return; // 已经开了，不重复消耗
                }

                if (!CardMatches(card, lockId))
                {
                    Notify("缺少 " + CardName(lockId));
                    return;
                }

                if (card.onItemTargetItemFunc == null)
                {
                    QLog.Warn("[双击开锁] 这张钥匙卡没有开锁回调，放弃");
                    return;
                }

                card.onItemTargetItemFunc(card, box); // 原版：消耗卡 + 解锁 + 音效

                if (box.contentWindow != null && ItemMouseDoubleClickHandler.current != null)
                {
                    ItemMouseDoubleClickHandler.current.OpenContentAction(box, position);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[双击开锁] 开箱失败：" + e);
            }
        }

        private static bool CardMatches(GameItem card, string lockId)
        {
            if (card == null) return false;

            string sprite = card.spritePath;
            if (string.IsNullOrEmpty(sprite)) return false;

            string wanted;
            if (CardSprites.TryGetValue(lockId, out wanted) && sprite == wanted) return true;
            return sprite == UniversalCard;
        }

        // ---------------------------------------------------------------
        // 文案
        // ---------------------------------------------------------------

        private static string CardName(string lockId)
        {
            string key;
            if (lockId != null && CardNameKeys.TryGetValue(lockId, out key))
            {
                try
                {
                    string name = LocHelper.GetLocalizedItem(key);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch
                {
                    // 本地化取不到就退回通用说法
                }
            }

            return "对应的钥匙卡";
        }

        private static string DisplayName(GameItem item)
        {
            try
            {
                if (item == null) return "?";
                string name = item.GetDisplayName();
                return string.IsNullOrEmpty(name) ? "?" : name;
            }
            catch
            {
                return "?";
            }
        }

        private static void Notify(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(text);
            }
            catch
            {
                // 气泡只是提示，失败不影响开箱
            }
        }
    }
}
