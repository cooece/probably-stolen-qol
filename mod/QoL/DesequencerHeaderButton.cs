using System;
using System.Collections.Generic;

namespace QoL
{
    /// <summary>
    /// 在「密码解序器」窗口的标题栏上加一个原生开关按钮：自动续卡 开/关。
    ///
    /// 用游戏自己的接口：`PixelWindow.AddHeaderButton(MachineryButton)` —— 这个接口
    /// 原生会把按钮排在关闭 ✕ 左边，样式与窗口一致，不需要自绘任何东西。
    /// 窗口是按需显示的，所以这里定时扫一遍：解序器窗口一露脸就把按钮装上去（每窗口只装一次）。
    /// </summary>
    internal static class DesequencerHeaderButton
    {
        private const float Interval = 0.4f;

        private static readonly Dictionary<PixelWindow, MachineryButton> Installed =
            new Dictionary<PixelWindow, MachineryButton>();

        private static float _timer;
        private static bool _warned;

        internal static void Tick(float dt)
        {
            _timer += dt;
            if (_timer < Interval)
            {
                return;
            }

            _timer = 0f;

            try
            {
                Scan();
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    QLog.Warn("解序器窗口按钮出错（只提示一次）：" + e.Message);
                }
            }
        }

        private static void Scan()
        {
            // 用解序器缓存：这里只需要解序器，不需要全店物品
            List<GameItem> machines = DesequencerAutoFeed.Machines();
            if (machines.Count == 0)
            {
                return;
            }

            for (int i = 0; i < machines.Count; i++)
            {
                GameItem item = machines[i];
                if (item == null)
                {
                    continue;
                }

                PixelWindow window = item.contentWindow;
                if (window == null || !window.IsVisible())
                {
                    continue;
                }

                MachineryButton button;
                if (!Installed.TryGetValue(window, out button) || button == null)
                {
                    Install(window);
                    continue;
                }

                string label = Label();
                if (!string.Equals(button.text, label, StringComparison.Ordinal))
                {
                    button.SetText(label);
                    window.Validate();
                }
            }
        }

        private static void Install(PixelWindow window)
        {
            MachineryButton button = new MachineryButton(Label(), OnClick, -1, 16);
            button.SetDescription("点睡觉时自动收走成品卡、补一张白卡。点这里可以随时开关。");
            window.AddHeaderButton(button);
            Installed[window] = button;

            QLog.Info("解序器窗口：标题栏已加「自动续卡」开关（当前 " + (Enabled ? "开" : "关") + "）");
        }

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.DesequencerAuto.Value;
            }
        }

        private static string Label()
        {
            return "自动续卡：" + (Enabled ? "开" : "关");
        }

        private static void OnClick()
        {
            QoLSettings settings = ModEntry.Settings;
            if (settings == null)
            {
                return;
            }

            bool now = !settings.DesequencerAuto.Value;
            settings.DesequencerAuto.Set(now);
            Refresh();
            QLog.Info("解序器自动续卡：" + (now ? "开" : "关") + "（标题栏按钮）");
        }

        /// <summary>设置面板改了值之后，按钮文字也跟着变。</summary>
        private static void Refresh()
        {
            string label = Label();
            foreach (KeyValuePair<PixelWindow, MachineryButton> pair in Installed)
            {
                try
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    pair.Value.SetText(label);
                    if (pair.Key != null)
                    {
                        pair.Key.Validate();
                    }
                }
                catch
                {
                    // 某个窗口没了就跳过
                }
            }
        }
    }
}
