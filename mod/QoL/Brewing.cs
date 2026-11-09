using System;
using System.Collections.Generic;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 酿酒工坊：热键开面板、按选定的搭配自动装料并启动发酵。
    ///
    /// 全部走游戏公开接口，不打补丁：
    ///   装果实/酵母  → GraphUtils.TryAcceptOnce(瓶, 物, 1)，装完核对瓶里的子物品数
    ///   倒水         → WaterHelper.TransferLiquid(水源, 瓶)，核对瓶里 ml 数
    ///   启动发酵     → WineHelper.TryStartFermentation(瓶)，核对 WINE_CONTAINER_STATE == 1
    /// 每一步都以「真实状态变了」为成功判据，不看返回值的意思。
    /// </summary>
    internal static class Brewing
    {
        internal const string Off = "关闭";

        /// <summary>一次「一键酿造」最多开几瓶，防止手滑把库存全烧了。</summary>
        internal const int MaxPerClick = 20;

        private static bool _applied;
        private static bool _dirty;
        private static float _nextBuild;

        private static string _keyRaw;
        private static KeyCode _key;
        private static bool _keyReady;

        // ---------------------------------------------------------------
        // 挂载 / 卸载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;
            _dirty = false;
            QLog.Info("[酿酒] 工坊已挂载（热键见设置项「酿酒工坊」）");
        }

        internal static void Remove()
        {
            _applied = false;
            _dirty = false;
            _keyReady = false;
            _keyRaw = null;

            try { BrewWindow.Close(); }
            catch (Exception e) { QLog.Warn("[酿酒] 关窗失败：" + e.Message); }
        }

        // ---------------------------------------------------------------
        // 每帧
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!_applied) return;

            // 界面重建推迟到下一帧：在按钮自己的回调里销毁它所在的窗口容易出事
            if (_dirty && Time.unscaledTime >= _nextBuild)
            {
                _dirty = false;
                BrewWindow.Rebuild();
            }

            // 离开店铺（回菜单 / 出门 / 换场景）就别把面板留在屏幕上
            if (BrewWindow.IsOpen && !InStore())
            {
                BrewWindow.Close();
                return;
            }

            KeyCode key = CurrentKey;
            if (key == KeyCode.None) return;
            if (!KeyDown(key)) return;
            if (Blocked()) return;

            BrewWindow.Toggle();
        }

        /// <summary>只有在店里才有物品树可读；菜单和读档阶段 PlayerStore 会抛异常。</summary>
        private static bool InStore()
        {
            try
            {
                if (PlayerStore.Instance == null) return false;
                return PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE;
            }
            catch
            {
                return false;
            }
        }

        private static void MarkDirty(float delay)
        {
            _dirty = true;
            _nextBuild = Time.unscaledTime + delay;
        }

        private static KeyCode CurrentKey
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                string raw = s != null && s.BrewPanelKey != null ? s.BrewPanelKey.Value : Off;

                if (!_keyReady || !string.Equals(raw, _keyRaw, StringComparison.Ordinal))
                {
                    _keyRaw = raw;
                    _key = string.Equals(raw, Off, StringComparison.Ordinal) ? KeyCode.None : QuickShelf.ParseKey(raw);
                    _keyReady = true;
                }
                return _key;
            }
        }

        private static bool KeyDown(KeyCode key)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) return Input.GetMouseButtonDown(key - KeyCode.Mouse0);
            return Input.GetKeyDown(key);
        }

        /// <summary>界面上显示的当前热键名（null 表示功能关着）。</summary>
        internal static string HotkeyName
        {
            get
            {
                KeyCode key = CurrentKey;
                if (key == KeyCode.None) return null;
                try { return QuickShelf.KeyDisplayName(key); }
                catch { return key.ToString(); }
            }
        }

        /// <summary>会抢键或不能开窗的时机。</summary>
        private static bool Blocked()
        {
            try
            {
                if (StoreUIManager.IsAnyTyping()) return true;
                if (KeyBindHelper.SuppressHotkeys) return true;
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 读输入状态失败：" + e.GetType().Name);
                return true;
            }

            try
            {
                if (QuickShelf.IsCapturing || QuickToolHotkey.IsCapturing) return true;
            }
            catch { }

            return !InStore();
        }

        // ---------------------------------------------------------------
        // 对外操作
        // ---------------------------------------------------------------

        /// <summary>按指定搭配酿一瓶。</summary>
        internal static void BrewSingle(BrewCombo combo)
        {
            if (combo == null) return;

            string why;
            bool ok = BrewOne(combo, BrewScan.Scan(), out why);

            if (ok) Brewing.Notify("已下料：" + Describe(combo));
            else Brewing.Notify("没酿成：" + why);

            QLog.Info("[酿酒] 手动酿一瓶：" + Describe(combo) + (ok ? " → 成功" : " → 失败（" + why + "）"));
            MarkDirty(0.25f);
        }

        /// <summary>
        /// 一键酿造。
        /// autoMode=true：按推荐方案逐瓶酿（发霉果不参与、只用干净水）。
        /// autoMode=false：反复酿玩家上面选的那套，材料用完为止。
        /// </summary>
        internal static void BrewAll(bool autoMode, BrewCombo manual, int waterTier)
        {
            int done = 0;
            string why = null;

            for (int i = 0; i < MaxPerClick; i++)
            {
                BrewStock stock = BrewScan.Scan();
                BrewCombo combo;

                if (autoMode)
                {
                    string note;
                    List<BrewPlan> plans = BrewPlanner.Recommend(stock, BrewWindow.PreferredYeast, waterTier, out note);
                    if (plans.Count == 0)
                    {
                        why = note;
                        break;
                    }
                    combo = plans[0].ToCombo();
                }
                else
                {
                    if (manual == null || (!manual.Counterfeit && manual.Grades.Count == 0))
                    {
                        why = "自选搭配里还没放果子";
                        break;
                    }
                    combo = manual;
                }

                string one;
                if (!BrewOne(combo, stock, out one))
                {
                    why = one;
                    break;
                }
                done++;
            }

            if (done == 0) Notify("没酿成：" + why);
            else if (done >= MaxPerClick) Notify("已启动 " + done + " 瓶（单次上限）");
            else Notify("已启动 " + done + " 瓶发酵");

            QLog.Info("[酿酒] 一键酿造（" + (autoMode ? "自动" : "自选") + "）：" + done + " 瓶" +
                      (why != null ? "，停下原因：" + why : string.Empty));

            MarkDirty(0.3f);
        }

        /// <summary>重新扫描：只重画界面。</summary>
        internal static void Rescan()
        {
            MarkDirty(0.05f);
        }

        // ---------------------------------------------------------------
        // 酿一瓶
        // ---------------------------------------------------------------

        private static bool BrewOne(BrewCombo combo, BrewStock stock, out string why)
        {
            why = string.Empty;
            if (combo == null) { why = "没有搭配"; return false; }
            if (stock == null) { why = "没读到库存"; return false; }

            GameItem bottle = FirstBottle(stock);
            if (bottle == null) { why = "没有空酒瓶"; return false; }

            int tier = combo.WaterTier >= 0 ? combo.WaterTier : BestWaterTier(stock);
            GameItem water = tier >= 0 ? PickWaterItem(stock, tier) : null;
            if (water == null)
            {
                why = "找不到" + (tier >= 0 ? BrewScan.TierName(tier) : "合适的水") + "（要 ≥749ml）";
                return false;
            }

            GameItem yeast = PickYeastItem(stock, combo.YeastId, combo.Counterfeit);
            if (yeast == null)
            {
                why = combo.Counterfeit ? "没有合成酵母（走私酒必须用它）" : "没有可用的酵母";
                return false;
            }

            List<GameItem> berries = new List<GameItem>();
            if (!combo.Counterfeit)
            {
                int[] used = new int[4];
                for (int i = 0; i < combo.Grades.Count; i++)
                {
                    int grade = combo.Grades[i];
                    GameItem berry = TakeBerry(stock, grade, used);
                    if (berry == null)
                    {
                        why = "荧光莓不够（缺" + BerryGrade.Label[grade] + "）";
                        return false;
                    }
                    berries.Add(berry);
                }
            }

            string how;
            for (int i = 0; i < berries.Count; i++)
            {
                if (LoadInto(bottle, berries[i], out how)) continue;
                why = "第 " + (i + 1) + " 颗果放不进瓶：" + how;
                return false;
            }

            if (!PourWater(bottle, water, out how))
            {
                why = "水倒不进瓶：" + how;
                return false;
            }

            if (!LoadInto(bottle, yeast, out how))
            {
                why = "酵母放不进瓶：" + how;
                return false;
            }

            bool started = false;
            try { started = WineHelper.TryStartFermentation(bottle); }
            catch (Exception e) { why = "启动发酵出错：" + e.GetType().Name; return false; }

            int state = BrewScan.TagIntOn(bottle, "WINE_CONTAINER_STATE");
            if (!started && state != 1)
            {
                why = "发酵没启动（料可能已经在瓶里）";
                return false;
            }

            int score = BrewScan.TagIntOn(bottle, "WINE_SCORE_INT");
            QLog.Info("[酿酒] 开瓶：" + (combo.Counterfeit ? "走私酒" : WineGrade.NameOf(score)) +
                      " " + score + " 分 · 果实=" + combo.BerryText + " · 水=" + BrewScan.TierName(tier) +
                      " · 酵母=" + BrewScan.Display(yeast));
            return true;
        }

        private static string Describe(BrewCombo combo)
        {
            return combo.BerryText + " + " + (combo.WaterTier >= 0 ? BrewScan.TierName(combo.WaterTier) : "自动水") +
                   " + " + (string.IsNullOrEmpty(combo.YeastId) ? "自动酵母" : "指定酵母");
        }

        private static GameItem FirstBottle(BrewStock stock)
        {
            for (int i = 0; i < stock.EmptyBottles.Count; i++)
            {
                if (BrewScan.IsBottleEmpty(stock.EmptyBottles[i])) return stock.EmptyBottles[i];
            }
            return null;
        }

        /// <summary>挑一颗指定品相的果子；used 记录这次已经用掉的颗数，避免同一颗被选两次。</summary>
        private static GameItem TakeBerry(BrewStock stock, int grade, int[] used)
        {
            BrewBerry stack = stock.Berry(grade);
            if (stack == null) return null;

            int index = used[grade];
            if (index >= stack.Items.Count) return null;

            used[grade] = index + 1;
            return stack.Items[index];
        }

        /// <summary>能用的水质里最好的那一档（0 最纯）。</summary>
        internal static int BestWaterTier(BrewStock stock)
        {
            for (int tier = 0; tier <= 5; tier++)
            {
                for (int i = 0; i < stock.Waters.Count; i++)
                {
                    if (stock.Waters[i].Tier == tier && stock.Waters[i].Ml >= BrewScan.WaterPerBottle) return tier;
                }
            }
            return -1;
        }

        private static GameItem PickWaterItem(BrewStock stock, int tier)
        {
            GameItem found = null;
            int best = 0;
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                BrewWater w = stock.Waters[i];
                if (w.Tier != tier || w.Ml < BrewScan.WaterPerBottle) continue;
                if (found == null || w.Ml > best) { found = w.Item; best = w.Ml; }
            }
            return found;
        }

        private static GameItem PickYeastItem(BrewStock stock, string id, bool synthetic)
        {
            if (synthetic)
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Synthetic && y.Items.Count > 0) return y.Items[0];
                }
                return null;
            }

            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Id == id && y.Items.Count > 0 && !y.Synthetic) return y.Items[0];
                }
            }

            string[] order = { "wine_yeast_infinite", "wine_superyeast", "wine_yeast" };
            for (int k = 0; k < order.Length; k++)
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Id == order[k] && y.Items.Count > 0) return y.Items[0];
                }
            }

            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                BrewYeast y = stock.Yeasts[i];
                if (!y.Synthetic && y.Items.Count > 0) return y.Items[0];
            }
            return null;
        }

        /// <summary>把一件东西塞进瓶子。三个候选路径，每一步都用「瓶子真的变了」验证。</summary>
        private static bool LoadInto(GameItem bottle, GameItem thing, out string how)
        {
            how = "放不进去";

            int before = BrewScan.ChildCount(bottle);
            string brief = Brief(bottle);

            GraphNodeStorage node = bottle as GraphNodeStorage;
            if (node != null)
            {
                try { GraphUtils.TryAcceptOnce(node, thing, 1); } catch { }
                if (Changed(bottle, before, brief)) { how = "accept-once"; return true; }

                try { GraphUtils.TryAccept(node, thing); } catch { }
                if (Changed(bottle, before, brief)) { how = "accept"; return true; }
            }

            GameInventory inner = InnerInventory(bottle);
            if (inner != null)
            {
                GraphNodeStorage innerNode = inner as GraphNodeStorage;
                if (innerNode != null)
                {
                    try { GraphUtils.TryAcceptOnce(innerNode, thing, 1); } catch { }
                    if (Changed(bottle, before, brief)) { how = "accept-inner"; return true; }
                }
            }

            return false;
        }

        private static bool Changed(GameItem bottle, int childBefore, string briefBefore)
        {
            if (BrewScan.ChildCount(bottle) != childBefore) return true;
            return !string.Equals(Brief(bottle), briefBefore, StringComparison.Ordinal);
        }

        private static string Brief(GameItem bottle)
        {
            return BrewScan.Ml(bottle) + "/" + BrewScan.ChildCount(bottle) + "/" +
                   BrewScan.TagIntOn(bottle, "WINE_CONTAINER_STATE");
        }

        private static GameInventory InnerInventory(GameItem item)
        {
            try
            {
                List<GraphNodeStorage> children = item.children;
                if (children == null) return null;
                for (int i = 0; i < children.Count; i++)
                {
                    GameInventory inv = children[i] as GameInventory;
                    if (inv != null) return inv;
                }
            }
            catch { }
            return null;
        }

        /// <summary>倒水：一次 750ml。做完核对瓶里的毫升数真的够一瓶。</summary>
        private static bool PourWater(GameItem bottle, GameItem water, out string how)
        {
            how = "没倒进去";

            try { WaterHelper.TransferLiquid(water, bottle); }
            catch (Exception e)
            {
                how = "接口出错 " + e.GetType().Name;
                return false;
            }

            if (BrewScan.Ml(bottle) >= BrewScan.WaterPerBottle) return true;

            // 极少数瓶子钩子没初始化，补一次再倒
            try
            {
                WineHelper.InitWineBottle(bottle);
                WaterHelper.TransferLiquid(water, bottle);
            }
            catch { }

            if (BrewScan.Ml(bottle) >= BrewScan.WaterPerBottle) { how = "初始化后补倒"; return true; }

            how = "瓶里只有 " + BrewScan.Ml(bottle) + "ml";
            return false;
        }

        internal static void Notify(string msg)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(msg);
            }
            catch { }
        }
    }
}
