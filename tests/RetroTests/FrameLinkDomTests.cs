// Step 4 unit tests — frames (parse contract) and link/text-decoration
// DOM-level behaviour. Geometry lives in LayoutTests.
using Retro96.Engine.Dom;
using Retro96.Drawing;
using Retro96.Engine.Html;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Css;

namespace RetroTests;

public class FrameLinkDomTests
{
    // ── frames ───────────────────────────────────────────────────────

    [Fact]
    public void ColsFramesetParses()
    {
        var doc = HtmlParser.Parse(
            "<html><frameset cols='200,*'><frame name=left src='a.html'><frame name=right src='b.html'></frameset></html>",
            ParsedUrl.Parse("http://x.test/index.html"), new CookieStore());
        var fs = doc.FirstTag("frameset");
        Check.That(fs?.GetAttr("cols") == "200,*", "cols='200,*' preserved on frameset");
        var frames = doc.AllTags("frame");
        Check.That(frames.Count == 2, "two <frame> elements", frames.Count.ToString());
        Check.That(frames[0].GetAttr("name") == "left" && frames[1].GetAttr("name") == "right",
            "frame names parsed");
        Check.That(frames[0].GetAttr("src") == "a.html", "frame src preserved");
        Check.Done();
    }

    [Fact]
    public void RowsFramesetParses()
    {
        var doc = HtmlParser.Parse(
            "<html><frameset rows='100,*'><frame src='top.html'><frame src='bot.html'></frameset></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        Check.That(doc.FirstTag("frameset")?.GetAttr("rows") == "100,*", "rows='100,*' preserved");
        Check.That(doc.AllTags("frame").Count == 2, "two frames parsed");
        Check.Done();
    }

    [Fact]
    public void NoresizeFlagIsStored()
    {
        var doc = HtmlParser.Parse(
            "<html><frameset cols='50%,*'><frame src=a.html noresize scrolling=no><frame src=b.html></frameset></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        var f = doc.AllTags("frame")[0];
        Check.That(f.HasAttr("noresize"), "noresize flag stored");
        Check.That(f.GetAttr("scrolling") == "no", "scrolling=no preserved");
        Check.Done();
    }

    [Fact]
    public void NoframesNotLaidOutWhenFramesOn()
    {
        var doc = HtmlParser.Parse(
            "<html><frameset cols='50%,*'><frame src=a.html><frame src=b.html>" +
            "<noframes><body><p>fallback</p></body></noframes></frameset></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        StyleResolver.Resolve(doc, 800);
        var noframes = doc.FirstTag("noframes");
        Check.That(noframes != null, "<noframes> present in DOM");
        // The engine hides noframes at the LAYOUT layer (IsNonVisualTag),
        // not via computed style — assert no boxes instead of display:none.
        var root = LayoutEngine.BuildLayoutTree(doc, 800, 600);
        int boxes = root.Descendants().Count(b => b.Element == noframes);
        Check.That(boxes == 0, "noframes produces no boxes when frames supported",
            $"{boxes} boxes");
        Check.Done();
    }

    [Fact]
    public void IframeAttributesPreserved()
    {
        var doc = HtmlParser.Parse(
            "<html><body><iframe src='inner.html' width=300 height=200 frameborder=1></iframe></body></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        var iframe = doc.FirstTag("iframe");
        Check.That(iframe != null, "<iframe> parsed");
        Check.That(iframe?.GetAttr("width") == "300" && iframe?.GetAttr("height") == "200",
            "iframe width=300 height=200 preserved");
        Check.Done();
    }

    // ── link colours (body attributes → StyleResolver) ───────────────

    [Fact]
    public void BodyLinkColorsParse()
    {
        var doc = HtmlParser.Parse(
            "<html><body link='#ff0000' vlink='#888888' alink='#00ff00'><a href='http://x.test/1'>one</a></body></html>",
            ParsedUrl.Parse("http://x.test/page.html"), new CookieStore());
        StyleResolver.Resolve(doc, 800);

        Check.That(StyleResolver.GetLinkColor(doc) == Color.FromArgb(255, 0, 0),
            "body link=#ff0000 → links red", StyleResolver.GetLinkColor(doc).ToString());
        Check.That(StyleResolver.GetVLinkColor(doc) == Color.FromArgb(0x88, 0x88, 0x88),
            "body vlink=#888888 → visited grey", StyleResolver.GetVLinkColor(doc).ToString());
        Check.That(StyleResolver.GetALinkColor(doc) == Color.FromArgb(0, 255, 0),
            "body alink=#00ff00 → active green");
        Check.Done();
    }

    [Fact]
    public void LinkElementsGetUnderlineAndHrefsDont()
    {
        var doc = HtmlParser.Parse(
            "<html><body>" +
            "<a href='http://x.test/'>real link</a>" +
            "<a name='anchor'>named anchor</a>" +
            "</body></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        StyleResolver.Resolve(doc, 800);

        var link = doc.AllTags("a").First(a => a.HasAttr("href"));
        var anchor = doc.AllTags("a").First(a => a.HasAttr("name"));

        Check.That(link.Style?.TextDecoration.HasFlag(TextDecoration.Underline) == true,
            "<a href> gets underline");
        Check.That(anchor.Style?.TextDecoration.HasFlag(TextDecoration.Underline) != true,
            "<a name=> (target) has no underline");
        Check.Done();
    }

    [Fact]
    public void MailtoAndJavascriptLinksParse()
    {
        var doc = HtmlParser.Parse(
            "<html><body>" +
            "<a href='mailto:webmaster@x.test'>mail</a>" +
            "<a href='javascript:void(0)'>js</a>" +
            "<a href='page2.html'>bare</a>" +
            "</body></html>",
            ParsedUrl.Parse("http://x.test/dir/page.html"), new CookieStore());
        var links = doc.AllTags("a");
        Check.That(links[0].GetAttr("href") == "mailto:webmaster@x.test", "mailto: href preserved");
        Check.That(links[1].GetAttr("href") == "javascript:void(0)", "javascript: href preserved");
        var resolved = doc.BaseUrl!.Resolve(links[2].GetAttr("href")!);
        Check.That(resolved.ToAbsolute() == "http://x.test/dir/page2.html",
            "bare relative href resolves against page dir", resolved.ToAbsolute());
        Check.Done();
    }
}
