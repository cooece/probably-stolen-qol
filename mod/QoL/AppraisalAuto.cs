using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace QoL
{
    /// <summary>
    /// 估价客户（鉴定）的自动鉴定，行为参考 DangpuPlus 的 AppraisalAutofill，实现全部自己写：
    ///
    /// 1. 自动填完美报价：打开估价物品的检查面板时，自动把「正确价值 × 105%」填进报价框
    ///    （105% 是游戏的"完美"档位上限，见 AppraisalServiceHelper.GetEstimatePrecision）。
    /// 2. 自动批量提交：估价客户放好物品、开场白一结束，就自动把台面上所有估价物品
    ///    依次按 105% 报价提交，全程不用按任何键。
    /// 不画任何新 UI，也不占快捷键；受「真伪鉴定」开关控制。
    /// 估价单等纸张的自动丢弃已独立到 AutoDiscardDocs（由「自动丢弃单据」开关控制）。
    /// </summary>
    internal static class AppraisalAuto
    {
        private static IntPtr _lastAutofilled;
        private static bool _submitting;
        private static readonly Queue<int> _submitQueue = new Queue<int>();
        private static readonly Dictionary<int, GameItem> _byId = new Dictionary<int, GameItem>();
        private static float _scanTimer;
        private static bool _loggedError;

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.Authenticity.Value;
            }
        }

        private static bool FastDialogue
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings == null || settings.DialogueFast.Value;
            }
        }

        internal static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                Autofill();
                AutoSubmit();
                PumpSubmit();
            }
            catch (Exception e)
            {
                // 只打一次堆栈，避免每帧刷屏
                if (!_loggedError)
                {
                    _loggedError = true;
                    Debug.LogWarning(ModEntry.Tag + "估价自动鉴定出错：" + e);
                }
            }
        }

        // ---------------------------------------------------------------
        // 1. 自动填完美报价
        // ---------------------------------------------------------------

        private static void Autofill()
        {
            InspectionUIManager ui = InspectionUIManager.Instance;
            if (ui == null || ui.appraisalPanel == null || !ui.appraisalPanel.activeInHierarchy)
            {
                _lastAutofilled = IntPtr.Zero;
                return;
            }

            GameItem item = ui.currentInspectingItem;
            TMP_InputField field = ui.apprisalServiceInputField;
            if (item == null || !item.IsTag("APPRAISAL_ITEM") || field == null || ui.isEditingAppraisalField)
            {
                return;
            }

            // 同一个物品只自动填一次，避免覆盖玩家手动改的报价
            IntPtr key = (IntPtr)item.uniqueId;
            if (key == _lastAutofilled)
            {
                return;
            }

            int correct = AppraisalHelper.GetCorrectBaseValue(item);
            if (correct <= 0)
            {
                return;
            }

            int quote = correct * 105 / 100;
            field.text = quote.ToString();
            ui.OnValueChanged(field.text);
            _lastAutofilled = key;
        }

        // ---------------------------------------------------------------
        // 2. 自动批量提交（无需按键）
        // ---------------------------------------------------------------

        /// <summary>
        /// 估价客户在场、台面出现估价物品、且当前没在提交时，自动开始批量提交。
        /// 不再需要按任何键——客户放好物品、开场白一结束就自己跑。
        /// </summary>
        private static void AutoSubmit()
        {
            if (_submitting || !AppraisalClientPresent())
            {
                _scanTimer = 0f;
                return;
            }

            // 还有对话在播（客户开场白）就先不抢，等它说完
            DialogUIManager dialog = DialogUIManager.Instance;
            if (dialog != null && dialog.currentDialog != null)
            {
                _scanTimer = 0f;
                return;
            }

            // 限流：不必每帧全量扫描台面，0.3 秒扫一次即可，避免持续卡顿。
            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer < 0.3f)
            {
                return;
            }
            _scanTimer = 0f;

            List<GameItem> items = EmporiumEntry.Instance != null
                ? EmporiumEntry.Instance.GetAllNonOwnedItem()
                : null;
            if (items == null)
            {
                return;
            }

            // 一次遍历同时完成「有没有可鉴定的」判断与入队，避免重复扫描整张台面
            _byId.Clear();
            _submitQueue.Clear();
            int queued = 0;

            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];
                if (item != null && item.IsTag("APPRAISAL_ITEM") &&
                    AppraisalHelper.GetCorrectBaseValue(item) > 0)
                {
                    _byId[item.uniqueId] = item;
                    _submitQueue.Enqueue(item.uniqueId);
                    queued++;
                }
            }

            if (queued > 0)
            {
                _submitting = true;
                Debug.Log(ModEntry.Tag + "自动开始批量估价：" + queued + " 件，按 105% 完美报价");
            }
        }

        private static void PumpSubmit()
        {
            if (!_submitting)
            {
                return;
            }

            // 客户走了就清空，安全收尾
            if (!AppraisalClientPresent())
            {
                _submitQueue.Clear();
                _submitting = false;
                return;
            }

            DialogUIManager dialog = DialogUIManager.Instance;
            bool dialogRunning = dialog != null && dialog.currentDialog != null;

            if (_submitQueue.Count == 0)
            {
                _submitting = false;
                return;
            }

            // 如果有对话在播：
            if (dialogRunning)
            {
                // 开启对话加速时：自动连按空格跳过中间的对话；
                // 当提交到最后一件（队列已清空）后不再触发，最后一句照常完整播放。
                if (FastDialogue)
                {
                    dialog.OnAdvancePressed();
                }
                return;
            }

            int id = _submitQueue.Peek();
            if (!_byId.TryGetValue(id, out GameItem item) || item == null ||
                !StillOnCounter(item))
            {
                _submitQueue.Dequeue();
                return;
            }

            // 检查面板还开着就先关掉，避免提交时状态冲突
            InspectionUIManager ui = InspectionUIManager.Instance;
            if (ui != null && ui.panel != null && ui.panel.activeSelf)
            {
                ui.CloseUI();
            }

            int correct = AppraisalHelper.GetCorrectBaseValue(item);
            AppraisalServiceHelper.OnEstimate(item, correct * 105 / 100);
            _submitQueue.Dequeue();
        }

        private static bool StillOnCounter(GameItem item)
        {
            // 「还在柜台上」等价于「仍非玩家所有」——物品一旦被拿下柜台就会被标记为拥有。
            // 无需再全量扫描台面，直接查归属即可，避免每次提交前的重扫。
            try
            {
                return item != null && !GeneralHelper.IsItemOwned(item);
            }
            catch
            {
                return false;
            }
        }

        private static bool AppraisalClientPresent()
        {
            if (EmporiumEntry.Instance == null)
            {
                return false;
            }

            PlayerStore store = PlayerStore.Instance;
            if (store == null || store.currentClientInstance == null)
            {
                return false;
            }

            StoreClient blueprint = store.currentClientInstance.GetClientBlueprint();
            return blueprint != null && blueprint.clientIntent == StoreClient.ClientIntent.APPRAISAL_SERVICE;
        }

    }
}
