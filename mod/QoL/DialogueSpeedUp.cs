using System;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 全局对话加速：让所有角色（普通顾客、商人、扒手、发明家、治安官、剧情人物等）
    /// 说话速度大幅加快，停顿大幅缩短，并支持按住空格极速快进。
    /// </summary>
    internal static class DialogueSpeedUp
    {
        private const string HarmonyId = "local.qol.dialogue_speedup";

        private static Harmony _harmony;
        private static float _turboTimer;

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.DialogueFast.Value;
            }
        }

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                // 1. 打字机速度提升：覆盖原版中文 0.35x 惩罚，让文字快速打出
                var setSpeed = AccessTools.Method(typeof(StoreUIManager), "SetTypewriterSpeed");
                if (setSpeed != null)
                {
                    _harmony.Patch(setSpeed,
                        postfix: new HarmonyMethod(typeof(DialogueSpeedUp), nameof(OnSetTypewriterSpeed)));
                }

                // 2. 缩短两句对话之间的停顿等待时间
                var scalePause = AccessTools.Method(typeof(DialogUIManager), "ScaleByDialogSpeed", new[] { typeof(float) });
                if (scalePause != null)
                {
                    _harmony.Patch(scalePause,
                        postfix: new HarmonyMethod(typeof(DialogueSpeedUp), nameof(OnScaleByDialogSpeed)));
                }

                // 立即对当前界面应用一次高速
                StoreUIManager ui = StoreUIManager.Instance;
                if (ui != null)
                {
                    ui.SetTypewriterSpeed();
                }

                QLog.Info("全局对话加速已挂载");
            }
            catch (Exception e)
            {
                QLog.Warn("全局对话加速挂载失败：" + e.Message);
            }
        }

        internal static void Remove()
        {
            if (_harmony != null)
            {
                try
                {
                    _harmony.UnpatchAll(HarmonyId);
                }
                catch
                {
                }

                _harmony = null;
            }

            // 还原打字机速度
            try
            {
                StoreUIManager ui = StoreUIManager.Instance;
                if (ui != null)
                {
                    ui.SetTypewriterSpeed();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 后置拦截 SetTypewriterSpeed：原版中文乘以了 0.35f 导致慢速，
        /// 这里将打字速度提升至 1.8f（约为原版中文速度的 5 倍），打字清脆利落。
        /// </summary>
        private static void OnSetTypewriterSpeed(StoreUIManager __instance)
        {
            if (!Enabled || __instance == null)
            {
                return;
            }

            try
            {
                float boostedSpeed = Mathf.Max(1.8f, __instance.dialogSpeed * 1.8f);
                if (__instance.clientTypewriter != null)
                {
                    __instance.clientTypewriter.SetTypewriterSpeed(boostedSpeed);
                }
                if (__instance.playerTypewriter != null)
                {
                    __instance.playerTypewriter.SetTypewriterSpeed(boostedSpeed);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 后置拦截 ScaleByDialogSpeed：将句与句之间的等待停顿缩短为 30%，节奏更紧凑。
        /// </summary>
        private static void OnScaleByDialogSpeed(ref float __result)
        {
            if (!Enabled)
            {
                return;
            }

            __result *= 0.3f;
        }

        /// <summary>
        /// 每帧驱动：按住空格键极速连发快进。
        /// </summary>
        internal static void Tick(float dt)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                DialogUIManager dialog = DialogUIManager.Instance;
                DialoguePhoneUIManager phone = DialoguePhoneUIManager.Instance;

                bool dialogActive = (dialog != null && dialog.currentDialog != null) ||
                                    (phone != null && phone.currentDialog != null);

                if (!dialogActive)
                {
                    _turboTimer = 0f;
                    return;
                }

                // 遇到玩家选项分支时，必须暂停快进，等待玩家主动选择
                bool isChoice = (dialog != null && dialog.IsChoice()) ||
                                (phone != null && phone.IsChoice());
                if (isChoice)
                {
                    _turboTimer = 0f;
                    return;
                }

                // 按住空格键（或绑定的推进键）极速快进：每 0.05 秒连发推进
                if (KeyBindHelper.GetKey("advance_dialogue") || Input.GetKey(KeyCode.Space))
                {
                    _turboTimer += dt;
                    if (_turboTimer >= 0.05f)
                    {
                        _turboTimer = 0f;
                        dialog?.OnAdvancePressed();
                        phone?.OnAdvancePressed();
                    }
                }
                else
                {
                    _turboTimer = 0f;
                }
            }
            catch
            {
            }
        }
    }
}
