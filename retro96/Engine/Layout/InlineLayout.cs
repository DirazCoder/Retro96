using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Retro96.Drawing;
using System.Linq;
using Retro96.Engine.Css;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Layout;

/// <summary>
/// Float band tracking + the inline formatting context. Text runs are
/// fragmented into word and space boxes so line wrapping and box hit-testing
/// remain stable across layout passes.
/// </summary>
public class FloatContext
{
    private readonly List<(float Y, float LeftEdge, float RightEdge)> _bands = new();
    private readonly List<LayoutBox> _floats = new();

    public FloatContext()
    {
        _bands.Add((0f, 0f, float.MaxValue));
    }

    public FloatContext Clone()
    {
        var clone = new FloatContext();
        clone._bands.Clear();
        foreach (var band in _bands)
            clone._bands.Add(band);
        foreach (var f in _floats)
            clone._floats.Add(f);
        return clone;
    }

    public void AddFloat(LayoutBox floatBox)
    {
        _floats.Add(floatBox);

        // FIX: band extent now includes the bottom margin — text must wrap
        // around (and clear) the float's MARGIN box, not its border box,
        // or content rides up into the float's hspace/vspace margin.
        float top = floatBox.Y;
        float bottom = OuterBottom(floatBox);

        if (floatBox.FloatSide == FloatValue.Left)
        {
            float edge = floatBox.X + floatBox.BorderLeft + floatBox.PaddingLeft
                       + floatBox.Width + floatBox.PaddingRight + floatBox.BorderRight
                       + floatBox.MarginRight;
            UpdateBands(top, bottom, edge, isLeft: true);
            LayoutTrace.Log($"AddFloat LEFT  tag={floatBox.Element?.TagName} " +
                $"X={floatBox.X:F1} Y={floatBox.Y:F1} W={floatBox.Width:F1} " +
                $"top={top:F1} bottom={bottom:F1} rightEdgeOfFloat={edge:F1} " +
                $"(_floats.Count now {_floats.Count})");
        }
        else
        {
            float edge = floatBox.X - floatBox.MarginLeft;
            UpdateBands(top, bottom, edge, isLeft: false);
            LayoutTrace.Log($"AddFloat RIGHT tag={floatBox.Element?.TagName} " +
                $"X={floatBox.X:F1} Y={floatBox.Y:F1} W={floatBox.Width:F1} " +
                $"top={top:F1} bottom={bottom:F1} leftEdgeOfFloat={edge:F1} " +
                $"(_floats.Count now {_floats.Count})");
        }
    }

    private void UpdateBands(float top, float bottom, float edge, bool isLeft)
    {
        if (bottom <= top) return;   // FIX: degenerate float — no band to constrain
        EnsureBandAt(top);
        EnsureBandAt(bottom);

        for (int i = 0; i < _bands.Count; i++)
        {
            var b = _bands[i];
            if (b.Y < top || b.Y >= bottom) continue;
            float newL = isLeft ? Math.Max(b.LeftEdge, edge) : b.LeftEdge;
            float newR = !isLeft ? Math.Min(b.RightEdge, edge) : b.RightEdge;
            _bands[i] = (b.Y, newL, newR);
        }
    }

    private void EnsureBandAt(float y)
    {
        int idx = BandIndexAt(y);
        if (idx >= 0 && _bands[idx].Y == y) return;

        var (_, l, r) = _bands[Math.Max(idx, 0)];
        _bands.Insert(idx >= 0 ? idx + 1 : 0, (y, l, r));
    }

    private int BandIndexAt(float y)
    {
        int best = -1;
        for (int i = 0; i < _bands.Count; i++)
        {
            if (_bands[i].Y <= y) best = i;
            else break;
        }
        return best;
    }

    public void ResetBandsBelow(float y)
    {
        _floats.RemoveAll(f => OuterBottom(f) <= y);
        RebuildBands();
    }

    // FIX: includes MarginBottom — clearance and band extent both cover the
    // full margin box (was border box only; <br clear> stopped short of a
    // vspace'd float and text overlapped its margin).
    private static float OuterBottom(LayoutBox f) =>
        f.Y + f.BorderTop + f.PaddingTop + f.Height + f.PaddingBottom + f.BorderBottom
          + f.MarginBottom;

    private void RebuildBands()
    {
        _bands.Clear();
        _bands.Add((0f, 0f, float.MaxValue));
        foreach (var f in _floats)
        {
            float top = f.Y;
            float bottom = OuterBottom(f);
            if (bottom <= top) continue;
            if (f.FloatSide == FloatValue.Left)
            {
                float edge = f.X + f.BorderLeft + f.PaddingLeft + f.Width
                           + f.PaddingRight + f.BorderRight + f.MarginRight;
                UpdateBands(top, bottom, edge, isLeft: true);
            }
            else
            {
                UpdateBands(top, bottom, f.X - f.MarginLeft, isLeft: false);
            }
        }
    }

    public float GetLeftEdge(float y)
    {
        float edge = 0f;
        foreach (var f in _floats)
        {
            bool inBand = !(y < f.Y || y >= OuterBottom(f));
            if (LayoutTrace.Enabled)
                LayoutTrace.Log($"  GetLeftEdge(y={y:F1}) checking float tag={f.Element?.TagName} " +
                    $"Y={f.Y:F1} OuterBottom={OuterBottom(f):F1} side={f.FloatSide} inBand={inBand}");
            if (!inBand) continue;
            if (f.FloatSide == FloatValue.Left)
                edge = Math.Max(edge, f.X + f.BorderLeft + f.PaddingLeft
                    + f.Width + f.PaddingRight + f.BorderRight + f.MarginRight);
        }
        LayoutTrace.Log($"  GetLeftEdge(y={y:F1}) => {edge:F1}");
        return edge;
    }

    public float GetRightEdge(float y)
    {
        float edge = float.MaxValue;
        foreach (var f in _floats)
        {
            if (y < f.Y || y >= OuterBottom(f)) continue;
            if (f.FloatSide == FloatValue.Right)
                edge = Math.Min(edge, f.X - f.MarginLeft);
        }
        return edge;
    }

    public bool HasFloats => _floats.Count > 0;

    /// <summary>DEBUG-ONLY: dumps every registered float's raw geometry, in
    /// registration order, so a Y/edge query can be checked against what was
    /// actually stored (not what AddFloat's inputs looked like at add-time).</summary>
    public string DebugDumpFloats()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in _floats)
            sb.AppendLine($"    float tag={f.Element?.TagName} side={f.FloatSide} " +
                $"X={f.X:F1} Y={f.Y:F1} W={f.Width:F1} H={f.Height:F1} " +
                $"MarginL={f.MarginLeft:F1} MarginR={f.MarginRight:F1} " +
                $"OuterBottom={OuterBottom(f):F1}");
        return sb.ToString();
    }

    public IEnumerable<float> FloatBottoms()
    {
        foreach (var f in _floats)
            yield return OuterBottom(f);
    }

    /// <summary>
    /// Returns the first bottom edge below <paramref name="fromY">fromY</paramref>
    /// of a float that is currently covering that Y band. A float wider than
    /// its containing block can make the available inline width exactly zero;
    /// in that case the next line box must begin after the float instead of
    /// being painted to its right/inside its overflow area.
    /// </summary>
    public float GetNextBlockingFloatBottom(float fromY)
    {
        float next = float.MaxValue;
        foreach (var f in _floats)
        {
            float bottom = OuterBottom(f);
            if (f.Y <= fromY + 0.01f && bottom > fromY + 0.01f)
                next = Math.Min(next, bottom);
        }
        return next;
    }

    public float GetClearY(float fromY, ClearValue clear)
    {
        float y = fromY;
        foreach (var f in _floats)
        {
            bool side = clear switch
            {
                ClearValue.Left => f.FloatSide == FloatValue.Left,
                ClearValue.Right => f.FloatSide == FloatValue.Right,
                ClearValue.Both => true,
                _ => false
            };
            if (!side) continue;
            y = Math.Max(y, OuterBottom(f));
        }
        return y;
    }
}

/// <summary>
/// The inline formatting context: fragments + line boxes using the shared SkiaSharp font metrics.
/// measurement, baseline alignment, HTML ALIGN= vertical modes, and text
/// reflow around floats.
/// </summary>
public static class InlineLayout
{
    private static readonly List<string> DefaultFontFamily = ["Times New Roman", "serif"];

    private static Render.FontCache? _fontCache;
    private static readonly Graphics _measureG;
    private const int MaxCachedTextWidths = 32768;
    private const int TextWidthCacheEvictionBatch = 4096;
    private static readonly Dictionary<TextWidthKey, float> _textWidthCache =
        new(new TextWidthKeyComparer());
    private static readonly Queue<TextWidthKey> _textWidthCacheOrder = new();

    private readonly record struct TextWidthKey(
        string Text, Font Font, float LetterSpacing, float WordSpacing, FontVariantValue Variant);

    private sealed class TextWidthKeyComparer : IEqualityComparer<TextWidthKey>
    {
        public bool Equals(TextWidthKey x, TextWidthKey y) =>
            ReferenceEquals(x.Font, y.Font) &&
            x.LetterSpacing.Equals(y.LetterSpacing) &&
            x.WordSpacing.Equals(y.WordSpacing) &&
            x.Variant == y.Variant &&
            string.Equals(x.Text, y.Text, StringComparison.Ordinal);

        public int GetHashCode(TextWidthKey key) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(key.Text),
                RuntimeHelpers.GetHashCode(key.Font),
                key.LetterSpacing,
                key.WordSpacing,
                key.Variant);
    }

    static InlineLayout()
    {
        _measureG = Graphics.CreateMeasurementContext();
        // ClearType did not exist in the 1996 target. Use grayscale
        // antialiasing so measurement and drawing match the era-oriented
        // renderer without introducing sub-pixel colour fringes.
        _measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    public static void SetFontCache(Render.FontCache fc)
    {
        if (ReferenceEquals(_fontCache, fc))
            return;

        _fontCache = fc;
        _textWidthCache.Clear();
        _textWidthCacheOrder.Clear();
    }

    private static readonly StringFormat _sf = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.None
    };

    internal static float MeasureTextWidth(string text, ComputedStyle style)
    {
        if (string.IsNullOrEmpty(text) || _fontCache == null)
            return text is null ? 0f : text.Length * 8f;

        var font = ResolveRunFont(style, text);
        var key = new TextWidthKey(
            text, font, style.LetterSpacing, style.WordSpacing, style.FontVariant);
        if (_textWidthCache.TryGetValue(key, out float cachedWidth))
            return cachedWidth;

        float width;
        if (style.FontVariant == FontVariantValue.SmallCaps)
        {
            width = MeasureSmallCapsWidth(text, style);
        }
        else if (Math.Abs(style.LetterSpacing) <= 0.001f &&
                 Math.Abs(style.WordSpacing) <= 0.001f)
        {
            var measured = _measureG.MeasureString(text, font, int.MaxValue, _sf);
            width = Math.Max(0f, measured.Width);
        }
        else
        {
            width = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                width += _measureG.MeasureString(text[i].ToString(), font, int.MaxValue, _sf).Width;
                if (i + 1 < text.Length) width += style.LetterSpacing;
                if (char.IsWhiteSpace(text[i])) width += style.WordSpacing;
            }
            width = Math.Max(0f, width);
        }

        if (_textWidthCache.Count >= MaxCachedTextWidths)
        {
            for (int i = 0; i < TextWidthCacheEvictionBatch && _textWidthCacheOrder.Count > 0; i++)
                _textWidthCache.Remove(_textWidthCacheOrder.Dequeue());
        }
        _textWidthCache[key] = width;
        _textWidthCacheOrder.Enqueue(key);
        return width;
    }

    private static float MeasureSmallCapsWidth(string text, ComputedStyle style)
    {
        var fullFont = ResolveRunFont(style);
        float smallSize = Math.Max(1f, style.FontSize * 0.80f);
        var family = style.FontFamily is { Count: > 0 } ? style.FontFamily : DefaultFontFamily;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        var smallFont = _fontCache!.Resolve(family, smallSize, (int)style.FontWeight, italic, oblique);

        float width = 0f;
        int i = 0;
        while (i < text.Length)
        {
            bool lower = char.IsLetter(text[i]) && char.IsLower(text[i]);
            int start = i++;
            while (i < text.Length)
            {
                bool nextLower = char.IsLetter(text[i]) && char.IsLower(text[i]);
                if (nextLower != lower) break;
                i++;
            }

            string run = text[start..i];
            string draw = lower ? run.ToUpperInvariant() : run;
            var runFont = lower
                ? _fontCache!.ResolveForText(family, smallSize, (int)style.FontWeight,
                    italic, oblique, draw)
                : fullFont;
            width += _measureG.MeasureString(draw, runFont, int.MaxValue, _sf).Width;
        }

        if (text.Length > 1) width += Math.Max(0f, text.Length - 1) * style.LetterSpacing;
        width += text.Count(c => char.IsWhiteSpace(c)) * style.WordSpacing;
        return Math.Max(0f, width);
    }

    private static Font ResolveRunFont(ComputedStyle style)
    {
        var family = style.FontFamily is { Count: > 0 } ? style.FontFamily : DefaultFontFamily;
        float size = style.FontSize > 0f ? style.FontSize : 16f;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        return _fontCache!.Resolve(family, size, (int)style.FontWeight, italic, oblique);
    }

    private static Font ResolveRunFont(ComputedStyle style, string text)
    {
        var family = style.FontFamily is { Count: > 0 } ? style.FontFamily : DefaultFontFamily;
        float size = style.FontSize > 0f ? style.FontSize : 16f;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        bool oblique = style.FontStyle == FontStyleValue.Oblique;
        return _fontCache!.ResolveForText(family, size, (int)style.FontWeight,
            italic, oblique, text);
    }

    private static void ApplyBdoOverrides(List<LayoutBox> source)
    {
        int index = 0;
        while (index < source.Count)
        {
            var bdo = FindBdoOverride(source[index].Element);
            if (bdo == null)
            {
                index++;
                continue;
            }

            int end = index + 1;
            while (end < source.Count &&
                   ReferenceEquals(FindBdoOverride(source[end].Element), bdo))
                end++;

            int left = index;
            int right = end - 1;
            while (left <= right)
            {
                if (source[left].TextRun is { Length: > 0 } leftText)
                    source[left].TextRun = ReverseTextElements(leftText);
                if (left != right && source[right].TextRun is { Length: > 0 } rightText)
                    source[right].TextRun = ReverseTextElements(rightText);
                (source[left], source[right]) = (source[right], source[left]);
                left++;
                right--;
            }

            index = end;
        }
    }

    private static DomElement? FindBdoOverride(DomElement? element)
    {
        for (var current = element; current != null; current = current.Parent as DomElement)
        {
            if (current.TagName == "bdo" &&
                current.Style?.UnicodeBidi == "bidi-override" &&
                current.Style.Direction == DirectionValue.Rtl)
                return current;
        }
        return null;
    }

    /// <summary>
    /// Shared auto-size math for form controls, used BOTH when the layout
    /// tree is built (so TableLayout's column pass sees the real width and
    /// an input never sizes its column at the 150px default only to be
    /// label-resized wider at line time — the "input comes out of the
    /// table" bug) and when the line measures the box.
    /// Explicit WIDTH/HEIGHT attributes are respected by the caller.
    /// Returns false when no font cache is available (box defaults stand).
    /// </summary>
    public static bool ControlNaturalSize(DomElement el, ComputedStyle? style,
                                          out float width, out float height)
    {
        width = 0f;
        height = 0f;
        if (style == null || _fontCache == null)
            return false;

        var font = ResolveRunFont(style);
        height = Math.Max(22f, font.GetHeight(_measureG) + 10f);

        switch (el.TagName)
        {
            case "input":
                {
                    string type = el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
                    if (type == "hidden")
                        return false;          // no natural size — occupies nothing
                    if (type == "image")
                        return false;          // FIX: SRC-driven, no label/natural size — used to return true with width 0
                    if (type is "submit" or "reset" or "button")
                    {
                        string label = type switch
                        {
                            "submit" => el.GetAttr("value") ?? "Submit Query",
                            "reset" => el.GetAttr("value") ?? "Reset",
                            _ => el.GetAttr("value") ?? "Button"
                        };
                        width = Math.Max(60f, MeasureTextWidth(label, style) + 24f);
                    }
                    else if (type is "text" or "password")
                    {
                        int size = el.GetAttrInt("size", 0);
                        if (size > 0)
                            width = MeasureTextWidth(new string('0', size), style) + 12f;
                    }
                    else if (type == "file")
                    {
                        int size = el.GetAttrInt("size", 0);
                        float legacyWidth = size > 0
                            ? MeasureTextWidth(new string('0', size), style) + 12f
                            : 0f;
                        float buttonWidth = Math.Max(92f,
                            MeasureTextWidth("Choose File", style) + 24f);
                        // Keep a little more room for the browser-visible file
                        // name. The native Choose File button is fixed, so the
                        // extra width belongs to the filename display area.
                        float labelWidth = Math.Max(132f,
                            MeasureTextWidth("No file chosen", style) + 14f);
                        width = Math.Max(
                            legacyWidth,
                            buttonWidth + labelWidth + 12f);
                    }
                    else if (type is "checkbox" or "radio")
                    {
                        width = 16f;
                        height = 16f;
                    }
                    return true;
                }

            case "button":
                {
                    float contentWidth = MeasureButtonContentWidth(el, style);
                    if (contentWidth <= 0f)
                    {
                        string label = el.GetAttr("value") ?? "Button";
                        contentWidth = MeasureTextWidth(label, style);
                    }
                    width = Math.Max(60f, contentWidth + 24f);
                    return true;
                }

            case "select":
                {
                    // OPTGROUP-aware row model: group header labels are part
                    // of the control's visible content and options inside a
                    // group are indented — the natural width must reserve
                    // room for both (Task 9).
                    float longest = 40f;
                    foreach (var row in SelectRowModel.Build(el))
                    {
                        if (row.Label.Length == 0) continue;
                        longest = Math.Max(longest,
                            MeasureTextWidth(Render.GlyphSubstitution.MapGlyphs(
                                row.Label), style) + row.Indent);
                    }
                    width = Math.Max(60f, longest + 36f);
                    int visibleRows = Math.Max(1, el.GetAttrInt("size", 1));
                    if (el.HasAttr("multiple") && visibleRows == 1)
                        visibleRows = 4;
                    height = (float)Math.Ceiling(
                        ResolveRunFont(style).GetHeight(_measureG) + 2f) * visibleRows + 4f;
                    return true;
                }

            case "textarea":
                {
                    int cols = el.GetAttrInt("cols", 0);
                    if (cols > 0)
                        width = cols * 8f;
                    int rows = Math.Max(1, el.GetAttrInt("rows", 4));
                    height = (float)Math.Ceiling(font.GetHeight(_measureG)) * rows + 4f;
                    return true;
                }
        }

        return false;
    }

    private static float MeasureButtonContentWidth(DomElement button, ComputedStyle fallbackStyle)
    {
        float width = 0f;
        bool previousWasSpace = true;
        void Visit(DomNode node, ComputedStyle inheritedStyle)
        {
            if (node is DomText textNode)
            {
                var normalized = new System.Text.StringBuilder();
                foreach (char c in textNode.Data)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        if (!previousWasSpace)
                        {
                            normalized.Append(' ');
                            previousWasSpace = true;
                        }
                    }
                    else
                    {
                        normalized.Append(c);
                        previousWasSpace = false;
                    }
                }

                if (normalized.Length > 0)
                {
                    string text = Render.GlyphSubstitution.MapGlyphs(normalized.ToString());
                    width += ResolveRunFont(inheritedStyle).SkFont.MeasureText(text);
                }
                return;
            }

            if (node is DomElement element)
                inheritedStyle = element.Style ?? inheritedStyle;
            foreach (var child in node.Children)
                Visit(child, inheritedStyle);
        }

        Visit(button, button.Style ?? fallbackStyle);
        return width;
    }

    private enum VAlignMode { Baseline, Top, Middle, Bottom }

    private readonly record struct MeasuredItem(
        LayoutBox Box, float W, float H, float Asc, float ContentHeight, float LeadingTop, VAlignMode VA,
        bool Atomic, float MarginL, float MarginR);

    private static VAlignMode GetVAlignMode(LayoutBox box)
    {
        var style = box.Element?.Style;
        if (style?.OwnVerticalAlign == true)
        {
            if (style.VerticalAlignPercent.HasValue)
                return VAlignMode.Baseline;
            return style.VerticalAlign switch
            {
                VerticalAlign.Top or VerticalAlign.TextTop => VAlignMode.Top,
                VerticalAlign.Middle => VAlignMode.Middle,
                VerticalAlign.Bottom or VerticalAlign.TextBottom => VAlignMode.Bottom,
                _ => VAlignMode.Baseline
            };
        }

        var align = box.Element?.GetAttr("align")?.Trim().ToLowerInvariant();
        return align switch
        {
            "top" => VAlignMode.Top,
            "texttop" => VAlignMode.Top,
            "middle" => VAlignMode.Middle,
            "center" => VAlignMode.Middle,
            "absmiddle" => VAlignMode.Middle,
            "absbottom" => VAlignMode.Bottom,
            "baseline" => VAlignMode.Baseline,
            "bottom" => VAlignMode.Baseline,   // HTML 3.2 bottom = on the baseline
            _ => VAlignMode.Baseline
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Entry point
    // ─────────────────────────────────────────────────────────────────────

    public static float Layout(
        IReadOnlyList<LayoutBox> inlineChildren,
        float containerWidth,
        float containerX,
        float startY,
        ComputedStyle containerStyle,
        FloatContext? floats)
    {
        if (inlineChildren == null || inlineChildren.Count == 0)
            return 0f;

        LayoutTrace.Log($"InlineLayout.Layout ENTER containerX={containerX:F1} " +
            $"startY={startY:F1} containerWidth={containerWidth:F1} " +
            $"hasFloats={floats?.HasFloats} itemCount={inlineChildren.Count} " +
            $"firstItemTag={inlineChildren[0].Element?.TagName} " +
            $"firstItemText=\"{Truncate(inlineChildren[0].TextRun)}\"");

        var source = inlineChildren.ToList();
        ApplyBdoOverrides(source);
        floats ??= new FloatContext();

        // ── Fragment + measure, collapsing adjacent spaces across run
        //    boundaries. Line-leading spaces are dropped at wrap time.
        var items = new List<MeasuredItem>(source.Count);
        bool lastWasSpace = true;   // stream start: no leading space
        foreach (var box in source)
        {
            foreach (var prepared in ApplyFirstLetter(box))
            {
                if (prepared.IsFloated)
                {
                    MeasureBox(prepared, out float floatWidth, out float floatHeight,
                        out _, out _, out _, prepared.StyleOverride);
                    prepared.Width = floatWidth;
                    prepared.Height = floatHeight;
                    prepared.Y = startY + prepared.MarginTop;

                    float leftEdge = Math.Max(containerX, floats.GetLeftEdge(prepared.Y));
                    float rightEdge = floats.GetRightEdge(prepared.Y);
                    if (rightEdge == float.MaxValue)
                        rightEdge = containerX + containerWidth;
                    rightEdge = Math.Min(rightEdge, containerX + containerWidth);
                    prepared.X = prepared.FloatSide == FloatValue.Right
                        ? rightEdge - floatWidth - prepared.MarginRight
                        : leftEdge + prepared.MarginLeft;
                    floats.AddFloat(prepared);
                    continue;
                }

                foreach (var frag in FragmentTextBox(prepared))
                {
                    bool isSpace = frag.TextRun == " ";
                    if (isSpace && lastWasSpace)
                    {
                        // Collapsed duplicate space (or a space at stream
                        // start): never rendered, but the box stays in the
                        // tree for selection/copy — anchor it to the
                        // container origin instead of a stale (0,0,0,0).
                        frag.X = containerX;
                        frag.Y = startY;
                        continue;
                    }
                    MeasureBox(frag, out float w, out float h, out float asc,
                        out float contentHeight, out float leadingTop);
                    bool atomic = frag.Element?.TagName == "spacer"
                                  || frag.BoxType is BoxType.Replaced or BoxType.InlineBlock or BoxType.Frame
                                  || frag.ReplacedImage != null;
                    items.Add(new MeasuredItem(frag, w, h, asc, contentHeight, leadingTop,
                        GetVAlignMode(frag), atomic,
                        atomic ? frag.MarginLeft : 0f,
                        atomic ? frag.MarginRight : 0f));
                    lastWasSpace = isSpace;
                }
            }
        }

        bool bidiOverride = containerStyle.Direction == DirectionValue.Rtl &&
            string.Equals(containerStyle.UnicodeBidi, "bidi-override", StringComparison.OrdinalIgnoreCase);
        if (bidiOverride)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Box.TextRun is not { Length: > 0 } text)
                    continue;
                item.Box.TextRun = ReverseTextElements(text);
                MeasureBox(item.Box, out float width, out float height, out float ascent,
                    out float contentHeight, out float leadingTop);
                items[i] = item with
                {
                    W = width,
                    H = height,
                    Asc = ascent,
                    ContentHeight = contentHeight,
                    LeadingTop = leadingTop
                };
            }
            items.Reverse();
        }

        // Inline non-replaced elements contribute horizontal margins at
        // their outer edges, not once per word fragment.
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var element = item.Box.Element;
            var style = element?.Style;
            if (item.Atomic || style?.Display != DisplayValue.Inline)
                continue;

            bool firstFragment = i == 0 || items[i - 1].Box.Element != element;
            bool lastFragment = i == items.Count - 1 || items[i + 1].Box.Element != element;
            float marginLeft = firstFragment
                ? style.MarginLeft + (style.MarginLeftPercent ?? 0f) * containerWidth / 100f
                : item.MarginL;
            float marginRight = lastFragment
                ? style.MarginRight + (style.MarginRightPercent ?? 0f) * containerWidth / 100f
                : item.MarginR;
            if (firstFragment || lastFragment)
                items[i] = item with { MarginL = marginLeft, MarginR = marginRight };
        }

        float currentY = startY;
        float availW = containerWidth;
        bool firstLine = true;
        bool nowrap = containerStyle.WhiteSpace is WhiteSpaceValue.Nowrap
                                                or WhiteSpaceValue.Pre;

        // If a float is wider than the current containing block, its left/right
        // exclusion edges can leave zero inline width. Do not flush text at
        // that Y: a line box with no usable width would otherwise be placed at
        // the float's right edge and paint directly over/alongside the image.
        // Instead advance to the next bottom edge of the blocking float, which
        // matches normal float overflow behaviour without changing ordinary
        // wrapping around floats that still leave usable width.
        void RecomputeAvailableWidth()
        {
            float rawAvail = GetAvailableWidth(containerX, currentY, containerWidth, floats);
            if (rawAvail <= 0.01f && floats != null && floats.HasFloats)
            {
                float next = floats.GetNextBlockingFloatBottom(currentY);
                if (next < float.MaxValue && next > currentY + 0.01f)
                    currentY = next;
                rawAvail = GetAvailableWidth(containerX, currentY, containerWidth, floats);
            }

            availW = rawAvail;
            if (firstLine)
                availW = Math.Max(0f, availW - ResolveTextIndent(containerStyle, containerWidth));
        }

        RecomputeAvailableWidth();
        LayoutTrace.Log($"  initial availW at y={currentY:F1} => {availW:F1} " +
            $"(containerWidth={containerWidth:F1})");

        var lineItems = new List<MeasuredItem>();
        float lineW = 0f;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            ComputedStyle? firstLineStyle = null;
            if (firstLine)
            {
                firstLineStyle = GetFirstLineStyle(it.Box, containerStyle);
                if (firstLineStyle != null) it = Remeasure(it, firstLineStyle);
            }

            // Leading spaces are dropped ONLY while the line is still empty.
            // The old flag-based version left skipLeadingSpaces=true after a
            // WORD-triggered wrap, so the space AFTER the first word of the
            // new line was eaten — "the problem" wrapped as "theproblem",
            // "case-sensitive on" as "case-sensitiveon".
            if (lineItems.Count == 0 && it.Box.TextRun == " ")
            {
                // Leading spaces on an empty line vanish from the painted
                // line, but they stay in the box tree for selection/copy —
                // give them sane geometry instead of a stale (0,0,0,0)
                // rect, the same contract wrap-dropped spaces follow.
                it.Box.X = containerX;
                it.Box.Y = currentY;
                it.Box.Width = 0f;
                it.Box.Height = it.H;
                continue;
            }

            if (it.Box.Element?.TagName == "br")
            {
                float brH = it.H > 0 ? it.H :
                            (lineItems.Count > 0 ? lineItems[^1].H : 16f);
                bool hadLineContent = lineItems.Count > 0;

                currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                      containerWidth, containerStyle, floats,
                                      justifyLine: true, firstLine);
                firstLine = false;
                lineItems.Clear();
                lineW = 0f;

                var clear = GetBrClear(it.Box.Element);
                if (clear != ClearValue.None && floats != null && floats.HasFloats)
                {
                    currentY = Math.Max(currentY, floats.GetClearY(currentY, clear));
                    floats.ResetBandsBelow(currentY);
                }

                it.Box.X = containerX;
                it.Box.Y = currentY;
                it.Box.Width = 0f;
                it.Box.Height = brH;
                // FlushLine already advances past a non-empty line.  Adding
                // brH as well double-counted the line after controls such as
                // <input><br><input>, leaving an extra vertical gap.
                if (!hadLineContent)
                    currentY += brH;
                RecomputeAvailableWidth();
                continue;
            }

            // Form controls shrink to the containing width (era behaviour:
            // an input too wide for its cell never spills out of the table).
            // Explicit WIDTH attributes still win — only auto-sized
            // controls clamp.  Images are exempt (broken-as-laid-out beats
            // silently resized art).
            if (it.Atomic && it.Box.Element is { } ctl &&
                ctl.TagName is "input" or "select" or "textarea" or "button" &&
                !ctl.HasAttr("width") &&
                availW > 40f &&
                it.W + it.MarginL + it.MarginR > availW)
            {
                float over = it.W + it.MarginL + it.MarginR - availW;
                float newW = Math.Max(24f, it.W - over);
                if (it.Box.Width > 0f)
                    it.Box.Width = newW;
                it = it with { W = newW };
            }

            if (it.Atomic && it.Box.Element?.TagName == "img" &&
                IsInsideTableCell(it.Box) && availW > 1f &&
                it.W + it.MarginL + it.MarginR > availW)
            {
                float horizontalChrome = it.W - it.Box.Width;
                float verticalChrome = it.H - it.Box.Height;
                float oldContentW = Math.Max(1f, it.Box.Width);
                float newContentW = Math.Max(1f,
                    availW - it.MarginL - it.MarginR - horizontalChrome);
                float scale = newContentW / oldContentW;
                float newContentH = Math.Max(1f, it.Box.Height * scale);
                it.Box.Width = newContentW;
                it.Box.Height = newContentH;
                it = it with
                {
                    W = newContentW + horizontalChrome,
                    H = newContentH + verticalChrome,
                    Asc = newContentH + verticalChrome
                };
            }

            float outerW = it.MarginL + it.W + it.MarginR;

            if (!nowrap && lineItems.Count == 0 && !it.Atomic &&
                !string.IsNullOrEmpty(it.Box.TextRun) &&
                it.W > availW && IsInsideTableCell(it.Box))
            {
                var pieces = SplitOversizedText(it.Box);
                if (pieces.Count > 1)
                {
                    items.RemoveAt(i);
                    items.InsertRange(i, pieces);
                    it = items[i];
                    outerW = it.MarginL + it.W + it.MarginR;
                }
            }

            bool keepSearchSubmitTogether = IsSubmitFollowingSearchField(it, lineItems);
            if (!nowrap && lineItems.Count > 0 && lineW + outerW > availW &&
                !keepSearchSubmitTogether)
            {
                LayoutTrace.Log($"  WRAP at y={currentY:F1}: lineW={lineW:F1} + outerW={outerW:F1} " +
                    $"> availW={availW:F1} (word=\"{Truncate(it.Box.TextRun)}\")");
                currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                      containerWidth, containerStyle, floats,
                                      justifyLine: true, firstLine);
                firstLine = false;
                lineItems.Clear();
                lineW = 0f;
                RecomputeAvailableWidth();
                if (firstLineStyle != null)
                {
                    it = Remeasure(it, null);
                    outerW = it.MarginL + it.W + it.MarginR;
                }
                LayoutTrace.Log($"  new availW at y={currentY:F1} => {availW:F1}");
                if (it.Box.TextRun == " ")
                {
                    // The space that caused the wrap vanishes from the line
                    // — but keep its box geometry sane (it stays in the tree
                    // for selection/copy) instead of a stale (0,0,0,0) rect.
                    it.Box.X = containerX;
                    it.Box.Y = currentY;
                    it.Box.Width = 0f;
                    it.Box.Height = it.H;
                    continue;
                }
            }

            if (lineItems.Count > 0) lineW += InterItemLetterSpacing(lineItems[^1].Box, it.Box);
            lineItems.Add(it);
            lineW += outerW;
        }

        if (lineItems.Count > 0)
            currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                  containerWidth, containerStyle, floats,
                                  justifyLine: false, firstLine);

        return Math.Max(0f, currentY - startY);
    }

    private static string ReverseTextElements(string text)
    {
        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var result = new System.Text.StringBuilder(text.Length);
        for (int i = starts.Length - 1; i >= 0; i--)
        {
            int end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            result.Append(text, starts[i], end - starts[i]);
        }
        return result.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fragmentation
    // ─────────────────────────────────────────────────────────────────────

    private static bool IsInsideTableCell(LayoutBox box)
    {
        for (var ancestor = box.Parent; ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.BoxType == BoxType.TableCell)
                return true;
        return false;
    }

    private static bool IsSubmitFollowingSearchField(
        MeasuredItem candidate, IReadOnlyList<MeasuredItem> precedingItems)
    {
        var submit = candidate.Box.Element;
        if (submit?.TagName != "input" ||
            !submit.GetAttrOrDefault("type", "text").Trim()
                .Equals("submit", StringComparison.OrdinalIgnoreCase))
            return false;

        DomElement? query = null;
        for (int i = precedingItems.Count - 1; i >= 0; i--)
        {
            var box = precedingItems[i].Box;
            if (box.TextRun == " ")
                continue;
            query = box.Element;
            break;
        }

        if (query?.TagName != "input")
            return false;

        string queryType = query.GetAttrOrDefault("type", "text").Trim();
        if (!queryType.Equals("text", StringComparison.OrdinalIgnoreCase) &&
            !queryType.Equals("search", StringComparison.OrdinalIgnoreCase))
            return false;

        DomElement? queryForm = GetAncestorForm(query);
        return queryForm != null && ReferenceEquals(queryForm, GetAncestorForm(submit));
    }

    private static DomElement? GetAncestorForm(DomElement element)
    {
        for (var node = element.Parent; node != null; node = node.Parent)
            if (node is DomElement ancestor && ancestor.TagName == "form")
                return ancestor;
        return null;
    }

    private static List<MeasuredItem> SplitOversizedText(LayoutBox box)
    {
        var parent = box.Parent;
        if (parent == null) return [];

        int index = parent.Children.IndexOf(box);
        if (index < 0) return [];

        var pieces = new List<LayoutBox>();
        foreach (char ch in box.TextRun!)
            pieces.Add(new LayoutBox(box.Element, BoxType.Inline)
            {
                TextRun = ch.ToString(),
                Parent = parent
            });

        parent.Children.RemoveAt(index);
        parent.Children.InsertRange(index, pieces);

        var measured = new List<MeasuredItem>(pieces.Count);
        foreach (var piece in pieces)
        {
            MeasureBox(piece, out float width, out float height, out float ascent,
                out float contentHeight, out float leadingTop);
            measured.Add(new MeasuredItem(piece, width, height, ascent, contentHeight, leadingTop,
                GetVAlignMode(piece), false, 0f, 0f));
        }
        return measured;
    }

    private static MeasuredItem Remeasure(MeasuredItem item, ComputedStyle? styleOverride)
    {
        MeasureBox(item.Box, out float width, out float height, out float ascent,
            out float contentHeight, out float leadingTop, styleOverride);
        return item with
        {
            W = width, H = height, Asc = ascent,
            ContentHeight = contentHeight, LeadingTop = leadingTop
        };
    }

    private static bool RunIsUnbreakable(DomElement? elem)
    {
        for (var node = (DomNode?)elem; node != null; node = node.Parent)
        {
            if (node is DomElement de &&
                de.Style?.WhiteSpace is WhiteSpaceValue.Pre or WhiteSpaceValue.Nowrap)
                return true;

        }
        return false;
    }

    private static List<LayoutBox> FragmentTextBox(LayoutBox box)
    {
        string text = box.TextRun ?? "";
        if (text.IndexOf(' ') < 0 || RunIsUnbreakable(box.Element))
            return new List<LayoutBox> { box };

        var parent = box.Parent;
        int idx = parent != null ? parent.Children.IndexOf(box) : -1;
        if (idx < 0)
            return new List<LayoutBox> { box };

        var frags = new List<LayoutBox>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            int sp = text.IndexOf(' ', i);
            int end = sp < 0 ? n : sp;

            if (end > i)
            {
                frags.Add(new LayoutBox(box.Element, BoxType.Inline)
                {
                    TextRun = text[i..end],
                    Parent = parent!,
                    StyleOverride = box.StyleOverride
                });
            }
            if (sp < 0) break;

            frags.Add(new LayoutBox(box.Element, BoxType.Inline)
            {
                TextRun = " ",
                Parent = parent!,
                StyleOverride = box.StyleOverride
            });
            i = sp + 1;
        }

        if (frags.Count == 0)
            return new List<LayoutBox> { box };

        parent!.Children.RemoveAt(idx);
        // FIX: single InsertRange — the one-by-one insert loop was O(n²)
        // on long paragraphs (each Insert shifts the tail).
        parent.Children.InsertRange(idx, frags);

        return frags;
    }

    private static ClearValue GetBrClear(DomElement? elem)
    {
        var clear = elem?.GetAttr("clear")?.Trim().ToLowerInvariant();
        return clear switch
        {
            "left" => ClearValue.Left,
            "right" => ClearValue.Right,
            "all" => ClearValue.Both,
            "both" => ClearValue.Both,
            _ => ClearValue.None
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Line flushing
    // ─────────────────────────────────────────────────────────────────────

    private static float FlushLine(
        List<MeasuredItem> items,
        float containerX, float y,
        float lineW, float containerWidth,
        ComputedStyle containerStyle,
        FloatContext? floats,
        bool justifyLine,
        bool firstLine)
    {
        if (items.Count == 0) return 0f;

        float maxAscent = 0f, maxDescent = 0f;
        float minLeadingTop = float.MaxValue;
        foreach (var it in items)
        {
            maxAscent = Math.Max(maxAscent, it.Asc);
            maxDescent = Math.Max(maxDescent, it.H - it.Asc);
            minLeadingTop = Math.Min(minLeadingTop, it.LeadingTop);
        }
        float lineH = Math.Max(maxAscent + maxDescent, 1f);
        float unshiftedLineH = lineH;
        foreach (var it in items)
        {
            if (it.VA != VAlignMode.Baseline) continue;
            float shift = GetBaselineShift(it, lineH);
            maxAscent = Math.Max(maxAscent, it.Asc + shift);
            maxDescent = Math.Max(maxDescent, it.H - it.Asc - shift);
        }
        lineH = Math.Max(maxAscent + maxDescent, 1f);

        float containerRight = containerX + containerWidth;
        float leftEdge = containerX;
        float rightEdge = containerRight;
        if (floats != null && floats.HasFloats)
        {
            leftEdge = Math.Max(containerX, floats.GetLeftEdge(y));
            float re = floats.GetRightEdge(y);
            if (re != float.MaxValue)
                rightEdge = Math.Min(rightEdge, re);
        }
        LayoutTrace.Log($"FlushLine y={y:F1} firstLine={firstLine} containerX={containerX:F1} " +
            $"=> leftEdge={leftEdge:F1} rightEdge={rightEdge:F1} lineW={lineW:F1} " +
            $"firstWord=\"{Truncate(items.Count > 0 ? items[0].Box.TextRun : null)}\"");

        if (firstLine) leftEdge += ResolveTextIndent(containerStyle, containerWidth);
        float x = leftEdge;
        float extra = 0f;

        TextAlign alignment = containerStyle.TextAlign;
        if (alignment == TextAlign.Left &&
            containerStyle.Direction == DirectionValue.Rtl &&
            string.Equals(containerStyle.UnicodeBidi, "bidi-override", StringComparison.OrdinalIgnoreCase))
            alignment = TextAlign.Right;

        switch (alignment)
        {
            case TextAlign.Center:
                x += Math.Max(0f, (rightEdge - leftEdge - lineW) / 2f);
                break;
            case TextAlign.Right:
                x += Math.Max(0f, rightEdge - leftEdge - lineW);
                break;
            case TextAlign.Justify when justifyLine && items.Count > 1:
                int gaps = 0;
                for (int i = 0; i < items.Count - 1; i++)
                    if (items[i].Box.TextRun != " " &&
                        (items[i].W > 0.01f || items[i].H > 0.01f)) gaps++;
                if (gaps > 0)
                    extra = Math.Max(0f, (rightEdge - leftEdge - lineW) / gaps);
                break;
        }

        Retro96.DebugLog.Write($"[flushdbg] y={y:F1} textAlign={containerStyle.TextAlign} " +
            $"leftEdge={leftEdge:F1} rightEdge={rightEdge:F1} lineW={lineW:F1} " +
            $"computed_x={x:F1} firstWord=\"{Truncate(items.Count > 0 ? items[0].Box.TextRun : null)}\"");

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var box = it.Box;

            if (i > 0) x += InterItemLetterSpacing(items[i - 1].Box, box);

            if (firstLine &&
                GetFirstLineStyle(box, containerStyle) is { } firstLineStyle)
                box.StyleOverride = firstLineStyle;

            box.X = x + it.MarginL;
            if (!it.Atomic)
            {
                box.Width = it.W;
                box.Height = it.H;
            }

            switch (it.VA)
            {
                case VAlignMode.Top:
                    box.Y = y + minLeadingTop - it.LeadingTop;
                    break;
                case VAlignMode.Middle:
                    box.Y = y + (unshiftedLineH - it.ContentHeight) / 2f - it.LeadingTop;
                    break;
                case VAlignMode.Bottom:
                    box.Y = y + unshiftedLineH - minLeadingTop - it.ContentHeight - it.LeadingTop;
                    break;
                default:
                    box.Y = y + maxAscent - it.Asc - GetBaselineShift(it, lineH);
                    break;
            }

            // 1996 raster text: every run sits on a whole pixel row.  A
            // fractional box.Y makes ClearTypeGridFit round each word's
            // draw origin independently — mixed-font lines (Arial word
            // next to a Courier word) then land a pixel apart even though
            // the baseline math is exact.
            if (!it.Atomic)
                box.Y = MathF.Round(box.Y);

            x += it.MarginL + it.W + it.MarginR;
            // Word-spacing only BETWEEN visible items — a zero-footprint
            // item (hidden input) is not a word and must not eat a gap.
            if (i < items.Count - 1 && it.Box.TextRun != " " &&
                (it.W > 0.01f || it.H > 0.01f || it.Asc > 0.01f))
                x += extra;
        }

        return lineH;
    }

    private static float GetBaselineShift(MeasuredItem item, float lineHeight)
    {
        var box = item.Box;
        var style = box.StyleOverride ?? box.Element?.Style;
        float shift = 0f;
        if (style?.VerticalAlign == VerticalAlign.Super || box.Element?.TagName == "sup")
            shift += item.H * 0.35f;
        else if (style?.VerticalAlign == VerticalAlign.Sub || box.Element?.TagName == "sub")
            shift -= item.H * 0.25f;
        if (style?.VerticalAlignPercent is { } percent)
            shift += lineHeight * percent / 100f;
        if (style?.VerticalAlignLength is { } length)
            shift += length;
        return shift;
    }

    private static float ResolveTextIndent(ComputedStyle style, float containingWidth) =>
        style.TextIndentPercent.HasValue
            ? containingWidth * style.TextIndentPercent.Value / 100f
            : style.TextIndent;

    private static float InterItemLetterSpacing(LayoutBox previous, LayoutBox current)
    {
        if (string.IsNullOrEmpty(previous.TextRun) || string.IsNullOrEmpty(current.TextRun))
            return 0f;
        var style = current.StyleOverride ?? current.Element?.Style;
        return style?.LetterSpacing ?? 0f;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Measurement
    // ─────────────────────────────────────────────────────────────────────

    private static void MeasureBox(LayoutBox box,
        out float width, out float height, out float ascent,
        out float contentHeight, out float leadingTop,
        ComputedStyle? styleOverride = null)
    {
        // Atomic inline boxes (images, form controls, inline-blocks, frames)
        // and Netscape <spacer type=horizontal>.  SPACER is an empty element,
        // so a plain Inline box would otherwise fall through to the empty/text
        // path and measure as 0px even though GenerateBoxes recorded its width.
        if (box.Element?.TagName == "spacer" ||
            box.BoxType is BoxType.Replaced or BoxType.InlineBlock or BoxType.Frame
            || box.ReplacedImage != null)
        {
            var el = box.Element;

            // HIDDEN inputs take no space at all — no chrome, no
            // line-height contribution, no phantom word gap.
            if (el != null && el.TagName == "input" &&
                el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant() == "hidden")
            {
                width = 0f; height = 0f; ascent = 0f;
                contentHeight = 0f; leadingTop = 0f;
                return;
            }

            float cw = box.Width > 0 ? box.Width : (box.ReplacedImage?.Width ?? 0);
            float ch = box.Height > 0 ? box.Height : (box.ReplacedImage?.Height ?? 0);

            // ── Label-driven auto-sizing, WRITTEN BACK TO THE BOX.
            //    Explicit WIDTH/HEIGHT attributes still win.
            //    Shared with GenerateBoxes so the table column pass and the
            //    line pass can never disagree about a control's size.
            if (el != null && ControlNaturalSize(el, el.Style, out float nw, out float nh))
            {
                if (nw > 0f && !el.HasAttr("width") && !box.ShrinkToFitCell)
                    box.Width = cw = Math.Max(cw, nw);
                if (nh > 0f && !el.HasAttr("height")) box.Height = ch = Math.Max(ch, nh);
            }

            width = cw + box.BorderLeft + box.BorderRight + box.PaddingLeft + box.PaddingRight;
            height = ch + box.BorderTop + box.BorderBottom + box.PaddingTop + box.PaddingBottom;
            contentHeight = height;
            leadingTop = 0f;

            // Baseline placement.  Images sit bottom-on-baseline (the
            // standard inline convention).  FORM CONTROLS straddle it —
            // roughly 2/3 above, 1/3 below — the way the period browsers
            // drew their bevelled fields.
            bool isFormControl = el != null &&
                el.TagName is "input" or "select" or "button" or "textarea";
            ascent = isFormControl ? height * 0.68f : height;
            return;
        }

        // Text run — measured with the same hint and format the renderer
        // draws with, spaces glued to its word.
        if (!string.IsNullOrEmpty(box.TextRun))
        {
            var style = styleOverride ?? box.StyleOverride ?? box.Element?.Style;
            if (_fontCache != null && style != null)
            {
                var measuredText = TransformText(box.TextRun, style.TextTransform);
                var font = ResolveRunFont(style, measuredText);
                width = MeasureTextWidth(measuredText, style);
                contentHeight = font.GetHeight(_measureG);

                height = style.LineHeightMode switch
                {
                    LineHeightMode.Number => (float)Math.Ceiling(Math.Max(0f, style.LineHeight) * style.FontSize),
                    LineHeightMode.Absolute => (float)Math.Ceiling(Math.Max(0f, style.LineHeightPixels)),
                    _ => (float)Math.Ceiling(contentHeight)
                };
                leadingTop = Math.Max(0f, (height - contentHeight) / 2f);


                // FIX: guard the em-unit ratio — GetLineSpacing can return 0
                // for broken/abstract font families → NaN ascent → every
                // box on the line lands at NaN Y.
                float cellH = font.FontFamily.GetLineSpacing(font.Style);
                float ascentU = font.FontFamily.GetCellAscent(font.Style);
                float naturalAscent = cellH > 0f
                    ? font.GetHeight(_measureG) * (ascentU / cellH)
                    : font.GetHeight(_measureG) * 0.8f;
                ascent = naturalAscent + leadingTop;
                return;
            }

            float fSize = style?.FontSize > 0f ? style.FontSize : 16f;
            width = TransformText(box.TextRun!, style?.TextTransform ?? TextTransform.None).Length
                * fSize * 0.55f;
            contentHeight = fSize * 1.2f;
            height = style?.LineHeightMode switch
            {
                LineHeightMode.Number => Math.Max(0f, style.LineHeight * style.FontSize),
                LineHeightMode.Absolute => Math.Max(0f, style.LineHeightPixels),
                _ => contentHeight
            };
            leadingTop = Math.Max(0f, (height - contentHeight) / 2f);
            ascent = fSize * 0.85f + leadingTop;
            return;
        }

        // Empty / unknown (<br>, <wbr>)
        width = 0f; height = 0f; ascent = 0f;
        contentHeight = 0f; leadingTop = 0f;
    }

    private static ComputedStyle? GetFirstLineStyle(LayoutBox box, ComputedStyle containerStyle)
    {
        var element = box.Element;
        if (box.StyleOverride != null || string.IsNullOrEmpty(box.TextRun) ||
            element?.Style is not { } elementStyle)
            return null;

        if (elementStyle.FirstLineStyle is { } ownFirstLineStyle &&
            (ReferenceEquals(elementStyle, containerStyle) ||
             ReferenceEquals(box.Parent?.Element, element)))
            return ownFirstLineStyle;

        // Inline descendants (notably links) keep their own element style,
        // while their text boxes are parented by the containing block's box.
        // Apply the block's first-line weight to that inherited inline style
        // without replacing link-specific color, decoration, or other styles.
        for (var parent = box.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.Element?.Style?.FirstLineStyle is not { } parentFirstLineStyle)
                continue;

            if (elementStyle.OwnFontWeight)
                return null;

            var inlineFirstLineStyle = elementStyle.Clone();
            inlineFirstLineStyle.FontWeight = parentFirstLineStyle.FontWeight;
            return inlineFirstLineStyle;
        }

        return null;
    }

    private static string TransformText(string text, TextTransform transform) =>
        transform switch
        {
            TextTransform.Uppercase => text.ToUpperInvariant(),
            TextTransform.Lowercase => text.ToLowerInvariant(),
            TextTransform.Capitalize => CapitalizeWords(text),
            _ => text
        };

    private static string CapitalizeWords(string text)
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
            else
            {
                start = true;
            }
        }
        return new string(chars);
    }

    private static IEnumerable<LayoutBox> ApplyFirstLetter(LayoutBox box)
    {
        var style = box.Element?.Style?.FirstLetterStyle;
        string text = box.TextRun ?? "";
        if (style == null || text.Length == 0 || box.Parent == null ||
            box.StyleOverride != null)
        {
            yield return box;
            yield break;
        }

        int first = 0;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
        if (first >= text.Length)
        {
            yield return box;
            yield break;
        }

        var firstBox = new LayoutBox(box.Element, BoxType.Inline)
        {
            TextRun = text[first].ToString(),
            Parent = box.Parent,
            StyleOverride = style,
            IsFloated = style.Float is FloatValue.Left or FloatValue.Right,
            FloatSide = style.Float
        };
        var rest = text[..first] + text[(first + 1)..];
        var restBox = new LayoutBox(box.Element, BoxType.Inline)
        {
            TextRun = rest,
            Parent = box.Parent
        };
        int index = box.Parent.Children.IndexOf(box);
        box.Parent.Children.RemoveAt(index);
        box.Parent.Children.Insert(index, firstBox);
        if (rest.Length > 0)
            box.Parent.Children.Insert(index + 1, restBox);

        yield return firstBox;
        if (rest.Length > 0)
            yield return restBox;
    }

    private static float GetAvailableWidth(float containerX, float y,
                                            float containerWidth, FloatContext? floats)
    {
        float containerRight = containerX + containerWidth;

        if (floats == null || !floats.HasFloats)
            return containerWidth;

        float l = Math.Max(containerX, floats.GetLeftEdge(y));
        float r = floats.GetRightEdge(y);
        r = (r == float.MaxValue) ? containerRight : Math.Min(r, containerRight);

        return Math.Max(0f, r - l);
    }

    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\n", "\\n");
        return s.Length <= 24 ? s : s[..24] + "…";
    }
}

/// <summary>
/// One renderable row of a SELECT control: either an option row or a
/// non-selectable OPTGROUP header row (HTML 4.01, Task 9).  Shared by the
/// renderer's listbox painting, the control's natural-size measurement and —
/// via the shell — listbox click hit-testing, so all three agree on the row
/// geometry: header rows carry the LABEL attribute (or the group's direct
/// text content) with no indent, options inside a group indent by
/// <see cref="GroupIndent"/> pixels.
/// </summary>
public sealed record SelectRow(DomElement? Option, string Label, float Indent, bool IsGroupHeader);

public static class SelectRowModel
{
    /// <summary>Indentation of options inside an OPTGROUP (period-plausible
    /// 12px gutter).</summary>
    public const float GroupIndent = 12f;

    public static int FindSelectableRow(IReadOnlyList<SelectRow> rows, int start, int direction)
    {
        if (rows.Count == 0 || direction == 0)
            return -1;

        int step = Math.Sign(direction);
        for (int index = start; index >= 0 && index < rows.Count; index += step)
            if (rows[index].Option != null)
                return index;
        return -1;
    }

    /// <summary>
    /// Builds the row list from the select's DIRECT children: optgroups
    /// contribute a header row plus indented option rows, loose options
    /// contribute plain rows (the parser keeps groups flat — options are
    /// never nested deeper than one optgroup).
    /// </summary>
    public static List<SelectRow> Build(DomElement select)
    {
        var rows = new List<SelectRow>();
        foreach (var node in select.Children)
        {
            if (node is not DomElement e)
                continue;
            switch (e.TagName)
            {
                case "optgroup":
                    {
                        string label = (e.GetAttr("label") ?? "").Trim();
                        if (label.Length == 0)
                            label = GroupTextContent(e);
                        rows.Add(new SelectRow(null, label, 0f, IsGroupHeader: true));
                        foreach (var child in e.Children)
                            if (child is DomElement opt && opt.TagName == "option")
                                rows.Add(new SelectRow(opt, OptionLabel(opt),
                                    GroupIndent, IsGroupHeader: false));
                        break;
                    }
                case "option":
                    rows.Add(new SelectRow(e, OptionLabel(e), 0f, IsGroupHeader: false));
                    break;
            }
        }
        return rows;
    }

    public static string OptionLabel(DomElement opt) =>
        (opt.GetAttr("label") ?? opt.InnerText ?? "").Trim();

    /// <summary>
    /// The optgroup's own text: its DIRECT text children only (an option's
    /// text belongs to the option rows).
    /// </summary>
    private static string GroupTextContent(DomElement group)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in group.Children)
            if (child is DomText t)
                sb.Append(t.Data);
        return sb.ToString().Trim();
    }
}

/// <summary>
/// TEMP DEBUG TRACE — instrumentation for the TB-08 float wrap-around
/// investigation (table align=left text overlapping instead of wrapping).
/// Off by default; set RETRO96_TRACE_FLOATS=1 in the environment to enable.
/// Writes to Console AND to retro96-float-trace.txt next to the exe, since
/// console output is easy to miss when the app is launched by double-click.
/// Safe to rip out (or leave permanently gated off) once the bug is found.
/// </summary>
public static class LayoutTrace
{
    public static readonly bool Enabled =
        Retro96.DebugLog.Enabled ||
        Environment.GetEnvironmentVariable("RETRO96_TRACE_FLOATS") == "1";

    // Written once, at class init, UNCONDITIONALLY (no Enabled check, no
    // try/catch swallow) so we can tell from the file's mere existence
    // whether this build is even the one running, and whether Enabled came
    // out true — instead of silently producing nothing on any I/O failure.
    static LayoutTrace()
    {
        if (!Enabled) return;
        try
        {
            string markerPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "retro96-trace-marker.txt");
            System.IO.File.WriteAllText(markerPath,
                $"LayoutTrace loaded at {DateTime.Now:O}{Environment.NewLine}" +
                $"BaseDirectory={AppContext.BaseDirectory}{Environment.NewLine}" +
                $"RETRO96_TRACE_FLOATS env var raw value = " +
                $"'{Environment.GetEnvironmentVariable("RETRO96_TRACE_FLOATS") ?? "(not set)"}'{Environment.NewLine}" +
                $"Enabled={Enabled}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // Even the marker write failed — dump to Temp instead, since
            // BaseDirectory itself must not be writable (e.g. Program Files).
            try
            {
                string fallback = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "retro96-trace-marker.txt");
                System.IO.File.WriteAllText(fallback,
                    $"BaseDirectory write failed: {ex}{Environment.NewLine}");
            }
            catch { /* truly nothing we can do */ }
        }
    }

    private static readonly object Gate = new();

    public static void Log(string message)
    {
        // Always echo to Console/Debug even when file logging is off or
        // failing, so a debugger or `dotnet run` console still shows output.
        System.Diagnostics.Debug.WriteLine("[float-trace] " + message);

        if (!Enabled) return;

        Console.WriteLine("[float-trace] " + message);

        lock (Gate)
        {
            string path = System.IO.Path.Combine(
                AppContext.BaseDirectory, "retro96-float-trace.txt");
            try
            {
                System.IO.File.AppendAllText(path, message + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Do NOT swallow this — if BaseDirectory isn't writable, fall
                // back to Temp so the trace exists SOMEWHERE, and record the
                // original failure once so it's diagnosable.
                try
                {
                    string fallback = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "retro96-float-trace.txt");
                    System.IO.File.AppendAllText(fallback,
                        message + Environment.NewLine);
                    System.IO.File.AppendAllText(fallback,
                        $"[note] primary path '{path}' failed: {ex.Message}{Environment.NewLine}");
                }
                catch { /* truly nothing we can do */ }
            }
        }
    }
}