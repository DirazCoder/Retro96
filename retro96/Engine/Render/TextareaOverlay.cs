// TextareaOverlay — the line-breaking and in-field selection geometry of
// <textarea>, extracted from BrowserCanvas so the "striped highlight"
// contract (Bug_TextareaSelectionStripedHighlight) is testable headlessly
// with real GDI+ measurement.  The canvas consumes these rects and only
// does the actual painting.
using Retro96.Drawing;

namespace Retro96.Engine.Render;

public static class TextareaOverlay
{
    /// <summary>
    /// Breaks field text into visual lines exactly the way the canvas does:
    /// hard breaks at \r / \n / \r\n, GDI+-measured word wrap otherwise
    /// (unless wrap=off).  Identical behaviour to the previous private
    /// BrowserCanvas.BreakTextareaLines.
    /// </summary>
    public static List<(int Start, int End)> BreakLines(
        Graphics g, string text, Font font, float wrapWidth, bool wrapOff)
    {
        var lines = new List<(int Start, int End)>();
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None
        };
        float lineHeight = font.GetHeight(g);

        int pos = 0;
        while (pos < text.Length)
        {
            int segEnd = pos;
            while (segEnd < text.Length && text[segEnd] is not ('\r' or '\n')) segEnd++;

            if (segEnd > pos)
            {
                // There is segment content before the next newline (or
                // EOF): emit it — one soft-wrapped run at a time.
                int fitted;
                if (wrapOff)
                {
                    fitted = segEnd - pos;
                }
                else
                {
                    string remaining = text[pos..segEnd];
                    g.MeasureString(remaining, font, new SizeF(wrapWidth, lineHeight * 1.5f),
                                    fmt, out fitted, out _);
                    fitted = Math.Clamp(fitted, 1, remaining.Length);
                }
                int lineEnd = pos + fitted;
                lines.Add((pos, lineEnd));
                pos = lineEnd;
                continue;
            }

            // pos sits ON a hard newline.  The line BEFORE it was already
            // emitted by the branch above (which stopped exactly here), so
            // this newline only introduces a NEW (possibly empty) line —
            // emit one only for genuine blank lines (the previous line
            // ended BEFORE this point, e.g. "\n\n").
            //
            // The old code emitted an empty (le, le) tuple after EVERY
            // newline: 8 visual lines became 16 entries, the selection
            // bands landed on every other index, and the highlight came
            // out STRIPED — the user-reported textarea bug.
            if (lines.Count == 0 || lines[^1].End != pos)
                lines.Add((pos, pos));

            if (text[pos] == '\r' && pos + 1 < text.Length && text[pos + 1] == '\n') pos++;
            pos++;
        }

        // Faithful final flush from the canvas original: covers empty text
        // and a trailing hard newline (adds the trailing empty line).
        if (text.Length == 0 || text[^1] is '\r' or '\n')
            lines.Add((text.Length, text.Length));

        return lines;
    }

    /// <summary>
    /// The selection highlight rectangles for a textarea selection range —
    /// one horizontal band per visual line that intersects the selection,
    /// in document order.  <paramref name="face"/> is the box ContentRect in
    /// page coordinates; scroll offsets are subtracted the same way the
    /// canvas paints them.
    /// </summary>
    public static List<RectangleF> SelectionRects(
        Graphics g, string text, Font font, RectangleF face,
        float scrollX, float scrollY, int selStart, int selEnd, bool wrapOff)
    {
        var rects = new List<RectangleF>();
        if (selEnd <= selStart) return rects;

        float textX = face.X + 3 - scrollX;
        float textY = face.Y + 2 - scrollY;
        float textWidth = Math.Max(1, face.Width - 6);
        float lineHeight = font.GetHeight(g);

        var lines = BreakLines(g, text, font, textWidth, wrapOff);
        using var noWrap = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None
        };

        float Measure(int start, int end) => end <= start ? 0f
            : g.MeasureString(text[start..end], font, int.MaxValue, noWrap).Width;

        for (int i = 0; i < lines.Count; i++)
        {
            var (ls, le) = lines[i];
            int a = Math.Max(selStart, ls), b = Math.Min(selEnd, le);
            if (b <= a) continue;
            float y = textY + i * lineHeight;
            float x1 = textX + Measure(ls, a);
            float x2 = textX + Measure(ls, b);
            rects.Add(new RectangleF(x1, y, Math.Max(1f, x2 - x1), lineHeight));
        }
        return rects;
    }
}
