using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Retro96.Engine.Html;

/// <summary>
/// HTML 2.0 / 3.2 named character entities plus the full Latin-1 set, and
/// numeric references (decimal and hex).  Handles the unterminated-entity
/// typo case (`&amp` with no semicolon) the way period browsers did: decode
/// the longest known entity name at the position.
/// </summary>
public static class HtmlEntities
{
    // Longest body we will consider between '&' and ';' — loose enough for
    // zero-padded hex refs (&#x0010FFFF; = 10 chars), tight enough that
    // ordinary prose like "AT&T;" doesn't go looking for entities.
    private const int MaxEntityBodyLength = 16;

    public static readonly Dictionary<string, string> NamedEntities =
        new(StringComparer.Ordinal)
    {
        // Core markup entities
        { "amp", "&" }, { "lt", "<" }, { "gt", ">" }, { "quot", "\"" },
        { "nbsp", "\u00A0" }, { "copy", "\u00A9" }, { "reg", "\u00AE" },
        { "trade", "\u2122" },

        // Latin-1 upper block
        { "iexcl", "\u00A1" }, { "cent", "\u00A2" }, { "pound", "\u00A3" },
        { "curren", "\u00A4" }, { "yen", "\u00A5" }, { "brvbar", "\u00A6" },
        { "sect", "\u00A7" }, { "uml", "\u00A8" }, { "ordf", "\u00AA" },
        { "laquo", "\u00AB" }, { "not", "\u00AC" }, { "shy", "\u00AD" },
        { "macr", "\u00AF" }, { "deg", "\u00B0" }, { "plusmn", "\u00B1" },
        { "sup2", "\u00B2" }, { "sup3", "\u00B3" }, { "acute", "\u00B4" },
        { "micro", "\u00B5" }, { "para", "\u00B6" }, { "middot", "\u00B7" },
        { "cedil", "\u00B8" }, { "sup1", "\u00B9" }, { "ordm", "\u00BA" },
        { "raquo", "\u00BB" }, { "frac14", "\u00BC" }, { "frac12", "\u00BD" },
        { "frac34", "\u00BE" }, { "iquest", "\u00BF" },

        // Latin-1 accented letters
        { "Agrave", "\u00C0" }, { "Aacute", "\u00C1" }, { "Acirc", "\u00C2" },
        { "Atilde", "\u00C3" }, { "Auml", "\u00C4" }, { "Aring", "\u00C5" },
        { "AElig", "\u00C6" }, { "Ccedil", "\u00C7" }, { "Egrave", "\u00C8" },
        { "Eacute", "\u00C9" }, { "Ecirc", "\u00CA" }, { "Euml", "\u00CB" },
        { "Igrave", "\u00CC" }, { "Iacute", "\u00CD" }, { "Icirc", "\u00CE" },
        { "Iuml", "\u00CF" }, { "ETH", "\u00D0" }, { "Ntilde", "\u00D1" },
        { "Ograve", "\u00D2" }, { "Oacute", "\u00D3" }, { "Ocirc", "\u00D4" },
        { "Otilde", "\u00D5" }, { "Ouml", "\u00D6" }, { "times", "\u00D7" },
        { "Oslash", "\u00D8" }, { "Ugrave", "\u00D9" }, { "Uacute", "\u00DA" },
        { "Ucirc", "\u00DB" }, { "Uuml", "\u00DC" }, { "Yacute", "\u00DD" },
        { "THORN", "\u00DE" }, { "szlig", "\u00DF" },
        { "agrave", "\u00E0" }, { "aacute", "\u00E1" }, { "acirc", "\u00E2" },
        { "atilde", "\u00E3" }, { "auml", "\u00E4" }, { "aring", "\u00E5" },
        { "aelig", "\u00E6" }, { "ccedil", "\u00E7" }, { "egrave", "\u00E8" },
        { "eacute", "\u00E9" }, { "ecirc", "\u00EA" }, { "euml", "\u00EB" },
        { "igrave", "\u00EC" }, { "iacute", "\u00ED" }, { "icirc", "\u00EE" },
        { "iuml", "\u00EF" }, { "eth", "\u00F0" }, { "ntilde", "\u00F1" },
        { "ograve", "\u00F2" }, { "oacute", "\u00F3" }, { "ocirc", "\u00F4" },
        { "otilde", "\u00F5" }, { "ouml", "\u00F6" }, { "divide", "\u00F7" },
        { "oslash", "\u00F8" }, { "ugrave", "\u00F9" }, { "uacute", "\u00FA" },
        { "ucirc", "\u00FB" }, { "uuml", "\u00FC" }, { "yacute", "\u00FD" },
        { "thorn", "\u00FE" }, { "yuml", "\u00FF" },

        // Additional entities common on 1996 pages
        { "mdash", "\u2014" }, { "ndash", "\u2013" }, { "hellip", "\u2026" },
        { "lsquo", "\u2018" }, { "rsquo", "\u2019" }, { "ldquo", "\u201C" },
        { "rdquo", "\u201D" }, { "bull", "\u2022" }, { "dagger", "\u2020" },
        { "Dagger", "\u2021" }, { "permil", "\u2030" }, { "prime", "\u2032" },
        { "Prime", "\u2033" }, { "oline", "\u203E" }, { "frasl", "\u2044" },
        { "euro", "\u20AC" }, { "image", "\u2111" }, { "real", "\u211C" },
        { "larr", "\u2190" }, { "uarr", "\u2191" }, { "rarr", "\u2192" },
        { "darr", "\u2193" }, { "harr", "\u2194" }, { "crarr", "\u21B5" },
        { "forall", "\u2200" }, { "part", "\u2202" }, { "exist", "\u2203" },
        { "empty", "\u2205" }, { "nabla", "\u2207" }, { "isin", "\u2208" },
        { "notin", "\u2209" }, { "ni", "\u220B" }, { "prod", "\u220F" },
        { "sum", "\u2211" }, { "minus", "\u2212" }, { "lowast", "\u2217" },
        { "radic", "\u221A" }, { "prop", "\u221D" }, { "infin", "\u221E" },
        { "ang", "\u2220" }, { "and", "\u2227" }, { "or", "\u2228" },
        { "cap", "\u2229" }, { "cup", "\u222A" }, { "int", "\u222B" },
        { "there4", "\u2234" }, { "sim", "\u223C" }, { "cong", "\u2245" },
        { "asymp", "\u2248" }, { "ne", "\u2260" }, { "equiv", "\u2261" },
        { "le", "\u2264" }, { "ge", "\u2265" }, { "sub", "\u2282" },
        { "sup", "\u2283" }, { "sube", "\u2286" }, { "supe", "\u2287" },
        { "oplus", "\u2295" }, { "otimes", "\u2297" }, { "perp", "\u22A5" },
        { "sdot", "\u22C5" }, { "lceil", "\u2308" }, { "rceil", "\u2309" },
        { "lfloor", "\u230A" }, { "rfloor", "\u230B" }, { "lang", "\u2329" },
        { "rang", "\u232A" }, { "loz", "\u25CA" }, { "spades", "\u2660" },
        { "clubs", "\u2663" }, { "hearts", "\u2665" }, { "diams", "\u2666" }
    };

    private static readonly int _longestEntityName;

    static HtmlEntities()
    {
        foreach (var kvp in NamedEntities)
            _longestEntityName = Math.Max(_longestEntityName, kvp.Key.Length);
    }

    /// <summary>
    /// Decode entities in <paramref name="input"/>.  Supports:
    ///   &amp;   &lt;   &#169;   &#xA9;
    ///   &amp    (no semicolon — longest-prefix match, period-browser tolerance)
    /// A raw &amp; that starts no known entity is emitted literally.
    /// </summary>
    public static string Decode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;
        if (!input.Contains('&'))
            return input;

        var sb = new StringBuilder(input.Length);
        int i = 0;

        while (i < input.Length)
        {
            if (input[i] != '&')
            {
                sb.Append(input[i]);
                i++;
                continue;
            }

            // Try a terminated entity first: &name; or &#NN; / &#xNN;
            int semi = input.IndexOf(';', i + 1);
            if (semi > i + 1 && semi - i - 1 <= MaxEntityBodyLength)
            {
                string body = input.Substring(i + 1, semi - i - 1);
                string? decoded = TryDecodeBody(body);
                if (decoded != null)
                {
                    sb.Append(decoded);
                    i = semi + 1;
                    continue;
                }
            }

            // Unterminated: &name at end / before non-entity char.
            // Try the longest entity name that fits (Netscape behaviour).
            int maxLen = Math.Min(input.Length - (i + 1), _longestEntityName);
            bool matched = false;
            for (int len = maxLen; len >= 2; len--)
            {
                string candidate = input.Substring(i + 1, len);
                if (NamedEntities.TryGetValue(candidate, out var decoded2))
                {
                    // Entity names are alphanumeric; stop at the boundary.
                    int after = i + 1 + len;
                    if (after >= input.Length || !char.IsLetterOrDigit(input[after]))
                    {
                        sb.Append(decoded2);
                        i = after;
                        matched = true;
                        break;
                    }
                }
            }
            if (matched)
                continue;

            // Numeric without semicolon: &#169 (period browsers decoded these too)
            if (i + 2 < input.Length && input[i + 1] == '#')
            {
                int j = i + 2;
                bool hex = j < input.Length && (input[j] == 'x' || input[j] == 'X');
                if (hex) j++;
                int digitsStart = j;
                while (j < input.Length && (char.IsDigit(input[j]) ||
                       (hex && Uri.IsHexDigit(input[j]))))
                    j++;
                int digitCount = j - digitsStart;
                if (digitCount is > 0 and <= 8)
                {
                    string num = input.Substring(digitsStart, digitCount);
                    var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;
                    if (int.TryParse(num, style, CultureInfo.InvariantCulture, out int cp) &&
                        TryCodePointFromUtf16(cp, out var text))
                    {
                        sb.Append(text);
                        i = j;
                        continue;
                    }
                }
            }

            // Not an entity — literal '&'
            sb.Append('&');
            i++;
        }

        return sb.ToString();
    }

    private static string? TryDecodeBody(string body)
    {
        if (body.Length == 0)
            return null;

        // Numeric reference
        if (body[0] == '#')
        {
            string num = body.Substring(1);
            bool hex = num.StartsWith("x", StringComparison.OrdinalIgnoreCase);
            if (hex)
                num = num.Substring(1);

            var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (num.Length is > 0 and <= 8 &&
                int.TryParse(num, style, CultureInfo.InvariantCulture, out int cp) &&
                TryCodePointFromUtf16(cp, out var text))
            {
                return text;
            }
            return null;
        }

        // Named references are case-sensitive in HTML 3.2.
        if (NamedEntities.TryGetValue(body, out var exact))
            return exact;
        return null;
    }

    /// <summary>
    /// Converts a numeric reference to its UTF-16 text.  char.ConvertFromUtf32
    /// THROWS on lone-surrogate code points (0xD800–0xDFFF) — and Decode has
    /// no catch, so a page containing &amp;#xD800; used to take the whole
    /// parse down.  Those are rejected here; 0 becomes the replacement
    /// character (what period engines rendered for &amp;#0;).
    /// </summary>
    private static bool TryCodePointFromUtf16(int cp, out string text)
    {
        text = "";
        if (cp == 0) { text = "\uFFFD"; return true; }
        if (cp < 0 || cp > 0x10FFFF) return false;
        if (cp >= 0xD800 && cp <= 0xDFFF) return false;   // lone surrogate
        text = char.ConvertFromUtf32(cp);
        return true;
    }
}