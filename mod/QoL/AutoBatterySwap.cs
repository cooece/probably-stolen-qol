using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 全自动换电：玩家不用管电池，也不用打开任何面板。
    ///
    /// 分两种用电设备，内部自动分流，设置项只有一个下拉：
    ///   周期型机器（冰箱、酒架、水培…）：电池常驻。订阅游戏自带 ModHook.OnGoingSleepLate
    ///     做早晨统一检查，再 patch PowerHelper 的取电接口捕捉白天断电/快没电，
    ///     统一入队后由 Tick 补换。换下来的旧电池落回候选原位，候选来自充电器就自动回充。
    ///   主动型工具（水瓶打印机、箱子分配器…）：平时不耗电，电池常驻在里面反而充不了。
    ///     patch MachineryButton.OnSelect，在按钮回调之前若电池槽是空的就临时装一块，
    ///     回调结束立刻取回原处。同一次点击就能成功使用，用完不占电池。
    ///
    /// 判定取自 BatteryManager 的严谨内核（先验原生 CanDrawPowerSource、Improves 防倒换、
    /// 按能量降序），触发取自 DangpuPlus（早晨 + 取电失败），并修掉两边已知的几个坑：
    /// 早晨只跑一次、周期数对即时路径无效、全有或全无、跳过关机机器、硬编码设备名、
    /// 每台设备重复全量扫描、旧电池不回充电器。
    /// </summary>
    internal static class AutoBatterySwap
    {
        private const string HarmonyId = "local.qol.auto_battery_swap";
        private const float TickInterval = 0.2f;
        private const float RetryCooldown = 3f;
        private const int MaxClosureDepth = 3;

        internal const string Off = "关闭";
        internal const string MorningOnly = "只早晨换";
        internal const string Auto1 = "随时换（够 1 次）";
        internal const string Auto2 = "随时换（够 2 次）";

        private static readonly FieldInfo CallbackField =
            AccessTools.Field(typeof(MachineryButton), "callback");

        private static Harmony _harmony;
        private static bool _applied;

        // 待补换的机器 uniqueId；两条触发路径共用，Tick 统一处理
        private static readonly HashSet<int> Queue = new HashSet<int>();
        // 每台机器实际观察到的一次耗电（很多主动设备没有 machinery_drawn_power tag）
        private static readonly Dictionary<int, int> Draws = new Dictionary<int, int>();
        // 失败后短暂冷却，避免每帧重试
        private static readonly Dictionary<int, float> Cooldown = new Dictionary<int, float>();
        // 通过按钮回调识别出「主动型」的机器：不给它们装常驻电池
        private static readonly HashSet<int> ButtonDriven = new HashSet<int>();
        // 主动型：本次按钮点击临时装进去的电池 + 它原来的容器
        private static readonly Dictionary<int, GameItem> Inserted = new Dictionary<int, GameItem>();
        private static readonly Dictionary<int, GameInventory> InsertedFrom = new Dictionary<int, GameInventory>();

        private static GameItem _buttonMachine;
        private static float _nextTick;

        private static string Mode
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.AutoBatterySwapMode != null
                    ? s.AutoBatterySwapMode.Value : Off;
            }
        }

        private static bool Enabled => Mode != Off;

        private static bool Immediate => Mode == Auto1 || Mode == Auto2;

        private static int Cycles => Mode == Auto2 ? 2 : 1;

        // ---------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;

            try
            {
                _harmony = new Harmony(HarmonyId);

                // 早晨：游戏自带事件，夜间结算完毕、进入早晨时触发
                ModHook.OnGoingSleepLate += OnMorning;

                PatchPowerHelper();
                PatchMachineryButton();

                QLog.Info("自动换电池已挂载");
            }
            catch (Exception e)
            {
                QLog.Error("自动换电池：挂载失败", e);
                Remove();
            }
        }

        private static void PatchPowerHelper()
        {
            Patch(typeof(PowerHelper), "CanDrawPowerSource", nameof(CanDrawPostfix));
            Patch(typeof(PowerHelper), "DrawPowerSource", nameof(DrawPostfix));
        }

        private static void Patch(Type type, string methodName, string postfixName)
        {
            MethodInfo method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                QLog.Warn("自动换电池：找不到 " + type.Name + "." + methodName);
                return;
            }

            // 参数名对不上时 Harmony 会静默传 0，这里提前发现。
            ParameterInfo[] ps = method.GetParameters();
            if (ps.Length < 2 || ps[0].Name != "gameItem" || ps[1].Name != "amount")
            {
                QLog.Warn("自动换电池：" + methodName + " 参数签名变了，已跳过");
                return;
            }

            _harmony.Patch(method, postfix: new HarmonyMethod(typeof(AutoBatterySwap), postfixName));
        }

        private static void PatchMachineryButton()
        {
            MethodInfo onSelect = AccessTools.Method(typeof(MachineryButton), "OnSelect");
            if (onSelect == null)
            {
                QLog.Warn("自动换电池：找不到 MachineryButton.OnSelect，按需装填不可用");
                return;
            }

            if (CallbackField == null)
            {
                QLog.Warn("自动换电池：找不到按钮回调字段，按需装填不可用");
                return;
            }

            _harmony.Patch(onSelect,
                prefix: new HarmonyMethod(typeof(AutoBatterySwap), nameof(ButtonPrefix)),
                postfix: new HarmonyMethod(typeof(AutoBatterySwap), nameof(ButtonPostfix)));
        }

        internal static void Remove()
        {
            try
            {
                ModHook.OnGoingSleepLate -= OnMorning;
                if (_harmony != null) _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception e)
            {
                QLog.Warn("自动换电池：卸载时出错 —— " + e.Message);
            }

            _harmony = null;
            _applied = false;
            _buttonMachine = null;
            _nextTick = 0f;
            Queue.Clear();
            Draws.Clear();
            Cooldown.Clear();
            ButtonDriven.Clear();
            Inserted.Clear();
            InsertedFrom.Clear();
        }

        // ---------------------------------------------------------------
        // 触发一：早晨
        // ---------------------------------------------------------------

        private static void OnMorning()
        {
            if (!Enabled) return;
            try
            {
                // 只入队，不在这里搬物品：睡醒时交换缓冲区可能被占用，交给 Tick 重试，
                // 避免像 DangpuPlus 那样一次失败就整天不换。
                List<GameItem> all = AllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    GameItem m = all[i];
                    if (m != null && m.IsTag("MACHINE_RECHARGABLE_TAG"))
                        Queue.Add(m.uniqueId);
                }
                _nextTick = 0f;
            }
            catch (Exception e)
            {
                QLog.Warn("自动换电池：早晨检查失败 —— " + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 触发二：白天取电
        // ---------------------------------------------------------------

        private static void CanDrawPostfix(GameItem gameItem, int amount, bool __result)
        {
            try
            {
                if (!Enabled || __result || gameItem == null) return;
                GameItem m = HostMachine(gameItem);
                if (m != null) Enqueue(m.uniqueId);
            }
            catch
            {
                // 取电检查非常频繁，任何异常都吞掉，绝不影响游戏
            }
        }

        private static void DrawPostfix(GameItem gameItem, int amount, bool __result)
        {
            try
            {
                if (!Enabled || !__result || gameItem == null) return;
                GameItem m = HostMachine(gameItem);
                if (m == null) return;

                // 只从真实扣电里记录耗电，避免 UI 指示灯的探测值污染
                if (amount > 0)
                {
                    int old;
                    Draws.TryGetValue(m.uniqueId, out old);
                    if (amount > old) Draws[m.uniqueId] = amount;
                }

                if (!Immediate) return;
                if (Energy(gameItem) < Need(m)) Enqueue(m.uniqueId);
            }
            catch
            {
                // 同上
            }
        }

        // ---------------------------------------------------------------
        // 触发三：主动型工具按按钮
        // ---------------------------------------------------------------

        private static void ButtonPrefix(MachineryButton __instance)
        {
            _buttonMachine = null;
            if (!Enabled) return;

            try
            {
                GameSlotInventory slot = FindBatterySlot(__instance);
                if (slot == null) return;

                GameItem machine = slot.GetParentItem();
                if (machine == null || !machine.IsTag("MACHINE_RECHARGABLE_TAG")) return;
                _buttonMachine = machine;

                // 能从按钮闭包找到电池槽，说明这是「按一下才用电」的主动型工具。
                // 记下来，早晨统一检查时不给它装常驻电池。
                ButtonDriven.Add(machine.uniqueId);

                // 严格只在空槽时装填：玩家自己放进去的电池一律不碰、也不取回。
                if (slot.childItem != null) return;
                if (!CanOperate()) return;

                List<GameItem> all = AllItems();
                List<GameItem> cells = CollectCells(all);
                GameItem best = null;
                for (int i = 0; i < cells.Count; i++)
                {
                    GameItem c = cells[i];
                    if (c != null && Energy(c) > 0 && PowerHelper.CanDrawPowerSource(c, 1))
                    {
                        best = c;
                        break; // cells 已按能量降序，第一块就是最满的
                    }
                }
                if (best == null) return;

                GameInventory from = best.parentInventory;
                if (!Install(slot, best)) return;

                Inserted[machine.uniqueId] = best;
                InsertedFrom[machine.uniqueId] = from;
            }
            catch (Exception e)
            {
                QLog.Warn("自动换电池：按需装填失败 —— " + e.Message);
            }
        }

        private static void ButtonPostfix()
        {
            GameItem machine = _buttonMachine;
            _buttonMachine = null;
            if (machine == null) return;

            int id = machine.uniqueId;
            GameItem cell;
            if (!Inserted.TryGetValue(id, out cell)) return;
            Inserted.Remove(id);

            GameInventory from;
            InsertedFrom.TryGetValue(id, out from);
            InsertedFrom.Remove(id);

            try
            {
                if (cell != null) ReturnCell(cell, from);
            }
            catch (Exception e)
            {
                QLog.Warn("自动换电池：用完取回失败 —— " + e.Message);
            }
        }

        /// <summary>把临时电池放回原来的容器（通常是充电器），再退到店内两个外层格子。</summary>
        private static void ReturnCell(GameItem cell, GameInventory from)
        {
            if (TryMoveInto(from, cell)) return;

            EmporiumEntry ee = EmporiumEntry.Instance;
            if (ee == null) return;
            if (TryMoveInto(ee.backInvinvElement, cell)) return;
            if (TryMoveInto(ee.invElement, cell)) return;

            QLog.Warn("自动换电池：没地方放回电池，暂时留在机器里");
        }

        private static bool TryMoveInto(GameInventory target, GameItem cell)
        {
            if (target == null || cell == null) return false;
            if (target.IsInsertLocked()) return false;
            SlotMarker marker = target.TryFindOneValidInventorySlot(cell);
            if (marker == null || marker.numTransfer < 1) return false;
            return marker.TryAcceptOnce(1) == 1;
        }

        /// <summary>从按钮回调闭包里找出它对应的电池槽；找不到就当普通按钮放过。</summary>
        private static GameSlotInventory FindBatterySlot(MachineryButton btn)
        {
            if (btn == null || CallbackField == null) return null;
            Action cb = CallbackField.GetValue(btn) as Action;
            if (cb == null || cb.Target == null) return null;
            return ScanForSlot(cb.Target, 0);
        }

        private static GameSlotInventory ScanForSlot(object obj, int depth)
        {
            if (obj == null || depth > MaxClosureDepth) return null;

            FieldInfo[] fields;
            try { fields = obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return null; }

            for (int i = 0; i < fields.Length; i++)
            {
                object v;
                try { v = fields[i].GetValue(obj); }
                catch { continue; }
                if (v == null) continue;

                GameSlotInventory slot = v as GameSlotInventory;
                if (slot != null) return slot;

                // 兜底：有的闭包只捕获机器本体，从机器反查电池槽
                GameItem machine = v as GameItem;
                if (machine != null && machine.IsTag("MACHINE_RECHARGABLE_TAG"))
                {
                    try
                    {
                        GameSlotInventory s = MachineHelper.GetBatterySlot(machine);
                        if (s != null) return s;
                    }
                    catch { }
                }

                // 只在编译器生成的闭包对象里继续找，避免顺着游戏对象图无限递归
                string tn = fields[i].FieldType.Name;
                if (tn.IndexOf("DisplayClass", StringComparison.Ordinal) >= 0 ||
                    tn.IndexOf("<>", StringComparison.Ordinal) >= 0 ||
                    fields[i].Name.IndexOf("<>", StringComparison.Ordinal) >= 0)
                {
                    GameSlotInventory found = ScanForSlot(v, depth + 1);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // 每帧驱动：统一处理队列
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Enabled || Queue.Count == 0) return;
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + TickInterval;
            if (!CanOperate()) return;

            try
            {
                ProcessQueue();
            }
            catch (Exception e)
            {
                QLog.Warn("自动换电池：补换失败 —— " + e.Message);
            }
        }

        private static void ProcessQueue()
        {
            List<GameItem> all = AllItems();
            List<GameItem> machines = new List<GameItem>();
            HashSet<int> alive = new HashSet<int>();

            for (int i = 0; i < all.Count; i++)
            {
                GameItem m = all[i];
                if (m == null || !m.IsTag("MACHINE_RECHARGABLE_TAG")) continue;
                alive.Add(m.uniqueId);
                machines.Add(m);
            }

            Trim(alive); // 机器没了就清掉状态，不留泄漏

            List<GameItem> cells = CollectCells(all);
            HashSet<int> used = new HashSet<int>();
            float now = Time.unscaledTime;

            for (int i = 0; i < machines.Count; i++)
            {
                GameItem m = machines[i];
                int id = m.uniqueId;
                if (!Queue.Contains(id)) continue;

                float until;
                if (Cooldown.TryGetValue(id, out until) && now < until) continue;
                Queue.Remove(id);

                int need = Need(m);
                GameSlotInventory slot = MachineHelper.GetBatterySlot(m);
                if (slot == null || ((GameInventory)slot).IsInsertLocked() || ((GameInventory)slot).IsRemoveLocked())
                    continue;

                GameItem old = slot.childItem;
                if (old != null && IsSpecial(old)) continue; // 电流转换器等不碰

                // 主动型工具不装常驻电池：空槽交给按钮按需装填。
                // 周期型机器带 machinery_drawn_power 标记，才在早晨自动补上。
                if (old == null && (ButtonDriven.Contains(id) || TagDraw(m) <= 0)) continue;

                int cur = old == null ? 0 : Energy(old);
                int once = DrawOf(m);
                bool needs = old == null
                    || !PowerHelper.CanDrawPowerSource(old, once)
                    || cur < need;
                if (!needs) continue;

                GameItem best = PickBest(cells, need, cur, once, used);
                if (best == null)
                {
                    Cooldown[id] = now + RetryCooldown;
                    continue;
                }

                if (SwapOrInstall(slot, old, best))
                {
                    used.Add(best.uniqueId);
                    Cooldown.Remove(id);
                }
                else
                {
                    Cooldown[id] = now + RetryCooldown;
                }
            }
        }

        private static void Trim(HashSet<int> alive)
        {
            Draws.RemoveAllBut(alive);
            Cooldown.RemoveAllBut(alive);
            ButtonDriven.RemoveWhere(id => !alive.Contains(id));
        }

        /// <summary>候选电池：只认普通可充电电池，按能量降序，同能量优先充电器里的。</summary>
        private static List<GameItem> CollectCells(List<GameItem> all)
        {
            HashSet<GameInventory> installedSlots = new HashSet<GameInventory>();
            List<GameItem> cells = new List<GameItem>();

            for (int i = 0; i < all.Count; i++)
            {
                GameItem m = all[i];
                if (m == null || !m.IsTag("MACHINE_RECHARGABLE_TAG")) continue;
                GameSlotInventory s = null;
                try { s = MachineHelper.GetBatterySlot(m); } catch { }
                if (s != null) installedSlots.Add(s);
            }

            for (int i = 0; i < all.Count; i++)
            {
                GameItem c = all[i];
                if (c == null || !IsNormalCell(c)) continue;
                GameInventory inv = c.parentInventory;
                if (inv == null || installedSlots.Contains(inv)) continue;
                if (!GeneralHelper.IsItemOwned(c) || ItemPinHelper.IsPinned(c)) continue;
                if (inv.IsRemoveLocked() || !c.MayRemove() || c.MaxNumRemove() < 1) continue;
                cells.Add(c);
            }

            cells.Sort((a, b) =>
            {
                int ea = Energy(a), eb = Energy(b);
                if (ea != eb) return eb.CompareTo(ea);
                int ca = InCharger(a) ? 1 : 0, cb = InCharger(b) ? 1 : 0;
                if (ca != cb) return cb.CompareTo(ca);
                return a.uniqueId.CompareTo(b.uniqueId);
            });
            return cells;
        }

        /// <summary>优先够 N 次的最高能量电池；都没有就降级选比旧电池好的，不做全有或全无。</summary>
        private static GameItem PickBest(List<GameItem> cells, int need, int cur, int once, HashSet<int> used)
        {
            GameItem fallback = null;
            for (int i = 0; i < cells.Count; i++)
            {
                GameItem c = cells[i];
                if (used.Contains(c.uniqueId)) continue;
                int e = Energy(c);
                if (e <= cur) continue;
                if (!PowerHelper.CanDrawPowerSource(c, once)) continue;
                if (e >= need) return c;
                if (fallback == null) fallback = c;
            }
            return fallback;
        }

        private static bool SwapOrInstall(GameSlotInventory slot, GameItem old, GameItem best)
        {
            if (old == null) return Install(slot, best);

            // 首选游戏原生一步交换：旧电池自动落到新电池原来的格子（充电器 → 自动回充）
            try
            {
                if (GraphUtils.SwapItemsInPlace(best, old) && slot.childItem == best) return true;
            }
            catch { }

            // 兜底：原生拖放式交换
            try
            {
                EmporiumEntry ee = EmporiumEntry.Instance;
                if (best.unitCount == 1 && old.unitCount == 1 &&
                    best.parentInventory != null && !best.parentInventory.IsInsertLocked() &&
                    best.parentInventory.MayHaveValidInventorySlot(old) &&
                    ee != null && ee.swapBufferElement != null && ee.swapBufferElement.childItem == null &&
                    best.MayTarget(old) && best.CanTarget(old))
                {
                    best.Target(old);
                    return slot.childItem == best;
                }
            }
            catch { }

            return false;
        }

        private static bool Install(GameSlotInventory slot, GameItem cell)
        {
            if (slot == null || cell == null || ((GameInventory)slot).IsInsertLocked()) return false;
            SlotMarker marker = ((GameInventory)slot).TryFindOneValidInventorySlot(cell);
            if (marker == null || marker.numTransfer < 1) return false;
            return marker.TryAcceptOnce(1) == 1;
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        private static List<GameItem> AllItems()
        {
            PlayerStore store = PlayerStore.Instance;
            return store != null ? store.FindAllItem(true) : new List<GameItem>();
        }

        private static bool CanOperate()
        {
            EmporiumEntry ee = EmporiumEntry.Instance;
            if (ee == null || PlayerStore.Instance == null) return false;
            if (ee.swapBufferElement != null && ee.swapBufferElement.childItem != null) return false;
            ItemMouseDragHandler drag = ItemMouseDragHandler.current;
            return drag == null || !drag.IsDraggingItem;
        }

        private static void Enqueue(int id)
        {
            float until;
            if (Cooldown.TryGetValue(id, out until) && Time.unscaledTime < until) return;
            Queue.Add(id);
        }

        private static GameItem HostMachine(GameItem cell)
        {
            if (cell == null || cell.parentInventory == null) return null;
            GameItem m = cell.parentInventory.GetParentItem();
            return m != null && m.IsTag("MACHINE_RECHARGABLE_TAG") ? m : null;
        }

        private static int Energy(GameItem item)
        {
            if (item == null) return 0;
            try { return PowerHelper.GetAvailableEnergyFromItem(item, false); }
            catch { return item.GetTagReadonly("power_source_item_energy").GetInt(); }
        }

        private static int Capacity(GameItem item)
        {
            if (item == null) return 0;
            return item.GetTagReadonly("power_source_item_max_energy").GetInt();
        }

        /// <summary>普通可充电电池：有电、有容量，且不是一次性 / 电流转换器 / 高科技电池。</summary>
        private static bool IsNormalCell(GameItem item)
        {
            if (item == null || !item.IsTag("power_source_item") || Capacity(item) <= 0) return false;
            return !PowerHelper.IsDisposablePowerSource(item)
                && !CurrentConverterHelper.IsCurrentConverter(item)
                && !HypertechBatteryHelper.IsHypertechBattery(item);
        }

        private static bool IsSpecial(GameItem item)
        {
            return item == null
                || CurrentConverterHelper.IsCurrentConverter(item)
                || HypertechBatteryHelper.IsHypertechBattery(item)
                || PowerHelper.IsDisposablePowerSource(item)
                || Capacity(item) <= 0;
        }

        private static bool InCharger(GameItem cell)
        {
            try
            {
                GameItem p = cell.parentInventory != null ? cell.parentInventory.GetParentItem() : null;
                return p != null && p.identifier == "recharger";
            }
            catch { return false; }
        }

        /// <summary>机器自带的周期耗电标记；主动型工具没设这个 tag，读出来是 0。</summary>
        private static int TagDraw(GameItem machine)
        {
            try { return MachineryHelper.GetMachinePowerUsage(machine); }
            catch { return 0; }
        }

        private static int DrawOf(GameItem machine)
        {
            int d = TagDraw(machine);
            int observed;
            if (Draws.TryGetValue(machine.uniqueId, out observed) && observed > d) d = observed;
            return Math.Max(1, d);
        }

        private static int Need(GameItem machine)
        {
            return DrawOf(machine) * Cycles;
        }
    }

    /// <summary>Dictionary 的按存活 id 裁剪扩展，只在本文件用。</summary>
    internal static class BatteryDictExt
    {
        internal static void RemoveAllBut<T>(this Dictionary<int, T> dict, HashSet<int> alive)
        {
            if (dict.Count == 0) return;
            List<int> dead = null;
            foreach (int key in dict.Keys)
            {
                if (alive.Contains(key)) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) dict.Remove(dead[i]);
        }
    }
}
