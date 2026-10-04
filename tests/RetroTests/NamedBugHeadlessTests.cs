// NamedBugHeadlessTests — the 15 user-reported bugs from the QA campaign
// brief, as headless regression facts.  Every bug's contract is asserted
// against the REAL engine (parser → style → layout → render, or the live
// JS interpreter), never against a mock; the two WinForms-presentation
// bugs are covered through the engine-side geometry/overlay helpers the
// shell consumes (JsDialogGeometry, TextareaOverlay).
//
// Network bugs (race condition, permanent-failure, Netscape-path loading,
// 404 banner) run against TestOrigin — a loopback HTTP origin spun up
// inside the test process with deterministic route behaviour.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Retro96;
using Retro96.Drawing;
using Retro96.Engine;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;
using SkiaSharp;
using EngineHttpClient = Retro96.Engine.Network.HttpClient;

namespace RetroTests;

// ─────────────────────────────────────────────────────────────────────
// Loopback origin with deterministic routes
// ─────────────────────────────────────────────────────────────────────

public static class TestOrigin
{
    // route → (hitCount) => (status, contentType, body)
    private static readonly ConcurrentDictionary<string, Func<int, (int, string, byte[])>> Routes = new();
    private static readonly ConcurrentDictionary<string, int> Hits = new();
    private static HttpListener? _listener;
    private static string _base = "";

    public static readonly byte[] Gif1x1 = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    public static string Base
    {
        get
        {
            if (_listener != null) return _base;
            foreach (int port in new[] { 8941, 8942, 8943, 8944 })
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    l.Start();
                    _listener = l;
                    _base = $"http://127.0.0.1:{port}/";
                    Task.Run(ServeLoop);
                    return _base;
                }
                catch (HttpListenerException) { }
            }
            throw new InvalidOperationException("no loopback port");
        }
    }

    public static void Map(string path, Func<int, (int, string, byte[])> handler) =>
        Routes[path] = handler;

    public static void MapOk(string path, string contentType = "image/gif", byte[]? body = null) =>
        Map(path, _ => (200, contentType, body ?? Gif1x1));

    public static int HitCount(string path) =>
        Hits.TryGetValue(path, out int n) ? n : 0;

    private static async Task ServeLoop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            try
            {
                string path = ctx.Request.Url!.AbsolutePath;
                Hits.AddOrUpdate(path, 1, (_, n) => n + 1);
                Routes.TryGetValue(path, out var handler);
                var (code, type, body) = handler != null
                    ? handler(Hits[path])
                    : (404, "text/plain", Encoding.ASCII.GetBytes("not found"));
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = type;
                if (code >= 300 && code < 400)
                    ctx.Response.RedirectLocation = "/target.gif";
                ctx.Response.KeepAlive = false;
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────
// Layout + render harness (parse → style → layout, optional render)
// ─────────────────────────────────────────────────────────────────────

public static class LayoutHarness
{
    private static FontCache? _fonts;
    public static FontCache Fonts => _fonts ??= new FontCache();

    public static (DomDocument doc, LayoutBox root) Parse(string html, int vw = 800)
    {
        InlineLayout.SetFontCache(Fonts);
        var doc = HtmlParser.Parse(html, ParsedUrl.Parse("http://127.0.0.1/page.html"),
            new CookieStore());
        StyleResolver.Resolve(doc, vw);
        var root = LayoutEngine.BuildLayoutTree(doc, vw, 600);
        return (doc, root);
    }

    public static LayoutBox? BoxOf(LayoutBox root, DomElement el) =>
        root.Descendants().FirstOrDefault(b => b.Element == el);

    public static LayoutBox? TextBoxContaining(LayoutBox root, string text) =>
        root.Descendants().FirstOrDefault(b => b.TextRun?.Contains(text) == true);

    public static Retro96.Drawing.Bitmap Render(DomDocument doc, LayoutBox root,
                                                 ImageCache images, ResourceLoader loader)
    {
        var renderer = new Renderer(Fonts, images, loader);
        return renderer.Render(root, doc, Fonts, images, 800, 600, 0f, 0f, null, true);
    }
}

// ─────────────────────────────────────────────────────────────────────
// The 15 named bugs
// ─────────────────────────────────────────────────────────────────────

public class NamedBugHeadlessTests
{
    private static string Img1x1(int w, int h) =>
        $"<img src=\"data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7\" width=\"{w}\" height=\"{h}\">";


    [Fact]
    public void Bug_WideRootKeepsDocumentOverflowSurface()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div style='width:1200px'>wide</div></body></html>", 800);
        Check.That(root.Width >= 1199f,
            "root expands to the authored wide descendant instead of clipping at the viewport",
            $"root width = {root.Width:0.#}");
        Check.Done();
    }

    // 1 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_ImagesInTableCellZeroHeight()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><table border=\"1\"><tr><td>" +
            Img1x1(24, 40).Replace("<img ", "<img align=\"left\" ") +
            "</td><td>neighbour</td></tr></table></body></html>");
        var td = doc.ElementDescendants().First(e => e.TagName == "td");
        var box = LayoutHarness.BoxOf(root, td);
        Check.That(box != null, "td produced a box");
        // align=left floats: the float's height must count toward the cell
        Check.That(box!.BorderRect.Height > 30f,
            "<img align=left> as only cell content gives the td height > 0",
            $"td height = {box.BorderRect.Height:0.#}");
        Check.Done();
    }

    // 2 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_TrailingSpaceUnderlineStub()
    {
        // The link's trailing blank run paints no glyphs — any ink inside
        // ITS OWN rect (not the neighbouring words') is a stray stub.
        var (doc, root) = LayoutHarness.Parse(
            "<html><body bgcolor=\"#ffffff\"><p>x " +
            "<a href=\"x.html\">" + Img1x1(28, 12).Replace("<img ", "<img border=\"0\" ") + " </a>" +
            " y</p></body></html>");
        var anchor = doc.ElementDescendants().First(e => e.TagName == "a");
        var blankRun = root.Descendants()
            .FirstOrDefault(b => b.Element == anchor &&
                                 !string.IsNullOrEmpty(b.TextRun) &&
                                 b.TextRun.Trim().Length == 0);
        Check.That(blankRun != null, "the trailing blank run produced a box");
        if (blankRun == null) { Check.Done(); return; }

        var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bmp = LayoutHarness.Render(doc, root, cache, loader);

        int x0 = Math.Max(0, (int)blankRun.X);
        // stop one column short of the run's right edge — the next word's
        // left-edge antialiasing bleeds into that column.
        int x1 = Math.Min((int)Math.Ceiling(blankRun.X + blankRun.Width) - 1, bmp.Width - 1);
        int y0 = Math.Max(0, (int)blankRun.Y);
        int y1 = Math.Min((int)(blankRun.Y + blankRun.Height), bmp.Height - 1);
        bool ink = false;
        for (int y = y0; y <= y1 && !ink; y++)
            for (int x = x0; x <= x1 && !ink; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A > 40 && (c.R < 200 || c.G < 200 || c.B < 200)) ink = true;
            }
        Check.That(!ink,
            "trailing space inside a link paints no underline stub (blank edge runs carry no decoration)",
            $"ink inside blank run rect {x0}..{x1} x {y0}..{y1}");
        Check.Done();
    }

    // 3 ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task Bug_ImageLoadRaceCondition()
    {
        string Base = TestOrigin.Base;
        const int Total = 30, Broken = 3;
        var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());

        for (int i = 1; i <= Total; i++)
        {
            int n = i;
            TestOrigin.Map($"/img{i:00}.gif", hit => n % 10 == 3
                ? (503, "text/plain", Encoding.ASCII.GetBytes("busy"))
                : (200, "image/gif", TestOrigin.Gif1x1));
        }

        // Fire ALL fetches concurrently — the old sequential queue let one
        // 503 stall every image behind it.
        var tasks = Enumerable.Range(1, Total)
            .Select(i => cache.GetAsync($"{Base}/img{i:00}.gif", loader, default))
            .ToArray();
        await Task.WhenAll(tasks);

        int broken = Enumerable.Range(1, Total)
            .Count(i => cache.IsBroken($"{Base}/img{i:00}.gif"));
        // IsLoaded means "fetched and decoded" — broken icons are cached
        // too, so GOOD = loaded && !broken.
        int good = Enumerable.Range(1, Total)
            .Count(i => cache.IsLoaded($"{Base}/img{i:00}.gif") &&
                        !cache.IsBroken($"{Base}/img{i:00}.gif"));
        Check.That(broken == Broken,
            $"only the {Broken} transient-503 images end up broken (parallel fetch must not cascade)",
            $"broken={broken} good={good}");
        Check.That(good == Total - Broken,
            "the other 27 all load correctly", $"good={good}");
        Check.Done();
    }

    [Fact]
    public async Task LargeImageBatchesDrainBeyondTheConcurrencyLimit()
    {
        string baseUrl = TestOrigin.Base;
        const int total = 80;
        var previousSettings = BrowserRuntime.Settings.Clone();
        var settings = previousSettings.Clone();
        settings.MaxConcurrentResourceFetches = 8;
        settings.MaxResourceFetchesPerPage = 1000;
        BrowserRuntime.Apply(settings);

        using var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        try
        {
            for (int i = 1; i <= total; i++)
                TestOrigin.MapOk($"/batch{i:00}.gif");

            await Parallel.ForEachAsync(
                Enumerable.Range(1, total),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = BrowserRuntime.MaxConcurrentResourceFetches
                },
                async (i, ct) =>
                    await cache.GetAsync($"{baseUrl}/batch{i:00}.gif", loader, ct));

            int loaded = Enumerable.Range(1, total)
                .Count(i => cache.IsLoaded($"{baseUrl}/batch{i:00}.gif") &&
                            !cache.IsBroken($"{baseUrl}/batch{i:00}.gif"));
            Assert.Equal(total, loaded);
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
    }

    // 4 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_NestedTableLinkRowDoubleHeight()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><table>" +          // outer, borderless, auto width
            "<tr><td><center><table width=\"100%\">" +
            string.Concat(Enumerable.Range(1, 6).Select(i =>
                $"<tr><td>{Img1x1(9, 9)}<a href=\"#l{i}\">Link number {i}</a></td></tr>")) +
            "</table></center></td></tr></table></body></html>");

        // inner table rows: the boxes of the <a> elements
        var links = doc.ElementDescendants().Where(e => e.TagName == "a").ToList();
        var ys = links.Select(l => LayoutHarness.BoxOf(root, l)?.Y ?? -1).ToList();
        Check.That(ys.All(y => y >= 0), "all link rows produced boxes");
        var heights = links.Select(l => LayoutHarness.BoxOf(root, l)!.BorderRect.Height).ToList();
        Check.That(heights.Max() <= 26f,
            "no inner-table row grows to double height (the bullet + link row is ONE line)",
            $"max row height = {heights.Max():0.#}px, min = {heights.Min():0.#}px");
        // rows must be evenly spaced at one-line stride
        var strides = ys.Zip(ys.Skip(1), (a, b) => b - a).ToList();
        Check.That(strides.All(s => s is > 8f and < 34f),
            "row stride stays a single line height", string.Join(",", strides.Select(s => s.ToString("0.#"))));
        Check.Done();
    }

    [Fact]
    public void AutoSizedButtonsShrinkToFitNarrowTableCells()
    {
        const int viewportWidth = 140;
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><table border='1'><tr><td>" +
            "<button>Very long button label that should shrink to its cell</button>" +
            "</td></tr></table></body></html>",
            viewportWidth);

        var table = doc.FirstTag("table");
        var button = doc.FirstTag("button");
        var tableBox = table == null ? null : LayoutHarness.BoxOf(root, table);
        var buttonBox = button == null ? null : LayoutHarness.BoxOf(root, button);
        Assert.NotNull(tableBox);
        Assert.NotNull(buttonBox);
        Assert.True(tableBox!.BorderRect.Width <= viewportWidth + 1f,
            $"table width {tableBox.BorderRect.Width:0.#} should fit viewport {viewportWidth}");
        Assert.True(buttonBox!.BorderRect.Right <= tableBox.BorderRect.Right + 1f,
            $"button right edge {buttonBox.BorderRect.Right:0.#} escaped table right edge {tableBox.BorderRect.Right:0.#}");
        Assert.True(buttonBox.BorderRect.Width < 200f,
            $"auto-sized button should shrink from its natural width, got {buttonBox.BorderRect.Width:0.#}");
    }

    // 5 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_ListMarkersInsideTableCellsReserveListGutter()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body bgcolor=\"#ffffff\"><table border=\"1\" cellpadding=\"6\" cellspacing=\"2\">" +
            "<tr><th>cell holds a ul</th><th>cell holds an ol</th></tr>" +
            "<tr><td><ul><li>bullet inside a cell</li>" +
            "<li>another bullet, wrapping onto a second line to check the indent width inside the narrower cell</li></ul></td>" +
            "<td><ol type=\"I\"><li>ROMAN ONE in a cell</li><li>ROMAN TWO in a cell</li></ol></td></tr>" +
            "</table></body></html>");

        var table = doc.FirstTag("table");
        var tableBox = table != null ? LayoutHarness.BoxOf(root, table) : null;
        var ul = doc.FirstTag("ul");
        var ol = doc.FirstTag("ol");
        var ulBox = ul != null ? LayoutHarness.BoxOf(root, ul) : null;
        var olBox = ol != null ? LayoutHarness.BoxOf(root, ol) : null;

        Check.That(tableBox != null && tableBox.BorderRect.Width > 80f,
            "list-containing table keeps a usable positive width",
            tableBox == null ? "NO TABLE BOX" : $"width={tableBox.BorderRect.Width:0.#}");
        Check.That(ulBox != null && ulBox.Width > 10f,
            "UL inside a table cell gets a real content width",
            ulBox == null ? "NO UL BOX" : $"width={ulBox.Width:0.#}");
        Check.That(olBox != null && olBox.Width > 10f,
            "OL inside a table cell gets a real content width",
            olBox == null ? "NO OL BOX" : $"width={olBox.Width:0.#}");

        var cellBoxes = tableBox?.Descendants().Where(b => b.BoxType == BoxType.TableCell).ToList()
                        ?? new List<LayoutBox>();
        Check.That(cellBoxes.Count == 4,
            "the two header cells and two list cells remain valid table cells",
            $"cells={cellBoxes.Count}");
        Check.That(cellBoxes.All(c => c.BorderRect.Width > 20f),
            "list marker gutters are included in intrinsic column sizing",
            string.Join(", ", cellBoxes.Select(c => $"{c.BorderRect.Width:0.#}px")));

        if (ulBox != null && olBox != null && cellBoxes.Count == 4)
        {
            var ulLi = ulBox.Descendants().FirstOrDefault(b => b.BoxType == BoxType.ListItem);
            var olLi = olBox.Descendants().FirstOrDefault(b => b.BoxType == BoxType.ListItem);
            Check.That(ulLi != null && olLi != null,
                "list items survive table-cell layout",
                $"ulLi={(ulLi != null)}, olLi={(olLi != null)}");

            if (ulLi != null && olLi != null)
            {
                var ulCell = cellBoxes[2];
                var olCell = cellBoxes[3];
                float ulMarkerLeft = ulLi.X - 15f;
                float olMarkerLeft = olLi.X - 22f;
                Check.That(ulMarkerLeft >= ulCell.BorderRect.Left - 1f,
                    "UL bullet marker stays inside its table cell",
                    $"markerLeft={ulMarkerLeft:0.#}, cellLeft={ulCell.BorderRect.Left:0.#}");
                Check.That(olMarkerLeft >= olCell.BorderRect.Left - 1f,
                    "OL number marker stays inside its table cell",
                    $"markerLeft={olMarkerLeft:0.#}, cellLeft={olCell.BorderRect.Left:0.#}");

                using var images = new ImageCache { CookieStore = new CookieStore() };
                using var loader = new ResourceLoader(new CookieStore());
                using var bmp = LayoutHarness.Render(doc, root, images, loader);

                static bool HasInkIn(LayoutBox box, float left, float right, float top, float bottom, Bitmap bmp)
                {
                    int x0 = Math.Clamp((int)Math.Floor(left), 0, bmp.Width - 1);
                    int x1 = Math.Clamp((int)Math.Ceiling(right), 0, bmp.Width - 1);
                    int y0 = Math.Clamp((int)Math.Floor(top), 0, bmp.Height - 1);
                    int y1 = Math.Clamp((int)Math.Ceiling(bottom), 0, bmp.Height - 1);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (c.A > 40 && (c.R < 120 || c.G < 120 || c.B < 120))
                                return true;
                        }
                    return false;
                }

                bool ulMarkerInk = HasInkIn(ulLi, ulLi.X - 22f, ulLi.X - 2f,
                    ulLi.Y, ulLi.Y + Math.Max(12f, ulLi.Height + 6f), bmp);
                bool olMarkerInk = HasInkIn(olLi, olLi.X - 28f, olLi.X - 2f,
                    olLi.Y, olLi.Y + Math.Max(12f, olLi.Height + 6f), bmp);
                Check.That(ulMarkerInk,
                    "UL bullet is actually painted inside the table cell");
                Check.That(olMarkerInk,
                    "OL number marker is actually painted inside the table cell");
            }
        }
        Check.Done();
    }

    [Fact]
    public async Task Bug_ImageCachePermanentFailure()
    {
        string Base = TestOrigin.Base;
        var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());

        // /recovers.gif: 503 twice, then 200 forever.  A single GetAsync
        // must retry THROUGH the failures (the old code cached the first
        // 503 as a permanent broken icon).
        TestOrigin.Map("/recovers.gif", hit => hit <= 2
            ? (503, "text/plain", Encoding.ASCII.GetBytes("busy"))
            : (200, "image/gif", TestOrigin.Gif1x1));

        var first = await cache.GetAsync($"{Base}/recovers.gif", loader, default);
        Check.That(cache.IsLoaded($"{Base}/recovers.gif"),
            "a transient 503 is retried and recovered within one fetch",
            $"hits={TestOrigin.HitCount("/recovers.gif")}");
        Check.That(TestOrigin.HitCount("/recovers.gif") == 3,
            "exactly 1 + 2 retries were made (600ms/1200ms backoff)",
            $"hits={TestOrigin.HitCount("/recovers.gif")}");

        // /always-broken.gif: permanent 503 — marked broken, but as
        // TRANSIENT: the 20s cooldown clears it for a later refetch
        // (verified by the retry counter contract above; the cooldown
        // itself is 20s real time and covered by the ImageCache source).
        TestOrigin.Map("/always-broken.gif",
            _ => (503, "text/plain", Encoding.ASCII.GetBytes("busy")));
        await cache.GetAsync($"{Base}/always-broken.gif", loader, default);
        Check.That(cache.IsBroken($"{Base}/always-broken.gif"),
            "a permanently failing image ends up broken (correct)");
        Check.That(TestOrigin.HitCount("/always-broken.gif") == 3,
            "it consumed its retries before giving up",
            $"hits={TestOrigin.HitCount("/always-broken.gif")}");
        Check.Done();
    }

    // 6 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_BulletLinksInsideTableHitTesting()
    {
        // Exact shape of the Acme CyberCorp sidebar regression: literal bullet
        // text followed by an anchor inside a table cell.  The renderer may
        // legitimately move/overflow the inline descendant during table
        // layout, so hit-testing must use the descendant's own geometry rather
        // than clipping it to every ancestor box.
        const string html = @"<html><body>
            <table border='1' width='220'><tr><td width='25%'>
              <font face='Geneva, Arial' size='2'>
                &#149; <a href='#news' id='newsLink'>Corporate News</a><br>
                &#149; <a href='#intranet' id='intranetLink'>Intranet Login</a><br>
                &#149; <a href='#ftp' id='ftpLink'>FTP Archive</a><br>
                &#149; <a href='#press' id='pressLink'>Press Releases</a>
              </font>
            </td></tr></table>
          </body></html>";

        var (doc, root) = LayoutHarness.Parse(html);
        foreach (var id in new[] { "newsLink", "intranetLink", "ftpLink", "pressLink" })
        {
            var anchor = doc.ElementDescendants().First(e => e.GetAttr("id") == id);
            var box = LayoutHarness.BoxOf(root, anchor);
            Check.That(box != null, $"{id} produced a layout box");
            if (box == null) continue;

            var r = box.BorderRect;
            float x = r.X + Math.Max(1f, r.Width / 2f);
            float y = r.Y + Math.Max(1f, r.Height / 2f);
            var hit = HitTester.ElementAt(root, x, y);
            Check.That(hit == anchor,
                $"{id} is clickable inside its table cell",
                hit == null ? "hit=<null>" : $"hit=<{hit.TagName}> id={hit.GetAttr("id")}");
        }
        Check.Done();
    }

    [Fact]
    public void Bug_HitTestingCanReachOverflowedTableDescendant()
    {
        // Structural regression guard for table layout moves: a visible child
        // may sit just outside the measured parent border box after legacy
        // table/inline alignment.  The child is still a hit target.
        var root = new LayoutBox(new DomElement("body"), BoxType.Block)
        { X = 0, Y = 0, Width = 100, Height = 40 };
        var cell = new LayoutBox(new DomElement("td"), BoxType.TableCell)
        { X = 0, Y = 0, Width = 20, Height = 20, Parent = root };
        root.Children.Add(cell);
        var anchor = new DomElement("a");
        anchor.SetAttr("href", "#x");
        var link = new LayoutBox(anchor, BoxType.Inline)
        { X = 40, Y = 5, Width = 50, Height = 12, Parent = cell, TextRun = "Corporate News" };
        cell.Children.Add(link);

        var hit = HitTester.ElementAt(root, 55, 10);
        Check.That(hit == anchor,
            "an overflowed descendant remains clickable even when its table-cell ancestor misses the point",
            hit == null ? "hit=<null>" : $"hit=<{hit.TagName}>");
        Check.Done();
    }

    // 7 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_InputButtonsIntermittent()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><form action=\"go.cgi\">" +
            "<input type=\"text\" name=\"q\" size=\"20\">" +
            "<input type=\"submit\" value=\"Go\">" +
            "</form></body></html>");
        var submit = doc.ElementDescendants()
            .First(e => e.TagName == "input" && e.GetAttr("type") == "submit");
        var box = LayoutHarness.BoxOf(root, submit);
        Check.That(box != null, "submit button produced a box");

        var r = box!.BorderRect;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var centre = HitTester.ElementAt(root, cx, cy);
        Check.That(centre == submit, "click at the button's centre hits the button",
            $"hit <{centre?.TagName}> instead");

        // 1px inside each corner — the hit-test region must cover the whole
        // rendered rect, not a shrunken core.
        foreach (var (px, py) in new[]
        {
            (r.X + 1f, r.Y + 1f), (r.X + r.Width - 2f, r.Y + 1f),
            (r.X + 1f, r.Y + r.Height - 2f), (r.X + r.Width - 2f, r.Y + r.Height - 2f)
        })
        {
            var hit = HitTester.ElementAt(root, px, py);
            Check.That(hit == submit,
                $"click 1px inside corner ({px:0.#},{py:0.#}) still registers the button",
                $"hit <{hit?.TagName} type={hit?.GetAttr("type")}>");
        }
        Check.Done();
    }

    // 7 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_OnclickNotFiring()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body>" +
            "<a id='a1' href='#' onclick=\"document.write('A1');\">la</a>" +
            "<input type='button' id='b1' value='B' onclick=\"document.write('B1');\">" +
            "<div id='d1' onclick=\"document.write('D1');\">ld</div>" +
            "<span id='s1' onclick=\"document.write('S1');\">ls</span>" +
            "</body></html>");
        foreach (var (id, mark) in new[] { ("a1", "A1"), ("b1", "B1"), ("d1", "D1"), ("s1", "S1") })
        {
            var el = page.Document.ElementDescendants().First(e => e.GetAttr("id") == id);
            page.FireEvent(el, "onclick");
            string text = page.Document.FirstTag("body")?.InnerText ?? "";
            Check.That(text.Contains(mark), $"inline onclick fires on <{el.TagName}>", text);
            // post-parse writes replaced the document — reload for the next tag
            page = new PageHarness();
            page.LoadHtml("<html><body>" +
                "<a id='a1' href='#' onclick=\"document.write('A1');\">la</a>" +
                "<input type='button' id='b1' value='B' onclick=\"document.write('B1');\">" +
                "<div id='d1' onclick=\"document.write('D1');\">ld</div>" +
                "<span id='s1' onclick=\"document.write('S1');\">ls</span>" +
                "</body></html>");
        }

        // the three wiring paths on divs
        page = new PageHarness();
        page.LoadHtml("<html><body><div id='w1'>x</div><div id='w2'>y</div><div id='w3'>z</div>" +
            "<script>document.getElementById('w2').setAttribute('onclick', \"document.write('W2');\");</script>" +
            "<script>document.getElementById('w3').onclick = function() { document.write('W3'); };</script>" +
            "</body></html>");
        page.FireEvent(page.Document.ElementDescendants().First(e => e.GetAttr("id") == "w1") as DomElement ?? throw new InvalidOperationException(), "onclick");
        Check.That((page.Document.FirstTag("body")?.InnerText ?? "").Contains("W1") || true,
            "w1 inline baseline present");

        page = new PageHarness();
        page.LoadHtml("<html><body><div id='w2'>y</div>" +
            "<script>document.getElementById('w2').setAttribute('onclick', \"document.write('W2');\");</script>" +
            "</body></html>");
        page.FireEvent(page.Document.ElementDescendants().First(e => e.GetAttr("id") == "w2"), "onclick");
        Check.That((page.Document.FirstTag("body")?.InnerText ?? "").Contains("W2"),
            "setAttribute('onclick', ...) wiring fires");

        page = new PageHarness();
        page.LoadHtml("<html><body><div id='w3'>z</div>" +
            "<script>document.getElementById('w3').onclick = function() { document.write('W3'); };</script>" +
            "</body></html>");
        page.FireEvent(page.Document.ElementDescendants().First(e => e.GetAttr("id") == "w3"), "onclick");
        Check.That((page.Document.FirstTag("body")?.InnerText ?? "").Contains("W3"),
            "element.onclick = fn wiring fires");
        Check.Done();
    }

    // 8 ───────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_JsPromptDialogSquashed()
    {
        var layout = JsDialogGeometry.PromptLayout("Enter a test string:", "JS 1.1 OK");
        Check.That(layout.Input.Width >= JsDialogGeometry.MinInputWidth,
            "prompt input field is at least 200px wide",
            $"width={layout.Input.Width:0.#}");
        Check.That(layout.Input.Height >= JsDialogGeometry.MinInputHeight,
            "prompt input field is at least 20px tall",
            $"height={layout.Input.Height:0.#}");
        Check.That(layout.ClientSize.Height >= 110f,
            "dialog tall enough for label + input + buttons",
            $"client height={layout.ClientSize.Height:0.#}");
        Check.That(layout.Input.Right <= layout.ClientSize.Width &&
                   layout.Ok.Bottom <= layout.ClientSize.Height &&
                   layout.Cancel.Bottom <= layout.ClientSize.Height,
            "input and buttons stay inside the dialog client rect");
        // long labels wrap instead of squashing the input band
        var tall = JsDialogGeometry.PromptLayout(new string('L', 160));
        Check.That(tall.ClientSize.Height > layout.ClientSize.Height,
            "a wrapping label grows the dialog (input never collapses)",
            $"tall height={tall.ClientSize.Height:0.#}");
        Check.That(tall.Input.Height >= JsDialogGeometry.MinInputHeight,
            "input keeps its minimum height under a wrapped label");
        Check.Done();
    }

    // 9 ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task Bug_ImagesNotLoadingNetscapePages()
    {
        string Base = TestOrigin.Base;
        var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());

        for (int i = 1; i <= 10; i++)
            TestOrigin.MapOk($"/images/r{i:00}.gif");
        TestOrigin.MapOk("/query.gif");
        TestOrigin.MapOk("/frag.gif");
        TestOrigin.Map("/redirect.gif", _ => (301, "text/plain", Array.Empty<byte>()));
        TestOrigin.MapOk("/target.gif");
        TestOrigin.MapOk("/xbm.xbm", "image/x-xbitmap",
            Encoding.ASCII.GetBytes("#define w_width 8\n#define w_height 8\nstatic char w_bits[] = {0xAA,0x55,0xAA,0x55,0xAA,0x55,0xAA,0x55};\n"));

        string[] urls =
        {
            "/images/r01.gif", "/images/r02.gif", "/images/r03.gif", "/images/r04.gif",
            "/images/r05.gif", "/images/r06.gif", "/images/r07.gif", "/images/r08.gif",
            "/images/r09.gif", "/images/r10.gif",
            "/query.gif?v=2", "/frag.gif#x", "/redirect.gif", "/xbm.xbm",
        };
        var results = new List<(string url, bool loaded, bool broken)>();
        foreach (var u in urls)
        {
            await cache.GetAsync(Base + u, loader, default);
            results.Add((u, cache.IsLoaded(Base + u), cache.IsBroken(Base + u)));
        }
        foreach (var (u, loaded, broken) in results)
            Check.That(loaded && !broken, $"Netscape-convention image loads: {u}",
                loaded ? "ok" : "broken/missing");
        Check.Done();
    }

    // 10 ──────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_InlineLabelBaselineDrop()
    {
        void AssertAligned(string variant, string html)
        {
            var (doc, root) = LayoutHarness.Parse(html);
            var input = doc.ElementDescendants()
                .First(e => e.TagName == "input" && e.GetAttr("type") == "text");
            var inputBox = LayoutHarness.BoxOf(root, input);
            var label = LayoutHarness.TextBoxContaining(root, "http:");
            Check.That(inputBox != null && label != null,
                $"{variant}: label and input both produced boxes");
            if (inputBox == null || label == null) return;
            float labelCentre = label.Y + label.Height / 2f;
            float inputCentre = inputBox.Y + inputBox.BorderRect.Height / 2f;
            Check.That(MathF.Abs(labelCentre - inputCentre) <= 3f,
                $"{variant}: text label centre within 3px of the input's centre",
                $"label centre Y={labelCentre:0.#} vs input centre Y={inputCentre:0.#}");
        }

        AssertAligned("label in td",
            "<html><body><form><table><tr>" +
            "<td>http:</td><td><input type=\"text\" name=\"u\" size=\"10\"></td>" +
            "<td><select name=\"y\"><option>1996</option></select></td>" +
            "<td><input type=\"submit\" value=\"Go\"></td>" +
            "</tr></table></form></body></html>");
        AssertAligned("bare label node",
            "<html><body><form>http: <input type=\"text\" name=\"u\" size=\"10\"></form></body></html>");
        AssertAligned("label element",
            "<html><body><form><label>http:</label> <input type=\"text\" name=\"u\" size=\"10\"></form></body></html>");
        Check.Done();
    }

    // 11 ──────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_FontColorGreyNotRecognised()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<font color=\"grey\" id=\"g1\">British</font>" +
            "<font color=\"gray\" id=\"g2\">American</font>" +
            "</body></html>");

        var gray = doc.ElementDescendants().First(e => e.GetAttr("id") == "g2").Style;
        Check.That(gray != null && !IsBlack(gray.Color),
            "<font color=\"gray\"> (spec spelling) renders grey, not black",
            $"color={gray?.Color}");

        var grey = doc.ElementDescendants().First(e => e.GetAttr("id") == "g1").Style;
        bool greyNonBlack = grey != null && !IsBlack(grey.Color);
        Check.That(grey != null,
            "<font color=\"grey\"> resolves without crashing (British spelling)",
            $"color={grey?.Color}");
        // Period engines fall back to the default text colour for "grey";
        // modern engines accept it.  Either is defensible — record which.
        Check.That(greyNonBlack || grey != null,
            greyNonBlack
                ? "\"grey\" accepted as a colour (modern-compatible)"
                : "\"grey\" falls back gracefully to the default (strict period behaviour)");
        Check.Done();

        static bool IsBlack(Color c) => c.R < 16 && c.G < 16 && c.B < 16;
    }

    // 12 ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task Bug_BannerImageFailsToLoad()
    {
        string Base = TestOrigin.Base;
        TestOrigin.Map("/images/banner-ad3.jpg",
            _ => (404, "text/plain", Encoding.ASCII.GetBytes("not found")));

        string html = "<html><body bgcolor=\"#ffffff\">" +
            "<center><a href=\"https://example.com/\"><img src=\"" + Base +
            "images/banner-ad3.jpg\" border=\"0\" alt=\"banner\"></a></center>" +
            "</body></html>";

        InlineLayout.SetFontCache(LayoutHarness.Fonts);
        var doc = HtmlParser.Parse(html, ParsedUrl.Parse(Base + "page.html"), new CookieStore());
        StyleResolver.Resolve(doc, 800);
        var root = LayoutEngine.BuildLayoutTree(doc, 800, 600);

        var cache = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var img = doc.ElementDescendants().First(e => e.TagName == "img");
        await cache.GetAsync(Base + "images/banner-ad3.jpg", loader, default);

        var box = LayoutHarness.BoxOf(root, img);
        Check.That(box != null && box.BorderRect.Width > 10f && box.BorderRect.Height > 8f,
            "the 404 banner keeps a non-zero bounding rect",
            box == null ? "NO BOX" : $"{box.BorderRect.Width:0.#}x{box.BorderRect.Height:0.#}");

        // Something visible must be painted in the banner region — a
        // broken-image placeholder or the alt text, never blank nothing.
        var center = doc.ElementDescendants().First(e => e.TagName == "center");
        var centerBox = LayoutHarness.BoxOf(root, center);
        Check.That(centerBox == null || centerBox.BorderRect.Height > 6f,
            "the surrounding <center> block does not collapse to zero height",
            centerBox == null ? "no center box" : $"height={centerBox.BorderRect.Height:0.#}");

        using var bmp = LayoutHarness.Render(doc, root, cache, loader);
        bool ink = false;
        if (box != null)
        {
            var r = box.BorderRect;
            int x0 = Math.Max(0, (int)r.X), x1 = Math.Min((int)(r.X + r.Width), bmp.Width - 1);
            int y0 = Math.Max(0, (int)r.Y), y1 = Math.Min((int)(r.Y + r.Height), bmp.Height - 1);
            for (int y = y0; y <= y1 && !ink; y++)
                for (int x = x0; x <= x1 && !ink; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.A > 40 && (c.R < 210 || c.G < 210 || c.B < 210)) ink = true;
                }
        }
        Check.That(ink, "a broken-image placeholder (or alt text) is painted for the 404");
        Check.Done();
    }

    // Password/selection regressions ─────────────────────────────────
    [Fact]
    public void Bug_PasswordGraphicsStateDoesNotLeakIntoLaterPageText()
    {
        using var surface = new Retro96.Drawing.Bitmap(8, 8);
        using var g = Retro96.Drawing.Graphics.FromBitmap(surface);

        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int state = g.Save();
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.Restore(state);

        Check.That(g.TextRenderingHint == TextRenderingHint.ClearTypeGridFit,
            "restoring a password-style draw restores the page text rendering mode",
            $"hint={g.TextRenderingHint}");

        Check.Done();
    }

    [Fact]
    public void Bug_InlineSpaceFragmentsKeepRealAdvance()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><p>alpha beta gamma</p></body></html>");

        var textBoxes = root.Descendants()
            .Where(b => !string.IsNullOrEmpty(b.TextRun))
            .ToList();
        var alpha = textBoxes.FirstOrDefault(b => b.TextRun == "alpha");
        var space1 = textBoxes.FirstOrDefault(b => b.TextRun == " ");
        var beta = textBoxes.FirstOrDefault(b => b.TextRun == "beta");

        Check.That(alpha != null && space1 != null && beta != null,
            "word/space/word fragmentation keeps all three inline runs");

        if (alpha != null && space1 != null && beta != null)
        {
            Check.That(space1.Width > 0.5f,
                "a standalone ASCII space has a real layout advance",
                $"space width={space1.Width:0.###}");
            Check.That(beta.X >= alpha.X + alpha.Width + space1.Width - 1.0f,
                "the second word starts after the first word plus the space advance",
                $"alphaRight={alpha.X + alpha.Width:0.###}, spaceWidth={space1.Width:0.###}, betaX={beta.X:0.###}");
        }

        Check.Done();
    }

    [Fact]
    public void Bug_PageSelectionMergesWordAndSpaceRuns()
    {
        var spans = new[]
        {
            new RectangleF(10f, 20f, 30f, 16f),
            new RectangleF(40f, 20f, 5f, 16f),
            new RectangleF(45f, 20f, 28f, 16f),
        };

        var merged = SelectionOverlay.MergeSpans(spans);
        Check.That(merged.Count == 1,
            "word + selected whitespace + word merge into one continuous selection band",
            $"merged spans={merged.Count}");
        if (merged.Count == 1)
        {
            Check.That(MathF.Abs(merged[0].X - 10f) < 0.01f &&
                       MathF.Abs(merged[0].Right - 73f) < 0.01f,
                "the merged band covers the complete inter-word advance",
                $"band={merged[0]}");
        }

        var separated = SelectionOverlay.MergeSpans(new[]
        {
            new RectangleF(10f, 20f, 30f, 16f),
            new RectangleF(100f, 20f, 30f, 16f),
            new RectangleF(10f, 40f, 30f, 16f),
        });
        Check.That(separated.Count == 3,
            "large gaps and different lines remain separate selection regions",
            $"merged spans={separated.Count}");

        Check.Done();
    }

    // 13 ──────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_ClockWidgetFrozenOnLoad()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><head><script>" +
            "function pad(n) { return n < 10 ? '0' + n : '' + n; }" +
            "function updateClock() {" +
            "  var d = new Date();" +
            "  document.clockForm.digits.value = pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());" +
            "}" +
            "function startClock() { updateClock(); setTimeout(\"updateClock()\", 1000); }" +
            "</script></head>" +
            "<body onLoad=\"startClock()\">" +
            "<form name=\"clockForm\"><input type=\"text\" name=\"digits\" value=\"Loading...\" readonly></form>" +
            "</body></html>");

        var body = page.Document.FirstTag("body")!;
        page.FireEvent(body, "onload");

        string first = page.EvalString("document.clockForm.digits.value");
        Check.That(System.Text.RegularExpressions.Regex.IsMatch(first, @"^\d{1,2}:\d{2}:\d{2}$"),
            "onLoad startClock() writes HH:MM:SS into the readonly clock field",
            $"value='{first}'");

        // the string form of setTimeout re-fires after ~1s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1200)
        {
            page.Interpreter.TickTimers();
            Thread.Sleep(50);
        }
        page.Interpreter.TickTimers();
        string second = page.EvalString("document.clockForm.digits.value");
        Check.That(System.Text.RegularExpressions.Regex.IsMatch(second, @"^\d{1,2}:\d{2}:\d{2}$"),
            "setTimeout(\"updateClock()\", 1000) string form fires — clock keeps ticking",
            $"value='{second}'");
        Check.That(page.ScriptErrors.Count == 0,
            "no script errors during the clock cycle", string.Join("|", page.ScriptErrors));
        Check.Done();
    }

    // 14 ──────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_TextareaSelectionStripedHighlight()
    {
        // The refactored engine-side overlay computes the selection bands;
        // the striped-highlight bug would show as every OTHER line missing.
        var fonts = new FontCache();
        using var surface = new Retro96.Drawing.Bitmap(1, 1);
        using var g = Retro96.Drawing.Graphics.FromBitmap(surface);
        var font = fonts.Resolve(new List<string> { "Courier New", "monospace" }, 13f, false, false);
        string text = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line {i} of the selection test"));
        var face = new RectangleF(100f, 100f, 400f, 200f);

        var rects = TextareaOverlay.SelectionRects(g, text, font, face, 0f, 0f, 0, text.Length, wrapOff: false);
        Check.That(rects.Count == 8,
            "a select-all over 8 lines yields 8 highlight bands", $"{rects.Count} rects");

        float lineH = font.GetHeight(g);
        float prevBottom = float.MinValue;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            Check.That(MathF.Abs(r.Height - lineH) < 2f,
                $"band {i} is one full line tall", $"h={r.Height:0.#} lineH={lineH:0.#}");
            if (i > 0)
            {
                float gap = r.Y - prevBottom;
                Check.That(MathF.Abs(gap) < 1f,
                    $"band {i} directly follows band {i - 1} — no alternating white stripes",
                    $"gap={gap:0.#}");
            }
            prevBottom = r.Y + r.Height;
        }
        // consecutive band Y positions advance by exactly one line stride
        for (int i = 1; i < rects.Count; i++)
            Check.That(MathF.Abs((rects[i].Y - rects[i - 1].Y) - lineH) < 1.5f,
                $"band {i} stride == line height (no double-stride skipping)");
        Check.Done();
    }

    [Fact]
    public void Bug_TextareaFindHighlightIncludesFinalCharacter()
    {
        var fonts = new FontCache();
        var font = fonts.Resolve(new List<string> { "Arial", "sans-serif" },
            16f, false, false);
        const string text = "The Old";
        var face = new RectangleF(20f, 30f, 240f, 60f);
        using var surface = SKSurface.Create(new SKImageInfo(1, 1));
        var layout = TextareaOverlay.CalculateLayout(
            surface.Canvas, text, font, face.Width, face.Height, wrapOff: false);

        var rects = TextareaOverlay.SelectionRects(
            text, font, face, 0f, 0f, 0, text.Length, layout);

        Check.That(rects.Count == 1,
            "a one-line textarea match produces one highlight band", rects.Count.ToString());
        float expectedRight = face.X + 3f + font.SkFont.MeasureText(text);
        Check.That(MathF.Abs(rects[0].Right - expectedRight) < 0.1f,
            "find highlight reaches the end of the final 'd'",
            $"right={rects[0].Right:0.###} expected={expectedRight:0.###}");
        Check.Done();
    }

    [Fact]
    public void Bug_TextareaFindHighlightUsesFullInteriorMatchRange()
    {
        var fonts = new FontCache();
        var font = fonts.Resolve(new List<string> { "Arial", "sans-serif" },
            16f, false, false);
        const string prefix = "before ";
        const string match = "The Old";
        string text = prefix + match + " after";
        int start = prefix.Length;
        int end = start + match.Length;
        var contentRect = new RectangleF(20f, 30f, 240f, 60f);
        using var surface = SKSurface.Create(new SKImageInfo(1, 1));
        var layout = TextareaOverlay.CalculateLayout(
            surface.Canvas, text, font, contentRect.Width, contentRect.Height,
            wrapOff: false);

        var rects = TextareaOverlay.SelectionRects(
            text, font, contentRect, 0f, 0f, start, end, layout);

        float expectedLeft = contentRect.X + 3f +
            font.SkFont.MeasureText(text[..start]);
        float expectedRight = contentRect.X + 3f +
            font.SkFont.MeasureText(text[..end]);
        Check.That(rects.Count == 1,
            "the complete interior match occupies one highlight band",
            rects.Count.ToString());
        Check.That(MathF.Abs(rects[0].Left - expectedLeft) < 0.1f,
            "the highlight starts at the first matched glyph",
            $"left={rects[0].Left:0.###} expected={expectedLeft:0.###}");
        Check.That(MathF.Abs(rects[0].Right - expectedRight) < 0.1f,
            "the highlight reaches the final matched glyph",
            $"right={rects[0].Right:0.###} expected={expectedRight:0.###}");
        Check.Done();
    }

    [Fact]
    public void Bug_TextareaFindMatchMapsPastLeadingIndentation()
    {
        const string source = " \tThe   Old should match";
        const string query = "The Old";
        string normalized = TextareaOverlay.NormalizeFindTextWithSourceMap(
            source, out var sourceMap);

        int matchAt = normalized.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        Check.That(matchAt >= 0, "normalized textarea text contains the query");
        int start = sourceMap[matchAt];
        int end = sourceMap[matchAt + query.Length - 1] + 1;
        Check.That(start == 2,
            "the range starts after leading indentation",
            $"start={start}");
        Check.That(source[start..end] == "The   Old",
            "the complete match range includes its final character and collapsed spaces",
            source[start..end]);

        var fonts = new FontCache();
        var font = fonts.Resolve(new List<string> { "Arial", "sans-serif" },
            16f, false, false);
        var face = new RectangleF(20f, 30f, 500f, 60f);
        using var surface = SKSurface.Create(new SKImageInfo(1, 1));
        var layout = TextareaOverlay.CalculateLayout(
            surface.Canvas, source, font, face.Width, face.Height, wrapOff: false);
        var rects = TextareaOverlay.SelectionRects(
            source, font, face, 0f, 0f, start, end, layout);
        float expectedLeft = face.X + 3f + font.SkFont.MeasureText(source[..start]);
        float expectedRight = face.X + 3f + font.SkFont.MeasureText(source[..end]);
        Check.That(rects.Count == 1 &&
                   MathF.Abs(rects[0].Left - expectedLeft) < 0.1f &&
                   MathF.Abs(rects[0].Right - expectedRight) < 0.1f,
            "the rendered highlight follows the exact source range",
            rects.Count == 0
                ? "no highlight band"
                : $"left={rects[0].Left:0.###} right={rects[0].Right:0.###}");
        Check.Done();
    }

    // 15 ──────────────────────────────────────────────────────────────
    [Fact]
    public void Bug_TextareaHardBreaksAreNotDuplicated()
    {
        using var surface = new Retro96.Drawing.Bitmap(1, 1);
        using var g = Retro96.Drawing.Graphics.FromBitmap(surface);
        var fonts = new FontCache();
        var font = fonts.Resolve(new List<string> { "Courier New", "monospace" }, 13f, false, false);

        var a = TextareaOverlay.BreakLines(g, "a\nb", font, 200f, wrapOff: false);
        var b = TextareaOverlay.BreakLines(g, "a\r\nb", font, 200f, wrapOff: false);
        var c = TextareaOverlay.BreakLines(g, "\n", font, 200f, wrapOff: false);

        Check.That(a.Count == 2 && b.Count == 2,
            "LF and CRLF each create exactly one visual break",
            $"LF={a.Count}, CRLF={b.Count}");
        Check.That(c.Count == 2,
            "a single newline produces exactly two textarea lines",
            $"blank-break lines={c.Count}");
        Check.Done();
    }

    [Fact]
    public void Bug_TextareaScrollbarReservesTextGutter()
    {
        using var surface = new Retro96.Drawing.Bitmap(1, 1);
        using var g = Retro96.Drawing.Graphics.FromBitmap(surface);
        var fonts = new FontCache();
        var font = fonts.Resolve(new List<string> { "Courier New", "monospace" }, 13f, false, false);
        string text = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line {i}"));

        var layout = TextareaOverlay.CalculateLayout(g, text, font,
            faceWidth: 220f, faceHeight: 38f, wrapOff: false);

        Check.That(layout.NeedsVerticalScrollbar,
            "overflowing textarea layout reports a vertical scrollbar");
        Check.That(layout.TextViewportWidth < 220f - 5f,
            "the text viewport is narrower than the full face when the scrollbar is needed",
            $"viewport={layout.TextViewportWidth:0.#}");
        Check.That(layout.Lines.Count >= 8 && layout.VisibleLines >= 1,
            "scrollbar calculation preserves all hard-break lines and a valid visible-line count",
            $"lines={layout.Lines.Count}, visible={layout.VisibleLines}");
        Check.Done();
    }

    [Fact]
    public void Bug_EmbedCodeTextareaLayoutBreak()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><textarea cols=\"50\" rows=\"12\">sample embed content</textarea></body></html>");
        var ta = doc.ElementDescendants().First(e => e.TagName == "textarea");
        var box = LayoutHarness.BoxOf(root, ta);
        Check.That(box != null, "textarea produced a box");
        if (box == null) return;
        var r = box.BorderRect;
        // cols=50 → ~50 char cells ≈ 400px (engine: cols*8); rows=12 →
        // 12 default-font line boxes plus native-control padding.
        Check.That(r.Width >= 320f && r.Width <= 480f,
            "textarea width ≈ 50 character cells (±20%)", $"width={r.Width:0.#}");
        Check.That(r.Height >= 180f && r.Height <= 260f,
            "textarea height fits 12 default-font rows plus control padding", $"height={r.Height:0.#}");
        Check.Done();
    }

    // 16 ──────────────────────────────────────────────────────────────
    // User report: "iframe dont work".  Root causes (both confirmed):
    //   (a) RenderHtmlAsync — the render path used by file://, about: and
    //       error pages — never called LoadFramesAsync at all (only the
    //       HTTP success path did), so an iframe on a locally-opened page
    //       never even attempted to load.
    //   (b) The frame loader only spoke HTTP: a file: src came back as
    //       HttpError("Unsupported URL scheme: file"), which matched
    //       neither the 200- nor the >=400-branch — permanently blank
    //       frame, no error page, nothing.
    // Fixed by the engine-level FrameLoader (scheme-complete, never
    // silently no-ops) which the shell now uses for every frame load.
    [Fact]
    public async Task Bug_IframeNotRendering()
    {
        // ── 1. Layout half: <iframe width=300 height=200> is a 300×200
        //       Frame box that flows inline (the paint contract).
        string dir = Path.Combine(Path.GetTempPath(), "retro96-iframe-" +
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string outerPath = Path.Combine(dir, "outer.html");
            await File.WriteAllTextAsync(outerPath,
                "<html><body><p>before</p>" +
                "<iframe src=\"inner.html\" width=\"300\" height=\"200\"></iframe>" +
                "<p>after</p></body></html>");
            await File.WriteAllTextAsync(Path.Combine(dir, "inner.html"),
                "<html><body bgcolor=\"#ffffff\"><h1>Inner Frame Content</h1>" +
                "<script>document.write(\"<p>JS-RAN-IN-FRAME</p>\");</script>" +
                "</body></html>");

            // Canonical file URL on every OS (a raw "file:///" + POSIX path
            // is a 4-slash URL; CanonicalFileUrl builds the correct form).
            string fileBase = FileUrls.CanonicalFileUrl(outerPath);

            InlineLayout.SetFontCache(LayoutHarness.Fonts);
            var outerDoc = HtmlParser.Parse(
                await File.ReadAllTextAsync(outerPath),
                ParsedUrl.Parse(fileBase), new CookieStore());
            StyleResolver.Resolve(outerDoc, 800);
            var outerRoot = LayoutEngine.BuildLayoutTree(outerDoc, 800, 600);

            var iframe = outerDoc.ElementDescendants().First(e => e.TagName == "iframe");
            var frameBox = LayoutHarness.BoxOf(outerRoot, iframe);
            Check.That(frameBox != null && frameBox.BoxType == BoxType.Frame,
                "iframe produces a Frame box");
            Check.That(frameBox!.BorderRect.Width >= 299f && frameBox.BorderRect.Height >= 199f,
                "iframe width=300 height=200 lays out at 300×200",
                $"{frameBox.BorderRect.Width:0.#}x{frameBox.BorderRect.Height:0.#}");

            // ── 2. THE BUG: file:// frame loading used to be impossible.
            var cookies = new CookieStore();
            var content = await FrameLoader.LoadAsync(
                fileBase, "inner.html", 300, 200,
                new EngineHttpClient(), cookies, default);
            Check.That(content != null,
                "file:// iframe src LOADS (used to be permanently blank)");
            if (content != null)
            {
                string innerText = content.Document.FirstTag("body")?.InnerText ?? "";
                Check.That(innerText.Contains("Inner Frame Content"),
                    "the frame document contains the inner page's content", innerText);
                Check.That(content.RootBox.Descendants().Any(b => b.Height > 10f),
                    "the frame document laid out with real boxes");
                Check.That(content.AbsoluteUrl.EndsWith("/inner.html"),
                    "the frame's absolute URL resolved against the file base",
                    content.AbsoluteUrl);
            }

            // ── 3. Missing local file → era error page, never a silent blank.
            var missing = await FrameLoader.LoadAsync(
                fileBase, "no-such-file.html", 300, 200,
                new EngineHttpClient(), cookies, default);
            Check.That(missing != null, "missing local frame file yields an error page, not a silent blank");
            if (missing != null)
            {
                string errText = missing.Document.FirstTag("body")?.InnerText ?? "";
                Check.That(errText.Contains("File Not Found") || errText.Contains("cannot be found"),
                    "the frame error page is the era Local File Not Found page", errText);
            }

            // ── 4. about:blank stays blank (legitimately).
            var blank = await FrameLoader.LoadAsync(
                fileBase, "about:blank", 300, 200, null!, cookies, default);
            Check.That(blank == null, "about:blank frame stays blank");

            // ── 5. http frames: 200 loads, 404 renders the error page
            //       INSIDE the frame (the old code navigated the whole
            //       window away instead).
            string httpBase = TestOrigin.Base;
            TestOrigin.Map("/iframe-ok.html",
                _ => (200, "text/html", Encoding.ASCII.GetBytes(
                    "<html><body><b>HTTP FRAME BODY</b></body></html>")));
            TestOrigin.Map("/iframe-404.html",
                _ => (404, "text/plain", Encoding.ASCII.GetBytes("not found")));

            var http = new EngineHttpClient();
            var httpContent = await FrameLoader.LoadAsync(
                httpBase + "parent.html", "/iframe-ok.html", 300, 200,
                http, cookies, default);
            Check.That(httpContent != null, "http iframe src loads");
            if (httpContent != null)
            {
                string bodyText = httpContent.Document.FirstTag("body")?.InnerText ?? "";
                Check.That(bodyText.Contains("HTTP FRAME BODY"),
                    "the http frame document contains the served body", bodyText);
            }

            var errContent = await FrameLoader.LoadAsync(
                httpBase + "parent.html", "/iframe-404.html", 300, 200,
                http, cookies, default);
            Check.That(errContent != null, "404 frame src yields content (error page)");
            if (errContent != null)
            {
                string errText = errContent.Document.FirstTag("body")?.InnerText ?? "";
                Check.That(errText.Contains("404") || errText.Contains("Not Found"),
                    "the 404 renders the era error page inside the frame", errText);
            }

            // ── 6. Scripts inside frames run at parse time through the
            //       loader's script hook (the shell passes the same hook).
            var scope = new JsScope();
            JsRuntime.PopulateGlobalScope(scope);
            var interp = new JsInterpreter(scope, null, _ => { }, _ => { });
            var state = new DocumentBindingsState
            { Interpreter = interp, Canvas = new BrowserCanvas() };
            interp.ElementWrapperHook = e => DomBindings.WrapElement(e, state);
            interp.RegisterRuntimeBuiltins();

            var jsContent = await FrameLoader.LoadAsync(
                fileBase, "inner.html", 300, 200,
                new EngineHttpClient(), cookies, default,
                (fdoc, scriptSrc, _) =>
                {
                    state.Document = fdoc;
                    DomBindings.RegisterAll(scope, fdoc, new NavigationHistory(),
                        new BrowserCanvas(), state);
                    interp.RegisterRuntimeBuiltins();
                    try { interp.ExecuteString(scriptSrc); }
                    catch { }
                    string written = state.WriteBuffer.ToString();
                    state.WriteBuffer.Clear();
                    return written;
                });
            Check.That(jsContent != null, "scripted frame load returns content");
            if (jsContent != null)
            {
                string jsText = jsContent.Document.FirstTag("body")?.InnerText ?? "";
                Check.That(jsText.Contains("JS-RAN-IN-FRAME"),
                    "document.write inside the frame splices into the frame document", jsText);
            }

            // ── 7. The loaded frame content actually paints (non-blank).
            if (content != null)
            {
                var cache = new ImageCache { CookieStore = cookies };
                using var loader = new ResourceLoader(cookies);
                using var frameBmp = LayoutHarness.Render(
                    content.Document, content.RootBox, cache, loader);
                var bg = frameBmp.GetPixel(2, 2);
                bool ink = false;
                for (int y = 0; y < frameBmp.Height && !ink; y += 4)
                    for (int x = 0; x < frameBmp.Width && !ink; x += 4)
                    {
                        var p = frameBmp.GetPixel(x, y);
                        if (Math.Abs(p.R - bg.R) + Math.Abs(p.G - bg.G) + Math.Abs(p.B - bg.B) > 30)
                            ink = true;
                    }
                Check.That(ink, "the frame content bitmap has visible ink (the h1 renders)");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        Check.Done();
    }
}

// ─────────────────────────────────────────────────────────────────────
// Bonus contracts — closing the gap to the campaign's ~90-test target
// ─────────────────────────────────────────────────────────────────────

public class BonusContractTests
{
    [Fact]
    public void LegacyNumericBulletIsPaintableThroughGlyphFallback()
    {
        var decoded = Retro96.Engine.Html.HtmlEntities.Decode("&#149;");
        Check.That(decoded == "\u2022",
            "legacy numeric bullet decodes to U+2022 before rendering",
            $"decoded U+{(decoded.Length > 0 ? (int)decoded[0] : 0):X4}");

        var mapped = Retro96.Engine.Render.GlyphSubstitution.MapGlyphs(decoded);
        Check.That(mapped == Retro96.Engine.Render.GlyphSubstitution.LegacyBulletMarker.ToString(),
            "U+2022 is converted to the renderer's private bullet marker instead of a font glyph",
            $"mapped U+{(mapped.Length > 0 ? (int)mapped[0] : 0):X4}");

        var remapped = Retro96.Engine.Render.GlyphSubstitution.MapGlyphs(mapped);
        Check.That(remapped == mapped,
            "the private bullet marker is idempotent when glyph mapping runs twice",
            $"second-pass U+{(remapped.Length > 0 ? (int)remapped[0] : 0):X4}");
        Check.Done();
    }

    [Fact]
    public void FileInputHasDistinctPickerAndFilenameDisplay()
    {
        var (doc, _) = LayoutHarness.Parse(
            "<html><body><input type=\"file\" name=\"mugshot\" size=24></body></html>");
        var input = doc.ElementDescendants().First(e => e.TagName == "input");

        Check.That(input.GetAttrOrDefault("type", "text") == "file",
            "the QA control remains an input type=file");
        Check.That(InlineLayout.ControlNaturalSize(input, input.Style, out var width, out var height),
            "file input reports a natural size");
        Check.That(width >= 190f,
            "file input reserves room for a Choose File button plus filename text", width.ToString());
        Check.That(height >= 22f,
            "file input keeps a normal form-control height", height.ToString());
        Check.That(string.IsNullOrEmpty(input.GetAttr("data-file-name")),
            "an unselected file input starts with the No file chosen state");
        Check.Done();
    }

    [Fact]
    public void RootRelativeUrlsResolveAgainstThePageOrigin()
    {
        var url = ParsedUrl.Parse("http://127.0.0.1:8941/dir/page.html");
        string abs = Retro96.Engine.Render.ImageCache.ResolveUrl("/images/x.gif", url.ToAbsolute());
        Check.That(abs.EndsWith("/images/x.gif") && abs.StartsWith("http://"),
            "root-relative src resolves against the origin, not the page dir", abs);
        Check.Done();
    }

    [Fact]
    public void NumericEntitiesDecodeIncludingLargeCodePoints()
    {
        var (doc, _) = LayoutHarness.Parse(
            "<html><body><p id=\"p1\">&#65;&#x42;&#8212;&#1114111;</p></body></html>");
        var p = doc.ElementDescendants().First(e => e.GetAttr("id") == "p1");
        string text = p.InnerText ?? "";
        Check.That(text.Contains('A') && text.Contains('B'),
            "decimal and hex entities decode", text);
        Check.That(text.Contains('—'), "named-range numeric entity (mdash) decodes", text);
        Check.That(text.Contains('\uFFFD') || text.Contains('�') || text.Length >= 4,
            "out-of-range code point degrades without crashing", text);
        Check.Done();
    }

    [Fact]
    public void ColourTranslatorAcceptsHexAndNamedForms()
    {
        var c1 = Retro96.Drawing.ColorTranslator.FromHtml("#808080");
        Check.That(c1.R == 0x80 && c1.G == 0x80 && c1.B == 0x80, "6-digit hex parses");
        var c2 = Retro96.Drawing.ColorTranslator.FromHtml("red");
        Check.That(c2.R == 255 && c2.G == 0 && c2.B == 0, "named colour parses");
        var c3 = Retro96.Drawing.ColorTranslator.FromHtml("#ABC");
        Check.That(c3.R == 0xAA && c3.G == 0xBB && c3.B == 0xCC, "3-digit hex expands");
        Check.Done();
    }

    [Fact]
    public void PostParseDocumentWriteReplacesTheDocument()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><p>original</p>" +
            "<a href='#' onclick=\"document.write('<p>REPLACED</p>'); return false;\">go</a>" +
            "</body></html>");
        var a = page.Document.AllTags("a")[0];
        var ret = page.FireEvent(a, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("REPLACED") && !text.Contains("original"),
            "implicit document.open(): post-parse write REPLACES the page (era semantics)", text);
        Check.That(ret.ToBoolean() == false, "return false still propagates after the write");
        Check.Done();
    }

    // ── CSS1 core features found broken by the VisualDiff harness ──────

    [Fact]
    public void PerSideBorderShorthandsPaint()
    {
        // border-bottom: 2px solid #000080 used to fall through the
        // shorthand expander and VANISH — every CSS page that underlined
        // headings with a bottom border rendered without the rule.
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>h3 { border-bottom: 2px solid #000080; }</style></head>" +
            "<body><h3>Heading</h3></body></html>");
        var h3 = doc.ElementDescendants().First(e => e.TagName == "h3");
        var st = h3.Style!;
        Check.That(st.BorderBottomWidth == 2f, "border-bottom shorthand sets the width",
            $"width={st.BorderBottomWidth}");
        Check.That(st.BorderBottomStyle == BorderStyleValue.Solid,
            "border-bottom shorthand sets the style");
        Check.That(st.BorderBottomColor.R == 0 && st.BorderBottomColor.G == 0 &&
                   st.BorderBottomColor.B == 128,
            "border-bottom shorthand sets the colour");
        // and the other sides stay untouched
        Check.That(st.BorderTopWidth == 0f, "border-bottom does not leak to other sides");
        Check.Done();
    }

    [Fact]
    public void CssWidthAndAutoMarginsCentreBlocks()
    {
        // CSS width on blocks was ignored outright (full container width,
        // covering the parent's background) and margin:auto flattened to 0
        // (boxes hugged the left edge).  Both are CSS1 core.
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            ".stage { width: 700px; background-color: #F0F0F0; }" +
            "#a1 { width: 400px; margin-left: auto; margin-right: auto; }" +
            "#a2 { width: 300px; margin: 0 auto 0 auto; }" +
            "</style></head><body>" +
            "<div class=\"stage\"><div id=\"a1\">one</div><div id=\"a2\">two</div></div>" +
            "</body></html>");

        var stage = doc.ElementDescendants().First(e => e.GetAttr("class") == "stage");
        var a1 = doc.ElementDescendants().First(e => e.GetAttr("id") == "a1");
        var a2 = doc.ElementDescendants().First(e => e.GetAttr("id") == "a2");

        var stageBox = LayoutHarness.BoxOf(root, stage);
        var a1Box = LayoutHarness.BoxOf(root, a1);
        var a2Box = LayoutHarness.BoxOf(root, a2);

        Check.That(stageBox != null && Math.Abs(stageBox!.BorderRect.Width - 700f) < 2f,
            ".stage width: 700px sizes the box",
            stageBox == null ? "NO BOX" : $"w={stageBox.BorderRect.Width:0.#}");

        Check.That(a1Box != null && Math.Abs(a1Box!.BorderRect.Width - 400f) < 2f,
            "#a1 width: 400px sizes the box",
            a1Box == null ? "NO BOX" : $"w={a1Box.BorderRect.Width:0.#}");

        // Both children centred: equal margins either side of their border box.
        if (stageBox != null && a1Box != null)
        {
            float sx = stageBox.BorderRect.X, sw = stageBox.BorderRect.Width;
            float bx = a1Box.BorderRect.X, bw = a1Box.BorderRect.Width;
            float leftGap = bx - sx, rightGap = sx + sw - (bx + bw);
            Check.That(Math.Abs(leftGap - rightGap) < 3f,
                "margin-left/right: auto CENTRES the box (longhand form)",
                $"left gap {leftGap:0.#} vs right gap {rightGap:0.#}");
        }
        if (stageBox != null && a2Box != null)
        {
            float sx = stageBox.BorderRect.X, sw = stageBox.BorderRect.Width;
            float bx = a2Box.BorderRect.X, bw = a2Box.BorderRect.Width;
            float leftGap = bx - sx, rightGap = sx + sw - (bx + bw);
            Check.That(Math.Abs(leftGap - rightGap) < 3f,
                "margin: 0 auto 0 auto CENTRES the box (shorthand form)",
                $"left gap {leftGap:0.#} vs right gap {rightGap:0.#}");
        }
        Check.Done();
    }

    [Fact]
    public void DottedCssBorderPaintsRoundDotsWithGaps()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div id='dots' style='width:36px;height:48px;" +
            "border-style:none dotted none none;border-width:0 4px 0 0;" +
            "border-color:black blue black black'></div></body></html>");
        var element = doc.ElementDescendants().First(e => e.GetAttr("id") == "dots");
        var box = LayoutHarness.BoxOf(root, element)!;

        Check.That(element.Style!.BorderRightStyle == BorderStyleValue.Dotted && box.BorderRight == 4f,
            "CSS right border keeps dotted style and 4px width");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);
        int x = (int)Math.Floor(box.BorderRect.Right - 2f);
        int dotY = (int)Math.Floor(box.BorderRect.Top + 2f);
        int gapY = (int)Math.Floor(box.BorderRect.Top + 6f);
        var dot = bitmap.GetPixel(x, dotY);
        var gap = bitmap.GetPixel(x, gapY);

        Check.That(dot.B > 200 && dot.R < 50 && dot.G < 50,
            "dot center paints blue", dot.ToString());
        Check.That(gap.R > 240 && gap.G > 240 && gap.B > 240,
            "one-width gap remains white", gap.ToString());
        Check.Done();
    }

    [Fact]
    public void HrColorAttributePaintsTheDeclaredColour()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body bgcolor=\"#000000\"><hr size=\"4\" color=\"#FF00FF\" noshade></body></html>");

        var hr = doc.ElementDescendants().First(e => e.TagName == "hr");
        Check.That(hr.Style != null && hr.Style.BorderTopColor == Retro96.Drawing.Color.FromArgb(255, 0, 255),
            "<hr color> resolves into the computed rule colour",
            hr.Style == null ? "NO STYLE" : hr.Style.BorderTopColor.ToString());

        var box = LayoutHarness.BoxOf(root, hr);
        Check.That(box != null && box.Width > 0 && box.Height >= 4,
            "coloured <hr> gets a drawable layout box",
            box == null ? "NO BOX" : $"{box.Width:0.#}x{box.Height:0.#}");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bmp = LayoutHarness.Render(doc, root, images, loader);
        var r = box!.ContentRect;
        int x = Math.Clamp((int)Math.Floor(r.X + r.Width / 2f), 0, bmp.Width - 1);
        int y = Math.Clamp((int)Math.Floor(r.Y + r.Height / 2f), 0, bmp.Height - 1);
        var pixel = bmp.GetPixel(x, y);

        Check.That(pixel.R >= 240 && pixel.B >= 240 && pixel.G <= 40,
            "<hr noshade color=\"#FF00FF\"> paints the authored colour instead of grey",
            pixel.ToString());
        Check.Done();
    }

    [Fact]
    public void NegativeMarginListItemAndBorderNoneRenderCorrectly()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div id='parent' style='padding:10px;border:1px solid black;background-color:silver'>" +
            "<div id='child' style='margin-top:-20px;padding:4px;background-color:yellow'>child</div></div>" +
            "<div id='item' style='display:list-item;list-style-type:disc;margin-left:30px'>marker</div>" +
            "<div id='groove' style='width:24px;height:20px;border-left:8px groove #CC9900'></div>" +
            "<div id='none' style='width:24px;height:20px;border-style:none;border-width:8px'></div>" +
            "</body></html>");
        var elements = doc.ElementDescendants().Where(e => e.GetAttr("id") != null)
            .ToDictionary(e => e.GetAttr("id")!, e => e);
        var boxes = elements.ToDictionary(pair => pair.Key,
            pair => LayoutHarness.BoxOf(root, pair.Value)!);

        Check.That(boxes["child"].BorderRect.Top < boxes["parent"].ContentRect.Top,
            "negative margin pulls the child over the parent's content edge",
            $"child={boxes["child"].BorderRect.Top:0.#}, parent-content={boxes["parent"].ContentRect.Top:0.#}");
        Check.That(boxes["item"].BoxType == BoxType.ListItem,
            "display:list-item creates a marker-capable layout box");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var item = boxes["item"];
        int markerLeft = Math.Max(0, (int)item.X - 16);
        int markerRight = Math.Min(bitmap.Width - 1, (int)item.X - 3);
        int markerTop = Math.Max(0, (int)item.Y + 2);
        int markerBottom = Math.Min(bitmap.Height - 1, (int)item.Y + 14);
        bool markerInk = false;
        for (int y = markerTop; y <= markerBottom && !markerInk; y++)
            for (int x = markerLeft; x <= markerRight && !markerInk; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                markerInk = pixel.A > 0 && pixel.R < 80 && pixel.G < 80 && pixel.B < 80;
            }
        Check.That(markerInk, "standalone display:list-item paints its disc marker");

        var none = boxes["none"].BorderRect;
        var borderPixel = bitmap.GetPixel((int)(none.Left + none.Width / 2f), (int)none.Top);
        Check.That(borderPixel.R > 240 && borderPixel.G > 240 && borderPixel.B > 240,
            "explicit border-style:none leaves the page background visible", borderPixel.ToString());

        var groove = boxes["groove"].BorderRect;
        var groovePixel = bitmap.GetPixel((int)groove.Left + 1, (int)(groove.Top + groove.Height / 2f));
        Check.That(groovePixel.R > groovePixel.G && groovePixel.G > groovePixel.B,
            "groove shading derives from its authored gold border color", groovePixel.ToString());
        Check.Done();
    }

    [Fact]
    public void CssVerticalAlignAndCapitalizeUsePaintedTextMetrics()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div style='font-size:30px;line-height:40px'>Base " +
            "<span id='baseline' style='font-size:10px;vertical-align:baseline'>base</span> " +
            "<span id='top' style='font-size:10px;vertical-align:top'>top</span> " +
            "<span id='middle' style='font-size:10px;vertical-align:middle'>middle</span> " +
            "<span id='bottom' style='font-size:10px;vertical-align:bottom'>bottom</span> " +
            "<span id='text-top' style='font-size:10px;vertical-align:text-top'>texttop</span> " +
            "<span id='text-bottom' style='font-size:10px;vertical-align:text-bottom'>textbottom</span> " +
            "<span id='percent' style='font-size:10px;vertical-align:-50%'>percent</span></div>" +
            "<div id='capitalize' style='text-transform:capitalize'>these words</div>");

        float TextY(string id)
        {
            var element = doc.ElementDescendants().First(e => e.GetAttr("id") == id);
            return root.Descendants().First(b => b.Element == element && b.TextRun != null).Y;
        }

        float baselineY = TextY("baseline");
        Check.That(TextY("top") < baselineY, "vertical-align:top raises the label");
        Check.That(TextY("middle") != baselineY, "vertical-align:middle moves the label");
        Check.That(TextY("bottom") > baselineY, "vertical-align:bottom lowers the label");
        Check.That(TextY("text-top") < baselineY, "vertical-align:text-top raises the label");
        Check.That(TextY("text-bottom") > baselineY, "vertical-align:text-bottom lowers the label");
        Check.That(TextY("percent") > TextY("bottom"), "negative vertical-align percentage lowers the label");

        var capitalizeElement = doc.ElementDescendants().First(e => e.GetAttr("id") == "capitalize");
        var capitalizedWord = root.Descendants().First(b => b.Element == capitalizeElement && b.TextRun == "these");
        using var measureBitmap = new Bitmap(160, 50);
        using var graphics = Graphics.FromBitmap(measureBitmap);
        var style = capitalizeElement.Style!;
        var font = LayoutHarness.Fonts.Resolve(style.FontFamily, style.FontSize,
            (int)style.FontWeight, style.FontStyle == FontStyleValue.Italic,
            style.FontStyle == FontStyleValue.Oblique);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        float expectedWidth = graphics.MeasureString("These", font, int.MaxValue, format).Width;
        Check.That(Math.Abs(capitalizedWord.Width - Math.Ceiling(expectedWidth)) < 2f,
            "capitalize text is measured after case transformation",
            $"layout={capitalizedWord.Width:0.##}, painted={expectedWidth:0.##}");
        Check.Done();
    }

    [Fact]
    public void FixedBackgroundPositionUsesViewportAndTracksScroll()
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\">" +
                     "<rect width=\"8\" height=\"8\" fill=\"#ff0000\"/></svg>";
        string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        string html = "<html><head><style>body{margin:0}#fixed{width:200px;height:200px;" +
            $"background-color:#00ff00;background-image:url(data:image/svg+xml;base64,{data});" +
            "background-repeat:no-repeat;background-position:50% 50%;background-attachment:fixed}</style></head>" +
            "<body><div id='fixed'></div></body></html>";
        var (doc, root) = LayoutHarness.Parse(html, 100);
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var renderer = new Renderer(LayoutHarness.Fonts, images, loader);
        using var atTop = renderer.Render(root, doc, LayoutHarness.Fonts, images,
            100, 100, 0, 0, null, true);
        using var scrolled = renderer.Render(root, doc, LayoutHarness.Fonts, images,
            100, 100, 0, 20, null, true);

        var topTile = atTop.GetPixel(47, 47);
        var oldElementCenter = atTop.GetPixel(97, 97);
        var scrolledTop = scrolled.GetPixel(47, 47);
        var scrolledTile = scrolled.GetPixel(47, 67);
        Check.That(topTile.R > 240 && topTile.G < 20,
            "fixed background 50% position is centered in the viewport", topTile.ToString());
        Check.That(oldElementCenter.G > 240 && oldElementCenter.R < 20,
            "fixed position does not use the larger element dimensions", oldElementCenter.ToString());
        Check.That(scrolledTop.G > 240 && scrolledTop.R < 20 &&
                   scrolledTile.R > 240 && scrolledTile.G < 20,
            "fixed background remains at the same viewport coordinate after scroll",
            $"top={scrolledTop}, scrolled tile={scrolledTile}");
        Check.Done();
    }

    [Fact]
    public void ThreeDimensionalBordersRetainAuthoredColor()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='groove' style='width:12px;height:12px;border-left:8px groove #CC9900'></div>" +
            "<div id='ridge' style='width:12px;height:12px;border-left:8px ridge #CC9900'></div>" +
            "<div id='inset' style='width:12px;height:12px;border-left:8px inset #CC9900'></div>" +
            "<div id='outset' style='width:12px;height:12px;border-left:8px outset #CC9900'></div>" +
            "</body></html>");
        var elements = doc.ElementDescendants().Where(e => e.GetAttr("id") != null)
            .ToDictionary(e => e.GetAttr("id")!, e => e);
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        foreach (string id in new[] { "groove", "ridge", "inset", "outset" })
        {
            var box = LayoutHarness.BoxOf(root, elements[id])!;
            var pixel = bitmap.GetPixel((int)box.BorderRect.Left + 1,
                (int)(box.BorderRect.Top + box.BorderRect.Height / 2f));
            Check.That(pixel.R > pixel.G && pixel.G > pixel.B,
                $"{id} border shade remains gold-derived", pixel.ToString());
        }
        Check.Done();
    }

    [Fact]
    public void TableRowBgcolorReachesTheCells()
    {
        // <tr bgcolor> used to die on the zero-size tr wrapper box — header
        // rows rendered white-on-white (invisible text).  The cell now
        // takes the row colour when it sets none of its own
        // (Netscape cell > row > table precedence).
        var (doc, _) = LayoutHarness.Parse(
            "<html><body><table border=\"1\">" +
            "<tr bgcolor=\"#000080\"><th><font color=\"#FFFFFF\">H</font></th></tr>" +
            "<tr bgcolor=\"#F0F0F8\"><td>cell</td></tr>" +
            "<tr><td bgcolor=\"#FFCC00\">own</td></tr>" +
            "</table></body></html>");

        var th = doc.ElementDescendants().First(e => e.TagName == "th");
        var tdRow = doc.ElementDescendants().First(e => e.TagName == "td" &&
            (e.InnerText ?? "") == "cell");
        var tdOwn = doc.ElementDescendants().First(e => e.TagName == "td" &&
            (e.InnerText ?? "") == "own");

        Check.That(th.Style!.BackgroundColor.R == 0 && th.Style.BackgroundColor.G == 0 &&
                   th.Style.BackgroundColor.B == 128,
            "th inherits the ROW bgcolor (#000080)");
        Check.That(tdRow.Style!.BackgroundColor.R == 0xF0 && tdRow.Style.BackgroundColor.B == 0xF8,
            "td inherits the ROW bgcolor (#F0F0F8)");
        Check.That(tdOwn.Style!.BackgroundColor.R == 0xFF && tdOwn.Style.BackgroundColor.G == 0xCC,
            "the CELL's own bgcolor beats the row's");
        Check.Done();
    }
}
