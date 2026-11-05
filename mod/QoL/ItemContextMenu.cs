using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 右键菜单项定义。
    /// </summary>
    internal sealed class ItemMenuEntry
    {
        public string Key;
        public string ElementName;
        public string Text;
        public bool Enabled = true;
        public Action<GameItem> OnClick;
    }

    /// <summary>
    /// 物品右键菜单扩展框架：
    /// 安全拦截原版 ItemContextHandler，给物品右键弹出菜单动态追加自定义操作按钮。
    /// </summary>
    internal static class ItemContextMenu
    {
        private const string HarmonyId = "local.qol.item_context_menu";
        private static Harmony _harmony;

        internal delegate void MenuProvider(GameItem item, List<ItemMenuEntry> entries);
        private static readonly List<MenuProvider> _providers = new List<MenuProvider>();

        // 反射缓存私有字段
        private static readonly FieldInfo F_contextWindow = AccessTools.Field(typeof(ItemContextHandler), "contextWindow");
        private static readonly FieldInfo F_currentItem = AccessTools.Field(typeof(ItemContextHandler), "currentItem");
        private static readonly FieldInfo F_childElements = AccessTools.Field(typeof(GridPixelElement), "childElements");
        private static readonly FieldInfo F_textColor = AccessTools.Field(typeof(ItemContextHandler), "textColor");
        private static readonly FieldInfo F_textDisabledColor = AccessTools.Field(typeof(ItemContextHandler), "textDisabledColor");
        private static readonly FieldInfo F_textSelectColor = AccessTools.Field(typeof(ItemContextHandler), "textSelectColor");
        private static readonly FieldInfo F_textSelectDisabledColor = AccessTools.Field(typeof(ItemContextHandler), "textSelectDisabledColor");
        private static readonly MethodInfo M_OnEventReset = AccessTools.Method(typeof(ItemContextHandler), "OnEventReset");

        private sealed class ExtraButton
        {
            public string Key;
            public RichTextElement Element;
            public ItemMenuEntry Entry;
        }

        private sealed class MenuState
        {
            public PixelWindow Window;
            public GridPixelElement OriginalGrid;
            public RichTextElement[] OriginalButtons;
            public Vector3 GridScale;
            public Vector3 ButtonScale;
            public readonly Dictionary<int, GridPixelElement> CustomGrids = new Dictionary<int, GridPixelElement>();
            public readonly Dictionary<string, ExtraButton> ButtonPool = new Dictionary<string, ExtraButton>();
            public List<ExtraButton> ActiveButtons = new List<ExtraButton>();
            public string CurrentLayout = "";
        }

        private static MenuState _menuState;
        private static float _lastPressTime;
        private static Vector3 _lastPressMouse;

        internal static void RegisterProvider(MenuProvider provider)
        {
            if (provider != null && !_providers.Contains(provider))
            {
                _providers.Add(provider);
            }
        }

        internal static void UnregisterProvider(MenuProvider provider)
        {
            if (provider != null)
            {
                _providers.Remove(provider);
            }
        }

        internal static void Apply()
        {
            if (_harmony != null) return;

            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(
                    AccessTools.Method(typeof(ItemContextHandler), "EqualizeButtonWidths"),
                    prefix: new HarmonyMethod(typeof(ItemContextMenu), nameof(EqualizeButtonWidthsPrefix)),
                    postfix: new HarmonyMethod(typeof(ItemContextMenu), nameof(EqualizeButtonWidthsPostfix)));

                _harmony.Patch(
                    AccessTools.Method(typeof(ItemContextHandler), "UpdateSelectButtonColor"),
                    postfix: new HarmonyMethod(typeof(ItemContextMenu), nameof(UpdateSelectButtonColorPostfix)));

                _harmony.Patch(
                    AccessTools.Method(typeof(ItemContextHandler), "ResetButtonColor"),
                    postfix: new HarmonyMethod(typeof(ItemContextMenu), nameof(ResetButtonColorPostfix)));

                _harmony.Patch(
                    AccessTools.Method(typeof(ItemContextHandler), "TriggerButton"),
                    prefix: new HarmonyMethod(typeof(ItemContextMenu), nameof(TriggerButtonPrefix)));

                _harmony.Patch(
                    AccessTools.Method(typeof(ItemContextHandler), "FindContextMenuNode"),
                    postfix: new HarmonyMethod(typeof(ItemContextMenu), nameof(FindContextMenuNodePostfix)));

                _harmony.Patch(
                    AccessTools.Method(typeof(ItemContextHandler), "OnEventReset"),
                    postfix: new HarmonyMethod(typeof(ItemContextMenu), nameof(OnEventResetPostfix)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ModEntry.Tag + "安装右键菜单扩展失败：" + ex.Message);
            }
        }

        internal static void Remove()
        {
            if (_harmony == null) return;
            try
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
                _menuState = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ModEntry.Tag + "卸载右键菜单扩展失败：" + ex.Message);
            }
        }

        private static MenuState GetOrCreateState(ItemContextHandler handler)
        {
            PixelWindow win = F_contextWindow.GetValue(handler) as PixelWindow;
            if (win == null)
            {
                LogOnce("contextWindow 为 null");
                return null;
            }

            // 每次都重新读取当前 grid：其他模组（Collect All Water 等）会往同一个
            // contextWindow 上挂新的 grid，旧引用会失效，所以不能缓存 grid 与按钮列表。
            PixelElement child = win.childElement;
            GridPixelElement grid = child as GridPixelElement;
            if (grid == null)
            {
                LogOnce("contextWindow.childElement 不是 GridPixelElement（类型=" + (child != null ? child.GetType().Name : "null") + "）");
                return null;
            }
            if (grid.gridWidth != 1)
            {
                LogOnce("右键菜单网格宽度不是 1（实际=" + grid.gridWidth + "）");
                return null;
            }

            RichTextElement[] existing = ReadGridButtons(grid);
            if (existing == null || existing.Length == 0)
            {
                LogOnce("右键菜单网格里没有读取到任何按钮");
                return null;
            }

            // 复用 state（保留 ButtonPool 里的自定义按钮元素），但刷新 grid 与按钮快照。
            if (_menuState == null || _menuState.Window != win)
            {
                _menuState = new MenuState
                {
                    Window = win
                };
            }

            _menuState.OriginalGrid = grid;
            _menuState.OriginalButtons = existing;
            _menuState.GridScale = grid.handler.transform.localScale;
            _menuState.ButtonScale = existing[0].handler.transform.localScale;

            return _menuState;
        }

        /// <summary>读取 GridPixelElement 当前挂载的所有 RichTextElement 按钮。</summary>
        private static RichTextElement[] ReadGridButtons(GridPixelElement grid)
        {
            try
            {
                PixelElement[] children = F_childElements.GetValue(grid) as PixelElement[];
                if (children == null) return null;

                List<RichTextElement> buttons = new List<RichTextElement>();
                for (int i = 0; i < children.Length; i++)
                {
                    RichTextElement btn = children[i] as RichTextElement;
                    if (btn != null)
                    {
                        buttons.Add(btn);
                    }
                }
                return buttons.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();
        private static void LogOnce(string msg)
        {
            if (_loggedOnce.Add(msg))
            {
                QLog.Warn("[右键菜单] " + msg);
            }
        }

        /// <summary>正常早退（右键了本来就不能绑快捷键的物品）只记调试日志，不报警告。</summary>
        private static void LogOnceDebug(string msg)
        {
            if (QLog.IsVerbose && _loggedOnce.Add(msg))
            {
                QLog.DebugLog("[右键菜单] " + msg);
            }
        }

        private static ExtraButton GetOrCreateButton(MenuState state, ItemMenuEntry entry)
        {
            if (!state.ButtonPool.TryGetValue(entry.Key, out ExtraButton extra))
            {
                RichTextElement element = new RichTextElement();
                element.handler.gameObject.name = entry.ElementName ?? ("QoL_Menu_" + entry.Key);
                extra = new ExtraButton
                {
                    Key = entry.Key,
                    Element = element
                };
                state.ButtonPool[entry.Key] = extra;
            }
            extra.Entry = entry;
            return extra;
        }

        private static void EqualizeButtonWidthsPrefix(ItemContextHandler __instance)
        {
            try
            {
                _lastPressTime = Time.unscaledTime;
                _lastPressMouse = Input.mousePosition;

                // GetOrCreateState 每次都会重新读取当前 grid 与按钮快照，
                // 兼容其他模组（Collect All Water 等）追加的按钮。
                MenuState state = GetOrCreateState(__instance);
                if (state == null) return;

                GameItem item = F_currentItem.GetValue(__instance) as GameItem;
                List<ItemMenuEntry> entries = new List<ItemMenuEntry>();
                for (int i = 0; i < _providers.Count; i++)
                {
                    try
                    {
                        _providers[i](item, entries);
                    }
                    catch (Exception pex)
                    {
                        Debug.LogWarning(ModEntry.Tag + "右键菜单 provider 异常：" + pex.Message);
                    }
                }

                List<ExtraButton> extras = new List<ExtraButton>();
                for (int i = 0; i < entries.Count; i++)
                {
                    extras.Add(GetOrCreateButton(state, entries[i]));
                }
                state.ActiveButtons = extras;

                if (extras.Count == 0 && _menuState != null)
                {
                    // 菜单项为空：说明 provider 判断未通过（物品不是可绑定类型）
                    LogOnceDebug("右键物品未能匹配可绑定条件（item=" + (item != null ? item.identifier ?? item.name : "null") + "），未添加快捷键选项");
                }

                PaintButtons(state, __instance, null);
                ApplyLayout(state);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ModEntry.Tag + "准备右键菜单项异常：" + ex.Message);
            }
        }

        private static void EqualizeButtonWidthsPostfix(ItemContextHandler __instance)
        {
            try
            {
                if (_menuState == null || _menuState.ActiveButtons.Count == 0) return;
                List<RichTextElement> allButtons = _menuState.OriginalButtons
                    .Concat(_menuState.ActiveButtons.Select(x => x.Element))
                    .ToList();

                foreach (RichTextElement b in allButtons)
                {
                    b.SetFixedSize(-1, -1);
                }

                int maxWidth = allButtons.Max(b => b.widthPixels);
                foreach (RichTextElement b in allButtons)
                {
                    b.SetFixedSize(maxWidth, -1);
                }

                GridPixelElement curGrid = GetGridForCount(_menuState, _menuState.ActiveButtons.Count);
                curGrid?.Validate();
            }
            catch { }
        }

        private static void UpdateSelectButtonColorPostfix(ItemContextHandler __instance, RichTextElement selectedButton)
        {
            if (_menuState != null && _menuState.ActiveButtons.Count > 0)
            {
                PaintButtons(_menuState, __instance, selectedButton);
            }
        }

        private static void ResetButtonColorPostfix(ItemContextHandler __instance)
        {
            if (_menuState != null && _menuState.ActiveButtons.Count > 0)
            {
                PaintButtons(_menuState, __instance, null);
            }
        }

        private static bool TriggerButtonPrefix(ItemContextHandler __instance, RichTextElement button)
        {
            if (button == null || _menuState == null || _menuState.ActiveButtons.Count == 0)
            {
                return true;
            }

            ExtraButton matched = _menuState.ActiveButtons.FirstOrDefault(x => x.Element == button);
            if (matched == null)
            {
                return true;
            }

            if (!matched.Entry.Enabled)
            {
                return false;
            }

            // 防误点保护（极短时间内的同位置抬起）
            if (Time.unscaledTime - _lastPressTime < 0.15f &&
                (Input.mousePosition - _lastPressMouse).sqrMagnitude < 9f)
            {
                return false;
            }

            GameItem currentItem = F_currentItem.GetValue(__instance) as GameItem;
            Action<GameItem> clickAction = matched.Entry.OnClick;

            // 关闭当前菜单并执行目标动作
            M_OnEventReset?.Invoke(__instance, null);
            clickAction?.Invoke(currentItem);
            return false;
        }

        private static void FindContextMenuNodePostfix(ItemContextHandler __instance, ref GameObject __result)
        {
            if (__result != null || _menuState == null || _menuState.ActiveButtons.Count == 0)
            {
                return;
            }

            Vector3 mouse = Input.mousePosition;
            if (IsInsideNode(_menuState.Window.handler, mouse) ||
                _menuState.ActiveButtons.Any(x => IsInsideNode(x.Element.handler, mouse)))
            {
                __result = _menuState.Window.handler.gameObject;
            }
        }

        private static void OnEventResetPostfix(ItemContextHandler __instance)
        {
            if (_menuState == null || string.IsNullOrEmpty(_menuState.CurrentLayout))
            {
                return;
            }

            try
            {
                _menuState.ActiveButtons.Clear();
                ApplyLayout(_menuState);
            }
            catch { }
        }

        private static bool IsInsideNode(TreeNodeRender node, Vector3 mouse)
        {
            if (node != null && node.gameObject.activeInHierarchy)
            {
                return RectTransformUtility.RectangleContainsScreenPoint(node.rectTransform, mouse, null);
            }
            return false;
        }

        private static void ApplyLayout(MenuState state)
        {
            // 布局指纹 = 当前 grid 引用 + 我的按钮 key 列表。
            // 其他模组会替换 contextWindow 的 grid，所以必须把 grid 引用也算进去，
            // 否则 grid 被换掉后我的按钮会丢失。
            string layoutFingerprint = state.OriginalGrid.GetHashCode() + "|" +
                string.Join(",", state.ActiveButtons.Select(x => x.Key));
            if (layoutFingerprint == state.CurrentLayout)
            {
                return;
            }

            state.CurrentLayout = null;
            state.OriginalGrid.DetachAll();
            foreach (GridPixelElement g in state.CustomGrids.Values)
            {
                g.DetachAll();
            }

            GridPixelElement targetGrid = GetGridForCount(state, state.ActiveButtons.Count);

            // 注意：上面的 OriginalGrid.DetachAll() 和 CustomGrids 的 DetachAll()
            // 已经把所有按钮（含我的自定义按钮）从旧 grid 上摘下来了，
            // 下面直接把「已有按钮 + 我的按钮」一起挂到目标 grid 即可。
            List<RichTextElement> buttons = state.OriginalButtons
                .Concat(state.ActiveButtons.Select(x => x.Element))
                .ToList();

            foreach (RichTextElement btn in buttons)
            {
                targetGrid.Attach(btn);
            }

            state.Window.Attach(targetGrid);

            RestoreTransform(targetGrid.handler.transform, state.GridScale);
            foreach (RichTextElement btn in buttons)
            {
                RestoreTransform(btn.handler.transform, state.ButtonScale);
            }

            targetGrid.Validate();

            // 为新增按钮打上 ContextMenuNode 标签，防止原版命中判定漏判
            foreach (ExtraButton extra in state.ActiveButtons)
            {
                Transform[] transforms = extra.Element.handler.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    transforms[i].gameObject.tag = "ContextMenuNode";
                }
            }

            state.CurrentLayout = layoutFingerprint;
        }

        private static GridPixelElement GetGridForCount(MenuState state, int extraCount)
        {
            if (extraCount <= 0)
            {
                return state.OriginalGrid;
            }

            // 总行数 = 当前 grid 里已有的按钮数 + 我的按钮数。
            // 已有按钮数每次右键都可能变化（其他模组会加按钮），
            // 所以用「总行数」作为缓存 key，而不是只用 extraCount。
            int totalRows = state.OriginalButtons.Length + extraCount;
            if (!state.CustomGrids.TryGetValue(totalRows, out GridPixelElement grid))
            {
                grid = new GridPixelElement(1, totalRows);
                state.CustomGrids[totalRows] = grid;
            }
            return grid;
        }

        private static void RestoreTransform(Transform t, Vector3 scale)
        {
            t.localScale = scale;
            Vector3 pos = t.localPosition;
            if (pos.z != 0f)
            {
                t.localPosition = new Vector3(pos.x, pos.y, 0f);
            }
        }

        private static void PaintButtons(MenuState state, ItemContextHandler handler, RichTextElement selected)
        {
            RenderHandler.ColorPalette textColor = (RenderHandler.ColorPalette)F_textColor.GetValue(handler);
            RenderHandler.ColorPalette textDisabledColor = (RenderHandler.ColorPalette)F_textDisabledColor.GetValue(handler);
            RenderHandler.ColorPalette textSelectColor = (RenderHandler.ColorPalette)F_textSelectColor.GetValue(handler);
            RenderHandler.ColorPalette textSelectDisabledColor = (RenderHandler.ColorPalette)F_textSelectDisabledColor.GetValue(handler);

            foreach (ExtraButton b in state.ActiveButtons)
            {
                bool isSelected = selected != null && b.Element == selected;
                RenderHandler.ColorPalette color;
                if (b.Entry.Enabled)
                {
                    color = isSelected ? textSelectColor : textColor;
                }
                else
                {
                    color = isSelected ? textSelectDisabledColor : textDisabledColor;
                }

                b.Element.SetText(b.Entry.Text, 10000, color);
            }
        }
    }
}
