using System;
using System.Collections.Generic;
using Retro96.Drawing;
using SkiaSharp;

namespace Retro96.Engine.Render;

public static class TextareaOverlay
{
    public readonly record struct Layout(
        List<(int Start, int End)> Lines,
        float TextWidth,
        float TextViewportWidth,
        float LineHeight,
        int VisibleLines,
        bool NeedsVerticalScrollbar);

    private const float ScrollbarGutter = 14f;

    public static void DrawLines(Graphics g, string text, Font font,
                                 List<(int Start, int End)> lines,
                                 SolidBrush brush, float x, float y,
                                 float width, float lineHeight,
                                 int scrollLine = 0)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            LineAlignment = StringAlignment.Near,
            Alignment = StringAlignment.Near
        };

        int first = Math.Max(0, scrollLine);
        for (int line = first; line < lines.Count; line++)
        {
            var (start, end) = lines[line];
            if (end <= start) continue;
            float drawY = y + (line - scrollLine) * lineHeight;
            if (drawY + lineHeight < y - lineHeight) continue;
            g.DrawStringWithoutLegacyStrokeBoost(text[start..end], font, brush,
                new RectangleF(x, drawY, Math.Max(1f, width), lineHeight), format);
        }
    }

    // Direct SkiaSharp rendering/layout overloads used by the main page renderer.
    // The legacy Graphics overloads remain for shell hit-testing/selection code
    // that has not yet been migrated.
    internal static void DrawLines(SKCanvas canvas, string text, Font font,
                                   List<(int Start, int End)> lines,
                                   SKPaint paint, float x, float y,
                                   float width, float lineHeight,
                                   int scrollLine = 0)
    {
        int first = Math.Max(0, scrollLine);
        for (int line = first; line < lines.Count; line++)
        {
            var (start, end) = lines[line];
            if (end <= start) continue;
            float drawY = y + (line - scrollLine) * lineHeight;
            if (drawY + lineHeight < y - lineHeight) continue;
            float baseline = drawY + font.AscentPx;
            canvas.DrawText(text[start..end], x, baseline,
                SKTextAlign.Left, font.SkFont, paint);
        }
    }

    internal static Layout CalculateLayout(SKCanvas canvas, string text, Font font,
                                           float faceWidth, float faceHeight, bool wrapOff)
    {
        text ??= string.Empty;
        float fullViewport = Math.Max(1f, faceWidth - 6f);
        float lineHeight = Math.Max(1f, font.GetHeight());
        int visibleLines = Math.Max(1, (int)Math.Floor(
            Math.Max(1f, faceHeight - 4f) / lineHeight));

        var fullLines = BreakLinesCoreSkia(text, font, fullViewport, wrapOff);
        float fullTextWidth = MaxLineWidthSkia(text, font, fullLines);

        if (fullLines.Count <= visibleLines || faceWidth < 16f)
            return new Layout(fullLines, fullTextWidth, fullViewport,
                lineHeight, visibleLines, false);

        float reservedViewport = Math.Max(1f, fullViewport - ScrollbarGutter);
        var reservedLines = BreakLinesCoreSkia(text, font, reservedViewport, wrapOff);
        if (reservedLines.Count <= visibleLines)
            return new Layout(fullLines, fullTextWidth, fullViewport,
                lineHeight, visibleLines, false);

        float reservedTextWidth = MaxLineWidthSkia(text, font, reservedLines);
        return new Layout(reservedLines, reservedTextWidth,
            reservedViewport, lineHeight, visibleLines, true);
    }

    private static List<(int Start, int End)> BreakLinesCoreSkia(
        string text, Font font, float wrapWidth, bool wrapOff)
    {
        var lines = new List<(int Start, int End)>();
        if (text.Length == 0)
        {
            lines.Add((0, 0));
            return lines;
        }

        int segmentStart = 0;
        int cursor = 0;
        while (cursor <= text.Length)
        {
            int breakAt = cursor;
            while (breakAt < text.Length && text[breakAt] is not ('\r' or '\n'))
                breakAt++;

            if (breakAt == segmentStart)
                lines.Add((segmentStart, segmentStart));
            else
                AppendWrappedSegmentSkia(text, font, segmentStart, breakAt,
                    wrapWidth, wrapOff, lines);

            if (breakAt >= text.Length)
                break;

            cursor = breakAt + 1;
            if (text[breakAt] == '\r' && cursor < text.Length && text[cursor] == '\n')
                cursor++;

            segmentStart = cursor;
            if (segmentStart == text.Length)
            {
                lines.Add((text.Length, text.Length));
                break;
            }
        }

        return lines.Count == 0 ? new List<(int, int)> { (0, 0) } : lines;
    }

    private static void AppendWrappedSegmentSkia(
        string text, Font font, int start, int end, float wrapWidth,
        bool wrapOff, List<(int Start, int End)> lines)
    {
        if (start >= end)
        {
            lines.Add((start, end));
            return;
        }

        int pos = start;
        while (pos < end)
        {
            int lineEnd;
            if (wrapOff)
            {
                lineEnd = end;
            }
            else
            {
                lineEnd = FindFittingEndSkia(text, font, pos, end, wrapWidth);
                if (lineEnd <= pos)
                    lineEnd = NextTextElementBoundary(text, pos, end);

                if (lineEnd < end)
                {
                    int preferred = LastWhitespaceBoundary(text, pos, lineEnd);
                    if (preferred > pos)
                        lineEnd = preferred;
                }
            }

            if (lineEnd <= pos)
                lineEnd = Math.Min(end, pos + 1);
            lines.Add((pos, lineEnd));
            pos = lineEnd;
        }
    }

    private static int FindFittingEndSkia(string text, Font font,
                                          int start, int end, float maxWidth)
    {
        var boundaries = new List<int>();
        int p = start;
        while (p < end)
        {
            p = NextTextElementBoundary(text, p, end);
            boundaries.Add(p);
        }
        if (boundaries.Count == 0) return start;

        bool Fits(int boundary) =>
            font.SkFont.MeasureText(text[start..boundary]) <= maxWidth + 0.01f;

        if (!Fits(boundaries[0])) return boundaries[0];
        if (Fits(boundaries[^1])) return boundaries[^1];

        int lo = 0, hi = boundaries.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Fits(boundaries[mid]))
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return boundaries[best];
    }

    private static float MaxLineWidthSkia(string text, Font font,
                                          List<(int Start, int End)> lines)
    {
        float max = 0f;
        foreach (var (start, end) in lines)
        {
            if (end <= start) continue;
            max = Math.Max(max, font.SkFont.MeasureText(text[start..end]));
        }
        return max;
    }

    /// <summary>
    /// Robust textarea line breaking. Hard newlines create exactly one new
    /// visual line, CRLF is consumed as one break, and soft wrapping only cuts
    /// at grapheme boundaries. A word boundary is preferred when it fits;
    /// otherwise long unbroken text is split at the last measured grapheme.
    /// </summary>
    public static List<(int Start, int End)> BreakLines(
        Graphics g, string text, Font font, float wrapWidth, bool wrapOff)
    {
        return BreakLinesCore(g, text ?? string.Empty, font,
            Math.Max(1f, wrapWidth), wrapOff);
    }

    public static Layout CalculateLayout(
        Graphics g, string text, Font font,
        float faceWidth, float faceHeight, bool wrapOff)
    {
        text ??= string.Empty;
        float fullViewport = Math.Max(1f, faceWidth - 6f);
        float lineHeight = Math.Max(1f, font.GetHeight(g));
        int visibleLines = Math.Max(1, (int)Math.Floor(
            Math.Max(1f, faceHeight - 4f) / lineHeight));

        var fullLines = BreakLinesCore(g, text, font, fullViewport, wrapOff);
        float fullTextWidth = MaxLineWidth(g, text, font, fullLines);

        // No vertical scrollbar is necessary, so use the full text gutter.
        if (fullLines.Count <= visibleLines || faceWidth < 16f)
        {
            return new Layout(fullLines, fullTextWidth, fullViewport,
                lineHeight, visibleLines, false);
        }

        // Once a bar is needed, reserve its gutter before wrapping. This is
        // deliberately a two-pass calculation: using a narrower width can add
        // lines, but if it somehow removes overflow we fall back to the full
        // width rather than leaving an unnecessary scrollbar gutter.
        float reservedViewport = Math.Max(1f, fullViewport - ScrollbarGutter);
        var reservedLines = BreakLinesCore(g, text, font, reservedViewport, wrapOff);
        if (reservedLines.Count <= visibleLines)
        {
            return new Layout(fullLines, fullTextWidth, fullViewport,
                lineHeight, visibleLines, false);
        }

        float reservedTextWidth = MaxLineWidth(g, text, font, reservedLines);
        return new Layout(reservedLines, reservedTextWidth,
            reservedViewport, lineHeight, visibleLines, true);
    }

    public static List<RectangleF> SelectionRects(
        Graphics g, string text, Font font, RectangleF face,
        float scrollX, float scrollY, int selStart, int selEnd, bool wrapOff)
    {
        var rects = new List<RectangleF>();
        text ??= string.Empty;
        if (selEnd <= selStart) return rects;

        var layout = CalculateLayout(g, text, font, face.Width, face.Height, wrapOff);
        float textX = face.X + 3 - scrollX;
        float textY = face.Y + 2 - scrollY;

        using var noWrap = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None
        };

        float Measure(int start, int end) => end <= start ? 0f
            : g.MeasureString(text[start..end], font, int.MaxValue, noWrap).Width;

        for (int i = 0; i < layout.Lines.Count; i++)
        {
            var (ls, le) = layout.Lines[i];
            int a = Math.Max(selStart, ls), b = Math.Min(selEnd, le);
            if (b <= a) continue;
            float x1 = textX + Measure(ls, a);
            float x2 = textX + Measure(ls, b);
            float y = textY + i * layout.LineHeight;
            rects.Add(new RectangleF(x1, y,
                Math.Max(1f, x2 - x1), layout.LineHeight));
        }
        return rects;
    }

    private static List<(int Start, int End)> BreakLinesCore(
        Graphics g, string text, Font font, float wrapWidth, bool wrapOff)
    {
        var lines = new List<(int Start, int End)>();
        if (text.Length == 0)
        {
            lines.Add((0, 0));
            return lines;
        }

        int segmentStart = 0;
        int cursor = 0;
        while (cursor <= text.Length)
        {
            int breakAt = cursor;
            while (breakAt < text.Length && text[breakAt] is not ('\r' or '\n'))
                breakAt++;

            if (breakAt == segmentStart)
            {
                // Empty hard-break-separated line.
                lines.Add((segmentStart, segmentStart));
            }
            else
            {
                AppendWrappedSegment(g, text, font, segmentStart, breakAt,
                    wrapWidth, wrapOff, lines);
            }

            if (breakAt >= text.Length)
                break;

            // Consume exactly one logical newline. CRLF is one break.
            cursor = breakAt + 1;
            if (text[breakAt] == '\r' && cursor < text.Length && text[cursor] == '\n')
                cursor++;

            segmentStart = cursor;
            if (segmentStart == text.Length)
            {
                // Trailing newline creates the expected empty line after it.
                lines.Add((text.Length, text.Length));
                break;
            }
        }

        return lines.Count == 0 ? new List<(int, int)> { (0, 0) } : lines;
    }

    private static void AppendWrappedSegment(
        Graphics g, string text, Font font, int start, int end,
        float wrapWidth, bool wrapOff,
        List<(int Start, int End)> lines)
    {
        if (start >= end)
        {
            lines.Add((start, end));
            return;
        }

        int pos = start;
        while (pos < end)
        {
            int lineEnd;
            if (wrapOff)
            {
                lineEnd = end;
            }
            else
            {
                lineEnd = FindFittingEnd(g, text, font, pos, end, wrapWidth);
                if (lineEnd <= pos)
                    lineEnd = NextTextElementBoundary(text, pos, end);

                if (lineEnd < end)
                {
                    int preferred = LastWhitespaceBoundary(text, pos, lineEnd);
                    if (preferred > pos)
                        lineEnd = preferred;
                }
            }

            if (lineEnd <= pos)
                lineEnd = Math.Min(end, pos + 1);
            lines.Add((pos, lineEnd));
            pos = lineEnd;
        }
    }

    private static int FindFittingEnd(Graphics g, string text, Font font,
                                      int start, int end, float maxWidth)
    {
        var boundaries = new List<int>();
        int p = start;
        while (p < end)
        {
            p = NextTextElementBoundary(text, p, end);
            boundaries.Add(p);
        }
        if (boundaries.Count == 0) return start;

        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            LineAlignment = StringAlignment.Near,
            Alignment = StringAlignment.Near
        };

        bool Fits(int boundary) =>
            g.MeasureString(text[start..boundary], font, int.MaxValue, fmt).Width
                <= maxWidth + 0.01f;

        if (!Fits(boundaries[0]))
            return boundaries[0];
        if (Fits(boundaries[^1]))
            return boundaries[^1];

        int lo = 0, hi = boundaries.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Fits(boundaries[mid]))
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return boundaries[best];
    }

    private static int LastWhitespaceBoundary(string text, int start, int end)
    {
        int p = start;
        int best = -1;
        while (p < end)
        {
            int next = NextTextElementBoundary(text, p, end);
            if (string.IsNullOrWhiteSpace(text[p..next]))
                best = next;
            p = next;
        }
        return best;
    }

    private static int NextTextElementBoundary(string text, int start, int end)
    {
        if (start >= end) return end;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text, start);
        if (!enumerator.MoveNext()) return Math.Min(end, start + 1);
        int next = enumerator.ElementIndex + enumerator.GetTextElement().Length;
        if (next <= start) next = start + 1;
        return Math.Min(end, next);
    }

    private static float MaxLineWidth(Graphics g, string text, Font font,
                                      List<(int Start, int End)> lines)
    {
        if (lines.Count == 0) return 0f;
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None
        };
        float max = 0f;
        foreach (var (start, end) in lines)
        {
            if (end <= start) continue;
            max = Math.Max(max, g.MeasureString(text[start..end], font,
                int.MaxValue, fmt).Width);
        }
        return max;
    }
}
