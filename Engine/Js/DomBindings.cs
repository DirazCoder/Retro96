using System;
using System.Windows.Forms;
using System.Diagnostics;
using Retro96;
using Retro96.Engine;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96.Engine.Js;

public static class DomBindings
{
    /// <summary>
    /// Register all DOM Level 0 bindings into the global scope.
    /// This method should be called after JsRuntime.PopulateGlobalScope(). this one toke me a while chrs mate
    /// </summary>
    public static void RegisterAll(JsScope globalScope, DomDocument document, NavigationHistory history, BrowserCanvas canvas)
    {
        RegisterWindow(globalScope, document, history, canvas);
        RegisterNavigator(globalScope);
        RegisterDocument(globalScope, document, canvas);
        RegisterHistory(globalScope, history);
        RegisterLocation(globalScope, document, canvas);

        // Set cross-object references: window.document and window.location point to global objects
        var windowObj = globalScope.Get("window").GetObject();
        windowObj.Set("document", globalScope.Get("document"));
        windowObj.Set("location", globalScope.Get("location"));
    }

    private static void RegisterWindow(JsScope scope, DomDocument doc, NavigationHistory history, BrowserCanvas canvas)
    {
        var windowObj = new JsObject();

        // window.alert(msg)
        windowObj.Set("alert", CreateNativeFunction((self, args) => {
            string msg = args.Length > 0 ? args[0].ToJsString() : "";
            MessageBox.Show(msg, "Retro96", MessageBoxButtons.OK);
            return JsValue.Undefined;
        }, scope, "alert"));

        // window.confirm(msg)
        windowObj.Set("confirm", CreateNativeFunction((self, args) => {
            string msg = args.Length > 0 ? args[0].ToJsString() : "";
            var result = MessageBox.Show(msg, "Retro96", MessageBoxButtons.YesNo);
            return JsValue.From(result == DialogResult.Yes);
        }, scope, "confirm"));

        // window.prompt(msg, def)
        windowObj.Set("prompt", CreateNativeFunction((self, args) => {
            string msg = args.Length > 0 ? args[0].ToJsString() : "Enter value:";
            string def = args.Length > 1 ? args[1].ToJsString() : "";
            return JsValue.From(def);
        }, scope, "prompt"));

        // window.open(url, target, features)
        windowObj.Set("open", CreateNativeFunction((self, args) => {
            string url = args.Length > 0 ? args[0].ToJsString() : "";
            if (!string.IsNullOrEmpty(url))
            {
                canvas.OpenNewWindow(url);
            }
            return JsValue.Undefined;
        }, scope, "open"));

        // window.close()
        windowObj.Set("close", CreateNativeFunction((self, args) => {
            var form = canvas.FindForm();
            if (form != null)
            {
                form.Close();
            }
            return JsValue.Undefined;
        }, scope, "close"));

        // window.scrollTo(x, y)
        windowObj.Set("scrollTo", CreateNativeFunction((self, args) => {
            if (args.Length >= 2)
            {
                int x = (int)args[0].ToNumber();
                int y = (int)args[1].ToNumber();
                canvas.ScrollTo(x, y);
            }
            return JsValue.Undefined;
        }, scope, "scrollTo"));

        // window.scrollBy(dx, dy)
        windowObj.Set("scrollBy", CreateNativeFunction((self, args) => {
            if (args.Length >= 2)
            {
                int dx = (int)args[0].ToNumber();
                int dy = (int)args[1].ToNumber();
                canvas.ScrollBy(dx, dy);
            }
            return JsValue.Undefined;
        }, scope, "scrollBy"));

        // window.innerWidth
        windowObj.Set("innerWidth", CreateNativeFunction((self, args) => {
            return JsValue.From(canvas.ClientSize.Width);
        }, scope, "innerWidth"));

        // window.innerHeight
        windowObj.Set("innerHeight", CreateNativeFunction((self, args) => {
            return JsValue.From(canvas.ClientSize.Height);
        }, scope, "innerHeight"));

        scope.Define("window", JsValue.FromObject(windowObj));
    }

    private static void RegisterNavigator(JsScope scope)
    {
        var navigatorObj = new JsObject();
        navigatorObj.Set("userAgent", JsValue.From("Mozilla/3.0 (compatible; Retro96/1.0)"));
        navigatorObj.Set("appName", JsValue.From("Netscape"));
        navigatorObj.Set("appVersion", JsValue.From("3.0 (Win95; I)"));
        navigatorObj.Set("language", JsValue.From("en"));
        navigatorObj.Set("platform", JsValue.From("Win32"));
        navigatorObj.Set("cookieEnabled", JsValue.From(true));
        scope.Define("navigator", JsValue.FromObject(navigatorObj));
    }

    private static void RegisterDocument(JsScope scope, DomDocument doc, BrowserCanvas canvas)
    {
        var docObj = new JsObject();

        // document.title (get/set)
        docObj.Set("title", CreateNativeFunction((self, args) => {
            if (args.Length > 0)
            {
                doc.Title = args[0].ToJsString();
                return JsValue.Undefined;
            }
            return JsValue.From(doc.Title);
        }, scope, "title"));

        // document.URL idk how i got this to work somehow
        docObj.Set("URL", JsValue.From(doc.BaseUrl?.ToAbsolute() ?? "about:blank"));

        // document.referrer
        docObj.Set("referrer", JsValue.From(""));

        // document.write(str)
        docObj.Set("write", CreateNativeFunction((self, args) => {
            if (args.Length > 0)
            {
                string str = args[0].ToJsString();
                Debug.WriteLine($"[DOM] document.write: {str}");
            }
            return JsValue.Undefined;
        }, scope, "write"));

        // document.writeln(str)
        docObj.Set("writeln", CreateNativeFunction((self, args) => {
            if (args.Length > 0)
            {
                string str = args[0].ToJsString() + "\n";
                Debug.WriteLine($"[DOM] document.writeln: {str}");
            }
            return JsValue.Undefined;
        }, scope, "writeln"));

        // Placeholder collections for DOM Level 0
        docObj.Set("forms", JsValue.FromObject(new JsObject()));
        docObj.Set("images", JsValue.FromObject(new JsObject()));
        docObj.Set("links", JsValue.FromObject(new JsObject()));
        docObj.Set("anchors", JsValue.FromObject(new JsObject()));

        scope.Define("document", JsValue.FromObject(docObj));
    }

// navigationhistory doesnt exist just yet wait for muhammad ali to add the new file
    private static void RegisterHistory(JsScope scope, NavigationHistory history)
    {
        var historyObj = new JsObject();

        // history.length
        historyObj.Set("length", JsValue.From(history.Count));

        // history.back()
        historyObj.Set("back", CreateNativeFunction((self, args) => {
            if (history.CanGoBack)
            {
                history.GoBack();
            }
            return JsValue.Undefined;
        }, scope, "back"));

        // history.forward()
        historyObj.Set("forward", CreateNativeFunction((self, args) => {
            if (history.CanGoForward)
            {
                history.GoForward();
            }
            return JsValue.Undefined;
        }, scope, "forward"));

        // history.go(n)
        historyObj.Set("go", CreateNativeFunction((self, args) => {
            if (args.Length > 0)
            {
                int n = (int)args[0].ToNumber();
                history.Go(n);
            }
            return JsValue.Undefined;
        }, scope, "go"));

        scope.Define("history", JsValue.FromObject(historyObj));
    }

    private static void RegisterLocation(JsScope scope, DomDocument doc, BrowserCanvas canvas)
    {
        var locationObj = new JsObject();
        var currentUrl = doc.BaseUrl ?? ParsedUrl.Parse("about:blank");

        // location.href (get/set)
        locationObj.Set("href", CreateNativeFunction((self, args) => {
            if (args.Length > 0)
            {
                string url = args[0].ToJsString();
                canvas.NavigateTo(url);
                return JsValue.Undefined;
            }
            return JsValue.From(currentUrl.ToAbsolute());
        }, scope, "href"));

        // location.hostname
        locationObj.Set("hostname", JsValue.From(currentUrl.Host ?? ""));

        // location.pathname
        locationObj.Set("pathname", JsValue.From(currentUrl.Path ?? "/"));

        // location.search
        locationObj.Set("search", JsValue.From(currentUrl.Query ?? ""));

        // location.hash
        locationObj.Set("hash", JsValue.From(currentUrl.Fragment ?? ""));

        // location.port
        locationObj.Set("port", JsValue.From(currentUrl.Port > 0 ? currentUrl.Port.ToString() : ""));

        // location.reload()
        locationObj.Set("reload", CreateNativeFunction((self, args) => {
            canvas.NavigateTo(currentUrl.ToAbsolute());
            return JsValue.Undefined;
        }, scope, "reload"));

        // location.replace(url)
        locationObj.Set("replace", CreateNativeFunction((self, args) => {
            if (args.Length > 0)
            {
                string url = args[0].ToJsString();
                canvas.NavigateToReplace(url);
            }
            return JsValue.Undefined;
        }, scope, "replace"));

        scope.Define("location", JsValue.FromObject(locationObj));
    }

    private static JsValue CreateNativeFunction(Func<JsValue, JsValue[], JsValue> impl, JsScope scope, string name)
    {
        var func = new JsFunction(impl, scope, name);
        return JsValue.FromFunction(func);
    }
}
