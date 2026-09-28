// Retro96.Graphics — pens, brushes, regions and the Win95 shade helpers.
//
// Pens and brushes hold ready SKPaint state so the renderer's hot paths
// (per-box borders, per-run text) pay no per-call allocation.  ControlPaint
// reproduces the exact Light/Dark blend the previous headless stub used,
// which keeps the 3-D border shades pixel-identical to the old baseline.
using SkiaSharp;

namespace Retro96.Drawing;

public enum DashStyle
{
    Solid = 0,
    Dash = 1,
    Dot = 2,
    DashDot = 3,
    DashDotDot = 4,
    Custom = 5,
}

public enum CombineMode
{
    Replace = 0,
    Intersect = 1,
    Union = 2,
    Xor = 3,
    Exclude = 4,
    Complement = 5,
}

public enum TextRenderingHint
{
    SystemDefault = 0,
    SingleBitPerPixelGridFit = 1,
    SingleBitPerPixel = 2,
    AntiAliasGridFit = 3,
    AntiAlias = 4,
    ClearTypeGridFit = 5,
}

public enum SmoothingMode
{
    Invalid = -1,
    Default = 0,
    HighSpeed = 1,
    HighQuality = 2,
    None = 3,
    AntiAlias = 4,
}

public enum PixelOffsetMode
{
    Invalid = -1,
    Default = 0,
    HighSpeed = 1,
    HighQuality = 2,
    None = 3,
    Half = 4,
}

public enum InterpolationMode
{
    Invalid = -1,
    Default = 0,
    Low = 1,
    High = 2,
    Bilinear = 3,
    Bicubic = 4,
    NearestNeighbor = 5,
    HighQualityBilinear = 6,
    HighQualityBicubic = 7,
}

public enum CompositingMode
{
    SourceOver = 0,
    SourceCopy = 1,
}

public enum CompositingQuality
{
    Invalid = -1,
    Default = 0,
    LowQuality = 1,
    HighQuality = 2,
    GammaCorrected = 3,
    AssumeLinear = 4,
}

// ── Brushes ───────────────────────────────────────────────────────────

public abstract class Brush : IDisposable
{
    internal SKPaint? _paint;

    internal SKPaint Paint => _paint ??= new SKPaint();

    public void Dispose()
    {
        _paint?.Dispose();
        _paint = null;
        GC.SuppressFinalize(this);
    }
}

public sealed class SolidBrush : Brush
{
    public SolidBrush(Color color)
    {
        Color = color;
    }

    public Color Color { get; }

    internal SKPaint Prepare(bool antialias)
    {
        var p = Paint;
        p.Color = Color.ToSkColor();
        p.Style = SKPaintStyle.Fill;
        p.IsAntialias = antialias;
        p.BlendMode = SKBlendMode.SrcOver;
        return p;
    }
}

public static class Brushes
{
    private static SolidBrush? _lightGray, _darkGray, _gray, _white, _black,
        _red, _green, _blue, _orange, _silver, _transparent, _navy;

    public static SolidBrush LightGray => _lightGray ??= new SolidBrush(Color.LightGray);
    public static SolidBrush DarkGray => _darkGray ??= new SolidBrush(Color.DarkGray);
    public static SolidBrush Gray => _gray ??= new SolidBrush(Color.Gray);
    public static SolidBrush White => _white ??= new SolidBrush(Color.White);
    public static SolidBrush Black => _black ??= new SolidBrush(Color.Black);
    public static SolidBrush Red => _red ??= new SolidBrush(Color.Red);
    public static SolidBrush Green => _green ??= new SolidBrush(Color.Green);
    public static SolidBrush Blue => _blue ??= new SolidBrush(Color.Blue);
    public static SolidBrush Orange => _orange ??= new SolidBrush(Color.Orange);
    public static SolidBrush Silver => _silver ??= new SolidBrush(Color.Silver);
    public static SolidBrush Transparent => _transparent ??= new SolidBrush(Color.Transparent);
    public static SolidBrush Navy => _navy ??= new SolidBrush(Color.Navy);
}

/// <summary>OS palette stand-ins for the era (classic Win95 values).</summary>
public static class SystemBrushes
{
    private static SolidBrush? _highlight, _highlightText, _window, _windowText;

    public static SolidBrush Highlight => _highlight ??= new SolidBrush(Color.FromArgb(0xFF, 0x00, 0x00, 0x80));
    public static SolidBrush HighlightText => _highlightText ??= new SolidBrush(Color.White);
    public static SolidBrush Window => _window ??= new SolidBrush(Color.White);
    public static SolidBrush WindowText => _windowText ??= new SolidBrush(Color.Black);
}

// ── Pens ──────────────────────────────────────────────────────────────

public sealed class Pen : IDisposable
{
    public Color Color { get; }
    public float Width { get; set; }
    public DashStyle DashStyle { get; set; }

    private SKPaint? _paint;
    private DashStyle _paintDash = DashStyle.Solid;

    public Pen(Color color, float width)
    {
        Color = color;
        Width = width <= 0f ? 1f : width;
    }

    public Pen(Color color) : this(color, 1f) { }

    internal SKPaint Prepare(bool antialias)
    {
        var p = _paint ??= new SKPaint();
        p.Color = Color.ToSkColor();
        p.Style = SKPaintStyle.Stroke;
        p.StrokeWidth = Width;
        p.IsAntialias = antialias;
        p.BlendMode = SKBlendMode.SrcOver;
        if (_paintDash != DashStyle || p.PathEffect == null && DashStyle != DashStyle.Solid)
        {
            _paintDash = DashStyle;
            p.PathEffect = DashStyle switch
            {
                DashStyle.Dash => SKPathEffect.CreateDash(new[] { 3f * Width, 1f * Width }, 0f),
                DashStyle.Dot => SKPathEffect.CreateDash(new[] { 1f * Width, 1f * Width }, 0f),
                DashStyle.DashDot => SKPathEffect.CreateDash(new[] { 3f * Width, 1f * Width, 1f * Width, 1f * Width }, 0f),
                DashStyle.DashDotDot => SKPathEffect.CreateDash(new[] { 3f * Width, 1f * Width, 1f * Width, 1f * Width, 1f * Width, 1f * Width }, 0f),
                _ => null,
            };
        }
        return p;
    }

    public void Dispose()
    {
        _paint?.Dispose();
        _paint = null;
        GC.SuppressFinalize(this);
    }
}

public static class Pens
{
    private static Pen? _gray, _darkGray, _white, _black, _red, _navy;

    public static Pen Gray => _gray ??= new Pen(Color.Gray);
    public static Pen DarkGray => _darkGray ??= new Pen(Color.DarkGray);
    public static Pen White => _white ??= new Pen(Color.White);
    public static Pen Black => _black ??= new Pen(Color.Black);
    public static Pen Red => _red ??= new Pen(Color.Red);
    public static Pen Navy => _navy ??= new Pen(Color.Navy);
}

// ── Region (clip path) ────────────────────────────────────────────────

public sealed class Region : IDisposable
{
    internal SKPath Path { get; }

    public Region(RectangleF rect)
    {
        Path = new SKPath();
        Path.AddRect(SKRect.Create(rect.X, rect.Y, rect.Width, rect.Height));
    }

    public Region(PointF[] polygon)
    {
        if (polygon == null) throw new ArgumentNullException(nameof(polygon));
        if (polygon.Length < 3) throw new ArgumentException("A clip polygon needs at least three points.", nameof(polygon));

        Path = new SKPath();
        Path.MoveTo(polygon[0].X, polygon[0].Y);
        for (int i = 1; i < polygon.Length; i++)
            Path.LineTo(polygon[i].X, polygon[i].Y);
        Path.Close();
    }

    public void Dispose()
    {
        Path.Dispose();
    }
}

// ── Win95 border shades ───────────────────────────────────────────────

/// <summary>
/// The Light/Dark shade ladder for 3-D borders.  Formulas are the same
/// linear blends the previous headless ControlPaint stub used, so groove /
/// ridge / inset / outset rendering is unchanged from the GDI+ baseline.
/// </summary>
public static class ControlPaint
{
    private static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb(a.A,
            a.R + (int)((b.R - a.R) * t),
            a.G + (int)((b.G - a.G) * t),
            a.B + (int)((b.B - a.B) * t));

    public static Color Light(Color c) => Blend(c, Color.White, 0.35f);
    public static Color LightLight(Color c) => Blend(c, Color.White, 0.7f);
    public static Color Dark(Color c) => Blend(c, Color.Black, 0.35f);
    public static Color DarkDark(Color c) => Blend(c, Color.Black, 0.7f);
}
