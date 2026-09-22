// Retro96.Graphics — font resolution and measurement over SkiaSharp.
//
// The 1996 font stack resolves through fontconfig exactly the way Chromium
// does on this same machine ("Times New Roman" → Liberation Serif, "Arial"
// → Liberation Sans, "Courier New" → Liberation Mono), which means the
// engine and the reference browser measure text with the SAME metric-
// compatible faces — the property that makes the geometry-diff harness
// meaningful.
//
// Family resolution rules (the FACE= fallback chain of the era):
//   • CSS generic families map to concrete stacks,
//   • period families resolve directly or through metric-compatible aliases,
//   • unknown families throw ArgumentException from the FontFamily ctor —
//     the signal TextMeasurer/FontCache catch to walk to the next name.
using System.Collections.Concurrent;

using SkiaSharp;

namespace Retro96.Drawing;

[Flags]
public enum FontStyle
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikeout = 8,
}

public enum GraphicsUnit
{
    World,
    Display,
    Pixel,
    Point,
    Inch,
    Document,
    Millimeter,
}

/// <summary>
/// Central font-family resolution + typeface cache.  Thread-safe: layout,
/// async image prefetches and shell paints overlap in the real browser.
/// </summary>
internal static class FontCatalog
{
    private static readonly ConcurrentDictionary<string, SKTypeface?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // requested name → candidate family names, first match wins.  Includes
    // the metric-compatible replacements so "Arial" keeps true Arial metrics
    // (via Liberation Sans) even on hosts without the licensed original.
    private static readonly string[][] AliasGroups =
    {
        new[] { "Times New Roman", "Tinos", "Liberation Serif", "DejaVu Serif" },
        new[] { "Arial", "Arimo", "Liberation Sans", "Helvetica", "DejaVu Sans" },
        new[] { "Helvetica", "Nimbus Sans", "Liberation Sans", "Arial", "DejaVu Sans" },
        new[] { "Courier New", "Nimbus Mono PS", "Liberation Mono", "DejaVu Sans Mono", "Noto Sans Mono" },
        new[] { "Verdana", "DejaVu Sans", "Liberation Sans" },
        new[] { "Tahoma", "DejaVu Sans", "Liberation Sans" },
        new[] { "Geneva", "DejaVu Sans", "Liberation Sans" },
        new[] { "Georgia", "Gelasio", "Liberation Serif", "DejaVu Serif" },
        new[] { "MS Sans Serif", "Microsoft Sans Serif", "DejaVu Sans", "Liberation Sans" },
        new[] { "Comic Sans MS", "Carlito", "DejaVu Sans", "Liberation Sans" },
        new[] { "Impact", "Liberation Sans", "DejaVu Sans" },
        new[] { "Terminal", "Liberation Mono", "DejaVu Sans Mono" },
        new[] { "Lucida Console", "Liberation Mono", "DejaVu Sans Mono" },
    };

    /// <summary>
    /// Resolves a family name to a typeface, or null when nothing (real or
    /// metric-compatible) matches — GDI's "family absent" signal.
    /// </summary>
    public static SKTypeface? Resolve(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return null;

        string name = familyName.Trim().Trim('\'', '"');
        return Cache.GetOrAdd(name, ResolveUncached);
    }

    private static SKTypeface? ResolveUncached(string name)
    {
        // CSS generic families first — the concrete stacks of this host.
        switch (name.ToLowerInvariant())
        {
            case "serif":
                return TryEach("Liberation Serif", "Tinos", "DejaVu Serif");
            case "sans-serif":
            case "sans":
                return TryEach("DejaVu Sans", "Liberation Sans");
            case "monospace":
            case "mono":
            case "fixed":
                return TryEach("DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono");
            case "cursive":
            case "fantasy":
                return TryEach("DejaVu Serif", "Liberation Serif");
        }

        foreach (var group in AliasGroups)
        {
            bool requestedInGroup = false;
            foreach (var candidate in group)
            {
                if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
                { requestedInGroup = true; break; }
            }
            if (!requestedInGroup) continue;

            foreach (var candidate in group)
            {
                var tf = SKTypeface.FromFamilyName(candidate, SKFontStyle.Normal);
                if (tf == null) continue;
                // Accept only if the manager really resolved to a family in
                // this group — FromFamilyName silently substitutes the
                // default typeface for unknown names, which must read as
                // "absent" so the fallback chain keeps walking.
                foreach (var known in group)
                    if (tf.FamilyName.Equals(known, StringComparison.OrdinalIgnoreCase))
                        return tf;
                tf.Dispose();
            }
            return null;   // era family is genuinely absent on this host
        }

        // A plain family name: accept only an exact family match.
        var direct = SKTypeface.FromFamilyName(name, SKFontStyle.Normal);
        if (direct != null && direct.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase))
            return direct;
        direct?.Dispose();
        return null;
    }

    private static SKTypeface? TryEach(params string[] names)
    {
        foreach (var n in names)
        {
            var tf = SKTypeface.FromFamilyName(n, SKFontStyle.Normal);
            if (tf != null && tf.FamilyName.Equals(n, StringComparison.OrdinalIgnoreCase))
                return tf;
            tf?.Dispose();
        }
        return null;
    }
}

public sealed class FontFamily : IDisposable
{
    internal SKTypeface Typeface { get; }

    /// <summary>The family name the host actually resolved to.</summary>
    public string Name { get; }

    private static FontFamily? _genericSerif;
    private static FontFamily? _genericSansSerif;
    private static FontFamily? _genericMonospace;

    public static FontFamily GenericSerif =>
        _genericSerif ??= new FontFamily("Times New Roman");

    public static FontFamily GenericSansSerif =>
        _genericSansSerif ??= new FontFamily("Arial");

    public static FontFamily GenericMonospace =>
        _genericMonospace ??= new FontFamily("Courier New");

    /// <summary>
    /// Resolves a family by name; throws ArgumentException when the family
    /// (nor a metric-compatible stand-in) exists — GDI+ semantics, which the
    /// FACE= fallback chains in TextMeasurer/FontCache depend on.
    /// </summary>
    public FontFamily(string familyName)
    {
        var tf = FontCatalog.Resolve(familyName)
                 ?? throw new ArgumentException(
                     $"Font family '{familyName}' could not be resolved.", nameof(familyName));
        Typeface = tf;
        Name = tf.FamilyName;
    }

    internal FontFamily(SKTypeface typeface, string name)
    {
        Typeface = typeface;
        Name = name;
    }

    // ── Design-unit metrics (callers only use the ratios between these) ──
    // Normalised to a 1000-unit em: a probe font at size 1000 makes the
    // Skia metrics land directly in "design units".

    private const float ProbeSize = 1000f;
    private static readonly ConcurrentDictionary<SKTypeface, (float Ascent, float Descent, float LineSpacing)> MetricCache = new();

    private (float Ascent, float Descent, float LineSpacing) Probe
    {
        get
        {
            if (MetricCache.TryGetValue(Typeface, out var cached))
                return cached;
            using var probe = new SKFont(Typeface, ProbeSize);
            var m = probe.Metrics;
            var result = (-m.Ascent, m.Descent, m.Descent - m.Ascent + m.Leading);
            MetricCache[Typeface] = result;
            return result;
        }
    }

    public int GetCellAscent(FontStyle style) => (int)MathF.Round(Probe.Ascent);
    public int GetCellDescent(FontStyle style) => (int)MathF.Round(Probe.Descent);
    public int GetLineSpacing(FontStyle style) => (int)MathF.Round(Probe.LineSpacing);
    public int GetEmHeight(FontStyle style) => (int)ProbeSize;

    public override string ToString() => Name;

    public void Dispose()
    {
        // Typefaces are catalog-cached and shared; dropping the reference is
        // all a family owns.
    }
}

public sealed class Font : IDisposable
{
    internal SKFont SkFont { get; }
    internal SKTypeface Typeface { get; }

    public FontFamily FontFamily { get; }
    public float Size { get; }             // in GraphicsUnit (engine uses Pixel)
    public FontStyle Style { get; }
    public GraphicsUnit Unit { get; }

    private readonly float _lineHeight;
    private readonly float _ascent;

    public Font(FontFamily family, float size, FontStyle style, GraphicsUnit unit)
    {
        if (family == null) throw new ArgumentNullException(nameof(family));
        if (float.IsNaN(size) || size <= 0f) size = 16f;

        FontFamily = family;
        Style = style;
        Unit = unit;

        float px = unit == GraphicsUnit.Pixel
            ? size
            : size * 96f / 72f;             // Point at 96 dpi
        Size = size;

        bool bold = (style & FontStyle.Bold) != 0;
        bool italic = (style & FontStyle.Italic) != 0;

        // Prefer the family's REAL bold/italic face (Liberation has them
        // all); fall back to the regular face + synthesis when absent.
        var wanted = new SKFontStyle(
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var styled = SKTypeface.FromFamilyName(family.Name, wanted);
        if (styled != null &&
            styled.FamilyName.Equals(family.Name, StringComparison.OrdinalIgnoreCase))
        {
            Typeface = styled;
        }
        else
        {
            styled?.Dispose();
            Typeface = family.Typeface;
        }

        SkFont = new SKFont(Typeface, px)
        {
            Edging = SKFontEdging.Antialias,
        };

        // Families without a real bold face get Skia's synthetic embolden —
        // the equivalent of GDI+'s automatic bold synthesis.
        if (bold && Typeface.FontStyle.Weight < 550)
            SkFont.Embolden = true;

        var m = SkFont.Metrics;
        _ascent = -m.Ascent;
        _lineHeight = m.Descent - m.Ascent + m.Leading;
    }

    public Font(FontFamily family, float size, FontStyle style)
        : this(family, size, style, GraphicsUnit.Pixel) { }

    public Font(FontFamily family, float size)
        : this(family, size, FontStyle.Regular, GraphicsUnit.Pixel) { }

    public string Name => FontFamily.Name;
    public bool Bold => (Style & FontStyle.Bold) != 0;
    public bool Italic => (Style & FontStyle.Italic) != 0;

    /// <summary>Distance from the line top to the baseline, in pixels.</summary>
    internal float AscentPx => _ascent;

    /// <summary>The full line height (ascent + descent + leading) in pixels —
    /// what GDI+ Font.GetHeight returned.</summary>
    public float GetHeight() => _lineHeight;

    public float GetHeight(Graphics? g) => _lineHeight;

    /// <summary>Advance width of a string in pixels (trailing spaces included).</summary>
    internal float MeasureText(string text) =>
        string.IsNullOrEmpty(text) ? 0f : SkFont.MeasureText(text);

    public void Dispose()
    {
        // SKFont is plain managed state; nothing unmanaged to release.
    }

    public override string ToString() => $"[{FontFamily.Name}, {Size:0.##}px, {Style}]";
}

/// <summary>Shell/UI fallback fonts for the pieces that had no CSS context.</summary>
public static class SystemFonts
{
    private static Font? _default;
    private static Font? _smallCaption;

    public static Font DefaultFont =>
        _default ??= new Font(FontFamily.GenericSansSerif, 12f, FontStyle.Regular, GraphicsUnit.Pixel);

    public static Font SmallCaptionFont =>
        _smallCaption ??= new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
}
