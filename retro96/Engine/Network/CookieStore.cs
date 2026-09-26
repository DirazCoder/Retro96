using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Retro96.Engine.Network;

public record Cookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTime? Expires,
    bool Secure,
    bool HostOnly = true)      // no Domain attribute → this host only (spec)
{
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}

/// <summary>
/// Netscape cookie jar.  Honours the era's limits (300 total, 20 per
/// domain, 4 KB values), the classic Set-Cookie attributes
/// (expires/domain/path/secure), host-only cookies when no Domain is
/// given, domain-suffix matching with a proper dot boundary, and
/// longest-path-first Cookie ordering per the original Netscape spec.
/// </summary>
public class CookieStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Cookie>> _cookies =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MaxCookiesPerDomain = 20;
    private const int MaxTotalCookies = 300;
    private const int MaxCookieValueLength = 4096;

    /// <summary>
    /// Parse and store a Set-Cookie header value.
    /// Format: NAME=VALUE; expires=DATE; path=PATH; domain=DOMAIN; secure
    /// Multiple cookies may arrive comma-joined (joined duplicate headers
    /// from HttpClient, or sloppy servers) — split on commas that are NOT
    /// inside an expires date.
    /// </summary>
    public void Set(string setCookieHeader, ParsedUrl requestUrl)
    {
        if (string.IsNullOrWhiteSpace(setCookieHeader) || requestUrl == null) return;
        lock (_sync)
            foreach (var single in SplitMultipleSetCookies(setCookieHeader)) SetSingle(single, requestUrl);
    }

    private static List<string> SplitMultipleSetCookies(string header)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        int i = 0;
        while (i < header.Length)
        {
            char c = header[i];
            if (c == ',')
            {
                // A comma inside "expires=Wed, 09-Nov-99 23:12:40 GMT"
                // belongs to the date: after it comes optional space, day
                // digits, then a date separator ('-' or whitespace).  The
                // old check — "digit follows the comma" — also swallowed
                // commas before cookies whose NAMES start with a digit
                // ("a=1, 2=x" never split).
                if (IsDateComma(header, i + 1))
                {
                    current.Append(c);
                    i++;
                    continue;
                }

                // Otherwise the comma starts a new cookie — but only when
                // the segment after it looks like "name=…" (an '=' before
                // any ';' / ',' / ':').
                int j = i + 1;
                while (j < header.Length && header[j] == ' ') j++;
                int k = j;
                while (k < header.Length && header[k] is not (';' or ',' or ':' or '='))
                    k++;
                if (k < header.Length && header[k] == '=')
                {
                    result.Add(current.ToString());
                    current.Clear();
                    i++;
                    continue;
                }
            }
            current.Append(c);
            i++;
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    /// <summary>True when, at <paramref name="after"/>, a date like
    /// " 09-Nov-99" or " 01 Jan 1970" follows the comma.</summary>
    private static bool IsDateComma(string s, int after)
    {
        int j = after;
        while (j < s.Length && s[j] == ' ') j++;
        int digits = 0;
        while (j < s.Length && char.IsDigit(s[j])) { j++; digits++; }
        if (digits == 0 || j >= s.Length)
            return false;
        return s[j] is '-' or ' ' or '\t';
    }

    private void SetSingle(string setCookieHeader, ParsedUrl requestUrl)
    {
        string[] parts = setCookieHeader.Split(';');
        if (parts.Length == 0) return;

        // name=value
        string nameValue = parts[0].Trim();
        int eqIdx = nameValue.IndexOf('=');
        if (eqIdx <= 0) return;

        string name = nameValue[..eqIdx].Trim();
        string value = nameValue[(eqIdx + 1)..].Trim();

        if (name.Length == 0 || value.Length > MaxCookieValueLength)
            return;

        // Defaults derived from the request URL
        string domain = requestUrl.Host.ToLowerInvariant();
        string path = requestUrl.Directory;
        bool hostOnly = true;   // no Domain attribute → this host only

        DateTime? expires = null;
        bool secure = false;

        for (int i = 1; i < parts.Length; i++)
        {
            string attr = parts[i].Trim();
            int attrEq = attr.IndexOf('=');
            string attrName = (attrEq > 0 ? attr[..attrEq] : attr).Trim().ToLowerInvariant();
            string attrValue = attrEq > 0 ? attr[(attrEq + 1)..].Trim() : string.Empty;

            switch (attrName)
            {
                case "expires":
                    expires = ParseEraDate(attrValue);
                    break;

                case "domain":
                    if (!string.IsNullOrEmpty(attrValue))
                    {
                        string d = attrValue.ToLowerInvariant().Trim('.');
                        // The cookie domain must be a dot-suffixed match of
                        // the request host (no "notreal.com" for "real.com")
                        if (d.Length > 0 && DomainMatches(requestUrl.Host.ToLowerInvariant(), d))
                        {
                            domain = d;
                            hostOnly = false;
                        }
                    }
                    break;

                case "path":
                    if (!string.IsNullOrEmpty(attrValue) && attrValue.StartsWith('/'))
                        path = attrValue;
                    break;

                case "secure":
                    secure = true;
                    break;
            }
        }

        RemoveExpiredCookies(domain);

        if (_cookies.Values.Sum(l => l.Count) >= MaxTotalCookies)
            return;

        if (!_cookies.TryGetValue(domain, out var domainCookies))
        {
            domainCookies = new List<Cookie>();
            _cookies[domain] = domainCookies;
        }

        if (domainCookies.Count >= MaxCookiesPerDomain)
            domainCookies.RemoveAt(0);   // oldest evicted

        domainCookies.RemoveAll(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            c.Path == path);

        domainCookies.Add(new Cookie(name, value, domain, path, expires, secure, hostOnly));
    }

    /// <summary>
    /// Netscape expiry formats: 2-digit-year "Wed, 09-Nov-99 23:12:40 GMT"
    /// and the 4-digit variants.  Parsed as GMT directly
    /// (AssumeUniversal | AdjustToUniversal) — the old Unspecified-parse +
    /// ToUniversalTime() treated the already-GMT time as LOCAL and shifted
    /// every cookie by the machine's UTC offset.
    /// </summary>
    private static DateTime? ParseEraDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim().Trim('"', '\'');

        const DateTimeStyles styles =
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal |
            DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite;

        string[] formats =
        {
            "ddd, dd-MMM-yy HH:mm:ss 'GMT'",
            "ddd, dd-MMM-yyyy HH:mm:ss 'GMT'",
            "ddd, dd MMM yy HH:mm:ss 'GMT'",
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
            "ddd MMM d HH:mm:ss yyyy",
            "R"
        };

        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(value, fmt, CultureInfo.InvariantCulture,
                    styles, out var parsed))
                return parsed;
        }

        // Fallback for anything the strict formats miss
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out var loose))
            return loose;

        return null;
    }

    /// <summary>
    /// The Cookie request-header VALUE for a URL — "name=value; name2=value2"
    /// — or empty when no cookies apply.  (No "Cookie: " prefix: HttpClient
    /// adds it on the wire, and JS document.cookie reads this verbatim —
    /// the old prefixed return leaked "Cookie: " into document.cookie.)
    /// </summary>
    public string Get(ParsedUrl requestUrl)
    {
        lock (_sync)
        {
        if (requestUrl == null)
            return string.Empty;

        var matching = new List<Cookie>();
        string requestHost = requestUrl.Host.ToLowerInvariant();
        string requestPath = requestUrl.Path;
        bool isHttps = requestUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        foreach (var kvp in _cookies)
        {
            if (!DomainMatches(requestHost, kvp.Key))
                continue;

            foreach (var cookie in kvp.Value)
            {
                // Host-only cookies (no Domain attribute) go back to the
                // originating host exactly — never to its subdomains.
                if (cookie.HostOnly &&
                    !requestHost.Equals(cookie.Domain, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!PathMatches(requestPath, cookie.Path))
                    continue;
                if (cookie.Secure && !isHttps)
                    continue;
                if (cookie.Expires.HasValue && cookie.Expires.Value <= DateTime.UtcNow)
                    continue;

                matching.Add(cookie);
            }
        }

        if (matching.Count == 0)
            return string.Empty;

        // Longest path first (Netscape spec)
        matching.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));

        return string.Join("; ", matching.Select(c => $"{c.Name}={c.Value}"));
        }
    }

    /// <summary>
    /// Cookie domains match when identical or when the host is a subdomain
    /// — the boundary is always a dot.
    /// </summary>
    private static bool DomainMatches(string requestHost, string cookieDomain)
    {
        if (requestHost.Equals(cookieDomain, StringComparison.OrdinalIgnoreCase))
            return true;
        return requestHost.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathMatches(string requestPath, string cookiePath)
    {
        if (requestPath.Equals(cookiePath, StringComparison.Ordinal))
            return true;

        if (requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            if (cookiePath.EndsWith('/') ||
                (requestPath.Length > cookiePath.Length &&
                 requestPath[cookiePath.Length] == '/'))
                return true;
        }

        return false;
    }

    private void RemoveExpiredCookies(string domain)
    {
        if (!_cookies.TryGetValue(domain, out var domainCookies)) return;
        DateTime now = DateTime.UtcNow;
        domainCookies.RemoveAll(c => c.Expires.HasValue && c.Expires.Value <= now);
    }

    public void ClearExpired()
    {
        lock (_sync)
        {
            ClearExpiredUnsafe();
        }
    }

    private void ClearExpiredUnsafe()
    {
        var empty = new List<string>();
        foreach (var kvp in _cookies)
        {
            RemoveExpiredCookies(kvp.Key);
            if (kvp.Value.Count == 0) empty.Add(kvp.Key);
        }
        foreach (var domain in empty) _cookies.Remove(domain);
    }


    public string? GetCookieValue(string name, ParsedUrl requestUrl)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string header = Get(requestUrl);
        foreach (string part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return part[(eq + 1)..].Trim();
        }
        return null;
    }

    public void SetCookieValue(string name, string value, ParsedUrl requestUrl, string? path = null, string? domain = null, DateTimeOffset? expires = null, bool secure = false)
    {
        string header = name + "=" + value;
        if (!string.IsNullOrWhiteSpace(path)) header += "; path=" + path;
        if (!string.IsNullOrWhiteSpace(domain)) header += "; domain=" + domain;
        if (expires.HasValue) header += "; expires=" + expires.Value.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        if (secure) header += "; secure";
        Set(header, requestUrl);
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                ClearExpiredUnsafe();
                return _cookies.Values.Sum(l => l.Count);
            }
        }
    }

    /// <summary>Remove everything (the "clear cookies" UI action).</summary>
    public void ClearAll() { lock (_sync) _cookies.Clear(); }
}