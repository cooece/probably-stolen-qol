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
        internal const string StateTag = "DESEQUENCER_STATE_TAG";
        private const string StateWorking = "STATE_DESEQUENCING";
        private const string BlankCardId = "blank_keycard";
        private const string InputSprite = "blank_card";
        private const string OutputSprite = "sec_keycard";

        private static readonly FieldInfo F_backgroundImage =
            AccessTools.Field(typeof(GameSlotInventory), "backgroundImage");

        private const float PollInterval = 1.0f;

        private static float _pollTimer;
        private static bool _dumpedSlots;
        private static bool _warnedSlot;
        private static readonly HashSet<string> MoveDiag = new HashSet<string>();

        /// <summary>搬运失败原因，每类只记一条。</summary>
        private static void DiagMove(string reason)
        {
            if (MoveDiag.Count > 20 || !MoveDiag.Add(reason))
            {
                return;
            }

            QLog.Warn("解序器搬运：" + reason);
        }
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
                // 睡觉「开始」那一刻：趁夜里的周期还没跑，先把白卡补上，今晚才能推进
                ModHook.OnGoingSleepEarly += OnSleep;

                // 睡觉「结束」那一刻（EndNight 最后一步，已经是第二天开始）：
                // 夜里的周期跑完了，可能刚解完一张，这时收成品、再补下一张
                ModHook.OnGoingSleepLate += OnSleep;

                QLog.Info("解序器自动续卡已挂载（睡觉开始 + 第二天开始 + 随时轮询）");
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

        // ---------------------------------------------------------------
        // 入口
        // ---------------------------------------------------------------

        /// <summary>
        /// 常驻检查：成品槽一出现解好的卡就收走，不等睡觉。
        /// 机器是「成品槽空才能开始下一张」，所以这一步晚一秒，机器就多闲置一秒。
        /// </summary>
        internal static void Tick(float dt)
        {
            if (!Enabled)
            {
                return;
            }

            _pollTimer += dt;
            if (_pollTimer < PollInterval)
            {
                return;
            }

            _pollTimer = 0f;

            try
            {
                List<GameItem> all = AllOwnedItems();
                if (all.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < all.Count; i++)
                {
                    GameItem item = all[i];
                    if (item == null || !item.IsTag(StateTag))
                    {
                        continue;
                    }

                    try
                    {
                        CollectFinished(item, all);
                    }
                    catch (Exception e)
                    {
                        QLog.Warn("解序器收卡：处理某台机器出错 —— " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                QLog.Warn("解序器收卡：检查失败 —— " + e.Message);
            }
        }

        /// <summary>成品槽有卡就搬进「本来就放着门禁卡」的箱子；没有成品就什么都不做。</summary>
        private static void CollectFinished(GameItem machine, List<GameItem> all)
        {
            List<GameSlotInventory> slots = GraphUtils.FindAllChildrenType<GameSlotInventory>(machine);

            if (!_dumpedSlots)
            {
                _dumpedSlots = true;
                DumpSlots(slots);
            }

            GameSlotInventory output = FindSlot(slots, OutputSprite);

            if (output == null || output.childItem == null)
            {
                return;
            }

            GameItem finished = output.childItem;
            if (!MoveToAnyCardContainer(finished, all, machine))
            {
                if (!_warnedFlow)
                {
                    _warnedFlow = true;
                    QLog.Warn("解序器收卡：成品卡搬不出去（目标容器不存在或放不进），先把卡留在输出槽");
                }

                return;
            }

            QLog.Info("解序器收卡：成品「" + Name(finished) + "」已收进箱子");
        }

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
                if (!MoveToAnyCardContainer(finished, all, machine))
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

            if (!MoveCard(input, blank))
            {
                QLog.Warn("解序器续卡：白卡放不进输入槽（可能被锁或槽位不认）");
                return;
            }

            if (ReferenceEquals(input.childItem, blank))
            {
                QLog.Info("解序器续卡：已补上一张白卡");
            }
            else
            {
                QLog.Warn("解序器续卡：白卡移动后输入槽里不是它，请检查");
            }
        }

        // ---------------------------------------------------------------
        // 槽位识别
        // ---------------------------------------------------------------

        /// <summary>排查用：把解序器各槽的子槽背景贴图名打一条日志，确认识别条件写对了。</summary>
        private static void DumpSlots(List<GameSlotInventory> slots)
        {
            try
            {
                if (slots == null)
                {
                    QLog.Warn("解序器槽位诊断：一个槽都没找到");
                    return;
                }

                string text = "解序器槽位诊断（共 " + slots.Count + " 个）：";
                for (int i = 0; i < slots.Count; i++)
                {
                    string name = "?";
                    try
                    {
                        TreeNodeRender bg = F_backgroundImage != null
                            ? F_backgroundImage.GetValue(slots[i]) as TreeNodeRender
                            : null;
                        if (bg != null && bg.image != null && bg.image.sprite != null)
                        {
                            name = bg.image.sprite.name;
                        }
                        else
                        {
                            name = "(无背景)";
                        }
                    }
                    catch
                    {
                        name = "(读取失败)";
                    }

                    text += " [" + i + "]=" + name;
                }

                QLog.Info(text);
            }
            catch (Exception e)
            {
                QLog.Warn("解序器槽位诊断失败：" + e.Message);
            }
        }

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

        /// <summary>
        /// 成品卡去处：先试「装着同一种卡」的容器（可能有多个，第一个满了就换下一个），
        /// 再试「任何装着门禁卡」的容器，最后退后仓、背包。每个都真的验证过才继续。
        /// </summary>
        private static bool MoveToAnyCardContainer(GameItem card, List<GameItem> all, GameItem machine)
        {
            EmporiumEntry entry = EmporiumEntry.Instance;

            // 门禁卡是违禁品：第一顺位放走私暗格（玩家的违禁品本来就都存在那儿）
            if (entry != null && MoveCard(entry.hiddenElement, card))
            {
                return true;
            }

            // 暗格放不下（满了）：退后仓，再退背包
            if (entry != null)
            {
                if (MoveCard(entry.backInvinvElement, card)) return true;
                if (MoveCard(entry.invElement, card)) return true;
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
        internal static List<GameItem> AllOwnedItems()
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

        /// <summary>
        /// 真正把物品搬到另一个容器：先从容器的原处移出，再放进目标，最后**用父容器验证**。
        /// 只靠 TryAcceptOnce 的返回值会骗人（实测返回 1 但物品没动，日志一秒一条刷屏）。
        /// 放不进去就退回原容器，绝不让物品消失。
        /// </summary>
        private static bool MoveCard(GameInventory target, GameItem item)
        {
            if (target == null || item == null)
            {
                return false;
            }

            GameInventory source = item.parentInventory;
            if (source == null || ReferenceEquals(source, target))
            {
                return false;
            }

            SlotMarker marker;
            try
            {
                if (target.IsInsertLocked())
                {
                    DiagMove("目标容器禁止插入：" + target.GetType().Name);
                    return false;
                }

                marker = target.TryFindOneValidInventorySlot(item);
                if (marker == null)
                {
                    DiagMove("目标容器找不到能放它的格子（可能是满了）：" + target.GetType().Name);
                    return false;
                }

                if (marker.numTransfer < 1)
                {
                    DiagMove("目标格子 numTransfer=" + marker.numTransfer);
                    return false;
                }
            }
            catch (Exception e)
            {
                DiagMove("查格子时抛异常：" + e.Message);
                return false;
            }

            try
            {
                if (!source.Expel(item))
                {
                    DiagMove("从原容器移出失败（Expel=false），原容器=" + source.GetType().Name);
                    return false;
                }
            }
            catch (Exception e)
            {
                DiagMove("移出时抛异常：" + e.Message);
                return false;
            }

            try
            {
                marker.TryAcceptOnce(1);
            }
            catch
            {
                // 下面用父容器验证结果
            }

            if (ReferenceEquals(item.parentInventory, target))
            {
                return true;
            }

            DiagMove("放进目标后父容器仍不是目标（实际=" +
                (item.parentInventory == null ? "null" : item.parentInventory.GetType().Name) + "）");

            // 没放进去：退回原容器，别把卡弄丢
            TryMoveInto(source, item);
            return false;
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
