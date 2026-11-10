using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 店内那块小显示器（收银台旁边显示「第 111 天（星期六）／支付须于明日支付」的那块屏幕）
    /// 底下追加一行今日的市场行情：哪一类在降价、哪一类在涨价。
    ///
    /// 数据来源完全是游戏自己的东西，没有另写一套推算：
    ///   事件系统 <c>StoreEventManager.GetActiveEvents()</c> 里每个进行中的事件都带一组
    ///   <c>NegociationData</c>（itemType + value + 原因），value 就是该类货品的价格修正，
    ///   正数是涨价、负数是降价。玩家看到的那些「日用品救济行动」「建设项目」「供水过剩」
    ///   全都记在这里，所以和实际结算价一定一致，不会自己算歪。
    ///
    /// 同一类货品如果被好几个事件同时影响，按游戏的做法（<c>CommissaryData.GetTotalTypeMarketPriceModifier</c>）
    /// 把 value 相加，合计为正才算涨价、为负才算降价，正好抵消就干脆不显示。
    ///
    /// 颜色按股市习惯：涨红跌绿（也确实和游戏自己的 UI 配色对得上）。
    /// 实现是给 <c>StoreCalendar.Update</c> 挂 postfix —— 那一帧原版刚把前两行写好，
    /// 我们接在后面补一行，原版逻辑一行都不用改。
    /// </summary>
    internal static class CalendarPriceLine
    {
        private const string HarmonyId = "local.qol.calendar_price";

        /// <summary>涨价（红）。取自游戏 UI 里的警示红。</summary>
        private const string UpColor = "#FA5151";

        /// <summary>降价（深绿）。原来的荧光绿 #91D300 在米黄屏上发飘看不清，换成深绿。</summary>
        private const string DownColor = "#29530A";

        /// <summary>一项与一项之间的分隔。用中文逗号比空格省地方，也比空格更像行情。</summary>
        private const string Separator = "，";

        /// <summary>
        /// 一行大概能放几个全角字位。那块屏幕的文字框实测 175×105、字号 20，
        /// 一行放 13 个左右，取 12 留一点余量，免得自己排完又被 TMP 二次折断。
        /// </summary>
        private const float LineCapacity = 12f;

        /// <summary>行情多久重算一次。事件不常变，没必要每帧去排序一遍事件列表。</summary>
        private const float RefreshInterval = 0.5f;

        private static Harmony _harmony;
        private static string _cachedLine = "";
        private static float _nextRefresh;
        private static int _cachedDay = -1;
        private static bool _loggedError;

        // 拼接结果缓存：这个钩子挂在 Update 上每帧都跑，
        // 原版每帧重写日期文字，我们就得每帧把行情接回去——
        // 但"底文 + 行情"的拼接结果只在内容变化时才重建，
        // 其余帧直接复用同一个字符串，避免每帧产生 GC 垃圾。
        private static string _lastBase;
        private static string _lastLine;
        private static string _combined;

        // 我们改过排版的那个文字框，以及它原本的排版设置：停用时还原，别把原版 UI 留在被改状态
        private static TMP_Text _touched;
        private static bool _origWordWrap;
        private static bool _origAutoSize;
        private static float _origFontMin;

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                var update = AccessTools.Method(typeof(StoreCalendar), "Update", Type.EmptyTypes);
                if (update == null)
                {
                    QLog.Warn("找不到 StoreCalendar.Update，日历行情不会显示");
                    _harmony = null;
                    return;
                }

                _harmony.Patch(update,
                    postfix: new HarmonyMethod(typeof(CalendarPriceLine), nameof(UpdatePostfix)));

                QLog.Info("日历行情已挂载");
            }
            catch (Exception e)
            {
                QLog.Error("挂载日历行情失败：" + e);
                _harmony = null;
            }
        }

        internal static void Remove()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll(HarmonyId);
                }
            }
            catch
            {
                // 卸载阶段出错无所谓
            }

            _harmony = null;

            // 还原被我们改过的原版文字框排版
            try
            {
                if (_touched != null)
                {
                    _touched.enableWordWrapping = _origWordWrap;
                    _touched.enableAutoSizing = _origAutoSize;
                    _touched.fontSizeMin = _origFontMin;
                }
            }
            catch
            {
                // 场景已卸载时文字框可能已经销毁，忽略
            }

            _touched = null;
            _cachedLine = "";
            _cachedDay = -1;
            _lastBase = null;
            _lastLine = null;
            _combined = null;
        }

        // ---------------------------------------------------------------
        // 挂载点
        // ---------------------------------------------------------------

        /// <summary>
        /// 原版 Update 这一帧刚写完「第 N 天（星期 X）\n到期提示」，
        /// 这里在它后面接一行行情。因为原版每帧都重写，所以我们每帧重写也不会累积。
        /// </summary>
        private static void UpdatePostfix(StoreCalendar __instance)
        {
            try
            {
                if (ModEntry.Settings == null || !ModEntry.Settings.ShowPriceTrend.Value)
                {
                    return;
                }

                if (__instance == null)
                {
                    return;
                }

                TextMeshProUGUI tmp = __instance.dayTMP;
                if (tmp == null)
                {
                    return;
                }

                // 第一次碰到这个文字框时记下原版排版，停用时还原
                if (_touched != tmp)
                {
                    _touched = tmp;
                    _origWordWrap = tmp.enableWordWrapping;
                    _origAutoSize = tmp.enableAutoSizing;
                    _origFontMin = tmp.fontSizeMin;
                }

                // 每帧强制：关掉自动换行，让 TMP 的自动缩放优先缩字而不是折行——
                // 否则「房贷：1055 元」会被从「房/贷」中间断开、「元」单独掉到下一行。
                // 这几个属性开销极小，且原版 Update 每帧重写 text 可能重置排版，
                // 所以不能只设一次，得每帧兜底。
                tmp.richText = true;
                if (tmp.enableWordWrapping)
                {
                    tmp.enableWordWrapping = false;
                }

                if (!tmp.enableAutoSizing)
                {
                    tmp.enableAutoSizing = true;
                }

                if (tmp.fontSizeMin > 8f)
                {
                    tmp.fontSizeMin = 8f;
                }

                string line = GetLine();

                // 原版这一帧写的底文（「第 N 天（星期 X）\n支付须于今日支付」）
                string baseText = tmp.text;

                // 上一帧我们接上去的内容还在（原版这帧没重写日期）就什么都不做
                if (baseText == _combined)
                {
                    return;
                }

                // 只有底文或行情变了才重新拼接；没变就复用上次的字符串
                if (baseText != _lastBase || line != _lastLine)
                {
                    _lastBase = baseText;
                    _lastLine = line;
                    _combined = Rebuild(baseText, line);
                }

                tmp.text = _combined;
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    QLog.Warn("日历行情出错：" + e);
                }
            }
        }

        /// <summary>
        /// 重拼整块文字：第一行保留原版日期；第二行换成「今天需支付房租：300 元」这种带金额的；
        /// 第三行接行情。底文里的原版「支付须于今日支付」被拆掉替换。
        /// </summary>
        private static string Rebuild(string baseText, string priceLine)
        {
            // 原版底文第一行是日期（「第 112 天（星期日）」），第二行是到期提示。
            int newline = baseText.IndexOf('\n');
            string dateLine = newline >= 0 ? baseText.Substring(0, newline) : baseText;

            string payLine = BuildPayLine();
            if (string.IsNullOrEmpty(payLine))
            {
                // 拿不到金额就退回原版底文（保留第二行）
                payLine = newline >= 0 ? baseText.Substring(newline + 1) : "";
            }

            var sb = new StringBuilder(128);
            sb.Append(dateLine);
            if (!string.IsNullOrEmpty(payLine))
            {
                sb.Append('\n').Append(payLine);
            }

            if (!string.IsNullOrEmpty(priceLine))
            {
                sb.Append('\n').Append(priceLine);
            }

            return sb.ToString();
        }

        /// <summary>第二行：今天/明天/N 日后 + 需支付房租(或房贷)：金额 元。</summary>
        private static string BuildPayLine()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null || store.IsPropertyPaid)
                {
                    return null;
                }

                int days = store.GetRentDayCounter();
                bool mortgage = store.isPayingMortgage;
                int amount = mortgage ? store.mortgageWeeklyPayment : store.GetRentValue();
                string kind = mortgage ? "房贷" : "房租";

                string when = days switch
                {
                    0 => "今天需支付",
                    1 => "明天需支付",
                    _ => days + " 日后需支付",
                };

                return when + kind + "：" + amount + " 元";
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // 算行情
        // ---------------------------------------------------------------

        private static string GetLine()
        {
            int day = -1;
            try
            {
                if (StoreStation.Instance != null)
                {
                    day = StoreStation.Instance.dayCounter;
                }
            }
            catch
            {
                // 读不到就当作都不知道，下面照常按时间刷新
            }

            float now = Time.unscaledTime;
            if (day == _cachedDay && now < _nextRefresh)
            {
                return _cachedLine;
            }

            _cachedDay = day;
            _nextRefresh = now + RefreshInterval;
            _cachedLine = BuildLine();
            return _cachedLine;
        }

        private static string BuildLine()
        {
            List<StoreEvent> events = null;

            try
            {
                StoreStation station = StoreStation.Instance;
                if (station == null || station.storeEventManager == null)
                {
                    return "";
                }

                events = station.storeEventManager.GetActiveEvents();
            }
            catch
            {
                return "";
            }

            if (events == null || events.Count == 0)
            {
                return "";
            }

            // 按首次出现的顺序记账，同类货品的修正加起来（和游戏结算用的是同一套口径）
            var order = new List<string>();
            var sums = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < events.Count; i++)
            {
                StoreEvent ev = events[i];
                if (ev == null || ev.negociationDatas == null)
                {
                    continue;
                }

                for (int j = 0; j < ev.negociationDatas.Count; j++)
                {
                    NegociationData data = ev.negociationDatas[j];
                    if (data == null || data.value == 0 || string.IsNullOrEmpty(data.itemType))
                    {
                        continue;
                    }

                    if (!sums.ContainsKey(data.itemType))
                    {
                        sums[data.itemType] = 0;
                        order.Add(data.itemType);
                    }

                    sums[data.itemType] += data.value;
                }
            }

            if (order.Count == 0)
            {
                return "";
            }

            // 先把每一项拼成「名字+带符号百分比」，同时量出它占几个全角字位。
            // 涨价和降价分开两组，后面各自成行：上面涨价、下面降价，
            // 避免某个百分比单独被挤到第二行。
            var upSegments = new List<string>();
            var upWidths = new List<float>();
            var downSegments = new List<string>();
            var downWidths = new List<float>();

            for (int i = 0; i < order.Count; i++)
            {
                string type = order[i];
                int value = sums[type];
                if (value == 0)
                {
                    // 涨的和降的正好抵消，没什么可说的
                    continue;
                }

                string name;
                try
                {
                    name = TypeHelper.GetTypeDisplayName(type);
                }
                catch
                {
                    name = null;
                }

                if (string.IsNullOrEmpty(name))
                {
                    name = type;
                }

                bool up = value > 0;
                int percent = Mathf.Abs(value);
                string number = (up ? "涨" : "降") + percent + "%";

                // 每项前面都带「今日」，涨跌用汉字而不是正负号，读起来更像一句话
                const string Today = "今日";
                string segment = "<color=" + (up ? UpColor : DownColor) + ">" + Today + name + number + "</color>";
                float width = MeasureWidth(Today) + MeasureWidth(name) + MeasureWidth(number);

                if (up)
                {
                    upSegments.Add(segment);
                    upWidths.Add(width);
                }
                else
                {
                    downSegments.Add(segment);
                    downWidths.Add(width);
                }
            }

            if (upSegments.Count == 0 && downSegments.Count == 0)
            {
                return "";
            }

            // 涨价一行、降价一行，组内放不下再自己换行（换行处不写逗号）。
            var sb = new StringBuilder(96);
            float separatorWidth = MeasureWidth(Separator);

            if (upSegments.Count > 0)
            {
                AppendGroup(sb, upSegments, upWidths, separatorWidth);
            }

            if (downSegments.Count > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                AppendGroup(sb, downSegments, downWidths, separatorWidth);
            }

            return sb.ToString();
        }

        /// <summary>把一组同向（全涨或全降）的行情段按宽度排进 sb，放不下就换行。</summary>
        private static void AppendGroup(StringBuilder sb, List<string> segments, List<float> widths, float separatorWidth)
        {
            float used = 0f;

            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0)
                {
                    if (used + separatorWidth + widths[i] > LineCapacity)
                    {
                        sb.Append('\n');
                        used = 0f;
                    }
                    else
                    {
                        sb.Append(Separator);
                        used += separatorWidth;
                    }
                }

                sb.Append(segments[i]);
                used += widths[i];
            }
        }

        /// <summary>
        /// 粗略量一段文字占几个全角字位：中日韩全角算 1，拉丁字母、数字、半角符号算 0.55。
        /// 只用来决定在哪换行，不需要精确到像素。
        /// </summary>
        private static float MeasureWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0f;
            }

            float width = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                width += text[i] >= 0x2E80 ? 1f : 0.55f;
            }

            return width;
        }
    }
}
