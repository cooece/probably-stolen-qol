using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 交易结果弹窗：顾客买/卖成交后，先后弹两条提示——
    ///   1. 金额：「支付宝到账：+68元」/「支付宝扣款：-68元」
    ///   2. 约 1.5 秒后再弹声望：「上层区声望：+0.006」
    ///
    /// 行为参考工坊「声望增加显示 (Reputation Pop)」与「到账语音播报」，实现自己写。
    /// 两条提示分开弹，避免挤在同一个气泡里。
    ///
    /// 实现：Harmony 拦截 StoreReputation.OnItemTraded（买、卖结算声望的统一入口）——
    ///   - prefix 记录该阵营声望的旧值
    ///   - postfix 用第 3 个参数 originalValue 取实际成交金额（= playerCash 的增减），
    ///     累加进整单金额；第 2 个参数 value 是声望基数、会被阵营倍率改写，不能当钱用
    ///   - 「出售 N 物品」会逐件回调，Tick 等 0.15s 静默后弹一条合计，避免气泡互相覆盖
    ///   - 声望同样压进延迟队列，到点后弹
    ///   都用游戏自带的 ReputationUIManager.Notify(message) 气泡，不画新 UI。
    /// </summary>
    internal static class TradeResultPopup
    {
        private const string HarmonyId = "local.qol.trade_result";
        private const float RepDelaySeconds = 1.5f;

        /// <summary>整单结算的静默时间。「出售 N 物品」会逐件回调，等这一串结束再弹一条合计。</summary>
        private const float CashFlushDelay = 0.15f;

        private static Harmony _harmony;

        private static double _repBefore;
        private static bool _repCaptured;

        /// <summary>本单累计成交金额（正数=到账，负数=扣款）。</summary>
        private static long _pendingCash;
        private static float _cashFlushTimer;

        /// <summary>估价结算前的现金，用于算鉴定费收入。</summary>
        private static int _cashBeforeFee;

        /// <summary>通用声望修改前的旧值（ModReputation 静态入口用）。</summary>
        private static double _repBefore2;
        private static bool _repCaptured2;

        /// <summary>待弹出的声望文案（非空表示有排队中）。</summary>
        private static string _pendingRepMessage;
        private static float _pendingRepTimer;

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.TradeResultPopup.Value;
            }
        }

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            _harmony = new Harmony(HarmonyId);

            var method = typeof(StoreReputation).GetMethod("OnItemTraded",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                Debug.LogWarning(ModEntry.Tag + "交易结果弹窗：找不到 OnItemTraded");
                return;
            }

            // 金额必须取第 3 个参数 originalValue（= playerCash 实际增减的数额）。
            // 第 2 个参数 value 是声望基数，会被阵营/外交倍率改写，不是钱。
            // 参数名对不上时 Harmony 会静默传 0，这里提前发现，避免游戏更新后金额恒为 0。
            var tradeParams = method.GetParameters();
            if (tradeParams.Length < 3 || tradeParams[2].Name != "originalValue")
            {
                Debug.LogWarning(ModEntry.Tag + "交易结果弹窗：OnItemTraded 参数签名变了，金额可能不准，请检查");
            }

            _harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(TradeResultPopup), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(TradeResultPopup), nameof(Postfix)));

            // 估价客户鉴定结算：GiveAppraisalFee 直接 playerCash += 佣金，不走 OnItemTraded，
            // 所以单独拦截，鉴定完也弹"支付宝到账"。
            var feeMethod = typeof(AppraisalServiceHelper).GetMethod("GiveAppraisalFee",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (feeMethod != null)
            {
                _harmony.Patch(feeMethod,
                    prefix: new HarmonyMethod(typeof(TradeResultPopup), nameof(FeePrefix)),
                    postfix: new HarmonyMethod(typeof(TradeResultPopup), nameof(FeePostfix)));
            }

            // 其他所有阵营声望变化（治安部罚款、小纸条举报、任务奖励、革命军/黑市等）：
            // 都汇聚到 StoreReputation.ModReputation(string, int, bool)。用前后差值弹出带数字的声望。
            var repMethod = typeof(StoreReputation).GetMethod("ModReputation",
                new[] { typeof(string), typeof(int), typeof(bool) });
            if (repMethod != null)
            {
                _harmony.Patch(repMethod,
                    prefix: new HarmonyMethod(typeof(TradeResultPopup), nameof(RepPrefix)),
                    postfix: new HarmonyMethod(typeof(TradeResultPopup), nameof(RepPostfix)));
            }

            // 现金统一入口 ModCash：罚款、房租、任务奖励、彩票、售货机等所有扣款/收款。
            var cashMethod = typeof(PlayerStore).GetMethod("ModCash",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (cashMethod != null)
            {
                _harmony.Patch(cashMethod,
                    prefix: new HarmonyMethod(typeof(TradeResultPopup), nameof(CashPrefix)));
            }

            Debug.Log(ModEntry.Tag + "交易结果弹窗已挂载");
        }

        internal static void Remove()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
            }

            _pendingRepMessage = null;
            _pendingCash = 0;
            _cashFlushTimer = 0f;
        }

        /// <summary>每帧驱动延迟弹出的声望提示（由 QoLUpdate 调用）。</summary>
        internal static void Tick(float dt)
        {
            // 整单金额：最后一次逐件结算后再静默一小会儿，把这一串成交合成一条合计。
            if (_cashFlushTimer > 0f)
            {
                _cashFlushTimer -= dt;
                if (_cashFlushTimer <= 0f)
                {
                    long cash = _pendingCash;
                    _pendingCash = 0;
                    if (cash != 0)
                    {
                        ReputationUIManager cashUi = ReputationUIManager.Instance;
                        if (cashUi != null)
                        {
                            NotifyCash(cashUi, cash);
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(_pendingRepMessage))
            {
                return;
            }

            _pendingRepTimer -= dt;
            if (_pendingRepTimer > 0f)
            {
                return;
            }

            ReputationUIManager ui = ReputationUIManager.Instance;
            if (ui != null)
            {
                ui.Notify(_pendingRepMessage);
            }

            _pendingRepMessage = null;
        }

        private static void Prefix(string factionId)
        {
            _repCaptured = false;
            _repBefore = 0.0;

            if (!Enabled || string.IsNullOrEmpty(factionId))
            {
                return;
            }

            StoreReputation rep = StoreReputation.GetStoreReputation(factionId);
            if (rep != null)
            {
                _repBefore = rep.GetReputationExact();
                _repCaptured = true;
            }
        }

        private static void Postfix(string factionId, int originalValue, bool isPlayerSold)
        {
            if (!Enabled || !_repCaptured)
            {
                return;
            }

            try
            {
                StoreReputation rep = StoreReputation.GetStoreReputation(factionId);
                if (rep == null)
                {
                    return;
                }

                double repDelta = rep.GetReputationExact() - _repBefore;

                // originalValue 才是 playerCash 真正增减的数额（已含顾客预算截断）。
                // 千万别用第 2 个参数 value：那是声望基数，会被阵营倍率 / 外交加成改写，不是钱。
                long cash = isPlayerSold ? originalValue : -originalValue;

                // 1. 金额累计：SellMultiple 会逐件回调，等整单结束由 Tick 弹一条合计
                if (cash != 0)
                {
                    _pendingCash += cash;
                    _cashFlushTimer = CashFlushDelay;
                }

                if (Math.Abs(repDelta) < 0.000001 && cash == 0)
                {
                    return;
                }

                // 2. 声望压进延迟队列，1.5 秒后再弹（避免和金额气泡叠在一起）
                string repMessage = BuildRepMessage(factionId, repDelta, rep.GetReputationExact(), cash);
                if (!string.IsNullOrEmpty(repMessage))
                {
                    _pendingRepMessage = repMessage;
                    _pendingRepTimer = RepDelaySeconds;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "交易结果弹窗出错：" + e.Message);
            }
        }

        /// <summary>弹一条现金气泡。金额用 long，兼容逐件累加后的整单合计。</summary>
        private static void NotifyCash(ReputationUIManager ui, long cash)
        {
            ui.Notify(cash > 0
                ? "支付宝到账：+" + cash.ToString(CultureInfo.InvariantCulture) + "元"
                : "支付宝扣款：-" + Math.Abs(cash).ToString(CultureInfo.InvariantCulture) + "元");
        }

        private static string BuildRepMessage(string factionId, double repDelta, double currentRep, long cash)
        {
            string faction = StoreReputation.GetFactionNameByID(factionId);

            if (Math.Abs(repDelta) >= 0.000001)
            {
                string sign = repDelta > 0 ? "+" : "-";
                return faction + "声望：" + sign +
                       Math.Abs(repDelta).ToString("0.####", CultureInfo.InvariantCulture);
            }

            if (cash != 0 && currentRep >= 200.0)
            {
                // 声望已达上限，提示一下避免误解
                return faction + "声望已满";
            }

            return null;
        }

        // ---------------------------------------------------------------
        // 估价鉴定结算：单独弹"支付宝到账"
        // ---------------------------------------------------------------

        private static void FeePrefix()
        {
            _cashBeforeFee = PlayerStore.Instance != null ? PlayerStore.Instance.playerCash : 0;
        }

        private static void FeePostfix()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null)
                {
                    return;
                }

                int delta = store.playerCash - _cashBeforeFee;
                if (delta == 0)
                {
                    return;
                }

                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui == null)
                {
                    return;
                }

                NotifyCash(ui, delta);
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "估价结算弹窗出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 其他阵营声望变化（治安部/小纸条/任务/革命军/黑市等）
        // ---------------------------------------------------------------

        private static void RepPrefix(string factionId, int value)
        {
            _repCaptured2 = false;
            _repBefore2 = 0.0;

            if (!Enabled || string.IsNullOrEmpty(factionId) || value == 0)
            {
                return;
            }

            StoreReputation rep = StoreReputation.GetStoreReputation(factionId);
            if (rep != null)
            {
                _repBefore2 = rep.GetReputationExact();
                _repCaptured2 = true;
            }
        }

        private static void RepPostfix(string factionId)
        {
            if (!Enabled || !_repCaptured2)
            {
                return;
            }

            try
            {
                StoreReputation rep = StoreReputation.GetStoreReputation(factionId);
                if (rep == null)
                {
                    return;
                }

                double repDelta = rep.GetReputationExact() - _repBefore2;
                if (Math.Abs(repDelta) < 0.000001)
                {
                    return;
                }

                string faction = StoreReputation.GetFactionNameByID(factionId);
                string sign = repDelta > 0 ? "+" : "-";
                string message = faction + "声望：" + sign +
                                 Math.Abs(repDelta).ToString("0.####", CultureInfo.InvariantCulture);

                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null)
                {
                    ui.Notify(message);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "声望弹窗出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 现金统一入口 ModCash：罚款、房租、任务、彩票等
        // ---------------------------------------------------------------

        private static void CashPrefix(int value)
        {
            if (!Enabled || value == 0)
            {
                return;
            }

            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null)
                {
                    NotifyCash(ui, value);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "现金弹窗出错：" + e.Message);
            }
        }
    }
}
