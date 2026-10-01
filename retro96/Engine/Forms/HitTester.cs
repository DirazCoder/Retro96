using System;
using System.Linq;
using Retro96.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Css;

namespace Retro96.Engine.Layout;

/// <summary>Layout hit-testing using the same overflow clip and z-order rules as painting.</summary>
public static class HitTester
{
    private static RectangleF? IntersectClip(RectangleF? a, RectangleF b)
    {
        if (!a.HasValue) return b;
        var r = a.Value;
        float l = Math.Max(r.Left, b.Left), t = Math.Max(r.Top, b.Top);
        float rr = Math.Min(r.Right, b.Right), bb = Math.Min(r.Bottom, b.Bottom);
        return rr <= l || bb <= t ? null : new RectangleF(l, t, rr - l, bb - t);
    }

    private static bool VisibleAt(RectangleF? clip, float x, float y) => !clip.HasValue || clip.Value.Contains(x, y);

    private static RectangleF? ChildClip(LayoutBox box, RectangleF? clip)
    {
        var overflow = box.Element?.Style?.Overflow ?? OverflowValue.Visible;
        if (overflow == OverflowValue.Visible) return clip;
        return IntersectClip(clip, new RectangleF(
            box.X + box.BorderLeft,
            box.Y + box.BorderTop,
            box.Width + box.PaddingLeft + box.PaddingRight,
            box.Height + box.PaddingTop + box.PaddingBottom));
    }

    private static IOrderedEnumerable<LayoutBox> PaintOrder(LayoutBox box) =>
        box.Children.Select((child, index) => (child, index))
            .OrderBy(p => p.child.Element?.Style?.ZIndex ?? 0)
            .ThenBy(p => p.index)
            .Select(p => p.child)
            .OrderByDescending(c => c.Element?.Style?.ZIndex ?? 0)
            .ThenByDescending(c => box.Children.IndexOf(c));

    public static DomElement? ElementAt(LayoutBox box, float x, float y) => ElementAtCore(box, x, y, null);

    private static DomElement? ElementAtCore(LayoutBox box, float x, float y, RectangleF? clip)
    {
        if (box.Element?.Style is { } style &&
            (style.Visibility == VisibilityValue.Hidden || style.Display == DisplayValue.None))
            return null;
        if (!VisibleAt(clip, x, y)) return null;
        var childClip = ChildClip(box, clip);
        if (childClip == null && (box.Element?.Style?.Overflow ?? OverflowValue.Visible) != OverflowValue.Visible)
            return box.BorderRect.Contains(x, y) ? box.Element : null;

        foreach (var child in PaintOrder(box))
        {
            var hit = ElementAtCore(child, x, y, childClip);
            if (hit != null) return hit;
        }
        return box.BorderRect.Contains(x, y) && VisibleAt(clip, x, y) ? box.Element : null;
    }

    public static LayoutBox? DeepestBoxAt(LayoutBox box, float x, float y) => DeepestCore(box, x, y, null);

    private static LayoutBox? DeepestCore(LayoutBox box, float x, float y, RectangleF? clip)
    {
        if (box.Element?.Style is { } style &&
            (style.Visibility == VisibilityValue.Hidden || style.Display == DisplayValue.None))
            return null;
        if (!VisibleAt(clip, x, y)) return null;
        var childClip = ChildClip(box, clip);
        if (childClip == null && (box.Element?.Style?.Overflow ?? OverflowValue.Visible) != OverflowValue.Visible)
            return box.BorderRect.Contains(x, y) ? box : null;

        foreach (var child in PaintOrder(box))
        {
            var hit = DeepestCore(child, x, y, childClip);
            if (hit != null) return hit;
        }
        return box.BorderRect.Contains(x, y) && VisibleAt(clip, x, y) ? box : null;
    }

    public static LayoutBox? TextBoxAt(LayoutBox box, float x, float y) => TextCore(box, x, y, null);

    private static LayoutBox? TextCore(LayoutBox box, float x, float y, RectangleF? clip)
    {
        if (box.Element?.Style is { } style &&
            (style.Visibility == VisibilityValue.Hidden || style.Display == DisplayValue.None))
            return null;
        if (!VisibleAt(clip, x, y)) return null;
        var childClip = ChildClip(box, clip);
        if (childClip == null && (box.Element?.Style?.Overflow ?? OverflowValue.Visible) != OverflowValue.Visible)
            return null;

        foreach (var child in PaintOrder(box))
        {
            var hit = TextCore(child, x, y, childClip);
            if (hit != null) return hit;
        }

        var rect = box.BorderRect;
        bool contains = rect.Contains(x, y);
        if (!contains && !string.IsNullOrEmpty(box.TextRun))
        {
            rect.Inflate(0, 2);
            contains = rect.Contains(x, y);
        }
        return contains && !string.IsNullOrEmpty(box.TextRun) && VisibleAt(clip, x, y) ? box : null;
    }

    public static LayoutBox? BoxForElement(LayoutBox root, DomElement element)
    {
        foreach (var b in root.Descendants())
            if (b.Element == element) return b;
        return null;
    }
}
