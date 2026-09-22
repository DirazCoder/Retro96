// PageProbe — deep diagnostic dump for a single test page.
// Parses → resolves styles → lays out → (optionally) renders to PNG,
// then prints every anomaly category we track.  Used to baseline the
// three new test pages (voyagersisland / nintendo-hallway / acme).
using Retro96.Drawing;
using Retro96.Engine;
using Retro96.Engine.Html;
using Retro96.Engine.Css;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace LayoutLab;

public static class PageProbe
{
    public static void Run(string[] args)
    {
        string file = args[0];
        int vw = args.Length > 1 ? int.Parse(args[1]) : 800;
        int vh = args.Length > 2 ? int.Parse(args[2]) : 600;
        bool renderPng = args.Length > 3 && args[3] == "png";
        bool raw = args.Contains("raw");

        string html = File.ReadAllText(file);
        string url = "file://" + Path.GetFullPath(file);
        // debug: how does ParsedUrl see this base?
        try
        {
            var pb = ParsedUrl.Parse(url);
            Console.WriteLine($"  base: scheme={pb.Scheme} host={pb.Host} path={pb.Path.Trunc(70)}");
        }
        catch (Exception ex) { Console.WriteLine($"  base parse THREW: {ex.GetType().Name}: {ex.Message}"); }
        var doc = HtmlParser.Parse(html, ParsedUrl.Parse(url), new CookieStore());
        StyleResolver.Resolve(doc, vw);
        var root = LayoutEngine.BuildLayoutTree(doc, vw, vh);

        Console.WriteLine($"PAGE {file}  viewport {vw}x{vh}");
        Console.WriteLine($"  elements: {doc.ElementDescendants().Count()}");
        var body = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        Console.WriteLine($"  body: {(body == null ? "MISSING" : "present")}  bgcolor={body?.GetAttr("bgcolor")} background={body?.GetAttr("background")}");

        // ── frames ──
        var frames = root.Descendants().Where(b => b.BoxType == BoxType.Frame).ToList();
        Console.WriteLine($"  FRAME boxes: {frames.Count}");
        foreach (var f in frames)
            Console.WriteLine($"    name={f.Element?.GetAttr("name")!,-15} src={f.Element?.GetAttr("src")!.Trunc(60),-62} rect=({f.X:0.#},{f.Y:0.#} {f.Width:0.#}x{f.Height:0.#})");

        // ── images ──
        var imgs = doc.ElementDescendants().Where(e => e.TagName == "img").ToList();
        Console.WriteLine($"  IMG elements: {imgs.Count}");
        var loader = new ResourceLoader(new CookieStore());
        var cache = new ImageCache { CookieStore = new CookieStore() };
        foreach (var im in imgs)
        {
            string src = im.GetAttr("src") ?? "";
            string abs = ImageCache.ResolveUrl(src, url);
            string w = im.GetAttr("width") ?? "-", h = im.GetAttr("height") ?? "-";
            // probe load
            string state = "n/a";
            try
            {
                cache.GetAsync(abs, loader, CancellationToken.None).Wait(3000);
                state = cache.IsBroken(abs) ? "BROKEN" : (cache.IsLoaded(abs) ? "loaded" : "unknown");
            }
            catch (Exception ex) { state = "THROW:" + ex.GetType().Name; }
            var box = root.Descendants().FirstOrDefault(b => b.Element == im);
            Console.WriteLine($"    abs={abs.Trunc(60),-62} src={src.Trunc(28),-30} wh=({w}x{h}) box={(box == null ? "NO-BOX" : $"{box.Width:0.#}x{box.Height:0.#}")} {state}");
        }

        // ── controls ──
        foreach (var tag in new[] { "input", "select", "textarea", "button" })
        {
            var ctrls = doc.ElementDescendants().Where(e => e.TagName == tag).ToList();
            if (ctrls.Count == 0) continue;
            Console.WriteLine($"  {tag.ToUpper()} elements: {ctrls.Count}");
            foreach (var c in ctrls)
            {
                var box = root.Descendants().FirstOrDefault(b => b.Element == c);
                string type = c.GetAttr("type") ?? "";
                string nm = c.GetAttr("name") ?? "";
                Console.WriteLine($"    {type}/{nm} box={(box == null ? "NO-BOX" : $"{box.Width:0.#}x{box.Height:0.#} @({box.X:0.#},{box.Y:0.#})")}");
            }
        }

        // ── style checks ──
        var styled = doc.ElementDescendants().Where(e => e.HasAttr("class") || e.Style != null).ToList();
        Console.WriteLine($"  styled elements (class/style): {styled.Count}");
        foreach (var s in styled.Take(12))
            Console.WriteLine($"    <{s.TagName} class={s.GetAttr("class")}> style: fg={s.Style?.Color} bg={s.Style?.BackgroundColor} display={s.Style?.Display} pos={s.Style?.Position} font={s.Style?.FontWeight}/{s.Style?.FontSize}");

        // links: computed color
        var links = doc.ElementDescendants().Where(e => e.TagName == "a").Take(4).ToList();
        foreach (var l in links)
            Console.WriteLine($"    <a href={l.GetAttr("href")?.Trunc(30)}> style: fg={l.Style?.Color} underline={(l.Style?.TextDecoration ?? 0).HasFlag(TextDecoration.Underline)}");

        // ── text anomalies ──
        var boxes = root.Descendants().Where(b => !string.IsNullOrEmpty(b.TextRun)).ToList();
        int joined = 0;
        static bool IsWordish(string? t) => t != null && t.Length > 0 && t.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '\'');
        foreach (var g in boxes.Where(b => b.TextRun != " " && b.Width > 0 && b.Height > 0).GroupBy(b => MathF.Round(b.Y)))
        {
            var line = g.OrderBy(b => b.X).ToList();
            for (int i = 1; i < line.Count; i++)
            {
                float gap = line[i].X - (line[i - 1].X + line[i - 1].Width);
                if (IsWordish(line[i - 1].TextRun) && IsWordish(line[i].TextRun) && gap <= 0.01f)
                {
                    if (joined < 8) Console.WriteLine($"    JOINED: '{line[i-1].TextRun}'+'{line[i].TextRun}'");
                    joined++;
                }
            }
        }
        Console.WriteLine($"  joined words: {joined}");

        var frac = boxes.Count(b => b.Width > 0 && MathF.Abs(b.Y - MathF.Round(b.Y)) > 0.01f);
        Console.WriteLine($"  fractional text rows: {frac}");

        // horizontal overflow: visible leaf boxes beyond viewport
        var over = root.Descendants().Where(b => b.Width > 0 && (b.BoxType == BoxType.Inline || !string.IsNullOrEmpty(b.TextRun) || b.Element?.TagName is "img" or "hr" or "table")
                     && b.X + b.Width > vw + 2).Take(8).ToList();
        Console.WriteLine($"  leaf boxes beyond right edge (> {vw}): {over.Count}");
        foreach (var o in over)
            Console.WriteLine($"    <{o.Element?.TagName}> '{o.TextRun?.Trunc(20)}' right={o.X + o.Width:0.#}");

        // zero-width visible text
        var zero = boxes.Count(b => b.TextRun!.Trim().Length > 0 && b.Width <= 0.01f);
        Console.WriteLine($"  zero-width text boxes: {zero}");

        // display:none elements that produced boxes (should be none)
        var hiddenElems = doc.ElementDescendants().Where(e => e.Style?.Display == DisplayValue.None).ToList();
        int leaked = hiddenElems.Count(e => root.Descendants().Any(b => b.Element == e));
        Console.WriteLine($"  display:none elements: {hiddenElems.Count}, boxes leaked: {leaked}");

        // named anchors
        var anchors = doc.ElementDescendants().Where(e => e.TagName == "a" && e.HasAttr("name")).ToList();
        Console.WriteLine($"  named anchors: {anchors.Count} -> {string.Join(",", anchors.Select(a => a.GetAttr("name")).Take(10))}");
        var anchorBoxes = anchors.Where(a => root.Descendants().Any(b => b.Element == a)).Count();
        Console.WriteLine($"  named anchors with boxes: {anchorBoxes}");

        // map/area
        var maps = doc.ElementDescendants().Where(e => e.TagName is "map" or "area").ToList();
        Console.WriteLine($"  map/area elements: {maps.Count}");

        // ── render ──
        if (renderPng)
        {
            var renderer = new Renderer(new FontCache(), cache, loader);
            using var bmp = renderer.Render(root, doc, new FontCache(), cache, vw, vh, 0, 0, null, true);
            string outPng = Path.ChangeExtension(file, null) + ".probe.png";
            bmp.Save(outPng, ImageFormat.Png);
            Console.WriteLine($"  rendered: {outPng} ({bmp.Width}x{bmp.Height})");
        }

        if (raw)
        {
            Console.WriteLine("  ── raw box tree (first 120 visible) ──");
            int n = 0;
            Dump(root, 0, ref n);
        }
        cache.Dispose();
        loader.Dispose();
    }

    static void Dump(LayoutBox b, int depth, ref int n)
    {
        if (n++ > 3000) return;
        string txt = string.IsNullOrEmpty(b.TextRun) ? "" : " '" + b.TextRun.Trunc(30) + "'";
        Console.WriteLine(new string(' ', Math.Min(depth * 2, 40)) +
            $"{b.BoxType} <{b.Element?.TagName ?? "text"}> {b.Width:0.#}x{b.Height:0.#}@({b.X:0.#},{b.Y:0.#}){txt}");
        foreach (var c in b.Children) Dump(c, depth + 1, ref n);
    }
}

public static class StrExt
{
    public static string Trunc(this string? s, int len) =>
        s == null ? "" : (s.Length <= len ? s : s[..len] + "…");
}
