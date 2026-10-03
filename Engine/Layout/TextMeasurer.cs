using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Retro96.Engine.Layout;

/// <summary>
/// Cache for System.Drawing.Font objects and text measurement helpers.
/// Implements 1996 GDI+ font metric caching + word-wrap.
/// </summary>
public class TextMeasurer : IDisposable
{
    private readonly Dictionary<string, Font> _fontCache = new();
    private readonly Bitmap _measureBitmap;
    private readonly Graphics _measureGfx;

    public TextMeasurer()
    {
        _measureBitmap = new Bitmap(1, 1);
        _measureGfx = Graphics.FromImage(_measureBitmap);
        _measureGfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        _measureGfx.SmoothingMode = SmoothingMode.None;
        _measureGfx.PixelOffsetMode = PixelOffsetMode.None;
    }

    /// <summary>
    /// Resolve and cache a Font object from a CSS font-family list.
    /// Tries each family in order; falls back to GenericSerif.
    /// </summary>
    public Font Resolve(IEnumerable<string> familyList, float sizePx, bool bold, bool italic)
    {
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;

        foreach (var family in familyList)
        {
            var key = $"{family}|{sizePx}|{(int)style}";
            if (_fontCache.TryGetValue(key, out var cached))
                return cached;

            try
            {
                var fontFamily = new FontFamily(family);
                var font = new Font(fontFamily, sizePx, style, GraphicsUnit.Pixel);
                _fontCache[key] = font;
                return font;
            }
            catch
            {
                // Try next family
            }
        }

        // Fallback: GenericSerif
        var fallbackKey = $"GenericSerif|{sizePx}|{(int)style}";
        if (_fontCache.TryGetValue(fallbackKey, out var fallback))
            return fallback;

        var fallbackFont = new Font(FontFamily.GenericSerif, sizePx, style, GraphicsUnit.Pixel);
        _fontCache[fallbackKey] = fallbackFont;
        return fallbackFont;
    }

    /// <summary>
    /// Measure text size using the given font.
    /// </summary>
    public SizeF Measure(string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
            return SizeF.Empty;
        return _measureGfx.MeasureString(text, font);
    }

    /// <summary>
    /// Get the line height for a font (ascent + descent).
    /// Multiplied by line-height multiplier from CSS.
    /// </summary>
    public float LineHeight(Font font, float lineHeightMultiplier)
    {
        return font.GetHeight() * lineHeightMultiplier;
    }

    /// <summary>
    /// Break text into lines that fit within maxWidth pixels.
    /// Never break inside a word unless the word is wider than maxWidth.
    /// Implements NN10 quirk: <nobr> / white-space: nowrap prevents breaking.
    /// </summary>
    public List<string> WordWrap(string text, Font font, float maxWidth, bool noWrap = false)
    {
        var lines = new List<string>();

        if (string.IsNullOrEmpty(text))
            return lines;

        if (noWrap || maxWidth <= 0)
        {
            lines.Add(text);
            return lines;
        }

        var words = text.Split(' ', StringSplitOptions.None);
        var currentLine = "";
        float currentWidth = 0;

        foreach (var word in words)
        {
            var wordWithSpace = (string.IsNullOrEmpty(currentLine) ? "" : " ") + word;
            var size = Measure(wordWithSpace, font);

            if (string.IsNullOrEmpty(currentLine))
            {
                currentLine = word;
                currentWidth = Measure(word, font).Width;
            }
            else if (currentWidth + size.Width <= maxWidth)
            {
                currentLine += wordWithSpace;
                currentWidth += size.Width;
            }
            else
            {
                // Word doesn't fit - push current line
                lines.Add(currentLine);

                // Check if the word itself is wider than maxWidth
                var wordSize = Measure(word, font);
                if (wordSize.Width > maxWidth)
                {
                    // Break the word (insert hyphens? No, 1996 browsers don't)
                    // Just push the word as its own line
                    lines.Add(word);
                    currentLine = "";
                    currentWidth = 0;
                }
                else
                {
                    currentLine = word;
                    currentWidth = wordSize.Width;
                }
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
            lines.Add(currentLine);

        return lines;
    }

    /// <summary>
    /// Tokenise text by whitespace and measure each word.
    /// Returns list of (word, width) pairs.
    /// </summary>
    public List<(string word, float width)> MeasureWords(string text, Font font)
    {
        var result = new List<(string word, float width)>();

        if (string.IsNullOrEmpty(text))
            return result;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            var size = Measure(word + " ", font);
            result.Add((word, size.Width));
        }

        return result;
    }

    /// <summary>
    /// Measure the width of a single character.
    /// </summary>
    public float MeasureChar(char c, Font font)
    {
        return Measure(c.ToString(), font).Width;
    }

    public void Dispose()
    {
        _measureGfx?.Dispose();
        _measureBitmap?.Dispose();
        foreach (var font in _fontCache.Values)
            font.Dispose();
        _fontCache.Clear();
    }
}
