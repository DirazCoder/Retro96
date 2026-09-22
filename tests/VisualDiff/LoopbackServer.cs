// LoopbackServer — deterministic era-style HTTP origin for the visual diff.
//
// Serves the html-websites QA pages at http://127.0.0.1:PORT/<page>.html and
// implements the 15 contracted "server route" behaviours images.html
// exercises (root-relative GIFs, 404 banner, 301 redirect, query string,
// XBM, server-push, flaky 503-then-200).  BOTH renderers — the Retro96
// engine and reference Chromium — load the SAME pages from this origin, so
// network behaviour is identical and the diff measures rendering, not
// transport luck.
using System.Net;
using System.Text;
using Retro96.Engine.Network;

namespace VisualDiff;

public sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _pagesDir;
    private readonly string _testdataDir;
    private readonly Dictionary<string, int> _routeHits = new();
    private Task? _loop;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public LoopbackServer(string pagesDir, string testdataDir)
    {
        _pagesDir = pagesDir;
        _testdataDir = testdataDir;

        // HttpListener has no port-0 wildcard; probe a few high ports.
        foreach (int port in new[] { 8964, 8971, 8988, 8999, 9013 })
        {
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                break;
            }
            catch (HttpListenerException) { _listener.Prefixes.Clear(); }
        }
        if (Port == 0)
            throw new InvalidOperationException("No loopback port available.");
    }

    public void Start() => _loop = Task.Run(() => ServeLoop());

    private async Task ServeLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (HttpListenerException) { return; }
            try { Handle(ctx); }
            catch { /* a bad route must never kill the origin */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        string query = ctx.Request.Url!.Query ?? "";

        _routeHits.TryGetValue(path, out int hits);
        _routeHits[path] = hits + 1;

        // ── contracted harness routes (images.html) ──────────────────
        switch (path)
        {
            case "/images/counter.gif":
                ServeFile(ctx, Path(_testdataDir, "counter.gif"), "image/gif"); return;
            case "/images/EFA.gif":
                ServeFile(ctx, Path(_testdataDir, "EFA.gif"), "image/gif"); return;
            case "/images/gilligan.gif":
                ServeFile(ctx, Path(_testdataDir, "gilligan.gif"), "image/gif"); return;
            case "/images/topper.gif":
                ServeFile(ctx, Path(_testdataDir, "topper.gif"), "image/gif"); return;
            case "/images/update.gif":
                ServeFile(ctx, Path(_testdataDir, "update.gif"), "image/gif"); return;
            case "/images/webring.gif":
                ServeFile(ctx, Path(_testdataDir, "webring.gif"), "image/gif"); return;
            case "/images/anistarbkgd.gif":
                ServeFile(ctx, Path(_testdataDir, "anistarbkgd.gif"), "image/gif"); return;
            case "/images/banner-ad3.jpg":
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/plain";
                Write(ctx, "not found");
                return;
            case "/query.gif":
                // identical bytes regardless of the query string — the
                // check is "query strings don't break the image path"
                ServeFile(ctx, Path(_testdataDir, "counter.gif"), "image/gif"); return;
            case "/redirect.gif":
                ctx.Response.StatusCode = 301;
                ctx.Response.RedirectLocation = "/images/counter.gif";
                ctx.Response.ContentLength64 = 0;
                return;
            case "/xbm.xbm":
                ctx.Response.ContentType = "image/x-xbitmap";
                Write(ctx, Xbm48x16);
                return;
            case "/push.cgi":
                // multipart/x-mixed-replace with ONE GIF part, then close.
                ctx.Response.ContentType = "multipart/x-mixed-replace;boundary=END";
                byte[] gif = File.ReadAllBytes(Path(_testdataDir, "counter.gif"));
                byte[] body = Encoding.ASCII.GetBytes(
                    "--END\r\nContent-Type: image/gif\r\n\r\n").Concat(gif)
                    .Concat(Encoding.ASCII.GetBytes("\r\n--END--\r\n")).ToArray();
                Write(ctx, body);
                return;
            case "/flaky1.gif":
            case "/flaky2.gif":
            case "/flaky3.gif":
                if (hits == 0)
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.Headers["Retry-After"] = "1";
                    Write(ctx, "temporarily unavailable");
                    return;
                }
                ServeFile(ctx, Path(_testdataDir, "counter.gif"), "image/gif");
                return;
        }

        // ── static page files ─────────────────────────────────────────
        // pagesDir first (the QA corpus), then testdataDir — the
        // real-world 1996 pages and their companion images
        // (nintendo-hallway.html + hall-*.html frame docs, dolekemp96's
        // logo.jpg / wbumper.gif, hall-features-main's one.jpg,
        // voyagersisland's anistarbkgd.gif …) all resolve from here, so
        // relative refs inside the corpus work exactly as they did on
        // their original 1996 origins.
        string safe = path.TrimStart('/');
        if (safe.Length == 0 || safe.Contains(".."))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        string file = Path(_pagesDir, safe);
        if (!File.Exists(file))
            file = Path(_testdataDir, safe);
        if (!File.Exists(file))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "text/plain";
            Write(ctx, "not found: " + path);
            return;
        }
        ServeFile(ctx, file, ContentTypeFor(safe, file));
    }

    private static string Path(params string[] parts) =>
        System.IO.Path.Combine(parts);

    private static string ContentTypeFor(string name, string filePath) =>
        name.EndsWith(".html") ? HtmlContentTypeFor(filePath)
        : name.EndsWith(".gif") ? "image/gif"
        : name.EndsWith(".jpg") || name.EndsWith(".jpeg") ? "image/jpeg"
        : name.EndsWith(".png") ? "image/png"
        : "application/octet-stream";

    private static readonly Dictionary<string, string> _htmlTypeCache = new();

    /// <summary>
    /// Serves .html with the document's OWN declared charset when it has
    /// one (dolekemp96 and voyagersisland declare windows-1252) — the
    /// same meta-sniff the engine's BodyDecoder performs, so both
    /// renderers decode the corpus identically.  Charset-less pages stay
    /// iso-8859-1, the 1996 default.
    /// </summary>
    private static string HtmlContentTypeFor(string filePath)
    {
        lock (_htmlTypeCache)
            if (_htmlTypeCache.TryGetValue(filePath, out string? cached))
                return cached;

        string type = "text/html; charset=iso-8859-1";
        try
        {
            string head = Encoding.GetEncoding("iso-8859-1")
                .GetString(File.ReadAllBytes(filePath));
            string? meta = BodyDecoder.ScanMetaCharset(head);
            if (!string.IsNullOrEmpty(meta))
                type = "text/html; charset=" + meta;
        }
        catch { /* keep the default */ }

        lock (_htmlTypeCache) _htmlTypeCache[filePath] = type;
        return type;
    }

    private static void ServeFile(HttpListenerContext ctx, string file, string type)
    {
        ctx.Response.ContentType = type;
        Write(ctx, File.ReadAllBytes(file));
    }

    private static void Write(HttpListenerContext ctx, string text) =>
        Write(ctx, Encoding.UTF8.GetBytes(text));

    private static void Write(HttpListenerContext ctx, byte[] body)
    {
        // Connection-per-response (HTTP/1.0-era semantics): multipart push
        // responses otherwise read as never-ending streams and the
        // reference browser's load event stalls 30s to timeout.
        ctx.Response.KeepAlive = false;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>A 48x16 checker XBM (the era's only bitmap format today's
    /// browsers dropped — the engine still decodes it).</summary>
    private static string Xbm48x16 =>
        "#define xbm_width 48\n" +
        "#define xbm_height 16\n" +
        "static char xbm_bits[] = {\n" +
        string.Join(",", Enumerable.Range(0, 96)
            .Select(i => "0x" + (((i / 6) + (i % 6)) % 2 == 0 ? "AA" : "55"))) +
        "};\n";

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
