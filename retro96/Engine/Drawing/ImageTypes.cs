// Retro96 image types over SkiaSharp.
//
// Web images are held as immutable SKImage objects. On the interactive
// GPU path they are promoted once to a texture-backed SKImage for the active
// Skia GPU context and then reused. Mutable SKBitmap storage remains available
// only for compatibility code that genuinely needs writable pixels.
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
    private SKImage? _skImage;
    private SKImage? _gpuImage;
    private IntPtr _gpuContextHandle;
    private readonly object _imageLock = new();

    // Mutable raster storage is kept only for compatibility pixel APIs.
    internal SKBitmap? Sk { get; set; }

    protected Image()
    {
    }

    protected Image(SKImage image)
    {
        _skImage = image ?? throw new ArgumentNullException(nameof(image));
    }

    public int Width
    {
        get
        {
            lock (_imageLock)
                return _skImage?.Width ?? Sk?.Width ?? 0;
        }
    }

    public int Height
    {
        get
        {
            lock (_imageLock)
                return _skImage?.Height ?? Sk?.Height ?? 0;
        }
    }

    /// <summary>Print/plumbing compat: Skia images carry no DPI, assume 96.</summary>
    public float HorizontalResolution => 96f;
    public float VerticalResolution => 96f;

    /// <summary>
    /// Returns the immutable Skia image used by all image drawing paths.
    /// Mutable bitmap-backed images are snapshotted once and then reused.
    /// </summary>
    internal SKImage GetSkImage()
    {
        lock (_imageLock)
        {
            if (_skImage != null)
                return _skImage;

            var bitmap = Sk ?? throw new InvalidOperationException("Image is empty.");
            // Snapshot the writable bitmap instead of making the source
            // immutable; compatibility callers may continue to mutate it.
            using var snapshot = bitmap.Copy();
            snapshot.SetImmutable();
            _skImage = SKImage.FromBitmap(snapshot)
                ?? throw new InvalidOperationException("Could not create an SKImage from the bitmap.");
            return _skImage;
        }
    }

    internal SKBitmap GetWritableSkBitmap()
    {
        lock (_imageLock)
        {
            if (Sk != null)
                return Sk;

            var image = _skImage ?? throw new InvalidOperationException("Image is empty.");
            var bitmap = SKBitmap.FromImage(image)
                ?? throw new InvalidOperationException("Could not create a writable bitmap from the image.");
            _gpuImage?.Dispose();
            _gpuImage = null;
            _gpuContextHandle = IntPtr.Zero;
            _skImage.Dispose();
            _skImage = null;
            Sk = bitmap;
            return bitmap;
        }
    }

    /// <summary>
    /// Returns a texture-backed image for the supplied Skia GPU context.
    /// The texture is cached per image/context and reused by subsequent draws.
    /// </summary>
    internal SKImage GetGpuImage(GRContext? gpuContext)
    {
        var source = GetSkImage();
        if (gpuContext == null)
            return source;

        lock (_imageLock)
        {
            if (_gpuImage != null &&
                _gpuContextHandle == gpuContext.Handle &&
                _gpuImage.IsValid(gpuContext))
            {
                return _gpuImage;
            }

            _gpuImage?.Dispose();
            _gpuImage = null;
            _gpuContextHandle = IntPtr.Zero;

            try
            {
                var textureImage = source.ToTextureImage(gpuContext);
                if (textureImage != null && textureImage.IsValid(gpuContext))
                {
                    if (!ReferenceEquals(textureImage, source))
                    {
                        _gpuImage = textureImage;
                        _gpuContextHandle = gpuContext.Handle;
                    }
                    return textureImage;
                }
            }
            catch (Exception ex)
            {
                Retro96.DebugLog.WriteException("Image GPU texture promotion", ex);
            }

            // Texture promotion is an optimization. Drawing the immutable
            // SKImage directly on a GPU canvas remains correct and Skia can
            // cache its GPU resource internally.
            return source;
        }
    }

    /// <summary>Decodes any format Skia understands.</summary>
    public static Image FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var image = SKImage.FromEncodedData(stream);
        return image == null
            ? throw new ArgumentException("Image data could not be decoded.")
            : new Bitmap(image);
    }

    protected void InvalidateImageCaches()
    {
        lock (_imageLock)
        {
            _gpuImage?.Dispose();
            _gpuImage = null;
            _gpuContextHandle = IntPtr.Zero;
            _skImage?.Dispose();
            _skImage = null;
        }
    }

    public void Dispose()
    {
        lock (_imageLock)
        {
            _gpuImage?.Dispose();
            _gpuImage = null;
            _gpuContextHandle = IntPtr.Zero;
            _skImage?.Dispose();
            _skImage = null;
            Sk?.Dispose();
            Sk = null;
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class Bitmap : Image
{
    /// <summary>Zeroed (fully transparent) BGRA bitmap, legacy bitmap semantics.</summary>
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

    /// <summary>Wraps an immutable Skia image (preferred for web images/frames).</summary>
    public Bitmap(SKImage image) : base(image)
    {
    }

    /// <summary>Copies an existing image (legacy clone semantics).</summary>
    public Bitmap(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = image.GetSkImage();
        Sk = SKBitmap.FromImage(source)
            ?? throw new InvalidOperationException("Could not copy source image.");
    }

    public Bitmap Clone() => new(this);

    /// <summary>Reads one pixel as an unpremultiplied colour (test pixel sampling).</summary>
    public Color GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return Color.Empty;

        using var bitmap = SKBitmap.FromImage(GetSkImage());
        if (bitmap == null) return Color.Empty;
        var c = bitmap.GetPixel(x, y);
        return Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    }

    public void SetPixel(int x, int y, Color color)
    {
        var bitmap = SkBitmap;
        if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
            return;

        InvalidateImageCaches();
        bitmap.SetPixel(x, y, color.ToSkColor());
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>Exposes the raw BGRA buffer for bulk pixel writes (XBM decode).</summary>
    public BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
    {
        var bitmap = SkBitmap;
        return new BitmapData
        {
            Scan0 = bitmap.GetPixels(),
            Stride = bitmap.RowBytes,
            Width = bitmap.Width,
            Height = bitmap.Height,
            PixelFormat = PixelFormat.Format32bppArgb,
        };
    }

    public void UnlockBits(BitmapData data) => NotifyPixelsChanged();

    /// <summary>Encodes to a file.</summary>
    public void Save(string filename, ImageFormat format)
    {
        using var data = GetSkImage().Encode(
            format switch
            {
                ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                ImageFormat.Bmp => SKEncodedImageFormat.Bmp,
                ImageFormat.Gif => SKEncodedImageFormat.Gif,
                _ => SKEncodedImageFormat.Png,
            }, 100);
        using (var fs = File.Create(filename))
            data.SaveTo(fs);
    }

    /// <summary>Encodes to an in-memory PNG.</summary>
    public byte[] EncodePng() => GetSkImage().Encode(SKEncodedImageFormat.Png, 100).ToArray();

    /// <summary>Marks writable pixels as changed and drops stale image/texture caches.</summary>
    internal void NotifyPixelsChanged()
    {
        var bitmap = SkBitmap;
        bitmap.NotifyPixelsChanged();
        InvalidateImageCaches();
    }

    internal SKBitmap SkBitmap => GetWritableSkBitmap();
}
