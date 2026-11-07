using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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

        /// <summary>设置项在配置面板里的键名，用来认出「就是我们这一行」。</summary>
        private const string ShelfKeyId = "shelf.key";

        private const string HarmonyId = "local.qol.quick_shelf";

        private static readonly FieldInfo F_selectedItems =
            AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        private static Harmony _harmony;

        private static bool _capturing;
        private static int _captureFrame = -1;
        private static TMP_InputField _captureField;
        private static bool _captureFromPanel;
        private static bool _panelHooked;

        internal static bool IsCapturing => _capturing;

        // ---------------------------------------------------------------
        // 挂载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            ItemContextMenu.RegisterProvider(ProvideContextMenu);
            PatchSettingsInput();
            QLog.Info("[一键上柜] 已挂载，当前按键 " + KeyDisplayName(CurrentKey));
        }

        internal static void Remove()
        {
            ItemContextMenu.UnregisterProvider(ProvideContextMenu);
            _capturing = false;
            _captureField = null;
            _captureFromPanel = false;

            if (_harmony != null)
            {
                try { _harmony.UnpatchAll(HarmonyId); }
                catch (Exception e) { QLog.Warn("[一键上柜] 卸下设置面板补丁失败：" + e.Message); }
                _harmony = null;
            }
        }

        /// <summary>
        /// 让设置面板里这一行的输入框变成「点一下就等你按键」。
        ///
        /// 游戏的模组设置只支持勾选/数字/文本，没有录制按键的控件，所以这里挂到
        /// CustomUIBuilder.AddInput 上：等它把这一行造好，再拿 CustomUIElement.input
        /// 把输入框设成只读，并盖一个按钮监听 —— 点一下就进捕获状态。
        /// </summary>
        private static void PatchSettingsInput()
        {
            try
            {
                MethodInfo addInput = AccessTools.Method(typeof(CustomUIBuilder), "AddInput");
                if (addInput == null)
                {
                    QLog.Warn("[一键上柜] 找不到 CustomUIBuilder.AddInput，设置面板里只能手动填按键名");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(addInput, postfix: new HarmonyMethod(typeof(QuickShelf), nameof(AddInputPostfix)));
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 挂设置面板补丁失败：" + e.Message);
            }
        }

        /// <summary>AddInput 执行完，__instance.Last 就是刚造好的那一行控件。</summary>
        private static void AddInputPostfix(CustomUIBuilder __instance, string id)
        {
            if (id != ShelfKeyId) return;

            try
            {
                CustomUIElement element = __instance != null ? __instance.Last : null;
                if (element == null || element.input == null) return;

                AttachCapture(element.input);
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 改造改键框失败：" + e.Message);
            }
        }

        private static void AttachCapture(TMP_InputField input)
        {
            if (input == null) return;

            // 只读：点击不再弹输入法，但仍然能被点中
            input.readOnly = true;

            // 旧配置里存的是 Mouse2 这种名字，显示成「鼠标中键」
            string shown = KeyDisplayName(ParseKey(input.text));
            if (input.text != shown) input.SetTextWithoutNotify(shown);

            Button button = input.GetComponent<Button>();
            if (button == null)
            {
                button = input.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
            }
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(delegate { BeginCapture(input); });

            if (!_panelHooked)
            {
                _panelHooked = true;
                QLog.Info("[一键上柜] 设置面板的改键框已接管：点一下就等按键");
            }
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
            get { return ParseKey(RawValue); }
        }

        /// <summary>
        /// 把配置里的值认成按键。三种写法都收：
        /// 中文名（鼠标中键 / 侧键1 / 关闭）、KeyCode 名（Mouse2 / N）、以及旧的数字。
        /// 认不出来就退回默认中键，不让功能变哑巴。
        /// </summary>
        internal static KeyCode ParseKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return KeyCode.None;

            raw = raw.Trim();
            if (raw.Length == 0) return KeyCode.None;
            if (raw == Disabled || raw == "无" || raw == "None") return KeyCode.None;

            switch (raw)
            {
                case "鼠标左键": case "左键": return KeyCode.Mouse0;
                case "鼠标右键": case "右键": return KeyCode.Mouse1;
                case "鼠标中键": case "中键": case "滚轮": return KeyCode.Mouse2;
                case "鼠标侧键1": case "侧键1": case "侧键4": return KeyCode.Mouse3;
                case "鼠标侧键2": case "侧键2": case "侧键5": return KeyCode.Mouse4;
                case "鼠标键5": return KeyCode.Mouse5;
                case "鼠标键6": return KeyCode.Mouse6;
            }

            KeyCode code;
            if (Enum.TryParse(raw, true, out code) && code != KeyCode.None) return code;

            int number;
            if (int.TryParse(raw, out number) && Enum.IsDefined(typeof(KeyCode), number)) return (KeyCode)number;

            return KeyCode.Mouse2;
        }

        /// <summary>按键在界面上显示的名字。鼠标键写成中文，键盘键直接用 KeyCode 名。</summary>
        internal static string KeyDisplayName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.None: return Disabled;
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                case KeyCode.Mouse3: return "鼠标侧键1";
                case KeyCode.Mouse4: return "鼠标侧键2";
                case KeyCode.Mouse5: return "鼠标键5";
                case KeyCode.Mouse6: return "鼠标键6";
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
                    // 设置面板被关掉时，输入框会先被销毁；这时候别继续等按键
                    if (_captureFromPanel && _captureField == null)
                    {
                        _capturing = false;
                        return;
                    }
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
            if (item == null) return;

            // 就算当前是「关闭」也留着这一项，点一下录制就能重新启用
            entries.Add(new ItemMenuEntry
            {
                Key = "quick_shelf_bind",
                ElementName = "QolQuickShelfBind",
                Text = "设上柜键(" + KeyDisplayName(CurrentKey) + ")",
                OnClick = delegate { BeginCapture(null); }
            });
        }

        private static void BeginCapture(TMP_InputField field)
        {
            _capturing = true;
            _captureField = field;
            _captureFromPanel = field != null;
            _captureFrame = Time.frameCount;
            Notify("请按下要用的按键（右键或 Esc 取消）");
        }

        private static void PollCapture()
        {
            if (Time.frameCount <= _captureFrame + 1) return; // 跳过触发捕获的那一下点击

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                CancelCapture();
                Notify("已取消");
                return;
            }

            // Backspace / Delete = 停用，对应设置里显示「关闭」
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                Bind(KeyCode.None);
                return;
            }

            if (!Input.anyKeyDown) return;

            // 鼠标键：左/中/侧键都能绑，右键留给「取消」
            for (int i = 0; i <= 6; i++)
            {
                if (i == 1) continue;
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

        private static void CancelCapture()
        {
            _capturing = false;
            _captureField = null;
            _captureFromPanel = false;
            ClearSelection();
        }

        private static void Bind(KeyCode code)
        {
            _capturing = false;
            _captureFromPanel = false;

            string name = KeyDisplayName(code);

            QoLSettings s = ModEntry.Settings;
            if (s == null || s.ShelfKey == null)
            {
                _captureField = null;
                return;
            }

            s.ShelfKey.Set(name);

            // 同步回设置面板那一格，否则点「应用」会把这格里的旧文字又写回去
            if (_captureField != null)
            {
                _captureField.SetTextWithoutNotify(name);
                _captureField = null;
            }

            ClearSelection();
            Notify(code == KeyCode.None ? "已关闭一键上柜台" : "上柜键已设为 " + name);
            QLog.Info("[一键上柜] 按键改为 " + name);
        }

        /// <summary>把焦点从输入框移开，避免面板里继续吃键盘。</summary>
        private static void ClearSelection()
        {
            try
            {
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            }
            catch
            {
                // 没有 EventSystem 就算了
            }
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
