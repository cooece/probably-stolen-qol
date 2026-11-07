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

        /// <summary>提醒冷却，避免连续按铃刷屏。</summary>
        private const float Cooldown = 20f;

        private static Harmony _harmony;
        private static float _nextWarn;

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
            _nextWarn = 0f;
        }

        private static void CallPostfix()
        {
            try
            {
                // 只有真的叫到人才提示（铃按了但队列是空的，不必提醒）
                PlayerStore player = PlayerStore.Instance;
                if (player == null || player.currentClientInstance == null) return;

                if (Time.unscaledTime < _nextWarn) return;

                EmporiumEntry store = EmporiumEntry.Instance;
                if (store == null || store.frontInvinvElement == null) return;

                GameGridInventory counter = store.frontInvinvElement;

                int clutter = CountOwned(counter);
                if (clutter <= 0) return;                 // 前台没自己的东西，不用提醒

                int free = CountFreeCells(counter);
                if (free >= LowSpaceCells) return;        // 还摆得下，不打扰

                _nextWarn = Time.unscaledTime + Cooldown;
                Notify("前台有杂物，请及时清理");
                QLog.Info("[前台提醒] 前台有 " + clutter + " 件自己的东西，只剩 " + free + " 格，已提示清理");
            }
            catch (Exception e)
            {
                QLog.Warn("[前台提醒] 检查失败：" + e.Message);
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
