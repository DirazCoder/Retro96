using Retro96.Engine.Dom;
using Retro96.Engine.Network;
using Retro96.Engine.Render;

namespace RetroTests;

public class ScratchDiag
{
    [Fact]
    public void Diag2()
    {
        // Bug_InlineLabelBaselineDrop: box geometry of the bare-label case
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><form>http: <input type=\"text\" name=\"u\" size=\"10\"></form></body></html>");
        foreach (var b in root.Descendants().Where(b => b.Y < 90))
            Console.WriteLine($"  box <{b.Element?.TagName ?? "text"}> '{b.TextRun}' " +
                $"X={b.X:0.#} Y={b.Y:0.#} W={b.Width:0.#} H={b.Height:0.#} " +
                $"baseline-ish bottom={b.Y + b.Height:0.#}");

        // Bug_NestedTableLinkRowDoubleHeight: what makes the stride 50?
        Console.WriteLine("\n--- nested table rows ---");
        var (doc2, root2) = LayoutHarness.Parse(
            "<html><body><table>" +
            "<tr><td><center><table width=\"100%\">" +
            "<tr><td>" + "<img src=\"data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7\" width=\"9\" height=\"9\">" + "<a href=\"#l1\">Link number 1</a></td></tr>" +
            "<tr><td>" + "<img src=\"data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7\" width=\"9\" height=\"9\">" + "<a href=\"#l2\">Link number 2</a></td></tr>" +
            "</table></center></td></tr></table></body></html>");
        foreach (var b in root2.Descendants().Where(b => b.Width > 0 || b.Height > 0).Take(25))
        {
            string t = b.TextRun ?? "";
            if (t.Length > 12) t = t[..12];
            Console.WriteLine($"  box <{b.Element?.TagName ?? "text"}> '{t}' " +
                $"X={b.X:0.#} Y={b.Y:0.#} W={b.Width:0.#} H={b.Height:0.#} " +
                $"padT={b.PaddingTop:0.#} padB={b.PaddingBottom:0.#} borderT={b.BorderTop:0.#} borderB={b.BorderBottom:0.#}");
        }
        Assert.True(true);
    }

    [Fact]
    public void Diag()
    {
        // Bug_TrailingSpaceUnderlineStub diagnosis: WHERE is the ink?
        string img = "<img src=\"data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7\" width=\"28\" height=\"12\" border=\"0\">";
        var (doc, root) = LayoutHarness.Parse(
            $"<html><body bgcolor=\"#ffffff\"><p>x <a href=\"x.html\">{img} </a> y</p></body></html>");

        var imgEl = doc.ElementDescendants().First(e => e.TagName == "img");
        var imgBox = LayoutHarness.BoxOf(root, imgEl);
        var aEl = doc.ElementDescendants().First(e => e.TagName == "a");
        var aBox = LayoutHarness.BoxOf(root, aEl);
        Console.WriteLine($"img box: X={imgBox?.X} Y={imgBox?.Y} W={imgBox?.Width} H={imgBox?.Height} border={imgBox?.BorderRight} padding={imgBox?.PaddingRight}");
        Console.WriteLine($"img borderRect: {imgBox?.BorderRect}");
        Console.WriteLine($"a  borderRect: {aBox?.BorderRect}");

        // every box near the image
        foreach (var b in root.Descendants().Where(b => b.Y < (imgBox?.Y ?? 0) + 40 && b.Width >= 0).Take(20))
            Console.WriteLine($"  box <{b.Element?.TagName ?? "text"}> '{b.TextRun?.Replace(" ", "·")}' " +
                $"X={b.X:0.#} Y={b.Y:0.#} W={b.Width:0.#} H={b.Height:0.#} deco={b.Element?.Style?.TextDecoration}");

        var cache = new Retro96.Engine.Render.ImageCache { CookieStore = new() };
        using var loader = new Retro96.Engine.Network.ResourceLoader(new());
        using var bmp = LayoutHarness.Render(doc, root, cache, loader);
        if (imgBox != null)
        {
            float right = imgBox.X + imgBox.Width + imgBox.BorderRight;
            for (int y = Math.Max(0, (int)imgBox.Y - 2); y < Math.Min((int)(imgBox.Y + imgBox.Height + 6), bmp.Height); y++)
                for (int x = (int)right + 1; x < Math.Min((int)(right + 20), bmp.Width); x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.A > 40 && (c.R < 200 || c.G < 200 || c.B < 200))
                        Console.WriteLine($"INK at ({x},{y}) rgba=({c.R},{c.G},{c.B},{c.A})");
                }
        }
        Assert.True(true);
    }
}
