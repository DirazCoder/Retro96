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
/// the shell blits the visible region).  Text is ClearTypeGridFit (matching
/// InlineLayout's measurement hint so advances agree); shapes are
/// anti-aliased; nothing else is resampled.  The output bitmap is capped
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

    private readonly ResourceLoader _resourceLoader;
    private string? _baseUrl;

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
                          float scrollX, float scrollY,   // unused: whole-document render, shell blits
                          DomElement? hoveredElement,
                          bool blinkVisible,
                          bool showBoxOutlines = false,
                          DomElement? focusedElement = null)
    {
        if (rootBox == null)
            return new Bitmap(1, 1);

        _baseUrl = document?.BaseUrl?.ToAbsolute();

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

        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
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

        var body = doc?.ElementDescendants()
                       .FirstOrDefault(e => e.TagName == "body");
        if (body == null) return;

        string? bgAttr = body.GetAttr("background");
        string? bgImg = !string.IsNullOrEmpty(bgAttr)
            ? bgAttr
            : ParseCssUrl(body.Style?.BackgroundImage);
        if (string.IsNullOrEmpty(bgImg))
            return;

        // A failed BODY background image falls back to a clean white page;
        // never expose the broken-image placeholder as a page-wide pattern.
        g.FillRectangle(Brushes.White, 0, 0, w, h);

        try
        {
            string absolute = ImageCache.ResolveUrl(bgImg, _baseUrl);
            var task = images.GetAsync(absolute, _resourceLoader, default);
            // A failed image fetch is represented by the shared broken-image
            // frame so normal <img> elements can keep their reserved box.
            // A BODY/CSS background must not tile that placeholder across the
            // whole document.
            if (images.IsBroken(absolute)) return;
            if (task.IsCompletedSuccessfully && task.Result?.Frames.Count > 0)
            {
                var frame = images.GetCurrentFrame(absolute) ?? task.Result.Frames[0];
                // FIX: a decoded 0×0 frame made the tiling loops below run
                // forever (y += 0) — a hard hang on a corrupt image.
                if (frame.Width <= 0 || frame.Height <= 0) return;

                int drawn = 0;
                for (float y = 0; y < h && drawn < MaxBackgroundTiles; y += frame.Height)
                    for (float x = 0; x < w && drawn < MaxBackgroundTiles; x += frame.Width)
                    {
                        g.DrawImage(frame, x, y);
                        drawn++;
                    }
            }
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Box painting
    // ─────────────────────────────────────────────────────────────────────

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

        foreach (var child in box.Children)
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
        var rect = box.BorderRect;
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
            foreach (var child in box.Children)
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

    private static void PaintBackgroundImage(Graphics g, RectangleF rect,
                                             Image image, ComputedStyle style)
    {
        float iw = image.Width;
        float ih = image.Height;
        if (iw <= 0 || ih <= 0) return;

        float anchorX = rect.X + (style.BackgroundPosition.X / 100f) * (rect.Width - iw);
        float anchorY = rect.Y + (style.BackgroundPosition.Y / 100f) * (rect.Height - ih);

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

        // Paint from the box's layout-resolved widths — they include HTML
        // border attributes the CSS style may know nothing about.
        PaintBorderSide(g, box.BorderRect, box.BorderTop, style.BorderTopStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderTopColor), isTableCell),
                        BorderSide.Top);
        PaintBorderSide(g, box.BorderRect, box.BorderRight, style.BorderRightStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderRightColor), isTableCell),
                        BorderSide.Right);
        PaintBorderSide(g, box.BorderRect, box.BorderBottom, style.BorderBottomStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderBottomColor), isTableCell),
                        BorderSide.Bottom);
        PaintBorderSide(g, box.BorderRect, box.BorderLeft, style.BorderLeftStyle,
                        BorderColorFor(BorderColorOrBlack(style.BorderLeftColor), isTableCell),
                        BorderSide.Left);
    }

    /// <summary>Table cell rules were grey (#808080), not text-black.</summary>
    private static Color BorderColorFor(Color cssColor, bool isTableCell) =>
        isTableCell && cssColor == Color.Black
            ? Color.FromArgb(0x80, 0x80, 0x80)
            : cssColor;

    /// <summary>An unset border colour paints black (an Empty colour makes an invisible pen).</summary>
    private static Color BorderColorOrBlack(Color c) => c == Color.Empty ? Color.Black : c;

    private static void PaintTableOuterBorder(Graphics g, LayoutBox box)
    {
        var rect = box.BorderRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        float w = Math.Max(1, box.BorderTop);
        using var penDark = new Pen(Color.FromArgb(0x80, 0x80, 0x80), 1);
        using var penLight = new Pen(Color.White, 1);

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
        PaintSunkenRect(g, rect, 2);
    }

    /// <summary>
    /// &lt;hr&gt;: SIZE thick 3-D inset rule; NOSHADE draws a flat grey bar.
    /// Width/size/alignment were resolved during layout.
    /// </summary>
    private static void PaintHr(Graphics g, LayoutBox box)
    {
        bool noshade = box.Element!.HasAttr("noshade");

        var rect = box.ContentRect;
        float x1 = rect.X;
        float x2 = rect.X + rect.Width;
        int thick = Math.Max(1, (int)Math.Round(box.Height));

        if (noshade)
        {
            // Filled bar flush with the top of the box — the old pen-based
            // draw was centred on the edge and spilled half the bar above
            g.FillRectangle(SolidBrushFor(Color.FromArgb(0x80, 0x80, 0x80)),
                x1, rect.Y, rect.Width, thick);
            return;
        }

        // 3-D inset: dark top half, light bottom half
        using var penDark = new Pen(Color.FromArgb(0x80, 0x80, 0x80), 1);
        using var penLight = new Pen(Color.White, 1);
        int half = (thick + 1) / 2;
        for (int i = 0; i < thick; i++)
        {
            float y = rect.Y + i;
            g.DrawLine(i < half ? penDark : penLight, x1, y, x2, y);
        }
    }

    private enum BorderSide { Top, Right, Bottom, Left }

    private static void PaintBorderSide(Graphics g, RectangleF rect, float width,
        BorderStyleValue bStyle, Color color, BorderSide side)
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
            Paint3DBorder(g, rect, bStyle, w, color, side);
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
        BorderStyleValue style, int w, Color baseColor, BorderSide side)
    {
        if (w < 1) return;

        Color light = ControlPaint.LightLight(baseColor);
        Color midLight = ControlPaint.Light(baseColor);
        Color dark = ControlPaint.Dark(baseColor);
        Color darkDark = ControlPaint.DarkDark(baseColor);

        bool topLeft = side is BorderSide.Top or BorderSide.Left;
        bool raised = style is BorderStyleValue.Ridge or BorderStyleValue.Outset;

        if (style is BorderStyleValue.Inset or BorderStyleValue.Outset)
        {
            Color shade = (raised == topLeft) ? light : darkDark;
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
                            ownerDoc.BaseUrl?.Resolve(href).ToAbsolute() ?? href);
                }
                catch
                {
                    // Odd schemes (snews:, cid:, …) in an href must not take
                    // the whole paint down — treat as unvisited.
                }

                bool hovered = linkAnchor == hoveredElement;

                // Author CSS (a:link { color: ... }) beats the engine's link
                // palette. OwnColor is tracked by the cascade, so this does
                // not depend on comparing the authored colour with a fallback
                // that may happen to be identical.
                Color docLink = StyleResolver.GetLinkColor(ownerDoc);
                bool authorStyled = linkAnchor!.Style?.OwnColor == true;

                if (!authorStyled)
                {
                    if (hovered)
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

        if (style.FontVariant == FontVariantValue.SmallCaps)
            PaintSmallCapsText(g, contentRect.X, contentRect.Y, text, style, fonts, textColor, sf);
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
                PaintTextControl(g, box, fonts, style, "_", isFocused);
                return;

            default:    // text, password, and unknown → text field
                string value = elem.GetAttr("value") ?? "";
                if (type == "password") value = new string('*', value.Length);
                PaintTextControl(g, box, fonts, style, value, isFocused);
                return;
        }
    }

    /// <summary>2-tone sunken (inset) rectangle frame, `size` px thick.</summary>
    private static void PaintSunkenRect(Graphics g, RectangleF rect, int size)
    {
        using var penDark = new Pen(Color.FromArgb(0x80, 0x80, 0x80), 1);
        using var penLight = new Pen(Color.White, 1);
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
    private static void PaintRaisedRect(Graphics g, RectangleF rect, int size)
    {
        using var penLight = new Pen(Color.White, 1);
        using var penDark = new Pen(Color.FromArgb(0x80, 0x80, 0x80), 1);
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

    private static void PaintTextControl(Graphics g, LayoutBox box, FontCache fonts,
                                         ComputedStyle style, string text, bool isFocused)
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
        PaintSunkenRect(g, rect, 2);

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
        var clip = g.Save();
        g.SetClip(textRect, CombineMode.Intersect);
        g.DrawString(text, font, brush, textRect, sf);
        g.Restore(clip);
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

    private static void PaintSelect(Graphics g, LayoutBox box, FontCache fonts)
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
        var options = elem.Descendants()
            .OfType<DomElement>()
            .Where(o => o.TagName == "option")
            .ToList();

        using var faceBrush = new SolidBrush(disabled
            ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.White);
        g.FillRectangle(faceBrush, rect.X, rect.Y, rect.Width, rect.Height);
        PaintSunkenRect(g, rect, 2);

        if (isListbox && options.Count > 0)
        {
            // Listbox: show min(size, options) rows
            float rowH = font.GetHeight(g) + 2;
            int rows = Math.Min(sizeAttr > 1 ? sizeAttr : 4, options.Count);
            for (int i = 0; i < rows; i++)
            {
                var opt = options[i];
                float rowY = face.Y + i * rowH;
                if (rowY + rowH > face.Bottom) break;

                bool selected = opt.HasAttr("selected");
                if (selected)
                    g.FillRectangle(SystemBrushes.Highlight,
                        face.X + 1, rowY, face.Width - 2, rowH);

                using var brush = new SolidBrush(disabled
                    ? Color.Gray : selected ? Color.White : Color.Black);
                g.DrawString(GlyphSubstitution.MapGlyphs((opt.InnerText ?? "").Trim()), font, brush,
                    face.X + 3, rowY + 1);
            }
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

    private static void PaintTextarea(Graphics g, LayoutBox box, FontCache fonts,
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
        PaintSunkenRect(g, rect, 2);

        if (isFocused)
        {
            using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
            g.DrawRectangle(focusPen, face.X, face.Y, face.Width - 1, face.Height - 1);
        }

        bool bold = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle is FontStyleValue.Italic or FontStyleValue.Oblique;
        var font = ResolveFont(fonts, style);

        string text = elem.InnerText ?? "";

        using var brush = new SolidBrush(fgColor);
        bool wrapOff = elem.GetAttrOrDefault("wrap", "").Trim().ToLowerInvariant() == "off";

        var lines = TextareaOverlay.BreakLines(g, text, font,
            Math.Max(1f, face.Width - 6), wrapOff);
        float lineHeight = font.GetHeight(g);
        int visibleLines = Math.Max(1, (int)Math.Floor((face.Height - 4) / lineHeight));
        var textClip = g.Save();
        g.SetClip(new RectangleF(face.X + 1, face.Y + 1,
            Math.Max(1, face.Width - 2), Math.Max(1, face.Height - 2)),
            CombineMode.Intersect);
        TextareaOverlay.DrawLines(g, text, font, lines, brush,
            face.X + 3, face.Y + 2, face.Width - 6, lineHeight);
        g.Restore(textClip);

        if (lines.Count > visibleLines)
            PaintTextareaScrollbar(g, face, lines.Count, visibleLines);
    }

    private static void PaintTextareaScrollbar(Graphics g, RectangleF face,
                                               int lineCount, int visibleLines)
    {
        if (lineCount <= visibleLines || face.Width < 12 || face.Height < 8) return;

        float trackX = face.Right - 12;
        float trackY = face.Top + 1;
        float trackHeight = Math.Max(1, face.Height - 2);
        using var track = new SolidBrush(Color.FromArgb(0xE0, 0xE0, 0xE0));
        using var thumb = new SolidBrush(Color.FromArgb(0x80, 0x80, 0x80));
        g.FillRectangle(track, trackX, trackY, 11, trackHeight);

        float thumbHeight = Math.Max(10, trackHeight * visibleLines / lineCount);
        g.FillRectangle(thumb, trackX + 1, trackY + 1, 9,
            Math.Min(trackHeight - 2, thumbHeight - 2));
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
        if (pressed)
            PaintSunkenRect(g, rect, 2);
        else
            PaintRaisedRect(g, rect, 2);

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

        if (listStyle.ListStyleImage is { Length: > 0 } imageUrl)
        {
            string absolute = ImageCache.ResolveUrl(imageUrl, _baseUrl);
            if (TryGetFrame(images, absolute, out var frame) && frame != null)
            {
                float markerX = box.X - frame.Width - 4f;
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
            float markerRight = box.X - 4;
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

            string shape = listStyle.ListStyleType switch
            {
                ListStyleType.Circle => "circle",
                ListStyleType.Square => "square",
                ListStyleType.Disc => "disc",
                _ => (type ?? "").ToLowerInvariant()
            };

            if (shape.Length == 0)
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