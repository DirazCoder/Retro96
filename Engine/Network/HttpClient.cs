using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// without this file this is when your browser would stop becoming a browser

namespace Retro96.Engine.Network;

// Discriminated union result type — never throws across the network layer.
public abstract record HttpResult;

public record HttpSuccess(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string ContentType,
    string Charset,
    byte[] Body) : HttpResult;

public record HttpError(string Message, Exception? Inner = null) : HttpResult;

public record CertError(string Message) : HttpResult;

public record TooManyRedirects() : HttpResult;

public class HttpClient
{
    private const int MaxRedirects = 5;
    private const int MaxBodySize = 50 * 1024 * 1024; // 50 MB
    private const int ConnectTimeoutMs = 10000;
    private const int ReadTimeoutMs = 10000;

    public async Task<HttpResult> GetAsync(ParsedUrl url, CookieStore cookies, CancellationToken ct)
    {
        return await SendRequestAsync("GET", url, null, cookies, ct);
    }

    public async Task<HttpResult> PostAsync(ParsedUrl url, string formData, CookieStore cookies, CancellationToken ct)
    {
        return await SendRequestAsync("POST", url, formData, cookies, ct);
    }

    private async Task<HttpResult> SendRequestAsync(string method, ParsedUrl url, string? body, CookieStore cookies, CancellationToken ct, int redirectCount = 0)
    {
        if (redirectCount > MaxRedirects)
            return new TooManyRedirects();

        ct.ThrowIfCancellationRequested();

        // Get cookies for this request
        string cookieHeader = cookies.Get(url);

        // Build request headers
        string requestPath = url.Path;
        if (!string.IsNullOrEmpty(url.Query))
            requestPath += "?" + url.Query;

        string requestHeaders = BuildRequestHeaders(method, url, requestPath, cookieHeader, body);

        // Connect and send request
        var result = await SendRequestAsync(url, requestHeaders, body, ct);

        if (result is not HttpSuccess success)
            return result;

        // Check for redirect
        if (IsRedirect(success.StatusCode) && success.Headers.TryGetValue("location", out var location))
        {
            // Check for HTTPS to HTTP downgrade
            ParsedUrl newUrl = url.Resolve(location);
            if (url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                newUrl.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpError("HTTPS to HTTP downgrade not allowed");
            }

            // Process Set-Cookie headers
            foreach (var header in success.Headers)
            {
                if (header.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                {
                    cookies.Set(header.Value, url);
                }
            }

            // Follow redirect with same method and body (for 307/308) or GET (for 301/302/303)
            bool useGetForRedirect = success.StatusCode == 301 || success.StatusCode == 302 || success.StatusCode == 303;
            return await SendRequestAsync(useGetForRedirect ? "GET" : method, newUrl, useGetForRedirect ? null : body, cookies, ct, redirectCount + 1);
        }

        // Process Set-Cookie headers for non-redirects too
        foreach (var header in success.Headers)
        {
            if (header.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            {
                cookies.Set(header.Value, url);
            }
        }

        return success;
    }

    private static bool IsRedirect(int statusCode)
    {
        return statusCode == 301 || statusCode == 302 || statusCode == 303 ||
               statusCode == 307 || statusCode == 308;
    }

    private static string BuildRequestHeaders(string method, ParsedUrl url, string path, string cookieHeader, string? body)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{method.ToUpper()} {path} HTTP/1.0");
        sb.AppendLine($"Host: {url.Host}");
        sb.AppendLine("User-Agent: Retro96/1.0 (compatible; Mozilla/3.0)");
        sb.AppendLine("Accept: text/html, image/gif, image/jpeg, image/png, */*;q=0.1");
        sb.AppendLine("Accept-Charset: iso-8859-1, utf-8");
        sb.AppendLine("Accept-Encoding: gzip");
        if (!string.IsNullOrEmpty(cookieHeader))
            sb.AppendLine(cookieHeader);
        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine($"Content-Type: application/x-www-form-urlencoded");
            sb.AppendLine($"Content-Length: {Encoding.ASCII.GetByteCount(body)}");
        }
        sb.AppendLine("Connection: close");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(body))
            sb.Append(body);
        return sb.ToString();
    }

    private async Task<HttpResult> SendRequestAsync(ParsedUrl url, string requestHeaders, string? body, CancellationToken ct)
    {
        TcpClient? tcpClient = null;
        try
        {
            tcpClient = new TcpClient();

            // Connect with timeout
            var connectTask = tcpClient.ConnectAsync(url.Host, url.Port);
            var timeoutTask = Task.Delay(ConnectTimeoutMs, ct);
            var completed = await Task.WhenAny(connectTask, timeoutTask);

            if (completed == timeoutTask)
                return new HttpError("Connection timeout");

            if (!tcpClient.Connected)
                return new HttpError("Failed to connect");

            Stream stream = tcpClient.GetStream();
            stream.ReadTimeout = ReadTimeoutMs;
            stream.WriteTimeout = ReadTimeoutMs;

            // Handle TLS if HTTPS
            if (url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var sslStream = new SslStream(stream, false, (sender, cert, chain, errors) => true);
                    await sslStream.AuthenticateAsClientAsync(url.Host);
                    stream = sslStream;
                }
                catch (AuthenticationException ex)
                {
                    return new CertError($"Certificate error: {ex.Message}");
                }
            }

            // Send request
            byte[] requestBytes = Encoding.ASCII.GetBytes(requestHeaders);
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length, ct);
            await stream.FlushAsync(ct);

            // Read response
            return await ReadResponseAsync(stream, ct);
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
    /// Reads a CRLF-terminated line from a raw stream one byte at a time.
    /// This avoids the StreamReader internal buffer poisoning the body position.
    /// </summary>
    private static async Task<string?> ReadRawLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        int prev = -1;
        while (true)
        {
            int n = await stream.ReadAsync(buf, 0, 1, ct);
            if (n == 0)
                return sb.Length > 0 ? sb.ToString() : null; // EOF
            int cur = buf[0];
            if (prev == '\r' && cur == '\n')
            {
                // Remove the trailing \r we already appended
                if (sb.Length > 0) sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)cur);
            prev = cur;
        }
    }

    private async Task<HttpResult> ReadResponseAsync(Stream stream, CancellationToken ct)
    {
        // *** FIX: read headers byte-by-byte so the stream position is exactly at the
        // body start when we hand off to ReadExactAsync / ReadChunkedAsync / ReadUntilCloseAsync.
        // The old approach used a StreamReader (bufferSize 1024) which consumed up to 1024 bytes
        // of body into its internal buffer, silently discarding them when we then read from
        // the raw stream.  That produced a truncated / empty body and a blank rendered page. ***

        // Read status line
        string? statusLine = await ReadRawLineAsync(stream, ct);
        if (string.IsNullOrEmpty(statusLine))
            return new HttpError("Empty response");

        // Parse status code
        int statusCode = 0;
        var statusParts = statusLine.Split(' ');
        if (statusParts.Length >= 2)
            int.TryParse(statusParts[1], out statusCode);

        // Read headers (blank line terminates)
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? headerLine;
        while (!string.IsNullOrEmpty(headerLine = await ReadRawLineAsync(stream, ct)))
        {
            int colonIdx = headerLine.IndexOf(':');
            if (colonIdx > 0)
            {
                string key = headerLine.Substring(0, colonIdx).Trim();
                string value = headerLine.Substring(colonIdx + 1).Trim();
                headers[key] = value;
            }
        }

        // Parse content type and charset
        string contentType = "text/html";
        string charset = "iso-8859-1";
        if (headers.TryGetValue("content-type", out var ctHeader))
        {
            var parts = ctHeader.Split(';');
            contentType = parts[0].Trim();
            for (int i = 1; i < parts.Length; i++)
            {
                var param = parts[i].Trim();
                if (param.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                {
                    charset = param.Substring(8).Trim('"', '\'');
                }
            }
        }

        // Read body
        byte[] body;
        if (headers.TryGetValue("content-length", out var contentLengthStr) &&
            int.TryParse(contentLengthStr, out int contentLength))
        {
            body = await ReadExactAsync(stream, contentLength, ct);
        }
        else if (headers.TryGetValue("transfer-encoding", out var transferEncoding) &&
                 transferEncoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(stream, ct);
        }
        else
        {
            body = await ReadUntilCloseAsync(stream, ct);
        }

        // Check body size limit
        if (body.Length > MaxBodySize)
            return new HttpError($"Response body too large: {body.Length} bytes");

        // Handle gzip content encoding
        if (headers.TryGetValue("content-encoding", out var contentEncoding) &&
            contentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var compressedStream = new MemoryStream(body);
            using var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            await gzipStream.CopyToAsync(decompressedStream, ct);
            body = decompressedStream.ToArray();
        }

        return new HttpSuccess(statusCode, headers, contentType, charset, body);
    }

    private async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        byte[] buffer = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int bytesRead = await stream.ReadAsync(buffer, totalRead, length - totalRead, ct);
            if (bytesRead == 0)
                break;
            totalRead += bytesRead;
        }
        if (totalRead < length)
            Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private async Task<byte[]> ReadChunkedAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);

        while (true)
        {
            string? chunkSizeLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(chunkSizeLine))
                break;

            // Parse chunk size (hex)
            int chunkSize = 0;
            int semicolonIdx = chunkSizeLine.IndexOf(';');
            string sizeStr = semicolonIdx >= 0 ? chunkSizeLine.Substring(0, semicolonIdx) : chunkSizeLine;
            if (!int.TryParse(sizeStr.Trim(), System.Globalization.NumberStyles.HexNumber, null, out chunkSize))
                break;

            if (chunkSize == 0)
                break;

            // Read chunk data
            byte[] chunk = new byte[chunkSize];
            int totalRead = 0;
            while (totalRead < chunkSize)
            {
                int bytesRead = await stream.ReadAsync(chunk, totalRead, chunkSize - totalRead, ct);
                if (bytesRead == 0)
                    break;
                totalRead += bytesRead;
            }
            ms.Write(chunk, 0, totalRead);

            // Read trailing CRLF
            await reader.ReadLineAsync();
        }

        // Read until we see the final CRLFCRLF
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line))
                break;
        }

        return ms.ToArray();
    }

    private async Task<byte[]> ReadUntilCloseAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (bytesRead == 0)
                break;
            ms.Write(buffer, 0, bytesRead);

            if (ms.Length > MaxBodySize)
                return ms.ToArray(); // Return what we have
        }
        return ms.ToArray();
    }
}