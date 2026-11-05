using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 便捷框选：满容器（没有空位）也能从物品上直接拉出框选框，不需要按任何修饰键。
    ///
    /// 原版限制（ItemMultiSelectHandler.OnEventPress）：
    ///   起点有物品 → RaycastElement != null → 直接 return，不进 PendingBand；
    ///   还必须 IsPointerOverDragSurface 为 true（容器空白背景）。
    ///   同一次左键按下会分发给 5 个 InputActionHandler，原版正是靠「起点是否空位」
    ///   来区分「框选一堆」和「拖走 / 双击 / 选中一个」。
    ///
    /// 本模组的区分信号是「按住不动一小会儿」：
    ///   - 在物品上按下左键 → 原版照常执行（state 仍是 Idle），我们只记一个待定；
    ///   - 立刻拖动 / 鼠标移动超过阈值 / 拖物品已启动 → 取消待定，完全走原版；
    ///   - 按住不动超过设定时长 → 把框选器强制置为 PendingBand 并填好起始容器，
    ///     同时让 InputActionManager 重置其它处理器；之后拉框、命中、整组拖动
    ///     全部由游戏原版状态机接管，我们不重复实现。
    ///
    /// 工坊模组 BatchSelect 用「按住空格 + 压制另外 4 个处理器」达到同样目的，
    /// 这里换成时间维度，代价是框选前要等约 0.18 秒，换来零按键、零冲突。
    ///
    /// 安全：
    ///   - 只在原版确实拒绝了框选（state == Idle）时接管，已有框选 / 整组拖动不插手；
    ///   - 起点必须能找到 GameInventory 与 PixelWindow，桌面等空白区域不触发；
    ///   - 图钉、商店 UI 悬停、正在拖 UI、正在拖物品、右键菜单等状态一律不介入；
    ///   - 所有反射字段只解析一次，缺失就自动禁用并记一条日志，绝不每帧抛异常。
    /// </summary>
    internal static class QuickBoxSelect
    {
        private const string HarmonyId = "local.qol.quick_box_select";
        private const int StateIdle = 0;
        private const int StatePendingBand = 1;

        /// <summary>鼠标位移超过这个像素数就认为玩家要拖物品，取消长按判定。</summary>
        private const float MoveTolerance = 7f;

        private static Harmony _harmony;
        private static bool _disabled;

        private sealed class Pending
        {
            public float Time;
            public Vector3 Pos;
        }

        private static Pending _pending;

        private static readonly FieldInfo StateField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "state");
        private static readonly FieldInfo PressPositionField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "pressPosition");
        private static readonly FieldInfo BandInventoryField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "bandInventory");
        private static readonly FieldInfo BandWindowField =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "bandWindow");

        private static bool Enabled
        {
            get
            {
                if (_disabled) return false;
                QoLSettings s = ModEntry.Settings;
                return s != null && s.BoxSelectLongPress.Value;
            }
        }

        private static float HoldSeconds
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                string raw = s != null && s.BoxSelectHoldMs != null ? s.BoxSelectHoldMs.Value : null;
                int ms;
                if (!int.TryParse(raw, out ms) || ms < 80 || ms > 1000) ms = 180;
                return ms / 1000f;
            }
        }

        // ---------------------------------------------------------------
        // 挂载 / 卸载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            try
            {
                if (_harmony != null) return;

                if (StateField == null || PressPositionField == null ||
                    BandInventoryField == null || BandWindowField == null)
                {
                    _disabled = true;
                    QLog.Warn("[便捷框选] 游戏框选器字段未找到，功能已禁用（可能是游戏版本更新）");
                    return;
                }

                MethodInfo press = AccessTools.Method(typeof(ItemMultiSelectHandler), "OnEventPress");
                if (press == null)
                {
                    _disabled = true;
                    QLog.Warn("[便捷框选] 找不到 ItemMultiSelectHandler.OnEventPress，功能已禁用");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(press, postfix: new HarmonyMethod(typeof(QuickBoxSelect), nameof(PressPostfix)));
                QLog.Info("[便捷框选] 长按框选已挂载");
            }
            catch (Exception e)
            {
                _disabled = true;
                QLog.Error("[便捷框选] 挂载失败，功能已禁用", e);
            }
        }

        internal static void Remove()
        {
            _pending = null;
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll(HarmonyId);
                    _harmony = null;
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[便捷框选] 卸载出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 按下时记录待定
        // ---------------------------------------------------------------

        private static void PressPostfix(ItemMultiSelectHandler __instance, ISet<KeyCode> keysPressed)
        {
            _pending = null;
            if (!Enabled || __instance == null) return;
            if (keysPressed == null || !keysPressed.Contains(KeyCode.Mouse0)) return;

            try
            {
                // 原版已经处理了（空位起拖 → PendingBand，已选中物品 → PendingGroupDrag 等）
                if (ReadState(__instance) != StateIdle) return;
                if (!SafeToActivate()) return;

                _pending = new Pending { Time = Time.unscaledTime, Pos = Input.mousePosition };
                QLog.DebugLog("[便捷框选] 记录长按起点");
            }
            catch (Exception e)
            {
                DisableOnce("按下处理出错", e);
            }
        }

        private static bool SafeToActivate()
        {
            if (ItemPinHelper.BlocksClicks) return false;

            StoreUIManager store = StoreUIManager.Instance;
            if (store != null && store.hoveringStoreUI) return false;
            if (UIDragHandler.isDraggingAnyUI) return false;

            ItemMouseDragHandler drag = ItemMouseDragHandler.current;
            if (drag != null && drag.IsDraggingItem) return false;

            return true;
        }

        // ---------------------------------------------------------------
        // 每帧检查长按
        // ---------------------------------------------------------------

        internal static void Tick()
        {
            if (_pending == null || !Enabled) return;

            try
            {
                if (!Input.GetMouseButton(0))
                {
                    _pending = null;
                    return;
                }

                Vector3 now = Input.mousePosition;
                // 阈值取原版 dragThreshold 的一部分：既要在原版启动拖动前判定，
                // 又要容忍手抖（下限 5px）。另有 IsDraggingItem 兜底。
                float tol = MoveTolerance;
                ItemMultiSelectHandler multi = ItemMultiSelectHandler.current;
                if (multi != null && multi.dragThreshold > 0f)
                {
                    tol = Mathf.Clamp(multi.dragThreshold * 0.6f, 5f, 10f);
                }

                if ((now - _pending.Pos).sqrMagnitude > tol * tol)
                {
                    _pending = null; // 玩家开始拖了，完全交回原版
                    return;
                }

                ItemMouseDragHandler drag = ItemMouseDragHandler.current;
                if (drag != null && drag.IsDraggingItem)
                {
                    _pending = null;
                    return;
                }

                if (Time.unscaledTime - _pending.Time < HoldSeconds) return;

                _pending = null;
                TryStartBand(now);
            }
            catch (Exception e)
            {
                _pending = null;
                DisableOnce("长按检测出错", e);
            }
        }

        private static void TryStartBand(Vector3 pos)
        {
            ItemMultiSelectHandler multi = ItemMultiSelectHandler.current;
            if (multi == null) return;
            if (ReadState(multi) != StateIdle) return;
            if (!SafeToActivate()) return;

            Vector2 screen = pos;
            GameInventory inv = RenderHandler.RaycastFindParentElement<GameInventory>(screen, null);
            if (inv == null) return; // 不在容器/机器内，别接管桌面空白
            PixelWindow window = RenderHandler.RaycastFindParentElement<PixelWindow>(screen, null);
            if (window == null) return;

            if (multi.HasSelection)
            {
                try { multi.Cancel(); }
                catch { /* 清不掉也不影响下面强制置状态 */ }
            }

            // state 是私有嵌套枚举，必须用 Enum.ToObject 造出同类型的值，直接塞 int 会抛异常
            StateField.SetValue(multi, Enum.ToObject(StateField.FieldType, StatePendingBand));
            PressPositionField.SetValue(multi, pos);
            BandInventoryField.SetValue(multi, inv);
            BandWindowField.SetValue(multi, window);

            // 让输入管理器重置其余处理器，避免松手/移动时又启动拖单件。
            // 之后 PendingBand → Banding → 拉框 → 命中全走游戏原版。
            InputActionManager manager = InputActionManager.current;
            if (manager != null) manager.ResetAfter(multi);

            QLog.Info("[便捷框选] 长按触发框选（容器 " + inv.GetType().Name + "）");
        }

        private static int ReadState(ItemMultiSelectHandler multi)
        {
            try { return Convert.ToInt32(StateField.GetValue(multi)); }
            catch { return -1; }
        }

        private static void DisableOnce(string reason, Exception e)
        {
            _pending = null;
            if (_disabled) return;
            _disabled = true;
            QLog.Error("[便捷框选] " + reason + "，功能已禁用（本次启动内不再尝试）", e);
        }
    }
}
