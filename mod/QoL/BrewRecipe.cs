using System;
using System.Collections.Generic;
using System.Text;

namespace QoL
{
    /// <summary>酒的档位：名字直接取游戏本地化，价格按游戏公式（基础 100 + 品质修正）估算。</summary>
    internal static class WineGrade
    {
        internal const string CounterfeitName = "阴光莓果酿";
        internal const int CounterfeitPrice = 44;

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

        internal static string NameOf(int score)
        {
            string[] n = Names;
            if (score >= 6) return n[0];
            if (score == 5) return n[1];
            if (score >= 3) return n[2];
            if (score == 2) return n[3];
            if (score == 1) return n[4];
            return n[5];
        }

        internal static int PriceOf(int score)
        {
            if (score >= 6) return 175;
            if (score == 5) return 150;
            if (score >= 3) return 125;
            if (score == 2) return 100;
            if (score == 1) return 85;
            return 0;
        }

        /// <summary>列表里的分数颜色：越高越显眼。</summary>
        internal static UnityEngine.Color ColorOf(int score)
        {
            if (score >= 6) return new UnityEngine.Color(0.925f, 0.78f, 0.35f);   // 金
            if (score == 5) return new UnityEngine.Color(0.75f, 0.60f, 0.95f);    // 紫
            if (score >= 3) return new UnityEngine.Color(0.55f, 0.78f, 0.95f);    // 蓝
            return new UnityEngine.Color(0.72f, 0.71f, 0.68f);                    // 灰
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
        internal int Price { get { return Counterfeit ? WineGrade.CounterfeitPrice : WineGrade.PriceOf(Score); } }
        internal string WaterName { get { return Water != null ? Water.Name : "?"; } }
        internal string YeastName { get { return Yeast != null ? Yeast.Name : "无酵母"; } }
    }

    internal sealed class BrewGroup
    {
        internal string Key;
        internal BrewPlan Sample;
        internal int Count;
        internal int TotalPrice { get { return Sample != null ? Sample.Price * Count : 0; } }
    }

    /// <summary>
    /// 配方与推荐。
    ///
    /// 配方表来自游戏真实公式（WineHelper.StartFermentation）：
    ///   分数 = Σ 果实品相（小果 2 / 标准 1 / 未熟 0 / 发霉 -1）
    ///        + 双果 1 + 有酵母 1 + 水质修正（纯·优 +1 / 基础 0 / 幽灵 -1 / 铁锈 -2 / 沟渠 -3）
    /// 所以上限 7 分（小果×2 + 优纯水 + 酵母）。
    /// </summary>
    internal static class BrewPlanner
    {
        private static readonly int[] WaterBest = { 1, 0 };      // 优质水、纯水
        private static readonly int[] WaterPlain = { 2 };        // 基础水
        private static readonly int[] WaterDirty = { 5, 4, 3 };  // 沟渠、铁锈、幽灵

        private static readonly BrewRecipe[] Table =
        {
            Make("7a", BerryGrade.Small,    BerryGrade.Small,    WaterBest,  7),
            Make("6a", BerryGrade.Standard, BerryGrade.Small,    WaterBest,  6),
            Make("6b", BerryGrade.Small,    BerryGrade.Small,    WaterPlain, 6),
            Make("5a", BerryGrade.Standard, BerryGrade.Standard, WaterBest,  5),
            Make("5b", BerryGrade.Small,    BerryGrade.Waste,    WaterBest,  5),
            Make("4a", BerryGrade.Small,    -1,                  WaterBest,  4),
            Make("4b", BerryGrade.Standard, BerryGrade.Waste,    WaterBest,  4),
            Make("3a", BerryGrade.Waste,    BerryGrade.Waste,    WaterBest,  3),
            Make("3b", BerryGrade.Standard, -1,                  WaterBest,  3),
            Make("3c", BerryGrade.Small,    -1,                  WaterPlain, 3),
            Make("X1", -1,                  -1,                  WaterDirty, 3, true)
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

        /// <summary>酿造顺序：先榨干高分配方，再降档。同一档里小果先配标准果，能省出小果再冲 7 分。</summary>
        private static readonly string[][] Phases =
        {
            new[] { "6a", "6b" },   // 备用：7a（两个小果 + 好水）
            new[] { "5a", "5b" },
            new[] { "4a", "4b" },
            new[] { "3a", "3b", "3c" },
            new[] { "X1" }
        };

        private static readonly string[] PhaseFirstFallback = { "7a", null, null, null, null };

        /// <summary>本次规划用的预算：瓶、果、水、酵母都会被扣减。</summary>
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
                    if (b.Grade != BerryGrade.Mold) Berry[b.Grade] += b.Count;
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

        /// <summary>
        /// 出推荐方案。note 里是「一共几瓶、合计多少」或「为什么一瓶都酿不了」。
        /// </summary>
        internal static List<BrewPlan> Recommend(BrewStock stock, string preferredYeast, out string note)
        {
            List<BrewPlan> plans = new List<BrewPlan>();
            note = string.Empty;

            if (stock == null)
            {
                note = "没读到库存。";
                return plans;
            }

            Budget budget = new Budget(stock);
            if (budget.Bottles <= 0)
            {
                note = stock.WineBottles.Count > 0
                    ? "没有空酒瓶了（已有的酒瓶都装着酒）。"
                    : "没有空酒瓶，先买或做几个。";
                return plans;
            }

            for (int i = 0; i < Phases.Length; i++)
            {
                string fallback = PhaseFirstFallback[i];
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
                note = Explain(stock);
                return plans;
            }

            int total = 0;
            int fake = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                total += plans[i].Price;
                if (plans[i].Counterfeit) fake++;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("可酿 ").Append(plans.Count).Append(" 瓶 · 约 ").Append(total).Append(" 信用点");
            if (fake > 0) sb.Append("（含走私酒 ").Append(fake).Append(" 瓶）");
            note = sb.ToString();
            return plans;
        }

        private static string Explain(BrewStock stock)
        {
            int berries = stock.BerryTotal();
            bool cleanWater = false;
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                if (stock.Waters[i].Tier <= 2) { cleanWater = true; break; }
            }

            if (berries == 0 && stock.BerryCount(BerryGrade.Mold) > 0) return "荧光莓都发霉了，酿不出好酒。";
            if (berries == 0) return "没有荧光莓（发霉的不算）。";
            if (stock.Yeasts.Count == 0) return "没有酵母 —— 缺了酵母会掉档，先补酵母。";
            if (!cleanWater) return "没有干净的水（要有 ≥749ml 的纯水/优质水/基础水）。";
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

        /// <summary>酵母：优先玩家指定的，其次无限酵母 → 超级酵母 → 普通酵母，最后随便挑力量最高的。</summary>
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

        /// <summary>相邻且配方/酵母/水完全一样的行合并成一条「酿 ×N」。</summary>
        internal static List<BrewGroup> Group(List<BrewPlan> plans)
        {
            List<BrewGroup> groups = new List<BrewGroup>();
            if (plans == null) return groups;

            for (int i = 0; i < plans.Count; i++)
            {
                BrewPlan plan = plans[i];
                string key = plan.Recipe.Id + "|" + (plan.Yeast != null ? plan.Yeast.Id : "-") + "|" + plan.WaterName;

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
