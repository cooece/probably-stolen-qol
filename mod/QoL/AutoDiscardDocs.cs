using System;
using System.Collections.Generic;

namespace QoL
{
    /// <summary>
    /// 自动丢弃单据：一个开关统一处理 5 类「用完即弃」的纸张。
    ///
    ///   鉴定单 / 事件回执单 / 治安回执单 / 手写便签 / 探险日志
    ///
    /// 识别方式参考工坊模组「Pen的便利性优化」，但只保留一个总开关：
    ///   - expedition_log / sec_slip / postit 按物品 identifier 直接判定；
    ///   - slip 这个 ID 同时被鉴定单和事件回执单复用，靠本地化名称与 SLIP_TITLE 标签区分，
    ///     名称对不上的 slip 一律不动，避免误丢任务相关单据；
    ///   - 带 MACHINE_MANUAL_TAG 的机器说明书、以及玩家特意放进槽位的便签，都保留。
    ///
    /// 丢弃流程（这里踩过坑，别改回去）：
    ///   直接对「还挂在原容器里」的物品调 GraphUtils.TryAcceptAll 会抛异常，并把物品
    ///   半路移出容器 —— 结果既没进垃圾桶、也回不到原处，玩家看到的就是「单据凭空消失」。
    ///   因此严格按游戏原版 UI（StoreUIManager.TryTrashHoveredItem）验证过的顺序：
    ///     解析垃圾桶内容窗口 -> 检查是否有空位 -> source.Expel(item) 移出
    ///     -> GraphUtils.TryAcceptAll 放入 -> 校验 parentInventory 确实指向垃圾桶
    ///     -> 任何一步失败都用 source.UncheckedAccept(item) 放回原处，绝不留下游离物品。
    ///   绝不使用 GraphUtils.TryAcceptAllOrDestroy，那个方法在放不下时会直接销毁物品。
    ///
    /// 安全：
    ///   - 读档途中（SaveManager.IsDecoding）完全不碰物品，防止存档加载被干扰；
    ///   - 每秒扫一次，先复制快照再移动，避免遍历时集合被修改；
    ///   - 垃圾桶满了 / 槽位检查异常时保留单据，不做任何移动；
    ///   - 失败按类型去重记 WARN，不会每秒刷屏。
    /// </summary>
    internal static class AutoDiscardDocs
    {
        private const float PollInterval = 1f;

        private static float _timer;
        private static bool _loggedError;
        private static bool _trashResolved;
        private static readonly HashSet<string> _warned = new HashSet<string>();
        private static readonly HashSet<string> _unknownSlips = new HashSet<string>();

        private static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.AutoDiscardDocuments.Value;
            }
        }

        internal static void Tick(float dt)
        {
            if (!Enabled) return;

            _timer += dt;
            if (_timer < PollInterval) return;
            _timer = 0f;

            try
            {
                Scan();
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    QLog.Error("[自动丢弃单据] 扫描出错", e);
                }
            }
        }

        private static void Scan()
        {
            // 读档途中移动物品可能干扰反序列化，直接跳过这一轮
            if (SaveManager.IsDecoding) return;

            EmporiumEntry entry = EmporiumEntry.Instance;
            if (entry == null) return;

            GameInventory trash = ResolveTrash(entry);
            if (trash == null) return;

            List<GameItem> all = entry.GetAllOwnedItems(false, true);
            if (all == null || all.Count == 0) return;

            // 先复制快照：下面会把物品移出原容器，直接遍历原集合会有修改冲突
            var snapshot = new List<GameItem>(all);
            for (int i = 0; i < snapshot.Count; i++)
            {
                GameItem item = snapshot[i];
                if (item == null) continue;
                if (!GeneralHelper.IsItemOwned(item)) continue;
                if (IsAlreadyInTrash(item, entry, trash)) continue;

                string kind = Classify(item);
                if (kind == null) continue;

                DiscardOne(item, trash, kind);
            }
        }

        /// <summary>
        /// 玩家打开垃圾桶看到的容器，是「垃圾桶物品的内容窗口」里的 GameInventory
        /// （与游戏原版 StoreUIManager.TryTrashHoveredItem 完全一致）。
        /// 窗口容器没就绪时返回 null，本轮跳过，单据原样留在柜台，下一秒再试。
        /// </summary>
        private static GameInventory ResolveTrash(EmporiumEntry entry)
        {
            GameInventory fromWindow = null;
            GameInventory fallback = null;
            try
            {
                GameItem can = entry.trashcan;
                if (can != null && can.contentWindow != null)
                {
                    fromWindow = can.contentWindow.child as GameInventory;
                }
                fallback = entry.trashcanInvElement;
            }
            catch (Exception e)
            {
                WarnOnce("resolve", "解析垃圾桶容器失败，本轮不处理：" + Describe(e));
            }

            if (!_trashResolved)
            {
                _trashResolved = true;
                QLog.DebugLog("[自动丢弃单据] 垃圾桶容器：窗口=" + TypeName(fromWindow) +
                              "，trashcanInvElement=" + TypeName(fallback) +
                              "，同一对象=" + (fromWindow != null && ReferenceEquals(fromWindow, fallback)));
            }

            // 21:38 实机日志确认：窗口容器与 trashcanInvElement 是两个不同对象。
            // 拿不到窗口容器时绝不退回后者 —— 那样东西会进一个玩家打开垃圾桶也看不到的
            // 容器，又变成「看起来被直接删除」。宁可本轮不动，下一秒再试。
            if (fromWindow == null)
            {
                WarnOnce("no_window", "垃圾桶窗口容器尚未就绪，本轮跳过（单据留在原处，不会丢失）");
                return null;
            }

            return fromWindow;
        }

        private static bool IsAlreadyInTrash(GameItem item, EmporiumEntry entry, GameInventory trash)
        {
            GameInventory parent = item.parentInventory;

            // 已经没有父容器 = 游离物品，不去动它，避免把历史遗留状态越弄越乱
            if (parent == null) return true;
            if (ReferenceEquals(parent, trash)) return true;

            try
            {
                if (ReferenceEquals(parent, entry.trashcanInvElement) ||
                    ReferenceEquals(parent, entry.trashInvElement)) return true;
            }
            catch { }

            return false;
        }

        private static void DiscardOne(GameItem item, GameInventory trash, string kind)
        {
            GameInventory source = item.parentInventory;
            if (source == null) return;

            // 1) 先确认垃圾桶真的放得下。满桶时连碰都不碰，单据留在原处最安全。
            //    TryFindOneValidInventorySlot 在某些界面状态下会抛异常，必须接住。
            bool hasRoom;
            try
            {
                SlotMarker slot = trash.TryFindOneValidInventorySlot(item);
                hasRoom = slot != null && slot.numTransfer > 0;
            }
            catch (Exception e)
            {
                WarnOnce("slot_" + kind, kind + " 垃圾桶槽位检查异常，已保留单据：" + Describe(e));
                return;
            }

            if (!hasRoom)
            {
                WarnOnce("full", "垃圾桶已满，单据暂时保留在原处（清空垃圾桶后会自动继续）");
                return;
            }

            // 2) 从原容器移出。少了这一步，TryAcceptAll 会抛异常并留下游离物品。
            bool expelled;
            try
            {
                expelled = source.Expel(item);
            }
            catch (Exception e)
            {
                WarnOnce("expel_" + kind, kind + " 从原容器移出失败，已保留：" + Describe(e));
                return;
            }

            bool detached = item.parentInventory == null;
            if (!expelled && !detached)
            {
                WarnOnce("expel_false_" + kind, kind + " 无法从原容器移出，已保留");
                return;
            }

            // 3) 放进垃圾桶
            int moved = 0;
            Exception acceptError = null;
            try
            {
                moved = GraphUtils.TryAcceptAll(trash, item, -1);
            }
            catch (Exception e)
            {
                acceptError = e;
            }

            if (item.parentInventory != null && ReferenceEquals(item.parentInventory, trash))
            {
                QLog.Info("[自动丢弃单据] " + kind + " (" + SafeId(item) + ") 已移入垃圾桶");
                return;
            }

            // 4) 没进去就放回原处。这一步是「不会凭空消失」的关键。
            try
            {
                source.UncheckedAccept(item);
            }
            catch (Exception e)
            {
                QLog.Error("[自动丢弃单据] " + kind + " 回滚到原容器失败", e);
                return;
            }

            WarnOnce("accept_" + kind,
                     kind + " 移入垃圾桶失败，已放回原处（转移量 " + moved +
                     (acceptError != null ? "，异常 " + Describe(acceptError) : "") + "）");
        }

        /// <summary>返回单据类型名；不是需要丢弃的单据则返回 null。</summary>
        private static string Classify(GameItem item)
        {
            string id = SafeId(item);

            if (id == "expedition_log") return "探险日志";
            if (id == "sec_slip") return "治安回执单";

            if (id == "postit")
            {
                // 机器说明书是便签形状，但属于设备资料，不能丢
                if (item.IsTag("MACHINE_MANUAL_TAG")) return null;
                // 玩家特意插进槽位（工具箱/快捷槽）的便签也保留
                if (item.parentInventory is GameSlotInventory) return null;
                return "手写便签";
            }

            if (id == "slip")
            {
                string name = item.name;
                string title = SlipTitle(item);

                if (Eq(name, Loc("appraisal_slip_name"))) return "鉴定单";
                if (Eq(name, Loc("mech_intel_slip_name")) ||
                    Eq(title, Loc("mech_intel_slip_title"))) return "事件回执单";

                // 名称对不上的 slip 一律保留。这里留一条去重诊断，
                // 万一以后遇到本地化名称不同的事件单据，日志会记下它的真实名字，
                // 不用特意去刷剧情也能据此补上判定。
                string unknownKey = (name ?? "?") + "|" + (title ?? "?");
                if (_unknownSlips.Add(unknownKey))
                {
                    QLog.DebugLog("[自动丢弃单据] 未识别的 slip（保留）：name=" +
                                  (name ?? "null") + "，title=" + (title ?? "null"));
                }
                return null;
            }

            // 兜底：模组原有的估价单判定就靠这个标签，保留以免行为回退
            if (item.IsTag("SLIP_TITLE")) return "鉴定单";

            return null;
        }

        private static string SlipTitle(GameItem item)
        {
            try
            {
                if (!item.IsTag("SLIP_TITLE")) return null;
                TagState tag = item.GetTagReadonly("SLIP_TITLE");
                return tag != null ? tag.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Loc(string key)
        {
            try
            {
                return LocHelper.GetLocalizedMechanic(key, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static bool Eq(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                   string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string SafeId(GameItem item)
        {
            try
            {
                return !string.IsNullOrEmpty(item.identifier) ? item.identifier : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string TypeName(object o)
        {
            return o == null ? "null" : o.GetType().Name;
        }

        private static string Describe(Exception e)
        {
            return e == null ? "无" : e.GetType().Name + ": " + (string.IsNullOrEmpty(e.Message) ? "(空消息)" : e.Message);
        }

        private static void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            QLog.Warn("[自动丢弃单据] " + message);
        }
    }
}
