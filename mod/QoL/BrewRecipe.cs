using System;
using System.Collections.Generic;
using System.Text;

namespace QoL
{
    /// <summary>
    /// 酒的档位（对应游戏 ItemConditionList 里的酒品品质，名字取游戏本地化）。
    /// </summary>
    internal static class WineGrade
    {
        internal const string CounterfeitName = "阴光莓果酿";
        internal const int CounterfeitPrice = 44;

        /// <summary>游戏里 wineScore &gt;= 6 就封顶在同一档，再多分也不会更值钱。</summary>
        internal const int TopTierScore = 6;

        /// <summary>档位：5 琼浆玉液 / 4 臻品窖藏 / 3 量产酒液 / 2 手工自酿 / 1 粗桶劣酿 / 0 难以下咽。</summary>
        internal const int Top = 5;

        private static string[] _names;

        private static string[] Names
        {
            get
            {
                if (_names != null) return _names;

                _names = new string[6];
                _names[0] = Loc("mech_condition_wine_master_vintage", "琼浆玉液");
                _names[1] = Loc("mech_condition_wine_reserve", "臻品窖藏");
                _names[2] = Loc("mech_condition_wine_production", "量产酒液");
                _names[3] = Loc("mech_condition_wine_homebrew", "手工自酿");
                _names[4] = Loc("mech_condition_wine_bucket_brew", "粗桶劣酿");
                _names[5] = Loc("mech_condition_wine_undrinkable", "难以下咽");
                return _names;
            }
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                string s = LocHelper.GetLocalizedMechanic(key);
                if (!string.IsNullOrEmpty(s) && s != key) return s;
            }
            catch { }
            return fallback;
        }

        internal static int TierOf(int score)
        {
            if (score >= 6) return 5;
            if (score == 5) return 4;
            if (score >= 3) return 3;
            if (score == 2) return 2;
            if (score == 1) return 1;
            return 0;
        }

        /// <summary>tier 5 → 琼浆玉液 …… tier 0 → 难以下咽。</summary>
        internal static string NameOfTier(int tier)
        {
            string[] n = Names;
            if (tier < 0) tier = 0;
            if (tier > 5) tier = 5;
            return n[5 - tier];
        }

        internal static string NameOf(int score)
        {
            return NameOfTier(TierOf(score));
        }

        /// <summary>档位颜色：越高越显眼。</summary>
        internal static UnityEngine.Color ColorOfTier(int tier)
        {
            switch (tier)
            {
                case 5: return new UnityEngine.Color(0.95f, 0.80f, 0.35f);   // 金
                case 4: return new UnityEngine.Color(0.78f, 0.62f, 0.97f);   // 紫
                case 3: return new UnityEngine.Color(0.56f, 0.80f, 0.97f);   // 蓝
                case 2: return new UnityEngine.Color(0.72f, 0.74f, 0.68f);
                case 1: return new UnityEngine.Color(0.66f, 0.64f, 0.60f);
                default: return new UnityEngine.Color(0.70f, 0.52f, 0.50f);
            }
        }

        internal static UnityEngine.Color ColorOf(int score)
        {
            return ColorOfTier(TierOf(score));
        }
    }

    /// <summary>
    /// 评分。完全照抄游戏 WineHelper.StartFermentation：
    ///   分数 = Σ 每颗果实的品相（优质 +2 / 普通 +1 / 干瘪 0 / 发霉 -1）
    ///        + 放了两颗以上再 +1
    ///        + 有酵母 +1
    ///        + 水质修正（纯·优 +1 / 基础 0 / 幽灵 -1 / 铁锈 -2 / 沟渠 -3）
    /// 酵母种类不加分，只影响发酵要几夜（力量 4 = 一夜，2 = 两夜）。
    /// </summary>
    internal static class BrewScore
    {
        internal static int Of(IList<int> grades, bool hasYeast, int waterTier)
        {
            int score = 0;
            for (int i = 0; i < grades.Count; i++) score += BerryGrade.Score(grades[i]);
            if (grades.Count >= 2) score++;
            if (hasYeast) score++;
            score += BrewScan.WaterScore(waterTier);
            return score;
        }

        /// <summary>发酵要几夜：初始 4，每晚按酵母力量递减。</summary>
        internal static int Days(int yeastPower)
        {
            int power = yeastPower > 0 ? yeastPower : 1;
            return (4 + power - 1) / power;
        }
    }

    /// <summary>一次酿造要用的搭配：果子（按品相列）、酵母、水质。</summary>
    internal sealed class BrewCombo
    {
        internal readonly List<int> Grades = new List<int>();
        internal string YeastId = string.Empty;   // 空 = 自动
        internal int WaterTier = -1;              // -1 = 自动
        internal bool Counterfeit;

        internal string BerryText
        {
            get
            {
                if (Counterfeit) return "不加果实";
                if (Grades.Count == 0) return "没选果实";

                int[] counts = new int[4];
                for (int i = 0; i < Grades.Count; i++) counts[Grades[i]]++;

                StringBuilder sb = new StringBuilder();
                for (int g = 0; g < 4; g++)
                {
                    if (counts[g] <= 0) continue;
                    if (sb.Length > 0) sb.Append("+");
                    sb.Append(BerryGrade.Label[g]).Append('×').Append(counts[g]);
                }
                return sb.ToString();
            }
        }

        internal BrewCombo Clone()
        {
            BrewCombo c = new BrewCombo();
            c.YeastId = YeastId;
            c.WaterTier = WaterTier;
            c.Counterfeit = Counterfeit;
            for (int i = 0; i < Grades.Count; i++) c.Grades.Add(Grades[i]);
            return c;
        }
    }

    /// <summary>表格里的一行：某个档位能酿几瓶。</summary>
    internal sealed class BrewTierRow
    {
        internal int Tier;            // 0..5
        internal bool Counterfeit;
        internal int Count;
        internal int ScoreMin;
        internal int ScoreMax;
        internal int Days;
        internal BrewCombo Sample;    // 第一瓶的搭配，用来显示配方 / 照着酿
        internal string Reason;       // Count == 0 时说明缺什么

        internal string Name { get { return Counterfeit ? WineGrade.CounterfeitName : WineGrade.NameOfTier(Tier); } }

        internal string ScoreText
        {
            get
            {
                if (Counterfeit) return "走私";
                if (Count <= 0) return "—";
                return ScoreMin == ScoreMax ? ScoreMin + " 分" : ScoreMin + "~" + ScoreMax + " 分";
            }
        }

        internal string RecipeText
        {
            get
            {
                if (Count <= 0 || Sample == null) return "—";
                string water = Sample.WaterTier >= 0 ? BrewScan.TierName(Sample.WaterTier) : "水";
                return Sample.BerryText + " + " + water;
            }
        }
    }

    /// <summary>
    /// 按现有材料，一口气分完能酿出的每种酒各几瓶。
    ///
    /// 两种分法（材料共用，只能二选一）：
    ///   高品质优先：先凑 6 分（普通+优质），再 5 分、4 分…… 发霉果不参与
    ///   多瓶优先  ：一颗果一瓶，瓶数最多（酒档次会低一些）
    /// 走私酒单独用「合成酵母 + 污染水」，两档都会顺手用掉剩下的脏水和合成酵母。
    /// </summary>
    internal static class BrewAllocator
    {
        private static readonly int[] WaterBest = { 1, 0 };      // 优质水、纯水
        private static readonly int[] WaterPlain = { 2 };        // 基础水
        private static readonly int[] WaterDirty = { 5, 4, 3 };  // 沟渠、铁锈、幽灵

        private struct Template
        {
            internal int A;              // 果实 A（-1 = 不加）
            internal int B;              // 果实 B（-1 = 不加）
            internal int[] WaterTiers;
            internal bool Counterfeit;
        }

        // 高品质优先：按分数从高到低试
        private static readonly Template[] QualityOrder =
        {
            T(BerryGrade.Standard, BerryGrade.Small,    WaterBest),   // 6 分
            T(BerryGrade.Small,    BerryGrade.Small,    WaterPlain),  // 6 分（没好水时）
            T(BerryGrade.Small,    BerryGrade.Small,    WaterBest),   // 7 分（只剩优质果时）
            T(BerryGrade.Standard, BerryGrade.Standard, WaterBest),   // 5 分
            T(BerryGrade.Small,    BerryGrade.Waste,    WaterBest),   // 5 分
            T(BerryGrade.Small,    -1,                  WaterBest),   // 4 分
            T(BerryGrade.Standard, BerryGrade.Waste,    WaterBest),   // 4 分
            T(BerryGrade.Waste,    BerryGrade.Waste,    WaterBest),   // 3 分
            T(BerryGrade.Standard, -1,                  WaterBest),   // 3 分
            T(BerryGrade.Small,    -1,                  WaterPlain),  // 3 分（只剩基础水）
            T(BerryGrade.Standard, -1,                  WaterPlain),  // 2 分（把剩下的普通果也用掉）
            T(BerryGrade.Waste,    -1,                  WaterPlain)   // 1 分（最后剩下干瘪果）
        };

        private static Template T(int a, int b, int[] waters)
        {
            Template t = new Template();
            t.A = a;
            t.B = b;
            t.WaterTiers = waters;
            t.Counterfeit = a < 0;
            return t;
        }

        internal static List<BrewTierRow> Plan(BrewStock stock, bool qualityFirst, string preferredYeast,
            int waterTierFilter, int yeastPower, out string note)
        {
            note = string.Empty;
            List<BrewTierRow> rows = EmptyRows(yeastPower);

            if (stock == null) { note = "没读到库存。"; return rows; }
            if (stock.EmptyBottles.Count <= 0)
            {
                note = stock.WineBottles.Count > 0 ? "没有空酒瓶（现有的瓶都装着酒）" : "没有空酒瓶";
                FillReasons(rows, stock, 0, waterTierFilter);
                return rows;
            }

            Budget budget = new Budget(stock, waterTierFilter);
            if (qualityFirst) FillByQuality(rows, budget, preferredYeast);
            else FillByCount(rows, budget, preferredYeast);

            FillCounterfeit(rows, budget);

            int total = 0;
            for (int i = 0; i < rows.Count; i++) total += rows[i].Count;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Count > 0) continue;
                rows[i].Reason = ReasonFor(rows[i], stock, total, waterTierFilter);
            }

            note = total > 0
                ? "共 " + total + " 瓶（材料一次分完，各档不能叠加）"
                : "现有材料一瓶都酿不了";
            return rows;
        }

        private static List<BrewTierRow> EmptyRows(int yeastPower)
        {
            List<BrewTierRow> rows = new List<BrewTierRow>();
            for (int tier = WineGrade.Top; tier >= 0; tier--)
            {
                BrewTierRow r = new BrewTierRow();
                r.Tier = tier;
                r.Days = BrewScore.Days(yeastPower);
                rows.Add(r);
            }

            BrewTierRow fake = new BrewTierRow();
            fake.Tier = 3;
            fake.Counterfeit = true;
            fake.Days = BrewScore.Days(yeastPower);
            rows.Add(fake);
            return rows;
        }

        private sealed class Budget
        {
            internal int Bottles;
            internal readonly int[] Berry = new int[4];
            internal readonly List<BrewWater> Waters = new List<BrewWater>();
            internal readonly List<BrewYeast> Yeasts = new List<BrewYeast>();

            internal Budget(BrewStock stock, int waterTierFilter)
            {
                Bottles = stock.EmptyBottles.Count;

                // 高品质优先永远不用发霉果；多瓶优先才把发霉也算进来
                for (int i = 0; i < stock.Berries.Count; i++)
                {
                    BrewBerry b = stock.Berries[i];
                    Berry[b.Grade] += b.Count;
                }

                for (int i = 0; i < stock.Waters.Count; i++)
                {
                    BrewWater w = stock.Waters[i];
                    if (waterTierFilter >= 0 && w.Tier != waterTierFilter) continue;

                    BrewWater copy = new BrewWater();
                    copy.Item = w.Item;
                    copy.Name = w.Name;
                    copy.Tier = w.Tier;
                    copy.Ml = w.Ml;
                    copy.RemainMl = w.Ml;
                    Waters.Add(copy);
                }

                for (int i = 0; i < stock.Yeasts.Count; i++) Yeasts.Add(stock.Yeasts[i].Clone());
            }

            internal void TakeYeast(BrewYeast y)
            {
                if (y != null && !y.Reusable && y.Remain > 0) y.Remain--;
            }
        }

        // ---------------------------------------------------------------
        // 高品质优先
        // ---------------------------------------------------------------

        private static void FillByQuality(List<BrewTierRow> rows, Budget budget, string preferredYeast)
        {
            while (budget.Bottles > 0)
            {
                bool made = false;

                for (int i = 0; i < QualityOrder.Length; i++)
                {
                    Template t = QualityOrder[i];
                    if (t.Counterfeit) continue;   // 走私留到最后统一处理

                    BrewYeast yeast = PickYeast(budget.Yeasts, preferredYeast);
                    if (yeast == null) return;

                    BrewWater water = PickWater(budget, t.WaterTiers);
                    if (water == null) continue;

                    if (t.A >= 0)
                    {
                        if (budget.Berry[t.A] <= 0) continue;
                        if (t.B >= 0 && t.B == t.A && budget.Berry[t.A] < 2) continue;
                        if (t.B >= 0 && t.B != t.A && budget.Berry[t.B] <= 0) continue;
                    }

                    // 成交
                    BrewCombo combo = new BrewCombo();
                    combo.YeastId = yeast.Id;
                    combo.WaterTier = water.Tier;
                    if (t.A >= 0) combo.Grades.Add(t.A);
                    if (t.B >= 0) combo.Grades.Add(t.B);

                    int score = BrewScore.Of(combo.Grades, true, water.Tier);

                    budget.Bottles--;
                    if (t.A >= 0) budget.Berry[t.A]--;
                    if (t.B >= 0) budget.Berry[t.B]--;
                    budget.TakeYeast(yeast);
                    water.RemainMl -= BrewScan.WaterPerBottle;

                    Accumulate(rows, combo, score);
                    made = true;
                    break;
                }

                if (!made) return;
            }
        }

        // ---------------------------------------------------------------
        // 多瓶优先：一颗果一瓶
        // ---------------------------------------------------------------

        private static void FillByCount(List<BrewTierRow> rows, Budget budget, string preferredYeast)
        {
            int[] order = { BerryGrade.Small, BerryGrade.Standard, BerryGrade.Waste, BerryGrade.Mold };

            while (budget.Bottles > 0)
            {
                BrewYeast yeast = PickYeast(budget.Yeasts, preferredYeast);
                if (yeast == null) return;

                BrewWater water = PickWater(budget, WaterBest) ?? PickWater(budget, WaterPlain);
                if (water == null) return;

                int grade = -1;
                for (int i = 0; i < order.Length; i++)
                {
                    if (budget.Berry[order[i]] > 0) { grade = order[i]; break; }
                }

                BrewCombo combo = new BrewCombo();
                combo.YeastId = yeast.Id;
                combo.WaterTier = water.Tier;
                if (grade < 0) return;      // 没果子了，剩下的交给走私分支
                combo.Grades.Add(grade);

                int score = BrewScore.Of(combo.Grades, true, water.Tier);

                budget.Bottles--;
                budget.Berry[grade]--;
                budget.TakeYeast(yeast);
                water.RemainMl -= BrewScan.WaterPerBottle;

                Accumulate(rows, combo, score);
            }
        }

        // ---------------------------------------------------------------
        // 走私酒
        // ---------------------------------------------------------------

        private static void FillCounterfeit(List<BrewTierRow> rows, Budget budget)
        {
            BrewTierRow row = rows[rows.Count - 1];

            while (budget.Bottles > 0)
            {
                BrewYeast yeast = PickSynthetic(budget.Yeasts);
                if (yeast == null) return;

                BrewWater water = PickWater(budget, WaterDirty);
                if (water == null) return;

                BrewCombo combo = new BrewCombo();
                combo.Counterfeit = true;
                combo.YeastId = yeast.Id;
                combo.WaterTier = water.Tier;

                budget.Bottles--;
                budget.TakeYeast(yeast);
                water.RemainMl -= BrewScan.WaterPerBottle;

                if (row.Count == 0) row.Sample = combo;
                row.Count++;
                row.ScoreMin = 3;
                row.ScoreMax = 3;
            }
        }

        // ---------------------------------------------------------------
        // 记账
        // ---------------------------------------------------------------

        private static void Accumulate(List<BrewTierRow> rows, BrewCombo combo, int score)
        {
            int tier = WineGrade.TierOf(score);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Counterfeit || rows[i].Tier != tier) continue;

                if (rows[i].Count == 0)
                {
                    rows[i].Sample = combo;
                    rows[i].ScoreMin = score;
                    rows[i].ScoreMax = score;
                }
                else
                {
                    if (score < rows[i].ScoreMin) rows[i].ScoreMin = score;
                    if (score > rows[i].ScoreMax) rows[i].ScoreMax = score;
                }
                rows[i].Count++;
                return;
            }
        }

        // ---------------------------------------------------------------
        // 取料
        // ---------------------------------------------------------------

        private static BrewWater PickWater(Budget budget, int[] tiers)
        {
            for (int i = 0; i < tiers.Length; i++)
            {
                BrewWater best = null;
                for (int j = 0; j < budget.Waters.Count; j++)
                {
                    BrewWater w = budget.Waters[j];
                    if (w.Tier != tiers[i] || w.RemainMl < BrewScan.WaterPerBottle) continue;
                    if (best == null || w.RemainMl > best.RemainMl) best = w;
                }
                if (best != null) return best;
            }
            return null;
        }

        private static BrewYeast PickYeast(List<BrewYeast> list, string preferred)
        {
            if (!string.IsNullOrEmpty(preferred))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Id == preferred && list[i].Remain > 0 && !list[i].Synthetic) return list[i];
                }
            }

            string[] order = { "wine_yeast_infinite", "wine_superyeast", "wine_yeast" };
            for (int i = 0; i < order.Length; i++)
            {
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].Id == order[i] && list[j].Remain > 0) return list[j];
                }
            }

            BrewYeast best = null;
            for (int i = 0; i < list.Count; i++)
            {
                BrewYeast y = list[i];
                if (y.Remain <= 0 || y.Synthetic) continue;
                if (best == null || y.Power > best.Power) best = y;
            }
            return best;
        }

        private static BrewYeast PickSynthetic(List<BrewYeast> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Synthetic && list[i].Remain > 0) return list[i];
            }
            return null;
        }

        // ---------------------------------------------------------------
        // 「做不出来」的原因
        // ---------------------------------------------------------------

        private static void FillReasons(List<BrewTierRow> rows, BrewStock stock, int total, int waterTierFilter)
        {
            for (int i = 0; i < rows.Count; i++) rows[i].Reason = ReasonFor(rows[i], stock, total, waterTierFilter);
        }

        private static string ReasonFor(BrewTierRow row, BrewStock stock, int total, int waterTierFilter)
        {
            if (row.Counterfeit)
            {
                if (!HasSyntheticYeast(stock)) return "缺合成酵母";
                if (waterTierFilter >= 0 && waterTierFilter < 3) return "走私酒要用污染水";
                if (!HasDirtyWater(stock)) return "缺污染水";
                return total > 0 ? "材料优先分给了上面" : "材料不够";
            }

            if (stock.EmptyBottles.Count <= 0) return "没有空瓶";
            if (total > 0) return "材料优先分给了上面";
            if (stock.BerryTotal() == 0 && stock.BerryCount(BerryGrade.Mold) == 0) return "没有荧光莓";
            if (!HasCleanWater(stock)) return "没有干净水";
            if (stock.Yeasts.Count == 0) return "没有酵母";
            return "凑不出这个档位";
        }

        private static bool HasCleanWater(BrewStock stock)
        {
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                if (stock.Waters[i].Tier <= 2 && stock.Waters[i].Ml >= BrewScan.WaterPerBottle) return true;
            }
            return false;
        }

        private static bool HasDirtyWater(BrewStock stock)
        {
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                if (stock.Waters[i].Tier >= 3 && stock.Waters[i].Ml >= BrewScan.WaterPerBottle) return true;
            }
            return false;
        }

        private static bool HasSyntheticYeast(BrewStock stock)
        {
            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                if (stock.Yeasts[i].Synthetic && stock.Yeasts[i].Items.Count > 0) return true;
            }
            return false;
        }
    }
}
