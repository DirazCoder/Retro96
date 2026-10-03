using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96.Engine.Html;

public static class HtmlParser
{
    public static DomDocument Parse(string html, ParsedUrl baseUrl, CookieStore cookies)
    {
        var doc = new DomDocument(cookies)
        {
            BaseUrl = baseUrl,
            Charset = "iso-8859-1"
        };

        // Handle content if it starts with a DOCTYPE
        // (the tokenizer will emit a DoctypeToken if present)

        var openElements = new Stack<DomElement>();
        DomElement? current = null;
        bool hasBody = false;

        foreach (var token in HtmlTokenizer.Tokenize(html))
        {
            switch (token)
            {
                case DoctypeToken doctype:
                    doc.QuirksMode = DetermineQuirksMode(doctype.RawText);
                    doc.AppendChild(new DomDoctype(doctype.RawText));
                    break;

                case StartTag startTag:
                    HandleStartTag(startTag, doc, openElements, ref current, ref hasBody);
                    break;

                case EndTag endTag:
                    HandleEndTag(endTag, openElements, ref current);
                    break;

                case TextToken text:
                    HandleTextToken(text, current);
                    break;

                case CommentToken comment:
                    if (current != null)
                        current.AppendChild(new DomComment(comment.Text));
                    else
                        doc.AppendChild(new DomComment(comment.Text));
                    break;
            }
        }

        // Auto-close any remaining open elements
        while (openElements.Count > 0)
        {
            var elem = openElements.Pop();
            // No need to do anything special - they're already in the tree
        }

        return doc;
    }

    private static string DetermineQuirksMode(string rawText)
    {
        // Quirks mode detection based on DOCTYPE
        if (string.IsNullOrEmpty(rawText))
            return "quirks"; // No DOCTYPE = quirks mode

        string upper = rawText.ToUpperInvariant();
        
        // HTML 3.2
        if (upper.Contains("HTML 3.2") || upper.Contains("HTML 3.2 FINAL"))
            return "html32";
        
        // HTML 2.0
        if (upper.Contains("HTML 2.0") || upper.Contains("RFC 1866"))
            return "html20";
        
        // HTML5 or modern DOCTYPE
        if (upper.Contains("HTML") && upper.Contains("DTD"))
        {
            // Check for standards mode DOCTYPEs
            if (upper.Contains("HTML 4.01") || upper.Contains("HTML 4.0") ||
                upper.Contains("XHTML") || upper.Contains("HTML5"))
            {
                return "strict";
            }
        }
        
        // HTML5 short DOCTYPE: <!DOCTYPE html>
        if (upper.Contains("<!DOCTYPE HTML>"))
            return "strict";

        // Default to quirks mode for unknown DOCTYPEs (per HTML5 spec)
        return "quirks";
    }

        private static void HandleStartTag(StartTag tag, DomDocument doc,
        Stack<DomElement> openElements, ref DomElement? current, ref bool hasBody)
    {
        var element = new DomElement(tag.Name);
        foreach (var attr in tag.Attrs)
        {
            element.Attrs[attr.Key] = attr.Value;
            // Copy inline event handlers (onclick, onmouseover, etc.) to EventHandlers
            if (attr.Key.StartsWith("on", StringComparison.OrdinalIgnoreCase) && attr.Key.Length > 2)
            {
                element.EventHandlers[attr.Key.Substring(2).ToLowerInvariant()] = attr.Value;
            }
        }

        // Handle special elements
        switch (tag.Name)
        {
            case "html":
                // Root element
                if (doc.FirstChild == null || !(doc.FirstChild is DomElement && ((DomElement)doc.FirstChild).TagName == "html"))
                {
                    doc.AppendChild(element);
                    openElements.Push(element);
                    current = element;
                }
                break;

            case "head":
            case "body":
                if (tag.Name == "body") hasBody = true;
                if (current == null || current.TagName == "html")
                {
                    // Append to html or document
                    if (current != null)
                        current.AppendChild(element);
                    else
                        doc.AppendChild(element);
                    openElements.Push(element);
                    current = element;
                }
                break;

            case "title":
                // Title element - will have text content
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                openElements.Push(element);
                current = element;
                break;

            case "frameset":
                if (hasBody)
                {
                    // Ignore frameset if body already open
                    return;
                }
                // Replace body with frameset
                if (current != null && current.TagName == "body")
                {
                    current.Parent?.RemoveChild(current);
                    openElements.Clear();
                }
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                openElements.Push(element);
                current = element;
                break;

            case "script":
                // Script content will be handled by raw text mode in tokenizer
                // Just add the element to the tree
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                // Don't push to open stack - script is handled by raw text mode
                // But we need to track it for the closing tag
                openElements.Push(element);
                current = element;
                break;

            case "style":
                // Similar to script
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                openElements.Push(element);
                current = element;
                break;

            case "base":
                if (tag.Attrs.TryGetValue("href", out var href) && !string.IsNullOrEmpty(href))
                {
                    try { doc.BaseUrl = doc.BaseUrl?.Resolve(href) ?? ParsedUrl.Parse(href); }
                    catch { /* ignore invalid URLs */ }
                }
                // Base is void, don't push to stack
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                break;

            case "meta":
                HandleMetaTag(element, doc);
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                // Meta is void
                break;

            case "link":
                HandleLinkTag(element, doc);
                if (current != null)
                    current.AppendChild(element);
                else
                    doc.AppendChild(element);
                // Link is void
                break;

            case "isindex":
                // Synthesise form
                var form = new DomElement("form");
                var p = new DomElement("p");
                if (element.GetAttr("prompt") is string prompt)
                    p.AppendChild(new DomText { Data = prompt });
                var input = new DomElement("input");
                input.Attrs["type"] = "text";
                input.Attrs["name"] = "terms";
                p.AppendChild(input);
                form.AppendChild(p);
                if (current != null)
                    current.AppendChild(form);
                else
                    doc.AppendChild(form);
                openElements.Push(form);
                current = form;
                break;

            default:
                // Regular element
                if (IsVoidElement(tag.Name))
                {
                    if (current != null)
                        current.AppendChild(element);
                    else
                        doc.AppendChild(element);
                    // Void elements don't get pushed to open stack
                }
                else
                {
                    if (current != null)
                        current.AppendChild(element);
                    else
                        doc.AppendChild(element);
                    openElements.Push(element);
                    current = element;
                }
                break;
        }

        // Handle auto-close rules for block-level tags inside <p>.
        // NOTE: the new element is already on the stack so Peek() is the new
        // element itself. The previously open element is at ElementAt(1).
        if (tag.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "div" or "p" or
            "ul" or "ol" or "dl" or "table" or "blockquote" or "pre" or "address" or "hr" or "form")
        {
            // Auto-close <p> if the element directly below the new one is a <p>
            if (openElements.Count >= 2 && openElements.ElementAt(1).TagName == "p")
            {
                // Remove the <p> from the stack (it is below the current new element)
                var newElem = openElements.Pop();   // save new element
                openElements.Pop();                  // discard the <p>
                openElements.Push(newElem);          // restore new element on top
                // current stays as the new element (already set)
            }
        }

        // Handle <li> auto-close
        if (tag.Name == "li" && openElements.Count > 0 && openElements.Peek().TagName == "li")
        {
            openElements.Pop();
        }

        // Handle <dt> and <dd> auto-close
        if ((tag.Name == "dt" || tag.Name == "dd") && openElements.Count > 0 &&
            (openElements.Peek().TagName == "dt" || openElements.Peek().TagName == "dd"))
        {
            openElements.Pop();
        }

        // Handle <tr> auto-close
        if (tag.Name == "tr" && openElements.Count > 0 && openElements.Peek().TagName == "tr")
        {
            openElements.Pop();
        }

        // Handle <td> and <th> auto-close
        if ((tag.Name == "td" || tag.Name == "th") && openElements.Count > 0 &&
            (openElements.Peek().TagName == "td" || openElements.Peek().TagName == "th"))
        {
            openElements.Pop();
        }
    }

    private static void HandleEndTag(EndTag tag, Stack<DomElement> openElements, ref DomElement? current)
    {
        // FIX: Stack<T>.ElementAt(0) is the TOP (most-recently-pushed) element.
        // We must search from top (i=0) toward bottom (i=Count-1).
        // Then we pop exactly (depthFromTop + 1) elements so the matched element
        // and anything accidentally left open above it are all closed.
        // The OLD code searched from bottom to top, found the match at index 0
        // (top), then did "while Count > 0" which popped the ENTIRE stack --
        // leaving current=null and causing all subsequent elements to be
        // appended to the document root instead of inside <body>.
        int depthFromTop = -1;
        for (int i = 0; i < openElements.Count; i++)
        {
            if (openElements.ElementAt(i).TagName == tag.Name)
            {
                depthFromTop = i;
                break;
            }
        }

        if (depthFromTop >= 0)
        {
            // Pop the matched element and everything above it in the stack
            for (int i = 0; i <= depthFromTop; i++)
                openElements.Pop();
            current = openElements.Count > 0 ? openElements.Peek() : null;
        }
        // If no match found, ignore the end tag (per HTML5 spec)
    }

    private static void HandleTextToken(TextToken text, DomElement? current)
    {
        if (current == null) return;

        string data = text.Data;
        if (string.IsNullOrEmpty(data)) return;

        // For <title> element, set document title
        if (current.TagName == "title")
        {
            // Find the document to set its title
            var node = current as DomNode;
            while (node != null && node.NodeType != NodeType.Document)
                node = node.Parent;
            if (node is DomDocument doc)
            {
                doc.Title = data;
            }
        }

        // For <pre> / white-space:pre elements, preserve exact text.
        bool isPre = current.TagName is "pre" or "textarea" or "script" or "style";
        if (isPre)
        {
            current.AppendChild(new DomText { Data = data });
            return;
        }

        // HTML normal whitespace rules:
        // 1. Collapse runs of whitespace to a single space.
        // 2. Do NOT trim leading/trailing spaces — they are meaningful between
        //    inline elements.  e.g. "<b>Hello</b> world" has a real space before
        //    "world" that must not be erased.
        //    We only skip a node if it is 100% whitespace AND it would add a
        //    redundant space (i.e. the previous sibling already ends with a space,
        //    or there are no siblings yet at block scope).
        var sb = new StringBuilder();
        bool lastWasWhitespace = false;
        foreach (char c in data)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace) { sb.Append(' '); lastWasWhitespace = true; }
            }
            else
            {
                sb.Append(c);
                lastWasWhitespace = false;
            }
        }
        data = sb.ToString();

        // Drop nodes that are entirely empty after collapsing.
        if (data.Length == 0) return;

        // Drop a lone single space when there are no siblings yet — avoids
        // leading whitespace inside block-level elements (e.g. <p> / <div>).
        bool isAllSpace = data == " ";
        if (isAllSpace && current.Children.Count == 0 &&
            current.TagName is "p" or "div" or "h1" or "h2" or "h3" or "h4"
                             or "h5" or "h6" or "li" or "td" or "th" or "blockquote")
            return;

        current.AppendChild(new DomText { Data = data });
    }

    private static void HandleMetaTag(DomElement element, DomDocument doc)
    {
        if (!element.HasAttr("http-equiv") || !element.HasAttr("content"))
            return;

        string httpEquiv = element.GetAttr("http-equiv")!.ToLowerInvariant();
        string content = element.GetAttr("content")!;

        if (httpEquiv == "refresh")
        {
            // Parse content for URL: "N;url=..."
            // Store for later processing
            element.Attrs["_refresh"] = content;
        }
        else if (httpEquiv == "content-type")
        {
            // Parse charset from content: "text/html; charset=..."
            if (content.ToLowerInvariant().Contains("charset="))
            {
                string charset = content.Substring(content.IndexOf("charset=") + 8).Trim();
                doc.Charset = charset.Trim('"', '\'', ' ');
            }
        }
        else if (httpEquiv == "set-cookie")
        {
            // Feed to CookieStore
            if (doc.Cookies != null && doc.BaseUrl != null)
            {
                // Parse cookie string and add to document's cookie store
                // Using base URL as the request URL for cookie domain/path validation
                doc.Cookies.Set(content, doc.BaseUrl);
            }
        }
    }

    private static void HandleLinkTag(DomElement element, DomDocument doc)
    {
        if (!element.HasAttr("rel") || !element.HasAttr("href"))
            return;

        string rel = element.GetAttr("rel")!.ToLowerInvariant();
        if (rel.Contains("stylesheet"))
        {
            // Enqueue CSS fetch
            string href = element.GetAttr("href")!;
            element.Attrs["_stylesheet"] = href;
        }
    }

    private static bool IsVoidElement(string name)
    {
        return name switch
        {
            "img" or "br" or "hr" or "input" or "param" or "wbr" or "area" or "col" or "basefont" or "frame" or "isindex" or "link" or "meta" or "base" => true,
            _ => false
        };
    }
}