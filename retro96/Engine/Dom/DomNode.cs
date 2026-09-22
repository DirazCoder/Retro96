using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Retro96.Engine.Css;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;

namespace Retro96.Engine.Dom;

public enum NodeType { Document, Element, Text, Comment, Doctype }

/// <summary>
/// Base node in the DOM tree.  Tree mutation helpers keep Parent in sync
/// and refuse cycles (a cyclic tree would hang every Descendants() walk).
/// </summary>
public abstract class DomNode
{
    public NodeType      NodeType { get; protected init; }
    public DomNode?      Parent   { get; set; }
    public List<DomNode> Children { get; } = [];

    public DomNode? FirstChild => Children.Count > 0 ? Children[0]  : null;
    public DomNode? LastChild  => Children.Count > 0 ? Children[^1] : null;

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
        if (child == null) return;
        if (WouldCreateCycle(child)) return;
        if (child.Parent != null)
            child.Parent.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    public void InsertBefore(DomNode newNode, DomNode referenceNode)
    {
        if (newNode == null) return;
        if (referenceNode == null)
        {
            AppendChild(newNode);
            return;
        }
        if (WouldCreateCycle(newNode)) return;
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

    /// <summary>
    /// True when appending <paramref name="node"/> to this one would create
    /// a cycle: node is this, or an ancestor of it.  JS DOM code can attempt
    /// exactly that (node.appendChild(node.parentNode)); a cyclic tree would
    /// make Descendants()/layout loop forever, so the mutators refuse it.
    /// </summary>
    private bool WouldCreateCycle(DomNode node)
    {
        if (node == this) return true;
        for (var n = this; n != null; n = n.Parent)
            if (n == node) return true;
        return false;
    }

    public void RemoveChild(DomNode child)
    {
        if (child != null && Children.Remove(child))
            child.Parent = null;
    }

    /// <summary>Walk the entire subtree (depth-first pre-order), including this node.</summary>
    public IEnumerable<DomNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    public IEnumerable<DomElement> ElementDescendants() => Descendants().OfType<DomElement>();

    /// <summary>Direct element children only.</summary>
    public IEnumerable<DomElement> ElementChildren() => Children.OfType<DomElement>();

    /// <summary>Find the owning document by walking up the tree.</summary>
    public DomDocument? OwnerDocument()
    {
        var node = this as DomNode;
        while (node != null)
        {
            if (node is DomDocument doc) return doc;
            node = node.Parent;
        }
        return null;
    }

    // CSS selector queries (first match / all matches over the subtree).
    public DomElement? QuerySelector(string selector)
    {
        var selectors = CssSelector.ParseSelector(selector);
        return ElementDescendants().FirstOrDefault(e => selectors.Any(s => s.Matches(e)));
    }

    public IEnumerable<DomElement> QuerySelectorAll(string selector)
    {
        var selectors = CssSelector.ParseSelector(selector);
        return ElementDescendants().Where(e => selectors.Any(s => s.Matches(e)));
    }
}

/// <summary>
/// The document node.  Carries document-wide state that the 1996 engine must
/// track: title, base URL/target, charset, body colour attributes, base font
/// size (BASEFONT), visited-link set and the cookie store.
/// </summary>
public class DomDocument : DomNode
{
    public string       Title        { get; set; } = "";
    public ParsedUrl?   BaseUrl      { get; set; }
    public string       BaseTarget   { get; set; } = "";
    public string       Charset      { get; set; } = "iso-8859-1";
    public string       QuirksMode   { get; set; } = "html32";

    /// <summary>True when the shell executes inline scripts for this
    /// document — gates &lt;noscript&gt; visibility (hidden with JS on,
    /// visible with JS off) in the style resolver.</summary>
    public bool ScriptingEnabled { get; set; }

    /// <summary>
    /// Set by HtmlParser.Parse just before it returns.  While false (the
    /// parse is still streaming), document.write output is handed back to
    /// the parser callback for stream splicing.  Once true, document.write
    /// from event handlers or timers REPLACES the live document — the
    /// period browsers' implicit document.open() semantics.
    /// </summary>
    public bool ParseComplete { get; set; }

    // <body text= link= vlink= alink= bgcolor= background=...>
    public string BodyTextColor { get; set; } = "#000000";
    public string BodyLinkColor { get; set; } = "#0000ee";
    public string BodyVLinkColor { get; set; } = "#551a8b";
    public string BodyALinkColor { get; set; } = "#ff0000";
    public string? BodyBackground { get; set; }

    // <basefont size=N> — document-wide base for relative <font size=+n>.
    public int BaseFontSize { get; set; } = 3;

    // Session-visited URLs (absolute), copied in by the browser shell.
    public HashSet<string> VisitedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CookieStore Cookies { get; }

    // META refresh: "N;url=..." pending navigation, consumed by the shell.
    public string? MetaRefresh { get; set; }

    // Live (lazily evaluated) DOM-0 collections.
    public IEnumerable<DomElement> Forms =>
        ElementDescendants().Where(e => e.TagName == "form");
    public IEnumerable<DomElement> Images =>
        ElementDescendants().Where(e => e.TagName == "img");
    public IEnumerable<DomElement> Links =>
        ElementDescendants().Where(e =>
            (e.TagName == "a" && e.HasAttr("href")) ||
            (e.TagName == "area" && e.HasAttr("href")));
    public IEnumerable<DomElement> Anchors =>
        ElementDescendants().Where(e => e.TagName == "a" && e.HasAttr("name"));

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

    // Populated by the style resolver.
    public ComputedStyle? Style { get; set; }
    // Populated by the layout engine.
    public LayoutBox?    Box   { get; set; }
    // Inline event handlers ("onclick" -> source), set by the HTML parser.
    public Dictionary<string, string> EventHandlers { get; } = [];

    /// <summary>
    /// Era "form owner" association: when a page opens &lt;form&gt; between
    /// &lt;table&gt; and &lt;tr&gt; (the 1996 search-box idiom), the parser
    /// foster-parents the form out of the table, so controls inside the
    /// cells are NOT tree-descendants of the form.  Period browsers keep
    /// the "form element pointer" so those controls still belong to the
    /// form; this field mirrors that, and FormSubmitter / DomBindings
    /// honour it.  Null for controls that really sit inside a form (or
    /// in no form at all).
    /// </summary>
    public DomElement? FormOwner { get; set; }

    public DomElement(string tagName)
    {
        TagName = tagName.ToLowerInvariant();
        NodeType = NodeType.Element;
    }

    public string? GetAttr(string name) =>
        Attrs.TryGetValue(name, out var value) ? value : null;

    public bool HasAttr(string name) => Attrs.ContainsKey(name);

    public string GetAttrOrDefault(string name, string def)
    {
        var value = GetAttr(name);
        return value ?? def;
    }

    public int GetAttrInt(string name, int def)
    {
        var value = GetAttr(name);
        if (value != null && int.TryParse(value.Trim(), out int result))
            return result;
        return def;
    }

    /// <summary>Set or remove a boolean-ish attribute.</summary>
    public void SetAttr(string name, string? value)
    {
        if (value == null) Attrs.Remove(name);
        else Attrs[name] = value;
    }

    /// <summary>
    /// Concatenated text of all Text descendants.  style/script/noscript
    /// subtrees are skipped ENTIRELY — the old walker only skipped the
    /// element node itself and still picked up its Text children, so
    /// script and stylesheet SOURCE leaked into every InnerText read
    /// (button labels, option text, JS-visible innerText).
    /// </summary>
    public string InnerText
    {
        get
        {
            var sb = new StringBuilder();
            AppendInnerTextFrom(this, sb);
            return sb.ToString();
        }
    }

    private static void AppendInnerTextFrom(DomNode node, StringBuilder sb)
    {
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case DomText textNode:
                    sb.Append(textNode.Data);
                    break;
                case DomElement elem:
                    if (elem.TagName is not ("style" or "script" or "noscript"))
                        AppendInnerTextFrom(elem, sb);
                    break;
            }
        }
    }
}

public class DomText : DomNode
{
    public string Data { get; set; } = "";

    public DomText()
    {
        NodeType = NodeType.Text;
    }

    public DomText(string data) : this()
    {
        Data = data;
    }
}

public class DomComment : DomNode
{
    public string Text { get; }

    public DomComment(string text)
    {
        Text = text;
        NodeType = NodeType.Comment;
    }
}

public class DomDoctype : DomNode
{
    public string RawText { get; }

    public DomDoctype(string rawText)
    {
        RawText = rawText;
        NodeType = NodeType.Doctype;
    }
}