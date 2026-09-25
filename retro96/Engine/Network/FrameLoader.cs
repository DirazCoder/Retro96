using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;

namespace Retro96.Engine.Network;

// ─────────────────────────────────────────────────────────────────────────────
// Frame/iframe child-document loading — the ONE scheme-dispatching content
// loader shared by the WinForms shell (Form1.LoadFramesAsync) and the
// headless test rigs.
//
// WHY THIS EXISTS (user report: "iframe don't work"):
//   The frame loader used to live inline in Form1.LoadFramesAsync and only
//   spoke HTTP: it called HttpClient.GetAsync directly, so a file: src
//   (every locally-opened page with an iframe) came back as
//   HttpError("Unsupported URL scheme: file") — which matched NEITHER the
//   200-branch NOR the >=400-branch, so NOTHING happened: no error page,
//   no content, just a permanently blank sunken rect.  Top-level pages,
//   stylesheets and images all had file:// support; frames were the only
//   resource class that did not.
//
//   On top of that, RenderHtmlAsync (the render path used by file://,
//   about: and error pages) never called LoadFramesAsync at all — only the
//   HTTP success path did — so an iframe on a locally-opened page never
//   even attempted to load.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A successfully prepared frame document: parsed, styled, laid out.</summary>
public sealed record FrameContent(DomDocument Document, LayoutBox RootBox, string AbsoluteUrl);

/// <summary>
/// Loads and prepares frame/iframe child documents.  Resolution uses
/// ImageCache.ResolveUrl (correct file:// base handling); content comes
/// from disk for file: URLs and from the socket client for http(s).
/// Failures NEVER throw and never silently no-op — they produce an
/// era-correct error page rendered inside the frame.
/// </summary>
public static class FrameLoader
{
    /// <summary>
    /// Loads the child document for a frame/iframe src attribute.
    /// Returns null when the frame should legitimately stay blank
    /// (empty src, about:blank).  Never throws.
    /// </summary>
    /// <param name="baseUrl">Absolute base URL of the page containing the
    /// frame (resolution base for relative srcs; ignored for absolute srcs).</param>
    /// <param name="src">Raw src attribute value.</param>
    /// <param name="frameW">Frame content width for layout.</param>
    /// <param name="frameH">Frame content height for layout.</param>
    /// <param name="http">HTTP client (null → http(s) srcs produce a
    /// network error page instead of throwing).</param>
    /// <param name="cookies">Cookie store shared with the parent page.</param>
    /// <param name="ct">Cancellation token (the shell applies a hard
    /// deadline so a stalled TLS handshake cannot wedge a frame forever).</param>
    /// <param name="runScript">Optional inline-script executor wired to a
    /// per-frame JS interpreter; parse-time scripts run through it exactly
    /// like they do for the top-level document.</param>
    public static async Task<FrameContent?> LoadAsync(
        string? baseUrl, string src,
        int frameW, int frameH,
        HttpClient? http, CookieStore cookies, CancellationToken ct,
        InlineScriptExecutor? runScript = null)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;

        string trimmed = src.Trim();

        // Absolute srcs (any known scheme) resolve to themselves — never
        // let them near a base-URL join (file://+file:// would mangle).
        bool absolute =
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

        // Resolve relative srcs through the image resolver: it already
        // handles file:// bases (no double-slash file://// mangling),
        // data: URLs and absolute http(s) srcs inside local pages.
        string abs = absolute
            ? trimmed
            : ImageCache.ResolveUrl(trimmed, baseUrl);
        Retro96.DebugLog.Write($"[FRAME] src='{trimmed}' base='{baseUrl}' resolved='{abs}' size={frameW}x{frameH}");

        ParsedUrl parsed;
        try { parsed = ParsedUrl.Parse(abs); }
        catch { return ErrorContent(ErrorPage.MalformedUrl(abs), abs, frameW, frameH); }

        try
        {
            switch (parsed.Scheme)
            {
                case "about":
                    // about:blank is a legitimately empty frame — keep the
                    // blank view the caller already installed.
                    return null;

                case "file":
                    {
                        bool parentIsHostOpenedLocalPage =
                            baseUrl?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true;
                        if (!parentIsHostOpenedLocalPage && !BrowserRuntime.AllowPageFileAccess)
                            return ErrorContent(
                                ErrorPage.AccessDenied(abs), abs, frameW, frameH);

                        string? local = FileUrls.LocalPathFromFileUrl(parsed);
                        if (local == null || !File.Exists(local))
                            return ErrorContent(
                                ErrorPage.LocalFileNotFound(local ?? parsed.Path), abs, frameW, frameH);

                        byte[] bytes = await File.ReadAllBytesAsync(local, ct);
                        string html = BodyDecoder.Decode(bytes, declaredCharset: null);
                        return BuildContent(html, parsed, abs, frameW, frameH,
                            cookies, runScript);
                    }

                case "http":
                case "https":
                    {
                        if (http == null)
                            return ErrorContent(
                                ErrorPage.NetworkError(abs, "No HTTP client available"), abs, frameW, frameH);

                        var result = await http.GetAsync(parsed, cookies, ct);
                        Retro96.DebugLog.Write($"[FRAME] response resolved='{abs}' result={result.GetType().Name} " +
                            (result is HttpSuccess success
                                ? $"status={success.StatusCode} bytes={success.Body.Length}"
                                : result is HttpError error ? $"error='{error.Message}'" : ""));
                        return result switch
                        {
                            HttpSuccess { StatusCode: 200 } ok =>
                                BuildLoggedContent(ok, parsed, abs, frameW, frameH, cookies, runScript),
                            HttpSuccess err => ErrorContent(
                                HttpStatusPage(err.StatusCode, abs), abs, frameW, frameH),
                            HttpError e => ErrorContent(
                                ErrorPage.NetworkError(abs, e.Message), abs, frameW, frameH),
                            CertError c => ErrorContent(
                                ErrorPage.CertificateError(abs, c.Message), abs, frameW, frameH),
                            TooManyRedirects => ErrorContent(
                                ErrorPage.TooManyRedirects(abs), abs, frameW, frameH),
                            _ => ErrorContent(
                                ErrorPage.NetworkError(abs, "Frame load failed"), abs, frameW, frameH)
                        };
                    }

                default:
                    return ErrorContent(
                        ErrorPage.ProtocolNotSupported(abs, parsed.Scheme), abs, frameW, frameH);
            }
        }
        catch (OperationCanceledException)
        {
            return ErrorContent(ErrorPage.Timeout(abs), abs, frameW, frameH);
        }
        catch (Exception ex)
        {
            // Never let a frame failure escape into the message loop.
            return ErrorContent(ErrorPage.NetworkError(abs, ex.Message), abs, frameW, frameH);
        }
    }

    private static FrameContent BuildLoggedContent(
        HttpSuccess response, ParsedUrl parsed, string abs,
        int frameW, int frameH, CookieStore cookies, InlineScriptExecutor? runScript)
    {
        string html = BodyDecoder.Decode(response.Body, response.Charset);
        var content = BuildContent(html, parsed, abs, frameW, frameH, cookies, runScript);
        Retro96.DebugLog.Write($"[FRAME] parsed resolved='{abs}' title='{content.Document.Title}' " +
            $"root={content.RootBox.Width:0.#}x{content.RootBox.Height:0.#} " +
            $"frames={content.RootBox.Descendants().Count(b => b.BoxType == BoxType.Frame)}");
        return content;
    }

    private static FrameContent BuildContent(
        string html, ParsedUrl url, string abs, int frameW, int frameH,
        CookieStore cookies, InlineScriptExecutor? runScript)
    {
        var doc = HtmlParser.Parse(html, url, cookies, runScript);
        StyleResolver.Resolve(doc, Math.Max(1, frameW));
        var root = LayoutEngine.BuildLayoutTree(doc, Math.Max(1, frameW), Math.Max(1, frameH));
        return new FrameContent(doc, root, abs);
    }

    private static FrameContent ErrorContent(string errorHtml, string abs,
                                              int frameW, int frameH) =>
        BuildContent(errorHtml, ParsedUrl.Parse("about:blank"), abs, frameW, frameH,
            new CookieStore(), null);

    /// <summary>Same status-code → error-page mapping the top-level
    /// navigation uses, so a 404 inside a frame reads identically to a
    /// 404 in the main window.</summary>
    private static string HttpStatusPage(int statusCode, string url) => statusCode switch
    {
        400 => ErrorPage.BadRequest(url),
        401 => ErrorPage.Unauthorized(url),
        403 => ErrorPage.AccessDenied(url),
        404 => ErrorPage.NotFound(url),
        500 => ErrorPage.ServerError(url),
        503 => ErrorPage.ServiceUnavailable(url),
        _ => ErrorPage.GenericHttpError(statusCode, url)
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// file:// URL helpers — shared by Form1 (top-level navigation, stylesheets)
// and FrameLoader.  Formerly private inside Form1; identical logic.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Local-path ↔ canonical file:/// URL conversions.</summary>
public static class FileUrls
{
    /// <summary>Local path behind a file: URL, accepting both
    /// file:///C:/x and file://C:/x forms; null when unmappable.</summary>
    public static string? LocalPathFromFileUrl(ParsedUrl url)
    {
        if (url.Scheme != "file") return null;

        string p;
        try
        {
            p = Uri.UnescapeDataString(url.Path);
        }
        catch
        {
            return null;
        }
        if (p.StartsWith("//")) p = p[2..];            // strip authority slashes

        if (p.StartsWith('/'))
        {
            // After the leading '/', the drive letter is p[1] and the colon
            // p[2] ("/C:/dir/x").  This used to test p[1]==':' && IsLetter(p[0]),
            // but p[0] is the '/', so the check never matched and every
            // file:///C:/... URL kept its leading slash — invalid on Windows,
            // so typed file URLs and Reload reported "File Not Found".
            if (p.Length >= 3 && p[2] == ':' && char.IsLetter(p[1]))
                return p[1..];                        // file:///C:/dir/x
            return p;                                  // file:///home/z/x
        }
        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]))
            return p;                                  // file://C:/dir/x

        return null;
    }

    /// <summary>Canonical file:/// URL for a local path (used as base URL
    /// so every relative resolution downstream is well-formed).</summary>
    public static string CanonicalFileUrl(string localPath) =>
        "file:///" + localPath.Replace('\\', '/');
}

// ─────────────────────────────────────────────────────────────────────────────
// Byte-body → text decoding (charset sniffing) — shared by Form1's top-level
// response processing and FrameLoader.  Formerly private inside Form1;
// identical logic (BOM → strict UTF-8 validity → declared charset →
// Latin-1 default), plus the meta-charset re-scan the top-level path uses.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Decodes response bodies with era-correct charset behaviour.</summary>
public static class BodyDecoder
{
    /// <summary>
    /// Decodes a response body.  Order: BOM sniff → strict UTF-8 sniff →
    /// declared charset → iso-8859-1 (the 1996 default).  If the sniffed
    /// result declares a different charset in a meta tag, the body is
    /// re-decoded with it.
    /// </summary>
    public static string Decode(byte[] body, string? declaredCharset,
                                string? overrideCharset = null)
    {
        string? charset = (overrideCharset ?? declaredCharset)
            ?.Trim().Trim('"', '\'');

        if (string.IsNullOrEmpty(charset) || charset == "unknown")
            charset = SniffCharset(body);

        string text;
        try { text = Encoding.GetEncoding(charset).GetString(body); }
        catch
        {
            try { text = Encoding.GetEncoding("iso-8859-1").GetString(body); }
            catch { text = Encoding.Latin1.GetString(body); }
        }

        // Meta charset inside the document overrides the sniff when it
        // names a genuinely different encoding (the top-level path has
        // always done this; frames now match).
        string? meta = ScanMetaCharset(text);
        if (meta?.Length > 0 &&
            !meta.Equals(charset, StringComparison.OrdinalIgnoreCase))
        {
            try { return Encoding.GetEncoding(meta).GetString(body); }
            catch { /* keep the first decode */ }
        }
        return text;
    }

    /// <summary>
    /// BOM → utf-8/utf-16; else strict UTF-8 validation with a multi-byte
    /// requirement (pure ASCII sniffs as iso-8859-1 harmlessly — identical
    /// decoding); else iso-8859-1 (the 1996 default).
    /// </summary>
    public static string SniffCharset(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            return "utf-8";
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
            return "unicode";
        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
            return "unicode";

        // Strict UTF-8 walk: any invalid sequence → Latin-1.
        bool sawMultiByte = false;
        int i = 0;
        while (i < body.Length)
        {
            byte b = body[i];
            if (b < 0x80) { i++; continue; }

            int seqLen;
            if ((b & 0xE0) == 0xC0) seqLen = 2;
            else if ((b & 0xF0) == 0xE0) seqLen = 3;
            else if ((b & 0xF8) == 0xF0) seqLen = 4;
            else return "iso-8859-1";                       // stray continuation/invalid lead

            if (i + seqLen > body.Length)
                return "iso-8859-1";                        // truncated sequence
            for (int k = 1; k < seqLen; k++)
                if ((body[i + k] & 0xC0) != 0x80)
                    return "iso-8859-1";
            if (seqLen == 2 && b < 0xC2)
                return "iso-8859-1";                        // overlong encoding
            sawMultiByte = true;
            i += seqLen;
        }
        return sawMultiByte ? "utf-8" : "iso-8859-1";
    }

    /// <summary>
    /// Scans the first chunk of a document for
    /// &lt;meta charset=…&gt; / &lt;meta http-equiv=content-type …charset=…&gt;.
    /// </summary>
    public static string? ScanMetaCharset(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        // Meta tags live in the head — the first 4 KB is always enough.
        string head = html.Length > 4096 ? html[..4096] : html;

        int pos = 0;
        while (true)
        {
            int tagIdx = head.IndexOf("<meta", pos, StringComparison.OrdinalIgnoreCase);
            if (tagIdx < 0) break;
            int end = head.IndexOf('>', tagIdx);
            if (end < 0) break;
            string tag = head[tagIdx..end];
            pos = end + 1;

            // charset="…" / charset=… (HTML 5 style)
            int cs = tag.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
            if (cs < 0) continue;
            int eq = tag.IndexOf('=', cs);
            if (eq < 0) continue;
            string val = tag[(eq + 1)..].TrimStart();
            if (val.Length == 0) continue;
            if (val[0] is '"' or '\'')
            {
                char quote = val[0];
                int close = val.IndexOf(quote, 1);
                if (close > 1) val = val[1..close];
                else val = val[1..];
            }
            else
            {
                int sp = val.IndexOfAny(new[] { ' ', '\t', '\'', '"', '/', '>' });
                if (sp > 0) val = val[..sp];
            }
            val = val.Trim().TrimEnd(';');
            if (val.Length > 0) return val;
        }
        return null;
    }
}
