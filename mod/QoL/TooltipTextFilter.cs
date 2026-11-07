using System;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 精简提示框文字：去掉物品的长描述，可选去掉"双击以打开"这类操作提示。
    ///
    /// 原版在 GameItemElement.GetTooltipBasic() 结尾处这样加文字：
    ///     if (shortDescription != "") AddLine(shortDescription, 白色)
    ///     if (flavorText      != "") AddLine(" " + flavorText, 灰色斜体)
    ///     ContainerHelper.CreateContainerTooltip(...)  → " " + mech_container_tooltip，青色
    ///
    /// 这三行是写死在方法体里的，没有单独的开关方法可以跳过，
    /// 所以做法是：在"生成提示框"这段时间内，把两个属性的 getter 临时改成返回空字符串。
    /// 两个 if 判断拿到的都是空串，那两行自然就不加了。
    ///
    /// 关键点：只在生成提示框期间生效（用计数器记录嵌套层数），
    /// 离开这个方法立刻恢复原值，不影响别处读取这两个属性。
    /// </summary>
    internal static class TooltipTextFilter
    {
        private const string HarmonyId = "local.qol.tooltip_text";

        private static Harmony _harmony;

        /// <summary>当前是否正在生成提示框。用计数而不是布尔，避免嵌套调用时提前复位。</summary>
        private static int _building;

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                // 1) 标记"正在生成提示框"的区间
                var basic = AccessTools.Method(typeof(GameItemElement), "GetTooltipBasic");
                if (basic != null)
                {
                    _harmony.Patch(basic,
                        prefix: new HarmonyMethod(typeof(TooltipTextFilter), nameof(BeginTooltip)),
                        finalizer: new HarmonyMethod(typeof(TooltipTextFilter), nameof(EndTooltip)));
                }
                else
                {
                    Debug.LogWarning(ModEntry.Tag + "找不到 GameItemElement.GetTooltipBasic，描述隐藏不会生效");
                }

                // 2) 区间内把描述文本变成空串
                PatchGetter("get_shortDescription", nameof(ShortDescriptionPostfix));
                PatchGetter("get_flavorText", nameof(FlavorTextPostfix));

                // 3) 容器操作提示：那个方法体里只有一行 AddLine，整段跳过即可
                var containerTip = AccessTools.Method(typeof(ContainerHelper), "CreateContainerTooltip",
                    new[] { typeof(RichTextBuilder), typeof(GameItem) });
                if (containerTip != null)
                {
                    _harmony.Patch(containerTip,
                        prefix: new HarmonyMethod(typeof(TooltipTextFilter), nameof(ContainerHintPrefix)));
                }

                // 4) 物品名下面那行厂商（「帕斯泰尔公司」「卡迈克尔制药」）：
                //    名字后面紧跟一行青色/绿色带下划线的公司名，对判断物品没什么用，
                //    和描述归到同一个开关下——描述关掉时一起消失。
                var manufacturer = AccessTools.Method(typeof(GameItem), "DisplayManufacturer",
                    new[] { typeof(RichTextBuilder), typeof(string) });
                if (manufacturer != null)
                {
                    _harmony.Patch(manufacturer,
                        prefix: new HarmonyMethod(typeof(TooltipTextFilter), nameof(ManufacturerPrefix)));
                }
                else
                {
                    Debug.LogWarning(ModEntry.Tag + "找不到 GameItem.DisplayManufacturer，厂商行隐藏不会生效");
                }

                Debug.Log(ModEntry.Tag + "提示框文字过滤已挂载");
            }
            catch (Exception e)
            {
                Debug.LogError(ModEntry.Tag + "提示框文字过滤挂载失败：" + e);
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
            _building = 0;
        }

        private static void PatchGetter(string name, string postfixName)
        {
            var getter = AccessTools.Method(typeof(GameItem), name);
            if (getter == null)
            {
                Debug.LogWarning(ModEntry.Tag + "找不到 GameItem." + name + " 的 getter");
                return;
            }

            _harmony.Patch(getter,
                postfix: new HarmonyMethod(typeof(TooltipTextFilter), postfixName));
        }

        // ---------------------------------------------------------------
        // 挂载点
        // ---------------------------------------------------------------

        private static void BeginTooltip()
        {
            _building++;
        }

        /// <summary>
        /// 不论正常返回还是抛异常都会走到这里，保证计数一定被还原。
        /// 返回原异常，等于什么都没发生。
        /// </summary>
        private static Exception EndTooltip(Exception __exception)
        {
            if (_building > 0)
            {
                _building--;
            }

            return __exception;
        }

        private static void ShortDescriptionPostfix(ref string __result)
        {
            try
            {
                if (!ShouldHideDescription())
                {
                    return;
                }

                __result = "";

            }
            catch (Exception e)
            {
                QLog.Warn("[提示框精简] 补丁出错：" + e.Message);
            }
        }

        private static void FlavorTextPostfix(ref string __result)
        {
            try
            {
                if (!ShouldHideDescription())
                {
                    return;
                }

                __result = "";

            }
            catch (Exception e)
            {
                QLog.Warn("[提示框精简] 补丁出错：" + e.Message);
            }
        }

        private static bool ShouldHideDescription()
        {
            if (_building <= 0)
            {
                return false;
            }

            QoLSettings settings = ModEntry.Settings;
            return settings != null && !settings.ShowItemDescription.Value;
        }

        /// <summary>返回 false = 不执行原方法，那行"双击以打开"就不出现。</summary>
        private static bool ContainerHintPrefix()
        {
            QoLSettings settings = ModEntry.Settings;
            return settings == null || settings.ShowItemDescription.Value;
        }

        /// <summary>
        /// 返回 false = 不执行 <c>GameItem.DisplayManufacturer</c>，厂商那行就不出现。
        /// 和描述共用同一个开关：这一行的作用只是交代公司名，属于"文字装饰"。
        /// </summary>
        private static bool ManufacturerPrefix()
        {
            QoLSettings settings = ModEntry.Settings;
            return settings == null || settings.ShowItemDescription.Value;
        }
    }
}
