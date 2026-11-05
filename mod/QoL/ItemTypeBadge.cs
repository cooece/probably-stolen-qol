using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QoL
{
    /// <summary>标签外观尺寸。固定值，不开放设置——这几组数字是实机比对后定下来的。</summary>
    internal static class TagStyle
    {
        /// <summary>左右内边距：边框到文字的横向距离。</summary>
        internal const float PadX = 3f;

        /// <summary>上下内边距：边框到文字的纵向距离。</summary>
        internal const float PadY = 1f;

        /// <summary>相邻标签之间的间距。</summary>
        internal const float Gap = 3f;

        /// <summary>标签文字大小相对提示框正文的比例。</summary>
        internal const float FontScale = 0.85f;

        /// <summary>标签底边到提示框顶边的距离。</summary>
        internal const float OffsetY = 3f;

        /// <summary>文字在标签内部的横向内缩。</summary>
        internal const float TextInsetX = 1f;

        /// <summary>圆角半径（像素）。0 表示直角。</summary>
        internal const float CornerRadius = 4f;

        /// <summary>边框粗细（像素）。</summary>
        internal const float BorderWidth = 1f;
    }

    /// <summary>
    /// 物品提示框上方的彩色分类标签。
    ///
    /// 原版会在提示框里写一行 " [日用品] [净水用品]"，这里把这行文字拦掉，
    /// 改成在提示框上方画一排色块。三个挂载点：
    ///   1. CommonTooltipHelper.CreateItemTypeTooltip —— 拿到这件物品的分类，并跳过原版文字
    ///   2. OverlayHandler.ShowTooltip（两个重载）—— 提示框显示后，建/摆放标签
    ///   3. OverlayHandler.HideTooltip —— 提示框关掉时隐藏标签
    ///
    /// 尺寸（内边距/间距/字号/离顶距离）是 TagStyle 里的固定常量，不开放设置。
    /// </summary>
    internal static class ItemTypeBadge
    {
        private const string HarmonyId = "local.qol.type_badge";

        /// <summary>提示框关掉后，分类数据多久算过期（秒）。</summary>
        private const float StaleSeconds = 0.35f;

        private sealed class Chip
        {
            internal string Type = "";
            internal string Display = "";
        }

        /// <summary>本次提示框要显示的分类（由 CreateItemTypeTooltip 填充）。</summary>
        private static readonly List<Chip> Pending = new List<Chip>();

        /// <summary>每个标签算出来的宽度，布局时用。</summary>
        private static readonly List<float> Widths = new List<float>();

        private static Harmony _harmony;
        private static GameObject _badge;
        private static TMP_Text _template;
        private static float _pendingAt = -999f;

        /// <summary>
        /// 提示框正在用的字体。别的模块（比如顾客提示）可以借它，
        /// 这样新加的文字和游戏原生文字字体完全一致，不会缺字。
        /// </summary>
        internal static TMP_FontAsset CurrentFont
        {
            get { return _template != null ? _template.font : null; }
        }

        /// <summary>按配色缓存的圆角贴图。每个分类一份，一共十来份，建一次就够。</summary>
        private static readonly Dictionary<string, Sprite> ChipSprites = new Dictionary<string, Sprite>();

        /// <summary>贴图的超采样倍数：1 个 UI 像素画 4 个纹理像素，圆角边缘才不会有锯齿。</summary>
        private const int Supersample = 4;

        // 缓存上一次的构建结果，内容没变就不重建
        private static string _builtKey = "";
        private static Transform _builtParent;

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                // 1) 拦截原版分类文字
                var createTypes = AccessTools.Method(typeof(CommonTooltipHelper), "CreateItemTypeTooltip",
                    new[] { typeof(RichTextBuilder), typeof(GameItem) });
                if (createTypes != null)
                {
                    _harmony.Patch(createTypes,
                        prefix: new HarmonyMethod(typeof(ItemTypeBadge), nameof(CreateItemTypeTooltipPrefix)));
                }
                else
                {
                    Debug.LogWarning(ModEntry.Tag + "找不到 CommonTooltipHelper.CreateItemTypeTooltip，分类标签不会生效");
                }

                // 2) 提示框显示后摆放标签（两个重载都要挂）
                var show3 = AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                    new[] { typeof(Vector2), typeof(RichText), typeof(int) });
                var show4 = AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                    new[] { typeof(Vector2), typeof(Vector2), typeof(RichText), typeof(int) });

                foreach (var show in new[] { show3, show4 })
                {
                    if (show != null)
                    {
                        _harmony.Patch(show,
                            postfix: new HarmonyMethod(typeof(ItemTypeBadge), nameof(ShowTooltipPostfix)));
                    }
                }

                // 3) 提示框关掉时隐藏
                var hide = AccessTools.Method(typeof(OverlayHandler), "HideTooltip");
                if (hide != null)
                {
                    _harmony.Patch(hide,
                        postfix: new HarmonyMethod(typeof(ItemTypeBadge), nameof(HideTooltipPostfix)));
                }

                Debug.Log(ModEntry.Tag + "分类标签已挂载");
            }
            catch (Exception e)
            {
                Debug.LogError(ModEntry.Tag + "分类标签挂载失败：" + e);
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
                // 卸载失败不影响游戏，忽略
            }

            _harmony = null;
            DestroyBadge();
            Pending.Clear();
        }

        // ---------------------------------------------------------------
        // 挂载点 1：拿到分类，并决定要不要跳过原版文字
        // ---------------------------------------------------------------

        /// <summary>
        /// 返回 false = 不执行原版方法（于是 " [分类]" 那行文字就不出现）。
        /// 参数由 Harmony 按名字注入：__0 是第一个参数（builder），__1 是第二个（item）。
        /// </summary>
        private static bool CreateItemTypeTooltipPrefix(RichTextBuilder __0, GameItem __1)
        {
            try
            {
                Pending.Clear();
                _pendingAt = Time.unscaledTime;

                QoLSettings settings = ModEntry.Settings;
                if (settings == null || !settings.TagsEnabled.Value)
                {
                    return true; // 关闭状态：原版爱怎么写怎么写
                }

                if (__1 == null)
                {
                    return true;
                }

                List<string> types = __1.GetGameItemType();
                if (types == null || types.Count == 0)
                {
                    return true; // 没分类，交给原版（它什么也不会加）
                }

                foreach (string type in types)
                {
                    if (string.IsNullOrEmpty(type))
                    {
                        continue;
                    }

                    string display;
                    try
                    {
                        display = TypeHelper.GetTypeDisplayName(type) ?? "";
                    }
                    catch
                    {
                        display = "";
                    }

                    if (display.Length == 0)
                    {
                        continue;
                    }

                    Pending.Add(new Chip { Type = type, Display = display });
                }

                if (Pending.Count == 0)
                {
                    return true;
                }

                // 原版在这行文字后面还有一个空行，跳过文字时手动补上，保持排版一致
                __0.AddLine("");
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "读取物品分类出错：" + e.Message);
                return true;
            }
        }

        // ---------------------------------------------------------------
        // 挂载点 2/3：显示后摆放，关闭时隐藏
        // ---------------------------------------------------------------

        private static void ShowTooltipPostfix(OverlayHandler __instance)
        {
            try
            {
                QoLSettings settings = ModEntry.Settings;
                if (settings == null || !settings.TagsEnabled.Value)
                {
                    Hide();
                    return;
                }

                if (Pending.Count == 0 || Time.unscaledTime - _pendingAt > StaleSeconds)
                {
                    Hide();
                    return;
                }

                PixelWindow window = __instance != null ? __instance.GetTooltipWindow() : null;
                if (window == null || window.rectTransform == null)
                {
                    Hide();
                    return;
                }

                RectTransform tooltipRect = window.rectTransform;
                if (!Build(tooltipRect))
                {
                    Hide();
                    return;
                }

                Place(tooltipRect);
                if (_badge != null)
                {
                    _badge.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "显示分类标签出错：" + e.Message);
                Hide();
            }
        }

        private static void HideTooltipPostfix()
        {
            Hide();
        }

        // ---------------------------------------------------------------
        // 构建
        // ---------------------------------------------------------------

        private static bool Build(RectTransform tooltipRect)
        {
            Transform parent = tooltipRect.parent;
            if (parent == null)
            {
                return false;
            }

            if (_template == null)
            {
                _template = tooltipRect.GetComponentInChildren<TMP_Text>(true);
            }

            if (_template == null)
            {
                return false; // 提示框里找不到文字模板，没法取字体
            }

            if (_badge == null)
            {
                _badge = new GameObject("QoLTypeBadge", typeof(RectTransform));
                _badge.transform.SetParent(parent, false);
                RectTransform rt = _badge.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
            }

            RectTransform badgeRect = _badge.GetComponent<RectTransform>();

            // 内容没变，直接复用上一次的构建结果
            string key = BuildKey();
            if (_builtParent == parent && _builtKey == key && badgeRect.childCount == Pending.Count)
            {
                return true;
            }

            if (badgeRect.parent != parent)
            {
                badgeRect.SetParent(parent, false);
            }

            // 多出来的子物体删掉
            for (int i = badgeRect.childCount - 1; i >= Pending.Count; i--)
            {
                UnityEngine.Object.Destroy(badgeRect.GetChild(i).gameObject);
            }

            // 不够就补
            for (int i = badgeRect.childCount; i < Pending.Count; i++)
            {
                GameObject chip = CreateChip();
                chip.transform.SetParent(badgeRect, false);
            }

            float padX = TagStyle.PadX;
            float padY = TagStyle.PadY;
            float gap = TagStyle.Gap;

            Widths.Clear();
            float height = 0f;
            for (int i = 0; i < Pending.Count; i++)
            {
                // 先按最终字号把文字配好，再用它自己量宽。
                // 如果拿提示框正文的模板去量，量出来的是未缩放的字宽，
                // 画出来却是 0.85 倍，框就会比文字宽一截。
                RectTransform chipRect = (RectTransform)badgeRect.GetChild(i);
                ConfigureChip(chipRect.gameObject, Pending[i]);

                float w;
                float h;
                try
                {
                    TMP_Text chipText = chipRect.GetComponentInChildren<TMP_Text>(true);
                    Vector2 preferred = chipText != null
                        ? chipText.GetPreferredValues(Pending[i].Display)
                        : _template.GetPreferredValues(Pending[i].Display);

                    w = Mathf.Ceil(preferred.x) + padX * 2f;
                    h = Mathf.Ceil(preferred.y) + padY * 2f;
                }
                catch
                {
                    w = 60f;
                    h = 18f;
                }

                Widths.Add(w);
                height = Mathf.Max(height, h);
            }

            float totalWidth = gap * Mathf.Max(0, Pending.Count - 1);
            foreach (float w in Widths)
            {
                totalWidth += w;
            }

            badgeRect.sizeDelta = new Vector2(totalWidth, height);

            float cursor = totalWidth * -0.5f;
            for (int i = 0; i < Pending.Count; i++)
            {
                RectTransform chipRect = (RectTransform)badgeRect.GetChild(i);

                chipRect.anchorMin = new Vector2(0.5f, 0.5f);
                chipRect.anchorMax = new Vector2(0.5f, 0.5f);
                chipRect.pivot = new Vector2(0.5f, 0.5f);
                chipRect.sizeDelta = new Vector2(Widths[i], height);
                chipRect.anchoredPosition = new Vector2(cursor + Widths[i] * 0.5f, 0f);
                cursor += Widths[i] + gap;
            }

            _builtKey = key;
            _builtParent = parent;
            return Pending.Count > 0;
        }

        /// <summary>把标签内容拼成一个字符串，用来判断要不要重建。</summary>
        private static string BuildKey()
        {
            var sb = new System.Text.StringBuilder();
            foreach (Chip chip in Pending)
            {
                sb.Append(chip.Display).Append(',');
            }
            return sb.ToString();
        }

        private static GameObject CreateChip()
        {
            var chip = new GameObject("Chip", typeof(RectTransform), typeof(Image));
            Image bg = chip.GetComponent<Image>();
            bg.sprite = null;
            bg.type = Image.Type.Simple;
            bg.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(chip.transform, false);

            var text = textGo.GetComponent<TextMeshProUGUI>();
            text.font = _template.font;
            text.fontSharedMaterial = _template.fontSharedMaterial;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.raycastTarget = false;

            RectTransform textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            // 比原版模组更贴边：原版是 (2,1)/(-2,-1)
            textRect.offsetMin = new Vector2(TagStyle.TextInsetX, 0f);
            textRect.offsetMax = new Vector2(-TagStyle.TextInsetX, 0f);

            return chip;
        }

        private static void ConfigureChip(GameObject chip, Chip info)
        {
            ChipColors(info.Type, out Color bg, out Color border);

            Image image = chip.GetComponent<Image>();
            if (image != null)
            {
                // 圆角 + 边框都画在贴图里，颜色由贴图提供，所以底色改成白色不做二次染色
                image.sprite = GetChipSprite(bg, border);
                image.type = Image.Type.Sliced;
                image.fillCenter = true;
                image.color = Color.white;
                image.raycastTarget = false;
            }

            TMP_Text text = chip.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.font = _template.font;
                text.fontSharedMaterial = _template.fontSharedMaterial;
                text.fontSize = Mathf.Max(8f, _template.fontSize * TagStyle.FontScale);
                text.color = Color.white;
                if (text.text != info.Display)
                {
                    text.text = info.Display;
                }
            }
        }

        /// <summary>
        /// 生成（或取缓存）一张圆角矩形贴图。
        ///
        /// 做成九宫格（Sliced）：四个角按原尺寸绘制，中间被拉伸。
        /// 这样不管标签多宽多窄，圆角半径永远不变形，边框粗细也保持 1 像素。
        /// </summary>
        private static Sprite GetChipSprite(Color bg, Color border)
        {
            // 贴图的像素密度跟着画布走，这样圆角半径在屏幕上就是标准的 CornerRadius 像素
            float refPpu = 100f;
            Canvas canvas = _template != null ? _template.GetComponentInParent<Canvas>() : null;
            if (canvas != null && canvas.referencePixelsPerUnit > 0f)
            {
                refPpu = canvas.referencePixelsPerUnit;
            }

            string key = bg.r.ToString("F3") + bg.g.ToString("F3") + bg.b.ToString("F3") + "_" +
                         border.r.ToString("F3") + border.g.ToString("F3") + border.b.ToString("F3") + "_" +
                         refPpu.ToString("F0");

            if (ChipSprites.TryGetValue(key, out Sprite cached) && cached != null)
            {
                return cached;
            }

            int radiusPx = Mathf.Max(1, Mathf.RoundToInt(TagStyle.CornerRadius * Supersample));
            int borderPx = Mathf.Max(1, Mathf.RoundToInt(TagStyle.BorderWidth * Supersample));

            // 边长 = 两个角 + 中间一小段直边（直边会被九宫格拉伸，所以留 1 像素即可）
            int size = radiusPx * 2 + 2;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            float inner = half - radiusPx; // 直边到圆心的距离

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 像素中心到贴图中心的坐标
                    float px = x + 0.5f - half;
                    float py = y + 0.5f - half;

                    // 圆角矩形的有符号距离：负数在内部，正数在外部
                    float qx = Mathf.Abs(px) - inner;
                    float qy = Mathf.Abs(py) - inner;
                    float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
                    float dist = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radiusPx;

                    // 边缘 1 像素做渐变，消除锯齿
                    float alpha = Mathf.Clamp01(0.5f - dist);

                    Color color = (-dist) < borderPx ? border : bg;
                    color.a = alpha;
                    pixels[y * size + x] = color;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            // 边框按 左、下、右、上 的顺序给
            Vector4 spriteBorder = new Vector4(radiusPx, radiusPx, radiusPx, radiusPx);
            Sprite sprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                Supersample * refPpu,
                0,
                SpriteMeshType.FullRect,
                spriteBorder);

            ChipSprites[key] = sprite;
            return sprite;
        }

        // ---------------------------------------------------------------
        // 定位：贴着提示框左上角往上放
        // ---------------------------------------------------------------

        private static void Place(RectTransform tooltipRect)
        {
            if (_badge == null)
            {
                return;
            }

            RectTransform badgeRect = _badge.GetComponent<RectTransform>();
            if (badgeRect == null)
            {
                return;
            }

            Rect rect = tooltipRect.rect;
            Vector3 anchor = tooltipRect.TransformPoint(new Vector3(rect.xMin, rect.yMax, 0f));

            float scaleY = tooltipRect.lossyScale.y;
            if (scaleY <= 0f)
            {
                scaleY = 1f;
            }

            float offsetY = TagStyle.OffsetY * scaleY;
            float halfHeight = badgeRect.rect.height * 0.5f * scaleY;
            float halfWidth = badgeRect.rect.width * 0.5f * scaleY;
            float edgeGap = 1f * scaleY; // 让标签左边和提示框左边对齐

            Vector3 position = anchor + new Vector3(halfWidth + edgeGap, offsetY + halfHeight, 0f);
            position.z = anchor.z;
            badgeRect.position = position;
        }

        private static void Hide()
        {
            if (_badge != null)
            {
                _badge.SetActive(false);
            }
        }

        private static void DestroyBadge()
        {
            if (_badge != null)
            {
                UnityEngine.Object.Destroy(_badge);
            }

            _badge = null;
            _template = null;
            _builtParent = null;
            _builtKey = "";
            Widths.Clear();
        }

        // ---------------------------------------------------------------
        // 配色：按分类常量取底色和边框色
        // ---------------------------------------------------------------

        private static void ChipColors(string typeConst, out Color bg, out Color border)
        {
            switch ((typeConst ?? "").ToUpperInvariant())
            {
                // 违禁品 / 受限品：红
                case "CONTRABAND":
                case "RESTRICTED_1":
                case "RESTRICTED_2":
                case "RESTRICTED_3":
                    bg = Rgb(87, 26, 28);
                    border = Rgb(255, 148, 133);
                    break;

                // 医疗、水、器官、毒品：蓝
                case "MEDICAL_TOOL":
                case "WATER_RATION":
                case "WATER_PURIFICATION_SUPPLY":
                case "MEDICAL":
                case "WATER":
                case "ORGAN":
                case "NARCOTIC":
                case "POISON":
                    bg = Rgb(21, 101, 192);
                    border = Rgb(115, 192, 255);
                    break;

                // 奢侈品、艺术品：粉
                case "LUXURY_ITEM":
                case "ART":
                    bg = Rgb(196, 44, 122);
                    border = Rgb(255, 154, 213);
                    break;

                // 酒、药剂：黑
                case "ALCOHOL":
                case "SUBSTANCE":
                    bg = Rgb(18, 18, 18);
                    border = Rgb(208, 208, 208);
                    break;

                // 日用品：青
                case "HOUSEHOLD_GOOD":
                    bg = Rgb(0, 124, 145);
                    border = Rgb(92, 225, 242);
                    break;

                // 食物、饮料、种子、农资：绿
                case "FRESH_FOOD":
                case "BREVAGE":
                case "TREAT":
                case "PROCESSED_FOOD":
                case "MODIFIED_SEED":
                case "SEED":
                case "FOOD":
                case "FARMING_SUPPLIES":
                    bg = Rgb(46, 125, 50);
                    border = Rgb(130, 217, 139);
                    break;

                // 武器、弹药、护甲、枪械配件：墨绿
                case "AMMUNITION":
                case "MELEE_WEAPON":
                case "GUN_MOD":
                case "FIREARM":
                case "ARMOR":
                case "MODDABLE":
                case "MUNITION":
                case "WEAPON":
                    bg = Rgb(0, 105, 92);
                    border = Rgb(99, 216, 204);
                    break;

                // 机器、材料、模组、工具：紫
                case "ALARM_MODULE":
                case "MACHINE":
                case "MATERIAL":
                case "FILTER":
                case "MODULE":
                case "ENERGY_CELL":
                case "BASIC_MATERIAL":
                case "PRINTER_MODULE":
                case "TOOL":
                case "NODE":
                case "REFINED_MATERIAL":
                case "EVAPORATOR_MODULE":
                case "HYDROPONIC_MODULE":
                    bg = Rgb(85, 58, 133);
                    border = Rgb(195, 166, 255);
                    break;

                // 其它：灰
                default:
                    bg = Rgb(85, 85, 85);
                    border = Rgb(192, 192, 192);
                    break;
            }
        }

        private static Color Rgb(byte r, byte g, byte b)
        {
            return new Color32(r, g, b, 255);
        }
    }
}
