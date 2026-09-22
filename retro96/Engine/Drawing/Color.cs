// Retro96.Graphics — colour value type + HTML/CSS colour translation.
//
// Replaces System.Drawing.Color and ColorTranslator for the engine.  The
// ARGB layout, the Empty/Transparent sentinels and the FromArgb overloads
// keep the exact semantics the CSS resolver, the renderer and the shell
// overlays were written against (Empty = "unset", Transparent = "alpha 0").
using SkiaSharp;

namespace Retro96.Drawing;

public readonly struct Color : IEquatable<Color>
{
    /// <summary>The unset colour — no alpha, no channels, not "transparent black".</summary>
    public static readonly Color Empty = default;

    /// <summary>Fully transparent white (GDI-compatible sentinel).</summary>
    public static readonly Color Transparent = new(0x00, 0xFF, 0xFF, 0xFF);

    public byte A { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    private Color(byte a, byte r, byte g, byte b)
    { A = a; R = r; G = g; B = b; }

    public static Color FromArgb(int argb) =>
        new((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    public static Color FromArgb(uint argb) =>
        FromArgb(unchecked((int)argb));

    public static Color FromArgb(int alpha, int red, int green, int blue)
    {
        if (alpha is < 0 or > 255) alpha = 255;
        return new((byte)alpha, Clamp(red), Clamp(green), Clamp(blue));
    }

    public static Color FromArgb(int red, int green, int blue) =>
        new(255, Clamp(red), Clamp(green), Clamp(blue));

    public static Color FromArgb(int alpha, Color baseColor) =>
        new((byte)Math.Clamp(alpha, 0, 255), baseColor.R, baseColor.G, baseColor.B);

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    public static Color FromName(string name)
    {
        if (NamedColors.TryGetValue(Normalize(name), out uint argb))
            return FromArgb(argb);
        // GDI FromName returns a phantom zero colour for unknown names — the
        // callers detect it via the all-zero check, so mirror that exactly.
        return default;
    }

    private static string Normalize(string name) =>
        name.Trim().ToLowerInvariant().Replace(" ", "");

    /// <summary>The colour's CSS name when it has one, else #RRGGBB (GDI compat).</summary>
    public string Name => ColorTranslator.ToHtml(this);

    public bool Equals(Color other) =>
        A == other.A && R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(A, R, G, B);
    public static bool operator ==(Color left, Color right) => left.Equals(right);
    public static bool operator !=(Color left, Color right) => !left.Equals(right);

    public override string ToString()
    {
        if (this == Empty) return "Color [Empty]";
        if (this == Transparent) return "Color [Transparent]";
        return $"Color [A={A}, R={R}, G={G}, B={B}]";
    }

    /// <summary>Converts to a SkiaSharp colour (straight alpha; Skia premultiplies at raster time).</summary>
    internal SkiaSharp.SKColor ToSkColor() => new(R, G, B, A);

    // ── Common named colours used directly by the engine ──
    public static Color White => new(0xFF, 0xFF, 0xFF, 0xFF);
    public static Color Black => new(0xFF, 0x00, 0x00, 0x00);
    public static Color Red => new(0xFF, 0xFF, 0x00, 0x00);
    public static Color Green => new(0xFF, 0x00, 0x80, 0x00);
    public static Color Blue => new(0xFF, 0x00, 0x00, 0xFF);
    public static Color Gray => new(0xFF, 0x80, 0x80, 0x80);
    public static Color DarkGray => new(0xFF, 0xA9, 0xA9, 0xA9);
    public static Color LightGray => new(0xFF, 0xD3, 0xD3, 0xD3);
    public static Color Orange => new(0xFF, 0xFF, 0xA5, 0x00);
    public static Color Silver => new(0xFF, 0xC0, 0xC0, 0xC0);
    public static Color Navy => new(0xFF, 0x00, 0x00, 0x80);
    public static Color Yellow => new(0xFF, 0xFF, 0xFF, 0x00);

    // ── The CSS/X11 named-colour table (the set 1996 pages actually used) ──
    private static readonly System.Collections.Generic.Dictionary<string, uint> NamedColors =
        new()
        {
            ["transparent"] = 0x00FFFFFF,
            ["aliceblue"] = 0xFFF0F8FF, ["antiquewhite"] = 0xFFFAEBD7,
            ["aqua"] = 0xFF00FFFF, ["aquamarine"] = 0xFF7FFFD4,
            ["azure"] = 0xFFF0FFFF, ["beige"] = 0xFFF5F5DC,
            ["bisque"] = 0xFFFFE4C4, ["black"] = 0xFF000000,
            ["blanchedalmond"] = 0xFFFFEBCD, ["blue"] = 0xFF0000FF,
            ["blueviolet"] = 0xFF8A2BE2, ["brown"] = 0xFFA52A2A,
            ["burlywood"] = 0xFFDEB887, ["cadetblue"] = 0xFF5F9EA0,
            ["chartreuse"] = 0xFF7FFF00, ["chocolate"] = 0xFFD2691E,
            ["coral"] = 0xFFFF7F50, ["cornflowerblue"] = 0xFF6495ED,
            ["cornsilk"] = 0xFFFFF8DC, ["crimson"] = 0xFFDC143C,
            ["cyan"] = 0xFF00FFFF, ["darkblue"] = 0xFF00008B,
            ["darkcyan"] = 0xFF008B8B, ["darkgoldenrod"] = 0xFFB8860B,
            ["darkgray"] = 0xFFA9A9A9, ["darkgreen"] = 0xFF006400,
            ["darkkhaki"] = 0xFFBDB76B, ["darkmagenta"] = 0xFF8B008B,
            ["darkolivegreen"] = 0xFF556B2F, ["darkorange"] = 0xFFFF8C00,
            ["darkorchid"] = 0xFF9932CC, ["darkred"] = 0xFF8B0000,
            ["darksalmon"] = 0xFFE9967A, ["darkseagreen"] = 0xFF8FBC8F,
            ["darkslateblue"] = 0xFF483D8B, ["darkslategray"] = 0xFF2F4F4F,
            ["darkturquoise"] = 0xFF00CED1, ["darkviolet"] = 0xFF9400D3,
            ["deeppink"] = 0xFFFF1493, ["deepskyblue"] = 0xFF00BFFF,
            ["dimgray"] = 0xFF696969, ["dodgerblue"] = 0xFF1E90FF,
            ["firebrick"] = 0xFFB22222, ["floralwhite"] = 0xFFFFFAF0,
            ["forestgreen"] = 0xFF228B22, ["fuchsia"] = 0xFFFF00FF,
            ["gainsboro"] = 0xFFDCDCDC, ["ghostwhite"] = 0xFFF8F8FF,
            ["gold"] = 0xFFFFD700, ["goldenrod"] = 0xFFDAA520,
            ["gray"] = 0xFF808080, ["grey"] = 0xFF808080,
            ["green"] = 0xFF008000, ["greenyellow"] = 0xFFADFF2F,
            ["honeydew"] = 0xFFF0FFF0, ["hotpink"] = 0xFFFF69B4,
            ["indianred"] = 0xFFCD5C5C, ["indigo"] = 0xFF4B0082,
            ["ivory"] = 0xFFFFFFF0, ["khaki"] = 0xFFF0E68C,
            ["lavender"] = 0xFFE6E6FA, ["lavenderblush"] = 0xFFFFF0F5,
            ["lawngreen"] = 0xFF7CFC00, ["lemonchiffon"] = 0xFFFFFACD,
            ["lightblue"] = 0xFFADD8E6, ["lightcoral"] = 0xFFF08080,
            ["lightcyan"] = 0xFFE0FFFF, ["lightgoldenrodyellow"] = 0xFFFAFAD2,
            ["lightgray"] = 0xFFD3D3D3, ["lightgreen"] = 0xFF90EE90,
            ["lightpink"] = 0xFFFFB6C1, ["lightsalmon"] = 0xFFFFA07A,
            ["lightseagreen"] = 0xFF20B2AA, ["lightskyblue"] = 0xFF87CEFA,
            ["lightslategray"] = 0xFF778899, ["lightsteelblue"] = 0xFFB0C4DE,
            ["lightyellow"] = 0xFFFFFFE0, ["lime"] = 0xFF00FF00,
            ["limegreen"] = 0xFF32CD32, ["linen"] = 0xFFFAF0E6,
            ["magenta"] = 0xFFFF00FF, ["maroon"] = 0xFF800000,
            ["mediumaquamarine"] = 0xFF66CDAA, ["mediumblue"] = 0xFF0000CD,
            ["mediumorchid"] = 0xFFBA55D3, ["mediumpurple"] = 0xFF9370DB,
            ["mediumseagreen"] = 0xFF3CB371, ["mediumslateblue"] = 0xFF7B68EE,
            ["mediumspringgreen"] = 0xFF00FA9A, ["mediumturquoise"] = 0xFF48D1CC,
            ["mediumvioletred"] = 0xFFC71585, ["midnightblue"] = 0xFF191970,
            ["mintcream"] = 0xFFF5FFFA, ["mistyrose"] = 0xFFFFE4E1,
            ["moccasin"] = 0xFFFFE4B5, ["navajowhite"] = 0xFFFFDEAD,
            ["navy"] = 0xFF000080, ["oldlace"] = 0xFFFDF5E6,
            ["olive"] = 0xFF808000, ["olivedrab"] = 0xFF6B8E23,
            ["orange"] = 0xFFFFA500, ["orangered"] = 0xFFFF4500,
            ["orchid"] = 0xFFDA70D6, ["palegoldenrod"] = 0xFFEEE8AA,
            ["palegreen"] = 0xFF98FB98, ["paleturquoise"] = 0xFFAFEEEE,
            ["palevioletred"] = 0xFFDB7093, ["papayawhip"] = 0xFFFFEFD5,
            ["peachpuff"] = 0xFFFFDAB9, ["peru"] = 0xFFCD853F,
            ["pink"] = 0xFFFFC0CB, ["plum"] = 0xFFDDA0DD,
            ["powderblue"] = 0xFFB0E0E6, ["purple"] = 0xFF800080,
            ["red"] = 0xFFFF0000, ["rosybrown"] = 0xFFBC8F8F,
            ["royalblue"] = 0xFF4169E1, ["saddlebrown"] = 0xFF8B4513,
            ["salmon"] = 0xFFFA8072, ["sandybrown"] = 0xFFF4A460,
            ["seagreen"] = 0xFF2E8B57, ["seashell"] = 0xFFFFF5EE,
            ["sienna"] = 0xFFA0522D, ["silver"] = 0xFFC0C0C0,
            ["skyblue"] = 0xFF87CEEB, ["slateblue"] = 0xFF6A5ACD,
            ["slategray"] = 0xFF708090, ["snow"] = 0xFFFFFAFA,
            ["springgreen"] = 0xFF00FF7F, ["steelblue"] = 0xFF4682B4,
            ["tan"] = 0xFFD2B48C, ["teal"] = 0xFF008080,
            ["thistle"] = 0xFFD8BFD8, ["tomato"] = 0xFFFF6347,
            ["turquoise"] = 0xFF40E0D0, ["violet"] = 0xFFEE82EE,
            ["wheat"] = 0xFFF5DEB3, ["white"] = 0xFFFFFFFF,
            ["whitesmoke"] = 0xFFF5F5F5, ["yellow"] = 0xFFFFFF00,
            ["yellowgreen"] = 0xFF9ACD32,
        };
}

public static class ColorTranslator
{
    /// <summary>
    /// Parses #rrggbb, #rgb and named HTML/CSS colours.  Junk input yields
    /// <see cref="Color.Empty"/> (callers treat that as "unparsed"), matching
    /// the previous GDI+ behaviour the renderer relies on.
    /// </summary>
    public static Color FromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Color.Empty;

        string v = html.Trim();

        if (v.StartsWith('#'))
        {
            string hex = v[1..];
            if (hex.Length == 3)
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length == 6 &&
                int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int argb))
                return Color.FromArgb(unchecked((int)(0xFF000000u | (uint)argb)));
            if (hex.Length == 8 &&
                int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int argb8))
                return Color.FromArgb(unchecked((int)argb8));
            return Color.Empty;
        }

        return Color.FromName(v);
    }

    /// <summary>Emits #RRGGBB, or the colour's CSS name when it has one.</summary>
    public static string ToHtml(Color c)
    {
        if (c == Color.Transparent) return "transparent";
        if (c == Color.Empty) return "";

        int argb = unchecked((int)(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));
        foreach (var kvp in NamedColorReverse.Shared)
        {
            if (kvp.Value == argb)
                return kvp.Key;
        }

        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static class NamedColorReverse
    {
        public static readonly System.Collections.Generic.Dictionary<string, int> Shared = Build();

        private static System.Collections.Generic.Dictionary<string, int> Build()
        {
            // A few canonical names first so ToHtml prefers them over
            // synonyms (aqua vs cyan, grey vs gray, …).
            var preferred = new[]
            {
                "black", "white", "red", "green", "blue", "yellow", "gray",
                "silver", "maroon", "navy", "olive", "teal", "purple",
                "fuchsia", "aqua", "lime", "orange", "pink", "brown",
                "transparent",
            };
            var d = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var name in preferred)
            {
                var c = Color.FromName(name);
                d[name] = unchecked((int)(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));
            }
            return d;
        }
    }
}
