using System;
using System.Collections.Generic;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 每帧执行的驱动。
    ///
    /// 游戏的 ModHook 是个挂在 DontDestroyOnLoad 对象上的 MonoBehaviour，
    /// 但模组自己不能直接收到 Update，所以这里造一个常驻 GameObject。
    ///
    /// 两条硬规则（吃过亏）：
    ///   1. 读档期间（SaveManager.IsDecoding）整体停手，别在物品树重建时插一脚；
    ///   2. 每个功能单独 try/catch —— 以前用一个大 try 包住全部 Tick，
    ///      某个功能抛异常会让同帧后面的功能整段被跳过，症状极难定位。
    /// </summary>
    internal sealed class QoLUpdate : MonoBehaviour
    {
        private static QoLUpdate _instance;

        /// <summary>静态委托表：只初始化一次，每帧循环调用，不产生额外分配。</summary>
        private static readonly (string Name, Action<float> Run)[] Drivers =
        {
            ("顾客对话",       d => CustomerDialog.Tick(d)),
            ("到店提示",       d => CustomerAlert.Tick(d)),
            ("对话加速",       d => DialogueSpeedUp.Tick(d)),
            ("真伪鉴定",       d => Authenticity.Tick(d)),
            ("交易结果弹窗",   d => TradeResultPopup.Tick(d)),
            ("估价客户",       d => AppraisalAuto.Tick()),
            ("自动丢弃单据",   d => AutoDiscardDocs.Tick(d)),
            ("需求高亮",       d => DemandHighlight.Tick()),
            ("检查收纳",       d => ContrabandSweep.Tick(d)),
            ("道具快捷键",     d => QuickToolHotkey.Tick(d)),
            ("便捷框选",       d => QuickBoxSelect.Tick()),
            ("框选统计",       d => SelectionCount.Tick()),
            ("一键上柜台",     d => QuickShelf.Tick(d)),
            ("柜台溢出",       d => CounterOverflow.Tick(d)),
            ("前台杂物提醒",   d => CounterClutterWarn.Tick(d)),
            ("全自动换电",     d => AutoBatterySwap.Tick(d)),
            ("自动换滤芯",     d => AutoFilterSwap.Tick(d)),
            ("解序器按钮",     d => DesequencerHeaderButton.Tick(d)),
            ("解序器收卡",     d => DesequencerAutoFeed.Tick(d)),
            ("酿酒工坊",       d => Brewing.Tick(d)),
            ("汉化调试面板",   d => DebugPanelZh.Tick()),
        };

        /// <summary>同一个功能报错 30 秒内只记一次，避免每帧刷日志。</summary>
        private static readonly Dictionary<string, float> LastError = new Dictionary<string, float>();

        internal static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            GameObject go = new GameObject("QoL_Update");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<QoLUpdate>();
            QLog.Info("每帧驱动已启动");
        }

        internal static void Shutdown()
        {
            if (_instance == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(_instance.gameObject);
            _instance = null;
            LastError.Clear();
        }

        private void Update()
        {
            if (ModEntry.Settings == null) return;

            // 读档期间物品树正在按 BFS 索引重建，任何创建/移动/销毁都要停
            if (SaveManager.IsDecoding) return;

            float dt = Time.unscaledDeltaTime;

            for (int i = 0; i < Drivers.Length; i++)
            {
                var driver = Drivers[i];
                try
                {
                    driver.Run(dt);
                }
                catch (Exception e)
                {
                    ReportOnce(driver.Name, e);
                }
            }
        }

        private static void ReportOnce(string name, Exception e)
        {
            float now = Time.unscaledTime;
            if (LastError.TryGetValue(name, out float last) && now - last < 30f) return;

            LastError[name] = now;
            QLog.Warn("[" + name + "] 每帧执行出错：" + e.GetType().Name + " " + e.Message);
        }
    }
}
