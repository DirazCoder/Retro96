using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Retro96.Engine.Network;

public class ResourceLoader : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _semaphore = new(8, 8); // Concurrency limit of 8
    private readonly ConcurrentDictionary<string, Task<HttpResult>> _inFlight = new();
    private readonly CookieStore _defaultCookies;
    private CancellationTokenSource? _pageCts;
    private int _fetchCount = 0;
    private const int MaxFetchesPerPage = 200;

    public ResourceLoader(CookieStore defaultCookies)
    {
        _defaultCookies = defaultCookies ?? throw new ArgumentNullException(nameof(defaultCookies));
        _pageCts = new CancellationTokenSource();
    }

    public async Task<HttpResult> FetchAsync(string url, ParsedUrl baseUrl, CookieStore cookies)
    {
        cookies ??= _defaultCookies;
        if (_fetchCount >= MaxFetchesPerPage)
            return new HttpError("Too many asset fetches (limit 200 per page)");

        // Resolve relative URL
        ParsedUrl resolvedUrl;
        try
        {
            resolvedUrl = baseUrl.Resolve(url);
        }
        catch (Exception ex)
        {
            return new HttpError($"Failed to resolve URL '{url}': {ex.Message}");
        }

        string absoluteUrl = resolvedUrl.ToAbsolute();

        // Check in-flight de-duplication
        if (_inFlight.TryGetValue(absoluteUrl, out var inFlightTask))
            return await inFlightTask;

        // Create new fetch task
        var fetchTask = FetchInternalAsync(resolvedUrl, cookies);
        _inFlight[absoluteUrl] = fetchTask;

        try
        {
            return await fetchTask;
        }
        finally
        {
            _inFlight.TryRemove(absoluteUrl, out _);
        }
    }

    private async Task<HttpResult> FetchInternalAsync(ParsedUrl url, CookieStore cookies)
    {
        _fetchCount++;
        CancellationToken ct = _pageCts?.Token ?? CancellationToken.None;

        await _semaphore.WaitAsync(ct);
        try
        {
            return await _httpClient.GetAsync(url, cookies, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void CancelAll()
    {
        _pageCts?.Cancel();
        _pageCts = new CancellationTokenSource();
        _fetchCount = 0;
        _inFlight.Clear();
    }

    public void Reset()
    {
        CancelAll();
    }

    public int FetchCount => _fetchCount;

    public void Dispose()
    {
        CancelAll();
        _semaphore.Dispose();
    }
}
