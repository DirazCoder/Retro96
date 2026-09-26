// Retro96.Graphics — the drawing surface.
//
// A GDI-shaped canvas over Skia's SKCanvas.  Every stroke, fill, image
// blit, text draw and text measurement in the engine and the layout rig
// funnels through here, which is what makes the whole rendering pipeline
// platform-independent (Linux test rig == Windows browser, same pixels).
//
// Metric contracts the engine depends on:
//   • DrawString(x, y) puts the line-box TOP at (x, y) — baseline at
//     y + ascent, exactly the GDI+ GenericTypographic placement.
//   • MeasureString returns tight advance width (trailing spaces included
//     when the format asks for them) and full line height.
//   • Save/Restore/TranslateTransform/ResetTransform/SetClip nest exactly
//     like the GDI state stack the marquee and clip code was written for.
using System.Text;

using SkiaSharp;

namespace Retro96.Drawing;

public sealed class Graphics : IDisposable
{
    private readonly SKSurface _surface;
    private readonly SKCanvas _canvas;
    private readonly Bitmap _bitmap;

    public TextRenderingHint TextRenderingHint { get; set; } = TextRenderingHint.ClearTypeGridFit;
    public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.Default;
    public PixelOffsetMode PixelOffsetMode { get; set; } = PixelOffsetMode.Default;
    public InterpolationMode InterpolationMode { get; set; } = InterpolationMode.Default;
    public CompositingMode CompositingMode { get; set; } = CompositingMode.SourceOver;
    public CompositingQuality CompositingQuality { get; set; } = CompositingQuality.Default;

    public float DpiX => 96f;
    public float DpiY => 96f;

    private Graphics(Bitmap bitmap)
    {
        _bitmap = bitmap;
        var sk = bitmap.SkBitmap;
        _surface = SKSurface.Create(sk.Info, sk.GetPixels(), sk.RowBytes)
                   ?? throw new InvalidOperationException("Could not create a raster surface for the bitmap.");
        _canvas = _surface.Canvas;
    }

    public static Graphics FromImage(Bitmap bitmap) => new(bitmap);

    public void Dispose()
    {
        _surface.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── State / transform / clip ───────────────────────────────────────

    public int Save() => _canvas.Save();

    public void Restore(int state) => _canvas.RestoreToCount(state);

    public void TranslateTransform(float dx, float dy) => _canvas.Translate(dx, dy);

    public void ScaleTransform(float sx, float sy) => _canvas.Scale(sx, sy);

    public void ResetTransform() => _canvas.ResetMatrix();

    public void SetClip(RectangleF rect, CombineMode combineMode = CombineMode.Replace) =>
        // Skia dropped the deprecated Replace clip op; every engine
        // Replace-clip site runs inside a Save/Restore pair, where
        // Intersect-from-clean state is behaviourally identical.
        _canvas.ClipRect(SKRect.Create(rect.X, rect.Y, rect.Width, rect.Height),
            SKClipOperation.Intersect);

    public void SetClip(Region region, CombineMode combineMode = CombineMode.Replace) =>
        _canvas.ClipPath(region.Path, SKClipOperation.Intersect);

    public void Clear(Color color) => _canvas.Clear(color.ToSkColor());

    // ── Shapes ─────────────────────────────────────────────────────────

    private bool ShapeAntiAlias => SmoothingMode is SmoothingMode.AntiAlias or SmoothingMode.HighQuality;

    public void FillRectangle(Brush brush, RectangleF rect) =>
        FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);

    public void FillRectangle(Brush brush, float x, float y, float width, float height)
    {
        if (brush is not SolidBrush sb || width <= 0f || height <= 0f) return;
        _canvas.DrawRect(x, y, width, height, sb.Prepare(ShapeAntiAlias));
    }

    public void DrawRectangle(Pen pen, RectangleF rect) =>
        DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

    public void DrawRectangle(Pen pen, float x, float y, float width, float height)
    {
        if (pen == null || width < 0f || height < 0f) return;
        _canvas.DrawRect(x, y, Math.Max(width, 0.01f), Math.Max(height, 0.01f), pen.Prepare(ShapeAntiAlias));
    }

    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
    {
        if (pen == null) return;
        _canvas.DrawLine(x1, y1, x2, y2, pen.Prepare(ShapeAntiAlias));
    }

    public void DrawEllipse(Pen pen, float x, float y, float width, float height)
    {
        if (pen == null || width <= 0f || height <= 0f) return;
        _canvas.DrawOval(SKRect.Create(x, y, width, height), pen.Prepare(ShapeAntiAlias));
    }

    public void FillEllipse(Brush brush, float x, float y, float width, float height)
    {
        if (brush is not SolidBrush sb || width <= 0f || height <= 0f) return;
        _canvas.DrawOval(SKRect.Create(x, y, width, height), sb.Prepare(ShapeAntiAlias));
    }

    public void FillPolygon(Brush brush, PointF[] points)
    {
        if (brush is not SolidBrush sb || points == null || points.Length < 3) return;
        using var path = new SKPath();
        var skPoints = new SKPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
            skPoints[i] = new SKPoint(points[i].X, points[i].Y);
        path.AddPoly(skPoints, true);
        _canvas.DrawPath(path, sb.Prepare(ShapeAntiAlias));
    }

    // ── Images ─────────────────────────────────────────────────────────

    private SKSamplingOptions Sampling =>
        InterpolationMode == InterpolationMode.NearestNeighbor
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

    private SKPaint? ImagePaint()
    {
        // Default path: draw with the plain source-over blend Skia applies
        // when no paint is given; SourceCopy blits get an explicit Src blend.
        if (CompositingMode == CompositingMode.SourceCopy)
            return _srcCopyPaint ??= new SKPaint { BlendMode = SKBlendMode.Src };
        return null;
    }

    private SKPaint? _srcCopyPaint;

    /// <summary>Zero-copy image view over the bitmap's live pixels; the
    /// owning Bitmap is referenced by every caller for the draw's duration.</summary>
    private static SKImage? Wrap(Image image)
    {
        var sk = image.Sk;
        if (sk == null) return null;
        return SKImage.FromPixels(sk.Info, sk.GetPixels(), sk.RowBytes);
    }

    public void DrawImage(Image image, float x, float y)
    {
        if (image?.Sk == null) return;
        using var img = Wrap(image);
        if (img == null) return;
        _canvas.DrawImage(img, SKRect.Create(x, y, image.Width, image.Height), Sampling, ImagePaint());
    }

    public void DrawImage(Image image, RectangleF destRect)
    {
        if (image?.Sk == null) return;
        using var img = Wrap(image);
        if (img == null) return;
        _canvas.DrawImage(img, SKRect.Create(destRect.X, destRect.Y, destRect.Width, destRect.Height),
            Sampling, ImagePaint());
    }

    public void DrawImage(Image image, float x, float y, float width, float height)
    {
        if (image?.Sk == null) return;
        using var img = Wrap(image);
        if (img == null) return;
        _canvas.DrawImage(img, SKRect.Create(x, y, width, height), Sampling, ImagePaint());
    }

    public void DrawImage(Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit)
    {
        if (image?.Sk == null) return;
        using var img = Wrap(image);
        if (img == null) return;
        _canvas.DrawImage(img,
            SKRect.Create(srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height),
            SKRect.Create(destRect.X, destRect.Y, destRect.Width, destRect.Height),
            Sampling, ImagePaint());
    }

    public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit)
    {
        if (image?.Sk == null) return;
        using var img = Wrap(image);
        if (img == null) return;
        _canvas.DrawImage(img,
            SKRect.Create(srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height),
            SKRect.Create(destRect.X, destRect.Y, destRect.Width, destRect.Height),
            Sampling, ImagePaint());
    }

    // ── Text ───────────────────────────────────────────────────────────

    // U+E000 is an engine-private text marker emitted for legacy HTML
    // bullets (for example Windows-1252 character 0x95 / &#149;).  It must
    // never reach Skia as a real glyph: some installed western faces paint
    // it as a square .notdef box.  Measurement treats it like a middle-dot
    // advance; point/rect text drawing replaces it with a small vector circle.
    private const char LegacyBulletMarker = '\uE000';

    private static string NormalizeSpecialGlyphsForMeasurement(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(LegacyBulletMarker) < 0)
            return text;
        return text.Replace(LegacyBulletMarker, '·');
    }

    private static float MeasureAdvance(Font font, string text) =>
        font.MeasureText(NormalizeSpecialGlyphsForMeasurement(text));

    private void ApplyEdging(Font font)
    {
        font.SkFont.Edging = TextRenderingHint switch
        {
            TextRenderingHint.SingleBitPerPixel
                or TextRenderingHint.SingleBitPerPixelGridFit
                or TextRenderingHint.SystemDefault => SKFontEdging.Alias,
            _ => SKFontEdging.Antialias,
        };
    }

    /// <summary>Plain draw: line-box top at (x, y). Format optional.</summary>
    public void DrawString(string? text, Font font, Brush brush, float x, float y, StringFormat? format)
    {
        if (string.IsNullOrEmpty(text) || font == null || brush is not SolidBrush sb) return;
        ApplyEdging(font);
        float baseline = y + font.AscentPx;
        if (text.IndexOf(LegacyBulletMarker) >= 0)
        {
            DrawTextWithSpecialGlyphs(text, font, sb, x, baseline);
            return;
        }
        _canvas.DrawText(text, x, baseline, font.SkFont, sb.Prepare(antialias: true));
    }

    public void DrawString(string? text, Font font, Brush brush, float x, float y)
        => DrawString(text, font, brush, x, y, null);

    private void DrawTextWithSpecialGlyphs(string text, Font font, SolidBrush brush,
                                           float x, float baseline)
    {
        var paint = brush.Prepare(antialias: true);
        float cursor = x;
        int start = 0;
        float lineHeight = font.GetHeight();
        float bulletAdvance = Math.Max(1f, MeasureAdvance(font, "·"));
        float diameter = Math.Clamp(lineHeight * 0.30f, 2.5f, 5.5f);
        float centerY = baseline - font.AscentPx + lineHeight * 0.52f;

        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != LegacyBulletMarker)
                continue;

            if (i > start)
            {
                string normal = text[start..i];
                _canvas.DrawText(normal, cursor, baseline, font.SkFont, paint);
                cursor += MeasureAdvance(font, normal);
            }

            if (i < text.Length)
            {
                float left = cursor + (bulletAdvance - diameter) * 0.5f;
                using var dot = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = paint.Color
                };
                _canvas.DrawOval(SKRect.Create(left, centerY - diameter * 0.5f,
                    diameter, diameter), dot);
                cursor += bulletAdvance;
                start = i + 1;
            }
        }
    }

    /// <summary>Rect draw with alignment, wrapping, clipping and ellipsis.</summary>
    public void DrawString(string? text, Font font, Brush brush, RectangleF layoutRect, StringFormat? format)
    {
        if (string.IsNullOrEmpty(text) || font == null || brush is not SolidBrush sb) return;
        if (layoutRect.Width <= 0f || layoutRect.Height <= 0f) return;

        var sf = format ?? new StringFormat();
        bool noWrap = (sf.FormatFlags & StringFormatFlags.NoWrap) != 0;

        ApplyEdging(font);
        var paint = sb.Prepare(antialias: true);
        float lineHeight = font.GetHeight();

        // Break into visual lines (single line when NoWrap).
        List<string> lines = noWrap
            ? new List<string> { text }
            : WrapToWidth(text, font, layoutRect.Width);

        // Trimming: ellipsize every line that still overflows.
        if (sf.Trimming is StringTrimming.EllipsisCharacter or StringTrimming.EllipsisWord)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                while (lines[i].Length > 1 && MeasureAdvance(font, Ellipsize(lines[i])) > layoutRect.Width)
                    lines[i] = lines[i][..^2];
            }
        }
        else if (sf.Trimming is StringTrimming.Character or StringTrimming.Word)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                while (lines[i].Length > 1 && MeasureAdvance(font, lines[i]) > layoutRect.Width)
                    lines[i] = lines[i][..^1];
            }
        }

        // Vertical placement of the whole block.
        float blockHeight = lines.Count * lineHeight;
        float top = sf.LineAlignment switch
        {
            StringAlignment.Center => layoutRect.Y + (layoutRect.Height - blockHeight) / 2f,
            StringAlignment.Far => layoutRect.Bottom - blockHeight,
            _ => layoutRect.Y,
        };

        int save = _canvas.Save();
        try
        {
            if ((sf.FormatFlags & StringFormatFlags.NoClip) == 0)
                _canvas.ClipRect(SKRect.Create(layoutRect.X, layoutRect.Y,
                    layoutRect.Width, layoutRect.Height), SKClipOperation.Intersect);

            for (int i = 0; i < lines.Count; i++)
            {
                float w = MeasureAdvance(font, lines[i]);
                float x = sf.Alignment switch
                {
                    StringAlignment.Center => layoutRect.X + (layoutRect.Width - w) / 2f,
                    StringAlignment.Far => layoutRect.Right - w,
                    _ => layoutRect.X,
                };
                float baseline = top + i * lineHeight + font.AscentPx;
                if (lines[i].IndexOf(LegacyBulletMarker) >= 0)
                    DrawTextWithSpecialGlyphs(lines[i], font, sb, x, baseline);
                else
                    _canvas.DrawText(lines[i], x, baseline, font.SkFont, paint);
            }
        }
        finally
        {
            _canvas.RestoreToCount(save);
        }
    }

    private static string Ellipsize(string s) =>
        s.Length <= 1 ? s : s[..^1] + "…";

    /// <summary>Greedy word wrap at spaces; over-long words break at chars.</summary>
    private static List<string> WrapToWidth(string text, Font font, float maxWidth)
    {
        var lines = new List<string>();
        if (maxWidth <= 0f)
        {
            lines.Add(text);
            return lines;
        }

        var current = new StringBuilder();
        float currentW = 0f;
        float spaceW = MeasureAdvance(font, " ");

        foreach (var word in text.Split(' '))
        {
            float wordW = MeasureAdvance(font, word);
            if (wordW > maxWidth)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    currentW = 0f;
                }
                var part = new StringBuilder();
                float w = 0f;
                foreach (var ch in word)
                {
                    float cw = MeasureAdvance(font, ch.ToString());
                    if (w + cw > maxWidth && part.Length > 0)
                    {
                        lines.Add(part.ToString());
                        part.Clear();
                        w = 0f;
                    }
                    part.Append(ch);
                    w += cw;
                }
                if (part.Length > 0) lines.Add(part.ToString());
                continue;
            }

            if (current.Length == 0)
            {
                current.Append(word);
                currentW = wordW;
            }
            else if (currentW + spaceW + wordW <= maxWidth)
            {
                current.Append(' ').Append(word);
                currentW += spaceW + wordW;
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
                currentW = wordW;
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());
        if (lines.Count == 0)
            lines.Add("");
        return lines;
    }

    // ── Measurement ────────────────────────────────────────────────────

    /// <summary>Tight advance / line-height measurement (the engine's
    /// GenericTypographic contract).</summary>
    public SizeF MeasureString(string? text, Font font, int maxWidth, StringFormat? format)
    {
        if (string.IsNullOrEmpty(text) || font == null)
            return SizeF.Empty;
        return new SizeF(MeasureAdvance(font, text), font.GetHeight());
    }

    public SizeF MeasureString(string? text, Font font, SizeF layoutArea, StringFormat? format)
    {
        MeasureString(text, font, layoutArea, format, out _, out _);
        return _lastWrapSize;
    }

    private SizeF _lastWrapSize;

    /// <summary>
    /// Word-wrapping measurement: reports how many characters fit inside
    /// the layout area (charsFitted) and how many lines the full text needs.
    /// Mirrors the GDI overload the textarea line-breaker was written for.
    /// </summary>
    public SizeF MeasureString(string? text, Font font, SizeF layoutArea,
                                StringFormat? format, out int charsFitted, out int linesFilled)
    {
        charsFitted = 0;
        linesFilled = 1;

        if (string.IsNullOrEmpty(text) || font == null)
        {
            _lastWrapSize = SizeF.Empty;
            return SizeF.Empty;
        }

        float lineHeight = font.GetHeight();
        float maxWidth = layoutArea.Width;
        bool noWrap = format != null && (format.FormatFlags & StringFormatFlags.NoWrap) != 0;

        if (noWrap || maxWidth <= 0f)
        {
            charsFitted = text.Length;
            _lastWrapSize = new SizeF(MeasureAdvance(font, text), lineHeight);
            return _lastWrapSize;
        }

        float spaceW = MeasureAdvance(font, " ");

        int lineStart = 0;          // index of the first char on the current line
        float width = 0f;           // width of the current line so far
        int lastSpace = -1;         // index of the last space ON the current line
        float lastSpaceWidth = 0f;  // line width up to (not including) that space
        int lines = 1;
        float maxLineWidth = 0f;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n' || ch == '\r')
                continue;           // callers split hard breaks themselves

            float cw = MeasureAdvance(font, ch.ToString());
            if (width + cw > maxWidth && i > lineStart)
            {
                maxLineWidth = Math.Max(maxLineWidth, width);

                // Break at the last space when one exists on this line;
                // otherwise the word is longer than the line — char-break.
                int breakAt = lastSpace >= lineStart ? lastSpace + 1 : i;
                if (charsFitted == 0 && lines == 1)
                    charsFitted = Math.Max(1, breakAt - lineStart);

                lines++;
                lineStart = breakAt;
                // rebuild the carried-over width
                width = MeasureAdvance(font, text[lineStart..i]) + cw;
                lastSpace = -1;
                lastSpaceWidth = 0f;
                continue;
            }

            width += cw;
            if (ch == ' ')
            {
                lastSpace = i;
                lastSpaceWidth = width - cw;
            }
        }

        maxLineWidth = Math.Max(maxLineWidth, width);
        if (charsFitted == 0)
            charsFitted = text.Length;

        linesFilled = lines;
        _lastWrapSize = new SizeF(maxLineWidth, lines * lineHeight);
        return _lastWrapSize;
    }

    public SizeF MeasureString(string? text, Font font)
        => MeasureString(text, font, int.MaxValue, null);
}
