using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Retro96.Plugins;

namespace Retro96;

/// <summary>Installed C# plugin manager. Kept intentionally compact and utilitarian.</summary>
public sealed class PluginManagerDialog : Form
{
    private readonly PluginManager _manager;
    private readonly ListView _list = new();
    private readonly Label _details = new();
    private readonly Button _add = new() { Text = "Add..." };
    private readonly Button _remove = new() { Text = "Delete" };
    private readonly Button _enable = new() { Text = "Enable" };
    private readonly Button _disable = new() { Text = "Disable" };
    private readonly Button _detailsButton = new() { Text = "Details..." };
    private readonly Button _permissions = new() { Text = "Permissions..." };
    private readonly Button _reload = new() { Text = "Reload" };
    private readonly Button _folder = new() { Text = "Open Folder" };
    private readonly Button _close = new() { Text = "Close" };
    private readonly ContextMenuStrip _contextMenu = new();

    public PluginManagerDialog(PluginManager manager)
    {
        _manager = manager;

        Text = "Retro96 Plugin Addons";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 600);
        ClientSize = new Size(980, 620);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = false;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 88,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14, 4, 14, 6),
            Margin = Padding.Empty
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Text = "Plugin Addons",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        var subtitle = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Text = "C# .r96p extensions installed for Retro96",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Margin = Padding.Empty;
        _list.Columns.Add("Plugin", 210);
        _list.Columns.Add("Version", 82);
        _list.Columns.Add("Status", 90);
        _list.Columns.Add("Permissions", 220);
        _list.SelectedIndexChanged += (s, e) => RefreshButtons();
        _list.DoubleClick += (s, e) => ShowDetails();
        _list.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null)
            {
                hit.Item.Selected = true;
                hit.Item.Focused = true;
            }
        };
        _contextMenu.Items.Add("Enable").Click += (_, _) => SetEnabled(true);
        _contextMenu.Items.Add("Disable").Click += (_, _) => SetEnabled(false);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Details...").Click += (_, _) => ShowDetails();
        _contextMenu.Items.Add("Permissions...").Click += (_, _) => EditPermissions();
        _contextMenu.Items.Add("Reload").Click += (_, _) => ReloadPlugin();
        _contextMenu.Items.Add("Open Folder").Click += (_, _) => OpenFolder();
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Delete").Click += (_, _) => DeletePlugin();
        _contextMenu.Opening += (_, _) => RefreshContextMenuState();
        _list.ContextMenuStrip = _contextMenu;

        _details.Dock = DockStyle.Fill;
        _details.BorderStyle = BorderStyle.FixedSingle;
        _details.Padding = new Padding(8, 6, 8, 6);
        _details.AutoEllipsis = true;
        _details.AutoSize = false;
        _details.Margin = new Padding(0, 8, 0, 0);
        _details.TextAlign = ContentAlignment.TopLeft;

        var center = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10, 0, 10, 10),
            Margin = Padding.Empty
        };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));
        center.Controls.Add(_list, 0, 0);
        center.Controls.Add(_details, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 0, 10, 10),
            Margin = Padding.Empty
        };

        foreach (var button in new[] { _add, _remove, _enable, _disable, _detailsButton, _permissions, _reload, _folder, _close })
        {
            button.AutoSize = false;
            button.Width = 164;
            button.Height = 32;
            button.Margin = new Padding(0, 0, 0, 8);
            actions.Controls.Add(button);
        }

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190f));
        body.Controls.Add(center, 0, 0);
        body.Controls.Add(actions, 1, 0);

        Controls.Add(body);
        Controls.Add(header);

        _add.Click += (s, e) => AddPlugin();
        _remove.Click += (s, e) => DeletePlugin();
        _enable.Click += (s, e) => SetEnabled(true);
        _disable.Click += (s, e) => SetEnabled(false);
        _detailsButton.Click += (s, e) => ShowDetails();
        _permissions.Click += (s, e) => EditPermissions();
        _reload.Click += (s, e) => ReloadPlugin();
        _folder.Click += (s, e) => OpenFolder();
        _close.Click += (s, e) => Close();

        body.Resize += (s, e) => UpdateListColumnWidths();
        Shown += (s, e) => UpdateListColumnWidths();

        _manager.PluginsChanged += ManagerOnPluginsChanged;
        FormClosed += (s, e) => _manager.PluginsChanged -= ManagerOnPluginsChanged;
        RefreshList();
    }

    private void UpdateListColumnWidths()
    {
        if (_list.Columns.Count < 4) return;

        int available = Math.Max(420, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
        int plugin = Math.Min(240, Math.Max(150, (int)(available * 0.28f)));
        int version = 84;
        int status = 94;
        int permissions = Math.Max(150, available - plugin - version - status);

        _list.Columns[0].Width = plugin;
        _list.Columns[1].Width = version;
        _list.Columns[2].Width = status;
        _list.Columns[3].Width = permissions;
    }

    private void RefreshContextMenuState()
    {
        var record = SelectedRecord();
        bool has = record != null;
        _contextMenu.Items[0].Enabled = has && !record!.Enabled;
        _contextMenu.Items[1].Enabled = has && record!.Enabled;
        _contextMenu.Items[3].Enabled = has;
        _contextMenu.Items[4].Enabled = has;
        _contextMenu.Items[5].Enabled = has && record!.Enabled;
        _contextMenu.Items[6].Enabled = has;
        _contextMenu.Items[8].Enabled = has;
    }

    private void ManagerOnPluginsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(RefreshList); return; }
        RefreshList();
    }

    private void RefreshList()
    {
        string? selected = SelectedId();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var record in _manager.Plugins)
            {
                string permissions = string.Join(", ", PluginPermissionNames.ToNames(record.GrantedPermissions));
                string status = record.Status;
                if (record.Enabled && record.Status == "Installed") status = "Enabled";
                if (!record.Enabled) status = "Disabled";
                var item = new ListViewItem(record.Manifest.Name) { Tag = record.Manifest.Id };
                item.SubItems.Add(record.Manifest.Version);
                item.SubItems.Add(status);
                item.SubItems.Add(permissions.Length == 0 ? "None" : permissions);
                _list.Items.Add(item);
            }
        }
        finally { _list.EndUpdate(); }

        if (selected != null)
        {
            foreach (ListViewItem item in _list.Items)
                if (string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase))
                    item.Selected = true;
        }
        RefreshButtons();
    }

    private string? SelectedId() => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as string;
    private PluginManager.PluginRecord? SelectedRecord() => SelectedId() is string id ? _manager.Plugins.FirstOrDefault(p => p.Manifest.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;

    private void RefreshButtons()
    {
        var record = SelectedRecord();
        bool has = record != null;
        _remove.Enabled = has;
        _enable.Enabled = has && !record!.Enabled;
        _disable.Enabled = has && record!.Enabled;
        _detailsButton.Enabled = has;
        _permissions.Enabled = has;
        _reload.Enabled = has && record!.Enabled;
        _folder.Enabled = has;
        _close.Enabled = true;
        if (record == null)
        {
            _details.Text = "No plugin selected.";
            return;
        }

        var sb = new StringBuilder();
        sb.Append(record.Manifest.Description);
        if (!string.IsNullOrWhiteSpace(record.Error)) sb.AppendLine().Append("Error: ").Append(record.Error);
        _details.Text = sb.ToString();
    }

    private void AddPlugin()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Install Retro96 Plugin",
            Filter = "Retro96 plugins (*.r96p)|*.r96p|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var permissions = ReadRequestedPermissions(dialog.FileName);
            var warning = "Retro96 plugins are compiled C# extensions that run in a separate Windows sandbox worker.\r\n" +
                          "Requested permissions are checked by the Retro96 host broker before privileged API calls.\r\n\r\n" +
                          "Requested permissions: " + (permissions.Count == 0 ? "None" : string.Join(", ", permissions)) + "\r\n\r\nThe plugin will be installed disabled. Grant permissions in Permissions... and then Enable it.";
            if (MessageBox.Show(this, warning, "Install Plugin", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            _manager.InstallPackage(dialog.FileName, enableImmediately: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Plugin Installation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static IReadOnlyList<string> ReadRequestedPermissions(string packagePath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("plugin.json");
        if (entry == null) return Array.Empty<string>();
        using var stream = entry.Open();
        var manifest = System.Text.Json.JsonSerializer.Deserialize(stream, PluginManifestJsonContext.Default.PluginManifest);
        return manifest?.Permissions ?? new List<string>();
    }

    private void DeletePlugin()
    {
        var record = SelectedRecord();
        if (record == null) return;
        if (MessageBox.Show(this, $"Delete '{record.Manifest.Name}' and its stored plugin data?", "Delete Plugin", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        try { _manager.Remove(record.Manifest.Id); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Delete Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SetEnabled(bool enabled)
    {
        var record = SelectedRecord();
        if (record == null) return;
        _manager.SetEnabled(record.Manifest.Id, enabled);
    }

    private void ReloadPlugin()
    {
        var record = SelectedRecord();
        if (record == null) return;
        _manager.Reload(record.Manifest.Id);
    }

    private void OpenFolder()
    {
        var record = SelectedRecord();
        if (record == null) return;
        try { _manager.OpenFolder(record.Manifest.Id); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open Folder Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ShowDetails()
    {
        var record = SelectedRecord();
        if (record == null) return;
        string requested = string.Join(", ", PluginPermissionNames.ToNames(record.RequestedPermissions));
        string granted = string.Join(", ", PluginPermissionNames.ToNames(record.GrantedPermissions));
        string text =
            $"Name: {record.Manifest.Name}\r\n" +
            $"ID: {record.Manifest.Id}\r\n" +
            $"Version: {record.Manifest.Version}\r\n" +
            $"Author: {record.Manifest.Author}\r\n" +
            $"API: {record.Manifest.ApiVersion}\r\n" +
            $"Entry: {record.Manifest.EntryPoint}\r\n" +
            $"Requested: {(string.IsNullOrWhiteSpace(requested) ? "None" : requested)}\r\n" +
            $"Granted: {(string.IsNullOrWhiteSpace(granted) ? "None" : granted)}\r\n" +
            $"Folder: {record.Directory}";
        MessageBox.Show(this, text, "Plugin Details", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void EditPermissions()
    {
        var record = SelectedRecord();
        if (record == null) return;
        using var dialog = new PluginPermissionsDialog(record);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _manager.SetGrantedPermissions(record.Manifest.Id, dialog.GrantedPermissions);
    }
}

internal sealed class PluginPermissionsDialog : Form
{
    private readonly PluginManager.PluginRecord _record;
    private readonly Dictionary<PluginPermission, CheckBox> _checks = new();
    public PluginPermission GrantedPermissions { get; private set; }

    public PluginPermissionsDialog(PluginManager.PluginRecord record)
    {
        _record = record;
        GrantedPermissions = record.GrantedPermissions;
        Text = "Plugin Permissions — " + record.Manifest.Name;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(520, 520);
        ClientSize = new Size(640, 640);

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(0, 0),
            Text = "Grant only the capabilities this plugin actually needs. The host checks the user's granted set before every privileged broker call.",
            Padding = new Padding(12, 10, 12, 10)
        };

        var introWrap = new TableLayoutPanel
        { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        introWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        introWrap.Controls.Add(intro, 0, 0);

        var panel = new FlowLayoutPanel
        { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 4, 12, 12), WrapContents = false, AutoScroll = true, Margin = Padding.Empty };

        foreach (var permission in Enum.GetValues<PluginPermission>().Where(p => p != PluginPermission.None))
        {
            if (!record.RequestedPermissions.HasFlag(permission)) continue;
            AddPermissionRow(panel, permission);
        }

        var bottom = new FlowLayoutPanel
        { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Padding = new Padding(10, 8, 10, 8), Margin = Padding.Empty };
        var ok = new Button { Text = "OK", Width = 92, Height = 34, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new Button { Text = "Cancel", Width = 92, Height = 34, Margin = new Padding(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            PluginPermission value = PluginPermission.None;
            foreach (var pair in _checks) if (pair.Value.Checked) value |= pair.Key;
            GrantedPermissions = value;
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        var layout = new TableLayoutPanel
        { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
        layout.Controls.Add(introWrap, 0, 0);
        layout.Controls.Add(panel, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        Controls.Add(layout);
    }

    private void AddPermissionRow(FlowLayoutPanel panel, PluginPermission permission)
    {
        string name = PluginPermissionNames.ToNames(permission).FirstOrDefault() ?? permission.ToString();
        var row = new Panel
        { Width = 580, Height = 34, Margin = new Padding(0, 0, 0, 4), Padding = Padding.Empty };
        var check = new CheckBox
        { Text = name, AutoSize = true, Dock = DockStyle.Fill, Checked = _record.GrantedPermissions.HasFlag(permission), Margin = new Padding(0, 5, 0, 0) };
        _checks[permission] = check;
        row.Controls.Add(check);
        panel.Controls.Add(row);
    }
}
