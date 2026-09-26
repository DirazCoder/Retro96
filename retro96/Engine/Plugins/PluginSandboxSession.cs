using System.IO.Pipes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows.Forms;
using Retro96;

namespace Retro96.Plugins;

/// <summary>
/// Host-side controller for one sandboxed plugin process. The plugin process
/// has no direct reference to the browser window; all privileged operations
/// cross this permission-checked broker.
/// </summary>
internal sealed class PluginSandboxSession : IDisposable
{
    private readonly Form1 _browser;
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
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private WindowsSecurity.WorkerProcess? _worker;
    private string? _profileName;
    private int _disposed;

    public PluginSandboxSession(Form1 browser, PluginManager.PluginRecord record)
    {
        _browser = browser;
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
                workerArguments: $"--plugin-worker \"{pipeName}\" \"PluginPayload\\{_record.Manifest.Id}\"");

            await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n"
            };

            _ = Task.Run(RunLoopAsync);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RaiseNavigated(string url) => SendEvent("event.navigated", new PluginSandboxProtocol.EventNavigatedPayload(url));
    public void RaisePageLoaded(string url, string title) => SendEvent("event.pageLoaded", new PluginSandboxProtocol.EventPageLoadedPayload(url, title));
    public void RaiseHostShuttingDown() => SendEvent("event.hostShuttingDown", new { });
    public void RaiseWindowFocusChanged(bool hasFocus) => SendEvent("event.focus", new PluginSandboxProtocol.EventFocusPayload(hasFocus));
    public void RaiseClipboardChanged() => SendEvent("event.clipboard.changed", new PluginSandboxProtocol.EventClipboardPayload());
    public void RaiseAudioComplete() => SendEvent("event.audio.complete", new PluginSandboxProtocol.EventPlaybackPayload());

    private void SendEvent(string op, object payload)
    {
        if (_writer == null || Volatile.Read(ref _disposed) != 0) return;
        _ = WriteSafeAsync(op, Guid.NewGuid().ToString("N"), payload);
    }

    private async Task WriteSafeAsync(string op, string id, object payload)
    {
        try
        {
            await PluginSandboxProtocol.WriteAsync(_writer!, op, id, payload, _writeLock, _lifetime.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var envelope = await PluginSandboxProtocol.ReadAsync(_reader!, _lifetime.Token).ConfigureAwait(false);
                if (envelope == null) break;
                if (envelope.Op is "response" or "error")
                {
                    if (_pending.TryGetValue(envelope.Id, out var completion))
                        completion.TrySetResult(envelope);
                    continue;
                }
                await HandleAsync(envelope).ConfigureAwait(false);
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
                        (long)_record.GrantedPermissions,
                        string.Equals(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.HelloPayload>(envelope)?.PluginId,
                            _record.Manifest.Id, StringComparison.OrdinalIgnoreCase) ? "" : "Plugin id mismatch."));
                    break;

                case "ready":
                    var ready = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ReadyPayload>(envelope);
                    if (ready?.Ready == true) _ready.TrySetResult(true);
                    else _ready.TrySetException(new InvalidOperationException(ready?.Error ?? "Plugin initialization failed."));
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.ReadyPayload(ready?.Ready == true, ready?.Error ?? ""));
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
                    Demand(PluginPermission.Network); var ng = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkRequestPayload>(envelope) ?? throw new InvalidDataException();
                    EnsurePluginHeaders(ng.Headers);
                    string networkText = await _browser.PluginNetworkSendRequestAsync(new HttpPluginRequest(ng.Method, ng.Url, ng.Headers, string.IsNullOrEmpty(ng.BodyBase64) ? null : Convert.FromBase64String(ng.BodyBase64), ng.ContentType), _lifetime.Token).ConfigureAwait(false);
                    await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.NetworkReply(true, Text: networkText)); break;
                case "network.bytes":
                    Demand(PluginPermission.Network); var nb = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NetworkRequestPayload>(envelope) ?? throw new InvalidDataException();
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
                case "dialogs.open":
                    Demand(PluginPermission.Dialogs); string? importPath = await _browser.PluginOpenFilePickerAsync(_record.Manifest.Id, PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DialogOpenPayload>(envelope)?.Title ?? "Open File", PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DialogOpenPayload>(envelope)?.Filter ?? "All files (*.*)|*.*", Path.Combine(_rootDirectory, "data")).ConfigureAwait(false); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.DialogOpenReply(importPath)); break;
                case "dialogs.save":
                    Demand(PluginPermission.Dialogs); var dsp = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.DialogSavePayload>(envelope) ?? throw new InvalidDataException(); bool saved = await _browser.PluginSaveFilePickerAsync(Path.Combine(_rootDirectory, "data"), dsp.RelativePath, dsp.SuggestedFilename, dsp.Filter).ConfigureAwait(false); await ReplyAsync(envelope.Id, "response", new PluginSandboxProtocol.DialogSaveReply(saved)); break;
                case "notifications.show":
                    Demand(PluginPermission.Notifications); var note = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.NotificationPayload>(envelope) ?? throw new InvalidDataException(); RunOnUi(() => _browser.ShowPluginNotification(note.Title, note.Body, note.Token == null ? null : () => SendEvent("event.notification.click", new PluginSandboxProtocol.NotificationPayload(note.Title, note.Body, note.Token)))); await ReplyOkAsync(envelope); break;

                case "log":
                    var log = PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.LogPayload>(envelope);
                    if (log != null) DebugLog.Write($"PLUGIN[{_record.Manifest.Id}] {log.Level}: {log.Message}{(string.IsNullOrEmpty(log.Exception) ? "" : " | " + log.Exception)}");
                    await ReplyOkAsync(envelope); break;

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
        try
        {
            await PluginSandboxProtocol.WriteAsync(_writer!, op, id, payload, _writeLock, cancellationToken).ConfigureAwait(false);
            var envelope = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.Op == "error")
                throw new InvalidOperationException(PluginSandboxProtocol.GetPayload<PluginSandboxProtocol.ErrorPayload>(envelope)?.Error ?? "Plugin worker request failed.");
            return PluginSandboxProtocol.GetPayload<T>(envelope) ?? throw new InvalidOperationException("Plugin worker returned an invalid response.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
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

    private void Demand(PluginPermission permission)
    {
        if (!_record.HasPermission(permission))
            throw new SecurityException($"Plugin '{_record.Manifest.Name}' lacks permission '{string.Join(", ", PluginPermissionNames.ToNames(permission))}'.");
    }

    private async Task ReplyOkAsync(string id) => await ReplyAsync(id, "response", new { success = true }).ConfigureAwait(false);
    private Task ReplyOkAsync(PluginSandboxProtocol.Envelope envelope) => ReplyOkAsync(envelope.Id);

    private Task ReplyAsync(string id, string op, object payload) =>
        PluginSandboxProtocol.WriteAsync(_writer!, op, id, payload, _writeLock, _lifetime.Token);

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        foreach (var completion in _pending.Values) completion.TrySetException(new ObjectDisposedException(nameof(PluginSandboxSession)));
        _pending.Clear();
        lock (_menuItems)
        { foreach (var item in _menuItems.Values) { try { item.Dispose(); } catch { } } _menuItems.Clear(); }
        lock (_toolbarItems) { foreach (var item in _toolbarItems.Values) { try { item.Dispose(); } catch { } } _toolbarItems.Clear(); }
        lock (_contextItems) { foreach (var item in _contextItems.Values) { try { item.Dispose(); } catch { } } _contextItems.Clear(); }
        lock (_panels) { foreach (var item in _panels.Values) { try { item.Dispose(); } catch { } } _panels.Clear(); }
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try { _worker?.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
        try { _lifetime.Dispose(); } catch { }
        _profileName = null;
    }
}
