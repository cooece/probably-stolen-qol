using System;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 模组入口。游戏加载器扫描 DLL 里实现了 IMod 接口的类，
    /// 然后按顺序调用 Init → OnEnable（关掉模组时调用 OnDisable）。
    /// 这三个方法的名字和签名都不能改，否则加载器找不到你。
    /// </summary>
    public sealed class ModEntry : IMod
    {
        /// <summary>日志前缀，方便在 Player.log 里搜到自己的输出。</summary>
        public const string Tag = "[QoL] ";

        /// <summary>当前模组 ID，Init 时从 manifest.xml 里读出来。</summary>
        internal static string ModId { get; private set; } = "local.qol";

        /// <summary>设置项。OnEnable 之后才可用。</summary>
        internal static QoLSettings Settings { get; private set; }

        public void Init(ModManifest manifest)
        {
            // Init 时只是拿到清单信息，游戏本体还没准备好，别在这里碰游戏对象。
            try
            {
                if (manifest != null && !string.IsNullOrEmpty(manifest.ID))
                {
                    ModId = manifest.ID;
                }

                string ver = manifest != null ? manifest.ModVersion : "?";
                QLog.Init();
                QLog.Info("Init，ID=" + ModId + "，版本=" + ver);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "Init 失败：" + e);
            }
        }

        public void OnEnable()
        {
            // 每个功能单独兜住：
            // 以前用一个 try 包住全部 Apply，任何一个抛异常，后面所有功能都不挂载，
            // 而且日志里只看得到「OnEnable 失败」，根本不知道是哪一个。
            try { Settings = new QoLSettings(ModId); }
            catch (Exception e) { QLog.Error("设置项加载失败", e); }

            try { QLog.DumpEnvironment(); }
            catch (Exception e) { QLog.Warn("打印环境信息失败：" + e.Message); }

            try { QoLUpdate.Ensure(); }
            catch (Exception e) { QLog.Error("每帧驱动启动失败", e); }

            ApplySafe("物品分类标签", ItemTypeBadge.Apply);
            ApplySafe("提示框精简", TooltipTextFilter.Apply);
            ApplySafe("顾客对话", CustomerDialog.Apply);
            ApplySafe("到店提示", CustomerAlert.Apply);
            ApplySafe("对话加速", DialogueSpeedUp.Apply);
            ApplySafe("摇铃确认", CustomerSkipConfirm.Apply);
            ApplySafe("打烊确认", ShutterConfirm.Apply);
            ApplySafe("店内行情", CalendarPriceLine.Apply);
            ApplySafe("交易结果弹窗", TradeResultPopup.Apply);
            ApplySafe("需求高亮", DemandHighlight.Apply);
            ApplySafe("检查收纳", ContrabandSweep.Apply);
            ApplySafe("水质标签", WaterLabelSync.Apply);
            // 道具快捷键内部会挂上右键菜单（ItemContextMenu）
            ApplySafe("道具快捷键", QuickToolHotkey.Apply);
            ApplySafe("便捷框选", QuickBoxSelect.Apply);
            ApplySafe("一键上柜台", QuickShelf.Apply);
            ApplySafe("双击开锁", QuickUnlock.Apply);
            ApplySafe("柜台溢出", CounterOverflow.Apply);
            ApplySafe("前台提醒", CounterClutterWarn.Apply);
            ApplySafe("汉化调试面板", DebugPanelZh.Apply);
            ApplySafe("荧光莓品质徽标", delegate { BerryBadge.Apply(Settings); });
            ApplySafe("自动换电", AutoBatterySwap.Apply);
            ApplySafe("自动换滤芯", AutoFilterSwap.Apply);
            ApplySafe("解序器续卡", DesequencerAutoFeed.Apply);
            ApplySafe("设置页隐藏项", SettingsHideEntry.Apply);
            ApplySafe("酿酒工坊", Brewing.Apply);

            try { ModHook.OnCreateTooltipLate += OnTooltipLate; }
            catch (Exception e) { QLog.Error("订阅提示框钩子失败", e); }

            QLog.Info("已启用：" + (Settings != null ? Settings.Count : 0) + " 项设置，配置文件 " +
                      (Settings != null ? Settings.FilePath : "?") + "，日志路径 " + QLog.LogFilePath);
        }

        public void OnDisable()
        {
            // 同样逐个兜住：卸载链里任何一个抛异常，后面的补丁就撤不掉了，
            // 异常还会冒回游戏加载器。
            try { ModHook.OnCreateTooltipLate -= OnTooltipLate; }
            catch (Exception e) { QLog.Warn("退订提示框钩子失败：" + e.Message); }

            RemoveSafe("自动换电", AutoBatterySwap.Remove);
            RemoveSafe("自动换滤芯", AutoFilterSwap.Remove);
            RemoveSafe("解序器续卡", DesequencerAutoFeed.Remove);
            RemoveSafe("设置页隐藏项", SettingsHideEntry.Remove);
            RemoveSafe("酿酒工坊", Brewing.Remove);
            RemoveSafe("道具快捷键", QuickToolHotkey.Remove);
            // 右键菜单补丁是 QuickToolHotkey 挂的，但它的 Remove 只撤 provider，补丁要单独撤
            RemoveSafe("右键菜单", ItemContextMenu.Remove);
            RemoveSafe("便捷框选", QuickBoxSelect.Remove);
            RemoveSafe("一键上柜台", QuickShelf.Remove);
            RemoveSafe("双击开锁", QuickUnlock.Remove);
            RemoveSafe("柜台溢出", CounterOverflow.Remove);
            RemoveSafe("前台提醒", CounterClutterWarn.Remove);
            RemoveSafe("框选统计", SelectionCount.Remove);
            RemoveSafe("对话加速", DialogueSpeedUp.Remove);
            RemoveSafe("交易结果弹窗", TradeResultPopup.Remove);
            RemoveSafe("需求高亮", DemandHighlight.Remove);
            RemoveSafe("检查收纳", ContrabandSweep.Remove);
            RemoveSafe("水质标签", WaterLabelSync.Remove);
            RemoveSafe("汉化调试面板", DebugPanelZh.Remove);
            RemoveSafe("店内行情", CalendarPriceLine.Remove);
            RemoveSafe("打烊确认", ShutterConfirm.Remove);
            RemoveSafe("摇铃确认", CustomerSkipConfirm.Remove);
            RemoveSafe("到店提示", CustomerAlert.Remove);
            RemoveSafe("顾客对话", CustomerDialog.Remove);
            RemoveSafe("提示框精简", TooltipTextFilter.Remove);
            RemoveSafe("荧光莓品质徽标", BerryBadge.Remove);
            RemoveSafe("物品分类标签", ItemTypeBadge.Remove);

            try { QoLUpdate.Shutdown(); }
            catch (Exception e) { QLog.Warn("停止每帧驱动失败：" + e.Message); }

            Settings = null;
            QLog.Info("已停用");
        }

        private static void ApplySafe(string name, Action apply)
        {
            try
            {
                apply();
            }
            catch (Exception e)
            {
                QLog.Error("[" + name + "] 挂载失败", e);
            }
        }

        private static void RemoveSafe(string name, Action remove)
        {
            try
            {
                remove();
            }
            catch (Exception e)
            {
                QLog.Warn("[" + name + "] 卸载失败：" + e.Message);
            }
        }

        /// <summary>提示框拼完后的最后一步，只有真伪鉴定用得到。</summary>
        private static void OnTooltipLate(RichTextBuilder builder, GameItem item)
        {
            try
            {
                Authenticity.AppendTooltip(builder, item);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Tag + "真伪鉴定提示出错：" + e.Message);
            }
        }
    }
}
