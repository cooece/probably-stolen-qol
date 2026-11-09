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

        // 表列宽：144+66+190+60+96+148 = 704 + 5 个间距 30 = 734
        private const float ColName = 144f;
        private const float ColScore = 66f;
        private const float ColRecipe = 190f;
        private const float ColDays = 60f;
        private const float ColCount = 96f;
        private const float ColAct = 148f;

        // 操作列里的 < N > 三个控件（按钮最小宽 32，见机械风 CreateButton）
        private const float ArrowW = 32f;
        private const float BrewW = ColAct - ArrowW * 2f - Gap * 2f;

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

        /// <summary>每一档用户自己选的瓶数（key = 档位；走私酒用 100+tier）。存的是用户原值，不随材料缩水。</summary>
        private static readonly Dictionary<int, int> _want = new Dictionary<int, int>();

        /// <summary>用户手动调过瓶数的档位：材料变动时不再自动跟随推荐。</summary>
        private static readonly HashSet<int> _touched = new HashSet<int>();

        /// <summary>当前这次构建出来的行控件，供 < > 局部刷新（不重建窗口）。</summary>
        private sealed class RowUi
        {
            internal BrewTierRow Row;
            internal CustomUIElement Name;
            internal CustomUIElement Less;
            internal CustomUIElement Brew;
            internal CustomUIElement More;
        }

        private static readonly List<RowUi> _rows = new List<RowUi>();
        private static CustomUIElement _footerHint;
        private static CustomUIElement _brewAll;
        private static int _materialTotal;

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
                else
                {
                    // 每次重新打开都按推荐重新起算，关窗期间的调整不保留
                    _want.Clear();
                    _touched.Clear();
                    Build(ui);
                }
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

            // 旧控件随窗口一起销毁，引用必须清掉，否则 < > 会改到已销毁的对象
            _rows.Clear();
            _footerHint = null;
            _brewAll = null;

            BrewStock stock = BrewScan.Scan();

            string note;
            List<BrewTierRow> rows = BrewAllocator.Plan(stock, QualityFirst, YeastPower(stock), out note);

            int total = 0;
            int wantTotal = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].Count;
                ApplyWant(rows[i]);
                wantTotal += rows[i].Want;
            }
            _materialTotal = total;

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
            BuildFooter(b, total, wantTotal, rows);

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

            Text(b, total > 0 ? "材料够 " + total + " 瓶" : "一瓶也酿不了", 150f, 14f,
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
            Tip(Text(b, "最多", ColCount, 13f, DimColor),
                "这一档材料全给它最多能做几瓶（也就是右边 < > 的上限）。" +
                "各档共用同一批材料，所以只作参考、不能相加。");
            Tip(Text(b, "自选瓶数", ColAct, 13f, DimColor),
                "用 < > 调这一档要酿几瓶，点中间的「酿 N」直接开酿。");
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

            if (row.Max > 0)
            {
                Text(b, row.Max.ToString(), ColCount, 15f,
                    row.Count > 0 ? GoldColor : (row.Feasible ? Value : DimColor));
            }
            else
            {
                Text(b, "—", ColCount, 15f, DimColor);
            }

            // 操作列：< 酿N >（自己选瓶数，点中间直接酿）
            BrewTierRow captured = row;
            bool canBrew = row.Want > 0;

            RowUi ui = new RowUi();
            ui.Row = row;
            ui.Name = name;
            _rows.Add(ui);

            ui.Less = Button(b, "<", ArrowW, delegate { Adjust(captured, -1); }, canBrew,
                "少酿一瓶", 16f, true);
            ui.Brew = Button(b, row.Want > 0 ? "酿 " + row.Want : "酿", BrewW,
                delegate { Brewing.BrewTier(captured); }, canBrew, BrewTip(row), 15f, true);
            ui.More = Button(b, ">", ArrowW, delegate { Adjust(captured, 1); }, row.Want < row.Max,
                row.Want >= row.Max ? "材料最多做到 " + row.Max + " 瓶" : "多酿一瓶", 16f, true);

            b.End();
        }

        private static string BrewTip(BrewTierRow row)
        {
            if (row.Want <= 0) return row.Reason ?? "这一档现在做不出来";
            return "按这一档的配方，一次酿 " + row.Want + " 瓶（这一档最多 " + row.Max + " 瓶）";
        }

        // ---------------------------------------------------------------
        // 自选瓶数
        // ---------------------------------------------------------------

        private static int RowKey(BrewTierRow row)
        {
            return row.Counterfeit ? 100 + row.Tier : row.Tier;
        }

        /// <summary>算出这一档当前的瓶数：用户调过就用他的，没调过就跟随推荐。</summary>
        private static void ApplyWant(BrewTierRow row)
        {
            int key = RowKey(row);

            int want;
            if (!_touched.Contains(key) || !_want.TryGetValue(key, out want))
            {
                // 没手动调过的档位每次都跟着推荐走：材料一变就重新播种
                want = row.Count > 0 ? row.Count : (row.Feasible ? 1 : 0);
                _want[key] = want;
            }

            // 只压「显示值」，不覆盖用户设的原值 —— 材料临时变少再变多时能回到原来的数字
            row.Want = Mathf.Clamp(want, 0, Math.Max(0, row.Max));
        }

        private static void Adjust(BrewTierRow row, int delta)
        {
            int key = RowKey(row);

            int want = Mathf.Clamp(row.Want + delta, 0, Math.Max(0, row.Max));
            _want[key] = want;
            _touched.Add(key);
            row.Want = want;

            // 只刷这一屏的数字和按钮状态，不重建窗口（重建会闪、连点会丢）
            RefreshWants();
        }

        /// <summary>局部刷新：所有行的「酿 N」、左右箭头的可用状态、底部统计，都不重建窗口。</summary>
        private static void RefreshWants()
        {
            int wantTotal = 0;

            for (int i = 0; i < _rows.Count; i++)
            {
                RowUi ui = _rows[i];
                BrewTierRow row = ui.Row;
                if (row == null) continue;

                bool canBrew = row.Want > 0;
                wantTotal += row.Want;

                if (ui.Brew != null)
                {
                    ui.Brew.SetText(row.Want > 0 ? "酿 " + row.Want : "酿");
                    ui.Brew.SetInteractable(canBrew);
                    Tip(ui.Brew, BrewTip(row));
                }

                if (ui.Name != null) Tip(ui.Name, RowTip(row));

                if (ui.Less != null) ui.Less.SetInteractable(canBrew);

                if (ui.More != null)
                {
                    ui.More.SetInteractable(row.Want < row.Max);
                    Tip(ui.More, row.Want >= row.Max ? "材料最多做到 " + row.Max + " 瓶" : "多酿一瓶");
                }
            }

            bool over = wantTotal > _materialTotal;

            if (_footerHint != null)
            {
                _footerHint.SetText(FooterText(wantTotal, over));
                if (_footerHint.text != null) _footerHint.text.color = over ? BadColor : DimColor;
            }

            if (_brewAll != null)
            {
                _brewAll.SetText(wantTotal > 0 ? "一键酿造（" + wantTotal + " 瓶）" : "一键酿造");
                _brewAll.SetInteractable(wantTotal > 0);
            }
        }

        private static string FooterText(int wantTotal, bool over)
        {
            return over
                ? "已选 " + wantTotal + " 瓶，材料只够 " + _materialTotal + " 瓶 —— 超出的会酿失败"
                : "已选 " + wantTotal + " 瓶（材料够 " + _materialTotal + " 瓶）　左右箭头调瓶数，点中间直接酿";
        }

        private static string RowTip(BrewTierRow row)
        {
            if (!row.Feasible) return row.Name + "：" + (row.Reason ?? "现在做不出来");

            StringBuilder sb = new StringBuilder();
            sb.Append("每瓶要：").Append(row.RecipeText).Append(" + 酵母×1");
            sb.Append("\n分数：").Append(row.ScoreText).Append("　发酵：").Append(row.Days).Append(" 夜");
            sb.Append("\n这一档最多能做：").Append(row.Max).Append(" 瓶");
            sb.Append("\n已选：").Append(row.Want).Append(" 瓶（用左右箭头调，点中间直接酿）");

            if (row.Counterfeit) sb.Append("\n走私酒：算违禁品，治安检查会查，售价也低");
            else if (row.Tier >= 4) sb.Append("\n挂「奢侈」标签，顾客更容易收");
            else if (row.Tier == 0) sb.Append("\n不能喝也卖不掉，只能倒掉");

            return sb.ToString();
        }

        // ---------------------------------------------------------------
        // 4) 底部
        // ---------------------------------------------------------------

        private static void BuildFooter(CustomUIBuilder b, int total, int wantTotal, List<BrewTierRow> rows)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            bool over = wantTotal > total;

            CustomUIElement hint = Text(b, FooterText(wantTotal, over), -1f, 13f, over ? BadColor : DimColor);
            if (hint != null)
            {
                hint.SetFlexible(1f, 0f);
                if (hint.text != null)
                {
                    hint.text.enableWordWrapping = false;
                    hint.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }
            _footerHint = hint;

            Button(b, "刷新", 90f, Brewing.Rescan, true, "材料有变动（拿果子、倒水、做瓶）后点一下重新算");

            List<BrewTierRow> captured = rows;
            _brewAll = Button(b, wantTotal > 0 ? "一键酿造（" + wantTotal + " 瓶）" : "一键酿造", 210f,
                delegate { Brewing.BrewEverything(captured); }, wantTotal > 0,
                "按表格从上到下，把每一档选好的瓶数全酿了");

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

        private static CustomUIElement Button(CustomUIBuilder b, string text, float width, Action onClick, bool enabled,
            string tooltip = null, float fontSize = 0f, bool compact = false)
        {
            try
            {
                b.AddButton(text, onClick, null);
                CustomUIElement el = b.Last;
                if (el == null) return null;

                FixSize(el, width, BtnH);
                if (el.text != null)
                {
                    el.text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                    el.text.verticalAlignment = VerticalAlignmentOptions.Middle;
                    el.text.enableWordWrapping = false;
                    el.text.overflowMode = TextOverflowModes.Ellipsis;
                    if (fontSize > 0f) el.text.fontSize = fontSize;
                }

                // 窄按钮（< >）：把机械风默认的 12px 左右内边距压掉，否则内容区只剩几像素、字被省略
                if (compact && el.root != null)
                {
                    UnityEngine.UI.HorizontalLayoutGroup group =
                        el.root.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                    if (group != null) group.padding = new RectOffset(2, 2, 0, 0);
                }

                if (!enabled) el.SetInteractable(false);
                if (!string.IsNullOrEmpty(tooltip))
                {
                    try { el.SetTooltip(tooltip); }
                    catch { }
                }
                return el;
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 按钮「" + text + "」创建失败：" + e.GetType().Name);
                return null;
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

        private static void Tip(CustomUIElement el, string text)
        {
            if (el == null || string.IsNullOrEmpty(text)) return;
            try { el.SetTooltip(text); }
            catch { }
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
