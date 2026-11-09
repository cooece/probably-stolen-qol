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
        internal string YeastId = string.Empty;
        internal int WaterTier = -1;
        internal bool Counterfeit;

        internal string BerryText
        {
            get
            {
                if (Counterfeit) return "不加果实";
                if (Grades.Count == 0) return "—";

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
    }

    /// <summary>表格里的一行：某个档位能酿几瓶。</summary>
    internal sealed class BrewTierRow
    {
        internal int Tier;             // 0..5
        internal bool Counterfeit;
        internal int Count;            // 当前分法分到的瓶数
        internal int Max;              // 材料全给这一档最多能做几瓶（界面上的上限）
        internal int Want = -1;        // 界面上用户自己选的瓶数（-1 = 还没设过）
        internal bool Feasible;        // 材料够不够单独做一瓶
        internal int ScoreMin;
        internal int ScoreMax;
        internal int Days;
        internal BrewCombo Sample;     // 这一档的典型搭配（0 瓶时也用来显示配方）
        internal string Reason;        // 做不了的原因

        internal string Name { get { return Counterfeit ? WineGrade.CounterfeitName : WineGrade.NameOfTier(Tier); } }

        internal string ScoreText
        {
            get
            {
                if (Counterfeit) return "走私";
                if (!Feasible) return "—";
                return ScoreMin == ScoreMax ? ScoreMin + " 分" : ScoreMin + "~" + ScoreMax + " 分";
            }
        }

        internal string RecipeText
        {
            get
            {
                if (Sample == null) return "—";
                string water = Sample.WaterTier >= 0 ? BrewScan.TierName(Sample.WaterTier) : "水";
                return Sample.BerryText + " + " + water;
            }
        }

        internal string DaysText
        {
            get { return Feasible ? Days + " 夜" : "—"; }
        }
    }

    /// <summary>
    /// 按现有材料排出「每种酒各能酿几瓶」。
    ///
    /// 关键：材料是共用的，所以「可酿」这一列是一次分完的结果（各档不能相加）。
    /// 另外每一档还会单独探一次「材料够不够做一瓶」，用来给 0 瓶的行显示配方和可用按钮。
    ///
    /// 两种分法：
    ///   高品质优先：先凑 6 分（普通+优质），再 5/4/3 分，发霉果不参与
    ///   多瓶优先  ：一颗果一瓶，瓶数最多（发霉也算）
    /// 走私酒单独用「合成酵母 + 污染水」，两种分法最后都会顺手把剩下的脏水用掉。
    /// </summary>
    internal static class BrewAllocator
    {
        private static readonly int[] WaterBest = { 1, 0 };      // 优质水、纯水
        private static readonly int[] WaterPlain = { 2 };        // 基础水
        private static readonly int[] WaterDirty = { 5, 4, 3 };  // 沟渠、铁锈、幽灵

        private struct Template
        {
            internal int A;
            internal int B;
            internal int[] WaterTiers;
        }

        /// <summary>高品质优先的尝试顺序（同时也是「单独做这一档」时的优先顺序）。</summary>
        private static readonly Template[] QualityOrder =
        {
            T(BerryGrade.Standard, BerryGrade.Small,    WaterBest),   // 6 分
            T(BerryGrade.Small,    BerryGrade.Small,    WaterPlain),  // 6 分（只剩基础水）
            T(BerryGrade.Small,    BerryGrade.Small,    WaterBest),   // 7 分（只剩优质果）
            T(BerryGrade.Standard, BerryGrade.Standard, WaterBest),   // 5 分
            T(BerryGrade.Small,    BerryGrade.Waste,    WaterBest),   // 5 分
            T(BerryGrade.Small,    -1,                  WaterBest),   // 4 分
            T(BerryGrade.Standard, BerryGrade.Waste,    WaterBest),   // 4 分
            T(BerryGrade.Waste,    BerryGrade.Waste,    WaterBest),   // 3 分
            T(BerryGrade.Standard, -1,                  WaterBest),   // 3 分
            T(BerryGrade.Small,    -1,                  WaterPlain),  // 3 分（只剩基础水）
            T(BerryGrade.Standard, -1,                  WaterPlain),  // 2 分
            T(BerryGrade.Waste,    -1,                  WaterBest),   // 2 分
            T(BerryGrade.Waste,    -1,                  WaterPlain)   // 1 分
        };

        /// <summary>再加上发霉果的两条（只有「单独做这一档」时才会用到）。</summary>
        private static readonly Template[] ProbeOrder = BuildProbeOrder();

        private static Template[] BuildProbeOrder()
        {
            List<Template> list = new List<Template>(QualityOrder);
            list.Add(T(BerryGrade.Mold, -1, WaterBest));    // 1 分
            list.Add(T(BerryGrade.Mold, -1, WaterPlain));   // 0 分
            list.Add(T(BerryGrade.Mold, BerryGrade.Mold, WaterPlain));
            return list.ToArray();
        }

        private static Template T(int a, int b, int[] waters)
        {
            Template t = new Template();
            t.A = a;
            t.B = b;
            t.WaterTiers = waters;
            return t;
        }

        // ---------------------------------------------------------------
        // 对外
        // ---------------------------------------------------------------

        internal static List<BrewTierRow> Plan(BrewStock stock, bool qualityFirst, int yeastPower, out string note)
        {
            note = string.Empty;
            List<BrewTierRow> rows = EmptyRows(yeastPower);

            if (stock == null) { note = "没读到库存。"; return rows; }
            if (stock.EmptyBottles.Count <= 0)
            {
                note = stock.WineBottles.Count > 0 ? "没有空酒瓶（现有的瓶都装着酒）" : "没有空酒瓶";
                for (int i = 0; i < rows.Count; i++) rows[i].Reason = "没有空瓶";
                return rows;
            }

            // 1) 先按分法整体分一遍
            Budget budget = new Budget(stock);
            if (qualityFirst) FillByQuality(rows, budget);
            else FillByCount(rows, budget);

            FillCounterfeit(rows, budget);

            int total = 0;
            for (int i = 0; i < rows.Count; i++) total += rows[i].Count;

            // 2) 每一档都单独探一次：材料全给它最多能做几瓶、配方长什么样
            for (int i = 0; i < rows.Count; i++)
            {
                BrewTierRow row = rows[i];

                BrewCombo sample;
                int max = Probe(stock, row.Tier, row.Counterfeit, out sample);
                row.Max = max;

                if (row.Count > 0) { row.Feasible = true; continue; }

                row.Feasible = max > 0;
                if (row.Feasible)
                {
                    row.Sample = sample;
                    int score = row.Counterfeit ? 3 : BrewScore.Of(sample.Grades, true, sample.WaterTier);
                    row.ScoreMin = score;
                    row.ScoreMax = score;
                    row.Reason = total > 0 ? "材料在别处" : null;
                }
                else
                {
                    row.Reason = ReasonFor(row, stock, total);
                }
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

        /// <summary>单独测算：只用材料做这一档，能做几瓶；sample 给出典型搭配。</summary>
        private static int Probe(BrewStock stock, int tier, bool counterfeit, out BrewCombo sample)
        {
            sample = null;
            Budget b = new Budget(stock);

            if (counterfeit)
            {
                int n = 0;
                while (b.Bottles > 0)
                {
                    Candidate c = PickCounterfeit(b);
                    if (c == null) break;
                    if (sample == null) sample = c.Combo;
                    Consume(b, c);
                    n++;
                }
                return n;
            }

            int count = 0;
            while (b.Bottles > 0)
            {
                Candidate c = PickForTier(b, tier);
                if (c == null) break;
                if (sample == null) sample = c.Combo;
                Consume(b, c);
                count++;
            }
            return count;
        }

        // ---------------------------------------------------------------
        // 材料预算
        // ---------------------------------------------------------------

        private sealed class Budget
        {
            internal int Bottles;
            internal readonly int[] Berry = new int[4];
            internal readonly List<BrewWater> Waters = new List<BrewWater>();
            internal readonly List<BrewYeast> Yeasts = new List<BrewYeast>();

            internal Budget(BrewStock stock)
            {
                Bottles = stock.EmptyBottles.Count;

                for (int i = 0; i < stock.Berries.Count; i++)
                {
                    BrewBerry b = stock.Berries[i];
                    Berry[b.Grade] += b.Count;
                }

                for (int i = 0; i < stock.Waters.Count; i++)
                {
                    BrewWater w = stock.Waters[i];
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
        }

        private sealed class Candidate
        {
            internal BrewCombo Combo;
            internal BrewWater Water;
            internal BrewYeast Yeast;
        }

        // ---------------------------------------------------------------
        // 两种分法
        // ---------------------------------------------------------------

        private static void FillByQuality(List<BrewTierRow> rows, Budget budget)
        {
            while (budget.Bottles > 0)
            {
                Candidate picked = null;
                for (int i = 0; i < QualityOrder.Length && picked == null; i++)
                {
                    if (QualityOrder[i].A < 0) continue;
                    picked = TryPick(budget, QualityOrder[i]);
                }
                if (picked == null) return;

                Consume(budget, picked);
                Accumulate(rows, picked.Combo, BrewScore.Of(picked.Combo.Grades, true, picked.Water.Tier));
            }
        }

        private static void FillByCount(List<BrewTierRow> rows, Budget budget)
        {
            int[] order = { BerryGrade.Small, BerryGrade.Standard, BerryGrade.Waste, BerryGrade.Mold };

            while (budget.Bottles > 0)
            {
                int grade = -1;
                for (int i = 0; i < order.Length; i++)
                {
                    if (budget.Berry[order[i]] > 0) { grade = order[i]; break; }
                }
                if (grade < 0) return;

                BrewWater water = PickWater(budget, WaterBest) ?? PickWater(budget, WaterPlain);
                BrewYeast yeast = PickYeast(budget.Yeasts);
                if (water == null || yeast == null) return;

                BrewCombo combo = new BrewCombo();
                combo.YeastId = yeast.Id;
                combo.WaterTier = water.Tier;
                combo.Grades.Add(grade);

                Candidate c = new Candidate();
                c.Combo = combo;
                c.Water = water;
                c.Yeast = yeast;

                Consume(budget, c);
                Accumulate(rows, combo, BrewScore.Of(combo.Grades, true, water.Tier));
            }
        }

        private static void FillCounterfeit(List<BrewTierRow> rows, Budget budget)
        {
            BrewTierRow row = rows[rows.Count - 1];

            while (budget.Bottles > 0)
            {
                Candidate c = PickCounterfeit(budget);
                if (c == null) return;

                Consume(budget, c);
                if (row.Count == 0) row.Sample = c.Combo;
                row.Count++;
                row.ScoreMin = 3;
                row.ScoreMax = 3;
            }
        }

        // ---------------------------------------------------------------
        // 挑一瓶
        // ---------------------------------------------------------------

        private static Candidate TryPick(Budget b, Template t)
        {
            if (!HasBerries(b, t)) return null;

            BrewWater water = PickWater(b, t.WaterTiers);
            if (water == null) return null;

            BrewYeast yeast = PickYeast(b.Yeasts);
            if (yeast == null) return null;

            BrewCombo combo = new BrewCombo();
            combo.YeastId = yeast.Id;
            combo.WaterTier = water.Tier;
            if (t.A >= 0) combo.Grades.Add(t.A);
            if (t.B >= 0) combo.Grades.Add(t.B);

            Candidate c = new Candidate();
            c.Combo = combo;
            c.Water = water;
            c.Yeast = yeast;
            return c;
        }

        /// <summary>只挑「成品刚好落在这一档」的搭配。</summary>
        private static Candidate PickForTier(Budget b, int tier)
        {
            for (int i = 0; i < ProbeOrder.Length; i++)
            {
                Candidate c = TryPick(b, ProbeOrder[i]);
                if (c == null) continue;

                int score = BrewScore.Of(c.Combo.Grades, true, c.Water.Tier);
                if (WineGrade.TierOf(score) != tier) continue;
                return c;
            }
            return null;
        }

        private static Candidate PickCounterfeit(Budget b)
        {
            BrewYeast yeast = PickSynthetic(b.Yeasts);
            if (yeast == null) return null;

            BrewWater water = PickWater(b, WaterDirty);
            if (water == null) return null;

            BrewCombo combo = new BrewCombo();
            combo.Counterfeit = true;
            combo.YeastId = yeast.Id;
            combo.WaterTier = water.Tier;

            Candidate c = new Candidate();
            c.Combo = combo;
            c.Water = water;
            c.Yeast = yeast;
            return c;
        }

        private static bool HasBerries(Budget b, Template t)
        {
            if (t.A < 0) return true;
            if (b.Berry[t.A] <= 0) return false;
            if (t.B >= 0)
            {
                if (t.B == t.A) return b.Berry[t.A] >= 2;
                return b.Berry[t.B] > 0;
            }
            return true;
        }

        private static void Consume(Budget b, Candidate c)
        {
            b.Bottles--;

            int first = c.Combo.Grades.Count > 0 ? c.Combo.Grades[0] : -1;
            int second = c.Combo.Grades.Count > 1 ? c.Combo.Grades[1] : -1;
            if (first >= 0) b.Berry[first]--;
            if (second >= 0) b.Berry[second]--;

            if (c.Yeast != null && !c.Yeast.Reusable && c.Yeast.Remain > 0) c.Yeast.Remain--;
            if (c.Water != null) c.Water.RemainMl -= BrewScan.WaterPerBottle;
        }

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

        private static BrewYeast PickYeast(List<BrewYeast> list)
        {
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

        private static string ReasonFor(BrewTierRow row, BrewStock stock, int total)
        {
            if (stock.EmptyBottles.Count <= 0) return "没有空瓶";

            if (row.Counterfeit)
            {
                if (!HasSyntheticYeast(stock)) return "缺合成酵母";
                if (!HasDirtyWater(stock)) return "缺污染水";
                return "材料不够";
            }

            if (total > 0) return "材料不够这一档";
            if (stock.BerryTotal() == 0 && stock.BerryCount(BerryGrade.Mold) == 0) return "没有荧光莓";
            if (!HasCleanWater(stock)) return "没有干净水";
            if (stock.Yeasts.Count == 0) return "没有酵母";
            return "凑不出这一档";
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
