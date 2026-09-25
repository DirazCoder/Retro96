using System;
using System.Collections.Concurrent;
using Retro96.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Retro96.Engine.Network;

namespace Retro96.Engine.Render;

/// <summary>
/// Thread-safe cache of decoded images keyed by absolute URL.
///
/// Failed fetches cache the broken-image icon (no refetch storms); in-
/// flight requests for the same URL are de-duplicated; animated GIFs
/// advance per-frame with each frame's own delay.  GetCurrentFrame
/// returning null means "still loading" — the caller paints whatever it
/// has (placeholder) and repaints when the fetch completes.
/// </summary>
public class ImageCache : IDisposable
{
    private readonly ConcurrentDictionary<string, DecodedImage> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, AnimationState> _animState = new();
    private readonly ConcurrentDictionary<string, byte> _knownBad = new(StringComparer.Ordinal);

    // URL -> UTC time after which a TRANSIENT failure may be retried.  The
    // painter calls GetAsync on every repaint, so a failure must stay cached
    // for a cooldown (no request storm) yet eventually expire (no permanent
    // red X from one dropped connection).
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _wasTransient = new(StringComparer.Ordinal);
    private static readonly TimeSpan TransientCooldown = TimeSpan.FromSeconds(20);
    private bool _disposed;

    /// <summary>
    /// Cookies for image fetches.  Ad banners, hit counters and
    /// session-protected images need cookies exactly like pages do —
    /// without this every such image 401s/403s.  Optional; null keeps the
    /// cookie-less behaviour.  (Wire it in Form1: _imageCache.CookieStore
    /// = _cookieStore;)
    /// </summary>
    public CookieStore? CookieStore { get; set; }

    /// <summary>
    /// True only while the current document is one the user opened directly
    /// via File → Open (a genuine local file: page). A remote page must
    /// never be able to pull file:// image sources off the user's disk just
    /// by embedding one — that flag stays false for anything fetched over
    /// http(s), even if it references a file:// URL. Form1 sets this on
    /// navigation start/end; it is not inferred from the image URL itself.
    /// </summary>
    public bool HostOpenedLocalPage { get; set; }

    /// <summary>
    /// Raised when a URL finishes loading SUCCESSFULLY after having been
    /// cached as a transient failure — i.e. a refetch that no caller is
    /// awaiting (the painter fires GetAsync and moves on).  The shell hooks
    /// this to repaint, otherwise the recovered image would sit in the
    /// cache invisible until something else happened to redraw.
    /// May fire on a background thread.
    /// </summary>
    public event Action<string>? ImageRecovered;

    private sealed class AnimationState
    {
        public int FrameIndex;
        public DateTime FrameStartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Resolve a possibly-relative URL against a base using the engine's
    /// own ParsedUrl resolution (trailing-slash and ../ semantics match
    /// what the HTML parser produced for every other URL on the page).
    /// </summary>
    public static string ResolveUrl(string url, string? baseUrl)
    {
        if (string.IsNullOrEmpty(url)) return url;

        // Inline data: URLs are absolute by definition — never let them
        // near a base-URL join (they used to mangle into
        // file:////dir/data:image/... and then fail to load).
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return url;

        // Probe "is this already an absolute http(s) URL" without letting
        // ANY exception escape: Parse assumes a schemeless, non-"://"
        // input is a bare host ("www.site.com/x"), not a path — so
        // root-relative image srcs like "/images/anibar.gif" (no scheme,
        // leading slash gives an empty "host" candidate) fail Uri.TryCreate
        // and Parse throws ArgumentException. Parse can also throw
        // UnsafeUrlException for junk schemes used as img src — and this
        // method is called OUTSIDE any try/catch by the renderer, so an
        // escaping exception here takes the whole paint down. Both cases
        // fall through to baseUrl.Resolve below, which is the whole point
        // of a relative image src in the first place.
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;   // explicit absolute http(s) URL

        // (Schemeless inputs like "topper.gif" must NOT early-exit:
        // ParsedUrl.Parse's address-bar host-guessing would turn them into
        // http://topper.gif, and the resource would fail DNS — they have
        // to fall through to base-URL resolution below.)

        if (string.IsNullOrEmpty(baseUrl))
            return url;

        // Wayback-rewritten documents use root-relative /web/... URLs for
        // frames and images. When the saved capture is opened from file://,
        // the page's local base must not turn that path into file:///web/... .
        // It is still a Wayback replay URL and belongs to web.archive.org.
        if (url.StartsWith("/web/", StringComparison.OrdinalIgnoreCase) &&
            (baseUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
             baseUrl.Contains("theoldnet.com", StringComparison.OrdinalIgnoreCase) ||
             baseUrl.Contains("web.archive.org", StringComparison.OrdinalIgnoreCase)))
            return "https://web.archive.org" + url;

        // file:// base — pages opened with File → Open resolve every
        // relative image against the on-disk directory. The generic
        // ParsedUrl.Resolve path used to produce malformed double-slash
        // file:////C:/... URLs (the opaque file parse keeps the authority
        // slashes inside Path), which then failed to load. Resolve the
        // local path directly instead.
        try
        {
            var parsedBase = ParsedUrl.Parse(baseUrl);
            if (parsedBase.Scheme == "file")
            {
                if (TryGetLocalPath(parsedBase, out string basePath))
                    return FileUrlFromRelative(url, basePath);
                return url;
            }
        }
        catch { /* fall through to the generic resolver */ }

        try
        {
            return ParsedUrl.Parse(baseUrl).Resolve(url).ToAbsolute();
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// Extracts a local filesystem path from a parsed file: URL, handling
    /// both the canonical <c>file:///C:/dir/x.htm</c> form (empty
    /// authority) and the two-slash <c>file://C:/dir/x.htm</c> form the
    /// shell's File-Open builds. Returns false for UNC-ish or drive-less
    /// paths the engine cannot map back to disk.
    /// </summary>
    private static bool TryGetLocalPath(ParsedUrl url, out string localPath)
    {
        localPath = "";
        if (url.Scheme != "file") return false;

        string p;
        try
        {
            p = Uri.UnescapeDataString(url.Path);
        }
        catch
        {
            return false;
        }

        // Strip the "//" authority marker; a third leading slash is the
        // start of the path proper (file:///…).
        if (p.StartsWith("//")) p = p[2..];

        if (p.StartsWith('/'))
        {
            // file:///C:/dir/x  →  Windows drive letter after the path slash
            if (p.Length >= 3 && p[2] == ':' && char.IsLetter(p[1]))
            {
                localPath = p[1..];
                return true;
            }
            // file:///home/z/x  →  Unix absolute path
            localPath = p;
            return true;
        }

        // file://C:/dir/x — drive form without the path slash
        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]))
        {
            localPath = p;
            return true;
        }

        return false;
    }

    /// <summary>Builds a canonical file:/// URL from a base file path + a relative src.</summary>
    private static string FileUrlFromRelative(string url, string basePath)
    {
        string trimmed = url.Trim();

        // Absolute http(s) srcs inside a local page stay absolute — but
        // only with an EXPLICIT scheme: ParsedUrl.Parse would also
        // http://-prefix a bare "topper.gif" (address-bar host guessing),
        // and the file:// page's own relative images would come back
        // unchanged — every local image a broken icon.
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        string combined;
        if (trimmed.StartsWith('/'))
        {
            // Root-relative on a file base normally means the drive root.
            // Local QA pages commonly keep their fixtures beside the page,
            // though, so use that directory when the drive-root candidate is
            // absent. HTTP root-relative URLs never enter this file branch.
            string root = basePath.Length >= 2 && basePath[1] == ':'
                ? basePath[..2]
                : "";
            string driveRootPath = root + System.IO.Path.GetFullPath(trimmed);
            string pageRelativePath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(basePath) ?? basePath,
                trimmed.TrimStart('/', '\\'));
            combined = System.IO.File.Exists(driveRootPath)
                ? driveRootPath
                : pageRelativePath;
        }
        else
        {
            string dir = System.IO.Path.GetDirectoryName(basePath) ?? basePath;
            combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, trimmed));
        }

        // canonical file:/// form — exactly three slashes before an
        // absolute path ("/home/…" / "C:/…"), not four.
        return "file://" + (combined.StartsWith('/') ? combined : "/" + combined)
            .Replace('\\', '/');
    }

    private static string? LocalPathFromFileUrl(string absoluteUrl)
    {
        try
        {
            var parsed = ParsedUrl.Parse(absoluteUrl);
            return TryGetLocalPath(parsed, out string local) ? local : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The frame that should be displayed right now.  Null while the
    /// image is still loading (or on a URL that failed — callers should
    /// check TryGetBroken for that case).
    /// </summary>
    public Bitmap? GetCurrentFrame(string absoluteUrl)
    {
        if (_disposed) return null;

        if (!_cache.TryGetValue(absoluteUrl, out var decoded) ||
            decoded.Frames.Count == 0)
            return null;

        if (!decoded.IsAnimated || decoded.Frames.Count == 1)
            return decoded.Frames[0];

        if (!BrowserRuntime.AnimatedImagesEnabled)
            return decoded.Frames[0];

        var state = _animState.GetOrAdd(absoluteUrl, _ => new AnimationState());

        // Advance as many frames as elapsed time covers so playback stays
        // correct even when paints are sparse
        var now = DateTime.UtcNow;
        int guard = 0;
        while (guard++ < 1000)
        {
            int delayMs = state.FrameIndex < decoded.DelaysMs.Count
                ? decoded.DelaysMs[state.FrameIndex]
                : 100;
            if (delayMs <= 0) delayMs = 100;

            var elapsed = (now - state.FrameStartedAt).TotalMilliseconds;
            if (elapsed < delayMs) break;

            state.FrameStartedAt = state.FrameStartedAt.AddMilliseconds(delayMs);
            state.FrameIndex = (state.FrameIndex + 1) % decoded.Frames.Count;
        }

        return decoded.Frames[state.FrameIndex];
    }

    /// <summary>True when the URL has been fetched and decoded (good or broken).</summary>
    public bool IsLoaded(string absoluteUrl) =>
        _cache.ContainsKey(absoluteUrl);

    /// <summary>True when the URL failed to load and the broken icon was cached.</summary>
    public bool IsBroken(string absoluteUrl) =>
        _knownBad.ContainsKey(absoluteUrl) || _retryAfter.ContainsKey(absoluteUrl);

    public Task<DecodedImage> GetAsync(string url, string? baseUrl,
        ResourceLoader loader, CancellationToken ct)
        => GetAsync(ResolveUrl(url, baseUrl), loader, ct);

    public async Task<DecodedImage> GetAsync(string absoluteUrl,
        ResourceLoader loader, CancellationToken ct)
    {
        Retro96.DebugLog.Write($"[IMAGE] request url='{absoluteUrl}'");
        // After Dispose (form closing while a prefetch is still in
        // flight) there is nothing left to fetch into or cache onto.
        if (_disposed)
            return BrokenResult();

        if (TryGetCached(absoluteUrl, out var cached))
            return cached;

        var semaphore = _semaphores.GetOrAdd(absoluteUrl, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            if (TryGetCached(absoluteUrl, out cached))
                return cached;

            ParsedUrl parsedUrl;
            try
            {
                // Inline data: URLs (base64 or URL-encoded payloads) decode
                // directly — the era-authentic image-in-a-URL trick used by
                // rollover buttons.  ParsedUrl rejects the data: scheme, so
                // this must run before the parse.
                if (absoluteUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var dataDecoded = DataUriDecoder.Decode(absoluteUrl);
                    if (dataDecoded == null || dataDecoded.Frames.Count == 0 ||
                        dataDecoded.Frames[0].Width == 0)
                    {
                        if (dataDecoded != null)
                            foreach (var f in dataDecoded.Frames) f.Dispose();
                        return MarkBroken(absoluteUrl);
                    }
                    _cache[absoluteUrl] = dataDecoded;
                    NaturalImageSizes.Register(absoluteUrl, dataDecoded);
                    return dataDecoded;
                }

                parsedUrl = ParsedUrl.Parse(absoluteUrl);
            }
            catch
            {
                return MarkBroken(absoluteUrl);
            }

            // file:// pages (File → Open) resolve their images from disk.
            // Everything non-http used to hit MarkBroken immediately, so a
            // locally-opened page never showed a single image — the
            // dolekemp96.org landing page was all broken icons.
            if (parsedUrl.Scheme == "file")
            {
                // Trust decision already made by IsSafeImageUrl above
                // (HostOpenedLocalPage OR AllowPageFileAccess) — don't
                // duplicate it here with a narrower check that ignores
                // the AllowPageFileAccess override.
                var local = LocalPathFromFileUrl(absoluteUrl);
                if (local == null)
                    return MarkBroken(absoluteUrl);

                DecodedImage? fileDecoded = null;
                try
                {
                    if (System.IO.File.Exists(local))
                    {
                        byte[] bytes = await System.IO.File.ReadAllBytesAsync(local);
                        fileDecoded = ImageDecoder.Decode(bytes, ContentTypeFromPath(local));
                        if (fileDecoded.Frames.Count == 0 || fileDecoded.Frames[0].Width == 0)
                        {
                            foreach (var f in fileDecoded.Frames) f.Dispose();
                            fileDecoded = null;
                        }
                    }
                }
                catch
                {
                    // unreadable/locked file → broken icon
                }

                if (fileDecoded == null)
                    return MarkBroken(absoluteUrl);

                if (_disposed)
                {
                    foreach (var f in fileDecoded.Frames) f.Dispose();
                    return BrokenResult();
                }

                _cache[absoluteUrl] = fileDecoded;
                NaturalImageSizes.Register(absoluteUrl, fileDecoded);
                return fileDecoded;
            }

            if (!parsedUrl.IsHttp)
                return MarkBroken(absoluteUrl);

            DecodedImage? decoded = null;
            bool transientFailure = false;

            // A single dropped connection, timeout or 429/5xx used to be
            // cached as a PERMANENT broken icon — the "images randomly all
            // fail or all work" bug: a burst of parallel fetches to a
            // throttling server failed a few URLs and nothing ever retried
            // them.  Retry transient failures a couple of times with
            // backoff; a clean 4xx or undecodable bytes are final.
            for (int attempt = 0; attempt <= MaxTransientRetries; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(RetryBackoffMs * attempt, ct); }
                    catch (OperationCanceledException) { break; }
                    if (_disposed) return BrokenResult();
                }

                var result = await loader.FetchAsync(
                    absoluteUrl, parsedUrl, CookieStore ?? new CookieStore());

                Retro96.DebugLog.Write($"[IMAGE] response url='{absoluteUrl}' result={result.GetType().Name} " +
                    (result is HttpSuccess responseSuccess
                        ? $"status={responseSuccess.StatusCode} bytes={responseSuccess.Body.Length} type='{responseSuccess.ContentType}'"
                        : result is HttpError responseError ? $"error='{responseError.Message}'" : ""));

                if (result is HttpSuccess success)
                {
                    if (success.StatusCode == 200 && success.Body.Length > 0)
                    {
                        decoded = ImageDecoder.Decode(success.Body, success.ContentType);
                        Retro96.DebugLog.Write($"[IMAGE] decoded url='{absoluteUrl}' " +
                            $"frames={decoded.Frames.Count} size={(decoded.Frames.Count > 0
                                ? $"{decoded.Frames[0].Width}x{decoded.Frames[0].Height}" : "none")}");
                        if (decoded.Frames.Count == 0 || decoded.Frames[0].Width == 0)
                        {
                            // Decode produced nothing usable — treat like a failed
                            // fetch so the reflow pass doesn't adopt a phantom size.
                            foreach (var f in decoded.Frames) f.Dispose();
                            decoded = null;
                        }
                        transientFailure = false;
                        break;                       // decoded, or final bad bytes
                    }

                    // 408/425/429/5xx (and an empty 200 body) are the server
                    // being busy — worth another go.  404/403/410… are final.
                    transientFailure = IsTransientStatus(success.StatusCode) ||
                                       (success.StatusCode == 200 && success.Body.Length == 0);
                    if (!transientFailure) break;
                    continue;
                }

                // Not an HTTP response at all: network-level failure.  Retry,
                // except a navigation cancel (HttpError "Fetch cancelled"),
                // which must stop immediately.
                if (ct.IsCancellationRequested ||
                    (result is HttpError he &&
                     he.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) &&
                     !he.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
                {
                    transientFailure = false;
                    break;
                }
                transientFailure = result is HttpError;   // CertError etc. are final
                if (!transientFailure) break;
            }

            if (decoded == null)
                return MarkBroken(absoluteUrl, transientFailure);

            if (_disposed)
            {
                // Dispose raced us — don't cache frames nobody will free.
                foreach (var f in decoded.Frames) f.Dispose();
                return BrokenResult();
            }

            _cache[absoluteUrl] = decoded;
            NaturalImageSizes.Register(absoluteUrl, decoded);
            Retro96.DebugLog.Write($"[IMAGE] cached url='{absoluteUrl}'");
            if (_wasTransient.TryRemove(absoluteUrl, out _))
            {
                try { ImageRecovered?.Invoke(absoluteUrl); }
                catch { /* a subscriber must never break image loading */ }
            }
            return decoded;
        }
        finally
        {
            semaphore.Release();
        }
    }

    // Transient-failure policy: total attempts = 1 + MaxTransientRetries,
    // waiting RetryBackoffMs * attempt between them (600ms, then 1200ms).
    // Bounded on purpose — the server is often throttling, and hammering
    // it makes that worse.
    private const int MaxTransientRetries = 2;
    private const int RetryBackoffMs = 600;

    /// <summary>Busy/throttled server responses that are worth retrying.</summary>
    private static bool IsTransientStatus(int status) =>
        status is 408 or 425 or 429 || (status >= 500 && status <= 599);

    /// <summary>
    /// Records a failed load.  A PERMANENT failure (404, bad image bytes)
    /// is cached so it never refetches.  A TRANSIENT one (network error,
    /// 429/5xx that survived the retries) still shows the broken icon for
    /// this paint, but is NOT cached — the next paint/navigation asks the
    /// network again instead of being stuck on a red X until restart.
    /// </summary>
    private DecodedImage MarkBroken(string absoluteUrl, bool transient = false)
    {
        var broken = BrokenResult();
        _cache[absoluteUrl] = broken;

        if (transient)
        {
            _retryAfter[absoluteUrl] = DateTime.UtcNow + TransientCooldown;
            _wasTransient[absoluteUrl] = 0;
        }
        else
            _knownBad.TryAdd(absoluteUrl, 0);   // permanent: never refetch

        return broken;
    }

    /// <summary>
    /// Cache lookup that lets an EXPIRED transient failure fall through so
    /// the caller refetches it.  Successful images and permanent failures
    /// never expire.
    /// </summary>
    private bool TryGetCached(string absoluteUrl, out DecodedImage cached)
    {
        if (_cache.TryGetValue(absoluteUrl, out cached!))
        {
            if (_retryAfter.TryGetValue(absoluteUrl, out var due))
            {
                if (DateTime.UtcNow < due)
                    return true;                       // still cooling down

                // Cooldown over: drop the stale broken icon and refetch.
                // The 24x24 placeholder is deliberately NOT disposed here —
                // the painter may be mid-DrawImage on it from another
                // thread, and a use-after-dispose would throw inside paint.
                // The GC reclaims it; it is tiny.
                if (_retryAfter.TryRemove(absoluteUrl, out _))
                    _cache.TryRemove(absoluteUrl, out _);
                cached = null!;
                return false;
            }
            return true;
        }
        return false;
    }

    private static DecodedImage BrokenResult() =>
        new([BrokenImageIcon.Create()], [0], false);

    /// <summary>Best-effort content-type for a local image file.</summary>
    private static string ContentTypeFromPath(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".xbm" => "image/x-xbitmap",
            _ => "application/octet-stream"
        };
    }

    /// <summary>True if a cached image is animated.</summary>
    public bool IsAnimated(string absoluteUrl) =>
        _cache.TryGetValue(absoluteUrl, out var cached) && cached.IsAnimated;

    /// <summary>
    /// True if any cached image is animated — lets the shell avoid running
    /// a repaint timer on static pages.
    /// </summary>
    public bool HasAnimatedImages
    {
        get
        {
            foreach (var decoded in _cache.Values)
                if (decoded.IsAnimated) return true;
            return false;
        }
    }

    public void Clear()
    {
        foreach (var img in _cache.Values)
            foreach (var frame in img.Frames)
                frame.Dispose();
        _cache.Clear();
        _animState.Clear();
        _knownBad.Clear();
        _retryAfter.Clear();
        _wasTransient.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Clear();

        foreach (var sem in _semaphores.Values)
            sem.Dispose();
        _semaphores.Clear();
    }
}
// ─────────────────────────────────────────────────────────────────────────────
// Natural image sizes — the layout-side half of "no WIDTH/HEIGHT on <img>".
//
// The layout engine is pure geometry: it has no ImageCache instance and no
// async patience, so it cannot wait for an image to decode.  But a
// dimensionless <img> must still flow at its NATURAL size once the image
// is known — every browser of the era did this — and the old fallback
// ("placeholder until load") had no "after load" step at all: the box was
// 32×32 forever, squishing e.g. voyagersisland's 468×60 banner exchange
// graphic into a stamp.
//
// So every successful decode (data:, file:, http:) publishes the first
// frame's dimensions here, keyed by absolute URL.  The layout consults it
// when generating an <img> box with no WIDTH/HEIGHT/CSS size; the shell's
// reflow-after-image-load picks the natural size up on the next layout
// pass, exactly like the era's incremental reflow.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Process-wide registry of decoded images' natural sizes, keyed by
/// absolute URL.  Written by <see cref="ImageCache"/> on every successful
/// decode; read by the layout engine for dimensionless &lt;img&gt; elements.
/// </summary>
public static class NaturalImageSizes
{
    private static readonly ConcurrentDictionary<string, (int Width, int Height)> _sizes =
        new(StringComparer.Ordinal);

    /// <summary>Publish a decoded image's natural size (first frame).</summary>
    public static void Register(string absoluteUrl, DecodedImage image)
    {
        if (image.Frames.Count == 0) return;
        Register(absoluteUrl, image.Frames[0].Width, image.Frames[0].Height);
    }

    /// <summary>Publish a natural size directly (test rigs, known assets).</summary>
    public static void Register(string absoluteUrl, int width, int height)
    {
        if (width > 0 && height > 0)
            _sizes[absoluteUrl] = (width, height);
    }

    /// <summary>Natural size for an absolute URL, when decoded at least once.</summary>
    public static bool TryGetSize(string absoluteUrl, out int width, out int height)
    {
        if (_sizes.TryGetValue(absoluteUrl, out var size))
        {
            width = size.Width; height = size.Height;
            return true;
        }
        width = 0; height = 0;
        return false;
    }
}
