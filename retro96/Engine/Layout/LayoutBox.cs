using System.Collections.Generic;
using System.Linq;
using Retro96.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Css;

namespace Retro96.Engine.Layout;

public enum BoxType
{
    Inline,
    InlineBlock,
    Block,
    ListItem,
    Replaced,
    Frame,
    Anonymous,
    Table,
    TableRow,
    TableCell,
    TableCaption
}

/// <summary>
/// A box in the layout tree.
///
/// Coordinate model used throughout the engine:
///   box.X / box.Y  = border-box origin (document-relative; margins external)
///   box.Width      = content width   (excludes margins/borders/padding)
///   box.Height     = content height  (excludes margins/borders/padding)
///
/// Derived rectangles (correctly nested):
///   content box : origin (X + BorderLeft + PaddingLeft, Y + BorderTop + PaddingTop), size (Width, Height)
///   padding box : content + padding
///   border  box : padding + border, origin (X, Y)
///   margin  box : border + margin, origin (X - MarginLeft, Y - MarginTop)
/// </summary>
public class LayoutBox
{
    public DomElement? Element { get; init; }     // null = anonymous box
    public BoxType BoxType { get; init; }
    public List<LayoutBox> Children { get; } = [];
    public LayoutBox? Parent { get; set; }

    // Position (border-box origin) and content size
    public float X, Y, Width, Height;
    public ComputedStyle? StyleOverride { get; set; }
    public bool ShrinkToFitCell { get; set; }

    // Spacing (px)
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float? MarginTopPercent, MarginRightPercent, MarginBottomPercent, MarginLeftPercent;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float? PaddingTopPercent, PaddingRightPercent, PaddingBottomPercent, PaddingLeftPercent;
    public float BorderTop, BorderRight, BorderBottom, BorderLeft;

    // CSS1 auto margins / percentage sizes — resolved during layout (see
    // LayoutEngine.ApplyStylesToBox and LayoutBlockChildren).
    public bool MarginLeftAuto, MarginRightAuto;
    public float? StyleWidthPercent;
    public bool ListMarkerInside;

    public RectangleF ContentRect => new(
        X + BorderLeft + PaddingLeft,
        Y + BorderTop + PaddingTop,
        Width, Height);

    public RectangleF PaddingRect => new(
        X + BorderLeft, Y + BorderTop,
        Width + PaddingLeft + PaddingRight,
        Height + PaddingTop + PaddingBottom);

    public RectangleF BorderRect => new(
        X, Y,
        Width + PaddingLeft + PaddingRight + BorderLeft + BorderRight,
        Height + PaddingTop + PaddingBottom + BorderTop + BorderBottom);

    public RectangleF MarginRect => new(
        X - MarginLeft, Y - MarginTop,
        Width + PaddingLeft + PaddingRight + BorderLeft + BorderRight + MarginLeft + MarginRight,
        Height + PaddingTop + PaddingBottom + BorderTop + BorderBottom + MarginTop + MarginBottom);

    // Float state
    public bool IsFloated { get; set; }
    public FloatValue FloatSide { get; set; }

    public bool IsAbsolutelyPositioned { get; set; }

    // Inline content: text run or image
    public string? TextRun { get; set; }
    public Image? ReplacedImage { get; set; }

    public bool HitTest(float px, float py)
    {
        var rect = BorderRect;
        return px >= rect.Left && px <= rect.Right &&
               py >= rect.Top && py <= rect.Bottom;
    }

    public LayoutBox(DomElement? element, BoxType boxType)
    {
        Element = element;
        BoxType = boxType;
    }

    /// <summary>Descendant boxes (depth-first pre-order), excluding this box.</summary>
    public IEnumerable<LayoutBox> Descendants()
    {
        // Explicit stack: the recursive-yield version re-yielded every node
        // through each ancestor's iterator (O(n * depth)) and was walked on
        // every paint/caret tick.  Same pre-order, same results.
        if (Children.Count == 0) yield break;
        var stack = new Stack<LayoutBox>();
        for (int i = Children.Count - 1; i >= 0; i--)
            stack.Push(Children[i]);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var kids = node.Children;
            for (int i = kids.Count - 1; i >= 0; i--)
                stack.Push(kids[i]);
        }
    }

    /// <summary>Find the deepest box containing the given point.</summary>
    public LayoutBox? BoxAtPoint(float px, float py)
    {
        // Later siblings paint on top of earlier ones (no z-index in 1996),
        // so test children in REVERSE document order — the topmost-painted
        // box wins any overlap, e.g. absolutely positioned content.
        foreach (var child in Children
            .Select((c, i) => (Box: c, Index: i))
            .OrderBy(p => p.Box.Element?.Style?.ZIndex ?? 0)
            .ThenBy(p => p.Index)
            .Reverse())
        {
            var found = child.Box.BoxAtPoint(px, py);
            if (found != null)
                return found;
        }

        if (HitTest(px, py))
            return this;

        return null;
    }

    /// <summary>Nearest block/table cell ancestor that establishes a containing block.</summary>
    public LayoutBox? GetContainingBlock()
    {
        var current = Parent;
        while (current != null)
        {
            if (current.BoxType is BoxType.Block or BoxType.Anonymous
                                    or BoxType.InlineBlock
                                    or BoxType.Table or BoxType.TableCell
                                    or BoxType.TableCaption or BoxType.ListItem)
            {
                return current;
            }
            current = current.Parent;
        }
        return null;
    }

    public LayoutBox? GetNextSibling()
    {
        if (Parent == null) return null;
        int idx = Parent.Children.IndexOf(this);
        if (idx < 0 || idx >= Parent.Children.Count - 1) return null;
        return Parent.Children[idx + 1];
    }

    public LayoutBox? GetPreviousSibling()
    {
        if (Parent == null) return null;
        int idx = Parent.Children.IndexOf(this);
        if (idx <= 0) return null;
        return Parent.Children[idx - 1];
    }
}