using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Retro96.Engine.Css;

public record CssDeclaration(string Property, string Value, bool Important);

public record CssRule(IReadOnlyList<CssSelector> Selectors,
               IReadOnlyList<CssDeclaration> Declarations);

/// <summary>
/// CSS2 @import record.  Media is the comma-separated media descriptor
/// ("@import url(a.css) screen, print;"); null/empty means the sheet
/// applies to ALL media per CSS2 §7.2.2.  The import-expansion layer
/// (Form1.ExpandCssImportsAsync — outside the CSS engine's ownership)
/// must consult AppliesTo("screen") before pulling a sheet in; as of the
/// CSS2 upgrade it still imports print-only sheets.
/// </summary>
public record CssImportRule(string Url, IReadOnlyList<string>? Media = null)
{
    /// <summary>True when this import applies to the given media type
    /// (an absent media descriptor means all media).</summary>
    public bool AppliesTo(string mediaType)
    {
        if (Media is not { Count: > 0 })
            return true;
        return Media.Any(m =>
            m.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            m.Equals(mediaType, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// CSS1/CSS2 parser.  Produces one CssRule per selector, with shorthands expanded
/// into individual (property, value) declarations.  Unknown properties and
/// malformed values are skipped without affecting the rest of the rule.
/// </summary>
public static class CssParser
{
    public static (List<CssRule> Rules, List<CssImportRule> ImportRules) Parse(string css)
    {
        var rules = new List<CssRule>();
        var importRules = new List<CssImportRule>();
        if (string.IsNullOrEmpty(css))
            return (rules, importRules);

        css = RemoveComments(css);

        int pos = 0;
        while (pos < css.Length)
        {
            SkipWhitespace(css, ref pos);
            if (pos >= css.Length)
                break;

            if (css[pos] == '@')
            {
                HandleAtRule(css, ref pos, rules, importRules);
                continue;
            }

            var selectors = ParseSelectorList(css, ref pos);
            if (selectors == null || selectors.Count == 0)
            {
                // Nothing usable — make sure we still make progress.
                if (pos < css.Length && css[pos] == '{')
                {
                    // Stray block (e.g. a leading '{' or a selector we
                    // could not read anything from) — skip the whole block.
                    // The old code left pos ON the '{' here and looped
                    // forever on such input.
                    SkipToMatchingBrace(css, ref pos);
                }
                else if (pos < css.Length)
                {
                    pos++;
                }
                continue;
            }

            var declarations = ParseDeclarationBlock(css, ref pos);
            // Declarations may be empty (harmless); still register the rule so
            // the selector count matches author expectations.

            foreach (var selector in selectors)
                rules.Add(new CssRule(new[] { selector }, declarations));
        }

        return (rules, importRules);
    }

    /// <summary>Parse an inline STYLE="…" attribute value.</summary>
    public static List<CssDeclaration> ParseInlineStyle(string style)
    {
        var declarations = new List<CssDeclaration>();
        if (string.IsNullOrWhiteSpace(style))
            return declarations;

        int pos = 0;
        ParseDeclarations(RemoveComments(style), ref pos, declarations, requireBraces: false);
        return declarations;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lexical helpers
    // ─────────────────────────────────────────────────────────────────────

    private static string RemoveComments(string css)
    {
        var sb = new StringBuilder(css.Length);
        int pos = 0;
        char quote = '\0';
        while (pos < css.Length)
        {
            if (quote != '\0')
            {
                sb.Append(css[pos]);
                if (css[pos] == '\\' && pos + 1 < css.Length) { sb.Append(css[++pos]); }
                else if (css[pos] == quote) quote = '\0';
                pos++; continue;
            }
            if (css[pos] == '\'' || css[pos] == '"') { quote = css[pos]; sb.Append(css[pos++]); continue; }
            if (pos + 1 < css.Length && css[pos] == '/' && css[pos + 1] == '*')
            {
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
                // Unterminated comment: rest of the sheet is comment (rare).
            }
            else
            {
                sb.Append(css[pos]);
                pos++;
            }
        }
        return sb.ToString();
    }

    private static void SkipWhitespace(string css, ref int pos)
    {
        while (pos < css.Length && char.IsWhiteSpace(css[pos]))
            pos++;
    }

    // ─────────────────────────────────────────────────────────────────────
    // @-rules
    // ─────────────────────────────────────────────────────────────────────

    private static void HandleAtRule(string css, ref int pos,
        List<CssRule> rules, List<CssImportRule> importRules)
    {
        pos++; // skip '@'

        var nameSb = new StringBuilder();
        while (pos < css.Length && !char.IsWhiteSpace(css[pos]) &&
               css[pos] != '(' && css[pos] != ';' && css[pos] != '{')
        {
            nameSb.Append(char.ToLowerInvariant(css[pos]));
            pos++;
        }
        string name = nameSb.ToString();

        switch (name)
        {
            case "import":
                {
                    SkipWhitespace(css, ref pos);
                    string url = ParseUrlOrString(css, ref pos);
                    // CSS2: optional media descriptor after the URL
                    // ("@import url(a.css) screen, print;").  The old code
                    // DISCARDED it — captured here so the expansion layer
                    // can skip non-screen sheets (CssImportRule.AppliesTo).
                    int mediaStart = pos;
                    SkipToSemicolonOrBrace(css, ref pos);
                    var media = ParseMediaList(css[mediaStart..pos]);
                    importRules.Add(new CssImportRule(url, media));
                    break;
                }

            case "charset":
                SkipToSemicolon(css, ref pos);
                break;

            case "media":
                {
                    // @media screen, all { … } — apply if any positive media
                    // query names this renderer; explicit `not` negates it.
                    SkipWhitespace(css, ref pos);
                    string media = ReadUntil(css, ref pos, '{').Trim().ToLowerInvariant();
                    bool hasNot = media.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() == "not";
                    bool apply = !hasNot && media.Split(',').Any(part =>
                    {
                        string token = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                        return token is "screen" or "all";
                    });

                    if (pos < css.Length && css[pos] == '{')
                    {
                        pos++;
                        int braceDepth = 1;
                        var mediaCss = new StringBuilder();
                        while (pos < css.Length && braceDepth > 0)
                        {
                            if (css[pos] == '{') braceDepth++;
                            else if (css[pos] == '}') braceDepth--;
                            if (braceDepth > 0)
                                mediaCss.Append(css[pos]);
                            pos++;
                        }

                        if (apply)
                        {
                            var (innerRules, innerImports) = Parse(mediaCss.ToString());
                            rules.AddRange(innerRules);
                            importRules.AddRange(innerImports);
                        }
                    }
                    break;
                }

            default:
                // Unknown @rule (@font-face, @page, …) — skip its block if
                // it has one.
                SkipToMatchingBrace(css, ref pos);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Selectors
    // ─────────────────────────────────────────────────────────────────────

    private static List<CssSelector>? ParseSelectorList(string css, ref int pos)
    {
        var selectors = new List<CssSelector>();
        var parts = new List<SelectorPart>();
        bool inSelector = true;

        while (pos < css.Length && inSelector)
        {
            bool hadWhitespace = false;
            while (pos < css.Length && char.IsWhiteSpace(css[pos]))
            {
                hadWhitespace = true;
                pos++;
            }
            if (pos >= css.Length) break;

            char c = css[pos];

            if (hadWhitespace && parts.Count > 0 &&
                c is not ('{' or ',' or '>' or '+' or '~') &&
                parts[^1].Kind is not (PartType.Descendant or PartType.Child or
                    PartType.AdjacentSibling or PartType.GeneralSibling))
            {
                parts.Add(new SelectorPart(PartType.Descendant, null));
            }

            if (c == '{')
            {
                if (parts.Count > 0)
                    selectors.Add(new CssSelector(parts));
                // Leave pos ON the '{' — ParseDeclarationBlock consumes it.
                inSelector = false;
            }
            else if (c == ',')
            {
                if (parts.Count > 0)
                {
                    selectors.Add(new CssSelector(parts));
                    parts = new List<SelectorPart>();
                }
                pos++;
            }
            else
            {
                var part = ParseSelectorPart(css, ref pos);
                if (part != null)
                    parts.Add(part);
            }
        }

        return selectors.Count > 0 ? selectors : null;
    }

    private static SelectorPart? ParseSelectorPart(string css, ref int pos)
    {
        if (pos >= css.Length) return null;

        char c = css[pos];

        switch (c)
        {
            case '*':
                pos++;
                return new SelectorPart(PartType.Universal, null);

            case '#':
                {
                    pos++;
                    var sb = new StringBuilder();
                    while (pos < css.Length && !char.IsWhiteSpace(css[pos]) &&
                           css[pos] is not ('{' or ',' or '.' or '#' or ':' or '>' or '['))
                    {
                        sb.Append(css[pos]);
                        pos++;
                    }
                    return new SelectorPart(PartType.Id, sb.ToString());
                }

            case '.':
                {
                    pos++;
                    var sb = new StringBuilder();
                    while (pos < css.Length && !char.IsWhiteSpace(css[pos]) &&
                           css[pos] is not ('{' or ',' or '.' or '#' or ':' or '>' or '['))
                    {
                        sb.Append(css[pos]);
                        pos++;
                    }
                    return new SelectorPart(PartType.Class, sb.ToString());
                }

            case ':':
                {
                    pos++;
                    bool isElement = false;
                    if (pos < css.Length && css[pos] == ':')
                    {
                        pos++;
                        isElement = true;
                    }
                    var sb = new StringBuilder();
                    while (pos < css.Length && !char.IsWhiteSpace(css[pos]) &&
                           css[pos] is not ('{' or ',' or '.' or '#' or ':' or '>' or '['))
                    {
                        sb.Append(char.ToLowerInvariant(css[pos]));
                        pos++;
                    }
                    string name = sb.ToString();
                    // CSS1/CSS2 spell pseudo-elements with one colon
                    // (:first-line, :before, :after…).  Accept the later
                    // double-colon spelling too, and classify them correctly
                    // so their declarations reach the pseudo routing in
                    // StyleResolver — :before used to fall through as a
                    // pseudo-CLASS, which never matches anything.
                    bool css1PseudoElement = name is "first-line" or "first-letter"
                        or "before" or "after";
                    return isElement || css1PseudoElement
                        ? new SelectorPart(PartType.PseudoElement, name)
                        : new SelectorPart(PartType.PseudoClass, name);
                }

            case '>':
                pos++;
                return new SelectorPart(PartType.Child, null);

            case '+':
                pos++;
                return new SelectorPart(PartType.AdjacentSibling, null);

            case '~':
                pos++;
                return new SelectorPart(PartType.GeneralSibling, null);

            case '[':
                {
                    pos++;
                    var sb = new StringBuilder();
                    while (pos < css.Length && css[pos] != ']')
                    {
                        sb.Append(css[pos]);
                        pos++;
                    }
                    if (pos < css.Length) pos++; // ']'
                    return new SelectorPart(PartType.Attribute, sb.ToString().Trim());
                }

            default:
                {
                    if (!char.IsLetter(c))
                    {
                        pos++;
                        return null;
                    }
                    var sb = new StringBuilder();
                    while (pos < css.Length && !char.IsWhiteSpace(css[pos]) &&
                           css[pos] is not ('{' or ',' or '.' or '#' or ':' or '>' or '['))
                    {
                        sb.Append(char.ToLowerInvariant(css[pos]));
                        pos++;
                    }
                    return new SelectorPart(PartType.Type, sb.ToString());
                }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Declarations
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a comma-separated CSS2 media list ("screen, print") into
    /// lowercase tokens.  Empty/whitespace → null (all media).
    /// </summary>
    private static IReadOnlyList<string>? ParseMediaList(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
            return null;
        var parts = descriptor.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim(';', '}').Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.ToLowerInvariant())
            .ToList();
        return parts.Count > 0 ? parts : null;
    }

    private static List<CssDeclaration> ParseDeclarationBlock(string css, ref int pos)
    {
        var declarations = new List<CssDeclaration>();
        ParseDeclarations(css, ref pos, declarations, requireBraces: true);
        return declarations;
    }

    private static void ParseDeclarations(string css, ref int pos,
        List<CssDeclaration> declarations, bool requireBraces)
    {
        if (requireBraces)
        {
            SkipWhitespace(css, ref pos);
            if (pos >= css.Length || css[pos] != '{')
                return;
            pos++;
        }

        while (pos < css.Length)
        {
            SkipWhitespace(css, ref pos);
            if (pos >= css.Length)
                break;

            char c = css[pos];

            if (c == ';')
            {
                pos++;
                continue;
            }

            if (c == '}')
            {
                pos++;
                break;
            }

            // Property name
            int nameStart = pos;
            while (pos < css.Length &&
                   css[pos] != ':' && !char.IsWhiteSpace(css[pos]) &&
                   css[pos] != ';' && css[pos] != '}')
            {
                pos++;
            }
            if (pos >= css.Length) break;

            string property = css[nameStart..pos].ToLowerInvariant().Trim();
            if (property.Length == 0)
            {
                pos++;
                continue;
            }

            SkipWhitespace(css, ref pos);

            if (pos >= css.Length || css[pos] != ':')
            {
                SkipToSemicolonOrBrace(css, ref pos);
                continue;
            }
            pos++; // ':'

            SkipWhitespace(css, ref pos);

            // Value: up to ';' or '}' at paren-depth 0
            var valueSb = new StringBuilder();
            int parenDepth = 0;
            char quote = '\0';
            bool invalidValue = false;
            while (pos < css.Length)
            {
                char v = css[pos];
                if (quote != '\0')
                {
                    valueSb.Append(v);
                    if (v == '\\' && pos + 1 < css.Length) valueSb.Append(css[++pos]);
                    else if (v == quote) quote = '\0';
                    pos++;
                    continue;
                }
                if (v is '\'' or '"') { quote = v; valueSb.Append(v); pos++; continue; }
                if ((v == ';' || v == '}') && parenDepth == 0) break;
                if (v == ':' && parenDepth == 0)
                    invalidValue = true;
                if (v == '(') parenDepth++;
                else if (v == ')') parenDepth = Math.Max(0, parenDepth - 1);
                valueSb.Append(v); pos++;
            }

            if (pos < css.Length && css[pos] == ';')
                pos++;

            if (invalidValue)
                continue;

            string value = valueSb.ToString().Trim();

            // !important
            bool important = false;
            int bang = value.LastIndexOf('!');
            if (bang >= 0)
            {
                var afterBang = value[(bang + 1)..].Trim();
                if (afterBang.Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    important = true;
                    value = value[..bang].Trim();
                }
            }

            if (value.Length == 0)
                continue;

            // Shorthand expansion → individual declarations.  Unknown
            // properties pass through unexpanded; ComputedStyle ignores them.
            foreach (var (prop, val) in ExpandShorthand(property, value))
                declarations.Add(new CssDeclaration(prop, val, important));
        }
    }

    /// <summary>
    /// Expand shorthand properties into (property, value) pairs.  Everything
    /// else returns a single (property, value) entry.
    /// </summary>
    private static IEnumerable<(string Prop, string Val)> ExpandShorthand(string property, string value)
    {
        // CSS2 'inherit' on a shorthand must NOT be split into longhands —
        // ComputedStyle.ApplyInherit copies every sub-property from the
        // parent as a unit (border: inherit pulls the parent's computed
        // width/style/color for all four sides).  Expanding here would
        // keep only the pieces the per-shorthand expansion happens to
        // recognise and reset the rest to initial values.
        if (value.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase))
            return new[] { (property, value) };

        switch (property)
        {
            case "border-width":
            case "border-top-width":
            case "border-right-width":
            case "border-bottom-width":
            case "border-left-width":
                return IsValidBorderWidthList(value)
                    ? new[] { (property, value) }
                    : Array.Empty<(string, string)>();

            case "margin":
                return ExpandBox(property, value);

            case "padding":
                return ExpandBox(property, value);

            case "border":
                {
                    // border: [width] [style] [color] — any order, each optional.
                    // Expands to ALL FOUR sides — the old expansion produced
                    // only border-top-*, so `border: 1px solid red` in a
                    // stylesheet or STYLE= attribute painted the top edge only.
                    string width = "", style = "", color = "";
                    foreach (var part in SplitTopLevel(value))
                    {
                        var lower = part.ToLowerInvariant();
                        if (lower is "none" or "hidden" or "dotted" or "dashed" or "solid" or
                                "double" or "groove" or "ridge" or "inset" or "outset")
                            style = part;
                        else if (lower is "thin" or "medium" or "thick" || IsLengthToken(lower))
                            width = part;
                        else
                            color = part;
                    }

                    var result = new List<(string, string)>();
                    foreach (var side in new[] { "top", "right", "bottom", "left" })
                    {
                        if (width.Length > 0)
                            result.Add(($"border-{side}-width", width));
                        result.Add(($"border-{side}-style", style.Length > 0 ? style : "none"));
                        if (color.Length > 0)
                            result.Add(($"border-{side}-color", color));
                    }
                    return result;
                }

            // Per-side border shorthands (CSS1 core): border-bottom: 2px
            // solid #000080 — the "horizontal rule under a heading" idiom.
            // These used to fall through to the default branch and VANISH
            // entirely (no expansion, no longhands, nothing painted), so
            // every CSS page that underlined its headings with a bottom
            // border rendered without the rule.
            case "border-top":
            case "border-right":
            case "border-bottom":
            case "border-left":
                {
                    string side = property["border-".Length..];
                    string width = "", style = "", color = "";
                    foreach (var part in SplitTopLevel(value))
                    {
                        var lower = part.ToLowerInvariant();
                        if (lower is "none" or "hidden" or "dotted" or "dashed" or "solid" or
                                "double" or "groove" or "ridge" or "inset" or "outset")
                            style = part;
                        else if (lower is "thin" or "medium" or "thick" || IsLengthToken(lower))
                            width = part;
                        else
                            color = part;
                    }

                    var result = new List<(string, string)>();
                    if (width.Length > 0)
                        result.Add(($"border-{side}-width", width));
                    result.Add(($"border-{side}-style", style.Length > 0 ? style : "none"));
                    if (color.Length > 0)
                        result.Add(($"border-{side}-color", color));
                    return result;
                }

            case "font":
                return ExpandFont(value);

            case "list-style":
                {
                    var result = new List<(string, string)>();
                    foreach (var part in SplitTopLevel(value))
                    {
                        var lower = part.ToLowerInvariant();
                        if (lower is "disc" or "circle" or "square" or "decimal" or
                                "lower-alpha" or "upper-alpha" or "lower-greek" or
                                "lower-roman" or "upper-roman" or "none")
                            result.Add(("list-style-type", part));
                        else if (lower is "inside" or "outside")
                            result.Add(("list-style-position", part));
                        else if (lower.StartsWith("url(") || lower == "none")
                            result.Add(("list-style-image", part));
                    }
                    return result;
                }

            default:
                // background and other multi-value properties are handled
                // whole by ComputedStyle.Apply.
                return new[] { (property, value) };
        }
    }

    private static bool IsValidBorderWidthList(string value)
    {
        var parts = SplitTopLevel(value);
        if (parts.Count is < 1 or > 4)
            return false;

        return parts.All(part =>
        {
            string token = part.Trim().ToLowerInvariant();
            if (token is "thin" or "medium" or "thick")
                return true;

            string number = token;
            if (token.EndsWith("px") || token.EndsWith("pt") ||
                token.EndsWith("pc") || token.EndsWith("in") ||
                token.EndsWith("cm") || token.EndsWith("mm") ||
                token.EndsWith("em") || token.EndsWith("ex"))
                number = token[..^2];
            else if (token != "0")
                return false;

            return double.TryParse(number,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out double parsed) &&
                   double.IsFinite(parsed) && parsed >= 0;
        });
    }

    private static IEnumerable<(string, string)> ExpandBox(string property, string value)
    {
        var parts = SplitTopLevel(value);
        string[] box = parts.Count switch
        {
            1 => new[] { parts[0], parts[0], parts[0], parts[0] },
            2 => new[] { parts[0], parts[1], parts[0], parts[1] },
            3 => new[] { parts[0], parts[1], parts[2], parts[1] },
            _ when parts.Count >= 4 => new[] { parts[0], parts[1], parts[2], parts[3] },
            _ => Array.Empty<string>()
        };

        if (box.Length == 0)
            return new[] { (property, value) };

        return new[]
        {
            (property + "-top",    box[0]),
            (property + "-right",  box[1]),
            (property + "-bottom", box[2]),
            (property + "-left",   box[3])
        };
    }

    private static IEnumerable<(string, string)> ExpandFont(string value)
    {
        var parts = SplitTopLevel(value);

        // CSS2 system fonts — font: caption | icon | menu | message-box |
        // small-caption | status-bar.  Mapped to plausible Win98 desktop
        // values (MS Sans Serif at the era control sizes).
        if (parts.Count == 1)
        {
            string? sysSize = parts[0].ToLowerInvariant() switch
            {
                "caption" or "icon" or "menu" => "13px",
                "message-box" => "14px",
                "small-caption" => "11px",
                "status-bar" => "12px",
                _ => null
            };
            if (sysSize != null)
            {
                return new[]
                {
                    ("font-style", "normal"),
                    ("font-variant", "normal"),
                    ("font-weight", "normal"),
                    ("font-size", sysSize),
                    ("font-family", "MS Sans Serif, sans-serif")
                };
            }
        }

        string fontStyle = "normal", fontVariant = "normal", fontWeight = "normal";
        string? size = null, lineHeight = null, family = null;

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var lower = part.ToLowerInvariant();

            if (size != null)
                break;

            if (lower is "italic" or "oblique") { fontStyle = part; continue; }
            if (lower == "small-caps") { fontVariant = part; continue; }
            if (lower is "bold" or "bolder" or "lighter" ||
                (lower.Length == 3 && int.TryParse(lower, out _)))
            { fontWeight = part; continue; }

            // "12px/1.5" — size and line-height glued together (no spaces).
            // The old code only recognised a separate "/1.5" token, so the
            // common glued form silently lost BOTH size and family.
            if (part.Contains('/'))
            {
                var bits = part.Split('/', 2);
                if (LooksLikeSize(bits[0].ToLowerInvariant()))
                {
                    size = bits[0];
                    if (bits.Length > 1)
                        lineHeight = bits[1];
                    if (i + 1 < parts.Count)
                        family = string.Join(" ", parts.Skip(i + 1));
                    break;
                }
            }

            if (LooksLikeSize(lower))
            {
                size = part;
                if (i + 1 < parts.Count && parts[i + 1].StartsWith("/"))
                {
                    lineHeight = parts[i + 1][1..];
                    i++;
                }
                if (i + 1 < parts.Count)
                    family = string.Join(" ", parts.Skip(i + 1));
                break;
            }
        }

        var result = new List<(string, string)>
        {
            ("font-style", fontStyle),
            ("font-variant", fontVariant),
            ("font-weight", fontWeight)
        };
        if (size != null) result.Add(("font-size", size));
        if (lineHeight != null) result.Add(("line-height", lineHeight));
        if (family != null) result.Add(("font-family", family));
        return result;

        static bool LooksLikeSize(string p) =>
            p.EndsWith("px") || p.EndsWith("pt") || p.EndsWith("em") || p.EndsWith("%") ||
            p is "xx-small" or "x-small" or "small" or "medium"
                   or "large" or "x-large" or "xx-large";
    }

    /// <summary>Any token usable as a length: px/pt/em or a bare number.</summary>
    private static bool IsLengthToken(string s) =>
        s.EndsWith("px") || s.EndsWith("pt") || s.EndsWith("em") ||
        (s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '.') && !s.Contains('('));

    private static bool TryNum(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.NumberStyles.Float.ToString() == "" ? default : System.Globalization.CultureInfo.InvariantCulture, out _);

    // ─────────────────────────────────────────────────────────────────────
    // Shared utilities
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Split on whitespace, keeping parenthesised groups intact.</summary>
    internal static List<string> SplitTopLevel(string value)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (char c in value)
        {
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);

            if (depth == 0 && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    private static string ParseUrlOrString(string css, ref int pos)
    {
        SkipWhitespace(css, ref pos);
        if (pos >= css.Length)
            return "";

        // url(...)
        if (pos + 3 < css.Length &&
            (css[pos] is 'u' or 'U') && (css[pos + 1] is 'r' or 'R') &&
            (css[pos + 2] is 'l' or 'L') && css[pos + 3] == '(')
        {
            pos += 4;
            SkipWhitespace(css, ref pos);
            var sb = new StringBuilder();
            while (pos < css.Length && css[pos] != ')')
            {
                sb.Append(css[pos]);
                pos++;
            }
            if (pos < css.Length) pos++; // ')'
            return sb.ToString().Trim().Trim('\'', '"');
        }

        // Quoted string
        if (css[pos] is '"' or '\'')
        {
            char quote = css[pos];
            pos++;
            var sb = new StringBuilder();
            while (pos < css.Length && css[pos] != quote)
            {
                sb.Append(css[pos]);
                pos++;
            }
            if (pos < css.Length) pos++;
            return sb.ToString();
        }

        // Bare token
        var bareSb = new StringBuilder();
        while (pos < css.Length && !char.IsWhiteSpace(css[pos]) && css[pos] != ')' && css[pos] != ';')
        {
            bareSb.Append(css[pos]);
            pos++;
        }
        return bareSb.ToString();
    }

    private static string ReadUntil(string css, ref int pos, char terminator)
    {
        int start = pos;
        while (pos < css.Length && css[pos] != terminator) pos++;
        return css[start..pos];
    }

    private static void SkipToSemicolon(string css, ref int pos)
    {
        while (pos < css.Length && css[pos] != ';')
            pos++;
        if (pos < css.Length) pos++;
    }

    private static void SkipToSemicolonOrBrace(string css, ref int pos)
    {
        while (pos < css.Length && css[pos] != ';' && css[pos] != '}')
            pos++;
        if (pos < css.Length && css[pos] == ';')
            pos++;
    }

    /// <summary>
    /// Skips a whole { … } block (or up to ';' for blockless rules).
    /// The old version kept scanning after the closing brace until it met
    /// ANOTHER '}' and then skipped that one too — so an unknown @rule
    /// like @font-face consumed the closing brace of the rule after it,
    /// turning that rule's declarations into garbage.
    /// </summary>
    private static void SkipToMatchingBrace(string css, ref int pos)
    {
        // Find the block (or a blockless terminator first)
        while (pos < css.Length && css[pos] != '{' && css[pos] != ';')
        {
            if (char.IsWhiteSpace(css[pos])) { pos++; continue; }
            // Some other construct — bail out; the caller's loop advances.
            return;
        }
        if (pos >= css.Length)
            return;
        if (css[pos] == ';')
        {
            pos++;
            return;
        }

        pos++; // consume '{'
        int depth = 1;
        while (pos < css.Length && depth > 0)
        {
            if (css[pos] == '{') depth++;
            else if (css[pos] == '}') depth--;
            pos++;
        }
    }
}