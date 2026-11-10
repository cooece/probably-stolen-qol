using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace QoL
{
    /// <summary>荧光莓品相。数值与游戏 BERRY_CONDITION_INT 换算一致。</summary>
    internal static class BerryGrade
    {
        internal const int Small = 0;      // 小果     条件值 >= 2   配方分 +2
        internal const int Standard = 1;   // 标准     条件值 == 1   配方分 +1
        internal const int Waste = 2;      // 未熟/过熟 条件值 >= -1  配方分 0
        internal const int Mold = 3;       // 发霉     条件值 < -1   配方分 -1

        internal static readonly string[] Label = { "小果", "标准", "未熟", "发霉" };

        /// <summary>游戏里每颗果实贡献的分：condition &lt; 0 时先 +1 再累加。</summary>
        internal static int Score(int grade)
        {
            switch (grade)
            {
                case Small: return 2;
                case Standard: return 1;
                case Waste: return 0;
                default: return -1;
            }
        }

        internal static int FromCondition(int cond)
        {
            if (cond >= 2) return Small;
            if (cond == 1) return Standard;
            if (cond >= -1) return Waste;
            return Mold;
        }
    }

    internal sealed class BrewBerry
    {
        internal int Grade;
        internal int Count;
        internal readonly List<GameItem> Items = new List<GameItem>();
    }

    internal sealed class BrewWater
    {
        internal GameItem Item;
        internal string Name;
        internal int Tier;          // 0 纯水 1 优质 2 基础 3 幽灵 4 铁锈 5 沟渠
        internal int Ml;
        internal int RemainMl;      // 规划时递减，判断这桶还够几瓶

        internal bool Dirty { get { return Tier >= 3; } }
    }

    internal sealed class BrewYeast
    {
        internal string Id;
        internal string Name;
        internal int Power;
        internal int Count;
        internal bool Reusable;     // 无限酵母：用掉会退回店里
        internal bool Synthetic;    // 合成酵母：走私酒专用
        internal readonly List<GameItem> Items = new List<GameItem>();
        internal int Remain;        // 规划时递减；可复用的一直有

        internal BrewYeast Clone()
        {
            BrewYeast y = new BrewYeast();
            y.Id = Id;
            y.Name = Name;
            y.Power = Power;
            y.Count = Count;
            y.Reusable = Reusable;
            y.Synthetic = Synthetic;
            y.Remain = Reusable ? 99 : Count;
            for (int i = 0; i < Items.Count; i++) y.Items.Add(Items[i]);
            return y;
        }
    }

    internal sealed class BrewStock
    {
        internal readonly List<GameItem> EmptyBottles = new List<GameItem>();
        internal readonly List<GameItem> BusyBottles = new List<GameItem>();   // 非空但没在酿酒
        internal readonly List<GameItem> WineBottles = new List<GameItem>();   // 发酵中 / 已酿好
        internal readonly List<BrewBerry> Berries = new List<BrewBerry>();
        internal readonly List<BrewWater> Waters = new List<BrewWater>();
        internal readonly List<BrewYeast> Yeasts = new List<BrewYeast>();
        internal readonly List<string> Notes = new List<string>();

        internal int Fermenting;
        internal int Finished;
        internal int SoonestDays;   // 发酵中最快的还要几天
        internal int ItemCount;

        internal BrewBerry Berry(int grade)
        {
            for (int i = 0; i < Berries.Count; i++)
            {
                if (Berries[i].Grade == grade) return Berries[i];
            }
            return null;
        }

        internal int BerryCount(int grade)
        {
            BrewBerry b = Berry(grade);
            return b != null ? b.Count : 0;
        }

        internal int BerryTotal()
        {
            int n = 0;
            for (int i = 0; i < Berries.Count; i++)
            {
                if (Berries[i].Grade != BerryGrade.Mold) n += Berries[i].Count;
            }
            return n;
        }

        /// <summary>某一档水一共有多少毫升。</summary>
        internal int WaterMl(int tier)
        {
            int n = 0;
            for (int i = 0; i < Waters.Count; i++)
            {
                if (Waters[i].Tier == tier) n += Waters[i].Ml;
            }
            return n;
        }

        /// <summary>某一档水最多还能灌几瓶。</summary>
        internal int WaterBottles(int tier)
        {
            return WaterMl(tier) / BrewScan.WaterPerBottle;
        }

        internal BrewYeast Yeast(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < Yeasts.Count; i++)
            {
                if (string.Equals(Yeasts[i].Id, id, StringComparison.Ordinal)) return Yeasts[i];
            }
            return null;
        }
    }

    /// <summary>
    /// 库存扫描。只认玩家自己的东西，并且排除「前厅柜台」——
    /// 那是正在和顾客谈价的区域，从里面抽材料会和交易打架。
    /// </summary>
    internal static class BrewScan
    {
        private const string BottleId = "wine_bottle";
        private const string BerryId = "wine_berry";

        /// <summary>一杯酒要 749ml 以上；瓶子容量 750。</summary>
        internal const int WaterPerBottle = 749;

        /// <summary>
        /// 不能抽水的东西（本体或插在里面的容器都算）：
        /// 鱼缸水箱、水培种植机（水是给生物用的）；
        /// 船载水循环机、饮水机（插在里面的壶/瓶是设备的一部分，抽了影响设备用）。
        /// </summary>
        private static readonly string[] NoDrawWater = { "ras_water_tank", "hydroponic", "water_recycler_system", "water_fountain" };

        private sealed class RefComparer : IEqualityComparer<GameItem>
        {
            internal static readonly RefComparer Instance = new RefComparer();

            public bool Equals(GameItem a, GameItem b) { return ReferenceEquals(a, b); }
            public int GetHashCode(GameItem o) { return RuntimeHelpers.GetHashCode(o); }
        }

        internal static BrewStock Scan()
        {
            BrewStock stock = new BrewStock();

            List<GameItem> flat = new List<GameItem>();
            HashSet<GameItem> seen = new HashSet<GameItem>(RefComparer.Instance);
            CollectAll(flat, seen);
            stock.ItemCount = flat.Count;

            if (flat.Count == 0)
            {
                stock.Notes.Add("没读到库存：请先进入存档、站在店里再打开。");
                return stock;
            }

            Dictionary<int, BrewBerry> berries = new Dictionary<int, BrewBerry>();
            Dictionary<string, BrewYeast> yeasts = new Dictionary<string, BrewYeast>();

            for (int i = 0; i < flat.Count; i++)
            {
                GameItem item = flat[i];
                if (item == null) continue;

                string id = Id(item);
                if (string.IsNullOrEmpty(id)) continue;

                // 地面/顾客身上的东西也会被枚举到，只认玩家自己的
                if (!Owned(item)) continue;

                // 藏在暗格 / 锁在保险箱里的，或者正被玩家拖在手上的，一律不碰
                if (Hidden(item) || InsideHidden(item) || Dragged(item)) continue;

                if (id == BerryId)
                {
                    AddBerry(berries, item);
                    continue;
                }

                if (id == BottleId)
                {
                    AddBottle(stock, item);
                    continue;
                }

                int power = TagInt(item, "WINE_YEAST_POWER_INT", 0);
                if (power > 0)
                {
                    AddYeast(yeasts, item, id, power);
                    continue;
                }

                BrewWater water = TryWater(item, id);
                if (water != null) stock.Waters.Add(water);
            }

            for (int g = 0; g < BerryGrade.Label.Length; g++)
            {
                BrewBerry b;
                if (berries.TryGetValue(g, out b)) stock.Berries.Add(b);
            }

            foreach (KeyValuePair<string, BrewYeast> pair in yeasts) stock.Yeasts.Add(pair.Value);

            stock.Waters.Sort(delegate (BrewWater a, BrewWater b)
            {
                if (a.Tier != b.Tier) return a.Tier.CompareTo(b.Tier);
                return b.Ml.CompareTo(a.Ml);   // 同档先用大桶，少开一个新容器
            });

            stock.Yeasts.Sort(CompareYeast);
            return stock;
        }

        // ---------------------------------------------------------------
        // 枚举
        // ---------------------------------------------------------------

        private static void CollectAll(List<GameItem> flat, HashSet<GameItem> seen)
        {
            List<GameItem> roots = new List<GameItem>();

            try
            {
                if (PlayerStore.IsInstanceExist() && PlayerStore.Instance != null)
                {
                    List<GameItem> owned = PlayerStore.Instance.FindAllItem(true);
                    if (owned != null) roots.AddRange(owned);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 读店内物品失败：" + e.GetType().Name);
            }

            try
            {
                GameMaster master = GameMaster.current;
                if (master != null && master.raidManager != null)
                {
                    List<GameItem> floor = master.raidManager.GetAllGameItem();
                    if (floor != null) roots.AddRange(floor);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 读地面机器失败：" + e.GetType().Name);
            }

            for (int i = 0; i < roots.Count; i++) Collect(roots[i], flat, seen, 0);
        }

        private static void Collect(GameItem item, List<GameItem> flat, HashSet<GameItem> seen, int depth)
        {
            if (item == null || depth > 4) return;
            if (!seen.Add(item)) return;
            if (InFrontCounter(item)) return;      // 前厅柜台整棵子树都不碰

            flat.Add(item);

            List<GameItem> children = null;
            try { children = item.FindAllChildItems(); }
            catch { }

            if (children == null) return;
            for (int i = 0; i < children.Count; i++) Collect(children[i], flat, seen, depth + 1);
        }

        /// <summary>物品是不是摆在前厅柜台（含柜台上的容器内部）。</summary>
        private static bool InFrontCounter(GameItem item)
        {
            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                if (store == null || store.frontInvinvElement == null) return false;

                GameInventory inv = item.parentInventory;
                for (int guard = 0; inv != null && guard < 8; guard++)
                {
                    if (ReferenceEquals(inv, store.frontInvinvElement)) return true;
                    GameItem parent = inv.GetParentItem();
                    if (parent == null) return false;
                    inv = parent.parentInventory;
                }
            }
            catch { }
            return false;
        }

        // ---------------------------------------------------------------
        // 分类
        // ---------------------------------------------------------------

        private static void AddBerry(Dictionary<int, BrewBerry> map, GameItem item)
        {
            int cond = TagInt(item, "BERRY_CONDITION_INT", 1);
            int grade = BerryGrade.FromCondition(cond);

            BrewBerry stack;
            if (!map.TryGetValue(grade, out stack))
            {
                stack = new BrewBerry();
                stack.Grade = grade;
                map[grade] = stack;
            }
            stack.Count++;
            stack.Items.Add(item);
        }

        private static void AddBottle(BrewStock stock, GameItem item)
        {
            bool wine = false;
            try { wine = WineHelper.IsFermentingOrFinished(item) || WineHelper.IsFinishedWine(item); }
            catch { }

            if (wine)
            {
                stock.WineBottles.Add(item);
                int state = TagIntOn(item, "WINE_CONTAINER_STATE");
                if (state == 1)
                {
                    stock.Fermenting++;
                    int days = TagIntOn(item, "FERMENTATION_TIME_REMAINING_INT");
                    if (days > 0 && (stock.SoonestDays == 0 || days < stock.SoonestDays)) stock.SoonestDays = days;
                }
                else stock.Finished++;
                return;
            }

            if (IsBottleEmpty(item)) stock.EmptyBottles.Add(item);
            else stock.BusyBottles.Add(item);
        }

        private static void AddYeast(Dictionary<string, BrewYeast> map, GameItem item, string id, int power)
        {
            BrewYeast stack;
            if (!map.TryGetValue(id, out stack))
            {
                stack = new BrewYeast();
                stack.Id = id;
                stack.Name = Display(item);
                stack.Power = power;
                stack.Reusable = item.IsTag("WINE_YEAST_REUSABLE_TAG");
                stack.Synthetic = item.IsTag("WINE_YEAST_ILLEGAL_TAG");
                stack.Remain = 0;
                map[id] = stack;
            }
            stack.Count++;
            stack.Items.Add(item);
        }

        private static BrewWater TryWater(GameItem item, string id)
        {
            if (IsNoDrawWater(id, item) || InsideNoDrawWater(item)) return null;

            bool canUse = false;
            try { canUse = WaterHelper.CanUseWaterContainer(item); }
            catch { }
            if (!canUse) return null;

            int ml = 0;
            try { ml = WaterHelper.GetCurrentCapacityML(item); }
            catch { }
            if (ml < WaterPerBottle) return null;

            int purity = 0;
            try { purity = WaterHelper.GetWaterPurity(item); }
            catch { }
            if (purity <= 0) return null;

            int tier = -1;
            try { tier = WaterFeatureHelper.GetPurityArrayIndex(purity); }
            catch { }
            if (tier < 0 || tier > 5) return null;

            BrewWater water = new BrewWater();
            water.Item = item;
            water.Tier = tier;
            water.Ml = ml;
            water.RemainMl = ml;

            string name = null;
            try { name = WaterFeatureHelper.GetWaterDisplayName(item); }
            catch { }
            water.Name = string.IsNullOrEmpty(name) ? TierName(tier) : name;
            return water;
        }

        /// <summary>船载水循环机 / 饮水机没设 identifier，只能按内部名认（当前版本未实装，防御性排除）。</summary>
        private static readonly string[] NoDrawNames =
        {
            "Water Recycler System", "Water Fountain", "Paying Water Fountain"
        };

        private static bool IsNoDrawWater(string id, GameItem item)
        {
            for (int i = 0; i < NoDrawWater.Length; i++)
            {
                if (string.Equals(id, NoDrawWater[i], StringComparison.Ordinal)) return true;
            }

            try
            {
                string name = item != null ? item.name : null;
                if (!string.IsNullOrEmpty(name))
                {
                    for (int i = 0; i < NoDrawNames.Length; i++)
                    {
                        if (string.Equals(name, NoDrawNames[i], StringComparison.Ordinal)) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool InsideNoDrawWater(GameItem item)
        {
            try
            {
                GameInventory inv = item.parentInventory;
                for (int guard = 0; inv != null && guard < 8; guard++)
                {
                    GameItem parent = inv.GetParentItem();
                    if (parent == null) return false;
                    if (IsNoDrawWater(Id(parent), parent)) return true;
                    inv = parent.parentInventory;
                }
            }
            catch { }
            return false;
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        internal static string Id(GameItem item)
        {
            try { return item != null ? item.identifier : null; }
            catch { return null; }
        }

        internal static bool Owned(GameItem item)
        {
            try { return item != null && item.IsTag("IS_OWNED_TAG"); }
            catch { return false; }
        }

        /// <summary>走私暗格、上锁保险箱里的东西：玩家特意藏起来的，自动化别去动。</summary>
        private static bool Hidden(GameItem item)
        {
            try
            {
                if (item == null) return false;
                return item.IsTag("ITEM_HIDDEN_TAG") || item.IsTag("ITEM_BEHIND_LOCK_TAG");
            }
            catch { return false; }
        }

        /// <summary>东西装在容器里时，容器本身在暗格/锁箱里也算（查祖先链，最多 8 层）。</summary>
        private static bool InsideHidden(GameItem item)
        {
            try
            {
                GameInventory inv = item.parentInventory;
                for (int guard = 0; inv != null && guard < 8; guard++)
                {
                    GameItem parent = inv.GetParentItem();
                    if (parent == null) return false;
                    if (Hidden(parent)) return true;
                    inv = parent.parentInventory;
                }
            }
            catch { }
            return false;
        }

        /// <summary>正被玩家拖在手上的那一件：这时候去搬它会和拖拽打架。</summary>
        private static bool Dragged(GameItem item)
        {
            try
            {
                ItemMouseDragHandler drag = ItemMouseDragHandler.current;
                return drag != null && drag.IsDraggingItem && drag.GetCurrentItem() == item;
            }
            catch { return false; }
        }

        internal static string Display(GameItem item)
        {
            try { return item != null ? item.GetDisplayName(false) : "?"; }
            catch { return "?"; }
        }

        internal static int TagInt(GameItem item, string key, int fallback)
        {
            try
            {
                if (item == null) return fallback;
                TagState tag = item.GetTagReadonly(key);
                return tag != null ? tag.valueInt : fallback;
            }
            catch { return fallback; }
        }

        /// <summary>标签必须真的启用了才读，否则一律当 0（未启用的标签残留旧值）。</summary>
        internal static int TagIntOn(GameItem item, string key)
        {
            try
            {
                if (item == null) return 0;
                TagState tag = item.GetTagReadonly(key);
                if (tag == null || !tag.IsEnabled()) return 0;
                return tag.valueInt;
            }
            catch { return 0; }
        }

        internal static int Ml(GameItem item)
        {
            try { return WaterHelper.GetCurrentCapacityML(item); }
            catch { return 0; }
        }

        internal static int ChildCount(GameItem item)
        {
            try
            {
                if (item == null) return -1;
                List<GameItem> list = item.FindAllChildItems();
                return list != null ? list.Count : 0;
            }
            catch { return -1; }
        }

        internal static bool IsBottleEmpty(GameItem item)
        {
            if (item == null || Id(item) != BottleId) return false;

            try { if (!WaterHelper.IsEmpty(item)) return false; }
            catch { return false; }

            if (ChildCount(item) != 0) return false;

            try
            {
                if (WineHelper.IsFermentingOrFinished(item) || WineHelper.IsFinishedWine(item)) return false;
            }
            catch { }

            return TagIntOn(item, "WINE_CONTAINER_STATE") == 0;
        }

        internal static string TierName(int tier)
        {
            switch (tier)
            {
                case 0: return "纯水";
                case 1: return "优质水";
                case 2: return "基础水";
                case 3: return "幽灵水";
                case 4: return "铁锈水";
                case 5: return "沟渠水";
                default: return "未知水";
            }
        }

        /// <summary>与游戏 WATER_QUALITY_SCORE 一致：纯/优 +1，基础 0，之后依次 -1/-2/-3。</summary>
        internal static int WaterScore(int tier)
        {
            switch (tier)
            {
                case 0: return 1;
                case 1: return 1;
                case 2: return 0;
                case 3: return -1;
                case 4: return -2;
                case 5: return -3;
                default: return 0;
            }
        }

        private static int CompareYeast(BrewYeast a, BrewYeast b)
        {
            if (a.Reusable != b.Reusable) return a.Reusable ? -1 : 1;
            if (a.Synthetic != b.Synthetic) return a.Synthetic ? 1 : -1;
            if (a.Power != b.Power) return a.Power.CompareTo(b.Power);
            return string.CompareOrdinal(a.Id, b.Id);
        }
    }
}
