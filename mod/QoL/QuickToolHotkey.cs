using System;
using System.Collections.Generic;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 工具快捷键与道具快速使用管理器：
    /// 1. 右键自己的道具（工具、涡轮增压器等可装备物品）添加“设置快捷键”选项；
    /// 2. 捕获按键或鼠标侧键进行绑定；
    /// 3. 按下快捷键自动从所有容器中拿起同类道具，点击目标使用；
    /// 4. 用完或损坏后自动无缝递补同类道具，全部耗尽自动停止；
    /// 5. 针对涡轮增压器优先选取高级/超科技增压器，一次性增压器最后使用。
    /// </summary>
    internal static class QuickToolHotkey
    {
        private const string OneShotTurbo = "turbo_booster";
        private const string AdvTurbo = "turbo_booster_adv";
        private const string HyperTurbo = "turbo_booster_hypertech";

        /// <summary>双击判定窗口（秒）。只影响「有框选 + 按标签机快捷键」这一种情况。</summary>
        private const float DoubleClickWindow = 0.32f;
        private static string _pendingGroup;
        private static float _pendingDeadline;

        private static bool _active;
        private static string _activeGroup;
        private static GameItem _currentEquipped;
        private static bool _activeIsContainer;
        private static int _activeItemId;

        private static readonly List<GameItem> _matchedItems = new List<GameItem>();
        private static readonly List<TurboCandidate> _turboCandidates = new List<TurboCandidate>();

        private struct TurboCandidate
        {
            public string Id;
            public bool Ready;
            public int Charge;
        }

        // 快捷键捕获状态
        private static string _capturingGroup;
        private static string _capturingName;
        private static int _captureStartFrame = -1;
        private static int _captureEndFrame = -1;

        // 长显提示：非空时每帧刷新 Notify 气泡，保持常显（设置快捷键期间用）。
        private static string _persistentNotice;
        private static float _persistentNoticeTimer;

        internal static bool IsCapturing
        {
            get
            {
                if (_capturingGroup == null)
                {
                    return Time.frameCount == _captureEndFrame;
                }
                return true;
            }
        }

        // 缓存解析后的绑定表
        private static string _cachedRawBindings;
        private static bool _bindingsLoaded;
        private static readonly Dictionary<string, KeyCode> _parsedBindings = new Dictionary<string, KeyCode>();

        private static bool Enabled
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.ToolHotkeyEnabled.Value;
            }
        }

        internal static void Apply()
        {
            ItemContextMenu.Apply();
            ItemContextMenu.RegisterProvider(ProvideContextMenu);
        }

        internal static void Remove()
        {
            BatchLabeler.Reset();
            _pendingGroup = null;
            ItemContextMenu.UnregisterProvider(ProvideContextMenu);
            StopUse();
            EndCapture();
        }

        private static void ProvideContextMenu(GameItem item, List<ItemMenuEntry> entries)
        {
            if (!Enabled || item == null) return;

            // 必须是归属于玩家且可装备选择的道具
            if (!GeneralHelper.IsItemOwned(item) || !item.MaySelectSlotItem())
            {
                return;
            }

            string group = item.identifier;
            string groupName = GetGroupName(group, item);

            // 液体容器按「具体哪一件」绑定，工具按「类型」绑定。
            // 同一个 identifier 的罐子可能分别装水、酒精、药剂，按类型分组会拿错罐。
            bool isLiquidContainer = IsLiquidContainer(item);
            string bindingKey = BindingKeyFor(item);
            KeyCode boundKey = GetBoundKey(bindingKey);
            bool hasKey = boundKey != KeyCode.None;

            // 菜单按最长一项统一撑宽，所以全部用半角括号、去掉「当前」「共…个」等衬字。
            // 例：更改快捷键（当前 1）（仅此容器） → 改快捷键(1,仅此罐)，宽度约减半。
            string keyText = GetKeyDisplayName(boundKey);
            string btnText;
            if (isLiquidContainer)
            {
                btnText = hasKey ? "改快捷键(" + keyText + ",仅此罐)" : "设快捷键(仅此罐)";
            }
            else
            {
                btnText = hasKey ? "改快捷键(" + keyText + ")" : "设置快捷键";
            }

            entries.Add(new ItemMenuEntry
            {
                Key = "qol_toolkey_set",
                ElementName = "QoL_ToolKeySet",
                Text = btnText,
                OnClick = (it) =>
                {
                    StartCapture(bindingKey, groupName);
                }
            });

            if (hasKey)
            {
                entries.Add(new ItemMenuEntry
                {
                    Key = "qol_toolkey_clear",
                    ElementName = "QoL_ToolKeyClear",
                    Text = "取消快捷键",
                    OnClick = (it) =>
                    {
                        SetBoundKey(bindingKey, KeyCode.None);
                        ShowNotice("已取消“" + groupName + "”的快捷键");
                    }
                });
            }

            // 绑定多了容易记混，或者某个按键被别的用途占了：一键全部清空重来。
            // 菜单文字直接带数量，点之前就知道会清掉几个。
            int total = CountBindings();
            if (total > 0)
            {
                entries.Add(new ItemMenuEntry
                {
                    Key = "qol_toolkey_clear_all",
                    ElementName = "QoL_ToolKeyClearAll",
                    Text = "清除所有快捷键(" + total + ")",
                    OnClick = (it) =>
                    {
                        int cleared = ClearAllBindings();
                        ShowNotice(cleared > 0
                            ? "已清除全部 " + cleared + " 个工具快捷键，可重新逐个绑定"
                            : "当前没有已绑定的工具快捷键");
                    }
                });
            }
        }

        internal static void Tick(float dt)
        {
            // 批量标签面板开着时，由它自己轮询同步，这里不抢快捷键
            BatchLabeler.Tick(dt);

            if (!Enabled || !GameMaster.current)
            {
                return;
            }

            // 处理按键录入捕获
            if (_capturingGroup != null)
            {
                PollCapture();
            }

            // 长显提示刷新（设置快捷键期间的提示保持常显）
            RefreshPersistentNotice();

            ItemSelectHandler sel = ItemSelectHandler.current;
            if (sel == null)
            {
                return;
            }

            // 处理连续快速使用
            if (_active)
            {
                TickActiveUse(sel);
            }

            // 监听快捷键触发
            CheckHotkeys(sel);
        }

        private static void CheckHotkeys(ItemSelectHandler sel)
        {
            if (IsCapturing || KeyBindHelper.SuppressHotkeys || BatchLabeler.IsActive)
            {
                return;
            }

            SyncBindings();

            // 上一次按下被挂起等双击，超时了就当普通单击执行
            if (_pendingGroup != null && Time.unscaledTime >= _pendingDeadline)
            {
                string pending = _pendingGroup;
                _pendingGroup = null;
                PressHotkey(sel, GroupOf(pending), ItemIdOf(pending));
            }

            foreach (KeyValuePair<string, KeyCode> kvp in _parsedBindings)
            {
                if (kvp.Value == KeyCode.None) continue;
                if (!Input.GetKeyDown(kvp.Value)) continue;

                string bindingKey = kvp.Key;
                string group = GroupOf(bindingKey);

                // 只有「标签机 + 框选了多个同类可改标签物品」才进入双击判定，
                // 其它所有情况都是零延迟的普通单击，手感不受影响。
                bool batchCapable = BatchLabeler.IsLabelerGroup(group) &&
                                    BatchLabeler.HasBatchSelection();
                if (batchCapable)
                {
                    float now = Time.unscaledTime;
                    if (_pendingGroup == bindingKey && now <= _pendingDeadline)
                    {
                        // 第二下：打开原版标签面板，批量改标签
                        _pendingGroup = null;
                        QLog.DebugLog("[批量标签] 检测到双击标签机快捷键");
                        if (BatchLabeler.TryOpen()) break;
                        // 打开失败就退回普通单击，不让按键石沉大海
                    }
                    else
                    {
                        _pendingGroup = bindingKey;
                        _pendingDeadline = now + DoubleClickWindow;
                        QLog.DebugLog("[批量标签] 第一次按下，等待双击");
                        break;
                    }
                }
                else
                {
                    _pendingGroup = null;
                }

                PressHotkey(sel, group, ItemIdOf(bindingKey));
                break;
            }
        }

        private static void PressHotkey(ItemSelectHandler sel, string group, int itemId)
        {
            if (_active)
            {
                sel.Cancel();
                StopUse();
                QLog.DebugLog("按键取消快捷工具使用: " + group);
                return;
            }

            _activeGroup = group;
            _activeItemId = itemId;
            CollectCandidates();

            // 容器绑定安全校验（核心逻辑，不是兼容代码）：
            // 任何没有 #实例ID 的容器绑定都不可信——同 ID 罐子内容物可能完全不同，
            // 一律拦下，绝不退回「拿同类型第一个」，避免拿错装水/装酒精的罐子。
            if (_activeIsContainer && itemId <= 0)
            {
                QLog.Warn("[工具快捷键] " + group + " 缺少实例ID的容器绑定，已跳过以免拿错内容物");
                ShowNotice("该容器绑定无效，请右键重新设置快捷键");
                StopUse();
                return;
            }

            // 精确绑定的那一件找不到了（卖掉 / 丢弃 / 读档后编号变化）：
            // 明确提示重绑，绝不退回「拿同类型第一个」，避免拿错罐。
            if (itemId > 0 && _matchedItems.Count == 0)
            {
                QLog.Warn("[工具快捷键] 绑定的容器实例 " + group + "#" + itemId + " 已不存在");
                ShowNotice("绑定的容器已不在，请右键重新设置快捷键");
                StopUse();
                return;
            }

            string name = GetGroupName(group, null);
            bool isTurbo = IsTurboIdentifier(group);
            QLog.DebugLog($"触发快捷键拿起: {name} (Group={group}), 候选物品数量={_matchedItems.Count}");

            if (_matchedItems.Count == 0)
            {
                ShowNotice(isTurbo ? "背包及容器内没有涡轮增压器" : "背包及容器内没有" + name);
                return;
            }

            int bestIndex = isTurbo ? PickBestTurbo(_turboCandidates) : 0;
            if (bestIndex < 0)
            {
                ShowNotice("涡轮增压器都在充电，暂时没有就绪的");
                return;
            }

            GameItem targetItem = _matchedItems[bestIndex];
            if (!EquipItem(sel, targetItem))
            {
                ShowNotice("无法拿起" + targetItem.name);
                return;
            }

            _active = true;
            if (isTurbo)
            {
                int totalUses = CountTotalTurboUses(_turboCandidates);
                ShowNotice(targetItem.name + "已就绪（剩余可增压 " + totalUses + " 台），点击目标机器使用");
            }
            else
            {
                ShowNotice("已拿起" + targetItem.name + "（共 " + _matchedItems.Count + " 个），点击目标使用");
            }
        }

        private static void TickActiveUse(ItemSelectHandler sel)
        {
            // 如果手上的物品尚未用完/损坏
            if (!IsItemSpent(_currentEquipped))
            {
                // 如果玩家手动取消拿起，或换了别的东西，则中止连发模式
                GameItem currentHolding = sel.GetEquippedItem();
                if (!IsSameItem(currentHolding, _currentEquipped))
                {
                    StopUse();
                }
                return;
            }

            // 容器是「认准绑定的那一件」，不递补：倒空了就明确停下，
            // 绝不悄悄换成另一个可能装着酒精/药剂的罐子。
            if (_activeItemId > 0)
            {
                StopUse();
                ShowNotice("该容器已不可使用，快捷键已停止");
                return;
            }

            // 当前手上的道具用完或坏了，扫描同类下一件
            CollectCandidates();
            bool isTurbo = IsTurboIdentifier(_activeGroup);
            int nextIndex = isTurbo ? PickBestTurbo(_turboCandidates) : 0;

            if (nextIndex < 0 || _matchedItems.Count == 0)
            {
                sel.Cancel();
                StopUse();
                ShowNotice(isTurbo ? "涡轮增压器已用完" : GetGroupName(_activeGroup, null) + "已全部用完");
            }
            else
            {
                GameItem nextItem = _matchedItems[nextIndex];
                if (!EquipItem(sel, nextItem))
                {
                    StopUse();
                }
                else
                {
                    if (isTurbo)
                    {
                        int totalUses = CountTotalTurboUses(_turboCandidates);
                        ShowNotice("换上下一个" + nextItem.name + "（剩余可增压 " + totalUses + " 台），继续点击目标机器");
                    }
                    else
                    {
                        ShowNotice("换上下一个" + nextItem.name + "（共 " + _matchedItems.Count + " 个），继续点击目标");
                    }
                }
            }
        }

        private static bool EquipItem(ItemSelectHandler sel, GameItem item)
        {
            if (item == null) return false;
            sel.TryEquipItem(item);
            if (!IsSameItem(sel.GetEquippedItem(), item))
            {
                return false;
            }
            _currentEquipped = item;
            return true;
        }

        private static void StopUse()
        {
            _active = false;
            _currentEquipped = null;
            _activeGroup = null;
            _activeIsContainer = false;
            _activeItemId = 0;
        }

        private static bool IsLiquidContainer(GameItem item)
        {
            try
            {
                return item != null && item.IsTag("LIQUID_CONTAINER_TAG");
            }
            catch
            {
                return false;
            }
        }

        // ---------------- 绑定 key：工具按类型，容器精确到实例 ----------------
        // 工具:     screwdriver
        // 容器:     water_pitcher#12345   （# 后面是 GameItem.uniqueId）
        // 旧格式:   water_pitcher         （没有 #，视为已过期，安全拦下不拿罐）

        private static string BindingKeyFor(GameItem item)
        {
            string group = item.identifier;
            if (!IsLiquidContainer(item)) return group;
            try
            {
                return group + "#" + item.uniqueId;
            }
            catch
            {
                return group;
            }
        }

        private static string GroupOf(string bindingKey)
        {
            if (string.IsNullOrEmpty(bindingKey)) return "";
            int hash = bindingKey.IndexOf('#');
            return hash > 0 ? bindingKey.Substring(0, hash) : bindingKey;
        }

        private static int ItemIdOf(string bindingKey)
        {
            if (string.IsNullOrEmpty(bindingKey)) return 0;
            int hash = bindingKey.IndexOf('#');
            if (hash < 0 || hash >= bindingKey.Length - 1) return 0;
            int.TryParse(bindingKey.Substring(hash + 1), out int id);
            return id > 0 ? id : 0;
        }

        private static bool IsSameItem(GameItem a, GameItem b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return ReferenceEquals(a, b);
        }

        private static bool IsItemSpent(GameItem item)
        {
            if (item == null) return true;
            if (item is GameItemElement el && el.IsDestroyed()) return true;
            if (!GeneralHelper.IsItemEffectivelyOwned(item)) return true;
            if (!item.MaySelectSlotItem() || !item.CanSelectSlotItem()) return true;

            // 涡轮增压器特判电量就绪
            if (IsTurboIdentifier(item.identifier) && item.identifier != OneShotTurbo)
            {
                return !MachineTurboBoosterAdv.IsTurboReady(item);
            }

            return false;
        }

        private static void CollectCandidates()
        {
            _matchedItems.Clear();
            _turboCandidates.Clear();
            _activeIsContainer = false;

            PlayerStore store = PlayerStore.Instance;
            if (store == null) return;

            List<GameItem> allOwned = store.FindAllItem();
            if (allOwned == null) return;

            EmporiumEntry emporium = EmporiumEntry.Instance;

            for (int i = 0; i < allOwned.Count; i++)
            {
                GameItem it = allOwned[i];
                if (it == null) continue;

                if (it.identifier != _activeGroup) continue;
                if (!GeneralHelper.IsItemOwned(it)) continue;

                // 精确到实例的绑定：只认那一个物品，找到即可提前收工
                if (_activeItemId > 0 && it.uniqueId != _activeItemId) continue;

                // 只要这组里有液体容器，整组都按「不可安全区分内容物」处理
                if (IsLiquidContainer(it)) _activeIsContainer = true;

                GameInventory parent = it.parentInventory;
                if (parent == null) continue;

                // 排除估价台/柜台上的物品
                if (emporium != null && parent == emporium.invElement) continue;

                // 排除加锁保险柜中的物品
                if (it.IsTag("ITEM_BEHIND_LOCK_TAG")) continue;

                if (it.MaySelectSlotItem() && it.CanSelectSlotItem())
                {
                    bool isReady = true;
                    if (IsTurboIdentifier(it.identifier) && it.identifier != OneShotTurbo)
                    {
                        isReady = MachineTurboBoosterAdv.IsTurboReady(it);
                    }

                    int charge = 0;
                    TagState chargeTag = it.GetTagReadonly("CURRENT_CHARGE_TAG");
                    if (chargeTag != null) charge = chargeTag.valueInt;

                    _matchedItems.Add(it);
                    _turboCandidates.Add(new TurboCandidate
                    {
                        Id = it.identifier,
                        Ready = isReady,
                        Charge = charge
                    });
                }
            }
        }

        private static int PickBestTurbo(List<TurboCandidate> cands)
        {
            int best = -1;
            for (int i = 0; i < cands.Count; i++)
            {
                if (!cands[i].Ready) continue;
                if (best < 0 || GetTurboPriority(cands[i].Id) < GetTurboPriority(cands[best].Id))
                {
                    best = i;
                }
            }
            return best;
        }

        private static int GetTurboPriority(string id)
        {
            // 进阶/超科技优先，普通一次性最后
            if (id == AdvTurbo) return 0;
            if (id == HyperTurbo) return 1;
            return 2;
        }

        private static int CountTotalTurboUses(List<TurboCandidate> cands)
        {
            int total = 0;
            for (int i = 0; i < cands.Count; i++)
            {
                if (!cands[i].Ready) continue;
                if (cands[i].Id == HyperTurbo)
                {
                    total += Math.Max(1, cands[i].Charge / 15);
                }
                else
                {
                    total += 1;
                }
            }
            return total;
        }

        private static bool IsTurboIdentifier(string id)
        {
            return id == OneShotTurbo || id == AdvTurbo || id == HyperTurbo;
        }

        private static string GetGroupName(string group, GameItem sampleItem)
        {
            if (sampleItem != null)
            {
                try
                {
                    string dn = sampleItem.GetDisplayName();
                    if (!string.IsNullOrEmpty(dn)) return dn;
                }
                catch { }
                if (!string.IsNullOrEmpty(sampleItem.name)) return sampleItem.name;
            }

            // 没有物品实例时（如重复绑定提示），根据 identifier 查显示名。
            try
            {
                string byId = GeneralHelper.GetDisplayNameFromID(group);
                if (!string.IsNullOrEmpty(byId)) return byId;
            }
            catch { }

            return group;
        }

        // ---------------- 快捷键配置存储与解析 ----------------
        // 绑定数据存到独立文件（与 ModConfig 同目录），内容格式 key=value;key=value，
        // 是纯文本，只是扩展名叫 .hotkeys.json。这样设置页不会多出一个「绑定数据」文本框。
        private static string _bindingsFilePath;

        private static string BindingsFilePath
        {
            get
            {
                if (_bindingsFilePath == null)
                {
                    QoLSettings s = ModEntry.Settings;
                    if (s != null && !string.IsNullOrEmpty(s.FilePath))
                    {
                        // FilePath 形如 .../ModConfigs/local.qol.json
                        string dir = System.IO.Path.GetDirectoryName(s.FilePath);
                        string baseName = System.IO.Path.GetFileNameWithoutExtension(s.FilePath);
                        _bindingsFilePath = System.IO.Path.Combine(dir, baseName + ".hotkeys.json");
                    }
                    else
                    {
                        _bindingsFilePath = "";
                    }
                }
                return _bindingsFilePath;
            }
        }

        private static void SyncBindings()
        {
            // 绑定数据只在游戏启动时从文件加载一次，之后都用内存缓存。
            // 唯一的变更入口是 SetBoundKey（同步更新内存并写回文件），
            // 所以每帧调用这里不需要反复读磁盘，避免 IO 卡顿。
            if (_bindingsLoaded)
            {
                return;
            }
            _bindingsLoaded = true;

            string path = BindingsFilePath;
            string raw = "";
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                try
                {
                    raw = System.IO.File.ReadAllText(path);
                }
                catch { raw = ""; }
            }
            raw = raw ?? "";

            _cachedRawBindings = raw;

            _parsedBindings.Clear();
            string[] pairs = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq > 0 && eq < pairs[i].Length - 1)
                {
                    string g = pairs[i].Substring(0, eq).Trim();
                    string k = pairs[i].Substring(eq + 1).Trim();
                    if (Enum.TryParse<KeyCode>(k, out KeyCode code))
                    {
                        _parsedBindings[g] = code;
                    }
                }
            }
        }

        private static KeyCode GetBoundKey(string group)
        {
            SyncBindings();
            if (_parsedBindings.TryGetValue(group, out KeyCode code))
            {
                return code;
            }
            return KeyCode.None;
        }

        private static void SetBoundKey(string group, KeyCode code)
        {
            SyncBindings();
            if (code == KeyCode.None)
            {
                _parsedBindings.Remove(group);
            }
            else
            {
                _parsedBindings[group] = code;
            }
            PersistBindings();
        }

        /// <summary>把内存里的绑定表拼成 key=value;... 纯文本，写回独立文件。</summary>
        private static void PersistBindings()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, KeyCode> kvp in _parsedBindings)
            {
                if (kvp.Value != KeyCode.None)
                {
                    parts.Add(kvp.Key + "=" + kvp.Value);
                }
            }

            string serialized = string.Join(";", parts);
            _cachedRawBindings = serialized;

            string path = BindingsFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.WriteAllText(path, serialized);
            }
            catch (Exception ex)
            {
                QLog.Warn("[工具快捷键] 保存绑定失败：" + ex.Message);
            }
        }

        /// <summary>当前生效的快捷键绑定数量。</summary>
        private static int CountBindings()
        {
            SyncBindings();
            int n = 0;
            foreach (KeyValuePair<string, KeyCode> kvp in _parsedBindings)
            {
                if (kvp.Value != KeyCode.None) n++;
            }
            return n;
        }

        /// <summary>清空所有工具快捷键，返回被清除的数量。</summary>
        private static int ClearAllBindings()
        {
            SyncBindings();

            int count = 0;
            List<string> detail = new List<string>();
            foreach (KeyValuePair<string, KeyCode> kvp in _parsedBindings)
            {
                if (kvp.Value == KeyCode.None) continue;
                count++;
                detail.Add(GetGroupName(GroupOf(kvp.Key), null) + "(" + GetKeyDisplayName(kvp.Value) + ")");
            }
            if (count == 0) return 0;

            // 一键清空是破坏性操作：先把旧绑定留一份备份，误点了还能照着恢复
            string backupPath = "";
            try
            {
                string path = BindingsFilePath;
                if (!string.IsNullOrEmpty(path))
                {
                    backupPath = path + ".bak";
                    System.IO.File.WriteAllText(backupPath, _cachedRawBindings ?? "");
                }
            }
            catch (Exception ex)
            {
                QLog.Warn("[工具快捷键] 备份旧绑定失败：" + ex.Message);
            }

            _parsedBindings.Clear();
            PersistBindings();

            // 清完绑定后收掉正在进行的连发 / 捕获，避免残留状态继续动作
            if (_active)
            {
                try
                {
                    ItemSelectHandler sel = ItemSelectHandler.current;
                    if (sel != null) sel.Cancel();
                }
                catch { }
                StopUse();
            }
            _pendingGroup = null;
            if (_capturingGroup != null) EndCapture();

            // 日志留明细 + 备份路径，万一误点也能照着恢复
            QLog.Info("[工具快捷键] 已清除全部 " + count + " 个绑定：" +
                      string.Join("、", detail.ToArray()) +
                      (string.IsNullOrEmpty(backupPath) ? "" : "；旧绑定备份于 " + backupPath));
            return count;
        }

        /// <summary>查找已绑定到指定按键的工具分组（无则返回 null）。</summary>
        private static string FindGroupByKey(KeyCode key)
        {
            SyncBindings();
            foreach (KeyValuePair<string, KeyCode> kvp in _parsedBindings)
            {
                if (kvp.Value == key)
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        // ---------------- 录制捕获按键 ----------------
        private static readonly KeyCode[] CandidateKeys = BuildCaptureKeyList();

        private static KeyCode[] BuildCaptureKeyList()
        {
            List<KeyCode> keys = new List<KeyCode>();
            // F1 - F12
            for (int i = 0; i < 12; i++) keys.Add((KeyCode)((int)KeyCode.F1 + i));
            // A - Z
            for (int i = 0; i < 26; i++) keys.Add((KeyCode)((int)KeyCode.A + i));
            // 0 - 9
            for (int i = 0; i < 10; i++) keys.Add((KeyCode)((int)KeyCode.Alpha0 + i));
            // 鼠标侧键
            keys.Add(KeyCode.Mouse3);
            keys.Add(KeyCode.Mouse4);
            keys.Add(KeyCode.Mouse5);
            keys.Add(KeyCode.Mouse6);
            return keys.ToArray();
        }

        private static void StartCapture(string group, string name)
        {
            _capturingGroup = group;
            _capturingName = name;
            _captureStartFrame = Time.frameCount;
            // 长显提示：一直显示到设置完成或取消
            ShowPersistentNotice("请按下要给“" + name + "”设置的按键或鼠标侧键（按 Esc 取消）");
        }

        private static void EndCapture()
        {
            _capturingGroup = null;
            _capturingName = null;
            _captureEndFrame = Time.frameCount;
            _persistentNotice = null;
        }

        private static void PollCapture()
        {
            if (Time.frameCount == _captureStartFrame) return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                EndCapture();
                ShowNotice("已取消设置快捷键");
                return;
            }

            for (int i = 0; i < CandidateKeys.Length; i++)
            {
                KeyCode key = CandidateKeys[i];
                if (Input.GetKeyDown(key))
                {
                    // 检查该按键是否已被其它工具占用
                    string occupiedBy = FindGroupByKey(key);
                    if (occupiedBy != null && occupiedBy != _capturingGroup)
                    {
                        string occupiedName = GetGroupName(GroupOf(occupiedBy), null);
                        EndCapture();
                        ShowNotice("该按键已绑定" + occupiedName);
                        return;
                    }

                    // 先存下名字：EndCapture 会清空 _capturingName，
                    // 之后再读就会变成空，导致日志和气泡提示都缺工具名。
                    string boundName = _capturingName;
                    bool precise = ItemIdOf(_capturingGroup) > 0;
                    SetBoundKey(_capturingGroup, key);
                    EndCapture();
                    QLog.Info("快捷键设置成功：" + boundName + " -> " + key + (precise ? " (精确实例)" : ""));
                    // 菜单文字精简了，语义放到气泡里补足：容器只对这一件生效、不递补
                    ShowNotice("“" + boundName + "”快捷键已设置为：" + GetKeyDisplayName(key) +
                               (precise ? "（仅对此容器生效）" : ""));
                    break;
                }
            }
        }

        private static string GetKeyDisplayName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.None: return "无";
                case KeyCode.Mouse3: return "鼠标侧键 1";
                case KeyCode.Mouse4: return "鼠标侧键 2";
                case KeyCode.Mouse5: return "鼠标侧键 3";
                case KeyCode.Mouse6: return "鼠标侧键 4";
                default:
                    try
                    {
                        return KeyBindHelper.KeyName(key);
                    }
                    catch
                    {
                        return key.ToString();
                    }
            }
        }

        private static void ShowNotice(string text)
        {
            _persistentNotice = null;
            ReputationUIManager ui = ReputationUIManager.Instance;
            if (ui != null)
            {
                ui.Notify(text);
            }
        }

        // 长显提示：设置快捷键期间一直显示，直到 EndCapture 清除。
        private static void ShowPersistentNotice(string text)
        {
            _persistentNotice = text;
            _persistentNoticeTimer = 0f;
            ReputationUIManager ui = ReputationUIManager.Instance;
            if (ui != null)
            {
                ui.Notify(text);
            }
        }

        // 每帧刷新长显提示（Notify 的气泡会定时消失，靠这里重新弹保持常显）。
        private static void RefreshPersistentNotice()
        {
            if (string.IsNullOrEmpty(_persistentNotice))
            {
                return;
            }
            _persistentNoticeTimer -= Time.unscaledDeltaTime;
            if (_persistentNoticeTimer > 0f)
            {
                return;
            }
            _persistentNoticeTimer = 2.0f; // 每 2 秒重新弹一次，覆盖 Notify 的自动消失
            ReputationUIManager ui = ReputationUIManager.Instance;
            if (ui != null)
            {
                ui.Notify(_persistentNotice);
            }
        }
    }
}
