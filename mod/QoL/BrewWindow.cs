using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 酿酒工坊的窗口。
    ///
    /// 走游戏自带的 CustomUI + Style.Machinery（2× 像素皮肤，和游戏里的机器面板同一套）。
    ///
    /// 布局：
    ///   1) 选择器：酵母 / 水质 / 空瓶
    ///   2) 自选搭配：四种品相的果子各点一下加一颗 + 实时结算
    ///   3) 分配方式：高品质优先（默认）或 多瓶优先
    ///   4) 可酿表：六种酒 + 走私酒，各能酿几瓶、缺什么、每瓶配方与发酵夜数
    ///   5) 一键酿造
    ///
    /// 表里的瓶数是「一次分完」的结果 —— 材料是共用的，各档不能叠加。
    /// </summary>
    internal static class BrewWindow
    {
        private const string WindowId = "qol.brew";
        private const string Title = "酿酒工坊";

        private const float WinW = 880f;

        /// <summary>窗口固定高度：外框 58 + 四行（选择/自选/预计/分配）+ 表头 + 七行 + 底行 + 分隔与间距。</summary>
        private const float WinH = 590f;

        private const float Pad = 8f;
        private const float BtnH = 32f;
        private const float Gap = 6f;

        // 表格列宽：772 + 5 个间距 30 = 802，塞进 848 的可用宽度
        private const float ColName = 150f;
        private const float ColScore = 66f;
        private const float ColRecipe = 240f;
        private const float ColDays = 66f;
        private const float ColCount = 160f;
        private const float ColAct = 90f;

        private const int MaxBerriesPerBottle = 10;

        private static readonly Color Body = MachineryStyle.BODY_TEXT;
        private static readonly Color Value = MachineryStyle.VALUE_TEXT;
        private static readonly Color DimColor = new Color(0.63f, 0.63f, 0.58f);
        private static readonly Color OkColor = MachineryStyle.PROGRESS_FILL;
        private static readonly Color WarnColor = MachineryStyle.PROGRESS_FILL_WARNING;
        private static readonly Color BadColor = MachineryStyle.PROGRESS_FILL_DANGER;
        private static readonly Color GoldColor = new Color(0.925f, 0.78f, 0.35f);

        // 按钮底色是米黄，文字只能压深
        private static readonly Color[] GradeInk =
        {
            new Color(0.16f, 0.40f, 0.15f),   // 优质
            new Color(0.20f, 0.20f, 0.18f),   // 普通
            new Color(0.52f, 0.32f, 0.06f),   // 干瘪
            new Color(0.55f, 0.14f, 0.12f)    // 发霉
        };

        private const string KeyYeast = "qol.brew.yeast";
        private const string KeyQuality = "qol.brew.quality";
        private const string KeyX = "qol.brew.x";
        private const string KeyY = "qol.brew.y";

        private static bool _posLoaded;
        private static bool _hasPos;
        private static Vector2 _pos;

        private static string _preferred;
        private static bool _preferredLoaded;

        private static bool _qualityFirst = true;
        private static bool _qualityLoaded;

        private static int _waterTier = -1;          // -1 = 自动
        private static readonly int[] _mix = new int[4];

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
            NormalizeSelection(stock);

            List<BrewYeast> yeastOptions = YeastOptions(stock);
            int waterTier = EffectiveTier(stock);

            string note;
            List<BrewTierRow> rows = BrewAllocator.Plan(stock, QualityFirst, PlannerYeast(stock),
                _waterTier, YeastPower(stock), out note);

            int total = 0;
            for (int i = 0; i < rows.Count; i++) total += rows[i].Count;

            CustomUIBuilder b = ui.CreateWindow(WindowId, Title, "overlay", null, CustomUIManager.Style.Machinery)
                .SetSize(WinW, WinH)
                .SetDraggable(true)
                .SetCloseOnEscape(true);

            b.BeginColumn(Pad, TextAnchor.UpperLeft);

            BuildSelectors(b, stock, yeastOptions, waterTier);
            BuildMix(b, stock);
            BuildPreview(b, stock, waterTier);

            b.AddSeparator();

            BuildStrategy(b, total);
            BuildTable(b, rows, stock);

            b.AddSeparator();

            BuildFooter(b, total, rows);

            b.End();

            CustomUIWindow win = b.Show();
            ApplyPosition(win);
        }

        // ---------------------------------------------------------------
        // 1) 选择器：酵母 / 水质 / 空瓶
        // ---------------------------------------------------------------

        private static void BuildSelectors(CustomUIBuilder b, BrewStock stock, List<BrewYeast> options, int waterTier)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            const string yeastTip = "点 < > 换酵母。酵母种类不加分，只决定发酵要几夜：力量 4 一夜，力量 2 两夜。合成酵母是走私酒专用。";
            const string waterTip = "点 < > 换水质，整张表都跟着算。纯水/优质水 +1 分，基础水 0，幽灵水 -1，铁锈水 -2，沟渠水 -3；「自动」＝按配方自己挑最合适的。";

            Text(b, "酵母", 52f, 14f, DimColor);
            Button(b, "<", 38f, delegate { CycleYeast(-1); }, options.Count > 0, yeastTip);
            Text(b, YeastLabel(stock), 168f, 14f, GoldColor).SetAlignment(HorizontalAlignmentOptions.Center);
            Button(b, ">", 38f, delegate { CycleYeast(1); }, options.Count > 0, yeastTip);

            Text(b, "水质", 52f, 14f, DimColor);
            Button(b, "<", 38f, delegate { CycleWater(-1); }, true, waterTip);
            Text(b, WaterLabel(stock, waterTier), 186f, 14f, Body).SetAlignment(HorizontalAlignmentOptions.Center);
            Button(b, ">", 38f, delegate { CycleWater(1); }, true, waterTip);

            Text(b, "空瓶", 52f, 14f, DimColor);
            Text(b, "×" + stock.EmptyBottles.Count, 72f, 15f,
                stock.EmptyBottles.Count > 0 ? Value : BadColor);

            b.End();
        }

        // ---------------------------------------------------------------
        // 2) 自选搭配
        // ---------------------------------------------------------------

        private static void BuildMix(CustomUIBuilder b, BrewStock stock)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            Text(b, "自选", 44f, 14f, DimColor);

            for (int grade = 0; grade < 4; grade++)
            {
                int g = grade;
                int avail = stock.BerryCount(g);
                string label = BerryGrade.Label[g] + " ×" + _mix[g] + " / " + avail;

                ButtonColored(b, label, 150f, delegate { CycleBerry(g, avail); }, avail > 0,
                    _mix[g] > 0 ? GradeInk[g] : new Color(0.35f, 0.33f, 0.31f), BerryTip(g));
            }

            Button(b, "清空", 64f, delegate
            {
                for (int i = 0; i < 4; i++) _mix[i] = 0;
                Brewing.Rescan();
            }, HasMix());

            b.End();
        }

        // ---------------------------------------------------------------
        // 3) 实时结算
        // ---------------------------------------------------------------

        private static void BuildPreview(CustomUIBuilder b, BrewStock stock, int waterTier)
        {
            BrewCombo combo = ManualCombo(stock, waterTier);
            string problem = Problem(stock, combo, waterTier);

            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            string text;
            Color color;
            if (!string.IsNullOrEmpty(problem))
            {
                text = "预计：—　" + problem;
                color = WarnColor;
            }
            else if (combo.Counterfeit)
            {
                text = "预计：走私酒「" + WineGrade.CounterfeitName + "」· 售价低、算违禁品 · 发酵 " +
                       BrewScore.Days(YeastPower(stock)) + " 夜";
                color = BadColor;
            }
            else
            {
                int score = BrewScore.Of(combo.Grades, stock.Yeasts.Count > 0, waterTier);
                text = "预计：" + score + " 分 · " + WineGrade.NameOf(score) + " · 发酵 " +
                       BrewScore.Days(YeastPower(stock)) + " 夜";
                color = WineGrade.ColorOf(score);

                if (score > WineGrade.TopTierScore) text += "（≥6 分同档，再加果子不加价）";
            }

            CustomUIElement label = Text(b, text, -1f, 15f, color);
            if (label != null)
            {
                label.SetFlexible(1f, 0f);
                if (label.text != null)
                {
                    label.text.enableWordWrapping = false;
                    label.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }

            BrewCombo captured = combo;
            Button(b, "按这套酿一瓶", 170f, delegate { Brewing.BrewSingle(captured); },
                string.IsNullOrEmpty(problem));
            b.End();
        }

        // ---------------------------------------------------------------
        // 4) 分配方式
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
        // 5) 可酿表
        // ---------------------------------------------------------------

        private static void BuildTable(CustomUIBuilder b, List<BrewTierRow> rows, BrewStock stock)
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
            for (int i = 0; i < rows.Count; i++) BuildTierRow(b, rows[i], stock);
            b.End();
        }

        private static void BuildTierRow(CustomUIBuilder b, BrewTierRow row, BrewStock stock)
        {
            bool ok = row.Count > 0;

            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            Color nameColor = row.Counterfeit ? BadColor : WineGrade.ColorOfTier(row.Tier);
            if (!ok) nameColor = new Color(nameColor.r * 0.55f, nameColor.g * 0.55f, nameColor.b * 0.55f);

            CustomUIElement name = Text(b, row.Name, ColName, 15f, nameColor);
            if (name != null) name.SetTooltip(RowTip(row));

            Text(b, row.ScoreText, ColScore, 15f, ok ? nameColor : DimColor);
            Text(b, row.RecipeText, ColRecipe, 14f, ok ? Body : DimColor);
            Text(b, ok ? row.Days + " 夜" : "—", ColDays, 14f, ok ? Body : DimColor);

            if (ok) Text(b, "×" + row.Count, ColCount, 16f, GoldColor);
            else Text(b, row.Reason, ColCount, 13f, new Color(0.60f, 0.47f, 0.45f));

            BrewTierRow captured = row;
            Button(b, ok ? "酿 " + row.Count : "酿", ColAct, delegate { Brewing.BrewTier(captured); }, ok);

            b.End();
        }

        private static string RowTip(BrewTierRow row)
        {
            if (row.Count <= 0) return row.Name + "：" + (row.Reason ?? "现在做不出来");

            string recipe = row.RecipeText;
            string text = "每瓶要：" + recipe + " + 酵母×1\n" +
                          "可酿：" + row.Count + " 瓶\n" +
                          "发酵：" + row.Days + " 夜（酵母力量决定）\n" +
                          "分数：" + row.ScoreText;

            if (row.Counterfeit) text += "\n走私酒：算违禁品，治安检查会查，售价也低";
            else if (row.Tier >= 4) text += "\n挂「奢侈」标签，顾客更容易收";
            else if (row.Tier == 0) text += "\n不能喝、也卖不掉，只能倒掉";

            return text;
        }

        // ---------------------------------------------------------------
        // 6) 底部
        // ---------------------------------------------------------------

        private static void BuildFooter(CustomUIBuilder b, int total, List<BrewTierRow> rows)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            CustomUIElement hint = Text(b, HintText(), -1f, 13f, DimColor);
            if (hint != null)
            {
                hint.SetFlexible(1f, 0f);
                if (hint.text != null)
                {
                    hint.text.enableWordWrapping = false;
                    hint.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }

            Button(b, "刷新", 90f, Brewing.Rescan, true, "材料有变动（从箱子拿果子、倒水、做瓶）后点一下重新算");

            List<BrewTierRow> captured = rows;
            Button(b, total > 0 ? "一键酿造（" + total + " 瓶）" : "一键酿造", 210f,
                delegate { Brewing.BrewEverything(captured); }, total > 0);

            b.End();
        }

        private static string HintText()
        {
            string key = Brewing.HotkeyName;
            if (string.IsNullOrEmpty(key)) return "热键已关，可在模组设置里打开";
            return "按 " + key + " 开关本面板 · 表里的瓶数是一次分完的结果，各档不能叠加";
        }

        // ---------------------------------------------------------------
        // 选择状态
        // ---------------------------------------------------------------

        private static List<BrewYeast> YeastOptions(BrewStock stock)
        {
            List<BrewYeast> normal = new List<BrewYeast>();
            List<BrewYeast> illegal = new List<BrewYeast>();

            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                BrewYeast y = stock.Yeasts[i];
                if (y.Items.Count == 0) continue;
                if (y.Synthetic) illegal.Add(y);
                else normal.Add(y);
            }

            normal.AddRange(illegal);
            return normal;
        }

        private static void NormalizeSelection(BrewStock stock)
        {
            if (!string.IsNullOrEmpty(_preferred))
            {
                BrewYeast y = stock.Yeast(_preferred);
                if (y == null || y.Items.Count == 0) _preferred = string.Empty;
            }

            if (_waterTier >= 0 && stock.WaterBottles(_waterTier) <= 0) _waterTier = -1;

            for (int g = 0; g < 4; g++)
            {
                int avail = stock.BerryCount(g);
                if (_mix[g] > avail) _mix[g] = avail;
                if (_mix[g] < 0) _mix[g] = 0;
            }
        }

        /// <summary>规划用酵母：选了合成酵母时不给规划，让表继续按普通酵母算。</summary>
        private static string PlannerYeast(BrewStock stock)
        {
            BrewYeast y = stock.Yeast(_preferred);
            if (y != null && y.Synthetic) return string.Empty;
            return PreferredYeast;
        }

        private static int EffectiveTier(BrewStock stock)
        {
            if (_waterTier >= 0 && stock.WaterBottles(_waterTier) > 0) return _waterTier;
            return Brewing.BestWaterTier(stock);
        }

        private static bool IsSyntheticSelected(BrewStock stock)
        {
            BrewYeast y = stock.Yeast(_preferred);
            return y != null && y.Synthetic;
        }

        private static int YeastPower(BrewStock stock)
        {
            BrewYeast y = stock.Yeast(_preferred);
            if (y != null) return y.Power;

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

        private static BrewCombo ManualCombo(BrewStock stock, int waterTier)
        {
            BrewCombo combo = new BrewCombo();
            combo.Counterfeit = IsSyntheticSelected(stock);
            combo.YeastId = _preferred;
            combo.WaterTier = waterTier;

            if (!combo.Counterfeit)
            {
                for (int g = 0; g < 4; g++)
                {
                    for (int n = 0; n < _mix[g]; n++) combo.Grades.Add(g);
                }
            }
            return combo;
        }

        private static string Problem(BrewStock stock, BrewCombo combo, int waterTier)
        {
            if (stock.EmptyBottles.Count <= 0) return "没有空酒瓶";

            if (combo.Counterfeit)
            {
                if (waterTier >= 0 && waterTier < 3) return "走私酒要用污染水（幽灵 / 铁锈 / 沟渠）";
                if (waterTier < 0) return "没有够一瓶的水";
                return string.Empty;
            }

            if (combo.Grades.Count == 0) return "先在下面点几种果子";
            if (combo.Grades.Count > MaxBerriesPerBottle) return "果子最多放 " + MaxBerriesPerBottle + " 颗";
            if (waterTier < 0) return "没有够一瓶（≥749ml）的水";
            if (string.IsNullOrEmpty(_preferred) && stock.Yeasts.Count == 0) return "没有酵母";

            return string.Empty;
        }

        private static bool HasMix()
        {
            for (int i = 0; i < 4; i++) if (_mix[i] > 0) return true;
            return false;
        }

        private static int MixTotal()
        {
            int n = 0;
            for (int i = 0; i < 4; i++) n += _mix[i];
            return n;
        }

        private static string BerryTip(int grade)
        {
            switch (grade)
            {
                case BerryGrade.Small:
                    return "优质果（小果）：每颗 +2 分。点一下放一颗，到库存上限再点归零。";
                case BerryGrade.Standard:
                    return "普通果：每颗 +1 分。放两颗以上还会额外 +1。";
                case BerryGrade.Waste:
                    return "干瘪果（未熟/过熟）：0 分，只用来凑「双果 +1」。";
                default:
                    return "发霉果：每颗 -1 分，高品质模式不会用它。";
            }
        }

        // ---------------------------------------------------------------
        // 点击行为
        // ---------------------------------------------------------------

        private static void CycleBerry(int grade, int avail)
        {
            if (avail <= 0) return;

            if (_mix[grade] >= avail) { _mix[grade] = 0; Brewing.Rescan(); return; }
            if (MixTotal() >= MaxBerriesPerBottle) return;

            _mix[grade]++;
            Brewing.Rescan();
        }

        private static void CycleYeast(int dir)
        {
            try
            {
                BrewStock stock = BrewScan.Scan();
                List<BrewYeast> options = YeastOptions(stock);

                int count = options.Count + 1;
                int index = 0;
                for (int i = 0; i < options.Count; i++)
                {
                    if (string.Equals(options[i].Id, _preferred, StringComparison.Ordinal)) { index = i + 1; break; }
                }

                index = ((index + dir) % count + count) % count;

                _preferred = index == 0 ? string.Empty : options[index - 1].Id;
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

        private static void CycleWater(int dir)
        {
            try
            {
                BrewStock stock = BrewScan.Scan();

                List<int> options = new List<int>();
                options.Add(-1);
                for (int t = 0; t <= 5; t++)
                {
                    if (stock.WaterBottles(t) > 0) options.Add(t);
                }

                int index = options.IndexOf(_waterTier);
                if (index < 0) index = 0;

                index = ((index + dir) % options.Count + options.Count) % options.Count;
                _waterTier = options[index];

                Brewing.Rescan();
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 切换水质失败：" + e.GetType().Name);
            }
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
        // 文案
        // ---------------------------------------------------------------

        private static string YeastLabel(BrewStock stock)
        {
            BrewYeast y = stock.Yeast(_preferred);
            if (y == null) return "《自动》";

            string amount = y.Reusable ? "无限" : "×" + y.Count;
            return y.Name + " " + amount;
        }

        private static string WaterLabel(BrewStock stock, int tier)
        {
            if (tier < 0) return "《无可用的水》";

            int ml = stock.WaterMl(tier);
            int bottles = stock.WaterBottles(tier);
            string name = BrewScan.TierName(tier);
            if (_waterTier < 0) name = "自动·" + name;

            return name + " " + ml + "ml（" + bottles + " 瓶）";
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

        private static void Button(CustomUIBuilder b, string text, float width, Action onClick, bool enabled, string tooltip = null)
        {
            ButtonColored(b, text, width, onClick, enabled, null, tooltip);
        }

        private static void ButtonColored(CustomUIBuilder b, string text, float width, Action onClick, bool enabled,
            Color? textColor, string tooltip = null)
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
                    if (textColor.HasValue) el.text.color = textColor.Value;
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
