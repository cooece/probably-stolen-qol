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

        /// <summary>显示物品描述文字和"双击以打开"这类操作提示。关掉只留数值。</summary>
        internal ConfigEntry<bool> ShowItemDescription { get; }

        /// <summary>用彩色标签代替原版提示框里的 [分类] 文字。</summary>
        internal ConfigEntry<bool> TagsEnabled { get; }

        /// <summary>鼠标停在店内顾客身上时，显示本次接待已经说过的对话。</summary>
        internal ConfigEntry<bool> CustomerHistory { get; }

        /// <summary>通缉犯或改造体顾客到店时弹出提示。</summary>
        internal ConfigEntry<bool> CustomerAlert { get; }

        /// <summary>改造体没击杀就想叫下一位时，先弹一个原版样式的确认框。</summary>
        internal ConfigEntry<bool> ConfirmSkipAug { get; }

        /// <summary>全局对话加速：打字速度提升约5倍，句间停顿缩短70%，按住空格可极速快进跳过。</summary>
        internal ConfigEntry<bool> DialogueSpeedUp { get; }

        /// <summary>柜台上还有顾客没谈完就想打烊时，先问一下。</summary>
        internal ConfigEntry<bool> ConfirmCloseBusy { get; }

        /// <summary>店内那块小显示器上多显示一行今日行情。</summary>
        internal ConfigEntry<bool> ShowPriceTrend { get; }

        /// <summary>真伪鉴定：提示显示结论、谈判时自动揭穿假货。</summary>
        internal ConfigEntry<bool> Authenticity { get; }

        /// <summary>交易成交后弹窗显示声望与金钱增减。</summary>
        internal ConfigEntry<bool> TradeResultPopup { get; }

        /// <summary>估价连续批量提交时，自动跳过中间对话加速（最后一件照常播放）。</summary>
        internal ConfigEntry<bool> AppraisalFastDialogue { get; }

        /// <summary>自动把 5 类单据移入垃圾桶（一个开关统一控制）。</summary>
        internal ConfigEntry<bool> AutoDiscardDocuments { get; }

        /// <summary>需求高亮：彩色框线标出顾客想要的物品。</summary>
        internal ConfigEntry<bool> DemandHighlight { get; }

        /// <summary>容器内水质档位变化后，自动把标签同步为真实水质。</summary>
        internal ConfigEntry<bool> WaterLabelSync { get; }

        /// <summary>道具快捷键：右键道具选“设置快捷键”，按键从所有容器拿起同类道具并连续使用。</summary>
        internal ConfigEntry<bool> ToolHotkeyEnabled { get; }

        /// <summary>框选多个同类物品后双击标签机快捷键，用原版面板批量改标签。</summary>
        internal ConfigEntry<bool> BatchLabelEnabled { get; }

        /// <summary>在物品上按住左键不动即可拉出框选框，满容器也能框选。</summary>
        internal ConfigEntry<bool> BoxSelectLongPress { get; }

        /// <summary>长按触发框选所需的按住时长（毫秒文本）。</summary>
        internal ConfigEntry<string> BoxSelectHoldMs { get; }

        /// <summary>汉化游戏 F9 调试面板，并在面板顶部加前进/后退翻页。</summary>
        internal ConfigEntry<bool> HanifyDebugPanel { get; }

        /// <summary>额外打开调试面板的快捷键，可自定义。</summary>
        internal ConfigEntry<string> DebugPanelHotkey { get; }

        /// <summary>详细日志记录：将详细调试信息与排查日志写入控制台与独立日志文件。</summary>
        internal ConfigEntry<bool> VerboseLogging { get; }

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
                () => "显示描述、厂商与操作提示",
                () => "提示框里那些不参与计算的文字：物品说明、风味文本、物品名下面那行厂商名，以及「双击以打开」这类操作提示。关掉后提示框只保留名称和数值，更省眼睛。");

            // ---------------- 分类标签 ----------------
            // 尺寸（内边距/间距/字号/离顶距离）已定为固定值，写死在 TagStyle 里，不再开放设置。
            TagsEnabled = _config.Define(
                "tag.enabled",
                true,
                "分类标签",
                () => "用彩色标签代替 [分类] 文字",
                () => "把提示框里那行 [日用品][净水用品] 换成提示框上方的彩色小标签。关掉就恢复原版文字。");

            // ---------------- 顾客 ----------------
            CustomerHistory = _config.Define(
                "customer.history",
                true,
                "顾客",
                () => "悬停顾客查看本次对话",
                () => "鼠标停在店内顾客身上时，显示这次接待已经说过的对话。顾客离开后清空。");

            CustomerAlert = _config.Define(
                "customer.alert",
                true,
                "顾客",
                () => "顾客到店提示",
                () => "通缉犯或改造体顾客到店时，在人物旁边显示一行提示：检测到通缉犯到店 / 检测到改造体到店 / 检测到通缉犯·改造体。潜伏型通缉犯不会提示。");

            ConfirmSkipAug = _config.Define(
                "customer.confirm_skip_aug",
                true,
                "顾客",
                () => "未击杀改造体时摇铃先确认",
                () => "柜台站着改造体、你还没开枪就想摇铃叫下一位时，先弹一个「未击杀改造体，是否跳过？」的确认框。选跳过＝直接送走这位顾客，放弃击杀奖励。");

            DialogueSpeedUp = _config.Define(
                "customer.dialogue_speedup",
                true,
                "顾客",
                () => "全局对话加速",
                () => "让所有角色（普通顾客、发明家、商人、小偷、治安官、剧情人物）说话打字速度大幅提升约5倍，句间停顿缩短70%；按住空格键可极速连发快进，遇到选项分支自动停止。");

            // ---------------- 打烊 ----------------
            ConfirmCloseBusy = _config.Define(
                "store.confirm_close_busy",
                true,
                "打烊",
                () => "顾客没谈完时打烊先确认",
                () => "柜台上还有顾客、生意还没谈完就拉闸门时，先弹一个确认框问一声，避免误关店。队列里还有人排队、或柜台这位还没走，都会先确认。");

            // ---------------- 店内日历 ----------------
            ShowPriceTrend = _config.Define(
                "calendar.price_trend",
                true,
                "店内日历",
                () => "显示器上显示今日行情",
                () => "收银台旁那块小显示器的日期下面，多显示一行今天的市场波动：降价绿色、涨价红色。数据取自当前生效的事件（日用品救济行动、建设项目、供水过剩等），和实际结算价一致。");

            // ---------------- 真伪鉴定 ----------------
            Authenticity = _config.Define(
                "trade.authenticity",
                true,
                "交易",
                () => "真伪鉴定（谈判时自动）",
                () => "物品提示里显示真伪结论（真品 / 假货 / 隐藏缺陷 / 水质虚标）和真实价值；顾客把物品放上台面时自动揭穿，不用手动鉴定。估价客户带来的鉴定物品（手、肋排、等离子燃料）也会显示正确价值，方便精准出价。");

            TradeResultPopup = _config.Define(
                "trade.result_popup",
                true,
                "交易",
                () => "交易结果弹窗",
                () => "和顾客买/卖成交后，弹一条提示显示这次加了（或扣了）多少声望、多少现金。声望是实际结算值（已含难度、阵营和天赋倍率），卖货加钱、买货扣钱。");

            AppraisalFastDialogue = _config.Define(
                "trade.appraisal_fast_dialogue",
                true,
                "交易",
                () => "提交下一件前跳过对话（估价加速）",
                () => "连续批量提交估价物品时，自动跳过中间顾客的对话（相当于自动连按空格加速），最后一件物品的对话照常播放。如果想慢慢看每一件估价后的顾客反应，关掉此项即可。");

            // ---------------- 单据 ----------------
            AutoDiscardDocuments = _config.Define(
                "doc.auto_discard",
                true,
                "单据",
                () => "自动丢弃单据",
                () => "鉴定顾客商品后产生的鉴定单、事件回执单、治安回执单、手写便签、探险日志，都会自动移进垃圾桶，不再占用柜台和背包。一个开关统一控制这 5 类。机器说明书和玩家特意放进槽位的便签会保留，不会误丢。");

            // ---------------- 水与容器 ----------------
            WaterLabelSync = _config.Define(
                "water.label_sync",
                true,
                "水与容器",
                () => "水质变化后同步容器标签",
                () => "容器里的水被净化、混合、加注或蒸发后，实际水质档位变了，标签却还停在旧档位；开启后自动把标签更新成真实水质。只有实际水质真的跨档变化时才改，手动用标签机贴的标签、顾客带来的虚标水都不会被动到。");

            // ---------------- 需求高亮 ----------------
            DemandHighlight = _config.Define(
                "highlight.demand",
                true,
                "需求高亮",
                () => "标出顾客想要的物品",
                () => "柜台前顾客想要的物品用彩色框线标出：绿线=想要、琥珀警戒带=想要的赃物、红双线=想要的违禁品、角括号=容器里装着想要的。顾客没来时不显示，几乎不占性能。");

            // ---------------- 道具快捷键 ----------------
            // 注意：绑定数据不走 ModConfig（否则会在设置页显示一个文本框），
            // 由 QuickToolHotkey 自己存到独立文件；内容是 key=value;... 纯文本，
            // 只是文件扩展名叫 .hotkeys.json，并不是 JSON 格式。
            ToolHotkeyEnabled = _config.Define(
                "tool.hotkey_enabled",
                true,
                "道具快捷键",
                () => "道具快捷键",
                () => "右键自己的道具（工具、涡轮增压器等能装备的）选“设置快捷键”，再按一个键或鼠标侧键；以后按这个键就从所有自己的容器里拿起一个同类道具，点目标使用，用完自动换下一个同类的，没有了就停止；右键或 Esc 取消。涡轮增压器只拿已就绪的（高级、超科技优先，一次性的最后用）。");

            BatchLabelEnabled = _config.Define(
                "tool.batch_label",
                true,
                "道具快捷键",
                () => "双击快捷键批量改标签",
                () => "框选多个同类物品（多个芬太片、多瓶水、多个梦尘等）后，快速按两下标签打印机绑定的快捷键，会弹出游戏原版的标签打印机面板；在面板里改任意标签，都会实时同步到框选的其它同类物品。不框选或只选一个时，快捷键仍是原来的单件用法。");

            // ---------------- 便捷框选 ----------------
            BoxSelectLongPress = _config.Define(
                "select.long_press_band",
                true,
                "便捷框选",
                () => "长按左键直接从物品上框选",
                () => "原版只能从容器的空位起拖框选，容器或机器被物品填满后就拉不出框。开启后：在物品上按住左键不动一小会儿，就会自动进入框选状态，松开前拖动即可拉出框选框，不需要按任何键。按下后立刻拖动仍是原来的拿物品 / 双击 / 单选，手感不变。");

            BoxSelectHoldMs = _config.Define(
                "select.hold_ms",
                "180",
                "便捷框选",
                () => "长按触发时长（毫秒）",
                new[] { "120", "180", "250", "350" },
                () => "在物品上按住多久才转成框选。默认 180 毫秒；觉得容易误触发就调大，觉得要等太久就调小。只有按住期间鼠标几乎没动才会触发，一移动就照常拿物品。");

            // ---------------- 开发者 ----------------
            HanifyDebugPanel = _config.Define(
                "dev.hanify_panel",
                true,
                "开发者",
                () => "汉化调试面板（F9）",
                () => "把游戏自带 F9 调试面板的按钮、分组、窗口标题译成中文，并额外提供一个可自定义的开关键（默认 F10）。面板保持游戏原生结构，不改动布局。关掉即还原成英文原版。");

            DebugPanelHotkey = _config.Define(
                "dev.panel_hotkey",
                "F10",
                "开发者",
                () => "调试面板快捷键",
                new[] { "无", "F5", "F6", "F7", "F8", "F10", "F11", "F12" },
                () => "除了游戏自带的 F9，再给调试面板一个可自定义的开关键。默认 F10，选「无」就只用 F9。改完立即生效，不用重启。");

            VerboseLogging = _config.Define(
                "dev.verbose_logging",
                true,
                "开发者",
                () => "详细日志记录",
                () => "开启后，模组会在控制台及 ModConfigs/local.qol.log 中输出详细诊断日志（包括模块初始化、快捷键触发、按键录入、右键菜单构建等）。排查故障时请保持开启，遇到问题只需复制该日志文件。");

            // 把定义（可能带新的默认值）写回文件
            _config.Save();
        }
    }
}
