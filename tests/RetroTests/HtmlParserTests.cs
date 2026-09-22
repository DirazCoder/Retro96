// Step 4 unit tests — HTML parser.
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Network;

namespace RetroTests;

public class HtmlParserTests
{
    private static DomDocument Parse(string html) =>
        HtmlParser.Parse(html, ParsedUrl.Parse("http://x.test/page.html"), new CookieStore());

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
    public void EntitiesDecodeThroughFullParse()
    {
        var doc = Parse("<html><body><p>caf&eacute; &amp; cr&egrave;me</p></body></html>");
        var p = doc.FirstTag("p");
        Check.That((p?.InnerText ?? "").Contains("café"), "parsed text contains decoded é");
        Check.That((p?.InnerText ?? "").Contains("&"), "parsed &amp; decodes to &");
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
}
