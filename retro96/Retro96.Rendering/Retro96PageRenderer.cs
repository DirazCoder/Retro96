using SkiaSharp;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;

namespace Retro96.Rendering;

/// <summary>
/// Parses, lays out, and paints an HTML document onto a caller-owned Skia canvas.
/// Create one instance per independently rendered page and dispose it when done.
/// </summary>
public sealed class Retro96PageRenderer : IDisposable
{
    private readonly CookieStore _cookies;
    private readonly ResourceLoader _resources;
    private readonly ImageCache _images;
    private readonly FontCache _fonts;
    private readonly Renderer _renderer;
    private DomDocument? _document;
    private LayoutBox? _layout;
    private bool _disposed;

    /// <summary>
    /// Raised when an image finishes loading and the page should be painted again.
    /// </summary>
    public event EventHandler? Invalidated;

    /// <summary>The parsed document from the most recent successful LoadHtml call.</summary>
    public DomDocument? Document => _document;

    /// <summary>The layout tree from the most recent successful LoadHtml call.</summary>
    public LayoutBox? Layout => _layout;

    /// <summary>
    /// Creates a renderer. If settings are supplied, they become the engine's
    /// process-wide browser settings, matching the engine's current runtime model.
    /// </summary>
    public Retro96PageRenderer(UserSettings? settings = null)
    {
        if (settings != null)
            BrowserRuntime.Apply(settings);

        _cookies = new CookieStore();
        _resources = new ResourceLoader(_cookies);
        _images = new ImageCache { CookieStore = _cookies };
        _fonts = new FontCache();
        _renderer = new Renderer(_fonts, _images, _resources);

        _images.ImageLoaded += OnImageChanged;
        _images.ImageRecovered += OnImageChanged;
        InlineLayout.SetFontCache(_fonts);
    }

    /// <summary>
    /// Parses, styles, and lays out HTML for a logical viewport. The base URL
    /// is used to resolve relative links and image URLs; it defaults to about:blank.
    /// Scripts are not executed by this static rendering API.
    /// </summary>
    public void LoadHtml(string html, float viewportWidth, float viewportHeight,
        string baseUrl = "about:blank")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ValidateViewport(viewportWidth, viewportHeight);

        var parsedBaseUrl = ParsedUrl.Parse(baseUrl);
        DomDocument document = HtmlParser.Parse(html, parsedBaseUrl, _cookies);
        StyleResolver.Resolve(document, viewportWidth);
        LayoutBox layout = LayoutEngine.BuildLayoutTree(
            document, viewportWidth, viewportHeight);

        _document = document;
        _layout = layout;
    }

    /// <summary>
    /// Paints the current page directly onto a caller-owned Skia canvas.
    /// The canvas remains owned by the caller and is not disposed or retained.
    /// </summary>
    public void RenderToCanvas(SKCanvas canvas, float viewportWidth, float viewportHeight,
        float scrollX = 0, float scrollY = 0, float renderScale = 1,
        bool clearBackground = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ValidateViewport(viewportWidth, viewportHeight);

        DomDocument document = _document
            ?? throw new InvalidOperationException("Call LoadHtml before rendering.");
        LayoutBox layout = _layout
            ?? throw new InvalidOperationException("Call LoadHtml before rendering.");

        _renderer.RenderToCanvas(
            canvas, layout, document, _fonts, _images,
            viewportWidth, viewportHeight, scrollX, scrollY,
            hoveredElement: null, blinkVisible: true,
            renderScale: renderScale, clearBackground: clearBackground);
    }

    /// <summary>
    /// Releases this page's image, font, and network-resource caches.
    /// The supplied canvas is never owned by this renderer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _images.ImageLoaded -= OnImageChanged;
        _images.ImageRecovered -= OnImageChanged;
        _images.Dispose();
        _resources.Dispose();
        _fonts.Dispose();
    }

    private void OnImageChanged(string _) => Invalidated?.Invoke(this, EventArgs.Empty);

    private static void ValidateViewport(float width, float height)
    {
        if (!float.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport width must be finite and positive.");
        if (!float.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Viewport height must be finite and positive.");
    }
}
