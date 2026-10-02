namespace Retro96;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Retro96.Drawing;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;
using Retro96.Engine.Js;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Render;
using Retro96.Plugins;
using LayoutEngineApi = Retro96.Engine.Layout.LayoutEngine;

/// <summary>
/// Rendering surface + input.  Clicks dispatch on mouse-UP (browser
/// style); Enter/Tab/arrows claimed via IsInputKey so KeyDown fires;
/// double-click selects a word; text fields support a caret, drag
/// selection and Ctrl+A/C/V/X; buttons get a Win95 press-in bevel while
/// held; wheel scrolling is immediate; releasing the mouse OFF a pressed
/// button cancels activation (browser behaviour).
/// </summary>
public class BrowserCanvas : SKGLControl
{
    private DomDocument? _document;
    private LayoutBox? _rootBox;
    private JsInterpreter? _jsInterpreter;
    private ImageCache? _imageCache;
    private ImageCache? _imageEventSource;
    private FontCache? _fontCache;
    private ResourceLoader? _resourceLoader;

    // Top-level scrolling is drawn by Skia on the same GPU surface as the page.
    // The WinForms scrollbar controls were a CPU/GDI child window and were also
    // a source of handle tearing/glitching while the GL surface was repainting.
    private const float TopScrollbarExtent = 16f;
    private bool _showVerticalScrollbar;
    private bool _showHorizontalScrollbar;
    // Visibility is decided only when layout/viewport geometry is dirty.
    // A repaint after a committed zoom must not re-solve the two-axis
    // scrollbar dependency from the same layout and flip H on/off.
    private bool _scrollbarVisibilityDirty = true;
    private enum TopScrollbarAxis { Vertical, Horizontal }
    private TopScrollbarAxis? _topScrollbarDragAxis;
    private float _topScrollbarGrabOffset;

    // Display lists make scrolling a transform-only operation: the document is
    // recorded once as an SKPicture and the GPU replays that command stream at
    // the current scroll offset. No page raster is rebuilt per wheel tick.
    private SKPicture? _rootDisplayList;
    private readonly Dictionary<FrameView, SKPicture> _frameDisplayLists = new();
    private readonly List<LayoutBox> _rootDynamicEmbeds = new();
    private bool _rootDynamicEmbedsCached;
    private readonly List<LayoutBox> _rootAnimatedSubtrees = new();
    private bool _rootAnimatedSubtreesCached;
    private readonly List<LayoutBox> _rootAnimatedImages = new();
    private bool _rootAnimatedImagesCached;
    private readonly Dictionary<FrameView, List<LayoutBox>> _frameDynamicEmbeds = new();
    private readonly Dictionary<FrameView, List<LayoutBox>> _frameAnimatedSubtrees = new();
    private readonly Dictionary<FrameView, List<LayoutBox>> _frameAnimatedImages = new();
    private bool _displayListsDirty = true;
    private bool _documentContentExtentDirty = true;
    private IntPtr _displayListGpuContextHandle;
    private Renderer? _animationRenderer;
    private SizeF _documentContentExtent = SizeF.Empty;
    private PointF _scrollOffset = PointF.Empty;

    // Kept only as non-visual compatibility objects for existing code paths.
    // They are never added to the control tree, so they cannot participate in
    // painting or steal frames from the GPU surface.
    private readonly VScrollBar _vScroll = new();
    private readonly HScrollBar _hScroll = new();

    // <marquee>/<blink> and animated GIFs stay on the GPU fast path: cached
    // display lists are preserved and only animated content is replayed. The cadence
    // follows the refresh rate of the display hosting this control rather than
    // being hard-coded to 60 Hz. WinForms timers are only a scheduling hint;
    // the GL surface's normal presentation remains responsible for VSync.
    private readonly Timer _animationTimer = new() { Interval = 16 };
    private double _animationFramePeriodMs = 1000.0 / 60.0;
    private long _animationNextDueTicks;
    private int _animationRefreshRate = 60;
    private string _animationDisplayDevice = string.Empty;
    private readonly Timer _blinkTimer = new() { Interval = 500 };
    private readonly Timer _jsTimer = new() { Interval = 16 };
    private readonly Timer _resizeReflowTimer = new() { Interval = 150 };
    private readonly List<ContextMenuStrip> _contextMenus = new();

    private const int DeviceCapsVRefresh = 116;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDeviceCaps(IntPtr hDC, int index);

    private bool _blinkVisible = true;
    private DomElement? _lastHoveredElement;
    private FrameView? _lastHoveredFrame;

    // Focused text field only: editable <input> or <textarea>.
    // Other form controls never enter this text-editing focus mode.
    private DomElement? _focusedInput;
    // Owning frame of the focused editable field. Null means the top-level page.
    private FrameView? _focusedInputFrame;

    // Value snapshot at focus time — drives the onchange-on-blur contract.
    private string? _fieldValueAtFocus;

    // In-field editing: caret + selection (character indices into the field text)
    private int _fieldCaret;
    private int _fieldSelAnchor;
    private bool _fieldDragging;
    private float _fieldScrollX;
    // Last pointer position for an active field drag, kept in the field's
    // logical coordinate space.  This lets a drag keep selecting when the
    // pointer reaches the edge of a horizontally scrollable input instead of
    // clamping the caret at the last visible character forever.
    private float _fieldDragPointerX;
    private float _fieldDragPointerY;
    private FrameView? _fieldDragPointerFrame;
    private readonly Timer _fieldDragAutoScrollTimer = new() { Interval = 30 };
    // Find navigation may target a horizontal position inside an unfocused
    // single-line control. Keep that view per element so the highlight remains
    // attached while the control is horizontally scrolled or clipped.
    private readonly Dictionary<DomElement, float> _fieldFindScrollXs = new();
    // Once the user explicitly clicks back into a field after Find has moved its
    // internal viewport, that manual/focus action owns the viewport.  Background
    // Find refreshes must not immediately force the old match back into view.
    private readonly HashSet<DomElement> _findFieldViewportUserOverrides = new();
    private int _textareaScrollLine;
    // Textarea scrolling belongs to the DOM control, not to focus. Keep it
    // here so blur/unfocus can repaint the field at the same viewport without
    // showing the editing scrollbar.
    private readonly Dictionary<DomElement, int> _textareaScrollLines = new();
    private readonly Dictionary<DomElement, float> _textareaScrollXs = new();
    private bool _textareaScrollbarDragging;
    private FrameView? _textareaScrollbarDragFrame;
    private float _textareaScrollbarGrabOffset;

    // Multiple-select listboxes have a real in-control vertical scrollbar.
    // Keep the DOM control's scroll position separate from the page scroll.
    private readonly Dictionary<DomElement, int> _selectScrollOffsets = new();
    private bool _selectScrollbarDragging;
    private DomElement? _selectScrollbarDragSelect;
    private FrameView? _selectScrollbarDragFrame;
    private float _selectScrollbarGrabOffset;

    // Pressed button (Win95 bevel animation)
    private DomElement? _pressedControl;
    private FrameView? _pressedControlFrame;

    // Page text selection
    private LayoutBox? _selAnchor, _selFocus;
    // Character offsets inside the anchor/focus text runs.  Selection state is
    // expressed in text coordinates rather than whole layout boxes so partial
    // word/line selections do not paint the surrounding whitespace or padding.
    private int _selAnchorOffset, _selFocusOffset;
    private FrameView? _selectionFrame;
    private LayoutBox? _pendingSelectionAnchor;
    private int _pendingSelectionAnchorOffset;
    private FrameView? _pendingSelectionFrame;
    private System.Drawing.Point _selStart;
    // Ctrl+A page selection is a document-wide visual selection rather than
    // just an endpoint range.  Keeping this explicit lets form controls and
    // every table/text fragment participate without abusing a pair of text
    // boxes that cannot represent replaced controls.
    private bool _selectAllPage;
    private bool _selecting;
    private bool _dragMoved;
    private bool _suppressNextMouseUp;
    private bool _suppressNextMouseDoubleClick;
    private DomElement? _selectionSemanticScope;
    private FrameView? _selectionSemanticScopeFrame;
    // Layout boxes do not change until a relayout swaps the root tree. Cache the
    // source-order selectable boxes so mouse selection and painting do not rebuild
    // a full-page list on every pointer event.
    private LayoutBox? _selectionRangeCacheRoot;
    private List<LayoutBox>? _selectionRangeCache;
    private LayoutBox? _lastTextClickBox;
    // Layout boxes are rebuilt when hover/active CSS is resolved. Do not use
    // the box reference itself to recognize a double/triple click; keep the
    // stable DOM element and frame identity instead.
    private DomElement? _lastTextClickElement;
    private FrameView? _lastTextClickFrame;
    private System.Drawing.Point _lastTextClickPoint;
    private long _lastTextClickTicks;
    private int _textClickCount;

    // Editable-field click tracking is separate from page-text selection.
    // A custom canvas does not get native TextBox selection semantics, so we
    // reproduce the familiar double-click word / triple-click all behaviour
    // here for both <input> and <textarea>.
    private DomElement? _lastFieldClickElement;
    private FrameView? _lastFieldClickFrame;
    private System.Drawing.Point _lastFieldClickPoint;
    private long _lastFieldClickTicks;
    private int _fieldClickCount;

    private DomElement? _contextElement;
    private ContextMenuStrip? _openSelectMenu;

    // In-page find state is backed by rendered text runs rather than just
    // Element.InnerText. This avoids duplicate matches on nested containers
    // and lets the active occurrence be highlighted precisely even when the
    // layout engine has split a line into several text boxes.
    private string _findText = string.Empty;
    private bool _findCaseSensitive;
    private bool _findWrap = true;
    private readonly List<FindMatch> _findMatches = new();
    private int _findIndex = -1;

    // Layout zoom (buttons/menu/plugins) changes the CSS viewport and reflows
    // the document. Gesture zoom (Ctrl+wheel) is a temporary pointer-anchored
    // visual transform and never changes the committed control zoom.
    private float _layoutZoom = 1f;
    private float _gestureZoom = 1f;
    // Gesture zoom is a viewport transform, not document scrolling. These
    // offsets are retained for compatibility with the paint path, but gesture
    // zoom now keeps them at zero and uses the normal document scroll offset.
    private float _gesturePanX;
    private float _gesturePanY;
    private System.Drawing.Point _lastGesturePoint;
    // After an anchored zoom, the first pointer move should be paint-only.
    // Legacy :hover scripts/CSS can otherwise enqueue a second layout in the
    // same message-loop turn as the zoom reflow, making the page appear to
    // jump only when the cursor moves.
    private bool _skipHoverRelayoutOnceAfterZoom;
    private long _zoomInteractionStabilizeUntilTicks;
    private bool IsZoomInteractionStabilizing => Environment.TickCount64 < _zoomInteractionStabilizeUntilTicks;
    private bool IsGestureZoomActive => Math.Abs(_gestureZoom - 1f) > 0.0005f;
    private float EffectiveZoom => Math.Clamp(_layoutZoom * _gestureZoom, 0.25f, 4f);

    private DomElement? _embeddedMidiElement;
    private string _embeddedMidiLabel = "MIDI";
    private bool _embeddedMidiPlaying;
    private bool _embeddedMidiLoop;
    private int _embeddedMidiVolume = 100;
    private RectangleF _embeddedMidiRect;
    private string? _embeddedMidiPressedAction;
    private bool _embeddedMidiVolumeDragging;
    private DomElement? _focusedEmbeddedElement;
    private DomElement? _pressedEmbeddedElement;
    private readonly Retro96.Engine.Java.JavaAppletHost _javaApplets;
    private DomElement? _focusedJavaAppletElement;
    private DomElement? _pressedJavaAppletElement;
    private DomElement? _hoveredJavaAppletElement;

    // Internal callbacks are fields rather than Control properties so WinForms
    // designer serialization never tries to persist delegates from the host.
    internal Func<DomElement, LayoutBox, bool, Bitmap?>? EmbeddedFrameResolver;
    internal Func<SKCanvas, DomElement, LayoutBox, bool, bool>? EmbeddedCanvasResolver;
    internal Action<DomElement, EmbeddedInputEvent>? EmbeddedInputDispatcher;
    internal Func<DomElement, (string ScriptName, IReadOnlyList<string> Methods)?>? EmbeddedScriptInfoResolver;
    internal Func<DomElement, string, IReadOnlyList<Retro96.Plugins.JsValue>, Task<Retro96.Plugins.JsValue>>? EmbeddedScriptCall;

    private readonly Dictionary<LayoutBox, FrameView> _frames = new();
    private readonly Dictionary<DomElement, int> _selectRangeAnchors = new();
    private LayoutBox? _focusedFrame;
    private bool _showBoxOutlines;

    // Legacy frame/iframe scrollbars are painted inside the frame itself.
    // WinForms scrollbars are reserved for the top-level page, so frame
    // scrollbars need their own hit-testing and thumb-drag state.
    private enum FrameScrollbarAxis { Vertical, Horizontal }
    private LayoutBox? _frameScrollbarDragBox;
    private FrameView? _frameScrollbarDragView;
    private FrameScrollbarAxis _frameScrollbarDragAxis;
    private float _frameScrollbarDragStartPointer;
    private float _frameScrollbarDragStartScroll;
    private bool _frameScrollbarMouseDownHandled;

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

    // Skia screen composer; presentation stays on the SKControl canvas.

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
    public event Action<float>? ZoomChanged;
    public event Action<int, int>? FindChanged;
    public event Action? BackRequested;
    public event Action? ForwardRequested;
    public event Action? ReloadRequested;
    public event Action? PageChanged;
    public event Action<System.Windows.Forms.ContextMenuStrip, Retro96.Plugins.ContextMenuContext>? PluginContextMenuRequested;
    public event Action<string>? EmbeddedMidiControlRequested;
    public event Action<string>? ExternalProtocolRequested;

    public LayoutBox? RootBox => _rootBox;
    public DomDocument? PageDocument => _document;
    public int FindMatchCount => _findMatches.Count;
    public int FindCurrentIndex => _findIndex >= 0 && _findIndex < _findMatches.Count ? _findIndex + 1 : 0;
    public bool ShowBoxOutlines => _showBoxOutlines;
    public ResourceLoader? ResourceLoader => _resourceLoader;

    public enum FrameScrollMode { Auto, Yes, No }

    private readonly record struct FindMatchSegment(
        FrameView? Frame,
        LayoutBox Box,
        int Start,
        int Length,
        DomElement? SourceElement = null);

    private readonly record struct FindMatch(
        FrameView? Frame,
        LayoutBox Box,
        int Start,
        int Length,
        DomElement? SourceElement = null,
        IReadOnlyList<FindMatchSegment>? Segments = null);

    public sealed class FrameView
    {
        public required DomDocument Document;
        public required LayoutBox RootBox;
        public SizeF ContentSize = SizeF.Empty;
        public PointF Scroll = PointF.Empty;
        public string Name = "";
        public string Url = "";
        public FrameScrollMode ScrollMode = FrameScrollMode.Auto;
        public bool ScrollingEnabled = true;
        public bool FrameBorder = true;
        public bool NoResize;
        public int MarginWidth = -1;
        public int MarginHeight = -1;

        /// <summary>
        /// Per-frame JS interpreter (frames carry their own scripting
        /// context — timers scheduled by frame scripts are driven by the
        /// canvas's JS timer tick alongside the page's own interpreter).
        /// </summary>
        public Retro96.Engine.Js.JsInterpreter? Interpreter;

        /// <summary>
        /// Frames/iframes nested INSIDE this frame's document. Their boxes
        /// live in this frame's own coordinate space and are composed directly
        /// into the active GPU canvas, avoiding page-level bitmap staging.
        /// </summary>
        public List<(LayoutBox Box, FrameView View)> ChildFrames = new();
    }

    public BrowserCanvas()
    {
        _javaApplets = new Retro96.Engine.Java.JavaAppletHost();
        _javaApplets.RepaintRequested = RequestRerender;
        _javaApplets.NavigateRequested = NavigateTo;
        _javaApplets.StatusChanged = SetStatus;
        TabStop = true;

        // Top-level scrollbars are GPU-painted overlays. Do not add WinForms
        // VScrollBar/HScrollBar child windows: their GDI repaint path can tear
        // over an OpenGL-backed SKGLControl during high-frequency scrolling.
        _vScroll.Scroll += OnVScroll;
        _hScroll.Scroll += OnHScroll;
        _vScroll.Visible = false;
        _hScroll.Visible = false;
        _vScroll.TabStop = false;
        _hScroll.TabStop = false;

        _animationTimer.Tick += OnAnimationTick;
        _blinkTimer.Tick += OnBlinkTick;
        _jsTimer.Tick += OnJsTimerTick;
        _resizeReflowTimer.Tick += OnResizeReflowTick;
        _caretTimer.Tick += OnCaretTick;
        _fieldDragAutoScrollTimer.Tick += OnFieldDragAutoScrollTick;
        UpdateAnimationRefreshRate();
        _caretTimer.Start();

        KeyDown += OnDevToolsKeyDown;
        KeyDown += OnEmbeddedKeyDown;
        KeyUp += OnEmbeddedKeyUp;
        KeyPress += OnCanvasKeyPress;
        Enter += (_, _) => SendEmbeddedFocus(true);
        Leave += (_, _) => SendEmbeddedFocus(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateAnimationRefreshRate();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateAnimationRefreshRate();
    }

    private void UpdateAnimationRefreshRate()
    {
        int refresh = 0;
        string displayDevice = string.Empty;
        if (IsHandleCreated && !IsDisposed)
        {
            try { displayDevice = Screen.FromControl(this).DeviceName; } catch { }

            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = GetDC(Handle);
                if (hdc != IntPtr.Zero)
                    refresh = GetDeviceCaps(hdc, DeviceCapsVRefresh);
            }
            catch
            {
                refresh = 0;
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    try { ReleaseDC(Handle, hdc); } catch { }
                }
            }
        }

        if (refresh < 24 || refresh > 360)
            refresh = 60; // Safe fallback when the display driver cannot report VREFRESH.

        _animationDisplayDevice = displayDevice;
        if (refresh == _animationRefreshRate && Math.Abs(_animationFramePeriodMs - 1000.0 / refresh) < 0.001)
            return;

        _animationRefreshRate = refresh;
        _animationFramePeriodMs = 1000.0 / refresh;
        int displayTickMs = Math.Max(1, (int)Math.Round(_animationFramePeriodMs));
        _animationTimer.Interval = displayTickMs;
        // JS timers are still governed by each timer's own wall-clock due time;
        // this only increases the shell's polling resolution so status-bar
        // scrollers and other short-period legacy scripts are not quantized to
        // a fixed 50 ms tick. The script timer never runs more often than its
        // requested delay.
        _jsTimer.Interval = displayTickMs;
        _animationNextDueTicks = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_imageEventSource != null)
            {
                _imageEventSource.ImageLoaded -= OnImageLoaded;
                _imageEventSource = null;
            }
            _animationTimer.Dispose();
            _blinkTimer.Dispose();
            _jsTimer.Dispose();
            _resizeReflowTimer.Dispose();
            _caretTimer.Dispose();
            _fieldDragAutoScrollTimer.Dispose();
            _measureGfx?.Dispose();
            DisposeDisplayLists();

            foreach (var frame in _frames.Values)
                DisposeFrameView(frame);
            _frames.Clear();

            foreach (var menu in _contextMenus.ToArray())
            {
                try { menu.Dispose(); } catch { /* already torn down */ }
            }
            _contextMenus.Clear();

            _controlDefaults.Clear();
            _textareaScrollLines.Clear();
            _textareaScrollXs.Clear();
            _fieldFindScrollXs.Clear();
            _textareaScrollbarDragging = false;
            _textareaScrollbarDragFrame = null;
            _selectScrollOffsets.Clear();
            _selectScrollbarDragging = false;
            _selectScrollbarDragSelect = null;
            _selectScrollbarDragFrame = null;
            _focusedInput = null;
            _focusedInputFrame = null;
            _lastHoveredElement = null;
            _taCacheLines = null;
            _taCacheText = null;
            _taCacheFont = null;
            _javaApplets.Dispose();
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

    private void DisposeDisplayLists()
    {
        _rootDisplayList?.Dispose();
        _rootDisplayList = null;
        foreach (var picture in _frameDisplayLists.Values)
            picture.Dispose();
        _frameDisplayLists.Clear();
        _rootDynamicEmbeds.Clear();
        _rootDynamicEmbedsCached = false;
        _rootAnimatedSubtrees.Clear();
        _rootAnimatedSubtreesCached = false;
        _rootAnimatedImages.Clear();
        _rootAnimatedImagesCached = false;
        _frameDynamicEmbeds.Clear();
        _frameAnimatedSubtrees.Clear();
        _frameAnimatedImages.Clear();
        _displayListsDirty = true;
        _documentContentExtentDirty = true;
    }

    private void InvalidateDisplayLists()
    {
        _rootDisplayList?.Dispose();
        _rootDisplayList = null;
        foreach (var picture in _frameDisplayLists.Values)
            picture.Dispose();
        _frameDisplayLists.Clear();
        _rootDynamicEmbeds.Clear();
        _rootDynamicEmbedsCached = false;
        _rootAnimatedSubtrees.Clear();
        _rootAnimatedSubtreesCached = false;
        _rootAnimatedImages.Clear();
        _rootAnimatedImagesCached = false;
        _frameDynamicEmbeds.Clear();
        _frameAnimatedSubtrees.Clear();
        _frameAnimatedImages.Clear();
        _displayListsDirty = true;
        _documentContentExtentDirty = true;
    }

    private static List<LayoutBox> CollectDynamicEmbeddedContent(LayoutBox root)
    {
        var result = new List<LayoutBox>();
        foreach (var box in root.Descendants())
        {
            if (box.BoxType != BoxType.Replaced || box.Element == null ||
                box.Width <= 0f || box.Height <= 0f)
                continue;

            string tag = box.Element.TagName;
            if (tag.Equals("applet", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("object", StringComparison.OrdinalIgnoreCase))
                result.Add(box);
        }
        return result;
    }

    private static bool IsEnabledAnimatedElement(DomElement? element)
    {
        if (element == null) return false;
        for (DomNode? node = element; node != null; node = node.Parent)
        {
            if (node is DomElement de)
            {
                if (BrowserRuntime.BlinkEnabled && de.TagName == "blink") return true;
                if (BrowserRuntime.MarqueeEnabled && de.TagName == "marquee") return true;
            }
        }
        return false;
    }

    private static List<LayoutBox> CollectAnimatedSubtrees(LayoutBox root)
    {
        var result = new List<LayoutBox>();
        CollectAnimatedSubtreesCore(root, ancestorAnimated: false, result);
        return result;
    }

    private static void CollectAnimatedSubtreesCore(LayoutBox box, bool ancestorAnimated,
                                                     List<LayoutBox> result)
    {
        bool animated = ancestorAnimated || IsEnabledAnimatedElement(box.Element);
        if (animated && !ancestorAnimated)
            result.Add(box);

        foreach (var child in box.Children)
            CollectAnimatedSubtreesCore(child, animated, result);
    }

    private List<LayoutBox> CollectAnimatedImageBoxes(LayoutBox root, DomDocument document)
    {
        var result = new List<LayoutBox>();
        if (_imageCache == null) return result;

        foreach (var box in root.Descendants())
        {
            var element = box.Element;
            if (element == null || box.BoxType != BoxType.Replaced ||
                !element.TagName.Equals("img", StringComparison.OrdinalIgnoreCase))
                continue;

            string? src = element.GetAttr("src");
            if (string.IsNullOrWhiteSpace(src)) continue;

            string absolute;
            try { absolute = ImageCache.ResolveUrl(src, document.BaseUrl?.ToAbsolute()); }
            catch { continue; }

            // GIFs inside <marquee>/<blink> are already replayed through the
            // animated-subtree path, which preserves their transform/visibility.
            // Keep them out of the standalone animated-image overlay.
            if (_imageCache.IsAnimated(absolute) && !IsEnabledAnimatedElement(element))
                result.Add(box);
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    private void BindImageCache(ImageCache images)
    {
        if (ReferenceEquals(_imageEventSource, images)) return;
        if (_imageEventSource != null)
            _imageEventSource.ImageLoaded -= OnImageLoaded;

        _imageEventSource = images;
        _imageEventSource.ImageLoaded += OnImageLoaded;
    }

    private void OnImageLoaded(string absoluteUrl)
    {
        // Image decoding finishes asynchronously, often after the first
        // display list has already been recorded. An animated GIF must be
        // promoted to the dynamic-image path exactly once at that boundary;
        // afterwards every animation frame is composited without re-recording
        // the whole page.
        if (string.IsNullOrEmpty(absoluteUrl) || IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                InvalidateDisplayLists();
                Invalidate();
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void SetResourceLoader(ResourceLoader loader)
    {
        _resourceLoader = loader ?? throw new ArgumentNullException(nameof(loader));
        _animationRenderer = null;
    }

    public void SetPage(DomDocument doc, LayoutBox rootBox,
                        JsInterpreter js, FontCache fontCache, ImageCache images)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(rootBox);

        // Dispose nested frame state before installing the new page.
        foreach (var frame in _frames.Values)
            DisposeFrameView(frame);
        _frames.Clear();
        _focusedFrame = null;

        // Focus is dropped silently on navigation — running the old page's
        // blur/change handlers from inside SetPage re-enters navigation.
        _focusedInput = null;
        _focusedInputFrame = null;
        _focusedEmbeddedElement = null;
        _pressedEmbeddedElement = null;
        _focusedJavaAppletElement = null;
        _pressedJavaAppletElement = null;
        _hoveredJavaAppletElement = null;
        _fieldDragging = false;
        _lastFieldClickElement = null;
        _lastFieldClickFrame = null;
        _fieldClickCount = 0;
        var focusDoc = _document;
        if (focusDoc != null)
            UpdateCssInteractionState(focusDoc, focusDoc.HoveredElement, focusDoc.ActiveElement, null);
        _fieldValueAtFocus = null;
        _pressedControl = null;
        _pressedControlFrame = null;
        _controlDefaults.Clear();
        _textareaScrollLines.Clear();
        _textareaScrollXs.Clear();
        _fieldFindScrollXs.Clear();
        _textareaScrollbarDragging = false;
        _textareaScrollbarDragFrame = null;
        _selectScrollOffsets.Clear();
        _selectScrollbarDragging = false;
        _selectScrollbarDragSelect = null;
        _selectScrollbarDragFrame = null;
        _selectRangeAnchors.Clear();
        _contextElement = null;

        ClearFindState();
        ClearPageSelection();
        _selecting = false;
        _lastTextClickBox = null;
        _lastTextClickElement = null;
        _lastTextClickFrame = null;
        _lastTextClickTicks = 0;
        _textClickCount = 0;
        _suppressNextMouseDoubleClick = false;
        _selectionSemanticScope = null;
        _selectionSemanticScopeFrame = null;

        InvalidateDisplayLists();
        _document = doc;
        _rootBox = rootBox;
        _jsInterpreter = js;
        _fontCache = fontCache;
        BindImageCache(images);
        _imageCache = images;
        _animationRenderer = null;

        _scrollOffset = PointF.Empty;
        _gestureZoom = 1f;
        _gesturePanX = 0f;
        _gesturePanY = 0f;
        _lastGesturePoint = System.Drawing.Point.Empty;
        _skipHoverRelayoutOnceAfterZoom = false;
        _scrollbarVisibilityDirty = true;
        _lastHoveredElement = null;

        _taCacheText = null;
        _taCacheLines = null;
        _taCacheFont = null;

        // Page-level animation/blink timers stop until the next render
        // re-evaluates them (they used to keep ticking against the OLD page).
        _animationTimer.Stop();
        _blinkTimer.Stop();

        _lastStatus = "";
        _ = _javaApplets.PreparePageAsync(doc, _resourceLoader ?? throw new InvalidOperationException("Resource loader not configured"));
        PageChanged?.Invoke();
    }

    public void ApplyRelayout(DomDocument document, LayoutBox newRoot)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (newRoot == null) return;

        InvalidateDisplayLists();
        _document = document;
        _scrollbarVisibilityDirty = true;
        RemapFramesToNewRoot(newRoot);
        _rootBox = newRoot;
        foreach (var (frameBox, frameView) in _frames.ToArray())
            UpdateFrameMetrics(frameBox, frameView);

        // Image-load reflow rebuilds the tree — remap the selection onto
        // the new boxes instead of clearing it.
        RemapSelection(newRoot);
        RefreshFindMatches(preserveCurrent: true);

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
        ReflowDocumentToStableViewport();
    }

    public bool ApplyEditedSource(string markup)
    {
        if (_document?.BaseUrl == null || _jsInterpreter == null || _fontCache == null || _imageCache == null)
            return false;

        try
        {
            var document = Engine.Html.HtmlParser.Parse(markup ?? "", _document.BaseUrl, _document.Cookies);
            var viewport = GetLayoutViewportSize();
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
    {
        if (!BrowserRuntime.JavaScriptDialogsEnabled) return;
        MessageBox.Show(FindForm(), message, "Retro96", MessageBoxButtons.OK);
    }

    public virtual bool ShowConfirm(string message)
    {
        if (!BrowserRuntime.JavaScriptDialogsEnabled) return false;
        return MessageBox.Show(FindForm(), message, "Retro96", MessageBoxButtons.YesNo) == DialogResult.Yes;
    }

    public virtual string? ShowPrompt(string message, string defaultValue)
    {
        if (!BrowserRuntime.JavaScriptDialogsEnabled) return null;
        return PromptDialog.Show(FindForm(), message, defaultValue);
    }

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
        _selAnchorOffset = _selAnchor == null ? 0 :
            Math.Clamp(_selAnchorOffset, 0, GetSelectionBoxTextLength(_selAnchor));
        _selFocusOffset = _selFocus == null ? 0 :
            Math.Clamp(_selFocusOffset, 0, GetSelectionBoxTextLength(_selFocus));
        if (_selAnchor == null || _selFocus == null ||
            IsSelectionExcludedElement(_selAnchor?.Element) ||
            IsSelectionExcludedElement(_selFocus?.Element))
            ClearPageSelection();
    }

    private void RemapFrameSelection(FrameView frameView, LayoutBox newRoot)
    {
        if (ReferenceEquals(_selectionFrame, frameView))
        {
            var oldAnchor = _selAnchor;
            var oldFocus = _selFocus;
            _selAnchor = FindMatchingBox(newRoot, oldAnchor);
            _selFocus = FindMatchingBox(newRoot, oldFocus);
            _selAnchorOffset = _selAnchor == null ? 0 :
                Math.Clamp(_selAnchorOffset, 0, GetSelectionBoxTextLength(_selAnchor));
            _selFocusOffset = _selFocus == null ? 0 :
                Math.Clamp(_selFocusOffset, 0, GetSelectionBoxTextLength(_selFocus));
            if (_selAnchor == null || _selFocus == null ||
                IsSelectionExcludedElement(_selAnchor?.Element) ||
                IsSelectionExcludedElement(_selFocus?.Element))
                ClearPageSelection();
        }

        if (ReferenceEquals(_pendingSelectionFrame, frameView) && _pendingSelectionAnchor != null)
        {
            _pendingSelectionAnchor = FindMatchingBox(newRoot, _pendingSelectionAnchor);
            _pendingSelectionAnchorOffset = _pendingSelectionAnchor == null ? 0 :
                Math.Clamp(_pendingSelectionAnchorOffset, 0, GetSelectionBoxTextLength(_pendingSelectionAnchor));
            if (_pendingSelectionAnchor == null)
                _pendingSelectionFrame = null;
        }
    }

    private static LayoutBox? FindMatchingBox(LayoutBox root, LayoutBox? old)
    {
        if (old?.Element == null) return null;

        // Layout boxes are disposable: hover/focus changes can rebuild the
        // entire tree while a page selection is still active.  Recover the
        // endpoint from stable DOM identity plus text/position rather than
        // assuming the first matching fragment is the same one.  This is
        // especially important for wrapped text and repeated identical runs.
        string text = old.TextRun ?? "";
        bool oldIsControl = old.BoxType == BoxType.Replaced &&
            IsPageSelectableControl(old.Element);
        var candidates = root.Descendants()
            .Where(b => ReferenceEquals(b.Element, old.Element) &&
                        (!string.IsNullOrEmpty(b.TextRun) ||
                         (oldIsControl && b.BoxType == BoxType.Replaced &&
                          IsPageSelectableControl(b.Element))))
            .ToList();
        if (candidates.Count == 0) return null;

        if (oldIsControl)
        {
            return candidates
                .OrderBy(b => Math.Abs(b.X - old.X) + Math.Abs(b.Y - old.Y))
                .FirstOrDefault();
        }

        var exact = candidates.Where(b => b.TextRun == text).ToList();
        if (exact.Count == 0) exact = candidates;

        return exact
            .OrderBy(b => Math.Abs(b.X - old.X) + Math.Abs(b.Y - old.Y))
            .FirstOrDefault();
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
            // Re-resolve the frame document against its new CSS viewport before
            // rebuilding it. Zoom/resize changes a frame's logical width, so
            // percentage sizes, wrapping and other viewport-dependent styles
            // must be recalculated rather than reusing the old frame layout.
            if (newBox.Width > 0 && newBox.Height > 0)
            {
                try { Engine.Css.StyleResolver.Resolve(view.Document, Math.Max(1, (int)MathF.Round(newBox.Width))); }
                catch { /* keep the frame document's current styles */ }
                var rebuiltFrameRoot = LayoutEngineApi.BuildLayoutTree(
                    view.Document, Math.Max(1, newBox.Width), Math.Max(1, newBox.Height));
                RemapFrameSelection(view, rebuiltFrameRoot);
                view.RootBox = rebuiltFrameRoot;
            }
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
    /// nested-frame boxes are stale. Re-find each child by DOM identity and
    /// rebuild the child document at its NEW viewport size as well. This keeps
    /// nested frame/iframe layout and selection geometry synchronized with
    /// top-level zoom and resize operations.
    /// </summary>
    private void RemapChildFrames(FrameView view)
    {
        if (view.RootBox == null) return;
        if (view.ChildFrames.Count == 0) return;

        var remapped = new List<(LayoutBox, FrameView)>();
        foreach (var (oldBox, childView) in view.ChildFrames)
        {
            var el = oldBox.Element;
            var newBox = el == null ? null
                : view.RootBox.Descendants()
                    .FirstOrDefault(b => b.BoxType == BoxType.Frame && b.Element == el);
            if (newBox == null)
            {
                DisposeFrameView(childView);
                continue;
            }

            if (newBox.Width > 0 && newBox.Height > 0)
            {
                try { Engine.Css.StyleResolver.Resolve(childView.Document, Math.Max(1, (int)MathF.Round(newBox.Width))); }
                catch { /* keep the frame document's current styles */ }
                var rebuiltChildRoot = LayoutEngineApi.BuildLayoutTree(
                    childView.Document, Math.Max(1, newBox.Width), Math.Max(1, newBox.Height));
                RemapFrameSelection(childView, rebuiltChildRoot);
                childView.RootBox = rebuiltChildRoot;
            }

            RemapChildFrames(childView);
            remapped.Add((newBox, childView));
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
        if (_frames.TryGetValue(frameBox, out var old) && !ReferenceEquals(old, view))
            DisposeFrameView(old);
        _frames[frameBox] = view;
        UpdateFrameMetrics(frameBox, view);
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

    public bool TryFindParentFrame(FrameView target, out FrameView? parent, out LayoutBox? childBox)
    {
        foreach (var (_, view) in _frames)
        {
            if (TryFindParentFrameRecursive(view, target, out parent, out childBox))
                return true;
        }
        parent = null;
        childBox = null;
        return false;
    }

    /// <summary>Find the frame's immediate host, including top-level frames.</summary>
    public bool TryFindFrameHost(FrameView target, out FrameView? parent, out LayoutBox? box)
    {
        foreach (var (topBox, topView) in _frames)
        {
            if (ReferenceEquals(topView, target))
            {
                parent = null; box = topBox; return true;
            }
            if (TryFindFrameHostRecursive(topView, target, out parent, out box))
                return true;
        }
        parent = null; box = null; return false;
    }

    private static bool TryFindFrameHostRecursive(FrameView current, FrameView target,
                                                   out FrameView? parent, out LayoutBox? box)
    {
        foreach (var (childBox, childView) in current.ChildFrames)
        {
            if (ReferenceEquals(childView, target))
            { parent = current; box = childBox; return true; }
            if (TryFindFrameHostRecursive(childView, target, out parent, out box))
                return true;
        }
        parent = null; box = null; return false;
    }

    private bool TryFindFrameViewByDocument(DomDocument document,
                                            out FrameView? view, out FrameView? parent,
                                            out LayoutBox? box)
    {
        foreach (var (topBox, topView) in _frames)
        {
            if (ReferenceEquals(topView.Document, document))
            { view = topView; parent = null; box = topBox; return true; }
            if (TryFindFrameViewByDocumentRecursive(topView, document, out view, out parent, out box))
                return true;
        }
        view = null; parent = null; box = null; return false;
    }

    private static bool TryFindFrameViewByDocumentRecursive(FrameView current, DomDocument document,
                                                              out FrameView? view, out FrameView? parent,
                                                              out LayoutBox? box)
    {
        foreach (var (childBox, childView) in current.ChildFrames)
        {
            if (ReferenceEquals(childView.Document, document))
            { view = childView; parent = current; box = childBox; return true; }
            if (TryFindFrameViewByDocumentRecursive(childView, document, out view, out parent, out box))
                return true;
        }
        view = null; parent = null; box = null; return false;
    }

    public void ClearChildFrames(FrameView view)
    {
        InvalidateDisplayLists();
        foreach (var (_, child) in view.ChildFrames) DisposeFrameView(child);
        view.ChildFrames.Clear();
    }

    public void RecomposeFrameTree(FrameView view)
    {
        if (view == null) { Invalidate(); return; }
        if (TryFindFrameHost(view, out var parent, out var box) && box != null)
        {
            if (parent == null)
                UpdateFrameMetrics(box, view);
            else
                UpdateFrameMetrics(box, view);
        }
        Invalidate();
    }

    private static bool TryFindParentFrameRecursive(FrameView current, FrameView target,
                                                    out FrameView? parent, out LayoutBox? childBox)
    {
        foreach (var (box, child) in current.ChildFrames)
        {
            if (ReferenceEquals(child, target))
            {
                parent = current;
                childBox = box;
                return true;
            }
            if (TryFindParentFrameRecursive(child, target, out parent, out childBox))
                return true;
        }
        parent = null;
        childBox = null;
        return false;
    }

    public System.Drawing.Size GetViewportSize()
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        if (_showVerticalScrollbar) w = Math.Max(0, w - (int)TopScrollbarExtent);
        if (_showHorizontalScrollbar) h = Math.Max(0, h - (int)TopScrollbarExtent);
        return new System.Drawing.Size(w, h);
    }

    /// <summary>CSS viewport used by layout zoom.
    /// This is deliberately different from the physical paint viewport so
    /// text wrapping/flow changes instead of scaling a screenshot. </summary>
    public System.Drawing.Size GetLayoutViewportSize()
        => GetLayoutViewportSizeForScrollbarState(
            _showVerticalScrollbar, _showHorizontalScrollbar, _layoutZoom);

    private System.Drawing.Size GetLayoutViewportSizeForScrollbarState(
        bool verticalScrollbar, bool horizontalScrollbar, float zoom)
    {
        int width = Math.Max(1, ClientSize.Width - (verticalScrollbar ? (int)TopScrollbarExtent : 0));
        int height = Math.Max(1, ClientSize.Height - (horizontalScrollbar ? (int)TopScrollbarExtent : 0));
        float safeZoom = Math.Max(0.25f, float.IsFinite(zoom) ? zoom : 1f);
        return new System.Drawing.Size(
            Math.Max(1, (int)MathF.Floor(width / safeZoom)),
            Math.Max(1, (int)MathF.Floor(height / safeZoom)));
    }

    private static (float Width, float Height) GetDocumentContentExtent(
        LayoutBox rootBox, float viewportWidth, float viewportHeight)
    {
        float width = Math.Max(1f, Math.Max(viewportWidth, rootBox.Width));
        float height = Math.Max(1f, Math.Max(viewportHeight, rootBox.Height));

        foreach (var box in rootBox.Descendants())
        {
            if (ReferenceEquals(box, rootBox)) continue;
            width = Math.Max(width, box.X + Math.Max(0f, box.Width));
            height = Math.Max(height, box.Y + Math.Max(0f, box.Height));
        }

        return (width, height);
    }

    public void SetBoxOutlines(bool show)
    {
        _showBoxOutlines = show;
        RequestRerender();
    }

    public void ClearForNavigation()
    {
        InvalidateDisplayLists();
        _scrollbarVisibilityDirty = true;
        _focusedInput = null;
        _focusedInputFrame = null;
        _fieldDragging = false;
        _fieldValueAtFocus = null;
        _pressedControl = null;
        _pressedControlFrame = null;
        ClearPageSelection();
        _selecting = false;
        _lastTextClickBox = null;
        _lastTextClickElement = null;
        _lastTextClickFrame = null;
        _lastTextClickTicks = 0;
        _textClickCount = 0;
        _lastHoveredElement = null;
        _lastHoveredFrame = null;
        _embeddedMidiElement = null;
        _embeddedMidiPressedAction = null;
        _embeddedMidiVolumeDragging = false;
        _embeddedMidiRect = RectangleF.Empty;
        _focusedJavaAppletElement = null;
        _pressedJavaAppletElement = null;
        _hoveredJavaAppletElement = null;
        ClearFindState();
        _javaApplets.StopPage();
        _scrollOffset = PointF.Empty;
        UpdateScrollBars();
        Invalidate();
    }

    private Bitmap? ResolveEmbeddedContent(DomElement element, LayoutBox box, bool printRendering)
    {
        if (Retro96.Engine.Java.JavaAppletHost.IsJavaElement(element))
            return _javaApplets.Resolve(element, box, printRendering);
        return EmbeddedFrameResolver?.Invoke(element, box, printRendering);
    }

    private bool RenderEmbeddedCanvas(SKCanvas canvas, DomElement element, LayoutBox box, bool printRendering)
    {
        if (Retro96.Engine.Java.JavaAppletHost.IsJavaElement(element) &&
            _javaApplets.RenderToCanvas(canvas, element, box, printRendering, GRContext))
            return true;
        return EmbeddedCanvasResolver?.Invoke(canvas, element, box, printRendering) == true;
    }

    internal void PrepareJavaAppletsAsync(DomDocument document)
    {
        if (_resourceLoader == null) return;
        _ = _javaApplets.PreparePageAsync(document, _resourceLoader);
    }

    public void ReRenderPage(FontCache fontCache, ImageCache imageCache,
                             ResourceLoader resourceLoader)
    {
        if (_rootBox == null || _document == null) return;

        InvalidateDisplayLists();
        foreach (var (box, view) in _frames)
            UpdateFrameMetrics(box, view);

        UpdateScrollBars(recomputeVisibility: _scrollbarVisibilityDirty);
        CheckAndStartTimers();
        Invalidate();
    }

    private void UpdateFrameMetrics(LayoutBox frameBox, FrameView view, int depth = 0)
    {
        if (depth > 8) return;
        if (view.RootBox == null) return;

        try
        {
            var contentSize = GetFrameContentSize(view.RootBox, frameBox.Width, frameBox.Height);
            view.ContentSize = new SizeF(contentSize.Width, contentSize.Height);

            bool overflow = contentSize.Width > frameBox.Width + 0.5f ||
                            contentSize.Height > frameBox.Height + 0.5f;
            view.ScrollingEnabled = view.ScrollMode switch
            {
                FrameScrollMode.Yes => true,
                FrameScrollMode.No => false,
                _ => overflow
            };

            if (!view.ScrollingEnabled)
            {
                view.Scroll = PointF.Empty;
            }
            else
            {
                var metrics = GetFrameScrollMetrics(frameBox, view);
                view.Scroll.X = Math.Clamp(view.Scroll.X, 0f, metrics.MaxScrollX);
                view.Scroll.Y = Math.Clamp(view.Scroll.Y, 0f, metrics.MaxScrollY);
            }
        }
        catch (Exception ex)
        {
            Retro96.DebugLog.WriteException("UpdateFrameMetrics", ex);
        }

        foreach (var (childBox, childView) in view.ChildFrames)
            UpdateFrameMetrics(childBox, childView, depth + 1);
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
        InvalidateDisplayLists();

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

        UpdateFrameMetrics(childBox, childView);
        CheckAndStartTimers();
    }

    /// <summary>Refreshes a child frame's layout metrics; its pixels are painted
    /// directly into the GPU canvas on the next surface paint.</summary>
    public void RefreshChildFrame(FrameView parent, LayoutBox childBox, FrameView childView)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(childBox);
        ArgumentNullException.ThrowIfNull(childView);
        UpdateFrameMetrics(childBox, childView);
        if (_frameDisplayLists.Remove(childView, out var oldPicture))
            oldPicture.Dispose();
        _frameDynamicEmbeds.Remove(childView);
        _frameAnimatedSubtrees.Remove(childView);
        Invalidate();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Painting
    // ─────────────────────────────────────────────────────────────────────

    private Renderer? CreateRenderer()
    {
        if (_fontCache == null || _imageCache == null || _resourceLoader == null)
            return null;

        return new Renderer(_fontCache, _imageCache, _resourceLoader)
        {
            PressedElement = _pressedControl,
            TextareaStateResolver = GetTextareaRenderState,
            SelectScrollResolver = GetSelectScrollOffset,
            FieldScrollResolver = GetRendererFieldScrollX,
            EmbeddedFrameResolver = ResolveEmbeddedContent,
            EmbeddedCanvasResolver = RenderEmbeddedCanvas,
            SkipEmbeddedContent = true
        };
    }

    private SKPicture? BuildRootDisplayList(GRContext? gpuContext)
    {
        if (_rootBox == null || _document == null ||
            _fontCache == null || _imageCache == null || _resourceLoader == null)
            return null;

        var renderer = CreateRenderer();
        if (renderer == null) return null;

        _rootDynamicEmbeds.Clear();
        _rootDynamicEmbeds.AddRange(CollectDynamicEmbeddedContent(_rootBox));
        _rootDynamicEmbedsCached = true;
        _rootAnimatedSubtrees.Clear();
        _rootAnimatedSubtrees.AddRange(CollectAnimatedSubtrees(_rootBox));
        _rootAnimatedSubtreesCached = true;
        _rootAnimatedImages.Clear();
        _rootAnimatedImages.AddRange(CollectAnimatedImageBoxes(_rootBox, _document));
        _rootAnimatedImagesCached = true;

        var logicalViewport = GetLayoutViewportSize();
        float width = Math.Max(1f, Math.Max(_rootBox.Width, logicalViewport.Width));
        float height = Math.Max(1f, Math.Max(_rootBox.Height, logicalViewport.Height));
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(SKRect.Create(0f, 0f, width, height));
        renderer.RenderToCanvas(canvas, _rootBox, _document,
            _fontCache, _imageCache, width, height, 0f, 0f,
            _lastHoveredElement, _blinkVisible, _showBoxOutlines, _focusedInput,
            renderScale: 1f, clearBackground: true, gpuContext: gpuContext,
            skipAnimatedContent: true, skipAnimatedImages: true);
        return recorder.EndRecording();
    }

    private SKPicture? BuildFrameDisplayList(FrameView view, GRContext? gpuContext)
    {
        if (view.RootBox == null || _fontCache == null || _imageCache == null || _resourceLoader == null)
            return null;

        var renderer = CreateRenderer();
        if (renderer == null) return null;

        _frameDynamicEmbeds[view] = CollectDynamicEmbeddedContent(view.RootBox);
        _frameAnimatedSubtrees[view] = CollectAnimatedSubtrees(view.RootBox);
        _frameAnimatedImages[view] = CollectAnimatedImageBoxes(view.RootBox, view.Document);

        var size = GetFrameContentSize(view.RootBox,
            Math.Max(1f, view.RootBox.Width), Math.Max(1f, view.RootBox.Height));
        float width = Math.Max(1f, Math.Max(view.RootBox.Width, size.Width));
        float height = Math.Max(1f, Math.Max(view.RootBox.Height, size.Height));
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(SKRect.Create(0f, 0f, width, height));
        renderer.RenderToCanvas(canvas, view.RootBox, view.Document,
            _fontCache, _imageCache, width, height, 0f, 0f,
            view.Document.HoveredElement, _blinkVisible,
            showBoxOutlines: _showBoxOutlines,
            focusedElement: _focusedInputFrame == view ? _focusedInput : null,
            renderScale: 1f, clearBackground: true, gpuContext: gpuContext,
            skipAnimatedContent: true, skipAnimatedImages: true);
        return recorder.EndRecording();
    }

    private SKPicture? GetRootDisplayList(GRContext? gpuContext = null)
    {
        if (_displayListsDirty || _rootDisplayList == null)
        {
            _rootDisplayList?.Dispose();
            _rootDisplayList = BuildRootDisplayList(gpuContext);
            _displayListsDirty = _rootDisplayList == null;
        }
        return _rootDisplayList;
    }

    private SKPicture? GetFrameDisplayList(FrameView view, GRContext? gpuContext = null)
    {
        if (_displayListsDirty)
            return null;
        if (_frameDisplayLists.TryGetValue(view, out var cached))
            return cached;
        var created = BuildFrameDisplayList(view, gpuContext);
        if (created != null)
            _frameDisplayLists[view] = created;
        return created;
    }

    private float PaintScrollX
    {
        get
        {
            float value = _scrollOffset.X + _gesturePanX;
            return float.IsFinite(value) ? value : 0f;
        }
    }

    private float PaintScrollY
    {
        get
        {
            float value = _scrollOffset.Y + _gesturePanY;
            return float.IsFinite(value) ? value : 0f;
        }
    }

    private void PaintDynamicEmbeddedContent(SKCanvas canvas, IReadOnlyList<LayoutBox> boxes)
    {
        if (boxes.Count == 0) return;
        foreach (var box in boxes)
        {
            if (box.Element == null || box.Width <= 0f || box.Height <= 0f)
                continue;
            try
            {
                // The static display list deliberately leaves dynamic embeds out.
                // Their current surface/frame is composited here on every GPU frame.
                RenderEmbeddedCanvas(canvas, box.Element, box, false);
            }
            catch (Exception ex)
            {
                Retro96.DebugLog.WriteException("PaintDynamicEmbeddedContent", ex);
            }
        }
    }

    private IReadOnlyList<LayoutBox> GetRootDynamicEmbeddedContent()
    {
        if (!_rootDynamicEmbedsCached && _rootBox != null)
        {
            _rootDynamicEmbeds.Clear();
            _rootDynamicEmbeds.AddRange(CollectDynamicEmbeddedContent(_rootBox));
            _rootDynamicEmbedsCached = true;
        }
        return _rootDynamicEmbeds;
    }

    private IReadOnlyList<LayoutBox> GetFrameDynamicEmbeddedContent(FrameView view)
    {
        if (!_frameDynamicEmbeds.TryGetValue(view, out var boxes))
        {
            boxes = CollectDynamicEmbeddedContent(view.RootBox);
            _frameDynamicEmbeds[view] = boxes;
        }
        return boxes;
    }

    private Renderer? GetAnimationRenderer()
    {
        if (_fontCache == null || _imageCache == null || _resourceLoader == null)
            return null;

        _animationRenderer ??= new Renderer(_fontCache, _imageCache, _resourceLoader);
        _animationRenderer.PressedElement = _pressedControl;
        _animationRenderer.TextareaStateResolver = GetTextareaRenderState;
        _animationRenderer.SelectScrollResolver = GetSelectScrollOffset;
        _animationRenderer.FieldScrollResolver = GetRendererFieldScrollX;
        _animationRenderer.EmbeddedFrameResolver = ResolveEmbeddedContent;
        _animationRenderer.EmbeddedCanvasResolver = RenderEmbeddedCanvas;
        _animationRenderer.SkipEmbeddedContent = true;
        return _animationRenderer;
    }

    private void PaintAnimatedSubtrees(SKCanvas canvas, Renderer renderer,
                                       IEnumerable<LayoutBox> boxes,
                                       DomDocument document, DomElement? hoveredElement,
                                       bool blinkVisible, DomElement? focusedElement,
                                       GRContext? gpuContext)
    {
        foreach (var box in boxes)
        {
            if (box.Width <= 0f || box.Height <= 0f) continue;
            try
            {
                renderer.RenderAnimatedBoxToCanvas(canvas, box, document, _fontCache!, _imageCache!,
                    hoveredElement, blinkVisible, focusedElement, gpuContext);
            }
            catch (Exception ex)
            {
                Retro96.DebugLog.WriteException("PaintAnimatedSubtrees", ex);
            }
        }
    }

    private void PaintAnimatedImages(SKCanvas canvas, Renderer renderer,
                                     IEnumerable<LayoutBox> boxes,
                                     DomDocument document, GRContext? gpuContext)
    {
        foreach (var box in boxes)
        {
            if (box.Width <= 0f || box.Height <= 0f) continue;
            try
            {
                renderer.RenderAnimatedImageToCanvas(canvas, box, document, _imageCache!, gpuContext);
            }
            catch (Exception ex)
            {
                Retro96.DebugLog.WriteException("PaintAnimatedImages", ex);
            }
        }
    }

    protected override void OnPaintSurface(SKPaintGLSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);

        var canvas = e.Surface.Canvas;
        var currentGpuContext = GRContext;
        IntPtr currentGpuHandle = currentGpuContext?.Handle ?? IntPtr.Zero;
        if (_displayListGpuContextHandle != currentGpuHandle)
        {
            _displayListGpuContextHandle = currentGpuHandle;
            InvalidateDisplayLists();
        }
        int clientW = Math.Max(1, ClientSize.Width);
        int clientH = Math.Max(1, ClientSize.Height);
        int viewportW = Math.Max(1, GetViewportSize().Width);
        int viewportH = Math.Max(1, GetViewportSize().Height);
        float zoom = Math.Max(0.25f, EffectiveZoom);
        float logicalVw = viewportW / zoom;
        float logicalVh = viewportH / zoom;
        float scrollX = PaintScrollX;
        float scrollY = PaintScrollY;

        // The page itself owns the content background. Fixed-size framesets can
        // legitimately occupy less than the physical viewport when zoomed out;
        // clearing to the Win95 workspace grey made that unused area look like a
        // broken second canvas. The renderer then paints the document's actual
        // background over the content region.
        canvas.Clear(SKColors.White);

        // One stable transform covers the entire browser content frame. The
        // scroll offset is just a translation on top of it, so a wheel event
        // does not cause any rasterization/layout work.
        var animationRendererForPaint = GetAnimationRenderer();
        int globalState = canvas.Save();
        try
        {
            canvas.Scale(zoom, zoom);
            canvas.ClipRect(SKRect.Create(0f, 0f, logicalVw, logicalVh), SKClipOperation.Intersect);

            int pageState = canvas.Save();
            try
            {
                canvas.Translate(-scrollX, -scrollY);
                var picture = GetRootDisplayList(currentGpuContext);
                if (picture != null)
                    canvas.DrawPicture(picture);
                if (_rootBox != null)
                    PaintDynamicEmbeddedContent(canvas, GetRootDynamicEmbeddedContent());
                if (_rootBox != null && (_rootAnimatedSubtreesCached || _hasMarquee || BrowserRuntime.BlinkEnabled))
                {
                    var animationRenderer = GetAnimationRenderer();
                    if (animationRenderer != null)
                    {
                        if (!_rootAnimatedSubtreesCached)
                        {
                            _rootAnimatedSubtrees.Clear();
                            _rootAnimatedSubtrees.AddRange(CollectAnimatedSubtrees(_rootBox));
                            _rootAnimatedSubtreesCached = true;
                        }
                        PaintAnimatedSubtrees(canvas, animationRenderer, _rootAnimatedSubtrees,
                            _document!, _lastHoveredElement, _blinkVisible, _focusedInputFrame == null ? _focusedInput : null,
                            currentGpuContext);
                    }
                }
                if (_rootBox != null && animationRendererForPaint != null && _rootAnimatedImagesCached &&
                    _rootAnimatedImages.Count > 0)
                {
                    PaintAnimatedImages(canvas, animationRendererForPaint, _rootAnimatedImages, _document!, currentGpuContext);
                }
            }
            finally
            {
                canvas.RestoreToCount(pageState);
            }

            // Nested frame contents are independent display lists so a frame's
            // own scroll can move without repainting the parent document.
            foreach (var (box, view) in _frames.ToArray())
            {
                var destRect = new RectangleF(
                    box.X - scrollX,
                    box.Y - scrollY,
                    Math.Max(0f, box.Width),
                    Math.Max(0f, box.Height));
                PaintFrameGpu(canvas, box, view, destRect,
                    new RectangleF(0, 0, logicalVw, logicalVh), 0, currentGpuContext,
                    animationRendererForPaint);
            }

            using var g = Graphics.FromCanvas(canvas, GRContext);
            int overlayState = g.Save();
            try
            {
                g.SetClip(new RectangleF(0, 0, logicalVw, logicalVh), CombineMode.Intersect);
                // Paint focused-field content before find overlays. Otherwise the
                // field repaint clears the find box whenever the match lives in a
                // focused input/textarea, especially after internal scrolling.
                // Find highlighting is the final text overlay and uses the same
                // field scroll state as the actual control renderer.
                // Links use the normal Renderer :active/BODY ALINK paint path.
                // Never paint a second red text copy here: that was the source
                // of the visible red-on-green ghost and the slight positional jump.
                if (_selectAllPage && _selectionFrame == null)
                    PaintPageSelectAllOverlay(g, scrollX, scrollY);
                else if (_selAnchor != null && _selFocus != null && _selectionFrame == null)
                    PaintPageSelectionOverlay(g, scrollX, scrollY);
                // Ctrl+A page selection paints its own control selections. Do
                // not draw the focused-field caret/overlay on top of that state;
                // doing so can leave a second stale selection/caret rectangle
                // beside the input even though the page selection is correct.
                if (_focusedInput != null && _rootBox != null &&
                    !IsFocusedFieldSuppressedByPageSelection())
                    PaintFieldOverlay(g);
                PaintFindHighlights(g, scrollX, scrollY);
                PaintEmbeddedMidiControls(g);
            }
            finally
            {
                g.Restore(overlayState);
            }
        }
        finally
        {
            canvas.RestoreToCount(globalState);
        }

        // Physical-pixel scrollbar UI is drawn in the same GL surface after
        // the page transform is restored, preventing thumb/page phase mismatch.
        PaintTopLevelScrollbars(canvas, clientW, clientH, viewportW, viewportH);
        e.Surface.Flush();
    }

    private void PaintFrameGpu(SKCanvas canvas, LayoutBox frameBox, FrameView view,
                               RectangleF destRect, RectangleF ancestorClip, int depth, GRContext? gpuContext,
                               Renderer? animationRenderer)
    {
        if (depth > 8 || destRect.Width <= 0f || destRect.Height <= 0f) return;
        var visible = IntersectRect(destRect, ancestorClip);
        if (visible.Width <= 0f || visible.Height <= 0f) return;

        int state = canvas.Save();
        try
        {
            canvas.ClipRect(SKRect.Create(visible.X, visible.Y, visible.Width, visible.Height), SKClipOperation.Intersect);
            canvas.Translate(destRect.X, destRect.Y);
            canvas.ClipRect(SKRect.Create(0, 0, frameBox.Width, frameBox.Height), SKClipOperation.Intersect);
            canvas.Translate(-view.Scroll.X, -view.Scroll.Y);

            var picture = GetFrameDisplayList(view, gpuContext);
            if (picture != null)
                canvas.DrawPicture(picture);
            if (view.RootBox != null)
                PaintDynamicEmbeddedContent(canvas, GetFrameDynamicEmbeddedContent(view));
            if (view.RootBox != null && animationRenderer != null)
            {
                if (!_frameAnimatedSubtrees.TryGetValue(view, out var animatedBoxes))
                {
                    animatedBoxes = CollectAnimatedSubtrees(view.RootBox);
                    _frameAnimatedSubtrees[view] = animatedBoxes;
                }
                PaintAnimatedSubtrees(canvas, animationRenderer, animatedBoxes, view.Document,
                    view.Document.HoveredElement, _blinkVisible,
                    _focusedInputFrame == view ? _focusedInput : null, gpuContext);
            }
            if (animationRenderer != null && _frameAnimatedImages.TryGetValue(view, out var animatedImages) &&
                animatedImages.Count > 0)
            {
                PaintAnimatedImages(canvas, animationRenderer, animatedImages, view.Document, gpuContext);
            }
        }
        finally
        {
            canvas.RestoreToCount(state);
        }

        foreach (var (childBox, childView) in view.ChildFrames.ToArray())
        {
            var childDest = new RectangleF(
                destRect.X + childBox.X - view.Scroll.X,
                destRect.Y + childBox.Y - view.Scroll.Y,
                Math.Max(0f, childBox.Width),
                Math.Max(0f, childBox.Height));
            PaintFrameGpu(canvas, childBox, childView, childDest, visible, depth + 1, gpuContext, animationRenderer);
        }

        using var g = Graphics.FromCanvas(canvas, GRContext);
        int overlayState = g.Save();
        try
        {
            g.SetClip(visible, CombineMode.Intersect);
            if (_selAnchor != null && _selFocus != null && ReferenceEquals(_selectionFrame, view))
                PaintFrameSelectionOverlay(g, frameBox, view, destRect);
            PaintFrameScrollbars(g, destRect, view, frameBox);
            if (view.FrameBorder) PaintFrameBorder(g, destRect);
            if (_focusedFrame == frameBox)
            {
                using var focusPen = new Pen(Color.FromArgb(0x00, 0x00, 0x80), 1);
                g.DrawRectangle(focusPen, destRect.X, destRect.Y,
                    Math.Max(0f, destRect.Width - 1f), Math.Max(0f, destRect.Height - 1f));
            }
            PaintFocusedFrameFieldOverlayRecursive(g, destRect, view);
        }
        finally
        {
            g.Restore(overlayState);
        }
    }

    private float MarqueeSelectionTranslationX(LayoutBox marqueeBox, long now)
    {
        var renderer = _animationRenderer ?? GetAnimationRenderer();
        if (renderer == null) return 0f;

        float translationX = renderer.GetMarqueeTranslationX(marqueeBox, now);

        // The animated marquee renderer snaps the final translation to one
        // device pixel before painting. Selection/Find overlays are painted
        // afterwards, so using the raw animation phase here leaves the yellow
        // or blue overlay a fraction of a pixel away from the moving glyphs.
        // Keep the overlay on the exact same raster position as the renderer.
        float deviceScaleX = MathF.Abs(EffectiveZoom);
        if (!float.IsFinite(deviceScaleX) || deviceScaleX < 0.01f)
            deviceScaleX = 1f;
        float devicePixel = 1f / deviceScaleX;
        return MathF.Round(translationX / devicePixel) * devicePixel;
    }

    private bool TryGetSelectionAnimationOffset(LayoutBox box, LayoutBox selectionRoot,
                                                 out float offsetX)
    {
        offsetX = 0f;
        bool blinkVisible = true;
        long now = Environment.TickCount64;
        var animatedAncestors = new List<(DomElement Element, bool Marquee)>();

        for (var node = box.Element?.Parent as DomElement; node != null; node = node.Parent as DomElement)
        {
            if (BrowserRuntime.BlinkEnabled && node.TagName == "blink")
                blinkVisible &= _blinkVisible;
            if (BrowserRuntime.MarqueeEnabled && node.TagName == "marquee")
                animatedAncestors.Add((node, true));
        }
        if (!blinkVisible) return false;

        // Apply outer-to-inner marquee transforms.  LayoutBox coordinates are
        // document-local, so each active marquee contributes only its own
        // translation; nested marquees naturally accumulate.
        for (int i = animatedAncestors.Count - 1; i >= 0; i--)
        {
            var marqueeElement = animatedAncestors[i].Element;
            var marqueeBox = FindBoxForElement(selectionRoot, marqueeElement);
            if (marqueeBox != null)
                offsetX += MarqueeSelectionTranslationX(marqueeBox, now);
        }

        if (BrowserRuntime.BlinkEnabled && box.Element is not null)
        {
            for (var node = (DomNode?)box.Element; node != null; node = node.Parent)
            {
                if (node is DomElement de && de.TagName == "blink")
                {
                    if (!_blinkVisible) return false;
                    break;
                }
            }
        }
        return true;
    }

    private void PaintFrameSelectionOverlay(Graphics g, LayoutBox frameBox,
                                             FrameView frameView, RectangleF destRect)
    {
        if (frameView.RootBox == null) return;
        if (_selectAllPage && ReferenceEquals(_selectionFrame, frameView))
        {
            PaintFrameSelectAllOverlay(g, frameView, destRect);
            return;
        }
        if (_selAnchor == null || _selFocus == null) return;

        var metrics = GetFrameScrollMetrics(frameBox, frameView);
        var contentRect = new RectangleF(
            destRect.Left,
            destRect.Top,
            metrics.ViewportWidth,
            metrics.ViewportHeight);
        contentRect = IntersectRect(contentRect, destRect);
        if (contentRect.Width <= 0f || contentRect.Height <= 0f) return;

        if (_selectionSemanticScope != null &&
            ReferenceEquals(_selectionSemanticScopeFrame, frameView))
        {
            int semanticState = g.Save();
            try
            {
                g.SetClip(contentRect, CombineMode.Intersect);
                PaintSemanticTextSelectionScope(g, frameView.RootBox,
                    _selectionSemanticScope,
                    destRect.X - frameView.Scroll.X,
                    destRect.Y - frameView.Scroll.Y);
            }
            finally { g.Restore(semanticState); }
            return;
        }

        var ordered = OrderedSelectionRangeBoxes(frameView.RootBox);
        int anchorIndex = ordered.IndexOf(_selAnchor);
        int focusIndex = ordered.IndexOf(_selFocus);
        if (anchorIndex < 0 || focusIndex < 0) return;

        bool forward = anchorIndex < focusIndex ||
            (anchorIndex == focusIndex && _selAnchorOffset <= _selFocusOffset);
        int firstIndex = Math.Min(anchorIndex, focusIndex);
        int lastIndex = Math.Max(anchorIndex, focusIndex);

        int state = g.Save();
        try
        {
            g.SetClip(contentRect, CombineMode.Intersect);
            using var selBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 170));
            float offsetX = destRect.X - frameView.Scroll.X;
            float offsetY = destRect.Y - frameView.Scroll.Y;

            var selectedSpans = new List<RectangleF>();
            for (int i = firstIndex; i <= lastIndex; i++)
            {
                var box = ordered[i];
                int len = GetSelectionBoxTextLength(box);
                if (len == 0) continue;
                if (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element))
                {
                    int a0 = 0, b0 = len;
                    if (i == anchorIndex)
                    {
                        int off = Math.Clamp(_selAnchorOffset, 0, len);
                        if (forward) a0 = off; else b0 = off;
                    }
                    if (i == focusIndex)
                    {
                        int off = Math.Clamp(_selFocusOffset, 0, len);
                        if (forward) b0 = off; else a0 = off;
                    }
                    if (b0 > a0 &&
                        TryGetSelectionAnimationOffset(box, frameView.RootBox, out float controlAnimatedX))
                    {
                        PaintSelectionControl(g, box, offsetX + controlAnimatedX, offsetY, selBrush, a0, b0);
                    }
                    continue;
                }
                if (!TryGetSelectionAnimationOffset(box, frameView.RootBox, out float animatedX))
                    continue;

                int a = 0, b = len;
                if (i == anchorIndex)
                {
                    int off = Math.Clamp(_selAnchorOffset, 0, len);
                    if (forward) a = off; else b = off;
                }
                if (i == focusIndex)
                {
                    int off = Math.Clamp(_selFocusOffset, 0, len);
                    if (forward) b = off; else a = off;
                }
                if (b < a) (a, b) = (b, a);
                if (b <= a) continue;

                foreach (var span in SelectionVisualSpans(g, box, a, b))
                    selectedSpans.Add(new RectangleF(span.X + animatedX, span.Y, span.Width, span.Height));
            }

            foreach (var span in selectedSpans)
            {
                var draw = new RectangleF(span.X + offsetX, span.Y + offsetY, span.Width, span.Height);
                var clipped = IntersectRect(draw, contentRect);
                if (clipped.Width > 0.01f && clipped.Height > 0.01f)
                    g.FillRectangle(selBrush, clipped.X, clipped.Y, clipped.Width, clipped.Height);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }


    private Color GetActiveLinkPaintColor(DomElement link, DomDocument document)
    {
        // Match Renderer.PaintText's legacy BODY ALINK rules without resolving
        // CSS again. Active state is paint-only, so the geometry and computed
        // font metrics remain exactly those of the cached page display list.
        if (link.Style?.OwnColor == true)
            return link.Style.Color;

        return StyleResolver.GetALinkColor(document);
    }

    private void PaintPressedLinkOverlay(Graphics g, float paintScrollX, float paintScrollY)
    {
        var link = _pressedControl;
        if (link == null || link.TagName != "a" || !link.HasAttr("href") || _fontCache == null)
            return;

        DomDocument? document = _pressedControlFrame?.Document ?? _document;
        LayoutBox? root = _pressedControlFrame?.RootBox ?? _rootBox;
        if (document == null || root == null) return;

        RectangleF frameRect = RectangleF.Empty;
        float offsetX = -paintScrollX;
        float offsetY = -paintScrollY;
        if (_pressedControlFrame != null)
        {
            if (!TryGetFrameScreenRect(_pressedControlFrame, out frameRect)) return;
            offsetX = frameRect.X - _pressedControlFrame.Scroll.X;
            offsetY = frameRect.Y - _pressedControlFrame.Scroll.Y;
            var state = g.Save();
            try
            {
                g.SetClip(frameRect, CombineMode.Intersect);
                PaintPressedLinkTextRuns(g, root, link, document, offsetX, offsetY);
            }
            finally { g.Restore(state); }
            return;
        }

        PaintPressedLinkTextRuns(g, root, link, document, offsetX, offsetY);
    }

    private void PaintPressedLinkTextRuns(Graphics g, LayoutBox root, DomElement link,
                                          DomDocument document, float offsetX, float offsetY)
    {
        foreach (var box in root.Descendants())
        {
            if (!IsSelectionEligibleBox(box) || !IsElementWithin(box.Element, link))
                continue;

            var style = box.StyleOverride ?? box.Element?.Style;
            if (style == null) continue;
            var font = ResolveFieldFontFromStyle(style);
            if (font == null) continue;

            string text = TransformSelectionText(box.TextRun ?? string.Empty, style.TextTransform);
            if (string.IsNullOrEmpty(text)) continue;

            Color color = GetActiveLinkPaintColor(link, document);
            // The legacy HTML in question uses ordinary left-aligned inline text.
            // Keep the exact box origin; no layout-affecting state is changed.
            float x = box.ContentRect.X + offsetX;
            float y = box.ContentRect.Y + offsetY;
            if (style.LineHeightMode != LineHeightMode.Normal)
                y += Math.Max(0f, (box.ContentRect.Height - font.GetHeight(g)) / 2f);
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;

            using var brush = new SolidBrush(color);
            using var fmt = NewSelectionFormat();
            g.DrawString(text, font, brush, x, y, fmt);
        }
    }

    private Font? ResolveFieldFontFromStyle(ComputedStyle style)
    {
        if (_fontCache == null) return null;
        return _fontCache.Resolve(style.FontFamily, style.FontSize,
            (int)style.FontWeight,
            style.FontStyle == FontStyleValue.Italic,
            style.FontStyle == FontStyleValue.Oblique);
    }

    private void PaintPageSelectAllOverlay(Graphics g, float paintScrollX, float paintScrollY)
    {
        if (_rootBox == null) return;

        PaintSelectAllTreeOverlay(g, _rootBox, _document!, -paintScrollX, -paintScrollY,
            includeFields: true);

        // Frame documents are separate layout trees and are not descendants of
        // the page tree. Ctrl+A on the page must still select their text and
        // their form-control values visually.
        foreach (var (box, view) in _frames.ToArray())
        {
            var dest = new RectangleF(
                box.X - paintScrollX,
                box.Y - paintScrollY,
                box.Width, box.Height);
            PaintFrameSelectAllOverlay(g, view, dest);
        }
    }

    private void PaintFrameSelectAllOverlay(Graphics g, FrameView view, RectangleF destRect)
    {
        if (view.RootBox == null || !TryGetFrameBoxForView(view, out var hostBox)) return;
        var metrics = GetFrameScrollMetrics(hostBox, view);
        var clip = IntersectRect(destRect,
            new RectangleF(destRect.X, destRect.Y,
                Math.Max(0f, metrics.ViewportWidth), Math.Max(0f, metrics.ViewportHeight)));
        if (clip.Width <= 0.01f || clip.Height <= 0.01f) return;

        int state = g.Save();
        try
        {
            g.SetClip(clip, CombineMode.Intersect);
            float ox = destRect.X - view.Scroll.X;
            float oy = destRect.Y - view.Scroll.Y;
            PaintSelectAllTreeOverlay(g, view.RootBox, view.Document, ox, oy, includeFields: true);
            foreach (var (childBox, childView) in view.ChildFrames)
            {
                var childDest = new RectangleF(
                    destRect.X + childBox.X - view.Scroll.X,
                    destRect.Y + childBox.Y - view.Scroll.Y,
                    childBox.Width, childBox.Height);
                PaintFrameSelectAllOverlay(g, childView, childDest);
            }
        }
        finally { g.Restore(state); }
    }

    private static IEnumerable<LayoutBox> EnumerateSelectionTree(LayoutBox root)
    {
        yield return root;
        foreach (var box in root.Descendants())
            yield return box;
    }

    private void PaintSelectAllTreeOverlay(Graphics g, LayoutBox root, DomDocument document,
                                           float offsetX, float offsetY, bool includeFields)
    {
        using var selBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 170));
        foreach (var box in EnumerateSelectionTree(root))
        {
            if (IsSelectionEligibleBox(box) &&
                TryGetSelectionAnimationOffset(box, root, out float animatedX))
            {
                foreach (var span in SelectionVisualSpans(g, box, 0, box.TextRun!.Length))
                    g.FillRectangle(selBrush,
                        span.X + offsetX + animatedX, span.Y + offsetY,
                        span.Width, span.Height);
            }

            if (!includeFields || box.BoxType != BoxType.Replaced ||
                !IsPageSelectableControl(box.Element))
                continue;

            if (!TryGetSelectionAnimationOffset(box, root, out float controlAnimatedX))
                continue;

            float controlOffsetX = offsetX + controlAnimatedX;
            if (IsEditableField(box.Element!))
                PaintSelectAllEditableField(g, box, controlOffsetX, offsetY);
            else
                PaintSelectAllControl(g, box, controlOffsetX, offsetY);
        }
    }

    private void PaintSelectAllControl(Graphics g, LayoutBox box,
                                       float offsetX, float offsetY)
    {
        // Select-all uses the same text-only control painter as drag selection.
        // In particular, a <select> exposes only its option label text, never
        // the bevel, arrow glyph, or unused control face. Buttons are excluded
        // by IsPageSelectableControl and therefore never get a page-selection
        // highlight.
        if (!IsPageSelectableControl(box.Element)) return;
        using var highlight = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
        PaintSelectionControl(g, box, offsetX, offsetY, highlight);
    }

    private void PaintSelectAllEditableField(Graphics g, LayoutBox box,
                                             float offsetX, float offsetY)
    {
        var el = box.Element;
        if (el == null || !IsEditableField(el)) return;
        string text = GetFieldText(el);
        if (text.Length == 0) return;

        var font = ResolveFieldFont(el);
        if (font == null) return;
        // The renderer paints the control inside BorderRect.  ContentRect can
        // include layout slack on legacy replaced boxes, which previously let
        // the select-all overlay spill a blue strip outside the actual input.
        var face = IntersectRect(box.ContentRect, box.BorderRect);
        face.X += offsetX; face.Y += offsetY;
        if (face.Width <= 0.01f || face.Height <= 0.01f) return;
        using var format = NewFieldFormat(noWrap: true);
        format.LineAlignment = StringAlignment.Center;

        using var highlight = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
        using var selectedText = new SolidBrush(Color.White);

        int state = g.Save();
        try
        {
            // Ctrl+A is a visual overlay, so a replaced control must be clipped
            // to its own face exactly like the focused-field renderer. Otherwise
            // a long textarea line/selection can paint a free-floating blue block
            // outside the input and make it look like a random page selection.
            g.SetClip(face, CombineMode.Intersect);

            if (el.TagName == "textarea")
            {
                var lines = GetTextareaLines(el, box, text, font);
                float lineHeight = font.GetHeight(g);
                var textareaState = GetTextareaRenderState(el);
                float textX = face.X + 3f - textareaState.ScrollX;
                float textY = face.Y + 2f - textareaState.ScrollLine * font.GetHeight(g);
                using var lineFormat = NewFieldFormat(noWrap: true);
                for (int i = 0; i < lines.Count; i++)
                {
                    var (start, end) = lines[i];
                    if (end <= start) continue;
                    float width = g.MeasureString(text[start..end], font, int.MaxValue, lineFormat).Width;
                    float lineY = textY + i * lineHeight;
                    g.FillRectangle(highlight, textX, lineY, Math.Max(1f, width), lineHeight);
                    g.DrawString(text[start..end], font, selectedText,
                        new RectangleF(textX, lineY, Math.Max(1f, width), lineHeight), lineFormat);
                }
                return;
            }

        bool password = el.GetAttrOrDefault("type", "text").Trim()
            .Equals("password", StringComparison.OrdinalIgnoreCase);
        string visual = password ? new string('*', text.Length) : text;
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float advance = password ? GetPasswordGlyphAdvance(g, font, format) : 0f;
        float totalWidth = GetSingleLineFieldTextWidth(g, visual, font, format, password, advance);
        float alignedOffset = GetSingleLineFieldAlignmentOffset(
            visibleWidth, totalWidth, GetFieldTextAlign(el));
        // Ctrl+A must paint from the same internal viewport as the renderer.
        // Find can intentionally move an unfocused input to the right; ignoring
        // that offset here made the selected value jump back to the left edge.
        float fieldScroll = password || el.TagName != "input"
            ? 0f
            : GetFieldViewportScrollX(el, box);
        float textXSingle = face.X + 3f + alignedOffset - fieldScroll;
        float alignedTextWidth = totalWidth;
        if (alignedTextWidth <= 0.01f) return;

        float visibleLeft = face.Left + 2f;
        float visibleRight = face.Right - 2f;
        float drawLeft = Math.Max(visibleLeft, textXSingle);
        float drawRight = Math.Min(visibleRight, textXSingle + alignedTextWidth + 1f);
        if (drawRight <= drawLeft) return;

        g.FillRectangle(highlight, drawLeft, face.Top,
            drawRight - drawLeft, face.Height);
        if (password)
        {
            PasswordMaskLayout.DrawRange(g, visual.Length, font, selectedText,
                textXSingle, face.Top, face.Height, 0, visual.Length, format);
        }
        else
        {
            g.DrawString(visual, font, selectedText,
                new RectangleF(textXSingle, face.Top, Math.Max(1f, alignedTextWidth), face.Height), format);
        }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private string GetSelectAllText()
    {
        LayoutBox? root = _selectionFrame?.RootBox ?? _rootBox;
        if (root == null) return string.Empty;
        var parts = new List<string>();
        AppendSelectAllText(root, parts);
        if (_selectionFrame == null)
        {
            foreach (var (_, view) in _frames.ToArray())
                AppendFrameSelectAllText(view, parts);
        }
        return string.Concat(parts);
    }

    private static void AppendSelectAllText(LayoutBox root, List<string> parts)
    {
        // Keep this helper's public shape for the existing frame traversal, but
        // build a visual-order string so spaces between layout fragments are not
        // silently lost when Ctrl+A is copied.
        var boxes = EnumerateSelectionTree(root)
            .Where(IsSelectionRangeBox)
            .Select((box, index) => (box, index))
            .OrderBy(item => item.box.ContentRect.Top)
            .ThenBy(item => item.box.ContentRect.Left)
            .ThenBy(item => item.index)
            .Select(item => item.box)
            .ToList();

        var sb = new StringBuilder();
        LayoutBox? previous = null;
        foreach (var box in boxes)
        {
            string text = IsSelectionEligibleBox(box)
                ? (box.TextRun ?? string.Empty)
                : (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element)
                    ? GetSelectableControlText(box.Element!)
                    : string.Empty);
            if (text.Length == 0) continue;
            if (previous != null) AppendSelectionSeparator(sb, previous, box);
            sb.Append(text);
            previous = box;
        }

        if (sb.Length > 0) parts.Add(sb.ToString());
    }

    private static void AppendFrameSelectAllText(FrameView view, List<string> parts)
    {
        if (view.RootBox != null)
            AppendSelectAllText(view.RootBox, parts);
        foreach (var (_, child) in view.ChildFrames)
            AppendFrameSelectAllText(child, parts);
    }


    private void PaintSelectionControl(Graphics g, LayoutBox box,
                                      float offsetX, float offsetY,
                                      SolidBrush brush, int start = 0, int end = -1)
    {
        var element = box.Element;
        if (!IsPageSelectableControl(element)) return;

        var face = IntersectRect(box.ContentRect, box.BorderRect);
        if (face.Width <= 0.01f || face.Height <= 0.01f) return;

        var font = ResolveFieldFont(element!);
        if (font == null) return;

        int state = g.Save();
        try
        {
            g.SetClip(new RectangleF(face.X + offsetX, face.Y + offsetY,
                face.Width, face.Height), CombineMode.Intersect);

            // A single-row <select> is a control surface, but only its option
            // glyphs participate in text selection.  Never paint the arrow,
            // bevel, or unused face area.
            if (element!.TagName == "select")
            {
                var options = element.Descendants().OfType<DomElement>()
                    .Where(o => o.TagName == "option").ToList();
                if (options.Count == 0) return;

                bool listbox = element.HasAttr("multiple") ||
                               element.GetAttrInt("size", 1) > 1;
                var format = NewFieldFormat(noWrap: true);
                if (!listbox)
                {
                    var selected = options.FirstOrDefault(o => o.HasAttr("selected")) ?? options[0];
                    string sourceText = GetSelectOptionText(selected);
                    string text = GlyphSubstitution.MapGlyphs(sourceText);
                    if (text.Length == 0) return;
                    start = Math.Clamp(start, 0, sourceText.Length);
                    end = end < 0 ? sourceText.Length : Math.Clamp(end, start, sourceText.Length);
                    if (end <= start) return;
                    float textMax = Math.Max(1f, face.Width - 18f);
                    float fullWidth = Math.Min(textMax,
                        g.MeasureString(text, font, int.MaxValue, format).Width);
                    float lineH = Math.Min(face.Height - 2f, Math.Max(1f, font.GetHeight(g)));
                    float x = face.X + 3f;
                    float y = face.Y + Math.Max(0f, (face.Height - lineH) / 2f);
                    float left = x + g.MeasureString(text[..Math.Min(start, text.Length)], font, int.MaxValue, format).Width;
                    float right = x + g.MeasureString(text[..Math.Min(end, text.Length)], font, int.MaxValue, format).Width;
                    left = Math.Max(x, Math.Min(left, x + fullWidth));
                    right = Math.Max(left, Math.Min(right, x + fullWidth));
                    if (right > left)
                        g.FillRectangle(brush, left + offsetX, y + offsetY,
                            Math.Max(1f, right - left + 1f), lineH);
                    return;
                }

                int visibleRows = GetSelectVisibleRows(element);
                int scrollOffset = GetSelectScrollOffset(element);
                float rowH = font.GetHeight(g) + 2f;
                float optionWidth = Math.Max(1f, face.Width - (options.Count > visibleRows ? 14f : 0f));
                var optionClip = new RectangleF(face.X + offsetX, face.Y + offsetY, optionWidth, face.Height);
                int optionState = g.Save();
                try
                {
                    g.SetClip(optionClip, CombineMode.Intersect);
                    int rows = Math.Min(visibleRows, options.Count - scrollOffset);
                    int globalOffset = 0;
                    for (int row = 0; row < scrollOffset; row++)
                        globalOffset += GlyphSubstitution.MapGlyphs(GetSelectOptionText(options[row])).Length + 1;

                    for (int row = 0; row < rows; row++)
                    {
                        var opt = options[scrollOffset + row];
                        string source = GetSelectOptionText(opt);
                        string text = GlyphSubstitution.MapGlyphs(source);
                        int optionStart = globalOffset;
                        int optionEnd = optionStart + source.Length;
                        int a = Math.Clamp(start, optionStart, optionEnd);
                        int b = end < 0 ? optionEnd : Math.Clamp(end, optionStart, optionEnd);
                        if (b > a && text.Length > 0)
                        {
                            int localA = Math.Clamp(a - optionStart, 0, text.Length);
                            int localB = Math.Clamp(b - optionStart, localA, text.Length);
                            float prefixA = g.MeasureString(text[..Math.Min(localA, text.Length)], font, int.MaxValue, format).Width;
                            float prefixB = g.MeasureString(text[..Math.Min(localB, text.Length)], font, int.MaxValue, format).Width;
                            float y = face.Y + row * rowH;
                            float drawTop = y + offsetY;
                            float left = face.X + 3f + prefixA + offsetX;
                            float right = face.X + 3f + prefixB + offsetX;
                            if (right > left)
                                g.FillRectangle(brush, left, drawTop, right - left,
                                    Math.Min(rowH, face.Bottom - y));
                        }
                        globalOffset = optionEnd + 1;
                    }
                }
                finally { g.Restore(optionState); }
                return;
            }

            string textValue = element.TagName == "textarea"
                ? GetFieldText(element) : GetSelectableControlText(element);
            if (textValue.Length == 0) return;

            string visual = element.GetAttrOrDefault("type", "text").Trim()
                .Equals("password", StringComparison.OrdinalIgnoreCase)
                ? new string('*', textValue.Length)
                : GlyphSubstitution.MapGlyphs(textValue);
            var sf = NewFieldFormat(noWrap: true);
            sf.LineAlignment = StringAlignment.Center;
            float widthValue = Math.Min(Math.Max(1f, face.Width - 6f),
                g.MeasureString(visual, font, int.MaxValue, sf).Width);
            g.FillRectangle(brush, face.X + 3f + offsetX, face.Y + offsetY,
                Math.Max(1f, widthValue), face.Height);
        }
        finally { g.Restore(state); }
    }

    private void PaintSemanticTextSelectionScope(Graphics g, LayoutBox root,
                                                  DomElement scope, float offsetX, float offsetY)
    {
        using var brush = new SolidBrush(Color.FromArgb(110, 0, 0, 170));
        foreach (var box in EnumerateSelectionTree(root))
        {
            if (!IsSelectionEligibleBox(box) || !IsElementWithin(box.Element, scope))
                continue;
            if (!TryGetSelectionAnimationOffset(box, root, out float animatedX))
                continue;
            foreach (var span in SelectionVisualSpans(g, box, 0, box.TextRun!.Length))
            {
                var draw = new RectangleF(
                    span.X + offsetX + animatedX, span.Y + offsetY,
                    span.Width, span.Height);
                if (draw.Width > 0.01f && draw.Height > 0.01f)
                    g.FillRectangle(brush, draw.X, draw.Y, draw.Width, draw.Height);
            }
        }
    }

    private void PaintPageSelectionOverlay(Graphics g, float paintScrollX, float paintScrollY)
    {
        if (_rootBox == null || _selAnchor == null || _selFocus == null) return;
        using var selBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 170));
        // Selections inside frames belong to the frame document's layout
        // tree. Using the top-level page tree here makes every frame endpoint
        // look absent, so the selection is created but never painted.
        var selectionRoot = _selectionFrame?.RootBox ?? _rootBox;
        if (selectionRoot == null) return;
        if (_selectionSemanticScope != null &&
            ReferenceEquals(_selectionSemanticScopeFrame, _selectionFrame))
        {
            if (_selectionFrame != null && TryGetFrameDestinationRect(_selectionFrame, out var semanticFrameDest))
            {
                int semanticState = g.Save();
                try
                {
                    g.SetClip(semanticFrameDest, CombineMode.Intersect);
                    PaintSemanticTextSelectionScope(g, selectionRoot, _selectionSemanticScope,
                        semanticFrameDest.X - _selectionFrame.Scroll.X,
                        semanticFrameDest.Y - _selectionFrame.Scroll.Y);
                }
                finally { g.Restore(semanticState); }
            }
            else
            {
                PaintSemanticTextSelectionScope(g, selectionRoot, _selectionSemanticScope,
                    -paintScrollX, -paintScrollY);
            }
            return;
        }
        var ordered = OrderedSelectionRangeBoxes(selectionRoot);
        int anchorIndex = ordered.IndexOf(_selAnchor);
        int focusIndex = ordered.IndexOf(_selFocus);
        if (anchorIndex < 0 || focusIndex < 0) return;

        bool forward = anchorIndex < focusIndex ||
            (anchorIndex == focusIndex && _selAnchorOffset <= _selFocusOffset);
        int firstIndex = Math.Min(anchorIndex, focusIndex);
        int lastIndex = Math.Max(anchorIndex, focusIndex);
        float offsetX = -paintScrollX;
        float offsetY = -paintScrollY;
        if (_selectionFrame != null && TryGetFrameDestinationRect(_selectionFrame, out var frameDest))
        {
            offsetX = frameDest.X - _selectionFrame.Scroll.X;
            offsetY = frameDest.Y - _selectionFrame.Scroll.Y;
        }

        var selectedSpans = new List<RectangleF>();
        for (int i = firstIndex; i <= lastIndex; i++)
        {
            var box = ordered[i];
            int len = GetSelectionBoxTextLength(box);
            if (len == 0) continue;
            if (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element))
            {
                int a0 = 0, b0 = len;
                if (i == anchorIndex)
                {
                    int off = Math.Clamp(_selAnchorOffset, 0, len);
                    if (forward) a0 = off; else b0 = off;
                }
                if (i == focusIndex)
                {
                    int off = Math.Clamp(_selFocusOffset, 0, len);
                    if (forward) b0 = off; else a0 = off;
                }
                if (b0 > a0 &&
                    TryGetSelectionAnimationOffset(box, selectionRoot, out float controlAnimatedX))
                {
                    PaintSelectionControl(g, box, offsetX + controlAnimatedX, offsetY, selBrush, a0, b0);
                }
                continue;
            }
            if (!TryGetSelectionAnimationOffset(box, selectionRoot, out float animatedX))
                continue;
            int a = 0, b = len;
            if (i == anchorIndex)
            {
                int off = Math.Clamp(_selAnchorOffset, 0, len);
                if (forward) a = off; else b = off;
            }
            if (i == focusIndex)
            {
                int off = Math.Clamp(_selFocusOffset, 0, len);
                if (forward) b = off; else a = off;
            }
            if (b < a) (a, b) = (b, a);
            if (b <= a) continue;
            foreach (var span in SelectionVisualSpans(g, box, a, b))
                selectedSpans.Add(new RectangleF(span.X + animatedX, span.Y, span.Width, span.Height));
        }
        foreach (var span in selectedSpans)
            g.FillRectangle(selBrush, span.X + offsetX, span.Y + offsetY, span.Width, span.Height);
    }

    private static RectangleF IntersectRect(RectangleF a, RectangleF b)
    {
        float left = Math.Max(a.Left, b.Left);
        float top = Math.Max(a.Top, b.Top);
        float right = Math.Min(a.Right, b.Right);
        float bottom = Math.Min(a.Bottom, b.Bottom);
        return new RectangleF(left, top,
            Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }

    // WinForms/GDI text rectangles and the Skia control renderer both use the
    // font's line height as their vertical centering unit.  Find used to place
    // the highlight at the raw control-top, so inputs/buttons/selects looked
    // a few pixels too high even though their text itself was centered. Keep
    // the overlay on the exact same vertical origin as the control renderer.
    private static float CenteredFieldTextTop(RectangleF rect, float lineHeight) =>
        rect.Top + Math.Max(0f, (rect.Height - lineHeight) * 0.5f);

    private void PaintFindHighlights(Graphics g, float paintScrollX, float paintScrollY)
    {
        if (_findMatches.Count == 0 || string.IsNullOrEmpty(_findText)) return;

        // Find uses one visual treatment everywhere. There is deliberately no
        // separate active color: every match is the same yellow box + border.
        using var matchBrush = new SolidBrush(Color.FromArgb(96, 255, 235, 59));
        using var matchPen = new Pen(Color.FromArgb(190, 160, 100, 0), 1f);

        for (int i = 0; i < _findMatches.Count; i++)
        {
            var match = _findMatches[i];
            var segments = match.Segments;
            if (segments == null || segments.Count == 0)
                segments = new[] { new FindMatchSegment(match.Frame, match.Box,
                    match.Start, match.Length, match.SourceElement) };

            foreach (var segment in segments)
            {
                if (segment.Length <= 0) continue;

                int state = -1;
                float offsetX;
                float offsetY;

                if (segment.Frame == null)
                {
                    offsetX = -paintScrollX;
                    offsetY = -paintScrollY;
                }
                else if (TryGetFrameScreenRect(segment.Frame, out var frameRect))
                {
                    offsetX = frameRect.X - segment.Frame.Scroll.X;
                    offsetY = frameRect.Y - segment.Frame.Scroll.Y;
                    state = g.Save();
                    g.SetClip(frameRect, CombineMode.Intersect);
                }
                else
                {
                    continue;
                }

                var segmentMatch = new FindMatch(
                    segment.Frame, segment.Box, segment.Start, segment.Length,
                    segment.SourceElement);

                LayoutBox? segmentRoot = segment.Frame?.RootBox ?? _rootBox;
                float animatedX = 0f;
                if (segmentRoot != null &&
                    !TryGetSelectionAnimationOffset(segment.Box, segmentRoot, out animatedX))
                {
                    if (state >= 0) g.Restore(state);
                    continue;
                }
                float segmentOffsetX = offsetX + animatedX;

                if (segment.SourceElement != null &&
                    IsSearchableControlElement(segment.SourceElement))
                {
                    var face = IntersectRect(segment.Box.ContentRect, segment.Box.BorderRect);
                    if (face.Width > 0.01f && face.Height > 0.01f)
                    {
                        if (segment.SourceElement.TagName == "textarea")
                        {
                            PaintTextareaFindHighlight(g, segmentMatch, face,
                                segmentOffsetX, offsetY, matchBrush, matchBrush,
                                matchPen, matchPen, active: false);
                        }
                        else
                        {
                            PaintSearchableControlFindHighlight(g, segmentMatch, face,
                                segmentOffsetX, offsetY, matchBrush, matchBrush,
                                matchPen, matchPen, active: false);
                        }
                    }
                }
                else
                {
                    foreach (var span in SelectionVisualSpans(
                                 g, segment.Box, segment.Start,
                                 segment.Start + segment.Length))
                    {
                        if (span.Width <= 0.01f || span.Height <= 0.01f) continue;
                        var drawRect = new RectangleF(
                            span.X + segmentOffsetX,
                            span.Y + offsetY,
                            span.Width,
                            span.Height);
                        g.FillRectangle(matchBrush, drawRect);
                        g.DrawRectangle(matchPen, drawRect.X, drawRect.Y,
                            Math.Max(0.5f, drawRect.Width - 0.5f),
                            Math.Max(0.5f, drawRect.Height - 0.5f));
                    }
                }

                if (state >= 0)
                    g.Restore(state);
            }
        }
    }

    private float GetStoredFieldFindScrollX(DomElement element, LayoutBox box)
    {
        // Painting is deliberately read-only: it must never rewrite either the
        // live editor viewport or Find's temporary viewport as a side effect of
        // drawing a highlight.  Rewriting during paint was what made a manual
        // scroll/click fight with Find and visibly "snap" back.
        return GetFieldViewportScrollX(element, box);
    }

    private void SetStoredFieldFindScrollX(DomElement element, LayoutBox box, float value)
    {
        float max = GetSingleLineFieldMaxScroll(element, box);
        value = Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, max);
        if (ReferenceEquals(element, _focusedInput))
            _fieldScrollX = value;
        else
            _fieldFindScrollXs[element] = value;

        // The root/frame display lists are cached. This control-local viewport
        // can change during Find navigation, during manual horizontal scrolling
        // just before blur, or while focus is being transferred. Invalidate the
        // cached list so the renderer and the find overlay both consume the same
        // horizontal offset; otherwise one can move while the other remains at
        // the old position. This call is intentionally local to field scroll
        // changes and does not reflow the document.
        InvalidateDisplayLists();
    }

    private static Color GetFindControlTextColor(DomElement element)
    {
        if (element.HasAttr("disabled")) return Color.Gray;
        var style = element.Style;
        return style != null && style.OwnColor ? style.Color : Color.Black;
    }

    private void EnsureFindControlInternalScrollVisible(FindMatch match)
    {
        var element = match.SourceElement;
        if (element == null || !IsSearchableControlElement(element)) return;
        if (_findFieldViewportUserOverrides.Contains(element)) return;
        LayoutBox? box = match.Box;
        if (box == null || element.TagName is not ("input" or "textarea")) return;
        var face = IntersectRect(box.ContentRect, box.BorderRect);
        if (face.Width <= 0.01f || face.Height <= 0.01f) return;
        var font = ResolveFieldFont(element);
        if (font == null) return;

        int start = Math.Max(0, match.Start);
        int end = Math.Max(start, match.Start + match.Length);

        if (element.TagName == "input")
        {
            string type = element.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
            if (type == "file") return; // File inputs have a fixed two-part visual layout, not an editor scroll.

            string source = GetSelectableControlText(element);
            start = Math.Clamp(start, 0, source.Length);
            end = Math.Clamp(end, start, source.Length);
            if (end <= start) return;

            using var format = NewFieldFormat(noWrap: true);
            string visual = GlyphSubstitution.MapGlyphs(source);
            float visibleWidth = Math.Max(1f, face.Width - 6f);
            float totalWidth = MeasureGraphics.MeasureString(visual, font, int.MaxValue, format).Width;
            float alignOffset = GetSingleLineFieldAlignmentOffset(visibleWidth, totalWidth, GetFieldTextAlign(element));
            float baseX = face.Left + 3f + alignOffset;
            float prefixStart = MeasureGraphics.MeasureString(visual[..Math.Min(start, visual.Length)], font, int.MaxValue, format).Width;
            float prefixEnd = MeasureGraphics.MeasureString(visual[..Math.Min(end, visual.Length)], font, int.MaxValue, format).Width;
            float fieldVisibleLeft = face.Left + 2f;
            float fieldVisibleRight = face.Right - 2f;
            float scroll = GetStoredFieldFindScrollX(element, box);
            float left = baseX + prefixStart - scroll;
            float right = baseX + prefixEnd - scroll;

            if (left < fieldVisibleLeft)
                scroll += left - fieldVisibleLeft;
            if (right > fieldVisibleRight)
                scroll += right - fieldVisibleRight;
            SetStoredFieldFindScrollX(element, box, scroll);
            return;
        }

        string text = GetFieldText(element);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (end <= start) return;

        bool wrapOff = element.GetAttrOrDefault("wrap", "").Trim()
            .Equals("off", StringComparison.OrdinalIgnoreCase);
        var geo = GetTextareaGeometry(element, box, match.Frame);
        if (geo == null) return;
        float lineHeight = Math.Max(1f, geo.Font.GetHeight(MeasureGraphics));
        var state = GetTextareaRenderState(element);
        float scrollX = state.ScrollX;
        int scrollLine = state.ScrollLine;
        var rects = TextareaOverlay.SelectionRects(
            MeasureGraphics, text, font, face, 0f, 0f, start, end, wrapOff);
        if (rects.Count == 0) return;

        float visibleLeft = face.Left + 2f;
        float visibleRight = face.Right - 2f;
        float minX = rects.Min(r => r.Left);
        float maxX = rects.Max(r => r.Right);
        if (maxX - scrollX > visibleRight)
            scrollX += maxX - scrollX - visibleRight;
        if (minX - scrollX < visibleLeft)
            scrollX += minX - scrollX - visibleLeft;

        float visibleTop = face.Top + 2f;
        float visibleBottom = face.Bottom - 2f;
        float minY = rects.Min(r => r.Top);
        float maxY = rects.Max(r => r.Bottom);
        float scrollY = scrollLine * lineHeight;
        if (maxY - scrollY > visibleBottom)
            scrollY = maxY - visibleBottom;
        if (minY - scrollY < visibleTop)
            scrollY = minY - visibleTop;
        int maxLine = Math.Max(0, geo.Lines.Count - geo.VisibleLines);
        scrollLine = Math.Clamp((int)Math.Round(Math.Max(0f, scrollY) / lineHeight), 0, maxLine);

        _textareaScrollXs[element] = Math.Max(0f, scrollX);
        _textareaScrollLines[element] = scrollLine;
        if (ReferenceEquals(element, _focusedInput))
        {
            _fieldScrollX = Math.Max(0f, scrollX);
            _textareaScrollLine = scrollLine;
        }
    }

    private void PaintSearchableControlFindHighlight(Graphics g, FindMatch match,
                                                      RectangleF face, float offsetX, float offsetY,
                                                      Brush matchBrush, Brush activeBrush,
                                                      Pen matchPen, Pen activePen, bool active)
    {
        var el = match.SourceElement;
        if (el == null) return;

        string source = GetSelectableControlText(el);
        if (string.IsNullOrEmpty(source) || match.Length <= 0) return;
        var font = ResolveFieldFont(el);
        if (font == null) return;

        int start = Math.Clamp(match.Start, 0, source.Length);
        int end = Math.Clamp(match.Start + match.Length, start, source.Length);
        if (end <= start) return;

        using var format = NewFieldFormat(noWrap: true);
        format.LineAlignment = StringAlignment.Center;
        string visualSource = GlyphSubstitution.MapGlyphs(source);
        if (visualSource.Length == 0) return;
        Color textColor = GetFindControlTextColor(el);

        string inputType = el.TagName == "input"
            ? el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant()
            : string.Empty;

        void DrawFindTextRange(RectangleF textRect, string visualText, int rangeStart, int rangeEnd,
                               StringAlignment align, bool clipToRect = true)
        {
            if (rangeEnd <= rangeStart || visualText.Length == 0) return;
            rangeStart = Math.Clamp(rangeStart, 0, visualText.Length);
            rangeEnd = Math.Clamp(rangeEnd, rangeStart, visualText.Length);
            float totalWidth = g.MeasureString(visualText, font, int.MaxValue, format).Width;
            float x = textRect.Left;
            if (align == StringAlignment.Center)
                x += Math.Max(0f, (textRect.Width - totalWidth) * 0.5f);
            else if (align == StringAlignment.Far)
                x += Math.Max(0f, textRect.Width - totalWidth);

            float p0 = g.MeasureString(visualText[..rangeStart], font, int.MaxValue, format).Width;
            float p1 = g.MeasureString(visualText[..rangeEnd], font, int.MaxValue, format).Width;
            var drawRect = new RectangleF(
                x + p0 + offsetX,
                CenteredFieldTextTop(textRect, Math.Max(1f, font.GetHeight(g))) + offsetY,
                Math.Max(1f, p1 - p0 + 1.0f),
                Math.Max(1f, font.GetHeight(g)));

            int state = g.Save();
            try
            {
                if (clipToRect)
                    g.SetClip(new RectangleF(textRect.X + offsetX, textRect.Y + offsetY,
                        Math.Max(1f, textRect.Width), Math.Max(1f, textRect.Height)), CombineMode.Intersect);
                g.FillRectangle(matchBrush, drawRect);
                g.DrawRectangle(matchPen, drawRect.X, drawRect.Y,
                    Math.Max(0.5f, drawRect.Width - 0.5f),
                    Math.Max(0.5f, drawRect.Height - 0.5f));
                // The live renderer already paints the actual glyphs. Redrawing
                // them here with GDI introduced a second, differently-measured
                // copy of the text, which became visibly offset/overlapped while
                // a field was horizontally scrolled. The translucent fill +
                // border is enough to highlight the existing glyphs.
            }
            finally { g.Restore(state); }
        }

        if (inputType == "file")
        {
            const string buttonLabel = "Choose File";
            string chosen = el.GetAttr("data-file-name") ?? string.Empty;
            string label = chosen.Length == 0 ? "No file chosen" : chosen;
            int labelStart = buttonLabel.Length + 1;

            float lineHeight = Math.Max(1f, font.GetHeight(g));
            float buttonWidth = 92f;
            float actualButtonWidth = Math.Min(buttonWidth, Math.Max(34f, face.Width - 8f));
            var button = new RectangleF(
                face.X + 2f, face.Y + 2f, actualButtonWidth, Math.Max(1f, face.Height - 4f));
            var labelRect = new RectangleF(
                button.Right + 6f, face.Y + 1f,
                Math.Max(0f, face.Right - button.Right - 9f),
                Math.Max(1f, face.Height - 2f));

            // The file input exposes one logical search string but two distinct
            // rendered regions. Split the match at the visual boundary so each
            // part is highlighted in the exact place where Renderer draws it.
            int buttonEnd = Math.Min(end, buttonLabel.Length);
            if (start < buttonLabel.Length && buttonEnd > start)
                DrawFindTextRange(button, buttonLabel, start, buttonEnd, StringAlignment.Center, clipToRect: true);

            int labelRangeStart = Math.Max(start, labelStart) - labelStart;
            int labelRangeEnd = Math.Min(end, labelStart + label.Length) - labelStart;
            if (labelRangeEnd > labelRangeStart)
            {
                DrawFindTextRange(labelRect, GlyphSubstitution.MapGlyphs(label),
                    labelRangeStart, labelRangeEnd, StringAlignment.Near, clipToRect: true);
            }
            return;
        }

        if (el.TagName == "select")
        {
            var options = el.Descendants().OfType<DomElement>()
                .Where(o => o.TagName == "option").ToList();
            bool listbox = el.HasAttr("multiple") || el.GetAttrInt("size", 1) > 1;
            if (!listbox)
            {
                var selected = options.FirstOrDefault(o => o.HasAttr("selected")) ?? options.FirstOrDefault();
                if (selected == null) return;
                string selectedText = GetSelectOptionText(selected);
                source = selectedText;
                start = Math.Clamp(start, 0, source.Length);
                end = Math.Clamp(end, start, source.Length);
                visualSource = GlyphSubstitution.MapGlyphs(source);
                float textRectWidth = Math.Max(1f, face.Width - 18f);
                var textRect = new RectangleF(face.Left + 3f, face.Top, textRectWidth, face.Height);
                DrawFindTextRange(textRect, visualSource, start, end, StringAlignment.Near, true);
                return;
            }

            int global = 0;
            int visibleRows = GetSelectVisibleRows(el);
            int scrollOffset = GetSelectScrollOffset(el);
            float rowH = font.GetHeight(g) + 2f;
            foreach (var (option, optionIndex) in options.Select((o, i) => (o, i)))
            {
                string optionText = GetSelectOptionText(option);
                int rowEnd = global + optionText.Length;
                if (start < rowEnd && end > global)
                {
                    int visibleIndex = optionIndex - scrollOffset;
                    if (visibleIndex < 0 || visibleIndex >= visibleRows) return;
                    string rowVisual = GlyphSubstitution.MapGlyphs(optionText);
                    int localStart = Math.Clamp(start - global, 0, optionText.Length);
                    int localEnd = Math.Clamp(end - global, localStart, optionText.Length);
                    var rowRect = new RectangleF(face.Left + 3f,
                        face.Top + visibleIndex * rowH + 1f,
                        Math.Max(1f, face.Width - 14f), rowH);
                    DrawFindTextRange(rowRect, rowVisual, localStart, localEnd, StringAlignment.Near, true);
                    return;
                }
                global += optionText.Length + 1;
            }
            return;
        }

        if (el.TagName == "button" || (el.TagName == "input" &&
            inputType is "submit" or "reset" or "button"))
        {
            var textRect = RectangleF.Inflate(match.Box.BorderRect, -2f, -2f);
            if (ReferenceEquals(el, _pressedControl))
                textRect.Offset(1f, 1f);
            DrawFindTextRange(textRect, visualSource, start, end, StringAlignment.Center, true);
            return;
        }

        // Single-line text inputs: use the same internal horizontal scroll as
        // the field renderer. This keeps a find match attached to its characters
        // while the user scrolls the value left/right and clips it at the face.
        float lineHeight2 = Math.Max(1f, font.GetHeight(g));
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float total = g.MeasureString(visualSource, font, int.MaxValue, format).Width;
        float align = GetSingleLineFieldAlignmentOffset(visibleWidth, total, GetFieldTextAlign(el));
        float scrollX = GetStoredFieldFindScrollX(el, match.Box);
        float textX = face.Left + 3f + align - scrollX;
        float pStart = g.MeasureString(visualSource[..Math.Min(start, visualSource.Length)], font, int.MaxValue, format).Width;
        float pEnd = g.MeasureString(visualSource[..Math.Min(end, visualSource.Length)], font, int.MaxValue, format).Width;
        if (pEnd <= pStart) return;

        var drawRect = new RectangleF(
            textX + pStart + offsetX,
            CenteredFieldTextTop(face, lineHeight2) + offsetY,
            Math.Max(1f, pEnd - pStart + 1.0f), lineHeight2);
        var clipRect = new RectangleF(face.Left + offsetX, face.Top + offsetY,
            Math.Max(1f, face.Width), Math.Max(1f, face.Height));
        int state2 = g.Save();
        try
        {
            g.SetClip(clipRect, CombineMode.Intersect);
            g.FillRectangle(matchBrush, drawRect);
            g.DrawRectangle(matchPen, drawRect.X, drawRect.Y,
                Math.Max(0.5f, drawRect.Width - 0.5f),
                Math.Max(0.5f, drawRect.Height - 0.5f));
            using var findTextBrush = new SolidBrush(textColor);
            g.DrawString(visualSource[start..end], font, findTextBrush, drawRect, format);
        }
        finally { g.Restore(state2); }
    }

    private void PaintTextareaFindHighlight(Graphics g, FindMatch match,
                                               RectangleF face, float offsetX, float offsetY,
                                               Brush matchBrush, Brush activeBrush,
                                               Pen matchPen, Pen activePen, bool active)
    {
        var el = match.SourceElement;
        if (el == null || el.TagName != "textarea") return;

        var font = ResolveFieldFont(el);
        if (font == null) return;

        string text = GetFieldText(el);
        if (text.Length == 0 || match.Length <= 0) return;

        bool wrapOff = el.GetAttrOrDefault("wrap", "").Trim()
            .Equals("off", StringComparison.OrdinalIgnoreCase);
        var state = GetTextareaRenderState(el);
        var geometry = GetTextareaGeometry(el, match.Box, match.Frame);
        if (geometry == null) return;

        float lineHeight = geometry.Font.GetHeight(g);
        int maxScrollLine = Math.Max(0, geometry.Lines.Count - geometry.VisibleLines);
        int scrollLine = Math.Clamp(state.ScrollLine, 0, maxScrollLine);
        float scrollY = scrollLine * lineHeight;

        int start = Math.Clamp(match.Start, 0, text.Length);
        int end = Math.Clamp(match.Start + match.Length, start, text.Length);
        if (end <= start) return;

        // Use the same line-breaking + prefix-measurement code as the live
        // textarea editor. That makes a find match line up with the actual
        // characters even when the match wraps across visual lines.
        var rects = Engine.Render.TextareaOverlay.SelectionRects(
            g, text, font, face, state.ScrollX, scrollY, start, end, wrapOff);
        if (rects.Count == 0) return;

        var clipRect = new RectangleF(
            face.X + 1f + offsetX, face.Y + 1f + offsetY,
            Math.Max(1f, face.Width - 2f), Math.Max(1f, face.Height - 2f));

        int clipState = g.Save();
        try
        {
            g.SetClip(clipRect, CombineMode.Intersect);
            foreach (var rect in rects)
            {
                if (rect.Width <= 0.01f || rect.Height <= 0.01f) continue;

                var drawRect = new RectangleF(
                    rect.X + offsetX, rect.Y + offsetY, rect.Width, rect.Height);
                g.FillRectangle(active ? activeBrush : matchBrush, drawRect);
                g.DrawRectangle(active ? activePen : matchPen, drawRect.X, drawRect.Y,
                    Math.Max(0.5f, drawRect.Width - 0.5f),
                    Math.Max(0.5f, drawRect.Height - 0.5f));
            }
        }
        finally
        {
            g.Restore(clipState);
        }
    }

    private void PaintFieldOverlay(Graphics g)
    {
        var el = _focusedInput;
        if (el == null || _rootBox == null || !IsEditableField(el)) return;

        var box = FindBoxForElement(_rootBox, el);
        if (box == null || _fontCache == null) return;

        if (el.TagName == "textarea")
        {
            PaintTextareaFieldOverlay(g);
            return;
        }

        var face = IntersectRect(box.ContentRect, box.BorderRect);
        if (face.Width <= 0.01f || face.Height <= 0.01f) return;
        string text = GetFieldText(el);
        bool password = el.GetAttrOrDefault("type", "text").Trim().Equals("password", StringComparison.OrdinalIgnoreCase);
        if (password) text = new string('*', text.Length);

        var font = ResolveFieldFont(el);
        if (font == null) return;
        using var fmt = NewFieldFormat(noWrap: true);

        float scrollX = PaintScrollX;
        float scrollY = PaintScrollY;
        using var scrollFormat = NewFieldFormat(noWrap: true);
        float visibleWidthForScroll = Math.Max(1f, face.Width - 6f);
        float passwordAdvanceForScroll = password ? GetPasswordGlyphAdvance(g, font, scrollFormat) : 0f;
        float totalWidthForScroll = GetSingleLineFieldTextWidth(
            g, text, font, scrollFormat, password, passwordAdvanceForScroll);
        float maxFieldScroll = Math.Max(0f, totalWidthForScroll - visibleWidthForScroll);
        float fieldScroll = Math.Clamp(_fieldScrollX, 0f, maxFieldScroll);
        bool needsTextRepaint = fieldScroll > 0.01f;

        float passwordAdvance = password ? GetPasswordGlyphAdvance(g, font, fmt) : 0f;
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float totalTextWidth = GetSingleLineFieldTextWidth(
            g, text, font, fmt, password, passwordAdvance);
        TextAlign fieldTextAlign = GetFieldTextAlign(el);
        float alignOffset = GetSingleLineFieldAlignmentOffset(
            visibleWidth, totalTextWidth, fieldTextAlign);
        float textX = face.X + 3 + alignOffset - scrollX - fieldScroll;

        float MeasureTo(int n) => password
            ? Math.Clamp(n, 0, text.Length) * passwordAdvance
            : n <= 0 ? 0f
            : g.MeasureString(text[..Math.Min(n, text.Length)], font, int.MaxValue, fmt).Width;

        int caret = Math.Clamp(_fieldCaret, 0, text.Length);
        int anchor = Math.Clamp(_fieldSelAnchor, 0, text.Length);
        int selStart = Math.Min(anchor, caret);
        int selEnd = Math.Max(anchor, caret);
        float lineH = font.GetHeight(g);
        float top = face.Y + Math.Max(1f, (face.Height - lineH) / 2f) - scrollY;
        float bottom = Math.Min(top + lineH, face.Bottom - 1 - scrollY);

        var oldClip = g.Save();
        g.TextRenderingHint = password
            ? Retro96.Drawing.TextRenderingHint.SingleBitPerPixelGridFit
            : Retro96.Drawing.TextRenderingHint.ClearTypeGridFit;
        g.SetClip(new RectangleF(face.X + 2 - scrollX, face.Y - scrollY,
                                 Math.Max(1, face.Width - 4), face.Height),
                  CombineMode.Intersect);

        var fieldStyle = el.Style;
        Color fieldBackground = fieldStyle != null && fieldStyle.OwnBackground && fieldStyle.BackgroundColor != Color.Transparent
            ? fieldStyle.BackgroundColor : Color.White;
        Color fieldForeground = fieldStyle != null && fieldStyle.OwnColor ? fieldStyle.Color : Color.Black;

        // Selection in text/password inputs is painted from the logical value
        // rather than relying on the already-rasterized page bitmap.  Password
        // fields are especially sensitive here: the renderer has already
        // replaced every character with '*', while a focused-field overlay can
        // otherwise measure/paint a slightly different run and leave the last
        // visible mask glyph outside the highlight. Repainting the content face
        // when a selection exists keeps the masked text, its character advances,
        // and the selection rectangle on exactly the same coordinate system.
        bool paintLiveFieldText = needsTextRepaint || selEnd > selStart;
        using var focusedTextFormat = NewFieldFormat(noWrap: true);
        focusedTextFormat.LineAlignment = StringAlignment.Center;

        if (paintLiveFieldText)
        {
            using (var background = new SolidBrush(fieldBackground))
                g.FillRectangle(background, face.X + 2 - scrollX, face.Y - scrollY,
                    Math.Max(1, face.Width - 4), face.Height);

            float visibleLeft = face.X + 2 - scrollX;
            float visibleRight = face.Right - 2 - scrollX;
            float x = textX;

            void DrawSegment(int from, int to, Color color)
            {
                if (to <= from) return;
                using var brush = new SolidBrush(color);
                if (password)
                {
                    // Password glyphs are laid out one-by-one using the exact same
                    // fixed advance used for caret hit-testing and selection bounds.
                    // This prevents star overlap, last-glyph drift, and a caret that
                    // lands between masked characters.
                    for (int i = from; i < to; i++)
                    {
                        float gx = x + i * passwordAdvance;
                        if (gx + passwordAdvance <= visibleLeft || gx >= visibleRight) continue;
                        PasswordMaskLayout.DrawRange(g, text.Length, font, brush,
                            textX, face.Y - scrollY, face.Height, i, i + 1, focusedTextFormat);
                    }
                    return;
                }

                float x1 = x + MeasureTo(from);
                float x2 = x + MeasureTo(to);
                if (x2 <= visibleLeft || x1 >= visibleRight) return;
                g.DrawString(text.Substring(from, to - from), font, brush,
                    new RectangleF(x1, face.Y - scrollY,
                        Math.Max(x2 - x1, 1f), face.Height), focusedTextFormat);
            }

            if (selEnd > selStart)
            {
                float sx1 = Math.Max(visibleLeft, x + MeasureTo(selStart));
                float sx2 = Math.Min(visibleRight, x + MeasureTo(selEnd));
                if (sx2 > sx1)
                {
                    using var selBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
                    g.FillRectangle(selBrush, sx1, top, Math.Max(1f, sx2 - sx1 + 1f), Math.Max(1f, bottom - top));
                }

                DrawSegment(0, selStart, fieldForeground);
                DrawSegment(selStart, selEnd, Color.White);
                DrawSegment(selEnd, text.Length, fieldForeground);
            }
            else
            {
                DrawSegment(0, text.Length, fieldForeground);
            }
        }

        if ((uint)Environment.TickCount / 500 % 2 == 0)
        {
            float cx = textX + MeasureTo(caret);
            using var caretPen = new Pen(fieldForeground, 1);
            g.DrawLine(caretPen, cx, top, cx, bottom);
        }
        g.Restore(oldClip);

        using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
        g.DrawRectangle(focusPen, face.X - scrollX, face.Y - scrollY,
            face.Width - 1, face.Height - 1);
    }

    private bool IsFocusedFieldSuppressedByPageSelection()
    {
        if (_focusedInput == null || _selAnchor == null || _selFocus == null)
            return _selectAllPage;

        LayoutBox? root = _selectionFrame?.RootBox ?? _rootBox;
        if (root == null) return false;
        var focusedRoot = _focusedInputFrame?.RootBox ?? _rootBox;
        if (focusedRoot == null) return false;
        var fieldBox = FindBoxForElement(focusedRoot, _focusedInput);
        if (fieldBox == null) return false;

        // A page selection is painted by one range overlay. Painting the focused
        // control again from the caret overlay produces the exact ghost rectangle
        // seen beside the visitor/time inputs. Treat the live field layer as
        // mutually exclusive with a page-owned selection.
        var ordered = OrderedSelectionRangeBoxes(root);
        int fieldIndex = ordered.IndexOf(fieldBox);
        int a = ordered.IndexOf(_selAnchor);
        int b = ordered.IndexOf(_selFocus);
        if (fieldIndex < 0 || a < 0 || b < 0) return false;
        int first = Math.Min(a, b);
        int last = Math.Max(a, b);
        return fieldIndex >= first && fieldIndex <= last;
    }

    private static void PaintFrameBorder(Graphics g, RectangleF rect)
    {
        if (rect.Width < 2 || rect.Height < 2) return;
        using var light = new Pen(Color.FromArgb(0xF0, 0xF0, 0xF0), 1);
        using var dark = new Pen(Color.FromArgb(0x40, 0x40, 0x40), 1);
        g.DrawRectangle(light, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        g.DrawLine(dark, rect.Left, rect.Bottom - 2, rect.Right - 1, rect.Bottom - 2);
        g.DrawLine(dark, rect.Right - 2, rect.Top, rect.Right - 2, rect.Bottom - 2);
    }

    private void PaintFocusedFrameFieldOverlayRecursive(Graphics g, RectangleF frameDestRect, FrameView view)
    {
        if (_focusedInput == null) return;
        if (IsFocusedFieldSuppressedByPageSelection()) return;

        if (ReferenceEquals(_focusedInputFrame, view))
            PaintFrameFieldOverlay(g, frameDestRect, view);

        foreach (var (childBox, childView) in view.ChildFrames)
        {
            var childDestRect = new RectangleF(
                frameDestRect.X + childBox.X - view.Scroll.X,
                frameDestRect.Y + childBox.Y - view.Scroll.Y,
                childBox.Width, childBox.Height);

            PaintFocusedFrameFieldOverlayRecursive(g, childDestRect, childView);
        }
    }

    private void PaintFrameFieldOverlay(Graphics g, RectangleF frameDestRect, FrameView view)
    {
        var el = _focusedInput;
        if (el == null) return;
        if (view.RootBox == null || _fontCache == null) return;
        var box = FindBoxForElement(view.RootBox, el);
        if (box == null) return;

        var localFace = IntersectRect(box.ContentRect, box.BorderRect);
        if (localFace.Width <= 0.01f || localFace.Height <= 0.01f) return;
        var face = new RectangleF(
            frameDestRect.X + localFace.X - view.Scroll.X,
            frameDestRect.Y + localFace.Y - view.Scroll.Y,
            localFace.Width, localFace.Height);
        var font = ResolveFieldFont(el);
        if (font == null) return;

        string text = GetFieldText(el);
        bool password = el.TagName == "input" &&
            el.GetAttrOrDefault("type", "text").Trim().Equals("password",
                StringComparison.OrdinalIgnoreCase);
        if (password)
            text = new string('*', text.Length);

        int caret = Math.Clamp(_fieldCaret, 0, text.Length);
        int anchor = Math.Clamp(_fieldSelAnchor, 0, text.Length);
        int selStart = Math.Min(anchor, caret);
        int selEnd = Math.Max(anchor, caret);
        var state = g.Save();
        g.TextRenderingHint = password
            ? Retro96.Drawing.TextRenderingHint.SingleBitPerPixelGridFit
            : Retro96.Drawing.TextRenderingHint.ClearTypeGridFit;
        g.SetClip(face, CombineMode.Intersect);

        if (el.TagName == "textarea")
        {
            var geo = GetTextareaGeometry(el, box, _focusedInputFrame);
            if (geo != null)
            {
                float lineHeight = geo.Font.GetHeight(g);
                int visibleLines = geo.VisibleLines;
                int maxScrollLine = Math.Max(0, geo.Lines.Count - visibleLines);
                int scrollLine = Math.Clamp(_textareaScrollLine, 0, maxScrollLine);
                bool needsTextRepaint = scrollLine != 0 || _fieldScrollX > 0.01f;
                float textX = face.X + 3 - _fieldScrollX;
                float textY = face.Y + 2;
                using var lineFormat = NewFieldFormat(noWrap: true);

                if (needsTextRepaint)
                {
                    var style = el.Style;
                    Color backgroundColor = style != null && style.OwnBackground &&
                        style.BackgroundColor != Color.Transparent ? style.BackgroundColor : Color.White;
                    Color foregroundColor = style != null && style.OwnColor ? style.Color : Color.Black;
                    using var background = new SolidBrush(backgroundColor);
                    using var foreground = new SolidBrush(foregroundColor);
                    g.FillRectangle(background, face);
                    Engine.Render.TextareaOverlay.DrawLines(g, text, geo.Font, geo.Lines,
                        foreground, textX, textY, geo.TextWidth, lineHeight, scrollLine);
                }

                float Measure(int start, int end) => end <= start ? 0f
                    : g.MeasureString(text[start..end], geo.Font, int.MaxValue, lineFormat).Width;

                if (selEnd > selStart)
                {
                    using var highlight = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
                    for (int line = 0; line < geo.Lines.Count; line++)
                    {
                        var (start, end) = geo.Lines[line];
                        int a = Math.Max(selStart, start), b = Math.Min(selEnd, end);
                        if (b <= a) continue;
                        float x1 = textX + Measure(start, a);
                        float x2 = textX + Measure(start, b);
                        g.FillRectangle(highlight, x1,
                            textY + (line - scrollLine) * lineHeight,
                            Math.Max(1f, x2 - x1 + 1f), lineHeight);
                    }
                }

                if ((uint)Environment.TickCount / 500 % 2 == 0 && geo.Lines.Count > 0)
                {
                    int line = CaretLineIndex(geo.Lines, caret);
                    var (start, _) = geo.Lines[line];
                    float cx = textX + Measure(start, caret);
                    float caretY = textY + (line - scrollLine) * lineHeight;
                    using var caretPen = new Pen(Color.Black, 1);
                    g.DrawLine(caretPen, cx, caretY, cx, caretY + lineHeight);
                }

                if (needsTextRepaint && geo.NeedsVerticalScrollbar)
                    PaintTextareaScrollbar(g, new RectangleF(face.X, face.Y, face.Width, face.Height),
                        geo.Lines.Count, lineHeight, scrollLine);
            }
        }
        else
        {
            using var format = NewFieldFormat(noWrap: true);
            format.LineAlignment = StringAlignment.Center;
            float scroll = EnsureSingleLineCaretVisible(g, text, font, box.ContentRect, caret, password);
            float passwordAdvance = password ? GetPasswordGlyphAdvance(g, font, format) : 0f;
            float visibleWidth = Math.Max(1f, box.ContentRect.Width - 6f);
            float totalTextWidth = GetSingleLineFieldTextWidth(
                g, text, font, format, password, passwordAdvance);
            TextAlign fieldTextAlign = GetFieldTextAlign(el);
            float alignOffset = GetSingleLineFieldAlignmentOffset(
                visibleWidth, totalTextWidth, fieldTextAlign);
            float textX = face.X + 3 + alignOffset - scroll;
            float MeasureTo(int n) => password
                ? Math.Clamp(n, 0, text.Length) * passwordAdvance
                : n <= 0 ? 0f
                : g.MeasureString(text[..Math.Min(n, text.Length)], font, int.MaxValue, format).Width;

            var style = el.Style;
            Color backgroundColor = style != null && style.OwnBackground &&
                style.BackgroundColor != Color.Transparent ? style.BackgroundColor : Color.White;
            Color foregroundColor = style != null && style.OwnColor ? style.Color : Color.Black;
            bool paintLiveFieldText = scroll > 0.01f || selEnd > selStart;

            if (paintLiveFieldText)
            {
                using var background = new SolidBrush(backgroundColor);
                g.FillRectangle(background, face);

                float visibleLeft = face.X + 2;
                float visibleRight = face.Right - 2;
                void DrawFrameSegment(int from, int to, Color color)
                {
                    if (to <= from) return;
                    using var brush = new SolidBrush(color);
                    if (password)
                    {
                        for (int i = from; i < to; i++)
                        {
                            float gx = textX + i * passwordAdvance;
                            if (gx + passwordAdvance <= visibleLeft || gx >= visibleRight) continue;
                            PasswordMaskLayout.DrawRange(g, text.Length, font, brush,
                                textX, face.Y, face.Height, i, i + 1, format);
                        }
                        return;
                    }

                    float x1 = textX + MeasureTo(from);
                    float x2 = textX + MeasureTo(to);
                    if (x2 <= visibleLeft || x1 >= visibleRight) return;
                    g.DrawString(text.Substring(from, to - from), font, brush,
                        new RectangleF(x1, face.Y, Math.Max(x2 - x1, 1f), face.Height), format);
                }

                if (selEnd > selStart)
                {
                    float sx1 = Math.Max(visibleLeft, textX + MeasureTo(selStart));
                    float sx2 = Math.Min(visibleRight, textX + MeasureTo(selEnd));
                    if (sx2 > sx1)
                    {
                        using var highlight = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
                        g.FillRectangle(highlight, sx1, face.Y, Math.Max(1f, sx2 - sx1 + 1f), face.Height);
                    }
                    DrawFrameSegment(0, selStart, foregroundColor);
                    DrawFrameSegment(selStart, selEnd, Color.White);
                    DrawFrameSegment(selEnd, text.Length, foregroundColor);
                }
                else
                {
                    DrawFrameSegment(0, text.Length, foregroundColor);
                }
            }
            if ((uint)Environment.TickCount / 500 % 2 == 0)
            {
                float cx = textX + MeasureTo(caret);
                using var caretPen = new Pen(foregroundColor, 1);
                g.DrawLine(caretPen, cx, face.Y + 2, cx, face.Bottom - 2);
            }
        }
        g.Restore(state);

        using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
        g.DrawRectangle(focusPen, face.X, face.Y, face.Width - 1, face.Height - 1);
    }

    private static void PaintTextareaScrollbar(Graphics g, RectangleF face,
                                               int lineCount, float lineHeight,
                                               int scrollLine = 0)
    {
        int visibleLines = Math.Max(1, (int)Math.Floor((face.Height - 4) / lineHeight));
        if (lineCount <= visibleLines || face.Width < 16 || face.Height < 8) return;

        const float barWidth = 14f;
        float trackX = face.Right - barWidth + 1f;
        float trackY = face.Top + 1f;
        float trackHeight = Math.Max(1f, face.Height - 2f);
        using var track = new SolidBrush(Color.FromArgb(0xE0, 0xE0, 0xE0));
        using var thumb = new SolidBrush(Color.FromArgb(0x80, 0x80, 0x80));
        g.FillRectangle(track, trackX, trackY, barWidth - 1f, trackHeight);
        float thumbHeight = Math.Clamp(
            trackHeight * visibleLines / Math.Max(1f, lineCount), 10f, trackHeight - 2f);
        float travel = Math.Max(0f, trackHeight - 2f - thumbHeight);
        int maxLine = Math.Max(0, lineCount - visibleLines);
        float thumbY = trackY + 1f +
            travel * Math.Clamp(scrollLine / (float)Math.Max(1, maxLine), 0f, 1f);
        g.FillRectangle(thumb, trackX + 1f, thumbY,
            Math.Max(1f, barWidth - 3f), thumbHeight);
    }

    private void PaintTextareaFieldOverlay(Graphics g)
    {
        var el = _focusedInput;
        if (el == null) return;
        var geo = GetTextareaGeometry(el, frameView: _focusedInputFrame);
        if (geo == null) return;

        var font = geo.Font;
        var face = geo.Box.ContentRect;
        string text = geo.Text;
        var lines = geo.Lines;
        float scrollX = PaintScrollX;
        float scrollY = PaintScrollY;
        float textX = face.X + 3 - scrollX - _fieldScrollX;
        float textY = face.Y + 2 - scrollY;
        float lineHeight = font.GetHeight(g);
        int visibleLines = geo.VisibleLines;
        int maxScrollLine = Math.Max(0, lines.Count - visibleLines);
        int scrollLine = Math.Clamp(_textareaScrollLine, 0, maxScrollLine);
        bool needsTextRepaint = scrollLine != 0 || _fieldScrollX > 0.01f;
        using var noWrap = NewFieldFormat(noWrap: true);

        float Measure(int start, int end) => end <= start ? 0f
            : g.MeasureString(text[start..end], font, int.MaxValue, noWrap).Width;

        int caret = Math.Clamp(_fieldCaret, 0, text.Length);
        int anchor = Math.Clamp(_fieldSelAnchor, 0, text.Length);
        int selStart = Math.Min(anchor, caret);
        int selEnd = Math.Max(anchor, caret);

        var oldClip = g.Save();
        g.SetClip(new RectangleF(face.X + 1 - scrollX, face.Y + 1 - scrollY,
                                 Math.Max(1, face.Width - 2), Math.Max(1, face.Height - 2)),
                  CombineMode.Intersect);

        if (needsTextRepaint)
        {
            var style = el.Style;
            Color fieldBackground = style != null && style.OwnBackground && style.BackgroundColor != Color.Transparent
                ? style.BackgroundColor : Color.White;
            Color fieldForeground = style != null && style.OwnColor ? style.Color : Color.Black;
            using (var background = new SolidBrush(fieldBackground))
                g.FillRectangle(background, face.X + 1 - scrollX, face.Y + 1 - scrollY,
                    Math.Max(1, face.Width - 2), Math.Max(1, face.Height - 2));
            using var foreground = new SolidBrush(fieldForeground);
            Engine.Render.TextareaOverlay.DrawLines(g, text, font, lines, foreground,
                textX, textY, geo.TextWidth, lineHeight, scrollLine);
        }

        if (selEnd > selStart)
        {
            using var highlight = new SolidBrush(Color.FromArgb(120, 0, 0, 170));
            for (int line = 0; line < lines.Count; line++)
            {
                var (start, end) = lines[line];
                int a = Math.Max(selStart, start), b = Math.Min(selEnd, end);
                if (b <= a) continue;
                float x1 = textX + Measure(start, a);
                float x2 = textX + Measure(start, b);
                g.FillRectangle(highlight, x1,
                    textY + (line - scrollLine) * lineHeight,
                    Math.Max(1f, x2 - x1 + 1f), lineHeight);
            }
        }

        if ((uint)Environment.TickCount / 500 % 2 == 0 && lines.Count > 0)
        {
            int line = CaretLineIndex(lines, caret);
            var (start, _) = lines[line];
            float cx = textX + Measure(start, caret);
            float caretY = textY + (line - scrollLine) * lineHeight;
            using var caretPen = new Pen(Color.Black, 1);
            g.DrawLine(caretPen, cx, caretY, cx, caretY + lineHeight);
        }
        g.Restore(oldClip);

        if (needsTextRepaint && geo.NeedsVerticalScrollbar)
        {
            var screenFace = new RectangleF(face.X - scrollX, face.Y - scrollY, face.Width, face.Height);
            PaintTextareaScrollbar(g, screenFace, lines.Count, lineHeight, scrollLine);
        }

        using var focusPen = new Pen(Color.FromArgb(0, 0, 128), 1);
        g.DrawRectangle(focusPen, face.X - scrollX, face.Y - scrollY,
            face.Width - 1, face.Height - 1);
    }

    private bool EmbeddedMidiUiAllowed()
    {
        if (_embeddedMidiElement == null || _rootBox == null) return false;
        if (LegacyMidiLoop.IsTrue(_embeddedMidiElement.GetAttr("hidden"))) return false;
        var box = FindBoxForElement(_rootBox, _embeddedMidiElement);
        if (box == null) return false;
        // A zero-sized plugin or an embed with neither width nor height is
        // treated as hidden/no-UI by the legacy compatibility rules.
        return box.BorderRect.Width > 0f && box.BorderRect.Height > 0f;
    }

    private static int MidiVolumeFromPoint(RectangleF rect, float x)
    {
        float left = rect.Left + 6f;
        float right = rect.Right - 6f;
        if (right <= left) return 0;
        return (int)Math.Round(Math.Clamp((x - left) / (right - left), 0f, 1f) * 100f);
    }

    private void PaintEmbeddedMidiControls(Graphics g)
    {
        if (!EmbeddedMidiUiAllowed())
        {
            _embeddedMidiRect = RectangleF.Empty;
            return;
        }

        var box = FindBoxForElement(_rootBox!, _embeddedMidiElement!);
        if (box == null) return;

        float width = box.BorderRect.Width;
        float height = box.BorderRect.Height;
        var rect = new RectangleF(box.BorderRect.X, box.BorderRect.Y, width, height);
        _embeddedMidiRect = rect;

        var screenRect = new RectangleF(rect.X - _scrollOffset.X, rect.Y - _scrollOffset.Y,
            rect.Width, rect.Height);
        using var background = new SolidBrush(Color.FromArgb(0xC8, 0xC8, 0xC8));
        using var border = new Pen(Color.FromArgb(0x30, 0x30, 0x30));
        g.FillRectangle(background, screenRect);
        g.DrawRectangle(border, screenRect.X, screenRect.Y,
            Math.Max(0f, screenRect.Width - 1f), Math.Max(0f, screenRect.Height - 1f));

        // Small plugin rectangles in the period commonly collapsed to a single
        // play/pause affordance rather than clipping a multi-control skin.
        if (screenRect.Height < 50f || screenRect.Width < 80f)
        {
            float smallButtonW = Math.Min(screenRect.Width - 8f, Math.Max(44f, screenRect.Width - 8f));
            float smallButtonH = Math.Max(18f, screenRect.Height - 8f);
            var button = new RectangleF(screenRect.X + (screenRect.Width - smallButtonW) / 2f,
                screenRect.Y + (screenRect.Height - smallButtonH) / 2f, smallButtonW, smallButtonH);
            PaintMidiButton(g, button, _embeddedMidiPlaying ? "Pause" : "Play", 8f);
            return;
        }

        float buttonY = screenRect.Y + 5f;
        float buttonH = Math.Max(22f, Math.Min(28f, screenRect.Height - 10f));
        float buttonW = Math.Min(46f, Math.Max(34f, (screenRect.Width - 18f) * 0.28f));
        var playButton = new RectangleF(screenRect.X + 5f, buttonY, buttonW, buttonH);
        var stopButton = new RectangleF(playButton.Right + 4f, buttonY, buttonW, buttonH);
        PaintMidiButton(g, playButton, _embeddedMidiPlaying ? "Pause" : "Play", 7f);
        PaintMidiButton(g, stopButton, "Stop", 7f);

        float sliderLeft = stopButton.Right + 7f;
        float sliderRight = screenRect.Right - 7f;
        float sliderY = screenRect.Y + screenRect.Height / 2f + 4f;
        if (sliderRight > sliderLeft + 20f)
        {
            using var labelFont = new Font(FontFamily.GenericSansSerif, 8f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var labelBrush = new SolidBrush(Color.Black);
            g.DrawString("Vol", labelFont, labelBrush, sliderLeft, screenRect.Y + 5f);
            sliderY = screenRect.Y + screenRect.Height - 12f;
            float trackW = sliderRight - sliderLeft;
            using var track = new Pen(Color.FromArgb(0x55, 0x55, 0x55), 2f);
            g.DrawLine(track, sliderLeft, sliderY, sliderRight, sliderY);
            float thumbX = sliderLeft + trackW * (_embeddedMidiVolume / 100f);
            using var fill = new Pen(Color.FromArgb(0x00, 0x00, 0x80), 3f);
            g.DrawLine(fill, sliderLeft, sliderY, thumbX, sliderY);
            using var thumb = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
            using var thumbPen = new Pen(Color.FromArgb(0x30, 0x30, 0x30));
            g.FillRectangle(thumb, thumbX - 3f, sliderY - 5f, 6f, 10f);
            g.DrawRectangle(thumbPen, thumbX - 3f, sliderY - 5f, 6f, 10f);
        }
    }

    private static void PaintMidiButton(Graphics g, RectangleF rect, string label, float fontSize)
    {
        using var b = new SolidBrush(Color.FromArgb(0xE8, 0xE8, 0xE8));
        using var p = new Pen(Color.FromArgb(0x65, 0x65, 0x65));
        g.FillRectangle(b, rect);
        g.DrawRectangle(p, rect.X, rect.Y, Math.Max(0f, rect.Width - 1f), Math.Max(0f, rect.Height - 1f));
        using var font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        var size = g.MeasureString(label, font);
        using var tb = new SolidBrush(Color.Black);
        g.DrawString(label, font, tb,
            rect.X + Math.Max(1f, (rect.Width - size.Width) / 2f),
            rect.Y + Math.Max(0f, (rect.Height - size.Height) / 2f));
    }

    public void SetEmbeddedMidiControls(DomElement element, string label, bool loop, bool playing, int volume = 100)
    {
        _embeddedMidiElement = element;
        _embeddedMidiLabel = label ?? "MIDI";
        _embeddedMidiLoop = loop;
        _embeddedMidiPlaying = playing;
        _embeddedMidiVolume = Math.Clamp(volume, 0, 100);
        Invalidate();
    }

    public void UpdateEmbeddedMidiControls(bool loop, bool playing, int volume = -1)
    {
        if (_embeddedMidiElement == null) return;
        _embeddedMidiLoop = loop;
        _embeddedMidiPlaying = playing;
        if (volume >= 0) _embeddedMidiVolume = Math.Clamp(volume, 0, 100);
        Invalidate();
    }

    public void ClearEmbeddedMidiControls()
    {
        _embeddedMidiElement = null;
        _embeddedMidiPressedAction = null;
        _embeddedMidiVolumeDragging = false;
        _embeddedMidiRect = RectangleF.Empty;
        Invalidate();
    }

    private bool TryBeginEmbeddedMidiControl(float x, float y)
    {
        if (!EmbeddedMidiUiAllowed() || !_embeddedMidiRect.Contains(x, y)) return false;
        float localX = x - _embeddedMidiRect.X;
        float localY = y - _embeddedMidiRect.Y;

        if (_embeddedMidiRect.Height < 50f || _embeddedMidiRect.Width < 80f)
        {
            _embeddedMidiPressedAction = _embeddedMidiPlaying ? "pause" : "play";
            return true;
        }

        float buttonY = 5f;
        float buttonH = Math.Max(22f, Math.Min(28f, _embeddedMidiRect.Height - 10f));
        float buttonW = Math.Min(46f, Math.Max(34f, (_embeddedMidiRect.Width - 18f) * 0.28f));
        if (localY >= buttonY && localY <= buttonY + buttonH)
        {
            if (localX >= 5f && localX <= 5f + buttonW)
            {
                _embeddedMidiPressedAction = _embeddedMidiPlaying ? "pause" : "play";
                return true;
            }
            float stopLeft = 5f + buttonW + 4f;
            if (localX >= stopLeft && localX <= stopLeft + buttonW)
            {
                _embeddedMidiPressedAction = "stop";
                return true;
            }
        }

        float sliderLeft = 5f + buttonW + 4f + buttonW + 7f;
        float sliderRight = _embeddedMidiRect.Width - 7f;
        float sliderY = _embeddedMidiRect.Height - 12f;
        if (sliderRight > sliderLeft + 20f && Math.Abs(localY - sliderY) <= 9f)
        {
            _embeddedMidiVolumeDragging = true;
            int volume = MidiVolumeFromPoint(
                new RectangleF(_embeddedMidiRect.X + sliderLeft, 0,
                    sliderRight - sliderLeft, _embeddedMidiRect.Height), x);
            _embeddedMidiVolume = volume;
            _embeddedMidiPressedAction = "volume:" + volume;
            Invalidate();
            return true;
        }
        return false;
    }
    // ─────────────────────────────────────────────────────────────────────
    // Scroll
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns the logical scrollable document extent from the CURRENT
    /// layout tree, never from the last raster. During a zoom transition the
    /// bitmap can briefly be at a different scale; basing scrollbar geometry
    /// on that stale raster can falsely report zero overflow and snap the page
    /// back to (0,0).
    /// </summary>
    private (float Width, float Height) GetDocumentContentSize()
    {
        var viewport = GetViewportSize();
        float layoutZoom = Math.Max(0.25f, _layoutZoom);
        if (_rootBox == null)
            return (Math.Max(1f, viewport.Width / layoutZoom), Math.Max(1f, viewport.Height / layoutZoom));

        if (_documentContentExtentDirty)
        {
            float width = Math.Max(1f, _rootBox.Width);
            float height = Math.Max(1f, _rootBox.Height);
            foreach (var box in _rootBox.Descendants())
            {
                if (ReferenceEquals(box, _rootBox)) continue;
                width = Math.Max(width, box.X + Math.Max(0f, box.Width));
                height = Math.Max(height, box.Y + Math.Max(0f, box.Height));
            }
            _documentContentExtent = new SizeF(width, height);
            _documentContentExtentDirty = false;
        }

        return (
            Math.Max(_documentContentExtent.Width, viewport.Width / layoutZoom),
            Math.Max(_documentContentExtent.Height, viewport.Height / layoutZoom));
    }

    private void ClampScrollOffsetToDocument()
    {
        if (_rootBox == null)
        {
            _scrollOffset = PointF.Empty;
            return;
        }

        var viewport = GetViewportSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        var content = GetDocumentContentSize();
        float maxX = Math.Max(0f, content.Width - viewport.Width / zoom);
        float maxY = Math.Max(0f, content.Height - viewport.Height / zoom);
        _scrollOffset.X = Math.Clamp(float.IsFinite(_scrollOffset.X) ? _scrollOffset.X : 0f, 0f, maxX);
        _scrollOffset.Y = Math.Clamp(float.IsFinite(_scrollOffset.Y) ? _scrollOffset.Y : 0f, 0f, maxY);
    }

    private static (bool Vertical, bool Horizontal) SolveScrollbarVisibility(
        float physicalDocW, float physicalDocH, int clientW, int clientH)
    {
        physicalDocW = Math.Max(0f, float.IsFinite(physicalDocW) ? physicalDocW : 0f);
        physicalDocH = Math.Max(0f, float.IsFinite(physicalDocH) ? physicalDocH : 0f);
        clientW = Math.Max(1, clientW);
        clientH = Math.Max(1, clientH);

        // Test every possible pair instead of iterating the coupled equations.
        // At tight zoom boundaries the old fixed-point iteration could alternate
        // between (V=1,H=0) and (V=0,H=1), which is exactly the one-frame
        // horizontal-scrollbar flap seen after repeated zoom clicks.
        var states = new (bool Vertical, bool Horizontal)[]
        {
            (false, false), (true, false), (false, true), (true, true)
        };
        foreach (var state in states)
        {
            int viewportW = Math.Max(1, clientW - (state.Vertical ? (int)TopScrollbarExtent : 0));
            int viewportH = Math.Max(1, clientH - (state.Horizontal ? (int)TopScrollbarExtent : 0));
            bool needsV = physicalDocH > viewportH + 0.5f;
            bool needsH = physicalDocW > viewportW + 0.5f;
            if (needsV == state.Vertical && needsH == state.Horizontal)
                return state;
        }

        // There is no exact fixed point only for pathological layouts whose
        // content dimensions themselves change between the four states. Choose
        // both bars deterministically rather than allowing the decision to flap.
        return (true, true);
    }

    private void UpdateScrollBars(bool scheduleReflow = true, bool recomputeVisibility = true)
    {
        if (_rootBox == null)
        {
            _showVerticalScrollbar = false;
            _showHorizontalScrollbar = false;
            _scrollbarVisibilityDirty = true;
            _vScroll.Visible = false;
            _hScroll.Visible = false;
            _scrollOffset = PointF.Empty;
            return;
        }

        float zoom = Math.Max(0.25f, EffectiveZoom);
        var content = GetDocumentContentSize();
        float logicalDocW = content.Width;
        float logicalDocH = content.Height;
        float physicalDocW = logicalDocW * zoom;
        float physicalDocH = logicalDocH * zoom;
        bool wasVVisible = _showVerticalScrollbar;
        bool wasHVisible = _showHorizontalScrollbar;

        // Scrollbar visibility is a two-way dependency: a vertical bar reduces
        // the horizontal viewport, while a horizontal bar reduces the vertical
        // viewport. Solve both axes together only when layout/viewport geometry
        // is dirty. A plain repaint must use the state already committed by the
        // stable reflow; otherwise a second pass against the same root can flap
        // the horizontal bar as rounding/centering changes at zoom boundaries.
        bool needV = wasVVisible;
        bool needH = wasHVisible;
        if (recomputeVisibility)
        {
            (needV, needH) = SolveScrollbarVisibility(
                physicalDocW, physicalDocH, ClientSize.Width, ClientSize.Height);
            _scrollbarVisibilityDirty = false;
        }
        else if (IsGestureZoomActive)
        {
            // Gesture zoom is a visual transform, so the CSS layout cannot be
            // rebuilt just to discover that the enlarged page now needs a
            // scrollbar. Calculate the temporary scrollbar state from the
            // effective visual scale. No layout/reflow is scheduled here.
            float gestureZoom = Math.Max(0.25f, EffectiveZoom);
            (needV, needH) = SolveScrollbarVisibility(
                logicalDocW * gestureZoom, logicalDocH * gestureZoom,
                ClientSize.Width, ClientSize.Height);
        }

        int vpW = Math.Max(1, ClientSize.Width - (needV ? (int)TopScrollbarExtent : 0));
        int vpH = Math.Max(1, ClientSize.Height - (needH ? (int)TopScrollbarExtent : 0));

        _showVerticalScrollbar = needV;
        _showHorizontalScrollbar = needH;
        // Keep the hidden controls synchronized for any legacy code that
        // inspects their range, but never let their native paint surface show.
        _vScroll.Visible = false;
        _hScroll.Visible = false;
        // Gesture zoom is visual-only. Do not let its temporary scrollbar
        // visibility changes enqueue a layout reflow at the baseline zoom.
        // Discrete zoom buttons/resize still use the debounced stable reflow.
        bool layoutZoomActive = Math.Abs(_gestureZoom - 1f) <= 0.0005f;
        if (scheduleReflow && layoutZoomActive &&
            (wasVVisible != needV || wasHVisible != needH) &&
            _document != null && _rootBox != null)
        {
            _resizeReflowTimer.Stop();
            _resizeReflowTimer.Start();
        }

        // During a visual gesture, scrollbar visibility remains the committed
        // layout state, but the underlying document can still be visually
        // magnified beyond that state's scroll range. Clamp against the actual
        // scaled content extent rather than snapping the regular scroll offset
        // to zero simply because the scrollbar is hidden.
        float maxX = IsGestureZoomActive
            ? Math.Max(0f, logicalDocW - vpW / zoom)
            : needH ? Math.Max(0f, logicalDocW - vpW / zoom) : 0f;
        float maxY = IsGestureZoomActive
            ? Math.Max(0f, logicalDocH - vpH / zoom)
            : needV ? Math.Max(0f, logicalDocH - vpH / zoom) : 0f;
        _scrollOffset.X = Math.Clamp(_scrollOffset.X, 0f, maxX);
        _scrollOffset.Y = Math.Clamp(_scrollOffset.Y, 0f, maxY);
        int logicalVpW = Math.Max(1, (int)MathF.Floor(vpW / zoom));
        int logicalVpH = Math.Max(1, (int)MathF.Floor(vpH / zoom));

        // Native WinForms scrollbar ranges are maintained only as compatibility
        // metadata; visual scrollbars are rendered directly on the GPU below.
        if (needV)
        {
            int page = logicalVpH;
            int maxValue = (int)Math.Min(int.MaxValue - 1L, MathF.Ceiling(maxY)) + page - 1;
            _vScroll.Minimum = 0; _vScroll.LargeChange = page; _vScroll.SmallChange = Math.Max(1, page / 10);
            _vScroll.Maximum = Math.Max(page - 1, maxValue);
            _vScroll.Value = Math.Clamp((int)MathF.Round(_scrollOffset.Y), 0, Math.Max(0, _vScroll.Maximum - _vScroll.LargeChange + 1));
        }
        else
        {
            _vScroll.Minimum = 0; _vScroll.LargeChange = 1; _vScroll.SmallChange = 1; _vScroll.Maximum = 0; _vScroll.Value = 0;
        }
        if (needH)
        {
            int page = logicalVpW;
            int maxValue = (int)Math.Min(int.MaxValue - 1L, MathF.Ceiling(maxX)) + page - 1;
            _hScroll.Minimum = 0; _hScroll.LargeChange = page; _hScroll.SmallChange = Math.Max(1, page / 10);
            _hScroll.Maximum = Math.Max(page - 1, maxValue);
            _hScroll.Value = Math.Clamp((int)MathF.Round(_scrollOffset.X), 0, Math.Max(0, _hScroll.Maximum - _hScroll.LargeChange + 1));
        }
        else
        {
            _hScroll.Minimum = 0; _hScroll.LargeChange = 1; _hScroll.SmallChange = 1; _hScroll.Maximum = 0; _hScroll.Value = 0;
        }
    }

    private RectangleF GetTopVerticalScrollbarRect(int clientW, int clientH, int viewportW, int viewportH) =>
        new RectangleF(viewportW, 0, TopScrollbarExtent, viewportH);

    private RectangleF GetTopHorizontalScrollbarRect(int clientW, int clientH, int viewportW, int viewportH) =>
        new RectangleF(0, viewportH, viewportW, TopScrollbarExtent);

    private RectangleF GetTopVerticalThumb(RectangleF track, float maxScroll, float scroll)
    {
        const float arrow = TopScrollbarExtent;
        float usable = Math.Max(1f, track.Height - 2f * arrow);
        float ratio = maxScroll <= 0 ? 1f : Math.Clamp(scroll / maxScroll, 0f, 1f);
        var content = GetDocumentContentSize();
        float viewportLogical = GetViewportSize().Height / Math.Max(0.25f, EffectiveZoom);
        float docLogical = Math.Max(viewportLogical, content.Height);
        float thumb = Math.Clamp(usable * viewportLogical / Math.Max(1f, docLogical), 18f, usable);
        float travel = Math.Max(1f, usable - thumb);
        return new RectangleF(track.X + 2f, track.Y + arrow + travel * ratio,
            Math.Max(4f, track.Width - 4f), thumb);
    }

    private RectangleF GetTopHorizontalThumb(RectangleF track, float maxScroll, float scroll)
    {
        const float arrow = TopScrollbarExtent;
        float usable = Math.Max(1f, track.Width - 2f * arrow);
        float ratio = maxScroll <= 0 ? 1f : Math.Clamp(scroll / maxScroll, 0f, 1f);
        var content = GetDocumentContentSize();
        float viewportLogical = GetViewportSize().Width / Math.Max(0.25f, EffectiveZoom);
        float docLogical = Math.Max(viewportLogical, content.Width);
        float thumb = Math.Clamp(usable * viewportLogical / Math.Max(1f, docLogical), 18f, usable);
        float travel = Math.Max(1f, usable - thumb);
        return new RectangleF(track.Left + arrow + travel * ratio, track.Y + 2f,
            thumb, Math.Max(4f, track.Height - 4f));
    }

    private static void PaintTopScrollbarButton(SKCanvas canvas, RectangleF rect, bool upOrLeft, bool pressed)
    {
        using var face = new SKPaint { Color = new SKColor(212, 208, 200), Style = SKPaintStyle.Fill, IsAntialias = false };
        using var light = new SKPaint { Color = new SKColor(255, 255, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
        using var dark = new SKPaint { Color = new SKColor(128, 128, 128), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(rect.X, rect.Y, rect.Width, rect.Height), face);
        canvas.DrawRect(SKRect.Create(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), pressed ? dark : light);
        if (!pressed)
            canvas.DrawLine(rect.Left, rect.Bottom - 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f, dark);

        float cx = rect.Left + rect.Width * 0.5f, cy = rect.Top + rect.Height * 0.5f;
        using var arrow = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
        using var path = new SKPathBuilder();
        if (rect.Height >= rect.Width)
        {
            if (upOrLeft) { path.MoveTo(cx, cy - 4); path.LineTo(cx - 4, cy + 3); path.LineTo(cx + 4, cy + 3); }
            else { path.MoveTo(cx - 4, cy - 3); path.LineTo(cx + 4, cy - 3); path.LineTo(cx, cy + 4); }
        }
        else
        {
            if (upOrLeft) { path.MoveTo(cx - 4, cy); path.LineTo(cx + 3, cy - 4); path.LineTo(cx + 3, cy + 4); }
            else { path.MoveTo(cx + 4, cy); path.LineTo(cx - 3, cy - 4); path.LineTo(cx - 3, cy + 4); }
        }
        path.Close();
        using var p = path.Detach();
        canvas.DrawPath(p, arrow);
    }

    private void PaintTopLevelScrollbars(SKCanvas canvas, int clientW, int clientH, int viewportW, int viewportH)
    {
        var content = GetDocumentContentSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        float maxX = Math.Max(0f, content.Width - viewportW / zoom);
        float maxY = Math.Max(0f, content.Height - viewportH / zoom);

        if (_showVerticalScrollbar)
        {
            var track = GetTopVerticalScrollbarRect(clientW, clientH, viewportW, viewportH);
            using var bg = new SKPaint { Color = new SKColor(212, 208, 200), Style = SKPaintStyle.Fill, IsAntialias = false };
            using var border = new SKPaint { Color = new SKColor(128, 128, 128), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            canvas.DrawRect(SKRect.Create(track.X, track.Y, track.Width, track.Height), bg);
            canvas.DrawRect(SKRect.Create(track.X + .5f, track.Y + .5f, track.Width - 1, track.Height - 1), border);
            PaintTopScrollbarButton(canvas, new RectangleF(track.X, track.Y, track.Width, TopScrollbarExtent), true, false);
            PaintTopScrollbarButton(canvas, new RectangleF(track.X, track.Bottom - TopScrollbarExtent, track.Width, TopScrollbarExtent), false, false);
            var thumb = GetTopVerticalThumb(track, maxY, _scrollOffset.Y);
            using var tf = new SKPaint { Color = new SKColor(128, 128, 128), Style = SKPaintStyle.Fill, IsAntialias = false };
            using var te = new SKPaint { Color = new SKColor(64, 64, 64), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            canvas.DrawRect(SKRect.Create(thumb.X, thumb.Y, thumb.Width, thumb.Height), tf);
            canvas.DrawRect(SKRect.Create(thumb.X + .5f, thumb.Y + .5f, thumb.Width - 1, thumb.Height - 1), te);
        }

        if (_showHorizontalScrollbar)
        {
            var track = GetTopHorizontalScrollbarRect(clientW, clientH, viewportW, viewportH);
            using var bg = new SKPaint { Color = new SKColor(212, 208, 200), Style = SKPaintStyle.Fill, IsAntialias = false };
            using var border = new SKPaint { Color = new SKColor(128, 128, 128), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            canvas.DrawRect(SKRect.Create(track.X, track.Y, track.Width, track.Height), bg);
            canvas.DrawRect(SKRect.Create(track.X + .5f, track.Y + .5f, track.Width - 1, track.Height - 1), border);
            PaintTopScrollbarButton(canvas, new RectangleF(track.X, track.Y, TopScrollbarExtent, track.Height), true, false);
            PaintTopScrollbarButton(canvas, new RectangleF(track.Right - TopScrollbarExtent, track.Y, TopScrollbarExtent, track.Height), false, false);
            var thumb = GetTopHorizontalThumb(track, maxX, _scrollOffset.X);
            using var tf = new SKPaint { Color = new SKColor(128, 128, 128), Style = SKPaintStyle.Fill, IsAntialias = false };
            using var te = new SKPaint { Color = new SKColor(64, 64, 64), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            canvas.DrawRect(SKRect.Create(thumb.X, thumb.Y, thumb.Width, thumb.Height), tf);
            canvas.DrawRect(SKRect.Create(thumb.X + .5f, thumb.Y + .5f, thumb.Width - 1, thumb.Height - 1), te);
        }

        if (_showVerticalScrollbar && _showHorizontalScrollbar)
        {
            using var corner = new SKPaint { Color = new SKColor(212, 208, 200), Style = SKPaintStyle.Fill, IsAntialias = false };
            using var edge = new SKPaint { Color = new SKColor(128, 128, 128), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            var rect = SKRect.Create(viewportW, viewportH, TopScrollbarExtent, TopScrollbarExtent);
            canvas.DrawRect(rect, corner);
            canvas.DrawRect(SKRect.Create(rect.Left + .5f, rect.Top + .5f, rect.Width - 1, rect.Height - 1), edge);
        }
    }

    private bool TryBeginTopLevelScrollbarInteraction(int px, int py)
    {
        int viewportW = Math.Max(1, GetViewportSize().Width);
        int viewportH = Math.Max(1, GetViewportSize().Height);
        var content = GetDocumentContentSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        float maxX = Math.Max(0f, content.Width - viewportW / zoom);
        float maxY = Math.Max(0f, content.Height - viewportH / zoom);

        if (_showVerticalScrollbar && py >= 0 && py < viewportH && px >= viewportW && px < ClientSize.Width)
        {
            var track = GetTopVerticalScrollbarRect(ClientSize.Width, ClientSize.Height, viewportW, viewportH);
            var thumb = GetTopVerticalThumb(track, maxY, _scrollOffset.Y);
            if (thumb.Contains(px, py))
            {
                _topScrollbarDragAxis = TopScrollbarAxis.Vertical;
                _topScrollbarGrabOffset = py - thumb.Top;
                Capture = true;
                return true;
            }
            if (py < track.Top + TopScrollbarExtent)
                ScrollBy(0, -Math.Max(40f, viewportH / zoom));
            else if (py > track.Bottom - TopScrollbarExtent)
                ScrollBy(0, Math.Max(40f, viewportH / zoom));
            else if (py < thumb.Top)
                ScrollBy(0, -Math.Max(1f, viewportH / zoom));
            else if (py > thumb.Bottom)
                ScrollBy(0, Math.Max(1f, viewportH / zoom));
            return true;
        }

        if (_showHorizontalScrollbar && py >= viewportH && py < ClientSize.Height && px >= 0 && px < viewportW)
        {
            var track = GetTopHorizontalScrollbarRect(ClientSize.Width, ClientSize.Height, viewportW, viewportH);
            var thumb = GetTopHorizontalThumb(track, maxX, _scrollOffset.X);
            if (thumb.Contains(px, py))
            {
                _topScrollbarDragAxis = TopScrollbarAxis.Horizontal;
                _topScrollbarGrabOffset = px - thumb.Left;
                Capture = true;
                return true;
            }
            if (px < track.Left + TopScrollbarExtent)
                ScrollBy(-Math.Max(40f, viewportW / zoom), 0);
            else if (px > track.Right - TopScrollbarExtent)
                ScrollBy(Math.Max(40f, viewportW / zoom), 0);
            else if (px < thumb.Left)
                ScrollBy(-Math.Max(1f, viewportW / zoom), 0);
            else if (px > thumb.Right)
                ScrollBy(Math.Max(1f, viewportW / zoom), 0);
            return true;
        }
        return false;
    }

    private bool UpdateTopLevelScrollbarDrag(int px, int py)
    {
        if (_topScrollbarDragAxis == null || !Capture) return false;
        int viewportW = Math.Max(1, GetViewportSize().Width);
        int viewportH = Math.Max(1, GetViewportSize().Height);
        var content = GetDocumentContentSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        if (_topScrollbarDragAxis == TopScrollbarAxis.Vertical)
        {
            float maxScroll = Math.Max(0f, content.Height - viewportH / zoom);
            var track = GetTopVerticalScrollbarRect(ClientSize.Width, ClientSize.Height, viewportW, viewportH);
            var thumb = GetTopVerticalThumb(track, maxScroll, _scrollOffset.Y);
            float arrow = TopScrollbarExtent;
            float usable = Math.Max(1f, track.Height - 2f * arrow);
            float travel = Math.Max(1f, usable - thumb.Height);
            float top = Math.Clamp(py - _topScrollbarGrabOffset, track.Top + arrow, track.Bottom - arrow - thumb.Height);
            float t = (top - (track.Top + arrow)) / travel;
            _scrollOffset.Y = Math.Clamp(t * maxScroll, 0f, maxScroll);
        }
        else
        {
            float maxScroll = Math.Max(0f, content.Width - viewportW / zoom);
            var track = GetTopHorizontalScrollbarRect(ClientSize.Width, ClientSize.Height, viewportW, viewportH);
            var thumb = GetTopHorizontalThumb(track, maxScroll, _scrollOffset.X);
            float arrow = TopScrollbarExtent;
            float usable = Math.Max(1f, track.Width - 2f * arrow);
            float travel = Math.Max(1f, usable - thumb.Width);
            float left = Math.Clamp(px - _topScrollbarGrabOffset, track.Left + arrow, track.Right - arrow - thumb.Width);
            float t = (left - (track.Left + arrow)) / travel;
            _scrollOffset.X = Math.Clamp(t * maxScroll, 0f, maxScroll);
        }
        Invalidate();
        return true;
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
        if (_rootBox == null) return;
        CloseMenusOnScroll();
        var vp = GetViewportSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        var content = GetDocumentContentSize();
        float maxX = Math.Max(0f, content.Width - vp.Width / zoom);
        float maxY = Math.Max(0f, content.Height - vp.Height / zoom);
        _scrollOffset.X = Math.Max(0, Math.Min(x, maxX));
        _scrollOffset.Y = Math.Max(0, Math.Min(y, maxY));
        Invalidate();
    }

    public void ScrollBy(float dx, float dy)
    {
        float x = float.IsFinite(_scrollOffset.X) ? _scrollOffset.X + dx : dx;
        float y = float.IsFinite(_scrollOffset.Y) ? _scrollOffset.Y + dy : dy;
        var viewport = GetViewportSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        var content = GetDocumentContentSize();
        float maxX = Math.Max(0f, content.Width - viewport.Width / zoom);
        float maxY = Math.Max(0f, content.Height - viewport.Height / zoom);
        _scrollOffset.X = Math.Clamp(x, 0f, maxX);
        _scrollOffset.Y = Math.Clamp(y, 0f, maxY);
        CloseMenusOnScroll();
        Invalidate();
    }

    private bool HandleHorizontalWheel(int delta, System.Drawing.Point clientPoint)
    {
        float x = clientPoint.X / EffectiveZoom + PaintScrollX;
        float y = clientPoint.Y / EffectiveZoom + PaintScrollY;

        if (TryHitFrame(x, y, out var frameHit) && frameHit.View.ScrollingEnabled)
        {
            var metrics = GetFrameScrollMetrics(frameHit.Box, frameHit.View);
            if (metrics.MaxScrollX > 0)
            {
                CloseMenusOnScroll();
                _focusedFrame = frameHit.Box;
                float amount = (delta / 120f) * 40f;
                if (Math.Abs(amount) < 0.5f) amount = Math.Sign(delta) * 4f;
                frameHit.View.Scroll.X = Math.Clamp(frameHit.View.Scroll.X - amount, 0f, metrics.MaxScrollX);
                Invalidate();
                return true;
            }
            return false;
        }

        if (_rootBox == null) return false;

        // A horizontal wheel/touchpad message can arrive before the deferred
        // relayout triggered by a DOM/style change. Do one synchronous stable
        // pass in that case so the bottom scrollbar and its viewport geometry
        // agree before we calculate the scroll range.
        if (_scrollbarVisibilityDirty && !IsGestureZoomActive)
            ReflowDocumentToStableViewport();

        var viewport = GetViewportSize();
        var content = GetDocumentContentSize();
        float maxX = Math.Max(0f, content.Width - viewport.Width / Math.Max(0.25f, EffectiveZoom));
        if (maxX <= 0f) return false;

        CloseMenusOnScroll();
        float pageAmount = (delta / 120f) * 40f;
        if (Math.Abs(pageAmount) < 0.5f)
            pageAmount = Math.Sign(delta) * 4f;
        ScrollBy(-pageAmount, 0f);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (m.Msg == WM_MOUSEHWHEEL)
        {
            long lp = m.LParam.ToInt64();
            int screenX = (short)(lp & 0xFFFF);
            int screenY = (short)((lp >> 16) & 0xFFFF);
            var clientPoint = PointToClient(new System.Drawing.Point(screenX, screenY));
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            if (delta != 0)
            {
                float fx = clientPoint.X / EffectiveZoom + PaintScrollX;
                float fy = clientPoint.Y / EffectiveZoom + PaintScrollY;
                if (ScrollFocusedFieldHorizontally(delta / 120f * 40f, fx, fy))
                    return;
            }
            if (delta != 0 && HandleHorizontalWheel(delta, clientPoint))
                return;
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Ctrl+wheel is a temporary, pointer-anchored gesture. It is deliberately
        // independent from the toolbar/menu zoom ladder: it never changes
        // _layoutZoom and it is never promoted into the control zoom state by a
        // normal mouse-wheel event.
        //
        // Gesture semantics are intentionally bounded by the committed layout
        // zoom. A gesture may zoom IN above that baseline and may zoom back DOWN
        // only far enough to undo its own zoom. Ctrl+wheel-down at the baseline is
        // therefore a no-op; it cannot freely zoom the page below the control zoom.
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            float baseline = Math.Max(0.25f, _layoutZoom);
            float current = Math.Max(0.25f, EffectiveZoom);
            if (e.Delta > 0)
            {
                float target = Math.Min(4f, NextZoomStep(current, 1));
                if (target > current + 0.0005f)
                    ZoomAtPoint(target, e.Location);
            }
            else if (e.Delta < 0 && current > baseline + 0.0005f)
            {
                // Undo gesture zoom one discrete step at a time, never below
                // the committed/control zoom baseline.
                float stepped = NextZoomStep(current, -1);
                float target = Math.Max(baseline, stepped);
                ZoomAtPoint(target, e.Location);
            }
            return;
        }

        // Ordinary wheel scrolling is completely independent of the temporary
        // gesture. In particular, do NOT commit/promote gesture scale here: doing
        // so makes a later toolbar click appear to inherit the gesture zoom.
        base.OnMouseWheel(e);
        float px = e.X / EffectiveZoom + PaintScrollX;
        float py = e.Y / EffectiveZoom + PaintScrollY;
        if (_focusedJavaAppletElement != null && TryGetJavaAppletBox(_focusedJavaAppletElement, out var jab) && jab.HitTest(px, py))
        {
            SendJavaAppletInput(_focusedJavaAppletElement, jab, new Retro96.Engine.Java.JavaInput(
                Retro96.Engine.Java.JavaInputKind.MouseWheel, (int)Math.Round(px - jab.X), (int)Math.Round(py - jab.Y),
                0, e.Delta, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
            return;
        }
        if (_focusedEmbeddedElement != null && TryGetEmbeddedBox(_focusedEmbeddedElement, out var feb) &&
            feb.HitTest(px, py))
        {
            SendEmbeddedInput(_focusedEmbeddedElement, MakeEmbeddedMouseEvent(EmbeddedInputEventKind.MouseWheel, e, feb, px, py) with { WheelDelta = e.Delta });
            return;
        }

        float x = e.X / EffectiveZoom + PaintScrollX;
        float y = e.Y / EffectiveZoom + PaintScrollY;

        if (TryGetSelectAtPoint(x, y, out var wheelSelect, out _, out _, out _, out _))
        {
            int visibleRows = GetSelectVisibleRows(wheelSelect);
            int optionCount = wheelSelect.Descendants().OfType<DomElement>()
                .Count(o => o.TagName == "option");
            int maxScroll = Math.Max(0, optionCount - visibleRows);
            if (maxScroll > 0)
            {
                int delta = e.Delta > 0 ? -3 : 3;
                _selectScrollOffsets[wheelSelect] = Math.Clamp(
                    GetSelectScrollOffset(wheelSelect) + delta, 0, maxScroll);
                RerenderNow();
                return;
            }
        }

        if (TryGetEditableFieldAtPoint(x, y, out var wheelField, out var wheelFieldBox, out var wheelFieldView, out _) &&
            ReferenceEquals(wheelField, _focusedInput) && wheelFieldBox != null)
        {
            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                if (ScrollFocusedFieldHorizontally(e.Delta / 120f * 40f, x, y))
                    return;
            }
            if (wheelField!.TagName == "textarea")
            {
                var geo = GetTextareaGeometry(wheelField, wheelFieldBox, wheelFieldView);
                if (geo != null)
                {
                    float lineHeight = geo.Font.GetHeight(MeasureGraphics);
                    int visibleLines = geo.VisibleLines;
                    int maxLine = Math.Max(0, geo.Lines.Count - visibleLines);
                    int delta = e.Delta > 0 ? -3 : 3;
                    _textareaScrollLine = Math.Clamp(_textareaScrollLine + delta, 0, maxLine);
                    PersistFocusedTextareaScrollState();
                    Invalidate();
                    return;
                }
            }
        }

        if (TryHitFrame(x, y, out var frameHit) && frameHit.View.ScrollingEnabled)
        {
            var metrics = GetFrameScrollMetrics(frameHit.Box, frameHit.View);
            if (metrics.MaxScrollY > 0 ||
                ((ModifierKeys & Keys.Shift) == Keys.Shift && metrics.MaxScrollX > 0))
            {
                CloseMenusOnScroll();
                _focusedFrame = frameHit.Box;
                float amount = (e.Delta / 120f) * 40f;
                if ((ModifierKeys & Keys.Shift) == Keys.Shift && metrics.MaxScrollX > 0)
                    frameHit.View.Scroll.X = Math.Clamp(frameHit.View.Scroll.X - amount, 0f, metrics.MaxScrollX);
                else
                    frameHit.View.Scroll.Y = Math.Clamp(frameHit.View.Scroll.Y - amount, 0f, metrics.MaxScrollY);
                Invalidate();
            }
            return;
        }

        if (_rootBox == null) return;

        if ((ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            // Shift+wheel: horizontal. Keep the committed scrollbar state
            // synchronized before the first horizontal move after a reflow.
            if (_scrollbarVisibilityDirty && !IsGestureZoomActive)
                ReflowDocumentToStableViewport();
            CloseMenusOnScroll();
            ScrollBy(-(e.Delta / 120f) * 60f, 0f);
            return;
        }

        // Scrolling is transform-only: reuse the cached GPU display list and
        // submit a new frame with the updated translation. Precision wheel/touchpad
        // deltas stay fractional for smooth motion instead of being truncated.
        CloseMenusOnScroll();
        var content = GetDocumentContentSize();
        float zoomForScroll = Math.Max(0.25f, EffectiveZoom);
        float max = Math.Max(0f, content.Height - GetViewportSize().Height / zoomForScroll);
        float deltaY = (e.Delta / 120f) * 80f;
        if (Math.Abs(deltaY) < 0.01f) deltaY = Math.Sign(e.Delta) * 2f;
        _scrollOffset.Y = Math.Clamp(_scrollOffset.Y - deltaY, 0f, max);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _scrollbarVisibilityDirty = true;
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

        ReflowDocumentToStableViewport();
    }

    private void RecenterScrollAxesIfContentFits()
    {
        var viewport = GetViewportSize();
        float zoom = Math.Max(0.25f, EffectiveZoom);
        var content = GetDocumentContentSize();
        float logicalViewportW = Math.Max(1f, viewport.Width / zoom);
        float logicalViewportH = Math.Max(1f, viewport.Height / zoom);
        const float epsilon = 0.75f;
        if (content.Width <= logicalViewportW + epsilon)
            _scrollOffset.X = 0f;
        if (content.Height <= logicalViewportH + epsilon)
            _scrollOffset.Y = 0f;
    }

    /// <summary>
    /// Reflows the top-level document against all four scrollbar states and
    /// commits the least-reserved exact fixed point. A scrollbar changes the
    /// available width/height, which can change wrapping and therefore whether
    /// the other scrollbar is needed; probing the four states avoids the old
    /// one-frame V/H oscillation during repeated zoom operations.
    /// </summary>
    private bool ReflowDocumentToStableViewport()
    {
        if (_document == null || _fontCache == null || _imageCache == null || _resourceLoader == null)
            return false;

        float zoom = Math.Max(0.25f, _layoutZoom);
        var states = new (bool Vertical, bool Horizontal)[]
        {
            (_showVerticalScrollbar, _showHorizontalScrollbar),
            (false, false), (true, false), (false, true), (true, true)
        };

        LayoutBox? stableRoot = null;
        bool stableV = false, stableH = false;
        int stableWidth = 0, stableHeight = 0;
        int stableScore = int.MaxValue;

        foreach (var state in states.Distinct())
        {
            var vp = GetLayoutViewportSizeForScrollbarState(state.Vertical, state.Horizontal, zoom);
            if (vp.Width <= 0 || vp.Height <= 0) continue;

            try { Engine.Css.StyleResolver.Resolve(_document, vp.Width); }
            catch { /* keep the document's current computed styles */ }

            LayoutBox candidate;
            try { candidate = LayoutEngineApi.BuildLayoutTree(_document, vp.Width, vp.Height); }
            catch { continue; }

            var extent = GetDocumentContentExtent(candidate, vp.Width, vp.Height);
            var expected = SolveScrollbarVisibility(
                extent.Width * zoom, extent.Height * zoom,
                ClientSize.Width, ClientSize.Height);

            int score = (state.Vertical ? 1 : 0) + (state.Horizontal ? 1 : 0);
            if (expected.Vertical != state.Vertical || expected.Horizontal != state.Horizontal)
                continue;

            if (score < stableScore)
            {
                stableRoot = candidate;
                stableV = state.Vertical;
                stableH = state.Horizontal;
                stableWidth = vp.Width;
                stableHeight = vp.Height;
                stableScore = score;
            }
        }

        LayoutBox? finalRoot = stableRoot;
        bool finalV = stableV, finalH = stableH;
        int finalWidth = stableWidth, finalHeight = stableHeight;

        if (finalRoot == null)
        {
            // If none of the four states is an exact fixed point, use the most
            // conservative state and rebuild it once. This is deterministic and
            // prevents the old V/H alternation from leaking into the next zoom.
            finalV = true;
            finalH = true;
            var vp = GetLayoutViewportSizeForScrollbarState(finalV, finalH, zoom);
            try { Engine.Css.StyleResolver.Resolve(_document, vp.Width); } catch { }
            try { finalRoot = LayoutEngineApi.BuildLayoutTree(_document, vp.Width, vp.Height); }
            catch { return false; }
            finalWidth = vp.Width;
            finalHeight = vp.Height;
        }
        if (finalRoot == null)
            return false;

        // The candidate was built with the selected viewport width. Re-resolve
        // and rebuild once so the document's computed styles and the committed
        // layout tree are guaranteed to describe the same viewport. Without this
        // final synchronization, a multi-state probe could leave the DOM's style
        // cache at the last probe width while painting the earlier centered tree.
        try { Engine.Css.StyleResolver.Resolve(_document, finalWidth); } catch { }
        try
        {
            finalRoot = LayoutEngineApi.BuildLayoutTree(_document, finalWidth, finalHeight);
        }
        catch { return false; }

        // Preserve the current document position across interaction-only
        // reflows. Re-centering immediately after ApplyRelayout could zero the
        // Y offset when an intermediate measurement said the page fit, which is
        // what made Find + moving back onto the page appear to snap to the top.
        float keepScrollX = _scrollOffset.X;
        float keepScrollY = _scrollOffset.Y;

        _showVerticalScrollbar = finalV;
        _showHorizontalScrollbar = finalH;
        _lastLayoutWidth = finalWidth;
        _lastLayoutHeight = finalHeight;
        ApplyRelayout(_document, finalRoot);
        UpdateScrollBars(scheduleReflow: false, recomputeVisibility: false);
        if (float.IsFinite(keepScrollX)) _scrollOffset.X = Math.Max(0f, keepScrollX);
        if (float.IsFinite(keepScrollY)) _scrollOffset.Y = Math.Max(0f, keepScrollY);
        ClampScrollOffsetToDocument();
        _scrollbarVisibilityDirty = false;
        return true;
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
                // Enter is claimed by IsInputKey, so WinForms does not
                // reliably follow it with KeyPress. Insert the textarea
                // newline here instead of waiting for a character event.
                var js = _focusedInputFrame?.Interpreter ?? _jsInterpreter;
                var evt = js?.CreateKeyEvent("Enter", 13);
                var down = js?.FireEvent(_focusedInput, "onkeydown", evt);
                var press = js?.FireEvent(_focusedInput, "onkeypress", evt);
                bool cancelled =
                    (down is { Type: JsType.Boolean } && !down.ToBoolean()) ||
                    (press is { Type: JsType.Boolean } && !press.ToBoolean());
                if (!cancelled)
                    FieldInsertText("\n");
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (IsEditableField(_focusedInput))
            {
                // Let page scripts see the key even with no <form> around the input
                // (the common "text box + JS button" pattern). Returning false cancels.
                var js = _focusedInputFrame?.Interpreter ?? _jsInterpreter;
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

        // Ctrl+A and other caret/selection commands do not change layout.
        // Re-rendering the whole document for those commands was enough to
        // re-run table/form layout while a password selection overlay was
        // active, producing page-wide position/size jumps. Only value edits
        // need a new document bitmap.
        bool valueChanges = e.KeyCode is Keys.Delete or Keys.V or Keys.X;

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

        EnsureFocusedTextareaCaretVisible();
        if (valueChanges)
            RequestRerender();
        else
            Invalidate();
        // Stop WinForms from delivering a second KeyPress for the command key.
        // In particular Ctrl+A must never fall through into the character path.
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void OnEmbeddedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.F11 or Keys.F12) return;
        if (_focusedInput != null) return;
        if (_focusedJavaAppletElement != null && TryGetJavaAppletBox(_focusedJavaAppletElement, out var jb))
        {
            SendJavaAppletInput(_focusedJavaAppletElement, jb, new Retro96.Engine.Java.JavaInput(
                Retro96.Engine.Java.JavaInputKind.KeyDown, 0, 0, 0, 0, (int)e.KeyCode, '\0', (e.Modifiers & Keys.Shift) != 0, (e.Modifiers & Keys.Control) != 0, (e.Modifiers & Keys.Alt) != 0));
            return;
        }
        if (_focusedEmbeddedElement == null) return;
        SendEmbeddedInput(_focusedEmbeddedElement, new EmbeddedInputEvent(
            EmbeddedInputEventKind.KeyDown, KeyCode: (int)e.KeyCode, Shift: e.Shift,
            Control: e.Control, Alt: e.Alt, Meta: e.KeyData.HasFlag(Keys.LWin) || e.KeyData.HasFlag(Keys.RWin)));
    }

    private void OnEmbeddedKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.F11 or Keys.F12) return;
        if (_focusedInput != null) return;
        if (_focusedJavaAppletElement != null && TryGetJavaAppletBox(_focusedJavaAppletElement, out var jb))
        {
            SendJavaAppletInput(_focusedJavaAppletElement, jb, new Retro96.Engine.Java.JavaInput(
                Retro96.Engine.Java.JavaInputKind.KeyUp, 0, 0, 0, 0, (int)e.KeyCode, '\0', (e.Modifiers & Keys.Shift) != 0, (e.Modifiers & Keys.Control) != 0, (e.Modifiers & Keys.Alt) != 0));
            return;
        }
        if (_focusedEmbeddedElement == null) return;
        SendEmbeddedInput(_focusedEmbeddedElement, new EmbeddedInputEvent(
            EmbeddedInputEventKind.KeyUp, KeyCode: (int)e.KeyCode, Shift: e.Shift,
            Control: e.Control, Alt: e.Alt, Meta: e.KeyData.HasFlag(Keys.LWin) || e.KeyData.HasFlag(Keys.RWin)));
    }

    private void OnCanvasKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (_focusedJavaAppletElement != null && _focusedInput == null && TryGetJavaAppletBox(_focusedJavaAppletElement, out var keyAppletBox))
        {
            var typed = new Retro96.Engine.Java.JavaInput(
                Retro96.Engine.Java.JavaInputKind.KeyTyped, 0, 0, 0, 0, 0, e.KeyChar, (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0);
            SendJavaAppletInput(_focusedJavaAppletElement, keyAppletBox, typed);
            // Keep the existing 1.0 compatibility path: old applets receive
            // character input through keyDown(int). The bridge suppresses this
            // path when only 1.1 listeners are registered.
            SendJavaAppletInput(_focusedJavaAppletElement, keyAppletBox, typed with { Kind = Retro96.Engine.Java.JavaInputKind.KeyDown });
            return;
        }
        if (_focusedEmbeddedElement != null && _focusedInput == null)
        {
            SendEmbeddedInput(_focusedEmbeddedElement, new EmbeddedInputEvent(
                EmbeddedInputEventKind.TextInput, Text: e.KeyChar.ToString()));
            return;
        }
        var elem = _focusedInput;
        if (elem == null || !IsEditableField(elem)) return;

        // KeyDown owns Ctrl+A/C/V/X and Enter. WinForms can still emit the
        // corresponding C0 control character through KeyPress; sending that
        // into the text-edit path scheduled a whole document rerender during
        // password select-all, allowing unrelated table/layout state to jump.
        // Backspace is the one control character that is real editing input.
        if ((e.KeyChar < ' ' && e.KeyChar != '\b') || e.KeyChar == '\x7f')
        {
            e.Handled = true;
            return;
        }

        // FIX: plain typing never fired onkeydown/onkeypress on the field,
        // so key-capture scripts (and "return false" input filters) were dead.
        var js = _focusedInputFrame?.Interpreter ?? _jsInterpreter;
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

        EnsureFocusedTextareaCaretVisible();
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
        EnsureFocusedTextareaCaretVisible();
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
        EnsureFocusedTextareaCaretVisible();
    }

    private void CopyFieldSelectionToClipboard()
    {
        var el = _focusedInput;
        if (el == null || IsPasswordField(el)) return;
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

    private Graphics? _measureGfx;
    private Graphics MeasureGraphics
    {
        get
        {
            if (_measureGfx == null)
            {
                _measureGfx = Graphics.CreateMeasurementContext();
                _measureGfx.TextRenderingHint = Retro96.Drawing.TextRenderingHint.ClearTypeGridFit;
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

        return _fontCache.Resolve(
            style?.FontFamily is { Count: > 0 } fam ? fam : new List<string> { "Times New Roman", "serif" },
            style?.FontSize > 0f ? style.FontSize : 16f,
            bold, italic);
    }

    private static TextAlign GetFieldTextAlign(DomElement el)
    {
        // Keep the live editing overlay on the exact same alignment contract
        // as Renderer.PaintTextControl. The overlay used to hard-code
        // StringAlignment.Near, so a centered <input> rendered correctly until
        // focus/selection caused the live overlay to replace it with a
        // left-aligned copy.
        return el.Style?.OwnTextAlign == true ? el.Style.TextAlign : TextAlign.Left;
    }

    private static float GetSingleLineFieldTextWidth(
        Graphics g, string text, Font font, StringFormat fmt, bool password, float passwordAdvance)
    {
        return password
            ? text.Length * passwordAdvance
            : g.MeasureString(text, font, int.MaxValue, fmt).Width;
    }

    private static float GetSingleLineFieldAlignmentOffset(
        float visibleWidth, float textWidth, TextAlign align)
    {
        // Legacy single-line controls do not wrap. While the value fits, honor
        // text-align exactly; once it overflows, the field's horizontal scroll
        // position controls the origin instead of centering a clipped run.
        if (textWidth >= visibleWidth - 0.01f) return 0f;
        float remaining = Math.Max(0f, visibleWidth - textWidth);
        return align switch
        {
            TextAlign.Center => remaining * 0.5f,
            TextAlign.Right => remaining,
            _ => 0f
        };
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
        public required float TextWidth;
        public required float TextViewportWidth;
        public required bool NeedsVerticalScrollbar;
        public required int VisibleLines;
    }

    private TextareaGeometry? GetTextareaGeometry(DomElement el, LayoutBox? box = null, FrameView? frameView = null)
    {
        LayoutBox? root = frameView?.RootBox ?? _rootBox;
        if (root == null) return null;
        box ??= FindBoxForElement(root, el);
        var font = ResolveFieldFont(el);
        if (box == null || font == null) return null;
        string text = GetFieldText(el);
        bool wrapOff = el.GetAttrOrDefault("wrap", "").Trim()
            .Equals("off", StringComparison.OrdinalIgnoreCase);
        var layout = TextareaOverlay.CalculateLayout(MeasureGraphics, text, font,
            box.ContentRect.Width, box.ContentRect.Height, wrapOff);
        return new TextareaGeometry
        {
            Box = box, Font = font, Text = text, Lines = layout.Lines,
            TextWidth = layout.TextWidth, TextViewportWidth = layout.TextViewportWidth,
            NeedsVerticalScrollbar = layout.NeedsVerticalScrollbar,
            VisibleLines = layout.VisibleLines
        };
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

        var layout = TextareaOverlay.CalculateLayout(MeasureGraphics, text, font,
            box.ContentRect.Width, box.ContentRect.Height, wrapOff);
        var lines = layout.Lines;
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

    private int EnsureTextareaScrollLine(List<(int Start, int End)> lines,
                                         float lineHeight, float faceHeight,
                                         int caret)
    {
        int visibleLines = Math.Max(1, (int)Math.Floor((faceHeight - 4) / lineHeight));
        int maxLine = Math.Max(0, lines.Count - visibleLines);
        int caretLine = CaretLineIndex(lines, Math.Clamp(caret, 0,
            lines.Count == 0 ? 0 : lines[^1].End));
        if (caretLine < _textareaScrollLine)
            _textareaScrollLine = caretLine;
        else if (caretLine >= _textareaScrollLine + visibleLines)
            _textareaScrollLine = caretLine - visibleLines + 1;
        _textareaScrollLine = Math.Clamp(_textareaScrollLine, 0, maxLine);
        PersistFocusedTextareaScrollState();
        return _textareaScrollLine;
    }

    private void EnsureFocusedTextareaCaretVisible()
    {
        var el = _focusedInput;
        if (el?.TagName != "textarea") return;
        var geo = GetTextareaGeometry(el, frameView: _focusedInputFrame);
        if (geo == null) return;
        EnsureTextareaScrollLine(geo.Lines, geo.Font.GetHeight(MeasureGraphics),
            geo.Box.ContentRect.Height, _fieldCaret);
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

    private int FieldCaretFromPoint(DomElement el, LayoutBox box, float docX, float docY, FrameView? frameView = null)
    {
        string text = GetFieldText(el);
        if (text.Length == 0) return 0;

        var font = ResolveFieldFont(el);
        if (font == null) return text.Length;

        using var fmt = NewFieldFormat(noWrap: true);
        var face = box.ContentRect;

        if (el.TagName == "textarea")
        {
            var geo = GetTextareaGeometry(el, box, frameView);
            if (geo == null || geo.Lines.Count == 0) return text.Length;
            float lineH = font.GetHeight(MeasureGraphics);
            int li = Math.Clamp(_textareaScrollLine +
                (int)Math.Floor((docY - (face.Y + 2)) / lineH),
                0, geo.Lines.Count - 1);
            var (ls, le) = geo.Lines[li];
            float textareaScrollX = ReferenceEquals(el, _focusedInput)
                ? _fieldScrollX
                : (_textareaScrollXs.TryGetValue(el, out var storedTextareaX) ? storedTextareaX : 0f);
            return NearestCaretIndex(geo.Text, font, fmt, ls, le,
                docX - (face.X + 3) + textareaScrollX);
        }

        bool password = el.GetAttrOrDefault("type", "text").Trim().Equals("password", StringComparison.OrdinalIgnoreCase);
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float viewportScrollX = ReferenceEquals(el, _focusedInput)
            ? _fieldScrollX
            : (_fieldFindScrollXs.TryGetValue(el, out var storedFieldX) ? storedFieldX : 0f);
        if (password)
        {
            float advance = GetPasswordGlyphAdvance(MeasureGraphics, font, fmt);
            float passwordTextWidth = text.Length * advance;
            float passwordAlignOffset = GetSingleLineFieldAlignmentOffset(
                visibleWidth, passwordTextWidth, GetFieldTextAlign(el));
            float relX = docX - (face.X + 3 + passwordAlignOffset) + viewportScrollX;
            if (relX <= 0f) return 0;
            int index = (int)Math.Floor((relX / Math.Max(advance, 1f)) + 0.5f);
            return Math.Clamp(index, 0, text.Length);
        }
        float textWidth = MeasureGraphics.MeasureString(text, font, int.MaxValue, fmt).Width;
        if (textWidth > visibleWidth && docX >= face.Right - 6f)
            return text.Length;
        float alignOffset = GetSingleLineFieldAlignmentOffset(
            visibleWidth, textWidth, GetFieldTextAlign(el));
        return NearestCaretIndex(text, font, fmt, 0, text.Length,
            docX - (face.X + 3 + alignOffset) + viewportScrollX);
    }

    private static float GetPasswordGlyphAdvance(Graphics g, Font font, StringFormat fmt)
        => PasswordMaskLayout.GetAdvance(g, font);

    private float EnsureSingleLineCaretVisible(Graphics g, string text, Font font,
                                                RectangleF face, int caret, bool password = false)
    {
        using var fmt = NewFieldFormat(noWrap: true);
        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float advance = password ? GetPasswordGlyphAdvance(g, font, fmt) : 0f;
        float caretX = password
            ? Math.Clamp(caret, 0, text.Length) * advance
            : caret <= 0 ? 0f
            : g.MeasureString(text[..Math.Min(caret, text.Length)], font,
                              int.MaxValue, fmt).Width;
        float totalWidth = password
            ? text.Length * advance
            : g.MeasureString(text, font, int.MaxValue, fmt).Width;
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
        var geo = GetTextareaGeometry(el, frameView: _focusedInputFrame);
        if (geo == null || geo.Lines.Count == 0)
            return start ? 0 : text.Length;
        int li = CaretLineIndex(geo.Lines, Math.Clamp(_fieldCaret, 0, text.Length));
        return start ? geo.Lines[li].Start : geo.Lines[li].End;
    }

    private void MoveTextareaCaret(int lineDelta, bool extend)
    {
        var el = _focusedInput;
        if (el == null) return;
        var geo = GetTextareaGeometry(el, frameView: _focusedInputFrame);
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
            is "text" or "password";
    }

    private bool TryGetEditableFieldAtPoint(float x, float y,
                                            out DomElement? field,
                                            out LayoutBox? fieldBox,
                                            out FrameView? frameView,
                                            out FrameHit frameHit)
    {
        field = null;
        fieldBox = null;
        frameView = null;
        frameHit = default;

        if (_rootBox != null)
        {
            var deepest = HitTestDeepestBox(_rootBox, x, y);
            var rootElement = deepest?.Element;
            if (IsEditableField(rootElement))
            {
                field = rootElement;
                fieldBox = FindBoxForElement(_rootBox, rootElement!) ?? deepest;
                return fieldBox != null;
            }
        }

        if (TryHitFrame(x, y, out frameHit))
        {
            var deepest = HitTestDeepestBox(frameHit.View.RootBox, frameHit.LocalX, frameHit.LocalY);
            var frameElement = deepest?.Element;
            if (IsEditableField(frameElement))
            {
                field = frameElement;
                fieldBox = FindBoxForElement(frameHit.View.RootBox, frameElement!) ?? deepest;
                frameView = frameHit.View;
                return fieldBox != null;
            }
        }

        return false;
    }

    private bool TryGetFieldBox(DomElement field, FrameView? frameView, out LayoutBox? box)
    {
        var root = frameView?.RootBox ?? _rootBox;
        box = root == null ? null : FindBoxForElement(root, field);
        return box != null;
    }

    private float GetSingleLineFieldMaxScroll(DomElement el, LayoutBox box)
    {
        var font = ResolveFieldFont(el);
        if (font == null) return 0f;
        string text = GetFieldText(el);
        if (el.GetAttrOrDefault("type", "text").Trim().Equals("password", StringComparison.OrdinalIgnoreCase))
            text = new string('*', text.Length);
        using var fmt = NewFieldFormat(noWrap: true);
        bool password = el.GetAttrOrDefault("type", "text").Trim().Equals("password", StringComparison.OrdinalIgnoreCase);
        float visible = Math.Max(1f, box.ContentRect.Width - 6f);
        float width = password
            ? text.Length * GetPasswordGlyphAdvance(MeasureGraphics, font, fmt)
            : MeasureGraphics.MeasureString(text, font, int.MaxValue, fmt).Width;
        return Math.Max(0f, width - visible);
    }

    private float GetTextareaMaxHorizontalScroll(DomElement el, LayoutBox box, FrameView? frameView)
    {
        if (!el.GetAttrOrDefault("wrap", "").Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            return 0f;
        var font = ResolveFieldFont(el);
        if (font == null) return 0f;
        var geo = GetTextareaGeometry(el, box, frameView);
        if (geo == null) return 0f;
        // CalculateLayout.TextWidth is the measured line width, while
        // TextViewportWidth is the actual drawable width after the vertical
        // scrollbar gutter. Subtract the latter; subtracting TextWidth from
        // itself made horizontal scrolling permanently report zero.
        using var fmt = NewFieldFormat(noWrap: true);
        float maxLine = 0f;
        foreach (var (start, end) in geo.Lines)
            maxLine = Math.Max(maxLine, MeasureGraphics.MeasureString(geo.Text[start..end], font, int.MaxValue, fmt).Width);
        return Math.Max(0f, maxLine - Math.Max(1f, geo.TextViewportWidth));
    }

    private bool UpdateFieldDragSelection(DomElement field, LayoutBox box,
                                           float localX, float localY, FrameView? frameView)
    {
        if (!IsEditableField(field) || field.HasAttr("disabled")) return false;
        if (!ReferenceEquals(field, _focusedInput)) return false;

        var face = box.ContentRect;
        if (face.Width <= 0.01f || face.Height <= 0.01f) return false;

        // Keep the caret in the exact same coordinate system as the rendered
        // text.  When the drag reaches either viewport edge, advance the
        // field's internal horizontal scroll and then resolve the caret again
        // using that new viewport.  The old code only changed the caret index,
        // so dragging past the final visible glyph simply clamped at the edge.
        float maxScroll = field.TagName == "textarea"
            ? GetTextareaMaxHorizontalScroll(field, box, frameView)
            : GetSingleLineFieldMaxScroll(field, box);
        float viewportLeft = face.Left + 3f;
        float viewportRight = face.Right - 3f;
        float nextScroll = Math.Clamp(_fieldScrollX, 0f, maxScroll);

        if (maxScroll > 0f && field.TagName == "input")
        {
            const float edge = 10f;
            if (localX > viewportRight - edge)
            {
                float over = Math.Max(0f, localX - (viewportRight - edge));
                float speed = Math.Clamp(3f + over * 0.65f, 3f, 28f);
                nextScroll = Math.Min(maxScroll, nextScroll + speed);
            }
            else if (localX < viewportLeft + edge)
            {
                float over = Math.Max(0f, (viewportLeft + edge) - localX);
                float speed = Math.Clamp(3f + over * 0.65f, 3f, 28f);
                nextScroll = Math.Max(0f, nextScroll - speed);
            }
        }

        if (Math.Abs(nextScroll - _fieldScrollX) > 0.001f)
        {
            _fieldScrollX = nextScroll;
            if (field.TagName == "input")
                _fieldFindScrollXs.Remove(field);
            InvalidateDisplayLists();
        }

        _fieldCaret = FieldCaretFromPoint(field, box, localX, localY, frameView);
        Invalidate();
        return true;
    }

    private bool UpdateFieldDragSelectionFromCanvasPoint(float canvasX, float canvasY)
    {
        if (!_fieldDragging || _focusedInput == null) return false;

        var frameView = _fieldDragPointerFrame;
        var root = frameView?.RootBox ?? _rootBox;
        if (root == null) return false;
        var box = FindBoxForElement(root, _focusedInput);
        if (box == null) return false;

        float localX = canvasX;
        float localY = canvasY;
        if (frameView != null)
        {
            if (TryGetFrameScreenRect(frameView, out var frameRect))
            {
                localX = canvasX - frameRect.X + frameView.Scroll.X;
                localY = canvasY - frameRect.Y + frameView.Scroll.Y;
            }
        }

        _fieldDragPointerX = localX;
        _fieldDragPointerY = localY;
        return UpdateFieldDragSelection(_focusedInput, box, localX, localY, frameView);
    }

    private void OnFieldDragAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_fieldDragging || _focusedInput == null)
        {
            _fieldDragAutoScrollTimer.Stop();
            return;
        }

        // Re-apply the edge rule using the last pointer location. This gives
        // native-like autoscroll even when the pointer is parked just beyond
        // the right/left edge and Windows sends no additional MouseMove events.
        var field = _focusedInput;
        if (field.TagName != "input") return;

        var root = _fieldDragPointerFrame?.RootBox ?? _rootBox;
        if (root == null) return;
        var box = FindBoxForElement(root, field);
        if (box == null) return;

        var face = box.ContentRect;
        const float edge = 10f;
        bool nearLeft = _fieldDragPointerX < face.Left + 3f + edge;
        bool nearRight = _fieldDragPointerX > face.Right - 3f - edge;
        if (!nearLeft && !nearRight)
        {
            _fieldDragAutoScrollTimer.Stop();
            return;
        }

        float maxScroll = GetSingleLineFieldMaxScroll(field, box);
        if (maxScroll <= 0.01f ||
            (nearLeft && _fieldScrollX <= 0.01f) ||
            (nearRight && _fieldScrollX >= maxScroll - 0.01f))
        {
            _fieldCaret = FieldCaretFromPoint(field, box, _fieldDragPointerX,
                _fieldDragPointerY, _fieldDragPointerFrame);
            Invalidate();
            _fieldDragAutoScrollTimer.Stop();
            return;
        }

        UpdateFieldDragSelection(field, box, _fieldDragPointerX,
            _fieldDragPointerY, _fieldDragPointerFrame);
    }

    private bool ScrollFocusedFieldHorizontally(float delta, float x, float y)
    {
        if (!TryGetEditableFieldAtPoint(x, y, out var field, out var box, out var frameView, out _))
            return false;
        if (!ReferenceEquals(field, _focusedInput) || box == null) return false;

        float maxScroll = field!.TagName == "textarea"
            ? GetTextareaMaxHorizontalScroll(field, box, frameView)
            : GetSingleLineFieldMaxScroll(field, box);
        if (maxScroll <= 0f) return true;

        _fieldScrollX = Math.Clamp(_fieldScrollX - delta, 0f, maxScroll);
        // The live focused viewport is the single source of truth while the
        // field is focused. Do not mirror manual scrolling into the unfocused
        // Find-scroll cache: blur must return an input to x=0.
        if (field!.TagName == "input")
            _fieldFindScrollXs.Remove(field);
        else
            PersistFocusedTextareaScrollState();
        InvalidateDisplayLists();
        Invalidate();
        return true;
    }

    private bool HandleEditableFieldMouseDown(DomElement element, LayoutBox box,
                                               float localX, float localY,
                                               FrameView? frameView,
                                               JsInterpreter? js, MouseEventArgs e)
    {
        if (!IsEditableField(element) || element.HasAttr("disabled")) return false;

        long now = Environment.TickCount64;
        bool continuing = ReferenceEquals(element, _lastFieldClickElement) &&
                          ReferenceEquals(frameView, _lastFieldClickFrame) &&
                          now - _lastFieldClickTicks <= SystemInformation.DoubleClickTime &&
                          Math.Abs(e.X - _lastFieldClickPoint.X) <= SystemInformation.DoubleClickSize.Width &&
                          Math.Abs(e.Y - _lastFieldClickPoint.Y) <= SystemInformation.DoubleClickSize.Height;
        _fieldClickCount = continuing ? _fieldClickCount + 1 : 1;
        _lastFieldClickElement = element;
        _lastFieldClickFrame = frameView;
        _lastFieldClickPoint = e.Location;
        _lastFieldClickTicks = now;

        // Resolve the caret against the viewport that is actually visible now.
        // For an unfocused input that may be Find's temporary right-shifted
        // viewport, using only _fieldScrollX would map the click to the wrong
        // characters and a subsequent caret correction would force the viewport
        // right again.
        int caret = FieldCaretFromPoint(element, box, localX, localY, frameView);
        bool hadFindViewport = element.TagName == "input" &&
            !IsPasswordField(element) && _fieldFindScrollXs.ContainsKey(element);
        if (hadFindViewport)
        {
            // The click is an explicit user action. Let FocusControl perform its
            // normal single-line reset to x=0, then never let background Find
            // refreshes reclaim the old viewport until a new Find navigation.
            _findFieldViewportUserOverrides.Add(element);
            _fieldFindScrollXs.Remove(element);
            _fieldScrollX = 0f;
            InvalidateDisplayLists();
        }

        ClearPageSelection();
        FocusControl(element, caret, js, frameView);
        if (frameView == null)
            _focusedFrame = null;

        if (_fieldClickCount >= 3)
        {
            SelectFieldAll(element);
            _fieldClickCount = 0;
            _fieldDragging = false;
            _suppressNextMouseUp = true;
            Capture = true;
            Invalidate();
            return true;
        }
        if (_fieldClickCount == 2)
        {
            SelectFieldWord(element, ref _fieldCaret, ref _fieldSelAnchor);
            _fieldDragging = false;
            _suppressNextMouseUp = true;
            Capture = true;
            Invalidate();
            return true;
        }

        _fieldDragging = true;
        _fieldDragPointerFrame = frameView;
        _fieldDragPointerX = localX;
        _fieldDragPointerY = localY;
        // Do not start the autoscroll timer for an ordinary click. It starts
        // from MouseMove once an actual drag is underway and the pointer is
        // near a horizontal viewport edge.
        _fieldDragAutoScrollTimer.Stop();
        Capture = true;
        return true;
    }

    private static void SelectFieldWord(DomElement el, ref int caret, ref int anchor)
    {
        string text = GetFieldText(el);
        caret = Math.Clamp(caret, 0, text.Length);
        int start = caret;
        int end = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        anchor = start;
        caret = end;
    }

    private void SelectFieldAll(DomElement el)
    {
        int length = GetFieldText(el).Length;
        _fieldSelAnchor = 0;
        _fieldCaret = length;
        _fieldClickCount = 0;
        Invalidate();
    }

    private void PersistFocusedTextareaScrollState()
    {
        if (_focusedInput?.TagName != "textarea") return;
        _textareaScrollLines[_focusedInput] = Math.Max(0, _textareaScrollLine);
        _textareaScrollXs[_focusedInput] = Math.Max(0f, _fieldScrollX);
    }

    private void RestoreTextareaScrollState(DomElement el)
    {
        _textareaScrollLine = _textareaScrollLines.TryGetValue(el, out int line) ? Math.Max(0, line) : 0;
        _fieldScrollX = _textareaScrollXs.TryGetValue(el, out float scrollX) ? Math.Max(0f, scrollX) : 0f;
    }

    private (int ScrollLine, float ScrollX, bool ShowScrollbar) GetTextareaRenderState(DomElement el)
    {
        if (ReferenceEquals(el, _focusedInput) && el.TagName == "textarea")
            return (Math.Max(0, _textareaScrollLine), Math.Max(0f, _fieldScrollX), true);

        int line = _textareaScrollLines.TryGetValue(el, out var storedLine) ? Math.Max(0, storedLine) : 0;
        float scrollX = _textareaScrollXs.TryGetValue(el, out var storedX) ? Math.Max(0f, storedX) : 0f;
        return (line, scrollX, false);
    }

    private static int GetSelectVisibleRows(DomElement select)
    {
        int size = Math.Max(1, select.GetAttrInt("size", 1));
        return select.HasAttr("multiple") && size == 1 ? 4 : size;
    }

    private int GetSelectScrollOffset(DomElement el)
    {
        if (el.TagName != "select") return 0;
        int optionCount = el.Descendants().OfType<DomElement>()
            .Count(o => o.TagName == "option");
        int maxScroll = Math.Max(0, optionCount - GetSelectVisibleRows(el));
        int current = _selectScrollOffsets.TryGetValue(el, out var value) ? value : 0;
        int clamped = Math.Clamp(current, 0, maxScroll);
        if (clamped != current) _selectScrollOffsets[el] = clamped;
        return clamped;
    }

    private bool TryGetSelectAtPoint(float x, float y,
                                     out DomElement select, out LayoutBox box,
                                     out FrameView? frameView, out float localX, out float localY)
    {
        select = null!;
        box = null!;
        frameView = null;
        localX = x;
        localY = y;

        LayoutBox? root;
        if (TryHitFrame(x, y, out var frameHit))
        {
            frameView = frameHit.View;
            root = frameView.RootBox;
            localX = frameHit.LocalX;
            localY = frameHit.LocalY;
        }
        else
        {
            root = _rootBox;
        }

        if (root == null) return false;

        var hitElement = HitTestDeepestBox(root, localX, localY)?.Element;
        for (var candidate = hitElement; candidate != null; candidate = candidate.Parent as DomElement)
        {
            if (candidate.TagName != "select") continue;
            if (candidate.HasAttr("disabled") ||
                (!candidate.HasAttr("multiple") && candidate.GetAttrInt("size", 1) <= 1))
                return false;

            var options = candidate.Descendants().OfType<DomElement>()
                .Where(o => o.TagName == "option").ToList();
            int visibleRows = GetSelectVisibleRows(candidate);
            if (options.Count <= visibleRows) return false;

            var candidateBox = FindBoxForElement(root, candidate);
            if (candidateBox == null || !candidateBox.ContentRect.Contains(localX, localY)) return false;

            select = candidate;
            box = candidateBox;
            return true;
        }

        return false;
    }

    private bool TryGetSelectScrollbarPoint(float x, float y,
                                             out DomElement select, out LayoutBox box,
                                             out FrameView? frameView, out float localX, out float localY)
    {
        if (!TryGetSelectAtPoint(x, y, out select, out box, out frameView, out localX, out localY))
            return false;

        const float barWidth = 14f;
        var face = box.ContentRect;
        return new RectangleF(face.Right - barWidth, face.Top, barWidth, face.Height)
            .Contains(localX, localY);
    }

    private void FocusControl(DomElement el, int caretPos, JsInterpreter? js = null, FrameView? frameView = null)
    {
        ArgumentNullException.ThrowIfNull(el);

        // This state owns caret/editing behavior. Only editable input fields
        // and textareas are allowed to enter it.
        if (!IsEditableField(el))
            return;

        js ??= _jsInterpreter;
        bool alreadyFocused = ReferenceEquals(_focusedInput, el);

        if (!alreadyFocused)
        {
            BlurField();
            _focusedInput = el;
            _focusedInputFrame = frameView;
            _fieldValueAtFocus = GetFieldText(el);
            var focusDoc = frameView?.Document ?? _document;
            if (focusDoc != null)
                UpdateCssInteractionState(focusDoc, focusDoc.HoveredElement, focusDoc.ActiveElement, el, relayout: false);
            js?.FireEvent(el, "onfocus");   // FIX: clicking a field never fired onfocus

            // A textarea's scroll position is control-local state. Re-focusing
            // it must resume where the user last scrolled instead of jumping to
            // line zero after every blur/refocus cycle.
            if (el.TagName == "textarea")
                RestoreTextareaScrollState(el);
            else
            {
                _textareaScrollLine = 0;
                // Single-line inputs are intentionally ephemeral viewports.
                // Any horizontal position is reset when focus changes so an
                // input always re-enters from its normal left edge.
                _fieldScrollX = 0f;
                _fieldFindScrollXs.Remove(el);
            }
        }

        _focusedInputFrame = frameView;
        RememberDefault(el);
        _fieldCaret = _fieldSelAnchor = Math.Clamp(caretPos, 0, GetFieldText(el).Length);
        RequestRerender();
    }

    private void BlurField()
    {
        var el = _focusedInput;
        if (el == null) return;
        var ownerFrame = _focusedInputFrame;
        PersistFocusedTextareaScrollState();
        if (el.TagName == "input")
        {
            // A single-line input must snap back to its native left edge as soon
            // as it loses focus. Never carry the live editor viewport into the
            // unfocused renderer; Find may create its own temporary viewport,
            // but ordinary focus/blur must not preserve manual scrolling.
            _fieldScrollX = 0f;
            _fieldFindScrollXs.Remove(el);
            InvalidateDisplayLists();
        }
        _focusedInput = null;
        _focusedInputFrame = null;
        _fieldDragging = false;
        _fieldDragPointerFrame = null;
        _fieldDragAutoScrollTimer.Stop();

        var js = ownerFrame?.Interpreter ?? _jsInterpreter;
        // FIX: onchange NEVER fired for text inputs/textareas.  DOM order:
        // change fires before blur.
        if (IsEditableField(el) && GetFieldText(el) != (_fieldValueAtFocus ?? GetFieldText(el)))
            js?.FireEvent(el, "onchange");
        js?.FireEvent(el, "onblur");
        _fieldValueAtFocus = null;
        _lastFieldClickElement = null;
        _lastFieldClickFrame = null;
        _fieldClickCount = 0;

        RequestRerender();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Mouse
    // ─────────────────────────────────────────────────────────────────────

    private static bool IsSameOrAncestor(DomElement candidate, DomElement? stateElement)
    {
        for (DomNode? node = stateElement; node != null; node = node.Parent)
            if (ReferenceEquals(node, candidate)) return true;
        return false;
    }

    private void UpdateCssInteractionState(DomDocument doc, DomElement? hovered = null,
                                           DomElement? active = null,
                                           DomElement? focused = null,
                                           bool relayout = true)
    {
        bool hoveredChanged = !ReferenceEquals(doc.HoveredElement, hovered);
        bool activeChanged = !ReferenceEquals(doc.ActiveElement, active);
        bool focusedChanged = !ReferenceEquals(doc.FocusedElement, focused);
        bool changed = hoveredChanged || activeChanged || focusedChanged;
        if (!changed) return;

        doc.HoveredElement = hovered;
        doc.ActiveElement = active;
        doc.FocusedElement = focused;

        // Editable-field focus/hover is painted as a live overlay. Rebuilding
        // the whole document for every mouse move/down/up changes the textarea
        // content-box rounding after the page has been scrolled, so the live
        // text layer can move by a pixel even though the page bitmap did not.
        // Keep those state transitions paint-only; ordinary links/buttons
        // still take the full dynamic-CSS relayout path.
        if (!relayout)
        {
            // Interaction state changes must still rebuild the cached display
            // list so legacy BODY ALINK and :hover/:active colors become visible,
            // but must never rebuild layout.  The old pressed-link overlay drew a
            // second copy of the glyphs on top of the normal green link, which
            // caused the red/green ghost and the tiny text displacement.
            if (hoveredChanged || activeChanged || focusedChanged)
                InvalidateDisplayLists();
            Invalidate();
            return;
        }

        // Dynamic selectors participate in the cascade. Re-resolve and
        // rebuild this document so :hover/:active/:focus visibly alter
        // colors, borders and display exactly like normal CSS rules.
        try
        {
            LayoutBox? frameKey = null;
            FrameView? frameView = null;
            TryFindFrameViewByDocument(doc, out frameView, out _, out frameKey);

            if (ReferenceEquals(doc, _document) && _rootBox != null)
            {
                // ROOT CAUSE FIX:
                // The old path rebuilt the top-level page against the physical
                // client viewport. At non-100% zoom that is the wrong CSS
                // viewport, and it bypassed the coupled V/H scrollbar solver.
                // Merely moving the pointer onto any element therefore changed
                // wrapping/centering and left scrollbar visibility out of sync
                // with the newly-built root. The visible symptom was a random
                // horizontal bar plus the page being pulled toward the left.
                //
                // Use the same stable reflow used by resize/zoom so hover/active
                // CSS can still change geometry without corrupting scroll state.
                if (!ReflowDocumentToStableViewport())
                    throw new InvalidOperationException("Unable to rebuild the page for interaction state.");
            }
            else if (frameView != null && frameKey != null)
            {
                int frameWidth = Math.Max(1, (int)frameKey.Width);
                int frameHeight = Math.Max(1, (int)frameKey.Height);
                StyleResolver.Resolve(doc, frameWidth);
                var rebuiltFrameRoot = LayoutEngineApi.BuildLayoutTree(doc, frameWidth, frameHeight);
                RemapFrameSelection(frameView, rebuiltFrameRoot);
                frameView.RootBox = rebuiltFrameRoot;
                RecomposeFrameTree(frameView);
            }
        }
        catch { RequestRerender(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        _dragMoved = false;

        if (CanFocus && !Focused)
            Focus();

        if (_openSelectMenu != null && !_openSelectMenu.IsDisposed)
            CloseOpenSelectMenu();

        if (e.Button != MouseButtons.Left || _rootBox == null || _document == null)
            return;

        int scrollPx = e.X;
        int scrollPy = e.Y;
        if (e.Button == MouseButtons.Left && TryBeginTopLevelScrollbarInteraction(scrollPx, scrollPy))
            return;

        // A new pointer press starts a new selection context.  Clearing here
        // prevents a previous Ctrl+A/drag/double-click highlight from
        // surviving when the user clicks a button, link, blank area, frame,
        // scrollbar, or form control elsewhere.
        ClearPageSelection();

        float x = e.X / EffectiveZoom + PaintScrollX;
        float y = e.Y / EffectiveZoom + PaintScrollY;

        if (e.Button == MouseButtons.Left && TryBeginJavaAppletInput(x, y, e))
        {
            Capture = true;
            return;
        }

        if (e.Button == MouseButtons.Left && TryBeginEmbeddedInput(x, y, e))
        {
            Capture = true;
            return;
        }

        if (e.Button == MouseButtons.Left && TryBeginEmbeddedMidiControl(x, y))
        {
            Capture = true;
            return;
        }

        if (e.Button == MouseButtons.Left && TryBeginFrameScrollbarInteraction(x, y))
        {
            _frameScrollbarMouseDownHandled = true;
            Capture = true;
            return;
        }

        if (e.Button == MouseButtons.Left &&
            _focusedInput?.TagName == "textarea" &&
            TryGetFocusedTextareaScrollbarPoint(x, y, out var scrollbarBox,
                out float scrollbarX, out float scrollbarY))
        {
            var geo = GetTextareaGeometry(_focusedInput, scrollbarBox, _focusedInputFrame);
            if (geo != null && geo.NeedsVerticalScrollbar)
            {
                float trackY = scrollbarBox.ContentRect.Top + 1f;
                float trackHeight = Math.Max(1f, scrollbarBox.ContentRect.Height - 2f);
                float thumbHeight = Math.Max(10f, trackHeight * geo.VisibleLines /
                    Math.Max(1, geo.Lines.Count));
                float travel = Math.Max(1f, trackHeight - thumbHeight);
                int maxLine = Math.Max(0, geo.Lines.Count - geo.VisibleLines);
                float thumbTop = trackY + travel * _textareaScrollLine /
                    Math.Max(1, maxLine);

                if (scrollbarY < thumbTop || scrollbarY > thumbTop + thumbHeight)
                {
                    float desiredTop = Math.Clamp(scrollbarY - thumbHeight / 2f,
                        trackY, trackY + travel);
                    _textareaScrollLine = Math.Clamp(
                        (int)Math.Round((desiredTop - trackY) / travel * maxLine),
                        0, maxLine);
                    thumbTop = trackY + travel * _textareaScrollLine /
                        Math.Max(1, maxLine);
                    _textareaScrollbarGrabOffset = thumbHeight / 2f;
                }
                else
                {
                    _textareaScrollbarGrabOffset = Math.Clamp(
                        scrollbarY - thumbTop, 0f, thumbHeight);
                }

                PersistFocusedTextareaScrollState();
                _textareaScrollbarDragging = true;
                _textareaScrollbarDragFrame = _focusedInputFrame;
                Capture = true;
                Invalidate();
                return;
            }
        }

        if (e.Button == MouseButtons.Left &&
            TryGetSelectScrollbarPoint(x, y, out var selectScrollbar,
                out var selectScrollbarBox, out var selectScrollbarFrame,
                out _, out float selectScrollbarY))
        {
            int visibleRows = GetSelectVisibleRows(selectScrollbar);
            int optionCount = selectScrollbar.Descendants().OfType<DomElement>()
                .Count(o => o.TagName == "option");
            int maxScroll = Math.Max(0, optionCount - visibleRows);
            float trackY = selectScrollbarBox.ContentRect.Top + 1f;
            float trackHeight = Math.Max(1f, selectScrollbarBox.ContentRect.Height - 2f);
            float thumbHeight = Math.Max(10f, trackHeight * visibleRows /
                Math.Max(1, optionCount));
            float travel = Math.Max(1f, trackHeight - thumbHeight);
            int currentScroll = GetSelectScrollOffset(selectScrollbar);
            float thumbTop = trackY + travel * currentScroll / Math.Max(1, maxScroll);

            if (selectScrollbarY < thumbTop || selectScrollbarY > thumbTop + thumbHeight)
            {
                float desiredTop = Math.Clamp(selectScrollbarY - thumbHeight / 2f,
                    trackY, trackY + travel);
                currentScroll = Math.Clamp(
                    (int)Math.Round((desiredTop - trackY) / travel * maxScroll),
                    0, maxScroll);
                thumbTop = trackY + travel * currentScroll / Math.Max(1, maxScroll);
                _selectScrollbarGrabOffset = thumbHeight / 2f;
                _selectScrollOffsets[selectScrollbar] = currentScroll;
                RerenderNow();
            }
            else
            {
                _selectScrollbarGrabOffset = Math.Clamp(
                    selectScrollbarY - thumbTop, 0f, thumbHeight);
            }

            _selectScrollbarDragging = true;
            _selectScrollbarDragSelect = selectScrollbar;
            _selectScrollbarDragFrame = selectScrollbarFrame;
            Capture = true;
            Invalidate();
            return;
        }

        // Frame documents have their own layout tree. Resolve controls and
        // events in the deepest frame, not against the page-level <frame> box.
        if (TryHitFrame(x, y, out var frameHit))
        {
            var frameView = frameHit.View;
            var frameElement = HitTestDeepestBox(frameView.RootBox, frameHit.LocalX, frameHit.LocalY)?.Element;

            if (_focusedInput != null && !ReferenceEquals(frameElement, _focusedInput))
                BlurField();

            // Buttons and other form controls inside a frame get the same
            // Win95 press-in state as controls on the top-level document.
            // The renderer is given the frame-local pressed element below.
            if (frameElement != null && IsControlElement(frameElement))
            {
                if (frameElement.HasAttr("disabled")) return;
                if (IsEditableField(frameElement))
                {
                    var frameFieldBox = FindBoxForElement(frameView.RootBox, frameElement);
                    if (frameFieldBox != null &&
                        HandleEditableFieldMouseDown(frameElement, frameFieldBox,
                            frameHit.LocalX, frameHit.LocalY, frameView,
                            frameView.Interpreter ?? _jsInterpreter, e))
                    {
                        _focusedFrame = frameHit.Box;
                        return;
                    }
                }

                if (!IsPageSelectableControl(frameElement))
                {
                    _pressedControl = frameElement;
                    _pressedControlFrame = frameView;
                    _focusedFrame = frameHit.Box;
                    UpdateCssInteractionState(frameView.Document, frameView.Document.HoveredElement,
                        frameElement, frameView.Document.FocusedElement, relayout: false);
                    Capture = true;
                    RerenderNow();
                    return;
                }

                // Keep the native-looking pressed state for buttons/selects,
                // but do not return: drag selection must still be able to use
                // the control as a selectable range box.
                _pressedControl = frameElement;
                _pressedControlFrame = frameView;
                _focusedFrame = frameHit.Box;
                UpdateCssInteractionState(frameView.Document, frameView.Document.HoveredElement,
                    frameElement, frameView.Document.FocusedElement, relayout: false);
            }

            // A pressed link is a paint-only interaction. Re-running layout for
            // :active changes can move inline text by a fractional pixel, so
            // keep the existing geometry frozen while the pointer is held.
            var frameLinkAnchor = frameElement != null && frameElement.TagName == "a"
                ? frameElement
                : frameElement != null ? FindAncestor(frameElement, "a") : null;
            if (frameLinkAnchor != null && frameLinkAnchor.HasAttr("href"))
            {
                // A link press is an activation gesture, not a text-selection
                // gesture. The old path continued into the selection setup
                // below, so a tiny pointer drift while holding the link created
                // a selection highlight and could make the link appear to jump.
                // Cancel any in-progress page selection, freeze the existing
                // layout geometry, and capture the pointer exclusively for the
                // pressed link until mouse-up.
                CancelPageTextSelectionForPointerInteraction();
                _pressedControl = frameLinkAnchor;
                _pressedControlFrame = frameView;
                _focusedFrame = frameHit.Box;
                UpdateCssInteractionState(frameView.Document, frameView.Document.HoveredElement,
                    frameLinkAnchor, frameView.Document.FocusedElement, relayout: false);
                Capture = true;
                Invalidate();
                return;
            }

            // Text selection inside a frame uses the frame's own document
            // and interpreter context.
            var frameTextHit = HitTestTextPosition(frameView.RootBox, frameHit.LocalX, frameHit.LocalY);
            if (frameTextHit != null)
            {
                var frameTextAnchor = frameTextHit.Value.Box;
                int frameTextOffset = frameTextHit.Value.Offset;
                _focusedFrame = frameHit.Box;
                _selStart = e.Location;
                long frameNow = Environment.TickCount64;
                var frameTextClickIdentity = GetTextClickIdentity(frameTextAnchor);
                bool continuingFrameTextClick =
                    _lastTextClickFrame == frameView &&
                    ReferenceEquals(frameTextClickIdentity, _lastTextClickElement) &&
                    frameNow - _lastTextClickTicks <= Math.Max(SystemInformation.DoubleClickTime + 150, 750) &&
                    Math.Abs(e.X - _lastTextClickPoint.X) <= Math.Max(SystemInformation.DoubleClickSize.Width, 8) &&
                    Math.Abs(e.Y - _lastTextClickPoint.Y) <= Math.Max(SystemInformation.DoubleClickSize.Height, 8);
                int frameEventClicks = Math.Clamp(e.Clicks, 1, 3);
                _textClickCount = continuingFrameTextClick
                    ? Math.Min(3, Math.Max(_textClickCount + 1, frameEventClicks))
                    : frameEventClicks;
                _lastTextClickBox = frameTextAnchor;
                _lastTextClickElement = GetTextClickIdentity(frameTextAnchor);
                _lastTextClickFrame = frameView;
                _lastTextClickPoint = e.Location;
                _lastTextClickTicks = frameNow;

                if (_textClickCount >= 3)
                {
                    _selectionFrame = frameView;
                    SelectTextBlock(frameTextAnchor);
                    _textClickCount = 0;
                    _selecting = false;
                    _suppressNextMouseUp = true;
                    _suppressNextMouseDoubleClick = true;
                    Invalidate();
                    return;
                }
                if (_textClickCount == 2)
                {
                    _selectionFrame = frameView;
                    SelectWordAt(frameTextAnchor, frameTextOffset);
                    _selecting = false;
                    // Keep the two-click sequence alive until the third MouseDown.
                    _suppressNextMouseUp = true;
                    Invalidate();
                    return;
                }

                _selectionFrame = null;
                _selAnchor = _selFocus = null;
                _selAnchorOffset = _selFocusOffset = 0;
                _pendingSelectionAnchor = frameTextAnchor;
                _pendingSelectionAnchorOffset = frameTextOffset;
                _pendingSelectionFrame = frameView;
                _selecting = true;
                Capture = true;
                Invalidate();
                return;
            }
        }

        _focusedFrame = null;
        var deepest = HitTestDeepestBox(_rootBox, x, y);
        var el = deepest?.Element;
        var linkAnchor = el != null && el.TagName == "a"
            ? el
            : el != null ? FindAncestor(el, "a") : null;
        bool linkActive = linkAnchor?.HasAttr("href") == true;
        if (linkActive)
        {
            // A link press ends any focused form-control session first. The
            // focused-field overlay is a live layer; leaving it alive while the
            // page link turns red makes the old caret/selection look like a
            // second, random highlight beside the field.
            if (_focusedInput != null)
                BlurField();

            // Keep the layout tree stable during an :active press. Renderer
            // already resolves legacy ALINK from ActiveElement, so this needs
            // no reflow just to turn the link red. More importantly, do not
            // fall through into page-text selection while the link is held.
            // That was the source of the tiny link shift + stray highlights.
            CancelPageTextSelectionForPointerInteraction();
            _pressedControl = linkAnchor;
            _pressedControlFrame = null;
            UpdateCssInteractionState(_document, _lastHoveredElement, linkAnchor, _focusedInput,
                relayout: false);
            Capture = true;
            Invalidate();
            return;
        }
        UpdateCssInteractionState(_document, _lastHoveredElement, el, _focusedInput,
            relayout: false);

        // Clicking anywhere other than the focused control itself blurs it.
        if (_focusedInput != null && !ReferenceEquals(el, _focusedInput))
            BlurField();

        if (el != null && IsControlElement(el))
        {
            if (el.HasAttr("disabled"))
                return;

            if (IsEditableField(el))
            {
                if (HandleEditableFieldMouseDown(el, deepest!, x, y, null, _jsInterpreter, e))
                    return;
                return;
            }

            // Paint the native-looking Win95 press immediately.  The chrome is
            // recorded into the cached display list, so merely invalidating the
            // control is not enough after changing PressedElement.
            _pressedControl = el;
            _pressedControlFrame = null;
            RerenderNow();

            if (!IsPageSelectableControl(el))
            {
                // Push buttons and other activation-only controls must own the
                // pointer until mouse-up so their click is delivered reliably.
                Capture = true;
                return;
            }

            // Selectable controls still fall through to the page range model so
            // dragging can select their label instead of suppressing selection.
        }

        // Page text selection drag.  FIX: a fresh press on blank space used
        // to leave the previous selection highlighted forever.
        _selStart = e.Location;
        var textHit = HitTestTextPosition(_rootBox, x, y);
        var anchor = textHit?.Box;
        int anchorOffset = textHit?.Offset ?? 0;
        long now = Environment.TickCount64;
        var textClickIdentity = GetTextClickIdentity(anchor);
        bool continuingTextClick = anchor != null &&
            _lastTextClickFrame == null &&
            ReferenceEquals(textClickIdentity, _lastTextClickElement) &&
            now - _lastTextClickTicks <= Math.Max(SystemInformation.DoubleClickTime + 150, 750) &&
            Math.Abs(e.X - _lastTextClickPoint.X) <= Math.Max(SystemInformation.DoubleClickSize.Width, 8) &&
            Math.Abs(e.Y - _lastTextClickPoint.Y) <= Math.Max(SystemInformation.DoubleClickSize.Height, 8);
        int eventClicks = Math.Clamp(e.Clicks, 1, 3);
        _textClickCount = continuingTextClick
            ? Math.Min(3, Math.Max(_textClickCount + 1, eventClicks))
            : eventClicks;
        _lastTextClickBox = anchor;
        _lastTextClickElement = textClickIdentity;
        _lastTextClickFrame = null;
        _lastTextClickPoint = e.Location;
        _lastTextClickTicks = now;

        if (anchor != null && _textClickCount >= 3)
        {
            SelectTextBlock(anchor);
            _textClickCount = 0;
            _selecting = false;
            _suppressNextMouseUp = true;
            _suppressNextMouseDoubleClick = true;
            Invalidate();
            return;
        }
        if (anchor != null && _textClickCount == 2)
        {
            SelectWordAt(anchor, anchorOffset);
            _selecting = false;
            // Preserve the two-click sequence so the third physical click can
            // promote it to triple-click even when WinForms reports Clicks=1.
            _suppressNextMouseUp = true;
            Invalidate();
            return;
        }
        _pendingSelectionAnchor = anchor;
        _pendingSelectionAnchorOffset = anchorOffset;
        _pendingSelectionFrame = null;
        _selecting = anchor != null;
        if (_selecting) Capture = true;
        Invalidate();
    }

    private bool TryGetJavaAppletBox(DomElement element, out LayoutBox box)
    {
        box = null!;
        if (_rootBox == null) return false;
        box = FindBoxForElement(_rootBox, element)!;
        return box != null;
    }

    private void SendJavaAppletInput(DomElement element, LayoutBox box, Retro96.Engine.Java.JavaInput input)
    {
        try { _javaApplets.DispatchInput(element, box, input); RequestRerender(); }
        catch (Exception ex) { Retro96.DebugLog.WriteException("JavaAppletInput", ex); }
    }

    private bool TryBeginJavaAppletInput(float x, float y, MouseEventArgs e)
    {
        if (_rootBox == null || _document == null) return false;
        var box = HitTestDeepestBox(_rootBox, x, y);
        var element = box?.Element;
        if (element?.TagName != "applet" || box == null) return false;
        if (!ReferenceEquals(_focusedJavaAppletElement, element))
        {
            _focusedJavaAppletElement = element;
            _focusedEmbeddedElement = null;
        }
        _pressedJavaAppletElement = element;
        SendJavaAppletInput(element, box, new Retro96.Engine.Java.JavaInput(
            Retro96.Engine.Java.JavaInputKind.MouseDown, (int)Math.Round(x - box.X), (int)Math.Round(y - box.Y),
            (int)e.Button, 0, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
        return true;
    }

    private bool TryBeginEmbeddedInput(float x, float y, MouseEventArgs e)
    {
        if (_rootBox == null || _document == null || EmbeddedInputDispatcher == null) return false;
        var box = HitTestDeepestBox(_rootBox, x, y);
        var element = box?.Element;
        if (element?.TagName != "embed") return false;
        var old = _focusedEmbeddedElement;
        if (!ReferenceEquals(old, element))
        {
            if (old != null) SendEmbeddedInput(old, new EmbeddedInputEvent(EmbeddedInputEventKind.FocusLost));
            _focusedEmbeddedElement = element;
            _focusedJavaAppletElement = null;
            SendEmbeddedInput(element, new EmbeddedInputEvent(EmbeddedInputEventKind.FocusGained));
        }
        _pressedEmbeddedElement = element;
        SendEmbeddedInput(element, MakeEmbeddedMouseEvent(EmbeddedInputEventKind.MouseDown, e, box!, x, y));
        return true;
    }

    private void SendEmbeddedFocus(bool gained)
    {
        if (_focusedEmbeddedElement == null) return;
        SendEmbeddedInput(_focusedEmbeddedElement, new EmbeddedInputEvent(
            gained ? EmbeddedInputEventKind.FocusGained : EmbeddedInputEventKind.FocusLost));
    }

    private void SendEmbeddedInput(DomElement element, EmbeddedInputEvent ev)
    {
        try { EmbeddedInputDispatcher?.Invoke(element, ev); } catch (Exception ex) { Retro96.DebugLog.WriteException("EmbeddedInput", ex); }
    }

    private bool TryGetEmbeddedBox(DomElement element, out LayoutBox box)
    {
        box = null!;
        if (_rootBox == null) return false;
        box = FindBoxForElement(_rootBox, element)!;
        return box != null;
    }

    private EmbeddedInputEvent MakeEmbeddedMouseEvent(EmbeddedInputEventKind kind, MouseEventArgs e, LayoutBox box, float x, float y)
    {
        return new EmbeddedInputEvent(kind,
            X: Math.Max(0, (int)Math.Round(x - box.X)),
            Y: Math.Max(0, (int)Math.Round(y - box.Y)),
            Button: (int)e.Button,
            WheelDelta: kind == EmbeddedInputEventKind.MouseWheel ? e.Delta : 0,
            Shift: (ModifierKeys & Keys.Shift) != 0,
            Control: (ModifierKeys & Keys.Control) != 0,
            Alt: (ModifierKeys & Keys.Alt) != 0);
    }

    private void UpdateCursorForHit(
        DomElement? element, FrameView? hoverFrame, FrameHit frameHit, DomDocument doc,
        bool overTextareaScrollbar, bool overSelectScrollbar, float x, float y)
    {
        var hoverAnchor = element != null && element.TagName == "a"
            ? element
            : element != null ? FindAncestor(element, "a") : null;

        if (overTextareaScrollbar || overSelectScrollbar)
        {
            Cursor = Cursors.Default;
            SetStatus("");
            return;
        }

        if (hoverAnchor != null && hoverAnchor.HasAttr("href"))
        {
            Cursor = Cursors.Hand;
            try
            {
                SetStatus(doc.BaseUrl?.Resolve(hoverAnchor.GetAttr("href")!).ToAbsolute() ?? "");
            }
            catch { SetStatus(""); }
            return;
        }

        bool overText = IsEditableField(element) ||
            (hoverFrame != null
                ? HitTestTextBox(hoverFrame.RootBox, frameHit.LocalX, frameHit.LocalY) != null
                : (_rootBox != null && HitTestTextBox(_rootBox, x, y) != null));
        Cursor = overText ? Cursors.IBeam : Cursors.Default;

        // Do not erase window.status on every mouse move. Legacy sites use
        // window.status as a marquee-like ticker and update it from a timer.
        var statusJs = hoverFrame?.Interpreter ?? _jsInterpreter;
        var statusValue = statusJs?.WindowObject?.Get("status") is { Type: JsType.String } sv
            ? sv.GetString() : "";
        SetStatus(statusValue ?? "");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // A pressed link owns the mouse gesture from down to up.  Do not run
        // hover hit-testing or text-selection logic while it is captured: a
        // tiny pointer drift must never synthesize a selection or alter active
        // link geometry.
        if (_pressedControl?.TagName == "a" && _pressedControl.HasAttr("href") && Capture)
        {
            Invalidate();
            return;
        }

        if (_topScrollbarDragAxis != null && Capture)
        {
            UpdateTopLevelScrollbarDrag(e.X, e.Y);
            return;
        }

        if (_pressedJavaAppletElement != null && Capture)
        {
            float px = e.X / EffectiveZoom + PaintScrollX;
            float py = e.Y / EffectiveZoom + PaintScrollY;
            if (TryGetJavaAppletBox(_pressedJavaAppletElement, out var jb))
                SendJavaAppletInput(_pressedJavaAppletElement, jb, new Retro96.Engine.Java.JavaInput(
                    Retro96.Engine.Java.JavaInputKind.MouseDrag, (int)Math.Round(px - jb.X), (int)Math.Round(py - jb.Y),
                    0, 0, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
            return;
        }

        // AWT 1.0/1.1 has explicit mouse enter/exit events. Track the applet
        // under the pointer even when no button is pressed; dragging remains
        // captured by the pressed applet above.
        if (_rootBox != null)
        {
            float hx = e.X / EffectiveZoom + PaintScrollX;
            float hy = e.Y / EffectiveZoom + PaintScrollY;
            var hit = HitTestDeepestBox(_rootBox, hx, hy);
            var hovered = hit?.Element?.TagName.Equals("applet", StringComparison.OrdinalIgnoreCase) == true ? hit!.Element : null;
            if (!ReferenceEquals(hovered, _hoveredJavaAppletElement))
            {
                if (_hoveredJavaAppletElement != null && TryGetJavaAppletBox(_hoveredJavaAppletElement, out var oldBox))
                    SendJavaAppletInput(_hoveredJavaAppletElement, oldBox, new Retro96.Engine.Java.JavaInput(
                        Retro96.Engine.Java.JavaInputKind.MouseExit, (int)Math.Round(hx - oldBox.X), (int)Math.Round(hy - oldBox.Y),
                        0, 0, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
                _hoveredJavaAppletElement = hovered;
                if (hovered != null && hit != null)
                    SendJavaAppletInput(hovered, hit, new Retro96.Engine.Java.JavaInput(
                        Retro96.Engine.Java.JavaInputKind.MouseEnter, (int)Math.Round(hx - hit.X), (int)Math.Round(hy - hit.Y),
                        0, 0, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
            }
            if (hovered != null && hit != null)
                SendJavaAppletInput(hovered, hit, new Retro96.Engine.Java.JavaInput(
                    Retro96.Engine.Java.JavaInputKind.MouseMove, (int)Math.Round(hx - hit.X), (int)Math.Round(hy - hit.Y),
                    0, 0, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
        }

        if (_pressedEmbeddedElement != null && Capture)
        {
            float px = e.X / EffectiveZoom + PaintScrollX;
            float py = e.Y / EffectiveZoom + PaintScrollY;
            if (TryGetEmbeddedBox(_pressedEmbeddedElement, out var eb))
                SendEmbeddedInput(_pressedEmbeddedElement, MakeEmbeddedMouseEvent(EmbeddedInputEventKind.MouseMove, e, eb, px, py));
            return;
        }

        if (_selectScrollbarDragging && _selectScrollbarDragSelect != null && Capture)
        {
            var root = _selectScrollbarDragFrame?.RootBox ?? _rootBox;
            if (root != null)
            {
                float px = e.X / EffectiveZoom + PaintScrollX;
                float py = e.Y / EffectiveZoom + PaintScrollY;
                float localY = py;
                if (_selectScrollbarDragFrame != null)
                {
                    if (!TryHitFrame(px, py, out var hit) ||
                        !ReferenceEquals(hit.View, _selectScrollbarDragFrame))
                        return;
                    localY = hit.LocalY;
                }

                var box = FindBoxForElement(root, _selectScrollbarDragSelect);
                if (box != null)
                {
                    int visibleRows = GetSelectVisibleRows(_selectScrollbarDragSelect);
                    int optionCount = _selectScrollbarDragSelect.Descendants().OfType<DomElement>()
                        .Count(o => o.TagName == "option");
                    int maxScroll = Math.Max(0, optionCount - visibleRows);
                    float trackY = box.ContentRect.Top + 1f;
                    float trackHeight = Math.Max(1f, box.ContentRect.Height - 2f);
                    float thumbHeight = Math.Max(10f, trackHeight * visibleRows /
                        Math.Max(1, optionCount));
                    float travel = Math.Max(1f, trackHeight - thumbHeight);
                    float thumbTop = Math.Clamp(localY - _selectScrollbarGrabOffset,
                        trackY, trackY + travel);
                    int scroll = Math.Clamp(
                        (int)Math.Round((thumbTop - trackY) / travel * maxScroll),
                        0, maxScroll);
                    if (_selectScrollOffsets.TryGetValue(_selectScrollbarDragSelect, out var old) && old == scroll)
                        return;
                    _selectScrollOffsets[_selectScrollbarDragSelect] = scroll;
                    RerenderNow();
                }
            }
            return;
        }

        if (_textareaScrollbarDragging && _focusedInput?.TagName == "textarea" && Capture)
        {
            var root = _textareaScrollbarDragFrame?.RootBox ?? _rootBox;
            if (root != null)
            {
                float px = e.X / EffectiveZoom + PaintScrollX;
                float py = e.Y / EffectiveZoom + PaintScrollY;
                float localY = py;
                if (_textareaScrollbarDragFrame != null)
                {
                    if (!TryHitFrame(px, py, out var hit) ||
                        !ReferenceEquals(hit.View, _textareaScrollbarDragFrame))
                        return;
                    localY = hit.LocalY;
                }

                var box = FindBoxForElement(root, _focusedInput);
                if (box != null)
                {
                    var geo = GetTextareaGeometry(_focusedInput, box, _textareaScrollbarDragFrame);
                    if (geo != null && geo.NeedsVerticalScrollbar)
                    {
                        float trackY = box.ContentRect.Top + 1f;
                        float trackHeight = Math.Max(1f, box.ContentRect.Height - 2f);
                        float thumbHeight = Math.Max(10f, trackHeight * geo.VisibleLines /
                            Math.Max(1, geo.Lines.Count));
                        float travel = Math.Max(1f, trackHeight - thumbHeight);
                        float thumbTop = Math.Clamp(localY - _textareaScrollbarGrabOffset,
                            trackY, trackY + travel);
                        int maxLine = Math.Max(0, geo.Lines.Count - geo.VisibleLines);
                        _textareaScrollLine = Math.Clamp(
                            (int)Math.Round((thumbTop - trackY) / travel * maxLine),
                            0, maxLine);
                        PersistFocusedTextareaScrollState();
                        Invalidate();
                    }
                }
            }
            return;
        }

        if (_embeddedMidiVolumeDragging && _embeddedMidiElement != null && _embeddedMidiRect != RectangleF.Empty)
        {
            float x = e.X / EffectiveZoom + PaintScrollX;
            var box = FindBoxForElement(_rootBox!, _embeddedMidiElement);
            if (box != null)
            {
                float buttonW = Math.Min(46f, Math.Max(34f, (_embeddedMidiRect.Width - 18f) * 0.28f));
                float sliderLeft = 5f + buttonW + 4f + buttonW + 7f;
                float sliderRight = _embeddedMidiRect.Width - 7f;
                if (sliderRight > sliderLeft + 20f)
                {
                    int volume = (int)Math.Round(Math.Clamp(
                        (x - (_embeddedMidiRect.X + sliderLeft)) / (sliderRight - sliderLeft), 0f, 1f) * 100f);
                    if (volume != _embeddedMidiVolume)
                    {
                        _embeddedMidiVolume = volume;
                        _embeddedMidiPressedAction = "volume:" + volume;
                        EmbeddedMidiControlRequested?.Invoke(_embeddedMidiPressedAction);
                        Invalidate();
                    }
                }
            }
            return;
        }

        if (_frameScrollbarDragView != null && _frameScrollbarDragBox != null && Capture)
        {
            float pointer = _frameScrollbarDragAxis == FrameScrollbarAxis.Vertical
                ? e.Y / EffectiveZoom + PaintScrollY
                : e.X / EffectiveZoom + PaintScrollX;
            var metrics = GetFrameScrollMetrics(_frameScrollbarDragBox, _frameScrollbarDragView);
            float trackLength = _frameScrollbarDragAxis == FrameScrollbarAxis.Vertical
                ? metrics.VerticalTrackLength
                : metrics.HorizontalTrackLength;
            float thumbLength = _frameScrollbarDragAxis == FrameScrollbarAxis.Vertical
                ? metrics.VerticalThumbLength
                : metrics.HorizontalThumbLength;
            float travel = Math.Max(1f, trackLength - thumbLength);
            float delta = pointer - _frameScrollbarDragStartPointer;
            float maxScroll = _frameScrollbarDragAxis == FrameScrollbarAxis.Vertical
                ? metrics.MaxScrollY : metrics.MaxScrollX;
            float ratio = maxScroll <= 0 ? 0 : Math.Clamp(delta / travel, -1f, 1f);
            if (_frameScrollbarDragAxis == FrameScrollbarAxis.Vertical)
                _frameScrollbarDragView.Scroll.Y = Math.Clamp(
                    _frameScrollbarDragStartScroll + ratio * maxScroll, 0f, maxScroll);
            else
                _frameScrollbarDragView.Scroll.X = Math.Clamp(
                    _frameScrollbarDragStartScroll + ratio * maxScroll, 0f, maxScroll);
            RecomposeFrameTree(_frameScrollbarDragView);
            return;
        }

        if (_fieldDragging && _focusedInput != null && _rootBox != null)
        {
            float canvasX = e.X / EffectiveZoom + PaintScrollX;
            float canvasY = e.Y / EffectiveZoom + PaintScrollY;

            if (_focusedInputFrame != null)
            {
                if (TryHitFrame(canvasX, canvasY, out var fieldHit) &&
                    ReferenceEquals(fieldHit.View, _focusedInputFrame))
                {
                    var box = FindBoxForElement(fieldHit.View.RootBox, _focusedInput);
                    if (box != null)
                        UpdateFieldDragSelection(_focusedInput, box, fieldHit.LocalX, fieldHit.LocalY, fieldHit.View);
                    _fieldDragPointerFrame = fieldHit.View;
                    _fieldDragPointerX = fieldHit.LocalX;
                    _fieldDragPointerY = fieldHit.LocalY;
                    _fieldDragAutoScrollTimer.Start();
                    return;
                }

                // Pointer may leave the field itself while remaining over its
                // frame. Keep converting the pointer into frame-local space so
                // dragging past the right edge can still autoscroll the input.
                _fieldDragPointerFrame = _focusedInputFrame;
                _fieldDragAutoScrollTimer.Start();
                UpdateFieldDragSelectionFromCanvasPoint(canvasX, canvasY);
                return;
            }

            var topBox = FindBoxForElement(_rootBox, _focusedInput);
            if (topBox != null)
            {
                _fieldDragPointerFrame = null;
                UpdateFieldDragSelection(_focusedInput, topBox, canvasX, canvasY, null);
                _fieldDragPointerX = canvasX;
                _fieldDragPointerY = canvasY;
                _fieldDragAutoScrollTimer.Start();
            }
            return;
        }

        if (_selecting)
        {
            // Links are intercepted before selection starts. Other
            // text-bearing controls are intentionally allowed to participate
            // in the page selection drag.
            float x = e.X / EffectiveZoom + PaintScrollX;
            float y = e.Y / EffectiveZoom + PaintScrollY;
            LayoutBox? f = null;
            var oldFocus = _selFocus;
            int oldFocusOffset = _selFocusOffset;
            var selectionView = _selectionFrame ?? _pendingSelectionFrame;
            if (selectionView != null)
            {
                // Use the recursive frame hit result here. Looking the frame
                // box up only in _frames fails for nested frames because their
                // boxes live in the parent's ChildFrames collection. That made
                // drag-selection work in a top-level frame but stop updating as
                // soon as the pointer was inside a nested frame.
                if (TryHitFrame(x, y, out var selectionHit) &&
                    ReferenceEquals(selectionHit.View, selectionView))
                {
                    var localHit = HitTestTextPosition(selectionHit.View.RootBox,
                        selectionHit.LocalX, selectionHit.LocalY);
                    if (localHit != null)
                    {
                        f = localHit.Value.Box;
                        _selFocusOffset = localHit.Value.Offset;
                    }
                    else
                    {
                        if (_pendingSelectionAnchor?.BoxType == BoxType.Replaced &&
                            IsPageSelectableControl(_pendingSelectionAnchor.Element))
                        {
                            f = _selFocus ?? _pendingSelectionAnchor;
                        }
                        else
                        {
                            var nearest = FindNearestSelectionPosition(selectionView.RootBox, selectionHit.LocalX, selectionHit.LocalY);
                            if (nearest != null)
                            {
                                f = nearest.Value.Box;
                                _selFocusOffset = nearest.Value.Offset;
                            }
                            else
                            {
                                f = _selFocus;
                            }
                        }
                    }
                }
            }
            else if (_rootBox != null)
            {
                var pageHit = HitTestTextPosition(_rootBox, x, y);
                if (pageHit != null)
                {
                    f = pageHit.Value.Box;
                    _selFocusOffset = pageHit.Value.Offset;
                }
                else
                {
                    // When the pointer crosses a control bevel, table gutter, or
                    // whitespace gap, keep a deterministic endpoint rather than
                    // allowing the range to become null. The nearest range box
                    // gives drag-selection a stable continuation point and is
                    // especially important for <select> text.
                    if (_pendingSelectionAnchor?.BoxType == BoxType.Replaced &&
                        IsPageSelectableControl(_pendingSelectionAnchor.Element))
                    {
                        f = _selFocus ?? _pendingSelectionAnchor;
                    }
                    else
                    {
                        var nearest = FindNearestSelectionPosition(_rootBox, x, y);
                        if (nearest != null)
                        {
                            f = nearest.Value.Box;
                            _selFocusOffset = nearest.Value.Offset;
                        }
                        else
                        {
                            f = _selFocus;
                        }
                    }
                }
            }

            bool moved = Math.Abs(e.X - _selStart.X) + Math.Abs(e.Y - _selStart.Y) > 4;
            if (moved && !_dragMoved)
            {
                _dragMoved = true;
                _selectionFrame = _pendingSelectionFrame;
                _selAnchor = _pendingSelectionAnchor;
                _selAnchorOffset = _pendingSelectionAnchorOffset;
                _selFocus = f ?? _pendingSelectionAnchor;
                if (f == null) _selFocusOffset = _pendingSelectionAnchorOffset;
                Invalidate();
            }
            else if (_dragMoved && f != null &&
                     (f != oldFocus || _selFocusOffset != oldFocusOffset))
            {
                _selFocus = f;
                Invalidate();
            }
            return;
        }

        if (_rootBox == null || _document == null) return;

        float mx = e.X / EffectiveZoom + PaintScrollX;
        float my = e.Y / EffectiveZoom + PaintScrollY;
        bool skipHoverRelayout = _skipHoverRelayoutOnceAfterZoom;

        DomElement? element;
        DomDocument doc = _document;
        FrameView? hoverFrame = null;
        FrameHit frameHit = default;

        if (TryHitFrame(mx, my, out frameHit))
        {
            hoverFrame = frameHit.View;
            element = HitTestElement(hoverFrame.RootBox, frameHit.LocalX, frameHit.LocalY);
            doc = hoverFrame.Document;
        }
        else
        {
            element = HitTestElement(_rootBox, mx, my);
        }

        bool overTextareaScrollbar = _focusedInput?.TagName == "textarea" &&
            TryGetFocusedTextareaScrollbarPoint(mx, my, out _, out _, out _);
        bool overSelectScrollbar = TryGetSelectScrollbarPoint(mx, my,
            out _, out _, out _, out _, out _);

        if (TryGetEditableFieldAtPoint(mx, my, out var cursorField, out _, out var cursorFrame, out var cursorHit))
        {
            element = cursorField;
            hoverFrame = cursorFrame;
            if (cursorFrame != null)
            {
                frameHit = cursorHit;
                doc = cursorFrame.Document;
            }
        }

        // Gesture zoom changes only the temporary visual transform. Do not let
        // it enter CSS hover/active relayout, but still update the cursor from
        // the current hit-test result. This keeps link/text cursors responsive
        // while the pointer is over zoomed content.
        bool gestureCursorOnly = IsGestureZoomActive && !_selecting && !_fieldDragging;
        if (gestureCursorOnly)
        {
            UpdateCursorForHit(element, hoverFrame, frameHit, doc, overTextareaScrollbar, overSelectScrollbar, mx, my);
            Invalidate();
            return;
        }

        var hoverAnchor = element != null && element.TagName == "a"
            ? element
            : element != null ? FindAncestor(element, "a") : null;

        bool fieldInteraction = IsEditableField(element) || IsEditableField(doc.FocusedElement);
        bool pressedLinkInteraction = _pressedControl != null &&
            _pressedControl.HasAttr("href") && ReferenceEquals(_pressedControlFrame, hoverFrame);
        bool selectionInteraction = (_selAnchor != null && _selFocus != null) || _selectAllPage;
        bool interactionRelayout = !fieldInteraction && !pressedLinkInteraction &&
            !IsGestureZoomActive && !selectionInteraction && !skipHoverRelayout &&
            !IsZoomInteractionStabilizing;
        UpdateCssInteractionState(doc, element, doc.ActiveElement, doc.FocusedElement,
            relayout: interactionRelayout);
        UpdateCursorForHit(element, hoverFrame, frameHit, doc, overTextareaScrollbar, overSelectScrollbar, mx, my);

        if (!ReferenceEquals(element, _lastHoveredElement) || !ReferenceEquals(hoverFrame, _lastHoveredFrame))
        {
            if (_lastHoveredElement != null)
            {
                var oldFrame = _lastHoveredFrame;
                var oldJs = oldFrame?.Interpreter ?? _jsInterpreter;
                int oldClientX = e.X, oldClientY = e.Y;
                if (oldFrame != null)
                    TryGetFrameRelativeClientPoint(oldFrame, mx, my, out oldClientX, out oldClientY);
                oldJs?.FireEvent(_lastHoveredElement, "onmouseout",
                    oldJs.CreateMouseEvent("onmouseout", oldClientX, oldClientY, 0));
                bool hoverRelayout = !IsGestureZoomActive &&
                    !skipHoverRelayout &&
                    !IsZoomInteractionStabilizing &&
                    _selAnchor == null && _selFocus == null && !_selectAllPage;
                if (oldFrame != null)
                    UpdateCssInteractionState(oldFrame.Document, null, oldFrame.Document.ActiveElement,
                        oldFrame.Document.FocusedElement, relayout: hoverRelayout);
                else
                    UpdateCssInteractionState(_document, null, _document.ActiveElement,
                        _document.FocusedElement, relayout: hoverRelayout);
            }
            if (element != null)
            {
                var hoverJs = hoverFrame?.Interpreter ?? _jsInterpreter;
                int hoverClientX = e.X, hoverClientY = e.Y;
                if (hoverFrame != null)
                {
                    hoverClientX = (int)Math.Round(frameHit.ViewX);
                    hoverClientY = (int)Math.Round(frameHit.ViewY);
                }
                hoverJs?.FireEvent(element, "onmouseover",
                    hoverJs.CreateMouseEvent("onmouseover", hoverClientX, hoverClientY, 0));
                var status = hoverJs?.WindowObject?.Get("status");
                if (status is { Type: JsType.String } && status.GetString().Length > 0)
                    SetStatus(status.GetString());
            }
            _lastHoveredElement = element;
            _lastHoveredFrame = hoverFrame;
        }
        if (skipHoverRelayout)
            _skipHoverRelayoutOnceAfterZoom = false;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_topScrollbarDragAxis != null)
        {
            _topScrollbarDragAxis = null;
            _topScrollbarGrabOffset = 0f;
            Capture = false;
            Invalidate();
            return;
        }

        if (_frameScrollbarDragView != null)
        {
            _frameScrollbarDragBox = null;
            _frameScrollbarDragView = null;
            _frameScrollbarMouseDownHandled = false;
            Capture = false;
            Invalidate();
            return;
        }

        if (_frameScrollbarMouseDownHandled)
        {
            _frameScrollbarMouseDownHandled = false;
            Capture = false;
            return;
        }

        if (_textareaScrollbarDragging)
        {
            _textareaScrollbarDragging = false;
            _textareaScrollbarDragFrame = null;
            Capture = false;
            PersistFocusedTextareaScrollState();
            return;
        }

        if (_selectScrollbarDragging)
        {
            _selectScrollbarDragging = false;
            _selectScrollbarDragSelect = null;
            _selectScrollbarDragFrame = null;
            Capture = false;
            return;
        }

        if (_pressedJavaAppletElement != null)
        {
            var applet = _pressedJavaAppletElement;
            _pressedJavaAppletElement = null;
            float px = e.X / EffectiveZoom + PaintScrollX;
            float py = e.Y / EffectiveZoom + PaintScrollY;
            if (TryGetJavaAppletBox(applet, out var jb))
                SendJavaAppletInput(applet, jb, new Retro96.Engine.Java.JavaInput(
                    Retro96.Engine.Java.JavaInputKind.MouseUp, (int)Math.Round(px - jb.X), (int)Math.Round(py - jb.Y),
                    (int)e.Button, 0, 0, '\0', (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Alt) != 0));
            Capture = false;
            return;
        }

        if (_pressedEmbeddedElement != null)
        {
            var embed = _pressedEmbeddedElement;
            _pressedEmbeddedElement = null;
            float px = e.X / EffectiveZoom + PaintScrollX;
            float py = e.Y / EffectiveZoom + PaintScrollY;
            if (TryGetEmbeddedBox(embed, out var eb))
                SendEmbeddedInput(embed, MakeEmbeddedMouseEvent(EmbeddedInputEventKind.MouseUp, e, eb, px, py));
            Capture = false;
            return;
        }

        if (_embeddedMidiPressedAction != null)
        {
            string action = _embeddedMidiPressedAction;
            _embeddedMidiPressedAction = null;
            _embeddedMidiVolumeDragging = false;
            Capture = false;
            if (e.Button == MouseButtons.Left && action != "none")
                EmbeddedMidiControlRequested?.Invoke(action);
            return;
        }

        // FIX: releasing the mouse OFF a pressed button used to activate it
        // anyway — browsers cancel the click in that case.
        var pressedEl = _pressedControl;
        var pressedFrame = _pressedControlFrame;
        bool wasPressed = pressedEl != null;

        bool wasSelecting = _selecting;
        _selecting = false;
        Capture = false;

        // Release any pressed button bevel and clear :active in the same
        // document that received the mouse-down.
        if (pressedEl != null)
        {
            if (pressedFrame != null)
                UpdateCssInteractionState(pressedFrame.Document,
                    pressedFrame.Document.HoveredElement, null,
                    pressedFrame.Document.FocusedElement, relayout: false);
            else if (_document != null)
                UpdateCssInteractionState(_document, _document.HoveredElement, null,
                    _document.FocusedElement, relayout: false);
            bool pressedLink = pressedEl.TagName == "a" && pressedEl.HasAttr("href");
            _pressedControl = null;
            _pressedControlFrame = null;
            if (pressedLink)
                Invalidate();
            else
                RerenderNow();
        }

        if (_fieldDragging)
        {
            _fieldDragging = false;
            _fieldDragPointerFrame = null;
            _fieldDragAutoScrollTimer.Stop();
            // The field click was fully handled at mouse-down. Do not clear
            // the active state through a second full relayout here; doing so
            // moved textarea geometry after a page scroll.
            var focusDoc = _focusedInputFrame?.Document ?? _document;
            if (focusDoc != null && focusDoc.ActiveElement != null)
                UpdateCssInteractionState(focusDoc, focusDoc.HoveredElement,
                    null, focusDoc.FocusedElement, relayout: false);
            return;
        }

        // Selection releases are paint-only. Rebuilding layout here can replace
        // the boxes selected by the preceding MouseDown, so a triple-click
        // highlight flashes and disappears. Drag selection also keeps its exact
        // geometry until the next real interaction.
        if (_suppressNextMouseUp)
        {
            _suppressNextMouseUp = false;
            _dragMoved = false;
            return;
        }

        if (_dragMoved)
        {
            _dragMoved = false;
            return;
        }

        if (wasSelecting)
        {
            ClearPageSelection();
            Invalidate();
        }

        if (_document != null && _document.ActiveElement != null)
            UpdateCssInteractionState(_document, _document.HoveredElement, null, _document.FocusedElement,
                relayout: false);

        if (e.Button == MouseButtons.Right)
        {
            ShowContextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;

        if (wasPressed)
        {
            float ux = e.X / EffectiveZoom + PaintScrollX;
            float uy = e.Y / EffectiveZoom + PaintScrollY;
            DomElement? upEl = null;
            if (pressedFrame != null)
            {
                if (TryHitFrame(ux, uy, out var releaseHit) &&
                    ReferenceEquals(releaseHit.View, pressedFrame))
                    upEl = HitTestDeepestBox(pressedFrame.RootBox,
                        releaseHit.LocalX, releaseHit.LocalY)?.Element;
            }
            else if (_rootBox != null)
            {
                upEl = HitTestDeepestBox(_rootBox, ux, uy)?.Element;
            }
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
        if (_suppressNextMouseDoubleClick)
        {
            _suppressNextMouseDoubleClick = false;
            return;
        }
        if (_rootBox == null || e.Button != MouseButtons.Left)
            return;

        float x = e.X / EffectiveZoom + PaintScrollX;
        float y = e.Y / EffectiveZoom + PaintScrollY;

        // Double-click inside an editable field: the primary mouse-down path
        // already performs the selection. Keep this WinForms event as a
        // compatibility fallback for both the page and nested frames.
        if (_focusedInput != null && _fieldClickCount == 2)
        {
            if (_focusedInputFrame != null &&
                TryHitFrame(x, y, out var fieldHit) &&
                ReferenceEquals(fieldHit.View, _focusedInputFrame))
            {
                var box = FindBoxForElement(fieldHit.View.RootBox, _focusedInput);
                if (box != null)
                {
                    int caret = FieldCaretFromPoint(_focusedInput, box, fieldHit.LocalX, fieldHit.LocalY, fieldHit.View);
                    SelectFieldWord(_focusedInput, ref caret, ref _fieldSelAnchor);
                    _fieldCaret = caret;
                    Invalidate();
                    return;
                }
            }
            else
            {
                var box = FindBoxForElement(_rootBox!, _focusedInput);
                if (box != null && box.BorderRect.Contains(x, y))
                {
                    int caret = FieldCaretFromPoint(_focusedInput, box, x, y);
                    SelectFieldWord(_focusedInput, ref caret, ref _fieldSelAnchor);
                    _fieldCaret = caret;
                    Invalidate();
                    return;
                }
            }
        }

        // Double-click on frame text uses the frame's own layout tree,
        // including nested iframe/frame views.
        if (TryHitFrame(x, y, out var frameHit))
        {
            var frameView = frameHit.View;
            var frameWordHit = HitTestTextPosition(frameView.RootBox, frameHit.LocalX, frameHit.LocalY);
            if (frameWordHit != null)
            {
                var frameWord = frameWordHit.Value.Box;
                _focusedFrame = frameHit.Box;
                _selectionFrame = frameView;
                if (e.Clicks >= 3)
                    SelectTextBlock(frameWord);
                else
                    SelectWordAt(frameWord, frameWordHit.Value.Offset);
                Invalidate();
                return;
            }
        }

        // Double-click on page text: each fragment IS one word (spaces are
        // glued to the previous word) — select that box.
        var wordHit = HitTestTextPosition(_rootBox, x, y);
        if (wordHit != null)
        {
            var word = wordHit.Value.Box;
            _selectionFrame = null;
            if (e.Clicks >= 3)
                SelectTextBlock(word);
            else
                SelectWordAt(word, wordHit.Value.Offset);
            Invalidate();
        }
    }

    private void SelectTextBlock(LayoutBox word)
    {
        if (word.BoxType == BoxType.Replaced && IsPageSelectableControl(word.Element))
        {
            _selAnchor = _selFocus = word;
            _selAnchorOffset = 0;
            _selFocusOffset = GetSelectionBoxTextLength(word);
            return;
        }

        LayoutBox? selectionRoot = _selectionFrame?.RootBox ?? _rootBox;
        if (selectionRoot == null) return;

        // Triple-click normally selects the surrounding text block.
        // <center> is a special 1990s-era container: it is often used as a
        // broad wrapper for several independent pieces of content, including
        // image / text / image constructions. Treating the whole <center> as
        // one semantic block therefore made a triple-click on a tiny label
        // select every text run in that wrapper (sometimes effectively the
        // whole page). For <center>, select only the clicked visual line.
        DomElement? scope = null;
        DomElement? inlineFallback = null;
        for (var node = word.Element; node != null; node = node.Parent as DomElement)
        {
            if (IsTripleClickBlockTag(node.TagName))
            {
                scope = node;
                break;
            }
            if (inlineFallback == null && IsTripleClickInlineFallbackTag(node.TagName))
                inlineFallback = node;
        }

        List<LayoutBox> textBoxes = new();
        bool centerScope = scope?.TagName == "center";
        if (centerScope)
        {
            // <center> is a layout/alignment container rather than a reliable
            // text-selection paragraph. Keep only text boxes occupying the same
            // rendered line as the clicked text. This makes constructions such
            // as <img> Social Links <img> triple-clickable without pulling in
            // the rest of the page.
            float lineTolerance = Math.Max(2f, Math.Min(word.ContentRect.Height, 24f) * 0.5f);
            float lineTop = word.ContentRect.Top;
            textBoxes = EnumerateSelectionTree(selectionRoot)
                .Where(IsSelectionEligibleBox)
                .Where(b => IsElementWithin(b.Element, scope!))
                .Where(b => Math.Abs(b.ContentRect.Top - lineTop) <= lineTolerance)
                .ToList();

            // EnumerateSelectionTree already follows layout/DOM source order.
            // Keep that order so the ordinary range model can represent the
            // selected line without screen-coordinate reordering.

            // Do not install <center> as a semantic copy scope. Copying should
            // follow the exact selected text boxes above, not the center's full
            // InnerText.
            _selectionSemanticScope = null;
            _selectionSemanticScopeFrame = null;
        }
        else
        {
            scope ??= inlineFallback;
            _selectionSemanticScope = scope;
            _selectionSemanticScopeFrame = _selectionFrame;

            if (scope != null)
            {
                // Never rely on the scope element having a dedicated LayoutBox.
                // Inline formatting such as <p><font>...</font></p> and table text
                // can flatten the DOM across anonymous layout wrappers. The DOM
                // ancestry filter is stable and collects every fragmented word run
                // belonging to the same semantic block.
                textBoxes = EnumerateSelectionTree(selectionRoot)
                    .Where(IsSelectionEligibleBox)
                    .Where(b => IsElementWithin(b.Element, scope))
                    .ToList();
            }
        }

        if (textBoxes.Count == 0)
        {
            // Final fallback for anonymous layout fragments: walk the layout
            // ancestors to the nearest semantic container, then keep the tree
            // order exactly as the range model expects.
            LayoutBox? container = null;
            for (var current = word; current != null; current = current.Parent)
            {
                if (current.Element != null &&
                    (IsTripleClickBlockTag(current.Element.TagName) ||
                     IsTripleClickInlineFallbackTag(current.Element.TagName)))
                {
                    container = current;
                    break;
                }
            }

            container ??= word;
            textBoxes = EnumerateSelectionTree(container)
                .Where(IsSelectionEligibleBox)
                .ToList();
        }

        // A triple click on normal text must select at least the clicked run even
        // when the surrounding element has no dedicated layout box.
        if (textBoxes.Count == 0 && IsSelectionEligibleBox(word))
            textBoxes.Add(word);

        if (textBoxes.Count == 0) return;

        _selAnchor = textBoxes[0];
        _selFocus = textBoxes[^1];
        _selAnchorOffset = 0;
        _selFocusOffset = GetSelectionBoxTextLength(_selFocus);
    }

    private void SelectWordAt(LayoutBox box, int offset)
    {
        if (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element))
        {
            int length = GetSelectionBoxTextLength(box);
            _selAnchor = _selFocus = box;
            _selAnchorOffset = 0;
            _selFocusOffset = length;
            return;
        }

        string text = box.TextRun ?? string.Empty;
        if (text.Length == 0) return;
        int p = Math.Clamp(offset, 0, text.Length - 1);
        if (char.IsWhiteSpace(text[p]))
        {
            int left = p - 1;
            while (left >= 0 && char.IsWhiteSpace(text[left])) left--;
            int right = p + 1;
            while (right < text.Length && char.IsWhiteSpace(text[right])) right++;
            if (left >= 0) p = left;
            else if (right < text.Length) p = right;
            else
            {
                _selAnchor = _selFocus = box;
                _selAnchorOffset = _selFocusOffset = 0;
                return;
            }
        }

        int start = p;
        int end = p + 1;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

        _selAnchor = _selFocus = box;
        _selAnchorOffset = start;
        _selFocusOffset = end;
    }

    private readonly record struct TextSelectionHit(LayoutBox Box, int Offset);

    private static bool IsTripleClickBlockTag(string tag) => tag is
        "p" or "div" or "li" or "pre" or "blockquote" or
        "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
        "td" or "th" or "dt" or "dd" or "center";

    private static bool IsTripleClickInlineFallbackTag(string tag) => tag is
        "font" or "b" or "strong" or "i" or "em" or "u" or "small" or
        "big" or "tt" or "s" or "strike" or "a";

    private static DomElement? GetTextClickIdentity(LayoutBox? box)
    {
        // Inline text is split into LayoutBoxes whose layout parents may be
        // anonymous. Follow the DOM parent chain to find the semantic block;
        // this is what makes triple-click work for <p><font>long text...</font>.
        DomElement? inlineFallback = null;
        for (DomNode? node = box?.Element; node is DomElement element; node = element.Parent)
        {
            if (IsTripleClickBlockTag(element.TagName))
                return element;
            if (inlineFallback == null && IsTripleClickInlineFallbackTag(element.TagName))
                inlineFallback = element;
        }

        for (var current = box?.Parent; current != null; current = current.Parent)
        {
            if (current.Element is not DomElement element) continue;
            if (IsTripleClickBlockTag(element.TagName))
                return element;
            if (inlineFallback == null && IsTripleClickInlineFallbackTag(element.TagName))
                inlineFallback = element;
        }
        return inlineFallback ?? box?.Element;
    }

    private TextSelectionHit? HitTestTextPosition(LayoutBox root, float x, float y)
    {
        // Selection dragging is a very hot mouse-move path. Resolve the deepest
        // hit first; the old code scanned every descendant for animated content
        // before doing the ordinary hit-test, which caused a visible lag spike
        // on long pages even for a tiny text selection.
        var deepest = HitTestDeepestBox(root, x, y);

        if (deepest != null)
        {
            // Selectable controls own their text source even when the deepest
            // layout box is a bevel/arrow child.
            var controlBox = FindPageSelectableControlBox(deepest);
            if (controlBox != null)
            {
                var hitRect = controlBox.BorderRect;
                if (hitRect.Contains(x, y))
                    return new TextSelectionHit(controlBox, GetControlTextOffset(controlBox, x, y));
            }

            // Common case: the deepest hit is already the text fragment. Avoid a
            // second tree traversal during the drag. Marquee/blink text gets its
            // animated offset here when applicable.
            if (IsSelectionEligibleBox(deepest))
            {
                var rect = deepest.BorderRect;
                if (IsSelectionAnimated(deepest.Element))
                {
                    if (TryGetSelectionAnimationOffset(deepest, root, out float animatedX))
                    {
                        rect.X += animatedX;
                        if (rect.Contains(x, y))
                            return new TextSelectionHit(deepest, GetTextOffsetAtPoint(deepest, x - animatedX));
                    }
                }
                else if (rect.Contains(x, y))
                {
                    return new TextSelectionHit(deepest, GetTextOffsetAtPoint(deepest, x));
                }
            }

            // Cheap parent-chain fallback for replaced/selectable controls.
            for (var candidate = deepest; candidate != null; candidate = candidate.Parent)
            {
                if (candidate.BoxType != BoxType.Replaced || !IsPageSelectableControl(candidate.Element))
                    continue;
                var hitRect = candidate.BorderRect;
                if (hitRect.Contains(x, y))
                    return new TextSelectionHit(candidate, GetControlTextOffset(candidate, x, y));
                break;
            }
        }

        // A direct hit failed to resolve. Keep marquee/blink support as a
        // fallback only, instead of paying the full descendant scan for every
        // normal text mouse move.
        foreach (var candidate in root.Descendants())
        {
            if (!IsSelectionEligibleBox(candidate) || !IsSelectionAnimated(candidate.Element))
                continue;
            if (!TryGetSelectionAnimationOffset(candidate, root, out float animatedX))
                continue;
            var rect = candidate.BorderRect;
            rect.X += animatedX;
            if (rect.Contains(x, y))
                return new TextSelectionHit(candidate, GetTextOffsetAtPoint(candidate, x - animatedX));
        }

        var box = HitTestTextBox(root, x, y);
        if (box != null && IsSelectionEligibleBox(box))
        {
            if (!TryGetSelectionAnimationOffset(box, root, out float boxAnimationX)) return null;
            var visibleRect = box.BorderRect;
            visibleRect.X += boxAnimationX;
            if (!visibleRect.Contains(x, y)) return null;
            return new TextSelectionHit(box, GetTextOffsetAtPoint(box, x - boxAnimationX));
        }

        return null;
    }

    private TextSelectionHit? FindNearestSelectionPosition(LayoutBox root, float x, float y)
    {
        // Pick the nearest visual line first. Pure Euclidean distance can jump
        // to a nearby table cell or paragraph when the pointer crosses a line
        // gap, which makes a manual selection highlight twitch or appear to
        // jump backwards.
        LayoutBox? best = null;
        float bestScore = float.PositiveInfinity;

        foreach (var candidate in OrderedSelectionRangeBoxes(root))
        {
            var rect = candidate.ContentRect;
            if (rect.Width <= 0.01f || rect.Height <= 0.01f) continue;

            float verticalGap = y < rect.Top ? rect.Top - y : y > rect.Bottom ? y - rect.Bottom : 0f;
            float horizontalGap = x < rect.Left ? rect.Left - x : x > rect.Right ? x - rect.Right : 0f;
            float centerDelta = Math.Abs(y - (rect.Top + rect.Height * 0.5f));
            float score = verticalGap * 10000f + horizontalGap * 10f + centerDelta;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best == null) return null;
        int length = GetSelectionBoxTextLength(best);
        int offset = x <= best.ContentRect.Left ? 0 : length;
        return new TextSelectionHit(best, offset);
    }

    private static bool IsSelectionAnimated(DomElement? element)
    {
        for (var node = element; node != null; node = node.Parent as DomElement)
        {
            if ((BrowserRuntime.MarqueeEnabled && node.TagName == "marquee") ||
                (BrowserRuntime.BlinkEnabled && node.TagName == "blink"))
                return true;
        }
        return false;
    }

    private static string TransformSelectionText(string text, TextTransform transform)
    {
        return transform switch
        {
            TextTransform.Uppercase => text.ToUpperInvariant(),
            TextTransform.Lowercase => text.ToLowerInvariant(),
            TextTransform.Capitalize => CapitalizeSelectionWords(text),
            _ => text
        };
    }

    private static string CapitalizeSelectionWords(string text)
    {
        var chars = text.ToCharArray();
        bool start = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsLetterOrDigit(chars[i]))
            {
                if (start) chars[i] = char.ToUpperInvariant(chars[i]);
                start = false;
            }
            else start = true;
        }
        return new string(chars);
    }

    private int GetTextOffsetAtPoint(LayoutBox box, float x)
    {
        string text = box.TextRun ?? string.Empty;
        if (text.Length == 0) return 0;

        var style = box.StyleOverride ?? box.Element?.Style;
        if (_fontCache == null || style == null)
            return Math.Clamp((int)Math.Round(x - box.ContentRect.X), 0, text.Length);

        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        var font = _fontCache.Resolve(style.FontFamily, style.FontSize,
            (int)style.FontWeight, italic, oblique);
        string visualText = TransformSelectionText(text, style.TextTransform);
        float target = Math.Max(0f, x - box.ContentRect.X);
        Graphics measureGraphics = _measureGfx ?? MeasureGraphics;

        // Selection hit-testing runs for every mouse move while dragging. The
        // previous linear prefix scan measured 1, 2, 3, ... characters, turning
        // a long text run into O(n²) GDI work per pointer event. SelectionAdvance
        // is monotonic, so binary-search the nearest prefix instead. This keeps
        // the same visual metrics while reducing the search to O(log n) prefix
        // measurements.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            float width = SelectionAdvance(measureGraphics, font, visualText[..mid], style);
            if (width < target) lo = mid;
            else hi = mid - 1;
        }

        int leftIndex = Math.Clamp(lo, 0, text.Length);
        int rightIndex = Math.Min(text.Length, leftIndex + 1);
        float leftWidth = SelectionAdvance(measureGraphics, font, visualText[..leftIndex], style);
        float rightWidth = rightIndex == leftIndex
            ? leftWidth
            : SelectionAdvance(measureGraphics, font, visualText[..rightIndex], style);
        if (rightIndex == leftIndex ||
            (target - leftWidth) <= (rightWidth - target))
            return leftIndex;
        return rightIndex;
    }

    private float SelectionAdvance(Graphics g, Font font, string text, ComputedStyle style)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float width = g.MeasureString(text, font, int.MaxValue, NewSelectionFormat()).Width;
        if (Math.Abs(style.LetterSpacing) > 0.001f || Math.Abs(style.WordSpacing) > 0.001f)
        {
            width = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                width += g.MeasureString(text[i].ToString(), font, int.MaxValue, NewSelectionFormat()).Width;
                if (i + 1 < text.Length) width += style.LetterSpacing;
                if (char.IsWhiteSpace(text[i])) width += style.WordSpacing;
            }
        }
        return Math.Max(0f, width);
    }

    private StringFormat NewSelectionFormat()
    {
        var sf = new StringFormat();
        sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
        return sf;
    }

    private static bool IsSelectionInvisibleCharacter(char ch) =>
        char.IsWhiteSpace(ch) || ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF';

    private static bool HasSelectionVisibleCharacters(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        for (int i = start; i < end; i++)
        {
            if (!IsSelectionInvisibleCharacter(text[i])) return true;
        }
        return false;
    }

    private IEnumerable<RectangleF> SelectionVisualSpans(Graphics g, LayoutBox box, int start, int end)
    {
        string text = box.TextRun ?? string.Empty;
        if (text.Length == 0 || box.Width <= 0.01f || box.Height <= 0.01f) yield break;
        var content = box.ContentRect;
        if (!float.IsFinite(content.X) || !float.IsFinite(content.Y) ||
            !float.IsFinite(content.Width) || !float.IsFinite(content.Height) ||
            content.Width <= 0.01f || content.Height <= 0.01f)
            yield break;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        if (end <= start || !HasSelectionVisibleCharacters(text, start, end)) yield break;

        var style = box.StyleOverride ?? box.Element?.Style;
        if (_fontCache == null || style == null)
            yield break;

        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        var font = _fontCache.Resolve(style.FontFamily, style.FontSize,
            (int)style.FontWeight, italic, oblique);
        float lineHeight = Math.Max(1f, Math.Min(box.Height > 0 ? box.Height : font.GetHeight(g), font.GetHeight(g)));
        float baseX = box.ContentRect.X;
        float y = box.ContentRect.Y;
        if (style.LineHeightMode != LineHeightMode.Normal)
            y += Math.Max(0f, (content.Height - font.GetHeight(g)) / 2f);
        string visualText = TransformSelectionText(text, style.TextTransform);

        // Whitespace at the *edges* of a selected text run is often layout-only
        // HTML indentation/newline content. It has no visible glyphs, but its
        // measured advance used to produce the phantom blue rectangles seen
        // beside inputs, table cells and animated marquee text. Keep whitespace
        // between real glyphs selected, but trim invisible characters at both
        // ends so the selection overlay starts/ends where something is actually
        // painted.
        int visualStart = start;
        int visualEnd = end;
        while (visualStart < visualEnd && IsSelectionInvisibleCharacter(text[visualStart]))
            visualStart++;
        while (visualEnd > visualStart && IsSelectionInvisibleCharacter(text[visualEnd - 1]))
            visualEnd--;
        if (visualEnd <= visualStart) yield break;

        // A selected range inside one text run is one continuous visual
        // selection, including the selected whitespace between words. Splitting
        // each word into its own rectangle leaves visible holes in the highlight
        // and does not match normal browser selection rendering.
        float x1 = baseX + SelectionAdvance(g, font, visualText[..visualStart], style);
        float x2 = baseX + SelectionAdvance(g, font, visualText[..visualEnd], style);
        float left = Math.Max(x1, content.Left);
        float right = Math.Min(x2, content.Right);
        float height = Math.Max(0f, Math.Min(lineHeight, content.Bottom - y));
        if (right > left && height > 0.01f)
            yield return new RectangleF(left, y, right - left, height);
    }

    private static IEnumerable<RectangleF> MergeSelectionSpans(List<RectangleF> spans)
        => SelectionOverlay.MergeSpans(spans);

    internal void ClearPageSelection()
    {
        _selectAllPage = false;
        _selAnchor = _selFocus = null;
        _selAnchorOffset = _selFocusOffset = 0;
        _selectionFrame = null;
        _pendingSelectionAnchor = null;
        _pendingSelectionAnchorOffset = 0;
        _pendingSelectionFrame = null;
        _selectionSemanticScope = null;
        _selectionSemanticScopeFrame = null;
        _selectionRangeCacheRoot = null;
        _selectionRangeCache = null;
        _suppressNextMouseDoubleClick = false;
    }

    private void CancelPageTextSelectionForPointerInteraction()
    {
        ClearPageSelection();
        _selecting = false;
        _dragMoved = false;
        _suppressNextMouseUp = false;
        _textClickCount = 0;
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
        if (_lastHoveredElement != null && !IsGestureZoomActive)
        {
            var hoverJs = _lastHoveredFrame?.Interpreter ?? _jsInterpreter;
            hoverJs?.FireEvent(_lastHoveredElement, "onmouseout");
            bool hoverRelayout = !IsGestureZoomActive &&
                !IsZoomInteractionStabilizing &&
                _selAnchor == null && _selFocus == null && !_selectAllPage;
            if (_lastHoveredFrame != null)
                UpdateCssInteractionState(_lastHoveredFrame.Document, null,
                    _lastHoveredFrame.Document.ActiveElement, _lastHoveredFrame.Document.FocusedElement,
                    relayout: hoverRelayout);
            else if (_document != null)
                UpdateCssInteractionState(_document, null, _document.ActiveElement,
                    _document.FocusedElement, relayout: hoverRelayout);
            _lastHoveredElement = null;
            _lastHoveredFrame = null;
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
            // Password fields are intentionally write-only to the clipboard.
            // Never fall back to their logical value even when Ctrl+C comes
            // through the generic context-menu copy path.
            if (_focusedInput != null && IsPasswordField(_focusedInput))
                return;

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

    private static bool IsSelectionExcludedElement(DomElement? element)
    {
        for (var node = element; node != null; node = node.Parent as DomElement)
        {
            // Form controls have their own selection/caret model. Their text
            // must never leak into the page-selection overlay. Script/style
            // nodes likewise are not rendered/selectable document text.
            if (node.TagName is "input" or "textarea" or "select" or "option" or
                "button" or "script" or "style" or "noscript")
                return true;
        }
        return false;
    }

    private static bool IsSelectionEligibleBox(LayoutBox box)
    {
        if (string.IsNullOrEmpty(box.TextRun) || box.Width <= 0.01f || box.Height <= 0.01f)
            return false;
        var rect = box.ContentRect;
        if (rect.Width <= 0.01f || rect.Height <= 0.01f ||
            !float.IsFinite(rect.X) || !float.IsFinite(rect.Y) ||
            !float.IsFinite(rect.Width) || !float.IsFinite(rect.Height))
            return false;
        return !IsSelectionExcludedElement(box.Element);
    }

    private static bool IsPageSelectableControl(DomElement? element)
    {
        if (element == null) return false;

        // Buttons are activation controls, not page-text selection surfaces.
        // Keep their labels available to Find, but never turn the entire
        // button face into a blue selection rectangle.
        switch (element.TagName)
        {
            case "textarea":
            case "select":
                return true;
            case "input":
            {
                string type = element.GetAttrOrDefault("type", "text").Trim()
                    .ToLowerInvariant();
                // Text-entry inputs participate in page text selection.
                // Push-button and other non-text input controls are activation
                // widgets, just like <button>, and must never get a blue text
                // selection highlight over their chrome.
                return type is not ("hidden" or "button" or "submit" or "reset" or
                                    "image" or "checkbox" or "radio" or "file");
            }
            case "button":
                return false;
            default:
                return false;
        }
    }

    private static bool IsSearchableControlElement(DomElement? element)
    {
        if (element == null) return false;
        return element.TagName switch
        {
            "textarea" or "select" or "button" => true,
            "input" => !element.GetAttrOrDefault("type", "text").Trim()
                .Equals("hidden", StringComparison.OrdinalIgnoreCase) &&
                !element.GetAttrOrDefault("type", "text").Trim()
                    .Equals("password", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string GetSelectOptionText(DomElement option) =>
        option.InnerText.Trim();

    private static bool IsPasswordField(DomElement? element) =>
        element?.TagName == "input" &&
        element.GetAttrOrDefault("type", "text").Trim()
            .Equals("password", StringComparison.OrdinalIgnoreCase);

    private float GetFieldViewportScrollX(DomElement element, LayoutBox? box = null)
    {
        if (element.TagName != "input" || IsPasswordField(element) ||
            element.GetAttrOrDefault("type", "text").Trim().Equals("file", StringComparison.OrdinalIgnoreCase))
            return 0f;

        float value = ReferenceEquals(element, _focusedInput)
            ? _fieldScrollX
            : (_fieldFindScrollXs.TryGetValue(element, out var stored) ? stored : 0f);
        if (box == null) return Math.Max(0f, value);
        return Math.Clamp(value, 0f, GetSingleLineFieldMaxScroll(element, box));
    }

    private float GetRendererFieldScrollX(DomElement element)
    {
        // Renderer instances also paint embedded-frame documents, whose LayoutBox
        // is not part of _rootBox. Do not silently discard a valid Find viewport
        // just because the owning box belongs to a frame.
        return GetFieldViewportScrollX(element);
    }

    private static string GetSelectableControlText(DomElement el)
    {
        switch (el.TagName)
        {
            case "textarea":
                return GetFieldText(el);

            case "button":
            {
                string label = (el.InnerText ?? string.Empty).Trim();
                if (label.Length == 0)
                    label = el.GetAttr("value") ?? "Button";
                return label;
            }

            case "select":
            {
                var options = el.Descendants().OfType<DomElement>()
                    .Where(o => o.TagName == "option")
                    .ToList();
                if (options.Count == 0) return string.Empty;

                // A dropdown exposes its selected option; a listbox exposes
                // all visible option labels to page selection/find.
                bool listbox = el.HasAttr("multiple") || el.GetAttrInt("size", 1) > 1;
                if (!listbox)
                {
                    var selected = options.FirstOrDefault(o => o.HasAttr("selected"))
                        ?? options.FirstOrDefault();
                    return selected == null ? string.Empty : GetSelectOptionText(selected);
                }

                return string.Join("\n", options.Select(GetSelectOptionText)
                    .Where(text => text.Length > 0));
            }

            case "input":
            {
                string type = el.GetAttrOrDefault("type", "text").Trim()
                    .ToLowerInvariant();
                if (type is "hidden" or "checkbox" or "radio" or "image")
                    return string.Empty;
                if (type == "password")
                    return string.Empty;
                if (type == "file")
                {
                    string chosen = el.GetAttr("data-file-name") ?? string.Empty;
                    string label = chosen.Length == 0 ? "No file chosen" : chosen;
                    return "Choose File " + label;
                }

                string value = el.GetAttr("value") ?? string.Empty;
                if (value.Length > 0) return value;
                return type switch
                {
                    "submit" => "Submit Query",
                    "reset" => "Reset",
                    "button" => "Button",
                    _ => string.Empty
                };
            }

            default:
                return string.Empty;
        }
    }

    private static int GetSelectionBoxTextLength(LayoutBox box)
    {
        if (IsSelectionEligibleBox(box)) return box.TextRun?.Length ?? 0;
        if (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element))
            return GetSelectableControlText(box.Element!).Length;
        return 0;
    }

    private static bool IsSelectionRangeBox(LayoutBox box)
    {
        if (IsSelectionEligibleBox(box))
            return HasSelectionVisibleCharacters(box.TextRun!, 0, box.TextRun!.Length);
        return box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element) &&
               GetSelectionBoxTextLength(box) > 0;
    }

    private List<LayoutBox> OrderedSelectionRangeBoxes(LayoutBox? root = null)
    {
        var selectionRoot = root ?? _selectionFrame?.RootBox ?? _rootBox;
        if (selectionRoot == null) return new List<LayoutBox>();

        // Range semantics must follow layout/DOM source order, not screen
        // coordinates. Cache the result for this root tree; a real relayout swaps
        // the root reference and naturally invalidates the cache.
        if (ReferenceEquals(_selectionRangeCacheRoot, selectionRoot) &&
            _selectionRangeCache != null)
            return _selectionRangeCache;

        _selectionRangeCacheRoot = selectionRoot;
        _selectionRangeCache = EnumerateSelectionTree(selectionRoot)
            .Where(IsSelectionRangeBox)
            .ToList();
        return _selectionRangeCache;
    }

    private static LayoutBox? FindPageSelectableControlBox(LayoutBox? hitBox)
    {
        for (var box = hitBox; box != null; box = box.Parent)
        {
            if (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element))
                return box;
        }
        return null;
    }

    private int GetControlTextOffset(LayoutBox box, float x, float y)
    {
        var element = box.Element;
        if (!IsPageSelectableControl(element)) return 0;
        var face = IntersectRect(box.ContentRect, box.BorderRect);
        if (face.Width <= 0.01f || face.Height <= 0.01f) return 0;

        string source = GetSelectableControlText(element!);
        if (source.Length == 0) return 0;
        var font = ResolveFieldFont(element!);
        if (font == null) return 0;
        using var format = NewFieldFormat(noWrap: true);

        if (element!.TagName == "select")
        {
            var options = element.Descendants().OfType<DomElement>()
                .Where(o => o.TagName == "option").ToList();
            bool listbox = element.HasAttr("multiple") || element.GetAttrInt("size", 1) > 1;
            if (!listbox)
            {
                var selected = options.FirstOrDefault(o => o.HasAttr("selected")) ?? options.FirstOrDefault();
                if (selected == null) return 0;
                source = GetSelectOptionText(selected);
            }
            else if (options.Count > 0)
            {
                int visibleRows = GetSelectVisibleRows(element);
                int row = Math.Clamp((int)Math.Floor((y - face.Top) / Math.Max(1f, font.GetHeight(_measureGfx ?? MeasureGraphics) + 2f)),
                    0, Math.Max(0, visibleRows - 1));
                int index = Math.Min(options.Count - 1, GetSelectScrollOffset(element) + row);
                int globalStart = 0;
                for (int i = 0; i < index; i++)
                    globalStart += GetSelectOptionText(options[i]).Length + 1;
                string optText = GetSelectOptionText(options[index]);
                float textLeft = face.X + 3f;
                int local = GetMeasuredTextOffset(optText, font, format, x - textLeft);
                return Math.Clamp(globalStart + local, 0, source.Length);
            }
        }

        float visibleWidth = Math.Max(1f, face.Width - 6f);
        float totalWidth = _measureGfx != null
            ? _measureGfx.MeasureString(source, font, int.MaxValue, format).Width
            : MeasureGraphics.MeasureString(source, font, int.MaxValue, format).Width;
        float alignOffset = GetSingleLineFieldAlignmentOffset(visibleWidth, totalWidth,
            element!.TagName == "select" ? TextAlign.Left : GetFieldTextAlign(element));
        float textLeftSingle = face.X + 3f + alignOffset;
        return GetMeasuredTextOffset(source, font, format, x - textLeftSingle);
    }

    private int GetMeasuredTextOffset(string text, Font font, StringFormat format, float target)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        target = Math.Max(0f, target);
        float previous = 0f;
        Graphics g = _measureGfx ?? MeasureGraphics;
        for (int i = 1; i <= text.Length; i++)
        {
            float width = g.MeasureString(text[..i], font, int.MaxValue, format).Width;
            if (target <= width)
                return (target - previous) <= (width - target) ? i - 1 : i;
            previous = width;
        }
        return text.Length;
    }

    private List<LayoutBox> OrderedSelectionBoxes()
    {
        if (_selAnchor == null || _selFocus == null)
            return new List<LayoutBox>();
        return OrderedSelectionRangeBoxes();
    }

    private IEnumerable<LayoutBox> SelectedTextBoxes()
    {
        var textBoxes = OrderedSelectionBoxes();
        if (textBoxes.Count == 0 || _selAnchor == null || _selFocus == null)
            yield break;
        int start = textBoxes.IndexOf(_selAnchor);
        int end = textBoxes.IndexOf(_selFocus);
        if (start < 0 || end < 0) yield break;
        if (start > end) (start, end) = (end, start);
        for (int i = start; i <= end; i++)
            yield return textBoxes[i];
    }

    internal string GetPluginSelectedText() => GetSelectedText();

    private static bool HasLayoutWhitespaceBetween(LayoutBox previous, LayoutBox current)
    {
        if (previous.Parent == null || !ReferenceEquals(previous.Parent, current.Parent))
            return false;
        int a = previous.Parent.Children.IndexOf(previous);
        int b = previous.Parent.Children.IndexOf(current);
        if (a < 0 || b < 0 || a == b) return false;
        if (a > b) (a, b) = (b, a);
        for (int i = a + 1; i < b; i++)
        {
            string? between = previous.Parent.Children[i].TextRun;
            if (!string.IsNullOrEmpty(between) && between.All(char.IsWhiteSpace))
                return true;
        }
        return false;
    }

    private static void AppendSelectionSeparator(StringBuilder sb, LayoutBox previous, LayoutBox current)
    {
        if (sb.Length == 0) return;

        var a = previous.ContentRect;
        var b = current.ContentRect;
        float lineTolerance = Math.Max(2f, Math.Min(a.Height, b.Height) * 0.5f);
        bool newLine = Math.Abs(b.Top - a.Top) > lineTolerance;
        if (newLine)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            return;
        }

        // Word fragmentation stores collapsed HTML spaces as separate layout
        // boxes. Those boxes are not selectable glyphs, so use the actual sibling
        // stream before relying on approximate geometry for clipboard text.
        if ((HasLayoutWhitespaceBetween(previous, current) || b.Left > a.Right + 0.5f) &&
            sb[^1] != ' ' && sb[^1] != '\n')
            sb.Append(' ');
    }

    private static void AppendSelectedBoxText(StringBuilder sb, LayoutBox box, int start, int end,
                                               LayoutBox? previousBox)
    {
        string text = IsSelectionEligibleBox(box)
            ? (box.TextRun ?? string.Empty)
            : (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element)
                ? GetSelectableControlText(box.Element!)
                : string.Empty);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (end <= start) return;

        if (previousBox != null)
            AppendSelectionSeparator(sb, previousBox, box);
        sb.Append(text[start..end]);
    }

    private static string NormalizeCollapsedSelectionText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    internal string GetDomSelectionText() => GetSelectedText();

    private string GetSelectedText()
    {
        if (_selectAllPage)
            return GetSelectAllText();

        // Triple-click is a semantic block selection. Reconstruct the copy text
        // from the DOM scope so collapsed HTML whitespace is restored even when
        // the layout split the paragraph into individual word boxes.
        if (_selectionSemanticScope != null)
        {
            string semanticText = _selectionSemanticScope.InnerText;
            return _selectionSemanticScope.TagName == "pre"
                ? semanticText
                : NormalizeCollapsedSelectionText(semanticText);
        }

        var boxes = OrderedSelectionBoxes();
        if (boxes.Count == 0 || _selAnchor == null || _selFocus == null) return "";
        int ai = boxes.IndexOf(_selAnchor);
        int fi = boxes.IndexOf(_selFocus);
        if (ai < 0 || fi < 0) return "";

        bool forward = ai < fi || (ai == fi && _selAnchorOffset <= _selFocusOffset);
        int startIndex = forward ? ai : fi;
        int endIndex = forward ? fi : ai;
        int startOffset = forward ? _selAnchorOffset : _selFocusOffset;
        int endOffset = forward ? _selFocusOffset : _selAnchorOffset;

        var sb = new System.Text.StringBuilder();
        LayoutBox? previousBox = null;
        for (int i = startIndex; i <= endIndex; i++)
        {
            var box = boxes[i];
            string raw = IsSelectionEligibleBox(box)
                ? (box.TextRun ?? string.Empty)
                : (box.BoxType == BoxType.Replaced && IsPageSelectableControl(box.Element)
                    ? GetSelectableControlText(box.Element!)
                    : string.Empty);
            if (raw.Length == 0) continue;

            int a = 0, b = raw.Length;
            if (i == startIndex) a = Math.Clamp(startOffset, 0, raw.Length);
            if (i == endIndex) b = Math.Clamp(endOffset, a, raw.Length);
            if (b > a)
            {
                AppendSelectedBoxText(sb, box, a, b, previousBox);
                previousBox = box;
            }
        }
        return sb.ToString();
    }

    private void SelectAllText()
    {
        // When an editable field owns keyboard focus, Ctrl+A belongs to that
        // field (handled earlier in HandleFieldKey). Reaching this method means
        // the page itself owns the selection, so select every visual text run
        // plus text-bearing input/textarea controls in the selected document.
        ClearPageSelection();

        FrameView? selectionView = null;
        LayoutBox? selectionRoot = _rootBox;
        if (_focusedFrame != null && TryGetFrameViewForBox(_focusedFrame, out var focusedView) &&
            focusedView != null)
        {
            selectionView = focusedView;
            selectionRoot = focusedView.RootBox;
        }
        if (selectionRoot == null) return;

        var rangeBoxes = OrderedSelectionRangeBoxes(selectionRoot);
        if (rangeBoxes.Count == 0) return;

        _selectionFrame = selectionView;
        _selectAllPage = true;

        // Keep endpoints in the complete visual range model. Replaced controls
        // (input/select/button/textarea) are legitimate selectable boxes even
        // though they do not expose a TextRun.
        _selAnchor = rangeBoxes[0];
        _selFocus = rangeBoxes[^1];
        _selAnchorOffset = 0;
        _selFocusOffset = GetSelectionBoxTextLength(_selFocus);

        Invalidate();
    }

    private static bool IsControlElement(DomElement? element) =>
        element != null && element.TagName is "input" or "select" or "textarea" or "button";

    // ─────────────────────────────────────────────────────────────────────
    // Menus (auto-disposed on close — they used to pile up until the
    // canvas itself was disposed)
    // ─────────────────────────────────────────────────────────────────────

    private void CloseOpenSelectMenu()
    {
        var menu = _openSelectMenu;
        _openSelectMenu = null;
        if (menu == null || menu.IsDisposed) return;
        try { menu.Close(ToolStripDropDownCloseReason.AppClicked); }
        catch { try { menu.Dispose(); } catch { } }
    }

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
        float x = clientPoint.X / EffectiveZoom + PaintScrollX;
        float y = clientPoint.Y / EffectiveZoom + PaintScrollY;
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

        string? targetUrl = _contextElement?.GetAttr("href") ?? _contextElement?.GetAttr("src");
        string? targetText = _contextElement?.InnerText;
        bool isLink = (_contextElement?.TagName == "a" || _contextElement?.TagName == "area") && !string.IsNullOrWhiteSpace(_contextElement?.GetAttr("href"));
        bool isImage = _contextElement?.TagName == "img";
        PluginContextMenuRequested?.Invoke(menu, new Retro96.Plugins.ContextMenuContext(targetUrl, targetText, isLink, isImage));

        TrackMenu(menu);
        menu.Show(this, clientPoint);
    }

    private void DispatchLeftClick(System.Drawing.Point clientPoint)
    {
        if (_rootBox == null || _document == null) return;

        float x = clientPoint.X / EffectiveZoom + PaintScrollX;
        float y = clientPoint.Y / EffectiveZoom + PaintScrollY;
        if (TryHitFrame(x, y, out var hit))
        {
            _focusedFrame = hit.Box;
            var frameJs = hit.View.Interpreter ?? _jsInterpreter;
            // clientX/clientY in an iframe are relative to that frame's viewport,
            // not the outer BrowserCanvas. hit.ViewX/ViewY are the pre-scroll local
            // viewport coordinates produced by frame hit-testing; LocalX/LocalY
            // include the frame document's scroll offset and therefore must NOT be
            // used for the event's client coordinates.
            var frameEvent = frameJs?.CreateMouseEvent("onclick",
                (int)Math.Round(hit.ViewX), (int)Math.Round(hit.ViewY), 1);
            HandleClickInView(hit.View, hit.Box, hit.LocalX, hit.LocalY, frameEvent);
            return;
        }

        var element = HitTestElement(_rootBox, x, y);
        if (element != null)
        {
            var mouseEvent = _jsInterpreter?.CreateMouseEvent("onclick", clientPoint.X, clientPoint.Y, 1);
            HandleElementClick(element, _document, _jsInterpreter, x, y,
                isFrame: false, mouseEvent: mouseEvent);
        }
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

    private static (float Width, float Height) GetFrameContentSize(LayoutBox rootBox,
                                                                      float viewportWidth,
                                                                      float viewportHeight)
    {
        float width = Math.Max(1f, Math.Max(viewportWidth, rootBox.Width));
        float height = Math.Max(1f, Math.Max(viewportHeight, rootBox.Height));

        // The root box may stay constrained to the viewport even when one of
        // its descendants overflows it.  Scan the laid-out descendants for
        // the true document extents instead of assuming rootBox.Width/Height
        // are the complete scrollable surface.
        foreach (var box in rootBox.Descendants())
        {
            if (ReferenceEquals(box, rootBox)) continue;
            width = Math.Max(width, box.X + Math.Max(0f, box.Width));
            height = Math.Max(height, box.Y + Math.Max(0f, box.Height));
        }

        return (width, height);
    }

    private readonly record struct FrameScrollMetrics(
        bool Vertical,
        bool Horizontal,
        float ViewportWidth,
        float ViewportHeight,
        float MaxScrollX,
        float MaxScrollY,
        float VerticalTrackLength,
        float VerticalThumbLength,
        float HorizontalTrackLength,
        float HorizontalThumbLength);

    private static FrameScrollMetrics GetFrameScrollMetrics(LayoutBox frameBox, FrameView view)
    {
        float frameW = Math.Max(1f, frameBox.Width);
        float frameH = Math.Max(1f, frameBox.Height);
        var layoutContent = GetFrameContentSize(view.RootBox, frameW, frameH);
        float contentW = Math.Max(frameW, Math.Max(layoutContent.Width, view.ContentSize.Width));
        float contentH = Math.Max(frameH, Math.Max(layoutContent.Height, view.ContentSize.Height));
        const float bar = 16f;

        bool vertical = false, horizontal = false;
        if (view.ScrollMode == FrameScrollMode.No || !view.ScrollingEnabled)
            return new FrameScrollMetrics(false, false, frameW, frameH, 0, 0, 0, 0, 0, 0);

        // scrolling=yes means that a scrollbar is offered even when the
        // current document fits. Keep the classic browser behaviour by
        // forcing the vertical bar; add the horizontal bar when content
        // really needs it (or the reduced viewport created by the vertical
        // bar makes it necessary).
        if (view.ScrollMode == FrameScrollMode.Yes)
            vertical = true;
        else
            vertical = contentH > frameH + 0.5f;

        horizontal = contentW > frameW + 0.5f;

        // A visible scrollbar shrinks the usable content viewport, which can
        // in turn require the other axis. Iterate to a stable pair.
        for (int i = 0; i < 3; i++)
        {
            float vpW = Math.Max(1f, frameW - (vertical ? bar : 0f));
            float vpH = Math.Max(1f, frameH - (horizontal ? bar : 0f));
            bool newHorizontal = view.ScrollMode == FrameScrollMode.Yes
                ? contentW > vpW + 0.5f
                : horizontal || contentW > vpW + 0.5f;
            bool newVertical = view.ScrollMode == FrameScrollMode.Yes
                ? true
                : vertical || contentH > vpH + 0.5f;
            if (newHorizontal == horizontal && newVertical == vertical) break;
            horizontal = newHorizontal;
            vertical = newVertical;
        }

        float viewportW = Math.Max(1f, frameW - (vertical ? bar : 0f));
        float viewportH = Math.Max(1f, frameH - (horizontal ? bar : 0f));
        float maxX = Math.Max(0f, contentW - viewportW);
        float maxY = Math.Max(0f, contentH - viewportH);

        const float arrow = 16f;
        float vTrack = vertical ? Math.Max(1f, viewportH - arrow * 2f) : 0f;
        float hTrack = horizontal ? Math.Max(1f, viewportW - arrow * 2f) : 0f;
        float vThumb = vertical
            ? Math.Clamp(vTrack * viewportH / Math.Max(viewportH, contentH), 10f, vTrack)
            : 0f;
        float hThumb = horizontal
            ? Math.Clamp(hTrack * viewportW / Math.Max(viewportW, contentW), 10f, hTrack)
            : 0f;

        return new FrameScrollMetrics(vertical, horizontal, viewportW, viewportH,
            maxX, maxY, vTrack, vThumb, hTrack, hThumb);
    }

    private static RectangleF GetFrameVerticalScrollbarRect(RectangleF rect, FrameScrollMetrics metrics)
    {
        const float bar = 16f;
        return new RectangleF(rect.Right - bar, rect.Top, bar,
            rect.Height - (metrics.Horizontal ? bar : 0f));
    }

    private static RectangleF GetFrameHorizontalScrollbarRect(RectangleF rect, FrameScrollMetrics metrics)
    {
        const float bar = 16f;
        return new RectangleF(rect.Left, rect.Bottom - bar,
            rect.Width - (metrics.Vertical ? bar : 0f), bar);
    }

    private static RectangleF GetFrameVerticalThumb(RectangleF track, FrameScrollMetrics metrics, float scroll)
    {
        const float arrow = 16f;
        float travel = Math.Max(1f, metrics.VerticalTrackLength - metrics.VerticalThumbLength);
        float offset = metrics.MaxScrollY <= 0 ? 0f :
            travel * Math.Clamp(scroll / metrics.MaxScrollY, 0f, 1f);
        return new RectangleF(track.X + 2f, track.Top + arrow + offset,
            Math.Max(4f, track.Width - 4f), metrics.VerticalThumbLength);
    }

    private static RectangleF GetFrameHorizontalThumb(RectangleF track, FrameScrollMetrics metrics, float scroll)
    {
        const float arrow = 16f;
        float travel = Math.Max(1f, metrics.HorizontalTrackLength - metrics.HorizontalThumbLength);
        float offset = metrics.MaxScrollX <= 0 ? 0f :
            travel * Math.Clamp(scroll / metrics.MaxScrollX, 0f, 1f);
        return new RectangleF(track.Left + arrow + offset, track.Y + 2f,
            metrics.HorizontalThumbLength, Math.Max(4f, track.Height - 4f));
    }

    private static void PaintFrameScrollButton(Graphics g, RectangleF rect, bool pressed, bool upOrLeft)
    {
        using var face = new SolidBrush(Color.FromArgb(212, 208, 200));
        using var edgeLight = new Pen(Color.FromArgb(255, 255, 255));
        using var edgeDark = new Pen(Color.FromArgb(128, 128, 128));
        g.FillRectangle(face, rect);
        g.DrawRectangle(pressed ? edgeDark : edgeLight, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        if (!pressed)
            g.DrawLine(edgeDark, rect.Left, rect.Bottom - 1, rect.Right - 1, rect.Bottom - 1);

        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;
        PointF[] tri = upOrLeft
            ? (rect.Height >= rect.Width
                ? new[] { new PointF(cx, cy - 4), new PointF(cx - 4, cy + 3), new PointF(cx + 4, cy + 3) }
                : new[] { new PointF(cx - 4, cy), new PointF(cx + 3, cy - 4), new PointF(cx + 3, cy + 4) })
            : (rect.Height >= rect.Width
                ? new[] { new PointF(cx - 4, cy - 3), new PointF(cx + 4, cy - 3), new PointF(cx, cy + 4) }
                : new[] { new PointF(cx - 3, cy - 4), new PointF(cx + 4, cy), new PointF(cx - 3, cy + 4) });
        using var arrow = new SolidBrush(Color.FromArgb(0, 0, 0));
        g.FillPolygon(arrow, tri);
    }

    private static void PaintFrameScrollbar(Graphics g, RectangleF track, bool vertical,
                                            float scroll, float maxScroll, float trackLength,
                                            float thumbLength)
    {
        using var bg = new SolidBrush(Color.FromArgb(212, 208, 200));
        using var border = new Pen(Color.FromArgb(128, 128, 128));
        g.FillRectangle(bg, track);
        g.DrawRectangle(border, track.X, track.Y, track.Width - 1, track.Height - 1);

        const float arrow = 16f;
        if (vertical)
        {
            var top = new RectangleF(track.Left, track.Top, track.Width, arrow);
            var bottom = new RectangleF(track.Left, track.Bottom - arrow, track.Width, arrow);
            PaintFrameScrollButton(g, top, false, true);
            PaintFrameScrollButton(g, bottom, false, false);
            float travel = Math.Max(1f, trackLength - thumbLength);
            float offset = maxScroll <= 0 ? 0f : travel * Math.Clamp(scroll / maxScroll, 0f, 1f);
            var thumb = new RectangleF(track.Left + 2f, track.Top + arrow + offset,
                Math.Max(4f, track.Width - 4f), thumbLength);
            using var thumbFill = new SolidBrush(Color.FromArgb(128, 128, 128));
            using var thumbEdge = new Pen(Color.FromArgb(64, 64, 64));
            g.FillRectangle(thumbFill, thumb);
            g.DrawRectangle(thumbEdge, thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1);
        }
        else
        {
            var left = new RectangleF(track.Left, track.Top, arrow, track.Height);
            var right = new RectangleF(track.Right - arrow, track.Top, arrow, track.Height);
            PaintFrameScrollButton(g, left, false, true);
            PaintFrameScrollButton(g, right, false, false);
            float travel = Math.Max(1f, trackLength - thumbLength);
            float offset = maxScroll <= 0 ? 0f : travel * Math.Clamp(scroll / maxScroll, 0f, 1f);
            var thumb = new RectangleF(track.Left + arrow + offset, track.Top + 2f,
                thumbLength, Math.Max(4f, track.Height - 4f));
            using var thumbFill = new SolidBrush(Color.FromArgb(128, 128, 128));
            using var thumbEdge = new Pen(Color.FromArgb(64, 64, 64));
            g.FillRectangle(thumbFill, thumb);
            g.DrawRectangle(thumbEdge, thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1);
        }
    }

    private void PaintFrameScrollbars(Graphics g, RectangleF destRect, FrameView view, LayoutBox frameBox)
    {
        var metrics = GetFrameScrollMetrics(frameBox, view);
        if (!metrics.Vertical && !metrics.Horizontal) return;

        int state = g.Save();
        g.SetClip(destRect, CombineMode.Intersect);
        if (metrics.Vertical)
        {
            var rect = GetFrameVerticalScrollbarRect(destRect, metrics);
            PaintFrameScrollbar(g, rect, true, view.Scroll.Y, metrics.MaxScrollY,
                metrics.VerticalTrackLength, metrics.VerticalThumbLength);
        }
        if (metrics.Horizontal)
        {
            var rect = GetFrameHorizontalScrollbarRect(destRect, metrics);
            PaintFrameScrollbar(g, rect, false, view.Scroll.X, metrics.MaxScrollX,
                metrics.HorizontalTrackLength, metrics.HorizontalThumbLength);
        }
        if (metrics.Vertical && metrics.Horizontal)
        {
            using var fill = new SolidBrush(Color.FromArgb(212, 208, 200));
            g.FillRectangle(fill, destRect.Right - 16f, destRect.Bottom - 16f, 16f, 16f);
            using var edge = new Pen(Color.FromArgb(128, 128, 128));
            g.DrawRectangle(edge, destRect.Right - 16f, destRect.Bottom - 16f, 15f, 15f);
        }
        g.Restore(state);
    }

    private bool TryBeginFrameScrollbarInteraction(float x, float y)
    {
        if (!TryHitFrame(x, y, out var hit) || !hit.View.ScrollingEnabled)
            return false;

        var metrics = GetFrameScrollMetrics(hit.Box, hit.View);
        var frameRect = new RectangleF(0, 0, hit.Box.Width, hit.Box.Height);
        float localX = hit.ViewX;
        float localY = hit.ViewY;
        if (metrics.Vertical)
        {
            var track = GetFrameVerticalScrollbarRect(frameRect, metrics);
            if (track.Contains(localX, localY))
            {
                const float arrow = 16f;
                var thumb = GetFrameVerticalThumb(track, metrics, hit.View.Scroll.Y);
                if (thumb.Contains(localX, localY))
                {
                    _frameScrollbarDragBox = hit.Box;
                    _frameScrollbarDragView = hit.View;
                    _frameScrollbarDragAxis = FrameScrollbarAxis.Vertical;
                    _frameScrollbarDragStartPointer = y;
                    _frameScrollbarDragStartScroll = hit.View.Scroll.Y;
                    _focusedFrame = hit.Box;
                    return true;
                }
                if (localY < thumb.Top && localY > track.Top + arrow)
                    hit.View.Scroll.Y = Math.Max(0f, hit.View.Scroll.Y - metrics.ViewportHeight);
                else if (localY > thumb.Bottom && localY < track.Bottom - arrow)
                    hit.View.Scroll.Y = Math.Min(metrics.MaxScrollY, hit.View.Scroll.Y + metrics.ViewportHeight);
                else if (localY <= track.Top + arrow)
                    hit.View.Scroll.Y = Math.Max(0f, hit.View.Scroll.Y - 40f);
                else if (localY >= track.Bottom - arrow)
                    hit.View.Scroll.Y = Math.Min(metrics.MaxScrollY, hit.View.Scroll.Y + 40f);
                _focusedFrame = hit.Box;
                RecomposeFrameTree(hit.View);
                return true;
            }
        }

        if (metrics.Horizontal)
        {
            var track = GetFrameHorizontalScrollbarRect(frameRect, metrics);
            if (track.Contains(localX, localY))
            {
                const float arrow = 16f;
                var thumb = GetFrameHorizontalThumb(track, metrics, hit.View.Scroll.X);
                if (thumb.Contains(localX, localY))
                {
                    _frameScrollbarDragBox = hit.Box;
                    _frameScrollbarDragView = hit.View;
                    _frameScrollbarDragAxis = FrameScrollbarAxis.Horizontal;
                    _frameScrollbarDragStartPointer = x;
                    _frameScrollbarDragStartScroll = hit.View.Scroll.X;
                    _focusedFrame = hit.Box;
                    return true;
                }
                if (localX < thumb.Left && localX > track.Left + arrow)
                    hit.View.Scroll.X = Math.Max(0f, hit.View.Scroll.X - metrics.ViewportWidth);
                else if (localX > thumb.Right && localX < track.Right - arrow)
                    hit.View.Scroll.X = Math.Min(metrics.MaxScrollX, hit.View.Scroll.X + metrics.ViewportWidth);
                else if (localX <= track.Left + arrow)
                    hit.View.Scroll.X = Math.Max(0f, hit.View.Scroll.X - 40f);
                else if (localX >= track.Right - arrow)
                    hit.View.Scroll.X = Math.Min(metrics.MaxScrollX, hit.View.Scroll.X + 40f);
                _focusedFrame = hit.Box;
                RecomposeFrameTree(hit.View);
                return true;
            }
        }
        return false;
    }

    private readonly record struct FrameHit(LayoutBox Box, FrameView View, float LocalX, float LocalY, float ViewX, float ViewY);

    private bool TryHitFrame(float x, float y, out FrameHit hit)
    {
        foreach (var (box, view) in _frames)
        {
            if (!box.BorderRect.Contains(x, y)) continue;
            float viewX = x - box.X, viewY = y - box.Y;
            float localX = viewX + view.Scroll.X, localY = viewY + view.Scroll.Y;
            if (TryHitNestedFrame(view, localX, localY, out hit)) return true;
            hit = new FrameHit(box, view, localX, localY, viewX, viewY); return true;
        }
        hit = default; return false;
    }

    private static bool TryHitNestedFrame(FrameView parent, float parentX, float parentY, out FrameHit hit)
    {
        foreach (var (childBox, childView) in parent.ChildFrames)
        {
            if (!childBox.BorderRect.Contains(parentX, parentY)) continue;
            float viewX = parentX - childBox.X, viewY = parentY - childBox.Y;
            float localX = viewX + childView.Scroll.X, localY = viewY + childView.Scroll.Y;
            if (TryHitNestedFrame(childView, localX, localY, out hit)) return true;
            hit = new FrameHit(childBox, childView, localX, localY, viewX, viewY); return true;
        }
        hit = default; return false;
    }

    private LayoutBox? FrameBoxAtPoint(float x, float y) => TryHitFrame(x, y, out var hit) ? hit.Box : null;

    private bool TryFindFrameBox(FrameView view, out LayoutBox box)
    {
        if (TryFindFrameHost(view, out _, out var found) && found != null)
        { box = found; return true; }
        box = null!; return false;
    }

    // Returns the frame view owning a frame layout box, including nested
    // ChildFrames. _frames only contains top-level frame boxes, so direct
    // dictionary lookups are insufficient for selection/focus inside an
    // embedded frame hierarchy.
    private static bool TryGetFrameViewForBox(FrameView current, LayoutBox target,
                                               out FrameView? view)
    {
        foreach (var (childBox, childView) in current.ChildFrames)
        {
            if (ReferenceEquals(childBox, target))
            {
                view = childView;
                return true;
            }
            if (TryGetFrameViewForBox(childView, target, out view))
                return true;
        }
        view = null;
        return false;
    }

    private bool TryGetFrameViewForBox(LayoutBox target, out FrameView? view)
    {
        foreach (var (topBox, topView) in _frames)
        {
            if (ReferenceEquals(topBox, target))
            {
                view = topView;
                return true;
            }
            if (TryGetFrameViewForBox(topView, target, out view))
                return true;
        }
        view = null;
        return false;
    }

    // Computes a frame's actual destination rectangle in top-level canvas
    // coordinates. Child frame boxes are expressed in their parent's document
    // coordinates, so simply using childBox.X/Y produces the wrong offset for
    // nested selections.
    private bool TryGetFrameDestinationRect(FrameView target, out RectangleF rect)
    {
        foreach (var (topBox, topView) in _frames)
        {
            var topRect = new RectangleF(
                topBox.X - _scrollOffset.X,
                topBox.Y - _scrollOffset.Y,
                topBox.Width, topBox.Height);
            if (ReferenceEquals(topView, target))
            {
                rect = topRect;
                return true;
            }
            if (TryGetNestedFrameDestinationRect(topView, target, topRect, out rect))
                return true;
        }
        rect = RectangleF.Empty;
        return false;
    }

    private static bool TryGetNestedFrameDestinationRect(FrameView parent, FrameView target,
                                                          RectangleF parentRect,
                                                          out RectangleF rect)
    {
        foreach (var (childBox, childView) in parent.ChildFrames)
        {
            var childRect = new RectangleF(
                parentRect.X + childBox.X - parent.Scroll.X,
                parentRect.Y + childBox.Y - parent.Scroll.Y,
                childBox.Width, childBox.Height);
            if (ReferenceEquals(childView, target))
            {
                rect = childRect;
                return true;
            }
            if (TryGetNestedFrameDestinationRect(childView, target, childRect, out rect))
                return true;
        }
        rect = RectangleF.Empty;
        return false;
    }

    private bool TryGetFocusedTextareaBox(float x, float y, out LayoutBox box)
    {
        box = null!;
        if (_focusedInput?.TagName != "textarea") return false;
        if (!TryGetEditableFieldAtPoint(x, y, out var field, out var fieldBox, out var frameView, out var frameHit) ||
            !ReferenceEquals(field, _focusedInput) || fieldBox == null)
            return false;
        float localX = frameView == null ? x : frameHit.LocalX;
        float localY = frameView == null ? y : frameHit.LocalY;
        if (!fieldBox.BorderRect.Contains(localX, localY)) return false;
        box = fieldBox;
        return true;
    }

    private bool TryGetFocusedTextareaScrollbarPoint(float x, float y,
                                                      out LayoutBox box,
                                                      out float localX,
                                                      out float localY)
    {
        box = null!;
        localX = localY = 0;
        if (_focusedInput?.TagName != "textarea") return false;

        var frameView = _focusedInputFrame;
        var root = frameView?.RootBox ?? _rootBox;
        if (root == null) return false;

        if (frameView == null)
        {
            localX = x;
            localY = y;
        }
        else
        {
            if (!TryHitFrame(x, y, out var hit) || !ReferenceEquals(hit.View, frameView))
                return false;
            localX = hit.LocalX;
            localY = hit.LocalY;
        }

        // Do not use deepest-box hit testing here: the scrollbar sits on the
        // same visual face as the textarea's text child, so generic hit-testing
        // can report the text node and send the click into text selection.
        var foundBox = FindBoxForElement(root, _focusedInput);
        if (foundBox == null || !foundBox.BorderRect.Contains(localX, localY)) return false;
        box = foundBox;

        var geo = GetTextareaGeometry(_focusedInput, box, frameView);
        if (geo == null || !geo.NeedsVerticalScrollbar) return false;

        var face = box.ContentRect;
        return new RectangleF(face.Right - 14f, face.Top, 14f, face.Height)
            .Contains(localX, localY);
    }

    private static LayoutBox? FindBoxForElement(LayoutBox root, DomElement element) =>
        Engine.Layout.HitTester.BoxForElement(root, element);

    private static DomElement? FindAncestor(DomElement element, string tagName) =>
        Engine.Forms.FormSubmitter.FindAncestor(element, tagName);

    // ─────────────────────────────────────────────────────────────────────
    // Click dispatch
    // ─────────────────────────────────────────────────────────────────────

    private void HandleClickInView(FrameView view, LayoutBox frameBox, float lx, float ly,
                                   JsObject? mouseEvent = null)
    {
        var element = HitTestElement(view.RootBox, lx, ly);
        if (element == null) return;
        // FIX: frame clicks ran on the PAGE interpreter — frame scripts'
        // own handlers (and their onsubmit) never saw them.
        HandleElementClick(element, view.Document, view.Interpreter ?? _jsInterpreter,
            lx, ly, isFrame: true, frameView: view, mouseEvent: mouseEvent);
    }

    private void HandleElementClick(DomElement element, DomDocument document,
                                    JsInterpreter? js, float x, float y,
                                    bool isFrame, FrameView? frameView = null,
                                    JsObject? mouseEvent = null)
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
            if (type == "file")
            {
                var clickResult = js?.FireEvent(element, "onclick", mouseEvent);
                if (clickResult is { Type: JsType.Boolean } && !clickResult.ToBoolean())
                    return;
                ChooseFileForInput(element, js);
                return;
            }
            if (type is "text" or "password")
            {
                FocusControl(element, GetFieldText(element).Length, js);
                js?.FireEvent(element, "onclick", mouseEvent);
                return;
            }
            if (type is "checkbox")
            {
                if (element.HasAttr("checked")) element.SetAttr("checked", null);
                else element.SetAttr("checked", "");
                js?.FireEvent(element, "onclick", mouseEvent);
                RequestRerender();
                return;
            }
            if (type is "radio")
            {
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
                js?.FireEvent(element, "onclick", mouseEvent);
                RequestRerender();
                return;
            }
            if (type is "submit" or "image")
            {
                // Browsers run the click handler first; returning false cancels the submit.
                var clickResult = js?.FireEvent(element, "onclick", mouseEvent);
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
                    SubmitFormInternal(form, document, js, imageClick, frameView, element);
                }
                return;
            }
            if (type is "reset")
            {
                js?.FireEvent(element, "onclick", mouseEvent);
                ResetForm(FindEnclosingForm(element));
                RequestRerender();
                return;
            }
            if (type is "button")
            {
                js?.FireEvent(element, "onclick", mouseEvent);
                return;
            }
            return;
        }

        if (element.TagName == "button")
        {
            if (element.GetAttrOrDefault("type", "submit").Trim().ToLowerInvariant() == "submit")
            {
                var clickResult = js?.FireEvent(element, "onclick", mouseEvent);
                if (clickResult is { Type: JsType.Boolean } && !clickResult.ToBoolean())
                    return;

                var form = FindEnclosingForm(element);
                if (form != null)
                {
                    SubmitFormInternal(form, document, js, null, frameView, element);
                }
                return;
            }
            js?.FireEvent(element, "onclick", mouseEvent);
            return;
        }

        if (element.TagName == "select")
        {
            if (element.HasAttr("multiple") || element.GetAttrInt("size", 1) > 1)
            {
                SelectListBoxOption(element, x, y, js, layoutRoot);
                return;
            }
            ShowSelectDropdown(element, js, frameView);
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
            var clickResult = js?.FireEvent(anchor, "onclick", mouseEvent);
            if (clickResult is { Type: JsType.Boolean } && !clickResult.ToBoolean())
                return;

            string trimmedHref = href.TrimStart();
            if (trimmedHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                try { js?.ExecuteString(trimmedHref[11..]); }
                catch { }
                return;
            }
            if (trimmedHref.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
            {
                if (!BrowserRuntime.VbScriptEnabled) return;
                try
                {
                    string vbSource = trimmedHref[9..];
                    js?.ExecuteString(VbScriptTranslator.Translate(vbSource));
                }
                catch { }
                return;
            }

            // External mail links are absolute by definition and must be
            // handled before requiring a document BaseUrl.  Frame documents
            // can legitimately have a missing/placeholder BaseUrl while still
            // containing <a href="mailto:..."> links; those links should open
            // the host mail handler rather than falling through to generic
            // protocol navigation and an error page.
            if (trimmedHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                if (TryNormalizeExternalMailto(trimmedHref, out string mailtoAbs))
                    ExternalProtocolRequested?.Invoke(mailtoAbs);
                return;
            }

            if (document.BaseUrl != null)
            {
                string abs = document.BaseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? Engine.Network.FileUrls.Resolve(document.BaseUrl, href)
                    : document.BaseUrl.Resolve(href).ToAbsolute();

                if (abs.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    ExternalProtocolRequested?.Invoke(abs);
                    return;
                }

                string? target = anchor.GetAttr("target");
                if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(document.BaseTarget))
                    target = document.BaseTarget;

                // A fragment pointing back into the CURRENT frame document is
                // same-document navigation.  It must scroll the existing frame
                // view rather than route through LoadFrameAsync and reload the
                // frame.  This is especially important for old 1996 table-based
                // nav links such as <a href="#order">Order</a>.
                if (isFrame && frameView != null &&
                    (string.IsNullOrEmpty(target) || target == "_self") &&
                    IsSameDocumentUrl(abs, document.BaseUrl.ToAbsolute()))
                {
                    string fragment = ParsedUrl.Parse(abs).Fragment;
                    if (!string.IsNullOrEmpty(fragment))
                    {
                        ScrollFrameToAnchor(frameView, fragment);
                        return;
                    }
                }

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
                string abs = document.BaseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? Engine.Network.FileUrls.Resolve(document.BaseUrl, href)
                    : document.BaseUrl.Resolve(href).ToAbsolute();
                string sep = abs.Contains('?') ? "&" : "?";
                NavigateRequested?.Invoke($"{abs}{sep}{(int)relX},{(int)relY}");
                return;
            }
        }

        js?.FireEvent(element, "onclick", mouseEvent);
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
        int visibleRows = GetSelectVisibleRows(select);
        int scrollOffset = GetSelectScrollOffset(select);
        int index = scrollOffset + (int)Math.Floor((y - box.ContentRect.Y) / rowHeight);
        if (index < scrollOffset || index >= Math.Min(scrollOffset + visibleRows, options.Count)) return;

        var captured = options[index];
        bool toggle = (ModifierKeys & Keys.Control) == Keys.Control;
        int anchorIndex = 0;
        bool range = (ModifierKeys & Keys.Shift) == Keys.Shift &&
                     select.HasAttr("multiple") &&
                     _selectRangeAnchors.TryGetValue(select, out anchorIndex);

        if (range)
        {
            int lo = Math.Min(anchorIndex, index);
            int hi = Math.Max(anchorIndex, index);
            foreach (var option in options)
                option.SetAttr("selected", null);
            for (int i = lo; i <= hi; i++)
                options[i].SetAttr("selected", "");
        }
        else
        {
            if (!toggle)
                foreach (var option in options)
                    option.SetAttr("selected", null);

            if (toggle && captured.HasAttr("selected"))
                captured.SetAttr("selected", null);
            else
                captured.SetAttr("selected", "");
        }

        _selectRangeAnchors[select] = index;
        js?.FireEvent(select, "change");
        RequestRerender();
    }

    private static bool IsSameDocumentUrl(string left, string right)
    {
        try
        {
            var a = ParsedUrl.Parse(left);
            var b = ParsedUrl.Parse(right);
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port &&
                   string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Query, b.Query, StringComparison.Ordinal);
        }
        catch
        {
            int ah = left.IndexOf('#');
            int bh = right.IndexOf('#');
            return string.Equals(ah >= 0 ? left[..ah] : left,
                                 bh >= 0 ? right[..bh] : right,
                                 StringComparison.OrdinalIgnoreCase);
        }
    }

    public void ScrollFrameToAnchor(FrameView frame, string anchorName)
    {
        if (frame == null || frame.RootBox == null || frame.Document == null ||
            string.IsNullOrEmpty(anchorName))
            return;

        var target = frame.Document.ElementDescendants()
            .FirstOrDefault(e =>
                (e.TagName == "a" &&
                 (e.GetAttr("name") ?? "").Equals(anchorName, StringComparison.OrdinalIgnoreCase)) ||
                (e.GetAttr("id") ?? "").Equals(anchorName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;

        // Legacy named anchors are often empty (<a name="order"></a>) and
        // therefore have no layout box. Resolve the anchor to the first real
        // laid-out node that follows it rather than the wrapper's start.
        var box = FindAnchorScrollBox(frame.RootBox, target);
        if (box == null) return;

        LayoutBox? frameBox = null;
        foreach (var (candidateBox, candidateView) in _frames)
        {
            if (ReferenceEquals(candidateView, frame))
            {
                frameBox = candidateBox;
                break;
            }

            if (TryFindChildFrameBox(candidateView, frame, out var childBox))
            {
                frameBox = childBox;
                break;
            }
        }

        // A nested frame can be reachable through a parent's ChildFrames tree
        // even when it is not represented in the top-level _frames dictionary.
        if (frameBox == null)
        {
            foreach (var (_, candidateView) in _frames)
            {
                if (TryFindChildFrameBox(candidateView, frame, out var childBox))
                {
                    frameBox = childBox;
                    break;
                }
            }
        }

        float viewportHeight = frameBox?.Height ?? 0f;
        float contentHeight = Math.Max(frame.ContentSize.Height, frame.RootBox.Height);
        float maxY = Math.Max(0f, contentHeight - viewportHeight);
        frame.Scroll.Y = Math.Clamp(box.Y - 10f, 0f, maxY);
        CloseMenusOnScroll();
        _focusedFrame = frameBox;
        if (frameBox != null)
            UpdateFrameMetrics(frameBox, frame);
        Invalidate();
    }

    private static bool TryFindChildFrameBox(FrameView parent, FrameView target,
                                              out LayoutBox? result)
    {
        foreach (var (childBox, childView) in parent.ChildFrames)
        {
            if (ReferenceEquals(childView, target))
            {
                result = childBox;
                return true;
            }

            if (TryFindChildFrameBox(childView, target, out result))
                return true;
        }

        result = null;
        return false;
    }

    private static LayoutBox? FindAnchorScrollBox(LayoutBox root, DomElement target)
    {
        var direct = FindBoxForElement(root, target);
        if (direct != null && (direct.Width > 0f || direct.Height > 0f))
            return direct;

        // Walk forward in document order. This intentionally skips the empty
        // anchor itself and any other zero-sized legacy anchors until it reaches
        // the first element that actually participates in layout.
        DomNode? cursor = target;
        while (cursor != null)
        {
            for (var sibling = cursor.NextSibling; sibling != null;
                 sibling = sibling.NextSibling)
            {
                var candidate = FindFirstLayoutBox(root, sibling);
                if (candidate != null)
                    return candidate;
            }

            // If the anchor is the final child of a wrapper, continue with the
            // wrapper's following siblings instead of using the wrapper itself,
            // whose box begins before the anchor.
            cursor = cursor.Parent;
        }

        return null;
    }

    private static LayoutBox? FindFirstLayoutBox(LayoutBox root, DomNode node)
    {
        if (node is DomElement element)
        {
            var box = FindBoxForElement(root, element);
            if (box != null && (box.Width > 0f || box.Height > 0f))
                return box;

            foreach (var descendant in element.ElementDescendants())
            {
                box = FindBoxForElement(root, descendant);
                if (box != null && (box.Width > 0f || box.Height > 0f))
                    return box;
            }
        }

        return null;
    }

    private static FrameView? FindNamedFrameRecursive(FrameView current, string target)
    {
        if (string.Equals(current.Name, target, StringComparison.OrdinalIgnoreCase))
            return current;
        foreach (var (_, child) in current.ChildFrames)
        {
            var found = FindNamedFrameRecursive(child, target);
            if (found != null) return found;
        }
        return null;
    }

    private static FrameView? FindParentOfFrameRecursive(FrameView current, FrameView target)
    {
        foreach (var (_, child) in current.ChildFrames)
        {
            if (ReferenceEquals(child, target)) return current;
            var found = FindParentOfFrameRecursive(child, target);
            if (found != null) return found;
        }
        return null;
    }

    private FrameView? FindNamedFrameForNavigation(FrameView? sourceFrame, string target)
    {
        target = target.Trim();
        if (target.Length == 0) return null;

        // A named target inside a nested frameset resolves to the nearest
        // frameset context first.  Without this, a nested frame named "main"
        // could accidentally resolve to the OUTER frames.html main frame,
        // breaking links on pages such as 1996-corporate.html.
        if (sourceFrame != null)
        {
            foreach (var (_, child) in sourceFrame.ChildFrames)
            {
                var found = FindNamedFrameRecursive(child, target);
                if (found != null) return found;
            }

            FrameView? parent = null;
            foreach (var (_, top) in _frames)
            {
                if (ReferenceEquals(top, sourceFrame)) break;
                parent = FindParentOfFrameRecursive(top, sourceFrame);
                if (parent != null) break;
            }
            if (parent != null)
            {
                foreach (var (_, sibling) in parent.ChildFrames)
                {
                    if (ReferenceEquals(sibling, sourceFrame)) continue;
                    var found = FindNamedFrameRecursive(sibling, target);
                    if (found != null) return found;
                }
                var inParent = FindNamedFrameRecursive(parent, target);
                if (inParent != null && !ReferenceEquals(inParent, sourceFrame)) return inParent;
            }
        }

        foreach (var (_, view) in _frames)
        {
            var found = FindNamedFrameRecursive(view, target);
            if (found != null) return found;
        }
        return null;
    }

    public FrameView? FindFrameByName(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        foreach (var (_, view) in _frames)
        {
            var found = FindNamedFrameRecursive(view, target.Trim());
            if (found != null) return found;
        }
        return null;
    }

    private static bool TryNormalizeExternalMailto(string href, out string absolute)
    {
        absolute = "";
        string s = href.Trim();
        if (!s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return false;

        // Keep the URI opaque; only reject control characters that cannot be
        // part of a valid mailto URI. The host owns the actual launch.
        foreach (char c in s)
            if (c < ' ' || c == '\x7F')
                return false;

        absolute = "mailto:" + s[7..];
        return true;
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

        if (target == "_top")
        {
            NavigateRequested?.Invoke(absUrl);
            return;
        }

        if (target == "_parent")
        {
            if (sourceFrame != null &&
                TryFindFrameHost(sourceFrame, out var parentView, out _) &&
                parentView != null)
            {
                FrameNavigationRequested?.Invoke((this, parentView, absUrl));
            }
            else
            {
                NavigateRequested?.Invoke(absUrl);
            }
            return;
        }

        if (target == "_blank")
        {
            OpenNewWindow(absUrl);
            return;
        }

        FrameView? named = FindNamedFrameForNavigation(sourceFrame, target);
        if (named != null)
        {
            FrameNavigationRequested?.Invoke((this, named, absUrl));
            return;
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
            .Where(c => IsEditableField(c) &&
                        !c.HasAttr("disabled"))
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

    private void ShowSelectDropdown(DomElement select, JsInterpreter? js, FrameView? frameView = null)
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

        CloseOpenSelectMenu();
        var menu = new ContextMenuStrip
        {
            // Keep the popup open while the pointer merely leaves its bounds,
            // while restoring normal dismissal for a committed item click or
            // a click elsewhere on the page.  AutoClose=true does exactly that
            // without making simple pointer travel outside the popup dismiss it.
            AutoClose = true
        };
        _openSelectMenu = menu;
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
                    menu.Close(ToolStripDropDownCloseReason.ItemClicked);
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

        var sharedBold = bold;
        menu.Disposed += (s, e) =>
        {
            if (ReferenceEquals(_openSelectMenu, menu))
                _openSelectMenu = null;
            sharedBold?.Dispose();
        };

        // ContextMenuStrip uses PHYSICAL WinForms client coordinates, while
        // layout boxes use logical CSS coordinates. Always convert through the
        // current effective zoom. For a frame, first resolve the frame viewport
        // origin through all ancestor frame scroll offsets, then add the select's
        // local position. The old code subtracted top-level scroll twice and did
        // not scale by zoom, so the popup drifted after either zoom path.
        System.Drawing.Point pt;
        float zoom = Math.Max(0.25f, EffectiveZoom);
        if (frameView != null && TryGetFrameClientOriginLogical(frameView, out var frameClientX, out var frameClientY))
        {
            var frameBox = frameView.RootBox != null ? FindBoxForElement(frameView.RootBox, select) : null;
            if (frameBox != null)
            {
                var border = frameBox.BorderRect;
                float logicalX = frameClientX + border.X - frameView.Scroll.X;
                float logicalY = frameClientY + border.Bottom - frameView.Scroll.Y;
                pt = new System.Drawing.Point(
                    (int)Math.Round(logicalX * zoom),
                    (int)Math.Round(logicalY * zoom));
            }
            else
            {
                pt = PointToClient(MousePosition);
            }
        }
        else
        {
            var box = _rootBox != null ? FindBoxForElement(_rootBox, select) : null;
            if (box != null)
            {
                var border = box.BorderRect;
                pt = new System.Drawing.Point(
                    (int)Math.Round((border.X - PaintScrollX) * zoom),
                    (int)Math.Round((border.Bottom - PaintScrollY) * zoom));
            }
            else
            {
                pt = PointToClient(MousePosition);
            }
        }
        pt.X = Math.Clamp(pt.X, 0, Math.Max(0, ClientSize.Width - 1));
        pt.Y = Math.Clamp(pt.Y, 0, Math.Max(0, ClientSize.Height - 1));

        TrackMenu(menu);
        menu.Show(this, pt);
    }

    private bool TryGetFrameRelativeClientPoint(FrameView target, float pageX, float pageY,
                                                 out int clientX, out int clientY)
    {
        if (TryGetFrameClientOriginLogical(target, out var originX, out var originY))
        {
            clientX = (int)Math.Round(pageX - originX);
            clientY = (int)Math.Round(pageY - originY);
            return true;
        }

        clientX = clientY = 0;
        return false;
    }

    private bool TryGetFrameClientOriginLogical(FrameView target, out float logicalX, out float logicalY)
    {
        foreach (var (box, view) in _frames)
        {
            float rootX = box.X - PaintScrollX;
            float rootY = box.Y - PaintScrollY;
            if (ReferenceEquals(view, target))
            {
                logicalX = rootX;
                logicalY = rootY;
                return true;
            }

            if (TryGetNestedFrameClientOriginLogical(view, rootX, rootY, target,
                                                     out logicalX, out logicalY))
                return true;
        }

        logicalX = logicalY = 0f;
        return false;
    }

    private static bool TryGetNestedFrameClientOriginLogical(FrameView parent,
                                                               float parentClientX,
                                                               float parentClientY,
                                                               FrameView target,
                                                               out float clientX,
                                                               out float clientY)
    {
        foreach (var (childBox, childView) in parent.ChildFrames)
        {
            float childX = parentClientX + childBox.X - parent.Scroll.X;
            float childY = parentClientY + childBox.Y - parent.Scroll.Y;
            if (ReferenceEquals(childView, target))
            {
                clientX = childX;
                clientY = childY;
                return true;
            }

            if (TryGetNestedFrameClientOriginLogical(childView, childX, childY, target,
                                                     out clientX, out clientY))
                return true;
        }

        clientX = clientY = 0f;
        return false;
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

    private void ChooseFileForInput(DomElement input, JsInterpreter? js)
    {
        if (input.HasAttr("disabled")) return;

        using var dialog = new OpenFileDialog
        {
            Title = "Choose File",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        var owner = FindForm();
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return;

        string fullPath = Path.GetFullPath(dialog.FileName);
        Engine.Forms.FormSubmitter.SetFileSelection(input, fullPath);
        // Only expose the browser-visible filename, never the native absolute path.
        input.SetAttr("data-file-name", Path.GetFileName(fullPath));
        input.SetAttr("data-file-path", null);
        js?.FireEvent(input, "onchange");
        RequestRerender();
    }

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
                    if (field.GetAttrOrDefault("type", "text").Trim().Equals("file", StringComparison.OrdinalIgnoreCase))
                    {
                        Engine.Forms.FormSubmitter.ClearFileSelection(field);
                        field.SetAttr("data-file-name", null);
                        field.SetAttr("data-file-path", null);
                    }
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
                                    FrameView? sourceFrame, DomElement? submitter = null,
                                    bool dispatchSubmitEvent = true)
    {
        // User-initiated submission (submit button / Enter) fires onsubmit.
        // Programmatic HTMLFormElement.submit() is different: it performs the
        // submission directly and deliberately bypasses the form's submit event.
        // Treating both paths identically made `form.submit()` unexpectedly
        // fire onsubmit — and pages that use submit() from inside onsubmit could
        // re-enter the handler or hit the same `return false` guard again.
        if (dispatchSubmitEvent)
        {
            // Only an explicit `return false` cancels the submit. FireEvent
            // returns Undefined when the form has no handler; that must NOT
            // cancel the default submit.
            var result = js?.FireEvent(form, "onsubmit");
            if (result is { Type: JsType.Boolean } && !result.ToBoolean()) return;
        }

        var req = Engine.Forms.FormSubmitter.BuildRequest(
            form, document.BaseUrl, document.BaseTarget, imageClick, submitter);

        if (req.Method == "post")
        {
            FormSubmitRequested?.Invoke((req.Url, req.QueryString, req.Target, sourceFrame,
                req.MultipartFields, req.MultipartFiles));
        }
        else
        {
            string url = req.Url;
            if (!string.IsNullOrEmpty(req.QueryString))
            {
                int hash = url.IndexOf('#');
                string fragment = hash >= 0 ? url[hash..] : "";
                string withoutFragment = hash >= 0 ? url[..hash] : url;
                url = withoutFragment + (withoutFragment.Contains('?') ? "&" : "?") + req.QueryString + fragment;
            }
            NavigateWithTarget(url, req.Target, sourceFrame);
        }
    }

    public event Action<(string Url, string Body, string? Target, FrameView? Frame,
        IReadOnlyList<Engine.Forms.MultipartField>? MultipartFields,
        IReadOnlyList<Engine.Forms.MultipartFile>? MultipartFiles)>? FormSubmitRequested;

    public void SubmitForm(DomElement? form, object? clickCoords, bool dispatchSubmitEvent = true)
    {
        if (form == null || _document == null) return;
        (string Name, int X, int Y)? coords = null;
        if (clickCoords is ValueTuple<string,int,int> tuple) coords = tuple;
        SubmitFormInternal(form, _document, _jsInterpreter, coords, null,
            dispatchSubmitEvent: dispatchSubmitEvent);
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
        string trimmedHref = href.TrimStart();
        if (trimmedHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            try { _jsInterpreter?.ExecuteString(trimmedHref[11..]); }
            catch { }
            return;
        }
        if (trimmedHref.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
        {
            if (!BrowserRuntime.VbScriptEnabled) return;
            try { _jsInterpreter?.ExecuteString(VbScriptTranslator.Translate(trimmedHref[9..])); }
            catch { }
            return;
        }

        if (document.BaseUrl != null)
        {
            try
            {
                string abs = document.BaseUrl.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? Engine.Network.FileUrls.Resolve(document.BaseUrl, href)
                    : document.BaseUrl.Resolve(href).ToAbsolute();
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

        // <blink> is not limited to the top-level document.  Frame and iframe
        // documents have their own DOMs and are rendered from the same shell
        // timer, so the timer must be started when a nested document contains
        // a blink element too.  Previously frames.html could contain a blink
        // page while the outer frameset had no <blink>, causing the shared
        // timer to stop and the frame content to remain permanently visible.
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

        if (!hasBlink)
        {
            foreach (var (_, frame) in _frames)
            {
                ScanAnimationTags(frame, ref hasBlink, ref _hasMarquee);
                if (hasBlink && _hasMarquee) break;
            }
        }
        _hasAnimatedImages = _imageCache?.HasAnimatedImages ?? false;

        bool needsAnimatedImages = BrowserRuntime.AnimatedImagesEnabled && _hasAnimatedImages;
        bool needsMarquee = BrowserRuntime.MarqueeEnabled && _hasMarquee;
        bool needsBlinkFrames = BrowserRuntime.BlinkEnabled && hasBlink;
        bool needsGpuAnimation = needsMarquee || needsBlinkFrames || needsAnimatedImages;
        if (needsGpuAnimation) UpdateAnimationRefreshRate();
        if (needsGpuAnimation) { if (!_animationTimer.Enabled) _animationTimer.Start(); }
        else if (_animationTimer.Enabled) _animationTimer.Stop();

        if (!BrowserRuntime.BlinkEnabled)
            _blinkVisible = true;

        if (BrowserRuntime.BlinkEnabled && hasBlink)
        {
            if (!_blinkTimer.Enabled) _blinkTimer.Start();
        }
        else if (_blinkTimer.Enabled) _blinkTimer.Stop();

        // PERF: the JS timer used to run forever at 20 Hz even with no
        // interpreter at all.  It now stops when there's nothing to tick.
        bool needsJsTick = BrowserRuntime.JavaScriptTimersEnabled &&
            (_jsInterpreter != null || _frames.Values.Any(v => v.Interpreter != null));
        if (needsJsTick) { if (!_jsTimer.Enabled) _jsTimer.Start(); }
        else if (_jsTimer.Enabled) _jsTimer.Stop();
    }

    private static void ScanAnimationTags(FrameView view, ref bool hasBlink, ref bool hasMarquee)
    {
        foreach (var e in view.Document.ElementDescendants())
        {
            var tag = e.TagName;
            if (tag == "blink") hasBlink = true;
            else if (tag == "marquee") hasMarquee = true;
            if (hasBlink && hasMarquee) break;
        }

        if (hasBlink && hasMarquee) return;

        foreach (var (_, child) in view.ChildFrames)
        {
            ScanAnimationTags(child, ref hasBlink, ref hasMarquee);
            if (hasBlink && hasMarquee) return;
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        bool needsMarquee = BrowserRuntime.MarqueeEnabled && _hasMarquee;
        bool needsBlink = BrowserRuntime.BlinkEnabled && HasEnabledBlinkContent();
        bool needsImages = BrowserRuntime.AnimatedImagesEnabled && _hasAnimatedImages;
        if (!(needsMarquee || needsBlink || needsImages))
            return;

        // Follow the refresh cadence of the monitor hosting the canvas for
        // marquee, blink repaint work, and animated GIF sampling. The
        // timer itself is not VSync; SKGLControl's presentation handles that.
        // Stopwatch pacing compensates for the integer WinForms timer interval
        // so 120/144/165 Hz displays do not accumulate long-term drift.
        string currentDisplay = string.Empty;
        try { currentDisplay = Screen.FromControl(this).DeviceName; } catch { }
        if (!string.Equals(currentDisplay, _animationDisplayDevice, StringComparison.Ordinal))
            UpdateAnimationRefreshRate();

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_animationNextDueTicks != 0 && now < _animationNextDueTicks)
            return;

        double periodTicks = _animationFramePeriodMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;
        long stepTicks = Math.Max(1L, (long)Math.Round(periodTicks));
        if (_animationNextDueTicks == 0)
            _animationNextDueTicks = now + stepTicks;
        else
        {
            _animationNextDueTicks += stepTicks;
            // A blocked UI thread must not cause a burst of catch-up paints.
            if (_animationNextDueTicks < now)
                _animationNextDueTicks = now + stepTicks;
        }

        Invalidate();
    }

    private bool HasEnabledBlinkContent()
    {
        if (!BrowserRuntime.BlinkEnabled) return false;
        if (_document != null && _document.ElementDescendants().Any(e => e.TagName == "blink"))
            return true;
        foreach (var (_, frame) in _frames)
        {
            if (ContainsBlink(frame)) return true;
        }
        return false;
    }

    private static bool ContainsBlink(FrameView view)
    {
        if (view.Document.ElementDescendants().Any(e => e.TagName == "blink"))
            return true;
        return view.ChildFrames.Any(pair => ContainsBlink(pair.View));
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        if (!BrowserRuntime.BlinkEnabled)
        {
            _blinkVisible = true;
            _blinkTimer.Stop();
            return;
        }
        _blinkVisible = !_blinkVisible;
        // The cached page picture intentionally excludes blink subtrees.
        // Only the next GPU composition needs to run.
        Invalidate();
    }

    // The text caret is drawn in OnPaintSurface from Environment.TickCount, so making
    // it blink needs a cheap repaint of just that spot — never a page re-render.
    private readonly Timer _caretTimer = new() { Interval = 500 };
    private void OnCaretTick(object? sender, EventArgs e)
    {
        // The caret timer only applies to editable fields.
        if (_focusedInput == null || _rootBox == null || !IsEditableField(_focusedInput))
            return;
        var box = FindBoxForElement(_rootBox, _focusedInput);
        if (box == null) return;
        var br = box.BorderRect;
        var r = new System.Drawing.Rectangle(
            (int)Math.Ceiling((br.X - _scrollOffset.X) * EffectiveZoom),
            (int)Math.Ceiling((br.Y - _scrollOffset.Y) * EffectiveZoom),
            Math.Max(1, (int)Math.Ceiling(br.Width * EffectiveZoom)),
            Math.Max(1, (int)Math.Ceiling(br.Height * EffectiveZoom)));
        r.Inflate(2, 2);
        Invalidate(r);
    }

    /// <summary>Disposes a frame view and its nested child views.</summary>
    private static void DisposeFrameView(FrameView view)
    {
        foreach (var (_, child) in view.ChildFrames)
            DisposeFrameView(child);
        view.ChildFrames.Clear();
    }

    private static void CollectFrameViews(FrameView view, List<FrameView> result)
    {
        result.Add(view);
        foreach (var (_, child) in view.ChildFrames)
            CollectFrameViews(child, result);
    }

    private void OnJsTimerTick(object? sender, EventArgs e)
    {
        if (!BrowserRuntime.JavaScriptTimersEnabled)
        {
            _jsTimer.Stop();
            return;
        }

        _jsInterpreter?.TickTimers();

        // Frame scripts run on their own interpreters — tick those too, or
        // a setTimeout loop inside an iframe (a clock widget) never fires.
        // A timer callback can navigate/rebuild the frame tree, which mutates
        // _frames while this callback is running. Snapshot the views first so
        // that navigation from a timer cannot invalidate the dictionary
        // enumerator and crash the WinForms UI timer.
        var frameViews = new List<FrameView>();
        foreach (var view in _frames.Values)
            CollectFrameViews(view, frameViews);
        foreach (var view in frameViews)
            view.Interpreter?.TickTimers();

        var statusJs = _lastHoveredFrame?.Interpreter ?? _jsInterpreter;
        var status = statusJs?.WindowObject?.Get("status");
        if (status?.Type == JsType.String && status.GetString().Length > 0)
            SetStatus(status.GetString());
    }

    private static float NextZoomStep(float current, int direction)
    {
        float[] steps =
        { 0.25f, 0.33f, 0.50f, 0.67f, 0.75f, 0.80f, 0.90f, 1.00f,
          1.10f, 1.25f, 1.50f, 1.75f, 2.00f, 2.50f, 3.00f, 4.00f };
        if (direction > 0)
        {
            foreach (float step in steps)
                if (step > current + 0.0005f) return step;
            return 4f;
        }
        for (int i = steps.Length - 1; i >= 0; i--)
            if (steps[i] < current - 0.0005f) return steps[i];
        return 0.25f;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public float ZoomFactor
    {
        get => _layoutZoom;
        set => SetZoom(value, null);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public float VisualZoomFactor => EffectiveZoom;

    /// <summary>Move the shared discrete zoom ladder by one step for toolbar,
    /// menu, keyboard, or other control-driven zoom.  Control zoom is intentionally
    /// independent from the pointer-gesture zoom layer: it reads and changes only
    /// the committed layout zoom.  Starting a control zoom therefore does not inherit
    /// a temporary Ctrl+wheel scale or pan.
    /// </summary>
    public bool ChangeZoomStep(int direction, System.Drawing.Point? anchorClient = null)
    {
        if (direction == 0) return false;

        int sign = Math.Sign(direction);
        float current = Math.Max(0.25f, _layoutZoom);
        float target = NextZoomStep(current, sign);
        if (Math.Abs(target - current) <= 0.0005f) return false;

        SetZoom(target, anchorClient);
        return Math.Abs(_layoutZoom - target) <= 0.001f && !IsGestureZoomActive;
    }

    private static float DistanceSquaredToRect(float x, float y, RectangleF rect)
    {
        float dx = x < rect.Left ? rect.Left - x : x > rect.Right ? x - rect.Right : 0f;
        float dy = y < rect.Top ? rect.Top - y : y > rect.Bottom ? y - rect.Bottom : 0f;
        return dx * dx + dy * dy;
    }

    /// <summary>Toolbar/menu/plugin zoom: change the CSS viewport, rebuild the
    /// layout, and rasterize directly at the resulting scale.</summary>
    public void SetZoom(float value, System.Drawing.Point? anchorClient)
    {
        float next = Math.Clamp(float.IsFinite(value) ? value : 1f, 0.25f, 4f);
        float previousLayout = _layoutZoom;
        float previousGesture = _gestureZoom;
        float previousGesturePanX = _gesturePanX;
        float previousGesturePanY = _gesturePanY;

        // This method is the committed/control zoom path.  A Ctrl+wheel gesture
        // lives in its own visual transform and must never become the input value
        // for a button/menu zoom.  Clear that temporary layer before calculating
        // the control anchor so the control operation is based strictly on the
        // committed layout zoom.
        bool hadGestureZoom = Math.Abs(previousGesture - 1f) >= 0.0005f ||
                              Math.Abs(previousGesturePanX) >= 0.0005f ||
                              Math.Abs(previousGesturePanY) >= 0.0005f;
        _gestureZoom = 1f;
        _gesturePanX = 0f;
        _gesturePanY = 0f;
        _lastGesturePoint = System.Drawing.Point.Empty;

        float previousEffective = Math.Max(0.25f, previousLayout);
        if (Math.Abs(next - previousLayout) < 0.0005f)
        {
            // A programmatic/control request for the already-committed zoom is
            // never allowed to turn the temporary gesture into a committed zoom.
            // If a gesture is active, simply cancel its visual layer and repaint
            // at the unchanged control zoom; do not perform a needless reflow.
            if (hadGestureZoom)
            {
                _resizeReflowTimer.Stop();
                RequestRerender();
            }
            return;
        }

        // Cancel any deferred scrollbar/resize reflow before zooming. Otherwise
        // that stale callback can run when the next mouse move pumps the UI queue
        // and restore the old layout, causing the post-zoom “tweak”.
        _resizeReflowTimer.Stop();

        var physicalBefore = GetViewportSize();
        float anchorX = anchorClient?.X ?? physicalBefore.Width / 2f;
        float anchorY = anchorClient?.Y ?? physicalBefore.Height / 2f;
        anchorX = Math.Clamp(anchorX, 0, Math.Max(0, physicalBefore.Width - 1));
        anchorY = Math.Clamp(anchorY, 0, Math.Max(0, physicalBefore.Height - 1));
        float docX = PaintScrollX + anchorX / Math.Max(0.0001f, previousEffective);
        float docY = PaintScrollY + anchorY / Math.Max(0.0001f, previousEffective);
        if (!float.IsFinite(docX)) docX = _scrollOffset.X;
        if (!float.IsFinite(docY)) docY = _scrollOffset.Y;

        // Centered legacy tables can change their document-space X/Y when the
        // CSS viewport changes. Remember the actual element under the cursor and
        // its fractional point so reflow can preserve that point, not just the
        // old raw document coordinate.
        DomElement? anchorElement = null;
        float anchorFracX = 0f, anchorFracY = 0f;
        if (anchorClient.HasValue && _rootBox != null)
        {
            var anchorHit = HitTestDeepestBox(_rootBox, docX, docY);
            LayoutBox? anchorBox = anchorHit;
            while (anchorBox != null && anchorBox.Element == null)
                anchorBox = anchorBox.Parent;
            if (anchorBox?.Element is DomElement ae)
            {
                var ar = anchorBox.ContentRect;
                anchorElement = ae;
                anchorFracX = ar.Width > 0.01f ? Math.Clamp((docX - ar.X) / ar.Width, 0f, 1f) : 0f;
                anchorFracY = ar.Height > 0.01f ? Math.Clamp((docY - ar.Y) / ar.Height, 0f, 1f) : 0f;
            }
        }

        // Control/menu zoom owns only the committed layout scale.  Gesture zoom
        // was cleared above and is never folded into this value.
        _layoutZoom = next;
        _gestureZoom = 1f;
        _gesturePanX = 0f;
        _gesturePanY = 0f;
        _lastGesturePoint = System.Drawing.Point.Empty;
        _skipHoverRelayoutOnceAfterZoom = false;
        if (_document == null || _rootBox == null || _fontCache == null || _imageCache == null || _resourceLoader == null)
        {
            UpdateScrollBars(); RequestRerender(); ZoomChanged?.Invoke(EffectiveZoom); return;
        }

        try
        {
            if (!ReflowDocumentToStableViewport())
                throw new InvalidOperationException("Unable to rebuild the page at the requested zoom.");

            float anchoredDocX = docX;
            float anchoredDocY = docY;
            if (anchorElement != null && _rootBox != null)
            {
                var candidate = EnumerateSelectionTree(_rootBox)
                    .Where(b => ReferenceEquals(b.Element, anchorElement) &&
                                b.Width > 0.01f && b.Height > 0.01f)
                    .OrderBy(b => DistanceSquaredToRect(docX, docY, b.ContentRect))
                    .FirstOrDefault();
                if (candidate != null)
                {
                    var ar = candidate.ContentRect;
                    anchoredDocX = ar.X + anchorFracX * ar.Width;
                    anchoredDocY = ar.Y + anchorFracY * ar.Height;
                }
            }

            float nextScrollX = anchoredDocX - anchorX / Math.Max(0.0001f, EffectiveZoom);
            float nextScrollY = anchoredDocY - anchorY / Math.Max(0.0001f, EffectiveZoom);
            if (float.IsFinite(nextScrollX)) _scrollOffset.X = Math.Max(0, nextScrollX);
            if (float.IsFinite(nextScrollY)) _scrollOffset.Y = Math.Max(0, nextScrollY);
            ClampScrollOffsetToDocument();
            // ReflowDocumentToStableViewport already committed a self-consistent
            // scrollbar state. Only refresh ranges/clamping here; do not run a
            // second visibility solver from the freshly committed root.
            UpdateScrollBars(scheduleReflow: false, recomputeVisibility: false);
            RecenterScrollAxesIfContentFits();
            _skipHoverRelayoutOnceAfterZoom = true;
            _zoomInteractionStabilizeUntilTicks = Environment.TickCount64 + 500;
            _resizeReflowTimer.Stop();
            RequestRerender();
        }
        catch (Exception ex)
        {
            Retro96.DebugLog.WriteException("SetZoom", ex);
            _layoutZoom = previousLayout;
            _gestureZoom = previousGesture;
            _gesturePanX = previousGesturePanX;
            _gesturePanY = previousGesturePanY;
            // A failed zoom rebuild can leave a partially rebuilt root. Restore
            // the previous zoom's layout too, rather than pairing the old zoom
            // factor with geometry from the failed target zoom.
            try { ReflowDocumentToStableViewport(); } catch { }
            UpdateScrollBars(scheduleReflow: false, recomputeVisibility: false);
            RequestRerender();
        }
        ZoomChanged?.Invoke(EffectiveZoom);
    }


    /// <summary>Temporary Ctrl+wheel zoom around an explicit client-space point.
    /// The committed/control zoom (_layoutZoom) is the lower bound and is never
    /// modified here. A gesture can only add magnification above that baseline
    /// and can later return exactly to it. The document coordinate beneath the
    /// cursor is preserved before and after the temporary scale change.
    /// </summary>
    public void ZoomAtPoint(float value, System.Drawing.Point clientPoint)
    {
        float current = Math.Max(0.25f, EffectiveZoom);
        float baseline = Math.Max(0.25f, _layoutZoom);
        float target = Math.Clamp(float.IsFinite(value) ? value : current, baseline, 4f);
        if (Math.Abs(target - current) <= 0.0005f) return;

        float docX = _scrollOffset.X + clientPoint.X / Math.Max(0.0001f, current);
        float docY = _scrollOffset.Y + clientPoint.Y / Math.Max(0.0001f, current);

        float gesture = target / baseline;
        float maxGesture = Math.Max(1f, 4f / baseline);
        if (!float.IsFinite(gesture)) gesture = 1f;
        _gestureZoom = Math.Clamp(gesture, 1f, maxGesture);
        // Keep the temporary gesture purely as scale + normal document scroll.
        // Do not maintain a second hidden pan layer: that was the source of the
        // gesture/control coupling and made pointer movement appear to change
        // the zoom state.
        _gesturePanX = 0f;
        _gesturePanY = 0f;
        _lastGesturePoint = clientPoint;

        float effectiveAfter = Math.Max(0.0001f, EffectiveZoom);
        float nextScrollX = docX - clientPoint.X / effectiveAfter;
        float nextScrollY = docY - clientPoint.Y / effectiveAfter;
        if (float.IsFinite(nextScrollX)) _scrollOffset.X = Math.Max(0f, nextScrollX);
        if (float.IsFinite(nextScrollY)) _scrollOffset.Y = Math.Max(0f, nextScrollY);

        ClampScrollOffsetToDocument();
        _resizeReflowTimer.Stop();
        _zoomInteractionStabilizeUntilTicks = Environment.TickCount64 + 1200;
        UpdateScrollBars(
            scheduleReflow: false,
            recomputeVisibility: !IsGestureZoomActive);
        // Gesture zoom is a temporary presentation transform, not a committed
        // zoom change. Do not raise ZoomChanged here: consumers of that event
        // (including zoom controls) must never observe or persist a gesture value.
        RequestRerender();
    }

    private void PaintFrameExport(SKCanvas canvas, Renderer renderer, LayoutBox frameBox,
                                          FrameView view, RectangleF destRect,
                                          RectangleF ancestorClip, int depth,
                                          FontCache fonts, ImageCache images)
    {
        if (depth > 8 || destRect.Width <= 0f || destRect.Height <= 0f) return;
        var visible = IntersectRect(destRect, ancestorClip);
        if (visible.Width <= 0f || visible.Height <= 0f) return;
        int state = canvas.Save();
        try
        {
            canvas.ClipRect(SKRect.Create(visible.X, visible.Y, visible.Width, visible.Height), SKClipOperation.Intersect);
            canvas.Translate(destRect.X, destRect.Y);
            canvas.ClipRect(SKRect.Create(0, 0, frameBox.Width, frameBox.Height), SKClipOperation.Intersect);
            renderer.RenderLocalToCanvas(canvas, view.RootBox!, view.Document, fonts, images,
                Math.Max(1f, frameBox.Width), Math.Max(1f, frameBox.Height),
                view.Scroll.X, view.Scroll.Y, view.Document.HoveredElement, true,
                _focusedInputFrame == view ? _focusedInput : null, _showBoxOutlines);
        }
        finally
        {
            canvas.RestoreToCount(state);
        }
        foreach (var (childBox, childView) in view.ChildFrames.ToArray())
        {
            var childDest = new RectangleF(
                destRect.X + childBox.X - view.Scroll.X,
                destRect.Y + childBox.Y - view.Scroll.Y,
                childBox.Width, childBox.Height);
            PaintFrameExport(canvas, renderer, childBox, childView, childDest, visible,
                depth + 1, fonts, images);
        }
    }

    public byte[] CaptureViewportPng()
    {
        // Screenshot capture is intentionally a raster readback/export boundary:
        // the live browser remains GPU-only, while capture creates a temporary
        // CPU surface solely because PNG encoding needs addressable pixels.
        int width = Math.Max(1, GetViewportSize().Width);
        int height = Math.Max(1, GetViewportSize().Height);
        float zoom = Math.Max(0.25f, EffectiveZoom);
        float logicalVw = width / zoom;
        float logicalVh = height / zoom;

        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Unable to create screenshot surface.");
        var canvas = surface.Canvas;
        // Keep viewport captures consistent with the live GL surface. A fixed
        // frameset can be smaller than the zoomed-out viewport; the unused
        // area is document/workspace space, not a second grey canvas.
        canvas.Clear(SKColors.White);

        if (_rootBox != null && _document != null && _fontCache != null &&
            _imageCache != null && _resourceLoader != null)
        {
            var renderer = new Renderer(_fontCache, _imageCache, _resourceLoader)
            {
                PressedElement = _pressedControl,
                TextareaStateResolver = GetTextareaRenderState,
                SelectScrollResolver = GetSelectScrollOffset,
                FieldScrollResolver = GetRendererFieldScrollX,
                EmbeddedFrameResolver = ResolveEmbeddedContent,
                EmbeddedCanvasResolver = RenderEmbeddedCanvas
            };

            renderer.RenderToCanvas(canvas, _rootBox, _document, _fontCache, _imageCache,
                logicalVw, logicalVh, PaintScrollX, PaintScrollY,
                _lastHoveredElement, _blinkVisible, _showBoxOutlines, _focusedInput,
                zoom, clearBackground: true);

            using var overlay = Graphics.FromCanvas(canvas);
            overlay.ScaleTransform(zoom, zoom);
            var viewportRect = new RectangleF(0, 0, logicalVw, logicalVh);
            overlay.SetClip(viewportRect, CombineMode.Intersect);
            foreach (var (box, view) in _frames.ToArray())
            {
                var destRect = new RectangleF(
                    box.X - PaintScrollX, box.Y - PaintScrollY,
                    Math.Max(0f, box.Width), Math.Max(0f, box.Height));
                PaintFrameExport(canvas, renderer, box, view, destRect, viewportRect, 0, _fontCache, _imageCache);
            }
            if (_focusedInput != null) PaintFieldOverlay(overlay);
            PaintFindHighlights(overlay, PaintScrollX, PaintScrollY);
            PaintEmbeddedMidiControls(overlay);
        }

        surface.Flush();
        using var image = surface.Snapshot();
        if (image == null) return Array.Empty<byte>();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public void FindInPage(string text, bool caseSensitive, bool wrapAround)
    {
        _findFieldViewportUserOverrides.Clear();
        _findText = text ?? string.Empty;
        _findCaseSensitive = caseSensitive;
        _findWrap = wrapAround;
        _findIndex = -1;
        RefreshFindMatches(preserveCurrent: false);
        if (_findMatches.Count > 0)
            FindNext();
    }

    public void FindNext()
    {
        // A deliberate Find navigation is a new request and may reclaim control
        // of the field viewport from a prior manual click.
        _findFieldViewportUserOverrides.Clear();
        if (_findMatches.Count == 0)
        {
            RaiseFindChanged();
            return;
        }

        int next;
        if (_findIndex < 0)
            next = 0;
        else
            next = _findIndex + 1;

        if (next >= _findMatches.Count)
        {
            if (!_findWrap)
            {
                RaiseFindChanged();
                return;
            }
            next = 0;
        }

        if (_findIndex >= 0 && _findIndex < _findMatches.Count)
            ResetFindFieldViewport(_findMatches[_findIndex]);
        _findIndex = next;
        EnsureFindMatchVisible(_findMatches[_findIndex]);
        RaiseFindChanged();
        Invalidate();
    }

    public void FindPrevious()
    {
        _findFieldViewportUserOverrides.Clear();
        if (_findMatches.Count == 0)
        {
            RaiseFindChanged();
            return;
        }

        int previous = _findIndex < 0 ? _findMatches.Count - 1 : _findIndex - 1;
        if (previous < 0)
        {
            if (!_findWrap)
            {
                RaiseFindChanged();
                return;
            }
            previous = _findMatches.Count - 1;
        }

        if (_findIndex >= 0 && _findIndex < _findMatches.Count)
            ResetFindFieldViewport(_findMatches[_findIndex]);
        _findIndex = previous;
        EnsureFindMatchVisible(_findMatches[_findIndex]);
        RaiseFindChanged();
        Invalidate();
    }

    public void FindClear() => ClearFindState();

    private void ClearFindState()
    {
        bool changed = _findText.Length != 0 || _findMatches.Count != 0 || _findIndex != -1;
        _findText = string.Empty;
        _findMatches.Clear();
        _findIndex = -1;
        // Find-created horizontal field viewports are temporary. Once Find is
        // closed there must be no stale offset left on an unfocused input.
        _fieldFindScrollXs.Clear();
        _findFieldViewportUserOverrides.Clear();
        if (_focusedInput?.TagName == "input")
            _fieldScrollX = 0f;
        InvalidateDisplayLists();
        if (changed) RaiseFindChanged();
        Invalidate();
    }

    private void RefreshFindMatches(bool preserveCurrent)
    {
        if (string.IsNullOrEmpty(_findText) || _rootBox == null)
        {
            _findMatches.Clear();
            _findIndex = -1;
            RaiseFindChanged();
            Invalidate();
            return;
        }

        DomElement? oldElement = null;
        int oldStart = -1;
        if (preserveCurrent && _findIndex >= 0 && _findIndex < _findMatches.Count)
        {
            var old = _findMatches[_findIndex];
            oldElement = old.Box.Element;
            oldStart = old.Start;
        }

        var matches = new List<FindMatch>();
        StringComparison comparison = _findCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        CollectFindMatches(_rootBox, null, matches, comparison);
        foreach (var (_, frame) in _frames)
            CollectFindMatchesInFrameTree(frame, matches, comparison);

        _findMatches.Clear();
        _findMatches.AddRange(matches);
        if (_findMatches.Count == 0)
        {
            _findIndex = -1;
        }
        else if (preserveCurrent && oldElement != null)
        {
            _findIndex = _findMatches.FindIndex(m =>
                ReferenceEquals(m.Box.Element, oldElement) && m.Start == oldStart);
            if (_findIndex < 0) _findIndex = Math.Min(_findIndex, _findMatches.Count - 1);
        }
        else
        {
            _findIndex = -1;
        }

        RaiseFindChanged();
    }

    private readonly record struct FindTextPart(
        FrameView? Frame,
        LayoutBox Box,
        string Text);

    private readonly record struct FindTextMap(
        int PartIndex,
        int SourceIndex);

    private static bool FindPartsNeedSeparator(
        FindTextPart previous,
        FindTextPart current)
    {
        var a = previous.Box.ContentRect;
        var b = current.Box.ContentRect;
        if (Math.Abs(b.Top - a.Top) > Math.Max(2f, Math.Min(a.Height, b.Height) * 0.5f))
            return true;

        return HasLayoutWhitespaceBetween(previous.Box, current.Box) ||
               b.Left > a.Right + 0.5f;
    }

    private static void AppendNormalizedFindPart(
        StringBuilder text,
        List<FindTextMap> map,
        FindTextPart part,
        int partIndex)
    {
        bool pendingWhitespace = false;
        int pendingWhitespaceSource = -1;

        for (int i = 0; i < part.Text.Length; i++)
        {
            char ch = part.Text[i];
            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace = true;
                if (pendingWhitespaceSource < 0)
                    pendingWhitespaceSource = i;
                continue;
            }

            if (pendingWhitespace && text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
                map.Add(new FindTextMap(partIndex, pendingWhitespaceSource));
            }

            pendingWhitespace = false;
            pendingWhitespaceSource = -1;
            text.Append(ch);
            map.Add(new FindTextMap(partIndex, i));
        }

        if (pendingWhitespace && text.Length > 0 && text[^1] != ' ')
        {
            text.Append(' ');
            map.Add(new FindTextMap(partIndex, pendingWhitespaceSource));
        }
    }

    private void AddFindMatchesAcrossTextParts(
        List<FindMatch> output,
        List<FindTextPart> parts,
        StringComparison comparison)
    {
        if (parts.Count == 0 || _findText.Length == 0) return;

        var normalized = new StringBuilder();
        var map = new List<FindTextMap>();
        for (int i = 0; i < parts.Count; i++)
        {
            if (normalized.Length > 0 &&
                FindPartsNeedSeparator(parts[i - 1], parts[i]) &&
                normalized[^1] != ' ')
            {
                normalized.Append(' ');
                // Synthetic separators participate in matching but never paint
                // a phantom glyph.
                map.Add(new FindTextMap(-1, -1));
            }

            AppendNormalizedFindPart(normalized, map, parts[i], i);
        }

        string query = NormalizeFindQuery(_findText);
        if (query.Length == 0 || normalized.Length < query.Length) return;

        string normalizedString = normalized.ToString();
        int from = 0;
        while (from <= normalizedString.Length - query.Length)
        {
            int at = normalizedString.IndexOf(query, from, comparison);
            if (at < 0) break;

            int end = at + query.Length;
            var segments = new List<FindMatchSegment>();
            var segmentStarts = new Dictionary<int, int>();
            var segmentEnds = new Dictionary<int, int>();

            for (int i = at; i < end && i < map.Count; i++)
            {
                var mapped = map[i];
                if (mapped.PartIndex < 0) continue;

                if (!segmentStarts.TryGetValue(mapped.PartIndex, out int existingStart))
                    segmentStarts[mapped.PartIndex] = mapped.SourceIndex;
                else
                    segmentStarts[mapped.PartIndex] =
                        Math.Min(existingStart, mapped.SourceIndex);

                int next = mapped.SourceIndex + 1;
                if (!segmentEnds.TryGetValue(mapped.PartIndex, out int existingEnd))
                    segmentEnds[mapped.PartIndex] = next;
                else
                    segmentEnds[mapped.PartIndex] = Math.Max(existingEnd, next);
            }

            foreach (var pair in segmentStarts.OrderBy(p => p.Key))
            {
                int partIndex = pair.Key;
                int segStart = pair.Value;
                int segEnd = segmentEnds[partIndex];
                if (segEnd <= segStart) continue;

                var part = parts[partIndex];
                segments.Add(new FindMatchSegment(
                    part.Frame, part.Box, segStart, segEnd - segStart, null));
            }

            if (segments.Count > 0)
            {
                var first = segments[0];
                output.Add(new FindMatch(
                    first.Frame, first.Box, first.Start, first.Length, null, segments));
            }

            from = at + Math.Max(1, query.Length);
        }
    }

    private static string NormalizeFindQuery(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        bool pendingWhitespace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace = sb.Length > 0;
                continue;
            }

            if (pendingWhitespace)
                sb.Append(' ');
            pendingWhitespace = false;
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    private void CollectFindMatches(LayoutBox root, FrameView? frame,
                                    List<FindMatch> output,
                                    StringComparison comparison)
    {
        var stack = new Stack<LayoutBox>();
        var textParts = new List<FindTextPart>();
        stack.Push(root);

        void FlushTextParts()
        {
            if (textParts.Count == 0) return;
            AddFindMatchesAcrossTextParts(output, textParts, comparison);
            textParts.Clear();
        }

        while (stack.Count > 0)
        {
            var box = stack.Pop();

            // Searchable replaced controls own their displayed text. Do not also
            // consume an incidental TextRun on the control, and for a single
            // select only search the option actually displayed in the field.
            if (box.BoxType == BoxType.Replaced &&
                IsSearchableControlElement(box.Element))
            {
                FlushTextParts();

                string controlText = GetSelectableControlText(box.Element!);
                if (controlText.Length > 0)
                    AddFindMatchesForText(output, frame, box, controlText,
                        comparison, box.Element);

                // The control owns its displayed value. Descendant <option> /
                // label text is not a second searchable copy; traversing it here
                // made a single dropdown produce matches for hidden options too.
                continue;
            }

            string run = box.TextRun ?? string.Empty;
            if (IsSelectionEligibleBox(box) && run.Length > 0)
                textParts.Add(new FindTextPart(frame, box, run));

            for (int i = box.Children.Count - 1; i >= 0; i--)
                stack.Push(box.Children[i]);
        }

        FlushTextParts();
    }

    private void AddFindMatchesForText(List<FindMatch> output, FrameView? frame,
                                       LayoutBox box, string text,
                                       StringComparison comparison, DomElement? sourceElement)
    {
        if (text.Length == 0 || _findText.Length == 0) return;

        string query = NormalizeFindQuery(_findText);
        string normalized = NormalizeFindQuery(text);
        if (query.Length == 0 || normalized.Length == 0) return;

        int from = 0;
        while (from <= normalized.Length - query.Length)
        {
            int at = normalized.IndexOf(query, from, comparison);
            if (at < 0) break;

            int originalStart = FindNormalizedOffsetToSource(text, at);
            int originalEnd = FindNormalizedOffsetToSource(text, at + query.Length);
            if (originalEnd <= originalStart)
                originalEnd = Math.Min(text.Length,
                    originalStart + Math.Max(1, query.Length));

            output.Add(new FindMatch(frame, box,
                originalStart, originalEnd - originalStart, sourceElement));
            from = at + Math.Max(1, query.Length);
        }
    }

    private static int FindNormalizedOffsetToSource(string source, int normalizedOffset)
    {
        if (normalizedOffset <= 0) return 0;

        int normalized = 0;
        bool inWhitespace = false;
        for (int i = 0; i < source.Length; i++)
        {
            if (char.IsWhiteSpace(source[i]))
            {
                if (!inWhitespace)
                {
                    if (normalized == normalizedOffset) return i;
                    normalized++;
                    inWhitespace = true;
                }
                continue;
            }

            inWhitespace = false;
            if (normalized == normalizedOffset) return i;
            normalized++;
        }

        return source.Length;
    }

    private void CollectFindMatchesInFrameTree(FrameView frame, List<FindMatch> output,
                                               StringComparison comparison)
    {
        CollectFindMatches(frame.RootBox, frame, output, comparison);
        foreach (var (_, child) in frame.ChildFrames)
            CollectFindMatchesInFrameTree(child, output, comparison);
    }

    private void RaiseFindChanged() => FindChanged?.Invoke(
        _findMatches.Count,
        _findIndex >= 0 && _findIndex < _findMatches.Count ? _findIndex + 1 : 0);

    private void ResetFindFieldViewport(FindMatch match)
    {
        var element = match.SourceElement;
        if (element?.TagName != "input" || IsPasswordField(element)) return;
        if (_fieldFindScrollXs.Remove(element))
            InvalidateDisplayLists();
        if (ReferenceEquals(element, _focusedInput))
            _fieldScrollX = 0f;
    }

    private void EnsureFindMatchVisible(FindMatch match)
    {
        // A match can be inside a horizontally/vertically scrollable form control.
        // Bring that control-local viewport to the match first; the page scroll
        // solver then only has to reveal the control itself.
        EnsureFindControlInternalScrollVisible(match);

        if (match.Frame == null)
        {
            EnsureTopLevelFindMatchVisible(match.Box);
            return;
        }

        // Scroll the matched frame internally first. The frame tree is then
        // recomposed so nested-frame changes become visible immediately.
        var metrics = TryGetFrameBoxForView(match.Frame, out var frameBox)
            ? GetFrameScrollMetrics(frameBox, match.Frame)
            : default;
        if (metrics.MaxScrollX > 0 || metrics.MaxScrollY > 0)
        {
            float targetX = match.Frame.Scroll.X;
            float targetY = match.Frame.Scroll.Y;
            if (match.Box.X < targetX + 12f)
                targetX = match.Box.X - 12f;
            else if (match.Box.X + match.Box.Width > targetX + metrics.ViewportWidth - 12f)
                targetX = match.Box.X + match.Box.Width - metrics.ViewportWidth + 12f;
            if (match.Box.Y < targetY + 12f)
                targetY = match.Box.Y - 12f;
            else if (match.Box.Y + match.Box.Height > targetY + metrics.ViewportHeight - 12f)
                targetY = match.Box.Y + match.Box.Height - metrics.ViewportHeight + 12f;

            match.Frame.Scroll.X = Math.Clamp(targetX, 0, metrics.MaxScrollX);
            match.Frame.Scroll.Y = Math.Clamp(targetY, 0, metrics.MaxScrollY);
            RecomposeFrameTree(match.Frame);
        }

        // Also bring the outer frame into view. For deeply nested frames this
        // follows their composed screen rectangle without dereferencing stale
        // layout boxes from a previous reflow.
        if (TryGetFrameScreenRect(match.Frame, out var rect))
        {
            var vp = GetViewportSize();
            float nextX = _scrollOffset.X;
            float nextY = _scrollOffset.Y;
            if (rect.Left < 0) nextX += rect.Left;
            else if (rect.Right > vp.Width) nextX += rect.Right - vp.Width;
            if (rect.Top < 0) nextY += rect.Top;
            else if (rect.Bottom > vp.Height) nextY += rect.Bottom - vp.Height;
            if (Math.Abs(nextX - _scrollOffset.X) > 0.1f || Math.Abs(nextY - _scrollOffset.Y) > 0.1f)
                ScrollTo((int)nextX, (int)nextY);
        }
    }

    private void EnsureTopLevelFindMatchVisible(LayoutBox box)
    {
        var vp = GetViewportSize();
        float viewportW = Math.Max(1, vp.Width) / Math.Max(0.25f, EffectiveZoom);
        float viewportH = Math.Max(1, vp.Height) / Math.Max(0.25f, EffectiveZoom);
        float nextX = _scrollOffset.X;
        float nextY = _scrollOffset.Y;
        float width = Math.Max(1f, box.BorderRect.Width);
        float height = Math.Max(1f, box.BorderRect.Height);

        if (width > viewportW)
            nextX = box.X;
        else if (box.X < nextX + 12f)
            nextX = box.X - 12f;
        else if (box.X + width > nextX + viewportW - 12f)
            nextX = box.X + width - viewportW + 12f;

        if (height > viewportH)
            nextY = box.Y;
        else if (box.Y < nextY + 16f)
            nextY = box.Y - 16f;
        else if (box.Y + height > nextY + viewportH - 16f)
            nextY = box.Y + height - viewportH + 16f;

        ScrollTo((int)Math.Max(0, nextX), (int)Math.Max(0, nextY));
    }

    private bool TryGetFrameBoxForView(FrameView target, out LayoutBox box)
    {
        foreach (var pair in _frames)
        {
            if (ReferenceEquals(pair.Value, target))
            {
                box = pair.Key;
                return true;
            }
            if (TryFindFrameBoxRecursive(pair.Value, target, out box))
                return true;
        }
        box = null!;
        return false;
    }

    private static bool TryFindFrameBoxRecursive(FrameView current, FrameView target, out LayoutBox box)
    {
        foreach (var (childBox, child) in current.ChildFrames)
        {
            if (ReferenceEquals(child, target))
            {
                box = childBox;
                return true;
            }
            if (TryFindFrameBoxRecursive(child, target, out box))
                return true;
        }
        box = null!;
        return false;
    }

    private bool TryGetFrameScreenRect(FrameView target, out RectangleF rect)
    {
        float scrollX = PaintScrollX;
        float scrollY = PaintScrollY;
        foreach (var (box, frame) in _frames)
        {
            var top = new RectangleF(box.X - scrollX, box.Y - scrollY, box.Width, box.Height);
            if (ReferenceEquals(frame, target))
            {
                rect = top;
                return true;
            }
            if (TryGetNestedFrameScreenRect(top, frame, target, out rect))
                return true;
        }
        rect = RectangleF.Empty;
        return false;
    }

    private static bool TryGetNestedFrameScreenRect(RectangleF parentRect, FrameView parent,
                                                    FrameView target, out RectangleF rect)
    {
        foreach (var (box, child) in parent.ChildFrames)
        {
            var childRect = new RectangleF(
                parentRect.X + box.X - parent.Scroll.X,
                parentRect.Y + box.Y - parent.Scroll.Y,
                box.Width,
                box.Height);
            if (ReferenceEquals(child, target))
            {
                rect = childRect;
                return true;
            }
            if (TryGetNestedFrameScreenRect(childRect, child, target, out rect))
                return true;
        }
        rect = RectangleF.Empty;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
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
        InvalidateDisplayLists();
        Invalidate();
        if (IsHandleCreated && !IsDisposed)
            Update();
    }

    /// <summary>
    /// Coalesced: any number of requests in one message-loop turn collapse
    /// into a single render.
    /// </summary>
    public void RequestRerender()
    {
        InvalidateDisplayLists();
        if (_rerenderQueued)
        {
            Invalidate();
            return;
        }
        _rerenderQueued = true;

        if (IsHandleCreated && !IsDisposed)
        {
            try
            {
                BeginInvoke(() =>
                {
                    _rerenderQueued = false;
                    if (IsDisposed) return;
                    if (_fontCache != null && _imageCache != null && _resourceLoader != null)
                        ReRenderPage(_fontCache, _imageCache, _resourceLoader);
                    else
                        Invalidate();
                });
            }
            catch (InvalidOperationException)
            {
                _rerenderQueued = false;
                Invalidate();
            }
        }
        else
        {
            _rerenderQueued = false;
            Invalidate();
        }
    }

    public void PrefetchImage(string url)
    {
        if (!BrowserRuntime.ImagesEnabled) return;
        if (_resourceLoader == null || _imageCache == null || _document?.BaseUrl == null)
            return;
        try
        {
            string abs = ImageCache.ResolveUrl(url, _document.BaseUrl.ToAbsolute());
            _ = _imageCache.GetAsync(abs, _resourceLoader, default);
        }
        catch { }
    }

    /// <summary>
    /// Builds a clean print surface from the current document without using
    /// the user's current scroll/hover/selection state.  The screen bitmap
    /// is a viewport composition, so printing it directly could print a
    /// partially scrolled or highlighted page.  Re-rendering from the live
    /// layout tree at scroll (0,0) gives PrintDocument a stable page surface.
    /// </summary>
    public Bitmap? CreatePrintBitmap()
    {
        if (_rootBox == null || _document == null ||
            _fontCache == null || _imageCache == null || _resourceLoader == null)
            return null;

        try
        {
            int width = Math.Max(1, (int)Math.Ceiling(_rootBox.Width));
            int height = Math.Max(1, (int)Math.Ceiling(_rootBox.Height));
            var renderer = new Renderer(_fontCache, _imageCache, _resourceLoader)
            {
                PressedElement = null,
                EmbeddedFrameResolver = ResolveEmbeddedContent,
                IsPrintRendering = true
            };

            return renderer.Render(
                _rootBox, _document,
                _fontCache, _imageCache,
                width, height,
                0f, 0f,
                hoveredElement: null,
                blinkVisible: true,
                showBoxOutlines: false,
                focusedElement: null);
        }
        catch (Exception ex)
        {
            Retro96.DebugLog.WriteException("CreatePrintBitmap", ex);
            return null;
        }
    }

    public void ScrollToAnchor(string anchorName)
    {
        if (_rootBox == null || _document == null || string.IsNullOrEmpty(anchorName)) return;

        var target = _document.ElementDescendants()
            .FirstOrDefault(e =>
                (e.TagName == "a" &&
                 (e.GetAttr("name") ?? "").Equals(anchorName, StringComparison.OrdinalIgnoreCase)) ||
                (e.GetAttr("id") ?? "").Equals(anchorName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;

        var box = FindAnchorScrollBox(_rootBox, target);
        if (box == null) return;

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