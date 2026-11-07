using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Debug = UnityEngine.Debug;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QoL
{
    /// <summary>
    /// 需求高亮：一眼看出柜台前顾客想要什么，用彩色框线标出库存物品。
    /// 绿线=想要、琥珀警戒带=想要的赃物、红双线=想要的违禁品、角括号=容器里装着想要的。
    /// 移植自开源 MOD「Demand Highlight」(Apache 2.0, KCN-judu)，逻辑保持一致。
    /// </summary>
    internal static class DemandHighlight
    {
        private const string HarmonyId = "local.qol.demand_highlight";

        private const float MinGap = 0.15f;
        private const float IntentPoll = 0.5f;

        private static Harmony _harmony;

        private static bool _dirty = true;
        private static bool _drawn;
        private static float _nextAllowed;
        private static float _nextIntentPoll;
        private static int _lastIntent;

        private static StoreClient _lastClient;
        private static bool _warned;

        private static bool Enabled
        {
            get
            {
                QoLSettings settings = ModEntry.Settings;
                return settings != null && settings.DemandHighlight.Value;
            }
        }

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                // 物品/标签/移动/旋转/窗口变化 → 标脏重算
                PatchPostfix(typeof(GameItemElement), "Validate", Type.EmptyTypes, nameof(ItemChanged));
                PatchPostfix(typeof(GameItem), "EnableTag", new[] { typeof(string), typeof(bool) }, nameof(TagChanged));
                PatchPostfix(typeof(GameItem), "DisableTag", new[] { typeof(string), typeof(bool) }, nameof(TagChanged));
                PatchPostfix(typeof(GameItem), "SetEnableDisableTag", new[] { typeof(string), typeof(bool) }, nameof(TagChanged));
                PatchPostfix(typeof(GameItem), "ModifyTag", new[] { typeof(string), typeof(Action<TagState>), typeof(bool) }, nameof(TagChanged));
                PatchPostfix(typeof(GameItem), "SetGameItemType", new[] { typeof(string) }, nameof(TagChanged));
                PatchPostfix(typeof(GameItemElement), "RotCW", Type.EmptyTypes, nameof(MarkDirty));
                PatchPostfix(typeof(GameItemElement), "RotCCW", Type.EmptyTypes, nameof(MarkDirty));
                PatchPostfix(typeof(GameItem), "AddParent", new[] { typeof(GraphNodeStorage) }, nameof(ItemMoved));
                PatchPostfix(typeof(GameItem), "RemoveParent", new[] { typeof(GraphNodeStorage) }, nameof(ItemMoved));

                // 容器窗口开关 → 重算（用反射枚举 PixelWindow 的 Show/Hide 重载）
                int shows = 0;
                int hides = 0;
                MethodInfo[] methods = typeof(PixelWindow).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (MethodInfo m in methods)
                {
                    if (m.Name == "Show")
                    {
                        _harmony.Patch(m, postfix: new HarmonyMethod(typeof(DemandHighlight), nameof(WindowChanged)));
                        shows++;
                    }
                    else if (m.Name == "Hide")
                    {
                        _harmony.Patch(m, postfix: new HarmonyMethod(typeof(DemandHighlight), nameof(WindowChanged)));
                        hides++;
                    }
                }

                if (shows == 0 || hides == 0)
                {
                    throw new MissingMethodException("PixelWindow.Show/Hide not found");
                }

                SceneManager.sceneLoaded += OnSceneLoaded;
                _dirty = true;

                // 清掉旧贴图缓存，确保绘制逻辑改动后（如框线粗细、违禁品样式）立即生效
                DemandSprites.Clear();

                Debug.Log(ModEntry.Tag + "需求高亮已挂载");
            }
            catch (Exception e)
            {
                TearDown();
                Debug.LogWarning(ModEntry.Tag + "需求高亮启动失败：" + e.Message);
            }
        }

        internal static void Remove()
        {
            TearDown();
        }

        private static void PatchPostfix(Type type, string name, Type[] args, string method)
        {
            MethodInfo target = type.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
            if (target == null)
            {
                throw new MissingMethodException(type.Name + "." + name + " not found");
            }

            _harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(DemandHighlight), method));
        }

        private static void TearDown()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_harmony != null)
            {
                try
                {
                    _harmony.UnpatchAll(HarmonyId);
                }
                catch
                {
                }

                _harmony = null;
            }

            try
            {
                DemandPainter.DestroyAll();
            }
            catch
            {
            }

            try
            {
                DemandSprites.Clear();
            }
            catch
            {
            }

            _drawn = false;
            _lastClient = null;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (mode == LoadSceneMode.Single && scene.name != "InventoryScene")
                {
                    Release();
                }
        
            }
            catch (Exception e)
            {
                QLog.Warn("[需求高亮] 补丁出错：" + e.Message);
            }
        }

        private static void Release()
        {
            DemandRules.Release();
            DemandPainter.DestroyAll();
            _lastClient = null;
            _lastIntent = 0;
            _drawn = false;
            _dirty = true;
        }

        private static void MarkDirty()
        {
            try
            {
                _dirty = true;
        
            }
            catch (Exception e)
            {
                QLog.Warn("[需求高亮] 补丁出错：" + e.Message);
            }
        }

        private static void ItemChanged(GameItem __instance)
        {
            try
            {
                _dirty = true;
                DemandRules.Forget(__instance);
        
            }
            catch (Exception e)
            {
                QLog.Warn("[需求高亮] 补丁出错：" + e.Message);
            }
        }

        private static void TagChanged(GameItem __instance)
        {
            try
            {
                _dirty = true;
                DemandRules.Forget(__instance);
        
            }
            catch (Exception e)
            {
                QLog.Warn("[需求高亮] 补丁出错：" + e.Message);
            }
        }

        private static void ItemMoved(GameItem __instance, GraphNodeStorage node)
        {
            try
            {
                _dirty = true;
                DemandRules.Forget(__instance);
        
            }
            catch (Exception e)
            {
                QLog.Warn("[需求高亮] 补丁出错：" + e.Message);
            }
        }

        private static void WindowChanged(PixelWindow __instance)
        {
            try
            {
                List<GameItem> parentItems = __instance.parentItems;
                if (parentItems != null && parentItems.Count > 0)
                {
                    _dirty = true;
                }
        
            }
            catch (Exception e)
            {
                QLog.Warn("[需求高亮] 补丁出错：" + e.Message);
            }
        }

        /// <summary>每帧驱动（由 QoLUpdate 调用）。</summary>
        internal static void Tick()
        {
            if (!Enabled)
            {
                if (_drawn)
                {
                    DemandPainter.HideAll();
                    _drawn = false;
                }

                _lastClient = null;
                return;
            }

            try
            {
                float now = Time.unscaledTime;
                StoreClient client = DemandRules.CurrentClient();

                if (client != _lastClient)
                {
                    _lastClient = client;
                    _dirty = true;
                    _nextAllowed = 0f;
                }

                if (client != null && now >= _nextIntentPoll)
                {
                    _nextIntentPoll = now + IntentPoll;
                    int intent = DemandRules.Intent(client);
                    if (intent != _lastIntent)
                    {
                        _lastIntent = intent;
                        _dirty = true;
                    }
                }

                if (client == null)
                {
                    if (_drawn)
                    {
                        DemandPainter.HideAll();
                        _drawn = false;
                    }

                    _dirty = false;
                }
                else if (_dirty && now >= _nextAllowed)
                {
                    _dirty = false;
                    _nextAllowed = now + MinGap;

                    bool complete;
                    bool walked = DemandRules.Walk(client, false, out complete);

                    if (walked)
                    {
                        DemandPainter.Paint(complete);
                        _drawn = true;
                    }
                    else if (_drawn)
                    {
                        DemandPainter.HideAll();
                        _drawn = false;
                    }

                    if (!complete)
                    {
                        _dirty = true;
                        _nextAllowed = 0f;
                    }
                }
            }
            catch (Exception e)
            {
                _nextAllowed = Time.unscaledTime + 1f;
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning(ModEntry.Tag + "需求高亮刷新失败：" + e);
                }
            }
        }
    }
}
