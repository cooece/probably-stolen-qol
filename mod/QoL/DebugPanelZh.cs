using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 游戏 F9 调试面板及相关调试窗口的中文化。
    ///
    /// Harmony prefix 拦截 CustomUIBuilder / CustomUIManager 的文本参数，按字典精确替换。
    /// 分组标题传的是 "&lt;b&gt;Store&lt;/b&gt;" 这种带富文本标签的字符串，Zh() 会剥掉标签再查字典。
    /// 带数字的动态说明文字用几条正则/片段规则处理。
    /// 字典只放调试面板特有的英文，其它界面对不上就原样放行，不会误伤。
    ///
    /// 不再注入任何翻页按钮——F5/F6 与「上一页/下一页」都已移除，面板保持游戏原生结构。
    /// 另外提供一个可自定义的额外开关键（默认 F10），F9 本身照常可用。
    /// </summary>
    internal static class DebugPanelZh
    {
        private const string HarmonyId = "local.qol.debug_zh";

        private static readonly string[] SubWindows =
        {
            "debug_panel_modding", "debug_panel_marketing", "debug_panel_story"
        };

        private static Harmony _harmony;

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.HanifyDebugPanel.Value;
            }
        }

        /// <summary>动态文案里的固定片段（含数字的句子用正则，纯前缀用片段替换）。</summary>
        private static readonly (string From, string To)[] Fragments =
        {
            ("Today's stack: ", "今日顾客队列："),
            ("No identifier matches '", "没有匹配的标识符：'"),
            (" sprites, hover for key, click to copy", " 个贴图，悬停看键名，点击复制"),
            (" customers, hover for details, click portrait to copy id", " 位顾客，悬停看详情，点头像复制 ID"),
            ("(factory failed)", "（生成失败）"),
        };

        private static readonly (Regex Pattern, string Replacement)[] Patterns =
        {
            (new Regex(@"^(\d+) of (\d+) customers, hover for details, click portrait to copy id$"),
                "匹配 $1 / 共 $2 位顾客，悬停看详情，点头像复制 ID"),
            (new Regex(@"^(\d+) of (\d+) sprites, hover for key, click to copy$"),
                "匹配 $1 / 共 $2 个贴图，悬停看键名，点击复制"),
        };

        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // ---------- 窗口标题 ----------
                { "Debug Panel", "调试面板" },
                { "Modding Debug Panel", "模组调试面板" },
                { "Marketing Debug Panel", "营销调试面板（顾客）" },
                { "Story Debug Panel", "剧情调试面板" },
                { "Item Spawner", "物品生成器" },
                { "SpriteDict Viewer", "贴图查看器" },
                { "Customer Dict Viewer", "顾客图鉴" },
                { "search buttons...", "搜索按钮…" },
                { "filter by identifier...", "按标识符筛选…" },
                { "filter by key...", "按键名筛选…" },
                { "filter by id or name...", "按 ID 或名字筛选…" },

                // ---------- 分组标题（被 <b></b> 包裹，Zh 会剥离后查这里） ----------
                { "Store", "商店" },
                { "Command", "指令" },
                { "Spawn", "生成" },
                { "Special", "特殊" },
                { "Modding", "模组" },
                { "Scene", "场景" },
                { "Kits", "套装" },
                { "Items", "物品" },
                { "Customers", "顾客" },
                { "Butcher", "屠夫" },
                { "Lou", "卢" },
                { "Eleanor", "埃莉诺" },
                { "All", "全部" },
                { "Recent", "最近" },

                // ---------- Store ----------
                { "Set Credit to 0", "信用点归零" },
                { "Add 500 Credit", "加 500 信用点" },
                { "Add 10000 Credit", "加 10000 信用点" },
                { "Set All Rep to Max", "全部声望拉满" },
                { "Set All Rep to Min", "全部声望清零" },
                { "Set Security Rep to Min", "安保声望清零" },
                { "Buy Store + Deed (Keep Playing)", "买下店铺与地契（继续游戏）" },
                { "Ending: Instant Buyout (Win)", "结局：立即买断（胜）" },
                { "Ending: Arrested (Loss)", "结局：被捕（败）" },

                // ---------- Command ----------
                { "Toggle Dev Mode", "切换开发者模式" },
                { "Toggle Verbose Log", "详细日志开关" },
                { "Skip Night (Machines Only)", "跳过夜晚（仅机器）" },
                { "Force Clear Inv", "强制清空背包" },
                { "Force Clear Trashcan", "强制清空垃圾桶" },
                { "Open Wild UI", "打开野外界面" },
                { "Queue Basic Gun Order (LL)", "排队：低级枪械订单" },
                { "Queue Mid Gun Order (Merc)", "排队：中级枪械订单" },
                { "Queue High Gun Order (UL)", "排队：高级枪械订单" },

                // ---------- Spawn / Special ----------
                { "Open Debug Spawner", "打开物品生成器" },
                { "Spawn Aquaculture Kit", "生成：水产套装" },
                { "Spawn Hydroponic Kit", "生成：水培套装" },
                { "Spawn Plant DNA Kit", "生成：植物 DNA 套装" },
                { "Spawn Gun Kit", "生成：枪械套装" },
                { "Spawn Rat Ranching Kit", "生成：养鼠套装" },
                { "Open Sprite Viewer", "打开贴图查看器" },
                { "Open Customer Viewer", "打开顾客图鉴" },
                { "Afterhour Events", "夜间事件" },
                { "Modding Panel", "模组面板" },
                { "Legacy Panel", "旧版面板" },
                { "Marketing Panel", "营销面板（顾客在这里）" },
                { "Story Panel", "剧情面板" },
                { "Back to Debug Panel", "返回调试面板" },

                // ---------- Modding ----------
                { "Paper Editor", "纸张编辑器" },
                { "Reload Mod Books", "重载模组书" },
                { "Close Paper Books", "关闭所有纸书" },
                { "Open Mods Folder", "打开模组文件夹" },
                { "Sprite Viewer", "贴图查看器" },
                { "Paper Sprite Picker", "纸张贴图选择器" },

                // ---------- Scene ----------
                { "Toggle Tooltip", "提示框开关" },
                { "Toggle Idle Animation", "待机动画开关" },
                { "Toggle Customer Sprite Swap", "顾客立绘循环开关" },
                { "Toggle Aug Sprite Swap", "改造体立绘循环开关" },
                { "Set Random Day and Credit", "随机日期与资金" },
                { "Setup Marketplace Request", "布置市场委托" },

                // ---------- Story：Butcher / Lou / Eleanor ----------
                { "Toggle Butcher Test Mode", "屠夫测试模式开关" },
                { "Queue The Butcher", "排队：屠夫" },
                { "Toggle Lou Test Mode", "卢测试模式开关" },
                { "Plant Bomb", "放置炸弹" },
                { "Drop Box Of Toys", "投放玩具箱" },
                { "Collect Box Of Toys", "收取玩具箱" },
                { "Disguise Visit", "伪装来访" },
                { "Collect Demand", "收取索取" },
                { "1 Intro", "1 开场" },
                { "2", "2" },
                { "3", "3" },
                { "4", "4" },
                { "5", "5" },
                { "Uncle (Crate)", "叔叔（箱子）" },
                { "Warned (Uncle Reported)", "已警告（叔叔被举报）" },
                { "6 (Plan)", "6（计划）" },
                { "Scavenging Visit", "拾荒来访" },
                { "7 (Data Drive)", "7（数据盘）" },
                { "Thug's Threat", "暴徒威胁" },
                { "8 (Choice)", "8（抉择）" },
                { "9 (Conclusion)", "9（结局）" },
                { "Free Nutrifruit Now", "立刻发放营养果" },
                { "Savings +200", "存款 +200" },
                { "Spawn Crate", "生成箱子" },
                { "Reset Story", "重置剧情" },

                // ---------- Kits / Items ----------
                { "Gun Kit", "枪械套装" },
                { "Farm Kit", "农场套装" },
                { "Plant DNA Kit", "植物 DNA 套装" },
                { "Chemistry Kit", "化学套装" },
                { "Lottery Kit", "彩票套装" },
                { "Cigarette Inspection Kit", "香烟检查套装" },
                { "Aquaculture Kit", "水产套装" },
                { "Add Food Item", "生成：食品" },
                { "Add Medical Item", "生成：医疗品" },
                { "Add Household Item", "生成：日用品" },
                { "Add Material Item", "生成：材料" },
                { "Add Shady Item", "生成：可疑物品包" },
                { "Add Business Card", "生成：名片" },
                { "Add Music Cassettes", "生成：磁带" },
                { "Add Music Cassettes (Album 2)", "生成：磁带（专辑 2）" },
                { "Add Hypertech Item", "生成：高科技物品" },

                // ---------- Customers ----------
                { "Queue Normal Seller", "排队：普通卖家" },
                { "Queue Normal Buyer", "排队：普通买家" },
                { "Queue Security Inspection", "排队：安全检查员" },
                { "Queue Appraisal", "排队：估价顾客" },
                { "Queue Landlord", "排队：地主" },
                { "Queue Unique Character 1 Intro", "排队：剧情人物 1" },
                { "Queue Unique Character 2 Intro", "排队：剧情人物 2" },
                { "Queue Scam Cigarette Seller", "排队：假烟剧情商贩" },
                { "Queue Custom Gun Buyer", "排队：枪械买家" },
                { "Queue next", "排队下一位" },
                { "Apply to customer", "应用到顾客" },

                // ---------- 物品生成器 / 查看器说明 ----------
                { "Click: spawn into the store. Shift+click: x10. Right-click: toggle favorite. Filter updates as you type (2+ characters).",
                  "左键：生成到店内；Shift+左键：一次 10 个；右键：收藏；输入 2 个字以上自动筛选。" },
                { "No item directories are registered in this scene.", "当前场景没有注册物品目录。" },
                { "SpriteDict is not loaded in this scene.", "当前场景没有加载贴图库。" },
                { "Nothing spawned yet. Spawned items show up here, newest first.", "还没有生成过物品，生成过的会按最新排在这里。" },
                { "Stack x10", "一次 10 个" },
            };

        internal static void Apply()
        {
            try
            {
                _harmony = new Harmony(HarmonyId);

                Patch(typeof(CustomUIBuilder), "AddButton", nameof(TextPrefix));
                Patch(typeof(CustomUIBuilder), "AddLabel", nameof(TextPrefix));
                Patch(typeof(CustomUIBuilder), "AddHeader", nameof(TextPrefix));
                Patch(typeof(CustomUIBuilder), "AddWarning", nameof(TextPrefix));
                Patch(typeof(CustomUIBuilder), "AddInput", nameof(PlaceholderPrefix));
                Patch(typeof(CustomUIBuilder), "AddToggle", nameof(LabelPrefix));
                Patch(typeof(CustomUIBuilder), "AddDropdown", nameof(OptionsPrefix));
                Patch(typeof(CustomUIManager), "CreateWindow", nameof(TitlePrefix));

                Debug.Log(ModEntry.Tag + "调试面板汉化已挂载");
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "调试面板汉化挂载失败：" + e);
            }
        }

        internal static void Remove()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
            }
        }

        private static void Patch(Type type, string name, string prefix)
        {
            var method = AccessTools.Method(type, name);
            if (method != null)
            {
                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(DebugPanelZh), prefix));
            }
            else
            {
                Debug.LogWarning(ModEntry.Tag + "汉化：找不到 " + type.Name + "." + name);
            }
        }

        // ---------------------------------------------------------------
        // 自定义快捷键（默认 F10），F9 是游戏自带的
        // ---------------------------------------------------------------

        internal static void Tick()
        {
            QoLSettings settings = ModEntry.Settings;
            if (settings == null || !Enabled)
            {
                return;
            }

            string name = settings.DebugPanelHotkey.Value;
            if (string.IsNullOrEmpty(name) || name == "无")
            {
                return;
            }

            KeyCode key;
            if (!Enum.TryParse(name, out key) || !Input.GetKeyDown(key))
            {
                return;
            }

            try
            {
                CustomUIManager manager = CustomUIManager.Instance;
                if (manager == null)
                {
                    return;
                }

                // 收起子页，避免和主面板叠在一起
                for (int i = 0; i < SubWindows.Length; i++)
                {
                    CustomUIWindow sub = manager.GetWindow(SubWindows[i]);
                    if (sub != null && sub.IsAlive)
                    {
                        sub.Hide();
                    }
                }

                CustomUIDebugPanel.Toggle();
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModEntry.Tag + "调试面板快捷键出错：" + e.Message);
            }
        }

        // ---------------------------------------------------------------
        // 各类文本参数
        // ---------------------------------------------------------------

        private static void TextPrefix(ref string text)
        {
            text = Zh(text);
        }

        private static void PlaceholderPrefix(ref string placeholder)
        {
            placeholder = Zh(placeholder);
        }

        private static void LabelPrefix(ref string label)
        {
            label = Zh(label);
        }

        private static void TitlePrefix(ref string title)
        {
            title = Zh(title);
        }

        /// <summary>下拉选项（例如物品生成器的 "All"）。</summary>
        private static void OptionsPrefix(IList<string> options)
        {
            if (!Enabled || options == null)
            {
                return;
            }

            try
            {
                for (int i = 0; i < options.Count; i++)
                {
                    string chinese;
                    if (options[i] != null && Map.TryGetValue(options[i], out chinese))
                    {
                        options[i] = chinese;
                    }
                }
            }
            catch
            {
                // 只读集合就跳过，不影响其它功能
            }
        }

        private static string Zh(string text)
        {
            if (string.IsNullOrEmpty(text) || !Enabled)
            {
                return text;
            }

            string chinese;
            if (Map.TryGetValue(text, out chinese))
            {
                return chinese;
            }

            // 分组标题是 "<b>Store</b>" 这类带富文本标签的字符串
            if (text.StartsWith("<b>", StringComparison.Ordinal) &&
                text.EndsWith("</b>", StringComparison.Ordinal))
            {
                string inner = text.Substring(3, text.Length - 7);
                if (Map.TryGetValue(inner, out chinese))
                {
                    return "<b>" + chinese + "</b>";
                }
            }

            // 带数字的动态说明
            for (int i = 0; i < Patterns.Length; i++)
            {
                if (Patterns[i].Pattern.IsMatch(text))
                {
                    return Patterns[i].Pattern.Replace(text, Patterns[i].Replacement);
                }
            }

            for (int i = 0; i < Fragments.Length; i++)
            {
                if (text.IndexOf(Fragments[i].From, StringComparison.Ordinal) >= 0)
                {
                    text = text.Replace(Fragments[i].From, Fragments[i].To);
                }
            }

            return text;
        }
    }
}
