// The 55 pre-existing regression checks, ported verbatim from the console
// runner to xUnit facts (one fact per original Test* method).
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Network;
using Retro96.Engine.Css;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;
using Retro96.Drawing;
using Retro96;

namespace RetroTests;

public class EngineRegressionTests
{
    [Fact]
    public void ParsedUrlPreservesExactlyOneTrailingDirectorySlash()
    {
        Check.That(
            ParsedUrl.Parse("http://spacejam.com/1996/").ToAbsolute() ==
            "http://spacejam.com/1996/",
            "address-bar directory URL does not gain a second trailing slash");
        Check.That(
            ParsedUrl.Parse("http://spacejam.com/1996").ToAbsolute() ==
            "http://spacejam.com/1996",
            "URL without a trailing slash remains slash-free");
        Check.That(
            ParsedUrl.Parse("http://spacejam.com/a//b///").ToAbsolute() ==
            "http://spacejam.com/a/b/",
            "duplicate path slashes normalize to one trailing slash");
        Check.Done();
    }

    [Fact]
    public void DownscaledImagesUseMipmapFiltering()
    {
        using var source = new Bitmap(64, 64);
        using var target = new Bitmap(1, 1);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                source.SetPixel(x, y, ((x + y) & 1) == 0 ? Color.Black : Color.White);

        using (var graphics = Graphics.FromBitmap(target))
            graphics.DrawImage(source, 0, 0, 1, 1);

        var pixel = target.GetPixel(0, 0);
        Check.That(pixel.R is >= 112 and <= 143 && pixel.G is >= 112 and <= 143 &&
                   pixel.B is >= 112 and <= 143,
            "strong image downscaling averages high-frequency detail",
            $"pixel={pixel}");
        Check.Done();
    }

    [Fact]
    public void ZoomedImagesUseCubicFiltering()
    {
        using var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.Black);
        source.SetPixel(1, 0, Color.White);
        source.SetPixel(0, 1, Color.White);
        source.SetPixel(1, 1, Color.Black);
        using var target = new Bitmap(16, 16);
        using (var graphics = Graphics.FromBitmap(target))
        {
            graphics.ScaleTransform(8f, 8f);
            graphics.DrawImage(source, 0, 0, 2, 2);
        }

        int blendedPixels = 0;
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                byte red = target.GetPixel(x, y).R;
                if (red is > 0 and < 255)
                    blendedPixels++;
            }
        Check.That(blendedPixels > 0,
            "zoomed raster images blend neighboring source pixels",
            $"blendedPixels={blendedPixels}");
        Check.Done();
    }

    [Fact]
    public void ParserSurvivesThe1996Pages()
    {
        foreach (var file in new[] { "detector.html", "netscape1996.html", "dolekemp96.html", "theoldnet.html" })
        {
            string path = Path.Combine(TestPaths.Testdata, file);
            Check.That(File.Exists(path), $"{file} exists");
            if (!File.Exists(path)) continue;

            string html = File.ReadAllText(path);
            DomDocument? doc = null;
            Exception? ex = null;
            try { doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/test.htm"), new CookieStore()); }
            catch (Exception e) { ex = e; }

            Check.That(ex == null, $"{file}: parse without exception", ex?.Message ?? "");
            if (doc == null) continue;

            var body = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
            Check.That(body != null, $"{file}: <body> inferred");
            if (body == null) continue;

            int visible = body.Descendants().OfType<DomElement>()
                .Count(e => e.TagName is "p" or "table" or "img" or "h1" or "h2" or "h3"
                    or "center" or "td" or "tr" or "form" or "b" or "font" or "a" or "marquee");
            Check.That(visible > 5, $"{file}: body has real content ({visible} visual elements)", $"only {visible}");
        }
        Check.Done();
    }

    [Fact]
    public void IrcChatPageParsesWithImagesAndIframe()
    {
        string path = Path.Combine(TestPaths.Testdata, "irc.html");
        Check.That(File.Exists(path), "irc.html exists");
        if (!File.Exists(path)) { Check.Done(); return; }

        string html = File.ReadAllText(path);
        DomDocument? doc = null;
        try { doc = HtmlParser.Parse(html, ParsedUrl.Parse("http://theoldnet.com/chat"), new CookieStore()); }
        catch (Exception e) { Check.That(false, "irc.html parses", e.Message); }

        var body = doc?.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        Check.That(body != null, "irc: <body> present");
        if (body == null) { Check.Done(); return; }

        var imgs = doc!.ElementDescendants().Where(e => e.TagName == "img").ToList();
        Check.That(imgs.Count == 3, "irc: 3 <img> elements in body", imgs.Count.ToString());
        Check.That(imgs.All(i => (i.GetAttr("src") ?? "").StartsWith("/images/")),
              "irc: image srcs preserved");
        Check.That(imgs.All(i => (i.GetAttr("src") ?? "").Trim().Length == (i.GetAttr("src") ?? "").Length),
              "irc: no trailing spaces in src (old netscape1996 poison)");

        var iframe = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "iframe");
        Check.That(iframe != null, "irc: <iframe> element present");
        Check.That(iframe?.GetAttr("src") == "https://webchat.oftc.net/?channels=theoldnet",
              "irc: iframe src intact", iframe?.GetAttr("src") ?? "");

        var centers = doc.ElementDescendants().Count(e => e.TagName == "center");
        Check.That(centers == 2, "irc: both <center> blocks parsed", centers.ToString());

        var font = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "font");
        Check.That(font?.GetAttr("color") == "white", "irc: <font color=white> parsed");
        Check.Done();
    }

    [Fact]
    public void ErrorPageShellAlignmentPinned()
    {
        string html = Retro96.Engine.ErrorPage.NetworkError("http://x.test/", "boom");
        Check.That(html.Contains("<td align=\"center\" valign=\"middle\">"),
              "shell: dialog remains centered in the viewport");
        Check.That(html.Contains("<td align=\"left\" valign=\"top\">"),
              "shell: message content is left-aligned");
        Check.That(html.Contains("<td align=\"left\"><font color=\"#ffffff\""),
              "shell: title-bar cell left-aligned");

        Check.That(html.Contains("<a href=\"retro96://home\">Home</a>"),
              "footer: provides the browser home link");

        foreach (var (name, page) in new[] {
            ("NotFound", Retro96.Engine.ErrorPage.NotFound("http://x.test/404")),
            ("Cert",     Retro96.Engine.ErrorPage.CertificateError("http://x.test/", "bad cert")),
            ("Timeout",  Retro96.Engine.ErrorPage.Timeout("http://x.test/")),
        })
        {
            Check.That(page.Contains("align=\"left\""), $"{name}: shell pins left alignment");
        }

        string cert = Retro96.Engine.ErrorPage.CertificateError("http://x.test/", "bad");
        Check.That(cert.Contains("window.acceptCertRisk && window.acceptCertRisk()"),
              "cert page: onsubmit references the shell hook");
        Check.Done();
    }

    [Fact]
    public void GlyphSubstitutionTable()
    {
        string Sub(string s) => Retro96.Engine.Render.GlyphSubstitution.MapGlyphs(s);

        Check.That(Sub("\u25B6") == ">", "\u25B6 (black right triangle) → >");
        Check.That(Sub("\u25BA") == ">", "\u25BA (pointer) → >");
        Check.That(Sub("\u258C") == "|", "\u258C (left half block) → | (blue sliver)");
        Check.That(Sub("\u26A0 Warning") == "!! Warning", "\u26A0 (warning) → !!");
        Check.That(Sub("\u266A") == "~", "\u266A (note) → ~");
        Check.That(Sub("a\u2014b") == "a\u2014b", "em dash preserved");
        Check.That(Sub("x\u2022y") == "x" + Retro96.Engine.Render.GlyphSubstitution.LegacyBulletMarker + "y",
            "bullet is mapped to the renderer's stable legacy-bullet marker");
        Check.That(Sub("caf\u00E9") == "caf\u00E9", "Latin-1 accented preserved");
        Check.That(Sub("plain text 123") == "plain text 123", "ASCII untouched");
        Check.That(Sub(Sub("\u25B6\u266A")) == Sub("\u25B6\u266A"), "idempotent");
        Check.That(Sub("[X]\u2588") == "[X]#", "panel-title block substitutes");
        Check.That(Sub("\u2550\u2551") == "=|", "box drawing double line → =|");
        Check.Done();
    }

    [Fact]
    public void DetectorScriptExecutes()
    {
        string html = File.ReadAllText(Path.Combine(TestPaths.Testdata, "detector.html"));

        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);

        var navigator = new JsObject();
        navigator.Set("appName", JsValue.From("Netscape"));
        navigator.Set("appVersion", JsValue.From("3.01 (Win95; I)"));
        navigator.Set("userAgent", JsValue.From("Mozilla/3.0 (compatible; Retro96/1.0; Windows 95)"));
        navigator.Set("platform", JsValue.From("Win32"));
        scope.Define("navigator", JsValue.FromObject(navigator));

        var interpreter = new JsInterpreter(scope, null, _ => { }, _ => { });
        interpreter.RegisterRuntimeBuiltins();

        var writeBuffer = new System.Text.StringBuilder();
        var document = new JsObject();
        document.Set("write", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            foreach (var a in args) writeBuffer.Append(a.ToJsString());
            return JsValue.Undefined;
        }, scope, "write")));
        document.Set("writeln", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            foreach (var a in args) writeBuffer.Append(a.ToJsString());
            writeBuffer.Append('\n');
            return JsValue.Undefined;
        }, scope, "writeln")));
        scope.Define("document", JsValue.FromObject(document));

        var doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/detector.htm"), new CookieStore());
        var script = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "script");
        var text = script?.Children.OfType<DomText>().FirstOrDefault()?.Data;
        Check.That(!string.IsNullOrEmpty(text), "script source extracted by tokenizer/parser",
            $"text length = {text?.Length ?? 0}");
        if (string.IsNullOrEmpty(text)) { Check.Done(); return; }

        Exception? ex = null;
        try { interpreter.ExecuteString(text); }
        catch (Exception e) { ex = e; }

        Check.That(ex == null, "detector script executes without fatal error", ex?.Message ?? "");
        string written = writeBuffer.ToString();
        Check.That(written.Contains("Raw Output for Legacy Browsers"),
            "document.write output streamed", written.Length == 0 ? "write buffer EMPTY" : "");
        Check.That(written.Contains("Netscape Navigator"),
            "sniffing produced Netscape Navigator (ua.indexOf chain works)",
            written.Length == 0 ? "" : "no match in: " + written[..Math.Min(200, written.Length)]);
        Check.Done();
    }

    [Fact]
    public void LocationAssignmentNavigates()
    {
        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);

        string? navigatedTo = null;
        var interpreter = new JsInterpreter(scope, null, url => navigatedTo = url, _ => { });
        interpreter.RegisterRuntimeBuiltins();

        interpreter.ExecuteString(
            "function openpowerstart() { location = \"http://personal.netscape.com/custom/page/show_page.html\"; }");
        interpreter.ExecuteString("openpowerstart();");
        Check.That(navigatedTo == "http://personal.netscape.com/custom/page/show_page.html",
            "bare location = \"url\" navigates", navigatedTo ?? "(no navigation)");

        navigatedTo = null;
        interpreter.ExecuteString("setTimeout(\"location = 'http://x.test/page'\", 0);");
        interpreter.TickTimers();
        Check.That(navigatedTo == "http://x.test/page",
            "setTimeout(\"location='...'\") fires and navigates", navigatedTo ?? "(no navigation)");

        navigatedTo = null;
        var window = new JsObject();
        window.Set("location", JsValue.From("http://old.value/"));
        scope.Define("window", JsValue.FromObject(window));
        scope.GlobalFallback = window;
        interpreter.ExecuteString("window.location = \"http://y.test/win\";");
        Check.That(navigatedTo == "http://y.test/win",
            "window.location = \"url\" navigates", navigatedTo ?? "(no navigation)");
        Check.Done();
    }

    [Fact]
    public void MarqueeParsesAsBlockWithContent()
    {
        var doc = HtmlParser.Parse(
            "<html><body><marquee behavior=\"scroll\" direction=\"left\" scrollamount=\"3\" bgcolor=\"#ffff00\">Welcome to The Old Net!</marquee></body></html>",
            ParsedUrl.Parse("about:blank"), new CookieStore());

        var marquee = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "marquee");
        Check.That(marquee != null, "marquee element parsed");
        Check.That(marquee?.GetAttr("behavior") == "scroll" &&
              marquee?.GetAttr("scrollamount") == "3" &&
              marquee?.GetAttr("bgcolor") == "#ffff00",
              "marquee attributes survive (behavior/scrollamount/bgcolor)");
        Check.That((marquee?.InnerText ?? "").Contains("Welcome to The Old Net!"),
            "marquee content present");
        Check.Done();
    }

    [Fact]
    public void FileUrlResolution()
    {
        var baseFile = ParsedUrl.Parse("file:///C:/web/dolekemp96.htm");
        Check.That(baseFile.Scheme == "file", "file: URL parses with scheme");

        // Drive-letter URL → native path. Windows keeps its native form;
        // other hosts map it to a rooted path (File APIs report not-found).
        string expectedLocal = OperatingSystem.IsWindows()
            ? "C:\\web\\dolekemp96.htm"
            : "/C:/web/dolekemp96.htm";
        var local = FileUrls.LocalPathFromFileUrl(baseFile);
        Check.That(local == expectedLocal,
            "canonical drive file URL maps to a native path", local ?? "(null)");

        var resolved = FileUrls.Resolve(baseFile, "assets/photo one.gif");
        Check.That(resolved == "file:///C:/web/assets/photo%20one.gif",
            "relative local link resolves canonically and escapes spaces", resolved);

        var fragment = FileUrls.Resolve(baseFile, "#features");
        Check.That(fragment == "file:///C:/web/dolekemp96.htm#features",
            "same-document file fragment keeps the canonical file URL", fragment);

        // file://server/share/... is UNC on Windows, a plain rooted path
        // on POSIX hosts (there is no such filesystem object either way).
        string expectedUnc = OperatingSystem.IsWindows()
            ? "\\\\server\\share\\sites\\home.html"
            : "/server/share/sites/home.html";
        var unc = FileUrls.LocalPathFromFileUrl(
            ParsedUrl.Parse("file://server/share/sites/home.html"));
        Check.That(unc == expectedUnc,
            "host-style file URL maps to a host filesystem path", unc ?? "(null)");

        var original = FileUrls.CanonicalFileUrl("C:\\Docs\\Retro Pages\\index.html");
        Check.That(original == "file:///C:/Docs/Retro%20Pages/index.html",
            "Windows path canonicalises to one file URL form", original);

        Check.That(FileUrls.TryResolveAddressBarInput(
                "C:\\Docs\\Retro Pages\\index.html", null, out var addressBarUrl) &&
            addressBarUrl == "file:///C:/Docs/Retro%20Pages/index.html",
            "absolute Windows paths are first-class address-bar navigation targets",
            addressBarUrl);

        // POSIX hosts: absolute /paths and sloppy multi-slash file: URLs
        // normalise instead of degrading into bogus UNC paths.
        if (!OperatingSystem.IsWindows())
        {
            Check.That(FileUrls.TryResolveAddressBarInput(
                    "/var/www/pages/site.html", null, out var posixUrl) &&
                posixUrl == "file:///var/www/pages/site.html",
                "absolute POSIX paths are first-class address-bar navigation targets",
                posixUrl);

            var sloppy = ParsedUrl.Parse("file:////var/www/pages/index.html");
            Check.That(FileUrls.LocalPathFromFileUrl(sloppy) == "/var/www/pages/index.html",
                "extra-slash file URLs normalise to a rooted POSIX path",
                FileUrls.LocalPathFromFileUrl(sloppy));
        }
        Check.Done();
    }

    // ─────────────────────────────────────────────────────────────────
    // Task 9 (Layout integration) — 1999 layout/render features:
    // DomElement↔LayoutBox wiring, IE5 quirks box model, min/max clamping,
    // generated content, outline, optgroup rows, collapsed borders,
    // table-layout:fixed, vertical-align lengths, caption-side,
    // empty-cells, marquee script-event hook.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LayoutBoxWiringPublishesPrincipalBox()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div id='d' style='width:180px'>x</div>" +
            "<p id='p'>para</p></body></html>");
        var div = doc.ElementDescendants().First(e => e.GetAttr("id") == "d");
        var p = doc.ElementDescendants().First(e => e.GetAttr("id") == "p");
        var body = doc.ElementDescendants().First(e => e.TagName == "body");

        Check.That(div.LayoutBox != null, "div's principal box is published on DomElement.LayoutBox");
        Check.That(div.Box != null && ReferenceEquals(div.Box, div.LayoutBox),
            "the legacy DomElement.Box slot mirrors LayoutBox (DomBindings offset* reads it)");
        Check.That(ReferenceEquals(LayoutHarness.BoxOf(root, div), div.LayoutBox),
            "the published box IS the box in the layout tree");
        Check.That(p.LayoutBox != null && body.LayoutBox != null,
            "paragraph and body boxes are published too");
        var oldPBox = p.LayoutBox;

        // Re-layout reassigns a FRESH box (no stale geometry for JS reads).
        var root2 = LayoutEngine.BuildLayoutTree(doc, 800, 600);
        Check.That(p.LayoutBox != null && !ReferenceEquals(p.LayoutBox, oldPBox),
            "a new layout pass publishes a fresh box");
        Check.That(ReferenceEquals(LayoutHarness.BoxOf(root2, p), p.LayoutBox),
            "the fresh reference matches the new tree");

        // An element whose box is DROPPED (display:none) loses its reference
        // instead of keeping stale geometry.
        div.Style!.Display = DisplayValue.None;
        LayoutEngine.BuildLayoutTree(doc, 800, 600);
        Check.That(div.LayoutBox == null && div.Box == null,
            "display:none clears the stale box reference");
        Check.Done();
    }

    [Fact]
    public void Ie5BoxModelPersonaTreatsAuthoredWidthAsBorderBox()
    {
        try
        {
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.InternetExplorer5 });
            var (doc, root) = LayoutHarness.Parse(
                "<html><body><div id='quirk' style='width:200px;height:100px;padding:10px;border:5px solid black'>x</div>" +
                "<img id='iq' src=\"data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7\" " +
                "style='width:100px;height:40px;border:2px solid black;padding:6px'></body></html>");
            var quirk = LayoutHarness.BoxOf(root,
                doc.ElementDescendants().First(e => e.GetAttr("id") == "quirk"))!;
            var img = LayoutHarness.BoxOf(root,
                doc.ElementDescendants().First(e => e.GetAttr("id") == "iq"))!;

            Check.That(Math.Abs(quirk.BorderRect.Width - 200f) < 0.5f,
                "IE5 quirks: authored CSS width IS the border-box width (padding+border inside)",
                $"borderBox={quirk.BorderRect.Width:0.#}");
            Check.That(Math.Abs(quirk.BorderRect.Height - 100f) < 0.5f,
                "IE5 quirks: authored CSS height IS the border-box height",
                $"borderBox={quirk.BorderRect.Height:0.#}");
            // offsetWidth/offsetHeight read exactly this border box.
            Check.That(Math.Abs((doc.ElementDescendants().First(e => e.GetAttr("id") == "quirk").LayoutBox!.BorderRect.Width) - 200f) < 0.5f,
                "offsetWidth via DomElement.LayoutBox = 200");

            Check.That(Math.Abs(img.BorderRect.Width - 100f) < 0.5f,
                "IE5 img quirk: margin+border+width+border — padding never participates",
                $"borderBox={img.BorderRect.Width:0.#}");

            // The W3C content-box model in the union persona.
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
            var (doc2, root2) = LayoutHarness.Parse(
                "<html><body><div id='std' style='width:200px;padding:10px;border:5px solid black'>x</div></body></html>");
            var std = LayoutHarness.BoxOf(root2,
                doc2.ElementDescendants().First(e => e.GetAttr("id") == "std"))!;
            Check.That(Math.Abs(std.BorderRect.Width - 230f) < 0.5f,
                "union persona keeps the W3C box model (content 200 + padding 20 + border 10)",
                $"borderBox={std.BorderRect.Width:0.#}");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());   // default persona restore
        }
        Check.Done();
    }

    [Fact]
    public void MinMaxWidthHeightClampBlockSizes()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='capped' style='max-width:120px'>some wide auto content</div>" +
            "<div id='floored' style='width:50px;min-width:200px'>x</div>" +
            "<div id='pctcapped' style='max-width:25%'>more wide auto content</div>" +
            "<div id='hfloored' style='min-height:80px'>short</div>" +
            "<div id='hcapped' style='height:300px;max-height:40px'>x</div>" +
            "</body></html>");
        LayoutBox BoxFor(string id) => LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == id))!;

        Check.That(Math.Abs(BoxFor("capped").BorderRect.Width - 120f) < 1f,
            "max-width clamps the auto-stretched block",
            $"width={BoxFor("capped").BorderRect.Width:0.#}");
        Check.That(Math.Abs(BoxFor("floored").BorderRect.Width - 200f) < 1f,
            "min-width raises an authored 50px width to 200px",
            $"width={BoxFor("floored").BorderRect.Width:0.#}");
        // 25% of the body content width (800 − 2×10 body margins = 780)
        Check.That(Math.Abs(BoxFor("pctcapped").BorderRect.Width - 195f) < 1.5f,
            "percentage max-width resolves against the containing block",
            $"width={BoxFor("pctcapped").BorderRect.Width:0.#}");
        Check.That(BoxFor("hfloored").BorderRect.Height >= 79f,
            "min-height raises the content height",
            $"height={BoxFor("hfloored").BorderRect.Height:0.#}");
        Check.That(Math.Abs(BoxFor("hcapped").BorderRect.Height - 40f) < 1f,
            "max-height caps an authored 300px height",
            $"height={BoxFor("hcapped").BorderRect.Height:0.#}");
        Check.Done();
    }

    [Fact]
    public void MinMaxConstraintsNeverOverrideLargerResolvedSizes()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='automin' style='min-width:200px'>x</div>" +
            "<div id='tallmin' style='min-height:20px'><p>a</p><p>b</p><p>c</p><p>d</p></div>" +
            "</body></html>");
        LayoutBox BoxFor(string id) => LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == id))!;

        // The auto stretch (780px body content) is LARGER than the 200px
        // floor — the stretch wins, min-width does not shrink the block.
        Check.That(BoxFor("automin").BorderRect.Width > 700f,
            "min-width never shrinks an auto width that already exceeds it",
            $"width={BoxFor("automin").BorderRect.Width:0.#}");

        // Content taller than the min-height floor grows past it (the floor
        // is not mistaken for an authored height).
        var contentH = 0f;
        foreach (var child in BoxFor("tallmin").Children)
            contentH = Math.Max(contentH, child.BorderRect.Bottom - BoxFor("tallmin").Y);
        Check.That(BoxFor("tallmin").BorderRect.Height >= contentH - 1f &&
                  BoxFor("tallmin").BorderRect.Height > 20f,
            "min-height raises but never caps taller content",
            $"height={BoxFor("tallmin").BorderRect.Height:0.#} content={contentH:0.#}");
        Check.Done();
    }

    [Fact]
    public void GeneratedBeforeAfterContentFlowsAsTextRuns()
    {
        // Author rules (StyleResolver already routes :before/:after into the
        // GeneratedBefore/GeneratedAfter slots) — independent of any Q UA
        // defaults, which the main agent seeds separately.
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "#t:before { content: \"[\" } " +
            "#t:after { content: attr(suffix) } " +
            "#blk:before { content: 'AB' }" +
            "</style></head><body>" +
            "<p><span id='t' suffix='END'>mid</span></p>" +
            "<div id='blk'>word</div>" +
            "</body></html>");
        var span = doc.ElementDescendants().First(e => e.GetAttr("id") == "t");
        var blk = doc.ElementDescendants().First(e => e.GetAttr("id") == "blk");

        var runs = root.Descendants()
            .Where(b => b.Element == span && !string.IsNullOrEmpty(b.TextRun))
            .ToList();
        Check.That(runs.Count == 3,
            ":before run + element content + :after run", $"runs={runs.Count}");
        Check.That(runs[0].TextRun == "[",
            "the :before string token flows as the FIRST text run", runs[0].TextRun ?? "");
        Check.That(runs[1].TextRun == "mid",
            "the element's own content sits between the generated runs", runs[1].TextRun ?? "");
        Check.That(runs[2].TextRun == "END",
            ":after attr(suffix) emits the element's attribute value", runs[2].TextRun ?? "");
        Check.That(runs[0].X < runs[1].X && runs[1].X < runs[2].X,
            "generated runs flow in inline document order");

        var blkRuns = LayoutHarness.BoxOf(root, blk)!.Children
            .Where(b => !string.IsNullOrEmpty(b.TextRun)).ToList();
        Check.That(blkRuns.Count == 2 && blkRuns[0].TextRun == "AB",
            "block elements get their :before run inside the block box",
            blkRuns.Count > 0 ? blkRuns[0].TextRun ?? "" : "(none)");
        Check.Done();
    }

    [Fact]
    public void OutlinePaintsRingAroundBorderBox()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='red' style='width:100px;height:60px;border:2px solid black;outline:4px solid #ff0000'></div>" +
            "<div id='plain' style='width:100px;height:60px;border:2px solid black'></div>" +
            "<div id='inv' style='width:100px;height:60px;outline-width:3px;outline-style:solid;outline-color:invert'></div>" +
            "</body></html>");
        var elements = doc.ElementDescendants().Where(e => e.GetAttr("id") != null)
            .ToDictionary(e => e.GetAttr("id")!, e => e);

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var red = LayoutHarness.BoxOf(root, elements["red"])!.BorderRect;
        var plain = LayoutHarness.BoxOf(root, elements["plain"])!.BorderRect;
        var inv = LayoutHarness.BoxOf(root, elements["inv"])!.BorderRect;

        // Outline band: 2px offset from the border edge, stroke outward.
        var redPixel = bitmap.GetPixel(
            (int)(red.Left + red.Width / 2f), (int)(red.Top - 4f));
        Check.That(redPixel.R > 200 && redPixel.G < 80 && redPixel.B < 80,
            "outline paints its authored colour 2px outside the border box", redPixel.ToString());

        var plainPixel = bitmap.GetPixel(
            (int)(plain.Left + plain.Width / 2f), (int)(plain.Top - 4f));
        Check.That(plainPixel.R > 240 && plainPixel.G > 240 && plainPixel.B > 240,
            "no outline declaration leaves the band unpainted", plainPixel.ToString());

        var invPixel = bitmap.GetPixel(
            (int)(inv.Left + inv.Width / 2f), (int)(inv.Top - 3.5f));
        Check.That(invPixel.R < 90 && invPixel.G < 90 && invPixel.B < 90,
            "outline-color: invert paints black", invPixel.ToString());
        Check.Done();
    }

    [Fact]
    public void OptgroupRowsRenderAsHeadersWithIndentedOptions()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><select id='s' size='6'>" +
            "<optgroup label='Colors'><option>Red</option><option>Blue</option></optgroup>" +
            "<option>Plain</option></select></body></html>");
        var select = doc.ElementDescendants().First(e => e.GetAttr("id") == "s");

        var rows = SelectRowModel.Build(select);
        Check.That(rows.Count == 4,
            "group header + 2 grouped options + 1 loose option", $"rows={rows.Count}");
        Check.That(rows[0].IsGroupHeader && rows[0].Option == null && rows[0].Label == "Colors",
            "the OPTGROUP header row carries the LABEL attribute and is non-selectable",
            $"label='{rows[0].Label}'");
        Check.That(!rows[1].IsGroupHeader && rows[1].Option != null &&
                   Math.Abs(rows[1].Indent - SelectRowModel.GroupIndent) < 0.01f,
            "options inside a group indent by 12px");
        Check.That(Math.Abs(rows[3].Indent) < 0.01f,
            "loose options are not indented");

        // Rendered geometry: the grouped option's text starts ~12px right of
        // the group header's text; the loose option aligns with the header.
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var face = LayoutHarness.BoxOf(root, select)!.ContentRect;
        float rowH = face.Height / 6f;

        int LeftmostInk(float y0, float y1)
        {
            for (int x = (int)face.X; x < (int)face.Right - 2; x++)
                for (int y = (int)y0; y <= (int)y1; y++)
                {
                    var p = bitmap.GetPixel(x, y);
                    if (p.A > 0 && p.R < 100 && p.G < 100 && p.B < 100)
                        return x;
                }
            return -1;
        }

        int headerX = LeftmostInk(face.Y + rowH * 0.15f, face.Y + rowH * 0.85f);
        int groupedX = LeftmostInk(face.Y + rowH * 1.15f, face.Y + rowH * 1.85f);
        int plainX = LeftmostInk(face.Y + rowH * 3.15f, face.Y + rowH * 3.85f);
        Check.That(headerX > 0 && groupedX > 0 && plainX > 0,
            "header, grouped option and loose option all render text",
            $"headerX={headerX} groupedX={groupedX} plainX={plainX}");
        if (headerX <= 0 || groupedX <= 0 || plainX <= 0) { Check.Done(); return; }

        Check.That(groupedX - headerX >= 10,
            "the option inside the group is indented right of the header label",
            $"Δ={groupedX - headerX}");
        Check.That(Math.Abs(plainX - headerX) <= 3,
            "the loose option aligns with the un-indented header label",
            $"Δ={plainX - headerX}");
        Check.Done();
    }

    [Fact]
    public void BorderCollapseForcesZeroCellSpacing()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='sep' border='1'><tr><td>a</td><td>b</td></tr></table>" +
            "<table id='col' border='1' style='border-collapse:collapse'><tr><td>a</td><td>b</td></tr></table>" +
            "<table id='sp' border='1' style='border-collapse:separate;border-spacing:9px'><tr><td>a</td><td>b</td></tr></table>" +
            "</body></html>");

        float Gap(string id)
        {
            var tds = doc.ElementDescendants().Where(e => e.GetAttr("id") == id).First()
                .ElementDescendants().Where(e => e.TagName == "td").ToList();
            var b1 = LayoutHarness.BoxOf(root, tds[0])!;
            var b2 = LayoutHarness.BoxOf(root, tds[1])!;
            return b2.X - b1.BorderRect.Right;
        }

        Check.That(Math.Abs(Gap("sep") - 2f) < 0.5f,
            "separate (default) keeps the NN CELLSPACING=2 gutter",
            $"gap={Gap("sep"):0.#}");
        Check.That(Math.Abs(Gap("col")) < 0.5f,
            "border-collapse:collapse removes ALL spacing between cells",
            $"gap={Gap("col"):0.#}");
        Check.That(Math.Abs(Gap("sp") - 9f) < 0.5f,
            "authored border-spacing beats the attribute default",
            $"gap={Gap("sp"):0.#}");
        Check.Done();
    }

    [Fact]
    public void TableLayoutFixedDistributesBySpecifiedColumnsOnly()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='f' style='table-layout:fixed;width:300px' cellspacing='0' cellpadding='0'>" +
            "<tr><td width='100'>a</td><td>MMMMMMMMM MMMMMMMMM MMMMMMMMM wide content</td></tr></table>" +
            "<table id='g' style='table-layout:fixed;width:400px' cellspacing='0' cellpadding='0'>" +
            "<colgroup><col width='80'><col></colgroup>" +
            "<tr><td>a</td><td>b</td></tr></table>" +
            "</body></html>");

        float[] Widths(string id)
        {
            var tds = doc.ElementDescendants().Where(e => e.GetAttr("id") == id).First()
                .ElementDescendants().Where(e => e.TagName == "td").ToList();
            return tds.Select(td => LayoutHarness.BoxOf(root, td)!.BorderRect.Width).ToArray();
        }

        var fixed1 = Widths("f");
        Check.That(fixed1.Length == 2 && Math.Abs(fixed1[0] - 100f) < 1f,
            "fixed: the first-row width attr pins column 0 at 100px",
            $"w0={fixed1[0]:0.#}");
        Check.That(fixed1.Length == 2 && Math.Abs(fixed1[1] - 200f) < 1f,
            "fixed: the unspecified column takes ALL the remaining space (content ignored)",
            $"w1={fixed1[1]:0.#}");

        var fixed2 = Widths("g");
        Check.That(fixed2.Length == 2 && Math.Abs(fixed2[0] - 80f) < 1f,
            "fixed: <colgroup><col width=80> pins column 0",
            $"w0={fixed2[0]:0.#}");
        Check.That(fixed2.Length == 2 && Math.Abs(fixed2[1] - 320f) < 1f,
            "fixed: the un-specced <col> splits the remainder equally",
            $"w1={fixed2[1]:0.#}");
        Check.Done();
    }

    [Fact]
    public void VerticalAlignLengthShiftsBaselineByPixels()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div style='font-size:30px;line-height:40px'>Base " +
            "<span id='up' style='font-size:10px;vertical-align:8px'>up</span> " +
            "<span id='down' style='font-size:10px;vertical-align:-8px'>down</span></div></body></html>");

        float TextY(string id)
        {
            var el = doc.ElementDescendants().First(e => e.GetAttr("id") == id);
            return root.Descendants().First(b => b.Element == el && b.TextRun != null).Y;
        }

        Check.That(Math.Abs((TextY("down") - TextY("up")) - 16f) < 1.5f,
            "+8px and −8px vertical-align lengths sit 16px apart",
            $"Δ={TextY("down") - TextY("up"):0.#}");
        Check.Done();
    }

    [Fact]
    public void CaptionSideBottomPlacesCaptionBelowTheGrid()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='top' border='1'><caption id='capTop'>above</caption><tr><td>cell</td></tr></table>" +
            "<table id='bot' border='1'><caption id='capBot' style='caption-side:bottom'>below</caption><tr><td>cell</td></tr></table>" +
            "</body></html>");

        var capTop = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "capTop"))!;
        var capBot = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "capBot"))!;
        var tdTop = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "top")
                .ElementDescendants().First(e => e.TagName == "td"))!;
        var tdBot = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "bot")
                .ElementDescendants().First(e => e.TagName == "td"))!;

        Check.That(capTop.Y + capTop.BorderRect.Height <= tdTop.Y + 1f,
            "default caption-side:top places the caption above the grid");
        Check.That(capBot.Y >= tdBot.BorderRect.Bottom - 1f,
            "caption-side:bottom places the caption below the grid",
            $"capY={capBot.Y:0.#} gridBottom={tdBot.BorderRect.Bottom:0.#}");
        Check.Done();
    }

    [Fact]
    public void EmptyCellsHideSuppressesEmptyCellPainting()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='hide' border='1' style='empty-cells:hide'><tr><td></td><td>x</td></tr></table>" +
            "<table id='show' border='1'><tr><td></td><td>y</td></tr></table>" +
            "</body></html>");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        foreach (var id in new[] { "hide", "show" })
        {
            var table = doc.ElementDescendants().First(e => e.GetAttr("id") == id);
            var emptyTd = table.ElementDescendants().First(e => e.TagName == "td" &&
                string.IsNullOrEmpty(e.InnerText));
            var box = LayoutHarness.BoxOf(root, emptyTd)!;
            // Probe INSIDE the empty cell's own left border strip, away from
            // the neighbouring cell's border.
            var pixel = bitmap.GetPixel((int)(box.X + 0.5f),
                (int)(box.Y + box.BorderRect.Height / 2f));
            if (id == "hide")
                Check.That(pixel.R > 230 && pixel.G > 230 && pixel.B > 230,
                    "empty-cells:hide paints no border on the empty cell", pixel.ToString());
            else
                Check.That(pixel.R <= 140 && pixel.G <= 140 && pixel.B <= 140,
                    "default empty-cells:show keeps the grey cell border", pixel.ToString());
        }
        Check.Done();
    }

    [Fact]
    public void OffsetWidthReadsTheRealLayoutBox()
    {
        var page = new PageHarness();
        page.LoadHtml(
            "<html><body><div id='d' style='width:220px; padding:10px; border:5px solid black'>box</div></body></html>");
        Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);
        LayoutEngine.BuildLayoutTree(page.Document, 800, 600);

        // Default persona = IE5 quirks: the authored width IS the border box,
        // so the REAL box-backed offsetWidth is 220 (the pre-layout style
        // fallback in DomBindings keeps its own 250 arithmetic — it never
        // consults layout).
        Check.That(page.EvalString("document.getElementById('d').offsetWidth") == "220",
            "offsetWidth reads the real layout box (IE5 quirks: authored width = border box)",
            page.EvalString("document.getElementById('d').offsetWidth"));
        Check.That(page.EvalString("document.getElementById('d').clientWidth") == "210",
            "clientWidth = border box − borders (quirks content + padding)",
            page.EvalString("document.getElementById('d').clientWidth"));
        Check.That(page.EvalString("document.getElementById('d').offsetHeight") != "0" &&
                  page.EvalString("document.getElementById('d').offsetHeight") != "",
            "offsetHeight is a real measured height once a box exists",
            page.EvalString("document.getElementById('d').offsetHeight"));
        Check.Done();
    }

    [Fact]
    public void MarqueeEventHookFiresStartBounceFinish()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<marquee id='alt' behavior='alternate' scrollamount='10' scrolldelay='10'>bouncy marquee content</marquee>" +
            "<marquee id='sli' behavior='slide' scrollamount='10' scrolldelay='10'>slidey marquee content</marquee>" +
            "</body></html>");
        var alt = doc.ElementDescendants().First(e => e.GetAttr("id") == "alt");
        var sli = doc.ElementDescendants().First(e => e.GetAttr("id") == "sli");
        var altBox = LayoutHarness.BoxOf(root, alt)!;
        var sliBox = LayoutHarness.BoxOf(root, sli)!;

        var events = new List<(string Id, string Event)>();
        Renderer.MarqueeEventHook = (elem, eventName) =>
            events.Add((elem.GetAttr("id") ?? "", eventName));
        try
        {
            using var images = new ImageCache { CookieStore = new CookieStore() };
            using var loader = new ResourceLoader(new CookieStore());
            var renderer = new Renderer(LayoutHarness.Fonts, images, loader);

            const long t0 = 1_000_000;
            renderer.GetMarqueeTranslationX(altBox, t0);
            renderer.GetMarqueeTranslationX(sliBox, t0);
            Check.That(events.Count(e => e.Event == "onstart") == 2,
                "the first animation query of each marquee fires onstart",
                string.Join(",", events.Select(e => $"{e.Id}:{e.Event}")));

            // Advance past the first alternate turnaround (half a cycle).
            float contentW = 0f;
            foreach (var ch in altBox.Children)
                contentW = Math.Max(contentW,
                    ch.X + ch.BorderLeft + ch.PaddingLeft + ch.Width - altBox.X);
            float span = Math.Max(1f, altBox.ContentRect.Width - contentW);
            float pxPerMs = 10f / 10f * BrowserRuntime.MarqueeSpeedPercent / 100f;
            long halfCycle = (long)Math.Ceiling(span / pxPerMs) + 5;
            renderer.GetMarqueeTranslationX(altBox, t0 + halfCycle);
            Check.That(events.Count(e => e.Id == "alt" && e.Event == "onbounce") == 1,
                "crossing the first turnaround fires onbounce exactly once",
                string.Join(",", events.Select(e => $"{e.Id}:{e.Event}")));

            // Slide: advance far past the single traversal.
            renderer.GetMarqueeTranslationX(sliBox, t0 + 100_000);
            renderer.GetMarqueeTranslationX(sliBox, t0 + 200_000);
            Check.That(events.Count(e => e.Id == "sli" && e.Event == "onfinish") == 1,
                "the slide traversal completion fires onfinish exactly once",
                string.Join(",", events.Select(e => $"{e.Id}:{e.Event}")));
        }
        finally
        {
            Renderer.MarqueeEventHook = null;
        }
        Check.Done();
    }
}
