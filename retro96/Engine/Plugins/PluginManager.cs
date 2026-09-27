using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.Http;
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
    private readonly Form1 _browser;
    private readonly string _rootDirectory;
    private readonly string _stateFile;
    private readonly Dictionary<string, PluginRecord> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private bool _disposed;
    private readonly Dictionary<DomElement, EmbeddedRuntime> _embedded = new();
    private readonly object _embedLock = new();
    private readonly System.Windows.Forms.Timer _embedRenderTimer;

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

    private void OnPageChanged()
    {
        EmbeddedRuntime[] old;
        lock (_embedLock) { old = _embedded.Values.ToArray(); _embedded.Clear(); }
        foreach (var item in old) item.Dispose();
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
        manifest.EmbedTypes ??= new List<string>();
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