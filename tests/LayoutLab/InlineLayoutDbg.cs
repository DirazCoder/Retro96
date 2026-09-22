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

    public void AddFloat(LayoutBox floatBox)
    {
        _floats.Add(floatBox);

        float top = floatBox.Y;
        float bottom = floatBox.Y + floatBox.BorderTop + floatBox.PaddingTop
                     + floatBox.Height + floatBox.PaddingBottom + floatBox.BorderBottom;

        if (floatBox.FloatSide == FloatValue.Left)
        {
            float edge = floatBox.X + floatBox.BorderLeft + floatBox.PaddingLeft
                       + floatBox.Width + floatBox.PaddingRight + floatBox.BorderRight
                       + floatBox.MarginRight;
            UpdateBands(top, bottom, edge, isLeft: true);
        }
        else
        {
            float edge = floatBox.X - floatBox.MarginLeft;
            UpdateBands(top, bottom, edge, isLeft: false);
        }
    }

    private void UpdateBands(float top, float bottom, float edge, bool isLeft)
    {
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

    private static float OuterBottom(LayoutBox f) =>
        f.Y + f.BorderTop + f.PaddingTop + f.Height + f.PaddingBottom + f.BorderBottom;

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
        int i = BandIndexAt(y);
        return i >= 0 ? _bands[i].LeftEdge : 0f;
    }

    public float GetRightEdge(float y)
    {
        int i = BandIndexAt(y);
        return i >= 0 ? _bands[i].RightEdge : float.MaxValue;
    }

    public bool HasFloats => _floats.Count > 0;

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
            return text?.Length * 8f ?? 0f;
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

        var source = inlineChildren.ToList();

        // ── Fragment + measure, collapsing adjacent spaces across run
        //    boundaries. Line-leading spaces are dropped at wrap time.
        var items = new List<MeasuredItem>(source.Count);
        bool lastWasSpace = true;   // stream start: no leading space
        foreach (var box in source)
        {
            foreach (var frag in FragmentTextBox(box))
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
        bool nowrap = containerStyle.WhiteSpace is WhiteSpaceValue.Nowrap
                                                or WhiteSpaceValue.Pre;
        float availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);

        var lineItems = new List<MeasuredItem>();
        float lineW = 0f;
        bool skipLeadingSpaces = true;   // fresh line: no leading space

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];

            if (skipLeadingSpaces && it.Box.TextRun == " ")
                continue;
            skipLeadingSpaces = false;

            if (it.Box.Element?.TagName == "br")
            {
                float brH = it.H > 0 ? it.H :
                            (lineItems.Count > 0 ? lineItems[^1].H : 16f);

                currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                      containerWidth, containerStyle, floats,
                                      justifyLine: true);
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
                currentY += brH;
                skipLeadingSpaces = true;
                availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
                continue;
            }

            float outerW = it.MarginL + it.W + it.MarginR;

            if (!nowrap && lineItems.Count > 0 && lineW + outerW > availW)
            {
                currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                      containerWidth, containerStyle, floats,
                                      justifyLine: true);
                lineItems.Clear();
                lineW = 0f;
                skipLeadingSpaces = true;
                availW = GetAvailableWidth(containerX, currentY, containerWidth, floats);
                if (it.Box.TextRun == " ")
                {
                    if (Environment.GetEnvironmentVariable("LAB_DEBUG") == "1")
                        Console.WriteLine($"    SPACE-DROPPED-AT-WRAP (lineW={lineW} outerW={outerW} availW={availW})");
                    continue;
                }
            }

            lineItems.Add(it);
            lineW += outerW;
        }

        if (lineItems.Count > 0)
            currentY += FlushLine(lineItems, containerX, currentY, lineW,
                                  containerWidth, containerStyle, floats,
                                  justifyLine: false);

        return Math.Max(0f, currentY - startY);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fragmentation
    // ─────────────────────────────────────────────────────────────────────

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
        for (int k = 0; k < frags.Count; k++)
            parent.Children.Insert(idx + k, frags[k]);

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
        bool justifyLine)
    {
        float leftEdgeCalc() { if (floats != null && floats.HasFloats) return Math.Max(containerX, floats.GetLeftEdge(y)); return containerX; }
        float rightEdgeCalc() { if (floats != null && floats.HasFloats) { float re = floats.GetRightEdge(y); if (re != float.MaxValue) return Math.Min(containerX + containerWidth, re); } return containerX + containerWidth; }
        if (items.Count == 0) return 0f;
        if (Environment.GetEnvironmentVariable("LAB_DEBUG") == "1")
            Console.WriteLine($"    FLUSH align={containerStyle.TextAlign} left={leftEdgeCalc()} right={rightEdgeCalc()} lineW={lineW} items=[{string.Join("|", items.Select(i => i.Box.TextRun))}]");
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
                    if (items[i].Box.TextRun != " ") gaps++;
                if (gaps > 0)
                    extra = Math.Max(0f, (rightEdge - leftEdge - lineW) / gaps);
                break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var box = it.Box;

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

            x += it.MarginL + it.W + it.MarginR;
            if (i < items.Count - 1 && it.Box.TextRun != " ")
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
            float cw = box.Width > 0 ? box.Width : (box.ReplacedImage?.Width ?? 0);
            float ch = box.Height > 0 ? box.Height : (box.ReplacedImage?.Height ?? 0);

            // ── Label-driven auto-sizing, WRITTEN BACK TO THE BOX — the
            //    natural size used to live only in the line math while the
            //    box kept the 80px default, so the renderer still painted
            //    "Clear Fo…".  Explicit WIDTH/HEIGHT attributes still win.
            if (el != null)
            {
                var style = el.Style;
                bool hasFont = style != null && _fontCache != null;

                switch (el.TagName)
                {
                    case "input":
                        {
                            string type = el.GetAttrOrDefault("type", "text").Trim().ToLowerInvariant();
                            if (type is "submit" or "reset" or "button")
                            {
                                if (!el.HasAttr("width") && hasFont)
                                {
                                    string label = type switch
                                    {
                                        "submit" => el.GetAttr("value") ?? "Submit Query",
                                        "reset" => el.GetAttr("value") ?? "Reset",
                                        _ => el.GetAttr("value") ?? "Button"
                                    };
                                    box.Width = cw = Math.Max(60f, MeasureTextWidth(label, style!) + 24f);
                                }
                                if (!el.HasAttr("height") && hasFont)
                                    box.Height = ch = Math.Max(22f,
                                        ResolveRunFont(style!).GetHeight(_measureG) + 10f);
                            }
                            else if (type is "text" or "password" or "file")
                            {
                                int size = el.GetAttrInt("size", 0);
                                if (size > 0 && !el.HasAttr("width") && hasFont)
                                    box.Width = cw = MeasureTextWidth(new string('0', size), style!) + 12f;
                                if (!el.HasAttr("height") && hasFont)
                                    box.Height = ch = Math.Max(22f,
                                        ResolveRunFont(style!).GetHeight(_measureG) + 10f);
                            }
                            break;
                        }

                    case "button":
                        {
                            if (!el.HasAttr("width") && hasFont)
                            {
                                string label = (el.InnerText ?? "").Trim();
                                if (label.Length == 0) label = el.GetAttr("value") ?? "Button";
                                box.Width = cw = Math.Max(60f, MeasureTextWidth(label, style!) + 24f);
                            }
                            if (!el.HasAttr("height") && hasFont)
                                box.Height = ch = Math.Max(22f,
                                    ResolveRunFont(style!).GetHeight(_measureG) + 10f);
                            break;
                        }

                    case "select":
                        {
                            if (!el.HasAttr("width") && hasFont)
                            {
                                float longest = 40f;
                                foreach (var opt in el.ElementDescendants())
                                    if (opt.TagName == "option")
                                        longest = Math.Max(longest,
                                            MeasureTextWidth((opt.InnerText ?? "").Trim(), style!));
                                box.Width = cw = Math.Max(60f, longest + 36f);
                            }
                            if (!el.HasAttr("height") && hasFont)
                                box.Height = ch = Math.Max(22f,
                                    ResolveRunFont(style!).GetHeight(_measureG) + 10f);
                            break;
                        }
                }
            }

            width = cw + box.BorderLeft + box.BorderRight + box.PaddingLeft + box.PaddingRight;
            height = ch + box.BorderTop + box.BorderBottom + box.PaddingTop + box.PaddingBottom;
            ascent = height;   // bottom of the border box sits on the baseline
            return;
        }

        // Text run — measured with the same hint and format the renderer
        // draws with, spaces glued to its word.
        if (!string.IsNullOrEmpty(box.TextRun))
        {
            var style = box.Element?.Style;
            if (_fontCache != null && style != null)
            {
                var font = ResolveRunFont(style);

                var sz = _measureG.MeasureString(box.TextRun, font, int.MaxValue, _sf);
                width = (float)Math.Ceiling(sz.Width);

                float lhMult = style.LineHeight > 0f ? style.LineHeight : 1.2f;
                height = (float)Math.Ceiling(font.GetHeight(_measureG) * lhMult);

                float cellH = font.FontFamily.GetLineSpacing(font.Style);
                float ascentU = font.FontFamily.GetCellAscent(font.Style);
                ascent = font.GetHeight(_measureG) * (ascentU / cellH);
                return;
            }

            float fSize = style?.FontSize > 0f ? style.FontSize : 16f;
            width = box.TextRun!.Length * fSize * 0.55f;
            height = fSize * 1.2f;
            ascent = fSize * 0.85f;
            return;
        }

        // Empty / unknown (<br>, <wbr>)
        width = 0f; height = 0f; ascent = 0f;
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
}