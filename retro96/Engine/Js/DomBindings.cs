using System;
using System.Collections.Generic;
using System.Linq;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Network;

namespace Retro96.Engine.Js;

/// <summary>
/// Per-page scripting state shared between the bindings, the HTML parser's
/// inline-script executor, and the browser shell.
/// </summary>
public sealed class DocumentBindingsState
{
    /// <summary>document.write output collected during the current script.</summary>
    public readonly System.Text.StringBuilder WriteBuffer = new();

    /// <summary>
    /// How many WriteBuffer characters have already been applied to the
    /// live document by a post-parse write.  The buffer itself is NEVER
    /// truncated on apply — test rigs inspect the full write history, and
    /// only the yet-unapplied tail is parsed into the document.
    /// </summary>
    public int WriteBufferAppliedTo;

    public JsInterpreter? Interpreter;

    /// <summary>Browser shell hooks — set by BrowserCanvas.</summary>
    public BrowserCanvas? Canvas;

    public DomDocument? Document;

    /// <summary>The window object for THIS page.  RegisterAll runs before
    /// every inline script; the window must be created ONCE and reused —
    /// implicit globals (zz = 5), window.onload handlers and window.status
    /// set by earlier scripts live on it, and a fresh window each
    /// registration silently discarded them between scripts.</summary>
    public JsObject? WindowObject;

    public string LastModified = "";
    public string Referrer = "";

    /// <summary>Element wrapper cache — one JsObject identity per element so
    /// property writes (rollover swaps) survive across script accesses.</summary>
    public readonly Dictionary<DomElement, JsObject> ElementWrappers = new();
}

/// <summary>
/// DOM Level 0 bindings: window, navigator, document (with live
/// forms/images/links/anchors collections and a REAL document.write that
/// feeds the parser's stream), history, location, form/element wrappers,
/// and the Image() constructor behind every 1996 rollover.
///
/// Global-scope wiring: in real JS the global scope and the window object
/// are the SAME thing.  The root JsScope gets the window as its
/// GlobalFallback, so bare alert()/confirm()/status reads resolve through
/// it and implicit global writes (status = "…") land on it.
/// </summary>
public static class DomBindings
{
    // ─────────────────────────────────────────────────────────────────────
    // Registration
    // ─────────────────────────────────────────────────────────────────────

    public static void RegisterAll(JsScope globalScope, DomDocument document,
                                   NavigationHistory history, BrowserCanvas canvas,
                                   DocumentBindingsState state)
    {
        state.Document = document;
        state.Canvas = canvas;

        RegisterWindow(globalScope, document, history, canvas, state);
        RegisterNavigator(globalScope);
        RegisterScreen(globalScope, canvas);
        RegisterLocation(globalScope, document, canvas);   // before document (document.location)
        RegisterDocument(globalScope, document, canvas, state);
        RegisterHistory(globalScope, history);
        RegisterImageConstructor(globalScope, canvas, state);

        var windowObj = globalScope.Get("window").GetObjectOrFunction();
        windowObj.Set("document", globalScope.Get("document"));
        windowObj.Set("location", globalScope.Get("location"));
        windowObj.Set("history", globalScope.Get("history"));
        windowObj.Set("navigator", globalScope.Get("navigator"));
        windowObj.Set("screen", globalScope.Has("screen")
            ? globalScope.Get("screen") : JsValue.Undefined);

        // DOM-0 named access on the WINDOW (the IE-ism era rollover scripts
        // used: window.<imgName>.src = "over.gif").  NN-style scripts read
        // document.<name>; seeding both costs nothing and the bare global
        // falls back to the window object.
        foreach (var form in document.Forms)
        {
            string? nm = form.GetAttr("name");
            if (!string.IsNullOrEmpty(nm) && !windowObj.HasOwn(nm))
                windowObj.Set(nm, JsValue.FromObject(WrapElement(form, state)));
        }
        foreach (var img in document.Images)
        {
            string? nm = img.GetAttr("name");
            if (!string.IsNullOrEmpty(nm) && !windowObj.HasOwn(nm))
                windowObj.Set(nm, JsValue.FromObject(WrapElement(img, state)));
        }

        // The global scope falls back to the window object — bare
        // alert()/confirm()/status/scrollTo resolve through it.  Without
        // this, every window function that was not ALSO scope.Define'd was
        // unreachable as a bare identifier: alert("hi") threw
        // "'alert' is not a function" on every page that used one.
        globalScope.GlobalFallback = windowObj;
    }

    /// <summary>
    /// Registers a bare window (dialogs, scrolling, status) and wires the
    /// global fallback — for the PARSE phase, before the full DOM exists,
    /// so inline scripts that call alert() while the page streams in work.
    /// Optional: Form1 may call this in PrepareScripting right after
    /// JsRuntime.PopulateGlobalScope; RegisterAll later replaces everything
    /// with the complete bindings.
    /// </summary>
    public static void RegisterEarlyGlobals(JsScope globalScope, BrowserCanvas canvas)
    {
        RegisterWindow(globalScope, null, null, canvas, null);
        if (globalScope.Get("window").GetObjectOrFunction() is { } w)
            globalScope.GlobalFallback = w;
    }

    /// <summary>
    /// Shell hook for the certificate-error page's "Accept Risk and
    /// Continue" button.  The page's onsubmit calls
    /// window.acceptCertRisk() after a confirm() — the function never
    /// existed, so the button showed the dialog and then silently did
    /// nothing.  Form1 registers this right after the early globals when a
    /// certificate failure is pending; the action re-navigates to the
    /// original URL with the exception recorded.
    /// </summary>
    public static void RegisterCertRiskHook(JsScope globalScope, Action accept)
    {
        var fn = JsValue.FromFunction(new JsFunction(
            (self, args) => { accept(); return JsValue.Undefined; },
            globalScope, "acceptCertRisk"));

        globalScope.Define("acceptCertRisk", fn);
        if (globalScope.Get("window").GetObjectOrFunction() is { } w)
            w.Set("acceptCertRisk", fn);
    }

    private static JsValue Fn(JsScope scope, string name,
                              Func<JsValue, JsValue[], JsValue> impl) =>
        JsValue.FromFunction(new JsFunction(impl, scope, name));

    // ─────────────────────────────────────────────────────────────────────
    // window
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterWindow(JsScope scope, DomDocument? doc,
                                       NavigationHistory? history, BrowserCanvas canvas,
                                       DocumentBindingsState? state)
    {
        // ONE window object per page, REUSED across every registration:
        // RegisterAll runs before each inline script, and a fresh window
        // each time silently discarded every implicit global (sw = 5 in
        // script 1 read back undefined in script 2 — the Bravenet counter
        // URL came out "sw=undefined"), every window.onload handler and
        // every window.status write.  The scope slot (set by the parse-phase
        // early globals) and the state field agree within a page; the scope
        // is re-created per navigation, so no cross-page leakage.
        var w = state?.WindowObject;
        if (w == null && scope.Get("window") is { Type: JsType.Object } existing)
            w = existing.GetObject();
        if (w == null)
        {
            w = new JsObject();
            if (state != null) state.WindowObject = w;
        }

        w.Set("alert", Fn(scope, "alert", (self, args) =>
        {
            string msg = args.Length > 0 ? args[0].ToJsString() : "";
            canvas.ShowAlert(msg);
            return JsValue.Undefined;
        }));

        w.Set("confirm", Fn(scope, "confirm", (self, args) =>
        {
            string msg = args.Length > 0 ? args[0].ToJsString() : "";
            return JsValue.From(canvas.ShowConfirm(msg));
        }));

        w.Set("prompt", Fn(scope, "prompt", (self, args) =>
        {
            string msg = args.Length > 0 ? args[0].ToJsString() : "Enter value:";
            string def = args.Length > 1 ? args[1].ToJsString() : "";
            string? input = canvas.ShowPrompt(msg, def);
            return input == null ? JsValue.Null : JsValue.From(input);
        }));

        w.Set("open", Fn(scope, "open", (self, args) =>
        {
            string url = args.Length > 0 ? args[0].ToJsString() : "";
            // Feature strings ("width=400,height=300") were cosmetic
            // differences between shells — accepted and ignored here.
            if (!string.IsNullOrEmpty(url))
                canvas.OpenNewWindow(url);
            return JsValue.Undefined;
        }));

        w.Set("close", Fn(scope, "close", (self, args) =>
        {
            canvas.CloseHostWindow();
            return JsValue.Undefined;
        }));

        w.Set("scrollTo", Fn(scope, "scrollTo", (self, args) =>
        {
            if (args.Length >= 2)
                canvas.ScrollTo((int)args[0].ToNumber(), (int)args[1].ToNumber());
            return JsValue.Undefined;
        }));

        w.Set("scroll", w.Get("scrollTo"));

        w.Set("scrollBy", Fn(scope, "scrollBy", (self, args) =>
        {
            if (args.Length >= 2)
                canvas.ScrollBy((int)args[0].ToNumber(), (int)args[1].ToNumber());
            return JsValue.Undefined;
        }));

        // Viewport size excludes the scrollbars; "outer" approximates the
        // window frame.
        var vp = canvas.GetViewportSize();
        var outer = canvas.GetOuterSize();
        w.Set("innerWidth", JsValue.From(vp.Width));
        w.Set("innerHeight", JsValue.From(vp.Height));
        w.Set("outerWidth", JsValue.From(outer.Width > 0 ? outer.Width : vp.Width));
        w.Set("outerHeight", JsValue.From(outer.Height > 0 ? outer.Height : vp.Height));

        // window.status / defaultStatus — the #1 script of the era
        // (onMouseOver="window.status='...'; return true").  Seeded once;
        // a re-registration must NOT clear what a previous script wrote.
        if (!w.Has("status")) w.Set("status", JsValue.From(""));
        if (!w.Has("defaultStatus")) w.Set("defaultStatus", JsValue.From(""));

        // Frame-ish self references
        w.Set("window", JsValue.FromObject(w));
        w.Set("self", JsValue.FromObject(w));
        w.Set("top", JsValue.FromObject(w));
        w.Set("parent", JsValue.FromObject(w));

        // Event handler slots (fired by the shell) — seeded once; a
        // re-registration must keep what a previous script assigned
        // (window.onload = startClock in script 1, fired after parse).
        if (!w.Has("onload")) w.Set("onload", JsValue.Undefined);
        if (!w.Has("onunload")) w.Set("onunload", JsValue.Undefined);
        if (!w.Has("onerror")) w.Set("onerror", JsValue.Undefined);

        if (!w.Has("name")) w.Set("name", JsValue.From(""));

        scope.Define("window", JsValue.FromObject(w));
    }

    // ─────────────────────────────────────────────────────────────────────
    // navigator
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterNavigator(JsScope scope)
    {
        var n = new JsObject();

        // Pretend to be Navigator 3.01 on Windows 95 — this is what
        // era sniffing scripts expect to find
        n.Set("appName", JsValue.From("Netscape"));
        n.Set("appVersion", JsValue.From("3.01 (Win95; I)"));
        n.Set("appCodeName", JsValue.From("Mozilla"));
        n.Set("userAgent", JsValue.From("Mozilla/3.0 (compatible; Retro96/1.0; Windows 95)"));
        n.Set("language", JsValue.From("en"));
        n.Set("platform", JsValue.From("Win32"));
        n.Set("cookieEnabled", JsValue.From(true));

        // mimeTypes / plugins — sniffed occasionally; empty arrays
        n.Set("mimeTypes", JsValue.FromObject(NewArray(scope)));
        n.Set("plugins", JsValue.FromObject(NewArray(scope)));

        scope.Define("navigator", JsValue.FromObject(n));
    }

    /// <summary>
    /// The screen object (JS 1.1) — the Bravenet-era counter scripts read
    /// screen.width / screen.colorDepth and died with "'screen' is not
    /// defined" before this, taking their document.write output with them.
    /// Values come from the real primary screen when we have a handle on
    /// one, else the classic 800x600x256 era setup.
    /// </summary>
    private static void RegisterScreen(JsScope scope, BrowserCanvas canvas)
    {
        int w, h, depth;
        canvas.GetScreenMetrics(out w, out h, out depth);

        var s = new JsObject();
        s.Set("width", JsValue.From(w));
        s.Set("height", JsValue.From(h));
        s.Set("availWidth", JsValue.From(w));
        s.Set("availHeight", JsValue.From(h - 28));      // minus the taskbar
        s.Set("colorDepth", JsValue.From(depth));
        s.Set("pixelDepth", JsValue.From(depth));
        scope.Define("screen", JsValue.FromObject(s));
    }

    // ─────────────────────────────────────────────────────────────────────
    // document
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterDocument(JsScope scope, DomDocument doc,
                                         BrowserCanvas canvas, DocumentBindingsState state)
    {
        // DocumentObject routes title/cookie/colour reads and writes to the
        // live document — the old "property wrapper" objects never worked:
        // they were stored as property VALUES, so document.title read back
        // the wrapper object itself ("[object Object]") and an assignment
        // merely replaced the wrapper.
        var d = new DocumentObject(doc, canvas, state);

        d.Set("URL", JsValue.From(doc.BaseUrl?.ToAbsolute() ?? "about:blank"));
        d.Set("location", scope.Has("location") ? scope.Get("location") : JsValue.Undefined);

        d.Set("lastModified", JsValue.From(state.LastModified));
        d.Set("referrer", JsValue.From(state.Referrer));

        // Live DOM-0 collections (built after parse — every image the page
        // will ever contain is present by the time top-level scripts run;
        // images written by mid-parse document.write land in the tree
        // before the LAST script block sees them because the shell
        // re-registers bindings after the parse completes)
        d.Set("forms", JsValue.FromObject(BuildElementCollection(scope, doc.Forms, state)));
        d.Set("images", JsValue.FromObject(BuildElementCollection(scope, doc.Images, state)));
        d.Set("links", JsValue.FromObject(BuildElementCollection(scope, doc.Links, state)));
        d.Set("anchors", JsValue.FromObject(BuildElementCollection(scope, doc.Anchors, state)));

        d.Set("write", Fn(scope, "write", (self, args) =>
        {
            foreach (var arg in args)
                state.WriteBuffer.Append(arg.ToJsString());
            ApplyPostParseWrite(state);
            return JsValue.Undefined;
        }));

        d.Set("writeln", Fn(scope, "writeln", (self, args) =>
        {
            foreach (var arg in args)
                state.WriteBuffer.Append(arg.ToJsString());
            state.WriteBuffer.Append('\n');
            ApplyPostParseWrite(state);
            return JsValue.Undefined;
        }));

        // Era no-ops that scripts called defensively
        d.Set("open", Fn(scope, "open", (self, args) => JsValue.Undefined));
        d.Set("close", Fn(scope, "close", (self, args) => JsValue.Undefined));
        d.Set("clear", Fn(scope, "clear", (self, args) => JsValue.Undefined));

        // getElementById — the CSS1-era modal dialogs
        // (document.getElementById("welcomeDialog").style.display = …)
        // died with "'getElementById' is not a function" before this.
        d.Set("getElementById", Fn(scope, "getElementById", (self, args) =>
        {
            string id = args.Length > 0 ? args[0].ToJsString() : "";
            if (string.IsNullOrEmpty(id)) return JsValue.Null;
            var el = doc.ElementDescendants()
                .FirstOrDefault(e => string.Equals(
                    e.GetAttr("id"), id, StringComparison.Ordinal));
            return el == null ? JsValue.Null
                : JsValue.FromObject(WrapElement(el, state));
        }));

        // createElement — old pages use this for small bits of dynamic DOM
        // (usually an element is created, configured, then appended later).
        d.Set("createElement", Fn(scope, "createElement", (self, args) =>
        {
            string tagName = args.Length > 0 ? args[0].ToJsString() : "";
            if (string.IsNullOrWhiteSpace(tagName)) return JsValue.Null;

            var element = new DomElement(tagName.Trim());
            return JsValue.FromObject(WrapElement(element, state));
        }));

        // getElementsByTagName — legacy pages use this constantly for broad
        // DOM scans, including document.getElementsByTagName("a") and "*".
        d.Set("getElementsByTagName", Fn(scope, "getElementsByTagName", (self, args) =>
        {
            string tagName = args.Length > 0 ? args[0].ToJsString() : "";
            if (string.IsNullOrEmpty(tagName)) return JsValue.FromObject(NewArray(scope));

            IEnumerable<DomElement> matches = tagName == "*"
                ? doc.ElementDescendants()
                : doc.ElementDescendants().Where(e => string.Equals(
                    e.TagName, tagName, StringComparison.OrdinalIgnoreCase));

            return JsValue.FromObject(BuildElementCollection(scope, matches, state));
        }));

        // getElementsByName — named lookups over anchors/inputs
        d.Set("getElementsByName", Fn(scope, "getElementsByName", (self, args) =>
        {
            string nm = args.Length > 0 ? args[0].ToJsString() : "";
            var arr = NewArray(scope);
            int i = 0;
            foreach (var e in doc.ElementDescendants()
                .Where(e => string.Equals(e.GetAttr("name"), nm,
                    StringComparison.Ordinal)))
            {
                arr.Set(i.ToString(), JsValue.FromObject(WrapElement(e, state)));
                i++;
            }
            arr.Set("length", JsValue.From(i));
            return JsValue.FromObject(arr);
        }));

        scope.Define("document", JsValue.FromObject(d));
    }

    /// <summary>
    /// Post-parse document.write semantics.  While the parser is still
    /// streaming (ParseComplete == false) the shell's inline-script
    /// callback returns the buffer for token splicing — this method does
    /// nothing.  After the parse, a write from an event handler or timer
    /// follows the period browsers' implicit document.open(): the
    /// yet-unapplied buffer tail is parsed as replacement markup and
    /// transplanted INTO the live DomDocument in place (same object), so
    /// every reference — state.Document, the scope's document object, the
    /// shell's cached root — keeps pointing at the current page.
    /// The replacement markup's own &lt;script&gt; blocks do not execute
    /// (era browsers did run them on document.close(); an acceptable,
    /// documented simplification — no test corpus relies on it).
    /// </summary>
    private static void ApplyPostParseWrite(DocumentBindingsState state)
    {
        var doc = state.Document;
        if (doc?.ParseComplete != true || doc.BaseUrl == null)
            return;                       // mid-parse: parser callback splices

        int length = state.WriteBuffer.Length;
        // If the shell cleared the buffer under us (every parse-phase
        // script callback does), an applied offset can overshoot; treat
        // the whole current buffer as fresh in that case.
        int start = state.WriteBufferAppliedTo > length ? 0 : state.WriteBufferAppliedTo;
        if (start >= length)
            return;                       // nothing new to apply

        string pending = state.WriteBuffer.ToString(start, length - start);
        state.WriteBufferAppliedTo = length;

        try
        {
            var replacement = Retro96.Engine.Html.HtmlParser.Parse(
                pending, doc.BaseUrl, doc.Cookies, onScript: null);

            // Transplant the replacement tree into the live document.
            foreach (var child in doc.Children.ToList())
                child.Parent = null;
            doc.Children.Clear();
            foreach (var child in replacement.Children.ToList())
                doc.AppendChild(child);

            // Document-level state comes from the replacement markup
            // (implicit-open wiped the page), except session state.
            doc.Title = replacement.Title;
            doc.BaseTarget = replacement.BaseTarget;
            doc.MetaRefresh = replacement.MetaRefresh;
            doc.BodyTextColor = replacement.BodyTextColor;
            doc.BodyLinkColor = replacement.BodyLinkColor;
            doc.BodyVLinkColor = replacement.BodyVLinkColor;
            doc.BodyALinkColor = replacement.BodyALinkColor;
            doc.BodyBackground = replacement.BodyBackground;
            doc.BaseFontSize = replacement.BaseFontSize;
            doc.Charset = replacement.Charset;
        }
        catch
        {
            // A write of garbage must never take the host page down — the
            // era engines rendered whatever they could and kept going.
        }

        // The DOM was replaced in place, so the old layout tree is no longer
        // valid. Reflow resolves the replacement styles and rebuilds all box
        // geometry before repainting; a plain repaint leaves stale pixels and
        // stale boxes from the previous document.
        state.Canvas?.ReflowDocument();
    }

    private static JsObject NewArray(JsScope scope)
    {
        var arr = new JsObject { Class = "Array" };
        arr.Set("length", JsValue.From(0));
        return arr;
    }

    /// <summary>
    /// Builds a DOM-0 collection object: numeric keys, length, and a
    /// named-lookup fallback for forms ("document.forms.myform").
    /// </summary>
    private static JsObject BuildElementCollection(
        JsScope scope, IEnumerable<DomElement> elements, DocumentBindingsState state)
    {
        var collection = new JsObject();
        int i = 0;
        foreach (var elem in elements)
        {
            var wrapper = WrapElement(elem, state);
            collection.Set(i.ToString(), JsValue.FromObject(wrapper));
            i++;

            // Named lookup by form name
            var name = elem.GetAttr("name");
            if (!string.IsNullOrEmpty(name) && !collection.HasOwn(name))
                collection.Set(name, JsValue.FromObject(wrapper));
        }
        collection.Set("length", JsValue.From(i));
        return collection;
    }

    // ─────────────────────────────────────────────────────────────────────
    // history
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterHistory(JsScope scope, NavigationHistory history)
    {
        var h = new JsObject();

        h.Set("length", JsValue.From(history.Count));

        h.Set("back", Fn(scope, "back", (self, args) =>
        {
            if (history.CanGoBack) history.GoBack();
            return JsValue.Undefined;
        }));

        h.Set("forward", Fn(scope, "forward", (self, args) =>
        {
            if (history.CanGoForward) history.GoForward();
            return JsValue.Undefined;
        }));

        h.Set("go", Fn(scope, "go", (self, args) =>
        {
            if (args.Length > 0)
                history.Go((int)args[0].ToNumber());
            return JsValue.Undefined;
        }));

        scope.Define("history", JsValue.FromObject(h));
    }

    // ─────────────────────────────────────────────────────────────────────
    // location
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterLocation(JsScope scope, DomDocument doc, BrowserCanvas canvas)
    {
        var url = doc.BaseUrl ?? ParsedUrl.Parse("about:blank");

        var l = new LocationObject(doc, canvas);
        // Seed the initial href directly into the property bag — do NOT go
        // through l.Set("href", ...), since that override treats every write
        // as a script-driven navigation and calls canvas.NavigateTo(...).
        // Using Set here made every page load re-navigate to itself the
        // instant its `location` object was set up, causing an infinite
        // reload loop on every single page.
        l.Properties["href"] = JsValue.From(url.ToAbsolute());
        l.Set("hostname", JsValue.From(url.Host ?? ""));
        l.Set("host", JsValue.From(url.Port > 0 ? $"{url.Host}:{url.Port}" : (url.Host ?? "")));
        l.Set("pathname", JsValue.From(url.Path ?? "/"));
        l.Set("search", JsValue.From(url.Query ?? ""));
        l.Set("hash", JsValue.From(url.Fragment ?? ""));
        l.Set("port", JsValue.From(url.Port > 0 ? url.Port.ToString() : ""));
        l.Set("protocol", JsValue.From(
            string.IsNullOrEmpty(url.Scheme) ? "http:" : url.Scheme + ":"));

        l.Set("toString", Fn(scope, "toString", (self, args) =>
            JsValue.From(self.GetObjectOrFunction().Get("href").ToJsString())));

        l.Set("reload", Fn(scope, "reload", (self, args) =>
        {
            var href = self.GetObjectOrFunction().Get("href");
            if (href.Type == JsType.String)
            {
                Retro96.DebugLog.Write($"JS location.reload() -> '{href.GetString()}'");
                canvas.NavigateTo(href.GetString());
            }
            return JsValue.Undefined;
        }));

        l.Set("replace", Fn(scope, "replace", (self, args) =>
        {
            if (args.Length > 0)
            {
                string target = args[0].ToJsString();
                Retro96.DebugLog.Write($"JS location.replace() -> '{target}'");
                canvas.NavigateToReplace(target);
            }
            return JsValue.Undefined;
        }));

        scope.Define("location", JsValue.FromObject(l));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Image constructor (rollover preloading)
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterImageConstructor(JsScope scope, BrowserCanvas canvas,
                                                 DocumentBindingsState state)
    {
        var imageCtor = new JsFunction((self, args) =>
        {
            // ImageObject: a write to .src starts the preload so the swap is
            // instant. The old "src property wrapper" object never worked —
            // img.src read back the wrapper, and img.src = "…" replaced it.
            var img = new ImageObject(canvas);
            if (args.Length >= 1) img.Set("width", JsValue.From(args[0].ToNumber()));
            if (args.Length >= 2) img.Set("height", JsValue.From(args[1].ToNumber()));
            img.Set("complete", JsValue.From(false));
            return JsValue.FromObject(img);
        }, scope, "Image");
        scope.Define("Image", JsValue.FromFunction(imageCtor));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Element wrappers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns (creating once) the script wrapper for a DOM element.
    /// Property reads/writes route to the live element and repaint.
    /// </summary>
    public static JsObject WrapElement(DomElement element, DocumentBindingsState state)
    {
        if (state.ElementWrappers.TryGetValue(element, out var existing))
            return existing;

        var wrapper = new ElementWrapper(element, state.Canvas,
            state.Interpreter?.GlobalScope, state);
        state.ElementWrappers[element] = wrapper;
        return wrapper;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Virtual-property objects — JsObject subclasses whose Get/Set touch
    // the live document / shell instead of a plain slot.  (These REPLACE
    // the element they wrap, like LocationObject — being stored as a
    // property VALUE never fired their overrides.)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>location.href = "..." navigates; reads return the URL.</summary>
    private sealed class LocationObject : JsObject
    {
        private readonly DomDocument _doc;
        private readonly BrowserCanvas _canvas;

        public LocationObject(DomDocument doc, BrowserCanvas canvas)
        {
            _doc = doc;
            _canvas = canvas;
        }

        public override JsValue Get(string name)
        {
            if (name == "href")
            {
                var url = _doc.BaseUrl?.ToAbsolute();
                if (Properties.TryGetValue("href", out var stored) &&
                    stored.Type == JsType.String)
                    url = stored.GetString();
                return JsValue.From(url ?? "about:blank");
            }
            return base.Get(name);
        }

        public override void Set(string name, JsValue value)
        {
            if (name == "href" && value.Type == JsType.String)
            {
                Properties["href"] = value;
                Retro96.DebugLog.Write($"JS location.href SET -> '{value.GetString()}'");
                _canvas.NavigateTo(value.GetString());
                return;
            }
            base.Set(name, value);
        }
    }

    /// <summary>
    /// The document object: title/cookie/bgColor/fgColor/link colours are
    /// live virtual properties over the real document — reads reflect it,
    /// writes update it and repaint.
    /// </summary>
    private sealed class DocumentObject : JsObject
    {
        private readonly DomDocument _doc;
        private readonly BrowserCanvas _canvas;
        private readonly DocumentBindingsState? _state;

        public DocumentObject(DomDocument doc, BrowserCanvas canvas,
                              DocumentBindingsState? state = null)
        {
            _doc = doc;
            _canvas = canvas;
            _state = state;
        }

        private DomElement? Body => _doc.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "body");

        private ParsedUrl BaseUrlOrBlank
        {
            get
            {
                try { return ParsedUrl.Parse(_doc.BaseUrl?.ToAbsolute() ?? "about:blank"); }
                catch { return ParsedUrl.Parse("about:blank"); }
            }
        }

        public override JsValue Get(string name)
        {
            switch (name)
            {
                case "title": return JsValue.From(_doc.Title);
                case "body":
                case "documentElement":
                    {
                        // document.body — the live wrapper (bgColor etc.
                        // route through Set; scripts also probe its existence)
                        var target = name == "body"
                            ? Body
                            : _doc.ElementDescendants().FirstOrDefault(e => e.TagName == "html");
                        if (target == null) return JsValue.Null;
                        if (_state == null) return JsValue.Null;
                        return JsValue.FromObject(WrapElement(target, _state));
                    }
                case "cookie":
                    try { return JsValue.From(_doc.Cookies.Get(BaseUrlOrBlank)); }
                    catch { return JsValue.From(""); }
                case "bgColor": return JsValue.From(Body?.GetAttr("bgcolor") ?? "#c0c0c0");
                case "fgColor": return JsValue.From(_doc.BodyTextColor);
                case "linkColor": return JsValue.From(_doc.BodyLinkColor);
                case "vlinkColor": return JsValue.From(_doc.BodyVLinkColor);
                case "alinkColor": return JsValue.From(_doc.BodyALinkColor);
                default:
                    // DOM-0 NAMED ACCESS — the era's #1 form idiom:
                    //     document.clockForm.digits.value = time
                    // resolves clockForm as a named <form>, NOT through
                    // document.forms only.  NN2/NN3 exposed named forms,
                    // images and applets directly on the document object;
                    // without this every clock/validator script of the era
                    // read its form fields as undefined.
                    if (_state != null)
                    {
                        foreach (var form in _doc.Forms)
                            if (string.Equals(form.GetAttr("name"), name,
                                StringComparison.Ordinal))
                                return JsValue.FromObject(WrapElement(form, _state));

                        foreach (var img in _doc.Images)
                            if (string.Equals(img.GetAttr("name"), name,
                                StringComparison.Ordinal))
                                return JsValue.FromObject(WrapElement(img, _state));
                    }
                    return base.Get(name);
            }
        }

        public override void Set(string name, JsValue value)
        {
            switch (name)
            {
                case "title":
                    _doc.Title = value.ToJsString();
                    _canvas.UpdateDocumentTitle(_doc.Title);
                    return;
                case "cookie":
                    try { _doc.Cookies.Set(value.ToJsString(), BaseUrlOrBlank); }
                    catch { /* cookies are best-effort from script */ }
                    return;
                case "bgColor":
                    Body?.SetAttr("bgcolor", value.ToJsString());
                    _canvas.RequestRerender();
                    return;
                case "fgColor":
                    _doc.BodyTextColor = value.ToJsString();
                    _canvas.RequestRerender();
                    return;
                case "linkColor":
                    _doc.BodyLinkColor = value.ToJsString();
                    _canvas.RequestRerender();
                    return;
                case "vlinkColor":
                    _doc.BodyVLinkColor = value.ToJsString();
                    _canvas.RequestRerender();
                    return;
                case "alinkColor":
                    _doc.BodyALinkColor = value.ToJsString();
                    _canvas.RequestRerender();
                    return;
                default:
                    base.Set(name, value);
                    return;
            }
        }
    }

    /// <summary>Minimal text-node wrapper for firstChild/lastChild/
    /// childNodes/siblings: {nodeType, nodeName, nodeValue, data, length}.</summary>
    private sealed class TextNodeObject : JsObject
    {
        public TextNodeObject(string data, int nodeType = 3)
        {
            Set("nodeType", JsValue.From(nodeType));
            Set("nodeName", JsValue.From(nodeType == 8 ? "#comment" : "#text"));
            Set("nodeValue", JsValue.From(data));
            Set("data", JsValue.From(data));
            Set("length", JsValue.From(data.Length));
        }
    }

    /// <summary>new Image().src = "..." — starts the preload fetch.</summary>
    private sealed class ImageObject : JsObject
    {
        private readonly BrowserCanvas _canvas;

        public ImageObject(BrowserCanvas canvas)
        {
            _canvas = canvas;
            Class = "Image";
        }

        public override void Set(string name, JsValue value)
        {
            base.Set(name, value);
            if (name == "src" && value.Type == JsType.String)
            {
                base.Set("complete", JsValue.From(false));
                try { _canvas.PrefetchImage(value.GetString()); }
                catch { /* best-effort preload */ }
            }
        }
    }

    /// <summary>
    /// Live wrapper around a DomElement: reads and writes of the common
    /// DOM-0 properties go straight to the element (and repaint), so
    /// document.images[0].src = 'over.gif' really swaps the image.
    /// </summary>
    public sealed class ElementWrapper : JsObject
    {
        private readonly DomElement _element;
        private readonly BrowserCanvas? _canvas;
        private readonly JsScope _scope;
        private readonly DocumentBindingsState? _state;

        public ElementWrapper(DomElement element, BrowserCanvas? canvas,
                              JsScope? scope = null, DocumentBindingsState? state = null)
        {
            _element = element;
            _canvas = canvas;
            _scope = scope ?? new JsScope();
            _state = state;
            Class = "Element";
        }

        public DomElement Element => _element;

        // Properties stored on the element itself (attribute name → storage key)
        private static readonly HashSet<string> RoutedAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            "src", "href", "value", "checked", "name", "id", "target",
            "action", "method", "width", "height", "alt", "border",
            "align", "bgColor", "title", "maxlength", "size", "cols", "rows",
            "type"
        };

        /// <summary>
        /// .style — a live InlineStyleObject bound to this element's STYLE
        /// attribute is created lazily by Get(name) and cached in
        /// Properties, so consecutive reads observe the same object and
        /// its writes reflow the page.
        /// </summary>
        private static IEnumerable<DomElement> OptionsOf(DomElement select)
        {
            foreach (var e in select.ElementDescendants())
                if (e.TagName == "option")
                    yield return e;
        }

        private static int CountOptions(DomElement select)
        {
            int n = 0;
            foreach (var _ in OptionsOf(select)) n++;
            return n;
        }

        /// <summary>The selected option — first option by default (matches
        /// the renderer's dropdown and form submission).</summary>
        private static DomElement? SelectedOption(DomElement select)
        {
            DomElement? first = null;
            foreach (var o in OptionsOf(select))
            {
                first ??= o;
                if (o.HasAttr("selected")) return o;
            }
            return first;
        }

        private static string OptionValue(DomElement opt) =>
            opt.GetAttr("value") ?? (opt.InnerText ?? "").Trim();

        /// <summary>Wraps a DOM node: elements get the full ElementWrapper,
        /// text nodes get a minimal {nodeType:3, nodeValue/data} object,
        /// comments get {nodeType:8}.</summary>
        private JsObject WrapNode(DomNode node)
        {
            if (node is DomElement el)
            {
                var w = new ElementWrapper(el, _canvas, _scope, _state);
                return w;
            }
            if (node is DomText tx)
                return new TextNodeObject(tx.Data);
            return new TextNodeObject("", nodeType: 8);   // comment / other
        }

        private JsValue WrapSibling(DomElement from, int direction)
        {
            if (from.Parent == null) return JsValue.Null;
            var siblings = from.Parent.Children;
            int idx = -1;
            for (int i = 0; i < siblings.Count; i++)
                if (ReferenceEquals(siblings[i], from)) { idx = i; break; }
            int next = idx + direction;
            if (idx < 0 || next < 0 || next >= siblings.Count) return JsValue.Null;
            return JsValue.FromObject(WrapNode(siblings[next]));
        }

        private static string EscapeHtmlText(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>Minimal HTML serialization for innerHTML/outerHTML reads
        /// (era scripts read these far more often than they wrote them;
        /// writes still go through document.write).</summary>
        private static void SerializeNode(DomNode node, System.Text.StringBuilder sb)
        {
            if (node is DomText t)
            {
                sb.Append(EscapeHtmlText(t.Data));
                return;
            }
            if (node is DomComment c)
            {
                sb.Append("<!--").Append(c.Text).Append("-->");
                return;
            }
            if (node is not DomElement e) return;

            sb.Append('<').Append(e.TagName);
            foreach (var (attrName, attrValue) in e.Attrs)
                sb.Append(' ').Append(attrName).Append("=\"")
                  .Append(attrValue.Replace("&", "&amp;")
                                    .Replace("\"", "&quot;"))
                  .Append('"');
            sb.Append('>');

            // script/style keep their raw text (no escaping, no children markup)
            if (e.TagName is "script" or "style")
            {
                foreach (var child in e.Children)
                    if (child is DomText st) sb.Append(st.Data);
            }
            else
            {
                foreach (var child in e.Children)
                    SerializeNode(child, sb);
            }

            sb.Append("</").Append(e.TagName).Append('>');
        }

        public override JsValue Get(string name)
        {
            // ── node identity / tree navigation (era scripts probed these) ──
            switch (name)
            {
                case "tagName":
                case "nodeName":
                    return JsValue.From(_element.TagName);
                case "nodeType":
                    return JsValue.From(1);
                case "innerHTML":
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var child in _element.Children)
                            SerializeNode(child, sb);
                        return JsValue.From(sb.ToString());
                    }
                case "innerText":
                case "text":
                    return JsValue.From(_element.InnerText);
                case "outerHTML":
                    {
                        var sb = new System.Text.StringBuilder();
                        SerializeNode(_element, sb);
                        return JsValue.From(sb.ToString());
                    }
                case "firstChild":
                    {
                        var fc = _element.FirstChild;
                        return fc == null ? JsValue.Null : JsValue.FromObject(WrapNode(fc));
                    }
                case "lastChild":
                    {
                        var lc = _element.LastChild;
                        return lc == null ? JsValue.Null : JsValue.FromObject(WrapNode(lc));
                    }
                case "parentNode":
                case "parentElement":
                    {
                        if (_element.Parent is not DomElement pe) return JsValue.Null;
                        if (_state == null) return JsValue.Null;
                        return JsValue.FromObject(WrapElement(pe, _state));
                    }
                case "nextSibling":
                    return WrapSibling(_element, +1);
                case "previousSibling":
                    return WrapSibling(_element, -1);
                case "childNodes":
                    {
                        var arr = new JsObject { Class = "Array" };
                        int i = 0;
                        foreach (var child in _element.Children)
                        {
                            arr.Set(i.ToString(), JsValue.FromObject(WrapNode(child)));
                            i++;
                        }
                        arr.Set("length", JsValue.From(i));
                        return JsValue.FromObject(arr);
                    }
            }

            // .style — the CSS1-era modal-dialog pattern
            // (getElementById("dialog").style.display = "block")
            if (name == "style" && !Properties.ContainsKey("style"))
            {
                Properties["style"] = JsValue.FromObject(
                    new InlineStyleObject(_element, _canvas, _scope));
            }

            // getElementsByTagName — DOM element collections are descendants
            // of this element (the element itself is not included).
            if (name == "getElementsByTagName")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    string tagName = args.Length > 0 ? args[0].ToJsString() : "";
                    if (string.IsNullOrEmpty(tagName))
                        return JsValue.FromObject(NewArray(_scope));

                    var matches = tagName == "*"
                        ? _element.ElementDescendants()
                        : _element.ElementDescendants().Where(e => string.Equals(
                            e.TagName, tagName, StringComparison.OrdinalIgnoreCase));

                    if (_state == null)
                        return JsValue.FromObject(NewArray(_scope));

                    return JsValue.FromObject(BuildElementCollection(_scope, matches, _state));
                }, _scope, "getElementsByTagName"));

            // Event handlers live as plain properties (assigned functions)
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                return Properties.TryGetValue(name, out var h) ? h : JsValue.Undefined;

            if (name == "form")
            {
                DomElement? form = _element.FormOwner;
                for (var parent = _element.Parent as DomElement;
                     form == null && parent != null;
                     parent = parent.Parent as DomElement)
                {
                    if (parent.TagName == "form") form = parent;
                }
                return form != null && _state != null
                    ? JsValue.FromObject(WrapElement(form, _state))
                    : JsValue.Null;
            }

            // ── select-specific (form-validation scripts lived on these) ──
            if (_element.TagName == "select")
            {
                switch (name)
                {
                    case "value":
                        {
                            var opt = SelectedOption(_element);
                            return JsValue.From(opt == null ? "" : OptionValue(opt));
                        }
                    case "selectedIndex":
                        {
                            int i = 0;
                            foreach (var o in OptionsOf(_element))
                            {
                                if (o.HasAttr("selected")) return JsValue.From(i);
                                i++;
                            }
                            return JsValue.From(0);
                        }
                    case "options":
                        {
                            var arr = new JsObject { Class = "Array" };
                            int i = 0;
                            foreach (var o in OptionsOf(_element))
                            {
                                arr.Set(i.ToString(),
                                    JsValue.FromObject(new ElementWrapper(o, _canvas, _scope)));
                                i++;
                            }
                            arr.Set("length", JsValue.From(i));
                            return JsValue.FromObject(arr);
                        }
                    case "length":
                        return JsValue.From(CountOptions(_element));
                }
            }

            if (name == "text")
                return JsValue.From(_element.InnerText);

            if (RoutedAttrs.Contains(name))
            {
                var attr = _element.GetAttr(name);
                if (attr == null) return JsValue.From("");
                if (name == "checked")
                    return JsValue.From(attr.Length > 0);
                if (name == "width" || name == "height" || name == "size" ||
                    name == "maxlength" || name == "cols" || name == "rows")
                    return JsValue.From(JsValue.StringToNumber(attr));
                return JsValue.From(attr);
            }

            if (name == "length")
            {
                // forms: number of controls
                if (_element.TagName == "form")
                    return JsValue.From(CountControls(_element));
                return base.Get(name);
            }

            if (name == "elements" && _element.TagName == "form")
            {
                var arr = new JsObject { Class = "Array" };
                int i = 0;
                foreach (var control in ControlsOf(_element))
                {
                    var wrapper = new ElementWrapper(control, _canvas, _scope);
                    arr.Set(i.ToString(), JsValue.FromObject(wrapper));
                    var cname = control.GetAttr("name");
                    if (!string.IsNullOrEmpty(cname) && !arr.HasOwn(cname))
                        arr.Set(cname, JsValue.FromObject(wrapper));
                    i++;
                }
                arr.Set("length", JsValue.From(i));
                return JsValue.FromObject(arr);
            }

            if (name == "submit" && _element.TagName == "form")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    _canvas?.SubmitForm(_element, null);
                    return JsValue.Undefined;
                }, _scope, "submit"));

            // appendChild / insertBefore / removeChild — enough of the old DOM
            // mutation surface for pages that build small bits of UI at runtime.
            if (name == "appendChild")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0 || args[0].Type != JsType.Object)
                        return JsValue.Null;

                    if (args[0].GetObject() is not ElementWrapper childWrapper)
                        return JsValue.Null;

                    var child = childWrapper.Element;
                    _element.AppendChild(child);
                    _canvas?.ReflowDocument();
                    return JsValue.FromObject(WrapElement(child, _state!));
                }, _scope, "appendChild"));

            if (name == "insertBefore")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0 || args[0].Type != JsType.Object)
                        return JsValue.Null;

                    if (args[0].GetObject() is not ElementWrapper childWrapper)
                        return JsValue.Null;

                    var child = childWrapper.Element;
                    DomElement? reference = null;
                    if (args.Length > 1 && args[1].Type == JsType.Object &&
                        args[1].GetObject() is ElementWrapper referenceWrapper)
                        reference = referenceWrapper.Element;

                    _element.InsertBefore(child, reference);
                    _canvas?.ReflowDocument();
                    return JsValue.FromObject(WrapElement(child, _state!));
                }, _scope, "insertBefore"));

            if (name == "removeChild")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0 || args[0].Type != JsType.Object)
                        return JsValue.Null;

                    if (args[0].GetObject() is not ElementWrapper childWrapper)
                        return JsValue.Null;

                    var child = childWrapper.Element;
                    _element.RemoveChild(child);
                    _canvas?.ReflowDocument();
                    return JsValue.FromObject(WrapElement(child, _state!));
                }, _scope, "removeChild"));

            // setAttribute / getAttribute — the generic attribute surface
            // era scripts used for late wiring.  setAttribute on an "on*"
            // name routes into EventHandlers exactly like the parser's
            // onclick="..." path, so one dispatch mechanism serves inline
            // attributes, element.onclick = fn, AND setAttribute.
            if (name == "setAttribute")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    string attrName = args.Length > 0 ? args[0].ToJsString() : "";
                    if (attrName.Length == 0) return JsValue.Undefined;
                    string attrValue = args.Length > 1 ? args[1].ToJsString() : "";
                    _element.SetAttr(attrName, attrValue);
                    if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase) &&
                        attrName.Length > 2)
                        _element.EventHandlers[attrName.ToLowerInvariant()] = attrValue;
                    _canvas?.RequestRerender();
                    return JsValue.Undefined;
                }, _scope, "setAttribute"));

            if (name == "getAttribute")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    string attrName = args.Length > 0 ? args[0].ToJsString() : "";
                    var value = _element.GetAttr(attrName);
                    return value == null ? JsValue.Null : JsValue.From(value);
                }, _scope, "getAttribute"));

            // DOM-0 NAMED CONTROL ACCESS — form.digits (the era's field
            // idiom, document.clockForm.digits.value = ...).  Named controls
            // live directly on the form object, exactly like on
            // form.elements; without this the field read came back
            // undefined and clock/validator writes went nowhere.
            if (_state != null && _element.TagName == "form" &&
                !char.IsDigit(name[0]) && name != "style" &&
                !name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var control in ControlsOf(_element))
                    if (string.Equals(control.GetAttr("name"), name,
                        StringComparison.Ordinal))
                        return JsValue.FromObject(WrapElement(control, _state));
            }

            return base.Get(name);
        }

        public override void Set(string name, JsValue value)
        {
            if (name == "innerHTML")
            {
                _element.Children.Clear();
                var doc = _element.OwnerDocument();
                if (doc != null)
                {
                    var fragment = HtmlParser.Parse(value.ToJsString(),
                        doc.BaseUrl ?? ParsedUrl.Parse("about:blank"), doc.Cookies);
                    var body = fragment.ElementDescendants()
                        .FirstOrDefault(e => e.TagName == "body");
                    if (body != null)
                    {
                        foreach (var child in body.Children.ToList())
                            _element.AppendChild(child);
                    }
                }
                _canvas?.ReflowDocument();
                return;
            }

            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                // Handler assignment: element.onclick = function() {...}
                Properties[name] = value;

                // Mirror into the element's EventHandlers as source too, so
                // the shell's single dispatch path works for both forms
                if (value.Type == JsType.Function)
                    _element.EventHandlers[name.ToLowerInvariant()] = "__js_handler__";
                return;
            }

            // ── select-specific writes ──────────────────────────────────
            if (_element.TagName == "select")
            {
                if (name == "value")
                {
                    string v = value.ToJsString();
                    foreach (var o in OptionsOf(_element))
                        o.SetAttr("selected", OptionValue(o) == v ? "" : null);
                    _canvas?.RequestRerender();
                    return;
                }
                if (name == "selectedIndex")
                {
                    int idx = (int)value.ToNumber();
                    int i = 0;
                    bool done = false;
                    foreach (var o in OptionsOf(_element))
                    {
                        bool sel = !done && i == idx;
                        if (sel) done = true;
                        o.SetAttr("selected", sel ? "" : null);
                        i++;
                    }
                    _canvas?.RequestRerender();
                    return;
                }
            }

            if (RoutedAttrs.Contains(name))
            {
                if (name == "checked")
                {
                    if (value.ToBoolean()) _element.SetAttr("checked", "");
                    else _element.SetAttr("checked", null);
                }
                else
                {
                    _element.SetAttr(name, value.ToJsString());
                }
                _canvas?.RequestRerender();
                return;
            }

            base.Set(name, value);
        }

        private static IEnumerable<DomElement> ControlsOf(DomElement form)
        {
            foreach (var e in form.ElementDescendants())
                if (e.TagName is "input" or "select" or "textarea" or "button")
                    yield return e;

            // Foster-parented forms (the <form>-between-table-and-rows
            // idiom) own controls that live in the table cells, NOT inside
            // the form element — form.elements, form.length and DOM-0
            // named access must see them too, exactly like period
            // browsers' form-element-pointer association.
            var doc = form.OwnerDocument();
            if (doc != null)
            {
                foreach (var e in doc.ElementDescendants())
                    if (e.FormOwner != null && ReferenceEquals(e.FormOwner, form))
                        yield return e;
            }
        }

        private static int CountControls(DomElement form)
        {
            int n = 0;
            foreach (var e in ControlsOf(form)) n++;
            return n;
        }
    }

    /// <summary>
    /// Live view of an element's inline STYLE attribute for scripts:
    ///     dialog.style.display = "none"   — hides, really
    ///     dialog.style.color     = "red"  — recolours, really
    /// Reads parse the current attribute; writes update it and trigger a
    /// full style re-resolve + re-layout through the canvas.
    /// </summary>
    private sealed class InlineStyleObject : JsObject
    {
        private readonly DomElement _element;
        private readonly BrowserCanvas? _canvas;

        public InlineStyleObject(DomElement element, BrowserCanvas? canvas, JsScope scope)
        {
            _element = element;
            _canvas = canvas;
            Class = "CSSStyleDeclaration";
        }

        private Dictionary<string, string> ParseCurrent()
        {
            var decls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? attr = _element.GetAttr("style");
            if (string.IsNullOrEmpty(attr)) return decls;

            foreach (var decl in Css.CssParser.ParseInlineStyle(attr))
            {
                string key = (decl.Property ?? "").Trim().ToLowerInvariant();
                if (key.Length > 0)
                    decls[key] = decl.Value ?? "";
            }
            return decls;
        }

        public override JsValue Get(string name)
        {
            // cssText — the whole attribute as source text
            if (name == "cssText")
                return JsValue.From(_element.GetAttr("style") ?? "");

            var decls = ParseCurrent();
            return decls.TryGetValue(name.ToLowerInvariant(), out string? v)
                ? JsValue.From(v)
                : JsValue.From("");
        }

        public override void Set(string name, JsValue value)
        {
            if (name == "cssText")
            {
                _element.SetAttr("style", value.ToJsString());
                Reflow();
                return;
            }

            string v = value.ToJsString().Trim();
            var decls = ParseCurrent();
            string key = name.ToLowerInvariant();
            if (v.Length == 0 || v.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("undefined", StringComparison.OrdinalIgnoreCase))
                decls.Remove(key);
            else
                decls[key] = v;

            _element.SetAttr("style",
                string.Join("; ", decls.Select(kv => $"{kv.Key}: {kv.Value}")));
            Reflow();
        }

        private void Reflow()
        {
            if (_canvas == null) return;
            try { _canvas.ReflowDocument(); }
            catch { /* best-effort reflow */ }
        }
    }
}