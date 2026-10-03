using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Network;

public class UnsafeUrlException : Exception
{
    public UnsafeUrlException(string message) : base(message) { }
}

/// <summary>
/// URL parsing and RFC-style relative resolution.
///
/// The 1996-relevant surface:
///   • http/https URLs with explicit or default ports
///   • mailto: and javascript: as opaque scheme URLs (a javascript: href
///     is a script to run, not a document to fetch — the shell decides)
///   • about:blank
///   • bare "www.host.com/path" typed into the address bar (scheme-less)
///   • relative resolution: ../ ./ /root protocol-relative //host and
///     plain relative, with the base's trailing slash PRESERVED (dropping
///     it silently re-based every relative link on directory URLs)
///   • resolved paths/queries are percent-escaped — a relative href with
///     a space ("my page.html") used to reach the wire as
///     "GET /my page.html", breaking the request line.  Valid %XX escapes
///     pass through untouched; fragments stay raw so the shell's
///     anchor-name comparisons keep working.
/// </summary>
public record ParsedUrl(string Scheme, string Host, int Port, string Path, string Query, string Fragment)
{
    private static readonly Regex _schemeRegex =
        new("^[a-zA-Z][a-zA-Z0-9+.\\-]*:", RegexOptions.Compiled);

    public static ParsedUrl Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Empty URL");

        string url = raw.Trim();
        AssertSafe(url);

        // ── Opaque schemes: mailto:, javascript:, about:, … ────────────────
        // Anything with a scheme we don't treat as hierarchical.
        var schemeMatch = _schemeRegex.Match(url);
        if (schemeMatch.Success)
        {
            string scheme = schemeMatch.Value[..^1].ToLowerInvariant();
            string rest = url[schemeMatch.Value.Length..];

            switch (scheme)
            {
                case "about":
                    return new ParsedUrl("about", "", 0, rest.Length > 0 ? rest : "blank", "", "");

                case "mailto":
                case "javascript":
                    return new ParsedUrl(scheme, "", 0, rest, "", "");

                case "retro96":
                    return new ParsedUrl(scheme, "", 0,
                        rest.Trim().TrimStart('/'), "", "");

                case "http":
                case "https":
                    break;   // hierarchical — continue below

                case "ftp":
                    {
                        string ftpPath = rest;
                        string ftpFragment = "";
                        int ftpHashIdx = ftpPath.IndexOf('#');
                        if (ftpHashIdx >= 0)
                        {
                            ftpFragment = ftpPath[(ftpHashIdx + 1)..];
                            ftpPath = ftpPath[..ftpHashIdx];
                        }
                        return new ParsedUrl(scheme, "", 0, ftpPath, "", ftpFragment);
                    }

                case "file":
                    {
                        // Local file URLs need their query and fragment kept as
                        // separate URL components.  If ?params stays in Path,
                        // the filesystem loader looks for a literal filename
                        // such as "multi.html?fruits=apple".
                        string filePath = rest;
                        string fileQuery = "";
                        string fileFragment = "";
                        int fileHashIdx = filePath.IndexOf('#');
                        if (fileHashIdx >= 0)
                        {
                            fileFragment = filePath[(fileHashIdx + 1)..];
                            filePath = filePath[..fileHashIdx];
                        }
                        int fileQueryIdx = filePath.IndexOf('?');
                        if (fileQueryIdx >= 0)
                        {
                            fileQuery = filePath[(fileQueryIdx + 1)..];
                            filePath = filePath[..fileQueryIdx];
                        }
                        return new ParsedUrl(scheme, "", 0, filePath, fileQuery, fileFragment);
                    }

                default:
                    // Unknown scheme → opaque (safer than inventing http://)
                    return new ParsedUrl(scheme, "", 0, rest, "", "");
            }
        }
        else if (!url.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            // Scheme-less bare host ("www.example.com/page.html" typed in
            // the address bar — the era's default behaviour)
            if (!url.StartsWith("//") && url.IndexOf('/') is int slash && slash >= 0)
            {
                string maybeHost = url[..slash];
                bool looksLikeHost = maybeHost.IndexOf('.') > 0 ||
                                     maybeHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
                if (looksLikeHost && !maybeHost.Contains(' '))
                    url = "http://" + url;
            }
            else if (!url.StartsWith("//"))
            {
                url = "http://" + url;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri == null)
            throw new ArgumentException($"Invalid URL format: {raw}");

        string urlScheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host;
        int port = uri.Port;

        if (port == -1)
            port = urlScheme == "https" ? 443 : 80;

        string path = NormalizePath(uri.AbsolutePath);
        if (host.Equals("web.archive.org", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("/web/", StringComparison.OrdinalIgnoreCase))
            path = path.Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);
        string query = uri.Query.TrimStart('?');
        string fragment = uri.Fragment.TrimStart('#');

        return new ParsedUrl(urlScheme, host, port, path, query, fragment);
    }

    /// <summary>True for schemes whose URL is hierarchical (http/https).</summary>
    public bool IsHttp =>
        Scheme is "http" or "https";

    /// <summary>True for opaque URLs that carry no fetchable resource.</summary>
    public bool IsOpaque =>
        Scheme is not ("http" or "https");

    public ParsedUrl Resolve(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return this;

        string trimmed = href.Trim();

        // Opaque or fully-qualified target → absolute parse
        if (trimmed.StartsWith("//"))
        {
            // Protocol-relative: inherit the base scheme
            return Parse(Scheme + ":" + trimmed);
        }
        // A scheme must be at the START of the href to count as absolute —
        // not merely present somewhere (hrefs like
        // "/get?url=http://example.com/x" embed a full URL in the QUERY
        // and are still root-relative links).
        if (_schemeRegex.IsMatch(trimmed))
            return Parse(trimmed);

        // Same-document fragment
        if (trimmed.StartsWith('#'))
            return this with { Fragment = trimmed[1..] };

        // Split off fragment before path resolution
        string frag = "";
        int hashIdx = trimmed.IndexOf('#');
        if (hashIdx >= 0)
        {
            frag = trimmed[(hashIdx + 1)..];
            trimmed = trimmed[..hashIdx];
        }

        string query = "";
        int qIdx = trimmed.IndexOf('?');
        if (qIdx >= 0)
        {
            query = trimmed[(qIdx + 1)..];
            trimmed = trimmed[..qIdx];
        }

        // Pure query against the base
        if (trimmed.Length == 0)
            return this with { Query = EscapeUrlPart(query), Fragment = frag };

        string basePath = Path;
        string merged;

        if (trimmed.StartsWith('/'))
        {
            merged = trimmed;                       // root-relative
        }
        else
        {
            // Relative: resolve against the base's directory
            int lastSlash = basePath.LastIndexOf('/');
            string baseDir = lastSlash >= 0 ? basePath[..(lastSlash + 1)] : "/";
            merged = baseDir + trimmed;
        }

        string resolvedPath = EscapeUrlPart(NormalizePath(merged));

        return new ParsedUrl(Scheme, Host, Port, resolvedPath, EscapeUrlPart(query), frag);
    }

    public string ToAbsolute()
    {
        if (IsOpaque || Scheme == "about")
        {
            string opaqueFragment = string.IsNullOrEmpty(Fragment) ? string.Empty : $"#{Fragment}";
            return (Path.Length > 0 ? $"{Scheme}:{Path}" : $"{Scheme}:") + opaqueFragment;
        }

        string portPart = string.Empty;
        if (Scheme == "http" && Port != 80) portPart = $":{Port}";
        else if (Scheme == "https" && Port != 443) portPart = $":{Port}";

        string queryPart = string.IsNullOrEmpty(Query) ? string.Empty : $"?{Query}";
        string fragmentPart = string.IsNullOrEmpty(Fragment) ? string.Empty : $"#{Fragment}";

        string hostPart = Host.Contains(':') && !Host.StartsWith("[") ? $"[{Host}]" : Host;
        return $"{Scheme}://{hostPart}{portPart}{Path}{queryPart}{fragmentPart}";
    }

    /// <summary>The document directory of this URL (trailing slash kept).</summary>
    public string Directory =>
        Path.LastIndexOf('/') is int slash && slash >= 0
            ? Path[..(slash + 1)]
            : "/";

    public static string PercentEncode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // RFC 3986 unreserved (and the era's form-encoding extras)
        var sb = new StringBuilder(raw.Length + 16);
        foreach (byte b in Encoding.UTF8.GetBytes(raw))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                    or '-' or '_' or '.' or '~' or '*')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('+');          // form-urlencoded space
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Blocks URL schemes with no business in a 1996 renderer.
    /// javascript: is NOT blocked here — it is a legitimate href the shell
    /// hands to the script engine.
    /// </summary>
    public static void AssertSafe(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new UnsafeUrlException($"Unsafe URL scheme detected: {raw}");
    }

    /// <summary>
    /// Collapses /./ and /../ segments and duplicate slashes while
    /// PRESERVING a trailing slash — "/dir/" and "/dir" are different
    /// documents, and dropping the slash re-bases every relative link.
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        bool trailingSlash = path.EndsWith('/') ||
                             path.EndsWith("/.", StringComparison.Ordinal) ||
                             path.EndsWith("/..", StringComparison.Ordinal);
        string[] segments = path.Split('/');
        var normalized = new List<string>();
        bool absolute = path.StartsWith('/');

        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (normalized.Count > 0)
                    normalized.RemoveAt(normalized.Count - 1);
                else if (!absolute)
                    normalized.Add("..");
                continue;
            }
            normalized.Add(segment);
        }

        string result = (absolute ? "/" : "") + string.Join("/", normalized);
        if (result.Length == 0) result = absolute ? "/" : ".";
        if (trailingSlash && result != "/")
            result += "/";

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // URL escaping for resolved parts
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Percent-escapes characters that cannot appear raw in a URL path or
    /// query — spaces above all.  Valid existing %XX escapes pass through
    /// untouched; a stray '%' (not followed by two hex digits) becomes
    /// %25.  Structural characters (/, ?, &amp;, =, ;, …) stay raw so
    /// paths and query strings keep their meaning.
    /// </summary>
    private static string EscapeUrlPart(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        bool needsEscape = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (IsSafeUrlChar(c)) continue;
            if (c == '%' && i + 2 < s.Length && IsHexDigit(s[i + 1]) && IsHexDigit(s[i + 2]))
                continue;   // valid escape — keep
            needsEscape = true;
            break;
        }
        if (!needsEscape)
            return s;

        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '%' && i + 2 < s.Length && IsHexDigit(s[i + 1]) && IsHexDigit(s[i + 2]))
            {
                sb.Append(s, i, 3);
                i += 2;
                continue;
            }
            if (IsSafeUrlChar(c))
            {
                sb.Append(c);
                continue;
            }
            foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static bool IsSafeUrlChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
        c is '-' or '_' or '.' or '~' or '/' or '?' or '[' or ']' or '@'
           or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}