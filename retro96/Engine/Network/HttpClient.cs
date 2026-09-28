using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retro96.Engine.Forms;

namespace Retro96.Engine.Network;

// ─────────────────────────────────────────────────────────────────────────────
// Result types — the network layer never throws across its boundary.
// ─────────────────────────────────────────────────────────────────────────────

public abstract record HttpResult;

public record HttpSuccess(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string ContentType,
    string Charset,
    byte[] Body,
    string EffectiveUrl,
    IReadOnlyList<string>? SetCookieHeaders = null) : HttpResult;

public record HttpError(string Message, Exception? Inner = null) : HttpResult;

public record CertError(string Message) : HttpResult;

public record TooManyRedirects() : HttpResult;

internal sealed record PluginNetworkRuleDecision(bool Blocked, string? RedirectUrl, IReadOnlySet<string> StripHeaders);

/// <summary>
/// Minimal HTTP/1.0 client over raw sockets — one connection per request,
/// "Connection: close", no keep-alive, exactly like the era.  Supports
/// GET/POST, redirects with a cap, cookies, Basic authentication, gzip
/// (defensive — the request never asks for it), and never lets a bad
/// Content-Length — or a gzip bomb — allocate unbounded memory.
/// </summary>
public class HttpClient
{
    private readonly bool _allowInvalidCertificates;

    public HttpClient(bool allowInvalidCertificates = false)
    {
        _allowInvalidCertificates = allowInvalidCertificates;
    }

    /// <summary>When set by a broker session, this replaces BrowserRuntime.UserAgent for wire headers.</summary>
    public string? UserAgentOverride { get; set; }

    /// <summary>Current page URL used as the Referer header when enabled.</summary>
    public string? ReferrerOverride { get; set; }

    public Func<string, IReadOnlyDictionary<string, string>, PluginNetworkRuleDecision>? PluginRuleEvaluator { get; set; }

    private const int MaxRedirects = 5;
    private const int MaxBodySize = 8 * 1024 * 1024;    // 8 MB is generous for 1996 pages
    private const int ConnectTimeoutMs = 10_000;
    private const int ReadTimeoutMs = 30_000;

    // Basic-auth credentials the shell installs after a 401 challenge
    public string? BasicAuthHeader { get; set; }
    public string? BasicAuthOrigin { get; set; }

    // Virtual so test rigs can sandbox the network (the VisualDiff rig
    // derives a loopback-only client so external-host luck can never
    // influence a diff run).  Behaviour is unchanged for the shell.
    public virtual Task<HttpResult> GetAsync(ParsedUrl url, CookieStore cookies, CancellationToken ct) =>
        GetAsync(url, cookies, ct, ResourceKind.Document);

    public virtual Task<HttpResult> GetAsync(ParsedUrl url, CookieStore cookies, CancellationToken ct, ResourceKind resourceKind) =>
        SendRequestAsync("GET", url, null, cookies, ct, resourceKind: resourceKind);

    public virtual Task<HttpResult> PostAsync(ParsedUrl url, string formData,
                                             CookieStore cookies, CancellationToken ct) =>
        PostAsync(url, formData, cookies, ct, ResourceKind.Document);

    public virtual Task<HttpResult> PostAsync(ParsedUrl url, string formData,
                                             CookieStore cookies, CancellationToken ct, ResourceKind resourceKind) =>
        SendRequestAsync("POST", url, Encoding.UTF8.GetBytes(formData), cookies, ct, resourceKind: resourceKind);

    private static string EscapeQuoted(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// POST with multipart/form-data — file upload encoding.
    /// fields: name → value; files: name → (filename, contentType, bytes).
    /// </summary>
    public async Task<HttpResult> PostMultipartAsync(
        ParsedUrl url,
        IReadOnlyList<MultipartField> fields,
        IReadOnlyList<MultipartFile> files,
        CookieStore cookies, CancellationToken ct)
    {
        string boundary = "----Retro96Boundary" + Guid.NewGuid().ToString("N")[..12];
        var body = new MemoryStream();

        foreach (var (name, value) in fields)
        {
            var part = Encoding.UTF8.GetBytes(
                $"--{boundary}\r\nContent-Disposition: form-data; name=\"{EscapeQuoted(name)}\"\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n{value}\r\n");
            await body.WriteAsync(part, ct);
        }

        foreach (var file in files)
        {
            await body.WriteAsync(Encoding.UTF8.GetBytes(
                $"--{boundary}\r\nContent-Disposition: form-data; name=\"{EscapeQuoted(file.Name)}\"; " +
                $"filename=\"{EscapeQuoted(file.Filename)}\"\r\nContent-Type: {file.ContentType}\r\n\r\n"), ct);
            await body.WriteAsync(file.Bytes, ct);
            await body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
        }

        await body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"), ct);

        return await SendRequestAsync("POST", url, body.ToArray(), cookies, ct,
            contentType: $"multipart/form-data; boundary={boundary}");
    }

    public Task<HttpResult> SendPluginAsync(
        string method, ParsedUrl url, byte[]? body, IReadOnlyDictionary<string, string>? headers,
        string? contentType, CookieStore cookies, CancellationToken ct)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        string normalized = method.Trim().ToUpperInvariant();
        if (normalized is not ("GET" or "POST" or "HEAD" or "PUT" or "DELETE" or "OPTIONS"))
            throw new ArgumentException("Unsupported plugin HTTP method.", nameof(method));
        return SendRequestAsync(normalized, url, body, cookies, ct, extraHeaders: headers, contentType: contentType, resourceKind: ResourceKind.Other);
    }

    private async Task<HttpResult> SendRequestAsync(
        string method, ParsedUrl url, byte[]? body, CookieStore cookies,
        CancellationToken ct, int redirectCount = 0, string? contentType = null, ResourceKind resourceKind = ResourceKind.Document,
        IReadOnlyDictionary<string, string>? extraHeaders = null, int ruleRedirectCount = 0)
    {
        if (redirectCount > MaxRedirects)
            return new TooManyRedirects();

        if (!url.IsHttp)
            return new HttpError($"Unsupported URL scheme: {url.Scheme}");

        ct.ThrowIfCancellationRequested();

        var ruleDecision = PluginRuleEvaluator?.Invoke(url.ToAbsolute(), extraHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (ruleDecision?.Blocked == true)
            return new HttpError("Blocked by an active plugin network rule.");
        if (!string.IsNullOrWhiteSpace(ruleDecision?.RedirectUrl))
        {
            if (ruleRedirectCount >= 5) return new TooManyRedirects();
            ParsedUrl redirected;
            try { redirected = ParsedUrl.Parse(ruleDecision.RedirectUrl); }
            catch (Exception ex) { return new HttpError($"Plugin network rule returned an invalid redirect: {ex.Message}"); }
            if (!redirected.IsHttp) return new HttpError("Plugin network rules may only redirect to http/https URLs.");
            return await SendRequestAsync(method, redirected, body, cookies, ct, redirectCount, contentType, resourceKind, extraHeaders, ruleRedirectCount + 1).ConfigureAwait(false);
        }

        // Bare "name=value; …" values — the "Cookie: " prefix is added when
        // the header block is built.
        string cookieValues = BrowserRuntime.CookiesEnabled ? cookies.Get(url) : string.Empty;
        string requestPath = string.IsNullOrEmpty(url.Query)
            ? url.Path
            : url.Path + "?" + url.Query;

        var requestHeaders = extraHeaders == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(extraHeaders, StringComparer.OrdinalIgnoreCase);
        var (headerBlock, headerBytes) = BuildRequestHeaders(
            method, url, requestPath, cookieValues, body, contentType, requestHeaders, ruleDecision?.StripHeaders);

        HttpResult result;
        if (SandboxContext.BrokerAllNetwork ||
            (resourceKind == ResourceKind.Image && SandboxContext.BrokerImages))
        {
            if (SandboxContext.Broker == null)
                return new HttpError("Network broker is unavailable in the current isolation mode");

            var wireHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) wireHeaders[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            var brokerReply = await SandboxContext.Broker.FetchRawAsync(
                method, url.ToAbsolute(), wireHeaders,
                body, resourceKind.ToString(), MaxBodySize, ct).ConfigureAwait(false);
            result = MapBrokerReply(brokerReply, url);
        }
        else
        {
            result = await SendOverSocketAsync(url, headerBytes, ct);
        }

        if (result is not HttpSuccess success)
            return result;

        // Set-Cookie on ANY response (including redirects). Keep each field
        // separate because Expires=... contains commas and HTTP does not
        // define Set-Cookie as a comma-combinable header.
        if (BrowserRuntime.CookiesEnabled)
            StoreCookies(success, url, cookies);

        if (IsRedirect(success.StatusCode) &&
            success.Headers.TryGetValue("location", out var location))
        {
            if (!BrowserRuntime.RedirectsEnabled)
                return new HttpError("HTTP redirects are disabled in Preferences → Advanced.");

            ParsedUrl newUrl;
            try
            {
                newUrl = url.Resolve(location);
            }
            catch (Exception ex)
            {
                return new HttpError($"Bad redirect Location: {location} ({ex.Message})");
            }

            if (url.Scheme == "https" && newUrl.Scheme == "http")
                return new HttpError("HTTPS to HTTP downgrade not allowed");

            // Never carry HTTP Basic credentials across an origin change.
            if (BasicAuthOrigin != null && !SameOrigin(url, newUrl))
            {
                BasicAuthHeader = null;
                BasicAuthOrigin = null;
            }

            // Era conversion: 301/302/303 → GET; keep method only for 307/308
            bool useGet = success.StatusCode is 301 or 302 or 303;
            return await SendRequestAsync(
                useGet ? "GET" : method,
                newUrl,
                useGet ? null : body,
                cookies, ct, redirectCount + 1, contentType, resourceKind, extraHeaders, ruleRedirectCount);
        }

        return success;
    }

    private static HttpResult MapBrokerReply(SandboxProtocol.FetchReply reply, ParsedUrl url)
    {
        if (reply.Success)
        {
            byte[] body;
            try { body = string.IsNullOrEmpty(reply.BodyBase64) ? Array.Empty<byte>() : Convert.FromBase64String(reply.BodyBase64); }
            catch { return new HttpError("Sandbox broker returned invalid response bytes"); }

            return new HttpSuccess(
                reply.StatusCode,
                reply.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                reply.ContentType ?? "",
                reply.Charset ?? "",
                body,
                string.IsNullOrEmpty(reply.EffectiveUrl) ? url.ToAbsolute() : reply.EffectiveUrl);
        }

        if (!string.IsNullOrEmpty(reply.CertError))
            return new CertError(reply.CertError);
        return new HttpError(reply.Error ?? "Sandbox broker request failed");
    }

    private static void StoreCookies(HttpSuccess success, ParsedUrl url, CookieStore cookies)
    {
        if (success.SetCookieHeaders is { Count: > 0 })
        {
            foreach (var header in success.SetCookieHeaders) cookies.Set(header, url);
            return;
        }
        if (success.Headers.TryGetValue("set-cookie", out var setCookie))
            cookies.Set(setCookie, url);
    }

    private static string OriginKey(ParsedUrl url) =>
        $"{url.Scheme.ToLowerInvariant()}://{url.Host.ToLowerInvariant()}:{url.Port}";

    private static bool SameOrigin(ParsedUrl a, ParsedUrl b) =>
        string.Equals(OriginKey(a), OriginKey(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(int statusCode) =>
        statusCode is 301 or 302 or 303 or 307 or 308;

    /// <summary>Returns the header block text and its wire form (headers
    /// ASCII + raw body bytes appended, so binary uploads survive).</summary>
    private (string Text, byte[] Wire) BuildRequestHeaders(
        string method, ParsedUrl url, string path,
        string cookieValues, byte[]? body, string? contentType,
        IReadOnlyDictionary<string, string>? extraHeaders = null, IReadOnlySet<string>? stripHeaders = null)
    {
        var sb = new StringBuilder(256);

        sb.Append(method.ToUpperInvariant()).Append(' ')
          .Append(path).Append(" HTTP/1.0\r\n");

        // Only the scheme's DEFAULT port is omitted — the old scheme-blind
        // check dropped ":80" from "https://host:80" too.
        int defaultPort = url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        string hostHeader = url.Port == defaultPort || !url.IsHttp
            ? url.Host
            : $"{url.Host}:{url.Port}";
        sb.Append("Host: ").Append(hostHeader).Append("\r\n");

        // Match navigator.userAgent so sniffing scripts agree with the wire
        sb.Append("User-Agent: ").Append(UserAgentOverride ?? BrowserRuntime.UserAgent).Append("\r\n");
        sb.Append("Accept: text/html, image/gif, image/x-xbitmap, image/jpeg, image/pjpeg, */*\r\n");
        sb.Append("Accept-Charset: iso-8859-1,*,utf-8\r\n");

        if (!string.IsNullOrEmpty(cookieValues))
            sb.Append("Cookie: ").Append(cookieValues).Append("\r\n");

        if (BasicAuthHeader != null &&
            BasicAuthOrigin != null &&
            string.Equals(BasicAuthOrigin, OriginKey(url), StringComparison.OrdinalIgnoreCase))
            sb.Append("Authorization: ").Append(BasicAuthHeader).Append("\r\n");

        if (BrowserRuntime.ReferrerEnabled && !string.IsNullOrWhiteSpace(ReferrerOverride))
            sb.Append("Referer: ").Append(ReferrerOverride).Append("\r\n");

        if (body != null && body.Length > 0)
        {
            sb.Append("Content-Type: ")
              .Append(contentType ?? "application/x-www-form-urlencoded")
              .Append("\r\n");
            sb.Append("Content-Length: ")
              .Append(body.Length)
              .Append("\r\n");
        }

        if (extraHeaders != null)
        {
            foreach (var pair in extraHeaders)
            {
                string key = pair.Key?.Trim() ?? string.Empty;
                string value = pair.Value ?? string.Empty;
                if (key.Length == 0 || key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(key).Append(": ").Append(value.Replace("\r", "").Replace("\n", "")).Append("\r\n");
            }
        }

        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        string headerText = sb.ToString();
        if (stripHeaders != null && stripHeaders.Count != 0)
        {
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            headerText = string.Join("\r\n", lines.Where(line =>
            {
                int colon = line.IndexOf(':');
                return colon <= 0 || !stripHeaders.Contains(line[..colon].Trim());
            }));
        }

        // Headers are ASCII; body bytes go out verbatim (binary-safe)
        byte[] wire;
        byte[] headerOnly = Encoding.ASCII.GetBytes(headerText);
        if (body is { Length: > 0 })
        {
            wire = new byte[headerOnly.Length + body.Length];
            headerOnly.CopyTo(wire, 0);
            body.CopyTo(wire, headerOnly.Length);
        }
        else
        {
            wire = headerOnly;
        }

        return (headerText, wire);
    }

    internal Task<HttpResult> SendRawAsync(
        ParsedUrl url, byte[] wire, CancellationToken ct,
        System.Net.IPAddress? connectAddress = null, int maxResponseBytes = MaxBodySize) =>
        SendOverSocketAsync(url, wire, ct, connectAddress,
            Math.Clamp(maxResponseBytes, 1, MaxBodySize));

    private async Task<HttpResult> SendOverSocketAsync(
        ParsedUrl url, byte[] wire, CancellationToken ct,
        System.Net.IPAddress? connectAddress = null, int maxResponseBytes = MaxBodySize)
    {
        TcpClient? tcpClient = null;
        try
        {
            tcpClient = new TcpClient();

            var connectTask = connectAddress == null
                ? tcpClient.ConnectAsync(url.Host, url.Port, ct).AsTask()
                : tcpClient.ConnectAsync(connectAddress, url.Port, ct).AsTask();
            var timeoutTask = Task.Delay(ConnectTimeoutMs, ct);
            var completed = await Task.WhenAny(connectTask, timeoutTask);

            if (completed != connectTask)
            {
                // Observe the abandoned connect so a late failure doesn't
                // surface as an unobserved-task exception.
                _ = connectTask.ContinueWith(
                    t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously);
                return new HttpError("Connection timed out");
            }

            Stream stream = tcpClient.GetStream();
            stream.ReadTimeout = ReadTimeoutMs;
            stream.WriteTimeout = ReadTimeoutMs;

            if (url.Scheme == "https")
            {
                try
                {
                    var sslStream = new SslStream(stream, false,
                        (sender, cert, chain, errors) => _allowInvalidCertificates || errors == SslPolicyErrors.None);
                    await sslStream.AuthenticateAsClientAsync(url.Host);
                    stream = sslStream;
                }
                catch (AuthenticationException ex)
                {
                    return new CertError($"Certificate error: {ex.Message}");
                }
            }

            await stream.WriteAsync(wire, ct);
            await stream.FlushAsync(ct);

            // Overall read deadline — a server that never closes the
            // connection must not hang the browser
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeoutMs);

            return await ReadResponseAsync(stream, url, readCts.Token, maxResponseBytes);
        }
        catch (OperationCanceledException)
        {
            return new HttpError("Request cancelled or timed out");
        }
        catch (SocketException ex)
        {
            return new HttpError($"Connection failed: {ex.SocketErrorCode} ({ex.Message})", ex);
        }
        catch (IOException ex)
        {
            return new HttpError($"Network I/O error: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return new HttpError($"Network error: {ex.Message}", ex);
        }
        finally
        {
            tcpClient?.Close();
        }
    }

    /// <summary>
    /// Reads one CRLF-terminated line byte-by-byte so the stream position
    /// lands exactly on the body start (a buffered reader here would
    /// swallow body bytes — the classic truncation bug).
    /// </summary>
    private static async Task<string?> ReadRawLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(80);
        int prev = -1;
        while (true)
        {
            int b = await stream.ReadByteAsync(ct);
            if (b < 0)
                return sb.Length > 0 ? sb.ToString() : null;

            if (prev == '\r' && b == '\n')
            {
                sb.Length--;   // drop the \r
                return sb.ToString();
            }

            sb.Append((char)b);
            prev = b;
        }
    }

    private async Task<HttpResult> ReadResponseAsync(Stream stream, ParsedUrl url,
                                                     CancellationToken ct, int maxBodySize)
    {
        string? statusLine = await ReadRawLineAsync(stream, ct);
        if (string.IsNullOrEmpty(statusLine))
            return new HttpError("Empty response from server");

        // "HTTP/1.0 200 OK"
        int statusCode = 0;
        string reason = "";
        var statusParts = statusLine.Split(' ', 3);
        if (statusParts.Length >= 2)
        {
            int.TryParse(statusParts[1], out statusCode);
            if (statusParts.Length == 3) reason = statusParts[2];
        }
        if (statusCode <= 0)
            return new HttpError($"Malformed status line: {statusLine}");

        // Headers
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var setCookies = new List<string>();
        string? headerLine;
        while (!string.IsNullOrEmpty(headerLine = await ReadRawLineAsync(stream, ct)))
        {
            int colonIdx = headerLine.IndexOf(':');
            if (colonIdx > 0)
            {
                string key = headerLine[..colonIdx].Trim();
                string value = headerLine[(colonIdx + 1)..].Trim();
                if (key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                {
                    setCookies.Add(value);
                    continue;
                }
                headers[key] = headers.TryGetValue(key, out var existing)
                    ? existing + ", " + value
                    : value;
            }
        }

        string contentType = "text/html";
        string charset = "iso-8859-1";
        if (headers.TryGetValue("content-type", out var ctHeader))
        {
            var parts = ctHeader.Split(';');
            contentType = parts[0].Trim().ToLowerInvariant();
            for (int i = 1; i < parts.Length; i++)
            {
                var param = parts[i].Trim();
                if (param.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                    charset = param[8..].Trim('"', '\'').ToLowerInvariant();
            }
        }

        // Body
        byte[] body;
        if (headers.TryGetValue("content-length", out var contentLengthStr) &&
            int.TryParse(contentLengthStr.Trim(), out int contentLength))
        {
            if (contentLength < 0)
                return new HttpError("Invalid Content-Length");
            if (contentLength > maxBodySize)
                return new HttpError($"Response body too large: {contentLength} bytes");

            try
            {
                body = await ReadExactAsync(stream, contentLength, ct);
            }
            catch (EndOfStreamException)
            {
                return new HttpError("Truncated HTTP response body");
            }
        }
        else if (headers.TryGetValue("transfer-encoding", out var transferEncoding) &&
                 transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(stream, ct, maxBodySize);
        }
        else
        {
            body = await ReadUntilCloseAsync(stream, ct, maxBodySize);
        }

        // Content-Encoding: gzip (defensive — the request never asks)
        if (headers.TryGetValue("content-encoding", out var encoding) &&
            encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase) &&
            body.Length > 0)
        {
            try
            {
                using var compressed = new MemoryStream(body);
                using var gzip = new System.IO.Compression.GZipStream(
                    compressed, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();

                // Cap the DECOMPRESSED size too — MaxBodySize used to apply
                // only before decompression, so a small gzip body could
                // expand without bound.
                byte[] buf = new byte[8192];
                while (true)
                {
                    int n = await gzip.ReadAsync(buf, ct);
                    if (n <= 0) break;
                    if (output.Length + n > maxBodySize)
                        return new HttpError("Decompressed response body too large");
                    await output.WriteAsync(buf.AsMemory(0, n), ct);
                }
                body = output.ToArray();
            }
            catch
            {
                return new HttpError("Failed to decompress gzip response body");
            }
        }

        return new HttpSuccess(statusCode, headers, contentType, charset, body,
            url.ToAbsolute(), setCookies);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        byte[] buffer = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
            if (bytesRead == 0)
                throw new EndOfStreamException("HTTP response ended before Content-Length bytes were received.");
            totalRead += bytesRead;
        }
        return buffer;
    }

    /// <summary>
    /// Chunked transfer decoding using ONLY raw single-byte line reads —
    /// a buffered StreamReader here swallows chunk data into its internal
    /// buffer and corrupts the body.
    /// </summary>
    private static async Task<byte[]> ReadChunkedAsync(
        Stream stream, CancellationToken ct, int maxBodySize)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            string? sizeLine = await ReadRawLineAsync(stream, ct);
            if (sizeLine == null) throw new EndOfStreamException("Truncated chunked response.");
            int semi = sizeLine.IndexOf(';');
            string sizeStr = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(sizeStr, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int chunkSize) || chunkSize < 0)
                throw new InvalidDataException("Malformed chunk size.");

            if (chunkSize == 0)
            {
                while (true)
                {
                    string? trailer = await ReadRawLineAsync(stream, ct);
                    if (trailer == null) throw new EndOfStreamException("Truncated chunk trailer block.");
                    if (trailer.Length == 0) return ms.ToArray();
                }
            }

            if (ms.Length + chunkSize > maxBodySize)
                throw new InvalidDataException("Chunked response body too large.");

            byte[] chunk = await ReadExactAsync(stream, chunkSize, ct);
            await ms.WriteAsync(chunk, ct);
            string? terminator = await ReadRawLineAsync(stream, ct);
            if (terminator == null || terminator.Length != 0)
                throw new EndOfStreamException("Malformed chunk terminator.");
        }
    }

    private async Task<byte[]> ReadUntilCloseAsync(
        Stream stream, CancellationToken ct, int maxBodySize)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int readSize = (int)Math.Min(buffer.Length, maxBodySize - ms.Length + 1);
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readSize), ct);
            if (bytesRead == 0) break;
            if (ms.Length + bytesRead > maxBodySize)
                throw new InvalidDataException("Response body too large before connection close.");
            await ms.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }
        return ms.ToArray();
    }
}

/// <summary>Single-byte async read extension used by the header reader.</summary>
internal static class StreamByteExtensions
{
    public static async Task<int> ReadByteAsync(this Stream stream, CancellationToken ct)
    {
        // A LOCAL buffer — the old static shared byte[1] raced between the
        // loader's concurrent fetches: one request's byte could land in
        // the buffer and another request's read could overwrite it before
        // the first checked the value, corrupting BOTH responses.
        byte[] one = new byte[1];
        int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
        return n == 0 ? -1 : one[0];
    }
}