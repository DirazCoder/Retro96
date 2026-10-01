using System;
using Retro96.Drawing;

namespace Retro96.Engine.Render;

/// <summary>
/// Shared password-mask geometry.  Password fields deliberately do not use
/// SkiaSharp's whole-run kerning/advance calculation for each selection boundary:
/// repeated '*' glyphs can be grid-fitted at slightly different fractional
/// edges, which makes the selection clip disagree with the painted glyphs.
/// A single measured glyph advance is therefore used consistently for paint,
/// caret, hit-testing, scrolling and selection.
/// </summary>
internal static class PasswordMaskLayout
{
    private const float ExtraAdvance = 1.25f;

    public static StringFormat CreateFormat()
    {
        return new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip |
                          StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near
        };
    }

    public static float GetAdvance(Graphics g, Font font, StringFormat? format = null)
    {
        using var canonical = CreateFormat();
        float glyphWidth = g.MeasureString("*", font, int.MaxValue, canonical).Width;
        if (!float.IsFinite(glyphWidth) || glyphWidth <= 0f)
            glyphWidth = MathF.Max(1f, font.Size * 0.5f);

        // Keep one deterministic advance for every password character. The same
        // value is used by the page renderer, focused-field overlay, caret,
        // hit-testing and horizontal scrolling. This prevents two independently
        // measured masked runs from landing on top of one another.
        return MathF.Max(1f, MathF.Round((glyphWidth + ExtraAdvance) * 4f) / 4f);
    }

    public static float TotalWidth(int length, float advance)
        => Math.Max(0, length) * advance;

    public static float CaretX(int index, Font font, Graphics g)
    {
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near
        };
        return Math.Max(0, index) * GetAdvance(g, font, fmt);
    }

    public static int IndexFromX(float x, int length, float advance)
    {
        if (length <= 0 || x <= 0f || advance <= 0f) return 0;
        int index = (int)MathF.Floor((x + advance * 0.5f) / advance);
        return Math.Clamp(index, 0, length);
    }

    public static void DrawRange(Graphics g, int length, Font font, Brush brush,
                                 float x, float y, float height,
                                 int start, int end, StringFormat format)
    {
        if (length <= 0 || end <= start) return;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, start, length);
        float advance = GetAdvance(g, font);
        float drawHeight = Math.Max(1f, height);

        for (int i = start; i < end; i++)
        {
            float glyphX = x + i * advance;
            g.DrawString("*", font, brush,
                new RectangleF(glyphX, y, Math.Max(1f, advance), drawHeight),
                format);
        }
    }
}
