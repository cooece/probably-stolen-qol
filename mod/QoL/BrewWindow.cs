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
    /// 走游戏自带的 CustomUI + Style.Machinery（2× 像素皮肤，和游戏里的机器面板同一套）。
    ///
    /// 界面只有三块：
    ///   1) 材料条：现在手里有什么（只读，悬停看明细）
    ///   2) 分法开关：高品质优先 / 多瓶优先
    ///   3) 可酿表：六种酒 + 走私酒，各能酿几瓶、每瓶配方、发酵几夜、缺什么
    /// 底部：刷新 + 一键酿造。
    ///
    /// 表里的「可酿」是一次分完的结果（材料共用，各档不能相加）；
    /// 没分到但材料够的档位会显示配方，并且可以单独酿一瓶。
    /// </summary>
    internal static class BrewWindow
    {
        private const string WindowId = "qol.brew";
        private const string Title = "酿酒工坊";

        private const float WinW = 880f;

        /// <summary>外框 58 + 材料条 + 分法 + 表头 + 七行 + 底栏 + 间距。</summary>
        private const float WinH = 512f;

        private const float Pad = 8f;
        private const float BtnH = 32f;
        private const float Gap = 6f;

        // 表列宽：706 + 5 个间距 30 = 736
        private const float ColName = 150f;
        private const float ColScore = 66f;
        private const float ColRecipe = 200f;
        private const float ColDays = 60f;
        private const float ColCount = 130f;
        private const float ColAct = 100f;

        private static readonly Color Body = MachineryStyle.BODY_TEXT;
        private static readonly Color Value = MachineryStyle.VALUE_TEXT;
        private static readonly Color DimColor = new Color(0.63f, 0.63f, 0.58f);
        private static readonly Color OkColor = MachineryStyle.PROGRESS_FILL;
        private static readonly Color BadColor = MachineryStyle.PROGRESS_FILL_DANGER;
        private static readonly Color GoldColor = new Color(0.925f, 0.78f, 0.35f);

        private const string KeyQuality = "qol.brew.quality";
        private const string KeyX = "qol.brew.x";
        private const string KeyY = "qol.brew.y";

        private static bool _posLoaded;
        private static bool _hasPos;
        private static Vector2 _pos;

        private static bool _qualityFirst = true;
        private static bool _qualityLoaded;

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

        private static bool QualityFirst
        {
            get
            {
                if (!_qualityLoaded)
                {
                    _qualityLoaded = true;
                    try { _qualityFirst = PlayerPrefs.GetInt(KeyQuality, 1) != 0; }
                    catch { _qualityFirst = true; }
                }
                return _qualityFirst;
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
            List<BrewTierRow> rows = BrewAllocator.Plan(stock, QualityFirst, YeastPower(stock), out note);

            int total = 0;
            for (int i = 0; i < rows.Count; i++) total += rows[i].Count;

            CustomUIBuilder b = ui.CreateWindow(WindowId, Title, "overlay", null, CustomUIManager.Style.Machinery)
                .SetSize(WinW, WinH)
                .SetDraggable(true)
                .SetCloseOnEscape(true);

            b.BeginColumn(Pad, TextAnchor.UpperLeft);

            BuildMaterials(b, stock);
            b.AddSeparator();
            BuildStrategy(b, total);
            BuildTable(b, rows);

            b.AddSeparator();
            BuildFooter(b, total, rows);

            b.End();

            CustomUIWindow win = b.Show();
            ApplyPosition(win);
        }

        // ---------------------------------------------------------------
        // 1) 材料条（只读）
        // ---------------------------------------------------------------

        private static void BuildMaterials(CustomUIBuilder b, BrewStock stock)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            Text(b, "材料", 44f, 14f, DimColor);

            CustomUIElement bar = Text(b, MaterialText(stock), -1f, 14f, Body);
            if (bar != null)
            {
                bar.SetFlexible(1f, 0f);
                if (bar.text != null)
                {
                    bar.text.enableWordWrapping = false;
                    bar.text.overflowMode = TextOverflowModes.Ellipsis;
                }
                bar.SetTooltip(MaterialTip(stock));
            }

            b.End();
        }

        private static string MaterialText(BrewStock stock)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append(C(stock.EmptyBottles.Count > 0 ? "#E6C25A" : "#CC4849", "空瓶 " + stock.EmptyBottles.Count));

            sb.Append(D("   水 "));
            bool anyWater = false;
            for (int tier = 0; tier <= 5; tier++)
            {
                int bottles = stock.WaterBottles(tier);
                if (bottles <= 0) continue;
                if (anyWater) sb.Append(D(" · "));
                sb.Append(C(tier <= 1 ? "#7FD3E0" : (tier >= 3 ? "#B98BE0" : "#C4C4B0"),
                            BrewScan.TierName(tier) + " " + bottles + " 瓶"));
                anyWater = true;
            }
            if (!anyWater) sb.Append(C("#CC4849", "无"));

            sb.Append(D("   果 "));
            string[] ink = { "#8FD46A", "#D9D6C2", "#DBA873", "#D97874" };
            bool anyBerry = false;
            for (int g = 0; g < 4; g++)
            {
                int n = stock.BerryCount(g);
                if (n <= 0) continue;
                if (anyBerry) sb.Append(D(" "));
                sb.Append(C(ink[g], BerryGrade.Label[g] + " " + n));
                anyBerry = true;
            }
            if (!anyBerry) sb.Append(C("#CC4849", "无"));

            sb.Append(D("   酵母 "));
            if (stock.Yeasts.Count == 0)
            {
                sb.Append(C("#CC4849", "无"));
            }
            else
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (i > 0) sb.Append(D(" · "));
                    string amount = y.Reusable ? "无限" : "×" + y.Count;
                    sb.Append(C(y.Synthetic ? "#CC4849" : "#B79BF0", y.Name + " " + amount));
                }
            }

            return sb.ToString();
        }

        private static string MaterialTip(BrewStock stock)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("空酒瓶：").Append(stock.EmptyBottles.Count).Append(" 个");
            if (stock.WineBottles.Count > 0)
            {
                sb.Append("（另有发酵中 ").Append(stock.Fermenting).Append(" · 已酿好 ").Append(stock.Finished).Append("）");
            }

            sb.Append("\n水（每瓶要 749ml）：");
            if (stock.Waters.Count == 0) sb.Append("没有");
            for (int tier = 0; tier <= 5; tier++)
            {
                int ml = stock.WaterMl(tier);
                if (ml <= 0) continue;
                sb.Append("\n  ").Append(BrewScan.TierName(tier)).Append(" ").Append(ml).Append("ml（")
                  .Append(stock.WaterBottles(tier)).Append(" 瓶）");
            }

            sb.Append("\n荧光莓：");
            bool any = false;
            for (int g = 0; g < 4; g++)
            {
                int n = stock.BerryCount(g);
                if (n <= 0) continue;
                sb.Append(any ? " · " : "").Append(BerryGrade.Label[g]).Append(" ").Append(n);
                any = true;
            }
            if (!any) sb.Append("没有");

            sb.Append("\n酵母：");
            if (stock.Yeasts.Count == 0) sb.Append("没有");
            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                BrewYeast y = stock.Yeasts[i];
                sb.Append("\n  ").Append(y.Name);
                if (y.Reusable) sb.Append(" 无限使用");
                else sb.Append(" ×").Append(y.Count);
                sb.Append("（力量 ").Append(y.Power).Append("，").Append(BrewScore.Days(y.Power)).Append(" 夜）");
            }

            return sb.ToString();
        }

        // ---------------------------------------------------------------
        // 2) 分法
        // ---------------------------------------------------------------

        private static void BuildStrategy(CustomUIBuilder b, int total)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            Text(b, "分法", 44f, 14f, DimColor);

            try
            {
                b.AddToggle("高品质优先", QualityFirst, OnQualityChanged, null);
                FixSize(b.Last, 170f, BtnH);
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 分配方式开关创建失败：" + e.GetType().Name);
            }

            CustomUIElement hint = Text(b, QualityFirst
                ? "先凑高分：普通+优质 → 6 分，发霉果不参与"
                : "一颗果一瓶：瓶数最多，档次会低一些", -1f, 13f, DimColor);
            if (hint != null)
            {
                hint.SetFlexible(1f, 0f);
                if (hint.text != null)
                {
                    hint.text.enableWordWrapping = false;
                    hint.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }

            Text(b, total > 0 ? "共可酿 " + total + " 瓶" : "一瓶也酿不了", 150f, 14f,
                total > 0 ? OkColor : BadColor).SetAlignment(HorizontalAlignmentOptions.Right);

            b.End();
        }

        // ---------------------------------------------------------------
        // 3) 可酿表
        // ---------------------------------------------------------------

        private static void BuildTable(CustomUIBuilder b, List<BrewTierRow> rows)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            Text(b, "品质", ColName, 13f, DimColor);
            Text(b, "分数", ColScore, 13f, DimColor);
            Text(b, "每瓶配方", ColRecipe, 13f, DimColor);
            Text(b, "发酵", ColDays, 13f, DimColor);
            Text(b, "可酿", ColCount, 13f, DimColor);
            Text(b, "操作", ColAct, 13f, DimColor);
            b.End();

            b.BeginColumn(Pad, TextAnchor.UpperLeft);
            for (int i = 0; i < rows.Count; i++) BuildTierRow(b, rows[i]);
            b.End();
        }

        private static void BuildTierRow(CustomUIBuilder b, BrewTierRow row)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            Color nameColor = row.Counterfeit ? BadColor : WineGrade.ColorOfTier(row.Tier);
            if (!row.Feasible) nameColor = new Color(nameColor.r * 0.5f, nameColor.g * 0.5f, nameColor.b * 0.5f);

            CustomUIElement name = Text(b, row.Name, ColName, 15f, nameColor);
            if (name != null) name.SetTooltip(RowTip(row));

            Color dim = row.Feasible ? Body : DimColor;
            Text(b, row.ScoreText, ColScore, 15f, row.Feasible ? nameColor : DimColor);
            Text(b, row.RecipeText, ColRecipe, 14f, dim);
            Text(b, row.DaysText, ColDays, 14f, dim);

            if (row.Count > 0) Text(b, "×" + row.Count, ColCount, 16f, GoldColor);
            else if (row.Feasible) Text(b, "0", ColCount, 15f, DimColor);
            else Text(b, row.Reason ?? "做不了", ColCount, 13f, new Color(0.60f, 0.47f, 0.45f));

            BrewTierRow captured = row;
            if (row.Count > 0)
            {
                Button(b, "酿 " + row.Count, ColAct, delegate { Brewing.BrewTier(captured); }, true,
                    "把这一档分到的 " + row.Count + " 瓶一次酿完");
            }
            else if (row.Feasible)
            {
                Button(b, "酿 1", ColAct, delegate { Brewing.BrewTier(captured); }, true,
                    "材料够做一瓶，单独酿一瓶这个档位");
            }
            else
            {
                Button(b, "酿", ColAct, null, false, row.Reason);
            }

            b.End();
        }

        private static string RowTip(BrewTierRow row)
        {
            if (!row.Feasible) return row.Name + "：" + (row.Reason ?? "现在做不出来");

            StringBuilder sb = new StringBuilder();
            sb.Append("每瓶要：").Append(row.RecipeText).Append(" + 酵母×1");
            sb.Append("\n分数：").Append(row.ScoreText).Append("　发酵：").Append(row.Days).Append(" 夜");
            sb.Append("\n当前分到：").Append(row.Count).Append(" 瓶");

            if (row.Counterfeit) sb.Append("\n走私酒：算违禁品，治安检查会查，售价也低");
            else if (row.Tier >= 4) sb.Append("\n挂「奢侈」标签，顾客更容易收");
            else if (row.Tier == 0) sb.Append("\n不能喝也卖不掉，只能倒掉");

            return sb.ToString();
        }

        // ---------------------------------------------------------------
        // 4) 底部
        // ---------------------------------------------------------------

        private static void BuildFooter(CustomUIBuilder b, int total, List<BrewTierRow> rows)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            CustomUIElement hint = Text(b,
                "「可酿」是按当前分法分到的；灰掉的档位材料够时也能单独酿 1 瓶", -1f, 13f, DimColor);
            if (hint != null)
            {
                hint.SetFlexible(1f, 0f);
                if (hint.text != null)
                {
                    hint.text.enableWordWrapping = false;
                    hint.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }

            Button(b, "刷新", 90f, Brewing.Rescan, true, "材料有变动（拿果子、倒水、做瓶）后点一下重新算");

            List<BrewTierRow> captured = rows;
            Button(b, total > 0 ? "一键酿造（" + total + " 瓶）" : "一键酿造", 210f,
                delegate { Brewing.BrewEverything(captured); }, total > 0,
                "按表格从上到下，把每一档分到的瓶数全酿了");

            b.End();
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        private static int YeastPower(BrewStock stock)
        {
            string[] order = { "wine_yeast_infinite", "wine_superyeast", "wine_yeast" };
            for (int i = 0; i < order.Length; i++)
            {
                BrewYeast pick = stock.Yeast(order[i]);
                if (pick != null && pick.Items.Count > 0) return pick.Power;
            }

            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                if (!stock.Yeasts[i].Synthetic && stock.Yeasts[i].Items.Count > 0) return stock.Yeasts[i].Power;
            }
            return 2;
        }

        private static string C(string color, string text)
        {
            return "<color=" + color + ">" + text + "</color>";
        }

        private static string D(string text)
        {
            return C("#8A8A80", text);
        }

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

        private static void Button(CustomUIBuilder b, string text, float width, Action onClick, bool enabled, string tooltip = null)
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
                if (!string.IsNullOrEmpty(tooltip))
                {
                    try { el.SetTooltip(tooltip); }
                    catch { }
                }
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

        private static void OnQualityChanged(bool on)
        {
            _qualityFirst = on;
            _qualityLoaded = true;
            try
            {
                PlayerPrefs.SetInt(KeyQuality, on ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch { }

            Brewing.Rescan();
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
