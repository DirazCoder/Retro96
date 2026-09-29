using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retro96.Plugins;

public static class Retro96PluginApi
{
    public const int ApiVersion = 1;
}

[Flags]
public enum PluginPermission : ulong
{
    None = 0,
    BrowserRead = 1UL << 0,
    BrowserNavigation = 1UL << 1,
    BrowserWindows = 1UL << 2,
    BrowserEvents = 1UL << 3,
    UserInterface = 1UL << 4,
    Storage = 1UL << 5,
    Network = 1UL << 6,
    FileSystem = 1UL << 7,
    Clipboard = 1UL << 8,
    BrowserZoom = 1UL << 9,
    BrowserCookies = 1UL << 10,
    BrowserFind = 1UL << 11,
    BrowserScreenshot = 1UL << 12,
    UiPanel = 1UL << 13,
    AudioPlayback = 1UL << 14,
    Notifications = 1UL << 15,
    Dialogs = 1UL << 16,
    EmbedRenderer = 1UL << 17,
    EmbedNetwork = 1UL << 18,
    EmbedNavigate = 1UL << 19,
    EmbedStatus = 1UL << 20,
    EmbedPrint = 1UL << 21,
    EmbedScript = 1UL << 22,
    PageRead = 1UL << 23,
    NetworkRules = 1UL << 24,
    Protocol = 1UL << 25,
    ContentTransform = 1UL << 26,
    PageStyle = 1UL << 27,
    Tabs = 1UL << 28,
    History = 1UL << 29,
    Bookmarks = 1UL << 30,
    Downloads = 1UL << 31,
    Omnibox = 1UL << 32,
    Settings = 1UL << 33,
    UiExtras = 1UL << 34,
    EmbedAudio = 1UL << 35,
    EmbedExtras = 1UL << 36
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
            [PluginPermission.EmbedScript] = "embed.script",
            [PluginPermission.PageRead] = "page.read",
            [PluginPermission.NetworkRules] = "network.rules",
            [PluginPermission.Protocol] = "protocol",
            [PluginPermission.ContentTransform] = "content.transform",
            [PluginPermission.PageStyle] = "page.style",
            [PluginPermission.Tabs] = "tabs",
            [PluginPermission.History] = "history",
            [PluginPermission.Bookmarks] = "bookmarks",
            [PluginPermission.Downloads] = "downloads",
            [PluginPermission.Omnibox] = "omnibox",
            [PluginPermission.Settings] = "settings",
            [PluginPermission.UiExtras] = "ui.extras",
            [PluginPermission.EmbedAudio] = "embed.audio",
            [PluginPermission.EmbedExtras] = "embed.extras"
        };

    // Reverse lookup for parsing; names are unique by construction.
    private static readonly IReadOnlyDictionary<string, PluginPermission> Lookup =
        new Dictionary<string, PluginPermission>(
            Names.Select(p => new KeyValuePair<string, PluginPermission>(p.Value, p.Key)),
            StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> ToNames(PluginPermission permissions) =>
        Names.Where(p => permissions.HasFlag(p.Key)).Select(p => p.Value);

    public static PluginPermission Parse(IEnumerable<string>? names)
    {
        PluginPermission value = PluginPermission.None;
        foreach (string raw in names ?? Array.Empty<string>())
        {
            if (Lookup.TryGetValue((raw ?? string.Empty).Trim(), out PluginPermission permission))
                value |= permission;
        }
        return value;
    }
}

public sealed record PluginContentTransformScope
{
    [JsonPropertyName("url")]
    public string UrlPattern { get; init; } = "";

    [JsonPropertyName("mime")]
    public string MimePattern { get; init; } = "";

    public PluginContentTransformScope() { }

    public PluginContentTransformScope(string urlPattern, string mimePattern)
    {
        UrlPattern = urlPattern;
        MimePattern = mimePattern;
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
    [JsonPropertyName("optional_permissions")]
    public List<string> OptionalPermissions { get; set; } = new();
    [JsonPropertyName("embed_types")]
    public List<string> EmbedTypes { get; set; } = new();
    [JsonPropertyName("content_transform_scopes")]
    public List<PluginContentTransformScope> ContentTransformScopes { get; set; } = new();
    public List<PluginSettingDefinition> Settings { get; set; } = new();

    [JsonPropertyName("script_name")]
    public string ScriptName { get; set; } = "";
    public string Website { get; set; } = "";
    [JsonPropertyName("min_host_version")]
    public string MinHostVersion { get; set; } = "";
    public PluginPermission RequestedPermissions => PluginPermissionNames.Parse(Permissions);
    public PluginPermission OptionalPermissionSet => PluginPermissionNames.Parse(OptionalPermissions);
    public PluginPermission AvailablePermissions => RequestedPermissions | OptionalPermissionSet;
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
    IPluginHostInfo Info { get; }
    IPluginPageRead Page { get; }
    IPluginNetworkRules NetworkRules { get; }
    IPluginProtocols Protocols { get; }
    IPluginContentTransform ContentTransform { get; }
    IPluginPageStyle PageStyle { get; }
    IPluginTabs Tabs { get; }
    IPluginHistory History { get; }
    IPluginBookmarks Bookmarks { get; }
    IPluginDownloads Downloads { get; }
    IPluginOmnibox Omnibox { get; }
    IPluginUiExtras UiExtras { get; }
    bool HasPermission(PluginPermission permission);
    Task<bool> RequestPermissionAsync(string name, CancellationToken cancellationToken = default);

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

public interface IPluginHostInfo
{
    string HostVersion { get; }
    int ApiVersion { get; }
    bool IsSupported(string name);
    string Theme { get; }
    string Locale { get; }
    int Dpi { get; }
}


public sealed record PluginPageLink(string Url, string Text, string Title);

public interface IPluginPageRead
{
    Task<string> GetPageTextAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PluginPageLink>> GetLinksAsync(CancellationToken cancellationToken = default);
    Task<string?> GetSelectionAsync(CancellationToken cancellationToken = default);
}

public enum PluginNetworkRuleKind { Block, Redirect, StripHeader }
public sealed record PluginNetworkRule(PluginNetworkRuleKind Kind, string Match, string? Replacement = null);
public interface IPluginNetworkRules
{
    void SetRules(IEnumerable<PluginNetworkRule> rules);
    void Clear();
}

public sealed record PluginProtocolRequest(string Scheme, string Url, string Method);
public sealed record PluginProtocolResponse(byte[] Body, string ContentType, int StatusCode = 200, string? Charset = null);
public interface IPluginProtocols
{
    IDisposable Register(string scheme, Func<PluginProtocolRequest, CancellationToken, Task<PluginProtocolResponse>> handler);
}

public sealed record PluginContentTransformRequest(string Url, string ContentType, string? Charset, byte[] Body);
public interface IPluginContentTransform
{
    IDisposable Register(string contentType, Func<PluginContentTransformRequest, CancellationToken, Task<string>> handler);
}

public interface IPluginPageStyle
{
    IDisposable SetCss(string css);
}

public sealed record PluginTabInfo(string Id, string Url, string Title, bool Active);
public sealed record PluginBeforeNavigateEventArgs(string TabId, string Url);
public enum PluginBeforeNavigateAction { Allow, Cancel, Redirect }
public sealed record PluginBeforeNavigateDecision(PluginBeforeNavigateAction Action, string? RedirectUrl = null);
public interface IPluginTabs
{
    Task<IReadOnlyList<PluginTabInfo>> ListAsync(CancellationToken cancellationToken = default);
    event Func<PluginBeforeNavigateEventArgs, Task<PluginBeforeNavigateDecision>>? BeforeNavigate;
}

public sealed record PluginHistoryEntry(string Url, string Title, DateTimeOffset VisitedUtc);
public interface IPluginHistory
{
    Task<IReadOnlyList<PluginHistoryEntry>> SearchAsync(string? query = null, int maxResults = 100, CancellationToken cancellationToken = default);
}

public sealed record PluginBookmarkEntry(string Title, string Url, DateTimeOffset AddedUtc);
public interface IPluginBookmarks
{
    Task<IReadOnlyList<PluginBookmarkEntry>> ListAsync(int maxResults = 500, CancellationToken cancellationToken = default);
    Task AddAsync(string title, string url, CancellationToken cancellationToken = default);
    Task RemoveAsync(string url, CancellationToken cancellationToken = default);
}

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
    event EventHandler<PluginNavigationFailedEventArgs>? NavigationFailed;
    event EventHandler<PluginTitleChangedEventArgs>? TitleChanged;
    event EventHandler<PluginLoadProgressEventArgs>? LoadProgress;
    event EventHandler<PluginZoomChangedEventArgs>? ZoomChanged;
    IDisposable CreateTimer(TimeSpan interval, Action callback);
}

public sealed class PluginNavigationEventArgs : EventArgs { public PluginNavigationEventArgs(string url) => Url = url; public string Url { get; } }
public sealed class PluginNavigationFailedEventArgs : EventArgs { public PluginNavigationFailedEventArgs(string url, string message) { Url = url; Message = message; } public string Url { get; } public string Message { get; } }
public sealed class PluginTitleChangedEventArgs : EventArgs { public PluginTitleChangedEventArgs(string url, string title) { Url = url; Title = title; } public string Url { get; } public string Title { get; } }
public sealed class PluginLoadProgressEventArgs : EventArgs { public PluginLoadProgressEventArgs(string url, double fraction) { Url = url; Fraction = Math.Clamp(fraction, 0d, 1d); } public string Url { get; } public double Fraction { get; } }
public sealed class PluginZoomChangedEventArgs : EventArgs { public PluginZoomChangedEventArgs(float zoom) => Zoom = zoom; public float Zoom { get; } }
public sealed class PluginPageEventArgs : EventArgs { public PluginPageEventArgs(string url, string title) { Url = url; Title = title; } public string Url { get; } public string Title { get; } }
public sealed class FocusEventArgs : EventArgs { public FocusEventArgs(bool hasFocus) => HasFocus = hasFocus; public bool HasFocus { get; } }

public enum PluginPcmSampleFormat { PcmS16Le, Float32Le }
public sealed record PluginPcmFormat(int SampleRate, int Channels, PluginPcmSampleFormat SampleFormat);

public interface IAudioService
{
    Task PlayFileAsync(string path);
    Task PlayBytesAsync(byte[] pcm, PluginPcmFormat format, CancellationToken cancellationToken = default);
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

public sealed record PluginDownloadProgress(string Url, string RelativePath, long BytesDownloaded, long? TotalBytes, bool Completed, string? Error = null);
public interface IPluginDownloads
{
    Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken = default);
    event EventHandler<PluginDownloadProgress>? Progress;
}

public sealed record PluginOmniboxSuggestion(string Text, string? Url = null, string? Description = null);
public interface IPluginOmnibox
{
    IDisposable RegisterKeyword(string keyword, Func<string, CancellationToken, Task<IReadOnlyList<PluginOmniboxSuggestion>>> handler);
}

public enum PluginMenuChoice { File, View, Tools, Help }
public sealed record PluginKeyboardShortcut(string Shortcut, string Description);
public interface IPluginUiExtras
{
    IDisposable AddToolbarButton(string label, string tooltip, byte[]? pngIcon, Action onClick, PluginMenuChoice menu = PluginMenuChoice.File);
    void SetBadge(string text);
    IDisposable RegisterShortcut(string shortcut, string description, Action callback);
}

public sealed record PluginSettingDefinition(string Name, string Type, string Label, string? Description = null, string? DefaultValue = null, string[]? Options = null);

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

public enum EmbeddedCursor { Default, Arrow, Hand, IBeam, Cross, SizeAll, SizeNS, SizeWE }
public sealed class EmbeddedVisibilityEventArgs : EventArgs { public EmbeddedVisibilityEventArgs(bool visible) => Visible = visible; public bool Visible { get; } }
public sealed class EmbeddedPauseEventArgs : EventArgs { public EmbeddedPauseEventArgs(bool paused) => Paused = paused; public bool Paused { get; } }
public sealed class EmbeddedResizeEventArgs : EventArgs { public EmbeddedResizeEventArgs(int width, int height) { Width = width; Height = height; } public int Width { get; } public int Height { get; } }

public interface IEmbeddedContentHost
{
    string? CurrentUrl { get; }
    string UserAgent { get; }
    bool IsVisible { get; }
    bool IsPaused { get; }
    bool IsAudioMuted { get; }
    event EventHandler<EmbeddedVisibilityEventArgs>? VisibilityChanged;
    event EventHandler<EmbeddedPauseEventArgs>? PauseChanged;
    event EventHandler<EmbeddedResizeEventArgs>? Resized;
    Task SetStatusAsync(string text, CancellationToken cancellationToken = default);
    Task RequestNavigationAsync(string url, CancellationToken cancellationToken = default);
    Task<IPluginNetworkResponse> OpenStreamAsync(HttpPluginRequest request, CancellationToken cancellationToken = default);
    Task PushAudioAsync(byte[] pcm, PluginPcmFormat format, CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetCursorAsync(EmbeddedCursor cursor, CancellationToken cancellationToken = default);
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