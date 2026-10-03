using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Dumps the layout box tree in source (DOM child) order, alongside each
/// box's final computed geometry, so ordering bugs — floats landing above
/// content that precedes them in the document, inline runs flushed out of
/// sequence, tables jumping ahead of siblings — show up as a scan-down-the-
/// list problem instead of a squint-at-pixels problem.
///
/// This does not depend on GDI+/WinForms, so it can be unit-tested or run
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
        bool OutOfOrder,       // true if Y went backwards vs. the previous sibling at this depth
        bool NeverLaidOut,     // true if X=Y=Width=Height=0 — box was never positioned/sized at all
        bool HasZeroSizedNonEmptyChildren); // true if this box has children but W=H=0 itself — likely a layout recursion bug, not just an empty <br>

    /// <summary>
    /// Walk the box tree and produce one row per box, depth-first, in the
    /// same order the boxes appear in their parent's Children list (i.e.
    /// DOM/source order — NOT re-sorted by Y or anything else).
    /// </summary>
    public static List<Row> Dump(LayoutBox root)
    {
        var rows = new List<Row>();
        Walk(root, depth: 0, rows);
        return rows;
    }

    private static void Walk(LayoutBox box, int depth, List<Row> rows)
    {
        float? prevSiblingY = null;
        for (int i = 0; i < box.Children.Count; i++)
        {
            var child = box.Children[i];

            // A float is expected to sit above/beside where it would have
            // landed in pure block flow, so don't flag floats for the
            // "went backwards" check — that's their normal, correct
            // behaviour. Only non-floated flow siblings should monotonically
            // increase in Y within the same parent.
            //
            // FIX: boxes that never got real geometry (X=Y=W=H=0 — e.g. <br>
            // elements, which InlineLayout currently doesn't size/position;
            // see the "br never gets geometry" bug this exposed) were being
            // flagged as "went backwards" on every single occurrence, since
            // comparing a real previous Y against a phantom 0 always looks
            // like a regression. That's noise about a different bug, not
            // evidence of ordering being wrong — skip zero boxes here.
            bool isZeroBox = child.X == 0f && child.Y == 0f
                           && child.Width == 0f && child.Height == 0f;

            bool outOfOrder = !child.IsFloated
                            && !isZeroBox
                            && prevSiblingY.HasValue
                            && child.Y < prevSiblingY.Value - 0.5f; // small epsilon for float rounding

            // A box with W=0/H=0 that DOES have children is a stronger
            // signal than a plain empty <br> — something with actual
            // content (e.g. a <td> with an <img> inside it) never got a
            // real size, which usually means the layout pass that should
            // have recursed into it either skipped it or bailed out early.
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

            if (!child.IsFloated && !isZeroBox)
                prevSiblingY = child.Y;

            Walk(child, depth + 1, rows);
        }
    }

    /// <summary>
    /// Render Dump() as an indented, fixed-width text table.
    ///   ** = a non-floated sibling's Y went backwards relative to its
    ///        predecessor — the "processed out of source order" bug class.
    ///   ZZ = this box has children but its own W/H is 0 — something that
    ///        should recurse into it (table/block layout) most likely
    ///        skipped or bailed out early. Stronger signal than a plain
    ///        empty leaf (e.g. <br>) being zero-sized, which is normal-ish.
    /// </summary>
    public static string DumpAsText(LayoutBox root)
    {
        var rows = Dump(root);
        var sb = new StringBuilder();
        sb.AppendLine("Depth Idx Flag Tag              BoxType      Float  X       Y       W       H");
        sb.AppendLine("───── ─── ──── ──────────────── ──────────── ────── ─────── ─────── ─────── ───────");

        foreach (var r in rows)
        {
            string indent = new string(' ', r.Depth * 2);
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
            : $"{outOfOrderCount} box(es) flagged ** — Y decreased vs. the previous non-floated sibling in the same parent. Check float placement / inline-run flush order for these.");
        sb.AppendLine(zeroSizedCount == 0
            ? "No zero-sized-but-has-children boxes detected."
            : $"{zeroSizedCount} box(es) flagged ZZ — has children but W=H=0 itself. The layout pass likely never recursed into these (check TableLayout for deeply nested/large tables first).");

        return sb.ToString();
    }

    /// <summary>
    /// Convenience: write the dump to Debug output AND to a plain text file
    /// next to the running exe.
    ///
    /// Debug.WriteLine only produces visible output when a debugger is
    /// actually attached (F5 in VS, Debug configuration) — Ctrl+F5, Release
    /// builds, or running the .exe directly all discard it silently with no
    /// error, which looks exactly like "outputs nothing" for no obvious
    /// reason. The file write has none of those conditions, so it's the
    /// reliable path — check retro96-layout-dump.txt next to the exe if the
    /// VS Output window shows nothing.
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