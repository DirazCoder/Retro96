using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retro96.Plugins;

public static class Retro96PluginApi
{
    public const int ApiVersion = 1;
}

[Flags]
public enum PluginPermission
{
    None = 0,
    BrowserRead = 1 << 0,
    BrowserNavigation = 1 << 1,
    BrowserWindows = 1 << 2,
    BrowserEvents = 1 << 3,
    UserInterface = 1 << 4,
    Storage = 1 << 5,
    Network = 1 << 6,
    FileSystem = 1 << 7,
    Clipboard = 1 << 8,
    BrowserZoom = 1 << 9,
    BrowserCookies = 1 << 10,
    BrowserFind = 1 << 11,
    BrowserScreenshot = 1 << 12,
    UiPanel = 1 << 13,
    AudioPlayback = 1 << 14,
    Notifications = 1 << 15,
    Dialogs = 1 << 16,
    EmbedRenderer = 1 << 17,
    EmbedNetwork = 1 << 18,
    EmbedNavigate = 1 << 19,
    EmbedStatus = 1 << 20,
    EmbedPrint = 1 << 21,
    EmbedScript = 1 << 22
}

public static class PluginPermissionNames
{
    private static readonly IReadOnlyDictionary<PluginPermission, string> Names =
        new Dictionary<PluginPermission, string>
        {
            [PluginPermission.BrowserRead] = "browser.read",
            [PluginPermission.BrowserNavigation] = "browser.navigate",
            [PluginPermission.BrowserWindows] = "browser.windows",
            [PluginPermission.BrowserEvents] = "browser.events",
            [PluginPermission.UserInterface] = "ui",
            [PluginPermission.Storage] = "storage",
            [PluginPermission.Network] = "network",
            [PluginPermission.FileSystem] = "filesystem",
            [PluginPermission.Clipboard] = "clipboard",
            [PluginPermission.BrowserZoom] = "browser.zoom",
            [PluginPermission.BrowserCookies] = "browser.cookies",
            [PluginPermission.BrowserFind] = "browser.find",
            [PluginPermission.BrowserScreenshot] = "browser.screenshot",
            [PluginPermission.UiPanel] = "ui.panel",
            [PluginPermission.AudioPlayback] = "audio.playback",
            [PluginPermission.Notifications] = "notifications",
            [PluginPermission.Dialogs] = "dialogs",
            [PluginPermission.EmbedRenderer] = "embed.renderer",
            [PluginPermission.EmbedNetwork] = "embed.network",
            [PluginPermission.EmbedNavigate] = "embed.navigate",
            [PluginPermission.EmbedStatus] = "embed.status",
            [PluginPermission.EmbedPrint] = "embed.print",
            [PluginPermission.EmbedScript] = "embed.script"
        };

    public static IEnumerable<string> ToNames(PluginPermission permissions) =>
        Names.Where(p => permissions.HasFlag(p.Key)).Select(p => p.Value);

    public static PluginPermission Parse(IEnumerable<string>? names)
    {
        PluginPermission value = PluginPermission.None;
        foreach (string raw in names ?? Array.Empty<string>())
        {
            string name = (raw ?? string.Empty).Trim();
            var pair = Names.FirstOrDefault(p => p.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!pair.Equals(default(KeyValuePair<PluginPermission, string>))) value |= pair.Key;
        }
        return value;
    }
}

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public int ApiVersion { get; set; } = Retro96PluginApi.ApiVersion;
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Assembly { get; set; } = "plugin.dll";
    public string EntryPoint { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
    [JsonPropertyName("embed_types")]
    public List<string> EmbedTypes { get; set; } = new();

    [JsonPropertyName("script_name")]
    public string ScriptName { get; set; } = "";
    public string Website { get; set; } = "";
    public PluginPermission RequestedPermissions => PluginPermissionNames.Parse(Permissions);
}

public interface IRetro96Plugin : IDisposable { void Initialize(IRetro96PluginHost host); }

public interface IRetro96PluginHost
{
    PluginManifest Manifest { get; }
    PluginPermission GrantedPermissions { get; }
    IPluginBrowser Browser { get; }
    IPluginUi Ui { get; }
    IPluginStorage Storage { get; }
    IPluginNetwork Network { get; }
    IPluginFileSystem FileSystem { get; }
    IPluginClipboard Clipboard { get; }
    IPluginEvents Events { get; }
    IPluginAudio Audio { get; }
    IPluginNotifications Notifications { get; }
    IPluginDialogs Dialogs { get; }
    IPluginEmbeddedContentService Embeds { get; }
    IPluginLogger Log { get; }
    bool HasPermission(PluginPermission permission);

    IBrowserService BrowserService => Browser;
    IUiService UiService => Ui;
    IStorageService StorageService => Storage;
    INetworkService NetworkService => Network;
    IFileSystemService FileSystemService => FileSystem;
    IClipboardService ClipboardService => Clipboard;
    IEventsService EventsService => Events;
    IAudioService AudioService => Audio;
    INotificationService NotificationService => Notifications;
    IDialogsService DialogsService => Dialogs;
    IEmbeddedContentService EmbeddedContentService => Embeds;
}

public interface IBrowserService
{
    string? CurrentUrl { get; }
    string CurrentTitle { get; }
    void Navigate(string url);
    void Reload();
    void Back();
    void Forward();
    void OpenWindow(string url);
    void ScrollTo(int x, int y);
    float Zoom { get; set; }
    (int Width, int Height) ViewportSize { get; }
    string? GetCookie(string name);
    void SetCookie(string name, string value, CookieOptions options);
    void Find(string text, bool caseSensitive, bool wrapAround);
    void FindNext();
    void FindClear();
    byte[] CaptureViewport();
}

public interface IPluginBrowser : IBrowserService { }

public sealed record CookieOptions(string? Path = null, string? Domain = null, DateTimeOffset? Expires = null, bool Secure = false);

public interface IUiService
{
    IDisposable AddFileMenuItem(string text, Action callback);
    IDisposable AddToolbarButton(string label, string tooltip, Action onClick);
    IDisposable AddContextMenuItem(string label, Func<ContextMenuContext, bool> shouldShow, Action<ContextMenuContext> onClick);
    void SetStatus(string text);
    void SetProgress(double? fraction);
    void ShowMessage(string title, string message);
    Task<string?> ShowInputDialog(string title, string prompt, string? defaultValue = null);
    IPluginPanel CreatePanel(string title);
}

public interface IPluginUi : IUiService { }

public sealed record ContextMenuContext(string? TargetUrl, string? TargetText, bool IsLink, bool IsImage);

public interface IPluginPanel : IDisposable
{
    IPluginWidget AddLabel(string text);
    IPluginWidget AddButton(string label, Action onClick);
    IPluginWidget AddTextBox(string placeholder, Action<string> onChange);
    IPluginWidget AddCheckBox(string label, bool initial, Action<bool> onChange);
    IPluginWidget AddListBox(IEnumerable<string> items, Action<int> onSelect);
    void Clear();
    void Show();
    void Hide();
}

public interface IPluginWidget : IDisposable
{
    void SetText(string text);
    void SetEnabled(bool enabled);
}

public sealed record HttpPluginRequest(string Method, string Url, IReadOnlyDictionary<string, string>? Headers = null, byte[]? Body = null, string? ContentType = null);

public interface IStorageService
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
    IReadOnlyDictionary<string, string> Snapshot();
    string[] ListKeys(string? prefix = null);
    T? GetObject<T>(string key);
    void SetObject<T>(string key, T value);
    long GetUsedBytes();
}

public interface INetworkService
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default);
    Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken = default);
    Task<string> PostStringAsync(string url, string body, string contentType, CancellationToken cancellationToken = default);
    Task<byte[]> PostBytesAsync(string url, byte[] body, string contentType, CancellationToken cancellationToken = default);
    Task<string> SendAsync(HttpPluginRequest request, CancellationToken cancellationToken = default);
    Task<IPluginNetworkResponse> GetStreamAsync(string url, CancellationToken cancellationToken = default);
    Task<IPluginNetworkResponse> PostStreamAsync(string url, byte[] body, string contentType, CancellationToken cancellationToken = default);
    Task<IPluginNetworkResponse> OpenStreamAsync(HttpPluginRequest request, CancellationToken cancellationToken = default);
}

public interface IFileSystemService
{
    string DataDirectory { get; }
    byte[] ReadAllBytes(string relativePath);
    string ReadAllText(string relativePath);
    void WriteAllBytes(string relativePath, byte[] content);
    void WriteAllText(string relativePath, string content);
    bool Exists(string relativePath);
    string[] ListFiles(string? subdirectory = null);
    void Delete(string relativePath);
    void CreateDirectory(string relativePath);
}

public interface IClipboardService
{
    string? GetText();
    void SetText(string text);
    event EventHandler? ClipboardChanged;
    byte[]? GetImage();
    void SetImage(byte[] pngBytes);
}

public interface IEventsService
{
    event EventHandler<PluginNavigationEventArgs>? Navigated;
    event EventHandler<PluginPageEventArgs>? PageLoaded;
    event EventHandler? HostShuttingDown;
    event EventHandler<FocusEventArgs>? WindowFocusChanged;
    IDisposable CreateTimer(TimeSpan interval, Action callback);
}

public sealed class PluginNavigationEventArgs : EventArgs { public PluginNavigationEventArgs(string url) => Url = url; public string Url { get; } }
public sealed class PluginPageEventArgs : EventArgs { public PluginPageEventArgs(string url, string title) { Url = url; Title = title; } public string Url { get; } public string Title { get; } }
public sealed class FocusEventArgs : EventArgs { public FocusEventArgs(bool hasFocus) => HasFocus = hasFocus; public bool HasFocus { get; } }

public interface IAudioService
{
    Task PlayFileAsync(string path);
    void Stop();
    bool IsPlaying { get; }
    float Volume { get; set; }
    bool Loop { get; set; }
    event EventHandler? PlaybackComplete;
}

public interface INotificationService
{
    void Show(string title, string body);
    void Show(string title, string body, Action onClick);
}

public interface IDialogsService
{
    Task<string?> OpenFilePickerAsync(string title, string filter);
    Task<bool> SaveFilePickerAsync(string sandboxRelativePath, string suggestedFilename, string filter);
}

public interface IPluginStorage : IStorageService { }
public interface IPluginNetwork : INetworkService { }
public interface IPluginFileSystem : IFileSystemService { }
public interface IPluginClipboard : IClipboardService { }
public interface IPluginEvents : IEventsService { }
public interface IPluginAudio : IAudioService { }
public interface IPluginNotifications : INotificationService { }
public interface IPluginDialogs : IDialogsService { }
public interface IPluginEmbeddedContentService : IEmbeddedContentService { }
public interface IPluginNetworkResponse : IDisposable
{
    int StatusCode { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    string? ContentType { get; }
    string? Charset { get; }
    string EffectiveUrl { get; }
    IPluginByteStream Body { get; }
}

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}


// ─────────────────────────────────────────────────────────────────────────────
// Secure embedded-content API
// ─────────────────────────────────────────────────────────────────────────────

public enum JsValueKind
{
    Null,
    String,
    Number,
    Boolean,
    Array,
    Object
}

/// <summary>
/// Strongly typed values permitted across the page/embedded-plugin scripting
/// boundary. Arrays and objects are deliberately flat: their direct children
/// may only be null, string, number, or bool. Live engine objects, functions,
/// host handles, and arbitrary serialization are not representable.
/// </summary>
public sealed record JsValue
{
    public JsValueKind Kind { get; }
    public string? StringValue { get; }
    public double NumberValue { get; }
    public bool BooleanValue { get; }
    public IReadOnlyList<JsValue>? ArrayValue { get; }
    public IReadOnlyDictionary<string, JsValue>? ObjectValue { get; }

    private JsValue(
        JsValueKind kind,
        string? stringValue = null,
        double numberValue = 0,
        bool booleanValue = false,
        IReadOnlyList<JsValue>? arrayValue = null,
        IReadOnlyDictionary<string, JsValue>? objectValue = null)
    {
        Kind = kind;
        StringValue = stringValue;
        NumberValue = numberValue;
        BooleanValue = booleanValue;
        ArrayValue = arrayValue;
        ObjectValue = objectValue;
    }

    public static JsValue Null { get; } = new(JsValueKind.Null);

    public static JsValue From(string value) =>
        new(JsValueKind.String, stringValue: value ?? string.Empty);

    public static JsValue From(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Embedded JavaScript numbers must be finite.");
        return new(JsValueKind.Number, numberValue: value);
    }

    public static JsValue From(bool value) =>
        new(JsValueKind.Boolean, booleanValue: value);

    public static JsValue FromArray(IReadOnlyList<JsValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToArray();
        ValidateFlat(copy);
        return new(JsValueKind.Array, arrayValue: new ReadOnlyCollection<JsValue>(copy));
    }

    public static JsValue FromObject(IReadOnlyDictionary<string, JsValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, JsValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (string.IsNullOrEmpty(pair.Key))
                throw new ArgumentException("Embedded JavaScript object keys must be non-empty.", nameof(values));
            copy[pair.Key] = pair.Value ?? throw new ArgumentException("Embedded JavaScript object values cannot be null references.", nameof(values));
        }
        ValidateFlat(copy.Values);
        return new(JsValueKind.Object,
            objectValue: new ReadOnlyDictionary<string, JsValue>(copy));
    }

    public static implicit operator JsValue(string value) => From(value);
    public static implicit operator JsValue(double value) => From(value);
    public static implicit operator JsValue(bool value) => From(value);

    private static void ValidateFlat(IEnumerable<JsValue> values)
    {
        foreach (var value in values)
        {
            if (value == null) throw new ArgumentException("Embedded JavaScript values cannot be null references.");
            if (value.Kind is JsValueKind.Array or JsValueKind.Object)
                throw new ArgumentException("Embedded JavaScript arrays and objects must be flat.");
        }
    }
}

public sealed record EmbeddedContentContext(
    string MimeType,
    string SourceUrl,
    string? CurrentUrl,
    string UserAgent,
    IReadOnlyDictionary<string, string> Parameters,
    int Width,
    int Height);

public sealed record EmbeddedRenderRequest(
    int Width,
    int Height,
    int Stride,
    int DpiX,
    int DpiY,
    bool IsPrint = false);

/// <summary>Host-composited BGRA-8888 premultiplied frame.</summary>
public sealed record EmbeddedFrameBuffer(
    int Width,
    int Height,
    int Stride,
    byte[] Pixels)
{
    public EmbeddedFrameBuffer Validate()
    {
        if (Width <= 0 || Height <= 0 || Stride < Width * 4)
            throw new ArgumentOutOfRangeException(nameof(Stride), "Invalid embedded frame geometry.");
        if (Pixels == null || Pixels.Length != checked(Stride * Height))
            throw new ArgumentException("Embedded frame pixel buffer length does not match its geometry.", nameof(Pixels));
        return this;
    }
}

public enum EmbeddedInputEventKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    KeyDown,
    KeyUp,
    TextInput,
    FocusGained,
    FocusLost
}

public sealed record EmbeddedInputEvent(
    EmbeddedInputEventKind Kind,
    int X = 0,
    int Y = 0,
    int Button = 0,
    int WheelDelta = 0,
    int KeyCode = 0,
    string? Text = null,
    bool Shift = false,
    bool Control = false,
    bool Alt = false,
    bool Meta = false);

public sealed record EmbeddedStreamChunkEventArgs(
    byte[] Data,
    long Offset,
    bool EndOfStream,
    string? Error = null);

public sealed record EmbeddedSeekResult(long Position, long? Length);

public interface IPluginByteStream : IDisposable
{
    bool CanSeek { get; }
    long? Length { get; }
    long Position { get; }
    bool EndOfStream { get; }
    event EventHandler<EmbeddedStreamChunkEventArgs>? ChunkReceived;
    Task RequestMoreAsync(int maxBytes, CancellationToken cancellationToken = default);
}

public interface IPluginSeekableByteStream : IPluginByteStream
{
    Task<EmbeddedSeekResult> SeekAsync(long offset, SeekOrigin origin, CancellationToken cancellationToken = default);
}

public interface IEmbeddedContentService
{
    IDisposable Register(EmbeddedContentRegistration registration);
}

public sealed record EmbeddedContentRegistration(
    IReadOnlyList<string> MimeTypes,
    Func<EmbeddedContentContext, IPluginByteStream, IEmbeddedContentHost, IEmbeddedScriptBridge, IEmbeddedContentInstance> Factory);

public interface IEmbeddedContentHost
{
    string? CurrentUrl { get; }
    string UserAgent { get; }
    Task SetStatusAsync(string text, CancellationToken cancellationToken = default);
    Task RequestNavigationAsync(string url, CancellationToken cancellationToken = default);
    Task<IPluginNetworkResponse> OpenStreamAsync(HttpPluginRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// JS bridge for a single embed instance. The plugin publishes named async
/// handlers; CallPageFunction always crosses the sandbox broker.
/// </summary>
public interface IEmbeddedScriptBridge
{
    IDictionary<string, Func<IReadOnlyList<JsValue>, Task<JsValue>>> Methods { get; }
    Task<JsValue> CallPageFunction(string name, IReadOnlyList<JsValue> args);
}

public interface IEmbeddedContentInstance : IDisposable
{
    Task<EmbeddedFrameBuffer> RenderAsync(EmbeddedRenderRequest request, CancellationToken cancellationToken = default);
    Task HandleInputAsync(EmbeddedInputEvent inputEvent, CancellationToken cancellationToken = default);
}
