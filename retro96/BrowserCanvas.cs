namespace Retro96;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Retro96.Drawing;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;
using LayoutEngineApi = Retro96.Engine.Layout.LayoutEngine;

/// <summary>
/// Rendering surface + input.  Clicks dispatch on mouse-UP (browser
/// style); Enter/Tab/arrows claimed via IsInputKey so KeyDown fires;
/// double-click selects a word; text fields support a caret, drag
/// selection and Ctrl+A/C/V/X; buttons get a Win95 press-in bevel while
/// held; wheel scrolling is immediate; releasing the mouse OFF a pressed
/// button cancels activation (browser behaviour).
/// </summary>
public class BrowserCanvas : Control
{
    private DomDocument? _document;
    private LayoutBox? _rootBox;
    private Bitmap? _renderedBitmap;
    private JsInterpreter? _jsInterpreter;
    private ImageCache? _imageCache;
    private FontCache? _fontCache;
    private ResourceLoader? _resourceLoader;

    private readonly VScrollBar _vScroll = new();
    private readonly HScrollBar _hScroll = new();
    private PointF _scrollOffset = PointF.Empty;

    private readonly Timer _animationTimer = new() { Interval = 100 };
    private readonly Timer _blinkTimer = new() { Interval = 500 };
    private readonly Timer _jsTimer = new() { Interval = 50 };
    private readonly Timer _resizeReflowTimer = new() { Interval = 150 };
    private readonly List<ContextMenuStrip> _contextMenus = new();

    private bool _blinkVisible = true;
    private DomElement? _lastHoveredElement;

    // Focused form control (text fields AND tab-cycled buttons/selects).
    private DomElement? _focusedInput;

    // Value snapshot at focus time — drives the onchange-on-blur contract.
    private string? _fieldValueAtFocus;

    // In-field editing: caret + selection (character indices into the field text)
    private int _fieldCaret;
    private int _fieldSelAnchor;
    private bool _fieldDragging;
    private float _fieldScrollX;

    // Pressed button (Win95 bevel animation)
    private DomElement? _pressedControl;

    // Page text selection
    private LayoutBox? _selAnchor, _selFocus;
    private LayoutBox? _pendingSelectionAnchor;
    private System.Drawing.Point _selStart;
    private bool _selecting;
    private bool _dragMoved;
    private bool _suppressNextMouseUp;
    private LayoutBox? _lastTextClickBox;
    private System.Drawing.Point _lastTextClickPoint;
    private long _lastTextClickTicks;
    private int _textClickCount;

    private DomElement? _contextElement;

    private readonly Dictionary<LayoutBox, FrameView> _frames = new();
    private LayoutBox? _focusedFrame;
    private bool _showBoxOutlines;

    // Cached content flags (one DOM pass per render instead of three).
    private bool _hasAnimatedImages;
    private bool _hasMarquee;

    // Last viewport the layout tree was actually built at — skips
    // redundant rebuilds when the resize debounce fires with no change.
    private int _lastLayoutWidth = -1;
    private int _lastLayoutHeight = -1;

    // Status-bar text dedupe (stop event spam 20×/sec + every mouse move).
    private string _lastStatus = "";

    // Textarea line-break cache — the blink timer repaints every 500 ms
    // and caret moves re-query; re-breaking a 4 KB textarea each time
    // was measurable.
    private string? _taCacheText;
    private Font? _taCacheFont;
    private float _taCacheWidth;
    private bool _taCacheWrapOff;
    private List<(int Start, int End)>? _taCacheLines;

    // Skia screen composer + the single WinForms hand-off (ShellPaint.cs)
    private readonly ScreenComposer _painter = new();

    private sealed class ControlDefault
    {
        public string? Value;
        public bool Checked;
        public string? TextAreaText;
        public List<DomElement> SelectedOptions = new();
    }
    private readonly Dictionary<DomElement, ControlDefault> _controlDefaults = new();

    public event Action<string>? NavigateRequested;
    public event Action<string>? StatusChanged;
    public event Action? BackRequested;
    public event Action? ForwardRequested;
    public event Action? ReloadRequested;
    public event Action? PageChanged;

    public LayoutBox? RootBox => _rootBox;
    public DomDocument? PageDocument => _document;
    public bool ShowBoxOutlines => _showBoxOutlines;
    public ResourceLoader? ResourceLoader => _resourceLoader;

    public sealed class FrameView
    {
        public required DomDocument Document;
        public required LayoutBox RootBox;
        public Bitmap? Rendered;
        public PointF Scroll = PointF.Empty;
        public string Name = "";
        public string Url = "";
        public bool ScrollingEnabled = true;

        /// <summary>
        /// Per-frame JS interpreter (frames carry their own scripting
        /// context — timers scheduled by frame scripts are driven by the
        /// canvas's JS timer tick alongside the page's own interpreter).
        /// </summary>
        public Retro96.Engine.Js.JsInterpreter? Interpreter;

        /// <summary>
        /// Frames/iframes nested INSIDE this frame's document.  Their boxes
        /// live in this frame's own coordinate space, so they compose into
        /// this frame's bitmap (not the page-level frame map) — otherwise a
        /// nested iframe would blit at page coordinates and land on top of
        /// a sibling frame.
        /// </summary>
        public List<(LayoutBox Box, FrameView View)> ChildFrames = new();
    }

    public BrowserCanvas()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();
        TabStop = true;

        _vScroll.Dock = DockStyle.Right;
        _vScroll.Scroll += OnVScroll;
        _vScroll.TabStop = false;
        Controls.Add(_vScroll);

        _hScroll.Dock = DockStyle.Bottom;
        _hScroll.Scroll += OnHScroll;
        _hScroll.TabStop = false;
        Controls.Add(_hScroll);

        _animationTimer.Tick += OnAnimationTick;
        _blinkTimer.Tick += OnBlinkTick;
        _jsTimer.Tick += OnJsTimerTick;
        _resizeReflowTimer.Tick += OnResizeReflowTick;
        _caretTimer.Tick += OnCaretTick;
        _caretTimer.Start();

        KeyDown += OnDevToolsKeyDown;
        KeyPress += OnCanvasKeyPress;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _blinkTimer.Dispose();
            _jsTimer.Dispose();
            _resizeReflowTimer.Dispose();
            _caretTimer.Dispose();
            _measureGfx?.Dispose();
            _measureBmp?.Dispose();
            _painter.Dispose();
            _renderedBitmap?.Dispose();
            _renderedBitmap = null;

            foreach (var frame in _frames.Values)
                DisposeFrameView(frame);
            _frames.Clear();

            foreach (var menu in _contextMenus.ToArray())
            {
                try { menu.Dispose(); } catch { /* already torn down */ }
            }
            _contextMenus.Clear();

            _controlDefaults.Clear();
            _focusedInput = null;
            _lastHoveredElement = null;
            _taCacheLines = null;
            _taCacheText = null;
            _taCacheFont = null;
        }
        base.Dispose(disposing);
    }

    // ── CRITICAL: without this override WinForms treats Enter / Tab /
    // arrows as form-navigation keys and NEVER delivers KeyDown for them
    // — Enter-to-submit and arrow-key caret movement were dead on arrival.
    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.Enter or Keys.Tab or Keys.Escape
                     or Keys.Up or Keys.Down or Keys.Left or Keys.Right
                     or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
            return true;
        return base.IsInputKey(keyData);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    public void SetResourceLoader(ResourceLoader loader) =>
        _resourceLoader = loader ?? throw new ArgumentNullException(nameof(loader));

    public void SetPage(DomDocument doc, LayoutBox rootBox,
                        JsInterpreter js, FontCache fontCache, ImageCache images)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(rootBox);

        // FIX: nested child-frame bitmaps were leaked here (only the
        // top frame's Rendered was disposed).
        foreach (var frame in _frames.Values)
            DisposeFrameView(frame);
        _frames.Clear();
        _focusedFrame = null;

        // Focus is dropped silently on navigation — running the old page's
        // blur/change handlers from inside SetPage re-enters navigation.
        _focusedInput = null;
        _fieldDragging = false;
        _fieldValueAtFocus = null;
        _pressedControl = null;
        _controlDefaults.Clear();
        _contextElement = null;

        _selAnchor = _selFocus = null;
        _selecting = false;

        _document = doc;
        _rootBox = rootBox;
        _jsInterpreter = js;
        _fontCache = fontCache;
        _imageCache = images;

        _scrollOffset = PointF.Empty;
        _lastHoveredElement = null;

        _renderedBitmap?.Dispose();
        _renderedBitmap = null;

        _taCacheText = null;
        _taCacheLines = null;
        _taCacheFont = null;

        // Page-level animation/blink timers stop until the next render
        // re-evaluates them (they used to keep ticking against the OLD page).
        _animationTimer.Stop();
        _blinkTimer.Stop();

        _lastStatus = "";
        PageChanged?.Invoke();
    }

    public void ApplyRelayout(DomDocument document, LayoutBox newRoot)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (newRoot == null) return;

        _document = document;
        RemapFramesToNewRoot(newRoot);
        _rootBox = newRoot;

        // Image-load reflow rebuilds the tree — remap the selection onto
        // the new boxes instead of clearing it.
        RemapSelection(newRoot);

        PageChanged?.Invoke();
        RequestRerender();
    }

    /// <summary>
    /// Re-resolves styles and rebuilds the layout tree for the CURRENT
    /// document — the script-driven DOM-mutation path (element.style
    /// writes, display toggling).  A plain repaint is not enough: hiding
    /// or showing a block changes the flow, so the whole tree must be
    /// rebuilt and re-applied.
    /// </summary>
    public void ReflowDocument()
    {
        if (_document == null || _rootBox == null) return;

        var vp = GetViewportSize();
        if (vp.Width <= 0 || vp.Height <= 0) return;

        try
        {
            Engine.Css.StyleResolver.Resolve(_document, vp.Width);
        }
        catch { /* keep the previous computed styles */ }

        var newRoot = LayoutEngineApi.BuildLayoutTree(_document, vp.Width, vp.Height);
        _lastLayoutWidth = vp.Width;
        _lastLayoutHeight = vp.Height;
        ApplyRelayout(_document, newRoot);
    }

    public bool ApplyEditedSource(string markup)
    {
        if (_document?.BaseUrl == null || _jsInterpreter == null || _fontCache == null || _imageCache == null)
            return false;

        try
        {
            var document = Engine.Html.HtmlParser.Parse(markup ?? "", _document.BaseUrl, _document.Cookies);
            var viewport = GetViewportSize();
            Engine.Css.StyleResolver.Resolve(document, viewport.Width);
            var root = LayoutEngineApi.BuildLayoutTree(document, viewport.Width, viewport.Height);
            SetPage(document, root, _jsInterpreter, _fontCache, _imageCache);
            RequestRerender();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Shell services the JS bindings need.  Virtual so the headless test
    // harness can stub them (a plain canvas with era defaults) — DomBindings
    // itself carries no WinForms types, which is what lets the engine's
    // script layer compile and run anywhere.
    // ─────────────────────────────────────────────────────────────────────

    public virtual void ShowAlert(string message)
        => MessageBox.Show(FindForm(), message, "Retro96", MessageBoxButtons.OK);

    public virtual bool ShowConfirm(string message)
        => MessageBox.Show(FindForm(), message, "Retro96", MessageBoxButtons.YesNo) == DialogResult.Yes;

    public virtual string? ShowPrompt(string message, string defaultValue)
        => PromptDialog.Show(FindForm(), message, defaultValue);

    public virtual void CloseHostWindow()
        => FindForm()?.Close();

    public virtual System.Drawing.Size GetOuterSize()
        => FindForm()?.Size ?? System.Drawing.Size.Empty;

    public virtual void GetScreenMetrics(out int width, out int height, out int depth)
    {
        width = 800; height = 600; depth = 8;
        try
        {
            var scr = Screen.FromControl(this);
            width = scr.Bounds.Width;
            height = scr.Bounds.Height;
            depth = scr.BitsPerPixel;
        }
        catch { /* headless / no screen handle — era defaults */ }
    }

    /// <summary>
    /// Modal JavaScript prompt dialog (era-style): label, input line,
    /// OK/Cancel.  Cancel (or closing the dialog) returns null — the
    /// JS-visible "user hit Cancel" of the era.
    /// </summary>
    private static class PromptDialog
    {
        public static string? Show(IWin32Window? owner, string message, string defaultValue)
        {
            string? result = null;

            // Geometry comes from the engine-level contract so the
            // "input at least 200x20" bug can be regression-tested
            // headlessly (see Engine/Js/JsDialogGeometry.cs).
            var layout = Engine.Js.JsDialogGeometry.PromptLayout(message, defaultValue);

            using var form = new Form
            {
                Text = "Retro96 — JavaScript prompt",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
                // FIX: every other window in this app (Form1) explicitly sets
                // AutoScaleMode.None because control positions are placed by
                // hand for pixel-accurate era layout, not through the
                // designer's scaling machinery. This dialog is built the
                // same way (bare `new Form`, manual Bounds on each control)
                // but never opted out of the default AutoScaleMode.Font, so
                // WinForms had no recorded baseline to scale FROM and still
                // tried to apply a DPI/font scale factor to controls that
                // were never meant to be scaled — squashing the label into
                // the input box and pushing OK/Cancel past the bottom edge
                // on any machine running above 100% scaling.
                AutoScaleMode = AutoScaleMode.None,
                ClientSize = new System.Drawing.Size(
                    (int)layout.ClientSize.Width, (int)layout.ClientSize.Height),
                Font = new System.Drawing.Font("Segoe UI", 10f),
                ShowInTaskbar = false
            };

            var label = new Label
            {
                Text = message,
                AutoSize = false,
                Bounds = new System.Drawing.Rectangle(
                    (int)layout.Label.X, (int)layout.Label.Y,
                    (int)layout.Label.Width, (int)layout.Label.Height)
            };
            var box = new TextBox
            {
                Text = defaultValue,
                AutoSize = false,
                Bounds = new System.Drawing.Rectangle(
                    (int)layout.Input.X, (int)layout.Input.Y,
                    (int)layout.Input.Width, (int)layout.Input.Height)
            };
            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Bounds = new System.Drawing.Rectangle(
                    (int)layout.Ok.X, (int)layout.Ok.Y,
                    (int)layout.Ok.Width, (int)layout.Ok.Height)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Bounds = new System.Drawing.Rectangle(
                    (int)layout.Cancel.X, (int)layout.Cancel.Y,
                    (int)layout.Cancel.Width, (int)layout.Cancel.Height)
            };

            form.Controls.AddRange(new Control[] { label, box, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            if (form.ShowDialog(owner) == DialogResult.OK)
                result = box.Text;

            return result;
        }
    }

    private void RemapSelection(LayoutBox newRoot)
    {
        if (_selAnchor == null && _selFocus == null) return;
        _selAnchor = FindMatchingBox(newRoot, _selAnchor);
        _selFocus = FindMatchingBox(newRoot, _selFocus);
        if (_selAnchor == null || _selFocus == null)
            _selAnchor = _selFocus = null;
    }

    private static LayoutBox? FindMatchingBox(LayoutBox root, LayoutBox? old)
    {
        if (old?.Element == null) return null;
        string text = old.TextRun ?? "";
        foreach (var b in root.Descendants())
            if (ReferenceEquals(b.Element, old.Element) && b.TextRun == text)
                return b;
        return null;
    }

    private void RemapFramesToNewRoot(LayoutBox newRoot)
    {
        if (_frames.Count == 0) return;

        var newBoxes = newRoot.Descendants()
            .Where(b => b.BoxType == BoxType.Frame && b.Element != null)
            .ToList();

        var focusedElem = _focusedFrame?.Element;

        var remapped = new Dictionary<LayoutBox, FrameView>();
        var dropped = new List<FrameView>();
        foreach (var kvp in _frames)
        {
            var newBox = newBoxes.FirstOrDefault(b => b.Element == kvp.Key.Element);
            var view = kvp.Value;
            if (newBox == null)
            {
                dropped.Add(view);
                continue;
            }
            // FIX: building a layout tree at 0×0 threw / produced garbage.
            if (newBox.Width > 0 && newBox.Height > 0)
                view.RootBox = LayoutEngineApi.BuildLayoutTree(view.Document, newBox.Width, newBox.Height);
            RemapChildFrames(view);
            remapped[newBox] = view;
        }

        // FIX: frames that vanished from the new layout leaked their bitmaps.
        foreach (var view in dropped)
            DisposeFrameView(view);

        _frames.Clear();
        foreach (var kvp in remapped)
            _frames[kvp.Key] = kvp.Value;

        _focusedFrame = focusedElem != null
            ? newBoxes.FirstOrDefault(b => b.Element == focusedElem)
            : null;
    }

    /// <summary>
    /// After a relayout rebuilds a frame's own layout tree, the stored
    /// nested-frame boxes are stale — re-find each child frame's CURRENT
    /// box by element so the composition rect follows the new layout.
    /// </summary>
    private static void RemapChildFrames(FrameView view)
    {
        if (view.ChildFrames.Count == 0 || view.RootBox == null) return;

        var remapped = new List<(LayoutBox, FrameView)>();
        foreach (var (oldBox, childView) in view.ChildFrames)
        {
            var el = oldBox.Element;
            var newBox = el == null ? null
                : view.RootBox.Descendants()
                    .FirstOrDefault(b => b.BoxType == BoxType.Frame && b.Element == el);
            if (newBox != null)
                remapped.Add((newBox, childView));
            else
                DisposeFrameView(childView);   // FIX: dropped children leaked
        }
        view.ChildFrames = remapped;
    }

    public LayoutBox? FindFrameBox(DomElement frameElem)
    {
        if (_rootBox == null) return null;
        foreach (var b in _rootBox.Descendants())
            if (b.BoxType == BoxType.Frame && b.Element == frameElem)
                return b;
        return null;
    }

    public void SetFrame(LayoutBox frameBox, FrameView view)
    {
        // FIX: replacing a frame leaked the previous view's bitmaps.
        if (_frames.TryGetValue(frameBox, out var old) && !ReferenceEquals(old, view))
            DisposeFrameView(old);
        _frames[frameBox] = view;
        RenderFrameBitmap(frameBox, view);
        CheckAndStartTimers();
        Invalidate();
    }

    public bool HasFrames => _frames.Count > 0;

    public IEnumerable<(LayoutBox Box, FrameView View)> Frames
    {
        get
        {
            foreach (var kvp in _frames)
                yield return (kvp.Key, kvp.Value);
        }
    }

    public System.Drawing.Size GetViewportSize()
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        if (_vScroll.Visible) w = Math.Max(0, w - _vScroll.Width);
        if (_hScroll.Visible) h = Math.Max(0, h - _hScroll.Height);
        return new System.Drawing.Size(w, h);
    }

    public void SetBoxOutlines(bool show)
    {
        _showBoxOutlines = show;
        RequestRerender();
    }

    public void ClearForNavigation()
    {
        _renderedBitmap?.Dispose();
        _renderedBitmap = null;
        _focusedInput = null;
        _fieldDragging = false;
        _fieldValueAtFocus = null;
        _pressedControl = null;
        _selAnchor = _selFocus = null;
        _pendingSelectionAnchor = null;
        _selecting = false;
        _scrollOffset = PointF.Empty;
        UpdateScrollBars();
        Invalidate();
    }

    public void ReRenderPage(FontCache fontCache, ImageCache imageCache,
                             ResourceLoader resourceLoader)
    {
        if (_rootBox == null || _document == null) return;

        try
        {
            // Render to the actual viewport, excluding visible scrollbars.
            // Using ClientSize here made the bitmap wider than the viewport
            // as soon as the vertical scrollbar appeared, which then forced
            // an unnecessary horizontal scrollbar.
            var viewport = GetViewportSize();
            int rw = Math.Max(1, viewport.Width);
            int rh = Math.Max(1, viewport.Height);

            var renderer = new Renderer(fontCache, imageCache, resourceLoader)
            {
                PressedElement = _pressedControl
            };

            var newBitmap = renderer.Render(
                _rootBox, _document,
                fontCache, imageCache,
                rw, rh,
                0f, 0f,
                _lastHoveredElement,
                _blinkVisible,
                _showBoxOutlines,
                _focusedInput);

            _renderedBitmap?.Dispose();
            _renderedBitmap = newBitmap;
        }
        catch (Exception ex)
        {
            // A paint-time exception used to escape into the message loop
            // with the bitmap already disposed/nulled — flat silver canvas,
            // "completely blank page". Keep the last good bitmap and log.
            Retro96.DebugLog.WriteException("ReRenderPage", ex);
        }

        foreach (var (box, view) in _frames)
            RenderFrameBitmap(box, view);

        UpdateScrollBars();
        CheckAndStartTimers();
        Invalidate();
    }

    private void RenderFrameBitmap(LayoutBox frameBox, FrameView view, int depth = 0)
    {
        if (_fontCache == null || _imageCache == null || _resourceLoader == null)
            return;
        if (depth > 4)   // FIX: cycle guard against pathological frame nesting
            return;

        try
        {
            var renderer = new Renderer(_fontCache, _imageCache, _resourceLoader);
            var bmp = renderer.Render(
                view.RootBox, view.Document,
                _fontCache, _imageCache,
                frameBox.Width, frameBox.Height,
                0f, 0f,
                _lastHoveredElement,
                _blinkVisible);

            view.Rendered?.Dispose();
            view.Rendered = bmp;
        }
        catch (Exception ex)
        {
            // FIX: a frame render exception used to abort the whole render
            // pass, skipping UpdateScrollBars/Invalidate — dead canvas.
            Retro96.DebugLog.WriteException("RenderFrameBitmap", ex);
            // keep the previous bitmap
        }

        foreach (var (childBox, childView) in view.ChildFrames)
        {
            RenderFrameBitmap(childBox, childView, depth + 1);
            ComposeChildIntoParent(view, childBox, childView);
        }
    }

    /// <summary>Composes a nested frame's bitmap into its parent frame's
    /// bitmap at the nested frame's (document-local) rect.</summary>
    private static void ComposeChildIntoParent(FrameView parent,
                                               LayoutBox childBox, FrameView childView)
    {
        if (parent.Rendered == null || childView.Rendered == null) return;

        var r = childBox.BorderRect;
        float w = Math.Min(r.Width, childView.Rendered.Width);
        float h = Math.Min(r.Height, childView.Rendered.Height);
        if (w <= 0 || h <= 0) return;

        using var g = Graphics.FromImage(parent.Rendered);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;   // pixel-exact, no bilinear bleed
        var state = g.Save();
        g.SetClip(new RectangleF(r.X, r.Y, w, h));
        g.DrawImage(childView.Rendered,
            new RectangleF(r.X, r.Y, w, h),
            new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
        g.Restore(state);
    }

    /// <summary>
    /// Registers a frame nested inside another frame's document: renders
    /// the child and composes it into the parent's bitmap, then repaints.
    /// </summary>
    public void AddChildFrame(FrameView parent, LayoutBox childBox, FrameView childView)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(childBox);
        ArgumentNullException.ThrowIfNull(childView);

        // FIX: registering the same child twice duplicated the entry.
        for (int i = 0; i < parent.ChildFrames.Count; i++)
        {
            if (ReferenceEquals(parent.ChildFrames[i].View, childView))
            {
                parent.ChildFrames.RemoveAt(i);
                break;
            }
        }
        parent.ChildFrames.Add((childBox, childView));

        RenderFrameBitmap(childBox, childView);
        // FIX: compose directly — nested-of-nested parents (not in _frames)
        // never got composed at all before.
        ComposeChildIntoParent(parent, childBox, childView);

        // Recompose the whole parent so earlier children stay painted too.
        if (_frames.Count > 0)
        {
            foreach (var (box, view) in _frames)
                if (ReferenceEquals(view, parent))
                { RenderFrameBitmap(box, view); break; }
        }
        CheckAndStartTimers();
        Invalidate();
    }

    /// <summary>Re-renders a child frame and recomposes it into its parent
    /// (used after image-load reflow of the child's document).</summary>
    public void RefreshChildFrame(FrameView parent, LayoutBox childBox, FrameView childView)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(childBox);
        ArgumentNullException.ThrowIfNull(childView);
        RenderFrameBitmap(childBox, childView);
        ComposeChildIntoParent(parent, childBox, childView);
        Invalidate();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Painting
    // ─────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        int vw = Math.Max(1, ClientSize.Width);
        int vh = Math.Max(1, ClientSize.Height);

        // Compose the whole frame on the Skia screen surface; ShellPaint
        // hands the finished composite to WinForms through the single
        // GDI blit.  The surface is REUSED across paints, so it must be
        // cleared every time — a short page leaves stale pixels at the
        // right/bottom otherwise.
        var g = _painter.Begin(vw, vh);
        g.Clear(Color.FromArgb(0xC0, 0xC0, 0xC0));

        // Whole-document blit at the scrolled position.  Pixel-exact:
        // nearest sampling + integer offsets, matching the old direct-GDI
        // blit (it truncated the scroll offset the same way), so a
        // scroll notch never smears glyphs.
        // FIX: a failed page render (null bitmap) used to skip frames and
        // the selection overlay entirely — now only the main blit is skipped.
        if (_renderedBitmap != null)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            int blitX = Math.Max(0, (int)_scrollOffset.X);
            int blitY = Math.Max(0, (int)_scrollOffset.Y);
            g.DrawImage(_renderedBitmap, -blitX, -blitY);
        }

        foreach (var (box, view) in _frames)
        {
            if (view.Rendered == null) continue;

            var destRect = new RectangleF(
                box.X - _scrollOffset.X,
                box.Y - _scrollOffset.Y,
                box.Width, box.Height);

            float sw = Math.Min(destRect.Width, view.Rendered.Width - view.Scroll.X);
            float sh = Math.Min(destRect.Height, view.Rendered.Height - view.Scroll.Y);
            if (sw > 0 && sh > 0)
            {
                var state = g.Save();
                g.SetClip(destRect);
                g.DrawImage(view.Rendered,
                    new RectangleF(destRect.X, destRect.Y, sw, sh),
                    new RectangleF(view.Scroll.X, view.Scroll.Y, sw, sh),
                    GraphicsUnit.Pixel);
                g.Restore(state);
            }

            if (_focusedFrame == box)
            {
                using var focusPen = new Pen(Color.FromArgb(0x00, 0x00, 0x80), 1);
                g.DrawRectangle(focusPen, destRect.X, destRect.Y,
                    destRect.Width - 1, destRect.Height - 1);
            }
        }

        // Page text selection highlight — one CONTIGUOUS rectangle per
        // line, spanning from the first selected box to the last.
        if (_selAnchor != null && _selFocus != null)
        {
            using var selBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 170));

            // FIX: SelectedTextBoxes() yields boxes in DOM/descendant order,
            // not visual order. That's the same thing for plain paragraph
            // text, but breaks down for content built from many small
            // nested inline boxes on the same line — e.g. per-token
            // <span>-wrapped syntax highlighting inside a <pre> block, or
            // any wrapped multi-line text where a nested element's children
            // aren't enumerated in strict left-to-right/top-to-bottom
            // reading order. When box order doesn't match screen order, the
            // same-line grouping below (which just compares each box's Y to
            // the PREVIOUS box's Y) sees top jump around, splits one visual
            // line into several disjoint spans, and paints a broken-up
            // highlight instead of one clean bar per line. Sorting into
            // actual visual order first — row by Y (banded, not exact, so
            // boxes on the same line with tiny baseline/metric differences
            // still group together), then column by X within a row — makes
            // the merge below see a proper reading-order sequence
            // regardless of how the underlying DOM/box tree is structured.
            var selBoxes = SelectedTextBoxes()
                .Where(b => b.Width > 0f && b.Height > 0f)
                .OrderBy(b => (int)(b.Y / 4f))   // coarse row bucket first
                .ThenBy(b => b.X)                // then left-to-right within the row
                .ToList();

            float spanL = float.MinValue, spanR = float.MinValue;
            float spanTop = 0f, spanBot = 0f;

            void FlushSpan()
            {
                if (spanR > spanL)
                    g.FillRectangle(selBrush,
                        spanL - _scrollOffset.X, spanTop - _scrollOffset.Y,
                        spanR - spanL, Math.Max(1f, spanBot - spanTop));
                spanL = spanR = float.MinValue;
            }

            foreach (var b in selBoxes)
            {
                float l = b.X, r = b.X + b.Width;
                float top = b.Y, bot = b.Y + b.Height;

                bool sameLine =
                    spanR > spanL &&                       // an open span exists
                    Math.Abs(top - spanTop) < 4f;          // same text row

                if (!sameLine)
                {
                    FlushSpan();
                    spanL = l; spanR = r; spanTop = top; spanBot = bot;
                }
                else
                {
                    spanR = Math.Max(spanR, r);
                    spanTop = Math.Min(spanTop, top);
                    spanBot = Math.Max(spanBot, bot);
                }
            }
            FlushSpan();
        }

        // Field caret / in-field selection overlay
        if (_focusedInput != null && _rootBox != null)
            PaintFieldOverlay(g);

        _painter.Blit(e.Graphics);
    }

    private void PaintFieldOverlay(Graphics g)
    {
        // FIX: a tab-focused select/button used to get a TEXT caret drawn
        // over it — the overlay is only for editable fields.
        var el = _focusedInput;
        if (el == null || _rootBox == null || !IsEditableField(el)) return;

        var box = FindBoxForElement(_rootBox, el);
        if (box == null || _fontCache == null) return;

        if (el.TagName == "textarea")
        {
            PaintTextareaFieldOverlay(g);
            return;
        }

        var face = box.ContentRect;
        string text = GetFieldText(el);
        bool password = el.GetAttrOrDefault("type", "text") == "password";
        if (password) text = new string('*', text.Length);

        // Same font + same GenericTypographic format the renderer draws with,
        // so caret and selection sit on the actual glyph edges.
        var font = ResolveFieldFont(el);
        if (font == null) return;
        using var fmt = NewFieldFormat(noWrap: true);

        float MeasureTo(int n) => n <= 0 ? 0f
            : g.MeasureString(text[..Math.Min(n, text.Length)], font, int.MaxValue, fmt).Width;

        float lineH = font.GetHeight(g);
        float top = face.Y + Math.Max(1f, (face.Height - lineH) / 2f) - _scrollOffset.Y;
        float bottom = Math.Min(top + lineH, face.Bottom - 1 - _scrollOffset.Y);

        int caret = Math.Clamp(_fieldCaret, 0, text.Length);
        int anchor = Math.Clamp(_fieldSelAnchor, 0, text.Length);
        int selStart = Math.Min(anchor, caret);
        int selEnd = Math.Max(anchor, caret);
        float fieldScroll = EnsureSingleLineCaretVisible(g, text, font, face, caret);
        float textX = face.X + 3 - _scrollOffset.X
                - fieldScroll;

        var oldClip = g.Save();
        g.SetClip(new RectangleF(face.X + 2 - _scrollOffset.X, face.Y - _scrollOffset.Y,
                                 Math.Max(1, face.Width - 4), face.Height),
                  CombineMode.Intersect);

        // Renderer paints the unfocused value from the left. Once the caret
        // scrolls right, repaint the focused value with the same offset so
        // dragging can reveal the hidden suffix instead of leaving stale text
        // underneath the selection overlay.
        var style = el.Style;
        Color fieldBackground = style != null && style.OwnBackground && style.BackgroundColor != Color.Transparent
            ? style.BackgroundColor : Color.White;
        Color fieldForeground = style != null && style.OwnColor ? style.Color : Color.Black;
        using (var background = new SolidBrush(fieldBackground))
            g.FillRectangle(background, face.X + 2 - _scrollOffset.X,
                face.Y - _scrollOffset.Y, Math.Max(1, face.Width - 4), face.Height);
        using var focusedTextFormat = NewFieldFormat(noWrap: true);
        focusedTextFormat.LineAlignment = StringAlignment.Center;
        using (var foreground = new SolidBrush(fieldForeground))
            g.DrawString(text, font, foreground,
                new RectangleF(textX, face.Y - _scrollOffset.Y,
                    Math.Max(face.Width, MeasureTo(text.Length) + 8), face.Height), focusedTextFormat);

        if (selEnd > selStart)
        {
            using var selBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
            float x1 = textX + MeasureTo(selStart);
            float x2 = textX + MeasureTo(selEnd);
            g.FillRectangle(selBrush, x1, top, Math.Max(1f, x2 - x1), bottom - top);
        }

        if ((uint)Environment.TickCount / 500 % 2 == 0)
        {
            float cx = textX + MeasureTo(caret);
            using var caretPen = new Pen(Color.Black, 1);
            g.DrawLine(caretPen, cx, top, cx, bottom);
        }
        g.Restore(oldClip);

        // Keep the focus outline attached to the control, independent of the
        // horizontal offset used for the field's text.
        using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
        g.DrawRectangle(focusPen, face.X - _scrollOffset.X, face.Y - _scrollOffset.Y,
            face.Width - 1, face.Height - 1);
    }

    private void PaintTextareaFieldOverlay(Graphics g)
    {
        var el = _focusedInput;
        if (el == null) return;
        var geo = GetTextareaGeometry(el);
        if (geo == null) return;

        var font = geo.Font;
        var face = geo.Box.ContentRect;
        string text = geo.Text;
        var lines = geo.Lines;

        float textX = face.X + 3 - _scrollOffset.X;
        float textY = face.Y + 2 - _scrollOffset.Y;
        float textBottom = face.Bottom - 2 - _scrollOffset.Y;
        float lineHeight = font.GetHeight(g);
        using var noWrap = NewFieldFormat(noWrap: true);

        float Measure(int start, int end) => end <= start ? 0f
            : g.MeasureString(text[start..end], font, int.MaxValue, noWrap).Width;

        var oldClip = g.Save();
        g.SetClip(new RectangleF(face.X + 1 - _scrollOffset.X, face.Y + 1 - _scrollOffset.Y,
                                 Math.Max(1, face.Width - 2), Math.Max(1, face.Height - 2)),
                  CombineMode.Intersect);

        int caret = Math.Clamp(_fieldCaret, 0, text.Length);
        int anchor = Math.Clamp(_fieldSelAnchor, 0, text.Length);
        int selStart = Math.Min(anchor, caret);
        int selEnd = Math.Max(anchor, caret);

        if (selEnd > selStart)
        {
            using var highlight = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
            for (int i = 0; i < lines.Count; i++)
            {
                var (ls, le) = lines[i];
                int a = Math.Max(selStart, ls), b = Math.Min(selEnd, le);
                if (b <= a) continue;
                float y = textY + i * lineHeight;
                float x1 = textX + Measure(ls, a);
                g.FillRectangle(highlight, x1, y, Math.Max(1f, Measure(a, b)), lineHeight);
            }
        }

        if ((uint)Environment.TickCount / 500 % 2 == 0)
        {
            // A caret at a wrap boundary belongs to the START of the next line;
            // a caret at a hard newline belongs to the end of its own line.
            int li = CaretLineIndex(lines, caret);
            var (cs, _) = lines[li];
            float cx = textX + Measure(cs, caret);
            float cy = textY + li * lineHeight;
            using var caretPen = new Pen(Color.Black, 1);
            g.DrawLine(caretPen, cx, cy, cx, Math.Min(cy + lineHeight, textBottom));
        }

        g.Restore(oldClip);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Scroll
    // ─────────────────────────────────────────────────────────────────────

    private void UpdateScrollBars()
    {
        if (_renderedBitmap == null)
        {
            _vScroll.Visible = false;
            _hScroll.Visible = false;
            _scrollOffset = PointF.Empty;
            return;
        }

        int docW = _renderedBitmap.Width;
        int docH = _renderedBitmap.Height;
        bool wasVVisible = _vScroll.Visible;
        bool wasHVisible = _hScroll.Visible;

        // Work in the same coordinate system as painting: the viewport is
        // the client area minus whichever scrollbars are actually visible.
        // The old code exposed the document size directly as ScrollBar.Maximum
        // and then manually treated Maximum-LargeChange as the usable range.
        // WinForms already applies that page-size semantics internally, and
        // the two ranges could drift apart (most visibly on long pages: the
        // thumb would stop before the document's real bottom while the canvas
        // still had content below it).
        int vpW = Math.Max(0, ClientSize.Width);
        int vpH = Math.Max(0, ClientSize.Height);

        bool needV = docH > vpH;
        if (needV) vpW = Math.Max(0, vpW - _vScroll.Width);

        // Layout and bitmap dimensions are rounded independently. Treat a
        // one-pixel discrepancy as viewport rasterisation noise; otherwise
        // ordinary pages get a horizontal bar even though their content fits.
        bool needH = docW > vpW + 1;
        if (needH) vpH = Math.Max(0, vpH - _hScroll.Height);

        // A horizontal scrollbar reduces the viewport height and can itself
        // make a previously unnecessary vertical scrollbar necessary.
        if (!needV && docH > vpH)
        {
            needV = true;
            vpW = Math.Max(0, vpW - _vScroll.Width);
        }

        _vScroll.Visible = needV;
        _hScroll.Visible = needH;

        // Scrollbars reduce the real client viewport. Rebuild the layout at
        // that reduced width so inline text wraps beside the visible bar,
        // rather than keeping the pre-scrollbar line width.
        if ((wasVVisible != needV || wasHVisible != needH) &&
            _document != null && _rootBox != null)
        {
            _resizeReflowTimer.Stop();
            _resizeReflowTimer.Start();
        }

        float maxX = needH ? Math.Max(0, docW - vpW) : 0;
        float maxY = needV ? Math.Max(0, docH - vpH) : 0;
        _scrollOffset.X = Math.Clamp(_scrollOffset.X, 0, maxX);
        _scrollOffset.Y = Math.Clamp(_scrollOffset.Y, 0, maxY);

        if (needV)
        {
            int page = Math.Max(1, vpH);
            int maxValue = (int)Math.Min(int.MaxValue - 1L, maxY) + page - 1;
            _vScroll.Minimum = 0;
            _vScroll.LargeChange = page;
            _vScroll.SmallChange = Math.Max(1, page / 10);
            _vScroll.Maximum = Math.Max(page - 1, maxValue);
            _vScroll.Value = Math.Clamp((int)_scrollOffset.Y, 0, (int)maxY);
        }
        else
        {
            _vScroll.Minimum = 0;
            _vScroll.LargeChange = 1;
            _vScroll.SmallChange = 1;
            _vScroll.Maximum = 0;
            _vScroll.Value = 0;
        }

        if (needH)
        {
            int page = Math.Max(1, vpW);
            int maxValue = (int)Math.Min(int.MaxValue - 1L, maxX) + page - 1;
            _hScroll.Minimum = 0;
            _hScroll.LargeChange = page;
            _hScroll.SmallChange = Math.Max(1, page / 10);
            _hScroll.Maximum = Math.Max(page - 1, maxValue);
            _hScroll.Value = Math.Clamp((int)_scrollOffset.X, 0, (int)maxX);
        }
        else
        {
            _hScroll.Minimum = 0;
            _hScroll.LargeChange = 1;
            _hScroll.SmallChange = 1;
            _hScroll.Maximum = 0;
            _hScroll.Value = 0;
        }
    }

    private void OnVScroll(object? sender, ScrollEventArgs e)
    {
        CloseMenusOnScroll();
        _scrollOffset.Y = e.NewValue;
        Invalidate();
    }

    private void OnHScroll(object? sender, ScrollEventArgs e)
    {
        CloseMenusOnScroll();
        _scrollOffset.X = e.NewValue;
        Invalidate();
    }

    /// <summary>
    /// Scrolling immediately discards any open dropdown / context menu —
    /// the menu is anchored to a DOCUMENT position, so letting it float at
    /// a fixed screen spot while the page moves under it is wrong.
    /// </summary>
    private void CloseMenusOnScroll()
    {
        if (_contextMenus.Count == 0) return;
        var menus = _contextMenus.ToArray();
        _contextMenus.Clear();
        foreach (var menu in menus)
        {
            try { if (menu.Visible) menu.Close(); }
            catch { /* menu already torn down */ }
            try { menu.Dispose(); }
            catch { }
        }
    }

    public void ScrollTo(int x, int y)
    {
        if (_renderedBitmap == null) return;
        CloseMenusOnScroll();
        var vp = GetViewportSize();
        float maxX = Math.Max(0, _renderedBitmap.Width - vp.Width);
        float maxY = Math.Max(0, _renderedBitmap.Height - vp.Height);
        _scrollOffset.X = Math.Max(0, Math.Min(x, maxX));
        _scrollOffset.Y = Math.Max(0, Math.Min(y, maxY));
        if (_vScroll.Visible) _vScroll.Value = Math.Clamp((int)_scrollOffset.Y,
            _vScroll.Minimum, Math.Max(_vScroll.Minimum, _vScroll.Maximum - _vScroll.LargeChange + 1));
        if (_hScroll.Visible) _hScroll.Value = Math.Clamp((int)_scrollOffset.X,
            _hScroll.Minimum, Math.Max(_hScroll.Minimum, _hScroll.Maximum - _hScroll.LargeChange + 1));
        Invalidate();
    }

    // FIX: float overload — high-resolution wheels deliver deltas smaller
    // than 120, and integer division (e.Delta / 120) * 80 truncated them
    // to zero, i.e. no scrolling at all. Int callers still compile.
    public void ScrollBy(float dx, float dy) =>
        ScrollTo((int)(_scrollOffset.X + dx), (int)(_scrollOffset.Y + dy));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        float x = e.X + _scrollOffset.X;
        float y = e.Y + _scrollOffset.Y;

        var frameBox = FrameBoxAtPoint(x, y);
        if (frameBox != null && _frames.TryGetValue(frameBox, out var view) &&
            view.ScrollingEnabled)
        {
            CloseMenusOnScroll();
            _focusedFrame = frameBox;
            float maxY = view.Rendered != null
                ? Math.Max(0, view.Rendered.Height - frameBox.Height)
                : 0;
            view.Scroll.Y = Math.Clamp(view.Scroll.Y - (e.Delta / 120f) * 40f, 0f, maxY);
            Invalidate();
            return;
        }

        if (_renderedBitmap == null) return;

        if ((ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            // Shift+wheel: horizontal
            CloseMenusOnScroll();
            ScrollBy(-(e.Delta / 120f) * 60f, 0f);
            return;
        }

        // Update immediately. Repainting the existing bitmap is cheap.
        CloseMenusOnScroll();
        float max = Math.Max(0, _renderedBitmap.Height - GetViewportSize().Height);
        _scrollOffset.Y = Math.Clamp(_scrollOffset.Y - (e.Delta / 120f) * 80f, 0f, max);
        if (_vScroll.Visible)
            _vScroll.Value = Math.Clamp((int)_scrollOffset.Y,
                _vScroll.Minimum, Math.Max(_vScroll.Minimum, _vScroll.Maximum - _vScroll.LargeChange + 1));
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBars();
        Invalidate();

        if (_rootBox != null && _document != null)
        {
            _resizeReflowTimer.Stop();
            _resizeReflowTimer.Start();
        }
    }

    private void OnResizeReflowTick(object? sender, EventArgs e)
    {
        _resizeReflowTimer.Stop();
        if (_rootBox == null || _document == null ||
            _fontCache == null || _imageCache == null || _resourceLoader == null)
            return;

        var vp = GetViewportSize();
        if (vp.Width <= 0 || vp.Height <= 0) return;

        // PERF: the debounce fires even when the size didn't actually
        // change (e.g. scrollbar toggle ping-pong) — skip redundant
        // full-tree rebuilds.
        if (vp.Width == _lastLayoutWidth && vp.Height == _lastLayoutHeight)
            return;
        _lastLayoutWidth = vp.Width;
        _lastLayoutHeight = vp.Height;

        var newRoot = LayoutEngineApi.BuildLayoutTree(_document, vp.Width, vp.Height);
        ApplyRelayout(_document, newRoot);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Keyboard
    // ─────────────────────────────────────────────────────────────────────

    private void OnDevToolsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F12)
        {
            if (_rootBox != null)
                LayoutDebug.DumpToDebugOutput(_rootBox);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F11)
        {
            _showBoxOutlines = !_showBoxOutlines;
            RequestRerender();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Tab)
        {
            // FIX: Shift+Tab cycled FORWARD too — now it cycles backwards,
            // and disabled controls are skipped.
            MoveFocusToNextControl(e.Shift ? -1 : 1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _focusedInput != null)
        {
            if (_focusedInput.TagName == "textarea")
            {
                // The newline is inserted by OnCanvasKeyPress — don't swallow it.
            }
            else if (IsEditableField(_focusedInput))
            {
                // Let page scripts see the key even with no <form> around the input
                // (the common "text box + JS button" pattern). Returning false cancels.
                var js = _jsInterpreter;
                var evt = js?.CreateKeyEvent("Enter", 13);
                var down = js?.FireEvent(_focusedInput, "onkeydown", evt);
                var press = js?.FireEvent(_focusedInput, "onkeypress", evt);
                bool cancelled =
                    (down is { Type: JsType.Boolean } && !down.ToBoolean()) ||
                    (press is { Type: JsType.Boolean } && !press.ToBoolean());

                // Era "form element pointer": the enclosing form may be a
                // foster-parented one (the <form>-between-table-and-rows
                // idiom) — FindEnclosingForm honours FormOwner.
                var form = FindEnclosingForm(_focusedInput);
                if (!cancelled && form != null)
                    SubmitForm(form, null);

                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else
            {
                // FIX: Enter on a tab-focused button/submit used to do
                // nothing — era browsers activate the focused control.
                string type = _focusedInput.GetAttrOrDefault("type", "").Trim().ToLowerInvariant();
                if (_document != null &&
                    (_focusedInput.TagName == "button" ||
                     (_focusedInput.TagName == "input" && type is "submit" or "reset" or "button")))
                {
                    HandleElementClick(_focusedInput, _document, _jsInterpreter, 0f, 0f, isFrame: false);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }
        else if (e.KeyCode == Keys.Space && _focusedInput != null && !IsEditableField(_focusedInput))
        {
            // FIX: Space never activated focused buttons/checkboxes/radios
            // or opened focused selects.
            var el = _focusedInput;
            string type = el.GetAttrOrDefault("type", "").Trim().ToLowerInvariant();
            if (_document != null &&
                (el.TagName is "button" or "select" ||
                 (el.TagName == "input" && type is "submit" or "reset" or "button" or "checkbox" or "radio")))
            {
                HandleElementClick(el, _document, _jsInterpreter, 0f, 0f, isFrame: false);
            }
            e.Handled = true;
        }
        else if (_focusedInput != null && IsEditableField(_focusedInput) &&
                 (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                                  or Keys.Home or Keys.End
                                  or Keys.Delete ||
                  (e.KeyCode == Keys.A && e.Control) ||
                  (e.KeyCode == Keys.V && e.Control) ||
                  (e.KeyCode == Keys.X && e.Control)))
        {
            // FIX: Up/Down scrolled the PAGE while a textarea was focused,
            // and Home/End jumped to the text edges instead of the line edges.
            HandleFieldKey(e);
        }
        else if (e.KeyCode == Keys.A && e.Control)
        {
            SelectAllText();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.C && e.Control)
        {
            CopySelectionToClipboard();
            e.Handled = true;
        }
        else if (_focusedInput == null && !_selecting &&
                 (e.KeyCode is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown))
        {
            int vh = Math.Max(40, ClientSize.Height);
            int dy = e.KeyCode switch
            {
                Keys.Up => -40,
                Keys.Down => 40,
                Keys.PageUp => -vh,
                Keys.PageDown => vh,
                _ => 0
            };
            ScrollBy(0, dy);
            e.Handled = true;
        }
    }

    private void HandleFieldKey(KeyEventArgs e)
    {
        var el = _focusedInput;
        if (el == null || !IsEditableField(el)) return;

        string text = GetFieldText(el);
        int len = text.Length;
        bool isTextarea = el.TagName == "textarea";

        // JS can rewrite the value mid-edit — never let the caret run off the ends.
        _fieldCaret = Math.Clamp(_fieldCaret, 0, len);
        _fieldSelAnchor = Math.Clamp(_fieldSelAnchor, 0, len);

        switch (e.KeyCode)
        {
            case Keys.Left:
                // FIX: Ctrl+Left/Right word jumps were missing.
                _fieldCaret = e.Control ? PrevWordStart(text, _fieldCaret) : Math.Max(0, _fieldCaret - 1);
                if (!e.Shift) _fieldSelAnchor = _fieldCaret;
                break;
            case Keys.Right:
                _fieldCaret = e.Control ? NextWordEnd(text, _fieldCaret) : Math.Min(len, _fieldCaret + 1);
                if (!e.Shift) _fieldSelAnchor = _fieldCaret;
                break;
            case Keys.Up:
                if (isTextarea) MoveTextareaCaret(-1, e.Shift);
                else { _fieldCaret = 0; if (!e.Shift) _fieldSelAnchor = 0; }
                break;
            case Keys.Down:
                if (isTextarea) MoveTextareaCaret(1, e.Shift);
                else { _fieldCaret = len; if (!e.Shift) _fieldSelAnchor = len; }
                break;
            case Keys.Home:
                _fieldCaret = !isTextarea ? 0
                    : e.Control ? 0 : TextareaLineEdge(el, text, start: true);
                if (!e.Shift) _fieldSelAnchor = _fieldCaret;
                break;
            case Keys.End:
                _fieldCaret = !isTextarea ? len
                    : e.Control ? len : TextareaLineEdge(el, text, start: false);
                if (!e.Shift) _fieldSelAnchor = _fieldCaret;
                break;
            case Keys.Delete:
                if (_fieldSelAnchor != _fieldCaret) DeleteFieldSelection();
                else if (_fieldCaret < len)
                {
                    SetFieldText(el, text.Remove(_fieldCaret, 1));
                    _fieldSelAnchor = _fieldCaret;
                }
                break;
            case Keys.A:
                _fieldSelAnchor = 0;
                _fieldCaret = len;
                break;
            case Keys.V:
                try
                {
                    string? clip = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clip))
                    {
                        // FIX: pasting multi-line clipboard text into a
                        // single-line <input> injected raw newlines.
                        string cleaned = clip.Replace("\r\n", "\n").Replace('\r', '\n');
                        if (!isTextarea) cleaned = cleaned.Replace("\n", "");
                        FieldInsertText(cleaned);
                    }
                }
                catch { }
                break;
            case Keys.X:
                // FIX: with no selection this used to COPY the whole field
                // and leave it intact — i.e. "Ctrl+X = Ctrl+C of everything".
                CopyFieldSelectionToClipboard();
                DeleteFieldSelection();
                break;
        }

        RequestRerender();
        e.Handled = true;
    }

    private void OnCanvasKeyPress(object? sender, KeyPressEventArgs e)
    {
        var elem = _focusedInput;
        if (elem == null || !IsEditableField(elem)) return;

        // FIX: plain typing never fired onkeydown/onkeypress on the field,
        // so key-capture scripts (and "return false" input filters) were dead.
        var js = _jsInterpreter;
        if (js != null)
        {
            try
            {
                var evt = js.CreateKeyEvent(e.KeyChar.ToString(), e.KeyChar);
                var down = js.FireEvent(elem, "onkeydown", evt);
                var press = js.FireEvent(elem, "onkeypress", evt);
                if ((down is { Type: JsType.Boolean } && !down.ToBoolean()) ||
                    (press is { Type: JsType.Boolean } && !press.ToBoolean()))
                {
                    e.Handled = true;
                    return;
                }
            }
            catch { /* a script error must not eat the keystroke */ }
        }

        if (e.KeyChar == '\b')
        {
            if (_fieldSelAnchor != _fieldCaret) DeleteFieldSelection();
            else
            {
                var text = GetFieldText(elem);
                // FIX: if JS shrank the value mid-edit, the caret could sit
                // past the end and text.Remove() threw ArgumentOutOfRange.
                _fieldCaret = Math.Clamp(_fieldCaret, 0, text.Length);
                if (_fieldCaret > 0)
                {
                    SetFieldText(elem, text.Remove(_fieldCaret - 1, 1));
                    _fieldCaret--;
                    _fieldSelAnchor = _fieldCaret;
                }
            }
        }
        else if (e.KeyChar >= ' ' && e.KeyChar != '\x7F')
        {
            FieldInsertText(e.KeyChar.ToString());
        }

        RequestRerender();
        e.Handled = true;
    }

    // ── Field text helpers ────────────────────────────────────────────────

    private static string GetFieldText(DomElement el) =>
        el.TagName == "textarea" ? (el.InnerText ?? "") : (el.GetAttr("value") ?? "");

    private static void SetFieldText(DomElement el, string text)
    {
        if (el.TagName == "textarea") SetTextareaText(el, text);
        else el.SetAttr("value", text);
    }

    private void FieldInsertText(string s)
    {
        var el = _focusedInput;
        if (el == null) return;
        if (_fieldSelAnchor != _fieldCaret) DeleteFieldSelection();
        var text = GetFieldText(el);
        int pos = Math.Clamp(_fieldCaret, 0, text.Length);
        int limit = el.GetAttrInt("maxlength", el.TagName == "textarea" ? 4096 : int.MaxValue);
        int available = Math.Max(0, limit - text.Length);
        if (available == 0) return;
        if (s.Length > available) s = s[..available];
        if (s.Length == 0) return;
        SetFieldText(el, text.Insert(pos, s));
        _fieldCaret = _fieldSelAnchor = pos + s.Length;
    }

    private void DeleteFieldSelection()
    {
        var el = _focusedInput;
        if (el == null) return;
        var text = GetFieldText(el);
        int s = Math.Clamp(Math.Min(_fieldSelAnchor, _fieldCaret), 0, text.Length);
        int e2 = Math.Clamp(Math.Max(_fieldSelAnchor, _fieldCaret), 0, text.Length);
        if (s == e2) return;
        SetFieldText(el, text.Remove(s, e2 - s));
        _fieldCaret = _fieldSelAnchor = s;
    }

    private void CopyFieldSelectionToClipboard()
    {
        var el = _focusedInput;
        if (el == null) return;
        try
        {
            int s = Math.Min(_fieldSelAnchor, _fieldCaret);
            int e2 = Math.Max(_fieldSelAnchor, _fieldCaret);
            if (s == e2) return;   // nothing selected — copy nothing
            var text = GetFieldText(el);
            string slice = text[Math.Clamp(s, 0, text.Length)..Math.Clamp(e2, 0, text.Length)];
            if (slice.Length > 0) Clipboard.SetText(slice);
        }
        catch { }
    }

    private Bitmap? _measureBmp;
    private Graphics? _measureGfx;
    private Graphics MeasureGraphics
    {
        get
        {
            if (_measureGfx == null)
            {
                _measureBmp = new Bitmap(1, 1);
                _measureGfx = Graphics.FromImage(_measureBmp);
                _measureGfx.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            }
            return _measureGfx;
        }
    }

    /// <summary>Font the field text is drawn with (single-line inputs and textareas).</summary>
    private Font? ResolveFieldFont(DomElement el)
    {
        if (_fontCache == null) return null;
        var style = el.Style;
        bool bold = style?.FontWeight >= FontWeightValue.Bold;
        bool italic = style?.FontStyle == FontStyleValue.Italic;

        if (el.TagName == "textarea")
        {
            float size = style?.FontSize > 0f ? Math.Min(style.FontSize, 13f) : 13f;
            return _fontCache.Resolve(new List<string> { "Courier New", "monospace" }, size, bold, italic);
        }

        return _fontCache.Resolve(
            style?.FontFamily is { Count: > 0 } fam ? fam : new List<string> { "Times New Roman", "serif" },
            style?.FontSize > 0f ? style.FontSize : 16f,
            bold, italic);
    }

    private static StringFormat NewFieldFormat(bool noWrap)
    {
        var f = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = (noWrap ? StringFormatFlags.NoWrap : 0)
                        | StringFormatFlags.MeasureTrailingSpaces,
            Trimming = StringTrimming.None,
            LineAlignment = StringAlignment.Near,
            Alignment = StringAlignment.Near
        };
        return f;
    }

    /// <summary>
    /// Break textarea text into visual lines.  Shared by the overlay painter and
    /// click-to-caret so drawing and hit-testing can never disagree.
    /// </summary>
    private static List<(int Start, int End)> BreakTextareaLines(
        Graphics g, string text, Font font, float wrapWidth, bool wrapOff)
        => Engine.Render.TextareaOverlay.BreakLines(g, text, font, wrapWidth, wrapOff);

    // ── Textarea geometry (cached) ───────────────────────────────────────

    private sealed class TextareaGeometry
    {
        public required LayoutBox Box;
        public required Font Font;
        public required string Text;
        public required List<(int Start, int End)> Lines;
    }

    private TextareaGeometry? GetTextareaGeometry(DomElement el, LayoutBox? box = null)
    {
        if (_rootBox == null) return null;
        box ??= FindBoxForElement(_rootBox, el);
        var font = ResolveFieldFont(el);
        if (box == null || font == null) return null;
        string text = GetFieldText(el);
        var lines = GetTextareaLines(el, box, text, font);
        return new TextareaGeometry { Box = box, Font = font, Text = text, Lines = lines };
    }

    private List<(int Start, int End)> GetTextareaLines(DomElement el, LayoutBox box,
                                                        string text, Font font)
    {
        bool wrapOff = el.GetAttrOrDefault("wrap", "").Trim()
            .Equals("off", StringComparison.OrdinalIgnoreCase);
        float width = Math.Max(1f, box.ContentRect.Width - 6);

        // PERF: the caret blink repainted (and re-broke lines) twice a
        // second and every drag-move re-broke them — now cached per
        // (text, font, width, wrap).
        if (_taCacheLines != null && _taCacheText == text &&
            ReferenceEquals(_taCacheFont, font) &&
            _taCacheWidth == width && _taCacheWrapOff == wrapOff)
            return _taCacheLines;

        var lines = BreakTextareaLines(MeasureGraphics, text, font, width, wrapOff);
        _taCacheText = text;
        _taCacheFont = font;
        _taCacheWidth = width;
        _taCacheWrapOff = wrapOff;
        _taCacheLines = lines;
        return lines;
    }

    /// <summary>
    /// Which visual line a caret index sits on.  A caret at a wrap boundary
    /// belongs to the START of the next line; a caret at a hard newline
    /// belongs to the end of its own line.
    /// </summary>
    private static int CaretLineIndex(List<(int Start, int End)> lines, int caret)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var (ls, le) = lines[i];
            bool lastLine = i == lines.Count - 1;
            if (caret >= ls && (caret < le || lastLine ||
                (caret == le && i + 1 < lines.Count && lines[i + 1].Start != le)))
                return i;
        }
        return Math.Max(0, lines.Count - 1);
    }

    /// <summary>
    /// Caret index inside [start,end) whose x-edge is closest to relX.
    /// PERF: prefix widths are non-decreasing, so a binary search replaces
    /// the old linear scan — which was O(n²) MeasureString calls on every
    /// drag-move for long lines.
    /// </summary>
    private int NearestCaretIndex(string text, Font font, StringFormat fmt,
                                  int start, int end, float relX)
    {
        if (relX <= 0f || end <= start) return start;
        var g = MeasureGraphics;

        float Width(int i) => g.MeasureString(text[start..i], font, int.MaxValue, fmt).Width;

        int lo = start, hi = end;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Width(mid) < relX) lo = mid + 1; else hi = mid;
        }
        if (lo > start)
        {
            float prev = Width(lo - 1), cur = Width(lo);
            if (Math.Abs(prev - relX) <= Math.Abs(cur - relX)) lo -= 1;
        }
        return Math.Clamp(lo, start, end);
    }

    private int FieldCaretFromPoint(DomElement el, LayoutBox box, float docX, float docY)
    {
        string text = GetFieldText(el);
        if (text.Length == 0) return 0;

        var font = ResolveFieldFont(el);
        if (font == null) return text.Length;

        using var fmt = NewFieldFormat(noWrap: true);
        var face = box.ContentRect;

        if (el.TagName == "textarea")
        {
            var geo = GetTextareaGeometry(el, box);
            if (geo == null || geo.Lines.Count == 0) return text.Length;
            float lineH = font.GetHeight(MeasureGraphics);
            int li = Math.Clamp((int)Math.Floor((docY - (face.Y + 2)) / lineH), 0, geo.Lines.Count - 1);
            var (ls, le) = geo.Lines[li];
            return NearestCaretIndex(geo.Text, font, fmt, ls, le, docX - (face.X + 3));
        }

        bool password = el.GetAttrOrDefault("type", "text") == "password";
        if (password) text = new string('*', text.Length);
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float textWidth = MeasureGraphics.MeasureString(text, font, int.MaxValue, fmt).Width;
        if (textWidth > visibleWidth && docX >= face.Right - 6f)
            return text.Length;
        return NearestCaretIndex(text, font, fmt, 0, text.Length,
            docX - (face.X + 3) + _fieldScrollX);
    }

    private float EnsureSingleLineCaretVisible(Graphics g, string text, Font font,
                                                RectangleF face, int caret)
    {
        using var fmt = NewFieldFormat(noWrap: true);
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float caretX = caret <= 0 ? 0f
            : g.MeasureString(text[..Math.Min(caret, text.Length)], font,
                              int.MaxValue, fmt).Width;
        float totalWidth = g.MeasureString(text, font, int.MaxValue, fmt).Width;
        float maxScroll = Math.Max(0f, totalWidth - visibleWidth);
        if (caretX - _fieldScrollX > visibleWidth)
            _fieldScrollX = caretX - visibleWidth;
        else if (caretX - _fieldScrollX < 0f)
            _fieldScrollX = caretX;
        _fieldScrollX = Math.Clamp(_fieldScrollX, 0f, maxScroll);
        return _fieldScrollX;
    }

    private static int PrevWordStart(string text, int i)
    {
        i = Math.Clamp(i, 0, text.Length);
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }

    private static int NextWordEnd(string text, int i)
    {
        int n = text.Length;
        i = Math.Clamp(i, 0, n);
        while (i < n && char.IsWhiteSpace(text[i])) i++;
        while (i < n && !char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private int TextareaLineEdge(DomElement el, string text, bool start)
    {
        var geo = GetTextareaGeometry(el);
        if (geo == null || geo.Lines.Count == 0)
            return start ? 0 : text.Length;
        int li = CaretLineIndex(geo.Lines, Math.Clamp(_fieldCaret, 0, text.Length));
        return start ? geo.Lines[li].Start : geo.Lines[li].End;
    }

    private void MoveTextareaCaret(int lineDelta, bool extend)
    {
        var el = _focusedInput;
        if (el == null) return;
        var geo = GetTextareaGeometry(el);
        if (geo == null || geo.Lines.Count == 0) return;

        int caret = Math.Clamp(_fieldCaret, 0, geo.Text.Length);
        int li = CaretLineIndex(geo.Lines, caret);
        int target = Math.Clamp(li + lineDelta, 0, geo.Lines.Count - 1);
        if (target == li) return;

        using var fmt = NewFieldFormat(noWrap: true);
        var g = MeasureGraphics;
        var (cs, _) = geo.Lines[li];
        float colX = caret > cs
            ? g.MeasureString(geo.Text[cs..caret], geo.Font, int.MaxValue, fmt).Width
            : 0f;
        var (ts, te) = geo.Lines[target];
        _fieldCaret = NearestCaretIndex(geo.Text, geo.Font, fmt, ts, te, colX);
        if (!extend) _fieldSelAnchor = _fieldCaret;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Focus plumbing (onfocus / onblur / onchange)
    // ─────────────────────────────────────────────────────────────────────

    private static bool IsEditableField(DomElement? el)
    {
        if (el == null) return false;
        if (el.TagName == "textarea") return true;
        if (el.TagName != "input") return false;
        return el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant()
            is "text" or "password" or "file";
    }

    private void FocusControl(DomElement el, int caretPos, JsInterpreter? js = null)
    {
        ArgumentNullException.ThrowIfNull(el);
        js ??= _jsInterpreter;

        if (!ReferenceEquals(_focusedInput, el))
        {
            BlurField();
            _focusedInput = el;
            _fieldValueAtFocus = GetFieldText(el);
            js?.FireEvent(el, "onfocus");   // FIX: clicking a field never fired onfocus
        }
        RememberDefault(el);
        _fieldCaret = _fieldSelAnchor = Math.Clamp(caretPos, 0, GetFieldText(el).Length);
        _fieldScrollX = 0f;
        RequestRerender();
    }

    private void BlurField()
    {
        var el = _focusedInput;
        if (el == null) return;
        _focusedInput = null;
        _fieldDragging = false;

        var js = _jsInterpreter;
        // FIX: onchange NEVER fired for text inputs/textareas.  DOM order:
        // change fires before blur.
        if (IsEditableField(el) && GetFieldText(el) != (_fieldValueAtFocus ?? GetFieldText(el)))
            js?.FireEvent(el, "onchange");
        js?.FireEvent(el, "onblur");
        _fieldValueAtFocus = null;

        RequestRerender();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Mouse
    // ─────────────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        _dragMoved = false;

        if (CanFocus && !Focused)
            Focus();

        if (e.Button != MouseButtons.Left || _rootBox == null || _document == null)
            return;

        float x = e.X + _scrollOffset.X;
        float y = e.Y + _scrollOffset.Y;

        var deepest = HitTestDeepestBox(_rootBox, x, y);
        var el = deepest?.Element;

        // DIAGNOSTIC (remove once the input-click bug is found): list every
        // box under the cursor, outermost first, so an overlay that covers a
        // control shows up by name and rect.
        try
        {
            var chain = new System.Text.StringBuilder();
            void Walk(LayoutBox b, int depth)
            {
                var r = b.BorderRect;
                bool hit = r.Contains(x, y);
                bool zero = b.Width == 0f && b.Height == 0f;
                if (!hit && !zero) return;
                if (hit)
                    chain.Append($"\n    {new string(' ', depth * 2)}<{b.Element?.TagName ?? b.BoxType.ToString()}> " +
                                 $"type={b.BoxType} rect=({r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#})");
                foreach (var c in b.Children) Walk(c, depth + 1);
            }
            Walk(_rootBox, 0);
            DebugLog.Write($"CLICK client=({e.X},{e.Y}) doc=({x:0.#},{y:0.#}) scroll=({_scrollOffset.X:0.#},{_scrollOffset.Y:0.#})" +
                           $" deepest=<{el?.TagName ?? "null"}> isControl={IsControlElement(el)} " +
                           $"frameBoxAtPoint={(FrameBoxAtPoint(x, y) != null)}{chain}");
        }
        catch (Exception ex) { DebugLog.WriteException("click diagnostic", ex); }

        // Clicking anywhere other than the focused control itself blurs it.
        if (_focusedInput != null && !ReferenceEquals(el, _focusedInput))
            BlurField();

        if (el != null && IsControlElement(el))
        {
            if (el.HasAttr("disabled"))
                return;

            string type = el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
            if (IsEditableField(el))
            {
                FocusControl(el, FieldCaretFromPoint(el, deepest!, x, y));
                _fieldDragging = true;
                Capture = true;   // FIX: drag-selection lost the mouse past the field edge
                return;
            }
            if (el.TagName == "button" ||
                (el.TagName == "input" && type is "submit" or "reset" or "button"))
            {
                // Win95 press-in bevel while held
                _pressedControl = el;
                RerenderNow();
                return;
            }
            return;   // checkbox / radio / select — dispatched on mouse-up
        }

        // Page text selection drag.  FIX: a fresh press on blank space used
        // to leave the previous selection highlighted forever.
        _selStart = e.Location;
        var anchor = HitTestTextBox(_rootBox, x, y);
        long now = Environment.TickCount64;
        bool continuingTextClick = anchor != null &&
            ReferenceEquals(anchor, _lastTextClickBox) &&
            now - _lastTextClickTicks <= SystemInformation.DoubleClickTime &&
            Math.Abs(e.X - _lastTextClickPoint.X) <= SystemInformation.DoubleClickSize.Width &&
            Math.Abs(e.Y - _lastTextClickPoint.Y) <= SystemInformation.DoubleClickSize.Height;
        _textClickCount = continuingTextClick ? _textClickCount + 1 : 1;
        _lastTextClickBox = anchor;
        _lastTextClickPoint = e.Location;
        _lastTextClickTicks = now;

        if (anchor != null && _textClickCount >= 3)
        {
            SelectTextBlock(anchor);
            _textClickCount = 0;
            _selecting = false;
            _suppressNextMouseUp = true;
            Invalidate();
            return;
        }
        if (anchor != null && _textClickCount == 2)
        {
            _selAnchor = _selFocus = anchor;
            _selecting = false;
            _suppressNextMouseUp = true;
            Invalidate();
            return;
        }
        _selAnchor = _selFocus = null;
        _pendingSelectionAnchor = anchor;
        _selecting = anchor != null;
        if (_selecting) Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_fieldDragging && _focusedInput != null && _rootBox != null)
        {
            float x = e.X + _scrollOffset.X;
            float y = e.Y + _scrollOffset.Y;
            var box = FindBoxForElement(_rootBox, _focusedInput);
            if (box != null)
            {
                _fieldCaret = FieldCaretFromPoint(_focusedInput, box, x, y);
                Invalidate();
            }
            return;
        }

        if (_selecting)
        {
            float x = e.X + _scrollOffset.X;
            float y = e.Y + _scrollOffset.Y;
            if (_rootBox != null)
            {
                var f = HitTestTextBox(_rootBox, x, y);
                bool moved = Math.Abs(e.X - _selStart.X) + Math.Abs(e.Y - _selStart.Y) > 4;
                if (moved && !_dragMoved)
                {
                    _dragMoved = true;
                    _selAnchor = _pendingSelectionAnchor;
                    _selFocus = f ?? _pendingSelectionAnchor;
                    Invalidate();
                }
                else if (_dragMoved && f != null && f != _selFocus)
                {
                    _selFocus = f;
                    Invalidate();
                }
            }
            return;
        }

        if (_rootBox == null || _document == null) return;

        float mx = e.X + _scrollOffset.X;
        float my = e.Y + _scrollOffset.Y;

        DomElement? element;
        DomDocument doc = _document;

        var frameBox = FrameBoxAtPoint(mx, my);
        if (frameBox != null && _frames.TryGetValue(frameBox, out var view))
        {
            element = HitTestElement(view.RootBox,
                mx - frameBox.X + view.Scroll.X,
                my - frameBox.Y + view.Scroll.Y);
            doc = view.Document;
        }
        else
        {
            element = HitTestElement(_rootBox, mx, my);
        }

        var hoverAnchor = element != null && element.TagName == "a"
            ? element
            : element != null ? FindAncestor(element, "a") : null;
        if (hoverAnchor != null && hoverAnchor.HasAttr("href"))
        {
            Cursor = Cursors.Hand;
            try
            {
                SetStatus(doc.BaseUrl?.Resolve(hoverAnchor.GetAttr("href")!).ToAbsolute() ?? "");
            }
            catch { SetStatus(""); }
        }
        else
        {
            // I-beam over selectable text AND over editable fields
            bool overText =
                (element != null && HitTestTextBox(_rootBox, mx, my) != null) ||
                IsEditableField(element);
            Cursor = overText ? Cursors.IBeam : Cursors.Default;
            SetStatus("");
        }

        if (element != _lastHoveredElement)
        {
            if (_lastHoveredElement != null)
                _jsInterpreter?.FireEvent(_lastHoveredElement, "onmouseout");
            if (element != null)
                _jsInterpreter?.FireEvent(element, "onmouseover");
            _lastHoveredElement = element;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        // FIX: releasing the mouse OFF a pressed button used to activate it
        // anyway — browsers cancel the click in that case.
        var pressedEl = _pressedControl;
        bool wasPressed = pressedEl != null;

        bool wasSelecting = _selecting;
        _selecting = false;
        Capture = false;

        // Release any pressed button bevel
        if (pressedEl != null)
        {
            _pressedControl = null;
            RerenderNow();
        }

        if (_fieldDragging)
        {
            _fieldDragging = false;
            return;   // field click fully handled at mouse-down
        }

        if (e.Button == MouseButtons.Right)
        {
            ShowContextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;

        if (_suppressNextMouseUp)
        {
            _suppressNextMouseUp = false;
            return;
        }

        if (_dragMoved)
        {
            _dragMoved = false;
            return;   // selection drag, not a click
        }

        // Plain single click clears the selection — but NOT the second
        // release of a double-click (that was the highlight flicker).
        if (wasSelecting && !_dragMoved)
        {
            _selAnchor = _selFocus = null;
            Invalidate();
        }

        if (wasPressed)
        {
            float ux = e.X + _scrollOffset.X;
            float uy = e.Y + _scrollOffset.Y;
            var upEl = _rootBox != null ? HitTestDeepestBox(_rootBox, ux, uy)?.Element : null;
            if (upEl == null || !IsElementWithin(upEl, pressedEl!))
                return;   // released off the control — cancel activation
        }

        DispatchLeftClick(e.Location);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // Clicks dispatch from OnMouseUp (browser-style activation).
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_rootBox == null || e.Button != MouseButtons.Left)
            return;

        float x = e.X + _scrollOffset.X;
        float y = e.Y + _scrollOffset.Y;

        // Double-click inside an editable field: select the word
        if (_focusedInput != null)
        {
            var box = FindBoxForElement(_rootBox, _focusedInput);
            if (box != null && box.BorderRect.Contains(x, y))
            {
                string text = GetFieldText(_focusedInput);
                int caret = FieldCaretFromPoint(_focusedInput, box, x, y);
                int s = caret, e2 = caret;
                while (s > 0 && !char.IsWhiteSpace(text[s - 1])) s--;
                while (e2 < text.Length && !char.IsWhiteSpace(text[e2])) e2++;
                _fieldSelAnchor = s;
                _fieldCaret = e2;
                Invalidate();
                return;
            }
        }

        // Double-click on page text: each fragment IS one word (spaces are
        // glued to the previous word) — select that box.
        var word = HitTestTextBox(_rootBox, x, y);
        if (word != null)
        {
            if (e.Clicks >= 3)
                SelectTextBlock(word);
            else
            {
                _selAnchor = word;
                _selFocus = word;
            }
            Invalidate();
        }
    }

    private void SelectTextBlock(LayoutBox word)
    {
        LayoutBox? container = null;
        for (var current = word.Parent; current != null; current = current.Parent)
        {
            if (current.Element != null && current.Element.TagName is
                "p" or "div" or "li" or "pre" or "blockquote" or
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
                "td" or "th" or "center")
            {
                container = current;
                break;
            }
        }

        container ??= word.Parent;
        var textBoxes = container?.Descendants()
            .Where(b => !string.IsNullOrEmpty(b.TextRun))
            .ToList();
        if (textBoxes is { Count: > 0 })
        {
            _selAnchor = textBoxes[0];
            _selFocus = textBoxes[^1];
        }
        else
        {
            _selAnchor = _selFocus = word;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // FIX: onmouseout never fired when the pointer simply left the
        // canvas, and the I-beam/hand cursor stuck.
        if (!_selecting && !_fieldDragging)
        {
            Cursor = Cursors.Default;
            SetStatus("");
        }
        if (_lastHoveredElement != null)
        {
            _jsInterpreter?.FireEvent(_lastHoveredElement, "onmouseout");
            _lastHoveredElement = null;
        }
    }

    private static bool IsElementWithin(DomElement? el, DomElement ancestor)
    {
        for (var n = el; n != null; n = n.Parent as DomElement)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    public void CopySelectionToClipboard()
    {
        try
        {
            string text = GetSelectedText();
            if (text.Length == 0 && _focusedInput != null)
            {
                // FIX: a field WITH a selection copied the WHOLE field text.
                var ft = GetFieldText(_focusedInput);
                int s = Math.Clamp(Math.Min(_fieldSelAnchor, _fieldCaret), 0, ft.Length);
                int e2 = Math.Clamp(Math.Max(_fieldSelAnchor, _fieldCaret), 0, ft.Length);
                text = s == e2 ? ft : ft[s..e2];
            }
            if (text.Length > 0)
                Clipboard.SetText(text);
        }
        catch { }
    }

    private IEnumerable<LayoutBox> SelectedTextBoxes()
    {
        if (_rootBox == null || _selAnchor == null || _selFocus == null)
            yield break;

        var textBoxes = _rootBox.Descendants()
            .Where(b => !string.IsNullOrEmpty(b.TextRun))
            .ToList();
        int start = textBoxes.IndexOf(_selAnchor);
        int end = textBoxes.IndexOf(_selFocus);
        if (start < 0 || end < 0) yield break;
        if (start > end) (start, end) = (end, start);

        for (int i = start; i <= end; i++)
            yield return textBoxes[i];
    }

    private string GetSelectedText()
    {
        if (_selAnchor == null || _selFocus == null) return "";
        return string.Concat(SelectedTextBoxes().Select(b => b.TextRun));
    }

    private void SelectAllText()
    {
        if (_rootBox == null) return;
        var textBoxes = _rootBox.Descendants()
            .Where(b => !string.IsNullOrEmpty(b.TextRun))
            .ToList();
        if (textBoxes.Count == 0) return;

        _selAnchor = textBoxes[0];
        _selFocus = textBoxes[^1];
        Invalidate();
    }

    private static bool IsControlElement(DomElement? element) =>
        element != null && element.TagName is "input" or "select" or "textarea" or "button";

    // ─────────────────────────────────────────────────────────────────────
    // Menus (auto-disposed on close — they used to pile up until the
    // canvas itself was disposed)
    // ─────────────────────────────────────────────────────────────────────

    private void TrackMenu(ContextMenuStrip menu)
    {
        _contextMenus.Add(menu);
        menu.Closed += (s, e) =>
        {
            if (IsDisposed || !IsHandleCreated) { RemoveMenu(menu); return; }
            try { BeginInvoke(() => RemoveMenu(menu)); }
            catch { RemoveMenu(menu); }
        };
    }

    private void RemoveMenu(ContextMenuStrip menu)
    {
        if (_contextMenus.Remove(menu))
        {
            try { menu.Dispose(); } catch { }
        }
    }

    private void ShowContextMenu(System.Drawing.Point clientPoint)
    {
        float x = clientPoint.X + _scrollOffset.X;
        float y = clientPoint.Y + _scrollOffset.Y;
        _contextElement = _rootBox != null ? HitTestElement(_rootBox, x, y) : null;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Back", null, (s, e) => BackRequested?.Invoke());
        menu.Items.Add("Forward", null, (s, e) => ForwardRequested?.Invoke());
        menu.Items.Add("Reload", null, (s, e) => ReloadRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        var copy = menu.Items.Add("Copy", null, (s, e) => CopySelectionToClipboard());
        copy.Enabled = GetSelectedText().Length > 0 || _focusedInput != null;
        menu.Items.Add("Select All", null, (s, e) => SelectAllText());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Inspect Element", null, (s, e) =>
            new PageInspector(this, _contextElement).Show(this));

        TrackMenu(menu);
        menu.Show(this, clientPoint);
    }

    private void DispatchLeftClick(System.Drawing.Point clientPoint)
    {
        if (_rootBox == null || _document == null) return;

        float x = clientPoint.X + _scrollOffset.X;
        float y = clientPoint.Y + _scrollOffset.Y;
        var frameBox = FrameBoxAtPoint(x, y);
        if (frameBox != null && _frames.TryGetValue(frameBox, out var view))
        {
            _focusedFrame = frameBox;
            HandleClickInView(view, frameBox,
                x - frameBox.X + view.Scroll.X,
                y - frameBox.Y + view.Scroll.Y);
            return;
        }

        var element = HitTestElement(_rootBox, x, y);
        if (element != null)
            HandleElementClick(element, _document, _jsInterpreter, x, y, isFrame: false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Hit-testing (zero-size tolerant — tr/tbody wrappers stay 0,0,0,0)
    // ─────────────────────────────────────────────────────────────────────

    private static DomElement? HitTestElement(LayoutBox box, float x, float y) =>
        Engine.Layout.HitTester.ElementAt(box, x, y);

    private static LayoutBox? HitTestDeepestBox(LayoutBox box, float x, float y) =>
        Engine.Layout.HitTester.DeepestBoxAt(box, x, y);

    private static LayoutBox? HitTestTextBox(LayoutBox box, float x, float y) =>
        Engine.Layout.HitTester.TextBoxAt(box, x, y);

    private LayoutBox? FrameBoxAtPoint(float x, float y)
    {
        foreach (var (box, _) in _frames)
            if (box.BorderRect.Contains(x, y))
                return box;
        return null;
    }

    private static LayoutBox? FindBoxForElement(LayoutBox root, DomElement element) =>
        Engine.Layout.HitTester.BoxForElement(root, element);

    private static DomElement? FindAncestor(DomElement element, string tagName) =>
        Engine.Forms.FormSubmitter.FindAncestor(element, tagName);

    // ─────────────────────────────────────────────────────────────────────
    // Click dispatch
    // ─────────────────────────────────────────────────────────────────────

    private void HandleClickInView(FrameView view, LayoutBox frameBox, float lx, float ly)
    {
        var element = HitTestElement(view.RootBox, lx, ly);
        if (element == null) return;
        // FIX: frame clicks ran on the PAGE interpreter — frame scripts'
        // own handlers (and their onsubmit) never saw them.
        HandleElementClick(element, view.Document, view.Interpreter ?? _jsInterpreter,
            lx, ly, isFrame: true, frameView: view);
    }

    private void HandleElementClick(DomElement element, DomDocument document,
                                    JsInterpreter? js, float x, float y,
                                    bool isFrame, FrameView? frameView = null)
    {
        var layoutRoot = frameView?.RootBox ?? _rootBox;

        // List-box rows are laid out as option elements, but activation belongs
        // to their owning select control.
        if (element.TagName == "option")
        {
            for (var parent = element.Parent as DomElement; parent != null;
                 parent = parent.Parent as DomElement)
            {
                if (parent.TagName != "select") continue;
                element = parent;
                break;
            }
        }

        if (element.TagName == "input")
        {
            if (element.HasAttr("disabled"))
                return;

            string type = element.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
            if (type is "text" or "password" or "file")
            {
                FocusControl(element, GetFieldText(element).Length, js);
                js?.FireEvent(element, "onclick");
                return;
            }
            if (type is "checkbox")
            {
                FocusControl(element, 0, js);
                if (element.HasAttr("checked")) element.SetAttr("checked", null);
                else element.SetAttr("checked", "");
                js?.FireEvent(element, "onclick");
                RequestRerender();
                return;
            }
            if (type is "radio")
            {
                FocusControl(element, 0, js);
                string? name = element.GetAttr("name");
                if (name != null)
                {
                    foreach (var other in document.ElementDescendants()
                        .Where(o => o.TagName == "input" &&
                                    o.GetAttrOrDefault("type", "")
                                        .Trim().Equals("radio", StringComparison.OrdinalIgnoreCase) &&
                                    o.GetAttr("name") == name))
                        other.SetAttr("checked", null);
                }
                element.SetAttr("checked", "");
                js?.FireEvent(element, "onclick");
                RequestRerender();
                return;
            }
            if (type is "submit" or "image")
            {
                FocusControl(element, 0, js);
                // Browsers run the click handler first; returning false cancels the submit.
                var clickResult = js?.FireEvent(element, "onclick");
                if (clickResult is { Type: JsType.Boolean } && !clickResult.ToBoolean())
                    return;

                var form = FindEnclosingForm(element);
                if (form != null)
                {
                    (string, int, int)? imageClick = null;
                    if (type == "image" && element.HasAttr("name") && layoutRoot != null)
                    {
                        var imgBox = FindBoxForElement(layoutRoot, element);
                        if (imgBox != null)
                            imageClick = (element.GetAttr("name")!,
                                (int)(x - imgBox.X), (int)(y - imgBox.Y));
                    }
                    SubmitFormInternal(form, document, js, imageClick, frameView);
                }
                return;
            }
            if (type is "reset")
            {
                FocusControl(element, 0, js);
                js?.FireEvent(element, "onclick");
                ResetForm(FindEnclosingForm(element));
                RequestRerender();
                return;
            }
            if (type is "button")
            {
                FocusControl(element, 0, js);
                js?.FireEvent(element, "onclick");
                return;
            }
            return;
        }

        if (element.TagName == "button")
        {
            FocusControl(element, 0, js);
            if (element.GetAttrOrDefault("type", "submit").Trim().ToLowerInvariant() == "submit")
            {
                var clickResult = js?.FireEvent(element, "onclick");
                if (clickResult is { Type: JsType.Boolean } && !clickResult.ToBoolean())
                    return;

                var form = FindEnclosingForm(element);
                if (form != null)
                {
                    SubmitFormInternal(form, document, js, null, frameView);
                }
                return;
            }
            js?.FireEvent(element, "onclick");
            return;
        }

        if (element.TagName == "select")
        {
            FocusControl(element, 0, js);
            if (element.HasAttr("multiple") || element.GetAttrInt("size", 1) > 1)
            {
                SelectListBoxOption(element, x, y, js, layoutRoot);
                return;
            }
            ShowSelectDropdown(element, js);
            return;
        }

        if (element.TagName == "textarea")
        {
            FocusControl(element, GetFieldText(element).Length, js);
            return;
        }

        var anchor = element.TagName == "a" ? element : FindAncestor(element, "a");
        if (anchor != null && anchor.HasAttr("href"))
        {
            string href = anchor.GetAttr("href")!;

            // Click handlers run before an anchor's default navigation. A
            // handler returning false cancels that navigation, which is the
            // classic JS pattern used by javascript-basic.html.
            var clickResult = js?.FireEvent(anchor, "onclick");
            if (clickResult is { Type: JsType.Boolean } && !clickResult.ToBoolean())
                return;

            if (href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                try { js?.ExecuteString(href.TrimStart()[11..]); }
                catch { }
                return;
            }

            if (document.BaseUrl != null)
            {
                string abs = document.BaseUrl.Resolve(href).ToAbsolute();

                string? target = anchor.GetAttr("target");
                if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(document.BaseTarget))
                    target = document.BaseTarget;

                NavigateWithTarget(abs, target, isFrame ? frameView : null);
            }
            return;
        }

        if (element.TagName == "img" && element.HasAttr("usemap"))
        {
            HandleImageMapClick(element, document, x, y, layoutRoot);
            return;
        }

        if (element.TagName == "img" && element.HasAttr("ismap"))
        {
            var ismapAnchor = FindAncestor(element, "a");
            var href = ismapAnchor?.GetAttr("href");
            if (href != null && document.BaseUrl != null && layoutRoot != null)
            {
                var box = FindBoxForElement(layoutRoot, element);
                float relX = box != null ? x - box.X : 0;
                float relY = box != null ? y - box.Y : 0;
                string abs = document.BaseUrl.Resolve(href).ToAbsolute();
                string sep = abs.Contains('?') ? "&" : "?";
                NavigateRequested?.Invoke($"{abs}{sep}{(int)relX},{(int)relY}");
                return;
            }
        }

        js?.FireEvent(element, "onclick");
    }

    private void SelectListBoxOption(DomElement select, float x, float y,
                                     JsInterpreter? js, LayoutBox? layoutRoot)
    {
        if (layoutRoot == null || _fontCache == null) return;
        var box = FindBoxForElement(layoutRoot, select);
        if (box == null) return;

        var options = select.Descendants().OfType<DomElement>()
            .Where(o => o.TagName == "option").ToList();
        if (options.Count == 0) return;

        var font = ResolveFieldFont(select);
        if (font == null) return;
        float rowHeight = font.GetHeight(MeasureGraphics) + 2f;
        int index = (int)Math.Floor((y - box.ContentRect.Y) / rowHeight);
        int visibleRows = Math.Max(1, select.GetAttrInt("size", 4));
        if (index < 0 || index >= Math.Min(visibleRows, options.Count)) return;

        var captured = options[index];
        bool toggle = (ModifierKeys & Keys.Control) == Keys.Control;
        if (!toggle)
            foreach (var option in options)
                option.SetAttr("selected", null);

        if (toggle && captured.HasAttr("selected"))
            captured.SetAttr("selected", null);
        else
            captured.SetAttr("selected", "");

        js?.FireEvent(select, "change");
        RequestRerender();
    }

    private void NavigateWithTarget(string absUrl, string? target, FrameView? sourceFrame)
    {
        target = target?.Trim();

        if (string.IsNullOrEmpty(target) || target == "_self")
        {
            if (sourceFrame != null)
            {
                FrameNavigationRequested?.Invoke((this, sourceFrame, absUrl));
                return;
            }
            NavigateRequested?.Invoke(absUrl);
            return;
        }

        if (target == "_top" || target == "_parent")
        {
            NavigateRequested?.Invoke(absUrl);
            return;
        }

        if (target == "_blank")
        {
            OpenNewWindow(absUrl);
            return;
        }

        foreach (var (_, view) in _frames)
        {
            if (string.Equals(view.Name, target, StringComparison.OrdinalIgnoreCase))
            {
                FrameNavigationRequested?.Invoke((this, view, absUrl));
                return;
            }
        }

        OpenNewWindow(absUrl);
    }

    public event Action<(BrowserCanvas Canvas, FrameView Frame, string Url)>? FrameNavigationRequested;

    // ─────────────────────────────────────────────────────────────────────
    // Focus cycling
    // ─────────────────────────────────────────────────────────────────────

    private void MoveFocusToNextControl(int direction)
    {
        if (_document == null || _rootBox == null) return;

        var controls = _document.ElementDescendants()
            .Where(c => c.TagName is "input" or "textarea" or "select" or "button" &&
                        !c.HasAttr("disabled") &&
                        c.GetAttrOrDefault("type", "") != "hidden")
            .ToList();
        if (controls.Count == 0) return;

        int idx = _focusedInput != null ? controls.IndexOf(_focusedInput) : -1;
        int next = (idx + direction) % controls.Count;
        if (next < 0) next += controls.Count;

        var target = controls[next];
        // FIX: tabbing fired no onblur/onfocus at all.
        FocusControl(target, GetFieldText(target).Length);

        var box = FindBoxForElement(_rootBox, target);
        if (box != null)
        {
            if (box.Y < _scrollOffset.Y + 20)
                ScrollTo((int)_scrollOffset.X, (int)box.Y - 20);
            else if (box.Y > _scrollOffset.Y + ClientSize.Height - 40)
                ScrollTo((int)_scrollOffset.X, (int)box.Y - ClientSize.Height + 40);
        }

        Invalidate();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Select dropdown
    // ─────────────────────────────────────────────────────────────────────

    private void ShowSelectDropdown(DomElement select, JsInterpreter? js)
    {
        var options = select.Descendants()
            .OfType<DomElement>()
            .Where(o => o.TagName == "option")
            .ToList();
        if (options.Count == 0) return;
        bool isMultiple = select.HasAttr("multiple");

        // The option the control currently shows — the popup must mark it.
        var currentOpt = options.FirstOrDefault(o => o.HasAttr("selected"))
                      ?? options.FirstOrDefault();

        var menu = new ContextMenuStrip();
        System.Drawing.Font? bold = null;
        foreach (var opt in options)
        {
            var captured = opt;
            var item = (ToolStripMenuItem)menu.Items.Add(
                GlyphSubstituteOptionLabel(opt), null,
                (s, e) =>
                {
                    if (isMultiple)
                    {
                        if (captured.HasAttr("selected"))
                            captured.SetAttr("selected", null);
                        else
                            captured.SetAttr("selected", "");
                    }
                    else
                    {
                        foreach (var o in options)
                            o.SetAttr("selected", null);
                        captured.SetAttr("selected", "");
                    }
                    js?.FireEvent(select, "onchange");
                    RequestRerender();
                });

            if (isMultiple)
                item.Checked = opt.HasAttr("selected");

            // Win95 combobox behaviour: the current value carries the
            // navy highlight bar in the dropped list.
            if (ReferenceEquals(opt, currentOpt))
            {
                // FIX: a new Font was allocated per menu and never disposed.
                bold ??= new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
                item.BackColor = System.Drawing.SystemColors.Highlight;
                item.ForeColor = System.Drawing.SystemColors.HighlightText;
                item.Font = bold;
            }
        }

        // The shared bold font is disposed together with the menu.
        var sharedBold = bold;
        menu.Disposed += (s, e) => sharedBold?.Dispose();

        // Open UNDER the control, left-aligned with its box.
        System.Drawing.Point pt;
        var box = _rootBox != null ? FindBoxForElement(_rootBox, select) : null;
        if (box != null)
        {
            var border = box.BorderRect;
            pt = new System.Drawing.Point(
                Math.Max(0, (int)Math.Round(border.X - _scrollOffset.X)),
                Math.Max(0, (int)Math.Round(border.Bottom - _scrollOffset.Y)));
        }
        else
        {
            pt = PointToClient(MousePosition);
        }

        TrackMenu(menu);
        menu.Show(this, pt);
    }

    private static string GlyphSubstituteOptionLabel(DomElement opt) =>
        Engine.Render.GlyphSubstitution.MapGlyphs(
            (opt.InnerText ?? "").Trim()) is { Length: > 0 } t ? t : " ";

    // ─────────────────────────────────────────────────────────────────────
    // Form control defaults
    // ─────────────────────────────────────────────────────────────────────

    private void RememberDefault(DomElement elem)
    {
        if (_controlDefaults.ContainsKey(elem)) return;

        var def = new ControlDefault();
        switch (elem.TagName)
        {
            case "input":
                def.Value = elem.GetAttr("value");
                def.Checked = elem.HasAttr("checked");
                break;
            case "textarea":
                def.TextAreaText = elem.InnerText ?? "";
                break;
            case "select":
                def.SelectedOptions = elem.Descendants().OfType<DomElement>()
                    .Where(o => o.TagName == "option" && o.HasAttr("selected"))
                    .ToList();
                break;
            default:
                return;
        }
        _controlDefaults[elem] = def;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Forms
    // ─────────────────────────────────────────────────────────────────────

    private static DomElement? FindEnclosingForm(DomElement element) =>
        Engine.Forms.FormSubmitter.FindEnclosingForm(element);

    private void ResetForm(DomElement? form)
    {
        if (form == null) return;
        // FieldsOf includes foster-form controls (the era
        // <form>-between-table-and-rows idiom) that are not tree
        // descendants of the form.
        foreach (var field in Retro96.Engine.Forms.FormSubmitter.FieldsOf(form))
        {
            switch (field.TagName)
            {
                case "input":
                    if (_controlDefaults.TryGetValue(field, out var def))
                    {
                        if (def.Value == null) field.SetAttr("value", null);
                        else field.SetAttr("value", def.Value);
                        if (def.Checked) field.SetAttr("checked", "");
                        else field.SetAttr("checked", null);
                    }
                    else
                    {
                        field.SetAttr("checked", null);
                    }
                    break;
                case "textarea":
                    if (_controlDefaults.TryGetValue(field, out var tdef))
                        SetTextareaText(field, tdef.TextAreaText ?? "");
                    break;
                case "select":
                    var defaults = _controlDefaults.TryGetValue(field, out var sdef)
                        ? sdef.SelectedOptions : null;
                    foreach (var o in field.Descendants().OfType<DomElement>()
                             .Where(o => o.TagName == "option"))
                        o.SetAttr("selected", null);
                    if (defaults != null)
                        foreach (var o in defaults)
                            o.SetAttr("selected", "");
                    break;
            }
        }
    }

    private void SubmitFormInternal(DomElement form, DomDocument document,
                                    JsInterpreter? js,
                                    (string Name, int X, int Y)? imageClick,
                                    FrameView? sourceFrame)
    {
        // Only an explicit `return false` cancels the submit.  FireEvent
        // returns JsValue.Undefined when the form has no onsubmit handler,
        // and Undefined.ToBoolean() is false — so the bare `!ToBoolean()`
        // check aborted EVERY form without an onsubmit (Go / Enter dead).
        var result = js?.FireEvent(form, "onsubmit");
        if (result is { Type: JsType.Boolean } && !result.ToBoolean()) return;

        var req = Engine.Forms.FormSubmitter.BuildRequest(
            form, document.BaseUrl, document.BaseTarget, imageClick);

        if (req.Method == "post")
        {
            FormSubmitRequested?.Invoke((req.Url, req.QueryString, req.Target, sourceFrame));
        }
        else
        {
            string url = string.IsNullOrEmpty(req.QueryString) ? req.Url : $"{req.Url}?{req.QueryString}";
            NavigateWithTarget(url, req.Target, sourceFrame);
        }
    }

    public event Action<(string Url, string Body, string? Target, FrameView? Frame)>? FormSubmitRequested;

    public void SubmitForm(DomElement? form, object? clickCoords)
    {
        if (form == null || _document == null) return;
        SubmitFormInternal(form, _document, _jsInterpreter, null, null);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Image maps
    // ─────────────────────────────────────────────────────────────────────

    private void HandleImageMapClick(DomElement imgElement, DomDocument document,
                                     float x, float y, LayoutBox? layoutRoot)
    {
        string mapName = imgElement.GetAttr("usemap")!.TrimStart('#');
        var map = document.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "map" && e.GetAttr("name") == mapName);
        if (map == null) return;

        var imgBox = layoutRoot != null ? FindBoxForElement(layoutRoot, imgElement) : null;
        if (imgBox == null) return;

        float relX = x - imgBox.X;
        float relY = y - imgBox.Y;

        DomElement? HitArea(bool defaultAreas)
        {
            foreach (var area in map.Children.OfType<DomElement>())
            {
                // FIX: an <area> without href ABORTED the whole search; per
                // spec it's simply inactive.  And "default" areas only match
                // when no specific shape did — they used to shadow real ones.
                if (area.TagName != "area" || !area.HasAttr("href")) continue;
                bool isDefault = area.GetAttrOrDefault("shape", "rect").Trim().ToLowerInvariant() == "default";
                if (isDefault != defaultAreas) continue;
                if (IsPointInArea(relX, relY, area)) return area;
            }
            return null;
        }

        var hit = HitArea(defaultAreas: false) ?? HitArea(defaultAreas: true);
        if (hit == null) return;

        string href = hit.GetAttr("href")!;
        if (href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            try { _jsInterpreter?.ExecuteString(href.TrimStart()[11..]); }
            catch { }
            return;
        }

        if (document.BaseUrl != null)
        {
            try
            {
                string abs = document.BaseUrl.Resolve(href).ToAbsolute();
                NavigateWithTarget(abs, hit.GetAttr("target"), null);
            }
            catch { }
        }
    }

    private static bool IsPointInArea(float x, float y, DomElement area)
    {
        string shape = area.GetAttrOrDefault("shape", "rect").Trim().ToLowerInvariant();
        var coords = (area.GetAttr("coords") ?? "")
            .Split(',')
            .Select(s => float.TryParse(s.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
            .ToArray();

        return shape switch
        {
            // FIX: Math.Pow replaced with multiplies; rect coords normalized
            // (x1>x2 lists are legal and used to fail the contains test).
            "circle" when coords.Length >= 3 =>
                (x - coords[0]) * (x - coords[0]) + (y - coords[1]) * (y - coords[1])
                    <= coords[2] * coords[2],
            "poly" when coords.Length >= 6 => PointInPolygon(x, y, coords),
            "default" => true,
            _ when coords.Length >= 4 =>
                x >= Math.Min(coords[0], coords[2]) && x <= Math.Max(coords[0], coords[2]) &&
                y >= Math.Min(coords[1], coords[3]) && y <= Math.Max(coords[1], coords[3]),
            _ => false
        };
    }

    private static bool PointInPolygon(float x, float y, float[] coords)
    {
        int n = coords.Length / 2;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = coords[2 * i], yi = coords[2 * i + 1];
            float xj = coords[2 * j], yj = coords[2 * j + 1];
            if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi))
                inside = !inside;
        }
        return inside;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Timers
    // ─────────────────────────────────────────────────────────────────────

    private void CheckAndStartTimers()
    {
        // PERF: one DOM pass instead of three full scans per render, with
        // early exit (this runs on every animation tick).
        bool hasBlink = false;
        _hasMarquee = false;
        if (_document != null)
        {
            foreach (var e in _document.ElementDescendants())
            {
                var tag = e.TagName;
                if (tag == "blink") hasBlink = true;
                else if (tag == "marquee") _hasMarquee = true;
                if (hasBlink && _hasMarquee) break;
            }
        }
        _hasAnimatedImages = _imageCache?.HasAnimatedImages ?? false;

        bool needsAnimation = _hasAnimatedImages || _hasMarquee;
        if (needsAnimation) { if (!_animationTimer.Enabled) _animationTimer.Start(); }
        else if (_animationTimer.Enabled) _animationTimer.Stop();

        if (hasBlink) { if (!_blinkTimer.Enabled) _blinkTimer.Start(); }
        else if (_blinkTimer.Enabled) _blinkTimer.Stop();

        // PERF: the JS timer used to run forever at 20 Hz even with no
        // interpreter at all.  It now stops when there's nothing to tick.
        bool needsJsTick = _jsInterpreter != null ||
            _frames.Values.Any(v => v.Interpreter != null);
        if (needsJsTick) { if (!_jsTimer.Enabled) _jsTimer.Start(); }
        else if (_jsTimer.Enabled) _jsTimer.Stop();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_hasAnimatedImages || _hasMarquee)
            RequestRerender();
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkVisible = !_blinkVisible;
        RequestRerender();
    }

    // The text caret is drawn in OnPaint from Environment.TickCount, so making
    // it blink needs a cheap repaint of just that spot — never a page re-render.
    private readonly Timer _caretTimer = new() { Interval = 500 };
    private void OnCaretTick(object? sender, EventArgs e)
    {
        // PERF: a tab-focused button/select used to get its box invalidated
        // twice a second forever — the caret only exists on editable fields.
        if (_focusedInput == null || _rootBox == null || !IsEditableField(_focusedInput))
            return;
        var box = FindBoxForElement(_rootBox, _focusedInput);
        if (box == null) return;
        var br = box.BorderRect;
        var r = new System.Drawing.Rectangle(
            (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y),
            (int)Math.Ceiling(br.Width), (int)Math.Ceiling(br.Height));
        r.Offset(-(int)_scrollOffset.X, -(int)_scrollOffset.Y);
        r.Inflate(2, 2);
        Invalidate(r);
    }

    /// <summary>Disposes a frame view and its nested child views.</summary>
    private static void DisposeFrameView(FrameView view)
    {
        view.Rendered?.Dispose();
        view.Rendered = null;
        foreach (var (_, child) in view.ChildFrames)
            DisposeFrameView(child);
        view.ChildFrames.Clear();
    }

    private void OnJsTimerTick(object? sender, EventArgs e)
    {
        _jsInterpreter?.TickTimers();

        // Frame scripts run on their own interpreters — tick those too, or
        // a setTimeout loop inside an iframe (a clock widget) never fires.
        foreach (var (_, view) in _frames)
            view.Interpreter?.TickTimers();

        var status = _jsInterpreter?.WindowObject?.Get("status");
        if (status?.Type == JsType.String && status.GetString().Length > 0)
            SetStatus(status.GetString());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Shell hooks
    // ─────────────────────────────────────────────────────────────────────

    private void SetStatus(string text)
    {
        // PERF: stop re-firing StatusChanged with the same string on every
        // mouse move / every 50 ms JS tick.
        if (string.Equals(_lastStatus, text, StringComparison.Ordinal)) return;
        _lastStatus = text;
        StatusChanged?.Invoke(text);
    }

    public void NavigateTo(string url) => NavigateRequested?.Invoke(url);
    public void NavigateToReplace(string url) => NavigateRequested?.Invoke(url);
    public void OpenNewWindow(string url) => NewWindowRequested?.Invoke(url);

    public event Action<string>? NewWindowRequested;

    public void UpdateDocumentTitle(string title)
    {
        // FIX: BeginInvoke with a destroyed handle throws into the JS timer.
        if (!IsHandleCreated || IsDisposed) return;
        FindForm()?.BeginInvoke(() =>
        {
            if (FindForm() is Form f)
                f.Text = $"{title} — Retro96";
        });
    }

    private bool _rerenderQueued;

    /// <summary>
    /// Render + paint right now.  Only for press/release feedback where the
    /// state flag can flip back before a queued render would run.
    /// </summary>
    private void RerenderNow()
    {
        if (_fontCache == null || _imageCache == null || _resourceLoader == null)
        {
            Invalidate();
            return;
        }
        ReRenderPage(_fontCache, _imageCache, _resourceLoader);
        Update();
    }

    /// <summary>
    /// Coalesced: any number of requests in one message-loop turn collapse
    /// into a single render.
    /// </summary>
    public void RequestRerender()
    {
        if (_fontCache == null || _imageCache == null || _resourceLoader == null)
        {
            Invalidate();
            return;
        }
        if (_rerenderQueued) return;
        _rerenderQueued = true;

        if (IsHandleCreated && !IsDisposed)
        {
            try
            {
                BeginInvoke(() =>
                {
                    _rerenderQueued = false;
                    if (IsDisposed || _fontCache == null || _imageCache == null || _resourceLoader == null)
                        return;
                    ReRenderPage(_fontCache, _imageCache, _resourceLoader);
                });
            }
            catch (InvalidOperationException)
            {
                // FIX: handle destroyed between the check and the invoke —
                // the queue flag used to jam permanently.
                _rerenderQueued = false;
            }
        }
        else
        {
            _rerenderQueued = false;
            ReRenderPage(_fontCache, _imageCache, _resourceLoader);
        }
    }

    public void PrefetchImage(string url)
    {
        if (_resourceLoader == null || _imageCache == null || _document?.BaseUrl == null)
            return;
        try
        {
            string abs = ImageCache.ResolveUrl(url, _document.BaseUrl.ToAbsolute());
            _ = _imageCache.GetAsync(abs, _resourceLoader, default);
        }
        catch { }
    }

    public Bitmap? RenderedBitmap => _renderedBitmap;

    public void ScrollToAnchor(string anchorName)
    {
        if (_rootBox == null || _document == null) return;

        var target = _document.ElementDescendants()
            .FirstOrDefault(e => e.TagName == "a" &&
                (e.GetAttr("name") ?? "").Equals(anchorName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;

        var box = FindBoxForElement(_rootBox, target);

        // Empty named anchors (<a name="main"></a> — every anchor on the
        // CSS1-era pages) generate no box of their own: the anchor is a
        // POSITION in the flow, not content.  Walk up the ancestors and
        // scroll to the nearest one that DID get a box.
        if (box == null)
        {
            for (var ancestor = target.Parent as DomElement;
                 ancestor != null && box == null;
                 ancestor = ancestor.Parent as DomElement)
            {
                box = FindBoxForElement(_rootBox, ancestor);
                if (box != null && box.BoxType == BoxType.Inline && box.Width <= 0)
                    box = null;      // a flattened stray wrapper — keep climbing
            }
        }

        if (box != null)
            ScrollTo((int)_scrollOffset.X, Math.Max(0, (int)box.Y - 10));
    }

    private static void SetTextareaText(DomElement elem, string text)
    {
        var textNode = elem.Children.OfType<DomText>().FirstOrDefault();
        if (textNode == null)
        {
            textNode = new DomText { Data = text };
            elem.AppendChild(textNode);
        }
        else
        {
            textNode.Data = text;
        }
    }
}