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
            if (manifest != null && !string.IsNullOrEmpty(manifest.ID))
            {
                ModId = manifest.ID;
            }

            string ver = manifest != null ? manifest.ModVersion : "?";
            QLog.Init();
            QLog.Info("Init，ID=" + ModId + "，版本=" + ver);
        }

        public void OnEnable()
        {
            try
            {
                Settings = new QoLSettings(ModId);
                QLog.DumpEnvironment();
                QoLUpdate.Ensure();

                // 功能一：物品分类彩色标签
                ItemTypeBadge.Apply();

                // 功能二：精简提示框文字（去掉长描述）
                TooltipTextFilter.Apply();

                // 功能三：悬停顾客查看本次对话
                CustomerDialog.Apply();

                // 功能四：通缉犯/改造体顾客到店提示
                CustomerAlert.Apply();

                // 全局对话加速：所有角色说话打字与停顿加速，长按空格极速快进
                DialogueSpeedUp.Apply();

                // 功能五：未击杀改造体时摇铃，先弹确认框
                CustomerSkipConfirm.Apply();

                // 功能六：柜台顾客没谈完时打烊，先弹通用确认框
                ShutterConfirm.Apply();

                // 功能七：店内显示器上追加今日行情（涨红跌绿）
                CalendarPriceLine.Apply();

                // 提示框生成到最后一步时，追加真伪鉴定结论
                ModHook.OnCreateTooltipLate += OnTooltipLate;

                // 交易结果弹窗：成交后显示声望/金钱增减
                TradeResultPopup.Apply();

                // 需求高亮：彩色框线标出顾客想要的物品
                DemandHighlight.Apply();

                // 水质变化后同步容器标签（净化 / 混合 / 加注 / 蒸发后自动改标签）
                WaterLabelSync.Apply();

                // 道具快捷键：右键菜单绑定快捷键 + 跨容器快速拿起连发使用
                QuickToolHotkey.Apply();

                // 便捷框选：满容器也能长按左键从物品上拉出框选框
                QuickBoxSelect.Apply();

                // 开发者工具：汉化 F9 调试面板 + 自定义开关键
                DebugPanelZh.Apply();

                // 荧光莓品质徽标：图标右上角标出酿酒分数（+2/+1/+0/-1）
                BerryBadge.Apply(Settings);

                QLog.Info("已启用：" + Settings.Count + " 项设置，配置文件 " + Settings.FilePath + "，日志路径 " + QLog.LogFilePath);
            }
            catch (Exception e)
            {
                // 模组抛异常不要让它冒出去，否则可能连累游戏主循环。
                QLog.Error("OnEnable 失败", e);
            }
        }

        public void OnDisable()
        {
            ModHook.OnCreateTooltipLate -= OnTooltipLate;
            QuickToolHotkey.Remove();
            QuickBoxSelect.Remove();
            SelectionCount.Remove();
            DialogueSpeedUp.Remove();
            TradeResultPopup.Remove();
            DemandHighlight.Remove();
            WaterLabelSync.Remove();
            DebugPanelZh.Remove();
            CalendarPriceLine.Remove();
            ShutterConfirm.Remove();
            CustomerSkipConfirm.Remove();
            CustomerAlert.Remove();
            CustomerDialog.Remove();
            TooltipTextFilter.Remove();
            ItemTypeBadge.Remove();
            QoLUpdate.Shutdown();
            Settings = null;
            QLog.Info("已停用");
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
