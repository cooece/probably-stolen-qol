using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

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

        // 按键值缓存：CurrentKey 每帧被问一次，值不变就不重算
        private static string _keyCacheRaw;
        private static KeyCode _keyCacheKey;
        private static bool _keyCacheReady;

        // 捕获时遍历用，只分配一次
        private static KeyCode[] _keyList;

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
                MethodInfo addInput = AccessTools.Method(typeof(CustomUIBuilder), "AddInput",
                    new[] { typeof(string), typeof(string), typeof(Action<string>), typeof(string), typeof(Action<string>) });
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

        /// <summary>
        /// AddInput 执行完，__instance.Last 就是刚造好的那一行控件。
        /// 用 __args 取 id（第 4 个参数），不按参数名绑定，避免游戏改名后补丁静默失效。
        /// </summary>
        private static void AddInputPostfix(CustomUIBuilder __instance, object[] __args)
        {
            if (__args == null || __args.Length < 4) return;
            if ((__args[3] as string) != ShelfKeyId) return;

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

            // 旧配置里存的是 Mouse2 这种名字，显示成「鼠标中键」。
            // 每一步单独兜住：哪一步坏了都不会连累后面的，日志里能看出是哪一步。
            try
            {
                string shown = KeyDisplayName(ParseKey(input.text));
                if (shown != null && input.text != shown) input.SetTextWithoutNotify(shown);
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 刷新改键框文字失败：" + e);
            }

            // 用输入框自带的 onSelect：点一下就选中，等于「点了这一格」。
            // 之前试过在多出来的 Button 上监听，Unity 那边会抛空引用，而且点击命中的
            // 是整行背景图、不一定传得到子物体上的按钮 —— 这条路更短也更稳。
            try
            {
                input.onSelect.AddListener(delegate { BeginCapture(input); });
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 改键框接管失败，只能用右键物品录制：" + e);
                return;
            }

            // 放在最后：点击能用了才设只读，否则至少还能手动输入
            try { input.readOnly = true; }
            catch (Exception e) { QLog.Warn("[一键上柜] 设只读失败：" + e); }

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
            get
            {
                // 每帧都会问一次，值没变就不重复解析（省下 Trim + 字符串比较）
                string raw = RawValue;
                if (!_keyCacheReady || !string.Equals(raw, _keyCacheRaw, StringComparison.Ordinal))
                {
                    _keyCacheRaw = raw;
                    _keyCacheKey = ParseKey(raw);
                    _keyCacheReady = true;
                }
                return _keyCacheKey;
            }
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

        /// <summary>当前选的目标容器。默认前厅柜台（游戏里叫 FrontCounterGrid），可切到展示柜或后仓。</summary>
        private static GameGridInventory TargetInventory()
        {
            EmporiumEntry store = EmporiumEntry.Instance;
            if (store == null) return null;

            QoLSettings settings = ModEntry.Settings;
            string target = QoLSettings.ShelfTargetFront;
            if (settings != null && settings.ShelfTarget != null && !string.IsNullOrEmpty(settings.ShelfTarget.Value))
            {
                target = settings.ShelfTarget.Value;
            }

            if (target == QoLSettings.ShelfTargetShowcase) return store.showcaseElement;
            if (target == QoLSettings.ShelfTargetBack) return store.backInvinvElement;
            return store.frontInvinvElement;
        }

        private static void ShelfAll()
        {
            List<GameItem> targets = Collect();
            if (targets.Count == 0) return;

            GameGridInventory destination = null;
            try
            {
                destination = TargetInventory();
            }
            catch (Exception e)
            {
                QLog.Warn("[一键上柜] 取目标容器失败：" + e.Message);
                return;
            }

            if (destination == null) return;

            int moved = 0;
            int notOwned = 0;    // 没买下来的
            int noRoom = 0;      // 目标塞满了
            int failed = 0;      // 物品本身拿不动 / 抛异常

            for (int i = 0; i < targets.Count; i++)
            {
                GameItem item = targets[i];
                if (item == null) continue;

                try
                {
                    if (!GeneralHelper.IsItemOwned(item)) { notOwned++; continue; }   // 还没买下来的东西不能上柜
                    if (ReferenceEquals(item.parentInventory, destination)) continue;  // 已经在目标里，别白搬
                    if (!item.MayRemove()) { failed++; continue; }

                    int remain = GraphUtils.TryAcceptAll(destination, item);
                    if (remain <= 0) moved++;
                    else noRoom++;
                }
                catch (Exception e)
                {
                    failed++;
                    QLog.Warn("[一键上柜] 移动「" + SafeName(item) + "」失败：" + e.Message);
                }
            }

            QLog.DebugLog("[一键上柜] 上架 " + moved + " 件（选中 " + targets.Count +
                          "，未购买 " + notOwned + "，放不下 " + noRoom + "，拿不动 " + failed + "）");
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

            // 键盘键（KeyCode 列表只取一次，避免每次捕获都分配数组）
            if (_keyList == null) _keyList = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            for (int k = 0; k < _keyList.Length; k++)
            {
                KeyCode code = _keyList[k];
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
