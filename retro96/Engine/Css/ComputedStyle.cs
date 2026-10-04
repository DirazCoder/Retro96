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
    public float? TextIndentPercent { get; set; }
    public LineHeightMode LineHeightMode { get; set; } = LineHeightMode.Normal;
    public float LineHeight { get; set; } = 0f;
    public float LineHeightPixels { get; set; } = 0f;
    private string? PendingLineHeight { get; set; }
    public float LetterSpacing { get; set; } = 0f;
    public float WordSpacing { get; set; } = 0f;
    public TextTransform TextTransform { get; set; } = TextTransform.None;
    public WhiteSpaceValue WhiteSpace { get; set; } = WhiteSpaceValue.Normal;
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Baseline;
    public float? VerticalAlignPercent { get; set; }
    /// <summary>CSS2: vertical-align &lt;length&gt; value in px (signed).
    /// Non-null only when an authored length was given; keywords keep
    /// VerticalAlign and % keeps VerticalAlignPercent.</summary>
    public float? VerticalAlignLength { get; set; }
    public bool OwnVerticalAlign { get; set; }

    // === BACKGROUND ===
    public Color BackgroundColor { get; set; } = Color.Transparent;
    public string? BackgroundImage { get; set; }           // URL or null
    public BackgroundRepeat BackgroundRepeat { get; set; } = BackgroundRepeat.Repeat;
    public bool BackgroundFixed { get; set; }
    public PointF BackgroundPosition { get; set; } = new PointF(0, 0);
    public float? BackgroundPositionXLength { get; set; }
    public float? BackgroundPositionYLength { get; set; }

    /// <summary>Authored (vs inherited) colour flags — set ONLY by
    /// declarations this element's own inline STYLE or matched CSS rules
    /// carry (everything flows through Apply).  The renderer consults them
    /// to decide whether a form control takes CSS1 colours (IE3-era form
    /// styling) or the classic native look: an inherited BODY text=
    /// colour must never turn input text invisible-on-white.</summary>
    public bool OwnColor, OwnBackground, OwnTextAlign, OwnMarginLeft, OwnPaddingLeft;
    public bool OwnListStyleType, OwnListStyleImage;
    public bool OwnBorderTopStyle, OwnBorderRightStyle, OwnBorderBottomStyle, OwnBorderLeftStyle;

    // === BOX MODEL (px) ===
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float? MarginTopPercent, MarginRightPercent, MarginBottomPercent, MarginLeftPercent;
    public float PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
    public float? PaddingTopPercent, PaddingRightPercent, PaddingBottomPercent, PaddingLeftPercent;
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

    // CSS2 min/max size constraints.  Like width/height, percentages stay
    // percentages (resolved at LAYOUT against the containing block).
    // MinWidth null = 0 (the CSS2 initial value); MaxWidth null = none.
    public float? MinWidth { get; set; }
    public float? MaxWidth { get; set; }
    public float? MinHeight { get; set; }
    public float? MaxHeight { get; set; }
    public float? MinWidthPercent { get; set; }
    public float? MaxWidthPercent { get; set; }
    public float? MinHeightPercent { get; set; }
    public float? MaxHeightPercent { get; set; }

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

    // === CSS2 CURSOR (inherited; rendering is the shell's job) ===
    public CursorValue Cursor { get; set; } = CursorValue.Auto;
    /// <summary>First url() of a cursor list (cursor: url(x.cur), pointer).
    /// Null when the value has no URI part.</summary>
    public string? CursorUri { get; set; }

    // === CSS2 OUTLINE (non-inherited; layout/paint is the integrator's) ===
    public float OutlineWidth { get; set; }              // px
    public BorderStyleValue OutlineStyle { get; set; } = BorderStyleValue.None;
    public Color OutlineColor { get; set; } = Color.Black;
    /// <summary>outline-color: invert — the CSS2 initial value; true until
    /// an explicit colour is authored.</summary>
    public bool OutlineColorInvert { get; set; } = true;

    // === CSS2 TABLE PROPERTIES (non-inherited) ===
    public BorderCollapseValue BorderCollapse { get; set; } = BorderCollapseValue.Separate;
    public float BorderSpacingX { get; set; }           // px
    public float BorderSpacingY { get; set; }           // px (defaults to X when single value)
    public TableLayoutValue TableLayout { get; set; } = TableLayoutValue.Auto;
    public CaptionSideValue CaptionSide { get; set; } = CaptionSideValue.Top;
    public EmptyCellsValue EmptyCells { get; set; } = EmptyCellsValue.Show;

    // === CSS2 FONT EXTRAS ===
    public float? FontSizeAdjust { get; set; }          // null = none
    public string? FontStretch { get; set; }            // raw keyword

    // === CSS2 BIDI / TEXT (stored; BDO rendering is later work) ===
    public DirectionValue Direction { get; set; } = DirectionValue.Ltr;   // inherited
    public string? UnicodeBidi { get; set; }            // raw keyword: normal|embed|bidi-override
    public string? TextShadow { get; set; }             // raw value (parse-level only — nobody shipped it in 1999)

    // === CSS2 GENERATED CONTENT ===
    /// <summary>Parsed content value: (type, text) tokens — "string",
    /// "attr", "uri", "counter", "counters", "open-quote"/"close-quote"/
    /// "no-open-quote"/"no-close-quote".  Null = none/normal/unset.
    /// Rendering is the integrator's job.</summary>
    public List<ContentToken>? Content { get; set; }
    /// <summary>quotes: pairs of open/close strings (inherited).
    /// Null = default quotes.</summary>
    public List<QuotePair>? Quotes { get; set; }
    /// <summary>counter-reset list ([name, value] — value defaults to 0).</summary>
    public List<CounterAction>? CounterReset { get; set; }
    /// <summary>counter-increment list (value defaults to 1).</summary>
    public List<CounterAction>? CounterIncrement { get; set; }

    // Pseudo-element declaration slots, exactly like FirstLineStyle /
    // FirstLetterStyle.  :before / ::before declarations land in
    // GeneratedBefore; :after / ::after in GeneratedAfter.
    public ComputedStyle? GeneratedBefore { get; set; }
    public ComputedStyle? GeneratedAfter { get; set; }

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
        child.TextIndentPercent = parent.TextIndentPercent;
        child.LineHeightMode = parent.LineHeightMode;
        child.LineHeight = parent.LineHeight;
        child.LineHeightPixels = parent.LineHeightPixels;
        child.LetterSpacing = parent.LetterSpacing;
        child.WordSpacing = parent.WordSpacing;
        child.TextTransform = parent.TextTransform;
        child.WhiteSpace = parent.WhiteSpace;
        child.Visibility = parent.Visibility;
        // CSS2 inherited additions
        child.FontSizeAdjust = parent.FontSizeAdjust;
        child.FontStretch = parent.FontStretch;
        child.Direction = parent.Direction;
        child.Cursor = parent.Cursor;
        child.CursorUri = parent.CursorUri;
        child.TextShadow = parent.TextShadow;
        child.Quotes = parent.Quotes == null ? null : new List<QuotePair>(parent.Quotes);
        // list-style-* is inherited in CSS1
        child.ListStyleType = parent.ListStyleType;
        child.ListStylePosition = parent.ListStylePosition;
        child.ListStyleImage = parent.ListStyleImage;

        // Non-inherited (background, box model, display, position) keep
        // their initial values.

        if (own != null)
        {
            foreach (var decl in own)
                child.Apply(decl, parent.FontSize, 0f, parent.FontWeight, parent);
        }
        child.ResolvePendingLineHeight();

        return child;
    }

    /// <summary>Parse and apply a single declaration in place.
    /// parentStyle enables the CSS2 'inherit' keyword for NON-inherited
    /// properties (border-width: inherit pulls the parent's computed
    /// value); it is null for the root element, where 'inherit' resolves
    /// to the initial value the style already carries.</summary>
    public void Apply(CssDeclaration decl, float parentFontSize, float viewportWidth,
        FontWeightValue parentFontWeight = FontWeightValue.Normal,
        ComputedStyle? parentStyle = null)
    {
        if (decl == null) return;

        var property = decl.Property.ToLowerInvariant();
        var value = decl.Value.Trim();

        // CSS2 'inherit' on ANY property routes to the parent's computed
        // value — inherited properties get it naturally, non-inherited
        // ones (border-width, margins, display, …) copy it explicitly
        // (CSS2 §6.2.1).  Shorthands pass through unexpanded (see
        // CssParser.ExpandShorthand) so every sub-property copies as a
        // unit.
        if (IsInheritToken(value))
        {
            ApplyInherit(property, parentStyle);
            return;
        }

        switch (property)
        {
            // === FONT ===
            case "font-family": FontFamily = ParseFontFamily(value); break;
            case "font-size": FontSize = ParseFontSize(value, parentFontSize); OwnFontSize = true; break;
            case "font-weight": FontWeight = ParseFontWeight(value, parentFontWeight); break;
            case "font-style": FontStyle = ParseFontStyle(value); break;
            case "font-variant": FontVariant = ParseFontVariant(value); break;
            case "font": ParseFontShorthand(value, parentFontSize, parentFontWeight); OwnFontSize = true; break;

            // === TEXT ===
            case "color": Color = ParseColor(value, Color); OwnColor = true; break;
            case "text-decoration": TextDecoration = ParseTextDecoration(value); break;
            case "text-align": TextAlign = ParseTextAlign(value); OwnTextAlign = true; break;
            case "text-indent":
                (TextIndent, TextIndentPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth);
                break;
            case "line-height":
                SetLineHeight(value);
                break;
            case "letter-spacing": LetterSpacing = ParseLength(value, parentFontSize, viewportWidth); break;
            case "word-spacing": WordSpacing = ParseLength(value, parentFontSize, viewportWidth); break;
            case "text-transform": TextTransform = ParseTextTransform(value); break;
            case "white-space": WhiteSpace = ParseWhiteSpace(value); break;
            case "vertical-align": SetVerticalAlign(value, parentFontSize); break;

            // === BACKGROUND ===
            case "background-color": BackgroundColor = ParseColor(value, BackgroundColor); OwnBackground = true; break;
            case "background-image": BackgroundImage = ParseUrl(value); break;
            case "background-repeat": BackgroundRepeat = ParseBackgroundRepeat(value); break;
            case "background-attachment": BackgroundFixed = value.Equals("fixed", StringComparison.OrdinalIgnoreCase); break;
            case "background-position": ParseAndSetBackgroundPosition(value, parentFontSize, viewportWidth); break;
            case "background": ParseBackgroundShorthand(value, parentFontSize, viewportWidth); OwnBackground = true; break;

            // === BOX MODEL ===
            case "margin-top": (MarginTop, MarginTopPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth); break;
            case "margin-right":
                (MarginRight, MarginRightPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth);
                MarginRightAuto = IsAutoKeyword(value); break;
            case "margin-bottom": (MarginBottom, MarginBottomPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth); break;
            case "margin-left":
                (MarginLeft, MarginLeftPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth);
                MarginLeftAuto = IsAutoKeyword(value);
                OwnMarginLeft = true;
                break;
            case "margin": ParseMarginShorthand(value, parentFontSize, viewportWidth); OwnMarginLeft = true; break;

            case "padding-top": (PaddingTop, PaddingTopPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth); break;
            case "padding-right": (PaddingRight, PaddingRightPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth); break;
            case "padding-bottom": (PaddingBottom, PaddingBottomPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth); break;
            case "padding-left": (PaddingLeft, PaddingLeftPercent) = ParseLengthOrPercent(value, parentFontSize, viewportWidth); OwnPaddingLeft = true; break;
            case "padding": ParsePaddingShorthand(value, parentFontSize, viewportWidth); OwnPaddingLeft = true; break;

            case "border-top-width": BorderTopWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-right-width": BorderRightWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-bottom-width": BorderBottomWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-left-width": BorderLeftWidth = ParseBorderWidth(value, parentFontSize, viewportWidth); break;
            case "border-top-style": BorderTopStyle = ParseBorderStyle(value); OwnBorderTopStyle = true; break;
            case "border-right-style": BorderRightStyle = ParseBorderStyle(value); OwnBorderRightStyle = true; break;
            case "border-bottom-style": BorderBottomStyle = ParseBorderStyle(value); OwnBorderBottomStyle = true; break;
            case "border-left-style": BorderLeftStyle = ParseBorderStyle(value); OwnBorderLeftStyle = true; break;
            case "border-top-color": BorderTopColor = ParseColor(value, BorderTopColor); break;
            case "border-right-color": BorderRightColor = ParseColor(value, BorderRightColor); break;
            case "border-bottom-color": BorderBottomColor = ParseColor(value, BorderBottomColor); break;
            case "border-left-color": BorderLeftColor = ParseColor(value, BorderLeftColor); break;
                        case "border-top": ParseBorderSideShorthand(value, "top", parentFontSize, viewportWidth); break;
                        case "border-right": ParseBorderSideShorthand(value, "right", parentFontSize, viewportWidth); break;
                        case "border-bottom": ParseBorderSideShorthand(value, "bottom", parentFontSize, viewportWidth); break;
                        case "border-left": ParseBorderSideShorthand(value, "left", parentFontSize, viewportWidth); break;
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
            case "list-style-type": ListStyleType = ParseListStyleType(value); OwnListStyleType = true; break;
            case "list-style-image": ListStyleImage = ParseUrl(value); OwnListStyleImage = true; break;
            case "list-style-position": ListStylePosition = ParseListStylePosition(value); break;

            // === SIZE ===
            // Percentages are kept AS percentages (resolved at layout
            // against the containing block); everything else is a pixel
            // length or auto.
            case "width": (Width, WidthPercent) = ParseSize(value, parentFontSize, viewportWidth); break;
            case "height": (Height, HeightPercent) = ParseSize(value, parentFontSize, viewportWidth); break;

            // === CSS2 SIZE CONSTRAINTS ===
            case "min-width": (MinWidth, MinWidthPercent) = ParseSize(value, parentFontSize, viewportWidth); break;
            case "max-width":
                if (IsNoneKeyword(value)) { MaxWidth = null; MaxWidthPercent = null; }
                else (MaxWidth, MaxWidthPercent) = ParseSize(value, parentFontSize, viewportWidth);
                break;
            case "min-height": (MinHeight, MinHeightPercent) = ParseSize(value, parentFontSize, viewportWidth); break;
            case "max-height":
                if (IsNoneKeyword(value)) { MaxHeight = null; MaxHeightPercent = null; }
                else (MaxHeight, MaxHeightPercent) = ParseSize(value, parentFontSize, viewportWidth);
                break;

            // === CSS2 CURSOR ===
            case "cursor": SetCursor(value); break;

            // === CSS2 OUTLINE ===
            case "outline-width": OutlineWidth = Math.Max(0f, ParseBorderWidth(value, parentFontSize, viewportWidth)); break;
            case "outline-style": OutlineStyle = ParseBorderStyle(value); break;
            case "outline-color": SetOutlineColor(value); break;
            case "outline": ParseOutlineShorthand(value, parentFontSize, viewportWidth); break;

            // === CSS2 TABLES ===
            case "border-collapse": BorderCollapse = ParseBorderCollapse(value); break;
            case "border-spacing": SetBorderSpacing(value, parentFontSize); break;
            case "table-layout": TableLayout = ParseTableLayout(value); break;
            case "caption-side": CaptionSide = ParseCaptionSide(value); break;
            case "empty-cells": EmptyCells = ParseEmptyCells(value); break;

            // === CSS2 FONT EXTRAS ===
            case "font-size-adjust": FontSizeAdjust = ParseFontSizeAdjust(value); break;
            case "font-stretch": FontStretch = ParseFontStretch(value); break;

            // === CSS2 BIDI / TEXT ===
            case "direction": Direction = ParseDirection(value); break;
            case "unicode-bidi": UnicodeBidi = ParseUnicodeBidi(value); break;
            case "text-shadow": TextShadow = IsNoneKeyword(value) ? null : value; break;

            // === CSS2 GENERATED CONTENT ===
            case "content": Content = ParseContent(value); break;
            case "quotes": Quotes = ParseQuotes(value); break;
            case "counter-reset": CounterReset = ParseCounterList(value, defaultValue: 0); break;
            case "counter-increment": CounterIncrement = ParseCounterList(value, defaultValue: 1); break;

            // === NS4 LAYER ALIASES (checklist §10) — store into the
            // background slots so paint Just Works ===
            case "layer-background-color": BackgroundColor = ParseColor(value, BackgroundColor); OwnBackground = true; break;
            case "layer-background-image": BackgroundImage = ParseUrl(value); break;

            // === LIST-STYLE (whole-shorthand form reaches Apply when the
            // parser passes 'inherit' through unexpanded) ===
            case "list-style": ParseListStyleShorthand(value); break;

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
        // The generated-content lists are mutable — clone them so a
        // pseudo-element clone (GeneratedBefore/…) can't alias the base.
        c.Content = Content == null ? null : new List<ContentToken>(Content);
        c.Quotes = Quotes == null ? null : new List<QuotePair>(Quotes);
        c.CounterReset = CounterReset == null ? null : new List<CounterAction>(CounterReset);
        c.CounterIncrement = CounterIncrement == null ? null : new List<CounterAction>(CounterIncrement);
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

    private static FontWeightValue ParseFontWeight(string value, FontWeightValue parentWeight)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "normal" or "400" => FontWeightValue.W400,
            "bold" or "700" => FontWeightValue.W700,
            "bolder" => ResolveRelativeWeight(parentWeight, heavier: true),
            "lighter" => ResolveRelativeWeight(parentWeight, heavier: false),
            "100" => FontWeightValue.W100,
            "200" => FontWeightValue.W200,
            "300" => FontWeightValue.W300,
            "500" => FontWeightValue.W500,
            "600" => FontWeightValue.W600,
            "800" => FontWeightValue.W800,
            "900" => FontWeightValue.W900,
            _ => FontWeightValue.W400
        };
    }

    private static FontWeightValue ResolveRelativeWeight(FontWeightValue parentWeight, bool heavier)
    {
        int w = (int)parentWeight;
        if (w <= 0) w = 400;

        int resolved = heavier
            ? w < 400 ? 400 : w < 700 ? 700 : w < 900 ? 900 : 900
            : w <= 100 ? 100 : w <= 200 ? 100 : w <= 300 ? 200 :
              w <= 400 ? 300 : w <= 500 ? 400 : w <= 600 ? 500 :
              w <= 700 ? 400 : w <= 800 ? 700 : 800;

        return resolved switch
        {
            100 => FontWeightValue.W100,
            200 => FontWeightValue.W200,
            300 => FontWeightValue.W300,
            400 => FontWeightValue.W400,
            500 => FontWeightValue.W500,
            600 => FontWeightValue.W600,
            700 => FontWeightValue.W700,
            800 => FontWeightValue.W800,
            _ => FontWeightValue.W900
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
    private void ParseFontShorthand(string value, float parentFontSize, FontWeightValue parentFontWeight)
    {
        var parts = SplitTopLevel(value);

        // CSS2 system fonts — font: caption | icon | menu | message-box |
        // small-caption | status-bar (kept in sync with CssParser.
        // ExpandFont, which handles the parse-time path).
        if (parts.Count == 1)
        {
            float? sysSize = parts[0].ToLowerInvariant() switch
            {
                "caption" or "icon" or "menu" => 13f,
                "message-box" => 14f,
                "small-caption" => 11f,
                "status-bar" => 12f,
                _ => (float?)null
            };
            if (sysSize.HasValue)
            {
                FontStyle = FontStyleValue.Normal;
                FontVariant = FontVariantValue.Normal;
                FontWeight = FontWeightValue.Normal;
                FontSize = sysSize.Value;
                FontFamily = ParseFontFamily("MS Sans Serif, sans-serif");
                return;
            }
        }

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
                        SetLineHeight(bits[1]);
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
                FontWeight = ParseFontWeight(part, parentFontWeight);
            else if (LooksLikeSize(part))
            {
                size = parts[i];

                // Optional separate "/line-height" token
                int next = i + 1;
                if (next < parts.Count && parts[next].StartsWith("/"))
                {
                    SetLineHeight(parts[next][1..]);
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
            p.EndsWith("px") || p.EndsWith("pt") || p.EndsWith("pc") ||
            p.EndsWith("in") || p.EndsWith("cm") || p.EndsWith("mm") ||
            p.EndsWith("em") || p.EndsWith("ex") || p.EndsWith("%") ||
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

    private void SetLineHeight(string value)
    {
        var v = value.Trim();
        if (v.Equals("normal", StringComparison.OrdinalIgnoreCase) || v.Length == 0)
        {
            LineHeightMode = LineHeightMode.Normal;
            LineHeight = 0f;
            LineHeightPixels = 0f;
            PendingLineHeight = null;
            return;
        }

        if (TryParseFloat(v, out float number) && !LooksLikeLengthToken(v))
        {
            LineHeightMode = LineHeightMode.Number;
            LineHeight = number;
            LineHeightPixels = 0f;
            PendingLineHeight = null;
            return;
        }

        PendingLineHeight = v;
        LineHeightMode = LineHeightMode.Absolute;
        LineHeight = 0f;
    }

    internal void ResolvePendingLineHeight()
    {
        if (PendingLineHeight == null) return;
        string v = PendingLineHeight.Trim();
        if (v.EndsWith('%') && TryParseFloat(v[..^1], out float pct))
        {
            LineHeightMode = LineHeightMode.Absolute;
            LineHeightPixels = Math.Max(0f, FontSize * pct / 100f);
            LineHeight = 0f;
        }
        else
        {
            LineHeightMode = LineHeightMode.Absolute;
            LineHeightPixels = Math.Max(0f, ParseLength(v, FontSize, 0f));
            LineHeight = 0f;
        }
        PendingLineHeight = null;
    }

    private static bool LooksLikeLengthToken(string v)
    {
        v = v.Trim().ToLowerInvariant();
         return v.EndsWith("px") || v.EndsWith("pt") || v.EndsWith("pc") || v.EndsWith("in") ||
             v.EndsWith("cm") || v.EndsWith("mm") || v.EndsWith("em") || v.EndsWith("ex") || v.EndsWith("%") ||
             v is "0px" or "0pt" or "0pc" or "0in" or "0cm" or "0mm" or "0em" or "0ex" or "0%";
    }

    /// <summary>
    /// Parse a CSS length while retaining percentage intent for properties
    /// whose percentage basis is the containing block, not the viewport.
    /// </summary>
    private static (float Pixels, float? Percent) ParseLengthOrPercent(
        string value, float parentFontSize, float viewportWidth)
    {
        string t = value.Trim();
        if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase))
            return (0f, null);
        if (t.EndsWith('%') && TryParseFloat(t[..^1], out float pct))
            return (0f, pct);
        return (ParseLength(t, parentFontSize, viewportWidth), null);
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

    private void SetVerticalAlign(string value, float parentFontSize)
    {
        OwnVerticalAlign = true;
        string token = value.Trim();
        if (token.EndsWith('%') && TryParseFloat(token[..^1], out float percent))
        {
            VerticalAlignPercent = percent;
            VerticalAlign = VerticalAlign.Baseline;
            VerticalAlignLength = null;
            return;
        }

        VerticalAlignPercent = null;

        // CSS2: signed &lt;length&gt; values (vertical-align: 3px / -2px /
        // 0.25em).  The old code only accepted keywords and %, so every
        // length silently fell back to baseline.
        if (LooksLikeLengthToken(token))
        {
            VerticalAlignLength = ParseLength(token, parentFontSize, 0f);
            VerticalAlign = VerticalAlign.Baseline;
            return;
        }

        VerticalAlignLength = null;
        VerticalAlign = ParseVerticalAlign(token);
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
        else if (v.EndsWith("pc"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result *= 16f;
        }
        else if (v.EndsWith("in"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result *= 96f;
        }
        else if (v.EndsWith("cm"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result *= 96f / 2.54f;
        }
        else if (v.EndsWith("mm"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result *= 96f / 25.4f;
        }
        else if (v.EndsWith("em"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result = result * parentFontSize;
        }
        else if (v.EndsWith("ex"))
        {
            if (!TryParseFloat(v[..^2], out result)) return fallback ?? 0f;
            result = result * parentFontSize * 0.5f;
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

    private static (float? Length, float Percent) ParseBackgroundPositionComponent(string token, float fontSize)
    {
        string t = token.Trim().ToLowerInvariant();
        return t switch
        {
            "left" => (null, 0f),
            "center" => (null, 50f),
            "right" => (null, 100f),
            "top" => (null, 0f),
            "bottom" => (null, 100f),
            _ when t.EndsWith("%") && TryParseFloat(t[..^1], out var pct) => (null, pct),
            _ => (ParseLength(t, fontSize, 0f), 0f)
        };
    }

    private void ParseAndSetBackgroundPosition(string value, float parentFontSize, float viewportWidth)
    {
        BackgroundPositionXLength = null;
        BackgroundPositionYLength = null;
        var parts = SplitTopLevel(value);
        if (parts.Count == 0) { BackgroundPosition = new PointF(0, 0); return; }
        if (parts.Count == 1)
        {
            string t = parts[0].Trim().ToLowerInvariant();
            if (t is "top" or "bottom")
            {
                BackgroundPosition = new PointF(50f, t == "top" ? 0f : 100f);
                return;
            }
            var (len, pct) = ParseBackgroundPositionComponent(t, parentFontSize);
            if (t is "left" or "right" or "center" || t.EndsWith('%'))
                BackgroundPosition = new PointF(pct, 50f);
            else
            {
                BackgroundPositionXLength = len;
                BackgroundPosition = new PointF(0f, 50f);
            }
            return;
        }

        string a = parts[0].Trim().ToLowerInvariant();
        string b = parts[1].Trim().ToLowerInvariant();
        bool aVertical = a is "top" or "bottom";
        bool bHorizontal = b is "left" or "center" or "right";
        if (aVertical && bHorizontal) (a, b) = (b, a);

        var (xLen, xPct) = ParseBackgroundPositionComponent(a, parentFontSize);
        var (yLen, yPct) = ParseBackgroundPositionComponent(b, parentFontSize);
        BackgroundPosition = new PointF(xPct, yPct);
        BackgroundPositionXLength = xLen;
        BackgroundPositionYLength = yLen;
    }

    private void ParseBackgroundShorthand(string value, float parentFontSize, float viewportWidth)
    {
        // CSS shorthand resets omitted subproperties to their initial values.
        BackgroundColor = Color.Transparent;
        BackgroundImage = null;
        BackgroundRepeat = BackgroundRepeat.Repeat;
        BackgroundFixed = false;
        BackgroundPosition = new PointF(0f, 0f);
        BackgroundPositionXLength = null;
        BackgroundPositionYLength = null;

        var position = new List<string>();
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
                     lower.EndsWith("px") || lower.EndsWith("pt") || lower.EndsWith("em") || lower.EndsWith("%"))
                position.Add(part);
            else
                BackgroundColor = ParseColor(part, BackgroundColor);
        }
        if (position.Count > 0)
            ParseAndSetBackgroundPosition(string.Join(" ", position.Take(2)), parentFontSize, viewportWidth);
    }

    private void ParseMarginShorthand(string value, float fs, float vw)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        (MarginTop, MarginTopPercent) = ParseLengthOrPercent(p[0], fs, vw);
        (MarginRight, MarginRightPercent) = ParseLengthOrPercent(p[1], fs, vw);
        (MarginBottom, MarginBottomPercent) = ParseLengthOrPercent(p[2], fs, vw);
        (MarginLeft, MarginLeftPercent) = ParseLengthOrPercent(p[3], fs, vw);
        MarginRightAuto = IsAutoKeyword(p[1]);
        MarginLeftAuto = IsAutoKeyword(p[3]);
    }

    private void ParsePaddingShorthand(string value, float fs, float vw)
    {
        var p = BoxShorthand(SplitTopLevel(value));
        if (p == null) return;
        (PaddingTop, PaddingTopPercent) = ParseLengthOrPercent(p[0], fs, vw);
        (PaddingRight, PaddingRightPercent) = ParseLengthOrPercent(p[1], fs, vw);
        (PaddingBottom, PaddingBottomPercent) = ParseLengthOrPercent(p[2], fs, vw);
        (PaddingLeft, PaddingLeftPercent) = ParseLengthOrPercent(p[3], fs, vw);
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
        OwnBorderTopStyle = OwnBorderRightStyle = OwnBorderBottomStyle = OwnBorderLeftStyle = true;
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
        OwnBorderTopStyle = OwnBorderRightStyle = OwnBorderBottomStyle = OwnBorderLeftStyle = true;
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

    private void ParseBorderSideShorthand(string value, string side, float fs, float vw)
    {
        float? width = null;
        BorderStyleValue? borderStyle = null;
        Color? borderColor = null;
        foreach (string part in SplitTopLevel(value))
        {
            string token = part.ToLowerInvariant();
            if (token is "none" or "hidden" or "dotted" or "dashed" or "solid" or
                "double" or "groove" or "ridge" or "inset" or "outset")
                borderStyle = ParseBorderStyle(part);
            else if (token is "thin" or "medium" or "thick" || IsLengthToken(token))
                width = ParseBorderWidth(part, fs, vw);
            else
                borderColor = ParseColor(part, Color.Black);
        }

        switch (side)
        {
            case "top":
                if (width.HasValue) BorderTopWidth = width.Value;
                if (borderStyle.HasValue) { BorderTopStyle = borderStyle.Value; OwnBorderTopStyle = true; }
                if (borderColor.HasValue) BorderTopColor = borderColor.Value;
                break;
            case "right":
                if (width.HasValue) BorderRightWidth = width.Value;
                if (borderStyle.HasValue) { BorderRightStyle = borderStyle.Value; OwnBorderRightStyle = true; }
                if (borderColor.HasValue) BorderRightColor = borderColor.Value;
                break;
            case "bottom":
                if (width.HasValue) BorderBottomWidth = width.Value;
                if (borderStyle.HasValue) { BorderBottomStyle = borderStyle.Value; OwnBorderBottomStyle = true; }
                if (borderColor.HasValue) BorderBottomColor = borderColor.Value;
                break;
            case "left":
                if (width.HasValue) BorderLeftWidth = width.Value;
                if (borderStyle.HasValue) { BorderLeftStyle = borderStyle.Value; OwnBorderLeftStyle = true; }
                if (borderColor.HasValue) BorderLeftColor = borderColor.Value;
                break;
        }
    }

    /// <summary>Any token usable as a border width: px/pt/em or a bare number.</summary>
    private static bool IsLengthToken(string s) =>
        s.EndsWith("px") || s.EndsWith("pt") || s.EndsWith("pc") || s.EndsWith("in") ||
        s.EndsWith("cm") || s.EndsWith("mm") || s.EndsWith("em") || s.EndsWith("ex") ||
        (s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '.') && !s.Contains('('));

    private static DisplayValue ParseDisplay(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "block" => DisplayValue.Block,
            "inline" => DisplayValue.Inline,
            "inline-block" => DisplayValue.InlineBlock,
            // CSS2 additions
            "inline-table" => DisplayValue.InlineTable,
            "run-in" => DisplayValue.RunIn,
            "compact" => DisplayValue.Compact,
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
    // CSS2 helpers (cursor, outline, tables, generated content)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>The CSS2 'inherit' value keyword (case-insensitive).</summary>
    internal static bool IsInheritToken(string value) =>
        value.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoneKeyword(string value) =>
        value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    private void SetCursor(string value)
    {
        CursorUri = null;
        // CSS2 cursor value: a comma list of url()s followed by a keyword
        // (cursor: url(x.cur), url(y.cur), pointer).  The keyword is the
        // fallback; URIs are a hint for the shell (actually CHANGING the
        // pointer is the shell's job).
        string keyword = "";
        foreach (var part in value.Split(','))
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            var lower = token.ToLowerInvariant();
            if (lower.StartsWith("url("))
            {
                if (CursorUri == null)
                    CursorUri = ParseUrl(token);
            }
            else
            {
                keyword = lower; // last keyword wins
            }
        }

        Cursor = keyword switch
        {
            "hand" => CursorValue.Pointer,   // IE alias for pointer (checklist §14a)
            "pointer" => CursorValue.Pointer,
            "crosshair" => CursorValue.Crosshair,
            "default" => CursorValue.Default,
            "move" => CursorValue.Move,
            "e-resize" => CursorValue.EResize,
            "ne-resize" => CursorValue.NeResize,
            "nw-resize" => CursorValue.NwResize,
            "n-resize" => CursorValue.NResize,
            "se-resize" => CursorValue.SeResize,
            "sw-resize" => CursorValue.SwResize,
            "s-resize" => CursorValue.SResize,
            "w-resize" => CursorValue.WResize,
            "text" => CursorValue.Text,
            "wait" => CursorValue.Wait,
            "help" => CursorValue.Help,
            _ => CursorValue.Auto
        };
    }

    private void SetOutlineColor(string value)
    {
        if (value.Trim().Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            OutlineColorInvert = true;
            return;
        }
        OutlineColor = ParseColor(value, OutlineColor);
        OutlineColorInvert = false;
    }

    /// <summary>outline: [width] [style] [color] — any order, each optional;
    /// omitted sub-properties reset to their CSS2 initial values.</summary>
    private void ParseOutlineShorthand(string value, float fs, float vw)
    {
        OutlineWidth = 0f;
        OutlineStyle = BorderStyleValue.None;
        OutlineColorInvert = true;

        foreach (var part in SplitTopLevel(value))
        {
            var lower = part.ToLowerInvariant();
            if (lower is "none" or "hidden" or "dotted" or "dashed" or "solid" or
                    "double" or "groove" or "ridge" or "inset" or "outset")
                OutlineStyle = ParseBorderStyle(part);
            else if (lower is "thin" or "medium" or "thick" || IsLengthToken(lower))
                OutlineWidth = Math.Max(0f, ParseBorderWidth(part, fs, vw));
            else
                SetOutlineColor(part); // colour or the 'invert' keyword
        }
    }

    private static BorderCollapseValue ParseBorderCollapse(string value) =>
        value.Trim().ToLowerInvariant() == "collapse"
            ? BorderCollapseValue.Collapse
            : BorderCollapseValue.Separate;

    /// <summary>border-spacing: one length (both axes) or two (horizontal,
    /// vertical).  Percentages are invalid for border-spacing.</summary>
    private void SetBorderSpacing(string value, float parentFontSize)
    {
        var parts = SplitTopLevel(value);
        if (parts.Count == 0) return;
        BorderSpacingX = Math.Max(0f, ParseLength(parts[0], parentFontSize, 0f));
        BorderSpacingY = parts.Count > 1
            ? Math.Max(0f, ParseLength(parts[1], parentFontSize, 0f))
            : BorderSpacingX;
    }

    private static TableLayoutValue ParseTableLayout(string value) =>
        value.Trim().ToLowerInvariant() == "fixed" ? TableLayoutValue.Fixed : TableLayoutValue.Auto;

    private static CaptionSideValue ParseCaptionSide(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "bottom" => CaptionSideValue.Bottom,
            "left" => CaptionSideValue.Left,
            "right" => CaptionSideValue.Right,
            _ => CaptionSideValue.Top
        };

    private static EmptyCellsValue ParseEmptyCells(string value) =>
        value.Trim().ToLowerInvariant() == "hide" ? EmptyCellsValue.Hide : EmptyCellsValue.Show;

    private static float? ParseFontSizeAdjust(string value)
    {
        string v = value.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        return TryParseFloat(v, out float n) && n >= 0f ? n : null;
    }

    private static string? ParseFontStretch(string value)
    {
        string v = value.Trim().ToLowerInvariant();
        return v is "normal" or "wider" or "narrower" or
                 "ultra-condensed" or "extra-condensed" or "condensed" or "semi-condensed" or
                 "semi-expanded" or "expanded" or "extra-expanded" or "ultra-expanded"
            ? v
            : null;
    }

    private static DirectionValue ParseDirection(string value) =>
        value.Trim().ToLowerInvariant() == "rtl" ? DirectionValue.Rtl : DirectionValue.Ltr;

    private static string? ParseUnicodeBidi(string value)
    {
        string v = value.Trim().ToLowerInvariant();
        return v is "normal" or "embed" or "bidi-override" ? v : null;
    }

    /// <summary>
    /// Parse a CSS2 content value into (type, text) tokens.  Accepts
    /// strings, attr(x), url(), counter()/counters(), the quote keywords
    /// and tolerates anything else as an identifier token.  none/normal →
    /// null.  Rendering is the integrator's job — this is parse-and-store.
    /// </summary>
    private static List<ContentToken>? ParseContent(string value)
    {
        string v = value.Trim();
        if (v.Length == 0 ||
            v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return null;

        var tokens = new List<ContentToken>();
        int i = 0;
        while (i < v.Length)
        {
            if (char.IsWhiteSpace(v[i]) || v[i] == ',') { i++; continue; }

            if (v[i] is '"' or '\'')
            {
                tokens.Add(new ContentToken("string", ReadQuoted(v, ref i)));
                continue;
            }

            // One identifier/function token — parenthesised groups stay
            // together so counter(item, upper-roman) survives intact.
            int start = i;
            int depth = 0;
            while (i < v.Length)
            {
                char ch = v[i];
                if (ch == '(') depth++;
                else if (ch == ')') depth = Math.Max(0, depth - 1);
                if (depth == 0 && (char.IsWhiteSpace(ch) || ch == ',')) break;
                i++;
            }
            string word = v[start..i];
            if (word.Length == 0) continue;

            int paren = word.IndexOf('(');
            if (paren < 0)
            {
                string name = word.ToLowerInvariant();
                switch (name)
                {
                    case "open-quote": tokens.Add(new ContentToken("open-quote", "")); break;
                    case "close-quote": tokens.Add(new ContentToken("close-quote", "")); break;
                    case "no-open-quote": tokens.Add(new ContentToken("no-open-quote", "")); break;
                    case "no-close-quote": tokens.Add(new ContentToken("no-close-quote", "")); break;
                    default:
                        tokens.Add(new ContentToken("identifier", word)); break;
                }
            }
            else
            {
                string name = word[..paren].ToLowerInvariant();
                string arg = word[(paren + 1)..].TrimEnd(')').Trim();
                switch (name)
                {
                    case "attr": tokens.Add(new ContentToken("attr", arg.Trim('\'', '"'))); break;
                    case "url": tokens.Add(new ContentToken("uri", arg.Trim('\'', '"'))); break;
                    case "counter": tokens.Add(new ContentToken("counter", arg)); break;
                    case "counters": tokens.Add(new ContentToken("counters", arg)); break;
                    default: tokens.Add(new ContentToken(name, arg)); break;
                }
            }
        }
        return tokens;
    }

    /// <summary>
    /// Read a quoted CSS string starting at s[i] (the quote character),
    /// advancing i past the closing quote.  \" \' \\ \n \t are unescaped.
    /// </summary>
    private static string ReadQuoted(string s, ref int i)
    {
        char quote = s[i];
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                sb.Append(next switch
                {
                    '"' or '\'' or '\\' => next.ToString(),
                    'n' => "\n",
                    't' => "\t",
                    _ => ""   // other escapes: dropped (era-tolerant)
                });
                i += 2;
                continue;
            }
            if (c == quote) { i++; break; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// quotes: pairs of quoted strings (\"«\" \"»\" …).  none / an odd
    /// count → null (default quotes).
    /// </summary>
    private static List<QuotePair>? ParseQuotes(string value)
    {
        string v = value.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;

        var strings = new List<string>();
        int i = 0;
        while (i < v.Length)
        {
            if (char.IsWhiteSpace(v[i])) { i++; continue; }
            if (v[i] is '"' or '\'')
            {
                strings.Add(ReadQuoted(v, ref i));
                continue;
            }
            while (i < v.Length && !char.IsWhiteSpace(v[i])) i++; // skip junk
        }

        if (strings.Count < 2 || strings.Count % 2 != 0)
            return null;

        var pairs = new List<QuotePair>();
        for (int p = 0; p + 1 < strings.Count; p += 2)
            pairs.Add(new QuotePair(strings[p], strings[p + 1]));
        return pairs;
    }

    /// <summary>
    /// counter-reset / counter-increment: a space-separated list of
    /// identifier + optional integer (default 0 for reset, 1 for
    /// increment).  none → null.
    /// </summary>
    private static List<CounterAction>? ParseCounterList(string value, int defaultValue)
    {
        string v = value.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = SplitTopLevel(v);
        var result = new List<CounterAction>();
        for (int i = 0; i < parts.Count; i++)
        {
            string name = parts[i].Trim();
            if (name.Length == 0) continue;
            int val = defaultValue;
            if (i + 1 < parts.Count &&
                int.TryParse(parts[i + 1].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int parsed))
            {
                val = parsed;
                i++;
            }
            result.Add(new CounterAction(name, val));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>list-style: [type] [position] [image] (mirrors the
    /// parse-time expansion in CssParser.ExpandShorthand).</summary>
    private void ParseListStyleShorthand(string value)
    {
        foreach (var part in SplitTopLevel(value))
        {
            var lower = part.ToLowerInvariant();
            if (lower is "disc" or "circle" or "square" or "decimal" or
                    "lower-alpha" or "upper-alpha" or "lower-roman" or "upper-roman" or "none")
            {
                ListStyleType = ParseListStyleType(part);
                OwnListStyleType = true;
            }
            else if (lower is "inside" or "outside")
                ListStylePosition = ParseListStylePosition(part);
            else if (lower.StartsWith("url("))
            {
                ListStyleImage = ParseUrl(part);
                OwnListStyleImage = true;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // CSS2 'inherit' routing
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The 'inherit' keyword: copy the parent's COMPUTED value for the
    /// named property (including every sub-property of a shorthand, and
    /// for NON-inherited properties — border-width: inherit pulls the
    /// parent's computed border width, CSS2 §6.2.1).  A null parent (the
    /// root element) means the current value already IS the initial one.
    /// </summary>
    private void ApplyInherit(string property, ComputedStyle? parent)
    {
        if (parent == null)
            return;

        switch (property)
        {
            // === FONT ===
            case "font-family": FontFamily = new List<string>(parent.FontFamily); break;
            case "font-size": FontSize = parent.FontSize; OwnFontSize = true; break;
            case "font-weight": FontWeight = parent.FontWeight; break;
            case "font-style": FontStyle = parent.FontStyle; break;
            case "font-variant": FontVariant = parent.FontVariant; break;
            case "font-size-adjust": FontSizeAdjust = parent.FontSizeAdjust; break;
            case "font-stretch": FontStretch = parent.FontStretch; break;
            case "font":
                FontFamily = new List<string>(parent.FontFamily);
                FontSize = parent.FontSize; OwnFontSize = true;
                FontWeight = parent.FontWeight;
                FontStyle = parent.FontStyle;
                FontVariant = parent.FontVariant;
                break;

            // === TEXT ===
            case "color": Color = parent.Color; OwnColor = true; break;
            case "text-decoration": TextDecoration = parent.TextDecoration; break;
            case "text-align": TextAlign = parent.TextAlign; OwnTextAlign = true; break;
            case "text-indent":
                TextIndent = parent.TextIndent;
                TextIndentPercent = parent.TextIndentPercent;
                break;
            case "line-height":
                LineHeightMode = parent.LineHeightMode;
                LineHeight = parent.LineHeight;
                LineHeightPixels = parent.LineHeightPixels;
                PendingLineHeight = null;
                break;
            case "letter-spacing": LetterSpacing = parent.LetterSpacing; break;
            case "word-spacing": WordSpacing = parent.WordSpacing; break;
            case "text-transform": TextTransform = parent.TextTransform; break;
            case "white-space": WhiteSpace = parent.WhiteSpace; break;
            case "vertical-align":
                VerticalAlign = parent.VerticalAlign;
                VerticalAlignPercent = parent.VerticalAlignPercent;
                VerticalAlignLength = parent.VerticalAlignLength;
                OwnVerticalAlign = true;
                break;
            case "text-shadow": TextShadow = parent.TextShadow; break;
            case "direction": Direction = parent.Direction; break;
            case "unicode-bidi": UnicodeBidi = parent.UnicodeBidi; break;

            // === BACKGROUND ===
            case "background-color": BackgroundColor = parent.BackgroundColor; OwnBackground = true; break;
            case "background-image": BackgroundImage = parent.BackgroundImage; break;
            case "background-repeat": BackgroundRepeat = parent.BackgroundRepeat; break;
            case "background-attachment": BackgroundFixed = parent.BackgroundFixed; break;
            case "background-position":
                BackgroundPosition = parent.BackgroundPosition;
                BackgroundPositionXLength = parent.BackgroundPositionXLength;
                BackgroundPositionYLength = parent.BackgroundPositionYLength;
                break;
            case "background":
                BackgroundColor = parent.BackgroundColor; OwnBackground = true;
                BackgroundImage = parent.BackgroundImage;
                BackgroundRepeat = parent.BackgroundRepeat;
                BackgroundFixed = parent.BackgroundFixed;
                BackgroundPosition = parent.BackgroundPosition;
                BackgroundPositionXLength = parent.BackgroundPositionXLength;
                BackgroundPositionYLength = parent.BackgroundPositionYLength;
                break;
            case "layer-background-color": BackgroundColor = parent.BackgroundColor; OwnBackground = true; break;
            case "layer-background-image": BackgroundImage = parent.BackgroundImage; break;

            // === BOX MODEL ===
            case "margin-top":
                MarginTop = parent.MarginTop; MarginTopPercent = parent.MarginTopPercent; break;
            case "margin-right":
                MarginRight = parent.MarginRight; MarginRightPercent = parent.MarginRightPercent;
                MarginRightAuto = parent.MarginRightAuto;
                break;
            case "margin-bottom":
                MarginBottom = parent.MarginBottom; MarginBottomPercent = parent.MarginBottomPercent; break;
            case "margin-left":
                MarginLeft = parent.MarginLeft; MarginLeftPercent = parent.MarginLeftPercent;
                MarginLeftAuto = parent.MarginLeftAuto; OwnMarginLeft = true;
                break;
            case "margin":
                MarginTop = parent.MarginTop; MarginTopPercent = parent.MarginTopPercent;
                MarginRight = parent.MarginRight; MarginRightPercent = parent.MarginRightPercent;
                MarginBottom = parent.MarginBottom; MarginBottomPercent = parent.MarginBottomPercent;
                MarginLeft = parent.MarginLeft; MarginLeftPercent = parent.MarginLeftPercent;
                MarginLeftAuto = parent.MarginLeftAuto;
                MarginRightAuto = parent.MarginRightAuto;
                OwnMarginLeft = true;
                break;
            case "padding-top":
                PaddingTop = parent.PaddingTop; PaddingTopPercent = parent.PaddingTopPercent; break;
            case "padding-right":
                PaddingRight = parent.PaddingRight; PaddingRightPercent = parent.PaddingRightPercent; break;
            case "padding-bottom":
                PaddingBottom = parent.PaddingBottom; PaddingBottomPercent = parent.PaddingBottomPercent; break;
            case "padding-left":
                PaddingLeft = parent.PaddingLeft; PaddingLeftPercent = parent.PaddingLeftPercent;
                OwnPaddingLeft = true;
                break;
            case "padding":
                PaddingTop = parent.PaddingTop; PaddingTopPercent = parent.PaddingTopPercent;
                PaddingRight = parent.PaddingRight; PaddingRightPercent = parent.PaddingRightPercent;
                PaddingBottom = parent.PaddingBottom; PaddingBottomPercent = parent.PaddingBottomPercent;
                PaddingLeft = parent.PaddingLeft; PaddingLeftPercent = parent.PaddingLeftPercent;
                OwnPaddingLeft = true;
                break;

            // === BORDERS ===
            case "border-top-width": BorderTopWidth = parent.BorderTopWidth; break;
            case "border-right-width": BorderRightWidth = parent.BorderRightWidth; break;
            case "border-bottom-width": BorderBottomWidth = parent.BorderBottomWidth; break;
            case "border-left-width": BorderLeftWidth = parent.BorderLeftWidth; break;
            case "border-top-style":
                BorderTopStyle = parent.BorderTopStyle; OwnBorderTopStyle = true; break;
            case "border-right-style":
                BorderRightStyle = parent.BorderRightStyle; OwnBorderRightStyle = true; break;
            case "border-bottom-style":
                BorderBottomStyle = parent.BorderBottomStyle; OwnBorderBottomStyle = true; break;
            case "border-left-style":
                BorderLeftStyle = parent.BorderLeftStyle; OwnBorderLeftStyle = true; break;
            case "border-top-color": BorderTopColor = parent.BorderTopColor; break;
            case "border-right-color": BorderRightColor = parent.BorderRightColor; break;
            case "border-bottom-color": BorderBottomColor = parent.BorderBottomColor; break;
            case "border-left-color": BorderLeftColor = parent.BorderLeftColor; break;
            case "border-width":
                BorderTopWidth = parent.BorderTopWidth;
                BorderRightWidth = parent.BorderRightWidth;
                BorderBottomWidth = parent.BorderBottomWidth;
                BorderLeftWidth = parent.BorderLeftWidth;
                break;
            case "border-style":
                BorderTopStyle = parent.BorderTopStyle; OwnBorderTopStyle = true;
                BorderRightStyle = parent.BorderRightStyle; OwnBorderRightStyle = true;
                BorderBottomStyle = parent.BorderBottomStyle; OwnBorderBottomStyle = true;
                BorderLeftStyle = parent.BorderLeftStyle; OwnBorderLeftStyle = true;
                break;
            case "border-color":
                BorderTopColor = parent.BorderTopColor;
                BorderRightColor = parent.BorderRightColor;
                BorderBottomColor = parent.BorderBottomColor;
                BorderLeftColor = parent.BorderLeftColor;
                break;
            case "border-top":
                BorderTopWidth = parent.BorderTopWidth;
                BorderTopStyle = parent.BorderTopStyle; OwnBorderTopStyle = true;
                BorderTopColor = parent.BorderTopColor;
                break;
            case "border-right":
                BorderRightWidth = parent.BorderRightWidth;
                BorderRightStyle = parent.BorderRightStyle; OwnBorderRightStyle = true;
                BorderRightColor = parent.BorderRightColor;
                break;
            case "border-bottom":
                BorderBottomWidth = parent.BorderBottomWidth;
                BorderBottomStyle = parent.BorderBottomStyle; OwnBorderBottomStyle = true;
                BorderBottomColor = parent.BorderBottomColor;
                break;
            case "border-left":
                BorderLeftWidth = parent.BorderLeftWidth;
                BorderLeftStyle = parent.BorderLeftStyle; OwnBorderLeftStyle = true;
                BorderLeftColor = parent.BorderLeftColor;
                break;
            case "border":
                BorderTopWidth = parent.BorderTopWidth;
                BorderRightWidth = parent.BorderRightWidth;
                BorderBottomWidth = parent.BorderBottomWidth;
                BorderLeftWidth = parent.BorderLeftWidth;
                BorderTopStyle = parent.BorderTopStyle; OwnBorderTopStyle = true;
                BorderRightStyle = parent.BorderRightStyle; OwnBorderRightStyle = true;
                BorderBottomStyle = parent.BorderBottomStyle; OwnBorderBottomStyle = true;
                BorderLeftStyle = parent.BorderLeftStyle; OwnBorderLeftStyle = true;
                BorderTopColor = parent.BorderTopColor;
                BorderRightColor = parent.BorderRightColor;
                BorderBottomColor = parent.BorderBottomColor;
                BorderLeftColor = parent.BorderLeftColor;
                break;

            // === DISPLAY / POSITIONING ===
            case "display": Display = parent.Display; break;
            case "visibility": Visibility = parent.Visibility; break;
            case "overflow": Overflow = parent.Overflow; break;
            case "position": Position = parent.Position; break;
            case "top": Top = parent.Top; TopPercent = parent.TopPercent; break;
            case "right": Right = parent.Right; RightPercent = parent.RightPercent; break;
            case "bottom": Bottom = parent.Bottom; BottomPercent = parent.BottomPercent; break;
            case "left": Left = parent.Left; LeftPercent = parent.LeftPercent; break;
            case "float": Float = parent.Float; break;
            case "clear": Clear = parent.Clear; break;
            case "z-index": ZIndex = parent.ZIndex; break;

            // === LISTS ===
            case "list-style-type":
                ListStyleType = parent.ListStyleType; OwnListStyleType = true; break;
            case "list-style-image":
                ListStyleImage = parent.ListStyleImage; OwnListStyleImage = true; break;
            case "list-style-position": ListStylePosition = parent.ListStylePosition; break;
            case "list-style":
                ListStyleType = parent.ListStyleType; OwnListStyleType = true;
                ListStyleImage = parent.ListStyleImage; OwnListStyleImage = true;
                ListStylePosition = parent.ListStylePosition;
                break;

            // === SIZE ===
            case "width": Width = parent.Width; WidthPercent = parent.WidthPercent; break;
            case "height": Height = parent.Height; HeightPercent = parent.HeightPercent; break;
            case "min-width": MinWidth = parent.MinWidth; MinWidthPercent = parent.MinWidthPercent; break;
            case "max-width": MaxWidth = parent.MaxWidth; MaxWidthPercent = parent.MaxWidthPercent; break;
            case "min-height": MinHeight = parent.MinHeight; MinHeightPercent = parent.MinHeightPercent; break;
            case "max-height": MaxHeight = parent.MaxHeight; MaxHeightPercent = parent.MaxHeightPercent; break;

            // === CSS2 MISC ===
            case "cursor": Cursor = parent.Cursor; CursorUri = parent.CursorUri; break;
            case "outline-width": OutlineWidth = parent.OutlineWidth; break;
            case "outline-style": OutlineStyle = parent.OutlineStyle; break;
            case "outline-color":
                OutlineColor = parent.OutlineColor;
                OutlineColorInvert = parent.OutlineColorInvert;
                break;
            case "outline":
                OutlineWidth = parent.OutlineWidth;
                OutlineStyle = parent.OutlineStyle;
                OutlineColor = parent.OutlineColor;
                OutlineColorInvert = parent.OutlineColorInvert;
                break;
            case "border-collapse": BorderCollapse = parent.BorderCollapse; break;
            case "border-spacing":
                BorderSpacingX = parent.BorderSpacingX; BorderSpacingY = parent.BorderSpacingY; break;
            case "table-layout": TableLayout = parent.TableLayout; break;
            case "caption-side": CaptionSide = parent.CaptionSide; break;
            case "empty-cells": EmptyCells = parent.EmptyCells; break;
            case "content": Content = parent.Content; break;
            case "quotes":
                Quotes = parent.Quotes == null ? null : new List<QuotePair>(parent.Quotes); break;
            case "counter-reset":
                CounterReset = parent.CounterReset == null ? null : new List<CounterAction>(parent.CounterReset); break;
            case "counter-increment":
                CounterIncrement = parent.CounterIncrement == null ? null : new List<CounterAction>(parent.CounterIncrement); break;

            // Unknown property: nothing to inherit (the property itself is
            // ignored, matching the 1996 tolerance rule).
        }
    }

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
public enum LineHeightMode { Normal, Number, Absolute }
public enum TextTransform { None, Capitalize, Uppercase, Lowercase }
public enum WhiteSpaceValue { Normal, Pre, Nowrap, PreWrap, PreLine }
public enum VerticalAlign { Baseline, Top, Middle, Bottom, TextTop, TextBottom, Super, Sub }
public enum BackgroundRepeat { Repeat, RepeatX, RepeatY, NoRepeat }

public enum DisplayValue
{
    Block, Inline, InlineBlock, None, ListItem,
    Table, TableRow, TableCell, TableCaption,
    TableRowGroup, TableColumnGroup, TableColumn,
    TableHeaderGroup, TableFooterGroup,
    // CSS2 additions
    InlineTable, RunIn, Compact
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

// ── CSS2 additions ──────────────────────────────────────────────────────

/// <summary>cursor keywords (CSS2 §18.1). 'hand' maps to Pointer — the IE
/// alias. Actual pointer changes are the shell's job.</summary>
public enum CursorValue
{
    Auto, Crosshair, Default, Pointer, Move,
    EResize, NeResize, NwResize, NResize,
    SeResize, SwResize, SResize, WResize,
    Text, Wait, Help
}

public enum BorderCollapseValue { Separate, Collapse }
public enum TableLayoutValue { Auto, Fixed }
public enum CaptionSideValue { Top, Bottom, Left, Right }
public enum EmptyCellsValue { Show, Hide }
public enum DirectionValue { Ltr, Rtl }

/// <summary>One parsed content value token. Type ∈ "string", "attr",
/// "uri", "counter", "counters", "open-quote", "close-quote",
/// "no-open-quote", "no-close-quote", "identifier"; Text carries the
/// string body / attribute name / counter spec.</summary>
public record ContentToken(string Type, string Text);

/// <summary>One quotes pair: open then close string.</summary>
public record QuotePair(string Open, string Close);

/// <summary>counter-reset / counter-increment entry.</summary>
public record CounterAction(string Name, int Value);