using System.Collections.Concurrent;
using System.Drawing;
using Retro96.Engine.Network;

namespace Retro96.Engine.Render;

/// <summary>
/// Thread-safe cache of decoded images keyed by absolute URL.
/// Uses SemaphoreSlim per URL to prevent duplicate in-flight fetches.
/// On fetch failure: stores BrokenImageIcon so subsequent requests get the placeholder.
/// </summary>
public class ImageCache : IDisposable
{
    private readonly ConcurrentDictionary<string, DecodedImage> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, AnimationState> _animState = new();
    private bool _disposed;

    /// <summary>
    /// Tracks which frame of an animated GIF is currently showing and when it
    /// started, so playback speed follows each frame's own delay instead of a
    /// fixed tick rate.
    /// </summary>
    private sealed class AnimationState
    {
        public int FrameIndex;
        public DateTime FrameStartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Get the frame that should currently be displayed for an animated
    /// image, advancing through frames based on each frame's own delay
    /// (falls back to a 100ms default if a delay entry is missing/zero).
    /// For non-animated images this just returns Frames[0].
    /// Returns null if the URL isn't in cache yet (still loading).
    /// </summary>
    public Bitmap? GetCurrentFrame(string absoluteUrl)
    {
        if (!_cache.TryGetValue(absoluteUrl, out var decoded) || decoded.Frames.Count == 0)
            return null;

        if (!decoded.IsAnimated || decoded.Frames.Count == 1)
            return decoded.Frames[0];

        var state = _animState.GetOrAdd(absoluteUrl, _ => new AnimationState());

        // Advance through as many frames as elapsed time covers, so playback
        // stays correct even if paint calls are sparse or delayed.
        var now = DateTime.UtcNow;
        int guard = 0; // safety cap in case of a pathological 0ms-delay GIF
        while (guard++ < 1000)
        {
            int delayMs = decoded.DelaysMs.Count > state.FrameIndex
                ? decoded.DelaysMs[state.FrameIndex]
                : 100;
            if (delayMs <= 0) delayMs = 100;

            var elapsed = (now - state.FrameStartedAt).TotalMilliseconds;
            if (elapsed < delayMs)
                break;

            state.FrameStartedAt = state.FrameStartedAt.AddMilliseconds(delayMs);
            state.FrameIndex = (state.FrameIndex + 1) % decoded.Frames.Count;
        }

        return decoded.Frames[state.FrameIndex];
    }

    /// <summary>
    /// Resolve a potentially-relative URL against a base URL.
    /// Returns the input unchanged if it is already absolute or base is null.
    /// </summary>
    public static string ResolveUrl(string url, string? baseUrl)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
        if (string.IsNullOrEmpty(baseUrl)) return url;
        try
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, url, out var resolved))
                return resolved.ToString();
        }
        catch { }
        return url;
    }

    /// <summary>
    /// Convenience overload: resolves <paramref name="url"/> against
    /// <paramref name="baseUrl"/> before fetching.
    /// </summary>
    public Task<DecodedImage> GetAsync(string url, string? baseUrl,
        ResourceLoader loader, CancellationToken ct)
        => GetAsync(ResolveUrl(url, baseUrl), loader, ct);

    /// <summary>
    /// Get a cached image or fetch and cache it.
    /// </summary>
    public async Task<DecodedImage> GetAsync(string absoluteUrl, ResourceLoader loader, CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(absoluteUrl, out var cached))
            return cached;

        // Get or create semaphore for this URL
        var semaphore = _semaphores.GetOrAdd(absoluteUrl, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cache.TryGetValue(absoluteUrl, out cached))
                return cached;

            // Parse the absolute URL
            ParsedUrl? parsedUrl = null;
            try
            {
                parsedUrl = ParsedUrl.Parse(absoluteUrl);
            }
            catch
            {
                // Invalid URL - store broken image
                var broken = new DecodedImage([BrokenImageIcon.Create()], [0], false);
                _cache[absoluteUrl] = broken;
                return broken;
            }

            // Fetch the image (pass null for baseUrl since we have absolute URL, null for cookies -> uses loader's default)
            var result = await loader.FetchAsync(absoluteUrl, parsedUrl, null);

            DecodedImage decoded;
            if (result is Retro96.Engine.Network.HttpSuccess success)
            {
                decoded = ImageDecoder.Decode(success.Body, success.ContentType);
            }
            else
            {
                // Fetch failed - store broken image to prevent repeated attempts
                decoded = new DecodedImage([BrokenImageIcon.Create()], [0], false);
            }

            _cache[absoluteUrl] = decoded;
            return decoded;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Clear all cached images.
    /// </summary>
    public void Clear()
    {
        foreach (var img in _cache.Values)
        {
            foreach (var frame in img.Frames)
            {
                frame.Dispose();
            }
        }
        _cache.Clear();
        _animState.Clear();
    }

    /// <summary>
    /// Check if an image is animated (has multiple frames).
    /// Returns false if the image is not in cache.
    /// </summary>
    public bool IsAnimated(string absoluteUrl)
    {
        if (_cache.TryGetValue(absoluteUrl, out var cached))
        {
            return cached.IsAnimated;
        }
        return false;
    }

    /// <summary>
    /// Dispose the cache and all cached images.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Clear();

        foreach (var sem in _semaphores.Values)
        {
            sem.Dispose();
        }
        _semaphores.Clear();
    }
}