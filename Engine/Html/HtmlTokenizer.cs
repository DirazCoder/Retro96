using System;
using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Html;

// Token types
public abstract record HtmlToken;

public record StartTag(string Name, IReadOnlyDictionary<string, string> Attrs,
                bool SelfClosing = false) : HtmlToken;

public record EndTag(string Name) : HtmlToken;

public record TextToken(string Data) : HtmlToken;

public record CommentToken(string Text) : HtmlToken;

public record DoctypeToken(string RawText) : HtmlToken;

public static class HtmlTokenizer
{
    public static IEnumerable<HtmlToken> Tokenize(string html)
    {
        if (string.IsNullOrEmpty(html))
            yield break;

        int pos = 0;
        var sb = new StringBuilder();
        string? rawTextTag = null; // If in raw text mode, this is the tag name
        bool isRcdata = false; // If true, decode entities in raw text

        while (pos < html.Length)
        {
            if (rawTextTag != null)
            {
                // In raw text or RCDATA mode - look for </tagname>
                if (pos + 2 < html.Length && html[pos] == '<' && html[pos + 1] == '/')
                {
                    // Check if this is the matching end tag
                    int tagStart = pos + 2;
                    var tagSb = new StringBuilder();
                    while (tagStart < html.Length && html[tagStart] != '>' && !char.IsWhiteSpace(html[tagStart]))
                    {
                        tagSb.Append(char.ToLowerInvariant(html[tagStart]));
                        tagStart++;
                    }

                    string endTagName = tagSb.ToString();
                    if (endTagName == rawTextTag && tagStart < html.Length && html[tagStart] == '>')
                    {
                        // Found matching end tag
                        pos = tagStart + 1;

                        // Emit the accumulated raw text
                        string rawText = sb.ToString();
                        if (isRcdata)
                            rawText = HtmlEntities.Decode(rawText);
                        yield return new TextToken(rawText);

                        sb.Clear();
                        rawTextTag = null;
                        isRcdata = false;
                        continue;
                    }
                }

                // Accumulate character
                sb.Append(html[pos]);
                pos++;
            }
            else
            {
                char c = html[pos];

                if (c == '<')
                {
                    // Emit any pending text
                    if (sb.Length > 0)
                    {
                        yield return new TextToken(HtmlEntities.Decode(sb.ToString()));
                        sb.Clear();
                    }

                    // Check what follows the <
                    if (pos + 1 < html.Length)
                    {
                        char next = html[pos + 1];

                        if (next == '!')
                        {
                            // Comment or doctype or bogus comment
                            if (pos + 3 < html.Length && html[pos + 2] == '-' && html[pos + 3] == '-')
                            {
                                // Comment <!-- ... -->
                                var commentResult = ParseComment(html, ref pos);
                                if (commentResult != null)
                                    yield return commentResult;
                            }
                            else if (pos + 7 < html.Length &&
                                     html.Substring(pos + 2, 7).Equals("DOCTYPE", StringComparison.OrdinalIgnoreCase))
                            {
                                // DOCTYPE
                                var doctypeResult = ParseDoctype(html, ref pos);
                                if (doctypeResult != null)
                                    yield return doctypeResult;
                            }
                            else if (pos + 1 < html.Length && html[pos + 1] == '?')
                            {
                                // Processing instruction <?...> - treat as bogus comment
                                var bogusResult = ParseBogusComment(html, ref pos);
                                if (bogusResult != null)
                                    yield return bogusResult;
                            }
                            else
                            {
                                // Bogus comment <!...>
                                var bogusResult = ParseBogusComment(html, ref pos);
                                if (bogusResult != null)
                                    yield return bogusResult;
                            }
                        }
                        else if (next == '/')
                        {
                            // End tag
                            var endTag = ParseEndTag(html, ref pos);
                            if (endTag != null)
                                yield return endTag;
                        }
                        else if (char.IsLetter(next))
                        {
                            // Start tag - check if it's a raw text or RCDATA element
                            string? tagName = PeekTagName(html, pos);
                            if (tagName != null && IsRawTextElement(tagName))
                            {
                                var startTag = ParseStartTag(html, ref pos);
                                if (startTag != null)
                                {
                                    yield return startTag;

                                    // Enter raw text mode
                                    rawTextTag = tagName;
                                    isRcdata = false;
                                }
                            }
                            else if (tagName != null && IsRcdataElement(tagName))
                            {
                                var startTag = ParseStartTag(html, ref pos);
                                if (startTag != null)
                                {
                                    yield return startTag;

                                    // Enter RCDATA mode
                                    rawTextTag = tagName;
                                    isRcdata = true;
                                }
                            }
                            else
                            {
                                // Normal start tag
                                var startTag = ParseStartTag(html, ref pos);
                                if (startTag != null)
                                    yield return startTag;
                            }
                        }
                        else
                        {
                            // Bare < not followed by letter, /, or ! - treat as literal
                            sb.Append('<');
                            pos++;
                        }
                    }
                    else
                    {
                        // Bare < at EOF
                        sb.Append('<');
                        pos++;
                    }
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
        }

        // Emit any remaining text
        if (sb.Length > 0)
        {
            if (rawTextTag != null)
            {
                // EOF inside raw text - emit what we have
                string rawText = sb.ToString();
                if (isRcdata)
                    rawText = HtmlEntities.Decode(rawText);
                yield return new TextToken(rawText);
            }
            else
            {
                yield return new TextToken(HtmlEntities.Decode(sb.ToString()));
            }
        }
    }

    private static string? PeekTagName(string html, int pos)
    {
        // Skip <
        pos++;

        var sb = new StringBuilder();
        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) && html[pos] != '>' && html[pos] != '/')
        {
            sb.Append(char.ToLowerInvariant(html[pos]));
            pos++;
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static bool IsRawTextElement(string name)
    {
        return name == "script" || name == "style" || name == "listing";
    }

    private static bool IsRcdataElement(string name)
    {
        return name == "textarea" || name == "title";
    }

    private static CommentToken? ParseComment(string html, ref int pos)
    {
        // Skip <!--
        pos += 4;

        var sb = new StringBuilder();

        while (pos < html.Length)
        {
            if (html[pos] == '-' && pos + 1 < html.Length && html[pos + 1] == '-' &&
                pos + 2 < html.Length && html[pos + 2] == '>')
            {
                // End of comment -->
                pos += 3;
                return new CommentToken(sb.ToString());
            }
            sb.Append(html[pos]);
            pos++;
        }

        // EOF inside comment - emit what we have
        return new CommentToken(sb.ToString());
    }

    private static DoctypeToken? ParseDoctype(string html, ref int pos)
    {
        // Skip <!DOCTYPE
        int start = pos;
        while (pos < html.Length && html[pos] != '>')
        {
            pos++;
        }

        if (pos < html.Length)
        {
            string rawText = html.Substring(start, pos - start + 1);
            pos++; // Skip >
            return new DoctypeToken(rawText);
        }

        // EOF
        string remaining = html.Substring(start);
        return new DoctypeToken(remaining);
    }

    private static CommentToken? ParseBogusComment(string html, ref int pos)
    {
        // Everything from <! to >
        int start = pos;
        pos++; // Skip <

        while (pos < html.Length && html[pos] != '>')
        {
            pos++;
        }

        if (pos < html.Length)
        {
            string text = html.Substring(start, pos - start + 1);
            pos++; // Skip >
            return new CommentToken(text);
        }

        // EOF
        string remaining = html.Substring(start);
        return new CommentToken(remaining);
    }

    private static EndTag? ParseEndTag(string html, ref int pos)
    {
        // Skip </
        pos += 2;

        // Parse tag name
        var nameSb = new StringBuilder();
        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) && html[pos] != '>')
        {
            nameSb.Append(char.ToLowerInvariant(html[pos]));
            pos++;
        }

        string name = nameSb.ToString();
        if (string.IsNullOrEmpty(name))
            return null;

        // Skip whitespace
        while (pos < html.Length && char.IsWhiteSpace(html[pos]))
            pos++;

        // Skip >
        if (pos < html.Length && html[pos] == '>')
            pos++;

        return new EndTag(name);
    }

    private static StartTag? ParseStartTag(string html, ref int pos)
    {
        // Skip <
        pos++;

        // Parse tag name
        var nameSb = new StringBuilder();
        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) && html[pos] != '>' && html[pos] != '/')
        {
            nameSb.Append(char.ToLowerInvariant(html[pos]));
            pos++;
        }

        string name = nameSb.ToString();
        if (string.IsNullOrEmpty(name))
            return null;

        // Check for void elements
        bool isVoid = IsVoidElement(name);

        // Parse attributes
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (pos < html.Length)
        {
            // Skip whitespace
            while (pos < html.Length && char.IsWhiteSpace(html[pos]))
                pos++;

            if (pos >= html.Length)
                break;

            if (html[pos] == '>')
            {
                pos++; // Skip >
                break;
            }

            if (html[pos] == '/')
            {
                pos++; // Skip /
                if (pos < html.Length && html[pos] == '>')
                {
                    pos++; // Skip >
                    return new StartTag(name, attrs, true);
                }
                continue;
            }

            // Parse attribute name
            var attrNameSb = new StringBuilder();
            while (pos < html.Length && html[pos] != '=' && !char.IsWhiteSpace(html[pos]) && html[pos] != '>' && html[pos] != '/')
            {
                attrNameSb.Append(char.ToLowerInvariant(html[pos]));
                pos++;
            }

            string attrName = attrNameSb.ToString();
            if (string.IsNullOrEmpty(attrName))
                continue;

            // Skip whitespace
            while (pos < html.Length && char.IsWhiteSpace(html[pos]))
                pos++;

            string? attrValue = null;

            // Check for =
            if (pos < html.Length && html[pos] == '=')
            {
                pos++; // Skip =

                // Skip whitespace
                while (pos < html.Length && char.IsWhiteSpace(html[pos]))
                    pos++;

                if (pos < html.Length)
                {
                    char quote = html[pos];
                    if (quote == '"' || quote == '\'')
                    {
                        // Quoted value
                        pos++; // Skip opening quote
                        var valueSb = new StringBuilder();
                        while (pos < html.Length && html[pos] != quote)
                        {
                            valueSb.Append(html[pos]);
                            pos++;
                        }
                        if (pos < html.Length && html[pos] == quote)
                            pos++; // Skip closing quote
                        attrValue = HtmlEntities.Decode(valueSb.ToString());
                    }
                    else
                    {
                        // Unquoted value
                        var valueSb = new StringBuilder();
                        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) && html[pos] != '>' && html[pos] != '/')
                        {
                            valueSb.Append(html[pos]);
                            pos++;
                        }
                        attrValue = HtmlEntities.Decode(valueSb.ToString());
                    }
                }
            }

            if (attrValue != null)
                attrs[attrName] = attrValue;
            else
                attrs[attrName] = string.Empty; // Boolean attribute
        }

        return new StartTag(name, attrs, false);
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
