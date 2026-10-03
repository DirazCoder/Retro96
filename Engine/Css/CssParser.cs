using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Retro96.Engine.Css;

public record CssDeclaration(string Property, string Value, bool Important);

public record CssRule(IReadOnlyList<CssSelector> Selectors,
               IReadOnlyList<CssDeclaration> Declarations);

public record CssImportRule(string Url);

public static class CssParser
{
    public static (List<CssRule> rules, List<CssImportRule> importRules) Parse(string css)
    {
        var rules = new List<CssRule>();
        var importRules = new List<CssImportRule>();
        if (string.IsNullOrEmpty(css))
            return (rules, importRules);

        int pos = 0;
        css = RemoveComments(css);

        while (pos < css.Length)
        {
            // Skip whitespace
            while (pos < css.Length && char.IsWhiteSpace(css[pos]))
                pos++;

            if (pos >= css.Length)
                break;

            // Check for @rules
            if (css[pos] == '@')
            {
                HandleAtRule(css, ref pos, rules, importRules);
                continue;
            }

            // Parse rule: selector { declarations }
            var selectors = ParseSelectorList(css, ref pos);
            if (selectors == null || selectors.Count == 0)
                continue;

            var declarations = ParseDeclarationBlock(css, ref pos);
            if (declarations == null)
                continue;

            // Create one rule per selector (as per task: "h1, h2, h3 { } → split into one CssRule per selector")
            foreach (var selector in selectors)
            {
                rules.Add(new CssRule(new[] { selector }, declarations));
            }
        }

        return (rules, importRules);
    }

    public static List<CssDeclaration> ParseInlineStyle(string style)
    {
        if (string.IsNullOrEmpty(style))
            return new List<CssDeclaration>();

        int pos = 0;
        var declarations = new List<CssDeclaration>();
        ParseDeclarations(style, ref pos, declarations, false);
        return declarations;
    }

    private static string RemoveComments(string css)
    {
        var sb = new StringBuilder();
        int pos = 0;

        while (pos < css.Length)
        {
            if (pos + 1 < css.Length && css[pos] == '/' && css[pos + 1] == '*')
            {
                // Skip comment
                pos += 2;
                while (pos < css.Length)
                {
                    if (pos + 1 < css.Length && css[pos] == '*' && css[pos + 1] == '/')
                    {
                        pos += 2;
                        break;
                    }
                    pos++;
                }
            }
            else
            {
                sb.Append(css[pos]);
                pos++;
            }
        }

        return sb.ToString();
    }

    private static void HandleAtRule(string css, ref int pos, List<CssRule> rules, List<CssImportRule> importRules)
    {
        // Skip @
        pos++;

        // Parse at-rule name
        var nameSb = new StringBuilder();
        while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != '(' && css[pos] != ';' && css[pos] != '{')
        {
            nameSb.Append(char.ToLowerInvariant(css[pos]));
            pos++;
        }

        string name = nameSb.ToString();

        switch (name)
        {
            case "import":
                // @import url("..."); or @import "..."
                SkipWhitespace(css, ref pos);
                string importUrl = "";
                if (pos < css.Length && css[pos] == '(')
                {
                    pos++; // Skip (
                    SkipWhitespace(css, ref pos);
                    // Parse url(...) or "..." or '...'
                    importUrl = ParseUrlOrString(css, ref pos);
                    SkipWhitespace(css, ref pos);
                    if (pos < css.Length && css[pos] == ')')
                        pos++; // Skip )
                }
                else
                {
                    importUrl = ParseUrlOrString(css, ref pos);
                }
                // Store the import rule for later processing
                importRules.Add(new CssImportRule(importUrl));
                SkipToSemicolonOrBrace(css, ref pos);
                break;

            case "charset":
                // @charset "..."; - should be first rule
                SkipWhitespace(css, ref pos);
                if (pos < css.Length && (css[pos] == '"' || css[pos] == '\''))
                {
                    char quote = css[pos];
                    pos++; // Skip opening quote
                    // Parse charset value
                    var charsetSb = new StringBuilder();
                    while (pos < css.Length && css[pos] != quote)
                    {
                        charsetSb.Append(css[pos]);
                        pos++;
                    }
                    if (pos < css.Length && css[pos] == quote)
                        pos++; // Skip closing quote
                }
                SkipToSemicolon(css, ref pos);
                break;

            case "media":
               // @media screen, all { ... } - apply rules inside only for screen/all
               SkipWhitespace(css, ref pos);
               // Parse media types and check if we should apply them
               bool applyMediaRules = false;
               var mediaTypeSb = new StringBuilder();
               while (pos < css.Length && css[pos] != '{')
               {
                   if (!char.IsWhiteSpace(css[pos]) && css[pos] != ',')
                   {
                       mediaTypeSb.Append(char.ToLowerInvariant(css[pos]));
                   }
                   else if (mediaTypeSb.Length > 0)
                   {
                       string mt = mediaTypeSb.ToString().Trim();
                       if (mt == "screen" || mt == "all")
                           applyMediaRules = true;
                       mediaTypeSb.Clear();
                   }
                   pos++;
               }
               // Check last media type
               if (mediaTypeSb.Length > 0)
               {
                   string mt = mediaTypeSb.ToString().Trim();
                   if (mt == "screen" || mt == "all")
                       applyMediaRules = true;
               }

               if (pos < css.Length && css[pos] == '{')
               {
                   pos++; // Skip {
                   int braceDepth = 1;
                   var mediaCss = new StringBuilder();

                   while (pos < css.Length && braceDepth > 0)
                   {
                       if (css[pos] == '{') braceDepth++;
                       else if (css[pos] == '}') braceDepth--;

                       if (braceDepth > 0) // Don't include the closing }
                           mediaCss.Append(css[pos]);

                       pos++;
                   }

                   // Parse the media block content only for screen/all media types
                   if (applyMediaRules)
                   {
                       var (mediaRules, mediaImports) = Parse(mediaCss.ToString());
                       rules.AddRange(mediaRules);
                       importRules.AddRange(mediaImports);
                   }
               }
               break;

            default:
                // Unknown @rule - skip to matching }
                SkipToMatchingBrace(css, ref pos);
                break;
        }
    }

    private static List<CssSelector>? ParseSelectorList(string css, ref int pos)
    {
        var selectors = new List<CssSelector>();
        var currentSelector = new List<SelectorPart>();
        bool inSelector = true;

        while (pos < css.Length && inSelector)
        {
            SkipWhitespace(css, ref pos);

            if (pos >= css.Length) break;

            char c = css[pos];

            if (c == '{')
            {
                // End of selector list
                if (currentSelector.Count > 0)
                {
                    selectors.Add(new CssSelector(currentSelector));
                }
                pos++; // Skip {
                inSelector = false;
            }
            else if (c == ',')
            {
                // Next selector in list
                if (currentSelector.Count > 0)
                {
                    selectors.Add(new CssSelector(currentSelector));
                    currentSelector = new List<SelectorPart>();
                }
                pos++; // Skip ,
            }
            else
            {
                // Parse a selector part
                var part = ParseSelectorPart(css, ref pos);
                if (part != null)
                    currentSelector.Add(part);
            }
        }

        return selectors.Count > 0 ? selectors : null;
    }

    private static SelectorPart? ParseSelectorPart(string css, ref int pos)
    {
        if (pos >= css.Length) return null;

        char c = css[pos];

        if (c == '*')
        {
            pos++;
            return new SelectorPart(PartType.Universal, null);
        }
        else if (c == '#')
        {
            pos++; // Skip #
            var idSb = new StringBuilder();
            while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != '{' && css[pos] != ',' && css[pos] != '.' && css[pos] != '#' && css[pos] != ':' && css[pos] != '>')
            {
                idSb.Append(css[pos]); // Don't lowercase - IDs are case-sensitive
                pos++;
            }
            return new SelectorPart(PartType.Id, idSb.ToString());
        }
        else if (c == '.')
        {
            pos++; // Skip .
            var classSb = new StringBuilder();
            while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != '{' && css[pos] != ',' && css[pos] != '.' && css[pos] != '#' && css[pos] != ':' && css[pos] != '>')
            {
                classSb.Append(css[pos]); // Don't lowercase - classes are case-sensitive
                pos++;
            }
            return new SelectorPart(PartType.Class, classSb.ToString());
        }
        else if (c == ':')
        {
            // Pseudo-class or pseudo-element
            pos++; // Skip :
            bool isElement = false;
            if (pos < css.Length && css[pos] == ':')
            {
                pos++; // Skip second :
                isElement = true;
            }

            var pseudoSb = new StringBuilder();
            while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != '{' && css[pos] != ',' && css[pos] != '.' && css[pos] != '#' && css[pos] != ':' && css[pos] != '>')
            {
                pseudoSb.Append(char.ToLowerInvariant(css[pos]));
                pos++;
            }

            string pseudo = pseudoSb.ToString();
            return isElement
                ? new SelectorPart(PartType.PseudoElement, pseudo)
                : new SelectorPart(PartType.PseudoClass, pseudo);
        }
        else if (char.IsLetter(c))
        {
            // Type selector
            var typeSb = new StringBuilder();
            while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != '{' && css[pos] != ',' && css[pos] != '.' && css[pos] != '#' && css[pos] != ':' && css[pos] != '>')
            {
                typeSb.Append(char.ToLowerInvariant(css[pos]));
                pos++;
            }
            return new SelectorPart(PartType.Type, typeSb.ToString());
        }
        else if (c == '>')
        {
            // Child combinator
            pos++;
            SkipWhitespace(css, ref pos);
            return new SelectorPart(PartType.Child, null);
        }
        else if (c == ' ')
        {
            // Descendant combinator - skip whitespace first
            pos++;
            SkipWhitespace(css, ref pos);
            // Check if next non-whitespace char is '>' (child combinator)
            if (pos < css.Length && css[pos] == '>')
            {
                // This is "div > p" case - the space before > should not create a descendant combinator
                // Just skip the > and return null, the child combinator will be handled on next call
                pos++; // Skip >
                SkipWhitespace(css, ref pos);
                return null;
            }
            return new SelectorPart(PartType.Descendant, null);
        }
        else
        {
            // Skip unknown character
            pos++;
            return null;
        }
    }

    private static List<CssDeclaration>? ParseDeclarationBlock(string css, ref int pos)
    {
        var declarations = new List<CssDeclaration>();
        ParseDeclarations(css, ref pos, declarations, true);
        return declarations.Count > 0 ? declarations : null;
    }

    private static void ParseDeclarations(string css, ref int pos, List<CssDeclaration> declarations, bool requireBraces)
    {
        if (requireBraces)
        {
            SkipWhitespace(css, ref pos);
            if (pos >= css.Length || css[pos] != '{')
                return;
            pos++; // Skip {
        }

        while (pos < css.Length)
        {
            SkipWhitespace(css, ref pos);

            if (pos >= css.Length)
                break;

            if (requireBraces && css[pos] == '}')
            {
                pos++; // Skip }
                break;
            }
            else if (!requireBraces && (css[pos] == ';' || css[pos] == '}'))
            {
                if (css[pos] == ';') pos++;
                break;
            }

            // Parse property name
            var propSb = new StringBuilder();
            while (pos < css.Length && css[pos] != ':' && !char.IsWhiteSpace(css[pos]) && css[pos] != ';' && css[pos] != '}')
            {
                propSb.Append(char.ToLowerInvariant(css[pos]));
                pos++;
            }

            string property = propSb.ToString();
            if (string.IsNullOrEmpty(property))
                continue;

            SkipWhitespace(css, ref pos);

            if (pos >= css.Length || css[pos] != ':')
            {
                // Skip to next ; or }
                SkipToSemicolonOrBrace(css, ref pos);
                continue;
            }

            pos++; // Skip :

            SkipWhitespace(css, ref pos);

            // Parse value
            var valueSb = new StringBuilder();
            int parenDepth = 0;
            while (pos < css.Length && (css[pos] != ';' || parenDepth > 0) && (css[pos] != '}' || parenDepth > 0))
            {
                if (css[pos] == '(') parenDepth++;
                else if (css[pos] == ')') parenDepth--;

                valueSb.Append(css[pos]);
                pos++;
            }

            if (pos < css.Length && css[pos] == ';')
                pos++; // Skip ;

            string value = valueSb.ToString().Trim();

            // Check for !important
            bool important = false;
            int bangIdx = value.IndexOf('!');
            if (bangIdx >= 0)
            {
                string afterBang = value.Substring(bangIdx + 1).Trim();
                if (afterBang.Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    important = true;
                    value = value.Substring(0, bangIdx).Trim();
                }
            }

            // Skip unknown properties (don't crash)
            if (IsKnownProperty(property))
            {
                // Expand shorthand properties
                var expanded = ExpandShorthand(property, value);
                // For box shorthands (margin/padding), expanded returns 4 values that need specific property names
                if ((property == "margin" || property == "padding") && expanded.Count == 4)
                {
                    string prefix = property + "-";
                    declarations.Add(new CssDeclaration(prefix + "top", expanded[0], important));
                    declarations.Add(new CssDeclaration(prefix + "right", expanded[1], important));
                    declarations.Add(new CssDeclaration(prefix + "bottom", expanded[2], important));
                    declarations.Add(new CssDeclaration(prefix + "left", expanded[3], important));
                }
                else
                {
                    declarations.AddRange(expanded.Select(v => new CssDeclaration(property, v, important)));
                }
            }
            else
            {
                // Log to console (skip unknown property)
                Console.WriteLine($"Skipping unknown CSS property: {property}");
            }
        }
    }

    private static bool IsKnownProperty(string property)
    {
        // List of known CSS1 properties + common additional properties
        var knownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
            "border", "border-top", "border-right", "border-bottom", "border-left",
            "border-width", "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
            "border-style", "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
            "border-color", "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
            "font", "font-family", "font-size", "font-weight", "font-style", "font-variant",
            "background", "background-color", "background-image", "background-repeat", "background-position",
            "list-style", "list-style-type", "list-style-position", "list-style-image",
            "color", "text-align", "text-indent", "line-height", "letter-spacing", "word-spacing",
            "text-transform", "vertical-align", "white-space", "text-decoration",
            "display", "visibility", "overflow", "position", "top", "right", "bottom", "left",
            "float", "clear", "z-index", "width", "height", "min-width", "min-height",
            "max-width", "max-height",
            // Additional properties
            "opacity", "cursor", "box-sizing", "border-radius",
            "margin-inline", "margin-block", "padding-inline", "padding-block",
            "border-collapse", "border-spacing", "caption-side", "table-layout",
            "empty-cells", "content", "quotes", "counter-reset", "counter-increment",
            "src", "unicode-range", "font-feature-settings", "font-kerning",
            "text-rendering", "image-rendering", "shape-outside", "clip",
            "resize", "user-select", "pointer-events", "fill", "stroke",
            "transform", "transform-origin", "transition", "animation",
            "flex", "flex-grow", "flex-shrink", "flex-basis", "flex-direction",
            "flex-wrap", "justify-content", "align-items", "align-content",
            "grid", "grid-template", "grid-area", "grid-column", "grid-row",
            "gap", "row-gap", "column-gap", "place-items", "place-content",
            "aspect-ratio", "object-fit", "object-position", "backface-visibility",
            "perspective", "perspective-origin", "will-change", "contain",
            "scroll-behavior", "overscroll-behavior", "touch-action",
            "hyphens", "line-break", "word-break", "overflow-wrap", "text-overflow",
            "writing-mode", "direction", "unicode-bidi", "isolation", "mix-blend-mode",
            "filter", "backdrop-filter", "mask", "mask-image", "clip-path",
            "border-image", "border-image-source", "border-image-slice",
            "border-image-width", "border-image-outset", "border-image-repeat",
            "box-shadow", "text-shadow", "box-decoration-break",
            "break-before", "break-after", "break-inside", "page-break-before",
            "page-break-after", "page-break-inside", "orphans", "widows",
            "columns", "column-count", "column-width", "column-gap",
            "column-rule", "column-rule-width", "column-rule-style",
            "column-rule-color", "column-span", "column-fill",
            "nav-index", "nav-up", "nav-right", "nav-down", "nav-left",
            "zoom", "max-zoom", "min-zoom", "orientation", "resolution",
            "charset", "viewport", "http-equiv", "name", "content",
            "rel", "href", "type", "media", "sizes", "crossorigin",
            "integrity", "referrerpolicy", "loading", "decoding",
            "importance", "color-scheme", "forced-color-adjust",
            "accent-color", "caret-color", "scrollbar-color", "scrollbar-width",
            "field-sizing", "anchor-name", "anchor-scope", "position-try",
            "position-area", "position-visibility", "position-try-order",
            "position-fallback", "position-fallback-below",
            "position-fallback-above", "position-fallback-left",
            "position-fallback-right", "position-fallback-block",
            "position-fallback-inline", "position-fallback-x",
            "position-fallback-y", "position-fallback-width",
            "position-fallback-height", "position-fallback-top",
            "position-fallback-bottom", "position-fallback-left",
            "position-fallback-right", "position-fallback-center",
            "position-fallback-start", "position-fallback-end",
            "position-fallback-self-start", "position-fallback-self-end",
            "position-fallback-flex-start", "position-fallback-flex-end",
            "position-fallback-normal", "position-fallback-baseline",
            "position-fallback-first-baseline", "position-fallback-last-baseline",
            "position-fallback-stretch", "position-fallback-safe",
            "position-fallback-unsafe"
        };

        return knownProperties.Contains(property);
    }

    private static List<string> ExpandShorthand(string property, string value)
    {
        // Handle shorthand property expansion
        switch (property)
        {
            case "margin":
            case "padding":
                return ExpandBoxShorthand(property, value);

            case "border":
                return ExpandBorderShorthand(value);

            case "font":
                return ExpandFontShorthand(value);

            case "background":
                return ExpandBackgroundShorthand(value);

            case "list-style":
                return ExpandListStyleShorthand(value);

            default:
                return new List<string> { value };
        }
    }

    private static List<string> ExpandBoxShorthand(string property, string value)
    {
        // margin/padding: top right bottom left
        var parts = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string top, right, bottom, left;

        switch (parts.Length)
        {
            case 1:
                top = right = bottom = left = parts[0];
                break;
            case 2:
                top = bottom = parts[0];
                right = left = parts[1];
                break;
            case 3:
                top = parts[0];
                right = left = parts[1];
                bottom = parts[2];
                break;
            case 4:
                top = parts[0];
                right = parts[1];
                bottom = parts[2];
                left = parts[3];
                break;
            default:
                return new List<string> { value };
        }

        string prefix = property + "-";
        return new List<string>
        {
            $"{top}",
            $"{right}",
            $"{bottom}",
            $"{left}"
        };
    }

    private static List<string> ExpandBorderShorthand(string value)
    {
        // border: [width] [style] [color]
        // Each component is optional, can be in any order
        string width = "medium";
        string style = "none";
        string color = "currentColor";

        // Parse carefully to handle rgb(255, 0, 0) with commas
        var parts = new List<string>();
        int pos = 0;
        while (pos < value.Length)
        {
            // Skip whitespace
            while (pos < value.Length && char.IsWhiteSpace(value[pos]))
                pos++;
            if (pos >= value.Length)
                break;

            // Check for function (rgb, hsl, etc.) or quoted string
            if (value[pos] == 'r' && pos + 2 < value.Length && value.Substring(pos, 3).ToLower() == "rgb")
            {
                // Parse rgb(...) or rgba(...)
                int start = pos;
                int parenDepth = 0;
                while (pos < value.Length)
                {
                    if (value[pos] == '(') parenDepth++;
                    else if (value[pos] == ')')
                    {
                        parenDepth--;
                        if (parenDepth == 0)
                        {
                            pos++;
                            break;
                        }
                    }
                    pos++;
                }
                parts.Add(value.Substring(start, pos - start));
            }
            else
            {
                // Parse until next whitespace
                int start = pos;
                while (pos < value.Length && !char.IsWhiteSpace(value[pos]))
                    pos++;
                parts.Add(value.Substring(start, pos - start));
            }
        }

        foreach (var part in parts)
        {
            if (IsBorderWidth(part))
                width = part;
            else if (IsBorderStyle(part))
                style = part;
            else
                color = part; // Assume it's a color
        }

        return new List<string> { width, style, color };
    }

     private static List<string> ExpandFontShorthand(string value)
     {
         // font: [style] [variant] [weight] [size] [line-height] [family]
         // Parse into individual components
         // Handle both "12px/1.5 Arial" (no spaces around /) and "12px / 1.5 Arial" (with spaces)
         var result = new List<string>();
         string? size = null;
         string? family = null;
         var fontStyle = "normal";
         var fontVariant = "normal";
         var fontWeight = "normal";
         string? lineHeight = null;

         // First, handle the font-size/line-height slash notation
         // Find the size part which may contain a slash for line-height
         var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         
         for (int i = 0; i < parts.Length; i++)
         {
             var part = parts[i].ToLowerInvariant();
             
             // Check if this part contains a slash that separates size from line-height
             // e.g., "12px/1.5" or "12px/1.5em"
             if (part.Contains("/") && (part.Contains("px") || part.Contains("em") || part.Contains("%") ||
                  part == "xx-small" || part == "x-small" || part == "small" ||
                  part == "medium" || part == "large" || part == "x-large" || part == "xx-large"))
             {
                 var slashParts = part.Split('/');
                 if (slashParts.Length >= 2)
                 {
                     size = parts[i]; // Keep original case for size
                     lineHeight = slashParts[1]; // Line height is after the slash
                 }
                 // Continue to find family from remaining parts
                 if (i + 1 < parts.Length)
                 {
                     family = string.Join(" ", parts, i + 1, parts.Length - i - 1);
                 }
                 break;
             }
             else if (part == "italic" || part == "oblique")
                 fontStyle = part;
             else if (part == "small-caps")
                 fontVariant = part;
             else if (part == "bold" || part == "bolder" || part == "lighter" ||
                      part == "100" || part == "200" || part == "300" || part == "400" ||
                      part == "500" || part == "600" || part == "700" || part == "800" || part == "900")
                 fontWeight = part;
             else if (part.Contains("px") || part.Contains("em") || part.Contains("%") ||
                      part == "xx-small" || part == "x-small" || part == "small" ||
                      part == "medium" || part == "large" || part == "x-large" || part == "xx-large")
             {
                 size = parts[i];
                 // Check for separate line-height with leading slash
                 if (i + 1 < parts.Length && parts[i + 1].StartsWith("/"))
                 {
                     lineHeight = parts[i + 1].Substring(1);
                     i++;
                 }
                 if (i + 1 < parts.Length)
                 {
                     family = string.Join(" ", parts, i + 1, parts.Length - i - 1);
                 }
                 break;
             }
         }

         result.Add(fontStyle);
         result.Add(fontVariant);
         result.Add(fontWeight);
         if (size != null) result.Add(size);
         if (lineHeight != null) result.Add(lineHeight);
         if (family != null) result.Add(family);
         return result;
     }
 
     private static List<string> ExpandBackgroundShorthand(string value)
     {
         // background: [color] [image] [repeat] [attachment] [position]
         var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         var result = new List<string>();
         foreach (var part in parts)
         {
             var lower = part.ToLowerInvariant();
             if (lower == "none" || lower.StartsWith("url("))
                 result.Add(part); // background-image
             else if (lower == "repeat" || lower == "repeat-x" || lower == "repeat-y" || lower == "no-repeat")
                 result.Add(part); // background-repeat
             else if (lower == "fixed" || lower == "scroll")
                 result.Add(part); // background-attachment
             else if (IsColorValue(part))
                 result.Add(part); // background-color
             else
                 result.Add(part); // background-position or other
         }
         return result;
     }

     private static bool IsColorValue(string value)
     {
         var lower = value.ToLowerInvariant();
         // Hex colors
         if (lower.StartsWith("#")) return true;
         // RGB colors
         if (lower.StartsWith("rgb(")) return true;
         // Named colors (CSS1 named colors)
         var namedColors = new HashSet<string>
         {
             "black", "silver", "gray", "white", "maroon", "red", "purple", "fuchsia",
             "green", "lime", "olive", "yellow", "navy", "blue", "teal", "aqua"
         };
         return namedColors.Contains(lower);
     }
 
     private static List<string> ExpandListStyleShorthand(string value)
     {
         // list-style: [type] [position] [image]
         var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         var result = new List<string>();
         foreach (var part in parts)
         {
             var lower = part.ToLowerInvariant();
             if (lower == "none" || lower == "disc" || lower == "circle" || lower == "square" ||
                 lower == "decimal" || lower == "lower-alpha" || lower == "upper-alpha" ||
                 lower == "lower-roman" || lower == "upper-roman")
                 result.Add(part); // list-style-type
             else if (lower == "inside" || lower == "outside")
                 result.Add(part); // list-style-position
             else if (lower == "none" || lower.StartsWith("url("))
                 result.Add(part); // list-style-image
         }
         return result;
     }

    private static bool IsBorderWidth(string value)
    {
        return value switch
        {
            "thin" or "medium" or "thick" => true,
            _ => value.EndsWith("px") || value.EndsWith("pt") || value.EndsWith("em")
        };
    }

    private static bool IsBorderStyle(string value)
    {
        return value switch
        {
            "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset" => true,
            _ => false
        };
    }

     private static string ParseUrlOrString(string css, ref int pos)
     {
         SkipWhitespace(css, ref pos);
         
         if (pos >= css.Length)
             return string.Empty;
         
         // Check for url(...)
         if (css[pos] == 'u' || css[pos] == 'U')
         {
             // Check if it starts with "url("
             if (pos + 3 < css.Length && 
                 (css[pos] == 'u' || css[pos] == 'U') &&
                 (css[pos + 1] == 'r' || css[pos + 1] == 'R') &&
                 (css[pos + 2] == 'l' || css[pos + 2] == 'L') &&
                 css[pos + 3] == '(')
             {
                 pos += 4; // Skip "url("
                 SkipWhitespace(css, ref pos);
                 
                 var urlSb = new StringBuilder();
                 while (pos < css.Length && css[pos] != ')')
                 {
                     urlSb.Append(css[pos]);
                     pos++;
                 }
                 
                 if (pos < css.Length && css[pos] == ')')
                     pos++; // Skip ')'
                 
                 return urlSb.ToString().Trim().Trim('\'', '"');
             }
         }
         
         // Check for quoted string
         if (css[pos] == '"' || css[pos] == '\'')
         {
             char quote = css[pos];
             pos++; // Skip opening quote
             
             var sb = new StringBuilder();
             while (pos < css.Length && css[pos] != quote)
             {
                 sb.Append(css[pos]);
                 pos++;
             }
             
             if (pos < css.Length && css[pos] == quote)
                 pos++; // Skip closing quote
             
             return sb.ToString();
         }
         
         // Unquoted value - parse until whitespace or closing paren
         var valueSb = new StringBuilder();
         while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != ')')
         {
             valueSb.Append(css[pos]);
             pos++;
         }
         
         return valueSb.ToString();
     }

     private static void SkipWhitespace(string css, ref int pos)
     {
         while (pos < css.Length && char.IsWhiteSpace(css[pos]))
             pos++;
     }

    private static void SkipToSemicolon(string css, ref int pos)
    {
        while (pos < css.Length && css[pos] != ';')
            pos++;
        if (pos < css.Length) pos++; // Skip ;
    }

    private static void SkipToSemicolonOrBrace(string css, ref int pos)
    {
        while (pos < css.Length && css[pos] != ';' && css[pos] != '}')
            pos++;
        if (pos < css.Length && css[pos] == ';') pos++;
    }

    private static void SkipToMatchingBrace(string css, ref int pos)
    {
        int depth = 0;
        while (pos < css.Length)
        {
            if (css[pos] == '{') depth++;
            else if (css[pos] == '}')
            {
                depth--;
                if (depth < 0) break;
            }
            pos++;
        }
        if (pos < css.Length) pos++; // Skip }
    }
}
