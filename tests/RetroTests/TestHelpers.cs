// Shared xUnit infrastructure for the RetroTests suite.
// The check-collector pattern keeps the console-era granularity (one named
// check = one bug-report line) while mapping onto xUnit facts: each fact
// collects ALL of its failed checks and throws once at Done().
using System.Text;
using Retro96;
using Retro96.Engine;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Network;
using Xunit.Sdk;

// Deterministic, sequentially executed tests (shared engine singletons
// like the write buffer / scope make parallel facts unsafe).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RetroTests;

public static class Check
{
    [ThreadStatic] private static List<string>? _failures;

    public static void That(bool condition, string name, string detail = "")
    {
        if (!condition)
        {
            _failures ??= new List<string>();
            _failures.Add(detail.Length > 0 ? $"{name} — {detail}" : name);
        }
    }

    /// <summary>Call at the end of every [Fact]. Throws once, listing every failed check.</summary>
    public static void Done()
    {
        var failures = _failures;
        _failures = null;
        if (failures is { Count: > 0 })
            throw new XunitException("FAILED CHECKS:\n  " + string.Join("\n  ", failures));
    }
}

public static class TestPaths
{
    /// <summary>Locates retro96/testdata robustly from the test bin directory.</summary>
    public static readonly string Testdata =
        FindDirectory(AppContext.BaseDirectory, "testdata")
        ?? throw new InvalidOperationException("testdata directory not found above " + AppContext.BaseDirectory);

    private static string? FindDirectory(string start, string name)
    {
        var dir = new DirectoryInfo(start);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// The Form1.RunInlineScript flow, verbatim shape — full DOM-0 bindings
/// registered before each inline script, writes spliced by the parser.
/// Ported from JsPageTests so JS contract tests share one rig.
/// </summary>
public sealed class PageHarness
{
    public JsScope Scope = new();
    public JsInterpreter Interpreter = null!;
    public DocumentBindingsState State = new();
    public BrowserCanvas Canvas = new();
    public DomDocument Document = null!;
    public List<string> ScriptErrors = new();

    public DomDocument LoadHtml(string html, string url = "file:///C:/web/page.htm",
                                int executionLimitMs = 5000)
    {
        var parsed = ParsedUrl.Parse(url);
        JsRuntime.PopulateGlobalScope(Scope);

        Interpreter = new JsInterpreter(Scope, null, _ => { }, _ => { },
            timeLimitMs: executionLimitMs);
        Interpreter.RegisterRuntimeBuiltins();
        State = new DocumentBindingsState { Interpreter = Interpreter, Canvas = Canvas };
        Interpreter.ElementWrapperHook = e => DomBindings.WrapElement(e, State);

        Document = HtmlParser.Parse(html, parsed, new CookieStore(),
            (doc, src, _) => RunInlineScript(doc, src));

        // The shell re-registers the DOM bindings after the parse completes
        // (pages with NO <script> must still see document/window objects).
        // Mirrored here so event handlers fired after load behave exactly
        // like they do in the browser.
        State.Document = Document;
        DomBindings.RegisterAll(Scope, Document, new NavigationHistory(), Canvas, State);
        Interpreter.RegisterRuntimeBuiltins();
        return Document;
    }

    public DomDocument LoadFile(string path)
    {
        string html = File.ReadAllText(path);
        return LoadHtml(html, "file://" + Path.GetFullPath(path));
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

    public string EvalString(string code) => Eval(code).ToJsString();

    /// <summary>Fires an element event handler the way the shell does.</summary>
    public JsValue FireEvent(DomElement element, string eventName) =>
        Interpreter.FireEvent(element, eventName);
}

public static class DomExt
{
    public static DomElement? FirstTag(this DomDocument doc, string tag) =>
        doc.ElementDescendants().FirstOrDefault(e => e.TagName == tag);

    public static List<DomElement> AllTags(this DomDocument doc, string tag) =>
        doc.ElementDescendants().Where(e => e.TagName == tag).ToList();
}
