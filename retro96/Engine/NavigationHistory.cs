using System;
using System.Collections.Generic;

namespace Retro96.Engine;

/// <summary>
/// One history entry: what to reload when returning, how it was produced,
/// and whether revisiting it should warn about form-data resubmission.
/// </summary>
public record HistoryEntry(
    string Url,
    string? PostData = null,      // non-null when the page came from a POST
    bool AnchorOnly = false)      // true for #fragment-only navigations
{
    /// <summary>True when Back/Forward to this entry must warn the user
    /// before re-sending the form data.</summary>
    public bool CameFromPost => PostData != null;
}

/// <summary>
/// Back/forward navigation stack.
///
/// Period behaviour: revisiting a page re-fetches and re-executes it
/// (scripts and onLoad run again) — no live-state caching; entries that
/// came from POST are flagged so the shell can warn before resubmitting;
/// #fragment navigations become their own entries on top of the document.
/// Per-frame history inside framesets is tracked by the shell, which owns
/// one NavigationHistory per frame.
/// </summary>
public class NavigationHistory
{
    private readonly List<HistoryEntry> _history = new();
    private int _currentIndex = -1;

    /// <summary>
    /// Raised by Back()/Forward()/Go() — the JS history bindings — with the
    /// URL to navigate to.  The shell subscribes once:
    ///   _history.NavigationRequested += url => BeginInvoke(() => NavigateTo(url));
    /// Without a subscriber these methods only move the pointer (the old
    /// behaviour — the page never changed).
    /// </summary>
    public event Action<string>? NavigationRequested;

    public HistoryEntry? CurrentEntry =>
        _currentIndex >= 0 && _currentIndex < _history.Count
            ? _history[_currentIndex]
            : null;

    public string? Current => CurrentEntry?.Url;

    public bool CanGoBack => _currentIndex > 0;

    public bool CanGoForward => _currentIndex < _history.Count - 1;

    /// <summary>Total entries (DOM history.length).</summary>
    public int Count => _history.Count;

    /// <summary>
    /// True when going back/forward from the current entry would land on
    /// a POST page — the shell warns before continuing.
    /// </summary>
    public bool NextBackwardIsPost =>
        _currentIndex > 0 && _history[_currentIndex - 1].CameFromPost;

    public bool NextForwardIsPost =>
        _currentIndex < _history.Count - 1 && _history[_currentIndex + 1].CameFromPost;

    /// <summary>
    /// True when the current entry itself originated from a POST — used
    /// on reload to decide whether to warn.
    /// </summary>
    public bool CurrentIsPost => CurrentEntry?.CameFromPost ?? false;

    /// <summary>Push a plain GET navigation.</summary>
    public void Push(string url) => Push(new HistoryEntry(url));

    /// <summary>
    /// Push an entry, truncating any forward entries.
    ///
    /// Back/forward re-arrival: the shell navigates via Peek(±1) and the
    /// page-load path then Push()es the loaded URL — recognise that here
    /// and MOVE the pointer instead of stacking a duplicate entry.  The
    /// old push-everything behaviour destroyed the forward stack and made
    /// Back oscillate between the two most recent pages forever.  (Trade-
    /// off: clicking a link to the exact page you just came from now moves
    /// the pointer back rather than stacking a duplicate — the much rarer
    /// case loses.)
    /// </summary>
    public void Push(HistoryEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Url))
            return;

        // Exact repeat of the current entry — nothing to do.
        if (_currentIndex >= 0 &&
            _history[_currentIndex].Url == entry.Url &&
            _history[_currentIndex].PostData == entry.PostData)
            return;

        // Re-arrival at the PREVIOUS entry — this is a Back navigation.
        if (_currentIndex > 0 &&
            _history[_currentIndex - 1].Url == entry.Url &&
            _history[_currentIndex - 1].PostData == entry.PostData)
        {
            _currentIndex--;
            return;   // forward history survives
        }

        // Re-arrival at the NEXT entry — this is a Forward navigation.
        if (_currentIndex >= 0 && _currentIndex + 1 < _history.Count &&
            _history[_currentIndex + 1].Url == entry.Url &&
            _history[_currentIndex + 1].PostData == entry.PostData)
        {
            _currentIndex++;
            return;
        }

        if (_currentIndex < _history.Count - 1)
            _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);

        _history.Add(entry);
        _currentIndex = _history.Count - 1;
    }

    /// <summary>
    /// Fragment navigation: stacks an AnchorOnly entry on top of the
    /// current one unless we're already at that fragment.
    /// </summary>
    public void PushAnchor(string urlWithFragment)
    {
        if (_currentIndex >= 0 &&
            _history[_currentIndex].Url == urlWithFragment)
            return;
        Push(new HistoryEntry(urlWithFragment, AnchorOnly: true));
    }

    public HistoryEntry? BackEntry()
    {
        if (!CanGoBack) return null;
        return _history[_currentIndex - 1];
    }

    public HistoryEntry? ForwardEntry()
    {
        if (!CanGoForward) return null;
        return _history[_currentIndex + 1];
    }

    public string? Back()
    {
        var entry = BackEntry();
        if (entry == null) return null;
        _currentIndex--;
        NavigationRequested?.Invoke(entry.Url);
        return entry.Url;
    }

    public string? Forward()
    {
        var entry = ForwardEntry();
        if (entry == null) return null;
        _currentIndex++;
        NavigationRequested?.Invoke(entry.Url);
        return entry.Url;
    }

    /// <summary>
    /// Relative navigation: Go(-1) = Back, Go(1) = Forward, Go(0) = reload.
    /// </summary>
    public HistoryEntry? Go(int n)
    {
        int newIndex = _currentIndex + n;
        if (newIndex < 0 || newIndex >= _history.Count)
            return null;
        _currentIndex = newIndex;
        var entry = _history[newIndex];
        NavigationRequested?.Invoke(entry.Url);
        return entry;
    }

    /// <summary>The entry Go(n) would move to, without moving.</summary>
    public HistoryEntry? Peek(int n)
    {
        int newIndex = _currentIndex + n;
        return newIndex >= 0 && newIndex < _history.Count
            ? _history[newIndex]
            : null;
    }

    // DOM-compat wrappers used by the JS history bindings
    public void GoBack()   { Back(); }
    public void GoForward() { Forward(); }
}