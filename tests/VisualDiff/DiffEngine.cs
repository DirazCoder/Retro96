// DiffEngine — the comparison core.
//
// GEOMETRY diff (the original campaign contract): elements matched by id
// (when present) or by tag+ordinal in document order; rects compared with
// two thresholds — 4px "OK" (sub-pixel/rounding class) and 16px "WARN"
// (same ballpark, real divergence) — anything beyond 16px FAILs.  Colours,
// fonts and styling are ignored by design.
//
// PIXEL diff (new since the SkiaSharp migration — with GDI+ gone there is
// no platform noise, so a full-frame compare is meaningful): both PNGs are
// 800x600 BGRA; a pixel matches when every channel is within the per-
// channel tolerance (font rasterization differs between Skia and Chromium
// even at the same metrics).  Output: similarity score, mismatch map PNG,
// and a pass/warn/fail verdict with generous-but-honest thresholds.
using SkiaSharp;

namespace VisualDiff;

public sealed class GeometryDelta
{
    public string Key = "";
    public string Tag = "";
    public float Dx, Dy, Dw, Dh;
    public string Verdict = "";        // WARN / FAIL
}

public class PageReport
{
    public string Page = "";
    public bool IsFrameset;
    // geometry
    public int GeomTotal, GeomMatched, GeomOk, GeomWarn, GeomFail;
    public int MissingInEngine, MissingInChromium;
    public List<GeometryDelta> Deltas = new();
    // pixels
    public double PixelSimilarity;
    public string PixelVerdict = "";
    public long MismatchedPixels;
    public List<string> Notes = new();

    public string GeomVerdict =>
        GeomFail > 0 ? "FAIL" : GeomWarn > 0 ? "WARN" : "PASS";
}

public sealed class DiffEngine
{
    private readonly float _okPx, _warnPx;
    private readonly int _channelTolerance;
    private readonly double _passSimilarity, _warnSimilarity;

    public DiffEngine(float okPx = 4f, float warnPx = 16f,
                      int channelTolerance = 16,
                      double passSimilarity = 0.90, double warnSimilarity = 0.70)
    {
        _okPx = okPx; _warnPx = warnPx;
        _channelTolerance = channelTolerance;
        _passSimilarity = passSimilarity; _warnSimilarity = warnSimilarity;
    }

    // ── geometry ───────────────────────────────────────────────────

    /// <summary>id-bearing elements key on "#id"; everything else on
    /// tag[ordinal-among-same-tag] in document order.  Both engines build
    /// the same DOM from the same source, so ordinals align.</summary>
    public static List<(GeomEntry e, string key)> KeyedEntries(List<GeomEntry> list)
    {
        var counters = new Dictionary<string, int>();
        var result = new List<(GeomEntry, string)>();
        foreach (var e in list)
        {
            if (!string.IsNullOrEmpty(e.Id))
            {
                result.Add((e, "#" + e.Id));
                continue;
            }
            counters.TryGetValue(e.Tag, out int n);
            counters[e.Tag] = n + 1;
            result.Add((e, $"{e.Tag}[{n}]"));
        }
        return result;
    }

    public void DiffGeometry(PageReport report,
                             List<GeomEntry> engine, List<GeomEntry> chromium)
    {
        var engineKeys = KeyedEntries(engine);
        var chromMap = KeyedEntries(chromium)
            .GroupBy(k => k.key)
            .ToDictionary(g => g.Key, g => g.First().e);

        report.GeomTotal = engineKeys.Count;
        var seen = new HashSet<string>();
        foreach (var (e, key) in engineKeys)
        {
            seen.Add(key);
            if (!chromMap.TryGetValue(key, out var c))
            {
                report.MissingInChromium++;
                continue;
            }
            float dx = MathF.Abs(e.X - c.X), dy = MathF.Abs(e.Y - c.Y);
            float dw = MathF.Abs(e.W - c.W), dh = MathF.Abs(e.H - c.H);
            float worst = MathF.Max(MathF.Max(dx, dy), MathF.Max(dw, dh));
            string verdict = worst <= _okPx ? "OK" : worst <= _warnPx ? "WARN" : "FAIL";
            report.GeomMatched++;
            if (verdict == "OK") report.GeomOk++;
            else if (verdict == "WARN") report.GeomWarn++;
            else report.GeomFail++;
            if (verdict != "OK" && report.Deltas.Count < 400)
                report.Deltas.Add(new GeometryDelta
                {
                    Key = key, Tag = e.Tag,
                    Dx = R1(e.X - c.X), Dy = R1(e.Y - c.Y),
                    Dw = R1(e.W - c.W), Dh = R1(e.H - c.H),
                    Verdict = verdict,
                });
        }
        report.MissingInEngine = chromMap.Count(k => !seen.Contains(k.Key));
    }

    // ── pixels ─────────────────────────────────────────────────────

    public void DiffPixels(PageReport report, string enginePng, string chromiumPng,
                           string diffPng)
    {
        using var a = SKBitmap.Decode(enginePng);
        using var b = SKBitmap.Decode(chromiumPng);
        if (a == null || b == null)
        {
            report.PixelVerdict = "ERROR";
            report.Notes.Add("PNG decode failed");
            return;
        }

        int w = Math.Min(a.Width, b.Width);
        int h = Math.Min(a.Height, b.Height);
        long total = (long)w * h;
        long mismatch = 0;

        using var diff = new SKBitmap(
            new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(diff))
        {
            canvas.Clear(new SKColor(0, 0, 0, 0));
            unsafe
            {
                byte* pa = (byte*)a.GetPixels();
                byte* pb = (byte*)b.GetPixels();
                byte* pd = (byte*)diff.GetPixels();
                int ra = a.RowBytes, rb = b.RowBytes, rd = diff.RowBytes;
                for (int y = 0; y < h; y++)
                {
                    byte* rowA = pa + (long)y * ra;
                    byte* rowB = pb + (long)y * rb;
                    byte* rowD = pd + (long)y * rd;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        int db = Math.Abs(rowA[i] - rowB[i]);
                        int dg = Math.Abs(rowA[i + 1] - rowB[i + 1]);
                        int dr = Math.Abs(rowA[i + 2] - rowB[i + 2]);
                        if (db <= _channelTolerance && dg <= _channelTolerance &&
                            dr <= _channelTolerance)
                        {
                            // match: dimmed engine pixel
                            rowD[i] = (byte)(rowA[i] / 2);
                            rowD[i + 1] = (byte)(rowA[i + 1] / 2);
                            rowD[i + 2] = (byte)(rowA[i + 2] / 2);
                            rowD[i + 3] = 255;
                        }
                        else
                        {
                            mismatch++;
                            rowD[i] = 0; rowD[i + 1] = 0;
                            rowD[i + 2] = 255; rowD[i + 3] = 255;
                        }
                    }
                }
            }
        }
        using var data = diff.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = File.Create(diffPng)) data.SaveTo(fs);

        report.MismatchedPixels = mismatch;
        report.PixelSimilarity = total > 0 ? 100.0 * (total - mismatch) / total : 0;
        report.PixelVerdict =
            report.PixelSimilarity >= _passSimilarity * 100 ? "PASS" :
            report.PixelSimilarity >= _warnSimilarity * 100 ? "WARN" : "FAIL";
    }

    private static float R1(float v) => MathF.Round(v, 1);
}
