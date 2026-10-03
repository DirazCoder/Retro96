// The 55 pre-existing regression checks, ported verbatim from the console
// runner to xUnit facts (one fact per original Test* method).
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Network;
using Retro96.Drawing;

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

        var local = FileUrls.LocalPathFromFileUrl(baseFile);
        Check.That(local == "C:\\web\\dolekemp96.htm",
            "canonical drive file URL maps to a native Windows path", local ?? "(null)");

        var resolved = FileUrls.Resolve(baseFile, "assets/photo one.gif");
        Check.That(resolved == "file:///C:/web/assets/photo%20one.gif",
            "relative local link resolves canonically and escapes spaces", resolved);

        var fragment = FileUrls.Resolve(baseFile, "#features");
        Check.That(fragment == "file:///C:/web/dolekemp96.htm#features",
            "same-document file fragment keeps the canonical file URL", fragment);

        var unc = FileUrls.LocalPathFromFileUrl(
            ParsedUrl.Parse("file://server/share/sites/home.html"));
        Check.That(unc == "\\\\server\\share\\sites\\home.html",
            "UNC file URL maps to a UNC filesystem path", unc ?? "(null)");

        var original = FileUrls.CanonicalFileUrl("C:\\Docs\\Retro Pages\\index.html");
        Check.That(original == "file:///C:/Docs/Retro%20Pages/index.html",
            "Windows path canonicalises to one file URL form", original);

        Check.That(FileUrls.TryResolveAddressBarInput(
                "C:\\Docs\\Retro Pages\\index.html", null, out var addressBarUrl) &&
            addressBarUrl == "file:///C:/Docs/Retro%20Pages/index.html",
            "absolute Windows paths are first-class address-bar navigation targets",
            addressBarUrl);
        Check.Done();
    }
}
