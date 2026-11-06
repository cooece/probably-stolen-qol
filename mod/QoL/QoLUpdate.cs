using System;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 每帧执行的驱动。
    ///
    /// 游戏的 ModHook 是个挂在 DontDestroyOnLoad 对象上的 MonoBehaviour，
    /// 但模组自己不能直接收到 Update，所以这里造一个常驻 GameObject。
    /// 需要轮询的功能（顾客对话、到店提示）都由它驱动，内部各自限流。
    /// </summary>
    internal sealed class QoLUpdate : MonoBehaviour
    {
        private static QoLUpdate _instance;

        internal static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            GameObject go = new GameObject("QoL_Update");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<QoLUpdate>();
            Debug.Log(ModEntry.Tag + "每帧驱动已启动");
        }

        internal static void Shutdown()
        {
            if (_instance == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(_instance.gameObject);
            _instance = null;
        }

        private void Update()
        {
            try
            {
                if (ModEntry.Settings == null)
                {
                    return;
                }

                // 顾客相关：悬停对话、到店提示（内部各自限流，不用每帧干活）
                float dt = Time.unscaledDeltaTime;
                CustomerDialog.Tick(dt);
                CustomerAlert.Tick(dt);

                // 全局对话加速：按住空格极速快进 + 可选普通对话自动跳过
                DialogueSpeedUp.Tick(dt);

                // 真伪鉴定：谈判台面变化时自动揭穿（内部 0.5 秒限流）
                Authenticity.Tick(dt);

                // 交易结果弹窗：金额立即弹、声望 1.5 秒后弹（内部延迟队列）
                TradeResultPopup.Tick(dt);

                // 估价客户：自动填完美报价 + 自动批量提交（全自动，无需按键）
                AppraisalAuto.Tick();

                // 自动丢弃单据：5 类纸张统一移入垃圾桶（内部 1 秒限流）
                AutoDiscardDocs.Tick(dt);

                // 需求高亮：标出顾客想要的物品（内部惰性更新，没变化不跑）
                DemandHighlight.Tick();

                // 道具快捷键：按键捕获 + 跨容器自动递补使用
                QuickToolHotkey.Tick(dt);

                // 便捷框选：长按左键从物品上起拖框选（内部有位移阈值，不动就不干活）
                QuickBoxSelect.Tick();

                // 框选统计：把数量叠在收银机屏幕上，最多列 3 种（内部 0.1 秒限流）
                SelectionCount.Tick();

                // 全自动换电：统一队列补换机器电池（内部 0.2 秒限流）
                AutoBatterySwap.Tick(dt);

                // F9 调试面板：汉化 + 自定义开关键（内部判空/限流）
                DebugPanelZh.Tick();

            }
            catch (Exception e)
            {
                QLog.Error("Update 出错", e);
            }
        }
    }
}
