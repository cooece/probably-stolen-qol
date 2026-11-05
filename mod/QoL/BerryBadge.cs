using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 荧光莓品质角标。
    ///
    /// 这份实现是照抄参考模组 WineBerryQualityBadges（创意工坊 3830557056）的做法的，
    /// 连像素位图、颜色常量、色块位置都原样搬过来——那套算法已经被玩家验证过，
    /// 自己另起一套只会更差。
    ///
    /// 机制：
    ///   1. Harmony 挂 <c>GameItemElement.HandleInnateModSprite</c> 的 Prefix，
    ///      在格子刷新前把委托包好（时机错了就永远不会被读到）；
    ///   2. 委托里往图层列表末尾追加一层 32×32 的角标贴图，PPU = 100（和图标同密度）；
    ///   3. Sprite 按等级缓存，四档各生成一次。
    ///
    /// 角标是「9×9 的方框 + 中间一个符号」，贴在图标右上角：
    ///   cond = 2     小果      +2   绿 #4FD17A   十字
    ///   cond = 1     标准      +1   黄 #F0C447   横杠
    ///   cond = 0/-1  未熟/过熟  +0   紫 #C48AF5   V 形
    ///   cond = -2    发霉      -1   红 #E64B4B   竖杠带底座
    /// </summary>
    internal static class BerryBadge
    {
        private const string HarmonyId = "local.qol.berry_badge";

        private const string BerryId = "wine_berry";
        private const string BerryTag = "BERRY_TAG";
        private const string CondKey = "BERRY_CONDITION_INT";

        private const string GradeTag = "WINE_BERRY_QUALITY_BADGES_GRADE_V2";
        private const string LegacyGradeTag = "WINE_BERRY_QUALITY_BADGES_GRADE_V1";

        /// <summary>
        /// 是否不需要放大镜鉴定就显示角标。
        /// 参考模组默认关（只显示鉴定过的），我们默认开——不然刚进游戏满背包看不到任何标记，
        /// 等于功能不存在。想看原版玩法就改成 false。
        /// </summary>
        private static readonly bool WithoutMagnifier = true;

        /// <summary>
        /// false = 符号模式（默认，和参考模组一致）；true = 数字模式（+2 / +1 / +0 / -1）。
        /// </summary>
        private const bool ShowScore = false;

        private static Harmony _harmony;
        private static bool _warned;

        /// <summary>已包装过委托的物品。用 ConditionalWeakTable，不阻止物品被回收。</summary>
        private static readonly ConditionalWeakTable<GameItem, Binding> Bindings =
            new ConditionalWeakTable<GameItem, Binding>();

        /// <summary>还记得的物品，用来在需要时刷新已显示的格子。</summary>
        private static readonly List<WeakReference<GameItem>> Tracked = new List<WeakReference<GameItem>>();

        /// <summary>[模式, 等级] → 贴图/精灵，四档各一份。</summary>
        private static readonly Sprite[,] Sprites = new Sprite[2, 5];
        private static readonly Texture2D[,] Textures = new Texture2D[2, 5];

        private sealed class Binding
        {
            internal Func<GameItem, List<ItemSpriteModifier>> Previous;
            internal Func<GameItem, List<ItemSpriteModifier>> Wrapper;

            internal List<ItemSpriteModifier> Render(GameItem item)
            {
                return Compose(item, Previous);
            }
        }

        // ------------------------------------------------------------------
        // 注册
        // ------------------------------------------------------------------

        internal static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);

                MethodInfoHolder.Ensure();

                if (MethodInfoHolder.HandleInnateModSprite != null)
                {
                    _harmony.Patch(
                        MethodInfoHolder.HandleInnateModSprite,
                        prefix: new HarmonyMethod(typeof(BerryBadge), nameof(BeforeRender)));
                }
                else
                {
                    QLog.Warn("荧光莓徽标：找不到 GameItemElement.HandleInnateModSprite，功能不会生效");
                }

                // 鉴定钩子：记录品质标签（只在关掉「免鉴定」时才会被读到）
                if (MethodInfoHolder.OnInspectItem != null)
                {
                    _harmony.Patch(
                        MethodInfoHolder.OnInspectItem,
                        postfix: new HarmonyMethod(typeof(BerryBadge), nameof(AfterInspect)));
                }

                QLog.Info("荧光莓徽标：已挂载（符号模式，免鉴定=" + WithoutMagnifier + "）");
            }
            catch (Exception e)
            {
                QLog.Warn("荧光莓徽标：挂载失败 —— " + e.Message);
            }
        }

        internal static void Remove()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // 卸载失败不影响退出
            }

            _harmony = null;
            DetachAll();
        }

        private static class MethodInfoHolder
        {
            internal static System.Reflection.MethodInfo HandleInnateModSprite;
            internal static System.Reflection.MethodInfo OnInspectItem;

            internal static void Ensure()
            {
                if (HandleInnateModSprite == null)
                {
                    HandleInnateModSprite = AccessTools.Method(typeof(GameItemElement), "HandleInnateModSprite");
                }

                if (OnInspectItem == null)
                {
                    Type t = AccessTools.TypeByName("InspectionUIManager");
                    if (t != null)
                    {
                        OnInspectItem = AccessTools.Method(t, "OnInspectItem");
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // 挂接
        // ------------------------------------------------------------------

        private static void BeforeRender(GameItemElement __instance)
        {
            try
            {
                Attach(__instance);
            }
            catch
            {
                // 渲染期间抛异常会连累游戏，一律吞掉
            }
        }

        private static void AfterInspect(GameItem gameItem)
        {
            try
            {
                if (gameItem == null || !IsBerry(gameItem))
                {
                    return;
                }

                int grade = Classify(gameItem.GetTagReadonly(CondKey).GetInt());

                gameItem.ModifyTag(GradeTag, state => state.Enable().SetInt(grade), false);
            }
            catch
            {
                // 鉴定钩子失败不影响鉴定本身
            }
        }

        internal static bool IsBerry(GameItem item)
        {
            return item != null
                && item.identifier == BerryId
                && item.IsTag(BerryTag);
        }

        private static void Attach(GameItem item)
        {
            if (!IsBerry(item) || Bindings.TryGetValue(item, out _))
            {
                return;
            }

            // 解开之前包过的层，避免同一个物品被包多份（委托里套委托会叠出多层角标）
            Func<GameItem, List<ItemSpriteModifier>> func = item.getItemSpriteModifierForItemFunc;
            while (func != null && func.Target is Binding existing)
            {
                func = existing.Previous;
            }

            var binding = new Binding { Previous = func };
            binding.Wrapper = binding.Render;

            Bindings.Add(item, binding);

            Tracked.RemoveAll(weak => !weak.TryGetTarget(out _));
            Tracked.Add(new WeakReference<GameItem>(item));

            item.getItemSpriteModifierForItemFunc = binding.Wrapper;
        }

        private static List<ItemSpriteModifier> Compose(
            GameItem item, Func<GameItem, List<ItemSpriteModifier>> previous)
        {
            List<ItemSpriteModifier> list = previous?.Invoke(item);

            if (!IsBerry(item))
            {
                return list;
            }

            int grade = DisplayGrade(item);
            if (!IsGrade(grade))
            {
                return list;
            }

            try
            {
                int mode = ShowScore ? 1 : 0;
                if (Sprites[mode, grade] == null)
                {
                    CreateSprite(grade, mode);
                }

                List<ItemSpriteModifier> result = list == null
                    ? new List<ItemSpriteModifier>(1)
                    : new List<ItemSpriteModifier>(list);

                result.Add(new ItemSpriteModifier(
                    Sprites[mode, grade], Vector2.zero, 0, false, 100, false, default(Color)));

                return result;
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    QLog.Warn("荧光莓徽标：合成图层失败 —— " + e.Message);
                }

                return list;
            }
        }

        /// <summary>等级：1=差(紫) 2=普通(黄) 3=优(绿) 4=发霉(红)。0 表示不显示。</summary>
        private static int DisplayGrade(GameItem item)
        {
            TagSystem state = item.modifiedState;

            if (WithoutMagnifier)
            {
                if (state.dict.TryGetValue(CondKey, out TagState value) && value.IsEnabled())
                {
                    return Classify(value.GetInt());
                }

                return 0;
            }

            if (state.dict.TryGetValue(GradeTag, out TagState v2) && v2.IsEnabled())
            {
                return v2.GetInt();
            }

            if (!state.dict.TryGetValue(LegacyGradeTag, out TagState v1) || !v1.IsEnabled())
            {
                return 0;
            }

            // 旧版标签：发霉被记成了 1，要按 condition 纠正回来
            if (v1.GetInt() == 1
                && state.dict.TryGetValue(CondKey, out TagState cond)
                && cond.IsEnabled()
                && cond.GetInt() == -2)
            {
                return 4;
            }

            return v1.GetInt();
        }

        private static int Classify(int condition)
        {
            switch (condition)
            {
                case -2:
                    return 4;   // 发霉
                case 1:
                    return 2;   // 标准
                case 2:
                    return 3;   // 小果
                default:
                    return 1;   // 未熟 / 过熟
            }
        }

        private static bool IsGrade(int grade)
        {
            return grade >= 1 && grade <= 4;
        }

        // ------------------------------------------------------------------
        // 生成贴图
        // ------------------------------------------------------------------

        private static void CreateSprite(int grade, int mode)
        {
            uint[] pixels = mode == 1 ? ScorePixels(grade) : Pixels(grade);

            var colors = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                uint v = pixels[i];
                colors[i] = new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }

            var tex = new Texture2D(32, 32, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            tex.SetPixels32(colors);
            tex.Apply(false, false);

            Textures[mode, grade] = tex;

            // PPU 必须 100：物品图标就是这个密度，写成 32 会放大约三倍，盖住旁边的格子。
            Sprites[mode, grade] = Sprite.Create(
                tex, new Rect(0f, 0f, 32f, 32f), new Vector2(0.5f, 0.5f), 100f);
        }

        private static uint[] Pixels(int grade)
        {
            return BuildPixels(grade, false);
        }

        private static uint[] ScorePixels(int grade)
        {
            return BuildPixels(grade, true);
        }

        /// <summary>
        /// 画 32×32 的像素图。色块是 9×9（数字模式 11×9），贴在右上角；
        /// 四个角留空做出圆角感，边框用深色 #242335，中间按等级画符号。
        /// 这套坐标和配色的常量是从参考模组逐字搬过来的。
        /// </summary>
        private static uint[] BuildPixels(int grade, bool showScore)
        {
            var px = new uint[1024];

            if (!IsGrade(grade))
            {
                return px;
            }

            uint bg;
            switch (grade)
            {
                case 1: bg = 3297441279u; break;   // #C48AF5 紫
                case 2: bg = 4039395327u; break;   // #F0C447 黄
                case 3: bg = 1339128575u; break;   // #4FD17A 绿
                default: bg = 3863694335u; break;  // #E64B4B 红
            }

            const uint Edge = 606287359u;          // #242335 边框与符号色

            int x0 = showScore ? 20 : 22;
            int w = 31 - x0;

            for (int i = 1; i <= 9; i++)
            {
                for (int j = x0; j <= 30; j++)
                {
                    int dx = j - x0;
                    int dy = i - 1;

                    // 四角留空
                    if ((dx == 0 || dx == w - 1) && (dy == 0 || dy == 8))
                    {
                        continue;
                    }

                    bool edge = dx == 0 || dx == w - 1 || dy == 0 || dy == 8;

                    bool symbol;
                    switch (grade)
                    {
                        case 1:
                            symbol = dx >= 2 && dx <= 6 && (dy == dx || dy == 8 - dx);
                            break;
                        case 2:
                            symbol = dy == 4 && dx >= 2 && dx <= 6;
                            break;
                        case 3:
                            symbol = (dy == 4 && dx >= 2 && dx <= 6)
                                     || (dx == 4 && dy >= 2 && dy <= 6);
                            break;
                        default:
                            symbol = dx == 4 && ((dy >= 2 && dy <= 4) || dy == 6);
                            break;
                    }

                    if (showScore)
                    {
                        symbol = IsScorePixel(grade, dx, dy);
                    }

                    // 注意是 31 - i：贴图 y 轴朝上，而这里 i 是从顶部往下数的
                    px[(31 - i) * 32 + j] = (edge || symbol) ? Edge : bg;
                }
            }

            return px;
        }

        /// <summary>数字模式的点阵：左边一个加减号，右边 3×5 的阿拉伯数字。</summary>
        private static bool IsScorePixel(int grade, int x, int y)
        {
            if (x >= 2 && x <= 4)
            {
                if (y == 4)
                {
                    return true;    // 加减号的横杠
                }

                if (grade != 4 && x == 3)
                {
                    return y == 3 || y == 5;   // 加号的竖杠
                }

                return false;
            }

            if (x < 6 || x > 8 || y < 2 || y > 6)
            {
                return false;
            }

            string glyph;
            switch (grade)
            {
                case 1: glyph = "111101101101111"; break;  // 0
                case 3: glyph = "111001111100111"; break;  // 2
                default: glyph = "010110010010111"; break; // 1
            }

            return glyph[(y - 2) * 3 + x - 6] == '1';
        }

        // ------------------------------------------------------------------
        // 刷新与卸载
        // ------------------------------------------------------------------

        /// <summary>让已经显示的格子重画一次（改设置后立刻生效）。</summary>
        internal static void RefreshDisplayed()
        {
            WeakReference<GameItem>[] snapshot = Tracked.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!snapshot[i].TryGetTarget(out GameItem target))
                {
                    continue;
                }

                GameItemElement element = target as GameItemElement;
                if (element != null && !element.IsDestroyed())
                {
                    element.HandleInnateModSprite();
                }
            }
        }

        private static void DetachAll()
        {
            foreach (WeakReference<GameItem> weak in Tracked)
            {
                if (!weak.TryGetTarget(out GameItem item))
                {
                    continue;
                }

                if (!Bindings.TryGetValue(item, out Binding binding))
                {
                    continue;
                }

                if (item.getItemSpriteModifierForItemFunc == binding.Wrapper)
                {
                    item.getItemSpriteModifierForItemFunc = binding.Previous;
                }

                GameItemElement element = item as GameItemElement;
                if (element != null)
                {
                    try
                    {
                        element.HandleInnateModSprite();
                    }
                    catch (MissingReferenceException)
                    {
                    }
                    catch (NullReferenceException)
                    {
                    }
                }
            }

            Tracked.Clear();

            for (int i = 0; i < Sprites.GetLength(0); i++)
            {
                for (int j = 1; j < Sprites.GetLength(1); j++)
                {
                    if (Sprites[i, j] != null)
                    {
                        UnityEngine.Object.Destroy(Sprites[i, j]);
                        Sprites[i, j] = null;
                    }

                    if (Textures[i, j] != null)
                    {
                        UnityEngine.Object.Destroy(Textures[i, j]);
                        Textures[i, j] = null;
                    }
                }
            }

            _warned = false;
        }
    }
}
