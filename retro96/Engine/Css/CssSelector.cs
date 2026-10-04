using System;
using System.Collections.Generic;
using System.Linq;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96.Engine.Css;

/// <summary>
/// A parsed CSS1 selector.  Parts are stored left-to-right; matching runs
/// RIGHT-TO-LEFT (rightmost simple selector matches the element, combinators
/// walk up/across the tree) — the same shape as real engines, which makes
/// descendant / child / sibling selectors actually work.
/// </summary>
public record CssSelector(IReadOnlyList<SelectorPart> Parts)
{
    public string? PseudoElementName =>
        GetPseudoElementName();

    private string? GetPseudoElementName()
    {
        foreach (var part in Parts)
            if (part.Kind == PartType.PseudoElement)
                return part.Value;
        return null;
    }

    /// <summary>
    /// Specificity (b, c, d): b = IDs, c = classes/attributes/pseudo-classes,
    /// d = types/pseudo-elements.
    /// </summary>
    public (int b, int c, int d) Specificity
    {
        get
        {
            int b = 0, c = 0, d = 0;
            foreach (var part in Parts)
            {
                switch (part.Kind)
                {
                    case PartType.Id: b++; break;
                    case PartType.Class:
                    case PartType.PseudoClass:
                    case PartType.Attribute: c++; break;
                    case PartType.Type:
                    case PartType.PseudoElement: d++; break;
                }
            }
            return (b, c, d);
        }
    }

    /// <summary>
    /// Parse a selector string (used by querySelector / querySelectorAll).
    /// Supports type, .class, #id, [attr], :pseudo, ::pseudo-element, and
    /// the combinators ' ', '&gt;', '+', '~'.
    /// </summary>
    public static List<CssSelector> ParseSelector(string selector)
    {
        var selectors = new List<CssSelector>();
        if (string.IsNullOrWhiteSpace(selector))
            return selectors;

        foreach (var part in selector.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = ParseOneSelector(part);
            if (parts.Count > 0)
                selectors.Add(new CssSelector(parts));
        }
        return selectors;
    }

    private static List<SelectorPart> ParseOneSelector(string selector)
    {
        var parts = new List<SelectorPart>();
        string current = "";
        int i = 0;

        while (i < selector.Length)
        {
            char c = selector[i];

            if (char.IsWhiteSpace(c))
            {
                FlushPart(parts, ref current);

                // Look ahead for a following combinator ('>' '+' '~')
                int j = i;
                char combinator = ' ';
                while (j < selector.Length)
                {
                    if (char.IsWhiteSpace(selector[j])) { j++; continue; }
                    if (selector[j] is '>' or '+' or '~')
                    {
                        combinator = selector[j];
                        j++;
                    }
                    break;
                }

                if (combinator == ' ')
                {
                    // Descendant — only if more content follows
                    int k = j;
                    while (k < selector.Length && char.IsWhiteSpace(selector[k])) k++;
                    if (k < selector.Length)
                        parts.Add(new SelectorPart(PartType.Descendant, null));
                    i = k;
                }
                else
                {
                    FlushPart(parts, ref current);
                    parts.Add(new SelectorPart(
                        combinator == '>' ? PartType.Child :
                        combinator == '+' ? PartType.AdjacentSibling :
                                            PartType.GeneralSibling, null));
                    while (j < selector.Length && char.IsWhiteSpace(selector[j])) j++;
                    i = j;
                }
                continue;
            }

            if (c is '>' or '+' or '~')
            {
                FlushPart(parts, ref current);
                parts.Add(new SelectorPart(
                    c == '>' ? PartType.Child :
                    c == '+' ? PartType.AdjacentSibling :
                              PartType.GeneralSibling, null));
                i++;
                continue;
            }

            if (c is '.' or '#' or ':')
            {
                // "::" is ONE pseudo-element marker — flushing between the
                // two colons used to emit a stray empty ":pseudo-class"
                // part that never matched anything.
                if (c == ':' && i + 1 < selector.Length && selector[i + 1] == ':')
                {
                    FlushPart(parts, ref current);
                    current = "::";
                    i += 2;
                    continue;
                }
                // Start of a new simple selector on the same subject
                FlushPart(parts, ref current);
                current = c.ToString();
                i++;
                continue;
            }

            if (c == '[')
            {
                FlushPart(parts, ref current);
                i++;
                var attrSb = new System.Text.StringBuilder();
                while (i < selector.Length && selector[i] != ']')
                {
                    attrSb.Append(selector[i]);
                    i++;
                }
                if (i < selector.Length) i++; // ']'
                parts.Add(new SelectorPart(PartType.Attribute, attrSb.ToString().Trim()));
                continue;
            }

            current += c;
            i++;
        }

        FlushPart(parts, ref current);
        return parts;
    }

    private static void FlushPart(List<SelectorPart> parts, ref string current)
    {
        if (string.IsNullOrEmpty(current))
            return;
        parts.Add(current[0] switch
        {
            '.' => new SelectorPart(PartType.Class, current[1..]),
            '#' => new SelectorPart(PartType.Id, current[1..]),
            // "::before" is a pseudo-ELEMENT; CSS1/CSS2 also spell
            // first-line/first-letter/before/after with ONE colon, and
            // those must classify as pseudo-elements too (the old flush
            // parsed ":before" as a pseudo-class that never matched).
            ':' => current.Length > 1 && current[1] == ':'
                ? new SelectorPart(PartType.PseudoElement, current[2..].ToLowerInvariant())
                : IsPseudoElementName(current[1..])
                    ? new SelectorPart(PartType.PseudoElement, current[1..].ToLowerInvariant())
                    : new SelectorPart(PartType.PseudoClass, current[1..]),
            '*' => new SelectorPart(PartType.Universal, null),
            _ => new SelectorPart(PartType.Type, current.ToLowerInvariant())
        });
        current = "";
    }

    /// <summary>CSS1/CSS2 pseudo-element names — single-colon spelling
    /// routes to PartType.PseudoElement.</summary>
    private static bool IsPseudoElementName(string name)
    {
        string n = name.ToLowerInvariant();
        return n is "first-line" or "first-letter" or "before" or "after";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Matching (right to left)
    // ─────────────────────────────────────────────────────────────────────

    public bool Matches(DomElement element)
    {
        if (element == null || Parts == null || Parts.Count == 0)
            return false;
        return MatchRight(element, Parts.Count - 1);
    }

    private bool MatchRight(DomElement element, int partIdx)
    {
        // Walked off the left edge — everything matched.
        if (partIdx < 0)
            return true;

        var part = Parts[partIdx];

        switch (part.Kind)
        {
            case PartType.Type:
            case PartType.Class:
            case PartType.Id:
            case PartType.Attribute:
            case PartType.PseudoClass:
            case PartType.PseudoElement:
            case PartType.Universal:
                if (!PartMatches(element, part))
                    return false;
                return MatchRight(element, partIdx - 1);

            case PartType.Descendant:
                {
                    var ancestor = element.Parent as DomElement;
                    while (ancestor != null)
                    {
                        if (MatchRight(ancestor, partIdx - 1))
                            return true;
                        ancestor = ancestor.Parent as DomElement;
                    }
                    return false;
                }

            case PartType.Child:
                return element.Parent is DomElement parent &&
                       MatchRight(parent, partIdx - 1);

            case PartType.AdjacentSibling:
                {
                    var prev = PreviousElementSibling(element);
                    return prev != null && MatchRight(prev, partIdx - 1);
                }

            case PartType.GeneralSibling:
                {
                    var prev = PreviousElementSibling(element);
                    while (prev != null)
                    {
                        if (MatchRight(prev, partIdx - 1))
                            return true;
                        prev = PreviousElementSibling(prev);
                    }
                    return false;
                }

            default:
                return false;
        }
    }

    private static DomElement? PreviousElementSibling(DomElement element)
    {
        if (element.Parent == null) return null;
        DomElement? prev = null;
        foreach (var child in element.Parent.Children)
        {
            if (ReferenceEquals(child, element))
                return prev;
            if (child is DomElement de)
                prev = de;
        }
        return null;
    }

    private static bool PartMatches(DomElement element, SelectorPart part)
    {
        return part.Kind switch
        {
            PartType.Type =>
                part.Value == null ||
                element.TagName.Equals(part.Value, StringComparison.OrdinalIgnoreCase),
            PartType.Class =>
                part.Value != null && element.HasAttr("class") &&
                element.GetAttr("class")!.Split()
                       .Contains(part.Value, StringComparer.Ordinal),
            PartType.Id =>
                part.Value != null && element.HasAttr("id") &&
                element.GetAttr("id")!.Equals(part.Value, StringComparison.Ordinal),
            PartType.Attribute =>
                MatchesAttribute(element, part.Value),
            PartType.PseudoClass =>
                MatchesPseudoClass(element, part.Value),
            PartType.PseudoElement =>
                true, // attached to the element's own box
            PartType.Universal => true,
            _ => false
        };
    }

    private static bool MatchesAttribute(DomElement element, string? attrExpr)
    {
        if (string.IsNullOrEmpty(attrExpr))
            return false;

        // [attr]
        int eq = IndexOfTopLevelEquals(attrExpr);
        if (eq < 0)
            return element.HasAttr(attrExpr.Trim());

        string attrName = attrExpr[..eq].Trim();
        string opAndValue = attrExpr[(eq + 1)..].Trim();

        char op = '=';
        if (attrName.EndsWith('~') || attrName.EndsWith('|') ||
            attrName.EndsWith('^') || attrName.EndsWith('$') || attrName.EndsWith('*'))
        {
            op = attrName[^1];
            attrName = attrName[..^1];
        }

        string attrValue = opAndValue.Trim('"', '\'');

        if (!element.HasAttr(attrName))
            return false;

        string? elemValue = element.GetAttr(attrName);
        if (elemValue == null)
            return false;

        return op switch
        {
            '=' => elemValue.Equals(attrValue, StringComparison.Ordinal),
            '~' => elemValue.Split().Contains(attrValue, StringComparer.Ordinal),
            '|' => elemValue.Equals(attrValue, StringComparison.Ordinal) ||
                    elemValue.StartsWith(attrValue + "-", StringComparison.Ordinal),
            '^' => attrValue.Length > 0 && elemValue.StartsWith(attrValue, StringComparison.Ordinal),
            '$' => attrValue.Length > 0 && elemValue.EndsWith(attrValue, StringComparison.Ordinal),
            '*' => attrValue.Length > 0 && elemValue.Contains(attrValue, StringComparison.Ordinal),
            _ => false
        };
    }

    private static int IndexOfTopLevelEquals(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '=') return i;
            if (c is '"' or '\'')
            {
                char quote = c;
                i++;
                while (i < s.Length && s[i] != quote) i++;
            }
        }
        return -1;
    }

    private static bool MatchesPseudoClass(DomElement element, string? pseudoClass)
    {
        if (string.IsNullOrEmpty(pseudoClass))
            return false;

        string pc = pseudoClass.ToLowerInvariant();

        // :lang(xx) — CSS2: the element's language (its own lang attribute
        // or the nearest ancestor's) matches the argument as a case-
        // insensitive hyphen-separated prefix (:lang(en) matches "en"
        // and "en-US", not "enx").
        if (pc.StartsWith("lang(") && pc.EndsWith(")"))
        {
            string arg = pc[5..^1].Trim().Trim('\'', '"');
            return MatchesLang(element, arg);
        }

        switch (pc)
        {
            case "link":
                return element.TagName == "a" && element.HasAttr("href") &&
                       !IsVisited(element);
            case "visited":
                return element.TagName == "a" && element.HasAttr("href") &&
                       IsVisited(element);
            case "hover":
                return IsDynamicStateFor(element, element.OwnerDocument()?.HoveredElement);
            case "active":
                return IsDynamicStateFor(element, element.OwnerDocument()?.ActiveElement);
            case "focus":
                return IsDynamicStateFor(element, element.OwnerDocument()?.FocusedElement);
            case "first-child":
                // CSS2: the element is the first ELEMENT child of its parent
                // (text/comment siblings do not count).
                return element.Parent == null || PreviousElementSibling(element) == null;
            default:
                return false;
        }
    }

    private static bool MatchesLang(DomElement element, string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return false;

        for (DomNode? node = element; node != null; node = node.Parent)
        {
            if (node is DomElement e)
            {
                string? lang = e.GetAttr("lang") ?? e.GetAttr("xml:lang");
                if (!string.IsNullOrEmpty(lang))
                {
                    string value = lang.Trim();
                    return value.Equals(arg, StringComparison.OrdinalIgnoreCase) ||
                           (value.Length > arg.Length &&
                            value[arg.Length] == '-' &&
                            value.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        return false;
    }

    private static bool IsDynamicStateFor(DomElement selectorElement, DomElement? stateElement)
    {
        if (stateElement == null) return false;
        if (ReferenceEquals(selectorElement, stateElement)) return true;

        // :hover/:active/:focus apply to an ancestor while a descendant is
        // the actual hit/focus target. Walk the target upward through the
        // DOM so a:hover still matches for nested spans/images.
        for (DomNode? node = stateElement.Parent; node != null; node = node.Parent)
        {
            if (ReferenceEquals(node, selectorElement)) return true;
        }
        return false;
    }

    private static bool IsVisited(DomElement element)
    {
        if (element.TagName != "a" || !element.HasAttr("href"))
            return false;

        var doc = element.OwnerDocument();
        if (doc == null)
            return false;

        string href = element.GetAttr("href")!;
        if (string.IsNullOrEmpty(href))
            return false;

        // Compare against absolute resolved URLs when possible.
        try
        {
            string abs = doc.BaseUrl != null
                ? (doc.BaseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? FileUrls.Resolve(doc.BaseUrl, href)
                    : doc.BaseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) ? FileUrls.Resolve(doc.BaseUrl, href) : doc.BaseUrl.Resolve(href).ToAbsolute())
                : href;
            return doc.VisitedUrls.Contains(abs) || doc.VisitedUrls.Contains(href);
        }
        catch
        {
            return doc.VisitedUrls.Contains(href);
        }
    }
}

public enum PartType
{
    Type, Class, Id, PseudoClass, PseudoElement, Universal,
    Descendant, Child, AdjacentSibling, GeneralSibling, Attribute
}

public record SelectorPart(PartType Kind, string? Value);