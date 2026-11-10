using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 解序器自动续卡：点睡觉之后（夜间结算开始那一刻）检查密码解序器——
    /// 成品卡收进「本来就放着门禁卡」的箱子，输入槽空了就补一张白卡。
    ///
    /// 机器规则（反编译 MachineDesequencer 确认）：
    ///   输入槽只收 ACCESS_CARD；芯片槽决定产出；电力槽按次耗电；
    ///   **开始条件包含「成品槽必须为空」**，所以成品不收走，机器就再也开不了第二张。
    ///   输入卡在开始后会被 LockItem 锁住、完成时 Destroy —— 所以运行中一律不碰。
    ///
    /// 槽位靠背景贴图名识别（输入槽 blank_card、成品槽 sec_keycard），不猜顺序。
    /// </summary>
    internal static class DesequencerAutoFeed
    {
        private const string StateTag = "DESEQUENCER_STATE_TAG";
        private const string StateWorking = "STATE_DESEQUENCING";
        private const string BlankCardId = "blank_keycard";
        private const string InputSprite = "blank_card";
        private const string OutputSprite = "sec_keycard";

        private static readonly FieldInfo F_backgroundImage =
            AccessTools.Field(typeof(GameSlotInventory), "backgroundImage");

        private static bool _warnedSlot;
        private static bool _warnedFlow;

        internal static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.DesequencerAuto.Value;
            }
        }

        internal static void Apply()
        {
            try
            {
                ModHook.OnGoingSleepEarly += OnSleep;
                QLog.Info("解序器自动续卡已挂载（点睡觉时检查）");
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
            }
            catch
            {
                // 卸载失败不影响游戏
            }
        }

        // ---------------------------------------------------------------
        // 入口
        // ---------------------------------------------------------------

        private static void OnSleep()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                List<GameItem> all = AllOwnedItems();
                if (all.Count == 0)
                {
                    return;
                }

                // 先把所有解序器的槽位收集起来：白卡不能从别的解序器槽里拿
                List<GameInventory> reserved = CollectReservedSlots(all);

                int machines = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    GameItem item = all[i];
                    if (item == null || !item.IsTag(StateTag))
                    {
                        continue;
                    }

                    machines++;
                    try
                    {
                        ProcessMachine(item, all, reserved);
                    }
                    catch (Exception e)
                    {
                        QLog.Warn("解序器续卡：处理某台机器出错 —— " + e.Message);
                    }
                }

                if (machines == 0)
                {
                    QLog.Info("解序器续卡：店里没找到解序器");
                }
            }
            catch (Exception e)
            {
                QLog.Warn("解序器续卡：检查失败 —— " + e.Message);
            }
        }

        private static void ProcessMachine(GameItem machine, List<GameItem> all, List<GameInventory> reserved)
        {
            string state;
            try
            {
                state = machine.GetTagReadonly(StateTag).GetString();
            }
            catch
            {
                return;
            }

            if (string.Equals(state, StateWorking, StringComparison.Ordinal))
            {
                // 正在解密：输入卡被锁、进度在走，一根手指都别动
                return;
            }

            List<GameSlotInventory> slots = GraphUtils.FindAllChildrenType<GameSlotInventory>(machine);
            GameSlotInventory input = FindSlot(slots, InputSprite);
            GameSlotInventory output = FindSlot(slots, OutputSprite);

            if (input == null || output == null)
            {
                if (!_warnedSlot)
                {
                    _warnedSlot = true;
                    QLog.Warn("解序器续卡：认不出输入/成品槽（背景贴图没对上），这台跳过");
                }

                return;
            }

            // 1) 先把成品收走，否则机器开不了下一张
            if (output.childItem != null)
            {
                GameItem finished = output.childItem;
                if (!MoveToCardContainer(finished, all, machine))
                {
                    if (!_warnedFlow)
                    {
                        _warnedFlow = true;
                        QLog.Warn("解序器续卡：成品卡找不到可放的容器（没有已放门禁卡的箱子？），这次没有补卡");
                    }

                    return;
                }

                QLog.Info("解序器续卡：成品「" + Name(finished) + "」已收进箱子");
            }

            // 2) 输入槽有卡就不动
            if (input.childItem != null)
            {
                return;
            }

            // 3) 找白卡补上
            GameItem blank = FindBlankCard(all, machine, input, reserved);
            if (blank == null)
            {
                Notice("解序器没白卡了");
                return;
            }

            if (!TryMoveInto(input, blank))
            {
                QLog.Warn("解序器续卡：白卡放不进输入槽（可能被锁或槽位不认）");
                return;
            }

            if (input.childItem == null)
            {
                QLog.Warn("解序器续卡：白卡已移动但输入槽仍为空，请检查");
            }
            else
            {
                QLog.Info("解序器续卡：已补上一张白卡");
            }
        }

        // ---------------------------------------------------------------
        // 槽位识别
        // ---------------------------------------------------------------

        /// <summary>所有解序器的子槽（输入/成品/芯片/电力），白卡来源要避开它们。</summary>
        private static List<GameInventory> CollectReservedSlots(List<GameItem> all)
        {
            List<GameInventory> reserved = new List<GameInventory>();

            for (int i = 0; i < all.Count; i++)
            {
                GameItem item = all[i];
                if (item == null || !item.IsTag(StateTag))
                {
                    continue;
                }

                List<GameSlotInventory> slots;
                try
                {
                    slots = GraphUtils.FindAllChildrenType<GameSlotInventory>(item);
                }
                catch
                {
                    continue;
                }

                if (slots == null)
                {
                    continue;
                }

                for (int j = 0; j < slots.Count; j++)
                {
                    if (slots[j] != null)
                    {
                        reserved.Add(slots[j]);
                    }
                }
            }

            return reserved;
        }

        /// <summary>靠槽位背景贴图名认出输入槽 / 成品槽，比猜槽位顺序可靠。</summary>
        private static GameSlotInventory FindSlot(List<GameSlotInventory> slots, string spriteName)
        {
            if (slots == null || F_backgroundImage == null)
            {
                return null;
            }

            for (int i = 0; i < slots.Count; i++)
            {
                GameSlotInventory slot = slots[i];
                if (slot == null)
                {
                    continue;
                }

                try
                {
                    TreeNodeRender background = F_backgroundImage.GetValue(slot) as TreeNodeRender;
                    if (background == null || background.image == null || background.image.sprite == null)
                    {
                        continue;
                    }

                    if (string.Equals(background.image.sprite.name, spriteName, StringComparison.Ordinal))
                    {
                        return slot;
                    }
                }
                catch
                {
                    // 取不到就当没这个槽
                }
            }

            return null;
        }

        // ---------------------------------------------------------------
        // 成品卡：放进本来就装着门禁卡的容器
        // ---------------------------------------------------------------

        private static bool MoveToCardContainer(GameItem card, List<GameItem> all, GameItem machine)
        {
            // 先找装着「同一种卡」的容器，再退到任何装着门禁卡的容器
            GameInventory target = FindCardContainer(all, machine, card.identifier);
            if (target == null)
            {
                target = FindCardContainer(all, machine, null);
            }

            if (target != null && TryMoveInto(target, card))
            {
                return true;
            }

            // 没有已放门禁卡的箱子时，退到后仓、再退背包——总比让机器卡死强
            EmporiumEntry entry = EmporiumEntry.Instance;
            if (entry != null)
            {
                if (TryMoveInto(entry.backInvinvElement, card)) return true;
                if (TryMoveInto(entry.invElement, card)) return true;
            }

            return false;
        }

        private static GameInventory FindCardContainer(List<GameItem> all, GameItem machine, string sameId)
        {
            for (int i = 0; i < all.Count; i++)
            {
                GameItem host = all[i];
                if (host == null || host == machine || host.IsTag(StateTag))
                {
                    continue;   // 不往别的解序器槽里塞
                }

                List<GameInventory> invs = GraphUtils.FindAllChildrenType<GameInventory>(host);
                if (invs == null)
                {
                    continue;
                }

                for (int j = 0; j < invs.Count; j++)
                {
                    GameInventory inv = invs[j];
                    if (inv == null || inv.IsInsertLocked())
                    {
                        continue;
                    }

                    if (ContainsCard(inv, sameId))
                    {
                        return inv;
                    }
                }
            }

            return null;
        }

        private static bool ContainsCard(GameInventory inv, string sameId)
        {
            List<GameItem> inside = inv.childItems;
            if (inside == null)
            {
                return false;
            }

            for (int i = 0; i < inside.Count; i++)
            {
                GameItem item = inside[i];
                if (item == null || !item.IsGameItemType("ACCESS_CARD"))
                {
                    continue;
                }

                if (sameId == null || string.Equals(item.identifier, sameId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // ---------------------------------------------------------------
        // 白卡来源
        // ---------------------------------------------------------------

        private static GameItem FindBlankCard(List<GameItem> all, GameItem machine, GameSlotInventory input,
            List<GameInventory> reserved)
        {
            for (int i = 0; i < all.Count; i++)
            {
                GameItem item = all[i];
                if (item == null || !string.Equals(item.identifier, BlankCardId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!Usable(item, machine, input, reserved))
                {
                    continue;
                }

                return item;
            }

            return null;
        }

        /// <summary>
        /// 白卡能不能拿：要属于玩家、不在锁着的箱子里、没被拖拽、不在解序器自己的槽里。
        /// 暗格里的白卡**允许**（这是用户明确要求的，和换滤芯那边相反）。
        /// </summary>
        private static bool Usable(GameItem card, GameItem machine, GameSlotInventory input,
            List<GameInventory> reserved)
        {
            try
            {
                if (!GeneralHelper.IsItemOwned(card))
                {
                    return false;
                }

                if (card.IsTag("ITEM_BEHIND_LOCK_TAG") || card.IsTag("LOCKED_TAG"))
                {
                    return false;
                }

                if (input != null && card.parentInventory == input)
                {
                    return false;
                }

                if (reserved != null && card.parentInventory != null && reserved.Contains(card.parentInventory))
                {
                    return false;      // 别的解序器槽里的卡，别拿
                }

                ItemMouseDragHandler drag = ItemMouseDragHandler.current;
                if (drag != null && ReferenceEquals(drag.GetCurrentItem(), card))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------

        /// <summary>和游戏自己的 FindAllItem 一致：前厅、后仓、柜台、背包四个窗口全扫。</summary>
        private static List<GameItem> AllOwnedItems()
        {
            List<GameItem> list = new List<GameItem>();

            EmporiumEntry entry = EmporiumEntry.Instance;
            if (entry == null)
            {
                return list;
            }

            Add(list, entry.frontInvWindow);
            Add(list, entry.backInvWindow);
            Add(list, entry.backInvinvElementCounter);
            Add(list, entry.invWindow);
            return list;
        }

        private static void Add(List<GameItem> list, GraphNodeStorage window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                List<GameItem> found = GraphUtils.FindAllChildrenType<GameItem>(window);
                if (found != null)
                {
                    list.AddRange(found);
                }
            }
            catch
            {
                // 某个窗口还没准备好就跳过
            }
        }

        private static bool TryMoveInto(GameInventory target, GameItem item)
        {
            if (target == null || item == null)
            {
                return false;
            }

            try
            {
                if (target.IsInsertLocked())
                {
                    return false;
                }

                SlotMarker marker = target.TryFindOneValidInventorySlot(item);
                if (marker == null || marker.numTransfer < 1)
                {
                    return false;
                }

                return marker.TryAcceptOnce(1) == 1;
            }
            catch
            {
                return false;
            }
        }

        private static string Name(GameItem item)
        {
            try
            {
                return item.name;
            }
            catch
            {
                return "?";
            }
        }

        private static void Notice(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null)
                {
                    ui.Notify(text);
                }
            }
            catch
            {
                // 提示失败不影响功能
            }

            QLog.Info("解序器续卡：" + text);
        }
    }
}
