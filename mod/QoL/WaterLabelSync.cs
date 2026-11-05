using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace QoL
{
    /// <summary>
    /// 水质变化后同步容器标签。
    ///
    /// 原版逻辑：容器里的水被净化 / 混合 / 加注 / 蒸发后，游戏只刷新「实际显示」，
    /// 标签（publicDisplay、customIntValue1、fakeCondition）仍停留在旧档位，
    /// 于是会出现「标签写锈水、实际已是纯水」的滞后，甚至被误判成水质虚标。
    ///
    /// 做法：给 WaterFeatureHelper.UpdateWaterFeature / OnLiquidChanged 挂 prefix + postfix。
    ///   prefix  读取上一次记录的实际水质（首次则从 realCondition 推断变更前水质）；
    ///   postfix 记录本次实际水质，若「水质档位」发生变化就把标签同步为真实水质。
    ///
    /// 非水液体（重要）：
    ///   GetWaterPurity 返回的是「水占全部液体的比例 × 10000」，并非杂质多少。
    ///   容器若装咖啡因、咖啡糊、梦帽菇糊、蛋白质等化学/制药液体，水占比会很低甚至 0，
    ///   游戏此时显示「未知的水」。这类容器根本不是水，水质标签不适用，一律跳过；
    ///   等它重新装回水时再强制同步一次标签（见 NotWater 基线）。
    ///
    /// 安全性：
    ///   - 比较的是档位（GetPurityArrayIndex），不是精确纯度，避免同档位微调被误判；
    ///   - 玩家用标签机手动贴的标签、顾客带来的虚标水，因为实际水质没变，不会被改动；
    ///   - 非水液体 / 空容器跳过；所有字段写入都包在 try/catch 里，出错只记日志。
    /// </summary>
    internal static class WaterLabelSync
    {
        private const string HarmonyId = "local.qol.water_label_sync";
        private const string WaterCategory = "CATEGORY_WATER_PURITY";
        private const int Unknown = int.MinValue;

        /// <summary>基线标记：容器当前装的不是水（化学制剂或空），水质标签不适用。</summary>
        private const int NotWater = -2;

        private static Harmony _harmony;

        /// <summary>非水液体跳过的提示按容器去重，避免化学制药时反复刷日志。</summary>
        private static readonly System.Collections.Generic.HashSet<string> _skipLogged =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>有效水质的最低纯度，取自游戏自己的档位表（读不到时用沟渠水下限兜底）。</summary>
        private static int _minValidPurity = -1;

        /// <summary>弱引用表：容器对象一旦被读档重建/回收，记录自动失效，不会泄漏内存。</summary>
        private static ConditionalWeakTable<GameItem, PurityBox> _lastPurity =
            new ConditionalWeakTable<GameItem, PurityBox>();

        private static int MinValidPurity
        {
            get
            {
                if (_minValidPurity > 0) return _minValidPurity;

                int min = 3500; // 兜底：GUTTERFLOW_WATER_PURITY
                try
                {
                    int[] arr = WaterFeatureHelper.WATER_PURITIES;
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (arr[i] > 0 && arr[i] < min) min = arr[i];
                        }
                    }
                }
                catch
                {
                    // 静态表尚未初始化时用兜底值
                }

                _minValidPurity = min;
                return min;
            }
        }

        private sealed class PurityBox
        {
            public int Value;
        }

        private static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.WaterLabelSync.Value;
            }
        }

        // ---------------------------------------------------------------
        // 挂载 / 卸载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            try
            {
                if (_harmony != null) return;

                _lastPurity = new ConditionalWeakTable<GameItem, PurityBox>();
                _skipLogged.Clear();
                _harmony = new Harmony(HarmonyId);

                // 主路径：净水器、净化药片、UV、污染物移除、倒水、加注、清空、植物净化等
                MethodInfo update = AccessTools.Method(
                    typeof(WaterFeatureHelper), "UpdateWaterFeature",
                    new[] { typeof(GameItem), typeof(bool) });
                if (update != null)
                {
                    _harmony.Patch(update,
                        prefix: new HarmonyMethod(typeof(WaterLabelSync), nameof(UpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(WaterLabelSync), nameof(UpdatePostfix)));
                }
                else
                {
                    QLog.Warn("[水质标签] 找不到 WaterFeatureHelper.UpdateWaterFeature，主路径未挂载");
                }

                // 补充路径：温度反应、蒸发机等走 OnLiquidChanged 的情况
                MethodInfo changed = AccessTools.Method(
                    typeof(WaterFeatureHelper), "OnLiquidChanged",
                    new[] { typeof(GameItem), typeof(bool) });
                if (changed != null)
                {
                    _harmony.Patch(changed,
                        prefix: new HarmonyMethod(typeof(WaterLabelSync), nameof(ChangedPrefix)),
                        postfix: new HarmonyMethod(typeof(WaterLabelSync), nameof(ChangedPostfix)));
                }
                else
                {
                    QLog.Warn("[水质标签] 找不到 WaterFeatureHelper.OnLiquidChanged，补充路径未挂载");
                }

                QLog.Info("[水质标签] 同步功能已挂载（UpdateWaterFeature + OnLiquidChanged）");
            }
            catch (Exception e)
            {
                QLog.Error("[水质标签] 挂载失败", e);
            }
        }

        internal static void Remove()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll(HarmonyId);
                    _harmony = null;
                }
                _lastPurity = new ConditionalWeakTable<GameItem, PurityBox>();
                _skipLogged.Clear();
            }
            catch (Exception e)
            {
                QLog.Warn("[水质标签] 卸载出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // Harmony 补丁：参数名必须和目标方法一致，用 __state 在 prefix/postfix 间传值
        // ---------------------------------------------------------------

        private static void UpdatePrefix(GameItem gameItem, ref int __state)
        {
            __state = Snapshot(gameItem);
        }

        private static void UpdatePostfix(GameItem gameItem, int __state)
        {
            Sync(gameItem, __state);
        }

        private static void ChangedPrefix(GameItem target, ref int __state)
        {
            __state = Snapshot(target);
        }

        private static void ChangedPostfix(GameItem target, int __state)
        {
            Sync(target, __state);
        }

        /// <summary>取「变更前」的实际水质。没有记录时从特征的真实条件推断。</summary>
        private static int Snapshot(GameItem item)
        {
            try
            {
                if (!Enabled || !IsWaterContainer(item)) return Unknown;

                if (_lastPurity.TryGetValue(item, out PurityBox box))
                {
                    return box.Value;
                }

                ItemFeature f = item.FindItemFeatureByCategory(WaterCategory);
                if (f == null) return Unknown;

                // realCondition 是上一次原版方法写入的真实水质条件，此刻尚未被本次调用刷新
                ItemCondition cond = f.realCondition;
                if (cond != null && cond.category == WaterCategory &&
                    cond.customValue >= MinValidPurity)
                {
                    return cond.customValue;
                }

                // 没有真实条件时，用标签值兜底（此时标签通常就等于真实水质）
                if (f.isFeatureMatch && f.customIntValue1 >= MinValidPurity)
                {
                    return f.customIntValue1;
                }

                return Unknown;
            }
            catch
            {
                return Unknown;
            }
        }

        /// <summary>水质档位真的变了，才把标签同步成真实水质。</summary>
        private static void Sync(GameItem item, int previousPurity)
        {
            try
            {
                if (!Enabled || !IsWaterContainer(item)) return;

                int now = WaterHelper.GetWaterPurity(item);

                // 化学炼药产物（咖啡因 / 咖啡糊 / 梦帽菇糊 / 蛋白质 / 淀粉…）或空容器：
                // 水占全部液体的比例低于最低水质档位，游戏原版显示「未知的水」，
                // 成分需要用成分光谱仪才能判定。这类容器根本不是「水」，
                // 绝不能替它贴水质标签（旧版会把标签写成沟渠水 / 未知的水）。
                if (now < MinValidPurity)
                {
                    // 记成 NotWater：等容器重新装回水时强制同步一次标签
                    Remember(item, NotWater);

                    string id = SafeId(item);
                    if (_skipLogged.Add(id))
                    {
                        QLog.DebugLog("[水质标签] " + id + " 为化学制剂或空容器（水占比 " +
                                      now + "/10000，需成分光谱仪判定成分），保留原标签不同步");
                    }
                    return;
                }

                // 从非水液体 / 空容器重新装回水：游戏可能已把标签重置成默认档，
                // 此时不再比较旧档位，强制同步一次，避免标签停留在「沟渠水」。
                bool resumeFromNonWater = previousPurity == NotWater;

                Remember(item, now);

                if (previousPurity == Unknown) return;

                int currentGrade = WaterFeatureHelper.GetPurityArrayIndex(now);
                if (!resumeFromNonWater)
                {
                    if (previousPurity < MinValidPurity) return; // 旧基线无效，不作比较
                    int previousGrade = WaterFeatureHelper.GetPurityArrayIndex(previousPurity);
                    if (previousGrade == currentGrade) return;
                }
                else
                {
                    QLog.DebugLog("[水质标签] " + SafeId(item) + " 重新装回水，强制同步标签");
                }

                ItemFeature f = item.FindItemFeatureByCategory(WaterCategory);
                if (f == null) return;

                ItemCondition cond = WaterFeatureHelper.GetConditionFromPurity(now);
                if (cond == null) return;

                // 显示格式与游戏原版完全一致：名称 (纯度区间 Purity)
                string[] ranges = WaterFeatureHelper.WATER_VALUE_DISPLAY;
                string range = ranges != null && currentGrade >= 0 && currentGrade < ranges.Length
                    ? ranges[currentGrade]
                    : WaterFeatureHelper.FormatPurityPercentage(now);

                string display = WaterFeatureHelper.GetWaterDisplayName(now) +
                                 " (" + range + " Purity)";

                f.fakeCondition = cond;
                f.realCondition = cond;
                f.customIntValue1 = now;
                f.isFeatureMatch = true;
                f.SetPublicDisplay(display);
                f.SetActualDisplay(display);

                string from = resumeFromNonWater
                    ? "（原为化学制剂/空容器）→ " + now
                    : previousPurity + " → " + now;
                QLog.DebugLog("[水质标签] " + SafeId(item) + " 水质 " + from +
                              "，标签同步为 " + display);
            }
            catch (Exception e)
            {
                QLog.Warn("[水质标签] 同步失败：" + e.Message);
            }
        }

        private static void Remember(GameItem item, int purity)
        {
            try
            {
                if (_lastPurity.TryGetValue(item, out PurityBox box))
                {
                    box.Value = purity;
                    return;
                }
                _lastPurity.Add(item, new PurityBox { Value = purity });
            }
            catch
            {
                // 记录失败不影响功能，下次会走 realCondition 兜底
            }
        }

        private static bool IsWaterContainer(GameItem item)
        {
            try
            {
                return item != null && item.IsTag("LIQUID_CONTAINER_TAG");
            }
            catch
            {
                return false;
            }
        }

        private static string SafeId(GameItem item)
        {
            try
            {
                return item != null && !string.IsNullOrEmpty(item.identifier)
                    ? item.identifier
                    : "?";
            }
            catch
            {
                return "?";
            }
        }
    }
}
