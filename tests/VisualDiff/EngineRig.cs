// EngineRig — headless Retro96 page pipeline mirroring the Form1 shell:
// parse (scripts executed, document.write spliced) → post-parse bindings →
// window/element onload → JS timer settle (50ms ticks like the shell's
// _jsTimer) → style resolve → layout → whole-document render → frame
// composition → crop to the viewport → PNG + element geometry.
using System.Text.Json;
using Retro96;
using Retro96.Drawing;
using Retro96.Engine;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;

namespace VisualDiff;

public sealed record GeomEntry(string Tag, string? Id, string? Name,
                                float X, float Y, float W, float H);

public sealed class EnginePageResult
{
    public string Page = "";
    public bool IsFrameset;
    public string PngPath = "";
    public List<GeomEntry> Geometry = new();
    public List<string> Notes = new();
}

public sealed class EngineRig
{
    private readonly string _baseUrl;
    private readonly int _vw, _vh;
    private readonly FontCache _fontCache = new();
    private readonly ResourceLoader _loader;
    private readonly Retro96.Engine.Network.HttpClient _http;
    private readonly CookieStore _cookies = new();

    public EngineRig(string baseUrl, int vw, int vh)
    {
        _baseUrl = baseUrl;
        _vw = vw; _vh = vh;
        // DETERMINISM: this environment's outbound network is host-lottery
        // (some hosts respond, some hang, some return transform-gated
        // bytes).  The rig must NEVER depend on that luck — the diff
        // measures rendering, not transport.  Every fetch goes through a
        // loopback-only client: external hosts fail INSTANTLY (HttpError),
        // which downstream produces the era network-error page inside
        // frames and the broken-image icon — exactly what the reference
        // browser's route-abort shows for the same URLs.
        _http = new LoopbackOnlyHttpClient();
        _loader = new ResourceLoader(_cookies, _http);
        InlineLayout.SetFontCache(_fontCache);
    }

    /// <summary>
    /// The engine's HTTP client, sandboxed to the loopback origin.  Any
    /// non-loopback host fails fast with HttpError — deterministic, and
    /// the failure mode the shell produces for genuinely unreachable
    /// hosts (era error page / broken icon).
    /// </summary>
    private sealed class LoopbackOnlyHttpClient : Retro96.Engine.Network.HttpClient
    {
        private static bool IsSandboxed(ParsedUrl url) =>
            url.Host is not ("127.0.0.1" or "localhost");

        public override Task<HttpResult> GetAsync(ParsedUrl url, CookieStore cookies,
            System.Threading.CancellationToken ct) =>
            IsSandboxed(url)
                ? Task.FromResult<HttpResult>(new HttpError(
                    "Connection blocked by VisualDiff harness (loopback-only)"))
                : base.GetAsync(url, cookies, ct);

        public override Task<HttpResult> PostAsync(ParsedUrl url, string formData,
            CookieStore cookies, System.Threading.CancellationToken ct) =>
            IsSandboxed(url)
                ? Task.FromResult<HttpResult>(new HttpError(
                    "Connection blocked by VisualDiff harness (loopback-only)"))
                : base.PostAsync(url, formData, cookies, ct);
    }

    public EnginePageResult Render(string pageName, string outPng)
    {
        var result = new EnginePageResult { Page = pageName };
        string url = _baseUrl + pageName;
        var parsedUrl = ParsedUrl.Parse(url);

        // Fetch the page through the engine's own HTTP client and decode
        // with the shell's BodyDecoder path (declared charset → sniff →
        // meta re-scan).  The rig used to read the file from disk as raw
        // Latin-1 — byte-identical for ASCII, but it bypassed the real
        // top-level pipeline the shell runs on every navigation.
        string html;
        {
            var fetch = _http.GetAsync(parsedUrl, _cookies,
                System.Threading.CancellationToken.None);
            if (!fetch.Wait(15000))
                throw new TimeoutException("page fetch timed out: " + pageName);
            if (fetch.Result is not HttpSuccess ok || ok.StatusCode != 200)
                throw new InvalidOperationException(
                    "page fetch failed: " + pageName + " — " + fetch.Result);
            html = BodyDecoder.Decode(ok.Body, ok.Charset);
        }

        // Per-page fetch budget reset — the shell calls Reset()/CancelAll()
        // at every navigation.  The rig shared ONE 200-fetch budget across
        // all pages, so once earlier pages spent it, every later page's
        // images failed with "Too many asset fetches" — a harness artifact,
        // not engine behaviour (theoldnet.html alone references ~180).
        _loader.CancelAll();

        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);
        var interpreter = new JsInterpreter(scope, null, _ => { }, _ => { });
        interpreter.RegisterRuntimeBuiltins();
        var canvas = new BrowserCanvas();
        var state = new DocumentBindingsState
        { Interpreter = interpreter, Canvas = canvas, LastModified = "" };
        interpreter.ElementWrapperHook = e => DomBindings.WrapElement(e, state);

        var doc = HtmlParser.Parse(html, parsedUrl, _cookies,
            (d, src, _) => RunInlineScript(d, src, interpreter, state));

        // Post-parse re-registration (pages without scripts still see
        // document/window) — then onload, exactly as Form1 does.
        state.Document = doc;
        DomBindings.RegisterAll(scope, doc, new NavigationHistory(), canvas, state);
        interpreter.RegisterRuntimeBuiltins();

        var win = state.WindowObject;
        if (win != null && win.Get("onload") is { Type: JsType.Function } onload)
            interpreter.CallHandler(onload, JsValue.FromObject(win));
        foreach (var elem in doc.ElementDescendants()
                     .Where(e => e.EventHandlers.ContainsKey("onload")))
            interpreter.FireEvent(elem, "onload");

        // JS timers settle — the shell ticks every 50ms; give scripts the
        // same 2s the reference browser gets before its screenshot.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            interpreter.TickTimers();
            Thread.Sleep(50);
        }
        interpreter.TickTimers();

        StyleResolver.Resolve(doc, _vw);

        // Image prefetch, exactly as the shell's PrefetchImagesAsync: the
        // renderer paints from the warmed cache (its inline fetches check
        // IsCompletedSuccessfully — a cold HTTP image is invisible by
        // design; the shell always prefetches first, so the rig must too).
        var imageCache = new ImageCache { CookieStore = _cookies };
        PrefetchImages(doc, parsedUrl, imageCache);

        var root = LayoutEngine.BuildLayoutTree(doc, _vw, _vh);

        result.IsFrameset = root.Descendants().Any(b => b.BoxType == BoxType.Frame);

        var renderer = new Renderer(_fontCache, imageCache, _loader);
        using var full = renderer.Render(root, doc, _fontCache, imageCache,
            _vw, _vh, 0f, 0f, null, true);

        // Frameset shells paint frame content the way BrowserCanvas.OnPaint
        // does: each frame's own document rendered at the frame box size and
        // blitted into the parent surface at the frame rect.
        if (result.IsFrameset)
        {
            using var surface = new Bitmap(Math.Max(1, _vw), Math.Max(1, _vh));
            using (var g = Graphics.FromImage(surface))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(full, 0f, 0f);
                ComposeFrames(g, root, doc, parsedUrl, imageCache, renderer);
            }
            CropAndSave(surface, outPng);
        }
        else
        {
            CropAndSave(full, outPng);
        }

        // Geometry: the UNION of every border-box an element generates, in
        // document order — the same shape as the reference browser's
        // getBoundingClientRect sweep.  The rig used to report the FIRST
        // box only, which under-measured inline elements: a <font> wrapping
        // several words reported one word fragment (26px) against
        // Chromium's whole-element rect (186px).  Inline boxes also
        // propagate up to their inline-level DOM ancestors — an <a> inside
        // a <font> still counts inside the font's rect, exactly like CSS
        // inline nesting.
        var rectsByElement = new Dictionary<DomElement, List<RectangleF>>();
        void AddRect(DomElement el, RectangleF r)
        {
            if (!rectsByElement.TryGetValue(el, out var list))
                rectsByElement[el] = list = new List<RectangleF>();
            list.Add(r);
        }
        foreach (var b in root.Descendants())
        {
            if (b.Element == null) continue;
            var r = b.BorderRect;
            if (r.Width <= 0.01f || r.Height <= 0.01f) continue;

            AddRect(b.Element, r);
            // Propagation is for TEXT fragments only — CSS inline nesting
            // fragments apply to text content.  A replaced child (image)
            // keeps its rect to itself: Chromium reports an <a> wrapping an
            // image as a small baseline-level inline box (not the image
            // rect), so propagating image rects up would trade one class of
            // noise for another.
            if (b.TextRun != null)
            {
                for (var a = b.Element.Parent; a is DomElement ae; a = a.Parent)
                {
                    if (ae.Style?.Display is not (DisplayValue.Inline or DisplayValue.InlineBlock))
                        break;                       // block container ends propagation
                    AddRect(ae, r);
                }
            }
        }
        foreach (var e in doc.ElementDescendants())
        {
            if (e.Style?.Display == DisplayValue.None) continue;
            if (!rectsByElement.TryGetValue(e, out var rects) || rects.Count == 0)
                continue;
            float x = rects.Min(r => r.X), y = rects.Min(r => r.Y);
            float w = rects.Max(r => r.Right) - x, h = rects.Max(r => r.Bottom) - y;
            if (w <= 0.01f || h <= 0.01f) continue;
            result.Geometry.Add(new GeomEntry(e.TagName,
                e.GetAttr("id"), e.GetAttr("name"),
                R1(x), R1(y), R1(w), R1(h)));
        }

        imageCache.Dispose();
        return result;
    }

    private void ComposeFrames(Graphics g, LayoutBox root, DomDocument parentDoc,
                               ParsedUrl parentUrl, ImageCache imageCache, Renderer renderer,
                               float offX = 0f, float offY = 0f)
    {
        foreach (var frameBox in root.Descendants()
                     .Where(b => b.BoxType == BoxType.Frame).ToList())
        {
            var el = frameBox.Element;
            string? src = el?.GetAttr("src");
            if (el == null || string.IsNullOrEmpty(src)) continue;

            // Per-frame JS context, exactly like the shell's
            // CreateFrameContext/RunFrameScript.
            var frameScope = new JsScope();
            JsRuntime.PopulateGlobalScope(frameScope);
            var frameInterpreter = new JsInterpreter(frameScope, null, _ => { }, _ => { });
            frameInterpreter.RegisterRuntimeBuiltins();
            var frameCanvas = new BrowserCanvas();
            var frameState = new DocumentBindingsState
            { Interpreter = frameInterpreter, Canvas = frameCanvas };
            frameInterpreter.ElementWrapperHook = e => DomBindings.WrapElement(e, frameState);

            // Load through the shell's REAL frame loader: scheme dispatch
            // (http/file/about), BodyDecoder charset handling, and era
            // error pages rendered INSIDE failed frames.  The rig used to
            // fetch raw bytes itself, decode as Latin-1 and silently SKIP
            // the frame on any failure — a blank rect where the shell shows
            // an error page (irc.html's external webchat iframe is the
            // live case).  12s wait bound: the engine's own 10s connect
            // timeout fires first, so the error page still lands.
            var loadTask = FrameLoader.LoadAsync(parentUrl.ToAbsolute(), src,
                Math.Max(1, (int)frameBox.Width), Math.Max(1, (int)frameBox.Height),
                _http, _cookies, System.Threading.CancellationToken.None,
                (d, s, _) => RunInlineScript(d, s, frameInterpreter, frameState));
            var swFrame = System.Diagnostics.Stopwatch.StartNew();
            bool loaded = loadTask.Wait(12000);
            swFrame.Stop();
            // Frame composition diagnostic (bracket-free: this environment's
            // output pipeline can swallow bracket sequences).
            Console.WriteLine($"    frame: {src} box={(int)frameBox.Width}x{(int)frameBox.Height}" +
                $" wait={loaded} {swFrame.ElapsedMilliseconds}ms" +
                (loaded && loadTask.Result != null
                    ? $" elems={loadTask.Result.Document.ElementDescendants().Count()}"
                    : " content=null"));
            if (!loaded) continue;
            var content = loadTask.Result;
            if (content == null) continue;   // empty src / about:blank stays blank

            var frameDoc = content.Document;
            var frameUrl = ParsedUrl.Parse(content.AbsoluteUrl);

            frameState.Document = frameDoc;
            DomBindings.RegisterAll(frameScope, frameDoc, new NavigationHistory(),
                frameCanvas, frameState);
            frameInterpreter.RegisterRuntimeBuiltins();
            var fwin = frameState.WindowObject;
            if (fwin != null && fwin.Get("onload") is { Type: JsType.Function } fol)
                frameInterpreter.CallHandler(fol, JsValue.FromObject(fwin));
            foreach (var elem in frameDoc.ElementDescendants()
                         .Where(e => e.EventHandlers.ContainsKey("onload")))
                frameInterpreter.FireEvent(elem, "onload");

            StyleResolver.Resolve(frameDoc, (int)frameBox.Width);
            PrefetchImages(frameDoc, frameUrl, imageCache);
            var frameRoot = LayoutEngine.BuildLayoutTree(
                frameDoc, (int)frameBox.Width, (int)frameBox.Height);

            var border = frameBox.BorderRect;
            // Page coordinates = this frame's rect shifted by the ANCESTOR
            // frame offsets.  A nested iframe's box coordinates live in its
            // OWN document's space — blitting them at raw page coordinates
            // dropped the iframe across sibling frames (the "iframe
            // overlapping the nav frame" diff on frames.html).
            float pageX = offX + border.X;
            float pageY = offY + border.Y;

            using var frameBmp = renderer.Render(frameRoot, frameDoc,
                _fontCache, imageCache, frameBox.Width, frameBox.Height,
                0f, 0f, null, true);

            float w = Math.Min(border.Width, frameBmp.Width);
            float h = Math.Min(border.Height, frameBmp.Height);
            if (w > 0 && h > 0)
            {
                var state = g.Save();
                g.SetClip(new RectangleF(pageX, pageY, w, h));
                g.DrawImage(frameBmp,
                    new RectangleF(pageX, pageY, w, h),
                    new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
                g.Restore(state);
            }

            // nested framesets recurse — WITH the cumulative offset, so an
            // iframe inside a frame composes inside that frame's rect.
            if (frameRoot.Descendants().Any(b => b.BoxType == BoxType.Frame))
                ComposeFrames(g, frameRoot, frameDoc, frameUrl, imageCache, renderer,
                    pageX, pageY);
        }
    }

    /// <summary>
    /// Mirrors Form1.PrefetchImagesAsync: img src/lowsrc, body background,
    /// and computed CSS background-image urls — fetched in PARALLEL (the
    /// shell does the same; one slow image must not block the queue) and
    /// awaited to completion before the render.
    /// </summary>
    private void PrefetchImages(DomDocument doc, ParsedUrl baseUrl, ImageCache imageCache)
    {
        var urls = new List<string>();
        foreach (var elem in doc.ElementDescendants())
        {
            string? raw = elem.TagName switch
            {
                "img" => elem.GetAttr("src") ?? elem.GetAttr("lowsrc"),
                "body" => elem.GetAttr("background"),
                _ => null
            };
            if (!string.IsNullOrEmpty(raw))
            {
                try { urls.Add(ImageCache.ResolveUrl(raw, baseUrl.ToAbsolute())); }
                catch { }
            }

            var bgCss = elem.Style?.BackgroundImage;
            if (!string.IsNullOrEmpty(bgCss) && bgCss != "none")
            {
                var bgUrl = Renderer.ParseCssUrl(bgCss);
                if (!string.IsNullOrEmpty(bgUrl))
                {
                    try { urls.Add(baseUrl.Resolve(bgUrl).ToAbsolute()); } catch { }
                }
            }
        }

        var fetches = urls.Distinct()
            .Select(u =>
            {
                try { return imageCache.GetAsync(u, _loader, default); }
                catch { return null; }
            })
            .Where(t => t != null)
            .ToArray();
        try { Task.WhenAll(fetches!).Wait(15000); }
        catch { /* a hung image must not kill the page render */ }
    }

    private void CropAndSave(Bitmap full, string outPng)
    {
        using var view = new Bitmap(Math.Max(1, _vw), Math.Max(1, _vh));
        using (var g = Graphics.FromImage(view))
        {
            g.Clear(Color.FromArgb(0xC0, 0xC0, 0xC0));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(full,
                new RectangleF(0, 0, Math.Min(_vw, full.Width), Math.Min(_vh, full.Height)),
                new RectangleF(0, 0, Math.Min(_vw, full.Width), Math.Min(_vh, full.Height)),
                GraphicsUnit.Pixel);
        }
        view.Save(outPng, ImageFormat.Png);
    }

    private static string RunInlineScript(DomDocument document, string scriptSource,
                                          JsInterpreter interpreter, DocumentBindingsState state)
    {
        state.Document = document;
        DomBindings.RegisterAll(interpreter.GlobalScope, document,
            new NavigationHistory(), state.Canvas, state);
        interpreter.RegisterRuntimeBuiltins();
        try { interpreter.ExecuteString(scriptSource); }
        catch { /* engine QA pages intentionally throw; keep going */ }
        string written = state.WriteBuffer.ToString();
        state.WriteBuffer.Clear();
        return written;
    }

    private static float R1(float v) => MathF.Round(v, 1);
}
