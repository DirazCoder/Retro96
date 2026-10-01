using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Dumps the layout box tree in source (DOM child) order alongside each
/// box's final computed geometry, so ordering bugs — floats landing above
/// content that precedes them in the document, inline runs flushed out of
/// sequence, tables jumping ahead of siblings — show up as a scan-down-the-
/// list problem instead of a squint-at-pixels problem.
///
/// Flags:
///   ** — a non-floated, non-positioned sibling's Y went backwards relative
///        to its predecessor: the "processed out of source order" bug class.
///   ZZ — the box has children but its own W/H is 0: something that should
///        have recursed into it (table/block layout) skipped or bailed early.
///
/// This has no WinForms dependency, so it can be unit-tested or run
/// from a console harness independently of the renderer.
/// </summary>
public static class LayoutDebug
{
    public readonly record struct Row(
        int Depth,
        int SourceIndex,       // index within Parent.Children — DOM/source order
        string Tag,
        BoxType BoxType,
        bool IsFloated,
        FloatValue FloatSide,
        float X, float Y, float Width, float Height,
        bool OutOfOrder,       // Y went backwards vs. the previous in-flow sibling
        bool NeverLaidOut,     // X=Y=Width=Height=0 — box was never positioned/sized
        bool HasZeroSizedNonEmptyChildren); // children exist but W=H=0 — recursion bug

    /// <summary>
    /// Walk the box tree and produce one row per box, depth-first, in the same
    /// order the boxes appear in their parent's Children list (i.e. DOM/source
    /// order — NOT re-sorted by Y).
    /// </summary>
    public static List<Row> Dump(LayoutBox root)
    {
        var rows = new List<Row>();
        if (root != null)
            Walk(root, depth: 0, rows);
        return rows;
    }

    private static void Walk(LayoutBox box, int depth, List<Row> rows)
    {
        float? prevSiblingY = null;

        for (int i = 0; i < box.Children.Count; i++)
        {
            var child = box.Children[i];

            // A float sits above/beside where pure block flow would put it —
            // that's its normal, correct behaviour, so floats are exempt
            // from the "went backwards" check.  Absolutely positioned boxes
            // are placed off-flow by design and get the same exemption.
            bool isZeroBox = child.X == 0f && child.Y == 0f
                          && child.Width == 0f && child.Height == 0f;

            bool outOfOrder = !child.IsFloated
                           && !child.IsAbsolutelyPositioned
                           && !isZeroBox
                           && prevSiblingY.HasValue
                           && child.Y < prevSiblingY.Value - 0.5f; // epsilon for float rounding

            // A box with W=0/H=0 that HAS children is a stronger signal than
            // a plain empty <br> — something with actual content never got a
            // real size, which usually means the layout pass that should
            // have recursed into it skipped it or bailed out early.
            bool hasZeroSizedNonEmptyChildren =
                child.Width == 0f && child.Height == 0f && child.Children.Count > 0;

            rows.Add(new Row(
                Depth: depth,
                SourceIndex: i,
                Tag: child.Element?.TagName ?? "(anon)",
                BoxType: child.BoxType,
                IsFloated: child.IsFloated,
                FloatSide: child.FloatSide,
                X: child.X, Y: child.Y, Width: child.Width, Height: child.Height,
                OutOfOrder: outOfOrder,
                NeverLaidOut: isZeroBox,
                HasZeroSizedNonEmptyChildren: hasZeroSizedNonEmptyChildren));

            if (!child.IsFloated && !child.IsAbsolutelyPositioned && !isZeroBox)
                prevSiblingY = child.Y;

            Walk(child, depth + 1, rows);
        }
    }

    /// <summary>
    /// Render Dump() as an indented, fixed-width text table.
    /// </summary>
    public static string DumpAsText(LayoutBox root)
    {
        var rows = Dump(root);
        var sb = new StringBuilder();
        sb.AppendLine("Depth Idx Flag Tag              BoxType      Float  X       Y       W       H");
        sb.AppendLine("───── ─── ──── ──────────────── ──────────── ────── ─────── ─────── ─────── ───────");

        foreach (var r in rows)
        {
            string indent = new string(' ', Math.Min(r.Depth, 20) * 2);
            string flag   = r.OutOfOrder ? "**" : (r.HasZeroSizedNonEmptyChildren ? "ZZ" : "  ");
            string floatS = r.IsFloated ? r.FloatSide.ToString() : "-";

            sb.AppendLine(
                $"{r.Depth,5} {r.SourceIndex,3} {flag,-4} " +
                $"{(indent + r.Tag),-18} {r.BoxType,-12} {floatS,-6} " +
                $"{r.X,7:F1} {r.Y,7:F1} {r.Width,7:F1} {r.Height,7:F1}");
        }

        int outOfOrderCount = rows.Count(r => r.OutOfOrder);
        int zeroSizedCount  = rows.Count(r => r.HasZeroSizedNonEmptyChildren);
        sb.AppendLine("───── ─── ──── ──────────────── ──────────── ────── ─────── ─────── ─────── ───────");
        sb.AppendLine(outOfOrderCount == 0
            ? "No out-of-order siblings detected."
            : $"{outOfOrderCount} box(es) flagged ** — Y decreased vs. the previous in-flow sibling in the same parent. Check float placement / inline-run flush order for these.");
        sb.AppendLine(zeroSizedCount == 0
            ? "No zero-sized-but-has-children boxes detected."
            : $"{zeroSizedCount} box(es) flagged ZZ — has children but W=H=0 itself. The layout pass likely never recursed into these (check TableLayout for deeply nested/large tables first).");

        return sb.ToString();
    }

    /// <summary>
    /// Convenience: write the dump to Debug output AND to a plain text file
    /// next to the running exe.  Debug.WriteLine only produces visible output
    /// when a debugger is attached, so the file is the reliable path — check
    /// retro96-layout-dump.txt next to the exe if the Output window is empty.
    /// </summary>
    public static void DumpToDebugOutput(LayoutBox root)
    {
        string text = "==== LayoutBox tree dump (source order) ====" + Environment.NewLine
                    + DumpAsText(root);

        System.Diagnostics.Debug.WriteLine(text);

        try
        {
            string path = System.IO.Path.Combine(
                AppContext.BaseDirectory, "retro96-layout-dump.txt");
            System.IO.File.WriteAllText(path, text);
        }
        catch
        {
            // Best-effort — if the exe's directory isn't writable, the
            // Debug.WriteLine above still ran, so nothing is fully lost.
        }
    }
}
