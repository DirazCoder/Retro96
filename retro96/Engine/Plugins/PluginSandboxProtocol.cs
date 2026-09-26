using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retro96.Plugins;

internal static class PluginSandboxProtocol
{
    public const int MaxLineCharacters = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public sealed record Envelope(string Op, string Id, JsonElement? Payload = null);
    public sealed record HelloPayload(string PluginId, int ProcessId);
    public sealed record HelloReply(bool Accepted, string ManifestJson, long GrantedPermissions, string Error = "");
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
    public sealed record ScreenshotReply(string PngBase64);
    public sealed record PanelInvokePayload(string Token, string? Text = null, bool? Checked = null, int? Index = null);

    public static async Task WriteAsync(StreamWriter writer, string op, string id, object? payload,
        SemaphoreSlim writeLock, CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(new Envelope(op, id, payload == null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions)), JsonOptions);
        if (json.Length > MaxLineCharacters) throw new InvalidOperationException("Plugin sandbox IPC message is too large.");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await writer.WriteLineAsync(json).ConfigureAwait(false); await writer.FlushAsync().ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    public static async Task<Envelope?> ReadAsync(StreamReader reader, CancellationToken cancellationToken = default)
    {
        string? line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (line == null) return null;
        if (line.Length > MaxLineCharacters) throw new InvalidOperationException("Plugin sandbox IPC message is too large.");
        var envelope = JsonSerializer.Deserialize<Envelope>(line, JsonOptions);
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.Op) || string.IsNullOrWhiteSpace(envelope.Id)) throw new InvalidOperationException("Malformed plugin sandbox IPC message.");
        return envelope;
    }

    public static T? GetPayload<T>(Envelope envelope) => envelope.Payload.HasValue ? envelope.Payload.Value.Deserialize<T>(JsonOptions) : default;
}
