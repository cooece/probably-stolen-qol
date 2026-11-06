using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QoL
{
    /// <summary>
    /// 荧光莓品质角标（纯文字版）。
    ///
    /// 外观：图标右上角一个彩色小底块（9×9、50% 透明）+ 一个 7 号黑字。
    ///   · 底块是运行时 Image（白 quad 染色），文字是游戏自己的 TMP 节点，
    ///     字体从同元素的「堆叠数量」节点借，所以和游戏字体一致；
    ///   · 位置用锚点钉在图标右上角，图标多大都不会飘。
    ///
    /// 字有三种写法，底块颜色固定四档：
    ///   优 +2  绿   甲乙丙丁=甲   优良中差=优   ABCD=A
    ///   普 +1  黄   甲乙丙丁=乙   优良中差=良   ABCD=B
    ///   差 +0  灰   甲乙丙丁=丙   优良中差=中   ABCD=C
    ///   霉 -1  红   甲乙丙丁=丁   优良中差=差   ABCD=D
    ///
    /// 性能上有三条纪律，都是踩过坑换来的：
    ///   1. 不重复登记元素（否则列表无限膨胀，加载直接卡死）；
    ///   2. 每帧的前缀里只做纯判定，不碰 UI（写 TMP 文本会触发重新排版，很贵）；
    ///   3. 等级和样式都没变就直接返回，一个字都不写。
    /// </summary>
    internal static class BerryBadge
    {
        private const string HarmonyId = "local.qol.berry_badge";

        private const string BerryId = "wine_berry";
        private const string BerryTag = "BERRY_TAG";
        private const string CondKey = "BERRY_CONDITION_INT";

        private const string GradeTag = "WINE_BERRY_QUALITY_BADGES_GRADE_V2";
        private const string LegacyGradeTag = "WINE_BERRY_QUALITY_BADGES_GRADE_V1";

        // 三种文字写法，同时也是设置里下拉框的三个选项
        private const string JiaYiChoice = "甲乙丙丁";        // 甲 乙 丙 丁
        private const string GradeWordChoice = "优良中差";   // 优 良 中 差
        private const string AbcdChoice = "ABCD";           // A B C D

        // ------------------------------------------------------------------
        // 外观参数。要调就调这几个。
        // ------------------------------------------------------------------

        /// <summary>字号。7 号在 32px 的图标上只占五分之一高，不挡果实。</summary>
        private const float TextSize = 7f;

        /// <summary>底色块尺寸（像素）。比一个字略大，留 1px 边。</summary>
        private static readonly Vector2 ChipSize = new Vector2(9f, 9f);

        /// <summary>底色块距图标右上角的内缩。</summary>
        private static readonly Vector2 ChipInset = new Vector2(1f, 1f);

        /// <summary>底色不透明度：50%，透出一点果实。</summary>
        private const byte ChipAlpha = 128;

        /// <summary>字用近黑色，压在彩色底上对比最强。</summary>
        private static readonly Color TextColor = new Color32(26, 27, 34, 255);

        // ------------------------------------------------------------------
        // 四种等级的文案与配色。下标就是等级，1=差 2=普通 3=优 4=发霉。
        // 用静态数组而不是 switch，避免每次刷新都走分支、也不需要临时字符串。
        // ------------------------------------------------------------------

        private static readonly string[] JiaYiText = { string.Empty, "丙", "乙", "甲", "丁" };
        private static readonly string[] GradeWordText = { string.Empty, "中", "良", "优", "差" };
        private static readonly string[] AbcdText = { string.Empty, "C", "B", "A", "D" };

        private static readonly Color[] GradeColors =
        {
            new Color32(0, 0, 0, 0),                            // 0 占位
            new Color32(176, 180, 190, ChipAlpha),              // 1 灰 差
            new Color32(240, 196, 71, ChipAlpha),               // 2 黄 普通
            new Color32(79, 209, 122, ChipAlpha),               // 3 绿 优
            new Color32(230, 75, 75, ChipAlpha),                // 4 红 发霉
        };

        private static Harmony _harmony;
        private static QoLSettings _settings;

        private static bool Enabled => _settings == null || _settings.BerryBadge.Value;

        /// <summary>免鉴定：开启时直接读 condition，关闭时只认放大镜鉴定过的。</summary>
        private static bool WithoutMagnifier => _settings == null || _settings.BerrySkipMagnifier.Value;

        /// <summary>当前字样式。存的值不认识（比如旧配置里的贴图模式）时按「文字」处理。</summary>
        private static string TextStyle
        {
            get
            {
                if (_settings == null)
                {
                    return JiaYiChoice;
                }

                string v = _settings.BerryDisplayMode.Value;

                return v == GradeWordChoice || v == AbcdChoice ? v : JiaYiChoice;
            }
        }

        // ------------------------------------------------------------------
        // 每个元素的状态。查一次表就能拿到，后续都是 O(1)。
        // ------------------------------------------------------------------

        private sealed class ElementState
        {
            /// <summary>
            /// 是不是荧光莓。只缓存「是」的结果——元素刚建出来时标签可能还没写入，
            /// 那时判定会得到 false，如果缓存下来这个格子就永远不显示角标了。
            /// </summary>
            internal bool IsBerry;

            /// <summary>角标节点。第一次需要显示时才创建，没有角标的元素不占这层开销。</summary>
            internal TMP_Text Text;
            internal Image Background;

            /// <summary>当前显示中的等级，0 表示没显示。用来判断要不要重写文本。</summary>
            internal int ShownGrade;

            /// <summary>当前显示用的字样式，样式切换时要重写。</summary>
            internal string ShownStyle;
        }

        private static readonly ConditionalWeakTable<GameItemElement, ElementState> States =
            new ConditionalWeakTable<GameItemElement, ElementState>();

        /// <summary>
        /// 首次见到某个元素时用来建状态的委托。
        /// 必须存成静态字段——写成 lambda 直接传进去的话，每次调用都会新建一个委托对象，
        /// 而 HandleInnateModSprite 一秒能调用几千次，白白给 GC 增加压力。
        /// </summary>
        private static readonly ConditionalWeakTable<GameItemElement, ElementState>.CreateValueCallback CreateState =
            e => new ElementState { IsBerry = IsBerry(e) };

        /// <summary>见过的荧光莓元素，改设置时用来重画。</summary>
        private static readonly List<WeakReference<GameItemElement>> Tracked =
            new List<WeakReference<GameItemElement>>();

        /// <summary>堆叠数量节点（私有字段），借它的字体和材质。</summary>
        private static readonly System.Reflection.FieldInfo StackCountField =
            AccessTools.Field(typeof(GameItemElement), "stackCountText");

        private static int _logged;

        // 诊断：统计识别到荧光莓的次数，只打印前几次定位问题
        private static int _diagBerryHits;

        // ------------------------------------------------------------------
        // 注册
        // ------------------------------------------------------------------

        internal static void Apply(QoLSettings settings)
        {
            if (_harmony != null)
            {
                return;
            }

            _settings = settings;

            try
            {
                _harmony = new Harmony(HarmonyId);

                MethodInfoHolder.Ensure();

                if (MethodInfoHolder.HandleInnateModSprite == null)
                {
                    QLog.Warn("荧光莓角标：找不到 GameItemElement.HandleInnateModSprite，功能不会生效");
                    return;
                }

                _harmony.Patch(
                    MethodInfoHolder.HandleInnateModSprite,
                    prefix: new HarmonyMethod(typeof(BerryBadge), nameof(BeforeRender)));

                if (MethodInfoHolder.OnInspectItem != null)
                {
                    _harmony.Patch(
                        MethodInfoHolder.OnInspectItem,
                        postfix: new HarmonyMethod(typeof(BerryBadge), nameof(AfterInspect)));
                }

                if (settings != null)
                {
                    settings.BerryBadge.OnChanged += OnToggleChanged;
                    settings.BerrySkipMagnifier.OnChanged += OnToggleChanged;
                    settings.BerryDisplayMode.OnChanged += OnModeChanged;
                }

                QLog.Info("荧光莓角标：已挂载（" + TextStyle + "，免鉴定=" + WithoutMagnifier + "）");
            }
            catch (Exception e)
            {
                QLog.Warn("荧光莓角标：挂载失败 —— " + e.Message);
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
                // 卸载失败不影响退出
            }

            if (_settings != null)
            {
                _settings.BerryBadge.OnChanged -= OnToggleChanged;
                _settings.BerrySkipMagnifier.OnChanged -= OnToggleChanged;
                _settings.BerryDisplayMode.OnChanged -= OnModeChanged;
                _settings = null;
            }

            _harmony = null;

            foreach (WeakReference<GameItemElement> weak in Tracked)
            {
                if (!weak.TryGetTarget(out GameItemElement element) || element == null)
                {
                    continue;
                }

                if (States.TryGetValue(element, out ElementState state))
                {
                    Hide(state);
                }
            }

            Tracked.Clear();
        }

        private static void OnToggleChanged(bool _)
        {
            RefreshDisplayed();
        }

        private static void OnModeChanged(string _)
        {
            RefreshDisplayed();
        }

        private static class MethodInfoHolder
        {
            internal static System.Reflection.MethodInfo HandleInnateModSprite;
            internal static System.Reflection.MethodInfo OnInspectItem;

            internal static void Ensure()
            {
                if (HandleInnateModSprite == null)
                {
                    HandleInnateModSprite = AccessTools.Method(typeof(GameItemElement), "HandleInnateModSprite");
                }

                if (OnInspectItem == null)
                {
                    Type t = AccessTools.TypeByName("InspectionUIManager");
                    if (t != null)
                    {
                        OnInspectItem = AccessTools.Method(t, "OnInspectItem");
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // 每帧入口：只做判定，不碰 UI
        // ------------------------------------------------------------------

        /// <summary>
        /// 注意参数必须叫 __instance：这是 Harmony 保留的参数名，改成别的名字会注入失败，
        /// 表现为整个功能静默不生效（日志里报 Parameter "xxx" not found）。
        /// </summary>
        private static void BeforeRender(GameItemElement __instance)
        {
            GameItemElement element = __instance;

            if (element == null)
            {
                return;
            }

            ElementState state;

            try
            {
                state = States.GetValue(element, CreateState);
            }
            catch (Exception e)
            {
                if (_logged < 2)
                {
                    _logged++;
                    QLog.Warn("角标诊断：取状态失败 —— " + e.GetType().Name + " " + e.Message);
                }

                return;
            }

            // 没判定成荧光莓的不缓存结论：元素刚创建时标签可能还没写进去，
            // 只在 true 时锁定，false 每次重新判（非荧光莓第一步就是字符串比较，很便宜）。
            if (!state.IsBerry)
            {
                state.IsBerry = IsBerry(element);

                if (!state.IsBerry)
                {
                    return;
                }
            }

            if (_diagBerryHits < 3)
            {
                _diagBerryHits++;
                QLog.Info("荧光莓角标：识别到荧光莓 id=" + (element.identifier ?? "null")
                          + " grade=" + DisplayGrade(element)
                          + " style=" + TextStyle);
            }

            Track(element);

            if (!Enabled)
            {
                Hide(state);
                return;
            }

            int grade = DisplayGrade(element);
            string style = TextStyle;

            // 等级和写法都没变：这次什么都不用动（不写文本、不碰材质）
            if (grade == state.ShownGrade && style == state.ShownStyle)
            {
                return;
            }

            try
            {
                Apply(state, element, grade, style);
            }
            catch (Exception e)
            {
                if (_logged < 4)
                {
                    _logged++;
                    QLog.Warn("荧光莓角标：更新失败 —— " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        private static void Track(GameItemElement element)
        {
            // 已经登记过就直接返回（绝大多数调用的实际路径）
            if (AlreadyTracked(element))
            {
                return;
            }

            // 只在列表增长到一定量时才清理
            if (Tracked.Count >= 256)
            {
                for (int i = Tracked.Count - 1; i >= 0; i--)
                {
                    if (!Tracked[i].TryGetTarget(out _))
                    {
                        Tracked.RemoveAt(i);
                    }
                }

                // 元素本来就多的话，丢掉最老的一半，防止无限增长
                if (Tracked.Count >= 512)
                {
                    Tracked.RemoveRange(0, Tracked.Count / 2);
                }
            }

            Tracked.Add(new WeakReference<GameItemElement>(element));
        }

        private static readonly ConditionalWeakTable<GameItemElement, object> TrackedSet =
            new ConditionalWeakTable<GameItemElement, object>();

        private static readonly object TrackedMarker = new object();

        private static bool AlreadyTracked(GameItemElement element)
        {
            if (TrackedSet.TryGetValue(element, out _))
            {
                return true;
            }

            TrackedSet.Add(element, TrackedMarker);
            return false;
        }

        internal static void RefreshDisplayed()
        {
            WeakReference<GameItemElement>[] snapshot = Tracked.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!snapshot[i].TryGetTarget(out GameItemElement element) || element == null)
                {
                    continue;
                }

                if (States.TryGetValue(element, out ElementState state))
                {
                    // 清掉缓存，下次渲染时按新样式重画
                    state.ShownGrade = -1;
                    state.ShownStyle = null;
                }

                try
                {
                    element.HandleInnateModSprite();
                }
                catch
                {
                    // 元素已销毁
                }
            }
        }

        private static void AfterInspect(GameItem gameItem)
        {
            try
            {
                if (gameItem == null || !IsBerry(gameItem))
                {
                    return;
                }

                int grade = Classify(gameItem.GetTagReadonly(CondKey).GetInt());

                gameItem.ModifyTag(GradeTag, tagState => tagState.Enable().SetInt(grade), false);
            }
            catch
            {
                // 鉴定钩子失败不影响鉴定本身
            }
        }

        // ------------------------------------------------------------------
        // 判定
        // ------------------------------------------------------------------

        internal static bool IsBerry(GameItem item)
        {
            if (item == null || item.identifier != BerryId)
            {
                return false;
            }

            try
            {
                return item.IsTag(BerryTag);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>等级：1=差 2=普通 3=优 4=发霉。0 表示不显示。</summary>
        private static int DisplayGrade(GameItem item)
        {
            TagSystem state;

            try
            {
                state = item.modifiedState;
            }
            catch
            {
                return 0;
            }

            // 预览节点、假节点没有状态，直接不给角标
            if (state == null || state.dict == null)
            {
                return 0;
            }

            if (WithoutMagnifier)
            {
                if (state.dict.TryGetValue(CondKey, out TagState value) && value.IsEnabled())
                {
                    return Classify(value.GetInt());
                }

                return 0;
            }

            if (state.dict.TryGetValue(GradeTag, out TagState v2) && v2.IsEnabled())
            {
                return v2.GetInt();
            }

            if (!state.dict.TryGetValue(LegacyGradeTag, out TagState v1) || !v1.IsEnabled())
            {
                return 0;
            }

            // 旧版标签把发霉记成了 1，按 condition 纠正
            if (v1.GetInt() == 1
                && state.dict.TryGetValue(CondKey, out TagState cond)
                && cond.IsEnabled()
                && cond.GetInt() == -2)
            {
                return 4;
            }

            return v1.GetInt();
        }

        private static int Classify(int condition)
        {
            switch (condition)
            {
                case -2:
                    return 4;   // 发霉
                case 1:
                    return 2;   // 标准
                case 2:
                    return 3;   // 小果
                default:
                    return 1;   // 未熟 / 过熟
            }
        }

        private static bool IsGrade(int grade)
        {
            return grade >= 1 && grade <= 4;
        }

        // ------------------------------------------------------------------
        // 真正动手：只在等级或样式变化时才会走到
        // ------------------------------------------------------------------

        private static void Apply(ElementState state, GameItemElement element, int grade, string style)
        {
            if (!IsGrade(grade))
            {
                Hide(state);
                return;
            }

            if (state.Text == null || state.Background == null)
            {
                if (!Create(state, element))
                {
                    return;
                }
            }

            state.Background.enabled = true;
            state.Background.color = GradeColors[grade];

            state.Text.enabled = true;
            state.Text.text = TextFor(grade, style);
            state.Text.color = TextColor;

            state.ShownGrade = grade;
            state.ShownStyle = style;
        }

        /// <summary>收起来。已经收着就直接返回，避免每帧写空字符串触发 TMP 重排。</summary>
        private static void Hide(ElementState state)
        {
            // 还没建过节点，或者已经收起来了：什么都不用做
            if (state.Text == null && state.Background == null)
            {
                state.ShownGrade = 0;
                return;
            }

            if (state.ShownGrade == 0 && state.Text != null && !state.Text.enabled)
            {
                return;
            }

            if (state.Text != null)
            {
                state.Text.enabled = false;
                state.Text.text = string.Empty;
            }

            if (state.Background != null)
            {
                state.Background.enabled = false;
            }

            state.ShownGrade = 0;
            state.ShownStyle = null;
        }

        private static string TextFor(int grade, string style)
        {
            if (style == GradeWordChoice)
            {
                return GradeWordText[grade];
            }

            return style == AbcdChoice ? AbcdText[grade] : JiaYiText[grade];
        }

        /// <summary>建节点。每个元素只做一次，之后都走 Apply 改属性。</summary>
        private static bool Create(ElementState state, GameItemElement element)
        {
            TreeNodeRender middle = element.middle;
            if (middle == null || middle.rectTransform == null)
            {
                if (_logged < 4)
                {
                    _logged++;
                    QLog.Warn("荧光莓角标：middle 为空，建不了角标");
                }

                return false;
            }

            TreeNodeRender template = null;
            try
            {
                template = StackCountField?.GetValue(element) as TreeNodeRender;
            }
            catch
            {
                // 拿不到模板就用默认字体
            }

            RectTransform chip;
            Image background;

            try
            {
                var go = new GameObject("QoL Berry Chip", typeof(RectTransform));
                chip = go.GetComponent<RectTransform>();
                chip.SetParent(middle.rectTransform, false);

                background = go.AddComponent<Image>();
                background.raycastTarget = false;
                background.sprite = null;

                chip.anchorMin = new Vector2(1f, 1f);
                chip.anchorMax = new Vector2(1f, 1f);
                chip.pivot = new Vector2(1f, 1f);
                chip.sizeDelta = ChipSize;
                chip.anchoredPosition = new Vector2(-ChipInset.x, -ChipInset.y);
            }
            catch (Exception e)
            {
                if (_logged < 4)
                {
                    _logged++;
                    QLog.Warn("荧光莓角标：底色块创建失败 —— " + e.GetType().Name + " " + e.Message);
                }

                return false;
            }

            TMP_Text txt = null;
            RectTransform rect = null;

            try
            {
                TreeNodeRender node = TreeNodeRender.InstantiateText();
                if (node != null && node.text != null)
                {
                    rect = node.rectTransform;
                    rect.SetParent(chip, false);
                    txt = node.text;
                }
            }
            catch (Exception e)
            {
                if (_logged < 4)
                {
                    _logged++;
                    QLog.Warn("荧光莓角标：文字节点创建失败 —— " + e.GetType().Name + " " + e.Message);
                }
            }

            if (txt == null)
            {
                try
                {
                    var go = new GameObject("QoL Berry Grade", typeof(RectTransform));
                    rect = go.GetComponent<RectTransform>();
                    rect.SetParent(chip, false);
                    txt = go.AddComponent<TextMeshProUGUI>();
                }
                catch (Exception e)
                {
                    if (_logged < 4)
                    {
                        _logged++;
                        QLog.Warn("荧光莓角标：自建文本失败 —— " + e.GetType().Name + " " + e.Message);
                    }

                    return false;
                }
            }

            if (template != null && template.text != null)
            {
                try
                {
                    txt.font = template.text.font;
                    txt.fontSharedMaterial = template.text.fontSharedMaterial;
                }
                catch
                {
                    // 借不到就用默认字体
                }
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            txt.alignment = TextAlignmentOptions.Center;
            txt.fontStyle = FontStyles.Bold;
            txt.fontSize = TextSize;
            txt.raycastTarget = false;
            txt.outlineWidth = 0f;

            state.Text = txt;
            state.Background = background;

            if (_logged < 1)
            {
                _logged++;
                Vector2 icon = element.background != null && element.background.rectTransform != null
                    ? element.background.rectTransform.sizeDelta
                    : Vector2.zero;
                QLog.Info("荧光莓角标：已创建，图标 " + icon.x + "x" + icon.y
                          + "，字体 " + (txt.font != null ? txt.font.name : "无")
                          + "，字号 " + TextSize + "，块 " + ChipSize.x + "x" + ChipSize.y);
            }

            return true;
        }
    }
}
