using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using Retro96;

namespace Retro96.Plugins;

internal static class PluginSandboxWorker
{
    public static bool IsInvocation(string[] args) => args.Length >= 3 && string.Equals(args[0], "--plugin-worker", StringComparison.OrdinalIgnoreCase);

    public static async Task RunAsync(string[] args)
    {
        if (!IsInvocation(args)) return;
        string pipeName = args[1];
        string pluginDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, args[2]));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15_000).ConfigureAwait(false);
        using var lifetime = new CancellationTokenSource();
        using var writeLock = new SemaphoreSlim(1, 1);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
        var host = new PluginWorkerHost(pluginDirectory, reader, writer, writeLock, lifetime);
        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await host.ReportFatalAsync(ex).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class PluginWorkerHost : IRetro96PluginHost, IDisposable
    {
        private readonly string _pluginDirectory;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock;
        private readonly CancellationTokenSource _lifetime;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<PluginSandboxProtocol.Envelope>> _pending = new();
        private readonly Dictionary<string, Action> _invokeCallbacks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<ContextMenuContext, bool>> _contextQueries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<ContextMenuContext>> _contextActions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<string?, bool?, int?>> _panelCallbacks = new(StringComparer.Ordinal);
        private readonly PluginEventsProxy _events = new();
        private PluginClipboard? _clipboard;
        private PluginAudio? _audio;
        private PluginAssemblyLoadContext? _loadContext;
        private IRetro96Plugin? _plugin;
        private PluginManifest _manifest = new();
        private PluginPermission _grantedPermissions;
        private Task? _readerLoop;
        private int _disposed;

        public PluginWorkerHost(string pluginDirectory, StreamReader reader, StreamWriter writer, SemaphoreSlim writeLock, CancellationTokenSource lifetime)
        { _pluginDirectory = pluginDirectory; _reader = reader; _writer = writer; _writeLock = writeLock; _lifetime = lifetime; }

        public PluginManifest Manifest => _manifest;
        public PluginPermission GrantedPermissions => _grantedPermissions;
        public IPluginBrowser Browser { get; private set; } = null!;
        public IPluginUi Ui { get; private set; } = null!;
        public IPluginStorage Storage { get; private set; } = null!;
        public IPluginNetwork Network { get; private set; } = null!;
        public IPluginFileSystem FileSystem { get; private set; } = null!;
        public IPluginClipboard Clipboard => _clipboard!;
        public IPluginEvents Events => _events;
        public IPluginAudio Audio => _audio!;
        public IPluginNotifications Notifications { get; private set; } = null!;
        public IPluginDialogs Dialogs { get; private set; } = null!;
        public IPluginLogger Log { get; private set; } = null!;
        public bool HasPermission(PluginPermission permission) => (_grantedPermissions & permission) == permission;

        public async Task RunAsync()
        {
            _readerLoop = Task.Run(ReadLoopAsync);
            string pluginId = ReadPluginId();
            var hello = await SendRequestAsync<PluginSandboxProtocol.HelloReply>("hello", new PluginSandboxProtocol.HelloPayload(pluginId, Environment.ProcessId), _lifetime.Token).ConfigureAwait(false);
            if (!hello.Accepted) throw new SecurityException(string.IsNullOrWhiteSpace(hello.Error) ? "Plugin sandbox handshake rejected." : hello.Error);
            var brokerManifest = JsonSerializer.Deserialize(hello.ManifestJson, PluginManifestJsonContext.Default.PluginManifest) ?? throw new InvalidDataException("Plugin manifest was invalid.");
            if (!string.Equals(brokerManifest.Id, _manifest.Id, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Plugin manifest identity mismatch.");
            _grantedPermissions = (PluginPermission)hello.GrantedPermissions;
            LoadPlugin();
            Browser = new WorkerBrowser(this);
            Ui = new WorkerUi(this);
            Storage = new WorkerStorage(this);
            Network = new WorkerNetwork(this);
            FileSystem = new WorkerFileSystem(this);
            _clipboard = new PluginClipboard(this);
            _audio = new PluginAudio(this);
            Notifications = new WorkerNotifications(this);
            Dialogs = new WorkerDialogs(this);
            Log = new WorkerLogger(this);
            try
            {
                _plugin!.Initialize(this);
                await SendRequestAsync<PluginSandboxProtocol.ReadyPayload>("ready", new PluginSandboxProtocol.ReadyPayload(true), _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { await SendRequestAsync<PluginSandboxProtocol.ReadyPayload>("ready", new PluginSandboxProtocol.ReadyPayload(false, ex.ToString()), CancellationToken.None).ConfigureAwait(false); } catch { }
                throw;
            }
            try { await _readerLoop.ConfigureAwait(false); } finally { Dispose(); }
        }

        private string ReadPluginId()
        {
            string manifestPath = Path.Combine(_pluginDirectory, "plugin.json");
            _manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), PluginManifestJsonContext.Default.PluginManifest) ?? throw new InvalidDataException("Sandbox plugin manifest is invalid.");
            if (string.IsNullOrWhiteSpace(_manifest.Id)) throw new InvalidDataException("Sandbox plugin id is missing.");
            return _manifest.Id;
        }

        private void LoadPlugin()
        {
            if (_manifest.ApiVersion != Retro96PluginApi.ApiVersion) throw new InvalidDataException($"Unsupported plugin API version {_manifest.ApiVersion}.");
            string assemblyPath = Path.GetFullPath(Path.Combine(_pluginDirectory, _manifest.Assembly));
            string prefix = Path.GetFullPath(_pluginDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            if (!assemblyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath)) throw new SecurityException("Plugin assembly path is invalid.");
            _loadContext = new PluginAssemblyLoadContext(assemblyPath);
            Assembly assembly = _loadContext.LoadFromAssemblyPath(assemblyPath);
            string typeName = _manifest.EntryPoint; int comma = typeName.IndexOf(','); if (comma >= 0) typeName = typeName[..comma].Trim();
            Type? entryType = assembly.GetType(typeName, false, false);
            if (entryType == null || !typeof(IRetro96Plugin).IsAssignableFrom(entryType)) throw new InvalidDataException("Plugin entry point does not implement IRetro96Plugin.");
            _plugin = Activator.CreateInstance(entryType) as IRetro96Plugin ?? throw new InvalidOperationException("Plugin entry point could not be created.");
        }

        private void InvokePluginCallback(string name, Action callback)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _ = ReportCallbackErrorAsync(name, ex);
            }
        }

        private async Task ReportCallbackErrorAsync(string name, Exception ex)
        {
            try
            {
                await SendRequestAsync<object>(
                    "log",
                    new PluginSandboxProtocol.LogPayload("error", $"Plugin callback '{name}' failed.", ex.ToString()),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var envelope = await PluginSandboxProtocol.ReadAsync(_reader, _lifetime.Token).ConfigureAwait(false);
                    if (envelope == null) break;
                    if (envelope.Op is "response" or "error") { if (_pending.TryGetValue(envelope.Id, out var completion)) completion.TrySetResult(envelope); continue; }
                    switch (envelope.Op)
                    {
                        case "ui.context.query":
                            if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiContextQueryPayload>(envelope) is { } query)
                            {
                                bool visible = QueryContext(query.Token, query.Context);
                                _ = PluginSandboxProtocol.WriteAsync(_writer, "response", envelope.Id, new PluginSandboxProtocol.UiContextResult(visible), _writeLock, _lifetime.Token);
                            }
                            break;
                        case "event.navigated": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EventNavigatedPayload>(envelope) is { } nav) _ = Task.Run(() => InvokePluginCallback("Navigated", () => _events.RaiseNavigated(new PluginNavigationEventArgs(nav.Url)))); break;
                        case "event.pageLoaded": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EventPageLoadedPayload>(envelope) is { } page) _ = Task.Run(() => InvokePluginCallback("PageLoaded", () => _events.RaisePageLoaded(new PluginPageEventArgs(page.Url, page.Title)))); break;
                        case "event.hostShuttingDown": _ = Task.Run(() => InvokePluginCallback("HostShuttingDown", _events.RaiseHostShuttingDown)); break;
                        case "event.focus": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EventFocusPayload>(envelope) is { } focus) _ = Task.Run(() => InvokePluginCallback("WindowFocusChanged", () => _events.RaiseFocus(focus.HasFocus))); break;
                        case "event.ui.invoke": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiInvokePayload>(envelope) is { } invoke && _invokeCallbacks.TryGetValue(invoke.Token, out var callback)) _ = Task.Run(() => InvokePluginCallback("UiInvoke", callback)); break;
                        case "event.ui.context.invoke": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiContextInvokePayload>(envelope) is { } cInvoke && _contextActions.TryGetValue(cInvoke.Token, out var cAction)) _ = Task.Run(() => InvokePluginCallback("ContextMenu", () => cAction(cInvoke.Context))); break;
                        case "event.ui.panel.invoke": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelInvokePayload>(envelope) is { } pInvoke && _panelCallbacks.TryGetValue(pInvoke.Token, out var pAction)) _ = Task.Run(() => InvokePluginCallback("Panel", () => pAction(pInvoke.Text, pInvoke.Checked, pInvoke.Index))); break;
                        case "event.clipboard.changed": _clipboard?.RaiseChanged(); break;
                        case "event.audio.complete": _audio?.RaisePlaybackComplete(); break;
                        case "event.notification.click": if (PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NotificationPayload>(envelope) is { Token: { } token } && _invokeCallbacks.TryGetValue(token, out var ncb)) _ = Task.Run(() => InvokePluginCallback("NotificationClick", ncb)); break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { foreach (var completion in _pending.Values) completion.TrySetException(ex); }
            finally { foreach (var completion in _pending.Values) completion.TrySetException(new IOException("Plugin sandbox connection closed.")); _lifetime.Cancel(); }
        }

        internal async Task<T> SendRequestAsync<T>(string op, object? payload, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginWorkerHost));
            string id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<PluginSandboxProtocol.Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await PluginSandboxProtocol.WriteAsync(_writer, op, id, payload, _writeLock, cancellationToken).ConfigureAwait(false);
                var envelope = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (envelope.Op == "error") throw new SecurityException(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ErrorPayload>(envelope)?.Error ?? "Plugin host request failed.");
                return PluginSandboxProtocol.GetPayload<T>(envelope) ?? throw new InvalidOperationException("Plugin host returned an invalid response.");
            }
            finally { _pending.TryRemove(id, out _); }
        }

        internal void RegisterInvoke(string token, Action callback) => _invokeCallbacks[token] = callback;
        internal void RemoveInvoke(string token) => _invokeCallbacks.Remove(token);
        internal void RegisterContext(string token, Func<ContextMenuContext, bool> query, Action<ContextMenuContext> action) { _contextQueries[token] = query; _contextActions[token] = action; }
        internal void RemoveContext(string token) { _contextQueries.Remove(token); _contextActions.Remove(token); }
        internal bool QueryContext(string token, ContextMenuContext context) => _contextQueries.TryGetValue(token, out var query) && query(context);
        internal void RegisterPanelCallback(string token, Action<string?, bool?, int?> callback) => _panelCallbacks[token] = callback;
        internal void RemovePanelCallback(string token) => _panelCallbacks.Remove(token);

        internal async Task ReportFatalAsync(Exception ex)
        {
            try
            {
                await PluginSandboxProtocol.WriteAsync(
                    _writer,
                    "worker.fatal",
                    Guid.NewGuid().ToString("N"),
                    new PluginSandboxProtocol.ErrorPayload(ex.ToString()),
                    _writeLock,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            try { _plugin?.Dispose(); } catch { }
            try { _loadContext?.Unload(); } catch { }
            foreach (var completion in _pending.Values) completion.TrySetException(new ObjectDisposedException(nameof(PluginWorkerHost)));
            _pending.Clear(); _writeLock.Dispose();
        }

        private abstract class RpcService
        {
            protected readonly PluginWorkerHost Host;
            protected RpcService(PluginWorkerHost host) => Host = host;
            protected void Demand(PluginPermission permission) { if (!Host.HasPermission(permission)) throw new SecurityException($"Permission '{string.Join(", ", PluginPermissionNames.ToNames(permission))}' has not been granted."); }
        }

        private sealed class WorkerBrowser : RpcService, IPluginBrowser
        {
            public WorkerBrowser(PluginWorkerHost host) : base(host) { }
            private PluginSandboxProtocol.BrowserState State() { Demand(PluginPermission.BrowserRead); return Host.SendRequestAsync<PluginSandboxProtocol.BrowserState>("browser.state", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public string? CurrentUrl => State().Url; public string CurrentTitle => State().Title;
            public void Navigate(string url) { Demand(PluginPermission.BrowserNavigation); Host.SendRequestAsync<object>("browser.navigate", new PluginSandboxProtocol.NavigatePayload(url), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Reload() { Demand(PluginPermission.BrowserNavigation); Host.SendRequestAsync<object>("browser.reload", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public void Back() { Demand(PluginPermission.BrowserNavigation); Host.SendRequestAsync<object>("browser.back", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public void Forward() { Demand(PluginPermission.BrowserNavigation); Host.SendRequestAsync<object>("browser.forward", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public void OpenWindow(string url) { Demand(PluginPermission.BrowserWindows); Host.SendRequestAsync<object>("browser.window", new PluginSandboxProtocol.NavigatePayload(url), CancellationToken.None).GetAwaiter().GetResult(); }
            public void ScrollTo(int x, int y) { Demand(PluginPermission.BrowserNavigation); Host.SendRequestAsync<object>("browser.scroll", new PluginSandboxProtocol.ScrollPayload(x, y), CancellationToken.None).GetAwaiter().GetResult(); }
            public float Zoom { get { Demand(PluginPermission.BrowserZoom); return State().Zoom; } set { Demand(PluginPermission.BrowserZoom); Host.SendRequestAsync<object>("browser.zoom.set", new PluginSandboxProtocol.ZoomPayload(value), CancellationToken.None).GetAwaiter().GetResult(); } }
            public (int Width, int Height) ViewportSize { get { Demand(PluginPermission.BrowserRead); var s = State(); return (s.Width, s.Height); } }
            public string? GetCookie(string name) { Demand(PluginPermission.BrowserCookies); return Host.SendRequestAsync<PluginSandboxProtocol.CookieGetPayload>("browser.cookie.get", new PluginSandboxProtocol.CookieGetPayload(name), CancellationToken.None).GetAwaiter().GetResult().Value; }
            public void SetCookie(string name, string value, CookieOptions options) { Demand(PluginPermission.BrowserCookies); Host.SendRequestAsync<object>("browser.cookie.set", new PluginSandboxProtocol.CookieSetPayload(name, value, options.Path, options.Domain, options.Expires, options.Secure), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Find(string text, bool caseSensitive, bool wrapAround) { Demand(PluginPermission.BrowserFind); Host.SendRequestAsync<object>("browser.find", new PluginSandboxProtocol.FindPayload(text, caseSensitive, wrapAround), CancellationToken.None).GetAwaiter().GetResult(); }
            public void FindNext() { Demand(PluginPermission.BrowserFind); Host.SendRequestAsync<object>("browser.find.next", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public void FindClear() { Demand(PluginPermission.BrowserFind); Host.SendRequestAsync<object>("browser.find.clear", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public byte[] CaptureViewport() { Demand(PluginPermission.BrowserScreenshot); return Convert.FromBase64String(Host.SendRequestAsync<PluginSandboxProtocol.ScreenshotReply>("browser.screenshot", null, CancellationToken.None).GetAwaiter().GetResult().PngBase64); }
        }

        private sealed class WorkerUi : RpcService, IPluginUi
        {
            public WorkerUi(PluginWorkerHost host) : base(host) { }
            public IDisposable AddFileMenuItem(string text, Action callback) => AddInvoke<PluginSandboxProtocol.UiMenuAddReply>("ui.menu.add", new PluginSandboxProtocol.UiMenuAddPayload(text), callback, token => new Lease(Host, token, "ui.menu.remove"));
            public IDisposable AddToolbarButton(string label, string tooltip, Action onClick) => AddInvoke<PluginSandboxProtocol.UiToolbarAddReply>("ui.toolbar.add", new PluginSandboxProtocol.UiToolbarAddPayload(label, tooltip), onClick, token => new Lease(Host, token, "ui.toolbar.remove"));
            private IDisposable AddInvoke<TReply>(string op, object payload, Action callback, Func<string, IDisposable> lease)
            {
                Demand(PluginPermission.UserInterface); object? replyObj = Host.SendRequestAsync<TReply>(op, payload, CancellationToken.None).GetAwaiter().GetResult(); string token = replyObj switch { PluginSandboxProtocol.UiMenuAddReply m => m.Token, PluginSandboxProtocol.UiToolbarAddReply t => t.Token, _ => throw new InvalidOperationException() }; Host.RegisterInvoke(token, callback); return lease(token);
            }
            public IDisposable AddContextMenuItem(string label, Func<ContextMenuContext, bool> shouldShow, Action<ContextMenuContext> onClick)
            {
                Demand(PluginPermission.UserInterface); var reply = Host.SendRequestAsync<PluginSandboxProtocol.UiContextAddReply>("ui.context.add", new PluginSandboxProtocol.UiContextAddPayload(label), CancellationToken.None).GetAwaiter().GetResult(); Host.RegisterContext(reply.Token, shouldShow, onClick); return new ContextLease(Host, reply.Token);
            }
            public void SetStatus(string text) { Demand(PluginPermission.UserInterface); Host.SendRequestAsync<object>("ui.status", new PluginSandboxProtocol.UiStatusPayload(text), CancellationToken.None).GetAwaiter().GetResult(); }
            public void SetProgress(double? fraction) { Demand(PluginPermission.UserInterface); Host.SendRequestAsync<object>("ui.progress", new PluginSandboxProtocol.UiProgressPayload(fraction), CancellationToken.None).GetAwaiter().GetResult(); }
            public void ShowMessage(string title, string message) { Demand(PluginPermission.UserInterface); Host.SendRequestAsync<object>("ui.message", new PluginSandboxProtocol.UiMessagePayload(title, message), CancellationToken.None).GetAwaiter().GetResult(); }
            public async Task<string?> ShowInputDialog(string title, string prompt, string? defaultValue = null) { Demand(PluginPermission.UserInterface); return (await Host.SendRequestAsync<PluginSandboxProtocol.UiInputReply>("ui.input", new PluginSandboxProtocol.UiInputPayload(title, prompt, defaultValue), CancellationToken.None).ConfigureAwait(false)).Value; }
            public IPluginPanel CreatePanel(string title) { Demand(PluginPermission.UiPanel); var reply = Host.SendRequestAsync<PluginSandboxProtocol.PanelCreateReply>("ui.panel.create", new PluginSandboxProtocol.PanelCreatePayload(title), CancellationToken.None).GetAwaiter().GetResult(); return new WorkerPanel(Host, reply.Token); }
            private sealed class Lease : IDisposable { private PluginWorkerHost? _host; private readonly string _token; private readonly string _op; public Lease(PluginWorkerHost host, string token, string op) { _host = host; _token = token; _op = op; } public void Dispose() { var h = Interlocked.Exchange(ref _host, null); if (h == null) return; h.RemoveInvoke(_token); try { h.SendRequestAsync<object>(_op, new PluginSandboxProtocol.UiMenuRemovePayload(_token), CancellationToken.None).GetAwaiter().GetResult(); } catch { } } }
            private sealed class ContextLease : IDisposable { private PluginWorkerHost? _host; private readonly string _token; public ContextLease(PluginWorkerHost host, string token) { _host = host; _token = token; } public void Dispose() { var h = Interlocked.Exchange(ref _host, null); if (h == null) return; h.RemoveContext(_token); try { h.SendRequestAsync<object>("ui.context.remove", new PluginSandboxProtocol.UiMenuRemovePayload(_token), CancellationToken.None).GetAwaiter().GetResult(); } catch { } } }
        }

        private sealed class WorkerPanel : IPluginPanel
        {
            private PluginWorkerHost? _host; private readonly string _panelToken;
            public WorkerPanel(PluginWorkerHost host, string panelToken) { _host = host; _panelToken = panelToken; }
            private IPluginWidget Add(string kind, string? text = null, string? placeholder = null, bool? check = null, IEnumerable<string>? items = null, Action<string?, bool?, int?>? callback = null)
            {
                var host = _host ?? throw new ObjectDisposedException(nameof(WorkerPanel)); string token = host.SendRequestAsync<PluginSandboxProtocol.PanelWidgetReply>("ui.panel.widget.add", new PluginSandboxProtocol.PanelWidgetPayload(_panelToken, "", kind, text, placeholder, check, items?.ToArray()), CancellationToken.None).GetAwaiter().GetResult().WidgetToken;
                if (callback != null) host.RegisterPanelCallback(token, callback); return new WorkerWidget(host, token);
            }
            public IPluginWidget AddLabel(string text) => Add("label", text);
            public IPluginWidget AddButton(string label, Action onClick) => Add("button", label, callback: (_, _, _) => onClick());
            public IPluginWidget AddTextBox(string placeholder, Action<string> onChange) => Add("textbox", placeholder: placeholder, callback: (text, _, _) => onChange(text ?? string.Empty));
            public IPluginWidget AddCheckBox(string label, bool initial, Action<bool> onChange) => Add("checkbox", label, check: initial, callback: (_, check, _) => onChange(check == true));
            public IPluginWidget AddListBox(IEnumerable<string> items, Action<int> onSelect) => Add("listbox", items: items, callback: (_, _, index) => { if (index.HasValue) onSelect(index.Value); });
            public void Clear() { _host?.SendRequestAsync<object>("ui.panel.clear", new PluginSandboxProtocol.PanelTokenPayload(_panelToken), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Show() { _host?.SendRequestAsync<object>("ui.panel.show", new PluginSandboxProtocol.PanelTokenPayload(_panelToken), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Hide() { _host?.SendRequestAsync<object>("ui.panel.hide", new PluginSandboxProtocol.PanelTokenPayload(_panelToken), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Dispose() { var h = Interlocked.Exchange(ref _host, null); if (h == null) return; try { h.SendRequestAsync<object>("ui.panel.dispose", new PluginSandboxProtocol.PanelTokenPayload(_panelToken), CancellationToken.None).GetAwaiter().GetResult(); } catch { } }
        }

        private sealed class WorkerWidget : IPluginWidget
        {
            private PluginWorkerHost? _host; private readonly string _token; public WorkerWidget(PluginWorkerHost host, string token) { _host = host; _token = token; }
            public void SetText(string text) { _host?.SendRequestAsync<object>("ui.panel.widget.set", new PluginSandboxProtocol.PanelWidgetSetPayload(_token, Text: text), CancellationToken.None).GetAwaiter().GetResult(); }
            public void SetEnabled(bool enabled) { _host?.SendRequestAsync<object>("ui.panel.widget.set", new PluginSandboxProtocol.PanelWidgetSetPayload(_token, Enabled: enabled), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Dispose() { var h = Interlocked.Exchange(ref _host, null); if (h != null) h.RemovePanelCallback(_token); }
        }

        private sealed class WorkerStorage : RpcService, IPluginStorage
        {
            public WorkerStorage(PluginWorkerHost host) : base(host) { }
            public string? Get(string key) { Demand(PluginPermission.Storage); return Host.SendRequestAsync<PluginSandboxProtocol.StorageKeyPayload>("storage.get", new PluginSandboxProtocol.StorageKeyPayload(key), CancellationToken.None).GetAwaiter().GetResult().Value; }
            public void Set(string key, string value) { Demand(PluginPermission.Storage); Host.SendRequestAsync<object>("storage.set", new PluginSandboxProtocol.StorageKeyPayload(key, value), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Delete(string key) { Demand(PluginPermission.Storage); Host.SendRequestAsync<object>("storage.delete", new PluginSandboxProtocol.StorageKeyPayload(key), CancellationToken.None).GetAwaiter().GetResult(); }
            public IReadOnlyDictionary<string, string> Snapshot() { Demand(PluginPermission.Storage); return Host.SendRequestAsync<PluginSandboxProtocol.StorageSnapshotPayload>("storage.snapshot", null, CancellationToken.None).GetAwaiter().GetResult().Values; }
            public string[] ListKeys(string? prefix = null) { Demand(PluginPermission.Storage); return Host.SendRequestAsync<PluginSandboxProtocol.StorageKeysPayload>("storage.keys", new PluginSandboxProtocol.StorageKeyPayload(prefix ?? ""), CancellationToken.None).GetAwaiter().GetResult().Keys; }
            public T? GetObject<T>(string key) { Demand(PluginPermission.Storage); var json = Host.SendRequestAsync<PluginSandboxProtocol.StorageObjectPayload>("storage.get.object", new PluginSandboxProtocol.StorageObjectPayload(key), CancellationToken.None).GetAwaiter().GetResult().Json; return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, PluginSandboxProtocol.JsonOptions); }
            public void SetObject<T>(string key, T value) { Demand(PluginPermission.Storage); Host.SendRequestAsync<object>("storage.set.object", new PluginSandboxProtocol.StorageObjectPayload(key, JsonSerializer.Serialize(value, PluginSandboxProtocol.JsonOptions)), CancellationToken.None).GetAwaiter().GetResult(); }
            public long GetUsedBytes() { Demand(PluginPermission.Storage); return Host.SendRequestAsync<PluginSandboxProtocol.StorageUsedPayload>("storage.used", null, CancellationToken.None).GetAwaiter().GetResult().Bytes; }
        }

        private sealed class WorkerNetwork : RpcService, IPluginNetwork
        {
            public WorkerNetwork(PluginWorkerHost host) : base(host) { }
            public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) => await SendString("GET", url, null, null, cancellationToken).ConfigureAwait(false);
            public async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken = default) => await SendBytes("GET", url, null, null, cancellationToken).ConfigureAwait(false);
            public async Task<string> PostStringAsync(string url, string body, string contentType, CancellationToken cancellationToken = default) => await SendString("POST", url, Encoding.UTF8.GetBytes(body ?? string.Empty), contentType, cancellationToken).ConfigureAwait(false);
            public async Task<byte[]> PostBytesAsync(string url, byte[] body, string contentType, CancellationToken cancellationToken = default) => await SendBytes("POST", url, body, contentType, cancellationToken).ConfigureAwait(false);
            public async Task<string> SendAsync(HttpPluginRequest request, CancellationToken cancellationToken = default) => await SendString(request.Method, request.Url, request.Body, request.ContentType, cancellationToken, request.Headers).ConfigureAwait(false);
            private async Task<string> SendString(string method, string url, byte[]? body, string? contentType, CancellationToken ct, IReadOnlyDictionary<string,string>? headers = null) { Demand(PluginPermission.Network); var reply = await Host.SendRequestAsync<PluginSandboxProtocol.NetworkReply>("network.get", new PluginSandboxProtocol.NetworkRequestPayload(method, url, headers == null ? null : new Dictionary<string,string>(headers), body == null ? null : Convert.ToBase64String(body), contentType), ct).ConfigureAwait(false); if (!reply.Success) throw new InvalidOperationException(reply.Error); return reply.Text ?? string.Empty; }
            private async Task<byte[]> SendBytes(string method, string url, byte[]? body, string? contentType, CancellationToken ct, IReadOnlyDictionary<string,string>? headers = null) { Demand(PluginPermission.Network); var merged = headers == null ? new Dictionary<string,string>() : new Dictionary<string,string>(headers); if (contentType != null) merged["Content-Type"] = contentType; var reply = await Host.SendRequestAsync<PluginSandboxProtocol.NetworkReply>("network.bytes", new PluginSandboxProtocol.NetworkRequestPayload(method, url, merged, body == null ? null : Convert.ToBase64String(body), contentType), ct).ConfigureAwait(false); if (!reply.Success) throw new InvalidOperationException(reply.Error); return Convert.FromBase64String(reply.BytesBase64 ?? ""); }
        }

        private sealed class WorkerFileSystem : RpcService, IPluginFileSystem
        {
            public WorkerFileSystem(PluginWorkerHost host) : base(host) { }
            public string DataDirectory => "plugin-data";
            public byte[] ReadAllBytes(string relativePath) { Demand(PluginPermission.FileSystem); return Convert.FromBase64String(Host.SendRequestAsync<PluginSandboxProtocol.FilePayload>("filesystem.read.bytes", new PluginSandboxProtocol.FilePayload(relativePath), CancellationToken.None).GetAwaiter().GetResult().BytesBase64 ?? ""); }
            public string ReadAllText(string relativePath) { Demand(PluginPermission.FileSystem); return Host.SendRequestAsync<PluginSandboxProtocol.FilePayload>("filesystem.read.text", new PluginSandboxProtocol.FilePayload(relativePath), CancellationToken.None).GetAwaiter().GetResult().Text ?? ""; }
            public void WriteAllBytes(string relativePath, byte[] content) { Demand(PluginPermission.FileSystem); Host.SendRequestAsync<object>("filesystem.write.bytes", new PluginSandboxProtocol.FilePayload(relativePath, BytesBase64: Convert.ToBase64String(content)), CancellationToken.None).GetAwaiter().GetResult(); }
            public void WriteAllText(string relativePath, string content) { Demand(PluginPermission.FileSystem); Host.SendRequestAsync<object>("filesystem.write.text", new PluginSandboxProtocol.FilePayload(relativePath, content), CancellationToken.None).GetAwaiter().GetResult(); }
            public bool Exists(string relativePath) { Demand(PluginPermission.FileSystem); return string.Equals(Host.SendRequestAsync<PluginSandboxProtocol.FilePayload>("filesystem.exists", new PluginSandboxProtocol.FilePayload(relativePath), CancellationToken.None).GetAwaiter().GetResult().Text, "true", StringComparison.OrdinalIgnoreCase); }
            public string[] ListFiles(string? subdirectory = null) { Demand(PluginPermission.FileSystem); return Host.SendRequestAsync<PluginSandboxProtocol.FileListPayload>("filesystem.list", new PluginSandboxProtocol.FileListPayload(subdirectory, Array.Empty<string>()), CancellationToken.None).GetAwaiter().GetResult().Files; }
            public void Delete(string relativePath) { Demand(PluginPermission.FileSystem); Host.SendRequestAsync<object>("filesystem.delete", new PluginSandboxProtocol.FilePayload(relativePath), CancellationToken.None).GetAwaiter().GetResult(); }
            public void CreateDirectory(string relativePath) { Demand(PluginPermission.FileSystem); Host.SendRequestAsync<object>("filesystem.mkdir", new PluginSandboxProtocol.FilePayload(relativePath), CancellationToken.None).GetAwaiter().GetResult(); }
        }

        private sealed class PluginClipboard : RpcService, IPluginClipboard
        {
            public PluginClipboard(PluginWorkerHost host) : base(host) { }
            public string? GetText() { Demand(PluginPermission.Clipboard); return Host.SendRequestAsync<PluginSandboxProtocol.ClipboardPayload>("clipboard.get", null, CancellationToken.None).GetAwaiter().GetResult().Text; }
            public void SetText(string text) { Demand(PluginPermission.Clipboard); Host.SendRequestAsync<object>("clipboard.set", new PluginSandboxProtocol.ClipboardPayload(Text: text), CancellationToken.None).GetAwaiter().GetResult(); }
            public event EventHandler? ClipboardChanged;
            internal void RaiseChanged() => ClipboardChanged?.Invoke(this, EventArgs.Empty);
            public byte[]? GetImage() { Demand(PluginPermission.Clipboard); var b = Host.SendRequestAsync<PluginSandboxProtocol.ClipboardPayload>("clipboard.image.get", null, CancellationToken.None).GetAwaiter().GetResult().PngBase64; return string.IsNullOrEmpty(b) ? null : Convert.FromBase64String(b); }
            public void SetImage(byte[] pngBytes) { Demand(PluginPermission.Clipboard); Host.SendRequestAsync<object>("clipboard.image.set", new PluginSandboxProtocol.ClipboardPayload(PngBase64: Convert.ToBase64String(pngBytes)), CancellationToken.None).GetAwaiter().GetResult(); }
        }

        private sealed class PluginAudio : RpcService, IPluginAudio
        {
            private bool _loop;
            public PluginAudio(PluginWorkerHost host) : base(host) { }
            public async Task PlayFileAsync(string path) { Demand(PluginPermission.AudioPlayback); await Host.SendRequestAsync<object>("audio.play", new PluginSandboxProtocol.AudioPlayPayload(path, _loop), CancellationToken.None).ConfigureAwait(false); }
            public void Stop() { Demand(PluginPermission.AudioPlayback); Host.SendRequestAsync<object>("audio.stop", null, CancellationToken.None).GetAwaiter().GetResult(); }
            public bool IsPlaying { get { Demand(PluginPermission.AudioPlayback); return Host.SendRequestAsync<PluginSandboxProtocol.AudioStatePayload>("audio.state", null, CancellationToken.None).GetAwaiter().GetResult().IsPlaying; } }
            public float Volume { get { Demand(PluginPermission.AudioPlayback); return Host.SendRequestAsync<PluginSandboxProtocol.AudioStatePayload>("audio.state", null, CancellationToken.None).GetAwaiter().GetResult().Volume; } set { Demand(PluginPermission.AudioPlayback); Host.SendRequestAsync<object>("audio.volume.set", new PluginSandboxProtocol.AudioStatePayload(false, value, false, null), CancellationToken.None).GetAwaiter().GetResult(); } }
            public bool Loop { get { Demand(PluginPermission.AudioPlayback); return _loop; } set { Demand(PluginPermission.AudioPlayback); _loop = value; Host.SendRequestAsync<object>("audio.loop.set", new PluginSandboxProtocol.AudioStatePayload(false, 1f, value, null), CancellationToken.None).GetAwaiter().GetResult(); } }
            public event EventHandler? PlaybackComplete;
            internal void RaisePlaybackComplete() => PlaybackComplete?.Invoke(this, EventArgs.Empty);
        }

        private sealed class WorkerNotifications : RpcService, IPluginNotifications
        {
            public WorkerNotifications(PluginWorkerHost host) : base(host) { }
            public void Show(string title, string body) { Demand(PluginPermission.Notifications); Host.SendRequestAsync<object>("notifications.show", new PluginSandboxProtocol.NotificationPayload(title, body), CancellationToken.None).GetAwaiter().GetResult(); }
            public void Show(string title, string body, Action onClick) { Demand(PluginPermission.Notifications); string token = Guid.NewGuid().ToString("N"); Host.RegisterInvoke(token, onClick); Host.SendRequestAsync<object>("notifications.show", new PluginSandboxProtocol.NotificationPayload(title, body, token), CancellationToken.None).GetAwaiter().GetResult(); }
        }

        private sealed class WorkerDialogs : RpcService, IPluginDialogs
        {
            public WorkerDialogs(PluginWorkerHost host) : base(host) { }
            public async Task<string?> OpenFilePickerAsync(string title, string filter) { Demand(PluginPermission.Dialogs); return (await Host.SendRequestAsync<PluginSandboxProtocol.DialogOpenReply>("dialogs.open", new PluginSandboxProtocol.DialogOpenPayload(title, filter), CancellationToken.None).ConfigureAwait(false)).RelativePath; }
            public async Task<bool> SaveFilePickerAsync(string sandboxRelativePath, string suggestedFilename, string filter) { Demand(PluginPermission.Dialogs); return (await Host.SendRequestAsync<PluginSandboxProtocol.DialogSaveReply>("dialogs.save", new PluginSandboxProtocol.DialogSavePayload(sandboxRelativePath, suggestedFilename, filter), CancellationToken.None).ConfigureAwait(false)).Success; }
        }

        private sealed class WorkerLogger : IPluginLogger
        {
            private readonly PluginWorkerHost _host; public WorkerLogger(PluginWorkerHost host) => _host = host;
            public void Info(string message) => Send("info", message, null); public void Warn(string message) => Send("warn", message, null); public void Error(string message, Exception? exception = null) => Send("error", message, exception?.ToString());
            private void Send(string level, string message, string? exception) { try { _host.SendRequestAsync<object>("log", new PluginSandboxProtocol.LogPayload(level, message, exception), CancellationToken.None).GetAwaiter().GetResult(); } catch { } }
        }

        private sealed class PluginEventsProxy : IPluginEvents
        {
            public event EventHandler<PluginNavigationEventArgs>? Navigated; public event EventHandler<PluginPageEventArgs>? PageLoaded; public event EventHandler? HostShuttingDown; public event EventHandler<FocusEventArgs>? WindowFocusChanged;
            public void RaiseNavigated(PluginNavigationEventArgs args) => Navigated?.Invoke(this, args); public void RaisePageLoaded(PluginPageEventArgs args) => PageLoaded?.Invoke(this, args); public void RaiseHostShuttingDown() => HostShuttingDown?.Invoke(this, EventArgs.Empty); public void RaiseFocus(bool hasFocus) => WindowFocusChanged?.Invoke(this, new FocusEventArgs(hasFocus));
            public IDisposable CreateTimer(TimeSpan interval, Action callback) { var timer = new System.Threading.Timer(_ => { try { callback(); } catch { } }, null, interval, interval); return new TimerLease(timer); }
            private sealed class TimerLease : IDisposable { private System.Threading.Timer? _timer; public TimerLease(System.Threading.Timer timer) => _timer = timer; public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose(); }
        }
    }
}
