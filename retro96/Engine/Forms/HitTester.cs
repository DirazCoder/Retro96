using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Pure layout-tree hit-testing — the geometric half of click dispatch,
/// extracted from the WinForms canvas so clicks can be simulated
/// headlessly.  Behaviour is byte-identical to the canvas originals:
/// zero-size boxes (tr/tbody wrappers stay 0,0,0,0) are transparent,
/// the DEEPEST box wins, and text boxes inflate by 2px vertically for
/// easier word selection.
/// </summary>
public static class HitTester
{
    /// <summary>Deepest element whose border box contains the point.</summary>
    public static DomElement? ElementAt(LayoutBox box, float x, float y)
    {
        bool contains = box.BorderRect.Contains(x, y);
        if (!contains && !(box.Width == 0f && box.Height == 0f))
            return null;

        for (int i = box.Children.Count - 1; i >= 0; i--)
        {
            var result = ElementAt(box.Children[i], x, y);
            if (result != null) return result;
        }
        return contains ? box.Element : null;
    }

    /// <summary>Deepest box (any kind) whose border box contains the point.</summary>
    public static LayoutBox? DeepestBoxAt(LayoutBox box, float x, float y)
    {
        bool contains = box.BorderRect.Contains(x, y);
        if (!contains && !(box.Width == 0f && box.Height == 0f))
            return null;

        for (int i = box.Children.Count - 1; i >= 0; i--)
        {
            var hit = DeepestBoxAt(box.Children[i], x, y);
            if (hit != null) return hit;
        }
        return contains ? box : null;
    }

    /// <summary>Deepest text-run box at the point (2px vertical tolerance).</summary>
    public static LayoutBox? TextBoxAt(LayoutBox box, float x, float y)
    {
        var rect = box.BorderRect;
        bool contains = rect.Contains(x, y);
        if (!contains && !string.IsNullOrEmpty(box.TextRun))
        {
            rect.Inflate(0, 2);
            contains = rect.Contains(x, y);
        }
        if (!contains && !(box.Width == 0f && box.Height == 0f))
            return null;

        for (int i = box.Children.Count - 1; i >= 0; i--)
        {
            var hit = TextBoxAt(box.Children[i], x, y);
            if (hit != null) return hit;
        }
        return contains && !string.IsNullOrEmpty(box.TextRun) ? box : null;
    }

    /// <summary>First box in document order bound to the given element.</summary>
    public static LayoutBox? BoxForElement(LayoutBox root, DomElement element)
    {
        foreach (var b in root.Descendants())
            if (b.Element == element) return b;
        return null;
    }
}
