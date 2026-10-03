namespace Retro96;

public partial class Form1
{
    private readonly ToolStripDropDownButton _bookmarksMenu = new("Bookmarks");
    private readonly ToolStripDropDownButton _historyMenu = new("History");
    private readonly BookmarkStore _bookmarkStore = new();
    private readonly HistoryStore _historyStore = new();

    private void InitializeBookmarksAndHistory()
    {
        BuildBookmarksMenu();
        BuildHistoryMenu();
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

    private void AddCurrentPageBookmark()
    {
        if (string.IsNullOrWhiteSpace(_currentPageUrl))
        {
            MessageBox.Show(this, "There is no page to bookmark.", "Bookmarks",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_bookmarkStore.Contains(_currentPageUrl))
        {
            MessageBox.Show(this, "This page is already bookmarked.", "Bookmarks",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string title = PluginCurrentTitle;
        _bookmarkStore.Toggle(title, _currentPageUrl);
        BuildBookmarksMenu();
        MessageBox.Show(this, $"Bookmark added for {title}.", "Bookmarks",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
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
}
