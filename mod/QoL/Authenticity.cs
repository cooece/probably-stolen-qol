using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 真伪鉴定：谈判时自动鉴定台面上的物品（假烟、假药、虚标水质等），
    /// 并在物品提示框里显示真伪结论和真实价值。
    ///
    /// 判定只读游戏自己的 ItemFeature（fakeCondition / realCondition / ExposeFeature），
    /// 价格用游戏公开字段按同一套分段公式重算，并做一致性校验——
    /// 用「显示修正值」算出来的数对不上游戏显示价时宁可不显示，避免给出错误数字。
    ///
    /// 不画任何新 UI：结论写在提示框里，自动鉴定在后台跑（0.5 秒轮询 + 台面签名去重）。
    /// </summary>
    internal static class Authenticity
    {
        private enum Kind { Genuine, Fake, Hidden }

        private sealed class Verdict
        {
            internal Kind Kind;
            internal string Text;
        }

        /// <summary>假烟/假药/假邮票等的具体破绽标签 → 中文说明。</summary>
        private static readonly string[] IssueTags =
        {
            "CIGARETTE_LETTERING_ISSUE", "CIGARETTE_COLOR_ISSUE", "CIGARETTE_SEAL_ISSUE",
            "CIGARETTE_LOGO_ISSUE", "CIGARETTE_BOX_ISSUE",
            "STAMP_LETTERING_ISSUE", "STAMP_LOGO_ISSUE", "STAMP_SEAL_ISSUE",
            "STAMP_AMOUNT_ISSUE", "STAMP_CHIP_ISSUE",
            "ISSUE_INDICATOR", "ISSUE_LABEL", "ISSUE_SERIAL", "ISSUE_BODY", "ISSUE_COLOR"
        };

        private static readonly string[] IssueNames =
        {
            "字母印刷", "颜色", "防伪标", "商标", "包装盒",
            "字母印刷", "商标", "防伪标", "面值", "芯片",
            "指示灯", "标签", "序列号", "瓶体", "颜色"
        };

        private const string WaterCategory = "CATEGORY_WATER_PURITY";
        private const string GenuinePrefix = "CATEGORY_GENUINE_";

        private static float _timer;
        private static string _lastSignature;
        private static bool _loggedValueMismatch;

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.Authenticity.Value;
            }
        }

        // ---------------------------------------------------------------
        // 提示框：真伪结论 + 真实价值
        // ---------------------------------------------------------------

        internal static void AppendTooltip(RichTextBuilder builder, GameItem item)
        {
            if (!Enabled || builder == null || item == null)
            {
                return;
            }

            try
            {
                Verdict verdict = Judge(item);
                if (verdict != null)
                {
                    RenderHandler.ColorPalette color = verdict.Kind == Kind.Genuine
                        ? RenderHandler.ColorPalette.LightGreen
                        : (verdict.Kind == Kind.Fake
                            ? RenderHandler.ColorPalette.LightRed
                            : RenderHandler.ColorPalette.LightOrange);

                    builder.AddLine(verdict.Text, ignoreEmpty: true, color: color);
                }

                long? real = TrueValue(item);
                if (real.HasValue)
                {
                    long shown = item.GetNegociatedValue();
                    if (real.Value != shown)
                    {
                        // 真实价低于显示价 = 被高估，红色；反之为捡漏，绿色
                        builder.AddLine("真实价值 " + real.Value + "（显示 " + shown + "）",
                            ignoreEmpty: true,
                            color: real.Value < shown
                                ? RenderHandler.ColorPalette.LightRed
                                : RenderHandler.ColorPalette.LightGreen);
                    }
                }

                AppendAppraisal(builder, item);
            }
            catch (Exception e)
            {
                QLog.Warn("真伪鉴定提示出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 估价客户物品：显示正确价值
        // ---------------------------------------------------------------

        /// <summary>
        /// 估价客户（鉴定）放上台面的物品带 APPRAISAL_ITEM 标签，真实价值由
        /// AppraisalItemRegistry 的 getCorrectValue 按物品内部属性算出（手、肋排、等离子燃料）。
        /// 游戏自己只在 devMode 下显示 TRUE VALUE，这里无条件揭示，方便玩家精准出价。
        /// </summary>
        private static void AppendAppraisal(RichTextBuilder builder, GameItem item)
        {
            if (!item.IsTag("APPRAISAL_ITEM"))
            {
                return;
            }

            try
            {
                int correct = AppraisalHelper.GetCorrectBaseValue(item);
                if (correct > 0)
                {
                    builder.AddLine("正确价值 " + correct,
                        ignoreEmpty: true,
                        color: RenderHandler.ColorPalette.LightYellow);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("估价物品正确价值出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 真伪判定
        // ---------------------------------------------------------------

        private static Verdict Judge(GameItem item)
        {
            if (item.IsTag("WINE_COUNTERFEIT_TAG"))
            {
                return Fake("假货：仿冒酒");
            }

            // 水质虚标：标签档位比真实档位好
            ItemFeature water = WaterFeature(item);
            int waterPurity = water != null ? WaterHelper.GetWaterPurity(item) : 0;

            if (water != null && !water.IsExposed())
            {
                int label = water.customIntValue1;
                int actual = waterPurity;
                if (WaterFeatureHelper.GetPurityArrayIndex(label) > WaterFeatureHelper.GetPurityArrayIndex(actual))
                {
                    string percent = (actual / 100f).ToString("0.00", CultureInfo.InvariantCulture) + "%";
                    return Fake("水质虚标：标 " + WaterFeatureHelper.GetWaterDisplayName(label) +
                                "，实为 " + WaterFeatureHelper.GetWaterDisplayName(actual) + " " + percent);
                }
            }

            List<ItemFeature> features = item.itemFeatures;
            if (features == null)
            {
                return null;
            }

            Verdict hidden = null;
            bool genuine = false;

            for (int i = 0; i < features.Count; i++)
            {
                ItemFeature f = features[i];
                if (!Judgeable(f) || string.Equals(f.category, WaterCategory, StringComparison.Ordinal))
                {
                    continue;
                }

                string category = f.category ?? "";
                string realName = RealName(f);

                if (f.useCondition && category.StartsWith(GenuinePrefix, StringComparison.Ordinal))
                {
                    string id = f.realCondition != null ? (f.realCondition.identifier ?? "") : "";
                    if (!id.StartsWith("genuine", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fake("假货：" + realName + Issues(item));
                    }

                    genuine = true;
                }
                else if (hidden == null && !f.IsFeatureMatch() && !f.IsExposed() && f.IsRealFeatureCheaper())
                {
                    hidden = new Verdict { Kind = Kind.Hidden, Text = "有隐藏缺陷：" + realName };
                }
            }

            if (hidden != null)
            {
                return hidden;
            }

            if (genuine)
            {
                return new Verdict { Kind = Kind.Genuine, Text = "真品" };
            }

            // 水容器没有其它特征时，也把真实水质报出来
            if (water != null)
            {
                int actual = waterPurity;
                string percent = (actual / 100f).ToString("0.00", CultureInfo.InvariantCulture) + "%";
                string text = "水质：" + WaterFeatureHelper.GetWaterDisplayName(actual) + " " + percent;

                // 真实档位比标签档位还好（低标）：补一句说明，和 DangpuPlus 一致
                int labelIndex = WaterFeatureHelper.GetPurityArrayIndex(water.customIntValue1);
                int actualIndex = WaterFeatureHelper.GetPurityArrayIndex(actual);
                if (labelIndex < actualIndex)
                {
                    text += "（低标：标 " + WaterFeatureHelper.GetWaterDisplayName(water.customIntValue1) + "）";
                }

                return new Verdict
                {
                    Kind = Kind.Genuine,
                    Text = text
                };
            }

            return null;
        }

        private static Verdict Fake(string text)
        {
            return new Verdict { Kind = Kind.Fake, Text = text };
        }

        private static bool Judgeable(ItemFeature f)
        {
            return f != null && !f.isDisabled &&
                   f.featureType == ItemFeature.FeatureType.Normale && f.isExposable;
        }

        private static string RealName(ItemFeature f)
        {
            string name = null;
            try
            {
                name = f.GetBaseRealDisplay();
            }
            catch
            {
                // 某些特征没有 real display，退回 condition 名
            }

            if (string.IsNullOrEmpty(name) && f.realCondition != null)
            {
                name = f.realCondition.display;
            }

            return string.IsNullOrEmpty(name) ? "未知" : name;
        }

        private static string Issues(GameItem item)
        {
            var parts = new List<string>();
            for (int i = 0; i < IssueTags.Length; i++)
            {
                if (item.IsTag(IssueTags[i]))
                {
                    parts.Add(IssueNames[i]);
                }
            }

            return parts.Count == 0 ? "" : "（问题：" + string.Join("、", parts.ToArray()) + "）";
        }

        private static ItemFeature WaterFeature(GameItem item)
        {
            if (!item.IsTag("LIQUID_CONTAINER_TAG") || item.IsTag("WINE_CONTAINER_TAG") ||
                WaterHelper.IsEmpty(item))
            {
                return null;
            }

            return item.FindItemFeatureByCategory(WaterCategory);
        }

        // ---------------------------------------------------------------
        // 真实价值：按游戏同一套分段公式，用「真实修正值」重算
        // ---------------------------------------------------------------

        private static long? TrueValue(GameItem item)
        {
            List<ItemFeature> features = item.itemFeatures;
            if (features == null)
            {
                return null;
            }

            long innateShown = 0, marketShown = 0;
            long innateReal = 0, marketReal = 0;
            double finalShown = 1.0, finalReal = 1.0;
            bool differs = false;

            // 真假烟/邮票/注射器这类「变形」物品：modValue 恒为 0，真实价值靠揭穿动作
            // 直接改 unitValue（假烟 100→50、假邮票 80→8、假注射器 160→40）。它们的
            // compareValue 精确记录了这个折扣（-50 / -90 / -75），用比例重算即可，无需触发动作。
            double transformativeRatio = 1.0;
            bool hasTransformative = false;

            for (int i = 0; i < features.Count; i++)
            {
                ItemFeature f = features[i];
                if (f == null || f.isDisabled)
                {
                    continue;
                }

                int shown = f.GetCurrentValueMod();
                int real = f.GetActualValueModifier();
                ItemFeature.ValueStage stage = f.GetValueStage();

                AddStage(stage, shown, ref innateShown, ref marketShown, ref finalShown);
                AddStage(stage, real, ref innateReal, ref marketReal, ref finalReal);
                if (shown != real)
                {
                    differs = true;
                }

                if (TransformativeCheaper(f))
                {
                    int fakeCv = f.fakeCondition != null ? f.fakeCondition.compareValue : 0;
                    int realCv = f.realCondition != null ? f.realCondition.compareValue : 0;
                    transformativeRatio *= Math.Max(0.0, (100.0 + realCv) / (100.0 + fakeCv));
                    hasTransformative = true;
                }
            }

            long baseValue = item.unitValue + item.GetComplexeItemValue();
            long late = item.GetItemMiscValue(useMarkup: true);
            long marketScaled = WaterHelper.GetContainerPrice(item);
            long negotiated = item.GetNegociatedValue();

            // 自校验：用「显示修正值」算出来必须等于游戏显示价，否则公式没对齐，不显示
            long check = GameItem.CheckValueSafe(
                Compose(baseValue, innateShown, marketShown, finalShown, late, marketScaled));

            if (check != negotiated)
            {
                if (!_loggedValueMismatch)
                {
                    _loggedValueMismatch = true;
                    QLog.Warn("真实价值自校验失败：differs=" + differs +
                        " base=" + baseValue + " innateShown=" + innateShown +
                        " marketShown=" + marketShown + " finalShown=" + finalShown.ToString("0.00") +
                        " late=" + late + " scaled=" + marketScaled +
                        " check=" + check + " negotiated=" + negotiated);
                }

                return null;
            }

            // 变形物品：直接按折扣率折算当前价（unitValue 会被动作改成固定值，compareValue 与之精确对应）
            if (hasTransformative && transformativeRatio < 0.9999)
            {
                return GameItem.CheckValueSafe(
                    Compose((long)(baseValue * transformativeRatio), innateReal, marketReal, finalReal, late, marketScaled));
            }

            if (!differs)
            {
                return null;
            }

            return GameItem.CheckValueSafe(
                Compose(baseValue, innateReal, marketReal, finalReal, late, marketScaled));
        }

        /// <summary>
        /// 真假烟/邮票/注射器等：真实价比标称低、且靠动作改 unitValue 而非 modValue。
        /// 只在「尚未揭穿」时成立——揭穿后 unitValue 已被动作改成真实值，
        /// 显示价就是真实价，不能再叠加折扣。
        /// </summary>
        private static bool TransformativeCheaper(ItemFeature f)
        {
            if (f.IsExposed())
            {
                return false;
            }

            if (!f.useCondition || f.fakeCondition == null || f.realCondition == null)
            {
                return false;
            }

            if (!f.fakeCondition.isTransformative && !f.realCondition.isTransformative)
            {
                return false;
            }

            return f.fakeCondition.compareValue > f.realCondition.compareValue;
        }

        private static void AddStage(ItemFeature.ValueStage stage, int mod,
            ref long innate, ref long market, ref double final)
        {
            switch (stage)
            {
                case ItemFeature.ValueStage.Market:
                    market += mod;
                    break;
                case ItemFeature.ValueStage.Final:
                    final *= Math.Max(0.0, 1.0 + mod / 100.0);
                    break;
                default:
                    innate += mod;
                    break;
            }
        }

        /// <summary>游戏 ComposeStagedValue 的数学公式（纯算术，自己按行为重写）。</summary>
        private static long Compose(long baseValue, long innate, long market, double final,
            long late, long marketScaled)
        {
            double a = Math.Max(0.0, baseValue * (1.0 + innate / 100.0));
            double b = Math.Max(0.0, 1.0 + market / 100.0);
            return (long)Math.Round(((a + marketScaled) * b + late) * final);
        }

        // ---------------------------------------------------------------
        // 谈判时自动鉴定
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Enabled)
            {
                return;
            }

            _timer += dt;
            if (_timer < 0.5f)
            {
                return;
            }

            _timer = 0f;

            try
            {
                if (!NegotiationOpen())
                {
                    _lastSignature = null;
                    return;
                }

                string signature = Signature();
                if (string.IsNullOrEmpty(signature) || signature == _lastSignature)
                {
                    return;
                }

                _lastSignature = signature;
                AppraiseAll();
            }
            catch (Exception e)
            {
                QLog.Warn("自动鉴定出错：" + e.Message);
            }
        }

        private static bool NegotiationOpen()
        {
            NegociationUIManager manager = NegociationUIManager.Instance;
            return manager != null && manager.uiObject != null && manager.uiObject.activeInHierarchy;
        }

        /// <summary>只处理顾客放上台面、还不归我们所有的物品。</summary>
        private static List<GameItem> CounterItems()
        {
            var list = new List<GameItem>();

            EmporiumEntry entry = EmporiumEntry.Instance;
            List<GameItem> counters = entry != null ? entry.GetCountersItems() : null;
            if (counters != null)
            {
                for (int i = 0; i < counters.Count; i++)
                {
                    GameItem item = counters[i];
                    if (item != null && !GeneralHelper.IsItemOwned(item))
                    {
                        list.Add(item);
                    }
                }
            }

            NegociationUIManager manager = NegociationUIManager.Instance;
            GameItem current = manager != null ? manager.currentNegociatedItem : null;
            if (current != null && !GeneralHelper.IsItemOwned(current) && !list.Contains(current))
            {
                list.Add(current);
            }

            return list;
        }

        private static string Signature()
        {
            var sb = new StringBuilder();
            List<GameItem> items = CounterItems();
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(items[i].uniqueId).Append(',');
            }

            return sb.ToString();
        }

        private static void AppraiseAll()
        {
            List<GameItem> items = CounterItems();
            int exposed = 0;

            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];

                // 虚标水质：揭穿标签并让游戏重算显示
                ItemFeature water = WaterFeature(item);
                if (water != null && !water.IsExposed())
                {
                    int label = water.customIntValue1;
                    int actual = WaterHelper.GetWaterPurity(item);
                    if (WaterFeatureHelper.GetPurityArrayIndex(label) > WaterFeatureHelper.GetPurityArrayIndex(actual))
                    {
                        water.ExposeFeature(true, false);
                        WaterFeatureHelper.OnWaterLabelUpdated(item);
                        exposed++;
                    }
                }

                List<ItemFeature> features = item.itemFeatures;
                if (features == null)
                {
                    continue;
                }

                for (int j = 0; j < features.Count; j++)
                {
                    ItemFeature f = features[j];
                    if (!Judgeable(f) || string.Equals(f.category, WaterCategory, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // 真实状态比标称差、且尚未揭穿 → 自动揭穿
                    if (!f.IsFeatureMatch() && !f.IsExposed() && f.IsRealFeatureCheaper())
                    {
                        f.ExposeFeature(true, false);
                        exposed++;
                    }
                }
            }

            if (exposed > 0)
            {
                NegociationUIManager manager = NegociationUIManager.Instance;
                if (manager != null)
                {
                    manager.RefreshUI();
                }

                QLog.Info("自动鉴定：揭穿 " + exposed + " 处");
            }
        }
    }
}
