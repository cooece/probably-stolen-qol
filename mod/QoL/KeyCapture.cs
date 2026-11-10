using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 设置面板的「点一下录键」。原来酿酒工坊和一键上柜台各抄了一份（连注释都一样），
    /// 这里合成一处：谁要用就注册一个 Binding，把「落库」和「怎么显示」交给功能自己。
    /// </summary>
    internal static class KeyCapture
    {
        internal sealed class Binding
        {
            /// <summary>AddInput 的配置键名，用来认出是哪一行输入框。</summary>
            internal string KeyId;

            /// <summary>日志前缀，例如 "[酿酒] "。</summary>
            internal string Tag;

            /// <summary>配置里的原值 -> 显示文字。</summary>
            internal Func<string, string> Normalize;

            /// <summary>KeyCode -> 显示名。</summary>
            internal Func<KeyCode, string> NameOf;

            /// <summary>录制完成：落库 + 写回输入框。</summary>
            internal Action<KeyCode, TMP_InputField> Bind;

            /// <summary>取消录制时的清理（通常是清焦点）。</summary>
            internal Action Cancel;

            /// <summary>气泡提示（可为 null）。</summary>
            internal Action<string> Toast;
        }

        private const string HarmonyId = "local.qol.keycapture";

        private static readonly List<Binding> Bindings = new List<Binding>();
        private static Harmony _harmony;
        private static Binding _active;
        private static TMP_InputField _field;
        private static int _frame = -100;
        private static KeyCode[] _keys;

        internal static bool IsCapturing => _active != null;

        internal static void Add(Binding binding)
        {
            if (binding != null && !Bindings.Contains(binding)) Bindings.Add(binding);
        }

        internal static void Remove(Binding binding)
        {
            if (binding == null) return;
            Bindings.Remove(binding);
            if (_active == binding) Cancel();
        }

        /// <summary>挂一次 CustomUIBuilder.AddInput，所有注册的输入框自动接管。</summary>
        internal static void EnsurePatched()
        {
            if (_harmony != null) return;

            try
            {
                MethodInfo addInput = AccessTools.Method(typeof(CustomUIBuilder), "AddInput",
                    new[] { typeof(string), typeof(string), typeof(Action<string>), typeof(string), typeof(Action<string>) });
                if (addInput == null)
                {
                    QLog.Warn("[改键] 找不到 CustomUIBuilder.AddInput，只能手动填按键名");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(addInput, postfix: new HarmonyMethod(typeof(KeyCapture), nameof(AddInputPostfix)));
            }
            catch (Exception e)
            {
                QLog.Warn("[改键] 挂设置面板补丁失败：" + e.Message);
            }
        }

        internal static void UnpatchAll()
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
            _active = null;
            _field = null;
        }

        /// <summary>AddInput 执行完，__instance.Last 是刚造好的那一行；__args[3] 是配置键名。</summary>
        private static void AddInputPostfix(CustomUIBuilder __instance, object[] __args)
        {
            if (__args == null || __args.Length < 4) return;

            string keyId = __args[3] as string;
            if (keyId == null) return;

            Binding binding = null;
            for (int i = 0; i < Bindings.Count; i++)
            {
                if (string.Equals(Bindings[i].KeyId, keyId, StringComparison.Ordinal)) { binding = Bindings[i]; break; }
            }

            if (binding == null) return;

            try
            {
                CustomUIElement element = __instance != null ? __instance.Last : null;
                if (element == null || element.input == null) return;
                Attach(binding, element.input);
            }
            catch (Exception e)
            {
                QLog.Warn(binding.Tag + "改造改键框失败：" + e.Message);
            }
        }

        private static void Attach(Binding binding, TMP_InputField input)
        {
            try
            {
                string shown = binding.Normalize(input.text);
                if (shown != null && input.text != shown) input.SetTextWithoutNotify(shown);
            }
            catch (Exception e)
            {
                QLog.Warn(binding.Tag + "刷新改键框文字失败：" + e.Message);
            }

            try
            {
                input.onSelect.AddListener(delegate { Begin(binding, input); });
            }
            catch (Exception e)
            {
                QLog.Warn(binding.Tag + "改键框接管失败，只能手动填键名：" + e);
                return;
            }

            try { input.readOnly = true; }
            catch (Exception e) { QLog.Warn(binding.Tag + "设只读失败：" + e); }

            QLog.Info(binding.Tag + "设置面板的改键框已接管：点一下就等按键");
        }

        private static void Begin(Binding binding, TMP_InputField field)
        {
            _active = binding;
            _field = field;
            _frame = Time.frameCount;
            binding.Toast?.Invoke("请按下要用的按键（右键或 Esc 取消）");
        }

        private static void Cancel()
        {
            Binding binding = _active;
            _active = null;
            _field = null;
            binding?.Cancel?.Invoke();
        }

        internal static void Poll()
        {
            if (_active == null) return;
            if (Time.frameCount <= _frame + 1) return;   // 跳过触发捕获的那一下点击

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                Binding binding = _active;
                Cancel();
                binding.Toast?.Invoke("已取消");
                return;
            }

            // 退格 / 删除 = 关掉这个热键
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                Finish(KeyCode.None);
                return;
            }

            if (!Input.anyKeyDown) return;

            // 鼠标键：左/中/侧键都能绑，右键留给「取消」
            for (int i = 0; i <= 6; i++)
            {
                if (i == 1) continue;
                if (Input.GetMouseButtonDown(i))
                {
                    Finish((KeyCode)((int)KeyCode.Mouse0 + i));
                    return;
                }
            }

            if (_keys == null) _keys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            for (int k = 0; k < _keys.Length; k++)
            {
                KeyCode code = _keys[k];
                if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6) continue;
                if (code == KeyCode.Escape || code == KeyCode.None) continue;
                if (Input.GetKeyDown(code))
                {
                    Finish(code);
                    return;
                }
            }
        }

        private static void Finish(KeyCode code)
        {
            Binding binding = _active;
            TMP_InputField field = _field;
            _active = null;
            _field = null;
            binding?.Bind?.Invoke(code, field);
        }
    }
}
