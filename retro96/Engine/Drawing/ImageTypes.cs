// Retro96.Graphics — image types over SkiaSharp's SKBitmap.
//
// Every decoded image, rendered page and shell blit surface in the engine is
// one of these; decoding goes through SKBitmap/SKCodec so GIF (animated),
// JPEG, PNG and BMP all decode natively on Linux and Windows alike — no
// GDI+/libgdiplus involved anywhere.
//
// Pixel memory is always BGRA-8888 premultiplied, which is byte-for-byte the
// same layout System.Drawing's Format32bppArgb used on little-endian hosts —
// the XBM decoder's Marshal writes and the WinForms screen blit keep working
// unchanged.
using SkiaSharp;

namespace Retro96.Drawing;

public enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
    Gif,
}

public enum PixelFormat
{
    Format32bppArgb,
    Format32bppRgb,
    Format24bppRgb,
}

public enum ImageLockMode
{
    ReadOnly,
    WriteOnly,
    ReadWrite,
}

/// <summary>Locked pixel access descriptor (Scan0 + row stride).</summary>
public sealed class BitmapData
{
    public IntPtr Scan0 { get; internal set; }
    public int Stride { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public PixelFormat PixelFormat { get; internal set; }
}

public abstract class Image : IDisposable
{
    internal SKBitmap? Sk { get; set; }

    public int Width => Sk?.Width ?? 0;
    public int Height => Sk?.Height ?? 0;

    /// <summary>Print/plumbing compat: Skia bitmaps carry no DPI, assume 96.</summary>
    public float HorizontalResolution => 96f;
    public float VerticalResolution => 96f;

    public void Dispose()
    {
        Sk?.Dispose();
        Sk = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Decodes any format Skia understands (GIF/JPEG/PNG/BMP/ICO/WBMP).</summary>
    public static Image FromStream(Stream stream)
    {
        var sk = SKBitmap.Decode(stream);
        return sk == null
            ? throw new ArgumentException("Image data could not be decoded.")
            : new Bitmap(sk);
    }
}

public sealed class Bitmap : Image
{
    /// <summary>Zeroed (fully transparent) BGRA bitmap, GDI-new-Bitmap semantics.</summary>
    public Bitmap(int width, int height)
    {
        if (width <= 0 || height <= 0)
            width = height = 1;
        Sk = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    public Bitmap(int width, int height, PixelFormat format) : this(width, height)
    {
        // Single canonical BGRA layout; the format argument is accepted for
        // source compatibility.
    }

    /// <summary>Wraps (takes ownership of) a decoded Skia bitmap.</summary>
    public Bitmap(SKBitmap sk)
    {
        Sk = sk ?? throw new ArgumentNullException(nameof(sk));
    }

    /// <summary>Copies an existing image (frame snapshot semantics).</summary>
    public Bitmap(Image image)
    {
        if (image.Sk == null) throw new ArgumentException("Source image is empty.");
        Sk = image.Sk.Copy();
    }

    public Bitmap Clone() => new(this);

    /// <summary>Reads one pixel as an unpremultiplied colour (test pixel sampling).</summary>
    public Color GetPixel(int x, int y)
    {
        if (Sk == null || x < 0 || y < 0 || x >= Sk.Width || y >= Sk.Height)
            return Color.Empty;
        var c = Sk.GetPixel(x, y);
        return Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    }

    public void SetPixel(int x, int y, Color color)
    {
        if (Sk == null || x < 0 || y < 0 || x >= Sk.Width || y >= Sk.Height)
            return;
        // Skia converts the unpremultiplied colour to the bitmap's premul
        // storage itself.
        Sk.SetPixel(x, y, color.ToSkColor());
    }

    /// <summary>Exposes the raw BGRA buffer for bulk pixel writes (XBM decode).</summary>
    public BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
    {
        if (Sk == null) throw new InvalidOperationException("Bitmap is empty.");
        return new BitmapData
        {
            Scan0 = Sk.GetPixels(),
            Stride = Sk.RowBytes,
            Width = Sk.Width,
            Height = Sk.Height,
            PixelFormat = PixelFormat.Format32bppArgb,
        };
    }

    public void UnlockBits(BitmapData data)
    {
        // The buffer is always live; nothing to flush.
    }

    /// <summary>Encodes to a file (PNG output for probes and the diff harness).</summary>
    public void Save(string filename, ImageFormat format)
    {
        if (Sk == null) throw new InvalidOperationException("Bitmap is empty.");
        using var data = Sk.Encode(
            format switch
            {
                ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                ImageFormat.Bmp => SKEncodedImageFormat.Bmp,
                _ => SKEncodedImageFormat.Png,
            }, 100);
        using (var fs = File.Create(filename))
            data.SaveTo(fs);
    }

    /// <summary>Encodes to an in-memory PNG (harness JSON embedding).</summary>
    public byte[] EncodePng()
    {
        if (Sk == null) return Array.Empty<byte>();
        using var data = Sk.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal SKBitmap SkBitmap => Sk ?? throw new InvalidOperationException("Bitmap is empty.");
}
