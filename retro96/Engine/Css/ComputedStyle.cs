using System;
using System.Collections.Generic;
using Retro96.Drawing;
using System.Globalization;
using System.Linq;

namespace Retro96.Engine.Css;

/// <summary>
/// Holds every CSS1 + 1996-presentational property in its final resolved
/// pixel / enum value.  Defaults follow the Netscape Navigator 3 user-agent
/// stylesheet.  All lengths are pixels by the time they land here.
/// </summary>
public class ComputedStyle
{
    // === FONT ===
    public List<string> FontFamily { get; set; } = ["Times New Roman", "serif"];
    public float FontSize { get; set; } = 16f;           // px
    public FontWeightValue FontWeight { get; set; } = FontWeightValue.Normal;
    public FontStyleValue FontStyle { get; set; } = FontStyleValue.Normal;
    public FontVariantValue FontVariant { get; set; } = FontVariantValue.Normal;
    public bool OwnFontSize { get; set; }

    // === TEXT ===
    public Color Color { get; set; } = Color.Black;
    public TextDecoration TextDecoration { get; set; } = TextDecoration.None;
    public TextAlign TextAlign { get; set; } = TextAlign.Left;
    public float TextIndent { get; set; } = 0f;
    public float LineHeight { get; set; } = 0f;          // 0 = 'normal' (font's natural line height); authored values are em multipliers
    public float LetterSpacing { get; set; } = 0f;
    public float WordSpacing { get; set; } = 0f;
    public TextTransform TextTransform { get; set; } = TextTransform.None;
    public WhiteSpaceValue WhiteSpace { get; set; } = WhiteSpaceValue.Normal;
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Baseline;

    // === BACKGROUND ===
    public Color BackgroundColor { get; set; } = Color.Transparent;
    public string? BackgroundImage { get; set; }           // URL or null
    public BackgroundRepeat BackgroundRepeat { get; set; } = BackgroundRepeat.Repeat;
    public bool BackgroundFixed { get; set; }
    public PointF BackgroundPosition { get; set; } = PointF.Empty;

    /// <summary>Authored (vs inherited) colour flags — set ONLY by
    /// declarations this element's own inline STYLE or matched CSS rules
    /// carry (everything flows through Apply).  The renderer consults them
    /// to decide whether a form control takes CSS1 colours (IE3-era form
    /// styling) or the classic native look: an inherited BODY text=
    /// colour must never turn input text invisible-on-white.</summary>
    public bool OwnColor, OwnBackground, OwnTextAlign;

    // === BOX MODEL (px) ===
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float BorderTopWidth, BorderRightWidth, BorderBottomWidth, BorderLeftWidth;
    public BorderStyleValue BorderTopStyle, BorderRightStyle,
                            BorderBottomStyle, BorderLeftStyle;
    public Color BorderTopColor, BorderRightColor,
                  BorderBottomColor, BorderLeftColor;
    public float? Width { get; set; }     // null = auto
    public float? Height { get; set; }    // null = auto

    // CSS1 percentage sizes (width: 50%) — resolved at LAYOUT time against
    // the real containing block, never at parse time against the viewport.
    // They used to be flattened into a viewport-relative pixel Width, which
    // put every CSS-sized box at the wrong width inside any container
    // narrower than the viewport.
    public float? WidthPercent { get; set; }
    public float? HeightPercent { get; set; }

    // FIX: same story for top/left/right/bottom on positioned boxes.
    // "top:30%" on an absolutely positioned element resolves against its
    // CONTAINING BLOCK's height (left/right against width), never the page
    // viewport, and that containing block isn't known until layout walks
    // the box tree. The old code ran these through ParseLength immediately
    // here, using viewportWidth for all four sides regardless of axis —
    // wrong base AND wrong axis for top/bottom — baking in a pixel offset
    // before layout had any idea what box it was actually offset from.
    public float? TopPercent { get; set; }
    public float? RightPercent { get; set; }
    public float? BottomPercent { get; set; }
    public float? LeftPercent { get; set; }

    // margin-left/right: auto — the CSS1 centring idiom.  ParseLength
    // flattens "auto" to 0, so the flag carries the intent into layout,
    // where the leftover space is distributed (both auto → centred).
    public bool MarginLeftAuto, MarginRightAuto;

    // === DISPLAY ===
    public DisplayValue Display { get; set; } = DisplayValue.Inline;
    public VisibilityValue Visibility { get; set; } = VisibilityValue.Visible;
    public OverflowValue Overflow { get; set; } = OverflowValue.Visible;
    public ComputedStyle? FirstLineStyle { get; set; }
    public ComputedStyle? FirstLetterStyle { get; set; }

    // === POSITIONING ===
    public PositionValue Position { get; set; } = PositionValue.Static;
    public float? Top, Right, Bottom, Left;
    public FloatValue Float { get; set; } = FloatValue.None;
    public ClearValue Clear { get; set; } = ClearValue.None;
    public int ZIndex { get; set; } = 0;

    // === LISTS ===
    public ListStyleType ListStyleType { get; set; } = ListStyleType.Disc;
    public string? ListStyleImage { get; set; }
    public ListStylePosition ListStylePosition { get; set; } = ListStylePosition.Outside;

    // Netscape 1–7 <font size> scale, in pixels.  Documented mapping.
    // Netscape's HTML font-size scale is in POINTS (1=8pt … 7=36pt+).
    // Everything downstream measures in PIXELS, so convert at the era's
    // 96-dpi screen density: pt × 96/72 = pt × 4/3.
    //   1:8pt→11px  2:10pt→13px  3:12pt→16px  4:14pt→19px
    //   5:18pt→24px 6:24pt→32px 7:36pt→48px
    public static float FontScaleToPx(int size) => size switch
    {
        <= 1 => 11f,
        2 => 13f,
        3 => 16f,
        4 => 19f,
        5 => 24f,
        6 => 32f,
        >= 7 => 48f
    };

    /// <summary>
    /// Create a new ComputedStyle by applying own declarations on top of the
    /// parent's inherited values.
    /// </summary>
    public static ComputedStyle Inherit(ComputedStyle parent, IEnumerable<CssDeclaration> own)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));

        var child = new ComputedStyle();

        // Inherited properties
        child.FontFamily = new List<string>(parent.FontFamily);
        child.FontSize = parent.FontSize;
        child.FontWeight = parent.FontWeight;
        child.FontStyle = parent.FontStyle;
        child.FontVariant = parent.FontVariant;
        child.Color = parent.Color;
        child.TextAlign = parent.TextAlign;
        child.TextIndent = parent.TextIndent;
        child.LineHeight = parent.LineHeight;
        child.LetterSpacing = parent.LetterSpacing;
        child.WordSpacing = parent.WordSpacing;
        child.TextTransform = parent.TextTransform;
        child.WhiteSpace = parent.WhiteSpace;
        child.Visibility = parent.Visibility;
        // list-style-* is inherited in CSS1
        child.ListStyleType = parent.ListStyleType;
        child.ListStylePosition = parent.ListStylePosition;
        child.ListStyleImage = parent.ListStyleImage;

        // Non-inherited (background, box model, display, position) keep
        // their initial values.

        if (own != null)
        {
            foreach (var decl in own)
                child.Apply(decl, parent.FontSize, 800f);
        }

        return child;
    }

    /// <summary>Parse and apply a single declaration in place.</summary>
    public void Apply(CssDeclaration decl, float parentFontSize, float viewportWidth)
    {
        if (decl == null) return;

        var property = decl.Property.ToLowerInvariant();
        var value = decl.Value.Trim();

        switch (property)
        {
            // === FONT ===
            case "font-family": FontFamily = ParseFontFamily(value); break;
            case "font-size": FontSize = ParseFontSize(value, parentFontSize); OwnFontSize = true; break;
            case "font-weight": FontWeight = ParseFontWeight(value); break;
            case "font-style": FontStyle = ParseFontStyle(value); break;
            case "font-variant": FontVariant = ParseFontVariant(value); break;
            case "font": ParseFontShorthand(value, parentFontSize); OwnFontSize = true; break;

            // === TEXT ===
            case "color": Color = ParseColor(value, Color); OwnColor = true; break;
            case "text-decoration": TextDecoration = ParseTextDecoration(value); break;
            case "text-align": TextAlign = ParseTextAlign(value); OwnTextAlign = true; break;
            case "text-indent": TextIndent = ParseLength(value, parentFontSize, viewportWidth); break;
            case "line-height": LineHeight = ParseLineHeight(value); break;
            case "letter-spacing": LetterSpacing = ParseLength(value, parentFontSize, viewportWidth); break;
            case "word-spacing": WordSpacing = ParseLength(value, parentFontSize, viewportWidth); break;
            case "text-transform": TextTransform = ParseTextTransform(value); break;
            case "white-space": WhiteSpace = ParseWhiteSpace(value); break;
            case "vertical-align": VerticalAlign = ParseVerticalAlign(value); break;

            // === BACKGROUND ===
            case "background-color": BackgroundColor = ParseColor(value, BackgroundColor); OwnBackground = true; break;
            case "background-image": BackgroundImage = ParseUrl(value); break;
            case "background-repeat": BackgroundRepeat = ParseBackgroundRepeat(value); break;
            case "background-attachment": BackgroundFixed = value.Equals("fixed", StringComparison.OrdinalIgnoreCase); break;
            case "background-position": BackgroundPosition = ParseBackgroundPosition(value); break;
            case "background": ParseBackgroundShorthand(value); OwnBackground = true; break;

            // === BOX MODEL ===
            case "margin-top": MarginTop = ParseLength(value, parentFontSize, viewportWidth); break;
            case "margin-right":
                MarginRight = ParseLength(value, parentFontSize, viewportWidth);
                MarginRightAuto = IsAutoKeyword(value); break;
            case "margin-bottom": MarginBottom = ParseLength(value, parentFontSize, viewportWidth); break;
            case "margin-left":
                MarginLeft = ParseLength(value, parentFontSize, viewportWidth);
                MarginLeftAuto = IsAutoKeyword(value); break;
            case "margin": ParseMarginShorthand(value, parentFontSize, viewportWidth); break;

            case "padding-top": PaddingTop = ParseLength(value, parentFontSize, viewportWidth); break;
            case "padding-right": PaddingRight = ParseLength(value, parentFontSize, viewportWidth); break;
            case "padding-bottom": PaddingBottom = ParseLength(value, parentFontSize, viewportWidth); break;
            case "padding-left": PaddingLeft = ParseLength(value, parentFontSize, viewportWidth); break;
            case "padding": ParsePaddingShorthand(value, parentFontSize, viewportWidth); break;

            case "border-top-width": BorderTopWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-right-width": BorderRightWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-bottom-width": BorderBottomWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-left-width": BorderLeftWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-top-style": BorderTopStyle = ParseBorderStyle(value); break;
            case "border-right-style": BorderRightStyle = ParseBorderStyle(value); break;
            case "border-bottom-style": BorderBottomStyle = ParseBorderStyle(value); break;
            case "border-left-style": BorderLeftStyle = ParseBorderStyle(value); break;
            case "border-top-color": BorderTopColor = ParseColor(value, BorderTopColor); break;
            case "border-right-color": BorderRightColor = ParseColor(value, BorderRightColor); break;
            case "border-bottom-color": BorderBottomColor = ParseColor(value, BorderBottomColor); break;
            case "border-left-color": BorderLeftColor = ParseColor(value, BorderLeftColor); break;
            case "border-width": ParseBorderWidthShorthand(value, parentFontSize, viewportWidth); break;
            case "border-style": ParseBorderStyleShorthand(value); break;
            case "border-color": ParseBorderColorShorthand(value); break;
            case "border": ParseBorderShorthand(value, parentFontSize, viewportWidth); break;

            // === DISPLAY / POSITIONING ===
            case "display": Display = ParseDisplay(value); break;
            case "visibility": Visibility = ParseVisibility(value); break;
            case "overflow": Overflow = ParseOverflow(value); break;
            case "position": Position = ParsePosition(value); break;
            case "top": (Top, TopPercent) = ParseOffset(value, parentFontSize, viewportWidth); break;
            case "right": (Right, RightPercent) = ParseOffset(value, parentFontSize, viewportWidth); break;
            case "bottom": (Bottom, BottomPercent) = ParseOffset(value, parentFontSize, viewportWidth); break;
            case "left": (Left, LeftPercent) = ParseOffset(value, parentFontSize, viewportWidth); break;
            case "float": Float = ParseFloat(value); break;
            case "clear": Clear = ParseClear(value); break;
            case "z-index": ZIndex = ParseZIndex(value); break;

            // === LISTS ===
            case "list-style-type": ListStyleType = ParseListStyleType(value); break;
            case "list-style-image": ListStyleImage = ParseUrl(value); break;
            case "list-style-position": ListStylePosition = ParseListStylePosition(value); break;

            // === SIZE ===
            // Percentages are kept AS percentages (resolved at layout
            // against the containing block); everything else is a pixel
            // length or auto.
            case "width": (Width, WidthPercent) = ParseSize(value, parentFontSize, viewportWidth); break;
            case "height": (Height, HeightPercent) = ParseSize(value, parentFontSize, viewportWidth); break;

            default:
                // Unknown property: silently ignored (1996 behaviour — one
                // bad property never breaks the rest of the rule).
                break;
        }
    }

    /// <summary>Clone this style.</summary>
    public ComputedStyle Clone()
    {
        var c = (ComputedStyle)MemberwiseClone();
        c.FontFamily = new List<string>(FontFamily);
        return c;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Parsing helpers
    // ─────────────────────────────────────────────────────────────────────

    internal static List<string> ParseFontFamily(string value)
    {
        var families = new List<string>();
        foreach (var part in value.Split(','))
        {
            var family = part.Trim().Trim('\'', '"');
            if (family.Length > 0)
                families.Add(family);
        }
        return families.Count > 0 ? families : ["Times New Roman", "serif"];
    }

    internal static float ParseFontSize(string value, float parentFontSize)
    {
        if (string.IsNullOrEmpty(value))
            return parentFontSize;

        string v = value.Trim();

        // Absolute-size keywords (CSS1 scale)
        switch (v.ToLowerInvariant())
        {
            case "xx-small": return FontScaleToPx(1);
            case "x-small": return FontScaleToPx(2);
            case "small": return 13f;
            case "medium": return 16f;
            case "large": return FontScaleToPx(4);
            case "x-large": return FontScaleToPx(5);
            case "xx-large": return FontScaleToPx(6);
            case "smaller": return parentFontSize * 0.83f;
            case "larger": return parentFontSize * 1.17f;
        }

        // Percentage of the parent size
        if (v.EndsWith('%'))
        {
            if (TryParseFloat(v[..^1], out float pct))
                return parentFontSize * pct / 100f;
            return parentFontSize;
        }

        return ParseLength(v, parentFontSize, 800f, parentFontSize);
    }

    private static FontWeightValue ParseFontWeight(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "normal" or "400" => FontWeightValue.Normal,
            "bold" or "700" => FontWeightValue.Bold,
            "bolder" => FontWeightValue.Bolder,
            "lighter" => FontWeightValue.Lighter,
            "100" => FontWeightValue.W100,
            "200" => FontWeightValue.W200,
            "300" => FontWeightValue.W300,
            "500" => FontWeightValue.W500,
            "600" => FontWeightValue.W600,
            "800" => FontWeightValue.W800,
            "900" => FontWeightValue.W900,
            _ => FontWeightValue.Normal
        };
    }

    private static FontStyleValue ParseFontStyle(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "italic" => FontStyleValue.Italic,
            "oblique" => FontStyleValue.Oblique,
            _ => FontStyleValue.Normal
        };
    }

    private static FontVariantValue ParseFontVariant(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "small-caps" => FontVariantValue.SmallCaps,
            _ => FontVariantValue.Normal
        };
    }

    /// <summary>font: [style] [variant] [weight] size[/line-height] family</summary>
    private void ParseFontShorthand(string value, float parentFontSize)
    {
        var parts = SplitTopLevel(value);
        string? size = null, family = null;

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i].ToLowerInvariant();

            if (part.Contains('/'))
            {
                // "12px/1.5" — size and line-height glued together
                var bits = parts[i].Split('/', 2);
                if (LooksLikeSize(bits[0]))
                {
                    size = bits[0];
                    if (bits.Length > 1)
                        LineHeight = ParseLineHeight(bits[1]);
                    if (i + 1 < parts.Count)
                        family = string.Join(" ", parts.Skip(i + 1));
                }
                break;
            }

            if (part is "italic" or "oblique")
                FontStyle = ParseFontStyle(part);
            else if (part == "small-caps")
                FontVariant = FontVariantValue.SmallCaps;
            else if (part is "bold" or "bolder" or "lighter" ||
                     (part.Length == 3 && int.TryParse(part, out _)))
                FontWeight = ParseFontWeight(part);
            else if (LooksLikeSize(part))
            {
                size = parts[i];

                // Optional separate "/line-height" token
                int next = i + 1;
                if (next < parts.Count && parts[next].StartsWith("/"))
                {
                    LineHeight = ParseLineHeight(parts[next][1..]);
                    next++;
                }
                if (next < parts.Count)
                    family = string.Join(" ", parts.Skip(next));
                break;
            }
        }

        if (size != null)
            FontSize = ParseFontSize(size, parentFontSize);
        if (family != null)
            FontFamily = ParseFontFamily(family);

        static bool LooksLikeSize(string p) =>
            p.EndsWith("px") || p.EndsWith("pt") || p.EndsWith("em") || p.EndsWith("%") ||
            p is "xx-small" or "x-small" or "small" or "medium" or "large"
                   or "x-large" or "xx-large";
    }

    internal static Color ParseColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string v = value.Trim();

        if (v.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return Color.Transparent;

        if (v.StartsWith("#"))
        {
            var hex = v[1..];
            if (hex.Length == 3) // #RGB → #RRGGBB
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length == 6 &&
                int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int argb))
            {
                return Color.FromArgb((argb & 0xFF0000) >> 16, (argb & 0xFF00) >> 8, argb & 0xFF);
            }
            return fallback;
        }

        if (v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            int open = v.IndexOf('(');
            int close = v.IndexOf(')');
            if (close < 0) close = v.Length - 1;
            if (close > open)
            {
                var parts = v[(open + 1)..close].Split(',');
                if (parts.Length >= 3 &&
                    TryParseRgb(parts[0], out int r) &&
                    TryParseRgb(parts[1], out int g) &&
                    TryParseRgb(parts[2], out int b))
                {
                    return Color.FromArgb(
                        Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
                }
            }
            return fallback;
        }

        // Named colour
        try
        {
            return ColorTranslator.FromHtml(v.ToLowerInvariant());
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// One rgb() component: a plain 0–255 integer, or a percentage scaled
    /// against 255 (rgb(50%,0%,0%) is 128,0,0 — the old parser read the
    /// "50" literally).
    /// </summary>
    private static bool TryParseRgb(string s, out int value)
    {
        s = s.Trim();
        if (s.EndsWith('%'))
        {
            if (TryParseFloat(s[..^1], out float pct))
            {
                value = (int)Math.Round(pct / 100f * 255f);
                return true;
            }
            value = 0;
            return false;
        }
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static TextDecoration ParseTextDecoration(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        TextDecoration deco = TextDecoration.None;
        if (v.Contains("underline")) deco |= TextDecoration.Underline;
        if (v.Contains("overline")) deco |= TextDecoration.Overline;
        if (v.Contains("line-through")) deco |= TextDecoration.LineThrough;
        if (v.Contains("blink")) deco |= TextDecoration.Blink;
        return deco;
    }

    private static TextAlign ParseTextAlign(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            "justify" => TextAlign.Justify,
            _ => TextAlign.Left
        };
    }

    internal static float ParseLineHeight(string value)
    {
        string v = value.Trim();
        if (v == "normal")
            return 0f;                    // 0 = 'normal' — let the font's natural metrics decide
        if (v.EndsWith('%'))
        {
            if (TryParseFloat(v[..^1], out float pct))
                return pct / 100f;        // em multiplier
        }
        else if (TryParseFloat(v, out float num))
        {
            return num;                   // unitless em multiplier
        }
        return 0f;                        // fallback: 'normal', must NOT silently re-introduce the bug
    }

    private static TextTransform ParseTextTransform(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "capitalize" => TextTransform.Capitalize,
            "uppercase" => TextTransform.Uppercase,
            "lowercase" => TextTransform.Lowercase,
            _ => TextTransform.None
        };
    }

    private static WhiteSpaceValue ParseWhiteSpace(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "pre" => WhiteSpaceValue.Pre,
            "nowrap" => WhiteSpaceValue.Nowrap,
            "pre-wrap" => WhiteSpaceValue.PreWrap,
            "pre-line" => WhiteSpaceValue.PreLine,
            _ => WhiteSpaceValue.Normal
        };
    }

    private static VerticalAlign ParseVerticalAlign(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "top" => VerticalAlign.Top,
            "middle" => VerticalAlign.Middle,
            "bottom" => VerticalAlign.Bottom,
            "text-top" => VerticalAlign.TextTop,
            "text-bottom" => VerticalAlign.TextBottom,
            "super" => VerticalAlign.Super,
            "sub" => VerticalAlign.Sub,
            _ => VerticalAlign.Baseline
        };
    }

    /// <summary>
    /// Parse a CSS length.  Bare numbers are pixels (HTML attr style);
    /// pt is converted at 4/3 px per pt; em/% relative to parentFontSize.
    /// </summary>
    internal static float ParseLength(string value, float parentFontSize,
                                      float viewportWidth, float? fallback = null)
    {
        if (string.IsNullOrEmpty(value))
            return fallback ?? 0f;

        string v = value.Trim().ToLowerInvariant();

        if (v == "auto" || v == "none")
            return 0f;
        if (v == "0")
            return 0f;

        // Sign
        bool negative = v.StartsWith('-');
        if (negative || v.StartsWith('+'))
            v = v[1..];

        float result;
        if (v.EndsWith("px"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
        }
        else if (v.EndsWith("pt"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result = result * 4f / 3f;
        }
        else if (v.EndsWith("em"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result = result * parentFontSize;
        }
        else if (v.EndsWith('%'))
        {
            if (!TryParseFloat(v[..^1], out result)) return fallback ?? 0f;
            result = result / 100f * viewportWidth;
        }
        else if (TryParseFloat(v, out result))
        {
            // unitless — treated as px
        }
        else
        {
            return fallback ?? 0f;
        }

        return negative ? -result : result;
    }

    private static float? ParseOptionalLength(string value, float parentFontSize, float viewportWidth)
    {
        // "auto" (or garbage) → auto.  Any real length — including the
        // various spellings of zero ("0", "0px", "0pt", "0%") — is a
        // length.  The old zero-or-"0" check turned "width: 0px" into
        // auto.
        string t = value.Trim();
        if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        return ParseLength(t, parentFontSize, viewportWidth);
    }

    /// <summary>True when a box-side value is the CSS auto keyword.</summary>
    private static bool IsAutoKeyword(string value) =>
        value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a top/right/bottom/left value: a percentage stays a
    /// percentage (resolved at layout against the containing block — width
    /// for left/right, height for top/bottom); anything else is a pixel
    /// length. "auto" and empty stay unset, matching the old
    /// ParseOptionalLength behaviour this replaces.
    /// </summary>
    private static (float? Length, float? Percent) ParseOffset(
        string value, float parentFontSize, float viewportWidth)
    {
        string t = value.Trim();
        if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (null, null);
        if (t.Length > 1 && t.EndsWith('%'))
        {
            if (float.TryParse(t[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float pct))
                return (null, pct);
            return (null, null);
        }
        return (ParseLength(t, parentFontSize, viewportWidth), null);
    }

    /// <summary>
    /// Parses a width/height value: a percentage stays a percentage
    /// (resolved at layout against the containing block); anything else
    /// is a pixel length or auto.
    /// </summary>
    private static (float? Length, float? Percent) ParseSize(
        string value, float parentFontSize, float viewportWidth)
    {
        string t = value.Trim();
        if (t.Length > 1 && t.EndsWith('%'))
        {
            if (float.TryParse(t[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float pct))
                return (null, pct);
            return (null, null);
        }
        return (ParseOptionalLength(t, parentFontSize, viewportWidth), null);
    }

    internal static string? ParseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string v = value.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && v.EndsWith(")"))
        {
            return v[4..^1].Trim().Trim('\'', '"');
        }
        return null;
    }

    private static BackgroundRepeat ParseBackgroundRepeat(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "repeat-x" => BackgroundRepeat.RepeatX,
            "repeat-y" => BackgroundRepeat.RepeatY,
            "no-repeat" => BackgroundRepeat.NoRepeat,
            _ => BackgroundRepeat.Repeat
        };
    }

    private static PointF ParseBackgroundPosition(string value)
    {
        var parts = SplitTopLevel(value);
        float x = 0, y = 0;
        bool sawX = false;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "left": x = 0; sawX = true; break;
                case "center": if (!sawX) { x = 50; sawX = true; } else { y = 50; } break;
                case "right": x = 100; sawX = true; break;
                case "top": y = 0; break;
                case "bottom": y = 100; break;
                default:
                    if (!sawX && TryParseFloat(part.TrimEnd('%'), out var px)) { x = px; sawX = true; }
                    else if (TryParseFloat(part.TrimEnd('%'), out var py)) { y = py; }
                    break;
            }
        }
        return new PointF(x, y);
    }

    private void ParseBackgroundShorthand(string value)
    {
        foreach (var part in SplitTopLevel(value))
        {
            var lower = part.ToLowerInvariant();
            if (lower.StartsWith("url(") || lower == "none")
                BackgroundImage = ParseUrl(part);
            else if (lower is "repeat" or "repeat-x" or "repeat-y" or "no-repeat")
                BackgroundRepeat = ParseBackgroundRepeat(part);
            else if (lower is "fixed" or "scroll")
                BackgroundFixed = lower == "fixed";
            else if (lower is "top" or "bottom" or "center" or "left" or "right" ||
                     lower.EndsWith("px") || lower.EndsWith("%"))
                BackgroundPosition = ParseBackgroundPosition(value);
            else
                BackgroundColor = ParseColor(part, BackgroundColor);
        }
    }

    private void ParseMarginShorthand(string value, float fs, float vw)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        MarginTop = ParseLength(p[0], fs, vw);
        MarginRight = ParseLength(p[1], fs, vw);
        MarginBottom = ParseLength(p[2], fs, vw);
        MarginLeft = ParseLength(p[3], fs, vw);
        // margin: 0 auto 0 auto — the classic centring shorthand.
        MarginRightAuto = IsAutoKeyword(p[1]);
        MarginLeftAuto = IsAutoKeyword(p[3]);
    }

    private void ParsePaddingShorthand(string value, float fs, float vw)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        PaddingTop = ParseLength(p[0], fs, vw);
        PaddingRight = ParseLength(p[1], fs, vw);
        PaddingBottom = ParseLength(p[2], fs, vw);
        PaddingLeft = ParseLength(p[3], fs, vw);
    }

    private static string[]? BoxShorthand(List<string> parts)
    {
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return new[] { parts[0], parts[0], parts[0], parts[0] };
        if (parts.Count == 2) return new[] { parts[0], parts[1], parts[0], parts[1] };
        if (parts.Count == 3) return new[] { parts[0], parts[1], parts[2], parts[1] };
        return new[] { parts[0], parts[1], parts[2], parts[3] };
    }

    private static float ParseBorderWidth(string value, float fs, float vw)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "thin" => 1f,
            "medium" => 2f,
            "thick" => 5f,
            _ => Math.Max(0f, ParseLength(value, fs, vw))
        };
    }

    private static BorderStyleValue ParseBorderStyle(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "hidden" => BorderStyleValue.Hidden,
            "dotted" => BorderStyleValue.Dotted,
            "dashed" => BorderStyleValue.Dashed,
            "solid" => BorderStyleValue.Solid,
            "double" => BorderStyleValue.Double,
            "groove" => BorderStyleValue.Groove,
            "ridge" => BorderStyleValue.Ridge,
            "inset" => BorderStyleValue.Inset,
            "outset" => BorderStyleValue.Outset,
            _ => BorderStyleValue.None
        };
    }

    private void ParseBorderWidthShorthand(string value, float fs, float vw)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        BorderTopWidth = ParseBorderWidth(p[0], fs, vw);
        BorderRightWidth = ParseBorderWidth(p[1], fs, vw);
        BorderBottomWidth = ParseBorderWidth(p[2], fs, vw);
        BorderLeftWidth = ParseBorderWidth(p[3], fs, vw);
    }

    private void ParseBorderStyleShorthand(string value)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        BorderTopStyle = ParseBorderStyle(p[0]);
        BorderRightStyle = ParseBorderStyle(p[1]);
        BorderBottomStyle = ParseBorderStyle(p[2]);
        BorderLeftStyle = ParseBorderStyle(p[3]);
    }

    private void ParseBorderColorShorthand(string value)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        BorderTopColor = ParseColor(p[0], BorderTopColor);
        BorderRightColor = ParseColor(p[1], BorderRightColor);
        BorderBottomColor = ParseColor(p[2], BorderBottomColor);
        BorderLeftColor = ParseColor(p[3], BorderLeftColor);
    }

    private void ParseBorderShorthand(string value, float fs, float vw)
    {
        foreach (var part in SplitTopLevel(value))
        {
            var lower = part.ToLowerInvariant();
            if (lower is "none" or "hidden" or "dotted" or "dashed" or "solid" or
                    "double" or "groove" or "ridge" or "inset" or "outset")
            {
                BorderTopStyle = BorderRightStyle = BorderBottomStyle = BorderLeftStyle =
                    ParseBorderStyle(part);
            }
            else if (lower is "thin" or "medium" or "thick" || IsLengthToken(lower))
            {
                var w = ParseBorderWidth(part, fs, vw);
                BorderTopWidth = BorderRightWidth = BorderBottomWidth = BorderLeftWidth = w;
            }
            else
            {
                var c = ParseColor(part, Color.Black);
                BorderTopColor = BorderRightColor = BorderBottomColor = BorderLeftColor = c;
            }
        }
    }

    /// <summary>Any token usable as a border width: px/pt/em or a bare number.</summary>
    private static bool IsLengthToken(string s) =>
        s.EndsWith("px") || s.EndsWith("pt") || s.EndsWith("em") ||
        (s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '.') && !s.Contains('('));

    private static DisplayValue ParseDisplay(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "block" => DisplayValue.Block,
            "inline" => DisplayValue.Inline,
            "inline-block" => DisplayValue.InlineBlock,
            "none" => DisplayValue.None,
            "list-item" => DisplayValue.ListItem,
            "table" => DisplayValue.Table,
            "table-row" => DisplayValue.TableRow,
            "table-cell" => DisplayValue.TableCell,
            "table-caption" => DisplayValue.TableCaption,
            "table-row-group" => DisplayValue.TableRowGroup,
            "table-column-group" => DisplayValue.TableColumnGroup,
            "table-column" => DisplayValue.TableColumn,
            "table-header-group" => DisplayValue.TableHeaderGroup,
            "table-footer-group" => DisplayValue.TableFooterGroup,
            _ => DisplayValue.Inline
        };
    }

    private static VisibilityValue ParseVisibility(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "hidden" => VisibilityValue.Hidden,
            "collapse" => VisibilityValue.Collapse,
            _ => VisibilityValue.Visible
        };
    }

    private static OverflowValue ParseOverflow(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "hidden" => OverflowValue.Hidden,
            "scroll" => OverflowValue.Scroll,
            "auto" => OverflowValue.Auto,
            _ => OverflowValue.Visible
        };
    }

    private static PositionValue ParsePosition(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "relative" => PositionValue.Relative,
            "absolute" => PositionValue.Absolute,
            "fixed" => PositionValue.Fixed,
            _ => PositionValue.Static
        };
    }

    private static FloatValue ParseFloat(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "left" => FloatValue.Left,
            "right" => FloatValue.Right,
            _ => FloatValue.None
        };
    }

    private static ClearValue ParseClear(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "left" => ClearValue.Left,
            "right" => ClearValue.Right,
            "both" => ClearValue.Both,
            _ => ClearValue.None
        };
    }

    private static int ParseZIndex(string value)
    {
        return int.TryParse(value.Trim(), out int z) ? z : 0;
    }

    internal static ListStyleType ParseListStyleType(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => ListStyleType.None,
            "circle" => ListStyleType.Circle,
            "square" => ListStyleType.Square,
            "decimal" => ListStyleType.Decimal,
            "lower-alpha" or "a" => ListStyleType.LowerAlpha,
            "upper-alpha" or "A" => ListStyleType.UpperAlpha,
            "lower-roman" or "i" => ListStyleType.LowerRoman,
            "upper-roman" or "I" => ListStyleType.UpperRoman,
            _ => ListStyleType.Disc
        };
    }

    private static ListStylePosition ParseListStylePosition(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "inside" => ListStylePosition.Inside,
            _ => ListStylePosition.Outside
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Small utilities
    // ─────────────────────────────────────────────────────────────────────

    private static bool TryParseFloat(string s, out float f) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

    /// <summary>Split on whitespace but keep parenthesised groups intact.</summary>
    private static List<string> SplitTopLevel(string value)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char c in value)
        {
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);

            if (depth == 0 && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}

// ── Supporting enums ─────────────────────────────────────────────────────

/// <summary>
/// Numeric values line up with the CSS scale so `weight >= Bold` means
/// "render bold".  (Lighter used to sit AFTER Bold in the enum, so
/// font-weight:lighter passed every boldness check in the engine.)
/// </summary>
public enum FontWeightValue
{
    W100 = 100, W200 = 200, W300 = 300, W400 = 400,
    W500 = 500, W600 = 600, W700 = 700, W800 = 800, W900 = 900,

    Lighter = 300,   // lighter than the parent — never bold
    Normal = 400,
    Bold = 700,
    Bolder = 800    // bolder than the parent — bold
}

public enum FontStyleValue { Normal, Italic, Oblique }
public enum FontVariantValue { Normal, SmallCaps }
public enum TextDecoration { None = 0, Underline = 1, Overline = 2, LineThrough = 4, Blink = 8 }
public enum TextAlign { Left, Center, Right, Justify }
public enum TextTransform { None, Capitalize, Uppercase, Lowercase }
public enum WhiteSpaceValue { Normal, Pre, Nowrap, PreWrap, PreLine }
public enum VerticalAlign { Baseline, Top, Middle, Bottom, TextTop, TextBottom, Super, Sub }
public enum BackgroundRepeat { Repeat, RepeatX, RepeatY, NoRepeat }

public enum DisplayValue
{
    Block, Inline, InlineBlock, None, ListItem,
    Table, TableRow, TableCell, TableCaption,
    TableRowGroup, TableColumnGroup, TableColumn,
    TableHeaderGroup, TableFooterGroup
}

public enum VisibilityValue { Visible, Hidden, Collapse }
public enum OverflowValue { Visible, Hidden, Scroll, Auto }
public enum PositionValue { Static, Relative, Absolute, Fixed }
public enum FloatValue { None, Left, Right }
public enum ClearValue { None, Left, Right, Both }

public enum BorderStyleValue
{
    None, Hidden, Dotted, Dashed, Solid, Double, Groove, Ridge, Inset, Outset
}

public enum ListStyleType
{
    None, Disc, Circle, Square,
    Decimal, LowerAlpha, UpperAlpha, LowerRoman, UpperRoman
}

public enum ListStylePosition { Inside, Outside }