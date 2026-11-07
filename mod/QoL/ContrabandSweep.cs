using System;
using System.Collections.Generic;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 治安检查一键收纳。
    /// 检查员到店时弹一次确认：「要把店里的违禁品和赃物收进暗格吗？」
    /// 选「是」→ 按「违禁优先、小件优先」把它们塞进海报后的暗格；
    /// 检查员离开后 → 按原容器、原坐标、原朝向放回去。
    /// 暗格固定 2×2（4 格），放不下的如实提示，不硬塞、不拆分、不合堆。
    /// </summary>
    internal static class ContrabandSweep
    {
        /// <summary>一件物品的复原信息。只活在本局内存里，读档/换场景即作废（物品留在暗格，不会丢）。</summary>
        private sealed class Entry
        {
            internal GameItem Item;
            internal GameInventory From;
            internal GameInventory To;
            internal int X;
            internal int Y;
            internal int Orientation;
            internal bool Flipped;
            internal int Count;
        }

        private static readonly List<Entry> Moved = new List<Entry>();
        private static readonly List<GameItem> Candidates = new List<GameItem>();
        private static readonly List<GameInventory> Targets = new List<GameInventory>();
        private static readonly HashSet<GameItem> Seen = new HashSet<GameItem>();

        private static StoreClientInstance _visit;
        private static bool _asked;
        private static float _restoreAt = -1f;
        private static bool _warned;

        internal static void Apply()
        {
            QLog.Info("[检查收纳] 已启用：检查员到店时会问是否把违禁品收进暗格");
        }

        internal static void Remove()
        {
            Moved.Clear();
            Candidates.Clear();
            Targets.Clear();
            Seen.Clear();
            _visit = null;
            _asked = false;
            _restoreAt = -1f;
        }

        /// <summary>每帧驱动（由 QoLUpdate 调用）。</summary>
        internal static void Tick(float dt)
        {
            try
            {
                // 主菜单/读档阶段 PlayerStore.Instance 会抛空引用（构造里建树失败），
                // 所以先看店里有没有东西，再碰它。
                EmporiumEntry store;
                try
                {
                    store = EmporiumEntry.Instance;
                }
                catch
                {
                    store = null;
                }

                if (store == null)
                {
                    if (Moved.Count > 0)
                    {
                        QLog.Warn("[检查收纳] 场景已切换，放弃复原记录；" + Moved.Count + " 件仍放在安全容器里");
                        Moved.Clear();
                        _restoreAt = -1f;
                    }

                    _visit = null;
                    _asked = false;
                    return;
                }

                PlayerStore player;
                try
                {
                    player = PlayerStore.Instance;
                }
                catch
                {
                    return;
                }

                if (player == null)
                {
                    return;
                }

                StoreClientInstance current = player.currentClientInstance;
                bool inspector = current != null && DemandRules.IsInspector(SafeBlueprint(current));

                if (inspector)
                {
                    if (current != _visit)
                    {
                        _visit = current;
                        _asked = false;
                        _restoreAt = -1f;
                    }

                    if (_asked || Moved.Count > 0)
                    {
                        return;
                    }

                    _asked = true;

                    // 没东西可收就别打扰玩家
                    Collect(store);
                    bool any = Candidates.Count > 0;
                    Candidates.Clear();

                    if (any)
                    {
                        Ask();
                    }

                    return;
                }

                // 检查员不在店
                if (Moved.Count == 0)
                {
                    _visit = null;
                    _asked = false;
                    return;
                }

                // 留一点时间等对话收尾，再放回去
                if (_restoreAt < 0f)
                {
                    _restoreAt = Time.unscaledTime + 1.5f;
                    return;
                }

                if (Time.unscaledTime >= _restoreAt)
                {
                    Restore(store);
                }
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    QLog.Warn("[检查收纳] 刷新失败：" + e.Message);
                }
            }
        }

        private static StoreClient SafeBlueprint(StoreClientInstance instance)
        {
            try
            {
                return instance.GetClientBlueprint();
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // 询问
        // ---------------------------------------------------------------

        private static void Ask()
        {
            try
            {
                GenericConfirmationUIManager ui = GenericConfirmationUIManager.Instance;
                if (ui == null)
                {
                    QLog.Warn("[检查收纳] 确认框不可用，本次跳过");
                    return;
                }

                ui.OpenPanel(
                    "治安部来检查了。\n\n要把店里的违禁品和赃物都收进海报后的暗格吗？\n（检查结束后自动放回原处）",
                    "是",
                    "否",
                    delegate { Sweep(); },
                    null);

                QLog.Info("[检查收纳] 已弹出确认框");
            }
            catch (Exception e)
            {
                QLog.Warn("[检查收纳] 弹确认框失败：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 收进去
        // ---------------------------------------------------------------

        private static void Sweep()
        {
            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                if (store == null)
                {
                    return;
                }

                Collect(store);
                BuildTargets(store);

                if (Targets.Count == 0)
                {
                    Notify("没有可用的安全容器（暗格或走私者暗格）");
                    return;
                }

                int moved = 0;
                int noSpace = 0;
                int skipped = 0;

                for (int i = 0; i < Candidates.Count; i++)
                {
                    GameItem item = Candidates[i];

                    // 已经跟着某个容器一起进了暗格/走私舱，不算「没放好」
                    if (AlreadyHidden(item))
                    {
                        continue;
                    }

                    if (!CanMove(item))
                    {
                        skipped++;
                        continue;
                    }

                    Entry record = Capture(item);
                    if (record == null)
                    {
                        skipped++;
                        continue;
                    }

                    if (PlaceAnywhere(item, record))
                    {
                        Moved.Add(record);
                        moved++;
                    }
                    else
                    {
                        noSpace++;
                    }
                }

                Candidates.Clear();

                bool leftBehind = noSpace + skipped > 0;

                string message;
                if (moved > 0)
                {
                    message = "已把 " + moved + " 件违禁品/赃物藏进安全容器";
                    if (leftBehind)
                    {
                        message += "；有部分违禁品未安全存放，请手动处理";
                    }
                }
                else if (leftBehind)
                {
                    message = "有部分违禁品未安全存放，请手动处理";
                }
                else
                {
                    message = "没有可收的违禁品";
                }

                if (PosterPulled())
                {
                    message += "（海报开着，暗格没用，只收了走私舱）";
                }

                Notify(message);
                QLog.Info("[检查收纳] 收进 " + moved + " 件，放不下 " + noSpace + "，跳过 " + skipped);
            }
            catch (Exception e)
            {
                QLog.Warn("[检查收纳] 收纳失败：" + e.Message);
            }
        }

        /// <summary>收集店里所有「会被检查员搜到」的违禁品与赃物。</summary>
        private static void Collect(EmporiumEntry store)
        {
            Candidates.Clear();
            Seen.Clear();

            AddRoot(store.invElement);
            AddRoot(store.frontInvinvElement);
            AddRoot(store.backInvinvElement);
            AddRoot(store.showcaseElement);
            AddRoot(store.trashInvElement);

            Candidates.Sort(Compare);
        }

        private static void AddRoot(GameInventory root)
        {
            if (root == null)
            {
                return;
            }

            List<GameItem> all;
            try
            {
                all = GraphUtils.FindAllChildrenType<GameItem>(root, null, CanSearchInside);
            }
            catch (Exception e)
            {
                QLog.Warn("[检查收纳] 遍历容器失败：" + e.Message);
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                GameItem item = all[i];
                if (item == null || !Seen.Add(item))
                {
                    continue;
                }

                if (ContrabandHelper.IsContraband(item) || StolenHelper.IsStolenItem(item))
                {
                    // 只算玩家自己的：客人待售的货是「未拥有」，不该由我们搬，也不该算进「没放好」
                    try
                    {
                        if (!GeneralHelper.IsItemOwned(item))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    Candidates.Add(item);
                }
            }
        }

        /// <summary>已经藏在搜不到的容器里（走私舱、隐藏容器）就不用再翻进去了。</summary>
        private static bool CanSearchInside(GameItem item)
        {
            try
            {
                return !ContrabandHelper.IsBlockSearchContainer(item);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>违禁品优先于赃物；同级别小件先收，能多塞几件。</summary>
        private static int Compare(GameItem a, GameItem b)
        {
            int ra = Rank(a);
            int rb = Rank(b);
            if (ra != rb)
            {
                return rb.CompareTo(ra);
            }

            return a.unitCount.CompareTo(b.unitCount);
        }

        private static int Rank(GameItem item)
        {
            return ContrabandHelper.IsContraband(item) ? 2 : 1;
        }

        private static bool CanMove(GameItem item)
        {
            if (item == null || item.parentInventory == null || item.unitCount <= 0)
            {
                return false;
            }

            // 暗格/走私舱本身是目的地，不能搬
            if (IsHiddenContainer(item))
            {
                return false;
            }

            // 已经跟着上一件东西进了暗格/走私舱（容器连带内容物一起搬），别再单独掏出来
            if (AlreadyHidden(item))
            {
                return false;
            }

            // 玩家手上正拖着的别搬：挪走了拖拽处理器会握着一个空引用
            if (IsBeingDragged(item))
            {
                return false;
            }

            try
            {
                return GeneralHelper.IsItemOwned(item) && item.MayRemove() &&
                       item.MaxNumRemove() >= item.unitCount;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBeingDragged(GameItem item)
        {
            try
            {
                ItemMouseDragHandler drag = ItemMouseDragHandler.current;
                if (drag != null && drag.IsDraggingItem && drag.GetCurrentItem() == item)
                {
                    return true;
                }

                ItemMultiSelectHandler multi = ItemMultiSelectHandler.current;
                return multi != null && multi.IsGroupDraggingItem(item);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>是不是「搜不到」的容器（走私者暗格三种规格）。</summary>
        private static bool IsHiddenContainer(GameItem item)
        {
            try
            {
                return item.IsTag("ITEM_HIDDEN_TAG");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>这件东西是否已经躺在某个安全容器内部（可能隔着几层）。</summary>
        private static bool AlreadyHidden(GameItem item)
        {
            GameInventory inventory = item != null ? item.parentInventory : null;

            for (int depth = 0; inventory != null && depth < 16; depth++)
            {
                for (int i = 0; i < Targets.Count; i++)
                {
                    if (ReferenceEquals(inventory, Targets[i]))
                    {
                        return true;
                    }
                }

                GameItem owner = inventory.GetParentItem();
                inventory = owner != null ? owner.parentInventory : null;
            }

            return false;
        }

        private static Entry Capture(GameItem item)
        {
            GridShape shape = item.modifiedShape;
            if (shape == null)
            {
                return null;
            }

            return new Entry
            {
                Item = item,
                From = item.parentInventory,
                X = shape.minX,
                Y = shape.minY,
                Orientation = shape.orientation,
                Flipped = shape.flipped,
                Count = item.unitCount
            };
        }

        /// <summary>
        /// 目的地顺序：海报后暗格（4 格）→ 走私者暗格（3×3 / 5×5 / 8×8，越大越靠前）。
        /// 暗格放完剩下的自动往走私舱塞。
        /// </summary>
        private static void BuildTargets(EmporiumEntry store)
        {
            Targets.Clear();

            // 海报拉开时，检查员会搜暗格（StartInspection 里就是这么判断的），
            // 这时候把东西塞进去等于白忙，直接用走私舱。
            if (store.hiddenElement != null && !PosterPulled())
            {
                Targets.Add(store.hiddenElement);
            }

            AddBays(store.invElement);
            AddBays(store.frontInvinvElement);
            AddBays(store.backInvinvElement);
            AddBays(store.showcaseElement);

            // 大的先放，能少搬几趟
            Targets.Sort(delegate (GameInventory a, GameInventory b)
            {
                return Capacity(b).CompareTo(Capacity(a));
            });
        }

        /// <summary>暗格永远排第一，其余按容量从大到小。</summary>
        private static int Capacity(GameInventory inventory)
        {
            EmporiumEntry store = EmporiumEntry.Instance;
            if (store != null && ReferenceEquals(inventory, store.hiddenElement))
            {
                return int.MaxValue;
            }

            try
            {
                GameGridInventory grid = inventory as GameGridInventory;
                GridShape shape = grid != null ? grid.inventoryShape : null;
                if (shape == null)
                {
                    return 0;
                }

                return shape.globalWidth * shape.globalHeight;
            }
            catch
            {
                return 0;
            }
        }

        private static void AddBays(GameInventory root)
        {
            if (root == null)
            {
                return;
            }

            List<GameItem> all;
            try
            {
                all = GraphUtils.FindAllChildrenType<GameItem>(root, null, CanSearchInside);
            }
            catch
            {
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                GameItem item = all[i];
                if (item == null || !IsHiddenContainer(item))
                {
                    continue;
                }

                try
                {
                    if (!GeneralHelper.IsItemOwned(item))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                GameInventory inner = InnerInventory(item);
                if (inner == null)
                {
                    continue;
                }

                bool duplicate = false;
                for (int k = 0; k < Targets.Count; k++)
                {
                    if (ReferenceEquals(Targets[k], inner))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    Targets.Add(inner);
                }
            }
        }

        private static GameInventory InnerInventory(GameItem item)
        {
            List<GameInventory> found;
            try
            {
                found = GraphUtils.FindChildrenType<GameInventory>(item, null, null);
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < found.Count; i++)
            {
                GameInventory inventory = found[i];
                if (inventory == null)
                {
                    continue;
                }

                if (ReferenceEquals(inventory.GetParentItem(), item))
                {
                    return inventory;
                }
            }

            return null;
        }

        private static bool Place(GameItem item, GameInventory target)
        {
            SlotMarker slot = FindSlot(target, item);
            if (slot == null)
            {
                return false;
            }

            int count = item.unitCount;
            int accepted = slot.TryAcceptOnce(count);
            return accepted == count && ReferenceEquals(item.parentInventory, target);
        }

        private static bool PlaceAnywhere(GameItem item, Entry record)
        {
            for (int i = 0; i < Targets.Count; i++)
            {
                GameInventory target = Targets[i];

                // 不能把容器塞进它自己
                if (item != null && ReferenceEquals(target.GetParentItem(), item))
                {
                    continue;
                }

                if (Place(item, target))
                {
                    record.To = target;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 在网格容器里找一个能放下的位置。
        /// 网格必须逐个坐标试（原版 TryInventorySlot 只认传进去的形状坐标），
        /// 槽位容器可以直接交给原版自己找。
        /// </summary>
        private static SlotMarker FindSlot(GameInventory inventory, GameItem item)
        {
            if (inventory == null || inventory.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(inventory))
            {
                return null;
            }

            int count = item.unitCount;

            GameGridInventory grid = inventory as GameGridInventory;
            if (grid == null)
            {
                SlotMarker loose = inventory.TryInventorySlot(item, count, item.modifiedShape, item.modifiedState);
                return Valid(loose, inventory, count) ? Clone(loose) : null;
            }

            GridShape area = grid.inventoryShape;
            if (area == null)
            {
                return null;
            }

            GridShapeBuilder probe = new GridShapeBuilder(item.modifiedShape);

            for (int flip = 0; flip < 2; flip++)
            {
                for (int orientation = 0; orientation < 4; orientation++)
                {
                    for (int y = area.minY; y <= area.maxY; y++)
                    {
                        for (int x = area.minX; x <= area.maxX; x++)
                        {
                            probe.SetTransform(x, y, flip != 0, orientation);

                            SlotMarker slot = inventory.TryInventorySlot(item, count, probe, item.modifiedState);
                            if (Valid(slot, inventory, count))
                            {
                                return Clone(slot);
                            }
                        }
                    }
                }
            }

            return null;
        }

        private static bool Valid(SlotMarker slot, GameInventory inventory, int count)
        {
            return slot != null && slot.IsValid() && slot.inventory == inventory &&
                   slot.itemGridShape != null && slot.targetItem == null && slot.numTransfer >= count;
        }

        private static SlotMarker Clone(SlotMarker slot)
        {
            // 形状对象是临时复用的，取到就把坐标复制下来，免得后面被改
            GridShape shape = slot.itemGridShape;
            if (shape != null)
            {
                slot.itemGridShape = shape.Clone();
            }

            return slot;
        }

        // ---------------------------------------------------------------
        // 放回去
        // ---------------------------------------------------------------

        private static void Restore(EmporiumEntry store)
        {
            _restoreAt = -1f;

            int restored = 0;
            int kept = 0;

            for (int i = 0; i < Moved.Count; i++)
            {
                Entry record = Moved[i];

                try
                {
                    GameItem item = record.Item;

                    // 玩家自己挪走了 / 已经卖掉用掉：不再管它
                    if (item == null || item.parentInventory == null ||
                        (record.To != null && !ReferenceEquals(item.parentInventory, record.To)))
                    {
                        continue;
                    }

                    GameInventory from = record.From;
                    if (from == null)
                    {
                        kept++;
                        continue;
                    }

                    if (!item.MayRemove() || item.MaxNumRemove() < record.Count)
                    {
                        kept++;
                        continue;
                    }

                    GridShape target = new GridShapeBuilder(item.modifiedShape)
                        .SetTransform(record.X, record.Y, record.Flipped, record.Orientation);

                    SlotMarker slot = from.TryInventorySlot(item, record.Count, target, item.modifiedState);
                    if (!Valid(slot, from, record.Count))
                    {
                        kept++;
                        continue;
                    }

                    int accepted = slot.TryAcceptOnce(record.Count);
                    if (accepted == record.Count && ReferenceEquals(item.parentInventory, from))
                    {
                        restored++;
                    }
                    else
                    {
                        kept++;
                        QLog.Warn("[检查收纳] 放回失败：" + SafeName(item));
                    }
                }
                catch (Exception e)
                {
                    kept++;
                    QLog.Warn("[检查收纳] 放回出错：" + e.Message);
                }
            }

            Moved.Clear();
            _visit = null;
            _asked = false;

            if (restored > 0 || kept > 0)
            {
                string message = restored > 0 ? "已把 " + restored + " 件放回原处" : "没能放回原位";

                if (kept > 0)
                {
                    message += "，" + kept + " 件留在暗格（原位被占或已改动）";
                }

                Notify(message);
                QLog.Info("[检查收纳] 复原 " + restored + " 件，留在暗格 " + kept + " 件");
            }
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        private static bool PosterPulled()
        {
            try
            {
                StorePoster poster = StorePoster.Instance;
                return poster != null && poster.isPulled;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeName(GameItem item)
        {
            try
            {
                return item != null ? item.name : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static void Notify(string text)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null)
                {
                    ui.Notify(text);
                }
            }
            catch
            {
                // 提示失败不影响搬运结果
            }
        }
    }
}
