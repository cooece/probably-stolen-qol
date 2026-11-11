using System;
using System.Reflection;
using HarmonyLib;

namespace QoL
{
    /// <summary>
    /// 反向取液：把容器拖到「有液体的容器」上，从对方抽液体进自己。
    ///
    /// 原版本来就允许「液体容器拖到液体容器」——`WaterHelper.InitLiquidContainerItem` 挂的
    /// mayThisTargetItemFunc 只要求目标也是液体容器，没要求拖动方非空；空瓶拖上去是合法的，
    /// 只是 onThisTargetItemFunc 里 TransferLiquid(空的, 目标) 什么也不搬，所以看起来没反应。
    ///
    /// 因此这里**不碰任何判定委托**。曾经给容器追加过 mayItemTargetThisFunc，结果很糟：
    /// 游戏把八组判定做 AND 合并（`flag = flag & obj2 & flag3`），我们那组在「拖动方是满的」
    /// 时返回 false，直接把整个拖放否掉了，原版倒水跟着一起失效。
    ///
    /// 现在的做法只有一件事：在 GameItem.Target 前后比较双方的液体总量。
    /// 原版搬过水就交给它；两边一滴都没动，才说明它搬不动（容器空着 / 目标装满了），
    /// 这时从目标抽给拖动方。
    /// </summary>
    internal static class ReversePour
    {
        private const string HarmonyId = "local.qol.reverse_pour";

        private static Harmony _harmony;

        private static GameItem _dragSource;
        private static GameItem _dragTarget;
        private static int _sourceVolumeBefore;
        private static int _targetVolumeBefore;

        /// <summary>上一次处理过的拖放，用来挡「一次拖放触发多次 Target」。</summary>
        private static GameItem _pairSource;
        private static GameItem _pairTarget;
        private static float _pairTime = -99f;

        private static bool _warned;
        internal static void Apply()
        {
            if (_harmony != null) return;

            try
            {
                MethodInfo target = AccessTools.Method(typeof(GameItem), "Target",
                    new[] { typeof(GameItem) });

                if (target == null)
                {
                    QLog.Warn("反向取液：找不到 GameItem.Target，功能未启用");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(ReversePour), nameof(BeforeTarget)),
                    postfix: new HarmonyMethod(typeof(ReversePour), nameof(AfterTarget)));

                QLog.Info("反向取液已挂载（把容器拖到有液体的容器上，从对方取液）");
            }
            catch (Exception e)
            {
                QLog.Error("反向取液挂载失败", e);
            }
        }

        internal static void Remove()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // 卸载失败不影响游戏
            }

            _harmony = null;
            _dragSource = null;
            _dragTarget = null;
        }

        /// <summary>拖放即将执行时（原版倒水之前）记下双方的液体总量。只读不改。</summary>
        private static void BeforeTarget(GameItem __instance, GameItem __0)
        {
            try
            {
                _dragSource = __instance;
                _dragTarget = __0;
                _sourceVolumeBefore = Volume(__instance);
                _targetVolumeBefore = Volume(__0);
            }
            catch
            {
                _dragSource = null;
                _dragTarget = null;
            }
        }

        /// <summary>
        /// 拖放执行完：原版那一遍也跑完了。只有「两边一滴都没动」才说明原版搬不动，
        /// 这时才从目标抽给拖动方。
        /// </summary>
        private static void AfterTarget()
        {
            GameItem source = _dragSource;
            GameItem target = _dragTarget;
            _dragSource = null;
            _dragTarget = null;

            if (source == null || target == null) return;

            int sourceNow = Volume(source);
            int targetNow = Volume(target);

            // 同一次拖放可能触发多次 Target：只认第一次，避免原版倒完我们又把水抽回来
            bool samePair = ReferenceEquals(source, _pairSource) && ReferenceEquals(target, _pairTarget)
                && UnityEngine.Time.unscaledTime - _pairTime < 0.35f;

            _pairSource = source;
            _pairTarget = target;
            _pairTime = UnityEngine.Time.unscaledTime;

            if (samePair) return;

            // 任何一边的液体量变了，说明原版这次真搬了水，交给它
            if (sourceNow != _sourceVolumeBefore) return;
            if (targetNow != _targetVolumeBefore) return;

            Draw(source, target);
        }

        /// <summary>
        /// 拖来的容器能不能从我这里抽液体。source = 被拖动的容器，self = 被拖到的容器。
        /// </summary>
        private static bool CanDraw(GameItem source, GameItem self)
        {
            try
            {
                if (source == null || self == null) return false;
                if (ReferenceEquals(source, self)) return false;
                if (!source.IsTag("LIQUID_CONTAINER_TAG")) return false;
                if (!self.IsTag("LIQUID_CONTAINER_TAG")) return false;

                // 滴管/注射器原版已经支持「空的自目标吸」，再抽一次会重复搬
                if (source.IsTag("LIQUID_CONTAINER_DROPPER_TAG")) return false;

                if (!GeneralHelper.IsItemOwned(source) || !GeneralHelper.IsItemOwned(self)) return false;

                if (WaterHelper.IsEmpty(self)) return false;  // 我这没液体，抽不出来
                if (WaterHelper.IsFull(source)) return false; // 拖来的已经装满，没空间接收

                // 发酵中/已完成的酒桶不抽：原版对这类容器本身就是禁抽的
                if (!WineHelper.CanModifyLiquid(self)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Draw(GameItem source, GameItem self)
        {
            try
            {
                // 拖放期间状态可能变了，执行前重验
                if (!CanDraw(source, self)) return;

                int before = Volume(source);
                WaterHelper.TransferLiquid(self, source);
                if (Volume(source) <= before) return;

                AudioHelper.PourWater();
                WaterHelper.UpdateWaterBottleSound(source);
                WaterHelper.UpdateWaterBottleSound(self);
            }
            catch (Exception e)
            {
                Warn("反向取液出错", e);
            }
        }

        /// <summary>容器里的液体总量；非液体容器是 0。</summary>
        private static int Volume(GameItem item)
        {
            try { return WaterHelper.GetTotalVolume(item); }
            catch { return 0; }
        }

        private static void Warn(string what, Exception e)
        {
            if (_warned) return;
            _warned = true;
            QLog.Warn("反向取液：" + what + "（只提示一次）：" + e.Message);
        }
    }
}
