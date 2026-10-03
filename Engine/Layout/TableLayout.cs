using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Implements the 1996 auto table layout algorithm (pre-CSS table-layout: auto).
/// Fixed: recurses through tbody/thead/tfoot, measures cell content via children,
/// lays out each cell's content after column widths are known.
/// </summary>
public static class TableLayout
{
    public static void Layout(LayoutBox tableBox, float availableWidth)
    {
        if (tableBox?.Element == null) return;

        var tableElem = tableBox.Element;

        // ── Table-level attributes ────────────────────────────────────────────
        float? tableWidthAttr = ResolveWidth(tableElem, "width", availableWidth);
        float  cellSpacing    = GetFloat(tableElem, "cellspacing", 2f);
        float  cellPadding    = GetFloat(tableElem, "cellpadding", 1f);
        float  borderWidth    = GetFloat(tableElem, "border",      0f);

        // ── Build the logical cell grid ───────────────────────────────────────
        var rows = BuildRowList(tableBox);
        if (rows.Count == 0) return;

        int colCount = rows.Max(r => r.Count > 0 ? r.Max(c => c.ColEnd) : 0);
        if (colCount == 0) return;

        // ── Compute column min / pref widths ──────────────────────────────────
        var minW  = new float[colCount];
        var prefW = new float[colCount];

        foreach (var row in rows)
        {
            foreach (var cell in row)
            {
                if ((cell.ColEnd - cell.Col) > 1) continue;   // handle multi-column span first pass

                int   col    = cell.Col;
                float minC   = MeasureCellMin(cell.Box,  cellPadding);
                float prefC  = MeasureCellPref(cell.Box, cellPadding);

                // Honour explicit <td width="N"> or <td width="N%">
                float? expl = ResolveWidth(cell.Box.Element, "width", availableWidth);
                if (expl.HasValue)
                {
                    minC  = Math.Max(minC,  expl.Value);
                    prefC = Math.Max(prefC, expl.Value);
                }

                if (col < colCount)
                {
                    minW[col]  = Math.Max(minW[col],  minC);
                    prefW[col] = Math.Max(prefW[col], prefC);
                }
            }
        }

        // Ensure minW ≤ prefW
        for (int c = 0; c < colCount; c++)
            prefW[c] = Math.Max(prefW[c], minW[c]);

        // ── Distribute table width ────────────────────────────────────────────
        float spacing  = cellSpacing * (colCount + 1);
        float totalMin  = minW.Sum()  + spacing;
        float totalPref = prefW.Sum() + spacing;

        float tableW = tableWidthAttr.HasValue
            ? Math.Max(tableWidthAttr.Value, totalMin)
            : totalMin;

        float innerW = tableW - spacing; // total space for columns

        float[] colW;
        if (tableW <= totalMin)
        {
            colW = (float[])minW.Clone();
        }
        else if (tableW >= totalPref)
        {
            // Distribute surplus proportionally by preferred width
            colW = (float[])prefW.Clone();
            float surplus = innerW - prefW.Sum();
            float totalP  = prefW.Sum();
            if (surplus > 0 && totalP > 0)
                for (int c = 0; c < colCount; c++)
                    colW[c] += surplus * (prefW[c] / totalP);
        }
        else
        {
            // Between min and pref: scale between them
            float t = (tableW - totalMin) / (totalPref - totalMin);
            colW = new float[colCount];
            for (int c = 0; c < colCount; c++)
                colW[c] = minW[c] + t * (prefW[c] - minW[c]);
        }

        tableBox.Width = tableW;

        // ── Build column X offsets ────────────────────────────────────────────
        float[] colX = new float[colCount];
        float x = tableBox.X + borderWidth + cellSpacing;
        for (int c = 0; c < colCount; c++)
        {
            colX[c] = x;
            x += colW[c] + cellSpacing;
        }

        // ── Lay out rows ──────────────────────────────────────────────────────
        float currentY = tableBox.Y + borderWidth + cellSpacing;

        foreach (var row in rows)
        {
            float rowH = 0f;

            // First pass: place and lay out each cell, collect row height
            foreach (var cell in row)
            {
                int col  = cell.Col;
                int cEnd = Math.Min(cell.ColEnd, colCount);
                if (col >= colCount) continue;

                // Cell width = sum of spanned columns + spacing between them
                float cellW = 0f;
                for (int c = col; c < cEnd; c++)
                    cellW += colW[c];
                cellW += cellSpacing * (cEnd - col - 1);

                var box = cell.Box;
                box.X            = colX[col];
                box.Y            = currentY;
                box.Width        = Math.Max(0f, cellW - 2f * cellPadding - 2f * borderWidth);
                box.PaddingLeft  = cellPadding;
                box.PaddingRight = cellPadding;
                box.PaddingTop   = cellPadding;
                box.PaddingBottom= cellPadding;
                box.BorderLeft   = borderWidth;
                box.BorderRight  = borderWidth;
                box.BorderTop    = borderWidth;
                box.BorderBottom = borderWidth;

                // Layout the cell's own block content
                LayoutCellContent(box, cellW);

                rowH = Math.Max(rowH, box.Height + 2f * cellPadding + 2f * borderWidth);
            }

            // Minimum row height from <tr height="N">
            if (row.Count > 0 && row[0].Box.Parent?.Element != null)
            {
                float? trH = ResolveWidth(row[0].Box.Parent!.Element, "height", 0f);
                if (trH.HasValue) rowH = Math.Max(rowH, trH.Value);
            }

            // Second pass: equalise row height for cells with rowspan == 1
            foreach (var cell in row)
            {
                if (cell.RowSpan == 1)
                {
                    float targetH = rowH - 2f * cellPadding - 2f * borderWidth;
                    if (cell.Box.Height < targetH)
                        cell.Box.Height = Math.Max(0f, targetH);
                }
            }

            currentY += rowH + cellSpacing;
        }

        tableBox.Height = currentY - tableBox.Y + borderWidth;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cell content layout (mini block formatter for inside a cell)
    // ─────────────────────────────────────────────────────────────────────────

    private static void LayoutCellContent(LayoutBox cell, float cellOuterWidth)
    {
        float contentW = Math.Max(0f,
            cellOuterWidth
            - cell.PaddingLeft - cell.PaddingRight
            - cell.BorderLeft  - cell.BorderRight);

        float contentX = cell.X + cell.BorderLeft + cell.PaddingLeft;
        float contentY = cell.Y + cell.BorderTop  + cell.PaddingTop;
        float currentY = contentY;

        // FIX: this used to be two separate passes over cell.Children — one
        // that walked block/anonymous/table children only (skipping inline
        // items, including <img>, which IsInline doesn't count as inline —
        // see below), then a second pass that dumped EVERY inline child
        // from the whole cell into a single InlineLayout.Layout() call using
        // whatever currentY pass 1 finished at. That throws away source
        // order exactly like the original LayoutBlock bug: a cell whose
        // content mixes block children (a nested <center>) with inline/
        // replaced children (<img>, <a>, text) as siblings — the common
        // case for 1996 "table for layout" pages — got the block child
        // positioned using a currentY that didn't account for the inline
        // siblings around it, while every inline/image child across the
        // whole cell got flattened into one run starting after ALL block
        // children had already been placed. That's what produced the
        // repeating pattern LayoutDebug flagged: images landing ~300px
        // above content that precedes them in the document, because they
        // were being sequenced by a block-only loop instead of flowing
        // with the text/links around them.
        //
        // Same fix as LayoutBlock: one pass over cell.Children in source
        // order, batching consecutive non-block children (inline AND
        // replaced/image — anything that isn't itself a block/table) into a
        // pending run, flushed through InlineLayout.Layout() right before
        // the next block-level sibling and again at the end.
        var floats = new FloatContext();
        var pendingInline = new List<LayoutBox>();
        var cellStyle = cell.Element?.Style ?? new ComputedStyle();

        void FlushInlineRun()
        {
            if (pendingInline.Count == 0) return;

            float inlineH = InlineLayout.Layout(
                pendingInline, contentW, contentX, currentY, cellStyle, floats);

            currentY += inlineH;
            pendingInline.Clear();
        }

        foreach (var child in cell.Children)
        {
            if (child.IsAbsolutelyPositioned) continue;

            bool isBlockLevel = child.BoxType is BoxType.Block or BoxType.ListItem
                                                or BoxType.Anonymous or BoxType.Table;

            if (!isBlockLevel)
            {
                // Inline content AND replaced elements (<img>, form controls)
                // both flow with the surrounding text/line-box, so both go
                // into the pending run — matches how LayoutBlock treats
                // "not block-level" as the inline-run test, rather than
                // IsInline's narrower Inline/InlineBlock-only check.
                pendingInline.Add(child);
                continue;
            }

            // Block-level child: flush whatever inline run precedes it so
            // it renders above this block, in source order.
            FlushInlineRun();

            child.X = contentX + child.MarginLeft;
            child.Y = currentY + child.MarginTop;

            if (child.Width <= 0f)
                child.Width = Math.Max(0f,
                    contentW - child.MarginLeft - child.MarginRight
                    - child.BorderLeft - child.BorderRight
                    - child.PaddingLeft - child.PaddingRight);

            if (child.BoxType is BoxType.Block or BoxType.ListItem or BoxType.Anonymous)
                LayoutCellContent(child, child.Width + child.PaddingLeft + child.PaddingRight
                                        + child.BorderLeft + child.BorderRight);
            else if (child.BoxType == BoxType.Table)
            {
                // A <table> nested inside a cell (e.g. the classic 1996
                // "table for layout inside a table for layout" pattern) has
                // its own column-width/row-height algorithm and must go
                // through TableLayout.Layout, same as LayoutBlock does for
                // top-level tables. Without this branch, the table's own
                // Width/Height and everything inside it are never computed
                // — the nested table and its whole subtree stay at 0x0
                // forever, which is exactly the ZZ pattern LayoutDebug
                // caught (td containing real content but reporting 0x0
                // itself).
                TableLayout.Layout(child, contentW);
            }

            currentY = child.Y
                + child.BorderTop   + child.PaddingTop
                + child.Height
                + child.PaddingBottom + child.BorderBottom
                + child.MarginBottom;
        }

        // Flush any trailing inline run after the last block child (or the
        // only run, if the cell has no block children at all).
        FlushInlineRun();

        cell.Height = Math.Max(0f, currentY - contentY);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cell grid construction (handles tbody / thead / tfoot nesting)
    // ─────────────────────────────────────────────────────────────────────────

    private record CellEntry(LayoutBox Box, int Col, int ColEnd, int RowSpan);

    private static List<List<CellEntry>> BuildRowList(LayoutBox tableBox)
    {
        var result = new List<List<CellEntry>>();

        // Walk direct children: could be tr (flat) or tbody/thead/tfoot (grouped)
        foreach (var child in tableBox.Children)
        {
            if (IsRowGroup(child))
            {
                foreach (var trBox in child.Children)
                    if (IsRow(trBox))
                        result.Add(BuildRowCells(trBox, result));
            }
            else if (IsRow(child))
            {
                result.Add(BuildRowCells(child, result));
            }
        }

        return result;
    }

    private static List<CellEntry> BuildRowCells(LayoutBox trBox,
                                                  List<List<CellEntry>> previous)
    {
        int rowIndex = previous.Count;
        var row      = new List<CellEntry>();

        // Seed occupied columns from above rows' rowspan
        var occupied = new HashSet<int>();
        for (int r = 0; r < previous.Count; r++)
        {
            foreach (var c in previous[r])
            {
                int spanned = c.RowSpan;
                if (r + spanned > rowIndex)
                    for (int cc = c.Col; cc < c.ColEnd; cc++)
                        occupied.Add(cc);
            }
        }

        int col = 0;
        foreach (var cellBox in trBox.Children)
        {
            if (!IsCell(cellBox)) continue;

            // Skip occupied columns
            while (occupied.Contains(col)) col++;

            int colSpan = GetColSpan(cellBox.Element);
            int rowSpan = GetRowSpan(cellBox.Element);

            row.Add(new CellEntry(cellBox, col, col + colSpan, rowSpan));

            for (int c = col; c < col + colSpan; c++)
                occupied.Add(c);

            col += colSpan;
        }

        return row;
    }

    private static bool IsRowGroup(LayoutBox b) =>
        b.Element?.TagName is "tbody" or "thead" or "tfoot"
        || b.BoxType == BoxType.TableRow && b.Children.Any(IsRow);

    private static bool IsRow(LayoutBox b) =>
        b.BoxType == BoxType.TableRow || b.Element?.TagName == "tr";

    private static bool IsCell(LayoutBox b) =>
        b.BoxType == BoxType.TableCell
        || b.Element?.TagName is "td" or "th";

    // ─────────────────────────────────────────────────────────────────────────
    // Column width measurement  (recurse into children to find content)
    // ─────────────────────────────────────────────────────────────────────────

    private static float MeasureCellMin(LayoutBox cell, float cellPadding)
    {
        float m = MeasureMin(cell);
        return Math.Max(m, 20f) + 2f * cellPadding;
    }

    private static float MeasureCellPref(LayoutBox cell, float cellPadding)
    {
        float p = MeasurePref(cell);
        return Math.Max(p, 40f) + 2f * cellPadding;
    }

    /// <summary>Minimum width = longest unbreakable word across the subtree.</summary>
    private static float MeasureMin(LayoutBox box)
    {
        float m = 0f;

        if (!string.IsNullOrEmpty(box.TextRun))
        {
            float charW = box.Element?.Style?.FontSize * 0.55f ?? 8f;
            // Minimum = longest word
            foreach (var word in box.TextRun.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                m = Math.Max(m, word.Length * charW);
        }

        foreach (var child in box.Children)
            m = Math.Max(m, MeasureMin(child));

        return m;
    }

    /// <summary>Preferred width = full unconstrained text width across the subtree.</summary>
    private static float MeasurePref(LayoutBox box)
    {
        float p = 0f;

        if (!string.IsNullOrEmpty(box.TextRun))
        {
            float charW = box.Element?.Style?.FontSize * 0.55f ?? 8f;
            p = box.TextRun.Length * charW;
        }

        if (box.ReplacedImage != null)
            p = box.Width > 0 ? box.Width : box.ReplacedImage.Width;

        // For block children sum widths; for inline children sum them on a line
        float childSum = 0f;
        foreach (var child in box.Children)
            childSum = Math.Max(childSum, MeasurePref(child));

        return Math.Max(p, childSum);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Attribute helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static int GetColSpan(DomElement? e) =>
        Math.Max(1, e?.GetAttrInt("colspan", 1) ?? 1);

    private static int GetRowSpan(DomElement? e) =>
        Math.Max(1, e?.GetAttrInt("rowspan", 1) ?? 1);

    private static float GetFloat(DomElement e, string attr, float def)
    {
        var v = e.GetAttr(attr);
        return float.TryParse(v, out float r) ? r : def;
    }

    /// <summary>
    /// Resolves a width attribute to pixels. Handles plain pixels and percentages.
    /// Returns null if the attribute is absent.
    /// </summary>
    private static float? ResolveWidth(DomElement? e, string attr, float available)
    {
        if (e == null) return null;
        string? v = e.GetAttr(attr);
        if (string.IsNullOrEmpty(v)) return null;

        if (v.EndsWith('%'))
        {
            if (float.TryParse(v.TrimEnd('%'), out float pct) && available > 0)
                return available * pct / 100f;
            return null;
        }

        return float.TryParse(v, out float px) ? px : null;
    }
}