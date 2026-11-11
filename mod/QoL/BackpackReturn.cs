using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 外出回来时，把背包放回出门前后仓的那个格子，并把「外出期间新拿到的」东西倒出来。
    ///
    /// 原版流程（`EmporiumEntry`）：
    ///   出门 OnGoingOutside → TransferBackpackToInv()：选中的背包被塞进 afterhourPocketSlotInvBackpack
    ///   回来 OnReturning    → TransferAfterhourToInv()：背包经 TryAddToPlayerInv
    ///                        → 先试后仓、再试随身背包、最后试展示柜 = 「第一个空位」
    ///
    /// 我们只在两端各插一次：
    ///   1. TransferBackpackToInv 之前：记下 selectedBackpack 的位置与已有内容（此刻它还在原位）
    ///   2. TransferAfterhourToInv 之后：放回原格子；新拿到的东西按原版顺序倒进店里
    ///
    /// 原位被占 / 形状放不下时什么都不做，保留原版结果。
    /// </summary>
    internal static class BackpackReturn
    {
        private const string HarmonyId = "local.qol.backpack_return";

        private sealed class Home
        {
            internal GameInventory Inventory;
            internal int X;
            internal int Y;
            internal HashSet<int> Contents;
        }

        private static Harmony _harmony;
        private static bool _warned;

        /// <summary>背包 uniqueId → 出门前的位置与内容。</summary>
        private static readonly Dictionary<int, Home> Saved = new Dictionary<int, Home>();

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                MethodInfo before = AccessTools.Method(typeof(EmporiumEntry), "TransferBackpackToInv");
                MethodInfo after = AccessTools.Method(typeof(EmporiumEntry), "TransferAfterhourToInv");

                if (before == null || after == null)
                {
                    QLog.Warn("找不到 TransferBackpackToInv / TransferAfterhourToInv，背包归位未启用");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(before, prefix: new HarmonyMethod(typeof(BackpackReturn), nameof(RecordHome)));
                _harmony.Patch(after, postfix: new HarmonyMethod(typeof(BackpackReturn), nameof(RestoreHome)));

                QLog.Info("背包归位已挂载");
            }
            catch (Exception e)
            {
                QLog.Error("背包归位挂载失败", e);
            }
        }

        internal static void Remove()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // 卸载失败不影响游戏
            }

            _harmony = null;
            Saved.Clear();
        }

        // ---------------------------------------------------------------
        // 出门：记位置
        // ---------------------------------------------------------------

        private static void RecordHome()
        {
            Saved.Clear();

            try
            {
                EmporiumEntry entry = EmporiumEntry.Instance;
                if (entry == null)
                {
                    return;
                }

                // 打烊选单里选中的那个背包，此刻还在后仓原位（方法体执行完才会被装进外出槽）
                GameItem pack = entry.selectedBackpack;
                if (pack == null || pack.parentInventory == null || pack.modifiedShape == null)
                {
                    return;
                }

                Saved[pack.uniqueId] = new Home
                {
                    Inventory = pack.parentInventory,
                    X = pack.modifiedShape.minX,
                    Y = pack.modifiedShape.minY,
                    Contents = Ids(BoxOf(pack))
                };

                QLog.Info("出门前：背包「" + pack.name + "」在" + NameOf(pack.parentInventory) +
                    " (" + pack.modifiedShape.minX + "," + pack.modifiedShape.minY + ")");
            }
            catch (Exception e)
            {
                WarnOnce("记录背包位置失败：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 回来：归位 + 倒出新东西
        // ---------------------------------------------------------------

        private static void RestoreHome()
        {
            if (Saved.Count == 0)
            {
                return;
            }

            try
            {
                EmporiumEntry entry = EmporiumEntry.Instance;
                if (entry == null)
                {
                    return;
                }

                foreach (KeyValuePair<int, Home> pair in Saved)
                {
                    GameItem pack = entry.FindItemByUniqueID(pair.Key);
                    if (pack == null)
                    {
                        continue;      // 背包被用掉/卖掉，跳过
                    }

                    EmptyNewLoot(entry, pack, pair.Value);
                    PutBack(pack, pair.Value);
                }
            }
            catch (Exception e)
            {
                WarnOnce("背包归位失败：" + e.Message);
            }
            finally
            {
                Saved.Clear();
            }
        }

        /// <summary>出门前不在背包里、回来却出现的东西 → 按原版顺序放进店里；放不进就留在背包。</summary>
        private static void EmptyNewLoot(EmporiumEntry entry, GameItem pack, Home home)
        {
            try
            {
                GameInventory box = BoxOf(pack);
                if (box == null)
                {
                    return;
                }

                List<GameItem> items = box.childItems;
                if (items == null)
                {
                    return;
                }

                int moved = 0;
                List<GameItem> snapshot = new List<GameItem>(items);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    GameItem item = snapshot[i];
                    if (item == null || home.Contents.Contains(item.uniqueId))
                    {
                        continue;      // 出门前就在包里：那是玩家自己带的，留着
                    }

                    box.Expel(item);
                    entry.TryAddToPlayerInv(item);

                    if (item.parentInventory == null)
                    {
                        box.UncheckedAccept(item);      // 店里放不下，塞回去，绝不丢
                        continue;
                    }

                    if (!ReferenceEquals(item.parentInventory, box))
                    {
                        moved++;
                    }
                }

                if (moved > 0)
                {
                    QLog.Info("外出归来：从背包倒出 " + moved + " 件新物品");
                }
            }
            catch (Exception e)
            {
                WarnOnce("倒出背包物品失败：" + e.Message);
            }
        }

        /// <summary>把背包放回出门前那个容器的那个格子。</summary>
        private static void PutBack(GameItem pack, Home home)
        {
            try
            {
                GameInventory target = home.Inventory;
                GameInventory source = pack.parentInventory;
                if (target == null || source == null)
                {
                    QLog.Info("归位诊断：目标=" + NameOf(target) + " 来源=" + NameOf(source) + "（跳过）");
                    return;
                }

                GridShape now = pack.modifiedShape;
                if (ReferenceEquals(source, target) && now != null && now.minX == home.X && now.minY == home.Y)
                {
                    return;      // 已经在原位，别动
                }

                // 克隆一份再改坐标：直接改 modifiedShape 会污染物品自己的形状
                GridShapeBuilder shape = pack.modifiedShape.Clone() as GridShapeBuilder;
                if (shape == null)
                {
                    QLog.Info("归位诊断：形状不是 GridShapeBuilder（" + pack.modifiedShape.GetType().Name + "）");
                    return;
                }

                shape.SetPosition(home.X, home.Y);

                // 数量固定 1：原版 GraphUtils.TryAccept 就是 TryAcceptOnce(1)；
                // 传 unitCount 在它为 0 时会让 numTransfer 直接变 0，传大于 1 又会触发拆分
                SlotMarker slot = target.TryInventorySlot(pack, 1, new Vector2(float.NaN, 0f), shape, null);
                if (slot == null || slot.numTransfer < 1)
                {
                    QLog.Info("归位诊断：原位放不下 —— 目标=" + NameOf(target) +
                        " 容器=" + SizeOf(target) +
                        " 记录坐标=(" + home.X + "," + home.Y + ")" +
                        " 形状=" + shape.globalWidth + "×" + shape.globalHeight +
                        " 现在在" + NameOf(source) +
                        " numTransfer=" + (slot == null ? -1 : slot.numTransfer) +
                        " unitCount=" + pack.unitCount +
                        " 可移除=" + pack.MaxNumRemove() +
                        " 愿意收=" + target.MayHaveValidInventorySlot(pack));
                    return;      // 原位被占 / 放不下：保留原版「第一个空位」的结果
                }

                if (!source.Expel(pack))
                {
                    QLog.Info("归位诊断：从" + NameOf(source) + "移出失败");
                    return;
                }

                slot.TryAcceptOnce(1);

                // 只认结果：TryAcceptOnce 的返回值会骗人，父容器变了才算成功
                if (ReferenceEquals(pack.parentInventory, target))
                {
                    QLog.Info("外出归来：背包「" + pack.name + "」已放回原位 (" + home.X + "," + home.Y + ")");
                    return;
                }

                source.UncheckedAccept(pack);      // 没放进去，塞回原处
            }
            catch (Exception e)
            {
                WarnOnce("背包归位失败：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------

        /// <summary>背包自己装东西的格子。</summary>
        private static GameInventory BoxOf(GameItem pack)
        {
            try
            {
                if (pack.contentWindow != null && pack.contentWindow.childElement is GameInventory)
                {
                    return (GameInventory)pack.contentWindow.childElement;
                }

                List<GameInventory> found = GraphUtils.FindAllChildrenType<GameInventory>(pack);
                return found != null && found.Count > 0 ? found[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<int> Ids(GameInventory inventory)
        {
            HashSet<int> ids = new HashSet<int>();

            try
            {
                if (inventory == null || inventory.childItems == null)
                {
                    return ids;
                }

                List<GameItem> items = inventory.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] != null)
                    {
                        ids.Add(items[i].uniqueId);
                    }
                }
            }
            catch
            {
                // 取不到就是空集合：那样回店时不会倒任何东西，安全
            }

            return ids;
        }

        private static string SizeOf(GameInventory inventory)
        {
            GameGridInventory grid = inventory as GameGridInventory;
            if (grid != null && grid.inventoryShape != null)
            {
                return grid.inventoryShape.width + "×" + grid.inventoryShape.height;
            }

            return "?";
        }

        private static string NameOf(GameInventory inventory)
        {
            EmporiumEntry entry = EmporiumEntry.Instance;
            if (inventory == null)
            {
                return "null";
            }

            if (entry != null)
            {
                if (ReferenceEquals(inventory, entry.backInvinvElement)) return "后仓";
                if (ReferenceEquals(inventory, entry.frontInvinvElement)) return "前厅";
                if (ReferenceEquals(inventory, entry.invElement)) return "随身背包";
                if (ReferenceEquals(inventory, entry.showcaseElement)) return "展示柜";
            }

            return inventory.GetType().Name;
        }

        private static void WarnOnce(string text)
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            QLog.Warn(text);
        }
    }
}
