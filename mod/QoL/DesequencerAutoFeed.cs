using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 解序器自动续卡：睡觉开始 / 第二天开始 / 每秒各检查一次——
    /// 输出槽有成品卡就收走（走私舱→后舱→柜台），输入槽空着就补一张白卡。
    /// 机器运行中（STATE_DESEQUENCING）一律不碰。
    /// </summary>
    internal static class DesequencerAutoFeed
    {
        internal const string StateTag = "DESEQUENCER_STATE_TAG";
        private const string Working = "STATE_DESEQUENCING";
        private const string BlankId = "blank_keycard";

        private static readonly FieldInfo BgField =
            AccessTools.Field(typeof(GameSlotInventory), "backgroundImage");

        private static readonly HashSet<string> Warned = new HashSet<string>();
        private static List<GameItem> _cache;
        private static float _cacheAt = -99f;
        private static float _poll;

        internal static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.DesequencerAuto.Value;
            }
        }

        internal static void Apply()
        {
            try
            {
                ModHook.OnGoingSleepEarly += OnSleep;   // 睡觉开始：今晚要有卡才能推进
                ModHook.OnGoingSleepLate += OnSleep;    // 第二天开始：夜里可能刚解完
                QLog.Info("解序器自动续卡已挂载");
            }
            catch (Exception e)
            {
                QLog.Error("解序器自动续卡挂载失败", e);
            }
        }

        internal static void Remove()
        {
            try
            {
                ModHook.OnGoingSleepEarly -= OnSleep;
                ModHook.OnGoingSleepLate -= OnSleep;
            }
            catch
            {
                // 卸载失败不影响游戏
            }
        }

        /// <summary>白天也每秒看一眼：成品槽一出现卡就收走，别等睡觉。</summary>
        internal static void Tick(float dt)
        {
            if (!Enabled) return;

            _poll += dt;
            if (_poll < 1f) return;
            _poll = 0f;

            foreach (GameItem machine in Machines())
            {
                Run(machine, false);
            }
        }

        private static void OnSleep()
        {
            if (!Enabled) return;

            foreach (GameItem machine in Machines())
            {
                Run(machine, true);
            }
        }

        // ---------------------------------------------------------------
        // 主流程
        // ---------------------------------------------------------------

        /// <param name="refill">睡觉时才补白卡；平时轮询只收成品。</param>
        private static void Run(GameItem machine, bool refill)
        {
            try
            {
                if (string.Equals(Str(machine, StateTag), Working, StringComparison.Ordinal))
                {
                    return;   // 正在解密：输入卡被锁，别动
                }

                List<GameSlotInventory> slots = GraphUtils.FindAllChildrenType<GameSlotInventory>(machine);
                GameSlotInventory input = FindSlot(slots, "blank_card");
                GameSlotInventory output = FindSlot(slots, "sec_keycard");

                if (input == null || output == null)
                {
                    WarnOnce("认不出解序器的输入/成品槽");
                    return;
                }

                // 1) 收成品（机器要求成品槽为空才能开下一张）
                if (output.childItem != null && !Store(output.childItem))
                {
                    WarnOnce("成品卡放不进任何容器，先留在输出槽");
                    return;
                }

                // 2) 补白卡
                if (!refill || input.childItem != null) return;

                GameItem blank = FindBlank();
                if (blank == null)
                {
                    Notice("解序器没白卡了");
                    return;
                }

                if (!Move(input, blank))
                {
                    WarnOnce("白卡放不进输入槽");
                    return;
                }

                QLog.Info("解序器续卡：已补上一张白卡");
            }
            catch (Exception e)
            {
                WarnOnce("处理解序器出错：" + e.Message);
            }
        }

        /// <summary>成品卡去处：走私舱（大的优先）→ 后舱 → 柜台；都不行就留在原处。</summary>
        private static bool Store(GameItem card)
        {
            try
            {
                if (!GeneralHelper.IsItemOwned(card)) GeneralHelper.SetItemOwned(card, true);
            }
            catch
            {
                // 归属标记失败不致命，放不进去会退回来
            }

            EmporiumEntry entry = EmporiumEntry.Instance;

            GameInventory bay = FindSmugglerBay();
            if (bay != null && Move(bay, card))
            {
                QLog.Info("解序器收卡：成品「" + card.name + "」已放进走私舱");
                return true;
            }

            if (entry != null && Move(entry.backInvinvElement, card))
            {
                QLog.Info("解序器收卡：成品「" + card.name + "」已放后舱");
                return true;
            }

            if (entry != null && Move(entry.frontInvinvElement, card))
            {
                QLog.Info("解序器收卡：成品「" + card.name + "」已放柜台");
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------
        // 搬运：先移出、再放入、用父容器验证；失败退回原处
        // ---------------------------------------------------------------

        private static bool Move(GameInventory target, GameItem item)
        {
            if (target == null || item == null) return false;

            GameInventory from = item.parentInventory;
            if (from == null || ReferenceEquals(from, target)) return false;

            try
            {
                if (target.IsInsertLocked()) return false;

                SlotMarker marker = target.TryFindOneValidInventorySlot(item);
                if (marker == null || marker.numTransfer < 1) return false;

                if (!from.Expel(item)) return false;

                marker.TryAcceptOnce(1);
                if (ReferenceEquals(item.parentInventory, target)) return true;

                TryMoveInto(from, item);   // 没放进去就退回，别弄丢
                WarnOnce("物品搬到 " + target.GetType().Name + " 后位置没变");
                return false;
            }
            catch (Exception e)
            {
                WarnOnce("搬运出错：" + e.Message);
                return false;
            }
        }

        private static bool TryMoveInto(GameInventory target, GameItem item)
        {
            if (target == null || item == null) return false;

            try
            {
                if (target.IsInsertLocked()) return false;

                SlotMarker marker = target.TryFindOneValidInventorySlot(item);
                return marker != null && marker.numTransfer > 0 && marker.TryAcceptOnce(1) == 1;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 查找
        // ---------------------------------------------------------------

        /// <summary>槽位靠背景贴图名认出（输入槽 blank_card、成品槽 sec_keycard），不猜顺序。</summary>
        private static GameSlotInventory FindSlot(List<GameSlotInventory> slots, string spriteName)
        {
            if (slots == null || BgField == null) return null;

            for (int i = 0; i < slots.Count; i++)
            {
                try
                {
                    TreeNodeRender bg = BgField.GetValue(slots[i]) as TreeNodeRender;
                    if (bg != null && bg.image != null && bg.image.sprite != null
                        && string.Equals(bg.image.sprite.name, spriteName, StringComparison.Ordinal))
                    {
                        return slots[i];
                    }
                }
                catch
                {
                    // 取不到就跳过
                }
            }

            return null;
        }

        /// <summary>走私舱：容器 identifier 都是 smuggler_bay（5×5 / 8×8），取最宽的即升级版。</summary>
        private static GameInventory FindSmugglerBay()
        {
            GameInventory best = null;
            int bestWidth = -1;

            foreach (GameItem host in AllOwnedItems())
            {
                if (host == null) continue;

                List<GameInventory> invs;
                try { invs = GraphUtils.FindAllChildrenType<GameInventory>(host); }
                catch { continue; }

                if (invs == null) continue;

                foreach (GameInventory inv in invs)
                {
                    if (inv == null || !string.Equals(inv.identifier, "smuggler_bay", StringComparison.Ordinal)) continue;

                    GameGridInventory grid = inv as GameGridInventory;
                    int width = grid != null ? grid.widthPixels : 0;
                    if (width > bestWidth)
                    {
                        bestWidth = width;
                        best = inv;
                    }
                }
            }

            return best;
        }

        /// <summary>白卡：自己的、不在锁箱里、没被拖拽、不在别的解序器槽里。</summary>
        private static GameItem FindBlank()
        {
            foreach (GameItem item in AllOwnedItems())
            {
                if (item == null || !string.Equals(item.identifier, BlankId, StringComparison.Ordinal)) continue;

                try
                {
                    if (!GeneralHelper.IsItemOwned(item)) continue;
                    if (item.IsTag("ITEM_BEHIND_LOCK_TAG") || item.IsTag("LOCKED_TAG")) continue;

                    GameInventory parent = item.parentInventory;
                    if (parent != null)
                    {
                        GameItem host = parent.GetParentItem();
                        if (host != null && host.IsTag(StateTag)) continue;   // 别的解序器槽里的卡
                    }

                    ItemMouseDragHandler drag = ItemMouseDragHandler.current;
                    if (drag != null && ReferenceEquals(drag.GetCurrentItem(), item)) continue;

                    return item;
                }
                catch
                {
                    // 这一件判断不了就跳过
                }
            }

            return null;
        }

        private static List<GameItem> Machines()
        {
            List<GameItem> list = new List<GameItem>();
            foreach (GameItem item in AllOwnedItems())
            {
                if (item != null && item.IsTag(StateTag)) list.Add(item);
            }

            return list;
        }

        /// <summary>前厅、后仓、柜台、背包四个窗口的物品，0.5 秒共用一份快照。</summary>
        internal static List<GameItem> AllOwnedItems()
        {
            if (_cache != null && Time.unscaledTime - _cacheAt < 0.5f) return _cache;

            List<GameItem> list = new List<GameItem>();
            EmporiumEntry e = EmporiumEntry.Instance;
            if (e != null)
            {
                AddTo(list, e.frontInvWindow);
                AddTo(list, e.backInvWindow);
                AddTo(list, e.backInvinvElementCounter);
                AddTo(list, e.invWindow);
            }

            _cache = list;
            _cacheAt = Time.unscaledTime;
            return list;
        }

        private static void AddTo(List<GameItem> list, GraphNodeStorage window)
        {
            if (window == null) return;

            try
            {
                List<GameItem> found = GraphUtils.FindAllChildrenType<GameItem>(window);
                if (found != null) list.AddRange(found);
            }
            catch
            {
                // 某个窗口还没准备好
            }
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        private static string Str(GameItem item, string tag)
        {
            try { return item.GetTagReadonly(tag).GetString(); }
            catch { return null; }
        }

        private static void WarnOnce(string text)
        {
            if (Warned.Count > 20 || !Warned.Add(text)) return;
            QLog.Warn("解序器：" + text);
        }

        private static void Notice(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(text);
            }
            catch
            {
                // 提示失败不影响功能
            }

            QLog.Info("解序器：" + text);
        }
    }
}
