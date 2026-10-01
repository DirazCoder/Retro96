using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Globalization;
using System.Linq;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Implements the 1996 auto table layout algorithm (pre-CSS2.1 table-layout).
///
/// Column sizing uses SkiaSharp text measurement (via InlineLayout's font
/// cache) instead of character-count estimates, so proportional fonts size
/// columns the way Netscape did.  The table's own width is shrink-to-fit by
/// default: preferred content width, capped at the available width, floored
/// at the minimum width — a table only overflows its container (horizontal
/// scroll) when even its minimum does not fit.
///
/// All sizes follow the engine box model: tableBox.Width/Height are CONTENT
/// dimensions; the border/frame lives in the Border* fields and BorderRect.
/// The BORDER attribute and WIDTH/HEIGHT attributes describe the border box.
///
/// Height runs in two phases: phase A lays out every cell's content and
/// collects natural row heights (distributing ROWSPAN cell heights across
/// their spanned rows); phase B places rows at their final Y, stretches
/// cells to their row height, and applies VALIGN (default middle, per
/// HTML 3.2) to the cell content.
/// </summary>
public static class TableLayout
{
    // FIX: read ONCE — Environment.GetEnvironmentVariable is a native call
    // and used to run per CELL in MeasureCellMin and per table in Layout.
    private static readonly string? TableDbg = Environment.GetEnvironmentVariable("TABLEDBG");

    // ─────────────────────────────────────────────────────────────────────────
    // Entry point
    // ─────────────────────────────────────────────────────────────────────────

    public static void Layout(
        LayoutBox tableBox,
        float availableWidth,
        FloatContext? inheritedFloats = null)
    {
        if (tableBox?.Element == null) return;

        // A floated table must inherit the parent float exclusions without
        // mutating the same shared flow state.  Reusing the same FloatContext
        // object across table internals causes the table's nested cell floats to
        // leak into the surrounding block flow and can collapse the exclusion
        // band before the next anonymous/block container is laid out.
        var floats = inheritedFloats?.Clone() ?? new FloatContext();

        var tableElem = tableBox.Element;

        // The table's own margins come out of the available width —
        // previously a <table align=left hspace=…> could overflow its band.
        float avail = Math.Max(0f, availableWidth - tableBox.MarginLeft - tableBox.MarginRight);

        // ── Table-level attributes ────────────────────────────────────────────
        float? tableWidthAttr = ResolveWidth(tableElem, "width", avail);
        // FIX: clamped ≥ 0 — a negative CELLSPACING/CELLPADDING attribute
        // used to flow straight into every cell's geometry.
        float cellSpacing = Math.Max(0f, GetPx(tableElem, "cellspacing", 2f));   // NN default 2
        float cellPadding = Math.Max(0f, GetPx(tableElem, "cellpadding", 1f));   // NN default 1
        float borderWidth = ResolveBorderWidth(tableElem);

        // ── Build the logical cell grid ───────────────────────────────────────
        var rows = BuildRowList(tableBox);

        // ── Caption (laid out at the final table width, top by default) ──────
        var captionBox = tableBox.Children.FirstOrDefault(c => c.BoxType == BoxType.TableCaption);

        int colCount = 0;
        foreach (var row in rows)
            foreach (var cell in row.Cells)
                colCount = Math.Max(colCount, cell.ColEnd);

        // FIX: a table with a caption but no cells (or no rows) used to
        // return before the caption was ever laid out — it vanished.
        if (colCount == 0)
        {
            if (captionBox != null)
            {
                captionBox.X = tableBox.X;
                captionBox.Y = tableBox.Y;
                captionBox.Width = Math.Max(1f, avail);
                LayoutCellContent(captionBox, avail, null, floats);
                tableBox.Height = Math.Max(0f,
                    captionBox.Height
                    + captionBox.PaddingTop + captionBox.BorderTop
                    + captionBox.PaddingBottom + captionBox.BorderBottom);
            }
            else
            {
                tableBox.Height = 0f;
            }
            return;
        }

        // ════════════════════════════════════════════════════════════════════
        // WIDTH PASS
        // ════════════════════════════════════════════════════════════════════

        var minW = new float[colCount];
        var prefW = new float[colCount];

        // Percentage cell widths describe a share of the TABLE's own
        // column space, not of the raw containing width: when the table
        // carries a WIDTH (e.g. width="100%" of its container, or
        // width=760), 25% + 75% cells must sum to exactly that table,
        // not to table + padding.  Resolve against the spec'd inner
        // width when there is one, else against the available width.
        float spacing0 = cellSpacing * (colCount + 1);
        float chrome0 = 2f * borderWidth;
        float percentBase = tableWidthAttr.HasValue
            ? Math.Max(0f, tableWidthAttr.Value - spacing0 - chrome0)
            : avail;

        // Pass 1 — single-column cells
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.ColEnd - cell.Col > 1) continue;

                int col = cell.Col;
                float minC = MeasureCellMin(cell.Box, cellPadding, borderWidth);
                float prefC = MeasureCellPref(cell.Box, cellPadding, borderWidth);

                float? expl = ResolveWidth(cell.Box.Element, "width", percentBase);
                if (expl.HasValue)
                {
                    // An explicit WIDTH is a hard constraint in the 1996
                    // algorithm (Netscape pinned such columns and let only
                    // auto columns flex): the column takes at least the
                    // spec'd size and PREF no longer drags it wider.
                    minC = Math.Max(minC, expl.Value);
                    prefC = minC;
                }

                if (col < colCount)
                {
                    minW[col] = Math.Max(minW[col], minC);
                    prefW[col] = Math.Max(prefW[col], prefC);
                }
            }
        }

        // Pass 2 — colspan cells only add the *deficit* their content creates
        // across the columns they span.  Do not divide the whole spanning-cell
        // width into every column: doing that manufactures width for columns
        // that have no single-column content.
        //
        // This matters for real-world markup such as:
        //   <tr><td colspan="3">header</td></tr>
        //   <tr><td><table width="100%">...</table></td></tr>
        //
        // Chromium collapses the otherwise-unused second and third columns, so
        // the one-cell row fills the table.  The old code gave columns 2 and 3
        // a permanent share of the header's intrinsic width, leaving a blank
        // block on the right.
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                int span = cell.ColEnd - cell.Col;
                if (span <= 1 || cell.Col >= colCount) continue;

                float minC = MeasureCellMin(cell.Box, cellPadding, borderWidth);
                float prefC = MeasureCellPref(cell.Box, cellPadding, borderWidth);

                float? expl = ResolveWidth(cell.Box.Element, "width", percentBase);
                if (expl.HasValue)
                {
                    // Pinned colspan width (same rule as pass 1).
                    minC = Math.Max(minC, expl.Value);
                    prefC = minC;
                }

                int end = Math.Min(cell.ColEnd, colCount);
                int eff = end - cell.Col;
                if (eff <= 0) continue;

                // colW includes the cell's own horizontal chrome, while the
                // table grid adds cellspacing only *between* spanned columns.
                float interColumnSpacing = cellSpacing * (eff - 1);

                float minSum = 0f;
                float prefSum = 0f;
                for (int c = cell.Col; c < end; c++)
                {
                    minSum += minW[c];
                    prefSum += prefW[c];
                }

                float minDeficit = minC - minSum - interColumnSpacing;
                if (minDeficit > 0.5f)
                    DistributeColspanDeficit(minW, cell.Col, end, minDeficit);

                float prefDeficit = prefC - prefSum - interColumnSpacing;
                if (prefDeficit > 0.5f)
                    DistributeColspanDeficit(prefW, cell.Col, end, prefDeficit);
            }
        }

        // Give an intrinsic colspan deficit to columns that already have
        // intrinsic width.  If every column is otherwise empty, keep the
        // width in the first column; the cell itself still spans the entire
        // grid, while this avoids inventing equal-width empty columns.
        static void DistributeColspanDeficit(float[] widths, int start, int end, float deficit)
        {
            float sum = 0f;
            for (int c = start; c < end; c++)
                sum += widths[c];

            if (sum <= 0.01f)
            {
                widths[start] += deficit;
                return;
            }

            for (int c = start; c < end; c++)
                widths[c] += deficit * (widths[c] / sum);
        }

        // minW ≤ prefW
        for (int c = 0; c < colCount; c++)
            prefW[c] = Math.Max(prefW[c], minW[c]);

        // ── Table width (border-box target) ──────────────────────────────────
        float spacing = cellSpacing * (colCount + 1);
        float chromeW = 2f * borderWidth;
        float totalMin = minW.Sum() + spacing + chromeW;
        float totalPref = prefW.Sum() + spacing + chromeW;

        float tableW;
        if (tableWidthAttr.HasValue)
            tableW = Math.Max(1f, tableWidthAttr.Value);
        else
        {
            // Shrink-to-fit: preferred width capped at the available width,
            // floored at the minimum.  Only when even the minimum overflows
            // does the table grow past its container (horizontal scroll).
            tableW = Math.Min(totalPref, avail);
            tableW = Math.Max(tableW, totalMin);
        }

        // TABLEDBG=1 — width-pass trace for tables that overflow their
        // container (or any table when TABLEDBG=2).
        if (TableDbg == "1" && tableW > avail + 4f || TableDbg == "2")
        {
            Console.WriteLine(
                $"[tabledbg] table@({tableBox.X:0.#},{tableBox.Y:0.#}) attr={(tableWidthAttr.HasValue ? tableWidthAttr.Value.ToString("0.#") : "auto")} " +
                $"avail={avail:0.#} totalMin={totalMin:0.#} totalPref={totalPref:0.#} tableW={tableW:0.#} cols={colCount}");
            for (int c = 0; c < colCount; c++)
                Console.WriteLine($"[tabledbg]   col {c}: min={minW[c]:0.#} pref={prefW[c]:0.#}");
        }

        float innerW = tableW - spacing - chromeW;

        float[] colW;
        if (tableW <= totalMin + 0.5f)
        {
            colW = (float[])minW.Clone();
            float availableColumns = Math.Max(0f, innerW);
            float minimumColumns = colW.Sum();
            if (minimumColumns > availableColumns + 0.5f && minimumColumns > 0f)
            {
                float scale = availableColumns / minimumColumns;
                for (int c = 0; c < colW.Length; c++)
                    colW[c] *= scale;
            }
        }
        else if (tableW >= totalPref - 0.5f)
        {
            colW = (float[])prefW.Clone();
            float surplus = innerW - prefW.Sum();
            float totalP = prefW.Sum();
            if (surplus > 0 && totalP > 0)
                for (int c = 0; c < colCount; c++)
                    colW[c] += surplus * (prefW[c] / totalP);
        }
        else
        {
            // Between min and pref: every column starts at its own minimum,
            // then the leftover width (tableW's slack over totalMin) is
            // handed out among columns in proportion to EACH COLUMN'S OWN
            // pref-minus-min spread — not a single global ratio applied to
            // every column alike.
            //
            // BUG FIX: the old code picked one ratio t = (tableW - totalMin)
            // / (totalPref - totalMin) and applied it to every column, so a
            // single wide column (a long word dominating totalPref) dragged
            // t toward 0 and squeezed every OTHER column back to its bare
            // minimum too — even columns that had plenty of slack of their
            // own and would have fit near their preferred width. That's why
            // short-text columns were wrapping/crowding despite visible
            // spare room in the row: the deficit was being paid by the whole
            // table, not by the column that actually caused it. Handing out
            // the same total leftover width per-column, weighted by that
            // column's own spread, matches what Chromium's auto-table
            // algorithm does and keeps small columns near their preferred
            // (unwrapped) width whenever the wide column is the only one
            // under pressure.
            float leftover = Math.Max(0f, tableW - totalMin);
            float totalSpread = 0f;
            for (int c = 0; c < colCount; c++)
                totalSpread += Math.Max(0f, prefW[c] - minW[c]);

            colW = new float[colCount];
            for (int c = 0; c < colCount; c++)
            {
                float spread = Math.Max(0f, prefW[c] - minW[c]);
                float share = totalSpread > 0.5f ? leftover * (spread / totalSpread) : 0f;
                colW[c] = minW[c] + Math.Min(share, spread);
            }
        }

        // Content width per the engine box model — the frame lives in Border*
        tableBox.Width = Math.Max(0f, tableW - chromeW);

        // Column X offsets (relative to the table's border-box origin)
        var colX = new float[colCount];
        {
            float x = borderWidth + cellSpacing;
            for (int c = 0; c < colCount; c++)
            {
                colX[c] = x;
                x += colW[c] + cellSpacing;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // HEIGHT PASS A — natural row heights (provisional Y positions)
        // ════════════════════════════════════════════════════════════════════

        int nRows = rows.Count;
        var rowH = new float[nRows];
        var rowSpanCells = new List<(int StartRow, int RowSpan, float OuterHeight)>();

        // Top caption is laid out at the final width before rows are placed.
        // The caption needs a real Y BEFORE its content lays out.
        float topCaptionH = 0f, bottomCaptionH = 0f;
        if (captionBox != null)
        {
            bool captionAtBottom = IsCaptionAtBottom(captionBox);
            float capW = Math.Max(1f, tableW - 2f * borderWidth);
            captionBox.X = tableBox.X + borderWidth;
            captionBox.Y = tableBox.Y;
            captionBox.Width = capW;
            LayoutCellContent(captionBox, capW, null);
            float capOuterH = captionBox.Height
                + captionBox.PaddingTop + captionBox.BorderTop
                + captionBox.PaddingBottom + captionBox.BorderBottom;
            if (captionAtBottom) bottomCaptionH = capOuterH;
            else topCaptionH = capOuterH;
        }

        float gridTop = tableBox.Y + topCaptionH + borderWidth + cellSpacing;
        float provisionalY = gridTop;

        for (int r = 0; r < nRows; r++)
        {
            var row = rows[r];

            foreach (var cell in row.Cells)
            {
                var box = cell.Box;
                int end = Math.Min(cell.ColEnd, colCount);
                if (cell.Col >= colCount) continue;

                float cellW = 0f;
                for (int c = cell.Col; c < end; c++)
                    cellW += colW[c];
                cellW += cellSpacing * (end - cell.Col - 1);

                // Keep CSS borders declared on the cell. The table border is
                // only the fallback for cells without their own CSS border.
                var cellStyle = box.Element?.Style;
                float cellBorderLeft = cellStyle?.OwnBorderLeftStyle == true &&
                                       cellStyle.BorderLeftStyle == BorderStyleValue.None
                    ? 0f : (box.BorderLeft > 0f ? box.BorderLeft : borderWidth);
                float cellBorderRight = cellStyle?.OwnBorderRightStyle == true &&
                                        cellStyle.BorderRightStyle == BorderStyleValue.None
                    ? 0f : (box.BorderRight > 0f ? box.BorderRight : borderWidth);
                float cellBorderTop = cellStyle?.OwnBorderTopStyle == true &&
                                      cellStyle.BorderTopStyle == BorderStyleValue.None
                    ? 0f : (box.BorderTop > 0f ? box.BorderTop : borderWidth);
                float cellBorderBottom = cellStyle?.OwnBorderBottomStyle == true &&
                                         cellStyle.BorderBottomStyle == BorderStyleValue.None
                    ? 0f : (box.BorderBottom > 0f ? box.BorderBottom : borderWidth);

                box.X = tableBox.X + colX[cell.Col];
                box.Y = provisionalY;
                box.Width = Math.Max(0f, cellW - 2f * cellPadding
                                                   - cellBorderLeft - cellBorderRight);
                box.PaddingLeft = cellPadding;
                box.PaddingRight = cellPadding;
                box.PaddingTop = cellPadding;
                box.PaddingBottom = cellPadding;
                box.BorderLeft = cellBorderLeft;
                box.BorderRight = cellBorderRight;
                box.BorderTop = cellBorderTop;
                box.BorderBottom = cellBorderBottom;

                // Row-level ALIGN inherits into cells that don't set their own
                var styleOverride = InheritRowAlign(box, row.RowBox?.Element);

                LayoutCellContent(box, cellW, styleOverride, floats);

                // Explicit <td height=N> (pixels)
                float hAttr = GetPx(box.Element, "height", 0f);
                if (hAttr > 0f)
                    box.Height = Math.Max(box.Height, hAttr - 2f * cellPadding
                                                     - cellBorderTop - cellBorderBottom);

                float outerCellH = box.Height + 2f * cellPadding
                                             + cellBorderTop + cellBorderBottom;

                // ROWSPAN cells do NOT dump their whole height on the first
                // spanned row — the requirement is distributed after the loop.
                if (cell.RowSpan > 1)
                    rowSpanCells.Add((cell.RowIndex, cell.RowSpan, outerCellH));
                else
                    rowH[r] = Math.Max(rowH[r], outerCellH);
            }

            // Minimum row height from <tr height=N>
            float trH = GetPx(row.RowBox?.Element, "height", 0f);
            if (trH > 0f) rowH[r] = Math.Max(rowH[r], trH);

            provisionalY += rowH[r] + cellSpacing;
        }

        // Distribute rowspan cell heights across their spanned rows so each
        // spanned row grows by (deficit / spanRows) — the Netscape behaviour.
        foreach (var (startRow, rowSpan, outerH) in rowSpanCells)
        {
            int end = Math.Min(startRow + rowSpan, nRows) - 1;
            if (end < startRow) continue;

            float spanned = cellSpacing * (end - startRow);
            for (int k = startRow; k <= end; k++)
                spanned += rowH[k];

            if (outerH > spanned + 0.5f)
            {
                float add = (outerH - spanned) / (end - startRow + 1);
                for (int k = startRow; k <= end; k++)
                    rowH[k] += add;
            }
        }

        // ── Table HEIGHT attribute — distribute surplus evenly over rows ────
        float naturalGrid = nRows > 0
            ? rowH.Sum() + cellSpacing * (nRows - 1)
            : 0f;
        float tableHAttr = GetPx(tableElem, "height", 0f);
        if (tableHAttr > naturalGrid + 2f * borderWidth + 2f * cellSpacing && nRows > 0)
        {
            float extra = (tableHAttr - naturalGrid - 2f * borderWidth - 2f * cellSpacing) / nRows;
            for (int r = 0; r < nRows; r++)
                rowH[r] += extra;
        }

        // ════════════════════════════════════════════════════════════════════
        // HEIGHT PASS B — final placement, rowspan, VALIGN
        // ════════════════════════════════════════════════════════════════════

        float y = gridTop;

        for (int r = 0; r < nRows; r++)
        {
            var row = rows[r];

            foreach (var cell in row.Cells)
            {
                var box = cell.Box;
                if (cell.Col >= colCount) continue;   // out-of-grid stray: no position

                // Shift the cell (and its content tree) from its provisional Y
                // to the final row Y — needed when height distribution moved
                // rows around.
                float dy = y - box.Y;
                if (Math.Abs(dy) > 0.5f)
                    OffsetChildren(box, 0f, dy);
                box.Y = y;

                // Vertical extent this cell must fill:
                //   rowspan 1  → this row's height
                //   rowspan N  → sum of the spanned rows' heights + the
                //                cellspacing between them
                int endRow = Math.Min(cell.RowIndex + cell.RowSpan, nRows) - 1;
                float spanH = 0f;
                for (int k = cell.RowIndex; k <= endRow; k++)
                    spanH += rowH[k];
                spanH += cellSpacing * (endRow - cell.RowIndex);
                float outerH = Math.Max(rowH[r], spanH);

                float contentH = Math.Max(0f, outerH - 2f * cellPadding - 2f * borderWidth);

                // Content extent BEFORE stretching — the VALIGN slack
                // comes from the difference between this and the final height.
                float contentExtent = box.Height;
                if (box.Height < contentH)
                    box.Height = contentH;

                // VALIGN — cell attribute beats the row's; default MIDDLE
                // (HTML 3.2 / Netscape behaviour — the reason every 1996
                // layout table writes valign=top explicitly).
                string vAlign = ResolveVAlign(box.Element, row.RowBox?.Element);
                if (vAlign != "top")
                {
                    float slack = Math.Max(0f, contentH - contentExtent);
                    float shift = vAlign switch
                    {
                        "bottom" => slack,
                        "baseline" => 0f,       // baseline ≈ top for block content
                        _ => slack / 2f   // middle
                    };
                    if (shift > 0.5f)
                        OffsetChildren(box, 0f, shift);
                }
            }

            y += rowH[r] + cellSpacing;
        }

        // Bottom caption sits under the grid
        if (captionBox != null && bottomCaptionH > 0f)
        {
            float dy = (y + cellSpacing) - captionBox.Y;
            if (Math.Abs(dy) > 0.5f)
                OffsetChildren(captionBox, 0f, dy);
            captionBox.Y = y + cellSpacing;
        }
        if (captionBox != null)
        {
            // Centered horizontally (NN default; left/right honour ALIGN)
            string capAlign = captionBox.Element?.GetAttrOrDefault("align", "").Trim().ToLowerInvariant() ?? "";
            float outerW = captionBox.Width;
            float newX = capAlign switch
            {
                "left" => tableBox.X + borderWidth,
                "right" => tableBox.X + tableW - borderWidth - outerW,
                _ => tableBox.X + (tableW - outerW) / 2f
            };
            float dx = newX - captionBox.X;
            if (Math.Abs(dx) > 0.5f)
                OffsetChildren(captionBox, dx, 0f);
            captionBox.X = newX;
        }

        // Total height: caption(s) + grid + trailing spacing + bottom border,
        // minus the borders — Height is the CONTENT height per the box model.
        float borderBoxH = (y + cellSpacing + bottomCaptionH) - tableBox.Y + borderWidth;
        tableBox.Height = Math.Max(0f, borderBoxH - 2f * borderWidth);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cell content layout (mini block formatter for inside a cell)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lays out a cell's (or caption's) content.  One pass over the children
    /// in source order: consecutive inline/replaced children are batched into
    /// runs flushed through InlineLayout right before the next block-level
    /// sibling — the same discipline LayoutBlock uses, so images flow with
    /// the text around them instead of being dumped after all blocks.
    /// Floats are placed and registered into the cell's own FloatContext.
    /// </summary>
    private static void LayoutCellContent(
        LayoutBox cell, float cellOuterWidth, ComputedStyle? styleOverride,
        FloatContext? inheritedFloats = null)
    {
        float contentW = Math.Max(0f,
            cellOuterWidth
            - cell.PaddingLeft - cell.PaddingRight
            - cell.BorderLeft - cell.BorderRight);

        float contentX = cell.X + cell.BorderLeft + cell.PaddingLeft;
        float contentY = cell.Y + cell.BorderTop + cell.PaddingTop;
        float currentY = contentY;

        var floats = inheritedFloats ?? new FloatContext();
        var pendingInline = new List<LayoutBox>();
        var cellStyle = styleOverride
                     ?? cell.Element?.Style
                     ?? cell.Parent?.Element?.Style
                     ?? new ComputedStyle();

        Retro96.DebugLog.Write($"[aligndbg] tag={cell.Element?.TagName} " +
            $"align_attr={cell.Element?.GetAttr("align") ?? "(none)"} " +
            $"styleOverride={(styleOverride == null ? "null" : styleOverride.TextAlign.ToString())} " +
            $"cellStyle.TextAlign(before center-guard)={cellStyle.TextAlign} " +
            $"OwnTextAlign={cellStyle.OwnTextAlign} " +
            $"elementStyleRefEquals={ReferenceEquals(cell.Element?.Style, cellStyle)}");

        // A surrounding <center> controls the table's placement, but it
        // must not center ordinary cell contents. Chromium resets the cell's
        // default inline alignment to left unless the cell or its row
        // explicitly supplies ALIGN/text-align.
        if (styleOverride == null && cell.Element != null &&
            !cell.Element.HasAttr("align") && !cellStyle.OwnTextAlign)
        {
            cellStyle = cellStyle.Clone();
            cellStyle.TextAlign = TextAlign.Left;
            Retro96.DebugLog.Write($"[aligndbg] tag={cell.Element?.TagName} " +
                "=> RESET to Left by center-guard");
        }

        Retro96.DebugLog.Write($"[aligndbg] tag={cell.Element?.TagName} " +
            $"FINAL cellStyle.TextAlign={cellStyle.TextAlign}");

        void FlushInlineRun()
        {
            if (pendingInline.Count == 0) return;

            if (!IsWhitespaceRun(pendingInline))
            {
                float inlineH = InlineLayout.Layout(
                    pendingInline, contentW, contentX, currentY, cellStyle, floats);
                currentY += inlineH;
            }
            pendingInline.Clear();
        }

        foreach (var child in cell.Children)
        {
            if (child.IsAbsolutelyPositioned) continue;

            // Floated children directly inside the cell.
            if (child.IsFloated)
            {
                PlaceCellFloat(child, contentX, currentY, contentW, floats);
                floats.AddFloat(child);
                continue;
            }

            bool isBlockLevel = child.Element?.TagName == "hr"
                              || child.BoxType is BoxType.Block or BoxType.ListItem
                                                   or BoxType.Anonymous or BoxType.Table;

            if (!isBlockLevel)
            {
                pendingInline.Add(child);
                continue;
            }

            FlushInlineRun();

            child.X = contentX + child.MarginLeft;
            child.Y = currentY + child.MarginTop;

            // Re-resolve percentage WIDTH now that the real containing
            // width is known (generation-time resolution used the body width)
            if (child.Element != null)
            {
                string? wAttr = child.Element.GetAttr("width");
                if (!string.IsNullOrEmpty(wAttr) && wAttr.TrimEnd().EndsWith('%') &&
                    float.TryParse(wAttr.TrimEnd().TrimEnd('%'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float pctVal))
                {
                    float resolved = contentW * pctVal / 100f
                                   - child.MarginLeft - child.MarginRight
                                   - child.BorderLeft - child.BorderRight
                                   - child.PaddingLeft - child.PaddingRight;
                    if (resolved > 0f) child.Width = resolved;
                }
            }

            if (child.Width <= 0f)
                child.Width = Math.Max(0f,
                    contentW - child.MarginLeft - child.MarginRight
                             - child.BorderLeft - child.BorderRight
                             - child.PaddingLeft - child.PaddingRight);

            if (child.BoxType is BoxType.Block or BoxType.ListItem or BoxType.Anonymous)
                LayoutInnerBlock(child, child.Width + child.PaddingLeft + child.PaddingRight
                                        + child.BorderLeft + child.BorderRight,
                                 cellStyle, floats);
            else if (child.BoxType == BoxType.Table)
                Layout(child, contentW, floats);

            AlignBlockChild(cell, child, contentX, contentW, cellStyle);

            currentY = child.Y
                + child.BorderTop + child.PaddingTop
                + child.Height
                + child.PaddingBottom + child.BorderBottom
                + child.MarginBottom;
        }

        FlushInlineRun();

        // A table cell establishes its own block formatting context, so it
        // must grow to CONTAIN its floats.  FloatBottoms() are absolute Y
        // coordinates, so measure them from the content top.
        float floatBottom = currentY;
        foreach (var cf in cell.Children)
        {
            if (!cf.IsFloated || cf.IsAbsolutelyPositioned) continue;
            float b = cf.Y + cf.BorderTop + cf.PaddingTop + cf.Height
                    + cf.PaddingBottom + cf.BorderBottom + cf.MarginBottom;
            if (b > floatBottom) floatBottom = b;
        }

        cell.Height = Math.Max(0f, floatBottom - contentY);
    }

    /// <summary>
    /// Lays out a block child inside a cell.  Floats inside cells are placed
    /// at the current Y (never above content that precedes them).
    /// </summary>
    private static void LayoutInnerBlock(
        LayoutBox box, float outerWidth, ComputedStyle containerStyle,
        FloatContext? inheritedFloats = null)
    {
        // A block child of a cell gets its content width fixed already; lay
        // out ITS children with the same inline-run discipline.
        var blockStyle = box.Element?.Style ?? containerStyle;
        if (containerStyle.TextAlign == TextAlign.Left && box.Element != null &&
            box.Element.TagName != "center" && !box.Element.HasAttr("align") &&
            !blockStyle.OwnTextAlign)
            blockStyle = containerStyle;
        float contentW = Math.Max(0f,
            outerWidth - box.BorderLeft - box.BorderRight
                       - box.PaddingLeft - box.PaddingRight);
        float contentX = box.X + box.BorderLeft + box.PaddingLeft;
        float contentY = box.Y + box.BorderTop + box.PaddingTop;
        float currentY = contentY;

        var floats = inheritedFloats ?? new FloatContext();
        var pendingInline = new List<LayoutBox>();

        void FlushInlineRun()
        {
            if (pendingInline.Count == 0) return;
            if (!IsWhitespaceRun(pendingInline))
            {
                float inlineH = InlineLayout.Layout(
                    pendingInline, contentW, contentX, currentY, blockStyle, floats);
                currentY += inlineH;
            }
            pendingInline.Clear();
        }

        foreach (var child in box.Children)
        {
            if (child.IsAbsolutelyPositioned) continue;

            if (child.IsFloated)
            {
                PlaceCellFloat(child, contentX, currentY, contentW, floats);
                floats.AddFloat(child);
                continue;
            }

            bool isBlockLevel = child.Element?.TagName == "hr"
                              || child.BoxType is BoxType.Block or BoxType.ListItem
                                                   or BoxType.Anonymous or BoxType.Table;

            if (!isBlockLevel)
            {
                pendingInline.Add(child);
                continue;
            }

            FlushInlineRun();

            child.X = contentX + child.MarginLeft;
            child.Y = currentY + child.MarginTop;

            // Re-resolve percentage widths against this cell's content box.
            // Generation happened before the containing cell had its final
            // width, so retaining that provisional width makes nested blocks
            // use the page width and overflow their cell.
            if (child.StyleWidthPercent is { } cssPct)
                child.Width = Math.Max(0f, contentW * cssPct / 100f);
            else if (child.Element?.GetAttr("width") is { } htmlWidth &&
                     htmlWidth.TrimEnd().EndsWith('%') &&
                     float.TryParse(htmlWidth.TrimEnd().TrimEnd('%'),
                         NumberStyles.Float, CultureInfo.InvariantCulture, out float htmlPct))
                child.Width = Math.Max(0f, contentW * htmlPct / 100f
                    - child.MarginLeft - child.MarginRight
                    - child.BorderLeft - child.BorderRight
                    - child.PaddingLeft - child.PaddingRight);

            if (child.Width <= 0f)
                child.Width = Math.Max(0f,
                    contentW - child.MarginLeft - child.MarginRight
                             - child.BorderLeft - child.BorderRight
                             - child.PaddingLeft - child.PaddingRight);

            if (child.BoxType is BoxType.Block or BoxType.ListItem or BoxType.Anonymous)
                LayoutInnerBlock(child, child.Width + child.PaddingLeft + child.PaddingRight
                                        + child.BorderLeft + child.BorderRight,
                                 blockStyle, floats);
            else if (child.BoxType == BoxType.Table)
                Layout(child, contentW, floats);

            AlignBlockChild(box, child, contentX, contentW, blockStyle);

            currentY = child.Y
                + child.BorderTop + child.PaddingTop
                + child.Height
                + child.PaddingBottom + child.BorderBottom
                + child.MarginBottom;
        }

        FlushInlineRun();

        if (box.Height <= 0f)
            box.Height = Math.Max(0f, currentY - contentY);
    }

    /// <summary>
    /// Applies legacy block-level horizontal alignment inside a table cell.
    /// The normal block formatter has the same rule, but table cells use this
    /// dedicated mini-formatter. In particular, <center><table> and
    /// <table align="center"> must centre a shrink-to-fit table instead of
    /// leaving it at the cell's left edge. <hr> also follows the legacy
    /// block-alignment path instead of being treated as inline content.
    /// </summary>
    private static void AlignBlockChild(
        LayoutBox parent, LayoutBox child, float contentX, float contentW,
        ComputedStyle parentStyle)
    {
        if (child.Width <= 0f) return;

        string? selfAlign = child.Element?.GetAttr("align")?.Trim().ToLowerInvariant();
        bool centerParent = parent.Element?.TagName == "center"
                         || parentStyle.TextAlign == TextAlign.Center;
        bool centerChild = selfAlign == "center"
                        || (selfAlign == null && child.Element?.TagName == "hr");
        bool hasAutoMargins = child.MarginLeftAuto || child.MarginRightAuto;

        float outerW = child.MarginLeft
                     + child.BorderLeft + child.PaddingLeft
                     + child.Width
                     + child.PaddingRight + child.BorderRight
                     + child.MarginRight;

        float newX = child.X;
        if (!hasAutoMargins && (centerParent || centerChild))
        {
            if (outerW < contentW - 1f)
                newX = contentX + (contentW - outerW) / 2f + child.MarginLeft;
        }
        else if (!hasAutoMargins && selfAlign == "right")
        {
            if (outerW < contentW - 1f)
                newX = contentX + contentW - outerW + child.MarginLeft;
        }

        float dx = newX - child.X;
        if (Math.Abs(dx) > 0.5f)
            OffsetTree(child, dx, 0f);
    }

    /// <summary>Float placement inside a cell.</summary>
    private static void PlaceCellFloat(
        LayoutBox box, float contentX, float currentY,
        float contentW, FloatContext floats)
    {
        // Provisional position: the subtree must lay out at coordinates in
        // the right neighbourhood, then shift by the delta once the final
        // spot is known (the final position depends on the measured size —
        // moving only the box used to leave the content at the old spot).
        float provX = contentX;
        float provY = currentY;
        box.X = provX;
        box.Y = provY;

        if (box.BoxType == BoxType.Table)
        {
            Layout(box, contentW, floats);
        }
        else
        {
            if (box.Width <= 0f)
                box.Width = Math.Max(20f, contentW / 3f);
            float outerW = box.Width + box.MarginLeft + box.MarginRight
                                 + box.BorderLeft + box.BorderRight
                                 + box.PaddingLeft + box.PaddingRight;
            LayoutInnerBlock(box, outerW, new ComputedStyle());
        }

        float boxW = box.BorderLeft + box.BorderRight
                   + box.PaddingLeft + box.PaddingRight
                   + box.Width;

        // FIX: margin box must fit the band (same bug as LayoutEngine.PlaceFloat).
        float fitW = boxW + box.MarginLeft + box.MarginRight;

        float top = currentY + box.MarginTop;
        float y = top;
        if (floats.HasFloats)
        {
            var candidates = new List<float> { top };
            foreach (float bottom in floats.FloatBottoms())
                if (bottom > top + 0.5f)
                    candidates.Add(bottom);
            candidates.Sort();

            bool placed = false;
            foreach (float cand in candidates)
            {
                float leftEdge = Math.Max(contentX, floats.GetLeftEdge(cand));
                float rightEdge = floats.GetRightEdge(cand);
                if (rightEdge == float.MaxValue)
                    rightEdge = contentX + contentW;
                rightEdge = Math.Min(rightEdge, contentX + contentW);
                if (rightEdge - leftEdge >= fitW) { y = cand; placed = true; break; }
            }
            if (!placed && candidates.Count > 0)
                y = candidates[^1];   // below every float
        }

        float newX;
        if (box.FloatSide == FloatValue.Right)
        {
            float boundary = contentX + contentW;
            float re = floats.GetRightEdge(y);
            if (re != float.MaxValue)
                boundary = Math.Min(boundary, re);
            newX = boundary - boxW - box.MarginRight;
        }
        else
        {
            newX = Math.Max(contentX, floats.GetLeftEdge(y)) + box.MarginLeft;
        }

        float dx = newX - provX;
        float dy = y - provY;
        box.X = newX;
        box.Y = y;
        if (Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f)
            foreach (var child in box.Children)
                OffsetTree(child, dx, dy);
    }

    /// <summary>
    /// FIX: ASCII-whitespace-only — the old string.IsNullOrWhiteSpace here
    /// classified U+00A0 (&amp;nbsp;) as droppable whitespace, the exact bug
    /// class LayoutEngine.NormaliseAndAttach already fixed for paragraphs:
    /// a cell whose only content was &amp;nbsp; rendered nothing.
    /// </summary>
    private static bool IsWhitespaceRun(List<LayoutBox> run) =>
        run.Count > 0 && run.All(b =>
            b.TextRun != null && LayoutEngine.IsAsciiWhitespaceOnly(b.TextRun));

    /// <summary>Shifts a box's children (not the box itself) by (dx, dy).</summary>
    private static void OffsetChildren(LayoutBox box, float dx, float dy)
    {
        foreach (var child in box.Children)
            OffsetTree(child, dx, dy);
    }

    private static void OffsetTree(LayoutBox box, float dx, float dy)
    {
        box.X += dx;
        box.Y += dy;

        // 1996 raster text: whole-pixel rows.  FlushLine already rounds
        // every word box, but this shift moves them by a fractional dy
        // (valign re-centring against a fractional row height) and undid
        // that rounding — mixed-font baselines then landed a pixel apart.
        if (dy != 0f && !string.IsNullOrEmpty(box.TextRun) &&
            box.BoxType == BoxType.Inline)
        {
            box.Y = MathF.Round(box.Y);
        }

        foreach (var child in box.Children)
            OffsetTree(child, dx, dy);
    }

    /// <summary>
    /// When a cell sets no ALIGN of its own but its &lt;tr&gt; does, returns a
    /// cloned style with the row's horizontal alignment applied (Netscape
    /// inherited row alignment into cells).
    /// </summary>
    private static ComputedStyle? InheritRowAlign(LayoutBox cell, DomElement? rowElem)
    {
        if (rowElem == null || cell.Element == null) return null;
        if (cell.Element.HasAttr("align")) return null;

        string? rowAlign = rowElem.GetAttr("align")?.Trim().ToLowerInvariant();
        if (rowAlign is not ("center" or "right" or "justify" or "left"))
            return null;

        var style = cell.Element.Style;
        if (style == null) return null;

        var clone = style.Clone();
        clone.TextAlign = rowAlign switch
        {
            "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            "justify" => TextAlign.Justify,
            _ => TextAlign.Left
        };
        return clone;
    }

    /// <summary>
    /// VALIGN resolution: cell attribute, then row attribute, then "middle"
    /// (the HTML 3.2 / Netscape default for cells).
    /// </summary>
    private static string ResolveVAlign(DomElement? cellElem, DomElement? rowElem)
    {
        string? v = cellElem?.GetAttr("valign")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v))
            v = rowElem?.GetAttr("valign")?.Trim().ToLowerInvariant();
        return v switch
        {
            "top" => "top",
            "bottom" => "bottom",
            "baseline" => "baseline",
            "middle" or "center" => "middle",
            _ => "middle"
        };
    }

    private static bool IsCaptionAtBottom(LayoutBox captionBox)
    {
        string align = captionBox.Element?.GetAttrOrDefault("align", "").Trim().ToLowerInvariant() ?? "";
        return align is "bottom";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cell grid construction (handles tbody/thead/tfoot nesting + tag soup)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record CellEntry(
        LayoutBox Box, int Col, int ColEnd, int RowSpan, int RowIndex);

    private sealed record RowEntry(LayoutBox? RowBox, List<CellEntry> Cells);

    /// <summary>
    /// FIX: occupancy tracking is now incremental (a col → last-occupied-row
    /// map carried across rows).  The old BuildRowCells rebuilt the occupied
    /// column set by re-scanning EVERY previous row's cells on every row —
    /// O(rows² · cells) on large tables.
    /// </summary>
    private static List<RowEntry> BuildRowList(LayoutBox tableBox)
    {
        // BUG FIX (TN-06): a <tfoot> written before <tbody> in the source —
        // legal HTML, and how hand-authored tables often declare the
        // footer early so it can be sent to the browser before the
        // (possibly long) body — used to lay out and paint in that same
        // source position. Every real engine renders thead first, tfoot
        // last, and every tbody/stray row in between IN SOURCE ORDER,
        // independent of where <tfoot> actually sits in the markup.
        // Collect the three streams separately, then concatenate in
        // render order; the occupancy map still advances by rowIndex
        // (this final, reordered position), so ROWSPAN across a
        // reordered boundary lines up with what actually gets painted.
        var theadRows = new List<RowEntry>();
        var bodyRows = new List<RowEntry>();
        var tfootRows = new List<RowEntry>();
        var occupiedThrough = new Dictionary<int, int>();   // col → last occupied row (inclusive)

        List<RowEntry> CurrentBucket(LayoutBox? group) => group?.Element?.TagName switch
        {
            "thead" => theadRows,
            "tfoot" => tfootRows,
            _ => bodyRows,
        };

        void AddRow(List<RowEntry> bucket, LayoutBox? trBox, IEnumerable<LayoutBox> cellBoxes)
        {
            // rowIndex must reflect the row's eventual position in the
            // fully reordered table (thead + body + tfoot), not its
            // position within its own bucket — otherwise a ROWSPAN cell's
            // occupancy would be computed against the wrong row count.
            int rowIndex = theadRows.Count + bodyRows.Count + tfootRows.Count;
            bucket.Add(new RowEntry(trBox,
                BuildRowCells(cellBoxes, occupiedThrough, rowIndex)));
        }

        // Tag soup: <td> directly inside <table> with no <tr> — a run of
        // stray cells becomes an implicit row (RowBox = null). Stray cells
        // always belong to the body stream: there's no row-group to place
        // them in thead/tfoot.
        List<LayoutBox>? strayCells = null;
        void FlushStray()
        {
            if (strayCells != null)
            {
                AddRow(bodyRows, null, strayCells);
                strayCells = null;
            }
        }

        foreach (var child in tableBox.Children)
        {
            if (IsRowGroup(child))
            {
                FlushStray();
                var bucket = CurrentBucket(child);
                Retro96.DebugLog.Write($"[tn06dbg] row-group <{child.Element?.TagName}> " +
                    $"-> bucket={(bucket == theadRows ? "thead" : bucket == tfootRows ? "tfoot" : "body")} " +
                    $"rowCount={child.Children.Count(IsRow)}");
                foreach (var trBox in child.Children)
                    if (IsRow(trBox))
                        AddRow(bucket, trBox, trBox.Children);
            }
            else if (IsRow(child))
            {
                FlushStray();
                Retro96.DebugLog.Write($"[tn06dbg] loose <tr> -> bucket=body");
                AddRow(bodyRows, child, child.Children);
            }
            else if (IsCell(child))
            {
                strayCells ??= new List<LayoutBox>();
                strayCells.Add(child);
            }
            else
            {
                FlushStray();
            }
        }
        FlushStray();

        Retro96.DebugLog.Write($"[tn06dbg] FINAL COUNTS thead={theadRows.Count} " +
            $"body={bodyRows.Count} tfoot={tfootRows.Count}");

        // Render order: thead, then every body/stray row in source order,
        // then tfoot — regardless of where tfoot appeared in the markup.
        var result = new List<RowEntry>(theadRows.Count + bodyRows.Count + tfootRows.Count);
        result.AddRange(theadRows);
        result.AddRange(bodyRows);
        result.AddRange(tfootRows);

        Retro96.DebugLog.Write($"[tn06dbg] RESULT ORDER ({result.Count} rows):");
        for (int i = 0; i < result.Count; i++)
        {
            var firstCellText = result[i].Cells.Count > 0
                ? DescribeCellText(result[i].Cells[0].Box)
                : "(empty row)";
            Retro96.DebugLog.Write($"[tn06dbg]   [{i}] tag={result[i].RowBox?.Element?.TagName ?? "null"} " +
                $"firstCell=\"{firstCellText}\"");
        }

        return result;
    }

    private static string DescribeCellText(LayoutBox cellBox)
    {
        foreach (var child in cellBox.Children)
        {
            if (!string.IsNullOrEmpty(child.TextRun))
                return child.TextRun.Length > 30 ? child.TextRun[..30] + "…" : child.TextRun;
        }
        return "(no text)";
    }

    private static List<CellEntry> BuildRowCells(
        IEnumerable<LayoutBox> cellBoxes,
        Dictionary<int, int> occupiedThrough,
        int rowIndex)
    {
        var row = new List<CellEntry>();

        int col = 0;
        foreach (var cellBox in cellBoxes)
        {
            if (!IsCell(cellBox)) continue;

            // Slide past columns still held by ROWSPAN cells from above.
            while (occupiedThrough.TryGetValue(col, out int until) && until >= rowIndex)
                col++;

            int colSpan = GetColSpan(cellBox.Element);
            int rowSpan = GetRowSpan(cellBox.Element);

            row.Add(new CellEntry(cellBox, col, col + colSpan, rowSpan, rowIndex));

            int lastOccupied = rowIndex + rowSpan - 1;
            for (int c = col; c < col + colSpan; c++)
                occupiedThrough[c] = lastOccupied;

            col += colSpan;
        }

        return row;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Column width measurement
    // ─────────────────────────────────────────────────────────────────────────

    private static float MeasureCellMin(LayoutBox cell, float cellPadding, float borderWidth)
    {
        float v = Math.Max(1f, MeasureMin(cell)) + 2f * cellPadding + 2f * borderWidth;
        if (TableDbg == "1" && v > 200f)
        {
            Console.WriteLine($"[cellmin] cell <{cell.Element?.TagName} width={cell.Element?.GetAttr("width")}> = {v:0.#}");
            foreach (var ch in cell.Children)
                Console.WriteLine($"[cellmin]   child <{ch.Element?.TagName ?? "text"}> type={ch.BoxType} text='{(ch.TextRun ?? "").TruncD(40)}' -> {MeasureMin(ch):0.#}");
        }
        return v;
    }

    private static string TruncD(this string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    // Small safety margin on top of the fragment-matched measurement above:
    // even with matching fragmentation, float summation order and per-call
    // font-metric rounding can still land a hair apart. A couple of spare pixels
    // costs nothing visually and removes any remaining razor-edge wraps —
    // cheaper than chasing the last fraction of a pixel of "exactness".
    private const float PrefWidthSlack = 2f;

    private static float MeasureCellPref(LayoutBox cell, float cellPadding, float borderWidth) =>
        Math.Max(1f, MeasurePref(cell)) + PrefWidthSlack + 2f * cellPadding + 2f * borderWidth;

    /// <summary>
    /// Minimum width: the widest unbreakable word (SkiaSharp measurement).
    /// PRE text never wraps, so its minimum is the full line width.
    /// </summary>
    private static float MeasureMin(LayoutBox box)
    {
        float m = 0f;

        if (!string.IsNullOrEmpty(box.TextRun))
        {
            var style = box.Element?.Style;
            if (style != null)
            {
                if (style.WhiteSpace == WhiteSpaceValue.Pre)
                {
                    m = InlineLayout.MeasureTextWidth(box.TextRun, style);
                }
                else
                {
                    foreach (var word in box.TextRun.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        m = Math.Max(m, InlineLayout.MeasureTextWidth(word, style));
                }
            }
            else
            {
                float charW = 16f * 0.55f;
                foreach (var word in box.TextRun.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    m = Math.Max(m, word.Length * charW);
            }
        }
        else if (box.BoxType is BoxType.Replaced or BoxType.InlineBlock or BoxType.Frame)
        {
            // An atomic inline box never breaks — its minimum is its FULL
            // outer width (margins/borders/padding included, so hspace and
            // BORDER count toward the column minimum).
            m = Math.Max(1f, box.Width
                + box.MarginLeft + box.MarginRight
                + box.BorderLeft + box.BorderRight
                + box.PaddingLeft + box.PaddingRight);
        }

        if (box.BoxType == BoxType.Table)
            return Math.Max(m, EstimateTableWidth(box, min: true));

        // Children: consecutive inline-level boxes sum onto one line,
        // block-level boxes stack (so they contribute their max).
        //
        // Wrap opportunities that BREAK the run:
        //   • a whitespace-only text box (soft wrap — spaces can become
        //     newlines),
        //   • <br> (hard break),
        //   • EDGE WHITESPACE: the spaces that let a line wrap live INSIDE
        //     the runs, not between them.  A child whose effective edge text
        //     is whitespace therefore opens/closes a wrap opportunity at
        //     that edge.
        // FIX: all three whitespace tests use ASCII whitespace — NBSP is
        // NOT a break opportunity, but char.IsWhiteSpace/string.IsNullOrWhiteSpace
        // classify it as one and used to split runs mid-&amp;nbsp;.
        float run = 0f;
        foreach (var child in box.Children)
        {
            if (IsBlockLevelBox(child))
            {
                m = Math.Max(m, run);
                run = 0f;
                m = Math.Max(m, MeasureMin(child));
            }
            else if (child.Element?.TagName is "br" or "wbr")
            {
                m = Math.Max(m, run);      // hard line break — close the run
                run = 0f;
            }
            else if (!string.IsNullOrEmpty(child.TextRun) &&
                     LayoutEngine.IsAsciiWhitespaceOnly(child.TextRun))
            {
                m = Math.Max(m, run);      // wrap opportunity — close the run
                run = 0f;
            }
            else
            {
                if (StartsEdgeWhitespace(child)) { m = Math.Max(m, run); run = 0f; }
                run += MeasureMin(child);
                if (EndsEdgeWhitespace(child)) { m = Math.Max(m, run); run = 0f; }
            }
        }
        if (box.Element?.TagName is "ul" or "ol" or "menu" or "dir")
        {
            m = Math.Max(m, run);
            // The list's own padding is the marker gutter in the engine's
            // Navigator-style UA rules.  Include it in min-content sizing so
            // an auto table column cannot become narrower than the marker
            // area plus the first item's unbreakable content.
            m += box.PaddingLeft + box.PaddingRight
               + box.BorderLeft + box.BorderRight
               + box.MarginLeft + box.MarginRight;
            return m;
        }

        return Math.Max(m, run);
    }

    /// <summary>Effective leading text of a box (its own run, or its first
    /// descendant's) — used to detect edge wrap opportunities.</summary>
    private static string? EdgeText(LayoutBox b, bool leading)
    {
        if (!string.IsNullOrEmpty(b.TextRun)) return b.TextRun;
        var kids = b.Children;
        if (kids == null || kids.Count == 0) return null;
        return EdgeText(leading ? kids[0] : kids[kids.Count - 1], leading);
    }

    private static bool StartsEdgeWhitespace(LayoutBox b)
    {
        var t = EdgeText(b, leading: true);
        return t != null && t.Length > 0 && LayoutEngine.IsAsciiWhitespace(t[0]);
    }

    private static bool EndsEdgeWhitespace(LayoutBox b)
    {
        var t = EdgeText(b, leading: false);
        return t != null && t.Length > 0 && LayoutEngine.IsAsciiWhitespace(t[^1]);
    }

    /// <summary>
    /// BUG FIX: a table cell's unwrapped line was measured ONE STRING AT A
    /// TIME here (a single MeasureTextWidth call), but InlineLayout.Layout
    /// lays the same text out as separate WORD and SPACE fragments, each
    /// individually Math.Ceiling'd (see InlineLayout.MeasureBox). Ceiling
    /// is superadditive: rounding five fragments up separately is always
    /// ≥ rounding their sum up once, so the fragment-summed width the line
    /// layouter actually needs is 1-3px MORE than what this function used
    /// to report as "preferred". The width pass then sized the column to
    /// the smaller, single-shot number, and every multi-word cell wrapped
    /// its last word by exactly that missing sliver — on every cell, every
    /// table, reproducibly. Simulating the same word/space fragmentation
    /// here (and ceiling each piece the same way) makes the two sides
    /// agree, so a column sized to its "preferred" width actually fits its
    /// own unwrapped content.
    /// </summary>
    private static float MeasureFragmentedTextWidth(string text, ComputedStyle style)
    {
        float total = 0f;
        int i = 0, n = text.Length;
        while (i < n)
        {
            int sp = text.IndexOf(' ', i);
            int end = sp < 0 ? n : sp;
            if (end > i)
                total += InlineLayout.MeasureTextWidth(text[i..end], style);
            if (sp < 0) break;
            total += InlineLayout.MeasureTextWidth(" ", style);
            i = sp + 1;
        }
        return total;
    }

    /// <summary>
    /// Preferred width: full unconstrained content width (SkiaSharp
    /// measurement), with the same sum-inline / max-block child discipline.
    /// </summary>
    private static float MeasurePref(LayoutBox box)
    {
        float p = 0f;

        if (!string.IsNullOrEmpty(box.TextRun))
        {
            var style = box.Element?.Style;
            if (style != null)
                p = MeasureFragmentedTextWidth(box.TextRun, style);
            else
                p = box.TextRun.Length * (16f * 0.55f);
        }
        else if (box.BoxType is BoxType.Replaced or BoxType.InlineBlock or BoxType.Frame)
        {
            p = Math.Max(1f, box.Width
                + box.MarginLeft + box.MarginRight
                + box.BorderLeft + box.BorderRight
                + box.PaddingLeft + box.PaddingRight);
        }

        if (box.BoxType == BoxType.Table)
            return Math.Max(p, EstimateTableWidth(box, min: false));

        // Preferred = longest LINE, not the whole paragraph: <br> always
        // breaks (the widest br-separated line is what Netscape measured).
        //
        // IMPORTANT: inline whitespace at a line/run edge is collapsible and
        // does not contribute to a shrink-to-fit table's intrinsic width. The
        // real inline formatter already drops a leading space and collapses
        // edge whitespace at the line boundary. Counting those anonymous
        // indentation nodes here made indented table markup wider than the
        // exact same markup written on one line, which showed up as phantom
        // empty space to the right of left-aligned cells (classic case: a
        // visitor-count badge surrounded by newlines/indentation).
        float run = 0f;
        bool runHasContent = false;
        float pendingSpace = 0f;

        foreach (var child in box.Children)
        {
            if (IsBlockLevelBox(child))
            {
                p = Math.Max(p, run);
                run = 0f;
                runHasContent = false;
                pendingSpace = 0f;
                p = Math.Max(p, MeasurePref(child));
                continue;
            }

            if (child.Element?.TagName is "br" or "wbr")
            {
                p = Math.Max(p, run);
                run = 0f;
                runHasContent = false;
                pendingSpace = 0f;
                continue;
            }

            // A whitespace-only inline box is a separator only when real
            // inline content exists on BOTH sides. Delay it until the next
            // visible child; this automatically drops leading/trailing and
            // indentation whitespace from the intrinsic width.
            if (child.TextRun != null && LayoutEngine.IsAsciiWhitespaceOnly(child.TextRun))
            {
                if (runHasContent)
                {
                    var style = child.Element?.Style;
                    pendingSpace = style != null
                        ? Math.Max(pendingSpace, InlineLayout.MeasureTextWidth(" ", style))
                        : Math.Max(pendingSpace, 16f * 0.55f);
                }
                continue;
            }

            float childWidth = MeasurePref(child);

            if (pendingSpace > 0f && runHasContent)
                run += pendingSpace;
            pendingSpace = 0f;

            // Inline wrappers may themselves begin/end with collapsible
            // whitespace. Measure their visible content without charging a
            // phantom edge space. Most normal HTML is already flattened, but
            // this keeps intrinsic sizing correct for any retained inline box.
            if (child.TextRun != null && child.TextRun.Length > 0 &&
                !LayoutEngine.IsAsciiWhitespaceOnly(child.TextRun))
            {
                var trimmed = TrimAsciiEdgeWhitespace(child.TextRun);
                if (trimmed != child.TextRun)
                {
                    var style = child.Element?.Style;
                    if (style != null)
                        childWidth = MeasureFragmentedTextWidth(trimmed, style);
                    else
                        childWidth = trimmed.Length * (16f * 0.55f);
                }
            }

            run += childWidth;
            runHasContent = childWidth > 0.01f || child.Height > 0.01f;
        }

        // List containers carry their bullet/number gutter in the UA stylesheet
        // (40px of left padding in Retro96).  That padding is part of the
        // list box's horizontal footprint even though the generic intrinsic
        // measurement above intentionally measures only its line content.
        //
        // When a list lives directly in an auto-sized <td>, omitting this
        // chrome lets the table column collapse around the text alone.  The
        // LI marker is then laid out in space the table never reserved for it:
        // bullets/numbers can be pushed outside the cell or visually lost,
        // while an adjacent column absorbs the missing width.
        // Count the list container's own horizontal padding/border/margins in
        // its intrinsic width so the table grid reserves the marker gutter.
        if (box.Element?.TagName is "ul" or "ol" or "menu" or "dir")
        {
            p = Math.Max(p, run);
            p += box.PaddingLeft + box.PaddingRight
               + box.BorderLeft + box.BorderRight
               + box.MarginLeft + box.MarginRight;
            return p;
        }

        return Math.Max(p, run);
    }

    private static string TrimAsciiEdgeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int start = 0, end = text.Length;
        while (start < end && LayoutEngine.IsAsciiWhitespace(text[start])) start++;
        while (end > start && LayoutEngine.IsAsciiWhitespace(text[end - 1])) end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    private static bool IsBlockLevelBox(LayoutBox b) =>
        b.Element?.TagName == "hr"
        || b.BoxType is
            BoxType.Block or BoxType.Table or BoxType.ListItem
            or BoxType.Anonymous or BoxType.TableRow or BoxType.TableCell
            or BoxType.TableCaption;

    /// <summary>
    /// Width estimate for a nested table while measuring an outer cell:
    /// walks the row/cell grid and takes the widest row (min) or the
    /// column-sum heuristic (pref).
    /// </summary>
    private static float EstimateTableWidth(LayoutBox tableBox, bool min)
    {
        float widest = 0f;
        // Use the same row-group-aware traversal as the real table layout.
        // Normal HTML puts rows under TBODY, so scanning only direct children
        // makes nested tables appear to have zero intrinsic width.
        foreach (var row in BuildRowList(tableBox))
        {
            float rowW = 0f;
            int col = 0;
            foreach (var entry in row.Cells)
            {
                var cell = entry.Box;
                int span = Math.Max(1, cell.Element?.GetAttrInt("colspan", 1) ?? 1);

                // Content + the cell's OWN horizontal chrome (padding and
                // border).  Content alone under-measures by 2-4px per cell:
                // a nested width=100% table then lands a few pixels short
                // of its unwrapped line, wraps the last word, and every
                // link row comes out double height.
                float chrome = cell.PaddingLeft + cell.PaddingRight +
                               cell.BorderLeft + cell.BorderRight;
                float cellW = (min ? MeasureMin(cell) : MeasurePref(cell)) + chrome;
                float? expl = ResolveWidth(cell.Element, "width", 0f);
                if (expl.HasValue) cellW = Math.Max(cellW, expl.Value);
                rowW += cellW;
                col += span;
            }
            widest = Math.Max(widest, rowW + 2f * (col + 1));
        }
        return widest;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Structural predicates
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsRowGroup(LayoutBox b) =>
        b.Element?.TagName is "tbody" or "thead" or "tfoot";

    private static bool IsRow(LayoutBox b) =>
        b.BoxType == BoxType.TableRow || b.Element?.TagName == "tr";

    private static bool IsCell(LayoutBox b) =>
        b.BoxType == BoxType.TableCell || b.Element?.TagName is "td" or "th";

    // ─────────────────────────────────────────────────────────────────────────
    // Attribute helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static int GetColSpan(DomElement? e) =>
        Math.Max(1, e?.GetAttrInt("colspan", 1) ?? 1);

    private static int GetRowSpan(DomElement? e)
    {
        int v = e?.GetAttrInt("rowspan", 1) ?? 1;
        if (v == 0) return 1_000_000;   // rowspan=0 spans to the end (era rule)
        return Math.Max(1, v);
    }

    private static float GetPx(DomElement? e, string attr, float def)
    {
        var v = e?.GetAttr(attr);
        if (string.IsNullOrWhiteSpace(v)) return def;
        return float.TryParse(v.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float r) ? r : def;
    }

    /// <summary>
    /// BORDER attribute: plain pixels, or the bare presence form
    /// (&lt;table border&gt; = 1) per HTML 3.2.  Absent = 0.
    /// </summary>
    private static float ResolveBorderWidth(DomElement e)
    {
        if (!e.HasAttr("border")) return 0f;
        var v = e.GetAttr("border");
        if (string.IsNullOrWhiteSpace(v)) return 1f;
        return float.TryParse(v.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float b) ? Math.Max(0f, b) : 1f;
    }

    /// <summary>
    /// Resolves a width attribute to pixels (plain or percentage).
    /// Returns null when the attribute is absent or unparseable.
    /// </summary>
    private static float? ResolveWidth(DomElement? e, string attr, float available)
    {
        if (e == null) return null;
        string? v = e.GetAttr(attr);
        if (string.IsNullOrEmpty(v)) return null;

        string t = v.Trim();
        if (t.EndsWith('%'))
        {
            if (float.TryParse(t[..^1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float pct) && available > 0)
                return available * pct / 100f;
            return null;
        }

        return float.TryParse(t, NumberStyles.Float,
            CultureInfo.InvariantCulture, out float px) ? px : null;
    }
}