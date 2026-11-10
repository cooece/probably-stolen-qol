using System;
using System.Collections.Generic;

namespace QoL
{
    /// <summary>
    /// 右键「升级」：不把材料拖到物品上，直接在右键菜单里升一级。
    ///
    /// 规则全部问游戏自己：目标能不能升用各类型的 CanXxx(target)，
    /// 材料能不能用在这件东西上用材料自己的 mayThisTargetItemFunc / mayItemTargetItemFunc，
    /// 执行就调它的 onXxxTargetItemFunc。我们只负责「挑一个材料」和「写字」。
    /// </summary>
    internal static class UpgradeMenu
    {
        private enum Kind
        {
            None,
            Upgrade,
            Repair,
            Charge
        }

        private const string ExpansionKit = "module_bay_expansion_kit";

        internal static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.UpgradeMenu.Value;
            }
        }

        internal static void Apply()
        {
            ItemContextMenu.RegisterProvider(Provide);
            QLog.Info("右键升级菜单已挂载");
        }

        internal static void Remove()
        {
            ItemContextMenu.UnregisterProvider(Provide);
        }

        // ---------------------------------------------------------------
        // 菜单项
        // ---------------------------------------------------------------

        private static void Provide(GameItem item, List<ItemMenuEntry> entries)
        {
            if (!Enabled || item == null) return;

            try
            {
                Kind kind = Classify(item);
                GameItem material = FindMaterial(item, out Kind matKind, out bool isKit);

                if (material == null)
                {
                    if (kind == Kind.None) return;   // 根本不能升：不显示

                    // 能升但没材料：灰显，让人知道这件东西可以升
                    entries.Add(new ItemMenuEntry
                    {
                        Key = "qol_upgrade",
                        ElementName = "qol_upgrade",
                        Text = TextOf(kind),
                        Enabled = false,
                        OnClick = null
                    });
                    return;
                }

                GameItem chosen = material;
                bool kit = isKit;
                entries.Add(new ItemMenuEntry
                {
                    Key = "qol_upgrade",
                    ElementName = "qol_upgrade",
                    Text = TextOf(matKind),
                    Enabled = true,
                    OnClick = delegate (GameItem target) { Run(target, chosen, kit); }
                });
            }
            catch (Exception e)
            {
                QLog.Warn("右键升级菜单出错：" + e.Message);
            }
        }

        private static string TextOf(Kind kind)
        {
            switch (kind)
            {
                case Kind.Repair: return "修理";
                case Kind.Charge: return "充能";
                case Kind.Upgrade: return "升级";
                default: return "升级";
            }
        }

        // ---------------------------------------------------------------
        // 判定：这件东西能做什么
        // ---------------------------------------------------------------

        private static Kind Classify(GameItem t)
        {
            try
            {
                if (ReactorHelper.CanUpgrade(t)) return Kind.Upgrade;
                if (CurrentConverterHelper.CanUpgradeEfficiency(t)) return Kind.Upgrade;

                if (t.IsTag("FRIDGE_TAG") && (MachineFridge.CanUpgradeStorage(t) || MachineFridge.CanUpgradePowerEfficiency(t)))
                {
                    return Kind.Upgrade;
                }

                if (t.IsTag("CAGE_TAG"))
                {
                    if (AnimalCage.CanUpgradeSize(t)) return Kind.Upgrade;
                    if (AnimalCage.CanRepair(t)) return Kind.Repair;
                }

                if (t.IsTag("TOOLBOX_TAG") && ToolboxHelper.CanUpgradeToolbox(t)) return Kind.Upgrade;
                if (WaterTankHelper.CanUpgrade(t)) return Kind.Upgrade;

                if (t.IsTag("RAS_TAG") && (MachineRAS.CanUpgradeWidth(t) || MachineRAS.CanUpgradeHeight(t)
                    || MachineRAS.CanUpgradePowerEfficiency(t)))
                {
                    return Kind.Upgrade;
                }

                if (MachineBottlePrinter.CanUpgradePrinter(t)) return Kind.Upgrade;

                if ("metal_scanner".Equals(t.identifier) && ScavHelper.CanUpgradeScavScanner(t)) return Kind.Upgrade;
                if ("vacuum_robot".Equals(t.identifier) && HusbandryHelper.CanUpgradeVacuumRobot(t)) return Kind.Upgrade;

                if (HypertechBatteryHelper.CanUpgradeOutput(t)) return Kind.Upgrade;
                if (MindReprogrammerHelper.CanAddCharge(t)) return Kind.Charge;

                if ("module_extractor".Equals(t.identifier)) return Kind.Repair;
                if (t.IsTag("MODULE_TAG") && t.identifier != null && t.identifier.Equals("system_module_corrupt")) return Kind.Repair;

                // 售货机/售水机核心：升级的是它所在的机器
                GameItem parent = SafeParent(t);
                if (parent != null
                    && (VendingMachineHelper.CanUpgradedCapacity(parent) || VendingMachineHelper.CanUpgradedAttractiveness(parent)))
                {
                    return Kind.Upgrade;
                }

                // 标准机器的模组舱扩容（用扩容套件）
                if (t.IsTag("STANDARD_MACHINE_TAG") && MachineHelper.CanExpand(t)) return Kind.Upgrade;

                return Kind.None;
            }
            catch
            {
                return Kind.None;
            }
        }

        /// <summary>单个材料对这件东西会造成什么效果（决定菜单文字）。</summary>
        private static Kind KindOf(GameItem material, GameItem target)
        {
            try
            {
                if (material.IsGameItemType("MATERIAL"))
                {
                    if (material.identifier != null && material.identifier.StartsWith("electronic", StringComparison.Ordinal))
                    {
                        if ("module_extractor".Equals(target.identifier)) return Kind.Repair;
                        if (target.IsTag("MODULE_TAG") && "system_module_corrupt".Equals(target.identifier)) return Kind.Repair;
                        if (MindReprogrammerHelper.CanAddCharge(target)) return Kind.Charge;
                        return Kind.Upgrade;
                    }

                    if ("scrap_metal".Equals(material.identifier))
                    {
                        if (target.IsTag("CAGE_TAG") && AnimalCage.CanRepair(target)) return Kind.Repair;
                        return Kind.Upgrade;
                    }

                    return Kind.Upgrade;
                }
            }
            catch
            {
                // 下面按未知处理
            }

            return Kind.Upgrade;
        }

        // ---------------------------------------------------------------
        // 找材料
        // ---------------------------------------------------------------

        private static GameItem FindMaterial(GameItem target, out Kind kind, out bool isKit)
        {
            kind = Kind.None;
            isKit = false;

            GameItem best = null;
            int bestValue = int.MaxValue;

            foreach (GameItem candidate in DesequencerAutoFeed.AllOwnedItems())
            {
                if (candidate == null || ReferenceEquals(candidate, target)) continue;
                if (!Usable(candidate)) continue;

                bool kit = string.Equals(candidate.identifier, ExpansionKit, StringComparison.Ordinal);
                if (!kit && !candidate.IsGameItemType("MATERIAL")) continue;

                bool ok = kit ? May(candidate.mayItemTargetItemFunc, candidate, target)
                              : May(candidate.mayThisTargetItemFunc, candidate, target)
                                || May(candidate.canThisTargetItemFunc, candidate, target);
                if (!ok) continue;

                int value = (int)Math.Min(int.MaxValue, candidate.unitValue);
                if (best == null || value < bestValue)
                {
                    best = candidate;
                    bestValue = value;
                    kind = KindOf(candidate, target);
                    isKit = kit;
                }
            }

            return best;
        }

        private static bool May(Func<GameItem, GameItem, bool> func, GameItem material, GameItem target)
        {
            if (func == null) return false;

            try { return func(material, target); }
            catch { return false; }
        }

        /// <summary>材料能拿吗：自己的、不在锁箱、没在拖拽。</summary>
        private static bool Usable(GameItem item)
        {
            try
            {
                if (!GeneralHelper.IsItemOwned(item)) return false;
                if (item.IsTag("ITEM_BEHIND_LOCK_TAG") || item.IsTag("LOCKED_TAG")) return false;

                ItemMouseDragHandler drag = ItemMouseDragHandler.current;
                if (drag != null && ReferenceEquals(drag.GetCurrentItem(), item)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static GameItem SafeParent(GameItem item)
        {
            try
            {
                GameInventory inv = item.parentInventory;
                return inv != null ? inv.GetParentItem() : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // 执行
        // ---------------------------------------------------------------

        private static void Run(GameItem target, GameItem material, bool isKit)
        {
            try
            {
                if (target == null || material == null) return;
                if (!GeneralHelper.IsItemOwned(target) || !Usable(material)) return;

                // 点下去之前再问一次：期间可能被消耗 / 目标已达上限
                if (isKit)
                {
                    if (material.onItemTargetItemFunc == null) return;
                    material.onItemTargetItemFunc(material, target);
                }
                else
                {
                    if (material.onThisTargetItemFunc == null) return;
                    material.onThisTargetItemFunc(material, target);
                }

                QLog.Info("右键升级：" + TextOf(KindOf(material, target)) + "「" + target.name + "」用了「" + material.name + "」");
            }
            catch (Exception e)
            {
                QLog.Warn("右键升级执行失败：" + e.Message);
            }
        }
    }
}
