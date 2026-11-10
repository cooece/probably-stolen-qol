using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QoL
{
    /// <summary>
    /// 悬停顾客时显示本次接待已经说过的对话。
    ///
    /// 做法（照 Item Details 的 CustomerHistory 行为重写）：
    ///   1. 挂 DialogUIManager.UpdateDialogHistory(string) 的 postfix，把每一句台词记下来；
    ///      "---" 表示清空（新话题/新一天）。
    ///   2. 每 0.2 秒检查一次鼠标是不是停在顾客身上；
    ///      是 → 用游戏自带的 CustomUIManager.ShowTooltip 弹一个小卡片；不是 → 收起。
    ///   3. 换顾客时清空记录。
    ///
    /// 只在"鼠标确实压在顾客模型上"时才显示：还要过一次 UI 射线检测，
    /// 这样打开背包/窗口挡住顾客时不会误弹。
    /// </summary>
    internal static class CustomerDialog
    {
        private const string HarmonyId = "local.qol.customer_dialog";

        /// <summary>最多记多少句，超出丢最早的。</summary>
        private const int KeepLines = 40;

        /// <summary>卡片里最多显示多少句（再多眼睛看不过来）。</summary>
        private const int ShowLines = 8;

        /// <summary>轮询间隔（秒）。</summary>
        private const float PollInterval = 0.2f;

        /// <summary>同一段文字最多多久重弹一次，避免每帧刷 ShowTooltip。</summary>
        private const float ReshowSeconds = 1f;

        private static Harmony _harmony;
        private static readonly List<string> Lines = new List<string>();

        /// <summary>台词开头的「<color=#RRGGBB>说话人：</color> 」，显示时去掉，只留内容。</summary>
        private static readonly Regex SpeakerPrefix =
            new Regex("^<color=#[0-9A-Fa-f]{6,8}>[^<]*[:：]</color> ?", RegexOptions.Compiled);

        private static StoreClientInstance _client;
        private static string _shown = "";
        private static float _reshowAt;
        private static float _timer;

        // 卡片缓存：台词没变就不重建整张卡（轮询 0.2 秒一次，
        // 台词只在顾客说话时才变，没必要每次拼 500 字符的字符串）
        private static int _linesVersion;
        private static int _cardVersion = -1;
        private static string _cardCache;

        // UI 射线检测复用：PointerEventData 和结果列表每 0.2 秒用一次，
        // 没必要每次都 new（虽然小，但能省就省）
        private static EventSystem _raycastSystem;
        private static PointerEventData _pointerData;
        private static readonly List<RaycastResult> RaycastHits = new List<RaycastResult>(8);

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                var update = AccessTools.Method(typeof(DialogUIManager), "UpdateDialogHistory",
                    new[] { typeof(string) });
                if (update == null)
                {
                    QLog.Warn("找不到 DialogUIManager.UpdateDialogHistory，顾客对话记录不会生效");
                }
                else
                {
                    _harmony.Patch(update,
                        postfix: new HarmonyMethod(typeof(CustomerDialog), nameof(AfterDialogLine)));
                    QLog.Info("顾客对话记录已挂载");
                }
            }
            catch (Exception e)
            {
                QLog.Error("顾客对话记录挂载失败：" + e);
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
                // 忽略
            }

            _harmony = null;
            Lines.Clear();
            _linesVersion++;
            _cardCache = null;
            _cardVersion = -1;
            _client = null;
            _shown = "";
            Hide();
        }

        // ---------------------------------------------------------------
        // 记录台词
        // ---------------------------------------------------------------

        private static void AfterDialogLine(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                if (text == "---")
                {
                    Lines.Clear();
                    _linesVersion++;
                    return;
                }

                QoLSettings settings = ModEntry.Settings;
                if (settings == null || !settings.CustomerHistory.Value)
                {
                    return;
                }

                // 只记说话的行（带颜色标签），系统提示行不记
                if (!text.StartsWith("<color=", StringComparison.Ordinal))
                {
                    return;
                }

                if (PlayerStore.Instance == null || PlayerStore.Instance.currentClientInstance == null)
                {
                    return;
                }

                Match m = SpeakerPrefix.Match(text);
                Lines.Add(m.Success ? text.Substring(m.Length) : text);
                _linesVersion++;

                if (Lines.Count > KeepLines)
                {
                    Lines.RemoveRange(0, Lines.Count - KeepLines);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("记录顾客对话出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 每帧驱动（由 QoLUpdate 调用）
        // ---------------------------------------------------------------

        internal static void Tick(float deltaTime)
        {
            _timer += deltaTime;
            if (_timer < PollInterval)
            {
                return;
            }

            _timer = 0f;

            try
            {
                QoLSettings settings = ModEntry.Settings;
                if (settings == null || !settings.CustomerHistory.Value)
                {
                    if (_shown.Length > 0)
                    {
                        Hide();
                    }

                    return;
                }

                StoreClientInstance current = null;
                try
                {
                    current = PlayerStore.Instance != null ? PlayerStore.Instance.currentClientInstance : null;
                }
                catch
                {
                    // 场景切换途中读不到，当作没有顾客
                }

                if (current != _client)
                {
                    _client = current;
                    Lines.Clear();
                    _linesVersion++;
                    Hide();
                }

                if (IsHoveringClient())
                {
                    Show();
                }
                else
                {
                    Hide();
                }
            }
            catch (Exception e)
            {
                QLog.Warn("顾客对话轮询出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 判断鼠标是否停在顾客身上
        // ---------------------------------------------------------------

        private static bool IsHoveringClient()
        {
            try
            {
                if (!Interactable.isHoveringInteractable)
                {
                    return false;
                }

                Interactable hovering = Interactable.currentHoveringInteractable;
                StoreUIManager ui = StoreUIManager.Instance;
                if (hovering == null || ui == null)
                {
                    return false;
                }

                StoreClientMono client = ui.clientGameObject;
                if (client == null || !client.gameObject.activeInHierarchy)
                {
                    return false;
                }

                Transform target = client.transform;
                if (!IsRelated(hovering.transform, target))
                {
                    return false;
                }

                return PointerOver(target);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRelated(Transform hit, Transform target)
        {
            if (hit == null || target == null)
            {
                return false;
            }

            return hit == target || hit.IsChildOf(target) || target.IsChildOf(hit);
        }

        /// <summary>鼠标位置真的有 UI 元素压在顾客身上吗（挡住的窗口会命中它自己）。</summary>
        private static bool PointerOver(Transform target)
        {
            EventSystem system = EventSystem.current;
            if (system == null)
            {
                return false;
            }

            try
            {
                // EventSystem 在场景切换后会换实例，换了就重建一次
                if (_pointerData == null || _raycastSystem != system)
                {
                    _raycastSystem = system;
                    _pointerData = new PointerEventData(system);
                }

                _pointerData.position = Input.mousePosition;
                RaycastHits.Clear();

                system.RaycastAll(_pointerData, RaycastHits);
                for (int i = 0; i < RaycastHits.Count; i++)
                {
                    GameObject go = RaycastHits[i].gameObject;
                    if (go != null && go.transform.IsChildOf(target))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 显示 / 收起
        // ---------------------------------------------------------------

        private static string BuildCard()
        {
            // 台词和顾客没变（版本号没动）就直接用上次拼好的卡
            if (_cardCache != null && _cardVersion == _linesVersion)
            {
                return _cardCache;
            }

            var sb = new StringBuilder(512);
            sb.Append("<color=#FFD166>").Append(ClientName()).Append("</color>");

            if (Lines.Count == 0)
            {
                sb.Append('\n').Append("<color=#8A8A8A>这次还没有说过话。</color>");
            }
            else
            {
                int shown = Math.Min(ShowLines, Lines.Count);
                int earlier = Lines.Count - shown;
                if (earlier > 0)
                {
                    sb.Append('\n').Append("<color=#8A8A8A>…").Append(earlier).Append(" 行更早</color>");
                }

                for (int i = Lines.Count - shown; i < Lines.Count; i++)
                {
                    sb.Append('\n').Append(Lines[i]);
                }
            }

            _cardVersion = _linesVersion;
            _cardCache = sb.ToString();
            return _cardCache;
        }

        private static string ClientName()
        {
            try
            {
                StoreClientInstance instance = PlayerStore.Instance != null
                    ? PlayerStore.Instance.currentClientInstance
                    : null;
                if (instance != null)
                {
                    StoreClient blueprint = instance.GetClientBlueprint();
                    if (blueprint != null && !string.IsNullOrEmpty(blueprint.displayName))
                    {
                        return blueprint.displayName;
                    }
                }
            }
            catch
            {
                // 忽略，用兜底名字
            }

            return "顾客";
        }

        private static void Show()
        {
            try
            {
                CustomUIManager manager = CustomUIManager.Instance;
                if (manager == null)
                {
                    return;
                }

                string text = BuildCard();
                float now = Time.unscaledTime;
                if (text == _shown && now < _reshowAt)
                {
                    return;
                }

                manager.ShowTooltip(text, null);
                _shown = text;
                _reshowAt = now + ReshowSeconds;
            }
            catch (Exception e)
            {
                QLog.Warn("显示顾客对话出错：" + e.Message);
            }
        }

        private static void Hide()
        {
            if (_shown.Length == 0)
            {
                return;
            }

            string previous = _shown;
            _shown = "";

            try
            {
                CustomUIManager manager = CustomUIManager.Instance;
                if (manager == null)
                {
                    return;
                }

                // 只有当提示框上还是我们的文字时才收起来，
                // 否则可能把玩家正在看的物品提示框一起关掉。
                GameObject root = GameObject.Find("custom_ui_tooltip");
                TMP_Text label = root != null ? root.GetComponentInChildren<TMP_Text>(true) : null;
                if (label == null || label.text == previous)
                {
                    manager.HideTooltip(null);
                }
            }
            catch
            {
                // 收起失败不影响功能
            }
        }
    }
}
