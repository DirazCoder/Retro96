using System.Drawing;
using System.Text.Json;

namespace Retro96;

public sealed record BookmarkEntry(string Title, string Url, DateTimeOffset Added);
public sealed record HistoryStoreEntry(string Title, string Url, DateTimeOffset Visited);

internal static class BrowserDataPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Retro96");
    public static string Bookmarks => Path.Combine(Root, "bookmarks.json");
    public static string History => Path.Combine(Root, "history.json");
    public static string Downloads => Path.Combine(Root, "downloads");
}

internal sealed class BookmarkStore
{
    private readonly object _sync = new();
    private List<BookmarkEntry> _items = new();

    public BookmarkStore() => Load();
    public IReadOnlyList<BookmarkEntry> Items { get { lock (_sync) return _items.ToArray(); } }

    public bool Contains(string url)
    {
        lock (_sync) return _items.Any(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    public bool Toggle(string title, string url)
    {
        lock (_sync)
        {
            int index = _items.FindIndex(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _items.RemoveAt(index);
            else _items.Add(new BookmarkEntry(string.IsNullOrWhiteSpace(title) ? url : title, url, DateTimeOffset.UtcNow));
            SaveLocked();
            return index < 0;
        }
    }

    public void Delete(string url)
    {
        lock (_sync)
        {
            _items.RemoveAll(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            Directory.CreateDirectory(BrowserDataPaths.Root);
            if (!File.Exists(BrowserDataPaths.Bookmarks)) return;
            _items = JsonSerializer.Deserialize<List<BookmarkEntry>>(File.ReadAllText(BrowserDataPaths.Bookmarks)) ?? new();
        }
        catch { _items = new(); }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(BrowserDataPaths.Root);
            File.WriteAllText(BrowserDataPaths.Bookmarks, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

internal sealed class HistoryStore
{
    private readonly object _sync = new();
    private List<HistoryStoreEntry> _items = new();

    public HistoryStore() => Load();
    public IReadOnlyList<HistoryStoreEntry> Items { get { lock (_sync) return _items.OrderByDescending(x => x.Visited).ToArray(); } }

    public void Record(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_sync)
        {
            _items.RemoveAll(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            _items.Add(new HistoryStoreEntry(string.IsNullOrWhiteSpace(title) ? url : title, url, DateTimeOffset.UtcNow));
            _items = _items.OrderByDescending(x => x.Visited).Take(500).ToList();
            SaveLocked();
        }
    }

    public void Delete(string url)
    {
        lock (_sync)
        {
            _items.RemoveAll(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            SaveLocked();
        }
    }

    public void Clear()
    {
        lock (_sync) { _items.Clear(); SaveLocked(); }
    }

    public IReadOnlyList<HistoryStoreEntry> Search(string query)
    {
        query ??= string.Empty;
        lock (_sync)
        {
            return _items
                .Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            x.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Visited)
                .ToArray();
        }
    }

    private void Load()
    {
        try
        {
            Directory.CreateDirectory(BrowserDataPaths.Root);
            if (!File.Exists(BrowserDataPaths.History)) return;
            _items = (JsonSerializer.Deserialize<List<HistoryStoreEntry>>(File.ReadAllText(BrowserDataPaths.History)) ?? new())
                .OrderByDescending(x => x.Visited)
                .Take(500)
                .ToList();
        }
        catch { _items = new(); }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(BrowserDataPaths.Root);
            File.WriteAllText(BrowserDataPaths.History, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

internal sealed class BookmarkManagerDialog : Form
{
    private readonly BookmarkStore _store;
    private readonly ListView _list = new();
    private readonly ContextMenuStrip _contextMenu = new();

    public BookmarkManagerDialog(BookmarkStore store)
    {
        _store = store;
        Text = "Retro96 Bookmarks";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(620, 390);
        ClientSize = new Size(820, 520);
        AutoScaleMode = AutoScaleMode.Font;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("Title", 300);
        _list.Columns.Add("URL", 460);
        _list.MouseUp += SelectRowAtMouse;
        _list.DoubleClick += (_, _) => OpenSelected();

        _contextMenu.Items.Add("Open").Click += (_, _) => OpenSelected();
        _contextMenu.Items.Add("Copy URL").Click += (_, _) => CopySelectedUrl();
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Delete").Click += (_, _) => DeleteSelected();
        _contextMenu.Opening += (_, _) =>
        {
            var has = SelectedEntry() != null;
            _contextMenu.Items[0].Enabled = has;
            _contextMenu.Items[1].Enabled = has;
            _contextMenu.Items[3].Enabled = has;
        };
        _list.ContextMenuStrip = _contextMenu;

        var delete = new Button { Text = "Delete", Width = 92, Height = 30 };
        var close = new Button { Text = "Close", Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
        delete.Click += (_, _) => DeleteSelected();
        close.Click += (_, _) => Close();

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10, 8, 10, 10),
            Margin = Padding.Empty
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var rightButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        rightButtons.Controls.Add(close);
        rightButtons.Controls.Add(delete);
        buttons.Controls.Add(rightButtons, 1, 0);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = Padding.Empty };
        bottom.Controls.Add(buttons);

        Controls.Add(_list);
        Controls.Add(bottom);
        AcceptButton = close;
        CancelButton = close;
        Resize += (_, _) => UpdateColumns();
        Shown += (_, _) => UpdateColumns();
        RefreshList();
    }

    private void UpdateColumns()
    {
        int width = Math.Max(420, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        _list.Columns[0].Width = Math.Max(180, width * 3 / 8);
        _list.Columns[1].Width = Math.Max(220, width - _list.Columns[0].Width);
    }

    private void SelectRowAtMouse(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item != null)
        {
            hit.Item.Selected = true;
            hit.Item.Focused = true;
        }
    }

    private BookmarkEntry? SelectedEntry()
    {
        if (_list.SelectedItems.Count == 0) return null;
        return _store.Items.FirstOrDefault(x => string.Equals(x.Url, _list.SelectedItems[0].Tag as string, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenSelected()
    {
        var entry = SelectedEntry();
        if (entry == null) return;
        Tag = entry.Url;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CopySelectedUrl()
    {
        var entry = SelectedEntry();
        if (entry == null) return;
        try { Clipboard.SetText(entry.Url); } catch { }
    }

    private void DeleteSelected()
    {
        var entry = SelectedEntry();
        if (entry == null) return;
        _store.Delete(entry.Url);
        RefreshList();
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var item in _store.Items)
            {
                var row = new ListViewItem(item.Title) { Tag = item.Url };
                row.SubItems.Add(item.Url);
                _list.Items.Add(row);
            }
        }
        finally { _list.EndUpdate(); }
    }
}

internal sealed class HistoryManagerDialog : Form
{
    private readonly HistoryStore _store;
    private readonly TextBox _search = new();
    private readonly ListView _list = new();
    private readonly ContextMenuStrip _contextMenu = new();

    public HistoryManagerDialog(HistoryStore store)
    {
        _store = store;
        Text = "Retro96 History";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 440);
        ClientSize = new Size(920, 620);
        AutoScaleMode = AutoScaleMode.Font;

        var searchBar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 6) };
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search title or URL";
        searchBar.Controls.Add(_search);
        _search.TextChanged += (_, _) => RefreshList();

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("Title", 300);
        _list.Columns.Add("URL", 430);
        _list.Columns.Add("Visited", 150);
        _list.MouseUp += SelectRowAtMouse;
        _list.DoubleClick += (_, _) => OpenSelected();

        _contextMenu.Items.Add("Open").Click += (_, _) => OpenSelected();
        _contextMenu.Items.Add("Copy URL").Click += (_, _) => CopySelectedUrl();
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Delete").Click += (_, _) => DeleteSelected();
        _contextMenu.Opening += (_, _) =>
        {
            var has = SelectedUrl() != null;
            _contextMenu.Items[0].Enabled = has;
            _contextMenu.Items[1].Enabled = has;
            _contextMenu.Items[3].Enabled = has;
        };
        _list.ContextMenuStrip = _contextMenu;

        var delete = new Button { Text = "Delete", Width = 90, Height = 30 };
        var clear = new Button { Text = "Clear All", Width = 90, Height = 30 };
        var close = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        delete.Click += (_, _) => DeleteSelected();
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Clear all history?", "Retro96", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _store.Clear();
                RefreshList();
            }
        };
        close.Click += (_, _) => Close();

        var rightButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        rightButtons.Controls.Add(close);
        rightButtons.Controls.Add(clear);
        rightButtons.Controls.Add(delete);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        bottom.Controls.Add(rightButtons);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(searchBar);
        AcceptButton = close;
        CancelButton = close;
        Resize += (_, _) => UpdateColumns();
        Shown += (_, _) => UpdateColumns();
        RefreshList();
    }

    private void UpdateColumns()
    {
        int width = Math.Max(520, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        int title = Math.Max(220, width * 3 / 8);
        int visited = 150;
        _list.Columns[0].Width = title;
        _list.Columns[1].Width = Math.Max(240, width - title - visited);
        _list.Columns[2].Width = visited;
    }

    private void SelectRowAtMouse(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item != null)
        {
            hit.Item.Selected = true;
            hit.Item.Focused = true;
        }
    }

    private string? SelectedUrl() => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as string;

    private void OpenSelected()
    {
        var url = SelectedUrl();
        if (url == null) return;
        Tag = url;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CopySelectedUrl()
    {
        var url = SelectedUrl();
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Clipboard.SetText(url); } catch { }
    }

    private void DeleteSelected()
    {
        var url = SelectedUrl();
        if (url == null) return;
        _store.Delete(url);
        RefreshList();
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var item in _store.Search(_search.Text))
            {
                var row = new ListViewItem(item.Title) { Tag = item.Url };
                row.SubItems.Add(item.Url);
                row.SubItems.Add(item.Visited.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                _list.Items.Add(row);
            }
        }
        finally { _list.EndUpdate(); }
    }
}
