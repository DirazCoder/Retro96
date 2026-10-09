// Step 4 unit tests — HTML parser.
using Retro96;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Network;

namespace RetroTests;

public class HtmlParserTests
{
    private static DomDocument Parse(string html) =>
        HtmlParser.Parse(html, ParsedUrl.Parse("http://x.test/page.html"), new CookieStore());

    /// <summary>
    /// The Form1 wiring: an executor is handed to the parser only while
    /// BrowserRuntime.ScriptingEnabled — used to test <noscript> both ways.
    /// </summary>
    private static DomDocument ParseWithScripting(string html) =>
        HtmlParser.Parse(html, ParsedUrl.Parse("http://x.test/page.html"), new CookieStore(),
            (doc, src, isVbScript) => "");

    private static string? ParentTag(DomNode? node) => (node?.Parent as DomElement)?.TagName;

    // ── entities ─────────────────────────────────────────────────────

    [Fact]
    public void NamedEntitiesDecodeToRightCodepoints()
    {
        var cases = new (string Entity, string Expected)[]
        {
            ("&nbsp;",   "\u00A0"),
            ("&amp;",    "&"),
            ("&lt;",     "<"),
            ("&gt;",     ">"),
            ("&quot;",   "\""),
            ("&copy;",   "\u00A9"),
            ("&reg;",    "\u00AE"),
            ("&trade;",  "\u2122"),
            ("&mdash;",  "\u2014"),
            ("&ndash;",  "\u2013"),
            ("&hellip;", "\u2026"),
            ("&laquo;",  "\u00AB"),
            ("&raquo;",  "\u00BB"),
            ("&euro;",   "\u20AC"),
            ("&pound;",  "\u00A3"),
            ("&yen;",    "\u00A5"),
        };
        foreach (var (entity, expected) in cases)
        {
            string decoded = HtmlEntities.Decode($"a{entity}b");
            Check.That(decoded == $"a{expected}b",
                $"entity {entity} → U+{(int)expected[0]:X4}", $"got '{decoded.Replace("\u00A0", "[nbsp]")}'");
        }
        Check.Done();
    }

    [Fact]
    public void NumericEntitiesDecimalAndHex()
    {
        Check.That(HtmlEntities.Decode("&#65;") == "A", "decimal entity &#65; → A");
        Check.That(HtmlEntities.Decode("&#x41;") == "A", "lowercase hex entity &#x41; → A");
        Check.That(HtmlEntities.Decode("&#X41;") == "A", "uppercase hex entity &#X41; → A");
        Check.That(HtmlEntities.Decode("&#169;") == "\u00A9", "decimal entity &#169; → ©");
        Check.That(HtmlEntities.Decode("&#xA9;") == "\u00A9", "hex entity &#xA9; → ©");
        Check.Done();
    }

    [Fact]
    public void LegacyWindows1252NumericEntitiesDecodeToVisibleUnicode()
    {
        // Many 1990s pages wrote a Windows-1252 bullet as &#149; before links.
        // U+0095 is a C1 control in Unicode, but the historical browser/content
        // compatibility mapping treats it as Windows-1252 U+2022 BULLET.
        Check.That(HtmlEntities.Decode("&#149;") == "\u2022",
            "legacy &#149; decodes to U+2022 BULLET",
            $"got U+{(int)HtmlEntities.Decode("&#149;")[0]:X4}");
        Check.That(HtmlEntities.Decode("&#146;") == "\u2019",
            "legacy &#146; decodes to U+2019 RIGHT SINGLE QUOTATION MARK");
        Check.That(HtmlEntities.Decode("&#150;") == "\u2013",
            "legacy &#150; decodes to U+2013 EN DASH");
        Check.Done();
    }

    [Fact]
    public void EntitiesDecodeThroughFullParse()
    {
        var doc = Parse("<html><body><p>caf&eacute; &amp; cr&egrave;me</p></body></html>");
        var p = doc.FirstTag("p");
        Check.That((p?.InnerText ?? "").Contains("café"), "parsed text contains decoded é");
        Check.That((p?.InnerText ?? "").Contains("&"), "parsed &amp; decodes to &");
        Check.Done();
    }

    [Fact]
    public void EditedTitleElementUpdatesDocumentTitleWithoutChangingBodyText()
    {
        var doc = Parse("<html><head><title>Retro67 &amp; Friends</title></head>" +
                        "<body><h1>Original heading</h1></body></html>");

        Check.That(doc.Title == "Retro67 & Friends",
            "the first title element supplies the decoded document title", doc.Title);
        Check.That(doc.FirstTag("title")?.InnerText == "Retro67 & Friends",
            "the title element retains its edited text");
        Check.That(doc.FirstTag("h1")?.InnerText == "Original heading",
            "changing document title does not rewrite visible body content");
        Check.Done();
    }

    // ── unclosed tag handling ────────────────────────────────────────

    [Fact]
    public void UnclosedTagsAutoCloseInRightOrder()
    {
        // two unclosed <p> → siblings, not nested
        var doc = Parse("<html><body><p>first<p>second</body></html>");
        var ps = doc.AllTags("p");
        Check.That(ps.Count == 2, "unclosed <p><p> → two paragraphs", ps.Count.ToString());
        Check.That(ps.Count == 2 && ReferenceEquals(ps[0].Parent, ps[1].Parent),
            "the two <p> are siblings (auto-closed in order)");

        // unclosed <li>
        doc = Parse("<html><body><ul><li>one<li>two<li>three</ul></body></html>");
        var lis = doc.AllTags("li");
        Check.That(lis.Count == 3, "unclosed <li> → three list items", lis.Count.ToString());

        // <b><i> cross-nesting closes in a sane order (no crash, content preserved)
        doc = Parse("<html><body><b>bold <i>bolditalic</b> italic</i></body></html>");
        Check.That(doc != null, "crossed <b><i>…</b>…</i> parses without crash");
        Check.That((doc!.FirstTag("body")?.InnerText ?? "").Contains("bolditalic"),
            "crossed inline content preserved");

        // <td> without closing tags inside a table
        doc = Parse("<html><body><table><tr><td>a<td>b</table></body></html>");
        var tds = doc.AllTags("td");
        Check.That(tds.Count == 2, "unclosed <td>s auto-close", tds.Count.ToString());
        Check.That(tds.Count == 2 && ReferenceEquals(tds[0].Parent, tds[1].Parent),
            "the two <td> share a <tr>");
        Check.Done();
    }

    [Fact]
    public void StrayTextBetweenTableRowsIsFosterParentedOutsideTable()
    {
        var doc = Parse("<html><body><table border='1'><tr><td>cell one</td></tr>" +
                        "stray text node directly between tr tags" +
                        "<tr><td>cell two</td></tr></table></body></html>");

        var body = doc.FirstTag("body")!;
        var table = doc.FirstTag("table")!;
        const string stray = "stray text node directly between tr tags";

        var fostered = body.Children.OfType<DomText>()
            .FirstOrDefault(t => t.Data.Contains(stray, StringComparison.Ordinal));
        Check.That(fostered != null,
            "non-whitespace text between rows is retained as a body-level text node");
        Check.That(fostered != null && body.Children.IndexOf(fostered) < body.Children.IndexOf(table),
            "fostered stray text is placed before the table");
        Check.That(!table.Descendants().OfType<DomText>()
            .Any(t => t.Data.Contains(stray, StringComparison.Ordinal)),
            "stray text is not inserted into the table row/cell grid");

        doc = Parse("<html><body><table><tr><td>one</td></tr>\n   \n<tr><td>two</td></tr></table></body></html>");
        var table2 = doc.FirstTag("table")!;
        Check.That(!table2.Descendants().OfType<DomText>()
            .Any(t => t.Data.Trim().Length == 0),
            "whitespace-only text between rows remains ignored (TN-07)");
        Check.Done();
    }

    [Fact]
    public void MisnestedFontEndTagReconstructsBoldOutsideFont()
    {
        var doc = Parse("<html><body><p><font color=red><b>red bold</font> bold after font?</b> plain.</p></body></html>");
        var font = doc.FirstTag("font");
        var p = doc.FirstTag("p");
        var bolds = doc.AllTags("b").ToList();

        Check.That(font != null && p != null, "font and p survive the malformed inline markup");
        Check.That(bolds.Count == 2, "misnested </font> reconstructs an open <b> chain", bolds.Count.ToString());

        var firstBold = bolds.FirstOrDefault(b => ReferenceEquals(b.Parent, font));
        var continuedBold = bolds.FirstOrDefault(b => ReferenceEquals(b.Parent, p));
        Check.That(firstBold != null && (firstBold!.InnerText ?? "").Contains("red bold"),
            "content before </font> stays inside the original bold+font chain");
        Check.That(continuedBold != null && (continuedBold!.InnerText ?? "").Contains("bold after font?"),
            "content after </font> remains bold outside the font element");
        Check.Done();
    }

    [Fact]
    public void ImpliedParagraphBreakReconstructsOpenFormattingChain()
    {
        var doc = Parse("<html><body><p><b>bold <i>bold-italic <u>bold-italic-underline" +
                        "<p>paragraph break inside three unclosed inlines" +
                        "<p>fourth paragraph — reconstruction must reopen b/i/u.</body></html>");
        Retro96.Engine.Css.StyleResolver.Resolve(doc, 800);
        var paragraphs = doc.AllTags("p").ToList();

        Check.That(paragraphs.Count == 3, "each implied paragraph break creates a sibling paragraph",
            paragraphs.Count.ToString());

        string[] expectedText =
        [
            "bold bold-italic bold-italic-underline",
            "paragraph break inside three unclosed inlines",
            "fourth paragraph — reconstruction must reopen b/i/u."
        ];
        for (int i = 0; i < Math.Min(paragraphs.Count, expectedText.Length); i++)
        {
            var bold = paragraphs[i].ElementChildren().FirstOrDefault(e => e.TagName == "b");
            var italic = bold?.ElementChildren().FirstOrDefault(e => e.TagName == "i");
            var underline = italic?.ElementChildren().FirstOrDefault(e => e.TagName == "u");
            Check.That(bold != null && italic != null && underline != null,
                $"paragraph {i + 1} reconstructs the b/i/u chain");
            Check.That(bold?.Style?.FontWeight == Retro96.Engine.Css.FontWeightValue.Bold &&
                       italic?.Style?.FontStyle == Retro96.Engine.Css.FontStyleValue.Italic &&
                       underline?.Style?.TextDecoration.HasFlag(Retro96.Engine.Css.TextDecoration.Underline) == true,
                $"paragraph {i + 1} retains bold, italic, and underline styling");
            Check.That(paragraphs[i].InnerText.Trim() == expectedText[i],
                $"paragraph {i + 1} retains all of its text",
                paragraphs[i].InnerText.Trim());
            if (i > 0)
                Check.That(underline?.InnerText.Trim() == expectedText[i],
                    $"paragraph {i + 1} text remains inside the reconstructed formatting chain",
                    underline?.InnerText.Trim() ?? "(no underline chain)");
        }
        Check.Done();
    }

    // ── attribute quoting ────────────────────────────────────────────

    [Fact]
    public void AttributeValuesParseWithAllQuotingStyles()
    {
        var doc = Parse("<html><body><a href=http://x.test/ unquoted1>link</a>" +
                        "<a href='http://y.test/single'>l2</a>" +
                        "<a href=\"http://z.test/double\">l3</a>" +
                        "<img src=a.gif width=10 height=20>" +
                        "</body></html>");
        var links = doc.AllTags("a");
        Check.That(links.Count == 3, "three <a> parsed", links.Count.ToString());
        Check.That(links.Count > 0 && links[0].GetAttr("href") == "http://x.test/",
            "unquoted attribute value", links.Count > 0 ? links[0].GetAttr("href") ?? "(null)" : "");
        Check.That(links.Count > 1 && links[1].GetAttr("href") == "http://y.test/single",
            "single-quoted attribute value");
        Check.That(links.Count > 2 && links[2].GetAttr("href") == "http://z.test/double",
            "double-quoted attribute value");

        var img = doc.FirstTag("img");
        Check.That(img?.GetAttr("src") == "a.gif", "unquoted img src");
        Check.That(img?.GetAttr("width") == "10" && img?.GetAttr("height") == "20",
            "unquoted img width/height");
        Check.Done();
    }

    // ── script / style CDATA handling ────────────────────────────────

    [Fact]
    public void ScriptContentIsNotParsedAsHtml()
    {
        var doc = Parse("<html><head><script>if (a < b && c > d) { document.write(\"<b>fake</b>\"); }</script></head><body><p>real</p></body></html>");
        var scripts = doc.AllTags("script");
        Check.That(scripts.Count == 1, "one <script> element");
        var text = scripts.Count == 1 ? scripts[0].Children.OfType<DomText>().FirstOrDefault()?.Data : null;
        Check.That(text != null && text.Contains("a < b && c > d"),
            "raw < and > inside script survive as text", text == null ? "(no text node)" : text[..Math.Min(60, text.Length)]);
        Check.That(text == null || !text.Contains("<b>fake</b>") || text.Contains("<b>fake</b>"),
            "script body not dropped");
        // the <b>fake</b> must NOT become an element in the body
        var bodyBs = doc.AllTags("b").Where(b => (b.InnerText ?? "") == "fake").ToList();
        Check.That(bodyBs.Count == 0, "markup inside script does not become DOM elements");
        Check.Done();
    }

    [Fact]
    public void StyleContentIsNotParsedAsHtml()
    {
        var doc = Parse("<html><head><style>p { color: red } b < i { }</style></head><body><p>x</p></body></html>");
        var styles = doc.AllTags("style");
        Check.That(styles.Count == 1, "one <style> element");
        var text = styles.Count == 1 ? styles[0].Children.OfType<DomText>().FirstOrDefault()?.Data : null;
        Check.That(text != null && text.Contains("color: red"),
            "CSS text survives unparsed", text ?? "(null)");
        Check.That(doc.AllTags("i").Count == 0, "stray < inside CSS does not create elements");
        Check.Done();
    }

    [Fact]
    public void CdataSectionPassesThrough()
    {
        // HTML 3.2 has no CDATA — browsers treated <![CDATA[...]]> as a bogus
        // comment. The contract: no crash, no elements from the content.
        DomDocument? doc = null;
        Exception? ex = null;
        try { doc = Parse("<html><body><![CDATA[ raw <b>text</b> here ]]>after</body></html>"); }
        catch (Exception e) { ex = e; }
        Check.That(ex == null, "CDATA section does not crash the parser", ex?.Message ?? "");
        Check.That(doc != null && (doc.FirstTag("body")?.InnerText ?? "").Contains("after"),
            "content after CDATA still parses");
        Check.That(doc?.AllTags("b").Count(b => (b.InnerText ?? "") == "text") == 0,
            "CDATA payload does not become elements");
        Check.Done();
    }

    // ── unknown tags ─────────────────────────────────────────────────

    [Fact]
    public void UnknownTagsAreKeptAsTransparentBoxes()
    {
        var doc = Parse("<html><body><blink>flashing</blink> <layer>layered</layer> <spacer width=10> <foovendor>custom</foovendor> <marquee>scrolling</marquee></body></html>");
        foreach (var (tag, content) in new[] { ("blink", "flashing"), ("layer", "layered"), ("foovendor", "custom"), ("marquee", "scrolling") })
        {
            var el = doc.AllTags(tag).FirstOrDefault();
            Check.That(el != null, $"<{tag}> kept in the DOM");
            Check.That(el != null && (el.InnerText ?? "").Contains(content),
                $"<{tag}> content preserved");
        }
        var spacer = doc.FirstTag("spacer");
        Check.That(spacer?.GetAttr("width") == "10", "<spacer width=10> attribute kept");
        Check.Done();
    }

    // ── document.write splicing ──────────────────────────────────────

    [Fact]
    public void DocumentWriteOutputIsInsertedAtTheRightPosition()
    {
        var page = new PageHarness();
        page.LoadHtml(
            "<html><body>" +
            "<p>before</p>" +
            "<script>document.write(\"<p>written</p>\");</script>" +
            "<p>after</p>" +
            "</body></html>");

        var ps = page.Document.AllTags("p");
        Check.That(ps.Count == 3, "document.write spliced one <p>", ps.Count.ToString());
        if (ps.Count == 3)
        {
            Check.That((ps[0].InnerText ?? "") == "before", "static content before script stays first");
            Check.That((ps[1].InnerText ?? "") == "written", "written content lands between them");
            Check.That((ps[2].InnerText ?? "") == "after", "static content after script stays last");
        }
        Check.Done();
    }

    // ═══════════════════════════════════════════════════════════════════
    // HTML 4.01 upgrade (Task 4) — new elements, IE5/NS4 concealment tags,
    // implied tbody, minimized attributes, the 4.01 entity appendix and
    // DOCTYPE flavour recording.
    // ═══════════════════════════════════════════════════════════════════

    // ── IE5/NS4 concealment tags ─────────────────────────────────────

    [Fact]
    public void IeCommentElementContentsNeverRender()
    {
        // IE5's <comment> element: annotation text that must never appear.
        // The tokenizer swallows it as raw text; the parser drops it, so
        // no text node and no markup from inside can ever reach the body.
        var doc = Parse("<html><body><p>before</p>" +
                        "<comment>SECRET annotation <b>bold too</b> --></comment>" +
                        "<p>after</p></body></html>");

        var comment = doc.FirstTag("comment");
        Check.That(comment != null, "the <comment> element itself stays in the DOM");
        Check.That(comment?.Children.Count == 0,
            "comment content is dropped entirely (no text children)",
            comment?.Children.Count.ToString() ?? "");
        Check.That(doc.AllTags("b").Count == 0,
            "markup inside <comment> does not become DOM elements");

        var body = doc.FirstTag("body")!;
        Check.That((body.InnerText ?? "").Contains("before") &&
                   (body.InnerText ?? "").Contains("after"),
            "content around the comment is untouched");
        Check.That(!(body.InnerText ?? "").Contains("SECRET"),
            "comment text is absent from the document body text");
        Check.That(!body.Descendants().OfType<DomText>()
            .Any(t => t.Data.Contains("annotation", StringComparison.Ordinal)),
            "no text node anywhere in the body holds the comment content");
        Check.Done();
    }

    [Fact]
    public void NoembedAndNolayerContentsNeverRender()
    {
        // NOEMBED is the fallback for browsers WITHOUT <embed>; NOLAYER for
        // browsers WITHOUT layers. This engine supports both, so the
        // fallback content is concealed at parser level.
        var doc = Parse("<html><body><p>keep</p>" +
                        "<noembed><b>embed fallback</b><img src=fallback.gif></noembed>" +
                        "<nolayer><a href=nolayer.html>layer fallback</a></nolayer>" +
                        "<p>also keep</p></body></html>");

        foreach (var tag in new[] { "noembed", "nolayer" })
        {
            var el = doc.AllTags(tag).FirstOrDefault();
            Check.That(el != null, $"<{tag}> element stays in the DOM");
            Check.That(el?.Children.Count == 0,
                $"<{tag}> content is dropped (nothing can render)", tag);
        }
        Check.That(doc.AllTags("b").Count(b => (b.InnerText ?? "") == "embed fallback") == 0,
            "no elements materialize from noembed content");
        Check.That(doc.AllTags("a").Count == 0 && doc.AllTags("img").Count == 0,
            "no links/images materialize from the fallback tags");
        var body = doc.FirstTag("body")!;
        Check.That((body.InnerText ?? "").Contains("keep") &&
                   !(body.InnerText ?? "").Contains("fallback"),
            "body text contains only the surrounding content");
        Check.Done();
    }

    [Fact]
    public void NoscriptConcealedWhenScriptingEnabledRenderedWhenDisabled()
    {
        const string page =
            "<html><body><noscript><p>fallback <b>content</b></p></noscript><p>main</p></body></html>";

        // ── scripting ENABLED: the parser gets a live executor (Form1
        //    wiring) and default runtime settings — noscript content is
        //    swallowed as raw text and the style resolver hides the element.
        var withJs = ParseWithScripting(page);
        var noscript = withJs.FirstTag("noscript");
        Check.That(noscript != null, "<noscript> present with scripting on");
        Check.That(noscript?.Children.OfType<DomElement>().Count() == 0,
            "noscript content did not parse as elements while JS is on",
            string.Join(",", noscript?.Children.Select(c => c.GetType().Name) ?? Enumerable.Empty<string>()));
        Check.That(noscript?.Children.OfType<DomText>().FirstOrDefault()?.Data.Contains("<p>") == true,
            "noscript payload is retained as raw text (the era CDATA treatment)");
        Check.That(withJs.AllTags("p").Count == 1,
            "no fallback paragraph exists in the DOM while JS is on");
        Retro96.Engine.Css.StyleResolver.Resolve(withJs, 800);
        Check.That(withJs.FirstTag("noscript")?.Style?.Display == Retro96.Engine.Css.DisplayValue.None,
            "noscript display:none while scripting enabled");
        Check.That((withJs.FirstTag("body")?.InnerText ?? "").Contains("main") &&
                  !(withJs.FirstTag("body")?.InnerText ?? "").Contains("fallback"),
            "fallback text absent from the body while JS is on");

        // ── scripting DISABLED: BrowserRuntime toggle + no executor — the
        //    fallback content MUST render (that was the whole point).
        try
        {
            BrowserRuntime.Apply(new UserSettings
            {
                EnableJavaScript = false,
                EnableVBScript = false
            });
            var noJs = Parse(page);   // Form1 passes a null executor here
            var ns = noJs.FirstTag("noscript");
            Check.That(ns != null, "<noscript> present with scripting off");
            Check.That(noJs.AllTags("p").Count == 2,
                "the fallback paragraph parsed as real markup while JS is off",
                noJs.AllTags("p").Count.ToString());
            Check.That(ParentTag(noJs.AllTags("b").FirstOrDefault()) == "p",
                "the fallback <b> nested normally inside the fallback paragraph");
            Retro96.Engine.Css.StyleResolver.Resolve(noJs, 800);
            Check.That(noJs.FirstTag("noscript")?.Style?.Display != Retro96.Engine.Css.DisplayValue.None,
                "noscript is visible (not display:none) while scripting is disabled");
            var fallbackP = ns?.ElementDescendants().FirstOrDefault(e => e.TagName == "p");
            Check.That(fallbackP != null && (fallbackP.InnerText ?? "").Contains("fallback"),
                "the fallback content is real, visible DOM text while JS is off");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());   // restore engine defaults
        }
        Check.Done();
    }

    [Fact]
    public void XmlDataIslandKeptAsRawTextChild()
    {
        // IE5 XML data islands: <xml id=...> holds XML source for scripts
        // (document.all(id).innerHTML). The payload must survive verbatim
        // as a text child and never become HTML elements.
        // NOTE (display): the element needs the renderer's UA default
        // display:none to stay invisible — integrator's StyleResolver entry.
        var doc = Parse("<html><body><p>page</p>" +
                        "<xml id=\"catalog\"><catalog><book id=\"1\"><title>T</title></book></catalog></xml>" +
                        "</body></html>");

        var island = doc.FirstTag("xml");
        Check.That(island != null, "<xml> data island element exists");
        Check.That(island?.GetAttr("id") == "catalog", "the island keeps its id attribute");
        var text = island?.Children.OfType<DomText>().FirstOrDefault();
        Check.That(text != null && text.Data.Contains("<book id=\"1\">") &&
                   text.Data.Contains("<title>T</title>"),
            "the XML source is kept verbatim as a raw text child",
            text?.Data ?? "(no text child)");
        Check.That(doc.AllTags("book").Count == 0 && doc.AllTags("title").Count == 0,
            "the XML payload never parses into HTML elements");
        Check.Done();
    }

    [Fact]
    public void KeygenAndServerParseAsVoidElements()
    {
        // HTML 4.01 KEYGEN and the NS-era SERVER tag: parsed, ignored, no
        // content. Following content must not nest inside them.
        var doc = Parse("<html><body><form><keygen name=\"spki\" challenge=\"abc123\" keytype=\"rsa\">" +
                        "<input type=\"submit\" value=\"go\"></form>" +
                        "<server type=\"module\">ignored</server><p>after</p></body></html>");

        var keygen = doc.FirstTag("keygen");
        Check.That(keygen != null, "<keygen> element exists");
        Check.That(keygen?.GetAttr("challenge") == "abc123", "keygen attributes preserved");
        Check.That(keygen?.Children.Count == 0, "keygen is void — no children");
        Check.That(ParentTag(doc.FirstTag("input")) == "form",
            "the control after keygen is a sibling, not nested inside it");

        var server = doc.FirstTag("server");
        Check.That(server != null, "<server> element exists");
        Check.That(server?.Children.Count == 0, "server is void — no children");
        Check.That(ParentTag(doc.FirstTag("p")) == "body",
            "content after <server> flows as a body child");
        Check.Done();
    }

    // ── implied tbody (checklist §4 error recovery) ───────────────────

    [Fact]
    public void ImpliedTbodyWrapsRowsWrittenDirectlyInTable()
    {
        var doc = Parse("<html><body><table border=\"1\">" +
                        "<tr><td>a</td></tr>" +
                        "<tr><td>b</td></tr>" +
                        "</table></body></html>");
        var table = doc.FirstTag("table")!;
        var groups = table.ElementChildren().Where(e => e.TagName == "tbody").ToList();
        var directRows = table.ElementChildren().Where(e => e.TagName == "tr").ToList();
        Check.That(groups.Count == 1,
            "one implied tbody wraps the stray rows", groups.Count.ToString());
        Check.That(directRows.Count == 0,
            "no <tr> remains a direct child of <table>", directRows.Count.ToString());
        Check.That(groups.Count == 1 && groups[0].ElementChildren().Count(e => e.TagName == "tr") == 2,
            "both rows live inside the implied tbody");
        Check.That(doc.AllTags("td").Count == 2 && doc.AllTags("td").Count > 0 &&
                  ParentTag(doc.AllTags("td")[0]) == "tr",
            "cells still nest inside their rows");

        // Rows after a CLOSED explicit group start a fresh implied tbody.
        doc = Parse("<html><body><table>" +
                    "<thead><tr><th>h</th></tr></thead>" +
                    "<tr><td>body row</td></tr>" +
                    "</table></body></html>");
        table = doc.FirstTag("table")!;
        var kids = table.ElementChildren().Select(e => e.TagName).ToList();
        Check.That(kids.Count == 2 && kids[0] == "thead" && kids[1] == "tbody",
            "the row after </thead> goes into a NEW implied tbody sibling",
            string.Join(",", kids));
        Check.That(doc.AllTags("td").Count == 1 &&
                  ParentTag(doc.AllTags("td")[0]?.Parent) == "tbody",
            "the stray row's cells sit inside the implied tbody");

        // A cell with no <tr> at all: implied tbody AND implied tr.
        doc = Parse("<html><body><table><td>cell</td></table></body></html>");
        var td = doc.FirstTag("td");
        Check.That(ParentTag(td) == "tr" &&
                   ParentTag(td?.Parent) == "tbody" &&
                   ParentTag(td?.Parent?.Parent) == "table",
            "stray cell gets implied tbody > tr > td nesting");
        Check.Done();
    }

    [Fact]
    public void RowGroupStartClosesOpenRowGroupInsteadOfNesting()
    {
        // <tfoot> arriving while the implied tbody is still open must close
        // it, not nest inside it (nested groups lose their rows in the
        // TableLayout row-group walk).
        var doc = Parse("<html><body><table>" +
                        "<tr><td>one</td></tr>" +
                        "<tfoot><tr><td>foot</td></tr></tfoot>" +
                        "</table></body></html>");
        var table = doc.FirstTag("table")!;
        var kids = table.ElementChildren().Select(e => e.TagName).ToList();
        Check.That(kids.Count == 2 && kids[0] == "tbody" && kids[1] == "tfoot",
            "tfoot is a SIBLING of the implied tbody, not a child",
            string.Join(",", kids));
        Check.That(doc.AllTags("td").Count == 2,
            "both the body row and the footer row survive", doc.AllTags("td").Count.ToString());
        Check.Done();
    }

    // ── minimized attributes (HTML 4.01 SGML expansion) ──────────────

    [Fact]
    public void MinimizedAttributesExpandToNameEqualsName()
    {
        var doc = Parse("<html><body><form>" +
                        "<select name=\"s\"><option value=\"1\">one</option><option selected>two</option></select>" +
                        "<input type=\"checkbox\" checked>" +
                        "</form><table><tr><td nowrap>cell</td></tr></table>" +
                        "<OPTION SELECTED LABEL=\"u\">caps</OPTION></body></html>");

        var options = doc.AllTags("option");
        Check.That(options.Count >= 3, "options parsed", options.Count.ToString());
        Check.That(options.Count > 1 && options[1].GetAttr("selected") == "selected",
            "<option selected> parses as selected=\"selected\"",
            options.Count > 1 ? options[1].GetAttr("selected") ?? "(null)" : "");
        Check.That(options.Count > 0 && options[0].GetAttr("selected") == null,
            "an option without the attribute still has no selected attr");

        var input = doc.FirstTag("input");
        Check.That(input?.GetAttr("checked") == "checked",
            "<input checked> parses as checked=\"checked\"",
            input?.GetAttr("checked") ?? "(null)");

        var td = doc.FirstTag("td");
        Check.That(td?.GetAttr("nowrap") == "nowrap",
            "<td nowrap> parses as nowrap=\"nowrap\"",
            td?.GetAttr("nowrap") ?? "(null)");

        var caps = doc.AllTags("option").LastOrDefault();
        Check.That(caps?.HasAttr("selected") == true && caps.GetAttr("label") == "u",
            "uppercase minimized attr also expands (attribute names lowercase)");

        // Explicit empty value (value=) must NOT become the attr name.
        var doc2 = Parse("<html><body><select><option value=>Text</option></select></body></html>");
        Check.That(doc2.FirstTag("option")?.GetAttr("value") == "",
            "an explicit value= still yields the empty string");
        Check.Done();
    }

    // ── lang / dir / tabindex / accesskey round-trip (§4) ────────────

    [Fact]
    public void LangDirTabindexAccesskeyRoundTripThroughParser()
    {
        var doc = Parse("<html lang=\"en\" dir=\"ltr\">" +
                        "<body dir=\"rtl\" lang=\"ar\" xml:lang=\"ar\">" +
                        "<p lang=\"fr\" xml:lang=\"fr\" dir=\"ltr\" tabindex=\"3\" accesskey=\"K\">texte</p>" +
                        "<a href=\"#x\" tabindex=\"1\" accesskey=\"B\">lien</a>" +
                        "</body></html>");

        var p = doc.FirstTag("p");
        Check.That(p?.GetAttr("lang") == "fr", "lang preserved");
        Check.That(p?.GetAttr("xml:lang") == "fr", "xml:lang preserved");
        Check.That(p?.GetAttr("dir") == "ltr", "dir preserved");
        Check.That(p?.GetAttr("tabindex") == "3", "tabindex preserved");
        Check.That(p?.GetAttr("accesskey") == "K", "accesskey preserved");
        var a = doc.FirstTag("a");
        Check.That(a?.GetAttr("tabindex") == "1" && a?.GetAttr("accesskey") == "B",
            "anchor tabindex/accesskey preserved");
        var body = doc.FirstTag("body");
        Check.That(body?.GetAttr("dir") == "rtl" && body?.GetAttr("lang") == "ar",
            "body lang/dir merge keeps the values");
        var html = doc.FirstTag("html");
        Check.That(html?.GetAttr("lang") == "en" && html?.GetAttr("dir") == "ltr",
            "html lang/dir preserved");
        Check.Done();
    }

    // ── BDO / new inline elements (§4) ───────────────────────────────

    [Fact]
    public void BdoDirAttrPreserved()
    {
        var doc = Parse("<html><body><bdo dir=\"rtl\" lang=\"he\">txet</bdo>" +
                        "<bdo>default ltr</bdo></body></html>");
        var bdo = doc.AllTags("bdo").FirstOrDefault();
        Check.That(bdo != null, "<bdo> element parses");
        Check.That(bdo?.GetAttr("dir") == "rtl", "bdo dir=\"rtl\" preserved (bidi is the renderer's job)");
        Check.That((bdo?.InnerText ?? "") == "txet", "bdo content kept");
        Check.That(doc.AllTags("bdo").Count == 2, "second bdo without dir also parses");
        Check.Done();
    }

    [Fact]
    public void NewHtml401InlineElementsParse()
    {
        var doc = Parse("<html><body>" +
                        "<abbr title=\"HyperText Markup Language\">HTML</abbr> " +
                        "<acronym title=\"File Transfer Protocol\">FTP</acronym> " +
                        "<q cite=\"http://x.test/q\">quoted</q> " +
                        "<span class=\"note\">span text</span>" +
                        "<ins datetime=\"1999-12-24\">inserted</ins>" +
                        "<del>deleted</del>" +
                        "<label for=\"user\">User name</label><input id=\"user\" type=\"text\">" +
                        "<label>Wrapped <input type=\"checkbox\" id=\"agree\"></label>" +
                        "<fieldset><legend>Personal data</legend><input name=\"nm\"></fieldset>" +
                        "</body></html>");

        foreach (var (tag, content) in new[]
        {
            ("abbr", "HTML"), ("acronym", "FTP"), ("q", "quoted"),
            ("span", "span text"), ("ins", "inserted"), ("del", "deleted"),
            ("label", "User name")
        })
        {
            var el = doc.AllTags(tag).FirstOrDefault();
            Check.That(el != null, $"<{tag}> element exists");
            Check.That(el != null && (el.InnerText ?? "").Contains(content),
                $"<{tag}> content preserved");
        }

        Check.That(doc.FirstTag("abbr")?.GetAttr("title")?.StartsWith("Hyper") == true,
            "abbr title attribute kept");
        Check.That(doc.FirstTag("q")?.GetAttr("cite") == "http://x.test/q",
            "q cite attribute kept");
        Check.That(doc.FirstTag("ins")?.GetAttr("datetime") == "1999-12-24",
            "ins datetime attribute kept");
        Check.That(doc.FirstTag("label")?.GetAttr("for") == "user",
            "label for=id attribute kept (label→control association is derivable from the DOM)");

        // Label wrapping a control associates structurally (control is a descendant).
        var wrapped = doc.AllTags("label").FirstOrDefault(l => l.GetAttr("for") == null);
        var wrappedInput = wrapped?.ElementChildren().FirstOrDefault(e => e.TagName == "input");
        Check.That(wrappedInput?.GetAttr("id") == "agree",
            "a label wrapping a control keeps the control as a descendant");

        var legend = doc.FirstTag("legend");
        Check.That(ParentTag(legend) == "fieldset", "legend nests inside its fieldset");
        Check.That(legend != null &&
                   doc.FirstTag("fieldset")?.ElementChildren().FirstOrDefault() == legend,
            "legend is the first element child of the fieldset");
        Check.Done();
    }

    [Fact]
    public void RubyRtRpParseAsInlineElements()
    {
        var doc = Parse("<html><body><p>" +
                        "<ruby>漢<rt>kan</rt><rp>(</rp>字<rp>)</rp></ruby>" +
                        "</p></body></html>");
        var ruby = doc.FirstTag("ruby");
        Check.That(ruby != null, "<ruby> element exists");
        Check.That(doc.AllTags("rt").Count == 1 && doc.AllTags("rp").Count == 2,
            "rt/rp children parse as elements");
        Check.That(doc.FirstTag("rt")?.Parent == ruby &&
                   doc.AllTags("rp").All(rp => rp.Parent == ruby),
            "rt/rp stay inside the ruby annotation");
        Check.That((doc.FirstTag("rt")?.InnerText ?? "") == "kan",
            "rt holds the annotation text");
        Check.That((ruby?.InnerText ?? "").Contains("漢"), "ruby base text kept");
        Check.Done();
    }

    // ── optgroup / option implied ends (§14) ─────────────────────────

    [Fact]
    public void OptgroupImpliedEndsKeepOptionsInTheirGroups()
    {
        var doc = Parse("<html><body><form><select name=\"parts\">" +
                        "<option value=\"0\">-- pick --</option>" +
                        "<optgroup label=\"Engines\"><option value=\"e1\">four-cylinder<option value=\"e2\">six-cylinder" +
                        "<optgroup label=\"Colors\"><option value=\"c1\">red</option><option label=\"Cyan\" value=\"c2\">blue" +
                        "</optgroup>" +
                        "<option value=\"x\">loose option</option>" +
                        "</select></form></body></html>");

        var groups = doc.AllTags("optgroup");
        Check.That(groups.Count == 2, "two optgroups parsed", groups.Count.ToString());
        Check.That(groups.Count == 2 &&
                   ParentTag(groups[0]) == "select" &&
                   ParentTag(groups[1]) == "select",
            "optgroups are siblings inside the select (implied end before the next group)");
        Check.That(groups.Count == 2 &&
                   groups[0].ElementChildren().Select(e => e.TagName).All(t => t == "option"),
            "optgroup children stay options (no foster parenting out of the group)");

        Check.That(groups.Count > 0 &&
                   groups[0].ElementDescendants().Count(e => e.TagName == "option") == 2 &&
                   groups[0].ElementChildren().All(o => o.TagName == "option" || o.Parent == groups[0]),
            "unclosed options after <optgroup> stay inside their group");
        Check.That(groups.Count > 1 &&
                   groups[1].ElementDescendants().Count(e => e.TagName == "option") == 2 &&
                   groups[1].ElementChildren().All(o => o.TagName == "option"),
            "options after the second implied </optgroup> land in the second group");

        var firstLoose = doc.AllTags("option").FirstOrDefault(o => o.GetAttr("value") == "0");
        Check.That(ParentTag(firstLoose) == "select",
            "the option before any group is a direct select child");

        var afterClose = doc.AllTags("option").FirstOrDefault(o => o.GetAttr("value") == "x");
        Check.That(ParentTag(afterClose) == "select",
            "an option after </optgroup> returns to the select");

        var labeled = doc.AllTags("option").FirstOrDefault(o => o.GetAttr("value") == "c2");
        Check.That(labeled?.GetAttr("label") == "Cyan",
            "option label attribute accepted (§14)");
        Check.That(groups.Count > 0 && groups[0].GetAttr("label") == "Engines",
            "optgroup label attribute preserved");
        Check.Done();
    }

    // ── entities: the HTML 4.01 appendix additions ────────────────────

    [Fact]
    public void Html401GreekEntitiesDecode()
    {
        var cases = new (string Entity, string Expected)[]
        {
            ("&Alpha;", "\u0391"), ("&Beta;", "\u0392"), ("&Gamma;", "\u0393"),
            ("&Delta;", "\u0394"), ("&Omega;", "\u03A9"), ("&Sigma;", "\u03A3"),
            ("&alpha;", "\u03B1"), ("&beta;", "\u03B2"), ("&gamma;", "\u03B3"),
            ("&delta;", "\u03B4"), ("&pi;", "\u03C0"), ("&omega;", "\u03C9"),
            ("&sigmaf;", "\u03C2"), ("&sigma;", "\u03C3"), ("&thetasym;", "\u03D1"),
            ("&piv;", "\u03D6"), ("&upsih;", "\u03D2"),
        };
        foreach (var (entity, expected) in cases)
        {
            string decoded = HtmlEntities.Decode($"x{entity}y");
            Check.That(decoded == $"x{expected}y",
                $"Greek entity {entity} → U+{(int)expected[0]:X4}",
                $"got '{decoded}'");
        }
        // Case sensitivity: &omega; and &Omega; are different letters.
        Check.That(HtmlEntities.Decode("&Omega;") != HtmlEntities.Decode("&omega;"),
            "Greek capitals and lowercase stay distinct");
        Check.Done();
    }

    [Fact]
    public void Html401SpecialAndSymbolEntitiesDecode()
    {
        var cases = new (string Entity, string Expected)[]
        {
            ("&ensp;", "\u2002"), ("&emsp;", "\u2003"), ("&thinsp;", "\u2009"),
            ("&zwnj;", "\u200C"), ("&zwj;", "\u200D"), ("&lrm;", "\u200E"),
            ("&rlm;", "\u200F"), ("&ndash;", "\u2013"), ("&mdash;", "\u2014"),
            ("&lsquo;", "\u2018"), ("&rsquo;", "\u2019"), ("&ldquo;", "\u201C"),
            ("&rdquo;", "\u201D"), ("&sbquo;", "\u201A"), ("&bdquo;", "\u201E"),
            ("&lsaquo;", "\u2039"), ("&rsaquo;", "\u203A"), ("&bull;", "\u2022"),
            ("&hellip;", "\u2026"), ("&trade;", "\u2122"), ("&euro;", "\u20AC"),
            ("&fnof;", "\u0192"), ("&weierp;", "\u2118"), ("&alefsym;", "\u2135"),
            ("&lArr;", "\u21D0"), ("&hArr;", "\u21D4"), ("&nsub;", "\u2284"),
            ("&dagger;", "\u2020"), ("&permil;", "\u2030"), ("&prime;", "\u2032"),
        };
        foreach (var (entity, expected) in cases)
        {
            string decoded = HtmlEntities.Decode($"a{entity}b");
            Check.That(decoded == $"a{expected}b",
                $"4.01 entity {entity} → U+{(int)expected[0]:X4}",
                $"got '{decoded}'");
        }
        // Through the full parse pipeline as well.
        var doc = Parse("<html><body><p>&alpha; &Gamma; &Omega; &euro; &mdash; &hellip;</p></body></html>");
        Check.That((doc.FirstTag("p")?.InnerText ?? "").Contains("\u03B1") &&
                  (doc.FirstTag("p")?.InnerText ?? "").Contains("\u0393") &&
                  (doc.FirstTag("p")?.InnerText ?? "").Contains("\u20AC"),
            "new entities decode through a full document parse");
        Check.Done();
    }

    // ── DOCTYPE flavours (§4) ─────────────────────────────────────────

    [Fact]
    public void Doctype401StrictTransitionalFramesetRecorded()
    {
        var strict = Parse("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\" " +
                           "\"http://www.w3.org/TR/html4/strict.dtd\">" +
                           "<html><body><p>strict</p></body></html>");
        Check.That(strict.QuirksMode == "strict",
            "HTML 4.01 Strict doctype → standards (strict) mode", strict.QuirksMode);
        Check.That(strict.Children.OfType<DomDoctype>().Count() == 1,
            "the DomDoctype node is recorded");
        Check.That(strict.Children.OfType<DomDoctype>().First().RawText.Contains("-//W3C//DTD HTML 4.01//EN"),
            "the doctype raw text (public identifier) is kept for inspection");

        var transitional = Parse("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\" " +
                                 "\"http://www.w3.org/TR/html4/loose.dtd\">" +
                                 "<html><body><p>loose</p></body></html>");
        Check.That(transitional.QuirksMode == "quirks",
            "HTML 4.01 Transitional doctype → quirks mode", transitional.QuirksMode);

        var frameset = Parse("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.0 Frameset//EN\" " +
                             "\"http://www.w3.org/TR/html4/frameset.dtd\">" +
                             "<html><frameset cols=\"50%,50%\"><frame src=\"a.html\"><frame src=\"b.html\"></frameset></html>");
        Check.That(frameset.QuirksMode == "quirks",
            "HTML 4.0 Frameset doctype → quirks mode", frameset.QuirksMode);
        Check.That(frameset.FirstTag("frameset") != null,
            "frameset still parses under a 4.0 Frameset doctype");

        var bare4 = Parse("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\">" +
                          "<html><body><p>bare</p></body></html>");
        Check.That(bare4.QuirksMode == "strict",
            "HTML 4.01 Strict without the system identifier is still strict", bare4.QuirksMode);
        Check.Done();
    }

    // ── plaintext / xmp raw text (§14b) ──────────────────────────────

    [Fact]
    public void PlaintextAndXmpAreRawText()
    {
        // XMP: raw text until </xmp> — markup inside is literal text.
        var doc = Parse("<html><body><xmp><b>not bold</b> &amp; literal</xmp><p>after</p></body></html>");
        var xmp = doc.FirstTag("xmp");
        Check.That(xmp != null, "<xmp> element exists");
        var xmpText = xmp?.Children.OfType<DomText>().FirstOrDefault()?.Data ?? "";
        Check.That(xmpText.Contains("<b>not bold</b>"),
            "xmp content stays literal (markup not parsed)", xmpText);
        Check.That(xmpText.Contains("&amp;"),
            "xmp is raw text, not RCDATA — entities stay literal", xmpText);
        Check.That(doc.AllTags("b").Count == 0, "no <b> element from inside xmp");
        Check.That(doc.FirstTag("p")?.InnerText == "after", "parsing resumes after </xmp>");

        // PLAINTEXT: everything to EOF is literal — including tag-lookalikes.
        doc = Parse("<html><body><p>before</p><plaintext><b>everything</b> after is <i>literal</i>");
        Check.That(doc.AllTags("b").Count == 0 && doc.AllTags("i").Count == 0,
            "no elements materialize after <plaintext>");
        var body = doc.FirstTag("body")!;
        Check.That((body.InnerText ?? "").Contains("<b>everything</b> after is <i>literal</i>"),
            "the rest of the document is literal text",
            body.InnerText ?? "");
        Check.Done();
    }

    [Fact]
    public void StyleRawTextEndsAtFirstEndTagOpenDelimiter()
    {
        var tokens = HtmlTokenizer.Tokenize(
            "<style>.red { color: red; }</hello>.ineffective { color: red; }</style>")
            .ToList();

        int styleEnd = tokens.FindIndex(token => token is EndTag { Name: "style" });
        int strayEnd = tokens.FindIndex(token => token is EndTag { Name: "hello" });
        Check.That(styleEnd > 0 && strayEnd > styleEnd,
            "a literal </ sequence terminates STYLE before the unmatched end tag");
        Check.That(tokens.Take(styleEnd)
                .OfType<TextToken>()
                .All(text => !text.Data.Contains(".ineffective", StringComparison.Ordinal)),
            "CSS after the first </ sequence is not part of STYLE raw text");
        Check.Done();
    }

    // ── textarea wrap / basefont color (§5, §14a) ────────────────────

    [Fact]
    public void TextareaWrapAndBasefontColorPreserved()
    {
        var doc = Parse("<html><body>" +
                        "<textarea name=\"notes\" wrap=\"hard\" rows=\"20\" cols=\"40\">line1\nline2</textarea>" +
                        "<textarea wrap=\"off\">plain</textarea>" +
                        "<basefont color=\"#FF0000\" size=\"4\" face=\"Arial\">" +
                        "<p>text</p></body></html>");

        var ta = doc.AllTags("textarea").FirstOrDefault();
        Check.That(ta?.GetAttr("wrap") == "hard", "textarea wrap=\"hard\" preserved (§5)");
        Check.That(doc.AllTags("textarea").Count > 1 &&
                   doc.AllTags("textarea")[1].GetAttr("wrap") == "off",
            "textarea wrap=\"off\" preserved");
        var bf = doc.FirstTag("basefont");
        Check.That(bf?.GetAttr("color") == "#FF0000", "basefont color preserved (§14a)");
        Check.That(bf?.GetAttr("size") == "4" && bf?.GetAttr("face") == "Arial",
            "basefont size/face preserved");
        Check.That(bf?.Children.Count == 0, "basefont stays void");
        Check.Done();
    }
}
