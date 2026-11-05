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
    }

    internal struct DemandStyle
    {
        internal DemandMark Mark;
        internal bool Contents;

        internal DemandStyle(DemandMark mark, bool contents)
        {
            Mark = mark;
            Contents = mark != DemandMark.None && contents;
        }

        internal bool Equals(DemandStyle other)
        {
            return Mark == other.Mark && Contents == other.Contents;
        }

        internal static DemandStyle Max(DemandStyle a, DemandStyle b)
        {
            if (a.Mark != b.Mark)
            {
                return (byte)a.Mark <= (byte)b.Mark ? b : a;
            }

            return new DemandStyle(a.Mark, a.Contents && b.Contents);
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

            if (kind.Inspector || (kind.Selling && clientGoods))
            {
                result.Illegal = f.Illegal;
            }

            return result;
        }

        internal static DemandAgg Report(DemandAgg self, DemandAgg inside, DemandFacts f)
        {
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
