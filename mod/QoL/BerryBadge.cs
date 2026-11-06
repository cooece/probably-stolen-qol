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
    /// 做法：在物品图标右上角挂一个彩色小底块 + 一个黑字。
    ///   · 底块是运行时的 Image（白色 quad 染色），半透明，不把图标糊死；
    ///   · 文字是游戏自己的 TMP 文本节点，字体从同元素的「堆叠数量」节点借，
    ///     所以和游戏字体完全一致，7 号字也清晰；
    ///   · 位置用锚点钉在图标右上角，图标多大都不会飘。
    ///
    /// 字有三种写法，底块颜色固定四档：
    ///   优 +2  绿   文字=甲   优良中差=优   ABCD=A
    ///   普 +1  黄   文字=乙   优良中差=良   ABCD=B
    ///   差 +0  灰   文字=丙   优良中差=中   ABCD=C
    ///   霉 -1  红   文字=丁   优良中差=差   ABCD=D
    ///
    /// 触发点是 Harmony 挂的 <c>GameItemElement.HandleInnateModSprite</c> 前缀：
    /// 格子每次刷新前都会走到，这时候更新角标最稳。
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
        private const string JiaYiChoice = "文字";          // 甲 乙 丙 丁
        private const string GradeWordChoice = "优良中差";   // 优 良 中 差
        private const string AbcdChoice = "ABCD";           // A B C D

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
                string v = _settings != null ? _settings.BerryDisplayMode.Value : null;

                if (v == GradeWordChoice)
                {
                    return GradeWordChoice;
                }

                if (v == AbcdChoice)
                {
                    return AbcdChoice;
                }

                return JiaYiChoice;
            }
        }

        // ------------------------------------------------------------------
        // 角标外观参数。改这几个就够了。
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
                if (weak.TryGetTarget(out GameItemElement element) && element != null)
                {
                    try
                    {
                        element.HandleInnateModSprite();
                    }
                    catch
                    {
                        // 元素已销毁，忽略
                    }
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
        // 判定
        // ------------------------------------------------------------------

        private static void BeforeRender(GameItemElement __instance)
        {
            if (__instance == null)
            {
                return;
            }

            Track(__instance);
            UpdateText(__instance);
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

                gameItem.ModifyTag(GradeTag, state => state.Enable().SetInt(grade), false);
            }
            catch
            {
                // 鉴定钩子失败不影响鉴定本身
            }
        }

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
        // 角标本体
        // ------------------------------------------------------------------

        private sealed class Badge
        {
            internal TMP_Text Text;
            internal Image Background;
        }

        private static readonly ConditionalWeakTable<GameItemElement, Badge> Badges =
            new ConditionalWeakTable<GameItemElement, Badge>();

        /// <summary>见过的元素，用于改设置后立刻重画。</summary>
        private static readonly List<WeakReference<GameItemElement>> Tracked =
            new List<WeakReference<GameItemElement>>();

        /// <summary>堆叠数量节点（私有字段），借它的字体和材质。</summary>
        private static readonly System.Reflection.FieldInfo StackCountField =
            AccessTools.Field(typeof(GameItemElement), "stackCountText");

        private static int _logged;

        private static void Track(GameItemElement element)
        {
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                if (!Tracked[i].TryGetTarget(out _))
                {
                    Tracked.RemoveAt(i);
                }
            }

            Tracked.Add(new WeakReference<GameItemElement>(element));
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

        private static void UpdateText(GameItemElement element)
        {
            try
            {
                UpdateTextCore(element);
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

        private static void UpdateTextCore(GameItemElement element)
        {
            bool berry = IsBerry(element);
            int grade = berry ? DisplayGrade(element) : 0;
            bool show = Enabled && IsGrade(grade);

            Badges.TryGetValue(element, out Badge badge);

            if (!show)
            {
                // 不是荧光莓、或者关掉功能时，把之前留下的角标藏起来
                if (badge != null)
                {
                    if (badge.Text != null)
                    {
                        badge.Text.text = string.Empty;
                        badge.Text.enabled = false;
                    }

                    if (badge.Background != null)
                    {
                        badge.Background.enabled = false;
                    }
                }

                return;
            }

            if (badge == null || badge.Text == null || badge.Background == null)
            {
                Badges.Remove(element);
                badge = Create(element);
                if (badge == null)
                {
                    return;
                }
            }

            badge.Background.enabled = true;
            badge.Background.color = GradeColor(grade);

            badge.Text.enabled = true;
            badge.Text.text = GradeText(grade);
            badge.Text.color = TextColor;
        }

        private static Badge Create(GameItemElement element)
        {
            TreeNodeRender middle = element.middle;
            if (middle == null || middle.rectTransform == null)
            {
                if (_logged < 4)
                {
                    _logged++;
                    QLog.Warn("荧光莓角标：middle 为空，建不了角标");
                }

                return null;
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

            // 1) 底色块：白色 quad 染色，钉在图标右上角
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

                return null;
            }

            // 2) 文字：挂在底色块里铺满，居中
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

                    return null;
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

            var result = new Badge { Text = txt, Background = background };
            Badges.Add(element, result);

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

            return result;
        }

        private static string GradeText(int grade)
        {
            string style = TextStyle;

            if (style == GradeWordChoice)
            {
                switch (grade)
                {
                    case 3: return "优";
                    case 2: return "良";
                    case 1: return "中";
                    default: return "差";
                }
            }

            if (style == AbcdChoice)
            {
                switch (grade)
                {
                    case 3: return "A";
                    case 2: return "B";
                    case 1: return "C";
                    default: return "D";
                }
            }

            switch (grade)
            {
                case 3: return "甲";
                case 2: return "乙";
                case 1: return "丙";
                default: return "丁";
            }
        }

        /// <summary>底色：绿(优) 黄(普通) 灰(差) 红(发霉)，统一 50% 透明。</summary>
        private static Color GradeColor(int grade)
        {
            switch (grade)
            {
                case 1: return new Color32(176, 180, 190, ChipAlpha);   // 灰
                case 2: return new Color32(240, 196, 71, ChipAlpha);    // 黄
                case 3: return new Color32(79, 209, 122, ChipAlpha);    // 绿
                default: return new Color32(230, 75, 75, ChipAlpha);    // 红
            }
        }
    }
}
