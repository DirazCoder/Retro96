using System;
using System.Collections.Generic;
using System.Drawing;
using Retro96.Engine.Html;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Css;

/// <summary>
/// Walks the DOM tree and fills DomElement.Style on every element.
/// Implements the 1996 Netscape UA stylesheet and HTML presentational attributes.
/// </summary>
public static class StyleResolver
{
    private static Dictionary<DomDocument, DocColorData> _docColors = new();

    private class DocColorData
    {
        public string LinkColor { get; set; } = "#0000ee";
        public string VLinkColor { get; set; } = "#551a8b";
        public string ALinkColor { get; set; } = "#ff0000";
        public int BaseFontSize { get; set; } = 3;
    }

    private static DocColorData GetDocData(DomDocument doc)
    {
        if (!_docColors.TryGetValue(doc, out var data))
        {
            data = new DocColorData();
            _docColors[doc] = data;
        }
        return data;
    }

    /// <summary>
    /// Returns the link colour defined on &lt;body link="..."&gt; for <paramref name="doc"/>,
    /// or the UA default (#0000ee) if not set.  Used by the Renderer to colour anchor text.
    /// </summary>
    public static Color GetLinkColor(DomDocument doc)
    {
        var data = GetDocData(doc);
        try { return ParseHtmlColor(data.LinkColor); }
        catch { return Color.FromArgb(0, 0, 0xEE); }
    }

    /// <summary>Returns the visited-link colour (body vlink attr or UA default #551a8b).</summary>
    public static Color GetVLinkColor(DomDocument doc)
    {
        var data = GetDocData(doc);
        try { return ParseHtmlColor(data.VLinkColor); }
        catch { return Color.FromArgb(0x55, 0x1A, 0x8B); }
    }

    /// <summary>Returns the active-link colour (body alink attr or UA default #ff0000).</summary>
    public static Color GetALinkColor(DomDocument doc)
    {
        var data = GetDocData(doc);
        try { return ParseHtmlColor(data.ALinkColor); }
        catch { return Color.FromArgb(255, 0, 0); }
    }

    // Built-in UA stylesheet
    private const string UAStylesheet = @"
        html, body   { display: block; margin: 8px; font-family: 'Times New Roman', serif;
                       font-size: 16px; color: #000000; background-color: #ffffff; }
        h1 { display: block; font-size: 2em; font-weight: bold; margin: 0.67em 0; }
        h2 { display: block; font-size: 1.5em; font-weight: bold; margin: 0.83em 0; }
        h3 { display: block; font-size: 1.17em; font-weight: bold; margin: 1em 0; }
        h4 { display: block; font-size: 1em; font-weight: bold; margin: 1.33em 0; }
        h5 { display: block; font-size: 0.83em; font-weight: bold; margin: 1.67em 0; }
        h6 { display: block; font-size: 0.67em; font-weight: bold; margin: 2.33em 0; }
        p  { display: block; margin: 1em 0; }
        ul, ol { display: block; margin: 1em 0; padding-left: 40px; }
        li { display: list-item; }
        dl { display: block; margin: 1em 0; }
        dt { display: block; font-weight: bold; }
        dd { display: block; margin-left: 40px; }
        table   { display: table; border-collapse: separate; }
        tr      { display: table-row; }
        td, th  { display: table-cell; padding: 1px; }
        th      { font-weight: bold; text-align: center; }
        caption { display: table-caption; text-align: center; }
        pre, code, tt, kbd, samp, listing { font-family: 'Courier New', monospace; }
        pre  { display: block; white-space: pre; margin: 1em 0; }
        blockquote { display: block; margin: 1em 40px; }
        b, strong { font-weight: bold; }
        i, em, cite, var, dfn { font-style: italic; }
        u  { text-decoration: underline; }
        s, strike { text-decoration: line-through; }
        sub { vertical-align: sub; font-size: 0.83em; }
        sup { vertical-align: super; font-size: 0.83em; }
        big   { font-size: 1.17em; }
        small { font-size: 0.83em; }
        hr    { display: block; margin: 0.5em auto; border-style: inset; border-width: 2px; }
        a:link    { color: #0000ee; text-decoration: underline; }
        a:visited { color: #551a8b; text-decoration: underline; }
        a:active  { color: #ff0000; }
        img  { display: inline; }
        br   { display: inline; }
        center { display: block; text-align: center; }
        address { display: block; font-style: italic; }
        marquee { display: block; overflow: hidden; }
        /* Form controls: inline-block so they participate in line flow
           but still have a proper box with width + height */
        input, button, select { display: inline-block; }
        textarea { display: inline-block; }
        /* HTML5 semantic elements - treated as block (graceful degradation) */
        article, section, aside, nav, header, footer, main,
        figure, figcaption, details, summary { display: block; }
        /* Table row groups */
        tbody, thead, tfoot { display: table-row-group; }
    ";

    /// <summary>
    /// Resolve all styles for the document.
    /// </summary>
    public static void Resolve(DomDocument doc)
    {
        if (doc == null)
            return;

        // Step 1: Apply UA stylesheet
        var (uaRules, uaImports) = CssParser.Parse(UAStylesheet);

        // Step 2: Collect author stylesheets from <link> and <style>
        var authorRules = new List<CssRule>();
        var authorImports = new List<CssImportRule>();

        // Find all <link rel="stylesheet"> elements
        foreach (var link in doc.ElementDescendants())
        {
            if (link.TagName == "link" && link.HasAttr("rel"))
            {
                var rel = link.GetAttr("rel")?.ToLowerInvariant() ?? "";
                if (rel.Contains("stylesheet") && link.HasAttr("href"))
                {
                    // The href would have been fetched by the HTML parser
                }
            }
        }

        // Find all <style> elements
        foreach (var styleElem in doc.ElementDescendants())
        {
            if (styleElem.TagName == "style" && styleElem.Children.Count > 0)
            {
                var textNode = styleElem.Children[0] as DomText;
                if (textNode != null)
                {
                    var (rules, imports) = CssParser.Parse(textNode.Data);
                    authorRules.AddRange(rules);
                    authorImports.AddRange(imports);
                }
            }
        }

        // Combine all rules: UA first, then author
        var allRules = new List<CssRule>();
        allRules.AddRange(uaRules);
        allRules.AddRange(authorRules);

        // Step 3: Walk the DOM and resolve styles
        ResolveNode(doc, null, allRules);

        // Step 4: Apply HTML presentational attributes (low specificity)
        ApplyHtmlAttributes(doc);
    }

    private static void ResolveNode(DomNode node, ComputedStyle? parentStyle, List<CssRule> allRules)
    {
        if (node is DomElement elem)
        {
            // Create initial style (inherit from parent if exists)
            ComputedStyle style;
            if (parentStyle != null)
            {
                style = ComputedStyle.Inherit(parentStyle, new List<CssDeclaration>());
            }
            else
            {
                style = new ComputedStyle();
            }

            // Apply UA defaults directly in code.
            // This bypasses CSS selector matching so UA styles work even if
            // CssSelector.Matches() is broken or returns false for everything.
            ApplyUaDefaults(elem, style, parentStyle?.FontSize ?? 16f);

            // Apply matching CSS rules (author stylesheet overrides UA)
            foreach (var rule in allRules)
            {
                foreach (var selector in rule.Selectors)
                {
                    if (selector.Matches(elem))
                    {
                        foreach (var decl in rule.Declarations)
                        {
                            style.Apply(decl, parentStyle?.FontSize ?? 16f, 800f);
                        }
                        break;
                    }
                }
            }

            // Apply inline style (highest specificity, a=1)
            var inlineStyle = elem.GetAttr("style");
            if (!string.IsNullOrEmpty(inlineStyle))
            {
                var inlineDecls = CssParser.ParseInlineStyle(inlineStyle);
                foreach (var decl in inlineDecls)
                {
                    style.Apply(decl, parentStyle?.FontSize ?? 16f, 800f);
                }
            }

            elem.Style = style;

            // Recurse into children
            foreach (var child in elem.Children)
            {
                ResolveNode(child, style, allRules);
            }
        }
        else if (node is DomDocument doc)
        {
            foreach (var child in doc.Children)
            {
                ResolveNode(child, null, allRules);
            }
        }
        else
        {
            foreach (var child in node.Children)
            {
                if (child is DomElement)
                    ResolveNode(child, parentStyle, allRules);
            }
        }
    }

    /// <summary>
    /// Applies Netscape Navigator 3 UA stylesheet defaults directly in code.
    /// Called before CSS rule matching so these are the baseline; author CSS overrides them.
    /// </summary>
    private static void ApplyUaDefaults(DomElement elem, ComputedStyle style, float parentFontSize)
    {
        switch (elem.TagName)
        {
            // Root / body
            case "html":
                style.Display = DisplayValue.Block;
                style.FontFamily = new List<string> { "Times New Roman", "serif" };
                style.FontSize = 16f;
                style.Color = Color.Black;
                style.BackgroundColor = Color.White;
                break;
            case "body":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginRight = style.MarginBottom = style.MarginLeft = 8f;
                style.BackgroundColor = Color.White;
                break;

            // Headings
            case "h1":
                style.Display = DisplayValue.Block; style.FontWeight = FontWeightValue.Bold;
                style.FontSize = parentFontSize * 2f;
                style.MarginTop = style.MarginBottom = parentFontSize * 0.67f; break;
            case "h2":
                style.Display = DisplayValue.Block; style.FontWeight = FontWeightValue.Bold;
                style.FontSize = parentFontSize * 1.5f;
                style.MarginTop = style.MarginBottom = parentFontSize * 0.75f; break;
            case "h3":
                style.Display = DisplayValue.Block; style.FontWeight = FontWeightValue.Bold;
                style.FontSize = parentFontSize * 1.17f;
                style.MarginTop = style.MarginBottom = parentFontSize * 0.83f; break;
            case "h4":
                style.Display = DisplayValue.Block; style.FontWeight = FontWeightValue.Bold;
                style.FontSize = parentFontSize;
                style.MarginTop = style.MarginBottom = parentFontSize * 1.12f; break;
            case "h5":
                style.Display = DisplayValue.Block; style.FontWeight = FontWeightValue.Bold;
                style.FontSize = parentFontSize * 0.83f;
                style.MarginTop = style.MarginBottom = parentFontSize * 1.5f; break;
            case "h6":
                style.Display = DisplayValue.Block; style.FontWeight = FontWeightValue.Bold;
                style.FontSize = parentFontSize * 0.67f;
                style.MarginTop = style.MarginBottom = parentFontSize * 1.67f; break;

            // Block elements
            case "p":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = parentFontSize; break;
            case "div":
            case "article": case "section": case "aside":
            case "header": case "footer": case "main": case "nav":
            case "figure": case "figcaption": case "address":
                style.Display = DisplayValue.Block; break;
            case "center":
                style.Display = DisplayValue.Block;
                style.TextAlign = TextAlign.Center; break;
            case "blockquote":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = parentFontSize;
                style.MarginLeft = style.MarginRight = 40f; break;
            case "pre":
                style.Display = DisplayValue.Block;
                style.WhiteSpace = WhiteSpaceValue.Pre;
                style.FontFamily = new List<string> { "Courier New", "monospace" };
                style.MarginTop = style.MarginBottom = parentFontSize; break;
            case "hr":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = 4f; break;
            case "marquee":
                style.Display = DisplayValue.Block; break;
            case "form":
                style.Display = DisplayValue.Block;
                style.MarginBottom = 8f; break;

            // Lists
            case "ul":
                style.Display = DisplayValue.Block;
                style.ListStyleType = ListStyleType.Disc;
                style.MarginTop = style.MarginBottom = parentFontSize;
                style.PaddingLeft = 40f; break;
            case "ol":
                style.Display = DisplayValue.Block;
                style.ListStyleType = ListStyleType.Decimal;
                style.MarginTop = style.MarginBottom = parentFontSize;
                style.PaddingLeft = 40f; break;
            case "li":
                style.Display = DisplayValue.ListItem;
                style.MarginBottom = 2f; break;
            case "dl":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = parentFontSize; break;
            case "dt":
                style.Display = DisplayValue.Block;
                style.FontWeight = FontWeightValue.Bold; break;
            case "dd":
                style.Display = DisplayValue.Block;
                style.MarginLeft = 40f; break;

            // Tables
            case "table":
                style.Display = DisplayValue.Table;
                style.MarginTop = style.MarginBottom = 4f; break;
            case "tbody": case "thead": case "tfoot":
                style.Display = DisplayValue.TableRowGroup; break;
            case "tr":
                style.Display = DisplayValue.TableRow; break;
            case "td":
                style.Display = DisplayValue.TableCell;
                style.PaddingTop = style.PaddingBottom = 2f;
                style.PaddingLeft = style.PaddingRight = 5f; break;
            case "th":
                style.Display = DisplayValue.TableCell;
                style.FontWeight = FontWeightValue.Bold;
                style.TextAlign = TextAlign.Center;
                style.PaddingTop = style.PaddingBottom = 2f;
                style.PaddingLeft = style.PaddingRight = 5f; break;
            case "caption":
                style.Display = DisplayValue.TableCaption;
                style.TextAlign = TextAlign.Center; break;

            // Inline text elements
            case "b": case "strong":
                style.Display = DisplayValue.Inline;
                style.FontWeight = FontWeightValue.Bold; break;
            case "i": case "em": case "cite": case "dfn":
                style.Display = DisplayValue.Inline;
                style.FontStyle = FontStyleValue.Italic; break;
            case "u": case "ins":
                style.Display = DisplayValue.Inline;
                style.TextDecoration = TextDecoration.Underline; break;
            case "s": case "strike": case "del":
                style.Display = DisplayValue.Inline;
                style.TextDecoration = TextDecoration.LineThrough; break;
            case "big":
                style.Display = DisplayValue.Inline;
                style.FontSize = parentFontSize * 1.17f; break;
            case "small":
                style.Display = DisplayValue.Inline;
                style.FontSize = parentFontSize * 0.83f; break;
            case "sup":
                style.Display = DisplayValue.Inline;
                style.FontSize = parentFontSize * 0.83f;
                style.VerticalAlign = VerticalAlign.Super; break;
            case "sub":
                style.Display = DisplayValue.Inline;
                style.FontSize = parentFontSize * 0.83f;
                style.VerticalAlign = VerticalAlign.Sub; break;
            case "code": case "tt": case "kbd": case "samp":
                style.Display = DisplayValue.Inline;
                style.FontFamily = new List<string> { "Courier New", "monospace" }; break;
            case "a":
                style.Display = DisplayValue.Inline;
                style.Color = Color.FromArgb(0, 0, 0xEE);
                style.TextDecoration = TextDecoration.Underline; break;

            // Non-rendering / replaced
            case "script": case "style": case "head": case "title":
            case "meta": case "link":
                style.Display = DisplayValue.None; break;

            // Form controls: inline-block so they sit in line flow
            // but still carry a real box with width + height.
            case "input":
            case "button":
            case "select":
            case "textarea":
                style.Display = DisplayValue.InlineBlock; break;

            // Replaced inline (img stays purely inline; InlineLayout sizes it)
            case "img":
                style.Display = DisplayValue.Inline; break;

            // Everything else stays inline (default)
            default:
                style.Display = DisplayValue.Inline; break;
        }
    }

    private static void ApplyHtmlAttributes(DomDocument doc)
    {
        ApplyHtmlAttributesRecursive(doc, doc);
    }

    private static void ApplyHtmlAttributesRecursive(DomNode node, DomDocument doc)
    {
        if (node is DomElement elem && elem.Style != null)
        {
            var tag = elem.TagName.ToLowerInvariant();
            var colors = GetDocData(doc);

            // === BODY attributes ===
            if (tag == "body")
            {
                if (elem.HasAttr("bgcolor"))
                {
                    var color = ParseHtmlColor(elem.GetAttr("bgcolor")!);
                    if (color != Color.Empty)
                        elem.Style.BackgroundColor = color;
                }
                if (elem.HasAttr("text"))
                {
                    var color = ParseHtmlColor(elem.GetAttr("text")!);
                    if (color != Color.Empty)
                        elem.Style.Color = color;
                }
                if (elem.HasAttr("link"))
                    colors.LinkColor = elem.GetAttr("link")!;
                if (elem.HasAttr("vlink"))
                    colors.VLinkColor = elem.GetAttr("vlink")!;
                if (elem.HasAttr("alink"))
                    colors.ALinkColor = elem.GetAttr("alink")!;
                if (elem.HasAttr("background"))
                {
                    elem.Style.BackgroundImage = elem.GetAttr("background");
                    elem.Style.BackgroundRepeat = BackgroundRepeat.Repeat;
                }
            }
            // === FONT attributes ===
            else if (tag == "font")
            {
                if (elem.HasAttr("color"))
                {
                    var color = ParseHtmlColor(elem.GetAttr("color")!);
                    if (color != Color.Empty)
                        elem.Style.Color = color;
                }
                if (elem.HasAttr("face"))
                {
                    elem.Style.FontFamily = new List<string>(elem.GetAttr("face")!.Split(','));
                }
                if (elem.HasAttr("size"))
                {
                    var sizeStr = elem.GetAttr("size")!;
                    elem.Style.FontSize = ParseHtmlFontSize(sizeStr, colors.BaseFontSize);
                }
            }
            // === TD/TH attributes ===
            else if (tag == "td" || tag == "th")
            {
                if (elem.HasAttr("align"))
                {
                    var align = elem.GetAttr("align")!.ToLowerInvariant();
                    elem.Style.TextAlign = align switch
                    {
                        "left" => TextAlign.Left,
                        "center" => TextAlign.Center,
                        "right" => TextAlign.Right,
                        "justify" => TextAlign.Justify,
                        _ => elem.Style.TextAlign
                    };
                }
                if (elem.HasAttr("valign"))
                {
                    var valign = elem.GetAttr("valign")!.ToLowerInvariant();
                    elem.Style.VerticalAlign = valign switch
                    {
                        "top" => VerticalAlign.Top,
                        "middle" => VerticalAlign.Middle,
                        "bottom" => VerticalAlign.Bottom,
                        "baseline" => VerticalAlign.Baseline,
                        _ => elem.Style.VerticalAlign
                    };
                }
                if (elem.HasAttr("bgcolor"))
                {
                    var color = ParseHtmlColor(elem.GetAttr("bgcolor")!);
                    if (color != Color.Empty)
                        elem.Style.BackgroundColor = color;
                }
                if (elem.HasAttr("width"))
                {
                    var width = ParseHtmlLength(elem.GetAttr("width")!);
                    if (width.HasValue)
                        elem.Style.Width = width;
                }
                if (elem.HasAttr("height"))
                {
                    var height = ParseHtmlLength(elem.GetAttr("height")!);
                    if (height.HasValue)
                        elem.Style.Height = height;
                }
                if (elem.HasAttr("nowrap"))
                {
                    elem.Style.WhiteSpace = WhiteSpaceValue.Nowrap;
                }
            }
            // === IMG attributes ===
            else if (tag == "img")
            {
                if (elem.HasAttr("width"))
                {
                    var width = ParseHtmlLength(elem.GetAttr("width")!);
                    if (width.HasValue)
                        elem.Style.Width = width;
                }
                if (elem.HasAttr("height"))
                {
                    var height = ParseHtmlLength(elem.GetAttr("height")!);
                    if (height.HasValue)
                        elem.Style.Height = height;
                }
                if (elem.HasAttr("border"))
                {
                    if (int.TryParse(elem.GetAttr("border"), out int b))
                    {
                        elem.Style.BorderTopWidth = elem.Style.BorderRightWidth =
                        elem.Style.BorderBottomWidth = elem.Style.BorderLeftWidth = b;
                        elem.Style.BorderTopStyle = elem.Style.BorderRightStyle =
                        elem.Style.BorderBottomStyle = elem.Style.BorderLeftStyle = BorderStyleValue.Solid;
                    }
                }
                if (elem.HasAttr("hspace"))
                {
                    if (float.TryParse(elem.GetAttr("hspace"), out float hs))
                    {
                        elem.Style.MarginLeft = elem.Style.MarginRight = hs;
                    }
                }
                if (elem.HasAttr("vspace"))
                {
                    if (float.TryParse(elem.GetAttr("vspace"), out float vs))
                    {
                        elem.Style.MarginTop = elem.Style.MarginBottom = vs;
                    }
                }
                if (elem.HasAttr("align"))
                {
                    var align = elem.GetAttr("align")!.ToLowerInvariant();
                    if (align == "left")
                        elem.Style.Float = FloatValue.Left;
                    else if (align == "right")
                        elem.Style.Float = FloatValue.Right;
                }
            }
            // === TABLE attributes ===
            else if (tag == "table")
            {
                if (elem.HasAttr("border"))
                {
                    var borderStr = elem.GetAttr("border")!;
                    if (borderStr == "")
                        borderStr = "1";
                    if (int.TryParse(borderStr, out int b))
                    {
                        elem.Style.BorderTopWidth = elem.Style.BorderRightWidth =
                        elem.Style.BorderBottomWidth = elem.Style.BorderLeftWidth = b;
                        elem.Style.BorderTopStyle = elem.Style.BorderRightStyle =
                        elem.Style.BorderBottomStyle = elem.Style.BorderLeftStyle = BorderStyleValue.Solid;
                    }
                }
                if (elem.HasAttr("width"))
                {
                    var width = ParseHtmlLength(elem.GetAttr("width")!);
                    if (width.HasValue)
                        elem.Style.Width = width;
                }
                if (elem.HasAttr("cellspacing"))
                    elem.Attrs["_cellspacing"] = elem.GetAttr("cellspacing")!;
                if (elem.HasAttr("cellpadding"))
                    elem.Attrs["_cellpadding"] = elem.GetAttr("cellpadding")!;
            }
            // === HR attributes ===
            else if (tag == "hr")
            {
                if (elem.HasAttr("noshade"))
                {
                    elem.Style.BorderTopStyle = BorderStyleValue.Solid;
                    elem.Style.BorderTopColor = Color.FromArgb(128, 128, 128);
                }
            }
            // === DIV/P/CENTER alignment ===
            else if (tag == "div" || tag == "p" || tag == "center")
            {
                if (elem.HasAttr("align"))
                {
                    var align = elem.GetAttr("align")!.ToLowerInvariant();
                    elem.Style.TextAlign = align switch
                    {
                        "left" => TextAlign.Left,
                        "center" => TextAlign.Center,
                        "right" => TextAlign.Right,
                        "justify" => TextAlign.Justify,
                        _ => elem.Style.TextAlign
                    };
                }
                if (tag == "center")
                {
                    elem.Style.Display = DisplayValue.Block;
                    elem.Style.TextAlign = TextAlign.Center;
                }
            }
        }

        // Recurse
        foreach (var child in node.Children)
        {
            ApplyHtmlAttributesRecursive(child, doc);
        }
    }

    private static float ParseHtmlFontSize(string sizeStr, int baseFontSize)
    {
        if (sizeStr.StartsWith("+") || sizeStr.StartsWith("-"))
        {
            if (int.TryParse(sizeStr, out int relSize))
            {
                int newSize = baseFontSize + relSize;
                return newSize switch
                {
                    <= 1 => 8f, 2 => 10f, 3 => 16f, 4 => 18f, 5 => 24f, 6 => 32f, 7 => 48f,
                    > 7 => 48f
                };
            }
        }

        if (int.TryParse(sizeStr, out int absSize))
        {
            return absSize switch
            {
                1 => 8f, 2 => 10f, 3 => 16f, 4 => 18f, 5 => 24f, 6 => 32f, 7 => 48f,
                _ => 16f
            };
        }

        return 16f;
    }

    private static Color ParseHtmlColor(string colorStr)
    {
        if (string.IsNullOrEmpty(colorStr))
            return Color.Empty;

        try
        {
            if (colorStr.StartsWith("#") && colorStr.Length == 4)
            {
                // Expand #rgb → #rrggbb  (MUST keep the # prefix or ColorTranslator fails)
                var r = colorStr[1];
                var g = colorStr[2];
                var b = colorStr[3];
                colorStr = $"#{r}{r}{g}{g}{b}{b}";
            }
            return ColorTranslator.FromHtml(colorStr);
        }
        catch
        {
            return Color.Empty;
        }
    }

    private static float? ParseHtmlLength(string lengthStr)
    {
        if (string.IsNullOrEmpty(lengthStr))
            return null;

        if (lengthStr.EndsWith("%"))
        {
            if (float.TryParse(lengthStr.TrimEnd('%'), out float pct))
                return pct;
        }
        else if (float.TryParse(lengthStr, out float val))
        {
            return val;
        }

        return null;
    }
}