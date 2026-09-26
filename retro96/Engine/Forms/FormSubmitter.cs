using Retro96.Engine.Dom;
using Retro96.Engine.Network;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Retro96.Engine.Forms;

/// <summary>Represents a serialized form submission request.</summary>
public sealed record MultipartField(string Name, string Value);
public sealed record MultipartFile(string Name, string Filename, string ContentType, byte[] Bytes);

public record FormSubmitRequest
{
    public string Url { get; init; }
    public string QueryString { get; init; }
    public string Method { get; init; }
    public string? Target { get; init; }
    public IReadOnlyList<MultipartField>? MultipartFields { get; init; }
    public IReadOnlyList<MultipartFile>? MultipartFiles { get; init; }

    public FormSubmitRequest(
        string url,
        string queryString,
        string method,
        string? target,
        IReadOnlyList<MultipartField>? multipartFields = null,
        IReadOnlyList<MultipartFile>? multipartFiles = null)
    {
        Url = url;
        QueryString = queryString;
        Method = method;
        Target = target;
        MultipartFields = multipartFields;
        MultipartFiles = multipartFiles;
    }
}

public static class FormSubmitter
{
    private sealed record FileSelection(string FullPath, string DisplayName);
    private static readonly ConditionalWeakTable<DomElement, FileSelection> FileSelections = new();

    public static void SetFileSelection(DomElement input, string fullPath)
    {
        FileSelections.Remove(input);
        FileSelections.Add(input, new FileSelection(fullPath, System.IO.Path.GetFileName(fullPath)));
    }

    public static bool TryGetFileSelection(DomElement input, out string fullPath, out string displayName)
    {
        if (FileSelections.TryGetValue(input, out var selected))
        {
            fullPath = selected.FullPath;
            displayName = selected.DisplayName;
            return true;
        }
        fullPath = string.Empty; displayName = string.Empty; return false;
    }

    public static void ClearFileSelection(DomElement input) => FileSelections.Remove(input);

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
        (string Name, int X, int Y)? imageClick = null, DomElement? submitter = null)
    {
        string action = form.GetAttrOrDefault("action", "");
        string method = form.GetAttrOrDefault("method", "get").Trim().ToLowerInvariant();

        string resolved = action;
        if (baseUrl != null)
        {
            if (string.IsNullOrEmpty(action))
                resolved = baseUrl.ToAbsolute();
            else
                resolved = baseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? FileUrls.Resolve(baseUrl, action)
                    : baseUrl.Resolve(action).ToAbsolute();
        }

        var pairs = CollectPairs(form, imageClick, submitter, includeFileDisplayNames: true);

        string qs = string.Join("&", pairs.Select(p =>
            $"{ParsedUrl.PercentEncode(p.Item1)}={ParsedUrl.PercentEncode(p.Item2)}"));

        string? target = form.GetAttr("target");
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(baseTarget))
            target = baseTarget;

        List<MultipartField>? fields = null;
        List<MultipartFile>? files = null;
        if (method == "post" && form.GetAttrOrDefault("enctype", "application/x-www-form-urlencoded")
            .Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            fields = new List<MultipartField>();
            files = new List<MultipartFile>();
            // Reuse successful-control ordering so repeated names,
            // checkboxes, radios and selects are preserved exactly. File
            // inputs are emitted as MIME file parts below, so omit only the
            // synthetic display-name pair for those controls.
            var nonFilePairs = CollectPairs(form, imageClick, submitter, includeFileDisplayNames: false);
            foreach (var (name, value) in nonFilePairs)
                if (!string.IsNullOrEmpty(name)) fields.Add(new MultipartField(name, value));

            foreach (var field in FieldsOf(form))
            {
                var n = field.GetAttr("name");
                if (string.IsNullOrEmpty(n) || field.HasAttr("disabled")) continue;
                if (field.TagName == "input" && field.GetAttrOrDefault("type", "text").Equals("file", StringComparison.OrdinalIgnoreCase) &&
                    TryGetFileSelection(field, out var fullPath, out var displayName))
                {
                    try
                    {
                        var info = new System.IO.FileInfo(fullPath);
                        if (info.Length > 8L * 1024 * 1024) continue;
                        files.Add(new MultipartFile(n, displayName, ContentTypeForPath(fullPath),
                            System.IO.File.ReadAllBytes(fullPath)));
                    }
                    catch { }
                }
            }
        }

        return new FormSubmitRequest(resolved, qs, method, target, fields, files);
    }

    /// <summary>
    /// The successful-controls set (era flavour): named text/password/
    /// hidden inputs, checked checkboxes and radios, textareas, single
    /// or multiple selects (first option wins when none selected), and
    /// the image button's .x/.y click coordinates.
    /// </summary>
    private static string ContentTypeForPath(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => "image/gif", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png",
            ".txt" => "text/plain", ".html" or ".htm" => "text/html", _ => "application/octet-stream"
        };

    public static List<(string, string)> CollectPairs(
        DomElement form, (string Name, int X, int Y)? imageClick = null,
        DomElement? submitter = null, bool includeFileDisplayNames = true)
    {
        var pairs = new List<(string, string)>();

        foreach (var field in FieldsOf(form))
        {
            string? name = field.GetAttr("name");
            if (string.IsNullOrEmpty(name)) continue;
            if (field.HasAttr("disabled")) continue;

            switch (field.TagName)
            {
                case "input":
                    {
                        string type = field.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
                        if (type == "hidden" || type == "text" || type == "password")
                            pairs.Add((name, field.GetAttr("value") ?? ""));
                        else if (type is "submit" or "button" && field.HasAttr("name") && ReferenceEquals(field, submitter))
                            pairs.Add((name, field.GetAttr("value") ?? ""));
                        else if (type == "file" && includeFileDisplayNames &&
                                 TryGetFileSelection(field, out _, out var displayName))
                            pairs.Add((name, displayName));
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
                    pairs.Add((name, field.InnerText ?? ""));
                    break;
                case "button":
                    if (field.GetAttrOrDefault("type", "submit").Equals("submit", StringComparison.OrdinalIgnoreCase) &&
                        ReferenceEquals(field, submitter))
                        pairs.Add((name, field.GetAttr("value") ?? field.InnerText ?? ""));
                    break;
                case "select":
                    {
                        var selected = field.Descendants().OfType<DomElement>()
                            .Where(o => o.TagName == "option" && o.HasAttr("selected"))
                            .ToList();
                        bool multiple = field.HasAttr("multiple");
                        if (selected.Count == 0 && !multiple)
                        {
                            var first = field.Descendants().OfType<DomElement>()
                                .FirstOrDefault(o => o.TagName == "option");
                            if (first != null) selected.Add(first);
                        }
                        foreach (var opt in multiple ? selected : selected.Take(1))
                            pairs.Add((name, opt.GetAttrOrDefault("value", (opt.InnerText ?? "").Trim())));
                        break;
                    }
            }
        }

        return pairs;
    }
}
