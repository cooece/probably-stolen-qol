using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 自动换滤芯：水壶里的滤芯、水族柜（RAS）机器 4 个滤芯槽，用完了自动换新的。
    ///
    /// 触发两条：
    ///   1. 过夜：关店 / 夜间结算前扫一遍，按水箱里的污染物量估算「今晚要消耗多少耐久」，
    ///      不够就提前换（乘 1.2 安全系数，宁早勿晚）；没水、没电、没养鱼的机器不浪费滤芯。
    ///   2. 随时（设置项可选）：实际耐久已经归零的，立刻换 —— 主要给水壶用，
    ///      白天倒水时用到空滤芯等于白倒。
    ///
    /// 选芯顺序：干净的优先 → 高级优先 → 同档先用耐久少的（把快用完的先用掉）。
    /// 过夜那一路还会优先挑「够今晚用」的里耐久最少的那支，避免拿满的换掉小需求。
    ///
    /// 候选池排除：UV 滤芯（没有耐久概念）、已经装在别的槽/水壶里的、
    /// 未拥有 / 待售 / 被玩家钉住的、正在拖拽的、槽位锁住的。
    ///
    /// 旧滤芯去向：塞进「已经装着滤芯的储物箱」（能叠就叠），没有这种箱子就放后仓，
    /// 后仓也放不下就留在原地（也就是新滤芯原来的位置），绝不销毁。
    /// </summary>
    internal static class AutoFilterSwap
    {
        internal const string Off = "关闭";
        internal const string NightOnly = "只在过夜换";
        internal const string Always = "随时换（含过夜）";

        private const string HarmonyId = "local.qol.auto_filter_swap";

        /// <summary>随时换的扫描间隔（秒）：别每帧扫全店物品。</summary>
        private const float ScanInterval = 1.5f;

        /// <summary>失败后的冷却（秒），也用于过夜检查的重试节奏。</summary>
        private const float RetryCooldown = 6f;

        /// <summary>「没滤芯可换」气泡的最小间隔（秒），别一直刷。</summary>
        private const float NoticeCooldown = 60f;

        /// <summary>接水限流提示的最小间隔（秒）。</summary>
        private const float FillNoticeCooldown = 4f;

        /// <summary>过夜消耗估算的安全系数（%）。</summary>
        private const int SafetyPercent = 120;

        private static Harmony _harmony;
        private static bool _applied;

        private static float _nextScan;
        private static float _nextNightTry;
        private static float _nextNotice;
        private static float _nextFillNotice;
        private static bool _nightPending;

        /// <summary>一个待换的滤芯位：机器槽或水壶槽。</summary>
        private sealed class Target
        {
            public GameItem Host;
            public GameSlotInventory Slot;
            public bool IsMachine;
        }

        private static string Mode
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.AutoFilterSwapMode != null
                    ? s.AutoFilterSwapMode.Value : Off;
            }
        }

        private static bool Enabled => Mode != Off;

        private static bool Immediate => Mode == Always;

        // ---------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;

            try
            {
                // 过夜两个入口都挂：关店和夜间结算各来一次，谁先到算谁
                ModHook.OnShutterClosedLate += OnNight;
                ModHook.OnHandlingNightlyServicesEarly += OnNight;
                ModHook.OnWakingUpLate += OnMorning;

                PatchAddWater();

                _nextScan = 0f;
                _nextNightTry = 0f;
                _nightPending = false;

                QLog.Info("自动换滤芯已挂载");
            }
            catch (Exception e)
            {
                QLog.Error("自动换滤芯：挂载失败", e);
                Remove();
            }
        }

        /// <summary>拦水龙头注水：只放「滤芯能完整滤干净」的那部分水进来。</summary>
        private static void PatchAddWater()
        {
            try
            {
                MethodInfo add = AccessTools.Method(typeof(WaterHelper), "AddWater",
                    new[] { typeof(GameItem), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(int), typeof(bool) });
                if (add == null)
                {
                    QLog.Warn("自动换滤芯：找不到 WaterHelper.AddWater，接水限流不可用");
                    return;
                }

                if (_harmony == null) _harmony = new Harmony(HarmonyId);
                _harmony.Patch(add, prefix: new HarmonyMethod(typeof(AutoFilterSwap), nameof(AddWaterPrefix)));
            }
            catch (Exception e)
            {
                QLog.Warn("自动换滤芯：挂接水补丁失败 —— " + e.Message);
            }
        }

        internal static void Remove()
        {
            try
            {
                ModHook.OnShutterClosedLate -= OnNight;
                ModHook.OnHandlingNightlyServicesEarly -= OnNight;
                ModHook.OnWakingUpLate -= OnMorning;
            }
            catch (Exception e)
            {
                QLog.Warn("自动换滤芯：卸载时出错 —— " + e.Message);
            }

            try
            {
                if (_harmony != null) _harmony.UnpatchAll(HarmonyId);
            }
            catch { }

            _harmony = null;
            _applied = false;
            _nightPending = false;
            _nextScan = 0f;
            _nextNightTry = 0f;
            _nextNotice = 0f;
            _nextFillNotice = 0f;
        }

        /// <summary>过夜：只挂个标记，真正的搬货交给 Tick（结算期间别抢交换缓冲）。</summary>
        private static void OnNight()
        {
            if (!Enabled) return;
            _nightPending = true;
        }

        private static void OnMorning()
        {
            _nightPending = false;
        }

        // ---------------------------------------------------------------
        // 每帧驱动
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Enabled) return;

            try
            {
                float now = Time.unscaledTime;

                if (_nightPending && now >= _nextNightTry)
                {
                    if (!CanOperate()) return;
                    _nextNightTry = now + RetryCooldown;
                    if (RunPass(true)) _nightPending = false;
                    return;
                }

                if (!Immediate) return;
                if (now < _nextScan) return;
                _nextScan = now + ScanInterval;
                if (!CanOperate()) return;

                RunPass(false);
            }
            catch (Exception e)
            {
                QLog.Warn("自动换滤芯：检查出错 —— " + e.Message);
            }
        }

        /// <summary>返回 true = 这一趟跑完了（过夜标记可以清）；false = 时机不对，稍后重试。</summary>
        private static bool RunPass(bool nightly)
        {
            List<GameItem> all = AllItems();
            if (all.Count == 0) return false;

            List<GameItem> pool = CollectCandidates(all);
            List<Target> targets = CollectTargets(all);
            if (targets.Count == 0) return true;

            HashSet<int> used = new HashSet<int>();
            int swapped = 0;
            int advanced = 0;
            int basic = 0;
            int failed = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                Target t = targets[i];
                if (t == null || t.Slot == null) continue;

                GameItem cur = null;
                try { cur = t.Slot.childItem; } catch { }
                if (cur != null && !cur.IsTag("WATER_FILTER")) continue;   // 槽里是别的东西，不碰

                int have = cur != null ? Real(cur) : 0;
                int need = 0;

                if (nightly && t.IsMachine)
                {
                    need = EstimateNight(t.Host, cur);
                    if (need <= 0) continue;                 // 今晚用不上过滤
                    if (cur != null && have >= need) continue; // 够用
                }
                else
                {
                    // 随时换：只换「已经装着、但彻底用完」的；
                    // 空槽不碰 —— 那是玩家自己的选择，等过夜那趟统一补
                    if (cur == null) continue;
                    if (have > 0) continue;
                }

                GameItem pick = Pick(pool, used, nightly && t.IsMachine ? need : 1);
                if (pick == null)
                {
                    failed++;
                    continue;
                }

                if (!SwapInto(t, pick, all))
                {
                    failed++;
                    continue;
                }

                used.Add(pick.uniqueId);
                swapped++;
                if (IsAdvanced(pick)) advanced++;
                else basic++;
            }

            if (swapped > 0) Report(swapped, advanced, basic, failed);
            else if (failed > 0) NoticeNoFilter(failed);

            return true;
        }

        // ---------------------------------------------------------------
        // 目标：RAS 机器滤芯槽 + 水壶里的滤芯
        // ---------------------------------------------------------------

        private static List<Target> CollectTargets(List<GameItem> all)
        {
            List<Target> list = new List<Target>();

            for (int i = 0; i < all.Count; i++)
            {
                GameItem it = all[i];
                if (it == null) continue;

                if (it.IsTag("RAS_TAG"))
                {
                    List<GameSlotInventory> slots = GraphUtils.FindAllChildrenType<GameSlotInventory>(it,
                        delegate (GameSlotInventory s) { return s != null && s.IsTag("RAS_FILTER_SLOT_TAG"); });

                    for (int j = 0; j < slots.Count; j++)
                    {
                        Target t = new Target();
                        t.Host = it;
                        t.Slot = slots[j];
                        t.IsMachine = true;
                        list.Add(t);
                    }
                    continue;
                }

                // 水壶：不管是躺在背包、仓库里，还是插在净水器机器里，都算
                if (!string.Equals(it.identifier, "water_pitcher", StringComparison.Ordinal)) continue;
                if (!IsOwned(it)) continue;

                List<GameSlotInventory> slots2 = GraphUtils.FindAllChildrenType<GameSlotInventory>(it);
                if (slots2.Count == 0) continue;

                Target t2 = new Target();
                t2.Host = it;
                t2.Slot = slots2[0];
                t2.IsMachine = false;
                list.Add(t2);
            }

            return list;
        }

        // ---------------------------------------------------------------
        // 候选滤芯
        // ---------------------------------------------------------------

        private static List<GameItem> CollectCandidates(List<GameItem> all)
        {
            List<GameItem> list = new List<GameItem>();
            for (int i = 0; i < all.Count; i++)
            {
                if (IsCandidate(all[i])) list.Add(all[i]);
            }
            list.Sort(CompareFilters);
            return list;
        }

        private static bool IsCandidate(GameItem f)
        {
            if (f == null) return false;
            if (!f.IsTag("WATER_FILTER")) return false;
            if (string.Equals(f.identifier, "uv_filter", StringComparison.Ordinal)) return false; // 没耐久概念
            if (!f.IsTag("DURABILITY_TAG")) return false;
            if (Real(f) <= 0) return false;
            if (!IsOwned(f)) return false;

            try
            {
                if (ItemPinHelper.IsPinned(f)) return false;
            }
            catch { }

            GameInventory inv = f.parentInventory;
            if (inv == null) return false;
            if (IsFilterSlot(inv)) return false;   // 已经装在槽/水壶里：不抢

            try
            {
                if (inv.IsRemoveLocked()) return false;
                if (!f.MayRemove()) return false;
                if (f.MaxNumRemove() < 1) return false;
            }
            catch { }

            return true;
        }

        /// <summary>这个容器是不是「滤芯位」（RAS 滤芯槽 / 水壶的滤芯格）。</summary>
        private static bool IsFilterSlot(GameInventory inv)
        {
            if (inv == null) return false;

            GameSlotInventory slot = inv as GameSlotInventory;
            if (slot != null && slot.IsTag("RAS_FILTER_SLOT_TAG")) return true;

            GameItem host = null;
            try { host = inv.GetParentItem(); } catch { }
            return host != null && string.Equals(host.identifier, "water_pitcher", StringComparison.Ordinal);
        }

        /// <summary>干净优先 → 高级优先 → 同档先用耐久少的。</summary>
        private static int CompareFilters(GameItem a, GameItem b)
        {
            int sa = IsStolen(a) ? 1 : 0;
            int sb = IsStolen(b) ? 1 : 0;
            if (sa != sb) return sa.CompareTo(sb);

            int aa = IsAdvanced(a) ? 0 : 1;
            int ab = IsAdvanced(b) ? 0 : 1;
            if (aa != ab) return aa.CompareTo(ab);

            int ra = Real(a);
            int rb = Real(b);
            if (ra != rb) return ra.CompareTo(rb);
            return a.uniqueId.CompareTo(b.uniqueId);
        }

        /// <summary>挑一支：第一个「耐久够 need」的（排序已按优先级），都不够就挑耐久最多的。</summary>
        private static GameItem Pick(List<GameItem> pool, HashSet<int> used, int need)
        {
            GameItem fallback = null;

            for (int i = 0; i < pool.Count; i++)
            {
                GameItem f = pool[i];
                if (f == null || used.Contains(f.uniqueId)) continue;

                int d = Real(f);
                if (d <= 0) continue;
                if (d >= need) return f;

                if (fallback == null || d > Real(fallback)) fallback = f;
            }

            return fallback;
        }

        // ---------------------------------------------------------------
        // 换芯
        // ---------------------------------------------------------------

        private static bool SwapInto(Target t, GameItem replacement, List<GameItem> all)
        {
            GameItem old = null;
            try { old = t.Slot.childItem; } catch { }

            if (old == null) return Install(t.Slot, replacement);

            try
            {
                // 原生一步对调：新滤芯进槽，旧滤芯落到新滤芯原来的位置
                if (GraphUtils.SwapItemsInPlace(replacement, old) && t.Slot.childItem == replacement)
                {
                    RelocateOld(old, all);
                    return true;
                }
            }
            catch (Exception e)
            {
                QLog.Warn("自动换滤芯：对调失败 —— " + e.Message);
            }

            return false;
        }

        private static bool Install(GameSlotInventory slot, GameItem item)
        {
            if (slot == null || item == null) return false;

            GameInventory inv = slot;
            try
            {
                if (inv.IsInsertLocked()) return false;
                SlotMarker marker = inv.TryFindOneValidInventorySlot(item);
                if (marker == null || marker.numTransfer < 1) return false;
                return marker.TryAcceptOnce(1) == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>旧滤芯：已经在装滤芯的箱子里就不动；否则找个装滤芯的箱子，再退到后仓、背包。</summary>
        private static void RelocateOld(GameItem old, List<GameItem> all)
        {
            if (old == null) return;

            GameInventory cur = null;
            try { cur = old.parentInventory; } catch { }
            if (cur == null) return;

            if (!IsFilterSlot(cur) && ContainsOtherFilter(cur, old)) return;

            GameGridInventory box = FindFilterBox(old, all);
            if (box != null && TryMoveInto(box, old)) return;

            EmporiumEntry ee = EmporiumEntry.Instance;
            if (ee == null) return;
            if (TryMoveInto(ee.backInvinvElement, old)) return;
            if (TryMoveInto(ee.invElement, old)) return;

            // 实在没地方：就留在新滤芯原来的位置，不销毁
        }

        private static bool ContainsOtherFilter(GameInventory inv, GameItem self)
        {
            try
            {
                List<GameItem> items = inv.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem it = items[i];
                    if (it != null && it != self && it.IsTag("WATER_FILTER")) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>找一个「已经装着滤芯」的储物格子（不是滤芯槽），用来收纳旧滤芯。</summary>
        private static GameGridInventory FindFilterBox(GameItem old, List<GameItem> all)
        {
            for (int i = 0; i < all.Count; i++)
            {
                GameItem f = all[i];
                if (f == null || f == old) continue;
                if (!f.IsTag("WATER_FILTER")) continue;

                GameGridInventory grid = f.parentInventory as GameGridInventory;
                if (grid == null) continue;
                if (IsFilterSlot(grid)) continue;

                try
                {
                    if (grid.IsInsertLocked()) continue;
                    if (!grid.MayHaveValidInventorySlot(old)) continue;
                }
                catch { continue; }

                return grid;
            }
            return null;
        }

        private static bool TryMoveInto(GameInventory target, GameItem item)
        {
            if (target == null || item == null) return false;

            try
            {
                if (target.IsInsertLocked()) return false;
                SlotMarker marker = target.TryFindOneValidInventorySlot(item);
                if (marker == null || marker.numTransfer < 1) return false;
                return marker.TryAcceptOnce(1) == 1;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 过夜估算：水箱里的污染物要吃掉多少耐久
        // ---------------------------------------------------------------

        private static int EstimateNight(GameItem machine, GameItem filter)
        {
            if (machine == null) return 0;

            GameItem tank = FindTank(machine);
            if (tank == null) return 0;
            if (!HasUsablePower(machine)) return 0;   // 没电=晚上不跑，不用换

            int size = 0;
            try
            {
                if (filter != null && filter.IsTag("FILTER_SIZE_TAG"))
                    size = filter.GetTagReadonly("FILTER_SIZE_TAG").GetInt();
            }
            catch { }

            int est = 0;
            try
            {
                List<Liquid> liquids = Liquid.Liquids;
                for (int i = 0; i < liquids.Count; i++)
                {
                    Liquid l = liquids[i];
                    if (l == null) continue;
                    if (string.Equals(l.identifier, "water", StringComparison.Ordinal)) continue;

                    // 滤芯孔径比污染物颗粒大：这类根本滤不掉，也不消耗耐久
                    if (size > 0 && size > l.particleSize) continue;

                    int parts = 0;
                    try { parts = tank.GetTagReadonly(l.currentPart).GetInt(); }
                    catch { continue; }
                    if (parts <= 0) continue;

                    int per = Mathf.Max(1, l.filterVolumeRemoved);
                    int use = Mathf.Max(1, l.filterDurabilityUsage);
                    est += ((parts + per - 1) / per) * use;
                }
            }
            catch (Exception e)
            {
                QLog.Warn("自动换滤芯：估算失败 —— " + e.Message);
                return 0;
            }

            return est * SafetyPercent / 100;
        }

        private static GameItem FindTank(GameItem machine)
        {
            try
            {
                List<GameSlotInventory> slots = GraphUtils.FindAllChildrenType<GameSlotInventory>(machine);
                for (int i = 0; i < slots.Count; i++)
                {
                    GameItem it = slots[i] != null ? slots[i].childItem : null;
                    if (it != null && it.IsTag("RAS_WATER_TANK_TAG")) return it;
                }
            }
            catch { }
            return null;
        }

        private static bool HasUsablePower(GameItem machine)
        {
            try
            {
                GameSlotInventory slot = MachineHelper.GetBatterySlot(machine);
                GameItem cell = slot != null ? slot.childItem : null;
                if (cell == null) return false;
                return PowerHelper.GetAvailableEnergyFromItem(cell, false) > 0;
            }
            catch
            {
                // 判断不了就当有电：宁可多检查一次，也别漏换
                return true;
            }
        }

        // ---------------------------------------------------------------
        // 物品枚举 / 小工具
        // ---------------------------------------------------------------

        private static List<GameItem> AllItems()
        {
            List<GameItem> src = new List<GameItem>();

            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store != null) src.AddRange(store.FindAllItem(true));
            }
            catch
            {
                // 主菜单 / 读档阶段 PlayerStore.Instance 会抛空引用，吃掉
            }

            try
            {
                // 店里地上的机器（水族柜、净水器）不在那几个窗口里，得从房间地面捞
                GameMaster gm = GameMaster.current;
                if (gm != null && gm.raidManager != null) src.AddRange(gm.raidManager.GetAllGameItem());
            }
            catch { }

            List<GameItem> list = new List<GameItem>(src.Count);
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < src.Count; i++)
            {
                GameItem it = src[i];
                if (it == null) continue;
                if (!seen.Add(it.uniqueId)) continue;
                list.Add(it);
            }
            return list;
        }

        private static bool CanOperate()
        {
            EmporiumEntry ee = EmporiumEntry.Instance;
            if (ee == null || PlayerStore.Instance == null) return false;
            if (ee.swapBufferElement != null && ee.swapBufferElement.childItem != null) return false;

            ItemMouseDragHandler drag = ItemMouseDragHandler.current;
            return drag == null || !drag.IsDraggingItem;
        }

        private static bool IsOwned(GameItem item)
        {
            try { return GeneralHelper.IsItemOwned(item); }
            catch { return false; }
        }

        private static bool IsStolen(GameItem item)
        {
            try { return StolenHelper.IsStolenItem(item); }
            catch { return false; }
        }

        private static bool IsAdvanced(GameItem item)
        {
            return item != null && string.Equals(item.identifier, "water_filter_adv", StringComparison.Ordinal);
        }

        private static int Real(GameItem item)
        {
            if (item == null) return 0;
            try
            {
                if (!item.IsTag("DURABILITY_TAG")) return 0;
                return DurabilityHelper.GetRealDurability(item);
            }
            catch
            {
                return 0;
            }
        }

        // ---------------------------------------------------------------
        // 提示
        // ---------------------------------------------------------------

        private static void Report(int swapped, int advanced, int basic, int failed)
        {
            string msg = "已换 " + swapped + " 支滤芯" + Detail(advanced, basic);
            if (failed > 0) msg += "，还有 " + failed + " 处没换（备用滤芯不够或没地方放）";

            Notify(msg);
        }

        private static void NoticeNoFilter(int failed)
        {
            if (Time.unscaledTime < _nextNotice) return;
            _nextNotice = Time.unscaledTime + NoticeCooldown;
            Notify("没有可用的备用滤芯，" + failed + " 处该换了");
        }

        private static string Detail(int advanced, int basic)
        {
            if (advanced > 0 && basic > 0) return "（" + advanced + " 高级 / " + basic + " 普通）";
            if (advanced > 0) return "（高级）";
            return "（普通）";
        }

        private static void Notify(string msg)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(msg);
            }
            catch
            {
                // 气泡失败不影响功能
            }

            QLog.Info("自动换滤芯：" + msg);
        }

        // ---------------------------------------------------------------
        // 接水限流：WaterHelper.AddWater 前缀
        // ---------------------------------------------------------------
        //
        // 水龙头注水是一次灌满（addAmount=9999999），游戏会把整壶水的污染物
        // 一次性丢给滤芯：能滤多少滤多少，滤不掉的照样进壶 —— 整壶水就废了。
        // 这里先算出「这点耐久能完整滤干净多少水」，把 addAmount 压到这个值。

        private static bool AddWaterPrefix(GameItem waterContainer, int grade, ref int addAmount,
                                           bool useRange, int lowPara, int highPara)
        {
            try
            {
                if (!Enabled || waterContainer == null) return true;
                if (!waterContainer.IsTag("FILTER_CONTAINER_TAG")) return true;   // 只有装滤芯的容器才限流

                GameItem filter = WaterHelper.GetFilterItem(waterContainer);
                if (filter == null) return true;                                   // 没滤芯：照游戏原样

                int capacity = waterContainer.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt();
                int filled = 0;
                List<Liquid> liquids = Liquid.Liquids;
                for (int i = 0; i < liquids.Count; i++)
                {
                    if (liquids[i] == null) continue;
                    filled += waterContainer.GetTagReadonly(liquids[i].currentPart).GetInt();
                }

                int free = capacity - filled;
                if (free <= 0) return true;

                int want = addAmount < 0 ? free : Math.Min(addAmount, free);
                if (want <= 0) return true;

                int max = MaxFullyFilterable(filter, want, grade, useRange, lowPara);
                if (max >= want) return true;      // 够滤，正常灌

                if (max <= 0)
                {
                    FillNotice("滤芯耐久不够，接不了干净的水（换一支再接）");
                    return false;                  // 一点也不放进来，免得接成脏水
                }

                addAmount = max;
                FillNotice("滤芯耐久只够过滤 " + (max / 1000) + " ml，已按上限接水");
                QLog.Info("自动换滤芯：接水限流 " + (want / 1000) + " → " + (max / 1000) + " ml（耐久 " + Real(filter) + "）");
                return true;
            }
            catch (Exception e)
            {
                QLog.Warn("自动换滤芯：接水限流出错 —— " + e.Message);
                return true;
            }
        }

        /// <summary>这点耐久能完整过滤多少水量（parts）。按最脏的水质算，宁可少接也不接脏。</summary>
        private static int MaxFullyFilterable(GameItem filter, int want, int grade, bool useRange, int lowPara)
        {
            Dictionary<string, float> dist = Distribution(grade);
            if (dist == null || dist.Count == 0) return want;

            int durability = Real(filter);
            if (durability <= 0) return 0;

            int size = 0;
            try
            {
                if (filter.IsTag("FILTER_SIZE_TAG")) size = filter.GetTagReadonly("FILTER_SIZE_TAG").GetInt();
            }
            catch { }

            int purity = useRange ? lowPara : GradeLowPurity(grade);

            int lo = 0;
            int hi = want;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (CanFullyFilter(mid, dist, purity, size, durability)) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>按 AddWater 的公式还原污染物分配，估算这点水会不会把耐久吃超。</summary>
        private static bool CanFullyFilter(int volume, Dictionary<string, float> dist, int purity, int size, int durability)
        {
            if (volume <= 0) return true;

            long pure = ((long)volume * purity + 9999) / 10000;
            int dirty = (int)(volume - pure);
            if (dirty <= 0) return true;

            int cost = 0;
            List<Liquid> liquids = Liquid.Liquids;

            foreach (KeyValuePair<string, float> kv in dist)
            {
                Liquid l = null;
                for (int i = 0; i < liquids.Count; i++)
                {
                    if (liquids[i] != null && string.Equals(liquids[i].currentPart, kv.Key, StringComparison.Ordinal))
                    {
                        l = liquids[i];
                        break;
                    }
                }
                if (l == null) continue;

                // 微生物只有 UV 滤芯处理，普通滤芯不扣耐久
                if (string.Equals(l.identifier, "microbe", StringComparison.Ordinal)) continue;
                // 孔径比颗粒大：这类根本滤不掉，也不扣耐久
                if (size > 0 && size > l.particleSize) continue;

                int amount = (int)(dirty * kv.Value);
                if (amount <= 0) continue;

                int per = Mathf.Max(1, l.filterVolumeRemoved);
                int use = Mathf.Max(1, l.filterDurabilityUsage);
                cost += ((amount + per - 1) / per) * use;

                if (cost > durability) return false;
            }

            return cost <= durability;
        }

        /// <summary>和 AddWater 内部同一套水质分布。</summary>
        private static Dictionary<string, float> Distribution(int grade)
        {
            try
            {
                switch (grade)
                {
                    case 0:
                    case 1:
                    case 3:
                        return WaterPremadeHelper.waterContaminantDistribution;
                    case 2:
                        return WaterPremadeHelper.baseWaterContaminantDistribution;
                    case 4:
                        return WaterPremadeHelper.rustWaterContaminantDistribution;
                    default:
                        return WaterPremadeHelper.gutterflowContaminantDistribution;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>和 AddWater 内部同一套「该等级最脏能有多脏」（纯度下限）。</summary>
        private static int GradeLowPurity(int grade)
        {
            switch (grade)
            {
                case 0: return 9990;
                case 1: return 9900;
                case 2: return 9800;
                case 3: return 9600;
                case 4: return 9000;
                default: return 3500;
            }
        }

        private static void FillNotice(string msg)
        {
            if (Time.unscaledTime < _nextFillNotice) return;
            _nextFillNotice = Time.unscaledTime + FillNoticeCooldown;
            Notify(msg);
        }
    }
}
