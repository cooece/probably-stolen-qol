using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QoL
{
    /// <summary>
    /// 框选统计：框中一批物品时浮出一块小面板，按名称列出每种物品
    /// 有几堆、共几个，最后给一行合计。交任务要凑数量时不用再自己数。
    ///
    /// 数据来源：游戏 ItemMultiSelectHandler 的私有字段 selectedItems，
    /// 用反射读（和便捷框选读 state 的做法一致）。GameItemElement 本身就是
    /// GameItem，直接读 GetDisplayName / unitCount，不用再解析 UI。
    ///
    /// 显示：复用 CustomerBadge 那套 ScreenSpaceOverlay + 游戏字体 + 缩放规则，
    /// 不引入 IMGUI，中文不会变方块；所有 Image/Text 都 raycastTarget=false，
    /// 面板不吃鼠标事件，不会挡住底下的操作。
    ///
    /// 交互：右上角 × 可关到下次框选。不做拖动——拖动要按住左键，而左键按下
    /// 会被游戏框选器当成一次新的起拖，直接把当前选中清掉，反而更容易出 BUG。
    /// 面板固定在游戏区域上方偏左，避开台面操作区。
    /// </summary>
    internal static class SelectionCount
    {
        private const float RefreshInterval = 0.1f;
        private const int SortingOrder = 960;
        private const float FontSize = 16f;
        private const float PadX = 10f;
        private const float PadY = 8f;
        private const float BorderWidth = 2f;
        private const float MinWidth = 200f;

        private static readonly Color BorderColor = new Color32(0x9B, 0x4A, 0x3D, 0xFF);
        private static readonly Color PlateColor = new Color32(0x29, 0x22, 0x34, 0xF0);

        private static readonly FieldInfo SelectedField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        private sealed class Kind
        {
            public string Name;
            public int Stacks;
            public int Units;
        }

        // 复用的临时容器，避免每次刷新都分配
        private static readonly List<Kind> Kinds = new List<Kind>();
        private static readonly Dictionary<string, int> Index = new Dictionary<string, int>();
        private static readonly StringBuilder Builder = new StringBuilder();
        private static readonly Vector3[] Corners = new Vector3[4];

        private static GameObject _root;
        private static RectTransform _panel;
        private static RectTransform _close;
        private static TMP_Text _body;
        private static Rect _closeRect;

        private static float _nextRefresh;
        private static bool _userClosed;
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

            // × 的点击每帧判一次，避免漏点击；面板没显示时不用算
            if (_root != null && _root.activeSelf && Input.GetMouseButtonDown(0))
            {
                Vector3 mouse = Input.mousePosition;
                if (_closeRect.Contains(new Vector2(mouse.x, mouse.y)))
                {
                    _userClosed = true;
                    Hide();
                    return;
                }
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
                // 选中清空 = 这次框选结束，下次重新框选时再显示
                _userClosed = false;
                Hide();
                return;
            }

            if (_userClosed)
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

                    Kind k = Kinds[at];
                    k.Stacks++;
                    k.Units += units;
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

            Builder.Length = 0;
            Builder.Append("<b>框选统计</b>\n");

            int total = 0;
            for (int i = 0; i < Kinds.Count; i++)
            {
                Kind k = Kinds[i];
                total += k.Units;
                Builder.Append(k.Name).Append(" ×").Append(k.Units);
                if (k.Stacks > 1 && k.Stacks != k.Units)
                {
                    Builder.Append("（").Append(k.Stacks).Append(" 堆）");
                }
                Builder.Append('\n');
            }

            Builder.Append("<color=#FFD479>共 ").Append(Kinds.Count)
                   .Append(" 种，").Append(total).Append(" 个</color>");
            return Builder.ToString();
        }

        // ---------------------------------------------------------------
        // 面板
        // ---------------------------------------------------------------

        private static void Show(string text)
        {
            if (!Ensure()) return;

            if (text != _lastText)
            {
                _lastText = text;
                _body.text = text;
                Resize();
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
            _panel = null;
            _close = null;
            _body = null;
            _lastText = "";
            _userClosed = false;
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

                Canvas canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
                CustomUIManager.AddScreenScaler(_root);

                // 外层做 2px 描边，内层缩进盖住中间，剩下一圈就是边框
                var panelGo = new GameObject("Panel", typeof(RectTransform));
                panelGo.transform.SetParent(_root.transform, false);
                _panel = (RectTransform)panelGo.transform;
                _panel.anchorMin = new Vector2(0f, 1f);
                _panel.anchorMax = new Vector2(0f, 1f);
                _panel.pivot = new Vector2(0f, 1f);

                Image border = panelGo.AddComponent<Image>();
                border.color = BorderColor;
                border.raycastTarget = false;

                var plateGo = new GameObject("Plate", typeof(RectTransform));
                plateGo.transform.SetParent(_panel, false);
                RectTransform plate = (RectTransform)plateGo.transform;
                plate.anchorMin = Vector2.zero;
                plate.anchorMax = Vector2.one;
                plate.offsetMin = new Vector2(BorderWidth, BorderWidth);
                plate.offsetMax = new Vector2(-BorderWidth, -BorderWidth);

                Image plateImage = plateGo.AddComponent<Image>();
                plateImage.color = PlateColor;
                plateImage.raycastTarget = false;

                var bodyGo = new GameObject("Body", typeof(RectTransform));
                bodyGo.transform.SetParent(plate, false);
                RectTransform bodyRect = (RectTransform)bodyGo.transform;
                bodyRect.anchorMin = Vector2.zero;
                bodyRect.anchorMax = Vector2.one;
                bodyRect.offsetMin = new Vector2(PadX, PadY);
                bodyRect.offsetMax = new Vector2(-PadX - 18f, -PadY); // 右侧给 × 留位

                _body = bodyGo.AddComponent<TextMeshProUGUI>();
                _body.font = CustomerBadge.ResolveFont();
                _body.fontSize = FontSize;
                _body.color = Color.white;
                _body.alignment = TextAlignmentOptions.TopLeft;
                _body.enableWordWrapping = false;
                _body.overflowMode = TextOverflowModes.Overflow;
                _body.raycastTarget = false;

                var closeGo = new GameObject("Close", typeof(RectTransform));
                closeGo.transform.SetParent(plate, false);
                _close = (RectTransform)closeGo.transform;
                _close.anchorMin = new Vector2(1f, 1f);
                _close.anchorMax = new Vector2(1f, 1f);
                _close.pivot = new Vector2(1f, 1f);
                _close.anchoredPosition = new Vector2(-4f, -2f);
                _close.sizeDelta = new Vector2(20f, 20f);

                TMP_Text closeText = closeGo.AddComponent<TextMeshProUGUI>();
                closeText.font = _body.font;
                closeText.fontSize = FontSize + 2f;
                closeText.text = "×";
                closeText.alignment = TextAlignmentOptions.Center;
                closeText.color = new Color32(0xFF, 0xB0, 0xA0, 0xFF);
                closeText.raycastTarget = false;

                _root.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                QLog.Warn("框选统计：面板创建失败 —— " + e.Message);
                Remove();
                return false;
            }
        }

        private static void Resize()
        {
            float w = MinWidth;
            float h = 56f;
            try
            {
                Vector2 preferred = _body.GetPreferredValues(_body.text);
                w = Mathf.Max(MinWidth, Mathf.Ceil(preferred.x) + (PadX + BorderWidth) * 2f + 18f);
                h = Mathf.Max(44f, Mathf.Ceil(preferred.y) + (PadY + BorderWidth) * 2f);
            }
            catch
            {
                // 量不出来就用兜底尺寸
            }

            _panel.sizeDelta = new Vector2(w, h);
        }

        private static void Reposition()
        {
            try
            {
                float areaW = LetterboxManager.GameAreaWidth;
                float areaH = LetterboxManager.GameAreaHeight;
                float x;
                float y;

                if (areaW <= 1f || areaH <= 1f)
                {
                    x = 0.31f * Screen.width;
                    y = 0.945f * Screen.height;
                }
                else
                {
                    x = LetterboxManager.GameAreaX + 0.31f * areaW;
                    y = LetterboxManager.GameAreaY + 0.945f * areaH;
                }

                // ScreenSpaceOverlay 下世界坐标就是屏幕像素
                _panel.position = new Vector3(x, y, 0f);
                _panel.SetAsLastSibling();

                // 拿 × 的四个世界角当点击区（Overlay 下即屏幕像素），放大一点好点中
                _close.GetWorldCorners(Corners);
                _closeRect = Rect.MinMaxRect(
                    Corners[0].x - 6f, Corners[0].y - 6f,
                    Corners[2].x + 6f, Corners[2].y + 6f);
            }
            catch (Exception e)
            {
                QLog.Warn("框选统计：定位失败 —— " + e.Message);
            }
        }
    }
}
