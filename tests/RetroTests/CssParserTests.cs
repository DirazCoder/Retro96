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
}
