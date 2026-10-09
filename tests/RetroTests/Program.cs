using System;
using System.Collections.Generic;
using System.Linq;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Network;

namespace RetroTests;

public static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(bool condition, string name, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
    }

    public static int Main()
    {
        if (Environment.GetEnvironmentVariable("DUMP") == "1") { DebugDump.Run(); return 0; }

        Console.WriteLine("Retro96 engine regression tests (headless)\n");

        TestParserOnAllPages();
        TestDetectorScript();
        TestLocationAssignmentNavigates();
        TestMarqueePresent();
        TestFileUrlResolution();
        TestIrcChatPage();
        TestErrorPageShell();
        TestGlyphSubstitution();

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The IRC #theoldnet page: centred images + external HTTPS iframe.
    /// It must parse with all 3 images and the iframe in the body (the
    /// "centre thing doesn't load" report was a frame-loading UX bug, not
    /// a parser bug — this pins the parser half).
    /// </summary>
    private static void TestIrcChatPage()
    {
        Console.WriteLine("— IRC chat page parses with images + iframe —");

        string path = "/home/z/my-project/retro96/testdata/irc.html";
        if (!System.IO.File.Exists(path)) { Check(false, "irc.html exists"); return; }

        string html = System.IO.File.ReadAllText(path);
        DomDocument? doc = null;
        try { doc = HtmlParser.Parse(html, ParsedUrl.Parse("http://theoldnet.com/chat"), new CookieStore()); }
        catch (Exception e) { Check(false, "irc.html parses", e.Message); }

        var body = doc?.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        Check(body != null, "irc: <body> present");
        if (body == null) return;

        var imgs = doc!.ElementDescendants().Where(e => e.TagName == "img").ToList();
        Check(imgs.Count == 3, "irc: 3 <img> elements in body", imgs.Count.ToString());
        Check(imgs.All(i => (i.GetAttr("src") ?? "").StartsWith("/images/")),
              "irc: image srcs preserved");
        Check(imgs.All(i => (i.GetAttr("src") ?? "").Trim().Length == (i.GetAttr("src") ?? "").Length),
              "irc: no trailing spaces in src (old netscape1996 poison)");

        var iframe = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "iframe");
        Check(iframe != null, "irc: <iframe> element present");
        Check(iframe?.GetAttr("src") == "https://webchat.oftc.net/?channels=theoldnet",
              "irc: iframe src intact", iframe?.GetAttr("src") ?? "");

        var centers = doc.ElementDescendants().Count(e => e.TagName == "center");
        Check(centers == 2, "irc: both <center> blocks parsed", centers.ToString());

        var font = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "font");
        Check(font?.GetAttr("color") == "white", "irc: <font color=white> parsed");
    }

    /// <summary>
    /// Error pages use the shared HTML 4.01/CSS 2 presentation shell.
    /// </summary>
    private static void TestErrorPageShell()
    {
        Console.WriteLine("— Error page shell: HTML 4.01 and CSS 2 —");

        string html = Retro96.Engine.ErrorPage.NetworkError("http://x.test/", "boom");
        Check(html.StartsWith("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\""),
              "shell: declares HTML 4.01 Strict");
        Check(html.Contains("<style type=\"text/css\">"),
              "shell: uses a CSS stylesheet");
        Check(html.Contains("<div class=\"page\">") &&
              html.Contains("<div class=\"titlebar\">"),
              "shell: uses standard structural elements");
        Check(html.Contains("<a href=\"retro96://home\">Home</a>"),
              "footer: provides the browser home link");

        foreach (var (name, page) in new[] {
            ("NotFound", Retro96.Engine.ErrorPage.NotFound("http://x.test/404")),
            ("Cert",     Retro96.Engine.ErrorPage.CertificateError("http://x.test/", "bad cert")),
            ("Timeout",  Retro96.Engine.ErrorPage.Timeout("http://x.test/")),
        })
        {
            Check(page.StartsWith("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\""),
                $"{name}: uses HTML 4.01 Strict");
        }

        // The certificate page's button contract: onsubmit must call the
        // shell hook window.acceptCertRisk (registered by DomBindings.
        // RegisterCertRiskHook since this batch).
        string cert = Retro96.Engine.ErrorPage.CertificateError("http://x.test/", "bad");
        Check(cert.Contains("window.acceptCertRisk && window.acceptCertRisk()"),
              "cert page: onsubmit references the shell hook");
    }

    /// <summary>
    /// Glyph substitution: chars the period core fonts lack collapse to
    /// era-safe ASCII; Latin-1/typographic punctuation passes through.
    /// </summary>
    private static void TestGlyphSubstitution()
    {
        Console.WriteLine("— Glyph substitution table —");

        var map = typeof(Retro96.Engine.Render.GlyphSubstitution);
        Check(map != null, "GlyphSubstitution type compiled");

        string Sub(string s) => Retro96.Engine.Render.GlyphSubstitution.MapGlyphs(s);

        Check(Sub("\u25B6") == ">", "\u25B6 (black right triangle) → >");
        Check(Sub("\u25BA") == ">", "\u25BA (pointer) → >");
        Check(Sub("\u258C") == "|", "\u258C (left half block) → | (blue sliver)");
        Check(Sub("\u26A0 Warning") == "!! Warning", "\u26A0 (warning) → !!");
        Check(Sub("\u266A") == "~", "\u266A (note) → ~");
        Check(Sub("a\u2014b") == "a\u2014b", "em dash preserved");
        Check(Sub("x\u2022y") == "x\u2022y", "bullet preserved");
        Check(Sub("caf\u00E9") == "caf\u00E9", "Latin-1 accented preserved");
        Check(Sub("plain text 123") == "plain text 123", "ASCII untouched");
        Check(Sub(Sub("\u25B6\u266A")) == Sub("\u25B6\u266A"), "idempotent");
        Check(Sub("[X]\u2588") == "[X]#", "panel-title block substitutes");
        Check(Sub("\u2550\u2551") == "=|", "box drawing double line → =|");
    }

    // ─────────────────────────────────────────────────────────────────────

    private static void TestParserOnAllPages()
    {
        Console.WriteLine("— Parser survives the 1996 pages and builds real body content —");

        foreach (var file in new[] { "detector.html", "netscape1996.html", "dolekemp96.html", "theoldnet.html" })
        {
            string path = System.IO.Path.Combine("/home/z/my-project/retro96/testdata", file);
            if (!System.IO.File.Exists(path)) { Check(false, $"{file} exists"); continue; }

            string html = System.IO.File.ReadAllText(path);
            DomDocument? doc = null;
            Exception? ex = null;
            try
            {
                doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/test.htm"),
                    new CookieStore());
            }
            catch (Exception e) { ex = e; }

            Check(ex == null, $"{file}: parse without exception", ex?.Message ?? "");
            if (doc == null) continue;

            var body = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
            Check(body != null, $"{file}: <body> inferred");
            if (body == null) continue;

            int visible = body.Descendants()
                .OfType<DomElement>()
                .Count(e => e.TagName is "p" or "table" or "img" or "h1" or "h2" or "h3"
                    or "center" or "td" or "tr" or "form" or "b" or "font" or "a" or "marquee");
            Check(visible > 5, $"{file}: body has real content ({visible} visual elements)",
                $"only {visible}");

            var scriptElems = doc.ElementDescendants().Where(e => e.TagName == "script").ToList();
            int withText = scriptElems.Count(s => s.Children.OfType<DomText>().Any());
            Console.WriteLine($"        {file}: scripts={scriptElems.Count} with source={withText}, title='{doc.Title}'");
        }
    }

    private static void TestDetectorScript()
    {
        Console.WriteLine("\n— Detector script (the 'stuck on Detecting...' bug) —");

        // Reproduces the FIXED Form1 flow: full DOM-0 bindings registered
        // BEFORE each inline script executes (the old code registered a
        // minimal document with no navigator, and the script died on its
        // very first line).
        string html = System.IO.File.ReadAllText("/home/z/my-project/retro96/testdata/detector.html");

        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);

        // navigator — what RegisterAll installs (values as in DomBindings)
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

        // Extract the script source the way the parser would
        var doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/detector.htm"),
            new CookieStore());
        var script = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "script");
        var text = script?.Children.OfType<DomText>().FirstOrDefault()?.Data;
        Check(!string.IsNullOrEmpty(text), "script source extracted by tokenizer/parser",
            $"text length = {text?.Length ?? 0}");
        if (string.IsNullOrEmpty(text)) return;

        // Strip the era comment-hiding wrappers exactly like nothing does —
        // the interpreter must cope with <!-- and // --> inside script.
        Exception? ex = null;
        try { interpreter.ExecuteString(text); }
        catch (Exception e) { ex = e; }

        Check(ex == null, "detector script executes without fatal error", ex?.Message ?? "");
        string written = writeBuffer.ToString();
        Check(written.Contains("Raw Output for Legacy Browsers"),
            "document.write output streamed", written.Length == 0 ? "write buffer EMPTY" : "");
        Check(written.Contains("Netscape Navigator"),
            "sniffing produced Netscape Navigator (ua.indexOf chain works)",
            written.Length == 0 ? "" : "no match in: " + written[..Math.Min(200, written.Length)]);
    }

    private static void TestLocationAssignmentNavigates()
    {
        Console.WriteLine("\n— location = \"url\" now navigates (dead 3D buttons) —");

        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);

        string? navigatedTo = null;
        var interpreter = new JsInterpreter(scope, null,
            url => navigatedTo = url, _ => { });
        interpreter.RegisterRuntimeBuiltins();

        // The Netscape button handlers, verbatim shape
        interpreter.ExecuteString(
            "function openpowerstart() { location = \"http://personal.netscape.com/custom/page/show_page.html\"; }");
        interpreter.ExecuteString("openpowerstart();");
        Check(navigatedTo == "http://personal.netscape.com/custom/page/show_page.html",
            "bare location = \"url\" navigates", navigatedTo ?? "(no navigation)");

        // setTimeout("location = '...'", 0) — the pickandclick form
        navigatedTo = null;
        interpreter.ExecuteString("setTimeout(\"location = 'http://x.test/page'\", 0);");
        interpreter.TickTimers();
        Check(navigatedTo == "http://x.test/page",
            "setTimeout(\"location='...'\") fires and navigates", navigatedTo ?? "(no navigation)");

        // window.location = "url"
        navigatedTo = null;
        var window = new JsObject();
        window.Set("location", JsValue.From("http://old.value/"));
        scope.Define("window", JsValue.FromObject(window));
        scope.GlobalFallback = window;
        interpreter.ExecuteString("window.location = \"http://y.test/win\";");
        Check(navigatedTo == "http://y.test/win",
            "window.location = \"url\" navigates", navigatedTo ?? "(no navigation)");
    }

    private static void TestMarqueePresent()
    {
        Console.WriteLine("\n— marquee parses as a block with content —");

        var doc = HtmlParser.Parse(
            "<html><body><marquee behavior=\"scroll\" direction=\"left\" scrollamount=\"3\" bgcolor=\"#ffff00\">Welcome to The Old Net!</marquee></body></html>",
            ParsedUrl.Parse("about:blank"), new CookieStore());

        var marquee = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "marquee");
        Check(marquee != null, "marquee element parsed");
        Check(marquee?.GetAttr("behavior") == "scroll" &&
              marquee?.GetAttr("scrollamount") == "3" &&
              marquee?.GetAttr("bgcolor") == "#ffff00",
              "marquee attributes survive (behavior/scrollamount/bgcolor)");
        Check((marquee?.InnerText ?? "").Contains("Welcome to The Old Net!"),
            "marquee content present");
    }

    private static void TestFileUrlResolution()
    {
        Console.WriteLine("\n— file:// URL handling for locally-opened pages —");

        // The opaque file: parse keeps the authority slashes in Path
        var baseFile = ParsedUrl.Parse("file:///C:/web/dolekemp96.htm");
        Check(baseFile.Scheme == "file", "file: URL parses with scheme");

        var resolved = baseFile.Resolve("dolekemp96org.jpg");
        Check(resolved.ToAbsolute().Contains("dolekemp96org.jpg"),
            "relative img resolves against file base", resolved.ToAbsolute());

        // Root-relative against a file base
        var rootRel = baseFile.Resolve("/images/logo.gif");
        Check(rootRel.Path.Contains("logo.gif"), "root-relative path kept", rootRel.Path);
    }
}
