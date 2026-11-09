using System;
using System.Collections.Generic;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 酿酒工坊：热键开面板、按推荐方案自动装料并启动发酵。
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

        /// <summary>一次「一键酿完」最多开几瓶，防止手滑把库存全烧了。</summary>
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

        /// <summary>界面上显示的当前热键名（「未设置」表示功能关着）。</summary>
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

            // 有别的功能正在等按键时，别抢
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

        /// <summary>酿一行：all=true 这一行全酿，false 只酿一瓶。</summary>
        internal static void BrewGroup(BrewGroup group, bool all)
        {
            if (group == null || group.Sample == null) return;

            int want = all ? group.Count : 1;
            int done = 0;
            string why = null;

            for (int i = 0; i < want; i++)
            {
                BrewStock stock = BrewScan.Scan();
                if (!BrewOne(group.Sample, stock, out why)) break;
                done++;
            }

            if (done == 0) Notify("没酿成：" + why);
            else if (done < want) Notify("已启动 " + done + " 瓶，" + why);
            else Notify("已启动 " + done + " 瓶发酵");

            QLog.Info("[酿酒] 按行酿造：目标 " + want + " 瓶，实际 " + done + " 瓶" + (why != null ? "（" + why + "）" : string.Empty));

            MarkDirty(0.25f);
        }

        /// <summary>一键酿完：每酿一瓶重新规划一次，材料变了也能接着用剩下的。</summary>
        internal static void BrewAll()
        {
            string preferred = BrewWindow.PreferredYeast;
            int done = 0;
            string why = null;

            for (int i = 0; i < MaxPerClick; i++)
            {
                BrewStock stock = BrewScan.Scan();
                string note;
                List<BrewPlan> plans = BrewPlanner.Recommend(stock, preferred, out note);
                if (plans.Count == 0)
                {
                    why = note;
                    break;
                }

                string one;
                if (!BrewOne(plans[0], stock, out one))
                {
                    why = one;
                    break;
                }
                done++;
            }

            if (done == 0) Notify("一键酿完没成功：" + why);
            else if (done >= MaxPerClick) Notify("已启动 " + done + " 瓶，达到单次上限");
            else Notify("一键酿完：已启动 " + done + " 瓶发酵");

            QLog.Info("[酿酒] 一键酿完：启动 " + done + " 瓶" + (why != null ? "，停下原因：" + why : string.Empty));

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

        private static bool BrewOne(BrewPlan plan, BrewStock stock, out string why)
        {
            why = string.Empty;
            if (plan == null || plan.Recipe == null)
            {
                why = "没有配方";
                return false;
            }

            GameItem bottle = FirstBottle(stock);
            if (bottle == null)
            {
                why = "没有空酒瓶";
                return false;
            }

            GameItem water = PickWaterItem(stock, plan.Water);
            if (water == null)
            {
                why = "找不到 " + (plan.Water != null ? plan.Water.Name : "合适的水") + "（要 ≥749ml）";
                return false;
            }

            GameItem berryA = null;
            GameItem berryB = null;
            if (plan.Recipe.HasBerry)
            {
                if (!PickBerries(stock, plan.Recipe, out berryA, out berryB))
                {
                    why = "荧光莓不够（要 " + plan.Recipe.BerryText + "）";
                    return false;
                }
            }

            GameItem yeast = PickYeastItem(stock, plan.Yeast);
            if (yeast == null)
            {
                why = plan.Counterfeit
                    ? "没有合成酵母（走私酒必须用它）"
                    : "没有可用的酵母";
                return false;
            }

            string how;
            if (berryA != null && !LoadInto(bottle, berryA, out how))
            {
                why = "果实放不进瓶：" + how;
                return false;
            }
            if (berryB != null && !LoadInto(bottle, berryB, out how))
            {
                why = "第二颗果放不进瓶：" + how;
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

            QLog.Info("[酿酒] 开瓶：" + plan.WineName + " " + plan.Score + " 分，水=" + plan.WaterName +
                      "，酵母=" + plan.YeastName + "，累计 " + BrewScan.ChildCount(bottle) + " 个子物品");
            return true;
        }

        private static GameItem FirstBottle(BrewStock stock)
        {
            for (int i = 0; i < stock.EmptyBottles.Count; i++)
            {
                if (BrewScan.IsBottleEmpty(stock.EmptyBottles[i])) return stock.EmptyBottles[i];
            }
            return null;
        }

        private static GameItem PickWaterItem(BrewStock stock, BrewWater like)
        {
            if (like == null) return null;

            GameItem found = null;
            int best = 0;
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                BrewWater w = stock.Waters[i];
                if (w.Tier != like.Tier || w.Ml < BrewScan.WaterPerBottle) continue;
                if (found == null || w.Ml > best) { found = w.Item; best = w.Ml; }
            }
            return found;
        }

        private static bool PickBerries(BrewStock stock, BrewRecipe recipe, out GameItem a, out GameItem b)
        {
            a = null;
            b = null;

            BrewBerry first = stock.Berry(recipe.BerryA);
            if (first == null || first.Items.Count == 0) return false;
            a = first.Items[0];

            if (!recipe.TwoBerry) return true;

            if (recipe.BerryB == recipe.BerryA)
            {
                if (first.Items.Count < 2) { a = null; return false; }
                b = first.Items[1];
                return true;
            }

            BrewBerry second = stock.Berry(recipe.BerryB);
            if (second == null || second.Items.Count == 0) { a = null; return false; }
            b = second.Items[0];
            return true;
        }

        private static GameItem PickYeastItem(BrewStock stock, BrewYeast like)
        {
            if (like != null)
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Id == like.Id && y.Items.Count > 0) return y.Items[0];
                }

                // 走私酒只能用合成酵母：绝不能用普通酵母顶替，否则会酿出一瓶劣质酒
                if (like.Synthetic) return null;
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

        /// <summary>倒水：一次 750ml。做完核对瓶里的毫升数真的涨了。</summary>
        private static bool PourWater(GameItem bottle, GameItem water, out string how)
        {
            how = "没倒进去";
            int before = BrewScan.Ml(bottle);

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

            how = "瓶里只有 " + BrewScan.Ml(bottle) + "ml（原 " + before + "ml）";
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
