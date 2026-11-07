using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace QoL
{
    // ---------------------------------------------------------------
    // 需求高亮的框线绘制（自绘 UI）
    // 移植自开源 MOD「Demand Highlight」(Apache 2.0, KCN-judu)
    // ---------------------------------------------------------------

    internal static class DemandPainter
    {
        private sealed class Frame
        {
            internal GameObject Root;
            internal RectTransform Rect;
            internal Image Image;
            internal RectTransform Anchor;
            internal int Seen;
            internal bool Visible;
            internal DemandStyle Style;
            internal object Shape;
            internal int Orientation;
            internal int W;
            internal int H;
            internal int Generation;
            internal bool Flipped;
            internal bool Drawn;
        }

        private const string NodeName = "DemandHighlight";
        private const int HiddenCap = 400;

        private static readonly Color Green = new Color(0.36f, 0.92f, 0.42f, 0.95f);
        private static readonly Color Amber = new Color(1f, 0.72f, 0.12f, 0.95f);
        private static readonly Color Red = new Color(1f, 0.25f, 0.22f, 0.95f);

        private static readonly Dictionary<object, Frame> frames = new Dictionary<object, Frame>();
        private static readonly List<object> scratch = new List<object>();
        private static int pass;

        internal static int Thickness = 1;
        internal static int Corner = 2;
        internal static bool Fill = true;

        private static readonly List<RectTransform> plates = new List<RectTransform>();

        internal static void Paint(bool complete)
        {
            pass++;
            List<DemandRules.Hit> hits = DemandRules.Hits;
            for (int i = 0; i < hits.Count; i++)
            {
                Item(hits[i].Element, hits[i].Style);
            }

            List<DemandRules.TabsHit> tabs = DemandRules.Tabs;
            for (int j = 0; j < tabs.Count; j++)
            {
                Tabs(tabs[j]);
            }

            if (complete)
            {
                HideUnseen();
            }
        }

        internal static void HideAll()
        {
            pass++;
            HideUnseen();
        }

        internal static void DestroyAll()
        {
            foreach (Frame value in frames.Values)
            {
                if (value.Root != null)
                {
                    Object.Destroy(value.Root);
                }
            }

            frames.Clear();
            plates.Clear();
        }

        private static Color ColorOf(DemandStyle style)
        {
            // Alarm：检查/橱窗警告一律用红，图案仍按 Mark 区分（违禁实线、赃物斜纹）。
            if (style.Alarm)
            {
                return Red;
            }

            switch (style.Mark)
            {
                case DemandMark.Stolen:
                    return Amber;
                case DemandMark.Contraband:
                    return Red;
                default:
                    return Green;
            }
        }

        private static void Item(GameItemElement element, DemandStyle style)
        {
            GameItem gameItem = element;
            GridShape shape = gameItem.shape;
            GridShape modifiedShape = gameItem.modifiedShape;
            int orientation = (shape != null ? shape.orientation : 0) * 4 +
                              (modifiedShape != null ? modifiedShape.orientation : 0);
            bool flipped = (shape != null && shape.flipped) || (modifiedShape != null && modifiedShape.flipped);

            RectTransform rect = null;
            bool measurable = false;
            if (orientation != 0)
            {
                TreeNodeRender rotator = element.rotator;
                rect = rotator != null ? rotator.rectTransform : null;
                measurable = Measurable(rect);
            }

            if (!measurable)
            {
                rect = element.rectTransform;
            }

            if (rect == null)
            {
                return;
            }

            int w = Mathf.RoundToInt(rect.rect.width);
            int h = Mathf.RoundToInt(rect.rect.height);
            if (w < 2 || h < 2)
            {
                return;
            }

            Frame frame = Ensure(element, rect);
            if (frame == null)
            {
                return;
            }

            frame.Seen = pass;
            Show(frame);

            if (!frame.Drawn || !frame.Style.Equals(style) || frame.Shape != shape ||
                frame.Orientation != orientation || frame.Flipped != flipped ||
                frame.W != w || frame.H != h || frame.Generation != DemandSprites.Generation)
            {
                DemandShape s = (shape != null && (orientation == 0 || measurable))
                    ? DemandSprites.Read(shape) : DemandShape.Full;

                if (s.Hash == DemandShape.Full.Hash)
                {
                    DrawFrame(frame, style, Corner);
                }
                else
                {
                    Draw(frame, DemandSprites.Get(s, w, h, style, Thickness, Corner, Fill),
                        style, (measurable && flipped) ? -1f : 1f);
                }

                frame.Shape = shape;
                frame.Orientation = orientation;
                frame.Flipped = flipped;
                frame.W = w;
                frame.H = h;
            }
        }

        private static void Tabs(DemandRules.TabsHit hit)
        {
            List<RectTransform> list = Plates(hit.Tabs, hit.Pages.Count);
            if (list == null)
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                DemandStyle style = default;
                GameInventory inv = hit.Pages[i];
                List<GameItem> items = inv != null ? inv.childItems : null;
                if (items != null)
                {
                    for (int j = 0; j < items.Count; j++)
                    {
                        style = DemandStyle.Max(style, DemandRules.StyleOf(items[j]));
                    }
                }

                if (style.Mark == DemandMark.None)
                {
                    continue;
                }

                RectTransform rt = list[i];
                int w = Mathf.RoundToInt(rt.rect.width);
                int h = Mathf.RoundToInt(rt.rect.height);
                if (w < 2 || h < 2)
                {
                    continue;
                }

                Frame frame = Ensure(rt, rt);
                if (frame == null)
                {
                    continue;
                }

                frame.Seen = pass;
                Show(frame);

                if (!frame.Drawn || !frame.Style.Equals(style) || frame.W != w || frame.H != h ||
                    frame.Generation != DemandSprites.Generation)
                {
                    // 标签页 plate 高度只有 10px，九宫格贴图 border 太大塞不下，
                    // 改用按实际尺寸生成的外框贴图（Simple 拉伸）。
                    Sprite sprite = DemandSprites.Get(DemandShape.Full, w, h, style,
                        Thickness, Mathf.Min(Corner, 2), Fill);
                    Draw(frame, sprite, style, 1f);
                    frame.W = w;
                    frame.H = h;
                }
            }
        }

        private static List<RectTransform> Plates(MachineryTabs tabs, int count)
        {
            RectTransform rt = tabs.rectTransform;
            if (rt == null || count <= 0)
            {
                return null;
            }

            Transform container = null;
            for (int i = 0; i < rt.childCount; i++)
            {
                Transform child = rt.GetChild(i);
                if (child != null && child.childCount == count * 2)
                {
                    container = child;
                    break;
                }
            }

            if (container == null)
            {
                return null;
            }

            plates.Clear();
            for (int j = 0; j < count; j++)
            {
                Transform child = container.GetChild(j * 2);
                RectTransform plate = child as RectTransform;
                if (plate == null)
                {
                    return null;
                }

                plates.Add(plate);
            }

            return plates;
        }

        private static void DrawFrame(Frame frame, DemandStyle style, int corner)
        {
            Image image = frame.Image;
            image.sprite = DemandSprites.Frame(style, Thickness, corner, Fill);
            image.type = (style.Mark != DemandMark.Stolen || style.Contents)
                ? Image.Type.Sliced : Image.Type.Tiled;
            image.fillCenter = true;

            Canvas canvas = image.canvas;
            float ppu = canvas != null && canvas.referencePixelsPerUnit > 0f
                ? canvas.referencePixelsPerUnit : 100f;
            image.pixelsPerUnitMultiplier = Mathf.Clamp(ppu / 100f, 0.01f, 100f);

            Finish(frame, style, 1f);
        }

        private static void Draw(Frame frame, Sprite sprite, DemandStyle style, float mirror)
        {
            frame.Image.type = Image.Type.Simple;
            frame.Image.pixelsPerUnitMultiplier = 1f;
            frame.Image.sprite = sprite;
            Finish(frame, style, mirror);
        }

        private static void Finish(Frame frame, DemandStyle style, float mirror)
        {
            frame.Image.color = ColorOf(style);
            frame.Rect.localScale = new Vector3(mirror, 1f, 1f);
            frame.Style = style;
            frame.Generation = DemandSprites.Generation;
            frame.Drawn = true;
        }

        private static Frame Ensure(object key, RectTransform anchor)
        {
            if (frames.TryGetValue(key, out Frame value))
            {
                if (value.Root == null)
                {
                    frames.Remove(key);
                    value = null;
                }
                else if (value.Anchor != anchor)
                {
                    value.Rect.SetParent(anchor, false);
                    value.Rect.SetAsLastSibling();
                    value.Anchor = anchor;
                    value.Drawn = false;
                }
            }

            if (value != null)
            {
                return value;
            }

            GameObject go = new GameObject(NodeName, typeof(RectTransform));
            RectTransform rt = go.transform as RectTransform;
            rt.SetParent(anchor, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();

            go.AddComponent<LayoutElement>().ignoreLayout = true;
            Image image = go.AddComponent<Image>();
            image.raycastTarget = false;
            image.type = Image.Type.Simple;

            value = new Frame
            {
                Root = go,
                Rect = rt,
                Image = image,
                Anchor = anchor,
                Visible = true
            };

            frames[key] = value;
            return value;
        }

        private static void Show(Frame frame)
        {
            if (!frame.Visible)
            {
                frame.Root.SetActive(true);
                frame.Visible = true;
            }
        }

        private static void HideUnseen()
        {
            scratch.Clear();
            int hidden = 0;

            foreach (KeyValuePair<object, Frame> pair in frames)
            {
                Frame value = pair.Value;
                if (value.Root == null)
                {
                    scratch.Add(pair.Key);
                }
                else if (value.Seen != pass)
                {
                    if (value.Visible)
                    {
                        value.Root.SetActive(false);
                        value.Visible = false;
                    }

                    hidden++;
                }
            }

            if (hidden > HiddenCap)
            {
                foreach (KeyValuePair<object, Frame> pair in frames)
                {
                    if (pair.Value.Root != null && !pair.Value.Visible)
                    {
                        Object.Destroy(pair.Value.Root);
                        scratch.Add(pair.Key);
                    }
                }
            }

            for (int i = 0; i < scratch.Count; i++)
            {
                frames.Remove(scratch[i]);
            }
        }

        private static bool Measurable(RectTransform rt)
        {
            return rt != null && rt.rect.width >= 1f && rt.rect.height >= 1f;
        }
    }
}
