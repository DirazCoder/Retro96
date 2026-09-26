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
        Parts.FirstOrDefault(p => p.Kind == PartType.PseudoElement)?.Value;

    /// <summary>
    /// Specificity (b, c, d): b = IDs, c = classes/attributes/pseudo-classes,
    /// d = types/pseudo-elements.
    /// </summary>
    public (int b, int c, int d) Specificity
    {
        get
        {
            int b = Parts.Count(p => p.Kind == PartType.Id);
            int c = Parts.Count(p => p.Kind is PartType.Class or PartType.PseudoClass or PartType.Attribute);
            int d = Parts.Count(p => p.Kind is PartType.Type or PartType.PseudoElement);
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
            // "::before" is a pseudo-ELEMENT — the old flush only looked at
            // the first ':' and parsed it as pseudo-class ":before".
            ':' => current.Length > 1 && current[1] == ':'
                ? new SelectorPart(PartType.PseudoElement, current[2..])
                : new SelectorPart(PartType.PseudoClass, current[1..]),
            '*' => new SelectorPart(PartType.Universal, null),
            _ => new SelectorPart(PartType.Type, current.ToLowerInvariant())
        });
        current = "";
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

        switch (pseudoClass.ToLowerInvariant())
        {
            case "link":
                return element.TagName == "a" && element.HasAttr("href") &&
                       !IsVisited(element);
            case "visited":
                return element.TagName == "a" && element.HasAttr("href") &&
                       IsVisited(element);
            default:
                // :hover/:active/:focus are dynamic — not part of static
                // style resolution; unknown pseudo-classes simply never match.
                return false;
        }
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