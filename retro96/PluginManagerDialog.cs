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
    private readonly Button _loadUnpacked = new() { Text = "Load Unpacked" };
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

        foreach (var button in new[] { _add, _remove, _enable, _disable, _detailsButton, _permissions, _reload, _folder, _loadUnpacked, _close })
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
        _loadUnpacked.Click += (s, e) => LoadUnpacked();
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
        _loadUnpacked.Visible = _manager.IsDeveloperModeEnabled;
        _loadUnpacked.Enabled = _manager.IsDeveloperModeEnabled;
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
            using var review = new PluginInstallReviewDialog(permissions);
            if (review.ShowDialog(this) != DialogResult.OK) return;
            var record = _manager.InstallPackage(dialog.FileName, enableImmediately: false);
            PluginPermission newlyRequested = _manager.ConsumePendingNewPermissions(record.Manifest.Id);
            if (newlyRequested != PluginPermission.None)
            {
                using var permissionsDialog = new PluginPermissionsDialog(record, newlyRequested);
                if (permissionsDialog.ShowDialog(this) == DialogResult.OK)
                    _manager.SetGrantedPermissions(record.Manifest.Id, record.GrantedPermissions | permissionsDialog.GrantedPermissions);
            }
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

    private void LoadUnpacked()
    {
        if (!_manager.IsDeveloperModeEnabled) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select an unpacked Retro96 plugin folder containing plugin.json and lib/.",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var record = _manager.LoadUnpacked(dialog.SelectedPath);
            PluginPermission newlyRequested = _manager.ConsumePendingNewPermissions(record.Manifest.Id);
            if (newlyRequested != PluginPermission.None)
            {
                using var permissionsDialog = new PluginPermissionsDialog(record, newlyRequested);
                if (permissionsDialog.ShowDialog(this) == DialogResult.OK)
                    _manager.SetGrantedPermissions(record.Manifest.Id, record.GrantedPermissions | permissionsDialog.GrantedPermissions);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load Unpacked Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        using var dialog = new PluginDetailsDialog(_manager, record);
        dialog.ShowDialog(this);
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
    private readonly PluginPermission _visiblePermissions;
    private readonly bool _onlyNewPermissions;
    private readonly Dictionary<PluginPermission, CheckBox> _checks = new();
    private bool _changingCheck;
    public PluginPermission GrantedPermissions { get; private set; }

    public PluginPermissionsDialog(PluginManager.PluginRecord record, PluginPermission? visiblePermissions = null)
    {
        _record = record;
        _onlyNewPermissions = visiblePermissions.HasValue;
        _visiblePermissions = visiblePermissions ?? record.AvailablePermissions;
        GrantedPermissions = _onlyNewPermissions ? PluginPermission.None : record.GrantedPermissions;
        Text = "Plugin Permissions — " + record.Manifest.Name;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(560, 560);
        ClientSize = new Size(700, 700);

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Text = _onlyNewPermissions
                ? "This update requests new permissions. Existing grants are kept unchanged; only these newly requested capabilities can be added now."
                : "Grant only the capabilities this plugin actually needs. The host checks the user's granted set before every privileged broker call.",
            Padding = new Padding(12, 10, 12, 10)
        };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(12, 4, 12, 12),
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty
        };

        AddGroup(panel, PluginPermissionTier.Standard, "Standard");
        AddGroup(panel, PluginPermissionTier.Elevated, "Elevated");
        AddGroup(panel, PluginPermissionTier.Sensitive, "Sensitive — review carefully");

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(10, 8, 10, 8),
            Margin = Padding.Empty
        };
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
        AcceptButton = ok;
        CancelButton = cancel;

        var introWrap = new TableLayoutPanel
        { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        introWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        introWrap.Controls.Add(intro, 0, 0);

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

    private void AddGroup(FlowLayoutPanel parent, PluginPermissionTier tier, string title)
    {
        var permissions = PluginPermissionCatalog.All
            .Where(x => x.Tier == tier && _visiblePermissions.HasFlag(x.Permission))
            .ToArray();
        if (permissions.Length == 0) return;

        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 4)
        };
        parent.Controls.Add(label);
        foreach (var entry in permissions)
            AddPermissionRow(parent, entry);
    }

    private void AddPermissionRow(FlowLayoutPanel parent, PluginPermissionInfo entry)
    {
        var row = new TableLayoutPanel
        {
            Width = 620,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4),
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28f));
        var check = new CheckBox
        {
            Text = entry.Name,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Checked = !_onlyNewPermissions && _record.GrantedPermissions.HasFlag(entry.Permission),
            Margin = new Padding(0, 5, 0, 0)
        };
        check.CheckedChanged += (_, _) => OnPermissionChecked(entry, check);
        _checks[entry.Permission] = check;
        row.Controls.Add(check, 0, 0);
        var info = new PermissionInfoButton(entry.Name, (_, _) => PermissionInfoDialog.Show(this, entry.Permission));
        row.Controls.Add(info, 1, 0);
        parent.Controls.Add(row);
    }

    private void OnPermissionChecked(PluginPermissionInfo entry, CheckBox check)
    {
        if (_changingCheck || !check.Checked || entry.Tier != PluginPermissionTier.Sensitive) return;
        bool hasNetwork = _record.HasPermission(PluginPermission.Network) ||
                          (_checks.TryGetValue(PluginPermission.Network, out var network) && network.Checked);
        bool readsData = PluginPermissionCatalog.All
            .Where(x => x.IsDataReading && _checks.TryGetValue(x.Permission, out _))
            .Any(x => _checks[x.Permission].Checked || (entry.Permission == x.Permission && check.Checked));
        if (!PermissionConfirmationDialog.Confirm(this, entry, hasNetwork && readsData))
        {
            _changingCheck = true;
            check.Checked = false;
            _changingCheck = false;
        }
    }
}

internal sealed class PermissionInfoDialog : Form
{
    public static void Show(IWin32Window owner, PluginPermission permission)
    {
        using var dialog = new PermissionInfoDialog(permission);
        dialog.ShowDialog(owner);
    }

    private PermissionInfoDialog(PluginPermission permission)
    {
        PluginPermissionInfo info = PluginPermissionCatalog.Get(permission);
        Text = "About permission " + info.Name;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(420, 360);
        ClientSize = new Size(540, 460);

        var text = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(14),
            Text =
                $"{info.FriendlyName}\r\n" +
                $"Manifest name: {info.Name}\r\n" +
                $"Tier: {info.Tier}\r\n\r\n" +
                $"What it lets the plugin do\r\n{info.Description}\r\n\r\n" +
                $"Allows\r\n{info.Allows}\r\n\r\n" +
                $"Does not allow\r\n{info.DoesNotAllow}\r\n\r\n" +
                $"Risk note\r\n{info.RiskNote}",
            AutoEllipsis = false
        };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = Padding.Empty };
        scroll.Controls.Add(text);
        text.MinimumSize = new Size(480, 0);
        text.SizeChanged += (_, _) => text.Height = Math.Max(420, text.PreferredHeight);
        var close = new Button { Text = "Close", Width = 92, Height = 34, DialogResult = DialogResult.OK };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8), WrapContents = false };
        bottom.Controls.Add(close);
        Controls.Add(scroll);
        Controls.Add(bottom);
        AcceptButton = close;
    }
}

internal sealed class PermissionConfirmationDialog : Form
{
    public static bool Confirm(IWin32Window owner, PluginPermissionInfo info, bool dataAndNetwork)
    {
        string message =
            $"This plugin will gain the Sensitive permission '{info.Name}'.\r\n\r\n" +
            info.Description + "\r\n\r\n" +
            info.RiskNote;
        if (dataAndNetwork)
            message += "\r\n\r\nBecause this plugin also has network access, it could send that data to any site.";
        return MessageBox.Show(owner, message, "Sensitive Permission", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }
}

internal sealed class PluginInstallReviewDialog : Form
{
    public PluginInstallReviewDialog(IReadOnlyList<string> permissionNames)
    {
        Text = "Install Plugin";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(560, 520);
        ClientSize = new Size(700, 620);

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Padding = new Padding(12),
            Text = "Retro96 plugins run in a separate sandbox worker. Requested permissions are checked by the host broker. Plugin packages are not signed. Review the requested capabilities below before installing."
        };
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12), Margin = Padding.Empty };
        var permissions = permissionNames
            .Select(name => PluginPermissionNames.Parse(new[] { name }))
            .Where(p => p != PluginPermission.None)
            .Select(PluginPermissionCatalog.Get)
            .ToArray();
        foreach (var info in permissions)
        {
            var row = new TableLayoutPanel { Width = 620, Height = 58, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28f));
            row.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Text = $"{info.Name} — {info.Tier}\r\n{info.Description}",
                Padding = new Padding(0, 2, 0, 2)
            }, 0, 0);
            row.Controls.Add(new PermissionInfoButton(info.Name, (_, _) => PermissionInfoDialog.Show(this, info.Permission)), 1, 0);
            rows.Controls.Add(row);
        }

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10, 8, 10, 8), Margin = Padding.Empty };
        var install = new Button { Text = "Install", Width = 92, Height = 34 };
        var cancel = new Button { Text = "Cancel", Width = 92, Height = 34 };
        install.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(install);
        bottom.Controls.Add(cancel);
        AcceptButton = install;
        CancelButton = cancel;

        var introWrap = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 1 };
        introWrap.Controls.Add(intro, 0, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
        layout.Controls.Add(introWrap, 0, 0);
        layout.Controls.Add(rows, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        Controls.Add(layout);
    }
}


internal sealed class PluginDetailsDialog : Form
{
    public PluginDetailsDialog(PluginManager manager, PluginManager.PluginRecord record)
    {
        Text = "Plugin Details — " + record.Manifest.Name;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(660, 560);
        ClientSize = new Size(820, 700);

        var text = new StringBuilder();
        text.AppendLine($"Name: {record.Manifest.Name}");
        text.AppendLine($"ID: {record.Manifest.Id}");
        text.AppendLine($"Version: {record.Manifest.Version}");
        text.AppendLine($"Author: {record.Manifest.Author}");
        text.AppendLine($"API: {record.Manifest.ApiVersion}");
        text.AppendLine($"Status: {record.Status}");
        text.AppendLine($"Install date (UTC): {record.InstalledUtc:O}");
        text.AppendLine($"DLL SHA-256: {record.DllSha256}");
        text.AppendLine($"Last crash: {(record.LastCrashUtc.HasValue ? record.LastCrashUtc.Value.ToString("O") : "none")}");
        text.AppendLine($"Crash count in last 5 minutes: {record.CrashCountInWindow}");
        if (!string.IsNullOrWhiteSpace(record.LastCrashReason)) text.AppendLine($"Crash reason: {record.LastCrashReason}");
        text.AppendLine($"Entry point: {record.Manifest.EntryPoint}");
        text.AppendLine();
        text.AppendLine("Permissions");
        text.AppendLine("────────────────────────────────────────");
        foreach (var info in PluginPermissionCatalog.All.Where(x => record.RequestedPermissions.HasFlag(x.Permission)))
        {
            bool granted = record.GrantedPermissions.HasFlag(info.Permission);
            text.AppendLine($"{info.Name} [{info.Tier}] — {(granted ? "GRANTED" : "not granted")}");
            text.AppendLine("  " + info.Description);
        }

        text.AppendLine();
        text.AppendLine("Permission changes between versions");
        text.AppendLine("────────────────────────────────────────");
        if (record.PermissionChanges.Count == 0)
            text.AppendLine("None recorded.");
        foreach (var change in record.PermissionChanges)
        {
            string oldNames = string.Join(", ", PluginPermissionNames.ToNames(change.OldRequested));
            string newNames = string.Join(", ", PluginPermissionNames.ToNames(change.NewRequested));
            text.AppendLine($"{change.ChangedUtc:O}: {change.FromVersion} -> {change.ToVersion}");
            text.AppendLine($"  Requested: {(string.IsNullOrWhiteSpace(oldNames) ? "None" : oldNames)} -> {(string.IsNullOrWhiteSpace(newNames) ? "None" : newNames)}");
            text.AppendLine($"  Author changed: {(change.AuthorChanged ? "YES" : "no")}");
            text.AppendLine($"  DLL SHA-256: {change.OldDllSha256} -> {change.NewDllSha256}");
        }

        text.AppendLine();
        text.AppendLine("Activity summary");
        text.AppendLine("────────────────────────────────────────");
        var activity = manager.GetActivitySummary(record.Manifest.Id);
        if (activity.Count == 0)
            text.AppendLine("No brokered permission calls recorded.");
        foreach (var entry in activity)
        {
            string hosts = entry.NetworkHosts.Count == 0 ? "" : $"; hosts: {string.Join(", ", entry.NetworkHosts)}";
            text.AppendLine($"{entry.Permission}: {entry.Calls} calls; last used {entry.LastUsedUtc:O}{hosts}");
        }
        if (!string.IsNullOrWhiteSpace(record.Error))
        {
            text.AppendLine();
            text.AppendLine("Last error");
            text.AppendLine(record.Error);
        }

        var body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = text.ToString(),
            Margin = Padding.Empty,
            BackColor = SystemColors.Window
        };
        var close = new Button { Text = "Close", Width = 92, Height = 34 };
        var viewLog = new Button { Text = "View log", Width = 100, Height = 34 };
        close.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        viewLog.Click += (_, _) => ShowLog(manager, record);
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 8),
            Margin = Padding.Empty
        };
        bottom.Controls.Add(close);
        bottom.Controls.Add(viewLog);
        Controls.Add(body);
        Controls.Add(bottom);
        AcceptButton = close;
        CancelButton = close;
    }

    private static void ShowLog(PluginManager manager, PluginManager.PluginRecord record)
    {
        using var dialog = new Form { Text = "Plugin Log — " + record.Manifest.Name, StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi, MinimumSize = new Size(700, 460), ClientSize = new Size(900, 620) };
        var body = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = manager.GetPluginLog(record) };
        var close = new Button { Text = "Close", Width = 92, Height = 34, DialogResult = DialogResult.OK };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10, 8, 10, 8) };
        bottom.Controls.Add(close); dialog.Controls.Add(body); dialog.Controls.Add(bottom); dialog.AcceptButton = close; dialog.CancelButton = close;
        dialog.ShowDialog();
    }
}

internal sealed class PermissionInfoButton : Button
{
    private readonly ToolTip _toolTip = new();

    public PermissionInfoButton(string permissionName, EventHandler click)
    {
        AccessibleName = "About permission " + permissionName;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        Width = 24;
        Height = 24;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Text = string.Empty;
        UseVisualStyleBackColor = false;
        Margin = new Padding(2, 2, 0, 0);
        _toolTip.SetToolTip(this, "About permission " + permissionName);
        Click += click;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int d = Math.Max(10, Math.Min(ClientSize.Width, ClientSize.Height) - 6);
        int x = (ClientSize.Width - d) / 2;
        int y = (ClientSize.Height - d) / 2;
        using var pen = new Pen(SystemColors.WindowText, Math.Max(1f, DeviceDpi / 96f));
        using var brush = new SolidBrush(SystemColors.WindowText);
        e.Graphics.DrawEllipse(pen, x, y, d - 1, d - 1);
        using var font = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1.5f), FontStyle.Bold, GraphicsUnit.Point);
        var size = e.Graphics.MeasureString("i", font);
        e.Graphics.DrawString("i", font, brush, (ClientSize.Width - size.Width) / 2f, (ClientSize.Height - size.Height) / 2f - 1f);
    }
}
