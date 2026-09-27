using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;

namespace RetroTests;

public class LegacyHtmlMidiTests
{
    private static DomDocument Parse(string html) =>
        HtmlParser.Parse(html, ParsedUrl.Parse("http://x.test/index.html"), new CookieStore());

    [Fact]
    public void BothLegacyTagsParseAndAreRetainedAsSeparateDomElements()
    {
        var doc = Parse("<html><body><embed src='a.mid' autostart='false' loop='3' volume='80' width='145' height='60'><bgsound src='a.mid' loop='1'></body></html>");
        var embeds = doc.ElementDescendants().Where(e => e.TagName == "embed").ToList();
        var sounds = doc.ElementDescendants().Where(e => e.TagName == "bgsound").ToList();
        Check.That(embeds.Count == 1, "embed tag survives parsing");
        Check.That(sounds.Count == 1, "bgsound tag survives parsing");
        Check.That(embeds[0].GetAttr("autostart") == "false", "embed autostart preserved");
        Check.That(sounds[0].GetAttr("loop") == "1", "bgsound loop preserved");
        Check.Done();
    }

    [Fact]
    public void LegacySpacersReserveHorizontalAndBlockFlowSpace()
    {
        InlineLayout.SetFontCache(new Retro96.Engine.Render.FontCache());
        var doc = Parse(
            "<html><body><p>before <spacer type='horizontal' size='40'> after " +
            "<spacer type='BLOCK' width='200' height='30'> done.</p></body></html>");
        StyleResolver.Resolve(doc, 800);
        var root = LayoutEngine.BuildLayoutTree(doc, 800, 600);

        var spacers = doc.ElementDescendants().Where(e => e.TagName == "spacer").ToList();
        Check.That(spacers.Count == 2, "both legacy spacers remain in the DOM", spacers.Count.ToString());

        var h = spacers.Count > 0 ? root.Descendants().FirstOrDefault(b => b.Element == spacers[0]) : null;
        var block = spacers.Count > 1 ? root.Descendants().FirstOrDefault(b => b.Element == spacers[1]) : null;
        var done = root.Descendants().FirstOrDefault(b => b.TextRun?.Contains("done.", StringComparison.Ordinal) == true);

        Check.That(h != null && h.Width >= 40f, "horizontal spacer reserves its authored width");
        Check.That(block != null && block.Height >= 30f, "block spacer reserves its authored height");
        Check.That(block != null && done != null && done.Y >= block.Y + block.Height - 0.5f,
            "content after a block spacer is laid out below the spacer");
        Check.Done();
    }


    [Fact]
    public void OversizedTableCellFloatMovesFollowingTextBelowTheFloat()
    {
        InlineLayout.SetFontCache(new Retro96.Engine.Render.FontCache());
        var doc = Parse(
            "<html><body><table width='200'><tr>" +
            "<td width='80'><img src='float.gif' width='200' height='150' align='left'>" +
            "A 200x150 float inside the cell.</td>" +
            "<td>Neighbouring cell.</td></tr></table></body></html>");
        StyleResolver.Resolve(doc, 800);
        var root = LayoutEngine.BuildLayoutTree(doc, 800, 600);

        var img = doc.ElementDescendants().First(e => e.TagName == "img");
        var imageBox = root.Descendants().FirstOrDefault(b => b.Element == img);
        var textBox = root.Descendants().FirstOrDefault(b => b.TextRun?.Contains("200x150", StringComparison.Ordinal) == true);

        Check.That(imageBox != null, "oversized cell float has a layout box");
        Check.That(textBox != null, "text following oversized cell float remains in the layout");
        Check.That(imageBox != null && textBox != null &&
                   textBox.Y >= imageBox.Y + imageBox.Height - 0.5f,
            "following text starts below a float that consumes the cell's available inline width");
        Check.Done();
    }

    [Fact]
    public void BgsoundProducesNoLayoutBoxAndEmbedUsesRequestedPluginSpace()
    {
        InlineLayout.SetFontCache(new Retro96.Engine.Render.FontCache());
        var doc = Parse("<html><body><bgsound src='a.mid' loop='1'><embed src='a.mid' width='145' height='60'></body></html>");
        StyleResolver.Resolve(doc, 800);
        var root = LayoutEngine.BuildLayoutTree(doc, 800, 600);
        var embed = doc.ElementDescendants().First(e => e.TagName == "embed");
        var bgsound = doc.ElementDescendants().First(e => e.TagName == "bgsound");
        var embedBox = root.Descendants().FirstOrDefault(b => b.Element == embed);
        var bgBox = root.Descendants().FirstOrDefault(b => b.Element == bgsound);
        Check.That(embedBox != null, "embed gets a replaced layout box");
        Check.That(embedBox != null && Math.Abs(embedBox.Width - 145f) < 0.1f && Math.Abs(embedBox.Height - 60f) < 0.1f,
            "embed reserves its declared width × height");
        Check.That(bgBox == null, "bgsound remains invisible and non-layout");
        Check.Done();
    }
}
