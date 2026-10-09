// Step 4 unit tests — form submission semantics (FormSubmitter).
using Retro96.Engine.Dom;
using Retro96.Engine.Forms;
using Retro96.Engine.Html;
using Retro96.Engine.Network;

namespace RetroTests;

public class FormDomTests
{
    private static DomDocument Parse(string html) =>
        HtmlParser.Parse(html, ParsedUrl.Parse("http://x.test/form.html"), new CookieStore());

    private static List<(string, string)> PairsOf(string html)
    {
        var doc = Parse(html);
        var form = doc.FirstTag("form")!;
        return FormSubmitter.CollectPairs(form);
    }

    [Fact]
    public void TextInputsAreSuccessfulControls()
    {
        var pairs = PairsOf("<form><input type=text name=q value='hello world'><input type=submit value=Go></form>");
        Check.That(pairs.Count == 1, "submit button is not a successful control (era)", pairs.Count.ToString());
        Check.That(pairs.Count == 1 && pairs[0].Item1 == "q" && pairs[0].Item2 == "hello world",
            "named text input submits its value");
        Check.Done();
    }

    [Fact]
    public void SequentialFocusOrderIncludesLinksAndControlsAndHonorsTabIndex()
    {
        var doc = Parse("<body>" +
                        "<a id=natural href='#x'>link</a>" +
                        "<input id=late tabindex=3>" +
                        "<button id=early tabindex=1>button</button>" +
                        "<input id=disabled disabled>" +
                        "<input id=hidden type=hidden tabindex=0>" +
                        "<select id=select><option>One</option></select>" +
                        "<textarea id=textarea></textarea>" +
                        "<div id=custom tabindex=0></div>" +
                        "<div id=excluded tabindex=-1></div>" +
                        "<a id=nohref>not a stop</a>" +
                        "</body>");

        var order = TabNavigation.GetSequentialFocusOrder(doc)
            .Select(element => element.GetAttr("id"))
            .ToList();
        Check.That(order.SequenceEqual(new[]
        {
            "early", "late", "natural", "select", "textarea", "custom"
        }), "positive tabindex sorts first, then native and explicit-zero stops follow document order",
            string.Join(",", order));
        Check.Done();
    }

    [Fact]
    public void SequentialFocusIndexStartsAtCorrectEndAndWrapsInBothDirections()
    {
        Check.That(TabNavigation.GetNextIndex(-1, 4, 1) == 0,
            "forward tab from no focus starts at the first item");
        Check.That(TabNavigation.GetNextIndex(-1, 4, -1) == 3,
            "reverse tab from no focus starts at the last item");
        Check.That(TabNavigation.GetNextIndex(3, 4, 1) == 0,
            "forward tab wraps at the end");
        Check.That(TabNavigation.GetNextIndex(0, 4, -1) == 3,
            "reverse tab wraps at the beginning");
        Check.Done();
    }

    [Fact]
    public void PasswordAndHiddenSubmit()
    {
        var pairs = PairsOf("<form><input type=password name=pw value=secret><input type=hidden name=h value=x></form>");
        Check.That(pairs.Count == 2 && pairs.Any(p => p.Item1 == "pw" && p.Item2 == "secret"),
            "password input submits its value");
        Check.That(pairs.Any(p => p.Item1 == "h" && p.Item2 == "x"), "hidden input submits its value");
        Check.Done();
    }

    [Fact]
    public void CheckboxesAndRadiosOnlyWhenChecked()
    {
        var pairs = PairsOf("<form>" +
                            "<input type=checkbox name=cb1 checked>" +
                            "<input type=checkbox name=cb2>" +
                            "<input type=radio name=r value=a checked>" +
                            "<input type=radio name=r value=b>" +
                            "</form>");
        Check.That(pairs.Count == 2, "only checked boxes/radios are successful", pairs.Count.ToString());
        Check.That(pairs.Any(p => p.Item1 == "cb1" && p.Item2 == "on"), "checked checkbox default value 'on'");
        Check.That(pairs.Any(p => p.Item1 == "r" && p.Item2 == "a"), "checked radio submits its value");
        Check.That(!pairs.Any(p => p.Item1 == "cb2"), "unchecked checkbox not submitted");
        Check.Done();
    }

    [Fact]
    public void SelectSubmitsSelectedOrDefaultOption()
    {
        var pairs = PairsOf("<form><select name=prod>" +
                            "<option value=p1>One</option><option value=p2 selected>Two</option>" +
                            "</select></form>");
        Check.That(pairs.Count == 1 && pairs[0].Item1 == "prod" && pairs[0].Item2 == "p2",
            "selected option submits", pairs.FirstOrDefault().ToString());

        pairs = PairsOf("<form><select name=prod><option>Alpha</option><option>Beta</option></select></form>");
        Check.That(pairs.Count == 1 && pairs[0].Item2 == "Alpha",
            "no selection → first option (era default)", pairs.FirstOrDefault().ToString());

        // option without value attr submits its text
        pairs = PairsOf("<form><select name=p><option value=>Text</option></select></form>");
        Check.That(pairs.Count == 1, "option without value still submits");
        Check.Done();
    }

    [Fact]
    public void MultipleSelectSubmitsAllSelected()
    {
        var pairs = PairsOf("<form><select name=ms multiple>" +
                            "<option value=1 selected>One</option>" +
                            "<option value=2>Two</option>" +
                            "<option value=3 selected>Three</option>" +
                            "</select></form>");
        Check.That(pairs.Count == 2, "multiple select submits all selected", pairs.Count.ToString());
        Check.That(pairs.Any(p => p.Item2 == "1") && pairs.Any(p => p.Item2 == "3"), "both selected values present");
        Check.Done();
    }

    [Fact]
    public void TextareaSubmitsContent()
    {
        var pairs = PairsOf("<form><textarea name=comments rows=4 cols=40>line one\nline two</textarea></form>");
        Check.That(pairs.Count == 1 && pairs[0].Item1 == "comments" && pairs[0].Item2.Contains("line one"),
            "textarea submits inner text", pairs.FirstOrDefault().ToString());
        Check.Done();
    }

    [Fact]
    public void GetRequestBuildsQueryAndResolvesAction()
    {
        var doc = Parse("<form action=/search method=get><input type=text name=q value=retro></form>");
        var form = doc.FirstTag("form")!;
        var req = FormSubmitter.BuildRequest(form, doc.BaseUrl, null);
        Check.That(req.Method == "get", "method=get preserved");
        Check.That(req.Url == "http://x.test/search", "action resolved against base", req.Url);
        Check.That(req.QueryString == "q=retro", "query string built", req.QueryString);
        Check.Done();
    }

    [Fact]
    public void PostRequestAndEmptyAction()
    {
        var doc = Parse("<form method=POST><input type=text name=a value=1></form>");
        var form = doc.FirstTag("form")!;
        var req = FormSubmitter.BuildRequest(form, doc.BaseUrl, null);
        Check.That(req.Method == "post", "method=POST lower-cased", req.Method);
        Check.That(req.Url == "http://x.test/form.html", "empty action → document URL", req.Url);

        var doc2 = Parse("<form action='http://other.test/handle' target=_blank method=get><input type=text name=b value=2></form>");
        var req2 = FormSubmitter.BuildRequest(doc2.FirstTag("form")!, doc2.BaseUrl, null);
        Check.That(req2.Url == "http://other.test/handle", "absolute action used verbatim");
        Check.That(req2.Target == "_blank", "form target preserved", req2.Target ?? "(null)");
        Check.Done();
    }

    [Fact]
    public void TextPlainPostBuildsMailBody()
    {
        var doc = Parse(
            "<form action='mailto:you@example.com' method='post' enctype='text/plain'>" +
            "<input type='text' name='visitor' value='Ada Lovelace'>" +
            "<textarea name='message'>Hello there</textarea></form>");
        var req = FormSubmitter.BuildRequest(doc.FirstTag("form")!, doc.BaseUrl, null);

        Check.That(req.TextPlainBody ==
                   "visitor=Ada Lovelace\r\nmessage=Hello there\r\n",
            "text/plain POST serializes successful controls as an email body",
            req.TextPlainBody ?? "(null)");
        Check.Done();
    }


    [Fact]
    public void MultipartPreservesRepeatedNamesAndOnlyActivatedSubmitter()
    {
        var doc = Parse(
            "<form method=post enctype='multipart/form-data' action=/upload>" +
            "<input type=text name=tag value=a><input type=text name=tag value=b>" +
            "<input type=submit name=go value=One><input type=submit name=go value=Two>" +
            "</form>");
        var form = doc.FirstTag("form")!;
        var submitter = form.Descendants().OfType<DomElement>()
            .First(e => e.TagName == "input" && e.GetAttr("value") == "Two");
        var req = FormSubmitter.BuildRequest(form, doc.BaseUrl, null, null, submitter);

        Check.That(req.MultipartFields != null && req.MultipartFields.Count(f => f.Name == "tag") == 2,
            "multipart fields preserve repeated names", req.MultipartFields?.Count.ToString() ?? "null");
        Check.That(req.MultipartFields != null && req.MultipartFields.Count(f => f.Name == "go" && f.Value == "Two") == 1,
            "only the activated submit button is included");
        Check.That(req.MultipartFields != null && req.MultipartFields.All(f => f.Name != "go" || f.Value != "One"),
            "inactive submit buttons are excluded");
        Check.Done();
    }

    [Fact]
    public void MultipartImageSubmitIncludesClickCoordinates()
    {
        var doc = Parse(
            "<form method=post enctype='multipart/form-data' action=/upload>" +
            "<input type=image name=imggo src=pixel.gif></form>");
        var form = doc.FirstTag("form")!;
        var image = form.Descendants().OfType<DomElement>()
            .First(e => e.TagName == "input" && e.GetAttr("type") == "image");
        var req = FormSubmitter.BuildRequest(form, doc.BaseUrl, null,
            ("imggo", 12, 6), image);

        Check.That(req.MultipartFields?.Any(f => f.Name == "imggo.x" && f.Value == "12") == true,
            "multipart image submit includes the clicked x coordinate");
        Check.That(req.MultipartFields?.Any(f => f.Name == "imggo.y" && f.Value == "6") == true,
            "multipart image submit includes the clicked y coordinate");
        Check.Done();
    }

    [Fact]
    public void ControlsInsideTablesAreCollected()
    {
        // the theoldnet layout idiom: controls inside table cells
        var pairs = PairsOf("<form><table><tr><td>http:</td>" +
                            "<td><input type=text name=url value=http://x.test/></td>" +
                            "<td><select name=year><option value=1996>1996</option></select></td>" +
                            "<td><input type=submit value='Go'></td>" +
                            "</tr></table></form>");
        Check.That(pairs.Count == 2, "both named controls collected from inside the table", pairs.Count.ToString());
        Check.That(pairs.Any(p => p.Item1 == "url"), "url input collected");
        Check.That(pairs.Any(p => p.Item1 == "year" && p.Item2 == "1996"), "select collected");
        Check.Done();
    }

    [Fact]
    public void FosterParentedFormOwnsTableControls()
    {
        // era idiom: <form> opened between <table> and <tr>
        var doc = HtmlParser.Parse(
            "<html><body><form name=f action=/g><table><tr><td><input type=text name=v value=1></td></tr></table></form></body></html>",
            ParsedUrl.Parse("http://x.test/"), new CookieStore());
        var form = doc.FirstTag("form");
        Check.That(form != null, "form parsed");
        if (form != null)
        {
            var pairs = FormSubmitter.CollectPairs(form);
            Check.That(pairs.Any(p => p.Item1 == "v"),
                "controls inside the table are collected for the enclosing form");
        }
        Check.Done();
    }
}
