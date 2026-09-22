// ChromiumRig — the reference browser.  Loads the same pages from the same
// loopback origin, lets scripts/timers settle for the same 2 seconds the
// engine rig gives them, captures the 800x600 viewport and an element
// geometry sweep in document order (matching the engine rig's output shape).
using System.Text.Json;
using Microsoft.Playwright;

namespace VisualDiff;

public sealed class ChromiumRig : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IPage _page;
    private readonly int _vw, _vh;

    private ChromiumRig(IPlaywright playwright, IBrowser browser, IPage page, int vw, int vh)
    {
        _playwright = playwright; _browser = browser; _page = page;
        _vw = vw; _vh = vh;
    }

    public static async Task<ChromiumRig> CreateAsync(int vw, int vh)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // Cached driver mismatches the installed package version —
            // point straight at the known-good chromium build (verified in
            // scripts/pwtest).
            ExecutablePath = "/home/z/.cache/ms-playwright/chromium-1243/chrome-linux64/chrome",
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" },
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = vw, Height = vh },
            DeviceScaleFactor = 1,
        });

        // Determinism: this environment has no outbound network, and DNS
        // does not fail fast — it HANGS.  The engine never fetches external
        // script src (by design) and its socket client times out at a fixed
        // 10s, but the reference browser would sit on resolver luck, making
        // screenshots depend on how long DNS happened to stall.  Abort every
        // non-loopback request instantly so external subresources fail
        // identically on both sides of the diff (netscape1996's archive.org
        // wrappers, irc.html's webchat iframe, theoldnet's CDN images).
        await context.RouteAsync("**/*", route =>
        {
            if (route.Request.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                _ = route.ContinueAsync();
            else
                _ = route.AbortAsync();
        });

        var page = await context.NewPageAsync();
        return new ChromiumRig(playwright, browser, page, vw, vh);
    }

    public async Task<(string PngPath, List<GeomEntry> Geometry, bool IsFrameset)> RenderAsync(
        string pageName, string outPng)
    {
        string url = pageName; // callers pass the absolute loopback URL
        await _page.GotoAsync(url, new PageGotoOptions
        {
            // DOMContentLoaded, not Load: the era pages exercise
            // multipart server-push (/push.cgi) whose stream never ends,
            // so the load event never fires in the reference browser.
            // The settle window below gives images the same time the
            // engine rig gives them.
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });

        // Best-effort full load (images incl. redirects); a push stream
        // legitimately never finishes — ignore that timeout.
        try { await _page.WaitForLoadStateAsync(LoadState.Load,
                new PageWaitForLoadStateOptions { Timeout = 1500 }); }
        catch { /* push.cgi never loads — that IS the era behaviour */ }

        // Same settle window as the engine rig: 2s of real time for JS
        // timers (the era pages' setTimeout markers land in this window).
        await _page.WaitForTimeoutAsync(2000);

        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outPng,
            FullPage = false,           // exactly the viewport, like the engine crop
        });

        string json = await _page.EvaluateAsync<string>("() => JSON.stringify((() => {" +
            "const out = [];" +
            "const isFrameset = !!document.querySelector('frameset');" +
            "for (const el of document.querySelectorAll('*')) {" +
            //  Image-wrapping anchors: Chromium reports these as small
            //  baseline-level inline boxes (a quirk of inline-fragment
            //  rects), while the engine gives text-less wrappers no box at
            //  all.  Counting them here would skew tag-ordinal matching
            //  for every real text link after them — skip on both sides
            //  (the engine side skips them by construction).
            "  if (el.tagName.toLowerCase() === 'a' && el.querySelector('img')" +
            "      && !el.textContent.trim()) continue;" +
            "  const r = el.getBoundingClientRect();" +
            "  const cs = getComputedStyle(el);" +
            "  out.push({ tag: el.tagName.toLowerCase(), id: el.id || null," +
            "             name: el.getAttribute('name'), x: r.x, y: r.y," +
            "             w: r.width, h: r.height, disp: cs.display });" +
            "}" +
            "return { entries: out, isFrameset };" +
            "})())");

        var doc = JsonDocument.Parse(json).RootElement;
        var geometry = new List<GeomEntry>();
        foreach (var e in doc.GetProperty("entries").EnumerateArray())
        {
            if (e.GetProperty("disp").GetString() == "none") continue;
            geometry.Add(new GeomEntry(
                e.GetProperty("tag").GetString() ?? "",
                NullIfEmpty(e.GetProperty("id")),
                NullIfEmpty(e.GetProperty("name")),
                R1((float)e.GetProperty("x").GetDouble()),
                R1((float)e.GetProperty("y").GetDouble()),
                R1((float)e.GetProperty("w").GetDouble()),
                R1((float)e.GetProperty("h").GetDouble())));
        }
        bool isFrameset = doc.GetProperty("isFrameset").GetBoolean();
        return (outPng, geometry, isFrameset);
    }

    private static string? NullIfEmpty(JsonElement e) =>
        e.ValueKind == JsonValueKind.Null || e.ValueKind == JsonValueKind.Undefined
            ? null : (e.GetString() is { Length: > 0 } s ? s : null);

    private static float R1(float v) => MathF.Round(v, 1);

    public async ValueTask DisposeAsync()
    {
        try { await _page.CloseAsync(); } catch { }
        try { await _browser.CloseAsync(); } catch { }
        _playwright.Dispose();
    }
}
