using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace QoL
{
    // ---------------------------------------------------------------
    // 需求高亮的核心遍历判定
    // 移植自开源 MOD「Demand Highlight」(Apache 2.0, KCN-judu)
    // ---------------------------------------------------------------

    internal static class DemandRules
    {
        private struct Up
        {
            internal DemandAgg Agg;
            internal bool Bought;
            internal bool AllBought;
            internal bool Complete;
        }

        private sealed class Entry
        {
            internal int Epoch = -1;
            internal DemandFacts F;
            internal int InsideEpoch = -1;
            internal DemandAgg Inside;
            internal bool InsideAllBought;
            internal int Walk;
            internal Up Last;
            internal DemandStyle Style;
        }

        internal struct Hit
        {
            internal GameItemElement Element;
            internal DemandStyle Style;
        }

        internal sealed class TabsHit
        {
            internal MachineryTabs Tabs;
            internal readonly List<GameInventory> Pages = new List<GameInventory>();
        }

        private const int MaxDepth = 16;
        private const double Budget = 2.5;

        private static StoreClient client;
        private static DemandKind kind;
        private static bool clientGoods;

        private static readonly Dictionary<GameItem, Entry> entries = new Dictionary<GameItem, Entry>();
        private static int epoch;
        private static StoreClient epochClient;
        private static DemandKind epochKind;
        private static bool epochGoods;
        private static int walk;
        private static readonly Stopwatch clock = new Stopwatch();
        private static bool outOfTime;
        private static int sinceCheck;

        internal static readonly List<Hit> Hits = new List<Hit>();
        internal static readonly List<TabsHit> Tabs = new List<TabsHit>();
        private static readonly List<TabsHit> tabsPool = new List<TabsHit>();
        private static readonly HashSet<object> nodesSeen = new HashSet<object>();
        private static readonly HashSet<string> keysBefore = new HashSet<string>();
        private static readonly List<string> keysAdded = new List<string>();

        internal static int Visited;
        internal static int Computed;

        internal static void Forget(GameItem item)
        {
            if (item == null)
            {
                return;
            }

            if (entries.TryGetValue(item, out Entry value))
            {
                value.Epoch = -1;
                value.InsideEpoch = -1;
            }

            ForgetAbove(item.parentInventory);
        }

        private static void ForgetAbove(GraphNodeStorage node)
        {
            GameItem item = node as GameItem;
            if (item == null)
            {
                GameInventory inv = node as GameInventory;
                item = inv != null ? inv.GetParentItem() : null;
            }

            int n = 0;
            while (item != null && n < MaxDepth)
            {
                if (entries.TryGetValue(item, out Entry value))
                {
                    value.InsideEpoch = -1;
                }

                GameInventory parent = item.parentInventory;
                item = parent != null ? parent.GetParentItem() : null;
                n++;
            }
        }

        private static void ForgetAll()
        {
            epoch++;
            if (entries.Count > 4 * Visited + 4096)
            {
                entries.Clear();
            }
        }

        internal static void Release()
        {
            entries.Clear();
            client = epochClient = null;
            Hits.Clear();
            Tabs.Clear();

            for (int i = 0; i < tabsPool.Count; i++)
            {
                tabsPool[i].Tabs = null;
                tabsPool[i].Pages.Clear();
            }

            nodesSeen.Clear();
        }

        internal static StoreClient CurrentClient()
        {
            if (EmporiumEntry.Instance == null)
            {
                return null;
            }

            PlayerStore store = PlayerStore.Instance;
            if (store == null || store.currentClientInstance == null)
            {
                return null;
            }

            return store.currentClientInstance.GetClientBlueprint();
        }

        internal static int Intent(StoreClient c)
        {
            if (c == null)
            {
                return 0;
            }

            return 1
                | ((int)c.clientIntent == 4 ? 2 : 0)
                | (c.IsClientBuying() ? 4 : 0)
                | (c.IsClientSelling() ? 8 : 0);
        }

        internal static bool Walk(StoreClient current, bool highlightClientGoods, out bool complete)
        {
            complete = true;
            Hits.Clear();
            Tabs.Clear();
            nodesSeen.Clear();
            Visited = Computed = 0;
            walk++;
            client = current;
            clientGoods = highlightClientGoods;

            EmporiumEntry instance = EmporiumEntry.Instance;
            if (instance == null)
            {
                return false;
            }

            if (client == null)
            {
                return false;
            }

            kind.Inspector = (int)client.clientIntent == 4;
            kind.Buying = client.IsClientBuying();
            kind.Selling = client.IsClientSelling();

            if (!kind.Inspector && !kind.Buying && (!kind.Selling || !clientGoods))
            {
                return false;
            }

            if (client != epochClient || kind.Inspector != epochKind.Inspector ||
                kind.Buying != epochKind.Buying || kind.Selling != epochKind.Selling ||
                clientGoods != epochGoods)
            {
                ForgetAll();
                epochClient = client;
                epochKind = kind;
                epochGoods = clientGoods;
            }

            outOfTime = false;
            sinceCheck = 0;
            clock.Reset();
            clock.Start();

            complete &= Root(instance.invElement);
            complete &= Root(instance.frontInvinvElement);
            complete &= Root(instance.backInvinvElement);
            complete &= Root(instance.backInvinvElementCounter);
            complete &= Root(instance.showcaseElement);
            complete &= Root(instance.bazarLeftinvElement);
            complete &= Root(instance.hiddenElement);
            complete &= Root(instance.soldElement);

            clock.Stop();
            return true;
        }

        internal static DemandStyle StyleOf(GameItem item)
        {
            if (item == null || !entries.TryGetValue(item, out Entry value) || value.Walk != walk)
            {
                return default;
            }

            return value.Style;
        }

        private static bool Root(GameInventory inventory)
        {
            if (inventory == null || !nodesSeen.Add(inventory))
            {
                return true;
            }

            List<GameItem> childItems = inventory.childItems;
            if (childItems == null)
            {
                return true;
            }

            bool complete = true;
            for (int i = 0; i < childItems.Count; i++)
            {
                if (childItems[i] != null)
                {
                    complete &= Item(childItems[i], 0, false, false, true).Complete;
                }
            }

            return complete;
        }

        private static bool OverBudget()
        {
            if (outOfTime)
            {
                return true;
            }

            if (++sinceCheck < 16)
            {
                return false;
            }

            sinceCheck = 0;
            outOfTime = clock.Elapsed.TotalMilliseconds > Budget;
            return outOfTime;
        }

        private static Up Item(GameItem item, int depth, bool lockedAbove, bool blockedAbove, bool shown)
        {
            if (!entries.TryGetValue(item, out Entry value))
            {
                value = new Entry();
                entries[item] = value;
            }

            if (value.Walk == walk)
            {
                return value.Last;
            }

            value.Walk = walk;
            value.Style = default;
            Visited++;

            Up up = new Up { Complete = true };

            if (value.Epoch != epoch)
            {
                if (OverBudget())
                {
                    up.Complete = false;
                    value.Last = up;
                    return up;
                }

                ComputeSelf(item, value);
            }

            bool opened = shown && IsOpened(item);
            DemandAgg inside = value.Inside;
            bool allBought = value.InsideAllBought;
            bool insideComplete = value.InsideEpoch == epoch;

            if (opened || !insideComplete)
            {
                if (depth >= MaxDepth)
                {
                    inside = default;
                    allBought = true;
                    insideComplete = true;
                }
                else if (!opened && OverBudget())
                {
                    insideComplete = false;
                }
                else
                {
                    inside = default;
                    allBought = true;
                    bool complete = true;
                    Children(item.children, depth + 1,
                        lockedAbove || value.F.Locked,
                        blockedAbove || value.F.BlocksSearch,
                        opened, ref inside, ref allBought, ref complete);
                    insideComplete = complete;

                    if (complete)
                    {
                        value.Inside = inside;
                        value.InsideAllBought = allBought;
                        value.InsideEpoch = epoch;
                    }
                }
            }

            if (!insideComplete)
            {
                up.Complete = false;
                value.Last = up;
                return up;
            }

            DemandAgg self = DemandDecide.Self(kind, clientGoods, value.F, allBought, out up.Bought);
            up.AllBought = up.Bought && allBought;

            if (shown)
            {
                DemandStyle style = value.Style = DemandDecide.Display(kind, clientGoods,
                    value.F, self, inside, lockedAbove, blockedAbove);

                GameItemElement element = item as GameItemElement;
                if (style.Mark != DemandMark.None && element != null)
                {
                    Hits.Add(new Hit { Element = element, Style = style });
                }

                if (opened)
                {
                    FindTabs(item);
                }
            }

            up.Agg = DemandDecide.Report(self, inside, value.F);
            value.Last = up;
            return up;
        }

        private static void ComputeSelf(GameItem item, Entry e)
        {
            Computed++;
            e.Epoch = epoch;

            DemandFacts f = default;
            f.Illegal = Illegality(item);
            f.Locked = IsLocked(item);
            f.BehindLock = item.IsTag("ITEM_BEHIND_LOCK_TAG");
            f.Immovable = IsImmovable(item);
            f.BlocksSearch = kind.Inspector && ContrabandHelper.IsBlockSearchContainer(item);
            f.Owned = GeneralHelper.IsItemOwned(item);

            if (kind.Buying)
            {
                f.SelfOk = BuyingThis(item);
                f.ContentsChecked = !CurrentlyLocked(item) && item.contentWindow != null &&
                                    !item.IsTag("ADV_GUN_TAG");
            }

            e.F = f;
        }

        private static void Children(List<GraphNodeStorage> nodes, int depth,
            bool lockedAbove, bool blockedAbove, bool shown,
            ref DemandAgg inside, ref bool allBought, ref bool complete)
        {
            if (nodes == null)
            {
                return;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                GraphNodeStorage node = nodes[i];
                if (node == null)
                {
                    continue;
                }

                GameItem item = node as GameItem;
                if (item != null)
                {
                    Up up = Item(item, depth, lockedAbove, blockedAbove, shown);
                    if (!up.Complete)
                    {
                        complete = false;
                        continue;
                    }

                    inside.Buyer = DemandDecide.Max(inside.Buyer, up.Agg.Buyer);
                    inside.Illegal = DemandDecide.Max(inside.Illegal, up.Agg.Illegal);
                    allBought &= up.AllBought;
                }
                else if (depth < MaxDepth && nodesSeen.Add(node))
                {
                    Children(node.children, depth + 1, lockedAbove, blockedAbove, shown,
                        ref inside, ref allBought, ref complete);
                }
            }
        }

        private static DemandMark Illegality(GameItem item)
        {
            bool contraband = ContrabandHelper.IsContraband(item);
            return DemandDecide.Illegality(contraband, !contraband && StolenHelper.IsStolenItem(item));
        }

        // ---------------------------------------------------------------
        // 机器存储标签页（MachineryTabs）：物品在某个标签下的格子里时，标签也标出来
        // ---------------------------------------------------------------

        private static void FindTabs(GameItem item)
        {
            PixelWindow contentWindow = item.contentWindow;
            MachineryPanelRow row = contentWindow != null
                ? contentWindow.childElement as MachineryPanelRow : null;
            if (row == null)
            {
                return;
            }

            MachineryTabs tabs = TabsOf(row);
            if (tabs == null || tabs.count <= 0)
            {
                return;
            }

            if (tabsPool.Count <= Tabs.Count)
            {
                tabsPool.Add(new TabsHit());
            }

            TabsHit hit = tabsPool[Tabs.Count];
            hit.Tabs = tabs;
            hit.Pages.Clear();

            for (int i = 0; i < tabs.count; i++)
            {
                hit.Pages.Add(PageInventory(tabs, i));
            }

            Tabs.Add(hit);
        }

        private static MachineryTabs TabsOf(MachineryPanelRow row)
        {
            MachineryPanel panel = row.GetPanel("storage");
            MachineryTabs tabs = panel != null ? panel.content as MachineryTabs : null;
            if (tabs != null)
            {
                return tabs;
            }

            System.Collections.Generic.IReadOnlyList<MachineryPanel> panels = row.Panels;
            if (panels == null)
            {
                return null;
            }

            for (int i = 0; i < panels.Count; i++)
            {
                tabs = panels[i] != null ? panels[i].content as MachineryTabs : null;
                if (tabs != null)
                {
                    return tabs;
                }
            }

            return null;
        }

        private static GameInventory PageInventory(MachineryTabs tabs, int index)
        {
            PixelElement page = tabs.GetPage(index);
            MachineryIntake intake = page as MachineryIntake;
            if (intake != null)
            {
                PixelElement content = intake.content;
                return content as GameInventory;
            }

            return page as GameInventory;
        }

        private static bool IsOpened(GameItem item)
        {
            PixelWindow contentWindow = item.contentWindow;
            return contentWindow != null && contentWindow.IsVisible();
        }

        private static bool IsLocked(GameItem item)
        {
            if (item.IsTag("LOCKED_CONTAINER_TAG") && LockStatus(item))
            {
                return true;
            }

            if (item.IsGameItemType("SUPPLY_CRATE") && LockStatus(item))
            {
                return true;
            }

            return item.contentWindow != null && item.contentWindow.isLocked;
        }

        private static bool LockStatus(GameItem item)
        {
            TagSystem state = item.modifiedState;
            Dictionary<string, TagState> dict = state != null ? state.dict : null;
            if (dict == null || !dict.TryGetValue("LOCK_STATUS", out TagState value))
            {
                return false;
            }

            return value.GetBool();
        }

        private static bool CurrentlyLocked(GameItem item)
        {
            return LockHelper.IsLockedContainer(item) && LockStatus(item);
        }

        private static bool BuyingThis(GameItem item)
        {
            TagSystem state = item.modifiedState;
            Dictionary<string, TagState> dict = state != null ? state.dict : null;

            if (dict == null)
            {
                return client.IsClientBuyingThisItem(item, true);
            }

            keysBefore.Clear();
            foreach (string key in dict.Keys)
            {
                keysBefore.Add(key);
            }

            int count = dict.Count;
            bool result = client.IsClientBuyingThisItem(item, true);

            if (dict.Count != count)
            {
                keysAdded.Clear();
                foreach (string key in dict.Keys)
                {
                    if (!keysBefore.Contains(key))
                    {
                        keysAdded.Add(key);
                    }
                }

                for (int i = 0; i < keysAdded.Count; i++)
                {
                    dict.Remove(keysAdded[i]);
                }
            }

            return result;
        }

        private static bool IsImmovable(GameItem item)
        {
            if (item.IsTag("DEBRIS_PILE_TAG"))
            {
                return true;
            }

            Func<GameItem, GameInventory, bool> remove = item.mayInventoryRemoveItemFunc;
            if (remove != null)
            {
                return !remove(item, item.parentInventory);
            }

            return false;
        }
    }
}
