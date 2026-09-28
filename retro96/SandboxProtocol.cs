using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Retro96;

internal static class SandboxProtocol
{
    // Wire limits. MaxLineCharacters must comfortably exceed the largest legal
    // message: a MaxFileBytes file reply serializes to ~1.34x its size in
    // base64 plus envelope overhead, so the ceiling is 64 Mi characters.
    public const int MaxLineCharacters = 64 * 1024 * 1024;
    public const int MaxFetchBodyBytes = 8 * 1024 * 1024;
    public const int MaxFileBytes = 32 * 1024 * 1024;

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
        StreamWriter writer,
        string op,
        string id,
        object? payload,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(op))
            throw new ArgumentException("Sandbox IPC operation name is required.", nameof(op));
        if (id is null)
            throw new ArgumentNullException(nameof(id));

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

    public static T? GetPayload<T>(Envelope envelope) =>
        envelope.Payload.HasValue
            ? envelope.Payload.Value.Deserialize<T>(JsonOptions)
            : default;

    /// <summary>
    /// Bounded, line-oriented envelope reader for the sandbox IPC pipe.
    ///
    /// This replaces StreamReader.ReadLineAsync + a length check, which buffers an
    /// entire line in memory before the size check can run — a misbehaving peer
    /// could otherwise exhaust the reader's process with a single oversized line.
    /// This reader rejects lines above SandboxProtocol.MaxLineCharacters while
    /// they are still being received, so memory stays bounded by the protocol
    /// limit.
    ///
    /// One instance must be used exclusively for one stream direction and one
    /// reader task at a time. After a read is cancelled, the underlying reader is
    /// no longer usable (the abandoned native read may still complete); callers
    /// only cancel during teardown.
    /// </summary>
    internal sealed class EnvelopeReader
    {
        private const int ChunkCharacters = 8192;

        private readonly TextReader _reader;
        private readonly char[] _chunk = new char[ChunkCharacters];
        private readonly StringBuilder _line = new(512);
        private int _chunkLength;
        private int _chunkPosition;

        public EnvelopeReader(TextReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>Reads and parses one envelope; returns null when the peer closes the pipe.</summary>
        public async Task<SandboxProtocol.Envelope?> ReadAsync(CancellationToken cancellationToken)
        {
            string? line;
            do
            {
                line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    return null; // peer closed the pipe
            }
            while (line.Length == 0); // tolerate blank lines

            SandboxProtocol.Envelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SandboxProtocol.Envelope>(line, SandboxProtocol.JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Malformed sandbox IPC message.", ex);
            }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Op))
                throw new InvalidOperationException("Malformed sandbox IPC message.");

            return envelope;
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _line.Clear();

            while (true)
            {
                if (_chunkPosition >= _chunkLength)
                {
                    int read = await ReadChunkAsync(cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        return _line.Length == 0 ? null : TakeLine(); // EOF, possibly with a trailing partial line
                }

                int newline = Array.IndexOf(_chunk, '\n', _chunkPosition, _chunkLength - _chunkPosition);
                if (newline >= 0)
                {
                    AppendBounded(_chunkPosition, newline - _chunkPosition);
                    _chunkPosition = newline + 1;
                    return TakeLine();
                }

                AppendBounded(_chunkPosition, _chunkLength - _chunkPosition);
                _chunkPosition = _chunkLength;
            }
        }

        private string TakeLine()
        {
            string line = _line.ToString();
            return line.Length > 0 && line[line.Length - 1] == '\r'
                ? line[..^1] // normalize \r\n
                : line;
        }

        private void AppendBounded(int index, int count)
        {
            if (count == 0) return;
            if (_line.Length + count > SandboxProtocol.MaxLineCharacters)
                throw new InvalidOperationException("Sandbox IPC message is too large.");
            _line.Append(_chunk, index, count);
        }

        private async Task<int> ReadChunkAsync(CancellationToken cancellationToken)
        {
            _chunkPosition = 0;
            _chunkLength = 0;

            // TextReader.ReadAsync is not natively cancellable; WaitAsync abandons
            // the read on cancellation, which is acceptable only because
            // cancellation is used exclusively during teardown.
            Task<int> read = _reader.ReadAsync(_chunk, 0, _chunk.Length);
            int count = await read.WaitAsync(cancellationToken).ConfigureAwait(false);
            _chunkLength = count;
            return count;
        }
    }
}