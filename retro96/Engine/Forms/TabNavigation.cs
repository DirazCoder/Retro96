using System.Globalization;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Forms;

public static class TabNavigation
{
    public static int GetNextIndex(int currentIndex, int focusableCount, int direction)
    {
        if (focusableCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(focusableCount));
        if (direction == 0)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (currentIndex < 0 || currentIndex >= focusableCount)
            return direction > 0 ? 0 : focusableCount - 1;

        int step = direction > 0 ? 1 : -1;
        return (currentIndex + step + focusableCount) % focusableCount;
    }

    public static IReadOnlyList<DomElement> GetSequentialFocusOrder(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.ElementDescendants()
            .Select((element, order) => new
            {
                Element = element,
                Order = order,
                TabIndex = GetTabIndex(element)
            })
            .Where(item => item.TabIndex.HasValue
                ? item.TabIndex.Value >= 0
                : IsNaturallyFocusable(item.Element))
            .Where(item => !IsHiddenInput(item.Element))
            .Where(item => !IsDisabledControl(item.Element))
            .OrderBy(item => item.TabIndex is > 0 ? 0 : 1)
            .ThenBy(item => item.TabIndex is > 0 ? item.TabIndex : 0)
            .ThenBy(item => item.Order)
            .Select(item => item.Element)
            .ToList();
    }

    private static int? GetTabIndex(DomElement element)
    {
        var value = element.GetAttr("tabindex");
        if (value == null ||
            !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tabIndex) ||
            tabIndex is < -32768 or > 32767)
            return null;
        return tabIndex;
    }

    private static bool IsNaturallyFocusable(DomElement element)
    {
        if (element.TagName is "a" or "area")
            return element.HasAttr("href");

        if (element.TagName is "textarea" or "select" or "button")
            return true;

        if (element.TagName == "input")
            return !element.GetAttrOrDefault("type", "text").Trim()
                .Equals("hidden", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool IsHiddenInput(DomElement element) =>
        element.TagName == "input" &&
        element.GetAttrOrDefault("type", "text").Trim()
            .Equals("hidden", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledControl(DomElement element) =>
        (element.TagName is "input" or "select" or "textarea" or "button") &&
        element.HasAttr("disabled");
}
