using System;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 打烊确认：拉闸门之前先问一句「确定要关店打烊吗？」。
    ///
    /// 面板用游戏自带的 <see cref="GenericConfirmationUIManager"/>，
    /// 和「未击杀改造体，是否跳过？」是同一块——本模组里所有确认框观感统一。
    ///
    /// 原版 <c>CloseShutter</c> 的两条分支都被这里接管：
    ///   队列里还有人       → 原版弹它自己那块打烊面板（就是那块「队伍里还有人在排队等候。」的旧面板）
    ///   队列空、柜台有人   → 原版直接关店，顾客被晾在那儿
    ///
    /// 现在统一走本模组的确认框，正文固定一句：
    ///   「确定要关店打烊吗？／关店后，排队中和柜台上的顾客都会被请走。」
    ///
    /// 「是」→ <c>EndAndClose</c>（原版关店流程：结算、请走顾客、收闸门）
    /// 「否」→ 什么都不做，继续营业
    ///
    /// 拦截的是 <c>CloseShutter</c> 而不是按钮的 <c>OnClick</c>，
    /// 这样"能不能关店"的前置判断（锁、逮捕、枪战、CanEndDay）全部沿用原版。
    /// </summary>
    internal static class ShutterConfirm
    {
        private const string HarmonyId = "local.qol.shutter_confirm";

        /// <summary>
        /// 弹窗正文。两种情况（队列有人 / 只有柜台有人）共用一句，
        /// 因为「会被请走」在两种情况下都成立，不会像原版那样在队列空时说错话。
        /// </summary>
        private const string BodyText = "确定要关店打烊吗？\n\n关店后，排队中和柜台上的顾客都会被请走。";

        private static Harmony _harmony;

        /// <summary>确认框开着的时候别再弹一层。</summary>
        private static bool _dialogOpen;

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                var closeShutter = AccessTools.Method(typeof(StoreShutterButton), "CloseShutter", Type.EmptyTypes);
                if (closeShutter == null)
                {
                    QLog.Warn("找不到 StoreShutterButton.CloseShutter，打烊确认不会生效");
                    _harmony = null;
                    return;
                }

                _harmony.Patch(closeShutter,
                    prefix: new HarmonyMethod(typeof(ShutterConfirm), nameof(CloseShutterPrefix)));

                QLog.Info("打烊确认已挂载");
            }
            catch (Exception e)
            {
                QLog.Error("挂载打烊确认失败：" + e);
                _harmony = null;
            }
        }

        internal static void Remove()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll(HarmonyId);
                }
            }
            catch
            {
                // 卸载阶段出错无所谓
            }

            _harmony = null;
            _dialogOpen = false;
        }

        /// <summary>返回 false = 不执行原版 CloseShutter（这次不关店，只弹确认）。</summary>
        private static bool CloseShutterPrefix(StoreShutterButton __instance)
        {
            try
            {
                if (ModEntry.Settings == null || !ModEntry.Settings.ConfirmCloseBusy.Value)
                {
                    return true;
                }

                PlayerStore store = PlayerStore.Instance;
                if (store == null || __instance == null)
                {
                    return true;
                }

                GenericConfirmationUIManager panel = GenericConfirmationUIManager.Instance;

                // 上一块框还开着：这次点击当作「已经在确认了」，不叠加、也不放行
                if (_dialogOpen)
                {
                    // 面板被右上角 X 关掉了 → 复位状态，允许重新判断
                    if (!IsPanelOpen(panel))
                    {
                        _dialogOpen = false;
                    }
                    else
                    {
                        return false;
                    }
                }

                int queued = 0;
                try
                {
                    if (store.storeClientManager != null)
                    {
                        queued = store.storeClientManager.clientStack.Count;
                    }
                }
                catch
                {
                    return true;
                }

                // 柜台这位是不是还没谈完（买够了 / 没东西可卖了，就算谈完）
                bool counterBusy = false;
                try
                {
                    StoreClientInstance client = store.currentClientInstance;
                    if (client != null && !StoreClientInstance.IsClientBusinessDone(client))
                    {
                        counterBusy = true;
                    }
                }
                catch
                {
                    counterBusy = false;
                }

                // 队列空、柜台也没人谈着：没什么好问的，交给原版直接关
                if (queued == 0 && !counterBusy)
                {
                    return true;
                }

                ShowDialog(__instance, panel);
                return false;
            }
            catch (Exception e)
            {
                QLog.Warn("打烊确认判断出错，按原版关店：" + e.Message);
                return true;
            }
        }

        // ---------------------------------------------------------------
        // 弹窗
        // ---------------------------------------------------------------

        private static void ShowDialog(StoreShutterButton button, GenericConfirmationUIManager panel)
        {
            if (panel == null)
            {
                // 这个场景没有通用确认框，退回原版那块打烊面板，至少不让玩家点不动
                QLog.Warn("当前场景没有 GenericConfirmationUIManager，改用原版打烊面板");
                button.ShowConfirmCloseUI();
                return;
            }

            try
            {
                _dialogOpen = true;

                panel.OpenPanel(
                    BodyText,
                    ButtonText("yes", "是"),
                    ButtonText("no", "否"),
                    delegate
                    {
                        _dialogOpen = false;
                        panel.ClosePanel();

                        // 二次校验：确认框开着的时候，场景/店铺状态/按钮都可能已经变了。
                        // 校验不过就什么都不做，绝不替玩家关店。
                        if (button == null) return;

                        PlayerStore store = PlayerStore.Instance;
                        if (store == null) return;
                        if (store.storeState != PlayerStore.StoreState.OPEN)
                        {
                            QLog.Info("打烊确认：店铺已不在营业状态，忽略");
                            return;
                        }

                        // 原版面板上「是」走的就是这个方法：关店 + 请走顾客 + 结算当天
                        button.EndAndClose();
                        QLog.Info("打烊确认：是，关闭店铺");
                    },
                    delegate
                    {
                        _dialogOpen = false;
                        panel.ClosePanel();
                        QLog.Info("打烊确认：否，继续营业");
                    });

                QLog.Info("弹出打烊确认");
            }
            catch (Exception e)
            {
                _dialogOpen = false;
                QLog.Warn("弹打烊确认失败，退回原版确认面板：" + e.Message);

                // 不能在这里直接关店：玩家根本没点过「是」。
                // 退回原版面板，让玩家自己确认。
                try
                {
                    if (button != null) button.ShowConfirmCloseUI();
                }
                catch (Exception inner)
                {
                    QLog.Warn("退回原版打烊面板也失败：" + inner.Message);
                }
            }
        }

        /// <summary>
        /// 按钮文字走游戏的本地化词条（和原版确认框一样是「是 / 否」），
        /// 取词失败就退回写的中文，保证按钮不会空着。
        /// </summary>
        private static string ButtonText(string key, string fallback)
        {
            try
            {
                string text = LocHelper.GetLocalizedMainMenu(key);
                return string.IsNullOrEmpty(text) ? fallback : text;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool IsPanelOpen(GenericConfirmationUIManager panel)
        {
            try
            {
                return panel != null && panel.panel != null && panel.panel.activeSelf;
            }
            catch
            {
                return false;
            }
        }
    }
}
