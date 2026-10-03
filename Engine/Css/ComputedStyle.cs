using System;
using System.Collections.Generic;
using System.Drawing;

namespace Retro96.Engine.Css;

/// <summary>
/// ComputedStyle holds every CSS1 + 1996 presentational property
/// in its final resolved pixel / enum value.
/// All fields have correct initial values matching the 1996 Netscape Navigator
/// user-agent stylesheet defaults.
/// </summary>
public class ComputedStyle
{
    // === FONT ===
    public List<string> FontFamily { get; set; } = ["Times New Roman", "serif"];
    public float FontSize { get; set; } = 16f;           // px
    public FontWeightValue FontWeight { get; set; } = FontWeightValue.Normal;
    public FontStyleValue FontStyle { get; set; } = FontStyleValue.Normal;
    public FontVariantValue FontVariant { get; set; } = FontVariantValue.Normal;

    // === TEXT ===
    public Color Color { get; set; } = Color.Black;
    public TextDecoration TextDecoration { get; set; } = TextDecoration.None;
    public TextAlign TextAlign { get; set; } = TextAlign.Left;
    public float TextIndent { get; set; } = 0f;
    public float LineHeight { get; set; } = 1.2f;          // multiplier
    public float LetterSpacing { get; set; } = 0f;
    public float WordSpacing { get; set; } = 0f;
    public TextTransform TextTransform { get; set; } = TextTransform.None;
    public WhiteSpaceValue WhiteSpace { get; set; } = WhiteSpaceValue.Normal;
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Baseline;

    // === BACKGROUND ===
    public Color BackgroundColor { get; set; } = Color.Transparent;
    public string? BackgroundImage { get; set; }           // URL or null
    public BackgroundRepeat BackgroundRepeat { get; set; } = BackgroundRepeat.Repeat;
    public bool BackgroundFixed { get; set; }           // attachment
    public PointF BackgroundPosition { get; set; } = PointF.Empty;

    // === BOX MODEL (all in px) ===
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float BorderTopWidth, BorderRightWidth, BorderBottomWidth, BorderLeftWidth;
    public BorderStyleValue BorderTopStyle, BorderRightStyle,
                            BorderBottomStyle, BorderLeftStyle;
    public Color BorderTopColor, BorderRightColor,
                  BorderBottomColor, BorderLeftColor;
    public float? Width { get; set; }    // null = auto
    public float? Height { get; set; }   // null = auto

    // === DISPLAY ===
    public DisplayValue Display { get; set; } = DisplayValue.Inline;
    public VisibilityValue Visibility { get; set; } = VisibilityValue.Visible;
    public OverflowValue Overflow { get; set; } = OverflowValue.Visible;

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

    /// <summary>
    /// Create a new ComputedStyle by applying own declarations on top of parent's inherited values.
    /// </summary>
    public static ComputedStyle Inherit(ComputedStyle parent, IEnumerable<CssDeclaration> own)
    {
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));
        if (own == null)
            throw new ArgumentNullException(nameof(own));

        var child = new ComputedStyle();

        // Inherit inheritable properties from parent
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
        // NOTE: background-color, background-image, borders, margins, padding,
        // display, position, float, width, height are NOT CSS-inherited properties.
        // They must NOT be copied here or every child paints the body's background
        // over itself, producing a fully grey/colored canvas with invisible content.
        child.Visibility = parent.Visibility;

        // Non-inherited properties get their defaults (already set in constructor)
        // Box model, display, positioning, lists are NOT inherited

        // Apply own declarations
        foreach (var decl in own)
        {
            child.Apply(decl, parent.FontSize, 800f); // 800px viewport width default
        }

        return child;
    }

    /// <summary>
    /// Parse and apply a single declaration in place.
    /// </summary>
    public void Apply(CssDeclaration decl, float parentFontSize, float viewportWidth)
    {
        if (decl == null)
            return;

        var property = decl.Property.ToLowerInvariant();
        var value = decl.Value.Trim();

        switch (property)
        {
            // === FONT PROPERTIES ===
            case "font-family":
                FontFamily = ParseFontFamily(value);
                break;
            case "font-size":
                FontSize = ParseFontSize(value, parentFontSize);
                break;
            case "font-weight":
                FontWeight = ParseFontWeight(value);
                break;
            case "font-style":
                FontStyle = ParseFontStyle(value);
                break;
            case "font-variant":
                FontVariant = ParseFontVariant(value);
                break;
            case "font":
                ParseFontShorthand(value, parentFontSize);
                break;

            // === TEXT PROPERTIES ===
            case "color":
                Color = ParseColor(value);
                break;
            case "text-decoration":
                TextDecoration = ParseTextDecoration(value);
                break;
            case "text-align":
                TextAlign = ParseTextAlign(value);
                break;
            case "text-indent":
                TextIndent = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "line-height":
                LineHeight = ParseLineHeight(value, parentFontSize);
                break;
            case "letter-spacing":
                LetterSpacing = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "word-spacing":
                WordSpacing = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "text-transform":
                TextTransform = ParseTextTransform(value);
                break;
            case "white-space":
                WhiteSpace = ParseWhiteSpace(value);
                break;
            case "vertical-align":
                VerticalAlign = ParseVerticalAlign(value);
                break;

            // === BACKGROUND PROPERTIES ===
            case "background-color":
                BackgroundColor = ParseColor(value);
                break;
            case "background-image":
                BackgroundImage = ParseUrl(value);
                break;
            case "background-repeat":
                BackgroundRepeat = ParseBackgroundRepeat(value);
                break;
            case "background-attachment":
                BackgroundFixed = value.Equals("fixed", StringComparison.OrdinalIgnoreCase);
                break;
            case "background-position":
                BackgroundPosition = ParseBackgroundPosition(value);
                break;
            case "background":
                ParseBackgroundShorthand(value);
                break;

            // === BOX MODEL ===
            case "margin-top":
                MarginTop = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "margin-right":
                MarginRight = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "margin-bottom":
                MarginBottom = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "margin-left":
                MarginLeft = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "margin":
                ParseMarginShorthand(value, parentFontSize, viewportWidth);
                break;

            case "padding-top":
                PaddingTop = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "padding-right":
                PaddingRight = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "padding-bottom":
                PaddingBottom = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "padding-left":
                PaddingLeft = ParseLength(value, parentFontSize, viewportWidth);
                break;
            case "padding":
                ParsePaddingShorthand(value, parentFontSize, viewportWidth);
                break;

            case "border-top-width":
                BorderTopWidth = ParseBorderWidth(value, parentFontSize, viewportWidth);
                break;
            case "border-right-width":
                BorderRightWidth = ParseBorderWidth(value, parentFontSize, viewportWidth);
                break;
            case "border-bottom-width":
                BorderBottomWidth = ParseBorderWidth(value, parentFontSize, viewportWidth);
                break;
            case "border-left-width":
                BorderLeftWidth = ParseBorderWidth(value, parentFontSize, viewportWidth);
                break;

            case "border-top-style":
                BorderTopStyle = ParseBorderStyle(value);
                break;
            case "border-right-style":
                BorderRightStyle = ParseBorderStyle(value);
                break;
            case "border-bottom-style":
                BorderBottomStyle = ParseBorderStyle(value);
                break;
            case "border-left-style":
                BorderLeftStyle = ParseBorderStyle(value);
                break;

            case "border-top-color":
                BorderTopColor = ParseColor(value);
                break;
            case "border-right-color":
                BorderRightColor = ParseColor(value);
                break;
            case "border-bottom-color":
                BorderBottomColor = ParseColor(value);
                break;
            case "border-left-color":
                BorderLeftColor = ParseColor(value);
                break;

            case "border-width":
                ParseBorderWidthShorthand(value, parentFontSize, viewportWidth);
                break;
            case "border-style":
                ParseBorderStyleShorthand(value);
                break;
            case "border-color":
                ParseBorderColorShorthand(value);
                break;
            case "border":
                ParseBorderShorthand(value, parentFontSize, viewportWidth);
                break;

            // === DISPLAY ===
            case "display":
                Display = ParseDisplay(value);
                break;
            case "visibility":
                Visibility = ParseVisibility(value);
                break;
            case "overflow":
                Overflow = ParseOverflow(value);
                break;

            // === POSITIONING ===
            case "position":
                Position = ParsePosition(value);
                break;
            case "top":
                Top = ParseOptionalLength(value, parentFontSize, viewportWidth);
                break;
            case "right":
                Right = ParseOptionalLength(value, parentFontSize, viewportWidth);
                break;
            case "bottom":
                Bottom = ParseOptionalLength(value, parentFontSize, viewportWidth);
                break;
            case "left":
                Left = ParseOptionalLength(value, parentFontSize, viewportWidth);
                break;
            case "float":
                Float = ParseFloat(value);
                break;
            case "clear":
                Clear = ParseClear(value);
                break;
            case "z-index":
                ZIndex = ParseZIndex(value);
                break;

            // === LISTS ===
            case "list-style-type":
                ListStyleType = ParseListStyleType(value);
                break;
            case "list-style-image":
                ListStyleImage = ParseUrl(value);
                break;
            case "list-style-position":
                ListStylePosition = ParseListStylePosition(value);
                break;
            case "list-style":
                ParseListStyleShorthand(value);
                break;

            // Width and height
            case "width":
                Width = ParseOptionalLength(value, parentFontSize, viewportWidth);
                break;
            case "height":
                Height = ParseOptionalLength(value, parentFontSize, viewportWidth);
                break;

            default:
                // Unknown property - skip (log to console in debug mode)
                System.Diagnostics.Debug.WriteLine($"Unknown CSS property: {property}");
                break;
        }
    }

    /// <summary>
    /// Clone this style.
    /// </summary>
    public ComputedStyle Clone()
    {
        return new ComputedStyle
        {
            FontFamily = new List<string>(FontFamily),
            FontSize = FontSize,
            FontWeight = FontWeight,
            FontStyle = FontStyle,
            FontVariant = FontVariant,
            Color = Color,
            TextDecoration = TextDecoration,
            TextAlign = TextAlign,
            TextIndent = TextIndent,
            LineHeight = LineHeight,
            LetterSpacing = LetterSpacing,
            WordSpacing = WordSpacing,
            TextTransform = TextTransform,
            WhiteSpace = WhiteSpace,
            VerticalAlign = VerticalAlign,
            BackgroundColor = BackgroundColor,
            BackgroundImage = BackgroundImage,
            BackgroundRepeat = BackgroundRepeat,
            BackgroundFixed = BackgroundFixed,
            BackgroundPosition = BackgroundPosition,
            MarginTop = MarginTop,
            MarginRight = MarginRight,
            MarginBottom = MarginBottom,
            MarginLeft = MarginLeft,
            PaddingTop = PaddingTop,
            PaddingRight = PaddingRight,
            PaddingBottom = PaddingBottom,
            PaddingLeft = PaddingLeft,
            BorderTopWidth = BorderTopWidth,
            BorderRightWidth = BorderRightWidth,
            BorderBottomWidth = BorderBottomWidth,
            BorderLeftWidth = BorderLeftWidth,
            BorderTopStyle = BorderTopStyle,
            BorderRightStyle = BorderRightStyle,
            BorderBottomStyle = BorderBottomStyle,
            BorderLeftStyle = BorderLeftStyle,
            BorderTopColor = BorderTopColor,
            BorderRightColor = BorderRightColor,
            BorderBottomColor = BorderBottomColor,
            BorderLeftColor = BorderLeftColor,
            Width = Width,
            Height = Height,
            Display = Display,
            Visibility = Visibility,
            Overflow = Overflow,
            Position = Position,
            Top = Top,
            Right = Right,
            Bottom = Bottom,
            Left = Left,
            Float = Float,
            Clear = Clear,
            ZIndex = ZIndex,
            ListStyleType = ListStyleType,
            ListStyleImage = ListStyleImage,
            ListStylePosition = ListStylePosition
        };
    }

    // === PARSING HELPER METHODS (static - don't access instance) ===

    private static List<string> ParseFontFamily(string value)
    {
        var families = new List<string>();
        var parts = value.Split(',');
        foreach (var part in parts)
        {
            var family = part.Trim().Trim('\'', '"');
            if (!string.IsNullOrEmpty(family))
                families.Add(family);
        }
        return families;
    }

    private static float ParseFontSize(string value, float parentFontSize)
    {
        // Handle absolute sizes (Netscape font size mapping)
        switch (value.ToLowerInvariant())
        {
            case "1": return 8f;   // NN4 quirk
            case "2": return 10f;
            case "3": return 16f;  // base
            case "4": return 18f;
            case "5": return 24f;
            case "6": return 32f;
            case "7": return 48f;
            case "xx-small": return 8f;
            case "x-small": return 10f;
            case "small": return 13f;
            case "medium": return 16f;
            case "large": return 18f;
            case "x-large": return 24f;
            case "xx-large": return 32f;
            case "smaller": return parentFontSize * 0.83f;
            case "larger": return parentFontSize * 1.17f;
        }

        // Handle relative sizes like "+1", "-1"
        if (value.StartsWith("+") || value.StartsWith("-"))
        {
            if (int.TryParse(value, out int relSize))
            {
                // Map relative to absolute based on base size 3 = 16px
                int baseSize = 3;
                int newSize = baseSize + relSize;
                return newSize switch
                {
                    1 => 8f, 2 => 10f, 3 => 16f, 4 => 18f, 5 => 24f, 6 => 32f, 7 => 48f,
                    < 1 => 8f,
                    > 7 => 48f,
                };
            }
        }

        return ParseLength(value, parentFontSize, 800f);
    }

    private static FontWeightValue ParseFontWeight(string value)
    {
        return value.ToLowerInvariant() switch
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
        return value.ToLowerInvariant() switch
        {
            "normal" => FontStyleValue.Normal,
            "italic" => FontStyleValue.Italic,
            "oblique" => FontStyleValue.Oblique,
            _ => FontStyleValue.Normal
        };
    }

    private static FontVariantValue ParseFontVariant(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "normal" => FontVariantValue.Normal,
            "small-caps" => FontVariantValue.SmallCaps,
            _ => FontVariantValue.Normal
        };
    }

    private void ParseFontShorthand(string value, float parentFontSize)
    {
        // font: [style] [variant] [weight] size [/line-height] family
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? size = null;
        string? family = null;
        var remaining = new List<string>();

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].ToLowerInvariant();
            if (part == "italic" || part == "oblique")
                FontStyle = ParseFontStyle(part);
            else if (part == "small-caps")
                FontVariant = FontVariantValue.SmallCaps;
            else if (part == "bold" || part == "bolder" || part == "lighter" ||
                     part == "100" || part == "200" || part == "300" || part == "400" ||
                     part == "500" || part == "600" || part == "700" || part == "800" || part == "900")
                FontWeight = ParseFontWeight(part);
            else if (part.Contains("px") || part.Contains("em") || part.Contains("%") ||
                     part == "xx-small" || part == "x-small" || part == "small" ||
                     part == "medium" || part == "large" || part == "x-large" || part == "xx-large")
            {
                size = parts[i];
                // Check for line-height
                if (i + 1 < parts.Length && parts[i + 1].StartsWith("/"))
                {
                    var lineHeightStr = parts[i + 1].Substring(1);
                    LineHeight = ParseLineHeight(lineHeightStr, parentFontSize);
                    i++;
                }
                // Remaining parts are font family
                if (i + 1 < parts.Length)
                {
                    family = string.Join(" ", parts, i + 1, parts.Length - i - 1);
                }
                break;
            }
        }

        if (size != null)
            FontSize = ParseFontSize(size, parentFontSize);
        if (family != null)
            FontFamily = ParseFontFamily(family);
    }

    private static Color ParseColor(string value)
    {
        if (string.IsNullOrEmpty(value) || value == "inherit")
            return Color.Black;

        // Handle named colors
        try { return ColorTranslator.FromHtml(value); }
        catch { /* ignore */ }

        // Handle hex colors
        if (value.StartsWith("#"))
        {
            var hex = value.Substring(1);
            if (hex.Length == 3) // #RGB -> #RRGGBB (NN28 quirk)
            {
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }
            if (hex.Length == 6)
            {
                try
                {
                    int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    return Color.FromArgb(r, g, b);
                }
                catch { /* ignore */ }
            }
        }

        // Handle rgb()
        if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            var rgb = value.Substring(4, value.Length - 5);
            var parts = rgb.Split(',');
            if (parts.Length == 3)
            {
                try
                {
                    int r = int.Parse(parts[0].Trim());
                    int g = int.Parse(parts[1].Trim());
                    int b = int.Parse(parts[2].Trim());
                    return Color.FromArgb(r, g, b);
                }
                catch { /* ignore */ }
            }
        }

        return Color.Black; // Default fallback
    }

    private static TextDecoration ParseTextDecoration(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => TextDecoration.None,
            "underline" => TextDecoration.Underline,
            "overline" => TextDecoration.Overline,
            "line-through" => TextDecoration.LineThrough,
            "blink" => TextDecoration.Blink,
            _ => TextDecoration.None
        };
    }

    private static TextAlign ParseTextAlign(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "left" => TextAlign.Left,
            "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            "justify" => TextAlign.Justify,
            _ => TextAlign.Left
        };
    }

    private static float ParseLineHeight(string value, float parentFontSize)
    {
        if (value == "normal")
            return 1.2f;
        if (value.EndsWith("%"))
        {
            if (float.TryParse(value.TrimEnd('%'), out float pct))
                return pct / 100f;
        }
        if (float.TryParse(value, out float num))
            return num;
        // Unitless number
        if (float.TryParse(value, out float mult))
            return mult;
        return 1.2f;
    }

    private static TextTransform ParseTextTransform(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => TextTransform.None,
            "capitalize" => TextTransform.Capitalize,
            "uppercase" => TextTransform.Uppercase,
            "lowercase" => TextTransform.Lowercase,
            _ => TextTransform.None
        };
    }

    private static WhiteSpaceValue ParseWhiteSpace(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "normal" => WhiteSpaceValue.Normal,
            "pre" => WhiteSpaceValue.Pre,
            "nowrap" => WhiteSpaceValue.Nowrap,
            "pre-wrap" => WhiteSpaceValue.PreWrap,
            "pre-line" => WhiteSpaceValue.PreLine,
            _ => WhiteSpaceValue.Normal
        };
    }

    private static VerticalAlign ParseVerticalAlign(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "baseline" => VerticalAlign.Baseline,
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

    private static float ParseLength(string value, float parentFontSize, float viewportWidth)
    {
        if (value == "0" || value == "auto")
            return 0f;

        if (value.EndsWith("px"))
        {
            if (float.TryParse(value.Substring(0, value.Length - 2), out float px))
                return px;
        }
        else if (value.EndsWith("em"))
        {
            if (float.TryParse(value.Substring(0, value.Length - 2), out float em))
                return em * parentFontSize;
        }
        else if (value.EndsWith("%"))
        {
            if (float.TryParse(value.TrimEnd('%'), out float pct))
                return pct / 100f * viewportWidth;
        }
        else if (float.TryParse(value, out float num))
            return num;

        return 0f;
    }

    private static float? ParseOptionalLength(string value, float parentFontSize, float viewportWidth)
    {
        if (value == "auto")
            return null;
        return ParseLength(value, parentFontSize, viewportWidth);
    }

    private static string? ParseUrl(string value)
    {
        if (value == "none")
            return null;
        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var url = value.Substring(4, value.Length - 5).Trim().Trim('\'', '"');
            return url;
        }
        return null;
    }

    private static BackgroundRepeat ParseBackgroundRepeat(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "repeat" => BackgroundRepeat.Repeat,
            "repeat-x" => BackgroundRepeat.RepeatX,
            "repeat-y" => BackgroundRepeat.RepeatY,
            "no-repeat" => BackgroundRepeat.NoRepeat,
            _ => BackgroundRepeat.Repeat
        };
    }

    private static PointF ParseBackgroundPosition(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            float x = parts[0] == "left" ? 0 : parts[0] == "center" ? 50 : parts[0] == "right" ? 100 : 0;
            float y = parts[1] == "top" ? 0 : parts[1] == "center" ? 50 : parts[1] == "bottom" ? 100 : 0;
            return new PointF(x, y);
        }
        return PointF.Empty;
    }

    private void ParseBackgroundShorthand(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].ToLowerInvariant();
            if (part == "none" || part.StartsWith("url("))
                BackgroundImage = ParseUrl(parts[i]);
            else if (part == "repeat" || part == "repeat-x" || part == "repeat-y" || part == "no-repeat")
                BackgroundRepeat = ParseBackgroundRepeat(parts[i]);
            else if (part == "fixed" || part == "scroll")
                BackgroundFixed = part == "fixed";
            else if (part.StartsWith("#") || part.StartsWith("rgb("))
                BackgroundColor = ParseColor(parts[i]);
        }
    }

    private void ParseMarginShorthand(string value, float parentFontSize, float viewportWidth)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                MarginTop = MarginRight = MarginBottom = MarginLeft = ParseLength(parts[0], parentFontSize, viewportWidth);
                break;
            case 2:
                MarginTop = MarginBottom = ParseLength(parts[0], parentFontSize, viewportWidth);
                MarginRight = MarginLeft = ParseLength(parts[1], parentFontSize, viewportWidth);
                break;
            case 3:
                MarginTop = ParseLength(parts[0], parentFontSize, viewportWidth);
                MarginRight = MarginLeft = ParseLength(parts[1], parentFontSize, viewportWidth);
                MarginBottom = ParseLength(parts[2], parentFontSize, viewportWidth);
                break;
            case 4:
                MarginTop = ParseLength(parts[0], parentFontSize, viewportWidth);
                MarginRight = ParseLength(parts[1], parentFontSize, viewportWidth);
                MarginBottom = ParseLength(parts[2], parentFontSize, viewportWidth);
                MarginLeft = ParseLength(parts[3], parentFontSize, viewportWidth);
                break;
        }
    }

    private void ParsePaddingShorthand(string value, float parentFontSize, float viewportWidth)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                PaddingTop = PaddingRight = PaddingBottom = PaddingLeft = ParseLength(parts[0], parentFontSize, viewportWidth);
                break;
            case 2:
                PaddingTop = PaddingBottom = ParseLength(parts[0], parentFontSize, viewportWidth);
                PaddingRight = PaddingLeft = ParseLength(parts[1], parentFontSize, viewportWidth);
                break;
            case 3:
                PaddingTop = ParseLength(parts[0], parentFontSize, viewportWidth);
                PaddingRight = PaddingLeft = ParseLength(parts[1], parentFontSize, viewportWidth);
                PaddingBottom = ParseLength(parts[2], parentFontSize, viewportWidth);
                break;
            case 4:
                PaddingTop = ParseLength(parts[0], parentFontSize, viewportWidth);
                PaddingRight = ParseLength(parts[1], parentFontSize, viewportWidth);
                PaddingBottom = ParseLength(parts[2], parentFontSize, viewportWidth);
                PaddingLeft = ParseLength(parts[3], parentFontSize, viewportWidth);
                break;
        }
    }

    private static float ParseBorderWidth(string value, float parentFontSize, float viewportWidth)
    {
        return value.ToLowerInvariant() switch
        {
            "thin" => 1f,
            "medium" => 3f,
            "thick" => 5f,
            _ => ParseLength(value, parentFontSize, viewportWidth)
        };
    }

    private static BorderStyleValue ParseBorderStyle(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => BorderStyleValue.None,
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

    private void ParseBorderWidthShorthand(string value, float parentFontSize, float viewportWidth)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                BorderTopWidth = BorderRightWidth = BorderBottomWidth = BorderLeftWidth = ParseBorderWidth(parts[0], parentFontSize, viewportWidth);
                break;
            case 2:
                BorderTopWidth = BorderBottomWidth = ParseBorderWidth(parts[0], parentFontSize, viewportWidth);
                BorderRightWidth = BorderLeftWidth = ParseBorderWidth(parts[1], parentFontSize, viewportWidth);
                break;
            case 3:
                BorderTopWidth = ParseBorderWidth(parts[0], parentFontSize, viewportWidth);
                BorderRightWidth = BorderLeftWidth = ParseBorderWidth(parts[1], parentFontSize, viewportWidth);
                BorderBottomWidth = ParseBorderWidth(parts[2], parentFontSize, viewportWidth);
                break;
            case 4:
                BorderTopWidth = ParseBorderWidth(parts[0], parentFontSize, viewportWidth);
                BorderRightWidth = ParseBorderWidth(parts[1], parentFontSize, viewportWidth);
                BorderBottomWidth = ParseBorderWidth(parts[2], parentFontSize, viewportWidth);
                BorderLeftWidth = ParseBorderWidth(parts[3], parentFontSize, viewportWidth);
                break;
        }
    }

    private void ParseBorderStyleShorthand(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                BorderTopStyle = BorderRightStyle = BorderBottomStyle = BorderLeftStyle = ParseBorderStyle(parts[0]);
                break;
            case 2:
                BorderTopStyle = BorderBottomStyle = ParseBorderStyle(parts[0]);
                BorderRightStyle = BorderLeftStyle = ParseBorderStyle(parts[1]);
                break;
            case 3:
                BorderTopStyle = ParseBorderStyle(parts[0]);
                BorderRightStyle = BorderLeftStyle = ParseBorderStyle(parts[1]);
                BorderBottomStyle = ParseBorderStyle(parts[2]);
                break;
            case 4:
                BorderTopStyle = ParseBorderStyle(parts[0]);
                BorderRightStyle = ParseBorderStyle(parts[1]);
                BorderBottomStyle = ParseBorderStyle(parts[2]);
                BorderLeftStyle = ParseBorderStyle(parts[3]);
                break;
        }
    }

    private void ParseBorderColorShorthand(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Color ParseCol(string v) => ParseColor(v);
        switch (parts.Length)
        {
            case 1:
                BorderTopColor = BorderRightColor = BorderBottomColor = BorderLeftColor = ParseCol(parts[0]);
                break;
            case 2:
                BorderTopColor = BorderBottomColor = ParseCol(parts[0]);
                BorderRightColor = BorderLeftColor = ParseCol(parts[1]);
                break;
            case 3:
                BorderTopColor = ParseCol(parts[0]);
                BorderRightColor = BorderLeftColor = ParseCol(parts[1]);
                BorderBottomColor = ParseCol(parts[2]);
                break;
            case 4:
                BorderTopColor = ParseCol(parts[0]);
                BorderRightColor = ParseCol(parts[1]);
                BorderBottomColor = ParseCol(parts[2]);
                BorderLeftColor = ParseCol(parts[3]);
                break;
        }
    }

    private void ParseBorderShorthand(string value, float parentFontSize, float viewportWidth)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            if (lower == "none" || lower == "hidden" || lower == "dotted" || lower == "dashed" ||
                lower == "solid" || lower == "double" || lower == "groove" || lower == "ridge" ||
                lower == "inset" || lower == "outset")
            {
                var style = ParseBorderStyle(part);
                BorderTopStyle = BorderRightStyle = BorderBottomStyle = BorderLeftStyle = style;
            }
            else if (lower == "thin" || lower == "medium" || lower == "thick" || lower.Contains("px"))
            {
                var width = ParseBorderWidth(part, parentFontSize, viewportWidth);
                BorderTopWidth = BorderRightWidth = BorderBottomWidth = BorderLeftWidth = width;
            }
            else
            {
                BorderTopColor = BorderRightColor = BorderBottomColor = BorderLeftColor = ParseColor(part);
            }
        }
    }

    private static DisplayValue ParseDisplay(string value)
    {
        return value.ToLowerInvariant() switch
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
        return value.ToLowerInvariant() switch
        {
            "visible" => VisibilityValue.Visible,
            "hidden" => VisibilityValue.Hidden,
            "collapse" => VisibilityValue.Collapse,
            _ => VisibilityValue.Visible
        };
    }

    private static OverflowValue ParseOverflow(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "visible" => OverflowValue.Visible,
            "hidden" => OverflowValue.Hidden,
            "scroll" => OverflowValue.Scroll,
            "auto" => OverflowValue.Auto,
            _ => OverflowValue.Visible
        };
    }

    private static PositionValue ParsePosition(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "static" => PositionValue.Static,
            "relative" => PositionValue.Relative,
            "absolute" => PositionValue.Absolute,
            "fixed" => PositionValue.Fixed,
            _ => PositionValue.Static
        };
    }

    private static FloatValue ParseFloat(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => FloatValue.None,
            "left" => FloatValue.Left,
            "right" => FloatValue.Right,
            _ => FloatValue.None
        };
    }

    private static ClearValue ParseClear(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => ClearValue.None,
            "left" => ClearValue.Left,
            "right" => ClearValue.Right,
            "both" => ClearValue.Both,
            _ => ClearValue.None
        };
    }

    private static int ParseZIndex(string value)
    {
        if (int.TryParse(value, out int z))
            return z;
        return 0;
    }

    private static ListStyleType ParseListStyleType(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => ListStyleType.None,
            "disc" => ListStyleType.Disc,
            "circle" => ListStyleType.Circle,
            "square" => ListStyleType.Square,
            "decimal" => ListStyleType.Decimal,
            "lower-alpha" or "lower-alpha" => ListStyleType.LowerAlpha,
            "upper-alpha" or "upper-alpha" => ListStyleType.UpperAlpha,
            "lower-roman" or "lower-roman" => ListStyleType.LowerRoman,
            "upper-roman" or "upper-roman" => ListStyleType.UpperRoman,
            _ => ListStyleType.Disc
        };
    }

    private static ListStylePosition ParseListStylePosition(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "inside" => ListStylePosition.Inside,
            "outside" => ListStylePosition.Outside,
            _ => ListStylePosition.Outside
        };
    }

    private void ParseListStyleShorthand(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            if (lower == "none" || lower == "disc" || lower == "circle" || lower == "square" ||
                lower == "decimal" || lower == "lower-alpha" || lower == "upper-alpha" ||
                lower == "lower-roman" || lower == "upper-roman")
                ListStyleType = ParseListStyleType(part);
            else if (lower == "inside" || lower == "outside")
                ListStylePosition = ParseListStylePosition(part);
            else if (lower == "none" || lower.StartsWith("url("))
                ListStyleImage = ParseUrl(part);
        }
    }
}

// === SUPPORTING ENUMS ===

public enum FontWeightValue
{
    Normal = 400,
    Bold = 700,
    Bolder,
    Lighter,
    W100 = 100,
    W200 = 200,
    W300 = 300,
    W400 = 400,
    W500 = 500,
    W600 = 600,
    W700 = 700,
    W800 = 800,
    W900 = 900
}

public enum FontStyleValue { Normal, Italic, Oblique }

public enum FontVariantValue { Normal, SmallCaps }

public enum TextDecoration { None, Underline, Overline, LineThrough, Blink }

public enum TextAlign { Left, Center, Right, Justify }

public enum TextTransform { None, Capitalize, Uppercase, Lowercase }

public enum WhiteSpaceValue { Normal, Pre, Nowrap, PreWrap, PreLine }

public enum VerticalAlign { Baseline, Top, Middle, Bottom, TextTop, TextBottom, Super, Sub }

public enum BackgroundRepeat { Repeat, RepeatX, RepeatY, NoRepeat }

public enum DisplayValue
{
    Block,
    Inline,
    InlineBlock,
    None,
    ListItem,
    Table,
    TableRow,
    TableCell,
    TableCaption,
    TableRowGroup,
    TableColumnGroup,
    TableColumn,
    TableHeaderGroup,
    TableFooterGroup
}

public enum VisibilityValue { Visible, Hidden, Collapse }

public enum OverflowValue { Visible, Hidden, Scroll, Auto }

public enum PositionValue { Static, Relative, Absolute, Fixed }

public enum FloatValue { None, Left, Right }

public enum ClearValue { None, Left, Right, Both }

public enum BorderStyleValue
{
    None,
    Hidden,
    Dotted,
    Dashed,
    Solid,
    Double,
    Groove,
    Ridge,
    Inset,
    Outset
}

public enum ListStyleType
{
    None,
    Disc,
    Circle,
    Square,
    Decimal,
    LowerAlpha,
    UpperAlpha,
    LowerRoman,
    UpperRoman
}

public enum ListStylePosition { Inside, Outside }