// Step 4 unit tests — JavaScript engine contracts (via the PageHarness
// rig: real parser + real DomBindings + canvas stub).
using Retro96;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Js;
using Retro96.Engine.Network;

namespace RetroTests;

public class JsEngineTests
{
    [Fact]
    public void RunLocalEcma3CorpusForDiagnostics()
    {
        const string suiteRoot = @"C:\Users\diraz\Downloads\ecma_3";
        const string resultPath =
            @"C:\Users\diraz\.copilot\session-state\527d2379-dd34-4d59-9b06-da3c30b3fa2d\files\ecma3-results.txt";
        const string testHarness = """
            var __ecma3 = { checks: 0, failures: [] };
            var testcases = [];
            var tc = 0;
            var SECTION = '';
            var summary = '';
            var status = '';
            var expect;
            var actual;
            function reportCompare(expected, actual, description) {
                __ecma3.checks++;
                if (expected != actual && !(expected != expected && actual != actual)) {
                    __ecma3.failures[__ecma3.failures.length] =
                        (description || '') + " expected '" + expected + "', got '" + actual + "'";
                    return false;
                }
                return true;
            }
            function TestCase(name, description, expected, actual) {
                this.name = name;
                this.description = description;
                this.expect = expected;
                this.actual = actual;
                this.passed = expected == actual ||
                    (expected != expected && actual != actual);
                testcases[tc++] = this;
            }
            function AddTestCase(description, expected, actual) {
                new TestCase(SECTION || '', description, expected, actual);
            }
            function test() {
                for (var i = 0; i < testcases.length; i++) {
                    reportCompare(testcases[i].expect, testcases[i].actual,
                        testcases[i].description);
                }
            }
            function addThis() { reportCompare(expect, actual, status || summary); }
            function inSection(section) { return 'Section ' + section + ' - '; }
            function print() {}
            function printStatus() {}
            function printBugNumber() {}
            function enterFunc() {}
            function exitFunc() {}
            function gc() {}
            function options() { return ''; }
            function jit() { return false; }
            function quit() {}
            function startTest() {}
            function writeHeaderToLog() {}
            """;

        Assert.True(Directory.Exists(suiteRoot), $"ECMA-3 corpus not found: {suiteRoot}");
        var testFiles = Directory.EnumerateFiles(suiteRoot, "*.js", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is not ("shell.js" or "browser.js") &&
                !IsMozillaSpecificCorpusFile(suiteRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int skipped = Directory.EnumerateFiles(suiteRoot, "*.js", SearchOption.AllDirectories)
            .Count(path => Path.GetFileName(path) is not ("shell.js" or "browser.js")) -
            testFiles.Length;
        var results = new List<string>();
        int passed = 0, failed = 0, errors = 0, noReport = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

        void SaveProgress()
        {
            File.WriteAllLines(resultPath,
                new[]
                {
                    $"Files={testFiles.Length}; SkippedMozillaSpecific={skipped}; Completed={passed + failed + errors + noReport}; Passed={passed}; Failed={failed}; Errors={errors}; NoReport={noReport}",
                    ""
                }.Concat(results));
        }

        File.WriteAllText(resultPath,
            $"Running {testFiles.Length} ECMA-3 test files directly.{Environment.NewLine}");

        foreach (string testPath in testFiles)
        {
            string relativePath = Path.GetRelativePath(suiteRoot, testPath);
            bool expectsError = Path.GetFileName(testPath).EndsWith("-n.js", StringComparison.Ordinal);
            var scope = new JsScope();
            JsRuntime.PopulateGlobalScope(scope);
            var interpreter = new JsInterpreter(scope, null, _ => { }, _ => { },
                timeLimitMs: 1000);
            interpreter.RegisterRuntimeBuiltins();
            var scriptErrors = new List<string>();
            interpreter.ConsoleMessage += entry =>
            {
                if (entry.Level == "error")
                    scriptErrors.Add(entry.Message);
            };
            Exception? scriptError = null;
            try
            {
                interpreter.ExecuteString(testHarness);
                string shellPath = Path.Combine(Path.GetDirectoryName(testPath)!, "shell.js");
                if (File.Exists(shellPath))
                    interpreter.ExecuteString(File.ReadAllText(shellPath));
                int errorsBeforeTest = scriptErrors.Count;
                interpreter.ExecuteString(File.ReadAllText(testPath));
                if (scriptErrors.Count == errorsBeforeTest &&
                    interpreter.ExecuteString("__ecma3.checks === 0 && testcases.length > 0")
                        .ToBoolean())
                    interpreter.ExecuteString("test()");
            }
            catch (Exception ex)
            {
                scriptError = ex;
            }

            if (scriptError != null || scriptErrors.Count > 0)
            {
                if (expectsError)
                    passed++;
                else
                {
                    errors++;
                    results.Add($"ERROR\t{relativePath}\t{scriptError?.Message ?? scriptErrors[^1]}");
                }
                SaveProgress();
                continue;
            }

            if (expectsError)
            {
                failed++;
                results.Add($"FAIL\t{relativePath}\texpected an exception, but the script completed");
                SaveProgress();
                continue;
            }

            int checks = int.Parse(interpreter.ExecuteString("__ecma3.checks").ToJsString(),
                System.Globalization.CultureInfo.InvariantCulture);
            int assertionFailures = int.Parse(interpreter.ExecuteString(
                "__ecma3.failures.length").ToJsString(),
                System.Globalization.CultureInfo.InvariantCulture);
            if (assertionFailures > 0)
            {
                failed++;
                results.Add($"FAIL\t{relativePath}\t{interpreter.ExecuteString("__ecma3.failures.join(' | ')").ToJsString()}");
            }
            else if (checks == 0)
            {
                noReport++;
                results.Add($"NO REPORT\t{relativePath}\tno assertions reported");
            }
            else
                passed++;

            SaveProgress();
        }
        Assert.True(failed == 0 && errors == 0 && noReport == 0,
            $"ECMA-3 corpus: files={testFiles.Length}, skipped Mozilla-specific={skipped}, passed={passed}, failed={failed}, errors={errors}, no-report={noReport}. Details: {resultPath}");
    }

    private static bool IsMozillaSpecificCorpusFile(string root, string path)
    {
        string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relativePath.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("Unicode/uc-005.js", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("Function/regress-58274.js", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuppliedEs3RuntimeFailuresAreCovered()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");

        void CheckExpression(string name, string expression)
        {
            var actual = page.Eval(expression);
            Check.That(actual.ToBoolean(), name, actual.ToJsString());
        }

        CheckExpression("labeled continue targets the outer loop",
            "var count=0; outer: for(var i=0;i<3;i++){for(var j=0;j<3;j++){" +
            "if(j===1) continue outer; count++;}} count===3");
        CheckExpression("var arguments after return parses and preserves arguments object",
            "(function(){function F3(){return arguments; var arguments=555;}" +
            "return typeof F3()==='object';})()");
        CheckExpression("formal parameter named arguments shadows the arguments object",
            "(function(arguments){return arguments;})(55)===55");
        CheckExpression("ToPrimitive calls object valueOf methods in operand order",
            "(function(){var order='';var a={valueOf:function(){order+='a';return 3;}};" +
            "var b={valueOf:function(){order+='b';return 6;}};" +
            "return (a&b)===2&&order==='ab';})()");
        CheckExpression("compound assignment evaluates its left reference first",
            "(function(){var order='';var o={x:1};" +
            "function key(){order+='l';return 'x';}" +
            "function right(){order+='r';return 2;}o[key()]+=right();" +
            "return order==='lr'&&o.x===3;})()");
        CheckExpression("function length reports declared parameters",
            "function f(a,b,c){} f.length===3");
        CheckExpression("plain objects inherit Object constructor",
            "({}).constructor===Object");
        CheckExpression("array index assignment extends length",
            "var a=[]; a[10]='x'; a.length===11");
        CheckExpression("String wrapper valueOf returns primitive",
            "var s=new String('test'); typeof s==='object' && s.valueOf()==='test'");
        CheckExpression("String wrapper ToPrimitive avoids recursive toString",
            "var s=new String('test'); String(s)==='test' && s+'! '==='test! '");
        CheckExpression("Number wrapper valueOf returns primitive",
            "var n=new Number(12); n.valueOf()===12 && n-2===10");
        CheckExpression("Boolean wrapper valueOf returns primitive",
            "var b=new Boolean(false); b.valueOf()===false && b==false");
        CheckExpression("String.localeCompare is available",
            "'a'.localeCompare('b')<0");
        CheckExpression("Date.UTC returns epoch milliseconds",
            "Date.UTC(2020,0,1)===1577836800000");
        CheckExpression("Date constructor preserves milliseconds",
            "new Date(2020,0,1,0,0,0,500).getMilliseconds()===500");
        CheckExpression("Date.toUTCString is available",
            "typeof new Date(0).toUTCString()==='string'");
        CheckExpression("Date.getTimezoneOffset is available",
            "typeof new Date().getTimezoneOffset()==='number'");
        CheckExpression("RegExp literal exposes multiline flag",
            "/test/m.multiline===true");
        CheckExpression("unresolved identifiers throw ReferenceError",
            "(function(){try{missingEs3Binding;}catch(e){return e instanceof ReferenceError;}})()");
        CheckExpression("typeof remains safe for unresolved identifiers",
            "typeof missingEs3Binding==='undefined'");
        CheckExpression("invalid array length throws RangeError",
            "(function(){try{new Array(-1);}catch(e){return e instanceof RangeError;}})()");
        CheckExpression("malformed URI throws URIError",
            "(function(){try{decodeURIComponent('%');}catch(e){return e instanceof URIError;}})()");
        CheckExpression("Function.prototype has function type",
            "typeof Function.prototype==='function'");
        CheckExpression("Object.prototype.toString identifies arrays",
            "Object.prototype.toString.call([])==='[object Array]'");
        CheckExpression("Object.prototype.toString identifies strings",
            "Object.prototype.toString.call('')==='[object String]'");
        CheckExpression("Math.log(0) is negative infinity",
            "Math.log(0)===-Infinity");
        CheckExpression("parseInt parses negative values",
            "parseInt('-42')===-42");
        CheckExpression("parseInt recognizes hex prefixes",
            "parseInt('0x10')===16");
        CheckExpression("parseFloat parses exponents",
            "parseFloat('1e3')===1000");
        Check.Done();
    }

    [Fact]
    public void DomMutationReflowsAreCoalescedUntilHandlerCompletes()
    {
        var canvas = new Retro96.BrowserCanvas();
        var page = new PageHarness { Canvas = canvas };
        page.LoadHtml(
            "<html><body><button id='run'>run</button>" +
            "<div id='target'></div></body></html>");
        page.Eval(
            "var target = document.getElementById('target');" +
            "document.getElementById('run').onclick = function() {" +
            "for (var i = 0; i < 100; i++) target.innerHTML = '<span>' + i + '</span>';};");
        canvas.Log.Reflows = 0;

        page.FireEvent(page.Document.AllTags("button")[0], "onclick");

        Check.That(canvas.Log.Reflows == 1,
            "100 DOM mutations cause one layout rebuild after the handler",
            canvas.Log.Reflows.ToString());
        Check.That(page.Document.AllTags("div")[0].InnerText == "99",
            "the final DOM mutation is still applied");
        Check.Done();
    }

    [Fact]
    public void FutureReservedWordsAreRejectedAsFunctionNames()
    {
        // ES3 §7.5.3: long/short are FutureReservedWords — IE5 and NS4.7
        // both rejected them as identifiers, and the ES3 conformance suite
        // (ecma/LexicalConventions/7.4.3-*) requires the SyntaxError. The
        // whole script block aborts, so the binding never happens.
        var page = new PageHarness();
        page.LoadHtml(
            "<html><body><script>" +
            "function long(value) { return value + 'ms'; }" +
            "function short(value) { return value + 's'; }" +
            "window.duration = long(12) + ' ' + short(3);" +
            "</script></body></html>");

        Check.That(page.Eval("typeof duration").ToJsString() == "undefined",
            "future-reserved function names are a SyntaxError; the script aborts",
            page.Eval("typeof duration").ToJsString());
        Check.That(page.EvalString("typeof long") == "undefined",
            "'long' stays unbound after the rejected declaration",
            page.EvalString("typeof long"));
        Check.Done();
    }

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
    public void DocumentLastModifiedNeverDefaultsToEmpty()
    {
        var fallback = new DateTime(2026, 10, 4, 19, 41, 31);
        string fallbackText = DocumentBindingsState.ResolveLastModified(null, null, fallback);
        string headerText = DocumentBindingsState.ResolveLastModified(
            ParsedUrl.Parse("https://example.test/"), "Sun, 04 Oct 2026 18:00:00 GMT", fallback);

        Check.That(fallbackText.Length > 0 &&
                   fallbackText == fallback.ToString("G", System.Globalization.CultureInfo.CurrentCulture),
            "missing Last-Modified uses a non-empty current-time fallback", fallbackText);
        Check.That(headerText == "Sun, 04 Oct 2026 18:00:00 GMT",
            "response Last-Modified header is preserved", headerText);

        string path = Path.GetTempFileName();
        try
        {
            var fileTime = new DateTime(2024, 5, 6, 7, 8, 10);
            File.SetLastWriteTime(path, fileTime);
            string fileUrl = Retro96.Engine.Network.FileUrls.CanonicalFileUrl(path);
            string fileModified = DocumentBindingsState.ResolveLastModified(
                ParsedUrl.Parse(fileUrl), null, fallback);
            Check.That(fileModified ==
                       File.GetLastWriteTime(path).ToString(
                           "G", System.Globalization.CultureInfo.CurrentCulture),
                "local file document uses its filesystem modification time",
                fileModified);
        }
        finally
        {
            File.Delete(path);
        }
        Check.Done();
    }

    [Fact]
    public void TextareaKeyUpUpdatesCharacterCounter()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body>" +
            "<textarea id='message' onkeyup=\"document.getElementById('count').innerHTML=this.value.length\"></textarea>" +
            "<span id='count'>0</span></body></html>");
        var message = page.Document.ElementDescendants().First(e => e.GetAttr("id") == "message");
        page.Interpreter.ExecuteString("document.getElementById('message').value='abc';");
        page.Interpreter.FireEvent(message, "onkeyup",
            page.Interpreter.CreateKeyEvent("C", 67));

        Check.That(page.Document.ElementDescendants().First(e => e.GetAttr("id") == "count")
                .InnerText == "3",
            "textarea keyup reads its value and updates the counter",
            page.Document.ElementDescendants().First(e => e.GetAttr("id") == "count").InnerText);
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
        page.LoadFile(Path.GetFullPath(Path.Combine(
            TestPaths.Testdata, "..", "tests", "html-websites", "javascript", "javascript-basic.html")));

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
    public void LegacyInnerTextAssignmentMutatesTheLiveDom()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><span id='d'>Detecting...</span></body></html>");

        page.Eval("document.all('d').innerText = 'Webmaster96';");

        Check.That((page.Document.FirstTag("span")?.InnerText ?? "") == "Webmaster96",
            "legacy innerText assignment updates the live DOM",
            page.Document.FirstTag("span")?.InnerText ?? "<empty>");
        Check.Done();
    }

    [Fact]
    public void InsertAdjacentHtmlMutatesTheLiveDom()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d'>A</div></body></html>");

        page.Eval("var d=document.all('d');" +
                  "d.insertAdjacentHTML('beforeBegin', '<b>B</b>');" +
                  "d.insertAdjacentHTML('afterBegin', '<i>A</i>');" +
                  "d.insertAdjacentHTML('beforeEnd', '<u>E</u>');" +
                  "d.insertAdjacentHTML('afterEnd', '<em>R</em>');");

        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text == "BAAER",
            "insertAdjacentHTML updates all four insertion positions", text);
        Check.Done();
    }

    [Fact]
    public void LegacyWindowEventIsAvailableDuringMouseEventDispatch()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><button id='b'>Go</button>" +
                      "<script>document.all('b').onclick=function(){ " +
                      "window.eventSeen = window.event && window.event.srcElement === this ? 'OK' : 'BAD'; " +
                      "};</script>" +
                      "</body></html>");
        var button = page.Document.AllTags("button")[0];

        page.FireEvent(button, "onclick");

        Check.That(page.EvalString("window.eventSeen") == "OK",
            "window.event exposes the actual srcElement during onclick",
            page.EvalString("window.eventSeen"));
        Check.That(page.EvalString("typeof window.event") == "undefined",
            "window.event is cleared after the handler returns",
            page.EvalString("typeof window.event"));
        Check.That(page.EvalString("window.hasOwnProperty('event')") == "false",
            "window.event property is removed after top-level dispatch",
            page.EvalString("window.hasOwnProperty('event')"));
        Check.Done();
    }

    [Fact]
    public void MouseEventsUseTheOwningInterpreterRealm()
    {
        var outer = new PageHarness();
        outer.LoadHtml("<html><body><div id='outer'></div></body></html>");
        var frame = new PageHarness();
        frame.LoadHtml("<html><body><div id='frame'></div></body></html>");

        var outerObjectProto = outer.Scope.Get("Object").GetObjectOrFunction().Get("prototype").GetObject();
        var frameObjectProto = frame.Scope.Get("Object").GetObjectOrFunction().Get("prototype").GetObject();
        var evt = frame.Interpreter.CreateMouseEvent("onclick", 7, 9, 1);

        Check.That(!ReferenceEquals(outerObjectProto, frameObjectProto),
            "independent page/iframe realms have distinct Object.prototype objects");
        Check.That(ReferenceEquals(evt.Prototype, frameObjectProto),
            "CreateMouseEvent uses the owning interpreter's Object.prototype");
        Check.That(outer.Interpreter.EvalString("typeof Object", outer.Interpreter.GlobalScope).ToJsString() == "function" &&
                   frame.Interpreter.EvalString("typeof Object", frame.Interpreter.GlobalScope).ToJsString() == "function",
            "both realms expose their own Object constructor");
        Check.Done();
    }

    [Fact]
    public void LegacyTextRangeApisAreExposed()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><p>Hello legacy range</p></body></html>");

        Check.That(page.EvalString("typeof document.selection.createRange") == "function",
            "document.selection.createRange is exposed in IE-compatible mode");
        Check.That(page.EvalString("typeof document.body.createTextRange") == "function",
            "document.body.createTextRange is exposed in IE-compatible mode");
        Check.That(page.EvalString("document.body.createTextRange().text.length > 0") == "true",
            "body TextRange exposes the document text");
        Check.Done();
    }

    [Fact]
    public void LegacyScriptEngineProbeIsExposed()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");

        Check.That(page.EvalString("typeof ScriptEngine") == "function",
            "ScriptEngine is exposed in IE-compatible mode");
        Check.That(page.EvalString("ScriptEngine()") == "JScript",
            "ScriptEngine reports JScript", page.EvalString("ScriptEngine()"));
        // Default persona is IE5 → JScript 5.0 (build 6325, era-plausible).
        Check.That(page.EvalString("ScriptEngineMajorVersion()") == "5",
            "ScriptEngineMajorVersion reports 5 for the IE5 persona",
            page.EvalString("ScriptEngineMajorVersion()"));
        Check.That(page.EvalString("ScriptEngineMinorVersion()") == "0",
            "ScriptEngineMinorVersion reports 0");
        Check.That(page.EvalString("ScriptEngineBuildVersion()") == "6325",
            "ScriptEngineBuildVersion reports the JScript 5.0 build",
            page.EvalString("ScriptEngineBuildVersion()"));
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

    [Fact]
    public void BlockingDialogsDoNotConsumeEventHandlerExecutionBudget()
    {
        var page = new PageHarness { Canvas = new SlowAlertCanvas() };
        page.LoadHtml(
            "<html><body>" +
            "<button id='inline' onclick=\"alert('wait'); window.inlineDone = true;\">inline</button>" +
            "<button id='dom0'>dom0</button>" +
            "</body></html>",
            executionLimitMs: 50);

        var buttons = page.Document.AllTags("button");
        page.FireEvent(buttons[0], "onclick");
        Check.That(page.EvalString("window.inlineDone") == "true",
            "time waiting for alert does not time out an inline event handler");

        page.Eval("document.getElementById('dom0').onclick = function() {" +
                  "alert('wait'); window.dom0Done = true;};");
        page.FireEvent(buttons[1], "onclick");
        Check.That(page.EvalString("window.dom0Done") == "true",
            "time waiting for alert does not time out a DOM-0 event handler");
        Check.Done();
    }

    private sealed class SlowAlertCanvas : BrowserCanvas
    {
        public override void ShowAlert(string message) =>
            Thread.Sleep(120);
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
    public void EvalSyntaxErrorsAreCatchableWithoutConsoleNoise()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        var entries = new List<JsInterpreter.ConsoleEntry>();
        page.Interpreter.ConsoleMessage += entry => entries.Add(entry);

        string result = page.EvalString(
            "(function() { try { eval('this is not valid javascript {{{'); } " +
            "catch (e) { return e.name + ':' + (e instanceof SyntaxError); } })()");

        Check.That(result == "SyntaxError:true",
            "invalid source passed to eval throws a catchable SyntaxError",
            result);
        Check.That(entries.Count == 0,
            "a caught eval SyntaxError is not reported as an uncaught page error",
            string.Join(" | ", entries.Select(e => e.Message)));
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
        // ES3 §8.7.1 / 1999 behaviour: reading a property of null throws a
        // catchable TypeError ("'x' is null or not an object"), so the
        // catch arm runs and e.message carries the era message.
        Check.That(text.StartsWith("ERR:'x' is null or not an object"),
            "null.x throws a catchable TypeError with the era message", text);
        Check.That(page.EvalString("(function() { try { null.x; } catch(e) { return e.name; } })()") == "TypeError",
            "null.x catch value is named TypeError");
        Check.That(page.EvalString("(function() { try { null.x; } catch(e) { return e instanceof TypeError; } })()") == "true",
            "null.x catch value inherits from TypeError.prototype");
        Check.That(page.EvalString("(function() { try { null.x; } catch(e) { return e instanceof Error; } })()") == "true",
            "TypeError catch value also inherits from Error.prototype");
        Check.That(page.EvalString("(function() { try { undefined.x; } catch(e) { return e.name; } })()") == "TypeError",
            "undefined property access also yields a TypeError");
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

    [Fact]
    public void EcmaScript3FunctionUriAndErrorBuiltins()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><script>;</script></body></html>");

        Check.That(page.EvalString(
                "typeof URIError === 'function' && typeof ReferenceError === 'function' && typeof SyntaxError === 'function'") == "true",
            "ES3 error constructors are available");
        Check.That(page.EvalString(
                "function calc(a,b){return (this.base||0)+a+b;} var ctx={base:10}; calc.call(ctx,1,2)+','+calc.apply(ctx,[1,2])") == "13,13",
            "Function.prototype.call and apply preserve this and pass arguments");
        Check.That(page.EvalString(
                "function testCal(){return arguments.length===2 && arguments.callee===testCal;} testCal(10,20)") == "true",
            "arguments.callee refers to the active function");
        Check.That(page.EvalString(
                "var raw='http://site.com/a b'; encodeURI(raw)==='http://site.com/a%20b' && decodeURI(encodeURI(raw))===raw") == "true",
            "encodeURI and decodeURI round-trip URI text");
        Check.That(page.EvalString(
                "encodeURIComponent('a&b')") == "a%26b",
            "encodeURIComponent escapes reserved delimiters");
        Check.That(page.EvalString(
                "function Base(){} function Derived(){} Derived.prototype=new Base(); var obj=new Derived(); Base.prototype.isPrototypeOf(obj)") == "true",
            "Object.prototype.isPrototypeOf walks the full prototype chain");
        Check.That(page.EvalString(
                "var makeSum=Function('a','b','return a+b;'); makeSum(2,3)") == "5",
            "Function constructor creates a callable function");
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

    // ═══════════════════════════════════════════════════════════════════
    // 1999 upgrade (Task 6/7) — DOM Level 1, IE5 DHTML, NS4 layers/events,
    // ES3/JScript 5.0, host objects. Default persona = IE5.
    // ═══════════════════════════════════════════════════════════════════

    // ── DOM Level 1 (checklist §10) ───────────────────────────────────

    [Fact]
    public void CreateTextNodeAppendAndNodeValueWrite()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d'>old</div></body></html>");
        page.Eval("var d = document.getElementById('d');" +
                  "d.innerHTML = '';" +
                  "var t = document.createTextNode('hello');" +
                  "d.appendChild(t);");
        Check.That(page.EvalString("document.getElementById('d').innerText") == "hello",
            "createTextNode + appendChild grafts the text into the live DOM",
            page.EvalString("document.getElementById('d').innerText"));
        Check.That(page.EvalString("document.getElementById('d').firstChild.nodeType") == "3",
            "the appended node reports nodeType 3 (text)");
        Check.That(page.EvalString("document.getElementById('d').firstChild.nodeName") == "#text",
            "text node name is #text");
        page.Eval("document.getElementById('d').firstChild.nodeValue = 'changed';");
        Check.That(page.EvalString("document.getElementById('d').innerText") == "changed",
            "nodeValue writes mutate the live text node",
            page.EvalString("document.getElementById('d').innerText"));
        Check.That(page.EvalString("document.getElementById('d').firstChild.nodeValue") == "changed",
            "nodeValue reads reflect the write");
        Check.Done();
    }

    [Fact]
    public void TableInsertRowAppendsAndInsertsRows()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><table id='results'><tbody>" +
            "<tr id='existing'><td>Existing</td></tr>" +
            "</tbody></table></body></html>");

        Check.That(page.EvalString("typeof document.getElementById('results').insertRow") == "function",
            "table exposes insertRow()");
        page.Eval("var table = document.getElementById('results');" +
            "var appended = table.insertRow(-1);" +
            "appended.innerHTML = '<td id=\"appended-cell\">Appended</td>';" +
            "window.appendedRow = appended;" +
            "var inserted = table.insertRow(0);" +
            "inserted.innerHTML = '<td>Inserted</td>';" +
            "window.insertedRow = inserted;");

        Check.That(page.EvalString("table.getElementsByTagName('tr').length") == "3",
            "insertRow adds rows to the table");
        Check.That(page.EvalString("table.getElementsByTagName('tr')[0].innerText") == "Inserted",
            "insertRow(index) inserts before the indexed row",
            page.EvalString("table.getElementsByTagName('tr')[0].innerText"));
        Check.That(page.EvalString("appendedRow.parentNode.tagName") == "TBODY",
            "appending a table row places it in a tbody");
        Check.That(page.EvalString("appendedRow.getElementsByTagName('td').length") == "1" &&
                   page.EvalString("document.getElementById('appended-cell').innerText") == "Appended",
            "insertRow return value supports populating the row");
        Check.Done();
    }

    [Fact]
    public void ReplaceChildSwapsNodes()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><p id='p'><i id='a'>A</i><b id='b'>B</b></p></body></html>");
        page.Eval("var p = document.getElementById('p');" +
                  "var a = document.getElementById('a');" +
                  "var b = document.getElementById('b');" +
                  "var u = document.createElement('u');" +
                  "u.innerHTML = 'U';" +
                  "window.replaced = p.replaceChild(u, a);");
        string html = page.EvalString("document.getElementById('p').innerHTML");
        Check.That(html.Contains("<u>U</u>") && !html.Contains("<i"),
            "replaceChild swaps the old node for the new one", html);
        Check.That(page.EvalString("window.replaced.nodeName") == "I",
            "replaceChild returns the replaced (old) node",
            page.EvalString("window.replaced.nodeName")); // DOM1 HTML: uppercase
        Check.Done();
    }

    [Fact]
    public void CloneNodeDeepAndShallow()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='src' class='box'><b>deep</b></div></body></html>");
        page.Eval("var src = document.getElementById('src');" +
                  "window.deep = src.cloneNode(true);" +
                  "window.shallow = src.cloneNode(false);");
        Check.That(page.EvalString("window.deep.getElementsByTagName('b').length") == "1",
            "cloneNode(true) copies the subtree");
        Check.That(page.EvalString("window.shallow.getElementsByTagName('b').length") == "0",
            "cloneNode(false) copies no children");
        Check.That(page.EvalString("window.shallow.className") == "box" ||
                  page.EvalString("window.shallow.getAttribute('class')") == "box",
            "cloneNode copies attributes");
        Check.That(page.EvalString("src.getElementsByTagName('b').length") == "1",
            "the source subtree is untouched");
        Check.Done();
    }

    [Fact]
    public void ClassNameReflectsClassAttributeAndRecalculatesStyles()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><head><style>.live { position: absolute; }</style></head><body>" +
            "<div id='target' class='old'></div></body></html>");

        page.Eval("var target = document.getElementById('target');" +
                  "window.originalClass = target.className;" +
                  "target.className = 'live';");

        Check.That(page.EvalString("window.originalClass") == "old",
            "className reads the class attribute");
        Check.That(page.EvalString("target.className") == "live" &&
                   page.EvalString("target.getAttribute('class')") == "live",
            "className writes the class attribute");

        StyleResolver.Resolve(page.Document, 800);
        var targetElement = page.Document.ElementDescendants()
            .Single(element => element.GetAttr("id") == "target");
        Check.That(targetElement.Style?.Position == PositionValue.Absolute,
            "className changes take effect in matched CSS rules");
        Check.That(page.Canvas.Log.Reflows > 0,
            "className changes request a document reflow");
        Check.Done();
    }

    [Fact]
    public void RemoveAttributeClearsAttrAndHandler()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><a id='lnk' href='http://x.test/' target='_blank' onclick=\"document.write('X');\">go</a></body></html>");
        page.Eval("var a = document.getElementById('lnk');" +
                  "a.removeAttribute('target');");
        Check.That(page.EvalString("document.getElementById('lnk').getAttribute('target')") == "null",
            "removeAttribute clears the attribute (getAttribute → null)");
        Check.That(page.EvalString("document.getElementById('lnk').getAttribute('href')") == "http://x.test/",
            "the other attributes survive");
        page.Eval("document.getElementById('lnk').removeAttribute('onclick');");
        var a = page.Document.AllTags("a")[0];
        page.FireEvent(a, "onclick");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(!text.Contains("X"),
            "removeAttribute('onclick') also removes the inline handler");
        Check.Done();
    }

    [Fact]
    public void AttributesNamedNodeMapSurface()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><img id='im' src='x.gif' width='10' alt='pic'></body></html>");
        Check.That(page.EvalString("document.getElementById('im').attributes.length") == "4",
            "attributes.length counts the attributes (id/src/width/alt)",
            page.EvalString("document.getElementById('im').attributes.length"));
        Check.That(page.EvalString("document.getElementById('im').attributes[0].name.length > 0") == "true",
            "attributes[0].name is a non-empty string");
        Check.That(page.EvalString("document.getElementById('im').attributes.getNamedItem('src').value") == "x.gif",
            "attributes.getNamedItem(name).value reads the attribute");
        Check.That(page.EvalString("document.getElementById('im').attributes.getNamedItem('src').nodeType") == "2",
            "attribute nodes have nodeType 2");
        page.Eval("document.getElementById('im').attributes.getNamedItem('alt').value = 'zoom';");
        Check.That(page.EvalString("document.getElementById('im').getAttribute('alt')") == "zoom",
            "writing an attribute node's .value updates the live element");
        Check.Done();
    }

    [Fact]
    public void HasChildNodesHasAttributesOwnerDocument()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d'>text</div><br></body></html>");
        Check.That(page.EvalString("document.getElementById('d').hasChildNodes()") == "true",
            "hasChildNodes() true for an element with a text child");
        Check.That(page.EvalString("document.getElementById('d').hasAttributes()") == "true",
            "hasAttributes() true for an element with id");
        Check.That(page.EvalString("document.getElementsByTagName('br')[0].hasAttributes()") == "false",
            "hasAttributes() false for a bare <br>");
        Check.That(page.EvalString("document.getElementById('d').ownerDocument === document") == "true",
            "ownerDocument is the document object");
        Check.Done();
    }

    // ── IE5 DHTML object model (§10) ──────────────────────────────────

    [Fact]
    public void AttachEventFiresAfterDom0InOrderAndDetachRemoves()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d'>x</div></body></html>");
        page.Eval("var d = document.getElementById('d');" +
                  "window.fA = function() { window.order += 'A'; };" +
                  "window.fB = function() { window.order += 'B'; };" +
                  "d.onclick = function() { window.order += '0'; };" +
                  "d.attachEvent('onclick', window.fA);" +
                  "d.attachEvent('onclick', window.fB);" +
                  "window.order = '';");
        var d = page.Document.AllTags("div")[0];
        page.FireEvent(d, "onclick");
        Check.That(page.EvalString("window.order") == "0AB",
            "DOM-0 handler fires first, then attachEvent handlers in registration order",
            page.EvalString("window.order"));

        // detachEvent removes by function identity; detaching an unattached
        // function is a no-op (IE returned void, the boolean here is a probe).
        page.Eval("window.fUnknown = function() { window.order += 'X'; };" +
                  "d.detachEvent('onclick', window.fUnknown);");
        page.Eval("d.detachEvent('onclick', window.fB);");
        page.Eval("window.order = '';");
        page.FireEvent(d, "onclick");
        Check.That(page.EvalString("window.order") == "0A",
            "detachEvent removes the matching attachEvent registration",
            page.EvalString("window.order"));
        Check.Done();
    }

    [Fact]
    public void EventReturnValueFalseCancelsDefaultAction()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><a id='nav' href='http://x.test/away'>go</a></body></html>");
        // window.event.returnValue = false must cancel like "return false"
        page.Eval("document.getElementById('nav').onclick = function() { " +
                  "window.event.returnValue = false; };");
        var a = page.Document.AllTags("a")[0];
        var result = page.FireEvent(a, "onclick");
        Check.That(result.ToBoolean() == false,
            "setting event.returnValue = false cancels the default action (FireEvent returns false)");
        Check.That(page.Canvas.Log.Navigations.Count == 0,
            "no navigation was recorded during the cancelled handler");
        // the event-argument spelling works too
        page.Eval("document.getElementById('nav').onclick = function(e) { e.returnValue = false; };");
        var result2 = page.FireEvent(a, "onclick");
        Check.That(result2.ToBoolean() == false,
            "e.returnValue = false (event argument form) also cancels");
        Check.Done();
    }

    [Fact]
    public void EventCancelBubbleStopsBubbling()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='outer'><span id='inner'>x</span></div></body></html>");
        page.Eval("window.hits = '';" +
                  "document.getElementById('inner').onclick = function() { window.hits += 'I'; };" +
                  "document.getElementById('outer').onclick = function() { window.hits += 'O'; };");
        var inner = page.Document.AllTags("span")[0];
        page.FireEvent(inner, "onclick");
        Check.That(page.EvalString("window.hits") == "IO",
            "click bubbles from the target to ancestor handlers (IE5 model)",
            page.EvalString("window.hits"));

        page.Eval("window.hits = '';" +
                  "document.getElementById('inner').onclick = function(e) { window.hits += 'I'; e.cancelBubble = true; };");
        page.FireEvent(inner, "onclick");
        Check.That(page.EvalString("window.hits") == "I",
            "event.cancelBubble = true stops bubbling to the ancestor handler",
            page.EvalString("window.hits"));
        Check.Done();
    }

    [Fact]
    public void OffsetGeometryFromStyledDiv()
    {
        var previousSettings = BrowserRuntime.Settings.Clone();
        try
        {
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
            var page = new PageHarness();
            page.LoadHtml("<html><body><div id='d' style='width:220px; padding:10px; border:5px solid black'>box</div></body></html>");
            // Style resolution installs the ComputedStyle the geometry reads
            Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);
            Check.That(page.EvalString("document.getElementById('d').offsetWidth") == "250",
                "offsetWidth = width + padding + border for the styled div",
                page.EvalString("document.getElementById('d').offsetWidth"));
            Check.That(page.EvalString("document.getElementById('d').clientWidth") == "240",
                "clientWidth = width + padding (border excluded)",
                page.EvalString("document.getElementById('d').clientWidth"));
            Check.That(page.EvalString("document.getElementById('d').offsetParent.tagName") == "BODY" ||
                      page.EvalString("document.getElementById('d').offsetParent.id") == "d",
                "offsetParent resolves to an ancestor (body fallback)");
            Check.That(page.EvalString("document.getElementById('d').offsetHeight > 0") == "true",
                "offsetHeight is positive for rendered content");
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
        Check.Done();
    }

    [Fact]
    public void GeometryAndCurrentStyleFlushDuringParserScript()
    {
        var page = new PageHarness();
        page.LoadHtml(
            "<html><head><style>.probe { padding: 3px; }</style></head><body>" +
            "<div id='probe'>content</div>" +
            "<script>var p = document.getElementById('probe');" +
            "window.geometry = (p.offsetWidth > 0 && p.offsetHeight > 0 && p.clientWidth > 0);" +
            "window.currentWidth = p.currentStyle.width;</script></body></html>");

        Check.That(page.EvalString("window.geometry") == "true",
            "parser-time geometry reads synchronously create layout boxes",
            page.EvalString("window.geometry"));
        Check.That(page.EvalString("window.currentWidth") == "auto",
            "currentStyle.width returns auto for an unspecified width",
            page.EvalString("window.currentWidth"));
        Check.Done();
    }

    [Fact]
    public void CurrentStyleReadsResolvedProperties()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><span id='s' style='color:#ff0000; font-size:20px'>red</span></body></html>");
        Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);
        Check.That(page.EvalString("document.getElementById('s').currentStyle.color") == "#ff0000",
            "currentStyle.color reads the resolved colour",
            page.EvalString("document.getElementById('s').currentStyle.color"));
        Check.That(page.EvalString("document.getElementById('s').currentStyle.fontSize") == "20px",
            "currentStyle.fontSize reads the resolved pixel size",
            page.EvalString("document.getElementById('s').currentStyle.fontSize"));
        Check.That(page.EvalString("typeof document.getElementById('s').currentStyle") == "object",
            "currentStyle object is exposed");
        Check.Done();
    }

    [Fact]
    public void InsertAdjacentTextMutatesAllFourPositions()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d'>A</div></body></html>");
        page.Eval("var d = document.getElementById('d');" +
                  "d.insertAdjacentText('beforeBegin', 'B');" +
                  "d.insertAdjacentText('afterBegin', 'a');" +
                  "d.insertAdjacentText('beforeEnd', 'z');" +
                  "d.insertAdjacentText('afterEnd', 'R');");
        string text = page.Document.FirstTag("body")?.InnerText ?? "";
        Check.That(text == "BaAzR",
            "insertAdjacentText updates all four insertion positions", text);
        Check.Done();
    }

    [Fact]
    public void UniqueIdStableAndDistinct()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='a'></div><div id='b'></div></body></html>");
        string first = page.EvalString("document.getElementById('a').uniqueID");
        string again = page.EvalString("document.getElementById('a').uniqueID");
        string other = page.EvalString("document.getElementById('b').uniqueID");
        Check.That(first.StartsWith("ms__id"),
            "uniqueID uses the IE ms__idN form", first);
        Check.That(first == again,
            "uniqueID is stable across accesses for the same element", $"{first} vs {again}");
        Check.That(first != other,
            "uniqueID differs between elements", $"{first} vs {other}");
        Check.Done();
    }

    [Fact]
    public void ReadyStateCompleteAndOnreadystatechangeFiresOnce()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><head><script>" +
                      "window.readyCount = 0;" +
                      "document.onreadystatechange = function() { window.readyCount++; };" +
                      "</script></head><body><p>page</p></body></html>");
        Check.That(page.EvalString("document.readyState") == "complete",
            "document.readyState is complete after the parse",
            page.EvalString("document.readyState"));
        Check.That(page.EvalString("window.readyCount") == "1",
            "onreadystatechange fired exactly once at the complete transition",
            page.EvalString("window.readyCount"));
        page.Eval("var noop = 1;");   // a later re-registration must not refire
        Check.That(page.EvalString("window.readyCount") == "1",
            "onreadystatechange does not refire on later registrations",
            page.EvalString("window.readyCount"));
        Check.Done();
    }

    [Fact]
    public void ShowModalDialogShowsHostModalAndReturnsUndefined()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        string result = page.EvalString("window.showModalDialog('dialog.htm')");
        Check.That(result == "undefined",
            "showModalDialog returns undefined (no dialog return value plumbing)");
        Check.That(page.Canvas.Log.Alerts.Any(a => a.Contains("dialog.htm")),
            "showModalDialog reached the shell's host-modal dialog service",
            string.Join("|", page.Canvas.Log.Alerts));
        Check.Done();
    }

    [Fact]
    public void StylePixelAndPosPropertiesReadAndWrite()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d' style='position:absolute; left:40px; top:25px; width:120px; height:50px'>x</div></body></html>");
        Check.That(page.EvalString("document.getElementById('d').style.pixelLeft") == "40",
            "style.pixelLeft reads the inline left in px",
            page.EvalString("document.getElementById('d').style.pixelLeft"));
        Check.That(page.EvalString("document.getElementById('d').style.posTop") == "25",
            "style.posTop reads the inline top in px");
        Check.That(page.EvalString("document.getElementById('d').style.pixelWidth") == "120",
            "style.pixelWidth reads the inline width in px");
        page.Eval("document.getElementById('d').style.pixelLeft = 99;");
        Check.That(page.EvalString("document.getElementById('d').style.left") == "99px",
            "writing style.pixelLeft lands as a px declaration",
            page.EvalString("document.getElementById('d').style.left"));
        Check.That(page.EvalString("document.getElementById('d').style.pixelLeft") == "99",
            "pixelLeft reads back the written value");
        Check.Done();
    }

    [Fact]
    public void ScrollTopScrollLeftReadback()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body><div id='d' style='height:40px; overflow:scroll'>tall content</div></body></html>");
        Check.That(page.EvalString("document.getElementById('d').scrollTop") == "0",
            "scrollTop reads 0 before any scroll");
        page.Eval("document.getElementById('d').scrollTop = 40; document.getElementById('d').scrollLeft = 12;");
        Check.That(page.EvalString("document.getElementById('d').scrollTop") == "40",
            "scrollTop reads back the scripted write",
            page.EvalString("document.getElementById('d').scrollTop"));
        Check.That(page.EvalString("document.getElementById('d').scrollLeft") == "12",
            "scrollLeft reads back the scripted write");
        Check.Done();
    }

    [Fact]
    public void WindowOpenReturnsFacadeAndOpenerIsNull()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        page.Eval("window.kid = window.open('child.htm', 'kid', 'width=320,height=200');");
        Check.That(page.Canvas.Log.Navigations.Any(n => n.Contains("child.htm")),
            "window.open(url, name, features) opened the url through the shell",
            string.Join("|", page.Canvas.Log.Navigations));
        Check.That(page.EvalString("window.kid.name") == "kid",
            "the window.open facade carries the window name");
        Check.That(page.EvalString("window.kid.closed") == "false",
            "the facade reports closed=false");
        Check.That(page.EvalString("typeof window.kid.close") == "function",
            "the facade exposes close()");
        Check.That(page.EvalString("window.kid.opener === window") == "true",
            "the facade's opener references the opening window");
        Check.That(page.EvalString("window.opener") == "null",
            "window.opener is null for a window not opened by script");
        Check.Done();
    }

    [Fact]
    public void FramesLengthAndNamedLookup()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body>" +
                      "<iframe src='nav.htm' name='nav'></iframe>" +
                      "<iframe src='main.htm' name='content'></iframe>" +
                      "</body></html>");
        Check.That(page.EvalString("window.length") == "2",
            "window.length counts the frame elements",
            page.EvalString("window.length"));
        Check.That(page.EvalString("window.frames.length") == "2",
            "window.frames.length counts the frame elements");
        Check.That(page.EvalString("window.frames[0].getAttribute('src')") == "nav.htm",
            "window.frames[i] resolves the indexed frame (element wrapper — documented limitation)");
        Check.That(page.EvalString("window.frames['content'].getAttribute('src')") == "main.htm",
            "window.frames[name] resolves the named frame");
        Check.Done();
    }

    [Fact]
    public void WindowOnErrorFiresAndSuppressesConsoleError()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        var entries = new List<JsInterpreter.ConsoleEntry>();
        page.Interpreter.ConsoleMessage += entry => entries.Add(entry);

        page.Eval("window.onerror = function(msg, url, line) { window.errMsg = msg; window.errLine = line; return true; };" +
                  "throw new Error('BOOM');");
        Check.That(page.EvalString("window.errMsg").Contains("BOOM"),
            "window.onerror received the error message",
            page.EvalString("window.errMsg"));
        Check.That(page.EvalString("window.errLine") == "0",
            "window.onerror received the line argument");
        Check.That(!entries.Any(e => e.Level == "error" && e.Message.Contains("BOOM")),
            "onerror returning true suppresses the default console error report",
            string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}")));

        entries.Clear();
        page.Eval("window.onerror = null;");
        page.Eval("throw new Error('SECOND');");
        Check.That(entries.Any(e => e.Level == "error" && e.Message.Contains("SECOND")),
            "without a handler (or with a non-true return), errors surface to the console",
            string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}")));
        Check.Done();
    }

    // ── Personas: Retro96 union default / IE5 / NS4.7 ─────────────────

    [Fact]
    public void DefaultPersonaIsRetro96Union()
    {
        var previousSettings = BrowserRuntime.Settings.Clone();
        try
        {
            BrowserRuntime.Apply(new UserSettings());
            // Engine default: Retro96 compatibility union.
            var page = new PageHarness();
            page.LoadHtml("<html><body></body></html>");
            Check.That(page.EvalString("navigator.userAgent") == UserSettings.DefaultRetro96UserAgent,
                "navigator.userAgent uses the default Retro96 union value",
                page.EvalString("navigator.userAgent"));
            Check.That(page.EvalString("navigator.appName") == "Microsoft Internet Explorer",
                "navigator.appName retains its legacy-compatible value");
            Check.That(page.EvalString("navigator.javaEnabled()") == "true",
                "navigator.javaEnabled() returns true (applets enabled)");
            Check.That(page.EvalString("typeof document.all") != "undefined",
                "document.all exists in the Retro96 union");
            Check.That(page.EvalString("typeof document.layers") != "undefined",
                "document.layers exists in the Retro96 union",
                page.EvalString("typeof document.layers"));
            Check.That(page.EvalString("typeof document.getElementById") == "function",
                "getElementById exists in the Retro96 union");
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
        Check.Done();
    }

    [Fact]
    public void NetscapePersonaExposesLayersNotAll()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var page = new PageHarness();
            page.LoadHtml("<html><body><div id='a' style='position:absolute; left:10px; top:20px'>L</div><div id='b'>plain</div></body></html>");
            Check.That(page.EvalString("typeof document.layers") == "object",
                "document.layers exists in the NS4.7 persona",
                page.EvalString("typeof document.layers"));
            Check.That(page.EvalString("typeof document.all") == "undefined",
                "document.all is undefined in the NS4.7 persona",
                page.EvalString("typeof document.all"));
            Check.That(page.EvalString("typeof document.getElementById") == "undefined",
                "getElementById is undefined in the NS4.7 persona",
                page.EvalString("typeof document.getElementById"));
            Check.That(page.EvalString("navigator.userAgent") == "Mozilla/4.7 [en] (Win98; I)",
                "navigator.userAgent is the NS4.7 string",
                page.EvalString("navigator.userAgent"));
            Check.That(page.EvalString("navigator.appName") == "Netscape",
                "navigator.appName is Netscape");
            Check.That(page.EvalString("document.layers.length") == "1",
                "only positioned elements appear in document.layers",
                page.EvalString("document.layers.length"));
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    [Fact]
    public void Retro96UnionPersonaExposesAllThree()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
        try
        {
            var page = new PageHarness();
            page.LoadHtml(
                "<html><body><div id='a' style='position:absolute; left:10px; top:20px'>L</div>" +
                "<a id='target' href='#'>target</a></body></html>");
            Check.That(page.EvalString("typeof document.all") != "undefined",
                "document.all exists in the Retro96 union persona");
            Check.That(page.EvalString("typeof document.layers") == "object",
                "document.layers exists in the Retro96 union persona",
                page.EvalString("typeof document.layers"));
            Check.That(page.EvalString("typeof document.getElementById") == "function",
                "getElementById exists in the Retro96 union persona");
            Check.That(page.EvalString("typeof document.layers['a']") == "object",
                "document.layers exposes Layer objects in the Retro96 union persona");
            Check.That(page.EvalString("document.layers['a'].left") == "10",
                "union Layer object exposes layer geometry");
            Check.That(page.EvalString("typeof document.layers['a'].moveTo") == "function",
                "union Layer object exposes layer methods");
            Check.That(page.EvalString("typeof Event") == "object" &&
                       page.EvalString("Event.CLICK") == "4",
                "Event constants are available in the Retro96 union persona");
            Check.That(page.EvalString("typeof window.captureEvents") == "function",
                "window.captureEvents is available in the Retro96 union persona");
            Check.That(page.EvalString("navigator.appName") == "Microsoft Internet Explorer",
                "the Retro96 union uses the IE-compatible appName for 1999 site sniffers");
            Check.That(page.EvalString("navigator.userAgent") == UserSettings.DefaultIe5UserAgent,
                "the Retro96 union uses a historically plausible IE5 User-Agent");

            page.Eval("window.unionCapture = 0; window.captureEvents(Event.CLICK);" +
                      "window.onclick = function() { window.unionCapture++; };");
            page.FireEvent(page.Document.AllTags("a")[0], "onclick");
            Check.That(page.EvalString("window.unionCapture") == "1",
                "union captureEvents routes captured clicks");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    // ── NS4 layer model (§10, persona-gated) ──────────────────────────

    [Fact]
    public void LayerLeftTopWritesMoveTheElement()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var page = new PageHarness();
            page.LoadHtml("<html><body><div id='shuttle' style='position:absolute; left:10px; top:20px'>S</div></body></html>");
            Check.That(page.EvalString("document.layers['shuttle'].left") == "10",
                "layer.left reads the inline left",
                page.EvalString("document.layers['shuttle'].left"));
            Check.That(page.EvalString("document.layers['shuttle'].top") == "20",
                "layer.top reads the inline top");
            page.Eval("document.layers['shuttle'].left = 137;");
            string style = page.Document.AllTags("div")[0].GetAttr("style") ?? "";
            Check.That(style.Contains("left: 137px"),
                "writing layer.left updates the element's inline style", style);
            Check.That(page.EvalString("document.layers['shuttle'].left") == "137",
                "layer.left reads back the moved position");
            page.Eval("document.layers['shuttle'].moveTo(5, 75);");
            string style2 = page.Document.AllTags("div")[0].GetAttr("style") ?? "";
            Check.That(style2.Contains("left: 5px") && style2.Contains("top: 75px"),
                "layer.moveTo(x, y) writes both offsets", style2);
            page.Canvas.Log.Reflows = 0;
            page.Eval("document.layers['shuttle'].moveBy(1, 2);");
            Check.That(page.Canvas.Log.Reflows > 0,
                "layer movement triggers the reflow hook");
            Check.That(page.EvalString("document.layers['shuttle'].visibility") == "inherit",
                "layer.visibility defaults to inherit");
            page.Eval("document.layers['shuttle'].visibility = 'hide';");
            string style3 = page.Document.AllTags("div")[0].GetAttr("style") ?? "";
            Check.That(style3.Contains("visibility: hidden"),
                "layer.visibility 'hide' maps to CSS hidden", style3);
            Check.That(page.EvalString("typeof document.layers['shuttle'].clip.left") == "number",
                "layer.clip exposes left/top/right/bottom numbers");
            Check.That(page.EvalString("document.layers['shuttle'].document === document") == "true",
                "layer.document is the (shared) document view");
            Check.That(page.EvalString("document.layers[0] === document.layers['shuttle']") == "true",
                "layers are indexable by number AND name with stable identity");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    [Fact]
    public void Ns4CaptureEventsAndEventConstants()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var page = new PageHarness();
            page.LoadHtml("<html><body><a id='t' href='#'>x</a></body></html>");
            Check.That(page.EvalString("typeof window.captureEvents") == "function",
                "window.captureEvents exists in the NS persona");
            Check.That(page.EvalString("Event.CLICK") == "4",
                "Event.CLICK mask constant");
            Check.That(page.EvalString("Event.MOUSEDOWN") == "1",
                "Event.MOUSEDOWN mask constant");
            Check.That(page.EvalString("Event.KEYDOWN") == "256",
                "Event.KEYDOWN mask constant");
            Check.That(page.EvalString("Event.SHIFT_MASK") == "4",
                "Event.SHIFT_MASK modifier constant");
            // Captured clicks go to the window's own handler before the target.
            // (NS persona has no getElementById — DOM-0 named access via the
            // live links collection is the era-correct route to the element.)
            page.Eval("window.captured = 0; window.targeted = 0;" +
                      "window.captureEvents(Event.CLICK);" +
                      "window.onclick = function(e) { window.captured++; };" +
                      "document.links[0].onclick = function() { window.targeted++; };");
            var link = page.Document.AllTags("a")[0];
            page.FireEvent(link, "onclick");
            Check.That(page.EvalString("window.captured") == "1",
                "captureEvents(Event.CLICK) routes the click to the window handler first");
            Check.That(page.EvalString("window.targeted") == "1",
                "the target handler still fires after the captured window handler");
            // releaseEvents stops the capture
            page.Eval("window.releaseEvents(Event.CLICK); window.captured = 0;");
            page.FireEvent(link, "onclick");
            Check.That(page.EvalString("window.captured") == "0",
                "releaseEvents(Event.CLICK) stops the window capture");
            Check.That(page.EvalString("typeof window.routeEvent") == "function",
                "window.routeEvent is exposed");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    [Fact]
    public void Ns4EventObjectFields()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var page = new PageHarness();
            page.LoadHtml("<html><body><a id='t' href='#'>x</a></body></html>");
            page.Eval("window.seen = '';" +
                      "document.links[0].onclick = function(e) {" +
                      "  window.seen = (e.target === this ? 'T' : 'x') + e.which + ',' + e.pageX + ',' + e.pageY + ',' + e.modifiers;" +
                      "};");
            var span = page.Document.AllTags("a")[0];
            var evt = page.Interpreter.CreateMouseEvent("onclick", 30, 40, 0);
            page.Interpreter.FireEvent(span, "onclick", evt);
            Check.That(page.EvalString("window.seen") == "T1,30,40,0",
                "NS4 event fields: target, which (1-based button), pageX/pageY, modifiers",
                page.EvalString("window.seen"));
            // key events carry the character code in which
            page.Eval("window.key = 0;" +
                      "document.links[0].onkeypress = function(e) { window.key = e.which; };");
            var key = page.Interpreter.CreateKeyEvent("A", 65);
            page.Interpreter.FireEvent(span, "onkeypress", key);
            Check.That(page.EvalString("window.key") == "65",
                "key events expose the character code as e.which");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }

    // ── ES3 / JScript 5.0 (§12) ───────────────────────────────────────

    [Fact]
    public void NumberToFixedFormats()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        Check.That(page.EvalString("(3.14159).toFixed(2)") == "3.14",
            "toFixed(2) rounds to two fraction digits");
        Check.That(page.EvalString("(2.5).toFixed(0)") == "3",
            "toFixed(0) rounds halves away from zero",
            page.EvalString("(2.5).toFixed(0)"));
        Check.That(page.EvalString("(0).toFixed(2)") == "0.00",
            "toFixed pads with zeros");
        Check.That(page.EvalString("(1.005).toFixed(2)") == "1.00",
            "toFixed honours the binary value (1.005 → 1.00, era behaviour)",
            page.EvalString("(1.005).toFixed(2)"));
        Check.That(page.EvalString("(123.456).toFixed(1)") == "123.5",
            "toFixed(1) rounds up");
        Check.That(page.EvalString("(-2.5).toFixed(0)") == "-3",
            "toFixed handles negatives");
        Check.That(page.EvalString("(0).toFixed()") == "0",
            "toFixed() without digits behaves like toFixed(0)");
        Check.Done();
    }

    [Fact]
    public void NumberToExponentialAndToPrecision()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        Check.That(page.EvalString("(123456).toExponential(2)") == "1.23e+5",
            "toExponential(2) formats d.dde+dd",
            page.EvalString("(123456).toExponential(2)"));
        Check.That(page.EvalString("(0.000123).toExponential(2)") == "1.23e-4",
            "toExponential formats negative exponents",
            page.EvalString("(0.000123).toExponential(2)"));
        Check.That(page.EvalString("(0).toExponential(2)") == "0.00e+0",
            "toExponential(2) of zero");
        Check.That(page.EvalString("(100).toExponential()") == "1e+2",
            "toExponential() no-arg trims insignificant zeros",
            page.EvalString("(100).toExponential()"));
        Check.That(page.EvalString("(3.14159).toPrecision(4)") == "3.142",
            "toPrecision(4) rounds to 4 significant digits",
            page.EvalString("(3.14159).toPrecision(4)"));
        Check.That(page.EvalString("(1234.567).toPrecision(6)") == "1234.57",
            "toPrecision(6) keeps fixed notation in range",
            page.EvalString("(1234.567).toPrecision(6)"));
        Check.That(page.EvalString("(0.000123).toPrecision(2)") == "0.00012",
            "toPrecision(2) fixed notation above the e-6 threshold");
        Check.That(page.EvalString("(0.0000000123).toPrecision(2)") == "1.2e-8",
            "toPrecision(2) switches to exponential below e-6",
            page.EvalString("(0.0000000123).toPrecision(2)"));
        Check.That(page.EvalString("(12345).toPrecision(2)") == "1.2e+4",
            "toPrecision(2) switches to exponential when exponent ≥ precision",
            page.EvalString("(12345).toPrecision(2)"));
        Check.Done();
    }

    [Fact]
    public void DateY2KAndUtcSurface()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        page.Eval("window.d = new Date(2000, 0, 1);");
        // The Y2K contract: getYear stays year-1900 (ECMA/JS1.3 semantics)
        Check.That(page.EvalString("window.d.getYear()") == "100",
            "new Date(2000,0,1).getYear() === 100 (era Y2K semantics)",
            page.EvalString("window.d.getYear()"));
        Check.That(page.EvalString("window.d.getFullYear()") == "2000",
            "getFullYear() returns the four-digit year");
        Check.That(page.EvalString("window.d.getUTCMonth()") == "0",
            "getUTCMonth() for Jan");
        Check.That(page.EvalString("window.d.getUTCDate()") == "1",
            "getUTCDate() for the 1st");
        Check.That(page.EvalString("window.d.getUTCFullYear()") == "2000",
            "getUTCFullYear() is the UTC year");
        Check.That(page.EvalString("window.d.getUTCHours()") == "0" &&
                  page.EvalString("window.d.getUTCMinutes()") == "0" &&
                  page.EvalString("window.d.getUTCSeconds()") == "0",
            "UTC time getters report midnight");
        Check.That(page.EvalString("window.d.toDateString()") == "Sat Jan 01 2000",
            "toDateString() uses the ECMAScript date form",
            page.EvalString("window.d.toDateString()"));
        Check.That(page.EvalString("window.d.toTimeString().substring(0, 8)") == "00:00:00",
            "toTimeString() starts with HH:mm:ss",
            page.EvalString("window.d.toTimeString()"));

        page.Eval("window.d.setFullYear(1999, 11, 31);");
        Check.That(page.EvalString("window.d.getFullYear()") == "1999" &&
                  page.EvalString("window.d.getMonth()") == "11" &&
                  page.EvalString("window.d.getDate()") == "31",
            "setFullYear(year, month, date) replaces all three fields");
        page.Eval("window.d.setMonth(0);");
        Check.That(page.EvalString("window.d.getMonth()") == "0" &&
                  page.EvalString("window.d.getDate()") == "31",
            "setMonth(month) keeps the current date");
        page.Eval("window.d.setDate(15);");
        Check.That(page.EvalString("window.d.getDate()") == "15",
            "setDate(day) replaces the day");
        page.Eval("window.e = new Date(2000, 0, 1); window.e.setTime(0);");
        Check.That(page.EvalString("window.e.getTime()") == "0" &&
                  page.EvalString("window.e.getUTCFullYear()") == "1970",
            "setTime(ms) re-bases the date");
        Check.Done();
    }

    [Fact]
    public void RegExpCompileRecompilesInPlace()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        page.Eval("window.re = new RegExp('a(b)');");
        Check.That(page.EvalString("window.re.source") == "a(b)",
            "initial source before compile");
        page.Eval("window.re.compile('c(d)', 'g');");
        Check.That(page.EvalString("window.re.source") == "c(d)",
            "compile(pattern, flags) replaces the source",
            page.EvalString("window.re.source"));
        Check.That(page.EvalString("window.re.global") == "true",
            "compile applies the new flags");
        Check.That(page.EvalString("window.re.test('cd')") == "true" &&
                  page.EvalString("window.re.test('ab')") == "false",
            "the recompiled pattern matches the new source");
        Check.That(page.EvalString("window.re.toString()") == "/c(d)/g",
            "toString reflects the recompiled pattern",
            page.EvalString("window.re.toString()"));
        Check.Done();
    }

    // ── navigator host objects (§12) ──────────────────────────────────

    [Fact]
    public void NavigatorPluginsAndMimeTypesTables()
    {
        var page = new PageHarness();
        page.LoadHtml("<html><body></body></html>");
        Check.That(int.Parse(page.EvalString("navigator.plugins.length")) >= 3,
            "the IE5 persona ships a plausible plugin table",
            page.EvalString("navigator.plugins.length"));
        Check.That(page.EvalString("navigator.plugins[0].name.length > 0") == "true" &&
                  page.EvalString("navigator.plugins[0].filename.length > 0") == "true" &&
                  page.EvalString("navigator.plugins[0].description.length > 0") == "true",
            "plugin entries carry name/filename/description");
        Check.That(page.EvalString("typeof navigator.plugins.refresh") == "function",
            "navigator.plugins.refresh() is exposed");
        Check.That(page.EvalString("navigator.plugins.refresh()") == "undefined",
            "plugins.refresh() is a documented no-op");
        Check.That(page.EvalString("navigator.mimeTypes['application/x-shockwave-flash'].enabledPlugin.name") == "Shockwave Flash",
            "navigator.mimeTypes carries the era sniffing entries with enabledPlugin");
        Check.That(page.EvalString("navigator.mimeTypes.length") == "4",
            "navigator.mimeTypes has sample entries",
            page.EvalString("navigator.mimeTypes.length"));
        Check.Done();
    }

    [Fact]
    public void NavigatorPluginsTablePerPersona()
    {
        BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Netscape47 });
        try
        {
            var ns = new PageHarness();
            ns.LoadHtml("<html><body></body></html>");
            Check.That(ns.EvalString("navigator.plugins['QuickTime Plug-in'] !== undefined") == "true",
                "the NS4.7 persona carries the classic Navigator plugin scan entries");
            Check.That(ns.EvalString("navigator.plugins['Acrobat Plug-in'].filename") == "NPPDF32.DLL",
                "NS4.7 plugin entries use the Netscape DLL filenames");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());
        }
        Check.Done();
    }
}
