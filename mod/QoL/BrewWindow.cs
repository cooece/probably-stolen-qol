using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 酿酒工坊的窗口。
    ///
    /// 走游戏自带的 CustomUI + Style.Machinery：
    /// 机械风皮肤本身就是 2× 像素 + 九宫格 bevel 框，字清、边框锐，还和游戏里的机器面板一致。
    /// 布局是「五张状态卡 → 表头 → 可滚动配方表 → 底部操作条」，列宽固定，数字右齐不跳动。
    /// </summary>
    internal static class BrewWindow
    {
        private const string WindowId = "qol.brew";
        private const string Title = "酿酒工坊";

        private const float WinW = 880f;

        /// <summary>除配方表以外的固定高度：外框 58 + 卡片 78 + 表头 32 + 列标题 26 + 两条分隔 + 两行操作 + 间距。</summary>
        private const float FixedH = 324f;

        private const float Pad = 8f;          // 行间距
        private const float CardH = 78f;
        private const float RowPitch = 40f;
        private const float BtnH = 32f;
        private const float Gap = 6f;

        // 列宽：合计 766 + 6 个间距 36 = 802，正好塞进滚动区（816）
        private const float ColBrew = 152f;
        private const float ColName = 138f;
        private const float ColScore = 62f;
        private const float ColBerry = 140f;
        private const float ColWater = 80f;
        private const float ColYeast = 118f;
        private const float ColPrice = 76f;

        private const int RowsPerPage = 8;

        private static readonly Color Body = MachineryStyle.BODY_TEXT;
        private static readonly Color Value = MachineryStyle.VALUE_TEXT;
        private static readonly Color DimColor = new Color(0.63f, 0.63f, 0.58f);
        private static readonly Color OkColor = MachineryStyle.PROGRESS_FILL;
        private static readonly Color WarnColor = MachineryStyle.PROGRESS_FILL_WARNING;
        private static readonly Color BadColor = MachineryStyle.PROGRESS_FILL_DANGER;
        private static readonly Color GoldColor = new Color(0.925f, 0.78f, 0.35f);

        private const string KeyYeast = "qol.brew.yeast";
        private const string KeyX = "qol.brew.x";
        private const string KeyY = "qol.brew.y";

        private static int _page;
        private static bool _posLoaded;
        private static bool _hasPos;
        private static Vector2 _pos;

        private static string _preferred;
        private static bool _preferredLoaded;

        // ---------------------------------------------------------------
        // 对外
        // ---------------------------------------------------------------

        internal static bool IsOpen
        {
            get
            {
                CustomUIManager ui = CustomUIManager.Instance;
                return ui != null && ui.IsOpen(WindowId);
            }
        }

        internal static void Toggle()
        {
            try
            {
                CustomUIManager ui = CustomUIManager.Instance;
                if (ui == null)
                {
                    Brewing.Notify("界面还没就绪，进游戏后再按。");
                    return;
                }

                if (ui.IsOpen(WindowId))
                {
                    Remember(ui);
                    ui.CloseWindow(WindowId);
                }
                else Build(ui);
            }
            catch (Exception e)
            {
                QLog.Error("[酿酒] 开关面板失败", e);
            }
        }

        internal static void Rebuild()
        {
            try
            {
                CustomUIManager ui = CustomUIManager.Instance;
                if (ui != null && ui.IsOpen(WindowId)) Build(ui);
            }
            catch (Exception e)
            {
                QLog.Error("[酿酒] 重画面板失败", e);
            }
        }

        internal static void Close()
        {
            try
            {
                CustomUIManager ui = CustomUIManager.Instance;
                if (ui != null && ui.IsOpen(WindowId)) ui.CloseWindow(WindowId);
            }
            catch { }
        }

        internal static string PreferredYeast
        {
            get
            {
                if (!_preferredLoaded)
                {
                    _preferredLoaded = true;
                    try { _preferred = PlayerPrefs.GetString(KeyYeast, string.Empty); }
                    catch { _preferred = string.Empty; }
                }
                return _preferred ?? string.Empty;
            }
        }

        // ---------------------------------------------------------------
        // 建窗
        // ---------------------------------------------------------------

        private static void Build(CustomUIManager ui)
        {
            try
            {
                BuildCore(ui);
            }
            catch (Exception e)
            {
                QLog.Error("[酿酒] 画面板失败", e);
                Brewing.Notify("面板画不出来：" + e.GetType().Name);
            }
        }

        private static void BuildCore(CustomUIManager ui)
        {
            Remember(ui);
            if (ui.IsOpen(WindowId)) ui.CloseWindow(WindowId);

            BrewStock stock = BrewScan.Scan();
            string note;
            List<BrewPlan> plans = BrewPlanner.Recommend(stock, PreferredYeast, out note);
            List<BrewGroup> groups = BrewPlanner.Group(plans);

            int totalValue = 0;
            for (int i = 0; i < plans.Count; i++) totalValue += plans[i].Price;

            int pageCount = Math.Max(1, (groups.Count + RowsPerPage - 1) / RowsPerPage);
            if (_page >= pageCount) _page = pageCount - 1;
            if (_page < 0) _page = 0;
            int first = _page * RowsPerPage;
            int visible = Math.Min(RowsPerPage, Math.Max(0, groups.Count - first));

            // 高度跟着内容走：两瓶时不空一大片，八瓶时也不会挤出框
            float listH = groups.Count == 0
                ? (1 + stock.Notes.Count) * 27f + 4f
                : Math.Max(1, visible) * RowPitch - Pad;
            float winH = FixedH + listH;

            CustomUIBuilder b = ui.CreateWindow(WindowId, Title, "overlay", null, CustomUIManager.Style.Machinery)
                .SetSize(WinW, winH)
                .SetDraggable(true)
                .SetCloseOnEscape(true);

            b.BeginColumn(Pad, TextAnchor.UpperLeft);

            BuildCards(b, stock);

            // ---- 表头行 ----
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            b.AddHeader("推荐配方");
            FixSize(b.Last, 200f, 0f);
            CustomUIElement noteCell = Text(b, plans.Count > 0 ? note : "", -1f, 13f, DimColor);
            if (noteCell != null)
            {
                noteCell.SetFlexible(1f, 0f);
                if (noteCell.text != null)
                {
                    noteCell.text.horizontalAlignment = HorizontalAlignmentOptions.Right;
                    noteCell.text.enableWordWrapping = false;
                    noteCell.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }
            b.End();

            b.AddSeparator();

            // ---- 列标题 ----
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            Text(b, "酿造", ColBrew, 13f, DimColor);
            Text(b, "品名", ColName, 13f, DimColor);
            Text(b, "分数", ColScore, 13f, DimColor);
            Text(b, "果实", ColBerry, 13f, DimColor);
            Text(b, "水", ColWater, 13f, DimColor);
            Text(b, "酵母", ColYeast, 13f, DimColor);
            Text(b, "合计", ColPrice, 13f, DimColor).SetAlignment(HorizontalAlignmentOptions.Right);
            b.End();

            // ---- 配方表 ----
            // 机械风皮肤没有 ScrollView 预制体（CustomUIMachineryStyle 不生成），BeginScroll 会
            // 静默退化成普通列；所以这里直接用列 + 翻页，窗口高度按行数算，保证永远不溢出边框。
            b.BeginColumn(Pad, TextAnchor.UpperLeft);
            if (groups.Count == 0)
            {
                Note(b, string.IsNullOrEmpty(note) ? "没有可酿的配方。" : note, 780f, 15f, BadColor);
                AddStockNotes(b, stock);
            }
            else
            {
                for (int i = first; i < first + visible; i++) BuildRow(b, groups[i], i);
            }
            b.End();

            b.AddSeparator();

            // ---- 操作条 ----
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            string allText = plans.Count > 0
                ? "一键酿完（" + Math.Min(plans.Count, Brewing.MaxPerClick) + " 瓶）"
                : "一键酿完";
            Button(b, allText, 210f, delegate { if (groups.Count > 0) Brewing.BrewAll(); else Brewing.Notify("没有可酿的配方"); }, plans.Count > 0);
            Button(b, "重新扫描", 130f, Brewing.Rescan, true);

            CustomUIElement filler = Text(b, "", -1f, 13f, DimColor);
            if (filler != null) filler.SetFlexible(1f, 0f);

            Button(b, "<", 40f, delegate { TurnPage(-1, pageCount); }, _page > 0);
            Text(b, (_page + 1) + " / " + pageCount, 64f, 15f, Value).SetAlignment(HorizontalAlignmentOptions.Center);
            Button(b, ">", 40f, delegate { TurnPage(1, pageCount); }, _page < pageCount - 1);
            b.End();

            // ---- 酵母行 ----
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            Text(b, "酵母", 56f, 14f, DimColor);
            Button(b, "<", 40f, delegate { CycleYeast(-1); }, true);
            Text(b, YeastLabel(stock), 170f, 14f, GoldColor).SetAlignment(HorizontalAlignmentOptions.Center);
            Button(b, ">", 40f, delegate { CycleYeast(1); }, true);

            CustomUIElement hint = Text(b, HintText(stock, totalValue), -1f, 13f, DimColor);
            if (hint != null)
            {
                hint.SetFlexible(1f, 0f);
                if (hint.text != null)
                {
                    hint.text.horizontalAlignment = HorizontalAlignmentOptions.Right;
                    hint.text.enableWordWrapping = false;
                    hint.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }
            b.End();

            b.End();

            CustomUIWindow win = b.Show();
            ApplyPosition(win);
        }

        private static void BuildCards(CustomUIBuilder b, BrewStock stock)
        {
            b.BeginRow(8f, TextAnchor.MiddleLeft);

            Card(b, "空酒瓶", stock.EmptyBottles.Count.ToString(),
                stock.EmptyBottles.Count > 0 ? Value : BadColor,
                stock.EmptyBottles.Count > 0 ? null : "没有空瓶，先弄几个空酒瓶。");

            string ferment = stock.Fermenting > 0
                ? stock.Fermenting + (stock.SoonestDays > 0 ? "（" + stock.SoonestDays + " 天）" : "")
                : "0";
            Card(b, "发酵中", ferment, stock.Fermenting > 0 ? OkColor : DimColor,
                stock.Fermenting > 0 ? "正在发酵的瓶数；括号里是最快还有几天。" : null);

            Card(b, "已酿好", stock.Finished.ToString(), stock.Finished > 0 ? GoldColor : DimColor,
                stock.Finished > 0 ? "已经能卖的酒。" : null);

            int portions = stock.WaterPortions();
            Card(b, "可用水", portions + " 份", portions > 0 ? Value : BadColor,
                WaterDetail(stock));

            Card(b, "荧光莓", stock.BerryTotal() + " 颗", stock.BerryTotal() > 0 ? Value : BadColor,
                BerryDetail(stock));

            b.End();
        }

        private static void Card(CustomUIBuilder b, string caption, string value, Color valueColor, string tooltip)
        {
            b.BeginPanel(160f, CardH, TextAnchor.MiddleLeft, 10f, 2f, null);
            CustomUIElement panel = b.Last;

            Text(b, caption, 140f, 12f, DimColor);
            Text(b, value, 140f, 20f, valueColor);

            b.End();

            if (panel != null)
            {
                // 面板默认不接收射线，挂鼠标说明前得先打开，否则悬停没反应
                if (panel.image != null) panel.image.raycastTarget = true;
                if (!string.IsNullOrEmpty(tooltip)) panel.SetTooltip(tooltip);
            }
        }

        private static void BuildRow(CustomUIBuilder b, BrewGroup group, int index)
        {
            BrewPlan plan = group.Sample;
            if (plan == null) return;

            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            // 第一列：酿造按钮
            if (group.Count > 1)
            {
                BrewGroup captured = group;
                Button(b, "酿 ×" + group.Count, 96f, delegate { Brewing.BrewGroup(captured, true); }, true);
                Button(b, "酿 1", 50f, delegate { Brewing.BrewGroup(captured, false); }, true);
            }
            else
            {
                BrewGroup captured = group;
                Button(b, "酿 1 瓶", 96f, delegate { Brewing.BrewGroup(captured, false); }, true);
                // 占位：让后面各列和「酿 ×N」那几行对齐
                Text(b, "", 50f, 13f, DimColor);
            }

            Text(b, plan.WineName, ColName, 15f, plan.Counterfeit ? BadColor : WineGrade.ColorOf(plan.Score));

            if (plan.Counterfeit) Text(b, "走私", ColScore, 15f, BadColor);
            else Text(b, plan.Score + " 分", ColScore, 15f, WineGrade.ColorOf(plan.Score));

            Text(b, plan.Recipe.BerryText, ColBerry, 15f, plan.Recipe.HasBerry ? Body : DimColor);
            Text(b, plan.WaterName, ColWater, 15f, plan.Water != null && plan.Water.Dirty ? new Color(0.75f, 0.6f, 0.95f) : Body);
            Text(b, plan.YeastName, ColYeast, 15f, Body);
            Text(b, group.TotalPrice.ToString(), ColPrice, 15f, GoldColor).SetAlignment(HorizontalAlignmentOptions.Right);

            b.End();
        }

        private static void AddStockNotes(CustomUIBuilder b, BrewStock stock)
        {
            for (int i = 0; i < stock.Notes.Count; i++) Note(b, stock.Notes[i], 780f, 13f, WarnColor);
        }

        /// <summary>可以换行的说明文字（提示不要被省略号吃掉）。</summary>
        private static CustomUIElement Note(CustomUIBuilder b, string text, float width, float size, Color color)
        {
            CustomUIElement el = Text(b, text, width, size, color);
            if (el != null && el.text != null)
            {
                el.text.enableWordWrapping = true;
                el.text.overflowMode = TextOverflowModes.Overflow;
            }
            return el;
        }

        // ---------------------------------------------------------------
        // 行为
        // ---------------------------------------------------------------

        private static void TurnPage(int dir, int pageCount)
        {
            int target = _page + dir;
            if (target < 0 || target >= pageCount) return;
            _page = target;
            Brewing.Rescan();
        }

        private static void CycleYeast(int dir)
        {
            try
            {
                BrewStock stock = BrewScan.Scan();
                List<BrewYeast> list = new List<BrewYeast>();
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    if (!stock.Yeasts[i].Synthetic) list.Add(stock.Yeasts[i]);
                }

                int count = list.Count + 1;   // 0 = 自动
                int index = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].Id, PreferredYeast, StringComparison.Ordinal)) { index = i + 1; break; }
                }

                index = ((index + dir) % count + count) % count;

                _preferred = index == 0 ? string.Empty : list[index - 1].Id;
                _preferredLoaded = true;
                try
                {
                    PlayerPrefs.SetString(KeyYeast, _preferred);
                    PlayerPrefs.Save();
                }
                catch { }

                Brewing.Rescan();
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 切换酵母失败：" + e.GetType().Name);
            }
        }

        private static string YeastLabel(BrewStock stock)
        {
            string id = PreferredYeast;
            if (string.IsNullOrEmpty(id)) return "《自动》";

            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                if (string.Equals(stock.Yeasts[i].Id, id, StringComparison.Ordinal)) return "《" + stock.Yeasts[i].Name + "》";
            }
            return "《自动》";
        }

        // ---------------------------------------------------------------
        // 文案
        // ---------------------------------------------------------------

        private static string WaterDetail(BrewStock stock)
        {
            if (stock.Waters.Count == 0) return "没有够一瓶（749ml）的干净水。";

            int[] counts = new int[6];
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                BrewWater w = stock.Waters[i];
                counts[w.Tier] += w.Ml / BrewScan.WaterPerBottle;
            }

            StringBuilder sb = new StringBuilder("每瓶 749ml。现有：");
            bool any = false;
            for (int t = 0; t < 6; t++)
            {
                if (counts[t] <= 0) continue;
                if (any) sb.Append(" · ");
                sb.Append(BrewScan.TierName(t)).Append(" ").Append(counts[t]).Append(" 份");
                any = true;
            }
            if (!any) sb.Append("没有");
            return sb.ToString();
        }

        private static string BerryDetail(BrewStock stock)
        {
            if (stock.Berries.Count == 0) return "没有荧光莓。发霉的不能酿酒。";

            StringBuilder sb = new StringBuilder("发霉的不参与。现有：");
            bool any = false;
            for (int i = 0; i < stock.Berries.Count; i++)
            {
                BrewBerry berry = stock.Berries[i];
                if (berry.Grade == BerryGrade.Mold) continue;
                if (any) sb.Append(" · ");
                sb.Append(BerryGrade.Label[berry.Grade]).Append(" ").Append(berry.Count);
                any = true;
            }
            if (!any) sb.Append("全是发霉的");
            return sb.ToString();
        }

        private static string HintText(BrewStock stock, int totalValue)
        {
            string key = Brewing.HotkeyName;
            if (string.IsNullOrEmpty(key)) return "已关闭热键，可在模组设置里打开";
            return "按 " + key + " 开关本面板";
        }

        // ---------------------------------------------------------------
        // 控件小工具
        // ---------------------------------------------------------------

        private static CustomUIElement Text(CustomUIBuilder b, string text, float width, float size, Color color)
        {
            try
            {
                b.AddLabel(text, null);
                CustomUIElement el = b.Last;
                if (el == null) return null;

                if (width > 0f) FixSize(el, width, 0f);

                if (el.text != null)
                {
                    el.text.fontSize = size;
                    el.text.color = color;
                    el.text.enableWordWrapping = false;
                    el.text.overflowMode = TextOverflowModes.Ellipsis;
                    el.text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                    el.text.verticalAlignment = VerticalAlignmentOptions.Middle;
                }
                return el;
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 文本「" + text + "」创建失败：" + e.GetType().Name);
                return null;
            }
        }

        private static void Button(CustomUIBuilder b, string text, float width, Action onClick, bool enabled)
        {
            try
            {
                b.AddButton(text, onClick, null);
                CustomUIElement el = b.Last;
                if (el == null) return;

                FixSize(el, width, BtnH);
                if (el.text != null)
                {
                    el.text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                    el.text.verticalAlignment = VerticalAlignmentOptions.Middle;
                    el.text.enableWordWrapping = false;
                    el.text.overflowMode = TextOverflowModes.Ellipsis;
                }
                if (!enabled) el.SetInteractable(false);
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 按钮「" + text + "」创建失败：" + e.GetType().Name);
            }
        }

        private static void FixSize(CustomUIElement el, float width, float height)
        {
            if (el == null || el.root == null) return;

            UnityEngine.UI.LayoutElement layout = el.root.GetComponent<UnityEngine.UI.LayoutElement>();
            if (layout == null) layout = el.root.AddComponent<UnityEngine.UI.LayoutElement>();

            if (width > 0f)
            {
                layout.preferredWidth = width;
                layout.flexibleWidth = 0f;
            }
            if (height > 0f)
            {
                layout.minHeight = height;
                layout.preferredHeight = height;
            }
        }

        private static CustomUIElement SetAlignment(this CustomUIElement el, HorizontalAlignmentOptions align)
        {
            if (el == null || el.text == null) return el;
            el.text.horizontalAlignment = align;
            return el;
        }

        // ---------------------------------------------------------------
        // 窗口位置
        // ---------------------------------------------------------------

        private static void Remember(CustomUIManager ui)
        {
            try
            {
                CustomUIWindow win = ui.GetWindow(WindowId);
                if (win == null || win.Rect == null) return;
                _pos = win.Rect.anchoredPosition;
                _hasPos = true;
                _posLoaded = true;
                PlayerPrefs.SetFloat(KeyX, _pos.x);
                PlayerPrefs.SetFloat(KeyY, _pos.y);
                PlayerPrefs.Save();
            }
            catch { }
        }

        private static void ApplyPosition(CustomUIWindow win)
        {
            try
            {
                if (win == null || win.Rect == null) return;

                if (!_posLoaded)
                {
                    _posLoaded = true;
                    _hasPos = PlayerPrefs.HasKey(KeyX) || PlayerPrefs.HasKey(KeyY);
                    _pos = new Vector2(PlayerPrefs.GetFloat(KeyX, 0f), PlayerPrefs.GetFloat(KeyY, 0f));
                }

                if (_hasPos) win.Rect.anchoredPosition = _pos;
            }
            catch { }
        }
    }
}
