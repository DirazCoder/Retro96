using System.Text;
using System.IO.Pipes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows.Forms;
using Retro96;
using Retro96.Engine.Dom;

namespace Retro96.Plugins;

/// <summary>
/// Host-side controller for one sandboxed plugin process. The plugin process
/// has no direct reference to the browser window; all privileged operations
/// cross this permission-checked broker.
/// </summary>
internal sealed class PluginSandboxSession : IDisposable
{
    private readonly Form1 _browser;
    private readonly PluginManager _manager;
    private readonly PluginManager.PluginRecord _record;
    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, IDisposable> _menuItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _toolbarItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _contextItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Form1.PluginPanelHost> _panels = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PluginSandboxProtocol.Envelope>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PluginSandboxProtocol.BinaryEnvelope>> _pendingBinary = new();
    private readonly Dictionary<string, HostEmbeddedRegistration> _embeddedRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostEmbeddedInstance> _embeddedInstances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostStreamState> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _protocols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contentTransforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pageStyles = new();
    private readonly Dictionary<string, string> _omniboxKeywords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisposable> _extraUiItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginKeyboardShortcut> _shortcuts = new(StringComparer.Ordinal);
    private readonly List<PluginNetworkRule> _networkRules = new();
    private NamedPipeServerStream? _pipe;
    private WindowsSecurity.WorkerProcess? _worker;
    private string? _profileName;
    private int _disposed;
    internal bool ExpectedShutdown { get; set; }

    public PluginSandboxSession(Form1 browser, PluginManager manager, PluginManager.PluginRecord record)
    {
        _browser = browser;
        _manager = manager;
        _record = record;
        _rootDirectory = record.Directory;
    }

    public async Task StartAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginSandboxSession));
        string pipeName = "Retro96.Plugin." + Guid.NewGuid().ToString("N");
        var profile = WindowsSecurity.CreateAppContainer(TrustMode.High);
        _profileName = profile.ProfileName;

        try
        {
            _pipe = CreatePipe(pipeName, profile.Sid);
            string runtimeDir = WindowsSecurity.PrepareWorkerRuntime(AppContext.BaseDirectory, profile.Sid);
            string pluginPayload = Path.Combine(runtimeDir, "PluginPayload", _record.Manifest.Id);
            CopyDirectory(_rootDirectory, pluginPayload);

            string workerFileName = Path.GetFileName(Environment.ProcessPath ?? "Retro96.exe");
            if (!workerFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                workerFileName = "Retro96.exe";
            string workerExecutable = Path.Combine(runtimeDir, workerFileName);
            if (!File.Exists(workerExecutable))
                throw new FileNotFoundException("Retro96 worker executable was not found for the plugin sandbox.", workerExecutable);

            _worker = WindowsSecurity.StartWorker(
                TrustMode.High,
                pipeName,
                profile.Sid,
                profile.ProfileName,
                workerExecutable,
                runtimeDir,
                useLpac: false,
                ownsRuntimeDirectory: true,
                cpuRatePercent: 25,
                workerArguments: $"--plugin-worker \"{pipeName}\" \"PluginPayload\\{_record.Manifest.Id}\"");

            await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);

            _ = Task.Run(RunLoopAsync);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool TryGetEmbedRegistrationToken(string mimeType, out string token)
    {
        lock (_embeddedRegistrations)
        {
            var match = _embeddedRegistrations.FirstOrDefault(p => p.Value.MimeTypes.Any(t => t.Equals(mimeType, StringComparison.OrdinalIgnoreCase)));
            token = match.Key ?? string.Empty;
            return match.Key != null;
        }
    }

    internal async Task<EmbeddedHostInstance> CreateEmbeddedInstanceAsync(
        string registrationToken, string mimeType, string sourceUrl, string? currentUrl, string userAgent,
        IReadOnlyDictionary<string, string> parameters, int width, int height,
        Stream stream, bool canSeek, long? length, CancellationToken cancellationToken)
    {
        Demand(PluginPermission.EmbedRenderer);
        HostEmbeddedRegistration registration;
        lock (_embeddedRegistrations)
        {
            if (!_embeddedRegistrations.TryGetValue(registrationToken, out registration!))
                throw new InvalidOperationException("Embedded content registration is unavailable.");
            if (!registration.MimeTypes.Any(t => t.Equals(mimeType, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityException("Embedded MIME type is not registered by the plugin.");
        }

        string instanceToken = Guid.NewGuid().ToString("N");
        string streamToken = Guid.NewGuid().ToString("N");
        var streamState = new HostStreamState(this, streamToken, stream, canSeek, length, PluginPermission.EmbedRenderer);
        lock (_streams) _streams[streamToken] = streamState;
        lock (_embeddedInstances) _embeddedInstances[instanceToken] = new HostEmbeddedInstance(instanceToken, streamToken, sourceUrl);
        try
        {
            var payload = new PluginSandboxProtocol.EmbedCreatePayload(
                registrationToken, instanceToken, mimeType, sourceUrl, currentUrl, userAgent,
                new Dictionary<string, string>(parameters, StringComparer.Ordinal), width, height,
                canSeek, length, streamToken);
            var reply = await SendRequestAsync<PluginSandboxProtocol.EmbedCreateReply>(
                "embed.create", payload, cancellationToken).ConfigureAwait(false);
            lock (_embeddedInstances)
            {
                if (_embeddedInstances.TryGetValue(instanceToken, out var state))
                    state.ScriptName = reply.ScriptName;
            }
            return new EmbeddedHostInstance(this, instanceToken, streamToken, reply.ScriptName, reply.ScriptMethods);
        }
        catch
        {
            lock (_embeddedInstances) _embeddedInstances.Remove(instanceToken);
            RemoveStream(streamToken);
            throw;
        }
    }

    internal void AttachEmbeddedElement(EmbeddedHostInstance instance, DomElement element)
    {
        instance.Element = element;
        lock (_embeddedInstances)
        {
            if (_embeddedInstances.TryGetValue(instance.InstanceToken, out var state))
                state.Element = element;
        }
    }

    internal async Task<EmbeddedFrameBuffer> RenderEmbeddedAsync(EmbeddedHostInstance instance, EmbeddedRenderRequest request, CancellationToken cancellationToken)
    {
        Demand(PluginPermission.EmbedRenderer);
        if (request.IsPrint) Demand(PluginPermission.EmbedPrint);
        ValidateRenderRequest(request);
        var reply = await SendBinaryRequestAsync("embed.render",
            new PluginSandboxProtocol.EmbedRenderPayload(instance.InstanceToken, request.Width, request.Height, request.Stride, request.DpiX, request.DpiY, request.IsPrint),
            cancellationToken).ConfigureAwait(false);
        var payload = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedFramePayload>(reply)
            ?? throw new InvalidDataException("Embedded plugin frame metadata is missing.");
        if (payload.InstanceToken != instance.InstanceToken || payload.Width != request.Width || payload.Height != request.Height || payload.Stride != request.Stride)
            throw new InvalidDataException("Embedded plugin frame metadata does not match the render request.");
        return new EmbeddedFrameBuffer(payload.Width, payload.Height, payload.Stride, reply.Data).Validate();
    }

    internal Task SendEmbeddedInputAsync(EmbeddedHostInstance instance, EmbeddedInputEvent inputEvent, CancellationToken cancellationToken = default)
    {
        Demand(PluginPermission.EmbedRenderer);
        RunOnUi(() => _browser.ActivatePluginEmbeddedAudio("embed:" + _record.Manifest.Id + ":" + instance.InstanceToken));
        return SendBinaryAsync("embed.input", Guid.NewGuid().ToString("N"),
            new PluginSandboxProtocol.EmbedInputPayload(instance.InstanceToken, inputEvent), Array.Empty<byte>(), cancellationToken);
    }

    internal Task<JsValue> CallEmbeddedScriptAsync(EmbeddedHostInstance instance, string name, IReadOnlyList<JsValue> args, CancellationToken cancellationToken = default)
    {
        Demand(PluginPermission.EmbedScript);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A script method name is required.", nameof(name));
        var wire = args.Select(PluginJsValueCodec.ToWire).ToArray();
        return CallEmbeddedScriptCoreAsync(instance, name, wire, cancellationToken);
    }

    private async Task<JsValue> CallEmbeddedScriptCoreAsync(EmbeddedHostInstance instance, string name, PluginSandboxProtocol.JsValueWire[] args, CancellationToken cancellationToken)
    {
        var reply = await SendRequestAsync<PluginSandboxProtocol.EmbedScriptCallReply>(
            "embed.script.call", new PluginSandboxProtocol.EmbedScriptCallPayload(instance.InstanceToken, name, args), cancellationToken).ConfigureAwait(false);
        return PluginJsValueCodec.FromWire(reply.Value);
    }

    private void ValidateRenderRequest(EmbeddedRenderRequest request)
    {
        if (request.Width <= 0 || request.Height <= 0 || request.Stride < checked(request.Width * 4))
            throw new ArgumentOutOfRangeException(nameof(request));
        if (checked(request.Stride * request.Height) > PluginSandboxProtocol.MaxBinaryFrameBytes)
            throw new InvalidOperationException("Embedded render surface is too large.");
        if (request.DpiX <= 0 || request.DpiY <= 0) throw new ArgumentOutOfRangeException(nameof(request));
    }

    public void RaiseNavigated(string url) => SendEvent("event.navigated", new PluginSandboxProtocol.EventNavigatedPayload(url));
    public void RaisePageLoaded(string url, string title) => SendEvent("event.pageLoaded", new PluginSandboxProtocol.EventPageLoadedPayload(url, title));
    public void RaiseNavigationFailed(string url, string message) => SendEvent("event.navigationFailed", new PluginSandboxProtocol.EventNavigationFailedPayload(url, message));
    public void RaiseTitleChanged(string url, string title) => SendEvent("event.titleChanged", new PluginSandboxProtocol.EventTitleChangedPayload(url, title));
    public void RaiseLoadProgress(string url, double fraction) => SendEvent("event.loadProgress", new PluginSandboxProtocol.EventLoadProgressPayload(url, fraction));
    public void RaiseZoomChanged(float zoom) => SendEvent("event.zoomChanged", new PluginSandboxProtocol.EventZoomChangedPayload(zoom));
    internal void RaiseEmbeddedVisibility(string instanceToken, bool visible)
    {
        if (_record.HasPermission(PluginPermission.EmbedExtras)) SendEvent("event.embed.visibility", new PluginSandboxProtocol.EventEmbedVisibilityPayload(instanceToken, visible));
    }
    internal void RaiseEmbeddedPause(string instanceToken, bool paused)
    {
        if (_record.HasPermission(PluginPermission.EmbedExtras)) SendEvent("event.embed.pause", new PluginSandboxProtocol.EventEmbedPausePayload(instanceToken, paused));
    }
    internal void RaiseEmbeddedResize(string instanceToken, int width, int height)
    {
        if (_record.HasPermission(PluginPermission.EmbedExtras)) SendEvent("event.embed.resize", new PluginSandboxProtocol.EventEmbedResizePayload(instanceToken, Math.Clamp(width, 1, 4096), Math.Clamp(height, 1, 4096)));
    }
    public void RaiseHostShuttingDown() => SendEvent("event.hostShuttingDown", new { });
    public void RaiseWindowFocusChanged(bool hasFocus) => SendEvent("event.focus", new PluginSandboxProtocol.EventFocusPayload(hasFocus));
    public void RaiseClipboardChanged() => SendEvent("event.clipboard.changed", new PluginSandboxProtocol.EventClipboardPayload());
    public void RaiseAudioComplete() => SendEvent("event.audio.complete", new PluginSandboxProtocol.EventPlaybackPayload());
    public void PushGrantedPermissions(PluginPermission permissions) => SendEvent("event.permissions.changed", new PluginSandboxProtocol.EventPermissionsPayload((ulong)permissions));
    public void PushSettingChanged(string name, string value) => SendEvent("event.setting.changed", new PluginSandboxProtocol.EventPluginSettingChangedPayload(name, value));

    private void SendEvent(string op, object payload)
    {
        if (_pipe == null || Volatile.Read(ref _disposed) != 0) return;
        _ = WriteSafeAsync(op, Guid.NewGuid().ToString("N"), payload);
    }

    private async Task WriteSafeAsync(string op, string id, object payload)
    {
        try
        {
            await PluginSandboxProtocol.WriteAsync(_pipe!, op, id, payload, _writeLock, _lifetime.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await PluginSandboxProtocol.ReadAsync(_pipe!, _lifetime.Token).ConfigureAwait(false);
                if (message == null) break;
                switch (message)
                {
                    case PluginSandboxProtocol.JsonMessage json:
                        var envelope = json.Envelope;
                        if (envelope.Op is "response" or "error")
                        {
                            if (_pending.TryGetValue(envelope.Id, out var completion))
                                completion.TrySetResult(envelope);
                            continue;
                        }
                        await HandleAsync(envelope).ConfigureAwait(false);
                        break;
                    case PluginSandboxProtocol.BinaryMessage binary:
                        if ((binary.Envelope.Op == "embed.frame" || binary.Envelope.Op == "protocol.response" || binary.Envelope.Op == "content.transform.response") && _pendingBinary.TryGetValue(binary.Envelope.Id, out var binaryCompletion))
                            binaryCompletion.TrySetResult(binary.Envelope);
                        else
                            await HandleBinaryAsync(binary.Envelope).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            _record.Error = ex.Message;
        }
        finally
        {
            _ready.TrySetException(new InvalidOperationException("Plugin sandbox disconnected."));
            if (!ExpectedShutdown) _manager.RecordSandboxCrash(_record, _record.Error ?? "Plugin worker disconnected.");
        }
    }

    private async Task HandleAsync(PluginSandboxProtocol.Envelope envelope)
    {
        try
        {
            switch (envelope.Op)
            {
                case "hello":
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.HelloReply(
                        string.Equals(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.HelloPayload>(envelope)?.PluginId,
                            _record.Manifest.Id, StringComparison.OrdinalIgnoreCase),
                        JsonSerializer.Serialize(_record.Manifest, PluginManifestJsonContext.Default.PluginManifest),
                        (ulong)_record.GrantedPermissions,
                        string.Equals(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.HelloPayload>(envelope)?.PluginId,
                            _record.Manifest.Id, StringComparison.OrdinalIgnoreCase) ? "" : "Plugin id mismatch."));
                    break;

                case "ready":
                    var ready = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ReadyPayload>(envelope);
                    if (ready?.Ready == true) _ready.TrySetResult(true);
                    else _ready.TrySetException(new InvalidOperationException(ready?.Error ?? "Plugin initialization failed."));
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ReadyPayload(ready?.Ready == true, ready?.Error ?? ""));
                    break;

                case "permission.request":
                    {
                        var request = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PermissionRequestPayload>(envelope) ?? throw new InvalidDataException();
                        bool granted = await _manager.RequestPermissionAsync(_record, request.Name, _lifetime.Token).ConfigureAwait(true);
                        await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.PermissionRequestReply(granted)).ConfigureAwait(false);
                        break;
                    }
                case "host.info":
                    {
                        string[] supported = new[] { "host.info", "browser", "ui", "storage", "network", "filesystem", "clipboard", "events", "audio", "notifications", "dialogs", "embeds", "logger", "permissions", "page.read", "network.rules", "protocol", "content.transform", "page.style", "tabs", "history", "bookmarks", "downloads", "omnibox", "settings", "ui.extras", "embed.audio", "embed.extras" };
                        var info = new PluginSandboxProtocol.HostInfoReply(
                            Application.ProductVersion, Retro96PluginApi.ApiVersion, supported, "classic",
                            System.Globalization.CultureInfo.CurrentUICulture.Name, Math.Max(96, _browser.DeviceDpi));
                        await ReplyAsync(envelope.Id, "response", info).ConfigureAwait(false);
                        break;
                    }
                case "network.rules.set":
                    Demand(PluginPermission.NetworkRules);
                    var networkRules = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkRulesPayload>(envelope) ?? throw new InvalidDataException();
                    SetNetworkRules(networkRules.Rules);
                    await ReplyOkAsync(envelope);
                    break;
                case "network.rules.clear":
                    Demand(PluginPermission.NetworkRules);
                    ClearNetworkRules();
                    await ReplyOkAsync(envelope);
                    break;
                case "protocol.register":
                    Demand(PluginPermission.Protocol);
                    var protocolRegister = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ProtocolRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    ValidateProtocolScheme(protocolRegister.Scheme);
                    _manager.RegisterPluginProtocol(_record, protocolRegister.Scheme, protocolRegister.Token);
                    lock (_protocols) _protocols[protocolRegister.Scheme] = protocolRegister.Token;
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ProtocolRegisterReply(protocolRegister.Token));
                    break;
                case "protocol.unregister":
                    Demand(PluginPermission.Protocol);
                    var protocolUnregister = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ProtocolRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    _manager.UnregisterPluginProtocol(_record, protocolUnregister.Scheme, protocolUnregister.Token);
                    UnregisterProtocol(protocolUnregister.Scheme, protocolUnregister.Token);
                    await ReplyOkAsync(envelope);
                    break;
                case "content.transform.register":
                    Demand(PluginPermission.ContentTransform);
                    var transformRegister = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ContentTransformRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    ValidateContentType(transformRegister.ContentType);
                    lock (_contentTransforms) _contentTransforms[transformRegister.ContentType] = transformRegister.Token;
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ContentTransformRegisterReply(transformRegister.Token));
                    break;
                case "content.transform.unregister":
                    Demand(PluginPermission.ContentTransform);
                    var transformUnregister = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ContentTransformRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    UnregisterContentTransform(transformUnregister.ContentType, transformUnregister.Token);
                    await ReplyOkAsync(envelope);
                    break;
                case "page.style.set":
                    Demand(PluginPermission.PageStyle);
                    var styleSet = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PageStylePayload>(envelope) ?? throw new InvalidDataException();
                    SetPageStyle(styleSet.Token, SanitizePluginCss(styleSet.Css));
                    await ReplyOkAsync(envelope);
                    break;
                case "page.style.remove":
                    Demand(PluginPermission.PageStyle);
                    var styleRemove = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PageStyleRemovePayload>(envelope) ?? throw new InvalidDataException();
                    RemovePageStyle(styleRemove.Token);
                    await ReplyOkAsync(envelope);
                    break;
                case "tabs.list":
                    Demand(PluginPermission.Tabs);
                    var tabs = RunOnUi(() => new[] { new PluginTabInfo("active", _browser.PluginCurrentUrl ?? "about:blank", _browser.PluginCurrentTitle ?? "", true) });
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.TabsListReply(tabs));
                    break;
                case "tabs.beforeNavigate":
                    Demand(PluginPermission.Tabs);
                    var before = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.BeforeNavigatePayload>(envelope) ?? throw new InvalidDataException();
                    var decision = await RunBeforeNavigateAsync(before.TabId, before.Url, _lifetime.Token).ConfigureAwait(true);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.BeforeNavigateReply(decision.Action, decision.RedirectUrl));
                    break;
                case "history.search":
                    Demand(PluginPermission.History);
                    var history = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.HistorySearchPayload>(envelope) ?? throw new InvalidDataException();
                    var historyEntries = RunOnUi(() => _browser.PluginHistorySearch(history.Query, Math.Clamp(history.MaxResults, 1, 100)))
                        .Select(x => new PluginHistoryEntry(x.Url, x.Title, x.Visited)).ToArray();
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.HistoryReply(historyEntries));
                    break;
                case "bookmarks.list":
                    Demand(PluginPermission.Bookmarks);
                    var bl = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.BookmarksListPayload>(envelope) ?? throw new InvalidDataException();
                    var bookmarkEntries = RunOnUi(() => _browser.PluginBookmarksList(Math.Clamp(bl.MaxResults, 1, 500)))
                        .Select(x => new PluginBookmarkEntry(x.Title, x.Url, x.Added)).ToArray();
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.BookmarksReply(bookmarkEntries));
                    break;
                case "bookmarks.add":
                    Demand(PluginPermission.Bookmarks);
                    var ba = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.BookmarkMutationPayload>(envelope) ?? throw new InvalidDataException();
                    RunOnUi(() => _browser.PluginBookmarkAdd(ba.Title, ba.Url));
                    await ReplyOkAsync(envelope);
                    break;
                case "bookmarks.remove":
                    Demand(PluginPermission.Bookmarks);
                    var br = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.BookmarkRemovePayload>(envelope) ?? throw new InvalidDataException();
                    RunOnUi(() => _browser.PluginBookmarkRemove(br.Url));
                    await ReplyOkAsync(envelope);
                    break;
                case "page.read.text":
                    var pageText = await GetPageTextAsync(_lifetime.Token).ConfigureAwait(true);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.PageReadReply(pageText));
                    break;
                case "page.read.links":
                    var pageLinks = await GetPageLinksAsync(_lifetime.Token).ConfigureAwait(true);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.PageLinksReply(pageLinks.ToArray()));
                    break;
                case "page.read.selection":
                    var selection = await GetSelectionAsync(_lifetime.Token).ConfigureAwait(true);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.PageSelectionReply(selection));
                    break;
                case "browser.state":
                    Demand(PluginPermission.BrowserRead);
                    var viewport = RunOnUi(() => _browser.PluginViewportSize);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.BrowserState(
                        _browser.PluginCurrentUrl, _browser.PluginCurrentTitle, _browser.PluginZoom, viewport.Width, viewport.Height));
                    break;
                case "browser.navigate":
                    Demand(PluginPermission.BrowserNavigation);
                    var nav = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NavigatePayload>(envelope) ?? throw new InvalidDataException();
                    RunOnUi(() => _browser.NavigateTo(nav.Url));
                    await ReplyOkAsync(envelope.Id); break;
                case "browser.reload":
                    Demand(PluginPermission.BrowserNavigation); RunOnUi(_browser.Reload); await ReplyOkAsync(envelope); break;
                case "browser.back":
                    Demand(PluginPermission.BrowserNavigation); RunOnUi(_browser.NavigateBack); await ReplyOkAsync(envelope); break;
                case "browser.forward":
                    Demand(PluginPermission.BrowserNavigation); RunOnUi(_browser.NavigateForward); await ReplyOkAsync(envelope); break;
                case "browser.window":
                    Demand(PluginPermission.BrowserWindows);
                    var open = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NavigatePayload>(envelope) ?? throw new InvalidDataException();
                    RunOnUi(() => _browser.OpenPluginWindow(open.Url)); await ReplyOkAsync(envelope); break;
                case "browser.scroll":
                    Demand(PluginPermission.BrowserNavigation);
                    var scroll = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ScrollPayload>(envelope) ?? throw new InvalidDataException();
                    RunOnUi(() => _browser.PluginScrollTo(scroll.X, scroll.Y)); await ReplyOkAsync(envelope); break;
                case "browser.zoom.get":
                    Demand(PluginPermission.BrowserZoom); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ZoomPayload(_browser.PluginZoom)); break;
                case "browser.zoom.set":
                    Demand(PluginPermission.BrowserZoom); var zoom = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ZoomPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.PluginZoom = zoom.Value); await ReplyOkAsync(envelope); break;
                case "browser.cookie.get":
                    Demand(PluginPermission.BrowserCookies); var cg = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.CookieGetPayload>(envelope) ?? throw new InvalidDataException(); string? cv = RunOnUi(() => _browser.GetPluginCookie(cg.Name)); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.CookieGetPayload(cg.Name, cv)); break;
                case "browser.cookie.set":
                    Demand(PluginPermission.BrowserCookies); var cs = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.CookieSetPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.SetPluginCookie(cs.Name, cs.Value, new CookieOptions(cs.Path, cs.Domain, cs.Expires, cs.Secure))); await ReplyOkAsync(envelope); break;
                case "browser.find":
                    Demand(PluginPermission.BrowserFind); var fp = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FindPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.PluginFind(fp.Text, fp.CaseSensitive, fp.WrapAround)); await ReplyOkAsync(envelope); break;
                case "browser.find.next":
                    Demand(PluginPermission.BrowserFind); RunOnUi(_browser.PluginFindNext); await ReplyOkAsync(envelope); break;
                case "browser.find.clear":
                    Demand(PluginPermission.BrowserFind); RunOnUi(_browser.PluginFindClear); await ReplyOkAsync(envelope); break;
                case "browser.screenshot":
                    Demand(PluginPermission.BrowserScreenshot); byte[] png = RunOnUi(_browser.PluginCaptureViewport); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ScreenshotReply(Convert.ToBase64String(png))); break;

                case "ui.menu.add":
                    Demand(PluginPermission.UserInterface);
                    var menu = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiMenuAddPayload>(envelope) ?? throw new InvalidDataException();
                    string token = Guid.NewGuid().ToString("N");
                    IDisposable item = RunOnUi(() => _browser.AddPluginFileMenuItem(_record.Manifest.Id, menu.Text, () => SendEvent("event.ui.invoke", new PluginSandboxProtocol.UiInvokePayload(token))));
                    lock (_menuItems) _menuItems[token] = item;
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.UiMenuAddReply(token)); break;
                case "ui.menu.remove":
                    Demand(PluginPermission.UserInterface);
                    var remove = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiMenuRemovePayload>(envelope) ?? throw new InvalidDataException();
                    IDisposable? disposable = null;
                    lock (_menuItems) { if (_menuItems.Remove(remove.Token, out var found)) disposable = found; }
                    try { disposable?.Dispose(); } catch { }
                    await ReplyOkAsync(envelope); break;
                case "ui.message":
                    Demand(PluginPermission.UserInterface);
                    var message = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiMessagePayload>(envelope) ?? throw new InvalidDataException();
                    RunOnUi(() => MessageBox.Show(_browser, message.Message, message.Title, MessageBoxButtons.OK, MessageBoxIcon.Information)); await ReplyOkAsync(envelope); break;
                case "ui.toolbar.add":
                    Demand(PluginPermission.UserInterface); var tb = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiToolbarAddPayload>(envelope) ?? throw new InvalidDataException(); string tbToken = Guid.NewGuid().ToString("N");
                    IDisposable tbItem = RunOnUi(() => _browser.AddPluginToolbarButton(_record.Manifest.Id, tb.Label, tb.Tooltip, () => SendEvent("event.ui.invoke", new PluginSandboxProtocol.UiInvokePayload(tbToken)))); lock (_toolbarItems) _toolbarItems[tbToken] = tbItem; await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.UiToolbarAddReply(tbToken)); break;
                case "ui.toolbar.remove":
                    Demand(PluginPermission.UserInterface); var tr = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiMenuRemovePayload>(envelope) ?? throw new InvalidDataException(); IDisposable? trd = null; lock (_toolbarItems) { if (_toolbarItems.Remove(tr.Token, out var f)) trd = f; } trd?.Dispose(); await ReplyOkAsync(envelope); break;
                case "ui.context.add":
                    Demand(PluginPermission.UserInterface); var ca = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiContextAddPayload>(envelope) ?? throw new InvalidDataException(); string ctoken = Guid.NewGuid().ToString("N");
                    IDisposable ci = RunOnUi(() => _browser.AddPluginContextMenuItem(_record.Manifest.Id, ca.Label, context => QueryWorkerContextVisible(ctoken, context), context => SendEvent("event.ui.context.invoke", new PluginSandboxProtocol.UiContextInvokePayload(ctoken, context)))); lock (_contextItems) _contextItems[ctoken] = ci; await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.UiContextAddReply(ctoken)); break;
                case "ui.context.remove":
                    Demand(PluginPermission.UserInterface); var crm = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiMenuRemovePayload>(envelope) ?? throw new InvalidDataException(); IDisposable? crd = null; lock (_contextItems) { if (_contextItems.Remove(crm.Token, out var foundContext)) crd = foundContext; } crd?.Dispose(); await ReplyOkAsync(envelope); break;
                case "ui.status":
                    Demand(PluginPermission.UserInterface); var status2 = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiStatusPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.SetPluginStatus(_record.Manifest.Id, status2.Text)); await ReplyOkAsync(envelope); break;
                case "ui.progress":
                    Demand(PluginPermission.UserInterface); var prog = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiProgressPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.SetPluginProgress(prog.Fraction)); await ReplyOkAsync(envelope); break;
                case "ui.input":
                    Demand(PluginPermission.UserInterface); var inp = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiInputPayload>(envelope) ?? throw new InvalidDataException(); string? inputResult = await RunOnUiAsync(() => _browser.ShowPluginInputDialogAsync(inp.Title, inp.Prompt, inp.DefaultValue)).ConfigureAwait(false); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.UiInputReply(inputResult)); break;
                case "ui.panel.create":
                    Demand(PluginPermission.UiPanel); var pc = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelCreatePayload>(envelope) ?? throw new InvalidDataException(); string ptoken = Guid.NewGuid().ToString("N");
                    var panel = RunOnUi(() => _browser.CreatePluginPanel(_record.Manifest.Id, pc.Title, (widgetToken, text, check, index) => SendEvent("event.ui.panel.invoke", new PluginSandboxProtocol.PanelInvokePayload(widgetToken, text, check, index)))); lock (_panels) _panels[ptoken] = panel; await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.PanelCreateReply(ptoken));
                    break;
                case "ui.panel.widget.add":
                    Demand(PluginPermission.UiPanel); var wa = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelWidgetPayload>(envelope) ?? throw new InvalidDataException(); if (!_panels.TryGetValue(wa.PanelToken, out var pHost)) throw new InvalidOperationException("Plugin panel is unavailable."); string wToken = RunOnUi(() => pHost.Add(wa.Kind, wa.Text, wa.Placeholder, wa.Checked, wa.Items)); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.PanelWidgetReply(wToken)); break;
                case "ui.panel.widget.set":
                    Demand(PluginPermission.UiPanel); var ws = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelWidgetSetPayload>(envelope) ?? throw new InvalidDataException(); foreach (var pHost2 in _panels.Values) RunOnUi(() => pHost2.Set(ws.WidgetToken, ws.Text, ws.Enabled)); await ReplyOkAsync(envelope); break;
                case "ui.panel.clear":
                    Demand(PluginPermission.UiPanel); var pcl = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelTokenPayload>(envelope) ?? throw new InvalidDataException(); if (_panels.TryGetValue(pcl.Token, out var phc)) RunOnUi(phc.Clear); await ReplyOkAsync(envelope); break;
                case "ui.panel.show":
                    Demand(PluginPermission.UiPanel); var psh = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelTokenPayload>(envelope) ?? throw new InvalidDataException(); if (_panels.TryGetValue(psh.Token, out var phs)) RunOnUi(phs.Show); await ReplyOkAsync(envelope); break;
                case "ui.panel.hide":
                    Demand(PluginPermission.UiPanel); var phi = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelTokenPayload>(envelope) ?? throw new InvalidDataException(); if (_panels.TryGetValue(phi.Token, out var phh)) RunOnUi(phh.Hide); await ReplyOkAsync(envelope); break;
                case "ui.panel.dispose":
                    Demand(PluginPermission.UiPanel); var pdi = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.PanelTokenPayload>(envelope) ?? throw new InvalidDataException(); if (_panels.Remove(pdi.Token, out var phd)) RunOnUi(phd.Dispose); await ReplyOkAsync(envelope); break;

                case "storage.get":
                    Demand(PluginPermission.Storage); var sg = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.StorageKeyPayload>(envelope) ?? throw new InvalidDataException();
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.StorageKeyPayload(sg.Key, ReadStorage().TryGetValue(sg.Key, out var sv) ? sv : null)); break;
                case "storage.set":
                    Demand(PluginPermission.Storage); var ss = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.StorageKeyPayload>(envelope) ?? throw new InvalidDataException();
                    var store = ReadStorage(); store[ss.Key] = ss.Value ?? ""; WriteStorage(store); await ReplyOkAsync(envelope); break;
                case "storage.delete":
                    Demand(PluginPermission.Storage); var sd = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.StorageKeyPayload>(envelope) ?? throw new InvalidDataException();
                    var del = ReadStorage(); del.Remove(sd.Key); WriteStorage(del); await ReplyOkAsync(envelope); break;
                case "storage.snapshot":
                    Demand(PluginPermission.Storage); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.StorageSnapshotPayload(ReadStorage())); break;
                case "storage.keys":
                    Demand(PluginPermission.Storage); var sk = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.StorageKeyPayload>(envelope); string prefix = sk?.Key ?? ""; await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.StorageKeysPayload(ReadStorage().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())); break;
                case "storage.get.object":
                    Demand(PluginPermission.Storage); var sgo = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.StorageObjectPayload>(envelope) ?? throw new InvalidDataException(); ReadStorage().TryGetValue(sgo.Key, out var objectJson); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.StorageObjectPayload(sgo.Key, objectJson)); break;
                case "storage.set.object":
                    Demand(PluginPermission.Storage); var sso = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.StorageObjectPayload>(envelope) ?? throw new InvalidDataException(); var sobj = ReadStorage(); sobj[sso.Key] = sso.Json ?? "null"; WriteStorage(sobj); await ReplyOkAsync(envelope); break;
                case "storage.used":
                    Demand(PluginPermission.Storage); long used = 0; string storageFile = Path.Combine(_rootDirectory, "data", "storage.json"); if (File.Exists(storageFile)) used = new FileInfo(storageFile).Length; await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.StorageUsedPayload(used)); break;

                case "filesystem.read.text":
                    Demand(PluginPermission.FileSystem); var frt = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException();
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.FilePayload(frt.RelativePath, ReadDataText(frt.RelativePath))); break;
                case "filesystem.read.bytes":
                    Demand(PluginPermission.FileSystem); var frb = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException();
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.FilePayload(frb.RelativePath, BytesBase64: Convert.ToBase64String(File.ReadAllBytes(GetDataPath(frb.RelativePath))))); break;
                case "filesystem.write.text":
                    Demand(PluginPermission.FileSystem); var fwt = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException();
                    File.WriteAllText(GetDataPath(fwt.RelativePath), fwt.Text ?? "", Encoding.UTF8); await ReplyOkAsync(envelope); break;
                case "filesystem.write.bytes":
                    Demand(PluginPermission.FileSystem); var fwb = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException();
                    File.WriteAllBytes(GetDataPath(fwb.RelativePath), Convert.FromBase64String(fwb.BytesBase64 ?? "")); await ReplyOkAsync(envelope); break;
                case "filesystem.delete":
                    Demand(PluginPermission.FileSystem); var fd = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException();
                    string deletePath = GetDataPath(fd.RelativePath); if (File.Exists(deletePath)) File.Delete(deletePath); await ReplyOkAsync(envelope); break;
                case "filesystem.exists":
                    Demand(PluginPermission.FileSystem); var fe = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException(); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.FilePayload(fe.RelativePath, Text: File.Exists(GetDataPath(fe.RelativePath)) ? "true" : "false")); break;
                case "filesystem.list":
                    Demand(PluginPermission.FileSystem); var fl = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FileListPayload>(envelope) ?? throw new InvalidDataException(); string listRoot = GetDataPath(string.IsNullOrWhiteSpace(fl.Subdirectory) ? "." : fl.Subdirectory!); string[] files = Directory.Exists(listRoot) ? Directory.GetFiles(listRoot).Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray() : Array.Empty<string>(); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.FileListPayload(fl.Subdirectory, files)); break;
                case "filesystem.mkdir":
                    Demand(PluginPermission.FileSystem); var fm = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.FilePayload>(envelope) ?? throw new InvalidDataException(); Directory.CreateDirectory(GetDataPath(fm.RelativePath)); await ReplyOkAsync(envelope); break;

                case "network.get":
                    var ng = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkRequestPayload>(envelope) ?? throw new InvalidDataException();
                    Demand(PluginPermission.Network, GetNetworkHost(ng.Url));
                    EnsurePluginHeaders(ng.Headers);
                    string networkText = await _browser.PluginNetworkSendRequestAsync(new HttpPluginRequest(ng.Method, ng.Url, ng.Headers, string.IsNullOrEmpty(ng.BodyBase64) ? null : Convert.FromBase64String(ng.BodyBase64), ng.ContentType), _lifetime.Token).ConfigureAwait(false);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.NetworkReply(true, Text: networkText)); break;
                case "network.bytes":
                    var nb = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkRequestPayload>(envelope) ?? throw new InvalidDataException();
                    Demand(PluginPermission.Network, GetNetworkHost(nb.Url));
                    EnsurePluginHeaders(nb.Headers);
                    byte[] bytes = Convert.FromBase64String(await _browser.PluginNetworkSendRequestAsync(new HttpPluginRequest(nb.Method, nb.Url, nb.Headers, string.IsNullOrEmpty(nb.BodyBase64) ? null : Convert.FromBase64String(nb.BodyBase64), nb.ContentType), _lifetime.Token, binary: true).ConfigureAwait(false));
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.NetworkReply(true, BytesBase64: Convert.ToBase64String(bytes))); break;

                case "clipboard.get":
                    Demand(PluginPermission.Clipboard); string? clip = null; RunOnUi(() => clip = Clipboard.GetText(TextDataFormat.UnicodeText)); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ClipboardPayload(clip)); break;
                case "clipboard.set":
                    Demand(PluginPermission.Clipboard); var cp = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ClipboardPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => Clipboard.SetText(cp.Text ?? "", TextDataFormat.UnicodeText)); await ReplyOkAsync(envelope); break;
                case "clipboard.image.get":
                    Demand(PluginPermission.Clipboard); byte[]? clipPng = RunOnUi(_browser.GetClipboardImagePng); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ClipboardPayload(PngBase64: clipPng == null ? null : Convert.ToBase64String(clipPng))); break;
                case "clipboard.image.set":
                    Demand(PluginPermission.Clipboard); var cpi = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ClipboardPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.SetClipboardImagePng(string.IsNullOrEmpty(cpi.PngBase64) ? Array.Empty<byte>() : Convert.FromBase64String(cpi.PngBase64))); await ReplyOkAsync(envelope); break;

                case "audio.play":
                    Demand(PluginPermission.AudioPlayback); var ap = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.AudioPlayPayload>(envelope) ?? throw new InvalidDataException(); string audioPath = GetDataPath(ap.RelativePath); await _browser.PlayPluginAudioAsync(audioPath, Path.GetFileName(audioPath), ap.Loop).ConfigureAwait(false); await ReplyOkAsync(envelope); break;
                case "audio.stop":
                    Demand(PluginPermission.AudioPlayback); RunOnUi(_browser.StopPluginAudio); await ReplyOkAsync(envelope); break;
                case "audio.state":
                    Demand(PluginPermission.AudioPlayback); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.AudioStatePayload(_browser.PluginAudioPlaying, _browser.PluginAudioVolume, _browser.PluginAudioLoop, _browser.PluginAudioFileName)); break;
                case "audio.volume.set":
                    Demand(PluginPermission.AudioPlayback); var av = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.AudioStatePayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.PluginAudioVolume = av.Volume); await ReplyOkAsync(envelope); break;
                case "audio.loop.set":
                    Demand(PluginPermission.AudioPlayback); var al = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.AudioStatePayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.PluginAudioLoop = al.Loop); await ReplyOkAsync(envelope); break;
                case "audio.play.bytes":
                    Demand(PluginPermission.AudioPlayback); var audioBytes = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.AudioBytesPayload>(envelope) ?? throw new InvalidDataException();
                    byte[] audioPcm = DecodeCappedBase64(audioBytes.PcmBase64, 1024 * 1024);
                    await _browser.PlayPluginPcmAsync("plugin:" + _record.Manifest.Id, audioPcm, audioBytes.Format, _lifetime.Token).ConfigureAwait(true);
                    await ReplyOkAsync(envelope); break;
                case "omnibox.register":
                    Demand(PluginPermission.Omnibox);
                    var orp = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.OmniboxRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    ValidateOmniboxKeyword(orp.Keyword);
                    _manager.RegisterPluginOmnibox(_record, orp.Keyword, orp.Token);
                    lock (_omniboxKeywords) _omniboxKeywords[orp.Token] = orp.Keyword;
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "omnibox.remove":
                    Demand(PluginPermission.Omnibox);
                    var orm = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.OmniboxRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    _manager.UnregisterPluginOmnibox(_record, orm.Keyword, orm.Token);
                    lock (_omniboxKeywords) _omniboxKeywords.Remove(orm.Token);
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "ui.extras.toolbar.add":
                    Demand(PluginPermission.UiExtras);
                    var utea = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiExtrasToolbarPayload>(envelope) ?? throw new InvalidDataException();
                    if (utea.Label.Length > 64 || utea.Tooltip.Length > 256) throw new InvalidDataException("Plugin toolbar text is too long.");
                    byte[]? png = string.IsNullOrWhiteSpace(utea.PngBase64) ? null : DecodeCappedBase64(utea.PngBase64, 64 * 1024);
                    var toolbarLease = RunOnUi(() => _browser.AddPluginUiExtrasToolbarButton(_record.Manifest.Id, utea.Label, utea.Tooltip, png,
                        () => SendEvent("event.ui.invoke", new PluginSandboxProtocol.UiInvokePayload(utea.Token)), utea.Menu));
                    lock (_extraUiItems) _extraUiItems[utea.Token] = toolbarLease;
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "ui.extras.toolbar.remove":
                    Demand(PluginPermission.UiExtras);
                    var uter = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiExtrasTokenPayload>(envelope) ?? throw new InvalidDataException();
                    lock (_extraUiItems) { if (_extraUiItems.Remove(uter.Token, out var lease)) lease.Dispose(); }
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "ui.extras.badge":
                    Demand(PluginPermission.UiExtras);
                    var ub = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiExtrasBadgePayload>(envelope) ?? throw new InvalidDataException();
                    if (ub.Text.Length > 32) throw new InvalidDataException("Plugin badge text is too long.");
                    RunOnUi(() => _browser.SetPluginBadge(_record.Manifest.Id, ub.Text));
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "ui.extras.shortcut.add":
                    Demand(PluginPermission.UiExtras);
                    var usa = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiExtrasShortcutPayload>(envelope) ?? throw new InvalidDataException();
                    var shortcut = new PluginKeyboardShortcut(usa.Shortcut.Trim(), usa.Description.Trim());
                    if (!_browser.TryRegisterPluginShortcut(_record.Manifest.Id, shortcut, () => SendEvent("event.ui.invoke", new PluginSandboxProtocol.UiInvokePayload(usa.Token))))
                    {
                        _record.RejectedShortcuts.Add(shortcut.Shortcut);
                        throw new InvalidOperationException($"Shortcut '{shortcut.Shortcut}' conflicts with a built-in shortcut or another plugin.");
                    }
                    lock (_shortcuts) _shortcuts[usa.Token] = shortcut;
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "ui.extras.shortcut.remove":
                    Demand(PluginPermission.UiExtras);
                    var usr = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.UiExtrasTokenPayload>(envelope) ?? throw new InvalidDataException();
                    lock (_shortcuts) { if (_shortcuts.Remove(usr.Token, out var shortcut)) _browser.UnregisterPluginShortcut(_record.Manifest.Id, shortcut); }
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;

                case "downloads.start":
                    Demand(PluginPermission.Downloads, GetNetworkHost(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DownloadStartPayload>(envelope)?.Url));
                    var dl = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DownloadStartPayload>(envelope) ?? throw new InvalidDataException();
                    string? downloaded = await _browser.PluginDownloadAsync(_record.Manifest.Id, dl.Url, dl.SuggestedFileName, Path.Combine(_rootDirectory, "data"),
                        progress => SendEvent("event.download.progress", new PluginSandboxProtocol.EventDownloadProgressPayload(progress)), _lifetime.Token).ConfigureAwait(true);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.DownloadReply(downloaded)).ConfigureAwait(false);
                    break;

                case "dialogs.open":
                    Demand(PluginPermission.Dialogs); string? importPath = await _browser.PluginOpenFilePickerAsync(_record.Manifest.Id, PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DialogOpenPayload>(envelope)?.Title ?? "Open File", PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DialogOpenPayload>(envelope)?.Filter ?? "All files (*.*)|*.*", Path.Combine(_rootDirectory, "data")).ConfigureAwait(false); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.DialogOpenReply(importPath)); break;
                case "dialogs.save":
                    Demand(PluginPermission.Dialogs); var dsp = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DialogSavePayload>(envelope) ?? throw new InvalidDataException(); bool saved = await _browser.PluginSaveFilePickerAsync(Path.Combine(_rootDirectory, "data"), dsp.RelativePath, dsp.SuggestedFilename, dsp.Filter).ConfigureAwait(false); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.DialogSaveReply(saved)); break;
                case "notifications.show":
                    Demand(PluginPermission.Notifications); var note = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NotificationPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.ShowPluginNotification(note.Title, note.Body, note.Token == null ? null : () => SendEvent("event.notification.click", new PluginSandboxProtocol.NotificationPayload(note.Title, note.Body, note.Token)))); await ReplyOkAsync(envelope); break;

                case "log":
                    var log = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.LogPayload>(envelope);
                    if (log != null) { string line = $"{DateTimeOffset.UtcNow:O} [{log.Level}] {log.Message}{(string.IsNullOrEmpty(log.Exception) ? "" : " | " + log.Exception)}"; DebugLog.Write($"PLUGIN[{_record.Manifest.Id}] {line}"); _manager.AppendPluginLog(_record, line); }
                    await ReplyOkAsync(envelope); break;

                case "embed.register":
                    Demand(PluginPermission.EmbedRenderer);
                    var er = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedRegisterPayload>(envelope) ?? throw new InvalidDataException();
                    var types = er.MimeTypes.Select(t => (t ?? string.Empty).Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (types.Length == 0) throw new InvalidDataException("At least one embedded MIME type is required.");
                    if (types.Any(t => !_record.Manifest.EmbedTypes.Any(m => m.Equals(t, StringComparison.OrdinalIgnoreCase))))
                        throw new SecurityException("Plugin attempted to register an undeclared embedded MIME type.");
                    string embedRegToken = Guid.NewGuid().ToString("N");
                    lock (_embeddedRegistrations) _embeddedRegistrations[embedRegToken] = new HostEmbeddedRegistration(embedRegToken, types);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.EmbedRegisterReply(embedRegToken)).ConfigureAwait(false);
                    break;
                case "embed.unregister":
                    Demand(PluginPermission.EmbedRenderer);
                    var eu = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedUnregisterPayload>(envelope) ?? throw new InvalidDataException();
                    lock (_embeddedRegistrations) _embeddedRegistrations.Remove(eu.Token);
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "embed.create":
                    throw new InvalidOperationException("embed.create is worker-owned.");
                case "embed.dispose":
                    Demand(PluginPermission.EmbedRenderer);
                    var ed = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedDisposePayload>(envelope) ?? throw new InvalidDataException();
                    HostEmbeddedInstance? disposedInstance;
                    lock (_embeddedInstances) _embeddedInstances.Remove(ed.InstanceToken, out disposedInstance);
                    RemoveStream(ed.StreamToken);
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "embed.stream.credit":
                    var credit = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedStreamCreditPayload>(envelope) ?? throw new InvalidDataException();
                    var creditStream = GetStream(credit.StreamToken);
                    Demand(creditStream.RequiredPermission);
                    await creditStream.AddCreditAsync(credit.MaxBytes, envelope.Id, _lifetime.Token).ConfigureAwait(false);
                    break;
                case "embed.stream.seek":
                    var seek = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedStreamSeekPayload>(envelope) ?? throw new InvalidDataException();
                    var seekStream = GetStream(seek.StreamToken);
                    Demand(seekStream.RequiredPermission);
                    var seekResult = await seekStream.SeekAsync(seek.Offset, seek.Origin, _lifetime.Token).ConfigureAwait(false);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.EmbedStreamSeekReply(seek.StreamToken, seekResult.Position, seekResult.Length)).ConfigureAwait(false);
                    break;
                case "embed.stream.close":
                    var close = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedStreamClosePayload>(envelope) ?? throw new InvalidDataException();
                    var closeStream = GetStream(close.StreamToken);
                    Demand(closeStream.RequiredPermission);
                    RemoveStream(close.StreamToken);
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "network.stream.open":
                    var ns = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkStreamOpenPayload>(envelope) ?? throw new InvalidDataException();
                    Demand(PluginPermission.Network, GetNetworkHost(ns.Request.Url));
                    EnsurePluginHeaders(ns.Request.Headers);
                    var net = await _browser.OpenPluginNetworkStreamAsync(ns.Request, _record, _lifetime.Token).ConfigureAwait(true);
                    string networkStreamToken = Guid.NewGuid().ToString("N");
                    var networkState = new HostStreamState(this, networkStreamToken, net.Body, net.CanSeek, net.Length, PluginPermission.Network);
                    lock (_streams) _streams[networkStreamToken] = networkState;
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.NetworkStreamOpenReply(
                        true, net.StatusCode, new Dictionary<string,string>(net.Headers, StringComparer.OrdinalIgnoreCase), net.ContentType, net.Charset,
                        net.EffectiveUrl, networkStreamToken, net.CanSeek, net.Length)).ConfigureAwait(false);
                    break;
                case "embed.network.stream.open":
                    var ens = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkStreamOpenPayload>(envelope) ?? throw new InvalidDataException();
                    Demand(PluginPermission.EmbedNetwork, GetNetworkHost(ens.Request.Url));
                    EnsurePluginHeaders(ens.Request.Headers);
                    var embedNet = await _browser.OpenPluginNetworkStreamAsync(ens.Request, _record, _lifetime.Token).ConfigureAwait(true);
                    string embedNetworkStreamToken = Guid.NewGuid().ToString("N");
                    var embedNetworkState = new HostStreamState(this, embedNetworkStreamToken, embedNet.Body, embedNet.CanSeek, embedNet.Length, PluginPermission.EmbedNetwork);
                    lock (_streams) _streams[embedNetworkStreamToken] = embedNetworkState;
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.NetworkStreamOpenReply(
                        true, embedNet.StatusCode, new Dictionary<string,string>(embedNet.Headers, StringComparer.OrdinalIgnoreCase), embedNet.ContentType, embedNet.Charset,
                        embedNet.EffectiveUrl, embedNetworkStreamToken, embedNet.CanSeek, embedNet.Length)).ConfigureAwait(false);
                    break;
                case "embed.script.pageCall":
                    Demand(PluginPermission.EmbedScript);
                    var pageCall = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedScriptPageCallPayload>(envelope) ?? throw new InvalidDataException();
                    HostEmbeddedInstance? pageInstance;
                    lock (_embeddedInstances) _embeddedInstances.TryGetValue(pageCall.InstanceToken, out pageInstance);
                    if (pageInstance?.Element == null) throw new InvalidOperationException("Embedded page-call instance is unavailable.");
                    var pageArgs = pageCall.Args.Select(PluginJsValueCodec.FromWire).ToArray();
                    var pageValue = await _browser.PluginCallPageFunctionAsync(pageInstance.Element, pageCall.Name, pageArgs, _lifetime.Token).ConfigureAwait(true);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.EmbedScriptCallReply(PluginJsValueCodec.ToWire(pageValue))).ConfigureAwait(false);
                    break;
                case "embed.status":
                    Demand(PluginPermission.EmbedStatus);
                    var status = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedStatusPayload>(envelope) ?? throw new InvalidDataException();
                    EnsureEmbeddedInstance(status.InstanceToken);
                    RunOnUi(() => _browser.SetEmbeddedPluginStatus(_record.Manifest.Id, status.Text));
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "embed.audio.push":
                    Demand(PluginPermission.EmbedAudio);
                    var ea = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedAudioPayload>(envelope) ?? throw new InvalidDataException();
                    var eaInstance = EnsureEmbeddedInstance(ea.InstanceToken);
                    byte[] eaPcm = DecodeCappedBase64(ea.PcmBase64, 1024 * 1024);
                    ValidatePcmFormat(ea.Format);
                    await _browser.PushPluginEmbeddedAudioAsync("embed:" + _record.Manifest.Id + ":" + ea.InstanceToken, eaPcm, ea.Format, _lifetime.Token, initiallyMuted: true).ConfigureAwait(true);
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "embed.audio.mute":
                    Demand(PluginPermission.EmbedAudio);
                    var em = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedMutePayload>(envelope) ?? throw new InvalidDataException();
                    EnsureEmbeddedInstance(em.InstanceToken);
                    RunOnUi(() => _browser.SetPluginEmbeddedAudioMuted("embed:" + _record.Manifest.Id + ":" + em.InstanceToken, em.Muted));
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "embed.cursor.set":
                    Demand(PluginPermission.EmbedExtras);
                    var ec = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedCursorPayload>(envelope) ?? throw new InvalidDataException();
                    var ecInstance = EnsureEmbeddedInstance(ec.InstanceToken);
                    RunOnUi(() => _browser.SetPluginEmbeddedCursor(ecInstance.Element, ec.Cursor));
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;
                case "embed.navigate":
                    Demand(PluginPermission.EmbedNavigate);
                    var navigate = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.EmbedNavigatePayload>(envelope) ?? throw new InvalidDataException();
                    EnsureEmbeddedInstance(navigate.InstanceToken);
                    ValidateEmbedNavigationUrl(navigate.Url);
                    RunOnUi(() => _browser.NavigateTo(navigate.Url));
                    await ReplyOkAsync(envelope.Id).ConfigureAwait(false);
                    break;

                case "worker.fatal":
                    var fatal = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ErrorPayload>(envelope);
                    _record.Error = fatal?.Error ?? "Plugin worker terminated unexpectedly.";
                    _ready.TrySetException(new InvalidOperationException(_record.Error));
                    break;

                default:
                    throw new InvalidOperationException($"Unknown plugin sandbox operation '{envelope.Op}'.");
            }
        }
        catch (Exception ex)
        {
            await ReplyAsync(envelope.Id, "error", new PluginSandboxProtocol.ErrorPayload(ex.Message)).ConfigureAwait(false);
            _record.Error = ex.Message;
        }
    }

    private async Task HandleBinaryAsync(PluginSandboxProtocol.BinaryEnvelope envelope)
    {
        if (envelope.Op == "embed.frame" && _pendingBinary.TryGetValue(envelope.Id, out var frameCompletion))
        {
            frameCompletion.TrySetResult(envelope);
            return;
        }
        throw new InvalidOperationException($"Unknown plugin sandbox binary operation '{envelope.Op}'.");
    }

    private static void ValidateProtocolScheme(string scheme)
    {
        string value = (scheme ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 1 or > 32 || !value.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.')) throw new InvalidDataException("Invalid protocol scheme.");
        if (value is "http" or "https" or "file" or "about" or "data" or "javascript" or "mailto" or "retro96") throw new SecurityException("That URL scheme is reserved by Retro96.");
    }

    private static void ValidateOmniboxKeyword(string keyword)
    {
        string value = (keyword ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 1 or > 32 || !value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) throw new InvalidDataException("Invalid omnibox keyword.");
    }

    private static void ValidateContentType(string contentType)
    {
        string value = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 1 or > 256 || !value.Contains('/')) throw new InvalidDataException("Invalid content type.");
    }

    private static byte[] DecodeCappedBase64(string value, int cap)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
        if (value.Length > ((cap + 2) / 3) * 4 + 8) throw new InvalidDataException("Plugin binary payload exceeds its size cap.");
        byte[] bytes; try { bytes = Convert.FromBase64String(value); } catch { throw new InvalidDataException("Plugin binary payload is not valid base64."); }
        if (bytes.Length > cap) throw new InvalidDataException("Plugin binary payload exceeds its size cap.");
        return bytes;
    }

    private static void ValidatePcmFormat(PluginPcmFormat format)
    {
        if (format.SampleRate is < 8000 or > 48000 || format.Channels is < 1 or > 2 || format.SampleFormat is not (PluginPcmSampleFormat.PcmS16Le or PluginPcmSampleFormat.Float32Le)) throw new InvalidDataException("Unsupported PCM format.");
    }

    private static string SanitizePluginCss(string css)
    {
        if (css == null || css.Length > 64 * 1024) throw new InvalidDataException("Plugin CSS exceeds its size cap.");
        css = System.Text.RegularExpressions.Regex.Replace(css, @"url\s*\([^)]*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        css = System.Text.RegularExpressions.Regex.Replace(css, @"@import\s+[^;]+;?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        css = System.Text.RegularExpressions.Regex.Replace(css, @"(?:-moz-binding|behavior)\s*:[^;]+;?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return css;
    }

    private async Task<PluginBeforeNavigateDecision> RunBeforeNavigateAsync(string tabId, string url, CancellationToken ct)
    {
        var reply = await SendRequestAsync<PluginSandboxProtocol.BeforeNavigateReply>(
            "tabs.beforeNavigate", new PluginSandboxProtocol.BeforeNavigatePayload(tabId, url), ct).ConfigureAwait(true);
        var action = reply.Action;
        if (action == PluginBeforeNavigateAction.Redirect)
        {
            if (string.IsNullOrWhiteSpace(reply.RedirectUrl) || reply.RedirectUrl.Length > 8192) return new PluginBeforeNavigateDecision(PluginBeforeNavigateAction.Cancel);
            try { _ = ParsedUrl.Parse(reply.RedirectUrl); } catch { return new PluginBeforeNavigateDecision(PluginBeforeNavigateAction.Cancel); }
            return new PluginBeforeNavigateDecision(action, reply.RedirectUrl);
        }
        return new PluginBeforeNavigateDecision(action);
    }

    internal async Task<PluginBeforeNavigateDecision> BeforeNavigateAsync(string tabId, string url, CancellationToken ct)
    {
        Demand(PluginPermission.Tabs);
        return await RunBeforeNavigateAsync(tabId, url, ct).ConfigureAwait(true);
    }

    internal async Task<IReadOnlyList<PluginOmniboxSuggestion>> GetOmniboxSuggestionsAsync(string keyword, string text, CancellationToken ct)
    {
        Demand(PluginPermission.Omnibox);
        string token;
        lock (_omniboxKeywords) token = _omniboxKeywords.FirstOrDefault(x => x.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase)).Key;
        if (string.IsNullOrWhiteSpace(token)) return Array.Empty<PluginOmniboxSuggestion>();
        var reply = await SendRequestAsync<PluginSandboxProtocol.OmniboxSuggestionsReply>("omnibox.suggest", new PluginSandboxProtocol.OmniboxSuggestPayload(token, text), ct).ConfigureAwait(true);
        return (reply.Suggestions ?? Array.Empty<PluginSandboxProtocol.OmniboxSuggestionWire>()).Take(8).Select(x => new PluginOmniboxSuggestion(TruncatePluginText(x.Text ?? string.Empty, 256), string.IsNullOrWhiteSpace(x.Url) ? null : TruncatePluginText(x.Url!, 8192), TruncatePluginText(x.Description ?? string.Empty, 512))).ToArray();
    }

    internal void RegisterProtocol(string scheme, string token)
    {
        Demand(PluginPermission.Protocol);
        ValidateProtocolScheme(scheme);
        _manager.RegisterPluginProtocol(_record, scheme, token);
        lock (_protocols) _protocols[scheme] = token;
    }

    internal void UnregisterProtocol(string scheme, string token)
    {
        lock (_protocols) if (_protocols.TryGetValue(scheme, out var current) && current == token) _protocols.Remove(scheme);
    }

    internal bool HasProtocol(string scheme) { lock (_protocols) return _protocols.ContainsKey(scheme); }

    internal void RegisterContentTransform(string contentType, string token)
    {
        Demand(PluginPermission.ContentTransform);
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 256) throw new InvalidDataException("Invalid content type.");
        lock (_contentTransforms) _contentTransforms[contentType] = token;
    }

    internal void UnregisterContentTransform(string contentType, string token)
    {
        lock (_contentTransforms) if (_contentTransforms.TryGetValue(contentType, out var current) && current == token) _contentTransforms.Remove(contentType);
    }

    internal string[] GetContentTransformTypes() { lock (_contentTransforms) return _contentTransforms.Keys.ToArray(); }

    internal async Task<PluginProtocolResponse?> HandleProtocolAsync(string scheme, string url, string method, CancellationToken ct)
    {
        Demand(PluginPermission.Protocol);
        string token;
        lock (_protocols) if (!_protocols.TryGetValue(scheme, out token!)) return null;
        var binary = await SendBinaryRequestAsync("protocol.request", new PluginSandboxProtocol.ProtocolRequestPayload(token, scheme, url, method), Array.Empty<byte>(), ct).ConfigureAwait(true);
        var meta = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ProtocolResponsePayload>(binary) ?? throw new InvalidDataException("Protocol response metadata is missing.");
        if (!string.IsNullOrEmpty(meta.Error)) throw new InvalidOperationException(meta.Error);
        if (binary.Data.Length > 32 * 1024 * 1024) throw new InvalidDataException("Protocol response exceeds its size cap.");
        return new PluginProtocolResponse(binary.Data.ToArray(), meta.ContentType, meta.StatusCode, meta.Charset);
    }

    internal async Task<string?> TransformContentAsync(string contentType, string url, string charset, byte[] body, CancellationToken ct)
    {
        Demand(PluginPermission.ContentTransform);
        string token;
        lock (_contentTransforms)
        {
            var pair = _contentTransforms.FirstOrDefault(x => contentType.Equals(x.Key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(pair.Key)) return null;
            token = pair.Value;
        }
        if (body.Length > 8 * 1024 * 1024) throw new InvalidDataException("Transform input exceeds its size cap.");
        var binary = await SendBinaryRequestAsync("content.transform.request", new PluginSandboxProtocol.ContentTransformRequestPayload(token, url, contentType, charset), body, ct).ConfigureAwait(true);
        var meta = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ContentTransformResponsePayload>(binary) ?? throw new InvalidDataException("Transform response metadata is missing.");
        if (!string.IsNullOrEmpty(meta.Error)) throw new InvalidOperationException(meta.Error);
        if (binary.Data.Length > 8 * 1024 * 1024) throw new InvalidDataException("Transform output exceeds its size cap.");
        return Encoding.UTF8.GetString(binary.Data);
    }

    internal IReadOnlyList<string> PageStyles { get { lock (_pageStyles) return _pageStyles.ToArray(); } }
    internal void SetNetworkRules(IEnumerable<PluginNetworkRule> rules)
    {
        Demand(PluginPermission.NetworkRules);
        var normalized = new List<PluginNetworkRule>();
        foreach (var rule in (rules ?? Array.Empty<PluginNetworkRule>()).Take(100))
        {
            string match = (rule.Match ?? string.Empty).Trim();
            if (match.Length == 0 || match.Length > 1024) throw new InvalidDataException("Network rule match is invalid.");
            string? replacement = rule.Replacement?.Trim();
            if (rule.Kind == PluginNetworkRuleKind.Redirect && (string.IsNullOrWhiteSpace(replacement) || replacement.Length > 8192)) throw new InvalidDataException("Network redirect target is invalid.");
            if (rule.Kind == PluginNetworkRuleKind.StripHeader && (string.IsNullOrWhiteSpace(replacement) || replacement.Length > 128)) throw new InvalidDataException("Header name is invalid.");
            if (rule.Kind is not (PluginNetworkRuleKind.Block or PluginNetworkRuleKind.Redirect or PluginNetworkRuleKind.StripHeader)) throw new InvalidDataException("Unknown network rule kind.");
            normalized.Add(new PluginNetworkRule(rule.Kind, match, replacement));
        }
        lock (_networkRules) { _networkRules.Clear(); _networkRules.AddRange(normalized); }
    }

    internal void ClearNetworkRules() { Demand(PluginPermission.NetworkRules); lock (_networkRules) _networkRules.Clear(); }
    internal PluginNetworkRule[] SnapshotNetworkRules() { lock (_networkRules) return _networkRules.ToArray(); }

    internal void SetPageStyle(string token, string css) { Demand(PluginPermission.PageStyle); lock (_pageStyles) { _pageStyles.RemoveAll(x => x.StartsWith(token + "\n", StringComparison.Ordinal)); _pageStyles.Add(token + "\n" + css); } }
    internal void RemovePageStyle(string token) { lock (_pageStyles) _pageStyles.RemoveAll(x => x.StartsWith(token + "\n", StringComparison.Ordinal)); }

    internal sealed class EmbeddedHostInstance : IEmbeddedContentInstance
    {
        private PluginSandboxSession? _session;
        internal EmbeddedHostInstance(PluginSandboxSession session, string instanceToken, string streamToken, string scriptName, IReadOnlyList<string> scriptMethods)
        { _session = session; InstanceToken=instanceToken; StreamToken=streamToken; ScriptName=scriptName; ScriptMethods=scriptMethods; }
        internal string InstanceToken { get; }
        internal string StreamToken { get; }
        internal PluginSandboxSession? Session => _session;
        public string ScriptName { get; }
        public IReadOnlyList<string> ScriptMethods { get; }
        internal DomElement? Element { get; set; }
        public Task<EmbeddedFrameBuffer> RenderAsync(EmbeddedRenderRequest request, CancellationToken cancellationToken = default) =>
            (_session ?? throw new ObjectDisposedException(nameof(EmbeddedHostInstance))).RenderEmbeddedAsync(this, request, cancellationToken);
        public Task HandleInputAsync(EmbeddedInputEvent inputEvent, CancellationToken cancellationToken = default) =>
            (_session ?? throw new ObjectDisposedException(nameof(EmbeddedHostInstance))).SendEmbeddedInputAsync(this, inputEvent, cancellationToken);
        internal Task<JsValue> CallScriptAsync(string name, IReadOnlyList<JsValue> args, CancellationToken ct = default) =>
            (_session ?? throw new ObjectDisposedException(nameof(EmbeddedHostInstance))).CallEmbeddedScriptAsync(this, name, args, ct);
        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _session, null);
            if (s == null) return;
            try { s.SendRequestAsync<object>("embed.dispose", new PluginSandboxProtocol.EmbedDisposePayload(InstanceToken, StreamToken), CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            lock (s._embeddedInstances) s._embeddedInstances.Remove(InstanceToken);
            s.RemoveStream(StreamToken);
        }
    }

    private sealed record HostEmbeddedRegistration(string Token, string[] MimeTypes);

    private sealed class HostEmbeddedInstance
    {
        public HostEmbeddedInstance(string token, string streamToken, string sourceUrl) { Token=token; StreamToken=streamToken; SourceUrl=sourceUrl; }
        public string Token { get; }
        public string StreamToken { get; }
        public string SourceUrl { get; }
        public string ScriptName { get; set; } = "";
        public DomElement? Element { get; set; }
    }

    internal sealed record PluginNetworkStream(Stream Body, int StatusCode, IReadOnlyDictionary<string,string> Headers, string? ContentType, string? Charset, string EffectiveUrl, bool CanSeek, long? Length) : IDisposable
    {
        public void Dispose() { Body.Dispose(); }
    }

    private sealed class HostStreamState : IDisposable
    {
        private readonly PluginSandboxSession _session;
        private readonly string _token;
        private readonly Stream _stream;
        private readonly bool _canSeek;
        private readonly long? _length;
        private readonly PluginPermission _requiredPermission;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private long _credit;
        private long _position;
        private long _generation;
        private bool _eof;
        private bool _pumping;
        private int _disposed;

        public HostStreamState(PluginSandboxSession session, string token, Stream stream, bool canSeek, long? length, PluginPermission permission)
        { _session=session; _token=token; _stream=stream; _canSeek=canSeek; _length=length; _requiredPermission=permission; _position=stream.CanSeek ? stream.Position : 0; }
        internal PluginPermission RequiredPermission => _requiredPermission;

        public async Task AddCreditAsync(int maxBytes, string requestId, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(HostStreamState));
            if (maxBytes <= 0 || maxBytes > PluginSandboxProtocol.MaxStreamChunkBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (this)
            {
                if (_eof) { _ = _session.ReplyAsync(requestId, "response", new { success = true }); return; }
                _credit = Math.Min(_credit + maxBytes, 8L * PluginSandboxProtocol.MaxStreamChunkBytes);
                if (_pumping) { _ = _session.ReplyAsync(requestId, "response", new { success = true }); return; }
                _pumping = true;
            }
            await _session.ReplyAsync(requestId, "response", new { success = true }).ConfigureAwait(false);
            _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    int readSize; long offset; long generation;
                    lock (this)
                    {
                        if (_eof || _credit <= 0) { _pumping=false; return; }
                        readSize=(int)Math.Min(_credit, PluginSandboxProtocol.MaxStreamChunkBytes);
                        offset=_position;
                        generation=_generation;
                    }
                    byte[] buffer = new byte[readSize];
                    int count;
                    try
                    {
                        await _ioLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                        try { count=await _stream.ReadAsync(buffer.AsMemory(0, readSize), _lifetime.Token).ConfigureAwait(false); }
                        finally { _ioLock.Release(); }
                    }
                    catch (Exception ex)
                    {
                        lock (this) { _eof=true; _pumping=false; }
                        await _session.SendBinaryAsync("embed.stream.chunk", Guid.NewGuid().ToString("N"),
                            new PluginSandboxProtocol.EmbedStreamChunkPayload(_token, offset, true, ex.Message), Array.Empty<byte>(), CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    bool end = count == 0;
                    lock (this)
                    {
                        if (generation != _generation)
                            continue;
                        _credit = Math.Max(0, _credit - count);
                        _position = checked(_position + count);
                        if (end) _eof = true;
                    }
                    await _session.SendBinaryAsync("embed.stream.chunk", Guid.NewGuid().ToString("N"),
                        new PluginSandboxProtocol.EmbedStreamChunkPayload(_token, offset, end),
                        count == buffer.Length ? buffer : buffer.AsMemory(0, count), _lifetime.Token).ConfigureAwait(false);
                    if (end) { lock (this) _pumping=false; return; }
                }
            }
            catch { lock (this) _pumping=false; }
        }

        public async Task<EmbeddedSeekResult> SeekAsync(long offset, SeekOrigin origin, CancellationToken cancellationToken)
        {
            if (!_canSeek || !_stream.CanSeek) throw new NotSupportedException("This embedded stream is not seekable.");
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long pos=_stream.Seek(offset, origin);
                lock (this) { _position=pos; _credit=0; _eof=false; _generation=checked(_generation + 1); }
                return new EmbeddedSeekResult(pos, _length);
            }
            finally { _ioLock.Release(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed,1)!=0) return;
            _lifetime.Cancel();
            try { _stream.Dispose(); } catch { }
            _ioLock.Dispose();
            _lifetime.Dispose();
        }
    }

    private HostStreamState GetStream(string token)
    {
        lock (_streams)
        {
            if (_streams.TryGetValue(token, out var state)) return state;
        }
        throw new InvalidOperationException("Plugin stream handle is invalid or closed.");
    }

    private void RemoveStream(string token)
    {
        HostStreamState? state = null;
        lock (_streams) _streams.Remove(token, out state);
        try { state?.Dispose(); } catch { }
    }

    private HostEmbeddedInstance EnsureEmbeddedInstance(string token)
    {
        lock (_embeddedInstances)
        {
            if (_embeddedInstances.TryGetValue(token, out var state)) return state;
        }
        throw new InvalidOperationException("Embedded content instance is unavailable.");
    }

    private static void ValidateEmbedNavigationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "file" or "about"))
            throw new SecurityException("Embedded navigation may only use http, https, file, or about URLs.");
    }

    private bool QueryPluginContextCallback(string token, ContextMenuContext context)
    {
        try
        {
            var result = SendRequestAsync<PluginSandboxProtocol.UiContextResult>("ui.context.check", new PluginSandboxProtocol.UiContextQueryPayload(token, context), CancellationToken.None).GetAwaiter().GetResult();
            return result.Visible;
        }
        catch { return false; }
    }


    private bool QueryWorkerContextVisible(string token, ContextMenuContext context)
    {
        try
        {
            return SendRequestAsync<PluginSandboxProtocol.UiContextResult>(
                "ui.context.query", new PluginSandboxProtocol.UiContextQueryPayload(token, context), CancellationToken.None)
                .GetAwaiter().GetResult().Visible;
        }
        catch { return false; }
    }

    private async Task<T> RunOnUiAsync<T>(Func<Task<T>> func)
    {
        if (!_browser.IsHandleCreated || _browser.IsDisposed) throw new InvalidOperationException("Browser window is unavailable.");
        if (!_browser.InvokeRequired) return await func().ConfigureAwait(true);
        var task = (Task<T>)_browser.Invoke(func)!;
        return await task.ConfigureAwait(true);
    }

    private async Task<T> SendRequestAsync<T>(string op, object? payload, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginSandboxSession));
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PluginSandboxProtocol.Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(GetCallTimeout(op));
        try
        {
            await PluginSandboxProtocol.WriteAsync(_pipe!, op, id, payload, _writeLock, timeout.Token).ConfigureAwait(false);
            var envelope = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (envelope.Op == "error")
                throw new InvalidOperationException(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ErrorPayload>(envelope)?.Error ?? "Plugin worker request failed.");
            return PluginSandboxProtocol.GetPayload<T>(envelope) ?? throw new InvalidOperationException("Plugin worker returned an invalid response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Plugin call '{op}' timed out after {GetCallTimeout(op).TotalMilliseconds:0} ms.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task<PluginSandboxProtocol.BinaryEnvelope> SendBinaryRequestAsync(string op, object? payload, CancellationToken cancellationToken) => SendBinaryRequestAsync(op, payload, Array.Empty<byte>(), cancellationToken);

    private async Task<PluginSandboxProtocol.BinaryEnvelope> SendBinaryRequestAsync(string op, object? payload, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginSandboxSession));
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PluginSandboxProtocol.BinaryEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingBinary[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(GetCallTimeout(op));
        try
        {
            await PluginSandboxProtocol.WriteBinaryAsync(_pipe!, op, id, payload, data, _writeLock, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Plugin call '{op}' timed out after {GetCallTimeout(op).TotalMilliseconds:0} ms.");
        }
        finally { _pendingBinary.TryRemove(id, out _); }
    }

    private static TimeSpan GetCallTimeout(string op)
    {
        if (op.Equals("tabs.beforeNavigate", StringComparison.Ordinal))
            return TimeSpan.FromMilliseconds(PluginSandboxProtocol.BeforeNavigateTimeoutMs);
        if (op.StartsWith("embed.render", StringComparison.Ordinal))
            return TimeSpan.FromMilliseconds(PluginSandboxProtocol.RenderCallTimeoutMs);
        return TimeSpan.FromMilliseconds(PluginSandboxProtocol.DefaultCallTimeoutMs);
    }

    private async Task SendBinaryAsync(string op, string id, object payload, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_pipe == null || Volatile.Read(ref _disposed) != 0) return;
        await PluginSandboxProtocol.WriteBinaryAsync(_pipe, op, id, payload, data, _writeLock, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetNetworkHost(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private void EnsurePluginHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        foreach (string name in (headers ?? new Dictionary<string, string>()).Keys)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Plugin HTTP header '{name}' is host-controlled.");
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !_record.HasPermission(PluginPermission.BrowserCookies))
                throw new SecurityException("Setting the Cookie header requires 'browser.cookies'.");
        }
    }

    private void Demand(PluginPermission permission, string? networkHost = null)
    {
        try { PluginPermissionSecurity.Require(_record.GrantedPermissions, permission); }
        catch (SecurityException ex) { throw new SecurityException($"Plugin '{_record.Manifest.Name}' is not allowed to perform this operation.", ex); }
        _record.RecordActivity(permission, networkHost);
    }

    private async Task ReplyOkAsync(string id) => await ReplyAsync(id, "response", new { success = true }).ConfigureAwait(false);
    private Task ReplyOkAsync(PluginSandboxProtocol.Envelope envelope) => ReplyOkAsync(envelope.Id);

    private Task ReplyAsync(string id, string op, object payload) =>
        PluginSandboxProtocol.WriteAsync(_pipe!, op, id, payload, _writeLock, _lifetime.Token);

    private T RunOnUi<T>(Func<T> func)
    {
        if (!_browser.IsHandleCreated || _browser.IsDisposed) throw new InvalidOperationException("Browser window is unavailable.");
        return _browser.InvokeRequired ? (T)_browser.Invoke(func)! : func();
    }

    private void RunOnUi(Action action)
    {
        if (!_browser.IsHandleCreated || _browser.IsDisposed) throw new InvalidOperationException("Browser window is unavailable.");
        if (_browser.InvokeRequired) _browser.Invoke(action); else action();
    }

    private Dictionary<string, string> ReadStorage()
    {
        string path = Path.Combine(_rootDirectory, "data", "storage.json");
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), PluginSandboxProtocol.JsonOptions)
                ?? new(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private void WriteStorage(Dictionary<string, string> values)
    {
        string dir = Path.Combine(_rootDirectory, "data");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "storage.json"), JsonSerializer.Serialize(values, PluginSandboxProtocol.JsonOptions));
    }

    private string GetDataPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("A relative plugin data path is required.");
        string dataRoot = Path.GetFullPath(Path.Combine(_rootDirectory, "data"));
        Directory.CreateDirectory(dataRoot);
        string full = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
        string prefix = dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Plugin file access may not escape its private data directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private string ReadDataText(string relativePath) => File.ReadAllText(GetDataPath(relativePath), Encoding.UTF8);

    private static NamedPipeServerStream CreatePipe(string name, string appContainerSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WindowsSecurity.CurrentUserSid),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(appContainerSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.SetAccessRuleProtection(true, false);

        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string dir in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(dir);
            if (name.Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(destination, name));
        }
    }

    public void Dispose()
    {
        ExpectedShutdown = true;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_protocols)
        {
            foreach (var pair in _protocols.ToArray()) _manager.UnregisterPluginProtocol(_record, pair.Key, pair.Value);
            _protocols.Clear();
        }
        _lifetime.Cancel();
        foreach (var completion in _pending.Values) completion.TrySetException(new ObjectDisposedException(nameof(PluginSandboxSession)));
        _pending.Clear();
        lock (_menuItems)
        { foreach (var item in _menuItems.Values) { try { item.Dispose(); } catch { } } _menuItems.Clear(); }
        lock (_toolbarItems) { foreach (var item in _toolbarItems.Values) { try { item.Dispose(); } catch { } } _toolbarItems.Clear(); }
        lock (_contextItems) { foreach (var item in _contextItems.Values) { try { item.Dispose(); } catch { } } _contextItems.Clear(); }
        lock (_panels) { foreach (var item in _panels.Values) { try { item.Dispose(); } catch { } } _panels.Clear(); }
        HostStreamState[] streams;
        lock (_streams) { streams = _streams.Values.ToArray(); _streams.Clear(); }
        foreach (var stream in streams) { try { stream.Dispose(); } catch { } }
        lock (_embeddedInstances) _embeddedInstances.Clear();
        lock (_embeddedRegistrations) _embeddedRegistrations.Clear();
        foreach (var completion in _pendingBinary.Values) completion.TrySetException(new ObjectDisposedException(nameof(PluginSandboxSession)));
        _pendingBinary.Clear();
        try { _pipe?.Dispose(); } catch { }
        try { _worker?.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
        try { _lifetime.Dispose(); } catch { }
        _profileName = null;
    }
}
