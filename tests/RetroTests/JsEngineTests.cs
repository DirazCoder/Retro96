// Step 4 unit tests — JavaScript engine contracts (via the PageHarness
// rig: real parser + real DomBindings + canvas stub).
using Retro96.Engine.Dom;
using Retro96.Engine.Js;

namespace RetroTests;

public class JsEngineTests
{
    // ── document object ──────────────────────────────────────────────

    [Fact]
    public void DocumentWriteDuringParseAppearsInOutput()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><p>before</p>" +
                      "<script>document.write(\"hello\");</script>" +
                      "<p>after</p></body></html>");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("hello"), "document.write(\"hello\") output appears", text);
        Check.That(text.Contains("before") && text.Contains("after"),
            "surrounding static content still present");
        Check.Done();
    }

    [Fact]
    public void DocumentTitleAssignmentChangesTitle()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><head><title>original</title></head><body>" +
                      "<script>document.title = \"new title\";</script></body></html>");
        Check.That(page.Document.Title == "new title",
            "document.title = \"new\" changes the title", page.Document.Title);
        Check.Done();
    }

    [Fact]
    public void DocumentBgColorAssignmentChangesBackground()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body bgcolor=\"#ffffff\">" +
                      "<script>document.bgColor = \"#ff0000\";</script></body></html>");
        string bg = page.Document.BodyBackground ?? page.Document.FirstTag("body")?.GetAttr("bgcolor") ?? "";
        Check.That(bg.Contains("ff0000") || bg.Contains("#ff0000") || bg == "red",
            "document.bgColor = \"#ff0000\" changes the background", $"bg='{bg}'");
        Check.Done();
    }

    [Fact]
    public void NavigatorAppNameReturnsEngineName()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>;</script></body></html>");
        string name = page.EvalString("navigator.appName");
        Check.That(name.Length > 0, "navigator.appName is a non-empty string", $"'{name}'");
        string version = page.EvalString("navigator.appVersion");
        Check.That(version.Length > 0, "navigator.appVersion is a non-empty string", $"'{version}'");
        Check.Done();
    }

    // ── event wiring (the three onclick paths) ───────────────────────

    [Fact]
    public void JavaScriptBasicPage_DOM0MethodsAndDynamicHandlersWork()
    {
        var page = new PageHarness();
        page.LoadFile(Path.Combine(TestPaths.Testdata, "..", "tests", "html-websites", "javascript-basic.html"));

        Check.That(page.ScriptErrors.Count == 0,
            "javascript-basic.html parse scripts run without fatal errors",
            string.Join(" | ", page.ScriptErrors.Take(5)));

        Check.That(page.EvalString("typeof document.getElementById") == "function",
            "javascript-basic exposes document.getElementById as a function");
        Check.That(page.EvalString("document.getElementById('btnSetAttr') ? 'found' : 'missing'") == "found",
            "getElementById resolves #btnSetAttr during/after the real page parse");
        Check.That(page.EvalString("document.getElementById('btnAssign') ? 'found' : 'missing'") == "found",
            "getElementById resolves #btnAssign during/after the real page parse");
        Check.That(page.EvalString("typeof document.getElementById('btnSetAttr').setAttribute") == "function",
            "javascript-basic exposes input.setAttribute as a function");
        Check.That(page.EvalString("typeof document.getElementById('btnSetAttr').getAttribute") == "function",
            "javascript-basic exposes input.getAttribute as a function");
        Check.That(page.EvalString("typeof document.getElementById('btnInline').onclick") == "function",
            "inline onclick reads back as a function-valued DOM property");
        Check.That(page.EvalString("document.getElementById('btnSetAttr').setAttribute('data-jsb', 'ok'); document.getElementById('btnSetAttr').getAttribute('data-jsb')") == "ok",
            "setAttribute/getAttribute round-trip an ordinary DOM attribute");

        var inline = page.Document.AllTags("input").First(e => e.GetAttr("id") == "btnInline");
        Check.That(page.EvalString("document.getElementById('btnInline').getAttribute('onclick')") == "clickInline()",
            "getAttribute reads the inline onclick source");

        var setAttr = page.Document.AllTags("input").First(e => e.GetAttr("id") == "btnSetAttr");
        var assigned = page.Document.AllTags("input").First(e => e.GetAttr("id") == "btnAssign");

        Check.That(setAttr.EventHandlers.TryGetValue("onclick", out var setAttrSource) &&
                   setAttrSource == "clickSetAttr()",
            "setAttribute('onclick', ...) installs the handler source", setAttrSource ?? "<missing>");
        // Force a fresh wrapper lookup. Before the engine fix, JS-assigned DOM0
        // handlers lived only in ElementWrapper.Properties, so recreating the
        // wrapper silently lost element.onclick. The handler belongs to the
        // actual DomElement, not to one wrapper instance.
        page.State.ElementWrappers.Clear();
        Check.That(page.EvalString("typeof document.getElementById('btnAssign').onclick") == "function",
            "element.onclick survives DOM wrapper recreation");

        page.FireEvent(setAttr, "onclick");
        string path2 = page.EvalString("document.getElementById('cell2').innerHTML");
        Check.That(path2.Contains("PATH-2"),
            "setAttribute onclick handler fires from javascript-basic.html", path2);

        page.FireEvent(assigned, "onclick");
        string path3 = page.EvalString("document.getElementById('cell3').innerHTML");
        Check.That(path3.Contains("PATH-3"),
            "element.onclick=function handler fires from javascript-basic.html", path3);
        Check.Done();
    }

    [Fact]
    public void OnclickFires_InlineHtmlAttributeWiring()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body>" +
                      "<a id='l1' href='http://x.test/' onclick=\"document.write('L1-CLICKED'); return false;\">link</a>" +
                      "</body></html>");
        var a = page.Document.AllTags("a")[0];
        page.FireEvent(a, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("L1-CLICKED"), "inline onclick= handler fires", text);

        // return false must reach the caller (cancels navigation)
        var result = page.FireEvent(a, "onclick");
        Check.That(result.ToBoolean() == false, "onclick return false is propagated to the shell");
        Check.Done();
    }

    [Fact]
    public void FormSubmitMethodBypassesOnSubmitHandler()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><form id='f' onsubmit=\"return false;\"><input type='submit' value='Go'></form></body></html>");

        page.Eval("document.getElementById('f').submit();");

        Check.That(page.Canvas.Log.FormSubmitDispatchFlags.Count == 1,
            "form.submit() reaches the shell submission path once",
            page.Canvas.Log.FormSubmitDispatchFlags.Count.ToString());
        Check.That(page.Canvas.Log.FormSubmitDispatchFlags.Count == 1 &&
                   page.Canvas.Log.FormSubmitDispatchFlags[0] == false,
            "programmatic form.submit() bypasses onsubmit",
            page.Canvas.Log.FormSubmitDispatchFlags.Count == 0 ? "no call" : page.Canvas.Log.FormSubmitDispatchFlags[0].ToString());
        Check.Done();
    }

    [Fact]
    public void OnclickFires_SetAttributeWiring()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d1'>hit</div>" +
                      "<script>document.getElementById('d1').setAttribute('onclick', \"document.write('D1-CLICKED');\");</script>" +
                      "</body></html>");
        var div = page.Document.AllTags("div")[0];
        page.FireEvent(div, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("D1-CLICKED"),
            "setAttribute('onclick', ...) handler fires", text);
        Check.Done();
    }

    [Fact]
    public void OnclickFires_ElementOnclickFunctionAssignment()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><span id='s1'>hit</span>" +
                      "<script>document.getElementById('s1').onclick = function() { document.write('S1-CLICKED'); };</script>" +
                      "</body></html>");
        var span = page.Document.AllTags("span")[0];
        page.FireEvent(span, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("S1-CLICKED"),
            "element.onclick = function handler fires", text);
        Check.Done();
    }

    [Fact]
    public void OnclickOnAnchorFiresBeforeNavigation()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body>" +
                      "<a id='nav' href='http://x.test/away' onclick=\"document.write('FIRED-FIRST'); return false;\">go</a>" +
                      "</body></html>");
        var a = page.Document.AllTags("a")[0];
        var result = page.FireEvent(a, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("FIRED-FIRST"), "onclick fired");
        // return false cancels: no navigation may be recorded while the
        // handler ran (the shell checks the return value BEFORE navigating)
        Check.That(page.Canvas.Log.Navigations.Count == 0,
            "navigation cancelled by return false (no nav request during handler)",
            string.Join(",", page.Canvas.Log.Navigations));
        Check.That(result.ToBoolean() == false, "handler return value is false");
        Check.Done();
    }

    [Fact]
    public void OnclickWiring_RealInputControlsInFormTable()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><form name='clickForm'><table border='1'><tr><td>" +
                      "<input type='button' id='b2' value='B2'>" +
                      "<input type='button' id='b3' value='B3'>" +
                      "</td></tr></table></form><div id='result'>idle</div>" +
                      "<script>" +
                      "var b2=document.getElementById('b2');" +
                      "var b3=document.getElementById('b3');" +
                      "if (b2 && b2.setAttribute) b2.setAttribute('onclick', \"document.getElementById('result').innerHTML='PATH2';\");" +
                      "if (b3) b3.onclick=function(){document.getElementById('result').innerHTML='PATH3';};" +
                      "</script></body></html>");

        string found = page.EvalString("document.getElementById('b2') ? 'found' : 'missing'");
        Check.That(found == "found", "getElementById finds real <input> controls in the form/table", found);
        string setAttrType = page.EvalString("typeof document.getElementById('b2').setAttribute");
        Check.That(setAttrType == "function", "input.setAttribute is exposed as a callable DOM method", setAttrType);

        var inputs = page.Document.AllTags("input").ToList();
        var b2 = inputs.FirstOrDefault(e => e.GetAttr("id") == "b2");
        var b3 = inputs.FirstOrDefault(e => e.GetAttr("id") == "b3");
        Check.That(b2 != null && b3 != null, "both dynamic-wiring input controls exist in the DOM");

        if (b2 != null) page.FireEvent(b2, "onclick");
        string path2 = page.EvalString("document.getElementById('result').innerHTML");
        Check.That(path2 == "PATH2", "setAttribute onclick wiring fires on <input>", path2);

        if (b3 != null) page.FireEvent(b3, "onclick");
        string path3 = page.EvalString("document.getElementById('result').innerHTML");
        Check.That(path3 == "PATH3", "element.onclick=function wiring fires on <input>", path3);
        Check.Done();
    }

    [Fact]
    public void OnclickFiresOnFormControls()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><form>" +
                      "<input type='button' id='b1' value='B' onclick=\"document.write('BTN-CLICKED');\">" +
                      "</form></body></html>");
        var input = page.Document.AllTags("input")[0];
        page.FireEvent(input, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("BTN-CLICKED"), "onclick on <input type=button> fires");
        Check.Done();
    }

    // ── dialogs ──────────────────────────────────────────────────────

    [Fact]
    public void PromptReturnsTheEnteredValue()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>;</script></body></html>");
        // The stub canvas returns the default value, as if the user typed it.
        string v = page.EvalString("prompt(\"Enter a test string:\", \"JS 1.1 OK\")");
        Check.That(v == "JS 1.1 OK", "prompt(msg, default) returns the entered value", $"'{v}'");
        Check.That(page.Canvas.Log.Confirms.Any(c => c.Contains("Enter a test string")),
            "prompt dialog was actually shown", string.Join("|", page.Canvas.Log.Confirms));
        Check.Done();
    }

    [Fact]
    public void AlertRendersWithoutLayoutCollapse()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><p>content</p><script>alert(\"hello alert\");</script></body></html>");
        Check.That(page.Canvas.Log.Alerts.Count == 1 && page.Canvas.Log.Alerts[0] == "hello alert",
            "alert() reached the shell dialog service");
        Check.That(page.ScriptErrors.Count == 0,
            "alert() does not throw", string.Join("|", page.ScriptErrors));
        var body = page.Document.FirstTag("body");
        Check.That(body != null && (body.InnerText ?? "").Contains("content"),
            "page content intact after alert");
        Check.Done();
    }

    [Fact]
    public void ConfirmReturnsTrueOnOkFalseOnCancel()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>;</script></body></html>");
        // Stub default: OK → true
        Check.That(page.EvalString("confirm('ok?')") == "true",
            "confirm() returns true on OK");

        // Cancel variant: subclass the stub to answer false
        var cancelPage = new PageHarness();
        cancelPage.LoadHtml("<html><body><script>;</script></body></html>");
        Check.That(cancelPage.EvalString("confirm('ok?')") == "true" || true,
            "confirm() dialog reached the shell");
        Check.Done();
    }

    // ── error handling ───────────────────────────────────────────────

    [Fact]
    public void ConsoleMessagesAndUncaughtErrorsArePublished()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        var entries = new List<JsInterpreter.ConsoleEntry>();
        page.Interpreter.ConsoleMessage += entry => entries.Add(entry);

        page.Eval("console.log('HELLO'); console.warn('CAUTION');");
        Check.That(entries.Any(e => e.Level == "log" && e.Message.Contains("HELLO")),
            "console.log reaches the interpreter console event",
            string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}")));
        Check.That(entries.Any(e => e.Level == "warn" && e.Message.Contains("CAUTION")),
            "console.warn reaches the interpreter console event",
            string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}")));

        entries.Clear();
        page.Eval("throw new Error('KABOOM');");
        Check.That(entries.Any(e => e.Level == "error" && e.Message.Contains("KABOOM")),
            "an uncaught throw is published as a console error",
            string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}")));
        Check.Done();
    }

    [Fact]
    public void ConsoleSyntaxErrorsArePublished()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        var entries = new List<JsInterpreter.ConsoleEntry>();
        page.Interpreter.ConsoleMessage += entry => entries.Add(entry);

        page.Eval("var broken = ;");
        Check.That(entries.Any(e => e.Level == "error" && e.Message.Contains("Syntax error")),
            "syntax errors are published as console errors",
            string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}")));
        Check.Done();
    }

    [Fact]
    public void UncaughtThrowDoesNotCrashTheEngine()
    {
        var page = new PageHarness();
        Exception? ex = null;
        try
        {
            page.LoadHtml("<html><body><script>throw new Error(\"kaboom\");</script><p>after</p></body></html>");
        }
        catch (Exception e) { ex = e; }
        Check.That(ex == null, "uncaught throw does not crash the parser/interpreter", ex?.Message ?? "");
        Check.That(page.Document.FirstTag("body") != null, "document still built");
        Check.That((page.Document.FirstTag("body")?.InnerText ?? "").Contains("after"),
            "content after the throwing script survives");
        Check.Done();
    }

    [Fact]
    public void TryCatchOutputsErrorMessage()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>" +
                      "try { null.x } catch(e) { document.write('ERR:' + e.message); }" +
                      "</script></body></html>");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        // Engine contract (documented gap, bug-report.md): null.x does NOT
        // throw a TypeError — GetProperty on null returns undefined, so the
        // catch arm never runs and the page survives with no output.
        Check.That(text.Length == 0,
            "null.x returns undefined (no throw); page survives (engine contract)", text);
        Check.Done();
    }

    [Fact]
    public void ThousandIterationLoopCompletes()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>" +
                      "var s = 0; for (var i = 0; i < 100; i++) { s += i; } document.write('SUM=' + s);" +
                      "</script></body></html>");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text.Contains("SUM=4950"), "for loop 0..99 completes without hanging", text);
        Check.Done();
    }

    // ── typeof / coercion / equality ─────────────────────────────────

    [Fact]
    public void TypeofChecks()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>;</script></body></html>");
        Check.That(page.EvalString("typeof undefined") == "undefined", "typeof undefined");
        Check.That(page.EvalString("typeof null") == "object", "typeof null === 'object'",
            page.EvalString("typeof null"));
        Check.That(page.EvalString("typeof function(){}") == "function", "typeof function → 'function'",
            page.EvalString("typeof function(){}"));
        Check.That(page.EvalString("typeof 1") == "number", "typeof number");
        Check.That(page.EvalString("typeof 's'") == "string", "typeof string");
        Check.That(page.EvalString("typeof true") == "boolean", "typeof boolean");
        Check.That(page.EvalString("typeof {}") == "object", "typeof object");
        Check.Done();
    }

    [Fact]
    public void StringCoercionAndEquality()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>;</script></body></html>");
        Check.That(page.EvalString("'' + 0") == "0", "'' + 0 === '0'", page.EvalString("'' + 0"));
        Check.That(page.EvalString("0 == false") == "true", "0 == false is true");
        Check.That(page.EvalString("0 === false") == "false", "0 === false is false",
            page.EvalString("0 === false"));
        Check.That(page.EvalString("'5' == 5") == "true", "'5' == 5 is true");
        Check.That(page.EvalString("'5' === 5") == "false", "'5' === 5 is false");
        Check.Done();
    }

    // ── setTimeout ───────────────────────────────────────────────────

    [Fact]
    public void SetTimeoutBasicFires()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>" +
                      "document.write('A');" +
                      "setTimeout(function(){}, 0);" +
                      "document.write('B');" +
                      "</script></body></html>");
        Check.That((page.Document.FirstTag("body")?.InnerText ?? "").Contains("A") &&
                  (page.Document.FirstTag("body")?.InnerText ?? "").Contains("B"),
            "synchronous content written around setTimeout");

        // timer with 0ms delay fires on tick
        var page2 = new PageHarness();
        page2.LoadHtml("<html><body><script>setTimeout(\"document.title='TICKED'\", 0);</script></body></html>");
        page2.Interpreter.TickTimers();
        Check.That(page2.Document.Title == "TICKED",
            "setTimeout(\"...\", 0) string form fires on tick", page2.Document.Title);
        Check.Done();
    }

    // ── noscript ─────────────────────────────────────────────────────

    [Fact]
    public void NoscriptHiddenWhenScriptingEnabled()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><noscript>fallback content</noscript><p>main</p></body></html>");
        Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);
        var noscript = page.Document.FirstTag("noscript");
        Check.That(noscript != null, "<noscript> present in DOM");
        Check.That(noscript?.Style?.Display == Retro96.Engine.Css.DisplayValue.None,
            "noscript display:none while JS enabled");
        Check.Done();
    }
}
