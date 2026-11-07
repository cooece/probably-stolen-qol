using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 前台杂物提醒：按铃叫下一位客人时，如果外柜台上堆着玩家自己的东西、
    /// 而且剩下的空格已经不够摆下一批货，就弹气泡提醒清理。
    ///
    /// 为什么要提醒：外柜台被杂物占满时，客人带来的货会摆不下 ——
    /// 原版会把它们直接丢掉（我们虽然做了暂存与补位，但补位要等空位，
    /// 客人可能等不及就走了），提前清一清才不折腾。
    ///
    /// 只统计「玩家自己的东西」：客人摆在柜台上的货是未拥有的，不算杂物。
    /// </summary>
    internal static class CounterClutterWarn
    {
        private const string HarmonyId = "local.qol.counter_clutter";

        /// <summary>剩余空格少到这个数就算「放不下下一批货」。</summary>
        private const int LowSpaceCells = 8;

        /// <summary>
        /// 重弹间隔。游戏气泡本身 3 秒收起（个别设置 6 秒），
        /// 这里 2.5 秒补一次，看上去就是一直挂着，直到条件解除。
        /// </summary>
        private const float RecallInterval = 2.5f;

        private static Harmony _harmony;

        private static bool _reminding;
        private static float _nextRecall;
        private static float _nextCheck;

        internal static void Apply()
        {
            if (_harmony != null) return;

            try
            {
                MethodInfo call = AccessTools.Method(typeof(PlayerStore), "TryCallNextClient", Type.EmptyTypes);
                if (call == null)
                {
                    QLog.Warn("[前台提醒] 找不到 PlayerStore.TryCallNextClient，功能未启用");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(call, postfix: new HarmonyMethod(typeof(CounterClutterWarn), nameof(CallPostfix)));
                QLog.Info("[前台提醒] 已挂载：按铃叫到人时检查前台是否被杂物占满");
            }
            catch (Exception e)
            {
                QLog.Warn("[前台提醒] 挂载失败：" + e);
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
            _reminding = false;
            _nextRecall = 0f;
        }

        private static void CallPostfix()
        {
            try
            {
                // 只有真的叫到人才提示（铃按了但队列是空的，不必提醒）
                PlayerStore player = PlayerStore.Instance;
                if (player == null || player.currentClientInstance == null) return;

                // 只有「带东西来卖」的客人才会占柜台；纯买货、检查、对话这些不占
                if (!IsSeller(player.currentClientInstance)) return;

                int clutter;
                int free;
                if (!ConditionMet(out clutter, out free)) return;

                _reminding = true;
                _nextRecall = Time.unscaledTime + RecallInterval;
                Notify("前台有杂物，请及时清理");
                QLog.Info("[前台提醒] 前台有 " + clutter + " 件自己的东西，只剩 " + free + " 格，开始持续提醒");
            }
            catch (Exception e)
            {
                QLog.Warn("[前台提醒] 检查失败：" + e.Message);
            }
        }

        /// <summary>
        /// 提醒期间每 2.5 秒重弹一次，直到柜台腾出足够空间。
        /// 顾客走了也停：这句提醒只在招呼客人时有意义。
        /// </summary>
        internal static void Tick(float dt)
        {
            if (!_reminding) return;

            // 空间检查不必每帧跑：柜台格子 × 物品数量的双重遍历，0.5 秒一次足够
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;

            try
            {
                PlayerStore player = PlayerStore.Instance;
                if (player == null || player.currentClientInstance == null)
                {
                    _reminding = false;
                    return;
                }

                int clutter;
                int free;
                if (!ConditionMet(out clutter, out free))
                {
                    _reminding = false;
                    QLog.Info("[前台提醒] 柜台腾出来了（剩 " + free + " 格），停止提醒");
                    return;
                }

                if (Time.unscaledTime < _nextRecall) return;

                _nextRecall = Time.unscaledTime + RecallInterval;
                Notify("前台有杂物，请及时清理");
            }
            catch
            {
                _reminding = false;
            }
        }

        /// <summary>前台有玩家自己的东西，且剩余空格不够摆下一批货。</summary>
        private static bool ConditionMet(out int clutter, out int free)
        {
            clutter = 0;
            free = 0;

            EmporiumEntry store = EmporiumEntry.Instance;
            if (store == null || store.frontInvinvElement == null) return false;

            GameGridInventory counter = store.frontInvinvElement;

            clutter = CountOwned(counter);
            if (clutter <= 0) return false;          // 前台没自己的东西，不用提醒

            free = CountFreeCells(counter);
            return free < LowSpaceCells;             // 还摆得下就不打扰
        }

        /// <summary>
        /// 这位客人会不会往柜台上摆东西。
        /// 他的货是到店后才逐件生成的，按铃这一刻拿不到件数，
        /// 所以只能按意图判断：带东西来卖的才占柜台。
        /// </summary>
        private static bool IsSeller(StoreClientInstance client)
        {
            try
            {
                StoreClient blueprint = client.GetClientBlueprint();
                if (blueprint == null) return true;

                switch (blueprint.clientIntent)
                {
                    case StoreClient.ClientIntent.SELL:
                    case StoreClient.ClientIntent.SELLNBUY:
                    case StoreClient.ClientIntent.BARTER:
                    case StoreClient.ClientIntent.PROCUREMENT_OFFER:
                    case StoreClient.ClientIntent.GUNSMITH:
                    case StoreClient.ClientIntent.EXPEDITION:
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return true; // 判断不了就按原来的方式走，宁可多提醒一次
            }
        }

        /// <summary>前台属于玩家的物品数量（客人待售的货是未拥有的，不算）。</summary>
        private static int CountOwned(GameGridInventory inventory)
        {
            int count = 0;

            try
            {
                List<GameItem> items = inventory.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem item = items[i];
                    if (item == null) continue;

                    if (GeneralHelper.IsItemOwned(item)) count++;
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[前台提醒] 统计杂物失败：" + e.Message);
            }

            return count;
        }

        /// <summary>数一下前台还有多少空格子（形状里有效、且没被任何物品压住）。</summary>
        private static int CountFreeCells(GameGridInventory inventory)
        {
            int free = 0;

            try
            {
                GridShape shape = inventory.inventoryShape;
                if (shape == null) return 0;

                List<GameItem> items = inventory.childItems;

                for (int y = 0; y < shape.height; y++)
                {
                    for (int x = 0; x < shape.width; x++)
                    {
                        if (shape.Get(x, y) == 0) continue;

                        bool used = false;
                        for (int i = 0; i < items.Count; i++)
                        {
                            GameItem item = items[i];
                            if (item != null && item.modifiedShape.Intersects(x, y))
                            {
                                used = true;
                                break;
                            }
                        }

                        if (!used) free++;
                    }
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[前台提醒] 统计空格失败：" + e.Message);
            }

            return free;
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
                // 气泡只是提示，失败不影响游戏
            }
        }
    }
}
