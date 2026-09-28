using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Retro96.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;
using Retro96.Engine.Js;

namespace Retro96.Plugins;

/// <summary>
/// Owns installed .r96p plugins, validates packages, loads C# entry points,
/// and exposes the permission-gated host API.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private const string PackageExtension = ".r96p";
    private const int MaxPermissionHistoryEntries = 32;
    private const int MaxPluginCrashesInWindow = 3;
    private static readonly TimeSpan PluginCrashWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] PluginRestartBackoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };
    private readonly Form1 _browser;
    private readonly string _rootDirectory;
    private readonly string _stateFile;
    private readonly Dictionary<string, PluginRecord> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private bool _disposed;
    private readonly Dictionary<DomElement, EmbeddedRuntime> _embedded = new();
    private readonly object _embedLock = new();
    private readonly System.Windows.Forms.Timer _embedRenderTimer;
    private readonly Dictionary<string, (PluginRecord Record, string Token)> _pluginProtocols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PluginRecord Record, string Token)> _pluginOmnibox = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(Form1 browser)
    {
        _browser = browser;
        _rootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Retro96", "Plugins");
        _stateFile = Path.Combine(_rootDirectory, "plugin-state.json");
        Directory.CreateDirectory(_rootDirectory);
        _browser.PluginCanvas.EmbeddedFrameResolver = ResolveEmbeddedFrame;
        _browser.PluginCanvas.EmbeddedInputDispatcher = DispatchEmbeddedInput;
        _browser.PluginCanvas.EmbeddedScriptInfoResolver = GetEmbeddedScriptInfo;
        _browser.PluginCanvas.EmbeddedScriptCall = CallEmbeddedScriptAsync;
        _browser.PluginCanvas.PageChanged += OnPageChanged;
        LoadState();
        if (_browser.IsHandleCreated)
            LoadEnabledPlugins();
        else
            _browser.HandleCreated += (_, _) => LoadEnabledPlugins();

        // Drive embedded content repaints at ~30 fps. The pull-based render
        // model (ResolveEmbeddedFrame returns LastFrame) means the host only
        // shows whatever was cached at the last RequestEmbeddedRender call.
        // Without this timer the frame never updates after the initial render.
        _embedRenderTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _embedRenderTimer.Tick += OnEmbedRenderTick;
        _embedRenderTimer.Start();
    }

    public IReadOnlyList<PluginRecord> Plugins => _plugins.Values.OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    internal string HostVersion => Application.ProductVersion;
    internal bool IsDeveloperModeEnabled => _browser.PluginDevMode;

    public event EventHandler? PluginsChanged;

    internal bool IsSupportedCapability(string name)
    {
        return name switch
        {
            "host.info" or "browser" or "ui" or "storage" or "network" or "filesystem" or
            "clipboard" or "events" or "audio" or "notifications" or "dialogs" or "embeds" or "logger" or "permissions" or "page.read" or "network.rules" or "protocol" or "content.transform" or "page.style" or "tabs" or "history" or "bookmarks" or "downloads" or "omnibox" or "settings" or "ui.extras" or "embed.audio" or "embed.extras" or "browser" => true,
            _ => false
        };
    }

    public PluginRecord InstallPackage(string packagePath, bool enableImmediately = false)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Plugin package not found.", packagePath);
        if (!packagePath.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Retro96 plugins must use the .r96p extension.");

        PluginManifest manifest = ReadManifestFromPackage(packagePath);
        ValidateManifest(manifest);

        _plugins.TryGetValue(manifest.Id, out var existing);
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
            string newHash = ComputeDllSha256(temp, installedManifest);

            if (existing == null)
            {
                Directory.Move(temp, destination);
                var record = new PluginRecord
                {
                    Manifest = installedManifest,
                    Directory = destination,
                    Enabled = enableImmediately,
                    GrantedPermissions = PluginPermission.None,
                    InstalledUtc = DateTimeOffset.UtcNow,
                    DllSha256 = newHash,
                    ActivityPersistence = SaveState
                };
                _plugins.Add(record.Manifest.Id, record);
                SaveState();
                if (record.Enabled) TryLoad(record);
                PluginsChanged?.Invoke(this, EventArgs.Empty);
                return record;
            }

            string oldHash = existing.DllSha256;
            if (string.IsNullOrWhiteSpace(oldHash))
            {
                try { oldHash = ComputeDllSha256(existing.Directory, existing.Manifest); }
                catch { oldHash = "unavailable"; }
            }

            int versionComparison = ComparePluginVersions(installedManifest.Version, existing.Manifest.Version);
            bool authorChanged = !string.Equals(existing.Manifest.Author?.Trim(), installedManifest.Author?.Trim(), StringComparison.Ordinal);
            string oldVersion = existing.Manifest.Version;
            string oldAuthor = existing.Manifest.Author;
            string updateMessage =
                $"Retro96 will replace the installed files for '{existing.Manifest.Name}'.\r\n\r\n" +
                $"Version: {existing.Manifest.Version} -> {installedManifest.Version}\r\n" +
                $"Old DLL SHA-256: {oldHash}\r\n" +
                $"New DLL SHA-256: {newHash}\r\n\r\n" +
                (authorChanged
                    ? $"WARNING: the manifest author changed:\r\n  {existing.Manifest.Author}\r\n  -> {installedManifest.Author}\r\n\r\n"
                    : "") +
                "Plugin packages are not signed. This confirmation is only a safety speed bump, not authentication.\r\n\r\n";

            if (versionComparison < 0)
                updateMessage = "WARNING: this package is a DOWNGRADE.\r\n\r\n" + updateMessage;
            else if (versionComparison == 0)
                updateMessage = "The package has the same version as the installed plugin and will replace its files.\r\n\r\n" + updateMessage;

            if (MessageBox.Show(_browser, updateMessage, "Update Plugin", MessageBoxButtons.OKCancel,
                    authorChanged || versionComparison < 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information) != DialogResult.OK)
                return existing;

            bool wasEnabled = existing.Enabled;
            PluginPermission oldRequested = existing.RequestedPermissions;
            PluginPermission oldGranted = existing.GrantedPermissions;
            DisableRecord(existing);

            string backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.Move(destination, backup);
                Directory.Move(temp, destination);
                temp = string.Empty;

                string oldData = Path.Combine(backup, "data");
                string newData = Path.Combine(destination, "data");
                if (Directory.Exists(oldData))
                {
                    TryDeleteDirectory(newData);
                    Directory.Move(oldData, newData);
                }

                TryDeleteDirectory(backup);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (Directory.Exists(backup))
                    Directory.Move(backup, destination);
                throw;
            }

            existing.Manifest = installedManifest;
            existing.DllSha256 = newHash;
            existing.GrantedPermissions = oldGranted & installedManifest.AvailablePermissions;
            existing.PendingNewPermissions = installedManifest.RequestedPermissions & ~oldRequested;
            existing.PermissionChanges.Insert(0, new PluginPermissionVersionChange
            {
                ChangedUtc = DateTimeOffset.UtcNow,
                FromVersion = oldVersion,
                ToVersion = installedManifest.Version,
                OldRequested = oldRequested,
                NewRequested = installedManifest.RequestedPermissions,
                OldGranted = oldGranted,
                NewGranted = existing.GrantedPermissions,
                AuthorChanged = authorChanged,
                OldDllSha256 = oldHash,
                NewDllSha256 = newHash
            });
            if (existing.PermissionChanges.Count > MaxPermissionHistoryEntries)
                existing.PermissionChanges.RemoveRange(MaxPermissionHistoryEntries, existing.PermissionChanges.Count - MaxPermissionHistoryEntries);

            existing.Error = null;
            existing.Status = wasEnabled ? "Loading (sandboxed)" : "Disabled";
            SaveState();
            if (wasEnabled) TryLoad(existing);
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            return existing;
        }
        catch
        {
            if (!string.IsNullOrEmpty(temp)) TryDeleteDirectory(temp);
            throw;
        }
    }

    public PluginRecord LoadUnpacked(string sourceDirectory, bool enableImmediately = true)
    {
        if (!_browser.PluginDevMode)
            throw new InvalidOperationException("Developer mode is disabled. Enable it in Preferences → Plugins first.");
        string source = Path.GetFullPath(sourceDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string manifestPath = Path.Combine(source, "plugin.json");
        string libPath = Path.Combine(source, "lib");
        if (!Directory.Exists(source) || !File.Exists(manifestPath) || !Directory.Exists(libPath))
            throw new InvalidDataException("Load Unpacked requires a folder containing plugin.json and lib/.");
        PluginManifest manifest = LoadManifest(manifestPath);
        ValidateManifest(manifest);
        ValidateEntryAssembly(source, manifest);

        string destination = GetPluginDirectory(manifest.Id);
        string temp = destination + ".unpacked-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            CopyDirectory(source, temp, skipGit: true);
            var copied = LoadManifest(Path.Combine(temp, "plugin.json"));
            ValidateManifest(copied);
            ValidateEntryAssembly(temp, copied);
            string hash = ComputeDllSha256(temp, copied);
            _plugins.TryGetValue(copied.Id, out var existing);
            if (existing == null)
            {
                Directory.Move(temp, destination);
                var record = new PluginRecord { Manifest = copied, Directory = destination, Enabled = enableImmediately, GrantedPermissions = PluginPermission.None, InstalledUtc = DateTimeOffset.UtcNow, DllSha256 = hash, IsDev = true, DevSourceDirectory = source, ActivityPersistence = SaveState };
                _plugins.Add(record.Manifest.Id, record);
                SaveState();
                if (record.Enabled) TryLoad(record);
                PluginsChanged?.Invoke(this, EventArgs.Empty);
                return record;
            }

            bool wasEnabled = existing.Enabled;
            PluginPermission oldGranted = existing.GrantedPermissions;
            PluginPermission oldRequested = existing.RequestedPermissions;
            DisableRecord(existing);
            string backup = destination + ".unpacked-backup-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.Move(destination, backup);
                Directory.Move(temp, destination); temp = string.Empty;
                string oldData = Path.Combine(backup, "data"); string newData = Path.Combine(destination, "data");
                if (Directory.Exists(oldData)) { TryDeleteDirectory(newData); Directory.Move(oldData, newData); }
                TryDeleteDirectory(backup);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
            existing.Manifest = copied;
            existing.Directory = destination;
            existing.DllSha256 = hash;
            existing.IsDev = true;
            existing.DevSourceDirectory = source;
            existing.GrantedPermissions = oldGranted & copied.AvailablePermissions;
            existing.PendingNewPermissions = copied.RequestedPermissions & ~oldRequested;
            existing.Error = null;
            existing.Status = wasEnabled ? "Loading (unpacked)" : "Disabled";
            SaveState();
            if (wasEnabled || enableImmediately) { existing.Enabled = true; TryLoad(existing); }
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            return existing;
        }
        catch { if (!string.IsNullOrEmpty(temp)) TryDeleteDirectory(temp); throw; }
    }

    private static void CopyDirectory(string source, string destination, bool skipGit)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            if (skipGit && Path.GetFileName(file).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            if (skipGit && Path.GetFileName(dir).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), skipGit);
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
        PluginPermission old = record.GrantedPermissions;
        PluginPermission sanitized = permissions & record.Manifest.AvailablePermissions;
        record.GrantedPermissions = sanitized;
        record.PendingNewPermissions = PluginPermission.None;
        if (old != sanitized)
            record.Sandbox?.PushGrantedPermissions(sanitized);
        SaveState();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task<bool> RequestPermissionAsync(PluginRecord record, string name, CancellationToken cancellationToken)
    {
        PluginPermission permission = PluginPermissionNames.Parse(new[] { name });
        if (permission == PluginPermission.None)
            throw new ArgumentException("Unknown plugin permission.", nameof(name));
        var info = PluginPermissionCatalog.Get(permission);
        if (!record.Manifest.OptionalPermissionSet.HasFlag(permission))
            throw new SecurityException($"Permission '{info.Name}' is not declared in optional_permissions.");
        if (record.HasPermission(permission)) return true;

        bool granted = await RunPermissionPromptAsync(info, record, cancellationToken).ConfigureAwait(true);
        if (!granted) return false;
        record.GrantedPermissions |= permission;
        record.Sandbox?.PushGrantedPermissions(record.GrantedPermissions);
        SaveState();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private Task<bool> RunPermissionPromptAsync(PluginPermissionInfo info, PluginRecord record, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);
        return Task.FromResult(PermissionConfirmationDialog.Confirm(_browser, info,
            record.HasPermission(PluginPermission.Network) && info.IsDataReading));
    }

    public void Reload(string id)
    {
        if (!_plugins.TryGetValue(id, out var record)) return;
        if (record.IsDev && !string.IsNullOrWhiteSpace(record.DevSourceDirectory))
        {
            try
            {
                ReloadUnpackedSource(record);
                return;
            }
            catch (Exception ex)
            {
                record.Error = ex.Message;
                record.Status = "Unpacked reload failed";
                SaveState();
                PluginsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        DisableRecord(record);
        if (record.Enabled) TryLoad(record);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadUnpackedSource(PluginRecord record)
    {
        string source = Path.GetFullPath(record.DevSourceDirectory!);
        string manifestPath = Path.Combine(source, "plugin.json");
        string libPath = Path.Combine(source, "lib");
        if (!Directory.Exists(source) || !File.Exists(manifestPath) || !Directory.Exists(libPath))
            throw new InvalidDataException("Load Unpacked source must contain plugin.json and lib/.");
        PluginManifest manifest = LoadManifest(manifestPath);
        ValidateManifest(manifest);
        if (!manifest.Id.Equals(record.Manifest.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The unpacked source changed plugin id.");
        ValidateEntryAssembly(source, manifest);

        bool wasEnabled = record.Enabled;
        PluginPermission oldGranted = record.GrantedPermissions;
        PluginPermission oldRequested = record.RequestedPermissions;
        string destination = record.Directory;
        string temp = destination + ".reload-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            CopyDirectory(source, temp, skipGit: true);
            PluginManifest copied = LoadManifest(Path.Combine(temp, "plugin.json"));
            ValidateManifest(copied);
            ValidateEntryAssembly(temp, copied);
            string newHash = ComputeDllSha256(temp, copied);
            DisableRecord(record);
            string backup = destination + ".reload-backup-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.Move(destination, backup);
                Directory.Move(temp, destination);
                temp = string.Empty;
                string oldData = Path.Combine(backup, "data");
                string newData = Path.Combine(destination, "data");
                if (Directory.Exists(oldData))
                {
                    TryDeleteDirectory(newData);
                    Directory.Move(oldData, newData);
                }
                TryDeleteDirectory(backup);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
            record.Manifest = copied;
            record.Directory = destination;
            record.DllSha256 = newHash;
            record.GrantedPermissions = oldGranted & copied.AvailablePermissions;
            record.PendingNewPermissions = copied.RequestedPermissions & ~oldRequested;
            record.Error = null;
            record.Status = wasEnabled ? "Reloading (unpacked)" : "Disabled";
            SaveState();
            if (wasEnabled) TryLoad(record);
            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (!string.IsNullOrEmpty(temp)) TryDeleteDirectory(temp);
            throw;
        }
    }

    public IReadOnlyList<PluginActivitySummary> GetActivitySummary(string id)
    {
        if (!_plugins.TryGetValue(id, out var record)) return Array.Empty<PluginActivitySummary>();
        return record.BuildActivitySummary();
    }

    public PluginPermission ConsumePendingNewPermissions(string id)
    {
        if (!_plugins.TryGetValue(id, out var record)) return PluginPermission.None;
        PluginPermission value = record.PendingNewPermissions;
        record.PendingNewPermissions = PluginPermission.None;
        return value;
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

    private void OnPageChanged()
    {
        EmbeddedRuntime[] old;
        lock (_embedLock) { old = _embedded.Values.ToArray(); _embedded.Clear(); }
        foreach (var item in old)
        {
            if (item.Instance != null)
            {
                try { item.Instance.Session?.RaiseEmbeddedVisibility(item.Instance.InstanceToken, false); } catch { }
                try { item.Instance.Session?.RaiseEmbeddedPause(item.Instance.InstanceToken, true); } catch { }
            }
            item.Dispose();
        }
    }

    private Bitmap? ResolveEmbeddedFrame(DomElement element, LayoutBox box, bool isPrint)
    {
        if (_disposed || element.TagName != "embed" || box.Width <= 0 || box.Height <= 0) return null;
        int width = Math.Max(1, (int)Math.Ceiling(box.Width));
        int height = Math.Max(1, (int)Math.Ceiling(box.Height));
        EmbeddedRuntime runtime;
        lock (_embedLock)
        {
            if (!_embedded.TryGetValue(element, out runtime!))
            {
                runtime = new EmbeddedRuntime(element);
                _embedded[element] = runtime;
            }
            EnsureEmbeddedStart(runtime, element, width, height);
            if (runtime.Instance != null)
            {
                if (!runtime.VisibleNotified)
                { runtime.Instance.Session?.RaiseEmbeddedVisibility(runtime.Instance.InstanceToken, true); runtime.VisibleNotified = true; }
                if (runtime.LastNotifiedWidth != width || runtime.LastNotifiedHeight != height)
                { runtime.Instance.Session?.RaiseEmbeddedResize(runtime.Instance.InstanceToken, width, height); runtime.LastNotifiedWidth = width; runtime.LastNotifiedHeight = height; }
            }
        }

        if (isPrint)
        {
            if (runtime.Instance == null) return runtime.LastPrintFrame;
            if (runtime.LastPrintWidth == width && runtime.LastPrintHeight == height && runtime.LastPrintFrame != null)
                return runtime.LastPrintFrame;
            try
            {
                // Safe to build the Bitmap synchronously here — this method
                // is only ever called from ReRenderPage on the UI thread.
                var request = new EmbeddedRenderRequest(width, height, checked(width * 4), 300, 300, true);
                var frame = runtime.Instance.RenderAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                var bitmap = BuildBitmap(ExtractPixels(frame), width, height);
                runtime.LastPrintFrame?.Dispose();
                runtime.LastPrintFrame = bitmap;
                runtime.LastPrintWidth = width;
                runtime.LastPrintHeight = height;
            }
            catch (Exception ex)
            {
                runtime.Error = ex.Message;
            }
            return runtime.LastPrintFrame;
        }

        if (runtime.Instance != null && (runtime.LastWidth != width || runtime.LastHeight != height))
            RequestEmbeddedRender(runtime, width, height, false);
        return runtime.LastFrame;
    }

    private void EnsureEmbeddedStart(EmbeddedRuntime runtime, DomElement element, int width, int height)
    {
        if (runtime.Instance != null || runtime.StartTask != null) return;
        runtime.StartTask = StartEmbeddedAsync(runtime, element, width, height);
    }

    // IMPORTANT: GDI+ Bitmap objects are not thread-safe and must only be
    // constructed, drawn, or disposed on the UI thread. Building the Bitmap
    // here (background thread) while OnPaint draws/disposes the previous one
    // concurrently on the UI thread is a real GDI+ handle-table hazard —
    // it manifests as a full-process hang (multiple threads blocked in
    // combase.dll/coreclr.dll waiting on GDI+'s internal synchronization),
    // not a clean crash. This was rare before the ~30fps render timer, but
    // firing RequestEmbeddedRender continuously makes the race window nearly
    // guaranteed to be hit. Only decode into a plain byte[] here; the actual
    // Bitmap gets built on the UI thread inside the BeginInvoke callback below.
    private static byte[] ExtractPixels(EmbeddedFrameBuffer frame)
    {
        // Frame is already tightly packed (stride == width*4) coming out of
        // RenderAsync's own conversion; if not, re-pack it here so the UI
        // thread's Bitmap construction is a straight LockBits copy.
        if (frame.Stride == frame.Width * 4)
            return frame.Pixels;

        byte[] packed = new byte[frame.Width * frame.Height * 4];
        int srcStride = frame.Stride;
        int dstStride = frame.Width * 4;
        for (int y = 0; y < frame.Height; y++)
            Buffer.BlockCopy(frame.Pixels, y * srcStride, packed, y * dstStride, dstStride);
        return packed;
    }

    // Must be called on the UI thread only.
    private static Bitmap BuildBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, Math.Min(pixels.Length, data.Stride * height));
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private void RequestEmbeddedRender(EmbeddedRuntime runtime, int width, int height, bool isPrint)
    {
        if (Interlocked.Exchange(ref runtime.Rendering, 1) != 0 || runtime.Instance == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                int stride = checked(width * 4);
                int dpi = isPrint ? 300 : 96;
                var frame = await runtime.Instance.RenderAsync(new EmbeddedRenderRequest(width, height, stride, dpi, dpi, isPrint), runtime.Lifetime.Token).ConfigureAwait(false);
                byte[] pixels = ExtractPixels(frame);
                if (runtime.IsDisposed) return;

                // Hop to the UI thread before touching any GDI+ object.
                if (_browser.IsHandleCreated && !_browser.IsDisposed)
                {
                    _browser.BeginInvoke((Action)(() =>
                    {
                        if (runtime.IsDisposed) return;
                        var bitmap = BuildBitmap(pixels, width, height);
                        if (isPrint) { runtime.LastPrintFrame?.Dispose(); runtime.LastPrintFrame = bitmap; runtime.LastPrintWidth = width; runtime.LastPrintHeight = height; }
                        else { runtime.LastFrame?.Dispose(); runtime.LastFrame = bitmap; runtime.LastWidth = width; runtime.LastHeight = height; }
                        _browser.Invalidate();
                    }));
                }
            }
            catch (Exception ex) { runtime.Error = ex.Message; }
            finally { Volatile.Write(ref runtime.Rendering, 0); }
        });
    }

    private async Task StartEmbeddedAsync(EmbeddedRuntime runtime, DomElement element, int width, int height)
    {
        try
        {
            string mime = (element.GetAttr("type") ?? InferMimeFromSource(element.GetAttr("src"))).Trim().ToLowerInvariant();
            if (mime.Length == 0) return;
            PluginRecord? record = null;
            string registrationToken = "";
            lock (_plugins)
            {
                foreach (var candidate in _plugins.Values)
                {
                    if (!candidate.Enabled || candidate.Sandbox == null || !candidate.HasPermission(PluginPermission.EmbedRenderer)) continue;
                    if (!candidate.Manifest.EmbedTypes.Any(t => t.Equals(mime, StringComparison.OrdinalIgnoreCase))) continue;
                    if (candidate.Sandbox.TryGetEmbedRegistrationToken(mime, out registrationToken)) { record = candidate; break; }
                }
            }
            if (record?.Sandbox == null || registrationToken.Length == 0) return;
            string source = element.GetAttr("src") ?? "";
            string absolute = ResolveEmbedUrl(source);
            var sourceStream = await _browser.OpenPluginEmbeddedSourceAsync(absolute, runtime.Lifetime.Token).ConfigureAwait(false);
            bool owned = false;
            try
            {
                var parameters = element.Attrs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                var instance = await record.Sandbox.CreateEmbeddedInstanceAsync(registrationToken, mime, absolute, _browser.PluginCurrentUrl, _browser.PluginUserAgent, parameters, width, height, sourceStream.Body, sourceStream.CanSeek, sourceStream.Length, runtime.Lifetime.Token).ConfigureAwait(false);
                owned = true;
                if (runtime.IsDisposed)
                {
                    instance.Dispose();
                    return;
                }
                runtime.Instance = instance;
                instance.Element = element;
                record.Sandbox.AttachEmbeddedElement(instance, element);
                runtime.ScriptName = instance.ScriptName;
                runtime.ScriptMethods = instance.ScriptMethods;
                RequestEmbeddedRender(runtime, width, height, false);
            }
            finally
            {
                if (!owned) sourceStream.Dispose();
            }
        }
        catch (Exception ex) { runtime.Error = ex.Message; }
        finally
        {
            lock (_embedLock)
            {
                if (runtime.Instance == null) runtime.StartTask = null;
            }
        }
    }

    private string ResolveEmbedUrl(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return _browser.PluginCurrentUrl ?? "about:blank";
        string baseUrl = _browser.PluginCurrentUrl ?? "about:blank";
        return ImageCache.ResolveUrl(source, baseUrl);
    }

    private static string InferMimeFromSource(string? source)
    {
        string path = (source ?? "").Split('?', '#')[0];
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dcr" or ".dir" or ".dxr" => "application/x-director",
            ".mov" => "video/quicktime",
            ".mid" or ".midi" => "audio/midi",
            _ => "application/octet-stream"
        };
    }

    private (string ScriptName, IReadOnlyList<string> Methods)? GetEmbeddedScriptInfo(DomElement element)
    {
        lock (_embedLock)
        {
            if (_embedded.TryGetValue(element, out var runtime) && !string.IsNullOrWhiteSpace(runtime.ScriptName))
                return (runtime.ScriptName, runtime.ScriptMethods);

            string mime = (element.GetAttr("type") ?? InferMimeFromSource(element.GetAttr("src"))).Trim().ToLowerInvariant();
            foreach (var candidate in _plugins.Values)
            {
                if (!candidate.Enabled || candidate.Sandbox == null || !candidate.HasPermission(PluginPermission.EmbedRenderer) || !candidate.HasPermission(PluginPermission.EmbedScript)) continue;
                if (string.IsNullOrWhiteSpace(candidate.Manifest.ScriptName)) continue;
                if (!candidate.Manifest.EmbedTypes.Any(t => t.Equals(mime, StringComparison.OrdinalIgnoreCase))) continue;
                return (candidate.Manifest.ScriptName, Array.Empty<string>());
            }
            return null;
        }
    }

    private async Task<JsValue> CallEmbeddedScriptAsync(DomElement element, string name, IReadOnlyList<JsValue> args)
    {
        EmbeddedRuntime runtime;
        lock (_embedLock)
        {
            if (!_embedded.TryGetValue(element, out runtime!))
            {
                runtime = new EmbeddedRuntime(element);
                _embedded[element] = runtime;
            }
            EnsureEmbeddedStart(runtime, element, Math.Max(1, _browser.PluginViewportSize.Width), Math.Max(1, _browser.PluginViewportSize.Height));
        }
        if (runtime.StartTask != null && runtime.Instance == null)
            await runtime.StartTask.ConfigureAwait(false);
        if (runtime.Instance == null) throw new InvalidOperationException("Embedded plugin instance is unavailable.");
        if (runtime.ScriptMethods.Count != 0 && !runtime.ScriptMethods.Contains(name, StringComparer.Ordinal))
            throw new MissingMethodException(name);
        return await runtime.Instance.CallScriptAsync(name, args).ConfigureAwait(false);
    }

    private void DispatchEmbeddedInput(DomElement element, EmbeddedInputEvent ev)
    {
        EmbeddedRuntime? runtime;
        lock (_embedLock)
        {
            if (!_embedded.TryGetValue(element, out runtime))
            {
                runtime = new EmbeddedRuntime(element);
                _embedded[element] = runtime;
            }
            EnsureEmbeddedStart(runtime, element, Math.Max(1, _browser.PluginViewportSize.Width), Math.Max(1, _browser.PluginViewportSize.Height));
        }
        _ = DispatchAfterStartAsync(runtime, ev);
    }

    private static async Task DispatchAfterStartAsync(EmbeddedRuntime runtime, EmbeddedInputEvent ev)
    {
        try
        {
            if (runtime.StartTask != null && runtime.Instance == null) await runtime.StartTask.ConfigureAwait(false);
            if (runtime.Instance != null) await runtime.Instance.HandleInputAsync(ev).ConfigureAwait(false);
        }
        catch { }
    }

    private sealed class EmbeddedRuntime : IDisposable
    {
        public EmbeddedRuntime(DomElement element) { Element = element; }
        public DomElement Element { get; }
        public PluginSandboxSession.EmbeddedHostInstance? Instance { get; set; }
        public string ScriptName { get; set; } = "";
        public IReadOnlyList<string> ScriptMethods { get; set; } = Array.Empty<string>();
        public Bitmap? LastFrame { get; set; }
        public Bitmap? LastPrintFrame { get; set; }
        public int LastWidth { get; set; }
        public int LastHeight { get; set; }
        public int LastPrintWidth { get; set; }
        public int LastPrintHeight { get; set; }
        public int LastNotifiedWidth { get; set; }
        public int LastNotifiedHeight { get; set; }
        public bool VisibleNotified { get; set; }
        public Task? StartTask { get; set; }
        public int Rendering;
        public string? Error { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Lifetime.Cancel();
            try { Instance?.Dispose(); } catch { }
            Instance = null;
            LastFrame?.Dispose();
            LastFrame = null;
            LastPrintFrame?.Dispose();
            LastPrintFrame = null;
            Lifetime.Dispose();
        }
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

    internal void RaiseNavigationFailed(string url, string message)
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.BrowserEvents))
                record.Sandbox.RaiseNavigationFailed(url, message);
    }

    internal void RaiseTitleChanged(string url, string title)
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.BrowserEvents))
                record.Sandbox.RaiseTitleChanged(url, title);
    }

    internal void RaiseLoadProgress(string url, double fraction)
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.BrowserEvents))
                record.Sandbox.RaiseLoadProgress(url, fraction);
    }

    internal void RaiseZoomChanged(float zoom)
    {
        foreach (var record in _plugins.Values.ToArray())
            if (record.Enabled && record.Sandbox != null && record.HasPermission(PluginPermission.BrowserEvents))
                record.Sandbox.RaiseZoomChanged(zoom);
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

    internal void RegisterPluginOmnibox(PluginRecord record, string keyword, string token)
    {
        lock (_pluginOmnibox)
        {
            if (_pluginOmnibox.TryGetValue(keyword, out var existing) && !ReferenceEquals(existing.Record, record)) throw new InvalidOperationException($"Omnibox keyword '{keyword}' is already registered by another plugin.");
            _pluginOmnibox[keyword] = (record, token);
        }
    }

    internal void UnregisterPluginOmnibox(PluginRecord record, string keyword, string token)
    {
        lock (_pluginOmnibox) if (_pluginOmnibox.TryGetValue(keyword, out var existing) && ReferenceEquals(existing.Record, record) && existing.Token == token) _pluginOmnibox.Remove(keyword);
    }

    internal IReadOnlyDictionary<string, string> GetPluginSettings(PluginRecord record)
    {
        if (!record.Manifest.Settings.Any() || !record.HasPermission(PluginPermission.Settings)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string path = Path.Combine(record.Directory, "data", "storage.json");
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            return values.Where(p => p.Key.StartsWith("settings.", StringComparison.Ordinal)).ToDictionary(p => p.Key[9..], p => p.Value ?? "", StringComparer.Ordinal);
        }
        catch { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    internal void SetPluginSetting(PluginRecord record, PluginSettingDefinition definition, string value)
    {
        if (!record.HasPermission(PluginPermission.Settings)) throw new SecurityException("Plugin settings permission has not been granted.");
        string key = "settings." + definition.Name;
        string path = Path.Combine(record.Directory, "data", "storage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Dictionary<string, string> values;
        try { values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); } catch { values = new(); }
        values[key] = value ?? "";
        File.WriteAllText(path, JsonSerializer.Serialize(values));
        record.Sandbox?.PushSettingChanged(definition.Name, values[key]);
    }

    internal async Task<IReadOnlyList<PluginOmniboxSuggestion>> GetOmniboxSuggestionsAsync(string text, CancellationToken ct)
    {
        string[] parts = (text ?? string.Empty).TrimStart().Split(' ', 2, StringSplitOptions.None);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return Array.Empty<PluginOmniboxSuggestion>();
        (PluginRecord Record, string Token) entry;
        lock (_pluginOmnibox) if (!_pluginOmnibox.TryGetValue(parts[0], out entry)) return Array.Empty<PluginOmniboxSuggestion>();
        if (!entry.Record.Enabled || entry.Record.Sandbox == null || !entry.Record.HasPermission(PluginPermission.Omnibox)) return Array.Empty<PluginOmniboxSuggestion>();
        string query = parts.Length > 1 ? parts[1] : string.Empty;
        return await entry.Record.Sandbox.GetOmniboxSuggestionsAsync(parts[0], query, ct).ConfigureAwait(true);
    }

    internal void RegisterPluginProtocol(PluginRecord record, string scheme, string token)
    {
        scheme = scheme.Trim().ToLowerInvariant();
        lock (_pluginProtocols)
        {
            if (_pluginProtocols.TryGetValue(scheme, out var existing) && !ReferenceEquals(existing.Record, record))
                throw new InvalidOperationException($"Protocol scheme '{scheme}' is already registered by another plugin.");
            _pluginProtocols[scheme] = (record, token);
        }
    }

    internal void UnregisterPluginProtocol(PluginRecord record, string scheme, string token)
    {
        scheme = scheme.Trim().ToLowerInvariant();
        lock (_pluginProtocols)
        {
            if (_pluginProtocols.TryGetValue(scheme, out var existing) && ReferenceEquals(existing.Record, record) && existing.Token == token)
                _pluginProtocols.Remove(scheme);
        }
    }

    internal async Task<PluginProtocolResponse?> HandleProtocolAsync(string scheme, string url, string method, CancellationToken ct)
    {
        PluginSandboxSession? session = null;
        lock (_pluginProtocols)
            if (_pluginProtocols.TryGetValue(scheme, out var item) && item.Record.Enabled && item.Record.HasPermission(PluginPermission.Protocol)) session = item.Record.Sandbox;
        if (session == null) return null;
        return await session.HandleProtocolAsync(scheme, url, method, ct).ConfigureAwait(true);
    }

    internal async Task<string?> TransformContentAsync(string contentType, string url, string charset, byte[] body, CancellationToken ct)
    {
        foreach (var record in _plugins.Values.ToArray())
        {
            if (!record.Enabled || record.Sandbox == null || !record.HasPermission(PluginPermission.ContentTransform)) continue;
            if (!record.Sandbox.GetContentTransformTypes().Any(t => contentType.Equals(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (!record.Manifest.ContentTransformScopes.Any(scope => PluginContentTransformPolicy.ScopeMatches(scope, url, contentType))) continue;
            return await record.Sandbox.TransformContentAsync(contentType, url, charset, body, ct).ConfigureAwait(true);
        }
        return null;
    }

    internal string BuildPageStyleCss()
    {
        var styles = new List<string>();
        foreach (var record in _plugins.Values.ToArray())
        {
            if (!record.Enabled || record.Sandbox == null || !record.HasPermission(PluginPermission.PageStyle)) continue;
            styles.AddRange(record.Sandbox.PageStyles);
        }
        return string.Join("\n", styles.Select(x => x[(x.IndexOf('\n') + 1)..]));
    }

    internal async Task<PluginBeforeNavigateDecision> BeforeNavigateAsync(string url, CancellationToken ct)
    {
        foreach (var record in _plugins.Values.ToArray())
        {
            if (!record.Enabled || record.Sandbox == null || !record.HasPermission(PluginPermission.Tabs)) continue;
            var decision = await record.Sandbox.BeforeNavigateAsync("active", url, ct).ConfigureAwait(true);
            if (decision.Action != PluginBeforeNavigateAction.Allow) return decision;
        }
        return new PluginBeforeNavigateDecision(PluginBeforeNavigateAction.Allow);
    }

    internal PluginNetworkRuleDecision EvaluateNetworkRules(string url, IReadOnlyDictionary<string, string> headers)
    {
        var strip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _plugins.Values.ToArray())
        {
            if (!record.Enabled || record.Sandbox == null || !record.HasPermission(PluginPermission.NetworkRules)) continue;
            foreach (var rule in record.Sandbox.SnapshotNetworkRules())
            {
                if (!NetworkRuleMatches(rule.Match, url)) continue;
                if (rule.Kind == PluginNetworkRuleKind.Block) return new PluginNetworkRuleDecision(true, null, strip);
                if (rule.Kind == PluginNetworkRuleKind.Redirect && !string.IsNullOrWhiteSpace(rule.Replacement)) return new PluginNetworkRuleDecision(false, rule.Replacement, strip);
                if (rule.Kind == PluginNetworkRuleKind.StripHeader && !string.IsNullOrWhiteSpace(rule.Replacement)) strip.Add(rule.Replacement);
            }
        }
        return new PluginNetworkRuleDecision(false, null, strip);
    }

    private static bool NetworkRuleMatches(string match, string url)
    {
        string value = match.Trim();
        if (value == "*") return true;
        if (value.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
        {
            string host = value[5..].Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
        }
        return url.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    internal void PopulateContextMenu(ContextMenuStrip menu, ContextMenuContext context)
    {
        _browser.PopulatePluginContextMenu(menu, context);
    }

    internal void AppendPluginLog(PluginRecord record, string line)
    {
        try
        {
            string path = Path.Combine(record.Directory, "data", "plugin.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line.Length > 16 * 1024 ? line[..(16 * 1024)] : line);
            writer.Flush();
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024) using (var input = File.OpenRead(path)) { input.Seek(info.Length - 1024 * 1024, SeekOrigin.Begin); using var output = File.Create(path + ".trim"); input.CopyTo(output); }
            string trim = path + ".trim"; if (File.Exists(trim)) { File.Delete(path); File.Move(trim, path); }
        } catch { }
    }

    internal string GetPluginLog(PluginRecord record)
    {
        try { string path = Path.Combine(record.Directory, "data", "plugin.log"); return File.Exists(path) ? File.ReadAllText(path) : "No plugin log has been written."; } catch (Exception ex) { return "Unable to read plugin log: " + ex.Message; }
    }

    internal void RecordSandboxCrash(PluginRecord record, string reason)
    {
        if (_disposed || !record.Enabled) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        record.LastCrashUtc = now; record.LastCrashReason = string.IsNullOrWhiteSpace(reason) ? "Plugin worker disconnected." : reason;
        record.CrashTimes.RemoveAll(t => now - t > PluginCrashWindow);
        record.CrashTimes.Add(now);
        record.RestartAttempt = Math.Min(record.RestartAttempt + 1, PluginRestartBackoff.Length);
        record.Sandbox = null;
        if (record.CrashTimes.Count >= MaxPluginCrashesInWindow)
        {
            record.Enabled = false;
            record.Status = "Disabled after repeated crashes";
        }
        else
        {
            record.Status = "Crashed; restart scheduled";
            TimeSpan delay = PluginRestartBackoff[Math.Max(0, record.RestartAttempt - 1)];
            _ = RestartAfterCrashAsync(record, delay);
        }
        SaveState(); PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RestartAfterCrashAsync(PluginRecord record, TimeSpan delay)
    {
        try { await Task.Delay(delay).ConfigureAwait(false); } catch { return; }
        if (_disposed || !record.Enabled || record.Sandbox != null) return;
        if (_browser.IsHandleCreated && !_browser.IsDisposed) _browser.BeginInvoke((Action)(() => TryLoad(record)));
        else TryLoad(record);
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
        var sandbox = new PluginSandboxSession(_browser, this, record);
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
            if (_browser.IsHandleCreated && !_browser.IsDisposed)
                _browser.BeginInvoke((Action)(() => _browser.Invalidate()));
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
                RecordSandboxCrash(record, ex.ToString());
            }
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string ComputeDllSha256(string root, PluginManifest manifest)
    {
        string path = Path.GetFullPath(Path.Combine(root, manifest.Assembly));
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static int ComparePluginVersions(string left, string right)
    {
        static (int number, string[] prerelease) Parse(string value)
        {
            string main = value?.Trim() ?? "0";
            string[] parts = main.Split('-', 2, StringSplitOptions.TrimEntries);
            string[] nums = parts[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
            int number = 0;
            foreach (string part in nums.Take(4))
            {
                number = checked(number * 1000 + (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 0, 999) : 0));
            }
            string[] pre = parts.Length == 2 ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            return (number, pre);
        }

        var a = Parse(left);
        var b = Parse(right);
        int c = a.number.CompareTo(b.number);
        if (c != 0) return c;
        if (a.prerelease.Length == 0 && b.prerelease.Length == 0) return 0;
        if (a.prerelease.Length == 0) return 1;
        if (b.prerelease.Length == 0) return -1;
        int count = Math.Max(a.prerelease.Length, b.prerelease.Length);
        for (int i = 0; i < count; i++)
        {
            if (i >= a.prerelease.Length) return -1;
            if (i >= b.prerelease.Length) return 1;
            bool an = int.TryParse(a.prerelease[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int av);
            bool bn = int.TryParse(b.prerelease[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bv);
            c = (an && bn) ? av.CompareTo(bv) : string.Compare(a.prerelease[i], b.prerelease[i], StringComparison.Ordinal);
            if (c != 0) return c;
        }
        return 0;
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

    private static void ValidateSettings(IEnumerable<PluginSettingDefinition>? settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var setting in settings ?? Array.Empty<PluginSettingDefinition>())
        {
            if (++count > 32) throw new InvalidDataException("A plugin may declare at most 32 settings.");
            string name = (setting.Name ?? "").Trim();
            string type = (setting.Type ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Length > 64 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new InvalidDataException($"Invalid plugin setting name '{setting.Name}'.");
            if (!seen.Add(name)) throw new InvalidDataException($"Duplicate plugin setting '{name}'.");
            if (type is not ("toggle" or "text" or "select"))
                throw new InvalidDataException($"Plugin setting '{name}' has unsupported type '{setting.Type}'. Use toggle, text, or select.");
            if ((setting.Label ?? "").Length > 128 || (setting.Description ?? "").Length > 512 || (setting.DefaultValue ?? "").Length > 2048)
                throw new InvalidDataException($"Plugin setting '{name}' contains text that is too long.");
            if (type == "select" && (setting.Options == null || setting.Options.Length == 0 || setting.Options.Length > 32 || setting.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 128)))
                throw new InvalidDataException($"Plugin select setting '{name}' must contain 1-32 options of at most 128 characters.");
        }
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("plugin.json requires id and name.");
        if (manifest.ApiVersion != Retro96PluginApi.ApiVersion)
            throw new InvalidDataException($"Unsupported plugin API version {manifest.ApiVersion}; this host supports {Retro96PluginApi.ApiVersion}.");
        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion) &&
            ComparePluginVersions(Application.ProductVersion, manifest.MinHostVersion) < 0)
            throw new InvalidDataException($"This plugin requires Retro96 {manifest.MinHostVersion} or newer; this host is {Application.ProductVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Assembly) || string.IsNullOrWhiteSpace(manifest.EntryPoint))
            throw new InvalidDataException("plugin.json requires assembly and entryPoint.");
        if (!manifest.Id.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new InvalidDataException("Plugin id may contain only letters, digits, '.', '-' and '_'.");
        ValidatePermissions(manifest.Permissions);
        ValidatePermissions(manifest.OptionalPermissions);
        ValidateSettings(manifest.Settings);
        var overlap = new HashSet<string>(manifest.Permissions ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        if (manifest.OptionalPermissions != null && manifest.OptionalPermissions.Any(overlap.Contains))
            throw new InvalidDataException("A permission cannot be listed in both permissions and optional_permissions.");
        manifest.OptionalPermissions ??= new List<string>();
        manifest.EmbedTypes ??= new List<string>();
        manifest.ContentTransformScopes ??= new List<PluginContentTransformScope>();
        bool transform = manifest.RequestedPermissions.HasFlag(PluginPermission.ContentTransform) ||
                         manifest.OptionalPermissionSet.HasFlag(PluginPermission.ContentTransform);
        if (transform && manifest.ContentTransformScopes.Count == 0)
            throw new InvalidDataException("Plugins declaring 'content.transform' must declare at least one content_transform_scopes entry with both url and mime patterns.");
        if (manifest.ContentTransformScopes.Count > 32)
            throw new InvalidDataException("At most 32 content_transform_scopes entries are allowed.");
        foreach (var scope in manifest.ContentTransformScopes)
        {
            if (!PluginContentTransformPolicy.IsSafeTransformUrlPattern(scope.UrlPattern))
                throw new InvalidDataException("Each content_transform_scopes url pattern must be 1-2048 characters and may use * or ? wildcards.");
            if (!PluginContentTransformPolicy.IsSafeTransformMimePattern(scope.MimePattern))
                throw new InvalidDataException("Each content_transform_scopes mime pattern must be a MIME type pattern such as text/*.");
        }
        bool renderer = manifest.RequestedPermissions.HasFlag(PluginPermission.EmbedRenderer);
        bool script = manifest.RequestedPermissions.HasFlag(PluginPermission.EmbedScript);
        if (renderer && manifest.EmbedTypes.Count == 0)
            throw new InvalidDataException("Plugins requesting 'embed.renderer' must declare at least one embed_types MIME type.");
        foreach (string mime in manifest.EmbedTypes)
        {
            string normalized = (mime ?? "").Trim();
            if (normalized.Length is < 3 or > 256 || !normalized.Contains('/'))
                throw new InvalidDataException($"Invalid embedded MIME type '{mime}'.");
        }
        if (script && !renderer)
            throw new InvalidDataException("'embed.script' requires 'embed.renderer'.");
        if (!string.IsNullOrWhiteSpace(manifest.ScriptName) && (manifest.ScriptName.Length > 128 || !(char.IsLetter(manifest.ScriptName[0]) || manifest.ScriptName[0] is '_' or '$') || !manifest.ScriptName.Skip(1).All(c => char.IsLetterOrDigit(c) || c is '_' or '$')))
            throw new InvalidDataException("script_name must be a simple JavaScript identifier.");
        if (script && string.IsNullOrWhiteSpace(manifest.ScriptName))
            throw new InvalidDataException("Plugins requesting 'embed.script' must declare script_name.");
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
                        GrantedPermissions = manifest.AvailablePermissions & (PluginPermission)state.GrantedPermissions,
                        InstalledUtc = state.InstalledUtc == default ? new DateTimeOffset(File.GetCreationTimeUtc(manifestPath), TimeSpan.Zero) : state.InstalledUtc,
                        DllSha256 = string.IsNullOrWhiteSpace(state.DllSha256) ? SafeComputeDllSha256(dir, manifest) : state.DllSha256,
                        PermissionChanges = state.PermissionChanges ?? new List<PluginPermissionVersionChange>(),
                        LastCrashReason = state.LastCrashReason,
                        LastCrashUtc = state.LastCrashUtc,
                        Activity = state.Activity ?? new List<PluginActivityEntry>(),
                        CrashTimes = state.CrashTimes ?? new List<DateTimeOffset>(),
                        RestartAttempt = state.RestartAttempt,
                        RejectedShortcuts = state.RejectedShortcuts ?? new List<string>(),
                        IsDev = state.IsDev,
                        DevSourceDirectory = state.DevSourceDirectory
                    };
                    _plugins[state.Id].ActivityPersistence = SaveState;
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
            GrantedPermissions = p.GrantedPermissions,
            InstalledUtc = p.InstalledUtc,
            DllSha256 = p.DllSha256,
            PermissionChanges = p.PermissionChanges,
            LastCrashReason = p.LastCrashReason,
            LastCrashUtc = p.LastCrashUtc,
            Activity = p.SnapshotActivity(),
            CrashTimes = p.CrashTimes.ToList(),
            RestartAttempt = p.RestartAttempt,
            RejectedShortcuts = p.RejectedShortcuts.ToList(),
            IsDev = p.IsDev,
            DevSourceDirectory = p.DevSourceDirectory
        }).ToList();
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, _jsonOptions));
    }

    private static string SafeComputeDllSha256(string root, PluginManifest manifest)
    {
        try { return ComputeDllSha256(root, manifest); } catch { return "unavailable"; }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static void DisableRuntimeOnly(PluginRecord record)
    {
        try { if (record.Sandbox is { } sandbox) sandbox.ExpectedShutdown = true; record.Sandbox?.Dispose(); } catch { }
        record.Sandbox = null;
    }

    private void DisableRecord(PluginRecord record)
    {
        lock (_pluginProtocols)
            foreach (var key in _pluginProtocols.Where(x => ReferenceEquals(x.Value.Record, record)).Select(x => x.Key).ToArray()) _pluginProtocols.Remove(key);
        lock (_pluginOmnibox)
            foreach (var key in _pluginOmnibox.Where(x => ReferenceEquals(x.Value.Record, record)).Select(x => x.Key).ToArray()) _pluginOmnibox.Remove(key);
        DisableRuntimeOnly(record);
        _browser.RemovePluginFileMenuItems(record.Manifest.Id);
        record.Status = "Disabled";
    }

    private void OnEmbedRenderTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        EmbeddedRuntime[] runtimes;
        lock (_embedLock) { runtimes = _embedded.Values.ToArray(); }
        foreach (var runtime in runtimes)
        {
            if (runtime.Instance != null && runtime.LastWidth > 0 && runtime.LastHeight > 0)
                RequestEmbeddedRender(runtime, runtime.LastWidth, runtime.LastHeight, false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _embedRenderTimer.Stop();
        _embedRenderTimer.Dispose();
        _browser.PluginCanvas.PageChanged -= OnPageChanged;
        _browser.PluginCanvas.EmbeddedFrameResolver = null;
        _browser.PluginCanvas.EmbeddedInputDispatcher = null;
        _browser.PluginCanvas.EmbeddedScriptInfoResolver = null;
        _browser.PluginCanvas.EmbeddedScriptCall = null;
        OnPageChanged();
        foreach (var record in _plugins.Values.ToArray()) DisableRecord(record);
        _plugins.Clear();
    }

    private sealed class PluginState
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; }
        public ulong GrantedPermissions { get; set; }
        public DateTimeOffset InstalledUtc { get; set; }
        public string DllSha256 { get; set; } = "";
        public List<PluginPermissionVersionChange>? PermissionChanges { get; set; }
        public string? LastCrashReason { get; set; }
        public DateTimeOffset? LastCrashUtc { get; set; }
        public List<PluginActivityEntry>? Activity { get; set; }
        public List<DateTimeOffset>? CrashTimes { get; set; }
        public int RestartAttempt { get; set; }
        public List<string>? RejectedShortcuts { get; set; }
        public bool IsDev { get; set; }
        public string? DevSourceDirectory { get; set; }
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
        public DateTimeOffset InstalledUtc { get; internal set; }
        public string DllSha256 { get; internal set; } = "";
        public List<PluginPermissionVersionChange> PermissionChanges { get; internal set; } = new();
        public string? LastCrashReason { get; internal set; }
        public DateTimeOffset? LastCrashUtc { get; internal set; }
        public PluginPermission PendingNewPermissions { get; internal set; }
        public bool IsDev { get; internal set; }
        public string? DevSourceDirectory { get; internal set; }
        internal List<PluginActivityEntry> Activity { get; set; } = new();
        internal readonly object ActivitySync = new();
        internal Action? ActivityPersistence { get; set; }
        internal PluginSandboxSession? Sandbox;
        public PluginPermission RequestedPermissions => Manifest.RequestedPermissions;
        public bool HasPermission(PluginPermission permission) => (GrantedPermissions & permission) == permission;
        public int CrashCountInWindow => CrashTimes.Count(t => DateTimeOffset.UtcNow - t <= PluginCrashWindow);

        internal void RecordActivity(PluginPermission permission, string? host)
        {
            string permissionName = PluginPermissionNames.ToNames(permission).FirstOrDefault() ?? permission.ToString();
            lock (ActivitySync)
            {
                Activity.Add(new PluginActivityEntry
                {
                    Permission = permissionName,
                    UsedUtc = DateTimeOffset.UtcNow,
                    NetworkHost = string.IsNullOrWhiteSpace(host) ? null : host
                });
                if (Activity.Count > 1000)
                    Activity.RemoveRange(0, Activity.Count - 1000);
            }
            try { ActivityPersistence?.Invoke(); } catch { }
        }

        internal List<PluginActivityEntry> SnapshotActivity()
        {
            lock (ActivitySync) return Activity.ToList();
        }

        internal IReadOnlyList<PluginActivitySummary> BuildActivitySummary()
        {
            var entries = SnapshotActivity();
            return entries.GroupBy(x => x.Permission, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PluginActivitySummary(
                    group.Key,
                    group.Count(),
                    group.Max(x => x.UsedUtc),
                    group.Where(x => !string.IsNullOrWhiteSpace(x.NetworkHost))
                         .Select(x => x.NetworkHost!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                         .ToArray()))
                .OrderBy(x => x.Permission, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public sealed class PluginActivityEntry
    {
        public string Permission { get; set; } = "";
        public DateTimeOffset UsedUtc { get; set; }
        public string? NetworkHost { get; set; }
    }

    public sealed record PluginActivitySummary(string Permission, int Calls, DateTimeOffset LastUsedUtc, IReadOnlyList<string> NetworkHosts);

    public sealed class PluginPermissionVersionChange
    {
        public DateTimeOffset ChangedUtc { get; set; }
        public string FromVersion { get; set; } = "";
        public string ToVersion { get; set; } = "";
        public PluginPermission OldRequested { get; set; }
        public PluginPermission NewRequested { get; set; }
        public PluginPermission OldGranted { get; set; }
        public PluginPermission NewGranted { get; set; }
        public bool AuthorChanged { get; set; }
        public string OldDllSha256 { get; set; } = "";
        public string NewDllSha256 { get; set; } = "";
    }
}