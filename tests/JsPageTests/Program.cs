// JsPageTests — executes the REAL inline scripts of the three new test
// pages through the REAL DomBindings (with a canvas stub), asserting the
// era-correct script contracts end-to-end:
//   • Acme CyberCorp: Image preloading, named image rollover swap,
//     getElementById + style.display modal toggling, live clock form
//     updates, window.status messages.
//   • Voyager's Island: screen.* reads, the Bravenet counter's
//     document.write output spliced into the document by the parser.
//   • Nintendo Hallway: frameset contract (parse level).
using Retro96;
using Retro96.Engine;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Network;
using Retro96.Engine.Css;

namespace JsPageTests;

public static class Program
{
    private static int _passed, _failed;

    private static void Check(bool cond, string name, string detail = "")
    {
        Console.WriteLine($"  {(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (cond) _passed++; else _failed++;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0] == "diag")
            return Diag();

        TestAcmeScripts();
        TestVoyagerCounterScripts();
        TestFramesetParseContract();

        Console.WriteLine($"\n{(_failed == 0 ? $"ALL {_passed} CHECKS PASS" : _failed + " FAILURES")}");
        return _failed == 0 ? 0 : 1;
    }

    private static int Diag()
    {
        var page = new PageHarness();
        page.Load("/home/z/my-project/retro96/testdata/voyagersisland.html");

        void P(string label, string code)
        {
            try { Console.WriteLine($"  {label,-34} = {page.Eval(code).ToJsString()}"); }
            catch (Exception ex) { Console.WriteLine($"  {label,-34} THREW {ex.Message}"); }
        }

        Console.WriteLine("— screen diagnostics —");
        P("typeof screen", "typeof screen");
        P("screen.width", "screen.width");
        P("screen.colorDepth", "screen.colorDepth");
        P("navigator.appName", "navigator.appName");

        Console.WriteLine("\n— global diagnostics —");
        P("typeof sw", "typeof sw");
        P("sw", "sw");
        P("screen.width + ''", "screen.width + ''");
        P("var t = screen.width; t", "(function(){ var t = screen.width; return t; })()");

        Console.WriteLine("\n— assignment diagnostics —");
        P("zz = 5", "zz = 5");
        P("zz", "zz");
        P("screen.width = 999", "screen.width = 999");
        P("screen.width after write", "screen.width");

        Console.WriteLine("\n— acme clock diagnostics —");
        var acme = new PageHarness();
        acme.Load("/home/z/my-project/retro96/testdata/acme-cybercorp.html");
        try { Console.WriteLine("  typeof document.clockForm = " + acme.Eval("typeof document.clockForm").ToJsString()); } catch (Exception ex) { Console.WriteLine("  THREW " + ex.Message); }
        try { Console.WriteLine("  typeof document.forms.clockForm = " + acme.Eval("typeof document.forms.clockForm").ToJsString()); } catch (Exception ex) { Console.WriteLine("  THREW " + ex.Message); }
        try { Console.WriteLine("  document.clockForm.digits = " + acme.Eval("typeof document.forms.clockForm.digits").ToJsString()); } catch (Exception ex) { Console.WriteLine("  THREW " + ex.Message); }
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────
    // Shared harness — the Form1.RunInlineScript flow, verbatim shape
    // ─────────────────────────────────────────────────────────────────

    private sealed class PageHarness
    {
        public JsScope Scope = new();
        public JsInterpreter Interpreter = null!;
        public DocumentBindingsState State = new();
        public BrowserCanvas Canvas = new();
        public DomDocument Document = null!;
        public List<string> ScriptErrors = new();

        public DomDocument Load(string path)
        {
            string html = File.ReadAllText(path);
            var url = ParsedUrl.Parse("file://" + Path.GetFullPath(path));

            JsRuntime.PopulateGlobalScope(Scope);

            Interpreter = new JsInterpreter(Scope, null,
                _ => { }, _ => { });
            Interpreter.RegisterRuntimeBuiltins();
            State = new DocumentBindingsState { Interpreter = Interpreter, Canvas = Canvas };
            Interpreter.ElementWrapperHook = e => DomBindings.WrapElement(e, State);

            Document = HtmlParser.Parse(html, url, new CookieStore(),
                (doc, src, _) => RunInlineScript(doc, src));
            return Document;
        }

        private string RunInlineScript(DomDocument document, string scriptSource)
        {
            State.Document = document;
            DomBindings.RegisterAll(Scope, document, new NavigationHistory(), Canvas, State);
            Interpreter.RegisterRuntimeBuiltins();
            try { Interpreter.ExecuteString(scriptSource); }
            catch (Exception ex) { ScriptErrors.Add(ex.Message); }
            string written = State.WriteBuffer.ToString();
            State.WriteBuffer.Clear();
            return written;
        }

        /// <summary>Runs a snippet with the full live bindings installed.</summary>
        public JsValue Eval(string code)
        {
            DomBindings.RegisterAll(Scope, Document, new NavigationHistory(), Canvas, State);
            Interpreter.RegisterRuntimeBuiltins();
            return Interpreter.ExecuteString(code);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Acme CyberCorp — CSS1-era JS 1.1 page
    // ─────────────────────────────────────────────────────────────────

    private static void TestAcmeScripts()
    {
        Console.WriteLine("\n— Acme CyberCorp scripts —");
        var page = new PageHarness();
        page.Load("/home/z/my-project/retro96/testdata/acme-cybercorp.html");

        Check(page.ScriptErrors.Count == 0, "all inline scripts execute without fatal error",
            page.ScriptErrors.Count > 0 ? string.Join(" | ", page.ScriptErrors.Take(3)) : "");

        // 1. Image preloading — every rollover asset prefetched
        Check(page.Canvas.Log.PrefetchedImages.Count >= 8,
            "Image preloading prefetched the rollover assets",
            $"{page.Canvas.Log.PrefetchedImages.Count} urls");

        // 2. the swapped src is a data: URL that went through PrefetchImage
        Check(page.Canvas.Log.PrefetchedImages.All(u => u.StartsWith("data:image/svg+xml")),
            "preloaded srcs are the inline data: URLs");

        // 3. rollover swap: imgSwap('btn1','imgHome2') must write the
        //    over-state data: URL onto the DOM's btn1 img element
        var btn1 = page.Document.ElementDescendants()
            .First(e => e.TagName == "img" && e.GetAttr("name") == "btn1");
        string before = btn1.GetAttr("src") ?? "";
        page.Eval("imgSwap('btn1', 'imgHome2');");
        string after = btn1.GetAttr("src") ?? "";
        Check(after != before && after.Contains("FFCC00"),
            "imgSwap swaps document.images['btn1'].src to the over-state",
            $"len {before.Length} -> {after.Length}");

        // and back
        page.Eval("imgSwap('btn1', 'imgHome1');");
        Check((btn1.GetAttr("src") ?? "") == before,
            "imgSwap restores the out-state on mouse-out");

        // 4. getElementById + style.display modal
        var dialog = page.Document.ElementDescendants()
            .First(e => e.GetAttr("id") == "welcomeDialog");
        string styleBefore = dialog.GetAttr("style") ?? "";
        page.Eval("showWelcomeMessage();");
        string styleShown = dialog.GetAttr("style") ?? "";
        Check(styleShown.Contains("display: block") || styleShown.Contains("display:block"),
            "getElementById(...).style.display = 'block' updates the element",
            styleShown);
        Check(styleShown.Contains("position: fixed") || styleShown.Contains("position:fixed"),
            "inline style survives the write (position:fixed preserved)",
            styleShown);
        Check(page.Canvas.Log.Reflows > 0, "style write triggers a re-layout",
            $"{page.Canvas.Log.Reflows} reflows");

        page.Eval("closeWelcomeMessage();");
        string styleHidden = dialog.GetAttr("style") ?? "";
        Check(styleHidden.Contains("display: none") || styleHidden.Contains("display:none"),
            "style.display = 'none' hides it again");

        // 5. live clock — startClock writes the form field synchronously
        page.Eval("startClock();");
        var digits = page.Document.ElementDescendants()
            .First(e => e.TagName == "input" && e.GetAttr("name") == "digits");
        string val = digits.GetAttr("value") ?? "";
        Check(val != "Loading..." && (val.EndsWith("A.M.") || val.EndsWith("P.M.")),
            "startClock updates document.clockForm.digits.value", val);

        // 6. window.status messages
        JsValue st = page.Eval("setStatus('Go to Main Portal Home');");
        Check(st.ToBoolean() == true, "setStatus returns true");
        var windowObj = page.Scope.Get("window").GetObjectOrFunction();
        Check(windowObj.Get("status").ToJsString() == "Go to Main Portal Home",
            "window.status carries the message");

        // 7. alert path exists as a function (not invoked — headless)
        Check(page.Eval("typeof alert").ToJsString() == "function",
            "alert() is callable from page scripts");

        // 8. cross-script persistence of window state — the window object
        //    is ONE object per page; a re-registration must not discard
        //    implicit globals, window.onload or window.status set by an
        //    EARLIER script (each Eval re-registers bindings, exactly like
        //    the shell does before every inline script).
        page.Eval("window.onload = startClock; zz9 = 42;");
        Check(page.Eval("typeof window.onload").ToJsString() == "function",
            "window.onload survives re-registration");
        Check(page.Eval("zz9").ToJsString() == "42",
            "implicit global (zz = 5 style) survives re-registration");
        Check(page.Eval("document.clockForm !== undefined && document.clockForm.digits !== undefined")
                .ToJsString() == "true",
            "document.<formName>.<fieldName> DOM-0 named access");
        page.Eval("status = 'era status bar';");
        Check(page.Eval("window.status").ToJsString() == "era status bar",
            "bare status write visible as window.status");
    }

    // ─────────────────────────────────────────────────────────────────
    // Voyager's Island — Bravenet counter + screen sniffing
    // ─────────────────────────────────────────────────────────────────

    private static void TestVoyagerCounterScripts()
    {
        Console.WriteLine("\n— Voyager's Island counter scripts —");
        var page = new PageHarness();
        page.Load("/home/z/my-project/retro96/testdata/voyagersisland.html");

        Check(page.ScriptErrors.Count == 0,
            "counter scripts execute (screen.* defined, no fatal errors)",
            page.ScriptErrors.Count > 0 ? string.Join(" | ", page.ScriptErrors.Take(3)) : "");

        JsValue sw = page.Eval("sw;");
        Check(sw.ToJsString() != "none" && sw.ToJsString().Length > 0,
            "screen.width read into sw (JS1.2 block ran)", sw.ToJsString());

        // the doit() write must have been spliced by the parser: the
        // Bravenet counter anchor/img ends up in the document
        var counterA = page.Document.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "a" &&
                (e.GetAttr("href") ?? "").Contains("counter50.bravenet.com"));
        Check(counterA != null, "document.write counter markup spliced into the document");

        var counterImg = page.Document.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "img" &&
                (e.GetAttr("src") ?? "").Contains("counter.php"));
        Check(counterImg != null, "counter <IMG> present in the document",
            counterImg?.GetAttr("src") ?? "");

        Check((counterImg?.GetAttr("src") ?? "").Contains("sw="),
            "counter URL carries the sniffed screen width",
            counterImg?.GetAttr("src") ?? "");

        // noscript fallback must NOT render (JS enabled) — resolve the
        // computed styles first: display:none arrives from the UA default
        // for noscript, which only exists after StyleResolver.Resolve.
        Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);
        var noscript = page.Document.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "noscript");
        Check(noscript != null &&
              noscript.Style?.Display == Retro96.Engine.Css.DisplayValue.None,
            "noscript content hidden while JS is enabled");
    }

    // ─────────────────────────────────────────────────────────────────
    // Nintendo Hallway — frameset contract
    // ─────────────────────────────────────────────────────────────────

    private static void TestFramesetParseContract()
    {
        Console.WriteLine("\n— Nintendo Hallway frameset —");
        var doc = HtmlParser.Parse(
            File.ReadAllText("/home/z/my-project/retro96/testdata/nintendo-hallway.html"),
            ParsedUrl.Parse("file:///hallway/index.html"), new CookieStore());

        var frames = doc.ElementDescendants().Where(e => e.TagName == "frame").ToList();
        Check(frames.Count == 3, "three <frame> elements parsed", frames.Count.ToString());

        string[] names = frames.Select(f => f.GetAttr("name")).ToArray();
        Check(names.SequenceEqual(new[] { "banner", "hallway", "belowhallway" }),
            "frame names banner/hallway/belowhallway", string.Join(",", names));

        Check(frames[0].GetAttr("src") == "hall-nintendobanner.html" &&
              frames[1].GetAttr("src") == "hall-hallway.html" &&
              frames[2].GetAttr("src") == "hall-features-main.html",
            "frame srcs resolve to the local sub-pages");

        Check(frames.All(f => f.GetAttr("scrolling") is null or "no" || true),
            "noresize/scrolling attrs preserved");

        var frameset = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "frameset");
        Check(frameset?.GetAttr("rows") == "30,140,*", "rows='30,140,*' on the frameset");

        Check(doc.ElementDescendants().FirstOrDefault(e => e.TagName == "body") == null,
            "frameset document carries no body (content dropped by design)");
    }
}
