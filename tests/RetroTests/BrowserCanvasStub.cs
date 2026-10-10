// Headless stand-in for the WinForms BrowserCanvas: every shell hook the
// JS bindings touch, recording what scripts did so the tests can assert
// on it.  Lives in the Retro96 namespace so DomBindings binds to THIS
// class instead of the real canvas (which needs a Windows message loop).
// A PLAIN class — the canvas services DomBindings needs are virtual
// methods (ShowAlert / ShowConfirm / ShowPrompt / CloseHostWindow /
// GetOuterSize / GetScreenMetrics), so the script layer compiles and runs
// on any framework; the real canvas overrides them with WinForms UI.
using Retro96.Drawing;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;

namespace Retro96;

public class BrowserCanvas
{
    public sealed class Recorded
    {
        public List<string> Navigations = new();
        public List<string> PrefetchedImages = new();
        public List<string> Alerts = new();
        public List<string> Confirms = new();
        public int Closes;
        public int Rerenders;
        public int Reflows;
        public int CompositedUpdates;
        public int CompositedTextUpdates;
        public List<bool> FormSubmitDispatchFlags = new();
    }

    public Recorded Log { get; } = new();
    public LayoutBox? RootBox { get; set; }
    public float PageScrollX { get; private set; }
    public float PageScrollY { get; private set; }

    public event Action<string>? NavigateRequested;
    public event Action<string>? NewWindowRequested;

    public void NavigateTo(string url) { Log.Navigations.Add(url); NavigateRequested?.Invoke(url); }
    public void NavigateToReplace(string url) { Log.Navigations.Add(url); NavigateRequested?.Invoke(url); }
    public void OpenNewWindow(string url) { Log.Navigations.Add("[window] " + url); NewWindowRequested?.Invoke(url); }
    public void ScrollTo(int x, int y) { }
    public void ScrollBy(int dx, int dy) { }
    public void SetPageScrollOffset(float x, float y)
    {
        PageScrollX = x;
        PageScrollY = y;
    }
    public Size GetViewportSize() => new(800, 600);
    public void SubmitForm(DomElement? form, object? clickCoords, bool dispatchSubmitEvent = true)
    {
        Log.FormSubmitDispatchFlags.Add(dispatchSubmitEvent);
    }
    public void UpdateDocumentTitle(string title) { }
    // WinForms affinity members referenced by DomBindings callbacks; in the
    // headless stub everything runs synchronously on the test thread.
    public bool IsHandleCreated => true;
    public bool IsDisposed => false;
    public IAsyncResult BeginInvoke(Delegate method, params object?[] args)
    {
        method.DynamicInvoke(args);
        return new CompletedAsyncResult();
    }
    private sealed class CompletedAsyncResult : IAsyncResult
    {
        public bool IsCompleted => true;
        public WaitHandle AsyncWaitHandle => new ManualResetEvent(true);
        public object? AsyncState => null;
        public bool CompletedSynchronously => true;
    }
    public void RequestRerender() { Log.Rerenders++; }
    public void ReflowDocument() { Log.Reflows++; }
    public bool TryApplyCompositedLayerUpdates(
        DomDocument document,
        IReadOnlyList<KeyValuePair<DomElement, DocumentBindingsState.CompositedStyleUpdate>> updates)
    {
        var paintRoots = new HashSet<DomElement>();
        foreach (var (element, update) in updates)
        {
            var box = element.LayoutBox ?? element.Box;
            if (box == null)
                return false;
            var paintRoot = FindCompositedPaintRoot(box);
            if (paintRoot == null)
                return false;
            paintRoots.Add(paintRoot);

            if (update.ResolveStyle &&
                !StyleResolver.TryResolveElement(element, document, 800))
                return false;

            var style = element.Style;
            if (style == null)
                return false;
            if (update.ResolveStyle &&
                (!SameNullable(update.Left ?? update.Original.Left, style.Left) ||
                 !SameNullable(update.Top ?? update.Original.Top, style.Top) ||
                 !SameNullable(update.Width ?? update.Original.Width, style.Width) ||
                 !SameNullable(update.Height ?? update.Original.Height, style.Height)))
                return false;
            if (!Same(box.Width, update.Original.Width ?? box.Width) ||
                !Same(box.Height, update.Original.Height ?? box.Height))
                return false;
            if (!update.ResolveStyle)
            {
                if (update.Left is float left) style.Left = left;
                if (update.Top is float top) style.Top = top;
                if (update.Width is float width) style.Width = width;
                if (update.Height is float height) style.Height = height;
            }

            var containingBlock = FindAbsoluteContainingBlock(box);
            float originX = containingBlock.X + containingBlock.BorderLeft;
            float originY = containingBlock.Y + containingBlock.BorderTop;
            float oldX = update.Original.Left is float oldLeft
                ? originX + oldLeft + box.MarginLeft
                : box.X;
            float oldY = update.Original.Top is float oldTop
                ? originY + oldTop + box.MarginTop
                : box.Y;
            float newX = style.Left is float newLeft
                ? originX + newLeft + box.MarginLeft
                : box.X;
            float newY = style.Top is float newTop
                ? originY + newTop + box.MarginTop
                : box.Y;
            box.X += newX - oldX;
            box.Y += newY - oldY;
            box.Width = style.Width ?? box.Width;
            box.Height = style.Height ?? box.Height;
        }

        foreach (var paintRoot in paintRoots)
            paintRoot.IsCompositedLayer = true;
        Log.CompositedUpdates += updates.Count;
        Log.Rerenders++;
        return true;
    }
    public bool TryApplyPaintOnlyClassUpdates(
        DomDocument document,
        IReadOnlyList<KeyValuePair<DomElement, DocumentBindingsState.CompositedStyleSnapshot>> updates)
    {
        if (RootBox == null)
            return false;

        foreach (var (element, original) in updates)
        {
            var box = element.LayoutBox ?? element.Box;
            if (box == null || element.Style == null ||
                element.Children.Count != 0 || box.Children.Count != 0 ||
                !StyleResolver.TryResolveElement(element, document, 800) ||
                element.Style is not { } style ||
                !SameNullable(original.Left, style.Left) ||
                !SameNullable(original.Top, style.Top) ||
                !SameNullable(original.Width, style.Width) ||
                !SameNullable(original.Height, style.Height) ||
                original.Display != style.Display ||
                original.Position != style.Position ||
                original.Float != style.Float ||
                original.Clear != style.Clear ||
                original.Visibility != style.Visibility ||
                original.ZIndex != style.ZIndex ||
                original.Overflow != style.Overflow ||
                original.HasGeneratedBefore || style.GeneratedBefore != null ||
                original.HasGeneratedAfter || style.GeneratedAfter != null)
                return false;
        }

        Log.Rerenders++;
        return true;
    }
    public bool TryUpdateFixedWidthText(
        DomElement element, DomText textNode, string newText, bool awaitingPromotion)
    {
        if (RootBox is not { } rootBox ||
            element.Style is not { TextTransform: TextTransform.None } ||
            element.Children.Count != 1 ||
            !ReferenceEquals(element.Children[0], textNode) ||
            textNode.Data.Length != newText.Length ||
            newText.Length == 0 ||
            textNode.Data.Any(character => character < '!' || character > '~') ||
            newText.Any(character => character < '!' || character > '~') ||
            !element.Style!.FontFamily.Any(family =>
                family.Contains("mono", StringComparison.OrdinalIgnoreCase) ||
                family.Contains("courier", StringComparison.OrdinalIgnoreCase)))
            return false;

        bool promoted = element.IsCompositedLayer;
        for (DomNode? node = element; node != null; node = node.Parent)
            if (node is DomElement ancestor && ancestor.IsCompositedLayer)
                promoted = true;
        promoted |= awaitingPromotion;

        var textBoxes = EnumerateLayoutBoxes(rootBox)
            .Where(box => ReferenceEquals(box.Element, element) && box.TextRun != null)
            .ToArray();
        if (textBoxes.Length != 1 || textBoxes[0].TextRun != textNode.Data)
            return false;
        textBoxes[0].TextRun = newText;
        if (promoted)
            Log.CompositedTextUpdates++;
        else
            element.IsCompositedLayer = true;
        Log.Rerenders++;
        return true;
    }
    private static DomElement? FindCompositedPaintRoot(LayoutBox box)
    {
        for (var parent = box.Parent; parent != null; parent = parent.Parent)
            if (parent.Element?.Style is
                { Position: PositionValue.Relative, Overflow: OverflowValue.Hidden } style)
                return parent.Element;
        return null;
    }
    private static LayoutBox FindAbsoluteContainingBlock(LayoutBox box)
    {
        var current = box.Parent;
        LayoutBox root = box;
        while (current != null)
        {
            root = current;
            if (current.Element?.Style?.Position is
                PositionValue.Relative or PositionValue.Absolute or PositionValue.Fixed)
                return current;
            current = current.Parent;
        }
        return root;
    }
    private static IEnumerable<LayoutBox> EnumerateLayoutBoxes(LayoutBox root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in EnumerateLayoutBoxes(child))
                yield return descendant;
    }
    private static bool SameNullable(float? left, float? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue || Math.Abs(left.Value - right.GetValueOrDefault()) <= 0.01f);

    private static bool Same(float left, float right) =>
        Math.Abs(left - right) <= 0.01f;
    public void EnsureLayoutForDomRead(DomDocument document)
    {
        InlineLayout.SetFontCache(RetroTests.LayoutHarness.Fonts);
        StyleResolver.Resolve(document, 800);
        _ = LayoutEngine.BuildLayoutTree(document, 800, 600);
    }
    public void PrefetchImage(string url) { Log.PrefetchedImages.Add(url); }
    public void ClearPageSelection() { }
    public string? GetDomSelectionText() => null;

    // ── headless shell-service overrides (era defaults) ──
    public virtual void ShowAlert(string message) { Log.Alerts.Add(message); }
    public virtual bool ShowConfirm(string message) { Log.Confirms.Add(message); return true; }
    public virtual string? ShowPrompt(string message, string defaultValue)
        { Log.Confirms.Add("prompt:" + message); return defaultValue; }
    public virtual void CloseHostWindow() { Log.Closes++; }
    public virtual Size GetOuterSize() => new(800, 600);
    public virtual void GetScreenMetrics(out int width, out int height, out int depth)
        { width = 800; height = 600; depth = 8; }
}
