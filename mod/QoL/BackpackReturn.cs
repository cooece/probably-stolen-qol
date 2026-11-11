using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

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
    /// 我们挂的是**前缀**，不是后缀：
    ///   在 TransferAfterhourToInv 开始前，背包还在外出槽里 —— 取出来直接放回原位。
    ///   放成功，槽就空了，原版那句 `if (childItem != null)` 自然不成立，也就不会再去堆第一个空位；
    ///   放不进去就把背包塞回槽里，原版照常处理。这样只有一次搬运，没有中间态。
    /// </summary>
    internal static class BackpackReturn
    {
        private const string HarmonyId = "local.qol.backpack_return";

        private sealed class Home
        {
            internal GameInventory Inventory;

            /// <summary>整个形状的副本（含位置、朝向），不是拆出来的两个坐标。</summary>
            internal GridShape Shape;
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
                _harmony.Patch(after, prefix: new HarmonyMethod(typeof(BackpackReturn), nameof(RestoreHome)));

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

                // 打烊选单里选中的那个背包，此刻还在原位（方法体执行完才会被装进外出槽）
                GameItem pack = entry.selectedBackpack;
                if (pack == null || pack.parentInventory == null || pack.modifiedShape == null)
                {
                    return;
                }

                Saved[pack.uniqueId] = new Home
                {
                    Inventory = pack.parentInventory,
                    Shape = new GridShapeBuilder(pack.modifiedShape).Build(),
                    Contents = Ids(BoxOf(pack))
                };
            }
            catch (Exception e)
            {
                WarnOnce("记录背包位置失败：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 回来：倒出新东西 + 放回原位
        // ---------------------------------------------------------------

        /// <summary>挂在 TransferAfterhourToInv 之前：背包还躺在外出槽里，此时取出最干净。</summary>
        private static void RestoreHome(EmporiumEntry __instance)
        {
            if (Saved.Count == 0)
            {
                return;
            }

            try
            {
                if (__instance == null)
                {
                    return;
                }

                GameSlotInventory slot = __instance.afterhourPocketSlotInvBackpack;
                GameItem pack = slot != null ? slot.childItem : null;
                if (pack == null)
                {
                    return;
                }

                Home home;
                if (!Saved.TryGetValue(pack.uniqueId, out home))
                {
                    return;
                }

                // 1) 先把外出期间新拿到的东西倒进店里（此时背包还在槽里，最安全）
                EmptyNewLoot(__instance, pack, home);

                // 2) 再把背包放回原位；失败就交给原版
                slot.Expel(pack);

                if (TryPlace(home.Inventory, pack, home.Shape))
                {
                    QLog.Info("外出归来：背包「" + pack.name + "」已放回原位");
                    return;
                }

                slot.UncheckedAccept(pack);
                QLog.Info("外出归来：原位放不下，背包交给原版处理");
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

        /// <summary>按保存的形状放到指定格子。数量固定 1：原版放置就是 TryAcceptOnce(1)。</summary>
        private static bool TryPlace(GameInventory inventory, GameItem item, GridShape shape)
        {
            if (inventory == null || item == null || shape == null)
            {
                return false;
            }

            try
            {
                // 4 参数重载内部用 -Infinity 作为鼠标位置，等于「别重算，用我给的形状」
                SlotMarker slot = inventory.TryInventorySlot(item, 1, shape, item.modifiedState);
                if (slot == null || slot.numTransfer < 1 || slot.targetItem != null || slot.inventory == null)
                {
                    return false;
                }

                slot.TryAcceptOnce(1);

                // 只认结果：TryAcceptOnce 的返回值会骗人，父容器变了才算成功
                return ReferenceEquals(item.parentInventory, inventory);
            }
            catch (Exception e)
            {
                WarnOnce("放到指定格子失败：" + e.Message);
                return false;
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
