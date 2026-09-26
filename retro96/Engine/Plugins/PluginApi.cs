using System.Collections.ObjectModel;
using System.Text.Json;

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
    Dialogs = 1 << 16
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
            [PluginPermission.Dialogs] = "dialogs"
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

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
