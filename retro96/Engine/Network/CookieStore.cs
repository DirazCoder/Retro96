using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Retro96.Engine.Network;

/// <summary>
/// One stored cookie.
///
/// Identity is (Name, Domain, Path): a cookie differing only in Value,
/// Expires, Secure or bookkeeping timestamps is the *same* cookie — the
/// newer one replaces the older.  CreatedAt / LastAccessedUtc are
/// deliberately excluded from equality so Contains / Remove / IndexOf
/// behave the way a cookie jar expects.
/// </summary>
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

    /// <summary>Raised on every read and write; drives least-recently-used
    /// eviction, which is what the Netscape spec demands for full jars.</summary>
    public DateTime LastAccessedUtc { get; init; } = DateTime.UtcNow;

    public virtual bool Equals(Cookie? other) =>
        other is not null &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Domain, other.Domain, StringComparison.Ordinal) &&
        string.Equals(Path, other.Path, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Domain),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Path));

    public override string ToString()
    {
        var sb = new StringBuilder(Name.Length + Value.Length + 64)
            .Append(Name).Append('=').Append(Value)
            .Append("; domain=").Append(HostOnly ? Domain : "." + Domain)
            .Append("; path=").Append(Path);
        if (Expires is DateTime e)
            sb.Append("; expires=").Append(e.ToString("R", CultureInfo.InvariantCulture));
        if (Secure) sb.Append("; secure");
        return sb.ToString();
    }
}

/// <summary>
/// Netscape cookie jar, 1996 rules:
///
///  - limits: 300 cookies total, 20 per domain, 4 KB per cookie; a full jar
///    evicts the *least recently used* cookie (spec) and never refuses a
///    replacement;
///  - attributes: expires / domain / path / secure (Max-Age, Comment and
///    Version are RFC 2109 — 1997 — and deliberately absent);
///  - no Domain attribute → host-only cookie; with one → dot-boundary
///    suffix matching, and the domain must domain-match the setting host
///    and contain a dot (no "Domain=com" — the hole Navigator later plugged);
///  - Cookie header: longest-path first, ties in jar (creation) order;
///  - an expires date already in the past deletes the matching cookie
///    (the classic logout idiom) instead of storing a zombie.
/// </summary>
public sealed class CookieStore
{
    private const int MaxCookiesPerDomain = 20;
    private const int MaxTotalCookies = 300;
    private const int MaxCookieSize = 4096;   // name + value, per the spec

    private static readonly char[] NameForbiddenChars = { ';', ',', '=' };
    private static readonly char[] ValueForbiddenChars = { ';', ',' };
    private static readonly char[] QueryAndFragmentChars = { '?', '#' };

    private readonly object _sync = new();
    private readonly Dictionary<string, List<Cookie>> _cookies =
        new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Set ----

    /// <summary>
    /// Parse and store a Set-Cookie header value.
    /// Format: NAME=VALUE; expires=DATE; path=PATH; domain=DOMAIN; secure.
    /// Multiple cookies may arrive comma-joined — split on commas that are
    /// NOT inside an expires date.
    /// </summary>
    public void Set(string? setCookieHeader, ParsedUrl? requestUrl)
    {
        if (string.IsNullOrWhiteSpace(setCookieHeader) || requestUrl is null) return;

        lock (_sync)
        {
            foreach (string single in SplitMultipleSetCookies(setCookieHeader))
                SetSingle(single, requestUrl);   // caller holds _sync
        }
    }

    /// <summary>Splits comma-joined Set-Cookie values (joined duplicate
    /// headers from HttpClient, or sloppy servers) on commas that are NOT
    /// inside an expires date and NOT followed by "name=…".</summary>
    private static List<string> SplitMultipleSetCookies(string header)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        int i = 0;
        while (i < header.Length)
        {
            char c = header[i];
            if (c == ',')
            {
                // A comma inside "expires=Wed, 09-Nov-99 23:12:40 GMT"
                // belongs to the date: after it comes optional space, day
                // digits, then a date separator ('-' or whitespace).
                if (IsDateComma(header, i + 1))
                {
                    current.Append(c);
                    i++;
                    continue;
                }

                // Otherwise the comma starts a new cookie — but only when
                // the segment after it looks like "name=…" (an '=' before
                // any ';' / ',' / ':' — the ':' guard keeps "12:30" in
                // values from being mistaken for a name).
                int j = i + 1;
                while (j < header.Length && header[j] == ' ') j++;
                int k = j;
                while (k < header.Length && header[k] is not (';' or ',' or ':' or '='))
                    k++;
                if (k < header.Length && header[k] == '=' && current.Length > 0)
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

    /// <summary>Parse and store one Set-Cookie value.  Caller holds _sync.</summary>
    private void SetSingle(string setCookieHeader, ParsedUrl requestUrl)
    {
        string[] parts = setCookieHeader.Split(';');

        // NAME=VALUE — everything up to the first ';'.
        string nameValue = parts[0].Trim();
        int eqIdx = nameValue.IndexOf('=');
        if (eqIdx <= 0) return;                          // no '=', or empty name

        string name = nameValue[..eqIdx].Trim();
        string value = nameValue[(eqIdx + 1)..].Trim();

        if (name.Length == 0) return;
        if (name.Length + value.Length > MaxCookieSize) return;
        if (ContainsControlCharacter(name) || ContainsControlCharacter(value))
            return;   // CRLF must never be echoed into the outgoing Cookie header

        bool localFile = requestUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
        string host = CookieHostFor(requestUrl);
        if (host.Length == 0) return;

        // Defaults derived from the request URL.
        string domain = host;
        string path = DefaultPath(requestUrl.Directory);
        bool hostOnly = true;                            // no Domain attribute → this host only
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
                    expires = ParseEraDate(attrValue);  // unparseable → session cookie
                    break;

                case "domain":
                    if (!localFile && !string.IsNullOrEmpty(attrValue))
                    {
                        string d = attrValue.Trim().Trim('.').ToLowerInvariant();
                        // Must domain-match the request host (no "notreal.com"
                        // for "real.com") and contain a dot, so nobody sets
                        // "Domain=com" for every *.com host on the planet.
                        if (d.Length > 0 && d.Contains('.') && DomainMatches(host, d))
                        {
                            domain = d;
                            hostOnly = false;
                        }
                        // else: attribute ignored, cookie stays host-only
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

        DateTime now = DateTime.UtcNow;

        // An expiry already in the past is the classic "delete this cookie"
        // idiom: drop any existing match and store nothing.
        if (expires is DateTime dead && dead <= now)
        {
            if (_cookies.TryGetValue(domain, out var doomed))
            {
                doomed.RemoveAll(c => MatchesIdentity(c, name, path));
                if (doomed.Count == 0) _cookies.Remove(domain);
            }
            return;
        }

        PurgeExpiredUnsafe();                            // everywhere, not just this domain

        if (!_cookies.TryGetValue(domain, out var bucket))
        {
            bucket = new List<Cookie>();
            _cookies[domain] = bucket;
        }

        // Replacing an existing cookie never needs capacity and never evicts
        // a neighbour: take the old entry out FIRST.
        bucket.RemoveAll(c => MatchesIdentity(c, name, path));

        // 20 per domain — evict the least recently used.
        while (bucket.Count >= MaxCookiesPerDomain)
            EvictLeastRecentlyUsedUnsafe(bucket);

        // 300 total — evict the least recently used in the whole jar.
        while (TotalCountUnsafe() >= MaxTotalCookies)
            EvictLeastRecentlyUsedUnsafe();

        bucket.Add(new Cookie(name, value, domain, path, expires, secure, hostOnly));
    }

    /// <summary>
    /// Netscape expiry formats: 2-digit-year "Wed, 09-Nov-99 23:12:40 GMT",
    /// 4-digit variants, asctime, RFC 1123.  Parsed as GMT directly
    /// (AssumeUniversal | AdjustToUniversal) — an Unspecified parse +
    /// ToUniversalTime() would treat already-GMT times as local and shift
    /// every cookie by the machine's UTC offset.
    /// </summary>
    private static DateTime? ParseEraDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim().Trim('"', '\'');

        const DateTimeStyles styles =
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal |
            DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite |
            DateTimeStyles.AllowInnerWhite;

        // 'd' (not 'dd') so single-digit days — "Sun, 6 Nov 1994 …" — parse;
        // AllowInnerWhite absorbs asctime's space-padded day ("Nov  6").
        // 2-digit years use .NET's 2029 pivot: 00–29 → 2000s, 30–99 → 1900s.
        string[] formats =
        {
            "ddd, d-MMM-yy HH:mm:ss 'GMT'",      // Netscape spec
            "ddd, d-MMM-yyyy HH:mm:ss 'GMT'",
            "ddd, d MMM yy HH:mm:ss 'GMT'",
            "ddd, d MMM yyyy HH:mm:ss 'GMT'",    // RFC 822 / 1123
            "dddd, d-MMM-yy HH:mm:ss 'GMT'",     // "Friday, 09-Nov-99 …"
            "dddd, d MMM yyyy HH:mm:ss 'GMT'",
            "ddd MMM d HH:mm:ss yyyy",           // asctime
            "R"                                  // round-trip RFC 1123
        };

        foreach (string fmt in formats)
            if (DateTime.TryParseExact(value, fmt, CultureInfo.InvariantCulture, styles, out DateTime parsed))
                return parsed;

        // Fallback for anything the strict formats miss
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out DateTime loose))
            return loose;

        return null;
    }

    // ---------------------------------------------------------------- Get ----

    /// <summary>
    /// The Cookie request-header VALUE for a URL — "name=value; name2=value2" —
    /// or empty when no cookies apply.  No "Cookie: " prefix: HttpClient adds
    /// it on the wire, and JS document.cookie reads this verbatim.
    /// Cookies are listed longest-path-first (Netscape); ties keep jar order.
    /// </summary>
    public string Get(ParsedUrl? requestUrl)
    {
        if (requestUrl is null) return string.Empty;

        lock (_sync)
        {
            string host = CookieHostFor(requestUrl);
            if (host.Length == 0) return string.Empty;

            string requestPath = StripQueryAndFragment(requestUrl.Path);
            if (requestPath.Length == 0 || requestPath[0] != '/')
                requestPath = "/";

            bool isHttps = string.Equals(requestUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase);

            PurgeExpiredUnsafe();
            DateTime now = DateTime.UtcNow;

            var matching = new List<Cookie>();
            foreach (KeyValuePair<string, List<Cookie>> kvp in _cookies)
            {
                if (!DomainMatches(host, kvp.Key))
                    continue;

                foreach (Cookie cookie in kvp.Value)
                {
                    // Host-only cookies (no Domain attribute) go back to the
                    // originating host exactly — never to its subdomains.
                    if (cookie.HostOnly &&
                        !string.Equals(host, cookie.Domain, StringComparison.Ordinal))
                        continue;
                    if (!PathMatches(requestPath, cookie.Path))
                        continue;
                    if (cookie.Secure && !isHttps)
                        continue;
                    if (cookie.Expires is DateTime e && e <= now)
                        continue;

                    matching.Add(cookie);
                }
            }

            if (matching.Count == 0)
                return string.Empty;

            // Reading a cookie makes it most recently used (LRU eviction).
            // Safe here: the Dictionary enumeration above has finished.
            foreach (Cookie cookie in matching)
                if (_cookies.TryGetValue(cookie.Domain, out List<Cookie>? bucket))
                {
                    int idx = bucket.FindIndex(c => ReferenceEquals(c, cookie));
                    if (idx >= 0)
                        bucket[idx] = cookie with { LastAccessedUtc = now };
                }

            // OrderByDescending is stable, unlike List<T>.Sort.
            return string.Join("; ",
                matching
                    .OrderByDescending(c => c.Path.Length)
                    .Select(c => $"{c.Name}={c.Value}"));
        }
    }

    /// <summary>Single named cookie (first/most-specific match), or null.</summary>
    public string? GetCookieValue(string? name, ParsedUrl? requestUrl)
    {
        if (string.IsNullOrWhiteSpace(name) || requestUrl is null) return null;

        string header = Get(requestUrl);
        foreach (string part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = part.Trim();
            int eq = segment.IndexOf('=');
            if (eq > 0 &&
                segment[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return segment[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Typed convenience wrapper: builds a Set-Cookie line and feeds it through
    /// <see cref="Set"/>.  Everything is round-tripped through header syntax,
    /// so name/value/path/domain are validated — a ';' or control character
    /// would otherwise inject attributes (or CRLF into the outgoing header).
    /// </summary>
    public void SetCookieValue(
        string name,
        string value,
        ParsedUrl requestUrl,
        string? path = null,
        string? domain = null,
        DateTimeOffset? expires = null,
        bool secure = false)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (requestUrl is null) throw new ArgumentNullException(nameof(requestUrl));

        if (name.Length == 0 ||
            name.IndexOfAny(NameForbiddenChars) >= 0 ||
            name.Any(char.IsWhiteSpace) ||
            ContainsControlCharacter(name))
            throw new ArgumentException(
                "Cookie name must be non-empty and free of ';', ',', '=', whitespace and control characters.",
                nameof(name));

        if (value.IndexOfAny(ValueForbiddenChars) >= 0 ||
            ContainsControlCharacter(value) ||
            (value.Length > 0 &&
             (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))))
            throw new ArgumentException(
                "Cookie value must not contain ';', ',' or control characters, and must not start or end with whitespace.",
                nameof(value));

        if (path is not null &&
            (path.Length == 0 || path[0] != '/' ||
             path.Contains(';') || ContainsControlCharacter(path)))
            throw new ArgumentException(
                "Cookie path must start with '/' and contain no ';' or control characters.",
                nameof(path));

        if (domain is not null &&
            (domain.Length == 0 ||
             domain.Contains(';') || domain.Contains('/') || ContainsControlCharacter(domain)))
            throw new ArgumentException(
                "Cookie domain must be non-empty and contain no ';', '/' or control characters.",
                nameof(domain));

        var sb = new StringBuilder();
        sb.Append(name).Append('=').Append(value);
        if (path is not null) sb.Append("; path=").Append(path);
        if (domain is not null) sb.Append("; domain=").Append(domain);
        if (expires.HasValue)
            sb.Append("; expires=")
              .Append(expires.Value.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        if (secure) sb.Append("; secure");

        Set(sb.ToString(), requestUrl);
    }

    // ------------------------------------------------------------ lifecycle ---

    /// <summary>Number of live cookies (expired ones are purged first).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                PurgeExpiredUnsafe();
                return TotalCountUnsafe();
            }
        }
    }

    /// <summary>Drop expired cookies and their now-empty buckets.</summary>
    public void ClearExpired()
    {
        lock (_sync) PurgeExpiredUnsafe();
    }

    /// <summary>Remove everything (the "clear cookies" UI action).</summary>
    public void ClearAll()
    {
        lock (_sync) _cookies.Clear();
    }

    // -------------------------------------------------------------- helpers ---

    private static bool MatchesIdentity(Cookie cookie, string name, string path) =>
        string.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(cookie.Path, path, StringComparison.Ordinal);

    /// <summary>
    /// Cookie domains match when identical or when the host is a subdomain —
    /// the boundary is always a dot ("ample.com" never matches "example.com").
    /// </summary>
    private static bool DomainMatches(string requestHost, string cookieDomain)
    {
        if (string.IsNullOrEmpty(cookieDomain)) return false;
        if (string.Equals(requestHost, cookieDomain, StringComparison.OrdinalIgnoreCase))
            return true;
        return requestHost.Length > cookieDomain.Length
            && requestHost[requestHost.Length - cookieDomain.Length - 1] == '.'
            && requestHost.EndsWith(cookieDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"/a/b" matches "/a/b", "/a/b/c"; not "/a/bc", not "/a".</summary>
    private static bool PathMatches(string requestPath, string cookiePath)
    {
        if (requestPath.Equals(cookiePath, StringComparison.Ordinal))
            return true;

        return requestPath.StartsWith(cookiePath, StringComparison.Ordinal) &&
               (cookiePath.EndsWith('/') ||
                (requestPath.Length > cookiePath.Length &&
                 requestPath[cookiePath.Length] == '/'));
    }

    private int TotalCountUnsafe() => _cookies.Values.Sum(list => list.Count);

    /// <summary>Drop expired cookies everywhere and prune empty buckets.
    /// Caller holds _sync.</summary>
    private void PurgeExpiredUnsafe()
    {
        DateTime now = DateTime.UtcNow;
        List<string>? empty = null;

        foreach (KeyValuePair<string, List<Cookie>> kvp in _cookies)
        {
            // Mutating the List is safe mid-Dictionary-enumeration: no keys
            // are added or removed here.
            kvp.Value.RemoveAll(c => c.Expires is DateTime e && e <= now);
            if (kvp.Value.Count == 0)
            {
                empty ??= new List<string>();
                empty.Add(kvp.Key);
            }
        }

        if (empty is not null)
            foreach (string domain in empty)
                _cookies.Remove(domain);
    }

    /// <summary>Evict the least-recently-used cookie of one domain. Caller holds _sync.</summary>
    private static void EvictLeastRecentlyUsedUnsafe(List<Cookie> bucket)
    {
        int victim = -1;
        for (int i = 0; i < bucket.Count; i++)
            if (victim < 0 || bucket[i].LastAccessedUtc < bucket[victim].LastAccessedUtc)
                victim = i;
        if (victim >= 0) bucket.RemoveAt(victim);
    }

    /// <summary>Evict the least-recently-used cookie in the whole jar. Caller holds _sync.</summary>
    private void EvictLeastRecentlyUsedUnsafe()
    {
        Cookie? victim = null;
        List<Cookie>? victimBucket = null;

        foreach (KeyValuePair<string, List<Cookie>> kvp in _cookies)
            foreach (Cookie cookie in kvp.Value)
                if (victim is null || cookie.LastAccessedUtc < victim.LastAccessedUtc)
                {
                    victim = cookie;
                    victimBucket = kvp.Value;
                }

        if (victim is null || victimBucket is null) return;

        victimBucket.Remove(victim);   // identity equality → exactly this cookie
        if (victimBucket.Count == 0) _cookies.Remove(victim.Domain);
    }

    /// <summary>Lower-case, trim, drop a ":port" suffix and a trailing root
    /// dot so "Example.com:80" and "example.com." land on one bucket.</summary>
    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string h = host.Trim().ToLowerInvariant();

        if (h.StartsWith('['))                              // [ipv6] or [ipv6]:port
        {
            int close = h.IndexOf(']');
            if (close >= 0) h = h[..(close + 1)];
        }
        else
        {
            int colon = h.LastIndexOf(':');
            if (colon > 0 &&
                h.IndexOf(':') == colon &&                  // exactly one ':' → not bare IPv6
                IsAllDigits(h[(colon + 1)..]))
                h = h[..colon];                             // hostname:port
        }

        if (h.EndsWith('.')) h = h[..^1];                   // "example.com." → "example.com"
        return h;
    }

    private static string CookieHostFor(ParsedUrl requestUrl)
    {
        string host = NormalizeHost(requestUrl.Host);
        if (host.Length > 0 ||
            !requestUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            return host;

        // File URLs have no network host. Give each canonical file path its
        // own host-only cookie bucket so an HTML guestbook can survive reloads
        // without sharing cookies with unrelated local files.
        string path = Uri.UnescapeDataString(requestUrl.Path).Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            path = path.ToLowerInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return "file-" + hash + ".invalid";
    }

    /// <summary>Default cookie path: the URL's directory, or "/" when
    /// empty/unrooted (an empty path would match *every* path).</summary>
    private static string DefaultPath(string? directory)
    {
        string d = StripQueryAndFragment(directory);
        return d.Length > 0 && d[0] == '/' ? d : "/";
    }

    private static string StripQueryAndFragment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int cut = path.IndexOfAny(QueryAndFragmentChars);
        return cut >= 0 ? path[..cut] : path;
    }

    /// <summary>CTLs (and C1 controls) must never be stored: they would be
    /// echoed straight back into the outgoing Cookie header.</summary>
    private static bool ContainsControlCharacter(string s)
    {
        foreach (char c in s)
            if (char.IsControl(c))
                return true;
        return false;
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!char.IsDigit(c)) return false;
        return true;
    }
}
