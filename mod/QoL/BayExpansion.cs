using System;
using System.Reflection;
using HarmonyLib;

namespace QoL
{
    /// <summary>
    /// 让原版的「储藏区扩建套件」也能给机器区和走私者暗格加标签页。
    ///
    /// 原版这套机制是完整的（`StorageExpansionHelper`）：
    ///   GetKitTabs  一个套件加几页（只认储藏区四种 id，这是唯一卡点）
    ///   Expand      写 tag、建页、加价、消耗套件
    ///   AddTabs     把网格换成原版 MachineryTabs 面板（标签栏就是储藏区那种）
    ///   RestoreTabs 读档时按 tag 把页补回来 —— DecodeNodesInternal 对每个物品都会调
    ///
    /// 所以这里只补 GetKitTabs：原版返回 0 时，看是不是我们的容器，是就给页数。
    /// 建页、翻页、存档恢复、标签名全部由原版完成，不自己造一套。
    /// </summary>
    internal static class BayExpansion
    {
        private const string HarmonyId = "local.qol.bay_expansion";

        private static Harmony _harmony;

        internal static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.BayPages.Value;
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
                MethodInfo getKitTabs = AccessTools.Method(typeof(StorageExpansionHelper), "GetKitTabs");
                MethodInfo canExpand = AccessTools.Method(typeof(StorageExpansionHelper), "CanExpand");

                if (getKitTabs == null || canExpand == null)
                {
                    QLog.Warn("找不到 StorageExpansionHelper.GetKitTabs/CanExpand，机器区/暗格扩容未启用");
                    return;
                }

                _harmony = new Harmony(HarmonyId);

                // 页数照给：读档时 RestoreTabs 靠它重建页，否则额外页的物品会错位
                _harmony.Patch(getKitTabs,
                    postfix: new HarmonyMethod(typeof(BayExpansion), nameof(AfterGetKitTabs)));

                // 但「能不能加页」受开关控制：关掉功能只是不能再加，已有的页照常恢复
                _harmony.Patch(canExpand,
                    postfix: new HarmonyMethod(typeof(BayExpansion), nameof(AfterCanExpand)));

                MethodInfo panelValidate = AccessTools.Method(typeof(MachineryPanel), "Validate");
                if (panelValidate != null)
                {
                    _harmony.Patch(panelValidate,
                        prefix: new HarmonyMethod(typeof(BayExpansion), nameof(BeforePanelValidate)));
                }

                QLog.Info("机器区/暗格扩容已挂载（用原版储藏区扩建套件）");
            }
            catch (Exception e)
            {
                QLog.Error("机器区/暗格扩容挂载失败", e);
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

        /// <summary>
        /// 只对「已扩建」的容器生效（扩建版机器区、改进版暗格），
        /// 对齐原版「大的储藏区每套件只加 1 页」的规则。
        /// </summary>
        private static void AfterGetKitTabs(GameItem bay, ref int __result)
        {
            if (__result != 0 || bay == null || bay.identifier == null)
            {
                return;
            }

            if (IsOurBay(bay))
            {
                __result = 1;
            }
        }

        /// <summary>
        /// 去掉仓库面板顶上那条「储藏」。它是 `MachineHelper.Panel(储藏, tabs, "storage")` 的标题，
        /// 有标题时 Validate 会留 18px，没标题只留 4px —— 所以清掉 label 就自动省下一行高度。
        ///
        /// 挂在 Validate 的 prefix 上而不是构造处：玩家现有的窗口面板早就建好了，
        /// 只有每次重排时顺手清一次，才能对「已经加过页」的那些窗口也生效。
        /// </summary>
        private static void BeforePanelValidate(MachineryPanel __instance)
        {
            if (!Enabled || __instance == null || __instance.label == null)
            {
                return;
            }

            if (!string.Equals(__instance.role, "storage", StringComparison.Ordinal))
            {
                return;
            }

            // 只在第一次清：清完 label 就是 null，上面的判断会把它挡掉，不会反复重排
            try
            {
                __instance.SetLabel((MachineryLabel)null);
            }
            catch (Exception e)
            {
                QLog.Warn("去掉仓库标题失败（面板仍可用）：" + e.Message);
            }
        }

        /// <summary>开关关着时不许再加页；页数本身照常生效，所以不会弄丢已有内容。</summary>
        private static void AfterCanExpand(GameItem bay, ref bool __result)
        {
            if (!__result || Enabled || bay == null)
            {
                return;
            }

            if (IsOurBay(bay))
            {
                __result = false;
            }
        }

        /// <summary>只对我们认的两种容器生效：扩建版机器区、改进版暗格。</summary>
        private static bool IsOurBay(GameItem bay)
        {
            if (bay == null || bay.identifier == null)
            {
                return false;
            }

            return string.Equals(bay.identifier, "machine_bay_ext", StringComparison.Ordinal)
                || string.Equals(bay.identifier, "smuggler_bay_mod", StringComparison.Ordinal);
        }
    }
}
