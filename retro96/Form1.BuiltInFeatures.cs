using System.Text.RegularExpressions;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96;

public partial class Form1
{
    private readonly ToolStripDropDownButton _bookmarksMenu = new("Bookmarks");
    private readonly ToolStripDropDownButton _historyMenu = new("History");
    private readonly ToolStripDropDownButton _viewMenu = new("View");
    private readonly ToolStripMenuItem _downloadsMenu = new("Downloads");
    private readonly BookmarkStore _bookmarkStore = new();
    private readonly HistoryStore _historyStore = new();
    private readonly DownloadManager _downloadManager = new();
    private readonly MidiPlayer _midiPlayer = new();
    private readonly ToolStrip _midiBar = new() { GripStyle = ToolStripGripStyle.Hidden, Visible = false };
    private readonly ToolStripLabel _midiLabel = new("MIDI");
    private readonly ToolStripButton _midiPlay = new("Play");
    private readonly ToolStripButton _midiPause = new("Pause");
    private readonly ToolStripButton _midiStop = new("Stop");
    private readonly ToolStripButton _midiLoop = new("Loop") { CheckOnClick = true };
    private readonly ToolStripProgressBar _pluginProgress = new() { Width = 120, Visible = false };
    private bool _builtInsInitialized;
    private bool _midiUiSuppressed;
    private byte[]? _embeddedMidiBytes;
    private string? _embeddedMidiName;
    private DomElement? _embeddedMidiElement;
    private int _embeddedMidiRepeatCount = 1;
    private int _embeddedMidiVolume = 100;

    private void InitializeBuiltInFeatures()
    {
        if (_builtInsInitialized) return;
        _builtInsInitialized = true;

        BuildBookmarksMenu();
        BuildHistoryMenu();
        _viewMenu.DropDownItems.Add(_downloadsMenu);
        _downloadsMenu.Click += (_, _) => OpenDownloadsWindow();
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _viewMenu.DropDownItems.Add("Mute MIDI").Click += (_, _) => _midiPlayer.Stop();

        _midiBar.Items.Add(_midiLabel);
        _midiBar.Items.Add(new ToolStripSeparator());
        _midiBar.Items.Add(_midiPlay); _midiBar.Items.Add(_midiPause); _midiBar.Items.Add(_midiStop); _midiBar.Items.Add(_midiLoop);
        _midiPlay.Click += (_, _) => _midiPlayer.Resume();
        _midiPause.Click += (_, _) => _midiPlayer.Pause();
        _midiStop.Click += (_, _) => _midiPlayer.Stop();
        _midiPlayer.StateChanged += (_, _) => BeginInvokeSafe(UpdateMidiUi);
        _midiPlayer.PlaybackComplete += (_, _) => BeginInvokeSafe(() => { UpdateMidiUi(); _pluginManager?.RaiseAudioComplete(); });
        _downloadManager.Changed += (_, _) => { };

        _statusStrip.Items.Add(_pluginProgress);
        InitializePluginHostUi();
        _canvas.PluginContextMenuRequested += (menu, context) => _pluginManager?.PopulateContextMenu(menu, context);
        _canvas.EmbeddedMidiControlRequested += HandleEmbeddedMidiControl;
        // Keep the built-in menus on the same top toolbar row as File.
        int fileIndex = _toolbar.Items.IndexOf(_btnFile);
        if (fileIndex >= 0)
        {
            int insertAt = fileIndex + 1;
            _toolbar.Items.Insert(insertAt++, _bookmarksMenu);
            _toolbar.Items.Insert(insertAt++, _historyMenu);
            _toolbar.Items.Insert(insertAt, _viewMenu);
        }

        _midiBar.BringToFront();
        Controls.Add(_midiBar);
        _midiBar.BringToFront();

        Activated += (_, _) => _pluginManager?.RaiseWindowFocusChanged(true);
        Deactivate += (_, _) => _pluginManager?.RaiseWindowFocusChanged(false);
    }

    private void BuildBookmarksMenu()
    {
        _bookmarksMenu.DropDownItems.Clear();
        bool bookmarked = _currentPageUrl != null && _bookmarkStore.Contains(_currentPageUrl);
        var toggle = new ToolStripMenuItem(bookmarked ? "Remove Bookmark" : "Add Bookmark");
        toggle.Enabled = !string.IsNullOrWhiteSpace(_currentPageUrl);
        toggle.Click += (_, _) =>
        {
            if (_currentPageUrl == null) return;
            _bookmarkStore.Toggle(PluginCurrentTitle, _currentPageUrl);
            BuildBookmarksMenu();
        };
        _bookmarksMenu.DropDownItems.Add(toggle);
        _bookmarksMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var bookmark in _bookmarkStore.Items.OrderByDescending(x => x.Added).Take(30))
        {
            var item = new ToolStripMenuItem(bookmark.Title);
            item.ToolTipText = bookmark.Url;
            item.Click += (_, _) => NavigateTo(bookmark.Url);
            _bookmarksMenu.DropDownItems.Add(item);
        }
        _bookmarksMenu.DropDownItems.Add(new ToolStripSeparator());
        _bookmarksMenu.DropDownItems.Add("Manage Bookmarks…").Click += (_, _) => OpenBookmarksManager();
    }

    private void BuildHistoryMenu()
    {
        _historyMenu.DropDownItems.Clear();
        var clear = new ToolStripMenuItem("Clear History");
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Clear all browsing history?", "Retro96", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { _historyStore.Clear(); BuildHistoryMenu(); }
        };
        _historyMenu.DropDownItems.Add(clear);
        _historyMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var item in _historyStore.Items.Take(20))
        {
            var row = new ToolStripMenuItem(item.Title) { ToolTipText = item.Url };
            row.Click += (_, _) => NavigateTo(item.Url);
            _historyMenu.DropDownItems.Add(row);
        }
        _historyMenu.DropDownItems.Add(new ToolStripSeparator());
        _historyMenu.DropDownItems.Add("Show All History…").Click += (_, _) => OpenHistoryManager();
    }

    private void OpenBookmarksManager()
    {
        using var dialog = new BookmarkManagerDialog(_bookmarkStore);
        dialog.ShowDialog(this);
        BuildBookmarksMenu();
    }

    private void OpenHistoryManager()
    {
        using var dialog = new HistoryManagerDialog(_historyStore);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Tag is string url) NavigateTo(url);
        BuildHistoryMenu();
    }

    private void OpenDownloadsWindow()
    {
        var dialog = new DownloadsDialog(_downloadManager);
        dialog.Show(this);
    }

    private void UpdateMidiUi()
    {
        bool visible = !_midiUiSuppressed && !string.IsNullOrWhiteSpace(_midiPlayer.CurrentFileName);
        _midiBar.Visible = visible;
        _midiLabel.Text = visible ? "MIDI: " + _midiPlayer.CurrentFileName : "MIDI";
        _midiLoop.Checked = _midiPlayer.Loop;
        _canvas.UpdateEmbeddedMidiControls(_midiPlayer.Loop, _midiPlayer.IsPlaying,
            _embeddedMidiElement != null ? _embeddedMidiVolume : (int)Math.Round(_midiPlayer.Volume * 100f));
    }

    private void HandleEmbeddedMidiControl(string action)
    {
        if (action.StartsWith("volume:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(action[7..], out int volume))
            {
                _embeddedMidiVolume = Math.Clamp(volume, 0, 100);
                _midiPlayer.Volume = _embeddedMidiVolume / 100f;
                UpdateMidiUi();
            }
            return;
        }

        switch (action)
        {
            case "play":
                if (_midiPlayer.CurrentFileName != null)
                {
                    _midiPlayer.Resume();
                }
                else if (_embeddedMidiBytes != null && _embeddedMidiName != null)
                {
                    _ = StartStoredEmbeddedMidiAsync();
                    return;
                }
                break;
            case "pause":
                _midiPlayer.Pause();
                break;
            case "stop":
                _midiPlayer.Stop();
                break;
            case "loop":
                _midiPlayer.Loop = !_midiPlayer.Loop;
                break;
        }
        UpdateMidiUi();
    }

    private async Task StartStoredEmbeddedMidiAsync()
    {
        if (_embeddedMidiBytes == null || _embeddedMidiName == null) return;
        try
        {
            _midiPlayer.Volume = _embeddedMidiVolume / 100f;
            await _midiPlayer.PlayBytesAsync(_embeddedMidiBytes, _embeddedMidiName, _embeddedMidiRepeatCount).ConfigureAwait(false);
            BeginInvokeSafe(UpdateMidiUi);
        }
        catch { }
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }

    private static string SuggestDownloadFileName(string url, IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("content-disposition", out var disposition))
        {
            var m = Regex.Match(disposition, "filename\\s*=\\s*\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value)) return Path.GetFileName(m.Groups[1].Value.Trim());
        }
        try
        {
            string name = Path.GetFileName(ParsedUrl.Parse(url).Path);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return "download";
    }

    private static bool LooksLikeMidi(ParsedUrl url, HttpSuccess success)
    {
        string path = url.Path ?? string.Empty;
        return success.ContentType.Equals("audio/midi", StringComparison.OrdinalIgnoreCase) ||
               success.ContentType.Equals("audio/x-midi", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".midi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldDownload(HttpSuccess success, ParsedUrl url)
    {
        if (success.Headers.TryGetValue("content-disposition", out var cd) && cd.Contains("attachment", StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikeMidi(url, success)) return false;
        string ct = success.ContentType ?? "";
        if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ||
            ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("text/css", StringComparison.OrdinalIgnoreCase)) return false;
        string path = url.Path.ToLowerInvariant();
        string[] renderable = [".htm", ".html", ".txt", ".css", ".gif", ".jpg", ".jpeg", ".png", ".xbm"];
        if (renderable.Any(path.EndsWith)) return false;
        return ct.StartsWith("application/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryHandleBinaryNavigationAsync(HttpSuccess success, ParsedUrl url, CancellationToken ct, long generation)
    {
        if (LooksLikeMidi(url, success))
        {
            _embeddedMidiBytes = null;
            _embeddedMidiName = null;
            _embeddedMidiElement = null;
            _midiUiSuppressed = false;
            string name = SuggestDownloadFileName(url.ToAbsolute(), success.Headers);
            if (generation != _navGeneration) return true;
            await _midiPlayer.PlayBytesAsync(success.Body, name, false).ConfigureAwait(false);
            if (generation == _navGeneration)
            {
                BeginInvokeSafe(() => { UpdateMidiUi(); _statusLabel.Text = "Playing MIDI: " + name; });
                _canvas.ClearForNavigation();
            }
            return true;
        }
        if (!ShouldDownload(success, url)) return false;
        string fileName = SuggestDownloadFileName(url.ToAbsolute(), success.Headers);
        using var save = new SaveFileDialog { FileName = fileName, Title = "Save Download", OverwritePrompt = true };
        if (save.ShowDialog(this) != DialogResult.OK) return true;
        _downloadManager.Start(url.ToAbsolute(), success.Body, fileName, save.FileName);
        _statusLabel.Text = "Download started: " + fileName;
        return true;
    }

    private async Task StartEmbeddedMidiAsync(DomDocument document, ParsedUrl baseUrl, CancellationToken ct, long generation)
    {
        // A new page owns the audio session. Stop the previous page's sound,
        // but keep the browser's top-level MIDI toolbar semantics unchanged.
        _midiPlayer.Stop();
        _embeddedMidiBytes = null;
        _embeddedMidiName = null;
        _embeddedMidiElement = null;
        _embeddedMidiRepeatCount = 1;
        _embeddedMidiVolume = 100;
        _midiUiSuppressed = false;
        BeginInvokeSafe(() => _canvas.ClearEmbeddedMidiControls());

        var candidates = document.ElementDescendants()
            .Where(e => e.TagName is "bgsound" or "embed")
            .Where(e => !string.IsNullOrWhiteSpace(e.GetAttr("src")))
            .ToList();

        // Pages often contained both declarations. Use the first tag that
        // would actually start in this browser, avoiding double playback.
        var selected = candidates.FirstOrDefault(ShouldLegacyMidiAutostart)
                    ?? candidates.FirstOrDefault(e => e.TagName == "embed");
        if (selected == null)
        {
            UpdateMidiUi();
            return;
        }

        string? src = selected.GetAttr("src");
        if (!TryResolveMidiSource(baseUrl, src, out string absolute))
        {
            UpdateMidiUi();
            return;
        }

        try
        {
            var result = await _httpClient.GetAsync(ParsedUrl.Parse(absolute), _cookieStore, ct, ResourceKind.Other).ConfigureAwait(false);
            if (result is not HttpSuccess success || generation != _navGeneration)
                return;

            bool isBackgroundSound = selected.TagName == "bgsound";
            int repeatCount = isBackgroundSound
                ? LegacyMidiLoop.ParseBgSoundLoop(selected.GetAttr("loop"))
                : LegacyMidiLoop.ParseEmbedLoop(selected.GetAttr("loop"));
            bool autostart = isBackgroundSound || LegacyMidiLoop.ParseAutoStart(selected.GetAttr("autostart"));
            int volume = isBackgroundSound ? 100 : LegacyMidiLoop.ParseVolume(selected.GetAttr("volume"));
            bool showControls = !isBackgroundSound && EmbedHasVisibleControls(selected);

            _embeddedMidiBytes = success.Body;
            _embeddedMidiName = SuggestDownloadFileName(absolute, success.Headers);
            _embeddedMidiElement = showControls ? selected : null;
            _embeddedMidiRepeatCount = repeatCount;
            _embeddedMidiVolume = volume;
            _midiUiSuppressed = true;
            _midiPlayer.Volume = volume / 100f;
            _midiPlayer.RepeatCount = repeatCount;

            if (autostart)
                await _midiPlayer.PlayBytesAsync(success.Body, _embeddedMidiName, repeatCount).ConfigureAwait(false);

            if (generation == _navGeneration)
            {
                BeginInvokeSafe(() =>
                {
                    if (showControls)
                        _canvas.SetEmbeddedMidiControls(selected, _embeddedMidiName!, repeatCount != 1, _midiPlayer.IsPlaying, volume);
                    else
                        _canvas.ClearEmbeddedMidiControls();
                    UpdateMidiUi();
                });
            }
        }
        catch { }
    }

    private static bool ShouldLegacyMidiAutostart(DomElement element) =>
        element.TagName == "bgsound" || LegacyMidiLoop.ParseAutoStart(element.GetAttr("autostart"));

    private static bool EmbedHasVisibleControls(DomElement element)
    {
        if (LegacyMidiLoop.IsTrue(element.GetAttr("hidden"))) return false;
        string? width = element.GetAttr("width");
        string? height = element.GetAttr("height");
        if (string.IsNullOrWhiteSpace(width) && string.IsNullOrWhiteSpace(height)) return false;
        if (int.TryParse(width, out int w) && w == 0) return false;
        if (int.TryParse(height, out int h) && h == 0) return false;
        return true;
    }

    private static bool TryResolveMidiSource(ParsedUrl baseUrl, string? src, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(src)) return false;
        try
        {
            var resolved = baseUrl.Resolve(src);
            string path = resolved.Path ?? string.Empty;
            if (!path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".midi", StringComparison.OrdinalIgnoreCase))
                return false;
            absolute = resolved.ToAbsolute();
            return true;
        }
        catch { return false; }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.J))
        {
            OpenDownloadsWindow();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void DisposeBuiltInFeatures()
    {
        try { _midiPlayer.Stop(); _midiPlayer.Dispose(); } catch { }
        try { _clipboardPollTimer.Stop(); } catch { }
        try { _pluginNotifyIcon.Visible = false; _pluginNotifyIcon.Dispose(); } catch { }
        try { _downloadManager.Dispose(); } catch { }
    }
}
