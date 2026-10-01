// Retro96.Graphics — geometry primitives.
//
// Part of the SkiaSharp rendering layer used throughout the engine.
// wholesale: the engine, the layout measurer and the renderer operate on
// these value types so every platform (Linux test rig included) shares one
// identical geometry model. Member shapes intentionally mirror the engine's
// structs the code was written against, so the port kept its logic intact.
namespace Retro96.Drawing;

public struct Point : IEquatable<Point>
{
    public int X { get; set; }
    public int Y { get; set; }

    public static readonly Point Empty = default;

    public Point(int x, int y) { X = x; Y = y; }

    public bool Equals(Point other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is Point p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Point left, Point right) => left.Equals(right);
    public static bool operator !=(Point left, Point right) => !left.Equals(right);
    public override string ToString() => $"{{X={X},Y={Y}}}";
}

public struct PointF : IEquatable<PointF>
{
    public float X { get; set; }
    public float Y { get; set; }

    public static readonly PointF Empty = default;

    public PointF(float x, float y) { X = x; Y = y; }

    public bool IsEmpty => X == 0f && Y == 0f;

    public bool Equals(PointF other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is PointF p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(PointF left, PointF right) => left.Equals(right);
    public static bool operator !=(PointF left, PointF right) => !left.Equals(right);
    public override string ToString() => $"{{X={X},Y={Y}}}";
}

public struct Size : IEquatable<Size>
{
    public int Width { get; set; }
    public int Height { get; set; }

    public static readonly Size Empty = default;

    public Size(int width, int height) { Width = width; Height = height; }

    public bool IsEmpty => Width == 0 && Height == 0;

    public bool Equals(Size other) => Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is Size s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Width, Height);
    public static bool operator ==(Size left, Size right) => left.Equals(right);
    public static bool operator !=(Size left, Size right) => !left.Equals(right);
    public override string ToString() => $"{{Width={Width},Height={Height}}}";
}

public struct SizeF : IEquatable<SizeF>
{
    public float Width { get; set; }
    public float Height { get; set; }

    public static readonly SizeF Empty = default;

    public SizeF(float width, float height) { Width = width; Height = height; }

    public bool IsEmpty => Width == 0f && Height == 0f;

    public bool Equals(SizeF other) => Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is SizeF s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Width, Height);
    public static bool operator ==(SizeF left, SizeF right) => left.Equals(right);
    public static bool operator !=(SizeF left, SizeF right) => !left.Equals(right);
    public override string ToString() => $"{{Width={Width},Height={Height}}}";
}

public struct Rectangle : IEquatable<Rectangle>
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public static readonly Rectangle Empty = default;

    public Rectangle(int x, int y, int width, int height)
    { X = x; Y = y; Width = width; Height = height; }

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public bool Contains(Point p) => Contains(p.X, p.Y);

    public bool IntersectsWith(Rectangle r) =>
        r.X < Right && X < r.Right && r.Y < Bottom && Y < r.Bottom;

    public static Rectangle FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public bool Equals(Rectangle other) =>
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is Rectangle r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(Rectangle left, Rectangle right) => left.Equals(right);
    public static bool operator !=(Rectangle left, Rectangle right) => !left.Equals(right);
    public override string ToString() => $"{{X={X},Y={Y},Width={Width},Height={Height}}}";
}

public struct RectangleF : IEquatable<RectangleF>
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public static readonly RectangleF Empty = default;

    public RectangleF(float x, float y, float width, float height)
    { X = x; Y = y; Width = width; Height = height; }

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public bool Contains(float x, float y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public bool Contains(PointF p) => Contains(p.X, p.Y);

    public bool Contains(RectangleF r) =>
        r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    public bool IntersectsWith(RectangleF r) =>
        r.X < Right && X < r.Right && r.Y < Bottom && Y < r.Bottom;

    /// <summary>Grows (or shrinks, with negatives) this rect in place.</summary>
    public void Inflate(float x, float y)
    {
        X -= x; Y -= y; Width += 2 * x; Height += 2 * y;
    }

    public static RectangleF Inflate(RectangleF rect, float x, float y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    public static RectangleF Offset(RectangleF rect, float x, float y)
    {
        rect.Offset(x, y);
        return rect;
    }

    public void Offset(float x, float y) { X += x; Y += y; }

    public static RectangleF FromLTRB(float left, float top, float right, float bottom) =>
        new(left, top, right - left, bottom - top);

    public bool Equals(RectangleF other) =>
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is RectangleF r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(RectangleF left, RectangleF right) => left.Equals(right);
    public static bool operator !=(RectangleF left, RectangleF right) => !left.Equals(right);
    public override string ToString() => $"{{X={X},Y={Y},Width={Width},Height={Height}}}";
}
