using Retro96.Drawing;
using Retro96.Engine;
using Retro96.Engine.Html;
using Retro96.Engine.Css;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;
using Retro96.Engine.Dom;
using LayoutLab;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0)
{
    // Real font measurement — the probe is meaningless without the shared
    // font cache (natural control sizes, table min/pref widths, everything).
    InlineLayout.SetFontCache(new FontCache());
    PageProbe.Run(args);
    return;
}

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures++;
}

var fc = new FontCache();
InlineLayout.SetFontCache(fc);

void DumpAndVerifyErrorPage(string name, Func<string> html)
{
    Console.WriteLine($"\n=== {name} ===");
    var doc = HtmlParser.Parse(html(), null, null, null);
    StyleResolver.Resolve(doc, 640);
    var root = LayoutEngine.BuildLayoutTree(doc, 640, 4000);

    // all text boxes in document order
    var boxes = root.Descendants().Where(b => !string.IsNullOrEmpty(b.TextRun)).ToList();

    // 1. No zero-gap between adjacent words on the same line (space-drop bug)
    int joined = 0;
    static bool IsWordish(string? t) =>
        t != null && t.Length > 0 && t.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '\'');
    foreach (var g in boxes.Where(b => b.TextRun != " " && b.Width > 0 && b.Height > 0)
                           .GroupBy(b => MathF.Round(b.Y)))
    {
        var line = g.OrderBy(b => b.X).ToList();
        for (int i = 1; i < line.Count; i++)
        {
            float gap = line[i].X - (line[i - 1].X + line[i - 1].Width);
            if (IsWordish(line[i - 1].TextRun) && IsWordish(line[i].TextRun) && gap <= 0.01f)
            {
                Console.WriteLine($"      JOINED: '{line[i-1].TextRun}'+'{line[i].TextRun}' gap={gap}");
                joined++;
            }
        }
    }
    Check("no joined words (missing spaces)", joined == 0, $"{joined} joined");

    // 2. No tofu-prone chars reach the layout
    var tofu = boxes.Where(b => b.TextRun!.Any(c => c >= '\u2500' && c <= '\u27BF') &&
                                b.TextRun!.Any(c => c >= '\u2E80')).ToList();
    Check("no unmapped high glyphs", tofu.Count == 0);

    // 3. Integer Y rows
    var frac = boxes.Count(b => b.Width > 0 && MathF.Abs(b.Y - MathF.Round(b.Y)) > 0.01f);
    Check("integer text rows", frac == 0, $"{frac} fractional");

    // 4. The [Reload] footer group stays together on one line
    var reloadBoxes = boxes.Where(b => b.TextRun!.Contains("Reload")).ToList();
    var closeBr = boxes.Where(b => b.TextRun == "]").ToList();
    bool reloadOk = true;
    foreach (var rb in reloadBoxes)
    {
        // a "]" box on the NEXT line below the Reload box = split link
        var splitBr = closeBr.FirstOrDefault(cb => cb.Y > rb.Y + 5 && MathF.Abs(cb.X - (rb.X + rb.Width)) < 3);
        if (splitBr != null) reloadOk = false;
    }
    Check("[Reload] not split across lines", reloadOk);

    // 5. Body text alignment: the first <p>'s first line starts at the
    //    paragraph's left edge (not centered).
    var p1 = boxes.FirstOrDefault(b => b.Element?.TagName == "p" && b.TextRun != " " && b.Width > 5);
    if (p1 != null)
    {
        // find the paragraph's containing block left edge: min X across
        // the first line's words must be <= first word X + 2
        var sameLine = boxes.Where(b => MathF.Abs(b.Y - p1.Y) < 2 && b.Width > 0).OrderBy(b => b.X).ToList();
        float firstWordX = sameLine[0].X;
        float lastRight = sameLine[^1].X + sameLine[^1].Width;
        // in a 640 viewport the li content band is ~[52..532]; a centered
        // single short line would start well right of 52.  If the first
        // line of the first paragraph starts within 8px of the block's
        // left edge AND the paragraph has >1 line, alignment is Left.
        var pLines = boxes.Where(b => b.Element == p1.Element && b.TextRun != " " && b.Width > 0)
                          .Select(b => MathF.Round(b.Y)).Distinct().ToList();
        bool startsLeft = firstWordX - 52f < 8f || pLines.Count == 1;
        Check("body content left-aligned", startsLeft, $"firstX={firstWordX:0.#} lines={pLines.Count}");
    }

    // 6. Dropped-space boxes are never at (0,0)
    var originBoxes = boxes.Where(b => b.TextRun == " " && b.X == 0 && b.Y == 0 && b.Width == 0).ToList();
    Check("wrap-dropped spaces positioned", originBoxes.Count == 0, $"{originBoxes.Count} at origin");
}

DumpAndVerifyErrorPage("NetworkError", () => ErrorPage.NetworkError("http://nonexistent.invalid/page.html", "Name or service not known"));
DumpAndVerifyErrorPage("NotFound", () => ErrorPage.NotFound("http://example.com/missing.html"));
DumpAndVerifyErrorPage("LocalFileNotFound", () => ErrorPage.LocalFileNotFound("/home/user/missing.html"));

// ─── Glyph substitution ───
Console.WriteLine("\n=== Glyph substitution (CyberSpace-style) ===");
{
    string cyber = "<html><body bgcolor=\"#000000\">" +
        "<a href=\"#\">&#9654; Link one</a><br>" +
        "<a href=\"#\">&#9632; Link two</a><br>" +
        "<font color=\"#00ffff\">Navigation [X]&#9608;</font><br>" +
        "<font color=\"#00ffff\">Hit Counter&#9608;</font><br>" +
        "&#9834; Mute Track<br>" +
        "plain &mdash; dash &bull; bullet &copy; 2026" +
        "</body></html>";
    var doc = HtmlParser.Parse(cyber, null, null, null);
    StyleResolver.Resolve(doc, 640);
    var root = LayoutEngine.BuildLayoutTree(doc, 640, 2000);
    var runs = root.Descendants().Where(b => !string.IsNullOrEmpty(b.TextRun)).Select(b => b.TextRun).ToList();
    string all = string.Join("|", runs);
    Check("arrow substituted", !all.Contains('\u25B6') && all.Contains(">| |Link"));
    Check("block char substituted", !all.Contains('\u2588'));
    Check("note substituted", !all.Contains('\u266A'));
    Check("em dash kept", all.Contains('\u2014'), $"'{all}'");
    Check("bullet kept", all.Contains('\u2022'));
    Console.WriteLine($"      runs: {all}");
}

// ─── Control sizing + table clamp ───
Console.WriteLine("\n=== Controls in tables ===");
{
    string form = "<html><body>" +
        "<table border=\"1\" width=\"300\"><tr>" +
        "<td width=\"100\">Name:</td>" +
        "<td><input type=\"text\" size=\"30\" name=\"q\"></td>" +
        "</tr><tr><td>Go:</td><td><input type=\"submit\" value=\"Search the entire index now\"></td></tr></table>" +
        "</body></html>";
    var doc = HtmlParser.Parse(form, null, null, null);
    StyleResolver.Resolve(doc, 640);
    var root = LayoutEngine.BuildLayoutTree(doc, 640, 2000);

    var input = root.Descendants().FirstOrDefault(b => b.Element?.TagName == "input" && b.Element.GetAttr("name") == "q");
    Check("input natural width computed at build", input != null && input.Width > 100f, $"w={input?.Width:0.#}");
    var submit = root.Descendants().FirstOrDefault(b => b.Element?.TagName == "input" && b.Element.GetAttr("type") == "submit");
    Check("submit label-sized", submit != null && submit.Width > 80f, $"w={submit?.Width:0.#}");

    // find the cell containing the input; the control must not exceed the
    // cell's border rect right edge
    var cellBox = root.Descendants().FirstOrDefault(b => b.BoxType == BoxType.TableCell && b.Descendants().Contains(input!));
    if (cellBox != null && input != null)
    {
        float cellRight = cellBox.X + cellBox.PaddingLeft + cellBox.BorderLeft + cellBox.Width;
        float inputRight = input.X + input.Width + input.BorderLeft + input.BorderRight;
        Check("input within cell", inputRight <= cellRight + 1f, $"inputRight={inputRight:0.#} cellRight={cellRight:0.#}");
    }
}

// ─── UTF-8 sniff (mirror of Form1.SniffByteCharset) ───
Console.WriteLine("\n=== Charset sniff ===");
{
    string Sniff(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) return "utf-8";
        bool saw = false; int i = 0;
        while (i < body.Length)
        {
            byte b = body[i];
            if (b < 0x80) { i++; continue; }
            int n = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : -1;
            if (n < 0 || i + n > body.Length) return "iso-8859-1";
            for (int k = 1; k < n; k++) if ((body[i + k] & 0xC0) != 0x80) return "iso-8859-1";
            if (n == 2 && b < 0xC2) return "iso-8859-1";
            saw = true; i += n;
        }
        return saw ? "utf-8" : "iso-8859-1";
    }
    byte[] emDashUtf8 = System.Text.Encoding.UTF8.GetBytes("Too many visitors — please try again");
    Check("utf-8 em dash body sniffs utf-8", Sniff(emDashUtf8) == "utf-8");
    string decoded = System.Text.Encoding.GetEncoding(Sniff(emDashUtf8)).GetString(emDashUtf8);
    Check("em dash round-trips", decoded.Contains('\u2014'), decoded);
    byte[] latin1 = new byte[] { 0x54, 0x6F, 0x6F, 0xE2, 0x20, 0x6D };   // 'Tooâ m' — 0xE2 then space
    Check("latin1 body stays latin1", Sniff(latin1) == "iso-8859-1");
    Check("pure ascii sniffs latin1 (harmless)", Sniff(System.Text.Encoding.ASCII.GetBytes("hello")) == "iso-8859-1");
}

// ─── Hidden inputs occupy no space (line pass) ───
Console.WriteLine("\n=== Hidden inputs ===");
{
    // five hidden fields between the label and the visible input — the
    // EasyRecommend pattern; hidden boxes must stay 0x0 through the LINE
    // pass (natural-size + control chrome used to re-inflate them to
    // 4px x ~28px each, shifting the visible fields right).
    string form = "<html><body><form>" +
        "<input type=\"hidden\" name=\"a\"><input type=\"hidden\" name=\"b\">" +
        "<input type=\"hidden\" name=\"c\"><input type=\"hidden\" name=\"d\">" +
        "<input type=\"hidden\" name=\"e\">" +
        "Email: <input type=\"text\" name=\"f\" size=\"20\">" +
        "</form></body></html>";
    var doc = HtmlParser.Parse(form, null, null, null);
    StyleResolver.Resolve(doc, 640);
    var root = LayoutEngine.BuildLayoutTree(doc, 640, 2000);
    var hidden = root.Descendants().Where(b => b.Element?.TagName == "input" &&
        b.Element?.GetAttr("type") == "hidden").ToList();
    Check("hidden inputs stay 0x0 through line layout",
        hidden.Count == 5 && hidden.All(h => h.Width <= 0.01f && h.Height <= 0.01f),
        $"{hidden.Count} boxes, max {hidden.Max(h => MathF.Max(h.Width, h.Height)):0.#}px");

    // natural size: hidden must report none
    var hiddenEl = doc.ElementDescendants().First(e => e.GetAttr("name") == "a");
    Check("ControlNaturalSize reports no size for hidden",
        !InlineLayout.ControlNaturalSize(hiddenEl, hiddenEl.Style, out _, out _));
}

// ─── align=middle / valign=center synonyms ───
Console.WriteLine("\n=== HTML 3.0 align synonyms ===");
{
    string page = "<html><body><table><tr>" +
        "<td align=\"middle\" valign=\"center\">X</td>" +
        "</tr></table></body></html>";
    var doc = HtmlParser.Parse(page, null, null, null);
    StyleResolver.Resolve(doc, 640);
    var td = doc.ElementDescendants().First(e => e.TagName == "td");
    Check("align=middle centers (HTML 3.0 synonym)",
        td.Style?.TextAlign == Retro96.Engine.Css.TextAlign.Center);
    Check("valign=center is middle (HTML 3.0 synonym)",
        td.Style?.VerticalAlign == VerticalAlign.Middle);
}

// ─── noscript visibility gate ───
Console.WriteLine("\n=== noscript gate ===");
{
    string page = "<html><body><noscript><img src=\"counter.gif\"></noscript></body></html>";
    var withJs = HtmlParser.Parse(page, null, new Retro96.Engine.Network.CookieStore(),
        (d, s) => "");
    StyleResolver.Resolve(withJs, 640);
    var ns = withJs.ElementDescendants().First(e => e.TagName == "noscript");
    Check("noscript hidden when scripting enabled",
        ns.Style?.Display == DisplayValue.None);

    var noJs = HtmlParser.Parse(page, null, new Retro96.Engine.Network.CookieStore());
    StyleResolver.Resolve(noJs, 640);
    var ns2 = noJs.ElementDescendants().First(e => e.TagName == "noscript");
    Check("noscript visible when scripting disabled",
        ns2.Style?.Display != DisplayValue.None);
}

Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASS" : failures + " FAILURES")}");
