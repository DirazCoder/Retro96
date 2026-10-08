// Regression tests for the three engine bugs found by the EXTENDED
// VisualDiff campaign (every HTML in the project — the 26 QA pages plus
// the 11 real-world 1996 corpus pages in testdata/).  The corpus was the
// trigger: dolekemp96.html exposed the margin double-count and the
// &nbsp; paragraph collapse; voyagersisland.html exposed the
// dimensionless-image 32×32 stamp.
using Retro96.Engine;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;
using Retro96.Engine.Render;
using Retro96.Engine.Html;

namespace RetroTests;

public class ExtendedCorpusRegressionTests
{
    // AB-13 ────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_NbspOnlyParagraphRendersBlankLine()
    {
        // <p>&nbsp;</p> is THE canonical 1996 vertical-spacing idiom (the
        // Dole/Kemp '96 page uses it twice).  .NET's char.IsWhiteSpace
        // classes U+00A0 as whitespace, so the whitespace-run check ate
        // these paragraphs whole — zero height, invisible.
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<p>first</p>" +
            "<p>&nbsp;</p>" +
            "<p>second</p>" +
            "</body></html>");

        var ps = doc.ElementDescendants().Where(e => e.TagName == "p").ToList();
        Check.That(ps.Count == 3, "three paragraphs parsed", $"count={ps.Count}");

        var nbspBox = LayoutHarness.BoxOf(root, ps[1]);
        Check.That(nbspBox != null, "the &nbsp; paragraph produced a box");
        Check.That(nbspBox!.BorderRect.Height >= 12f,
            "<p>&nbsp;</p> renders a visible blank line",
            $"height = {nbspBox.BorderRect.Height:0.#}");

        // And it sits BETWEEN the other two, in document order.
        var first = LayoutHarness.BoxOf(root, ps[0])!;
        var second = LayoutHarness.BoxOf(root, ps[2])!;
        Check.That(nbspBox.BorderRect.Y > first.BorderRect.Bottom - 1f &&
                   second.BorderRect.Y > nbspBox.BorderRect.Bottom - 1f,
            "the blank line flows between its neighbours");
        Check.Done();
    }

    // AB-14 ────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_AdjacentSiblingMarginsCollapse()
    {
        // The flow loop added the previous child's margin-bottom to
        // currentY AND then applied max(prevMarginBottom, marginTop) on
        // top — every inter-paragraph gap was mb + max(mb, mt) (32px for
        // default paragraphs: TWO blank lines where every reference
        // renderer shows ONE).
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<p id=\"a\">one</p>\n   " +
            "<p id=\"b\">two</p>" +
            "<h3 id=\"c\">heading</h3>" +
            "<p id=\"d\">three</p>" +
            "</body></html>");

        var a = doc.ElementDescendants().First(e => e.GetAttr("id") == "a");
        var b = doc.ElementDescendants().First(e => e.GetAttr("id") == "b");
        var c = doc.ElementDescendants().First(e => e.GetAttr("id") == "c");
        var d = doc.ElementDescendants().First(e => e.GetAttr("id") == "d");

        var ab = LayoutHarness.BoxOf(root, a)!;
        var bb = LayoutHarness.BoxOf(root, b)!;
        var cb = LayoutHarness.BoxOf(root, c)!;
        var db = LayoutHarness.BoxOf(root, d)!;

        // p → p: gap must equal max(mb, mt) — whitespace between them must
        // NOT break the collapsing (the inter-p text node is whitespace-only).
        float expectPP = Math.Max(a.Style!.MarginBottom, b.Style!.MarginTop);
        float gapPP = bb.BorderRect.Y - ab.BorderRect.Bottom;
        Check.That(Math.Abs(gapPP - expectPP) < 1.5f,
            "p→p gap collapses to max(margin-bottom, margin-top)",
            $"gap={gapPP:0.#} expected={expectPP:0.#}");

        // h3 → p: different margins, same rule.
        float expectHP = Math.Max(c.Style!.MarginBottom, d.Style!.MarginTop);
        float gapHP = db.BorderRect.Y - cb.BorderRect.Bottom;
        Check.That(Math.Abs(gapHP - expectHP) < 1.5f,
            "h3→p gap collapses to max(margin-bottom, margin-top)",
            $"gap={gapHP:0.#} expected={expectHP:0.#}");

        // Sanity: the collapsed gap is one blank line, not two.
        Check.That(gapPP < 24f && gapPP > 8f,
            "inter-paragraph spacing is ONE blank line (16px class)",
            $"gap={gapPP:0.#}");
        Check.Done();
    }

    [Fact]
    public void ParagraphMarginCollapsesThroughListItemToFollowingItem()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><ol>" +
            "<li><p>first</p></li>" +
            "<li><p>second</p></li>" +
            "<li><p id='third'>third</p></li>" +
            "<li><a id='fourth' href='#'>fourth</a></li>" +
            "</ol></body></html>");
        var thirdParagraph = doc.ElementDescendants()
            .First(element => element.GetAttr("id") == "third");
        var thirdItem = thirdParagraph.Parent!;
        var fourthItem = doc.ElementDescendants()
            .First(element => element.TagName == "li" &&
                element.Descendants().OfType<DomElement>()
                    .Any(descendant => descendant.GetAttr("id") == "fourth"));

        var thirdItemBox = LayoutHarness.BoxOf(root, (DomElement)thirdItem)!;
        var fourthItemBox = LayoutHarness.BoxOf(root, fourthItem)!;
        float actualGap = fourthItemBox.BorderRect.Y - thirdItemBox.BorderRect.Bottom;
        float expectedGap = thirdParagraph.Style!.MarginBottom;
        Check.That(Math.Abs(actualGap - expectedGap) < 1.5f,
            "the final paragraph margin collapses through its list item",
            $"gap={actualGap:0.#} expected={expectedGap:0.#}");
        Check.Done();
    }

    // AB-15 ────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_DimensionlessImgUsesNaturalSize()
    {
        // <img src=x> with no WIDTH/HEIGHT used to keep the 32×32
        // "placeholder until load" FOREVER — there was no after-load step.
        // voyagersisland's 468×60 banner exchange graphic rendered as a
        // stamp.  The layout now consults the NaturalImageSizes registry
        // the ImageCache fills on every successful decode.
        string banner = "http://127.0.0.1/banner.gif";   // doc base is page.html
        NaturalImageSizes.Register(banner, 468, 60);

        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<img src=\"banner.gif\">" +
            "</body></html>");
        var img = doc.ElementDescendants().First(e => e.TagName == "img");
        var box = LayoutHarness.BoxOf(root, img);
        Check.That(box != null, "dimensionless img produced a box");
        Check.That(Math.Abs(box!.BorderRect.Width - 468f) < 0.5f &&
                   Math.Abs(box.BorderRect.Height - 60f) < 0.5f,
            "no WIDTH/HEIGHT → natural size from the decoded image",
            $"box = {box.BorderRect.Width:0.#}x{box.BorderRect.Height:0.#}");
        Check.Done();
    }

    [Fact]
    public void Bug_OneDimensionImgScalesByAspect()
    {
        string banner = "http://127.0.0.1/banner2.gif";
        NaturalImageSizes.Register(banner, 468, 60);

        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<img src=\"banner2.gif\" width=\"234\">" +
            "</body></html>");
        var img = doc.ElementDescendants().First(e => e.TagName == "img");
        var box = LayoutHarness.BoxOf(root, img);
        Check.That(box != null, "img produced a box");
        Check.That(Math.Abs(box!.BorderRect.Width - 234f) < 0.5f,
            "given WIDTH is honoured");
        Check.That(Math.Abs(box.BorderRect.Height - 30f) < 0.5f,
            "missing HEIGHT scales by the natural aspect ratio (234 * 60/468 = 30)",
            $"height = {box.BorderRect.Height:0.#}");
        Check.Done();
    }

    [Fact]
    public void Bug_ImageCacheRegistersNaturalSize()
    {
        // The pipeline side of AB-15: a decoded image must publish its
        // natural size so a later layout pass can use it.
        var cache = new ImageCache { CookieStore = new CookieStore() };
        string data = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
        var decoded = cache.GetAsync(data, loader: null!,
            System.Threading.CancellationToken.None).Result;
        Check.That(decoded.Frames.Count > 0, "the 1x1 data URI decodes");
        Check.That(NaturalImageSizes.TryGetSize(data, out int w, out int h) &&
                   w == 1 && h == 1,
            "ImageCache registered the decoded natural size",
            $"registry says {w}x{h}");
        cache.Dispose();
        Check.Done();
    }
}
