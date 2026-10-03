using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;

namespace Retro96.Engine.Render;

/// <summary>
/// Paints the LayoutBox tree into a Bitmap using GDI+.
/// Implements NN6, NN7, NN11, NN14, NN15 quirks.
/// </summary>
public class Renderer
{
    private FontCache _fontCache;
    private ImageCache _imageCache;
    private ResourceLoader _resourceLoader;
    private string? _baseUrl;

    public Renderer(FontCache fontCache, ImageCache imageCache, ResourceLoader resourceLoader)
    {
        _fontCache = fontCache;
        _imageCache = imageCache;
        _resourceLoader = resourceLoader ?? throw new ArgumentNullException(nameof(resourceLoader));
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

    /// <summary>
    /// Checks if the given tag name is a form element that needs custom rendering.
    /// </summary>
    private static bool IsFormElement(string tagName)
    {
        return tagName is "input" or "select" or "textarea" or "button";
    }

    /// <summary>
    /// Renders a form element (input, select, textarea, button).
    /// </summary>
    private void PaintFormElement(Graphics g, LayoutBox box, FontCache fonts, DomElement? hoveredElement)
    {
        var elem = box.Element!;
        var rect = box.ContentRect;
        var style = elem.Style ?? FallbackStyle(elem);

        // Don't render hidden inputs
        if (elem.TagName == "input" && elem.GetAttrOrDefault("type", "text") == "hidden")
            return;

        // Draw background
        Color bgColor = Color.White;
        if (style.BackgroundColor != Color.Transparent && style.BackgroundColor != Color.Empty)
            bgColor = style.BackgroundColor;
        using (var bgBrush = new SolidBrush(bgColor))
            g.FillRectangle(bgBrush, rect.X, rect.Y, rect.Width, rect.Height);

        // Draw border
        PaintBorder(g, box);

        // Draw the form element content
        switch (elem.TagName)
        {
            case "input":
                PaintInput(g, box, fonts, style);
                break;
            case "select":
                PaintSelect(g, box, fonts, style);
                break;
            case "textarea":
                PaintTextarea(g, box, fonts, style);
                break;
            case var _ when elem.TagName == "input" &&
                           (elem.GetAttrOrDefault("type", "text") == "submit" ||
                            elem.GetAttrOrDefault("type", "text") == "button"):
                PaintButton(g, box, fonts, style, elem.GetAttr("value") ?? "Button");
                break;
        }
    }

    /// <summary>
    /// Renders an input element.
    /// </summary>
    private void PaintInput(Graphics g, LayoutBox box, FontCache fonts, ComputedStyle style)
    {
        var elem = box.Element!;
        var rect = box.ContentRect;
        string type = elem.GetAttrOrDefault("type", "text");

        if (type == "checkbox" || type == "radio")
        {
            PaintCheckboxOrRadio(g, box, type == "radio");
            return;
        }

        // Text input
        string value = elem.GetAttr("value") ?? "";
        if (type == "password") value = new string('*', value.Length);

        bool bold = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        var font = fonts.Resolve(style.FontFamily, style.FontSize, bold, italic);

        using var brush = new SolidBrush(style.Color);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near
        };

        // Draw text with some padding
        var textRect = new RectangleF(rect.X + 2, rect.Y, rect.Width - 4, rect.Height);
        g.DrawString(value, font, brush, textRect, sf);

        // Draw cursor (simulated as a small line at the end)
        if (!string.IsNullOrEmpty(value))
        {
            var size = g.MeasureString(value, font, int.MaxValue, sf);
            float cursorX = rect.X + 2 + size.Width;
            if (cursorX < rect.Right)
                g.DrawLine(Pens.Black, cursorX, rect.Y + 2, cursorX, rect.Bottom - 2);
        }
    }

    /// <summary>
    /// Renders a checkbox or radio button.
    /// </summary>
    private void PaintCheckboxOrRadio(Graphics g, LayoutBox box, bool isRadio)
    {
        var rect = box.ContentRect;
        float size = Math.Min(rect.Width, rect.Height);
        float x = rect.X + (rect.Width - size) / 2;
        float y = rect.Y + (rect.Height - size) / 2;

        // Draw the outer shape
        if (isRadio)
        {
            g.DrawEllipse(Pens.Black, x, y, size, size);
            // Draw filled center if checked
            if (box.Element!.HasAttr("checked"))
                g.FillEllipse(Brushes.Black, x + size * 0.25f, y + size * 0.25f, size * 0.5f, size * 0.5f);
        }
        else
        {
            g.DrawRectangle(Pens.Black, x, y, size, size);
            // Draw checkmark if checked
            if (box.Element!.HasAttr("checked"))
            {
                // Simple X checkmark
                g.DrawLine(Pens.Black, x + 2, y + 2, x + size - 2, y + size - 2);
                g.DrawLine(Pens.Black, x + size - 2, y + 2, x + 2, y + size - 2);
            }
        }
    }

    /// <summary>
    /// Renders a select dropdown.
    /// </summary>
    private void PaintSelect(Graphics g, LayoutBox box, FontCache fonts, ComputedStyle style)
    {
        var elem = box.Element!;
        var rect = box.ContentRect;

        bool bold = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        var font = fonts.Resolve(style.FontFamily, style.FontSize, bold, italic);

        // Find selected option
        var selected = elem.Descendants().OfType<DomElement>()
            .FirstOrDefault(o => o.TagName == "option" && o.HasAttr("selected"))
            ?? elem.Descendants().OfType<DomElement>()
                .FirstOrDefault(o => o.TagName == "option");

        string text = selected?.InnerText ?? "Select...";

        using var brush = new SolidBrush(style.Color);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near
        };

        var textRect = new RectangleF(rect.X + 2, rect.Y, rect.Width - 20, rect.Height);
        g.DrawString(text, font, brush, textRect, sf);

        // Draw dropdown arrow
        float arrowX = rect.Right - 15;
        float arrowY = rect.Y + rect.Height / 2;
        PointF[] arrow = new[]
        {
            new PointF(arrowX, arrowY - 4),
            new PointF(arrowX + 8, arrowY - 4),
            new PointF(arrowX + 4, arrowY + 4)
        };
        g.FillPolygon(Brushes.Black, arrow);
    }

    /// <summary>
    /// Renders a textarea.
    /// </summary>
    private void PaintTextarea(Graphics g, LayoutBox box, FontCache fonts, ComputedStyle style)
    {
        var elem = box.Element!;
        var rect = box.ContentRect;
        string text = elem.InnerText ?? "";

        bool bold = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        var font = fonts.Resolve(style.FontFamily, style.FontSize, bold, italic);

        using var brush = new SolidBrush(style.Color);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            LineAlignment = StringAlignment.Near,
            Alignment = StringAlignment.Near
        };

        var textRect = new RectangleF(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4);
        g.DrawString(text, font, brush, textRect, sf);
    }

    /// <summary>
    /// Renders a button.
    /// </summary>
    private void PaintButton(Graphics g, LayoutBox box, FontCache fonts, ComputedStyle style, string text)
    {
        var rect = box.ContentRect;

        // Draw 3D button effect (Netscape style)
        using var lightBrush = new SolidBrush(Color.LightGray);
        using var darkBrush = new SolidBrush(Color.DarkGray);

        // Top and left: light
        g.FillRectangle(lightBrush, rect.X, rect.Y, rect.Width, 2);
        g.FillRectangle(lightBrush, rect.X, rect.Y, 2, rect.Height);

        // Bottom and right: dark
        g.FillRectangle(darkBrush, rect.X, rect.Bottom - 2, rect.Width, 2);
        g.FillRectangle(darkBrush, rect.Right - 2, rect.Y, 2, rect.Height);

        // Center: gray
        g.FillRectangle(Brushes.LightGray, rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4);

        // Draw text
        bool bold = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        var font = fonts.Resolve(style.FontFamily, style.FontSize, bold, italic);

        using var brush = new SolidBrush(style.Color);
        using var sf = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Center
        };

        g.DrawString(text, font, brush, rect, sf);
    }

    /// <summary>
    /// Paint the full document. Returns a new Bitmap sized to (docWidth × docHeight).
    /// </summary>
    public Bitmap Render(LayoutBox rootBox, DomDocument document,
                           FontCache fonts, ImageCache images,
                           float viewportWidth, float viewportHeight,
                           float scrollX, float scrollY,
                           DomElement? hoveredElement,
                           bool blinkVisible,
                           bool showBoxOutlines = false)
    {
        if (rootBox == null)
            return new Bitmap(1, 1);

        // Store base URL for resolving relative image URLs
        _baseUrl = document?.BaseUrl?.ToAbsolute();

        float docWidth  = Math.Max(rootBox.Width,  viewportWidth);
        float docHeight = Math.Max(rootBox.Height, viewportHeight);

        var bmp = new Bitmap(
            (int)Math.Ceiling(docWidth),
            (int)Math.Ceiling(docHeight));

        using var g = Graphics.FromImage(bmp);

        // 1996-era: no anti-aliasing
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.SmoothingMode     = SmoothingMode.None;
        g.PixelOffsetMode   = PixelOffsetMode.None;

        // ── Background: use body bgcolor attr first, then CSS, then white ─────
        Color pageBackground = ResolveBodyBackground(document, images, g,
                                                      docWidth, docHeight);
        if (pageBackground != Color.Empty)
            g.FillRectangle(new SolidBrush(pageBackground), 0, 0, bmp.Width, bmp.Height);
        else
            g.FillRectangle(Brushes.White, 0, 0, bmp.Width, bmp.Height);

        // ── Paint layout tree ─────────────────────────────────────────────────
        // NOTE: scrollX/scrollY are NOT used for culling here — we render the
        // entire document into the bitmap; BrowserCanvas.OnPaint handles the
        // visible-viewport blit via DrawImage offset.
        PaintBox(g, rootBox, fonts, images, hoveredElement, blinkVisible);

        // ── DevTools-style box-model outline overlay (F11 in BrowserCanvas) ──
        // Drawn as a final pass on top of everything else so outlines are
        // never obscured by page content. Uses the same colour convention as
        // Chrome/Firefox devtools: margin=orange, border=red, padding=green,
        // content=blue — so it maps onto muscle memory instead of introducing
        // a new colour language to learn.
        if (showBoxOutlines)
            PaintBoxOutlines(g, rootBox);

        return bmp;
    }

    /// <summary>
    /// Recursively outlines every box's margin/border/padding/content
    /// rectangles. Skips zero-size boxes (mostly anonymous wrapper boxes
    /// with no painted content) to avoid cluttering the overlay with noise
    /// that doesn't correspond to anything visible on the page.
    /// </summary>
    private static void PaintBoxOutlines(Graphics g, LayoutBox box)
    {
        if (box.Width > 0f || box.Height > 0f)
        {
            using var marginPen  = new Pen(Color.FromArgb(160, Color.Orange), 1);
            using var borderPen  = new Pen(Color.FromArgb(200, Color.Red),    1);
            using var paddingPen = new Pen(Color.FromArgb(200, Color.Green),  1);
            using var contentPen = new Pen(Color.FromArgb(220, Color.Blue),   1);

            void DrawRect(Pen pen, RectangleF r)
            {
                if (r.Width <= 0f || r.Height <= 0f) return;
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }

            DrawRect(marginPen,  box.MarginRect);
            DrawRect(borderPen,  box.BorderRect);
            DrawRect(paddingPen, box.PaddingRect);
            DrawRect(contentPen, box.ContentRect);
        }

        foreach (var child in box.Children)
            PaintBoxOutlines(g, child);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body background resolution
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads &lt;body bgcolor&gt; / &lt;body background&gt; and also tiles the
    /// background image if present, returning the fill colour (or Color.Empty).
    /// </summary>
    private Color ResolveBodyBackground(DomDocument doc,
        ImageCache images, Graphics g, float w, float h)
    {
        var body = doc?.ElementDescendants()
                       .FirstOrDefault(e => e.TagName == "body");
        if (body == null) return Color.White;

        // bgcolor attribute (HTML 3.2)
        string? bgColor = body.GetAttr("bgcolor");
        Color fill = Color.White;
        if (!string.IsNullOrEmpty(bgColor))
        {
            try { fill = ParseHtmlColor(bgColor); } catch { }
        }
        else if (body.Style?.BackgroundColor is { } c && c != Color.Transparent)
        {
            fill = c;
        }

        // background image tile (NN11)
        string? bgAttr = body.GetAttr("background");
        string? bgImg = !string.IsNullOrEmpty(bgAttr) ? bgAttr : ParseCssUrl(body.Style?.BackgroundImage);
        if (!string.IsNullOrEmpty(bgImg))
        {
            try
            {
                var task = images.GetAsync(bgImg, _resourceLoader, default);
                if (task.IsCompletedSuccessfully && task.Result?.Frames.Count > 0)
                {
                    var frame = images.GetCurrentFrame(bgImg) ?? task.Result.Frames[0];
                    for (float y = 0; y < h; y += frame.Height)
                        for (float x = 0; x < w; x += frame.Width)
                            g.DrawImage(frame, x, y);
                }
            }
            catch { }
        }

        return fill;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Box painting  (NO viewport culling — full document render)
    // ─────────────────────────────────────────────────────────────────────────

    private void PaintBox(Graphics g, LayoutBox box, FontCache fonts,
                          ImageCache images, DomElement? hoveredElement,
                          bool blinkVisible)
    {
        if (box == null) return;

        // <blink> visibility toggle
        if (box.Element?.TagName == "blink" && !blinkVisible)
            return;

        PaintBackground(g, box, images);
        PaintBorder(g, box);
        PaintContent(g, box, fonts, images, hoveredElement);

        foreach (var child in box.Children)
            PaintBox(g, child, fonts, images, hoveredElement, blinkVisible);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Background
    // ─────────────────────────────────────────────────────────────────────────

    private void PaintBackground(Graphics g, LayoutBox box, ImageCache images)
    {
        // Anonymous boxes never have their own background
        if (box.Element == null) return;

        var style = box.Element.Style;
        if (style == null) return;

        var rect = box.BorderRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // ── Solid colour ────────────────────────────────────────────────────
        Color bg = style.BackgroundColor;

        // Honour HTML bgcolor attribute (tables, td, th, etc.)
        string? bgAttr = box.Element.GetAttr("bgcolor");
        if (!string.IsNullOrEmpty(bgAttr))
        {
            try { bg = ParseHtmlColor(bgAttr); } catch { }
        }

        if (bg != Color.Transparent && bg != Color.Empty)
        {
            using var brush = new SolidBrush(bg);
            g.FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
        }

        // ── Background image ─────────────────────────────────────────────────
        string? bgCss = style.BackgroundImage;
        if (string.IsNullOrEmpty(bgCss) || bgCss == "none") return;

        string? bgUrl = ParseCssUrl(bgCss);
        if (string.IsNullOrEmpty(bgUrl)) return;

        try
        {
            var task = images.GetAsync(bgUrl, _resourceLoader, default);
            if (!task.IsCompletedSuccessfully) return;
            var decoded = task.Result;
            if (decoded?.Frames.Count > 0)
                PaintBackgroundImage(g, rect, images.GetCurrentFrame(bgUrl) ?? decoded.Frames[0], style);
        }
        catch { }
    }

    private static void PaintBackgroundImage(Graphics g, RectangleF rect,
                                              Image image, ComputedStyle style)
    {
        float iw = image.Width;
        float ih = image.Height;
        if (iw <= 0 || ih <= 0) return;

        float posX   = style.BackgroundPosition.X;
        float posY   = style.BackgroundPosition.Y;
        float startX = rect.X + (posX / 100f) * (rect.Width  - iw);
        float startY = rect.Y + (posY / 100f) * (rect.Height - ih);

        var repeat = style.BackgroundRepeat;

        using var clip = new Region(rect);
        g.SetClip(clip, CombineMode.Replace);

        switch (repeat)
        {
            case BackgroundRepeat.NoRepeat:
                g.DrawImage(image, startX, startY, iw, ih);
                break;

            case BackgroundRepeat.RepeatX:
                for (float x = startX; x < rect.Right; x += iw)
                    g.DrawImage(image, x, startY, iw, ih);
                break;

            case BackgroundRepeat.RepeatY:
                for (float y = startY; y < rect.Bottom; y += ih)
                    g.DrawImage(image, startX, y, iw, ih);
                break;

            default: // Repeat
                for (float y = startY; y < rect.Bottom; y += ih)
                    for (float x = startX; x < rect.Right; x += iw)
                        g.DrawImage(image, x, y, iw, ih);
                break;
        }

        g.ResetClip();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Border
    // ─────────────────────────────────────────────────────────────────────────

    private static void PaintBorder(Graphics g, LayoutBox box)
    {
        if (box.Element == null) return;
        var style = box.Element.Style;
        if (style == null) return;

        // <hr> — always draw as 3-D inset rule (NN7)
        if (box.Element.TagName == "hr")
        {
            PaintHr(g, box);
            return;
        }

        PaintBorderSideFull(g, box, style.BorderTopStyle,    style.BorderTopWidth,
                            style.BorderTopColor,    BorderSide.Top);
        PaintBorderSideFull(g, box, style.BorderRightStyle,  style.BorderRightWidth,
                            style.BorderRightColor,  BorderSide.Right);
        PaintBorderSideFull(g, box, style.BorderBottomStyle, style.BorderBottomWidth,
                            style.BorderBottomColor, BorderSide.Bottom);
        PaintBorderSideFull(g, box, style.BorderLeftStyle,   style.BorderLeftWidth,
                            style.BorderLeftColor,   BorderSide.Left);
    }

    /// <summary>Draws a &lt;hr&gt; as an inset 3-D rule, honouring noshade / size / width.</summary>
    private static void PaintHr(Graphics g, LayoutBox box)
    {
        bool noshade = box.Element!.HasAttr("noshade");
        int  thick   = box.Element.GetAttrInt("size", 2);
        thick = Math.Max(1, thick);

        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;

        // Respect HTML width attribute (already resolved in layout)
        float ruleW = box.Width;
        float x1    = cx - ruleW / 2f;
        float y1    = cy - thick / 2f;

        if (noshade)
        {
            using var pen = new Pen(Color.Gray, thick);
            g.DrawLine(pen, x1, cy, x1 + ruleW, cy);
        }
        else
        {
            // 3-D inset: dark top/left, light bottom/right
            using var penDark  = new Pen(Color.FromArgb(128, 128, 128), 1);
            using var penLight = new Pen(Color.White, 1);
            for (int i = 0; i < thick; i++)
            {
                float y = y1 + i;
                g.DrawLine(i < thick / 2 + 1 ? penDark : penLight,
                           x1, y, x1 + ruleW, y);
            }
        }
    }

    private enum BorderSide { Top, Right, Bottom, Left }

    private static void PaintBorderSideFull(Graphics g, LayoutBox box,
        BorderStyleValue bStyle, float width, Color color, BorderSide side)
    {
        if (width <= 0 || bStyle == BorderStyleValue.None ||
            bStyle == BorderStyleValue.Hidden) return;

        var rect = box.BorderRect;

        if (bStyle is BorderStyleValue.Groove or BorderStyleValue.Ridge
                   or BorderStyleValue.Inset  or BorderStyleValue.Outset)
        { Paint3DBorder(g, rect, bStyle, width, color, side); return; }

        if (bStyle == BorderStyleValue.Double)
        { PaintDoubleBorder(g, rect, width, color, side); return; }

        using var pen = CreateBorderPen(bStyle, width, color);
        switch (side)
        {
            case BorderSide.Top:
                g.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Top); break;
            case BorderSide.Right:
                g.DrawLine(pen, rect.Right, rect.Top, rect.Right, rect.Bottom); break;
            case BorderSide.Bottom:
                g.DrawLine(pen, rect.Left, rect.Bottom, rect.Right, rect.Bottom); break;
            case BorderSide.Left:
                g.DrawLine(pen, rect.Left, rect.Top, rect.Left, rect.Bottom); break;
        }
    }

    private static void Paint3DBorder(Graphics g, RectangleF rect,
        BorderStyleValue style, float width, Color baseColor, BorderSide side)
    {
        if (width < 1) return;
        int w = Math.Max(1, (int)Math.Round(width));

        Color light, dark;
        switch (style)
        {
            case BorderStyleValue.Groove:
                light = ControlPaint.Light(baseColor);
                dark  = ControlPaint.Dark(baseColor); break;
            case BorderStyleValue.Ridge:
                light = ControlPaint.Dark(baseColor);
                dark  = ControlPaint.Light(baseColor); break;
            case BorderStyleValue.Inset:
                light = ControlPaint.LightLight(baseColor);
                dark  = ControlPaint.DarkDark(baseColor); break;
            default: // Outset
                light = ControlPaint.DarkDark(baseColor);
                dark  = ControlPaint.LightLight(baseColor); break;
        }

        using var penL = new Pen(light, 1);
        using var penD = new Pen(dark, 1);

        switch (side)
        {
            case BorderSide.Top:
                for (int i = 0; i < w; i++)
                {
                    float y = rect.Top + i;
                    g.DrawLine(i < w / 2 ? penL : penD, rect.Left, y, rect.Right, y);
                }
                break;
            case BorderSide.Bottom:
                for (int i = 0; i < w; i++)
                {
                    float y = rect.Bottom - i - 1;
                    g.DrawLine(i < w / 2 ? penD : penL, rect.Left, y, rect.Right, y);
                }
                break;
            case BorderSide.Left:
                for (int i = 0; i < w; i++)
                {
                    float x = rect.Left + i;
                    g.DrawLine(i < w / 2 ? penL : penD, x, rect.Top, x, rect.Bottom);
                }
                break;
            case BorderSide.Right:
                for (int i = 0; i < w; i++)
                {
                    float x = rect.Right - i - 1;
                    g.DrawLine(i < w / 2 ? penD : penL, x, rect.Top, x, rect.Bottom);
                }
                break;
        }
    }

    private static void PaintDoubleBorder(Graphics g, RectangleF rect,
        float width, Color color, BorderSide side)
    {
        int w = (int)Math.Round(width);
        if (w < 3) return;
        int lw = Math.Max(1, w / 3);
        int gap = Math.Max(1, w / 3);

        using var pen = new Pen(color, 1);
        switch (side)
        {
            case BorderSide.Top:
                for (int i = 0; i < lw; i++)
                    g.DrawLine(pen, rect.Left, rect.Top + i, rect.Right, rect.Top + i);
                for (int i = 0; i < lw; i++)
                {
                    float y = rect.Top + lw + gap + i;
                    if (y < rect.Bottom)
                        g.DrawLine(pen, rect.Left, y, rect.Right, y);
                }
                break;
            case BorderSide.Bottom:
                for (int i = 0; i < lw; i++)
                {
                    float y = rect.Bottom - lw - gap - lw + i;
                    if (y >= rect.Top)
                        g.DrawLine(pen, rect.Left, y, rect.Right, y);
                }
                for (int i = 0; i < lw; i++)
                {
                    float y = rect.Bottom - lw + i;
                    if (y < rect.Bottom)
                        g.DrawLine(pen, rect.Left, y, rect.Right, y);
                }
                break;
            case BorderSide.Left:
                for (int i = 0; i < lw; i++)
                    g.DrawLine(pen, rect.Left + i, rect.Top, rect.Left + i, rect.Bottom);
                for (int i = 0; i < lw; i++)
                {
                    float x = rect.Left + lw + gap + i;
                    if (x < rect.Right)
                        g.DrawLine(pen, x, rect.Top, x, rect.Bottom);
                }
                break;
            case BorderSide.Right:
                for (int i = 0; i < lw; i++)
                {
                    float x = rect.Right - lw - gap - lw + i;
                    if (x >= rect.Left)
                        g.DrawLine(pen, x, rect.Top, x, rect.Bottom);
                }
                for (int i = 0; i < lw; i++)
                {
                    float x = rect.Right - lw + i;
                    if (x < rect.Right)
                        g.DrawLine(pen, x, rect.Top, x, rect.Bottom);
                }
                break;
        }
    }

    private static Pen CreateBorderPen(BorderStyleValue style, float width, Color color)
    {
        var pen = new Pen(color, Math.Max(1, width));
        switch (style)
        {
            case BorderStyleValue.Dashed:
                pen.DashStyle   = DashStyle.Dash;
                pen.DashPattern = new float[] { 6, 3 }; break;
            case BorderStyleValue.Dotted:
                pen.DashStyle   = DashStyle.Dot;
                pen.DashPattern = new float[] { 2, 2 }; break;
        }
        return pen;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Content
    // ─────────────────────────────────────────────────────────────────────────

    private void PaintContent(Graphics g, LayoutBox box, FontCache fonts,
                               ImageCache images, DomElement? hoveredElement)
    {
        // ── Replaced elements (images, form controls) ──────────────────────
        if (box.BoxType == BoxType.Replaced && box.Element != null)
        {
            if (box.Element.TagName == "img")
            {
                PaintImage(g, box, images);
                return;
            }

            // Form elements
            if (IsFormElement(box.Element.TagName))
            {
                PaintFormElement(g, box, fonts, hoveredElement);
                return;
            }
        }

        // ── List bullet / number ───────────────────────────────────────────
        if (box.BoxType == BoxType.ListItem && box.Element != null)
        {
            PaintListMarker(g, box, fonts);
        }

        // ── Text run ────────────────────────────────────────────────────────
        if (string.IsNullOrEmpty(box.TextRun)) return;

        // Text boxes carry their parent element's style
        var elemForStyle = box.Element;
        if (elemForStyle == null) return;
        var style = elemForStyle.Style ?? FallbackStyle(elemForStyle);
        if (style == null) return;

        bool bold   = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle  == FontStyleValue.Italic;
        var  font   = fonts.Resolve(style.FontFamily, style.FontSize, bold, italic);

        // ── Colour: walk up to find enclosing <a> ──────────────────────────
        Color textColor = style.Color;
        DomElement? linkAnchor = FindAnchorAncestor(elemForStyle);
        if (linkAnchor != null)
        {
            // Resolve link colour from <body link="..." vlink="..." alink="...">
            // (StyleResolver stores these per-document; fall back to UA defaults)
            var ownerDoc = FindOwnerDocument(elemForStyle);
            Color linkColor   = ownerDoc != null
                ? Css.StyleResolver.GetLinkColor(ownerDoc)
                : Color.FromArgb(0, 0, 0xEE);
            Color alinkColor  = ownerDoc != null
                ? Css.StyleResolver.GetALinkColor(ownerDoc)
                : Color.FromArgb(255, 0, 0);

            textColor = linkAnchor == hoveredElement ? alinkColor : linkColor;
        }

        // Override with explicit HTML color attr on enclosing <font>
        string? fontColor = FindFontColor(elemForStyle);
        if (fontColor != null)
        {
            try { textColor = ParseHtmlColor(fontColor); } catch { }
        }

        var contentRect = box.ContentRect;

        using var brush = new SolidBrush(textColor);
        using var sf    = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags   = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming      = StringTrimming.None,
            LineAlignment = StringAlignment.Near,
            Alignment     = StringAlignment.Near
        };

        g.DrawString(box.TextRun, font, brush, contentRect.X, contentRect.Y, sf);

        // ── Text decoration ────────────────────────────────────────────────
        var deco = style.TextDecoration;
        if (linkAnchor != null) deco |= TextDecoration.Underline; // links always underline

        if (deco != TextDecoration.None)
        {
            using var decoP = new Pen(textColor, 1);
            var sz = g.MeasureString(box.TextRun, font, int.MaxValue, sf);

            if (deco.HasFlag(TextDecoration.Underline))
            {
                float uy = contentRect.Y + font.GetHeight(g) - 1;
                g.DrawLine(decoP, contentRect.X, uy, contentRect.X + sz.Width, uy);
            }
            if (deco.HasFlag(TextDecoration.LineThrough))
            {
                float sy = contentRect.Y + font.GetHeight(g) / 2f;
                g.DrawLine(decoP, contentRect.X, sy, contentRect.X + sz.Width, sy);
            }
            if (deco.HasFlag(TextDecoration.Overline))
            {
                float oy = contentRect.Y;
                g.DrawLine(decoP, contentRect.X, oy, contentRect.X + sz.Width, oy);
            }
        }
    }

    // ── Image rendering ───────────────────────────────────────────────────────

    private void PaintImage(Graphics g, LayoutBox box, ImageCache images)
    {
        var elem = box.Element!;
        string? src = elem.GetAttr("src");

        // Try to get from cache (resolve relative URLs to absolute)
        if (!string.IsNullOrEmpty(src))
        {
            try
            {
                // Resolve relative URL to absolute using the document's base URL
                string absoluteUrl = ImageCache.ResolveUrl(src, _baseUrl);
                var task = images.GetAsync(absoluteUrl, _resourceLoader, default);
                if (task.IsCompletedSuccessfully && task.Result?.Frames.Count > 0)
                {
                    var frame = images.GetCurrentFrame(absoluteUrl) ?? task.Result.Frames[0];
                    var rect  = box.ContentRect;

                    // Scale to box dimensions if set, otherwise use intrinsic
                    float dw = rect.Width  > 0 ? rect.Width  : frame.Width;
                    float dh = rect.Height > 0 ? rect.Height : frame.Height;

                    g.DrawImage(frame, rect.X, rect.Y, dw, dh);
                    return;
                }
            }
            catch { }
        }

        // Broken image placeholder: small grey rect + 'X'
        DrawBrokenImage(g, box);
    }

    private static void DrawBrokenImage(Graphics g, LayoutBox box)
    {
        var r    = box.ContentRect;
        float bw = Math.Max(16, r.Width  > 0 ? r.Width  : 32);
        float bh = Math.Max(16, r.Height > 0 ? r.Height : 32);
        var  br  = new RectangleF(r.X, r.Y, bw, bh);

        g.FillRectangle(Brushes.LightGray, br);
        g.DrawRectangle(Pens.DarkGray, br.X, br.Y, br.Width - 1, br.Height - 1);

        // Draw broken image icon (simple X)
        using var p = new Pen(Color.DarkGray, 1);
        g.DrawLine(p, br.X + 2, br.Y + 2, br.Right - 3, br.Bottom - 3);
        g.DrawLine(p, br.Right - 3, br.Y + 2, br.X + 2, br.Bottom - 3);

        // Alt text
        string? alt = box.Element?.GetAttr("alt");
        if (!string.IsNullOrEmpty(alt) && bw > 30)
        {
            using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
            using var font = SystemFonts.SmallCaptionFont ?? SystemFonts.DefaultFont;
            g.DrawString(alt, font, Brushes.DarkGray, br, sf);
        }
    }

    // ── List marker ───────────────────────────────────────────────────────────

    private static void PaintListMarker(Graphics g, LayoutBox box, FontCache fonts)
    {
        if (box.Element == null) return;

        // Determine list type from parent <ul> or <ol>
        bool isOrdered = false;
        int  index     = 1;
        var  parent    = box.Element.Parent as DomElement;
        if (parent?.TagName == "ol") isOrdered = true;

        // Count preceding <li> siblings to get ordinal
        if (isOrdered && parent != null)
        {
            foreach (var sib in parent.Children)
            {
                if (sib == box.Element) break;
                if (sib is DomElement de && de.TagName == "li") index++;
            }
        }

        var style    = box.Element.Style ?? FallbackStyle(box.Element);
        float fSize  = style.FontSize;
        var   font   = fonts.Resolve(style.FontFamily, fSize, false, false);
        float markerX = box.X - 20;   // bullet is 20px left of content
        float markerY = box.Y + box.BorderTop + box.PaddingTop;

        using var brush = new SolidBrush(style?.Color ?? Color.Black);

        if (isOrdered)
        {
            using var sf = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString($"{index}.", font, brush, markerX + 16, markerY);
        }
        else
        {
            // Filled disc
            float r = Math.Max(2, fSize * 0.2f);
            float cx = markerX + 12;
            float cy = markerY + fSize * 0.4f;
            g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Walks ancestors to find an enclosing &lt;a&gt; element.</summary>
    private static DomElement? FindAnchorAncestor(DomElement elem)
    {
        var node = elem.Parent;
        while (node != null)
        {
            if (node is DomElement de)
            {
                if (de.TagName == "a") return de;
                if (de.TagName is "body" or "html") break;
            }
            node = node.Parent;
        }
        return null;
    }

    /// <summary>Walks ancestors to reach the DomDocument root.</summary>
    private static DomDocument? FindOwnerDocument(DomElement elem)
    {
        DomNode? node = elem.Parent;
        while (node != null)
        {
            if (node is DomDocument doc) return doc;
            node = node.Parent;
        }
        return null;
    }

    /// <summary>Walks ancestors to find a &lt;font color="..."&gt; attribute.</summary>
    private static string? FindFontColor(DomElement elem)
    {
        var node = elem.Parent;
        while (node != null)
        {
            if (node is DomElement de)
            {
                if (de.TagName == "font" && de.HasAttr("color"))
                    return de.GetAttr("color");
                if (de.TagName is "body" or "html") break;
            }
            node = node.Parent;
        }
        return null;
    }

    /// <summary>Minimal fallback style for elements with no ComputedStyle.</summary>
    private static ComputedStyle FallbackStyle(DomElement elem)
    {
        var s = new ComputedStyle();
        s.Color = Color.Black;
        s.FontFamily = new List<string> { "Times New Roman", "serif" };
        s.FontSize = 16f;
        s.Display = DisplayValue.Inline;
        return s;
    }

    /// <summary>Parses #rrggbb, #rgb, and named HTML colours.</summary>
    internal static Color ParseHtmlColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Color.Empty;

        try
        {
            value = value.Trim();
            // Expand #rgb to #rrggbb if needed
            if (value.StartsWith("#", StringComparison.Ordinal) && value.Length == 4)
            {
                var r = value[1];
                var g = value[2];
                var b = value[3];
                value = $"#{r}{r}{g}{g}{b}{b}";
            }
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return Color.Empty;
        }
    }
}