using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QoL
{
    /// <summary>
    /// 顾客到店提示：人物脑袋右侧的一小块带底色和描边的文字牌。
    ///
    /// 外观直接照抄游戏自己的小提示框（就是鼠标悬停时那种「自动贩卖机」小标签）：
    ///   - 底色 #292234（游戏提示框的板面色，从截图里取的同一像素）
    ///   - 描边 #9B4A3D、2 像素、直角（游戏 UI 全是直角，没有圆角）
    ///   - 文字离边框留一点内边距，牌子的宽高跟着文字走
    ///
    /// 位置写死在人物脑袋右边，不跟鼠标走，也不需要玩家去关；
    /// 顾客走了/换成普通顾客，牌子自己消失。
    ///
    /// 技术上是一个独立的 ScreenSpaceOverlay Canvas + 两层 Image + 一个 TMP 文字。
    /// 位置用"游戏区域内的相对比例"算，所以换分辨率也不会跑偏。
    /// </summary>
    internal static class CustomerBadge
    {
        // ---- 位置：都在 1920×1080 的游戏区域里量出来，用比例表示 ----

        /// <summary>横向位置：从游戏区域左边算起，0.527 ≈ 人物脑袋右侧。</summary>
        private const float AnchorFractionX = 0.527f;

        /// <summary>纵向位置：从游戏区域底边算起，0.853 ≈ 人物眼睛高度。</summary>
        private const float AnchorFractionY = 0.853f;

        /// <summary>字号（游戏区域 1080 高时的实际像素）。</summary>
        private const float FontSize = 20f;

        // ---- 外观：和游戏提示框同一套配色 ----

        /// <summary>牌面底色（= 游戏提示框 tan 层的 #292234）。</summary>
        private static readonly Color PlateColor = new Color32(0x29, 0x22, 0x34, 0xFF);

        /// <summary>描边色（= 游戏提示框外框的 #9B4A3D）。</summary>
        private static readonly Color BorderColor = new Color32(0x9B, 0x4A, 0x3D, 0xFF);

        /// <summary>描边粗细，和游戏提示框一样是 2 像素。</summary>
        private const float BorderWidth = 2f;

        /// <summary>文字到描边的横向内边距。</summary>
        private const float PadX = 7f;

        /// <summary>文字到描边的纵向内边距。</summary>
        private const float PadY = 4f;

        /// <summary>盖在游戏自己的 UI 之上。</summary>
        private const int SortingOrder = 950;

        private static GameObject _root;
        private static RectTransform _rect;
        private static TMP_Text _text;

        private static string _current = "";
        private static Color _currentColor = Color.white;
        private static bool _loggedError;

        /// <summary>显示一行提示。内容/颜色没变就只重新摆一下位置。</summary>
        internal static void Show(string content, Color color)
        {
            try
            {
                ShowCore(content, color);
            }
            catch (Exception e)
            {
                // 提示牌出问题不该影响游戏，只在第一次记一条，避免刷屏
                if (!_loggedError)
                {
                    _loggedError = true;
                    Debug.LogWarning(ModEntry.Tag + "顾客提示牌异常：" + e);
                }
            }
        }

        private static void ShowCore(string content, Color color)
        {
            if (string.IsNullOrEmpty(content))
            {
                Hide();
                return;
            }

            if (!Ensure())
            {
                return;
            }

            bool same = _current == content && _currentColor == color && _root.activeSelf;

            if (!same)
            {
                _current = content;
                _currentColor = color;

                try
                {
                    _text.text = content;
                    _text.color = color;
                    Resize();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(ModEntry.Tag + "顾客提示文字设置失败：" + e.Message);
                    return;
                }

                _root.SetActive(true);
            }

            // 分辨率可能变了，位置每轮都重算一次，很便宜
            Reposition();
        }

        internal static void Hide()
        {
            _current = "";
            try
            {
                if (_root != null && _root.activeSelf)
                {
                    _root.SetActive(false);
                }
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
                if (_root != null)
                {
                    UnityEngine.Object.Destroy(_root);
                }
            }
            catch
            {
                // 忽略
            }

            _root = null;
            _rect = null;
            _text = null;
            _current = "";
        }

        // ---------------------------------------------------------------
        // 建对象（只建一次）
        // ---------------------------------------------------------------

        private static bool Ensure()
        {
            if (_root != null)
            {
                return true;
            }

            try
            {
                _root = new GameObject("QoL_CustomerBadge", typeof(RectTransform));
                UnityEngine.Object.DontDestroyOnLoad(_root);

                Canvas canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;

                // 复用游戏的缩放规则（1920×1080 参考分辨率），字才不会忽大忽小
                CustomUIManager.AddScreenScaler(_root);

                // 外层：整块填描边色，露出来的边缘就是 2px 的边框
                var badgeGo = new GameObject("Badge", typeof(RectTransform));
                badgeGo.transform.SetParent(_root.transform, false);

                _rect = (RectTransform)badgeGo.transform;
                _rect.anchorMin = new Vector2(0f, 0.5f);
                _rect.anchorMax = new Vector2(0f, 0.5f);
                _rect.pivot = new Vector2(0f, 0.5f);

                Image border = badgeGo.AddComponent<Image>();
                border.color = BorderColor;
                border.raycastTarget = false;

                // 内层：缩进 2px，把中间的描边色盖掉，剩下的就是牌面
                var plateGo = new GameObject("Plate", typeof(RectTransform));
                plateGo.transform.SetParent(badgeGo.transform, false);

                RectTransform plate = (RectTransform)plateGo.transform;
                plate.anchorMin = Vector2.zero;
                plate.anchorMax = Vector2.one;
                plate.offsetMin = new Vector2(BorderWidth, BorderWidth);
                plate.offsetMax = new Vector2(-BorderWidth, -BorderWidth);

                Image plateImage = plateGo.AddComponent<Image>();
                plateImage.color = PlateColor;
                plateImage.raycastTarget = false;

                // 文字：铺满牌面，自己留内边距
                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(plateGo.transform, false);

                RectTransform labelRect = (RectTransform)labelGo.transform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(PadX, PadY);
                labelRect.offsetMax = new Vector2(-PadX, -PadY);

                _text = labelGo.AddComponent<TextMeshProUGUI>();
                _text.font = ResolveFont();
                _text.fontSize = FontSize;
                _text.fontStyle = FontStyles.Bold;
                _text.alignment = TextAlignmentOptions.MidlineLeft;
                _text.enableWordWrapping = false;
                _text.overflowMode = TextOverflowModes.Overflow;
                _text.raycastTarget = false;

                _rect.sizeDelta = new Vector2(160f, FontSize + PadY * 2f + BorderWidth * 2f);

                _root.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(ModEntry.Tag + "顾客提示牌创建失败：" + e);
                _root = null;
                _rect = null;
                _text = null;
                return false;
            }
        }

        /// <summary>
        /// 找一个支持中文的字体。优先用提示框正在用的那个（和游戏原生文字一致），
        /// 其次在场景里随便找一个 TMP 文字借用，最后才退回 TMP 默认字体。
        /// </summary>
        internal static TMP_FontAsset ResolveFont()
        {
            TMP_FontAsset font = ItemTypeBadge.CurrentFont;
            if (font != null)
            {
                return font;
            }

            try
            {
                TMP_Text[] all = Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < all.Length; i++)
                {
                    TMP_Text candidate = all[i];
                    if (candidate == null || candidate.font == null)
                    {
                        continue;
                    }

                    // 只要场景里的对象，不要资源里的预制体
                    if (!candidate.gameObject.scene.IsValid())
                    {
                        continue;
                    }

                    return candidate.font;
                }
            }
            catch
            {
                // 忽略，走兜底
            }

            return TMP_Settings.defaultFontAsset;
        }

        // ---------------------------------------------------------------
        // 量宽高 + 摆位置
        // ---------------------------------------------------------------

        /// <summary>让牌子贴着文字：宽高 = 文字尺寸 + 内边距 + 描边。</summary>
        private static void Resize()
        {
            if (_rect == null || _text == null)
            {
                return;
            }

            float w = 160f;
            float h = FontSize + PadY * 2f + BorderWidth * 2f;

            try
            {
                // 用牌子自己的字号量，量出来的宽度才和画出来的文字一致
                Vector2 preferred = _text.GetPreferredValues(_current);
                w = Mathf.Ceil(preferred.x) + (PadX + BorderWidth) * 2f;
                h = Mathf.Ceil(preferred.y) + (PadY + BorderWidth) * 2f;
            }
            catch
            {
                // 量不出来就用兜底尺寸，不影响功能
            }

            _rect.sizeDelta = new Vector2(w, h);
        }

        private static void Reposition()
        {
            if (_rect == null)
            {
                return;
            }

            try
            {
                float areaW = LetterboxManager.GameAreaWidth;
                float areaH = LetterboxManager.GameAreaHeight;
                if (areaW <= 1f || areaH <= 1f)
                {
                    areaW = Screen.width;
                    areaH = Screen.height;
                }

                float x = LetterboxManager.GameAreaX + AnchorFractionX * areaW;
                float y = LetterboxManager.GameAreaY + AnchorFractionY * areaH;

                // ScreenSpaceOverlay 画布的世界坐标就是屏幕像素，直接给坐标即可
                _rect.position = new Vector3(x, y, 0f);
                _rect.SetAsLastSibling();
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "顾客提示牌定位失败：" + e.Message);
            }
        }
    }
}
