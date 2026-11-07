using System;

namespace QoL
{
    // ---------------------------------------------------------------
    // 需求高亮的纯数据结构与判定逻辑
    // 移植自开源 MOD「Demand Highlight」(Apache 2.0, KCN-judu)，
    // 命名空间 DemandHighlight → QoL，逻辑保持一致。
    // ---------------------------------------------------------------

    internal enum DemandMark : byte
    {
        None,
        Normal,
        Stolen,
        Contraband
    }

    internal struct DemandKind
    {
        internal bool Buying;
        internal bool Selling;
        internal bool Inspector;

        /// <summary>违禁扫描模式：不管顾客想买什么，只把违禁品/赃物标出来。</summary>
        internal bool Illegal;
    }

    internal struct DemandAgg
    {
        internal DemandMark Buyer;
        internal DemandMark Illegal;
    }

    internal struct DemandFacts
    {
        internal bool SelfOk;
        internal bool ContentsChecked;
        internal DemandMark Illegal;
        internal bool Locked;
        internal bool BehindLock;
        internal bool Immovable;
        internal bool BlocksSearch;
        internal bool Owned;

        /// <summary>这件东西在展示橱窗里：违禁/赃物要一直标红，不受顾客与检查状态影响。</summary>
        internal bool InShowcase;
    }

    internal struct DemandStyle
    {
        internal DemandMark Mark;
        internal bool Contents;

        /// <summary>一律用红色画（检查/橱窗警告）。图案仍按 Mark 区分：违禁实线框、赃物斜纹带。</summary>
        internal bool Alarm;

        internal DemandStyle(DemandMark mark, bool contents, bool alarm = false)
        {
            Mark = mark;
            Contents = mark != DemandMark.None && contents;
            Alarm = mark != DemandMark.None && alarm;
        }

        internal bool Equals(DemandStyle other)
        {
            return Mark == other.Mark && Contents == other.Contents && Alarm == other.Alarm;
        }

        internal static DemandStyle Max(DemandStyle a, DemandStyle b)
        {
            if (a.Mark != b.Mark)
            {
                return (byte)a.Mark <= (byte)b.Mark ? b : a;
            }

            return new DemandStyle(a.Mark, a.Contents && b.Contents, a.Alarm || b.Alarm);
        }
    }

    internal static class DemandDecide
    {
        internal static DemandMark Max(DemandMark a, DemandMark b)
        {
            return (byte)a <= (byte)b ? b : a;
        }

        internal static DemandMark Illegality(bool contraband, bool stolen)
        {
            if (!contraband)
            {
                return stolen ? DemandMark.Stolen : DemandMark.None;
            }

            return DemandMark.Contraband;
        }

        internal static DemandAgg Self(DemandKind kind, bool clientGoods,
            DemandFacts f, bool insideAllBought, out bool bought)
        {
            DemandAgg result = default;
            bought = false;

            if (kind.Buying)
            {
                bought = f.SelfOk && (!f.ContentsChecked || insideAllBought);
                if (bought)
                {
                    result.Buyer = f.Illegal == DemandMark.None ? DemandMark.Normal : f.Illegal;
                }
            }

            // 非法标记始终向上汇总：橱窗里的「装着违禁品的箱子」也要能被标出来。
            // 只有 Inspector / Illegal 两类会真的把它画成红色（见 Display）。
            result.Illegal = f.Illegal;

            return result;
        }

        /// <summary>
        /// 把「自己的非法标记」和「内容物里的非法标记」合成一个结论。
        /// deep = 违禁扫描：锁着的、藏在暗格里的也照样算（检查员本来就能翻出来）。
        /// </summary>
        internal static DemandAgg Report(bool deep, DemandAgg self, DemandAgg inside, DemandFacts f)
        {
            if (deep)
            {
                DemandAgg all = self;
                all.Buyer = Max(all.Buyer, inside.Buyer);
                all.Illegal = Max(all.Illegal, inside.Illegal);
                return all;
            }

            DemandAgg result = !f.BehindLock ? self : default;
            if (!f.Locked)
            {
                result.Buyer = Max(result.Buyer, inside.Buyer);
                if (!f.BlocksSearch)
                {
                    result.Illegal = Max(result.Illegal, inside.Illegal);
                }
            }

            return result;
        }

        internal static DemandStyle Display(DemandKind kind, bool clientGoods,
            DemandFacts f, DemandAgg self, DemandAgg inside, bool lockedAbove, bool blockedAbove)
        {
            // 违禁扫描（治安检查）：不看顾客想要什么，只看「违禁/赃物」，一律红色。
            // 这里刻意不看 lockedAbove / BehindLock / Locked —— 藏在箱子、暗格里的才是要标出来的。
            if (kind.Illegal)
            {
                if (self.Illegal != DemandMark.None)
                {
                    return new DemandStyle(self.Illegal, false, true);
                }

                return new DemandStyle(inside.Illegal, true, true);
            }

            // 展示橱窗里的违禁/赃物：任何时候都标红（不受顾客去留、检查状态影响）。
            // 装在橱窗容器里的也一并标出来（用角括号表示「在里面」）。
            if (f.InShowcase)
            {
                if (self.Illegal != DemandMark.None)
                {
                    return new DemandStyle(self.Illegal, false, true);
                }

                if (inside.Illegal != DemandMark.None)
                {
                    return new DemandStyle(inside.Illegal, true, true);
                }
            }

            if (lockedAbove || f.BehindLock || f.Immovable)
            {
                return default;
            }

            if (kind.Inspector)
            {
                if (blockedAbove)
                {
                    return default;
                }

                if (self.Illegal != DemandMark.None)
                {
                    return new DemandStyle(self.Illegal, false);
                }

                if (f.Locked || f.BlocksSearch)
                {
                    return default;
                }

                return new DemandStyle(inside.Illegal, true);
            }

            if (kind.Selling && !f.Owned)
            {
                if (!clientGoods)
                {
                    return default;
                }

                if (self.Illegal != DemandMark.None)
                {
                    return new DemandStyle(self.Illegal, false);
                }

                if (f.Locked)
                {
                    return default;
                }

                return new DemandStyle(inside.Illegal, true);
            }

            if (kind.Buying)
            {
                if (self.Buyer != DemandMark.None)
                {
                    return new DemandStyle(self.Buyer, false);
                }

                if (f.Locked)
                {
                    return default;
                }

                return new DemandStyle(inside.Buyer, true);
            }

            return default;
        }
    }

    internal struct DemandShape
    {
        internal int W;
        internal int H;
        internal bool[] Cells;
        internal long Hash;

        internal static readonly DemandShape Full = new DemandShape
        {
            W = 1,
            H = 1,
            Cells = new bool[1] { true },
            Hash = 1L
        };

        internal static DemandShape FromCells(int w, int h, Func<int, int, bool> covered)
        {
            if (w <= 0 || h <= 0 || w > 64 || h > 64)
            {
                return Full;
            }

            bool[] array = new bool[w * h];
            long num = 17L;
            bool flag = true;

            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    bool v = array[i * w + j] = covered(j, i);
                    flag = flag && v;
                    num = num * 1099511628211L ^ (v ? 2 : 1);
                }
            }

            if (flag)
            {
                return Full;
            }

            num = num * 31 + w;
            num = num * 31 + h;

            return new DemandShape { W = w, H = h, Cells = array, Hash = num };
        }
    }
}
