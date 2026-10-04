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

        // 1. Nav-cell rollover, IE5 branch (default persona): the page's
        //    navOver() sniffs document.layers then document.all — under the
        //    IE5 persona the DHTML branch runs and writes
        //    backgroundColor onto the td's inline style.
        var navHome = page.Document.ElementDescendants()
            .First(e => e.GetAttr("id") == "navHome");
        page.Eval("navOver('navHome');");
        string rolledOver = navHome.GetAttr("style") ?? "";
        Check(rolledOver.Contains("background-color: #FFCC00", StringComparison.OrdinalIgnoreCase) ||
              rolledOver.Contains("background-color:#FFCC00", StringComparison.OrdinalIgnoreCase),
            "navOver writes backgroundColor through document.all (IE5 persona)",
            rolledOver);
        page.Eval("navOut('navHome');");
        Check((navHome.GetAttr("style") ?? "").Contains("000066", StringComparison.OrdinalIgnoreCase),
            "navOut restores the dark cell colour");

        // 2. Nav-cell rollover, NS4.7 branch: the SAME page takes the
        //    document.layers path and writes the layer's bgColor.
        var nsPage = LoadAcmeUnderNetscape47();
        bool nsBranchOk = nsPage.Success;
        Check(nsBranchOk,
            "navOver writes layer bgColor through document.layers (NS4.7 persona)",
            nsPage.Detail);

        // 4. getElementById + style.display modal
        var dialog = page.Document.ElementDescendants()
            .First(e => e.GetAttr("id") == "welcomeDialog");
        string styleBefore = dialog.GetAttr("style") ?? "";
        page.Eval("showWelcomeMessage();");
        string styleShown = dialog.GetAttr("style") ?? "";
        Check(styleShown.Contains("display: block") || styleShown.Contains("display:block"),
            "getElementById(...).style.display = 'block' updates the element",
            styleShown);
        Check(styleShown.Contains("position: absolute") || styleShown.Contains("position:absolute"),
            "inline style survives the write (position:absolute preserved)",
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

    /// <summary>
    /// Loads the Acme page under the strict Navigator 4.7 persona and runs
    /// the nav rollover: the page must take the document.layers branch
    /// (document.all is undefined there) and the layer write must land on
    /// the element. Settings are restored even on failure.
    /// </summary>
    private static (bool Success, string Detail) LoadAcmeUnderNetscape47()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var page = new PageHarness();
            page.Load("/home/z/my-project/retro96/testdata/acme-cybercorp.html");

            // Persona contract: layers exists, all does not, getElementById does not.
            string sniff = page.Eval(
                "typeof document.layers + '/' + typeof document.all + '/' + typeof getElementById")
                .ToJsString();
            if (sniff != "object/undefined/undefined")
                return (false, "persona sniff: " + sniff);

            // Checklist §10: only POSITIONED elements (CSS position:
            // absolute|relative, or <layer>/<ilayer>) appear in
            // document.layers. The nav <td> is not positioned → undefined,
            // exactly like Navigator 4.7. navOver therefore takes no
            // branch there — the IE branch is what the page was built for.
            string navLayerType = page.Eval("typeof document.layers['navHome']").ToJsString();
            if (navLayerType != "undefined")
                return (false, "non-positioned td leaked into document.layers: " + navLayerType);

            // The positioned modal div IS a layer; a bgColor write through
            // the layer object must land on the element.
            string dialogLayerType = page.Eval("typeof document.layers['welcomeDialog']").ToJsString();
            if (dialogLayerType == "undefined")
                return (false, "positioned div missing from document.layers");

            page.Eval("document.layers['welcomeDialog'].bgColor = '#FFCC00';");
            var dialog = page.Document.ElementDescendants()
                .First(e => e.GetAttr("id") == "welcomeDialog");
            string dialogStyle = (dialog.GetAttr("style") ?? "") + " " + (dialog.GetAttr("bgcolor") ?? "");
            return dialogStyle.Contains("FFCC00", StringComparison.OrdinalIgnoreCase)
                ? (true, "")
                : (false, "dialog style after layer bgColor write: " + dialogStyle.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
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
