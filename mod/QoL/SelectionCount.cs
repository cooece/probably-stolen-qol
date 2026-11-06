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
    /// 文字区用贴图比例锚在遮罩形状内部的安全矩形里，再套一层 RectMask2D 硬裁剪，
    /// 保证怎么算都不会跑到形状外。文字颜色固定 #172C3E，和屏幕原生文字一致。
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
        private const int MaxNameChars = 5;

        // 遮罩形状在 960×540 图里的范围（图像坐标，原点左上）
        private const float ShapeLeft = 676f / 960f;
        private const float ShapeRight = 753f / 960f;
        private const float ShapeTop = 184f / 540f;
        private const float ShapeBottom = 237f / 540f;

        // 文字安全区：取形状最窄的下半段，再各留 2~4 px 边距
        private const float TextLeft = 685f / 960f;
        private const float TextRight = 743f / 960f;
        private const float TextTop = 188f / 540f;
        private const float TextBottom = 233f / 540f;

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
        private static TMP_Text _text;

        private static SpriteRenderer _register;
        private static Sprite _patchSprite;
        private static bool _patchLoadFailed;

        private static float _nextRefresh;
        private static bool _failed;
        private static string _lastText = "";
        private static int _rows = 3;

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

            string text = Build(items);
            if (text == null)
            {
                Hide();
                return;
            }

            Show(text);
        }

        // ---------------------------------------------------------------
        // 统计
        // ---------------------------------------------------------------

        private static string Build(List<GameItemElement> items)
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

            if (Kinds.Count == 0) return null;

            Kinds.Sort((a, b) => a.Units == b.Units
                ? string.CompareOrdinal(a.Name, b.Name)
                : b.Units.CompareTo(a.Units));

            int total = 0;
            for (int i = 0; i < Kinds.Count; i++) total += Kinds[i].Units;

            Builder.Length = 0;
            Builder.Append("<b>商品统计</b>\n");

            // 屏幕小，只列数量最多的前 3 种；名字太长截一下
            int shown = Mathf.Min(Kinds.Count, MaxKinds);
            for (int i = 0; i < shown; i++)
            {
                Kind k = Kinds[i];
                // 截断后连同省略号也不超过 MaxNameChars 个字符
                string name = k.Name.Length > MaxNameChars
                    ? k.Name.Substring(0, MaxNameChars - 1) + "…"
                    : k.Name;

                Builder.Append(name).Append(" ×").Append(k.Units).Append('\n');
            }

            // 合计始终算整批物品，不因为只列 3 种而少算
            if (Kinds.Count > shown)
            {
                Builder.Append("还有 ").Append(Kinds.Count - shown).Append(" 种\n");
                Builder.Append("共 ").Append(total).Append(" 个");
                _rows = shown + 3;
            }
            else
            {
                Builder.Append("共 ").Append(Kinds.Count).Append(" 种，")
                       .Append(total).Append(" 个");
                _rows = shown + 2;
            }
            return Builder.ToString();
        }

        // ---------------------------------------------------------------
        // 叠加层
        // ---------------------------------------------------------------

        private static void Show(string text)
        {
            if (!Ensure()) return;

            if (text != _lastText)
            {
                _lastText = text;
                _text.text = text;
            }

            if (!_root.activeSelf) _root.SetActive(true);
            Reposition();
        }

        private static void Hide()
        {
            _lastText = "";
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
            _text = null;
            _register = null;
            _lastText = "";
            _rows = 3;
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

                // 文字：铺满裁剪框
                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(_clip, false);
                RectTransform tr = (RectTransform)textGo.transform;
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = Vector2.zero;
                tr.offsetMax = Vector2.zero;

                _text = textGo.AddComponent<TextMeshProUGUI>();
                _text.font = CustomerBadge.ResolveFont();
                _text.color = new Color32(0x17, 0x2C, 0x3E, 0xFF); // #172C3E
                _text.alignment = TextAlignmentOptions.TopLeft;
                _text.enableWordWrapping = false;
                _text.overflowMode = TextOverflowModes.Overflow; // 越界交给 RectMask2D 裁掉
                _text.margin = Vector4.zero;
                _text.raycastTarget = false;

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

                // 字号同时受行数和宽度约束，保证每行都塞得进安全矩形
                float wLogical = _patch.rect.width * (TextRight - TextLeft);
                float hLogical = _patch.rect.height * (TextBottom - TextTop);
                // 1.35 留足行距，避免不同中文字体的行高把最后一行挤出裁剪框
                float byHeight = hLogical / (Mathf.Max(3, _rows) * 1.35f);
                float byWidth = wLogical / 7.8f;
                _text.fontSize = Mathf.Clamp(Mathf.Min(byHeight, byWidth), 8f, 40f);
            }
            catch (Exception e)
            {
                QLog.Warn("框选统计：定位失败 —— " + e.Message);
            }
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
