namespace Retro96;

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Retro96.Engine;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;

public partial class Form1 : Form
{
    // Controls
    private ToolStrip _toolbar           = new ToolStrip();
    private ToolStripDropDownButton _btnFile = new ToolStripDropDownButton("File");
    private ToolStripButton _btnBack     = new ToolStripButton("←");
    private ToolStripButton _btnForward  = new ToolStripButton("→");
    private ToolStripButton _btnStop     = new ToolStripButton("■");
    private ToolStripTextBox _txtUrl     = new ToolStripTextBox();
    private ToolStripButton _btnGo       = new ToolStripButton("Go");
    private BrowserCanvas _canvas        = new BrowserCanvas();
    private StatusStrip _statusStrip     = new StatusStrip();
    private ToolStripStatusLabel _statusLabel = new ToolStripStatusLabel();

    // State
    private NavigationHistory _history = new NavigationHistory();
    private CancellationTokenSource? _loadCts;

    // Engine components
    private JsScope?        _globalScope;
    private JsInterpreter?  _jsInterpreter;
    private ImageCache?     _imageCache;
    private FontCache?      _fontCache;
    private ResourceLoader? _resourceLoader;
    private CookieStore?    _cookieStore;
    private HttpClient?     _httpClient;

    public Form1()
    {
        InitializeComponent();
        InitializeBrowser();
    }

    /// <summary>
    /// Parses a CSS url() value and extracts the actual URL.
    /// Handles url("..."), url('...'), and url(...) formats.
    /// Returns null if the input is not a valid url() value.
    /// </summary>
    private static string? ParseCssUrl(string? cssValue)
    {
        if (string.IsNullOrWhiteSpace(cssValue) || cssValue == "none")
            return null;

        // Check if it's a url() wrapper
        ReadOnlySpan<char> span = cssValue.AsSpan().Trim();
        if (!span.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            return cssValue; // Not a url() wrapper, return as-is

        // Remove "url(" prefix and ")" suffix
        span = span.Slice(4, span.Length - 5).Trim();

        // Remove quotes if present
        if (span.Length >= 2)
        {
            char first = span[0];
            if ((first == '"' || first == '\'') && span[span.Length - 1] == first)
                span = span.Slice(1, span.Length - 2);
        }

        return span.IsEmpty ? null : span.ToString();
    }

    private void InitializeComponent()
    {
        AutoScaleMode      = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text            = "— Retro96";
        Size            = new Size(1280, 900);
        MinimumSize     = new Size(800, 600);
        StartPosition   = FormStartPosition.CenterScreen;
        FormClosing    += (s, e) => { _resourceLoader?.Dispose(); _httpClient = null; };

        // URL bar stretches to fill toolbar
        _txtUrl.AutoSize = false;
        _txtUrl.Width    = 600;

        _toolbar.Items.Add(_btnFile);
        _toolbar.Items.Add(_btnBack);
        _toolbar.Items.Add(_btnForward);
        _toolbar.Items.Add(_btnStop);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_txtUrl);
        _toolbar.Items.Add(_btnGo);
        _btnBack.Enabled    = false;
        _btnForward.Enabled = false;
        _btnStop.Enabled    = false;

        _btnFile.DropDownItems.Add("Open html").Click += (s, e) => OpenHtmlFile();

        _canvas.Dock                = DockStyle.Fill;
        _canvas.NavigateRequested  += OnCanvasNavigateRequested;
        _canvas.StatusChanged      += OnCanvasStatusChanged;

        _statusStrip.Items.Add(_statusLabel);
        _statusLabel.Text   = "Ready";
        _statusLabel.Spring = true;

        Controls.Add(_canvas);
        Controls.Add(_statusStrip);
        Controls.Add(_toolbar);

        _btnBack.Click    += (s, e) => NavigateBack();
        _btnForward.Click += (s, e) => NavigateForward();
        _btnStop.Click    += (s, e) => { _loadCts?.Cancel(); };
        _btnGo.Click      += (s, e) => NavigateTo(_txtUrl.Text);
        _txtUrl.KeyDown   += (s, e) => { if (e.KeyCode == Keys.Enter) NavigateTo(_txtUrl.Text); };
    }

    private void InitializeBrowser()
    {
        _cookieStore    = new CookieStore();
        _imageCache     = new ImageCache();
        _fontCache      = new FontCache();
        _resourceLoader = new ResourceLoader(_cookieStore);
        _canvas.SetResourceLoader(_resourceLoader);
        _httpClient     = new HttpClient();

        _globalScope = new JsScope();
        JsRuntime.PopulateGlobalScope(_globalScope);

        _jsInterpreter = new JsInterpreter(
            _globalScope,
            new DomDocument(_cookieStore!),
            url => NavigateTo(url),
            msg => BeginInvoke(() => _statusLabel.Text = msg)
        );
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Navigation
    // ─────────────────────────────────────────────────────────────────────────

    public async void NavigateTo(string rawUrl)
    {
        try { ParsedUrl.AssertSafe(rawUrl); }
        catch (Exception ex) { ShowErrorPage("Invalid URL", ex.Message); return; }

        string url = rawUrl.Trim();

        // POST detection
        bool   isPost    = false;
        string? postData = null;
        if (url.StartsWith("post://", StringComparison.OrdinalIgnoreCase))
        {
            isPost = true;
            url    = "http://" + url[7..];
            var parts = url.Split('?', 2);
            if (parts.Length > 1) { postData = parts[1]; url = parts[0]; }
        }
        else if (!url.Contains("://"))
        {
            url = "http://" + url;
        }

        // Update UI
        _txtUrl.Text      = url;
        Text              = "Loading… — Retro96";
        _statusLabel.Text = "Connecting…";
        Cursor            = Cursors.WaitCursor;
        _btnStop.Enabled  = true;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            if (_httpClient == null || _cookieStore == null)
            { ShowErrorPage("Init Error", "Browser engine not initialised."); return; }

            HttpResult result = isPost
                ? await _httpClient.PostAsync(ParsedUrl.Parse(url), postData ?? "", _cookieStore, ct)
                : await _httpClient.GetAsync(ParsedUrl.Parse(url), _cookieStore, ct);

            switch (result)
            {
                case HttpSuccess s:    await ProcessSuccessResponse(s, url, ct); break;
                case CertError   ce:   await ProcessCertError(ce, url);          break;
                case HttpError   he:   await ProcessHttpError(he, url);           break;
                case TooManyRedirects: await ProcessTooManyRedirects(url);        break;
            }
        }
        catch (OperationCanceledException)
        {
            BeginInvoke(() => { _statusLabel.Text = "Cancelled"; Cursor = Cursors.Default; _btnStop.Enabled = false; });
        }
        catch (Exception ex)
        {
            BeginInvoke(() => ShowErrorPage("Navigation Error", ex.Message));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTTP success
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ProcessSuccessResponse(HttpSuccess success, string url, CancellationToken ct)
    {
        if (success.StatusCode >= 400)
        {
            string errorHtml = success.StatusCode switch
            {
                404 => ErrorPage.NotFound(url),
                403 => ErrorPage.AccessDenied(url),
                500 => ErrorPage.ServerError(url, Encoding.UTF8.GetString(success.Body)),
                _   => ErrorPage.NetworkError(url, $"HTTP {success.StatusCode}")
            };
            await RenderErrorHtml(errorHtml, url);
            return;
        }

        // ── Decode body ────────────────────────────────────────────────────
        string charset = success.Charset ?? "iso-8859-1";
        string html;
        try   { html = Encoding.GetEncoding(charset).GetString(success.Body); }
        catch { html = Encoding.GetEncoding("iso-8859-1").GetString(success.Body); }

        BeginInvoke(() => _statusLabel.Text = "Parsing…");

        // ── Parse ──────────────────────────────────────────────────────────
        var baseUrl  = ParsedUrl.Parse(url);
        var document = HtmlParser.Parse(html, baseUrl, _cookieStore!);

        // ── Fetch linked CSS ────────────────────────────────────────────────
        BeginInvoke(() => _statusLabel.Text = "Fetching stylesheets…");
        var cssTasks = document.ElementDescendants()
            .Where(e => e.TagName == "link" &&
                        e.GetAttr("rel")?.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) == true &&
                        e.HasAttr("href"))
            .Select(async link =>
            {
                string href = link.GetAttr("href")!;
                var res = await _resourceLoader!.FetchAsync(href, baseUrl, _cookieStore!);
                if (res is HttpSuccess css)
                {
                    string enc = css.Charset ?? "iso-8859-1";
                    string cssText;
                    try   { cssText = Encoding.GetEncoding(enc).GetString(css.Body); }
                    catch { cssText = Encoding.GetEncoding("iso-8859-1").GetString(css.Body); }

                    var styleElem = new DomElement("style");
                    styleElem.AppendChild(new DomText { Data = cssText });
                    document.AppendChild(styleElem);
                }
            });
        await Task.WhenAll(cssTasks);

        // ── Style + Layout ─────────────────────────────────────────────────
        BeginInvoke(() => _statusLabel.Text = "Laying out…");
        StyleResolver.Resolve(document);

        // Capture canvas size on UI thread before building layout
        Size canvasSize = Size.Empty;
        if (InvokeRequired) canvasSize = (Size)Invoke(() => _canvas.ClientSize);
        else canvasSize = _canvas.ClientSize;

        var rootBox = Retro96.Engine.Layout.LayoutEngine.BuildLayoutTree(
            document, canvasSize.Width, canvasSize.Height);

        // ── Pre-fetch all images (async, then repaint when done) ───────────
        _ = PrefetchImagesAsync(document, baseUrl, ct);

        // ── JS setup ───────────────────────────────────────────────────────
        _jsInterpreter = new JsInterpreter(
            _globalScope!, document,
            url => NavigateTo(url),
            msg => BeginInvoke(() => _statusLabel.Text = msg));
        _jsInterpreter.SetCurrentPageUrl(url);
        DomBindings.RegisterAll(_globalScope!, document, _history, _canvas);
        _jsInterpreter.RegisterDeferredBindings();

        foreach (var scriptElem in document.ElementDescendants()
                                           .Where(e => e.TagName == "script"))
        {
            var tn = scriptElem.Children.OfType<DomText>().FirstOrDefault();
            if (tn == null) continue;
            try
            {
                var prog = Retro96.Engine.Js.JsParser.Parse(tn.Data);
                _jsInterpreter.Execute(prog);
            }
            catch (Exception ex)
            {
                BeginInvoke(() => _statusLabel.Text = $"Script error: {ex.Message}");
            }
        }

        // ── Render + display ───────────────────────────────────────────────
        BeginInvoke(() => UpdatePage(document, rootBox, url));
    }

    /// <summary>
    /// Fetches all img src / body background URLs into the image cache,
    /// then triggers a repaint so images appear as they arrive.
    /// </summary>
    private async Task PrefetchImagesAsync(DomDocument doc, ParsedUrl baseUrl,
                                           CancellationToken ct)
    {
        // Collect every URL that could carry an image
        var urls = new List<string>();

        foreach (var elem in doc.ElementDescendants())
        {
            string? raw = elem.TagName switch
            {
                "img"  => elem.GetAttr("src"),
                "body" => elem.GetAttr("background"),
                _      => null
            };
            if (!string.IsNullOrEmpty(raw))
            {
                try { urls.Add(baseUrl.Resolve(raw).ToAbsolute()); } catch { }
            }

            // CSS background-image - parse url() wrapper and resolve against base URL
            string? bgCss = elem.Style?.BackgroundImage;
            if (!string.IsNullOrEmpty(bgCss) && bgCss != "none")
            {
                string? bgUrl = ParseCssUrl(bgCss);
                if (!string.IsNullOrEmpty(bgUrl))
                {
                    try { urls.Add(baseUrl.Resolve(bgUrl).ToAbsolute()); } catch { }
                }
            }
        }

        var tasks = urls.Distinct().Select(async absoluteUrl =>
        {
            try { await _imageCache!.GetAsync(absoluteUrl, _resourceLoader!, ct); }
            catch { }
        });

        await Task.WhenAll(tasks);

        // Repaint now that images are loaded
        BeginInvoke(() => _canvas.ReRenderPage(_fontCache!, _imageCache!, _resourceLoader!));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Error helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ProcessCertError(CertError ce, string url)
        => await RenderErrorHtml(ErrorPage.CertificateError(url, ce.Message, () => true), url);

    private async Task ProcessHttpError(HttpError he, string url)
        => await RenderErrorHtml(ErrorPage.NetworkError(url, he.Message), url);

    private async Task ProcessTooManyRedirects(string url)
        => await RenderErrorHtml(ErrorPage.TooManyRedirects(url), url);

    private Task RenderErrorHtml(string errorHtml, string originalUrl)
    {
        var doc = HtmlParser.Parse(errorHtml, ParsedUrl.Parse("about:blank"), _cookieStore!);
        StyleResolver.Resolve(doc);

        Size sz = InvokeRequired ? (Size)Invoke(() => _canvas.ClientSize) : _canvas.ClientSize;
        var root = Retro96.Engine.Layout.LayoutEngine.BuildLayoutTree(doc, sz.Width, sz.Height);
        BeginInvoke(() => UpdatePage(doc, root, originalUrl));
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UpdatePage — render on UI thread then hand to canvas
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdatePage(DomDocument document, LayoutBox rootBox, string url)
    {
        if (_history.Current != url)
            _history.Push(url);

        UpdateNavigationButtons();

        // Pass layout to canvas — canvas renders from the layout tree directly
        _canvas.SetPage(document, rootBox, _jsInterpreter!, _fontCache!, _imageCache!);
        _canvas.ReRenderPage(_fontCache!, _imageCache!, _resourceLoader!);

        Text              = $"{document.Title} — Retro96";
        _txtUrl.Text      = url;
        _statusLabel.Text = "Done";
        Cursor            = Cursors.Default;
        _btnStop.Enabled  = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Navigation helpers
    // ─────────────────────────────────────────────────────────────────────────

    public void NavigateBack()
    {
        var url = _history.Back();
        if (url != null) NavigateTo(url);
    }

    public void NavigateForward()
    {
        var url = _history.Forward();
        if (url != null) NavigateTo(url);
    }

    private void UpdateNavigationButtons()
    {
        _btnBack.Enabled    = _history.CanGoBack;
        _btnForward.Enabled = _history.CanGoForward;
    }

    private void OnCanvasNavigateRequested(string url) => NavigateTo(url);
    private void OnCanvasStatusChanged(string status)  => BeginInvoke(() => _statusLabel.Text = status);

    private void ShowErrorPage(string title, string message)
    {
        string html = ErrorPage.NetworkError(_txtUrl.Text, message);
        var doc     = HtmlParser.Parse(html, ParsedUrl.Parse("about:blank"), _cookieStore!);
        StyleResolver.Resolve(doc);
        var root = Retro96.Engine.Layout.LayoutEngine.BuildLayoutTree(doc, _canvas.ClientSize.Width, _canvas.ClientSize.Height);
        UpdatePage(doc, root, _txtUrl.Text);
    }

    private async void OpenHtmlFile()
    {
        using var dialog = new OpenFileDialog();
        dialog.Filter = "HTML files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*";
        dialog.Title = "Open HTML File";
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            string filePath = dialog.FileName;
            string url = new Uri(filePath).AbsoluteUri;
            await LoadLocalHtmlFile(filePath, url);
        }
    }

    private async Task LoadLocalHtmlFile(string filePath, string url)
    {
        try
        {
            _txtUrl.Text      = url;
            Text              = "Loading… — Retro96";
            _statusLabel.Text = "Reading file…";
            Cursor            = Cursors.WaitCursor;
            _btnStop.Enabled  = false;

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            string html = await File.ReadAllTextAsync(filePath, ct);

            BeginInvoke(() => _statusLabel.Text = "Parsing…");

            var baseUrl = ParsedUrl.Parse(url);
            var document = HtmlParser.Parse(html, baseUrl, _cookieStore!);

            // Fetch linked CSS
            BeginInvoke(() => _statusLabel.Text = "Fetching stylesheets…");
            var cssTasks = document.ElementDescendants()
                .Where(e => e.TagName == "link" &&
                            e.GetAttr("rel")?.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) == true &&
                            e.HasAttr("href"))
                .Select(async link =>
                {
                    string href = link.GetAttr("href")!;
                    try
                    {
                        var resolved = baseUrl.Resolve(href);
                        if (resolved.Scheme == "file")
                        {
                            string localPath = new Uri(resolved.ToAbsolute()).LocalPath;
                            string cssText = await File.ReadAllTextAsync(localPath, ct);
                            var styleElem = new DomElement("style");
                            styleElem.AppendChild(new DomText { Data = cssText });
                            document.AppendChild(styleElem);
                        }
                        else
                        {
                            var res = await _resourceLoader!.FetchAsync(href, baseUrl, _cookieStore!);
                            if (res is HttpSuccess css)
                            {
                                string enc = css.Charset ?? "iso-8859-1";
                                string cssText = Encoding.GetEncoding(enc).GetString(css.Body);
                                var styleElem = new DomElement("style");
                                styleElem.AppendChild(new DomText { Data = cssText });
                                document.AppendChild(styleElem);
                            }
                        }
                    }
                    catch { }
                });
            await Task.WhenAll(cssTasks);

            BeginInvoke(() => _statusLabel.Text = "Laying out…");
            StyleResolver.Resolve(document);

            Size canvasSize = InvokeRequired ? (Size)Invoke(() => _canvas.ClientSize) : _canvas.ClientSize;
            var rootBox = Retro96.Engine.Layout.LayoutEngine.BuildLayoutTree(document, canvasSize.Width, canvasSize.Height);

            _ = PrefetchImagesAsync(document, baseUrl, ct);

            _jsInterpreter = new JsInterpreter(
                _globalScope!, document,
                u => NavigateTo(u),
                msg => BeginInvoke(() => _statusLabel.Text = msg));
            _jsInterpreter.SetCurrentPageUrl(url);
            DomBindings.RegisterAll(_globalScope!, document, _history, _canvas);
            _jsInterpreter.RegisterDeferredBindings();

            foreach (var scriptElem in document.ElementDescendants()
                                               .Where(e => e.TagName == "script"))
            {
                var tn = scriptElem.Children.OfType<DomText>().FirstOrDefault();
                if (tn == null) continue;
                try
                {
                    var prog = Retro96.Engine.Js.JsParser.Parse(tn.Data);
                    _jsInterpreter.Execute(prog);
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => _statusLabel.Text = $"Script error: {ex.Message}");
                }
            }

            BeginInvoke(() => UpdatePage(document, rootBox, url));
        }
        catch (OperationCanceledException)
        {
            BeginInvoke(() => { _statusLabel.Text = "Cancelled"; Cursor = Cursors.Default; _btnStop.Enabled = false; });
        }
        catch (Exception ex)
        {
            BeginInvoke(() => ShowErrorPage("File Load Error", ex.Message));
        }
    }
}