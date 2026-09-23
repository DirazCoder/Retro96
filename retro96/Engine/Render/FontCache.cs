using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Globalization;
using System.Text;

namespace Retro96.Engine.Render;

/// <summary>
/// Resolves and caches System.Drawing.Font objects.  Thread-safe: layout
/// runs after async continuations and image prefetches can overlap, so
/// every cache access is serialized.
///
/// FACE= fallback chain per the era: each comma-separated family is tried
/// in order against the installed fonts, generic CSS families map to GDI+
/// generics, name heuristics catch the common period families ("courier",
/// "helvetica", "verdana"…), and the platform default (serif) catches the
/// rest.  Bold/italic variants that a family lacks are synthesised by
/// GDI+ automatically.
/// </summary>
public class FontCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Font> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontFamily> _familyCache =
        new(StringComparer.OrdinalIgnoreCase);
    // Families created via new FontFamily(...) own a GDI+ handle and must
    // be disposed on teardown; the Generic* statics are process-wide and
    // never are.
    private readonly HashSet<FontFamily> _ownedFamilies = new();
    private bool _disposed;

    /// <summary>Resolve a font from a CSS/HTML font-family list.</summary>
    public Font Resolve(IEnumerable<string>? familyList, float sizePx, bool bold, bool italic)
        => Resolve(familyList, sizePx, bold ? 700 : 400, italic, false);

    public Font Resolve(IEnumerable<string>? familyList, float sizePx, int weight, bool italic, bool oblique = false)
    {
        // The Font constructor throws on non-positive or NaN sizes — a
        // stray unresolved style must not take the whole paint down.
        if (float.IsNaN(sizePx) || sizePx <= 0f) sizePx = 16f;

        lock (_lock)
        {
            string key = BuildKey(familyList, sizePx, weight, italic, oblique);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            FontStyle style = weight >= 700 ? FontStyle.Bold : FontStyle.Regular;
            if (italic) style |= FontStyle.Italic;

            Font? font = null;
            if (familyList != null)
            {
                foreach (var familyName in familyList)
                {
                    var trimmed = familyName?.Trim().Trim('\'', '"');
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    try
                    {
                        var family = GetOrCreateFamily(trimmed);
                        font = new Font(family, sizePx, style, GraphicsUnit.Pixel, weight, oblique);
                        break;
                    }
                    catch
                    {
                        continue;   // try the next name in the list
                    }
                }
            }

            // Platform default
            font ??= new Font(FontFamily.GenericSerif, sizePx, style, GraphicsUnit.Pixel, weight, oblique);

            if (_disposed)
                return font;   // cache is gone (shutdown) — hand out an uncached one

            _cache[key] = font;
            return font;
        }
    }

    // caller holds _lock
    private FontFamily GetOrCreateFamily(string name)
    {
        if (_familyCache.TryGetValue(name, out var cached))
            return cached;

        var family = ResolveFamily(name);
        _familyCache[name] = family;
        return family;
    }

    // caller holds _lock
    private FontFamily ResolveFamily(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "serif":
                return FontFamily.GenericSerif;
            case "sans-serif":
                return FontFamily.GenericSansSerif;
            case "helvetica":
            case "verdana":
            case "tahoma":
                return FontFamily.GenericSansSerif;
            case "arial":
                // The REAL Arial, not GenericSansSerif (MS Sans Serif on
                // Windows — a bitmap-strike family whose drawn baseline
                // does not track FontFamily.GetCellAscent math, which put
                // neighbouring words a couple of pixels apart on
                // Arial-styled pages).  Falls back to the generic when the
                // family is absent (stock Win3.1/NT boxes).
                return TryOwnedFamily("Arial") ?? FontFamily.GenericSansSerif;
            case "monospace":
            case "mono":
            case "fixed":
                return FontFamily.GenericMonospace;
            case "courier":
            case "courier new":
                return TryOwnedFamily("Courier New") ?? FontFamily.GenericMonospace;
            case "times":
            case "times new roman":
                return TryOwnedFamily("Times New Roman") ?? FontFamily.GenericSerif;
            case "cursive":
            case "comic sans ms":
                return TryOwnedFamily("Comic Sans MS") ?? FontFamily.GenericSerif;
            case "fantasy":
            case "impact":
                return TryOwnedFamily("Impact") ?? FontFamily.GenericSerif;
            default:
                try { return OwnedFamily(name); }
                catch
                {
                    // Loose heuristics for near-miss family names
                    var lower = name.ToLowerInvariant();
                    if (lower.Contains("mono") || lower.Contains("courier"))
                        return FontFamily.GenericMonospace;
                    if (lower.Contains("sans") || lower.Contains("arial") ||
                        lower.Contains("helvetica") || lower.Contains("verdana"))
                        return FontFamily.GenericSansSerif;
                    // serif / times / georgia / book … and everything else
                    return FontFamily.GenericSerif;
                }
        }
    }

    // caller holds _lock
    private FontFamily OwnedFamily(string name)
    {
        var family = new FontFamily(name);   // throws when the family is absent
        _ownedFamilies.Add(family);
        return family;
    }

    // caller holds _lock
    private FontFamily? TryOwnedFamily(string name)
    {
        try { return OwnedFamily(name); }
        catch { return null; }
    }

    private static string BuildKey(IEnumerable<string>? familyList, float sizePx, int weight, bool italic, bool oblique)
    {
        var sb = new System.Text.StringBuilder();
        if (familyList != null)
            foreach (var f in familyList) sb.Append(f).Append(',');
        sb.Append('|').Append(sizePx.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
          .Append('|').Append(Math.Clamp(weight, 100, 900))
          .Append(italic ? 'i' : ' ')
          .Append(oblique ? 'o' : ' ');
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var font in _cache.Values)
                font.Dispose();
            _cache.Clear();

            foreach (var family in _ownedFamilies)
                family.Dispose();
            _ownedFamilies.Clear();

            // Whatever remains in the family cache is a Generic* static —
            // process-wide, never disposed.
            _familyCache.Clear();
        }
    }
}