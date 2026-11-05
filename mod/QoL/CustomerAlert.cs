using System;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 通缉犯 / 改造体顾客的到店提示。
    ///
    /// 检测：每 0.3 秒看一眼当前顾客是不是换了，读顾客蓝图里的 isWanted / isAug。
    /// 显示：交给 CustomerBadge 在人物脑袋右边写一行固定文字，不是可拖动可关闭的窗口。
    ///
    /// 文案（都按"检测到 X 到店"这个格式）：
    ///   通缉犯         → 检测到通缉犯到店
    ///   改造体         → 检测到改造体到店
    ///   两者都是       → 检测到通缉犯·改造体
    ///   潜伏型通缉犯   → 不提示（游戏里这类本来就不该被认出来）
    /// </summary>
    internal static class CustomerAlert
    {
        /// <summary>轮询间隔（秒）。</summary>
        private const float PollInterval = 0.3f;

        private static readonly Color WantedColor = new Color(1f, 0.42f, 0.35f);
        private static readonly Color AugColor = new Color(1f, 0.66f, 0.20f);

        private static float _timer;
        private static bool _loggedStack;

        internal static void Apply()
        {
            _timer = 0f;
            _loggedStack = false; // 重挂后允许再记一次完整堆栈
        }

        internal static void Remove()
        {
            CustomerBadge.Remove();
        }

        internal static void Tick(float deltaTime)
        {
            QoLSettings settings = ModEntry.Settings;
            if (settings == null || !settings.CustomerAlert.Value)
            {
                CustomerBadge.Hide();
                return;
            }

            _timer += deltaTime;
            if (_timer < PollInterval)
            {
                return;
            }

            _timer = 0f;

            try
            {
                Step();
            }
            catch (Exception e)
            {
                // 只记一次完整堆栈：这条路径读档途中容易空引用，刷屏没意义，
                // 但要能看出到底哪一行，所以第一次把 e 整个打出来
                if (!_loggedStack)
                {
                    _loggedStack = true;
                    Debug.LogWarning(ModEntry.Tag + "顾客到店提示出错：" + e);
                }
            }
        }

        private static void Step()
        {
            if (EmporiumEntry.Instance == null)
            {
                CustomerBadge.Hide();
                return;
            }

            PlayerStore store = PlayerStore.Instance;
            if (store == null)
            {
                CustomerBadge.Hide();
                return;
            }

            // 只有营业中才会有顾客站柜台；读档/场景切换途中的半初始化状态直接跳过，
            // 免得在 UI 还没建好时去摆提示牌（那一段会拿到空引用）
            try
            {
                if (store.storeState != PlayerStore.StoreState.OPEN)
                {
                    CustomerBadge.Hide();
                    return;
                }
            }
            catch
            {
                return;
            }

            StoreClientInstance current;
            try
            {
                current = store.currentClientInstance;
            }
            catch
            {
                // 场景切换途中读不到，等下个周期
                return;
            }

            if (current == null)
            {
                CustomerBadge.Hide();
                return;
            }

            StoreClient blueprint;
            try
            {
                blueprint = current.GetClientBlueprint();
            }
            catch
            {
                return;
            }

            if (blueprint == null)
            {
                CustomerBadge.Hide();
                return;
            }

            bool wanted = false;
            bool aug = false;

            try
            {
                wanted = blueprint.isWanted;
            }
            catch
            {
                // 字段缺失就当作不是
            }

            try
            {
                aug = blueprint.isAug;
            }
            catch
            {
                // 同上
            }

            // 潜伏型通缉犯不暴露
            if (wanted)
            {
                try
                {
                    if (blueprint.isStealthWanted)
                    {
                        wanted = false;
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            if (!wanted && !aug)
            {
                CustomerBadge.Hide();
                return;
            }

            string text;
            Color color;

            if (wanted && aug)
            {
                text = "检测到通缉犯·改造体";
                color = WantedColor;
            }
            else if (wanted)
            {
                text = "检测到通缉犯到店";
                color = WantedColor;
            }
            else
            {
                text = "检测到改造体到店";
                color = AugColor;
            }

            CustomerBadge.Show(text, color);
        }
    }
}
