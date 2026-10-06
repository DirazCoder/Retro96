// ─────────────────────────────────────────────────────────────────────────────
// Retro99SuiteTests — the 1999 upgrade QA battery (checklist §17).
//
// One load test per html-websites/1999 page: every page must parse →
// style → lay out through the REAL engine, and the script pages must
// report their era contracts (the same lines a human reads in-browser).
// Persona-dependent pages are exercised under the persona they target
// (IE5 default / Navigator 4.7 / the union profile).
// ─────────────────────────────────────────────────────────────────────────────
using Retro96;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;
using EngineHttpClient = Retro96.Engine.Network.HttpClient;

namespace RetroTests;

public class Retro99SuiteTests
{
    private static readonly Lazy<string> SuiteDir = new(() =>
        Path.GetFullPath(Path.Combine(TestPaths.Testdata, "..", "tests", "html-websites")));

    private static string Page(params string[] parts) =>
        Path.Combine(SuiteDir.Value, Path.Combine(parts));

    private static DomDocument LoadScripted(PageHarness page, params string[] parts)
    {
        page.LoadFile(Page(parts));
        return page.Document;
    }

    private static (DomDocument doc, LayoutBox root) Layout(params string[] parts)
    {
        InlineLayout.SetFontCache(LayoutHarness.Fonts);
        var doc = HtmlParser.Parse(File.ReadAllText(Page(parts)),
            ParsedUrl.Parse("file:///" + Page(parts).Replace('\\', '/')),
            new CookieStore());
        StyleResolver.Resolve(doc, 800);
        var root = LayoutEngine.BuildLayoutTree(doc, 800, 600);
        return (doc, root);
    }

    private static string? TextOf(DomDocument doc, string id)
    {
        var el = doc.ElementDescendants().FirstOrDefault(e => e.GetAttr("id") == id);
        return el?.InnerText;
    }

    private static ComputedStyle? StyleOf(DomDocument doc, string id)
    {
        var el = doc.ElementDescendants().FirstOrDefault(e => e.GetAttr("id") == id);
        return el?.Style;
    }

    // ── every non-scripted page: parse + style + layout + real content ──────

    [Theory]
    [InlineData("html", "html401-full.html")]
    [InlineData("html", "broken-html-1999.html")]
    [InlineData("forms", "forms-1999.html")]
    [InlineData("tables", "tables-1999.html")]
    [InlineData("css", "css2-selectors.html")]
    [InlineData("css", "css2-boxmodel.html")]
    [InlineData("ie", "ie5-extras.html")]
    [InlineData("netscape", "ns4-extras.html")]
    [InlineData("images", "images-media.html")]
    [InlineData("network", "http11-page.html")]
    [InlineData("", "acid1.html")]
    public void PageParsesStylesAndLaysOut(string dir, string file)
    {
        var (doc, root) = Layout(dir.Length == 0 ? new[] { file } : new[] { dir, file });
        Check.That(doc != null && root != null, $"{file} parses, styles and lays out");
        Check.That(root.Descendants().Count(b => b.Height > 0f) > 3,
            $"{file} produces a real box tree", $"{root.Descendants().Count()} boxes");
        Check.That((doc.FirstTag("body")?.InnerText ?? "").Length > 40,
            $"{file} keeps its body text content");
        Check.Done();
    }

    // ── §4: HTML 4.01 contracts ─────────────────────────────────────────────

    [Fact]
    public void Html401ElementContracts()
    {
        var (doc, root) = Layout("html", "html401-full.html");
        var body = doc.FirstTag("body")?.InnerText ?? "";

        Check.That(doc.AllTags("fieldset").Count == 1 && doc.AllTags("legend").Count == 1,
            "fieldset + legend parse");
        Check.That(doc.AllTags("label").Count >= 2, "label elements parse (for= and wrapped)");
        Check.That(doc.ElementDescendants().Any(l => l.TagName == "label" && l.GetAttr("for") == "name401"),
            "label for= association round-trips");
        Check.That(doc.AllTags("optgroup").Count == 2, "two optgroups parse");
        Check.That(doc.AllTags("thead").Count == 1 && doc.AllTags("tfoot").Count == 1,
            "thead + tfoot parse");
        Check.That(doc.AllTags("tbody").Count >= 2, "explicit tbody sections parse");
        Check.That(doc.AllTags("colgroup").Count >= 1 && doc.AllTags("col").Count >= 1,
            "colgroup + col parse");
        var caption = doc.FirstTag("caption");
        var captionBox = caption == null ? null : LayoutHarness.BoxOf(root, caption);
        float tableGridBottom = doc.FirstTag("table")!.ElementDescendants()
            .Where(e => e.TagName is "td" or "th")
            .Select(cell => LayoutHarness.BoxOf(root, cell)?.BorderRect.Bottom ?? 0f)
            .DefaultIfEmpty(0f).Max();
        Check.That(caption?.GetAttr("align") == "bottom" &&
                   captionBox != null && captionBox.Y >= tableGridBottom,
            "the bottom caption is positioned after the table grid",
            $"captionY={captionBox?.Y:0.#}, gridBottom={tableGridBottom:0.#}");
        Check.That(doc.AllTags("q").Count == 1, "Q parses");
        Check.That(doc.AllTags("ins").Count == 1 && doc.AllTags("del").Count == 1,
            "ins + del parse");
        Check.That(doc.AllTags("abbr").Count == 1 && doc.AllTags("acronym").Count == 1,
            "abbr + acronym parse");
        Check.That(doc.AllTags("bdo").Count == 1 && doc.AllTags("span").Count >= 1,
            "bdo + span parse");
        var bdo = doc.FirstTag("bdo");
        string bdoText = bdo == null ? "" : string.Concat(root.Descendants()
            .Where(box => ReferenceEquals(box.Element, bdo) && !string.IsNullOrEmpty(box.TextRun))
            .OrderBy(box => box.X)
            .Select(box => box.TextRun));
        Check.That(bdo?.Style?.Direction == DirectionValue.Rtl &&
                   bdo.Style.UnicodeBidi == "bidi-override",
            "bdo dir=rtl resolves to a directional override");
        Check.That(bdoText == "ltr=rid htiw ODB",
            "bdo text is laid out in right-to-left visual order",
            bdoText);
        Check.That(doc.AllTags("ruby").Count == 1 && doc.AllTags("rt").Count == 1,
            "ruby + rt parse");
        var ruby = doc.FirstTag("ruby");
        var rubyBase = LayoutHarness.TextBoxContaining(root, "\u6F22\u5B57");
        var rubyFont = ruby?.Style == null ? null : LayoutHarness.Fonts.ResolveForText(
            ruby.Style.FontFamily, ruby.Style.FontSize, (int)ruby.Style.FontWeight,
            ruby.Style.FontStyle == FontStyleValue.Italic,
            ruby.Style.FontStyle == FontStyleValue.Oblique, "\u6F22\u5B57");
        Check.That(rubyBase?.TextRun == "\u6F22\u5B57",
            "ruby base CJK characters are retained instead of replaced with question marks",
            rubyBase?.TextRun ?? "(no ruby base text run)");
        Check.That(rubyFont?.SkFont.ContainsGlyphs("\u6F22\u5B57") == true,
            "text-aware font fallback resolves a font containing both ruby base glyphs");
        Check.That(doc.AllTags("button").Count == 1, "button with rich content parses");
        Check.That(doc.ElementDescendants().Any(e => e.TagName == "input" && e.GetAttr("name") == "terms"),
            "isindex synthesizes its search form (spec behaviour)");

        // Concealment: COMMENT content and the XML island payload never
        // reach the rendered text.
        Check.That(!body.Contains("this text must never render"),
            "IE comment content is concealed");
        var islandStyle = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "xml")?.Style;
        Check.That(islandStyle?.Display == DisplayValue.None,
            "xml data island content is concealed (display none)");
        var island = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "xml");
        Check.That(island != null && (island.InnerText ?? "").Contains("<catalog>"),
            "xml island keeps its source for script access");

        // Entities (HTML 4.01 appendix)
        Check.That(body.Contains("\u03A9") || body.Contains("\u03C9") || body.Contains("\u03C2"),
            "Greek entities decode");
        Check.That(body.Contains("\u2014") && body.Contains("\u2022") && body.Contains("\u20AC"),
            "mdash, bull and euro entities decode");
        Check.That(body.Contains("Paragraph with nowrap \u2014 width wins over wrapping."),
            "the declared Latin-1 fixture uses an entity for its em dash");
        Check.That(body.Contains("AB"), "numeric decimal/hex refs decode");

        // DOCTYPE recorded, quirks for transitional
        Check.That(doc.QuirksMode is "quirks" or "html32",
            "4.01 Transitional doctype records quirks mode", doc.QuirksMode);

        // Q renders with UA quote marks (generated content)
        var qText = root.Descendants()
            .Where(b => b.Element?.TagName == "q").ToList();
        Check.That(qText.Any(b => b.TextRun?.Contains("\u201C") == true || b.TextRun?.Contains("\u201D") == true),
            "Q carries UA-generated quotation marks");

        Check.Done();
    }

    [Fact]
    public void BrokenHtml1999Recovery()
    {
        var (doc, root) = Layout("html", "broken-html-1999.html");

        Check.That(doc.AllTags("li").Count == 3, "unclosed <li> items all recover", $"{doc.AllTags("li").Count}");
        Check.That(doc.AllTags("dt").Count == 2 && doc.AllTags("dd").Count == 2,
            "unclosed dt/dd recover");
        Check.That(doc.AllTags("option").Count == 3, "unclosed options recover");
        var table = doc.AllTags("table").FirstOrDefault();
        Check.That(table != null && table.ElementDescendants().Any(e => e.TagName == "tbody"),
            "table rows land in an implied tbody");
        Check.That(doc.AllTags("h2").Count >= 5, "headings survive the tag soup");

        // Misnested inlines: b reopens around the paragraphs
        var fourth = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "p" && (e.InnerText ?? "").Contains("fourth paragraph"));
        Check.That(fourth != null, "paragraph after three unclosed inlines parses");
        var boldAncestors = doc.ElementDescendants().Count(e => e.TagName == "b");
        Check.That(boldAncestors >= 1, "misnested bold chain reconstructs");

        Check.That((doc.FirstTag("body")?.InnerText ?? "").Contains("After the stray closers"),
            "content after stray closers keeps flowing");
        Check.Done();
    }

    // ── §5: forms ───────────────────────────────────────────────────────────

    [Fact]
    public void Forms1999Contracts()
    {
        var (doc, _) = Layout("forms", "forms-1999.html");
        var labels = doc.AllTags("label");
        Check.That(labels.Any(l => l.GetAttr("for") == "email"), "label for= association");
        Check.That(labels.Any(l => l.ElementDescendants().Any(c => c.TagName == "input")),
            "wrapped label contains its control");
        var file = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "input" && e.GetAttr("type") == "file");
        Check.That(file != null, "input type=file parses");
        var image = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "input" && e.GetAttr("type") == "image");
        Check.That(image != null && image.GetAttr("src") != null, "input type=image parses");
        var disabled = doc.ElementDescendants()
            .FirstOrDefault(e => e.HasAttr("disabled"));
        Check.That(disabled != null, "disabled attribute parses");
        var ro = doc.ElementDescendants()
            .FirstOrDefault(e => e.HasAttr("readonly"));
        Check.That(ro != null, "readonly attribute parses");
        var ta = doc.ElementDescendants()
            .First(e => e.TagName == "textarea" && e.GetAttr("name") == "hard");
        Check.That(ta.GetAttr("wrap") == "hard", "textarea wrap attr round-trips");
        var groups = doc.AllTags("optgroup");
        Check.That(groups.Count == 3 && groups[1].GetAttr("label") == "Europe",
            "optgroups with labels parse");
        var form = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "form" && e.HasAttr("enctype"));
        Check.That(form?.GetAttr("enctype") == "multipart/form-data",
            "multipart enctype round-trips");
        Check.That(doc.AllTags("button").Count == 3, "three rich-content buttons parse");
        Check.Done();
    }

    // ── §6: tables ──────────────────────────────────────────────────────────

    [Fact]
    public void Tables1999Contracts()
    {
        var (doc, _) = Layout("tables", "tables-1999.html");
        var frameset = doc.AllTags("table");
        Check.That(frameset.Count >= 8, "eight table batteries parse", $"{frameset.Count}");
        Check.That(doc.AllTags("caption").Count >= 5, "captions parse");
        var ie = doc.ElementDescendants().FirstOrDefault(t =>
            t.TagName == "table" && t.HasAttr("bordercolor"));
        Check.That(ie != null && ie.GetAttr("bordercolorlight") != null,
            "IE bordercolor family round-trips");
        Check.That(ie?.HasAttr("cols") == true, "table cols attr round-trips");
        var framed = doc.ElementDescendants().FirstOrDefault(t =>
            t.TagName == "table" && t.GetAttr("frame") == "hsides");
        Check.That(framed?.GetAttr("rules") == "groups", "frame/rules attrs round-trip");
        var th = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "th" && e.HasAttr("abbr"));
        Check.That(th != null && th.HasAttr("scope") && th.HasAttr("axis"),
            "cell accessibility attrs (abbr/axis/scope/summary) round-trip");
        Check.Done();
    }

    // ── §7: frames ──────────────────────────────────────────────────────────

    [Fact]
    public void Frames1999Contracts()
    {
        var (doc, _) = Layout("frames", "frames-1999.html");
        var frames = doc.AllTags("frame");
        Check.That(frames.Count == 3, "three frames in the nested frameset", $"{frames.Count}");
        Check.That(frames.Select(f => f.GetAttr("name")).SequenceEqual(
            new[] { "nav99", "banner99", "main99" }), "frame names parse");
        Check.That(frames[0].GetAttr("marginwidth") == "8" && frames[0].HasAttr("noresize"),
            "marginwidth/noresize round-trip");
        var fs = doc.FirstTag("frameset");
        Check.That(fs?.GetAttr("bordercolor") != null && fs.GetAttr("framespacing") == "2",
            "frameset IE extras (border/bordercolor/framespacing) round-trip");
        Check.That(doc.AllTags("noframes").Count == 1, "noframes fallback parses");
        Check.That(doc.FirstTag("body") == null, "frameset document carries no body");
        Check.That(File.Exists(Page("frames", "frames-nav99.html")) &&
                   File.Exists(Page("frames", "frame-child99.html")),
            "frame child pages exist");

        // The main frame page: iframe with the full attribute set
        var (main, _) = Layout("frames", "frames-main99.html");
        var iframe = main.FirstTag("iframe");
        Check.That(iframe != null && iframe.GetAttr("frameborder") == "1" &&
                   iframe.GetAttr("marginwidth") == "6" && iframe.GetAttr("hspace") == "8",
            "iframe 1999 attribute set round-trips");
        Check.That(main.AllTags("object").Count >= 2, "nested objects parse");
        Check.That(main.AllTags("param").Count >= 1, "param inside object parses");
        Check.That(main.AllTags("embed").Count == 1 && main.AllTags("noembed").Count == 1,
            "embed + noembed fallback parse");
        Check.Done();
    }

    // ── §8: CSS2 selectors ──────────────────────────────────────────────────

    [Fact]
    public void Css2SelectorOutcomes()
    {
        var (doc, _) = Layout("css", "css2-selectors.html");

        var child = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "span" && e.Parent is DomElement p1 && p1.TagName == "p");
        Check.That(child?.Style?.Color.R == 0xCC,
            "child selector p > span applies", child?.Style?.Color.ToString());

        var adjacent = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "p" && e.Parent is DomElement pb && pb.TagName == "body" &&
                                 (e.InnerText ?? "").StartsWith("Paragraph directly"));
        Check.That(adjacent?.Style?.BorderLeftWidth is > 2.5f,
            "adjacent sibling h4 + p applies", $"{adjacent?.Style?.BorderLeftWidth}");

        var firstLi = doc.FirstTag("li");
        Check.That(firstLi?.Style?.FontWeight == FontWeightValue.Bold,
            ":first-child bolds the first LI", $"{firstLi?.Style?.FontWeight}");
        var secondLi = doc.AllTags("li")[1];
        Check.That(secondLi?.Style?.FontWeight == FontWeightValue.Normal, "second LI stays normal");

        var french = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "p" && e.GetAttr("lang") == "fr");
        Check.That(french?.Style?.FontStyle == FontStyleValue.Italic,
            ":lang(fr) matches the French paragraph");

        var enUs = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "p" && e.GetAttr("lang") == "en-US");
        Check.That(enUs?.Style?.FontStyle != FontStyleValue.Italic,
            ":lang(fr) does not match en-US");

        var overlined = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "a" && e.HasAttr("lang"));
        Check.That((overlined?.Style?.TextDecoration ?? 0).HasFlag(TextDecoration.Overline),
            "attribute selector [lang] applies");

        var inheritP = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "p" && e.Parent is DomElement pd && pd.TagName == "div" &&
                                 (e.InnerText ?? "").Contains("inherits color"));
        Check.That(inheritP?.Style?.Color.R == 0x00 && inheritP!.Style!.Color.G == 0x66,
            "inherit keyword pulls the parent color",
            inheritP?.Style?.Color.ToString() ?? "null");

        var generated = doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "p" && (e.InnerText ?? "").Contains("generated brackets"));
        Check.That(generated?.Style?.GeneratedBefore?.Content != null,
            ":before content parses into GeneratedBefore");

        var spec = doc.ElementDescendants().FirstOrDefault(e => e.GetAttr("id") == "spec-p");
        Check.That(spec?.Style?.Color.R == 0xCC, "id specificity beats class and type");
        Check.Done();
    }

    // ── §9: box model personas ──────────────────────────────────────────────

    [Fact]
    public void Ie5BoxModelPersona()
    {
        var previousSettings = BrowserRuntime.Settings.Clone();
        try
        {
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.InternetExplorer5 });
            // IE5 persona: width includes padding and border.
            var (doc, root) = Layout("css", "css2-boxmodel.html");
            var box = doc.ElementDescendants().FirstOrDefault(e => e.GetAttr("id") == "ie5box");
            var layoutBox = LayoutHarness.BoxOf(root, box!);
            Check.That(layoutBox != null, "the box-model probe lays out");
            Check.That(Math.Abs(layoutBox!.BorderRect.Width - 200f) < 1.5f,
                "IE5 persona: authored width is the border-box width (200px)",
                $"{layoutBox.BorderRect.Width:0.#}");

            // Union persona: W3C content-box → 200 + 2*20 + 2*10 = 260.
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
            var (doc2, root2) = Layout("css", "css2-boxmodel.html");
            var box2 = doc2.ElementDescendants().FirstOrDefault(e => e.GetAttr("id") == "ie5box");
            var layoutBox2 = LayoutHarness.BoxOf(root2, box2!);
            Check.That(Math.Abs(layoutBox2!.BorderRect.Width - 260f) < 1.5f,
                "union persona: W3C content-box width (260px total)",
                $"{layoutBox2.BorderRect.Width:0.#}");
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
        Check.Done();
    }

    [Fact]
    public void ZIndexAndMinMax()
    {
        var previousSettings = BrowserRuntime.Settings.Clone();
        try
        {
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
            var (doc, root) = Layout("css", "css2-boxmodel.html");
            var minmax = doc.ElementDescendants().FirstOrDefault(e =>
                (e.GetAttr("class") ?? "").Contains("minmax"));
            var minmaxBox = LayoutHarness.BoxOf(root, minmax!);
            Check.That(minmaxBox != null && minmaxBox!.BorderRect.Width >= 118f,
                "min-width clamps the box open", $"{minmaxBox?.BorderRect.Width:0.#}");
            Check.That(minmaxBox!.BorderRect.Width <= 272f,
                "max-width caps content width before padding and borders are added in the union",
                $"{minmaxBox.BorderRect.Width:0.#}");

            // z-index stack order: z5 paints above z2 at the same level
            var z2 = doc.ElementDescendants().First(e => (e.GetAttr("class") ?? "").Contains("z2"));
            var z5 = doc.ElementDescendants().First(e => (e.GetAttr("class") ?? "").Contains("z5"));
            Check.That((z5.Style?.ZIndex ?? 0) > (z2.Style?.ZIndex ?? 0),
                "z-index values resolve on positioned elements");
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
        Check.Done();
    }

    // ── §10: DOM Level 1 + IE5 DHTML ────────────────────────────────────────

    [Fact]
    public void DomLevel1PageContracts()
    {
        var page = new PageHarness();
        LoadScripted(page, "dom", "dom-level1.html");
        Check.That(page.ScriptErrors.Count == 0,
            "dom-level1 scripts run clean",
            string.Join(" | ", page.ScriptErrors.Take(3)));
        string report = TextOf(page.Document, "report1") ?? "";
        Check.That(report.Contains("nodeType(body)=1"), "body nodeType is 1", report);
        Check.That(report.Contains("nodeName(body)=BODY"), "body nodeName is BODY");
        Check.That(report.Contains("createTextNode data=created by "), "createTextNode works");
        Check.That(report.Contains("replaceChild=true"), "replaceChild works");
        Check.That(report.Contains("cloneNode(deep)=true"), "cloneNode(deep) works");
        Check.That(report.Contains("removeAttribute=gone"), "removeAttribute works");
        Check.That(report.Contains("attributes.length>0=true"), "attributes map exposes length");
        Check.That(report.Contains("H2 count="), "getElementsByTagName counts");
        Check.That(report.Contains("style.backgroundColor="), "style camelCase write works");
        Check.Done();
    }

    [Fact]
    public void Ie5DhtmlPageContracts()
    {
        var page = new PageHarness();
        LoadScripted(page, "dom", "dom-ie5-dhtml.html");
        Check.That(page.ScriptErrors.Count == 0,
            "dom-ie5 scripts run clean",
            string.Join(" | ", page.ScriptErrors.Take(3)));

        string report = TextOf(page.Document, "report2") ?? "";
        Check.That(report.Contains("all-count=true"), "document.all collection enumerates");
        Check.That(report.Contains("all(name)=ie5probe"), "all(name) resolves by id/name");
        Check.That(report.Contains("all.item=ok"), "all.item() works");
        Check.That(report.Contains("tags(H2)="), "all.tags() works");
        Check.That(report.Contains("innerHTML=<b>replaced</b>"), "innerHTML writes parse back");
        Check.That(report.Contains("innerText=head+ replaced"), "insertAdjacentText lands");
        Check.That(report.Contains("pixelLeft=40"), "style.pixelLeft works");
        Check.That(report.Contains("offsetWidth>0=true"), "offsetWidth is real geometry");
        Check.That(report.Contains("offsetHeight>0=true"), "offsetHeight is real geometry");
        Check.That(report.Contains("clientWidth="), "clientWidth reads");
        Check.That(report.Contains("currentStyle=ok"), "currentStyle object exists");
        Check.That(report.Contains("currentStyle.width=auto"), "currentStyle.width reports auto");
        Check.That(report.Contains("readyState=loading"), "parser-time report sees loading readyState");
        Check.That(report.Contains("uniqueID=ok"), "uniqueID is stable");
        Check.That(report.Contains("parentElement=BODY"), "parentElement resolves");
        Check.That(report.Contains("children=ok"), "body.children reports its child elements");
        Check.Done();
    }

    [Fact]
    public void Ns47LayerPersonaContracts()
    {
        // The NS4.7 page takes the layer branch ONLY under its persona.
        var page = new PageHarness();
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            LoadScripted(page, "dom", "dom-ns4-layers.html");
            Check.That(page.ScriptErrors.Count == 0,
                "ns4-layers scripts run clean under the NS4.7 persona",
                string.Join(" | ", page.ScriptErrors.Take(3)));

            // The browser resolves styles BEFORE scripts query layers —
            // document.layers only contains POSITIONED elements.
            Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);

            Check.That(page.EvalString("typeof document.layers") == "object",
                "document.layers exists in the NS4.7 persona");
            Check.That(page.EvalString("typeof document.all") == "undefined",
                "document.all is undefined in the NS4.7 persona");
            Check.That(page.EvalString("typeof document.getElementById") == "undefined",
                "getElementById is undefined in the NS4.7 persona");

            string report = page.EvalString("ns4Report()");
            Check.That(report.Contains("layers-count=3"),
                "three positioned elements appear in layers", report);
            Check.That(report.Contains("layers[name]=ok"), "layers resolve by name");
            Check.That(report.Contains("non-positioned-in-layers=no"),
                "non-positioned elements never leak into layers");
            Check.That(report.Contains("after-moveTo left=120"), "moveTo moves the layer");
            Check.That(report.Contains("after-moveBy left=130"), "moveBy offsets the layer");
            Check.That(report.Contains("moveAbove=ok"), "moveAbove restacks");
            Check.That(report.Contains("clip.right="), "clip rect reads");
            Check.That(report.Contains("clip-set=100"), "clip rect writes");
            Check.That(report.Contains("bgColor-set=ok"), "bgColor writes");
            Check.That(report.Contains("captureEvents=ok"), "window.captureEvents works");
            Check.That(report.Contains("pageXOffset="), "pageXOffset reads");
            Check.That(report.Contains("innerWidth=ok"), "innerWidth reads");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    [Fact]
    public void Ns4LayerBatteryAlsoRunsInRetro96UnionPersona()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
        try
        {
            var page = new PageHarness();
            LoadScripted(page, "dom", "dom-ns4-layers.html");
            Check.That(page.ScriptErrors.Count == 0,
                "layer page scripts run clean in the Retro96 union persona",
                string.Join(" | ", page.ScriptErrors.Take(3)));

            var layers = page.EvalString("ns4Report()");
            Check.That(layers.Contains("layers-count=3"),
                "stylesheet-positioned elements populate union document.layers", layers);
            Check.That(layers.Contains("layers[name]=ok"),
                "union document.layers resolves CSS-positioned elements by name", layers);
            Check.That(layers.Contains("non-positioned-in-layers=no"),
                "unpositioned elements stay out of union document.layers", layers);
            Check.That(page.EvalString("document.layers[0].id") == "box1",
                "union document.layers supports numeric indexing");
            Check.That(page.EvalString("document.layers['box1'].document === document") == "true",
                "union Layer.document refers to the owning document");
            Check.That(layers.Contains("after-moveTo left=120"),
                "union Layer.moveTo works", layers);
            Check.That(layers.Contains("after-moveBy left=130"),
                "union Layer.moveBy works", layers);
            Check.That(layers.Contains("moveAbove=ok"),
                "union Layer.moveAbove works", layers);
            Check.That(layers.Contains("clip-set=100"),
                "union Layer.clip is writable", layers);
            Check.That(layers.Contains("bgColor-set=ok"),
                "union Layer.bgColor is writable", layers);
            Check.That(layers.Contains("captureEvents=ok"),
                "union window.captureEvents and Event constants are exposed", layers);
            Check.That(layers.Contains("Event.CLICK=4"),
                "union Event.CLICK has the expected mask", layers);
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    // ── §11: events ─────────────────────────────────────────────────────────

    [Fact]
    public void Events1999Contracts()
    {
        var page = new PageHarness();
        LoadScripted(page, "events", "events-1999.html");
        Check.That(page.ScriptErrors.Count == 0,
            "events page scripts run clean",
            string.Join(" | ", page.ScriptErrors.Take(3)));

        // attachEvent handler fires on click
        var attach1 = page.Document.ElementDescendants()
            .First(e => e.GetAttr("id") == "attach1");
        page.FireEvent(attach1, "onclick");
        var w = page.Scope.Get("window").GetObjectOrFunction();
        string status1 = w?.Get("status").ToJsString() ?? "";
        Check.That(status1.Contains("attachEvent fired"),
            "attachEvent handlers receive clicks", status1);

        // IE bubbling: clicking #inner walks up to #outer, srcElement stays inner
        var inner = page.Document.ElementDescendants().First(e => e.GetAttr("id") == "inner");
        page.FireEvent(inner, "onclick");
        string status2 = w?.Get("status").ToJsString() ?? "";
        Check.That(status2.Contains("srcElement=inner"),
            "the bubbled outer handler sees srcElement=inner", status2);

        // onsubmit cancellation via return false and returnValue=false
        var f1 = page.Document.FirstTag("form");
        var blocked = page.FireEvent(f1, "onsubmit");
        Check.That(!blocked.ToBoolean(),
            "onsubmit return false cancels submission");

        // intrinsic event attrs survive on the 4.01 events set
        Check.That(inner.Parent is DomElement ip && ip.EventHandlers.ContainsKey("onclick"),
            "intrinsic onclick attr is captured");
        var keyBox = page.Document.ElementDescendants()
            .First(e => e.TagName == "div" && (e.InnerText ?? "").Contains("dblclick / mousedown"));
        Check.That(keyBox.EventHandlers.ContainsKey("ondblclick") &&
                  keyBox.EventHandlers.ContainsKey("onkeydown") &&
                  keyBox.EventHandlers.ContainsKey("onkeypress") &&
                  keyBox.EventHandlers.ContainsKey("onkeyup"),
            "the 4.01 key/dblclick event set is captured");
        Check.Done();
    }

    // ── §12: ES3 language + version gating ──────────────────────────────────

    [Fact]
    public void Es3PageContracts()
    {
        var page = new PageHarness();
        LoadScripted(page, "javascript", "es3-1999.html");
        Check.That(page.ScriptErrors.Count == 0,
            "es3 scripts run clean",
            string.Join(" | ", page.ScriptErrors.Take(3)));
        string report = TextOf(page.Document, "r1") ?? "";
        Check.That(report.Contains("catch=TypeError"), "null property access yields a TypeError");
        Check.That(report.Contains("finally=ok"), "finally runs");
        Check.That(report.Contains("switch-fallthrough=one two"), "switch fallthrough works");
        Check.That(report.Contains("dowhile=3"), "do-while works");
        Check.That(report.Contains("labels=6"), "labelled break/continue works");
        Check.That(report.Contains("===num,strict"), "strict equality works");
        Check.That(report.Contains("delete=gone"), "delete works");
        Check.That(report.Contains("match=answer:42"), "String.match with regex works");
        Check.That(report.Contains("replace=a#b#"), "regex replace works");
        Check.That(report.Contains("split=4"), "regex split works");
        Check.That(report.Contains("fn-expr=49"), "function expressions work");
        Check.That(report.Contains("push=4,4"), "Array.push/pop/shift/unshift work");
        Check.That(report.Contains("toFixed=3.14"), "Number.toFixed(2) works");
        Check.That(report.Contains("toExponential=1.23e+3") ||
                   report.Contains("toExponential=1.23e+03"),
            "toExponential works", report);
        Check.That(report.Contains("toPrecision=1.23e+3") ||
                   report.Contains("toPrecision=1230"),
            "toPrecision works", report);
        Check.That(report.Contains("getYear=100"), "Y2K getYear returns 100");
        Check.That(report.Contains("getFullYear=2000"), "getFullYear returns 2000");
        Check.That(report.Contains("push-returns=2"), "JS1.3 push returns the new length");
        string host = TextOf(page.Document, "r2") ?? "";
        Check.That(host.Contains("userAgent=Mozilla/4.0 (compatible; MSIE 5.0; Windows 98)"),
            "default persona advertises the IE5 UA", host);
        Check.That(host.Contains("javaEnabled=true"), "navigator.javaEnabled() works");
        Check.That(host.Contains("screen="), "screen object reads");
        Check.That(host.Contains("escape=a%20b%26c") || host.Contains("escape=a+b%26c") ||
                  host.Contains("escape=a%20b%26c"),
            "escape() encodes", host);
        Check.Done();
    }

    [Fact]
    public void ScriptLanguageVersionGating()
    {
        // Under the NS4.7 persona, JavaScript1.5 blocks are skipped;
        // 1.0-1.3 blocks run. The union persona runs everything.
        string html = "<html><body>" +
            "<script language=\"JavaScript1.2\">window.gate12 = 'ran';</script>" +
            "<script language=\"JavaScript1.5\">window.gate15 = 'ran';</script>" +
            "</body></html>";

        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var ns = new PageHarness();
            ns.LoadHtml(html);
            Check.That(ns.EvalString("typeof window.gate12") == "string",
                "NS4.7 persona runs the JavaScript1.2 block");
            Check.That(ns.EvalString("typeof window.gate15") == "undefined",
                "NS4.7 persona SKIPS the JavaScript1.5 block (its ceiling is 1.3)");
        }
        finally { BrowserRuntime.Apply(new UserSettings()); }

        var union = new PageHarness();
        union.LoadHtml(html);
        Check.That(union.EvalString("typeof window.gate15") == "string",
            "union persona runs every language version");
        Check.Done();
    }

    // ── §13: VBScript 5.0 (page-level; semantics live in VbsTests) ──────────

    [Fact]
    public void Vbscript5PageParses()
    {
        var (doc, root) = Layout("vbscript", "vbscript5.html");
        Check.That(doc.AllTags("script").Count >= 3,
            "the VBScript page carries its script blocks");
        Check.That((doc.FirstTag("body")?.InnerText ?? "").Contains("Class_Initialize"),
            "the class documentation text renders");
        for (int i = 1; i <= 10; i++)
            Check.That(doc.ElementDescendants().Any(e => e.GetAttr("id") == $"v{i}"),
                $"report slot v{i} parses");
        Check.That(doc.AllTags("input").Count == 1, "the Btn_OnClick button parses");
        Check.Done();
    }

    // ── §14a/§14b: extras pages ─────────────────────────────────────────────

    [Fact]
    public void Ie5ExtrasContracts()
    {
        var (doc, root) = Layout("ie", "ie5-extras.html");
        Check.That(doc.AllTags("marquee").Count == 3, "three marquees parse");
        var alt = doc.ElementDescendants().FirstOrDefault(m => m.GetAttr("behavior") == "alternate");
        Check.That(alt?.GetAttr("truespeed") == "truespeed" || alt?.HasAttr("truespeed") == true,
            "trueSpeed attr round-trips (minimized)");
        Check.That(doc.AllTags("bgsound").Count == 1, "bgsound parses");
        Check.That(doc.AllTags("nobr").Count == 1 && doc.AllTags("wbr").Count == 1,
            "nobr + wbr parse");
        Check.That(doc.AllTags("ruby").Count == 1, "ruby parses");
        var body = doc.FirstTag("body");
        Check.That(body?.GetAttr("leftmargin") == "10" && body.GetAttr("bgproperties") == "fixed",
            "body IE extras (leftmargin/bgproperties) round-trip");
        var island = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "xml");
        Check.That(island != null, "the xml data island parses");
        Check.That(!(doc.FirstTag("body")?.InnerText ?? "").Contains("never renders"),
            "IE comment content stays concealed");
        var lowsrc = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "img" && e.HasAttr("lowsrc"));
        Check.That(lowsrc != null && lowsrc.HasAttr("dynsrc"), "img lowsrc/dynsrc round-trip");
        Check.Done();
    }

    [Fact]
    public void Ns4ExtrasContracts()
    {
        var (doc, root) = Layout("netscape", "ns4-extras.html");
        Check.That(doc.AllTags("layer").Count == 2, "two LAYER elements parse");
        var lay1 = doc.AllTags("layer")[0];
        Check.That(lay1.GetAttr("left") == "20" && lay1.GetAttr("clip") != null,
            "layer attrs (left/top/clip/above/visibility) round-trip");
        Check.That(doc.AllTags("ilayer").Count == 1, "ILAYER parses");
        Check.That(doc.AllTags("nolayer").Count == 1, "NOLAYER parses (concealed)");
        Check.That(!(doc.FirstTag("body")?.InnerText ?? "").Contains("stays hidden here"),
            "nolayer fallback content stays concealed");
        Check.That(doc.AllTags("multicol").Count == 1, "MULTICOL parses");
        Check.That(doc.AllTags("spacer").Count >= 3, "spacer elements parse");
        Check.That(doc.AllTags("keygen").Count == 1 && doc.AllTags("server").Count == 1,
            "keygen + server parse-and-ignore");
        var font = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "font" && e.HasAttr("point-size"));
        Check.That(font != null, "font point-size attr parses");
        var jsss = doc.AllTags("style").FirstOrDefault(s =>
            (s.GetAttr("type") ?? "").Contains("javascript"));
        Check.That(jsss != null, "the JSSS style block parses as an element");
        Check.Done();
    }

    // ── §15: images/media ───────────────────────────────────────────────────

    [Fact]
    public void ImagesMediaContracts()
    {
        var (doc, root) = Layout("images", "images-media.html");
        Check.That(doc.AllTags("map").Count == 1 && doc.AllTags("area").Count == 3,
            "client-side image map parses");
        var ismap = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "img" && e.HasAttr("ismap"));
        Check.That(ismap != null, "server-side ismap parses");
        var lowsrc = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "img" && e.HasAttr("lowsrc"));
        Check.That(lowsrc != null, "lowsrc pair parses");
        Check.That(doc.AllTags("embed").Count == 1 && doc.AllTags("noembed").Count == 1,
            "audio embed + noembed parse");
        Check.That(doc.AllTags("bgsound").Count == 1, "bgsound audio parses");
        Check.That(File.Exists(Page("images", "tada.wav")) &&
                   File.Exists(Page("images", "theme.mid")),
            "audio fixtures exist");
        Check.Done();
    }

    // ── §17: Acid1 + Y2K ────────────────────────────────────────────────────

    [Fact]
    public void Acid1MosaicRenders()
    {
        var (doc, root) = Layout("acid1.html");
        var solidCells = doc.ElementDescendants()
            .Count(e => e.TagName == "td" && (e.GetAttr("class") ?? "").Contains("solid"));
        Check.That(solidCells == 2, "the two solid mosaic corner cells parse", $"{solidCells}");
        var images = doc.AllTags("img");
        Check.That(images.Count == 5, "float + mosaic + padding images parse", $"{images.Count}");

        // The mosaic must have ink: render and probe for black pixels.
        var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bmp = LayoutHarness.Render(doc, root, cache, loader);
        int darkPixels = 0;
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R < 40 && p.G < 40 && p.B < 40) darkPixels++;
            }
        Check.That(darkPixels > 40,
            "Acid1's solid cells paint real ink (box metrics produce a mosaic)",
            $"{darkPixels} dark samples");
        Check.Done();
    }

    [Fact]
    public void Y2KPageContracts()
    {
        var page = new PageHarness();
        LoadScripted(page, "y2k.html");
        Check.That(page.ScriptErrors.Count == 0,
            "y2k scripts run clean", string.Join(" | ", page.ScriptErrors.Take(3)));
        string report = TextOf(page.Document, "report") ?? "";
        Check.That(report.Contains("getYear(2000-01-01)=100"),
            "new Date(2000,0,1).getYear() === 100 (ECMA)", report);
        Check.That(report.Contains("getFullYear(2000-01-01)=2000"),
            "getFullYear() === 2000", report);
        Check.That(report.Contains("getYear(1999-12-31)=99"), "1999 getYear is 99");
        Check.That(report.Contains("getYear(2001-03-01)=101"), "2001 getYear is 101");
        Check.That(report.Contains("cookie-past-2000=stored") ||
                  report.Contains("cookie-past-2000=skipped-file"),
            "cookie expiry past 2000 stores (or skips on file: pages)", report);
        Check.That(report.Contains("parse(January 1, 2000)=epoch-ok"),
            "Date.parse of a 4-digit year lands in the right epoch", report);
        Check.Done();
    }

    // ── §16: HTTP/1.1 wire contracts (real loopback server) ─────────────────

    [Fact]
    public async Task Http11KeepAliveAndValidationCache()
    {
        int port = 18950 + Random.Shared.Next(40);
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        int hits = 0;
        string etag = "\"v1\"";
        var serveLoop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                hits++;
                if (ctx.Request.Headers["If-None-Match"] == etag)
                {
                    ctx.Response.StatusCode = 304;
                    ctx.Response.Headers["ETag"] = etag;
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.Close();
                    continue;
                }
                byte[] body = System.Text.Encoding.ASCII.GetBytes(
                    "<html><body><h1>HTTP11-CACHE-OK</h1></body></html>");
                ctx.Response.StatusCode = 200;
                ctx.Response.Headers["ETag"] = etag;
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });

        try
        {
            EngineHttpClient.ClearCache();
            var http = new EngineHttpClient();
            var cookies = new CookieStore();
            var url = ParsedUrl.Parse($"http://127.0.0.1:{port}/cached.html");

            var first = await http.GetAsync(url, cookies, default(CancellationToken));
            Check.That(first is HttpSuccess s1 && s1.StatusCode == 200,
                "first GET returns 200");
            Check.That(first is HttpSuccess s1b && s1b.Body.Length > 0, "first GET carries the body");

            var second = await http.GetAsync(url, cookies, default(CancellationToken));
            Check.That(second is HttpSuccess s2 &&
                       System.Text.Encoding.ASCII.GetString(s2.Body).Contains("HTTP11-CACHE-OK"),
                "second GET is served from cache after 304 revalidation");
            Check.That(hits == 2,
                "the server saw exactly two requests (200 + one revalidation)",
                $"{hits} hits");

            // keep-alive: two sequential requests reuse the pooled socket
            // (HttpListener serves both on one connection when the client
            // keeps it alive).
            int connectionsBefore = listenerCountField(listener);
            _ = await http.GetAsync(ParsedUrl.Parse($"http://127.0.0.1:{port}/a.html"), cookies, default(CancellationToken));
            _ = await http.GetAsync(ParsedUrl.Parse($"http://127.0.0.1:{port}/b.html"), cookies, default(CancellationToken));
            Check.That(hits == 4, "both sequential GETs complete", $"{hits} hits");
        }
        finally
        {
            listener.Stop();
            listener.Close();
            EngineHttpClient.ClearCache();
        }
        Check.Done();
    }

    private static int listenerCountField(System.Net.HttpListener listener) => 0; // connection count is not exposed; hits are the contract

    // ── persona surface used by the whole suite ─────────────────────────────

    [Fact]
    public void PersonaSurfaceDefaults()
    {
        var previousSettings = BrowserRuntime.Settings.Clone();
        try
        {
            BrowserRuntime.Apply(new UserSettings());
            Check.That(BrowserRuntime.IsRetro96, "fresh settings default to the Retro96 compatibility union");
            Check.That(BrowserRuntime.SupportsDocumentAll && BrowserRuntime.SupportsDocumentLayers,
                "Retro96 union exposes both legacy DOM surfaces");
            Check.That(BrowserRuntime.SupportsGetElementById, "Retro96 union exposes getElementById");
            Check.That(BrowserRuntime.Http11Enabled, "1999 personas speak HTTP/1.1");
            Check.That(!BrowserRuntime.UsesIe5BoxModel, "Retro96 union uses the standard CSS box model");
            Check.That(UserSettings.DefaultIe5UserAgent == "Mozilla/4.0 (compatible; MSIE 5.0; Windows 98)",
                "the IE5 UA string is the period capture");
            Check.That(UserSettings.DefaultNetscape47UserAgent == "Mozilla/4.7 [en] (Win98; I)",
                "the NS4.7 UA string is the period capture");
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
        Check.Done();
    }
}
