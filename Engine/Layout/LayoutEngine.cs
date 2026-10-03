using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;

namespace Retro96.Engine.Layout;

using System.Diagnostics;

/// <summary>
/// Orchestrates the complete layout pipeline for the Retro96 1996-era browser engine.
///
/// Coordinate model (consistent throughout):
///   box.X / box.Y   = border-box origin  (margins are external to X/Y)
///   box.Width        = content width      (excludes margins, borders, padding)
///   box.Height       = content height     (excludes margins, borders, padding)
///
/// Visual box model for a child placed inside a parent:
///   child border-box left  = parentContentX + child.MarginLeft
///   child border-box top   = currentY       + collapsedTopMargin
///   child content left     = child.X + child.BorderLeft + child.PaddingLeft
///   child content top      = child.Y + child.BorderTop  + child.PaddingTop
///
/// Quirks implemented (Netscape Navigator 2 / 3, 1996):
///   NN23   – &lt;spacer type="horizontal"&gt;  inline spacer
///   NN24   – &lt;spacer type="vertical"&gt;    block  spacer
///   NN25   – &lt;multicol cols="N"&gt;          multi-column block extension
///   NN-BLINK   – &lt;blink&gt;               inline; toggled externally by BrowserCanvas timer
///   NN-CENTER  – &lt;center&gt;              block, text-align:center
///   NN-FONT    – &lt;font size color face&gt; inline; attributes read by Renderer
///   NN-NOBR    – &lt;nobr&gt;               inline; no-break text (NN2 extension)
///   IE-MARQUEE – &lt;marquee&gt;             block; scrolled externally by BrowserCanvas timer
/// </summary>
public static class LayoutEngine
{
    private static readonly bool _debugLayout = true; // Set to false to disable layout logging
    // ─────────────────────────────────────────────────────────────────────────
    // 1996-era defaults  (Netscape Navigator 2 / 3 defaults)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Default body side margins (pixels).  NN2 used 8 px on all sides.</summary>
    private const float DefaultBodyMarginH = 8f;
    private const float DefaultBodyMarginV = 8f;

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the complete layout tree for a parsed DOM document.
    /// Returns a root <see cref="LayoutBox"/> whose descendants cover the entire
    /// document.  The root box itself is an anonymous block spanning the viewport.
    /// </summary>
    public static LayoutBox BuildLayoutTree(
        DomDocument document,
        float viewportWidth,
        float viewportHeight)
    {
        // Guard: null document → empty viewport box
        if (document == null)
            return MakeRootBox(null, viewportWidth, viewportHeight);

        // ── Frameset document? ────────────────────────────────────────────
        // Check both the document's first child and inside <html>.
        var directFrameset = document.FirstChild as DomElement;
        if (directFrameset?.TagName == "frameset")
            return LayoutFrameset(directFrameset, viewportWidth, viewportHeight);

        var innerFrameset = document.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "frameset");
        if (innerFrameset != null)
            return LayoutFrameset(innerFrameset, viewportWidth, viewportHeight);

        // ── Locate <body> ─────────────────────────────────────────────────
        var body = document.ElementDescendants()
                           .FirstOrDefault(e => e.TagName == "body");
        if (body == null)
            return MakeRootBox(null, viewportWidth, viewportHeight);

        // ── Viewport root box (anonymous, never painted) ──────────────────
        var rootBox = MakeRootBox(null, viewportWidth, viewportHeight);

        // ── Body box ──────────────────────────────────────────────────────
        //
        // CRITICAL FIX: The original code returned an empty tree when body.Style
        // was null (e.g. StyleResolver did not run, or body has no author styles).
        // This produced a black canvas with a scrollbar but no content.
        // We now fall back to the Netscape Navigator 2 UA defaults instead of
        // bailing out.
        //
        var bodyBox = BuildBodyBox(body, rootBox, viewportWidth);
        rootBox.Children.Add(bodyBox);

        // ── Generate all descendant boxes ─────────────────────────────────
        var rawChildren = GenerateBoxes(body, bodyBox, bodyBox.Width);
        NormaliseAndAttach(rawChildren, bodyBox);

        // ── Block layout pass ─────────────────────────────────────────────
        LayoutBlock(bodyBox, viewportWidth, viewportHeight);

        // Expand root to contain full document height
        float docBottom = bodyBox.Y
                        + bodyBox.BorderTop  + bodyBox.PaddingTop
                        + bodyBox.Height
                        + bodyBox.PaddingBottom + bodyBox.BorderBottom
                        + bodyBox.MarginBottom;
        rootBox.Height = Math.Max(viewportHeight, docBottom);

        return rootBox;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body box construction
    // ─────────────────────────────────────────────────────────────────────────

    private static LayoutBox BuildBodyBox(
        DomElement body, LayoutBox rootBox, float viewportWidth)
    {
        // Determine margins: HTML presentational attrs > CSS > UA default
        float mH = DefaultBodyMarginH;
        float mV = DefaultBodyMarginV;

        int lm = Math.Max(body.GetAttrInt("leftmargin",   -1),
                          body.GetAttrInt("marginwidth",  -1));
        int tm = Math.Max(body.GetAttrInt("topmargin",    -1),
                          body.GetAttrInt("marginheight", -1));
        if (lm >= 0) mH = lm;
        if (tm >= 0) mV = tm;

        // Author CSS margins override HTML attrs
        var style = body.Style; // may be null
        float marginL  = (style != null && style.MarginLeft   >= 0) ? style.MarginLeft   : mH;
        float marginR  = (style != null && style.MarginRight  >= 0) ? style.MarginRight  : mH;
        float marginT  = (style != null && style.MarginTop    >= 0) ? style.MarginTop    : mV;
        float marginB  = (style != null && style.MarginBottom >= 0) ? style.MarginBottom : mV;

        float padL = style?.PaddingLeft   ?? 0f;
        float padR = style?.PaddingRight  ?? 0f;
        float padT = style?.PaddingTop    ?? 0f;
        float padB = style?.PaddingBottom ?? 0f;

        float borL = style?.BorderLeftWidth   ?? 0f;
        float borR = style?.BorderRightWidth  ?? 0f;
        float borT = style?.BorderTopWidth    ?? 0f;
        float borB = style?.BorderBottomWidth ?? 0f;

        // Content width = viewport − all horizontal box-model values
        float contentW = Math.Max(0f,
            viewportWidth - marginL - marginR - borL - borR - padL - padR);

        // body.X = border-box left (= left margin away from viewport left)
        var bodyBox = new LayoutBox(body, BoxType.Block)
        {
            X            = marginL + borL,     // border-box origin X
            Y            = marginT + borT,     // border-box origin Y
            Width        = contentW,
            Height       = 0,                  // set by LayoutBlock
            MarginTop    = marginT,
            MarginRight  = marginR,
            MarginBottom = marginB,
            MarginLeft   = marginL,
            PaddingTop   = padT,
            PaddingRight = padR,
            PaddingBottom= padB,
            PaddingLeft  = padL,
            BorderTop    = borT,
            BorderRight  = borR,
            BorderBottom = borB,
            BorderLeft   = borL,
            Parent       = rootBox
        };

        return bodyBox;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Box-tree construction (recursive)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a flat list of raw <see cref="LayoutBox"/> objects for every
    /// direct child of <paramref name="element"/>.  Does NOT attach them to
    /// <paramref name="parentBox"/>; the caller does that via
    /// <see cref="NormaliseAndAttach"/>.
    /// </summary>
    private static List<LayoutBox> GenerateBoxes(
        DomElement element, LayoutBox parentBox, float containingWidth)
    {
        var result = new List<LayoutBox>();

        foreach (var node in element.Children)
        {
            switch (node)
            {
                // ── Element nodes ──────────────────────────────────────────
                case DomElement elem:
                {
                    // Skip non-visual structural elements entirely
                    if (IsNonVisualTag(elem.TagName))
                        break;

                    // and Resolve style: author stylesheet → UA fallback
                    var style = elem.Style ?? FallbackStyleFor(elem);
                    if (style == null)
                        break; // null = "no box" (e.g. <map>, <area>)

                    // this is display:none and it removes the element from the layout tree 
                    if (style.Display == DisplayValue.None)
                        break;

                    // ── <spacer> (NN23 / NN24) ─────────────────────────
                    if (elem.TagName == "spacer")
                    {
                        var spacer = CreateSpacerBox(elem, parentBox);
                        if (spacer != null) result.Add(spacer);
                        break;
                    }

                    var boxType = DetermineBoxType(elem, style);

                    // ── Flatten inline containers ──────────────────────
                    // InlineLayout only measures top-level items in the
                    // inline child list.  An inline wrapper box (e.g. <a>,
                    // <b>, <span>, <font>) has no TextRun and no replaced
                    // content, so its width measures as 0 and its children
                    // are never seen.  The fix: emit children directly into
                    // the parent flow.  The DOM tree still exists so the
                    // Renderer can walk ancestors to find <a> link colours,
                    // <font> overrides, etc.  Text nodes already reference
                    // the inline element as their Element for style lookup.
                    //
                    // Exceptions:
                    //   <br>   – must stay a box; InlineLayout checks TagName
                    //            to trigger a line break.
                    //   <blink>– must stay a box; PaintBox checks TagName to
                    //            toggle visibility.
                    if (boxType == BoxType.Inline
                        && elem.TagName != "br"
                        && elem.TagName != "blink")
                    {
                        var flatChildren = GenerateBoxes(elem, parentBox, containingWidth);
                        result.AddRange(flatChildren);
                        break;
                    }

                    var box     = new LayoutBox(elem, boxType) { Parent = parentBox };

                    ApplyStylesToBox(box, style);
                    ApplyHtmlPresentationalAttrs(box, elem, containingWidth);

                    // Recurse into children (replaced elements do not generate
                    // descendant layout boxes here; table cells do)
                    if (boxType != BoxType.Replaced)
                    {
                        var childRaw = GenerateBoxes(elem, box, containingWidth);
                        NormaliseAndAttach(childRaw, box);
                    }

                    result.Add(box);
                    break;
                }

                // ── Text nodes ─────────────────────────────────────────────
                case DomText textNode:
                {
                    // Collapse whitespace per HTML 3.2 rules
                    string data = CollapseWhitespace(textNode.Data ?? "");
                    if (string.IsNullOrEmpty(data))
                        break;

                    // Text boxes inherit the PARENT ELEMENT so that the Renderer
                    // can read font/color/alignment from box.Element.Style.
                    // FIX: original used null element, causing renderer to bail.
                    var textBox = new LayoutBox(element, BoxType.Inline)
                    {
                        TextRun = data,
                        Parent  = parentBox
                    };
                    result.Add(textBox);
                    break;
                }
            }
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Anonymous block-box normalisation  (CSS 2.1 §9.2.1.1)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When a block container holds both block-level and inline-level children,
    /// each contiguous run of inline children is wrapped in an anonymous block.
    ///
    /// FIX: the original engine wrapped ALL inlines into a SINGLE anonymous block
    /// regardless of ordering, which placed "text after a paragraph" inside the
    /// same anonymous box as "text before a paragraph".  This rewrite creates one
    /// anonymous box per contiguous inline run, preserving document order.
    /// </summary>
    private static void NormaliseAndAttach(List<LayoutBox> rawBoxes, LayoutBox parent)
    {
        if (rawBoxes.Count == 0)
            return;

        bool hasBlock  = rawBoxes.Any(b => IsBlockLevel(b));
        bool hasInline = rawBoxes.Any(b => !IsBlockLevel(b));

        if (!hasBlock || !hasInline)
        {
            // Homogeneous list – attach directly
            foreach (var b in rawBoxes)
            {
                parent.Children.Add(b);
                b.Parent = parent;
            }
            return;
        }

        // Mixed list – wrap each contiguous inline run in an anonymous block
        var inlineRun = new List<LayoutBox>();

        foreach (var b in rawBoxes)
        {
            if (IsBlockLevel(b))
            {
                if (inlineRun.Count > 0)
                {
                    FlushAnonBlock(inlineRun, parent);
                    inlineRun.Clear();
                }
                parent.Children.Add(b);
                b.Parent = parent;
            }
            else
            {
                inlineRun.Add(b);
            }
        }

        if (inlineRun.Count > 0)
            FlushAnonBlock(inlineRun, parent);
    }

    private static void FlushAnonBlock(List<LayoutBox> inlines, LayoutBox parent)
    {
        var anon = new LayoutBox(null, BoxType.Anonymous) { Parent = parent };
        foreach (var b in inlines)
        {
            anon.Children.Add(b);
            b.Parent = anon;
        }
        parent.Children.Add(anon);
    }

    // FIX: TableCell / TableCaption were missing from this list. NormaliseAndAttach
    // uses IsBlockLevel to decide which children of ANY parent get wrapped in a
    // synthetic anonymous box vs. attached directly — and it runs generically over
    // every parent's children, not just <body>/<div>-style block containers. A <td>
    // (or a stray <td> that ends up as a direct child of <table>/<tbody> due to a
    // parser-recovery quirk around malformed/omitted <tr> tags) was being treated as
    // "inline" by this check, so NormaliseAndAttach wrapped it — and anything else in
    // that same run — inside a new Anonymous box instead of attaching it as a real
    // table-structural child. TableLayout's BuildRowList only recognises <tr>/<tbody>-
    // style children of a <table> (via IsRowGroup/IsRow); an Anonymous wrapper sitting
    // where a <tr> or <td> was expected is invisible to it, so every cell that ended
    // up inside one of these anon boxes silently dropped out of the table entirely —
    // that's the "half the rows are 0x0 (anon) siblings of real <tr>s" pattern the ZZ
    // flag in LayoutDebug caught. TableRow was already listed, which is why real <tr>
    // boxes (whose own children are cells, not other <tr>s) were fine; cells one level
    // down were the gap.
    private static bool IsBlockLevel(LayoutBox b) => b.BoxType is
        BoxType.Block     or BoxType.Table    or BoxType.ListItem
        or BoxType.Anonymous or BoxType.TableRow or BoxType.TableCell
        or BoxType.TableCaption or BoxType.Frame;

    // ─────────────────────────────────────────────────────────────────────────
    // Spacer  (NN23 / NN24)
    // ─────────────────────────────────────────────────────────────────────────

    /// <quirk id="NN23" /> type="horizontal" → inline spacer, width=size
    /// <quirk id="NN24" /> type="vertical"   → block  spacer, height=size
    private static LayoutBox? CreateSpacerBox(DomElement elem, LayoutBox parentBox)
    {
        string type   = elem.GetAttrOrDefault("type", "horizontal");
        int    size   = elem.GetAttrInt("size",   0);
        int    width  = elem.GetAttrInt("width",  size);
        int    height = elem.GetAttrInt("height", size);

        return type switch
        {
            "horizontal" => new LayoutBox(elem, BoxType.Inline)
                            { Width = Math.Max(1, width), Height = 1, Parent = parentBox },
            "vertical"   => new LayoutBox(elem, BoxType.Block)
                            { Width = 1, Height = Math.Max(1, height), Parent = parentBox },
            "block"      => new LayoutBox(elem, BoxType.Block)
                            { Width = Math.Max(1, width), Height = Math.Max(1, height), Parent = parentBox },
            _            => null
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Style helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static BoxType DetermineBoxType(DomElement elem, ComputedStyle style)
    {
        // Replaced content: single visual unit sized by attributes or intrinsic dimensions
        if (elem.TagName is "img"      or "input"   or "button"  or "select"
                          or "textarea" or "object" or "embed"   or "applet"
                          or "hr"      or "canvas")
            return BoxType.Replaced;

        // Table structure
        if (style.Display == DisplayValue.Table)
            return BoxType.Table;
        if (style.Display == DisplayValue.TableRow)
            return BoxType.TableRow;
        if (style.Display == DisplayValue.TableCell ||
            style.Display == DisplayValue.TableCaption)
            return BoxType.TableCell;

        // List items
        if (style.Display == DisplayValue.ListItem)
            return BoxType.ListItem;

        // General block / inline
        return style.Display switch
        {
            DisplayValue.Block       => BoxType.Block,
            DisplayValue.InlineBlock => BoxType.InlineBlock,
            _                        => BoxType.Inline
        };
    }

    /// <summary>
    /// Copies all CSS box-model properties from <paramref name="style"/> into
    /// <paramref name="box"/>.
    /// </summary>
    private static void ApplyStylesToBox(LayoutBox box, ComputedStyle style)
    {
        box.MarginTop    = style.MarginTop;
        box.MarginRight  = style.MarginRight;
        box.MarginBottom = style.MarginBottom;
        box.MarginLeft   = style.MarginLeft;

        box.PaddingTop    = style.PaddingTop;
        box.PaddingRight  = style.PaddingRight;
        box.PaddingBottom = style.PaddingBottom;
        box.PaddingLeft   = style.PaddingLeft;

        box.BorderTop    = style.BorderTopWidth;
        box.BorderRight  = style.BorderRightWidth;
        box.BorderBottom = style.BorderBottomWidth;
        box.BorderLeft   = style.BorderLeftWidth;

        box.IsFloated              = style.Float != FloatValue.None;
        box.FloatSide              = style.Float;
        box.IsAbsolutelyPositioned = style.Position == PositionValue.Absolute
                                  || style.Position == PositionValue.Fixed;
    }

    /// <summary>
    /// Applies 1996-era HTML presentational attributes to a layout box.
    /// These attributes (width, height, hspace, vspace, border, align, …) were
    /// the primary layout mechanism before CSS and must be honoured as-is.
    /// </summary>
    private static void ApplyHtmlPresentationalAttrs(
        LayoutBox box, DomElement elem, float containingWidth)
    {
        // width ────────────────────────────────────────────────────────────
        string? wAttr = elem.GetAttr("width");
        if (!string.IsNullOrEmpty(wAttr))
        {
            float pct = 0f;
            bool hasPct = false;
            if (wAttr.TrimEnd() == "100%")
            {
                pct = 100f;
                hasPct = true;
            }
            else if (wAttr.EndsWith('%') && float.TryParse(wAttr.TrimEnd('%'), out pct))
            {
                hasPct = true;
            }
            if (hasPct)
            {
                // Resolve percentage relative to containing width
                float computed = containingWidth * pct / 100f
                               - box.MarginLeft - box.MarginRight
                               - box.BorderLeft - box.BorderRight
                               - box.PaddingLeft - box.PaddingRight;
                if (computed > 0f) box.Width = computed;
            }
            else if (float.TryParse(wAttr, out float wpx) && wpx > 0f)
            {
                box.Width = wpx;
            }
        }

        // height ───────────────────────────────────────────────────────────
        string? hAttr = elem.GetAttr("height");
        if (!string.IsNullOrEmpty(hAttr) &&
            float.TryParse(hAttr, out float hpx) && hpx > 0f)
        {
            box.Height = hpx;
        }

        // hspace / vspace  (NN2 image spacing extension) ──────────────────
        int hspace = elem.GetAttrInt("hspace", 0);
        int vspace = elem.GetAttrInt("vspace", 0);
        if (hspace > 0)
        {
            box.MarginLeft  = Math.Max(box.MarginLeft,  hspace);
            box.MarginRight = Math.Max(box.MarginRight, hspace);
        }
        if (vspace > 0)
        {
            box.MarginTop    = Math.Max(box.MarginTop,    vspace);
            box.MarginBottom = Math.Max(box.MarginBottom, vspace);
        }

        // border on <img> and <table> (HTML attribute, not CSS property) ──
        if (elem.TagName is "img" or "table")
        {
            int bw = elem.GetAttrInt("border", -1);
            if (bw >= 0)
            {
                box.BorderTop    = bw;
                box.BorderRight  = bw;
                box.BorderBottom = bw;
                box.BorderLeft   = bw;
            }
        }

        // align="left|right" on images → float ────────────────────────────
        if (elem.TagName == "img")
        {
            string align = elem.GetAttrOrDefault("align", "").ToLowerInvariant();
            if (align == "left")
            {
                box.IsFloated = true;
                box.FloatSide = FloatValue.Left;
            }
            else if (align == "right")
            {
                box.IsFloated = true;
                box.FloatSide = FloatValue.Right;
            }
        }

        // ── Default / intrinsic sizes for replaced elements ────────────────
        //
        // Without these, any Replaced box that has no explicit width/height
        // attribute ends up 0×0 and is completely invisible in the layout.
        //
        // img   → 32×32 placeholder; the Renderer overwrites with the real
        //         bitmap once the image cache has loaded.
        // input / select / textarea / button → browser-default control sizes.
        // hr    → always needs a non-zero height to be painted.
        //
        switch (elem.TagName)
        {
            case "img":
                if (box.Width  <= 0f) box.Width  = 32f;
                if (box.Height <= 0f) box.Height = 32f;
                break;

            case "input":
            {
                if (box.Height <= 0f) box.Height = 22f;
                if (box.Width  <= 0f)
                {
                    string t = elem.GetAttrOrDefault("type", "text").ToLowerInvariant();
                    box.Width = t switch
                    {
                        "hidden"               => 0f,   // hidden inputs have no visual
                        "checkbox" or "radio" => 16f,
                        "submit" or "reset"   => 80f,
                        "button"              => 80f,
                        "image"               => 32f,
                        _                     => 150f   // text, password, email, …
                    };
                }
                break;
            }

            case "select":
                if (box.Width  <= 0f) box.Width  = 150f;
                if (box.Height <= 0f) box.Height = 22f;
                break;

            case "textarea":
                if (box.Width  <= 0f) box.Width  = elem.GetAttrInt("cols", 20) * 8f;
                if (box.Height <= 0f) box.Height = elem.GetAttrInt("rows",  4) * 16f;
                break;

            case "button":
                if (box.Width  <= 0f) box.Width  = 80f;
                if (box.Height <= 0f) box.Height = 22f;
                break;

            case "hr":
                if (box.Height <= 0f) box.Height = 2f;
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 1996-era UA stylesheet (Netscape Navigator 2 / 3 defaults)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="ComputedStyle"/> matching the Netscape Navigator 2/3
    /// User-Agent stylesheet for the given HTML element.
    /// Returns <c>null</c> for purely non-visual elements (&lt;map&gt;, &lt;area&gt;).
    ///
    /// This method is called only when <c>elem.Style</c> is null, i.e. when
    /// StyleResolver did not produce a computed style (e.g. it did not run, or
    /// the document is synthetic such as an error page).
    /// </summary>
    private static ComputedStyle? FallbackStyleFor(DomElement elem)
    {
        var s = new ComputedStyle();

        switch (elem.TagName)
        {
            // ── Headings ──────────────────────────────────────────────────
            case "h1":
                s.Display = DisplayValue.Block;
                s.MarginTop = 14; s.MarginBottom = 14; break;
            case "h2":
                s.Display = DisplayValue.Block;
                s.MarginTop = 12; s.MarginBottom = 12; break;
            case "h3":
                s.Display = DisplayValue.Block;
                s.MarginTop = 10; s.MarginBottom = 10; break;
            case "h4":
                s.Display = DisplayValue.Block;
                s.MarginTop = 8;  s.MarginBottom = 8;  break;
            case "h5": case "h6":
                s.Display = DisplayValue.Block;
                s.MarginTop = 6;  s.MarginBottom = 6;  break;

            // ── Paragraph / generic block ──────────────────────────────────
            case "p":
                s.Display = DisplayValue.Block;
                s.MarginTop = 8; s.MarginBottom = 8; break;
            case "div":
                s.Display = DisplayValue.Block; break;

            // HTML5 semantic elements treated as block (graceful degradation)
            case "article": case "section": case "aside":
            case "header":  case "footer":  case "main": case "nav": case "figure":
                s.Display = DisplayValue.Block; break;

            // ── <center>  (NN extension – block + center alignment) ────────
            case "center":
                s.Display   = DisplayValue.Block;
                s.TextAlign = TextAlign.Center; break;

            // ── Blockquote ─────────────────────────────────────────────────
            case "blockquote":
                s.Display    = DisplayValue.Block;
                s.MarginLeft = 40; s.MarginTop = 8; s.MarginBottom = 8; break;

            // ── Lists ──────────────────────────────────────────────────────
            case "ul": case "ol": case "menu": case "dir":
                s.Display    = DisplayValue.Block;
                s.MarginLeft = 40; s.MarginTop = 8; s.MarginBottom = 8; break;
            case "li":
                s.Display    = DisplayValue.ListItem;
                s.MarginBottom = 2; break;
            case "dl":
                s.Display = DisplayValue.Block;
                s.MarginTop = 8; s.MarginBottom = 8; break;
            case "dt":
                s.Display = DisplayValue.Block; break;
            case "dd":
                s.Display    = DisplayValue.Block;
                s.MarginLeft = 40; break;

            // ── Preformatted ───────────────────────────────────────────────
            case "pre":
                s.Display      = DisplayValue.Block;
                s.MarginTop    = 8; s.MarginBottom = 8; break;

            // ── Tables ─────────────────────────────────────────────────────
            case "table":
                s.Display    = DisplayValue.Table;
                s.MarginTop  = 4; s.MarginBottom = 4; break;
            case "tbody": case "thead": case "tfoot":
                s.Display = DisplayValue.TableRow; break;  // row-group → row for simplicity
            case "tr":
                s.Display = DisplayValue.TableRow; break;
            case "td":
                s.Display       = DisplayValue.TableCell;
                s.PaddingTop    = 2; s.PaddingRight  = 5;
                s.PaddingBottom = 2; s.PaddingLeft   = 5; break;
            case "th":
                s.Display       = DisplayValue.TableCell;
                s.PaddingTop    = 2; s.PaddingRight  = 5;
                s.PaddingBottom = 2; s.PaddingLeft   = 5; break;
            case "caption":
                s.Display = DisplayValue.TableCaption; break;
            case "col": case "colgroup":
                s.Display = DisplayValue.None; break;

            // ── Forms ──────────────────────────────────────────────────────
            case "form":
                s.Display      = DisplayValue.Block;
                s.MarginBottom = 8; break;
            case "fieldset":
                s.Display         = DisplayValue.Block;
                s.BorderTopWidth  = 1; s.BorderRightWidth   = 1;
                s.BorderBottomWidth = 1; s.BorderLeftWidth  = 1;
                s.PaddingTop      = 6; s.PaddingRight      = 6;
                s.PaddingBottom   = 6; s.PaddingLeft       = 6; break;
            case "legend": case "label":
                s.Display = DisplayValue.Inline; break;

            // ── Replaced inline (sized by attributes or intrinsic dims) ────
            // Form controls are inline-block: they sit in inline flow but have
            // a real block box (width + height from attributes / UA defaults).
            case "img":
            case "object":
            case "embed":
            case "applet":
                s.Display = DisplayValue.Inline; break;

            case "input":
            case "select":
            case "textarea":
                s.Display           = DisplayValue.InlineBlock;
                s.BorderTopStyle    = s.BorderRightStyle
                                     = s.BorderBottomStyle
                                     = s.BorderLeftStyle    = BorderStyleValue.Inset;
                s.BorderTopWidth    = s.BorderRightWidth
                                     = s.BorderBottomWidth
                                     = s.BorderLeftWidth    = 2; break;
            case "button":
                s.Display           = DisplayValue.InlineBlock;
                s.BorderTopStyle    = s.BorderRightStyle
                                     = s.BorderBottomStyle
                                     = s.BorderLeftStyle    = BorderStyleValue.Outset;
                s.BorderTopWidth    = s.BorderRightWidth
                                     = s.BorderBottomWidth
                                     = s.BorderLeftWidth    = 2; break;

            // ── <hr> ───────────────────────────────────────────────────────
            case "hr":
                s.Display      = DisplayValue.Block;
                s.MarginTop    = 4; s.MarginBottom = 4; break;

            // ── Common inline elements ─────────────────────────────────────
            case "a":     case "span":    case "abbr":   case "acronym":
            case "cite":  case "q":       case "dfn":
            case "b":     case "strong":
            case "i":     case "em":
            case "u":     case "s":       case "strike":
            case "big":   case "small":
            case "sup":   case "sub":
            case "ins":   case "del":
            case "code":  case "tt":      case "kbd":    case "samp": case "var":
                s.Display = DisplayValue.Inline; break;

            // ── NN2/3 extensions ───────────────────────────────────────────
            case "font":                             // NN-FONT
                s.Display = DisplayValue.Inline; break;
            case "blink":                            // NN-BLINK
                s.Display = DisplayValue.Inline; break;
            case "nobr":                             // NN no-break
                s.Display = DisplayValue.Inline; break;
            case "wbr":                              // soft line-break
                s.Display = DisplayValue.Inline; break;
            case "marquee":                          // IE / NN marquee
                s.Display = DisplayValue.Block; break;
            case "multicol":                         // NN25
                s.Display = DisplayValue.Block; break;
            case "spacer":                           // NN23 / NN24 (handled separately)
                s.Display = DisplayValue.Inline; break;
            case "layer": case "ilayer":             // NN4 layer extension
                s.Display = DisplayValue.Block; break;

            // ── Line break ─────────────────────────────────────────────────
            case "br":
                s.Display = DisplayValue.Inline; break;

            // ── Address ────────────────────────────────────────────────────
            case "address":
                s.Display      = DisplayValue.Block;
                s.MarginTop    = 4; s.MarginBottom = 4; break;

            // ── Non-visual elements (produce no box) ───────────────────────
            case "map": case "area":
                return null;

            // ── Unknown elements → inline (NN2 default) ───────────────────
            default:
                s.Display = DisplayValue.Inline; break;
        }

        return s;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Block formatting context layout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs block-formatting-context layout for <paramref name="box"/> and
    /// all of its in-flow children.  Mutates X, Y, Width, Height on every box
    /// in the sub-tree.
    ///
    /// Key fixes vs. the original:
    ///   1. Auto width resolved from containingWidth (not from box.Width==0 sentinel
    ///      that could erroneously skip explicit zero-widths).
    ///   2. child.Y = currentY + collapsedTopMargin  (original omitted MarginTop).
    ///   3. currentY advances by border+padding+content+padding+border+bottomMargin
    ///      (original double-counted MarginTop by adding it after the child).
    ///   4. Anonymous boxes are laid out recursively (original skipped them).
    ///   5. Percentage widths re-resolved against the now-known containingWidth.
    /// </summary>
    /// <summary>
    /// Recursively shifts every box in a sub-tree by (dx, dy).
    /// Used to centre block children after their layout width is known.
    /// </summary>
    private static void OffsetBoxTree(LayoutBox box, float dx, float dy)
    {
        box.X += dx;
        box.Y += dy;
        foreach (var child in box.Children)
            OffsetBoxTree(child, dx, dy);
    }

    private static void LayoutBlock(
        LayoutBox box, float containingWidth, float containingHeight)
    {
        // Tables are handled by the dedicated TableLayout algorithm
        if (box.BoxType == BoxType.Table)
        {
            TableLayout.Layout(box, containingWidth);
            return;
        }

        // ── Re-resolve percentage widths ───────────────────────────────────
        // HTML width="N%" attributes are converted here because containingWidth
        // was not available at box-generation time for nested elements.
        if (box.Element != null)
        {
            string? wAttr = box.Element.GetAttr("width");
            if (!string.IsNullOrEmpty(wAttr) && wAttr.TrimEnd().EndsWith('%'))
            {
                if (float.TryParse(wAttr.TrimEnd().TrimEnd('%'), out float pctVal))
                {
                    float resolved = containingWidth * pctVal / 100f
                                   - box.MarginLeft - box.MarginRight
                                   - box.BorderLeft - box.BorderRight
                                   - box.PaddingLeft - box.PaddingRight;
                    if (resolved > 0f) box.Width = resolved;
                }
            }
        }

        // ── Auto content width ─────────────────────────────────────────────
        // A box.Width of 0 means "auto" (no explicit width was set).
        // Tables are shrink-to-fit — TableLayout computes their own width.
        // All other blocks stretch to fill the containing width.
        bool isTable = box.BoxType == BoxType.Table;
        if (box.Width <= 0f && !isTable)
        {
            box.Width = Math.Max(0f,
                containingWidth
                - box.MarginLeft  - box.MarginRight
                - box.BorderLeft  - box.BorderRight
                - box.PaddingLeft - box.PaddingRight);
        }

        // ── Content-area origin (border-box model) ─────────────────────────
        // box.X/Y = border-box origin.
        // Content area starts after border + padding.
        float contentX = box.X + box.BorderLeft + box.PaddingLeft;
        float contentY = box.Y + box.BorderTop  + box.PaddingTop;
        float currentY = contentY;

        // Remember whether height was explicit before we might overwrite it
        bool hasExplicitHeight = box.Height > 0f;

        // ── Lay out block-level children ───────────────────────────────────
        //
        // FIX: margin collapsing now correctly collapses adjacent sibling margins
        // AND correctly places each child by adding the collapsed margin to child.Y
        // (original added MarginTop to currentY AFTER the child, effectively
        //  treating it as extra bottom spacing on the preceding sibling).
        //
        // FIX: <center> and text-align:center parents now horizontally centre
        // their block children after layout (so child width is known).
        //
        bool centerBlockChildren =
            box.Element?.TagName == "center" ||
            box.Element?.Style?.TextAlign == TextAlign.Center;

        float prevMarginBottom = 0f;

        // ── Lay out children in source order, grouping consecutive inline
        //    runs and pulling floats out of whichever run they land in ──────
        //
        // FIX (major): the previous approach collected all block children,
        // laid them out first, THEN collected all inline children and laid
        // them out in one shot afterward. That threw away source order
        // entirely — any page whose flow mixes block content (tables, divs)
        // with inline/floated content as siblings (which is the common case:
        // text, an <img align="left">, more text, a <table>, ...) ended up
        // with every float projected up to the container's top and every
        // block child's Y computed as if the floats had never taken up
        // space, producing exactly the "everything collapses to the top /
        // tables land in the wrong place" symptoms.
        //
        // The fix processes box.Children once, in order. Consecutive
        // non-float inline-level children are batched into a single
        // InlineLayout.Layout() call (line-breaking needs the whole run to
        // make good wrap decisions); a block-level child flushes the
        // pending inline run first, then lays itself out at the current Y;
        // a float is placed immediately at the *current* flow Y (not the
        // container's Y) and registered into the shared FloatContext so
        // later inline runs wrap around it — floats do not advance
        // currentY themselves, per CSS 2.1 §9.5.
        var floatCtx = new FloatContext();
        var pendingInline = new List<LayoutBox>();
        var containerStyle = box.Element?.Style ?? new ComputedStyle();

        void FlushInlineRun()
        {
            if (pendingInline.Count == 0) return;

            float inlineHeight = InlineLayout.Layout(
                pendingInline, box.Width, contentX, currentY,
                containerStyle, floatCtx);

            currentY += inlineHeight;
            pendingInline.Clear();
            prevMarginBottom = 0f; // inline content doesn't participate in margin collapsing
        }

        foreach (var child in box.Children)
        {
            if (child.IsAbsolutelyPositioned)
                continue; // handled in second pass below

            if (child.IsFloated)
            {
                // Floats are placed at the current flow position, not the
                // container top, so multiple floats separated by other
                // content land where they actually occur in the document.
                PlaceFloat(child, contentX, currentY, box.Width, containingHeight);
                floatCtx.AddFloat(child);
                continue;
            }

            if (!IsBlockLevel(child))
            {
                pendingInline.Add(child);
                continue;
            }

            // Block-level child: flush any pending inline run first so it
            // renders above this block, in source order.
            FlushInlineRun();

            // CSS 2.1 §8.3.1 – collapse adjacent sibling top/bottom margins
            float collapsedMargin = Math.Max(prevMarginBottom, child.MarginTop);

            // Border-box X = parent content left + child's own left margin
            child.X = contentX + child.MarginLeft;

            // Border-box Y = current flow position + collapsed top margin
            child.Y = currentY + collapsedMargin;

            // Recurse into block / anonymous children
            if (child.BoxType is BoxType.Block
                              or BoxType.ListItem
                              or BoxType.Anonymous)
                LayoutBlock(child, box.Width, containingHeight);
            else if (child.BoxType == BoxType.Table)
                TableLayout.Layout(child, box.Width);

            // ── Centre block child if parent is <center> or text-align:center ──
            // We do this AFTER recursing so child.Width is the final value.
            // Only centre children that are narrower than the parent — auto-fill
            // blocks already occupy the full width and centering is a no-op.
            if (centerBlockChildren && child.Width > 0f)
            {
                float childOuterW = child.BorderLeft + child.PaddingLeft
                                  + child.Width
                                  + child.PaddingRight + child.BorderRight;
                float availableW  = box.Width;
                if (childOuterW < availableW - 1f)
                {
                    float newX    = contentX + (availableW - childOuterW) / 2f;
                    float deltaX  = newX - child.X;
                    if (Math.Abs(deltaX) > 0.5f)
                        OffsetBoxTree(child, deltaX, 0f);
                }
            }

            prevMarginBottom = child.MarginBottom;

            // Advance currentY past the child's full painted extent
            currentY = child.Y
                     + child.BorderTop   + child.PaddingTop
                     + child.Height
                     + child.PaddingBottom + child.BorderBottom
                     + child.MarginBottom;
        }

        // Flush a trailing inline run that wasn't followed by a block sibling
        FlushInlineRun();

        // ── Set auto content height ────────────────────────────────────────
        if (!hasExplicitHeight)
        {
            box.Height = Math.Max(0f, currentY - contentY);
        }

        // ── Second pass: absolutely positioned children ────────────────────
        foreach (var child in box.Children.Where(c => c.IsAbsolutelyPositioned))
            LayoutAbsolute(child, box);

        // ── Second pass: relatively positioned children ────────────────────
        foreach (var child in box.Children.Where(c =>
            c.Element?.Style?.Position == PositionValue.Relative))
            LayoutRelative(child);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Float placement
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Places a floated element at the current flow position without consuming
    /// vertical space in normal flow.  Floating elements are self-sized.
    /// </summary>
    private static void PlaceFloat(
        LayoutBox box, float contentX, float currentY,
        float containingWidth, float containingHeight)
    {
        // Give the float a default width if none was specified
        if (box.Width <= 0f)
            box.Width = Math.Max(20f, containingWidth / 3f);

        // Layout the float's own content
        float floatContainerW = box.Width
                              + box.MarginLeft + box.MarginRight
                              + box.BorderLeft + box.BorderRight
                              + box.PaddingLeft + box.PaddingRight;
        LayoutBlock(box, floatContainerW, containingHeight);

        float boxW = box.BorderLeft + box.BorderRight
                   + box.PaddingLeft + box.PaddingRight
                   + box.Width;

        // Position: floated right aligns to the right edge of content area
        if (box.FloatSide == FloatValue.Right)
            box.X = contentX + containingWidth - box.MarginRight - boxW;
        else
            box.X = contentX + box.MarginLeft;

        box.Y = currentY + box.MarginTop;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Absolute / relative positioning
    // ─────────────────────────────────────────────────────────────────────────

    private static void LayoutAbsolute(LayoutBox box, LayoutBox containingBlock)
    {
        var style = box.Element?.Style;
        if (style == null) return;

        // Containing block content area (border-box model)
        float cbContentX = containingBlock.X
                         + containingBlock.BorderLeft + containingBlock.PaddingLeft;
        float cbContentY = containingBlock.Y
                         + containingBlock.BorderTop  + containingBlock.PaddingTop;

        if (style.Left.HasValue)
            box.X = cbContentX + style.Left.Value + box.MarginLeft;
        else if (style.Right.HasValue)
            box.X = cbContentX + containingBlock.Width
                  - style.Right.Value
                  - box.MarginRight - box.BorderLeft - box.BorderRight
                  - box.PaddingLeft - box.PaddingRight - box.Width;

        if (style.Top.HasValue)
            box.Y = cbContentY + style.Top.Value + box.MarginTop;
        else if (style.Bottom.HasValue)
            box.Y = cbContentY + containingBlock.Height
                  - style.Bottom.Value
                  - box.MarginBottom - box.BorderTop - box.BorderBottom
                  - box.PaddingTop - box.PaddingBottom - box.Height;
    }

    private static void LayoutRelative(LayoutBox box)
    {
        var style = box.Element?.Style;
        if (style == null) return;

        if (style.Left.HasValue)        box.X += style.Left.Value;
        else if (style.Right.HasValue)  box.X -= style.Right.Value;

        if (style.Top.HasValue)         box.Y += style.Top.Value;
        else if (style.Bottom.HasValue) box.Y -= style.Bottom.Value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Frameset layout
    // ─────────────────────────────────────────────────────────────────────────

    private static LayoutBox LayoutFrameset(
        DomElement frameset, float viewportWidth, float viewportHeight)
    {
        var box = new LayoutBox(frameset, BoxType.Block)
        {
            X = 0, Y = 0,
            Width  = viewportWidth,
            Height = viewportHeight
        };

        var rows = ParseFramesetSizes(
            frameset.GetAttrOrDefault("rows", ""), viewportHeight);
        var cols = ParseFramesetSizes(
            frameset.GetAttrOrDefault("cols", ""), viewportWidth);

        if (rows.Count == 0) rows.Add(viewportHeight);
        if (cols.Count == 0) cols.Add(viewportWidth);

        // Walk <frame> / nested <frameset> children in document order
        int frameIndex = 0;
        float y = 0f;

        for (int r = 0; r < rows.Count; r++)
        {
            float x = 0f;
            for (int c = 0; c < cols.Count; c++)
            {
                // Skip non-element nodes (whitespace text) between frames
                DomElement? frameElem = null;
                while (frameIndex < frameset.Children.Count)
                {
                    if (frameset.Children[frameIndex] is DomElement fe &&
                        (fe.TagName == "frame" || fe.TagName == "frameset"))
                    {
                        frameElem = fe;
                        frameIndex++;
                        break;
                    }
                    frameIndex++;
                }

                var frameBox = new LayoutBox(frameElem, BoxType.Frame)
                {
                    X      = x,
                    Y      = y,
                    Width  = cols[c],
                    Height = rows[r],
                    Parent = box
                };
                box.Children.Add(frameBox);

                x += cols[c];
            }
            y += rows[r];
        }

        return box;
    }

    /// <summary>
    /// Parses a frameset rows/cols attribute into an ordered list of pixel sizes.
    ///
    /// Supported formats:
    ///   "200"   – fixed pixels
    ///   "50%"   – percentage of total dimension
    ///   "*"     – wildcard: receives equal share of remaining space
    ///
    /// FIX: The original parser inserted pixel and percentage sizes at list index
    /// positions that drifted from attribute positions, causing frame slots to be
    /// assigned the wrong size.  This implementation processes all parts in a
    /// single pass and defers wildcard distribution to a second pass.
    /// </summary>
    private static List<float> ParseFramesetSizes(string attr, float total)
    {
        if (string.IsNullOrWhiteSpace(attr))
            return new List<float>();

        string[] parts  = attr.Split(',');
        var sizes       = new List<float>(parts.Length);
        var wildcardIdx = new List<int>();
        float used      = 0f;

        for (int i = 0; i < parts.Length; i++)
        {
            string t = parts[i].Trim();

            if (t == "*")
            {
                sizes.Add(0f);
                wildcardIdx.Add(i);
            }
            else if (t.EndsWith('%') &&
                     float.TryParse(t.TrimEnd('%'), out float pct))
            {
                float px = total * pct / 100f;
                sizes.Add(px);
                used += px;
            }
            else if (float.TryParse(t, out float px))
            {
                sizes.Add(px);
                used += px;
            }
            else   // malformed token → wildcard
            {
                sizes.Add(0f);
                wildcardIdx.Add(i);
            }
        }

        // Distribute remaining space equally among wildcards
        if (wildcardIdx.Count > 0)
        {
            float each = Math.Max(0f, (total - used) / wildcardIdx.Count);
            foreach (int i in wildcardIdx)
                sizes[i] = each;
        }

        return sizes;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Utility
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsNonVisualTag(string tag) => tag switch
    {
        "script" or "noscript" or "style" or "link"  or "meta"
             or  "head"        or "title" or "base"  or "param" => true,
        _ => false
    };

    private static LayoutBox MakeRootBox(DomElement? elem, float w, float h)
        => new LayoutBox(elem, BoxType.Block) { X = 0, Y = 0, Width = w, Height = h };

    /// <summary>
    /// Collapses runs of ASCII whitespace to a single space and trims the result,
    /// matching HTML 3.2 / Netscape Navigator 2 text-node handling outside PRE.
    /// </summary>
    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb     = new StringBuilder(s.Length);
        bool inWs  = false;

        foreach (char c in s)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (!inWs) { sb.Append(' '); inWs = true; }
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }

        // Do NOT call .Trim() here: it destroys the single space between
        // adjacent inline elements, e.g. <b>bold</b> word becomes "boldword".
        // Fully-whitespace nodes collapse to " " and are kept intentionally.
        string result = sb.ToString();
        return result == " " ? "" : result;  // drop lone-space nodes that add no content
    }
}