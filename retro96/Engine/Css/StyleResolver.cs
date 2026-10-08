using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Css;

/// <summary>
/// Walks the DOM tree and fills DomElement.Style on every element.
/// Implements the Netscape Navigator 3 UA defaults plus the 1996 HTML
/// presentational attributes (BODY/FONT/TD/HR/DIV colours, alignment,
/// BASEFONT tracking).
///
/// Cascade order (later wins):
///   1. UA defaults (in code)
///   2. Author stylesheet rules (source order)
///   3. Inline STYLE=
///   4. HTML presentational attributes (applied last — they are the
///      "closest to the content" in 1996 practice)
///
/// Presentational attributes are applied DURING the walk, before an
/// element's children inherit from it — a separate post-pass applied
/// them after every descendant had already copied its inherited values,
/// so <body text=red> and <font color=red> never coloured anything
/// inside them.
/// </summary>
public static class StyleResolver
{
    private sealed record HoverSelectorEntry(CssSelector Selector, bool RequiresLayout);
    private sealed record HoverRuleEntry(IReadOnlyList<HoverSelectorEntry> Selectors);

    private static readonly ConditionalWeakTable<DomDocument, HoverRuleEntry> HoverRuleCache = new();
    private static readonly object HoverRuleCacheLock = new();

    /// <summary>
    /// Returns whether the current hovered element or one of its ancestors
    /// matches an author :hover selector. A hover with no matching rule needs
    /// cursor/status updates, not a full document reflow.
    /// </summary>
    internal static bool HasMatchingHoverRule(DomDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!HoverRuleCache.TryGetValue(doc, out var entry))
            entry = CacheHoverRules(doc, ReadAuthorRules(doc));
        if (doc.HoveredElement == null) return false;

        foreach (var element in HoveredElementAndAncestors(doc.HoveredElement))
        {
            foreach (var entrySelector in entry.Selectors)
                if (entrySelector.Selector.Matches(element))
                    return true;
        }

        return false;
    }

    /// <summary>
    /// Returns whether the current hovered element matches a rule that can
    /// change layout. Paint-only hover changes can update styles and redraw
    /// without rebuilding the document's layout tree.
    /// </summary>
    internal static bool HasLayoutAffectingHoverRule(DomDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!HoverRuleCache.TryGetValue(doc, out var entry))
            entry = CacheHoverRules(doc, ReadAuthorRules(doc));
        if (doc.HoveredElement == null) return false;

        foreach (var element in HoveredElementAndAncestors(doc.HoveredElement))
        {
            foreach (var entrySelector in entry.Selectors)
                if (entrySelector.RequiresLayout &&
                    entrySelector.Selector.Matches(element))
                    return true;
        }

        return false;
    }

    private static IEnumerable<DomElement> HoveredElementAndAncestors(DomElement hovered)
    {
        for (DomNode? node = hovered; node is DomElement element; node = element.Parent)
            yield return element;
    }

    private static List<CssRule> ReadAuthorRules(DomDocument doc)
    {
        var rules = new List<CssRule>();
        if (doc.AuthorStylesDisabled)
            return rules;
        foreach (var element in doc.ElementDescendants())
        {
            if (element.TagName != "style") continue;
            if (!MediaAppliesTo(element.GetAttr("media"), doc.MediaType)) continue;
            foreach (var text in element.Children.OfType<DomText>())
                if (!string.IsNullOrEmpty(text.Data))
                {
                    var (parsedRules, _) = CssParser.Parse(text.Data, doc.MediaType);
                    rules.AddRange(parsedRules);
                }
        }
        return rules;
    }

    /// <summary>
    /// CSS2 media matching for &lt;style media=…&gt; attributes: applies when
    /// the list is absent/empty or contains "screen"/"all" (comma lists
    /// count, print-only sheets do not).  Mirrors the CssParser @media
    /// behaviour.
    /// </summary>
    internal static bool MediaAppliesTo(string? media, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(media))
            return true;
        foreach (var part in media.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            if (token.Equals(mediaType, StringComparison.OrdinalIgnoreCase) ||
                token.Equals("all", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static HoverRuleEntry CacheHoverRules(DomDocument doc, IEnumerable<CssRule> rules)
    {
        var selectors = rules
            .SelectMany(rule => rule.Selectors
                .Where(selector => selector.Parts.Any(part =>
                    part.Kind == PartType.PseudoClass &&
                    part.Value?.Equals("hover", StringComparison.OrdinalIgnoreCase) == true))
                .Select(selector => new HoverSelectorEntry(
                    selector, rule.Declarations.Any(declaration =>
                        !IsPaintOnlyHoverProperty(declaration.Property)))))
            .ToArray();
        var entry = new HoverRuleEntry(selectors);
        lock (HoverRuleCacheLock)
        {
            HoverRuleCache.Remove(doc);
            HoverRuleCache.Add(doc, entry);
            return entry;
        }
    }

    private static bool IsPaintOnlyHoverProperty(string property) =>
        property.Trim().ToLowerInvariant() is
            "color" or "background" or "background-color" or "background-image" or
            "background-repeat" or "background-position" or "background-attachment" or
            "text-decoration" or "text-shadow" or "cursor" or "outline" or
            "outline-color" or "outline-style" or "outline-width" or
            "border-color" or "border-top-color" or "border-right-color" or
            "border-bottom-color" or "border-left-color";

    // ── Document colour accessors (used by the Renderer) ─────────────────

    public static Color GetLinkColor(DomDocument doc) =>
        UsableColor(ParseHtmlColor(doc.BodyLinkColor), Color.FromArgb(0x00, 0x00, 0xEE));

    public static Color GetVLinkColor(DomDocument doc) =>
        UsableColor(ParseHtmlColor(doc.BodyVLinkColor), Color.FromArgb(0x55, 0x1A, 0x8B));

    public static Color GetALinkColor(DomDocument doc) =>
        UsableColor(ParseHtmlColor(doc.BodyALinkColor), Color.FromArgb(0xFF, 0x00, 0x00));

    private static Color UsableColor(Color? parsed, Color fallback) =>
        parsed is { } color && color != Color.Empty && color != Color.Transparent
            ? color : fallback;

    internal static Color? ParseHtmlColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string v = value.Trim();
        // HTML 3.x accepted bare 3/6-digit hexadecimal colours in
        // presentational attributes (e.g. LINK="CC3333", TEXT="FFFFFF").
        // CSS requires the '#', so keep this compatibility rule here instead
        // of weakening ComputedStyle.ParseColor for actual CSS.
        if (!v.StartsWith('#') &&
            ((v.Length == 3 || v.Length == 6) &&
             v.All(c => Uri.IsHexDigit(c))))
        {
            v = "#" + v;
        }

        try
        {
            return ComputedStyle.ParseColor(v, Color.Transparent);
        }
        catch
        {
            return null;
        }
    }

    // ── Entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolve styles for the whole document.  viewportWidth is what CSS
    /// percentages resolve against (the old code hardcoded 800 everywhere;
    /// the parameter defaults to that so existing callers compile — pass
    /// the real canvas width for accurate %).
    /// </summary>
    public static void Resolve(DomDocument doc, float viewportWidth = 800f)
    {
        if (doc == null)
            return;

        // Author rules from every <style> element (including <link
        // rel=stylesheet> sheets that the loader injected as <style> nodes).
        // CSS2: a <style media=…> attribute limits the sheet to the listed
        // media types — non-screen sheets are skipped (the attribute was
        // previously ignored entirely, so print-only <style> blocks
        // applied on screen).
        var authorRules = new List<CssRule>();
        if (!doc.AuthorStylesDisabled)
        {
            foreach (var styleElem in doc.ElementDescendants())
            {
                if (styleElem.TagName != "style")
                    continue;
                if (!MediaAppliesTo(styleElem.GetAttr("media"), doc.MediaType))
                    continue;
                foreach (var child in styleElem.Children)
                {
                    if (child is DomText t && !string.IsNullOrEmpty(t.Data))
                    {
                        var (rules, _) = CssParser.Parse(t.Data, doc.MediaType);
                        authorRules.AddRange(rules);
                    }
                }
            }
        }

        CacheHoverRules(doc, authorRules);
        var ruleIndex = new AuthorRuleIndex(authorRules, doc);
        int activeBaseFontSize = doc.BaseFontSize;
        ResolveNode(doc, null, ruleIndex, doc, viewportWidth, ref activeBaseFontSize);
        int quoteDepth = 0;
        ResolveGeneratedContent(
            doc, new Dictionary<string, List<int>>(StringComparer.Ordinal), ref quoteDepth);

        float textSizeScale = float.IsFinite(doc.TextSizeScale)
            ? Math.Clamp(doc.TextSizeScale, 0.5f, 3f)
            : 1f;
        if (textSizeScale != 1f)
        {
            foreach (var element in doc.ElementDescendants())
                if (element.Style != null)
                    element.Style.FontSize *= textSizeScale;
        }
    }

    private static readonly QuotePair[] DefaultQuotes =
    [
        new("\u201c", "\u201d"),
        new("\u2018", "\u2019")
    ];

    private static void ResolveGeneratedContent(
        DomNode node, Dictionary<string, List<int>> counters, ref int quoteDepth)
    {
        if (node is DomElement element)
        {
            var style = element.Style;
            var resetScopes = new List<string>();
            ApplyCounterActions(counters, style?.CounterReset, reset: true, resetScopes);
            ApplyCounterActions(counters, style?.CounterIncrement, reset: false);
            ResolvePseudoContent(element, style?.GeneratedBefore, counters, ref quoteDepth);

            foreach (var child in element.Children)
                ResolveGeneratedContent(child, counters, ref quoteDepth);

            ResolvePseudoContent(element, style?.GeneratedAfter, counters, ref quoteDepth);
            PopCounterScopes(counters, resetScopes);
            return;
        }

        foreach (var child in node.Children)
            ResolveGeneratedContent(child, counters, ref quoteDepth);
    }

    private static void ResolvePseudoContent(DomElement element, ComputedStyle? pseudo,
        Dictionary<string, List<int>> counters, ref int quoteDepth)
    {
        if (pseudo?.Content == null)
            return;

        var resetScopes = new List<string>();
        ApplyCounterActions(counters, pseudo.CounterReset, reset: true, resetScopes);
        ApplyCounterActions(counters, pseudo.CounterIncrement, reset: false);

        var text = new System.Text.StringBuilder();
        IReadOnlyList<QuotePair> quotePairs =
            (pseudo.Quotes ?? element.Style?.Quotes) is { Count: > 0 } authoredQuotes
            ? authoredQuotes
            : DefaultQuotes;

        foreach (var token in pseudo.Content)
        {
            switch (token.Type)
            {
                case "string":
                    text.Append(token.Text);
                    break;
                case "attr":
                    text.Append(element.GetAttr(token.Text) ?? string.Empty);
                    break;
                case "counter":
                    {
                        var pieces = token.Text.Split(',', 2);
                        string name = pieces[0].Trim();
                        int value = CurrentCounter(counters, name);
                        string format = pieces.Length > 1 ? pieces[1].Trim() : "decimal";
                        text.Append(FormatCounter(value, format));
                        break;
                    }
                case "counters":
                    {
                        var pieces = token.Text.Split(',', 2);
                        string name = pieces[0].Trim();
                        string separator = pieces.Length > 1
                            ? pieces[1].Trim().Trim('\'', '"')
                            : string.Empty;
                        if (counters.TryGetValue(name, out var values))
                            text.Append(string.Join(separator,
                                values.Select(value => FormatCounter(value, "decimal"))));
                        break;
                    }
                case "open-quote":
                    text.Append(quotePairs[Math.Min(quoteDepth, quotePairs.Count - 1)].Open);
                    quoteDepth++;
                    break;
                case "close-quote":
                    quoteDepth = Math.Max(0, quoteDepth - 1);
                    text.Append(quotePairs[Math.Min(quoteDepth, quotePairs.Count - 1)].Close);
                    break;
                case "no-open-quote":
                    quoteDepth++;
                    break;
                case "no-close-quote":
                    quoteDepth = Math.Max(0, quoteDepth - 1);
                    break;
            }
        }

        pseudo.ResolvedGeneratedContentText = text.ToString();
        PopCounterScopes(counters, resetScopes);
    }

    private static void ApplyCounterActions(
        Dictionary<string, List<int>> counters, List<CounterAction>? actions,
        bool reset, List<string>? resetScopes = null)
    {
        if (actions == null) return;
        foreach (var action in actions)
        {
            if (reset)
            {
                if (!counters.TryGetValue(action.Name, out var values))
                    counters[action.Name] = values = new List<int>();
                values.Add(action.Value);
                resetScopes?.Add(action.Name);
            }
            else
            {
                if (!counters.TryGetValue(action.Name, out var values) || values.Count == 0)
                    counters[action.Name] = values = new List<int> { action.Value };
                else
                    values[^1] += action.Value;
            }
        }
    }

    private static int CurrentCounter(Dictionary<string, List<int>> counters, string name) =>
        counters.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : 0;

    private static void PopCounterScopes(
        Dictionary<string, List<int>> counters, List<string> resetScopes)
    {
        for (int i = resetScopes.Count - 1; i >= 0; i--)
        {
            string name = resetScopes[i];
            if (!counters.TryGetValue(name, out var values) || values.Count == 0)
                continue;
            values.RemoveAt(values.Count - 1);
            if (values.Count == 0)
                counters.Remove(name);
        }
    }

    private static string FormatCounter(int value, string format) =>
        format.ToLowerInvariant() switch
        {
            "upper-roman" => FormatRomanCounter(value, lowercase: false),
            "lower-roman" => FormatRomanCounter(value, lowercase: true),
            _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    private static string FormatRomanCounter(int value, bool lowercase)
    {
        if (value is < 1 or > 3999)
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        ReadOnlySpan<(int Value, string Numeral)> numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];
        var result = new System.Text.StringBuilder();
        foreach (var (amount, numeral) in numerals)
            while (value >= amount)
            {
                result.Append(numeral);
                value -= amount;
            }
        string text = result.ToString();
        return lowercase ? text.ToLowerInvariant() : text;
    }

    private sealed class AuthorRuleIndex
    {
        private readonly Dictionary<string, CssRule[]> _rulesByTag;

        public AuthorRuleIndex(IReadOnlyList<CssRule> rules, DomDocument document)
        {
            var tags = document.ElementDescendants()
                .Select(element => element.TagName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidates = tags.ToDictionary(
                tag => tag,
                _ => new List<CssRule>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var rule in rules)
            {
                var subjectTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool canMatchAnyTag = false;

                foreach (var selector in rule.Selectors)
                {
                    bool hasSubjectType = false;
                    for (int i = selector.Parts.Count - 1; i >= 0; i--)
                    {
                        var part = selector.Parts[i];
                        if (part.Kind is PartType.Descendant or PartType.Child or
                            PartType.AdjacentSibling or PartType.GeneralSibling)
                            break;

                        if (part.Kind == PartType.Type && !string.IsNullOrEmpty(part.Value))
                        {
                            subjectTags.Add(part.Value);
                            hasSubjectType = true;
                        }
                    }

                    if (!hasSubjectType)
                        canMatchAnyTag = true;
                }

                if (canMatchAnyTag)
                {
                    foreach (var candidateList in candidates.Values)
                        candidateList.Add(rule);
                }
                else
                {
                    foreach (var tag in subjectTags)
                        if (candidates.TryGetValue(tag, out var candidateList))
                            candidateList.Add(rule);
                }
            }

            _rulesByTag = candidates.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<CssRule> ForTag(string tag) =>
            _rulesByTag.TryGetValue(tag, out var rules) ? rules : Array.Empty<CssRule>();
    }

    private static void ResolveNode(DomNode node, ComputedStyle? parentStyle,
                                    AuthorRuleIndex authorRules, DomDocument doc,
                                    float viewportWidth, ref int activeBaseFontSize)
    {
        if (node is DomElement elem)
        {
            var style = parentStyle != null
                ? ComputedStyle.Inherit(parentStyle, Array.Empty<CssDeclaration>())
                : new ComputedStyle();

            var parentFs = parentStyle?.FontSize ?? 16f;

            // 1. UA defaults in code (baseline).
            ApplyUaDefaults(elem, style, parentFs, doc);

            // 2. Author CSS rules — the CSS1 cascade.  Declarations are
            //    collected across ALL matching rules and applied in
            //    (specificity, source order) so a class rule beats an
            //    element rule regardless of sheet order, and a later rule
            //    only wins at equal specificity.  !important declarations
            //    apply in a second tier AFTER inline STYLE= — a casual
            //    later rule (or inline style) can never stomp an important
            //    one, which the old apply-in-source-order loop allowed.
            List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>? importantDecls = null;
            List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>? normalDecls = null;
            Dictionary<string, List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>>? pseudoNormalDecls = null;
            Dictionary<string, List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>>? pseudoImportantDecls = null;
            int order = 0;
            foreach (var rule in authorRules.ForTag(elem.TagName))
            {
                foreach (var selector in rule.Selectors)
                {
                    if (selector.Matches(elem))
                    {
                        string? pseudo = selector.PseudoElementName;
                        foreach (var decl in rule.Declarations)
                        {
                            if (pseudo != null)
                            {
                                var target = decl.Important
                                    ? pseudoImportantDecls ??= new(StringComparer.OrdinalIgnoreCase)
                                    : pseudoNormalDecls ??= new(StringComparer.OrdinalIgnoreCase);
                                if (!target.TryGetValue(pseudo, out var list))
                                    target[pseudo] = list = new();
                                list.Add((decl, selector.Specificity, order));
                            }
                            else if (decl.Important)
                                (importantDecls ??= new()).Add((decl, selector.Specificity, order));
                            else
                                (normalDecls ??= new()).Add((decl, selector.Specificity, order));
                            order++;
                        }
                        break;
                    }
                }
            }
            if (normalDecls != null)
                foreach (var (decl, _, _) in
                         normalDecls.OrderBy(x => x.Spec).ThenBy(x => x.Order))
                    style.Apply(decl, parentFs, viewportWidth,
                        parentStyle?.FontWeight ?? FontWeightValue.Normal, parentStyle);

            // 3. Inline STYLE= — outranks non-important author rules.
            var inlineStyle = doc.AuthorStylesDisabled ? null : elem.GetAttr("style");
            if (!string.IsNullOrEmpty(inlineStyle))
            {
                foreach (var decl in CssParser.ParseInlineStyle(inlineStyle))
                {
                    if (decl.Important)
                        (importantDecls ??= new()).Add((decl, (1_000_000, 0, 0), int.MaxValue));
                    else
                        style.Apply(decl, parentFs, viewportWidth,
                            parentStyle?.FontWeight ?? FontWeightValue.Normal, parentStyle);
                }
            }

            // 3b. !important tier — beats every non-important declaration,
            //     inline included.
            if (importantDecls != null)
                foreach (var (decl, _, _) in
                         importantDecls.OrderBy(x => x.Spec).ThenBy(x => x.Order))
                    style.Apply(decl, parentFs, viewportWidth,
                        parentStyle?.FontWeight ?? FontWeightValue.Normal, parentStyle);

            if (activeBaseFontSize != 3 && !style.OwnFontSize &&
                elem.TagName is not ("h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
                                     "pre" or "listing" or "xmp" or "plaintext"))
            {
                style.FontSize = ComputedStyle.FontScaleToPx(activeBaseFontSize);
            }

            elem.Style = style;

            // 4. HTML presentational attributes — BEFORE recursing, so the
            // attributes that INHERIT (BODY text=, FONT color/face/size=)
            // are on the style children copy from.  Document order also
            // keeps BASEFONT ahead of the <font size=+n> elements that
            // read it.
            ApplyHtmlAttributes(elem, style, doc);

            if (style.BorderTopStyle is BorderStyleValue.None or BorderStyleValue.Hidden)
                style.BorderTopWidth = 0f;
            if (style.BorderRightStyle is BorderStyleValue.None or BorderStyleValue.Hidden)
                style.BorderRightWidth = 0f;
            if (style.BorderBottomStyle is BorderStyleValue.None or BorderStyleValue.Hidden)
                style.BorderBottomWidth = 0f;
            if (style.BorderLeftStyle is BorderStyleValue.None or BorderStyleValue.Hidden)
                style.BorderLeftWidth = 0f;

            // CSS list-style-type:none has no marker to reserve the normal
            // list gutter for.  Keep an explicitly authored margin-left, but
            // remove the UA 40px list margin when the marker is suppressed.
            if (elem.TagName is "ul" or "ol" or "menu" or "dir" &&
                style.ListStyleType == ListStyleType.None &&
                !style.OwnMarginLeft && style.MarginLeft == 40f)
            {
                style.MarginLeft = 0f;
            }

            // The UA list gutter is represented by 40px of left padding, not
            // by the marker itself.  list-style-type:none suppresses that
            // UA padding unless the author explicitly supplied padding-left
            // or padding shorthand.
            if (elem.TagName is "ul" or "ol" or "menu" or "dir" &&
                style.ListStyleType == ListStyleType.None &&
                !style.OwnPaddingLeft && style.PaddingLeft == 40f)
            {
                style.PaddingLeft = 0f;
            }

            if (elem.TagName == "basefont")
                activeBaseFontSize = doc.BaseFontSize;

            style.ResolvePendingLineHeight();

            ApplyPseudoStyle(style, pseudoNormalDecls, pseudoImportantDecls,
                "first-line", viewportWidth, s => style.FirstLineStyle = s);
            ApplyPseudoStyle(style, pseudoNormalDecls, pseudoImportantDecls,
                "first-letter", viewportWidth, s => style.FirstLetterStyle = s);
            // CSS2 generated content — :before/:after declarations land in
            // the GeneratedBefore/GeneratedAfter slots (same clone-apply
            // shape as FirstLineStyle; the renderer lays the content out).
            ApplyPseudoStyle(style, pseudoNormalDecls, pseudoImportantDecls,
                "before", viewportWidth, s => style.GeneratedBefore = s);
            ApplyPseudoStyle(style, pseudoNormalDecls, pseudoImportantDecls,
                "after", viewportWidth, s => style.GeneratedAfter = s);

            foreach (var child in elem.Children)
                ResolveNode(child, style, authorRules, doc, viewportWidth, ref activeBaseFontSize);
        }
        else
        {
            // Document / comment / doctype nodes — walk children.
            foreach (var child in node.Children)
                ResolveNode(child, parentStyle, authorRules, doc, viewportWidth, ref activeBaseFontSize);
        }
    }

    private static void ApplyPseudoStyle(
        ComputedStyle baseStyle,
        Dictionary<string, List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>>? normal,
        Dictionary<string, List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>>? important,
        string name, float viewportWidth,
        Action<ComputedStyle> assign)
    {
        List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>? normalList = null;
        List<(CssDeclaration Decl, (int b, int c, int d) Spec, int Order)>? importantList = null;
        normal?.TryGetValue(name, out normalList);
        important?.TryGetValue(name, out importantList);
        if (normalList == null && importantList == null)
            return;

        var pseudo = baseStyle.Clone();
        if (normalList != null)
            foreach (var (decl, _, _) in normalList.OrderBy(x => x.Spec).ThenBy(x => x.Order))
                pseudo.Apply(decl, baseStyle.FontSize, viewportWidth, baseStyle.FontWeight, baseStyle);
        if (importantList != null)
            foreach (var (decl, _, _) in importantList.OrderBy(x => x.Spec).ThenBy(x => x.Order))
                pseudo.Apply(decl, baseStyle.FontSize, viewportWidth, baseStyle.FontWeight, baseStyle);
        assign(pseudo);
    }

    // ─────────────────────────────────────────────────────────────────────
    // UA defaults (Netscape Navigator 3)
    // ─────────────────────────────────────────────────────────────────────

    private static void ApplyUaDefaults(DomElement elem, ComputedStyle style, float parentFontSize,
                                        DomDocument? doc = null)
    {
        switch (elem.TagName)
        {
            // Root / body
            case "html":
                style.Display = DisplayValue.Block;
                style.FontFamily = ["Times New Roman", "serif"];
                style.FontSize = 16f;
                style.Color = Color.Black;
                style.BackgroundColor = Color.White;
                break;
            case "body":
                style.Display = DisplayValue.Block;
                // Checklist §9: IE's default body margins are 10px left/right
                // and 15px top/bottom; Navigator kept 8px. Persona-gated so
                // the strict historical profiles match their screenshots.
                bool ieBodyMargins = BrowserRuntime.IsInternetExplorer3 || BrowserRuntime.IsInternetExplorer5;
                style.MarginTop = style.MarginBottom = ieBodyMargins ? 15f : 8f;
                style.MarginLeft = style.MarginRight = ieBodyMargins ? 10f : 8f;
                style.BackgroundColor = Color.White;
                style.Color = Color.Black;
                break;

            // Headings
            case "h1": Heading(2.00f, 0.67f); break;
            case "h2": Heading(1.50f, 0.83f); break;
            case "h3": Heading(1.17f, 1.00f); break;
            case "h4": Heading(1.00f, 1.33f); break;
            case "h5": Heading(0.83f, 1.67f); break;
            case "h6": Heading(0.67f, 2.33f); break;

            // Blocks
            case "p":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = parentFontSize;
                break;
            case "div":
            case "article":
            case "section":
            case "aside":
            case "header":
            case "footer":
            case "main":
            case "nav":
            case "figure":
            case "figcaption":
                style.Display = DisplayValue.Block;
                break;
            case "fieldset":
                style.Display = DisplayValue.Block;
                style.BorderTopWidth = style.BorderRightWidth =
                    style.BorderBottomWidth = style.BorderLeftWidth = 2f;
                style.BorderTopStyle = style.BorderRightStyle =
                    style.BorderBottomStyle = style.BorderLeftStyle = BorderStyleValue.Groove;
                break;
            case "legend":
                // IE5 renders the legend inset into the fieldset's top
                // border: a block with a small left offset.
                style.Display = DisplayValue.Block;
                style.MarginLeft = 10f;
                style.PaddingLeft = 2f;
                style.PaddingRight = 2f;
                break;
            case "center":
                style.Display = DisplayValue.Block;
                style.TextAlign = TextAlign.Center;
                break;
            case "blockquote":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = parentFontSize;
                style.MarginLeft = style.MarginRight = 40f;
                break;
            case "address":
                style.Display = DisplayValue.Block;
                style.FontStyle = FontStyleValue.Italic;
                break;
            case "pre":
            case "listing":
                style.Display = DisplayValue.Block;
                style.WhiteSpace = WhiteSpaceValue.Pre;
                style.FontFamily = ["Courier New", "monospace"];
                style.FontSize = 13f;
                style.MarginTop = style.MarginBottom = parentFontSize;
                break;
            case "xmp":
                style.Display = DisplayValue.Block;
                style.WhiteSpace = WhiteSpaceValue.Pre;
                style.FontFamily = ["Courier New", "monospace"];
                break;
            case "hr":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = 4f;
                break;
            case "marquee":
                style.Display = DisplayValue.Block;
                style.WhiteSpace = WhiteSpaceValue.Nowrap;
                style.Overflow = OverflowValue.Hidden;
                break;
            case "multicol":
                style.Display = DisplayValue.Block;
                break;
            case "form":
                style.Display = DisplayValue.Block;
                style.MarginBottom = 8f;
                break;
            case "isindex":
                style.Display = DisplayValue.Block;
                break;

            // Lists
            case "ul":
                style.Display = DisplayValue.Block;
                style.ListStyleType = DefaultUnorderedListType(elem);
                style.MarginTop = style.MarginBottom = IsNestedList(elem) ? 0f : parentFontSize;
                style.PaddingLeft = 40f;
                break;
            case "dir":
            case "menu":
                style.Display = DisplayValue.Block;
                style.ListStyleType = ListStyleType.Disc;
                style.MarginTop = style.MarginBottom = IsNestedList(elem) ? 0f : parentFontSize;
                style.PaddingLeft = 40f;
                break;
            case "ol":
                style.Display = DisplayValue.Block;
                style.ListStyleType = ListStyleType.Decimal;
                style.MarginTop = style.MarginBottom = IsNestedList(elem) ? 0f : parentFontSize;
                style.PaddingLeft = 40f;
                break;
            case "li":
                style.Display = DisplayValue.ListItem;
                style.MarginBottom = 2f;
                break;
            case "dl":
                style.Display = DisplayValue.Block;
                style.MarginTop = style.MarginBottom = parentFontSize;
                break;
            case "dt":
                style.Display = DisplayValue.Block;
                break;
            case "dd":
                style.Display = DisplayValue.Block;
                style.MarginLeft = 40f;
                break;

            // Tables
            case "table":
                style.Display = DisplayValue.Table;
                style.MarginTop = style.MarginBottom = 4f;
                break;
            case "tbody":
            case "thead":
            case "tfoot":
                style.Display = DisplayValue.TableRowGroup;
                break;
            case "tr":
                style.Display = DisplayValue.TableRow;
                break;
            case "td":
                style.Display = DisplayValue.TableCell;
                style.PaddingTop = style.PaddingBottom = 2f;
                style.PaddingLeft = style.PaddingRight = 5f;
                break;
            case "th":
                style.Display = DisplayValue.TableCell;
                style.FontWeight = FontWeightValue.Bold;
                style.TextAlign = TextAlign.Center;
                style.PaddingTop = style.PaddingBottom = 2f;
                style.PaddingLeft = style.PaddingRight = 5f;
                break;
            case "caption":
                style.Display = DisplayValue.TableCaption;
                style.TextAlign = TextAlign.Center;
                break;
            case "col":
            case "colgroup":
                style.Display = DisplayValue.None;
                break;

            // Inline text
            case "b":
            case "strong":
                style.FontWeight = FontWeightValue.Bold;
                break;
            case "i":
            case "em":
            case "cite":
            case "dfn":
            case "var":
                style.FontStyle = FontStyleValue.Italic;
                break;
            case "u":
            case "ins":
                style.TextDecoration |= TextDecoration.Underline;
                break;
            case "s":
            case "strike":
            case "del":
                style.TextDecoration |= TextDecoration.LineThrough;
                break;
            case "blink":
                style.TextDecoration |= TextDecoration.Blink;
                break;
            case "big":
                style.FontSize = parentFontSize * 1.17f;
                break;
            case "small":
                style.FontSize = parentFontSize * 0.83f;
                break;
            case "sup":
                style.FontSize = parentFontSize * 0.83f;
                style.VerticalAlign = VerticalAlign.Super;
                break;
            case "sub":
                style.FontSize = parentFontSize * 0.83f;
                style.VerticalAlign = VerticalAlign.Sub;
                break;
            case "code":
            case "tt":
            case "kbd":
            case "samp":
                style.FontFamily = ["Courier New", "monospace"];
                break;
            case "a":
                // Only actual links get link colour + underline; named
                // anchors (<a name=...>) are plain text.  The underline
                // now comes solely from this UA rule, so author CSS
                // `a { text-decoration: none }` can override it.
                //
                // The colour is SEEDED FROM THE DOCUMENT'S link colours
                // (BODY LINK=/VLINK= or the NN3 default): the computed
                // style then carries the real base colour, and author
                // CSS (a:link { color: … }) overrides it through the
                // normal cascade — the paint path can distinguish
                // "styled by the author" from "engine default".
                if (elem.HasAttr("href"))
                {
                    style.Color = doc != null ? GetLinkColor(doc)
                                              : Color.FromArgb(0x00, 0x00, 0xEE);
                    style.TextDecoration |= TextDecoration.Underline;
                }
                break;
            case "q":
                // CSS2 UA stylesheet: Q:before { content: open-quote }
                // Q:after { content: close-quote }.
                style.GeneratedBefore = new ComputedStyle
                {
                    Content = new List<ContentToken> { new ContentToken("open-quote", "") }
                };
                style.GeneratedAfter = new ComputedStyle
                {
                    Content = new List<ContentToken> { new ContentToken("close-quote", "") }
                };
                break;

            // Ruby (IE5, §14a): ruby text renders inline after the base
            // at half size; rp fallback punctuation is hidden because
            // ruby IS supported.
            case "ruby":
                style.Display = DisplayValue.Inline;
                break;
            case "rt":
                style.Display = DisplayValue.Inline;
                style.FontSize = parentFontSize * 0.5f;
                break;
            case "rp":
                style.Display = DisplayValue.None;
                break;

            // XML data islands (IE5): present in the DOM for script access,
            // never rendered. Same for the concealment wrappers whose
            // contents the parser already dropped.
            case "xml":
            case "comment":
            case "noembed":
            case "nolayer":
            case "keygen":
            case "server":
                style.Display = DisplayValue.None;
                break;

            // Non-rendering
            case "script":
            case "style":
            case "head":
            case "title":
            case "meta":
            case "link":
            case "base":
            case "basefont":
            case "bgsound":
                style.Display = DisplayValue.None;
                break;

            // <noscript>: hidden while scripting is enabled (the era
            // behaviour — NN3 with JS on never drew the fallback content,
            // e.g. the static Bravenet counter GIF), VISIBLE when JS is off
            // (that was its whole purpose).
            case "noscript":
                style.Display = doc?.ScriptingEnabled == true
                    ? DisplayValue.None
                    : style.Display;
                break;

            // Form controls: inline-block so they participate in line flow
            case "input":
            case "button":
            case "select":
                style.FontFamily = ["Arial", "Helvetica", "sans-serif"];
                style.Display = DisplayValue.InlineBlock;
                break;
            case "textarea":
                style.FontFamily = ["Courier New", "monospace"];
                style.Display = DisplayValue.InlineBlock;
                break;

            // Replaced inline
            case "img":
            case "embed":
            case "applet":
            case "object":
                style.Display = DisplayValue.Inline;
                break;

            case "iframe":
                style.Display = DisplayValue.Inline;
                break;

            case "br":
                style.Display = DisplayValue.Inline;
                break;

            case "nobr":
                style.Display = DisplayValue.Inline;
                style.WhiteSpace = WhiteSpaceValue.Nowrap;
                break;

            case "wbr":
            case "spacer":
            case "font":
            case "span":
            case "layer":
            case "ilayer":
            default:
                style.Display = DisplayValue.Inline;
                break;
        }

        return;

        void Heading(float sizeFactor, float marginFactor)
        {
            style.Display = DisplayValue.Block;
            style.FontWeight = FontWeightValue.Bold;
            style.FontSize = parentFontSize * sizeFactor;
            style.MarginTop = style.MarginBottom = parentFontSize * marginFactor;
        }
    }

    private static bool IsNestedList(DomElement elem)
    {
        for (var parent = elem.Parent; parent != null; parent = parent.Parent)
            if (parent is DomElement ancestor && ancestor.TagName == "li")
                return true;
        return false;
    }

    private static ListStyleType DefaultUnorderedListType(DomElement elem)
    {
        int depth = 0;
        for (var parent = elem.Parent; parent != null; parent = parent.Parent)
            if (parent is DomElement ancestor && ancestor.TagName is "ul" or "menu" or "dir")
                depth++;
        return depth switch
        {
            0 => ListStyleType.Disc,
            1 => ListStyleType.Circle,
            _ => ListStyleType.Square
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // HTML presentational attributes (applied per element, pre-recursion)
    // ─────────────────────────────────────────────────────────────────────

    private static void ApplyHtmlAttributes(DomElement elem, ComputedStyle style, DomDocument doc)
    {
        switch (elem.TagName)
        {
            case "body":
                {
                    // Colours live on the document (Renderer + link colours) and
                    // on the body style, where children INHERIT the text colour.
                    var bg = ParseHtmlColor(elem.GetAttr("bgcolor"));
                    if (bg is { } b) style.BackgroundColor = b;
                    var txt = ParseHtmlColor(elem.GetAttr("text"));
                    if (txt is { } t) style.Color = t;
                    if (elem.HasAttr("background"))
                    {
                        style.BackgroundImage = elem.GetAttr("background");
                        style.BackgroundRepeat = BackgroundRepeat.Repeat;
                    }
                    // leftmargin/topmargin/marginwidth/marginheight override the
                    // default 8px body margins.
                    int lm = Math.Max(elem.GetAttrInt("leftmargin", -1),
                                      elem.GetAttrInt("marginwidth", -1));
                    int rm = Math.Max(elem.GetAttrInt("rightmargin", -1),
                                      elem.GetAttrInt("marginwidth", -1));
                    int tm = Math.Max(elem.GetAttrInt("topmargin", -1),
                                      elem.GetAttrInt("marginheight", -1));
                    int bm = Math.Max(elem.GetAttrInt("bottommargin", -1),
                                      elem.GetAttrInt("marginheight", -1));
                    if (lm >= 0) style.MarginLeft = lm;
                    if (rm >= 0) style.MarginRight = rm;
                    if (tm >= 0) style.MarginTop = tm;
                    if (bm >= 0) style.MarginBottom = bm;
                    break;
                }

            case "basefont":
                // Track the document-wide base for relative <font size>.
                // Document order guarantees this runs before any <font>
                // that follows it in the source.
                var bf = elem.GetAttr("size");
                if (!string.IsNullOrEmpty(bf))
                {
                    int baseSize = ParseHtmlFontSize(bf, 3, doc.BaseFontSize);
                    doc.BaseFontSize = Math.Clamp(baseSize, 1, 7);
                }
                break;

            case "bdo":
                if (elem.GetAttr("dir")?.Trim().ToLowerInvariant() is "ltr" or "rtl")
                {
                    style.Direction = elem.GetAttr("dir")!.Trim().ToLowerInvariant() == "rtl"
                        ? DirectionValue.Rtl
                        : DirectionValue.Ltr;
                    style.UnicodeBidi = "bidi-override";
                }
                break;

            case "font":
                {
                    var color = ParseHtmlColor(elem.GetAttr("color"));
                    if (color is { } c) style.Color = c;

                    if (elem.HasAttr("face"))
                    {
                        var families = elem.GetAttr("face")!
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(f => f.Trim())
                            .Where(f => f.Length > 0)
                            .ToList();
                        if (families.Count > 0)
                            style.FontFamily = families;
                    }

                    var size = elem.GetAttr("size");
                    if (!string.IsNullOrEmpty(size))
                        style.FontSize = ComputedStyle.FontScaleToPx(
                            Math.Clamp(ParseHtmlFontSize(size, doc.BaseFontSize, 3), 1, 7));
                    break;
                }

            case "td":
            case "th":
                {
                    ApplyAlignAttr(elem, style);
                    ApplyVAlignAttr(elem, style);
                    var bg = ParseHtmlColor(elem.GetAttr("bgcolor"));
                    if (bg is { } c) style.BackgroundColor = c;
                    else if (style.BackgroundColor == Color.Transparent)
                    {
                        // Netscape cell > row > table precedence.  A row
                        // bgcolor used to die on the zero-size <tr> wrapper
                        // box and never reach the cells — header rows like
                        // <tr bgcolor="#000080"><th>… rendered white with
                        // white text (invisible).  The cell takes the ROW's
                        // colour when it sets none of its own.
                        var row = elem.Parent as DomElement;
                        var rowBg = ParseHtmlColor(row?.GetAttr("bgcolor"));
                        if (rowBg is { } rb) style.BackgroundColor = rb;
                    }
                    if (elem.HasAttr("nowrap"))
                        style.WhiteSpace = WhiteSpaceValue.Nowrap;
                    break;
                }

            case "tr":
            case "table":
                {
                    var bg = ParseHtmlColor(elem.GetAttr("bgcolor"));
                    if (bg is { } c) style.BackgroundColor = c;
                    break;
                }

            case "hr":
                {
                    // Netscape/IE accepted the presentational COLOR attribute
                    // on <hr>.  Resolve it here so the renderer has a concrete
                    // colour instead of falling back to the hard-coded grey
                    // 3-D rule.  The element colour is also the natural base
                    // for shaded rules that do not specify NOSHADE.
                    var hrColor = ParseHtmlColor(elem.GetAttr("color"));
                    if (hrColor is { } c)
                    {
                        style.Color = c;
                        style.BorderTopColor = c;
                    }

                    if (elem.HasAttr("noshade"))
                    {
                        style.BorderTopStyle = BorderStyleValue.Solid;
                        if (hrColor == null)
                            style.BorderTopColor = Color.FromArgb(128, 128, 128);
                    }
                    break;
                }

            case "div":
            case "p":
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
            case "caption":
                ApplyAlignAttr(elem, style);
                break;

            case "ul":
            case "ol":
            case "li":
                // HTML 3.2 list TYPE is a presentational declaration.  Keep
                // it in the computed style so the marker painter sees the
                // authored shape instead of the UA default (disc/decimal).
                ApplyLegacyListTypeAttr(elem, style);
                break;

            case "dd":
                // HTML 3.2 definition descriptions have a UA hanging indent.
                // Enforce it at the final presentational-attribute stage so a
                // later fallback style cannot accidentally collapse it to 0.
                style.MarginLeft = Math.Max(style.MarginLeft, 40f);
                break;
        }
    }

    private static void ApplyLegacyListTypeAttr(DomElement elem, ComputedStyle style)
    {
        string? raw = elem.GetAttr("type");
        if (string.IsNullOrWhiteSpace(raw)) return;

        string type = raw.Trim();
        if (elem.TagName is "ul" or "dir" or "menu" ||
            elem.TagName == "li" && elem.Parent is DomElement parent &&
            parent.TagName is "ul" or "dir" or "menu")
            type = type.ToLowerInvariant();
        ListStyleType mapped = type switch
        {
            // Unordered list shapes.
            "disc" => ListStyleType.Disc,
            "circle" => ListStyleType.Circle,
            "square" => ListStyleType.Square,

            // Ordered-list HTML TYPE values.
            "1" => ListStyleType.Decimal,
            "a" => ListStyleType.LowerAlpha,
            "A" => ListStyleType.UpperAlpha,
            "i" => ListStyleType.LowerRoman,
            "I" => ListStyleType.UpperRoman,
            _ => style.ListStyleType
        };

        // Only recognised HTML TYPE values are authored list-style choices.
        // Unknown values are tolerated and leave the current computed value
        // alone, as old browsers did.
        if (mapped != style.ListStyleType || type is "disc" or "circle" or "square" or
            "1" or "a" or "A" or "i" or "I")
        {
            style.ListStyleType = mapped;
            style.OwnListStyleType = true;
        }
    }

    private static void ApplyAlignAttr(DomElement elem, ComputedStyle style)
    {
        if (!elem.HasAttr("align")) return;
        style.TextAlign = elem.GetAttr("align")!.Trim().ToLowerInvariant() switch
        {
            "center" => TextAlign.Center,
            // "middle" — the HTML 3.0 synonym still all over 1996 pages
            // (Voyager's banner cell: <td align="middle" valign="center">);
            // dropping to Left pushed the banner exchange off-centre.
            "middle" => TextAlign.Center,
            "right" => TextAlign.Right,
            "justify" => TextAlign.Justify,
            _ => TextAlign.Left
        };
    }

    private static void ApplyVAlignAttr(DomElement elem, ComputedStyle style)
    {
        if (!elem.HasAttr("valign")) return;
        style.VerticalAlign = elem.GetAttr("valign")!.Trim().ToLowerInvariant() switch
        {
            "top" => VerticalAlign.Top,
            "middle" => VerticalAlign.Middle,
            // "center" — same HTML 3.0 synonym, middle-of-the-cell.
            "center" => VerticalAlign.Middle,
            "bottom" => VerticalAlign.Bottom,
            "baseline" => VerticalAlign.Baseline,
            _ => style.VerticalAlign
        };
    }

    /// <summary>
    /// Parse an HTML font-size attribute.  Absolute ("1"–"7") or relative
    /// ("+2"/"-1" against the base font size).
    /// </summary>
    internal static int ParseHtmlFontSize(string sizeStr, int baseSize, int fallback)
    {
        sizeStr = sizeStr.Trim();
        if (sizeStr.Length == 0)
            return fallback;

        if (sizeStr[0] is '+' or '-')
        {
            if (int.TryParse(sizeStr, out int rel))
                return Math.Clamp(baseSize + rel, 1, 7);
            return fallback;
        }

        if (int.TryParse(sizeStr, out int abs))
            return Math.Clamp(abs, 1, 7);

        return fallback;
    }
}