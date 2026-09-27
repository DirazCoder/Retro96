using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Linq;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using ControlPaint = Retro96.Drawing.ControlPaint;

namespace Retro96.Engine.Render;

/// <summary>
/// Paints the LayoutBox tree into a Bitmap using GDI+.
///
/// The whole document is rendered at document size (no viewport culling —
/// the shell blits the visible region).  Text uses ClearType antialiasing; thin era rules remain pixel-crisp; nothing else is resampled.  The output bitmap is capped
/// (see MaxSurfaceDimension/MaxSurfacePixels) so hostile markup cannot turn
/// into a multi-gigabyte allocation.
///
/// Quirks: NN-BLINK toggling (any box whose element descends from a
/// &lt;blink&gt; — the layout engine flattens the wrapper), HR 3-D rules with
/// NOSHADE, table cell rules in grey with an outset table frame, list
/// markers honouring UL/OL/LI TYPE + START + VALUE + nesting depth,
/// VLINK/ALINK link colours from &lt;body&gt;, LOWSRC placeholders, the 2-px
/// default border on linked images, and a sunken frame placeholder for
/// frame boxes whose child documents the shell paints over them.
/// </summary>
public class Renderer
{
    private static readonly List<string> DefaultFontFamily = ["Times New Roman", "serif"];
    private static readonly List<string> MonospaceFamily = ["Courier New", "monospace"];

    /// <summary>Hard cap per bitmap edge — hostile width="99999999" markup
    /// used to reach here as a real allocation request. Kept high enough
    /// that an ordinary long single-column page (a personal homepage full
    /// of stacked tables/images can easily run past 8-9k px tall) never
    /// hits it; MaxSurfacePixels below is the real memory backstop, since
    /// it bounds total allocation regardless of aspect ratio.</summary>
    private const int MaxSurfaceDimension = 32768;

    /// <summary>Hard cap on total pixels (~128 MB BGRA).</summary>
    private const long MaxSurfacePixels = 33_554_432;

    /// <summary>Cap on background-tile draws per element — a tiny tiled
    /// background on a huge rect used to be an unbounded loop.</summary>
    private const int MaxBackgroundTiles = 100_000;

    // Shared, immutable formats (DrawString never mutates a StringFormat):
    // one per paint used to allocate a fresh format for EVERY WORD BOX.
    private static readonly StringFormat TypographicFormat = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.None,
        LineAlignment = StringAlignment.Near,
        Alignment = StringAlignment.Near
    };

    private static readonly StringFormat ButtonFormat = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
                    | StringFormatFlags.MeasureTrailingSpaces,
        LineAlignment = StringAlignment.Center,
        Alignment = StringAlignment.Center,
        Trimming = StringTrimming.None
    };

    // Small shared brush/pen pools: text runs and backgrounds repeat the
    // same few colours per page; the old per-box `new SolidBrush` churned
    // GDI objects on every paint.  Capped — a hostile page cycling colours
    // cannot grow it unboundedly (cap hit = dispose-all + rebuild).
    private static readonly Dictionary<Color, SolidBrush> _solidBrushes = new();
    private static readonly Dictionary<Color, Pen> _solidPens = new();

    private static SolidBrush SolidBrushFor(Color c)
    {
        if (_solidBrushes.TryGetValue(c, out var b)) return b;
        if (_solidBrushes.Count > 64)
        {
            foreach (var pb in _solidBrushes.Values) pb.Dispose();
            _solidBrushes.Clear();
        }
        b = new SolidBrush(c);
        _solidBrushes[c] = b;
        return b;
    }

    private static Pen SolidPenFor(Color c)
    {
        if (_solidPens.TryGetValue(c, out var p)) return p;
        if (_solidPens.Count > 64)
        {
            foreach (var pp in _solidPens.Values) pp.Dispose();
            _solidPens.Clear();
        }
        p = new Pen(c, 1);
        _solidPens[c] = p;
        return p;
    }

    /// <summary>Control currently held down — painted with the Win95
    /// pressed-in bevel.</summary>
    public DomElement? PressedElement { get; set; }

    // Textareas keep their scroll position as control-local state in the shell.
    // The renderer is deliberately agnostic about focus ownership, so the
    // shell supplies the current scroll offsets and whether the scrollbar is
    // visible for each control.
    public Func<DomElement, (int ScrollLine, float ScrollX, bool ShowScrollbar)>? TextareaStateResolver { get; set; }

    // Multiple-select listboxes keep their option scroll position in the shell,
    // just like textarea scrolling. This resolver keeps the renderer stateless.
    public Func<DomElement, int>? SelectScrollResolver { get; set; }

    private readonly ResourceLoader _resourceLoader;
    private string? _baseUrl;
    private float _scrollX, _scrollY;

    // fontCache/imageCache are accepted for shell API compatibility; the
    // per-call Render(...) arguments are the ones used for painting.
    public Renderer(FontCache fontCache, ImageCache imageCache, ResourceLoader resourceLoader)
    {
        _resourceLoader = resourceLoader ?? throw new ArgumentNullException(nameof(resourceLoader));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Entry point
    // ─────────────────────────────────────────────────────────────────────

    public Bitmap Render(LayoutBox rootBox, DomDocument document,
                          FontCache fonts, ImageCache images,
                          float viewportWidth, float viewportHeight,
                          float scrollX, float scrollY,
                          DomElement? hoveredElement,
                          bool blinkVisible,
                          bool showBoxOutlines = false,
                          DomElement? focusedElement = null)
    {
        if (rootBox == null)
            return new Bitmap(1, 1);

        _baseUrl = document?.BaseUrl?.ToAbsolute();
        _scrollX = scrollX;
        _scrollY = scrollY;

        float docWidth = Math.Max(rootBox.Width, viewportWidth);
        float docHeight = Math.Max(rootBox.Height, viewportHeight);

        // FIX: the bitmap size is now CAPPED.  A hostile width/height attr
        // or CSS percent chain used to arrive here as e.g. 100000×80000 —
        // a guaranteed multi-gigabyte GDI+ allocation (instant OOM/DoS).
        // Content beyond the cap is simply not rendered; the shell's
        // scrollbars size to the capped bitmap.
        int bw = Math.Max(1, (int)Math.Ceiling(docWidth));
        int bh = Math.Max(1, (int)Math.Ceiling(docHeight));
        if (bw > MaxSurfaceDimension) bw = MaxSurfaceDimension;
        if (bh > MaxSurfaceDimension) bh = MaxSurfaceDimension;
        if ((long)bw * bh > MaxSurfacePixels)
        {
            if (bw >= bh) bw = (int)Math.Max(1, MaxSurfacePixels / bh);
            else bh = (int)Math.Max(1, MaxSurfacePixels / bw);
        }

        var bmp = new Bitmap(bw, bh);

        using var g = Graphics.FromImage(bmp);

        g.TextRenderingHint = Retro96.Drawing.TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        // ── Page background: colour fill FIRST, then the tiled image on
        //    top (the old order painted the tiles and then blanked them
        //    with the fill).
        Color pageBackground = BrowserRuntime.ResolveBackground(ResolveBodyBackgroundColor(document));
        using (var bgBrush = new SolidBrush(pageBackground))
        {
            g.FillRectangle(bgBrush, 0, 0, bmp.Width, bmp.Height);
        }
        PaintBodyBackgroundImage(document, images, g, bmp.Width, bmp.Height);

        try
        {
            PaintBox(g, rootBox, fonts, images, hoveredElement, blinkVisible, focusedElement);

            if (showBoxOutlines)
                PaintBoxOutlines(g, rootBox);
        }
        catch (Exception ex)
        {
            // One bad box used to abort the WHOLE document paint after the
            // bitmap was created — the shell then saw Render throw, nulled
            // its bitmap, and the page came out completely blank.  Draw a
            // visible notice and keep the partially-painted bitmap instead.
            Retro96.DebugLog.WriteException("Renderer.Render/PaintBox", ex);
            using var errFont = new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Regular,
                GraphicsUnit.Pixel);
            using var errBrush = new SolidBrush(Color.FromArgb(0x80, 0x00, 0x00));
            g.DrawString(
                $"Rendering stopped part-way: {ex.Message}",
                errFont, errBrush, 8f, Math.Max(8f, bmp.Height - 24f));
        }

        return bmp;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Body background
    // ─────────────────────────────────────────────────────────────────────

    private static Color ResolveBodyBackgroundColor(DomDocument? doc)
    {
        var body = doc?.ElementDescendants()
                       .FirstOrDefault(e => e.TagName == "body");
        if (body == null) return Color.White;

        Color fill = Color.White;

        string? bgColor = body.GetAttr("bgcolor");
        if (!string.IsNullOrEmpty(bgColor))
        {
            Color parsed = ParseHtmlColor(bgColor);
            if (parsed != Color.Empty) fill = parsed;
        }
        else if (body.Style?.BackgroundColor is { } c &&
                 c != Color.Transparent && c != Color.Empty)
        {
            fill = c;
        }

        return fill;
    }

    private void PaintBodyBackgroundImage(DomDocument? doc,
        ImageCache images, Graphics g, float w, float h)
    {
        if (!BrowserRuntime.ImagesEnabled ||
            BrowserRuntime.Settings.BackgroundMode == BackgroundMode.Force) return;

        var body = doc?.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        if (body == null) return;

        string? bgAttr = body.GetAttr("background");
        string? bgImg = !string.IsNullOrEmpty(bgAttr) ? bgAttr
            : ParseCssUrl(body.Style?.BackgroundImage);
        if (string.IsNullOrEmpty(bgImg)) return;

        try
        {
            string absolute = ImageCache.ResolveUrl(bgImg, _baseUrl);
            var task = images.GetAsync(absolute, _resourceLoader, default);
            if (images.IsBroken(absolute) || !task.IsCompletedSuccessfully)
                return;
            var decoded = task.Result;
            if (decoded == null || decoded.Frames.Count <= 0)
                return;
            var frame = images.GetCurrentFrame(absolute) ?? decoded.Frames[0];
            if (frame.Width <= 0 || frame.Height <= 0) return;

            var style = body.Style?.Clone() ?? new ComputedStyle();
            style.BackgroundImage = bgImg;
            if (string.IsNullOrEmpty(body.Style?.BackgroundImage))
                style.BackgroundRepeat = BackgroundRepeat.Repeat;
            PaintBackgroundImage(g, new RectangleF(0, 0, w, h), frame, style);
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Box painting
    // ─────────────────────────────────────────────────────────────────────

    private static bool IsSameOrAncestor(DomElement candidate, DomElement? stateElement)
    {
        for (DomNode? node = stateElement; node != null; node = node.Parent)
            if (ReferenceEquals(node, candidate)) return true;
        return false;
    }

    private static IEnumerable<LayoutBox> PaintOrder(LayoutBox box)
    {
        return box.Children.Select((child, index) => (child, index))
            .OrderBy(p => p.child.Element?.Style?.ZIndex ?? 0)
            .ThenBy(p => p.index)
            .Select(p => p.child);
    }

    private void PaintBox(Graphics g, LayoutBox box, FontCache fonts,
                          ImageCache images, DomElement? hoveredElement,
                          bool blinkVisible, DomElement? focusedElement = null)
    {
        if (box == null) return;

        if (box.Element?.Style?.Visibility == VisibilityValue.Hidden)
            return;

        // <blink> content toggles on the shell's timer.  The layout engine
        // flattens blink wrappers, so any box whose element DESCENDS from a
        // <blink> must toggle — not just boxes owned by the blink element
        // itself.
        if (!blinkVisible && InsideBlink(box.Element))
            return;

        // Frame/iframe: the shell paints the child document into this
        // rect — leave a sunken placeholder border here
        if (box.BoxType == BoxType.Frame)
        {
            PaintFramePlaceholder(g, box);
            return;
        }

        // Text fragments created for a block element keep the owning element
        // on LayoutBox so they inherit its text style. They are not the
        // element's principal box. Repainting the block background for each
        // word resets a tiled image's origin and creates stray colour patches
        // behind individual words.
        bool isDecorationFragment = box.BoxType == BoxType.Inline &&
            box.Element?.Style?.Display is not null &&
            box.Element.Style.Display != DisplayValue.Inline;
        if (!isDecorationFragment)
        {
            PaintBackground(g, box, images);
            PaintBorder(g, box);
        }
        PaintContent(g, box, fonts, images, hoveredElement, focusedElement);

        // <marquee> — IE/NN extension: the block lays out normally, but the
        // content is repainted through a horizontal scroll transform clipped
        // to the marquee's border box.
        if (box.Element?.TagName == "marquee" && BrowserRuntime.MarqueeEnabled)
        {
            PaintMarqueeContent(g, box, fonts, images, hoveredElement,
                blinkVisible, focusedElement);
            return;
        }

        var overflow = box.Element?.Style?.Overflow ?? OverflowValue.Visible;
        int clipState = 0;
        if (overflow != OverflowValue.Visible)
        {
            clipState = g.Save();
            g.SetClip(new RectangleF(
                box.X + box.BorderLeft,
                box.Y + box.BorderTop,
                box.Width + box.PaddingLeft + box.PaddingRight,
                box.Height + box.PaddingTop + box.PaddingBottom),
                CombineMode.Intersect);
        }

        foreach (var child in PaintOrder(box))
        {
            // FIX: one bad subtree used to abort every sibling after it
            // (the exception unwound straight to the root catch).  Skip the
            // failure, log it, keep painting the rest of the page.
            try
            {
                PaintBox(g, child, fonts, images, hoveredElement, blinkVisible, focusedElement);
            }
            catch (Exception ex)
            {
                Retro96.DebugLog.WriteException("Renderer.PaintBox(child)", ex);
            }
        }

        if (overflow != OverflowValue.Visible)
            g.Restore(clipState);
    }

    // ── Marquee ─────────────────────────────────────────────────────

    /// <summary>
    /// Paints a &lt;marquee&gt;'s children shifted by the current scroll phase.
    /// Supports BEHAVIOR=scroll (default) / alternate / slide, DIRECTION=
    /// left (default) / right, SCROLLAMOUNT (px per move, default 6) and
    /// SCROLLDELAY (ms per move, default 85). The phase derives from
    /// wall-clock time so the speed is exact even when repaints are sparse.
    /// </summary>
    private void PaintMarqueeContent(Graphics g, LayoutBox box, FontCache fonts,
                                     ImageCache images, DomElement? hoveredElement,
                                     bool blinkVisible, DomElement? focusedElement)
    {
        var elem = box.Element!;
        var rect = box.ContentRect;
        if (rect.Width <= 0f || rect.Height <= 0f) return;

        // BGCOLOR face (era default: no fill unless given)
        string? bg = elem.GetAttr("bgcolor");
        if (!string.IsNullOrEmpty(bg))
        {
            Color c = ParseHtmlColor(bg);
            if (c != Color.Empty)
                using (var brush = new SolidBrush(c))
                    g.FillRectangle(brush, rect);
        }

        // Content extent laid out at the marquee's width
        float contentW = 0f;
        foreach (var child in box.Children)
            contentW = Math.Max(contentW,
                child.X + child.BorderLeft + child.PaddingLeft + child.Width - box.X);
        if (contentW <= 0f) return;

        // FIX: attribute clamps — SCROLLDELAY=0 used to divide by zero and
        // absurd values overflowed the phase arithmetic.
        float scrollAmount = Math.Clamp(elem.GetAttrInt("scrollamount", 6), 1, 4096);
        int scrollDelay = Math.Clamp(elem.GetAttrInt("scrolldelay", 85), 1, 60_000);
        string behavior = (elem.GetAttrOrDefault("behavior", "scroll")).Trim().ToLowerInvariant();
        bool rightward = (elem.GetAttrOrDefault("direction", "left")).Trim().ToLowerInvariant() == "right";

        // FIX: the phase used TickCount64 % 1_000_000 (exactly 1000 s) — the
        // wrap is NOT a multiple of the travel distance, so every ~16.7
        // minutes the marquee visibly snapped back to its start.  The modulo
        // period is now derived from the traversal cycle, making the wrap
        // seamless by construction.
        float pxPerMs = scrollAmount / scrollDelay;   // = (1000/delay)·amount/1000
        float originX = box.X;
        float travel = rect.Width + contentW;   // full traversal distance
        float baseX;

        switch (behavior)
        {
            case "alternate":
                {
                    // Bounce between the two edges; amplitude shrinks when the
                    // content is wider than the box (clip shows the middle run).
                    float span = Math.Max(1f, rect.Width - contentW);
                    long cycleMs = Math.Max(1L, (long)((2f * span) / pxPerMs));
                    float pos = (Environment.TickCount64 % cycleMs) * pxPerMs;
                    if (pos > span) pos = 2f * span - pos;
                    baseX = rightward
                        ? originX + pos                              // left edge → right edge
                        : originX + (rect.Width - contentW) - pos;   // right → left
                    break;
                }
            case "slide":
                {
                    // One pass, then parked at the resting edge (left edge for
                    // direction=left, right edge for direction=right).  The 24h
                    // modulo only bounds the float's magnitude; Min parks it.
                    float elapsed = (Environment.TickCount64 % 86_400_000L) * pxPerMs;
                    float done = Math.Min(elapsed, rect.Width);
                    baseX = rightward
                        ? originX - contentW + done
                        : originX + rect.Width - done;
                    break;
                }
            default: // scroll — enters from the off edge, wraps seamlessly
                {
                    long cycleMs = Math.Max(1L, (long)(travel / pxPerMs));
                    float pos = (Environment.TickCount64 % cycleMs) * pxPerMs;
                    baseX = rightward
                        ? originX - contentW + pos         // starts off-left, travels right
                        : originX + rect.Width - pos;      // starts off-right, travels left
                    break;
                }
        }

        var clip = g.Save();
        g.SetClip(rect, CombineMode.Intersect);

        // SCROLL draws a second copy one travel-distance behind the first,
        // so as one copy exits the visible edge the next is already
        // entering — the seamless wrap every 1996 marquee had.
        bool dual = behavior == "scroll";

        try
        {
            g.TranslateTransform(baseX - originX, 0f);
            foreach (var child in PaintOrder(box))
                PaintBox(g, child, fonts, images, hoveredElement, blinkVisible, focusedElement);

            if (dual)
            {
                float secondDx = rightward ? -travel : travel;
                g.TranslateTransform(secondDx, 0f);
                foreach (var child in box.Children)
                    PaintBox(g, child, fonts, images, hoveredElement, blinkVisible, focusedElement);
            }
        }
        finally
        {
            g.ResetTransform();
            g.Restore(clip);
        }
    }

    /// <summary>True when the element is, or descends from, a &lt;blink&gt;.</summary>
    private static bool InsideBlink(DomElement? elem)
    {
        for (var node = (DomNode?)elem; node != null; node = node.Parent)
        {
            if (node is DomElement de && de.TagName == "blink")
                return true;
        }
        return false;
    }

    // ── Background ───────────────────────────────────────────────────────

    private void PaintBackground(Graphics g, LayoutBox box, ImageCache images)
    {
        if (box.Element == null) return;   // anonymous boxes have no background

        var style = box.Element.Style;
        if (style == null) return;

        var rect = box.BorderRect;
        if (box.Element.TagName == "body" &&
            BrowserRuntime.Settings.BackgroundMode == BackgroundMode.Force)
        {
            g.FillRectangle(SolidBrushFor(BrowserRuntime.Settings.GetForcedBackgroundColor()),
                rect.X, rect.Y, rect.Width, rect.Height);
            return;
        }
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Solid colour — bgcolor attribute beats CSS
        Color bg = style.BackgroundColor;
        string? bgAttr = box.Element.GetAttr("bgcolor");
        if (!string.IsNullOrEmpty(bgAttr))
        {
            Color parsed = ParseHtmlColor(bgAttr);
            if (parsed != Color.Empty) bg = parsed;
        }

        if (bg != Color.Transparent && bg != Color.Empty)
        {
            // FIX: shared brush pool — was a fresh SolidBrush per box per paint.
            g.FillRectangle(SolidBrushFor(bg), rect.X, rect.Y, rect.Width, rect.Height);
        }

        // Background image
        string? bgCss = style.BackgroundImage;
        if (string.IsNullOrEmpty(bgCss) || bgCss == "none") return;

        string? bgUrl = ParseCssUrl(bgCss);
        if (string.IsNullOrEmpty(bgUrl)) return;

        try
        {
            string absolute = ImageCache.ResolveUrl(bgUrl, _baseUrl);
            var task = images.GetAsync(absolute, _resourceLoader, default);
            if (!task.IsCompletedSuccessfully) return;
            // Failed background images are cached as broken-image frames for
            // normal <img> fallback rendering, but must never be tiled into
            // an element background.
            if (images.IsBroken(absolute)) return;
            var decoded = task.Result;
            if (decoded?.Frames.Count > 0)
                PaintBackgroundImage(g, rect,
                    images.GetCurrentFrame(absolute) ?? decoded.Frames[0], style);
        }
        catch { }
    }

    private void PaintBackgroundImage(Graphics g, RectangleF rect,
                                             Image image, ComputedStyle style)
    {
        float iw = image.Width;
        float ih = image.Height;
        if (iw <= 0 || ih <= 0) return;

        float anchorX = style.BackgroundPositionXLength.HasValue
            ? rect.X + style.BackgroundPositionXLength.Value
            : rect.X + (style.BackgroundPosition.X / 100f) * (rect.Width - iw);
        float anchorY = style.BackgroundPositionYLength.HasValue
            ? rect.Y + style.BackgroundPositionYLength.Value
            : rect.Y + (style.BackgroundPosition.Y / 100f) * (rect.Height - ih);
        if (style.BackgroundFixed)
        {
            anchorX += _scrollX;
            anchorY += _scrollY;
        }

        var state = g.Save();
        try
        {
            using var clip = new Region(rect);
            g.SetClip(clip, CombineMode.Replace);

            // FIX: draw cap — a small tile on a large element used to be an
            // unbounded loop (worst case: millions of DrawImage calls).
            int drawn = 0;
            var oldInterpolation = g.InterpolationMode;
            var oldPixelOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;

            try
            {
                switch (style.BackgroundRepeat)
            {
                case BackgroundRepeat.NoRepeat:
                    g.DrawImage(image, anchorX, anchorY, iw, ih);
                    break;

                case BackgroundRepeat.RepeatX:
                    {
                        // Tile in BOTH directions from the anchor, or a
                        // positioned tile leaves the left of the rect uncovered
                        float x0 = BackOffToCover(anchorX, rect.Left, iw);
                        for (float x = x0; x < rect.Right && drawn < MaxBackgroundTiles; x += iw)
                        {
                            g.DrawImage(image, x, anchorY, iw, ih);
                            drawn++;
                        }
                        break;
                    }
                case BackgroundRepeat.RepeatY:
                    {
                        float y0 = BackOffToCover(anchorY, rect.Top, ih);
                        for (float y = y0; y < rect.Bottom && drawn < MaxBackgroundTiles; y += ih)
                        {
                            g.DrawImage(image, anchorX, y, iw, ih);
                            drawn++;
                        }
                        break;
                    }
                default:
                    {
                        float x0 = BackOffToCover(anchorX, rect.Left, iw);
                        float y0 = BackOffToCover(anchorY, rect.Top, ih);
                        for (float y = y0; y < rect.Bottom && drawn < MaxBackgroundTiles; y += ih)
                            for (float x = x0; x < rect.Right && drawn < MaxBackgroundTiles; x += iw)
                            {
                                g.DrawImage(image, x, y, iw, ih);
                                drawn++;
                            }
                        break;
                    }
                }
            }
            finally
            {
                g.InterpolationMode = oldInterpolation;
                g.PixelOffsetMode = oldPixelOffset;
            }
        }
        finally
        {
            g.Restore(state);   // (ResetClip would have cleared any outer clip)
        }
    }

    /// <summary>Moves a tile anchor back so forward tiling covers [edge, …].</summary>
    private static float BackOffToCover(float anchor, float edge, float step)
    {
        if (step <= 0f || anchor <= edge) return anchor;
        return anchor - (float)Math.Ceiling((anchor - edge) / step) * step;
    }

    // ── Border ───────────────────────────────────────────────────────────

    private void PaintBorder(Graphics g, LayoutBox box)
    {
        if (box.Element == null) return;

        var style = box.Element.Style;
        if (style == null) return;

        var elem = box.Element;

        // Form controls paint their own 3-D chrome at the layout-reserved
        // border ring — the generic path would duplicate it.
        if (elem.TagName is "input" or "select" or "textarea" or "button")
            return;

        // <hr> — the 3-D inset rule of the era
        if (elem.TagName == "hr")
        {
            PaintHr(g, box);
            return;
        }

        // <table border=N> — outset frame; cells keep simple grey rules.
        // The width comes from the BOX (layout applies the HTML attribute
        // there), not from the CSS style.
        if (elem.TagName == "table" && box.BorderTop > 0)
        {
            PaintTableOuterBorder(g, box);
            return;
        }

        bool isTableCell = elem.TagName is "td" or "th";
        Color localBackground = ResolveLocalBackground(elem, Color.White);

        // Paint from the box's layout-resolved widths — they include HTML
        // border attributes the CSS style may know nothing about.
        PaintBorderSide(g, box.BorderRect, box.BorderTop, style.BorderTopStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderTopColor), isTableCell, localBackground),
                        BorderSide.Top, localBackground);
        PaintBorderSide(g, box.BorderRect, box.BorderRight, style.BorderRightStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderRightColor), isTableCell, localBackground),
                        BorderSide.Right, localBackground);
        PaintBorderSide(g, box.BorderRect, box.BorderBottom, style.BorderBottomStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderBottomColor), isTableCell, localBackground),
                        BorderSide.Bottom, localBackground);
        PaintBorderSide(g, box.BorderRect, box.BorderLeft, style.BorderLeftStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderLeftColor), isTableCell, localBackground),
                        BorderSide.Left, localBackground);
    }

    /// <summary>Table cell rules were grey (#808080), not text-black.
    /// When an authored cell border is itself too close to the cell's local
    /// background, give the rule enough contrast to remain visible.</summary>
    private static Color BorderColorFor(Color cssColor, bool isTableCell, Color background)
    {
        var color = isTableCell && cssColor == Color.Black
            ? Color.FromArgb(0x80, 0x80, 0x80)
            : cssColor;
        if (!isTableCell) return color;

        return LuminanceDistance(color, background) >= 0.14f
            ? color
            : EnsureBevelContrast(color, background,
                preferLighter: RelativeLuminance(color) >= RelativeLuminance(background));
    }

    /// <summary>An unset border colour paints black (an Empty colour makes an invisible pen).</summary>
    private static Color BorderColorOrBlack(Color c) => c == Color.Empty ? Color.Black : c;

    private static void PaintTableOuterBorder(Graphics g, LayoutBox box)
    {
        var rect = box.BorderRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        float w = Math.Max(1, box.BorderTop);
        Color background = ResolveLocalBackground(box.Element, Color.White);
        Color light = EnsureBevelContrast(Color.White, background, preferLighter: true);
        Color dark = EnsureBevelContrast(Color.FromArgb(0x80, 0x80, 0x80), background, preferLighter: false);
        using var penDark = new Pen(dark, 1);
        using var penLight = new Pen(light, 1);
        if (NeedsStrongBevelOutline(background))
        {
            using var outline = new Pen(BevelOutlineColor(background), 1);
            g.DrawRectangle(outline, rect.X, rect.Y, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
        }

        // Outset: light top/left, dark bottom/right
        for (int i = 0; i < w; i++)
        {
            float x0 = rect.X + i, y0 = rect.Y + i;
            float x1 = rect.Right - i - 1, y1 = rect.Bottom - i - 1;
            if (x1 < x0 || y1 < y0) break;

            g.DrawLine(penLight, x0, y0, x1, y0);
            g.DrawLine(penLight, x0, y0, x0, y1);
            g.DrawLine(penDark, x0, y1, x1, y1);
            g.DrawLine(penDark, x1, y0, x1, y1);
        }
    }

    /// <summary>3-D sunken frame placeholder for frame/iframe boxes.</summary>
    private static void PaintFramePlaceholder(Graphics g, LayoutBox box)
    {
        var rect = box.BorderRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using var bg = new SolidBrush(Color.FromArgb(0xC0, 0xC0, 0xC0));
        g.FillRectangle(bg, rect);
        PaintSunkenRect(g, rect, 2, Color.FromArgb(0xC0, 0xC0, 0xC0));
    }

    /// <summary>
    /// &lt;hr&gt;: SIZE thick 3-D inset rule; NOSHADE draws a flat grey bar.
    /// Width/size/alignment were resolved during layout.
    /// </summary>
    private static void PaintHr(Graphics g, LayoutBox box)
    {
        var elem = box.Element!;
        var style = elem.Style;
        bool noshade = elem.HasAttr("noshade");

        // HTML COLOR is a presentational attribute, not a CSS border colour,
        // so make it authoritative at paint time too.  This also keeps the
        // renderer robust if a caller builds a layout tree without running
        // the normal style-attribute pass first.
        Color color = ParseHtmlColor(elem.GetAttr("color") ?? string.Empty);
        if (color == Color.Empty && style != null)
            color = style.BorderTopColor != Color.Empty
                ? style.BorderTopColor
                : style.Color;
        if (color == Color.Empty)
            color = Color.Black;

        var rect = box.ContentRect;
        float x1 = rect.X;
        float x2 = rect.X + rect.Width;
        int thick = Math.Max(1, (int)Math.Round(box.Height));

        if (noshade)
        {
            // NOSHADE is a flat rule: paint the authored COLOR verbatim.
            g.FillRectangle(SolidBrushFor(color),
                x1, rect.Y, rect.Width, thick);
            return;
        }

        // 3-D inset: keep the authored colour while deriving a darker top and
        // lighter bottom shade.  A coloured <hr> therefore stays coloured in
        // both NOSHADE and the default 3-D presentation.
        var background = ResolveLocalBackground(elem, Color.White);
        var dark = EnsureBevelContrast(Shade(color, 0.55f), background, preferLighter: false);
        var light = EnsureBevelContrast(Tint(color, 0.65f), background, preferLighter: true);
        int half = (thick + 1) / 2;
        using var penDark = new Pen(dark, 1);
        using var penLight = new Pen(light, 1);
        for (int i = 0; i < thick; i++)
        {
            float y = rect.Y + i;
            g.DrawLine(i < half ? penDark : penLight, x1, y, x2, y);
        }
    }

    private static Color Shade(Color color, float factor) =>
        Color.FromArgb(color.A,
            (int)Math.Round(color.R * factor),
            (int)Math.Round(color.G * factor),
            (int)Math.Round(color.B * factor));

    private static Color Tint(Color color, float factor) =>
        Color.FromArgb(color.A,
            (int)Math.Round(color.R + (255 - color.R) * factor),
            (int)Math.Round(color.G + (255 - color.G) * factor),
            (int)Math.Round(color.B + (255 - color.B) * factor));

    private enum BorderSide { Top, Right, Bottom, Left }

    private static void PaintBorderSide(Graphics g, RectangleF rect, float width,
        BorderStyleValue bStyle, Color color, BorderSide side, Color background)
    {
        if (width <= 0) return;

        if (bStyle is BorderStyleValue.None or BorderStyleValue.Hidden)
        {
            // A positive box border with no resolvable style can only have
            // come from an HTML attribute (img/table BORDER), table cell
            // rules or the fieldset UA rule — those paint as the era
            // default: solid.
            bStyle = BorderStyleValue.Solid;
        }

        if (rect.Width <= 0 || rect.Height <= 0) return;

        int w = Math.Max(1, (int)Math.Round(width));

        if (bStyle is BorderStyleValue.Groove or BorderStyleValue.Ridge
                   or BorderStyleValue.Inset or BorderStyleValue.Outset)
        {
            Paint3DBorder(g, rect, bStyle, w, color, side, background);
            return;
        }

        if (bStyle == BorderStyleValue.Double)
        {
            PaintDoubleBorder(g, rect, w, color, side);
            return;
        }

        using var pen = CreateBorderPen(bStyle, color);
        PaintSimpleSide(g, rect, w, pen, side);
    }

    /// <summary>Draws a plain side as `w` one-pixel strokes.</summary>
    private static void PaintSimpleSide(Graphics g, RectangleF rect, int w,
                                        Pen pen, BorderSide side)
    {
        switch (side)
        {
            case BorderSide.Top:
                for (int i = 0; i < w; i++)
                    g.DrawLine(pen, rect.Left, rect.Top + i, rect.Right, rect.Top + i);
                break;
            case BorderSide.Right:
                for (int i = 0; i < w; i++)
                    g.DrawLine(pen, rect.Right - i - 1, rect.Top, rect.Right - i - 1, rect.Bottom);
                break;
            case BorderSide.Bottom:
                for (int i = 0; i < w; i++)
                    g.DrawLine(pen, rect.Left, rect.Bottom - i - 1, rect.Right, rect.Bottom - i - 1);
                break;
            case BorderSide.Left:
                for (int i = 0; i < w; i++)
                    g.DrawLine(pen, rect.Left + i, rect.Top, rect.Left + i, rect.Bottom);
                break;
        }
    }

    private static void Paint3DBorder(Graphics g, RectangleF rect,
        BorderStyleValue style, int w, Color baseColor, BorderSide side, Color background)
    {
        if (w < 1) return;

        Color light = EnsureBevelContrast(ControlPaint.LightLight(baseColor), background, preferLighter: true);
        Color midLight = EnsureBevelContrast(ControlPaint.Light(baseColor), background, preferLighter: true);
        Color dark = EnsureBevelContrast(ControlPaint.Dark(baseColor), background, preferLighter: false);
        Color darkDark = EnsureBevelContrast(ControlPaint.DarkDark(baseColor), background, preferLighter: false);

        bool topLeft = side is BorderSide.Top or BorderSide.Left;
        bool raised = style is BorderStyleValue.Ridge or BorderStyleValue.Outset;

        if (style is BorderStyleValue.Inset or BorderStyleValue.Outset)
        {
            Color shade = (raised == topLeft) ? light : darkDark;
            if (NeedsStrongBevelOutline(background))
                shade = raised == topLeft ? BevelHighlightColor(background) : BevelShadowColor(background);
            using var brush = new SolidBrush(shade);
            switch (side)
            {
                case BorderSide.Top: g.FillRectangle(brush, rect.Left, rect.Top, rect.Width, w); break;
                case BorderSide.Bottom: g.FillRectangle(brush, rect.Left, rect.Bottom - w, rect.Width, w); break;
                case BorderSide.Left: g.FillRectangle(brush, rect.Left, rect.Top, w, rect.Height); break;
                case BorderSide.Right: g.FillRectangle(brush, rect.Right - w, rect.Top, w, rect.Height); break;
            }
            return;
        }

        // Groove and ridge are two-tone borders. The light/dark order flips
        // on the bottom/right half so the border reads as carved or raised.
        bool outerLight = raised == topLeft;
        Color outer = outerLight ? light : darkDark;
        Color inner = outerLight ? midLight : dark;
        if (NeedsStrongBevelOutline(background))
        {
            outer = outerLight ? BevelHighlightColor(background) : BevelShadowColor(background);
            inner = outerLight ? BevelShadowColor(background) : BevelHighlightColor(background);
        }
        int first = Math.Max(1, w / 2);
        int second = Math.Max(1, w - first);
        using var outerBrush = new SolidBrush(outer);
        using var innerBrush = new SolidBrush(inner);

        switch (side)
        {
            case BorderSide.Top:
                g.FillRectangle(outerBrush, rect.Left, rect.Top, rect.Width, first);
                g.FillRectangle(innerBrush, rect.Left, rect.Top + first, rect.Width, second);
                break;
            case BorderSide.Bottom:
                g.FillRectangle(innerBrush, rect.Left, rect.Bottom - w, rect.Width, second);
                g.FillRectangle(outerBrush, rect.Left, rect.Bottom - first, rect.Width, first);
                break;
            case BorderSide.Left:
                g.FillRectangle(outerBrush, rect.Left, rect.Top, first, rect.Height);
                g.FillRectangle(innerBrush, rect.Left + first, rect.Top, second, rect.Height);
                break;
            case BorderSide.Right:
                g.FillRectangle(innerBrush, rect.Right - w, rect.Top, second, rect.Height);
                g.FillRectangle(outerBrush, rect.Right - first, rect.Top, first, rect.Height);
                break;
        }
    }

    private static void PaintDoubleBorder(Graphics g, RectangleF rect,
                                          int w, Color color, BorderSide side)
    {
        if (w < 3)
        {
            using var solidPen = new Pen(color, 1);
            PaintSimpleSide(g, rect, w, solidPen, side);
            return;
        }

        int line = Math.Max(1, w / 3);
        int gap = Math.Max(1, w - line * 2);
        using var brush = new SolidBrush(color);

        switch (side)
        {
            case BorderSide.Top:
                g.FillRectangle(brush, rect.Left, rect.Top, rect.Width, line);
                g.FillRectangle(brush, rect.Left, rect.Top + line + gap, rect.Width, line);
                break;
            case BorderSide.Bottom:
                g.FillRectangle(brush, rect.Left, rect.Bottom - line - gap - line, rect.Width, line);
                g.FillRectangle(brush, rect.Left, rect.Bottom - line, rect.Width, line);
                break;
            case BorderSide.Left:
                g.FillRectangle(brush, rect.Left, rect.Top, line, rect.Height);
                g.FillRectangle(brush, rect.Left + line + gap, rect.Top, line, rect.Height);
                break;
            case BorderSide.Right:
                g.FillRectangle(brush, rect.Right - line - gap - line, rect.Top, line, rect.Height);
                g.FillRectangle(brush, rect.Right - line, rect.Top, line, rect.Height);
                break;
        }
    }

    /// <summary>Pen is always 1 px wide — the caller loops it for thickness.</summary>
    private static Pen CreateBorderPen(BorderStyleValue style, Color color)
    {
        var pen = new Pen(color, 1);
        switch (style)
        {
            case BorderStyleValue.Dashed:
                pen.DashStyle = DashStyle.Dash; break;
            case BorderStyleValue.Dotted:
                pen.DashStyle = DashStyle.Dot; break;
        }
        return pen;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Content
    // ─────────────────────────────────────────────────────────────────────

    private void PaintContent(Graphics g, LayoutBox box, FontCache fonts,
                              ImageCache images, DomElement? hoveredElement,
                              DomElement? focusedElement = null)
    {
        if (box.Element != null)
        {
            if (box.BoxType == BoxType.Replaced)
            {
                switch (box.Element.TagName)
                {
                    case "img":
                        PaintImage(g, box, images);
                        return;
                    case "input":
                        PaintInputElement(g, box, fonts, images, box.Element == focusedElement);
                        return;
                    case "select":
                        PaintSelect(g, box, fonts);
                        return;
                    case "textarea":
                        PaintTextarea(g, box, fonts, box.Element == focusedElement);
                        return;
                    case "button":
                        {
                            string label = (box.Element.InnerText ?? "").Trim();
                            if (label.Length == 0)
                                label = box.Element.GetAttr("value") ?? "Button";
                            PaintButton(g, box, fonts, GlyphSubstitution.MapGlyphs(label));
                            return;
                        }
                    case "hr":
                        return;   // painted as border
                    case "embed":
                        // Legacy MIDI <embed> controls are painted by
                        // BrowserCanvas. Do not leave the generic plugin
                        // placeholder underneath them (or underneath a
                        // hidden/zero-sized embed). Non-MIDI embeds retain the
                        // historic fallback surface.
                        string? embedSrc = box.Element.GetAttr("src");
                        string embedPath = embedSrc?.Split('?', '#')[0] ?? string.Empty;
                        bool isMidiEmbed = embedPath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ||
                            embedPath.EndsWith(".midi", StringComparison.OrdinalIgnoreCase);
                        if (!isMidiEmbed) PaintFallbackContent(g, box);
                        return;
                    default:
                        PaintFallbackContent(g, box);
                        return;
                }
            }

            if (box.BoxType == BoxType.ListItem)
                PaintListMarker(g, box, fonts, images);
        }

        if (string.IsNullOrEmpty(box.TextRun) || box.Width <= 0f || box.Height <= 0f)
            return;

        var elemForStyle = box.Element;
        if (elemForStyle == null) return;
        var style = box.StyleOverride ?? elemForStyle.Style ?? FallbackStyle(elemForStyle);
        if (style == null) return;

        var font = ResolveFont(fonts, style);
        Color textColor = EffectiveTextColor(style);

        var linkAnchor = FindAncestorElement(elemForStyle, "a");
        bool isLink = linkAnchor?.HasAttr("href") == true;

        if (isLink)
        {
            var ownerDoc = elemForStyle.OwnerDocument();
            if (ownerDoc != null)
            {
                string? href = linkAnchor!.GetAttr("href");
                bool visited = false;
                try
                {
                    visited = href != null &&
                        ownerDoc.VisitedUrls.Contains(
                            ownerDoc.BaseUrl == null ? href :
                            (ownerDoc.BaseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                                ? FileUrls.Resolve(ownerDoc.BaseUrl, href)
                                : ownerDoc.BaseUrl.Resolve(href).ToAbsolute()));
                }
                catch
                {
                    // Odd schemes (snews:, cid:, …) in an href must not take
                    // the whole paint down — treat as unvisited.
                }

                bool active = IsSameOrAncestor(linkAnchor!, ownerDoc.ActiveElement);

                // Author CSS (a:hover/a:active/a:visited) is represented in
                // the computed style and therefore wins naturally. Legacy
                // BODY ALINK, however, means the pressed/active link color —
                // it is NOT a general pointer-hover color.
                Color docLink = StyleResolver.GetLinkColor(ownerDoc);
                bool authorStyled = linkAnchor!.Style?.OwnColor == true;

                if (!authorStyled)
                {
                    if (active)
                        textColor = StyleResolver.GetALinkColor(ownerDoc);
                    else if (visited)
                        textColor = StyleResolver.GetVLinkColor(ownerDoc);
                    else
                        textColor = docLink;
                }

            }
        }

        var fontColorElem = FindAncestorElement(elemForStyle, "font");
        if (fontColorElem?.GetAttr("color") is { } fcAttr &&
            (!isLink || ReferenceEquals(fontColorElem, linkAnchor)))
        {
            Color fc = ParseHtmlColor(fcAttr);
            if (fc != Color.Empty) textColor = fc;
        }

        var contentRect = box.ContentRect;
        if (!float.IsFinite(contentRect.X) || !float.IsFinite(contentRect.Y) ||
            !float.IsFinite(contentRect.Width) || !float.IsFinite(contentRect.Height) ||
            !float.IsFinite(box.Width) || !float.IsFinite(box.Height))
            return;

        // FIX: shared format + shared brush — a text-heavy page used to
        // allocate one StringFormat and one SolidBrush per WORD BOX on
        // every repaint (the caret blink repaints twice a second).
        var brush = SolidBrushFor(textColor);
        var sf = TypographicFormat;

        string text = style.TextTransform switch
        {
            TextTransform.Uppercase => box.TextRun.ToUpperInvariant(),
            TextTransform.Lowercase => box.TextRun.ToLowerInvariant(),
            TextTransform.Capitalize => CapitalizeWords(box.TextRun),
            _ => box.TextRun
        };

        if (text.Contains("VISITOR") || text.Contains("COUNT") || text.Contains("0 0 0") || text.Contains("["))
            Retro96.DebugLog.Write($"[paintdbg] text=\"{text}\" box.X={box.X:F1} " +
                $"contentRect.X={contentRect.X:F1} box.Width={box.Width:F1} " +
                $"parent.X={box.Parent?.X:F1} parent.Width={box.Parent?.Width:F1} " +
                $"style.TextAlign={style.TextAlign}");

        if (style.FontVariant == FontVariantValue.SmallCaps)
            PaintSmallCapsText(g, contentRect.X, contentRect.Y, text, style, fonts, textColor, sf);
        else if (Math.Abs(style.LetterSpacing) > 0.001f || Math.Abs(style.WordSpacing) > 0.001f)
            PaintSpacedText(g, contentRect.X, contentRect.Y, text, font, brush, style, sf);
        else
            g.DrawString(text, font, brush, contentRect.X, contentRect.Y, sf);

        // Text decoration.  HTML links carry their UA underline through
        // descendant inline elements such as <font>, even though
        // text-decoration itself is not a normal inherited CSS property.
        // Use the anchor's final computed decoration so `a { text-decoration:
        // none }` still removes it.
        var deco = style.TextDecoration;
        if (isLink && linkAnchor?.Style?.TextDecoration.HasFlag(TextDecoration.Underline) == true)
            deco |= TextDecoration.Underline;

        // A blank (space-only) run that sits at the EDGE of a link draws no
        // glyphs, so decorating it leaves a stray "_" stub.  But a blank
        // run BETWEEN two words is the gap in "Sign the Guestbook" and MUST
        // stay decorated, or the underline turns into dashed "_ _ _".
        if (deco != TextDecoration.None && IsBlankRun(box) && !BridgedByText(box))
            deco = TextDecoration.None;

        if (deco != TextDecoration.None)
        {
            // Span the fragment's FULL advance width (spaces included) so
            // underline/decoration renders as one continuous line instead
            // of the dashed "_  _  _" the per-word measurement produced.
            float lineRight = contentRect.X + Math.Max(box.Width, 0f);
            if (!float.IsFinite(lineRight)) return;
            var decoP = SolidPenFor(textColor);

            if (deco.HasFlag(TextDecoration.Underline))
            {
                float uy = contentRect.Y + font.GetHeight(g) - 1;
                g.DrawLine(decoP, contentRect.X, uy, lineRight, uy);
            }
            if (deco.HasFlag(TextDecoration.LineThrough))
            {
                float sy = contentRect.Y + font.GetHeight(g) / 2f;
                g.DrawLine(decoP, contentRect.X, sy, lineRight, sy);
            }
            if (deco.HasFlag(TextDecoration.Overline))
            {
                g.DrawLine(decoP, contentRect.X, contentRect.Y,
                    lineRight, contentRect.Y);
            }
        }
    }

    private static void PaintSpacedText(Graphics g, float x, float y, string text,
                                        Font font, Brush brush, ComputedStyle style, StringFormat sf)
    {
        // Draw glyphs individually when CSS1 tracking is non-zero. A single
        // DrawString call can report the expanded advance to layout while
        // still painting the original untracked glyph positions. Keeping the
        // rasterized positions in lock-step with measurement makes headings,
        // nav labels and buttons line up with the box geometry.
        float drawX = x;
        for (int i = 0; i < text.Length; i++)
        {
            string glyph = text[i].ToString();
            g.DrawString(glyph, font, brush, drawX, y, sf);

            float advance = g.MeasureString(glyph, font, int.MaxValue, sf).Width;
            if (i + 1 < text.Length)
                advance += style.LetterSpacing;
            if (char.IsWhiteSpace(text[i]))
                advance += style.WordSpacing;
            drawX += advance;
        }
    }

    /// <summary>True for a text run made only of collapsible whitespace.</summary>
    private static void PaintSmallCapsText(Graphics g, float x, float y, string text,
                                           ComputedStyle style, FontCache fonts,
                                           Color color, StringFormat sf)
    {
        float smallSize = Math.Max(1f, style.FontSize * 0.80f);
        var family = style.FontFamily is { Count: > 0 } ? style.FontFamily : DefaultFontFamily;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        var smallFont = fonts.Resolve(family, smallSize, (int)style.FontWeight, italic, oblique);
        var fullFont = fonts.Resolve(family, style.FontSize > 0f ? style.FontSize : 16f, (int)style.FontWeight, italic, oblique);
        using var runBrush = new SolidBrush(color);

        int i = 0;
        float drawX = x;
        while (i < text.Length)
        {
            bool lower = char.IsLetter(text[i]) && char.IsLower(text[i]);
            int start = i++;
            while (i < text.Length)
            {
                bool nextLower = char.IsLetter(text[i]) && char.IsLower(text[i]);
                if (nextLower != lower) break;
                i++;
            }

            string run = text[start..i];
            string draw = lower ? run.ToUpperInvariant() : run;
            var runFont = lower ? smallFont : fullFont;
            float baselineOffset = lower ? Math.Max(0f, fullFont.AscentPx - smallFont.AscentPx) : 0f;
            g.DrawString(draw, runFont, runBrush, drawX, y + baselineOffset, sf);

            // Small-cap glyphs are visually reduced, but their advance stays
            // on the normal/full-size metric grid. Using the reduced glyph
            // width here made strings such as "The Quick Brown Fox" look
            // unnaturally cramped after the lowercase letters were promoted
            // to capitals.
            drawX += g.MeasureString(draw, fullFont, int.MaxValue, sf).Width;
        }
    }

    private static string CapitalizeWords(string text)
    {
        var chars = text.ToCharArray();
        bool start = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsLetterOrDigit(chars[i]))
            {
                if (start) chars[i] = char.ToUpperInvariant(chars[i]);
                start = false;
            }
            else
            {
                start = true;
            }
        }
        return new string(chars);
    }

    private static bool IsBlankRun(LayoutBox box)
    {
        if (string.IsNullOrEmpty(box.TextRun)) return false;
        foreach (char ch in box.TextRun)
            if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n')
                return false;
        return true;
    }

    /// <summary>
    /// True when a blank run sits between two non-blank text runs that are
    /// part of the SAME link/decoration — i.e. it is an inter-word gap, not
    /// a dangling edge space next to an image.
    /// </summary>
    private static bool BridgedByText(LayoutBox box)
    {
        var siblings = box.Parent?.Children;
        if (siblings == null) return false;

        int i = siblings.IndexOf(box);
        if (i <= 0 || i >= siblings.Count - 1) return false;

        static bool IsWord(LayoutBox b) =>
            !string.IsNullOrEmpty(b.TextRun) && !IsBlankRun(b);

        var prev = siblings[i - 1];
        var next = siblings[i + 1];
        if (!IsWord(prev) || !IsWord(next)) return false;

        // Both neighbours must carry the same underline, otherwise the gap
        // straddles a decoration boundary (plain text | link) and the
        // decorated side should not extend across it.
        var ps = prev.Element?.Style?.TextDecoration ?? TextDecoration.None;
        var ns = next.Element?.Style?.TextDecoration ?? TextDecoration.None;
        var me = box.Element?.Style?.TextDecoration ?? TextDecoration.None;
        return ps.HasFlag(TextDecoration.Underline) == me.HasFlag(TextDecoration.Underline)
            && ns.HasFlag(TextDecoration.Underline) == me.HasFlag(TextDecoration.Underline);
    }

    private static Font ResolveFont(FontCache fonts, ComputedStyle style)
    {
        var family = style.FontFamily is { Count: > 0 } ? style.FontFamily : DefaultFontFamily;
        float size = style.FontSize > 0f ? style.FontSize : 16f;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        return fonts.Resolve(family, size, (int)style.FontWeight, italic, oblique);
    }

    /// <summary>Unset/transparent text colours render black, not invisible.</summary>
    private static Color EffectiveTextColor(ComputedStyle style)
    {
        Color c = style.Color;
        return (c == Color.Empty || c == Color.Transparent) ? Color.Black : c;
    }

    // ── Images ────────────────────────────────────────────────────────────

    private void PaintImage(Graphics g, LayoutBox box, ImageCache images)
    {
        var elem = box.Element!;
        var rect = box.ContentRect;
        string? src = elem.GetAttr("src");
        string? lowsrc = elem.GetAttr("lowsrc");

        Bitmap? frame = null;

        if (!string.IsNullOrEmpty(src))
        {
            string absolute = ImageCache.ResolveUrl(src, _baseUrl);
            if (TryGetFrame(images, absolute, out var f))
                frame = f;
        }

        // LOWSRC placeholder while the real image is still loading
        if (frame == null && !string.IsNullOrEmpty(lowsrc) && !string.IsNullOrEmpty(src))
        {
            string absoluteLow = ImageCache.ResolveUrl(lowsrc, _baseUrl);
            if (TryGetFrame(images, absoluteLow, out var lowFrame) &&
                images.IsLoaded(ImageCache.ResolveUrl(src, _baseUrl)) == false)
            {
                frame = lowFrame;
            }
        }

        float dw = rect.Width > 0 ? rect.Width : (frame?.Width ?? 32f);
        float dh = rect.Height > 0 ? rect.Height : (frame?.Height ?? 32f);
        if (frame == null) { dw = Math.Max(16, dw); dh = Math.Max(16, dh); }
        var drawRect = new RectangleF(rect.X, rect.Y, dw, dh);

        if (frame != null)
            g.DrawImage(frame, drawRect);
        else
            DrawBrokenImage(g, box, drawRect);

        // Linked images with no BORDER attribute get the 2-px link-coloured
        // border (era default) — broken images too.  The pen is centred on
        // the path, so inset by 1 to keep the border inside the box.
        if (box.BorderLeft <= 0 && !elem.HasAttr("border") && dw >= 4 && dh >= 4 &&
            FindAncestorElement(elem, "a")?.HasAttr("href") == true)
        {
            var doc = elem.OwnerDocument();
            using var linkPen = new Pen(
                doc != null ? StyleResolver.GetLinkColor(doc) : Color.FromArgb(0, 0, 0xEE), 2);
            g.DrawRectangle(linkPen, drawRect.X + 1, drawRect.Y + 1, dw - 2, dh - 2);
        }
    }

    private bool TryGetFrame(ImageCache images, string absoluteUrl, out Bitmap? frame)
    {
        try
        {
            var task = images.GetAsync(absoluteUrl, _resourceLoader, default);
        if (task.IsCompletedSuccessfully && task.Result?.Frames.Count > 0)
        {
            frame = images.GetCurrentFrame(absoluteUrl) ?? task.Result.Frames[0];
            return true;
        }
        }
        catch { }
        frame = null;
        return false;
    }

    private static void DrawBrokenImage(Graphics g, LayoutBox box, RectangleF br)
    {
        g.FillRectangle(Brushes.LightGray, br);
        g.DrawRectangle(Pens.DarkGray, br.X, br.Y, br.Width - 1, br.Height - 1);

        float iconSize = Math.Min(20f, Math.Max(12f, br.Height - 4f));
        using var p = new Pen(Color.Red, 2);
        g.DrawLine(p, br.X + 2, br.Y + 2,
            br.X + iconSize - 2, br.Y + iconSize - 2);
        g.DrawLine(p, br.X + iconSize - 2, br.Y + 2,
            br.X + 2, br.Y + iconSize - 2);

        // Keep alt text readable beside the broken-image marker instead of
        // drawing it over the marker and losing most of the label.
        string? alt = box.Element?.GetAttr("alt");
        if (!string.IsNullOrEmpty(alt) && br.Width > iconSize + 8)
        {
            using var sf = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            using var font = SystemFonts.SmallCaptionFont ?? SystemFonts.DefaultFont;
            var textRect = new RectangleF(br.X + iconSize + 3, br.Y + 1,
                br.Width - iconSize - 5, Math.Max(1f, br.Height - 2));
            g.DrawString(alt, font, Brushes.DarkGray, textRect, sf);
        }
    }

    /// <summary>object/embed/applet: reserved box + fallback content.</summary>
    private static void PaintFallbackContent(Graphics g, LayoutBox box)
    {
        var r = box.ContentRect;
        if (r.Width <= 0 || r.Height <= 0) return;

        using var bg = new SolidBrush(Color.FromArgb(0xC0, 0xC0, 0xC0));
        g.FillRectangle(bg, r);
        g.DrawRectangle(Pens.Gray, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    // ── Form controls ─────────────────────────────────────────────────────

    private void PaintInputElement(Graphics g, LayoutBox box, FontCache fonts,
                                   ImageCache images, bool isFocused = false)
    {
        var elem = box.Element!;
        var style = elem.Style ?? FallbackStyle(elem);
        string type = elem.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();

        switch (type)
        {
            case "hidden":
                return;

            case "checkbox":
            case "radio":
                PaintCheckboxOrRadio(g, box, type == "radio");
                return;

            case "submit":
            case "reset":
            case "button":
                PaintButton(g, box, fonts,
                    elem.GetAttr("value") ?? (type == "submit" ? "Submit Query" : "Button"));
                return;

            case "image":
                // Image inputs render their SRC image just like <img>
                PaintImage(g, box, images);
                return;

            case "file":
                PaintFileInput(g, box, fonts, style);
                return;

            default:    // text, password, and unknown → text field
                string value = elem.GetAttr("value") ?? "";
                if (type == "password") value = new string('*', value.Length);
                PaintTextControl(g, box, fonts, style, value, isFocused, type == "password");
                return;
        }
    }

    // ── Adaptive 3-D chrome ──────────────────────────────────────────────

    /// <summary>
    /// Finds the nearest opaque background that is actually painted behind an
    /// element. Background is not inherited in CSS, so walking ancestors until
    /// an explicit background is found is a better approximation of the local
    /// pixel than using one document-wide colour.
    /// </summary>
    private static Color ResolveLocalBackground(DomElement? element, Color fallback)
    {
        for (DomNode? node = element; node != null; node = node.Parent)
        {
            if (node is not DomElement elem) continue;

            string? attr = elem.GetAttr("bgcolor");
            if (!string.IsNullOrWhiteSpace(attr))
            {
                Color parsed = ParseHtmlColor(attr);
                if (parsed != Color.Empty && parsed != Color.Transparent)
                    return parsed;
            }

            var style = elem.Style;
            if (style != null && style.BackgroundColor != Color.Empty &&
                style.BackgroundColor != Color.Transparent)
                return style.BackgroundColor;
        }

        return fallback;
    }

    private static float RelativeLuminance(Color color)
    {
        static float Channel(int c)
        {
            float s = c / 255f;
            return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        }
        return 0.2126f * Channel(color.R) +
               0.7152f * Channel(color.G) +
               0.0722f * Channel(color.B);
    }

    private static float LuminanceDistance(Color a, Color b) =>
        MathF.Abs(RelativeLuminance(a) - RelativeLuminance(b));

    private static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(from.A,
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    /// <summary>
    /// Keeps the classic white/gray bevel on normal Windows-grey faces but
    /// moves either tone far enough away from the element's real local
    /// background when the normal tone would disappear into it.
    /// </summary>
    private static bool NeedsStrongBevelOutline(Color background)
    {
        float l = RelativeLuminance(background);
        return l >= 0.82f || l <= 0.12f;
    }

    private static Color BevelShadowColor(Color background)
    {
        float l = RelativeLuminance(background);
        if (l >= 0.82f) return Color.FromArgb(0x58, 0x58, 0x58);
        if (l <= 0.12f) return Color.FromArgb(0x28, 0x28, 0x28);
        return Color.FromArgb(0x60, 0x60, 0x60);
    }

    private static Color BevelHighlightColor(Color background)
    {
        float l = RelativeLuminance(background);

        // On white/near-white surfaces, a near-white highlight is effectively
        // invisible.  Keep it light enough to read as a raised edge, but make
        // it a definite neutral grey so the bevel remains visible.
        if (l >= 0.90f) return Color.FromArgb(0xC8, 0xC8, 0xC8);
        if (l >= 0.72f) return Color.FromArgb(0xD0, 0xD0, 0xD0);
        if (l >= 0.50f) return Color.FromArgb(0xD8, 0xD8, 0xD8);
        if (l <= 0.12f) return Color.FromArgb(0xF8, 0xF8, 0xF8);
        return Color.FromArgb(0xF0, 0xF0, 0xF0);
    }

    private static Color BevelOutlineColor(Color background)
    {
        float l = RelativeLuminance(background);
        if (l >= 0.82f) return Color.FromArgb(0x70, 0x70, 0x70);
        if (l <= 0.12f) return Color.FromArgb(0xD8, 0xD8, 0xD8);
        return Color.FromArgb(0x70, 0x70, 0x70);
    }

    /// <summary>
    /// Keeps the classic white/gray bevel on normal Windows-grey faces, but
    /// deliberately increases the tone separation when a near-white or
    /// near-black local background would wash the bevel out.
    /// </summary>
    private static Color EnsureBevelContrast(Color candidate, Color background, bool preferLighter)
    {
        if (candidate == Color.Empty || candidate == Color.Transparent)
            candidate = preferLighter ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);

        float bg = RelativeLuminance(background);

        // A white highlight technically has enough luminance distance from a
        // light-grey control face, but it still disappears against the page
        // when the chrome sits on a white/near-white surface.  Force the
        // light edge into a visible neutral-grey range on light backgrounds.
        if (preferLighter && bg >= 0.50f && RelativeLuminance(candidate) >= 0.90f)
            return BevelHighlightColor(background);

        if (NeedsStrongBevelOutline(background))
            return preferLighter ? BevelHighlightColor(background) : BevelShadowColor(background);

        const float MinimumLuminanceContrast = 0.22f;
        if (LuminanceDistance(candidate, background) >= MinimumLuminanceContrast)
            return candidate;

        if (preferLighter)
            return bg > 0.72f ? Mix(background, Color.Black, 0.20f)
                              : Mix(background, Color.White, 0.78f);

        return bg < 0.28f ? Mix(background, Color.White, 0.55f)
                          : Mix(background, Color.Black, 0.78f);
    }

    /// <summary>2-tone sunken (inset) rectangle frame, `size` px thick.</summary>
    private static void PaintSunkenRect(Graphics g, RectangleF rect, int size, Color background = default)
    {
        if (background == Color.Empty || background == Color.Transparent)
            background = Color.White;
        Color dark = EnsureBevelContrast(Color.FromArgb(0x80, 0x80, 0x80), background, preferLighter: false);
        Color light = EnsureBevelContrast(Color.White, background, preferLighter: true);
        using var penDark = new Pen(dark, 1);
        using var penLight = new Pen(light, 1);
        if (NeedsStrongBevelOutline(background))
        {
            using var outline = new Pen(BevelOutlineColor(background), 1);
            g.DrawRectangle(outline, rect.Left, rect.Top, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
        }
        for (int i = 0; i < size; i++)
        {
            float x0 = rect.Left + i, y0 = rect.Top + i;
            float x1 = rect.Right - i - 1, y1 = rect.Bottom - i - 1;
            if (x1 < x0 || y1 < y0) break;
            g.DrawLine(penDark, x0, y0, x1, y0);
            g.DrawLine(penDark, x0, y0, x0, y1);
            g.DrawLine(penLight, x0, y1, x1, y1);
            g.DrawLine(penLight, x1, y0, x1, y1);
        }
    }

    /// <summary>2-tone raised (outset) rectangle frame, `size` px thick.</summary>
    private static void PaintRaisedRect(Graphics g, RectangleF rect, int size, Color background = default)
    {
        if (background == Color.Empty || background == Color.Transparent)
            background = Color.White;
        Color light = EnsureBevelContrast(Color.White, background, preferLighter: true);
        Color dark = EnsureBevelContrast(Color.FromArgb(0x80, 0x80, 0x80), background, preferLighter: false);
        using var penLight = new Pen(light, 1);
        using var penDark = new Pen(dark, 1);
        if (NeedsStrongBevelOutline(background))
        {
            using var outline = new Pen(BevelOutlineColor(background), 1);
            g.DrawRectangle(outline, rect.Left, rect.Top, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
        }
        for (int i = 0; i < size; i++)
        {
            float x0 = rect.Left + i, y0 = rect.Top + i;
            float x1 = rect.Right - i - 1, y1 = rect.Bottom - i - 1;
            if (x1 < x0 || y1 < y0) break;
            g.DrawLine(penLight, x0, y0, x1, y0);
            g.DrawLine(penLight, x0, y0, x0, y1);
            g.DrawLine(penDark, x0, y1, x1, y1);
            g.DrawLine(penDark, x1, y0, x1, y1);
        }
    }

    private void PaintFileInput(Graphics g, LayoutBox box, FontCache fonts,
                                       ComputedStyle style)
    {
        var rect = box.BorderRect;
        var face = box.ContentRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        bool disabled = box.Element?.HasAttr("disabled") == true;
        Color outer = disabled ? Color.FromArgb(0xD0, 0xD0, 0xD0) : Color.FromArgb(0xC0, 0xC0, 0xC0);
        Color buttonFace = disabled ? Color.FromArgb(0xD8, 0xD8, 0xD8) : Color.FromArgb(0xE0, 0xE0, 0xE0);
        Color textColor = disabled ? Color.Gray
            : style.OwnColor ? style.Color : Color.Black;

        using (var bg = new SolidBrush(outer))
            g.FillRectangle(bg, rect.X, rect.Y, rect.Width, rect.Height);
        PaintSunkenRect(g, rect, 1, outer);

        const float buttonWidth = 92f;
        float actualButtonWidth = Math.Min(buttonWidth, Math.Max(34f, rect.Width - 8f));
        var button = new RectangleF(
            face.X + 2, face.Y + 2,
            actualButtonWidth, Math.Max(1f, face.Height - 4));

        using (var buttonBrush = new SolidBrush(buttonFace))
            g.FillRectangle(buttonBrush, button.X, button.Y, button.Width, button.Height);

        bool pressed = box.Element != null && ReferenceEquals(box.Element, PressedElement);
        if (pressed)
        {
            // File-input controls contain a real button-like sub-face. Keep
            // the same Win95 press-in treatment as ordinary buttons.
            PaintSunkenRect(g, button, 2, buttonFace);
        }
        else
        {
            PaintRaisedRect(g, button, 2, buttonFace);
        }

        var font = ResolveFont(fonts, style);
        using var brush = new SolidBrush(textColor);
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.EllipsisCharacter
        };

        var labelButtonRect = pressed
            ? new RectangleF(button.X + 1, button.Y + 1, Math.Max(0, button.Width - 1), Math.Max(0, button.Height - 1))
            : button;
        g.DrawString("Choose File", font, brush, labelButtonRect, fmt);

        string chosen = box.Element?.GetAttr("data-file-name") ?? "";
        string label = chosen.Length == 0 ? "No file chosen" : chosen;
        var labelRect = new RectangleF(
            button.Right + 6, face.Y + 1,
            Math.Max(0f, face.Right - button.Right - 9), face.Height - 2);

        string displayLabel = FitFileNameToWidth(g, label, font, Math.Max(0f, labelRect.Width));
        using var labelFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None
        };
        var clip = g.Save();
        g.SetClip(labelRect, CombineMode.Intersect);
        g.DrawString(displayLabel, font, brush, labelRect, labelFmt);
        g.Restore(clip);
    }

    private static string FitFileNameToWidth(Graphics g, string fileName, Font font, float width)
    {
        if (width <= 0f || string.IsNullOrEmpty(fileName)) return fileName;
        if (g.MeasureString(fileName, font).Width <= width) return fileName;

        const string ellipsis = "…";
        string extension = System.IO.Path.GetExtension(fileName);
        string stem = extension.Length > 0 ? fileName[..^extension.Length] : fileName;

        // Preserve the complete final extension. Remove characters from the
        // middle of the stem until the prefix + suffix + extension fits.
        int left = (stem.Length + 1) / 2;
        int right = stem.Length / 2;
        while (left + right > 0)
        {
            string candidate = stem[..left] + ellipsis +
                               (right > 0 ? stem[^right..] : string.Empty) + extension;
            if (g.MeasureString(candidate, font).Width <= width)
                return candidate;

            if (left >= right && left > 0) left--;
            else if (right > 0) right--;
            else break;
        }

        string extensionOnly = ellipsis + extension;
        if (g.MeasureString(extensionOnly, font).Width <= width)
            return extensionOnly;
        return extension.Length > 0 ? extension : ellipsis;
    }

    private static void PaintTextControl(Graphics g, LayoutBox box, FontCache fonts,
                                         ComputedStyle style, string text, bool isFocused,
                                         bool isPassword)
    {
        var rect = box.BorderRect;   // chrome sits on the layout-reserved ring
        var face = box.ContentRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // CSS1-authored colours (IE3-era form styling): declarations the
        // page itself put on the control win; everything else keeps the
        // classic native white-on-black sunken field.  Only OWN
        // declarations count — an inherited BODY text= white must never
        // turn the field's text invisible.
        bool disabled = box.Element?.HasAttr("disabled") == true;
        bool authoredBg = style.OwnBackground && style.BackgroundColor != Color.Transparent;
        Color bgColor = disabled ? Color.FromArgb(0xE0, 0xE0, 0xE0)
                     : authoredBg ? style.BackgroundColor : Color.White;
        Color fgColor = disabled ? Color.Gray
                     : style.OwnColor ? style.Color : Color.Black;

        using (var faceBrush = new SolidBrush(bgColor))
            g.FillRectangle(faceBrush, rect.X, rect.Y, rect.Width, rect.Height);
        PaintSunkenRect(g, rect, 2, bgColor);

        // Focused fields get a dark ring just inside the bevel — otherwise
        // there is no visual difference between focused and unfocused.
        if (isFocused)
        {
            using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
            g.DrawRectangle(focusPen, face.X, face.Y, face.Width - 1, face.Height - 1);
        }

        var font = ResolveFont(fonts, style);
        using var brush = new SolidBrush(fgColor);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            LineAlignment = StringAlignment.Center,
            // text-align only when the page authored it for THIS control
            // (the digital-clock idiom centres its digits); an inherited
            // cell alignment never moves the caret text.
            Alignment = style.OwnTextAlign
                ? (style.TextAlign == TextAlign.Center ? StringAlignment.Center
                  : style.TextAlign == TextAlign.Right ? StringAlignment.Far
                  : StringAlignment.Near)
                : StringAlignment.Near
        };

        var textRect = new RectangleF(face.X + 3, face.Y,
                                      Math.Max(0, face.Width - 6), face.Height);
        // TextRenderingHint is a mutable Graphics state.  Password masks may
        // use single-bit grid fitting for crisp '*' glyphs, but that setting
        // must never leak into the text painted after this control.  Save the
        // full graphics state and explicitly select the correct hint for this
        // one draw.
        var textState = g.Save();
        try
        {
            g.SetClip(textRect, CombineMode.Intersect);
            g.TextRenderingHint = isPassword
                ? Retro96.Drawing.TextRenderingHint.SingleBitPerPixelGridFit
                : Retro96.Drawing.TextRenderingHint.ClearTypeGridFit;
            if (isPassword && box.Element != null)
            {
                using var maskFormat = new StringFormat(StringFormat.GenericTypographic)
                {
                    FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
                    Trimming = StringTrimming.None,
                    LineAlignment = StringAlignment.Center,
                    Alignment = StringAlignment.Near
                };
                int length = text.Length;
                PasswordMaskLayout.DrawRange(g, length, font, brush, textRect.X, textRect.Y,
                    textRect.Height, 0, length, maskFormat);
            }
            else
            {
                g.DrawString(text, font, brush, textRect, sf);
            }
        }
        finally
        {
            g.Restore(textState);
        }
    }

    private static void PaintCheckboxOrRadio(Graphics g, LayoutBox box, bool isRadio)
    {
        var rect = box.ContentRect;
        float size = Math.Min(rect.Width, rect.Height);
        float x = rect.X + (rect.Width - size) / 2;
        float y = rect.Y + (rect.Height - size) / 2;
        bool disabled = box.Element?.HasAttr("disabled") == true;
        Color controlColor = disabled ? Color.Gray : Color.Black;

        bool isChecked = box.Element!.HasAttr("checked");

        if (isRadio)
        {
            using var pen = new Pen(controlColor, 1);
            g.DrawEllipse(pen, x, y, size, size);
            if (isChecked)
                using (var brush = new SolidBrush(controlColor))
                    g.FillEllipse(brush,
                    x + size * 0.3f, y + size * 0.3f, size * 0.4f, size * 0.4f);
        }
        else
        {
            using var pen = new Pen(controlColor, 1);
            g.DrawRectangle(pen, x, y, size, size);
            if (isChecked)
            {
                using var checkPen = new Pen(controlColor, 2);
                g.DrawLine(checkPen, x + 2, y + 2, x + size - 2, y + size - 2);
                g.DrawLine(checkPen, x + size - 2, y + 2, x + 2, y + size - 2);
            }
        }
    }

    private void PaintSelect(Graphics g, LayoutBox box, FontCache fonts)
    {
        var elem = box.Element!;
        var rect = box.BorderRect;
        var face = box.ContentRect;
        var style = elem.Style ?? FallbackStyle(elem);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        bool disabled = elem.HasAttr("disabled");

        var font = ResolveFont(fonts, style);

        int sizeAttr = Math.Max(1, elem.GetAttrInt("size", 1));
        bool isListbox = sizeAttr > 1 || elem.HasAttr("multiple");   // MULTIPLE → listbox, era rule
        int visibleRows = elem.HasAttr("multiple") && sizeAttr == 1 ? 4 : sizeAttr;
        var options = elem.Descendants()
            .OfType<DomElement>()
            .Where(o => o.TagName == "option")
            .ToList();

        Color selectFaceColor = disabled
            ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.White;
        using var faceBrush = new SolidBrush(selectFaceColor);
        g.FillRectangle(faceBrush, rect.X, rect.Y, rect.Width, rect.Height);
        PaintSunkenRect(g, rect, 2, selectFaceColor);

        if (isListbox && options.Count > 0)
        {
            float rowH = font.GetHeight(g) + 2;
            visibleRows = Math.Max(1, visibleRows);
            int maxScroll = Math.Max(0, options.Count - visibleRows);
            int scrollOffset = Math.Clamp(SelectScrollResolver?.Invoke(elem) ?? 0, 0, maxScroll);
            bool needsScrollbar = options.Count > visibleRows && face.Width >= 16;
            const float scrollbarWidth = 14f;
            float optionWidth = Math.Max(1f, face.Width - (needsScrollbar ? scrollbarWidth : 0f));
            var optionFace = new RectangleF(face.X, face.Y, optionWidth, face.Height);

            int rows = Math.Min(visibleRows, options.Count - scrollOffset);
            var state = g.Save();
            try
            {
                g.SetClip(optionFace, CombineMode.Intersect);
                for (int row = 0; row < rows; row++)
                {
                    var opt = options[scrollOffset + row];
                    float rowY = face.Y + row * rowH;
                    if (rowY + rowH > face.Bottom) break;

                    bool selected = opt.HasAttr("selected");
                    if (selected)
                        g.FillRectangle(SystemBrushes.Highlight,
                            optionFace.X + 1, rowY, Math.Max(1f, optionFace.Width - 2), rowH);

                    using var brush = new SolidBrush(disabled
                        ? Color.Gray : selected ? Color.White : Color.Black);
                    g.DrawString(GlyphSubstitution.MapGlyphs((opt.InnerText ?? "").Trim()), font, brush,
                        optionFace.X + 3, rowY + 1);
                }
            }
            finally
            {
                g.Restore(state);
            }

            if (needsScrollbar)
                PaintSelectScrollbar(g, face, options.Count, visibleRows, scrollOffset);
            return;
        }

        // Dropdown: selected option text + arrow
        var selectedOpt = options.FirstOrDefault(o => o.HasAttr("selected"))
                       ?? options.FirstOrDefault();
        string text = selectedOpt != null
            ? GlyphSubstitution.MapGlyphs(selectedOpt.InnerText?.Trim() ?? "")
            : "";

        using var brush2 = new SolidBrush(disabled ? Color.Gray : Color.Black);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near
        };

        var textRect = new RectangleF(face.X + 3, face.Y,
                                      Math.Max(0, face.Width - 18), face.Height);
        g.DrawString(text, font, brush2, textRect, sf);

        // Arrow button
        float arrowX = face.Right - 14;
        float arrowY = face.Y + face.Height / 2;
        using var arrowBrush = new SolidBrush(disabled ? Color.Gray : Color.Black);
        g.FillPolygon(arrowBrush, new[]
        {
            new PointF(arrowX, arrowY - 4),
            new PointF(arrowX + 8, arrowY - 4),
            new PointF(arrowX + 4, arrowY + 3)
        });
    }

    private static void PaintSelectScrollbar(Graphics g, RectangleF face,
                                              int optionCount, int visibleRows,
                                              int scrollOffset)
    {
        if (optionCount <= visibleRows || face.Width < 16 || face.Height < 8) return;

        const float barWidth = 14f;
        float trackX = face.Right - barWidth + 1f;
        float trackY = face.Top + 1f;
        float trackHeight = Math.Max(1f, face.Height - 2f);
        using var track = new SolidBrush(Color.FromArgb(0xE0, 0xE0, 0xE0));
        using var thumb = new SolidBrush(Color.FromArgb(0x80, 0x80, 0x80));
        g.FillRectangle(track, trackX, trackY, barWidth - 1f, trackHeight);
        float maxThumbHeight = Math.Max(1f, trackHeight - 2f);
        float thumbHeight = Math.Min(maxThumbHeight, Math.Max(10f,
            trackHeight * visibleRows / Math.Max(1f, optionCount)));
        float travel = Math.Max(0f, trackHeight - 2f - thumbHeight);
        int maxScroll = Math.Max(0, optionCount - visibleRows);
        float thumbY = trackY + 1f +
            travel * Math.Clamp(scrollOffset / (float)Math.Max(1, maxScroll), 0f, 1f);
        g.FillRectangle(thumb, trackX + 1f, thumbY,
            Math.Max(1f, barWidth - 3f), thumbHeight);
    }

    private void PaintTextarea(Graphics g, LayoutBox box, FontCache fonts,
                               bool isFocused)
    {
        var elem = box.Element!;
        var rect = box.BorderRect;
        var face = box.ContentRect;
        var style = elem.Style ?? FallbackStyle(elem);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        bool disabled = elem.HasAttr("disabled");
        bool authoredBg = style.OwnBackground && style.BackgroundColor != Color.Transparent;
        Color bgColor = disabled ? Color.FromArgb(0xE0, 0xE0, 0xE0)
                     : authoredBg ? style.BackgroundColor : Color.White;
        Color fgColor = disabled ? Color.Gray
                     : style.OwnColor ? style.Color : Color.Black;

        using (var faceBrush = new SolidBrush(bgColor))
            g.FillRectangle(faceBrush, rect.X, rect.Y, rect.Width, rect.Height);
        PaintSunkenRect(g, rect, 2, bgColor);

        if (isFocused)
        {
            using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
            g.DrawRectangle(focusPen, face.X, face.Y, face.Width - 1, face.Height - 1);
        }

        var font = ResolveFont(fonts, style);
        string text = elem.InnerText ?? "";
        using var brush = new SolidBrush(fgColor);
        bool wrapOff = elem.GetAttrOrDefault("wrap", "").Trim()
            .Equals("off", StringComparison.OrdinalIgnoreCase);

        var layout = TextareaOverlay.CalculateLayout(g, text, font,
            face.Width, face.Height, wrapOff);
        var textareaState = TextareaStateResolver?.Invoke(elem) ?? default;
        int requestedScroll = textareaState.ScrollLine;
        int scrollLine = Math.Clamp(requestedScroll, 0,
            Math.Max(0, layout.Lines.Count - layout.VisibleLines));
        float requestedScrollX = textareaState.ScrollX;
        float maxScrollX = Math.Max(0f, layout.TextWidth - layout.TextViewportWidth);
        float scrollX = wrapOff ? Math.Clamp(requestedScrollX, 0f, maxScrollX) : 0f;

        int textState = g.Save();
        try
        {
            g.SetClip(new RectangleF(face.X + 1, face.Y + 1,
                Math.Max(1, face.Width - 2), Math.Max(1, face.Height - 2)),
                CombineMode.Intersect);
            TextareaOverlay.DrawLines(g, text, font, layout.Lines, brush,
                face.X + 3 - scrollX, face.Y + 2,
                layout.TextWidth, layout.LineHeight, scrollLine);
        }
        finally
        {
            g.Restore(textState);
        }

        // The scrollbar is focus-owned UI.  Its absence while unfocused is
        // intentional, but the text layout still reserves the same gutter so
        // wrapping does not jump when focus moves away.
        if (isFocused && layout.NeedsVerticalScrollbar)
            PaintTextareaScrollbar(g, face, layout.Lines.Count,
                layout.VisibleLines, scrollLine);
    }

    private static void PaintTextareaScrollbar(Graphics g, RectangleF face,
                                               int lineCount, int visibleLines,
                                               int scrollLine)
    {
        if (lineCount <= visibleLines || face.Width < 16 || face.Height < 8) return;

        const float barWidth = 14f;
        float trackX = face.Right - barWidth + 1f;
        float trackY = face.Top + 1f;
        float trackHeight = Math.Max(1f, face.Height - 2f);
        using var track = new SolidBrush(Color.FromArgb(0xE0, 0xE0, 0xE0));
        using var thumb = new SolidBrush(Color.FromArgb(0x80, 0x80, 0x80));
        g.FillRectangle(track, trackX, trackY, barWidth - 1f, trackHeight);

        float thumbHeight = Math.Clamp(
            trackHeight * visibleLines / Math.Max(1f, lineCount), 10f, trackHeight - 2f);
        float travel = Math.Max(0f, trackHeight - 2f - thumbHeight);
        int maxLine = Math.Max(0, lineCount - visibleLines);
        float thumbY = trackY + 1f +
            travel * Math.Clamp(scrollLine / (float)Math.Max(1, maxLine), 0f, 1f);
        g.FillRectangle(thumb, trackX + 1f, thumbY,
            Math.Max(1f, barWidth - 3f), thumbHeight);
    }

    private void PaintButton(Graphics g, LayoutBox box, FontCache fonts, string text)
    {
        var style = box.Element!.Style ?? FallbackStyle(box.Element);
        var rect = box.BorderRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        bool pressed = box.Element != null && ReferenceEquals(box.Element, PressedElement);
        bool disabled = box.Element?.HasAttr("disabled") == true;

        using var faceBrush = new SolidBrush(disabled
            ? Color.FromArgb(0xE0, 0xE0, 0xE0)
            : Color.FromArgb(0xC0, 0xC0, 0xC0));
        g.FillRectangle(faceBrush, rect.X, rect.Y, rect.Width, rect.Height);
        var bevelFace = disabled
            ? Color.FromArgb(0xE0, 0xE0, 0xE0)
            : Color.FromArgb(0xC0, 0xC0, 0xC0);
        if (pressed)
            PaintSunkenRect(g, rect, 2, bevelFace);
        else
            PaintRaisedRect(g, rect, 2, bevelFace);

        var font = ResolveFont(fonts, style);
        using var brush = new SolidBrush(disabled ? Color.Gray : Color.Black);
        // MUST be GenericTypographic like InlineLayout._sf: a plain StringFormat
        // adds ~1/6em of side padding that layout never reserved, which pushed
        // the label past the face and got it ellipsized ("Submit...").
        // Win95 buttons clip, they never ellipsize.
        var sf = ButtonFormat;

        // Centre on the whole border box (minus the 2px bevel) instead of the
        // content rect, which layout may have shrunk by padding.
        var textRect = RectangleF.Inflate(rect, -2, -2);
        if (pressed) textRect.Offset(1, 1);
        g.DrawString(text, font, brush, textRect, sf);
    }

    // ── List markers ─────────────────────────────────────────────────────

    private void PaintListMarker(Graphics g, LayoutBox box, FontCache fonts,
                                 ImageCache images)
    {
        var elem = box.Element!;
        var parent = elem.Parent as DomElement;
        if (parent == null) return;

        string parentTag = parent.TagName;

        // <li TYPE=...> beats <ul TYPE=...>/<ol TYPE=...>
        string? type = (elem.GetAttr("type") ?? parent.GetAttr("type"))?.Trim();
        int value = elem.GetAttrInt("value", -1);
        int start = parent.GetAttrInt("start", 1);

        var style = elem.Style ?? FallbackStyle(elem);
        var listStyle = style.Clone();
        if (!style.OwnListStyleType && parent.Style != null)
            listStyle.ListStyleType = parent.Style.ListStyleType;
        if (!style.OwnListStyleImage && parent.Style != null)
            listStyle.ListStyleImage = parent.Style.ListStyleImage;

        var font = ResolveFont(fonts, style);
        Color markerColor = EffectiveTextColor(style);

        float markerY = box.Y + box.BorderTop + box.PaddingTop;
        bool inside = box.ListMarkerInside;
        float insideX = box.X + box.BorderLeft + box.PaddingLeft + 2f;

        if (listStyle.ListStyleImage is { Length: > 0 } imageUrl)
        {
            string absolute = ImageCache.ResolveUrl(imageUrl, _baseUrl);
            if (TryGetFrame(images, absolute, out var frame) && frame != null)
            {
                float markerX = inside ? insideX : box.X - frame.Width - 4f;
                g.DrawImage(frame, markerX, markerY,
                    frame.Width, frame.Height);
                return;
            }
        }

        using var brush = new SolidBrush(markerColor);

        if (parentTag == "ol")
        {
            // Ordinal: honour VALUE attr; otherwise count preceding <li>
            // siblings + START
            int index = value;
            if (index < 0)
            {
                index = start;
                foreach (var sib in parent.Children)
                {
                    if (sib == elem) break;
                    if (sib is DomElement de && de.TagName == "li")
                    {
                        int siblingValue = de.GetAttrInt("value", -1);
                        index = siblingValue >= 0 ? siblingValue + 1 : index + 1;
                    }
                }
            }

            // TYPE is case-sensitive: "A"/"I" are the uppercase forms.
            string marker = type switch
            {
                "a" => MarkerLetters(index, 'a'),
                "A" => MarkerLetters(index, 'A'),
                "i" => MarkerRoman(index, lowercase: true),
                "I" => MarkerRoman(index, lowercase: false),
                _ => index.ToString(),
            };

            string label = marker + ".";
            using var sf = new StringFormat(StringFormat.GenericTypographic);
            var size = g.MeasureString(label, font, int.MaxValue, sf);
            float markerRight = inside ? insideX + 16f : box.X - 4;
            g.DrawString(label, font, brush, markerRight - size.Width, markerY, sf);
            return;
        }

        // Unordered: disc / circle / square, with nesting-depth default
        // (disc → circle → square, per the era's nesting behaviour)
        if (parentTag is "ul" or "menu" or "dir")
        {
            // CSS list-style-* is inherited by the <li>; use the computed
            // style rather than looking only at the old HTML TYPE attribute.
            // `none` means there is no marker at all.
            if (listStyle.ListStyleType == ListStyleType.None)
                return;

            // HTML TYPE and CSS list-style-type are explicit choices.  When
            // neither is present, use the historical nested-list convention
            // (disc -> circle -> square).  The old code could never reach its
            // depth fallback because every <ul>/<menu>/<dir> received the UA
            // ListStyleType.Disc value during style resolution, so even
            // TYPE=circle/square collapsed to a filled disc at paint time.
            bool explicitHtmlType = !string.IsNullOrWhiteSpace(type);
            bool explicitCssType = style.OwnListStyleType ||
                                   parent.Style?.OwnListStyleType == true;

            string shape;
            if (explicitHtmlType || explicitCssType)
            {
                shape = listStyle.ListStyleType switch
                {
                    ListStyleType.Circle => "circle",
                    ListStyleType.Square => "square",
                    ListStyleType.Disc => "disc",
                    _ => (type ?? "").ToLowerInvariant()
                };
            }
            else
            {
                int depth = 0;
                var p = elem.Parent;
                while (p != null)
                {
                    if (p is DomElement pe && pe.TagName is "ul" or "menu" or "dir")
                        depth++;
                    p = p.Parent;
                }
                shape = depth <= 1 ? "disc" : depth == 2 ? "circle" : "square";
            }

            float markerSize = Math.Clamp((style.FontSize > 0f ? style.FontSize : 16f) * 0.45f, 7f, 9f);
            float cx = box.X - 10f;
            float cy = markerY + (style.FontSize > 0f ? style.FontSize : 16f) * 0.42f;
            float half = markerSize * 0.5f;

            switch (shape)
            {
                case "circle":
                    using (var pen = new Pen(markerColor, 1))
                        g.DrawEllipse(pen, cx - half, cy - half, markerSize, markerSize);
                    break;
                case "square":
                    g.FillRectangle(brush, cx - half, cy - half, markerSize, markerSize);
                    break;
                default:
                    g.FillEllipse(brush, cx - half, cy - half, markerSize, markerSize);
                    break;
            }
        }
    }

    private static string MarkerLetters(int index, char startChar)
    {
        // 1→a, 26→z, 27→aa (the era clamped at zz for sanity)
        if (index <= 0) index = 1;
        var sb = new System.Text.StringBuilder();
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)(startChar + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    private static string MarkerRoman(int index, bool lowercase)
    {
        if (index <= 0) index = 1;
        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] syms = { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            while (index >= values[i])
            {
                sb.Append(syms[i]);
                index -= values[i];
            }
        }
        string result = sb.ToString();
        return lowercase ? result : result.ToUpperInvariant();
    }

    // ─────────────────────────────────────────────────────────────────────
    // DevTools-style overlay
    // ─────────────────────────────────────────────────────────────────────

    // FIX: the four pens used to be allocated PER BOX (inside the recursion)
    // — F11 on a large tree churned thousands of GDI pens per paint.
    private static void PaintBoxOutlines(Graphics g, LayoutBox root)
    {
        using var marginPen = new Pen(Color.FromArgb(160, Color.Orange), 1);
        using var borderPen = new Pen(Color.FromArgb(200, Color.Red), 1);
        using var paddingPen = new Pen(Color.FromArgb(200, Color.Green), 1);
        using var contentPen = new Pen(Color.FromArgb(220, Color.Blue), 1);
        PaintBoxOutlinesCore(g, root, marginPen, borderPen, paddingPen, contentPen);
    }

    private static void PaintBoxOutlinesCore(Graphics g, LayoutBox box,
        Pen marginPen, Pen borderPen, Pen paddingPen, Pen contentPen)
    {
        if (box.Width > 0f || box.Height > 0f)
        {
            static void DrawRect(Graphics g, Pen pen, RectangleF r)
            {
                if (r.Width <= 0f || r.Height <= 0f) return;
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }

            DrawRect(g, marginPen, box.MarginRect);
            DrawRect(g, borderPen, box.BorderRect);
            DrawRect(g, paddingPen, box.PaddingRect);
            DrawRect(g, contentPen, box.ContentRect);
        }

        foreach (var child in box.Children)
            PaintBoxOutlinesCore(g, child, marginPen, borderPen, paddingPen, contentPen);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static DomElement? FindAncestorElement(DomElement elem, string tagName)
    {
        // The element ITSELF counts — a text run directly inside <a> or
        // <font> carries that element as its own style element
        var node = elem as DomNode;
        while (node != null)
        {
            if (node is DomElement de)
            {
                if (de.TagName == tagName) return de;
                if (de.TagName is "body" or "html") break;
            }
            node = node.Parent;
        }
        return null;
    }

    private static ComputedStyle FallbackStyle(DomElement elem)
    {
        var s = new ComputedStyle();
        s.Color = Color.Black;
        s.FontFamily = ["Times New Roman", "serif"];
        s.FontSize = 16f;
        s.Display = DisplayValue.Inline;
        return s;
    }

    /// <summary>Parses a CSS url() value, stripping quotes.</summary>
    internal static string? ParseCssUrl(string? cssValue)
    {
        if (string.IsNullOrWhiteSpace(cssValue) || cssValue == "none")
            return null;

        ReadOnlySpan<char> span = cssValue.AsSpan().Trim();
        if (!span.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            return cssValue;

        // must be at least "url(x)" and properly closed — an unterminated
        // url( used to silently swallow its last character
        if (span.Length < 6 || span[^1] != ')')
            return null;

        span = span[4..^1].Trim();

        if (span.Length >= 2)
        {
            char first = span[0];
            if ((first == '"' || first == '\'') && span[^1] == first)
                span = span[1..^1];
        }

        return span.IsEmpty ? null : span.ToString();
    }

    /// <summary>Parses #rrggbb, #rgb, and named HTML colours.</summary>
    internal static Color ParseHtmlColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Color.Empty;

        try
        {
            value = value.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal) && value.Length == 4)
            {
                char r = value[1], g = value[2], b = value[3];
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