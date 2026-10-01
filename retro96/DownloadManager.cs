using System.Drawing;
using System.Diagnostics;

namespace Retro96;

public enum DownloadStatus { Downloading, Complete, Failed, Cancelled }

public sealed class DownloadItem
{
    public string FileName { get; internal set; } = "download";
    public string Url { get; internal set; } = "";
    public string? FilePath { get; internal set; }
    public long? TotalBytes { get; internal set; }
    public long BytesDownloaded { get; internal set; }
    public DownloadStatus Status { get; internal set; } = DownloadStatus.Downloading;
    public string? Error { get; internal set; }
    internal CancellationTokenSource Cancellation { get; } = new();
    public double? Progress => TotalBytes is > 0 ? Math.Min(1d, (double)BytesDownloaded / TotalBytes.Value) : null;
    public string ProgressDisplay => Progress is double p ? $"{p:P0}" : "…";
}

internal sealed class DownloadManager : IDisposable
{
    private readonly object _sync = new();
    private readonly List<DownloadItem> _items = new();
    public IReadOnlyList<DownloadItem> Items { get { lock (_sync) return _items.ToArray(); } }
    public event EventHandler? Changed;

    public DownloadItem Start(string url, byte[] bytes, string suggestedName, string path)
    {
        var item = new DownloadItem { Url = url, FileName = Path.GetFileName(suggestedName), FilePath = path, TotalBytes = bytes.LongLength };
        lock (_sync) _items.Insert(0, item);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                const int chunk = 64 * 1024;
                for (int offset = 0; offset < bytes.Length; offset += chunk)
                {
                    item.Cancellation.Token.ThrowIfCancellationRequested();
                    int count = Math.Min(chunk, bytes.Length - offset);
                    await file.WriteAsync(bytes.AsMemory(offset, count), item.Cancellation.Token).ConfigureAwait(false);
                    item.BytesDownloaded += count;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                await file.FlushAsync(item.Cancellation.Token).ConfigureAwait(false);
                item.Status = DownloadStatus.Complete;
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Cancelled;
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed; item.Error = ex.Message;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        });
        return item;
    }

    public void Cancel(DownloadItem item)
    {
        try { item.Cancellation.Cancel(); } catch { }
    }

    public void Remove(DownloadItem item)
    {
        lock (_sync) _items.Remove(item);
        try { item.Cancellation.Dispose(); } catch { }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var item in Items) Cancel(item);
        lock (_sync) _items.Clear();
    }
}

internal sealed class DownloadsDialog : Form
{
    private readonly DownloadManager _manager;
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 250 };
    private readonly EventHandler _changedHandler;
    private readonly ContextMenuStrip _contextMenu = new();

    public DownloadsDialog(DownloadManager manager)
    {
        _manager = manager;
        Text = "Retro96 Downloads";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 460);
        ClientSize = new Size(980, 600);
        AutoScaleMode = AutoScaleMode.Font;

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.MultiSelect = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", DataPropertyName = nameof(DownloadItem.FileName), Width = 210 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "URL", DataPropertyName = nameof(DownloadItem.Url), Width = 380 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Progress",
            Width = 180,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DataPropertyName = nameof(DownloadItem.ProgressDisplay)
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Status",
            DataPropertyName = nameof(DownloadItem.Status),
            Width = 110,
            MinimumWidth = 88,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.CellDoubleClick += (_, _) => OpenSelected();

        // The main command bar already exposes Open. Keeping a second Open entry
        // here made the Downloads WinForm present the same action twice to users.
        _contextMenu.Items.Add("Open Folder").Click += (_, _) => OpenFolderSelected();
        _contextMenu.Items.Add("Copy URL").Click += (_, _) => CopySelectedUrl();
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Cancel").Click += (_, _) => CancelSelected();
        _contextMenu.Items.Add("Remove").Click += (_, _) => RemoveSelected();
        _contextMenu.Opening += (_, _) => RefreshContextMenuState();
        _grid.ContextMenuStrip = _contextMenu;
        _grid.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0 && hit.RowIndex < _grid.Rows.Count)
            {
                _grid.ClearSelection();
                _grid.Rows[hit.RowIndex].Selected = true;
                _grid.CurrentCell = _grid.Rows[hit.RowIndex].Cells[Math.Max(0, hit.ColumnIndex)];
            }
        };

        var close = new Button { Text = "Close", Width = 94, Height = 30 };
        var remove = new Button { Text = "Remove", Width = 94, Height = 30 };
        var open = new Button { Text = "Open", Width = 94, Height = 30 };
        var cancel = new Button { Text = "Cancel", Width = 94, Height = 30 };
        cancel.Click += (_, _) => CancelSelected();
        open.Click += (_, _) => OpenSelected();
        remove.Click += (_, _) => RemoveSelected();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 10),
            Margin = Padding.Empty
        };
        // Keep a single Open command in the bottom bar. Open Folder remains
        // available from the row context menu without creating a second
        // Open-style button beside it.
        buttons.Controls.Add(close);
        buttons.Controls.Add(remove);
        buttons.Controls.Add(open);
        buttons.Controls.Add(cancel);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = Padding.Empty };
        bottom.Controls.Add(buttons);

        Controls.Add(_grid);
        Controls.Add(bottom);
        close.Click += (_, _) => Close();

        _changedHandler = (_, _) =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(RefreshGrid)); } catch (InvalidOperationException) { }
            }
            else RefreshGrid();
        };
        _manager.Changed += _changedHandler;
        FormClosed += (_, _) => { _manager.Changed -= _changedHandler; _refresh.Stop(); _refresh.Dispose(); };
        _refresh.Tick += (_, _) => RefreshGrid();
        _refresh.Start();
        Resize += (_, _) => UpdateGridColumns();
        Shown += (_, _) => UpdateGridColumns();
        RefreshGrid();
    }

    private void UpdateGridColumns()
    {
        if (_grid.Columns.Count < 4 || WindowState == FormWindowState.Minimized || !_grid.IsHandleCreated)
            return;

        int available = _grid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (available <= 0) return;

        // The old Math.Max(520, ...) floor forced a wider four-column layout
        // into a temporarily tiny client area during minimize/narrow resize,
        // leaving Progress and Status overlapping until a later repaint. Keep
        // the fixed columns inside the actual client width and let Status fill
        // the right-hand remainder.
        int file = Math.Clamp(available / 4, 140, 230);
        int progress = Math.Clamp(available / 6, 104, 180);
        int status = Math.Clamp(available / 7, 88, 132);
        int url = available - file - progress - status;

        if (url < 80)
        {
            int deficit = 80 - url;
            int reducibleFile = Math.Max(0, file - 120);
            int take = Math.Min(deficit, reducibleFile);
            file -= take;
            deficit -= take;

            int reducibleProgress = Math.Max(0, progress - 92);
            take = Math.Min(deficit, reducibleProgress);
            progress -= take;
            deficit -= take;

            int reducibleStatus = Math.Max(0, status - 80);
            take = Math.Min(deficit, reducibleStatus);
            status -= take;
            deficit -= take;
            url = Math.Max(20, available - file - progress - status);
        }

        _grid.Columns[0].Width = file;
        _grid.Columns[1].Width = url;
        _grid.Columns[2].Width = progress;
        _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[3].MinimumWidth = Math.Min(status, Math.Max(1, available - file - progress));
        _grid.Columns[3].FillWeight = 1f;
    }

    private DownloadItem? SelectedItem() => _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as DownloadItem;

    private void RefreshContextMenuState()
    {
        var item = SelectedItem();
        bool has = item != null;
        bool hasPath = item != null && !string.IsNullOrWhiteSpace(item.FilePath);
        _contextMenu.Items[0].Enabled = hasPath; // Open Folder
        _contextMenu.Items[1].Enabled = has;     // Copy URL
        _contextMenu.Items[3].Enabled = item?.Status == DownloadStatus.Downloading; // Cancel
        _contextMenu.Items[4].Enabled = has && item?.Status != DownloadStatus.Downloading; // Remove
    }

    private void OpenSelected()
    {
        var item = SelectedItem();
        if (item?.Status != DownloadStatus.Complete || string.IsNullOrWhiteSpace(item.FilePath)) return;
        try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open Download", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenFolderSelected()
    {
        var item = SelectedItem();
        if (string.IsNullOrWhiteSpace(item?.FilePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.FilePath + "\"") { UseShellExecute = true }); }
        catch { }
    }

    private void CopySelectedUrl()
    {
        var item = SelectedItem();
        if (item == null) return;
        try { Clipboard.SetText(item.Url); } catch { }
    }

    private void CancelSelected()
    {
        var item = SelectedItem();
        if (item?.Status == DownloadStatus.Downloading) _manager.Cancel(item);
    }

    private void RemoveSelected()
    {
        var item = SelectedItem();
        if (item != null && item.Status != DownloadStatus.Downloading) _manager.Remove(item);
    }

    private void RefreshGrid()
    {
        if (IsDisposed || Disposing) return;
        var previous = SelectedItem();
        var items = _manager.Items;
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            foreach (var item in items)
            {
                int rowIndex = _grid.Rows.Add(item.FileName, item.Url, item.ProgressDisplay, item.Status.ToString());
                var row = _grid.Rows[rowIndex];
                row.Tag = item;
                row.Cells[3].ToolTipText = item.Error ?? string.Empty;
            }

            if (previous != null)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (ReferenceEquals(row.Tag, previous))
                    {
                        row.Selected = true;
                        _grid.CurrentCell = row.Cells[0];
                        break;
                    }
                }
            }
        }
        finally { _grid.ResumeLayout(); }
        _grid.InvalidateColumn(2);
        UpdateGridColumns();
    }
}
