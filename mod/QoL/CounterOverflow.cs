using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 柜台溢出 + 分批翻页：
    /// 原版在「外柜台摆满」时会把后面本可购买的商品直接丢掉 ——
    /// 物品版走 <c>GraphUtils.DestroyIfUnplaced(..., "front counter full")</c> 销毁，
    /// 字符串版连实例都不创建（只记一笔 unplacedCounts 就跳过）。
    ///
    /// 本功能做三件事，全在后台，不开设置项：
    ///   1. 柜台满时把要丢的货收进私有暂存网格，不销毁（品质、违禁、偷窃标记全保留）；
    ///   2. 按下柜台右上角的左右箭头，整批切换显示的货（◀ 上一批 / ▶ 下一批）；
    ///   3. 当前批在柜台上有空位时，把本批没放下的零星货补回去。
    ///
    /// 批次怎么分：第 0 批 = 一开始就能摆上柜台的货；后面放不下的按到达顺序
    /// 用同样大小的网格试排，排满就开下一批。翻页 = 两批物品在「柜台」和
    /// 「私有暂存网格」之间整体对调，不重建物品、不碰存档。
    /// </summary>
    internal static class CounterOverflow
    {
        private const string HarmonyId = "local.qol.counter_overflow";

        /// <summary>只接管原版标记为「外柜台满」的丢弃，其他场景的销毁一概不碰。</summary>
        private const string ContextMarker = "front counter full";

        /// <summary>批次上限（正常客人两三批就顶天，纯粹防跑飞）。</summary>
        private const int MaxPages = 20;

        /// <summary>补位节流（秒）：别每帧都尝试搬，柜台上也可能正被玩家摆东西。</summary>
        private const float FillInterval = 0.5f;

        /// <summary>顾客离开后多久清空暂存（秒）：留点余量，避免到店瞬间误判。</summary>
        private const float IdleClearDelay = 6f;

        /// <summary>箭头可用状态刷新间隔（秒）。</summary>
        private const float UiRefreshInterval = 0.2f;

        private static Harmony _harmony;

        /// <summary>各批次的暂存网格。第 0 批在「正在显示」时物品在柜台上，网格是空的。</summary>
        private static readonly List<GameGridInventory> _pages = new List<GameGridInventory>();

        /// <summary>当前显示的是第几批（0 = 一开始那批）。</summary>
        private static int _current;

        private static float _nextFill;
        private static float _nextUiRefresh;
        private static float _lastActivity;
        private static bool _wasInStore;

        // 柜台右上角的翻页箭头
        private static RectTransform _toolbar;
        private static UnityEngine.UI.Button _prevButton;
        private static UnityEngine.UI.Button _nextButton;
        private static bool _uiUnavailable;

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

                QLog.Info("[柜台溢出] 已挂载：外柜台满时暂存，柜台右上角可左右翻批");
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
            ClearPages();
            _current = 0;
            _wasInStore = false;
            DestroyToolbar();
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

                QLog.Info("[柜台溢出] 柜满了，先收好「" + DisplayName(item) + "」，可按箭头翻批查看");
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
        // 分批暂存
        // ---------------------------------------------------------------

        /// <summary>
        /// 把一个放不下柜台的货收进某一批的暂存网格。
        /// 优先塞进已有的批次（按到达顺序排满就开新的），当前显示的批次不算 ——
        /// 那一批的货应该在柜台上，不该悄悄躲进它的暂存里。
        /// </summary>
        private static bool Park(GameItem item)
        {
            if (item == null) return false;

            EnsurePage(0);   // 第 0 批占位，保证溢出批次从 1 开始

            for (int i = 1; i < _pages.Count; i++)
            {
                if (i == _current) continue;
                GameGridInventory page = _pages[i];
                if (page == null) continue;
                if (TryPut(page, item)) return true;
            }

            if (_pages.Count >= MaxPages) return false;

            GameGridInventory created = NewPageGrid();
            if (created == null) return false;
            if (!TryPut(created, item)) return false;

            _pages.Add(created);
            return true;
        }

        private static bool TryPut(GameGridInventory grid, GameItem item)
        {
            try
            {
                int left = GraphUtils.TryAcceptAll(grid, item);
                if (left > 0) return false;

                _lastActivity = Time.unscaledTime;
                return true;
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 暂存失败：" + e.Message);
                return false;
            }
        }

        private static GameGridInventory NewPageGrid()
        {
            try
            {
                int w = 18;
                int h = 4;

                GameGridInventory front = Front();
                if (front != null && front.inventoryShape != null)
                {
                    w = Mathf.Max(1, front.inventoryShape.width);
                    h = Mathf.Max(1, front.inventoryShape.height);
                }

                return new GameGridInventory(w, h);
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 创建暂存网格失败：" + e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        private static GameGridInventory EnsurePage(int index)
        {
            while (_pages.Count <= index) _pages.Add(null);
            if (_pages[index] == null) _pages[index] = NewPageGrid();
            return _pages[index];
        }

        // ---------------------------------------------------------------
        // 翻页
        // ---------------------------------------------------------------

        private static void SwitchPage(int direction)
        {
            try
            {
                GameGridInventory front = Front();
                if (front == null) return;

                int target = FindNeighbourPage(direction);
                if (target < 0) return;

                GameGridInventory currentPage = EnsurePage(_current);
                GameGridInventory targetPage = EnsurePage(target);
                if (currentPage == null || targetPage == null) return;

                // 搬货期间屏蔽柜台的增删回调：否则顾客蓝图会对每一件货反复
                // 增删「首购折扣」，还会顺手清掉正在估价那件的高亮。
                Action<GameItem, GameInventory, SlotMarker> addCb = front.onSlotAddItemEndFunc;
                Action<GameItem, GameInventory, SlotMarker> remCb = front.onSlotRemoveItemEndFunc;
                front.onSlotAddItemEndFunc = null;
                front.onSlotRemoveItemEndFunc = null;

                try
                {
                    MoveNonOwned(front, currentPage);   // 柜台上的本批货 → 本批暂存
                    MoveAll(targetPage, front);        // 目标批次的货 → 柜台
                    _current = target;
                }
                finally
                {
                    front.onSlotAddItemEndFunc = addCb;
                    front.onSlotRemoveItemEndFunc = remCb;
                }

                _lastActivity = Time.unscaledTime;
                PlayButtonSound();

                QLog.Info("[柜台溢出] 翻到第 " + (_current + 1) + " 批（柜台 " +
                          front.childItems.Count + " 件，" + DescribePages() + "）");
                RefreshArrows();
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 翻页失败：" + e.Message);
            }
        }

        /// <summary>把 from 里不属于玩家的东西全搬进 to（放不下的留在原地，下次再试）。</summary>
        private static void MoveNonOwned(GameGridInventory from, GameGridInventory to)
        {
            if (from == null || to == null) return;

            List<GameItem> items = new List<GameItem>(from.childItems);
            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];
                if (item == null) continue;
                if (GeneralHelper.IsItemOwned(item)) continue;   // 玩家自己的东西不参与翻页
                TryPut(to, item);
            }
        }

        /// <summary>把 from 里的东西全搬进 to。</summary>
        private static void MoveAll(GameGridInventory from, GameGridInventory to)
        {
            if (from == null || to == null) return;

            List<GameItem> items = new List<GameItem>(from.childItems);
            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];
                if (item == null) continue;
                TryPut(to, item);
            }
        }

        /// <summary>往 direction 方向找最近一个有货的批次。</summary>
        private static int FindNeighbourPage(int direction)
        {
            if (direction < 0)
            {
                for (int i = _current - 1; i >= 0; i--)
                {
                    if (PageHasItems(i)) return i;
                }
                return -1;
            }

            for (int i = _current + 1; i < _pages.Count; i++)
            {
                if (PageHasItems(i)) return i;
            }
            return -1;
        }

        private static bool PageHasItems(int index)
        {
            if (index < 0 || index >= _pages.Count) return false;
            GameGridInventory page = _pages[index];
            if (page == null) return false;

            List<GameItem> items = page.childItems;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null) return true;
            }
            return false;
        }

        private static bool AnyPageHasItems()
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                if (PageHasItems(i)) return true;
            }
            return false;
        }

        private static string DescribePages()
        {
            int total = 0;
            for (int i = 0; i < _pages.Count; i++)
            {
                GameGridInventory page = _pages[i];
                if (page == null) continue;
                total += page.childItems.Count;
            }
            return "暂存 " + total + " 件";
        }

        // ---------------------------------------------------------------
        // 每帧：清理 / 补位 / 箭头状态
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
                    ClearPages();
                    _current = 0;
                    _wasInStore = false;
                    HideToolbar();
                    return;
                }

                _wasInStore = inStore;

                if (!inStore)
                {
                    if (AnyPageHasItems() && Time.unscaledTime - _lastActivity > IdleClearDelay)
                    {
                        ClearPages();
                        _current = 0;
                    }
                    HideToolbar();
                    return;
                }

                EnsureToolbar();
                UpdateToolbar();
                FillGapsFromCurrentPage();
            }
            catch (Exception e)
            {
                QLog.Warn("[柜台溢出] 每帧检查出错：" + e.Message);
            }
        }

        /// <summary>当前批次里没摆上柜台的零星货（柜台被玩家自己的东西占着时会剩下几件）。</summary>
        private static void FillGapsFromCurrentPage()
        {
            if (_current < 0 || _current >= _pages.Count) return;

            GameGridInventory page = _pages[_current];
            if (page == null || page.childItems.Count == 0) return;

            if (Time.unscaledTime < _nextFill) return;

            EmporiumEntry store = EmporiumEntry.Instance;
            if (store == null || store.frontInvinvElement == null) return;
            if (!store.frontInvinvElement.CanTakeAnything()) return;

            _nextFill = Time.unscaledTime + FillInterval;

            GameGridInventory front = store.frontInvinvElement;
            List<GameItem> items = new List<GameItem>(page.childItems);
            int filled = 0;

            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];
                if (item == null) continue;

                // 玩家已经买下的东西不往柜台上搬，也绝不销毁
                if (GeneralHelper.IsItemOwned(item)) continue;

                if (!TryPut(front, item)) continue;

                filled++;
                if (filled >= 4) break;   // 一次别搬太多，避免一帧卡顿
            }

            if (filled > 0)
            {
                QLog.Info("[柜台溢出] 柜台空出位置，本批补上 " + filled + " 件");
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

        private static void ClearPages()
        {
            int count = 0;

            for (int i = 0; i < _pages.Count; i++)
            {
                GameGridInventory page = _pages[i];
                if (page == null) continue;

                List<GameItem> items = page.childItems;
                for (int j = 0; j < items.Count; j++)
                {
                    try
                    {
                        if (items[j] != null) items[j].Destroy();
                    }
                    catch
                    {
                        // 单个销毁失败不影响其他
                    }
                }

                count += items.Count;
                items.Clear();
            }

            _pages.Clear();

            if (count > 0)
            {
                QLog.Info("[柜台溢出] 顾客已离开，清掉 " + count + " 件没来得及卖掉的货");
            }
        }

        // ---------------------------------------------------------------
        // 柜台右上角的翻页箭头
        // ---------------------------------------------------------------

        private static GameGridInventory Front()
        {
            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                return store != null ? store.frontInvinvElement : null;
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureToolbar()
        {
            if (_toolbar != null) return;
            if (_uiUnavailable) return;

            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                PixelWindow window = store != null ? store.frontInvWindow : null;
                if (window == null || window.handler == null) return;

                // 图标直接用游戏自带的平板皮肤箭头（32px 的左右选择箭头）
                Sprite prevNormal = null;
                Sprite prevHover = null;
                Sprite nextNormal = null;
                Sprite nextHover = null;

                // 估价平板还没建好就先等：这时候拿不到皮肤，不能当作「永远拿不到」
                if (NegociationUIManager.Instance == null || NegociationUIManager.Instance.uiObject == null) return;

                NegotiationTabletFrame frame =
                    NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>();
                if (frame == null) return;

                prevNormal = frame.prevNormal;
                prevHover = frame.prevHover;
                nextNormal = frame.nextNormal;
                nextHover = frame.nextHover;

                if (prevNormal == null || nextNormal == null)
                {
                    // 拿不到皮肤就先不做按钮，避免自绘一套跟游戏风格不搭的图
                    _uiUnavailable = true;
                    QLog.Warn("[柜台溢出] 没拿到估价平板的箭头图标，翻页按钮不显示（翻批逻辑仍在）");
                    return;
                }

                const float size = 16f;   // 一个格子大，跟柜台格子同比例
                const float gap = 2f;
                float total = size * 2f + gap;

                // 坑：frontInvWindow 的节点本身几乎没有尺寸，用锚点 (1,1) 会算到节点原点
                // （上次就跑到了货架最左边）。游戏自己摆窗口子节点用的是「左上角 + 像素坐标」
                // （WildNetworkUI.SetRect：anchor 左上、pivot 左上），这里照同一套来：
                // 用窗口像素宽度算右上角，再把整个箭头块放到窗口顶边之上。
                float windowWidth = window.widthPixels > 0 ? window.widthPixels : 18 * 16f;

                GameObject holder = new GameObject("QoL_PageArrows", typeof(RectTransform));
                _toolbar = (RectTransform)holder.transform;
                _toolbar.SetParent(window.handler.rectTransform, false);
                _toolbar.anchorMin = new Vector2(0f, 1f);
                _toolbar.anchorMax = new Vector2(0f, 1f);
                _toolbar.pivot = new Vector2(0f, 0f);   // 左下角当基准 → 底边贴着窗口顶边之上
                // 右边距：柜台画布本身缩放到屏幕约 3 倍，这里 1 画布像素 ≈ 3 屏幕像素。
                // 原先 2 像素边距，用户要求再往左靠 4 个屏幕像素 → 边距改 3.5。
                const float rightMargin = 3.5f;
                _toolbar.anchoredPosition = new Vector2(windowWidth - total - rightMargin, 3f);
                _toolbar.sizeDelta = new Vector2(total, size);

                _prevButton = TabletUI.ArrowButton(_toolbar, "QoL_Prev", 0f, 0f, size,
                    prevNormal, prevHover, delegate { SwitchPage(-1); });
                _nextButton = TabletUI.ArrowButton(_toolbar, "QoL_Next", size + gap, 0f, size,
                    nextNormal, nextHover, delegate { SwitchPage(1); });

                _toolbar.SetAsLastSibling();
                holder.SetActive(false);

                QLog.Info("[柜台溢出] 翻页箭头已就位（柜台右上角）");
            }
            catch (Exception e)
            {
                _uiUnavailable = true;
                QLog.Warn("[柜台溢出] 创建翻页箭头失败：" + e.Message);
            }
        }

        private static void UpdateToolbar()
        {
            if (_toolbar == null) return;

            bool show = AnyPageHasItems() && FrontWindowVisible();
            if (_toolbar.gameObject.activeSelf != show) _toolbar.gameObject.SetActive(show);
            if (!show) return;

            if (Time.unscaledTime < _nextUiRefresh) return;
            _nextUiRefresh = Time.unscaledTime + UiRefreshInterval;
            RefreshArrows();
        }

        private static void RefreshArrows()
        {
            if (_toolbar == null) return;

            TabletUI.SetArrowEnabled(_prevButton, FindNeighbourPage(-1) >= 0);
            TabletUI.SetArrowEnabled(_nextButton, FindNeighbourPage(1) >= 0);
        }

        private static bool FrontWindowVisible()
        {
            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                PixelWindow window = store != null ? store.frontInvWindow : null;
                return window != null && window.IsVisible();
            }
            catch
            {
                return false;
            }
        }

        private static void HideToolbar()
        {
            if (_toolbar != null && _toolbar.gameObject.activeSelf)
            {
                _toolbar.gameObject.SetActive(false);
            }
        }

        private static void DestroyToolbar()
        {
            if (_toolbar == null) return;

            try
            {
                UnityEngine.Object.Destroy(_toolbar.gameObject);
            }
            catch
            {
                // 场景已经卸载，忽略
            }

            _toolbar = null;
            _prevButton = null;
            _nextButton = null;
            _uiUnavailable = false;
        }

        private static void PlayButtonSound()
        {
            try
            {
                EmporiumAudioPlayer player = EmporiumAudioPlayer.Instance;
                if (player != null) player.Play();   // 就是 audioButton1 的点击音
            }
            catch
            {
                // 音效失败不影响翻页
            }
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
