namespace Retro96;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Retro96.Engine;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;
using Retro96.Plugins;
using LayoutEngineApi = Retro96.Engine.Layout.LayoutEngine;

/// <summary>
/// Opt-in diagnostic logger. Set RETRO96_DEBUG=1 before launching to write
/// retro96-debug.log next to the executable.
/// </summary>
public static class DebugLog
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("RETRO96_DEBUG") == "1";

    // Focused JavaScript/DOM diagnostics. RETRO96_DEBUG also enables JS logs.
    public static readonly bool JsEnabled =
        Enabled || Environment.GetEnvironmentVariable("RETRO96_JS_DEBUG") == "1";

    // Extra-verbose JavaScript property/function tracing.
    public static readonly bool JsTraceEnabled =
        Environment.GetEnvironmentVariable("RETRO96_JS_TRACE") == "1";

    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "retro96-debug.log");
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Lock)
            {
                File.AppendAllText(Path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the thing that crashes the app.
        }
    }

    public static void WriteException(string context, Exception ex) =>
        Write($"{context} THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static void JsWrite(string message)
    {
        if (!JsEnabled) return;
        try
        {
            lock (Lock)
            {
                File.AppendAllText(Path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [JSDBG] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect page execution.
        }
    }
}

public partial class Form1 : Form
{
    // Controls
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripDropDownButton _btnFile = new("File");
    private readonly ToolStripButton _btnBack = new("←");
    private readonly ToolStripButton _btnForward = new("→");
    private readonly ToolStripButton _btnReload = new("⟳");
    private readonly ToolStripButton _btnStop = new("■");
    private readonly ToolStripButton _btnPrint = new("Print");
    private readonly ToolStripTextBox _txtUrl = new();
    private readonly ToolStripButton _btnGo = new("Go");
    private readonly BrowserCanvas _canvas = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private const bool ThrobberEnabled = false;
    private readonly SKGLControl _throbberBox = new();

    // Throbber state
    private DecodedImage? _throbberDecoded;
    private int _throbberFrameIndex;
    private System.Windows.Forms.Timer? _throbberTimer;
    private SKBitmap? _staticBitmap;
    private string? _throbberDiag;   // why static.png is missing, drawn into the box
    private bool _isLoading;

    // Session state
    private readonly NavigationHistory _history = new();

    // Certificate-error recovery: the URL whose TLS failed, kept so the
    // error page's window.acceptCertRisk() hook (registered while this is
    // set) can re-navigate.  Capped to avoid a confirm-dialog retry loop.
    private string? _pendingCertRetryUrl;
    private int _certRetryCount;
    private readonly HashSet<string> _visitedUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Form1> _childWindows = new();
    private CancellationTokenSource? _loadCts;
    private System.Windows.Forms.Timer? _metaRefreshTimer;
    private string? _currentPageUrl;
    private string? _referrerUrl;          // document.referrer for the next page

    // User preferences (search engine etc.); persisted per-user with a portable retro96.ini mirror
    private readonly UserSettings _settings = UserSettings.Load();
    // Process DPI awareness is selected by Program before any controls exist,
    // so keep the startup value separate from the editable preference. This
    // prevents changing the checkbox at runtime from making a live monitor
    // transition half-scaled before the required restart.
    private bool _highDpiScaleModeActive;

    // Guards against meta-refresh chains that loop forever
    private string? _metaRefreshChainStartUrl;
    private int _metaRefreshChainCount;
    private bool _isMetaRefreshNav;
    private int _pluginRedirectDepth;
    private const int MaxMetaRefreshChain = 10;

    private long _navGeneration;

    // Engine components
    private readonly CookieStore _cookieStore = new();
    private readonly ImageCache _imageCache = new();
    private readonly FontCache _fontCache = new();
    private ResourceLoader? _resourceLoader;
    private readonly HttpClient _httpClient = new();
    private static readonly System.Net.Http.HttpClient _pluginHttpClient = new(new System.Net.Http.HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    });
    private bool _hostOpenedLocalDocument;

    // Per-page JS
    private JsScope? _globalScope;
    private JsInterpreter? _jsInterpreter;
    private DocumentBindingsState? _jsState;

    // C# plugin host. Plugins are loaded after the shell/engine are initialized
    // so their APIs can safely target the live browser window.
    private PluginManager? _pluginManager;
    private ToolStripMenuItem? _pluginCommandsMenu;
    internal BrowserCanvas PluginCanvas => _canvas;

    internal string? PluginCurrentUrl => _currentPageUrl;
    internal bool PluginDevMode => _settings.PluginDevMode;

    internal string PluginCurrentTitle => Text.EndsWith(" — Retro96", StringComparison.Ordinal)
        ? Text[..^10] : Text;

    internal string PluginUserAgent => BrowserRuntime.UserAgent;

    internal sealed record PluginEmbeddedSource(
        Stream Body, int StatusCode, IReadOnlyDictionary<string, string> Headers,
        string? ContentType, string? Charset, string EffectiveUrl, bool CanSeek, long? Length) : IDisposable
    {
        public void Dispose() { Body.Dispose(); }
    }

    internal async Task<PluginEmbeddedSource> OpenPluginEmbeddedSourceAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new InvalidDataException("Embedded source URL is invalid.");
        if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParsedUrl.Parse(uri.AbsoluteUri);
            string? path = LocalPathFromFileUrl(parsed);
            if (path == null || !File.Exists(path)) throw new FileNotFoundException("Embedded source was not found.", path);
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return new PluginEmbeddedSource(fs, 200, new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase),
                MimeFromExtension(path), null, CanonicalFileUrl(path), true, fs.Length);
        }
        if (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            int comma = url.IndexOf(',');
            if (comma < 0) throw new InvalidDataException("Malformed data URL.");
            string meta = url[..comma];
            string dataPart = url[(comma + 1)..];
            byte[] bytes = meta.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Uri.UnescapeDataString(dataPart))
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(dataPart));
            return new PluginEmbeddedSource(new MemoryStream(bytes, writable: false), 200, new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase),
                meta.Split(';').FirstOrDefault(x => x.StartsWith("data:", StringComparison.OrdinalIgnoreCase))?[5..] ?? "text/plain", null, uri.AbsoluteUri, true, bytes.Length);
        }
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Embedded sources may only use http, https, file, or data URLs.");

        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", PluginUserAgent);
        if (BrowserRuntime.ReferrerEnabled && !string.IsNullOrWhiteSpace(_currentPageUrl))
            request.Headers.TryAddWithoutValidation("Referer", _currentPageUrl);
        try
        {
            string cookie = _cookieStore.Get(ParsedUrl.Parse(uri.AbsoluteUri));
            if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        catch { }
        var response = await _pluginHttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException($"Embedded source request failed with HTTP {status}.");
        }
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        string? charset = response.Content.Headers.ContentType?.CharSet;
        string effective = response.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;
        long? length = response.Content.Headers.ContentLength;
        // Keep the HttpResponseMessage alive by wrapping its content stream.
        return new PluginEmbeddedSource(new ResponseOwnedStream(response, stream), (int)response.StatusCode,
            response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
            contentType, charset, effective, false, length);
    }

    internal async Task<PluginSandboxSession.PluginNetworkStream> OpenPluginNetworkStreamAsync(
        PluginSandboxProtocol.NetworkRequestPayload request, PluginManager.PluginRecord record, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) throw new InvalidDataException("Plugin network URL is invalid.");
        if (uri.Scheme is not ("http" or "https" or "file" or "data"))
            throw new SecurityException("Plugin network requests may only use http, https, file, or data URLs.");

        if (uri.Scheme is "file" or "data")
        {
            var local = await OpenPluginEmbeddedSourceAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return new PluginSandboxSession.PluginNetworkStream(local.Body, local.StatusCode, local.Headers, local.ContentType, local.Charset, local.EffectiveUrl, local.CanSeek, local.Length);
        }

        var method = new System.Net.Http.HttpMethod(request.Method?.Trim().ToUpperInvariant() ?? "GET");
        using var message = new System.Net.Http.HttpRequestMessage(method, uri);
        message.Headers.TryAddWithoutValidation("User-Agent", PluginUserAgent);
        if (BrowserRuntime.ReferrerEnabled && !string.IsNullOrWhiteSpace(_currentPageUrl))
            message.Headers.TryAddWithoutValidation("Referer", _currentPageUrl);
        foreach (var pair in request.Headers ?? new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase))
            message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        try
        {
            string cookie = _cookieStore.Get(ParsedUrl.Parse(uri.AbsoluteUri));
            if (!string.IsNullOrEmpty(cookie) && !(request.Headers?.Keys.Any(k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) ?? false))
                message.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        catch { }
        if (!string.IsNullOrEmpty(request.BodyBase64))
        {
            var bytes = Convert.FromBase64String(request.BodyBase64);
            message.Content = new ByteArrayContent(bytes);
            if (!string.IsNullOrWhiteSpace(request.ContentType)) message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType);
        }
        var response = await _pluginHttpClient.SendAsync(message, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var detached = await ResponseOwnedStream.DetachAsync(response, cancellationToken).ConfigureAwait(false);
        return new PluginSandboxSession.PluginNetworkStream(detached.Stream, (int)response.StatusCode, detached.Headers, detached.ContentType, detached.Charset, detached.EffectiveUrl, false, detached.Length);
    }

    private static string MimeFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dcr" or ".dir" or ".dxr" => "application/x-director",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly System.Net.Http.HttpResponseMessage _response;
        private readonly Stream _inner;
        public ResponseOwnedStream(System.Net.Http.HttpResponseMessage response, Stream inner) { _response=response; _inner=inner; }
        public static async Task<(Stream Stream, IReadOnlyDictionary<string,string> Headers, string? ContentType, string? Charset, string EffectiveUrl, long? Length, IDisposable Owner)> DetachAsync(System.Net.Http.HttpResponseMessage response, CancellationToken ct)
        {
            var inner = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var stream = new ResponseOwnedStream(response, inner);
            return (stream, response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase), response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentType?.CharSet, response.RequestMessage?.RequestUri?.AbsoluteUri ?? "", response.Content.Headers.ContentLength, stream);
        }
        protected override void Dispose(bool disposing) { if (disposing) { try { _inner.Dispose(); } catch { } try { _response.Dispose(); } catch { } } base.Dispose(disposing); }
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => _inner.CanSeek; public override bool CanWrite => _inner.CanWrite; public override long Length => _inner.Length; public override long Position { get=>_inner.Position; set=>_inner.Position=value; }
        public override void Flush()=>_inner.Flush(); public override int Read(byte[] buffer,int offset,int count)=>_inner.Read(buffer,offset,count); public override long Seek(long offset,SeekOrigin origin)=>_inner.Seek(offset,origin); public override void SetLength(long value)=>_inner.SetLength(value); public override void Write(byte[] buffer,int offset,int count)=>_inner.Write(buffer,offset,count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct=default)=>_inner.ReadAsync(buffer,ct);
    }

    public Form1()
    {
        _highDpiScaleModeActive = _settings.HighDpiScaleMode;
        BrowserRuntime.Apply(_settings);
        InitializeComponent();
        InitializeBrowser();
        _canvas.ZoomFactor = _settings.DefaultPageZoomPercent / 100f;
        InitializeBuiltInFeatures();
        _pluginManager = new PluginManager(this);
        _httpClient.PluginRuleEvaluator = _pluginManager.EvaluateNetworkRules;
    }

    // ─────────────────────────────────────────────────────────────────────
    // UI setup
    // ─────────────────────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        Text = "Retro96";
        Size = new Size(1024, 760);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += (s, e) =>
        {
            if (IsWelcomeUrl(_currentPageUrl))
            {
                _settings.WelcomeDismissed = true;
                _settings.Save();
            }
            _loadCts?.Cancel();
            if (_settings.DiscardPageStateOnClose)
            {
                _canvas.ClearForNavigation();
                _cookieStore.ClearAll();
            }
            _pluginManager?.RaiseHostShuttingDown();
            _pluginManager?.Dispose();
            _pluginManager = null;
            _pluginPcmMixer.Dispose();
            DisposeBuiltInFeatures();
            _resourceLoader?.Dispose();
            _imageCache?.Dispose();
            _fontCache?.Dispose();
            _globalScope = null;
            _jsState = null;
            _jsInterpreter = null;
            _currentPageUrl = null;
            foreach (var child in _childWindows.ToArray())
                child.Close();
        };

        _txtUrl.AutoSize = false;
        _txtUrl.Width = 480;

        _toolbar.Items.Add(_btnFile);
        _toolbar.Items.Add(_btnBack);
        _toolbar.Items.Add(_btnForward);
        _toolbar.Items.Add(_btnReload);
        _toolbar.Items.Add(_btnStop);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_txtUrl);
        _toolbar.Items.Add(_btnGo);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_btnPrint);
        _btnBack.Enabled = false;
        _btnForward.Enabled = false;
        _btnStop.Enabled = false;

        _btnFile.DropDownItems.Add("Plugin Addons\u2026").Click += (s, e) => _pluginManager?.OpenManager(this);
        _btnFile.DropDownItems.Add(new ToolStripSeparator());
        _btnFile.DropDownItems.Add("Open Local HTML\u2026").Click += (s, e) => OpenHtmlFile();
        _btnFile.DropDownItems.Add("Open New Window").Click += (s, e) => OpenNewBrowserWindow("about:blank");
        _btnFile.DropDownItems.Add("Preferences\u2026").Click += (s, e) => ShowPreferencesDialog();
        _pluginCommandsMenu = new ToolStripMenuItem("Plugin Commands");
        _pluginCommandsMenu.Visible = false;
        _btnFile.DropDownItems.Add(_pluginCommandsMenu);

        _throbberBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _throbberBox.PaintSurface += (s, e) =>
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var target = new SKRect(0, 0, e.Info.Width, e.Info.Height);
            if (_isLoading && _throbberDecoded != null && _throbberDecoded.Frames.Count > 0)
            {
                var frame = _throbberDecoded.Frames[_throbberFrameIndex % _throbberDecoded.Frames.Count];
                if (frame?.SkBitmap != null)
                    canvas.DrawBitmap(frame.SkBitmap, target, SKSamplingOptions.Default);
            }
            else if (_staticBitmap != null)
            {
                canvas.DrawBitmap(_staticBitmap, target, SKSamplingOptions.Default);
            }
            else if (_throbberDiag != null)
            {
                using var bg = new SKPaint { Color = SKColors.MistyRose, IsAntialias = false };
                canvas.DrawRect(target, bg);
                using var typeface = SKTypeface.FromFamilyName("Segoe UI");
                using var font = new SKFont(typeface, 7f);
                using var text = new SKPaint { Color = SKColors.DarkRed, IsAntialias = true };
                canvas.DrawText(_throbberDiag, 4f, Math.Max(10f, 4f + font.Size), SKTextAlign.Left, font, text);
            }
        };

        _canvas.Dock = DockStyle.Fill;
        _canvas.NavigateRequested += OnCanvasNavigateRequested;
        _canvas.StatusChanged += OnCanvasStatusChanged;
        _canvas.NewWindowRequested += OpenNewBrowserWindow;
        _canvas.FrameNavigationRequested += OnFrameNavigationRequested;
        _canvas.ExternalProtocolRequested += OpenExternalProtocol;
        _canvas.FormSubmitRequested += OnFormSubmitted;

        // Context-menu navigation hooks
        _canvas.BackRequested += NavigateBack;
        _canvas.ForwardRequested += NavigateForward;
        _canvas.ReloadRequested += Reload;

        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Dock = DockStyle.Bottom;
        // Keep enough vertical room for the native status-strip renderer's
        // border/padding plus the full status-font ascent/descent.  A fixed
        // 24px strip could clip the lower part of short live-status messages
        // such as "Done" on some font/DPI combinations.
        _statusStrip.AutoSize = false;
        var statusFont = SystemFonts.StatusFont ?? SystemFonts.DefaultFont;
        _statusStrip.Height = Math.Max(28, statusFont.Height + 10);
        _statusStrip.Padding = Padding.Empty;
        _statusStrip.Visible = true;
        _statusStrip.SizingGrip = false;
        _statusStrip.RenderMode = ToolStripRenderMode.System;
        _statusLabel.AutoSize = false;
        _statusLabel.Font = statusFont;
        _statusLabel.Margin = Padding.Empty;
        _statusLabel.Padding = new Padding(4, 0, 4, 0);
        _statusLabel.DisplayStyle = ToolStripItemDisplayStyle.Text;
        // Website-driven window.status tickers rely on the native status bar's
        // leading edge and often pad/rotate the string themselves. Centering the
        // label made those updates appear trapped in a small band in the middle
        // of the strip. Use the full spring-expanded strip from the left edge so
        // the site's own horizontal ticker can traverse the entire available area.
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Ready";
        _statusLabel.Spring = true;

        Controls.Add(_canvas);
        Controls.Add(_statusStrip);
        Controls.Add(_toolbar);
        Controls.Add(_throbberBox);
        _throbberBox.Visible = ThrobberEnabled;

        _btnBack.Click += (s, e) => NavigateBack();
        _btnForward.Click += (s, e) => NavigateForward();
        _btnReload.Click += (s, e) => Reload();
        _btnStop.Click += (s, e) => _loadCts?.Cancel();
        _btnPrint.Click += (s, e) => PrintPage();

        _btnGo.Click += (s, e) => NavigateOrSearch(_txtUrl.Text);
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.D)
            {
                AddCurrentPageBookmark();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (_pluginShortcuts.TryGetValue(e.KeyData, out var shortcut)) { try { shortcut.Callback(); } catch (Exception ex) { DebugLog.WriteException($"Plugin shortcut '{shortcut.PluginId}'", ex); } e.Handled = true; e.SuppressKeyPress = true; }
        };

        _txtUrl.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                NavigateOrSearch(_txtUrl.Text);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        Load += (s, e) =>
        {
            float scale = _highDpiScaleModeActive && DeviceDpi > 0
                ? DeviceDpi / 96f : 1f;
            if (scale > 1.01f)
            {
                Size = new Size((int)(Width * scale), (int)(Height * scale));
                _txtUrl.Width = (int)(480 * scale);
            }

            // Land on the configured home page instead of a blank canvas.
            NavigateTo(_settings.WelcomeDismissed ? _settings.HomePageUrl : "retro96:welcome");
            SetAppIcon();
        };

        Resize += (s, e) => PositionThrobber();
        DpiChanged += (s, e) =>
        {
            if (!_highDpiScaleModeActive) return;

            // PerMonitorV2 delivers the monitor transition here.  Form1 uses
            // AutoScaleMode.None because the retro shell owns its pixel layout,
            // so update the handful of explicitly sized shell controls here and
            // let Windows provide the recommended window bounds.
            Bounds = e.SuggestedRectangle;
            float scale = Math.Max(1f, e.DeviceDpiNew / 96f);
            _txtUrl.Width = (int)Math.Round(480f * scale);
            PositionThrobber();
        };
    }

    // Base size at 96 DPI; scaled by the monitor's DPI in PositionThrobber.
    private const int ThrobberBaseSize = 56;

    private void PositionThrobber()
    {
        if (!ThrobberEnabled || _throbberBox == null || _throbberBox.IsDisposed) return;

        float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
        int size = (int)(ThrobberBaseSize * scale);
        int margin = (int)(6 * scale);

        _throbberBox.Size = new Size(size, size);
        // ClientSize, not Width: Width includes the window frame and pushed
        // the old 20px box off the right edge.
        _throbberBox.Location = new Point(ClientSize.Width - size - margin, margin);
        _throbberBox.BringToFront();
    }

    private void InitializeBrowser()
    {
        _resourceLoader = new ResourceLoader(_cookieStore);
        _imageCache.CookieStore = _cookieStore;
        _imageCache.HostOpenedLocalPage = _hostOpenedLocalDocument;

        // An image that failed transiently and later recovered (cooldown
        // refetch triggered from a paint) has nobody awaiting it — repaint
        // so it actually appears instead of waiting for an unrelated redraw.
        _imageCache.ImageRecovered += _ =>
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() => _canvas.RequestRerender());
            }
            catch (ObjectDisposedException) { /* closing */ }
            catch (InvalidOperationException) { /* handle gone */ }
        };

        _canvas.SetResourceLoader(_resourceLoader);

        // THE measurement hookup: InlineLayout measures every text run,
        // table column, button label and select width — and it was never
        // given the FontCache, so every width fell back to the
        // characters × 8px heuristic. The renderer and layout now share
        // the same SkiaSharp font metrics, so measured and drawn widths
        // stay aligned. This fixes drifting/colliding text, incorrectly
        // sized buttons, and table columns that used to overestimate width.
        InlineLayout.SetFontCache(_fontCache);

        // JS history.back()/forward()/go() actually navigates
        _history.NavigationRequested += url => BeginInvoke(() => NavigateTo(url));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Address bar: URL or search
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Address-bar dispatch: URL-looking text navigates, anything else is
    /// a web search through the configured engine (FrogFind by default,
    /// user-changeable in File → Preferences… or retro96.ini).
    /// </summary>
    private void NavigateOrSearch(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return;

        // Local paths/URLs get first-class treatment. This prevents Windows
        // paths containing spaces from being sent to search, and lets a
        // locally-opened page address another existing file beside it.
        if (FileUrls.TryResolveAddressBarInput(t, _currentPageUrl, out string localUrl))
        {
            NavigateTo(localUrl);
            return;
        }

        if (LooksLikeUrl(t))
        {
            NavigateTo(t);
            return;
        }

        string template = UserSettings.NormalizeSearchTemplate(_settings.SearchQueryUrl);
        NavigateTo(template.Replace("%s", ParsedUrl.PercentEncode(t)));
    }

    private static bool LooksLikeUrl(string t)
    {
        if (t.Contains(' ')) return false;
        if (t.StartsWith("//")) return true;

        // scheme:path — a letter first, then letters/digits/+/-/. up to ':'
        int colon = t.IndexOf(':');
        if (colon > 0 && char.IsLetter(t[0]) &&
            t[..colon].All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.'))
            return true;

        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains('.')) return true;
        if (t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void ShowPreferencesDialog()
    {
        using var dialog = new PreferencesDialog(_settings, _pluginManager);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var updated = dialog.Settings;
        bool highDpiModeChanged = updated.HighDpiScaleMode != _settings.HighDpiScaleMode;
        _settings.SearchQueryUrl = UserSettings.NormalizeSearchTemplate(updated.SearchQueryUrl);
        _settings.HomePageUrl = updated.HomePageUrl;
        _settings.EngineMode = updated.EngineMode;
        _settings.UserAgentOverride = updated.UserAgentOverride;
        _settings.BackgroundMode = updated.BackgroundMode;
        _settings.ForcedBackgroundColor = updated.ForcedBackgroundColor;
        _settings.LoadImages = updated.LoadImages;
        _settings.EnableJavaScript = updated.EnableJavaScript;
        _settings.EnableVBScript = updated.EnableVBScript;
        _settings.EnableJavaApplets = updated.EnableJavaApplets;
        _settings.AllowScriptedWindows = updated.AllowScriptedWindows;
        _settings.HighDpiScaleMode = updated.HighDpiScaleMode;
        _settings.DefaultPageZoomPercent = updated.DefaultPageZoomPercent;
        _settings.LoadStylesheets = updated.LoadStylesheets;
        _settings.LoadFrames = updated.LoadFrames;
        _settings.AllowFormSubmissions = updated.AllowFormSubmissions;
        _settings.EnableExternalScripts = updated.EnableExternalScripts;
        _settings.EnableJavaScriptEval = updated.EnableJavaScriptEval;
        _settings.EnableJavaScriptTimers = updated.EnableJavaScriptTimers;
        _settings.EnableJavaScriptDialogs = updated.EnableJavaScriptDialogs;
        _settings.JavaScriptMaxExecutionSeconds = updated.JavaScriptMaxExecutionSeconds;
        _settings.JavaScriptMemoryLimitMb = updated.JavaScriptMemoryLimitMb;
        _settings.JavaScriptMaxCallDepth = updated.JavaScriptMaxCallDepth;
        _settings.MaxScriptSpliceTokens = updated.MaxScriptSpliceTokens;
        _settings.JavaMaxCallDepth = updated.JavaMaxCallDepth;
        _settings.FollowHttpRedirects = updated.FollowHttpRedirects;
        _settings.RequestCompressedResponses = updated.RequestCompressedResponses;
        _settings.MaxHttpRedirects = updated.MaxHttpRedirects;
        _settings.HttpConnectTimeoutSeconds = updated.HttpConnectTimeoutSeconds;
        _settings.HttpResponseTimeoutSeconds = updated.HttpResponseTimeoutSeconds;
        _settings.MaxConcurrentResourceFetches = updated.MaxConcurrentResourceFetches;
        _settings.MaxResourceFetchesPerPage = updated.MaxResourceFetchesPerPage;
        _settings.FollowMetaRefresh = updated.FollowMetaRefresh;
        _settings.EnableCookies = updated.EnableCookies;
        _settings.SendReferrer = updated.SendReferrer;
        _settings.AnimateImages = updated.AnimateImages;
        _settings.AnimatedGifSpeedPercent = updated.AnimatedGifSpeedPercent;
        _settings.BlinkText = updated.BlinkText;
        _settings.BlinkIntervalMilliseconds = updated.BlinkIntervalMilliseconds;
        _settings.MarqueeText = updated.MarqueeText;
        _settings.MarqueeSpeedPercent = updated.MarqueeSpeedPercent;
        _settings.TrustMode = updated.TrustMode;
        _settings.HostCheckImages = updated.HostCheckImages;
        _settings.DiscardPageStateOnClose = updated.DiscardPageStateOnClose;
        _settings.PluginDevMode = updated.PluginDevMode;
        _settings.Save();
        BrowserRuntime.Apply(_settings);
        _canvas.ZoomFactor = _settings.DefaultPageZoomPercent / 100f;

        // Preferences are live for the current page where possible.  A reload
        // is required for navigator/User-Agent, scripting and newly tightened
        // resource policy to apply consistently to the active document.
        if (_currentPageUrl != null)
            Reload();
        else
            _canvas.RequestRerender();

        if (highDpiModeChanged)
        {
            MessageBox.Show(this,
                "High-DPI scale mode has been saved. Restart Retro96 for the new Windows DPI awareness mode to take effect.",
                "Retro96", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static string EscapeHtmlText(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;")
                 .Replace(">", "&gt;").Replace("\"", "&quot;");

    // ─────────────────────────────────────────────────────────────────────
    // Navigation
    // ─────────────────────────────────────────────────────────────────────

    public async void NavigateTo(string rawUrl) => await NavigateAsync(rawUrl);

    public async Task NavigateAsync(string rawUrl, string? postData = null,
                                     bool replaceHistory = false)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return;

        string requestedUrl = rawUrl.Trim();
        if (FileUrls.TryResolveAddressBarInput(requestedUrl, _currentPageUrl, out string localUrl))
            requestedUrl = localUrl;
        rawUrl = requestedUrl;

        if (!_settings.WelcomeDismissed && IsWelcomeUrl(_currentPageUrl) && !IsWelcomeUrl(rawUrl))
        {
            _settings.WelcomeDismissed = true;
            _settings.Save();
        }

        // Keep the address bar/history URL synchronized with the navigation
        // attempt before any error page is rendered.  Local file failures use
        // RenderErrorAsync(), which historically took _txtUrl.Text; leaving
        // the old page URL there caused the generated error page to be
        // treated as a duplicate of the current entry, so Back skipped the
        // page that was actually being left.
        _txtUrl.Text = rawUrl;

        if (rawUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            !_hostOpenedLocalDocument && !BrowserRuntime.AllowPageFileAccess)
        {
            _statusLabel.Text = "Blocked by the current security mode: page-directed file access.";
            return;
        }

        if (!rawUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            _hostOpenedLocalDocument = false;
            _imageCache.HostOpenedLocalPage = false;
        }

        long myGeneration = ++_navGeneration;
        _canvas.ClearForNavigation();
        _midiPlayer.Stop();
        _midiUiSuppressed = false;
        UpdateMidiUi();
        _pendingCertRetryUrl = null;   // a fresh navigation invalidates any stale acceptCertRisk hook
        DebugLog.Write($"NavigateAsync ENTER gen={myGeneration} rawUrl='{rawUrl}' postData={(postData != null ? "yes" : "no")} replaceHistory={replaceHistory}");

        bool isMetaRefreshNav = _isMetaRefreshNav;
        _isMetaRefreshNav = false;
        if (!isMetaRefreshNav)
        {
            _metaRefreshChainStartUrl = null;
            _metaRefreshChainCount = 0;
        }

        try
        {
            var url = ParsedUrl.Parse(rawUrl.Trim());

            if (_pluginManager != null)
            {
                var decision = await _pluginManager.BeforeNavigateAsync(rawUrl, CancellationToken.None).ConfigureAwait(true);
                if (decision.Action == PluginBeforeNavigateAction.Cancel)
                {
                    _statusLabel.Text = "Navigation cancelled by a plugin.";
                    return;
                }
                if (decision.Action == PluginBeforeNavigateAction.Redirect && !string.IsNullOrWhiteSpace(decision.RedirectUrl))
                {
                    if (decision.RedirectUrl.Equals(rawUrl, StringComparison.OrdinalIgnoreCase)) return;
                    if (_pluginRedirectDepth >= 3)
                    {
                        await RenderErrorAsync(ErrorPage.NetworkError(rawUrl, "Too many plugin navigation redirects."), myGeneration);
                        return;
                    }
                    _pluginRedirectDepth++;
                    try { await NavigateAsync(decision.RedirectUrl, postData, replaceHistory).ConfigureAwait(true); }
                    finally { _pluginRedirectDepth--; }
                    return;
                }
            }

            if (_pluginManager != null && !url.IsHttp && url.Scheme is not ("about" or "file" or "mailto" or "retro96" or "data" or "javascript"))
            {
                var protocolResponse = await _pluginManager.HandleProtocolAsync(url.Scheme, rawUrl, postData == null ? "GET" : "POST", CancellationToken.None).ConfigureAwait(true);
                if (protocolResponse != null)
                {
                    await RenderPluginProtocolResponseAsync(rawUrl, protocolResponse, replaceHistory, myGeneration).ConfigureAwait(true);
                    return;
                }
            }

            switch (url.Scheme)
            {
                case "about":
                    // about:home (and bare "about:") is the same page as
                    // retro96://home; other about: URLs keep the stub below.
                    string aboutTarget = (url.Path ?? "").Trim('/');
                    if (aboutTarget.Equals("home", StringComparison.OrdinalIgnoreCase) ||
                        aboutTarget.Length == 0)
                    {
                        await RenderHtmlAsync(Retro96HomePageHtml(), rawUrl, replaceHistory, myGeneration);
                        return;
                    }
                    await RenderHtmlAsync(
                        "<html><head><title>About Retro96</title></head>" +
                        "<body bgcolor=\"#c0c0c0\">" +
                        "<center><h2>Retro96 Browser</h2>" +
                        "<p>A retro browser for the web as it was in 1996, with extra compatibility for later throwback sites.</p>" +
                        "<p><font size=\"-1\" color=\"#606060\">HTML 3.2 · CSS1 · ES3 JavaScript · " +
                        "VBScript 1.0 · Java applets</font></p>" +
                        "</center></body></html>",
                        rawUrl, replaceHistory, myGeneration);
                    return;

                case "mailto":
                    {
                        // Hand the link to the OS default mail app (ShellExecute).
                        // Only if there is no handler, or the link is malformed,
                        // fall back to telling the user what the page asked for.
                        string mailTarget = url.Path ?? "";
                        _statusLabel.Text = "Mail: " + mailTarget;

                        if (TryBuildMailtoUri(mailTarget, out string mailUri))
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(
                                    new System.Diagnostics.ProcessStartInfo(mailUri)
                                    { UseShellExecute = true })?.Dispose();
                                return;
                            }
                            catch (Exception mailEx)   // no default mail app registered, or launch refused
                            {
                                DebugLog.WriteException("mailto launch", mailEx);
                            }
                        }

                        MessageBox.Show(this,
                            "Retro96 couldn't open a mail program.\n\n" +
                            "The page requested:\n  " + mailTarget,
                            "Retro96 — Mail",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                case "ftp":
                    await RenderErrorAsync(ErrorPage.ProtocolNotSupported(
                        rawUrl, url.Scheme), myGeneration);
                    return;

                case "file":
                    {
                        // file:// links from locally-opened pages used to hit the
                        // "protocol not supported" wall; now they load from disk
                        // so a local page's relative links actually navigate.
                        string? localPath = LocalPathFromFileUrl(url);
                        if (localPath == null || !File.Exists(localPath))
                        {
                            await RenderErrorAsync(ErrorPage.LocalFileNotFound(
                                localPath ?? url.Path), myGeneration);
                            return;
                        }

                        try
                        {
                            string fileHtml = BodyDecoder.Decode(
                                await File.ReadAllBytesAsync(localPath), null);
                            await RenderHtmlAsync(fileHtml, CanonicalFileUrl(localPath),
                                replaceHistory, myGeneration);
                        }
                        catch (Exception ex)
                        {
                            await RenderErrorAsync(ErrorPage.LocalFileNotFound(localPath),
                                myGeneration);
                            if (myGeneration == _navGeneration)
                                _statusLabel.Text = ex.Message;
                        }
                        return;
                    }

                case "javascript":
                    try { _jsInterpreter?.ExecuteString(url.Path); }
                    catch (Exception ex) { _statusLabel.Text = $"Script error: {ex.Message}"; }
                    return;

                case "retro96":
                    if (url.Path.Equals("welcome", StringComparison.OrdinalIgnoreCase))
                    {
                        await RenderHtmlAsync(Retro96WelcomePageHtml(), rawUrl, replaceHistory, myGeneration);
                    }
                    else if (url.Path.Equals("home", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(url.Path))
                    {
                        await RenderHtmlAsync(Retro96HomePageHtml(), rawUrl, replaceHistory, myGeneration);
                    }
                    else
                    {
                        HandleInternalScheme(url.Path);
                    }
                    return;
            }

            if (!url.IsHttp)
            {
                await RenderErrorAsync(ErrorPage.MalformedUrl(rawUrl), myGeneration);
                return;
            }

            _txtUrl.Text = url.ToAbsolute();
            Text = "Loading… — Retro96";
            _statusLabel.Text = "Connecting…";
            Cursor = Cursors.WaitCursor;
            _btnStop.Enabled = true;
            _isLoading = true;
            _throbberBox.Invalidate();
            _metaRefreshTimer?.Stop();
            _metaRefreshTimer?.Dispose();
            _metaRefreshTimer = null;

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;
            _referrerUrl = BrowserRuntime.ReferrerEnabled ? _currentPageUrl : null;
            _httpClient.ReferrerOverride = BrowserRuntime.ReferrerEnabled ? _referrerUrl : null;

            try
            {
                HttpResult result = postData != null
                    ? await _httpClient.PostAsync(url, postData, _cookieStore, ct)
                    : await _httpClient.GetAsync(url, _cookieStore, ct);

                if (result is HttpSuccess unauthorized && unauthorized.StatusCode == 401)
                {
                    var creds = PromptForCredentials(url.Host);
                    if (creds != null)
                    {
                        _httpClient.BasicAuthHeader =
                            "Basic " + Convert.ToBase64String(
                                Encoding.ASCII.GetBytes($"{creds.Value.User}:{creds.Value.Pass}"));
                        _httpClient.BasicAuthOrigin = $"{url.Scheme.ToLowerInvariant()}://{url.Host.ToLowerInvariant()}:{url.Port}";
                        try
                        {
                            result = await _httpClient.GetAsync(url, _cookieStore, ct);
                        }
                        finally
                        {
                            _httpClient.BasicAuthHeader = null;
                            _httpClient.BasicAuthOrigin = null;
                        }

                        if (result is HttpSuccess ok2 && ok2.StatusCode == 401)
                        {
                            await RenderErrorAsync(ErrorPage.Unauthorized(
                                url.ToAbsolute(),
                                unauthorized.Headers.TryGetValue("www-authenticate", out var realm)
                                    ? realm : null), myGeneration);
                            return;
                        }
                    }
                    else
                    {
                        await RenderErrorAsync(ErrorPage.Unauthorized(url.ToAbsolute()), myGeneration);
                        return;
                    }
                }

                switch (result)
                {
                    case HttpSuccess s:
                        _pluginManager?.RaiseLoadProgress(url.ToAbsolute(), 0.75);
                        await ProcessSuccessAsync(s, url, ct, postData, replaceHistory, isMetaRefreshNav, myGeneration);
                        break;
                    case CertError ce:
                        _pluginManager?.RaiseNavigationFailed(url.ToAbsolute(), ce.Message);
                        _pendingCertRetryUrl = url.ToAbsolute();
                        _certRetryCount++;
                        await RenderErrorAsync(ErrorPage.CertificateError(url.ToAbsolute(), ce.Message), myGeneration);
                        break;
                    case HttpError he:
                        _pluginManager?.RaiseNavigationFailed(url.ToAbsolute(), he.Message);
                        await RenderErrorAsync(he.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                            ? ErrorPage.Timeout(url.ToAbsolute())
                            : ErrorPage.NetworkError(url.ToAbsolute(), he.Message), myGeneration);
                        break;
                    case TooManyRedirects:
                        _pluginManager?.RaiseNavigationFailed(url.ToAbsolute(), "Too many redirects.");
                        await RenderErrorAsync(ErrorPage.TooManyRedirects(url.ToAbsolute()), myGeneration);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                BeginInvoke(() =>
                {
                    if (myGeneration == _navGeneration)
                        _statusLabel.Text = "Cancelled";
                });
            }
            catch (Exception ex)
            {
                _pluginManager?.RaiseNavigationFailed(url.ToAbsolute(), ex.Message);
                BeginInvoke(async () =>
                    await RenderErrorAsync(ErrorPage.NetworkError(url.ToAbsolute(), ex.Message), myGeneration));
            }
            finally
            {
                BeginInvoke(() =>
                {
                    if (myGeneration != _navGeneration) return;
                    Cursor = Cursors.Default;
                    _btnStop.Enabled = false;
                    _isLoading = false;
                    _throbberBox.Invalidate();
                });
            }
        }
        catch (UnsafeUrlException)
        {
            await RenderErrorAsync(ErrorPage.MalformedUrl(rawUrl), myGeneration);
        }
        catch (Exception ex)
        {
            await RenderErrorAsync(ErrorPage.NetworkError(rawUrl, ex.Message), myGeneration);
        }
    }

    private void HandleInternalScheme(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "home":
                NavigateTo("retro96://home");
                return;
            case "back":
                NavigateBack();
                return;
            case "reload":
                Reload();
                return;
        }

        if (command.ToLowerInvariant().StartsWith("home/"))
        {
            NavigateTo("retro96://home");
            return;
        }
    }

    private static bool IsWelcomeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var url = ParsedUrl.Parse(value);
            return url.Scheme.Equals("retro96", StringComparison.OrdinalIgnoreCase) &&
                   url.Path.Trim('/').Equals("welcome", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── file:// helpers (shared with ImageCache's URL scheme) ────────────

    /// <summary>Builds the string handed to the OS for a mailto: link.  The
    /// scheme is always re-attached here (never trusted from the page), and
    /// anything that could break out of the mail client's command line is
    /// refused: ShellExecute substitutes the URL into the handler's
    /// registered command template, so a raw quote or control character
    /// from a hostile page must never reach it.  Spaces become %20 so
    /// "?subject=Hi there" survives an unquoted template.</summary>
    internal static bool TryBuildMailtoUri(string? target, out string uri)
    {
        uri = "";
        string s = (target ?? "").Trim();
        if (s.Length > 2000) return false;
        foreach (char c in s)
            if (c < ' ' || c == '"' || c == '\x7F') return false;

        uri = "mailto:" + s.Replace(" ", "%20");
        return true;
    }

    /// <summary>Local path behind a file: URL, accepting both
    /// file:///C:/x and file://C:/x forms; null when unmappable.</summary>
    internal static string? LocalPathFromFileUrl(ParsedUrl url) =>
        FileUrls.LocalPathFromFileUrl(url);

    /// <summary>Canonical file:/// URL for a local path (used as base URL
    /// so every relative resolution downstream is well-formed).</summary>
    internal static string CanonicalFileUrl(string localPath) =>
        FileUrls.CanonicalFileUrl(localPath);

    // ─────────────────────────────────────────────────────────────────────
    // Response processing
    // ─────────────────────────────────────────────────────────────────────

    private async Task ProcessSuccessAsync(HttpSuccess success, ParsedUrl url,
                                          CancellationToken ct,
                                          string? postData, bool replaceHistory,
                                          bool isMetaRefreshNav, long myGeneration)
    {
        // Redirects are followed inside HttpClient. Use the final response
        // URL for the address bar, history, base URL, and relative resources.
        if (!string.IsNullOrEmpty(success.EffectiveUrl))
        {
            try { url = ParsedUrl.Parse(success.EffectiveUrl); }
            catch { }
        }

        if (success.StatusCode >= 400)
        {
            string errorHtml = success.StatusCode switch
            {
                400 => ErrorPage.BadRequest(url.ToAbsolute()),
                401 => ErrorPage.Unauthorized(url.ToAbsolute()),
                403 => ErrorPage.AccessDenied(url.ToAbsolute()),
                404 => ErrorPage.NotFound(url.ToAbsolute()),
                500 => ErrorPage.ServerError(url.ToAbsolute()),
                503 => ErrorPage.ServiceUnavailable(url.ToAbsolute()),
                _ => ErrorPage.GenericHttpError(success.StatusCode, url.ToAbsolute())
            };
            await RenderHtmlAsync(errorHtml, url.ToAbsolute(), replaceHistory, myGeneration);
            return;
        }

        if (success.Body.Length <= 8 * 1024 * 1024 && _pluginManager != null)
        {
            string? transformed = await _pluginManager.TransformContentAsync(success.ContentType, url.ToAbsolute(), success.Charset, success.Body, ct).ConfigureAwait(true);
            if (transformed != null)
            {
                success = success with { Body = Encoding.UTF8.GetBytes(transformed), ContentType = "text/html", Charset = "utf-8" };
            }
        }

        if (await TryHandleBinaryNavigationAsync(success, url, ct, myGeneration).ConfigureAwait(true))
            return;

        // ── Content-Type drives interpretation (checklist): standalone
        //    images display in a page, plain text wraps in <pre> — the
        //    era behaviours.  Everything else is parsed as HTML.
        if (success.ContentType.StartsWith("image/", StringComparison.Ordinal))
        {
            string name = Path.GetFileName(url.Path);
            string page =
                "<html><head><title>" + EscapeHtmlText(name) + "</title></head>" +
                "<body bgcolor=\"#c0c0c0\" text=\"#000000\"><p>&nbsp;</p>" +
                "<img src=\"" + EscapeHtmlText(url.ToAbsolute()) + "\"" +
                " alt=\"" + EscapeHtmlText(name) + "\"></body></html>";
            await RenderHtmlAsync(page, url.ToAbsolute(), replaceHistory, myGeneration);
            return;
        }
        if (success.ContentType == "text/plain")
        {
            string text = EscapeHtmlText(DecodeBody(success));
            await RenderHtmlAsync(
                "<html><head><title>" + EscapeHtmlText(url.ToAbsolute()) + "</title></head>" +
                "<body bgcolor=\"#c0c0c0\"><pre>" + text + "</pre></body></html>",
                url.ToAbsolute(), replaceHistory, myGeneration);
            return;
        }

        string html = DecodeBody(success);
        string metaCharset = ScanMetaCharset(html);
        if (metaCharset.Length > 0 &&
            !metaCharset.Equals(success.Charset ?? "iso-8859-1",
                StringComparison.OrdinalIgnoreCase))
        {
            html = DecodeBody(success, metaCharset);
        }

        BeginInvoke(() => _statusLabel.Text = "Parsing…");

        _pluginManager?.RaiseLoadProgress(url.ToAbsolute(), 0.8);
        var (document, interpreter, state) = PrepareScripting(url, html);

        // document.lastModified / document.referrer (checklist)
        if (success.Headers.TryGetValue("last-modified", out var lastMod))
            state.LastModified = lastMod;
        state.Referrer = _referrerUrl ?? "";

        BeginInvoke(() => _statusLabel.Text = "Fetching stylesheets…");
        if (BrowserRuntime.StylesheetsEnabled)
            await FetchStylesheetsAsync(document, url, ct);
        ApplyPluginPageStyle(document);

        BeginInvoke(() => _statusLabel.Text = "Laying out…");
        _visitedUrls.Add(url.ToAbsolute());
        document.VisitedUrls.UnionWith(_visitedUrls);
        Size canvasSize = GetCanvasSize();
        StyleResolver.Resolve(document, canvasSize.Width);
        _pluginManager?.RaiseLoadProgress(url.ToAbsolute(), 0.9);

        var rootBox = LayoutEngineApi.BuildLayoutTree(
            document, canvasSize.Width, canvasSize.Height);

        RegisterBindings(document, interpreter!, state!);

        BeginInvoke(() =>
        {
            if (myGeneration != _navGeneration)
            {
                DebugLog.Write($"ProcessSuccessAsync gen={myGeneration} STALE (current={_navGeneration}) — UpdatePage dropped for {url.ToAbsolute()}");
                return;
            }
            try
            {
                UpdatePage(document, rootBox, url.ToAbsolute(),
                    new HistoryEntry(url.ToAbsolute(), postData));
                var win = interpreter.WindowObject;
                if (win != null && win.Get("onload") is { Type: JsType.Function } onload)
                    interpreter.CallHandler(onload, Retro96.Engine.Js.JsValue.FromObject(win));
                var body = document.ElementDescendants()
                    .FirstOrDefault(e => e.TagName == "body");
                if (body != null && BrowserRuntime.JavaScriptEnabled)
                {
                    DebugLog.Write($"BODY ONLOAD: found attrs={body.Attrs.Count} " +
                                   $"handlers=[{string.Join(",", body.EventHandlers.Keys)}]");
                    // Body attributes can pass through HTML recovery paths
                    // that preserve Attrs but not the event-handler map.
                    // Restore the canonical inline handler before dispatch.
                    var bodyOnload = body.GetAttr("onload");
                    DebugLog.Write($"BODY ONLOAD: source='{bodyOnload ?? "<null>"}'");
                    if (!string.IsNullOrEmpty(bodyOnload))
                        body.EventHandlers["onload"] = bodyOnload;
                    try
                    {
                        interpreter.FireEvent(body, "onload");
                    }
                    catch (Exception ex)
                    {
                        DebugLog.WriteException("BODY ONLOAD FireEvent", ex);
                    }

                    // Canonical body onload dispatch is the single execution path.
                }
                else
                    DebugLog.Write("BODY ONLOAD: body element not found");
                if (BrowserRuntime.JavaScriptEnabled)
                {
                    foreach (var elem in document.ElementDescendants()
                                 .Where(e => !ReferenceEquals(e, body) &&
                                            e.EventHandlers.ContainsKey("onload")))
                        interpreter.FireEvent(elem, "onload");
                }
                // Start image loading after the initial page is live. Fast
                // data-URI images otherwise queued natural-size reflow
                // against the old page before UpdatePage installed it.
                _ = PrefetchImagesAsync(document, url, ct,
                    reflowWhenLoaded: true, myGeneration, interpreter);
                if (BrowserRuntime.FramesEnabled)
                    _ = LoadFramesAsync(document, rootBox);
            }
            catch (Exception ex)
            {
                // An exception escaping this BeginInvoke used to hit the
                // bare message loop AFTER SetPage had already nulled the
                // bitmap — the classic "completely blank page" with no
                // explanation. Render an era-style error page instead.
                DebugLog.WriteException($"UpdatePage gen={myGeneration}", ex);
                _ = RenderErrorAsync(ErrorPage.NetworkError(url.ToAbsolute(),
                    $"Layout error: {ex.Message}"), myGeneration);
            }
        });

        if (BrowserRuntime.MetaRefreshEnabled && !string.IsNullOrEmpty(document.MetaRefresh))
        {
            var (delay, refreshUrl) = ParseMetaRefresh(document.MetaRefresh, url);
            if (refreshUrl != null)
            {
                BeginInvoke(() =>
                {
                    if (myGeneration != _navGeneration)
                        return;

                    if (_metaRefreshChainStartUrl == null || !isMetaRefreshNav)
                    {
                        _metaRefreshChainStartUrl = url.ToAbsolute();
                        _metaRefreshChainCount = 0;
                    }

                    _metaRefreshChainCount++;

                    if (_metaRefreshChainCount > MaxMetaRefreshChain)
                    {
                        _statusLabel.Text =
                            $"Stopped an automatic refresh loop after {MaxMetaRefreshChain} redirects.";
                        _metaRefreshChainStartUrl = null;
                        _metaRefreshChainCount = 0;
                        return;
                    }

                    _metaRefreshTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, delay) };
                    _metaRefreshTimer.Tick += (s, e) =>
                    {
                        _metaRefreshTimer?.Stop();
                        _isMetaRefreshNav = true;
                        NavigateTo(refreshUrl);
                    };
                    _metaRefreshTimer.Start();
                });
            }
        }
    }

    private static string DecodeBody(HttpSuccess success, string? overrideCharset = null) =>
        BodyDecoder.Decode(success.Body, success.Charset, overrideCharset);

    private static string ScanMetaCharset(string html) =>
        BodyDecoder.ScanMetaCharset(html) ?? "";

    private static (int DelayMs, string? Url) ParseMetaRefresh(string content, ParsedUrl baseUrl)
    {
        int semi = content.IndexOf(';');
        if (semi < 0)
        {
            return (int.TryParse(content.Trim(), out var d) && d > 0 && d < 600
                ? d * 1000 : -1, null);
        }

        string delayStr = content[..semi].Trim();
        string urlPart = content[(semi + 1)..].Trim();
        if (urlPart.StartsWith("url", StringComparison.OrdinalIgnoreCase))
        {
            int eq = urlPart.IndexOf('=');
            if (eq > 0) urlPart = urlPart[(eq + 1)..].Trim().Trim('"', '\'');
        }

        if (!int.TryParse(delayStr, out int delay) || delay < 0 || delay > 600)
            return (-1, null);

        try
        {
            return (delay * 1000, baseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                ? FileUrls.Resolve(baseUrl, urlPart)
                : baseUrl.Resolve(urlPart).ToAbsolute());
        }
        catch
        {
            return (-1, null);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scripting context
    // ─────────────────────────────────────────────────────────────────────

    private (DomDocument doc, JsInterpreter interp, DocumentBindingsState state)
        PrepareScripting(ParsedUrl url, string html)
    {
        _globalScope = new JsScope();
        JsRuntime.PopulateGlobalScope(_globalScope);
        DomBindings.RegisterEarlyGlobals(_globalScope, _canvas);   // parse-time alert() etc.

        // The certificate-error page's only button is a form whose
        // onsubmit calls window.acceptCertRisk() — with nothing bound
        // there, "Accept Risk and Continue" popped the confirm dialog and
        // then did nothing at all.
        if (_pendingCertRetryUrl != null)
        {
            string retryUrl = _pendingCertRetryUrl;
            int attempt = _certRetryCount;
            DomBindings.RegisterCertRiskHook(_globalScope, () =>
            {
                _pendingCertRetryUrl = null;
                if (attempt < 3)
                    BeginInvoke(() => NavigateTo(retryUrl));
            });
        }

        _jsInterpreter = new JsInterpreter(
            _globalScope,
            null,
            navUrl => BeginInvoke(() => NavigateTo(navUrl)),
            msg => BeginInvoke(() => _statusLabel.Text = msg),
            BrowserRuntime.JavaScriptMaxExecutionMilliseconds,
            BrowserRuntime.JavaScriptMemoryLimitBytes,
            BrowserRuntime.JavaScriptMaxCallDepth);

        _jsInterpreter.ConsoleMessage += entry =>
            PageInspector.PublishConsole(entry.Level, entry.Message, entry.Timestamp);

        _jsState = new DocumentBindingsState
        {
            Interpreter = _jsInterpreter,
            Canvas = _canvas,
            LastModified = "",
            Referrer = "",
            EmbeddedScriptInfoResolver = _canvas.EmbeddedScriptInfoResolver,
            EmbeddedScriptCall = _canvas.EmbeddedScriptCall,
            JavaAppletScriptMemberResolver = _canvas.ResolveJavaAppletScriptMember,
            JavaAppletScriptMemberSetter = _canvas.SetJavaAppletScriptMember
        };
        _jsInterpreter.ElementWrapperHook =
            e => DomBindings.WrapElement(e, _jsState);

        // Runtime builtins (setTimeout/setInterval/eval/call/apply) used to
        // be installed only AFTER the parse (RegisterBindings), so a script
        // that scheduled anything while the page streamed in got
        // "'setTimeout' is not a function". Idempotent — re-registering
        // later merely overwrites the same slots.
        _jsInterpreter.RegisterRuntimeBuiltins();

        var document = HtmlParser.Parse(html, url, _cookieStore,
            BrowserRuntime.ScriptingEnabled
                ? (doc, src, isVbScript) => RunInlineScript(doc, src, isVbScript, _jsInterpreter!, _jsState!)
                : null,
            BrowserRuntime.ScriptingEnabled && BrowserRuntime.ExternalScriptsEnabled
                ? LoadExternalScript : null);

        return (document, _jsInterpreter, _jsState);
    }

    private string? LoadExternalScript(DomDocument document, string sourceUrl)
    {
        if (_resourceLoader == null || document.BaseUrl == null) return null;
        try
        {
            // The HTML parser is intentionally synchronous because classic
            // script execution blocks tokenization at its source position.
            // Await the asynchronous, deduplicated resource loader from a
            // worker so a WinForms synchronization context cannot deadlock.
            var task = Task.Run(() => _resourceLoader.FetchAsync(
                sourceUrl, document.BaseUrl, _cookieStore));
            var result = task.GetAwaiter().GetResult();
            return result is HttpSuccess ok ? DecodeBody(ok) : null;
        }
        catch { return null; }
    }

    private string RunInlineScript(DomDocument document, string scriptSource, bool isVbScript,
                                  JsInterpreter interpreter,
                                  DocumentBindingsState state)
    {
        state.Document = document;
        if (isVbScript)
            return BrowserRuntime.VbScriptEnabled
                ? _canvas.RunVbsScript(document, scriptSource, interpreter)
                : "";
        if (!BrowserRuntime.JavaScriptEnabled) return "";

        // Full DOM-0 bindings at PARSE time. The old minimal document
        // (write/writeln/title/URL only, no navigator!) meant the very
        // first line of the era's standard sniffing preamble —
        //     var ua = navigator.userAgent;
        // — threw "'navigator' is not defined", the script aborted, the
        // document.write() output never streamed, and pages like the
        // 1996 Browser Detector sat forever on "Detecting...".
        // Rebuilding the bindings before EVERY script also refreshes the
        // live collections (forms/images/links) to include everything
        // parsed so far — period-accurate: a script at the bottom of the
        // body sees the form above it. document.write still routes to
        // the shared state buffer the parser splices.
        DomBindings.RegisterAll(_globalScope!, document, _history, _canvas, state);
        interpreter.RegisterRuntimeBuiltins();

        try
        {
            interpreter.ExecuteString(scriptSource);
        }
        catch (Exception ex)
        {
            PageInspector.PublishConsole("error", $"Script error: {ex.Message}", DateTime.Now);
            BeginInvoke(() => _statusLabel.Text = $"Script error: {ex.Message}");
        }

        string written = state.WriteBuffer.ToString();
        state.WriteBuffer.Clear();
        return written;
    }

    private void RegisterBindings(DomDocument document, JsInterpreter interpreter,
                                  DocumentBindingsState state)
    {
        state.Document = document;
        DomBindings.RegisterAll(_globalScope!, document, _history, _canvas, state);
        interpreter.RegisterRuntimeBuiltins();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Stylesheets & images
    // ─────────────────────────────────────────────────────────────────────

    private async Task FetchStylesheetsAsync(DomDocument document, ParsedUrl baseUrl,
                                             CancellationToken ct)
    {
        ParsedUrl importBase = document.BaseUrl ?? baseUrl;
        foreach (var styleElem in document.ElementDescendants()
                     .Where(e => e.TagName == "style"))
        {
            foreach (var text in styleElem.Children.OfType<DomText>())
            {
                if (string.IsNullOrWhiteSpace(text.Data)) continue;
                text.Data = await ExpandCssImportsAsync(text.Data, importBase, ct, 0,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        var links = document.ElementDescendants()
            .Where(e => e.TagName == "link" &&
                        e.GetAttr("rel")?.Contains("stylesheet",
                            StringComparison.OrdinalIgnoreCase) == true &&
                        e.HasAttr("href"))
            .ToList();

        foreach (var link in links)
        {
            string href = link.GetAttr("href")!;
            try
            {
                // Local pages (file:// base) read their stylesheets from
                // disk — the HTTP fetcher knows nothing about files.
                if (baseUrl.Scheme == "file")
                {
                    string abs = ImageCache.ResolveUrl(href, baseUrl.ToAbsolute());
                    var localParsed = ParsedUrl.Parse(abs);
                    var localPath = LocalPathFromFileUrl(localParsed);
                    if (localPath != null && File.Exists(localPath))
                    {
                        string cssText = await File.ReadAllTextAsync(localPath);
                        cssText = await ExpandCssImportsAsync(cssText, localParsed, ct, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        var styleElem = new DomElement("style");
                        styleElem.AppendChild(new DomText { Data = cssText });
                        document.AppendChild(styleElem);
                    }
                    continue;
                }

                var res = await _resourceLoader!.FetchAsync(href, baseUrl, _cookieStore);
                if (res is HttpSuccess css)
                {
                    string cssText = DecodeBody(css);
                    cssText = await ExpandCssImportsAsync(cssText, baseUrl, ct, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    var styleElem = new DomElement("style");
                    styleElem.AppendChild(new DomText { Data = cssText });
                    document.AppendChild(styleElem);
                }
            }
            catch { }
        }
    }

    private async Task<string> ExpandCssImportsAsync(string cssText, ParsedUrl baseUrl,
        CancellationToken ct, int depth, HashSet<string> visited)
    {
        if (depth >= 8 || string.IsNullOrWhiteSpace(cssText)) return cssText;
        var (_, imports) = CssParser.Parse(cssText);
        if (imports.Count == 0) return cssText;

        var prefix = new StringBuilder();
        foreach (var import in imports.Take(32))
        {
            try
            {
                string abs = baseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? FileUrls.Resolve(baseUrl, import.Url)
                    : baseUrl.Resolve(import.Url).ToAbsolute();
                if (!visited.Add(abs)) continue;

                string importedText;
                var parsed = ParsedUrl.Parse(abs);
                if (parsed.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    string? path = LocalPathFromFileUrl(parsed);
                    if (path == null || !File.Exists(path)) continue;
                    importedText = await File.ReadAllTextAsync(path, ct);
                }
                else if (parsed.IsHttp)
                {
                    var result = await _resourceLoader!.FetchAsync(abs, baseUrl, _cookieStore);
                    if (result is not HttpSuccess imported) continue;
                    importedText = DecodeBody(imported);
                }
                else continue;

                prefix.AppendLine(await ExpandCssImportsAsync(importedText, parsed, ct, depth + 1, visited));
            }
            catch { /* one bad import must not discard the parent stylesheet */ }
        }
        return prefix.AppendLine(cssText).ToString();
    }

    private async Task PrefetchImagesAsync(DomDocument doc, ParsedUrl baseUrl,
                                           CancellationToken ct, bool reflowWhenLoaded,
                                           long myGeneration, JsInterpreter? js = null)
    {
        if (!BrowserRuntime.ImagesEnabled) return;
        _imageCache.HostOpenedLocalPage = _hostOpenedLocalDocument;
        var urls = new List<string>();

        foreach (var elem in doc.ElementDescendants())
        {
            string? raw = elem.TagName switch
            {
                "img" => elem.GetAttr("src") ?? elem.GetAttr("lowsrc"),
                "body" => elem.GetAttr("background"),
                _ => null
            };
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    urls.Add(ImageCache.ResolveUrl(raw, baseUrl.ToAbsolute()));
                }
                catch { }
            }

            var bgCss = elem.Style?.BackgroundImage;
            if (!string.IsNullOrEmpty(bgCss) && bgCss != "none")
            {
                var bgUrl = Renderer.ParseCssUrl(bgCss);
                if (!string.IsNullOrEmpty(bgUrl))
                {
                    try { urls.Add(baseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                            ? FileUrls.Resolve(baseUrl, bgUrl)
                            : baseUrl.Resolve(bgUrl).ToAbsolute()); } catch { }
                }
            }
        }

        var distinct = urls.Distinct().ToList();

        // PARALLEL fetching — the old sequential loop let one slow/hung
        // image block every image after it (the "middle images don't
        // load" symptom: background + first image arrive, the queue
        // stalls, everything after shows the broken icon).
        var fetches = new List<Task>();
        foreach (var absoluteUrl in distinct)
            fetches.Add(FetchOneImageAsync(absoluteUrl, ct, myGeneration));
        await Task.WhenAll(fetches);

        if (js != null)
        {
            foreach (var elem in doc.ElementDescendants().Where(e => e.TagName == "img"))
            {
                string? raw = elem.GetAttr("src") ?? elem.GetAttr("lowsrc");
                if (string.IsNullOrEmpty(raw)) continue;
                try
                {
                    string abs = ImageCache.ResolveUrl(raw, baseUrl.ToAbsolute());
                    if (_imageCache.TryGetCached(abs, out var image) && image.Frames.Count > 0)
                        js.FireEvent(elem, "onload");
                    else
                        js.FireEvent(elem, "onerror");
                }
                catch { js.FireEvent(elem, "onerror"); }
            }
        }

        if (reflowWhenLoaded && distinct.Count > 0)
        {
            foreach (var elem in doc.ElementDescendants().Where(e => e.TagName == "img"))
            {
                string? src = elem.GetAttr("src");
                if (string.IsNullOrEmpty(src)) continue;
                string abs;
                try { abs = ImageCache.ResolveUrl(src, baseUrl.ToAbsolute()); }
                catch { continue; }

                if (!elem.HasAttr("width") && !elem.HasAttr("height"))
                {
                    try
                    {
                        if (_imageCache.IsLoaded(abs) && !_imageCache.IsBroken(abs))
                        {
                            var frame = _imageCache.GetCurrentFrame(abs);
                            if (frame != null)
                            {
                                elem.SetAttr("width", frame.Width.ToString());
                                elem.SetAttr("height", frame.Height.ToString());
                            }
                        }
                        else if (_imageCache.IsLoaded(abs))
                        {
                            // Failed to load.  Leaving the 32x32 placeholder in
                            // place made ONE bad bullet.gif inflate every row of
                            // a ~150-row link table (802px vs 527px) — the
                            // "too much space between the links" bug.  Collapse
                            // to the broken-icon size so layout stays compact;
                            // the renderer still paints the icon + ALT text.
                            elem.SetAttr("width", "16");
                            elem.SetAttr("height", "16");
                        }
                    }
                    catch { }
                }
            }

            Size canvasSize = GetCanvasSize();
            var newRoot = LayoutEngineApi.BuildLayoutTree(doc, canvasSize.Width, canvasSize.Height);

            BeginInvoke(() =>
            {
                if (myGeneration != _navGeneration) return;
                _canvas.ApplyRelayout(doc, newRoot);
            });
        }
    }

    private async Task FetchOneImageAsync(string absoluteUrl, CancellationToken ct, long myGeneration)
    {
        if (!BrowserRuntime.ImagesEnabled) return;
        try
        {
            await _imageCache.GetAsync(absoluteUrl, _resourceLoader!, ct);
            BeginInvoke(() =>
            {
                if (myGeneration != _navGeneration) return;
                _canvas.RequestRerender();
            });
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Frames
    // ─────────────────────────────────────────────────────────────────────

    private async Task LoadFramesAsync(DomDocument document, LayoutBox rootBox)
    {
        long gen = _navGeneration;
        await LoadFrameLevelAsync(document, rootBox, parentView: null, gen);
    }

    /// <summary>
    /// Loads every frame/iframe of one document level.  Recurses into each
    /// loaded frame's own document, so nested frames (an iframe inside a
    /// frame's page) load too — they used to show only the sunken
    /// placeholder forever because only the TOP document's frames were
    /// ever walked.
    /// </summary>
    private static BrowserCanvas.FrameScrollMode ParseFrameScrollMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "yes" => BrowserCanvas.FrameScrollMode.Yes,
            "no" => BrowserCanvas.FrameScrollMode.No,
            _ => BrowserCanvas.FrameScrollMode.Auto
        };

    private static FrameContent ApplyFramePresentation(DomElement frameElem, FrameContent content,
                                                        int frameW, int frameH)
    {
        var body = content.Document.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        if (body != null)
        {
            int mw = frameElem.GetAttrInt("marginwidth", -1);
            int mh = frameElem.GetAttrInt("marginheight", -1);
            if (mw >= 0) body.SetAttr("marginwidth", mw.ToString());
            if (mh >= 0) body.SetAttr("marginheight", mh.ToString());
        }

        StyleResolver.Resolve(content.Document, Math.Max(1, frameW));
        var root = LayoutEngineApi.BuildLayoutTree(content.Document,
            Math.Max(1, frameW), Math.Max(1, frameH));
        return new FrameContent(content.Document, root, content.AbsoluteUrl);
    }

    private async Task LoadFrameLevelAsync(DomDocument document, LayoutBox rootBox,
                                           BrowserCanvas.FrameView? parentView, long gen)
    {
        var frameBoxes = rootBox.Descendants()
            .Where(b => b.BoxType == BoxType.Frame && b.Element != null)
            .ToList();
        if (frameBoxes.Count == 0) return;

        string baseUrl = document.BaseUrl?.ToAbsolute() ?? "";

        foreach (var frameBox in frameBoxes)
        {
            var frameElem = frameBox.Element!;
            string? src = frameElem.GetAttr("src");
            if (string.IsNullOrEmpty(src)) continue;

            var blankDoc = HtmlParser.Parse("<html><body></body></html>",
                ParsedUrl.Parse("about:blank"), _cookieStore);
            var view = new BrowserCanvas.FrameView
            {
                Document = blankDoc,
                RootBox = LayoutEngineApi.BuildLayoutTree(
                    blankDoc, (int)frameBox.Width, (int)frameBox.Height),
                Name = frameElem.GetAttr("name") ?? "",
                Url = "",
                ScrollMode = ParseFrameScrollMode(frameElem.GetAttrOrDefault("scrolling", "auto")),
                ScrollingEnabled = !frameElem.GetAttrOrDefault("scrolling", "auto")
                    .Equals("no", StringComparison.OrdinalIgnoreCase),
                FrameBorder = frameElem.GetAttrOrDefault("frameborder", "1") != "0",
                NoResize = frameElem.HasAttr("noresize"),
                MarginWidth = frameElem.GetAttrInt("marginwidth", -1),
                MarginHeight = frameElem.GetAttrInt("marginheight", -1)
            };

            // Per-frame scripting context, created BEFORE the load so
            // parse-time scripts (the document.write streaming pattern)
            // execute exactly like they do for the top-level document —
            // frames used to be parsed with NO scripting at all.
            var (frameInterpreter, frameState) = CreateFrameContext(view);

            try
            {
                // Show the sunken frame immediately — the chat-style pages
                // (IRC #theoldnet: <iframe src="https://webchat.oftc.net/…">)
                // used to paint NOTHING here: the frame only appeared after
                // the fetch returned, and a modern TLS endpoint that hangs
                // left the centred chat area blank forever ("that centre
                // thing doesn't load at all").  A frame nested inside
                // another frame's document registers against its PARENT
                // frame (its box coordinates are frame-local).
                if (parentView == null)
                    _canvas.SetFrame(frameBox, view);
                else
                    _canvas.AddChildFrame(parentView, frameBox, view);

                // A hard deadline on the whole frame fetch — the socket
                // timeouts only cover connect/read, not a stalled TLS
                // handshake against a modern CDN.
                using var frameCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var content = await FrameLoader.LoadAsync(
                    baseUrl, src,
                    (int)frameBox.Width, (int)frameBox.Height,
                    _httpClient, _cookieStore, frameCts.Token,
                    BrowserRuntime.ScriptingEnabled
                        ? (fdoc, scriptSrc, isVbScript) => RunFrameScript(fdoc, scriptSrc, isVbScript, frameInterpreter, frameState)
                        : null,
                    BrowserRuntime.ScriptingEnabled && BrowserRuntime.ExternalScriptsEnabled
                        ? LoadExternalScript : null);
                if (gen != _navGeneration) return;

                if (content != null)
                {
                    content = ApplyFramePresentation(frameElem, content,
                        (int)frameBox.Width, (int)frameBox.Height);
                    ApplyFrameContent(view, content, frameInterpreter, frameState);
                    if (parentView == null)
                        SetFrameOnLiveBox(frameElem, frameBox, view);
                    else
                        _canvas.RefreshChildFrame(parentView, frameBox, view);

                    // Fetch the frame's images, then reflow the frame layout
                    // at real image sizes — frame images used to stay at the
                    // 32×32 placeholder size forever because only the MAIN
                    // page reflowed after its prefetch.
                    _ = LoadFrameImagesThenReflowAsync(view, content, gen,
                        parentView, frameElem, frameBox);

                    // Frames nested inside THIS frame's document.
                    await LoadFrameLevelAsync(content.Document, content.RootBox,
                        view, gen);
                }
                else
                {
                    // about:blank — the blank view is already correct.
                    if (parentView == null)
                        SetFrameOnLiveBox(frameElem, frameBox, view);
                }
            }
            catch
            {
                // FrameLoader never throws, but a failure between SetFrame
                // calls must not kill the remaining frames of the page.
                if (gen != _navGeneration) return;
                if (parentView == null)
                    SetFrameOnLiveBox(frameElem, frameBox, view);
            }
        }
    }

    /// <summary>
    /// Creates the per-frame JS context (scope, interpreter, DOM state).
    /// JS navigation inside a frame targets the frame itself, through the
    /// existing frame-navigation path.
    /// </summary>
    private (JsInterpreter interpreter, DocumentBindingsState state)
        CreateFrameContext(BrowserCanvas.FrameView view)
    {
        var frameScope = new JsScope();
        JsRuntime.PopulateGlobalScope(frameScope);
        DomBindings.RegisterEarlyGlobals(frameScope, _canvas);

        var interpreter = new JsInterpreter(
            frameScope,
            null,
            navUrl => BeginInvoke(() => _ = LoadFrameAsync(view, navUrl)),
            msg => BeginInvoke(() => _statusLabel.Text = msg),
            BrowserRuntime.JavaScriptMaxExecutionMilliseconds,
            BrowserRuntime.JavaScriptMemoryLimitBytes,
            BrowserRuntime.JavaScriptMaxCallDepth);

        interpreter.ConsoleMessage += entry =>
            PageInspector.PublishConsole(entry.Level, entry.Message, entry.Timestamp);

        var state = new DocumentBindingsState
        {
            Interpreter = interpreter,
            Canvas = _canvas,
            LastModified = "",
            Referrer = "",
            EmbeddedScriptInfoResolver = _canvas.EmbeddedScriptInfoResolver,
            EmbeddedScriptCall = _canvas.EmbeddedScriptCall,
            JavaAppletScriptMemberResolver = _canvas.ResolveJavaAppletScriptMember,
            JavaAppletScriptMemberSetter = _canvas.SetJavaAppletScriptMember
        };
        interpreter.ElementWrapperHook = e => DomBindings.WrapElement(e, state);
        interpreter.RegisterRuntimeBuiltins();

        return (interpreter, state);
    }

    /// <summary>
    /// Parse-time script executor for frame documents — the frame twin of
    /// RunInlineScript: full DOM-0 bindings refreshed before EVERY script
    /// (period-accurate: a script at the bottom of the frame sees the form
    /// above it), document.write routed to the shared state buffer the
    /// parser splices.
    /// </summary>
    private string RunFrameScript(DomDocument document, string scriptSource, bool isVbScript,
                                  JsInterpreter interpreter,
                                  DocumentBindingsState state)
    {
        state.Document = document;
        if (isVbScript)
            return BrowserRuntime.VbScriptEnabled
                ? _canvas.RunVbsScript(document, scriptSource, interpreter)
                : "";
        if (!BrowserRuntime.JavaScriptEnabled) return "";
        DomBindings.RegisterAll((JsScope)interpreter.GlobalScope!, document,
            new NavigationHistory(), _canvas, state);
        interpreter.RegisterRuntimeBuiltins();

        try { interpreter.ExecuteString(scriptSource); }
        catch (Exception ex)
        {
            PageInspector.PublishConsole("error", $"Script error: {ex.Message}", DateTime.Now);
            BeginInvoke(() => _statusLabel.Text = $"Script error: {ex.Message}");
        }

        string written = state.WriteBuffer.ToString();
        state.WriteBuffer.Clear();
        return written;
    }

    /// <summary>
    /// Installs loaded content into a frame view: visited-link colours,
    /// the frame's interpreter hookup (timers), and the window/element
    /// onload pass — the same pass the headless rig runs for frames.
    /// </summary>
    private void ApplyFrameContent(BrowserCanvas.FrameView view, FrameContent content,
                                   JsInterpreter frameInterpreter,
                                   DocumentBindingsState frameState)
    {
        content.Document.VisitedUrls.UnionWith(_visitedUrls);
        // Replacing a frame document invalidates every nested iframe/frameset
        // view that belonged to the old document. Dispose the old frame tree
        // before the new document is installed so no stale GPU composition
        // survives the navigation.
        _canvas.ClearChildFrames(view);
        _canvas.StopJavaAppletsForDocument(view.Document);
        view.Document = content.Document;
        view.RootBox = content.RootBox;
        view.Url = content.AbsoluteUrl;

        // Full DOM-0 bindings for the frame document.
        frameState.Document = content.Document;
        DomBindings.RegisterAll((JsScope)frameInterpreter.GlobalScope!,
            content.Document, new NavigationHistory(), _canvas, frameState);
        frameInterpreter.RegisterRuntimeBuiltins();

        // Timers scheduled by frame scripts are driven by the canvas's
        // JS timer tick.
        view.Interpreter = frameInterpreter;
        _canvas.PrepareJavaAppletsAsync(content.Document, frameInterpreter);

        // window.onload + element onload.
        try
        {
            var fwin = frameState.WindowObject;
            if (fwin != null && fwin.Get("onload") is { Type: JsType.Function } fol)
                frameInterpreter.CallHandler(fol, Retro96.Engine.Js.JsValue.FromObject(fwin));
            if (BrowserRuntime.JavaScriptEnabled)
            {
                foreach (var elem in content.Document.ElementDescendants()
                             .Where(e => e.EventHandlers.ContainsKey("onload")))
                    frameInterpreter.FireEvent(elem, "onload");
            }
        }
        catch (Exception ex)
        {
            BeginInvoke(() => _statusLabel.Text = $"Script error: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-finds the frame's CURRENT layout box before painting content: an
    /// image-load relayout between the fetch starting and finishing can
    /// have replaced the tree the box came from, and SetFrame keyed on the
    /// dead box would paint the frame at a stale rect.
    /// </summary>
    private void SetFrameOnLiveBox(DomElement frameElem, LayoutBox originalBox,
                                   BrowserCanvas.FrameView view)
    {
        var live = _canvas.FindFrameBox(frameElem) ?? originalBox;
        _canvas.SetFrame(live, view);
    }

    /// <summary>The layout box currently displaying a frame view.</summary>
    private LayoutBox? FindBoxForView(BrowserCanvas.FrameView view) =>
        _canvas.TryFindFrameHost(view, out _, out var box) ? box : null;

    /// <summary>
    /// Fetches a frame's images, then rebuilds the frame layout with real
    /// image sizes — the per-frame twin of the main page's post-prefetch
    /// reflow (images without WIDTH/HEIGHT used to stay 32×32 in frames
    /// forever; broken ones collapsed instead to the compact 16×16 icon).
    /// Works for top-level frames (page frame map) and frames nested inside
    /// another frame's document (parent-relative composition).
    /// </summary>
    private async Task LoadFrameImagesThenReflowAsync(
        BrowserCanvas.FrameView view, FrameContent content, long gen,
        BrowserCanvas.FrameView? parentView, DomElement frameElem, LayoutBox originalBox)
    {
        var baseUrl = ParsedUrl.Parse(content.AbsoluteUrl);

        await PrefetchImagesAsync(content.Document, baseUrl,
            CancellationToken.None, reflowWhenLoaded: false, gen);
        if (gen != _navGeneration) return;

        bool changed = false;
        foreach (var elem in content.Document.ElementDescendants()
                     .Where(e => e.TagName == "img"))
        {
            string? src = elem.GetAttr("src");
            if (string.IsNullOrEmpty(src)) continue;
            string abs;
            try { abs = ImageCache.ResolveUrl(src, baseUrl.ToAbsolute()); }
            catch { continue; }

            if (elem.HasAttr("width") || elem.HasAttr("height")) continue;

            try
            {
                if (_imageCache.IsLoaded(abs) && !_imageCache.IsBroken(abs))
                {
                    var frame = _imageCache.GetCurrentFrame(abs);
                    if (frame != null)
                    {
                        elem.SetAttr("width", frame.Width.ToString());
                        elem.SetAttr("height", frame.Height.ToString());
                        changed = true;
                    }
                }
                else if (_imageCache.IsLoaded(abs))
                {
                    // Broken image — collapse to the broken-icon size so
                    // layout stays compact (same rule as the main page).
                    elem.SetAttr("width", "16");
                    elem.SetAttr("height", "16");
                    changed = true;
                }
            }
            catch { }
        }

        if (!changed)
        {
            // Images with explicit WIDTH/HEIGHT do not trigger a relayout,
            // but they still arrive after the first frame bitmap was painted.
            // Refresh the frame anyway so the decoded pixels replace the
            // initial blank/broken image.
            if (parentView != null)
            {
                var liveChild = parentView.ChildFrames
                    .FirstOrDefault(f => f.Box.Element == frameElem);
                if (liveChild.Box != null)
                    _canvas.RefreshChildFrame(parentView, liveChild.Box, view);
            }
            else
            {
                var live = _canvas.FindFrameBox(frameElem) ?? originalBox;
                _canvas.SetFrame(live, view);
            }
            return;
        }

        // Rebuild the frame layout at its CURRENT box size and repaint.
        if (parentView != null)
        {
            // Nested frame: re-find its current box inside the parent's
            // (possibly re-laid-out) tree, then recompose into the parent.
            var liveChild = parentView.ChildFrames
                .FirstOrDefault(f => f.Box.Element == frameElem);
            if (liveChild.Box == null) return;
            view.RootBox = LayoutEngineApi.BuildLayoutTree(content.Document,
                (int)liveChild.Box.Width, (int)liveChild.Box.Height);
            _canvas.RefreshChildFrame(parentView, liveChild.Box, view);
        }
        else
        {
            var live = _canvas.FindFrameBox(frameElem) ?? originalBox;
            view.RootBox = LayoutEngineApi.BuildLayoutTree(content.Document,
                (int)live.Width, (int)live.Height);
            _canvas.SetFrame(live, view);
        }
    }

    private async Task LoadFrameAsync(BrowserCanvas.FrameView view, string url,
                                      string? postData = null)
    {
        long gen = _navGeneration;
        try
        {
            if (!_canvas.TryFindFrameHost(view, out var parentView, out var frameBox) || frameBox == null)
                return;

            var (interpreter, state) = CreateFrameContext(view);
            int frameW = Math.Max(1, (int)frameBox.Width);
            int frameH = Math.Max(1, (int)frameBox.Height);
            string baseUrl = string.IsNullOrWhiteSpace(view.Url) ? url : view.Url;

            FrameContent? content = null;
            if (postData == null)
            {
                using var frameCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                content = await FrameLoader.LoadAsync(
                    baseUrl, url, frameW, frameH,
                    _httpClient, _cookieStore, frameCts.Token,
                    BrowserRuntime.ScriptingEnabled
                        ? (fdoc, scriptSrc, isVbScript) => RunFrameScript(fdoc, scriptSrc, isVbScript, interpreter, state)
                        : null,
                    BrowserRuntime.ScriptingEnabled && BrowserRuntime.ExternalScriptsEnabled
                        ? LoadExternalScript : null);
            }
            else
            {
                var parsed = ParsedUrl.Parse(url);
                var result = await _httpClient.PostAsync(parsed, postData,
                    _cookieStore, CancellationToken.None);
                if (gen != _navGeneration) return;

                if (result is HttpSuccess s && s.StatusCode == 200)
                {
                    string html = DecodeBody(s);
                    var doc = HtmlParser.Parse(html, parsed, _cookieStore,
                        BrowserRuntime.ScriptingEnabled
                            ? (fdoc, scriptSrc, isVbScript) => RunFrameScript(fdoc, scriptSrc, isVbScript, interpreter, state)
                            : null,
                        BrowserRuntime.ScriptingEnabled && BrowserRuntime.ExternalScriptsEnabled
                            ? LoadExternalScript : null);
                    if (BrowserRuntime.StylesheetsEnabled)
                        await FetchStylesheetsAsync(doc, parsed, CancellationToken.None);
                    doc.VisitedUrls.UnionWith(_visitedUrls);
                    StyleResolver.Resolve(doc, Math.Max(1, frameW));
                    var root = LayoutEngineApi.BuildLayoutTree(doc, frameW, frameH);
                    content = new FrameContent(doc, root, s.EffectiveUrl.Length > 0 ? s.EffectiveUrl : url);
                }
            }

            if (gen != _navGeneration || content == null)
            {
                if (gen == _navGeneration)
                    DebugLog.Write($"Frame navigation produced no content: {url}");
                return;
            }

            var frameElem = frameBox.Element;
            if (frameElem != null)
                content = ApplyFramePresentation(frameElem, content, frameW, frameH);

            view.Scroll = Retro96.Drawing.PointF.Empty;
            ApplyFrameContent(view, content, interpreter, state);

            // Re-render the actual host. Nested frames cannot be found in the
            // top-level _frames dictionary; route through the parent view so
            // target="main" and links inside nested frames keep their content.
            if (parentView == null)
                _canvas.SetFrame(frameBox, view);
            else
                _canvas.RefreshChildFrame(parentView, frameBox, view);

            if (frameElem != null)
            {
                _ = LoadFrameImagesThenReflowAsync(view, content, gen,
                    parentView, frameElem, frameBox);
            }

            // A navigated frame may itself contain a frameset or iframe. The
            // initial top-level load already recursed through this path;
            // navigations must do the same or pages such as 1996-corporate
            // and frames-main#geometry appear blank inside the target frame.
            await LoadFrameLevelAsync(content.Document, content.RootBox, view, gen);

            // Targeted frame navigation may include a fragment, e.g.
            // frames-main.html#geometry or #help.  The frame document itself
            // must finish loading (including nested iframes) before the
            // fragment can be scrolled into view.
            try
            {
                string fragment = ParsedUrl.Parse(view.Url).Fragment;
                if (!string.IsNullOrEmpty(fragment))
                    _canvas.ScrollFrameToAnchor(view, fragment);
            }
            catch { }
        }
        catch (Exception ex)
        {
            DebugLog.WriteException("LoadFrameAsync", ex);
            if (gen == _navGeneration)
                BeginInvoke(() => _statusLabel.Text = $"Frame navigation failed: {ex.Message}");
        }
    }

    private void OpenExternalProtocol(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Unable to open external link: {ex.Message}";
        }
    }

    private void OnFrameNavigationRequested(
        (BrowserCanvas Canvas, BrowserCanvas.FrameView Frame, string Url) nav)
        => _ = LoadFrameAsync(nav.Frame, nav.Url);

    // ─────────────────────────────────────────────────────────────────────
    // Page display / history
    // ─────────────────────────────────────────────────────────────────────

    private Size GetCanvasSize() =>
        InvokeRequired ? (Size)Invoke(() => _canvas.GetLayoutViewportSize())
                       : _canvas.GetLayoutViewportSize();

    private void UpdatePage(DomDocument document, LayoutBox rootBox, string url,
                            HistoryEntry entry)
    {
        DebugLog.Write($"UpdatePage APPLIED gen={_navGeneration} url='{url}' title='{document.Title}' rootBoxChildren={rootBox.Children?.Count ?? -1}");
        _currentPageUrl = url;
        _pluginManager?.RaiseNavigation(url);

        if (_history.Current != url)
            _history.Push(entry);

        _historyStore.Record(document.Title, url);
        BuildHistoryMenu();
        BuildBookmarksMenu();
        UpdateNavigationButtons();

        _canvas.SetPage(document, rootBox, _jsInterpreter!, _fontCache, _imageCache);
        _canvas.ReRenderPage(_fontCache, _imageCache, _resourceLoader!);

        Text = (document.Title.Length > 0 ? document.Title : "Untitled") + " — Retro96";
        _pluginManager?.RaiseTitleChanged(url, document.Title);
        _txtUrl.Text = url;
        _statusLabel.Text = "Done";
        Cursor = Cursors.Default;
        _btnStop.Enabled = false;
        _isLoading = false;
        _throbberBox.Invalidate();
        _pluginManager?.RaisePageLoaded(url, PluginCurrentTitle);
        _ = StartEmbeddedMidiAsync(document, ParsedUrl.Parse(url), _loadCts?.Token ?? CancellationToken.None, _navGeneration);

        int hash = url.IndexOf('#');
        if (hash >= 0 && hash + 1 < url.Length)
            _canvas.ScrollToAnchor(url[(hash + 1)..]);
    }

    public void NavigateBack()
    {
        if (_history.NextBackwardIsPost &&
            MessageBox.Show(
                "This page was produced by a form submission.  Going back " +
                "will send the form data again.",
                "Retro96",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        var entry = _history.Peek(-1);
        if (entry != null)
            _ = NavigateAsync(entry.Url, entry.PostData);
    }

    public void NavigateForward()
    {
        if (_history.NextForwardIsPost &&
            MessageBox.Show(
                "This page was produced by a form submission.  Going forward " +
                "will send the form data again.",
                "Retro96",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        var entry = _history.Peek(1);
        if (entry != null)
            _ = NavigateAsync(entry.Url, entry.PostData);
    }

    public void Reload()
    {
        var entry = _history.CurrentEntry;
        if (entry != null)
            _ = NavigateAsync(entry.Url, entry.PostData);
    }

    private void UpdateNavigationButtons()
    {
        _btnBack.Enabled = _history.CanGoBack;
        _btnForward.Enabled = _history.CanGoForward;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Errors (rendered as era pages)
    // ─────────────────────────────────────────────────────────────────────

    private void ApplyPluginPageStyle(DomDocument document)
    {
        string css = _pluginManager?.BuildPageStyleCss() ?? string.Empty;
        if (css.Length == 0) return;
        var style = new DomElement("style");
        style.AppendChild(new DomText(css));
        var head = document.ElementDescendants().FirstOrDefault(x => x.TagName == "head");
        if (head != null) head.AppendChild(style);
        else document.AppendChild(style);
    }

    private async Task RenderPluginProtocolResponseAsync(string url, PluginProtocolResponse response, bool replaceHistory, long generation)
    {
        string contentType = (response.ContentType ?? "text/plain").Split(';', 2)[0].Trim().ToLowerInvariant();
        if (response.StatusCode >= 400)
        {
            await RenderErrorAsync(ErrorPage.GenericHttpError(response.StatusCode, url), generation);
            return;
        }
        if (contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            string dataUrl = "data:" + contentType + ";base64," + Convert.ToBase64String(response.Body);
            string html = "<html><head><title>" + EscapeHtmlText(url) + "</title></head><body bgcolor=\"#c0c0c0\"><img src=\"" + EscapeHtmlText(dataUrl) + "\"></body></html>";
            await RenderHtmlAsync(html, url, replaceHistory, generation);
            return;
        }
        if (contentType == "text/html" || contentType.EndsWith("+html", StringComparison.Ordinal))
        {
            string html = BodyDecoder.Decode(response.Body, response.Charset);
            await RenderHtmlAsync(html, url, replaceHistory, generation);
            return;
        }
        if (contentType.StartsWith("text/", StringComparison.Ordinal) || contentType is "application/json" or "application/xml")
        {
            string text = EscapeHtmlText(BodyDecoder.Decode(response.Body, response.Charset));
            await RenderHtmlAsync("<html><head><title>" + EscapeHtmlText(url) + "</title></head><body><pre>" + text + "</pre></body></html>", url, replaceHistory, generation);
            return;
        }
        await RenderErrorAsync(ErrorPage.NetworkError(url, $"Protocol '{contentType}' returned content that Retro96 cannot render inline."), generation);
    }

    private async Task RenderErrorAsync(string html, long? generation = null)
    {
        await RenderHtmlAsync(html, _txtUrl.Text, replaceHistory: false, generation);
    }

    private async Task RenderHtmlAsync(string html, string url, bool replaceHistory,
                                       long? generation = null)
    {
        long myGeneration = generation ?? ++_navGeneration;

        DebugLog.Write($"RenderHtmlAsync ENTER gen={myGeneration} url='{url}' htmlLen={html.Length}");
        try
        {
            var baseUrl = ParsedUrl.Parse(url);

            var (document, interpreter, state) = PrepareScripting(baseUrl, html);

            // Local pages (file:// base) load their <link rel=stylesheet>
            // stylesheets here — the fetcher's own file:// branch used to
            // be unreachable from this render path (only the HTTP success
            // path called it), so a local page's CSS silently vanished.
            if (BrowserRuntime.StylesheetsEnabled)
                await FetchStylesheetsAsync(document, baseUrl, CancellationToken.None);
            ApplyPluginPageStyle(document);

            // The visited session store must be copied onto the document
            // BEFORE CSS matching. Resolving first meant a:visited saw an
            // empty history on local/file pages, even after a link had been
            // followed and the page reloaded.
            document.VisitedUrls.UnionWith(_visitedUrls);
            StyleResolver.Resolve(document);

            Size sz = GetCanvasSize();
            var root = LayoutEngineApi.BuildLayoutTree(document, sz.Width, sz.Height);
            RegisterBindings(document, interpreter, state);

            BeginInvoke(() =>
            {
                if (myGeneration != _navGeneration)
                {
                    DebugLog.Write($"RenderHtmlAsync gen={myGeneration} STALE (current={_navGeneration}) — dropped");
                    return;
                }
                try
                {
                    UpdatePage(document, root, url, new HistoryEntry(url));

                    // Local file/about pages use RenderHtmlAsync rather than
                    // the HTTP success path, so they need the same post-load
                    // event dispatch explicitly.
                    var win = interpreter.WindowObject;
                    if (BrowserRuntime.JavaScriptEnabled &&
                        win != null && win.Get("onload") is { Type: JsType.Function } onload)
                        interpreter.CallHandler(onload, Retro96.Engine.Js.JsValue.FromObject(win));

                    var body = document.ElementDescendants()
                        .FirstOrDefault(e => e.TagName == "body");
                    DebugLog.Write($"BODY ONLOAD(local): found={body != null} " +
                                   $"source='{body?.GetAttr("onload") ?? "<null>"}'");
                    if (BrowserRuntime.JavaScriptEnabled && body != null)
                    {
                        var bodyOnload = body.GetAttr("onload");
                        if (!string.IsNullOrEmpty(bodyOnload))
                        {
                            body.EventHandlers["onload"] = bodyOnload;
                            interpreter.FireEvent(body, "onload");
                        }
                    }

                    // file://, about: and error pages render through this
                    // path — their frames/iframes must load too.  The
                    // HTTP-only success path used to be the ONLY caller of
                    // LoadFramesAsync, so an iframe on a locally-opened
                    // page never even attempted to load ("iframe don't
                    // work").
                    if (BrowserRuntime.FramesEnabled)
                        _ = LoadFramesAsync(document, root);
                }
                catch (Exception ex)
                {
                    // See ProcessSuccessAsync: never let a render exception
                    // leave the canvas blank and unexplained.
                    DebugLog.WriteException($"RenderHtmlAsync/UpdatePage gen={myGeneration}", ex);
                    _ = RenderErrorAsync(ErrorPage.NetworkError(url,
                        $"Layout error: {ex.Message}"), myGeneration);
                }
            });
        }
        catch (Exception ex)
        {
            DebugLog.WriteException($"RenderHtmlAsync gen={myGeneration}", ex);
            BeginInvoke(() =>
            {
                if (myGeneration == _navGeneration)
                    _statusLabel.Text = $"Render error: {ex.Message}";
            });
        }
        await Task.CompletedTask;
    }

    private void OnCanvasNavigateRequested(string url)
    {
        if (_currentPageUrl != null && url.StartsWith('#'))
        {
            string absoluteAnchorUrl = _currentPageUrl + url;
            MarkVisitedUrl(absoluteAnchorUrl);
            _history.PushAnchor(absoluteAnchorUrl);
            _canvas.ScrollToAnchor(url[1..]);
            _txtUrl.Text = absoluteAnchorUrl;
            return;
        }
        if (_currentPageUrl != null && url.StartsWith(_currentPageUrl + "#"))
        {
            MarkVisitedUrl(url);
            _history.PushAnchor(url);
            _canvas.ScrollToAnchor(url[(_currentPageUrl.Length + 1)..]);
            _txtUrl.Text = url;
            return;
        }

        NavigateTo(url);
    }

    /// <summary>
    /// Same-document fragment navigation does not go through NavigateAsync,
    /// so it used to bypass the session history used by :visited entirely.
    /// Record the resolved href here and immediately expose it to the live
    /// document, then re-resolve CSS so a:visited can take effect without
    /// reloading or disturbing the anchor scroll.
    /// </summary>
    private void MarkVisitedUrl(string absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return;

        _visitedUrls.Add(absoluteUrl);

        var document = _canvas.PageDocument;
        if (document == null)
            return;

        document.VisitedUrls.Add(absoluteUrl);
        _canvas.ReflowDocument();
    }

    private void OnCanvasStatusChanged(string status) =>
        BeginInvoke(() => _statusLabel.Text = status);

    private void OnFormSubmitted((string Url, string Body, string? Target,
                                  BrowserCanvas.FrameView? Frame,
                                  IReadOnlyList<Engine.Forms.MultipartField>? MultipartFields,
                                  IReadOnlyList<Engine.Forms.MultipartFile>? MultipartFiles) submit)
    {
        if (!BrowserRuntime.FormSubmissionsEnabled)
        {
            _statusLabel.Text = "Form submission blocked by Preferences → Advanced.";
            return;
        }

        if (submit.MultipartFields != null && submit.MultipartFiles != null)
        {
            _ = SubmitMultipartAsync(submit);
            return;
        }

        if (submit.Frame != null &&
            (string.IsNullOrEmpty(submit.Target) || submit.Target == "_self"))
        {
            _ = LoadFrameAsync(submit.Frame, submit.Url, submit.Body);
            return;
        }

        if (submit.Target == "_top" || submit.Target == "_parent" ||
            string.IsNullOrEmpty(submit.Target))
        {
            _ = NavigateAsync(submit.Url, submit.Body);
        }
        else if (submit.Target == "_blank")
        {
            OpenNewBrowserWindow(submit.Url);
        }
        else
        {
            var named = _canvas.FindFrameByName(submit.Target);
            if (named != null)
                _ = LoadFrameAsync(named, submit.Url, submit.Body);
            else
                _ = NavigateAsync(submit.Url, submit.Body);
        }
    }

    private async Task SubmitMultipartAsync((string Url, string Body, string? Target,
        BrowserCanvas.FrameView? Frame,
        IReadOnlyList<Engine.Forms.MultipartField>? MultipartFields,
        IReadOnlyList<Engine.Forms.MultipartFile>? MultipartFiles) submit)
    {
        if (submit.MultipartFields == null || submit.MultipartFiles == null) return;
        try
        {
            var url = ParsedUrl.Parse(submit.Url);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _httpClient.PostMultipartAsync(url, submit.MultipartFields, submit.MultipartFiles, _cookieStore, cts.Token);
            if (result is not HttpSuccess ok)
            {
                _statusLabel.Text = "Multipart form submission failed.";
                return;
            }

            string target = submit.Target ?? "";
            if (submit.Frame != null &&
                (target.Length == 0 || target.Equals("_self", StringComparison.OrdinalIgnoreCase)))
            {
                await ApplyMultipartResponseToFrameAsync(submit.Frame, ok, url, cts.Token);
            }
            else if (submit.Frame != null && target.Equals("_parent", StringComparison.OrdinalIgnoreCase) &&
                     _canvas.TryFindParentFrame(submit.Frame, out var parent, out _ ) && parent != null)
            {
                await ApplyMultipartResponseToFrameAsync(parent, ok, url, cts.Token);
            }
            else if (!string.IsNullOrEmpty(target) && !target.Equals("_top", StringComparison.OrdinalIgnoreCase))
            {
                var named = _canvas.FindFrameByName(target);
                if (named != null)
                    await ApplyMultipartResponseToFrameAsync(named, ok, url, cts.Token);
                else if (target.Equals("_blank", StringComparison.OrdinalIgnoreCase))
                    OpenNewBrowserWindow(submit.Url);
                else
                    await ProcessSuccessAsync(ok, url, cts.Token, null, false, false, _navGeneration);
            }
            else
            {
                await ProcessSuccessAsync(ok, url, cts.Token, null, false, false, _navGeneration);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Multipart form submission failed: " + ex.Message;
        }
    }

    private async Task ApplyMultipartResponseToFrameAsync(
        BrowserCanvas.FrameView view, HttpSuccess response, ParsedUrl responseUrl,
        CancellationToken ct)
    {
        _canvas.TryFindFrameHost(view, out var parentView, out var frameBox);
        int frameW = (int)(frameBox?.Width ?? view.RootBox.Width);
        int frameH = (int)(frameBox?.Height ?? view.RootBox.Height);
        var (interpreter, state) = CreateFrameContext(view);

        string html = DecodeBody(response);
        var doc = HtmlParser.Parse(html, responseUrl, _cookieStore,
            BrowserRuntime.ScriptingEnabled
                ? (fdoc, scriptSrc, isVbScript) => RunFrameScript(fdoc, scriptSrc, isVbScript, interpreter, state)
                : null,
            BrowserRuntime.ScriptingEnabled && BrowserRuntime.ExternalScriptsEnabled
                ? LoadExternalScript : null);
        if (BrowserRuntime.StylesheetsEnabled)
            await FetchStylesheetsAsync(doc, responseUrl, ct);

        var content = new FrameContent(doc,
            LayoutEngineApi.BuildLayoutTree(doc, Math.Max(1, frameW), Math.Max(1, frameH)),
            response.EffectiveUrl.Length > 0 ? response.EffectiveUrl : responseUrl.ToAbsolute());
        if (frameBox?.Element != null)
            content = ApplyFramePresentation(frameBox.Element, content, frameW, frameH);

        ApplyFrameContent(view, content, interpreter, state);
        view.Scroll = Retro96.Drawing.PointF.Empty;

        if (frameBox != null)
        {
            if (parentView == null) _canvas.SetFrame(frameBox, view);
            else _canvas.RefreshChildFrame(parentView, frameBox, view);
            await LoadFrameLevelAsync(content.Document, content.RootBox, view, _navGeneration);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dialogs
    // ─────────────────────────────────────────────────────────────────────

    private (string User, string Pass)? PromptForCredentials(string host)
    {
        using var form = new Form
        {
            Text = $"Enter username and password for {host}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 130),
            ShowInTaskbar = false
        };

        var lblUser = new Label { Text = "Username:", AutoSize = true, Location = new Point(12, 15) };
        var txtUser = new TextBox { Location = new Point(100, 12), Width = 220 };
        var lblPass = new Label { Text = "Password:", AutoSize = true, Location = new Point(12, 45) };
        var txtPass = new TextBox { Location = new Point(100, 42), Width = 220, UseSystemPasswordChar = true };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(160, 84) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(244, 84) };

        form.Controls.AddRange(new Control[] { lblUser, txtUser, lblPass, txtPass, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(this) == DialogResult.OK && txtUser.Text.Length > 0)
            return (txtUser.Text, txtPass.Text);
        return null;
    }

    internal IDisposable AddPluginFileMenuItem(string pluginId, string text, Action callback)
    {
        if (_pluginCommandsMenu == null)
            throw new InvalidOperationException("Plugin menu is not initialized.");

        var item = new ToolStripMenuItem(text);
        item.Click += OnPluginMenuItemClick;
        item.Tag = new PluginMenuRegistration(pluginId, callback);
        _pluginCommandsMenu.DropDownItems.Add(item);
        _pluginCommandsMenu.Visible = _pluginCommandsMenu.DropDownItems.Count > 0;
        return new DelegateDisposable(() =>
        {
            if (item.IsDisposed) return;
            item.Click -= OnPluginMenuItemClick;
            if (_pluginCommandsMenu.DropDownItems.Contains(item))
                _pluginCommandsMenu.DropDownItems.Remove(item);
            item.Dispose();
            _pluginCommandsMenu.Visible = _pluginCommandsMenu.DropDownItems.Count > 0;
        });
    }

    internal void RemovePluginFileMenuItems(string pluginId)
    {
        if (_pluginCommandsMenu == null) return;
        foreach (ToolStripItem item in _pluginCommandsMenu.DropDownItems.Cast<ToolStripItem>().ToArray())
        {
            if (item.Tag is PluginMenuRegistration registration &&
                registration.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            {
                _pluginCommandsMenu.DropDownItems.Remove(item);
                item.Dispose();
            }
        }
        _pluginCommandsMenu.Visible = _pluginCommandsMenu.DropDownItems.Count > 0;
    }

    internal void SetPluginStatus(string pluginId, string text)
    {
        _statusLabel.Text = $"{text}";
    }

    internal void SetEmbeddedPluginStatus(string pluginId, string text) => _statusLabel.Text = text ?? string.Empty;

    internal async Task<Retro96.Plugins.JsValue> PluginCallPageFunctionAsync(
        DomElement embed, string name, IReadOnlyList<Retro96.Plugins.JsValue> args, CancellationToken cancellationToken = default)
    {
        if (!BrowserRuntime.JavaScriptEnabled) throw new SecurityException("Page JavaScript is disabled.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A page function name is required.", nameof(name));
        if (_jsInterpreter == null || _globalScope == null) throw new InvalidOperationException("The page scripting context is unavailable.");
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

        string[] parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 8 || parts.Any(p => !p.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')))
            throw new SecurityException("Page function names must be simple JavaScript member paths.");
        return await InvokeOnUiThreadAsync(() =>
        {
            Retro96.Engine.Js.JsValue target = _globalScope.Get(parts[0]);
            Retro96.Engine.Js.JsValue thisValue = Retro96.Engine.Js.JsValue.FromObject(_globalScope.Get("window").Type == JsType.Object
                ? _globalScope.Get("window").GetObject()
                : new JsObject());
            for (int i = 1; i < parts.Length; i++)
            {
                if (target.Type is not (JsType.Object or JsType.Function)) throw new MissingMemberException(parts[i]);
                thisValue = target;
                target = target.GetObjectOrFunction().Get(parts[i]);
            }
            if (target.Type != JsType.Function) throw new MissingMethodException(name);
            var engineArgs = args.Select(Retro96.Plugins.PluginJsValueCodec.ToEngine).ToArray();
            var result = _jsInterpreter.CallFunction(target.GetFunction(), thisValue, engineArgs);
            return Retro96.Plugins.PluginJsValueCodec.FromEngine(result);
        }).ConfigureAwait(true);
    }

    private async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(Form1));
        if (!InvokeRequired) return action();
        return (T)Invoke(action)!;
    }

    internal void OpenPluginWindow(string url) => OpenNewBrowserWindow(url);
    internal void PluginScrollTo(int x, int y) => _canvas.ScrollTo(x, y);

    private void OnPluginMenuItemClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not PluginMenuRegistration registration)
            return;
        try { registration.Callback(); }
        catch (Exception ex)
        {
            DebugLog.WriteException($"Plugin menu '{registration.PluginId}'", ex);
            MessageBox.Show(this, ex.Message, "Plugin Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed record PluginMenuRegistration(string PluginId, Action Callback);

    private sealed class DelegateDisposable : IDisposable
    {
        private Action? _dispose;
        public DelegateDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private void OpenNewBrowserWindow(string url)
    {
        if (!BrowserRuntime.ScriptedWindowsAllowed && !url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            _statusLabel.Text = "New window blocked by High trust mode.";
            return;
        }
        var window = new Form1();
        _childWindows.Add(window);
        window.FormClosed += (s, e) => _childWindows.Remove(window);
        window.Show();
        window.NavigateTo(url);
    }

    private async void OpenHtmlFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "HTML files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*",
            Title = "Open HTML File"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        long myGeneration = ++_navGeneration;
        _hostOpenedLocalDocument = true;
        _imageCache.HostOpenedLocalPage = true;
        DebugLog.Write($"OpenHtmlFile gen={myGeneration} file='{dialog.FileName}'");

        try
        {
            string html = BodyDecoder.Decode(
                await File.ReadAllBytesAsync(dialog.FileName), null);
            // Canonical file:/// base (three slashes, empty authority) so
            // every relative link/image on the page resolves cleanly —
            // the old two-slash form produced malformed file:////C:/…
            // resolved URLs downstream.
            string url = CanonicalFileUrl(Path.GetFullPath(dialog.FileName));
            await RenderHtmlAsync(html, url, replaceHistory: false, myGeneration);
        }
        catch (Exception ex)
        {
            DebugLog.WriteException($"OpenHtmlFile gen={myGeneration}", ex);
            await RenderErrorAsync(ErrorPage.LocalFileNotFound(dialog.FileName), myGeneration);
            if (myGeneration == _navGeneration)
                _statusLabel.Text = ex.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Printing
    // ─────────────────────────────────────────────────────────────────────

    private void PrintPage()
    {
        _statusLabel.Text = "Preparing print…";

        // Never print the live scrolled/selected screen composition.  Ask
        // the engine for a clean, top-of-document surface first.  Falling
        // back to the existing bitmap keeps printing useful if a transient
        // render failure occurs.
        var printEngineBitmap = _canvas.CreatePrintBitmap();
        var engineBitmap = printEngineBitmap;
        if (engineBitmap == null)
        {
            _statusLabel.Text = "Nothing to print";
            return;
        }

        using var printerBitmap = SkiaPrintInterop.ToPrinterBitmap(engineBitmap);
        if (printEngineBitmap != null)
            printEngineBitmap.Dispose();

        if (printerBitmap == null)
        {
            _statusLabel.Text = "Nothing to print";
            return;
        }

        using var printDoc = new System.Drawing.Printing.PrintDocument
        {
            DocumentName = string.IsNullOrWhiteSpace(Text) ? "Retro96" : Text
        };
        using var dialog = new PrintDialog { Document = printDoc, UseEXDialog = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            _statusLabel.Text = "Ready";
            return;
        }

        int pageIndex = 0;

        printDoc.PrintPage += (s, e) =>
        {
            try
            {
                var graphics = e.Graphics;
                if (graphics == null || printerBitmap.Width <= 0 || printerBitmap.Height <= 0)
                {
                    e.HasMorePages = false;
                    return;
                }

                // MarginBounds is already expressed in the printer's page
                // unit (1/100 inch).  Fit the entire rendered page to the
                // printable width and derive the source-pixel height from
                // the same scale, so there is no DPI double-conversion or
                // vertical drift between pages.
                float destWidth = Math.Max(1f, e.MarginBounds.Width);
                float scale = destWidth / printerBitmap.Width;
                float srcPageHeight = Math.Max(1f, e.MarginBounds.Height / scale);
                float srcY = pageIndex * srcPageHeight;

                if (srcY >= printerBitmap.Height - 0.01f)
                {
                    e.HasMorePages = false;
                    return;
                }

                float srcHeight = Math.Min(srcPageHeight, printerBitmap.Height - srcY);
                var srcRect = new RectangleF(0f, srcY, printerBitmap.Width, srcHeight);
                var destRect = new RectangleF(
                    e.MarginBounds.Left,
                    e.MarginBounds.Top,
                    destWidth,
                    srcHeight * scale);

                var graphicsState = graphics.Save();
                try
                {
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                    graphics.DrawImage(printerBitmap, destRect, srcRect, GraphicsUnit.Pixel);
                }
                finally
                {
                    graphics.Restore(graphicsState);
                }

                pageIndex++;
                e.HasMorePages = srcY + srcHeight < printerBitmap.Height - 0.01f;
                if (!e.HasMorePages)
                    _statusLabel.Text = $"Printed ({pageIndex} page{(pageIndex == 1 ? "" : "s")}).";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Print error: {ex.Message}";
                e.HasMorePages = false;
            }
        };

        try
        {
            _statusLabel.Text = "Printing…";
            printDoc.Print();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Print error: {ex.Message}";
        }
    }


    private static string Retro96HomePageHtml()
    {
        return @"<!DOCTYPE HTML PUBLIC ""-//W3C//DTD HTML 3.2 Final//EN"">
<HTML>
<HEAD>
<TITLE>Retro96</TITLE>
</HEAD>
<BODY BGCOLOR=""#FFFFFF"" TEXT=""#000000"" LINK=""#0000EE"" VLINK=""#551A8B"" ALINK=""#FF0000"">
<CENTER>
<TABLE WIDTH=""600"" BORDER=""0"" CELLPADDING=""0"" CELLSPACING=""0"">
<TR><TD ALIGN=""LEFT"">
<FONT COLOR=""#000080"" SIZE=""6"" FACE=""Arial, Helvetica""><B>Retro96</B></FONT><BR>
<FONT COLOR=""#666666"" SIZE=""2"" FACE=""Arial, Helvetica"">A small browser for the 1996 web.</FONT>
</TD></TR>
</TABLE>

<BR>

<TABLE WIDTH=""600"" BORDER=""1"" CELLPADDING=""10"" CELLSPACING=""0"" BGCOLOR=""#F2F2F2"">
<TR><TD ALIGN=""LEFT"">
<FONT SIZE=""3"" FACE=""Arial, Helvetica""><B>Search</B></FONT><BR>
<FORM ACTION=""http://frogfind.com"" METHOD=""GET"">
<INPUT TYPE=""TEXT"" NAME=""q"" SIZE=""42"" MAXLENGTH=""128""><INPUT TYPE=""SUBMIT"" VALUE=""Search"">
</FORM>
</TD></TR>
</TABLE>

<BR>

<TABLE WIDTH=""600"" BORDER=""0"" CELLPADDING=""6"" CELLSPACING=""0"">
<TR><TD ALIGN=""LEFT""><FONT SIZE=""3"" FACE=""Arial, Helvetica""><B>Explore</B></FONT></TD></TR>
<TR><TD ALIGN=""LEFT""><A HREF=""https://web.archive.org/web/19961020015116/http://www3.netscape.com/""><B>Netscape</B></A><BR><FONT SIZE=""2"">Netscape's home page, preserved from October 1996.</FONT></TD></TR>
<TR><TD ALIGN=""LEFT""><A HREF=""https://web.archive.org/web/19961220034419/http://www.ncsa.uiuc.edu/SDG/Software/Mosaic/NCSAMosaicHome.html""><B>NCSA Mosaic</B></A><BR><FONT SIZE=""2"">The Mosaic home page as captured on December 20, 1996.</FONT></TD></TR>
<TR><TD ALIGN=""LEFT""><A HREF=""https://web.archive.org/web/19970414062947/http://www.hendrix.edu/""><B>Hendrix College</B></A><BR><FONT SIZE=""2"">The Summer 1996 version of the Hendrix College site.</FONT></TD></TR>
</TABLE>

<BR>
<HR WIDTH=""600"" SIZE=""1"">
<FONT SIZE=""2"" COLOR=""#666666"">The links above open preserved period pages from the Web's 1996 era.</FONT>
</CENTER>
</BODY>
</HTML>";
    }

        private static string Retro96WelcomePageHtml() => """
<!DOCTYPE HTML PUBLIC "-//W3C//DTD HTML 3.2 Final//EN">
<html>
<head>
<title>Welcome to Retro96</title>
<style type="text/css">
body {
    background-color: #c0c0c0;
    color: #000000;
    font-family: "Times New Roman", Times, serif;
    margin: 0;
    padding: 0;
}

.page {
    width: 580px;
    margin-left: auto;
    margin-right: auto;
    margin-top: 32px;
    margin-bottom: 32px;
    background-color: #ffffff;
    border: 2px solid #808080;
    padding: 32px 40px 40px 40px;
}

h1 {
    font-family: "Times New Roman", Times, serif;
    font-size: 28pt;
    font-weight: bold;
    color: #000080;
    margin-top: 0;
    margin-bottom: 4px;
    letter-spacing: -1px;
}

.subtitle {
    font-family: Arial, Helvetica, sans-serif;
    font-size: 9pt;
    color: #808080;
    margin-bottom: 24px;
}

hr {
    border: none;
    border-top: 1px solid #808080;
    margin-top: 0;
    margin-bottom: 24px;
}

h2 {
    font-family: Arial, Helvetica, sans-serif;
    font-size: 11pt;
    font-weight: bold;
    color: #000000;
    margin-top: 24px;
    margin-bottom: 8px;
    border-bottom: 1px solid #c0c0c0;
    padding-bottom: 2px;
}

p {
    font-family: "Times New Roman", Times, serif;
    font-size: 11pt;
    line-height: 1.55;
    margin-top: 0;
    margin-bottom: 12px;
    color: #000000;
}

.note {
    background-color: #ffffcc;
    border: 1px solid #c0c000;
    padding: 10px 14px;
    font-family: Arial, Helvetica, sans-serif;
    font-size: 9pt;
    line-height: 1.5;
    margin-bottom: 20px;
    color: #333300;
}

ul {
    font-family: "Times New Roman", Times, serif;
    font-size: 11pt;
    line-height: 1.6;
    margin-top: 0;
    margin-bottom: 12px;
    padding-left: 20px;
    color: #000000;
}

li {
    margin-bottom: 4px;
}

.footer {
    font-family: Arial, Helvetica, sans-serif;
    font-size: 8pt;
    color: #808080;
    text-align: center;
    margin-top: 28px;
    border-top: 1px solid #c0c0c0;
    padding-top: 12px;
}

a {
    color: #000080;
}

a:visited {
    color: #800080;
}

code {
    font-family: "Courier New", Courier, monospace;
    font-size: 9pt;
    background-color: #f0f0f0;
    padding: 1px 3px;
}
</style>
</head>
<body>

<div class="page">

    <h1>Retro96</h1>
    <div class="subtitle">Version: I have no idea &mdash; Beta &mdash; Not production software</div>

    <hr>

    <div class="note">
        <b>Beta notice:</b> It works well right now, but I'm still finding and fixing bugs.
        Not polished yet. Not ready for production.
    </div>

    <div class="note" style="border-color: #c00000; background-color: #fff0f0; color: #330000;">
        <b>Windows only.</b> The security model, sandboxing, plugin isolation, and everything else
        are built for Windows and staying that way. I can smell you, backporters. Do not.
    </div>

    <p>
        Retro96 renders the web the way it looked in 1996. Not "quirks mode close enough" &mdash;
        actually correctly. The target is 1996, but it covers some 1997&ndash;1998 features on
        purpose, to handle later retro sites without breaking.
    </p>

    <p>
        Almost everything is hand-written C#. The HTML tokenizer and parser, CSS parser,
        selector engine, style resolver, layout engine, ES3 JavaScript and VBScript 1.0
        interpreters, DOM bindings, a Java applet interpreter with hand-written
        java.* natives and AWT, networking, and the plugin sandbox. SkiaSharp provides the
        rendering surface.
    </p>

    <h2>What it does</h2>

    <ul>
        <li>HTML tokenizer and parser, written from scratch (HTML 3.2)</li>
        <li>CSS1 parser, selector engine, and style resolver</li>
        <li>Block, inline, and table layout &mdash; period-accurate, no silent fixes</li>
        <li>ES3 JavaScript with DOM-0 scripting (<code>document.formName.fieldName</code>, <code>window.status</code>, live clocks)</li>
        <li>A separate native VBScript 1.0 engine for classic VBScript blocks and event procedures</li>
        <li>Frames and nested iframes that actually load and run correctly</li>
        <li><code>&lt;blink&gt;</code>, <code>text-decoration: blink</code>, and <code>String.prototype.blink()</code></li>
        <li>Java applets via a built-in interpreter: Java 1.0/1.1 bytecode, lifecycle and AWT support, with no JRE needed</li>
        <li>Applet audio for WAVE, AU, AIFF/AIFC, and Standard MIDI; MIDI playback uses Windows MCI</li>
        <li>LiveConnect through named applets, supported public members, and <code>netscape.javascript.JSObject</code>; applets can find and call one another across page frames</li>
        <li>Plugin system with sandboxed, permission-gated <code>.r96p</code> packages</li>
    </ul>

    <h2>What it doesn't do</h2>

    <ul>
        <li>HTML 4, CSS2, ES5, or anything past the late 1990s</li>
        <li>Java 1.2+ bytecode, Swing, or the collections framework (selected later runtime APIs are supported separately)</li>
        <li>Every historical JVM class, browser-plugin quirk, audio codec, or LiveConnect conversion; support focuses on common Java 1.0/1.1 applets and the documented bridge</li>
        <li>macOS or Linux &mdash; see the Windows notice above</li>
    </ul>

    <h2>Status</h2>

    <p>
        The test sources define 261 xUnit cases across the main and focused VBScript suites
        (229 facts, 9 theory cases, and 23 VBScript facts), plus 29 live JavaScript page-contract
        assertions and 54 hand-authored QA HTML files. There is also a layout lab and a
        Chromium pixel-diff harness. Retro96 loads actual 1996 sites. It's a side project built
        for fun, and that's what it'll stay.
    </p>

    <h2>Building</h2>

    <p>Requires .NET 8 or 11. From the <code>retro96/</code> directory:</p>

    <p><code>dotnet build -p:EnableWindowsTargeting=true</code></p>

    <p>
        Full instructions, project layout, and the plugin SDK are in the
        <a href="https://github.com/DirazCoder/Retro96">README on GitHub</a>
        &mdash; open that in a modern browser.
    </p>

    <div class="footer">
        Retro96 &mdash; MIT License &mdash; <a href="https://github.com/DirazCoder/Retro96">github.com/DirazCoder/Retro96</a> (open in a modern browser)
    </div>

</div>

</body>
</html>
""";


    private SKBitmap LoadEmbeddedAsset(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
                throw new FileNotFoundException($"Embedded asset not found: {resourceName}");
            return SKBitmap.Decode(stream) ?? throw new InvalidDataException($"Embedded asset could not be decoded: {resourceName}");
        }
    }

    private void LoadThrobberGif()
    {
        try
        {
            using var staticSkia = LoadEmbeddedAsset("Retro96.assets.static.png");
            _staticBitmap = staticSkia.Copy();

            string resourceName = "Retro96.assets.throbber.gif";
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] data = ms.ToArray();
                _throbberDecoded = ImageDecoder.Decode(data, "image/gif");

                if (_throbberDecoded != null && _throbberDecoded.Frames.Count > 1)
                {
                    int interval = _throbberDecoded.DelaysMs.Count > 0
                        ? Math.Max(20, _throbberDecoded.DelaysMs[0])
                        : 100;

                    _throbberTimer = new System.Windows.Forms.Timer { Interval = interval };
                    _throbberTimer.Tick += (s, e) =>
                    {
                        if (!_isLoading) return;
                        _throbberFrameIndex++;
                        if (_throbberBox != null && !_throbberBox.IsDisposed)
                            _throbberBox.Invalidate();
                    };
                    _throbberTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            _throbberDiag = "load failed: " + ex.GetType().Name;
            DebugLog.WriteException("LoadThrobberGif", ex);
        }
    }


    private void SetAppIcon()
    {
        try
        {
            using var skiaBmp = LoadEmbeddedAsset("Retro96.assets.logo.png");
            using var ms = new MemoryStream();
            skiaBmp.Encode(SKEncodedImageFormat.Png, 100).SaveTo(ms);
            ms.Position = 0;
            using var bmp = new System.Drawing.Bitmap(ms);
            IntPtr hIcon = bmp.GetHicon();
            Icon = Icon.FromHandle(hIcon);
        }
        catch { }
    }
}
