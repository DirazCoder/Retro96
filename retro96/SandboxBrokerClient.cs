using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Retro96;

/// <summary>
/// Worker-side broker client. High/Medium workers cannot use the host
/// filesystem or direct network policy; they ask this broker over a
/// per-window named pipe whose ACL contains the worker AppContainer SID.
/// </summary>
internal sealed class SandboxBrokerClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SandboxProtocol.EnvelopeReader _envelopeReader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SandboxProtocol.Envelope>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readerLoop;
    private int _disposed;

    private SandboxBrokerClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n"
        };
        _envelopeReader = new SandboxProtocol.EnvelopeReader(_reader);
        _readerLoop = Task.Run(ReadLoopAsync);
    }

    public UserSettings Settings { get; private set; } = new();
    public string StartupUrl { get; private set; } = "retro96:home";
    public string SessionId { get; private set; } = "";

    public static async Task<SandboxBrokerClient> ConnectAsync(
        string pipeName,
        TrustMode trustMode,
        CancellationToken cancellationToken = default)
    {
        StartupDiagnostics.Step("WORKER", "Creating NamedPipeClientStream for " + pipeName);
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        SandboxBrokerClient? client = null;
        try
        {
            await pipe.ConnectAsync(15_000, cancellationToken).ConfigureAwait(false);
            StartupDiagnostics.Step("WORKER", "NamedPipeClientStream.ConnectAsync succeeded.");

            client = new SandboxBrokerClient(pipe);
            StartupDiagnostics.Step("WORKER", "Sending broker hello request.");
            var reply = await client.RequestAsync<SandboxProtocol.HelloReply>(
                "hello",
                new SandboxProtocol.HelloPayload(trustMode.ToString(), Environment.ProcessId),
                cancellationToken).ConfigureAwait(false);

            StartupDiagnostics.Step("WORKER", "Broker hello reply received. Accepted=" + reply.Accepted);
            if (!reply.Accepted)
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(reply.Error) ? "Sandbox handshake rejected." : reply.Error);

            client.Settings = DeserializeSettings(reply.SettingsJson);
            client.StartupUrl = string.IsNullOrWhiteSpace(reply.StartupUrl)
                ? client.Settings.HomePageUrl
                : reply.StartupUrl;
            client.SessionId = reply.SessionId;
            return client;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Error("WORKER", "SandboxBrokerClient.ConnectAsync failed", ex);
            if (client != null)
                client.Dispose(); // also disposes the pipe and unwinds the reader loop
            else
                pipe.Dispose();
            throw;
        }
    }

    private static UserSettings DeserializeSettings(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(json, SandboxProtocol.JsonOptions);
            return settings ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public async Task<SandboxProtocol.FetchReply> FetchRawAsync(
        string method,
        string url,
        Dictionary<string, string>? headers,
        byte[]? body,
        string resourceType,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        headers ??= new Dictionary<string, string>();
        string? bodyBase64 = body is { Length: > 0 } ? Convert.ToBase64String(body) : null;
        return await RequestAsync<SandboxProtocol.FetchReply>(
            "fetch",
            new SandboxProtocol.FetchPayload(
                method,
                url,
                headers,
                bodyBase64,
                resourceType,
                maxBytes),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReadFileAsync(
        string path,
        string resourceType,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync<SandboxProtocol.FileReply>(
            "file.read",
            new SandboxProtocol.FileReadPayload(path, resourceType, maxBytes),
            cancellationToken).ConfigureAwait(false);

        if (!reply.Success || string.IsNullOrEmpty(reply.BodyBase64))
            return null;

        try { return Convert.FromBase64String(reply.BodyBase64); }
        catch { return null; }
    }

    public async Task<SandboxProtocol.OpenFileReply> OpenFileAsync(
        CancellationToken cancellationToken = default)
    {
        return await RequestAsync<SandboxProtocol.OpenFileReply>(
            "file.open", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SaveSettingsAsync(UserSettings settings,
                                               CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(settings, SandboxProtocol.JsonOptions);
        var reply = await RequestAsync<SandboxProtocol.AckPayload>(
            "settings.save",
            new SandboxProtocol.SettingsPayload(json),
            cancellationToken).ConfigureAwait(false);
        return reply.Success;
    }

    public async Task<bool> SpawnWindowAsync(string url,
                                              CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync<SandboxProtocol.AckPayload>(
            "window.spawn",
            new SandboxProtocol.SpawnPayload(url),
            cancellationToken).ConfigureAwait(false);
        return reply.Success;
    }

    public async Task<bool> RestartWindowAsync(string url,
                                                CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync<SandboxProtocol.AckPayload>(
            "window.restart",
            new SandboxProtocol.RestartPayload(url),
            cancellationToken).ConfigureAwait(false);
        return reply.Success;
    }

    public async Task NotifyWindowClosedAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            await RequestAsync<SandboxProtocol.AckPayload>(
                "window.closed", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<T> RequestAsync<T>(string op, object? payload,
                                          CancellationToken cancellationToken)
        where T : class
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SandboxBrokerClient));

        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<SandboxProtocol.Envelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Sandbox IPC request ID collision.");

        try
        {
            await SandboxProtocol.WriteAsync(
                _writer, op, id, payload, _writeLock, cancellationToken).ConfigureAwait(false);

            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

            SandboxProtocol.Envelope envelope = await completion.Task.ConfigureAwait(false);
            if (!string.Equals(envelope.Op, "response", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected sandbox IPC reply: {envelope.Op}");

            T? value = SandboxProtocol.GetPayload<T>(envelope);
            if (value == null)
                throw new InvalidOperationException("Malformed sandbox IPC response.");
            return value;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                SandboxProtocol.Envelope? envelope = await _envelopeReader
                    .ReadAsync(_lifetime.Token)
                    .ConfigureAwait(false);
                if (envelope == null)
                    break; // broker closed the pipe

                if (envelope.Op == "event")
                    continue;

                if (!string.IsNullOrEmpty(envelope.Id) &&
                    _pending.TryGetValue(envelope.Id, out var completion))
                {
                    completion.TrySetResult(envelope);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var pair in _pending)
                pair.Value.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        foreach (var pair in _pending)
            pair.Value.TrySetException(new ObjectDisposedException(nameof(SandboxBrokerClient)));
        _pending.Clear();

        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
        try { _lifetime.Dispose(); } catch { }
    }
}