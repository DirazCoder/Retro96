using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    /// <summary>
    /// Upper bound on live workers. Each worker is a process plus a copied
    /// runtime directory, and a compromised worker can ask the broker to spawn
    /// windows, so the count must be capped to prevent resource exhaustion.
    /// </summary>
    private const int MaxConcurrentSessions = 12;

    private readonly Control _dispatcher;
    private readonly ConcurrentDictionary<string, SandboxWorkerSession> _sessions = new();
    private readonly object _settingsLock = new();

    // Template for subsequently opened windows; guarded by _settingsLock.
    private UserSettings _settings;
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
            UserSettings settings;
            lock (_settingsLock) settings = _settings.Clone();

            var session = await StartWorkerAsync(settings, settings.HomePageUrl).ConfigureAwait(false);
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

        if (_sessions.Count >= MaxConcurrentSessions)
        {
            StartupDiagnostics.Step("HOST", "Worker launch refused: session cap of " + MaxConcurrentSessions + " reached.");
            return null;
        }

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

        // Higher enum values are weaker trust levels (High < Medium < Low).
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

        // The saved settings become the template for subsequently opened
        // windows. Replace the whole object rather than copying a fixed field
        // list so every setting — including the advanced engine toggles —
        // propagates to new windows.
        lock (_settingsLock)
        {
            _settings = clone;
        }

        session.SetSettings(clone);
        return true;
    }

    internal async Task<bool> SpawnWindowAsync(SandboxWorkerSession parent, string url)
    {
        if (!IsScriptedWindowAllowed(parent.Settings, url))
            return false;

        try
        {
            UserSettings settings;
            lock (_settingsLock) settings = _settings.Clone();

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
            lock (_settingsLock) settings = _settings.Clone();

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
        Control dispatcher = _dispatcher;

        void Invoke()
        {
            try { tcs.TrySetResult(action()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        try
        {
            if (dispatcher.IsDisposed)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(_dispatcher)));
            }
            else if (dispatcher.InvokeRequired)
            {
                dispatcher.BeginInvoke((Action)Invoke);
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
    private const int ConnectTimeoutSeconds = 20;

    private readonly SandboxBrokerApplicationContext _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private SandboxProtocol.EnvelopeReader? _envelopeReader;
    private SemaphoreSlim? _writeLock;
    private WindowsSecurity.WorkerProcess? _worker;
    private string? _profileName;
    private string? _appContainerSid;
    private string? _localRoot;
    private UserSettings _settings;
    private int _disposed;
    private int _fileOpenInFlight;

    private readonly Retro96.Engine.Network.HttpClient _brokerHttp = new(allowInvalidCertificates: false);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

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
        UserSettings settings = Settings; // snapshot; SetSettings may run later
        StartupDiagnostics.Step("HOST", "Session StartAsync. Session=" + SessionId + " Trust=" + settings.TrustMode + " Pipe=" + PipeName);
        if (_worker != null)
            throw new InvalidOperationException("Sandbox worker already started.");

        if (settings.TrustMode is TrustMode.High or TrustMode.Medium)
        {
            StartupDiagnostics.Step("HOST", "Creating AppContainer profile.");
            var profile = WindowsSecurity.CreateAppContainer(settings.TrustMode);
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

        if (settings.TrustMode is TrustMode.High or TrustMode.Medium)
        {
            StartupDiagnostics.Step("HOST", "Preparing AppContainer worker runtime.");
            workerRuntimeDirectory = WindowsSecurity.PrepareWorkerRuntime(
                Path.GetDirectoryName(workerExecutable)!,
                _appContainerSid!);
            workerExecutable = Path.Combine(workerRuntimeDirectory, Path.GetFileName(workerExecutable));
            usesRuntimeShadow = true;
            StartupDiagnostics.Step("HOST", "Worker runtime prepared: " + workerRuntimeDirectory);
        }

        bool useLpac = settings.TrustMode == TrustMode.High &&
                       !File.Exists(Path.Combine(workerRuntimeDirectory,
                           Path.GetFileNameWithoutExtension(workerExecutable) + ".runtimeconfig.json"));

        try
        {
            StartupDiagnostics.Step("HOST", "Launching worker process. LPAC=" + useLpac + " WorkingDir=" + workerRuntimeDirectory + " Executable=" + workerExecutable);
            _worker = WindowsSecurity.StartWorker(
                settings.TrustMode,
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
            // StartWorker already tears down the profile and its runtime copy
            // when it fails; this delete is a belt-and-braces retry.
            if (usesRuntimeShadow)
                TryDeleteRuntimeDirectory(workerRuntimeDirectory);
            throw;
        }

        // StartWorker resumes the child only after it is inside the Job, so a
        // live _worker here is a running process. Wait for the pipe connection
        // while watching for an early worker death so the failure surfaces
        // with the real exit code instead of a bare timeout.
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
            StartupDiagnostics.Step("HOST", "Waiting for worker pipe connection.");

            Task connectTask = _pipe.WaitForConnectionAsync(connectCts.Token);
            while (!connectTask.IsCompleted)
            {
                if (_worker.HasExited(out uint exitCode))
                {
                    connectCts.Cancel();
                    try { await connectTask.ConfigureAwait(false); }
                    catch { /* observe the abandoned wait */ }

                    StartupDiagnostics.Step("HOST", "Worker exited before pipe connection. ExitCode=0x" + exitCode.ToString("X8"));
                    throw new InvalidOperationException(
                        $"Retro96 content worker exited before connecting to the broker pipe. Exit code: 0x{exitCode:X8} ({exitCode}).");
                }

                await Task.WhenAny(connectTask, Task.Delay(25, connectCts.Token)).ConfigureAwait(false);
            }

            await connectTask.ConfigureAwait(false);
        }

        // Defense in depth: the pipe ACL restricts callers to this worker's
        // AppContainer (High/Medium) or the current user (Low), and the
        // kernel-reported client PID must match the process this broker
        // launched. The hello payload's PID is self-reported and is not
        // trusted on its own.
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid) ||
            clientPid != _worker.ProcessId)
        {
            StartupDiagnostics.Step("HOST", "Pipe client PID mismatch. Client=" + clientPid + " Worker=" + _worker.ProcessId);
            throw new UnauthorizedAccessException("Sandbox pipe client is not the launched worker process.");
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n"
        };
        _envelopeReader = new SandboxProtocol.EnvelopeReader(_reader);

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

        // `dotnet run` may leave the managed host as the current process. The
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

        // The pipe must be reachable by the broker's own user identity and by
        // the exact per-worker AppContainer SID. Synchronize is included
        // because the AppContainer lowbox access path can require it even
        // though the broker only performs read/write traffic.
        var userSid = new SecurityIdentifier(WindowsSecurity.CurrentUserSid);
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        if (!string.IsNullOrWhiteSpace(appContainerSid))
        {
            var workerSid = new SecurityIdentifier(appContainerSid);
            security.AddAccessRule(new PipeAccessRule(
                workerSid,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));
        }

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task PerformHandshakeAsync()
    {
        if (_envelopeReader == null || _writer == null || _writeLock == null)
            throw new InvalidOperationException("Sandbox IPC stream is not initialized.");

        var hello = await _envelopeReader.ReadAsync(_lifetime.Token).ConfigureAwait(false)
            ?? throw new IOException("Sandbox worker closed the pipe before handshaking.");

        if (!string.Equals(hello.Op, "hello", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sandbox worker did not send a hello message.");

        var helloPayload = SandboxProtocol.GetPayload<SandboxProtocol.HelloPayload>(hello)
            ?? throw new UnauthorizedAccessException("Sandbox hello payload is missing.");

        if (!Enum.TryParse<TrustMode>(helloPayload.TrustMode, true, out var helloTrust) ||
            helloTrust != Settings.TrustMode)
            throw new UnauthorizedAccessException("Sandbox trust mode mismatch.");

        // Secondary to the kernel-checked pipe client PID.
        if (_worker != null && helloPayload.ProcessId != _worker.ProcessId)
            throw new UnauthorizedAccessException("Sandbox worker process ID mismatch.");

        var reply = new SandboxProtocol.HelloReply(
            true,
            JsonSerializer.Serialize(Settings, SandboxProtocol.JsonOptions),
            StartupUrl,
            SessionId,
            "");
        await WriteResponseAsync(hello.Id, reply, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            if (_envelopeReader == null) return;

            while (!_lifetime.IsCancellationRequested)
            {
                SandboxProtocol.Envelope? envelope;
                try
                {
                    envelope = await _envelopeReader.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (envelope == null)
                    break; // worker closed the pipe

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
        catch (Exception ex)
        {
            // Malformed messages (bad JSON, oversized lines, invalid ops) and
            // broken pipes end the session here: this loop is fire-and-forget,
            // so the failure must be observed rather than escaping unobserved.
            StartupDiagnostics.Error("HOST", "Sandbox worker session loop terminated", ex);
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
        string url = request.Url ?? "";
        SandboxProtocol.FetchReply Fail(string? error = null, string? certError = null) =>
            new(false, 0, new Dictionary<string, string>(), "", "", null, url, error, certError);

        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Only GET and POST are allowed through the sandbox broker.");
        }

        // The worker is trusted only as far as the sandbox boundary: anything
        // it sends that ends up in the raw HTTP request must be rejected if it
        // could inject headers or request-line content.
        Dictionary<string, string> headers = request.Headers ?? new Dictionary<string, string>();
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (ContainsHeaderInjection(header.Key) || ContainsHeaderInjection(header.Value))
                return Fail("Request header contains characters that cannot be sent safely.");
        }

        ParsedUrl parsed;
        try { parsed = ParsedUrl.Parse(url); }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        if (!parsed.IsHttp)
            return Fail("Only HTTP(S) network resources are brokered.");

        if (ContainsHeaderInjection(parsed.Host))
            return Fail("Request URL contains characters that cannot be sent safely.");

        string requestTarget = string.IsNullOrEmpty(parsed.Query)
            ? parsed.Path
            : parsed.Path + "?" + parsed.Query;
        requestTarget = requestTarget.Replace(" ", "%20");
        if (ContainsControlCharacter(requestTarget))
            return Fail("Request URL contains characters that cannot be sent safely.");

        if (!NetworkPolicy.IsAllowed(parsed, Settings.TrustMode))
            return Fail("Network destination is blocked by the current isolation policy.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail("DNS lookup failed: " + ex.Message);
        }

        // Hand the validated address to the engine so the actual connection is
        // pinned to it; re-resolving at connect time would allow DNS rebinding
        // to private ranges after this check has passed.
        IPAddress? safeAddress = addresses.FirstOrDefault(NetworkPolicy.IsPublicAddress);
        if (safeAddress == null)
            return Fail("DNS resolved only to blocked/private addresses.");

        byte[]? body = null;
        if (!string.IsNullOrEmpty(request.BodyBase64))
        {
            try { body = Convert.FromBase64String(request.BodyBase64); }
            catch
            {
                return Fail("Request body is not valid base64.");
            }
        }

        if (body is { Length: > SandboxProtocol.MaxFetchBodyBytes })
            return Fail("Request body exceeds broker limit.");

        byte[] wire = BuildBrokerWire(request.Method, requestTarget, parsed, headers, body);
        int maxBytes = Math.Clamp(request.MaxBytes, 1, SandboxProtocol.MaxFetchBodyBytes);
        HttpResult result = await _brokerHttp.SendRawAsync(
            parsed,
            wire,
            cancellationToken,
            safeAddress,
            maxBytes).ConfigureAwait(false);
        if (result is CertError cert)
            return Fail(null, cert.Message);

        if (result is not HttpSuccess success)
            return Fail(result is HttpError error ? error.Message : "Broker request failed.");

        if (success.Body.Length > maxBytes)
            return Fail("Response exceeded the sandbox broker size limit.");

        if (string.Equals(request.ResourceType, nameof(ResourceKind.Image), StringComparison.OrdinalIgnoreCase) &&
            !ImageSafety.IsSafeImageResponse(success.ContentType, success.Body, request.MaxBytes))
        {
            return Fail("Host image validation rejected the response.");
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

    private static bool ContainsHeaderInjection(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
            if (c == '\r' || c == '\n' || c == '\0') return true;
        return false;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
            if (c <= ' ') return true;
        return false;
    }

    private static byte[] BuildBrokerWire(
        string method,
        string requestTarget,
        ParsedUrl parsed,
        Dictionary<string, string> headers,
        byte[]? body)
    {
        var sb = new StringBuilder(512);

        sb.Append(method.ToUpperInvariant()).Append(' ')
            .Append(requestTarget).Append(" HTTP/1.0\r\n");

        sb.Append("Host: ").Append(parsed.Host);
        int defaultPort = parsed.Scheme == "https" ? 443 : 80;
        if (parsed.Port != defaultPort)
            sb.Append(':').Append(parsed.Port);
        sb.Append("\r\n");

        sb.Append("User-Agent: ").Append(TryHeader(headers, "User-Agent") ?? "Retro96/1.0").Append("\r\n");
        AppendHeaderIfPresent(sb, headers, "Accept");
        AppendHeaderIfPresent(sb, headers, "Accept-Charset");
        AppendHeaderIfPresent(sb, headers, "Cookie");
        AppendHeaderIfPresent(sb, headers, "Authorization");
        AppendHeaderIfPresent(sb, headers, "Referer");

        if (body is { Length: > 0 })
        {
            sb.Append("Content-Type: ")
                .Append(TryHeader(headers, "Content-Type") ?? "application/x-www-form-urlencoded")
                .Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        if (body is not { Length: > 0 })
            return headerBytes;

        var wire = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, wire, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, wire, headerBytes.Length, body.Length);
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
            int max = Math.Clamp(request.MaxBytes, 1, SandboxProtocol.MaxFileBytes);
            byte[]? bytes = await TryReadFileAsync(path, max, _lifetime.Token).ConfigureAwait(false);
            if (bytes == null)
            {
                await WriteResponseAsync(envelope.Id,
                    new SandboxProtocol.FileReply(false, null, "File does not exist or exceeds the broker limit."),
                    _lifetime.Token).ConfigureAwait(false);
                return;
            }

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
        // A compromised worker could otherwise stack modal dialogs by
        // replaying file.open; allow one dialog per session at a time.
        if (Interlocked.CompareExchange(ref _fileOpenInFlight, 1, 0) != 0)
        {
            await WriteResponseAsync(envelope.Id,
                new SandboxProtocol.OpenFileReply(false, null, null, null, "A file dialog is already open."),
                _lifetime.Token).ConfigureAwait(false);
            return;
        }

        try
        {
            // Only the dialog runs on the UI thread; the file is read and
            // encoded off it so a slow disk cannot freeze the broker UI.
            string? fileName = await _owner.RunOnUiAsync(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = "HTML files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*",
                    Title = "Open HTML File"
                };
                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }).ConfigureAwait(false);

            if (fileName == null)
            {
                await WriteResponseAsync(envelope.Id,
                    new SandboxProtocol.OpenFileReply(false, null, null, null, "Cancelled"),
                    _lifetime.Token).ConfigureAwait(false);
                return;
            }

            SandboxProtocol.OpenFileReply reply;
            try
            {
                string fullPath = Path.GetFullPath(fileName);
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length > SandboxProtocol.MaxFileBytes)
                {
                    reply = new SandboxProtocol.OpenFileReply(false, null, null, null,
                        "File is missing or exceeds the " + (SandboxProtocol.MaxFileBytes / (1024 * 1024)) + " MiB broker limit.");
                }
                else
                {
                    byte[]? bytes = await TryReadFileAsync(fullPath, SandboxProtocol.MaxFileBytes, _lifetime.Token).ConfigureAwait(false);
                    reply = bytes == null
                        ? new SandboxProtocol.OpenFileReply(false, null, null, null, "File is missing or unreadable.")
                        : new SandboxProtocol.OpenFileReply(
                            true,
                            fullPath,
                            FileUrls.CanonicalFileUrl(fullPath),
                            Convert.ToBase64String(bytes),
                            "");
                }
            }
            catch (Exception ex)
            {
                reply = new SandboxProtocol.OpenFileReply(false, null, null, null, ex.Message);
            }

            if (reply.Success && !string.IsNullOrWhiteSpace(reply.Path))
            {
                lock (_stateLock)
                    _localRoot = Path.GetDirectoryName(reply.Path);
            }

            await WriteResponseAsync(envelope.Id, reply, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _fileOpenInFlight, 0);
        }
    }

    /// <summary>
    /// Reads a local file, enforcing the size limit at read time (not only in
    /// a pre-check) so a file that grows between check and read cannot exceed
    /// the limit. Returns null when the file is missing, unreadable or too
    /// large.
    /// </summary>
    private static async Task<byte[]?> TryReadFileAsync(string path, int limit, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > limit)
                return null;

            int length = checked((int)stream.Length);
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break; // file shrank mid-read
                offset += read;
            }

            if (offset != length)
                Array.Resize(ref buffer, offset);
            return buffer;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task HandleSettingsSaveAsync(SandboxProtocol.Envelope envelope)
    {
        var payload = SandboxProtocol.GetPayload<SandboxProtocol.SettingsPayload>(envelope)
            ?? throw new InvalidDataException("Missing settings payload.");

        UserSettings? updated = null;
        if (!string.IsNullOrWhiteSpace(payload.SettingsJson))
        {
            try
            {
                updated = JsonSerializer.Deserialize<UserSettings>(payload.SettingsJson, SandboxProtocol.JsonOptions);
            }
            catch (JsonException)
            {
                updated = null;
            }
        }

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
            return true; // fail closed
        }

        return false;
    }

    private string? ValidateFilePath(string rawPath)
    {
        // Every mode requires a user-selected local root; pages can never name
        // arbitrary filesystem paths through the broker.
        try
        {
            string? root;
            lock (_stateLock) root = _localRoot;
            if (string.IsNullOrWhiteSpace(root))
                return null;

            string full = Path.GetFullPath(rawPath);

            root = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string prefix = root + Path.DirectorySeparatorChar;
            bool inside = full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                          full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (!inside)
                return null;

            // A path-prefix check alone can be bypassed by a junction, symlink
            // or other reparse point inside the selected root; this applies to
            // every trust mode.
            if (ContainsReparsePoint(root, full))
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
        if (_worker == null)
            WindowsSecurity.DeleteAppContainer(_profileName);
        try { _lifetime.Dispose(); } catch { }
        _owner.SessionClosed(this);
    }
}

/// <summary>
/// Broker-side network allow rules. Connection targets are additionally
/// validated by <see cref="IsPublicAddress"/>, and the validated address is
/// pinned for the actual connection, which prevents DNS rebinding to private
/// ranges after the check.
/// </summary>
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
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4-mapped IPv6 address (::ffff:a.b.c.d) carries an IPv4 address
        // that would otherwise skip every IPv4 rule below and allow reaching
        // private ranges.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                                      // fc00::/7 unique-local
            if (b[0] == 0x20 && b[1] == 0x02) return false;                               // 2002::/16 6to4 (tunnels IPv4)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;     // 2001:0::/32 Teredo
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 documentation
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return false; // 64:ff9b::/96 NAT64
            return true;
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
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsKnownImageSignature(body);
    }

    private static bool IsKnownImageSignature(byte[] bytes)
    {
        // PNG
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return true;
        // GIF87a / GIF89a
        if (bytes.Length >= 6 &&
            bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8' &&
            (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == 'a')
            return true;
        // JPEG
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;
        // BMP
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return true;
        // RIFF....WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return true;
        // ICO: reserved=0, type=1 (icon) — era-correct favicons.
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
            return true;
        // Legacy XBM is text-based. Keep it bounded and require the markers
        // the format actually uses rather than accepting arbitrary text.
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