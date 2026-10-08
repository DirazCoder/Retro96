using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96;

public partial class Form1
{
    private readonly ToolStripDropDownButton _viewMenu = new("View");
    private readonly ToolStripProgressBar _pluginProgress = new() { Width = 120, Visible = false };
    private bool _builtInsInitialized;

    private void InitializeBuiltInFeatures()
    {
        if (_builtInsInitialized) return;
        _builtInsInitialized = true;

        InitializeBookmarksAndHistory();
        InitializeDownloads();
        InitializeMidi();
        _viewMenu.DropDownItems.Add(_pageStyleMenu);
        _pageStyleMenu.DropDownOpening += (_, _) => RefreshPageStyleMenu();
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _viewMenu.DropDownItems.Add("Mute MIDI").Click += (_, _) => _midiPlayer.Stop();

        _statusStrip.Items.Add(_pluginProgress);
        InitializePluginHostUi();
        BuildToolsMenu();
        BuildHelpMenu();
        InitializeFindZoomUi();
        _canvas.PluginContextMenuRequested += (menu, context) => _pluginManager?.PopulateContextMenu(menu, context);
        _canvas.EmbeddedMidiControlRequested += HandleEmbeddedMidiControl;

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

    private readonly ToolStripMenuItem _pageStyleMenu = new("Page Style");

    private void RefreshPageStyleMenu()
    {
        _pageStyleMenu.DropDownItems.Clear();
        var documents = new List<DomDocument>();
        if (_canvas.PageDocument is { } page)
            documents.Add(page);
        foreach (var (_, frame) in _canvas.Frames)
            AddFrameDocuments(frame, documents);

        string[] titles = documents
            .SelectMany(StylesheetLinkSelection.GetAvailableTitles)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var noStyle = new ToolStripMenuItem("No Style")
        {
            Checked = _pageStylesDisabled
        };
        noStyle.Click += (_, _) => SelectPageStyle(null, disableAll: true);
        _pageStyleMenu.DropDownItems.Add(noStyle);
        _pageStyleMenu.DropDownItems.Add(new ToolStripSeparator());

        var defaultStyle = new ToolStripMenuItem("Default Style")
        {
            Checked = !_pageStylesDisabled && _selectedStyleSheetTitle == null
        };
        defaultStyle.Click += (_, _) => SelectPageStyle(null);
        _pageStyleMenu.DropDownItems.Add(defaultStyle);

        foreach (string title in titles)
        {
            var item = new ToolStripMenuItem(title)
            {
                Checked = !_pageStylesDisabled &&
                    title.Equals(_selectedStyleSheetTitle, StringComparison.Ordinal)
            };
            item.Click += (_, _) => SelectPageStyle(title);
            _pageStyleMenu.DropDownItems.Add(item);
        }
        _pageStyleMenu.Enabled = documents.Count > 0;
    }

    private static void AddFrameDocuments(
        BrowserCanvas.FrameView frame, ICollection<DomDocument> documents)
    {
        documents.Add(frame.Document);
        foreach (var (_, child) in frame.ChildFrames)
            AddFrameDocuments(child, documents);
    }

    private void SelectPageStyle(string? title, bool disableAll = false)
    {
        if (string.Equals(_selectedStyleSheetTitle, title, StringComparison.Ordinal) &&
            _pageStylesDisabled == disableAll)
            return;

        _selectedStyleSheetTitle = title;
        _pageStylesDisabled = disableAll;
        _styleSelectionPageUrl = _currentPageUrl;
        if (!string.IsNullOrWhiteSpace(_currentPageUrl))
            Reload();
    }

    private static bool SamePageUrl(string? first, string? second)
    {
        if (first == null || second == null)
            return first == second;
        static string WithoutFragment(string value)
        {
            int fragment = value.IndexOf('#');
            return fragment >= 0 ? value[..fragment] : value;
        }
        return WithoutFragment(first).Equals(
            WithoutFragment(second), StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeBuiltInFeatures()
    {
        DisposeMidi();
        try { _clipboardPollTimer.Stop(); } catch { }
        try { _pluginNotifyIcon.Visible = false; _pluginNotifyIcon.Dispose(); } catch { }
        DisposeDownloads();
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }
}
