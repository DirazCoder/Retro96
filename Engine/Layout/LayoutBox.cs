using System.Collections.Generic;
using System.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Css;

namespace Retro96.Engine.Layout;

/// <summary>
/// Box types for the layout engine.
/// </summary>
public enum BoxType
{
    Block,
    Inline,
    InlineBlock,
    Anonymous,
    Table,
    TableRow,
    TableCell,
    TableCaption,
    ListItem,
    Replaced,   // images, form controls
    Frame       // <frame> / <iframe>
}

/// <summary>
/// LayoutBox represents a box in the layout tree.
/// Corresponds to CSS2 Appendix E (simplified bcz i dont need to explain, clue: just check the year goal of this project then you know).
/// </summary>
public class LayoutBox
{
    public DomElement? Element { get; init; }     // null = anonymous box
    public BoxType     BoxType  { get; init; }
    public List<LayoutBox> Children { get; } = [];
    public LayoutBox?  Parent   { get; set; }

    // Content rectangle (top-left is document-relative)
    public float X, Y, Width, Height;

    // Spacing (all in px)
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float BorderTop, BorderRight, BorderBottom, BorderLeft;

    // Derived rectangles
    public RectangleF ContentRect => new RectangleF(X, Y, Width, Height);
    public RectangleF PaddingRect => new RectangleF(
        X - PaddingLeft, Y - PaddingTop,
        Width + PaddingLeft + PaddingRight,
        Height + PaddingTop + PaddingBottom);
    public RectangleF BorderRect => new RectangleF(
        X - PaddingLeft - BorderLeft,
        Y - PaddingTop - BorderTop,
        Width + PaddingLeft + PaddingRight + BorderLeft + BorderRight,
        Height + PaddingTop + PaddingBottom + BorderTop + BorderBottom);
    public RectangleF MarginRect => new RectangleF(
        X - PaddingLeft - BorderLeft - MarginLeft,
        Y - PaddingTop - BorderTop - MarginTop,
        Width + PaddingLeft + PaddingRight + BorderLeft + BorderRight + MarginLeft + MarginRight,
        Height + PaddingTop + PaddingBottom + BorderTop + BorderBottom + MarginTop + MarginBottom);

    // Float state
    public bool IsFloated      { get; set; }
    public FloatValue FloatSide { get; set; }

    // Absolute positioning
    public bool IsAbsolutelyPositioned { get; set; }

    // For inline boxes: the text run or image they represent
    public string?   TextRun  { get; set; }
    public Image?    ReplacedImage { get; set; }

    // Hit-test: is point (px, py) inside the border box?
    public bool HitTest(float px, float py)
    {
        var rect = BorderRect;
        return px >= rect.Left && px <= rect.Right &&
               py >= rect.Top && py <= rect.Bottom;
    }

    // Constructor
    public LayoutBox(DomElement? element, BoxType boxType)
    {
        Element = element;
        BoxType = boxType;
    }

    /// <summary>
    /// Get all descendant boxes (depth-first pre-order).
    /// </summary>
    public IEnumerable<LayoutBox> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    /// <summary>
    /// Find the box containing the given point.
    /// </summary>
    public LayoutBox? BoxAtPoint(float px, float py)
    {
        // Check children first (they're on top)
        foreach (var child in Children.AsReadOnly().Reverse())
        {
            var found = child.BoxAtPoint(px, py);
            if (found != null)
                return found;
        }

        // Check this box
        if (HitTest(px, py))
            return this;

        return null;
    }

    /// <summary>
    /// Get the containing block for this box.
    /// </summary>
    public LayoutBox? GetContainingBlock()
    {
        var current = Parent;
        while (current != null)
        {
            if (current.BoxType == BoxType.Block ||
                current.BoxType == BoxType.Table ||
                current.BoxType == BoxType.TableCell ||
                current.BoxType == BoxType.TableCaption ||
                current.BoxType == BoxType.ListItem)
            {
                return current;
            }
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Get the next sibling box.
    /// </summary>
    public LayoutBox? GetNextSibling()
    {
        if (Parent == null)
            return null;
        int idx = Parent.Children.IndexOf(this);
        if (idx < 0 || idx >= Parent.Children.Count - 1)
            return null;
        return Parent.Children[idx + 1];
    }

    /// <summary>
    /// Get the previous sibling box.
    /// </summary>
    public LayoutBox? GetPreviousSibling()
    {
        if (Parent == null)
            return null;
        int idx = Parent.Children.IndexOf(this);
        if (idx <= 0)
            return null;
        return Parent.Children[idx - 1];
    }
}
