using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 框选统计：框中一批物品时，把数量直接叠在右侧收银机的小屏幕上。
    ///
    /// 数据来源：游戏 ItemMultiSelectHandler 的私有字段 selectedItems，
    /// 用反射读（和便捷框选读 state 的做法一致）。GameItemElement 本身就是
    /// GameItem，直接读 GetDisplayName / unitCount，不用再解析 UI。
    ///
    /// 显示：只有一层文字，没有底板、边框和按钮，看着就像收银机自己打出来的。
    /// 位置按游戏区域比例固定在收银机屏幕左上角，换分辨率不会跑偏；
    /// 文字 raycastTarget=false，不吃鼠标事件，不会挡住底下的操作。
    ///
    /// 收银机屏幕只能放下几行：最多列出数量最多的 3 种，多出来的用一行
    /// 「还有 N 种」带过，合计仍然统计整批物品。
    /// </summary>
    internal static class SelectionCount
    {
        private const float RefreshInterval = 0.1f;
        private const int SortingOrder = 960;
        private const float FontSize = 26f;
        private const int MaxKinds = 3;
        private const int MaxNameChars = 9;

        // 文字区域用逻辑像素（游戏 CanvasScaler 以 1920×1080 为基准）
        private const float PanelWidth = 340f;
        private const float PanelHeight = 240f;

        // 收银机浅蓝屏幕的左上角，在 1920×1080 游戏区域里量出来的比例
        private const float AnchorFractionX = 0.755f;
        private const float AnchorFractionY = 0.505f;

        private static readonly FieldInfo SelectedField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        private sealed class Kind
        {
            public string Name;
            public int Units;
        }

        // 复用的临时容器，避免每次刷新都分配
        private static readonly List<Kind> Kinds = new List<Kind>();
        private static readonly Dictionary<string, int> Index = new Dictionary<string, int>();
        private static readonly StringBuilder Builder = new StringBuilder();

        private static GameObject _root;
        private static RectTransform _rect;
        private static TMP_Text _text;

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
            Builder.Append("<color=#FFE9A8><size=30><b>商品统计</b></size></color>\n");

            // 屏幕小，只列数量最多的前 3 种；名字太长截一下，免得冲出屏幕
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
            _rect = null;
            _text = null;
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

                Canvas canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
                CustomUIManager.AddScreenScaler(_root);

                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(_root.transform, false);
                _rect = (RectTransform)textGo.transform;
                _rect.anchorMin = new Vector2(0f, 1f);
                _rect.anchorMax = new Vector2(0f, 1f);
                _rect.pivot = new Vector2(0f, 1f);
                _rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

                _text = textGo.AddComponent<TextMeshProUGUI>();
                _text.font = CustomerBadge.ResolveFont();
                _text.fontSize = FontSize;
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
                    x = AnchorFractionX * Screen.width;
                    y = AnchorFractionY * Screen.height;
                }
                else
                {
                    x = LetterboxManager.GameAreaX + AnchorFractionX * areaW;
                    y = LetterboxManager.GameAreaY + AnchorFractionY * areaH;
                }

                // ScreenSpaceOverlay 下世界坐标就是屏幕像素
                _rect.position = new Vector3(x, y, 0f);
            }
            catch (Exception e)
            {
                QLog.Warn("框选统计：定位失败 —— " + e.Message);
            }
        }
    }
}
