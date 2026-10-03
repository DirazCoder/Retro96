using System;
using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Network;

public class UnsafeUrlException : Exception
{
    public UnsafeUrlException(string message) : base(message) { }
}

public record ParsedUrl(string Scheme, string Host, int Port, string Path, string Query, string Fragment)
{
    public static ParsedUrl Parse(string raw)
    {
        AssertSafe(raw);

        string url = raw.Trim();
        
        // Handle special about: URLs (e.g., about:blank)
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedUrl("about", "blank", 80, "/", "", "");
        }
        
        // Handle bare domains without scheme
        if (!url.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            throw new ArgumentException($"Invalid URL format: {raw}");
        }

        string scheme = uri.Scheme;
        string host = uri.Host;
        int port = uri.Port;

        // Set default ports if not specified
        if (port == -1) // Uri uses -1 to indicate default port
        {
            port = scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        }

        string path = NormalizePath(uri.AbsolutePath);
        string query = uri.Query.TrimStart('?');
        string fragment = uri.Fragment.TrimStart('#');

        return new ParsedUrl(scheme, host, port, path, query, fragment);
    }

    public ParsedUrl Resolve(string href)
    {
        string baseUrl = ToAbsolute();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri))
        {
            throw new InvalidOperationException("Base URL is not valid for resolution.");
        }

        // Resolve relative URL using Uri's built-in resolution (follows RFC 3986)
        if (!Uri.TryCreate(baseUri, href, out Uri resolvedUri))
        {
            throw new ArgumentException($"Cannot resolve relative URL: {href}");
        }

        return Parse(resolvedUri.ToString());
    }

    public string ToAbsolute()
    {
        // Only add port if it's not the default for the scheme
        string portPart = string.Empty;
        if (Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && Port != 80)
        {
            portPart = $":{Port}";
        }
        else if (Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && Port != 443)
        {
            portPart = $":{Port}";
        }

        string queryPart = string.IsNullOrEmpty(Query) ? string.Empty : $"?{Query}";
        string fragmentPart = string.IsNullOrEmpty(Fragment) ? string.Empty : $"#{Fragment}";

        return $"{Scheme}://{Host}{portPart}{Path}{queryPart}{fragmentPart}";
    }

    public static string PercentEncode(string raw)
    {
        // RFC 3986 unreserved characters: A-Z a-z 0-9 - _ . ~
        var unreserved = new HashSet<char>();
        for (char c = 'A'; c <= 'Z'; c++) unreserved.Add(c);
        for (char c = 'a'; c <= 'z'; c++) unreserved.Add(c);
        for (char c = '0'; c <= '9'; c++) unreserved.Add(c);
        unreserved.Add('-');
        unreserved.Add('_');
        unreserved.Add('.');
        unreserved.Add('~');

        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        var sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            char c = (char)b;
            if (unreserved.Contains(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.AppendFormat("%{0:X2}", b);
            }
        }
        return sb.ToString();
    }

    public static void AssertSafe(string raw)
    {
        string trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeUrlException($"Unsafe URL scheme detected: {raw}");
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        // Ensure path starts with /
        if (!path.StartsWith("/")) path = "/" + path;

        string[] segments = path.Split(new[] { '/' }, StringSplitOptions.None);
        var normalized = new List<string>();

        foreach (string segment in segments)
        {
            if (segment == "." || segment == string.Empty)
            {
                // Skip current directory or empty segments (except leading /)
                continue;
            }
            else if (segment == "..")
            {
                if (normalized.Count > 0)
                {
                    normalized.RemoveAt(normalized.Count - 1);
                }
            }
            else
            {
                normalized.Add(segment);
            }
        }

        string result = "/" + string.Join("/", normalized);
        return result == "/" ? "/" : result;
    }
}
