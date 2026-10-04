// Headless stand-in for the WinForms BrowserCanvas: every shell hook the
// JS bindings touch, recording what scripts did so the tests can assert
// on it.  Lives in the Retro96 namespace so DomBindings binds to THIS
// class instead of the real canvas (which needs a Windows message loop).
// A PLAIN class — the canvas services DomBindings needs are virtual
// methods (ShowAlert / ShowConfirm / ShowPrompt / CloseHostWindow /
// GetOuterSize / GetScreenMetrics), so the script layer compiles and runs
// on any framework; the real canvas overrides them with WinForms UI.
using Retro96.Drawing;
using Retro96.Engine.Dom;

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
    }

    public Recorded Log { get; } = new();

    public event Action<string>? NavigateRequested;
    public event Action<string>? NewWindowRequested;

    public void NavigateTo(string url) { Log.Navigations.Add(url); NavigateRequested?.Invoke(url); }
    public void NavigateToReplace(string url) { Log.Navigations.Add(url); NavigateRequested?.Invoke(url); }
    public void OpenNewWindow(string url) { Log.Navigations.Add("[window] " + url); NewWindowRequested?.Invoke(url); }
    public void ScrollTo(int x, int y) { }
    public void ScrollBy(int dx, int dy) { }
    public void ClearPageSelection() { }
    public string? GetDomSelectionText() => null;
    public bool IsHandleCreated { get; } = true;
    public bool IsDisposed { get; private set; }
    public void Dispose() => IsDisposed = true;
    public bool InvokeRequired { get; } = false;
    public IAsyncResult BeginInvoke(Delegate method) { method.DynamicInvoke(); return null!; }
    public IAsyncResult BeginInvoke(Action method) { method(); return null!; }
    public object EndInvoke(IAsyncResult result) => null!;
    public Size GetViewportSize() => new(800, 600);
    public void SubmitForm(DomElement? form, object? clickCoords, bool dispatchSubmitEvent = true) { }
    public void UpdateDocumentTitle(string title) { }
    public void RequestRerender() { Log.Rerenders++; }
    public void ReflowDocument() { Log.Reflows++; }
    public void PrefetchImage(string url) { Log.PrefetchedImages.Add(url); }

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
