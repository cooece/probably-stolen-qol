using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 批量改标签：框选多个同类物品后，快速按两下标签机的快捷键，
    /// 打开游戏原版的「标签打印机」面板；面板里改任意标签，会实时同步到其余选中物品。
    ///
    /// 为什么不用自建窗口：原版面板的下拉选项、分类名、价值提示、重复按钮都是现成的，
    /// 直接复用既不会漏掉某种标签类型，也和玩家平时的操作手感完全一致。
    ///
    /// 原理：
    ///   1. 从 ItemMultiSelectHandler.selectedItems（私有字段）读框选列表；
    ///   2. 取第一个「玩家所有 + 有可编辑标签」的物品作为样板，调用 LabelerUIManager.OpenUI 打开原版面板；
    ///   3. 记录样板物品各标签分类的 fakeCondition.identifier 作为基准；
    ///   4. 轮询中一旦发现某个分类的标签变了，就把原版 OnDropdownValueChanged 的三步写入
    ///      （fakeCondition / SetExposed(false) / customIntValue1）复制到其它同类物品。
    ///      这样批量写入和原版单件改标签的效果逐字节一致。
    /// </summary>
    internal static class BatchLabeler
    {
        /// <summary>原版标签机的物品 identifier（与快捷键分组名一致）。</summary>
        internal const string LabelerGroup = "labeler";

        /// <summary>
        /// 判断某个快捷键分组是不是标签机。
        /// 游戏里除了 labeler，还有 labeler_blue 等变体（玩家同样会给它们绑快捷键），
        /// 所以按前缀放行，避免「蓝色标签机双击没反应」。
        /// </summary>
        internal static bool IsLabelerGroup(string group)
        {
            if (string.IsNullOrEmpty(group)) return false;
            return string.Equals(group, LabelerGroup, StringComparison.Ordinal) ||
                   group.StartsWith(LabelerGroup + "_", StringComparison.Ordinal);
        }

        private const float PollInterval = 0.08f;

        private static readonly FieldInfo F_selectedItems =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        private static readonly List<GameItem> _targets = new List<GameItem>();
        private static readonly Dictionary<string, string> _baseline =
            new Dictionary<string, string>();

        private static GameItem _source;
        private static float _pollTimer;
        private static bool _batchActive;

        internal static bool IsActive => _batchActive;

        private static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.BatchLabelEnabled.Value;
            }
        }

        // ---------------------------------------------------------------
        // 框选读取与判定
        // ---------------------------------------------------------------

        /// <summary>当前框选里是否有至少 2 个同类、玩家所有、可改标签的物品。</summary>
        internal static bool HasBatchSelection()
        {
            if (!Enabled) return false;
            return BuildTargets().Count >= 2;
        }

        /// <summary>读取框选，返回与样板物品同 identifier 且可改标签的物品（含样板自身）。</summary>
        private static List<GameItem> BuildTargets()
        {
            var result = new List<GameItem>();
            try
            {
                ItemMultiSelectHandler ms = ItemMultiSelectHandler.current;
                if (ms == null || !ms.HasSelection || F_selectedItems == null) return result;

                object raw = F_selectedItems.GetValue(ms);
                IEnumerable en = raw as IEnumerable;
                if (en == null) return result;

                var owned = new List<GameItem>();
                foreach (object o in en)
                {
                    GameItem it = o as GameItem;
                    if (it == null) continue;
                    if (!GeneralHelper.IsItemOwned(it)) continue;
                    if (!HasEditableLabel(it)) continue;
                    owned.Add(it);
                }
                if (owned.Count < 2) return result;

                // 以第一个物品所属类别为准，只批量这一类，避免跨类别误改
                string id = owned[0].identifier;
                for (int i = 0; i < owned.Count; i++)
                {
                    if (owned[i].identifier == id) result.Add(owned[i]);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[批量标签] 读取框选失败：" + e.Message);
                result.Clear();
            }
            return result;
        }

        private static bool HasEditableLabel(GameItem item)
        {
            try
            {
                List<ItemFeature> feats = item.itemFeatures;
                if (feats == null) return false;
                for (int i = 0; i < feats.Count; i++)
                {
                    if (feats[i] != null && LabelerHelper.IsEditableLabelFeature(feats[i]))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static ItemFeature FindFeature(GameItem item, string category)
        {
            try
            {
                List<ItemFeature> feats = item.itemFeatures;
                if (feats == null) return null;
                for (int i = 0; i < feats.Count; i++)
                {
                    ItemFeature f = feats[i];
                    if (f != null && f.category == category) return f;
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------
        // 打开原版面板 / 结束批量模式
        // ---------------------------------------------------------------

        /// <summary>打开原版标签面板并进入批量模式。失败返回 false，由调用方退回普通单击。</summary>
        internal static bool TryOpen()
        {
            Reset();

            List<GameItem> all = BuildTargets();
            if (all.Count < 2)
            {
                QLog.DebugLog("[批量标签] 框选不足 2 件同类可改标签物品，不进入批量模式");
                return false;
            }

            LabelerUIManager ui = LabelerUIManager.Instance;
            if (ui == null || ui.panel == null)
            {
                QLog.Warn("[批量标签] 当前场景没有标签打印机面板");
                return false;
            }

            try
            {
                _source = all[0];
                for (int i = 1; i < all.Count; i++) _targets.Add(all[i]);

                // 打开游戏原版面板（会按 source 的可编辑标签重建下拉项）
                ui.OpenUI(_source);

                // 基准在 OpenUI 之后再采，避免面板初始化时下拉框回调造成误判
                CaptureBaseline();

                _batchActive = true;
                _pollTimer = 0f;

                string name = SafeDisplayName(_source);
                QLog.Info("[批量标签] 已打开原版标签面板：" + name + "，共 " + all.Count + " 件同类物品");
                Notify("批量标签：已选中 " + all.Count + " 件" + name + "，改标签将同步到全部");
                return true;
            }
            catch (Exception e)
            {
                QLog.Error("[批量标签] 打开标签面板失败", e);
                Reset();
                return false;
            }
        }

        /// <summary>记录样板物品各标签分类当前的标签值。</summary>
        private static void CaptureBaseline()
        {
            _baseline.Clear();
            if (_source == null) return;
            try
            {
                List<ItemFeature> feats = _source.itemFeatures;
                if (feats == null) return;
                for (int i = 0; i < feats.Count; i++)
                {
                    ItemFeature f = feats[i];
                    if (f == null || !LabelerHelper.IsEditableLabelFeature(f)) continue;
                    _baseline[f.category] = f.fakeCondition != null
                        ? (f.fakeCondition.identifier ?? "")
                        : "";
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[批量标签] 记录标签基准失败：" + e.Message);
            }
        }

        /// <summary>每帧轮询：面板关了就收工，标签变了就广播。</summary>
        internal static void Tick(float dt)
        {
            if (!_batchActive) return;

            LabelerUIManager ui = LabelerUIManager.Instance;
            if (ui == null || ui.panel == null || !ui.panel.activeSelf)
            {
                QLog.DebugLog("[批量标签] 面板已关闭，批量模式结束");
                Reset();
                return;
            }

            _pollTimer -= dt;
            if (_pollTimer > 0f) return;
            _pollTimer = PollInterval;

            if (_source == null)
            {
                Reset();
                return;
            }

            try
            {
                List<ItemFeature> feats = _source.itemFeatures;
                if (feats == null) return;

                for (int i = 0; i < feats.Count; i++)
                {
                    ItemFeature f = feats[i];
                    if (f == null || !LabelerHelper.IsEditableLabelFeature(f)) continue;

                    string category = f.category;
                    string current = f.fakeCondition != null
                        ? (f.fakeCondition.identifier ?? "")
                        : "";

                    if (!_baseline.TryGetValue(category, out string previous)) continue;
                    if (string.Equals(previous, current, StringComparison.Ordinal)) continue;

                    _baseline[category] = current;
                    Broadcast(category, f, current);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[批量标签] 轮询同步出错：" + e.Message);
            }
        }

        /// <summary>把样板物品的新标签复制到其余同类物品，写入方式与原版完全一致。</summary>
        private static void Broadcast(string category, ItemFeature src, string newId)
        {
            int changed = 0;
            for (int i = 0; i < _targets.Count; i++)
            {
                GameItem target = _targets[i];
                if (target == null || target == _source) continue;

                ItemFeature f = FindFeature(target, category);
                if (f == null) continue;

                try
                {
                    // 对应原版 LabelerElement.OnDropdownValueChanged 的三步：
                    //   fakeCondition = conditionLookup[category][dropdown.value]
                    //   SetExposed(false)
                    //   WaterFeatureHelper.OnWaterLabelUpdated(feature, dropdown.value) → 写 customIntValue1
                    f.fakeCondition = src.fakeCondition;
                    f.SetExposed(false);
                    f.customIntValue1 = src.customIntValue1;
                    changed++;
                }
                catch (Exception e)
                {
                    QLog.Warn("[批量标签] 单件同步失败：" + e.Message);
                }
            }

            // 不额外播音 / 不重复检查成就：原版 LabelerElement.OnDropdownValueChanged
            // 在改样板物品时已经做过一次，这里再播会变成双响。
            QLog.Info("[批量标签] " + category + " → " + newId + "，已同步 " + changed + " 件");
            Notify("已批量修改 " + (changed + 1) + " 件物品的标签");
        }

        internal static void Reset()
        {
            _batchActive = false;
            _source = null;
            _targets.Clear();
            _baseline.Clear();
            _pollTimer = 0f;
        }

        private static string SafeDisplayName(GameItem item)
        {
            try
            {
                if (item == null) return "物品";
                string dn = item.GetDisplayName();
                if (!string.IsNullOrEmpty(dn)) return dn;
                if (!string.IsNullOrEmpty(item.name)) return item.name;
            }
            catch { }
            return "物品";
        }

        private static void Notify(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(text);
            }
            catch { }
        }
    }
}
