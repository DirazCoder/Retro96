using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Retro96.Plugins;

/// <summary>
/// Owns installed .r96p plugins, validates packages, loads C# entry points,
/// and exposes the permission-gated host API.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private const string PackageExtension = ".r96p";
    private readonly Form1 _browser;
    private readonly string _rootDirectory;
    private readonly string _stateFile;
    private readonly Dictionary<string, PluginRecord> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private bool _disposed;

    public PluginManager(Form1 browser)
    {
        _browser = browser;
        _rootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Retro96", "Plugins");
        _stateFile = Path.Combine(_rootDirectory, "plugin-state.json");
        Directory.CreateDirectory(_rootDirectory);
        LoadState();
        if (_browser.IsHandleCreated)
            LoadEnabledPlugins();
        else
            _browser.HandleCreated += (_, _) => LoadEnabledPlugins();
    }

    public IReadOnlyList<PluginRecord> Plugins => _plugins.Values.OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public event EventHandler? PluginsChanged;

    public PluginRecord InstallPackage(string packagePath, bool enableImmediately = false)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Plugin package not found.", packagePath);
        if (!packagePath.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Retro96 plugins must use the .r96p extension.");

        PluginManifest manifest = ReadManifestFromPackage(packagePath);
        ValidateManifest(manifest);

        if (_plugins.ContainsKey(manifest.Id))
            throw new InvalidOperationException($"A plugin with id '{manifest.Id}' is already installed.");

        string destination = GetPluginDirectory(manifest.Id);
        string temp = destination + ".installing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            ExtractPackageSafely(packagePath, temp);
            var installedManifest = LoadManifest(Path.Combine(temp, "plugin.json"));
            ValidateManifest(installedManifest);
            if (!installedManifest.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package manifest changed during installation.");

            ValidateEntryAssembly(temp, installedManifest);
            Directory.Move(temp, destination);

            var record = new PluginRecord
            {
                Manifest = installedManifest,
                Directory = destination,
                Enabled = enableImmediately,
                GrantedPermissions = PluginPermission.None
            };
            _plugins.Add(record.Manifest.Id, record);
            SaveState();
            if (record.Enabled)
            {
                TryLoad(record);
            }
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            return record;
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }
    }

    public void Remove(string id)
    {
        if (!_plugins.TryGetValue(id, out var record)) return;
        DisableRecord(record);
        _plugins.Remove(id);
        SaveState();
        TryDeleteDirectory(record.Directory);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (!_plugins.TryGetValue(id, out var record)) return;
        if (record.Enabled == enabled) return;
        record.Enabled = enabled;
        if (enabled) TryLoad(record);
        else DisableRecord(record);
        SaveState();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetGrantedPermissions(string id, PluginPermission permissions)
    {
        if (!_plugins.TryGetValue(id, out var record)) return;
        PluginPermission sanitized = permissions & record.Manifest.RequestedPermissions;
        record.GrantedPermissions = sanitized;
        if (record.Enabled)
        {
            DisableRecord(record);
            TryLoad(record);
        }
        SaveState();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reload(string id)
    {
        if (!_plugins.TryGetValue(id, out var record)) return;
        DisableRecord(record);
        if (record.Enabled) TryLoad(record);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenFolder(string id)
    {
        if (!_plugins.TryGetValue(id, out var record)) return;
        Directory.CreateDirectory(record.Directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = record.Directory,
            UseShellExecute = true
        });
    }

    public void OpenManager(Form? owner)
    {
        using var dialog = new PluginManagerDialog(this);
        dialog.ShowDialog(owner);
    }

    internal IDisposable RegisterFileMenuItem(PluginRecord record, string text, Action callback)
    {
        if (!record.HasPermission(PluginPermission.UserInterface))
            throw new SecurityException("Plugin permission 'ui' has not been granted.");
        return _browser.AddPluginFileMenuItem(record.Manifest.Id, text, callback);
    }

    internal void SetStatus(PluginRecord record, string text)
    {
        if (!record.HasPermission(PluginPermission.UserInterface))
            throw new SecurityException("Plugin permission 'ui' has not been granted.");
        _browser.SetPluginStatus(record.Manifest.Id, text);
    }

    internal void RaiseNavigation(string url)
    {
        foreach (var record in _plugins.Values.ToArray())
        {
            if (!record.Enabled || record.Sandbox == null || !record.HasPermission(PluginPermission.BrowserEvents)) continue;
            record.Sandbox.RaiseNavigated(url);
        }
    }

    internal void RaisePageLoaded(string url, string title)
    {
        foreach (var record in _plugins.Values.ToArray())
        {
            if (!record.Enabled || record.Sandbox == null || !record.HasPermission(PluginPermission.BrowserEvents)) continue;
            record.Sandbox.RaisePageLoaded(url, title);
        }
    }

    internal void RaiseHostShuttingDown()
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.BrowserEvents))
                record.Sandbox.RaiseHostShuttingDown();
    }

    internal void RaiseWindowFocusChanged(bool hasFocus)
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.BrowserEvents))
                record.Sandbox.RaiseWindowFocusChanged(hasFocus);
    }

    internal void RaiseClipboardChanged()
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.Clipboard))
                record.Sandbox.RaiseClipboardChanged();
    }

    internal void RaiseAudioComplete()
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.AudioPlayback))
                record.Sandbox.RaiseAudioComplete();
    }

    internal void PopulateContextMenu(ContextMenuStrip menu, ContextMenuContext context)
    {
        _browser.PopulatePluginContextMenu(menu, context);
    }

    private void LoadEnabledPlugins()
    {
        foreach (var record in _plugins.Values.ToArray())
        {
            if (record.Enabled) TryLoad(record);
        }
    }

    private void TryLoad(PluginRecord record)
    {
        ValidateEntryAssembly(record.Directory, record.Manifest);
        record.Status = "Loading (sandboxed)";
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        _ = LoadSandboxAsync(record);
    }

    private async Task LoadSandboxAsync(PluginRecord record)
    {
        var sandbox = new PluginSandboxSession(_browser, record);
        record.Sandbox = sandbox;
        try
        {
            await sandbox.StartAsync().ConfigureAwait(false);
            if (!record.Enabled || !ReferenceEquals(record.Sandbox, sandbox))
            {
                sandbox.Dispose();
                return;
            }
            record.Status = "Loaded (sandboxed)";
            record.Error = null;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(record.Sandbox, sandbox))
            {
                try { sandbox.Dispose(); } catch { }
                record.Sandbox = null;
                _browser.RemovePluginFileMenuItems(record.Manifest.Id);
                record.Status = "Error";
                record.Error = ex.Message;
            }
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ValidateEntryAssembly(string root, PluginManifest manifest)
    {
        string assemblyPath = Path.GetFullPath(Path.Combine(root, manifest.Assembly));
        string rootFull = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        if (!assemblyPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin assembly path escapes the package directory.");
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Plugin assembly not found.", assemblyPath);
    }

    private static PluginManifest ReadManifestFromPackage(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("plugin.json") ?? archive.Entries.FirstOrDefault(e => e.FullName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new InvalidDataException("Package is missing plugin.json.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize(stream, PluginManifestJsonContext.Default.PluginManifest)
               ?? throw new InvalidDataException("plugin.json is empty or invalid.");
    }

    private PluginManifest LoadManifest(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), PluginManifestJsonContext.Default.PluginManifest)
        ?? throw new InvalidDataException("plugin.json is empty or invalid.");

    private static void ValidatePermissions(IEnumerable<string>? permissions)
    {
        var known = new HashSet<string>(PluginPermissionNames.ToNames(
            Enum.GetValues<PluginPermission>()
                .Where(p => p != PluginPermission.None)
                .Aggregate(PluginPermission.None, (value, p) => value | p)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string raw in permissions ?? Array.Empty<string>())
        {
            string name = (raw ?? string.Empty).Trim();
            if (!known.Contains(name))
                throw new InvalidDataException($"Unknown plugin permission '{name}'.");
        }
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("plugin.json requires id and name.");
        if (manifest.ApiVersion != Retro96PluginApi.ApiVersion)
            throw new InvalidDataException($"Unsupported plugin API version {manifest.ApiVersion}; this host supports {Retro96PluginApi.ApiVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Assembly) || string.IsNullOrWhiteSpace(manifest.EntryPoint))
            throw new InvalidDataException("plugin.json requires assembly and entryPoint.");
        if (!manifest.Id.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new InvalidDataException("Plugin id may contain only letters, digits, '.', '-' and '_'.");
        ValidatePermissions(manifest.Permissions);
    }

    private static void ExtractPackageSafely(string packagePath, string destination)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
            string full = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin package contains a path traversal entry.");

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
        }
    }

    private string GetPluginDirectory(string id) => Path.Combine(_rootDirectory, id);

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var saved = JsonSerializer.Deserialize<List<PluginState>>(File.ReadAllText(_stateFile), _jsonOptions) ?? new();
            foreach (var state in saved)
            {
                string dir = GetPluginDirectory(state.Id);
                string manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = LoadManifest(manifestPath);
                    var requested = manifest.RequestedPermissions;
                    _plugins[state.Id] = new PluginRecord
                    {
                        Manifest = manifest,
                        Directory = dir,
                        Enabled = state.Enabled,
                        GrantedPermissions = requested & (PluginPermission)state.GrantedPermissions
                    };
                }
                catch { /* a broken installed plugin should not prevent startup */ }
            }
        }
        catch { }
    }

    private void SaveState()
    {
        Directory.CreateDirectory(_rootDirectory);
        var state = _plugins.Values.Select(p => new PluginState
        {
            Id = p.Manifest.Id,
            Enabled = p.Enabled,
            GrantedPermissions = (long)p.GrantedPermissions
        }).ToList();
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, _jsonOptions));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static void DisableRuntimeOnly(PluginRecord record)
    {
        try { record.Sandbox?.Dispose(); } catch { }
        record.Sandbox = null;
    }

    private void DisableRecord(PluginRecord record)
    {
        DisableRuntimeOnly(record);
        _browser.RemovePluginFileMenuItems(record.Manifest.Id);
        record.Status = "Disabled";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var record in _plugins.Values.ToArray()) DisableRecord(record);
        _plugins.Clear();
    }

    private sealed class PluginState
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; }
        public long GrantedPermissions { get; set; }
    }

    public sealed class PluginRecord
    {
        internal PluginRecord() { }
        public PluginManifest Manifest { get; internal set; } = new();
        public string Directory { get; internal set; } = "";
        public bool Enabled { get; internal set; }
        public PluginPermission GrantedPermissions { get; internal set; }
        public string Status { get; internal set; } = "Installed";
        public string? Error { get; internal set; }
        internal PluginSandboxSession? Sandbox;
        public PluginPermission RequestedPermissions => Manifest.RequestedPermissions;
        public bool HasPermission(PluginPermission permission) => (GrantedPermissions & permission) == permission;
    }
}

