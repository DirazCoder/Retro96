// The 55 pre-existing regression checks, ported verbatim from the console
// runner to xUnit facts (one fact per original Test* method).
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Js;
using Retro96.Engine.Network;
using Retro96.Engine.Css;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;
using Retro96.Drawing;
using Retro96;

namespace RetroTests;

public class EngineRegressionTests
{
    [Fact]
    public void ParagraphsStayBlockLevelAndBoldInlineTextPaintsBold()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>.bold-link { font-weight: bold; }</style></head><body><p id='first'>MMMMMM " +
            "<b id='bold'>MMMMMM</b> " +
            "<a href='#' id='plain-link'>MMMMMM</a> " +
            "<a href='#' id='link'><b id='bold-link'>MMMMMM</b></a> " +
            "<a href='#' class='bold-link' id='css-bold-link'>MMMMMM</a></p>" +
            "<p id='second'>Following paragraph</p></body></html>");
        var first = doc.ElementDescendants().First(e => e.GetAttr("id") == "first");
        var second = doc.ElementDescendants().First(e => e.GetAttr("id") == "second");
        var bold = doc.ElementDescendants().First(e => e.GetAttr("id") == "bold");
        var plainLink = doc.ElementDescendants().First(e => e.GetAttr("id") == "plain-link");
        var boldLink = doc.ElementDescendants().First(e => e.GetAttr("id") == "bold-link");
        var cssBoldLink = doc.ElementDescendants().First(e => e.GetAttr("id") == "css-bold-link");
        var plainText = root.Descendants().First(b => b.Element?.TagName == "p" &&
            b.TextRun?.Contains("MMMMMM") == true);
        var boldText = root.Descendants().First(b => b.Element == bold &&
            b.TextRun?.Contains("MMMMMM") == true);
        var plainLinkText = root.Descendants().First(b => b.Element == plainLink &&
            b.TextRun?.Contains("MMMMMM") == true);
        var boldLinkText = root.Descendants().First(b => b.Element == boldLink &&
            b.TextRun?.Contains("MMMMMM") == true);
        var cssBoldLinkText = root.Descendants().First(b => b.Element == cssBoldLink &&
            b.TextRun?.Contains("MMMMMM") == true);

        Check.That(first.Style?.Display == DisplayValue.Block &&
                   LayoutHarness.BoxOf(root, first)?.BoxType == BoxType.Block,
            "paragraph has a block-level layout box");
        Check.That(LayoutHarness.BoxOf(root, second)!.Y >=
                   LayoutHarness.BoxOf(root, first)!.BorderRect.Bottom,
            "following paragraph starts after the first paragraph");
        Check.That(bold.Style?.FontWeight >= FontWeightValue.Bold &&
                   boldText.Element?.Style?.FontWeight >= FontWeightValue.Bold,
            "B text keeps its bold computed style");
        Check.That(boldLink.Style?.FontWeight >= FontWeightValue.Bold &&
                   boldLinkText.Element?.Style?.FontWeight >= FontWeightValue.Bold,
            "bold text inside a link keeps its bold computed style");
        Check.That(cssBoldLink.Style?.FontWeight >= FontWeightValue.Bold,
            "author CSS bold weight applies to the link itself");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        static int CountInk(Retro96.Drawing.Bitmap bitmap, LayoutBox box)
        {
            int count = 0;
            int left = Math.Max(0, (int)MathF.Floor(box.X));
            int top = Math.Max(0, (int)MathF.Floor(box.Y));
            int right = Math.Min(bitmap.Width, (int)MathF.Ceiling(box.X + box.Width));
            int bottom = Math.Min(bitmap.Height, (int)MathF.Ceiling(box.Y + box.Height));
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A > 0 && (pixel.R < 220 || pixel.G < 220 || pixel.B < 220))
                        count++;
                }
            return count;
        }

        int plainInk = CountInk(bitmap, plainText);
        Check.That(CountInk(bitmap, boldText) > plainInk,
            "B text paints heavier than the same regular text",
            $"regular={plainInk}, bold={CountInk(bitmap, boldText)}");
        int plainLinkInk = CountInk(bitmap, plainLinkText);
        Check.That(CountInk(bitmap, boldLinkText) > plainLinkInk,
            "bold linked text paints heavier than regular linked text",
            $"plain-link={plainLinkInk}, bold-link={CountInk(bitmap, boldLinkText)}");
        Check.That(CountInk(bitmap, cssBoldLinkText) > plainLinkInk,
            "CSS-bold link text paints heavier than regular linked text",
            $"plain-link={plainLinkInk}, css-bold-link={CountInk(bitmap, cssBoldLinkText)}");
        Check.Done();
    }

    [Fact]
    public void ZeroMarginLineHeightOneHeadingFlowsAtItsBorderBottom()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "body { margin: 0; } h1 { margin: 0; line-height: 1; }" +
            "</style></head><body><h1 id='heading'>Heading</h1>" +
            "<div id='after'>Following content</div></body></html>");
        var heading = doc.ElementDescendants().First(e => e.GetAttr("id") == "heading");
        var following = doc.ElementDescendants().First(e => e.GetAttr("id") == "after");

        Check.That(heading.Box != null, "heading has a layout box");
        Check.That(following.Box != null, "following block has a layout box");
        if (heading.Box != null && following.Box != null)
        {
            Check.That(Math.Abs(following.Box.Y - heading.Box.BorderRect.Bottom) < 0.1f,
                "zero-margin following block starts at the heading border-box bottom",
                $"heading bottom={heading.Box.BorderRect.Bottom}, following y={following.Box.Y}");
        }
        Check.Done();
    }

    [Fact]
    public void FocusRulesResolveAndRevertForFieldsAndLinks()
    {
        var (doc, _) = LayoutHarness.Parse(
            "<html><head><style>" +
            "input, a { background-color:#FFFFEE; } " +
            "input:focus { background-color:#FFFFCC; } " +
            "a:focus { color:#CC0000; }" +
            "</style></head><body><input id='field'><a id='link' href='#'>link</a></body></html>");
        var field = doc.ElementDescendants().First(e => e.GetAttr("id") == "field");
        var link = doc.ElementDescendants().First(e => e.GetAttr("id") == "link");

        doc.FocusedElement = field;
        StyleResolver.Resolve(doc, 800);
        Check.That(field.Style?.BackgroundColor == Color.FromArgb(0xFF, 0xFF, 0xCC),
            "focused field receives its focus background");
        doc.FocusedElement = null;
        StyleResolver.Resolve(doc, 800);
        Check.That(field.Style?.BackgroundColor == Color.FromArgb(0xFF, 0xFF, 0xEE),
            "blurred field returns to its ordinary background");

        doc.FocusedElement = link;
        StyleResolver.Resolve(doc, 800);
        Check.That(link.Style?.Color == Color.FromArgb(0xCC, 0x00, 0x00),
            "anchor :focus rule uses the focused element");
        doc.FocusedElement = null;
        StyleResolver.Resolve(doc, 800);
        Check.That(link.Style?.Color != Color.FromArgb(0xCC, 0x00, 0x00),
            "anchor :focus rule stops matching after blur");
        Check.Done();
    }

    [Fact]
    public void FirstLineAndGeneratedBeforeUseTheirPseudoStyles()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "h2 { color:#003366; } h2:before { content:'> '; color:#CC6600; } " +
            "p:first-line { font-weight:bold; }" +
            "</style></head><body><h2>Heading</h2><p>Good evening and welcome to the page.</p></body></html>");
        var heading = doc.ElementDescendants().First(e => e.TagName == "h2");
        var paragraph = doc.ElementDescendants().First(e => e.TagName == "p");
        var marker = LayoutHarness.TextBoxContaining(root, ">");
        var firstWord = LayoutHarness.TextBoxContaining(root, "Good");

        Check.That(heading.Style?.GeneratedBefore?.Color == Color.FromArgb(0xCC, 0x66, 0x00),
            "h2:before computes the authored orange color",
            heading.Style?.GeneratedBefore?.Color.ToString() ?? "(missing)");
        Check.That(marker?.StyleOverride?.Color == Color.FromArgb(0xCC, 0x66, 0x00),
            "generated marker retains its own color",
            marker?.StyleOverride?.Color.ToString() ?? "(missing)");
        Check.That(paragraph.Style?.FirstLineStyle?.FontWeight == FontWeightValue.Bold,
            "p:first-line computes a bold first-line style");
        Check.That(firstWord?.StyleOverride?.FontWeight == FontWeightValue.Bold,
            "first rendered word receives the first-line style",
            firstWord?.StyleOverride?.FontWeight.ToString() ?? "(missing)");
        Check.Done();
    }

    [Fact]
    public void FirstLineStyleAppliesToParagraphsInsideTableCells()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>p:first-line { font-weight:bold; } " +
            "a:link { color:#123456; } .normal { font-weight:normal; }</style></head>" +
            "<body><table><tr><td><p>Check out the <a href='#'>About Me</a> section " +
            "<a class='normal' href='#'>normal</a></p></td></tr></table></body></html>");
        var paragraph = doc.ElementDescendants().First(e => e.TagName == "p");
        var firstWord = LayoutHarness.TextBoxContaining(root, "Check");
        var link = doc.ElementDescendants().First(e =>
            e.TagName == "a" && e.GetAttr("class") == null);
        var linkText = LayoutHarness.TextBoxContaining(root, "About");
        var normalText = LayoutHarness.TextBoxContaining(root, "normal");

        Check.That(paragraph.Style?.FirstLineStyle?.FontWeight == FontWeightValue.Bold,
            "table paragraph computes a bold first-line style");
        Check.That(firstWord?.StyleOverride?.FontWeight == FontWeightValue.Bold,
            "table paragraph first rendered word receives the first-line style",
            firstWord?.StyleOverride?.FontWeight.ToString() ?? "(missing)");
        Check.That(linkText?.StyleOverride?.FontWeight == FontWeightValue.Bold,
            "About Me link's first word inherits the pseudo-element weight",
            linkText?.StyleOverride?.FontWeight.ToString() ?? "(missing)");
        Check.That(linkText?.StyleOverride?.Color == link.Style?.Color,
            "first-line styling preserves the link's own color",
            linkText?.StyleOverride?.Color.ToString() ?? "(missing)");
        Check.That(normalText?.StyleOverride == null &&
                   normalText?.Element?.Style?.FontWeight == FontWeightValue.Normal,
            "an inline element's explicit normal weight remains authoritative",
            normalText?.Element?.Style?.FontWeight.ToString() ?? "(missing)");
        Check.Done();
    }

    [Fact]
    public void InlineMarginsAndInlineBlockChildrenParticipateInLayout()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>.spaced { margin-right:15px; } " +
            ".box { display:inline-block; width:120px; height:40px; }</style></head>" +
            "<body><p><span class='spaced'>one</span><span id='next'>two</span> " +
            "<span id='ib' class='box'>Inline-Block</span></p></body></html>");
        var first = LayoutHarness.TextBoxContaining(root, "one");
        var second = LayoutHarness.TextBoxContaining(root, "two");
        var inlineBlock = doc.ElementDescendants().First(e => e.GetAttr("id") == "ib");
        var inlineBlockBox = LayoutHarness.BoxOf(root, inlineBlock);
        var inlineBlockText = LayoutHarness.TextBoxContaining(root, "Inline-Block");

        Check.That(first != null && second != null &&
                   second.X - first.BorderRect.Right >= 14f,
            "inline margin-right contributes to the following text position",
            $"gap={second?.X - first?.BorderRect.Right:0.#}");
        Check.That(inlineBlockBox?.BoxType == BoxType.InlineBlock &&
                   inlineBlockText?.Width > 0f && inlineBlockText.Height > 0f &&
                   inlineBlockText.X >= inlineBlockBox.ContentRect.Left &&
                   inlineBlockText.Y >= inlineBlockBox.ContentRect.Top &&
                   inlineBlockText.BorderRect.Right <= inlineBlockBox.ContentRect.Right + 1f &&
                   inlineBlockText.BorderRect.Bottom <= inlineBlockBox.ContentRect.Bottom + 1f,
            "inline-block child text is laid out inside its atomic box",
            $"box={inlineBlockBox?.ContentRect}, text={inlineBlockText?.BorderRect}");
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);
        int textPixels = 0;
        if (inlineBlockBox != null)
        {
            var content = inlineBlockBox.ContentRect;
            for (int y = (int)content.Top; y < (int)content.Bottom; y++)
                for (int x = (int)content.Left; x < (int)content.Right; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < 100 && pixel.G < 100 && pixel.B < 100)
                        textPixels++;
                }
        }
        Check.That(textPixels > 20,
            "inline-block child text is actually painted",
            $"dark content pixels={textPixels}, color={inlineBlockText?.Element?.Style?.Color}");
        Check.Done();
    }

    [Fact]
    public void InlineBlockTextInsideTableCellsIsLaidOutAndPainted()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "body{margin:0}.badge{display:inline-block;width:88px;height:31px;" +
            "border:1px solid black;background:white;color:black;" +
            "font:10px Arial;line-height:10px}" +
            "</style></head><body><table><tr><td>" +
            "<div id='first' class='badge'><b>NETSCAPE</b><br>NAVIGATOR<br>4.0 GOLD</div>" +
            "<div id='second' class='badge'><b>MADE WITH</b><br>NOTEPAD<br>HTML 4.0</div>" +
            "</td></tr></table></body></html>");
        var first = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(element => element.GetAttr("id") == "first"));
        var second = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(element => element.GetAttr("id") == "second"));
        var firstLine = LayoutHarness.TextBoxContaining(root, "NETSCAPE");
        var secondLine = LayoutHarness.TextBoxContaining(root, "NAVIGATOR");
        var thirdLine = LayoutHarness.TextBoxContaining(root, "4.0");
        var thirdLineEnd = LayoutHarness.TextBoxContaining(root, "GOLD");

        Check.That(first?.BoxType == BoxType.InlineBlock &&
                   second?.BoxType == BoxType.InlineBlock &&
                   Math.Abs(first.Y - second.Y) < 1f &&
                   second.X - first.X >= 88f,
            "two badge inline-blocks sit side by side in the table cell",
            $"first={first?.BorderRect}, second={second?.BorderRect}");
        Check.That(first != null && firstLine != null && secondLine != null &&
                   thirdLine != null &&
                   firstLine.Y >= first.ContentRect.Top &&
                   secondLine.Y > firstLine.Y &&
                   thirdLine.Y > secondLine.Y &&
                   thirdLineEnd != null &&
                   Math.Abs(thirdLineEnd.Y - thirdLine.Y) < 1f &&
                   thirdLine.BorderRect.Bottom <= first.ContentRect.Bottom + 1f,
            "each badge text line is laid out inside its inline-block",
            $"badge={first?.ContentRect}, lines={firstLine?.BorderRect};" +
            $"{secondLine?.BorderRect};{thirdLine?.BorderRect}");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var renderer = new Renderer(LayoutHarness.Fonts, images, loader);
        using var bitmap = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(320, 100, SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        float scrollY = Math.Max(0f, (first?.Y ?? 0f) - 10f);
        renderer.RenderToCanvas(canvas, root, doc, LayoutHarness.Fonts, images,
            320, 100, 0, scrollY, null, true);
        canvas.Flush();

        int inkPixels = 0;
        if (first != null)
        {
            int left = Math.Max(0, (int)MathF.Floor(first.ContentRect.Left));
            int right = Math.Min(bitmap.Width, (int)MathF.Ceiling(first.ContentRect.Right));
            int top = Math.Max(0, (int)MathF.Floor(first.ContentRect.Top - scrollY));
            int bottom = Math.Min(bitmap.Height,
                (int)MathF.Ceiling(first.ContentRect.Bottom - scrollY));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 100 && pixel.Green < 100 && pixel.Blue < 100)
                    inkPixels++;
            }
        }
        Check.That(inkPixels > 20,
            "badge text produces visible ink in the scrolled viewport",
            $"dark content pixels={inkPixels}");
        Check.Done();
    }

    [Fact]
    public void CssPreWhiteSpacePreservesSourceSpacesAndLineBreaks()
    {
        const string content = "  first\tline\n second  ";
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div id='pre' style='white-space:pre'>" +
            content + "</div></body></html>");
        var pre = doc.ElementDescendants().First(element => element.GetAttr("id") == "pre");
        var text = pre.Children.OfType<DomText>().Single();
        var lines = root.Descendants()
            .Where(box => box.Element == pre && !string.IsNullOrEmpty(box.TextRun))
            .ToArray();

        Check.That(text.Data == content,
            "parser preserves whitespace until CSS white-space is resolved",
            $"parsed='{text.Data.Replace("\n", "\\n").Replace("\t", "\\t")}'");
        Check.That(lines.Any(line => line.TextRun!.Contains("first")) &&
                   lines.Any(line => line.TextRun!.Contains("second")) &&
                   lines.Select(line => line.Y).Distinct().Count() >= 2 &&
                   lines.Where(line => line.TextRun!.Contains("second"))
                       .Min(line => line.Y) > lines.Where(line => line.TextRun!.Contains("first"))
                       .Max(line => line.Y),
            "white-space:pre lays out source lines on separate rows",
            string.Join("; ", lines.Select(line => $"'{line.TextRun}'@{line.Y:0.#}")));
        Check.Done();
    }

    [Fact]
    public void CssNowrapWorksForClassInlineStyleAndInheritance()
    {
        const string content = "one two three four";
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            ".nowrap{white-space:nowrap} .narrow{width:35px}" +
            "</style></head><body>" +
            "<div id='class' class='nowrap narrow'>" + content + "</div>" +
            "<div id='inline' class='narrow' style='white-space:nowrap'>" +
            content + "</div>" +
            "<div id='parent' class='narrow' style='white-space:nowrap'>" +
            "<span id='inherited'>" + content + "</span></div>" +
            "<div id='normal' class='narrow'>" + content + "</div>" +
            "</body></html>");

        DomElement Element(string id) =>
            doc.ElementDescendants().First(element => element.GetAttr("id") == id);
        float[] TextLineYs(DomElement element) =>
            root.Descendants()
                .Where(box => box.Element == element && !string.IsNullOrEmpty(box.TextRun))
                .Select(box => box.Y)
                .Distinct()
                .ToArray();

        var classLines = TextLineYs(Element("class"));
        var inlineLines = TextLineYs(Element("inline"));
        var inheritedLines = TextLineYs(Element("inherited"));
        var normalLines = TextLineYs(Element("normal"));

        Check.That(classLines.Length == 1 && inlineLines.Length == 1 &&
                   inheritedLines.Length == 1,
            "nowrap prevents wrapping from class, inline style, and inheritance",
            $"class={classLines.Length}, inline={inlineLines.Length}, " +
            $"inherited={inheritedLines.Length}");
        Check.That(normalLines.Length > 1,
            "same narrow content wraps without white-space:nowrap",
            $"normal lines={normalLines.Length}");
        Check.Done();
    }

    [Fact]
    public void BodyBackgroundImageTilesFromCanvasOriginAndBrokenImageUsesColorFallback()
    {
        byte[] bmp = new byte[62];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), 8);
        bmp[54] = 0; bmp[55] = 0; bmp[56] = 255;
        bmp[57] = 255; bmp[58] = 0; bmp[59] = 0;
        string pattern = Convert.ToBase64String(bmp);
        var (patternDoc, patternRoot) = LayoutHarness.Parse(
            "<html><head><style>body{margin:9px;background-repeat:repeat;" +
            $"background-image:url(data:image/bmp;base64,{pattern})" +
            "}</style></head><body></body></html>", 32);
        using var patternImages = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var patternRenderer = new Renderer(LayoutHarness.Fonts, patternImages, loader);
        using var patternBitmap = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(32, 32, SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
        using (var canvas = new SkiaSharp.SKCanvas(patternBitmap))
        {
            patternRenderer.RenderToCanvas(canvas, patternRoot, patternDoc,
                LayoutHarness.Fonts, patternImages, 32, 32, 0, 0, null, true);
            canvas.Flush();
        }
        var originPixel = patternBitmap.GetPixel(0, 0);
        var marginPixel = patternBitmap.GetPixel(9, 0);
        Check.That(originPixel.Red > 200 && originPixel.Blue < 50 &&
                   marginPixel.Blue > 200 && marginPixel.Red < 50,
            "body background tiles continue from canvas origin across the body margin",
            $"origin={originPixel}, at body margin={marginPixel}");

        const string malformed = "data:image/png;base64,not-valid";
        var (fallbackDoc, fallbackRoot) = LayoutHarness.Parse(
            "<html><head><style>" +
            "body{margin:9px;background-color:#008080;" +
            $"background-image:url('{malformed}')" +
            "}</style></head><body></body></html>", 32);
        using var fallbackImages = new ImageCache { CookieStore = new CookieStore() };
        var fallbackRenderer = new Renderer(LayoutHarness.Fonts, fallbackImages, loader);
        using var fallbackBitmap = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(32, 32, SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
        using (var canvas = new SkiaSharp.SKCanvas(fallbackBitmap))
        {
            fallbackRenderer.RenderToCanvas(canvas, fallbackRoot, fallbackDoc,
                LayoutHarness.Fonts, fallbackImages, 32, 32, 0, 0, null, true);
            canvas.Flush();
        }
        var fallbackBody = fallbackDoc.ElementDescendants()
            .First(element => element.TagName == "body");
        string backgroundUrl = fallbackBody.Style!.BackgroundImage!;
        backgroundUrl = backgroundUrl[(backgroundUrl.IndexOf('(') + 1)..]
            .TrimEnd(')').Trim('\'', '"');
        string absoluteUrl = ImageCache.ResolveUrl(backgroundUrl,
            fallbackDoc.BaseUrl?.ToAbsolute());
        var fallbackPixel = fallbackBitmap.GetPixel(0, 0);
        var fallbackMarginPixel = fallbackBitmap.GetPixel(9, 9);
        Check.That(fallbackImages.IsBroken(absoluteUrl) &&
                   fallbackPixel.Green > 90 && fallbackPixel.Green < 170 &&
                   fallbackPixel.Red < 30 && fallbackMarginPixel.Red < 30,
            "malformed background image is rejected and the teal color remains visible",
            $"broken={fallbackImages.IsBroken(absoluteUrl)}, " +
            $"origin={fallbackPixel}, body margin={fallbackMarginPixel}");
        Check.Done();
    }

    [Fact]
    public void CssCountersQuotesGenericTablesAndGreekMarkersResolve()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            ".counter-container{counter-reset:test-counter}" +
            ".counter-item:before{counter-increment:test-counter;" +
            "content:'Section ' counter(test-counter,upper-roman) ': '}" +
            "q{quotes:'«' '»' '“' '”'}" +
            "#bottom{caption-side:bottom}" +
            "#bottom td{border:1px solid #666;padding:8px}" +
            ".test-table{display:table}.test-row{display:table-row}" +
            ".test-cell{display:table-cell;padding:10px}" +
            "ol.custom-ol{list-style-type:lower-greek}" +
            "</style></head><body>" +
            "<div class='counter-container'><p class='counter-item'>First</p>" +
            "<p class='counter-item'>Second</p></div>" +
            "<q>Outer <q>Inner</q> end</q>" +
            "<table id='bottom'><caption>Caption</caption><tr><td>Cell</td></tr></table>" +
            "<div class='test-table'><div class='test-row'><div id='generic-cell' class='test-cell'>Simulation</div></div></div>" +
            "<ol class='custom-ol'><li>Alpha</li><li>Beta</li></ol>" +
            "</body></html>");

        var counterItems = doc.ElementDescendants()
            .Where(element => element.GetAttr("class") == "counter-item").ToArray();
        string firstCounter = counterItems[0].Style?.GeneratedBefore?.ResolvedGeneratedContentText ?? "";
        string secondCounter = counterItems[1].Style?.GeneratedBefore?.ResolvedGeneratedContentText ?? "";
        Check.That(firstCounter == "Section I: " && secondCounter == "Section II: ",
            "counter() is resolved and upper-roman formats the incremented value",
            $"first='{firstCounter}', second='{secondCounter}'");

        string allText = string.Concat(root.Descendants()
            .Where(box => box.TextRun != null)
            .Select(box => box.TextRun));
        Check.That(allText.Contains("«") && allText.Contains("»") &&
                   allText.Contains("“") && allText.Contains("”"),
            "q generated content uses authored nested quote pairs",
            $"text='{allText}'");

        var table = doc.ElementDescendants().First(element => element.GetAttr("id") == "bottom");
        var caption = LayoutHarness.BoxOf(root,
            table.ElementDescendants().First(element => element.TagName == "caption"))!;
        var cell = LayoutHarness.BoxOf(root,
            table.ElementDescendants().First(element => element.TagName == "td"))!;
        var cellText = LayoutHarness.TextBoxContaining(root, "Cell");
        Check.That(caption.Y >= cell.BorderRect.Bottom - 1f,
            "caption-side on the table positions its caption below the grid",
            $"caption={caption.Y:0.#}, grid bottom={cell.BorderRect.Bottom:0.#}");
        Check.That(cell.PaddingLeft == 8f && cellText != null &&
                   cellText.X >= cell.BorderRect.Left + cell.BorderLeft + 7f,
            "authored CSS table-cell padding is included in content placement",
            $"padding={cell.PaddingLeft:0.#}, textX={cellText?.X:0.#}, cellX={cell.BorderRect.Left:0.#}");

        var genericCellElement = doc.ElementDescendants()
            .First(element => element.GetAttr("id") == "generic-cell");
        var genericCell = LayoutHarness.BoxOf(root, genericCellElement);
        Check.That(genericCell?.BoxType == BoxType.TableCell &&
                   LayoutHarness.TextBoxContaining(root, "Simulation")?.Width > 0f,
            "generic display:table-cell participates in table layout",
            $"box={genericCell?.BoxType}");

        var orderedList = doc.ElementDescendants().First(element => element.TagName == "ol");
        Check.That(orderedList.Style?.ListStyleType == ListStyleType.LowerGreek,
            "lower-greek parses as the authored list marker type",
            orderedList.Style?.ListStyleType.ToString() ?? "(missing)");
        Check.Done();
    }

    [Fact]
    public void GenericCssTableHonorsPercentageWidthAndZeroDefaultSpacing()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "#host{width:500px}" +
            "#sim{display:table;width:100%;border:1px solid black}" +
            ".sim-row{display:table-row}" +
            ".sim-cell{display:table-cell;border:1px solid black}" +
            "</style></head><body><div id='host'>" +
            "<div id='sim'><div class='sim-row'>" +
            "<div id='cell-one' class='sim-cell'>One</div>" +
            "<div id='cell-two' class='sim-cell'>Two</div>" +
            "</div></div></div></body></html>");

        var host = doc.ElementDescendants().First(element => element.GetAttr("id") == "host");
        var table = doc.ElementDescendants().First(element => element.GetAttr("id") == "sim");
        var cellOne = doc.ElementDescendants().First(element => element.GetAttr("id") == "cell-one");
        var cellTwo = doc.ElementDescendants().First(element => element.GetAttr("id") == "cell-two");
        var hostBox = LayoutHarness.BoxOf(root, host)!;
        var tableBox = LayoutHarness.BoxOf(root, table)!;
        var firstCellBox = LayoutHarness.BoxOf(root, cellOne)!;
        var secondCellBox = LayoutHarness.BoxOf(root, cellTwo)!;

        Check.That(Math.Abs(tableBox.BorderRect.Width - hostBox.Width) < 1f,
            "display:table div with width:100% fills its containing block",
            $"table={tableBox.BorderRect.Width:0.##}, host={hostBox.Width:0.##}");
        Check.That(Math.Abs(firstCellBox.X - (tableBox.X + tableBox.BorderLeft)) < 0.5f,
            "CSS table cells start at the inside edge of the table border without default cellspacing",
            $"cell={firstCellBox.X:0.##}, border edge={tableBox.X + tableBox.BorderLeft:0.##}");
        Check.That(firstCellBox.PaddingLeft == 0f,
            "CSS-created table cells do not receive the legacy HTML cellpadding default",
            $"padding={firstCellBox.PaddingLeft:0.##}");
        Check.That(Math.Abs(secondCellBox.X - firstCellBox.BorderRect.Right) < 0.5f,
            "adjacent CSS table cells touch when border-spacing is not authored",
            $"gap={secondCellBox.X - firstCellBox.BorderRect.Right:0.##}");
        Check.Done();
    }

    [Fact]
    public void Css2NestedCountersQuotesColorsMarkersAndDisplayTablesResolve()
    {
        const string html = """
            <html><head><style>
            .quote-container{quotes:'"' '"' "'" "'"}
            .q-open:before{content:open-quote}
            .q-close:after{content:close-quote}
            ol.counter-reset-root{counter-reset:chapter 0 section 0;list-style-type:none}
            ol.counter-reset-root>li:before{counter-increment:chapter;content:"Chapter " counter(chapter) ". "}
            ol.counter-reset-sub{counter-reset:section 0;list-style-type:none}
            ol.counter-reset-sub>li:before{counter-increment:section;content:counter(chapter) "." counters(section,".") " "}
            li.marker-test:before{display:marker;marker-offset:15px;content:">>"}
            .inline-table{display:inline-table;border:1px solid black}
            .table-row{display:table-row}
            .table-cell{display:table-cell;padding:3px}
            .row-group-table{display:table}
            .row-group{display:table-row-group}
            .auto-width{table-layout:auto;width:200px;border:1px solid black}
            .compact{display:compact}
            .rtl{direction:rtl;unicode-bidi:bidi-override}
            .collapsed{visibility:collapse}
            .caps{display:table;width:360px}
            .caption-top{display:table-caption;caption-side:top}
            .caption-bottom{display:table-caption;caption-side:bottom}
            .caption-left{display:table-caption;caption-side:left;width:80px}
            .caption-right{display:table-caption;caption-side:right;width:80px}
            .caps-row{display:table-row}
            .caps-cell{display:table-cell;border:1px solid #aaa}
            </style></head><body>
            <div id="system-colors">
              <p id="highlight" style="color:Highlight">Highlight</p>
              <p id="info" style="background-color:InfoBackground">Info</p>
              <p id="button" style="background-color:ButtonFace">Button</p>
            </div>
            <div class="quote-container"><span class="q-open"></span>Outer Quote
              <span class="q-open"></span>Inner Quote<span class="q-close"></span>
              Outer Quote<span class="q-close"></span></div>
            <ol class="counter-reset-root">
              <li>First Chapter<ol class="counter-reset-sub">
                <li id="counter-one">First Section</li>
                <li id="counter-two">Second Section</li>
              </ol></li>
              <li>Second Chapter<ol class="counter-reset-sub">
                <li id="counter-three">First Section</li>
              </ol></li>
            </ol>
            <ul><li id="marker" class="marker-test">Marker text</li></ul>
            <ul class="inline-table" id="inline-table"><div class="table-row">
              <div class="table-cell" id="inline-cell-one">One</div>
              <div class="table-cell" id="inline-cell-two">Two</div>
            </div></ul>
            <div id="row-group-table" class="row-group-table">
              <div id="body-group" class="row-group"><div class="table-row">
                <div class="table-cell" id="body-cell-one">Body Cell 1</div>
                <div class="table-cell" id="body-cell-two">Body Cell 2</div>
              </div></div>
            </div>
            <table id="auto-table" class="auto-width"><tr><td>Small cell</td></tr></table>
            <div id="caps" class="caps">
              <div id="top-caption" class="caption-top">Top Caption</div>
              <div id="bottom-caption" class="caption-bottom">Bottom Caption</div>
              <div id="left-caption" class="caption-left">Left Caption</div>
              <div id="right-caption" class="caption-right">Right Caption</div>
              <div class="caps-row"><div id="caption-cell" class="caps-cell">Cell</div></div>
            </div>
            <div><div id="compact-label" class="compact">Compact:</div>
              <span id="compact-following">Following text</span></div>
            <p id="rtl-text" class="rtl">ENGLISH TEXT BIDI OVERRIDE RTL</p>
            <p>[<span id="collapsed-text" class="collapsed">COLLAPSED CONTENT</span>]</p>
            </body></html>
            """;

        var (doc, root) = LayoutHarness.Parse(html, 640);
        DomElement ById(string id) =>
            doc.ElementDescendants().First(element => element.GetAttr("id") == id);
        LayoutBox Box(string id) => LayoutHarness.BoxOf(root, ById(id))!;

        var highlight = ById("highlight").Style!;
        var info = ById("info").Style!;
        var button = ById("button").Style!;
        Check.That(highlight.Color == Color.FromArgb(49, 106, 197),
            "Highlight resolves to a visible system accent color", highlight.Color.ToString());
        Check.That(info.BackgroundColor == Color.FromArgb(255, 255, 225),
            "InfoBackground resolves to the pale system tooltip background",
            info.BackgroundColor.ToString());
        Check.That(button.BackgroundColor == Color.FromArgb(212, 208, 200),
            "ButtonFace resolves to the system button surface", button.BackgroundColor.ToString());

        var quoteText = string.Concat(root.Descendants()
            .Where(box => box.TextRun != null)
            .Select(box => box.TextRun));
        Check.That(quoteText.Contains("\"Outer Quote 'Inner Quote' Outer Quote\""),
            "quote depth carries across generated pseudo-elements", quoteText);

        string[] expectedCounters = ["1.0.1 ", "1.0.2 ", "2.0.1 "];
        string[] actualCounters = new[] { "counter-one", "counter-two", "counter-three" }
            .Select(id => ById(id).Style?.GeneratedBefore?.ResolvedGeneratedContentText ?? "")
            .ToArray();
        Check.That(actualCounters.SequenceEqual(expectedCounters),
            "counters() joins nested reset instances and restores outer scopes",
            string.Join("|", actualCounters));

        var inlineTable = Box("inline-table");
        var inlineCellOne = Box("inline-cell-one");
        var inlineCellTwo = Box("inline-cell-two");
        Check.That(inlineTable.BoxType == BoxType.InlineBlock &&
                   inlineTable.BorderRect.Width < 640f &&
                   Math.Abs(inlineCellOne.Y - inlineCellTwo.Y) < 0.5f &&
                   inlineCellTwo.X >= inlineCellOne.BorderRect.Right - 0.5f,
            "inline-table shrink-wraps and lays its cells in one row",
            $"width={inlineTable.BorderRect.Width:0.#}, cell ys={inlineCellOne.Y:0.#}/{inlineCellTwo.Y:0.#}");

        var bodyGroup = Box("body-group");
        Check.That(bodyGroup.BoxType == BoxType.Block &&
                   Box("body-cell-one").Width > 0f &&
                   Box("body-cell-two").X >= Box("body-cell-one").BorderRect.Right - 0.5f,
            "CSS table-row-group contributes its child row to table layout",
            $"group={bodyGroup.BoxType}");

        var autoTable = Box("auto-table");
        Check.That(Math.Abs(autoTable.BorderRect.Width - 200f) < 1f,
            "CSS width is honored by table-layout:auto", $"width={autoTable.BorderRect.Width:0.#}");

        var table = Box("caps");
        var captionCell = Box("caption-cell");
        var leftCaption = Box("left-caption");
        var rightCaption = Box("right-caption");
        Check.That(Box("top-caption").Height > 0f &&
                   Box("bottom-caption").Y >= captionCell.BorderRect.Bottom - 0.5f &&
                   leftCaption.X < table.X &&
                   rightCaption.X >= table.BorderRect.Right - 0.5f &&
                   leftCaption.BorderRect.Right <= table.X + 0.5f &&
                   table.BorderRect.Right <= rightCaption.BorderRect.Left + 0.5f &&
                   Math.Abs(leftCaption.BorderRect.Width - 80f) < 1f &&
                   Math.Abs(rightCaption.BorderRect.Width - 80f) < 1f,
            "each table caption is positioned on its authored side",
            $"top={Box("top-caption").Y:0.#}, bottom={Box("bottom-caption").Y:0.#}, " +
            $"left={leftCaption.BorderRect}, table={table.BorderRect}, right={rightCaption.BorderRect}");

        var compactLabel = Box("compact-label");
        Check.That(compactLabel.BoxType == BoxType.Block &&
                   Box("compact-following").Y >= compactLabel.BorderRect.Bottom - 0.5f,
            "compact falls back to block when the following inline content cannot run in",
            $"label bottom={compactLabel.BorderRect.Bottom:0.#}, next={Box("compact-following").Y:0.#}");

        var rtl = ById("rtl-text");
        var rtlRuns = root.Descendants()
            .Where(box => box.Element == rtl && box.TextRun != null)
            .OrderBy(box => box.X)
            .ToArray();
        string rtlVisualText = string.Concat(rtlRuns.Select(box => box.TextRun));
        Check.That(rtlVisualText == "LTR EDIRREVO IDIB TXET HSILGNE" &&
                   rtlRuns.Length > 0 &&
                   Math.Abs(rtlRuns.Max(box => box.X + box.Width) -
                       (rtl.Box?.ContentRect.Right ?? 0f)) < 2f,
            "RTL bidi-override reverses visual order and aligns the line to the right",
            rtlVisualText);

        var marker = ById("marker").Style?.GeneratedBefore;
        Check.That(marker?.Display == DisplayValue.Marker && marker.MarkerOffset == 15f &&
                   LayoutHarness.TextBoxContaining(root, ">>") == null,
            "display:marker removes generated text from the inline run and retains its offset");

        var collapsed = ById("collapsed-text");
        Check.That(collapsed.Style?.Visibility == VisibilityValue.Collapse,
            "visibility:collapse remains represented for the renderer");

        var listTypes = new (string Css, ListStyleType Type, string Marker)[]
        {
            ("decimal-leading-zero", ListStyleType.DecimalLeadingZero, "01"),
            ("lower-latin", ListStyleType.LowerAlpha, "a"),
            ("upper-latin", ListStyleType.UpperAlpha, "A"),
            ("armenian", ListStyleType.Armenian, "Ա"),
            ("georgian", ListStyleType.Georgian, "ა"),
            ("hebrew", ListStyleType.Hebrew, "א"),
            ("hiragana", ListStyleType.Hiragana, "あ"),
            ("katakana", ListStyleType.Katakana, "ア"),
            ("hiragana-iroha", ListStyleType.HiraganaIroha, "い"),
            ("katakana-iroha", ListStyleType.KatakanaIroha, "イ")
        };
        foreach (var (css, type, expectedMarker) in listTypes)
        {
            Check.That(ComputedStyle.ParseListStyleType(css) == type &&
                       Renderer.FormatListMarker(type, 1) == expectedMarker,
                $"{css} parses and formats the first list marker",
                Renderer.FormatListMarker(type, 1));
        }
        Check.Done();
    }

    [Fact]
    public void FixedPositionStaysAtViewportBottomWhenDocumentScrolls()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>body{margin:0}.fixed{" +
            "position:fixed;bottom:0;left:0;right:0;height:30px;background:#00ff00}" +
            ".long{height:1600px}</style></head><body>" +
            "<div id='fixed' class='fixed'>Pinned status bar</div>" +
            "<div class='long'></div></body></html>");
        var fixedBox = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(element => element.GetAttr("id") == "fixed"))!;
        Check.That(Math.Abs(fixedBox.BorderRect.Bottom - 600f) < 0.5f && root.Height > 1200f,
            "fixed bottom is computed from the viewport, not the expanded document",
            $"bar bottom={fixedBox.BorderRect.Bottom:0.#}, document height={root.Height:0.#}");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var renderer = new Renderer(LayoutHarness.Fonts, images, loader);
        using var recorder = new SkiaSharp.SKPictureRecorder();
        var pictureCanvas = recorder.BeginRecording(
            SkiaSharp.SKRect.Create(0, 0, root.Width, root.Height));
        renderer.RenderToCanvas(pictureCanvas, root, doc, LayoutHarness.Fonts, images,
            root.Width, root.Height, 0, 0, null, true,
            skipFixedPositioned: true);
        using var pagePicture = recorder.EndRecording();

        using var topBitmap = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(800, 600, SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
        using var topCanvas = new SkiaSharp.SKCanvas(topBitmap);
        using var scrolledBitmap = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(800, 600, SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
        using var scrolledCanvas = new SkiaSharp.SKCanvas(scrolledBitmap);

        void PaintAtScroll(SkiaSharp.SKCanvas target, float scrollY)
        {
            target.Clear(SkiaSharp.SKColors.White);
            int state = target.Save();
            try
            {
                target.ClipRect(SkiaSharp.SKRect.Create(0, 0, 800, 600));
                target.Translate(0, -scrollY);
                target.DrawPicture(pagePicture);
                renderer.RenderFixedToCanvas(target, root, doc,
                    LayoutHarness.Fonts, images, 800, 600, 0, scrollY,
                    null, true);
            }
            finally
            {
                target.RestoreToCount(state);
            }
            target.Flush();
        }

        PaintAtScroll(topCanvas, 0);
        PaintAtScroll(scrolledCanvas, 200);
        var topPixel = topBitmap.GetPixel(10, 580);
        var scrolledPixel = scrolledBitmap.GetPixel(10, 580);
        Check.That(topPixel.Green > 180 && scrolledPixel.Green > 180 &&
                   scrolledPixel.Red < 30 && scrolledPixel.Blue < 30,
            "fixed bar paints at the same viewport coordinate after vertical scrolling",
            $"before={topPixel}, after={scrolledPixel}");

        int changedPixels = 0;
        for (int y = 560; y < 600; y++)
            for (int x = 0; x < 800; x++)
                if (topBitmap.GetPixel(x, y) != scrolledBitmap.GetPixel(x, y))
                    changedPixels++;
        Check.That(changedPixels == 0,
            "fixed bar text and background stay aligned at the viewport bottom",
            $"changed pixels in the bar region={changedPixels}");
        Check.Done();
    }

    [Fact]
    public void CollapseVisibilityAndCollapsedHiddenTableBordersPaintCorrectly()
    {
        const string html = """
            <html><head><style>
            body{margin:0}
            p{margin:0}
            table{border-collapse:collapse;border:3px solid black}
            </style></head><body>
            <p>[<span id="collapsed-text" style="visibility:collapse">COLLAPSED CONTENT</span>]</p>
            <table id="border-table" border="1"><tr>
              <td id="hidden-cell" style="border:3px hidden black">Hidden</td>
              <td id="normal-cell" style="border:3px solid black">Normal Cell</td>
            </tr></table>
            </body></html>
            """;
        var (doc, root) = LayoutHarness.Parse(html, 640);
        var hiddenElement = doc.ElementDescendants()
            .First(element => element.GetAttr("id") == "collapsed-text");
        var hiddenRuns = root.Descendants()
            .Where(box => box.Element == hiddenElement && box.TextRun != null)
            .ToArray();
        var table = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(element => element.GetAttr("id") == "border-table"))!;
        var hiddenCell = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(element => element.GetAttr("id") == "hidden-cell"))!;
        var normalCell = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(element => element.GetAttr("id") == "normal-cell"))!;

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var renderer = new Renderer(LayoutHarness.Fonts, images, loader);
        using var bitmap = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(640, 600, SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        renderer.RenderToCanvas(canvas, root, doc, LayoutHarness.Fonts, images,
            640, 600, 0, 0, null, true);
        canvas.Flush();

        int DarkPixels(int x, int y) =>
            Enumerable.Range(Math.Max(0, y - 2), 5)
                .SelectMany(py => Enumerable.Range(Math.Max(0, x - 2), 5)
                    .Select(px => bitmap.GetPixel(px, py)))
                .Count(pixel => pixel.Red < 40 && pixel.Green < 40 && pixel.Blue < 40);
        int DarkPixelsInBlock(int left, int top, int width, int height)
        {
            int count = 0;
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 40 && pixel.Green < 40 && pixel.Blue < 40)
                    count++;
            }
            return count;
        }

        int hiddenTextPixels = 0;
        foreach (var run in hiddenRuns)
        {
            int left = Math.Max(0, (int)MathF.Floor(run.X));
            int right = Math.Min(bitmap.Width, (int)MathF.Ceiling(run.X + run.Width));
            int top = Math.Max(0, (int)MathF.Floor(run.Y - 3f));
            int bottom = Math.Min(bitmap.Height, (int)MathF.Ceiling(run.Y + run.Height + 3f));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 100 && pixel.Green < 100 && pixel.Blue < 100)
                    hiddenTextPixels++;
            }
        }
        Check.That(hiddenTextPixels == 0,
            "visibility:collapse suppresses non-table text during painting",
            $"dark pixels in collapsed text bounds={hiddenTextPixels}");

        int centerX = (int)MathF.Round((normalCell.BorderRect.Left + normalCell.BorderRect.Right) / 2f);
        int topY = (int)MathF.Round(table.BorderRect.Top + 1f);
        int rightX = (int)MathF.Round(table.BorderRect.Right - 2f);
        int middleY = (int)MathF.Round((normalCell.BorderRect.Top + normalCell.BorderRect.Bottom) / 2f);
        int bottomY = (int)MathF.Round(table.BorderRect.Bottom - 2f);
        int outerRight = (int)MathF.Round(table.BorderRect.Right - 3f);
        int outerTop = (int)MathF.Round(table.BorderRect.Top);
        int outerBottom = (int)MathF.Round(table.BorderRect.Bottom - 3f);
        int leftX = (int)MathF.Round(table.BorderRect.Left + 1f);
        int normalRightX = (int)MathF.Round(normalCell.BorderRect.Right - 2f);
        int normalTopY = (int)MathF.Round(normalCell.BorderRect.Top + 1f);
        int normalBottomY = (int)MathF.Round(normalCell.BorderRect.Bottom - 2f);
        int hiddenMiddleY = (int)MathF.Round((hiddenCell.BorderRect.Top + hiddenCell.BorderRect.Bottom) / 2f);
        Check.That(DarkPixels(centerX, topY) > 0 &&
                   DarkPixels(rightX, middleY) > 0 &&
                   DarkPixels(centerX, bottomY) > 0 &&
                   DarkPixels(leftX, hiddenMiddleY) == 0,
            "hidden collapsed cell border suppresses only its conflicting outer edge",
            $"top={DarkPixels(centerX, topY)}, right={DarkPixels(rightX, middleY)}, " +
            $"bottom={DarkPixels(centerX, bottomY)}, hidden-left={DarkPixels(leftX, hiddenMiddleY)}; " +
            $"table={table.BorderRect}, normal={normalCell.BorderRect}, hidden={hiddenCell.BorderRect}; " +
            $"sample={centerX},{topY}/{rightX},{middleY}/{centerX},{bottomY}/{leftX},{hiddenMiddleY}; " +
            $"table-right={table.BorderRight}/{table.Element?.Style?.BorderRightStyle}/" +
            $"{table.Element?.Style?.OwnBorderRightStyle}, cell-right={normalCell.Element?.Style?.BorderRightStyle}");
        Check.That(DarkPixels(normalRightX, normalTopY) > 0 &&
               DarkPixels(normalRightX, normalBottomY) > 0,
            "collapsed-border hidden-cell resolution preserves the normal cell's right corners",
            $"top-right={DarkPixels(normalRightX, normalTopY)}, " +
            $"bottom-right={DarkPixels(normalRightX, normalBottomY)}; " +
            $"normal={normalCell.BorderRect}");
        Check.That(DarkPixelsInBlock(outerRight, outerTop, 3, 3) == 9 &&
                   DarkPixelsInBlock(outerRight, outerBottom, 3, 3) == 9,
            "collapsed table border paints solid 3x3 top-right and bottom-right corner blocks",
            $"top-right={DarkPixelsInBlock(outerRight, outerTop, 3, 3)}/9, " +
            $"bottom-right={DarkPixelsInBlock(outerRight, outerBottom, 3, 3)}/9; " +
            $"table={table.BorderRect}");
        Check.Done();
    }

    [Fact]
    public void DottedOutlineAndAuthoredTextInputBorderPaintTheirStyles()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "input[type='text']{background-color:#eef8ff;border:1px solid #0066cc}" +
            ".dotted{width:120px;height:40px;outline:3px dotted #000}" +
            ".dashed{width:60px;height:40px;border:5px dashed #005588}" +
            "</style></head><body>" +
            "<input id='field' type='text' value='test'>" +
            "<div id='outline' class='dotted'></div>" +
            "<div id='dashed' class='dashed'></div>" +
            "</body></html>");
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var fieldElement = doc.ElementDescendants().First(element => element.GetAttr("id") == "field");
        var field = LayoutHarness.BoxOf(root, fieldElement)!;
        var fieldEdge = bitmap.GetPixel(
            (int)(field.BorderRect.Left + field.BorderRect.Width / 2f),
            (int)(field.BorderRect.Top + 0.5f));
        Check.That(fieldElement.Style?.BorderTopStyle == BorderStyleValue.Solid &&
                   fieldElement.Style.BorderTopWidth == 1f &&
                   fieldEdge.B > 150 && fieldEdge.R < 80,
            "authored input border replaces the native grey inset edge",
            fieldEdge.ToString());

        var outlineElement = doc.ElementDescendants().First(element => element.GetAttr("id") == "outline");
        var outline = LayoutHarness.BoxOf(root, outlineElement)!.BorderRect;
        int maxDark = 0, maxLight = 0;
        for (int y = (int)outline.Top - 8; y < (int)outline.Top + 2; y++)
        {
            int dark = 0, light = 0;
            for (int x = (int)outline.Left - 8; x < (int)outline.Right + 8; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R < 80 && pixel.G < 80 && pixel.B < 80) dark++;
                if (pixel.R > 200 && pixel.G > 200 && pixel.B > 200) light++;
            }
            maxDark = Math.Max(maxDark, dark);
            maxLight = Math.Max(maxLight, light);
        }
        Check.That(maxDark > 2 && maxLight > 2,
            "outline-style:dotted paints visible dots separated by gaps",
            $"dark pixels={maxDark}, light pixels={maxLight}");

        var dashedElement = doc.ElementDescendants().First(element => element.GetAttr("id") == "dashed");
        var dashed = LayoutHarness.BoxOf(root, dashedElement)!.BorderRect;
        var bottomCorner = bitmap.GetPixel((int)dashed.Left + 1, (int)dashed.Bottom - 2);
        Check.That(bottomCorner.B > 80 && bottomCorner.R < 80,
            "matching dashed sides meet without a diagonal corner cut",
            bottomCorner.ToString());
        Check.Done();
    }

    [Fact]
    public void DottedOutlinesUseRoundDotsAndDashedBordersKeepUniformDashLengths()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "#dots{width:100px;height:60px;outline:6px dotted #000}" +
            "#border-dots{width:100px;height:50px;border:6px dotted #000}" +
            "#dashes{width:40px;height:43px;border:5px dashed #005588}" +
            "</style></head><body><div id='dots'></div>" +
            "<div id='border-dots'></div><div id='dashes'></div></body></html>");
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var dotsElement = doc.ElementDescendants().First(element => element.GetAttr("id") == "dots");
        var dotsBox = LayoutHarness.BoxOf(root, dotsElement)!;
        var dotsBorder = dotsBox.BorderRect;
        float outlineX = dotsBorder.Left - 2f - 3f;
        float outlineY = dotsBorder.Top - 2f - 3f;
        int outlineDotY = (int)MathF.Round(outlineY);
        int outlineDotPixels = 0;
        for (int y = outlineDotY - 3; y <= outlineDotY + 3; y++)
        {
            int dark = 0;
            for (int x = (int)outlineX + 8; x < outlineX + 50f; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R < 80 && pixel.G < 80 && pixel.B < 80)
                    dark++;
            }
            if (dark > outlineDotPixels)
            {
                outlineDotPixels = dark;
                outlineDotY = y;
            }
        }
        int dotStart = -1, dotEnd = -1;
        bool inDot = false;
        for (int x = (int)outlineX + 8; x < outlineX + 50f; x++)
        {
            var pixel = bitmap.GetPixel(x, outlineDotY);
            bool dark = pixel.R < 80 && pixel.G < 80 && pixel.B < 80;
            if (dark && !inDot) dotStart = x;
            if (!dark && inDot)
            {
                dotEnd = x - 1;
                break;
            }
            inDot = dark;
        }
        if (inDot && dotEnd < 0) dotEnd = (int)(outlineX + 50f) - 1;
        int dotCenterX = dotStart >= 0 && dotEnd >= dotStart
            ? (dotStart + dotEnd) / 2 : (int)outlineX;
        var dotCenter = bitmap.GetPixel(dotCenterX, outlineDotY);
        var dotCorner = bitmap.GetPixel(dotCenterX + 3, outlineDotY - 2);
        Check.That(dotCenter.R < 80 && dotCenter.G < 80 && dotCenter.B < 80,
            "dotted outline paints a solid dot at its center",
            $"center={dotCenter}, dotsFound={outlineDotPixels}");
        Check.That(dotCorner.R > 200 && dotCorner.G > 200 && dotCorner.B > 200,
            "dotted outline dots are round rather than square",
            dotCorner.ToString());

        var borderDotsElement = doc.ElementDescendants()
            .First(element => element.GetAttr("id") == "border-dots");
        var borderDotsRect = LayoutHarness.BoxOf(root, borderDotsElement)!.BorderRect;
        int borderDotsY = (int)MathF.Round(borderDotsRect.Top + 3f);
        var dotWidths = new List<int>();
        int currentDotWidth = 0;
        bool insideDot = false;
        for (int x = (int)borderDotsRect.Left + 20;
             x < borderDotsRect.Right - 20; x++)
        {
            var pixel = bitmap.GetPixel(x, borderDotsY);
            bool dark = pixel.R < 80 && pixel.G < 80 && pixel.B < 80;
            if (dark)
            {
                insideDot = true;
                currentDotWidth++;
            }
            else if (insideDot)
            {
                dotWidths.Add(currentDotWidth);
                currentDotWidth = 0;
                insideDot = false;
            }
        }
        if (currentDotWidth > 0) dotWidths.Add(currentDotWidth);
        if (dotWidths.Count > 2)
        {
            dotWidths.RemoveAt(dotWidths.Count - 1);
            dotWidths.RemoveAt(0);
        }
        Check.That(dotWidths.Count >= 3 &&
                   dotWidths.Max() - dotWidths.Min() <= 1,
            "dotted border keeps dot size consistent along its straight side",
            $"dot widths=[{string.Join(",", dotWidths)}]");

        var dashElement = doc.ElementDescendants().First(element => element.GetAttr("id") == "dashes");
        var dashRect = LayoutHarness.BoxOf(root, dashElement)!.BorderRect;
        int sampleX = (int)(dashRect.Left + 2f);
        var runLengths = new List<int>();
        int run = 0;
        for (int y = (int)dashRect.Top + 6; y < (int)dashRect.Bottom - 6; y++)
        {
            var pixel = bitmap.GetPixel(sampleX, y);
            bool painted = pixel.B > 80 && pixel.R < 80;
            if (painted)
                run++;
            else if (run > 0)
            {
                runLengths.Add(run);
                run = 0;
            }
        }
        if (run > 0) runLengths.Add(run);
        Check.That(runLengths.Count >= 2 &&
                   runLengths.Max() - runLengths.Min() <= 1,
            "vertical dashed borders keep full, uniform dashes instead of a clipped final dash",
            $"runs=[{string.Join(",", runLengths)}]");
        Check.Done();
    }

    [Fact]
    public void FractionalWordAdvancesDoNotCausePrematureWrapping()
    {
        const float containerWidth = 300f;
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div id='width-test' " +
            "style='width:300px;font:16px Arial'>placeholder</div></body></html>");
        var container = doc.ElementDescendants()
            .First(element => element.GetAttr("id") == "width-test");
        var style = container.Style!;
        float wordWidth = InlineLayout.MeasureTextWidth("a", style);
        float spaceWidth = InlineLayout.MeasureTextWidth(" ", style);

        int wordCount = 0;
        float bestPreciseWidth = 0f;
        for (int count = 1; count <= 100; count++)
        {
            float preciseWidth = count * wordWidth + (count - 1) * spaceWidth;
            float roundedFragmentWidth =
                count * MathF.Ceiling(wordWidth) +
                (count - 1) * MathF.Ceiling(spaceWidth);
            if (preciseWidth < containerWidth - 0.5f &&
                roundedFragmentWidth > containerWidth)
            {
                wordCount = count;
                bestPreciseWidth = preciseWidth;
            }
        }

        Check.That(wordCount > 0,
            "test text distinguishes precise advances from rounding every inline fragment");
        if (wordCount > 0)
        {
            string text = string.Join(" ", Enumerable.Repeat("a", wordCount));
            var (wrappedDoc, wrappedRoot) = LayoutHarness.Parse(
                $"<html><body><div id='width-test' " +
                $"style='width:300px;font:16px Arial'>{text}</div></body></html>");
            var fragments = wrappedRoot.Descendants()
                .Where(box => box.TextRun == "a").ToList();
            Check.That(fragments.Count == wordCount &&
                       fragments.All(box => Math.Abs(box.Y - fragments[0].Y) < 0.01f),
                "fractional word widths that fit a 300px content box stay on one line",
                $"words={fragments.Count}/{wordCount}, preciseWidth={bestPreciseWidth:0.##}");
        }
        Check.Done();
    }

    [Fact]
    public void TextShadowIsPaintedBehindHeadingGlyphs()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><h1 id='welcome' style='color:#000;text-shadow:4px 4px #ff0000'>" +
            "Welcome to my home Page</h1></body></html>");
        var heading = doc.ElementDescendants().First(element => element.GetAttr("id") == "welcome");
        Check.That(heading.Style?.TextShadow == "4px 4px #ff0000",
            "heading receives its authored text-shadow value");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);
        int shadowPixels = 0;
        foreach (var box in root.Descendants().Where(box => box.Element == heading && box.TextRun != null))
        {
            var rect = box.ContentRect;
            int left = Math.Max(0, (int)rect.Left);
            int top = Math.Max(0, (int)rect.Top);
            int right = Math.Min(bitmap.Width, (int)Math.Ceiling(rect.Right + 6f));
            int bottom = Math.Min(bitmap.Height, (int)Math.Ceiling(rect.Bottom + 6f));
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R > 120 && pixel.G < 80 && pixel.B < 80)
                        shadowPixels++;
                }
        }
        Check.That(shadowPixels > 10,
            "text-shadow produces visible red pixels offset from the heading text",
            $"red shadow pixels={shadowPixels}");
        Check.Done();
    }

    [Fact]
    public void ZeroOffsetTextShadowStaysBehindForegroundGlyphs()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><span id='layered' style='color:#00ff00;" +
            "text-shadow:0 0 0 #0000ff'>M</span></body></html>");
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);
        var run = root.Descendants()
            .First(box => box.TextRun == "M" && box.Element?.GetAttr("id") == "layered");
        var rect = run.ContentRect;
        int greenPixels = 0, bluePixels = 0;
        for (int y = Math.Max(0, (int)MathF.Floor(rect.Top));
             y < Math.Min(bitmap.Height, (int)MathF.Ceiling(rect.Bottom)); y++)
        for (int x = Math.Max(0, (int)MathF.Floor(rect.Left));
             x < Math.Min(bitmap.Width, (int)MathF.Ceiling(rect.Right)); x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.G > pixel.R + 60 && pixel.G > pixel.B + 60)
                greenPixels++;
            if (pixel.B > pixel.R + 60 && pixel.B > pixel.G + 60)
                bluePixels++;
        }
        Check.That(greenPixels > 0 && bluePixels < greenPixels,
            "foreground glyph pixels remain above an exactly overlapping blue text shadow",
            $"green={greenPixels}, blue={bluePixels}");
        Check.Done();
    }

    [Fact]
    public void FocusedTextareaScrollbarTrackUsesAuthoredBackground()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><textarea id='message' rows='2' cols='20' " +
            "style='background-color:#ffff99'>one\ntwo\nthree\nfour\nfive\nsix\nseven\neight</textarea></body></html>");
        var textarea = doc.ElementDescendants().First(element => element.GetAttr("id") == "message");
        var box = LayoutHarness.BoxOf(root, textarea)!;

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        var renderer = new Renderer(LayoutHarness.Fonts, images, loader);
        using var bitmap = renderer.Render(root, doc, LayoutHarness.Fonts, images,
            800f, 600f, 0f, 0f, null, true, focusedElement: textarea);

        var face = box.ContentRect;
        int trackX = (int)(face.Right - 6f);
        var trackPixel = bitmap.GetPixel(trackX, (int)(face.Bottom - 4f));
        Check.That(trackPixel == Color.FromArgb(0xFF, 0xFF, 0x99),
            "focused textarea scrollbar track keeps the authored yellow field background",
            trackPixel.ToString());

        var thumbPixel = bitmap.GetPixel(trackX, (int)(face.Top + 8f));
        Check.That(thumbPixel.R < 180 && thumbPixel.G < 180 && thumbPixel.B < 180,
            "scrollbar thumb remains distinct from the focused field background",
            thumbPixel.ToString());
        Check.Done();
    }

    [Fact]
    public void ControlOverlayTextDoesNotUseLegacyStrokeBoost()
    {
        var previousSettings = BrowserRuntime.Settings.Clone();
        try
        {
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
            using var font = new Font(FontFamily.GenericSansSerif, 13f,
                FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Black);
            using var format = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                LineAlignment = StringAlignment.Center
            };
            using var boostedBitmap = new Bitmap(180, 32);
            using var overlayBitmap = new Bitmap(180, 32);
            using (var graphics = Graphics.FromBitmap(boostedBitmap))
                graphics.DrawString("Horizontal scroll", font, brush,
                    new RectangleF(0, 0, 180, 32), format);
            using (var graphics = Graphics.FromBitmap(overlayBitmap))
                graphics.DrawStringWithoutLegacyStrokeBoost("Horizontal scroll", font, brush,
                    new RectangleF(0, 0, 180, 32), format);

            static int CountInk(Bitmap bitmap)
            {
                int ink = 0;
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.A > 0 && pixel.R < 220 && pixel.G < 220 && pixel.B < 220)
                            ink++;
                    }
                return ink;
            }

            Check.That(CountInk(boostedBitmap) > CountInk(overlayBitmap),
                "control overlay text omits the extra legacy embolden applied by ordinary rectangle draws",
                $"boosted={CountInk(boostedBitmap)}, overlay={CountInk(overlayBitmap)}");
        }
        finally
        {
            BrowserRuntime.Apply(previousSettings);
        }
        Check.Done();
    }

    [Fact]
    public void ParsedUrlPreservesExactlyOneTrailingDirectorySlash()
    {
        Check.That(
            ParsedUrl.Parse("http://spacejam.com/1996/").ToAbsolute() ==
            "http://spacejam.com/1996/",
            "address-bar directory URL does not gain a second trailing slash");
        Check.That(
            ParsedUrl.Parse("http://spacejam.com/1996").ToAbsolute() ==
            "http://spacejam.com/1996",
            "URL without a trailing slash remains slash-free");
        Check.That(
            ParsedUrl.Parse("http://spacejam.com/a//b///").ToAbsolute() ==
            "http://spacejam.com/a/b/",
            "duplicate path slashes normalize to one trailing slash");
        Check.Done();
    }

    [Fact]
    public void DownscaledImagesUseMipmapFiltering()
    {
        using var source = new Bitmap(64, 64);
        using var target = new Bitmap(1, 1);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                source.SetPixel(x, y, ((x + y) & 1) == 0 ? Color.Black : Color.White);

        using (var graphics = Graphics.FromBitmap(target))
            graphics.DrawImage(source, 0, 0, 1, 1);

        var pixel = target.GetPixel(0, 0);
        Check.That(pixel.R is >= 112 and <= 143 && pixel.G is >= 112 and <= 143 &&
                   pixel.B is >= 112 and <= 143,
            "strong image downscaling averages high-frequency detail",
            $"pixel={pixel}");
        Check.Done();
    }

    [Fact]
    public void ZoomedImagesUseCubicFiltering()
    {
        using var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.Black);
        source.SetPixel(1, 0, Color.White);
        source.SetPixel(0, 1, Color.White);
        source.SetPixel(1, 1, Color.Black);
        using var target = new Bitmap(16, 16);
        using (var graphics = Graphics.FromBitmap(target))
        {
            graphics.ScaleTransform(8f, 8f);
            graphics.DrawImage(source, 0, 0, 2, 2);
        }

        int blendedPixels = 0;
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                byte red = target.GetPixel(x, y).R;
                if (red is > 0 and < 255)
                    blendedPixels++;
            }
        Check.That(blendedPixels > 0,
            "zoomed raster images blend neighboring source pixels",
            $"blendedPixels={blendedPixels}");
        Check.Done();
    }

    [Fact]
    public void ParserSurvivesThe1996Pages()
    {
        foreach (var file in new[] { "detector.html", "netscape1996.html", "dolekemp96.html", "theoldnet.html" })
        {
            string path = Path.Combine(TestPaths.Testdata, file);
            Check.That(File.Exists(path), $"{file} exists");
            if (!File.Exists(path)) continue;

            string html = File.ReadAllText(path);
            DomDocument? doc = null;
            Exception? ex = null;
            try { doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/test.htm"), new CookieStore()); }
            catch (Exception e) { ex = e; }

            Check.That(ex == null, $"{file}: parse without exception", ex?.Message ?? "");
            if (doc == null) continue;

            var body = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
            Check.That(body != null, $"{file}: <body> inferred");
            if (body == null) continue;

            int visible = body.Descendants().OfType<DomElement>()
                .Count(e => e.TagName is "p" or "table" or "img" or "h1" or "h2" or "h3"
                    or "center" or "td" or "tr" or "form" or "b" or "font" or "a" or "marquee");
            Check.That(visible > 5, $"{file}: body has real content ({visible} visual elements)", $"only {visible}");
        }
        Check.Done();
    }

    [Fact]
    public void IrcChatPageParsesWithImagesAndIframe()
    {
        string path = Path.Combine(TestPaths.Testdata, "irc.html");
        Check.That(File.Exists(path), "irc.html exists");
        if (!File.Exists(path)) { Check.Done(); return; }

        string html = File.ReadAllText(path);
        DomDocument? doc = null;
        try { doc = HtmlParser.Parse(html, ParsedUrl.Parse("http://theoldnet.com/chat"), new CookieStore()); }
        catch (Exception e) { Check.That(false, "irc.html parses", e.Message); }

        var body = doc?.ElementDescendants().FirstOrDefault(e => e.TagName == "body");
        Check.That(body != null, "irc: <body> present");
        if (body == null) { Check.Done(); return; }

        var imgs = doc!.ElementDescendants().Where(e => e.TagName == "img").ToList();
        Check.That(imgs.Count == 3, "irc: 3 <img> elements in body", imgs.Count.ToString());
        Check.That(imgs.All(i => (i.GetAttr("src") ?? "").StartsWith("/images/")),
              "irc: image srcs preserved");
        Check.That(imgs.All(i => (i.GetAttr("src") ?? "").Trim().Length == (i.GetAttr("src") ?? "").Length),
              "irc: no trailing spaces in src (old netscape1996 poison)");

        var iframe = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "iframe");
        Check.That(iframe != null, "irc: <iframe> element present");
        Check.That(iframe?.GetAttr("src") == "https://webchat.oftc.net/?channels=theoldnet",
              "irc: iframe src intact", iframe?.GetAttr("src") ?? "");

        var centers = doc.ElementDescendants().Count(e => e.TagName == "center");
        Check.That(centers == 2, "irc: both <center> blocks parsed", centers.ToString());

        var font = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "font");
        Check.That(font?.GetAttr("color") == "white", "irc: <font color=white> parsed");
        Check.Done();
    }

    [Fact]
    public void ErrorPageShellAlignmentPinned()
    {
        string html = Retro96.Engine.ErrorPage.NetworkError("http://x.test/", "boom");
        Check.That(html.Contains("<td align=\"center\" valign=\"middle\">"),
              "shell: dialog remains centered in the viewport");
        Check.That(html.Contains("<td align=\"left\" valign=\"top\">"),
              "shell: message content is left-aligned");
        Check.That(html.Contains("<td align=\"left\"><font color=\"#ffffff\""),
              "shell: title-bar cell left-aligned");

        Check.That(html.Contains("<a href=\"retro96://home\">Home</a>"),
              "footer: provides the browser home link");

        foreach (var (name, page) in new[] {
            ("NotFound", Retro96.Engine.ErrorPage.NotFound("http://x.test/404")),
            ("Cert",     Retro96.Engine.ErrorPage.CertificateError("http://x.test/", "bad cert")),
            ("Timeout",  Retro96.Engine.ErrorPage.Timeout("http://x.test/")),
        })
        {
            Check.That(page.Contains("align=\"left\""), $"{name}: shell pins left alignment");
        }

        string cert = Retro96.Engine.ErrorPage.CertificateError("http://x.test/", "bad");
        Check.That(cert.Contains("window.acceptCertRisk && window.acceptCertRisk()"),
              "cert page: onsubmit references the shell hook");
        Check.Done();
    }

    [Fact]
    public void GlyphSubstitutionTable()
    {
        string Sub(string s) => Retro96.Engine.Render.GlyphSubstitution.MapGlyphs(s);

        Check.That(Sub("\u25B6") == ">", "\u25B6 (black right triangle) → >");
        Check.That(Sub("\u25BA") == ">", "\u25BA (pointer) → >");
        Check.That(Sub("\u258C") == "|", "\u258C (left half block) → | (blue sliver)");
        Check.That(Sub("\u26A0 Warning") == "!! Warning", "\u26A0 (warning) → !!");
        Check.That(Sub("\u266A") == "~", "\u266A (note) → ~");
        Check.That(Sub("a\u2014b") == "a\u2014b", "em dash preserved");
        Check.That(Sub("x\u2022y") == "x" + Retro96.Engine.Render.GlyphSubstitution.LegacyBulletMarker + "y",
            "bullet is mapped to the renderer's stable legacy-bullet marker");
        Check.That(Sub("caf\u00E9") == "caf\u00E9", "Latin-1 accented preserved");
        Check.That(Sub("plain text 123") == "plain text 123", "ASCII untouched");
        Check.That(Sub(Sub("\u25B6\u266A")) == Sub("\u25B6\u266A"), "idempotent");
        Check.That(Sub("[X]\u2588") == "[X]#", "panel-title block substitutes");
        Check.That(Sub("\u2550\u2551") == "=|", "box drawing double line → =|");
        Check.Done();
    }

    [Fact]
    public void DetectorScriptExecutes()
    {
        string html = File.ReadAllText(Path.Combine(TestPaths.Testdata, "detector.html"));

        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);

        var navigator = new JsObject();
        navigator.Set("appName", JsValue.From("Netscape"));
        navigator.Set("appVersion", JsValue.From("3.01 (Win95; I)"));
        navigator.Set("userAgent", JsValue.From("Mozilla/3.0 (compatible; Retro96/1.0; Windows 95)"));
        navigator.Set("platform", JsValue.From("Win32"));
        scope.Define("navigator", JsValue.FromObject(navigator));

        var interpreter = new JsInterpreter(scope, null, _ => { }, _ => { });
        interpreter.RegisterRuntimeBuiltins();

        var writeBuffer = new System.Text.StringBuilder();
        var document = new JsObject();
        document.Set("write", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            foreach (var a in args) writeBuffer.Append(a.ToJsString());
            return JsValue.Undefined;
        }, scope, "write")));
        document.Set("writeln", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            foreach (var a in args) writeBuffer.Append(a.ToJsString());
            writeBuffer.Append('\n');
            return JsValue.Undefined;
        }, scope, "writeln")));
        scope.Define("document", JsValue.FromObject(document));

        var doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/detector.htm"), new CookieStore());
        var script = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "script");
        var text = script?.Children.OfType<DomText>().FirstOrDefault()?.Data;
        Check.That(!string.IsNullOrEmpty(text), "script source extracted by tokenizer/parser",
            $"text length = {text?.Length ?? 0}");
        if (string.IsNullOrEmpty(text)) { Check.Done(); return; }

        Exception? ex = null;
        try { interpreter.ExecuteString(text); }
        catch (Exception e) { ex = e; }

        Check.That(ex == null, "detector script executes without fatal error", ex?.Message ?? "");
        string written = writeBuffer.ToString();
        Check.That(written.Contains("Raw Output for Legacy Browsers"),
            "document.write output streamed", written.Length == 0 ? "write buffer EMPTY" : "");
        Check.That(written.Contains("Netscape Navigator"),
            "sniffing produced Netscape Navigator (ua.indexOf chain works)",
            written.Length == 0 ? "" : "no match in: " + written[..Math.Min(200, written.Length)]);
        Check.Done();
    }

    [Fact]
    public void LocationAssignmentNavigates()
    {
        var scope = new JsScope();
        JsRuntime.PopulateGlobalScope(scope);

        string? navigatedTo = null;
        var interpreter = new JsInterpreter(scope, null, url => navigatedTo = url, _ => { });
        interpreter.RegisterRuntimeBuiltins();

        interpreter.ExecuteString(
            "function openpowerstart() { location = \"http://personal.netscape.com/custom/page/show_page.html\"; }");
        interpreter.ExecuteString("openpowerstart();");
        Check.That(navigatedTo == "http://personal.netscape.com/custom/page/show_page.html",
            "bare location = \"url\" navigates", navigatedTo ?? "(no navigation)");

        navigatedTo = null;
        interpreter.ExecuteString("setTimeout(\"location = 'http://x.test/page'\", 0);");
        interpreter.TickTimers();
        Check.That(navigatedTo == "http://x.test/page",
            "setTimeout(\"location='...'\") fires and navigates", navigatedTo ?? "(no navigation)");

        navigatedTo = null;
        var window = new JsObject();
        window.Set("location", JsValue.From("http://old.value/"));
        scope.Define("window", JsValue.FromObject(window));
        scope.GlobalFallback = window;
        interpreter.ExecuteString("window.location = \"http://y.test/win\";");
        Check.That(navigatedTo == "http://y.test/win",
            "window.location = \"url\" navigates", navigatedTo ?? "(no navigation)");
        Check.Done();
    }

    [Fact]
    public void MarqueeParsesAsBlockWithContent()
    {
        var doc = HtmlParser.Parse(
            "<html><body><marquee behavior=\"scroll\" direction=\"left\" scrollamount=\"3\" bgcolor=\"#ffff00\">Welcome to The Old Net!</marquee></body></html>",
            ParsedUrl.Parse("about:blank"), new CookieStore());

        var marquee = doc.ElementDescendants().FirstOrDefault(e => e.TagName == "marquee");
        Check.That(marquee != null, "marquee element parsed");
        Check.That(marquee?.GetAttr("behavior") == "scroll" &&
              marquee?.GetAttr("scrollamount") == "3" &&
              marquee?.GetAttr("bgcolor") == "#ffff00",
              "marquee attributes survive (behavior/scrollamount/bgcolor)");
        Check.That((marquee?.InnerText ?? "").Contains("Welcome to The Old Net!"),
            "marquee content present");
        Check.Done();
    }

    [Fact]
    public void FileUrlResolution()
    {
        var baseFile = ParsedUrl.Parse("file:///C:/web/dolekemp96.htm");
        Check.That(baseFile.Scheme == "file", "file: URL parses with scheme");

        // Drive-letter URL → native path. Windows keeps its native form;
        // other hosts map it to a rooted path (File APIs report not-found).
        string expectedLocal = OperatingSystem.IsWindows()
            ? "C:\\web\\dolekemp96.htm"
            : "/C:/web/dolekemp96.htm";
        var local = FileUrls.LocalPathFromFileUrl(baseFile);
        Check.That(local == expectedLocal,
            "canonical drive file URL maps to a native path", local ?? "(null)");

        var resolved = FileUrls.Resolve(baseFile, "assets/photo one.gif");
        Check.That(resolved == "file:///C:/web/assets/photo%20one.gif",
            "relative local link resolves canonically and escapes spaces", resolved);

        var fragment = FileUrls.Resolve(baseFile, "#features");
        Check.That(fragment == "file:///C:/web/dolekemp96.htm#features",
            "same-document file fragment keeps the canonical file URL", fragment);

        // file://server/share/... is UNC on Windows, a plain rooted path
        // on POSIX hosts (there is no such filesystem object either way).
        string expectedUnc = OperatingSystem.IsWindows()
            ? "\\\\server\\share\\sites\\home.html"
            : "/server/share/sites/home.html";
        var unc = FileUrls.LocalPathFromFileUrl(
            ParsedUrl.Parse("file://server/share/sites/home.html"));
        Check.That(unc == expectedUnc,
            "host-style file URL maps to a host filesystem path", unc ?? "(null)");

        var original = FileUrls.CanonicalFileUrl("C:\\Docs\\Retro Pages\\index.html");
        Check.That(original == "file:///C:/Docs/Retro%20Pages/index.html",
            "Windows path canonicalises to one file URL form", original);

        Check.That(FileUrls.TryResolveAddressBarInput(
                "C:\\Docs\\Retro Pages\\index.html", null, out var addressBarUrl) &&
            addressBarUrl == "file:///C:/Docs/Retro%20Pages/index.html",
            "absolute Windows paths are first-class address-bar navigation targets",
            addressBarUrl);

        // POSIX hosts: absolute /paths and sloppy multi-slash file: URLs
        // normalise instead of degrading into bogus UNC paths.
        if (!OperatingSystem.IsWindows())
        {
            Check.That(FileUrls.TryResolveAddressBarInput(
                    "/var/www/pages/site.html", null, out var posixUrl) &&
                posixUrl == "file:///var/www/pages/site.html",
                "absolute POSIX paths are first-class address-bar navigation targets",
                posixUrl);

            var sloppy = ParsedUrl.Parse("file:////var/www/pages/index.html");
            Check.That(FileUrls.LocalPathFromFileUrl(sloppy) == "/var/www/pages/index.html",
                "extra-slash file URLs normalise to a rooted POSIX path",
                FileUrls.LocalPathFromFileUrl(sloppy));
        }
        Check.Done();
    }

    [Fact]
    public void UnsupportedActiveXObjectLaysOutNestedImageFallback()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><h3>object nesting and fallback</h3>" +
            "<object classid='clsid:unsupported' width='200' height='40'>" +
            "<object data='dot16.png' type='image/png' width='64' height='48'></object>" +
            "</object><img src='dot16.png' width='16' height='16'></body></html>");
        var objects = doc.ElementDescendants()
            .Where(e => e.TagName == "object").ToArray();
        var outer = objects[0].LayoutBox;
        var fallback = objects[1].LayoutBox;
        var directImage = doc.ElementDescendants().First(e => e.TagName == "img").LayoutBox;

        Check.That(outer?.BoxType == BoxType.InlineBlock,
            "unsupported ActiveX object becomes a fallback container");
        Check.That(fallback?.BoxType == BoxType.Replaced &&
                   outer?.Children.Contains(fallback) == true,
            "nested image object remains in the layout tree",
            fallback?.BoxType.ToString() ?? "(missing)");
        Check.That(fallback != null && fallback.Width == 64f && fallback.Height == 48f,
            "nested image keeps its explicit dimensions",
            $"{fallback?.Width}x{fallback?.Height}");
        Check.That(fallback != null && root.Descendants().Contains(fallback),
            "fallback image is reachable by the renderer");
        Check.That(outer != null && fallback != null &&
                   fallback.X >= outer.X && fallback.Y >= outer.Y && fallback.Y > 0f,
            "nested image is laid out at the fallback object's document position",
            $"outer=({outer?.X},{outer?.Y}), fallback=({fallback?.X},{fallback?.Y})");
        Check.That(directImage != null && directImage.Width == 16f && directImage.Height == 16f,
            "explicit IMG width and height both reach the content box",
            $"{directImage?.Width}x{directImage?.Height}");
        Check.Done();
    }

    // ─────────────────────────────────────────────────────────────────
    // Task 9 (Layout integration) — 1999 layout/render features:
    // DomElement↔LayoutBox wiring, IE5 quirks box model, min/max clamping,
    // generated content, outline, optgroup rows, collapsed borders,
    // table-layout:fixed, vertical-align lengths, caption-side,
    // empty-cells, marquee script-event hook.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LayoutBoxWiringPublishesPrincipalBox()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div id='d' style='width:180px'>x</div>" +
            "<p id='p'>para</p></body></html>");
        var div = doc.ElementDescendants().First(e => e.GetAttr("id") == "d");
        var p = doc.ElementDescendants().First(e => e.GetAttr("id") == "p");
        var body = doc.ElementDescendants().First(e => e.TagName == "body");

        Check.That(div.LayoutBox != null, "div's principal box is published on DomElement.LayoutBox");
        Check.That(div.Box != null && ReferenceEquals(div.Box, div.LayoutBox),
            "the legacy DomElement.Box slot mirrors LayoutBox (DomBindings offset* reads it)");
        Check.That(ReferenceEquals(LayoutHarness.BoxOf(root, div), div.LayoutBox),
            "the published box IS the box in the layout tree");
        Check.That(p.LayoutBox != null && body.LayoutBox != null,
            "paragraph and body boxes are published too");
        var oldPBox = p.LayoutBox;

        // Re-layout reassigns a FRESH box (no stale geometry for JS reads).
        var root2 = LayoutEngine.BuildLayoutTree(doc, 800, 600);
        Check.That(p.LayoutBox != null && !ReferenceEquals(p.LayoutBox, oldPBox),
            "a new layout pass publishes a fresh box");
        Check.That(ReferenceEquals(LayoutHarness.BoxOf(root2, p), p.LayoutBox),
            "the fresh reference matches the new tree");

        // An element whose box is DROPPED (display:none) loses its reference
        // instead of keeping stale geometry.
        div.Style!.Display = DisplayValue.None;
        LayoutEngine.BuildLayoutTree(doc, 800, 600);
        Check.That(div.LayoutBox == null && div.Box == null,
            "display:none clears the stale box reference");
        Check.Done();
    }

    [Fact]
    public void Ie5BoxModelPersonaTreatsAuthoredWidthAsBorderBox()
    {
        try
        {
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.InternetExplorer5 });
            var (doc, root) = LayoutHarness.Parse(
                "<html><body><div id='quirk' style='width:200px;height:100px;padding:10px;border:5px solid black'>x</div>" +
                "<img id='iq' src=\"data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7\" " +
                "style='width:100px;height:40px;border:2px solid black;padding:6px'></body></html>");
            var quirk = LayoutHarness.BoxOf(root,
                doc.ElementDescendants().First(e => e.GetAttr("id") == "quirk"))!;
            var img = LayoutHarness.BoxOf(root,
                doc.ElementDescendants().First(e => e.GetAttr("id") == "iq"))!;

            Check.That(Math.Abs(quirk.BorderRect.Width - 200f) < 0.5f,
                "IE5 quirks: authored CSS width IS the border-box width (padding+border inside)",
                $"borderBox={quirk.BorderRect.Width:0.#}");
            Check.That(Math.Abs(quirk.BorderRect.Height - 100f) < 0.5f,
                "IE5 quirks: authored CSS height IS the border-box height",
                $"borderBox={quirk.BorderRect.Height:0.#}");
            // offsetWidth/offsetHeight read exactly this border box.
            Check.That(Math.Abs((doc.ElementDescendants().First(e => e.GetAttr("id") == "quirk").LayoutBox!.BorderRect.Width) - 200f) < 0.5f,
                "offsetWidth via DomElement.LayoutBox = 200");

            Check.That(Math.Abs(img.BorderRect.Width - 100f) < 0.5f,
                "IE5 img quirk: margin+border+width+border — padding never participates",
                $"borderBox={img.BorderRect.Width:0.#}");

            // The W3C content-box model in the union persona.
            BrowserRuntime.Apply(new UserSettings { EngineMode = RetroEngineMode.Retro96 });
            var (doc2, root2) = LayoutHarness.Parse(
                "<html><body><div id='std' style='width:200px;padding:10px;border:5px solid black'>x</div></body></html>");
            var std = LayoutHarness.BoxOf(root2,
                doc2.ElementDescendants().First(e => e.GetAttr("id") == "std"))!;
            Check.That(Math.Abs(std.BorderRect.Width - 230f) < 0.5f,
                "union persona keeps the W3C box model (content 200 + padding 20 + border 10)",
                $"borderBox={std.BorderRect.Width:0.#}");
        }
        finally
        {
            BrowserRuntime.Apply(new UserSettings());   // default persona restore
        }
        Check.Done();
    }

    [Fact]
    public void MinMaxWidthHeightClampBlockSizes()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='capped' style='max-width:120px'>some wide auto content</div>" +
            "<div id='floored' style='width:50px;min-width:200px'>x</div>" +
            "<div id='pctcapped' style='max-width:25%'>more wide auto content</div>" +
            "<div id='hfloored' style='min-height:80px'>short</div>" +
            "<div id='hcapped' style='height:300px;max-height:40px'>x</div>" +
            "</body></html>");
        LayoutBox BoxFor(string id) => LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == id))!;

        Check.That(Math.Abs(BoxFor("capped").BorderRect.Width - 120f) < 1f,
            "max-width clamps the auto-stretched block",
            $"width={BoxFor("capped").BorderRect.Width:0.#}");
        Check.That(Math.Abs(BoxFor("floored").BorderRect.Width - 200f) < 1f,
            "min-width raises an authored 50px width to 200px",
            $"width={BoxFor("floored").BorderRect.Width:0.#}");
        // 25% of the body content width (800 − 2×10 body margins = 780)
        Check.That(Math.Abs(BoxFor("pctcapped").BorderRect.Width - 195f) < 1.5f,
            "percentage max-width resolves against the containing block",
            $"width={BoxFor("pctcapped").BorderRect.Width:0.#}");
        Check.That(BoxFor("hfloored").BorderRect.Height >= 79f,
            "min-height raises the content height",
            $"height={BoxFor("hfloored").BorderRect.Height:0.#}");
        Check.That(Math.Abs(BoxFor("hcapped").BorderRect.Height - 40f) < 1f,
            "max-height caps an authored 300px height",
            $"height={BoxFor("hcapped").BorderRect.Height:0.#}");
        Check.Done();
    }

    [Fact]
    public void MinMaxConstraintsNeverOverrideLargerResolvedSizes()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='automin' style='min-width:200px'>x</div>" +
            "<div id='tallmin' style='min-height:20px'><p>a</p><p>b</p><p>c</p><p>d</p></div>" +
            "</body></html>");
        LayoutBox BoxFor(string id) => LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == id))!;

        // The auto stretch (780px body content) is LARGER than the 200px
        // floor — the stretch wins, min-width does not shrink the block.
        Check.That(BoxFor("automin").BorderRect.Width > 700f,
            "min-width never shrinks an auto width that already exceeds it",
            $"width={BoxFor("automin").BorderRect.Width:0.#}");

        // Content taller than the min-height floor grows past it (the floor
        // is not mistaken for an authored height).
        var contentH = 0f;
        foreach (var child in BoxFor("tallmin").Children)
            contentH = Math.Max(contentH, child.BorderRect.Bottom - BoxFor("tallmin").Y);
        Check.That(BoxFor("tallmin").BorderRect.Height >= contentH - 1f &&
                  BoxFor("tallmin").BorderRect.Height > 20f,
            "min-height raises but never caps taller content",
            $"height={BoxFor("tallmin").BorderRect.Height:0.#} content={contentH:0.#}");
        Check.Done();
    }

    [Fact]
    public void GeneratedBeforeAfterContentFlowsAsTextRuns()
    {
        // Author rules (StyleResolver already routes :before/:after into the
        // GeneratedBefore/GeneratedAfter slots) — independent of any Q UA
        // defaults, which the main agent seeds separately.
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>" +
            "#t:before { content: \"[\" } " +
            "#t:after { content: attr(suffix) } " +
            "#blk:before { content: 'AB' }" +
            "</style></head><body>" +
            "<p><span id='t' suffix='END'>mid</span></p>" +
            "<div id='blk'>word</div>" +
            "</body></html>");
        var span = doc.ElementDescendants().First(e => e.GetAttr("id") == "t");
        var blk = doc.ElementDescendants().First(e => e.GetAttr("id") == "blk");

        var runs = root.Descendants()
            .Where(b => b.Element == span && !string.IsNullOrEmpty(b.TextRun))
            .ToList();
        Check.That(runs.Count == 3,
            ":before run + element content + :after run", $"runs={runs.Count}");
        Check.That(runs[0].TextRun == "[",
            "the :before string token flows as the FIRST text run", runs[0].TextRun ?? "");
        Check.That(runs[1].TextRun == "mid",
            "the element's own content sits between the generated runs", runs[1].TextRun ?? "");
        Check.That(runs[2].TextRun == "END",
            ":after attr(suffix) emits the element's attribute value", runs[2].TextRun ?? "");
        Check.That(runs[0].X < runs[1].X && runs[1].X < runs[2].X,
            "generated runs flow in inline document order");

        var blkRuns = LayoutHarness.BoxOf(root, blk)!.Children
            .Where(b => !string.IsNullOrEmpty(b.TextRun)).ToList();
        Check.That(blkRuns.Count == 2 && blkRuns[0].TextRun == "AB",
            "block elements get their :before run inside the block box",
            blkRuns.Count > 0 ? blkRuns[0].TextRun ?? "" : "(none)");
        Check.Done();
    }

    [Fact]
    public void OutlinePaintsRingAroundBorderBox()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<div id='red' style='width:100px;height:60px;border:2px solid black;outline:4px solid #ff0000'></div>" +
            "<div id='plain' style='width:100px;height:60px;border:2px solid black'></div>" +
            "<div id='inv' style='width:100px;height:60px;outline-width:3px;outline-style:solid;outline-color:invert'></div>" +
            "</body></html>");
        var elements = doc.ElementDescendants().Where(e => e.GetAttr("id") != null)
            .ToDictionary(e => e.GetAttr("id")!, e => e);

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var red = LayoutHarness.BoxOf(root, elements["red"])!.BorderRect;
        var plain = LayoutHarness.BoxOf(root, elements["plain"])!.BorderRect;
        var inv = LayoutHarness.BoxOf(root, elements["inv"])!.BorderRect;

        // Outline band: 2px offset from the border edge, stroke outward.
        var redPixel = bitmap.GetPixel(
            (int)(red.Left + red.Width / 2f), (int)(red.Top - 4f));
        Check.That(redPixel.R > 200 && redPixel.G < 80 && redPixel.B < 80,
            "outline paints its authored colour 2px outside the border box", redPixel.ToString());

        var plainPixel = bitmap.GetPixel(
            (int)(plain.Left + plain.Width / 2f), (int)(plain.Top - 4f));
        Check.That(plainPixel.R > 240 && plainPixel.G > 240 && plainPixel.B > 240,
            "no outline declaration leaves the band unpainted", plainPixel.ToString());

        var invPixel = bitmap.GetPixel(
            (int)(inv.Left + inv.Width / 2f), (int)(inv.Top - 3.5f));
        Check.That(invPixel.R < 90 && invPixel.G < 90 && invPixel.B < 90,
            "outline-color: invert paints black", invPixel.ToString());
        Check.Done();
    }

    [Fact]
    public void OptgroupRowsRenderAsHeadersWithIndentedOptions()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><select id='s' size='6'>" +
            "<optgroup label='Colors'><option>Red</option><option>Blue</option></optgroup>" +
            "<option>Plain</option></select></body></html>");
        var select = doc.ElementDescendants().First(e => e.GetAttr("id") == "s");

        var rows = SelectRowModel.Build(select);
        Check.That(rows.Count == 4,
            "group header + 2 grouped options + 1 loose option", $"rows={rows.Count}");
        Check.That(rows[0].IsGroupHeader && rows[0].Option == null && rows[0].Label == "Colors",
            "the OPTGROUP header row carries the LABEL attribute and is non-selectable",
            $"label='{rows[0].Label}'");
        Check.That(!rows[1].IsGroupHeader && rows[1].Option != null &&
                   Math.Abs(rows[1].Indent - SelectRowModel.GroupIndent) < 0.01f,
            "options inside a group indent by 12px");
        Check.That(Math.Abs(rows[3].Indent) < 0.01f,
            "loose options are not indented");

        // Rendered geometry: the grouped option's text starts ~12px right of
        // the group header's text; the loose option aligns with the header.
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        var face = LayoutHarness.BoxOf(root, select)!.ContentRect;
        float rowH = face.Height / 6f;

        int LeftmostInk(float y0, float y1)
        {
            for (int x = (int)face.X; x < (int)face.Right - 2; x++)
                for (int y = (int)y0; y <= (int)y1; y++)
                {
                    var p = bitmap.GetPixel(x, y);
                    if (p.A > 0 && p.R < 100 && p.G < 100 && p.B < 100)
                        return x;
                }
            return -1;
        }

        int headerX = LeftmostInk(face.Y + rowH * 0.15f, face.Y + rowH * 0.85f);
        int groupedX = LeftmostInk(face.Y + rowH * 1.15f, face.Y + rowH * 1.85f);
        int plainX = LeftmostInk(face.Y + rowH * 3.15f, face.Y + rowH * 3.85f);
        Check.That(headerX > 0 && groupedX > 0 && plainX > 0,
            "header, grouped option and loose option all render text",
            $"headerX={headerX} groupedX={groupedX} plainX={plainX}");
        if (headerX <= 0 || groupedX <= 0 || plainX <= 0) { Check.Done(); return; }

        Check.That(groupedX - headerX >= 10,
            "the option inside the group is indented right of the header label",
            $"Δ={groupedX - headerX}");
        Check.That(Math.Abs(plainX - headerX) <= 3,
            "the loose option aligns with the un-indented header label",
            $"Δ={plainX - headerX}");
        Check.Done();
    }

    [Fact]
    public void OptionLabelAttributeSuppliesDisplayedText()
    {
        var (doc, _) = LayoutHarness.Parse(
            "<html><body><select><option>London</option>" +
            "<option label='Paris (label attr)' selected>Paris</option></select></body></html>");
        var options = doc.ElementDescendants().Where(e => e.TagName == "option").ToList();
        var selected = options.FirstOrDefault(option => option.HasAttr("selected"));
        Check.That(selected != null && SelectRowModel.OptionLabel(selected) == "Paris (label attr)",
            "the selected option is preserved and its label attribute supplies closed-face text",
            selected == null ? "no selected option" : SelectRowModel.OptionLabel(selected));
        Check.Done();
    }

    [Fact]
    public void ImageInputBorderAttributeReservesBorderWidth()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><input type='image' src='missing.gif' width='40' height='20' border='1'></body></html>");
        var imageInput = doc.ElementDescendants().First(e =>
            e.TagName == "input" && e.GetAttr("type") == "image");
        var box = LayoutHarness.BoxOf(root, imageInput);
        Check.That(box != null && box.BorderTop == 1f && box.BorderLeft == 1f &&
                   box.BorderBottom == 1f && box.BorderRight == 1f,
            "border=1 on an image input sets all four border widths");
        Check.Done();
    }

    [Fact]
    public void FieldsetGetsDefaultGrooveBorder()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><fieldset><legend>Contact</legend><input></fieldset></body></html>");
        var fieldset = doc.ElementDescendants().First(e => e.TagName == "fieldset");
        var box = LayoutHarness.BoxOf(root, fieldset);
        Check.That(box != null && box.BorderTop == 2f && box.BorderLeft == 2f,
            "fieldset UA styles reserve a 2px border");
        Check.That(fieldset.Style?.BorderTopStyle == BorderStyleValue.Groove,
            "fieldset UA border uses the native groove style");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);
        if (box != null)
        {
            var pixel = bitmap.GetPixel((int)(box.BorderRect.Left + 0.5f),
                (int)(box.BorderRect.Top + box.BorderRect.Height * 0.75f));
            Check.That(pixel.R < 240 || pixel.G < 240 || pixel.B < 240,
                "fieldset border is visibly painted", pixel.ToString());
        }
        Check.Done();
    }

    [Fact]
    public void ButtonRichChildrenRetainTheirStyles()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><button><b>Submit</b> <font color='#008000'>button</font>" +
            "<tt>mono</tt><u>under</u></button></body></html>");
        var button = doc.ElementDescendants().First(e => e.TagName == "button");
        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);
        var buttonRect = LayoutHarness.BoxOf(root, button)!.BorderRect;
        bool greenText = false;
        for (int y = (int)buttonRect.Top + 2; y < buttonRect.Bottom - 2; y++)
            for (int x = (int)buttonRect.Left + 2; x < buttonRect.Right - 2; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.G > 80 && pixel.G > pixel.R * 1.25f &&
                    pixel.G > pixel.B * 1.25f)
                    greenText = true;
            }
        Check.That(greenText, "font color styling is painted inside button content");
        Check.Done();
    }

    [Fact]
    public void ButtonNaturalWidthIncludesMonospaceChildren()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><button>Reset type=reset</button>" +
            "<button>Reset <tt>type=reset</tt></button></body></html>");
        var buttons = doc.ElementDescendants().Where(e => e.TagName == "button").ToList();
        float plainWidth = LayoutHarness.BoxOf(root, buttons[0])!.BorderRect.Width;
        float richWidth = LayoutHarness.BoxOf(root, buttons[1])!.BorderRect.Width;
        Check.That(richWidth > plainWidth + 1f,
            "button natural width accounts for its wider monospace child",
            $"plain={plainWidth:0.##}, rich={richWidth:0.##}");
        Check.Done();
    }

    [Fact]
    public void FormControlsUseEraDefaultFontFamilies()
    {
        var (doc, _) = LayoutHarness.Parse(
            "<html><body><textarea></textarea><input><button>Go</button><select><option>One</option></select></body></html>");
        var elements = doc.ElementDescendants()
            .Where(e => e.TagName is "textarea" or "input" or "button" or "select")
            .ToDictionary(e => e.TagName);
        Check.That(elements["textarea"].Style?.FontFamily.Contains("monospace") == true,
            "textarea defaults to monospace");
        Check.That(elements["input"].Style?.FontFamily.Contains("sans-serif") == true &&
                   elements["button"].Style?.FontFamily.Contains("sans-serif") == true &&
                   elements["select"].Style?.FontFamily.Contains("sans-serif") == true,
            "input, button and select default to the system-style sans-serif family");
        Check.Done();
    }

    [Fact]
    public void BorderCollapseForcesZeroCellSpacing()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='sep' border='1'><tr><td>a</td><td>b</td></tr></table>" +
            "<table id='col' border='1' style='border-collapse:collapse'><tr><td>a</td><td>b</td></tr></table>" +
            "<table id='sp' border='1' style='border-collapse:separate;border-spacing:9px'><tr><td>a</td><td>b</td></tr></table>" +
            "</body></html>");

        float Gap(string id)
        {
            var tds = doc.ElementDescendants().Where(e => e.GetAttr("id") == id).First()
                .ElementDescendants().Where(e => e.TagName == "td").ToList();
            var b1 = LayoutHarness.BoxOf(root, tds[0])!;
            var b2 = LayoutHarness.BoxOf(root, tds[1])!;
            return b2.X - b1.BorderRect.Right;
        }

        Check.That(Math.Abs(Gap("sep") - 2f) < 0.5f,
            "separate (default) keeps the NN CELLSPACING=2 gutter",
            $"gap={Gap("sep"):0.#}");
        Check.That(Math.Abs(Gap("col")) < 0.5f,
            "border-collapse:collapse removes ALL spacing between cells",
            $"gap={Gap("col"):0.#}");
        Check.That(Math.Abs(Gap("sp") - 9f) < 0.5f,
            "authored border-spacing beats the attribute default",
            $"gap={Gap("sp"):0.#}");
        Check.Done();
    }

    [Fact]
    public void TableLayoutFixedDistributesBySpecifiedColumnsOnly()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='f' style='table-layout:fixed;width:300px' cellspacing='0' cellpadding='0'>" +
            "<tr><td width='100'>a</td><td>MMMMMMMMM MMMMMMMMM MMMMMMMMM wide content</td></tr></table>" +
            "<table id='g' style='table-layout:fixed;width:400px' cellspacing='0' cellpadding='0'>" +
            "<colgroup><col width='80'><col></colgroup>" +
            "<tr><td>a</td><td>b</td></tr></table>" +
            "</body></html>");

        float[] Widths(string id)
        {
            var tds = doc.ElementDescendants().Where(e => e.GetAttr("id") == id).First()
                .ElementDescendants().Where(e => e.TagName == "td").ToList();
            return tds.Select(td => LayoutHarness.BoxOf(root, td)!.BorderRect.Width).ToArray();
        }

        var fixed1 = Widths("f");
        Check.That(fixed1.Length == 2 && Math.Abs(fixed1[0] - 100f) < 1f,
            "fixed: the first-row width attr pins column 0 at 100px",
            $"w0={fixed1[0]:0.#}");
        Check.That(fixed1.Length == 2 && Math.Abs(fixed1[1] - 200f) < 1f,
            "fixed: the unspecified column takes ALL the remaining space (content ignored)",
            $"w1={fixed1[1]:0.#}");

        var fixed2 = Widths("g");
        Check.That(fixed2.Length == 2 && Math.Abs(fixed2[0] - 80f) < 1f,
            "fixed: <colgroup><col width=80> pins column 0",
            $"w0={fixed2[0]:0.#}");
        Check.That(fixed2.Length == 2 && Math.Abs(fixed2[1] - 320f) < 1f,
            "fixed: the un-specced <col> splits the remainder equally",
            $"w1={fixed2[1]:0.#}");
        Check.Done();
    }

    [Fact]
    public void VerticalAlignLengthShiftsBaselineByPixels()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body><div style='font-size:30px;line-height:40px'>Base " +
            "<span id='up' style='font-size:10px;vertical-align:8px'>up</span> " +
            "<span id='down' style='font-size:10px;vertical-align:-8px'>down</span></div></body></html>");

        float TextY(string id)
        {
            var el = doc.ElementDescendants().First(e => e.GetAttr("id") == id);
            return root.Descendants().First(b => b.Element == el && b.TextRun != null).Y;
        }

        Check.That(Math.Abs((TextY("down") - TextY("up")) - 16f) < 1.5f,
            "+8px and −8px vertical-align lengths sit 16px apart",
            $"Δ={TextY("down") - TextY("up"):0.#}");
        Check.Done();
    }

    [Fact]
    public void FirstLetterFloatExcludesTextFromItsInitialLine()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><head><style>body{margin:0}p{width:180px;margin:0}" +
            "p:first-letter{float:left;font-size:48px}</style></head>" +
            "<body><p id='dropcap'>Floating initials wrap beside the first letter, then continue " +
            "across several more lines until they reach the full paragraph width.</p></body></html>");
        var paragraph = doc.ElementDescendants()
            .First(element => element.GetAttr("id") == "dropcap");
        var letter = root.Descendants().First(box =>
            box.Element == paragraph && box.TextRun == "F" && box.StyleOverride != null);
        var followingWord = root.Descendants().First(box =>
            box.Element == paragraph && box.TextRun == "loating");
        bool resumesAtLeftBelowFloat = root.Descendants().Any(box =>
            box.Element == paragraph && box.TextRun is { Length: > 0 } &&
            box.TextRun != "F" && box.X < letter.BorderRect.Right - 1f &&
            box.Y >= letter.BorderRect.Bottom - 0.5f);

        Check.That(letter.IsFloated && letter.FloatSide == FloatValue.Left &&
                   letter.Width > 20f && letter.Height > 40f,
            "the first-letter pseudo box retains its float and enlarged glyph geometry",
            $"float={letter.IsFloated}/{letter.FloatSide}, size={letter.Width:0.#}x{letter.Height:0.#}");
        Check.That(followingWord.X >= letter.BorderRect.Right - 1f,
            "the first line wraps to the right of the floated initial letter",
            $"wordX={followingWord.X:0.#}, letterRight={letter.BorderRect.Right:0.#}");
        Check.That(resumesAtLeftBelowFloat,
            "later lines resume at the paragraph edge below the floated initial letter");
        Check.Done();
    }

    [Fact]
    public void KanaListMarkerSelectsATypefaceWithKanaGlyphs()
    {
        using var fonts = new FontCache();
        var latinFont = fonts.Resolve(["Times New Roman"], 16f, 400, false);
        var markerFont = fonts.ResolveForText(
            ["Times New Roman"], 16f, 400, false, false, "あ.ア.い.イ.");

        Check.That(!string.Equals(latinFont.FontFamily.Name, markerFont.FontFamily.Name,
                       StringComparison.OrdinalIgnoreCase),
            "kana markers fall back from the Latin text face to a kana-capable typeface",
            $"text face={latinFont.FontFamily.Name}, marker face={markerFont.FontFamily.Name}");
        Check.Done();
    }

    [Fact]
    public void CaptionSideBottomPlacesCaptionBelowTheGrid()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='top' border='1'><caption id='capTop'>above</caption><tr><td>cell</td></tr></table>" +
            "<table id='bot' border='1'><caption id='capBot' style='caption-side:bottom'>below</caption><tr><td>cell</td></tr></table>" +
            "</body></html>");

        var capTop = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "capTop"))!;
        var capBot = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "capBot"))!;
        var tdTop = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "top")
                .ElementDescendants().First(e => e.TagName == "td"))!;
        var tdBot = LayoutHarness.BoxOf(root,
            doc.ElementDescendants().First(e => e.GetAttr("id") == "bot")
                .ElementDescendants().First(e => e.TagName == "td"))!;

        Check.That(capTop.Y + capTop.BorderRect.Height <= tdTop.Y + 1f,
            "default caption-side:top places the caption above the grid");
        Check.That(capBot.Y >= tdBot.BorderRect.Bottom - 1f,
            "caption-side:bottom places the caption below the grid",
            $"capY={capBot.Y:0.#} gridBottom={tdBot.BorderRect.Bottom:0.#}");
        Check.Done();
    }

    [Fact]
    public void EmptyCellsHideSuppressesEmptyCellPainting()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<table id='hide' border='1' style='empty-cells:hide'><tr><td></td><td>x</td></tr></table>" +
            "<table id='show' border='1'><tr><td></td><td>y</td></tr></table>" +
            "</body></html>");

        using var images = new ImageCache { CookieStore = new CookieStore() };
        using var loader = new ResourceLoader(new CookieStore());
        using var bitmap = LayoutHarness.Render(doc, root, images, loader);

        foreach (var id in new[] { "hide", "show" })
        {
            var table = doc.ElementDescendants().First(e => e.GetAttr("id") == id);
            var emptyTd = table.ElementDescendants().First(e => e.TagName == "td" &&
                string.IsNullOrEmpty(e.InnerText));
            var box = LayoutHarness.BoxOf(root, emptyTd)!;
            // Probe INSIDE the empty cell's own left border strip, away from
            // the neighbouring cell's border.
            var pixel = bitmap.GetPixel((int)(box.X + 0.5f),
                (int)(box.Y + box.BorderRect.Height / 2f));
            if (id == "hide")
                Check.That(pixel.R > 230 && pixel.G > 230 && pixel.B > 230,
                    "empty-cells:hide paints no border on the empty cell", pixel.ToString());
            else
                Check.That(pixel.R <= 140 && pixel.G <= 140 && pixel.B <= 140,
                    "default empty-cells:show keeps the grey cell border", pixel.ToString());
        }
        Check.Done();
    }

    [Fact]
    public void OffsetWidthReadsTheRealLayoutBox()
    {
        var page = new PageHarness();
        page.LoadHtml(
            "<html><body><div id='d' style='width:220px; padding:10px; border:5px solid black'>box</div></body></html>");
        Retro96.Engine.Css.StyleResolver.Resolve(page.Document, 800);
        LayoutEngine.BuildLayoutTree(page.Document, 800, 600);

        // Default persona = IE5 quirks: the authored width IS the border box,
        // so the REAL box-backed offsetWidth is 220 (the pre-layout style
        // fallback in DomBindings keeps its own 250 arithmetic — it never
        // consults layout).
        Check.That(page.EvalString("document.getElementById('d').offsetWidth") == "220",
            "offsetWidth reads the real layout box (IE5 quirks: authored width = border box)",
            page.EvalString("document.getElementById('d').offsetWidth"));
        Check.That(page.EvalString("document.getElementById('d').clientWidth") == "210",
            "clientWidth = border box − borders (quirks content + padding)",
            page.EvalString("document.getElementById('d').clientWidth"));
        Check.That(page.EvalString("document.getElementById('d').offsetHeight") != "0" &&
                  page.EvalString("document.getElementById('d').offsetHeight") != "",
            "offsetHeight is a real measured height once a box exists",
            page.EvalString("document.getElementById('d').offsetHeight"));
        Check.Done();
    }

    [Fact]
    public void MarqueeEventHookFiresStartBounceFinish()
    {
        var (doc, root) = LayoutHarness.Parse(
            "<html><body>" +
            "<marquee id='alt' behavior='alternate' scrollamount='10' scrolldelay='10'>bouncy marquee content</marquee>" +
            "<marquee id='sli' behavior='slide' scrollamount='10' scrolldelay='10'>slidey marquee content</marquee>" +
            "</body></html>");
        var alt = doc.ElementDescendants().First(e => e.GetAttr("id") == "alt");
        var sli = doc.ElementDescendants().First(e => e.GetAttr("id") == "sli");
        var altBox = LayoutHarness.BoxOf(root, alt)!;
        var sliBox = LayoutHarness.BoxOf(root, sli)!;

        var events = new List<(string Id, string Event)>();
        Renderer.MarqueeEventHook = (elem, eventName) =>
            events.Add((elem.GetAttr("id") ?? "", eventName));
        try
        {
            using var images = new ImageCache { CookieStore = new CookieStore() };
            using var loader = new ResourceLoader(new CookieStore());
            var renderer = new Renderer(LayoutHarness.Fonts, images, loader);

            const long t0 = 1_000_000;
            renderer.GetMarqueeTranslationX(altBox, t0);
            renderer.GetMarqueeTranslationX(sliBox, t0);
            Check.That(events.Count(e => e.Event == "onstart") == 2,
                "the first animation query of each marquee fires onstart",
                string.Join(",", events.Select(e => $"{e.Id}:{e.Event}")));

            // Advance past the first alternate turnaround (half a cycle).
            float contentW = 0f;
            foreach (var ch in altBox.Children)
                contentW = Math.Max(contentW,
                    ch.X + ch.BorderLeft + ch.PaddingLeft + ch.Width - altBox.X);
            float span = Math.Max(1f, altBox.ContentRect.Width - contentW);
            float pxPerMs = 10f / 10f * BrowserRuntime.MarqueeSpeedPercent / 100f;
            long halfCycle = (long)Math.Ceiling(span / pxPerMs) + 5;
            renderer.GetMarqueeTranslationX(altBox, t0 + halfCycle);
            Check.That(events.Count(e => e.Id == "alt" && e.Event == "onbounce") == 1,
                "crossing the first turnaround fires onbounce exactly once",
                string.Join(",", events.Select(e => $"{e.Id}:{e.Event}")));

            // Slide: advance far past the single traversal.
            renderer.GetMarqueeTranslationX(sliBox, t0 + 100_000);
            renderer.GetMarqueeTranslationX(sliBox, t0 + 200_000);
            Check.That(events.Count(e => e.Id == "sli" && e.Event == "onfinish") == 1,
                "the slide traversal completion fires onfinish exactly once",
                string.Join(",", events.Select(e => $"{e.Id}:{e.Event}")));
        }
        finally
        {
            Renderer.MarqueeEventHook = null;
        }
        Check.Done();
    }
}
