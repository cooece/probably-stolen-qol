using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 荧光莓品质角标。
    ///
    /// 这份实现是照抄参考模组 WineBerryQualityBadges（创意工坊 3830557056）的做法的，
    /// 连像素位图、颜色常量、色块位置都原样搬过来——那套算法已经被玩家验证过，
    /// 自己另起一套只会更差。
    ///
    /// 机制：
    ///   1. Harmony 挂 <c>GameItemElement.HandleInnateModSprite</c> 的 Prefix，
    ///      在格子刷新前把委托包好（时机错了就永远不会被读到）；
    ///   2. 委托里往图层列表末尾追加一层 32×32 的角标贴图，PPU = 100（和图标同密度）；
    ///   3. Sprite 按等级缓存，四档各生成一次。
    ///
    /// 角标是「9×9 的方框 + 中间一个符号」，贴在图标右上角：
    ///   cond = 2     小果      +2   绿 #4FD17A   十字
    ///   cond = 1     标准      +1   黄 #F0C447   横杠
    ///   cond = 0/-1  未熟/过熟  +0   紫 #C48AF5   V 形
    ///   cond = -2    发霉      -1   红 #E64B4B   竖杠带底座
    /// </summary>
    internal static class BerryBadge
    {
        private const string HarmonyId = "local.qol.berry_badge";

        private const string BerryId = "wine_berry";
        private const string BerryTag = "BERRY_TAG";
        private const string CondKey = "BERRY_CONDITION_INT";

        private const string GradeTag = "WINE_BERRY_QUALITY_BADGES_GRADE_V2";
        private const string LegacyGradeTag = "WINE_BERRY_QUALITY_BADGES_GRADE_V1";

        /// <summary>「显示方式」里代表数字档的选项文字，和 QoLSettings 里的choices 必须一致。</summary>
        private const string ScoreChoice = "酿造评分";
        private const string KanjiChoice = "甲乙丙丁";
        private const string TextChoice = "文字";

        /// <summary>设置来源。为空时按「全开、符号模式」的默认行为跑。</summary>
        private static QoLSettings _settings;

        private static bool Enabled => _settings == null || _settings.BerryBadge.Value;

        /// <summary>免鉴定：开启时直接读 condition，关闭时只认鉴定过的标签。</summary>
        private static bool WithoutMagnifier => _settings == null || _settings.BerrySkipMagnifier.Value;

        /// <summary>true = 数字模式（+2 / +1 / +0 / -1）。</summary>
        private static bool ShowScore =>
            _settings != null && _settings.BerryDisplayMode.Value == ScoreChoice;

        /// <summary>true = 甲乙丙丁模式。</summary>
        private static bool ShowKanji =>
            _settings != null && _settings.BerryDisplayMode.Value == KanjiChoice;

        /// <summary>true = 真文字模式（游戏自己的 TMP 文本节点，不是贴图）。</summary>
        private static bool ShowText =>
            _settings != null && _settings.BerryDisplayMode.Value == TextChoice;

        /// <summary>0=符号 1=数字 2=汉字，对应贴图缓存的第一个下标。</summary>
        private static int Mode => ShowScore ? 1 : (ShowKanji ? 2 : 0);

        private static Harmony _harmony;
        private static bool _warned;

        /// <summary>已包装过委托的物品。用 ConditionalWeakTable，不阻止物品被回收。</summary>
        private static readonly ConditionalWeakTable<GameItem, Binding> Bindings =
            new ConditionalWeakTable<GameItem, Binding>();

        /// <summary>还记得的物品，用来在需要时刷新已显示的格子。</summary>
        private static readonly List<WeakReference<GameItem>> Tracked = new List<WeakReference<GameItem>>();

        /// <summary>[模式, 等级] → 贴图/精灵，四档各一份。</summary>
        private static readonly Sprite[,] Sprites = new Sprite[3, 5];
        private static readonly Texture2D[,] Textures = new Texture2D[3, 5];

        private sealed class Binding
        {
            internal Func<GameItem, List<ItemSpriteModifier>> Previous;
            internal Func<GameItem, List<ItemSpriteModifier>> Wrapper;

            internal List<ItemSpriteModifier> Render(GameItem item)
            {
                return Compose(item, Previous);
            }
        }

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

                if (MethodInfoHolder.HandleInnateModSprite != null)
                {
                    _harmony.Patch(
                        MethodInfoHolder.HandleInnateModSprite,
                        prefix: new HarmonyMethod(typeof(BerryBadge), nameof(BeforeRender)));
                }
                else
                {
                    QLog.Warn("荧光莓徽标：找不到 GameItemElement.HandleInnateModSprite，功能不会生效");
                }

                // 鉴定钩子：记录品质标签（只在关掉「免鉴定」时才会被读到）
                if (MethodInfoHolder.OnInspectItem != null)
                {
                    _harmony.Patch(
                        MethodInfoHolder.OnInspectItem,
                        postfix: new HarmonyMethod(typeof(BerryBadge), nameof(AfterInspect)));
                }

                // 设置一改就重画所有已显示的格子，不用退出去再进背包
                if (settings != null)
                {
                    settings.BerryBadge.OnChanged += OnToggleChanged;
                    settings.BerrySkipMagnifier.OnChanged += OnToggleChanged;
                    settings.BerryDisplayMode.OnChanged += OnModeChanged;
                }

                QLog.Info("荧光莓徽标：已挂载（" + ModeName() + "模式，免鉴定=" + WithoutMagnifier + "）");
            }
            catch (Exception e)
            {
                QLog.Warn("荧光莓徽标：挂载失败 —— " + e.Message);
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
            DetachAll();
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
        // 挂接
        // ------------------------------------------------------------------

        private static void BeforeRender(GameItemElement __instance)
        {
            try
            {
                Attach(__instance);
                UpdateText(__instance);
            }
            catch
            {
                // 渲染期间抛异常会连累游戏，一律吞掉
            }
        }

        // ------------------------------------------------------------------
        // 真文字模式：用游戏自己的文本节点
        //
        // 图标上本来就有一块文字——堆叠数量的 "x5"，类型是 TextMeshProUGUI。
        // 所以「在图标上写字」不需要烘焙像素，建一个同样的文本节点就行，
        // 而且字体直接从那个节点借，保证和游戏完全一致。
        // ------------------------------------------------------------------

        private sealed class TextRef
        {
            internal TMP_Text Text;
            internal RectTransform Rect;
        }

        private static readonly ConditionalWeakTable<GameItemElement, TextRef> TextNodes =
            new ConditionalWeakTable<GameItemElement, TextRef>();

        /// <summary>堆叠数量节点（私有字段），借它的字体和材质。</summary>
        private static readonly System.Reflection.FieldInfo StackCountField =
            AccessTools.Field(typeof(GameItemElement), "stackCountText");

        /// <summary>字号。9 号在 32px 的图标上刚好占下半边多一点。</summary>
        private const float TextSize = 9f;

        /// <summary>相对图标中心的偏移，往右上角靠。</summary>
        private static readonly Vector2 TextOffset = new Vector2(9f, 8f);

        private static int _textLogged;
        private static bool _textEntered;
        private static bool _showSkipped;

        private static void UpdateText(GameItemElement element)
        {
            try
            {
                UpdateTextCore(element);
            }
            catch (Exception e)
            {
                if (_textLogged < 6)
                {
                    _textLogged++;
                    QLog.Warn("荧光莓文字：更新失败 —— " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        private static void UpdateTextCore(GameItemElement element)
        {
            if (element == null)
            {
                return;
            }

            bool berry = IsBerry(element);
            int grade = berry ? DisplayGrade(element) : 0;
            bool show = Enabled && ShowText && IsGrade(grade);

            TextNodes.TryGetValue(element, out TextRef reference);

            if (!show)
            {
                // 切回其它模式时，把之前留下的字清掉
                if (reference != null && reference.Text != null)
                {
                    reference.Text.text = string.Empty;
                    reference.Text.enabled = false;
                }

                if (ShowText && !_showSkipped)
                {
                    _showSkipped = true;
                    QLog.Info("荧光莓文字：跳过（Enabled=" + Enabled + " IsBerry=" + berry
                              + " grade=" + grade + "）");
                }

                return;
            }

            if (!_textEntered)
            {
                _textEntered = true;
                QLog.Info("荧光莓文字：进入更新，middle=" + (element.middle != null));
            }

            if (reference == null || reference.Text == null || reference.Rect == null)
            {
                TextNodes.Remove(element);
                reference = Create(element);
                if (reference == null)
                {
                    return;
                }
            }

            reference.Text.enabled = true;
            reference.Text.text = GradeText(grade);
            reference.Text.color = GradeColor(grade);
        }

        private static TextRef Create(GameItemElement element)
        {
            TreeNodeRender middle = element.middle;
            if (middle == null || middle.rectTransform == null)
            {
                if (_textLogged < 6)
                {
                    _textLogged++;
                    QLog.Warn("荧光莓文字：middle 为空，建不了文本节点");
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

            TMP_Text txt = null;
            RectTransform rect = null;

            // 首选：游戏自己的文字节点工厂
            try
            {
                TreeNodeRender node = TreeNodeRender.InstantiateText();
                if (node != null)
                {
                    node.SetParent(middle);
                    if (node.text != null)
                    {
                        txt = node.text;
                        rect = node.rectTransform;
                    }
                }
            }
            catch (Exception e)
            {
                if (_textLogged < 6)
                {
                    _textLogged++;
                    QLog.Warn("荧光莓文字：InstantiateText 失败 —— " + e.GetType().Name + " " + e.Message);
                }
            }

            // 兜底：自己建一个 TMP 文本
            if (txt == null)
            {
                try
                {
                    var go = new GameObject("QoL Berry Grade", typeof(RectTransform));
                    rect = go.GetComponent<RectTransform>();
                    rect.SetParent(middle.rectTransform, false);
                    txt = go.AddComponent<TextMeshProUGUI>();
                }
                catch (Exception e)
                {
                    if (_textLogged < 6)
                    {
                        _textLogged++;
                        QLog.Warn("荧光莓文字：自建文本失败 —— " + e.GetType().Name + " " + e.Message);
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
                    // 字体借不到就先这样，至少字能出来
                }
            }

            rect.sizeDelta = new Vector2(40f, 12f);
            rect.anchoredPosition = TextOffset;

            txt.alignment = TextAlignmentOptions.Center;
            txt.fontStyle = FontStyles.Bold;
            txt.fontSize = TextSize;
            txt.raycastTarget = false;
            txt.outlineWidth = 0.25f;
            txt.outlineColor = new Color32(36, 35, 53, 255);

            if (_textLogged < 6)
            {
                _textLogged++;
                Vector2 icon = element.background != null && element.background.rectTransform != null
                    ? element.background.rectTransform.sizeDelta
                    : Vector2.zero;
                QLog.Info("荧光莓文字：已创建，图标 " + icon.x + "x" + icon.y
                          + "，字体 " + (txt.font != null ? txt.font.name : "无")
                          + "，字号 " + TextSize + "，偏移 " + TextOffset.x + "," + TextOffset.y);
            }

            var result = new TextRef { Text = txt, Rect = rect };
            TextNodes.Add(element, result);
            return result;
        }

        private static string GradeText(int grade)
        {
            switch (grade)
            {
                case 3: return "甲";
                case 2: return "乙";
                case 1: return "丙";
                default: return "丁";
            }
        }

        private static Color GradeColor(int grade)
        {
            switch (grade)
            {
                case 1: return new Color32(196, 138, 245, 255);   // 丙 紫
                case 2: return new Color32(240, 196, 71, 255);    // 乙 黄
                case 3: return new Color32(79, 209, 122, 255);    // 甲 绿
                default: return new Color32(235, 96, 96, 255);    // 丁 红
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

                gameItem.ModifyTag(GradeTag, state => state.Enable().SetInt(grade), false);
            }
            catch
            {
                // 鉴定钩子失败不影响鉴定本身
            }
        }

        private static string ModeName()
        {
            if (ShowText)
            {
                return "文字";
            }

            return ShowScore ? "数字" : (ShowKanji ? "甲乙丙丁" : "符号");
        }

        private static void OnToggleChanged(bool _)
        {
            RefreshDisplayed();
        }

        private static void OnModeChanged(string _)
        {
            RefreshDisplayed();
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

        private static void Attach(GameItem item)
        {
            if (!Enabled || !IsBerry(item) || Bindings.TryGetValue(item, out _))
            {
                return;
            }

            // 解开之前包过的层，避免同一个物品被包多份（委托里套委托会叠出多层角标）
            Func<GameItem, List<ItemSpriteModifier>> func = item.getItemSpriteModifierForItemFunc;
            while (func != null && func.Target is Binding existing)
            {
                func = existing.Previous;
            }

            var binding = new Binding { Previous = func };
            binding.Wrapper = binding.Render;

            Bindings.Add(item, binding);

            Tracked.RemoveAll(weak => !weak.TryGetTarget(out _));
            Tracked.Add(new WeakReference<GameItem>(item));

            item.getItemSpriteModifierForItemFunc = binding.Wrapper;
        }

        private static List<ItemSpriteModifier> Compose(
            GameItem item, Func<GameItem, List<ItemSpriteModifier>> previous)
        {
            List<ItemSpriteModifier> list = previous?.Invoke(item);

            if (!Enabled || !IsBerry(item))
            {
                return list;
            }

            int grade = DisplayGrade(item);
            if (!IsGrade(grade) || ShowText)
            {
                // 文字模式走 BeforeRender 里的 TMP 节点，不在这里叠贴图
                return list;
            }

            try
            {
                int mode = Mode;
                if (Sprites[mode, grade] == null)
                {
                    CreateSprite(grade, mode);
                }

                List<ItemSpriteModifier> result = list == null
                    ? new List<ItemSpriteModifier>(1)
                    : new List<ItemSpriteModifier>(list);

                result.Add(new ItemSpriteModifier(
                    Sprites[mode, grade], Vector2.zero, 0, false, 100, false, default(Color)));

                return result;
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    QLog.Warn("荧光莓徽标：合成图层失败 —— " + e.Message);
                }

                return list;
            }
        }

        /// <summary>等级：1=差(紫) 2=普通(黄) 3=优(绿) 4=发霉(红)。0 表示不显示。</summary>
        private static int DisplayGrade(GameItem item)
        {
            TagSystem state = null;

            try
            {
                state = item.modifiedState;
            }
            catch
            {
                return 0;
            }

            // 有些元素（预览、假节点）没有状态，直接不给角标，别让它抛异常连累整帧
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

            // 旧版标签：发霉被记成了 1，要按 condition 纠正回来
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
        // 生成贴图
        // ------------------------------------------------------------------

        private static void CreateSprite(int grade, int mode)
        {
            uint[] pixels = BuildPixels(grade, mode);

            var colors = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                uint v = pixels[i];
                colors[i] = new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }

            var tex = new Texture2D(32, 32, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            tex.SetPixels32(colors);
            tex.Apply(false, false);

            Textures[mode, grade] = tex;

            // PPU 必须 100：物品图标就是这个密度，写成 32 会放大约三倍，盖住旁边的格子。
            Sprites[mode, grade] = Sprite.Create(
                tex, new Rect(0f, 0f, 32f, 32f), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// 画 32×32 的像素图。三种模式共用同一个外框逻辑，只是方块尺寸和里面的图案不同：
        ///   符号   9×9   贴右边第 22 列起
        ///   数字  11×9   要先放下 +/- 号
        ///   汉字  15×15  13×13 的字形外面还得留 1px 边框
        /// 方块四个角留空，边框和图案用同一个深色 #242335。
        /// 坐标和配色常量都是从参考模组原样搬过来的。
        /// </summary>
        private static uint[] BuildPixels(int grade, int mode)
        {
            var px = new uint[1024];

            if (!IsGrade(grade))
            {
                return px;
            }

            uint bg;
            switch (grade)
            {
                case 1: bg = 3297441279u; break;   // #C48AF5 紫
                case 2: bg = 4039395327u; break;   // #F0C447 黄
                case 3: bg = 1339128575u; break;   // #4FD17A 绿
                default: bg = 3863694335u; break;  // #E64B4B 红
            }

            const uint Edge = 606287359u;          // #242335 边框与图案色

            int boxW = mode == 1 ? 11 : (mode == 2 ? 15 : 9);
            int boxH = mode == 1 ? 9 : boxW;
            int x0 = 32 - boxW - 1;

            for (int row = 0; row < boxH; row++)
            {
                for (int col = 0; col < boxW; col++)
                {
                    // 四角留空
                    if ((col == 0 || col == boxW - 1) && (row == 0 || row == boxH - 1))
                    {
                        continue;
                    }

                    bool edge = col == 0 || col == boxW - 1 || row == 0 || row == boxH - 1;

                    bool ink;
                    switch (mode)
                    {
                        case 1: ink = IsScorePixel(grade, col, row); break;
                        case 2: ink = IsKanjiPixel(grade, col, row); break;
                        default: ink = IsSymbolPixel(grade, col, row); break;
                    }

                    // 贴图 y 轴朝上，row=0 是最上面那一行，所以是 30 - row
                    px[(30 - row) * 32 + x0 + col] = (edge || ink) ? Edge : bg;
                }
            }

            return px;
        }

        /// <summary>
        /// 符号模式。方块内侧 5×5 的范围里画：
        ///   V/X 形、横杠、十字、竖杠带底座。
        /// </summary>
        private static bool IsSymbolPixel(int grade, int dx, int dy)
        {
            switch (grade)
            {
                case 1:
                    return dx >= 2 && dx <= 6 && (dy == dx || dy == 8 - dx);
                case 2:
                    return dy == 4 && dx >= 2 && dx <= 6;
                case 3:
                    return (dy == 4 && dx >= 2 && dx <= 6) || (dx == 4 && dy >= 2 && dy <= 6);
                default:
                    return dx == 4 && ((dy >= 2 && dy <= 4) || dy == 6);
            }
        }

        /// <summary>数字模式的点阵：左边一个加减号，右边 3×5 的阿拉伯数字。</summary>
        private static bool IsScorePixel(int grade, int x, int y)
        {
            if (x >= 2 && x <= 4)
            {
                if (y == 4)
                {
                    return true;    // 加减号的横杠
                }

                if (grade != 4 && x == 3)
                {
                    return y == 3 || y == 5;   // 加号的竖杠
                }

                return false;
            }

            if (x < 6 || x > 8 || y < 2 || y > 6)
            {
                return false;
            }

            string glyph;
            switch (grade)
            {
                case 1: glyph = "111101101101111"; break;  // 0
                case 3: glyph = "111001111100111"; break;  // 2
                default: glyph = "010110010010111"; break; // 1
            }

            return glyph[(y - 2) * 3 + x - 6] == '1';
        }

        /// <summary>
        /// 汉字模式。13×13 的字形放在方块内侧，四边各留 1 像素顶着边框。
        ///
        /// 位图是拿宋体 13px 渲染后二值化存下来的——运行时不去碰字体，
        /// 之前用 TMP 字形采样踩过的坑（图集不可读、字体随机取错、坐标系要翻转）全都不用再管。
        /// 排序也是「甲乙丙丁」，甲最好、丁最差。
        /// </summary>
        private static bool IsKanjiPixel(int grade, int dx, int dy)
        {
            int gx = dx - 1;
            int gy = dy - 1;

            if (gx < 0 || gx >= 13 || gy < 0 || gy >= 13)
            {
                return false;
            }

            string[] glyph;
            switch (grade)
            {
                case 3: glyph = GlyphJia; break;    // 优
                case 2: glyph = GlyphYi; break;     // 普通
                case 1: glyph = GlyphBing; break;   // 差
                default: glyph = GlyphDing; break;  // 发霉
            }

            return glyph[gy][gx] == '1';
        }

        // 甲：口字框中间一竖，竖到底
        private static readonly string[] GlyphJia =
        {
            "0011111111100",
            "0010001000100",
            "0010001000100",
            "0011111111100",
            "0010001000100",
            "0010001000100",
            "0010001000100",
            "0011111111100",
            "0010001000100",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000000000000",
        };

        // 乙
        private static readonly string[] GlyphYi =
        {
            "0000000000000",
            "0111111111000",
            "0000000010000",
            "0000000100000",
            "0000001000000",
            "0000010000000",
            "0000100000000",
            "0001000000000",
            "0010000000100",
            "0010000000100",
            "0010000000100",
            "0001111111100",
            "0000000000000",
        };

        // 丙
        private static readonly string[] GlyphBing =
        {
            "0000000000000",
            "0111111111110",
            "0000001000000",
            "0000001000000",
            "0111111111110",
            "0100001000010",
            "0100001000010",
            "0100010100010",
            "0100100010010",
            "0101000001010",
            "0100000000010",
            "0100000000110",
            "0000000000000",
        };

        // 丁
        private static readonly string[] GlyphDing =
        {
            "0000000000000",
            "0111111111110",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000001000000",
            "0000111000000",
            "0000000000000",
        };

        // ------------------------------------------------------------------
        // 刷新与卸载
        // ------------------------------------------------------------------

        /// <summary>让已经显示的格子重画一次（改设置后立刻生效）。</summary>
        internal static void RefreshDisplayed()
        {
            WeakReference<GameItem>[] snapshot = Tracked.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!snapshot[i].TryGetTarget(out GameItem target))
                {
                    continue;
                }

                GameItemElement element = target as GameItemElement;
                if (element != null && !element.IsDestroyed())
                {
                    element.HandleInnateModSprite();
                }
            }
        }

        private static void DetachAll()
        {
            foreach (WeakReference<GameItem> weak in Tracked)
            {
                if (!weak.TryGetTarget(out GameItem item))
                {
                    continue;
                }

                if (!Bindings.TryGetValue(item, out Binding binding))
                {
                    continue;
                }

                if (item.getItemSpriteModifierForItemFunc == binding.Wrapper)
                {
                    item.getItemSpriteModifierForItemFunc = binding.Previous;
                }

                GameItemElement element = item as GameItemElement;
                if (element != null)
                {
                    try
                    {
                        element.HandleInnateModSprite();
                    }
                    catch (MissingReferenceException)
                    {
                    }
                    catch (NullReferenceException)
                    {
                    }
                }
            }

            Tracked.Clear();

            for (int i = 0; i < Sprites.GetLength(0); i++)
            {
                for (int j = 1; j < Sprites.GetLength(1); j++)
                {
                    if (Sprites[i, j] != null)
                    {
                        UnityEngine.Object.Destroy(Sprites[i, j]);
                        Sprites[i, j] = null;
                    }

                    if (Textures[i, j] != null)
                    {
                        UnityEngine.Object.Destroy(Textures[i, j]);
                        Textures[i, j] = null;
                    }
                }
            }

            _warned = false;
        }
    }
}
