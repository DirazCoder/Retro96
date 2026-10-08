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

public static class HttpLinkHeaderParser
{
    public static IReadOnlyList<string> ParseStylesheetHrefs(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return Array.Empty<string>();

        var hrefs = new List<string>();
        foreach (string entry in SplitEntries(header))
        {
            string candidate = entry.Trim();
            if (candidate.Length < 3 || candidate[0] != '<')
                continue;

            int uriEnd = candidate.IndexOf('>');
            if (uriEnd <= 1)
                continue;
            string href = candidate[1..uriEnd].Trim();
            if (href.Length == 0)
                continue;

            string parameters = candidate[(uriEnd + 1)..];
            if (!TryReadParameters(parameters, out var relValues))
                continue;
            if (relValues.Any(value => value.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(rel => rel.Equals("stylesheet", StringComparison.OrdinalIgnoreCase))))
                hrefs.Add(href);
        }

        return hrefs;
    }

    public static void AddStylesheetLinks(DomDocument document, string? header)
    {
        ArgumentNullException.ThrowIfNull(document);
        var links = ParseStylesheetHrefs(header);
        if (links.Count == 0)
            return;

        DomElement? head = document.ElementDescendants()
            .FirstOrDefault(element => element.TagName == "head");
        foreach (string href in links)
        {
            var link = new DomElement("link");
            link.SetAttr("rel", "stylesheet");
            link.SetAttr("href", href);
            if (head != null)
                head.AppendChild(link);
            else
                document.AppendChild(link);
        }
    }

    private static IEnumerable<string> SplitEntries(string header)
    {
        int start = 0;
        bool inUri = false;
        bool inQuotedString = false;
        bool escaped = false;
        for (int i = 0; i < header.Length; i++)
        {
            char c = header[i];
            if (inQuotedString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inQuotedString = false;
                continue;
            }

            if (inUri)
            {
                if (c == '>')
                    inUri = false;
                continue;
            }

            if (c == '<')
                inUri = true;
            else if (c == '"')
                inQuotedString = true;
            else if (c == ',')
            {
                yield return header[start..i];
                start = i + 1;
            }
        }

        yield return header[start..];
    }

    private static bool TryReadParameters(string text, out List<string> relValues)
    {
        relValues = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
            if (pos == text.Length)
                break;
            if (text[pos++] != ';')
                return false;
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;

            int nameStart = pos;
            while (pos < text.Length && IsTokenCharacter(text[pos]))
                pos++;
            if (pos == nameStart)
                return false;
            string name = text[nameStart..pos];
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
            if (pos >= text.Length || text[pos++] != '=')
                return false;
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;

            string value;
            if (pos < text.Length && text[pos] == '"')
            {
                pos++;
                var builder = new StringBuilder();
                bool closed = false;
                while (pos < text.Length)
                {
                    char c = text[pos++];
                    if (c == '\\')
                    {
                        if (pos >= text.Length)
                            return false;
                        builder.Append(text[pos++]);
                    }
                    else if (c == '"')
                    {
                        closed = true;
                        break;
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }
                if (!closed)
                    return false;
                value = builder.ToString();
            }
            else
            {
                int valueStart = pos;
                while (pos < text.Length && IsTokenCharacter(text[pos]))
                    pos++;
                if (pos == valueStart)
                    return false;
                value = text[valueStart..pos];
            }

            if (name.Equals("rel", StringComparison.OrdinalIgnoreCase))
                relValues.Add(value);
        }

        return true;
    }

    private static bool IsTokenCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or
            '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}

public static class StylesheetLinkSelection
{
    public static DomElement InsertLoadedStylesheet(
        DomDocument document, DomElement link, string cssText)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(cssText);

        var style = new DomElement("style");
        if (link.GetAttr("media") is { Length: > 0 } media)
            style.SetAttr("media", media);
        style.AppendChild(new DomText { Data = cssText });
        if (link.Parent is { } parent)
            parent.InsertBefore(style, link.NextSibling);
        else
            document.AppendChild(style);
        return style;
    }

    public static IReadOnlyList<string> GetAvailableTitles(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.ElementDescendants()
            .Where(element => element.TagName == "link" &&
                HasRelToken(element, "stylesheet") &&
                element.HasAttr("href"))
            .Select(element => element.GetAttr("title")?.Trim())
            .Where(title => !string.IsNullOrEmpty(title))
            .Select(title => title!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<DomElement> SelectForDocument(
        DomDocument document, string? activeTitle = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var stylesheetLinks = document.ElementDescendants()
            .Where(element => element.TagName == "link" &&
                HasRelToken(element, "stylesheet") &&
                element.HasAttr("href"))
            .ToList();

        if (!string.IsNullOrWhiteSpace(activeTitle) &&
            stylesheetLinks.Any(link => link.GetAttr("title")?.Trim().Equals(
                activeTitle, StringComparison.Ordinal) == true))
        {
            return stylesheetLinks
                .Where(link =>
                {
                    string? title = link.GetAttr("title")?.Trim();
                    return string.IsNullOrEmpty(title) ||
                        title.Equals(activeTitle, StringComparison.Ordinal);
                })
                .ToArray();
        }

        var defaultStyle = document.ElementDescendants()
            .Where(element => element.TagName == "meta" &&
                element.GetAttr("http-equiv")?.Equals(
                    "Default-Style", StringComparison.OrdinalIgnoreCase) == true)
            .Select(element => element.GetAttr("content")?.Trim())
            .LastOrDefault();
        if (string.IsNullOrEmpty(defaultStyle))
            defaultStyle = null;

        string? selectedTitle = defaultStyle;
        if (selectedTitle == null)
        {
            selectedTitle = stylesheetLinks
                .Where(link => !HasRelToken(link, "alternate"))
                .Select(link => link.GetAttr("title")?.Trim())
                .FirstOrDefault(title => !string.IsNullOrEmpty(title));
        }

        return stylesheetLinks
            .Where(link =>
            {
                string? title = link.GetAttr("title")?.Trim();
                if (string.IsNullOrEmpty(title))
                    return true;
                return selectedTitle != null &&
                    !HasRelToken(link, "alternate") &&
                    title.Equals(selectedTitle, StringComparison.Ordinal);
            })
            .ToArray();
    }

    private static bool HasRelToken(DomElement element, string token) =>
        element.GetAttr("rel")?.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Any(value => value.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;
}

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
        InlineScriptExecutor? runScript = null,
        ExternalScriptLoader? loadExternalScript = null,
        Func<DomDocument, ParsedUrl, CancellationToken, Task>? loadStylesheets = null)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;

        string trimmed = src.Trim();

        // Absolute srcs (any known scheme) resolve to themselves — never
        // let them near a base-URL join (file://+file:// would mangle).
        bool absolute =
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
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
                        return await BuildContentAsync(html, parsed, abs, frameW, frameH,
                            cookies, runScript, loadExternalScript, loadStylesheets, null, ct);
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
                                await BuildLoggedContentAsync(ok, parsed, abs, frameW, frameH,
                                    cookies, runScript, loadExternalScript, loadStylesheets, ct),
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

    private static async Task<FrameContent> BuildLoggedContentAsync(
        HttpSuccess response, ParsedUrl parsed, string abs,
        int frameW, int frameH, CookieStore cookies, InlineScriptExecutor? runScript,
        ExternalScriptLoader? loadExternalScript,
        Func<DomDocument, ParsedUrl, CancellationToken, Task>? loadStylesheets,
        CancellationToken ct)
    {
        string html = BodyDecoder.Decode(response.Body, response.Charset);
        ParsedUrl documentUrl = string.IsNullOrWhiteSpace(response.EffectiveUrl)
            ? parsed
            : ParsedUrl.Parse(response.EffectiveUrl);
        string documentAbsoluteUrl = string.IsNullOrWhiteSpace(response.EffectiveUrl)
            ? abs
            : response.EffectiveUrl;
        var content = await BuildContentAsync(html, documentUrl, documentAbsoluteUrl,
            frameW, frameH, cookies, runScript, loadExternalScript, loadStylesheets,
            response.Headers.TryGetValue("link", out string? linkHeader) ? linkHeader : null, ct);
        Retro96.DebugLog.Write($"[FRAME] parsed resolved='{abs}' title='{content.Document.Title}' " +
            $"root={content.RootBox.Width:0.#}x{content.RootBox.Height:0.#} " +
            $"frames={content.RootBox.Descendants().Count(b => b.BoxType == BoxType.Frame)}");
        return content;
    }

    private static async Task<FrameContent> BuildContentAsync(
        string html, ParsedUrl url, string abs, int frameW, int frameH,
        CookieStore cookies, InlineScriptExecutor? runScript, ExternalScriptLoader? loadExternalScript,
        Func<DomDocument, ParsedUrl, CancellationToken, Task>? loadStylesheets,
        string? linkHeader, CancellationToken ct)
    {
        var doc = HtmlParser.Parse(html, url, cookies, runScript, loadExternalScript);
        HttpLinkHeaderParser.AddStylesheetLinks(doc, linkHeader);
        if (loadStylesheets != null)
            await loadStylesheets(doc, url, ct);
        StyleResolver.Resolve(doc, Math.Max(1, frameW));
        var root = LayoutEngine.BuildLayoutTree(doc, Math.Max(1, frameW), Math.Max(1, frameH));
        return new FrameContent(doc, root, abs);
    }

    private static FrameContent ErrorContent(string errorHtml, string abs,
                                              int frameW, int frameH) =>
        BuildErrorContent(errorHtml, abs, frameW, frameH);

    private static FrameContent BuildErrorContent(
        string errorHtml, string abs, int frameW, int frameH)
    {
        var doc = HtmlParser.Parse(errorHtml, ParsedUrl.Parse("about:blank"), new CookieStore());
        StyleResolver.Resolve(doc, Math.Max(1, frameW));
        var root = LayoutEngine.BuildLayoutTree(doc, Math.Max(1, frameW), Math.Max(1, frameH));
        return new FrameContent(doc, root, abs);
    }

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
    /// <summary>
    /// Converts a local filesystem path into the one canonical URL form used
    /// throughout the browser: file:///C:/... for drive paths and
    /// file://server/share/... for UNC paths.
    /// </summary>
    public static string CanonicalFileUrl(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("Local path is empty.", nameof(localPath));

        string path = localPath.Trim().Replace('\\', '/');

        // Do not ask the host OS to turn a Windows drive/UNC path into an
        // absolute path when tests or tooling run on a non-Windows host.
        if (!IsDrivePath(path) && !IsUncPath(path) && !path.StartsWith('/'))
            path = Path.GetFullPath(path).Replace('\\', '/');

        if (IsUncPath(path))
        {
            string unc = path.TrimStart('/');
            return "file://" + EscapeFilePath(unc, encodeColon: true);
        }

        // POSIX-mapped drive path ("/C:/web/..." from LocalPathFromFileUrl
        // on a non-Windows host) canonicalises back to the drive URL form
        // instead of percent-escaping the drive colon.
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];

        if (IsDrivePath(path))
            return "file:///" + EscapeFilePath(path, encodeColon: false);

        if (!path.StartsWith('/'))
            path = "/" + path;
        return "file://" + EscapeFilePath(path, encodeColon: true);
    }

    /// <summary>
    /// Maps a parsed file: URL back to a native filesystem path. Handles
    /// canonical drive URLs, the older file://C:/ form, POSIX paths and UNC
    /// file://server/share URLs.
    /// </summary>
    public static string? LocalPathFromFileUrl(ParsedUrl url)
    {
        if (!string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            return null;

        string p;
        try { p = Uri.UnescapeDataString(url.Path); }
        catch { return null; }

        if (!string.IsNullOrEmpty(url.Host))
        {
            string unc = "\\\\" + url.Host + "/" + p.TrimStart('/');
            return unc.Replace('/', '\\');
        }

        // Normalise the leading-slash soup before deciding what the path
        // means. Period pages (and hand-built test pages) produce
        // file:/x, file:///x and file:////x alike; every one of those
        // carries an EMPTY authority, so only the segment content decides
        // whether this is a drive path, a UNC path or a POSIX path.
        // Mapping //tmp/x or ////tmp/x to a bogus \\tmp\... UNC path on
        // POSIX broke every relative link on a locally-opened page.
        int leading = 0;
        while (leading < p.Length && p[leading] == '/') leading++;
        string body = p[leading..].Replace('\\', '/');

        // Drive path: file:///C:/web/index.html (any slash count).
        if (body.Length >= 2 && char.IsLetter(body[0]) && body[1] == ':')
        {
            return OperatingSystem.IsWindows()
                ? body.Replace('/', '\\')
                : "/" + body;   // rooted POSIX form; File APIs report not-found
        }

        // Host-less authority form: //server/share/... is UNC on Windows.
        if (leading == 2 && body.Contains('/') && OperatingSystem.IsWindows())
            return "\\\\" + body.Replace('/', '\\');

        // POSIX (or unknown) → single-root path on any OS.
        if (body.Length == 0)
            return OperatingSystem.IsWindows() ? "\\" : "/";
        return (OperatingSystem.IsWindows() ? "\\" : "/") + body;
    }

    /// <summary>
    /// Resolves an href against a local file: document using filesystem
    /// semantics at the URL/filesystem boundary. This handles spaces, '..',
    /// drive-root links, fragments and UNC paths consistently for links,
    /// forms, frames and resource loads.
    /// </summary>
    public static string Resolve(ParsedUrl baseUrl, string href)
    {
        if (!string.Equals(baseUrl.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            return baseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                ? FileUrls.Resolve(baseUrl, href)
                : baseUrl.Resolve(href).ToAbsolute();

        string trimmed = (href ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return baseUrl.ToAbsolute();

        if (trimmed.StartsWith('#'))
            return StripQueryAndFragment(baseUrl.ToAbsolute()) + trimmed;

        if (trimmed.StartsWith('?'))
            return StripQueryAndFragment(baseUrl.ToAbsolute()) + trimmed;

        // Protocol-relative URLs inherit the file: scheme. In a local page
        // that is naturally a UNC-style file URL rather than a web URL.
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "file:" + trimmed;

        if (HasExplicitScheme(trimmed))
        {
            try
            {
                var parsed = ParsedUrl.Parse(trimmed);
                if (parsed.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    string? local = LocalPathFromFileUrl(parsed);
                    return local == null
                        ? parsed.ToAbsolute()
                        : AppendUrlSuffix(CanonicalFileUrl(local), parsed.Query, parsed.Fragment);
                }
                return parsed.ToAbsolute();
            }
            catch
            {
                return trimmed;
            }
        }

        string? basePath = LocalPathFromFileUrl(baseUrl);
        if (basePath == null)
            return baseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? FileUrls.Resolve(baseUrl, trimmed)
                    : baseUrl.Resolve(trimmed).ToAbsolute();

        SplitPathSuffix(trimmed, out string rawPath, out string query, out string fragment);
        string decodedPath;
        try { decodedPath = Uri.UnescapeDataString(rawPath); }
        catch { decodedPath = rawPath; }
        decodedPath = decodedPath.Replace('/', Path.DirectorySeparatorChar);

        string combined;
        if (decodedPath.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            if (IsDrivePath(basePath))
            {
                // Standard file:// semantics use the current drive root.
                // Legacy local sites also commonly used leading-slash paths
                // as a shorthand for a file beside the saved page. Preserve
                // that compatibility without scattering the fallback across
                // image/frame/link loaders.
                string driveRoot = basePath[..2] + decodedPath;
                string? pageDir = Path.GetDirectoryName(basePath);
                string besidePage = string.IsNullOrEmpty(pageDir)
                    ? decodedPath.TrimStart(Path.DirectorySeparatorChar)
                    : Path.Combine(pageDir, decodedPath.TrimStart(Path.DirectorySeparatorChar));
                combined = File.Exists(driveRoot) ? driveRoot : besidePage;
            }
            else
            {
                combined = decodedPath;
            }
        }
        else
        {
            string? dir = Path.GetDirectoryName(basePath);
            if (string.IsNullOrEmpty(dir)) dir = basePath;
            combined = Path.Combine(dir!, decodedPath);
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(combined); }
        catch { fullPath = combined; }

        return AppendUrlSuffix(CanonicalFileUrl(fullPath), query, fragment);
    }

    /// <summary>
    /// Normalises a local file URL or absolute filesystem path entered in the
    /// address bar. On a local page, an existing relative file is also accepted.
    /// </summary>
    public static bool TryResolveAddressBarInput(
        string input, string? currentPageUrl, out string canonicalUrl)
    {
        canonicalUrl = string.Empty;
        string t = (input ?? string.Empty).Trim();
        if (t.Length == 0) return false;

        if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = ParsedUrl.Parse(t);
                if (!parsed.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                    return false;
                string? local = LocalPathFromFileUrl(parsed);
                if (local == null) return false;
                canonicalUrl = AppendUrlSuffix(
                    CanonicalFileUrl(local), parsed.Query, parsed.Fragment);
                return true;
            }
            catch { return false; }
        }

        if (IsDrivePath(t) || IsUncPath(t))
        {
            SplitPathSuffix(t, out string path, out string query, out string fragment);
            canonicalUrl = AppendUrlSuffix(CanonicalFileUrl(path), query, fragment);
            return true;
        }

        // POSIX absolute paths are first-class address-bar targets on
        // non-Windows hosts, exactly like C:\ paths are on Windows.
        if (!OperatingSystem.IsWindows() && t.StartsWith('/'))
        {
            SplitPathSuffix(t, out string path, out string query, out string fragment);
            canonicalUrl = AppendUrlSuffix(CanonicalFileUrl(path), query, fragment);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(currentPageUrl) &&
            currentPageUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var baseUrl = ParsedUrl.Parse(currentPageUrl);
                string candidate = Resolve(baseUrl, t);
                string? local = LocalPathFromFileUrl(ParsedUrl.Parse(candidate));
                if (local != null && File.Exists(local))
                {
                    canonicalUrl = candidate;
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    private static bool HasExplicitScheme(string s)
    {
        int colon = s.IndexOf(':');
        if (colon <= 0 || !char.IsLetter(s[0])) return false;
        for (int i = 1; i < colon; i++)
        {
            char c = s[i];
            if (!(char.IsLetterOrDigit(c) || c is '+' or '-' or '.'))
                return false;
        }
        return true;
    }

    private static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
        (path[2] == '/' || path[2] == '\\');

    private static bool IsUncPath(string path) =>
        path.StartsWith("//", StringComparison.Ordinal) ||
        path.StartsWith("\\\\", StringComparison.Ordinal);

    private static void SplitPathSuffix(
        string value, out string path, out string query, out string fragment)
    {
        fragment = string.Empty;
        int hash = value.IndexOf('#');
        if (hash >= 0)
        {
            fragment = value[(hash + 1)..];
            value = value[..hash];
        }

        query = string.Empty;
        int q = value.IndexOf('?');
        if (q >= 0)
        {
            query = value[(q + 1)..];
            value = value[..q];
        }
        path = value;
    }

    private static string AppendUrlSuffix(string url, string query, string fragment)
    {
        if (!string.IsNullOrEmpty(query)) url += "?" + query;
        if (!string.IsNullOrEmpty(fragment)) url += "#" + fragment;
        return url;
    }

    private static string StripQueryAndFragment(string url)
    {
        int q = url.IndexOf('?');
        int h = url.IndexOf('#');
        int cut = q < 0 ? h : h < 0 ? q : Math.Min(q, h);
        return cut < 0 ? url : url[..cut];
    }

    private static string EscapeFilePath(string path, bool encodeColon)
    {
        var sb = new StringBuilder(path.Length + 16);
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            bool safe =
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c is '-' or '_' or '.' or '~' or '/' ||
                (!encodeColon && c == ':');
            if (safe) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Byte-body → text decoding (charset sniffing) — shared by Form1's top-level
// response processing and FrameLoader.  Formerly private inside Form1;
// identical logic (BOM → strict UTF-8 validity → declared charset →
// Windows-1252 fallback), plus the meta-charset re-scan the top-level path uses.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Decodes response bodies with era-correct charset behaviour.</summary>
public static class BodyDecoder
{
    static BodyDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Decodes a response body with deterministic precedence: BOM, explicit
    /// transport/override charset, then a conservative UTF-8/Windows-1252 sniff.
    /// A meta declaration is used only when no stronger external declaration
    /// was supplied, so a page cannot silently reinterpret an explicit
    /// non-UTF-8 HTTP charset.
    /// </summary>
    public static string Decode(byte[] body, string? declaredCharset,
                                string? overrideCharset = null)
    {
        string? explicitCharset = (overrideCharset ?? declaredCharset)
            ?.Trim().Trim('"', '\'');
        explicitCharset = NormalizeCharsetLabel(explicitCharset);
        bool hasExplicitCharset = !string.IsNullOrEmpty(explicitCharset) &&
                                  !explicitCharset.Equals("unknown", StringComparison.OrdinalIgnoreCase);

        string charset;
        if (HasUtf8Bom(body))
            charset = "utf-8";
        else if (body.Length >= 2 && (body[0] == 0xFF && body[1] == 0xFE || body[0] == 0xFE && body[1] == 0xFF))
            charset = "unicode";
        else if (hasExplicitCharset)
            charset = explicitCharset!;
        else
            charset = SniffCharset(body);

        string text;
        try { text = Encoding.GetEncoding(charset).GetString(body); }
        catch
        {
            try { text = Encoding.GetEncoding("windows-1252").GetString(body); }
            catch { text = Encoding.Latin1.GetString(body); }
        }

        // Meta charset is a fallback when transport did not supply a usable
        // declaration. BOM/transport charset always wins.
        if (!hasExplicitCharset && !HasAnyBom(body))
        {
            string? meta = ScanMetaCharset(text);
            meta = NormalizeCharsetLabel(meta);
            if (meta?.Length > 0 &&
                !meta.Equals(charset, StringComparison.OrdinalIgnoreCase))
            {
                try { return Encoding.GetEncoding(meta).GetString(body); }
                catch { /* keep the first decode */ }
            }
        }
        return text;
    }

    private static bool HasUtf8Bom(byte[] body) =>
        body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF;

    private static bool HasAnyBom(byte[] body) =>
        HasUtf8Bom(body) ||
        (body.Length >= 2 && ((body[0] == 0xFF && body[1] == 0xFE) ||
                              (body[0] == 0xFE && body[1] == 0xFF)));

    private static string? NormalizeCharsetLabel(string? charset)
    {
        if (charset == null) return null;
        string label = charset.Trim();
        return label.ToLowerInvariant() switch
        {
            "iso-8859-1" or "iso8859-1" or "latin1" or "latin-1" or
            "ascii" or "us-ascii" or "ansi_x3.4-1968" => "windows-1252",
            _ => label
        };
    }

    /// <summary>
    /// BOM → utf-8/utf-16; else strict UTF-8 validation with a multi-byte
    /// requirement (pure ASCII sniffs as Windows-1252 harmlessly — identical
    /// decoding); otherwise use the web-compatible Windows-1252 fallback.
    /// </summary>
    public static string SniffCharset(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            return "utf-8";
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
            return "unicode";
        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
            return "unicode";

        // Strict UTF-8 walk: any invalid sequence → the web-compatible
        // Windows-1252 fallback for legacy single-byte content.
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
            else return "windows-1252";                       // stray continuation/invalid lead

            if (i + seqLen > body.Length)
                return "windows-1252";                        // truncated sequence
            for (int k = 1; k < seqLen; k++)
                if ((body[i + k] & 0xC0) != 0x80)
                    return "windows-1252";
            if (seqLen == 2 && b < 0xC2)
                return "windows-1252";                        // overlong encoding
            sawMultiByte = true;
            i += seqLen;
        }
        return sawMultiByte ? "utf-8" : "windows-1252";
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
