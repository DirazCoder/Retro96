// Step 4 unit tests — CSS parser, cascade, inheritance, colour parsing.
using Retro96.Drawing;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Network;

namespace RetroTests;

public class CssParserTests
{
    private static DomDocument ParseAndResolve(string bodyHtml, string css = "")
    {
        var doc = HtmlParser.Parse(
            $"<html><head><style>{css}</style></head><body>{bodyHtml}</body></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        StyleResolver.Resolve(doc, 800);
        return doc;
    }

    [Fact]
    public void VisitedPseudoClassUsesSessionHistoryAndStillAllowsHoverToWin()
    {
        const string css = "a:link { color: #0000EE; } a:visited { color: #551A8B; } a:hover { color: #FF0000; }";
        var doc = ParseAndResolve("<a id='go' href='#anchor-alpha'>alpha</a>", css);
        var link = doc.AllTags("a")[0];
        var visited = CssSelector.ParseSelector("a:visited")[0];
        var unvisited = CssSelector.ParseSelector("a:link")[0];

        Check.That(!visited.Matches(link), "fresh href is not visited");
        Check.That(unvisited.Matches(link), "fresh href matches :link");

        doc.VisitedUrls.Add("http://x.test/#anchor-alpha");
        StyleResolver.Resolve(doc, 800);
        Check.That(visited.Matches(link), "session history makes the fragment :visited");
        Check.That(!unvisited.Matches(link), "visited href no longer matches :link");
        Check.That(link.Style != null && link.Style.Color == Retro96.Drawing.Color.FromArgb(0x55, 0x1A, 0x8B),
            "a:visited supplies the purple author color", link.Style?.Color.ToString() ?? "(no style)");

        doc.HoveredElement = link;
        StyleResolver.Resolve(doc, 800);
        Check.That(link.Style != null && link.Style.Color == Retro96.Drawing.Color.FromArgb(0xFF, 0x00, 0x00),
            "a:hover still outranks a:visited while hovering", link.Style?.Color.ToString() ?? "(no style)");
        Check.Done();
    }

    [Fact]
    public void QuotedLegacyLinkColorsDoNotSuppressBodyLinkColor()
    {
        var doc = HtmlParser.Parse(
            "<html><head><style>A:link {color:\"#003399\";} " +
            "A:visited {color:\"#003399\";} A:hover {color:\"red\";}</style></head>" +
            "<body link=\"#003399\" vlink=\"#003399\" alink=\"#003399\">" +
            "<a href=\"/target\">target</a></body></html>",
            ParsedUrl.Parse("http://example.test/"), new CookieStore());
        StyleResolver.Resolve(doc);

        var link = doc.AllTags("a").Single();
        Check.That(link.Style?.Color == Color.FromArgb(0x00, 0x33, 0x99),
            "BODY LINK color survives quoted, invalid CSS color values",
            $"resolvedLink={StyleResolver.GetLinkColor(doc)}, bodyLink={doc.BodyLinkColor}, href={link.GetAttr("href")}, " +
            $"ownColor={link.Style?.OwnColor}, color={link.Style?.Color}");
        Check.Done();
    }

    [Fact]
    public void DynamicPseudoClassesUseDocumentInteractionState()
    {
        var doc = ParseAndResolve("<a id='go' href='/x'><span>go</span></a>");
        var link = doc.AllTags("a")[0];
        var child = doc.AllTags("span")[0];
        var hover = CssSelector.ParseSelector("a:hover")[0];
        var active = CssSelector.ParseSelector("a:active")[0];
        var focus = CssSelector.ParseSelector("a:focus")[0];

        doc.HoveredElement = child;
        Check.That(hover.Matches(link), "a:hover follows the hovered descendant");
        doc.ActiveElement = link;
        Check.That(active.Matches(link), "a:active follows document active state");
        doc.ActiveElement = null;
        doc.FocusedElement = link;
        Check.That(focus.Matches(link), "a:focus follows document focus state");
        doc.HoveredElement = null;
        doc.FocusedElement = null;
        Check.That(!hover.Matches(link), "hover clears when document state clears");
        Check.Done();
    }

    [Fact]
    public void HoverRelayoutDetectionRequiresAMatchingAuthorRule()
    {
        var doc = ParseAndResolve(
            "<a id='go' href='/x'><span>go</span></a><div id='other'></div>",
            "#other:hover { display: none; }");
        var link = doc.AllTags("a")[0];
        var child = doc.AllTags("span")[0];
        var other = doc.AllTags("div")[0];

        Check.That(!StyleResolver.HasMatchingHoverRule(doc),
            "no hover target means no dynamic hover rule applies");
        doc.HoveredElement = child;
        Check.That(!StyleResolver.HasMatchingHoverRule(doc),
            "hovering a normal link without an author hover rule does not require restyling");
        doc.HoveredElement = other;
        Check.That(StyleResolver.HasMatchingHoverRule(doc),
            "a matching element hover rule is detected");
        doc.HoveredElement = link;
        Check.That(!StyleResolver.HasMatchingHoverRule(doc),
            "a selector for a different element does not trigger restyling");
        doc.HoveredElement = null;
        Check.That(!StyleResolver.HasMatchingHoverRule(doc),
            "clearing hover removes the dynamic match");
        doc.AllTags("style")[0].Children.OfType<DomText>().First().Data =
            "a:hover { color: red; } #other:hover { color: blue; }";
        StyleResolver.Resolve(doc, 800);
        doc.HoveredElement = child;
        Check.That(StyleResolver.HasMatchingHoverRule(doc),
            "a matching ancestor hover rule is detected for nested link content");
        doc.HoveredElement = other;
        Check.That(StyleResolver.HasMatchingHoverRule(doc),
            "style recalculation refreshes cached hover selectors");
        Check.Done();
    }

    [Fact]
    public void PaintOnlyHoverRulesDoNotRequireDocumentRelayout()
    {
        var doc = ParseAndResolve("<a id='go' href='/x'>go</a>",
            "a:hover { color: red; background-color: #eeeeee; }");
        var link = doc.AllTags("a")[0];
        doc.HoveredElement = link;

        Check.That(StyleResolver.HasMatchingHoverRule(doc),
            "the hovered link matches its author rule");
        Check.That(!StyleResolver.HasLayoutAffectingHoverRule(doc),
            "color and background hover changes can repaint without relayout");

        doc.AllTags("style")[0].Children.OfType<DomText>().First().Data =
            "a:hover { color: red; padding-left: 8px; }";
        StyleResolver.Resolve(doc, 800);
        Check.That(StyleResolver.HasLayoutAffectingHoverRule(doc),
            "a hover rule that changes padding still requires relayout");
        Check.Done();
    }

    [Fact]
    public void IndexedStyleRulesKeepTypeClassIdAndCombinatorMatches()
    {
        var doc = ParseAndResolve(
            "<div class='container'><p id='target' class='shared special'></p><span class='shared'></span></div>",
            "p { color: red; } .shared { font-weight: bold; } " +
            ".container > p { font-size: 22px; } #target { color: blue; } " +
            ".special { background-color: yellow; }");
        var paragraph = doc.AllTags("p")[0].Style!;
        var span = doc.AllTags("span")[0].Style!;

        Check.That(paragraph.Color == Color.FromArgb(0, 0, 255),
            "type and id rule candidates retain the id cascade");
        Check.That(paragraph.FontWeight >= FontWeightValue.Bold &&
                   span.FontWeight >= FontWeightValue.Bold,
            "untyped class rules apply across tag-specific candidate sets");
        Check.That(paragraph.FontSize == 22f,
            "child combinator type candidate applies to its subject");
        Check.That(paragraph.BackgroundColor == Color.FromArgb(255, 255, 0),
            "class candidate remains available beside type-indexed rules");
        Check.Done();
    }

    // ── shorthand expansion ──────────────────────────────────────────

    [Fact]
    public void MarginShorthandExpandsCorrectly()
    {
        var doc = ParseAndResolve("<div id=a style='margin: 10px 20px 30px 40px'></div>");
        var s = doc.AllTags("div")[0].Style!;
        Check.That(s.MarginTop == 10f, "margin shorthand: top", s.MarginTop.ToString());
        Check.That(s.MarginRight == 20f, "margin shorthand: right", s.MarginRight.ToString());
        Check.That(s.MarginBottom == 30f, "margin shorthand: bottom", s.MarginBottom.ToString());
        Check.That(s.MarginLeft == 40f, "margin shorthand: left", s.MarginLeft.ToString());

        doc = ParseAndResolve("<div style='margin: 10px 20px'></div>");
        s = doc.AllTags("div")[0].Style!;
        Check.That(s.MarginTop == 10f && s.MarginBottom == 10f, "2-value margin: vertical");
        Check.That(s.MarginRight == 20f && s.MarginLeft == 20f, "2-value margin: horizontal");

        doc = ParseAndResolve("<div style='margin: 7px'></div>");
        s = doc.AllTags("div")[0].Style!;
        Check.That(s.MarginTop == 7f && s.MarginRight == 7f && s.MarginBottom == 7f && s.MarginLeft == 7f,
            "1-value margin: all sides");
        Check.Done();
    }

    [Fact]
    public void PaddingShorthandExpandsCorrectly()
    {
        var doc = ParseAndResolve("<div style='padding: 1px 2px 3px 4px'></div>");
        var s = doc.AllTags("div")[0].Style!;
        Check.That(s.PaddingTop == 1f, "padding shorthand: top");
        Check.That(s.PaddingRight == 2f, "padding shorthand: right");
        Check.That(s.PaddingBottom == 3f, "padding shorthand: bottom");
        Check.That(s.PaddingLeft == 4f, "padding shorthand: left");
        Check.Done();
    }

    [Fact]
    public void BorderShorthandParses()
    {
        var doc = ParseAndResolve("<div style='border: 2px solid #ff0000'></div>");
        var s = doc.AllTags("div")[0].Style!;
        Check.That(s.BorderTopWidth == 2f, "border shorthand: width", s.BorderTopWidth.ToString());
        Check.That(s.BorderTopColor == Color.FromArgb(255, 0, 0), "border shorthand: colour",
            s.BorderTopColor.ToString());
        Check.Done();
    }

    [Fact]
    public void FontShorthandParses()
    {
        var doc = ParseAndResolve("<div style='font: bold 14px Arial, sans-serif'></div>");
        var s = doc.AllTags("div")[0].Style!;
        Check.That(s.FontSize == 14f, "font shorthand: size", s.FontSize.ToString());
        Check.That(s.FontWeight >= FontWeightValue.Bold, "font shorthand: bold");
        Check.That(s.FontFamily.Count > 0 && s.FontFamily[0].Contains("Arial"),
            "font shorthand: family", string.Join(",", s.FontFamily));
        Check.Done();
    }

    [Fact]
    public void BackgroundShorthandParses()
    {
        var doc = ParseAndResolve("<div style='background: #00ff00'></div>");
        var s = doc.AllTags("div")[0].Style!;
        Check.That(s.BackgroundColor == Color.FromArgb(0, 255, 0), "background shorthand: colour",
            s.BackgroundColor.ToString());

        doc = ParseAndResolve("<div style=\"background: url('x.gif') repeat-x\"></div>");
        s = doc.AllTags("div")[0].Style!;
        Check.That(s.BackgroundImage?.Contains("x.gif") == true, "background shorthand: image",
            s.BackgroundImage ?? "(null)");
        Check.Done();
    }

    // ── cascade ──────────────────────────────────────────────────────

    [Fact]
    public void CascadeOrderInlineBeatsIdBeatsClassBeatsType()
    {
        var doc = ParseAndResolve(
            "<p id='target' class='cls' style='color: #111111'>text</p>",
            "p { color: #ff0000 } " +
            ".cls { color: #00ff00 } " +
            "#target { color: #0000ff }");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(0x11, 0x11, 0x11),
            "inline STYLE= beats id/class/type", s.Color.ToString());

        doc = ParseAndResolve(
            "<p id='target' class='cls'>text</p>",
            "p { color: #ff0000 } .cls { color: #00ff00 } #target { color: #0000ff }");
        s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(0, 0, 255),
            "id selector beats class and type", s.Color.ToString());

        doc = ParseAndResolve(
            "<p class='cls'>text</p>",
            "p { color: #ff0000 } .cls { color: #00ff00 }");
        s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(0, 255, 0),
            "class selector beats type selector", s.Color.ToString());
        Check.Done();
    }

    [Fact]
    public void StylesheetDescendantSelectorsMatchNestedElements()
    {
        var doc = ParseAndResolve(
            "<div class='test-card'><h2 id='heading'>Heading</h2></div>" +
            "<p class='inline-block-test'><span id='inline'>Inline</span></p>" +
            "<div class='block-test'><span id='block'>Block</span></div>",
            ".test-card h2 { color: #0056b3; font-size: 22px; } " +
            ".inline-block-test span { background-color: #bbdefb; padding: 4px; } " +
            ".block-test span { display: block; background-color: #c8e6c9; }");

        var heading = doc.ElementDescendants().First(e => e.GetAttr("id") == "heading").Style!;
        var inline = doc.ElementDescendants().First(e => e.GetAttr("id") == "inline").Style!;
        var block = doc.ElementDescendants().First(e => e.GetAttr("id") == "block").Style!;

        Check.That(heading.Color == Color.FromArgb(0, 0x56, 0xB3), "descendant heading color applies");
        Check.That(heading.FontSize == 22f, "descendant heading size applies");
        Check.That(inline.BackgroundColor == Color.FromArgb(0xBB, 0xDE, 0xFB) && inline.PaddingLeft == 4f,
            "inline span background and padding apply");
        Check.That(block.Display == DisplayValue.Block && block.BackgroundColor == Color.FromArgb(0xC8, 0xE6, 0xC9),
            "block span display and background apply");
        Check.Done();
    }

    [Fact]
    public void CssComplianceUnitsAlignmentListsAndSideBordersResolve()
    {
        var doc = ParseAndResolve(
            "<div id='ex' class='ex'></div><div id='pc' class='pc'></div>" +
            "<div id='in' class='in'></div><div id='cm' class='cm'></div><div id='mm' class='mm'></div>" +
            "<div id='neg-one' style='margin-top:-1px'></div>" +
            "<span id='va-top' class='va-top'></span><span id='va-bottom' class='va-bottom'></span>" +
            "<span id='va-pct' class='va-pct'></span><ol class='roman'><li></li></ol>" +
            "<ol class='alpha'><li></li></ol><div id='groove'></div><div id='none'></div>",
            ".ex { font-size: 2ex } .pc { font-size: 1.5pc } .in { font-size: .25in } " +
            ".cm { font-size: .6cm } .mm { font-size: 6mm } " +
            ".va-top { vertical-align: text-top } .va-bottom { vertical-align: bottom } " +
            ".va-pct { vertical-align: -50% } ol.roman { list-style-type: upper-roman } " +
            "ol.alpha { list-style-type: lower-alpha } " +
            "#groove { border-left: 10px groove #CC9900 } " +
            "#none { border-style: none; border-width: 8px }");

        var byId = doc.ElementDescendants().Where(e => e.GetAttr("id") != null)
            .ToDictionary(e => e.GetAttr("id")!, e => e.Style!);
        Check.That(byId["ex"].FontSize == 16f, "ex resolves against font size");
        Check.That(byId["pc"].FontSize == 24f && byId["in"].FontSize == 24f,
            "pc and inch units resolve to CSS pixels");
        Check.That(Math.Abs(byId["cm"].FontSize - 96f * 0.6f / 2.54f) < 0.1f &&
                   Math.Abs(byId["mm"].FontSize - 96f * 6f / 25.4f) < 0.1f,
            "centimeter and millimeter units resolve to CSS pixels");
        Check.That(byId["neg-one"].MarginTop == -1f,
            "negative one-pixel margin is not confused with an unset value");
        Check.That(byId["va-top"].VerticalAlign == VerticalAlign.TextTop &&
                   byId["va-bottom"].VerticalAlign == VerticalAlign.Bottom,
            "vertical-align keyword values resolve");
        Check.That(byId["va-pct"].VerticalAlignPercent == -50f,
            "vertical-align percentage remains signed");
        Check.That(doc.AllTags("ol")[0].Style!.ListStyleType == ListStyleType.UpperRoman &&
                   doc.AllTags("ol")[1].Style!.ListStyleType == ListStyleType.LowerAlpha,
            "ordered list CSS marker types resolve");
        Check.That(byId["groove"].BorderLeftStyle == BorderStyleValue.Groove &&
                   byId["groove"].BorderLeftWidth == 10f &&
                   byId["groove"].BorderLeftColor == Color.FromArgb(0xCC, 0x99, 0x00),
            "per-side border shorthand keeps groove color and width");
        Check.That(byId["none"].OwnBorderTopStyle &&
                   byId["none"].BorderTopStyle == BorderStyleValue.None &&
                   byId["none"].BorderTopWidth == 0f,
            "explicit border-style none computes a zero border width");
        Check.Done();
    }

    [Fact]
    public void ImportParsesQuotedAndUnquotedUrls()
    {
        var (_, imports) = CssParser.Parse(
            "@import url(\"quoted.css\"); @import url(bare.css);");
        Check.That(imports.Count == 2, "both CSS import declarations are collected", imports.Count.ToString());
        Check.That(imports[0].Url == "quoted.css", "quoted url() import is unwrapped", imports[0].Url);
        Check.That(imports[1].Url == "bare.css", "unquoted url() import is unwrapped", imports[1].Url);
        Check.Done();
    }

    [Fact]
    public void ImportantBeatsCascadeOrder()
    {
        var doc = ParseAndResolve(
            "<p id='target' style='color: #111111'>text</p>",
            "p { color: #ff0000 !important }");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(255, 0, 0),
            "!important beats inline STYLE=", s.Color.ToString());

        doc = ParseAndResolve(
            "<p id='target'>text</p>",
            "#target { color: #0000ff } p { color: #ff0000 !important }");
        s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(255, 0, 0),
            "!important beats higher specificity", s.Color.ToString());
        Check.Done();
    }

    // ── inheritance ──────────────────────────────────────────────────

    [Fact]
    public void InheritedPropertiesPropagateThroughDomDepth()
    {
        var doc = ParseAndResolve(
            "<div style='color: #ff0000; font-size: 20px'><div><div><p id='deep'>text</p></div></div></div>");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(255, 0, 0),
            "color inherits through 3 levels", s.Color.ToString());
        Check.That(Math.Abs(s.FontSize - 20f) < 0.01f,
            "font-size inherits through 3 levels", s.FontSize.ToString());
        Check.Done();
    }

    [Fact]
    public void NonInheritedPropertiesDoNotPropagate()
    {
        var doc = ParseAndResolve(
            "<div style='border: 5px solid #ff0000; background-color: #00ff00'><p id='child'>text</p></div>");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(s.BorderTopWidth == 0f,
            "border does NOT inherit to child", s.BorderTopWidth.ToString());
        Check.That(s.BackgroundColor == Color.Transparent,
            "background does NOT inherit to child", s.BackgroundColor.ToString());
        Check.Done();
    }

    // ── units ────────────────────────────────────────────────────────

    [Fact]
    public void PercentageUnitsResolveAgainstTheRightBase()
    {
        // font-size % resolves against the PARENT font-size
        var doc = ParseAndResolve(
            "<div style='font-size: 32px'><p style='font-size: 50%'>t</p></div>");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(Math.Abs(s.FontSize - 16f) < 0.01f,
            "font-size: 50% of parent 32px → 16px", s.FontSize.ToString());
        Check.Done();
    }

    [Fact]
    public void EmUnitsResolveAgainstCurrentFontSize()
    {
        var doc = ParseAndResolve(
            "<div style='font-size: 16px'><p style='font-size: 1.5em'>t</p></div>");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(Math.Abs(s.FontSize - 24f) < 0.01f,
            "font-size: 1.5em against parent 16px → 24px", s.FontSize.ToString());
        Check.Done();
    }

    // ── colours ──────────────────────────────────────────────────────

    [Fact]
    public void NamedColoursParseToRightRgb()
    {
        var cases = new (string Css, Color Expected)[]
        {
            ("red",   Color.FromArgb(255, 0, 0)),
            ("blue",  Color.FromArgb(0, 0, 255)),
            ("green", Color.FromArgb(0, 128, 0)),
            ("black", Color.FromArgb(0, 0, 0)),
            ("white", Color.FromArgb(255, 255, 255)),
            ("gray",  Color.FromArgb(128, 128, 128)),
            ("navy",  Color.FromArgb(0, 0, 128)),
            ("teal",  Color.FromArgb(0, 128, 128)),
        };
        foreach (var (css, expected) in cases)
        {
            var doc = ParseAndResolve($"<p style='color: {css}'>t</p>");
            var s = doc.AllTags("p")[0].Style!;
            Check.That(s.Color == expected, $"named colour '{css}' → {expected}", s.Color.ToString());
        }
        Check.Done();
    }

    [Fact]
    public void HexColoursParseThreeAndSixDigit()
    {
        var doc = ParseAndResolve("<p style='color: #f00'>t</p>");
        Check.That(doc.AllTags("p")[0].Style!.Color == Color.FromArgb(255, 0, 0),
            "3-digit hex #f00 → red");

        doc = ParseAndResolve("<p style='color: #ff8000'>t</p>");
        Check.That(doc.AllTags("p")[0].Style!.Color == Color.FromArgb(255, 128, 0),
            "6-digit hex #ff8000 → (255,128,0)", doc.AllTags("p")[0].Style!.Color.ToString());
        Check.Done();
    }

    [Fact]
    public void RgbFunctionParses()
    {
        var doc = ParseAndResolve("<p style='color: rgb(12, 34, 56)'>t</p>");
        Check.That(doc.AllTags("p")[0].Style!.Color == Color.FromArgb(12, 34, 56),
            "rgb(12,34,56)", doc.AllTags("p")[0].Style!.Color.ToString());

        doc = ParseAndResolve("<p style='color: rgb(50%, 0%, 100%)'>t</p>");
        Check.That(doc.AllTags("p")[0].Style!.Color == Color.FromArgb(128, 0, 255),
            "rgb(50%,0%,100%) percentages");
        Check.Done();
    }

    // ── font colour attribute (HTML route) ───────────────────────────

    [Fact]
    public void FontColorAttributeApplies()
    {
        var doc = ParseAndResolve("<font color='#008000'>green text</font>");
        var s = doc.AllTags("font")[0].Style!;
        Check.That(s.Color == Color.FromArgb(0, 128, 0),
            "<font color=#008000> applies green", s.Color.ToString());
        Check.Done();
    }

    // ══════════════════════════════════════════════════════════════════
    // CSS2 upgrade (Task 5) — selectors, values, properties, media
    // ══════════════════════════════════════════════════════════════════

    private static ComputedStyle StyleById(DomDocument doc, string id) =>
        doc.ElementDescendants().First(e => e.GetAttr("id") == id).Style!;

    // ── CSS2 selectors ───────────────────────────────────────────────

    [Fact]
    public void FirstChildPseudoClassMatchesFirstElementChild()
    {
        // Text nodes and comments before the <p> must not disqualify it —
        // :first-child counts ELEMENT children only.
        var doc = ParseAndResolve(
            "<div>text<!--c--><p id='one'>a</p><p id='two'>b</p></div>" +
            "<div><em>lead</em><p id='three'>c</p></div>",
            "p:first-child { color: #ff0000 }");
        var one = doc.ElementDescendants().First(e => e.GetAttr("id") == "one");
        var two = doc.ElementDescendants().First(e => e.GetAttr("id") == "two");
        var three = doc.ElementDescendants().First(e => e.GetAttr("id") == "three");
        var sel = CssSelector.ParseSelector("p:first-child")[0];

        Check.That(sel.Matches(one), "first ELEMENT child matches :first-child");
        Check.That(!sel.Matches(two), "later siblings do not match :first-child");
        Check.That(!sel.Matches(three), "an element after an <em> is not the first child");
        Check.That(one.Style!.Color == Color.FromArgb(255, 0, 0),
            ":first-child rule applies its declarations",
            one.Style.Color.ToString());
        Check.That(two.Style!.Color == Color.Black,
            ":first-child rule does not apply to later siblings");
        Check.Done();
    }

    [Fact]
    public void ChildCombinatorOnlyMatchesDirectChildren()
    {
        var doc = ParseAndResolve(
            "<p class='first-demo'><span id='child'>direct</span>" +
            "<span><span id='grandchild'>nested</span></span></p>",
            "p.first-demo > span { color: #CC0000; }");
        var child = doc.AllTags("span").First(e => e.GetAttr("id") == "child");
        var grandchild = doc.AllTags("span").First(e => e.GetAttr("id") == "grandchild");
        var selector = CssSelector.ParseSelector("p.first-demo > span")[0];

        Check.That(selector.Matches(child), "child combinator matches the direct span");
        Check.That(!selector.Matches(grandchild), "child combinator does not match the nested span");
        Check.That(child.Style!.Color == Color.FromArgb(0xCC, 0, 0),
            "direct child receives its rule color");
        // color is inherited normally: a correct child-combinator match on
        // the parent span also gives its nested span the inherited color.
        Check.That(grandchild.Style!.Color == Color.FromArgb(0xCC, 0, 0),
            "nested span inherits color from its direct-span parent");
        Check.Done();
    }

    [Fact]
    public void WhitespaceAttributeSelectorAppliesToOption()
    {
        var doc = ParseAndResolve(
            "<select><option id='beta' value='beta'>beta</option>" +
            "<option id='other' value='alpha gamma'>other</option></select>",
            "option[value~='beta'] { color: #808000; }");
        var beta = doc.AllTags("option").First(e => e.GetAttr("id") == "beta");
        var other = doc.AllTags("option").First(e => e.GetAttr("id") == "other");

        Check.That(beta.Style!.Color == Color.FromArgb(0x80, 0x80, 0),
            "~= rule resolves onto matching option");
        Check.That(other.Style!.Color != Color.FromArgb(0x80, 0x80, 0),
            "~= rule does not match unrelated option value");
        Check.Done();
    }

    [Fact]
    public void LangPseudoClassMatchesOwnAndInheritedLanguage()
    {
        var doc = ParseAndResolve(
            "<div lang='en'><p id='a'>x</p></div>" +
            "<p id='b' lang='fr'>y</p>" +
            "<p id='c' lang='en-US'>z</p>" +
            "<p id='d' lang='enx'>w</p>");
        var a = doc.ElementDescendants().First(e => e.GetAttr("id") == "a");
        var b = doc.ElementDescendants().First(e => e.GetAttr("id") == "b");
        var c = doc.ElementDescendants().First(e => e.GetAttr("id") == "c");
        var d = doc.ElementDescendants().First(e => e.GetAttr("id") == "d");
        var en = CssSelector.ParseSelector("p:lang(en)")[0];
        var fr = CssSelector.ParseSelector("p:lang(FR)")[0];

        Check.That(en.Matches(a), ":lang(en) matches a descendant of lang='en' (inherited)");
        Check.That(!en.Matches(b), ":lang(en) does not match lang='fr'");
        Check.That(en.Matches(c), ":lang(en) prefix-matches lang='en-US'");
        Check.That(!en.Matches(d), ":lang(en) does not match lang='enx' (needs hyphen)");
        Check.That(fr.Matches(b), ":lang(FR) matches lang='fr' case-insensitively");
        Check.Done();
    }

    [Fact]
    public void LinkAndVisitedCoexistWithActive()
    {
        // CSS2 §5.11.2: :link/:visited are no longer mutually exclusive
        // with :active (a CSS1 restriction).
        var doc = ParseAndResolve("<a id='go' href='/x'>go</a>");
        var link = doc.AllTags("a")[0];
        var linkActive = CssSelector.ParseSelector("a:link:active")[0];
        var visitedActive = CssSelector.ParseSelector("a:visited:active")[0];

        doc.ActiveElement = link;
        Check.That(linkActive.Matches(link), ":link and :active co-exist on an unvisited link");
        Check.That(!visitedActive.Matches(link), ":visited:active does not match an unvisited link");

        doc.VisitedUrls.Add("http://x.test/x");
        StyleResolver.Resolve(doc, 800);
        Check.That(visitedActive.Matches(link), ":visited and :active co-exist once visited");
        Check.That(!linkActive.Matches(link), ":link:active no longer matches after visiting");
        doc.ActiveElement = null;
        Check.Done();
    }

    [Fact]
    public void ActiveLinkRuleChangesComputedColorWhenStateIsResolved()
    {
        var doc = ParseAndResolve(
            "<a id='go' href='/x'>go</a>",
            "a:link { color: #0000FF; } a:link:active { color: #FF0000; }");
        var link = doc.AllTags("a")[0];
        Check.That(link.Style!.Color == Color.FromArgb(0, 0, 0xFF),
            "unpressed link uses the link color");

        doc.ActiveElement = link;
        StyleResolver.Resolve(doc, 800);
        Check.That(link.Style!.Color == Color.FromArgb(0xFF, 0, 0),
            "active link resolves its active author color");

        doc.ActiveElement = null;
        StyleResolver.Resolve(doc, 800);
        Check.That(link.Style!.Color == Color.FromArgb(0, 0, 0xFF),
            "releasing the pointer restores the link color");
        Check.Done();
    }

    // ── generated content ────────────────────────────────────────────

    [Fact]
    public void BeforeAndAfterPseudoElementsRouteGeneratedContent()
    {
        var doc = ParseAndResolve("<p id='x'>t</p>",
            "p:before { content: '['; color: #ff0000 } " +
            "p::after { content: ']'; color: #00ff00 }");
        var s = doc.AllTags("p")[0].Style!;

        Check.That(s.GeneratedBefore != null,
            ":before (single colon) declarations land in GeneratedBefore");
        Check.That(s.GeneratedAfter != null,
            "::after (double colon) declarations land in GeneratedAfter");
        Check.That(s.GeneratedBefore!.Content != null &&
                   s.GeneratedBefore.Content.Count == 1 &&
                   s.GeneratedBefore.Content[0] == new ContentToken("string", "["),
            ":before content string token stored");
        Check.That(s.GeneratedBefore.Color == Color.FromArgb(255, 0, 0),
            ":before declarations apply to the generated slot",
            s.GeneratedBefore.Color.ToString());
        Check.That(s.GeneratedAfter!.Content != null &&
                   s.GeneratedAfter.Content[0].Text == "]",
            "::after content string token stored");
        Check.That(s.Content == null,
            "generated content does not leak onto the element's own style");
        Check.Done();
    }

    [Fact]
    public void ContentPropertyParsesStringsAttrsAndCounters()
    {
        var doc = ParseAndResolve(
            "<a id='l' href='/x'>go</a><q id='q'>x</q><span id='n'></span>",
            "a { content: 'link: ' attr(href) } " +
            "q:before { content: open-quote counter(chap, upper-roman) } " +
            "#n { content: none }");
        var l = StyleById(doc, "l");
        var q = StyleById(doc, "q");
        var n = StyleById(doc, "n");

        Check.That(l.Content != null && l.Content.Count == 2 &&
                   l.Content[0] == new ContentToken("string", "link: ") &&
                   l.Content[1] == new ContentToken("attr", "href"),
            "content: 'text' attr(href) parses into string + attr tokens");
        Check.That(q.GeneratedBefore!.Content != null &&
                   q.GeneratedBefore.Content.Count == 2 &&
                   q.GeneratedBefore.Content[0] == new ContentToken("open-quote", "") &&
                   q.GeneratedBefore.Content[1] == new ContentToken("counter", "chap, upper-roman"),
            "quote keywords and counter() survive parsing with their arguments");
        Check.That(n.Content == null, "content: none stores null");
        Check.Done();
    }

    [Fact]
    public void QuotesParseIntoPairs()
    {
        var doc = ParseAndResolve(
            "<p id='q'></p><p id='odd'></p><p id='none'></p>",
            "#q { quotes: '<' '>' '(' ')' } " +
            "#odd { quotes: 'x' 'y' 'z' } " +
            "#none { quotes: none }");
        var q = StyleById(doc, "q");
        var odd = StyleById(doc, "odd");
        var none = StyleById(doc, "none");

        Check.That(q.Quotes != null && q.Quotes.Count == 2 &&
                   q.Quotes[0] == new QuotePair("<", ">") &&
                   q.Quotes[1] == new QuotePair("(", ")"),
            "quotes parses consecutive string pairs");
        Check.That(odd.Quotes == null, "an odd number of quote strings is ignored");
        Check.That(none.Quotes == null, "quotes: none keeps the default");
        Check.Done();
    }

    [Fact]
    public void CounterResetAndIncrementParse()
    {
        var doc = ParseAndResolve(
            "<div id='a'></div><div id='b'></div>",
            "#a { counter-reset: chap 3 item; counter-increment: section } " +
            "#b { counter-reset: none; counter-increment: none }");
        var a = StyleById(doc, "a");
        var b = StyleById(doc, "b");

        Check.That(a.CounterReset != null && a.CounterReset.Count == 2 &&
                   a.CounterReset[0] == new CounterAction("chap", 3) &&
                   a.CounterReset[1] == new CounterAction("item", 0),
            "counter-reset parses names with default 0");
        Check.That(a.CounterIncrement != null && a.CounterIncrement.Count == 1 &&
                   a.CounterIncrement[0] == new CounterAction("section", 1),
            "counter-increment defaults to 1");
        Check.That(b.CounterReset == null && b.CounterIncrement == null,
            "counter-*: none stores null");
        Check.Done();
    }

    // ── 'inherit' ────────────────────────────────────────────────────

    [Fact]
    public void InheritKeywordPullsParentComputedValues()
    {
        var doc = ParseAndResolve(
            "<div style='color: #ff0000; border-top: 5px solid black; margin-left: 30px'>" +
            "<p id='k'>x</p></div>",
            "p { color: inherit; border-top: inherit; margin-left: inherit }");
        var k = StyleById(doc, "k");

        Check.That(k.Color == Color.FromArgb(255, 0, 0),
            "color: inherit routes to the parent's computed colour",
            k.Color.ToString());
        Check.That(k.BorderTopWidth == 5f && k.BorderTopStyle == BorderStyleValue.Solid,
            "border-top: inherit pulls the parent's computed width AND style",
            $"{k.BorderTopWidth}/{k.BorderTopStyle}");
        Check.That(k.MarginLeft == 30f,
            "margin-left: inherit pulls a non-inherited property's parent value",
            k.MarginLeft.ToString());
        Check.Done();
    }

    [Fact]
    public void InheritKeywordWorksAtComputedStyleLevelIncludingShorthands()
    {
        var parent = new ComputedStyle();
        parent.Color = Color.FromArgb(1, 2, 3);
        parent.BorderTopWidth = 5f;
        parent.BorderLeftWidth = 7f;
        parent.MarginTop = 11f;
        parent.MarginRight = 12f;
        parent.MarginBottom = 13f;
        parent.MarginLeft = 14f;

        var child = ComputedStyle.Inherit(parent, new[]
        {
            new CssDeclaration("color", "inherit", false),
            new CssDeclaration("border-top-width", "inherit", false),
            new CssDeclaration("border-left-width", "INHERIT", false),
            new CssDeclaration("margin", "inherit", false),
        });

        Check.That(child.Color == Color.FromArgb(1, 2, 3), "color inherit copies the parent colour");
        Check.That(child.BorderTopWidth == 5f,
            "border-top-width: inherit copies the parent's computed width (non-inherited property)",
            child.BorderTopWidth.ToString());
        Check.That(child.BorderLeftWidth == 7f, "inherit is case-insensitive",
            child.BorderLeftWidth.ToString());
        Check.That(child.MarginTop == 11f && child.MarginRight == 12f &&
                   child.MarginBottom == 13f && child.MarginLeft == 14f,
            "margin: inherit copies all four sides as a unit");
        Check.Done();
    }

    // ── CSS2 size constraints / vertical-align ───────────────────────

    [Fact]
    public void MinMaxWidthHeightParseAndKeepPercentages()
    {
        var doc = ParseAndResolve(
            "<div id='a' style='min-width: 100px; max-width: 50%'></div>" +
            "<div id='b' style='min-height: 20px; max-height: none'></div>" +
            "<div id='c' style='min-width: 25%; max-width: 300px'></div>");
        var a = StyleById(doc, "a");
        var b = StyleById(doc, "b");
        var c = StyleById(doc, "c");

        Check.That(a.MinWidth == 100f, "min-width: 100px parses");
        Check.That(a.MaxWidth == null && a.MaxWidthPercent == 50f,
            "max-width: 50% stays a percentage for layout to resolve");
        Check.That(b.MinHeight == 20f, "min-height: 20px parses");
        Check.That(b.MaxHeight == null, "max-height: none stores no maximum");
        Check.That(c.MinWidthPercent == 25f && c.MinWidth == null,
            "min-width: 25% stays a percentage");
        Check.That(c.MaxWidth == 300f, "max-width: 300px parses");
        Check.Done();
    }

    [Fact]
    public void VerticalAlignAcceptsLengthValues()
    {
        var doc = ParseAndResolve(
            "<span id='up' style='vertical-align: 3px'></span>" +
            "<span id='down' style='vertical-align: -2px'></span>" +
            "<span id='em' style='vertical-align: .25em'></span>" +
            "<span id='kw' style='vertical-align: text-top'></span>");
        var up = StyleById(doc, "up");
        var down = StyleById(doc, "down");
        var em = StyleById(doc, "em");
        var kw = StyleById(doc, "kw");

        Check.That(up.VerticalAlignLength == 3f, "vertical-align: 3px stores a length",
            up.VerticalAlignLength?.ToString() ?? "(null)");
        Check.That(down.VerticalAlignLength == -2f, "negative lengths keep their sign");
        Check.That(Math.Abs(em.VerticalAlignLength!.Value - 4f) < 0.01f,
            "em lengths resolve against the parent font size");
        Check.That(kw.VerticalAlignLength == null && kw.VerticalAlign == VerticalAlign.TextTop,
            "keywords still route to the enum");
        Check.Done();
    }

    // ── cursor / outline ─────────────────────────────────────────────

    [Fact]
    public void CursorParsesKeywordsHandAliasAndUrlLists()
    {
        var doc = ParseAndResolve(
            "<div id='hand' style='cursor: hand'></div>" +
            "<div id='ptr' style='cursor: pointer'></div>" +
            "<div id='url' style='cursor: url(x.cur), pointer'></div>" +
            "<div id='wait' style='cursor: wait'></div>" +
            "<div id='parent' style='cursor: wait'><span id='child'>x</span></div>");
        var hand = StyleById(doc, "hand");
        var ptr = StyleById(doc, "ptr");
        var url = StyleById(doc, "url");
        var wait = StyleById(doc, "wait");
        var child = StyleById(doc, "child");

        Check.That(hand.Cursor == CursorValue.Pointer, "cursor: hand maps to the IE pointer alias");
        Check.That(ptr.Cursor == CursorValue.Pointer, "cursor: pointer parses");
        Check.That(url.CursorUri == "x.cur" && url.Cursor == CursorValue.Pointer,
            "cursor: url(x.cur), pointer keeps the URI and the fallback keyword",
            url.CursorUri ?? "(null)");
        Check.That(wait.Cursor == CursorValue.Wait, "cursor: wait parses");
        Check.That(child.Cursor == CursorValue.Wait,
            "cursor inherits to children (CSS2)");
        Check.Done();
    }

    [Fact]
    public void OutlineShorthandExpands()
    {
        var doc = ParseAndResolve(
            "<div id='o' style='outline: 2px solid red'></div>" +
            "<div id='inv' style='outline-color: invert'></div>" +
            "<div id='w' style='outline-width: thin'></div>");
        var o = StyleById(doc, "o");
        var inv = StyleById(doc, "inv");
        var w = StyleById(doc, "w");

        Check.That(o.OutlineWidth == 2f && o.OutlineStyle == BorderStyleValue.Solid,
            "outline shorthand: width + style");
        Check.That(o.OutlineColor == Color.FromArgb(255, 0, 0) && !o.OutlineColorInvert,
            "outline shorthand: colour switches off the invert default",
            o.OutlineColor.ToString());
        Check.That(inv.OutlineColorInvert, "outline-color: invert keeps the CSS2 initial");
        Check.That(w.OutlineWidth == 1f, "outline-width: thin = 1px");
        Check.Done();
    }

    // ── tables / NS4 aliases ─────────────────────────────────────────

    [Fact]
    public void TablePropertyStorage()
    {
        var doc = ParseAndResolve(
            "<table id='t' style='border-collapse: collapse; border-spacing: 5px 10px; " +
            "table-layout: fixed; caption-side: bottom; empty-cells: hide'></table>" +
            "<table id='u' style='border-spacing: 7px'></table>");
        var t = StyleById(doc, "t");
        var u = StyleById(doc, "u");

        Check.That(t.BorderCollapse == BorderCollapseValue.Collapse, "border-collapse: collapse");
        Check.That(t.BorderSpacingX == 5f && t.BorderSpacingY == 10f,
            "border-spacing takes horizontal + vertical lengths");
        Check.That(u.BorderSpacingX == 7f && u.BorderSpacingY == 7f,
            "single-value border-spacing applies to both axes");
        Check.That(t.TableLayout == TableLayoutValue.Fixed, "table-layout: fixed");
        Check.That(t.CaptionSide == CaptionSideValue.Bottom, "caption-side: bottom");
        Check.That(t.EmptyCells == EmptyCellsValue.Hide, "empty-cells: hide");
        Check.Done();
    }

    [Fact]
    public void LayerBackgroundAliasesStoreIntoBackgroundSlots()
    {
        var doc = ParseAndResolve(
            "<div id='l' style='layer-background-color: #336699; " +
            "layer-background-image: url(bg.gif)'></div>");
        var l = StyleById(doc, "l");

        Check.That(l.BackgroundColor == Color.FromArgb(0x33, 0x66, 0x99),
            "layer-background-color aliases background-color (NS4)",
            l.BackgroundColor.ToString());
        Check.That(l.BackgroundImage == "bg.gif",
            "layer-background-image aliases background-image",
            l.BackgroundImage ?? "(null)");
        Check.Done();
    }

    // ── media ────────────────────────────────────────────────────────

    [Fact]
    public void MediaAtRuleHonoursCommaListsAndSkipsPrint()
    {
        var doc = ParseAndResolve("<p id='a'>x</p><p id='b'>y</p>",
            "@media screen, print { #a { color: #ff0000 } } " +
            "@media print { #b { color: #0000ff } }");
        var a = StyleById(doc, "a");
        var b = StyleById(doc, "b");

        Check.That(a.Color == Color.FromArgb(255, 0, 0),
            "@media 'screen, print' applies on a screen renderer",
            a.Color.ToString());
        Check.That(b.Color == Color.Black,
            "@media print rules are parsed but not applied on screen",
            b.Color.ToString());
        Check.Done();
    }

    [Fact]
    public void StyleMediaAttributeFiltersSheets()
    {
        var doc = HtmlParser.Parse(
            "<html><head>" +
            "<style media='print'>p { color: #0000ff }</style>" +
            "<style media='screen, print'>p { color: #ff0000 }</style>" +
            "</head><body><p id='p'>x</p></body></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        StyleResolver.Resolve(doc, 800);
        var p = doc.AllTags("p")[0];

        Check.That(p.Style!.Color == Color.FromArgb(255, 0, 0),
            "<style media> non-screen sheets are skipped, screen sheets apply",
            p.Style.Color.ToString());
        Check.Done();
    }

    [Fact]
    public void ImportMediaDescriptorIsSurfaced()
    {
        var (_, imports) = CssParser.Parse(
            "@import url(a.css) screen, print; " +
            "@import 'b.css' print; " +
            "@import url(c.css);");

        Check.That(imports.Count == 3, "all three imports collected", imports.Count.ToString());
        Check.That(imports[0].Media != null && imports[0].Media.Count == 2 &&
                   imports[0].Media[0] == "screen" && imports[0].Media[1] == "print",
            "the media descriptor after the import URL is captured");
        Check.That(imports[0].AppliesTo("screen") && imports[0].AppliesTo("print"),
            "a screen,print import applies to screen");
        Check.That(!imports[1].AppliesTo("screen") && imports[1].AppliesTo("print"),
            "a print-only import does not apply to screen");
        Check.That(imports[1].Url == "b.css", "quoted import URL still parses", imports[1].Url);
        Check.That(imports[2].Media == null && imports[2].AppliesTo("screen"),
            "an import without a media descriptor applies to all media");
        Check.Done();
    }

    // ── specificity / colours ────────────────────────────────────────

    [Fact]
    public void SpecificityCountsAttributesPseudoClassesAndPseudoElements()
    {
        Check.That(CssSelector.ParseSelector("div > p + span")[0].Specificity == (0, 0, 3),
            "child/adjacent combinators count types only");
        Check.That(CssSelector.ParseSelector(".cls:hover")[0].Specificity == (0, 2, 0),
            "class + pseudo-class land in the (c) bucket");
        Check.That(CssSelector.ParseSelector("#id[rel]")[0].Specificity == (1, 1, 0),
            "attribute selectors land in the (c) bucket");
        Check.That(CssSelector.ParseSelector("*")[0].Specificity == (0, 0, 0),
            "the universal selector counts nothing");
        Check.That(CssSelector.ParseSelector("p:first-child")[0].Specificity == (0, 1, 1),
            ":first-child counts as a pseudo-class");
        Check.That(CssSelector.ParseSelector("a:lang(en)")[0].Specificity == (0, 1, 1),
            ":lang() counts as a pseudo-class");
        Check.That(CssSelector.ParseSelector("p::before")[0].Specificity == (0, 0, 2),
            ":before counts as a pseudo-ELEMENT (d bucket)");

        var doc = ParseAndResolve("<a id='l' href='/x'>go</a>",
            "a { color: #0000ff } [href] { color: #ff0000 }");
        var l = doc.AllTags("a")[0];
        Check.That(l.Style!.Color == Color.FromArgb(255, 0, 0),
            "an attribute selector (c=1) outranks a bare type selector (d=1)",
            l.Style.Color.ToString());
        Check.Done();
    }

    [Fact]
    public void RgbValuesAreClampedInCss2()
    {
        var doc = ParseAndResolve("<p style='color: rgb(300, -20, 150%)'>t</p>");
        var s = doc.AllTags("p")[0].Style!;
        Check.That(s.Color == Color.FromArgb(255, 0, 255),
            "rgb() integers and percentages clamp to the 0-255 range",
            s.Color.ToString());
        Check.Done();
    }

    // ── display / fonts / bidi / raw storage ─────────────────────────

    [Fact]
    public void DisplayAcceptsCss2ValuesAndFallsBackToInline()
    {
        var doc = ParseAndResolve(
            "<div id='it' style='display: inline-table'></div>" +
            "<div id='ri' style='display: run-in'></div>" +
            "<div id='cp' style='display: compact'></div>" +
            "<div id='bad' style='display: frobnicate'></div>");
        var it = StyleById(doc, "it");
        var ri = StyleById(doc, "ri");
        var cp = StyleById(doc, "cp");
        var bad = StyleById(doc, "bad");

        Check.That(it.Display == DisplayValue.InlineTable, "display: inline-table parses");
        Check.That(ri.Display == DisplayValue.RunIn, "display: run-in parses");
        Check.That(cp.Display == DisplayValue.Compact, "display: compact parses");
        Check.That(bad.Display == DisplayValue.Inline,
            "unknown display values fall back to the CSS2 initial value (inline)");
        Check.Done();
    }

    [Fact]
    public void SystemFontsResolveToPlausibleValues()
    {
        var doc = ParseAndResolve(
            "<div id='cap' style='font: caption'></div>" +
            "<div id='mb' style='font: message-box'></div>");
        var cap = StyleById(doc, "cap");
        var mb = StyleById(doc, "mb");

        Check.That(cap.FontSize == 13f, "font: caption maps to a small control size",
            cap.FontSize.ToString());
        Check.That(cap.FontFamily.Count > 0 && cap.FontFamily[0] == "MS Sans Serif",
            "system fonts map to the era UI family", string.Join(",", cap.FontFamily));
        Check.That(cap.FontStyle == FontStyleValue.Normal &&
                   cap.FontWeight == FontWeightValue.W400,
            "system fonts map to normal style/weight");
        Check.That(mb.FontSize == 14f, "font: message-box is one step up",
            mb.FontSize.ToString());

        // The ComputedStyle re-handling path (belt and braces).
        var direct = new ComputedStyle();
        direct.Apply(new CssDeclaration("font", "small-caption", false), 16f, 800f);
        Check.That(direct.FontSize == 11f,
            "font: small-caption resolves through ComputedStyle.ParseFontShorthand too",
            direct.FontSize.ToString());
        Check.Done();
    }

    [Fact]
    public void FontSizeAdjustAndFontStretchStore()
    {
        var doc = ParseAndResolve(
            "<div id='a' style='font-size-adjust: 0.58; font-stretch: condensed; " +
            "font-variant: small-caps'></div>" +
            "<div id='b' style='font-size-adjust: none; font-stretch: frob'></div>");
        var a = StyleById(doc, "a");
        var b = StyleById(doc, "b");

        Check.That(Math.Abs(a.FontSizeAdjust!.Value - 0.58f) < 0.001f, "font-size-adjust number");
        Check.That(a.FontStretch == "condensed", "font-stretch keyword");
        Check.That(a.FontVariant == FontVariantValue.SmallCaps, "font-variant: small-caps still parses");
        Check.That(b.FontSizeAdjust == null, "font-size-adjust: none");
        Check.That(b.FontStretch == null, "unknown font-stretch values are ignored");
        Check.Done();
    }

    [Fact]
    public void DirectionUnicodeBidiAndTextShadowStore()
    {
        var doc = ParseAndResolve(
            "<bdo id='b' dir='rtl' style='direction: rtl; unicode-bidi: bidi-override'></bdo>" +
            "<p id='t' style='text-shadow: 2px 2px #808080'></p>" +
            "<p id='n' style='text-shadow: none'></p>");
        var b = StyleById(doc, "b");
        var t = StyleById(doc, "t");
        var n = StyleById(doc, "n");

        Check.That(b.Direction == DirectionValue.Rtl, "direction: rtl parses");
        Check.That(b.UnicodeBidi == "bidi-override", "unicode-bidi stores its keyword");
        Check.That(t.TextShadow == "2px 2px #808080",
            "text-shadow stores the raw value (parse-level only)",
            t.TextShadow ?? "(null)");
        Check.That(n.TextShadow == null, "text-shadow: none stores null");
        Check.Done();
    }

    // ── modern selector extras kept for the union mode ───────────────

    [Fact]
    public void Beyond1999SelectorsDoNotRegress()
    {
        // ^= $= *= ~= |= attribute operators and the ~ sibling combinator
        // are beyond 1999, but the union mode (Retro96 persona) wants them.
        var doc = ParseAndResolve(
            "<a id='one' href='http://x.test/page.htm'>a</a>" +
            "<a id='two' href='/other'>b</a>" +
            "<div><span id='s1'>1</span><em>x</em><span id='s2'>2</span><span id='s3'>3</span></div>",
            "[href^='http'] { color: #ff0000 } " +
            "[href$='.htm'] { text-decoration: underline } " +
            "[href*='oth'] { font-weight: bold } " +
            "span ~ span { background-color: #00ff00 }");

        var one = doc.AllTags("a")[0];
        var two = doc.AllTags("a")[1];
        var s1 = StyleById(doc, "s1");
        var s2 = StyleById(doc, "s2");
        var s3 = StyleById(doc, "s3");

        Check.That(one.Style!.Color == Color.FromArgb(255, 0, 0),
            "attribute prefix ^= still matches");
        Check.That(one.Style.TextDecoration.HasFlag(TextDecoration.Underline),
            "attribute suffix $= still matches");
        Check.That(two.Style!.FontWeight >= FontWeightValue.Bold,
            "attribute substring *= still matches");
        Check.That(s1.BackgroundColor == Color.Transparent,
            "the ~ combinator needs an earlier sibling");
        Check.That(s2.BackgroundColor == Color.FromArgb(0, 255, 0) &&
                   s3.BackgroundColor == Color.FromArgb(0, 255, 0),
            "the general sibling ~ combinator still matches later siblings");
        Check.Done();
    }

    [Fact]
    public void InlineStyleInheritShorthandWorks()
    {
        // Inline STYLE= goes through the same parse-time expansion, so the
        // inherit passthrough must survive ParseInlineStyle too.
        var doc = ParseAndResolve(
            "<div style='padding: 4px 8px'><p id='k' style='padding: inherit'>x</p></div>");
        var k = StyleById(doc, "k");

        Check.That(k.PaddingTop == 4f && k.PaddingRight == 8f &&
                   k.PaddingBottom == 4f && k.PaddingLeft == 8f,
            "padding: inherit via inline STYLE= copies all four sides");
        Check.Done();
    }

    [Fact]
    public void UniversalAndBarePseudoClassSelectorsKeepMatching()
    {
        var doc = ParseAndResolve(
            "<div><p id='one'>a</p><p id='two'>b</p></div>",
            "*:first-child { color: #ff0000 } :first-child { text-decoration: underline }");
        var one = StyleById(doc, "one");
        var two = StyleById(doc, "two");

        Check.That(one.Color == Color.FromArgb(255, 0, 0) &&
                   one.TextDecoration.HasFlag(TextDecoration.Underline),
            "*:first-child and bare :first-child both match the first element");
        // NB: the wrapping <div> is itself body's first child, so its red
        // colour legitimately INHERITS into the second <p> — only the
        // non-inherited declaration proves the rule did not match it.
        Check.That(two.TextDecoration == TextDecoration.None,
            "later siblings match neither form");
        Check.Done();
    }
}
