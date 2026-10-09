using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Orchestrates the complete layout pipeline for the Retro96 1996-era browser engine.
///
/// Coordinate model (consistent throughout the engine):
///   box.X / box.Y  = border-box origin (document-relative; margins external)
///   box.Width      = content width   (excludes margins/borders/padding)
///   box.Height     = content height  (excludes margins/borders/padding)
///
/// Visual box model for a child placed inside a parent:
///   child border-box left  = parentContentX + child.MarginLeft
///   child border-box top   = currentY       + collapsedTopMargin
///   child content left     = child.X + child.BorderLeft + child.PaddingLeft
///   child content top      = child.Y + child.BorderTop  + child.PaddingTop
///
/// Quirks implemented (Netscape Navigator 2 / 3, 1996):
///   NN23       – <spacer type="horizontal">   inline spacer
///   NN24       – <spacer type="vertical">      block  spacer
///   NN25       – <multicol cols=N gutter=G>   balanced multi-column block
///   NN-BLINK   – <blink>                      inline; flattened so its content
///                                             is measured — the runs keep the
///                                             blink element so the shell timer
///                                             can toggle them through it
///   NN-CENTER  – <center>                     block, centres inline runs AND block children
///   NN-FONT    – <font size color face>       inline; attributes resolved by StyleResolver
///   NN-NOBR    – <nobr>                       inline; no-break text
///   NN-PRE     – <pre>/<listing>/<xmp>         literal whitespace, tab stops at 8 cols, no wrap
///   IE-MARQUEE – <marquee>                     block; scrolled externally by the shell timer
///   HTML32-HR  – <hr size width align>         block rule, default ALIGN=center per HTML 3.2
/// </summary>
public static class LayoutEngine
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1996-era defaults  (Netscape Navigator 2 / 3)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Default body side margins (pixels).  NN2/3 used 10 px.</summary>
    private const float DefaultBodyMarginH = 10f;
    private const float DefaultBodyMarginV = 10f;

    /// <summary>Tab stop width (columns) used when expanding tabs inside PRE.</summary>
    private const int PreTabStopColumns = 8;

    /// <summary>
    /// Upper bound for any single pixel length parsed from an attribute.
    /// FIX: a hostile width="99999999" used to flow straight into the box
    /// tree, the root extents and finally the render bitmap — a guaranteed
    /// multi-gigabyte allocation (OOM / DoS).  32768 px is far beyond any
    /// era-legitimate size.
    /// </summary>
    private const float MaxAttrLength = 32768f;

    /// <summary>Shared fallback container style — was a fresh allocation per block.</summary>
    private static readonly ComputedStyle DefaultStyle = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the complete layout tree for a parsed DOM document.
    /// Returns a root LayoutBox whose descendants cover the entire document.
    /// The root box itself is an anonymous block spanning (at least) the viewport.
    /// Its Width/Height grow past the viewport when content overflows, which is
    /// how the shell decides to show a horizontal scrollbar — the period-correct
    /// behaviour for wide tables, unwrapped PRE lines and oversized images.
    /// </summary>
    public static LayoutBox BuildLayoutTree(
        DomDocument document,
        float viewportWidth,
        float viewportHeight)
    {
        if (document == null)
            return MakeRootBox(null, viewportWidth, viewportHeight);

        // ── DomElement ↔ LayoutBox wiring (Task 9) ────────────────────────
        // Layout trees are rebuilt from scratch on every pass, so every
        // element reference is invalidated up front and re-published below
        // (GenerateBoxes / BuildBodyBox / spacer / frameset construction).
        // Elements that stop generating a box therefore never keep a stale
        // geometry reference for JS offset* reads.
        foreach (var e in document.ElementDescendants())
        {
            e.Box = null;
            e.LayoutBox = null;
        }

        // ── Frameset document? ────────────────────────────────────────────
        var frameset = FindTopLevelFrameset(document);
        if (frameset != null)
            return LayoutFrameset(frameset, 0f, 0f, viewportWidth, viewportHeight);

        // ── Locate <body> (the parser infers one for tag-soup documents) ──
        var body = document.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        if (body == null)
            return MakeRootBox(null, viewportWidth, viewportHeight);

        // ── Viewport root box (anonymous, never painted) ──────────────────
        var rootBox = MakeRootBox(null, viewportWidth, viewportHeight);

        // ── Body box ──────────────────────────────────────────────────────
        var bodyBox = BuildBodyBox(body, rootBox, viewportWidth);
        rootBox.Children.Add(bodyBox);

        if (Retro96.DebugLog.Enabled)
            Retro96.DebugLog.Write(
                $"BuildLayoutTree: body DOM children={body.Children.Count} " +
                $"({string.Join(",", body.Children.Select(n => n is DomElement de ? de.TagName : n is DomText dt ? $"text[{(dt.Data ?? "").Length}]" : n.GetType().Name))})");

        // ── Generate all descendant boxes ─────────────────────────────────
        var rawChildren = GenerateBoxes(body, bodyBox, bodyBox.Width);
        NormaliseAndAttach(rawChildren, bodyBox);

        // ── Block layout pass ─────────────────────────────────────────────
        LayoutBlock(bodyBox, viewportWidth, viewportHeight);

        // ── Expand the root to cover the full document extents ────────────
        // (floats, wide PRE lines and oversized tables may hang past the
        // viewport — the shell turns that into a horizontal scrollbar, it
        // does not squeeze the content.)
        ExpandRoot(rootBox, viewportWidth, viewportHeight);

        if (Retro96.DebugLog.Enabled)
            Retro96.DebugLog.Write(
                $"BuildLayoutTree: bodyBox children={bodyBox.Children.Count} " +
                $"bodyBox W={bodyBox.Width} H={bodyBox.Height} X={bodyBox.X} Y={bodyBox.Y}");

        return rootBox;
    }

    /// <summary>
    /// The top-level frameset is the first &lt;frameset&gt; that is not nested
    /// inside a &lt;body&gt; (pages occasionally carry junk framesets inside
    /// body content; those are just ignored inline content, not the layout).
    /// </summary>
    private static DomElement? FindTopLevelFrameset(DomDocument document)
    {
        foreach (var e in document.ElementDescendants())
        {
            if (e.TagName != "frameset") continue;

            bool insideBody = false;
            for (var p = e.Parent; p != null; p = p.Parent)
            {
                if (p is DomElement pe && pe.TagName == "body")
                {
                    insideBody = true;
                    break;
                }
            }
            if (!insideBody) return e;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body box construction
    // ─────────────────────────────────────────────────────────────────────────

    private static LayoutBox BuildBodyBox(
        DomElement body, LayoutBox rootBox, float viewportWidth)
    {
        // Margins: IE-style leftmargin/topmargin (+ right/bottom variants),
        // NN-style marginwidth/marginheight.
        int lm = Math.Max(body.GetAttrInt("leftmargin", -1),
                          body.GetAttrInt("marginwidth", -1));
        int rm = Math.Max(body.GetAttrInt("rightmargin", -1),
                          body.GetAttrInt("marginwidth", -1));
        int tm = Math.Max(body.GetAttrInt("topmargin", -1),
                          body.GetAttrInt("marginheight", -1));
        int bm = Math.Max(body.GetAttrInt("bottommargin", -1),
                          body.GetAttrInt("marginheight", -1));

        float attrL = lm >= 0 ? lm : DefaultBodyMarginH;
        float attrR = rm >= 0 ? rm : DefaultBodyMarginH;
        float attrT = tm >= 0 ? tm : DefaultBodyMarginV;
        float attrB = bm >= 0 ? bm : DefaultBodyMarginV;

        // Author CSS margins override the HTML attributes (NN4 behaviour).
        // Resolve percentage values against the containing block (the viewport for BODY).
        var style = body.Style;
        float marginL = ResolveStyleLength(style?.MarginLeft, style?.MarginLeftPercent, attrL, viewportWidth, allowNegative: true);
        float marginR = ResolveStyleLength(style?.MarginRight, style?.MarginRightPercent, attrR, viewportWidth, allowNegative: true);
        float marginT = ResolveStyleLength(style?.MarginTop, style?.MarginTopPercent, attrT, viewportWidth, allowNegative: true);
        float marginB = ResolveStyleLength(style?.MarginBottom, style?.MarginBottomPercent, attrB, viewportWidth, allowNegative: true);

        float padL = ResolveStyleLength(style?.PaddingLeft, style?.PaddingLeftPercent, 0f, viewportWidth);
        float padR = ResolveStyleLength(style?.PaddingRight, style?.PaddingRightPercent, 0f, viewportWidth);
        float padT = ResolveStyleLength(style?.PaddingTop, style?.PaddingTopPercent, 0f, viewportWidth);
        float padB = ResolveStyleLength(style?.PaddingBottom, style?.PaddingBottomPercent, 0f, viewportWidth);

        float borL = Math.Max(0f, style?.BorderLeftWidth ?? 0f);
        float borR = Math.Max(0f, style?.BorderRightWidth ?? 0f);
        float borT = Math.Max(0f, style?.BorderTopWidth ?? 0f);
        float borB = Math.Max(0f, style?.BorderBottomWidth ?? 0f);

        float contentW = Math.Max(0f,
            viewportWidth - marginL - marginR - borL - borR - padL - padR);

        var bodyBox = new LayoutBox(body, BoxType.Block)
        {
            X = marginL,           // border-box origin X (margins external)
            Y = marginT,           // border-box origin Y
            Width = contentW,
            Height = 0f,                 // set by LayoutBlock
            MarginTop = marginT,
            MarginRight = marginR,
            MarginBottom = marginB,
            MarginLeft = marginL,
            PaddingTop = padT,
            PaddingRight = padR,
            PaddingBottom = padB,
            PaddingLeft = padL,
            BorderTop = borT,
            BorderRight = borR,
            BorderBottom = borB,
            BorderLeft = borL,
            Parent = rootBox
        };

        // Body's principal box publication (see the wiring note in
        // BuildLayoutTree) — the body box is built here, not in GenerateBoxes.
        body.LayoutBox = bodyBox;
        body.Box = bodyBox;

        return bodyBox;
    }

    /// <summary>Publishes an element's principal box on both the canonical
    /// LayoutBox property and the legacy Box slot (DomElement ↔ LayoutBox
    /// wiring, Task 9).</summary>
    private static void PublishBox(DomElement element, LayoutBox box)
    {
        element.LayoutBox = box;
        element.Box = box;
    }

    private static float ResolveStyleLength(float? absolute, float? percent, float fallback,
        float containingWidth, bool allowNegative = false)
    {
        if (percent.HasValue)
        {
            float value = containingWidth * percent.Value / 100f;
            return allowNegative ? value : Math.Max(0f, value);
        }
        if (absolute.HasValue)
            return allowNegative ? absolute.Value : Math.Max(0f, absolute.Value);
        return Math.Max(0f, fallback);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Box-tree construction (recursive)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a flat list of raw LayoutBox objects for every direct child
    /// of <paramref name="element"/>.  Does NOT attach them; the caller does
    /// that via NormaliseAndAttach.
    /// <paramref name="inheritedPre"/> carries PRE-ness down through flattened
    /// inline wrappers so &lt;pre&gt;&lt;b&gt;literal text&lt;/b&gt;&lt;/pre&gt;
    /// keeps its literal whitespace.
    /// </summary>
    private static List<LayoutBox> GenerateBoxes(
        DomElement element, LayoutBox parentBox, float containingWidth,
        bool inheritedPre = false)
    {
        var result = new List<LayoutBox>();

        bool parentIsPre = inheritedPre
            || element.Style?.WhiteSpace == WhiteSpaceValue.Pre
            || element.TagName is "pre" or "listing" or "xmp" or "plaintext";

        // CSS2 generated content (Task 9): the :before pseudo-element's
        // content flows as a synthetic text run at the very start of the
        // element's inline content.  The run keeps the element as its
        // Element (so ancestry/hit-testing see it as element content) and
        // carries the pseudo-element's computed style as a StyleOverride,
        // exactly like the first-letter fragments do.
        AddGeneratedContent(element, parentBox, result, before: true);

        foreach (var node in element.Children)
        {
            switch (node)
            {
                // ── Element nodes ──────────────────────────────────────────
                case DomElement elem:
                    {
                        // Non-visual structural elements produce no boxes at all
                        if (IsNonVisualTag(elem.TagName))
                            break;

                        // Style: author-resolved → UA fallback (cached back onto
                        // the element so the renderer can read it later)
                        var style = elem.Style ?? FallbackStyleFor(elem);
                        if (style == null)
                            break;                      // null = "no box" (map, area)

                        if (elem.Style == null) elem.Style = style;

                        if (style.Display == DisplayValue.None)
                            break;                      // display:none removes from layout

                        // ── <spacer> (NN23 / NN24) ─────────────────────────────
                        if (elem.TagName == "spacer")
                        {
                            var spacer = CreateSpacerBox(elem, parentBox);
                            if (spacer != null) result.Add(spacer);
                            break;
                        }

                        var boxType = DetermineBoxType(elem, style);
                        if (style.Display == DisplayValue.TableCell &&
                            elem.TagName is not ("td" or "th") &&
                            !IsTableFormattingParent(parentBox))
                        {
                            boxType = BoxType.InlineBlock;
                        }
                        if (elem.TagName == "object" &&
                            elem.GetAttr("classid")?.StartsWith("clsid:", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // ActiveX class IDs are unsupported. Treat the object
                            // as a fallback container so nested object/image content
                            // participates in layout instead of reserving a plugin box.
                            boxType = BoxType.InlineBlock;
                        }

                        // ── Flatten inline containers ───────────────────────────
                        // InlineLayout only measures top-level items in the inline
                        // child list; an inline wrapper (<a>, <b>, <font>, …) has
                        // no TextRun and no replaced content, so it would measure
                        // as 0.  Its children are emitted directly into the flow
                        // instead; text runs keep the inline element as their
                        // Element so font/colour lookups still work through it.
                        //
                        // Exceptions that must remain real boxes:
                        //   <br>  – InlineLayout checks TagName to break the line
                        //   <wbr> – zero-width soft break hint (no content either)
                        // <blink> is flattened like the rest — its content has to
                        // reach the flow to be measured at all; the runs keep the
                        // blink element so the shell timer can still toggle them.
                        if (boxType == BoxType.Inline
                            && elem.TagName != "br"
                            && elem.TagName != "wbr")
                        {
                            result.AddRange(GenerateBoxes(elem, parentBox,
                                containingWidth, parentIsPre));
                            break;
                        }

                        var box = new LayoutBox(elem, boxType) { Parent = parentBox };

                        // DomElement ↔ LayoutBox wiring: publish the element's
                        // PRINCIPAL box (the canonical LayoutBox property and
                        // the legacy Box slot DomBindings reads).  Text-run
                        // fragments created later keep the element as their
                        // Element but never overwrite this reference — the
                        // assignment happens only here, at construction.
                        elem.LayoutBox = box;
                        elem.Box = box;

                        ApplyStylesToBox(box, style);
                        ApplyHtmlPresentationalAttrs(box, elem, containingWidth);

                        // Definition descriptions have a visible hanging
                        // indent in the HTML 3.2 UA presentation. Keep that
                        // invariant on the actual layout box as well as in the
                        // computed style so no fallback path can flatten <dd>
                        // back onto the <dt> column.
                        if (elem.TagName == "dd")
                            box.MarginLeft = Math.Max(box.MarginLeft, 40f);

                        // CSS1 width/height (CSS beats the HTML attribute — the
                        // CSS1 cascade puts presentational attributes below author
                        // styles, exactly like NN4).  Percentage widths wait for
                        // layout (StyleWidthPercent); pixel sizes apply now.
                        //
                        // IE5 quirks box model (checklist §9, the layout idiom
                        // 1999 pages were built around): an authored CSS pixel
                        // width/height is the BORDER box — content = authored −
                        // padding − border.  Percentage widths and HTML width
                        // ATTRIBUTES stay on their existing border-box-ish
                        // resolution paths and are never re-subtracted here.
                        // IMG quirk: an img lays out as margin+border+width+
                        // border+margin — padding never participates at all,
                        // so it is dropped from the box itself.
                        if (BrowserRuntime.UsesIe5BoxModelFor(elem.OwnerDocument()) &&
                            elem.TagName == "img" &&
                            (style.Width is > 0f || style.Height is > 0f))
                        {
                            box.PaddingLeft = 0f;
                            box.PaddingRight = 0f;
                            box.PaddingTop = 0f;
                            box.PaddingBottom = 0f;
                        }
                        if (style.Width is > 0f)
                            box.Width = AuthoredContentExtent(elem, style.Width.Value,
                                box.PaddingLeft + box.PaddingRight,
                                box.BorderLeft + box.BorderRight);
                        if (style.Height is > 0f)
                            box.Height = AuthoredContentExtent(elem, style.Height.Value,
                                box.PaddingTop + box.PaddingBottom,
                                box.BorderTop + box.BorderBottom);

                        // FIX: natural-size / aspect derivation moved AFTER the
                        // CSS override (used to run inside the attribute pass, so
                        // `img { width:100px }` over a natural 200×100 image kept
                        // the attr-era 100px height — a squashed image: the CSS
                        // width participated in nothing).
                        if (boxType == BoxType.Replaced && elem.TagName == "img")
                            ResolveImageNaturalSize(box, elem);

                        // CSS2 min/max clamping — applied to whatever extent the
                        // box now carries (authored CSS, HTML attribute or the
                        // natural image size).  AUTO-width blocks skip it here:
                        // their clamp runs inside ResolveAutoWidth AFTER the
                        // container stretch, and auto heights clamp in LayoutBlock
                        // after the content is laid out (a generation-time raise
                        // would be mistaken for an explicit height).  Block
                        // layout re-clamps authored extents — idempotent.
                        if (box.Width > 0f) ClampWidthToBounds(box, containingWidth);
                        if (box.Height > 0f) ClampHeightToBounds(box, 0f);

                        // Replaced elements / frames do not generate child boxes
                        // here (their visuals are painted from the element itself).
                        if (boxType != BoxType.Replaced && boxType != BoxType.Frame)
                        {
                            // Children resolve their percentage sizes against this
                            // box's content width when it is already known (from a
                            // WIDTH attribute); otherwise fall back to the width
                            // this element itself was generated against.
                            float childContaining = box.Width > 0f ? box.Width : containingWidth;
                            var childRaw = GenerateBoxes(elem, box, childContaining);
                            NormaliseAndAttach(childRaw, box);
                        }

                        result.Add(box);
                        break;
                    }

                // ── Text nodes ─────────────────────────────────────────────
                case DomText textNode:
                    {
                        if (parentIsPre)
                        {
                            // FIX: the leading-newline strip used to fire for EVERY
                            // text node inside the PRE.  The era rule strips one
                            // newline only immediately after the <pre> start tag —
                            // a newline opening a LATER text node (after a <b>,
                            // say) is content and used to be swallowed.
                            bool strip = ShouldStripLeadingNewline(textNode, element);
                            AddPreFormattedText(textNode.Data ?? "", element, parentBox,
                                                result, strip);
                        }
                        else
                        {
                            // Collapse runs of whitespace to single spaces
                            string data = CollapseWhitespace(textNode.Data ?? "");
                            if (data.Length == 0)
                                break;

                            // Text boxes reference the PARENT element so inline
                            // formatting (<b>, <font>, <a> colours…) is visible
                            // through box.Element.Style.
                            result.Add(new LayoutBox(element, BoxType.Inline)
                            {
                                TextRun = Render.GlyphSubstitution.MapGlyphs(data),
                                Parent = parentBox
                            });
                        }
                        break;
                    }
            }
        }

        // CSS2 generated content — the :after pseudo-element's content run
        // closes the element's inline content.
        AddGeneratedContent(element, parentBox, result, before: false);

        return result;
    }

    /// <summary>
    /// Emits the <c>:before</c>/<c>:after</c> generated-content text as an
    /// inline text-run box.  Only the renderable token types contribute:
    /// "string" tokens emit their literal text and <c>attr(x)</c> emits the
    /// element's attribute value. The style resolver provides resolved text
    /// for counters and quote keywords; marker pseudo-elements are painted
    /// in the list-marker position rather than inline.
    /// </summary>
    private static void AddGeneratedContent(
        DomElement element, LayoutBox parentBox, List<LayoutBox> result, bool before)
    {
        var pseudo = before ? element.Style?.GeneratedBefore : element.Style?.GeneratedAfter;
        if (pseudo == null)
            return;

        var tokens = pseudo.Content;
        if (tokens == null || tokens.Count == 0)
            return;
        if (pseudo.Display == DisplayValue.Marker)
            return;

        string text = pseudo.ResolvedGeneratedContentText ?? GeneratedContentText(tokens, element);
        if (text.Length == 0)
            return;

        result.Add(new LayoutBox(element, BoxType.Inline)
        {
            TextRun = Render.GlyphSubstitution.MapGlyphs(text),
            Parent = parentBox,
            StyleOverride = pseudo
        });
    }

    /// <summary>Flattens generated-content tokens to their text value.</summary>
    internal static string GeneratedContentText(List<ContentToken> tokens, DomElement element)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case "string":
                    sb.Append(token.Text);
                    break;
                case "attr":
                    sb.Append(element.GetAttr(token.Text) ?? string.Empty);
                    break;
                // "counter"/"counters" — no counter registry exists yet;
                // "open-quote"/"close-quote"/"no-open-quote"/"no-close-quote"
                // — no quote nesting depth is tracked;
                // "uri" — generated images unsupported (era-simple).
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The era rule: exactly ONE newline directly after the PRE start tag is
    /// ignored.  That means the strip applies only when this text node is the
    /// FIRST child of the pre-like element itself — not to text inside nested
    /// inline wrappers, and not to later text nodes.
    /// </summary>
    private static bool ShouldStripLeadingNewline(DomText node, DomElement owner)
    {
        if (!ReferenceEquals(node.Parent, owner)) return false;
        bool ownerIsPre = owner.TagName is "pre" or "listing" or "xmp" or "plaintext"
            || owner.Style?.WhiteSpace == WhiteSpaceValue.Pre;
        if (!ownerIsPre) return false;
        return owner.Children.Count > 0 && ReferenceEquals(owner.Children[0], node);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PRE handling — literal whitespace, tab stops, no wrapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits one text box per source line (so lines never re-wrap), separated
    /// by synthetic &lt;br&gt; boxes.  Tabs expand to the next 8-column stop,
    /// matching Netscape's monospace tab handling.  <paramref name="owner"/>
    /// is the element the text runs will reference for styling.
    /// </summary>
    private static void AddPreFormattedText(
        string data, DomElement owner, LayoutBox parentBox, List<LayoutBox> result,
        bool stripLeadingNewline)
    {
        // Drop ONE leading newline (<pre>\n…  — the parser of the era did this)
        if (stripLeadingNewline)
        {
            if (data.StartsWith("\r\n")) data = data[2..];
            else if (data.StartsWith('\n')) data = data[1..];
        }

        // Split into physical lines; a trailing newline produces a final empty
        // segment which is dropped (it does not open another line).
        var lines = data.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int last = lines.Length - 1;
        if (last > 0 && lines[last].Length == 0)
            last--;                              // trailing newline — no extra line

        for (int i = 0; i <= last; i++)
        {
            string line = ExpandTabs(lines[i]);

            if (line.Length > 0)
            {
                result.Add(new LayoutBox(owner, BoxType.Inline)
                {
                    TextRun = Render.GlyphSubstitution.MapGlyphs(line),
                    Parent = parentBox
                });
            }

            // Line separator — but never after the final line
            if (i < last)
            {
                var brElem = new DomElement("br");   // synthetic, not in the DOM
                brElem.Style = FallbackStyleFor(brElem);   // renderer reads Element.Style
                result.Add(new LayoutBox(brElem, BoxType.Inline)
                {
                    Parent = parentBox
                });
            }
        }
    }

    /// <summary>Expands tab characters to the next 8-column tab stop.</summary>
    private static string ExpandTabs(string line)
    {
        if (line.IndexOf('\t') < 0)
            return line;

        var sb = new StringBuilder(line.Length + 8);
        int col = 0;
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int spaces = PreTabStopColumns - (col % PreTabStopColumns);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else
            {
                sb.Append(c);
                col++;
            }
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Anonymous block-box normalisation  (CSS 2.1 §9.2.1.1)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When a block container holds both block-level and inline-level children,
    /// each contiguous run of inline children is wrapped in an anonymous block
    /// so that document order is preserved through the block layout pass.
    /// Runs that consist entirely of whitespace-only text are dropped.
    /// Whitespace runs BETWEEN inline siblings are word separators, however,
    /// and must survive.
    /// </summary>
    private static void NormaliseAndAttach(List<LayoutBox> rawBoxes, LayoutBox parent)
    {
        if (rawBoxes.Count == 0)
            return;

        bool hasBlock = rawBoxes.Any(b => IsBlockLevel(b));
        bool hasInline = rawBoxes.Any(b => !IsBlockLevel(b));

        if (!hasBlock || !hasInline)
        {
            // Homogeneous list — attach directly.  A list whose ENTIRE content
            // is whitespace (only possible for an all-inline list) renders
            // nothing; individual whitespace runs inside a list that also has
            // real content are kept (they separate words).
            if (IsAllWhitespaceRun(rawBoxes))
                return;

            foreach (var b in rawBoxes)
            {
                parent.Children.Add(b);
                b.Parent = parent;
            }
            return;
        }

        // Mixed list — one anonymous block per contiguous inline run
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
        // Whitespace-only runs between block siblings never become boxes
        if (IsAllWhitespaceRun(inlines))
            return;

        var anon = new LayoutBox(null, BoxType.Anonymous) { Parent = parent };
        foreach (var b in inlines)
        {
            anon.Children.Add(b);
            b.Parent = anon;
        }
        parent.Children.Add(anon);
    }

    /// <summary>True when every box in the run is a whitespace-only text run.
    /// NBSP (U+00A0) is NOT collapsible HTML whitespace — a paragraph whose
    /// only content is &amp;nbsp; must still open a line.  .NET's
    /// char.IsWhiteSpace classes U+00A0 as whitespace, so
    /// string.IsNullOrWhiteSpace here made those paragraphs collapse.</summary>
    private static bool IsAllWhitespaceRun(List<LayoutBox> run) =>
        run.Count > 0 && run.All(b =>
            b.TextRun != null && IsAsciiWhitespaceOnly(b.TextRun));

    /// <summary>HTML-collapsible whitespace is ASCII-only.  U+00A0 and any
    /// other Unicode space-like codepoint is CONTENT by HTML rules.
    /// internal: TableLayout needs the same rule for cell content.</summary>
    internal static bool IsAsciiWhitespaceOnly(string s)
    {
        foreach (char c in s)
            if (!IsAsciiWhitespace(c))
                return false;
        return true;
    }

    internal static bool IsAsciiWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

    /// <summary>
    /// Block-level determination.  <hr> is block-level in flow (HTML 3.2 rules
    /// render it as a horizontal rule occupying its own line, centred by
    /// default).  Table cells/captions/rows are included so stray cells dropped
    /// directly into a table by parser recovery stay recognisable to
    /// TableLayout instead of disappearing inside anonymous wrappers.
    /// </summary>
    private static bool IsBlockLevel(LayoutBox b) =>
        b.Element?.TagName == "hr"
        || b.BoxType is
            BoxType.Block or BoxType.Table or BoxType.ListItem
            or BoxType.Anonymous or BoxType.TableRow or BoxType.TableCell
            or BoxType.TableCaption;

    // ─────────────────────────────────────────────────────────────────────────
    // Spacer  (NN23 / NN24)
    // ─────────────────────────────────────────────────────────────────────────

    private static LayoutBox? CreateSpacerBox(DomElement elem, LayoutBox parentBox)
    {
        // SPACER TYPE values were case-insensitive in Netscape's extension.
        // Without trimming/normalising, a page using TYPE="BLOCK" or
        // surrounding whitespace falls through to the null case and silently
        // contributes no layout space.
        string type = elem.GetAttrOrDefault("type", "horizontal").Trim().ToLowerInvariant();
        int size = Math.Max(0, elem.GetAttrInt("size", 0));
        int width = Math.Max(0, elem.GetAttrInt("width", size));
        int height = Math.Max(0, elem.GetAttrInt("height", size));

        LayoutBox? box = type switch
        {
            "horizontal" => new LayoutBox(elem, BoxType.Inline)
            { Width = Math.Max(1, width), Height = 1, Parent = parentBox },
            "vertical" => new LayoutBox(elem, BoxType.Block)
            { Width = 1, Height = Math.Max(1, height), Parent = parentBox },
            "block" => new LayoutBox(elem, BoxType.Block)
            { Width = Math.Max(1, width), Height = Math.Max(1, height), Parent = parentBox },
            _ => null
        };
        if (box != null) PublishBox(elem, box);
        return box;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Style helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsTableFormattingParent(LayoutBox box)
    {
        if (box.BoxType is BoxType.Table or BoxType.TableRow)
            return true;

        if (box.Element?.TagName is "table" or "tr" or "thead" or "tbody" or "tfoot")
            return true;

        return box.Element?.Style?.Display is DisplayValue.Table or
            DisplayValue.TableRow or DisplayValue.TableRowGroup or
            DisplayValue.TableHeaderGroup or DisplayValue.TableFooterGroup;
    }

    private static BoxType DetermineBoxType(DomElement elem, ComputedStyle style)
    {
        // Replaced content — single visual unit sized by attributes/intrinsics
        switch (elem.TagName)
        {
            case "img":
            case "input":
            case "button":
            case "select":
            case "textarea":
            case "object":
            case "embed":
            case "applet":
            case "hr":
            case "isindex":
            case "canvas":
                return BoxType.Replaced;

            // <iframe> is an inline frame — flows like an image but hosts a
            // whole child document, painted by the shell into this rect.
            case "iframe":
                return BoxType.Frame;
        }

        // CSS display mapping
        return style.Display switch
        {
            DisplayValue.Table => BoxType.Table,
            DisplayValue.InlineTable => BoxType.InlineBlock,
            DisplayValue.TableRow => BoxType.TableRow,
            DisplayValue.TableRowGroup or DisplayValue.TableHeaderGroup or
                DisplayValue.TableFooterGroup => BoxType.Block,
            DisplayValue.TableCell => BoxType.TableCell,
            DisplayValue.TableCaption => BoxType.TableCaption,
            DisplayValue.ListItem => BoxType.ListItem,
            DisplayValue.Block => BoxType.Block,
            DisplayValue.InlineBlock => BoxType.InlineBlock,
            DisplayValue.Compact => BoxType.Block,
            _ => BoxType.Inline
        };
    }

    private static void ApplyStylesToBox(LayoutBox box, ComputedStyle style)
    {
        box.MarginTop = style.MarginTop;
        box.MarginRight = style.MarginRight;
        box.MarginBottom = style.MarginBottom;
        box.MarginLeft = style.MarginLeft;
        box.MarginTopPercent = style.MarginTopPercent;
        box.MarginRightPercent = style.MarginRightPercent;
        box.MarginBottomPercent = style.MarginBottomPercent;
        box.MarginLeftPercent = style.MarginLeftPercent;

        // CSS1 auto margins (margin-left/right: auto) — the values flatten
        // to 0 above; the flags carry the centring intent into layout.
        box.MarginLeftAuto = style.MarginLeftAuto;
        box.MarginRightAuto = style.MarginRightAuto;

        // FIX: clamp padding/border to ≥ 0 — a negative CSS padding/border
        // used to flow straight into every rectangle computation and the
        // painter (negative rects, inverted geometry).
        box.PaddingTop = Math.Max(0f, style.PaddingTop);
        box.PaddingRight = Math.Max(0f, style.PaddingRight);
        box.PaddingBottom = Math.Max(0f, style.PaddingBottom);
        box.PaddingLeft = Math.Max(0f, style.PaddingLeft);
        box.PaddingTopPercent = style.PaddingTopPercent;
        box.PaddingRightPercent = style.PaddingRightPercent;
        box.PaddingBottomPercent = style.PaddingBottomPercent;
        box.PaddingLeftPercent = style.PaddingLeftPercent;
        box.ListMarkerInside = style.ListStylePosition == ListStylePosition.Inside &&
                               style.ListStyleType != ListStyleType.None;

        box.BorderTop = Math.Max(0f, style.BorderTopWidth);
        box.BorderRight = Math.Max(0f, style.BorderRightWidth);
        box.BorderBottom = Math.Max(0f, style.BorderBottomWidth);
        box.BorderLeft = Math.Max(0f, style.BorderLeftWidth);

        box.IsFloated = style.Float != FloatValue.None;
        box.FloatSide = style.Float;
        box.IsAbsolutelyPositioned = style.Position == PositionValue.Absolute
                                  || style.Position == PositionValue.Fixed;

        // CSS1 percentage width — resolved at layout against the containing
        // block (never the viewport).
        box.StyleWidthPercent = style.WidthPercent;
    }

    private static void ResolveBoxPercentages(LayoutBox box, float containingWidth)
    {
        float basis = Math.Max(0f, containingWidth);
        if (box.MarginTopPercent.HasValue) box.MarginTop = basis * box.MarginTopPercent.Value / 100f;
        if (box.MarginRightPercent.HasValue) box.MarginRight = basis * box.MarginRightPercent.Value / 100f;
        if (box.MarginBottomPercent.HasValue) box.MarginBottom = basis * box.MarginBottomPercent.Value / 100f;
        if (box.MarginLeftPercent.HasValue) box.MarginLeft = basis * box.MarginLeftPercent.Value / 100f;
        if (box.PaddingTopPercent.HasValue) box.PaddingTop = Math.Max(0f, basis * box.PaddingTopPercent.Value / 100f);
        if (box.PaddingRightPercent.HasValue) box.PaddingRight = Math.Max(0f, basis * box.PaddingRightPercent.Value / 100f);
        if (box.PaddingBottomPercent.HasValue) box.PaddingBottom = Math.Max(0f, basis * box.PaddingBottomPercent.Value / 100f);
        if (box.PaddingLeftPercent.HasValue) box.PaddingLeft = Math.Max(0f, basis * box.PaddingLeftPercent.Value / 100f);
    }

    /// <summary>
    /// Applies 1996-era HTML presentational attributes to a layout box — the
    /// primary layout mechanism of the period.  Spacing and border attributes
    /// are applied BEFORE the width/height math so percentage sizes can
    /// account for them.
    /// </summary>
    private static void ApplyHtmlPresentationalAttrs(
        LayoutBox box, DomElement elem, float containingWidth)
    {
        // hspace / vspace (NN2 image spacing extension) ───────────────────
        int hspace = Math.Max(0, elem.GetAttrInt("hspace", 0));
        int vspace = Math.Max(0, elem.GetAttrInt("vspace", 0));
        if (hspace > 0)
        {
            box.MarginLeft = Math.Max(box.MarginLeft, hspace);
            box.MarginRight = Math.Max(box.MarginRight, hspace);
        }
        if (vspace > 0)
        {
            box.MarginTop = Math.Max(box.MarginTop, vspace);
            box.MarginBottom = Math.Max(box.MarginBottom, vspace);
        }

        // border on images, image inputs, and tables (HTML attribute, not CSS property).
        // <TABLE BORDER> with no value means BORDER=1 (HTML 3.2).
        if (elem.TagName is "img" or "table" ||
            (elem.TagName == "input" &&
             elem.GetAttrOrDefault("type", "text").Trim()
                 .Equals("image", StringComparison.OrdinalIgnoreCase)))
        {
            int bw = elem.GetAttrInt("border", -1);
            if (bw < 0 && elem.HasAttr("border") &&
                string.IsNullOrWhiteSpace(elem.GetAttr("border")))
                bw = 1;                     // bare "border" attribute
            if (bw >= 0)
            {
                bw = Math.Min(bw, (int)MaxAttrLength);
                var style = elem.Style;
                if (style?.OwnBorderTopWidth != true) box.BorderTop = bw;
                if (style?.OwnBorderRightWidth != true) box.BorderRight = bw;
                if (style?.OwnBorderBottomWidth != true) box.BorderBottom = bw;
                if (style?.OwnBorderLeftWidth != true) box.BorderLeft = bw;
            }
        }

        // width (pixels or percentage) ─────────────────────────────────────
        string? wAttr = elem.GetAttr("width");
        if (!string.IsNullOrEmpty(wAttr))
        {
            var parsed = ParseLengthOrPercent(wAttr);
            if (parsed.Percent.HasValue)
            {
                float computed = containingWidth * parsed.Percent.Value / 100f
                               - box.MarginLeft - box.MarginRight
                               - box.BorderLeft - box.BorderRight
                               - box.PaddingLeft - box.PaddingRight;
                if (computed > 0f) box.Width = Math.Min(computed, MaxAttrLength);
            }
            else if (parsed.Pixels > 0f)
            {
                box.Width = parsed.Pixels;
            }
        }

        // height (pixels only at generation time; % resolved during layout) ─
        string? hAttr = elem.GetAttr("height");
        if (!string.IsNullOrEmpty(hAttr))
        {
            var parsed = ParseLengthOrPercent(hAttr);
            if (parsed.Pixels > 0f)
                box.Height = parsed.Pixels;
        }

        // align="left|right" floats the element (images, inline frames,
        // objects, applets, and tables — the classic 1996 text-wrap pattern)
        if (elem.TagName is "img" or "iframe" or "object" or "applet"
                        or "embed" or "table")
        {
            string align = elem.GetAttrOrDefault("align", "").Trim().ToLowerInvariant();
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
        // (img natural sizing lives in ResolveImageNaturalSize, called AFTER
        // CSS sizing in GenerateBoxes — see the FIX note there.)
        switch (elem.TagName)
        {
            case "embed":
                // LiveAudio was a plugin box. When neither dimension is
                // supplied, the requested compatibility mode treats the tag
                // as hidden/no-UI. If only one dimension is supplied, use the
                // era's common 145×60 control-box fallback for the missing side.
                if (box.Width <= 0f && box.Height <= 0f &&
                    !elem.HasAttr("width") && !elem.HasAttr("height"))
                {
                    box.Width = 0f;
                    box.Height = 0f;
                }
                else
                {
                    if (box.Width <= 0f && !elem.HasAttr("width")) box.Width = 145f;
                    if (box.Height <= 0f && !elem.HasAttr("height")) box.Height = 60f;
                }
                break;

            case "iframe":
                // No WIDTH/HEIGHT → the period default frame size; without
                // this the frame rect is 0×0 and the child document is
                // never visible.
                if (box.Width <= 0f) box.Width = 300f;
                if (box.Height <= 0f) box.Height = 150f;
                break;

            case "input":
                {
                    string t = elem.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
                    if (t == "hidden")
                    {
                        box.Width = 0f;   // hidden inputs take no space at all
                        box.Height = 0f;
                        break;
                    }
                    if (box.Height <= 0f) box.Height = 22f;
                    if (box.Width <= 0f)
                    {
                        box.Width = t switch
                        {
                            "checkbox" or "radio" => 16f,
                            "submit" or "reset" or "button" => 80f,
                            "image" => 32f,
                            _ => 150f  // text, password, …
                        };

                        // Natural (label/size-driven) width computed HERE, at
                        // box-build time, so the table column pass that follows
                        // already sees the real control width.
                        if (!elem.HasAttr("width") &&
                            InlineLayout.ControlNaturalSize(elem, elem.Style,
                                out float nw, out float nh))
                        {
                            if (nw > 0f) box.Width = nw;
                            if (nh > 0f) box.Height = nh;
                        }
                    }
                    break;
                }

            case "select":
                if (box.Width <= 0f) box.Width = 150f;
                if (box.Height <= 0f) box.Height = 22f;
                if (!elem.HasAttr("width") &&
                    InlineLayout.ControlNaturalSize(elem, elem.Style, out float sw, out float sh))
                {
                    if (sw > 0f) box.Width = sw;
                    if (sh > 0f) box.Height = sh;
                }
                break;

            case "textarea":
                if (box.Width <= 0f) box.Width = Math.Min(elem.GetAttrInt("cols", 20) > 0
                    ? elem.GetAttrInt("cols", 20) * 8f : 160f, MaxAttrLength);
                if (box.Height <= 0f) box.Height = Math.Min(elem.GetAttrInt("rows", 4) * 16f, MaxAttrLength);
                if (!elem.HasAttr("width") &&
                    InlineLayout.ControlNaturalSize(elem, elem.Style, out float tw, out float th))
                {
                    if (tw > 0f) box.Width = tw;
                    if (th > 0f) box.Height = th;
                }
                break;

            case "button":
                if (box.Width <= 0f) box.Width = 80f;
                if (box.Height <= 0f) box.Height = 22f;
                if (!elem.HasAttr("width") &&
                    InlineLayout.ControlNaturalSize(elem, elem.Style, out float bw, out float bh))
                {
                    if (bw > 0f) box.Width = bw;
                    if (bh > 0f) box.Height = bh;
                }
                break;

            case "isindex":
                if (box.Width <= 0f) box.Width = 200f;
                if (box.Height <= 0f) box.Height = 22f;
                break;

            case "hr":
                // SIZE is the rule thickness (HTML 3.2, default 2).
                // No WIDTH → auto → full containing width (the NN 100%
                // default), resolved during block layout.
                float size = ParseLengthOrPercent(elem.GetAttrOrDefault("size", "")).Pixels;
                box.Height = Math.Max(1f, Math.Min(size > 0f ? size : 2f, MaxAttrLength));
                break;
        }
    }

    /// <summary>
    /// Fills missing img dimensions from the decoded natural size, deriving
    /// the second dimension by aspect ratio when only one is known.  Runs
    /// AFTER CSS width/height so a CSS-set dimension participates in the
    /// aspect math.  32×32 remains the not-yet-loaded placeholder.
    /// </summary>
    private static void ResolveImageNaturalSize(LayoutBox box, DomElement elem)
    {
        // A missing-SRC image has no natural bitmap size. Keep its broken
        // marker and ALT text readable by expanding the fallback box to fit
        // the label, even when a small legacy WIDTH was supplied.
        if (!elem.HasAttr("src") && !elem.HasAttr("lowsrc"))
        {
            string? alt = elem.GetAttr("alt");
            if (!string.IsNullOrEmpty(alt))
                box.Width = Math.Max(box.Width, Math.Min(MaxAttrLength,
                    alt.Length * 7.5f + 28f));
        }

        if (box.Width > 0f && box.Height > 0f) return;

        string? naturalSrc = elem.GetAttr("src") ?? elem.GetAttr("lowsrc");
        if (!string.IsNullOrEmpty(naturalSrc))
        {
            string abs;
            try
            {
                abs = Retro96.Engine.Render.ImageCache.ResolveUrl(
                    naturalSrc, elem.OwnerDocument()?.BaseUrl?.ToAbsolute());
            }
            catch { abs = naturalSrc; }

            if (Retro96.Engine.Render.NaturalImageSizes.TryGetSize(abs, out int nw, out int nh))
            {
                if (box.Width > 0f && box.Height <= 0f && nw > 0)
                    box.Height = box.Width * nh / nw;
                else if (box.Height > 0f && box.Width <= 0f && nh > 0)
                    box.Width = box.Height * nw / nh;
                else
                {
                    if (box.Width <= 0f) box.Width = nw;
                    if (box.Height <= 0f) box.Height = nh;
                }
            }
        }
        if (box.Width <= 0f) box.Width = 32f;   // placeholder until load
        if (box.Height <= 0f) box.Height = 32f;
    }

    /// <summary>Parses "50", "50%", "1.5" → pixels and/or percent. Invariant culture.
    /// Pixel values are clamped to [0, MaxAttrLength] — hostile huge values
    /// used to reach the render bitmap as-is (multi-GB allocation).</summary>
    private static (float Pixels, float? Percent) ParseLengthOrPercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (0f, null);

        string t = value.Trim();
        if (t.EndsWith('%'))
        {
            if (float.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                return (0f, pct);
            return (0f, null);
        }
        return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float px)
            ? (Math.Clamp(px, 0f, MaxAttrLength), null)
            : (0f, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 1996-era UA stylesheet (Netscape Navigator 2 / 3 defaults)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ComputedStyle matching the Netscape Navigator 2/3 user-agent stylesheet
    /// for the given element, or null for purely non-visual elements.
    /// Only used when StyleResolver did not produce a style (synthetic docs,
    /// error pages); the result is cached onto elem.Style by the caller.
    /// </summary>
    private static ComputedStyle? FallbackStyleFor(DomElement elem)
    {
        var s = new ComputedStyle();

        switch (elem.TagName)
        {
            // ── Headings (bold, NN size scale) ────────────────────────────
            case "h1":
                s.Display = DisplayValue.Block; s.FontWeight = FontWeightValue.Bold;
                s.FontSize = 32f; s.MarginTop = 14; s.MarginBottom = 14; break;
            case "h2":
                s.Display = DisplayValue.Block; s.FontWeight = FontWeightValue.Bold;
                s.FontSize = 24f; s.MarginTop = 14; s.MarginBottom = 14; break;
            case "h3":
                s.Display = DisplayValue.Block; s.FontWeight = FontWeightValue.Bold;
                s.FontSize = 19f; s.MarginTop = 12; s.MarginBottom = 12; break;
            case "h4":
                s.Display = DisplayValue.Block; s.FontWeight = FontWeightValue.Bold;
                s.FontSize = 16f; s.MarginTop = 12; s.MarginBottom = 12; break;
            case "h5":
                s.Display = DisplayValue.Block; s.FontWeight = FontWeightValue.Bold;
                s.FontSize = 13f; s.MarginTop = 10; s.MarginBottom = 10; break;
            case "h6":
                s.Display = DisplayValue.Block; s.FontWeight = FontWeightValue.Bold;
                s.FontSize = 11f; s.MarginTop = 10; s.MarginBottom = 10; break;

            // ── Paragraph / generic blocks ─────────────────────────────────
            case "p":
                s.Display = DisplayValue.Block;
                s.MarginTop = 8; s.MarginBottom = 8; break;
            case "div": s.Display = DisplayValue.Block; break;

            // HTML5-era semantic elements degrade to blocks
            case "article":
            case "section":
            case "aside":
            case "header":
            case "footer":
            case "main":
            case "nav":
            case "figure":
                s.Display = DisplayValue.Block; break;

            // ── <center> (NN extension — block + centring) ──────────────────
            case "center":
                s.Display = DisplayValue.Block;
                s.TextAlign = TextAlign.Center; break;

            // ── Blockquote ─────────────────────────────────────────────────
            case "blockquote":
                s.Display = DisplayValue.Block;
                s.MarginLeft = 40; s.MarginRight = 40;
                s.MarginTop = 8; s.MarginBottom = 8; break;

            // ── Lists ──────────────────────────────────────────────────────
            case "ul":
            case "ol":
            case "menu":
            case "dir":
                s.Display = DisplayValue.Block;
                s.MarginLeft = 40; s.MarginTop = 8; s.MarginBottom = 8; break;
            case "li":
                s.Display = DisplayValue.ListItem;
                s.MarginBottom = 2; break;
            case "dl":
                s.Display = DisplayValue.Block;
                s.MarginTop = 8; s.MarginBottom = 8; break;
            case "dt":
                s.Display = DisplayValue.Block; break;
            case "dd":
                s.Display = DisplayValue.Block;
                s.MarginLeft = 40; break;

            // ── Preformatted (monospace, literal whitespace) ───────────────
            case "pre":
            case "listing":
            case "xmp":
            case "plaintext":
                s.Display = DisplayValue.Block;
                s.FontFamily = ["Courier New", "monospace"];
                s.FontSize = 13f;
                s.WhiteSpace = WhiteSpaceValue.Pre;
                s.MarginTop = 8; s.MarginBottom = 8; break;

            // ── Tables ─────────────────────────────────────────────────────
            case "table":
                s.Display = DisplayValue.Table;
                s.MarginTop = 4; s.MarginBottom = 4; break;
            case "tbody":
            case "thead":
            case "tfoot":
                s.Display = DisplayValue.TableRowGroup; break;
            case "tr":
                s.Display = DisplayValue.TableRow; break;
            case "td":
                s.Display = DisplayValue.TableCell;
                s.PaddingTop = 2; s.PaddingRight = 5;
                s.PaddingBottom = 2; s.PaddingLeft = 5; break;
            case "th":
                s.Display = DisplayValue.TableCell;
                s.FontWeight = FontWeightValue.Bold;
                s.TextAlign = TextAlign.Center;
                s.PaddingTop = 2; s.PaddingRight = 5;
                s.PaddingBottom = 2; s.PaddingLeft = 5; break;
            case "caption":
                s.Display = DisplayValue.TableCaption; break;
            case "col":
            case "colgroup":
                s.Display = DisplayValue.None; break;

            // ── Forms ───────────────────────────────────────────────────────
            case "form":
                s.Display = DisplayValue.Block;
                s.MarginBottom = 8; break;
            case "fieldset":
                s.Display = DisplayValue.Block;
                s.BorderTopWidth = 1; s.BorderRightWidth = 1;
                s.BorderBottomWidth = 1; s.BorderLeftWidth = 1;
                s.PaddingTop = 6; s.PaddingRight = 6;
                s.PaddingBottom = 6; s.PaddingLeft = 6; break;
            case "legend":
            case "label":
                s.Display = DisplayValue.Inline; break;

            // ── Replaced inline ─────────────────────────────────────────────
            case "img":
            case "object":
            case "embed":
            case "applet":
                s.Display = DisplayValue.Inline; break;

            case "input":
            case "select":
            case "textarea":
                s.Display = DisplayValue.InlineBlock;
                s.BorderTopStyle = s.BorderRightStyle
                                     = s.BorderBottomStyle
                                     = s.BorderLeftStyle = BorderStyleValue.Inset;
                s.BorderTopWidth = s.BorderRightWidth
                                     = s.BorderBottomWidth
                                     = s.BorderLeftWidth = 2; break;
            case "button":
                s.Display = DisplayValue.InlineBlock;
                s.BorderTopStyle = s.BorderRightStyle
                                     = s.BorderBottomStyle
                                     = s.BorderLeftStyle = BorderStyleValue.Outset;
                s.BorderTopWidth = s.BorderRightWidth
                                     = s.BorderBottomWidth
                                     = s.BorderLeftWidth = 2; break;

            // ── <hr> ───────────────────────────────────────────────────────
            case "hr":
                s.Display = DisplayValue.Block;
                s.MarginTop = 4; s.MarginBottom = 4; break;

            // ── Common inline elements ─────────────────────────────────────
            case "a":
            case "span":
            case "abbr":
            case "acronym":
            case "cite":
            case "q":
            case "dfn":
            case "big":
            case "small":
            case "sup":
            case "sub":
            case "code":
            case "tt":
            case "kbd":
            case "samp":
            case "var":
                s.Display = DisplayValue.Inline; break;

            // StyleResolver is authoritative for the semantic formatting
            // defaults above.  Do not re-apply them here: the layout pass
            // must preserve author CSS such as `font-weight: lighter` on
            // <b>/<strong>, `font-style: normal` on <i>/<em>, and explicit
            // text-decoration overrides.  These formatting defaults remain
            // available through FallbackStyleFor() for synthetic documents
            // where no resolved style exists.
            case "b":
            case "strong":
            case "i":
            case "em":
            case "u":
            case "ins":
            case "s":
            case "strike":
            case "del":
                s.Display = DisplayValue.Inline; break;

            // ── NN2/3 + IE extensions ──────────────────────────────────────
            case "font": s.Display = DisplayValue.Inline; break;   // NN-FONT
            case "blink": s.Display = DisplayValue.Inline; break;   // NN-BLINK
            case "nobr":
                s.Display = DisplayValue.Inline;
                s.WhiteSpace = WhiteSpaceValue.Nowrap; break;
            case "wbr": s.Display = DisplayValue.Inline; break;
            case "marquee":
                s.Display = DisplayValue.Block;
                s.WhiteSpace = WhiteSpaceValue.Nowrap;
                s.Overflow = OverflowValue.Hidden;
                break;  // IE-MARQUEE
            case "multicol": s.Display = DisplayValue.Block; break; // NN25
            case "layer": case "ilayer": s.Display = DisplayValue.Block; break;

            // ── Line break ─────────────────────────────────────────────────
            case "br": s.Display = DisplayValue.Inline; break;

            // ── Address (italic block) ─────────────────────────────────────
            case "address":
                s.Display = DisplayValue.Block;
                s.FontStyle = FontStyleValue.Italic;
                s.MarginTop = 4; s.MarginBottom = 4; break;

            // ── Non-visual elements (produce no box) ────────────────────────
            case "map":
            case "area":
            case "bgsound":
                return null;

            // ── Unknown elements → inline (NN2 default) ────────────────────
            default:
                s.Display = DisplayValue.Inline; break;
        }

        return s;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Block formatting context layout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Block-formatting-context layout for a box and all of its in-flow
    /// children.  Tables go through TableLayout; MULTICOL gets the balanced
    /// column treatment; everything else flows as a plain block.
    /// </summary>
    private static void LayoutBlock(
        LayoutBox box, float containingWidth, float containingHeight,
        FloatContext? inheritedFloats = null)
    {
        ResolveBoxPercentages(box, containingWidth);
        if (box.BoxType == BoxType.Table ||
            box.Element?.Style?.Display == DisplayValue.InlineTable)
        {
            ResolvePercentWidth(box, containingWidth);
            // A floated table must inherit the active flow exclusion band, but it
            // must not mutate the same FloatContext object while laying out its
            // own cell content.  The table body and the outer flow are separate
            // formatting contexts, even though the floats may start at the same Y.
            TableLayout.Layout(box, containingWidth, inheritedFloats?.Clone());
            return;
        }

        // <multicol> — NN multi-column extension
        if (box.Element?.TagName == "multicol")
        {
            LayoutMulticolumn(box, containingWidth, containingHeight);
            return;
        }

        ResolveAutoWidth(box, containingWidth);

        LayoutBlockChildren(box, containingWidth, containingHeight, inheritedFloats);

        // CSS2 min-height/max-height on the resolved (auto or explicit)
        // height — the width clamp already ran inside ResolveAutoWidth.
        ClampHeightToBounds(box, containingHeight);
    }

    internal static float LayoutInlineRun(
        IReadOnlyList<LayoutBox> inlineBoxes, float containerWidth,
        float contentX, float startY, ComputedStyle containerStyle,
        FloatContext? floats, float containingHeight)
    {
        var inlineBlocks = new List<(LayoutBox Box, float X, float Y)>();
        foreach (var container in inlineBoxes.Where(child =>
                     child.BoxType == BoxType.InlineBlock && child.Element != null))
        {
            if (container.Element?.Style?.Display == DisplayValue.TableCell &&
                container.Width <= 0f)
            {
                float chrome = container.PaddingLeft + container.PaddingRight
                    + container.BorderLeft + container.BorderRight;
                float available = Math.Max(0f,
                    containerWidth - container.MarginLeft - container.MarginRight);
                container.Width = Math.Max(0f, Math.Min(available,
                    TableLayout.MeasureInlineCellPreferredWidth(container)) - chrome);
            }

            container.X = contentX + container.MarginLeft;
            container.Y = startY;
            LayoutBlock(container,
                container.Width > 0f ? container.Width : containerWidth,
                containingHeight, floats?.Clone());
            inlineBlocks.Add((container, container.X, container.Y));
        }

        float height = InlineLayout.Layout(
            inlineBoxes, containerWidth, contentX, startY, containerStyle, floats);
        foreach (var (container, oldX, oldY) in inlineBlocks)
        {
            float dx = container.X - oldX;
            float dy = container.Y - oldY;
            if (Math.Abs(dx) < 0.01f && Math.Abs(dy) < 0.01f) continue;
            foreach (var child in container.Children)
                OffsetBoxTree(child, dx, dy);
        }
        return height;
    }

    /// <summary>
    /// Re-resolves a percentage WIDTH attribute against the now-known
    /// containing width (the generation pass could only guess at ancestors'
    /// content widths).  Pixel widths are left untouched and boxes without a
    /// percentage width are never stretched here.
    /// </summary>
    private static void ResolvePercentWidth(LayoutBox box, float containingWidth)
    {
        string? wAttr = box.Element?.GetAttr("width");
        if (string.IsNullOrEmpty(wAttr) || !wAttr.TrimEnd().EndsWith('%'))
            return;

        var parsed = ParseLengthOrPercent(wAttr);
        if (!parsed.Percent.HasValue)
            return;

        float resolved = containingWidth * parsed.Percent.Value / 100f
                       - box.MarginLeft - box.MarginRight
                       - box.BorderLeft - box.BorderRight
                       - box.PaddingLeft - box.PaddingRight;
        if (resolved > 0f) box.Width = Math.Min(resolved, MaxAttrLength);
    }

    /// <summary>
    /// Resolves a percentage HEIGHT attribute (the classic
    /// &lt;table height="100%"&gt; trick) against the containing height.
    /// The generation pass only ever records pixel heights.
    /// </summary>
    private static void ResolvePercentHeight(LayoutBox box, float containingHeight)
    {
        if (containingHeight <= 0f || box.Height > 0f)
            return;

        string? hAttr = box.Element?.GetAttr("height");
        if (string.IsNullOrEmpty(hAttr) || !hAttr.TrimEnd().EndsWith('%'))
            return;

        var parsed = ParseLengthOrPercent(hAttr);
        if (!parsed.Percent.HasValue)
            return;

        float resolved = containingHeight * parsed.Percent.Value / 100f
                       - box.MarginTop - box.MarginBottom
                       - box.BorderTop - box.BorderBottom
                       - box.PaddingTop - box.PaddingBottom;
        if (resolved > 0f) box.Height = resolved;
    }

    /// <summary>
    /// Resolves the box's content width: an explicit percentage WIDTH attr is
    /// re-resolved against the now-known containing width, then an unset
    /// (zero) width stretches to fill the container minus its own
    /// margins/borders/padding.
    /// </summary>
    private static void ResolveAutoWidth(LayoutBox box, float containingWidth)
    {
        bool hasCssPercentageWidth = box.StyleWidthPercent.HasValue;

        // CSS percentage width first — the real containing width is known
        // only here (the generation pass would have had to guess at the
        // ancestor chain).
        if (box.StyleWidthPercent is { } pct)
        {
            float resolved = containingWidth * pct / 100f;
            box.Width = Math.Min(Math.Max(0f, resolved), MaxAttrLength);
            box.StyleWidthPercent = null;   // consumed
        }

        ResolvePercentWidth(box, containingWidth);

        if (box.Width <= 0f && !hasCssPercentageWidth)
        {
            box.Width = Math.Max(0f,
                containingWidth
                - box.MarginLeft - box.MarginRight
                - box.BorderLeft - box.BorderRight
                - box.PaddingLeft - box.PaddingRight);
        }

        // CSS2 min/max-width applies to whatever width was resolved above
        // (authored, percentage or auto-stretched).  Percentage bounds
        // resolve against the containing block exactly like width %.
        // NOTE: the anonymous-block width computed in LayoutBlockChildren
        // needs no IE5 variant — anonymous boxes never carry padding or
        // border, so border-box and content-box coincide for them.
        ClampWidthToBounds(box, containingWidth);
    }

    /// <summary>
    /// IE5 quirks-mode interpretation of an authored CSS width/height
    /// (checklist §9): the authored extent is the BORDER box, so the content
    /// extent is authored minus the padding and border chrome.  IMG elements
    /// ignore padding entirely (margin+border+width+border+margin, the
    /// classic IE image rule).  In the W3C model the authored value already
    /// IS the content extent and passes through unchanged.
    /// </summary>
    private static float AuthoredContentExtent(DomElement elem, float authored,
        float paddingExtent, float borderExtent)
    {
        if (!BrowserRuntime.UsesIe5BoxModelFor(elem.OwnerDocument()))
            return authored;
        float pad = elem.TagName == "img" ? 0f : paddingExtent;
        return Math.Max(0f, authored - pad - borderExtent);
    }

    /// <summary>
    /// CSS2 min-width/max-width clamp.  In the W3C model the constraint
    /// applies to the CONTENT width; under the IE5 quirks box model the
    /// authored width semantics are border-box, so the clamp is applied to
    /// the border-box extent and converted back.  Idempotent (safe to run
    /// in both the generation pass and block layout).
    /// </summary>
    private static void ClampWidthToBounds(LayoutBox box, float containingWidth)
    {
        var style = box.Element?.Style;
        if (style == null) return;
        if (!style.MinWidth.HasValue && !style.MaxWidth.HasValue &&
            !style.MinWidthPercent.HasValue && !style.MaxWidthPercent.HasValue)
            return;

        float basis = Math.Max(0f, containingWidth);
        float min = style.MinWidth ??
            (style.MinWidthPercent is { } mp ? basis * mp / 100f : 0f);
        float max = style.MaxWidth ??
            (style.MaxWidthPercent is { } xp ? basis * xp / 100f : float.MaxValue);
        if (max < min) max = min;

        if (BrowserRuntime.UsesIe5BoxModelFor(box.Element?.OwnerDocument()))
        {
            float chrome = box.BorderLeft + box.BorderRight
                         + box.PaddingLeft + box.PaddingRight;
            float outer = box.Width + chrome;
            if (outer < min) outer = min;
            if (outer > max) outer = max;
            box.Width = Math.Max(0f, outer - chrome);
        }
        else
        {
            if (box.Width < min) box.Width = min;
            if (box.Width > max) box.Width = max;
        }
    }

    /// <summary>
    /// CSS2 min-height/max-height clamp, mirroring <see cref="ClampWidthToBounds"/>.
    /// <paramref name="containingHeight"/> of 0 leaves percentage bounds
    /// unresolved-at-zero (a containing height that is not yet known — the
    /// pixel bounds still apply).
    /// </summary>
    private static void ClampHeightToBounds(LayoutBox box, float containingHeight)
    {
        var style = box.Element?.Style;
        if (style == null) return;
        if (!style.MinHeight.HasValue && !style.MaxHeight.HasValue &&
            !style.MinHeightPercent.HasValue && !style.MaxHeightPercent.HasValue)
            return;

        float basis = Math.Max(0f, containingHeight);
        float min = style.MinHeight ??
            (style.MinHeightPercent is { } mp ? basis * mp / 100f : 0f);
        float max = style.MaxHeight ??
            (style.MaxHeightPercent is { } xp ? basis * xp / 100f : float.MaxValue);
        if (max < min) max = min;

        if (BrowserRuntime.UsesIe5BoxModelFor(box.Element?.OwnerDocument()))
        {
            float chrome = box.BorderTop + box.BorderBottom
                         + box.PaddingTop + box.PaddingBottom;
            float outer = box.Height + chrome;
            if (outer < min) outer = min;
            if (outer > max) outer = max;
            box.Height = Math.Max(0f, outer - chrome);
        }
        else
        {
            if (box.Height < min) box.Height = min;
            if (box.Height > max) box.Height = max;
        }
    }

    /// <summary>
    /// The heart of block layout: one pass over the children in source order.
    /// Consecutive inline-level children are batched into inline runs; a
    /// block-level child flushes the pending run first so everything renders
    /// in document order; floats are placed at the current flow Y and
    /// registered into the shared FloatContext so later runs wrap around
    /// them.  When several floats stack, each new float that does not fit
    /// beside the earlier ones slides down until it finds room — the
    /// Netscape side-by-side float behaviour.
    /// </summary>
    private static void LayoutBlockChildren(
        LayoutBox box, float containingWidth, float containingHeight,
        FloatContext? inheritedFloats = null)
    {
        float contentX = box.X + box.BorderLeft + box.PaddingLeft;
        if (box.ListMarkerInside) contentX += 20f;
        float contentY = box.Y + box.BorderTop + box.PaddingTop;

        // currentY tracks the BORDER-BOX END of the last placed in-flow
        // child — the previous child's bottom margin is NOT consumed into
        // it.  It is held in prevMarginBottom and collapses with the next
        // child's margin-top (Math.Max below), which is what CSS1 margin
        // collapsing means for adjacent siblings.
        float currentY = contentY;

        bool hasExplicitHeight = box.Height > 0f;

        // Anonymous boxes inherit their formatting style from the real
        // container one level up so centring/alignment survives the wrap.
        var containerStyle = box.Element?.Style
                          ?? box.Parent?.Element?.Style
                          ?? DefaultStyle;

        bool centerBlockChildren =
            box.Element?.TagName == "center" ||
            containerStyle.TextAlign == TextAlign.Center;

        float prevMarginBottom = 0f;

        var floatCtx = inheritedFloats ?? new FloatContext();
        var pendingInline = new List<LayoutBox>();

        void FlushInlineRun()
        {
            if (pendingInline.Count == 0) return;

            // Whitespace-only runs render nothing — and they must not break
            // margin collapsing between the blocks on either side of them.
            if (!IsAllWhitespaceRun(pendingInline))
            {
                // Inline content has no margin-top; the pending collapsed
                // margin from the preceding block still applies above the
                // first line box.
                float start = currentY + prevMarginBottom;
                LayoutTrace.Log($"FlushInlineRun: box(tag={box.Element?.TagName},anon={box.BoxType == BoxType.Anonymous}) " +
                    $"currentY={currentY:F1} prevMarginBottom={prevMarginBottom:F1} => start={start:F1} " +
                    $"contentX={contentX:F1} box.Width={box.Width:F1}");
                float inlineHeight = LayoutInlineRun(
                    pendingInline, box.Width, contentX, start,
                    containerStyle, floatCtx, containingHeight);
                currentY = start + inlineHeight;
                prevMarginBottom = 0f;   // real inline content breaks collapsing
            }

            pendingInline.Clear();
        }

        foreach (var child in box.Children)
        {
            ResolveBoxPercentages(child, box.Width);
            if (child.IsAbsolutelyPositioned)
                continue;   // second pass below

            if (child.IsFloated)
            {
                // Floats sit at the current flow position — after the
                // pending collapsed margin — but being out of flow they
                // neither consume it nor break collapsing between the
                // in-flow blocks around them.
                LayoutTrace.Log($"LayoutBlockChildren: placing FLOAT tag={child.Element?.TagName} " +
                    $"at flow currentY={currentY:F1} prevMarginBottom={prevMarginBottom:F1} " +
                    $"=> placeY={currentY + prevMarginBottom:F1}, box.Width(container)={box.Width:F1}");
                PlaceFloat(child, contentX, currentY + prevMarginBottom,
                           box.Width, containingHeight, floatCtx);
                floatCtx.AddFloat(child);
                LayoutTrace.Log($"  float placed at final X={child.X:F1} Y={child.Y:F1} " +
                    $"W={child.Width:F1} H={child.Height:F1}");

                // FIX: PlaceFloat resolves the float's OWN top-margin
                // offset internally (box.Y ends up at the requested Y plus
                // its MarginTop), so a float with a non-zero top margin
                // lands strictly below the Y this loop still thinks of as
                // "current". If the very next sibling starts at that
                // stale, pre-margin currentY, its content is laid out
                // above the float's real top — GetLeftEdge/GetRightEdge
                // correctly report "no float active yet" for that Y band,
                // so the first line of wrapped text overlaps the float
                // instead of stepping around it. Floats still don't
                // consume flow height (this is NOT currentY += float
                // height), but the next sibling must not be dated earlier
                // than a float that has already, physically, started.
                if (child.Y > currentY)
                    currentY = child.Y;

                continue;
            }

            if (!IsBlockLevel(child))
            {
                pendingInline.Add(child);
                continue;
            }

            // Block-level child — flush the inline run that precedes it
            FlushInlineRun();

            // Percentage HEIGHT ("height=100%") — resolved now that the
            // containing height is known.
            ResolvePercentHeight(child, containingHeight);

            var clear = child.Element?.Style?.Clear ?? ClearValue.None;
            if (clear != ClearValue.None && floatCtx.HasFloats)
            {
                currentY = Math.Max(currentY, floatCtx.GetClearY(currentY, clear));
                floatCtx.ResetBandsBelow(currentY);
            }

            float collapsedMargin = CollapseMargins(prevMarginBottom, child.MarginTop);

            child.X = contentX + child.MarginLeft;
            child.Y = currentY + collapsedMargin;

            if (child.BoxType == BoxType.Anonymous)
            {
                child.Width = Math.Max(0f,
                    box.Width - child.MarginLeft - child.MarginRight
                             - child.BorderLeft - child.BorderRight
                             - child.PaddingLeft - child.PaddingRight);
                LayoutTrace.Log($"LayoutBlockChildren: recursing into ANON block " +
                    $"X={child.X:F1} Y={child.Y:F1} Width={child.Width:F1} " +
                    $"hasFloatsInCtx={floatCtx.HasFloats} floatsSoFar=[{Environment.NewLine}" +
                    $"{floatCtx.DebugDumpFloats()}]");
                LayoutBlockChildren(child, child.Width, containingHeight, floatCtx);
            }
            else if (child.BoxType is BoxType.Block
                             or BoxType.ListItem
                             or BoxType.TableRow
                             or BoxType.TableCell
                             or BoxType.TableCaption)
            {
                LayoutBlock(child, box.Width, containingHeight, floatCtx);
            }
            else if (child.BoxType == BoxType.Table)
            {
                ResolvePercentWidth(child, box.Width);   // % width vs real container
                TableLayout.Layout(child, box.Width, floatCtx);
            }
            else if (child.BoxType == BoxType.Replaced)
            {
                // Replaced block-level boxes (<hr>, …) never go through
                // LayoutBlock — resolve their % / auto width here against
                // the real containing width (auto = full width, the NN
                // default for <hr>).
                ResolveAutoWidth(child, box.Width);
            }

            // An empty block's vertical margins collapse through the block;
            // do not advance by its bottom margin and then collapse that same
            // margin again with the next sibling.
            bool collapsesThrough = child.Children.Count == 0
                                 && child.Height <= 0f
                                 && child.PaddingTop == 0f
                                 && child.PaddingBottom == 0f;
            if (collapsesThrough)
            {
                prevMarginBottom = CollapseMargins(
                    CollapseMargins(prevMarginBottom, child.MarginTop),
                    child.MarginBottom);
                continue;
            }

            // CSS1 auto margins — the leftover horizontal space is
            // distributed to the auto sides (both auto → centred, one auto
            // → absorbs the rest).
            bool hasAutoMargins = (child.MarginLeftAuto || child.MarginRightAuto)
                                  && child.Width > 0f;
            if (hasAutoMargins)
            {
                float borderBoxW = child.BorderLeft + child.PaddingLeft
                                 + child.Width
                                 + child.PaddingRight + child.BorderRight;
                float leftover = box.Width
                               - child.MarginLeft - borderBoxW - child.MarginRight;
                if (leftover > 0f)
                {
                    float newLeft = child.MarginLeft, newRight = child.MarginRight;
                    if (child.MarginLeftAuto && child.MarginRightAuto)
                    {
                        newLeft = leftover / 2f;
                        newRight = leftover / 2f;
                    }
                    else if (child.MarginLeftAuto)
                        newLeft = leftover;
                    else
                        newRight = leftover;

                    float newX = contentX + newLeft;
                    child.MarginLeft = newLeft;
                    child.MarginRight = newRight;
                    float deltaX = newX - child.X;
                    if (Math.Abs(deltaX) > 0.5f)
                        OffsetBoxTree(child, deltaX, 0f);
                }
            }

            // Centre a narrower block child when the parent is <center> /
            // text-align:center (NN4 also centred tables this way), or when
            // the child itself asked for ALIGN=center (classic <hr> and
            // <table align=center> centring).  <hr> with no ALIGN defaults
            // to center per HTML 3.2.  ALIGN=right on a block right-aligns
            // it.  The margin box is what gets centred/right-aligned.
            // (CSS auto margins already placed the box — they win.)
            string? selfAlign = child.Element?.GetAttr("align")?.Trim().ToLowerInvariant();
            bool centerThisChild = !hasAutoMargins
                                && (centerBlockChildren
                                || selfAlign == "center"
                                || (selfAlign == null && child.Element?.TagName == "hr"));

            float childOuterW = child.MarginLeft
                              + child.BorderLeft + child.PaddingLeft
                              + child.Width
                              + child.PaddingRight + child.BorderRight
                              + child.MarginRight;

            if (centerThisChild && child.Width > 0f)
            {
                if (childOuterW < box.Width - 1f)
                {
                    float newX = contentX + (box.Width - childOuterW) / 2f + child.MarginLeft;
                    float deltaX = newX - child.X;
                    if (Math.Abs(deltaX) > 0.5f)
                        OffsetBoxTree(child, deltaX, 0f);
                }
            }
            else if (selfAlign == "right" && child.Width > 0f)
            {
                float newX = contentX + box.Width - childOuterW + child.MarginLeft;
                float deltaX = newX - child.X;
                if (Math.Abs(deltaX) > 0.5f)
                    OffsetBoxTree(child, deltaX, 0f);
            }

            prevMarginBottom = child.MarginBottom;

            // Border-box end — the bottom margin stays pending in
            // prevMarginBottom so it can collapse with the next sibling.
            currentY = child.Y
                     + child.BorderTop + child.PaddingTop
                     + child.Height
                     + child.PaddingBottom + child.BorderBottom;
        }

        FlushInlineRun();

        if (!hasExplicitHeight)
        {
            box.Height = Math.Max(0f, currentY - contentY);
            if (box.PaddingBottom == 0f && box.BorderBottom == 0f &&
                box.Element?.Style is
                    { Overflow: OverflowValue.Visible } style &&
                style.MinHeight.GetValueOrDefault() <= 0f &&
                style.MinHeightPercent.GetValueOrDefault() <= 0f)
            {
                box.MarginBottom = CollapseMargins(box.MarginBottom, prevMarginBottom);
            }
        }

        // Absolutely positioned children — out of flow.  They are laid out
        // against this block's content area first (their subtree needs real
        // geometry), then anchored to its edges; Right/Bottom anchors need
        // the final size, so the anchor is recomputed after measurement and
        // the tree shifted by the difference.
        foreach (var child in box.Children.Where(c => c.IsAbsolutelyPositioned))
        {
            var st = child.Element?.Style;

            // Static-position fallback: where the child would have started
            // in flow when no edge offset was specified at all (not even a
            // percentage — a percent value counts as specified, it just
            // isn't resolved to pixels yet; the old check only looked at
            // the resolved Left/Top and missed LeftPercent/TopPercent, so
            // a "left:30%" box was treated as unpositioned here even
            // though it had a real offset).
            if (st?.Left == null && st?.LeftPercent == null &&
                st?.Right == null && st?.RightPercent == null)
                child.X = contentX;
            if (st?.Top == null && st?.TopPercent == null &&
                st?.Bottom == null && st?.BottomPercent == null)
                child.Y = currentY + prevMarginBottom;

            var positionedContainingBlock = st?.Position == PositionValue.Fixed
                ? FindViewportContainingBlock(box)
                : FindPositionedContainingBlock(box);
            LayoutBlock(child, positionedContainingBlock.Width, positionedContainingBlock.Height);

            // FIX: the containing block for an absolutely positioned box is its
            // nearest positioned ancestor (position != static), not necessarily
            // its immediate parent. The old code always passed `box` (the direct
            // parent), so a dialog like <div class="overlay"><div style="position:
            // absolute; top:30%; left:30%">...</div></div> anchored to the
            // unpositioned overlay's box instead of the page, landing wherever
            // that overlay happened to be laid out rather than at 30%/30% of the
            // viewport. The same containing block must also be used to resolve
            // any WIDTH/HEIGHT percentage on the box itself (see LayoutBlock
            // call above) — sizing against one box and positioning against a
            // different one is how a percent-width dialog like this one ended
            // up both the wrong size AND in the wrong place.
            //
            // LayoutAbsolute applies the final offset to the entire subtree. Do
            // not apply the resulting delta again here: doing so moved the
            // dialog twice (30% became roughly 60%, producing the detached
            // bottom-right gray box seen on the Acme CyberCorp page).
            LayoutAbsolute(child, positionedContainingBlock);
        }

        // Relatively positioned children
        foreach (var child in box.Children.Where(c =>
            c.Element is { } element &&
            element.Style?.Position == PositionValue.Relative &&
            ReferenceEquals(element.LayoutBox, c)))
            LayoutRelative(child);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MULTICOL (NN25) — balanced multi-column layout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Netscape's &lt;multicol cols=N gutter=G&gt;: children are flowed once
    /// at the column width, then the result is split across N balanced
    /// columns.  This mirrors NN's behaviour (which balanced content height
    /// across columns rather than filling each column to the page bottom).
    /// </summary>
    private static void LayoutMulticolumn(
        LayoutBox box, float containingWidth, float containingHeight)
    {
        var elem = box.Element;
        if (elem == null)
        {
            LayoutBlockChildren(box, containingWidth, containingHeight);
            return;
        }

        ResolveAutoWidth(box, containingWidth);
        float contentW = box.Width;

        int cols = Math.Clamp(elem.GetAttrInt("cols", 2), 1, 12);
        float gutter = Math.Max(0f,
            ParseLengthOrPercent(elem.GetAttrOrDefault("gutter", "10")).Pixels);

        if (cols <= 1 || contentW <= 1f)
        {
            LayoutBlockChildren(box, containingWidth, containingHeight);
            return;
        }

        bool hasExplicitHeight = box.Height > 0f;
        float colW = Math.Max(1f, (contentW - gutter * (cols - 1)) / cols);

        // Single-flow pass at column width
        box.Width = colW;
        LayoutBlockChildren(box, colW, containingHeight);
        box.Width = contentW;

        float contentX = box.X + box.BorderLeft + box.PaddingLeft;
        float contentY = box.Y + box.BorderTop + box.PaddingTop;

        var kids = box.Children.Where(c => !c.IsAbsolutelyPositioned).ToList();
        if (kids.Count == 0)
        {
            if (!hasExplicitHeight) box.Height = 0f;
            return;
        }

        float totalH = 0f;
        foreach (var k in kids)
            totalH = Math.Max(totalH, OuterBottom(k) - contentY);
        float target = totalH / cols;

        // Assign children to columns in order, then reposition.  Column 0
        // keeps its in-flow position; later columns are shifted so each
        // column's first child lands on the content top.
        int cur = 0;
        float colStart = contentY;
        float colMax = contentY;
        bool colHasContent = false;

        foreach (var k in kids)
        {
            float top = k.Y;
            float bottom = OuterBottom(k);

            if (colHasContent && cur < cols - 1 && (bottom - colStart) > target)
            {
                cur++;
                colStart = top;
                colHasContent = false;
            }

            float dx = cur * (colW + gutter);
            float dy = contentY - colStart;
            if (Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f)
                OffsetBoxTree(k, dx, dy);

            colMax = Math.Max(colMax, bottom + dy);
            colHasContent = true;
        }

        if (!hasExplicitHeight)
            box.Height = Math.Max(0f, colMax - contentY);
    }

    /// <summary>
    /// Bottom of a box's painted extent including padding/border and the
    /// bottom margin.  (Y is the border-box origin — the top margin lies
    /// above it and is not part of the extent below.)
    /// </summary>
    private static float OuterBottom(LayoutBox b) =>
        b.Y + b.BorderTop + b.PaddingTop
           + b.Height
           + b.PaddingBottom + b.BorderBottom + b.MarginBottom;

    // ─────────────────────────────────────────────────────────────────────────
    // Float placement
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Places a floated element at the current flow position without
    /// consuming vertical space.  The float is laid out and sized first, then
    /// positioned beside earlier floats on the same band; if there is no
    /// horizontal room it slides down float-by-float until it fits.
    /// </summary>
    private static void PlaceFloat(
        LayoutBox box, float contentX, float currentY,
        float containingWidth, float containingHeight, FloatContext floats)
    {
        // Provisional position so the subtree lays out in the right
        // neighbourhood; the tree is shifted by the delta once the final spot
        // (which depends on the measured size) is known.
        float provX = contentX;
        float provY = currentY;
        box.X = provX;
        box.Y = provY;

        if (box.BoxType == BoxType.Table)
        {
            ResolvePercentWidth(box, containingWidth);
            TableLayout.Layout(box, containingWidth, floats.Clone());
        }
        else
        {
            if (box.Width <= 0f)
            {
                float availableContentWidth = Math.Max(0f,
                    containingWidth
                    - box.MarginLeft - box.MarginRight
                    - box.BorderLeft - box.BorderRight
                    - box.PaddingLeft - box.PaddingRight);
                box.Width = TableLayout.MeasureShrinkToFitContentWidth(
                    box, availableContentWidth);
            }

            float floatContainerW = box.Width
                                  + box.MarginLeft + box.MarginRight
                                  + box.BorderLeft + box.BorderRight
                                  + box.PaddingLeft + box.PaddingRight;
            LayoutBlock(box, floatContainerW, containingHeight);
        }

        float boxW = box.BorderLeft + box.BorderRight
                   + box.PaddingLeft + box.PaddingRight
                   + box.Width;

        float top = currentY + box.MarginTop;

        // FIX: the FIT test now includes the margins — the margin BOX has to
        // fit in the band, not just the border box.  An hspace'd float used
        // to be wedged into a too-narrow band and overlap its neighbour.
        float outerW = boxW + box.MarginLeft + box.MarginRight;

        float y = top;
        if (floats.HasFloats)
        {
            var candidates = new List<float> { top };
            foreach (float bottom in floats.FloatBottoms())
                if (bottom > top + 0.5f)
                    candidates.Add(bottom);
            candidates.Sort();

            bool placed = false;
            foreach (float cand in candidates)
            {
                float leftEdge = Math.Max(contentX, floats.GetLeftEdge(cand));
                float rightEdge = floats.GetRightEdge(cand);
                if (rightEdge == float.MaxValue)
                    rightEdge = contentX + containingWidth;
                rightEdge = Math.Min(rightEdge, contentX + containingWidth);

                if (rightEdge - leftEdge >= outerW)
                {
                    y = cand;
                    placed = true;
                    break;
                }
            }
            if (!placed && candidates.Count > 0)
                y = candidates[^1];     // below every float
        }

        float newX;
        if (box.FloatSide == FloatValue.Right)
        {
            float boundary = contentX + containingWidth;
            float re = floats.GetRightEdge(y);
            if (re != float.MaxValue)
                boundary = Math.Min(boundary, re);
            newX = boundary - boxW - box.MarginRight;
        }
        else
        {
            newX = Math.Max(contentX, floats.GetLeftEdge(y)) + box.MarginLeft;
        }

        float dx = newX - provX;
        float dy = y - provY;
        box.X = newX;
        box.Y = y;
        LayoutTrace.Log($"  PlaceFloat internal: contentX={contentX:F1} currentY={currentY:F1} " +
            $"provX={provX:F1} provY={provY:F1} boxW(afterLayout)={box.Width:F1} " +
            $"outerW={outerW:F1} finalY={y:F1} finalX={newX:F1} dx={dx:F1} dy={dy:F1}");
        if (Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f)
            foreach (var child in box.Children)
                OffsetBoxTree(child, dx, dy);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Absolute / relative positioning
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from <paramref name="from"/> (inclusive) to find the nearest
    /// ancestor whose element has position: relative/absolute/fixed — that's
    /// the CSS containing block for an absolutely positioned descendant.
    /// Falls back to the root box (viewport) if nothing in the chain is
    /// positioned, matching normal browser behaviour.
    /// </summary>
    private static LayoutBox FindPositionedContainingBlock(LayoutBox from)
    {
        var current = from;
        LayoutBox root = from;
        while (current != null)
        {
            root = current;
            var pos = current.Element?.Style?.Position;
            if (pos == PositionValue.Relative
             || pos == PositionValue.Absolute
             || pos == PositionValue.Fixed)
                return current;
            current = current.Parent;
        }
        return root;
    }

    private static LayoutBox FindViewportContainingBlock(LayoutBox from)
    {
        var root = from;
        while (root.Parent != null)
            root = root.Parent;
        return root;
    }

    private static void LayoutAbsolute(LayoutBox box, LayoutBox containingBlock)
    {
        var style = box.Element?.Style;
        if (style == null) return;

        // CSS2 positions absolute descendants against the containing block's
        // padding box: the origin is the inner border edge, and its size
        // includes the containing block's padding.
        float cbPaddingX = containingBlock.X + containingBlock.BorderLeft;
        float cbPaddingY = containingBlock.Y + containingBlock.BorderTop;
        float cbWidth = style.Position == PositionValue.Fixed &&
            containingBlock.ViewportWidth > 0f
                ? containingBlock.ViewportWidth
                : containingBlock.Width + containingBlock.PaddingLeft + containingBlock.PaddingRight;
        float cbHeight = style.Position == PositionValue.Fixed &&
            containingBlock.ViewportHeight > 0f
                ? containingBlock.ViewportHeight
                : containingBlock.Height + containingBlock.PaddingTop + containingBlock.PaddingBottom;

        // FIX: top/left/right/bottom percentages used to be flattened to
        // pixels back in ComputedStyle.Apply, against the page viewport
        // width — for EVERY side, including top/bottom, which per spec
        // resolve against the containing block's HEIGHT, not the
        // viewport's width. That's how "top:30%; left:30%" on a dialog
        // ended up nowhere near 30% of its actual containing block: the
        // percentage was already gone, baked into the wrong number,
        // before layout ever knew what box it was supposed to be relative
        // to. TopPercent/LeftPercent/etc. carry the raw percentage through
        // instead, resolved here against the real containingBlock now that
        // it's known.
        float? left = style.Left ?? (style.LeftPercent is { } lp ? lp / 100f * cbWidth : null);
        float? right = style.Right ?? (style.RightPercent is { } rp ? rp / 100f * cbWidth : null);
        float? top = style.Top ?? (style.TopPercent is { } tp ? tp / 100f * cbHeight : null);
        float? bottom = style.Bottom ?? (style.BottomPercent is { } bp ? bp / 100f * cbHeight : null);

        // Work out the final border-box origin first, then shift the whole
        // subtree exactly once. The box was already laid out provisionally at
        // its static-flow position above, so descendants need the same delta.
        // Mutating box.X/Y and then shifting the entire tree from the caller
        // would double-apply the offset.
        float targetX = box.X;
        float targetY = box.Y;

        if (left.HasValue)
            targetX = cbPaddingX + left.Value + box.MarginLeft;
        else if (right.HasValue)
            targetX = cbPaddingX + cbWidth
                    - right.Value
                    - box.MarginRight - box.BorderLeft - box.BorderRight
                    - box.PaddingLeft - box.PaddingRight - box.Width;

        if (top.HasValue)
            targetY = cbPaddingY + top.Value + box.MarginTop;
        else if (bottom.HasValue)
            targetY = cbPaddingY + cbHeight
                    - bottom.Value
                    - box.MarginBottom - box.BorderTop - box.BorderBottom
                    - box.PaddingTop - box.PaddingBottom - box.Height;

        float dx = targetX - box.X;
        float dy = targetY - box.Y;
        if (Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f)
            OffsetBoxTree(box, dx, dy);
    }

    private static void LayoutRelative(LayoutBox box)
    {
        var style = box.Element?.Style;
        if (style == null) return;

        float dx = 0f, dy = 0f;
        if (style.Left.HasValue) dx = style.Left.Value;
        else if (style.Right.HasValue) dx = -style.Right.Value;

        if (style.Top.HasValue) dy = style.Top.Value;
        else if (style.Bottom.HasValue) dy = -style.Bottom.Value;

        if (dx == 0f && dy == 0f) return;

        // FIX: the whole SUBTREE shifts, not just the box.  The old version
        // moved only box.X/Y — the background/border slid out from under
        // the children, which stayed painted at the in-flow position.
        OffsetBoxTree(box, dx, dy);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Frameset layout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the frame grid for a frameset at the given position and size.
    /// Nested &lt;frameset&gt; children recurse into their own grids, so
    /// rows-containing-colsets (and any depth of mixing) lay out correctly.
    /// </summary>
    private static LayoutBox LayoutFrameset(
        DomElement frameset, float x, float y, float width, float height)
    {
        var box = new LayoutBox(frameset, BoxType.Block)
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
        PublishBox(frameset, box);

        var rows = ParseFramesetSizes(frameset.GetAttrOrDefault("rows", ""), height);
        var cols = ParseFramesetSizes(frameset.GetAttrOrDefault("cols", ""), width);

        if (rows.Count == 0) rows.Add(height);
        if (cols.Count == 0) cols.Add(width);

        // If the specified sizes overrun the frame, scale them back
        // proportionally (what Netscape did with e.g. rows="60%,60%").
        NormalizeSizes(rows, height);
        NormalizeSizes(cols, width);

        // Walk <frame> / nested <frameset> children in document order
        int frameIndex = 0;
        float cy = y;

        for (int r = 0; r < rows.Count; r++)
        {
            float cx = x;
            for (int c = 0; c < cols.Count; c++)
            {
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

                if (frameElem != null)
                {
                    if (frameElem.TagName == "frameset")
                    {
                        // Nested frameset — its own grid fills this slot
                        var nested = LayoutFrameset(frameElem, cx, cy, cols[c], rows[r]);
                        nested.Parent = box;
                        box.Children.Add(nested);
                    }
                    else
                    {
                        var frameBox = new LayoutBox(frameElem, BoxType.Frame)
                        {
                            X = cx,
                            Y = cy,
                            Width = cols[c],
                            Height = rows[r],
                            Parent = box
                        };
                        PublishBox(frameElem, frameBox);
                        box.Children.Add(frameBox);
                    }
                }

                cx += cols[c];   // advance even when the slot stayed empty
            }
            cy += rows[r];
        }

        return box;
    }

    /// <summary>
    /// Scales sizes down proportionally when their sum exceeds the total.
    /// </summary>
    private static void NormalizeSizes(List<float> sizes, float total)
    {
        if (total <= 0f) return;
        float sum = 0f;
        foreach (var s in sizes) sum += s;
        if (sum > total + 0.5f && sum > 0f)
        {
            float scale = total / sum;
            for (int i = 0; i < sizes.Count; i++)
                sizes[i] *= scale;
        }
    }

    /// <summary>
    /// Parses a frameset rows/cols attribute into pixel sizes.
    ///   "200"    – fixed pixels
    ///   "50%"    – percentage of the total dimension
    ///   "*"      – wildcard share of the remaining space
    ///   "2*,*"   – weighted wildcard (Netscape)
    /// Percentages and pixels that overrun the total squeeze the wildcards to
    /// nothing rather than overflowing the frameset.
    /// </summary>
    private static List<float> ParseFramesetSizes(string attr, float total)
    {
        if (string.IsNullOrWhiteSpace(attr))
            return new List<float>();

        string[] parts = attr.Split(',');
        var sizes = new List<float>(parts.Length);
        var wildcards = new List<(int Index, float Weight)>();
        float used = 0f;

        for (int i = 0; i < parts.Length; i++)
        {
            string t = parts[i].Trim();

            if (t == "*")
            {
                sizes.Add(0f);
                wildcards.Add((i, 1f));
            }
            else if (t.EndsWith('*'))
            {
                // weighted wildcard "N*"
                if (float.TryParse(t[..^1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float weight) && weight > 0f)
                {
                    sizes.Add(0f);
                    wildcards.Add((i, weight));
                }
                else
                {
                    sizes.Add(0f);
                    wildcards.Add((i, 1f));
                }
            }
            else if (t.EndsWith('%') &&
                     float.TryParse(t[..^1], NumberStyles.Float,
                         CultureInfo.InvariantCulture, out float pct))
            {
                float px = total * pct / 100f;
                sizes.Add(px);
                used += px;
            }
            else if (float.TryParse(t, NumberStyles.Float,
                         CultureInfo.InvariantCulture, out float px))
            {
                // FIX: clamp to [0, total] — a negative or oversized token
                // used to poison the grid geometry directly.
                px = Math.Clamp(px, 0f, Math.Max(0f, total));
                sizes.Add(px);
                used += px;
            }
            else
            {
                // malformed token → wildcard
                sizes.Add(0f);
                wildcards.Add((i, 1f));
            }
        }

        if (wildcards.Count > 0)
        {
            float totalWeight = 0f;
            foreach (var (_, w) in wildcards) totalWeight += w;

            float remaining = Math.Max(0f, total - used);
            foreach (var (idx, w) in wildcards)
                sizes[idx] = remaining * (w / totalWeight);
        }

        return sizes;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Utility
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsNonVisualTag(string tag) => tag switch
    {
        "script" or "noscript" or "style" or "link" or "meta"
             or "head" or "title" or "base" or "param"
             or "noframes" or "basefont"
             or "map" or "area"
             or "col" or "colgroup"
             or "frame" or "option" or "optgroup" => true,
        _ => false
    };

    /// <summary>
    /// Recursively shifts every box in a sub-tree by (dx, dy).
    /// Used to centre block children / reflow multicol columns after their
    /// final geometry is known.
    /// FIX: iterative — the recursive version overflowed the stack on
    /// pathologically deep DOM trees.
    /// </summary>
    private static void OffsetBoxTree(LayoutBox box, float dx, float dy)
    {
        if (dx == 0f && dy == 0f) return;

        var stack = new Stack<LayoutBox>();
        stack.Push(box);
        while (stack.Count > 0)
        {
            var b = stack.Pop();
            b.X += dx;
            b.Y += dy;
            var kids = b.Children;
            for (int i = 0; i < kids.Count; i++)
                stack.Push(kids[i]);
        }
    }

    private static LayoutBox MakeRootBox(DomElement? elem, float w, float h)
        => new LayoutBox(elem, BoxType.Block)
        {
            X = 0, Y = 0, Width = w, Height = h,
            ViewportWidth = w, ViewportHeight = h
        };

    /// <summary>
    /// Grows the root box to cover the full document extents (any box that
    /// hangs past the right/bottom of the viewport — wide PRE lines, oversized
    /// tables, floats, oversized inline frames).  The shell maps that
    /// overflow to scrollbars instead of squeezing the layout, which is how
    /// 1996 engines behaved.
    /// </summary>
    private static void ExpandRoot(LayoutBox root, float viewportWidth, float viewportHeight)
    {
        float maxRight = viewportWidth;
        float maxBottom = viewportHeight;

        foreach (var b in root.Descendants())
        {
            var mr = b.MarginRect;
            float scrollableRight = b.ScrollableMarginRight;
            if (scrollableRight > maxRight) maxRight = scrollableRight;
            if (mr.Bottom > maxBottom) maxBottom = mr.Bottom;
        }

        // The document viewport is screen-width constrained. Descendants may
        // paint beyond it, but that overflow is clipped by the canvas rather
        // than turning the whole page into a wider horizontal scroll surface.
        root.Width = Math.Max(viewportWidth, maxRight);
        root.Height = Math.Max(viewportHeight, maxBottom);
    }

    /// <summary>
    /// Collapses runs of ASCII whitespace to a single space.  The result is
    /// NOT trimmed: the single space between adjacent inline elements is the
    /// word separator and must survive.  Whitespace-only nodes are kept here —
    /// the decision to drop them belongs to the inline-run flush, which
    /// knows whether real content surrounds them.
    /// </summary>
    private static float CollapseMargins(float first, float second)
    {
        if (first >= 0f && second >= 0f)
            return Math.Max(first, second);
        if (first <= 0f && second <= 0f)
            return Math.Min(first, second);
        return first + second;
    }

    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        bool inWs = false;

        foreach (char c in s)
        {
            if (IsAsciiWhitespace(c))
            {
                if (!inWs) { sb.Append(' '); inWs = true; }
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }

        return sb.ToString();
    }
}