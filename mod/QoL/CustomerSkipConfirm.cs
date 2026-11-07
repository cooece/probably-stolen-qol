using System;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 改造体还没处理掉就想摇铃叫下一位时，先弹一个确认框。
    ///
    /// 面板用游戏自带的 <see cref="GenericConfirmationUIManager"/>：
    /// 和 DangpuPlus 那个「用指挥卡开箱？」确认框是同一个 API、同一套外观
    /// （半透明底 + 边框 + 正文 + 是/否按钮），不需要自己画窗口。
    ///
    /// 注意别和「确定要关店打烊吗？」搞混：那个是游戏自己的
    /// <c>StoreUIManager.closeShutterConfirmationPanel</c>，按钮直接绑死了关店逻辑，
    /// 借来用会导致点「是」就真的打烊，所以这里用通用确认框。
    ///
    /// 触发点：<c>StoreBell.RingBell</c>。柜台有人时摇铃 = 驱赶顾客并自动叫下一位，
    /// 所以这里拦下来问一句，避免手快点掉、白白放跑改造体。
    ///
    ///   是 → 走游戏自己的 <c>TryPlayerDismissCurrentClient</c>，顾客离开后自动叫下一位
    ///   否 → 什么都不做，玩家可以先去拿枪
    /// </summary>
    internal static class CustomerSkipConfirm
    {
        private const string HarmonyId = "local.qol.skip_confirm";

        /// <summary>弹窗正文。第二行是后果说明，按游戏原版的排版（空一行）。</summary>
        private const string BodyText = "未击杀改造体，是否跳过？\n\n跳过会直接送走这位顾客。";

        private static Harmony _harmony;

        /// <summary>弹窗已经开着，别因为连点摇铃再叠一层。</summary>
        private static bool _dialogOpen;

        /// <summary>「场景里没有确认面板」这条警告只打一次，免得刷屏。</summary>
        private static bool _warnedNoPanel;

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                var ringBell = AccessTools.Method(typeof(StoreBell), "RingBell", Type.EmptyTypes);
                if (ringBell == null)
                {
                    Debug.LogWarning(ModEntry.Tag + "找不到 StoreBell.RingBell，跳过确认不会生效");
                    _harmony = null;
                    return;
                }

                _harmony.Patch(ringBell,
                    prefix: new HarmonyMethod(typeof(CustomerSkipConfirm), nameof(RingBellPrefix)));

                Debug.Log(ModEntry.Tag + "未击杀改造体确认框已挂载");
            }
            catch (Exception e)
            {
                Debug.LogError(ModEntry.Tag + "挂载跳过确认失败：" + e);
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

        // ---------------------------------------------------------------
        // 拦截摇铃
        // ---------------------------------------------------------------

        /// <summary>
        /// 返回 false = 原版 RingBell 不执行（铃不响、人也不走）。
        /// </summary>
        private static bool RingBellPrefix()
        {
            try
            {
                if (ModEntry.Settings == null || !ModEntry.Settings.ConfirmSkipAug.Value)
                {
                    return true;
                }

                // 上一块确认框还开着：拦截这次摇铃（不叠加、也不放行原版）
                if (_dialogOpen)
                {
                    if (IsPanelOpen())
                    {
                        return false;
                    }
                    // 面板被右上角 X 关掉了 → 复位状态，继续正常判断
                    _dialogOpen = false;
                }

                if (!ShouldAsk())
                {
                    return true;
                }

                // 弹不出来（面板没加载）就别拦着，按原版摇铃处理
                if (!ShowDialog())
                {
                    return true;
                }

                // 这一次摇铃由确认框接手，原版逻辑先别跑
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "跳过确认判断出错，放行原版摇铃：" + e.Message);
                return true;
            }
        }

        private static bool ShouldAsk()
        {
            PlayerStore store = PlayerStore.Instance;
            if (store == null)
            {
                return false;
            }

            // 打烊/早晨摇铃本来就不该有反应，别在这里插一脚
            if (store.storeState != PlayerStore.StoreState.OPEN)
            {
                return false;
            }

            // 铃铛被锁（比如安保对话中）
            if (store.lockNextBellFlag > 0)
            {
                return false;
            }

            StoreUIManager ui = StoreUIManager.Instance;
            if (ui == null)
            {
                return false;
            }

            // 场景里没有原版确认面板就别问，避免摇铃没反应
            if (GenericConfirmationUIManager.Instance == null)
            {
                if (!_warnedNoPanel)
                {
                    _warnedNoPanel = true;
                    Debug.LogWarning(ModEntry.Tag + "当前场景没有 GenericConfirmationUIManager，跳过确认暂时不生效");
                }

                return false;
            }

            // 已经在枪战/逮捕流程里，别打断演出
            if (ui.isShot || ui.isShootingMode)
            {
                return false;
            }

            if (store.isClientBeingArrested || store.isHandlingShootout)
            {
                return false;
            }

            StoreClientInstance client;
            try
            {
                client = store.currentClientInstance;
            }
            catch
            {
                return false;
            }

            if (client == null)
            {
                // 柜台没人，摇铃就是叫下一位，正常放行
                return false;
            }

            StoreClient blueprint;
            try
            {
                blueprint = client.GetClientBlueprint();
            }
            catch
            {
                return false;
            }

            if (blueprint == null || !blueprint.isAug)
            {
                return false;
            }

            // 不是"能请走"的顾客（剧情人物那种），原版摇铃也不会理，别问
            try
            {
                if (!blueprint.dismissable)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------
        // 弹窗
        // ---------------------------------------------------------------

        private static bool ShowDialog()
        {
            GenericConfirmationUIManager panel = GenericConfirmationUIManager.Instance;
            if (panel == null)
            {
                Debug.LogWarning(ModEntry.Tag + "场景里没有确认面板，这次不拦摇铃");
                return false;
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
                        SkipOnce();
                    },
                    delegate
                    {
                        _dialogOpen = false;
                        panel.ClosePanel();
                    });

                Debug.Log(ModEntry.Tag + "弹出未击杀改造体确认框");
                return true;
            }
            catch (Exception e)
            {
                _dialogOpen = false;
                Debug.LogWarning(ModEntry.Tag + "弹确认框失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 跳过 = 走游戏自己的驱赶流程。第二个参数为 true，顾客走出门后游戏会自动叫下一位。
        /// 用 TryPlayerDismissCurrentClient 而不是 DismissCurrentClient，
        /// 是为了尊重顾客自己的拦截逻辑（有些剧情人物会拒绝离开）。
        /// </summary>
        private static void SkipOnce()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null || store.currentClientInstance == null)
                {
                    return;
                }

                store.TryPlayerDismissCurrentClient(true);

                // 驱赶是同步的：顾客被 tryInterceptDismiss 拦下时 currentClientInstance 不会清空。
                // 不校验就打「已跳过」，会让玩家以为送走了，实际上人还站着。
                if (store.currentClientInstance != null)
                {
                    Debug.Log(ModEntry.Tag + "顾客拒绝了驱赶（tryInterceptDismiss）");
                    try
                    {
                        ReputationUIManager ui = ReputationUIManager.Instance;
                        if (ui != null) ui.Notify("这位客人不肯离开");
                    }
                    catch
                    {
                        // 气泡失败不影响流程
                    }
                    return;
                }

                Debug.Log(ModEntry.Tag + "已跳过未击杀的改造体");
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "跳过顾客失败：" + e.Message);
            }
        }

        /// <summary>
        /// 按钮文字走游戏的本地化词条（和原版确认框一样是「是 / 否」），
        /// 万一取词失败就退回写的中文，保证按钮不会空着。
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

        private static bool IsPanelOpen()
        {
            try
            {
                GenericConfirmationUIManager panel = GenericConfirmationUIManager.Instance;
                return panel != null && panel.panel != null && panel.panel.activeSelf;
            }
            catch
            {
                return false;
            }
        }
    }
}
