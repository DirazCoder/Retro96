using System;
using System.Collections.Generic;
using System.Linq;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Network;
using Retro96.Plugins;

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

    /// <summary>IE5 uniqueID registry — one stable "ms__idN" per element
    /// (checklist §10), counted per document/page.</summary>
    public readonly Dictionary<DomElement, string> UniqueIds = new();
    public int NextUniqueId;

    /// <summary>document.readyState="complete" fires document.onreadystatechange
    /// exactly once per page, when the post-parse RegisterAll first sees
    /// ParseComplete == true.</summary>
    public bool ReadyStateChangeFired;

    /// <summary>NS4 layer-object cache — document.layers["a"] must return the
    /// SAME layer object on every access.</summary>
    public readonly Dictionary<DomElement, JsObject> LayerWrappers = new();

    /// <summary>Scripted scroll position surfaced as window.pageXOffset/
    /// pageYOffset and element.scrollTop/scrollLeft readback. The shell does
    /// not expose its live scroll offset to the DOM bindings, so scrollTo()
    /// writes are mirrored here only (documented limitation).</summary>
    public double PageXOffset;
    public double PageYOffset;

    /// <summary>Element wrapper cache — one JsObject identity per element so
    /// property writes (rollover swaps) survive across script accesses.</summary>
    public readonly Dictionary<DomElement, JsObject> ElementWrappers = new();

    /// <summary>One stable document host per page. Parser-time RegisterAll calls
    /// rebind this object to the same live DOM instead of replacing the JS
    /// document object before every script.</summary>
    public JsObject? DocumentObject;

    /// <summary>Host-managed legacy &lt;embed&gt; script bridge. The JS engine only sees typed values and async promise facades.</summary>
    public Func<DomElement, (string ScriptName, IReadOnlyList<string> Methods)?>? EmbeddedScriptInfoResolver;
    public Func<DomElement, string, IReadOnlyList<Retro96.Plugins.JsValue>, Task<Retro96.Plugins.JsValue>>? EmbeddedScriptCall;
    public Func<DomElement, string, JsInterpreter, JsValue?>? JavaAppletScriptMemberResolver;
    public Func<DomElement, string, JsInterpreter, JsValue, bool>? JavaAppletScriptMemberSetter;
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
    // HTML void elements that never receive an explicit closing tag.
    // Keep this aligned with HtmlParser.IsVoidElement for DOM serialization.
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "embed",
        "frame", "hr", "img", "input", "isindex", "keygen", "link", "meta",
        "param", "server", "spacer", "wbr"
    };

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
        foreach (var applet in document.ElementDescendants().Where(Retro96.Engine.Java.JavaAppletHost.IsJavaElement))
        {
            string? nm = applet.GetAttr("name");
            nm = !string.IsNullOrEmpty(nm) ? nm : applet.GetAttr("id");
            if (!string.IsNullOrEmpty(nm) && !windowObj.HasOwn(nm))
                windowObj.Set(nm, JsValue.FromObject(WrapElement(applet, state)));
        }

        if (BrowserRuntime.SupportsInternetExplorerLegacy)
        {
            // IE's legacy host object model made named page elements reachable
            // from window. Keep forms/images above as the first-class cases,
            // then fill the remaining names/ids from the live DOM without
            // overwriting an explicit window property.
            foreach (var element in document.ElementDescendants())
            {
                string? id = element.GetAttr("id");
                string? name = element.GetAttr("name");
                string? key = !string.IsNullOrEmpty(id) ? id : name;
                if (!string.IsNullOrEmpty(key) && !windowObj.HasOwn(key))
                    windowObj.Set(key, JsValue.FromObject(WrapElement(element, state)));
            }
        }

        // The global scope falls back to the window object — bare
        // alert()/confirm()/status/scrollTo resolve through it.  Without
        // this, every window function that was not ALSO scope.Define'd was
        // unreachable as a bare identifier: alert("hi") threw
        // "'alert' is not a function" on every page that used one.
        globalScope.GlobalFallback = windowObj;

        // document.readyState transitions to "complete" after the parse;
        // fire document.onreadystatechange EXACTLY once at that point
        // (checklist §10 IE5: readyState + onreadystatechange). The
        // post-parse RegisterAll (the shell runs it before firing
        // window/body onload) is the transition moment.
        if (document.ParseComplete && !state.ReadyStateChangeFired)
        {
            state.ReadyStateChangeFired = true;
            if (state.DocumentObject?.Get("onreadystatechange") is { Type: JsType.Function } readyHandler)
            {
                try
                {
                    state.Interpreter?.CallFunction(readyHandler.GetFunction(),
                        JsValue.FromObject(state.DocumentObject), Array.Empty<JsValue>());
                }
                catch
                {
                    // A throwing readystatechange handler must not break
                    // the rest of the page's bindings.
                }
            }
        }
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
        if (w.Prototype == null &&
            scope.Get("Object") is { Type: JsType.Function } objectConstructor)
        {
            var prototype = objectConstructor.GetObjectOrFunction().Get("prototype");
            if (prototype.Type == JsType.Object)
                w.Prototype = prototype.GetObject();
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
            string name = args.Length > 1 ? args[1].ToJsString() : "";
            // Feature strings ("width=400,height=300") were cosmetic
            // differences between shells — parsed shape is accepted; every
            // feature beyond opening the window is ignored (documented).
            if (!string.IsNullOrEmpty(url) && BrowserRuntime.ScriptedWindowsAllowed)
                canvas.OpenNewWindow(url);

            // Documented limitation: the opened window is a separate shell
            // document with its own scripting context — cross-window
            // scripting is not reachable from these bindings, so window.open
            // returns a small window-like facade (name/opener/closed/close()
            // that flips the facade's own closed flag) instead of the real
            // new window object.
            var facade = new JsObject { Class = "Window" };
            facade.Set("name", JsValue.From(name));
            facade.Set("closed", JsValue.From(false));
            facade.Set("length", JsValue.From(0));
            facade.Set("opener", JsValue.FromObject(w));
            facade.Set("close", Fn(scope, "close", (s2, a2) =>
            {
                facade.Set("closed", JsValue.From(true));
                return JsValue.Undefined;
            }));
            facade.Set("focus", Fn(scope, "focus", (s2, a2) => JsValue.Undefined));
            return JsValue.FromObject(facade);
        }));

        w.Set("close", Fn(scope, "close", (self, args) =>
        {
            canvas.CloseHostWindow();
            return JsValue.Undefined;
        }));

        // window.opener — null unless this window itself was opened by
        // script (the shell does not wire opener back into the DOM
        // bindings; documented limitation).
        if (!w.Has("opener")) w.Set("opener", JsValue.Null);

        // window.frames[] / window.length — the frame/iframe count of THIS
        // document. Documented limitation: the real per-frame window
        // objects live in the shell (BrowserCanvas.FrameView / per-frame
        // interpreters) and are not reachable from the bindings; indexed and
        // named lookups return the frame ELEMENT wrapper, which at least
        // exposes src/name etc. to scripts.
        if (doc != null && state != null)
        {
            var frames = new FramesCollectionObject(doc, state, scope);
            w.Set("frames", JsValue.FromObject(frames));
            w.Set("length", JsValue.From(frames.FrameCount));
        }
        else if (!w.Has("frames"))
        {
            w.Set("frames", JsValue.FromObject(NewArray(scope)));
            if (!w.Has("length")) w.Set("length", JsValue.From(0));
        }

        // window.showModalDialog (IE5, checklist §10) — era-modal via the
        // shell's host-modal alert service. The dialog page itself is NOT
        // fetched (no secondary document plumbing from these bindings —
        // documented choice) and the return value is undefined (no
        // returnValue argument passing without a real dialog document).
        if (BrowserRuntime.SupportsInternetExplorerLegacy)
        {
            w.Set("showModalDialog", Fn(scope, "showModalDialog", (self, args) =>
            {
                string url = args.Length > 0 ? args[0].ToJsString() : "";
                if (BrowserRuntime.ScriptedWindowsAllowed && url.Length > 0)
                    canvas.ShowAlert(url);
                return JsValue.Undefined;
            }));
            w.Set("setActive", Fn(scope, "setActive", (self, args) => JsValue.Undefined));
        }

        // ── Netscape 4 event capture model (§11) ──
        if (BrowserRuntime.SupportsNetscapeLegacy && state != null)
        {
            var evtConstants = new JsObject { Class = "Event" };
            evtConstants.Set("MOUSEDOWN", JsValue.From(0x00000001));
            evtConstants.Set("MOUSEUP", JsValue.From(0x00000002));
            evtConstants.Set("CLICK", JsValue.From(0x00000004));
            evtConstants.Set("DBLCLICK", JsValue.From(0x00000008));
            evtConstants.Set("MOUSEMOVE", JsValue.From(0x00000010));
            evtConstants.Set("MOUSEOVER", JsValue.From(0x00000020));
            evtConstants.Set("MOUSEOUT", JsValue.From(0x00000040));
            evtConstants.Set("KEYPRESS", JsValue.From(0x00000080));
            evtConstants.Set("KEYDOWN", JsValue.From(0x00000100));
            evtConstants.Set("KEYUP", JsValue.From(0x00000200));
            evtConstants.Set("FOCUS", JsValue.From(0x00000400));
            evtConstants.Set("BLUR", JsValue.From(0x00000800));
            evtConstants.Set("SELECT", JsValue.From(0x00001000));
            evtConstants.Set("CHANGE", JsValue.From(0x00002000));
            evtConstants.Set("SUBMIT", JsValue.From(0x00004000));
            evtConstants.Set("RESET", JsValue.From(0x00008000));
            evtConstants.Set("LOAD", JsValue.From(0x00010000));
            evtConstants.Set("UNLOAD", JsValue.From(0x00020000));
            // modifier masks — present, all zero at runtime (modifier state
            // is not tracked; e.modifiers reads 0)
            evtConstants.Set("ALT_MASK", JsValue.From(0x00000001));
            evtConstants.Set("CONTROL_MASK", JsValue.From(0x00000002));
            evtConstants.Set("SHIFT_MASK", JsValue.From(0x00000004));
            evtConstants.Set("META_MASK", JsValue.From(0x00000008));
            w.Set("Event", JsValue.FromObject(evtConstants));

            w.Set("captureEvents", Fn(scope, "captureEvents", (self, args) =>
            {
                int mask = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                state.Interpreter?.CaptureEvents(mask);
                return JsValue.Undefined;
            }));
            w.Set("releaseEvents", Fn(scope, "releaseEvents", (self, args) =>
            {
                int mask = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                state.Interpreter?.ReleaseEvents(mask);
                return JsValue.Undefined;
            }));
            w.Set("routeEvent", Fn(scope, "routeEvent", (self, args) =>
            {
                // Era-plausible re-dispatch: send the event object back to
                // its own target's handlers and return the event.
                if (args.Length > 0 && args[0].Type == JsType.Object)
                {
                    var evt = args[0].GetObject();
                    var target = evt.Get("target");
                    if (target is { Type: JsType.Object } t &&
                        t.GetObjectOrFunction() is ElementWrapper wrapper)
                        state.Interpreter?.FireEvent(wrapper.Element,
                            evt.Get("type").ToJsString(), evt);
                }
                return args.Length > 0 ? args[0] : JsValue.Undefined;
            }));
            var handleEventFn = Fn(scope, "handleEvent", (self, args) =>
            {
                // window.handleEvent(evt) is accepted and behaves like
                // routeEvent: re-dispatch to the event's target.
                if (args.Length > 0 && args[0].Type == JsType.Object)
                {
                    var evt = args[0].GetObject();
                    var target = evt.Get("target");
                    if (target is { Type: JsType.Object } t &&
                        t.GetObjectOrFunction() is ElementWrapper wrapper)
                        state.Interpreter?.FireEvent(wrapper.Element,
                            evt.Get("type").ToJsString(), evt);
                }
                return JsValue.Undefined;
            });
            // Marker so the capture model can tell this BUILTIN apart from a
            // script-assigned window.handleEvent (the capture path calls the
            // script's own handleEvent; the builtin redispatch would loop).
            handleEventFn.GetFunction().Set("__dom_builtin__", JsValue.From(true));
            w.Set("handleEvent", handleEventFn);

            // window.pageXOffset/pageYOffset — read-only 0 (the shell's live
            // scroll offset is not reachable from the bindings; documented).
            w.Set("pageXOffset", JsValue.From(0));
            w.Set("pageYOffset", JsValue.From(0));
        }

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

        // The Preferences > Compatibility choice is a real scripting profile,
        // not merely a User-Agent label.  IE3 exposes the JScript personality
        // marker while Netscape 3 keeps the Navigator profile.
        w.Set("scriptEngine", JsValue.From(BrowserRuntime.JavaScriptEngineName));
        w.Set("scriptVersion", JsValue.From(BrowserRuntime.JavaScriptVersion));

        // JScript-era global engine probes.  These are deliberately exposed
        // only through the IE-compatible host personality; Navigator did not
        // provide the Microsoft ScriptEngine* globals.  IE3 reports the
        // JScript 1.0 numbers; the 1999 IE5 persona (and the Retro96 union)
        // report JScript 5.0 build 6325 (era-plausible for the March 1999
        // IE5.0 vbscript/jscript binaries).
        if (BrowserRuntime.SupportsInternetExplorerLegacy)
        {
            bool modern = !BrowserRuntime.IsInternetExplorer3;
            w.Set("ScriptEngine", Fn(scope, "ScriptEngine", (self, args) =>
                JsValue.From("JScript")));
            w.Set("ScriptEngineMajorVersion", Fn(scope, "ScriptEngineMajorVersion", (self, args) =>
                JsValue.From(modern ? 5 : 1)));
            w.Set("ScriptEngineMinorVersion", Fn(scope, "ScriptEngineMinorVersion", (self, args) =>
                JsValue.From(0)));
            w.Set("ScriptEngineBuildVersion", Fn(scope, "ScriptEngineBuildVersion", (self, args) =>
                JsValue.From(modern ? 6325 : 0)));
        }

        scope.Define("window", JsValue.FromObject(w));
    }

    // ─────────────────────────────────────────────────────────────────────
    // navigator
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterNavigator(JsScope scope)
    {
        var prefs = BrowserRuntime.Settings;
        var n = new JsObject();

        // Compatibility profiles affect both the advertised personality and
        // the exposed DOM. Retro96 is the engine's native compatibility union:
        // it keeps its own identity while exposing both IE- and Navigator-era
        // surfaces instead of pretending to be only one browser.
        if (prefs.EngineMode == RetroEngineMode.InternetExplorer5)
        {
            // IE5, March 1999: JScript 5.0, ES3-era.
            n.Set("appName", JsValue.From("Microsoft Internet Explorer"));
            n.Set("appVersion", JsValue.From("5.0 (Windows 98; Win32)"));
            n.Set("appCodeName", JsValue.From("Mozilla"));
            n.Set("appMinorVersion", JsValue.From("0"));
        }
        else if (prefs.EngineMode == RetroEngineMode.Netscape47)
        {
            // Navigator 4.7, August 1999: JavaScript 1.3 (ECMA-262 v1/v2).
            n.Set("appName", JsValue.From("Netscape"));
            n.Set("appVersion", JsValue.From("4.7 (Win98; I)"));
            n.Set("appCodeName", JsValue.From("Mozilla"));
        }
        else if (prefs.EngineMode == RetroEngineMode.InternetExplorer3)
        {
            n.Set("appName", JsValue.From("Microsoft Internet Explorer"));
            n.Set("appVersion", JsValue.From("3.02 (Windows 95)"));
            n.Set("appCodeName", JsValue.From("Mozilla"));
        }
        else if (prefs.EngineMode == RetroEngineMode.Netscape3)
        {
            n.Set("appName", JsValue.From("Netscape"));
            n.Set("appVersion", JsValue.From("3.01 (Win95; I)"));
            n.Set("appCodeName", JsValue.From("Mozilla"));
        }
        else
        {
            n.Set("appName", JsValue.From("Retro96"));
            n.Set("appVersion", JsValue.From("2.0 (Windows 98; IE5+NN4.7 compatibility)"));
            n.Set("appCodeName", JsValue.From("Mozilla"));
        }

        n.Set("userAgent", JsValue.From(prefs.EffectiveUserAgent));
        n.Set("language", JsValue.From("en"));
        n.Set("platform", JsValue.From("Win32"));
        n.Set("cookieEnabled", JsValue.From(BrowserRuntime.CookiesEnabled));
        // navigator.javaEnabled() — the 1999 host surface (IE5: true with the
        // MS VM installed; NS4.7: true with the bundled JVM). Always false
        // when applets are disabled in Preferences or High trust mode.
        n.Set("javaEnabled", JsValue.FromFunction(new JsFunction(
            (self, args) => JsValue.From(BrowserRuntime.JavaAppletsEnabled),
            scope, "javaEnabled")));

        // navigator.plugins[] / mimeTypes[] — era-flavoured static tables
        // (checklist §12). The 1996 personas (IE3/NS3) keep the empty lists
        // their browsers really had; the 1999 personas ship plausible
        // installs of the year's common plugins. plugins.refresh() is a
        // no-op (the tables are static by design).
        var pluginArray = BuildNavigatorPlugins(scope);
        n.Set("plugins", JsValue.FromObject(pluginArray));
        n.Set("mimeTypes", JsValue.FromObject(BuildNavigatorMimeTypes(scope, pluginArray)));

        scope.Define("navigator", JsValue.FromObject(n));
    }

    /// <summary>A (name, filename, description, mimeTypes) plugin row.</summary>
    private sealed record NavigatorPlugin(
        string Name, string Filename, string Description, string[] MimeTypes);

    /// <summary>
    /// The 1999 plugin tables per persona. IE5's "plugins" were ActiveX
    /// controls surfaced through navigator.plugins; NS4.7's list is the
    /// classic Navigator plugin scan of its plugins directory.
    /// </summary>
    private static readonly NavigatorPlugin[] Ie5Plugins =
    {
        new("Shockwave Flash", "SWFLASH.OCX",
            "Macromedia Shockwave Flash 4.0 r10", new[] { "application/x-shockwave-flash" }),
        new("Acrobat Control for ActiveX", "PDF.PDF.1",
            "Adobe Acrobat Control for ActiveX 4.05", new[] { "application/pdf" }),
        new("NetShow Player Control", "NSPLAY.OCX",
            "Microsoft NetShow Player 3.0", new[] { "video/x-ms-asf" }),
        new("Windows Media Player Control", "WMP.OCX",
            "Windows Media Player 6.4", new[] { "video/x-msvideo", "audio/mpeg" }),
    };

    private static readonly NavigatorPlugin[] Ns47Plugins =
    {
        new("Shockwave Flash", "NPSWF32.DLL",
            "Shockwave Flash 4.0 r10", new[] { "application/x-shockwave-flash", "application/futuresplash" }),
        new("Netscape Default Plug-in", "NPNUL32.DLL",
            "Default Plug-in", new[] { "*" }),
        new("Acrobat Plug-in", "NPPDF32.DLL",
            "Adobe Acrobat 4.005", new[] { "application/pdf" }),
        new("LiveAudio", "NPAUDIO.DLL",
            "LiveAudio; Netscape sound player 3.0", new[] { "audio/basic", "audio/wav", "audio/x-aiff" }),
        new("QuickTime Plug-in", "NPQTPLUGIN.DLL",
            "QuickTime 4.1.2 Plug-in", new[] { "video/quicktime", "image/x-quicktime" }),
        new("RealPlayer\u2122 G2 LiveConnect Plug-In", "NPRLZIP.DLL",
            "RealPlayer G2 6.0", new[] { "audio/x-pn-realaudio", "audio/x-pn-realaudio-plugin" }),
    };

    /// <summary>Shared mimeType sample table (the types pages actually probed
    /// in 1999 sniffers) used for navigator.mimeTypes.</summary>
    private static readonly (string Type, string Suffixes, string Description, string PluginName)[] EraMimeTypes =
    {
        ("application/x-shockwave-flash", "swf", "Shockwave Flash", "Shockwave Flash"),
        ("application/pdf", "pdf", "Acrobat", "Acrobat Plug-in"),
        ("audio/x-pn-realaudio", "ra,ram,rm", "RealAudio", "RealPlayer\u2122 G2 LiveConnect Plug-In"),
        ("video/quicktime", "mov,qt", "QuickTime video", "QuickTime Plug-in"),
    };

    /// <summary>Builds navigator.plugins with per-plugin mimeType entries
    /// (each plugin object carries name/filename/description/length plus its
    /// mimeType rows) and seeds navigator.mimeTypes with the sample table.</summary>
    private static JsObject BuildNavigatorPlugins(JsScope scope)
    {
        var plugins = new JsObject { Class = "PluginArray", Prototype = JsInterpreter.ArrayPrototype };

        NavigatorPlugin[]? table = BrowserRuntime.Settings.EngineMode switch
        {
            RetroEngineMode.InternetExplorer5 => Ie5Plugins,
            RetroEngineMode.Netscape47 => Ns47Plugins,
            RetroEngineMode.Retro96 => Ns47Plugins, // native union keeps the richer table
            _ => null
        };

        if (table == null || table.Length == 0)
        {
            plugins.Set("length", JsValue.From(0));
            plugins.Set("refresh", Fn(scope, "refresh", (self, args) => JsValue.Undefined));
            return plugins;
        }

        for (int i = 0; i < table.Length; i++)
        {
            var row = table[i];
            var plugin = new JsObject { Class = "Plugin" };
            plugin.Set("name", JsValue.From(row.Name));
            plugin.Set("filename", JsValue.From(row.Filename));
            plugin.Set("description", JsValue.From(row.Description));

            var mimes = NewArray(scope);
            int mi = 0;
            foreach (var type in row.MimeTypes)
            {
                var mime = new JsObject { Class = "MimeType" };
                mime.Set("type", JsValue.From(type));
                mime.Set("suffixes", JsValue.From(SuffixesFor(type)));
                mime.Set("description", JsValue.From(row.Description));
                mime.Set("enabledPlugin", JsValue.FromObject(plugin));
                mimes.Set(mi.ToString(), JsValue.FromObject(mime));
                mi++;
            }
            mimes.Set("length", JsValue.From(mi));
            plugin.Set("mimeTypes", JsValue.FromObject(mimes));
            plugin.Set("length", JsValue.From(mi));

            plugins.Set(i.ToString(), JsValue.FromObject(plugin));
            if (!plugins.HasOwn(row.Name))
                plugins.Set(row.Name, JsValue.FromObject(plugin));
        }
        plugins.Set("length", JsValue.From(table.Length));
        plugins.Set("refresh", Fn(scope, "refresh", (self, args) =>
            JsValue.Undefined));

        return plugins;
    }

    private static string SuffixesFor(string mime) => mime switch
    {
        "application/x-shockwave-flash" => "swf",
        "application/futuresplash" => "spl",
        "application/pdf" => "pdf",
        "audio/basic" => "au,snd",
        "audio/wav" => "wav",
        "audio/x-aiff" => "aiff,aifc",
        "video/quicktime" => "mov,qt",
        "image/x-quicktime" => "mov,qt",
        "audio/x-pn-realaudio" => "ra,ram,rm",
        "audio/x-pn-realaudio-plugin" => "rpm",
        "video/x-ms-asf" => "asf,asx",
        "video/x-msvideo" => "avi",
        "audio/mpeg" => "mp3,mp2",
        "*" => "*",
        _ => ""
    };

    /// <summary>Seeds navigator.mimeTypes with the era sample entries
    /// (a flat MimeTypeArray whose enabledPlugin back-references the matching
    /// plugin object, when one exists).</summary>
    private static JsObject BuildNavigatorMimeTypes(JsScope scope, JsObject plugins)
    {
        var mimes = new JsObject { Class = "MimeTypeArray", Prototype = JsInterpreter.ArrayPrototype };
        int i = 0;
        foreach (var (type, suffixes, description, pluginName) in EraMimeTypes)
        {
            var entry = new JsObject { Class = "MimeType" };
            entry.Set("type", JsValue.From(type));
            entry.Set("suffixes", JsValue.From(suffixes));
            entry.Set("description", JsValue.From(description));
            var plugin = plugins.Get(pluginName);
            if (plugin.Type == JsType.Object)
                entry.Set("enabledPlugin", plugin);
            mimes.Set(i.ToString(), JsValue.FromObject(entry));
            mimes.Set(type, JsValue.FromObject(entry));
            i++;
        }
        mimes.Set("length", JsValue.From(i));
        return mimes;
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
        var d = state.DocumentObject as DocumentObject;
        if (d == null)
        {
            d = new DocumentObject(doc, canvas, state);
            state.DocumentObject = d;
        }
        else
        {
            d.Rebind(doc);
        }

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
        d.Set("embeds", JsValue.FromObject(BuildElementCollection(scope,
            doc.ElementDescendants().Where(e => e.TagName.Equals("embed", StringComparison.OrdinalIgnoreCase)), state)));

        // IE4-era selection host.  The range is live against the browser
        // selection state rather than being a detached fake object.
        if (BrowserRuntime.SupportsInternetExplorerLegacy)
            d.Set("selection", JsValue.FromObject(
                new LegacySelectionObject(canvas, scope, state)));

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

        // getElementById is a DOM Level 1 API (Oct 1998). IE3 mode and the
        // Navigator 4.x personas deliberately do not expose it — IE3 pages
        // use document.all, NS4.x pages use document.layers / document.ids.
        if (BrowserRuntime.SupportsGetElementById)
            d.Set("getElementById", Fn(scope, "getElementById", (self, args) =>
        {
            string id = args.Length > 0 ? args[0].ToJsString().Trim() : "";
            if (id.Length == 0) return JsValue.Null;

            // Always search the page's current DOM. The parser calls
            // RegisterAll before each inline script; capturing the callback's
            // document here made this binding fragile when the live document
            // object was replaced/rebound during streaming or document.write.
            var liveDoc = state.Document ?? doc;
            var el = liveDoc.ElementDescendants()
                .FirstOrDefault(e => string.Equals(
                    e.GetAttr("id"), id, StringComparison.Ordinal));
            // Legacy pages occasionally vary ID casing; keep the exact HTML
            // match first, then provide the forgiving fallback older UAs used.
            el ??= liveDoc.ElementDescendants()
                .FirstOrDefault(e => string.Equals(
                    e.GetAttr("id"), id, StringComparison.OrdinalIgnoreCase));

            return el == null ? JsValue.Null
                : JsValue.FromObject(WrapElement(el, state));
        }));

        // createElement was not part of the IE3 DOM surface, and Navigator 4
        // exposed no DOM1 factory either.
        if (BrowserRuntime.SupportsGetElementById)
            d.Set("createElement", Fn(scope, "createElement", (self, args) =>
        {
            string tagName = args.Length > 0 ? args[0].ToJsString() : "";
            if (string.IsNullOrWhiteSpace(tagName)) return JsValue.Null;

            var element = new DomElement(tagName.Trim());
            return JsValue.FromObject(WrapElement(element, state));
        }));

        // createTextNode (DOM Level 1, checklist §10). The node is detached
        // until appendChild/insertBefore graft it; nodeValue stays writable.
        if (BrowserRuntime.SupportsGetElementById)
            d.Set("createTextNode", Fn(scope, "createTextNode", (self, args) =>
        {
            string data = args.Length > 0 ? args[0].ToJsString() : "";
            return JsValue.FromObject(new TextNodeObject(new DomText(data), canvas));
        }));

        // getElementsByTagName is a DOM Level 1 API; hidden in the IE3 and
        // Navigator 4.x personas.
        if (BrowserRuntime.SupportsGetElementById)
            d.Set("getElementsByTagName", Fn(scope, "getElementsByTagName", (self, args) =>
        {
            string tagName = args.Length > 0 ? args[0].ToJsString() : "";
            if (string.IsNullOrEmpty(tagName)) return JsValue.FromObject(NewArray(scope));

            // Consult the live document, not the registration-time snapshot —
            // post-parse document.write replaces the DOM underneath.
            var liveDoc = state.Document ?? doc;
            IEnumerable<DomElement> matches = tagName == "*"
                ? liveDoc.ElementDescendants()
                : liveDoc.ElementDescendants().Where(e => string.Equals(
                    e.TagName, tagName, StringComparison.OrdinalIgnoreCase));

            return JsValue.FromObject(BuildElementCollection(scope, matches, state));
        }));

        // getElementsByName is a later DOM API, so hide it in strict IE3 mode.
        if (!BrowserRuntime.IsInternetExplorer3)
            d.Set("getElementsByName", Fn(scope, "getElementsByName", (self, args) =>
        {
            string nm = args.Length > 0 ? args[0].ToJsString() : "";
            var liveDoc = state.Document ?? doc;
            var arr = NewArray(scope);
            int i = 0;
            foreach (var e in liveDoc.ElementDescendants()
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
        // Chain to Array.prototype so host-built arrays and collections
        // actually expose join/push/etc.; they used to be prototype-less
        // and silently lost every Array method.
        var arr = new JsObject { Class = "Array", Prototype = JsInterpreter.ArrayPrototype };
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
        var collection = new JsObject
        {
            Class = "Array",
            Prototype = JsInterpreter.ArrayPrototype
        };
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

    /// <summary>IE5 uniqueID (§10): a stable "ms__idN" string per element,
    /// allocated per page from the bindings state.</summary>
    internal static string GetOrCreateUniqueId(DomElement element, DocumentBindingsState state)
    {
        if (state.UniqueIds.TryGetValue(element, out var existing)) return existing;
        state.NextUniqueId++;
        var id = $"ms__id{state.NextUniqueId}";
        state.UniqueIds[element] = id;
        return id;
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
        private DomDocument _doc;
        private readonly BrowserCanvas _canvas;
        private readonly DocumentBindingsState? _state;

        public DocumentObject(DomDocument doc, BrowserCanvas canvas,
                              DocumentBindingsState? state = null)
        {
            _doc = doc;
            _canvas = canvas;
            _state = state;
        }

        public void Rebind(DomDocument doc) => _doc = doc;

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
                    {
                        var target = Body;
                        if (target == null || _state == null) return JsValue.Null;
                        return JsValue.FromObject(WrapElement(target, _state));
                    }
                case "documentElement" when !BrowserRuntime.IsInternetExplorer3:
                    {
                        var target = _doc.ElementDescendants().FirstOrDefault(e => e.TagName == "html");
                        if (target == null || _state == null) return JsValue.Null;
                        return JsValue.FromObject(WrapElement(target, _state));
                    }
                case "cookie":
                    if (!BrowserRuntime.CookiesEnabled) return JsValue.From("");
                    try { return JsValue.From(_doc.Cookies.Get(BaseUrlOrBlank)); }
                    catch { return JsValue.From(""); }
                case "readyState":
                    // IE5: "loading" while the parser streams, "complete" once
                    // the parse finished (the post-parse RegisterAll is the
                    // transition, firing onreadystatechange exactly once).
                    return JsValue.From(_doc.ParseComplete ? "complete" : "loading");
                case "all" when BrowserRuntime.SupportsInternetExplorerLegacy:
                    if (_state == null) return JsValue.Undefined;
                    // One stable collection object per document so
                    // document.all === document.all holds.
                    if (!Properties.TryGetValue("all", out var cachedAll))
                    {
                        cachedAll = JsValue.FromFunction(CreateLegacyElementCollection(
                            _state, () => _doc.ElementDescendants(),
                            "all", supportsTags: true));
                        Properties["all"] = cachedAll;
                    }
                    return cachedAll;
                case "layers" when BrowserRuntime.SupportsDocumentLayers:
                    // NS4 layer collection — LIVE: positioned elements (CSS
                    // position:absolute/relative via inline or resolved style)
                    // plus <layer>/<ilayer>, indexable by number AND name
                    // (id/name attribute). One stable collection object so
                    // document.layers === document.layers holds.
                    if (_state == null) return JsValue.Undefined;
                    if (!Properties.TryGetValue("layers", out var layers))
                    {
                        layers = JsValue.FromObject(new LayerCollectionObject(
                            _doc, _state, _state.Interpreter?.GlobalScope ?? new JsScope()));
                        Properties["layers"] = layers;
                    }
                    return layers;
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
                        var applet = _doc.ElementDescendants()
                            .FirstOrDefault(e => Retro96.Engine.Java.JavaAppletHost.IsJavaElement(e) &&
                                (string.Equals(e.GetAttr("name"), name, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(e.GetAttr("id"), name, StringComparison.OrdinalIgnoreCase)));
                        if (applet != null)
                            return JsValue.FromObject(WrapElement(applet, _state));

                        if (BrowserRuntime.SupportsInternetExplorerLegacy)
                        {
                            foreach (var element in _doc.ElementDescendants())
                            {
                                if (string.Equals(element.GetAttr("id"), name, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(element.GetAttr("name"), name, StringComparison.OrdinalIgnoreCase))
                                    return JsValue.FromObject(WrapElement(element, _state));
                            }
                        }

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
                    if (!BrowserRuntime.CookiesEnabled) return;
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

    /// <summary>Host bridge for legacy &lt;embed&gt; script objects.</summary>
    private sealed class EmbeddedScriptObject : JsObject
    {
        private readonly DomElement _element;
        private readonly DocumentBindingsState _state;
        private readonly IReadOnlySet<string> _methods;

        public EmbeddedScriptObject(DomElement element, DocumentBindingsState state, IReadOnlyList<string> methods)
        { _element = element; _state = state; _methods = methods.ToHashSet(StringComparer.Ordinal); Class = "EmbeddedPlugin"; }

        public override JsValue Get(string name)
        {
            if ((_methods.Count != 0 && !_methods.Contains(name)) ||
                _state.EmbeddedScriptCall == null ||
                _state.Interpreter == null || _state.Canvas == null)
                return base.Get(name);
            return JsValue.FromFunction(new JsFunction((self, args) =>
            {
                try
                {
                    var pluginArgs = args.Select(PluginJsValueCodec.FromEngine).ToArray();
                    var task = _state.EmbeddedScriptCall(_element, name, pluginArgs);
                    return JsValue.FromObject(new JsPromiseObject(_state.Interpreter!, _state.Canvas!, task));
                }
                catch (Exception ex)
                {
                    return JsValue.FromObject(JsPromiseObject.FromFault(_state.Interpreter!, _state.Canvas!, ex));
                }
            }, _state.Interpreter?.GlobalScope ?? new JsScope(), name));
        }
    }

    /// <summary>Small DOM-0 promise facade. It deliberately exposes only then/catch; all work is still brokered asynchronously.</summary>
    private sealed class JsPromiseObject : JsObject
    {
        private readonly JsInterpreter _interpreter;
        private readonly BrowserCanvas _canvas;
        private readonly Task<Retro96.Plugins.JsValue> _task;

        public JsPromiseObject(JsInterpreter interpreter, BrowserCanvas canvas, Task<Retro96.Plugins.JsValue> task)
        { _interpreter=interpreter; _canvas=canvas; _task=task; Class="Promise"; }

        public static JsPromiseObject FromFault(JsInterpreter interpreter, BrowserCanvas canvas, Exception ex) =>
            new(interpreter, canvas, Task.FromException<Retro96.Plugins.JsValue>(ex));

        public override JsValue Get(string name)
        {
            if (name is not ("then" or "catch")) return base.Get(name);
            return JsValue.FromFunction(new JsFunction((self, args) =>
            {
                JsFunction? Callback = args.Length > 0 && args[0].Type == JsType.Function ? args[0].GetFunction() : null;
                if (Callback != null)
                {
                    _ = _task.ContinueWith(_ =>
                    {
                        try
                        {
                            var engineValue = PluginJsValueCodec.ToEngine(_task.GetAwaiter().GetResult());
                            if (_canvas.IsHandleCreated && !_canvas.IsDisposed)
                                _canvas.BeginInvoke((Action)(() => _interpreter.CallFunction(Callback, JsValue.Undefined, new[] { engineValue })));
                        }
                        catch (Exception ex)
                        {
                            if (name == "catch" && _canvas.IsHandleCreated && !_canvas.IsDisposed)
                                _canvas.BeginInvoke((Action)(() => _interpreter.CallFunction(Callback, JsValue.Undefined, new[] { JsValue.From(ex.Message) })));
                        }
                    }, TaskScheduler.Default);
                }
                return self;
            }, _interpreter.GlobalScope, name));
        }
    }

    /// <summary>
    /// A DOM text node wrapper (DOM Level 1, §10): nodeType 3, live
    /// nodeValue/data reads and writes (a write mutates the underlying
    /// DomText and repaints), nodeNames "#text" / "#comment". Detached nodes
    /// (document.createTextNode) work the same way until they are grafted
    /// with appendChild/insertBefore.
    /// </summary>
    private sealed class TextNodeObject : JsObject
    {
        private readonly DomText? _node;
        private readonly BrowserCanvas? _canvas;
        private readonly string _staticData;
        private readonly int _nodeType;

        public TextNodeObject(DomText node, BrowserCanvas? canvas)
        {
            _node = node;
            _canvas = canvas;
            _staticData = node.Data;
            _nodeType = 3;
            Class = "Text";
        }

        public TextNodeObject(string data, int nodeType = 3)
        {
            // Comment / other fallback (old shape): a static text carrier.
            _node = null;
            _canvas = null;
            _staticData = data;
            _nodeType = nodeType;
            Class = nodeType == 8 ? "Comment" : "Text";
        }

        private string Data => _node?.Data ?? _staticData;

        /// <summary>The wrapped DomText (null for the static comment
        /// fallback) — used by DOM mutation methods to graft created nodes.</summary>
        internal DomNode? UnderlyingNode => _node;

        public override JsValue Get(string name)
        {
            switch (name)
            {
                case "nodeType": return JsValue.From(_nodeType);
                case "nodeName": return JsValue.From(_nodeType == 8 ? "#comment" : "#text");
                case "nodeValue":
                case "data": return JsValue.From(Data);
                case "length": return JsValue.From(Data.Length);
            }
            return base.Get(name);
        }

        public override void Set(string name, JsValue value)
        {
            if (name is "nodeValue" or "data")
            {
                if (_node != null)
                {
                    _node.Data = value.ToJsString();
                    _canvas?.RequestRerender();
                }
                return;   // static/comment fallback nodes are read-only
            }
            base.Set(name, value);
        }
    }

    private sealed class LegacySelectionObject : JsObject
    {
        private readonly BrowserCanvas _canvas;
        private readonly JsScope _scope;
        private readonly DocumentBindingsState _state;

        public LegacySelectionObject(BrowserCanvas canvas, JsScope scope, DocumentBindingsState state)
        {
            _canvas = canvas;
            _scope = scope;
            _state = state;
            Class = "IHTMLSelectionObject";
        }

        public override JsValue Get(string name)
        {
            if (name.Equals("createRange", StringComparison.OrdinalIgnoreCase))
            {
                return JsValue.FromFunction(new JsFunction((self, args) =>
                    JsValue.FromObject(new LegacyTextRangeObject(
                        null, _canvas, _scope, _state, useSelection: true)),
                    _scope, "createRange"));
            }
            if (name.Equals("empty", StringComparison.OrdinalIgnoreCase))
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    _canvas.ClearPageSelection();
                    return JsValue.Undefined;
                }, _scope, "empty"));
            return base.Get(name);
        }
    }

    private sealed class LegacyTextRangeObject : JsObject
    {
        private DomElement? _root;
        private readonly BrowserCanvas? _canvas;
        private readonly JsScope _scope;
        private readonly DocumentBindingsState? _state;
        private readonly bool _useSelection;

        public LegacyTextRangeObject(DomElement? root, BrowserCanvas? canvas, JsScope scope,
                                     DocumentBindingsState? state, bool useSelection)
        {
            _root = root;
            _canvas = canvas;
            _scope = scope;
            _state = state;
            _useSelection = useSelection;
            Class = "TextRange";
        }

        public override JsValue Get(string name)
        {
            if (name.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                string text = _useSelection
                    ? _canvas!.GetDomSelectionText()
                    : _root?.InnerText ?? "";
                return JsValue.From(text);
            }

            if (name.Equals("parentElement", StringComparison.OrdinalIgnoreCase))
                return _root == null || _state == null ? JsValue.Null
                    : JsValue.FromObject(WrapElement(_root, _state));

            if (name.Equals("moveToElementText", StringComparison.OrdinalIgnoreCase))
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length > 0 && args[0].Type == JsType.Object &&
                        args[0].GetObjectOrFunction() is ElementWrapper wrapper)
                        _root = wrapper.Element;
                    return JsValue.Undefined;
                }, _scope, "moveToElementText"));

            if (name.Equals("duplicate", StringComparison.OrdinalIgnoreCase))
                return JsValue.FromFunction(new JsFunction((self, args) =>
                    JsValue.FromObject(new LegacyTextRangeObject(
                        _root, _canvas, _scope, _state, _useSelection)),
                    _scope, "duplicate"));

            if (name.Equals("collapse", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("select", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("setEndPoint", StringComparison.OrdinalIgnoreCase))
            {
                return JsValue.FromFunction(new JsFunction((self, args) =>
                    JsValue.Undefined, _scope, name));
            }

            return base.Get(name);
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
    /// Callable IE-style document.all / element.all collection. The old DOM
    /// exposed these collections as host objects that were simultaneously
    /// callable (`all(0)`, `all("id")`) and property-addressable
    /// (`all[0]`, `all.foo`). A plain JsObject/Array cannot be called by the
    /// interpreter, which was the root cause of the "all is not a function"
    /// failures.
    /// </summary>
    private sealed class LegacyDomCollection : JsFunction
    {
        private readonly Func<IEnumerable<DomElement>> _source;
        private readonly DocumentBindingsState _state;
        private readonly JsScope _scope;
        private readonly bool _supportsTags;

        public LegacyDomCollection(DocumentBindingsState state, JsScope scope,
                                   Func<IEnumerable<DomElement>> source,
                                   string name, bool supportsTags)
            : base((self, args) => self.GetObjectOrFunction() is LegacyDomCollection c
                ? c.ResolveCall(args)
                : JsValue.Undefined, scope, name, useFunctionObjectAsThis: true)
        {
            _state = state;
            _scope = scope;
            _source = source;
            _supportsTags = supportsTags;
            Class = "HTMLCollection";

            Set("item", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                return self.GetObjectOrFunction() is LegacyDomCollection c
                    ? c.ResolveItem(args)
                    : JsValue.Undefined;
            }, _scope, "item")));

            Set("namedItem", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                return self.GetObjectOrFunction() is LegacyDomCollection c
                    ? c.ResolveNamedItem(args)
                    : JsValue.Undefined;
            }, _scope, "namedItem")));

            if (_supportsTags)
            {
                Set("tags", JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (self.GetObjectOrFunction() is not LegacyDomCollection c)
                        return JsValue.Undefined;
                    string tag = args.Length > 0 ? args[0].ToJsString().Trim() : "";
                    if (tag.Length == 0)
                        return CreateLegacyElementCollection(c._state, () => Array.Empty<DomElement>(),
                            "tags", supportsTags: false).AsValue();

                    return CreateLegacyElementCollection(c._state,
                        () => c._source().Where(e => string.Equals(
                            e.TagName, tag, StringComparison.OrdinalIgnoreCase)),
                        "tags", supportsTags: false).AsValue();
                }, _scope, "tags")));
            }
        }

        public JsValue AsValue() => JsValue.FromFunction(this);

        private List<DomElement> Snapshot() => _source().Where(e => e != null).ToList();

        private DomElement? ResolveIndex(int index)
        {
            if (index < 0) return null;
            var list = Snapshot();
            return index < list.Count ? list[index] : null;
        }

        private IEnumerable<DomElement> ResolveNamed(string key)
        {
            if (string.IsNullOrEmpty(key))
                yield break;

            foreach (var element in Snapshot())
            {
                string? id = element.GetAttr("id");
                string? elementName = element.GetAttr("name");
                if (string.Equals(id, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(elementName, key, StringComparison.OrdinalIgnoreCase))
                    yield return element;
            }
        }

        private JsValue Wrap(DomElement element) =>
            JsValue.FromObject(DomBindings.WrapElement(element, _state));

        private JsValue ResolveItem(JsValue[] args)
        {
            if (args.Length == 0) return JsValue.Undefined;

            if (args[0].Type == JsType.Number)
            {
                int index = (int)args[0].ToNumber();
                var indexed = ResolveIndex(index);
                return indexed == null ? JsValue.Undefined : Wrap(indexed);
            }

            string key = args[0].ToJsString();
            if (int.TryParse(key, out int numericIndex))
            {
                var indexed = ResolveIndex(numericIndex);
                return indexed == null ? JsValue.Undefined : Wrap(indexed);
            }

            var matches = ResolveNamed(key).ToList();
            if (matches.Count == 0) return JsValue.Undefined;

            if (args.Length > 1)
            {
                int duplicateIndex = (int)args[1].ToNumber();
                return duplicateIndex >= 0 && duplicateIndex < matches.Count
                    ? Wrap(matches[duplicateIndex])
                    : JsValue.Undefined;
            }

            return Wrap(matches[0]);
        }

        private JsValue ResolveNamedItem(JsValue[] args)
        {
            if (args.Length == 0) return JsValue.Undefined;
            var match = ResolveNamed(args[0].ToJsString()).FirstOrDefault();
            return match == null ? JsValue.Undefined : Wrap(match);
        }

        private JsValue ResolveCall(JsValue[] args) => ResolveItem(args);

        public override JsValue Get(string name)
        {
            if (string.Equals(name, "length", StringComparison.Ordinal))
                return JsValue.From(Snapshot().Count);

            if (int.TryParse(name, out int index))
            {
                var indexed = ResolveIndex(index);
                return indexed == null ? JsValue.Undefined : Wrap(indexed);
            }

            if (name is "item" or "namedItem" or "tags")
                return base.Get(name);

            var named = ResolveNamed(name).FirstOrDefault();
            return named == null ? base.Get(name) : Wrap(named);
        }
    }

    private static LegacyDomCollection CreateLegacyElementCollection(
        DocumentBindingsState state, Func<IEnumerable<DomElement>> source,
        string name, bool supportsTags)
    {
        var scope = state.Interpreter?.GlobalScope ?? new JsScope();
        return new LegacyDomCollection(state, scope, source, name, supportsTags);
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

            // Keep the legacy DOM methods as own properties as well as virtual
            // Get() members. This matters for JS code that first probes the
            // member (`if (el.setAttribute)`) before calling it. Some old DOM
            // code paths only inspect the wrapper's own property map.
            Properties["setAttribute"] = MakeSetAttributeFunction();
            Properties["getAttribute"] = MakeGetAttributeFunction();
        }

        public DomElement Element => _element;

        // Properties stored on the element itself (attribute name → storage key)
        private static readonly HashSet<string> RoutedAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            "src", "href", "value", "checked", "name", "id", "target",
            "action", "method", "width", "height", "alt", "border",
            "align", "bgColor", "title", "maxlength", "size", "cols", "rows",
            "type", "color", "face"
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

        /// <summary>Wraps a DOM node: elements get the CACHED ElementWrapper
        /// (stable identity across accesses), text nodes get a live
        /// TextNodeObject, comments get {nodeType:8}.</summary>
        private JsObject WrapNode(DomNode node)
        {
            if (node is DomElement el)
            {
                if (_state != null) return WrapElement(el, _state);
                return new ElementWrapper(el, _canvas, _scope);
            }
            if (node is DomText tx)
                return new TextNodeObject(tx, _canvas);
            return new TextNodeObject("", nodeType: 8);   // comment / other
        }

        /// <summary>Unwraps a script node argument: ElementWrapper → its
        /// DomElement, TextNodeObject → its DomText (or null for the static
        /// comment fallback). Returns null for anything else.</summary>
        private static DomNode? UnwrapNodeArg(JsValue value)
        {
            if (value.Type != JsType.Object) return null;
            if (value.GetObject() is ElementWrapper elementWrapper)
                return elementWrapper.Element;
            if (value.GetObject() is TextNodeObject textObject)
                return textObject.UnderlyingNode;
            return null;
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
            if (VoidElements.Contains(e.TagName)) return;

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

        private JsValue MakeSetAttributeFunction() => JsValue.FromFunction(
            new JsFunction((self, args) =>
            {
                string attrName = args.Length > 0 ? args[0].ToJsString() : "";
                if (attrName.Length == 0) return JsValue.Undefined;

                string attrValue = args.Length > 1 ? args[1].ToJsString() : "";
                _element.SetAttr(attrName, attrValue);

                if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase) &&
                    attrName.Length > 2)
                {
                    string eventName = attrName.ToLowerInvariant();
                    _state?.Interpreter?.ClearDomEventProperty(_element, eventName);
                    _element.EventHandlers[eventName] = attrValue;
                }

                _canvas?.RequestRerender();
                return JsValue.Undefined;
            }, _scope, "setAttribute"));

        private JsValue MakeGetAttributeFunction() => JsValue.FromFunction(
            new JsFunction((self, args) =>
            {
                string attrName = args.Length > 0 ? args[0].ToJsString() : "";
                var value = _element.GetAttr(attrName);
                return value == null ? JsValue.Null : JsValue.From(value);
            }, _scope, "getAttribute"));

        private JsValue MakeInsertAdjacentHtmlFunction() => JsValue.FromFunction(
            new JsFunction((self, args) =>
            {
                if (!BrowserRuntime.SupportsInternetExplorerLegacy || args.Length < 2)
                    return JsValue.Undefined;

                string position = args[0].ToJsString().Trim().ToLowerInvariant();
                string markup = args[1].ToJsString();
                if (markup.Length == 0) return JsValue.Undefined;

                var doc = _element.OwnerDocument();
                if (doc == null) return JsValue.Undefined;

                var fragment = HtmlParser.Parse(markup,
                    doc.BaseUrl ?? ParsedUrl.Parse("about:blank"), doc.Cookies);
                var body = fragment.ElementDescendants()
                    .FirstOrDefault(e => e.TagName.Equals("body", StringComparison.OrdinalIgnoreCase));
                var nodes = (body?.Children ?? fragment.Children).ToList();

                if (nodes.Count == 0) return JsValue.Undefined;

                switch (position)
                {
                    case "beforebegin":
                        if (_element.Parent != null)
                            foreach (var node in nodes) _element.Parent.InsertBefore(node, _element);
                        break;
                    case "afterbegin":
                        {
                            // Keep fragment order stable. Re-reading FirstChild
                            // for each insertion reverses multi-node fragments.
                            var reference = _element.FirstChild;
                            foreach (var node in nodes)
                                _element.InsertBefore(node, reference);
                            break;
                        }
                    case "beforeend":
                        foreach (var node in nodes) _element.AppendChild(node);
                        break;
                    case "afterend":
                        if (_element.Parent != null)
                        {
                            var parent = _element.Parent;
                            var reference = _element.NextSibling;
                            foreach (var node in nodes) parent.InsertBefore(node, reference);
                        }
                        break;
                    default:
                        return JsValue.Undefined;
                }

                _canvas?.ReflowDocument();
                return JsValue.Undefined;
            }, _scope, "insertAdjacentHTML"));

        /// <summary>insertAdjacentText(position, text) — the IE4/5 text twin
        /// of insertAdjacentHTML: same four positions, inserts a DomText.</summary>
        private JsValue MakeInsertAdjacentTextFunction() => JsValue.FromFunction(
            new JsFunction((self, args) =>
            {
                if (args.Length < 2) return JsValue.Undefined;

                string position = args[0].ToJsString().Trim().ToLowerInvariant();
                var textNode = new DomText { Data = args[1].ToJsString() };

                switch (position)
                {
                    case "beforebegin":
                        _element.Parent?.InsertBefore(textNode, _element);
                        break;
                    case "afterbegin":
                        _element.InsertBefore(textNode, _element.FirstChild);
                        break;
                    case "beforeend":
                        _element.AppendChild(textNode);
                        break;
                    case "afterend":
                        _element.Parent?.InsertBefore(textNode, _element.NextSibling);
                        break;
                    default:
                        return JsValue.Undefined;
                }

                _canvas?.ReflowDocument();
                return JsValue.Undefined;
            }, _scope, "insertAdjacentText"));

        // ── IE5 geometry helpers (offset*/client*) ─────────────────────────
        //
        // The layout engine keeps geometry on LayoutBox (element → box via
        // LayoutBox.Element, or a future DomElement.Box); the resolved
        // ComputedStyle carries authored lengths. offset*/client* read the
        // box when one exists and fall back to the authored style — a page
        // that never laid out still reports its inline widths.

        private DomElement? OffsetParent()
        {
            for (var ancestor = _element.Parent as DomElement;
                 ancestor != null;
                 ancestor = ancestor.Parent as DomElement)
            {
                if (IsPositionedElement(ancestor) || ancestor.TagName == "body")
                    return ancestor;
            }
            // IE5 contract: an attached element's offsetParent is the BODY
            // when no positioned ancestor exists — null only for detached
            // or display:none elements.
            return _element.OwnerDocument()?.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        }

        private static bool IsPositionedElement(DomElement element)
        {
            if (element.TagName is "layer" or "ilayer") return true;
            var position = element.Style?.Position;
            if (position is Css.PositionValue.Relative or Css.PositionValue.Absolute
                or Css.PositionValue.Fixed)
                return true;
            // Inline STYLE= position (style resolution may not have run yet).
            var inlinePosition = element.GetAttr("style");
            if (inlinePosition != null)
            {
                foreach (var decl in Css.CssParser.ParseInlineStyle(inlinePosition))
                    if (string.Equals(decl.Property, "position", StringComparison.OrdinalIgnoreCase) &&
                        (decl.Value ?? "").Trim() is "absolute" or "relative" or "fixed")
                        return true;
            }
            return false;
        }

        private (double Left, double Top, double Width, double Height) OffsetMetrics()
        {
            var box = _element.Box;
            var parentBox = OffsetParent()?.Box;
            if (box != null)
            {
                double left = parentBox != null ? box.X - parentBox.X : box.X;
                double top = parentBox != null ? box.Y - parentBox.Y : box.Y;
                double width = box.Width + box.PaddingLeft + box.PaddingRight +
                               box.BorderLeft + box.BorderRight;
                double height = box.Height + box.PaddingTop + box.PaddingBottom +
                                box.BorderTop + box.BorderBottom;
                return (left, top, width, height);
            }

            var style = _element.Style;
            if (style == null) return (0, 0, 0, 0);
            double w = (style.Width ?? 0) + style.PaddingLeft + style.PaddingRight +
                       style.BorderLeftWidth + style.BorderRightWidth;
            double h = (style.Height ?? 0) + style.PaddingTop + style.PaddingBottom +
                       style.BorderTopWidth + style.BorderBottomWidth;
            return (style.Left ?? 0, style.Top ?? 0, w, h);
        }

        private (double Width, double Height) ClientMetrics()
        {
            var box = _element.Box;
            if (box != null)
                return (box.Width + box.PaddingLeft + box.PaddingRight,
                        box.Height + box.PaddingTop + box.PaddingBottom);
            var style = _element.Style;
            if (style == null) return (0, 0);
            double width = (style.Width ?? 0) + style.PaddingLeft + style.PaddingRight;
            double height = (style.Height ?? 0) + style.PaddingTop + style.PaddingBottom;
            return (width, height);
        }

        /// <summary>Deep/shallow DOM clone (DOM1 cloneNode): attributes and
        /// inline event handler sources are copied (IE cloned them); deep
        /// clones copy the whole child subtree.</summary>
        private static DomElement CloneElement(DomElement source, bool deep)
        {
            var clone = new DomElement(source.TagName);
            foreach (var (attrName, attrValue) in source.Attrs)
                clone.SetAttr(attrName, attrValue);
            foreach (var (eventName, handlerSource) in source.EventHandlers)
                clone.EventHandlers[eventName] = handlerSource;
            if (deep)
                foreach (var child in source.Children)
                    clone.AppendChild(CloneNodeDeep(child));
            return clone;
        }

        private static DomNode CloneNodeDeep(DomNode node) => node switch
        {
            DomElement element => CloneElement(element, deep: true),
            DomText text => new DomText { Data = text.Data },
            DomComment comment => new DomComment(comment.Text),
            _ => node
        };

        /// <summary>Wraps a child element for a mutation-method return value,
        /// tolerating a null state instead of crashing.</summary>
        private JsValue WrapChildValue(DomElement child) =>
            _state != null
                ? JsValue.FromObject(WrapElement(child, _state))
                : JsValue.FromObject(new ElementWrapper(child, _canvas, _scope));

        public override JsValue Get(string name)
        {
            if (Retro96.Engine.Java.JavaAppletHost.IsJavaElement(_element) &&
                _state?.Interpreter is { } interpreter &&
                _state.JavaAppletScriptMemberResolver?.Invoke(_element, name, interpreter) is { } appletMember)
                return appletMember;

            if (_element.TagName.Equals("embed", StringComparison.OrdinalIgnoreCase) &&
                _state?.EmbeddedScriptInfoResolver?.Invoke(_element) is { } scriptInfo &&
                scriptInfo.ScriptName.Length > 0 && string.Equals(name, scriptInfo.ScriptName, StringComparison.Ordinal))
            {
                return JsValue.FromObject(new EmbeddedScriptObject(_element, _state, scriptInfo.Methods));
            }

            // DOM Level 0 methods are real built-ins, not HTML attributes.
            // Keep them ahead of routed-attribute handling so `typeof
            // element.setAttribute` and `typeof element.getAttribute` are
            // always functions, matching the legacy DOM surface.
            if (string.Equals(name, "setAttribute", StringComparison.OrdinalIgnoreCase))
                return MakeSetAttributeFunction();
            if (string.Equals(name, "getAttribute", StringComparison.OrdinalIgnoreCase))
                return MakeGetAttributeFunction();
            // insertAdjacentHTML is an IE4-era surface — expose it only on
            // IE-compatible profiles so typeof checks don't lie in NN mode.
            if (string.Equals(name, "insertAdjacentHTML", StringComparison.OrdinalIgnoreCase) &&
                BrowserRuntime.SupportsInternetExplorerLegacy)
                return MakeInsertAdjacentHtmlFunction();

            if (string.Equals(name, "createTextRange", StringComparison.OrdinalIgnoreCase) &&
                BrowserRuntime.SupportsInternetExplorerLegacy &&
                _element.TagName.Equals("body", StringComparison.OrdinalIgnoreCase))
            {
                return JsValue.FromFunction(new JsFunction((self, args) =>
                    JsValue.FromObject(new LegacyTextRangeObject(
                        _element, _canvas, _scope, _state, useSelection: false)),
                    _scope, "createTextRange"));
            }

            // ── DOM Level 1 Core (checklist §10) ─────────────────────────
            if (name == "hasChildNodes")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                    JsValue.From(_element.Children.Count > 0), _scope, "hasChildNodes"));
            if (name == "hasAttributes")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                    JsValue.From(_element.Attrs.Count > 0), _scope, "hasAttributes"));
            if (name == "ownerDocument")
                return _state?.DocumentObject != null
                    ? JsValue.FromObject(_state.DocumentObject)
                    : JsValue.Null;
            if (name == "removeAttribute")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    string attrName = args.Length > 0 ? args[0].ToJsString() : "";
                    if (attrName.Length == 0) return JsValue.Undefined;
                    _element.SetAttr(attrName, null);
                    if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase) &&
                        attrName.Length > 2)
                    {
                        string eventName = attrName.ToLowerInvariant();
                        _state?.Interpreter?.ClearDomEventProperty(_element, eventName);
                        _element.EventHandlers.Remove(eventName);
                    }
                    _canvas?.RequestRerender();
                    return JsValue.Undefined;
                }, _scope, "removeAttribute"));
            if (name == "replaceChild")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length < 2) return JsValue.Null;
                    var newNode = UnwrapNodeArg(args[0]);
                    var oldNode = UnwrapNodeArg(args[1]);
                    if (newNode == null || oldNode == null || !ReferenceEquals(oldNode.Parent, _element))
                        return JsValue.Null;
                    _element.InsertBefore(newNode, oldNode);
                    _element.RemoveChild(oldNode);
                    _canvas?.ReflowDocument();
                    return JsValue.FromObject(WrapNode(oldNode));   // DOM1: returns the replaced node
                }, _scope, "replaceChild"));
            if (name == "cloneNode")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    bool deep = args.Length > 0 && args[0].ToBoolean();
                    var clone = CloneElement(_element, deep);
                    return _state != null
                        ? JsValue.FromObject(WrapElement(clone, _state))
                        : JsValue.FromObject(new ElementWrapper(clone, _canvas, _scope));
                }, _scope, "cloneNode"));
            if (name == "attributes")
                return JsValue.FromObject(new AttributesObject(_element, _canvas, _scope, _state));

            // ── IE5 DHTML object model (checklist §10) ───────────────────
            if (BrowserRuntime.SupportsInternetExplorerLegacy)
            {
                if (string.Equals(name, "attachEvent", StringComparison.OrdinalIgnoreCase))
                    return JsValue.FromFunction(new JsFunction((self, args) =>
                    {
                        string evt = args.Length > 0 ? args[0].ToJsString() : "";
                        if (args.Length > 1 && args[1].Type == JsType.Function && evt.Length > 0)
                            _state?.Interpreter?.AttachEventHandler(_element, evt, args[1]);
                        return JsValue.From(args.Length > 1 && args[1].Type == JsType.Function);
                    }, _scope, "attachEvent"));
                if (string.Equals(name, "detachEvent", StringComparison.OrdinalIgnoreCase))
                    return JsValue.FromFunction(new JsFunction((self, args) =>
                    {
                        string evt = args.Length > 0 ? args[0].ToJsString() : "";
                        bool removed = args.Length > 1 && evt.Length > 0 &&
                            _state?.Interpreter?.DetachEventHandler(_element, evt, args[1]) == true;
                        return JsValue.From(removed);
                    }, _scope, "detachEvent"));
                if (string.Equals(name, "insertAdjacentText", StringComparison.OrdinalIgnoreCase))
                    return MakeInsertAdjacentTextFunction();
                if (name == "currentStyle" && !Properties.ContainsKey("currentStyle"))
                    Properties["currentStyle"] = JsValue.FromObject(new CurrentStyleObject(_element));
                if (name == "uniqueID" && _state != null)
                    return JsValue.From(DomBindings.GetOrCreateUniqueId(_element, _state));
                if (name == "setActive")
                    return JsValue.FromFunction(new JsFunction((self, args) =>
                    {
                        // setActive() is focus-without-scroll; the engine's
                        // focus state is the document's FocusedElement slot.
                        var owner = _element.OwnerDocument();
                        if (owner != null) owner.FocusedElement = _element;
                        _canvas?.RequestRerender();
                        return JsValue.Undefined;
                    }, _scope, "setActive"));

                // IE5 geometry (§10): offset*/client*/scroll*. LayoutBox when
                // a layout ran (box carries document-space border-box origin
                // + content size); the resolved ComputedStyle otherwise.
                switch (name)
                {
                    case "offsetParent":
                        return OffsetParent() is { } op && _state != null
                            ? JsValue.FromObject(WrapElement(op, _state))
                            : JsValue.Null;
                    case "offsetLeft": return JsValue.From(OffsetMetrics().Left);
                    case "offsetTop": return JsValue.From(OffsetMetrics().Top);
                    case "offsetWidth": return JsValue.From(OffsetMetrics().Width);
                    case "offsetHeight": return JsValue.From(OffsetMetrics().Height);
                    case "clientWidth": return JsValue.From(ClientMetrics().Width);
                    case "clientHeight": return JsValue.From(ClientMetrics().Height);
                    case "scrollTop":
                    case "scrollLeft":
                        // Readback of scripted writes (the engine has no
                        // per-element content scrolling — documented
                        // limitation); default 0, matching an unscrolled box.
                        if (Properties.TryGetValue(name, out var stored) && stored.Type == JsType.Number)
                            return stored;
                        return JsValue.From(0);
                }
            }

            // IE's legacy `element.all` is a callable sub-collection of every
            // descendant element. Keep it live so scripts see DOM changes made
            // after the wrapper was first obtained.
            if (name == "all" && BrowserRuntime.SupportsInternetExplorerLegacy && _state != null)
                return JsValue.FromFunction(CreateLegacyElementCollection(
                    _state, () => _element.ElementDescendants().Skip(1),
                    "all", supportsTags: true));

            if (name == "children")
            {
                if (_state == null) return JsValue.Null;
                return JsValue.FromObject(BuildElementCollection(
                    _scope, _element.ElementChildren(), _state));
            }

            if (name == "contains")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0 || args[0].Type != JsType.Object)
                        return JsValue.From(false);

                    if (args[0].GetObjectOrFunction() is not ElementWrapper other)
                        return JsValue.From(false);

                    for (var node = other.Element as DomNode; node != null; node = node.Parent)
                        if (ReferenceEquals(node, _element))
                            return JsValue.From(true);

                    return JsValue.From(false);
                }, _scope, "contains"));

            // ── node identity / tree navigation ──
            // Strict IE3 predates the later DOM tree/HTML mutation surface.
            // Retro96 exposes the broader DOM union.
            if (BrowserRuntime.IsInternetExplorer3 && name is
                "firstChild" or "lastChild" or "parentNode" or "nextSibling" or
                "previousSibling" or "childNodes")
                return JsValue.Undefined;

            switch (name)
            {
                case "tagName":
                case "nodeName":
                    // DOM Level 1 HTML: element names report in the
                    // uppercase HTML canonical form ("BODY"), exactly like
                    // IE5 and NS4.7 did.
                    return JsValue.From(_element.TagName.ToUpperInvariant());
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
                case "outerText":
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
                        var arr = new JsObject { Class = "Array", Prototype = JsInterpreter.ArrayPrototype };
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

            // .style is a later IE DOM surface; strict IE3 mode does not expose it.
            if (name == "style" && BrowserRuntime.IsInternetExplorer3)
                return JsValue.Undefined;

            if (name == "style" && !Properties.ContainsKey("style"))
            {
                Properties["style"] = JsValue.FromObject(
                    new InlineStyleObject(_element, _canvas, _scope));
            }

            // getElementsByTagName — later DOM API; hide it in IE3 mode.
            if (name == "getElementsByTagName" && !BrowserRuntime.IsInternetExplorer3)
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

            // DOM-0 event properties are live properties of the underlying
            // element. There are two sources in the DOM: a function assigned
            // by script, and an inline HTML event-source string. Expose both
            // through the same property surface; the latter must read back as
            // a function just like a real 1996 DOM did.
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && name.Length > 2)
            {
                string eventName = name.ToLowerInvariant();
                if (_state?.Interpreter?.TryGetDomEventProperty(
                        _element, eventName, out var h) == true &&
                    h.Type == JsType.Function)
                    return h;

                if (Properties.TryGetValue(name, out h) && h.Type == JsType.Function)
                    return h;

                if (_element.EventHandlers.TryGetValue(eventName, out var source) &&
                    source != "__js_handler__")
                {
                    var eventInterpreter = _state?.Interpreter;
                    if (eventInterpreter != null)
                    {
                        return JsValue.FromFunction(new JsFunction(
                            (self, args) => eventInterpreter.FireEvent(_element, eventName),
                            _scope, eventName));
                    }
                }

                return JsValue.Undefined;
            }

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
                            bool hasOptions = false;
                            foreach (var o in OptionsOf(_element))
                            {
                                if (o.HasAttr("selected")) return JsValue.From(i);
                                i++;
                                hasOptions = true;
                            }
                            // No explicit selection: the renderer and .value
                            // treat the first option as implicitly selected —
                            // selectedIndex must agree (used to return -1).
                            return JsValue.From(hasOptions ? 0 : -1);
                        }
                    case "options":
                        {
                            var arr = new JsObject { Class = "Array", Prototype = JsInterpreter.ArrayPrototype };
                            int i = 0;
                            foreach (var o in OptionsOf(_element))
                            {
                                // Cached wrappers (with state!) so option
                                // identity is stable and handlers survive.
                                arr.Set(i.ToString(), JsValue.FromObject(
                                    _state != null ? WrapElement(o, _state)
                                                   : new ElementWrapper(o, _canvas, _scope)));
                                i++;
                            }
                            arr.Set("length", JsValue.From(i));
                            return JsValue.FromObject(arr);
                        }
                    case "length":
                        return JsValue.From(CountOptions(_element));
                }
            }

            if (name == "value" && _element.TagName == "textarea")
                return JsValue.From(_element.InnerText);

            if (RoutedAttrs.Contains(name))
            {
                var attr = _element.GetAttr(name);
                if (name == "checked")
                    // Boolean HTML attribute: presence = true, absence = false.
                    // (The old code returned "" for absence and then tested
                    // `attr != null` after a null-guard return — always true.)
                    return JsValue.From(attr != null);
                if (attr == null) return JsValue.From("");
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
                var arr = new JsObject { Class = "Array", Prototype = JsInterpreter.ArrayPrototype };
                int i = 0;
                foreach (var control in ControlsOf(_element))
                {
                    var wrapper = _state != null ? WrapElement(control, _state)
                                                 : new ElementWrapper(control, _canvas, _scope);
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
                    // HTMLFormElement.submit() is the programmatic fast path:
                    // it bypasses onsubmit instead of dispatching a new submit event.
                    _canvas?.SubmitForm(_element, null, dispatchSubmitEvent: false);
                    return JsValue.Undefined;
                }, _scope, "submit"));

            // appendChild / insertBefore / removeChild / replaceChild — the
            // old DOM mutation surface for pages that build small bits of UI
            // at runtime. Elements AND text nodes (document.createTextNode)
            // are accepted as children.
            if (name == "appendChild")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0) return JsValue.Null;
                    var child = UnwrapNodeArg(args[0]);
                    if (child == null) return JsValue.Null;

                    _element.AppendChild(child);
                    _canvas?.ReflowDocument();
                    return child is DomElement childElement
                        ? WrapChildValue(childElement)
                        : JsValue.FromObject(WrapNode(child));
                }, _scope, "appendChild"));

            if (name == "insertBefore")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0) return JsValue.Null;
                    var child = UnwrapNodeArg(args[0]);
                    if (child == null) return JsValue.Null;

                    DomNode? reference = null;
                    if (args.Length > 1)
                        reference = UnwrapNodeArg(args[1]);

                    _element.InsertBefore(child, reference);
                    _canvas?.ReflowDocument();
                    return child is DomElement childElement
                        ? WrapChildValue(childElement)
                        : JsValue.FromObject(WrapNode(child));
                }, _scope, "insertBefore"));

            if (name == "removeChild")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length == 0) return JsValue.Null;
                    var child = UnwrapNodeArg(args[0]);
                    if (child == null || !ReferenceEquals(child.Parent, _element))
                        return JsValue.Null;

                    _element.RemoveChild(child);
                    _canvas?.ReflowDocument();
                    return child is DomElement childElement
                        ? WrapChildValue(childElement)
                        : JsValue.FromObject(WrapNode(child));
                }, _scope, "removeChild"));

            // DOM-0 NAMED CONTROL ACCESS — form.digits (the era's field
            // idiom, document.clockForm.digits.value = ...).  Named controls
            // live directly on the form object, exactly like on
            // form.elements; without this the field read came back
            // undefined and clock/validator writes went nowhere.
            if (_state != null && _element.TagName == "form" &&
                name.Length > 0 && !char.IsDigit(name[0]) && name != "style" &&
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
            if (Retro96.Engine.Java.JavaAppletHost.IsJavaElement(_element) &&
                _state?.Interpreter is { } interpreter &&
                _state.JavaAppletScriptMemberSetter?.Invoke(_element, name, interpreter, value) == true)
                return;

            // Keep IE3's DOM intentionally old in both reads and writes.
            if (BrowserRuntime.IsInternetExplorer3 &&
                name is "innerHTML" or "innerText" or "outerHTML" or "outerText")
                return;

            if (name == "innerHTML")
            {
                foreach (var oldChild in _element.Children.ToList())
                    _element.RemoveChild(oldChild);
                var doc = _element.OwnerDocument();
                // Detached elements (document.createElement + innerHTML, the
                // era's UI-building idiom) have no owner document yet — parse
                // the fragment against a blank base so the children still land.
                var baseUrl = doc?.BaseUrl ?? ParsedUrl.Parse("about:blank");
                var fragment = HtmlParser.Parse(value.ToJsString(), baseUrl,
                    doc?.Cookies ?? new CookieStore());
                var body = fragment.ElementDescendants()
                    .FirstOrDefault(e => e.TagName.Equals("body", StringComparison.OrdinalIgnoreCase));
                // Fall back to the fragment's own children when the parser
                // did not synthesise a <body> — the old code appended
                // NOTHING in that case, silently discarding the markup.
                var source = body != null ? body.Children : fragment.Children;
                foreach (var child in source.ToList())
                    _element.AppendChild(child);
                _canvas?.ReflowDocument();
                return;
            }

            // IE/Trident exposed innerText as a live writable host property.
            // The old binding only implemented the getter, so assignment
            // silently landed in JsObject.Properties and the real DOM stayed
            // unchanged — exactly why the test bench's detection cells kept
            // showing "Detecting...". Replace the children with one live text
            // node and reflow the page.
            if (name is "innerText" or "text")
            {
                foreach (var oldChild in _element.Children.ToList())
                    _element.RemoveChild(oldChild);
                _element.AppendChild(new DomText { Data = value.ToJsString() });
                _canvas?.ReflowDocument();
                return;
            }

            if (name == "outerText")
            {
                string text = value.ToJsString();
                if (_element.Parent is DomElement parent)
                {
                    parent.InsertBefore(new DomText { Data = text }, _element);
                    parent.RemoveChild(_element);
                }
                else
                {
                    foreach (var oldChild in _element.Children.ToList())
                        _element.RemoveChild(oldChild);
                    _element.AppendChild(new DomText { Data = text });
                }
                _canvas?.ReflowDocument();
                return;
            }

            // outerHTML — replace this element in its parent with the parsed
            // replacement markup. Used to be silently dropped into Properties.
            if (name == "outerHTML")
            {
                var doc = _element.OwnerDocument();
                var parent = _element.Parent as DomElement;
                if (doc == null || parent == null)
                    return;   // detached element — nothing to replace in the tree
                var fragment = HtmlParser.Parse(value.ToJsString(),
                    doc.BaseUrl ?? ParsedUrl.Parse("about:blank"), doc.Cookies);
                var body = fragment.ElementDescendants()
                    .FirstOrDefault(e => e.TagName.Equals("body", StringComparison.OrdinalIgnoreCase));
                var nodes = (body?.Children ?? fragment.Children).ToList();
                foreach (var node in nodes)
                    parent.InsertBefore(node, _element);
                parent.RemoveChild(_element);
                _canvas?.ReflowDocument();
                return;
            }

            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                // Handler assignment is a live DOM property. Store it in the
                // per-element interpreter state so a newly-created wrapper
                // still observes and dispatches the same function.
                string eventName = name.ToLowerInvariant();
                Properties[name] = value;
                if (value.Type == JsType.Function)
                {
                    _state?.Interpreter?.SetDomEventProperty(_element, eventName, value);
                    _element.EventHandlers[eventName] = "__js_handler__";
                }
                else if (value.Type == JsType.String)
                {
                    _state?.Interpreter?.ClearDomEventProperty(_element, eventName);
                    _element.EventHandlers[eventName] = value.GetString();
                }
                else
                {
                    _state?.Interpreter?.ClearDomEventProperty(_element, eventName);
                    _element.EventHandlers.Remove(eventName);
                }
                return;
            }

            if (name == "value" && _element.TagName == "textarea")
            {
                string text = value.ToJsString();
                var textNode = _element.Children.OfType<DomText>().FirstOrDefault();
                if (textNode == null)
                    _element.AppendChild(new DomText { Data = text });
                else
                    textNode.Data = text;
                _canvas?.RequestRerender();
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

            // IE5 element.scrollTop/scrollLeft writes — the engine has no
            // per-element content scrolling; the value is stored for
            // readback and a repaint is requested (documented limitation:
            // setting scrollTop does not actually scroll clipped content).
            if ((name == "scrollTop" || name == "scrollLeft") &&
                BrowserRuntime.SupportsInternetExplorerLegacy)
            {
                base.Set(name, value);
                _canvas?.RequestRerender();
                return;
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
            // browsers' form-element-pointer association. Skip duplicates
            // for controls that are already descendants of the form.
            var doc = form.OwnerDocument();
            if (doc != null)
            {
                foreach (var e in doc.ElementDescendants())
                {
                    if (e.FormOwner == null || !ReferenceEquals(e.FormOwner, form))
                        continue;
                    bool isDescendant = false;
                    for (var p = e.Parent; p != null && !isDescendant; p = p.Parent)
                        if (ReferenceEquals(p, form)) isDescendant = true;
                    if (!isDescendant)
                        yield return e;
                }
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

        /// <summary>Normalizes both "backgroundColor" and "background-color"
        /// spellings to the hyphenated CSS form — era scripts used both.</summary>
        private static string NormalizeStyleKey(string name)
        {
            if (name.IndexOf('-') >= 0) return name.ToLowerInvariant();
            var sb = new System.Text.StringBuilder(name.Length + 4);
            foreach (char c in name)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public override JsValue Get(string name)
        {
            // cssText — the whole attribute as source text
            if (name == "cssText")
                return JsValue.From(_element.GetAttr("style") ?? "");

            // IE5 style.pixel*/pos* (§10): numeric pixel reads of the inline
            // left/top/width/height declarations. posLeft/posTop are the
            // same numbers (IE stored the unitful authored value; scripts
            // used the pair interchangeably for positioned DHTML).
            switch (name)
            {
                case "pixelLeft":
                case "posLeft":
                    return JsValue.From(InlinePixels("left"));
                case "pixelTop":
                case "posTop":
                    return JsValue.From(InlinePixels("top"));
                case "pixelWidth":
                    return JsValue.From(InlinePixels("width"));
                case "pixelHeight":
                    return JsValue.From(InlinePixels("height"));
                case "pixelRight":
                    return JsValue.From(InlinePixels("right"));
                case "pixelBottom":
                    return JsValue.From(InlinePixels("bottom"));
            }

            var decls = ParseCurrent();
            if (decls.TryGetValue(name.ToLowerInvariant(), out string? v))
                return JsValue.From(v);
            if (decls.TryGetValue(NormalizeStyleKey(name), out v))
                return JsValue.From(v);
            return JsValue.From("");
        }

        /// <summary>The numeric px value of an inline declaration (unitless
        /// numbers count as px, the era default), 0 when absent.</summary>
        private double InlinePixels(string property)
        {
            var decls = ParseCurrent();
            if (!decls.TryGetValue(property, out string? raw) || raw == null)
                return 0;
            string t = raw.Trim();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                t = t[..^2].Trim();
            return double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0;
        }

        public override void Set(string name, JsValue value)
        {
            if (name == "cssText")
            {
                _element.SetAttr("style", value.ToJsString());
                Reflow();
                return;
            }

            // IE5 style.pixel*/pos* writes (§10): numeric writes land as px
            // declarations, so layer-style DHTML animation scripts work.
            switch (name)
            {
                case "pixelLeft":
                case "posLeft":
                    SetInlinePx("left", value); return;
                case "pixelTop":
                case "posTop":
                    SetInlinePx("top", value); return;
                case "pixelWidth":
                    SetInlinePx("width", value); return;
                case "pixelHeight":
                    SetInlinePx("height", value); return;
                case "pixelRight":
                    SetInlinePx("right", value); return;
                case "pixelBottom":
                    SetInlinePx("bottom", value); return;
            }

            string v = value.ToJsString().Trim();
            var decls = ParseCurrent();
            string key = NormalizeStyleKey(name);
            if (v.Length == 0 || v.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("undefined", StringComparison.OrdinalIgnoreCase))
                decls.Remove(key);
            else
                decls[key] = v;

            _element.SetAttr("style",
                string.Join("; ", decls.Select(kv => $"{kv.Key}: {kv.Value}")));
            Reflow();
        }

        /// <summary>Writes an inline declaration as "<paramref name="property"/>: Npx"
        /// (replacing any existing declaration) and reflows.</summary>
        private void SetInlinePx(string property, JsValue value)
        {
            double n = value.ToNumber();
            var decls = ParseCurrent();
            decls[property] = double.IsNaN(n) ? "0px" :
                $"{n.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}px";
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

    // ═════════════════════════════════════════════════════════════════════
    // 1999 DOM additions: attributes map, currentStyle, frames, NS4 layers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// element.attributes — a NamedNodeMap-ish live view (DOM Level 1, §10):
    /// attributes[i].name/.value, attributes.length, attributes[name],
    /// attributes.getNamedItem(name). Attribute nodes are {nodeType: 2} with
    /// a writable .value (a write hits the live element and repaints).
    /// </summary>
    private sealed class AttributesObject : JsObject
    {
        private readonly DomElement _element;
        private readonly BrowserCanvas? _canvas;
        private readonly JsScope _scope;
        private readonly DocumentBindingsState? _state;

        public AttributesObject(DomElement element, BrowserCanvas? canvas,
                                JsScope scope, DocumentBindingsState? state)
        {
            _element = element;
            _canvas = canvas;
            _scope = scope;
            _state = state;
            Class = "NamedNodeMap";
        }

        private List<KeyValuePair<string, string>> Snapshot() =>
            _element.Attrs.ToList();

        private JsValue WrapAttr(KeyValuePair<string, string> attr) =>
            JsValue.FromObject(new AttributeNodeObject(_element, attr.Key, _canvas));

        public override JsValue Get(string name)
        {
            if (name == "length")
                return JsValue.From(_element.Attrs.Count);

            if (name == "getNamedItem")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    string key = args.Length > 0 ? args[0].ToJsString() : "";
                    return _element.HasAttr(key)
                        ? WrapAttr(new KeyValuePair<string, string>(key, _element.GetAttr(key)!))
                        : JsValue.Null;
                }, _scope, "getNamedItem"));

            if (name == "setNamedItem")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    if (args.Length > 0 && args[0].GetObject() is AttributeNodeObject node)
                    {
                        node.Commit(_element);
                        _canvas?.RequestRerender();
                    }
                    return JsValue.Undefined;
                }, _scope, "setNamedItem"));

            if (name == "removeNamedItem")
                return JsValue.FromFunction(new JsFunction((self, args) =>
                {
                    string key = args.Length > 0 ? args[0].ToJsString() : "";
                    if (!_element.HasAttr(key)) return JsValue.Null;
                    var removed = WrapAttr(new KeyValuePair<string, string>(key, _element.GetAttr(key)!));
                    _element.SetAttr(key, null);
                    _canvas?.RequestRerender();
                    return removed;
                }, _scope, "removeNamedItem"));

            // Index or attribute-name access
            if (int.TryParse(name, out int index))
            {
                var snapshot = Snapshot();
                return index >= 0 && index < snapshot.Count
                    ? WrapAttr(snapshot[index])
                    : JsValue.Undefined;
            }
            if (_element.HasAttr(name))
                return WrapAttr(new KeyValuePair<string, string>(name, _element.GetAttr(name)!));
            return base.Get(name);
        }
    }

    /// <summary>One attribute node: nodeType 2, name, live value
    /// (write → SetAttr + repaint), specified, nodeName/nodeValue.</summary>
    private sealed class AttributeNodeObject : JsObject
    {
        private readonly DomElement _element;
        private readonly BrowserCanvas? _canvas;
        private readonly string _name;

        public AttributeNodeObject(DomElement element, string name, BrowserCanvas? canvas)
        {
            _element = element;
            _name = name;
            _canvas = canvas;
            Class = "Attr";
        }

        internal void Commit(DomElement element) =>
            element.SetAttr(_name, element.GetAttr(_name) ?? "");

        public override JsValue Get(string name)
        {
            switch (name)
            {
                case "nodeType": return JsValue.From(2);
                case "nodeName":
                case "name": return JsValue.From(_name);
                case "nodeValue":
                case "value": return JsValue.From(_element.GetAttr(_name) ?? "");
                case "specified": return JsValue.From(_element.HasAttr(_name));
            }
            return base.Get(name);
        }

        public override void Set(string name, JsValue value)
        {
            if (name is "nodeValue" or "value")
            {
                _element.SetAttr(_name, value.ToJsString());
                _canvas?.RequestRerender();
                return;
            }
            base.Set(name, value);
        }
    }

    /// <summary>
    /// element.currentStyle — IE5's READ-ONLY computed style object (§10).
    /// Reads the resolved ComputedStyle the style resolver installed on the
    /// element, exposing the same camelCase property surface as
    /// element.style; writes are silently ignored (read-only contract).
    /// Before the first style resolve the reads return "".
    /// </summary>
    private sealed class CurrentStyleObject : JsObject
    {
        private readonly DomElement _element;

        public CurrentStyleObject(DomElement element)
        {
            _element = element;
            Class = "CSSCurrentStyle";
        }

        private static string NormalizeKey(string name)
        {
            if (name.IndexOf('-') >= 0) return name.ToLowerInvariant();
            var sb = new System.Text.StringBuilder(name.Length + 4);
            foreach (char c in name)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string Px(float v) =>
            ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";

        private static string Hex(Retro96.Drawing.Color c) =>
            $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        public override JsValue Get(string name)
        {
            if (name == "length" || name == "cssText") return base.Get(name);
            var style = _element.Style;
            if (style == null) return JsValue.From("");

            switch (NormalizeKey(name))
            {
                case "color": return JsValue.From(Hex(style.Color));
                case "background-color":
                    return JsValue.From(style.BackgroundColor == Retro96.Drawing.Color.Transparent
                        ? "transparent" : Hex(style.BackgroundColor));
                case "background-image":
                    return JsValue.From(style.BackgroundImage ?? "none");
                case "background-repeat":
                    return JsValue.From(style.BackgroundRepeat.ToString().ToLowerInvariant());
                case "display": return JsValue.From(style.Display.ToString().ToLowerInvariant());
                case "visibility": return JsValue.From(style.Visibility.ToString().ToLowerInvariant());
                case "overflow": return JsValue.From(style.Overflow.ToString().ToLowerInvariant());
                case "position": return JsValue.From(style.Position.ToString().ToLowerInvariant());
                case "float": return JsValue.From(style.Float.ToString().ToLowerInvariant());
                case "clear": return JsValue.From(style.Clear.ToString().ToLowerInvariant());
                case "z-index": return JsValue.From(style.ZIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                case "font-family": return JsValue.From(string.Join(", ", style.FontFamily));
                case "font-size": return JsValue.From(Px(style.FontSize));
                case "font-weight": return JsValue.From(style.FontWeight switch
                {
                    Css.FontWeightValue.Bold => "bold",
                    Css.FontWeightValue.Bolder => "bolder",
                    Css.FontWeightValue.Lighter => "lighter",
                    _ => "normal"
                });
                case "font-style": return JsValue.From(style.FontStyle == Css.FontStyleValue.Italic
                    ? "italic" : "normal");
                case "font-variant": return JsValue.From(style.FontVariant == Css.FontVariantValue.SmallCaps
                    ? "small-caps" : "normal");
                case "text-align": return JsValue.From(style.TextAlign.ToString().ToLowerInvariant());
                case "text-decoration":
                {
                    var d = style.TextDecoration;
                    if (d == Css.TextDecoration.None) return JsValue.From("none");
                    var parts = new List<string>();
                    if ((d & Css.TextDecoration.Underline) != 0) parts.Add("underline");
                    if ((d & Css.TextDecoration.Overline) != 0) parts.Add("overline");
                    if ((d & Css.TextDecoration.LineThrough) != 0) parts.Add("line-through");
                    if ((d & Css.TextDecoration.Blink) != 0) parts.Add("blink");
                    return JsValue.From(string.Join(" ", parts));
                }
                case "text-transform": return JsValue.From(style.TextTransform.ToString().ToLowerInvariant());
                case "white-space": return JsValue.From(style.WhiteSpace.ToString().ToLowerInvariant());
                case "line-height":
                    return JsValue.From(style.LineHeightMode == Css.LineHeightMode.Normal
                        ? "normal" : Px(style.LineHeightPixels));
                case "width": return JsValue.From(style.Width is { } w ? Px(w) : "auto");
                case "height": return JsValue.From(style.Height is { } h ? Px(h) : "auto");
                case "top": return JsValue.From(style.Top is { } t ? Px(t) : "auto");
                case "left": return JsValue.From(style.Left is { } l ? Px(l) : "auto");
                case "right": return JsValue.From(style.Right is { } r ? Px(r) : "auto");
                case "bottom": return JsValue.From(style.Bottom is { } b ? Px(b) : "auto");
                case "margin-top": return JsValue.From(Px(style.MarginTop));
                case "margin-right": return JsValue.From(Px(style.MarginRight));
                case "margin-bottom": return JsValue.From(Px(style.MarginBottom));
                case "margin-left": return JsValue.From(Px(style.MarginLeft));
                case "padding-top": return JsValue.From(Px(style.PaddingTop));
                case "padding-right": return JsValue.From(Px(style.PaddingRight));
                case "padding-bottom": return JsValue.From(Px(style.PaddingBottom));
                case "padding-left": return JsValue.From(Px(style.PaddingLeft));
                case "border-top-width": return JsValue.From(Px(style.BorderTopWidth));
                case "border-right-width": return JsValue.From(Px(style.BorderRightWidth));
                case "border-bottom-width": return JsValue.From(Px(style.BorderBottomWidth));
                case "border-left-width": return JsValue.From(Px(style.BorderLeftWidth));
                case "border-top-color": return JsValue.From(Hex(style.BorderTopColor));
                case "border-left-color": return JsValue.From(Hex(style.BorderLeftColor));
                case "list-style-type": return JsValue.From(style.ListStyleType.ToString().ToLowerInvariant());
            }
            return JsValue.From("");
        }

        /// <summary>currentStyle is read-only — writes are ignored (IE5
        /// threw only in the DOM1 spec; scripts probed with typeof).</summary>
        public override void Set(string name, JsValue value)
        {
            // deliberately ignored
        }
    }

    /// <summary>
    /// window.frames[] + window.length — the frame/iframe elements of THIS
    /// document in tree order, indexable by number and by name (documented
    /// limitation: entries are the frame ELEMENT wrappers, not the real
    /// cross-frame window objects, which the shell owns).
    /// </summary>
    private sealed class FramesCollectionObject : JsObject
    {
        private readonly DomDocument _doc;
        private readonly DocumentBindingsState _state;
        private readonly JsScope _scope;

        public FramesCollectionObject(DomDocument doc, DocumentBindingsState state, JsScope scope)
        {
            _doc = doc;
            _state = state;
            _scope = scope;
            Class = "FramesArray";
        }

        internal int FrameCount => Frames().Count;

        private List<DomElement> Frames() =>
            _doc.ElementDescendants()
                .Where(e => e.TagName is "frame" or "iframe")
                .ToList();

        public override JsValue Get(string name)
        {
            if (name == "length")
                return JsValue.From(FrameCount);

            var frames = Frames();
            if (int.TryParse(name, out int index))
                return index >= 0 && index < frames.Count
                    ? JsValue.FromObject(WrapElement(frames[index], _state))
                    : JsValue.Undefined;

            foreach (var frame in frames)
            {
                var frameName = frame.GetAttr("name") ?? frame.GetAttr("id");
                if (frameName == name)
                    return JsValue.FromObject(WrapElement(frame, _state));
            }
            return base.Get(name);
        }
    }

    /// <summary>
    /// document.layers — the NS4 layer collection (checklist §10): every
    /// element declared positioned (CSS position:absolute/relative via
    /// inline OR resolved style) plus &lt;layer&gt;/&lt;ilayer&gt; elements,
    /// in document order, indexable by number AND by name (id/name attr).
    /// </summary>
    private sealed class LayerCollectionObject : JsObject
    {
        private readonly DomDocument _doc;
        private readonly DocumentBindingsState _state;
        private readonly JsScope _scope;

        public LayerCollectionObject(DomDocument doc, DocumentBindingsState state, JsScope scope)
        {
            _doc = doc;
            _state = state;
            _scope = scope;
            Class = "LayerArray";
        }

        internal static bool IsLayerElement(DomElement element)
        {
            if (element.TagName is "layer" or "ilayer") return true;

            // Resolved style (a StyleResolver pass ran)
            if (element.Style?.Position is Css.PositionValue.Absolute
                or Css.PositionValue.Relative or Css.PositionValue.Fixed)
                return true;

            // Inline STYLE= position (may predate style resolution)
            var inline = element.GetAttr("style");
            if (inline != null)
            {
                foreach (var decl in Css.CssParser.ParseInlineStyle(inline))
                    if (string.Equals(decl.Property, "position", StringComparison.OrdinalIgnoreCase) &&
                        (decl.Value ?? "").Trim() is "absolute" or "relative" or "fixed")
                        return true;
            }
            return false;
        }

        private List<DomElement> Layers() =>
            _doc.ElementDescendants().Where(IsLayerElement).ToList();

        public override JsValue Get(string name)
        {
            if (name == "length")
                return JsValue.From(Layers().Count);

            var layers = Layers();
            if (int.TryParse(name, out int index))
                return index >= 0 && index < layers.Count
                    ? JsValue.FromObject(GetOrWrapLayer(layers[index]))
                    : JsValue.Undefined;

            foreach (var layer in layers)
            {
                var layerName = layer.GetAttr("name") ?? layer.GetAttr("id");
                if (layerName == name)
                    return JsValue.FromObject(GetOrWrapLayer(layer));
            }
            return base.Get(name);
        }

        /// <summary>One stable layer object per element
        /// (document.layers["a"] === document.layers["a"]).</summary>
        private JsObject GetOrWrapLayer(DomElement element)
        {
            if (_state.LayerWrappers.TryGetValue(element, out var existing))
                return existing;
            var layer = new NetscapeLayerObject(element, _state, _scope);
            _state.LayerWrappers[element] = layer;
            return layer;
        }
    }

    /// <summary>
    /// A Netscape 4 layer object (checklist §10): left/top/zIndex/visibility
    /// ("show"/"hide"/"inherit"), clip.{left,top,right,bottom}, bgColor,
    /// background, src, document, and the moveTo/moveBy/resizeTo/moveAbove/
    /// moveBelow/load methods. Writing left/top REALLY moves the element —
    /// the write lands in the inline style (forcing position:absolute when
    /// missing) and triggers the same reflow the innerHTML writer uses.
    /// </summary>
    private sealed class NetscapeLayerObject : JsObject
    {
        private readonly DomElement _element;
        private readonly DocumentBindingsState _state;
        private readonly JsScope _scope;

        public NetscapeLayerObject(DomElement element, DocumentBindingsState state, JsScope scope)
        {
            _element = element;
            _state = state;
            _scope = scope;
            Class = "Layer";
        }

        private JsValue Fn(string name, Func<JsValue, JsValue[], JsValue> impl) =>
            JsValue.FromFunction(new JsFunction(impl, _scope, name));

        private Dictionary<string, string> StyleDecls()
        {
            var decls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var attr = _element.GetAttr("style");
            if (attr == null) return decls;
            foreach (var decl in Css.CssParser.ParseInlineStyle(attr))
            {
                string key = (decl.Property ?? "").Trim().ToLowerInvariant();
                if (key.Length > 0) decls[key] = decl.Value ?? "";
            }
            return decls;
        }

        private void WriteStyle(Action<Dictionary<string, string>> mutate)
        {
            var decls = StyleDecls();
            // Layer-style writes imply positioned elements (NS4 layers were
            // always positioned boxes).
            decls.TryAdd("position", "absolute");
            mutate(decls);
            _element.SetAttr("style",
                string.Join("; ", decls.Select(kv => $"{kv.Key}: {kv.Value}")));
            try { _state.Canvas?.ReflowDocument(); }
            catch { /* best-effort reflow */ }
        }

        private static double PxOf(string? raw)
        {
            if (raw == null) return 0;
            string t = raw.Trim();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
            return double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0;
        }

        private double ReadPx(string property) => PxOf(StyleDecls().TryGetValue(property, out var raw) ? raw : null);

        public override JsValue Get(string name)
        {
            switch (name)
            {
                case "left": return JsValue.From(ReadPx("left"));
                case "top": return JsValue.From(ReadPx("top"));
                case "zIndex":
                    return JsValue.From(PxOf(StyleDecls().TryGetValue("z-index", out var z) ? z : "0"));
                case "visibility":
                {
                    // NS4 vocabulary: show/hide/inherit (checklist §10)
                    var resolved = _element.Style?.Visibility;
                    if (StyleDecls().TryGetValue("visibility", out var vis))
                        return JsValue.From(vis.Trim() switch
                        {
                            "show" or "visible" => "show",
                            "hide" or "hidden" => "hide",
                            _ => "inherit"
                        });
                    return JsValue.From(resolved == Css.VisibilityValue.Hidden ? "hide" : "inherit");
                }
                case "bgColor":
                {
                    if (StyleDecls().TryGetValue("background-color", out var bg)) return JsValue.From(bg);
                    var attr = _element.GetAttr("bgcolor");
                    return JsValue.From(attr ?? "null");
                }
                case "background":
                {
                    if (StyleDecls().TryGetValue("background-image", out var img)) return JsValue.From(img);
                    return JsValue.From("null");
                }
                case "src":
                    return JsValue.From(_element.GetAttr("src") ?? "null");
                case "document":
                    // NS4 layers were separate documents; the engine keeps one
                    // DOM, so the layer's "document" view IS the parent
                    // document (documented choice: nested layer lookups via
                    // layer.document.layers work against the same collection).
                    return _state.DocumentObject != null
                        ? JsValue.FromObject(_state.DocumentObject)
                        : JsValue.Null;
                case "name":
                    return JsValue.From(_element.GetAttr("name") ?? _element.GetAttr("id") ?? "");
                case "id":
                    return JsValue.From(_element.GetAttr("id") ?? "");
                case "clip":
                {
                    // ONE clip object per layer wrapper — NS4 pages write
                    // layer.clip.right = 100 and expect the value to stick
                    // (a fresh object per read threw the write away).
                    if (Properties.TryGetValue("clip", out var cachedClip) &&
                        cachedClip.Type == JsType.Object)
                        return cachedClip;
                    var clip = new JsObject { Class = "LayerClip" };
                    double width = _element.Box?.BorderRect.Width ?? _element.Style?.Width ?? 0;
                    double height = _element.Box?.BorderRect.Height ?? _element.Style?.Height ?? 0;
                    string? raw = StyleDecls().TryGetValue("clip", out var c) ? c : null;
                    clip.Set("left", JsValue.From(0));
                    clip.Set("top", JsValue.From(0));
                    clip.Set("right", JsValue.From(width));
                    clip.Set("bottom", JsValue.From(height));
                    if (raw != null)
                    {
                        // rect(10 20 30 5) / rect(10,20,30,5)
                        var digits = raw.Replace("rect(", "").Replace(")", "")
                            .Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (digits.Length == 4)
                        {
                            clip.Set("left", JsValue.From(PxOf(digits[3])));
                            clip.Set("top", JsValue.From(PxOf(digits[0])));
                            clip.Set("right", JsValue.From(PxOf(digits[1])));
                            clip.Set("bottom", JsValue.From(PxOf(digits[2])));
                        }
                    }
                    var clipValue = JsValue.FromObject(clip);
                    Properties["clip"] = clipValue;
                    return clipValue;
                }
                case "moveTo":
                    return Fn("moveTo", (self, args) =>
                    {
                        double x = args.Length > 0 ? args[0].ToNumber() : 0;
                        double y = args.Length > 1 ? args[1].ToNumber() : 0;
                        WriteStyle(d =>
                        {
                            d["left"] = x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                            d["top"] = y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                        });
                        return JsValue.Undefined;
                    });
                case "moveBy":
                    return Fn("moveBy", (self, args) =>
                    {
                        double dx = args.Length > 0 ? args[0].ToNumber() : 0;
                        double dy = args.Length > 1 ? args[1].ToNumber() : 0;
                        WriteStyle(d =>
                        {
                            double x = PxOf(d.TryGetValue("left", out var l) ? l : null) + dx;
                            double y = PxOf(d.TryGetValue("top", out var t) ? t : null) + dy;
                            d["left"] = x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                            d["top"] = y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                        });
                        return JsValue.Undefined;
                    });
                case "resizeTo":
                    return Fn("resizeTo", (self, args) =>
                    {
                        double w = args.Length > 0 ? args[0].ToNumber() : 0;
                        double h = args.Length > 1 ? args[1].ToNumber() : 0;
                        WriteStyle(d =>
                        {
                            d["width"] = w.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                            d["height"] = h.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                        });
                        return JsValue.Undefined;
                    });
                case "resizeBy":
                    return Fn("resizeBy", (self, args) =>
                    {
                        double dw = args.Length > 0 ? args[0].ToNumber() : 0;
                        double dh = args.Length > 1 ? args[1].ToNumber() : 0;
                        WriteStyle(d =>
                        {
                            double w = PxOf(d.TryGetValue("width", out var wv) ? wv : null) + dw;
                            double h = PxOf(d.TryGetValue("height", out var hv) ? hv : null) + dh;
                            d["width"] = w.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                            d["height"] = h.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                        });
                        return JsValue.Undefined;
                    });
                case "moveAbove":
                case "moveBelow":
                    return Fn(name, (self, args) =>
                    {
                        if (args.Length == 0 || args[0].GetObject() is not NetscapeLayerObject other)
                            return JsValue.Undefined;
                        int ownZ = (int)ReadPx("z-index");
                        int otherZ = (int)other.ReadPx("z-index");
                        int target = name == "moveAbove" ? otherZ + 1 : otherZ - 1;
                        if (ownZ == target) target += name == "moveAbove" ? 1 : -1;
                        WriteStyle(d => d["z-index"] = target.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                        return JsValue.Undefined;
                    });
                case "load":
                    return Fn("load", (self, args) =>
                    {
                        // layer.load(url, width): re-fetch the layer's source.
                        // Documented limitation: the DOM bindings cannot drive
                        // the shell's resource fetcher — the URL is recorded
                        // as the layer's src attribute (and width as the
                        // inline width) and the page reflows.
                        string url = args.Length > 0 ? args[0].ToJsString() : "";
                        double width = args.Length > 1 ? args[1].ToNumber() : 0;
                        _element.SetAttr("src", url);
                        WriteStyle(d =>
                        {
                            if (args.Length > 1)
                                d["width"] = width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
                        });
                        return JsValue.Undefined;
                    });
            }
            return base.Get(name);
        }

        public override void Set(string name, JsValue value)
        {
            switch (name)
            {
                case "left":
                case "top":
                    WriteStyle(d => d[name] =
                        value.ToNumber().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px");
                    return;
                case "zIndex":
                    WriteStyle(d => d["z-index"] =
                        ((int)value.ToNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case "visibility":
                {
                    string v = value.ToJsString().Trim().ToLowerInvariant();
                    // NS4 show/hide/inherit map to CSS visible/hidden/inherit
                    string css = v switch
                    {
                        "show" => "visible",
                        "hide" => "hidden",
                        _ => "inherit"
                    };
                    WriteStyle(d => d["visibility"] = css);
                    return;
                }
                case "bgColor":
                    WriteStyle(d => d["background-color"] = value.ToJsString());
                    _state.Canvas?.RequestRerender();
                    return;
                case "background":
                    WriteStyle(d => d["background-image"] = value.ToJsString());
                    return;
                case "src":
                    // Writing src would reload the layer; without shell fetch
                    // reachability the attribute is recorded (documented).
                    _element.SetAttr("src", value.ToJsString());
                    return;
            }
            base.Set(name, value);
        }
    }
}