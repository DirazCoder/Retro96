using System;
using System.Collections.Generic;
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
/// The inline formatting context: fragments + line boxes with GDI+
/// measurement, baseline alignment, HTML ALIGN= vertical modes, and text
/// reflow around floats.
/// </summary>
public static class InlineLayout
{
    private static readonly List<string> DefaultFontFamily = ["Times New Roman", "serif"];

    private static Render.FontCache? _fontCache;
    private static readonly Bitmap _measureBmp = new(1, 1);
    private static readonly Graphics _measureG;

    static InlineLayout()
    {
        _measureG = Graphics.FromImage(_measureBmp);
        // MUST match the Renderer's hint (now ClearTypeGridFit) — a
        // measure/draw hint mismatch produces wrong advance widths, which
        // is itself a spacing bug.
        _measureG.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static void SetFontCache(Render.FontCache fc) => _fontCache = fc;

    private static readonly StringFormat _sf = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.None
    };

    internal static float MeasureTextWidth(string text, ComputedStyle style)
    {
        if (string.IsNullOrEmpty(text) || _fontCache == null)
            return text is null ? 0f : text.Length * 8f;
        var font = ResolveRunFont(style);
        var sz = _measureG.MeasureString(text, font, int.MaxValue, _sf);
        return (float)Math.Ceiling(sz.Width);
    }

    private static Font ResolveRunFont(ComputedStyle style)
    {
        var family = style.FontFamily is { Count: > 0 } ? style.FontFamily : DefaultFontFamily;
        float size = style.FontSize > 0f ? style.FontSize : 16f;
        bool bold = style.FontWeight >= FontWeightValue.Bold;
        bool italic = style.FontStyle == FontStyleValue.Italic;
        return _fontCache!.Resolve(family, size, bold, italic);
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
                    else if (type is "text" or "password" or "file")
                    {
                        int size = el.GetAttrInt("size", 0);
                        if (size > 0)
                            width = MeasureTextWidth(new string('0', size), style) + 12f;
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
                    string label = (el.InnerText ?? "").Trim();
                    if (label.Length == 0) label = el.GetAttr("value") ?? "Button";
                    width = Math.Max(60f, MeasureTextWidth(label, style) + 24f);
                    return true;
                }

            case "select":
                {
                    float longest = 40f;
                    foreach (var opt in el.ElementDescendants())
                        if (opt.TagName == "option")
                            longest = Math.Max(longest,
                                MeasureTextWidth(Render.GlyphSubstitution.MapGlyphs(
                                    (opt.InnerText ?? "").Trim()), style));
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
                        width = MeasureTextWidth(new string('0', cols), style) + 12f;
                    int rows = Math.Max(1, el.GetAttrInt("rows", 4));
                    height = (float)Math.Ceiling(font.GetHeight(_measureG)) * rows + 4f;
                    return true;
                }
        }

        return false;
    }

    private enum VAlignMode { Baseline, Top, Middle, Bottom }

    private readonly record struct MeasuredItem(
        LayoutBox Box, float W, float H, float Asc, VAlignMode VA,
        bool Atomic, float MarginL, float MarginR);

    private static VAlignMode GetVAlignMode(LayoutBox box)
    {
        var align = box.Element?.GetAttr("align")?.Trim().ToLowerInvariant();
        return align switch
        {
            "top" => VAlignMode.Top,
            "texttop" => VAlignMode.Top,
            "middle" => VAlignMode.Middle,
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

        // ── Fragment + measure, collapsing adjacent spaces across run
        //    boundaries. Line-leading spaces are dropped at wrap time.
        var items = new List<MeasuredItem>(source.Count);
        bool lastWasSpace = true;   // stream start: no leading space
        foreach (var box in source)
        {
            foreach (var prepared in ApplyFirstLetter(box))
                foreach (var frag in FragmentTextBox(prepared))
                {
                    bool isSpace = frag.TextRun == " ";
                    if (isSpace && lastWasSpace)
                        continue;
                    MeasureBox(frag, out float w, out float h, out float asc);
                    bool atomic = frag.BoxType is BoxType.Replaced or BoxType.InlineBlock or BoxType.Frame
                                  || frag.ReplacedImage != null;
                    items.Add(new MeasuredItem(frag, w, h, asc, GetVAlignMode(frag), atomic,
                        atomic ? frag.MarginLeft : 0f,
                        atomic ? frag.MarginRight : 0f));
                    lastWasSpace = isSpace;
                }
        }

        float currentY = startY;
        bool firstLine = true;
        bool nowrap = containerStyle.WhiteSpace is WhiteSpaceValue.Nowrap
                                                or WhiteSpaceValue.Pre;
        float availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
        LayoutTrace.Log($"  initial availW at y={currentY:F1} => {availW:F1} " +
            $"(containerWidth={containerWidth:F1})");

        var lineItems = new List<MeasuredItem>();
        float lineW = 0f;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];

            // Leading spaces are dropped ONLY while the line is still empty.
            // The old flag-based version left skipLeadingSpaces=true after a
            // WORD-triggered wrap, so the space AFTER the first word of the
            // new line was eaten — "the problem" wrapped as "theproblem",
            // "case-sensitive on" as "case-sensitiveon".
            if (lineItems.Count == 0 && it.Box.TextRun == " ")
                continue;

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
                availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
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

            if (!nowrap && lineItems.Count > 0 && lineW + outerW > availW)
            {
                LayoutTrace.Log($"  WRAP at y={currentY:F1}: lineW={lineW:F1} + outerW={outerW:F1} " +
                    $"> availW={availW:F1} (word=\"{Truncate(it.Box.TextRun)}\")");
                currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                      containerWidth, containerStyle, floats,
                                      justifyLine: true, firstLine);
                firstLine = false;
                lineItems.Clear();
                lineW = 0f;
                availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
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

            lineItems.Add(it);
            lineW += outerW;
        }

        if (lineItems.Count > 0)
            currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                  containerWidth, containerStyle, floats,
                                  justifyLine: false, firstLine);

        return Math.Max(0f, currentY - startY);
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
            MeasureBox(piece, out float width, out float height, out float ascent);
            measured.Add(new MeasuredItem(piece, width, height, ascent,
                GetVAlignMode(piece), false, 0f, 0f));
        }
        return measured;
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
                    Parent = parent!
                });
            }
            if (sp < 0) break;

            frags.Add(new LayoutBox(box.Element, BoxType.Inline)
            {
                TextRun = " ",
                Parent = parent!
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
        foreach (var it in items)
        {
            maxAscent = Math.Max(maxAscent, it.Asc);
            maxDescent = Math.Max(maxDescent, it.H - it.Asc);
        }
        float lineH = Math.Max(maxAscent + maxDescent, 1f);

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

        float x = leftEdge;
        float extra = 0f;

        switch (containerStyle.TextAlign)
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

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var box = it.Box;

            if (firstLine && box.Element?.Style?.FirstLineStyle != null &&
                box.Element == containerStyleElement(box, containerStyle))
                box.StyleOverride = box.Element.Style.FirstLineStyle;

            box.X = x + it.MarginL;
            if (!it.Atomic)
            {
                box.Width = it.W;
                box.Height = it.H;
            }

            switch (it.VA)
            {
                case VAlignMode.Top:
                    box.Y = y;
                    break;
                case VAlignMode.Middle:
                    box.Y = y + (lineH - it.H) / 2f;
                    break;
                case VAlignMode.Bottom:
                    box.Y = y + lineH - it.H;
                    break;
                default:
                    box.Y = y + (maxAscent - it.Asc);
                    var elem = box.Element;
                    var cssVa = elem?.Style?.VerticalAlign ?? VerticalAlign.Baseline;
                    var tag = elem?.TagName;
                    if (cssVa == VerticalAlign.Super || tag == "sup") box.Y -= it.H * 0.35f;
                    else if (cssVa == VerticalAlign.Sub || tag == "sub") box.Y += it.H * 0.25f;
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

    // ─────────────────────────────────────────────────────────────────────
    // Measurement
    // ─────────────────────────────────────────────────────────────────────

    private static void MeasureBox(LayoutBox box,
        out float width, out float height, out float ascent)
    {
        // Atomic inline boxes (images, form controls, inline-blocks, frames)
        if (box.BoxType is BoxType.Replaced or BoxType.InlineBlock or BoxType.Frame
            || box.ReplacedImage != null)
        {
            var el = box.Element;

            // HIDDEN inputs take no space at all — no chrome, no
            // line-height contribution, no phantom word gap.
            if (el != null && el.TagName == "input" &&
                el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant() == "hidden")
            {
                width = 0f; height = 0f; ascent = 0f;
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
                if (nw > 0f && !el.HasAttr("width")) box.Width = cw = Math.Max(cw, nw);
                if (nh > 0f && !el.HasAttr("height")) box.Height = ch = Math.Max(ch, nh);
            }

            width = cw + box.BorderLeft + box.BorderRight + box.PaddingLeft + box.PaddingRight;
            height = ch + box.BorderTop + box.BorderBottom + box.PaddingTop + box.PaddingBottom;

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
            var style = box.StyleOverride
                     ?? box.Element?.Style?.FirstLineStyle
                     ?? box.Element?.Style;
            if (_fontCache != null && style != null)
            {
                var font = ResolveRunFont(style);

                var measuredText = TransformText(box.TextRun, style.TextTransform);
                var sz = _measureG.MeasureString(measuredText, font, int.MaxValue, _sf);
                width = (float)Math.Ceiling(sz.Width);

                // line-height: 'normal' (LineHeight==0) = the font's natural
                // line height (ascent+descent+leading) — Chromium's normal.
                // An authored number/% is an EM multiplier per CSS1/2.1:
                // applied against font-size, not stacked on GetHeight (the
                // old code multiplied the natural height by another 1.2,
                // inflating every line box ~20% and drifting whole pages).
                height = style.LineHeight > 0f
                    ? (float)Math.Ceiling(style.LineHeight * style.FontSize)
                    : (float)Math.Ceiling(font.GetHeight(_measureG));


                // FIX: guard the em-unit ratio — GetLineSpacing can return 0
                // for broken/abstract font families → NaN ascent → every
                // box on the line lands at NaN Y.
                float cellH = font.FontFamily.GetLineSpacing(font.Style);
                float ascentU = font.FontFamily.GetCellAscent(font.Style);
                ascent = cellH > 0f
                    ? font.GetHeight(_measureG) * (ascentU / cellH)
                    : font.GetHeight(_measureG) * 0.8f;
                return;
            }

            float fSize = style?.FontSize > 0f ? style.FontSize : 16f;
            width = TransformText(box.TextRun!, style?.TextTransform ?? TextTransform.None).Length
                * fSize * 0.55f;
            height = fSize * 1.2f;
            ascent = fSize * 0.85f;
            return;
        }

        // Empty / unknown (<br>, <wbr>)
        width = 0f; height = 0f; ascent = 0f;
    }

    private static DomElement? containerStyleElement(LayoutBox box, ComputedStyle style)
    {
        var element = box.Element;
        return element?.Style == style ? element : null;
    }

    private static string TransformText(string text, TextTransform transform) =>
        transform switch
        {
            TextTransform.Uppercase => text.ToUpperInvariant(),
            TextTransform.Lowercase => text.ToLowerInvariant(),
            _ => text
        };

    private static IEnumerable<LayoutBox> ApplyFirstLetter(LayoutBox box)
    {
        var style = box.Element?.Style?.FirstLetterStyle;
        string text = box.TextRun ?? "";
        if (style == null || text.Length == 0 || box.Parent == null)
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
            StyleOverride = style
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