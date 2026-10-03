using System;
using System.Collections.Generic;
using System.Linq;

namespace Retro96.Engine.Network;

public record Cookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTime? Expires,
    bool Secure)
{
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}

public class CookieStore
{
    private readonly Dictionary<string, List<Cookie>> _cookies = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCookiesPerDomain = 20;
    private const int MaxTotalCookies = 300;
    private const int MaxCookieValueLength = 4096; // 4 KB

    public void Set(string setCookieHeader, ParsedUrl requestUrl)
    {
        if (string.IsNullOrWhiteSpace(setCookieHeader))
            return;

        // Parse the Set-Cookie header
        // Format: NAME=VALUE; expires=DATE; path=PATH; domain=DOMAIN; secure
        string[] parts = setCookieHeader.Split(';');
        if (parts.Length == 0) return;

        // Parse name=value
        string nameValue = parts[0].Trim();
        int eqIdx = nameValue.IndexOf('=');
        if (eqIdx <= 0) return; // Invalid format

        string name = nameValue.Substring(0, eqIdx).Trim();
        string value = nameValue.Substring(eqIdx + 1).Trim();

        // Validate cookie value length
        if (value.Length > MaxCookieValueLength)
            return;

        // Default values
        string domain = requestUrl.Host.ToLowerInvariant();
        string path = requestUrl.Path;
        // Ensure path ends at last slash
        int lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0)
            path = path.Substring(0, lastSlash + 1);
        else
            path = "/";

        DateTime? expires = null;
        bool secure = false;

        // Parse attributes
        for (int i = 1; i < parts.Length; i++)
        {
            string attr = parts[i].Trim();
            int attrEq = attr.IndexOf('=');
            string attrName = attrEq > 0 ? attr.Substring(0, attrEq).Trim().ToLowerInvariant() : attr.Trim().ToLowerInvariant();
            string attrValue = attrEq > 0 ? attr.Substring(attrEq + 1).Trim() : string.Empty;

            switch (attrName)
            {
                case "expires":
                    if (DateTime.TryParse(attrValue, out DateTime exp))
                        expires = exp.ToUniversalTime();
                    break;
                case "domain":
                    if (!string.IsNullOrEmpty(attrValue))
                    {
                        string d = attrValue.ToLowerInvariant().TrimStart('.');
                        // Validate domain is a suffix of request host
                        if (requestUrl.Host.EndsWith(d, StringComparison.OrdinalIgnoreCase))
                            domain = d;
                    }
                    break;
                case "path":
                    if (!string.IsNullOrEmpty(attrValue) && attrValue.StartsWith("/"))
                        path = attrValue;
                    break;
                case "secure":
                    secure = true;
                    break;
            }
        }

        // Remove expired cookies for this domain
        RemoveExpiredCookies(domain);

        // Check total cookie limit
        int totalCount = _cookies.Values.Sum(list => list.Count);
        if (totalCount >= MaxTotalCookies)
            return; // Reject new cookie

        // Get or create domain cookie list
        if (!_cookies.TryGetValue(domain, out var domainCookies))
        {
            domainCookies = new List<Cookie>();
            _cookies[domain] = domainCookies;
        }

        // Check per-domain limit
        if (domainCookies.Count >= MaxCookiesPerDomain)
        {
            // Remove oldest cookie to make room
            if (domainCookies.Count > 0)
                domainCookies.RemoveAt(0);
        }

        // Remove existing cookie with same name, domain, and path
        domainCookies.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                     c.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                                     c.Path.Equals(path, StringComparison.Ordinal));

        // Add new cookie
        domainCookies.Add(new Cookie(name, value, domain, path, expires, secure));
    }

    public string Get(ParsedUrl requestUrl)
    {
        var matchingCookies = new List<Cookie>();
        string requestHost = requestUrl.Host.ToLowerInvariant();
        string requestPath = requestUrl.Path;

        foreach (var kvp in _cookies)
        {
            string domain = kvp.Key.ToLowerInvariant();

            // Domain match: cookie domain must be a suffix of request host
            if (!DomainMatches(requestHost, domain))
                continue;

            foreach (var cookie in kvp.Value)
            {
                // Path match: cookie path must be a prefix of request path
                if (!PathMatches(requestPath, cookie.Path))
                    continue;

                // Secure check: if cookie is secure, only send over HTTPS
                if (cookie.Secure && !requestUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check expiration
                if (cookie.Expires.HasValue && cookie.Expires.Value <= DateTime.UtcNow)
                    continue;

                matchingCookies.Add(cookie);
            }
        }

        // Sort by path length (longest path first per Netscape spec)
        matchingCookies.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));

        if (matchingCookies.Count == 0)
            return string.Empty;

        return "Cookie: " + string.Join("; ", matchingCookies.Select(c => $"{c.Name}={c.Value}"));
    }

    private static bool DomainMatches(string requestHost, string cookieDomain)
    {
        if (requestHost.Equals(cookieDomain, StringComparison.OrdinalIgnoreCase))
            return true;

        // Cookie domain must be a suffix of request host
        // And the host must contain a dot in the domain part (security check)
        if (requestHost.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool PathMatches(string requestPath, string cookiePath)
    {
        if (requestPath.Equals(cookiePath, StringComparison.Ordinal))
            return true;

        if (requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            // Path must be a directory prefix (end with / or be followed by /)
            if (cookiePath.EndsWith("/") || requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/')
                return true;
        }

        return false;
    }

    private void RemoveExpiredCookies(string domain)
    {
        if (_cookies.TryGetValue(domain, out var domainCookies))
        {
            DateTime now = DateTime.UtcNow;
            domainCookies.RemoveAll(c => c.Expires.HasValue && c.Expires.Value <= now);
        }
    }

    public void ClearExpired()
    {
        var domainsToRemove = new List<string>();
        foreach (var kvp in _cookies)
        {
            RemoveExpiredCookies(kvp.Key);
            if (kvp.Value.Count == 0)
                domainsToRemove.Add(kvp.Key);
        }

        foreach (var domain in domainsToRemove)
            _cookies.Remove(domain);
    }

    public int Count
    {
        get
        {
            ClearExpired();
            return _cookies.Values.Sum(list => list.Count);
        }
    }
}
