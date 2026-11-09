using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QoL
{
    /// <summary>
    /// 酿酒工坊：热键开面板、按选定的搭配自动装料并启动发酵。
    ///
    /// 装料走游戏公开接口：
    ///   装果实/酵母  → GraphUtils.TryAcceptOnce(瓶, 物, 1)，装完核对瓶里的子物品数
    ///   倒水         → WaterHelper.TransferLiquid(水源, 瓶)，核对瓶里 ml 数
    ///   启动发酵     → WineHelper.TryStartFermentation(瓶)，核对 WINE_CONTAINER_STATE == 1
    /// 每一步都以「真实状态变了」为成功判据，不看返回值的意思。
    ///
    /// 唯一的 Harmony 补丁在设置面板：把热键那行输入框改成「点一下录按键」
    /// （和一键上柜同一套做法，见 PatchSettingsInput）。
    /// </summary>
    internal static class Brewing
    {
        internal const string Off = "关闭";

        /// <summary>「酿 N / 全酿 N」时每瓶之间的间隔：一瓶要重扫一次全店库存，连着做 30 瓶会卡。</summary>
        private const float BatchInterval = 0.02f;

        private static bool _applied;
        private static bool _dirty;
        private static float _nextBuild;

        // 「全酿 N」的分帧队列
        private static BrewTierRow _batchRow;
        private static string _batchName;
        private static int _batchLeft;
        private static int _batchDone;
        private static float _batchNext;
        private static bool _batchAnnounce;

        private static string _keyRaw;
        private static KeyCode _key;
        private static bool _keyReady;

        // 设置面板「点一下录按键」
        private const string HarmonyId = "local.qol.brew";
        private const string PanelKeyId = "brew.panel_key";

        private static Harmony _harmony;
        private static bool _capturing;
        private static TMP_InputField _captureField;
        private static int _captureFrame;
        private static KeyCode[] _keyList;

        /// <summary>正在等玩家按键（别的功能要避开）。</summary>
        internal static bool IsCapturing { get { return _capturing; } }

        // ---------------------------------------------------------------
        // 挂载 / 卸载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;
            _dirty = false;
            PatchSettingsInput();
            QLog.Info("[酿酒] 工坊已挂载（热键见设置项「酿酒工坊」）");
        }

        internal static void Remove()
        {
            _applied = false;
            _dirty = false;
            _keyReady = false;
            _keyRaw = null;
            ResetBatch();
            UnpatchSettingsInput();

            try { BrewWindow.Close(); }
            catch (Exception e) { QLog.Warn("[酿酒] 关窗失败：" + e.Message); }
        }

        // ---------------------------------------------------------------
        // 每帧
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!_applied) return;

            // 录制按键时优先吃掉输入，不响应热键，也不推批次
            if (_capturing)
            {
                PollCapture();
                return;
            }

            // 「全酿 N」排队中：每帧酿一瓶，别一口气做三十瓶把画面卡住
            StepBatch();

            // 界面重建推迟到下一帧：在按钮自己的回调里销毁它所在的窗口容易出事
            if (_dirty && Time.unscaledTime >= _nextBuild)
            {
                _dirty = false;
                BrewWindow.Rebuild();
            }

            // 离开店铺（回菜单 / 出门 / 换场景）就别把面板留在屏幕上
            if (BrewWindow.IsOpen && !InStore())
            {
                BrewWindow.Close();
                return;
            }

            KeyCode key = CurrentKey;
            if (key == KeyCode.None) return;
            if (!KeyDown(key)) return;
            if (Blocked()) return;

            BrewWindow.Toggle();
        }

        /// <summary>只有在店里才有物品树可读；菜单和读档阶段 PlayerStore 会抛异常。</summary>
        private static bool InStore()
        {
            try
            {
                if (PlayerStore.Instance == null) return false;
                return PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE;
            }
            catch
            {
                return false;
            }
        }

        private static void MarkDirty(float delay)
        {
            _dirty = true;
            _nextBuild = Time.unscaledTime + delay;
        }

        private static KeyCode CurrentKey
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                string raw = s != null && s.BrewPanelKey != null ? s.BrewPanelKey.Value : Off;

                if (!_keyReady || !string.Equals(raw, _keyRaw, StringComparison.Ordinal))
                {
                    _keyRaw = raw;
                    _key = string.Equals(raw, Off, StringComparison.Ordinal) ? KeyCode.None : QuickShelf.ParseKey(raw);
                    _keyReady = true;
                }
                return _key;
            }
        }

        private static bool KeyDown(KeyCode key)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) return Input.GetMouseButtonDown(key - KeyCode.Mouse0);
            return Input.GetKeyDown(key);
        }

        /// <summary>界面上显示的当前热键名（null 表示功能关着）。</summary>
        internal static string HotkeyName
        {
            get
            {
                KeyCode key = CurrentKey;
                if (key == KeyCode.None) return null;
                try { return QuickShelf.KeyDisplayName(key); }
                catch { return key.ToString(); }
            }
        }

        // ---------------------------------------------------------------
        // 设置面板「点一下录按键」
        //
        // 和一键上柜同一套路：游戏对 string 配置只会生成普通输入框，这里补一个
        // Harmony postfix，认出自己那一行后把输入框设成只读、点一下就进捕获。
        // 配置里存的是可读名（「鼠标中键」「F3」），解析走 QuickShelf.ParseKey。
        // ---------------------------------------------------------------

        private static void PatchSettingsInput()
        {
            try
            {
                MethodInfo addInput = AccessTools.Method(typeof(CustomUIBuilder), "AddInput",
                    new[] { typeof(string), typeof(string), typeof(Action<string>), typeof(string), typeof(Action<string>) });
                if (addInput == null)
                {
                    QLog.Warn("[酿酒] 找不到 CustomUIBuilder.AddInput，设置面板里只能手动填按键名");
                    return;
                }

                if (_harmony == null) _harmony = new Harmony(HarmonyId);
                _harmony.Patch(addInput, postfix: new HarmonyMethod(typeof(Brewing), nameof(AddInputPostfix)));
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 挂设置面板补丁失败：" + e.Message);
            }
        }

        private static void UnpatchSettingsInput()
        {
            try
            {
                if (_harmony != null) _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 卸下设置面板补丁失败：" + e.Message);
            }
            _harmony = null;
            _capturing = false;
            _captureField = null;
        }

        /// <summary>AddInput 执行完，__instance.Last 是刚造好的那一行；__args[3] 是配置键名。</summary>
        private static void AddInputPostfix(CustomUIBuilder __instance, object[] __args)
        {
            if (__args == null || __args.Length < 4) return;
            if ((__args[3] as string) != PanelKeyId) return;

            try
            {
                CustomUIElement element = __instance != null ? __instance.Last : null;
                if (element == null || element.input == null) return;
                AttachCapture(element.input);
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 改造改键框失败：" + e.Message);
            }
        }

        private static void AttachCapture(TMP_InputField input)
        {
            if (input == null) return;

            // 旧配置里可能是 KeyCode 名或别的写法，统一刷成可读名
            try
            {
                string shown = KeyNameOf(ParseKeyRaw(input.text));
                if (shown != null && input.text != shown) input.SetTextWithoutNotify(shown);
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 刷新改键框文字失败：" + e);
            }

            try
            {
                input.onSelect.AddListener(delegate { BeginCapture(input); });
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 改键框接管失败，只能手动填键名：" + e);
                return;
            }

            try { input.readOnly = true; }
            catch (Exception e) { QLog.Warn("[酿酒] 设只读失败：" + e); }

            QLog.Info("[酿酒] 设置面板的改键框已接管：点一下就等按键");
        }

        private static void BeginCapture(TMP_InputField field)
        {
            _capturing = true;
            _captureField = field;
            _captureFrame = Time.frameCount;
            Notify("请按下要用的按键（右键或 Esc 取消）");
        }

        private static void PollCapture()
        {
            if (Time.frameCount <= _captureFrame + 1) return;   // 跳过触发捕获的那一下点击

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                CancelCapture();
                Notify("已取消");
                return;
            }

            // 退格 / 删除 = 关掉这个热键
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                BindKey(KeyCode.None);
                return;
            }

            if (!Input.anyKeyDown) return;

            // 鼠标键：右键留给「取消」
            for (int i = 0; i <= 6; i++)
            {
                if (i == 1) continue;
                if (Input.GetMouseButtonDown(i))
                {
                    BindKey((KeyCode)((int)KeyCode.Mouse0 + i));
                    return;
                }
            }

            if (_keyList == null) _keyList = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            for (int k = 0; k < _keyList.Length; k++)
            {
                KeyCode code = _keyList[k];
                if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6) continue;
                if (code == KeyCode.Escape || code == KeyCode.None) continue;
                if (Input.GetKeyDown(code))
                {
                    BindKey(code);
                    return;
                }
            }
        }

        private static void CancelCapture()
        {
            _capturing = false;
            _captureField = null;
            ClearSelection();
        }

        private static void BindKey(KeyCode code)
        {
            _capturing = false;

            string name = KeyNameOf(code);

            QoLSettings s = ModEntry.Settings;
            if (s == null || s.BrewPanelKey == null)
            {
                _captureField = null;
                ClearSelection();
                return;
            }

            s.BrewPanelKey.Set(name);

            if (_captureField != null)
            {
                _captureField.SetTextWithoutNotify(name);
                _captureField = null;
            }

            ClearSelection();
            Notify(code == KeyCode.None ? "已关闭酿酒面板热键" : "酿酒面板热键已设为 " + name);
            QLog.Info("[酿酒] 热键改为 " + name);
        }

        private static KeyCode ParseKeyRaw(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return KeyCode.None;
            if (raw.Trim() == Off || raw.Trim() == QuickShelf.Disabled || raw.Trim() == "无" || raw.Trim() == "None")
                return KeyCode.None;
            return QuickShelf.ParseKey(raw);
        }

        private static string KeyNameOf(KeyCode code)
        {
            try { return QuickShelf.KeyDisplayName(code); }
            catch { return code == KeyCode.None ? Off : code.ToString(); }
        }

        /// <summary>把焦点从输入框移开，避免面板里继续吃键盘。</summary>
        private static void ClearSelection()
        {
            try
            {
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            }
            catch { }
        }

        /// <summary>会抢键或不能开窗的时机。</summary>
        private static bool Blocked()
        {
            try
            {
                if (StoreUIManager.IsAnyTyping()) return true;
                if (KeyBindHelper.SuppressHotkeys) return true;
            }
            catch (Exception e)
            {
                QLog.Warn("[酿酒] 读输入状态失败：" + e.GetType().Name);
                return true;
            }

            try
            {
                if (QuickShelf.IsCapturing || QuickToolHotkey.IsCapturing) return true;
                if (_capturing) return true;
            }
            catch { }

            return !InStore();
        }

        // ---------------------------------------------------------------
        // 对外操作
        // ---------------------------------------------------------------

        /// <summary>酿某一档：酿界面上选好的瓶数。</summary>
        internal static void BrewTier(BrewTierRow row)
        {
            int want = row == null ? 0 : Math.Max(0, row.Want);
            if (row == null || row.Sample == null || want <= 0)
            {
                Notify("这一档还没选瓶数，或者材料不够");
                return;
            }

            StartBatch(row, want, false);
        }

        /// <summary>全酿这一档：把材料能做的最大瓶数都酿了。</summary>
        internal static void BrewTierAll(BrewTierRow row)
        {
            if (row == null || row.Sample == null || row.Max <= 0)
            {
                Notify("这一档现在做不了");
                return;
            }

            StartBatch(row, row.Max, true);
        }

        /// <summary>开一批：一瓶一瓶地做（每瓶要重扫一次库存，一口气做三十瓶会卡）。</summary>
        private static void StartBatch(BrewTierRow row, int count, bool announce)
        {
            if (BatchBusy()) return;

            _batchRow = row;
            _batchName = row.Name;
            _batchLeft = count;
            _batchDone = 0;
            _batchNext = 0f;
            _batchAnnounce = announce || count > 8;

            if (_batchAnnounce) Notify("开始酿「" + row.Name + "」" + count + " 瓶…");
            QLog.Info("[酿酒] 开始酿：" + row.Name + " 目标 " + count + " 瓶");
        }

        private static bool BatchBusy()
        {
            if (_batchLeft <= 0) return false;
            Notify("上一批还在酿，等它酿完再来");
            return true;
        }

        private static void StepBatch()
        {
            if (_batchLeft <= 0) return;

            float now = Time.unscaledTime;
            if (now < _batchNext) return;

            string why;
            if (!BrewOne(_batchRow.Sample, BrewScan.Scan(), out why))
            {
                int done = _batchDone;
                string name = _batchName;
                ResetBatch();

                if (done > 0) Notify("已酿 " + done + " 瓶，" + why);
                else Notify("没酿成：" + why);
                QLog.Info("[酿酒] 批次中断：" + name + " 已酿 " + done + " 瓶，" + why);
                MarkDirty(0.3f);
                return;
            }

            _batchDone++;
            _batchLeft--;
            _batchNext = now + BatchInterval;

            if (_batchLeft > 0) return;

            int all = _batchDone;
            string done2 = _batchName;
            bool loud = _batchAnnounce;
            ResetBatch();

            if (loud) Notify("已下料 " + all + " 瓶「" + done2 + "」");
            QLog.Info("[酿酒] 批次完成：" + done2 + " " + all + " 瓶");
            MarkDirty(0.35f);
        }

        private static void ResetBatch()
        {
            _batchRow = null;
            _batchName = null;
            _batchLeft = 0;
            _batchDone = 0;
            _batchNext = 0f;
            _batchAnnounce = false;
        }

        /// <summary>重新扫描：只重画界面。</summary>
        internal static void Rescan()
        {
            MarkDirty(0.05f);
        }

        // ---------------------------------------------------------------
        // 酿一瓶
        // ---------------------------------------------------------------

        private static bool BrewOne(BrewCombo combo, BrewStock stock, out string why)
        {
            why = string.Empty;
            if (combo == null) { why = "没有搭配"; return false; }
            if (stock == null) { why = "没读到库存"; return false; }

            GameItem bottle = FirstBottle(stock);
            if (bottle == null) { why = "没有空酒瓶"; return false; }

            int tier = combo.WaterTier >= 0 ? combo.WaterTier : BestWaterTier(stock);
            GameItem water = tier >= 0 ? PickWaterItem(stock, tier) : null;
            if (water == null)
            {
                why = "找不到" + (tier >= 0 ? BrewScan.TierName(tier) : "合适的水") + "（要 ≥749ml）";
                return false;
            }

            GameItem yeast = PickYeastItem(stock, combo.YeastId, combo.Counterfeit);
            if (yeast == null)
            {
                why = combo.Counterfeit ? "没有合成酵母（走私酒必须用它）" : "没有可用的酵母";
                return false;
            }

            List<GameItem> berries = new List<GameItem>();
            if (!combo.Counterfeit)
            {
                int[] used = new int[4];
                for (int i = 0; i < combo.Grades.Count; i++)
                {
                    int grade = combo.Grades[i];
                    GameItem berry = TakeBerry(stock, grade, used);
                    if (berry == null)
                    {
                        why = "荧光莓不够（缺" + BerryGrade.Label[grade] + "）";
                        return false;
                    }
                    berries.Add(berry);
                }
            }

            string how;
            for (int i = 0; i < berries.Count; i++)
            {
                if (LoadInto(bottle, berries[i], out how)) continue;
                why = "第 " + (i + 1) + " 颗果放不进瓶：" + how;
                return false;
            }

            if (!PourWater(bottle, water, out how))
            {
                why = "水倒不进瓶：" + how;
                return false;
            }

            if (!LoadInto(bottle, yeast, out how))
            {
                why = "酵母放不进瓶：" + how;
                return false;
            }

            bool started = false;
            try { started = WineHelper.TryStartFermentation(bottle); }
            catch (Exception e) { why = "启动发酵出错：" + e.GetType().Name; return false; }

            int state = BrewScan.TagIntOn(bottle, "WINE_CONTAINER_STATE");
            if (!started && state != 1)
            {
                why = "发酵没启动（料可能已经在瓶里）";
                return false;
            }

            int score = BrewScan.TagIntOn(bottle, "WINE_SCORE_INT");
            QLog.Info("[酿酒] 开瓶：" + (combo.Counterfeit ? "走私酒" : WineGrade.NameOf(score)) +
                      " " + score + " 分 · 果实=" + combo.BerryText + " · 水=" + BrewScan.TierName(tier) +
                      " · 酵母=" + BrewScan.Display(yeast));
            return true;
        }

        private static GameItem FirstBottle(BrewStock stock)
        {
            for (int i = 0; i < stock.EmptyBottles.Count; i++)
            {
                if (BrewScan.IsBottleEmpty(stock.EmptyBottles[i])) return stock.EmptyBottles[i];
            }
            return null;
        }

        /// <summary>挑一颗指定品相的果子；used 记录这次已经用掉的颗数，避免同一颗被选两次。</summary>
        private static GameItem TakeBerry(BrewStock stock, int grade, int[] used)
        {
            BrewBerry stack = stock.Berry(grade);
            if (stack == null) return null;

            int index = used[grade];
            if (index >= stack.Items.Count) return null;

            used[grade] = index + 1;
            return stack.Items[index];
        }

        /// <summary>能用的水质里最好的那一档（0 最纯）。</summary>
        internal static int BestWaterTier(BrewStock stock)
        {
            for (int tier = 0; tier <= 5; tier++)
            {
                for (int i = 0; i < stock.Waters.Count; i++)
                {
                    if (stock.Waters[i].Tier == tier && stock.Waters[i].Ml >= BrewScan.WaterPerBottle) return tier;
                }
            }
            return -1;
        }

        private static GameItem PickWaterItem(BrewStock stock, int tier)
        {
            GameItem found = null;
            int best = 0;
            for (int i = 0; i < stock.Waters.Count; i++)
            {
                BrewWater w = stock.Waters[i];
                if (w.Tier != tier || w.Ml < BrewScan.WaterPerBottle) continue;
                if (found == null || w.Ml > best) { found = w.Item; best = w.Ml; }
            }
            return found;
        }

        private static GameItem PickYeastItem(BrewStock stock, string id, bool synthetic)
        {
            if (synthetic)
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Synthetic && y.Items.Count > 0) return y.Items[0];
                }
                return null;
            }

            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Id == id && y.Items.Count > 0 && !y.Synthetic) return y.Items[0];
                }
            }

            string[] order = { "wine_yeast_infinite", "wine_superyeast", "wine_yeast" };
            for (int k = 0; k < order.Length; k++)
            {
                for (int i = 0; i < stock.Yeasts.Count; i++)
                {
                    BrewYeast y = stock.Yeasts[i];
                    if (y.Id == order[k] && y.Items.Count > 0) return y.Items[0];
                }
            }

            for (int i = 0; i < stock.Yeasts.Count; i++)
            {
                BrewYeast y = stock.Yeasts[i];
                if (!y.Synthetic && y.Items.Count > 0) return y.Items[0];
            }
            return null;
        }

        /// <summary>把一件东西塞进瓶子。三个候选路径，每一步都用「瓶子真的变了」验证。</summary>
        private static bool LoadInto(GameItem bottle, GameItem thing, out string how)
        {
            how = "放不进去";

            int before = BrewScan.ChildCount(bottle);
            string brief = Brief(bottle);

            GraphNodeStorage node = bottle as GraphNodeStorage;
            if (node != null)
            {
                try { GraphUtils.TryAcceptOnce(node, thing, 1); } catch { }
                if (Changed(bottle, before, brief)) { how = "accept-once"; return true; }

                try { GraphUtils.TryAccept(node, thing); } catch { }
                if (Changed(bottle, before, brief)) { how = "accept"; return true; }
            }

            GameInventory inner = InnerInventory(bottle);
            if (inner != null)
            {
                GraphNodeStorage innerNode = inner as GraphNodeStorage;
                if (innerNode != null)
                {
                    try { GraphUtils.TryAcceptOnce(innerNode, thing, 1); } catch { }
                    if (Changed(bottle, before, brief)) { how = "accept-inner"; return true; }
                }
            }

            return false;
        }

        private static bool Changed(GameItem bottle, int childBefore, string briefBefore)
        {
            if (BrewScan.ChildCount(bottle) != childBefore) return true;
            return !string.Equals(Brief(bottle), briefBefore, StringComparison.Ordinal);
        }

        private static string Brief(GameItem bottle)
        {
            return BrewScan.Ml(bottle) + "/" + BrewScan.ChildCount(bottle) + "/" +
                   BrewScan.TagIntOn(bottle, "WINE_CONTAINER_STATE");
        }

        private static GameInventory InnerInventory(GameItem item)
        {
            try
            {
                List<GraphNodeStorage> children = item.children;
                if (children == null) return null;
                for (int i = 0; i < children.Count; i++)
                {
                    GameInventory inv = children[i] as GameInventory;
                    if (inv != null) return inv;
                }
            }
            catch { }
            return null;
        }

        /// <summary>倒水：一次 750ml。做完核对瓶里的毫升数真的够一瓶。</summary>
        private static bool PourWater(GameItem bottle, GameItem water, out string how)
        {
            how = "没倒进去";

            try { WaterHelper.TransferLiquid(water, bottle); }
            catch (Exception e)
            {
                how = "接口出错 " + e.GetType().Name;
                return false;
            }

            if (BrewScan.Ml(bottle) >= BrewScan.WaterPerBottle) return true;

            // 极少数瓶子钩子没初始化，补一次再倒
            try
            {
                WineHelper.InitWineBottle(bottle);
                WaterHelper.TransferLiquid(water, bottle);
            }
            catch { }

            if (BrewScan.Ml(bottle) >= BrewScan.WaterPerBottle) { how = "初始化后补倒"; return true; }

            how = "瓶里只有 " + BrewScan.Ml(bottle) + "ml";
            return false;
        }

        internal static void Notify(string msg)
        {
            try
            {
                ReputationUIManager ui = ReputationUIManager.Instance;
                if (ui != null) ui.Notify(msg);
            }
            catch { }
        }
    }
}
