using System;
using System.Collections.Generic;
using System.Linq;
using Retro96.Engine.Network;
using Retro96.Engine.Css;
using Retro96.Engine.Layout;

namespace Retro96.Engine.Dom;

public enum NodeType { Document, Element, Text, Comment, Doctype }

public abstract class DomNode
{
    public NodeType      NodeType { get; protected init; }
    public DomNode?      Parent   { get; set; }
    public List<DomNode> Children { get; } = [];

    // Tree traversal helpers
    public DomNode? FirstChild => Children.Count > 0 ? Children[0]    : null;
    public DomNode? LastChild  => Children.Count > 0 ? Children[^1]   : null;

    public DomNode? NextSibling
    {
        get
        {
            if (Parent == null) return null;
            int idx = Parent.Children.IndexOf(this);
            if (idx < 0 || idx >= Parent.Children.Count - 1) return null;
            return Parent.Children[idx + 1];
        }
    }

    public DomNode? PreviousSibling
    {
        get
        {
            if (Parent == null) return null;
            int idx = Parent.Children.IndexOf(this);
            if (idx <= 0) return null;
            return Parent.Children[idx - 1];
        }
    }

    public void AppendChild(DomNode child)
    {
        if (child.Parent != null)
            child.Parent.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    public void InsertBefore(DomNode newNode, DomNode referenceNode)
    {
        if (newNode.Parent != null)
            newNode.Parent.Children.Remove(newNode);

        int idx = Children.IndexOf(referenceNode);
        if (idx < 0)
        {
            AppendChild(newNode);
        }
        else
        {
            newNode.Parent = this;
            Children.Insert(idx, newNode);
        }
    }

    public void RemoveChild(DomNode child)
    {
        if (Children.Remove(child))
            child.Parent = null;
    }

    // CSS selector query - fully implemented per CSS1
    public DomElement? QuerySelector(string selector)
    {
        // Parse the selector and find first match
        var selectors = CssSelector.ParseSelector(selector);
        return ElementChildren().FirstOrDefault(e => selectors.Any(s => s.Matches(e)));
    }

    public IEnumerable<DomElement> QuerySelectorAll(string selector)
    {
        // Parse the selector and find all matches
        var selectors = CssSelector.ParseSelector(selector);
        return ElementChildren().Where(e => selectors.Any(s => s.Matches(e)));
    }

    // Walk the entire subtree (depth-first pre-order)
    public IEnumerable<DomNode> Descendants()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.Descendants())
            {
                yield return descendant;
            }
        }
    }

    public IEnumerable<DomElement> ElementDescendants()
    {
        return Descendants().OfType<DomElement>();
    }

    // Walk only direct children (for QuerySelector to avoid matching self)
    public IEnumerable<DomElement> ElementChildren()
    {
        return Children.OfType<DomElement>();
    }
}

public class DomDocument : DomNode
{
    public string       Title     { get; set; } = "";
    public ParsedUrl?   BaseUrl   { get; set; }
    public string       Charset   { get; set; } = "iso-8859-1";
    public string       QuirksMode { get; set; } = "html32"; // "html32" | "html20" | "strict"
    public HashSet<string> VisitedUrls { get; } = [];
    public CookieStore  Cookies   { get; }

    // Indexed collections — live, not snapshots:
    public IEnumerable<DomElement> Forms   => ElementDescendants().Where(e => e.TagName == "form");
    public IEnumerable<DomElement> Images  => ElementDescendants().Where(e => e.TagName == "img");
    public IEnumerable<DomElement> Links   => ElementDescendants().Where(e =>
        (e.TagName == "a" && e.HasAttr("href")) ||
        (e.TagName == "link" && e.HasAttr("href")));
    public IEnumerable<DomElement> Anchors => ElementDescendants().Where(e => e.TagName == "a" && e.HasAttr("name"));

    public DomDocument(CookieStore cookies)
    {
        NodeType = NodeType.Document;
        Cookies = cookies;
    }
}

public class DomElement : DomNode
{
    public string TagName { get; }
    public Dictionary<string, string> Attrs { get; } = new(StringComparer.OrdinalIgnoreCase);
    // CSS computed style (set by StyleResolver):
    public ComputedStyle? Style   { get; set; }
    // Layout geometry (set by LayoutEngine):
    public LayoutBox?     Box     { get; set; }
    // JS event handlers parsed from HTML attributes (set by HtmlParser):
    public Dictionary<string, string> EventHandlers { get; } = [];

    public string? GetAttr(string name)
    {
        return Attrs.TryGetValue(name, out var value) ? value : null;
    }

    public bool HasAttr(string name)
    {
        return Attrs.ContainsKey(name);
    }

    public string GetAttrOrDefault(string name, string def)
    {
        var value = GetAttr(name);
        return value ?? def;
    }

    public int GetAttrInt(string name, int def)
    {
        var value = GetAttr(name);
        if (value != null && int.TryParse(value, out int result))
            return result;
        return def;
    }

    // Returns the element's inner text (concatenated Text node descendants):
    public string InnerText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            bool lastWasBlock = false;
            
            foreach (var node in Descendants())
            {
                // Skip style and script elements
                if (node is DomElement elem &&
                    (elem.TagName == "style" || elem.TagName == "script" || elem.TagName == "noscript"))
                {
                    continue;
                }
                
                if (node is DomText textNode)
                {
                    string text = textNode.Data;
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Add space between block-level elements
                        if (lastWasBlock && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                            sb.Append(' ');
                        sb.Append(text);
                        lastWasBlock = false;
                    }
                }
                else if (node is DomElement element)
                {
                    // Check if this is a block-level element
                    bool isBlock = IsBlockLevelElement(element.TagName);
                    if (isBlock && sb.Length > 0 && !lastWasBlock)
                    {
                        lastWasBlock = true;
                    }
                }
            }
            return sb.ToString();
        }
    }

    private static bool IsBlockLevelElement(string tagName)
    {
        return tagName switch
        {
            "div" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "ul" or "ol" or "li" or "dl" or "dt" or "dd" or "table" or "thead" or
            "tbody" or "tfoot" or "tr" or "td" or "th" or "blockquote" or "pre" or
            "hr" or "br" or "form" or "fieldset" or "legend" or "section" or "article" or
            "aside" or "header" or "footer" or "nav" or "main" or "figure" or "figcaption" or
            "details" or "summary" or "center" or "dir" or "menu" => true,
            _ => false
        };
    }

    public DomElement(string tagName) : base()
    {
        TagName = tagName.ToLowerInvariant();
        NodeType = NodeType.Element;
    }
}

public class DomText : DomNode
{
    public string Data  { get; set; } = "";

    public DomText()
    {
        NodeType = NodeType.Text;
    }
}

public class DomComment : DomNode
{
    public string Text  { get; }

    public DomComment(string text) : base()
    {
        Text = text;
        NodeType = NodeType.Comment;
    }
}

public class DomDoctype : DomNode
{
    public string RawText { get; }

    public DomDoctype(string rawText) : base()
    {
        RawText = rawText;
        NodeType = NodeType.Doctype;
    }
}
