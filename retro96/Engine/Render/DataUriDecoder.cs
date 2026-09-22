using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Linq;
using System.Text;

namespace Retro96.Engine.Render;

/// <summary>
/// Decodes data: URIs into images.
///
/// Two payload families matter for the era-flavoured pages:
///   data:image/png;base64,… / data:image/gif;base64,… — straight bytes
///     through the regular ImageDecoder;
///   data:image/svg+xml;utf8,&lt;svg…&gt; — the inline rollover-button trick.
///     A 1996 engine has no SVG plugin, but the buttons of the period are
///     uniformly "&lt;rect fill&gt; + &lt;text&gt;" — a tiny rasterizer covers
///     them without dragging in a full SVG stack.
/// </summary>
public static class DataUriDecoder
{
    public static DecodedImage? Decode(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;

        // data:[<mediatype>][;base64],<payload>
        int comma = uri.IndexOf(',');
        if (comma < 5) return null;                    // "data:" alone — junk

        string header = uri[5..comma].Trim();
        string payload = uri[(comma + 1)..];

        bool isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                         || header.Contains(";base64;", StringComparison.OrdinalIgnoreCase);
        string mediaType = header;
        int semi = mediaType.IndexOf(';');
        if (semi >= 0) mediaType = mediaType[..semi];
        mediaType = mediaType.Trim().ToLowerInvariant();

        byte[] bytes;
        try
        {
            if (isBase64)
            {
                // tolerate URL-encoding noise inside base64 segments
                string b64 = Uri.UnescapeDataString(payload)
                                .Replace(" ", "").Replace("\n", "").Replace("\r", "");
                bytes = Convert.FromBase64String(b64);
            }
            else
            {
                bytes = Uri.UnescapeDataString(payload).Trim().StartsWith("<") ||
                        mediaType.Contains("svg")
                    ? Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload))
                    : Encoding.Latin1.GetBytes(Uri.UnescapeDataString(payload));
            }
        }
        catch
        {
            return null;
        }

        if (mediaType.Contains("svg"))
            return RasterizeBasicSvg(bytes);

        // GIF/JPEG/PNG payloads reuse the regular decoder
        return ImageDecoder.Decode(bytes, mediaType.Length > 0 ? mediaType : "");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Minimal SVG subset: <svg width height> <rect width height fill/> and
    // <text x y font-family font-size font-weight fill text-anchor>…
    // Anything unrecognised degrades to the closest era look (solid fill),
    // never to a crash.
    // ─────────────────────────────────────────────────────────────────────

    private static DecodedImage? RasterizeBasicSvg(byte[] bytes)
    {
        string svg;
        try { svg = Encoding.UTF8.GetString(bytes); }
        catch { return null; }

        var (w, h) = SvgRootSize(svg);
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            // every <rect …/> in document order
            foreach (var rect in ExtractElements(svg, "rect"))
            {
                var fill = Attr(rect, "fill") is { } f && TryParseColor(f, out var c)
                    ? c : Color.Black;
                float rx = ParseFloat(Attr(rect, "x")),
                      ry = ParseFloat(Attr(rect, "y")),
                      rw = ParseFloat(Attr(rect, "width"), w),
                      rh = ParseFloat(Attr(rect, "height"), h);
                if (rw <= 0 || rh <= 0) continue;
                using var brush = new SolidBrush(fill);
                g.FillRectangle(brush, rx, ry, rw, rh);
            }

            // every <text …>…</text>
            foreach (var text in ExtractElements(svg, "text"))
            {
                string content = TextContent(svg, text);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var fill = Attr(text, "fill") is { } f && TryParseColor(f, out var c)
                    ? c : Color.Black;
                float size = ParseFloat(Attr(text, "font-size"), 12f);
                string family = Attr(text, "font-family") ?? "Arial";
                bool bold = string.Equals(Attr(text, "font-weight"), "bold",
                    StringComparison.OrdinalIgnoreCase);

                using var font = new Font(new FontFamily(MapFamily(family)), size,
                    bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(fill);

                float tx = ParseFloat(Attr(text, "x"), w / 2f);
                float ty = ParseFloat(Attr(text, "y"), h / 2f);
                string anchor = (Attr(text, "text-anchor") ?? "").ToLowerInvariant();

                using var sf = new StringFormat(StringFormat.GenericTypographic);
                if (anchor == "middle")
                    sf.Alignment = StringAlignment.Center;
                else if (anchor == "end")
                    sf.Alignment = StringAlignment.Far;

                // SVG text y = BASELINE; GDI+ draws from the top — nudge by
                // the ascent so single-line labels sit right.
                float ascent = font.FontFamily.GetCellAscent(font.Style) *
                               font.Size / font.FontFamily.GetEmHeight(font.Style);
                g.DrawString(content, font, brush, tx, ty - ascent, sf);
            }

            return new DecodedImage(
                new List<Bitmap> { bmp }, new List<int> { 0 }, false);
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
    }

    private static (int, int) SvgRootSize(string svg)
    {
        int open = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return (0, 0);
        int close = svg.IndexOf('>', open);
        if (close < 0) return (0, 0);
        string tag = svg[open..close];

        int w = (int)Math.Round(ParseFloat(Attr(tag, "width"), 0));
        int h = (int)Math.Round(ParseFloat(Attr(tag, "height"), 0));
        if (w <= 0 || h <= 0)
        {
            // viewBox="0 0 120 24" fallback
            var vb = Attr(tag, "viewBox");
            if (vb != null)
            {
                var parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4)
                {
                    w = (int)Math.Round(ParseFloat(parts[2], 0));
                    h = (int)Math.Round(ParseFloat(parts[3], 0));
                }
            }
        }
        return (w, h);
    }

    /// <summary>Yields the full tag text of every &lt;name …&gt; element.</summary>
    private static IEnumerable<string> ExtractElements(string svg, string name)
    {
        int i = 0;
        while (true)
        {
            int open = svg.IndexOf('<' + name, i, StringComparison.OrdinalIgnoreCase);
            if (open < 0) yield break;
            // avoid matching <textpath> when looking for <text>
            char after = open + 1 + name.Length < svg.Length
                ? svg[open + 1 + name.Length] : ' ';
            if (!char.IsWhiteSpace(after) && after != '>' && after != '/')
            {
                i = open + 1;
                continue;
            }
            int close = svg.IndexOf('>', open);
            if (close < 0) yield break;
            yield return svg[open..close];
            i = close;
        }
    }

    /// <summary>Inner text of the element starting at tagStart (a full tag).</summary>
    private static string TextContent(string svg, string tag)
    {
        int gt = tag.IndexOf('>');
        if (gt < 0) return "";
        // recover the position of this tag inside the document by matching
        // its opening angle position — tags are yielded in order, so search
        int docIdx = svg.IndexOf(tag, StringComparison.Ordinal);
        if (docIdx < 0) return "";
        int contentStart = docIdx + tag.Length + 1;
        int end = svg.IndexOf("</text", contentStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return "";
        return svg[contentStart..end].Trim();
    }

    private static string? Attr(string tag, string name)
    {
        // name="value" or name='value'
        foreach (var sep in new[] { '"', '\'' })
        {
            int key = tag.IndexOf(name + "=" + sep, StringComparison.OrdinalIgnoreCase);
            if (key >= 0)
            {
                int valStart = key + name.Length + 2;
                int valEnd = tag.IndexOf(sep, valStart);
                if (valEnd > valStart)
                    return tag[valStart..valEnd];
            }
        }
        return null;
    }

    private static float ParseFloat(string? s, float def = 0f)
    {
        if (string.IsNullOrEmpty(s)) return def;
        // percentages of the root size are resolved by the caller; here just px
        s = s.Trim().TrimEnd('%', 'p', 'x');
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
    }

    private static bool TryParseColor(string s, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (s.StartsWith('#'))
            {
                string hex = s[1..];
                if (hex.Length == 3)
                    hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
                if (hex.Length == 6)
                {
                    color = Color.FromArgb(
                        Convert.ToInt32(hex[..2], 16),
                        Convert.ToInt32(hex[2..4], 16),
                        Convert.ToInt32(hex[4..6], 16));
                    return true;
                }
                return false;
            }
            color = Color.FromName(s);           // "yellow", "red", "white", …
            return color != Color.Empty && !color.IsNamedColorInvalid();
        }
        catch { return false; }
    }

    private static string MapFamily(string family)
    {
        string f = (family ?? "").Trim().Trim('\'', '"');
        string first = f.Split(',')[0].Trim().ToLowerInvariant();
        return first switch
        {
            "times" or "times new roman" or "serif"      => "Times New Roman",
            "courier" or "courier new" or "monospace"    => "Courier New",
            "verdana" or "geneva"                        => "Verdana",
            "georgia"                                    => "Georgia",
            "comic" or "comic sans ms"                   => "Comic Sans MS",
            _                                             => "Arial"
        };
    }
}

internal static class ColorCheckExt
{
    /// <summary>Color.FromName returns a phantom ARGB=0 named color for
    /// unknown names — detect that without relying on KnownColor checks.</summary>
    public static bool IsNamedColorInvalid(this Color c) =>
        c.A == 0 && c.R == 0 && c.G == 0 && c.B == 0;
}
