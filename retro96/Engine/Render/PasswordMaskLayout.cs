using System;
using Retro96.Drawing;
using SkiaSharp;

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

    public static float GetAdvance(Font font)
    {
        float glyphWidth;
        try { glyphWidth = font.SkFont.MeasureText("*"); }
        catch { glyphWidth = MathF.Max(1f, font.Size * 0.5f); }
        if (!float.IsFinite(glyphWidth) || glyphWidth <= 0f)
            glyphWidth = MathF.Max(1f, font.Size * 0.5f);

        // Use the exact Skia glyph advance.  A fixed, DPI-independent padding
        // term caused the caret/selection mask to drift from the renderer at
        // different zoom scales and font rasterization resolutions.
        return MathF.Max(1f, glyphWidth);
    }

    // Compatibility overload used by the GDI-backed shell overlay. The previous
    // implementation measured the glyph with GDI while the page renderer measured
    // it with Skia, so the selected password stars slowly drifted out of alignment.
    // Both paths now consume the exact same Font/Skia advance.
    public static float GetAdvance(Graphics g, Font font, StringFormat? format = null)
        => GetAdvance(font);
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
        return Math.Max(0, index) * GetAdvance(font);
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
        float advance = GetAdvance(font);
        float drawHeight = Math.Max(1f, height);

        // BrowserCanvas field selection is drawn as an overlay on top of the
        // renderer's cached control. Drawing the selected mask glyphs through
        // the same SKCanvas/SkFont pair removes the old sub-pixel mismatch where
        // a white selected '*' sat a fraction to the left of the black '*' under
        // it. Use exactly the same per-glyph center and vertical-center contract
        // as Renderer.DrawPasswordMaskRange.
        var previousEdging = font.SkFont.Edging;
        try
        {
            // Password controls are rendered by Renderer with aliased Skia glyphs.
            // Do not infer the mask edging from the compatibility Graphics hint: at
            // high-DPI/zoom that hint can be ClearType/antialiased while the actual
            // control remains aliased, producing a visible white/black fringe.
            font.SkFont.Edging = SKFontEdging.Alias;

            float lineY = y + Math.Max(0f, (drawHeight - font.GetHeight()) * 0.5f);
            float baseline = lineY + font.AscentPx;
            var paint = brush is SolidBrush solid
                ? solid.Prepare(antialias: true)
                : null;
            if (paint == null) return;

            for (int i = start; i < end; i++)
            {
                float centerX = x + i * advance + advance * 0.5f;
                g.Canvas.DrawText("*", centerX, baseline, SKTextAlign.Center,
                    font.SkFont, paint);
            }
        }
        finally
        {
            font.SkFont.Edging = previousEdging;
        }
    }
}
