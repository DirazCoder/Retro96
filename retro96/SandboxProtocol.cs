using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Retro96;

internal static class SandboxProtocol
{
    public const int MaxLineCharacters = 16 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public sealed record Envelope(
        string Op,
        string Id,
        JsonElement? Payload = null);

    public sealed record HelloPayload(
        string TrustMode,
        int ProcessId);

    public sealed record HelloReply(
        bool Accepted,
        string SettingsJson,
        string StartupUrl,
        string SessionId,
        string Error = "");

    public sealed record FetchPayload(
        string Method,
        string Url,
        Dictionary<string, string> Headers,
        string? BodyBase64,
        string ResourceType,
        int MaxBytes);

    public sealed record FetchReply(
        bool Success,
        int StatusCode,
        Dictionary<string, string> Headers,
        string ContentType,
        string Charset,
        string? BodyBase64,
        string EffectiveUrl,
        string? Error,
        string? CertError);

    public sealed record FileReadPayload(
        string Path,
        string ResourceType,
        int MaxBytes);

    public sealed record FileReply(
        bool Success,
        string? BodyBase64,
        string Error = "");

    public sealed record OpenFileReply(
        bool Success,
        string? Path,
        string? Url,
        string? BodyBase64,
        string Error = "");

    public sealed record SettingsPayload(string SettingsJson);
    public sealed record SpawnPayload(string Url);
    public sealed record RestartPayload(string Url);
    public sealed record ExternalPayload(string Url);
    public sealed record AckPayload(bool Success, string Error = "");

    public static async Task WriteAsync(
        System.IO.StreamWriter writer,
        string op,
        string id,
        object? payload,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(
            new Envelope(
                op,
                id,
                payload == null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions)),
            JsonOptions);

        if (json.Length > MaxLineCharacters)
            throw new InvalidOperationException("Sandbox IPC message is too large");

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public static async Task<Envelope?> ReadAsync(
        System.IO.StreamReader reader,
        CancellationToken cancellationToken = default)
    {
        Task<string?> readTask = reader.ReadLineAsync();
        string? line = await readTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (line == null)
            return null;
        if (line.Length > MaxLineCharacters)
            throw new InvalidOperationException("Sandbox IPC message is too large");

        var envelope = JsonSerializer.Deserialize<Envelope>(line, JsonOptions);
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.Op))
            throw new InvalidOperationException("Malformed sandbox IPC message");
        return envelope;
    }

    public static T? GetPayload<T>(Envelope envelope) =>
        envelope.Payload.HasValue
            ? envelope.Payload.Value.Deserialize<T>(JsonOptions)
            : default;
}
