using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QoL
{
    /// <summary>
    /// 框选统计：框中一批物品时，右侧收银机的浅蓝屏幕切成统计页。
    ///
    /// 遮罩图 negociation_tablet_ScreenOverlay.png 是玩家画的「干净屏幕补丁」：
    /// 整图 960×540，只有屏幕那块不透明（上宽下窄、两侧斜切），其余全透明。
    /// 把它铺到游戏里收银机 SpriteRenderer 对应的整张贴图矩形上，正好盖掉原有的
    /// Useful info，同时不会碰到机器其它部分。
    ///
    /// 版式是上中下三块各自独立锚定、位置恒定，框选 1 种还是 3 种都不会移动：
    ///   顶部一行：标题「商品统计」，水平居中，贴安全区顶；
    ///   中间三行：物品明细，靠左侧黄线左对齐，贴着标题下方往下排；
    ///             名字按实际剩余宽度尽量多显示，放不下才省略，整行不越过右侧黄线；
    ///   底部一行：以中线为界左「种类 N」右「数量 M」，各自左对齐、贴安全区底。
    /// 字号按满 5 行固定算，不随物品数量变化。文字区套一层 RectMask2D 硬裁剪，
    /// 保证永远出不了遮罩形状；颜色固定 #172C3E，和屏幕原生文字一致。
    ///
    /// 位置不写死屏幕比例：运行时找收银机 SpriteRenderer，用 bounds 经主相机投影出
    /// 整张贴图在屏幕上的矩形。换分辨率、相机缩放、换美化贴图都不会跑偏。
    ///
    /// 数据来源：ItemMultiSelectHandler 的私有 selectedItems，反射读取。
    /// 屏幕小，最多列数量最多的 3 种，合计仍算整批。
    /// </summary>
    internal static class SelectionCount
    {
        private const float RefreshInterval = 0.1f;
        private const int SortingOrder = 960;
        private const int MaxKinds = 3;
        private const int RowSlots = 5; // 标题 1 + 明细 3 + 合计 1

        // 遮罩形状在 960×540 图里的范围（图像坐标，原点左上）
        private const float ShapeLeft = 676f / 960f;
        private const float ShapeRight = 753f / 960f;
        private const float ShapeTop = 184f / 540f;
        private const float ShapeBottom = 237f / 540f;

        // 文字安全区：横向取形状最窄段内缩，纵向尽量撑到形状上下沿
        private const float TextLeft = 685f / 960f;
        private const float TextRight = 743f / 960f;
        private const float TextTop = 186f / 540f;
        private const float TextBottom = 236f / 540f;

        // 兜底：找不到收银机对象时，按真实截图估的屏幕区域比例（相对游戏区域）
        private const float FallbackLeft = 0.5595f;
        private const float FallbackRight = 0.8284f;
        private const float FallbackTopFromBottom = 0.7780f;
        private const float FallbackBottomFromBottom = 0.4892f;

        private static readonly FieldInfo SelectedField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        private sealed class Kind
        {
            public string Name;
            public int Units;
        }

        private static readonly List<Kind> Kinds = new List<Kind>();
        private static readonly Dictionary<string, int> Index = new Dictionary<string, int>();
        private static readonly StringBuilder Builder = new StringBuilder();

        private static GameObject _root;
        private static Canvas _canvas;
        private static RectTransform _patch;
        private static Image _patchImage;
        private static RectTransform _clip;
        private static TMP_Text _title;
        private static TMP_Text _items;
        private static TMP_Text _footerLeft;
        private static TMP_Text _footerRight;

        private static SpriteRenderer _register;
        private static Sprite _patchSprite;
        private static bool _patchLoadFailed;

        private static float _nextRefresh;
        private static bool _failed;
        private static string _leftText = "";
        private static string _rightText = "";

        private static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.SelectionCountEnabled.Value;
            }
        }

        // ---------------------------------------------------------------
        // 每帧驱动（QoLUpdate 调用）
        // ---------------------------------------------------------------

        internal static void Tick()
        {
            if (!Enabled)
            {
                Hide();
                return;
            }

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Refresh();
        }

        private static void Refresh()
        {
            ItemMultiSelectHandler current = ItemMultiSelectHandler.current;
            List<GameItemElement> items = current != null && SelectedField != null
                ? SelectedField.GetValue(current) as List<GameItemElement>
                : null;

            if (items == null || items.Count == 0)
            {
                Hide();
                return;
            }

            if (!Build(items))
            {
                Hide();
                return;
            }

            Show();
        }

        // ---------------------------------------------------------------
        // 统计
        // ---------------------------------------------------------------

        private static bool Build(List<GameItemElement> items)
        {
            Kinds.Clear();
            Index.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                GameItemElement item = items[i];
                if (item == null) continue;

                try
                {
                    if (item.IsDestroyed()) continue;

                    string name = item.GetDisplayName(false);
                    if (string.IsNullOrEmpty(name)) name = "?";
                    int units = item.unitCount < 1 ? 1 : item.unitCount;

                    int at;
                    if (!Index.TryGetValue(name, out at))
                    {
                        at = Kinds.Count;
                        Index[name] = at;
                        Kinds.Add(new Kind { Name = name });
                    }

                    Kinds[at].Units += units;
                }
                catch
                {
                    // 单个物品读不出来就跳过，不影响其它行
                }
            }

            if (Kinds.Count == 0) return false;

            Kinds.Sort((a, b) => a.Units == b.Units
                ? string.CompareOrdinal(a.Name, b.Name)
                : b.Units.CompareTo(a.Units));

            int total = 0;
            for (int i = 0; i < Kinds.Count; i++) total += Kinds[i].Units;

            // 明细文本在 Reposition 里按可用宽度生成；这里只留合计数字（算整批）
            _leftText = "种类 " + Kinds.Count;
            _rightText = "数量 " + total;
            return true;
        }

        // ---------------------------------------------------------------
        // 叠加层
        // ---------------------------------------------------------------

        private static void Show()
        {
            if (!Ensure()) return;
            if (!_root.activeSelf) _root.SetActive(true);
            Reposition();
        }

        private static void Hide()
        {
            try
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
            }
            catch
            {
                // 场景卸载途中拿不到，忽略
            }
        }

        internal static void Remove()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch
            {
                // 忽略
            }

            _root = null;
            _canvas = null;
            _patch = null;
            _patchImage = null;
            _clip = null;
            _title = null;
            _items = null;
            _footerLeft = null;
            _footerRight = null;
            _register = null;
            _leftText = "";
            _rightText = "";
            _failed = false;
        }

        private static bool Ensure()
        {
            if (_root != null) return true;
            if (_failed) return false;

            try
            {
                _root = new GameObject("QoL_SelectionCount", typeof(RectTransform));
                UnityEngine.Object.DontDestroyOnLoad(_root);

                _canvas = _root.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = SortingOrder;
                CustomUIManager.AddScreenScaler(_root);

                // 屏幕补丁：整张 960×540 图铺到收银机贴图矩形上，透明处不遮挡
                var patchGo = new GameObject("ScreenPatch", typeof(RectTransform));
                patchGo.transform.SetParent(_root.transform, false);
                _patch = (RectTransform)patchGo.transform;
                _patch.anchorMin = Vector2.zero;
                _patch.anchorMax = Vector2.zero;
                _patch.pivot = Vector2.zero;

                _patchImage = patchGo.AddComponent<Image>();
                _patchImage.raycastTarget = false;
                _patchImage.preserveAspect = false;
                _patchImage.sprite = LoadPatch();
                _patchImage.enabled = false; // 定位准确才盖，避免错位的重影

                // 裁剪框：用贴图比例锚在补丁内部，文字永远出不了这个矩形
                var clipGo = new GameObject("Clip", typeof(RectTransform));
                clipGo.transform.SetParent(_patch, false);
                _clip = (RectTransform)clipGo.transform;
                _clip.anchorMin = new Vector2(TextLeft, 1f - TextBottom);
                _clip.anchorMax = new Vector2(TextRight, 1f - TextTop);
                _clip.offsetMin = Vector2.zero;
                _clip.offsetMax = Vector2.zero;
                clipGo.AddComponent<RectMask2D>();

                // 上中下三块用比例锚点钉死：顶 1 行 / 中 3 行 / 底 1 行。
                // 矩形一次性设好，运行时不改，框选几种商品都不会移动。
                _title = MakeBlock("Title", TextAlignmentOptions.Top,
                                   new Vector2(0f, 0.8f), Vector2.one);
                _title.text = "<b>商品统计</b>"; // 固定不变
                _items = MakeBlock("Items", TextAlignmentOptions.TopLeft,
                                   new Vector2(0f, 0.2f), new Vector2(1f, 0.8f));
                _footerLeft = MakeBlock("Kind", TextAlignmentOptions.BottomLeft,
                                        Vector2.zero, new Vector2(0.5f, 0.2f));
                _footerRight = MakeBlock("Total", TextAlignmentOptions.BottomLeft,
                                         new Vector2(0.5f, 0f), new Vector2(1f, 0.2f));

                _root.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                QLog.Warn("框选统计：叠加层创建失败 —— " + e.Message);
                Remove();
                return false;
            }
        }

        private static void SetupText(TMP_Text t)
        {
            t.font = CustomerBadge.ResolveFont();
            t.color = new Color32(0x17, 0x2C, 0x3E, 0xFF); // #172C3E
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow; // 越界交给 RectMask2D 裁掉
            t.raycastTarget = false;
        }

        /// <summary>裁剪框里的一个文字块，用比例锚点钉在固定位置。</summary>
        private static TMP_Text MakeBlock(string name, TextAlignmentOptions alignment,
                                          Vector2 aMin, Vector2 aMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_clip, false);
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            TMP_Text t = go.AddComponent<TextMeshProUGUI>();
            SetupText(t);
            t.alignment = alignment;
            return t;
        }

        /// <summary>从 MOD 目录读玩家画的干净屏幕补丁，只读一次。</summary>
        private static Sprite LoadPatch()
        {
            if (_patchSprite != null) return _patchSprite;
            if (_patchLoadFailed) return null;

            try
            {
                string dir = Path.GetDirectoryName(typeof(SelectionCount).Assembly.Location);
                string file = Path.Combine(dir, "negociation_tablet_ScreenOverlay.png");
                if (!File.Exists(file))
                {
                    _patchLoadFailed = true;
                    QLog.Warn("框选统计：找不到遮罩图 " + file);
                    return null;
                }

                byte[] data = File.ReadAllBytes(file);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                if (!tex.LoadImage(data))
                {
                    _patchLoadFailed = true;
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                _patchSprite = Sprite.Create(tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                return _patchSprite;
            }
            catch (Exception e)
            {
                _patchLoadFailed = true;
                QLog.Warn("框选统计：遮罩图加载失败 —— " + e.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------
        // 定位：跟着游戏里真实的收银机走
        // ---------------------------------------------------------------

        private static void Reposition()
        {
            try
            {
                float scale = _canvas != null && _canvas.scaleFactor > 0.001f
                    ? _canvas.scaleFactor : 1f;

                Rect texRect;
                bool exact = TryGetTextureScreenRect(out texRect);
                if (!exact) texRect = FallbackTextureRect();

                _patch.position = new Vector3(texRect.xMin, texRect.yMin, 0f);
                _patch.sizeDelta = new Vector2(texRect.width / scale, texRect.height / scale);
                _patchImage.enabled = exact && _patchImage.sprite != null;

                // 用补丁自己的 rect 算安全区（非 stretch 锚点，sizeDelta 即刻生效）
                float w = _patch.rect.width * (TextRight - TextLeft);
                float h = _patch.rect.height * (TextBottom - TextTop);
                float lineH = h / RowSlots;

                // 字号按满 5 行固定算，不随物品数量变化 → 三块位置永远不动
                float font = Mathf.Clamp(Mathf.Min(lineH / 1.35f, w / 7.8f), 8f, 40f);
                float pad = font * 0.35f; // 商品名与「种类」都靠左侧黄线对齐

                _title.fontSize = font;
                _items.fontSize = font;
                _footerLeft.fontSize = font;
                _footerRight.fontSize = font;

                _title.margin = Vector4.zero;
                _items.margin = new Vector4(pad, 0f, 0f, 0f);
                _footerLeft.margin = new Vector4(pad, 0f, 0f, 0f);
                _footerRight.margin = new Vector4(pad, 0f, 0f, 0f);

                // 明细按实际可容纳宽度生成：能全显示就全显示，放不下才省略，
                // 名字再长也不会越过右边黄线（黄线就是安全区右边界）。
                float avail = w - pad - font * 0.2f; // 右侧再留一点余量
                Builder.Length = 0;
                int shown = Mathf.Min(Kinds.Count, MaxKinds);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) Builder.Append('\n');
                    AppendFit(Builder, Kinds[i], avail);
                }
                _items.text = Builder.ToString();
                _footerLeft.text = _leftText;
                _footerRight.text = _rightText;
            }
            catch (Exception e)
            {
                QLog.Warn("框选统计：定位失败 —— " + e.Message);
            }
        }

        /// <summary>塞进尽可能多的字，实在放不下才加省略号，保证整行不越线。</summary>
        private static void AppendFit(StringBuilder sb, Kind k, float avail)
        {
            string suffix = " ×" + k.Units;
            string name = k.Name;

            if (Fits(name + suffix, avail))
            {
                sb.Append(name).Append(suffix);
                return;
            }

            for (int n = name.Length - 1; n > 0; n--)
            {
                string cut = name.Substring(0, n) + "…";
                if (Fits(cut + suffix, avail))
                {
                    sb.Append(cut).Append(suffix);
                    return;
                }
            }

            sb.Append('…').Append(suffix);
        }

        private static bool Fits(string line, float avail)
        {
            float width = _items.GetPreferredValues(line).x;
            // 字体偶尔未就绪会返回 0，退回按每字一个字宽粗估，宁可多截也不越线
            if (width <= 0.001f) return line.Length * _items.fontSize <= avail;
            return width <= avail;
        }

        /// <summary>找不到收银机对象时，按屏幕区域比例反推整张贴图矩形。</summary>
        private static Rect FallbackTextureRect()
        {
            float bx = 0f;
            float by = 0f;
            float areaW = LetterboxManager.GameAreaWidth;
            float areaH = LetterboxManager.GameAreaHeight;
            if (areaW > 1f && areaH > 1f)
            {
                bx = LetterboxManager.GameAreaX;
                by = LetterboxManager.GameAreaY;
            }
            else
            {
                areaW = Screen.width;
                areaH = Screen.height;
            }

            Rect shape = Rect.MinMaxRect(
                bx + FallbackLeft * areaW,
                by + FallbackBottomFromBottom * areaH,
                bx + FallbackRight * areaW,
                by + FallbackTopFromBottom * areaH);

            float texW = shape.width / (ShapeRight - ShapeLeft);
            float texH = shape.height / (ShapeBottom - ShapeTop);
            float xMin = shape.xMin - ShapeLeft * texW;
            float yMax = shape.yMax + ShapeTop * texH;
            return Rect.MinMaxRect(xMin, yMax - texH, xMin + texW, yMax);
        }

        /// <summary>整张收银机贴图在屏幕上的矩形（屏幕像素，原点左下）。</summary>
        private static bool TryGetTextureScreenRect(out Rect rect)
        {
            rect = default(Rect);

            if (_register == null) _register = FindRegister();
            if (_register == null || _register.sprite == null) return false;

            Camera cam = Camera.main;
            if (cam == null) return false;

            Sprite sp = _register.sprite;
            Texture2D tex = sp.texture;
            if (tex == null) return false;

            Bounds b = _register.bounds;
            Vector3 lo = cam.WorldToScreenPoint(b.min);
            Vector3 hi = cam.WorldToScreenPoint(b.max);

            // bounds 对应 sp.rect；换算到整张 texture 的范围（sprite 可能是从图集裁的）
            Rect spr = sp.rect;
            if (spr.width < 1f || spr.height < 1f) return false;
            float u0 = (0f - spr.x) / spr.width;
            float u1 = (tex.width - spr.x) / spr.width;
            float v0 = (0f - spr.y) / spr.height;
            float v1 = (tex.height - spr.y) / spr.height;

            float x0 = Mathf.Lerp(lo.x, hi.x, u0);
            float x1 = Mathf.Lerp(lo.x, hi.x, u1);
            float y0 = Mathf.Lerp(lo.y, hi.y, v0);
            float y1 = Mathf.Lerp(lo.y, hi.y, v1);

            rect = Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(y0, y1),
                                   Mathf.Max(x0, x1), Mathf.Max(y0, y1));
            return rect.width > 8f && rect.height > 8f;
        }

        private static SpriteRenderer FindRegister()
        {
            SpriteRenderer[] all = Resources.FindObjectsOfTypeAll<SpriteRenderer>();
            for (int i = 0; i < all.Length; i++)
            {
                SpriteRenderer sr = all[i];
                if (sr == null || sr.sprite == null) continue;
                if (!sr.gameObject.scene.IsValid()) continue;

                string a = (sr.sprite.name ?? "").Replace(' ', '_');
                string t = sr.sprite.texture != null
                    ? (sr.sprite.texture.name ?? "").Replace(' ', '_') : "";

                // 原版叫 negociation tablet，美化 MOD 换成 nightshift_negociation_tablet
                if (a.IndexOf("negociation_tablet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("negociation_tablet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return sr;
                }
            }
            return null;
        }
    }
}
