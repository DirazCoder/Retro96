using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Retro96.Engine.Network;

public class ResourceLoader : IDisposable
{
    public sealed record FetchRecord(string Url, string Status, int? StatusCode, DateTime StartedUtc, DateTime CompletedUtc);

    private readonly HttpClient _httpClient;
    private const int ConcurrencyPermits = 32;
    private readonly SemaphoreSlim _semaphore = new(ConcurrencyPermits, ConcurrencyPermits);
    private readonly ConcurrentDictionary<string, Lazy<Task<HttpResult>>> _inFlight = new();
    private readonly CookieStore _defaultCookies;

    private CancellationTokenSource _pageCts = new();
    private int _fetchCount;
    private DateTime _lastFetchCompletedUtc = DateTime.MinValue;
    private readonly List<FetchRecord> _history = new();
    private readonly object _historyLock = new();

    // The budget is shared by the top document and all nested frames.
    private static readonly TimeSpan PageIdleReset = TimeSpan.FromSeconds(15);

    public ResourceLoader(CookieStore defaultCookies, HttpClient? httpClient = null)
    {
        _defaultCookies = defaultCookies ?? throw new ArgumentNullException(nameof(defaultCookies));
        // Injectable so test rigs can sandbox the network (the VisualDiff
        // rig passes a loopback-only client); the shell keeps the default
        // socket client.  Behaviour identical when null.
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<HttpResult> FetchAsync(string url, ParsedUrl baseUrl, CookieStore cookies)
    {
        cookies ??= _defaultCookies;

        MaybeResetAfterIdle();

        if (Volatile.Read(ref _fetchCount) >= BrowserRuntime.MaxResourceFetchesPerPage)
            return new HttpError($"Too many asset fetches (limit {BrowserRuntime.MaxResourceFetchesPerPage} per page)");

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
        if (resolvedUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) &&
            !BrowserRuntime.AllowPageFileAccess)
            return new HttpError("Local file resources are blocked by the current trust mode");

        if (!resolvedUrl.IsHttp)
            return new HttpError("Only host-mediated HTTP(S) resources are allowed here");

        DateTime startedUtc = DateTime.UtcNow;

        // Lazy-based de-duplication: two concurrent FetchAsync calls for
        // the same URL used to both miss the dictionary and BOTH hit the
        // network, and the loser's unconditional TryRemove could delete a
        // newer entry.  GetOrAdd picks one Lazy; the loser awaits the
        // winner and never starts its own request.
        var lazy = new Lazy<Task<HttpResult>>(() => FetchInternalAsync(resolvedUrl, cookies));
        var winner = _inFlight.GetOrAdd(absoluteUrl, lazy);

        if (!ReferenceEquals(winner, lazy))
            return await AwaitSafeAsync(winner.Value);

        try
        {
            var result = await AwaitSafeAsync(lazy.Value);
            AddHistory(absoluteUrl, result, startedUtc);
            return result;
        }
        finally
        {
            // Remove only OUR entry — an unconditional TryRemove could
            // delete a newer fetch registered after a page reset.
            _inFlight.TryRemove(
                new KeyValuePair<string, Lazy<Task<HttpResult>>>(absoluteUrl, winner));
            _lastFetchCompletedUtc = DateTime.UtcNow;
        }
    }

    public IReadOnlyList<FetchRecord> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    public void ClearHistory()
    {
        lock (_historyLock) _history.Clear();
    }

    private void AddHistory(string url, HttpResult result, DateTime startedUtc)
    {
        int? code = result is HttpSuccess success ? success.StatusCode : null;
        string status = result switch
        {
            HttpSuccess => "Complete",
            HttpError error => "Error: " + error.Message,
            CertError error => "Certificate: " + error.Message,
            TooManyRedirects => "Too many redirects",
            _ => result.GetType().Name
        };
        lock (_historyLock)
        {
            _history.Add(new FetchRecord(url, status, code, startedUtc, DateTime.UtcNow));
            if (_history.Count > 500) _history.RemoveAt(0);
        }
    }

    private static async Task<HttpResult> AwaitSafeAsync(Task<HttpResult> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return new HttpError("Fetch cancelled");
        }
    }

    /// <summary>
    /// Safety net for shells that never call Reset() between pages: the
    /// per-page fetch budget used to be a SESSION budget — after 200
    /// total asset fetches, every later page failed all its images.  When
    /// nothing is in flight and nothing has been fetched for a while, a
    /// new fetch is almost certainly the first asset of a NEW page —
    /// start a fresh budget.
    /// </summary>
    private void MaybeResetAfterIdle()
    {
        if (_fetchCount == 0 || !_inFlight.IsEmpty)
            return;
        if (DateTime.UtcNow - _lastFetchCompletedUtc < PageIdleReset)
            return;
        _fetchCount = 0;
    }

    private async Task<HttpResult> FetchInternalAsync(ParsedUrl url, CookieStore cookies)
    {
        int fetchCount = Interlocked.Increment(ref _fetchCount);
        if (fetchCount > BrowserRuntime.MaxResourceFetchesPerPage)
        {
            Interlocked.Decrement(ref _fetchCount);
            return new HttpError($"Too many asset fetches (limit {BrowserRuntime.MaxResourceFetchesPerPage} per page)");
        }

        CancellationToken ct = _pageCts.Token;

        int permits = ConcurrencyPermits / BrowserRuntime.MaxConcurrentResourceFetches;
        try
        {
            await _semaphore.WaitAsync(permits, ct);
        }
        catch
        {
            Interlocked.Decrement(ref _fetchCount);
            throw;
        }

        try
        {
            return await _httpClient.GetAsync(url, cookies, ct);
        }
        finally
        {
            // Guarded — Dispose() during shutdown must not throw through
            // in-flight releases.
            try { _semaphore.Release(permits); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
    }

    /// <summary>
    /// Cancel all in-flight fetches and start a fresh per-page budget.
    /// The shell calls this at the start of each navigation.
    /// </summary>
    public void CancelAll()
    {
        _pageCts.Cancel();
        _pageCts = new CancellationTokenSource();
        _fetchCount = 0;
        _inFlight.Clear();
        ClearHistory();
    }

    public void Reset() => CancelAll();

    public int FetchCount => _fetchCount;

    public void Dispose()
    {
        _pageCts.Cancel();
        _pageCts.Dispose();
        _inFlight.Clear();
        _semaphore.Dispose();
    }
}