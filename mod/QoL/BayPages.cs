using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 机器区 / 暗格 多页扩容（默认关闭）。
    ///
    /// 只用游戏自己的东西：GameGridInventory 建页、PixelWindow.Attach 换页、
    /// 容器物品自己的 tag 存页数、MachineryButton 当按钮、PlayerStore.playerCash 扣钱。
    ///
    /// 存档红线段：游戏只保存"当前挂在窗口上的那一页"，所以**存档前把 2~N 页的物品收回第 1 页**
    /// （挂 PlayerStore.SaveGame 前缀），否则读档会丢东西。这是不改存档格式的唯一安全做法。
    /// </summary>
    internal static class BayPages
    {
        private const string HarmonyId = "local.qol.bay_pages";
        private const string PagesTag = "QOL_BAY_PAGES";
        private const int MaxPages = 5;
        private const int MachinePageCost = 1200;
        private const int InjectorNeed = 8;

        /// <summary>容器标识 → 每页格子尺寸（取自游戏自己的工厂方法）。</summary>
        private static readonly Dictionary<string, int[]> Sizes = new Dictionary<string, int[]>
        {
            { "machine_bay0", new[] { 6, 8 } },
            { "machine_bay_ext", new[] { 11, 14 } },
            { "smuggler_bay", new[] { 5, 5 } },
            { "smuggler_bay_mod", new[] { 8, 8 } },
        };

        private sealed class Bay
        {
            internal GameItem Item;
            internal PixelWindow Window;
            internal GameGridInventory Original;
            internal readonly List<GameGridInventory> Pages = new List<GameGridInventory>();
            internal readonly List<MachineryButton> Buttons = new List<MachineryButton>();
            internal int Current;
            internal int Unlocked = 1;
            internal bool Buying;
        }

        private static readonly Dictionary<GameItem, Bay> Bays = new Dictionary<GameItem, Bay>();
        private static Harmony _harmony;
        private static float _timer;
        private static bool _warned;

        internal static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.BayPages.Value;
            }
        }

        internal static void Apply()
        {
            try
            {
                // 存档前把额外页的物品收回首页，避免读档丢失
                var save = AccessTools.Method(typeof(PlayerStore), "SaveGame");
                if (save != null)
                {
                    _harmony = new Harmony(HarmonyId);
                    _harmony.Patch(save, prefix: new HarmonyMethod(typeof(BayPages), nameof(BeforeSave)));
                }
                else
                {
                    QLog.Warn("[扩容页] 找不到 PlayerStore.SaveGame，额外页物品不会自动回迁");
                }

                QLog.Info("[扩容页] 已挂载（机器区 / 暗格多页）");
            }
            catch (Exception e)
            {
                QLog.Error("[扩容页] 挂载失败", e);
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
            Bays.Clear();
        }

        // ---------------------------------------------------------------
        // 每帧：给打开的容器装按钮、维护页
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Enabled) return;

            _timer += dt;
            if (_timer < 0.5f) return;
            _timer = 0f;

            try
            {
                foreach (GameItem item in DesequencerAutoFeed.AllOwnedItems())
                {
                    if (!IsBay(item)) continue;

                    try
                    {
                        Sync(item);
                    }
                    catch (Exception e)
                    {
                        WarnOnce("处理 " + item.identifier + " 出错：" + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                WarnOnce("扫描容器出错：" + e.Message);
            }
        }

        private static bool IsBay(GameItem item)
        {
            return item != null && item.identifier != null && Sizes.ContainsKey(item.identifier);
        }

        /// <summary>按需建页、按页数补按钮；窗口没打开就只读页数不建 UI。</summary>
        private static void Sync(GameItem item)
        {
            Bay bay;
            if (!Bays.TryGetValue(item, out bay))
            {
                bay = new Bay { Item = item };
                Bays[item] = bay;
            }

            bay.Unlocked = Mathf.Clamp(ReadPages(item), 1, MaxPages);

            PixelWindow window = item.contentWindow;
            if (window == null || !window.IsVisible()) return;

            if (bay.Window != window)
            {
                bay.Window = window;
                bay.Buttons.Clear();
                bay.Current = 0;
                bay.Original = FindGrid(window);
                if (bay.Original == null) return;
                EnsurePages(bay);
                AttachPage(bay, 0);
            }

            EnsureButtons(bay);
        }

        private static int ReadPages(GameItem item)
        {
            try { return item.GetTagReadonly(PagesTag).GetInt(); }
            catch { return 1; }
        }

        private static void WritePages(GameItem item, int pages)
        {
            try
            {
                item.ModifyTag(PagesTag, state => state.Enable().SetInt(pages));
            }
            catch (Exception e)
            {
                QLog.Warn("[扩容页] 写页数失败：" + e.Message);
            }
        }

        private static GameGridInventory FindGrid(PixelWindow window)
        {
            try
            {
                List<GameGridInventory> invs = GraphUtils.FindAllChildrenType<GameGridInventory>(window);
                return invs != null && invs.Count > 0 ? invs[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static void EnsurePages(Bay bay)
        {
            int[] size;
            if (!Sizes.TryGetValue(bay.Item.identifier, out size)) return;

            while (bay.Pages.Count < bay.Unlocked)
            {
                GameGridInventory page = new GameGridInventory(size[0], size[1]);
                CopyDelegates(bay.Original, page);
                bay.Pages.Add(page);
            }
        }

        /// <summary>新页必须继承原页的准入规则与排序，否则东西放不进去、图标也不刷新。</summary>
        private static void CopyDelegates(GameInventory from, GameInventory to)
        {
            try
            {
                to.mayInventoryAddItemFunc = from.mayInventoryAddItemFunc;
                to.slotPriorityFunc = from.slotPriorityFunc;
                to.onSlotAddItemEndFunc = from.onSlotAddItemEndFunc;
                to.onSlotRemoveItemEndFunc = from.onSlotRemoveItemEndFunc;
            }
            catch (Exception e)
            {
                QLog.Warn("[扩容页] 复制容器规则失败：" + e.Message);
            }
        }

        private static void AttachPage(Bay bay, int index)
        {
            try
            {
                if (index < 0 || index >= bay.Pages.Count) return;

                // 原页也要先摘下来，否则窗口会同时挂两个格子
                if (bay.Original != null) bay.Window.Detach(bay.Original);
                bay.Window.Attach(bay.Pages[index]);
                bay.Current = index;
                PaintButtons(bay);
            }
            catch (Exception e)
            {
                QLog.Warn("[扩容页] 换页失败：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 按钮
        // ---------------------------------------------------------------

        private static void EnsureButtons(Bay bay)
        {
            int want = bay.Unlocked + (bay.Unlocked < MaxPages ? 1 : 0);
            while (bay.Buttons.Count < want)
            {
                int index = bay.Buttons.Count;
                bool isBuy = index >= bay.Unlocked;
                string text = isBuy ? "+1页" : (index + 1).ToString();
                Bay captured = bay;
                int capturedIndex = index;

                MachineryButton btn = new MachineryButton(text, delegate
                {
                    if (isBuy) BuyPage(captured);
                    else AttachPage(captured, capturedIndex);
                }, isBuy ? 34 : 16, 16);

                try { bay.Window.AddHeaderButton(btn); }
                catch { return; }

                bay.Buttons.Add(btn);
            }

            PaintButtons(bay);
        }

        private static void PaintButtons(Bay bay)
        {
            for (int i = 0; i < bay.Buttons.Count; i++)
            {
                try
                {
                    if (i >= bay.Unlocked) bay.Buttons[i].SetText("+1页");
                    else bay.Buttons[i].SetText(i == bay.Current ? "[" + (i + 1) + "]" : (i + 1).ToString());
                }
                catch
                {
                    // 单个按钮失败不影响其它
                }
            }
        }

        // ---------------------------------------------------------------
        // 买页
        // ---------------------------------------------------------------

        private static void BuyPage(Bay bay)
        {
            try
            {
                if (bay.Unlocked >= MaxPages) return;
                if (bay.Buying) return;
                bay.Buying = true;

                bool smuggler = bay.Item.identifier != null && bay.Item.identifier.StartsWith("smuggler", StringComparison.Ordinal);

                if (smuggler)
                {
                    if (!PaySmuggler()) { Notice("材料不够：需要 1 个空的走私者暗格 + " + InjectorNeed + " 个粉色注射器"); return; }
                }
                else
                {
                    if (!PayCash(MachinePageCost)) { Notice("钱不够：需要 " + MachinePageCost); return; }
                }

                bay.Unlocked++;
                WritePages(bay.Item, bay.Unlocked);
                EnsurePages(bay);
                EnsureButtons(bay);
                Notice("已扩容到 " + bay.Unlocked + " 页");
                QLog.Info("[扩容页]「" + bay.Item.name + "」扩到 " + bay.Unlocked + " 页");
            }
            catch (Exception e)
            {
                QLog.Warn("[扩容页] 购买失败：" + e.Message);
            }
            finally
            {
                bay.Buying = false;
            }
        }

        private static bool PayCash(int amount)
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null || store.playerCash < amount) return false;
                store.playerCash -= amount;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool PaySmuggler()
        {
            try
            {
                GameItem crate = null;
                List<GameItem> injectors = new List<GameItem>();

                foreach (GameItem item in DesequencerAutoFeed.AllOwnedItems())
                {
                    if (item == null) continue;

                    if (crate == null && IsEmptySmugglerCrate(item)) crate = item;
                    else if (injectors.Count < InjectorNeed && IsPinkInjector(item)) injectors.Add(item);
                }

                if (crate == null || injectors.Count < InjectorNeed) return false;

                crate.Destroy();
                for (int i = 0; i < InjectorNeed; i++) injectors[i].Destroy();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEmptySmugglerCrate(GameItem item)
        {
            try
            {
                if (!string.Equals(item.identifier, "smuggler_bay", StringComparison.Ordinal)) return false;
                if (!GeneralHelper.IsItemOwned(item)) return false;
                if (item.IsTag("ITEM_BEHIND_LOCK_TAG") || item.IsTag("LOCKED_TAG")) return false;

                GameInventory inv = item.parentInventory;
                // 空的：它自己不是容器（作为材料用的暗格是普通物品形态）
                return inv != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPinkInjector(GameItem item)
        {
            try
            {
                return string.Equals(item.identifier, "pink_injector", StringComparison.Ordinal)
                    && GeneralHelper.IsItemOwned(item)
                    && !item.IsTag("ITEM_BEHIND_LOCK_TAG")
                    && !item.IsTag("LOCKED_TAG");
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 存档前回迁
        // ---------------------------------------------------------------

        private static void BeforeSave()
        {
            if (!Enabled) return;

            try
            {
                foreach (KeyValuePair<GameItem, Bay> pair in Bays)
                {
                    Bay bay = pair.Value;
                    for (int i = 1; i < bay.Pages.Count; i++)
                    {
                        MoveAllToFirst(bay, bay.Pages[i]);
                    }
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[扩容页] 存档前回迁失败：" + e.Message);
            }
        }

        private static void MoveAllToFirst(Bay bay, GameGridInventory from)
        {
            if (from == null) return;

            List<GameItem> inside = from.childItems;
            if (inside == null || inside.Count == 0) return;

            GameGridInventory home = bay.Pages.Count > 0 ? bay.Pages[0] : null;
            if (home == null) return;

            // 复制一份再动，避免遍历时集合被改
            List<GameItem> list = new List<GameItem>(inside);
            int moved = 0;
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    SlotMarker marker = home.TryFindOneValidInventorySlot(list[i]);
                    if (marker == null || marker.numTransfer < 1) continue;
                    from.Expel(list[i]);
                    if (marker.TryAcceptOnce(1) == 1) moved++;
                }
                catch
                {
                    // 放不下就留在原页，下次再试
                }
            }

            if (moved > 0)
            {
                QLog.Info("[扩容页] 存档前把 " + moved + " 件物品收回首页");
            }
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        private static void WarnOnce(string text)
        {
            if (_warned) return;
            _warned = true;
            QLog.Warn("[扩容页] " + text);
        }

        private static void Notice(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(text);
            }
            catch
            {
                // 提示失败不影响功能
            }

            QLog.Info("[扩容页] " + text);
        }
    }
}
