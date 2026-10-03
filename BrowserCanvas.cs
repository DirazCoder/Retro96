namespace Retro96;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Render;
using Retro96.Engine.Js;
using Retro96.Engine.Network;

/// <summary>
/// Main rendering surface for the Retro96 1996-era browser engine.
/// Renders the full document into a bitmap and blits the visible viewport
/// at scroll offset, giving pixel-perfect 1:1 rendering.
/// </summary>
public class BrowserCanvas : Control
{
    private DomDocument?   _document;
    private LayoutBox?     _rootBox;
    private Bitmap?        _renderedBitmap;
    private JsInterpreter? _jsInterpreter;
    private ImageCache?    _imageCache;
    private FontCache?     _fontCache;
    private ResourceLoader? _resourceLoader;

    public void SetResourceLoader(ResourceLoader loader)
    {
        _resourceLoader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    private readonly VScrollBar _vScroll = new VScrollBar();
    private readonly HScrollBar _hScroll = new HScrollBar();

    private PointF _scrollOffset = PointF.Empty;

    private readonly Timer _animationTimer = new Timer { Interval = 100 };  // 10 fps GIFs
    private readonly Timer _blinkTimer     = new Timer { Interval = 500 };  // blink toggle
    private readonly Timer _marqueeTimer   = new Timer { Interval = 50  };  // marquee scroll
    private readonly Timer _jsTimer        = new Timer { Interval = 50  };  // JS timers (setTimeout/setInterval)

    private bool _blinkVisible   = true;
    private int  _marqueeOffset  = 0;

    private DomElement? _lastHoveredElement;

    // ── DevTools-style debug overlay state ──────────────────────────────────
    // F12 = dump the layout box tree (source order + geometry) to Debug output.
    // F11 = toggle the box-model outline overlay (margin/border/padding/content).
    private bool _showBoxOutlines = false;

    public event Action<string>? NavigateRequested;
    public event Action<string>? StatusChanged;

    public BrowserCanvas()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint  |
            ControlStyles.UserPaint             |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();

        // Needed so this control actually receives KeyDown for the devtools
        // shortcuts below — Control doesn't take keyboard focus by default.
        TabStop = true;

        _vScroll.Dock      = DockStyle.Right;
        _vScroll.Scroll   += OnVScroll;
        _vScroll.TabStop   = false;
        Controls.Add(_vScroll);

        _hScroll.Dock      = DockStyle.Bottom;
        _hScroll.Scroll   += OnHScroll;
        _hScroll.TabStop   = false;
        Controls.Add(_hScroll);

        _animationTimer.Tick += OnAnimationTick;
        _blinkTimer.Tick     += OnBlinkTick;
        _marqueeTimer.Tick   += OnMarqueeTick;
        _jsTimer.Tick        += OnJsTimerTick;

        KeyDown += OnDevToolsKeyDown;
    }

    /// <summary>
    /// F12: dump the layout box tree (source order + geometry) to the Debug
    /// output window — flags any non-floated sibling whose Y went backwards
    /// relative to the previous sibling in the same parent, which is the
    /// signature of an ordering bug (float misplacement, inline run flushed
    /// out of sequence, block child positioned before its float/inline
    /// predecessor was accounted for).
    ///
    /// F11: toggle the box-model outline overlay (margin=orange, border=red,
    /// padding=green, content=blue — matches the Chrome/Firefox devtools
    /// convention) and re-render so it's visible immediately.
    /// </summary>
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
            if (_fontCache != null && _imageCache != null && _resourceLoader != null)
                ReRenderPage(_fontCache, _imageCache, _resourceLoader);
            e.Handled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hand a new parsed page to the canvas.  ReRenderPage() must be called
    /// afterwards to produce the bitmap (done by Form1).
    /// </summary>
    public void SetPage(DomDocument doc, LayoutBox rootBox,
                         JsInterpreter js, FontCache fontCache, ImageCache images)
    {
        _document      = doc;
        _rootBox       = rootBox;
        _jsInterpreter = js;
        _fontCache     = fontCache;
        _imageCache    = images;

        _scrollOffset       = PointF.Empty;
        _lastHoveredElement = null;

        // Dispose old bitmap so memory is freed before new one is allocated
        _renderedBitmap?.Dispose();
        _renderedBitmap = null;
    }

    /// <summary>
    /// Re-renders the full document into a bitmap and forces a repaint.
    /// Safe to call from the UI thread.
    /// </summary>
    public void ReRenderPage(FontCache fontCache, ImageCache imageCache, ResourceLoader resourceLoader)
    {
        if (_rootBox == null || _document == null) return;

        var renderer = new Renderer(fontCache, imageCache, resourceLoader);

        // Full document size render — NO viewport culling inside the renderer.
        var newBitmap = renderer.Render(
            _rootBox, _document,
            fontCache, imageCache,
            ClientSize.Width, ClientSize.Height,
            0f, 0f,
            _lastHoveredElement,
            _blinkVisible,
            _showBoxOutlines);

        _renderedBitmap?.Dispose();
        _renderedBitmap = newBitmap;

        UpdateScrollBars();
        CheckAndStartTimers();
        Invalidate();
        Update();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Painting
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.None;
        g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.None;

        if (_renderedBitmap == null)
        {
            g.Clear(Color.White);
            return;
        }

        // Blit the full-document bitmap with the scroll offset applied.
        // DrawImage(bmp, destX, destY) — negative offsets scroll the bitmap.
        g.DrawImage(_renderedBitmap,
                    -(int)_scrollOffset.X,
                    -(int)_scrollOffset.Y);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scroll
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateScrollBars()
    {
        if (_renderedBitmap == null)
        {
            _vScroll.Visible = false;
            _hScroll.Visible = false;
            return;
        }

        int docW = _renderedBitmap.Width;
        int docH = _renderedBitmap.Height;
        int vpW  = ClientSize.Width;
        int vpH  = ClientSize.Height;

        bool needV = docH > vpH;
        bool needH = docW > vpW;

        _vScroll.Visible = needV;
        if (needV)
        {
            _vScroll.Minimum    = 0;
            _vScroll.Maximum    = docH;
            _vScroll.LargeChange = Math.Max(1, vpH);
            _vScroll.SmallChange = Math.Max(1, vpH / 10);
        }

        _hScroll.Visible = needH;
        if (needH)
        {
            _hScroll.Minimum    = 0;
            _hScroll.Maximum    = docW;
            _hScroll.LargeChange = Math.Max(1, vpW);
            _hScroll.SmallChange = Math.Max(1, vpW / 10);
        }

        // Clamp offsets
        float maxX = needH ? Math.Max(0, docW - vpW) : 0;
        float maxY = needV ? Math.Max(0, docH - vpH) : 0;
        _scrollOffset.X = Math.Max(0, Math.Min(_scrollOffset.X, maxX));
        _scrollOffset.Y = Math.Max(0, Math.Min(_scrollOffset.Y, maxY));

        if (needV) _vScroll.Value = Math.Min((int)_scrollOffset.Y, Math.Max(0, _vScroll.Maximum - _vScroll.LargeChange));
        if (needH) _hScroll.Value = Math.Min((int)_scrollOffset.X, Math.Max(0, _hScroll.Maximum - _hScroll.LargeChange));
    }

    private void OnVScroll(object? sender, ScrollEventArgs e)
    {
        _scrollOffset.Y = e.NewValue;
        Invalidate();
    }

    private void OnHScroll(object? sender, ScrollEventArgs e)
    {
        _scrollOffset.X = e.NewValue;
        Invalidate();
    }

    public void ScrollTo(int x, int y)
    {
        if (_renderedBitmap == null) return;
        float maxX = Math.Max(0, _renderedBitmap.Width  - ClientSize.Width);
        float maxY = Math.Max(0, _renderedBitmap.Height - ClientSize.Height);
        _scrollOffset.X = Math.Max(0, Math.Min(x, maxX));
        _scrollOffset.Y = Math.Max(0, Math.Min(y, maxY));
        if (_vScroll.Visible) _vScroll.Value = (int)_scrollOffset.Y;
        if (_hScroll.Visible) _hScroll.Value = (int)_scrollOffset.X;
        Invalidate();
    }

    public void ScrollBy(int dx, int dy) => ScrollTo((int)_scrollOffset.X + dx, (int)_scrollOffset.Y + dy);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_vScroll.Visible) return;
        int delta = -(e.Delta / 120) * 40; // 40 px per notch, Netscape style
        ScrollBy(0, delta);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBars();
        Invalidate();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse / hit-test
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_rootBox == null || _document == null) return;

        float x = e.X + _scrollOffset.X;
        float y = e.Y + _scrollOffset.Y;

        var element = HitTestElement(_rootBox, x, y);
        if (element == null) return;

        // Handle form elements
        if (IsFormElement(element))
        {
            HandleFormElementClick(element);
            return;
        }

        if (IsSubmitButton(element))
        {
            HandleFormSubmission(element);
            return;
        }

        if (element.TagName == "a" && element.HasAttr("href"))
        {
            string href = element.GetAttr("href")!;
            if (_document.BaseUrl != null)
                NavigateRequested?.Invoke(_document.BaseUrl.Resolve(href).ToAbsolute());
            return;
        }

        if (element.TagName == "img" && element.HasAttr("usemap"))
            HandleImageMapClick(element, x, y);

        _jsInterpreter?.FireEvent(element, "onclick");
    }

    /// <summary>
    /// Checks if the element is a form control.
    /// </summary>
    private static bool IsFormElement(DomElement elem)
    {
        return elem.TagName is "input" or "select" or "textarea" or "button";
    }

    /// <summary>
    /// Handles clicking on form elements (toggle checkboxes, dropdowns, etc.).
    /// </summary>
    private void HandleFormElementClick(DomElement element)
    {
        if (element.TagName == "input")
        {
            string type = element.GetAttrOrDefault("type", "text");
            if (type == "checkbox" || type == "radio")
            {
                // Toggle checked state
                if (element.HasAttr("checked"))
                    element.Attrs.Remove("checked");
                else
                    element.Attrs["checked"] = "checked";

                // Trigger onclick event
                _jsInterpreter?.FireEvent(element, "onclick");

                // Re-render to show the change
                if (_document != null && _rootBox != null && _fontCache != null && _imageCache != null && _resourceLoader != null)
                {
                    ReRenderPage(_fontCache, _imageCache, _resourceLoader);
                }
            }
        }
        else if (element.TagName == "select")
        {
            // Toggle dropdown (simplified - just show an alert for now)
            var options = element.Children.OfType<DomElement>()
                .Where(o => o.TagName == "option")
                .Select(o => o.InnerText ?? o.GetAttr("value") ?? "")
                .ToList();

            // For now, just cycle through options on click
            var selected = element.Children.OfType<DomElement>()
                .FirstOrDefault(o => o.TagName == "option" && o.HasAttr("selected"));

            if (selected != null)
            {
                selected.Attrs.Remove("selected");
                var next = selected.NextSibling as DomElement;
                if (next?.TagName == "option")
                    next.Attrs["selected"] = "selected";
                else
                {
                    // Wrap to first option
                    var first = element.Children.OfType<DomElement>()
                        .FirstOrDefault(o => o.TagName == "option");
                    if (first != null)
                        first.Attrs["selected"] = "selected";
                }
            }

            // Trigger onchange event
            _jsInterpreter?.FireEvent(element, "onchange");

            // Re-render to show the change
            if (_document != null && _rootBox != null && _fontCache != null && _imageCache != null && _resourceLoader != null)
            {
                ReRenderPage(_fontCache, _imageCache, _resourceLoader);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_rootBox == null || _document == null) return;

        float x = e.X + _scrollOffset.X;
        float y = e.Y + _scrollOffset.Y;

        var element = HitTestElement(_rootBox, x, y);

        if (element != null && element.TagName == "a" && element.HasAttr("href"))
        {
            Cursor = Cursors.Hand;
            try
            {
                var abs = _document.BaseUrl?.Resolve(element.GetAttr("href")!).ToAbsolute() ?? "";
                StatusChanged?.Invoke(abs);
            }
            catch { StatusChanged?.Invoke(""); }
        }
        else
        {
            Cursor = Cursors.Default;
            StatusChanged?.Invoke("");
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

    // ─────────────────────────────────────────────────────────────────────────
    // Hit testing
    // ─────────────────────────────────────────────────────────────────────────

    private static DomElement? HitTestElement(LayoutBox box, float x, float y)
    {
        // Use BorderRect for hit testing so borders are clickable
        if (!box.BorderRect.Contains(x, y)) return null;

        // Children are painted on top, so test them first (in reverse order)
        for (int i = box.Children.Count - 1; i >= 0; i--)
        {
            var result = HitTestElement(box.Children[i], x, y);
            if (result != null) return result;
        }

        return box.Element;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Forms
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsSubmitButton(DomElement e) =>
        (e.TagName == "input"  && e.GetAttrOrDefault("type", "") == "submit") ||
        (e.TagName == "button" && e.GetAttrOrDefault("type", "submit") == "submit");

    private void HandleFormSubmission(DomElement submitButton)
    {
        if (_document == null) return;

        var form = FindEnclosingForm(submitButton);
        if (form == null) return;

        string action = form.GetAttrOrDefault("action", "");
        string method = form.GetAttrOrDefault("method", "get").ToLowerInvariant();

        string resolvedAction = action;
        if (_document.BaseUrl != null && !string.IsNullOrEmpty(action))
            resolvedAction = _document.BaseUrl.Resolve(action).ToAbsolute();
        else if (string.IsNullOrEmpty(action) && _document.BaseUrl != null)
            resolvedAction = _document.BaseUrl.ToAbsolute();

        var formData = new Dictionary<string, string>();
        foreach (var field in form.Descendants().OfType<DomElement>())
        {
            string name = field.GetAttr("name") ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            var val = GetFormFieldValue(field);
            if (val != null) formData[name] = val;
        }

        string qs = string.Join("&", formData.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        string navUrl = method == "get"
            ? (string.IsNullOrEmpty(qs) ? resolvedAction : $"{resolvedAction}?{qs}")
            : $"post://{resolvedAction[7..]}?{qs}"; // strip http:// then prepend post://

        NavigateRequested?.Invoke(navUrl);
    }

    private static DomElement? FindEnclosingForm(DomElement element)
    {
        var node = element.Parent;
        while (node != null)
        {
            if (node is DomElement de && de.TagName == "form") return de;
            node = node.Parent;
        }
        return null;
    }

    private static string? GetFormFieldValue(DomElement field) => field.TagName switch
    {
        "input"    => field.GetAttrOrDefault("type", "text").ToLower() is "checkbox" or "radio"
                          ? (field.HasAttr("checked") ? field.GetAttrOrDefault("value", "on") : null)
                          : field.GetAttrOrDefault("value", ""),
        "textarea" => field.InnerText,
        "select"   => (field.Descendants().OfType<DomElement>()
                           .FirstOrDefault(o => o.TagName == "option" && o.HasAttr("selected"))
                       ?? field.Descendants().OfType<DomElement>()
                               .FirstOrDefault(o => o.TagName == "option"))
                      ?.GetAttrOrDefault("value", "") ?? "",
        _          => null
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Image maps
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleImageMapClick(DomElement imgElement, float x, float y)
    {
        if (_document == null) return;
        string mapName = imgElement.GetAttr("usemap")!.TrimStart('#');
        var map = _document.ElementDescendants()
                           .FirstOrDefault(e => e.TagName == "map" && e.GetAttr("name") == mapName);
        if (map == null) return;

        var imgBox = _rootBox?.Descendants()
                              .FirstOrDefault(b => b.Element == imgElement);
        if (imgBox == null) return;

        float relX = x - imgBox.X;
        float relY = y - imgBox.Y;

        foreach (var area in map.Children.OfType<DomElement>().Where(a => a.TagName == "area"))
        {
            if (!IsPointInArea(relX, relY, area)) continue;
            string href = area.GetAttr("href") ?? "";
            if (!string.IsNullOrEmpty(href) && _document.BaseUrl != null)
                NavigateRequested?.Invoke(_document.BaseUrl.Resolve(href).ToAbsolute());
            break;
        }
    }

    private static bool IsPointInArea(float x, float y, DomElement area)
    {
        string shape = area.GetAttrOrDefault("shape", "rect").ToLowerInvariant();
        var coords   = (area.GetAttr("coords") ?? "")
                           .Split(',')
                           .Select(s => float.TryParse(s.Trim(), out var v) ? v : 0)
                           .ToArray();
        return shape switch
        {
            "circle" when coords.Length >= 3 =>
                Math.Pow(x - coords[0], 2) + Math.Pow(y - coords[1], 2) <= Math.Pow(coords[2], 2),
            "poly" when coords.Length >= 6 =>
                PointInPolygon(x, y, coords),
            "default" => true,
            _ when coords.Length >= 4 => // rect
                x >= coords[0] && y >= coords[1] && x <= coords[2] && y <= coords[3],
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

    // ─────────────────────────────────────────────────────────────────────────
    // Timers
    // ─────────────────────────────────────────────────────────────────────────

    private void CheckAndStartTimers()
    {
        if (!_animationTimer.Enabled && HasAnimatedGifs())  _animationTimer.Start();
        if (!_blinkTimer.Enabled     && HasBlinkElements()) _blinkTimer.Start();
        if (!_marqueeTimer.Enabled   && HasMarqueeElements()) _marqueeTimer.Start();
        if (!_jsTimer.Enabled) _jsTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        // FIX: this used to call Invalidate() only, which just re-blits the
        // cached _renderedBitmap from OnPaint — the bitmap itself was never
        // regenerated, so every GIF frame after the first one was silently
        // dropped and animated images appeared static. The renderer needs to
        // actually re-run (picking up ImageCache.GetCurrentFrame's advanced
        // frame index) for the tick to have any visible effect.
        if (HasAnimatedGifs() && _fontCache != null && _imageCache != null && _resourceLoader != null)
            ReRenderPage(_fontCache, _imageCache, _resourceLoader);
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkVisible = !_blinkVisible;
        // Same issue as OnAnimationTick: Invalidate() alone re-blits the
        // stale cached bitmap, so <blink> never actually blinked.
        if (HasBlinkElements() && _fontCache != null && _imageCache != null && _resourceLoader != null)
            ReRenderPage(_fontCache, _imageCache, _resourceLoader);
    }

    private void OnMarqueeTick(object? sender, EventArgs e)
    {
        _marqueeOffset += 2;
        // Same issue as OnAnimationTick: Invalidate() alone re-blits the
        // stale cached bitmap, so <marquee> never actually scrolled.
        if (HasMarqueeElements() && _fontCache != null && _imageCache != null && _resourceLoader != null)
            ReRenderPage(_fontCache, _imageCache, _resourceLoader);
    }

    private void OnJsTimerTick(object? sender, EventArgs e)
    {
        _jsInterpreter?.TickTimers();
    }

    private bool HasAnimatedGifs()
    {
        if (_document == null || _imageCache == null) return false;
        foreach (var img in _document.Images)
        {
            string? src = img.GetAttr("src");
            if (string.IsNullOrEmpty(src)) continue;
            try
            {
                var abs = (_document.BaseUrl ?? ParsedUrl.Parse("about:blank"))
                              .Resolve(src).ToAbsolute();
                if (_imageCache.IsAnimated(abs)) return true;
            }
            catch { }
        }
        return false;
    }

    private bool HasBlinkElements()   => _document?.ElementDescendants().Any(e => e.TagName == "blink")   ?? false;
    private bool HasMarqueeElements() => _document?.ElementDescendants().Any(e => e.TagName == "marquee") ?? false;

    // Navigation helpers (for JS)
    public void NavigateTo(string url)        => NavigateRequested?.Invoke(url);
    public void NavigateToReplace(string url) => NavigateRequested?.Invoke(url);
    public void OpenNewWindow(string url)     => NavigateRequested?.Invoke(url);
}