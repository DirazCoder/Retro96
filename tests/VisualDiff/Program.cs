// VisualDiff — Retro96 engine vs reference Chromium, same pages, same
// loopback origin, same 2s script settle.  Two verdicts per page:
//   GEOMETRY  element rects, 4px OK / 16px WARN / >16px FAIL  (ignore
//             colours & styles — the original campaign contract)
//   PIXEL     full-frame compare with per-channel tolerance (possible
//             since the GDI+ → SkiaSharp migration removed all platform
//             rendering noise)
//
// Usage:
//   dotnet run [-- --pages <dir> --out <dir> --testdata <dir>]
// Defaults resolve to the repo layout (tests/html-websites, tests/…/out).
using System.Text;
using System.Text.Json;
using VisualDiff;

Console.OutputEncoding = Encoding.UTF8;

string root = FindRepoRoot(AppContext.BaseDirectory);
string pagesDir = Arg("--pages") ?? Path.Combine(root, "tests", "html-websites");
string testdataDir = Arg("--testdata") ?? Path.Combine(root, "testdata");
string outDir = Arg("--out") ?? Path.Combine(root, "tests", "VisualDiff", "out");
int vw = 800, vh = 600;

Directory.CreateDirectory(outDir);
foreach (string sub in new[] { "engine", "chromium", "diff" })
    Directory.CreateDirectory(Path.Combine(outDir, sub));

string? Arg(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "retro96")) &&
            Directory.Exists(Path.Combine(dir.FullName, "tests")))
            return dir.FullName;
    throw new InvalidOperationException("repo root not found above " + start);
}

// pages to compare — EVERY html in the project: the QA corpus in
// tests/html-websites AND the real-world 1996 corpus in testdata/
// (acme-cybercorp, detector, dolekemp96, the Nintendo Hallway frameset
// + its three frame docs, irc, netscape1996, theoldnet,
// voyagersisland).  FRAMESET ROOTS (frames.html, 1996-corporate.html,
// nintendo-hallway.html) get the frame-composition render on the engine
// side but no geometry verdict (frame documents live in separate
// coordinate spaces in the reference browser).
string[] framesetRoots =
    { "frames.html", "1996-corporate.html", "nintendo-hallway.html" };
var pages = new[] { pagesDir, testdataDir }
    .SelectMany(d => Directory.GetFiles(d, "*.html"))
    .Select(p => Path.GetFileName(p))!
    .Distinct(StringComparer.Ordinal)
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToList();
if (Arg("--only") is { Length: > 0 } only)
    pages = pages.Where(p => p.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

Console.WriteLine($"VisualDiff — {pages.Count} pages from {pagesDir} + {testdataDir}");
Console.WriteLine($"out: {outDir}\n");

var reports = new List<PageReport>();

// ── pass 1: engine renders (loopback server up so http images resolve) ──
using (var server = new LoopbackServer(pagesDir, testdataDir))
{
    server.Start();
    Console.WriteLine($"loopback origin: {server.BaseUrl}");
    var rig = new EngineRig(server.BaseUrl, vw, vh);
    foreach (string page in pages)
    {
        string png = Path.Combine(outDir, "engine", Path.ChangeExtension(page, ".png"));
        try
        {
            // The rig fetches the page from the loopback origin through
            // the engine's own HTTP client — the shell's real pipeline.
            var r = rig.Render(page, png);
            reports.Add(new PageReport
            {
                Page = page,
                IsFrameset = r.IsFrameset,
                Notes = r.Notes,
                _enginePng = png,
                _engineGeometry = r.Geometry,
            });
            Console.WriteLine($"  engine: {page,-28} geom={r.Geometry.Count,3} frameset={r.IsFrameset}");
        }
        catch (Exception ex)
        {
            reports.Add(new PageReport
            {
                Page = page,
                Notes = { "ENGINE THREW: " + ex.Message },
                _enginePng = "",
            });
            Console.WriteLine($"  engine: {page,-28} THREW {ex.GetType().Name}: {ex.Message}");
        }
    }
}

// ── pass 2: reference chromium ────────────────────────────────────────
await using (var chromium = await ChromiumRig.CreateAsync(vw, vh))
{
    using (var server = new LoopbackServer(pagesDir, testdataDir))
    {
        server.Start();
        foreach (var report in reports)
        {
            if (report._enginePng.Length == 0) continue;
            string png = Path.Combine(outDir, "chromium",
                Path.ChangeExtension(report.Page, ".png"));
            try
            {
                var (p, geom, isFrameset) = await chromium.RenderAsync(
                    server.BaseUrl + report.Page, png);
                report._chromiumPng = png;
                report._chromiumGeometry = geom;
                if (report.IsFrameset != isFrameset)
                    report.Notes.Add($"frameset detection differs (engine={report.IsFrameset}, chromium={isFrameset})");
            }
            catch (Exception ex)
            {
                report.Notes.Add("CHROMIUM THREW: " + ex.Message);
                Console.WriteLine($"  chromium: {report.Page,-28} THREW {ex.Message}");
            }
        }
    }
}

// ── pass 3: diff ───────────────────────────────────────────────────────
bool dumpGeom = args.Contains("--dumpgeom");
if (dumpGeom)
{
    foreach (var report in reports)
    {
        Console.WriteLine($"\n=== GEOM DUMP {report.Page} ===");
        Console.WriteLine("--- engine ---");
        foreach (var e in report._engineGeometry)
            Console.WriteLine($"  {e.Tag,-10} id={e.Id ?? "-"} name={e.Name ?? "-"} x={e.X} y={e.Y} w={e.W} h={e.H}");
        Console.WriteLine("--- chromium ---");
        foreach (var e in report._chromiumGeometry ?? new())
            Console.WriteLine($"  {e.Tag,-10} id={e.Id ?? "-"} name={e.Name ?? "-"} x={e.X} y={e.Y} w={e.W} h={e.H}");
    }
}
var differ = new DiffEngine(okPx: 4f, warnPx: 16f);
foreach (var report in reports)
{
    if (report._chromiumPng is not { Length: > 0 }) continue;

    if (!report.IsFrameset && report._engineGeometry.Count > 0)
        differ.DiffGeometry(report, report._engineGeometry, report._chromiumGeometry);

    differ.DiffPixels(report, report._enginePng, report._chromiumPng,
        Path.Combine(outDir, "diff", Path.ChangeExtension(report.Page, ".png")));
}

// ── report ─────────────────────────────────────────────────────────────
WriteConsole(reports);
WriteJson(reports, Path.Combine(outDir, "visualdiff-report.json"));
WriteMarkdown(reports, Path.Combine(outDir, "visualdiff-report.md"));

int fails = reports.Count(r =>
    (r._chromiumPng?.Length > 0 || r._enginePng?.Length > 0) &&
    ((r.GeomVerdict == "FAIL" && !r.IsFrameset) || r.PixelVerdict == "FAIL"));
Console.WriteLine($"\n{(fails == 0 ? "ALL PAGES within thresholds" : fails + " page(s) FAIL")}");
return fails == 0 ? 0 : 1;

// ═════════════════════════════════════════════════════════════════════

static void WriteConsole(List<PageReport> reports)
{
    Console.WriteLine("\n┌─ RESULTS ──────────────────────────────────────────────────────────┐");
    Console.WriteLine($"{"page",-28} {"geom",8} {"miss",9} {"pixel",14} verdict");
    Console.WriteLine(new string('─', 74));
    foreach (var r in reports)
    {
        string geom = r.IsFrameset ? "n/a*" :
            r._chromiumPng is { Length: > 0 }
            ? $"{r.GeomOk}/{r.GeomMatched} {r.GeomVerdict}" : "-";
        string miss = r.MissingInChromium + "|" + r.MissingInEngine;
        string pix = r._chromiumPng is { Length: > 0 }
            ? $"{r.PixelSimilarity:0.0}% {r.PixelVerdict}" : "-";
        string verdict = "OK";
        if (r.PixelVerdict == "FAIL" || (r.GeomVerdict == "FAIL" && !r.IsFrameset)) verdict = "FAIL";
        else if (r.PixelVerdict == "WARN" || r.GeomVerdict == "WARN") verdict = "WARN";
        if (r.Notes.Any(n => n.StartsWith("ENGINE THREW") || n.StartsWith("CHROMIUM THREW"))) verdict = "ERROR";
        Console.WriteLine($"{r.Page,-28} {geom,8} {miss,9} {pix,14} {verdict}");
    }
    Console.WriteLine("* frameset roots: pixel diff only (frame docs have separate coordinates)");
}

static void WriteJson(List<PageReport> reports, string path)
{
    var options = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(path, JsonSerializer.Serialize(reports.Select(r => new
    {
        page = r.Page,
        isFrameset = r.IsFrameset,
        geometry = new
        {
            total = r.GeomTotal, matched = r.GeomMatched,
            ok = r.GeomOk, warn = r.GeomWarn, fail = r.GeomFail,
            missingInChromium = r.MissingInChromium,
            missingInEngine = r.MissingInEngine,
            verdict = r.GeomVerdict,
            deltas = r.Deltas.Select(d => new
            { key = d.Key, tag = d.Tag, dx = d.Dx, dy = d.Dy, dw = d.Dw, dh = d.Dh, verdict = d.Verdict })
        },
        pixels = new
        {
            similarity = Math.Round(r.PixelSimilarity, 2),
            mismatched = r.MismatchedPixels,
            verdict = r.PixelVerdict
        },
        notes = r.Notes
    }), options));
    Console.WriteLine("\nJSON  → " + path);
}

static void WriteMarkdown(List<PageReport> reports, string path)
{
    var sb = new StringBuilder();
    sb.AppendLine("# VisualDiff report — Retro96 engine vs reference Chromium");
    sb.AppendLine();
    sb.AppendLine("Same pages, same loopback origin, same 2s script settle, 800x600.  ");
    sb.AppendLine("Geometry verdicts ignore colour/style by design (4px OK / 16px WARN).  ");
    sb.AppendLine("Pixel similarity is per-channel tolerance 16 — fonts rasterize differently");
    sb.AppendLine("between Skia and Chromium even at identical metrics, so a page that is");
    sb.AppendLine("geometrically perfect still scores below 100%.");
    sb.AppendLine();
    sb.AppendLine("| page | geometry (OK/matched) | missing eng\\|chr | pixel similarity | verdict |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var r in reports)
    {
        string geom = r.IsFrameset ? "n/a (frameset)" :
            r._chromiumPng is { Length: > 0 }
            ? $"{r.GeomOk}/{r.GeomMatched} — {r.GeomVerdict}" : "—";
        string miss = $"{r.MissingInChromium}\\|{r.MissingInEngine}";
        string pix = r._chromiumPng is { Length: > 0 }
            ? $"{r.PixelSimilarity:0.0}% — {r.PixelVerdict}" : "—";
        string verdict = "OK";
        if (r.PixelVerdict == "FAIL" || (r.GeomVerdict == "FAIL" && !r.IsFrameset)) verdict = "FAIL";
        else if (r.PixelVerdict == "WARN" || r.GeomVerdict == "WARN") verdict = "WARN";
        if (r.Notes.Any(n => n.StartsWith("ENGINE THREW") || n.StartsWith("CHROMIUM THREW"))) verdict = "ERROR";
        sb.AppendLine($"| {r.Page} | {geom} | {miss} | {pix} | {verdict} |");
    }
    sb.AppendLine();

    var notable = reports.Where(r => r.Deltas.Count > 0).ToList();
    if (notable.Count > 0)
    {
        sb.AppendLine("## Geometry deltas (WARN/FAIL, worst 40 per page)");
        sb.AppendLine();
        foreach (var r in notable)
        {
            sb.AppendLine($"### {r.Page}");
            sb.AppendLine();
            sb.AppendLine("| element | dx | dy | dw | dh | verdict |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var d in r.Deltas.OrderByDescending(d =>
                         Math.Max(Math.Abs(d.Dx), Math.Max(Math.Abs(d.Dy),
                             Math.Max(Math.Abs(d.Dw), Math.Abs(d.Dh))))).Take(40))
                sb.AppendLine($"| `{d.Key}` ({d.Tag}) | {d.Dx} | {d.Dy} | {d.Dw} | {d.Dh} | {d.Verdict} |");
            sb.AppendLine();
        }
    }
    var notes = reports.Where(r => r.Notes.Count > 0).ToList();
    if (notes.Count > 0)
    {
        sb.AppendLine("## Notes");
        sb.AppendLine();
        foreach (var r in notes)
            foreach (var n in r.Notes)
                sb.AppendLine($"- **{r.Page}** — {n}");
        sb.AppendLine();
    }
    File.WriteAllText(path, sb.ToString());
    Console.WriteLine("MD    → " + path);
}

/// <summary>Internal plumbing fields (excluded from the JSON report).</summary>
internal sealed class PageReport : VisualDiff.PageReport
{
    public string _enginePng = "";
    public string? _chromiumPng;
    public List<GeomEntry> _engineGeometry = new();
    public List<GeomEntry> _chromiumGeometry = new();
}
