using System;
using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Html;

// ── Token types ─────────────────────────────────────────────────────────────

public abstract record HtmlToken;

public record StartTag(string Name, IReadOnlyDictionary<string, string> Attrs,
                bool SelfClosing = false) : HtmlToken;

public record EndTag(string Name) : HtmlToken;

public record TextToken(string Data) : HtmlToken;

public record CommentToken(string Text) : HtmlToken;

public record DoctypeToken(string RawText) : HtmlToken;

/// <summary>
/// HTML tokenizer for the 1996/1999 compatibility targets.
///
/// Recovers from everything a real 1996/1999 page throws at it:
///   - tag/attribute names in any case
///   - unquoted / missing-value attributes (minimized boolean attributes
///     expand to name="name", per the HTML 4.01 SGML declaration)
///   - comments spanning lines and containing '&gt;'
///   - stray '&lt;' that starts no tag
///   - raw-text elements (SCRIPT, STYLE, LISTING, XMP, PLAINTEXT, plus the
///     IE5/NS4-era COMMENT / NOEMBED / NOLAYER / XML data-island tags)
///   - NOSCRIPT: raw text ONLY while scripting is enabled (fallback
///     content parses as ordinary markup when scripting is off)
///   - RCDATA elements (TEXTAREA, TITLE)
///   - unterminated everything at EOF — including a synthetic end tag for
///     an unterminated raw-text element, so a truncated page's last
///     &lt;script&gt; still executes (scripts run on the end tag)
/// </summary>
public static class HtmlTokenizer
{
    public static IEnumerable<HtmlToken> Tokenize(string html) =>
        Tokenize(html, scriptingEnabled: false);

    /// <param name="scriptingEnabled">True while the host executes page
    /// scripts — switches &lt;noscript&gt; into raw-text mode (the era
    /// behaviour: with scripting on, the fallback content is never parsed
    /// as markup; with scripting off it renders like any other element).
    /// </param>
    public static IEnumerable<HtmlToken> Tokenize(string html, bool scriptingEnabled)
    {
        if (string.IsNullOrEmpty(html))
            yield break;

        int pos = 0;
        var sb = new StringBuilder();
        string? rawTextTag = null;   // non-null while inside a raw-text/RCDATA element
        bool   isRcdata   = false;

        while (pos < html.Length)
        {
            if (rawTextTag != null)
            {
                // PLAINTEXT is special: everything after its start tag is text,
                // including strings that look like end tags.
                if (rawTextTag == "plaintext")
                {
                    sb.Append(html.AsSpan(pos));
                    pos = html.Length;
                    break;
                }

                // HTML 4 STYLE content ends at the first "</" sequence, even
                // when it is not followed by "style".
                if (rawTextTag == "style" && html[pos] == '<' &&
                    pos + 1 < html.Length && html[pos + 1] == '/')
                {
                    string rawText = sb.ToString();
                    if (rawText.Length > 0)
                        yield return new TextToken(rawText);
                    sb.Clear();
                    yield return new EndTag(rawTextTag);
                    rawTextTag = null;
                    isRcdata = false;
                    continue;
                }

                // Other raw-text elements accumulate until </tagname
                // (case-insensitive), allowing whitespace before '>'.
                if (html[pos] == '<' && pos + 1 < html.Length && html[pos + 1] == '/' &&
                    TryMatchRawEndTag(html, pos, rawTextTag, out int afterEnd))
                {
                    string rawText = sb.ToString();
                    if (isRcdata)
                        rawText = HtmlEntities.Decode(rawText);
                    if (rawText.Length > 0)
                        yield return new TextToken(rawText);
                    sb.Clear();
                    pos = afterEnd + 1;               // skip '>'
                    yield return new EndTag(rawTextTag);
                    rawTextTag = null;
                    isRcdata = false;
                    continue;
                }

                sb.Append(html[pos]);
                pos++;
                continue;
            }

            char c = html[pos];

            if (c == '<')
            {
                if (pos + 1 >= html.Length)
                {
                    // Bare '<' at EOF — literal
                    sb.Append('<');
                    pos++;
                    continue;
                }

                char next = html[pos + 1];

                if (next == '!')
                {
                    if (pos + 3 < html.Length && html[pos + 2] == '-' && html[pos + 3] == '-')
                    {
                        FlushText(sb, out var textTok);
                        if (textTok != null) yield return textTok;
                        var commentResult = ParseComment(html, ref pos);
                        if (commentResult != null) yield return commentResult;
                    }
                    else if (pos + 8 < html.Length &&
                             html.Substring(pos + 2, 7)
                                 .Equals("DOCTYPE", StringComparison.OrdinalIgnoreCase))
                    {
                        FlushText(sb, out var textTok2);
                        if (textTok2 != null) yield return textTok2;
                        var doctypeResult = ParseDoctype(html, ref pos);
                        if (doctypeResult != null) yield return doctypeResult;
                    }
                    else
                    {
                        FlushText(sb, out var textTok3);
                        if (textTok3 != null) yield return textTok3;
                        var bogus = ParseBogusComment(html, ref pos);
                        if (bogus != null) yield return bogus;
                    }
                }
                else if (next == '/')
                {
                    var endTag = ParseEndTag(html, ref pos);
                    if (endTag != null)
                    {
                        FlushText(sb, out var textTok4);
                        if (textTok4 != null) yield return textTok4;
                        yield return endTag;
                    }
                    else
                    {
                        // "</" with nothing usable: literal
                        sb.Append('<');
                        pos++;
                    }
                }
                else if (char.IsLetter(next))
                {
                    string? tagName = PeekTagName(html, pos);

                    var startTag = ParseStartTag(html, ref pos);
                    if (startTag != null)
                    {
                        FlushText(sb, out var textTok5);
                        if (textTok5 != null) yield return textTok5;
                        yield return startTag;

                        // Enter raw-text / RCDATA mode for elements whose content
                        // is not parsed as markup.  A self-closing tag (e.g. <br/>)
                        // never enters raw text mode.
                        if (!startTag.SelfClosing && tagName != null)
                        {
                            if (IsRawTextElement(tagName, scriptingEnabled))
                            {
                                rawTextTag = tagName;
                                isRcdata = false;
                            }
                            else if (IsRcdataElement(tagName))
                            {
                                rawTextTag = tagName;
                                isRcdata = true;
                            }
                        }
                    }
                    else
                    {
                        sb.Append('<');
                        pos++;
                    }
                }
                else
                {
                    // Bare '<' not followed by letter, / or ! — literal text
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

        // EOF: emit whatever is pending, and ALWAYS close a raw-text
        // element. The old code only emitted the synthetic end tag when
        // there was NO text — an unterminated <script>alert(1)… at EOF
        // lost its end tag, and since scripts execute on </script>, the
        // truncated page's last script silently never ran (an unclosed
        // <title>/<textarea> never closed either).
        if (sb.Length > 0)
        {
            string rawText = sb.ToString();
            if (rawTextTag != null && isRcdata)
                rawText = HtmlEntities.Decode(rawText);
            yield return new TextToken(rawText);
        }
        if (rawTextTag != null)
            yield return new EndTag(rawTextTag);
    }

    // Flush pending character data as a TextToken (entity decoding is
    // unconditional here — RCDATA content reaches this only through the
    // raw-text path above, which decodes per-mode).
    private static void FlushText(StringBuilder sb, out TextToken? tok)
    {
        tok = null;
        if (sb.Length > 0)
        {
            tok = new TextToken(HtmlEntities.Decode(sb.ToString()));
            sb.Clear();
        }
    }

    private static bool TryMatchRawEndTag(string html, int pos, string tagName, out int gtPos)
    {
        // pos points at '<'; caller ensured "</"
        int i = pos + 2;
        if (i >= html.Length || !char.IsLetter(html[i]))
        {
            gtPos = -1;
            return false;
        }

        var sb = new StringBuilder();
        while (i < html.Length && char.IsLetterOrDigit(html[i]))
        {
            sb.Append(char.ToLowerInvariant(html[i]));
            i++;
        }
        if (sb.ToString() != tagName)
        {
            gtPos = -1;
            return false;
        }

        // Closing raw-text tags may contain whitespace before '>', but not
        // arbitrary attributes/junk (</script foo> must remain text).
        while (i < html.Length && char.IsWhiteSpace(html[i])) i++;

        if (i < html.Length && html[i] == '>')
        {
            gtPos = i;
            return true;
        }

        // EOF inside end tag: still close
        gtPos = i - 1;
        return true;
    }

    private static string? PeekTagName(string html, int pos)
    {
        pos++; // skip '<'
        var sb = new StringBuilder();
        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) &&
               html[pos] != '>' && html[pos] != '/')
        {
            sb.Append(char.ToLowerInvariant(html[pos]));
            pos++;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static bool IsRawTextElement(string name, bool scriptingEnabled) =>
        name is "script" or "style" or "listing" or "xmp" or "plaintext"
            // IE5 <comment> element: its content is annotation text that
            // must never reach the renderer (or the DOM as markup).
            or "comment"
            // NOEMBED is the fallback for browsers WITHOUT <embed> — this
            // engine HAS embed, so the fallback is concealed (raw text).
            or "noembed"
            // NOLAYER is the fallback for browsers WITHOUT layer support —
            // this engine HAS layers, so the fallback is concealed (raw text).
            or "nolayer"
            // IE5 XML data islands (<xml id=...>...</xml>): the payload is
            // XML source kept as a raw text child for scripts
            // (document.all(id).innerHTML), never parsed as HTML markup.
            or "xml"
            // <noscript> content is markup ONLY for non-scripting browsers.
            || (scriptingEnabled && name == "noscript");

    private static bool IsRcdataElement(string name) =>
        name is "textarea" or "title";

    private static CommentToken? ParseComment(string html, ref int pos)
    {
        pos += 4; // skip <!--
        var sb = new StringBuilder();
        while (pos < html.Length)
        {
            if (html[pos] == '-' && pos + 2 < html.Length &&
                html[pos + 1] == '-' && html[pos + 2] == '>')
            {
                pos += 3;
                return new CommentToken(sb.ToString());
            }
            sb.Append(html[pos]);
            pos++;
        }
        // EOF inside comment: emit what we have
        return new CommentToken(sb.ToString());
    }

    private static DoctypeToken? ParseDoctype(string html, ref int pos)
    {
        int start = pos;
        while (pos < html.Length && html[pos] != '>')
            pos++;
        if (pos < html.Length)
        {
            string rawText = html.Substring(start, pos - start + 1);
            pos++; // skip '>'
            return new DoctypeToken(rawText);
        }
        return new DoctypeToken(html.Substring(start));
    }

    private static CommentToken? ParseBogusComment(string html, ref int pos)
    {
        int start = pos;
        pos++; // skip '<'
        while (pos < html.Length && html[pos] != '>')
            pos++;
        if (pos < html.Length)
        {
            string text = html.Substring(start, pos - start + 1);
            pos++;
            return new CommentToken(text);
        }
        return new CommentToken(html.Substring(start));
    }

    private static EndTag? ParseEndTag(string html, ref int pos)
    {
        pos += 2; // skip </
        var nameSb = new StringBuilder();
        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) && html[pos] != '>')
        {
            nameSb.Append(char.ToLowerInvariant(html[pos]));
            pos++;
        }

        string name = nameSb.ToString();
        if (name.Length == 0)
            return null; // "</>" or "</ ..." — treat as literal

        // Skip everything up to and including '>' (end tags with attributes)
        while (pos < html.Length && html[pos] != '>')
            pos++;
        if (pos < html.Length)
            pos++;

        return new EndTag(name);
    }

    private static StartTag? ParseStartTag(string html, ref int pos)
    {
        int tagStart = pos;
        pos++; // skip '<'

        var nameSb = new StringBuilder();
        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) &&
               html[pos] != '>' && html[pos] != '/')
        {
            nameSb.Append(char.ToLowerInvariant(html[pos]));
            pos++;
        }

        string name = nameSb.ToString();
        if (name.Length == 0)
        {
            pos = tagStart;
            return null;
        }

        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (pos < html.Length)
        {
            while (pos < html.Length && char.IsWhiteSpace(html[pos]))
                pos++;
            if (pos >= html.Length)
                break;

            if (html[pos] == '>')
            {
                pos++;
                break;
            }

            if (html[pos] == '/')
            {
                pos++;
                if (pos < html.Length && html[pos] == '>')
                {
                    pos++;
                    return new StartTag(name, attrs, true);
                }
                continue;
            }

            // Attribute name: up to '=', whitespace, '>' or '/'.
            var attrNameSb = new StringBuilder();
            while (pos < html.Length && html[pos] != '=' &&
                   !char.IsWhiteSpace(html[pos]) && html[pos] != '>')
            {
                char a = html[pos];
                // A '/' ends the name only when followed by '>' or whitespace,
                // so unquoted values like href=http://x/ keep their slashes.
                if (a == '/' && pos + 1 < html.Length &&
                    (html[pos + 1] == '>' || char.IsWhiteSpace(html[pos + 1])))
                    break;
                if (a == '/' && pos + 1 >= html.Length)
                    break;
                attrNameSb.Append(char.ToLowerInvariant(a));
                pos++;
            }

            string attrName = attrNameSb.ToString();
            if (attrName.Length == 0)
            {
                pos++; // skip stray char
                continue;
            }

            int savePos = pos;
            while (pos < html.Length && char.IsWhiteSpace(html[pos]))
                pos++;

            if (pos < html.Length && html[pos] == '=')
            {
                pos++; // skip '='
                while (pos < html.Length && char.IsWhiteSpace(html[pos]))
                    pos++;

                if (pos < html.Length)
                {
                    char quote = html[pos];
                    if (quote == '"' || quote == '\'')
                    {
                        pos++;
                        var valueSb = new StringBuilder();
                        while (pos < html.Length && html[pos] != quote)
                        {
                            valueSb.Append(html[pos]);
                            pos++;
                        }
                        if (pos < html.Length)
                            pos++; // closing quote
                        attrs[attrName] = HtmlEntities.Decode(valueSb.ToString());
                    }
                    else
                    {
                        // Unquoted value: runs to whitespace or '>'.  A '/' is
                        // PART of the value — HTML tokenization rules, and
                        // what 1996 browsers did. The old code stripped a
                        // trailing "…/" before '>', silently truncating every
                        // unquoted URL that ended in a slash
                        // (href=http://site/ → href=http://site).
                        // (<br/> still self-closes via the '/' branch above.)
                        var valueSb = new StringBuilder();
                        while (pos < html.Length && !char.IsWhiteSpace(html[pos]) &&
                               html[pos] != '>')
                        {
                            valueSb.Append(html[pos]);
                            pos++;
                        }
                        attrs[attrName] = HtmlEntities.Decode(valueSb.ToString());
                    }
                }
                else
                {
                    attrs[attrName] = ""; // '=' at EOF
                }
            }
            else
            {
                // No '=' — boolean/minimized attribute.  Per the HTML 4.01
                // SGML declaration the minimized form expands to the
                // attribute NAME as its value: <option selected> ≡
                // selected="selected".  (The 3.2-era parser stored "";
                // every engine consumer tests presence via HasAttr, so the
                // richer value is a pure correctness win — and matches what
                // IE5/NS4.7 handed to getAttribute.)
                // Restore position only when we actually advanced past
                // whitespace without finding '='.
                if (pos != savePos && pos >= html.Length)
                    pos = savePos;
                attrs.TryAdd(attrName, attrName);
            }
        }

        return new StartTag(name, attrs, false);
    }
}