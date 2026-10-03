using System;
using System.Collections.Generic;

namespace Retro96.Engine;

/// <summary>
/// Implements the back/forward navigation stack for the Retro96 browser.
/// </summary>
public class NavigationHistory
{
    private readonly List<string> _history = new List<string>();
    private int _currentIndex = -1;

    /// <summary>
    /// Gets the current URL in the history stack, or null if history is empty.
    /// </summary>
    public string? Current
    {
        get
        {
            if (_currentIndex < 0 || _currentIndex >= _history.Count)
                return null;
            return _history[_currentIndex];
        }
    }

    /// <summary>
    /// Gets a value indicating whether there is a previous entry in the history.
    /// </summary>
    public bool CanGoBack => _currentIndex > 0;

    /// <summary>
    /// Gets a value indicating whether there is a next entry in the history.
    /// </summary>
    public bool CanGoForward => _currentIndex < _history.Count - 1;

    /// <summary>
    /// Gets the total number of entries in the history stack (for DOM history.length).
    /// </summary>
    public int Count => _history.Count;

    /// <summary>
    /// Adds a new URL to the history stack, truncating any forward entries if not at the end.
    /// Null or empty URLs are ignored.
    /// </summary>
    /// <param name="url">The URL to add to the history.</param>
    public void Push(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // Truncate forward entries if we are not at the end of the history
        if (_currentIndex < _history.Count - 1)
        {
            _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);
        }

        _history.Add(url);
        _currentIndex = _history.Count - 1;
    }

    /// <summary>
    /// Navigates back one entry in the history stack.
    /// </summary>
    /// <returns>The URL of the previous entry, or null if navigation back is not possible.</returns>
    public string? Back()
    {
        if (!CanGoBack)
            return null;

        _currentIndex--;
        return _history[_currentIndex];
    }

    /// <summary>
    /// Navigates forward one entry in the history stack.
    /// </summary>
    /// <returns>The URL of the next entry, or null if navigation forward is not possible.</returns>
    public string? Forward()
    {
        if (!CanGoForward)
            return null;

        _currentIndex++;
        return _history[_currentIndex];
    }

    /// <summary>
    /// Performs relative navigation in the history stack.
    /// Go(-1) is equivalent to Back(), Go(1) to Forward(), Go(0) reloads current page.
    /// </summary>
    /// <param name="n">Relative offset from current position.</param>
    /// <returns>The URL at the new position, or null if the target is out of bounds.</returns>
    public string? Go(int n)
    {
        int newIndex = _currentIndex + n;
        if (newIndex < 0 || newIndex >= _history.Count)
            return null;

        _currentIndex = newIndex;
        return _history[_currentIndex];
    }

    /// <summary>
    /// Navigates back one entry (matches DOM history.back() usage in JS bindings).
    /// </summary>
    public void GoBack() => Back();

    /// <summary>
    /// Navigates forward one entry (matches DOM history.forward() usage in JS bindings).
    /// </summary>
    public void GoForward() => Forward();
}
