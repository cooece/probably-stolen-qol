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
    /// 走游戏自带的 CustomUI + Style.Machinery：皮肤本身就是 2× 像素 + 九宫格 bevel 框，
    /// 字清、边框锐，和游戏里的机器面板一套风格。
    ///
    /// 布局（自上而下）：
    ///   1) 三个选择器：酵母 / 水质 / 空瓶数
    ///   2) 自选搭配：四种品相的果子各点一下加一颗
    ///   3) 实时结算：分数 · 品名 · 发酵几夜 · 能不能酿（缺什么）
    ///   4) 推荐方案表
    ///   5) 自动模式 + 一键酿造
    /// </summary>
    internal static class BrewWindow
    {
        private const string WindowId = "qol.brew";
        private const string Title = "酿酒工坊";

        private const float WinW = 880f;

        /// <summary>除方案表以外的固定高度（外框 58 + 五行内容 + 两条分隔线 + 行距）。</summary>
        private const float FixedH = 320f;

        private const float Pad = 8f;
        private const float RowPitch = 40f;
        private const float BtnH = 32f;
        private const float Gap = 6f;

        // 方案表列宽：770 + 6 个间距 36 = 806，塞进 816 的可用宽度
        private const float ColBrew = 96f;
        private const float ColBerry = 200f;
        private const float ColYeast = 130f;
        private const float ColWater = 88f;
        private const float ColScore = 66f;
        private const float ColGrade = 130f;
        private const float ColCount = 60f;

        private const int RowsPerPage = 8;

        /// <summary>瓶里 6×2=12 格，酵母和果实各占一格，果子上限留一格余量。</summary>
        private const int MaxBerriesPerBottle = 10;

        private static readonly Color Body = MachineryStyle.BODY_TEXT;
        private static readonly Color Value = MachineryStyle.VALUE_TEXT;
        private static readonly Color DimColor = new Color(0.63f, 0.63f, 0.58f);
        private static readonly Color OkColor = MachineryStyle.PROGRESS_FILL;
        private static readonly Color WarnColor = MachineryStyle.PROGRESS_FILL_WARNING;
        private static readonly Color BadColor = MachineryStyle.PROGRESS_FILL_DANGER;
        private static readonly Color GoldColor = new Color(0.925f, 0.78f, 0.35f);
        // 按钮底色是米黄，文字只能压深，浅色会糊成一片
        private static readonly Color GradeColor0 = new Color(0.16f, 0.40f, 0.15f);   // 优质
        private static readonly Color GradeColor1 = new Color(0.20f, 0.20f, 0.18f);   // 普通
        private static readonly Color GradeColor2 = new Color(0.52f, 0.32f, 0.06f);   // 干瘪
        private static readonly Color GradeColor3 = new Color(0.55f, 0.14f, 0.12f);   // 发霉

        private const string KeyYeast = "qol.brew.yeast";
        private const string KeyX = "qol.brew.x";
        private const string KeyY = "qol.brew.y";
        private const string KeyAuto = "qol.brew.auto";

        private static readonly Color[] GradeColors = { GradeColor0, GradeColor1, GradeColor2, GradeColor3 };

        private static int _page;

        private static bool _posLoaded;
        private static bool _hasPos;
        private static Vector2 _pos;

        private static string _preferred;
        private static bool _preferredLoaded;

        private static int _waterTier = -1;          // -1 = 自动
        private static bool _autoMode = true;
        private static bool _autoLoaded;
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

        private static bool AutoMode
        {
            get
            {
                if (!_autoLoaded)
                {
                    _autoLoaded = true;
                    try { _autoMode = PlayerPrefs.GetInt(KeyAuto, 1) != 0; }
                    catch { _autoMode = true; }
                }
                return _autoMode;
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

            // 选择器的状态先和库存对齐（东西用完了就自动退回「自动」，别卡在空选项上）
            NormalizeSelection(stock);

            List<BrewYeast> yeastOptions = YeastOptions(stock);
            int waterTier = EffectiveTier(stock);

            string note;
            List<BrewPlan> plans = BrewPlanner.Recommend(stock, PlannerYeast(stock), waterTier, out note);
            List<BrewGroup> groups = BrewPlanner.Group(plans);

            int pageCount = Math.Max(1, (groups.Count + RowsPerPage - 1) / RowsPerPage);
            if (_page >= pageCount) _page = pageCount - 1;
            if (_page < 0) _page = 0;

            int first = _page * RowsPerPage;
            int visible = Math.Min(RowsPerPage, Math.Max(0, groups.Count - first));

            float listH = groups.Count == 0 ? 60f : Math.Max(1, visible) * RowPitch - Pad;
            float winH = FixedH + listH;

            CustomUIBuilder b = ui.CreateWindow(WindowId, Title, "overlay", null, CustomUIManager.Style.Machinery)
                .SetSize(WinW, winH)
                .SetDraggable(true)
                .SetCloseOnEscape(true);

            b.BeginColumn(Pad, TextAnchor.UpperLeft);

            BuildSelectors(b, stock, yeastOptions, waterTier);
            BuildMix(b, stock);
            BuildPreview(b, stock, waterTier);
            b.AddSeparator();

            // ---- 推荐方案 ----
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            b.AddHeader("推荐方案");
            FixSize(b.Last, 200f, 0f);

            CustomUIElement noteCell = Text(b, groups.Count > 0 ? note : "", -1f, 13f, DimColor);
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
            Button(b, "刷新库存", 130f, Brewing.Rescan, true);
            b.End();

            // ---- 列标题 ----
            b.BeginRow(Gap, TextAnchor.MiddleLeft);
            Text(b, "操作", ColBrew, 13f, DimColor);
            Text(b, "果实搭配", ColBerry, 13f, DimColor);
            Text(b, "酵母", ColYeast, 13f, DimColor);
            Text(b, "水质", ColWater, 13f, DimColor);
            Text(b, "评分", ColScore, 13f, DimColor);
            CustomUIElement gradeHead = Text(b, "品质", ColGrade, 13f, DimColor);
            if (gradeHead != null)
            {
                try
                {
                    gradeHead.SetTooltip("档位：≥6 分「臻品陈酿」、5 分「珍藏」、3~4 分「量产酒液」、2 分「自酿」、1 分「桶酿」。\n" +
                                         "6 分以上都算同档，再多放果子不加价。\n" +
                                         "非走私酒每过一晚陈化 +3 价值。");
                }
                catch { }
            }
            Text(b, "可酿", ColCount, 13f, DimColor);
            b.End();

            // ---- 方案表 ----
            // 机械风皮肤没有 ScrollView 预制体（CustomUIMachineryStyle 不生成），BeginScroll 会静默退化成
            // 普通列；所以这里直接用列 + 翻页，窗口高度按行数算，保证永远不溢出边框。
            b.BeginColumn(Pad, TextAnchor.UpperLeft);
            if (groups.Count == 0)
            {
                Note(b, string.IsNullOrEmpty(note) ? "没有可酿的配方。" : note, 800f, 15f, BadColor);
                for (int i = 0; i < stock.Notes.Count; i++) Note(b, stock.Notes[i], 800f, 13f, WarnColor);
            }
            else
            {
                for (int i = first; i < first + visible; i++) BuildRow(b, groups[i]);
            }
            b.End();

            b.AddSeparator();

            BuildFooter(b, stock, plans);
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
            const string waterTip = "点 < > 换水质。纯水/优质水 +1 分，基础水 0，幽灵水 -1，铁锈水 -2，沟渠水 -3；选「自动」用现有最好的水。";

            Text(b, "酵母", 52f, 14f, DimColor);
            Button(b, "<", 38f, delegate { CycleYeast(-1); }, options.Count > 0, yeastTip);
            Text(b, YeastLabel(stock, options), 168f, 14f, GoldColor).SetAlignment(HorizontalAlignmentOptions.Center);
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
                    _mix[g] > 0 ? GradeColors[g] : new Color(0.35f, 0.33f, 0.31f), BerryTip(g));
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
                string name = WineGrade.NameOf(score);
                text = "预计：" + score + " 分 · " + name + " · 发酵 " + BrewScore.Days(YeastPower(stock)) + " 夜";
                color = score >= WineGrade.TopScore ? GoldColor : WineGrade.ColorOf(score);

                if (score > WineGrade.TopScore) text += "（≥6 分同档，再加果子不加价）";
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
        // 4) 方案行
        // ---------------------------------------------------------------

        private static void BuildRow(CustomUIBuilder b, BrewGroup group)
        {
            BrewPlan plan = group.Sample;
            if (plan == null) return;

            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            BrewGroup captured = group;
            Button(b, "酿这瓶", ColBrew, delegate { Brewing.BrewSingle(captured.Sample.ToCombo()); }, true);

            Text(b, plan.Recipe.BerryText, ColBerry, 15f, plan.Recipe.HasBerry ? Body : DimColor);
            Text(b, plan.YeastName, ColYeast, 15f, Body);
            Text(b, plan.WaterName, ColWater, 15f, plan.Water != null && plan.Water.Dirty ? new Color(0.75f, 0.6f, 0.95f) : Body);
            Text(b, plan.Counterfeit ? "走私" : plan.Score + " 分", ColScore, 15f,
                plan.Counterfeit ? BadColor : WineGrade.ColorOf(plan.Score));
            Text(b, plan.WineName, ColGrade, 15f, plan.Counterfeit ? BadColor : WineGrade.ColorOf(plan.Score));
            Text(b, group.Count > 1 ? "×" + group.Count : "1", ColCount, 15f, DimColor);

            b.End();
        }

        // ---------------------------------------------------------------
        // 5) 底部：自动模式 + 一键酿造
        // ---------------------------------------------------------------

        private static void BuildFooter(CustomUIBuilder b, BrewStock stock, List<BrewPlan> plans)
        {
            b.BeginRow(Gap, TextAnchor.MiddleLeft);

            try
            {
                b.AddToggle("自动模式", AutoMode, OnAutoModeChanged, null);
                FixSize(b.Last, 150f, BtnH);
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 自动模式开关创建失败：" + e.GetType().Name);
            }

            CustomUIElement hint = Text(b, AutoMode
                ? "按推荐方案批量酿：发霉果不参与，只用干净水"
                : "只反复酿上面自选的那套，材料用完为止", -1f, 13f, DimColor);
            if (hint != null)
            {
                hint.SetFlexible(1f, 0f);
                if (hint.text != null)
                {
                    hint.text.enableWordWrapping = false;
                    hint.text.overflowMode = TextOverflowModes.Ellipsis;
                }
            }

            int count = AutoMode ? Math.Min(plans.Count, Brewing.MaxPerClick) : CountManual(stock);
            string label = count > 0 ? "一键酿造（" + count + " 瓶）" : "一键酿造";
            Button(b, label, 210f, delegate { BrewAllNow(); }, count > 0);

            b.End();
        }

        private static void BrewAllNow()
        {
            BrewStock stock = BrewScan.Scan();
            int waterTier = EffectiveTier(stock);
            BrewCombo manual = ManualCombo(stock, waterTier);
            Brewing.BrewAll(AutoMode, manual, waterTier);
        }

        private static void OnAutoModeChanged(bool on)
        {
            _autoMode = on;
            _autoLoaded = true;
            try
            {
                PlayerPrefs.SetInt(KeyAuto, on ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch { }

            Brewing.Rescan();
        }

        // ---------------------------------------------------------------
        // 选择状态
        // ---------------------------------------------------------------

        /// <summary>可用酵母：非合成在前，合成（走私用）在后。</summary>
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
            // 酵母没了就退回自动
            if (!string.IsNullOrEmpty(_preferred))
            {
                BrewYeast y = stock.Yeast(_preferred);
                if (y == null || y.Items.Count == 0) _preferred = string.Empty;
            }

            // 选的水质没水了也退回自动
            if (_waterTier >= 0 && stock.WaterBottles(_waterTier) <= 0) _waterTier = -1;

            // 自选颗数不能超过库存
            for (int g = 0; g < 4; g++)
            {
                int avail = stock.BerryCount(g);
                if (_mix[g] > avail) _mix[g] = avail;
                if (_mix[g] < 0) _mix[g] = 0;
            }
        }

        /// <summary>规划用酵母：选了合成酵母时不给规划，让推荐继续用普通酵母。</summary>
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

            // 自动：按游戏里的优先顺序挑一支
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

        /// <summary>这套搭配缺什么，缺就返回说明（空了才算能酿）。</summary>
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

        private static int CountManual(BrewStock stock)
        {
            bool counterfeit = IsSyntheticSelected(stock);
            if (!HasMix() && !counterfeit) return 0;
            if (stock.EmptyBottles.Count <= 0) return 0;

            int water = stock.WaterBottles(EffectiveTier(stock));
            if (water <= 0) return 0;

            int n = Math.Min(stock.EmptyBottles.Count, Math.Min(water, YeastBottles(stock)));

            // 果子够几瓶：按每种品相「库存 ÷ 每瓶用量」取最小
            if (!counterfeit)
            {
                for (int g = 0; g < 4; g++)
                {
                    if (_mix[g] <= 0) continue;
                    n = Math.Min(n, stock.BerryCount(g) / _mix[g]);
                }
            }

            return Math.Max(0, Math.Min(n, Brewing.MaxPerClick));
        }

        private static int YeastBottles(BrewStock stock)
        {
            BrewYeast y = stock.Yeast(_preferred);
            if (y != null) return y.Reusable ? 999 : y.Count;

            int total = 0;
            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                BrewYeast s = stock.Yeasts[i];
                if (!s.Synthetic) total += s.Reusable ? 999 : s.Count;
            }
            return total;
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
                    return "发霉果：每颗 -1 分，自动模式不会用它。";
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

        private static int MixTotal()
        {
            int n = 0;
            for (int i = 0; i < 4; i++) n += _mix[i];
            return n;
        }

        private static void CycleYeast(int dir)
        {
            try
            {
                BrewStock stock = BrewScan.Scan();
                List<BrewYeast> options = YeastOptions(stock);

                int count = options.Count + 1;      // 0 = 自动
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

        // ---------------------------------------------------------------
        // 文案
        // ---------------------------------------------------------------

        private static string YeastLabel(BrewStock stock, List<BrewYeast> options)
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
