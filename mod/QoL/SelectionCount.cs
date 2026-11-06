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
    /// 框选统计：框中一批物品时，右侧收银机的屏幕切成统计页。
    ///
    /// 做法：把玩家画的「干净收银机」遮罩图（negociation_tablet_Mask.png，
    /// 就是把屏幕上原有的 Useful info 擦掉那版）按游戏里收银机的真实位置盖上去，
    /// 再把统计文字写进那块浅蓝屏幕里。遮罩图和工坊美化 MOD 的贴图同为 960×540、
    /// 机器像素位置完全一致，所以能严丝合缝地盖住，不需要改任何别的 MOD 文件。
    ///
    /// 位置不写死屏幕比例：运行时找到收银机的 SpriteRenderer，用它的 bounds 经主相机
    /// 投影出整张贴图在屏幕上的矩形，再按贴图内部比例取出浅蓝屏幕。换分辨率、
    /// 相机缩放、换美化贴图都不会跑偏。找不到对象时退回按真实截图估的比例。
    ///
    /// 数据来源：ItemMultiSelectHandler 的私有 selectedItems，反射读取。
    /// 屏幕小，最多列数量最多的 3 种，合计仍算整批。
    /// </summary>
    internal static class SelectionCount
    {
        private const float RefreshInterval = 0.1f;
        private const int SortingOrder = 960;
        private const int MaxKinds = 3;
        private const int MaxNameChars = 9;

        // 浅蓝屏幕在 960×540 遮罩图里的范围（图像坐标，原点左上）
        private const float ScreenLeft = 661f / 960f;
        private const float ScreenRight = 770f / 960f;
        private const float ScreenTop = 183f / 540f;
        private const float ScreenBottom = 245f / 540f;

        // 兜底位置：找不到收银机对象时，按真实游戏截图估的游戏区域比例
        private const float FallbackX = 0.519f;
        private const float FallbackW = 0.258f;
        private const float FallbackTopFromBottom = 0.855f;
        private const float FallbackH = 0.265f;

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
        private static RectTransform _mask;
        private static Image _maskImage;
        private static RectTransform _panel;
        private static TMP_Text _text;

        private static SpriteRenderer _register;
        private static Sprite _maskSprite;
        private static bool _maskLoadFailed;

        private static float _nextRefresh;
        private static bool _failed;
        private static string _lastText = "";

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
            Builder.Append("<color=#FFE9A8><size=120%><b>商品统计</b></size></color>\n");

            // 屏幕小，只列数量最多的前 3 种；名字太长截一下
            int shown = Mathf.Min(Kinds.Count, MaxKinds);
            for (int i = 0; i < shown; i++)
            {
                Kind k = Kinds[i];
                string name = k.Name.Length > MaxNameChars
                    ? k.Name.Substring(0, MaxNameChars) + "…"
                    : k.Name;

                Builder.Append("<color=#FFD54A>").Append(name)
                       .Append(" ×").Append(k.Units).Append("</color>\n");
            }

            // 合计始终算整批物品，不因为只列 3 种而少算
            Builder.Append("<color=#FFE9A8>");
            if (Kinds.Count > shown)
            {
                Builder.Append("还有 ").Append(Kinds.Count - shown)
                       .Append(" 种，共 ").Append(total).Append(" 个");
            }
            else
            {
                Builder.Append("共 ").Append(Kinds.Count)
                       .Append(" 种，").Append(total).Append(" 个");
            }
            Builder.Append("</color>");
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
            _mask = null;
            _maskImage = null;
            _panel = null;
            _text = null;
            _register = null;
            _lastText = "";
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

                // 遮罩：整张 960×540 图，左下角对齐；盖住收银机原有的 Useful info
                var maskGo = new GameObject("Mask", typeof(RectTransform));
                maskGo.transform.SetParent(_root.transform, false);
                _mask = (RectTransform)maskGo.transform;
                _mask.anchorMin = Vector2.zero;
                _mask.anchorMax = Vector2.zero;
                _mask.pivot = Vector2.zero;

                _maskImage = maskGo.AddComponent<Image>();
                _maskImage.raycastTarget = false;
                _maskImage.preserveAspect = false;
                Sprite mask = LoadMask();
                _maskImage.sprite = mask;
                _maskImage.enabled = mask != null;

                // 文字：左上角对齐到浅蓝屏幕左上角
                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(_root.transform, false);
                _panel = (RectTransform)textGo.transform;
                _panel.anchorMin = new Vector2(0f, 1f);
                _panel.anchorMax = new Vector2(0f, 1f);
                _panel.pivot = new Vector2(0f, 1f);

                _text = textGo.AddComponent<TextMeshProUGUI>();
                _text.font = CustomerBadge.ResolveFont();
                _text.alignment = TextAlignmentOptions.TopLeft;
                _text.enableWordWrapping = false;
                _text.overflowMode = TextOverflowModes.Overflow;
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

        /// <summary>从 MOD 目录读玩家画的干净收银机图，只读一次。</summary>
        private static Sprite LoadMask()
        {
            if (_maskSprite != null) return _maskSprite;
            if (_maskLoadFailed) return null;

            try
            {
                string dir = Path.GetDirectoryName(typeof(SelectionCount).Assembly.Location);
                string file = Path.Combine(dir, "negociation_tablet_Mask.png");
                if (!File.Exists(file))
                {
                    _maskLoadFailed = true;
                    QLog.Warn("框选统计：找不到遮罩图 " + file);
                    return null;
                }

                byte[] data = File.ReadAllBytes(file);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                if (!tex.LoadImage(data))
                {
                    _maskLoadFailed = true;
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                _maskSprite = Sprite.Create(tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                return _maskSprite;
            }
            catch (Exception e)
            {
                _maskLoadFailed = true;
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
                if (!exact)
                {
                    float areaW = LetterboxManager.GameAreaWidth;
                    float areaH = LetterboxManager.GameAreaHeight;
                    if (areaW <= 1f || areaH <= 1f)
                    {
                        areaW = Screen.width;
                        areaH = Screen.height;
                    }

                    // 兜底只有屏幕区域，遮罩整图按同一比例反推
                    float x = LetterboxManager.GameAreaX + FallbackX * areaW;
                    float yTop = LetterboxManager.GameAreaY + FallbackTopFromBottom * areaH;
                    float w = FallbackW * areaW;
                    float h = FallbackH * areaH;
                    texRect = Rect.MinMaxRect(
                        x - w * (ScreenLeft / (ScreenRight - ScreenLeft)),
                        yTop - h * ((1f - ScreenTop) / (ScreenBottom - ScreenTop)),
                        x + w * ((1f - ScreenRight) / (ScreenRight - ScreenLeft)) + w,
                        yTop + h * (ScreenTop / (ScreenBottom - ScreenTop)));
                }

                // 遮罩：整张贴图铺到 texRect
                _mask.position = new Vector3(texRect.xMin, texRect.yMin, 0f);
                _mask.sizeDelta = new Vector2(texRect.width / scale, texRect.height / scale);
                // 定位不准时宁可不盖，避免出现两台收银机错位的重影
                _maskImage.enabled = exact && _maskImage.sprite != null;

                // 文字：贴图里浅蓝屏幕那一小块
                float sx0 = Mathf.Lerp(texRect.xMin, texRect.xMax, ScreenLeft);
                float sx1 = Mathf.Lerp(texRect.xMin, texRect.xMax, ScreenRight);
                float syTop = Mathf.Lerp(texRect.yMax, texRect.yMin, ScreenTop);
                float syBottom = Mathf.Lerp(texRect.yMax, texRect.yMin, ScreenBottom);

                float wLogical = (sx1 - sx0) / scale;
                float hLogical = (syTop - syBottom) / scale;
                _panel.position = new Vector3(sx0, syTop, 0f);
                _panel.sizeDelta = new Vector2(wLogical, hLogical);

                // 字号跟着屏幕大小走；留边距避开两侧斜切角
                _text.fontSize = Mathf.Clamp(hLogical / 6.5f, 12f, 56f);
                _text.margin = new Vector4(wLogical * 0.09f, hLogical * 0.07f,
                                           wLogical * 0.09f, hLogical * 0.04f);
            }
            catch (Exception e)
            {
                QLog.Warn("框选统计：定位失败 —— " + e.Message);
            }
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
