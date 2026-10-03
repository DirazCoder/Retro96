using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace Retro96.Engine.Render;

/// <summary>
/// Resolves and caches System.Drawing.Font objects.
/// Tries font family names in order (CSS font-family list).
/// Final fallback: FontFamily.GenericSerif.
/// </summary>
public class FontCache : IDisposable
{
    private readonly Dictionary<string, Font> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontFamily> _familyCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Resolve a font from a CSS font-family list.
    /// Tries each family in order; falls back to GenericSerif.
    /// </summary>
    public Font Resolve(IEnumerable<string> familyList, float sizePx, bool bold, bool italic)
    {
        var key = BuildKey(familyList, sizePx, bold, italic);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        FontStyle style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;

        Font? font = null;

        foreach (var familyName in familyList)
        {
            var trimmed = familyName.Trim().Trim('\'', '\"');
            if (string.IsNullOrEmpty(trimmed))
                continue;

            try
            {
                var family = GetOrCreateFamily(trimmed);
                font = new Font(family, sizePx, style, GraphicsUnit.Pixel);
                break;
            }
            catch
            {
                // Try next family
                continue;
            }
        }

        // Final fallback
        if (font == null)
        {
            font = new Font(FontFamily.GenericSerif, sizePx, style, GraphicsUnit.Pixel);
        }

        _cache[key] = font;
        return font;
    }

    /// <summary>
    /// Get or create a FontFamily, caching the result.
    /// </summary>
    private FontFamily GetOrCreateFamily(string name)
    {
        if (_familyCache.TryGetValue(name, out var family))
            return family;

        try
        {
            family = new FontFamily(name);
        }
        catch
        {
            // Try with standard mappings
            var lower = name.ToLowerInvariant();
            if (lower.Contains("serif") && !lower.Contains("sans"))
                family = FontFamily.GenericSerif;
            else if (lower.Contains("sans") || lower.Contains("arial") || lower.Contains("helvetica"))
                family = FontFamily.GenericSansSerif;
            else if (lower.Contains("mono") || lower.Contains("courier"))
                family = new FontFamily("Courier New");
            else
                family = FontFamily.GenericSerif; // Final fallback
        }

        _familyCache[name] = family;
        return family;
    }

    /// <summary>
    /// Build a cache key for the font.
    /// </summary>
    private static string BuildKey(IEnumerable<string> familyList, float sizePx, bool bold, bool italic)
    {
        var families = string.Join(",", familyList);
        return $"{families}|{sizePx}|{bold}|{italic}";
    }

    /// <summary>
    /// Dispose all cached fonts and font families.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var font in _cache.Values)
            font.Dispose();

        foreach (var family in _familyCache.Values)
        {
            if (!family.Equals(FontFamily.GenericSerif) && 
                !family.Equals(FontFamily.GenericSansSerif))
            {
                family.Dispose();
            }
        }

        _cache.Clear();
        _familyCache.Clear();
    }
}
