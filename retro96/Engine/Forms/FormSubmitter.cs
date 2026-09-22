using Retro96.Engine.Dom;
using Retro96.Engine.Network;

namespace Retro96.Engine.Forms;

/// </summary>
public record FormSubmitRequest(
    string Url,          // resolved action (or the document URL for empty action)
    string QueryString,  // percent-encoded field pairs, &-joined
    string Method,       // "get" | "post" (lower-cased)
    string? Target);     // form target / base target / null

public static class FormSubmitter
{
    /// <summary>Walks the DOM parent chain looking for an enclosing element.</summary>
    public static DomElement? FindAncestor(DomElement element, string tagName)
    {
        var node = element as DomNode;
        while (node != null)
        {
            if (node is DomElement de && de.TagName == tagName) return de;
            node = node.Parent;
        }
        return null;
    }

    public static DomElement? FindEnclosingForm(DomElement element) =>
        FindAncestor(element, "form") ?? element.FormOwner;

    /// <summary>
    /// The form's controls: tree descendants, PLUS controls associated
    /// through the era "form element pointer" — foster-parented forms
    /// (the &lt;form&gt;-between-table-and-rows idiom) own controls that
    /// live in the table cells, not inside the form element.  Used by
    /// CollectPairs, DomBindings' form.elements / DOM-0 named access, and
    /// the shell's form reset.
    /// </summary>
    public static IEnumerable<DomElement> FieldsOf(DomElement form)
    {
        foreach (var e in form.Descendants().OfType<DomElement>())
            yield return e;

        var doc = form.OwnerDocument();
        if (doc != null)
        {
            foreach (var e in doc.ElementDescendants())
                if (e.FormOwner != null && ReferenceEquals(e.FormOwner, form))
                    yield return e;
        }
    }

    /// <summary>
    /// Builds the submit request exactly the way the shell submits:
    /// collects successful control pairs, percent-encodes them, resolves
    /// the action against the document base URL and honours the target.
    /// </summary>
    public static FormSubmitRequest BuildRequest(
        DomElement form,
        ParsedUrl? baseUrl,
        string? baseTarget,
        (string Name, int X, int Y)? imageClick = null)
    {
        string action = form.GetAttrOrDefault("action", "");
        string method = form.GetAttrOrDefault("method", "get").Trim().ToLowerInvariant();

        string resolved = action;
        if (baseUrl != null)
        {
            if (string.IsNullOrEmpty(action))
                resolved = baseUrl.ToAbsolute();
            else
                resolved = baseUrl.Resolve(action).ToAbsolute();
        }

        var pairs = CollectPairs(form, imageClick);

        string qs = string.Join("&", pairs.Select(p =>
            $"{ParsedUrl.PercentEncode(p.Item1)}={ParsedUrl.PercentEncode(p.Item2)}"));

        string? target = form.GetAttr("target");
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(baseTarget))
            target = baseTarget;

        return new FormSubmitRequest(resolved, qs, method, target);
    }

    /// <summary>
    /// The successful-controls set (era flavour): named text/password/
    /// hidden inputs, checked checkboxes and radios, textareas, single
    /// or multiple selects (first option wins when none selected), and
    /// the image button's .x/.y click coordinates.
    /// </summary>
    public static List<(string, string)> CollectPairs(
        DomElement form, (string Name, int X, int Y)? imageClick = null)
    {
        var pairs = new List<(string, string)>();

        foreach (var field in FieldsOf(form))
        {
            string? name = field.GetAttr("name");
            if (string.IsNullOrEmpty(name)) continue;

            switch (field.TagName)
            {
                case "input":
                    {
                        string type = field.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
                        if (type == "hidden" || type == "text" || type == "password")
                            pairs.Add((name, field.GetAttr("value") ?? ""));
                        else if (type is "checkbox" or "radio")
                        {
                            if (field.HasAttr("checked"))
                                pairs.Add((name, field.GetAttrOrDefault("value", "on")));
                        }
                        else if (type == "image" && imageClick != null &&
                                 field.GetAttr("name") == imageClick.Value.Name)
                        {
                            pairs.Add(($"{name}.x", imageClick.Value.X.ToString()));
                            pairs.Add(($"{name}.y", imageClick.Value.Y.ToString()));
                        }
                        break;
                    }
                case "textarea":
                    pairs.Add((name, field.InnerText));
                    break;
                case "select":
                    {
                        var selected = field.Descendants().OfType<DomElement>()
                            .Where(o => o.TagName == "option" && o.HasAttr("selected"))
                            .ToList();
                        if (selected.Count == 0)
                        {
                            var first = field.Descendants().OfType<DomElement>()
                                .FirstOrDefault(o => o.TagName == "option");
                            if (first != null) selected.Add(first);
                        }
                        bool multiple = field.HasAttr("multiple");
                        foreach (var opt in multiple ? selected : selected.Take(1))
                            pairs.Add((name, opt.GetAttrOrDefault("value", (opt.InnerText ?? "").Trim())));
                        break;
                    }
            }
        }

        return pairs;
    }
}
