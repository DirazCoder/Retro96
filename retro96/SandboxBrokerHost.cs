using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Retro96.Engine.Network;

namespace Retro96;

/// <summary>
/// Trusted, unsandboxed host broker. It owns settings, local-file selection,
/// network policy and the lifetime of each isolated content worker.
/// </summary>
internal sealed class SandboxBrokerApplicationContext : ApplicationContext
{
    private readonly Control _dispatcher;
    private readonly ConcurrentDictionary<string, SandboxWorkerSession> _sessions = new();
    private readonly UserSettings _settings;
    private int _disposed;

    public SandboxBrokerApplicationContext()
    {
        StartupDiagnostics.Step("HOST", "SandboxBrokerApplicationContext constructor entered.");
        _settings = UserSettings.Load();
        StartupDiagnostics.Step("HOST", "Settings loaded. TrustMode=" + _settings.TrustMode + " HomePage=" + _settings.HomePageUrl);
        _dispatcher = new Control();
        _dispatcher.CreateControl();
        StartupDiagnostics.Step("HOST", "UI dispatcher control created.");
        _ = StartInitialWorkerAsync();
    }

    private async Task StartInitialWorkerAsync()
    {
        StartupDiagnostics.Step("HOST", "Initial worker startup task entered.");
        try
        {
            var session = await StartWorkerAsync(_settings.Clone(), _settings.HomePageUrl).ConfigureAwait(false);
            if (session == null)
                throw new InvalidOperationException("Retro96 could not start its isolated content worker.");
            StartupDiagnostics.Step("HOST", "Initial worker startup completed. Session=" + session.SessionId);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Error("HOST", "Initial worker startup failed", ex);
            try
            {
                RunOnUi(() => MessageBox.Show(
                    ex.Message,
                    "Retro96 — Sandbox startup failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error));
            }
            finally
            {
                RunOnUi(Application.ExitThread);
            }
        }
    }

    internal async Task<SandboxWorkerSession?> StartWorkerAsync(UserSettings settings, string startupUrl)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return null;

        var session = new SandboxWorkerSession(this, settings.Clone(), startupUrl);
        if (!_sessions.TryAdd(session.SessionId, session))
            throw new InvalidOperationException("Sandbox session ID collision.");

        try
        {
            await session.StartAsync().ConfigureAwait(false);
            return session;
        }
        catch
        {
            _sessions.TryRemove(session.SessionId, out _);
            session.Dispose();
            throw;
        }
    }

    internal async Task<bool> SaveSessionSettingsAsync(SandboxWorkerSession session, UserSettings updated)
    {
        if (updated == null) return false;

        var current = session.Settings;
        var clone = updated.Clone();
        bool loweringSecurity = clone.TrustMode > current.TrustMode;
        if (loweringSecurity)
        {
            bool allow = await RunOnUiAsync(() =>
            {
                DialogResult result = MessageBox.Show(
                    $"This browser window requested a security change from {current.TrustMode} to {clone.TrustMode}.\r\n\r\n" +
                    "Lowering the trust level weakens the Windows isolation policy for this window and future windows. Continue?",
                    "Retro96 — Confirm security downgrade",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                return result == DialogResult.Yes;
            }).ConfigureAwait(false);

            if (!allow)
                return false;
        }

        clone.Save();

        // The host keeps the saved settings as the template for subsequently
        // opened windows. The current worker applies its in-memory copy too.
        lock (_settings)
        {
            CopySettings(_settings, clone);
        }

        session.SetSettings(clone);
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    internal async Task<bool> SpawnWindowAsync(SandboxWorkerSession parent, string url)
    {
        if (!IsScriptedWindowAllowed(parent.Settings, url))
            return false;

        try
        {
            UserSettings settings;
            lock (_settings) settings = _settings.Clone();
            await StartWorkerAsync(settings, string.IsNullOrWhiteSpace(url) ? "about:blank" : url)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal async Task<bool> RestartWindowAsync(SandboxWorkerSession oldSession, string url)
    {
        try
        {
            UserSettings settings;
            lock (_settings) settings = _settings.Clone();
            var replacement = await StartWorkerAsync(settings, string.IsNullOrWhiteSpace(url) ? "about:blank" : url)
                .ConfigureAwait(false);
            return replacement != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsScriptedWindowAllowed(UserSettings settings, string url) =>
        settings.TrustMode != TrustMode.High ||
        string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);

    internal void SessionClosed(SandboxWorkerSession session)
    {
        _sessions.TryRemove(session.SessionId, out _);
        if (_sessions.IsEmpty)
            RunOnUi(Application.ExitThread);
    }

    internal Task<T> RunOnUiAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Invoke()
        {
            try { tcs.TrySetResult(action()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        try
        {
            if (_dispatcher.IsDisposed)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(_dispatcher)));
            }
            else if (_dispatcher.InvokeRequired)
            {
                _dispatcher.BeginInvoke((Action)Invoke);
            }
            else
            {
                Invoke();
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    internal void RunOnUi(Action action)
    {
        _ = RunOnUiAsync(() =>
        {
            action();
            return true;
        });
    }

    private static void CopySettings(UserSettings target, UserSettings source)
    {
        target.SearchQueryUrl = source.SearchQueryUrl;
        target.HomePageUrl = source.HomePageUrl;
        target.EngineMode = source.EngineMode;
        target.UserAgentOverride = source.UserAgentOverride;
        target.BackgroundMode = source.BackgroundMode;
        target.ForcedBackgroundColor = source.ForcedBackgroundColor;
        target.LoadImages = source.LoadImages;
        target.EnableJavaScript = source.EnableJavaScript;
        target.AllowScriptedWindows = source.AllowScriptedWindows;
        target.TrustMode = source.TrustMode;
        target.HostCheckImages = source.HostCheckImages;
        target.DiscardPageStateOnClose = source.DiscardPageStateOnClose;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (var session in _sessions.Values.ToArray())
                session.Dispose();
            _sessions.Clear();

            try { _dispatcher.Dispose(); } catch { }
        }

        base.Dispose(disposing);
    }
}

internal sealed class SandboxWorkerSession : IDisposable
{
    private readonly SandboxBrokerApplicationContext _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private readonly TaskCompletionSource<bool> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private SemaphoreSlim? _writeLock;
    private WindowsSecurity.WorkerProcess? _worker;
    private string? _profileName;
    private string? _appContainerSid;
    private string? _localRoot;
    private UserSettings _settings;
    private int _disposed;

    private readonly Retro96.Engine.Network.HttpClient _brokerHttp = new(allowInvalidCertificates: false);
    private readonly CookieStore _cookies = new();

    public SandboxWorkerSession(SandboxBrokerApplicationContext owner, UserSettings settings, string startupUrl)
    {
        _owner = owner;
        _settings = settings.Clone();
        StartupUrl = string.IsNullOrWhiteSpace(startupUrl) ? settings.HomePageUrl : startupUrl;
        SessionId = Guid.NewGuid().ToString("N");
        PipeName = "LOCAL\\Retro96." + SessionId;
    }

    public string SessionId { get; }
    public string PipeName { get; }
    public string StartupUrl { get; }
    public UserSettings Settings
    {
        get { lock (_stateLock) return _settings.Clone(); }
    }

    public void SetSettings(UserSettings settings)
    {
        lock (_stateLock) _settings = settings.Clone();
    }

    public async Task StartAsync()
    {
        StartupDiagnostics.Step("HOST", "Session StartAsync. Session=" + SessionId + " Trust=" + _settings.TrustMode + " Pipe=" + PipeName);
        if (_worker != null)
            throw new InvalidOperationException("Sandbox worker already started.");

        if (_settings.TrustMode is TrustMode.High or TrustMode.Medium)
        {
            StartupDiagnostics.Step("HOST", "Creating AppContainer profile.");
            var profile = WindowsSecurity.CreateAppContainer(_settings.TrustMode);
            StartupDiagnostics.Step("HOST", "AppContainer created. Profile=" + profile.ProfileName + " SID=" + profile.Sid);
            _profileName = profile.ProfileName;
            _appContainerSid = profile.Sid;
        }

        StartupDiagnostics.Step("HOST", "Creating broker pipe.");
        _pipe = CreatePipe(PipeName, _appContainerSid);
        StartupDiagnostics.Step("HOST", "Broker pipe created.");
        _writeLock = new SemaphoreSlim(1, 1);

        string workerExecutable = ResolveWorkerExecutable();
        StartupDiagnostics.Step("HOST", "Resolved worker executable: " + workerExecutable);
        string workerRuntimeDirectory = Path.GetDirectoryName(workerExecutable)!;
        bool usesRuntimeShadow = false;

        if (_settings.TrustMode is TrustMode.High or TrustMode.Medium)
        {
            StartupDiagnostics.Step("HOST", "Preparing AppContainer worker runtime.");
            workerRuntimeDirectory = WindowsSecurity.PrepareWorkerRuntime(
                Path.GetDirectoryName(workerExecutable)!,
                _appContainerSid!);
            workerExecutable = Path.Combine(workerRuntimeDirectory, Path.GetFileName(workerExecutable));
            usesRuntimeShadow = true;
            StartupDiagnostics.Step("HOST", "Worker runtime prepared: " + workerRuntimeDirectory);
        }

        bool useLpac = _settings.TrustMode == TrustMode.High &&
                       !File.Exists(Path.Combine(workerRuntimeDirectory,
                           Path.GetFileNameWithoutExtension(workerExecutable) + ".runtimeconfig.json"));

        try
        {
            StartupDiagnostics.Step("HOST", "Launching worker process. LPAC=" + useLpac + " WorkingDir=" + workerRuntimeDirectory + " Executable=" + workerExecutable);
            _worker = WindowsSecurity.StartWorker(
                _settings.TrustMode,
                PipeName,
                _appContainerSid,
                _profileName ?? "",
                workerExecutable,
                workerRuntimeDirectory,
                useLpac,
                usesRuntimeShadow);
            StartupDiagnostics.Step("HOST", "Worker process created. PID=" + _worker.ProcessId);
        }
        catch
        {
            if (usesRuntimeShadow) TryDeleteRuntimeDirectory(workerRuntimeDirectory);
            throw;
        }

        // The child is suspended until it has been inserted into the Job;
        // StartWorker resumes it only after that point. If the process dies
        // before the pipe handshake, report its real exit code instead of
        // leaving the host waiting for a timeout with no useful diagnosis.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(20));
        StartupDiagnostics.Step("HOST", "Waiting for worker pipe connection.");
        while (!_pipe.IsConnected)
        {
            if (_worker.HasExited(out uint exitCode))
            {
                StartupDiagnostics.Step("HOST", "Worker exited before pipe connection. ExitCode=0x" + exitCode.ToString("X8"));
                throw new InvalidOperationException(
                    $"Retro96 content worker exited before connecting to the broker pipe. Exit code: 0x{exitCode:X8} ({exitCode}).");
            }

            await Task.Delay(25, connectCts.Token).ConfigureAwait(false);
        }
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n"
        };

        StartupDiagnostics.Step("HOST", "Worker connected; performing handshake.");
        await PerformHandshakeAsync().ConfigureAwait(false);
        StartupDiagnostics.Step("HOST", "Worker handshake accepted.");
        _ = RunLoopAsync();
    }

    private static string ResolveWorkerExecutable()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "Retro96.exe");
        if (OperatingSystem.IsWindows() && File.Exists(candidate))
            return candidate;

        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Retro96 executable path is unavailable.");
        if (string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        // `dotnet run` may leave the managed host as the current process.  The
        // build output still has an apphost beside the managed DLL, so use it
        // whenever it exists.
        string assemblyName = typeof(SandboxBrokerApplicationContext).Assembly.GetName().Name ?? "Retro96";
        candidate = Path.Combine(baseDir, assemblyName + ".exe");
        if (File.Exists(candidate)) return candidate;

        throw new InvalidOperationException(
            "Retro96 could not locate its Windows worker executable. Build the WinExe project first.");
    }

    private static void TryDeleteRuntimeDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName, string? appContainerSid)
    {
        var security = new PipeSecurity();

        // AppContainer access is checked against both sides of the restricted
        // token. The normal user identity must be able to reach the pipe, and
        // the exact per-worker AppContainer SID must also be authorized.
        // Synchronize is intentionally included: Windows' AppContainer
        // lowbox access path can require it even though ordinary ReadWrite
        // traffic is all the broker actually performs.
        var userSid = new SecurityIdentifier(WindowsSecurity.CurrentUserSid);
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));

        if (!string.IsNullOrWhiteSpace(appContainerSid))
        {
            var workerSid = new SecurityIdentifier(appContainerSid);
            security.AddAccessRule(new PipeAccessRule(
                workerSid,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task PerformHandshakeAsync()
    {
        if (_reader == null || _writer == null || _writeLock == null)
            throw new InvalidOperationException("Sandbox IPC stream is not initialized.");

        var hello = await SandboxProtocol.ReadAsync(_reader, _lifetime.Token).ConfigureAwait(false)
            ?? throw new IOException("Sandbox worker closed the pipe before handshaking.");

        if (!string.Equals(hello.Op, "hello", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sandbox worker did not send a hello message.");

        var helloPayload = SandboxProtocol.GetPayload<SandboxProtocol.HelloPayload>(hello)
            ?? throw new UnauthorizedAccessException("Sandbox hello payload is missing.");

        if (!Enum.TryParse<TrustMode>(helloPayload.TrustMode, true, out var helloTrust) ||
            helloTrust != Settings.TrustMode)
            throw new UnauthorizedAccessException("Sandbox trust mode mismatch.");

        if (_worker != null && helloPayload.ProcessId != _worker.ProcessId)
            throw new UnauthorizedAccessException("Sandbox worker process ID mismatch.");

        var reply = new SandboxProtocol.HelloReply(
            true,
            System.Text.Json.JsonSerializer.Serialize(Settings, SandboxProtocol.JsonOptions),
            StartupUrl,
            SessionId,
            "");
        await WriteResponseAsync(hello.Id, reply, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            if (_reader == null) return;

            while (!_lifetime.IsCancellationRequested)
            {
                SandboxProtocol.Envelope? envelope;
                try
                {
                    envelope = await SandboxProtocol.ReadAsync(_reader, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (envelope == null)
                    break;

                try
                {
                    switch (envelope.Op)
                    {
                        case "fetch":
                            await HandleFetchAsync(envelope).ConfigureAwait(false);
                            break;
                        case "file.read":
                            await HandleFileReadAsync(envelope).ConfigureAwait(false);
                            break;
                        case "file.open":
                            await HandleFileOpenAsync(envelope).ConfigureAwait(false);
                            break;
                        case "settings.save":
                            await HandleSettingsSaveAsync(envelope).ConfigureAwait(false);
                            break;
                        case "window.spawn":
                            await HandleSpawnAsync(envelope).ConfigureAwait(false);
                            break;
                        case "window.restart":
                            await HandleRestartAsync(envelope).ConfigureAwait(false);
                            break;
                        case "window.closed":
                            await WriteResponseAsync(envelope.Id, new SandboxProtocol.AckPayload(true), _lifetime.Token)
                                .ConfigureAwait(false);
                            return;
                        default:
                            await WriteResponseAsync(envelope.Id,
                                new SandboxProtocol.AckPayload(false, "Unknown sandbox operation."), _lifetime.Token)
                                .ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        await WriteResponseAsync(envelope.Id,
                            new SandboxProtocol.AckPayload(false, ex.Message), _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch { break; }
                }
            }
        }
        finally
        {
            Dispose();
        }
    }

    private async Task HandleFetchAsync(SandboxProtocol.Envelope envelope)
    {
        var request = SandboxProtocol.GetPayload<SandboxProtocol.FetchPayload>(envelope)
            ?? throw new InvalidDataException("Missing fetch payload.");

        var result = await FetchAsync(request, _lifetime.Token).ConfigureAwait(false);
        await WriteResponseAsync(envelope.Id, result, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task<SandboxProtocol.FetchReply> FetchAsync(
        SandboxProtocol.FetchPayload request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "Only GET and POST are allowed through the sandbox broker.", null);
        }

        ParsedUrl parsed;
        try { parsed = ParsedUrl.Parse(request.Url); }
        catch (Exception ex)
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url, ex.Message, null);
        }

        if (!parsed.IsHttp)
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "Only HTTP(S) network resources are brokered.", null);

        if (!NetworkPolicy.IsAllowed(parsed, Settings.TrustMode))
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "Network destination is blocked by the current isolation policy.", null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "DNS lookup failed: " + ex.Message, null);
        }

        var safeAddress = addresses.FirstOrDefault(NetworkPolicy.IsPublicAddress);
        if (safeAddress == null)
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "DNS resolved only to blocked/private addresses.", null);
        }

        byte[]? body = null;
        if (!string.IsNullOrEmpty(request.BodyBase64))
        {
            try { body = Convert.FromBase64String(request.BodyBase64); }
            catch
            {
                return new SandboxProtocol.FetchReply(false, 0,
                    new Dictionary<string, string>(), "", "", null, request.Url,
                    "Request body is not valid base64.", null);
            }
        }

        if (body?.Length > 8 * 1024 * 1024)
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "Request body exceeds broker limit.", null);

        byte[] wire = BuildBrokerWire(request, parsed, body);
        HttpResult result = await _brokerHttp.SendRawAsync(
            parsed,
            wire,
            cancellationToken,
            safeAddress).ConfigureAwait(false);

        if (result is CertError cert)
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                null, cert.Message);
        }

        if (result is not HttpSuccess success)
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                result is HttpError error ? error.Message : "Broker request failed.", null);
        }

        if (success.Body.Length > Math.Max(1, Math.Min(request.MaxBytes, 8 * 1024 * 1024)))
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "Response exceeded the sandbox broker size limit.", null);
        }

        if (string.Equals(request.ResourceType, nameof(ResourceKind.Image), StringComparison.OrdinalIgnoreCase) &&
            (!ImageSafety.IsSafeImageResponse(success.ContentType, success.Body, request.MaxBytes)))
        {
            return new SandboxProtocol.FetchReply(false, 0,
                new Dictionary<string, string>(), "", "", null, request.Url,
                "Host image validation rejected the response.", null);
        }

        return new SandboxProtocol.FetchReply(
            true,
            success.StatusCode,
            new Dictionary<string, string>(success.Headers, StringComparer.OrdinalIgnoreCase),
            success.ContentType,
            success.Charset,
            Convert.ToBase64String(success.Body),
            success.EffectiveUrl,
            null,
            null);
    }

    private static byte[] BuildBrokerWire(
        SandboxProtocol.FetchPayload request,
        ParsedUrl parsed,
        byte[]? body)
    {
        var sb = new StringBuilder(512);
        string requestPath = string.IsNullOrEmpty(parsed.Query)
            ? parsed.Path
            : parsed.Path + "?" + parsed.Query;

        sb.Append(request.Method.ToUpperInvariant()).Append(' ')
            .Append(requestPath).Append(" HTTP/1.0\r\n");
        sb.Append("Host: ").Append(parsed.Host);
        int defaultPort = parsed.Scheme == "https" ? 443 : 80;
        if (parsed.Port != defaultPort)
            sb.Append(':').Append(parsed.Port);
        sb.Append("\r\n");

        string? userAgent = TryHeader(request.Headers, "User-Agent");
        sb.Append("User-Agent: ").Append(userAgent ?? "Retro96/1.0").Append("\r\n");
        AppendHeaderIfPresent(sb, request.Headers, "Accept");
        AppendHeaderIfPresent(sb, request.Headers, "Accept-Charset");
        AppendHeaderIfPresent(sb, request.Headers, "Cookie");
        AppendHeaderIfPresent(sb, request.Headers, "Authorization");
        AppendHeaderIfPresent(sb, request.Headers, "Referer");

        if (body is { Length: > 0 })
        {
            sb.Append("Content-Type: ")
                .Append(TryHeader(request.Headers, "Content-Type") ?? "application/x-www-form-urlencoded")
                .Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");
        byte[] headers = Encoding.ASCII.GetBytes(sb.ToString());
        if (body is not { Length: > 0 }) return headers;

        var wire = new byte[headers.Length + body.Length];
        Buffer.BlockCopy(headers, 0, wire, 0, headers.Length);
        Buffer.BlockCopy(body, 0, wire, headers.Length, body.Length);
        return wire;
    }

    private static string? TryHeader(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static void AppendHeaderIfPresent(StringBuilder sb,
                                              Dictionary<string, string> headers,
                                              string name)
    {
        if (headers.TryGetValue(name, out var value) && value.Length > 0)
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private async Task HandleFileReadAsync(SandboxProtocol.Envelope envelope)
    {
        var request = SandboxProtocol.GetPayload<SandboxProtocol.FileReadPayload>(envelope)
            ?? throw new InvalidDataException("Missing file.read payload.");

        string? path = ValidateFilePath(request.Path);
        if (path == null)
        {
            await WriteResponseAsync(envelope.Id,
                new SandboxProtocol.FileReply(false, null, "File access denied by broker policy."),
                _lifetime.Token).ConfigureAwait(false);
            return;
        }

        try
        {
            var info = new FileInfo(path);
            int max = Math.Clamp(request.MaxBytes, 1, 32 * 1024 * 1024);
            if (!info.Exists || info.Length > max)
            {
                await WriteResponseAsync(envelope.Id,
                    new SandboxProtocol.FileReply(false, null, "File does not exist or exceeds the broker limit."),
                    _lifetime.Token).ConfigureAwait(false);
                return;
            }

            byte[] bytes = await File.ReadAllBytesAsync(path, _lifetime.Token).ConfigureAwait(false);
            await WriteResponseAsync(envelope.Id,
                new SandboxProtocol.FileReply(true, Convert.ToBase64String(bytes)),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(envelope.Id,
                new SandboxProtocol.FileReply(false, null, ex.Message),
                _lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task HandleFileOpenAsync(SandboxProtocol.Envelope envelope)
    {
        var reply = await _owner.RunOnUiAsync(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "HTML files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*",
                Title = "Open HTML File"
            };

            if (dialog.ShowDialog() != DialogResult.OK)
                return new SandboxProtocol.OpenFileReply(false, null, null, null, "Cancelled");

            string fullPath = Path.GetFullPath(dialog.FileName);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > 32 * 1024 * 1024)
                return new SandboxProtocol.OpenFileReply(false, null, null, null, "File is missing or exceeds the 32 MiB broker limit.");

            byte[] bytes = File.ReadAllBytes(fullPath);
            return new SandboxProtocol.OpenFileReply(
                true,
                fullPath,
                "file://" + fullPath.Replace('\\', '/'),
                Convert.ToBase64String(bytes),
                "");
        }).ConfigureAwait(false);

        if (reply.Success && !string.IsNullOrWhiteSpace(reply.Path))
        {
            lock (_stateLock)
                _localRoot = Path.GetDirectoryName(reply.Path);
        }

        await WriteResponseAsync(envelope.Id, reply, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task HandleSettingsSaveAsync(SandboxProtocol.Envelope envelope)
    {
        var payload = SandboxProtocol.GetPayload<SandboxProtocol.SettingsPayload>(envelope)
            ?? throw new InvalidDataException("Missing settings payload.");

        var updated = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(
            payload.SettingsJson,
            SandboxProtocol.JsonOptions);
        if (updated == null)
        {
            await WriteResponseAsync(envelope.Id,
                new SandboxProtocol.AckPayload(false, "Invalid settings JSON."), _lifetime.Token).ConfigureAwait(false);
            return;
        }

        bool ok = await _owner.SaveSessionSettingsAsync(this, updated).ConfigureAwait(false);
        await WriteResponseAsync(envelope.Id,
            new SandboxProtocol.AckPayload(ok, ok ? "" : "Could not save settings."), _lifetime.Token)
            .ConfigureAwait(false);
    }

    private async Task HandleSpawnAsync(SandboxProtocol.Envelope envelope)
    {
        var payload = SandboxProtocol.GetPayload<SandboxProtocol.SpawnPayload>(envelope)
            ?? throw new InvalidDataException("Missing window.spawn payload.");
        bool ok = await _owner.SpawnWindowAsync(this, payload.Url).ConfigureAwait(false);
        await WriteResponseAsync(envelope.Id,
            new SandboxProtocol.AckPayload(ok, ok ? "" : "New window blocked or failed."), _lifetime.Token)
            .ConfigureAwait(false);
    }

    private async Task HandleRestartAsync(SandboxProtocol.Envelope envelope)
    {
        var payload = SandboxProtocol.GetPayload<SandboxProtocol.RestartPayload>(envelope)
            ?? throw new InvalidDataException("Missing window.restart payload.");
        bool ok = await _owner.RestartWindowAsync(this, payload.Url).ConfigureAwait(false);
        await WriteResponseAsync(envelope.Id,
            new SandboxProtocol.AckPayload(ok, ok ? "" : "Worker restart failed."), _lifetime.Token)
            .ConfigureAwait(false);
    }

    private static bool ContainsReparsePoint(string root, string fullPath)
    {
        try
        {
            string current = Path.GetFullPath(root);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;

            string relative = Path.GetRelativePath(current, fullPath);
            if (relative == ".")
                return false;

            foreach (string part in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!File.Exists(current) && !Directory.Exists(current))
                    break;

                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private string? ValidateFilePath(string rawPath)
    {
        if (Settings.TrustMode != TrustMode.Low && string.IsNullOrWhiteSpace(_localRoot))
            return null;

        try
        {
            string full = Path.GetFullPath(rawPath);
            string? root;
            lock (_stateLock) root = _localRoot;

            if (string.IsNullOrWhiteSpace(root))
                return null;

            root = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string prefix = root + Path.DirectorySeparatorChar;
            bool inside = full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                          full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (!inside)
                return null;

            // Do not let a path-prefix check be bypassed by a junction, symlink,
            // or other reparse point inside the user-selected local root.
            if (Settings.TrustMode is (TrustMode.High or TrustMode.Medium) &&
                ContainsReparsePoint(root, full))
                return null;

            return full;
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteResponseAsync(string id, object payload, CancellationToken ct)
    {
        if (_writer == null || _writeLock == null)
            return;

        await SandboxProtocol.WriteAsync(_writer, "response", id, payload, _writeLock, ct)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        try { _pipe?.Disconnect(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try { _writeLock?.Dispose(); } catch { }
        try { _worker?.Dispose(); } catch { }
        if (_worker == null) WindowsSecurity.DeleteAppContainer(_profileName);
        try { _lifetime.Dispose(); } catch { }
        _owner.SessionClosed(this);
    }

    ~SandboxWorkerSession()
    {
        Dispose();
    }
}

internal static class NetworkPolicy
{
    public static bool IsAllowed(ParsedUrl url, TrustMode mode)
    {
        if (!url.IsHttp) return false;
        if (string.IsNullOrWhiteSpace(url.Host)) return false;
        if (url.Host.Contains('@')) return false;
        if (url.Port != 80 && url.Port != 443) return mode == TrustMode.Low;
        if (url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return false;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            // RFC 4193 unique-local fc00::/7
            return (b[0] & 0xFE) != 0xFC;
        }

        byte[] v4 = address.GetAddressBytes();
        int a = v4[0], b0 = v4[1], b1 = v4[2];
        if (a == 0 || a == 10 || a == 127 || a >= 224)
            return false;
        if (a == 169 && b0 == 254)
            return false;
        if (a == 172 && b0 is >= 16 and <= 31)
            return false;
        if (a == 192 && b0 == 168)
            return false;
        if (a == 192 && b0 == 0 && b1 == 0)
            return false;
        if (a == 192 && b0 == 0 && b1 == 2)
            return false;
        if (a == 192 && b0 == 88 && b1 == 99)
            return false;
        if (a == 192 && b0 == 18)
            return false;
        if (a == 198 && b0 == 18)
            return false;
        if (a == 198 && b0 == 19)
            return false;
        if (a == 198 && b0 == 51 && b1 == 100)
            return false;
        if (a == 203 && b0 == 0 && b1 == 113)
            return false;
        if (a == 100 && b0 is >= 64 and <= 127)
            return false;
        return true;
    }
}

internal static class ImageSafety
{
    public static bool IsSafeImageResponse(string contentType, byte[] body, int maxBytes)
    {
        if (body.Length == 0 || body.Length > Math.Clamp(maxBytes, 1, 16 * 1024 * 1024))
            return false;

        string type = contentType ?? "";
        bool declaredImage = type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        if (!declaredImage)
            return false;

        return IsKnownImageSignature(body);
    }

    private static bool IsKnownImageSignature(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return true;
        if (bytes.Length >= 6 &&
            ((bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8' && bytes[4] is (byte)'7' or (byte)'9' && bytes[5] == 'a')))
            return true;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return true;
        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return true;
        // Legacy XBM is text-based. Keep it bounded and require the markers
        // used by the era's image format rather than accepting arbitrary text.
        if (bytes.Length <= 1024 * 1024)
        {
            string text = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (text.Contains("#define", StringComparison.Ordinal) &&
                text.Contains("_width", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("_height", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
