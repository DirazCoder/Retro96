using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96.Engine.Html;

/// <summary>
/// Optional hook executed when an inline &lt;script&gt; element finishes
/// parsing.  Receives the document being built and the raw script source;
/// returns any HTML the script wrote via document.write(), which the parser
/// splices into the token stream immediately after the script — the
/// period-accurate merged-stream behaviour.
/// Return an empty string when nothing was written.
/// </summary>
public delegate string InlineScriptExecutor(DomDocument document, string scriptSource);

/// <summary>
/// HTML 3.2-era tag-soup parser.  Builds a DOM from a token stream while
/// recovering from everything a real 1996 page contains:
///
///   - missing HTML/HEAD/BODY (structure inferred lazily)
///   - unclosed P, LI, TD, TR, DT, DD, OPTION (implied end tags BEFORE insert)
///   - mismatched/overlapping inline tags (stray end tags ignored)
///   - HEAD content after BODY started
///   - multiple BODY tags (attributes merged into one body)
///   - FRAMESET documents (body replaced, NOFRAMES kept for fallback)
///   - inline &lt;script&gt; execution with document.write() token splicing
///     (including scripts that write further script tags — chained content)
///   - raw &lt;/PRE&gt; first-newline quirk, &amp;nbsp; not collapsed
/// </summary>
public static class HtmlParser
{
    // Runaway guard for document.write chains: a script whose output
    // contains another script that also writes grows the token stream
    // without bound.  The cap counts INSERTED TOKENS (the old per-round
    // cap of 50 silently dropped the output of the 51st script on a
    // write-heavy page); 200k tokens is far beyond any real page and
    // still stops an infinite chain in well under a second.
    private const int MaxScriptSpliceTokens = 200_000;

    // Start tags that imply the end of an open <p> (HTML 3.2: P contains %inline).
    private static readonly HashSet<string> ClosesP =
        new(StringComparer.Ordinal)
        {
            "p", "h1", "h2", "h3", "h4", "h5", "h6",
            "ul", "ol", "dir", "menu", "dl", "dt", "dd",
            "pre", "listing", "xmp", "blockquote", "address",
            "center", "div", "table", "hr", "form", "isindex", "multicol"
        };

    // Never pop past these while hunting for an implied-end-tag target.
    private static readonly HashSet<string> StopBoundaries =
        new(StringComparer.Ordinal)
        {
            "table", "tbody", "thead", "tfoot", "tr", "td", "th", "caption",
            "body", "html", "frameset", "frame"
        };

    public static DomDocument Parse(string html, ParsedUrl baseUrl, CookieStore cookies,
                                    InlineScriptExecutor? onScript = null)
    {
        var doc = new DomDocument(cookies)
        {
            BaseUrl = baseUrl,
            Charset = "iso-8859-1",
            // A live script executor means scripting is on — <noscript>
            // content must not render (era NN behaviour).
            ScriptingEnabled = onScript != null
        };

        var builder = new TreeBuilder(doc, onScript);
        builder.Build(HtmlTokenizer.Tokenize(html ?? "").ToList());

        // Everything from here on is post-parse: document.write calls made
        // by event handlers and timers take the implicit-open/replace path
        // in DomBindings instead of the stream-splice path.
        doc.ParseComplete = true;
        return doc;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Tree builder
    // ─────────────────────────────────────────────────────────────────────

    private sealed class TreeBuilder
    {
        private readonly DomDocument _doc;
        private readonly InlineScriptExecutor? _onScript;

        private readonly Stack<DomElement> _open = new();
        private DomElement? _current;
        private DomElement? _html;
        private DomElement? _head;
        private DomElement? _body;
        private DomElement? _frameset;
        private bool _bodyClosed;
        private DomElement? _documentTitleElement;

        // Era "form element pointer" — the most recently opened <form>.
        // When a page opens <form> between <table> and <tr> (the 1996
        // search-box idiom), the form is foster-parented out of the table
        // and the cell controls are NOT tree-descendants of it; period
        // browsers associate them with the form through this pointer, and
        // we mirror that via DomElement.FormOwner.
        private DomElement? _formPointer;

        public TreeBuilder(DomDocument doc, InlineScriptExecutor? onScript)
        {
            _doc = doc;
            _onScript = onScript;
        }

        public void Build(List<HtmlToken> tokens)
        {
            int splicedTokens = 0;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                switch (token)
                {
                    case DoctypeToken doctype:
                        _doc.QuirksMode = DetermineQuirksMode(doctype.RawText);
                        _doc.AppendChild(new DomDoctype(doctype.RawText));
                        break;

                    case StartTag startTag:
                        HandleStartTag(startTag);
                        break;

                    case EndTag endTag:
                        var written = HandleEndTag(endTag);

                        // </script> may have produced document.write output —
                        // splice its tokens right after this position so the
                        // merged stream keeps parsing from the same spot.
                        if (written.Length > 0)
                        {
                            var spliced = HtmlTokenizer.Tokenize(written).ToList();
                            if (spliced.Count > 0 &&
                                splicedTokens + spliced.Count <= MaxScriptSpliceTokens)
                            {
                                splicedTokens += spliced.Count;
                                tokens.InsertRange(i + 1, spliced);
                            }
                        }
                        break;

                    case TextToken text:
                        HandleText(text.Data);
                        break;

                    case CommentToken comment:
                        if (_current != null)
                            _current.AppendChild(new DomComment(comment.Text));
                        else
                            _doc.AppendChild(new DomComment(comment.Text));
                        break;
                }
            }

            // EOF: nothing left to close by hand — the tokenizer emits a
            // synthetic end tag for every unterminated raw-text element
            // (script/style/textarea/title/…), so scripts left open at EOF
            // still execute.
        }

        // ── Element creation helpers ──────────────────────────────────────

        private DomElement Create(StartTag tag)
        {
            var element = new DomElement(tag.Name);
            foreach (var attr in tag.Attrs)
            {
                element.Attrs[attr.Key] = attr.Value;
                if (attr.Key.StartsWith("on", StringComparison.OrdinalIgnoreCase) &&
                    attr.Key.Length > 2)
                {
                    // Keep the "on" prefix: every FireEvent caller and the JS
                    // `el.onclick = fn` path key handlers as "onclick".
                    element.EventHandlers[attr.Key.ToLowerInvariant()] = attr.Value;
                }
            }
            return element;
        }

        private void Push(DomElement element)
        {
            _open.Push(element);
            _current = element;
        }

        /// <summary>Append a void (never-closed) element to the current insertion point.</summary>
        private void InsertVoid(DomElement element)
        {
            if (_current != null)
                _current.AppendChild(element);
            else
                _doc.AppendChild(element);
        }

        private void EnsureHtml()
        {
            if (_html != null) return;
            _html = new DomElement("html");
            _doc.AppendChild(_html);
        }

        private void EnsureHead()
        {
            if (_head != null) return;
            EnsureHtml();
            _head = new DomElement("head");
            _html!.AppendChild(_head);
        }

        /// <summary>
        /// Synthesize <html><body> as needed.  Returns the element that new
        /// body-level content should be appended to (the body).
        /// </summary>
        private DomElement EnsureBody()
        {
            if (_frameset != null)
                return _frameset;   // content inside a frameset is dropped by layout

            if (_body != null && !_bodyClosed)
                return _body;

            EnsureHtml();
            EnsureHead();

            if (_body == null)
            {
                _body = new DomElement("body");
                _html!.AppendChild(_body);
                Push(_body);
            }
            else
            {
                // Re-open the existing body (multiple <body> tags in the source)
                Push(_body);
            }
            _bodyClosed = false;
            return _body;
        }

        // ── Implied end tags (run BEFORE inserting the new element) ────────

        private void CloseImpliedBefore(string name)
        {
            if (_open.Count == 0 || _current == null)
                return;

            string top = _current.TagName;

            // Any block-level start tag closes an open <p> when it is the
            // innermost open element (the HTML 3.2 %inline content model).
            if (ClosesP.Contains(name) && top == "p")
            {
                PopOne();
                return;
            }

            switch (name)
            {
                case "li":
                    PopUpTo(name, extraStop: "ul ol dir menu");
                    break;

                case "dt":
                case "dd":
                    PopUpTo("dt", "dd", stop: "dl");
                    break;

                case "tr":
                    // Close open cells and any open row — never past a table,
                    // and never past an open row-group. tbody/thead/tfoot are
                    // a new <tr>'s intended PARENT, not stale elements to
                    // close: popping them here (as the old code did) orphans
                    // every row in the group as a loose sibling of <table>
                    // instead of a child of <thead>/<tbody>/<tfoot> (TN-06).
                    while (_open.Count > 0)
                    {
                        string t = _open.Peek().TagName;
                        if (t is "table" or "body" or "html" or "frameset" or
                               "tbody" or "thead" or "tfoot") break;
                        if (t is "td" or "th" or "tr" or "caption" or "colgroup")
                            { PopOne(); continue; }
                        PopOne();
                    }
                    break;

                case "td":
                case "th":
                    // Close the current CELL (and its unclosed content),
                    // leaving the row open for the next cell — and never
                    // crossing a table boundary (nested-table safety).
                    while (_open.Count > 0)
                    {
                        string t = _open.Peek().TagName;
                        if (t is "td" or "th") { PopOne(); break; }
                        if (t is "tr" or "table" or "tbody" or "thead" or "tfoot" or
                               "caption" or "colgroup" or "body" or "html" or "frameset")
                            break;
                        PopOne();
                    }
                    break;

                case "option":
                    if (IsOpen("option"))
                        PopOne();
                    break;

                case "p":
                    // <p><b>text<p> — the second P closes through the open
                    // inline elements to the real paragraph. The old code
                    // only checked the stack TOP, so the new <p> nested
                    // inside the <b> inside the old <p>. PopUpTo stops at
                    // table/cell boundaries, so this can never escape the
                    // current cell.
                    if (IsOpenInStack("p"))
                        PopUpTo("p");
                    break;

                case "a":
                    // Nested <a> closes the earlier link (era behaviour) —
                    // also through intervening inline elements.
                    if (IsOpenInStack("a"))
                        PopUpTo("a");
                    break;

                default:
                    // Headings do not nest: <h1><b>text<h2> closes both the
                    // <b> and the <h1>. The old top-only check swallowed the
                    // new heading inside the open inline child.
                    if (IsHeadingName(name) && StackHasOpenHeading())
                        PopToOpenHeading();
                    break;
            }
        }

        private static bool IsHeadingName(string name) =>
            name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6';

        private bool IsOpen(string name) => _current != null && _current.TagName == name;

        private bool IsOpenInStack(string name) => _open.Any(e => e.TagName == name);

        /// <summary>True when a heading is open above every boundary element.</summary>
        private bool StackHasOpenHeading()
        {
            foreach (var e in _open)
            {
                if (IsHeadingName(e.TagName)) return true;
                if (StopBoundaries.Contains(e.TagName)) return false;
            }
            return false;
        }

        /// <summary>Pops (through inline elements) until the open heading is closed.</summary>
        private void PopToOpenHeading()
        {
            while (_open.Count > 0)
            {
                var top = _open.Peek();
                if (IsHeadingName(top.TagName)) { PopOne(); return; }
                if (StopBoundaries.Contains(top.TagName)) return;
                PopOne();
            }
        }

        private void PopOne()
        {
            if (_open.Count == 0) return;
            _open.Pop();
            _current = _open.Count > 0 ? _open.Peek() : null;
        }

        /// <summary>
        /// Pop elements until <paramref name="target"/> is found and popped,
        /// stopping (without popping) at any boundary element.
        /// </summary>
        private void PopUpTo(string target, string extraStop = "")
        {
            var stops = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in extraStop.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                stops.Add(s);

            while (_open.Count > 0)
            {
                var top = _open.Peek();
                if (top.TagName == target)
                {
                    PopOne();
                    return;
                }
                if (StopBoundaries.Contains(top.TagName) || stops.Contains(top.TagName))
                    return;
                PopOne();
            }
        }

        private void PopUpTo(string t1, string t2, string stop)
        {
            while (_open.Count > 0)
            {
                var top = _open.Peek();
                if (top.TagName == t1 || top.TagName == t2)
                {
                    PopOne();
                    return;
                }
                if (StopBoundaries.Contains(top.TagName) || top.TagName == stop)
                    return;
                PopOne();
            }
        }

        // ── Start tags ─────────────────────────────────────────────────────

        private void HandleStartTag(StartTag tag)
        {
            switch (tag.Name)
            {
                case "html":
                    if (_html == null)
                    {
                        _html = Create(tag);
                        _doc.AppendChild(_html);
                        Push(_html);
                    }
                    // Duplicate <html> tags: keep the first, merge attrs.
                    else
                    {
                        foreach (var a in tag.Attrs)
                            _html.Attrs.TryAdd(a.Key, a.Value);
                    }
                    return;

                case "head":
                    if (_head == null && _body == null && _frameset == null)
                    {
                        EnsureHtml();
                        _head = Create(tag);
                        _html!.AppendChild(_head);
                        Push(_head);
                    }
                    // <head> after body started: ignore the tag itself; its
                    // children still parse into the current position.
                    return;

                case "body":
                    HandleBodyStart(tag);
                    return;

                case "frameset":
                    HandleFramesetStart(tag);
                    return;

                case "frame":
                    // Void element, only meaningful inside a frameset.
                    if (_frameset != null || _open.Any(e => e.TagName == "frameset"))
                    {
                        CloseImpliedBefore("frame");
                        InsertVoid(Create(tag));
                    }
                    return;

                case "noframes":
                    // Kept in the tree; layout hides it when a frameset renders.
                    InsertElementNormally(tag);
                    return;

                case "base":
                    {
                        var element = Create(tag);
                        // BASE applies document-wide.
                        if (element.HasAttr("href") && !string.IsNullOrEmpty(element.GetAttr("href")))
                        {
                            try
                            {
                                _doc.BaseUrl = _doc.BaseUrl?.Resolve(element.GetAttr("href")!)
                                              ?? ParsedUrl.Parse(element.GetAttr("href")!);
                            }
                            catch { /* invalid URL in <base href> — ignore */ }
                        }
                        if (element.HasAttr("target"))
                            _doc.BaseTarget = element.GetAttr("target")!;
                        InsertIntoHeadOrCurrent(element);
                        return;
                    }

                case "meta":
                    {
                        var element = Create(tag);
                        HandleMeta(element);
                        InsertIntoHeadOrCurrent(element);
                        return;
                    }

                case "title":
                    {
                        // <title> has no case of its own before this fix, so it
                        // fell through to the default branch (InsertElementNormally),
                        // which unconditionally calls EnsureBody() — meaning the
                        // very first <title> in the document forced <body> open
                        // early and every subsequent head tag (meta, link, and
                        // the whitespace between them) landed inside <body>
                        // instead of <head>. That left <body> with nothing but
                        // non-visual tags, so layout produced zero boxes and the
                        // page rendered blank. Route it through the same
                        // head-or-current logic as meta/link/base instead, and
                        // push it so its text content (handled in HandleText's
                        // "title" case) still accumulates into _doc.Title.
                        var element = Create(tag);
                        _documentTitleElement ??= element;
                        InsertIntoHeadOrCurrent(element);
                        if (!tag.SelfClosing)
                            Push(element);
                        return;
                    }

                case "link":
                    {
                        var element = Create(tag);
                        InsertIntoHeadOrCurrent(element);
                        return;
                    }

                // ── Head raw-text elements that ALSO legally appear in the
                //    body. These used to fall into InsertElementNormally,
                //    whose unconditional EnsureBody() created a SPURIOUS
                //    <body> NESTED INSIDE the still-open <head> whenever a
                //    page put a <script> (or <style>) before <title> — the
                //    Wayback/Netscape 1996 head starts with exactly such a
                //    script. The later </head> then popped through the
                //    phantom body, the real <body> start tag never re-opened
                //    it (it only re-pushes when _bodyClosed), and every
                //    content element of the page ended up a DIRECT child of
                //    <html>. Layout renders from <body> — which held only
                //    head junk — so the whole page came out completely
                //    blank. Route them head-or-current exactly like
                //    meta/link/base/title, and push so the tokenizer's raw
                //    text child attaches to the right element.
                case "script":
                case "style":
                case "noscript":
                    {
                        var element = Create(tag);
                        InsertIntoHeadOrCurrent(element);
                        if (!tag.SelfClosing)
                            Push(element);
                        return;
                    }

                case "isindex":
                    HandleIsindex(tag);
                    return;

                case "form":
                    {
                        // Period pages open <form> between <table> and <tr> and
                        // close it after the cells (the 1996 search-box idiom).
                        // The old default path pushed the form under <table>,
                        // where the <tr> handler's CloseImpliedCellsAndRows
                        // popped it EMPTY — every control then landed outside
                        // the form and BOTH the click and the Enter submit
                        // chains died.  Browsers foster-parent such a form out
                        // of the table (inserted before it as a sibling) and
                        // associate the cell controls with it through the
                        // "form element pointer"; we mirror both halves.
                        var element = Create(tag);
                        bool formStraddlesTableStructure =
                            _current != null &&
                            _current.TagName is "table" or "tbody" or "thead"
                                or "tfoot" or "tr";
                        if (formStraddlesTableStructure && !IsOpenInStack("form"))
                        {
                            var snapshot = _open.ToArray();   // index 0 = top
                            DomElement? table = null;
                            for (int i = 0; i < snapshot.Length; i++)
                                if (snapshot[i].TagName == "table") { table = snapshot[i]; break; }
                            var parent = table?.Parent;
                            if (table != null && parent != null)
                                parent.InsertBefore(element, table);
                            else
                            {
                                EnsureBody();
                                if (_current != null) _current.AppendChild(element);
                                else _doc.AppendChild(element);
                            }
                            // Never pushed: the table structure continues
                            // uninterrupted through <tr>/<td>.
                        }
                        else
                        {
                            CloseImpliedBefore(tag.Name);
                            EnsureBody();
                            if (_current != null) _current.AppendChild(element);
                            else _doc.AppendChild(element);
                            Push(element);
                        }
                        _formPointer = element;
                        return;
                    }

                case "table":
                case "tbody":
                case "thead":
                case "tfoot":
                case "tr":
                case "td":
                case "th":
                case "caption":
                case "colgroup":
                case "col":
                    HandleTableStructure(tag);
                    return;

                case "br":
                    InsertVoid(Create(tag));
                    return;

                default:
                    InsertElementNormally(tag);
                    return;
            }
        }

        private void InsertElementNormally(StartTag tag)
        {
            CloseImpliedBefore(tag.Name);
            EnsureBody();
            var element = Create(tag);

            // Era "form element pointer": controls that are NOT inside any
            // open <form> (the <form>-between-table-and-rows idiom, where
            // the form is foster-parented out of the table) still belong
            // to the most recently opened form — exactly like period
            // browsers.
            if (_formPointer != null && !IsOpenInStack("form") &&
                tag.Name is "input" or "select" or "textarea" or "button")
            {
                element.FormOwner = _formPointer;
            }

            if (_current != null)
                _current.AppendChild(element);
            else
                _doc.AppendChild(element);

            if (!IsVoidElement(tag.Name) && !tag.SelfClosing)
                Push(element);
            // A "self-closing" non-void tag (<div/>) — period browsers
            // actually treated it as an open tag, but tolerating the XML-ish
            // syntax here is harmless and matches IE-era behaviour.
        }

        private void InsertIntoHeadOrCurrent(DomElement element)
        {
            // BUG (found after the earlier <title> fix had no effect): this
            // used to check `_current == null`, but _current is _html (not
            // null) the whole time between <html> opening and <body>/<head>
            // ever being reached — Push(_html) sets it immediately. So this
            // always took the "body already started" branch for any document
            // whose <head> tag is implicit (i.e. almost everything), forcing
            // EnsureBody() on the very first meta/link/title and dragging
            // every subsequent head tag into <body> with it. What actually
            // matters is just "has <body> been opened yet", not what _current
            // happens to be.
            if (_body == null && _frameset == null)
            {
                EnsureHead();
                _head!.AppendChild(element);
            }
            else
            {
                // HEAD content appearing after BODY started — 1996 browsers
                // appended it wherever it appeared; these tags are all
                // non-visual so it renders fine.
                EnsureBody();
                if (_current != null)
                    _current.AppendChild(element);
                else
                    _doc.AppendChild(element);
            }
        }

        private void HandleBodyStart(StartTag tag)
        {
            if (_frameset != null)
                return;   // body after frameset: dropped

            if (_body != null)
            {
                // Multiple <body> tags: merge attributes into the first body
                // (period behaviour) and continue appending content to it.
                // FIRST occurrence wins per attribute — a later duplicate
                // <body> only fills gaps.  (Found by the VisualDiff harness:
                // broken-html.html's second <body bgcolor="#FFFFCC"> used
                // to overwrite the first <body bgcolor="#C0C0C0">, painting
                // the whole page the wrong colour where every period
                // browser — and the reference browser — kept the first.)
                foreach (var a in tag.Attrs)
                {
                    if (!_body.Attrs.ContainsKey(a.Key))
                        _body.Attrs[a.Key] = a.Value;
                    if (a.Key.StartsWith("on", StringComparison.OrdinalIgnoreCase) &&
                        a.Key.Length > 2)
                        _body.EventHandlers[a.Key.ToLowerInvariant()] = a.Value;
                }
                ApplyBodyAttrs(_body);
                // Re-open only when the body is genuinely closed — OR when
                // some recovery path (e.g. an end tag popping through a
                // mis-nested stack) removed it from the open stack while
                // _bodyClosed stayed false. Without the second check the
                // body start tag silently dropped and everything after it
                // landed one level too high (directly under <html>).
                if (_bodyClosed || !IsOpenInStack("body"))
                {
                    Push(_body);
                    _bodyClosed = false;
                }
                return;
            }

            EnsureHtml();
            EnsureHead();
            _body = Create(tag);
            _html!.AppendChild(_body);
            ApplyBodyAttrs(_body);
            Push(_body);
        }

        private void HandleFramesetStart(StartTag tag)
        {
            if (_body != null && _body.Children.Count > 0)
            {
                // Frameset after real body content — dropped (period behaviour).
                return;
            }

            if (_body != null)
            {
                // Empty body created earlier: remove it in favour of the frameset.
                var parent = _body.Parent;
                parent?.RemoveChild(_body);
                while (_open.Count > 0 && _open.Peek() != _body)
                    PopOne();
                if (_open.Count > 0)
                    PopOne(); // the body itself
                _body = null;
                _bodyClosed = false;
            }

            var element = Create(tag);
            if (_frameset == null)
            {
                EnsureHtml();
                _frameset = element;
                _html!.AppendChild(element);
                Push(element);
            }
            else
            {
                // Nested frameset
                if (_current != null)
                    _current.AppendChild(element);
                else
                    _doc.AppendChild(element);
                Push(element);
            }
        }

        private void ApplyBodyAttrs(DomElement body)
        {
            if (body.HasAttr("text")) _doc.BodyTextColor = body.GetAttr("text")!;
            if (body.HasAttr("link")) _doc.BodyLinkColor = body.GetAttr("link")!;
            if (body.HasAttr("vlink")) _doc.BodyVLinkColor = body.GetAttr("vlink")!;
            if (body.HasAttr("alink")) _doc.BodyALinkColor = body.GetAttr("alink")!;
            if (body.HasAttr("background")) _doc.BodyBackground = body.GetAttr("background");
            // BGCOLOR is read straight off the <body> element by the
            // renderer — no document-level mirror needed.
        }

        private void HandleMeta(DomElement element)
        {
            if (!element.HasAttr("http-equiv") || !element.HasAttr("content"))
                return;

            string httpEquiv = element.GetAttr("http-equiv")!.ToLowerInvariant();
            string content = element.GetAttr("content")!;

            if (httpEquiv == "refresh")
            {
                // "N;url=..." or just "N" (reload same page)
                _doc.MetaRefresh = content;
            }
            else if (httpEquiv == "content-type")
            {
                int idx = content.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string charset = content.Substring(idx + 8).Trim().Trim('"', '\'', ' ');
                    if (charset.Length > 0)
                        _doc.Charset = charset;
                }
            }
            else if (httpEquiv == "set-cookie")
            {
                try { _doc.Cookies?.Set(content, _doc.BaseUrl!); }
                catch { /* ignore malformed cookie */ }
            }
        }

        private void HandleIsindex(StartTag tag)
        {
            var element = Create(tag);
            EnsureBody();

            var form = new DomElement("form");
            var p = new DomElement("p");
            if (element.HasAttr("prompt") && element.GetAttr("prompt") is string prompt)
                p.AppendChild(new DomText(prompt + " "));

            var input = new DomElement("input");
            input.Attrs["type"] = "text";
            input.Attrs["name"] = element.HasAttr("name")
                ? element.GetAttr("name")!
                : "terms";
            p.AppendChild(input);
            form.AppendChild(p);

            if (_current != null)
                _current.AppendChild(form);
            else
                _doc.AppendChild(form);
            // The synthesized form is complete — nothing is left open.
        }

        /// <summary>
        /// Table-structure recovery: auto-close cells/rows and synthesize
        /// missing &lt;tr&gt; wrappers so every cell lands inside a row.
        /// </summary>
        private void HandleTableStructure(StartTag tag)
        {
            switch (tag.Name)
            {
                case "table":
                    InsertElementNormally(tag);
                    return;

                case "caption":
                case "colgroup":
                    CloseImpliedCellsAndRows();
                    InsertElementNormally(tag);
                    return;

                case "col":
                    CloseImpliedCellsAndRows();
                    InsertVoid(Create(tag));
                    return;

                case "tbody":
                case "thead":
                case "tfoot":
                    CloseImpliedCellsAndRows();
                    if (IsOpen("tr"))
                        PopOne();
                    InsertElementNormally(tag);
                    return;

                case "tr":
                    CloseImpliedCellsAndRows();
                    InsertElementNormally(tag);
                    return;

                case "td":
                case "th":
                    {
                        // Close only the current CELL — the row stays open so
                        // sibling cells land in the SAME <tr> (the previous
                        // close-everything behaviour produced one <tr> per cell).
                        CloseImpliedCellOnly();

                        // If we are directly under table/tbody/thead/tfoot (no
                        // <tr> was written), synthesize one.
                        if (_current != null &&
                            _current.TagName is "table" or "tbody" or "thead" or "tfoot")
                        {
                            var tr = new DomElement("tr");
                            _current.AppendChild(tr);
                            Push(tr);
                        }
                        else if (_current == null || _current.TagName is not ("tr" or "td" or "th"))
                        {
                            // Cell outside any table — treat as ordinary content.
                            InsertElementNormally(tag);
                            return;
                        }

                        var cell = Create(tag);
                        _current!.AppendChild(cell);
                        Push(cell);
                        return;
                    }
            }
        }

        private void CloseImpliedCellsAndRows()
        {
            // Pop cells/rows/captions and their unclosed content, but never
            // cross into an OUTER table (nested-table safety) and never pop
            // an open tbody/thead/tfoot — a new <tr> belongs INSIDE that
            // group, not next to it. Popping the group here (as the old
            // code did) orphans every row in it as a sibling of <table>
            // instead of a child of <thead>/<tbody>/<tfoot> (TN-06).
            while (_open.Count > 0)
            {
                string t = _open.Peek().TagName;
                if (t is "table" or "body" or "html" or "frameset" or
                       "tbody" or "thead" or "tfoot")
                    break;
                if (t is "td" or "th" or "tr" or "caption" or "colgroup")
                {
                    PopOne();
                    continue;
                }
                PopOne();
            }
        }

        /// <summary>
        /// Pops the current cell and anything left open inside it, leaving
        /// the containing row open — used when a new TD/TH starts.
        /// </summary>
        private void CloseImpliedCellOnly()
        {
            while (_open.Count > 0)
            {
                string t = _open.Peek().TagName;
                if (t is "td" or "th") { PopOne(); return; }
                if (t is "tr" or "table" or "tbody" or "thead" or "tfoot" or
                       "caption" or "colgroup" or "body" or "html" or "frameset")
                    return;
                PopOne();
            }
        }

        // ── End tags ───────────────────────────────────────────────────────

        /// <summary>
        /// Handles an end tag; returns HTML written by an executed script
        /// (empty string for every other tag).
        /// </summary>
        private string HandleEndTag(EndTag tag)
        {
            // </br> seen in the wild — treated as <br> by period browsers.
            if (tag.Name == "br")
            {
                InsertVoid(new DomElement("br"));
                return "";
            }

            // A stray </p> with no open paragraph is rendered by Netscape as
            // an empty paragraph.
            if (tag.Name == "p" && !IsOpenInStack("p"))
            {
                var p = new DomElement("p");
                EnsureBody();
                if (_current != null)
                    _current.AppendChild(p);
                else
                    _doc.AppendChild(p);
                return "";
            }

            // A foster-parented form (the <form>-between-table-and-rows
            // idiom) never sits on the open stack, so its </form> arrives
            // with no stack match — it must still clear the form pointer so
            // later controls don't attach to a closed form.  Likewise when
            // the pointer form itself is the one being closed.
            if (tag.Name == "form" && _formPointer != null)
            {
                var formSnapshot = _open.ToArray();
                int formDepth = -1;
                for (int i = 0; i < formSnapshot.Length; i++)
                    if (formSnapshot[i].TagName == "form") { formDepth = i; break; }
                if (formDepth < 0 || formSnapshot[formDepth] == _formPointer)
                    _formPointer = null;
            }

            // Find the most recently opened matching element (search from top).
            int depthFromTop = -1;
            var snapshot = _open.ToArray(); // index 0 = top
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].TagName == tag.Name)
                {
                    depthFromTop = i;
                    break;
                }
            }

            if (depthFromTop < 0)
                return ""; // stray end tag: ignore

            // Never pop html/body through a random end tag.
            if (snapshot[depthFromTop].TagName is "html" or "body" or "frameset")
            {
                if (tag.Name == "body")
                {
                    for (int i = 0; i < depthFromTop; i++)
                        PopOne();
                    PopOne(); // the body
                    _bodyClosed = true;
                }
                else if (tag.Name == "frameset")
                {
                    for (int i = 0; i < depthFromTop; i++)
                        PopOne();
                    PopOne();
                }
                // </html>: ignore entirely
                return "";
            }

            string written = "";

            // Closing a <script>: execute it and collect document.write output.
            if (tag.Name == "script")
            {
                var scriptElement = snapshot[depthFromTop];
                written = ExecuteScript(scriptElement);
            }

            for (int i = 0; i <= depthFromTop; i++)
                PopOne();

            return written;
        }

        private string ExecuteScript(DomElement scriptElement)
        {
            if (_onScript == null)
                return "";

            // External scripts (src=) are out of scope for this engine.
            if (scriptElement.HasAttr("src"))
                return "";

            string language = scriptElement.GetAttrOrDefault("language", "").ToLowerInvariant();
            string type = scriptElement.GetAttrOrDefault("type", "").ToLowerInvariant();
            if (language.Length > 0 &&
                !(language.StartsWith("java") || language.StartsWith("jscript") ||
                  language.StartsWith("ecmascript") || language.StartsWith("livescript")))
                return "";
            if (type.Length > 0 && type != "text/javascript")
                return "";

            var textNode = scriptElement.Children.OfType<DomText>().FirstOrDefault();
            if (textNode == null || string.IsNullOrEmpty(textNode.Data))
                return "";

            try
            {
                return _onScript(_doc, textNode.Data) ?? "";
            }
            catch
            {
                // Script errors must never halt parsing of the rest of the page.
                return "";
            }
        }

        // ── Text ───────────────────────────────────────────────────────────

        private void HandleText(string data)
        {
            if (string.IsNullOrEmpty(data))
                return;

            // Whitespace-only text before any structure exists is dropped.
            if (_current == null)
            {
                if (IsAllHtmlWhitespace(data))
                    return;
                EnsureBody();
            }

            // Text inside a frameset is ignored.
            if (_frameset != null && _current != null && IsInsideFrameset())
                return;

            var parent = _current!;

            switch (parent.TagName)
            {
                case "title":
                    {
                        // The first title element defines document.title. A later
                        // title in body content must not concatenate into it.
                        if (ReferenceEquals(parent, _documentTitleElement))
                            _doc.Title += data;
                        parent.AppendChild(new DomText(data));
                        return;
                    }
                case "pre":
                case "listing":
                case "xmp":
                case "textarea":
                case "plaintext":
                    {
                        // Literal whitespace preserved (PLAINTEXT used to fall
                        // through to the collapsing path below — it is a
                        // literal-text element like PRE).  HTML quirk: a single
                        // newline immediately after the start tag is ignored.
                        if (parent.Children.Count == 0)
                        {
                            if (data.StartsWith("\r\n")) data = data[2..];
                            else if (data.StartsWith('\n')) data = data[1..];
                        }
                        if (data.Length == 0)
                            return;
                        parent.AppendChild(new DomText(data));
                        return;
                    }
                case "script":
                case "style":
                    {
                        parent.AppendChild(new DomText(data));
                        return;
                    }
            }

            // Normal collapsing.  NBSP (U+00A0) must NOT be collapsed even
            // though .NET's char.IsWhiteSpace reports it as whitespace.
            var sb = new StringBuilder(data.Length);
            bool lastWasWhitespace = false;
            foreach (char c in data)
            {
                if (IsHtmlWhitespace(c))
                {
                    if (!lastWasWhitespace)
                    {
                        sb.Append(' ');
                        lastWasWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasWhitespace = false;
                }
            }

            string collapsed = sb.ToString();
            if (collapsed.Length == 0)
                return;

            // Drop a lone space that is the first child of a common block
            // container — leading indentation noise.
            if (collapsed == " " && parent.Children.Count == 0 &&
                parent.TagName is "p" or "div" or "h1" or "h2" or "h3" or "h4"
                                 or "h5" or "h6" or "li" or "td" or "th"
                                 or "blockquote" or "body" or "center" or "form")
                return;

            parent.AppendChild(new DomText(collapsed));
        }

        private bool IsInsideFrameset()
        {
            foreach (var e in _open)
            {
                if (e.TagName == "frameset") return true;
                if (e.TagName == "noframes") return false;
            }
            return false;
        }

        private static bool IsHtmlWhitespace(char c) =>
            c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

        private static bool IsAllHtmlWhitespace(string s)
        {
            foreach (char c in s)
                if (!IsHtmlWhitespace(c))
                    return false;
            return true;
        }

        private static bool IsVoidElement(string name) => name switch
        {
            "img" or "br" or "hr" or "input" or "param" or "wbr" or "area"
            or "col" or "basefont" or "frame" or "isindex" or "link"
            or "meta" or "base" or "spacer" or "bgsound" or "embed" => true,
            _ => false
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Quirks mode from DOCTYPE
    // ─────────────────────────────────────────────────────────────────────

    private static string DetermineQuirksMode(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return "quirks";

        string upper = rawText.ToUpperInvariant();

        if (upper.Contains("HTML 3.2"))
            return "html32";
        if (upper.Contains("HTML 2.0") || upper.Contains("RFC 1866"))
            return "html20";
        if (upper.Contains("HTML 4.01") || upper.Contains("HTML 4.0") ||
            upper.Contains("XHTML"))
            return "strict";
        // "<!DOCTYPE html>" and friends
        if (upper.Contains("<!DOCTYPE HTML>") && !upper.Contains("DTD"))
            return "strict";
        if (upper.Contains("HTML") && upper.Contains("DTD"))
            return "quirks";
        return "quirks";
    }
}