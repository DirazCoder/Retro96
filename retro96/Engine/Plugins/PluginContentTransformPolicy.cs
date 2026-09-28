using System.Text;
using Retro96.Engine.Html;

namespace Retro96.Plugins;

internal static class PluginContentTransformPolicy
{
    public const int MaxPatternLength = 2048;

    public static bool ScopeMatches(PluginContentTransformScope scope, string url, string mime)
        => GlobMatches(scope.UrlPattern, url) && GlobMatches(scope.MimePattern, mime);

    public static bool GlobMatches(string pattern, string value)
    {
        pattern = (pattern ?? string.Empty).Trim();
        value ??= string.Empty;
        if (pattern.Length == 0 || pattern.Length > MaxPatternLength)
            return false;

        int p = 0, v = 0, star = -1, match = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++; v++; continue;
            }
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++; match = v; continue;
            }
            if (star >= 0)
            {
                p = star + 1;
                v = ++match;
                continue;
            }
            return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public static bool IsSafeTransformMimePattern(string pattern)
    {
        pattern = (pattern ?? string.Empty).Trim();
        if (pattern.Length is < 3 or > MaxPatternLength) return false;
        int slash = pattern.IndexOf('/');
        return slash > 0 && slash < pattern.Length - 1 &&
               !pattern.Contains('"') && !pattern.Contains('\'') &&
               !pattern.Contains('\\') && !pattern.Contains('\n') && !pattern.Contains('\r');
    }

    public static bool IsSafeTransformUrlPattern(string pattern)
    {
        pattern = (pattern ?? string.Empty).Trim();
        return pattern.Length is > 0 and <= MaxPatternLength &&
               !pattern.Contains('"') && !pattern.Contains('\'') &&
               !pattern.Contains('\\') && !pattern.Contains('\n') && !pattern.Contains('\r');
    }

    public static string SanitizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var output = new StringBuilder(Math.Min(html.Length + 32, 8 * 1024 * 1024));
        int index = 0;
        while (index < html.Length)
        {
            int lt = html.IndexOf('<', index);
            if (lt < 0)
            {
                output.Append(html, index, html.Length - index);
                break;
            }
            if (lt > index)
                output.Append(html, index, lt - index);

            if (StartsWithTag(html, lt, "script"))
            {
                int end = FindTagEnd(html, lt);
                if (end < 0) break;
                int close = html.IndexOf("</script", end + 1, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                int closeEnd = FindTagEnd(html, close);
                index = closeEnd >= 0 ? closeEnd + 1 : html.Length;
                continue;
            }

            int gt = FindTagEnd(html, lt);
            if (gt < 0)
            {
                output.Append(html, lt, html.Length - lt);
                break;
            }

            string fragment = html.Substring(lt, gt - lt + 1);
            if (TrySanitizeStartTag(fragment, out string sanitized))
                output.Append(sanitized);
            else
                output.Append(fragment);
            index = gt + 1;
        }
        return output.ToString();
    }

    private static bool TrySanitizeStartTag(string fragment, out string sanitized)
    {
        sanitized = fragment;
        foreach (var token in HtmlTokenizer.Tokenize(fragment))
        {
            if (token is not StartTag start)
                return false;

            var attrs = new StringBuilder();
            foreach (var pair in start.Attrs)
            {
                string name = pair.Key ?? string.Empty;
                string value = pair.Value ?? string.Empty;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsJavascriptUrl(value))
                    continue;
                attrs.Append(' ').Append(name);
                if (pair.Value != null && value.Length > 0)
                    attrs.Append("=\"").Append(EscapeAttribute(value)).Append('\"');
            }

            sanitized = "<" + start.Name + attrs + (start.SelfClosing ? "/>" : ">");
            return true;
        }
        return false;
    }

    private static bool IsJavascriptUrl(string value)
    {
        string normalized = HtmlEntities.Decode(value ?? string.Empty);
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized.TrimStart())
        {
            if (c <= 0x20 || c == '\u007f') continue;
            sb.Append(c);
        }
        return sb.ToString().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);

    private static bool StartsWithTag(string html, int index, string name)
    {
        if (index + name.Length + 1 > html.Length || html[index] != '<') return false;
        if (!html.AsSpan(index + 1, name.Length).Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
        int next = index + 1 + name.Length;
        return next >= html.Length || char.IsWhiteSpace(html[next]) || html[next] == '>' || html[next] == '/';
    }

    private static int FindTagEnd(string html, int start)
    {
        char quote = '\0';
        for (int i = start + 1; i < html.Length; i++)
        {
            char c = html[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '>') return i;
        }
        return -1;
    }
}
