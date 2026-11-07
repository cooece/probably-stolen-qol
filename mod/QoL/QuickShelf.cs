using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 一键上柜台：鼠标停在一件物品上，或者框选一批物品，按下绑定键就把它们摆到展示柜。
    ///
    /// 为什么不用自己搬运：游戏原版把东西放上柜台走的就是
    /// <c>GraphUtils.TryAcceptAll(EmporiumEntry.Instance.showcaseElement, item)</c>，
    /// 容量、是否已购买、能不能放进玩家容器的规则全在这个函数里，
    /// 放完还会触发 onSlotAddItemLateFunc，教程进度和顾客刷新都照常。
    ///
    /// 放不下的部分会原样留在原来的容器里（TryAcceptAll 只搬得动的部分），
    /// 所以「塞满为止、剩下不动」是自然结果，不需要额外处理。
    ///
    /// 选中集合直接读游戏自己的多选器 <c>ItemMultiSelectHandler.selectedItems</c>，
    /// 不另存一份，避免和我们自己的框选状态不同步。
    /// </summary>
    internal static class QuickShelf
    {
        /// <summary>设置里填这个值表示停用。</summary>
        internal const string Disabled = "关闭";

        private static readonly FieldInfo F_selectedItems =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        private static bool _capturing;
        private static int _captureFrame = -1;

        internal static bool IsCapturing => _capturing;

        // ---------------------------------------------------------------
        // 挂载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            ItemContextMenu.RegisterProvider(ProvideContextMenu);
            QLog.Info("[一键上柜] 已挂载，当前按键 " + KeyDisplayName(CurrentKey));
        }

        internal static void Remove()
        {
            ItemContextMenu.UnregisterProvider(ProvideContextMenu);
            _capturing = false;
        }

        // ---------------------------------------------------------------
        // 设置读取
        // ---------------------------------------------------------------

        private static string RawValue
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                if (s == null || s.ShelfKey == null) return null;
                return s.ShelfKey.Value;
            }
        }

        internal static KeyCode CurrentKey
        {
            get
            {
                string raw = RawValue;
                if (string.IsNullOrEmpty(raw)) return KeyCode.None;

                raw = raw.Trim();
                if (raw.Length == 0 || raw == Disabled || raw == "无" || raw == "None") return KeyCode.None;

                KeyCode code;
                if (Enum.TryParse(raw, true, out code) && code != KeyCode.None) return code;
                return KeyCode.Mouse2; // 值写坏了就退回默认，不让功能失效
            }
        }

        private static bool Enabled => CurrentKey != KeyCode.None;

        private static string KeyDisplayName(KeyCode key)
        {
            if (key == KeyCode.None) return Disabled;
            switch (key)
            {
                case KeyCode.Mouse0: return "左键";
                case KeyCode.Mouse1: return "右键";
                case KeyCode.Mouse2: return "中键";
                case KeyCode.Mouse3: return "侧键4";
                case KeyCode.Mouse4: return "侧键5";
                default: return key.ToString();
            }
        }

        // ---------------------------------------------------------------
        // 每帧
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            try
            {
                if (_capturing)
                {
                    PollCapture();
                    return;
                }

                KeyCode key = CurrentKey;
                if (key == KeyCode.None) return;
                if (!KeyDown(key)) return;
                if (Blocked()) return;

                ShelfAll();
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 执行失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>按下判定：鼠标键要走 GetMouseButtonDown，KeyCode 那套对鼠标不生效。</summary>
        private static bool KeyDown(KeyCode key)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6)
            {
                return Input.GetMouseButtonDown(key - KeyCode.Mouse0);
            }
            return Input.GetKeyDown(key);
        }

        /// <summary>会抢走按键的时机：拖东西、开窗拖拽、别的功能正在等键。</summary>
        private static bool Blocked()
        {
            if (QuickToolHotkey.IsCapturing) return true;

            ItemMouseDragHandler drag = ItemMouseDragHandler.current;
            if (drag != null && drag.IsDraggingItem) return true;

            if (UIDragHandler.isDraggingAnyUI) return true;

            if (EmporiumEntry.Instance == null) return true;

            try
            {
                // PlayerStore 在读档阶段会抛空引用，碰它一律兜住
                if (PlayerStore.Instance == null) return true;
                if (PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE) return true;
            }
            catch
            {
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------
        // 真正搬东西
        // ---------------------------------------------------------------

        private static void ShelfAll()
        {
            List<GameItem> targets = Collect();
            if (targets.Count == 0) return;

            GameGridInventory showcase = null;
            try
            {
                showcase = EmporiumEntry.Instance.showcaseElement;
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 取展示柜失败：" + e.Message);
                return;
            }

            if (showcase == null) return;

            int moved = 0;
            int skipped = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                GameItem item = targets[i];
                if (item == null) continue;

                try
                {
                    if (!GeneralHelper.IsItemOwned(item)) { skipped++; continue; }   // 还没买下来的东西不能上柜
                    if (ReferenceEquals(item.parentInventory, showcase)) continue;     // 已经在柜台上，别白搬
                    if (!item.MayRemove()) { skipped++; continue; }

                    int remain = GraphUtils.TryAcceptAll(showcase, item);
                    if (remain <= 0) moved++;
                    else skipped++;
                }
                catch (Exception e)
                {
                    skipped++;
                    QLog.Warn("[一键上柜] 移动「" + SafeName(item) + "」失败：" + e.Message);
                }
            }

            QLog.DebugLog("[一键上柜] 上架 " + moved + " 件，未放入 " + skipped + " 件（选中 " + targets.Count + "）");
        }

        /// <summary>优先用框选集合；没有框选就用鼠标指着的那一件。</summary>
        private static List<GameItem> Collect()
        {
            var list = new List<GameItem>();

            List<GameItem> selected = SelectedItems();
            if (selected.Count > 0) return selected;

            GameItem hovered = RenderHandler.RaycastElement<GameItem>(Input.mousePosition);
            if (hovered != null) list.Add(hovered);

            return list;
        }

        private static List<GameItem> SelectedItems()
        {
            var list = new List<GameItem>();

            ItemMultiSelectHandler handler = ItemMultiSelectHandler.current;
            if (handler == null || F_selectedItems == null) return list;

            try
            {
                IList raw = F_selectedItems.GetValue(handler) as IList;
                if (raw == null || raw.Count == 0) return list;

                for (int i = 0; i < raw.Count; i++)
                {
                    GameItem item = raw[i] as GameItem;   // GameItemElement 本身就是 GameItem
                    if (item != null) list.Add(item);
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 读取框选集合失败：" + e.Message);
            }

            return list;
        }

        private static string SafeName(GameItem item)
        {
            try { return item != null ? item.GetDisplayName() : "?"; }
            catch { return "?"; }
        }

        // ---------------------------------------------------------------
        // 右键菜单：设定按键
        // ---------------------------------------------------------------

        private static void ProvideContextMenu(GameItem item, List<ItemMenuEntry> entries)
        {
            if (!Enabled || item == null) return;

            entries.Add(new ItemMenuEntry
            {
                Key = "quick_shelf_bind",
                ElementName = "QolQuickShelfBind",
                Text = "设上柜键(" + KeyDisplayName(CurrentKey) + ")",
                OnClick = delegate { BeginCapture(); }
            });
        }

        private static void BeginCapture()
        {
            _capturing = true;
            _captureFrame = Time.frameCount;
            Notify("请按下要用的按键（Esc 取消）");
        }

        private static void PollCapture()
        {
            if (Time.frameCount <= _captureFrame + 1) return; // 跳过触发这次捕获的那一下

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _capturing = false;
                Notify("已取消");
                return;
            }

            // 鼠标键
            for (int i = 0; i <= 4; i++)
            {
                if (i == 1) continue; // 右键是菜单本身的键，不收
                if (Input.GetMouseButtonDown(i))
                {
                    Bind((KeyCode)((int)KeyCode.Mouse0 + i));
                    return;
                }
            }

            // 键盘键
            foreach (KeyCode code in Enum.GetValues(typeof(KeyCode)))
            {
                if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6) continue;
                if (code == KeyCode.Escape || code == KeyCode.None) continue;
                if (Input.GetKeyDown(code))
                {
                    Bind(code);
                    return;
                }
            }
        }

        private static void Bind(KeyCode code)
        {
            _capturing = false;

            QoLSettings s = ModEntry.Settings;
            if (s == null || s.ShelfKey == null) return;

            s.ShelfKey.Set(code.ToString());
            Notify("上柜键已设为 " + KeyDisplayName(code));
            QLog.Info("[一键上柜] 按键改为 " + code);
        }

        private static void Notify(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(text);
            }
            catch
            {
                // 气泡只是提示，失败不影响功能
            }
        }
    }
}
