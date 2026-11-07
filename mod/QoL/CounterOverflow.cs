using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 柜台溢出：原版在「外柜台摆满」时会把后面本可购买的商品直接丢掉 ——
    /// 物品版走 <c>GraphUtils.DestroyIfUnplaced(..., "front counter full")</c> 销毁，
    /// 字符串版连实例都不创建（只记一笔 unplacedCounts 就跳过）。
    ///
    /// 这里做两件事，都在后台，不开设置项：
    ///   1. 柜台满时把要丢的货先收进一个私有暂存网格，不销毁；
    ///   2. 柜台一有空位就自动把暂存货搬回柜台，让顾客还能看到、还能买。
    ///
    /// 不重建物品：救回来的是原来的那一件，品质、违禁、偷窃、含水全都保留。
    /// 顾客离开后暂存区里还没补上的货按原版规则清掉（它们本来就没摆上柜台）。
    /// </summary>
    internal static class CounterOverflow
    {
        private const string HarmonyId = "local.qol.counter_overflow";

        /// <summary>只接管原版标记为「外柜台满」的丢弃，其他场景的销毁一概不碰。</summary>
        private const string ContextMarker = "front counter full";

        /// <summary>暂存区尺寸：够放一整批客人的货。</summary>
        private const int StashSize = 20;

        /// <summary>补位节流（秒）：别每帧都尝试搬，柜台上也可能正被玩家摆东西。</summary>
        private const float FillInterval = 0.5f;

        /// <summary>顾客离开后多久清空暂存（秒）：留点余量，避免到店瞬间误判。</summary>
        private const float IdleClearDelay = 6f;

        private static Harmony _harmony;

        private static readonly List<GameItem> _parked = new List<GameItem>();
        private static GameGridInventory _stash;

        private static float _nextFill;
        private static float _lastActivity;
        private static bool _wasInStore;

        // ---------------------------------------------------------------
        // 挂载
        // ---------------------------------------------------------------

        internal static void Apply()
        {
            if (_harmony != null) return;

            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                // 明确写签名：以后游戏给这个方法加重载时，不会因歧义静默失配
                MethodInfo destroy = AccessTools.Method(typeof(GraphUtils), "DestroyIfUnplaced",
                    new[] { typeof(GameItem), typeof(string) });
                if (destroy != null)
                {
                    harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(CounterOverflow), nameof(DestroyPrefix)));
                }
                else
                {
                    QLog.Warn("[柜台溢出] 找不到 GraphUtils.DestroyIfUnplaced，物品版丢弃无法拦截");
                }

                MethodInfo sellString = AccessTools.Method(
                    typeof(PlayerStore), "TryAddDirectSellingItemToTable",
                    new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(int) });
                if (sellString != null)
                {
                    harmony.Patch(sellString, prefix: new HarmonyMethod(typeof(CounterOverflow), nameof(DirectSellStringPrefix)));
                }
                else
                {
                    QLog.Warn("[柜台溢出] 找不到 TryAddDirectSellingItemToTable(string)，跳过版丢弃无法拦截");
                }

                _harmony = harmony;

                // 读档 / 换场景时旧场景的暂存货和暂存网格都不能带过去，
                // 否则会把上一局的顾客货补进新场景的柜台。
                ModHook.OnGameLoadedInit -= ResetForScene;
                ModHook.OnGameLoadedInit += ResetForScene;

                QLog.Info("[柜台溢出] 已挂载：外柜台满时暂存，空位出现自动补上");
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 挂载失败：" + e);
                _harmony = null;
            }
        }

        internal static void Remove()
        {
            ModHook.OnGameLoadedInit -= ResetForScene;

            try
            {
                if (_harmony != null) _harmony.UnpatchAll(HarmonyId);
            }
            catch
            {
                // 卸载阶段出错无所谓
            }

            _harmony = null;
            ResetForScene();
        }

        private static void ResetForScene()
        {
            ClearParked();
            _stash = null;
            _wasInStore = false;
        }

        // ---------------------------------------------------------------
        // 拦截点 1：物品放不下 → 原版要销毁，我们收走
        // ---------------------------------------------------------------

        private static bool DestroyPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return true;

                GameItem item = __args[0] as GameItem;
                string context = __args[1] as string;

                if (item == null) return true;
                if (item.parentInventory != null) return true;                       // 已经放进容器了
                if (string.IsNullOrEmpty(context)) return true;
                if (context.IndexOf(ContextMarker, StringComparison.OrdinalIgnoreCase) < 0) return true;

                if (!Park(item)) return true;                                         // 收不下就交回原版

                QLog.Info("[柜台溢出] 柜满了，先收好「" + DisplayName(item) + "」，有空位再补");
                return false;                                                        // 不执行销毁
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 销毁拦截出错：" + e.Message);
                return true;
            }
        }

        // ---------------------------------------------------------------
        // 拦截点 2：柜台满时原版连实例都不造，我们造出来收走
        // ---------------------------------------------------------------

        private static bool DirectSellStringPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1) return true;

                string identifier = __args[0] as string;
                if (string.IsNullOrEmpty(identifier)) return true;

                EmporiumEntry store = EmporiumEntry.Instance;
                if (store == null || store.frontInvinvElement == null) return true;

                // 柜台还放得下 → 完全交给原版
                if (store.frontInvinvElement.CanTakeAnything()) return true;

                // 用和原版完全相同的创建方式（原版字符串版内部就是 DirectoryMaster.Item），
                // 这样货的品质、随机属性、许可证登记都和「没被跳过」时一致。
                GameItem item = DirectoryMaster.Item(identifier);
                if (item == null) return true;

                bool owned = __args.Length > 1 && __args[1] is bool b && b;
                bool stolen = __args.Length > 2 && __args[2] is bool s && s;
                int heat = __args.Length > 4 && __args[4] is int h ? h : 0;

                GeneralHelper.SetItemOwned(item, owned);

                if (stolen)
                {
                    try
                    {
                        if (PlayerStore.Instance != null && PlayerStore.Instance.isStolenItemIntroduced)
                        {
                            StolenHelper.InitStolenItem(item, heat);
                        }
                    }
                    catch
                    {
                        // 偷窃标记不是主流程，失败就继续
                    }
                }

                if (!Park(item))
                {
                    // 暂存区也满了：只能按原版规则丢弃，但至少要留一条日志
                    QLog.Warn("[柜台溢出] 暂存区放不下「" + DisplayName(item) + "」，按原版规则丢弃");
                    item.Destroy();
                    return true;
                }

                QLog.Info("[柜台溢出] 柜台放不下「" + DisplayName(item) + "」，已先收好");
                return false;
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 跳过拦截出错：" + e.Message);
                return true;
            }
        }

        // ---------------------------------------------------------------
        // 暂存
        // ---------------------------------------------------------------

        private static bool Park(GameItem item)
        {
            if (item == null) return false;

            GameGridInventory stash = EnsureStash();
            if (stash == null) return false;

            try
            {
                int left = GraphUtils.TryAcceptAll(stash, item);
                if (left > 0) return false;

                _parked.Add(item);
                _lastActivity = Time.unscaledTime;
                return true;
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 暂存失败：" + e.Message);
                return false;
            }
        }

        /// <summary>私有暂存网格：不挂窗口、不进存档，只在本场景里临时放东西。</summary>
        private static GameGridInventory EnsureStash()
        {
            if (_stash != null) return _stash;

            try
            {
                _stash = new GameGridInventory(StashSize, StashSize);
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 创建暂存区失败：" + e.GetType().Name + " " + e.Message);
                _stash = null;
            }

            return _stash;
        }

        // ---------------------------------------------------------------
        // 每帧：有空位就补上
        // ---------------------------------------------------------------

        internal static void Tick(float dt)
        {
            try
            {
                bool inStore = CustomerInStore();

                // 顾客刚离店：他自己的货要跟着走 —— 柜台上剩的未拥有物品 + 暂存区里的
                // 全部清掉。原版只在夜间结算时清柜台，客人走了货会一直留在那儿，
                // 看起来就像「没买下也归你了」。
                if (_wasInStore && !inStore)
                {
                    ClearCounterLeftovers();
                    ClearParked();
                    _wasInStore = false;
                    return;
                }

                _wasInStore = inStore;

                if (_parked.Count == 0) return;

                // 顾客不在店就一律不补位，只等清理
                if (!inStore)
                {
                    if (Time.unscaledTime - _lastActivity > IdleClearDelay) ClearParked();
                    return;
                }

                if (Time.unscaledTime < _nextFill) return;
                _nextFill = Time.unscaledTime + FillInterval;

                EmporiumEntry store = EmporiumEntry.Instance;
                if (store == null || store.frontInvinvElement == null) return;
                if (!store.frontInvinvElement.CanTakeAnything()) return;

                int filled = 0;
                for (int i = _parked.Count - 1; i >= 0; i--)
                {
                    GameItem item = _parked[i];
                    if (item == null || item.parentInventory == null)
                    {
                        // 已经销毁 / 被移出暂存：不能再往柜台上放
                        _parked.RemoveAt(i);
                        continue;
                    }

                    // 玩家已经买下了（变成自己的东西）：不需要再补，也不该再放回去
                    if (GeneralHelper.IsItemOwned(item))
                    {
                        _parked.RemoveAt(i);
                        continue;
                    }

                    try
                    {
                        int left = GraphUtils.TryAcceptAll(store.frontInvinvElement, item);
                        if (left <= 0)
                        {
                            _parked.RemoveAt(i);
                            filled++;
                            _lastActivity = Time.unscaledTime;
                        }
                    }
                    catch (Exception e)
                    {
                        QLog.Warn("[柜台溢出] 补位失败：" + e.Message);
                    }

                    if (filled >= 4) break;   // 一次别搬太多，避免一帧卡顿
                }

                if (filled > 0)
                {
                    QLog.Info("[柜台溢出] 柜台空出来了，补上 " + filled + " 件（还剩 " + _parked.Count + " 件等着）");
                }
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 每帧检查出错：" + e.Message);
            }
        }

        private static bool CustomerInStore()
        {
            try
            {
                // 主菜单 / 场景外别碰 PlayerStore.Instance（那会抛空引用），
                // 这是每帧调用的路径，不能靠「抛了再 catch」糊过去。
                if (EmporiumEntry.Instance == null) return false;

                // 用「当前有没有客人」判断，比 isClientArrived 可靠：
                // 普通客人送走时只把 currentClientInstance 置空，不一定动 isClientArrived。
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.currentClientInstance != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 清掉柜台上不属于玩家的残留货（= 上一位客人没卖掉的东西）。
        /// 用原版自己的 ClearFrontInvNonOwned，只清未拥有，玩家自己的东西不动。
        /// </summary>
        private static void ClearCounterLeftovers()
        {
            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                if (store == null || store.frontInvinvElement == null) return;

                int before = 0;
                List<GameItem> items = store.frontInvinvElement.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem item = items[i];
                    if (item == null) continue;
                    if (!GeneralHelper.IsItemOwned(item)) before++;
                }

                if (before <= 0) return;

                store.ClearFrontInvNonOwned();
                QLog.Info("[柜台溢出] 顾客离开，带走柜台上他自己剩下的 " + before + " 件货");
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 清理柜台残留失败：" + e.Message);
            }
        }

        private static void ClearParked()
        {
            if (_parked.Count == 0) return;

            int count = _parked.Count;
            for (int i = 0; i < _parked.Count; i++)
            {
                try
                {
                    if (_parked[i] != null) _parked[i].Destroy();
                }
                catch
                {
                    // 单个销毁失败不影响其他
                }
            }

            _parked.Clear();
            QLog.Info("[柜台溢出] 顾客已离开，清掉 " + count + " 件没来得及补位的货");
        }

        private static string DisplayName(GameItem item)
        {
            try
            {
                if (item == null) return "?";
                string name = item.GetDisplayName();
                return string.IsNullOrEmpty(name) ? "?" : name;
            }
            catch
            {
                return "?";
            }
        }
    }
}
