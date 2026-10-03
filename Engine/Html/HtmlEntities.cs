using System;
using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Html;

public static class HtmlEntities
{
    public static readonly Dictionary<string, string> NamedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        // HTML 3.2 entities
        { "amp", "&" },
        { "lt", "<" },
        { "gt", ">" },
        { "quot", "\"" },
        { "nbsp", "\u00A0" },
        { "copy", "\u00A9" },
        { "reg", "\u00AE" },
        { "trade", "\u2122" },
        { "iexcl", "\u00A1" },
        { "cent", "\u00A2" },
        { "pound", "\u00A3" },
        { "curren", "\u00A4" },
        { "yen", "\u00A5" },
        { "brvbar", "\u00A6" },
        { "sect", "\u00A7" },
        { "uml", "\u00A8" },
        { "ordf", "\u00AA" },
        { "laquo", "\u00AB" },
        { "not", "\u00AC" },
        { "shy", "\u00AD" },
        { "macr", "\u00AF" },
        { "deg", "\u00B0" },
        { "plusmn", "\u00B1" },
        { "sup2", "\u00B2" },
        { "sup3", "\u00B3" },
        { "acute", "\u00B4" },
        { "micro", "\u00B5" },
        { "para", "\u00B6" },
        { "middot", "\u00B7" },
        { "cedil", "\u00B8" },
        { "sup1", "\u00B9" },
        { "ordm", "\u00BA" },
        { "raquo", "\u00BB" },
        { "frac14", "\u00BC" },
        { "frac12", "\u00BD" },
        { "frac34", "\u00BE" },
        { "iquest", "\u00BF" },
        { "agrave", "\u00E0" },
        { "aacute", "\u00E1" },
        { "acirc", "\u00E2" },
        { "atilde", "\u00E3" },
        { "auml", "\u00E4" },
        { "aring", "\u00E5" },
        { "aelig", "\u00E6" },
        { "ccedil", "\u00E7" },
        { "egrave", "\u00E8" },
        { "eacute", "\u00E9" },
        { "ecirc", "\u00EA" },
        { "euml", "\u00EB" },
        { "igrave", "\u00EC" },
        { "iacute", "\u00ED" },
        { "icirc", "\u00EE" },
        { "iuml", "\u00EF" },
        { "eth", "\u00F0" },
        { "ntilde", "\u00F1" },
        { "ograve", "\u00F2" },
        { "oacute", "\u00F3" },
        { "ocirc", "\u00F4" },
        { "otilde", "\u00F5" },
        { "ouml", "\u00F6" },
        { "divide", "\u00F7" },
        { "oslash", "\u00F8" },
        { "ugrave", "\u00F9" },
        { "uacute", "\u00FA" },
        { "ucirc", "\u00FB" },
        { "uuml", "\u00FC" },
        { "yacute", "\u00FD" },
        { "thorn", "\u00FE" },
        { "yuml", "\u00FF" },

        // Additional HTML 4 entities commonly used by 1996
        { "mdash", "\u2014" },
        { "ndash", "\u2013" },
        { "hellip", "\u2026" },
        { "lsquo", "\u2018" },
        { "rsquo", "\u2019" },
        { "ldquo", "\u201C" },
        { "rdquo", "\u201D" },
        { "bull", "\u2022" },
        { "prime", "\u2032" },
        { "oline", "\u203E" },
        { "frasl", "\u2044" },
        { "euro", "\u20AC" },
        { "image", "\u2111" },
        { "real", "\u211C" },
        { "alefsym", "\u2135" },
        { "larr", "\u2190" },
        { "uarr", "\u2191" },
        { "rarr", "\u2192" },
        { "darr", "\u2193" },
        { "harr", "\u2194" },
        { "crarr", "\u21B5" },
        { "forall", "\u2200" },
        { "part", "\u2202" },
        { "exist", "\u2203" },
        { "empty", "\u2205" },
        { "nabla", "\u2207" },
        { "isin", "\u2208" },
        { "notin", "\u2209" },
        { "ni", "\u220B" },
        { "prod", "\u220F" },
        { "sum", "\u2211" },
        { "minus", "\u2212" },
        { "lowast", "\u2217" },
        { "radic", "\u221A" },
        { "prop", "\u221D" },
        { "infin", "\u221E" },
        { "ang", "\u2220" },
        { "and", "\u2227" },
        { "or", "\u2228" },
        { "cap", "\u2229" },
        { "cup", "\u222A" },
        { "int", "\u222B" },
        { "there4", "\u2234" },
        { "sim", "\u223C" },
        { "cong", "\u2245" },
        { "asymp", "\u2248" },
        { "ne", "\u2260" },
        { "equiv", "\u2261" },
        { "le", "\u2264" },
        { "ge", "\u2265" },
        { "sub", "\u2282" },
        { "sup", "\u2283" },
        { "sube", "\u2286" },
        { "supe", "\u2287" },
        { "oplus", "\u2295" },
        { "otimes", "\u2297" },
        { "perp", "\u22A5" },
        { "sdot", "\u22C5" },
        { "lceil", "\u2308" },
        { "rceil", "\u2309" },
        { "lfloor", "\u230A" },
        { "rfloor", "\u230B" },
        { "lang", "\u2329" },
        { "rang", "\u232A" },
        { "loz", "\u25CA" },
        { "spades", "\u2660" },
        { "clubs", "\u2663" },
        { "hearts", "\u2665" },
        { "diams", "\u2666" }
    };

    public static string Decode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var sb = new StringBuilder();
        int i = 0;

        while (i < input.Length)
        {
            if (input[i] == '&')
            {
                // Try to decode an entity
                int end = input.IndexOf(';', i);
                if (end > i)
                {
                    string entity = input.Substring(i + 1, end - i - 1);
                    string? decoded = TryDecodeEntity(entity);
                    if (decoded != null)
                    {
                        sb.Append(decoded);
                        i = end + 1;
                        continue;
                    }
                }
            }
            sb.Append(input[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string? TryDecodeEntity(string entity)
    {
        if (string.IsNullOrEmpty(entity))
            return null;

        // Check for named entity
        if (NamedEntities.TryGetValue(entity, out var value))
            return value;

        // Check for numeric entity (decimal: &#NNN;)
        if (entity.StartsWith("#"))
        {
            string numStr = entity.Substring(1);
            int baseNum = 10;

            // Check for hex entity (&#xNN;)
            if (numStr.StartsWith("x") || numStr.StartsWith("X"))
            {
                numStr = numStr.Substring(1);
                baseNum = 16;
            }

            if (int.TryParse(numStr, baseNum == 16 ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                null, out int codePoint))
            {
                return char.ConvertFromUtf32(codePoint);
            }
        }

        return null;
    }
}