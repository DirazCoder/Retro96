using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retro96.Plugins;

/// <summary>
/// Length-prefixed sandbox IPC. JSON remains the control plane; binary frames
/// carry pixel buffers and streamed content without base64 expansion or whole-
/// payload buffering. The framing is deliberately self-describing so both
/// endpoints can reject malformed/oversized messages before allocation.
/// </summary>
internal static class PluginSandboxProtocol
{
    public const int MaxJsonBytes = 4 * 1024 * 1024;
    public const int MaxBinaryFrameBytes = 64 * 1024 * 1024;
    public const int MaxStreamChunkBytes = 1024 * 1024;
    public const int BinaryHeaderBytes = 1 + 4 + 4 + 4;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public enum FrameKind : byte
    {
        Json = 1,
        Binary = 2
    }

    public abstract record Message;
    public sealed record JsonMessage(Envelope Envelope) : Message;
    public sealed record BinaryMessage(BinaryEnvelope Envelope) : Message;

    public sealed record Envelope(string Op, string Id, JsonElement? Payload = null);

    /// <summary>
    /// Metadata for a binary frame. Data is the raw trailing byte payload and
    /// is never represented as base64 in the control message.
    /// </summary>
    public sealed record BinaryEnvelope(
        string Op,
        string Id,
        JsonElement? Payload,
        byte[] Data);

    public sealed record HelloPayload(string PluginId, int ProcessId);
    public sealed record HelloReply(bool Accepted, string ManifestJson, ulong GrantedPermissions, string Error = "");
    public sealed record ReadyPayload(bool Ready, string Error = "");
    public sealed record ErrorPayload(string Error);
    public sealed record BrowserState(string? Url, string Title, float Zoom, int Width, int Height);
    public sealed record NavigatePayload(string Url);
    public sealed record ScrollPayload(int X, int Y);
    public sealed record ZoomPayload(float Value);
    public sealed record CookieGetPayload(string Name, string? Value = null);
    public sealed record CookieSetPayload(string Name, string Value, string? Path = null, string? Domain = null, DateTimeOffset? Expires = null, bool Secure = false);
    public sealed record FindPayload(string Text, bool CaseSensitive, bool WrapAround);
    public sealed record UiMenuAddPayload(string Text);
    public sealed record UiMenuAddReply(string Token);
    public sealed record UiMenuRemovePayload(string Token);
    public sealed record UiToolbarAddPayload(string Label, string Tooltip);
    public sealed record UiToolbarAddReply(string Token);
    public sealed record UiInvokePayload(string Token);
    public sealed record UiContextAddPayload(string Label);
    public sealed record UiContextAddReply(string Token);
    public sealed record UiContextQueryPayload(string Token, ContextMenuContext Context);
    public sealed record UiContextResult(bool Visible);
    public sealed record UiContextInvokePayload(string Token, ContextMenuContext Context);
    public sealed record UiStatusPayload(string Text);
    public sealed record UiProgressPayload(double? Fraction);
    public sealed record UiMessagePayload(string Title, string Message);
    public sealed record UiInputPayload(string Title, string Prompt, string? DefaultValue);
    public sealed record UiInputReply(string? Value);
    public sealed record PanelCreatePayload(string Title);
    public sealed record PanelCreateReply(string Token);
    public sealed record PanelWidgetPayload(string PanelToken, string WidgetToken, string Kind, string? Text = null, string? Placeholder = null, bool? Checked = null, string[]? Items = null);
    public sealed record PanelWidgetReply(string WidgetToken);
    public sealed record PanelWidgetSetPayload(string WidgetToken, string? Text = null, bool? Enabled = null);
    public sealed record PanelTokenPayload(string Token);
    public sealed record StorageKeyPayload(string Key, string? Value = null);
    public sealed record StorageSnapshotPayload(Dictionary<string, string> Values);
    public sealed record StorageKeysPayload(string[] Keys);
    public sealed record StorageObjectPayload(string Key, string? Json = null);
    public sealed record StorageUsedPayload(long Bytes);
    public sealed record FilePayload(string RelativePath, string? Text = null, string? BytesBase64 = null);
    public sealed record FileListPayload(string? Subdirectory, string[] Files);
    public sealed record NetworkRequestPayload(string Method, string Url, Dictionary<string, string>? Headers = null, string? BodyBase64 = null, string? ContentType = null);
    public sealed record NetworkReply(bool Success, string? Text = null, string? BytesBase64 = null, string Error = "");
    public sealed record ClipboardPayload(string? Text = null, string? PngBase64 = null);
    public sealed record LogPayload(string Level, string Message, string? Exception = null);
    public sealed record TimerCreatePayload(int IntervalMs);
    public sealed record TimerCreateReply(string Token);
    public sealed record TimerTokenPayload(string Token);
    public sealed record DialogOpenPayload(string Title, string Filter);
    public sealed record DialogOpenReply(string? RelativePath);
    public sealed record DialogSavePayload(string RelativePath, string SuggestedFilename, string Filter);
    public sealed record DialogSaveReply(bool Success);
    public sealed record AudioPlayPayload(string RelativePath, bool Loop = false);
    public sealed record AudioStatePayload(bool IsPlaying, float Volume, bool Loop, string? FileName);
    public sealed record NotificationPayload(string Title, string Body, string? Token = null);
    public sealed record EventNavigatedPayload(string Url);
    public sealed record EventPageLoadedPayload(string Url, string Title);
    public sealed record EventFocusPayload(bool HasFocus);
    public sealed record EventTimerPayload(string Token);
    public sealed record EventClipboardPayload();
    public sealed record EventPlaybackPayload();
    public sealed record EventPermissionsPayload(ulong GrantedPermissions);
    public sealed record ScreenshotReply(string PngBase64);
    public sealed record PanelInvokePayload(string Token, string? Text = null, bool? Checked = null, int? Index = null);

    // ── Embedded content / binary transport ────────────────────────────────

    public sealed record EmbedRegisterPayload(string[] MimeTypes);
    public sealed record EmbedRegisterReply(string Token);
    public sealed record EmbedUnregisterPayload(string Token);
    public sealed record EmbedCreatePayload(
        string RegistrationToken,
        string InstanceToken,
        string MimeType,
        string SourceUrl,
        string? CurrentUrl,
        string UserAgent,
        Dictionary<string, string> Parameters,
        int Width,
        int Height,
        bool StreamCanSeek,
        long? StreamLength,
        string StreamToken);
    public sealed record EmbedCreateReply(string InstanceToken, string ScriptName, string[] ScriptMethods);
    public sealed record EmbedDisposePayload(string InstanceToken, string StreamToken);
    public sealed record EmbedRenderPayload(string InstanceToken, int Width, int Height, int Stride, int DpiX, int DpiY, bool IsPrint);
    public sealed record EmbedFramePayload(string InstanceToken, int Width, int Height, int Stride, int DpiX, int DpiY, bool IsPrint);
    public sealed record EmbedInputPayload(string InstanceToken, EmbeddedInputEvent Event);
    public sealed record EmbedStreamCreditPayload(string StreamToken, int MaxBytes);
    public sealed record EmbedStreamSeekPayload(string StreamToken, long Offset, SeekOrigin Origin);
    public sealed record EmbedStreamSeekReply(string StreamToken, long Position, long? Length);
    public sealed record EmbedStreamChunkPayload(string StreamToken, long Offset, bool EndOfStream, string? Error = null);
    public sealed record EmbedStreamClosePayload(string StreamToken);
    public sealed record EmbedScriptCallPayload(string InstanceToken, string Name, JsValueWire[] Args);
    public sealed record EmbedScriptCallReply(JsValueWire Value);
    public sealed record EmbedScriptPageCallPayload(string InstanceToken, string Name, JsValueWire[] Args);
    public sealed record EmbedStatusPayload(string InstanceToken, string Text);
    public sealed record EmbedNavigatePayload(string InstanceToken, string Url);
    public sealed record NetworkStreamOpenPayload(NetworkRequestPayload Request);
    public sealed record NetworkStreamOpenReply(bool Success, int StatusCode, Dictionary<string, string> Headers,
                                                  string? ContentType, string? Charset, string EffectiveUrl,
                                                  string StreamToken, bool CanSeek, long? Length, string Error = "");
    public sealed record JsValueWire(string Kind, string? StringValue = null, double? NumberValue = null,
                                     bool? BooleanValue = null, JsValueWire[]? ArrayValue = null,
                                     Dictionary<string, JsValueWire>? ObjectValue = null);

    public static async Task WriteAsync(Stream stream, string op, string id, object? payload,
        SemaphoreSlim writeLock, CancellationToken cancellationToken = default)
    {
        var envelope = new Envelope(op, id,
            payload == null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions));
        await WriteJsonAsync(stream, envelope, writeLock, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteJsonAsync(Stream stream, Envelope envelope,
        SemaphoreSlim writeLock, CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        if (jsonBytes.Length > MaxJsonBytes)
            throw new InvalidOperationException("Plugin sandbox IPC JSON message is too large.");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int frameBytes = checked(1 + jsonBytes.Length);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, frameBytes);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new[] { (byte)FrameKind.Json }, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    public static async Task WriteBinaryAsync(Stream stream, string op, string id, object? payload,
        ReadOnlyMemory<byte> data, SemaphoreSlim writeLock, CancellationToken cancellationToken = default)
    {
        if (data.Length > MaxBinaryFrameBytes - BinaryHeaderBytes - 16)
            throw new InvalidOperationException("Plugin sandbox IPC binary message is too large.");
        byte[] opBytes = Encoding.UTF8.GetBytes(op ?? "");
        byte[] idBytes = Encoding.UTF8.GetBytes(id ?? "");
        byte[] payloadBytes = payload == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (payloadBytes.Length > MaxJsonBytes)
            throw new InvalidOperationException("Plugin sandbox IPC JSON payload is too large.");
        int bodyBytes = checked(1 + 4 + 4 + 4 + opBytes.Length + idBytes.Length + payloadBytes.Length + data.Length);
        if (bodyBytes > MaxBinaryFrameBytes)
            throw new InvalidOperationException("Plugin sandbox IPC binary message is too large.");
        byte[] header = new byte[4];
        byte[] fixedHeader = new byte[BinaryHeaderBytes];
        fixedHeader[0] = (byte)FrameKind.Binary;
        BinaryPrimitives.WriteInt32LittleEndian(fixedHeader.AsSpan(1, 4), opBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fixedHeader.AsSpan(5, 4), idBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fixedHeader.AsSpan(9, 4), payloadBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header, bodyBytes);

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(fixedHeader, cancellationToken).ConfigureAwait(false);
            if (opBytes.Length > 0) await stream.WriteAsync(opBytes, cancellationToken).ConfigureAwait(false);
            if (idBytes.Length > 0) await stream.WriteAsync(idBytes, cancellationToken).ConfigureAwait(false);
            if (payloadBytes.Length > 0) await stream.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
            if (!data.IsEmpty) await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] lengthBytes = new byte[4];
        if (!await TryReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false)) return null;
        int frameBytes = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (frameBytes < 1 || frameBytes > MaxBinaryFrameBytes)
            throw new InvalidOperationException("Malformed or oversized plugin sandbox IPC frame.");

        byte[] frame = new byte[frameBytes];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        var kind = (FrameKind)frame[0];
        if (kind == FrameKind.Json)
        {
            int jsonLength = frameBytes - 1;
            if (jsonLength > MaxJsonBytes) throw new InvalidOperationException("Plugin sandbox IPC JSON message is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(frame.AsSpan(1, jsonLength), JsonOptions);
            ValidateEnvelope(envelope);
            return new JsonMessage(envelope!);
        }

        if (kind != FrameKind.Binary || frameBytes < BinaryHeaderBytes)
            throw new InvalidOperationException("Malformed plugin sandbox IPC frame kind.");

        int opLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1, 4));
        int idLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(5, 4));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(9, 4));
        if (opLength < 1 || idLength < 1 || payloadLength < 0 || payloadLength > MaxJsonBytes)
            throw new InvalidOperationException("Malformed or oversized plugin sandbox binary message header.");
        int dataLength = checked(frameBytes - BinaryHeaderBytes - opLength - idLength - payloadLength);
        if (dataLength < 0)
            throw new InvalidOperationException("Malformed plugin sandbox binary message lengths.");
        int offset = BinaryHeaderBytes;
        string op = Encoding.UTF8.GetString(frame, offset, opLength); offset += opLength;
        string id = Encoding.UTF8.GetString(frame, offset, idLength); offset += idLength;
        JsonElement? payload = payloadLength == 0
            ? null
            : JsonSerializer.Deserialize<JsonElement>(frame.AsSpan(offset, payloadLength), JsonOptions);
        offset += payloadLength;
        byte[] data = dataLength == 0 ? Array.Empty<byte>() : frame.AsSpan(offset, dataLength).ToArray();
        return new BinaryMessage(new BinaryEnvelope(op, id, payload, data));
    }

    public static T? GetPayload<T>(Envelope envelope) =>
        envelope.Payload.HasValue ? envelope.Payload.Value.Deserialize<T>(JsonOptions) : default;

    public static T? GetPayload<T>(BinaryEnvelope envelope) =>
        envelope.Payload.HasValue ? envelope.Payload.Value.Deserialize<T>(JsonOptions) : default;

    private static void ValidateEnvelope(Envelope? envelope)
    {
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.Op) || string.IsNullOrWhiteSpace(envelope.Id))
            throw new InvalidOperationException("Malformed plugin sandbox IPC message.");
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException("Unexpected end of plugin sandbox IPC stream.");
            offset += read;
        }
        return true;
    }
}
