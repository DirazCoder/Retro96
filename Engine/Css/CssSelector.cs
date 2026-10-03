using System;
using System.Collections.Generic;
using System.Linq;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Css;

// Parsed form of a single CSS1 selector.
public record CssSelector(IReadOnlyList<SelectorPart> Parts)
{
    // Specificity (a,b,c,d) tuple — compare lexicographically.
    // a = inline style, b = ID, c = class/attribute/pseudo-class, d = type/pseudo-element
    public (int a, int b, int c, int d) Specificity
    {
        get
        {
            int b = Parts.Count(p => p.Kind == PartType.Id);
            int c = Parts.Count(p => p.Kind == PartType.Class || p.Kind == PartType.PseudoClass || p.Kind == PartType.Attribute);
            int d = Parts.Count(p => p.Kind == PartType.Type || p.Kind == PartType.PseudoElement);
            return (0, b, c, d); // a = 0 for non-inline styles
        }
    }

    /// <summary>
    /// Parse a CSS selector string into a list of CssSelector objects.
    /// Supports: type, .class, #id, [attr], :pseudo, >, +, ~, and descendant combinator.
    /// </summary>
    public static List<CssSelector> ParseSelector(string selector)
    {
        var selectors = new List<CssSelector>();
        var parts = new List<SelectorPart>();
        
        string current = "";
        int i = 0;
        
        while (i < selector.Length)
        {
            char c = selector[i];
            
            if (char.IsWhiteSpace(c))
            {
                // End current part if any
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                    current = "";
                }
                
                // Skip whitespace
                while (i < selector.Length && char.IsWhiteSpace(selector[i]))
                    i++;
                
                // Add descendant combinator
                parts.Add(new SelectorPart(PartType.Descendant, null));
                continue;
            }
            else if (c == '>')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                    current = "";
                }
                parts.Add(new SelectorPart(PartType.Child, null));
                i++;
            }
            else if (c == '+')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                    current = "";
                }
                parts.Add(new SelectorPart(PartType.AdjacentSibling, null));
                i++;
            }
            else if (c == '~')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                    current = "";
                }
                parts.Add(new SelectorPart(PartType.GeneralSibling, null));
                i++;
            }
            else if (c == '.')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                }
                current = ".";
                i++;
            }
            else if (c == '#')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                }
                current = "#";
                i++;
            }
            else if (c == ':')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                }
                current = ":";
                i++;
            }
            else if (c == '[')
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(ParseSelectorPart(current));
                    current = "";
                }
                // Parse attribute selector [attr] or [attr=value]
                i++;
                string attr = "";
                while (i < selector.Length && selector[i] != ']')
                {
                    attr += selector[i];
                    i++;
                }
                i++; // skip ']'
                parts.Add(new SelectorPart(PartType.Attribute, attr));
            }
            else
            {
                current += c;
                i++;
            }
        }
        
        // Add final part
        if (!string.IsNullOrEmpty(current))
        {
            parts.Add(ParseSelectorPart(current));
        }
        
        if (parts.Count > 0)
        {
            selectors.Add(new CssSelector(parts));
        }
        
        return selectors;
    }
    
    private static SelectorPart ParseSelectorPart(string part)
    {
        if (part.StartsWith("."))
        {
            return new SelectorPart(PartType.Class, part.Substring(1));
        }
        else if (part.StartsWith("#"))
        {
            return new SelectorPart(PartType.Id, part.Substring(1));
        }
        else if (part.StartsWith(":"))
        {
            return new SelectorPart(PartType.PseudoClass, part.Substring(1));
        }
        else if (part == "*")
        {
            return new SelectorPart(PartType.Universal, null);
        }
        else
        {
            return new SelectorPart(PartType.Type, part);
        }
    }

    // Returns true if this selector matches the given element in its DOM context.
    public bool Matches(DomElement element)
    {
        if (element == null || Parts == null || Parts.Count == 0)
            return false;

        int partIdx = 0;
        return MatchesRecursive(element, ref partIdx);
    }

    private bool MatchesRecursive(DomElement element, ref int partIdx)
    {
        if (partIdx >= Parts.Count)
            return true;

        var part = Parts[partIdx];

        // Handle descendant combinator
        if (part.Kind == PartType.Descendant)
        {
            partIdx++;
            if (partIdx >= Parts.Count)
                return true; // Trailing descendant combinator

            // Check if any ancestor matches the remaining parts
            // Start at element.Parent, not element (to avoid matching element against itself)
            var current = (DomElement?)element.Parent;
            while (current != null)
            {
                int savedIdx = partIdx;
                if (MatchesRecursive(current, ref savedIdx))
                    return true;
                current = (DomElement?)current.Parent;
            }
            return false;
        }

        // Handle child combinator (>)
        if (part.Kind == PartType.Child)
        {
            partIdx++;
            if (partIdx >= Parts.Count)
                return true; // Trailing child combinator

            // Check if parent matches the remaining parts
            if (element.Parent is DomElement parent)
            {
                int savedIdx = partIdx;
                return MatchesRecursive(parent, ref savedIdx);
            }
            return false;
        }

        // Handle adjacent sibling combinator (+)
        if (part.Kind == PartType.AdjacentSibling)
        {
            partIdx++;
            if (partIdx >= Parts.Count)
                return true; // Trailing adjacent sibling combinator

            // Check if previous sibling matches the remaining parts
            var prev = GetPreviousSibling(element);
            if (prev != null)
            {
                int savedIdx = partIdx;
                return MatchesRecursive(prev, ref savedIdx);
            }
            return false;
        }

        // Handle general sibling combinator (~)
        if (part.Kind == PartType.GeneralSibling)
        {
            partIdx++;
            if (partIdx >= Parts.Count)
                return true; // Trailing general sibling combinator

            // Check if any previous sibling matches the remaining parts
            var prev = GetPreviousSibling(element);
            while (prev != null)
            {
                int savedIdx = partIdx;
                if (MatchesRecursive(prev, ref savedIdx))
                    return true;
                prev = GetPreviousSibling(prev);
            }
            return false;
        }

        // Check if this part matches the current element
        if (!PartMatches(element, part))
            return false;

        partIdx++;
        return MatchesRecursive(element, ref partIdx);
    }

    private static DomElement? GetPreviousSibling(DomElement element)
    {
        if (element.Parent == null)
            return null;

        DomElement? prev = null;
        foreach (var child in element.Parent.Children)
        {
            if (child == element)
                return prev;
            if (child is DomElement domChild)
                prev = domChild;
        }
        return null;
    }

    private static bool PartMatches(DomElement element, SelectorPart part)
    {
        return part.Kind switch
        {
            PartType.Type => part.Value == null || element.TagName.Equals(part.Value, StringComparison.OrdinalIgnoreCase),
            PartType.Class => part.Value != null && element.HasAttr("class") &&
                                  element.GetAttr("class")!.Split().Contains(part.Value),
            PartType.Id => part.Value != null && element.HasAttr("id") &&
                                element.GetAttr("id")!.Equals(part.Value),
            PartType.Attribute => MatchesAttribute(element, part.Value),
            PartType.PseudoClass => MatchesPseudoClass(element, part.Value),
            PartType.PseudoElement => true, // Pseudo-elements are stored but not matched for styling
            PartType.Universal => true,
            _ => false
        };
    }

    private static bool MatchesAttribute(DomElement element, string? attrExpr)
    {
        if (string.IsNullOrEmpty(attrExpr) || element == null)
            return false;

        // [attr]
        if (!attrExpr.Contains('='))
            return element.HasAttr(attrExpr);

        // [attr=value] or [attr~=value] or [attr|=value]
        var match = System.Text.RegularExpressions.Regex.Match(attrExpr, @"([\w:-]+)([~|]?=)(.+)");
        if (!match.Success)
            return false;

        string attrName = match.Groups[1].Value;
        string op = match.Groups[2].Value;
        string attrValue = match.Groups[3].Value.Trim('\"', '\'');

        if (!element.HasAttr(attrName))
            return false;

        string? elemValue = element.GetAttr(attrName);
        if (elemValue == null)
            return false;

        return op switch
        {
            "=" => elemValue.Equals(attrValue, StringComparison.OrdinalIgnoreCase),
            "~=" => elemValue.Split().Contains(attrValue, StringComparer.OrdinalIgnoreCase),
            "|=" => elemValue.Equals(attrValue, StringComparison.OrdinalIgnoreCase) ||
                    elemValue.StartsWith(attrValue + "-", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool MatchesPseudoClass(DomElement element, string? pseudoClass)
    {
        if (string.IsNullOrEmpty(pseudoClass))
            return false;

        return pseudoClass.ToLowerInvariant() switch
        {
            "link" => element.TagName == "a" && element.HasAttr("href") && !IsVisited(element), // Unvisited link
            "visited" => element.TagName == "a" && element.HasAttr("href") && IsVisited(element), // Visited link
            "hover" => false, // Hover state is dynamic - checked during rendering
            "active" => false, // Active state is dynamic - checked during rendering
            "focus" => false, // Focus state is dynamic
            _ => false
        };
    }

    private static bool IsVisited(DomElement element)
    {
        // Check if the link's href is in the document's visited URLs set
        if (element == null || element.TagName != "a" || !element.HasAttr("href"))
            return false;

        // Walk up the tree to find the document
        DomNode? node = element;
        while (node != null && node.NodeType != NodeType.Document)
            node = node.Parent;

        if (node is DomDocument doc && doc.VisitedUrls != null)
        {
            string? href = element.GetAttr("href");
            return !string.IsNullOrEmpty(href) && doc.VisitedUrls.Contains(href);
        }

        return false;
    }
}

public enum PartType { Type, Class, Id, PseudoClass, PseudoElement, Universal, Descendant, Child, AdjacentSibling, GeneralSibling, Attribute }

public record SelectorPart(PartType Kind, string? Value);
