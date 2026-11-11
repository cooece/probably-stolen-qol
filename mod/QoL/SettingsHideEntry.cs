using System;
using HarmonyLib;

namespace QoL
{
    /// <summary>
    /// 把「解序器自动续卡」从模组设置页里藏掉。
    ///
    /// 这一项没有删：配置项还在，值照样存盘、照样被机器窗口标题栏那个开关读写。
    /// 只是渲染设置面板时不画它（`ModConfig.BuildEntry` 的 prefix 返回 false 即跳过该行）。
    /// 这样既满足「MOD 页面不再出现」，又不会因为删掉配置项而丢掉玩家已经选过的值。
    /// </summary>
    internal static class SettingsHideEntry
    {
        private const string HarmonyId = "local.qol.settings_hide";
        /// <summary>不希望出现在设置页里的项：解序器续卡开关、快捷键绑定数据。</summary>
        private static readonly string[] HiddenKeys = { "machine.desequencer_auto", "hotkey.store" };

        private static Harmony _harmony;

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                var build = AccessTools.Method(typeof(ModConfigWindow), "BuildEntry");
                if (build == null)
                {
                    QLog.Warn("找不到 ModConfigWindow.BuildEntry，解序器设置项仍会显示在模组页");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(build,
                    prefix: new HarmonyMethod(typeof(SettingsHideEntry), nameof(SkipHidden)));
                QLog.Info("设置页隐藏项已挂载（" + string.Join(", ", HiddenKeys) + "）");
            }
            catch (Exception e)
            {
                QLog.Error("设置页隐藏项挂载失败", e);
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
        }

        private static bool SkipHidden(ConfigEntry entry)
        {
            try
            {
                if (entry == null) return true;

                for (int i = 0; i < HiddenKeys.Length; i++)
                {
                    if (string.Equals(entry.Key, HiddenKeys[i], StringComparison.Ordinal)) return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
