using System;
using System.Collections.Generic;
using System.Globalization;
using Retro96.Drawing;
using System.Text;

namespace Retro96.Engine.Layout;

/// <summary>
/// SkiaSharp font cache + word-wrap helper.  Generic CSS families
/// (serif / sans-serif / monospace / cursive / fantasy) map to the shared
/// families; every other family is tried in order and falls back to the
/// platform default — the FACE= fallback chain behaviour of 1996 engines.
/// Measurement uses the same GenericTypographic format as the renderer and
/// InlineLayout so all widths agree.
/// </summary>
public class TextMeasurer : IDisposable
{
    private static readonly StringFormat _sf = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming    = StringTrimming.None
    };

    private readonly Dictionary<string, Font> _fontCache = new(StringComparer.OrdinalIgnoreCase);
    // Only families created via new FontFamily(name) — the Generic* statics
    // must never be disposed.
    private readonly Dictionary<string, FontFamily> _familyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _familyMisses = new(StringComparer.OrdinalIgnoreCase);

    private readonly Graphics _measureGfx;
    private bool _disposed;

    public TextMeasurer()
    {
        _measureGfx = Graphics.CreateMeasurementContext();
        _measureGfx.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        _measureGfx.SmoothingMode = SmoothingMode.None;
        _measureGfx.PixelOffsetMode = PixelOffsetMode.None;
    }

    /// <summary>Resolve and cache a Font from a CSS font-family list.</summary>
    public Font Resolve(IEnumerable<string>? familyList, float sizePx, bool bold, bool italic)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TextMeasurer));

        if (sizePx <= 0f) sizePx = 16f;   // Font ctor throws on non-positive size
        // FIX: clamp the upper bound too.  A hostile `font-size: 999999px`
        // used to throw inside the FALLBACK Font ctor — which is outside
        // every try block — and took the whole layout pass down.
        if (sizePx > 2048f) sizePx = 2048f;

        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;

        // FIX: invariant culture.  The default float ToString is culture-
        // sensitive ("16,5" on de-DE); if the thread culture ever changes
        // mid-process the cache keys silently split and duplicate font resources
        // accumulate for the same face.
        string sizeKey = sizePx.ToString("0.###", CultureInfo.InvariantCulture);

        if (familyList != null)
        {
            foreach (var family in familyList)
            {
                if (string.IsNullOrWhiteSpace(family)) continue;

                string key = $"{family}|{sizeKey}|{(int)style}";
                if (_fontCache.TryGetValue(key, out var cached))
                    return cached;

                var fontFamily = ResolveFamily(family);
                if (fontFamily != null)
                {
                    try
                    {
                        var font = new Font(fontFamily, sizePx, style, GraphicsUnit.Pixel);
                        _fontCache[key] = font;
                        return font;
                    }
                    catch
                    {
                        // fall through to next family
                    }
                }
            }
        }

        // Platform default
        string fallbackKey = $"__default__|{sizeKey}|{(int)style}";
        if (_fontCache.TryGetValue(fallbackKey, out var fb))
            return fb;
        var fallbackFont = new Font(FontFamily.GenericSerif, sizePx, style, GraphicsUnit.Pixel);
        _fontCache[fallbackKey] = fallbackFont;
        return fallbackFont;
    }

    private FontFamily? ResolveFamily(string family)
    {
        string name = family.Trim().Trim('\'', '"');
        if (name.Length == 0) return null;

        switch (name.ToLowerInvariant())
        {
            case "serif":      return FontFamily.GenericSerif;
            case "sans-serif": return FontFamily.GenericSansSerif;
            case "monospace":  return FontFamily.GenericMonospace;
            case "cursive":
            case "fantasy":    return FontFamily.GenericSerif;
        }

        if (_familyCache.TryGetValue(name, out var cached))
            return cached;
        if (_familyMisses.Contains(name))
            return null;

        FontFamily? resolved = null;
        try { resolved = new FontFamily(name); }
        catch { }

        if (resolved != null)
        {
            _familyCache[name] = resolved;
            return resolved;
        }

        // Name matching heuristics for the era's common families.
        string lower = name.ToLowerInvariant();
        FontFamily? guess =
            lower.Contains("mono") || lower.Contains("courier") ? FontFamily.GenericMonospace :
            lower.Contains("sans") || lower.Contains("arial") || lower.Contains("helvetica") ||
            lower.Contains("verdana") || lower.Contains("tahoma") ? FontFamily.GenericSansSerif :
            lower.Contains("serif") || lower.Contains("times") || lower.Contains("georgia")
                ? FontFamily.GenericSerif : null;

        if (guess == null)
            _familyMisses.Add(name);   // don't retry an unknown name forever
        return guess;
    }

    public SizeF Measure(string text, Font font)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TextMeasurer));
        if (string.IsNullOrEmpty(text) || font == null)
            return SizeF.Empty;
        return _measureGfx.MeasureString(text, font, int.MaxValue, _sf);
    }

    public float LineHeight(Font font, float lineHeightMultiplier) =>
        font.GetHeight(_measureGfx) * lineHeightMultiplier;

    /// <summary>
    /// Break text into lines that fit maxWidth.  The space between words
    /// counts toward the line width; a single word wider than maxWidth is
    /// broken at character level.
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

        float spaceW = Measure(" ", font).Width;

        var current = new StringBuilder();
        float currentW = 0f;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            float wordW = Measure(word, font).Width;

            if (wordW > maxWidth)
            {
                // Oversized word: break it at character level
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    currentW = 0f;
                }
                AppendOversizedWord(word, font, maxWidth, lines);
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

        return lines;
    }

    private void AppendOversizedWord(string word, Font font, float maxWidth, List<string> lines)
    {
        var part = new StringBuilder();
        float w = 0f;
        foreach (var ch in word)
        {
            float cw = Measure(ch.ToString(), font).Width;
            if (w + cw > maxWidth && part.Length > 0)
            {
                lines.Add(part.ToString());
                part.Clear();
                w = 0f;
            }
            part.Append(ch);
            w += cw;
        }
        if (part.Length > 0)
            lines.Add(part.ToString());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Fonts first, then the families they were created from — disposing
        // a FontFamily while fonts derived from it are alive is undefined.
        _measureGfx?.Dispose();
        foreach (var font in _fontCache.Values)
            font.Dispose();
        _fontCache.Clear();
        foreach (var ff in _familyCache.Values)
            ff.Dispose();
        _familyCache.Clear();
        _familyMisses.Clear();
    }
}