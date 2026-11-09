using System;
using System.Collections.Generic;
using System.Text;

namespace QoL
{
    /// <summary>
    /// 酒的档位（对应游戏 ItemConditionList 里的酒品品质）。
    /// 名字取游戏本地化，价格按游戏公式估算。
    /// </summary>
    internal static class WineGrade
    {
        internal const string CounterfeitName = "阴光莓果酿";
        internal const int CounterfeitPrice = 44;

        /// <summary>游戏里 wineScore &gt;= 6 就封顶在同一档，再多分也不会更值钱。</summary>
        internal const int TopScore = 6;

        private static string[] _names;

        private static string[] Names
        {
            get
            {
                if (_names != null) return _names;

                _names = new string[6];
                _names[0] = Loc("mech_condition_wine_master_vintage", "臻品陈酿");   // >= 6
                _names[1] = Loc("mech_condition_wine_reserve", "珍藏");              // 5
                _names[2] = Loc("mech_condition_wine_production", "量产酒液");        // 3~4
                _names[3] = Loc("mech_condition_wine_homebrew", "自酿");             // 2
                _names[4] = Loc("mech_condition_wine_bucket_brew", "桶酿");          // 1
                _names[5] = Loc("mech_condition_wine_undrinkable", "难以下咽");       // 0
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

        /// <summary>品质档：5 = 臻品，4 = 珍藏，3 = 量产，2 = 自酿，1 = 桶酿，0 = 不能喝。</summary>
        internal static int TierOf(int score)
        {
            if (score >= 6) return 5;
            if (score == 5) return 4;
            if (score >= 3) return 3;
            if (score == 2) return 2;
            if (score == 1) return 1;
            return 0;
        }

        internal static string NameOf(int score)
        {
            string[] n = Names;
            switch (TierOf(score))
            {
                case 5: return n[0];
                case 4: return n[1];
                case 3: return n[2];
                case 2: return n[3];
                case 1: return n[4];
                default: return n[5];
            }
        }

        internal static int PriceOf(int score)
        {
            switch (TierOf(score))
            {
                case 5: return 175;
                case 4: return 150;
                case 3: return 125;
                case 2: return 100;
                case 1: return 85;
                default: return 0;
            }
        }

        /// <summary>列表里的分数颜色：越高越显眼。</summary>
        internal static UnityEngine.Color ColorOf(int score)
        {
            switch (TierOf(score))
            {
                case 5: return new UnityEngine.Color(0.925f, 0.78f, 0.35f);   // 金
                case 4: return new UnityEngine.Color(0.75f, 0.60f, 0.95f);    // 紫
                case 3: return new UnityEngine.Color(0.55f, 0.78f, 0.95f);    // 蓝
                default: return new UnityEngine.Color(0.72f, 0.71f, 0.68f);   // 灰
            }
        }
    }

    /// <summary>
    /// 评分。完全照抄游戏 WineHelper.StartFermentation：
    ///   分数 = Σ 每颗果实的品相（优质 +2 / 普通 +1 / 干瘪 0 / 发霉 -1）
    ///        + 放了两颗以上再 +1
    ///        + 有酵母 +1
    ///        + 水质修正（纯·优 +1 / 基础 0 / 幽灵 -1 / 铁锈 -2 / 沟渠 -3）
    /// 酵母种类**不影响分数**，只影响发酵要几夜（力量 2 = 两夜，4 = 一夜）。
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
    }

    internal sealed class BrewRecipe
    {
        internal string Id;
        internal int BerryA = -1;
        internal int BerryB = -1;
        internal int[] Tiers;
        internal int Score;
        internal bool Counterfeit;

        internal bool HasBerry { get { return BerryA >= 0; } }
        internal bool TwoBerry { get { return BerryA >= 0 && BerryB >= 0; } }

        internal string BerryText
        {
            get
            {
                if (!HasBerry) return "不加果实";
                if (!TwoBerry) return BerryGrade.Label[BerryA];
                if (BerryA == BerryB) return BerryGrade.Label[BerryA] + "×2";
                return BerryGrade.Label[BerryA] + "+" + BerryGrade.Label[BerryB];
            }
        }
    }

    internal sealed class BrewPlan
    {
        internal BrewRecipe Recipe;
        internal BrewYeast Yeast;
        internal BrewWater Water;
        internal int Score;

        internal bool Counterfeit { get { return Recipe != null && Recipe.Counterfeit; } }
        internal string WineName { get { return Counterfeit ? WineGrade.CounterfeitName : WineGrade.NameOf(Score); } }
        internal string YeastName { get { return Yeast != null ? Yeast.Name : "无酵母"; } }
        internal string WaterName { get { return Water != null ? Water.Name : "?"; } }

        internal BrewCombo ToCombo()
        {
            BrewCombo combo = new BrewCombo();
            combo.Counterfeit = Counterfeit;
            combo.YeastId = Yeast != null ? Yeast.Id : string.Empty;
            combo.WaterTier = Water != null ? Water.Tier : -1;

            if (!Counterfeit && Recipe != null)
            {
                if (Recipe.BerryA >= 0) combo.Grades.Add(Recipe.BerryA);
                if (Recipe.TwoBerry) combo.Grades.Add(Recipe.BerryB);
            }
            return combo;
        }
    }

    internal sealed class BrewGroup
    {
        internal string Key;
        internal BrewPlan Sample;
        internal int Count;
    }

    /// <summary>
    /// 配方与推荐。
    /// 顺序原则：先榨高分配方（6 分档先配「普通+优质」，能省下一颗优质果再冲 7 分），
    /// 再逐级降档，最后才用脏水 + 合成酵母做走私酒。
    /// </summary>
    internal static class BrewPlanner
    {
        private static readonly int[] WaterBest = { 1, 0 };      // 优质水、纯水
        private static readonly int[] WaterPlain = { 2 };        // 基础水
        private static readonly int[] WaterDirty = { 5, 4, 3 };  // 沟渠、铁锈、幽灵

        private static readonly BrewRecipe[] Table =
        {
            Make("7",  BerryGrade.Small,    BerryGrade.Small,    WaterBest,  7),
            Make("6a", BerryGrade.Standard, BerryGrade.Small,    WaterBest,  6),
            Make("6b", BerryGrade.Small,    BerryGrade.Small,    WaterPlain, 6),
            Make("5a", BerryGrade.Standard, BerryGrade.Standard, WaterBest,  5),
            Make("5b", BerryGrade.Small,    BerryGrade.Waste,    WaterBest,  5),
            Make("4a", BerryGrade.Small,    -1,                  WaterBest,  4),
            Make("4b", BerryGrade.Standard, BerryGrade.Waste,    WaterBest,  4),
            Make("3a", BerryGrade.Waste,    BerryGrade.Waste,    WaterBest,  3),
            Make("3b", BerryGrade.Standard, -1,                  WaterBest,  3),
            Make("3c", BerryGrade.Small,    -1,                  WaterPlain, 3),
            Make("X",  -1,                  -1,                  WaterDirty, 3, true)
        };

        private static BrewRecipe Make(string id, int a, int b, int[] tiers, int score, bool fake = false)
        {
            BrewRecipe r = new BrewRecipe();
            r.Id = id;
            r.BerryA = a;
            r.BerryB = b;
            r.Tiers = tiers;
            r.Score = score;
            r.Counterfeit = fake;
            return r;
        }

        private static readonly string[][] Phases =
        {
            new[] { "6a", "6b" },   // 备用：7（两颗优质）
            new[] { "5a", "5b" },
            new[] { "4a", "4b" },
            new[] { "3a", "3b", "3c" },
            new[] { "X" }
        };

        private static readonly string[] PhaseFallback = { "7", null, null, null, null };

        private sealed class Budget
        {
            internal int Bottles;
            internal readonly int[] Berry = new int[4];
            internal readonly List<BrewWater> Waters = new List<BrewWater>();
            internal readonly List<BrewYeast> Yeasts = new List<BrewYeast>();

            internal Budget(BrewStock stock, int tierFilter)
            {
                Bottles = stock.EmptyBottles.Count;

                for (int i = 0; i < stock.Berries.Count; i++)
                {
                    BrewBerry b = stock.Berries[i];
                    if (b.Grade != BerryGrade.Mold) Berry[b.Grade] += b.Count;
                }

                for (int i = 0; i < stock.Waters.Count; i++)
                {
                    BrewWater w = stock.Waters[i];
                    if (tierFilter >= 0 && w.Tier != tierFilter) continue;

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

        /// <summary>
        /// 出推荐方案。waterTier &gt;= 0 时只用水质等于它的水（玩家手动挑了水质）。
        /// </summary>
        internal static List<BrewPlan> Recommend(BrewStock stock, string preferredYeast, int waterTier, out string note)
        {
            List<BrewPlan> plans = new List<BrewPlan>();
            note = string.Empty;

            if (stock == null)
            {
                note = "没读到库存。";
                return plans;
            }

            if (stock.EmptyBottles.Count <= 0)
            {
                note = stock.WineBottles.Count > 0
                    ? "没有空酒瓶了（现有的瓶都装着酒）。"
                    : "没有空酒瓶，先弄几个。";
                return plans;
            }

            Budget budget = new Budget(stock, waterTier);

            for (int i = 0; i < Phases.Length; i++)
            {
                string fallback = PhaseFallback[i];
                while (true)
                {
                    BrewPlan plan = FirstFit(budget, Phases[i], preferredYeast);
                    if (plan == null && fallback != null) plan = FirstFit(budget, new[] { fallback }, preferredYeast);
                    if (plan == null) break;

                    Consume(budget, plan);
                    plans.Add(plan);
                }
            }

            if (plans.Count == 0)
            {
                note = Explain(stock, waterTier);
                return plans;
            }

            int fake = 0;
            for (int i = 0; i < plans.Count; i++) if (plans[i].Counterfeit) fake++;

            StringBuilder sb = new StringBuilder();
            sb.Append("可酿 ").Append(plans.Count).Append(" 瓶");
            if (fake > 0) sb.Append("（含走私 ").Append(fake).Append(" 瓶）");
            note = sb.ToString();
            return plans;
        }

        private static string Explain(BrewStock stock, int waterTier)
        {
            int berries = stock.BerryTotal();
            if (berries == 0 && stock.BerryCount(BerryGrade.Mold) > 0) return "荧光莓全发霉了。";
            if (berries == 0) return "没有荧光莓（发霉的不算）。";
            if (stock.Yeasts.Count == 0) return "没有酵母：缺了酵母会掉一档。";
            if (stock.Waters.Count == 0) return "没有够一瓶（≥749ml）的水。";
            if (waterTier >= 0) return "你选的水质凑不出配方，换一种水质试试。";
            return "现有材料凑不出 3 分以上的配方。";
        }

        private static BrewPlan FirstFit(Budget budget, string[] ids, string preferredYeast)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                BrewRecipe recipe = Find(ids[i]);
                if (recipe == null) continue;
                BrewPlan plan = TryMake(budget, recipe, preferredYeast);
                if (plan != null) return plan;
            }
            return null;
        }

        private static BrewRecipe Find(string id)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Id == id) return Table[i];
            }
            return null;
        }

        private static BrewPlan TryMake(Budget budget, BrewRecipe recipe, string preferredYeast)
        {
            if (recipe.HasBerry)
            {
                if (budget.Berry[recipe.BerryA] <= 0) return null;
                if (recipe.TwoBerry)
                {
                    if (recipe.BerryB == recipe.BerryA)
                    {
                        if (budget.Berry[recipe.BerryA] < 2) return null;
                    }
                    else if (budget.Berry[recipe.BerryB] <= 0) return null;
                }
            }

            if (budget.Bottles <= 0) return null;

            BrewWater water = PickWater(budget, recipe.Tiers);
            if (water == null) return null;

            BrewYeast yeast = recipe.Counterfeit ? PickSynthetic(budget.Yeasts) : PickYeast(budget.Yeasts, preferredYeast);
            if (yeast == null) return null;

            BrewPlan plan = new BrewPlan();
            plan.Recipe = recipe;
            plan.Water = water;
            plan.Yeast = yeast;
            plan.Score = recipe.Score;
            return plan;
        }

        private static void Consume(Budget budget, BrewPlan plan)
        {
            budget.Bottles--;

            if (plan.Recipe.HasBerry)
            {
                budget.Berry[plan.Recipe.BerryA]--;
                if (plan.Recipe.TwoBerry) budget.Berry[plan.Recipe.BerryB]--;
            }

            if (plan.Yeast != null && !plan.Yeast.Reusable && plan.Yeast.Remain > 0) plan.Yeast.Remain--;
            if (plan.Water != null) plan.Water.RemainMl -= BrewScan.WaterPerBottle;
        }

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

        internal static List<BrewGroup> Group(List<BrewPlan> plans)
        {
            List<BrewGroup> groups = new List<BrewGroup>();
            if (plans == null) return groups;

            for (int i = 0; i < plans.Count; i++)
            {
                BrewPlan plan = plans[i];
                string key = plan.Recipe.Id + "|" + (plan.Yeast != null ? plan.Yeast.Id : "-") +
                             "|" + (plan.Water != null ? plan.Water.Name : "-");

                if (groups.Count > 0 && groups[groups.Count - 1].Key == key)
                {
                    groups[groups.Count - 1].Count++;
                    continue;
                }

                BrewGroup group = new BrewGroup();
                group.Key = key;
                group.Sample = plan;
                group.Count = 1;
                groups.Add(group);
            }
            return groups;
        }
    }
}
