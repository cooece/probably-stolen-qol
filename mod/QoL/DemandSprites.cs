using System;
using System.Collections.Generic;
using UnityEngine;

namespace QoL
{
    // ---------------------------------------------------------------
    // 需求高亮的框线贴图生成（距离场算法 + 贴图缓存）
    // 移植自开源 MOD「Demand Highlight」(Apache 2.0, KCN-judu)
    // ---------------------------------------------------------------

    internal static class DemandOutline
    {
        private struct Point
        {
            internal float X;
            internal float Y;

            internal Point(float x, float y)
            {
                X = x;
                Y = y;
            }
        }

        private const int Inf = 1048576;

        internal static byte[] Alpha(DemandShape shape, int w, int h, DemandStyle style,
            int t, int r, bool fill, float reach)
        {
            int num = w * h;
            bool[] mask = Mask(shape, w, h);
            bool[] set = mask;
            int[] dist = new int[num];

            if (r > 0)
            {
                Distance(set, w, h, true, dist);
                bool[] far = new bool[num];
                for (int i = 0; i < num; i++)
                {
                    far[i] = dist[i] > 3 * r;
                }

                Distance(far, w, h, false, dist);
                bool[] trimmed = new bool[num];
                for (int j = 0; j < num; j++)
                {
                    trimmed[j] = mask[j] && dist[j] <= 3 * r + 1;
                }

                set = trimmed;
            }

            Distance(set, w, h, true, dist);
            byte fillByte = (byte)(fill ? (style.Mark == DemandMark.Contraband ? 44u : 30u) : 0u);

            List<Point> corners = null;
            if (style.Contents)
            {
                corners = Corners(shape, w, h);
                if (reach <= 0f)
                {
                    reach = Math.Max(3 * t + 3,
                        Math.Min((float)w / shape.W, (float)h / shape.H) * 0.3f);
                }
            }

            int near = 3 * t;
            int stripe = 2 * t + 2;
            byte[] result = new byte[num];

            for (int k = 0; k < h; k++)
            {
                for (int l = 0; l < w; l++)
                {
                    int idx = k * w + l;
                    if (!set[idx])
                    {
                        continue;
                    }

                    int d = dist[idx];
                    byte b;
                    if (style.Contents)
                    {
                        b = (d <= near && NearCorner(corners, l + 0.5f, k + 0.5f, reach))
                            ? byte.MaxValue : (byte)0;
                    }
                    else if (style.Mark == DemandMark.Stolen)
                    {
                        b = d > near ? fillByte
                            : ((l + k) / stripe % 2 == 0 ? byte.MaxValue : (byte)80);
                    }
                    else
                    {
                        // 普通物品与违禁品同构：单层外框 + 透明填充，
                        // 仅靠颜色区分（违禁品红、fillByte 更深）。
                        b = d <= near ? byte.MaxValue : fillByte;
                    }

                    result[idx] = b;
                }
            }

            return result;
        }

        private static bool[] Mask(DemandShape shape, int w, int h)
        {
            bool[] array = new bool[w * h];
            for (int i = 0; i < h; i++)
            {
                int y = Math.Min(shape.H - 1, (h - 1 - i) * shape.H / h);
                for (int j = 0; j < w; j++)
                {
                    int x = Math.Min(shape.W - 1, j * shape.W / w);
                    array[i * w + j] = shape.Cells[y * shape.W + x];
                }
            }

            return array;
        }

        private static void Distance(bool[] set, int w, int h, bool towardOutside, int[] dist)
        {
            int border = towardOutside ? 0 : Inf;
            for (int i = 0; i < w * h; i++)
            {
                dist[i] = set[i] == towardOutside ? Inf : 0;
            }

            for (int j = 0; j < h; j++)
            {
                for (int k = 0; k < w; k++)
                {
                    int idx = j * w + k;
                    int d = dist[idx];
                    if (d == 0)
                    {
                        continue;
                    }

                    d = Math.Min(d, (k > 0 ? dist[idx - 1] : border) + 3);
                    d = Math.Min(d, (j > 0 ? dist[idx - w] : border) + 3);
                    d = Math.Min(d, (k > 0 && j > 0 ? dist[idx - w - 1] : border) + 4);
                    d = Math.Min(d, (k < w - 1 && j > 0 ? dist[idx - w + 1] : border) + 4);
                    dist[idx] = d;
                }
            }

            for (int m = h - 1; m >= 0; m--)
            {
                for (int n = w - 1; n >= 0; n--)
                {
                    int idx = m * w + n;
                    int d = dist[idx];
                    if (d == 0)
                    {
                        continue;
                    }

                    d = Math.Min(d, (n < w - 1 ? dist[idx + 1] : border) + 3);
                    d = Math.Min(d, (m < h - 1 ? dist[idx + w] : border) + 3);
                    d = Math.Min(d, (n < w - 1 && m < h - 1 ? dist[idx + w + 1] : border) + 4);
                    d = Math.Min(d, (n > 0 && m < h - 1 ? dist[idx + w - 1] : border) + 4);
                    dist[idx] = d;
                }
            }
        }

        private static List<Point> Corners(DemandShape shape, int w, int h)
        {
            var list = new List<Point>();
            for (int i = 0; i <= shape.H; i++)
            {
                for (int j = 0; j <= shape.W; j++)
                {
                    if (Cell(shape, j - 1, i - 1) + Cell(shape, j, i - 1) +
                        Cell(shape, j - 1, i) + Cell(shape, j, i) == 1)
                    {
                        list.Add(new Point(
                            (float)j * w / shape.W,
                            h - (float)i * h / shape.H));
                    }
                }
            }

            return list;
        }

        private static int Cell(DemandShape shape, int x, int y)
        {
            if (x < 0 || y < 0 || x >= shape.W || y >= shape.H || !shape.Cells[y * shape.W + x])
            {
                return 0;
            }

            return 1;
        }

        private static bool NearCorner(List<Point> corners, float x, float y, float reach)
        {
            for (int i = 0; i < corners.Count; i++)
            {
                if (Math.Abs(corners[i].X - x) <= reach && Math.Abs(corners[i].Y - y) <= reach)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class DemandSprites
    {
        private struct Key : IEquatable<Key>
        {
            internal long Shape;
            internal int W;
            internal int H;
            internal int Look;
            internal int Thickness;
            internal int Corner;
            internal bool Fill;

            public bool Equals(Key o)
            {
                return Shape == o.Shape && W == o.W && H == o.H && Look == o.Look &&
                       Thickness == o.Thickness && Corner == o.Corner && Fill == o.Fill;
            }

            public override bool Equals(object o)
            {
                return o is Key && Equals((Key)o);
            }

            public override int GetHashCode()
            {
                return (((((((int)Shape ^ (int)(Shape >> 32)) * 31 + W) * 31 + H) * 31 + Look)
                    * 31 + Thickness) * 31 + Corner) * 2 + (Fill ? 1 : 0);
            }
        }

        private const int MaxSide = 512;
        private const int CacheCap = 384;

        private static readonly Dictionary<Key, Sprite> cache = new Dictionary<Key, Sprite>();

        internal static int Generation { get; private set; }

        internal static DemandShape Read(GridShape shape)
        {
            return DemandShape.FromCells(shape.width, shape.height,
                (x, y) => shape.GetLocal(x, y) != 0);
        }

        internal static void Clear()
        {
            foreach (Sprite value in cache.Values)
            {
                if (value != null)
                {
                    UnityEngine.Object.Destroy(value.texture);
                    UnityEngine.Object.Destroy(value);
                }
            }

            cache.Clear();
            Generation++;
        }

        internal static Sprite Get(DemandShape shape, float width, float height,
            DemandStyle style, int thickness, int corner, bool fill)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(width), 2, MaxSide);
            int h = Mathf.Clamp(Mathf.RoundToInt(height), 2, MaxSide);

            Key key = new Key
            {
                Shape = shape.Hash,
                W = w,
                H = h,
                Look = LookOf(style),
                Thickness = thickness,
                Corner = corner,
                Fill = fill && !style.Contents
            };

            return GetOrBake(key,
                () => Bake(shape, w, h, style, thickness, corner, key.Fill, 0f, Vector4.zero));
        }

        private static int FrameBorder(int thickness, int corner)
        {
            return 3 * thickness + 4 + corner;
        }

        internal static Sprite Frame(DemandStyle style, int thickness, int corner, bool fill)
        {
            int border = FrameBorder(thickness, corner);
            int size = 2 * border + 2 * (2 * thickness + 2);

            Key key = new Key
            {
                Shape = -1L,
                W = size,
                H = size,
                Look = LookOf(style),
                Thickness = thickness,
                Corner = corner,
                Fill = fill && !style.Contents
            };

            return GetOrBake(key,
                () => Bake(DemandShape.Full, size, size, style, thickness, corner, key.Fill,
                    3 * thickness + 4,
                    new Vector4(border, border, border, border)));
        }

        /// <summary>统一「查缓存 → 满则清空 → 烘焙并缓存」，Get 和 Frame 共用。</summary>
        private static Sprite GetOrBake(Key key, Func<Sprite> bake)
        {
            if (cache.TryGetValue(key, out Sprite value) && value != null)
            {
                return value;
            }

            if (cache.Count >= CacheCap)
            {
                Clear();
            }

            value = bake();
            cache[key] = value;
            return value;
        }

        private static int LookOf(DemandStyle style)
        {
            return style.Contents ? 0 : (int)style.Mark;
        }

        private static Sprite Bake(DemandShape shape, int w, int h, DemandStyle style,
            int t, int r, bool fill, float reach, Vector4 border)
        {
            int num = w * h;
            byte[] alpha = DemandOutline.Alpha(shape, w, h, style, t, r, fill, reach);
            Color32[] colors = new Color32[num];
            for (int i = 0; i < num; i++)
            {
                colors[i] = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, alpha[i]);
            }

            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixels32(colors);
            tex.Apply(false, true);

            Sprite obj = Sprite.Create(tex, new Rect(0f, 0f, w, h),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            obj.hideFlags = HideFlags.HideAndDontSave;
            return obj;
        }
    }
}
