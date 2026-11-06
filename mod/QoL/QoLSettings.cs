using System;

namespace QoL
{
    /// <summary>
    /// 模组的设置项集合。
    ///
    /// 游戏自带 ModConfig：每个模组的设置会存成一个独立 JSON
    /// （存档同级的 ModConfigs\local.qol.json），玩家在模组列表里点「设置」就能改。
    /// 以后每加一个功能，就在这里加一行 Define，不用自己画界面。
    /// </summary>
    internal sealed class QoLSettings
    {
        private readonly ModConfig _config;

        // 下拉选项与消费方共用同一份常量，避免「界面改了名字、代码还在匹配旧串」。
        internal const string DebugPanelOff = "关闭";
        internal const string DebugPanelF9Only = "仅 F9";

        internal const string BadgeModeOff = "关闭";
        internal const string BadgeModeJiaYi = "甲乙丙丁";
        internal const string BadgeModeGrade = "优良中差";
        internal const string BadgeModeAbcd = "ABCD";

        internal const string BoxSelectOff = "关闭";

        /// <summary>显示物品说明、厂商与操作提示。关掉只留名称和数值。</summary>
        internal ConfigEntry<bool> ShowItemDescription { get; }

        /// <summary>用彩色标签代替提示框里的 [分类] 文字。</summary>
        internal ConfigEntry<bool> TagsEnabled { get; }

        /// <summary>鼠标停在顾客身上时，显示本次接待说过的对话。</summary>
        internal ConfigEntry<bool> CustomerHistory { get; }

        /// <summary>通缉犯或改造体进店时弹提示。</summary>
        internal ConfigEntry<bool> CustomerAlert { get; }

        /// <summary>没击杀改造体就想叫下一位时，先确认。</summary>
        internal ConfigEntry<bool> ConfirmSkipAug { get; }

        /// <summary>对话加速：一个开关同时管全局打字加速与估价时跳过中间对话。</summary>
        internal ConfigEntry<bool> DialogueFast { get; }

        /// <summary>柜台还有顾客没谈完就想打烊时，先确认。</summary>
        internal ConfigEntry<bool> ConfirmCloseBusy { get; }

        /// <summary>店内小显示器上多显示一行今日行情。</summary>
        internal ConfigEntry<bool> ShowPriceTrend { get; }

        /// <summary>真伪鉴定：提示显示结论、谈判时自动揭穿假货。</summary>
        internal ConfigEntry<bool> Authenticity { get; }

        /// <summary>成交后弹窗显示金额与声望变化。</summary>
        internal ConfigEntry<bool> TradeResultPopup { get; }

        /// <summary>自动把 5 类单据移入垃圾桶（一个开关统一控制）。</summary>
        internal ConfigEntry<bool> AutoDiscardDocuments { get; }

        /// <summary>需求高亮：彩色框线标出顾客想要的物品。</summary>
        internal ConfigEntry<bool> DemandHighlight { get; }

        /// <summary>容器内水质跨档变化后，自动把标签同步为真实水质。</summary>
        internal ConfigEntry<bool> WaterLabelSync { get; }

        /// <summary>道具快捷键：右键道具绑定按键，跨容器连续拿起同类道具使用。</summary>
        internal ConfigEntry<bool> ToolHotkeyEnabled { get; }

        /// <summary>框选多个同类物品后双击标签机快捷键，用原版面板批量改标签。</summary>
        internal ConfigEntry<bool> BatchLabelEnabled { get; }

        /// <summary>便捷框选：一个下拉同时管开关与长按时长（含「关闭」）。</summary>
        internal ConfigEntry<string> BoxSelectMode { get; }

        /// <summary>调试面板：一个下拉同时管开关与自定义快捷键。</summary>
        internal ConfigEntry<string> DebugPanelMode { get; }

        /// <summary>详细日志：把排查信息写入独立日志文件。</summary>
        internal ConfigEntry<bool> VerboseLogging { get; }

        /// <summary>荧光莓角标：一个下拉同时管开关与呈现方式（含「关闭」）。</summary>
        internal ConfigEntry<string> BerryDisplayMode { get; }

        internal int Count => _config.Entries.Count;

        internal string FilePath => _config.FilePath;

        internal QoLSettings(string modId)
        {
            // For() 会按 modId 读取已有的 JSON；没有就新建一份。
            _config = ModConfig.For(modId);

            // Define 的参数顺序：键名、默认值、分区标题、显示名、提示文字
            // 显示名和提示文字用委托传入，是为了游戏切换语言时能重新取词。
            ShowItemDescription = _config.Define(
                "tooltip.show_description",
                false,
                "提示框",
                () => "显示物品说明",
                () => "物品提示框里显示说明文字、厂商和操作提示。关掉后只留名称和数值，更清爽。");

            // ---------------- 分类标签 ----------------
            TagsEnabled = _config.Define(
                "tag.enabled",
                true,
                "分类标签",
                () => "彩色分类标签",
                () => "把提示框里的 [日用品] 这类文字换成上方的彩色小标签。关掉就恢复原版文字。");

            // ---------------- 顾客 ----------------
            CustomerHistory = _config.Define(
                "customer.history",
                true,
                "顾客",
                () => "悬停查看说过的话",
                () => "鼠标停在顾客身上，就能看到本次接待已经说过的对话。顾客离开后清空。");

            CustomerAlert = _config.Define(
                "customer.alert",
                true,
                "顾客",
                () => "特殊顾客到店提示",
                () => "通缉犯或改造体进店时，在人物旁边提示一句。潜伏型通缉犯不会提示。");

            ConfirmSkipAug = _config.Define(
                "customer.confirm_skip_aug",
                true,
                "顾客",
                () => "摇铃跳过改造体先确认",
                () => "柜台是改造体、还没击杀就想叫下一位时，先问一声，免得白丢击杀奖励。");

            // 键名沿用旧版 customer.dialogue_speedup，玩家已设的状态不会丢。
            DialogueFast = _config.Define(
                "customer.dialogue_speedup",
                true,
                "顾客",
                () => "对话加速",
                () => "所有对话打字更快，按住空格可快进；连续估价时自动跳过中间对话。关掉就恢复原版节奏。");

            // ---------------- 打烊 ----------------
            ConfirmCloseBusy = _config.Define(
                "store.confirm_close_busy",
                true,
                "打烊",
                () => "没谈完时打烊先确认",
                () => "柜台还有顾客就想拉闸门时，先确认一下，防止误关店。");

            // ---------------- 店内日历 ----------------
            ShowPriceTrend = _config.Define(
                "calendar.price_trend",
                true,
                "店内日历",
                () => "显示今日行情",
                () => "收银台小显示器上多加一行今日涨跌：降价绿、涨价红。");

            // ---------------- 交易 ----------------
            Authenticity = _config.Define(
                "trade.authenticity",
                true,
                "交易",
                () => "自动真伪鉴定",
                () => "物品提示里直接显示真假和真实价值；顾客放上假货时自动揭穿，不用手动鉴定。");

            TradeResultPopup = _config.Define(
                "trade.result_popup",
                true,
                "交易",
                () => "交易结果弹窗",
                () => "买卖成交后弹一条提示，显示到账金额和声望变化。");

            // ---------------- 单据 ----------------
            AutoDiscardDocuments = _config.Define(
                "doc.auto_discard",
                true,
                "单据",
                () => "自动丢弃单据",
                () => "鉴定单、回执、便签这些废纸自动进垃圾桶，不占柜台和背包。说明书和特意放好的便签会保留。");

            // ---------------- 水与容器 ----------------
            WaterLabelSync = _config.Define(
                "water.label_sync",
                true,
                "水与容器",
                () => "水质变了自动改标签",
                () => "水被净化或混合后水质跨档了，标签自动跟着更新；手动贴的标签不会动。");

            // ---------------- 需求高亮 ----------------
            DemandHighlight = _config.Define(
                "highlight.demand",
                true,
                "需求高亮",
                () => "标出顾客想要的物品",
                () => "顾客想要的物品加彩色框：绿＝想要、黄＝赃物、红＝违禁品。顾客走了就不显示。");

            // ---------------- 道具快捷键 ----------------
            // 绑定数据不走 ModConfig（否则会在设置页显示一个文本框），
            // 由 QuickToolHotkey 自己存到独立文件；内容是 key=value;... 纯文本，
            // 只是文件扩展名叫 .hotkeys.json，并不是 JSON 格式。
            ToolHotkeyEnabled = _config.Define(
                "tool.hotkey_enabled",
                true,
                "道具快捷键",
                () => "道具快捷键",
                () => "右键自己的道具选「设置快捷键」，之后按这个键就从各容器里连续拿起同类道具使用；右键或 Esc 取消。");

            BatchLabelEnabled = _config.Define(
                "tool.batch_label",
                true,
                "道具快捷键",
                () => "双击快捷键批量改标签",
                () => "框选多个同类物品后，连按两下标签机快捷键，改一个标签就同步给全部。不框选时仍是单件用法。");

            // ---------------- 便捷框选 ----------------
            // 键名沿用旧版 select.hold_ms（旧值是 "180" 这类数字），合并后的下拉
            // 仍包含这些数字，玩家已选的时长不会丢；新增「关闭」代替原来的独立开关。
            BoxSelectMode = _config.Define(
                "select.hold_ms",
                "180",
                "便捷框选",
                () => "长按触发时长（毫秒）",
                new[] { BoxSelectOff, "120", "180", "250", "350" },
                () => "在物品上按住左键不动，到时间就能拉出框选框，容器装满也一样。数字越小越灵敏、越大越不容易误触；选「关闭」则不启用。");

            // ---------------- 开发者 ----------------
            // 键名沿用旧版 dev.panel_hotkey，玩家已经选过的按键不会丢。
            DebugPanelMode = _config.Define(
                "dev.panel_hotkey",
                "F10",
                "开发者",
                () => "汉化调试面板",
                new[] { DebugPanelOff, DebugPanelF9Only, "F5", "F6", "F7", "F8", "F10", "F11", "F12" },
                () => "一个设置管全部：关闭＝英文原版；仅 F9＝只汉化；选某个 F 键＝汉化并用它开关面板。改完立即生效。");

            VerboseLogging = _config.Define(
                "dev.verbose_logging",
                true,
                "开发者",
                () => "详细日志",
                () => "把详细排查日志写到 ModConfigs/local.qol.log。出问题时复制这个文件就行。");

            // ---------------- 荧光莓 ----------------
            // 键名沿用旧版 berry.display_mode，玩家已选的写法不会丢。
            BerryDisplayMode = _config.Define(
                "berry.display_mode",
                BadgeModeJiaYi,
                "荧光莓",
                () => "角标呈现方式",
                new[] { BadgeModeJiaYi, BadgeModeGrade, BadgeModeAbcd, BadgeModeOff },
                () => "默认开启。荧光莓图标右上角显示品质角标，不用放大镜：甲乙丙丁＝甲最好、丁发霉；优良中差更直白；ABCD 是字母序号。选关闭则不显示。");

            // 把定义（可能带新的默认值）写回文件
            _config.Save();
        }
    }
}
