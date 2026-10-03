using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using Retro96.Engine.Css;

namespace Retro96.Engine.Layout;

/// <summary>
/// Tracks left/right float edges per Y-band.
/// </summary>
public class FloatContext
{
    private readonly List<(float Y, float LeftEdge, float RightEdge)> _bands = new();

    public FloatContext()
    {
        _bands.Add((0f, 0f, float.MaxValue));
    }

    public void AddFloat(LayoutBox floatBox)
    {
        float top    = floatBox.Y;
        float bottom = floatBox.Y + floatBox.BorderTop + floatBox.PaddingTop
                     + floatBox.Height
                     + floatBox.PaddingBottom + floatBox.BorderBottom;

        if (floatBox.FloatSide == FloatValue.Left)
        {
            float edge = floatBox.X + floatBox.BorderLeft + floatBox.PaddingLeft
                       + floatBox.Width
                       + floatBox.PaddingRight + floatBox.BorderRight + floatBox.MarginRight;
            UpdateBands(top, bottom, edge, isLeft: true);
        }
        else
        {
            float edge = floatBox.X - floatBox.MarginLeft;
            UpdateBands(top, bottom, edge, isLeft: false);
        }
    }

    private void UpdateBands(float top, float bottom, float edge, bool isLeft)
    {
        EnsureBandAt(top);
        EnsureBandAt(bottom);

        for (int i = 0; i < _bands.Count; i++)
        {
            var b = _bands[i];
            if (b.Y < top || b.Y >= bottom) continue;
            float newL = isLeft  ? Math.Max(b.LeftEdge,  edge) : b.LeftEdge;
            float newR = !isLeft ? Math.Min(b.RightEdge, edge) : b.RightEdge;
            _bands[i] = (b.Y, newL, newR);
        }
    }

    private void EnsureBandAt(float y)
    {
        int idx = BandIndexAt(y);
        if (idx >= 0 && _bands[idx].Y == y) return;

        // Clone the band that covers y
        int src = idx >= 0 ? idx : 0;
        var (_, l, r) = _bands[src];
        // Insert after src
        _bands.Insert(src + 1, (y, l, r));
    }

    private int BandIndexAt(float y)
    {
        int best = -1;
        for (int i = 0; i < _bands.Count; i++)
        {
            if (_bands[i].Y <= y) best = i;
            else break;
        }
        return best;
    }

    public float GetLeftEdge(float y)
    {
        int i = BandIndexAt(y);
        return i >= 0 ? _bands[i].LeftEdge : 0f;
    }

    public float GetRightEdge(float y)
    {
        int i = BandIndexAt(y);
        return i >= 0 ? _bands[i].RightEdge : float.MaxValue;
    }

    public void Clear(float y, ClearValue clear)
    {
        int i = BandIndexAt(y);
        if (i < 0) return;
        for (int j = i; j < _bands.Count; j++)
        {
            var b = _bands[j];
            float l = (clear == ClearValue.Left || clear == ClearValue.Both) ? 0f : b.LeftEdge;
            float r = (clear == ClearValue.Right || clear == ClearValue.Both) ? float.MaxValue : b.RightEdge;
            _bands[j] = (b.Y, l, r);
        }
    }
}

/// <summary>
/// Implements the CSS inline formatting context.
/// Uses real GDI+ text measurement for correct line-breaking and box sizing.
/// </summary>
public static class InlineLayout
{
    // ── Static measurement surface ────────────────────────────────────────────
    // Set by the Renderer (or Form1) once at startup so InlineLayout can measure
    // text without needing a Graphics passed through every layout call.

    private static Retro96.Engine.Render.FontCache? _fontCache;
    private static readonly Bitmap    _measureBmp = new Bitmap(1, 1);
    private static readonly Graphics  _measureG;

    static InlineLayout()
    {
        _measureG = Graphics.FromImage(_measureBmp);
        _measureG.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
    }

    /// <summary>Call once after FontCache is created (Form1.InitializeBrowser).</summary>
    public static void SetFontCache(Retro96.Engine.Render.FontCache fc) => _fontCache = fc;

    // ── StringFormat used for all measurements ────────────────────────────────
    private static readonly StringFormat _sf = new StringFormat(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming    = StringTrimming.None
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Layout inline children into line boxes. Returns total height consumed.
    /// </summary>
    public static float Layout(
        IReadOnlyList<LayoutBox> inlineChildren,
        float containerWidth,
        float containerX,
        float startY,
        ComputedStyle containerStyle,
        FloatContext? floats)
    {
        if (inlineChildren == null || inlineChildren.Count == 0)
            return 0f;

        // Pre-measure every box so line-breaking decisions are accurate
        var measured = new List<(LayoutBox box, float w, float h, float ascent)>(inlineChildren.Count);
        foreach (var box in inlineChildren)
        {
            MeasureBox(box, out float w, out float h, out float asc);
            measured.Add((box, w, h, asc));
        }

        float currentY  = startY;
        bool  nowrap    = containerStyle.WhiteSpace == WhiteSpaceValue.Nowrap;
        float availW    = GetAvailableWidth(containerX, currentY, containerWidth, floats);

        var lineItems = new List<(LayoutBox box, float w, float h, float asc)>();
        float lineW   = 0f;

        for (int i = 0; i < measured.Count; i++)
        {
            var (box, w, h, asc) = measured[i];

            // Force line-break on <br>
            if (box.Element?.TagName == "br")
            {
                float brH = h > 0 ? h : (lineItems.Count > 0 ? lineItems[^1].h : 16f);
                currentY += FlushLine(lineItems, containerX, currentY, lineW, availW,
                                      containerStyle, floats);

                // FIX: <br> was never assigned real geometry — it short-
                // circuited straight to `continue` without ever entering
                // lineItems, and FlushLine is the only place that sets
                // box.X/Y/Width/Height for items in that list. The box
                // stayed at its default 0,0,0,0 forever, which is harmless
                // for painting (a 0x0 box draws nothing) but breaks anything
                // that reads its geometry — hit-testing, devtools dumps,
                // "what line did this break on" logic. Give it a real,
                // zero-width box positioned where the break actually occurs.
                box.X      = containerX;
                box.Y      = currentY;
                box.Width  = 0f;
                box.Height = brH;

                currentY += brH; // blank line
                lineItems.Clear();
                lineW  = 0f;
                availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
                continue;
            }

            // Wrap if the item overflows the current line (and we have something on the line)
            if (!nowrap && lineItems.Count > 0 && lineW + w > availW)
            {
                currentY += FlushLine(lineItems, containerX, currentY, lineW, availW,
                                      containerStyle, floats);
                lineItems.Clear();
                lineW  = 0f;
                availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
            }

            lineItems.Add((box, w, h, asc));
            lineW += w;
        }

        // Flush trailing line
        if (lineItems.Count > 0)
            currentY += FlushLine(lineItems, containerX, currentY, lineW, availW,
                                  containerStyle, floats);

        return Math.Max(0f, currentY - startY);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Line flushing
    // ─────────────────────────────────────────────────────────────────────────

    private static float FlushLine(
        List<(LayoutBox box, float w, float h, float asc)> items,
        float containerX, float y,
        float lineW, float availW,
        ComputedStyle containerStyle,
        FloatContext? floats)
    {
        if (items.Count == 0) return 0f;

        // Line box height = maximum ascent + maximum descent
        float maxAscent  = 0f;
        float maxDescent = 0f;
        foreach (var (_, w, h, asc) in items)
        {
            maxAscent  = Math.Max(maxAscent,  asc);
            maxDescent = Math.Max(maxDescent, h - asc);
        }
        float lineH = Math.Max(maxAscent + maxDescent, 1f);

        // X start based on text-align
        float leftEdge  = floats != null ? Math.Max(containerX, floats.GetLeftEdge(y)) : containerX;
        float rightEdge = floats != null ? floats.GetRightEdge(y) : containerX + availW;
        if (rightEdge == float.MaxValue) rightEdge = containerX + availW;

        float x = leftEdge;
        float extra = 0f;   // extra pixels between items (justify)

        switch (containerStyle.TextAlign)
        {
            case TextAlign.Center:
                x += (rightEdge - leftEdge - lineW) / 2f;
                break;
            case TextAlign.Right:
                x += (rightEdge - leftEdge - lineW);
                break;
            case TextAlign.Justify when items.Count > 1:
                extra = Math.Max(0f, (rightEdge - leftEdge - lineW) / (items.Count - 1));
                break;
        }

        // Place each item
        for (int i = 0; i < items.Count; i++)
        {
            var (box, w, h, asc) = items[i];

            box.Width  = w;
            box.Height = h;
            box.X      = x;

            // Baseline-align: top of box = line top + (maxAscent - this item's ascent)
            box.Y = y + (maxAscent - asc);

            x += w;
            if (i < items.Count - 1) x += extra;
        }

        return lineH;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Measurement
    // ─────────────────────────────────────────────────────────────────────────

    private static void MeasureBox(LayoutBox box,
        out float width, out float height, out float ascent)
    {
        // Replaced image or sized box
        if (box.BoxType == BoxType.Replaced || box.ReplacedImage != null)
        {
            width   = box.Width  > 0 ? box.Width  : (box.ReplacedImage?.Width  ?? 0);
            height  = box.Height > 0 ? box.Height : (box.ReplacedImage?.Height ?? 0);
            // Images sit on the baseline — ascent = full height
            ascent  = height;
            return;
        }

        // Inline-block: use pre-set dimensions
        if (box.BoxType == BoxType.InlineBlock)
        {
            width  = box.Width  + box.MarginLeft + box.MarginRight
                                + box.BorderLeft + box.BorderRight
                                + box.PaddingLeft + box.PaddingRight;
            height = box.Height + box.MarginTop  + box.MarginBottom
                                + box.BorderTop  + box.BorderBottom
                                + box.PaddingTop + box.PaddingBottom;
            ascent = height;
            return;
        }

        // Text run — measure with GDI+
        if (!string.IsNullOrEmpty(box.TextRun))
        {
            var style = box.Element?.Style;
            if (_fontCache != null && style != null)
            {
                bool bold   = style.FontWeight >= FontWeightValue.Bold;
                bool italic = style.FontStyle  == FontStyleValue.Italic;
                var  font   = _fontCache.Resolve(style.FontFamily, style.FontSize, bold, italic);

                var sz = _measureG.MeasureString(box.TextRun, font, int.MaxValue, _sf);
                width  = (float)Math.Ceiling(sz.Width);
                height = (float)Math.Ceiling(font.GetHeight(_measureG));

                // Ascent ≈ Em-height of the font (descent = height − ascent)
                float emH    = font.FontFamily.GetEmHeight(font.Style);
                float cellH  = font.FontFamily.GetLineSpacing(font.Style);
                float ascentU = font.FontFamily.GetCellAscent(font.Style);
                ascent = height * (ascentU / cellH);
                return;
            }
            // No FontCache yet — use rough estimates
            float fSize = box.Element?.Style?.FontSize ?? 16f;
            width  = box.TextRun.Length * fSize * 0.55f;
            height = fSize * 1.2f;
            ascent = fSize * 0.85f;
            return;
        }

        // Empty / unknown
        width  = 0f;
        height = 0f;
        ascent = 0f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Float helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static float GetAvailableWidth(float containerX, float y,
                                            float containerWidth, FloatContext? floats)
    {
        // The right boundary of the container in absolute coordinates
        float containerRight = containerX + containerWidth;

        if (floats == null)
            return containerWidth;   // no floats: full content width

        float l = Math.Max(containerX, floats.GetLeftEdge(y));
        float r = floats.GetRightEdge(y);
        // Always clamp to the container's own right edge
        r = (r == float.MaxValue) ? containerRight : Math.Min(r, containerRight);

        return Math.Max(0f, r - l);
    }
}