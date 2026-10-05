using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Retro96.Drawing;
using SkiaSharp;
using Svg.Skia;

namespace Retro96.Engine.Render;

/// <summary>
/// Result of decoding an image.
/// </summary>
/// <param name="Frames">One or more frames (animated GIFs have multiple)</param>
/// <param name="DelaysMs">Delay per frame in milliseconds</param>
/// <param name="IsAnimated">True if this is an animated image</param>
public record DecodedImage(
    IReadOnlyList<Bitmap> Frames,
    IReadOnlyList<int> DelaysMs,
    bool IsAnimated,
    bool IsBroken = false);

/// <summary>
/// Decodes image bytes into a DecodedImage (single frame or animated).
///
/// GIF87a/GIF89a (multi-frame, per-frame delay, disposal, interlaced,
/// transparency) decode through Skia's SKCodec — each frame is composited
/// onto the running canvas with its disposal method applied, exactly what
/// legacy frame-selection code previously did; SVGs are rasterized through
/// Svg.Skia; JPEG, PNG, BMP, ICO and other Skia-supported formats decode
/// directly into immutable SKImage objects; XBM (the era's other icon
/// format) uses a hand parser. Anything unrecognised or truncated yields
/// the broken-image icon so layout keeps a stable box instead of collapsing.
/// </summary>
public static class ImageDecoder
{
    private const int SvgRasterScale = 4;

    public static DecodedImage Decode(byte[] data, string contentType)
    {
        if (data == null || data.Length == 0)
            return Broken();

        try
        {
            if (IsGif(data))
                return DecodeGif(data);

            if (IsXbm(data, contentType))
                return DecodeXbm(data);

            if (IsSvg(data, contentType))
                return DecodeSvg(data);

            // JPEG / PNG / BMP / ICO / WebP / AVIF — keep the decoded web
            // resource as an immutable SKImage. Skia can decode lazily and
            // the browser's GPU canvas can promote the same image to a
            // texture without another bitmap-blit layer.
            var image = SKImage.FromEncodedData(data);
            if (image == null || image.Width <= 0 || image.Height <= 0)
            {
                image?.Dispose();
                return Broken();
            }
            return new DecodedImage([new Bitmap(image)], [0], false);
        }
        catch
        {
            return Broken();
        }
    }

    private static DecodedImage Broken() =>
        new([BrokenImageIcon.Create()], [0], false, true);

    private static bool IsGif(byte[] data) =>
        data.Length >= 6 &&
        data[0] == 'G' && data[1] == 'I' && data[2] == 'F' &&
        data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';

    private static bool IsXbm(byte[] data, string contentType)
    {
        if (contentType != null &&
            contentType.Contains("xbm", StringComparison.OrdinalIgnoreCase))
            return true;

        // XBM files are C source — start with #define
        var start = Encoding.ASCII.GetString(data, 0, Math.Min(32, data.Length));
        return start.Contains("#define");
    }

    private static bool IsSvg(byte[] data, string contentType)
    {
        if (contentType?.Contains("image/svg+xml", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return false;

        string prefix = Encoding.UTF8.GetString(data, 0, Math.Min(512, data.Length));
        return prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static DecodedImage DecodeSvg(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var svg = new SKSvg();
        var picture = svg.Load(stream);
        if (picture == null)
            return Broken();

        var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
            !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom) ||
            bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.Width > 8192 || bounds.Height > 8192 ||
            bounds.Width * bounds.Height > 32_000_000)
            return Broken();

        float scale = Math.Min(SvgRasterScale, Math.Min(
            8192f / bounds.Width,
            Math.Min(8192f / bounds.Height,
                MathF.Sqrt(32_000_000f / (bounds.Width * bounds.Height)))));
        int logicalWidth = (int)MathF.Ceiling(bounds.Width);
        int logicalHeight = (int)MathF.Ceiling(bounds.Height);
        int width = (int)MathF.Ceiling(bounds.Width * scale);
        int height = (int)MathF.Ceiling(bounds.Height * scale);
        using var bitmap = new SKBitmap(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var matrix = SKMatrix.CreateScaleTranslation(
                scale, scale, -bounds.Left * scale, -bounds.Top * scale);
            canvas.DrawPicture(picture, in matrix);
            canvas.Flush();
        }
        bitmap.SetImmutable();
        var rasterized = SKImage.FromBitmap(bitmap);
        return rasterized == null
            ? Broken()
            : new DecodedImage(
                [new Bitmap(rasterized, logicalWidth, logicalHeight)], [0], false);
    }

    /// <summary>
    /// GIF decoding through SKCodec: every frame is decoded full-size (its
    /// transparent regions preserved) and composited over the running
    /// canvas; the disposal method then rewinds/erases the canvas for the
    /// next frame.  A zero duration becomes 100 ms — what browsers did for
    /// compatibility with encoders that emitted 0.
    /// </summary>
    private static DecodedImage DecodeGif(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            return Broken();

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);

        int frameCount = codec.FrameCount;
        if (frameCount <= 1)
        {
            var single = new SKBitmap(info);
            var result = codec.GetPixels(info, single.GetPixels());
            if (result != SKCodecResult.Success)
            {
                single.Dispose();
                return Broken();
            }
            single.NotifyPixelsChanged();
            single.SetImmutable();
            var image = SKImage.FromBitmap(single);
            single.Dispose();
            return image == null
                ? Broken()
                : new DecodedImage([new Bitmap(image)], [0], false);
        }

        var frames = new List<Bitmap>(frameCount);
        var delays = new List<int>(frameCount);

        // Skia's codec performs the required-frame composition and disposal
        // handling when a frame is decoded with its FrameIndex.  Decoding
        // each logical frame through the codec avoids treating a partial GIF
        // frame as a full-canvas update (the old manual compositor cleared
        // the entire canvas for RestoreBackgroundColor).
        for (int i = 0; i < frameCount; i++)
        {
            var frame = new SKBitmap(info);
            var result = codec.GetPixels(info, frame.GetPixels(), new SKCodecOptions(i));
            if (result != SKCodecResult.Success)
            {
                frame.Dispose();
                break;
            }

            frame.NotifyPixelsChanged();
            frame.SetImmutable();
            var image = SKImage.FromBitmap(frame);
            frame.Dispose();
            if (image == null)
                break;
            frames.Add(new Bitmap(image));
            int dur = codec.FrameInfo[i].Duration;
            delays.Add(dur > 0 ? dur : 100);
        }

        if (frames.Count == 0)
            return Broken();

        return new DecodedImage(frames, delays, frames.Count > 1);
    }

    private static readonly Regex _widthRegex =
        new(@"#define\s+\w+_width\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex _heightRegex =
        new(@"#define\s+\w+_height\s+(\d+)", RegexOptions.Compiled);
    // Anchored to the bits declaration — the FIRST brace group in the file
    // is not necessarily the pixel array (C comments, hot-spot tables).
    private static readonly Regex _bitsRegex =
        new(@"_bits\s*\[\s*\]\s*=\s*\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex _anyBracesRegex =
        new(@"\{([^}]*)\}", RegexOptions.Compiled);
    // Hex first, then SIGNED decimal — old hand-written XBM freely used
    // -1, and dropping the sign inverted every such byte.
    private static readonly Regex _hexTokenRegex =
        new(@"0[xX][0-9a-fA-F]+|-?\d+", RegexOptions.Compiled);

    /// <summary>
    /// XBM: #define w/h + a C array of bytes, LSB-first bit order, 1 = black.
    /// Pixels are written straight into the BGRA buffer (premultiplied:
    /// opaque black 0xFF000000, transparent 0).
    /// </summary>
    private static DecodedImage DecodeXbm(byte[] data)
    {
        string text = Encoding.ASCII.GetString(data);

        var wMatch = _widthRegex.Match(text);
        var hMatch = _heightRegex.Match(text);
        if (!wMatch.Success || !hMatch.Success)
            return Broken();

        int width = int.Parse(wMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        int height = int.Parse(hMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            return Broken();

        var bitsMatch = _bitsRegex.Match(text);
        if (!bitsMatch.Success)
            bitsMatch = _anyBracesRegex.Match(text);   // fall back to first brace group
        if (!bitsMatch.Success)
            return Broken();

        int bytesPerRow = (width + 7) / 8;
        byte[] bits = new byte[bytesPerRow * height];

        int bi = 0;
        foreach (Match token in _hexTokenRegex.Matches(bitsMatch.Groups[1].Value))
        {
            if (bi >= bits.Length) break;
            string t = token.Value;
            int value = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture);
            bits[bi++] = (byte)(value & 0xFF);
        }

        var bmp = new Bitmap(width, height);
        Span<byte> pixels = bmp.SkBitmap.GetPixelSpan();
        pixels.Clear();
        int rowBytes = bmp.SkBitmap.RowBytes;
        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                bool set = (bits[y * bytesPerRow + (x >> 3)] & (1 << (x & 7))) != 0;
                if (!set) continue;
                int offset = row + x * 4;
                pixels[offset + 0] = 0;   // B
                pixels[offset + 1] = 0;   // G
                pixels[offset + 2] = 0;   // R
                pixels[offset + 3] = 255; // A
            }
        }
        bmp.SkBitmap.NotifyPixelsChanged();
        bmp.SkBitmap.SetImmutable();
        var image = SKImage.FromBitmap(bmp.SkBitmap);
        bmp.Dispose();
        return image == null
            ? Broken()
            : new DecodedImage([new Bitmap(image)], [0], false);
    }
}

/// <summary>
/// 24×24 grey box with a red X — the broken-image placeholder
/// (Netscape-style) that keeps failed images from collapsing layout.
/// </summary>
public static class BrokenImageIcon
{
    private static readonly object _lock = new();
    private static Bitmap? _cached;

    public static Bitmap Create()
    {
        // Clone per caller so each cache entry owns a disposable copy; the
        // lock keeps the one-time build from racing on background fetches.
        lock (_lock)
        {
            if (_cached == null)
            {
                var bmp = new Bitmap(24, 24);
                using var surface = SKSurface.Create(bmp.SkBitmap.Info, bmp.SkBitmap.GetPixels(), bmp.SkBitmap.RowBytes)
                    ?? throw new InvalidOperationException("Unable to create broken-image surface.");
                var canvas = surface.Canvas;
                using var bg = new SKPaint { Color = new SKColor(0xD3, 0xD3, 0xD3, 0xFF), IsAntialias = false, Style = SKPaintStyle.Fill };
                using var border = new SKPaint { Color = SKColors.Gray, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                using var red = new SKPaint { Color = SKColors.Red, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                canvas.Clear(SKColors.Transparent);
                canvas.DrawRect(SKRect.Create(0, 0, 24, 24), bg);
                canvas.DrawRect(SKRect.Create(0.5f, 0.5f, 23, 23), border);
                canvas.DrawLine(4, 4, 19, 19, red);
                canvas.DrawLine(19, 4, 4, 19, red);
                surface.Flush();
                bmp.SkBitmap.SetImmutable();
                var image = SKImage.FromBitmap(bmp.SkBitmap);
                bmp.Dispose();
                if (image == null)
                    throw new InvalidOperationException("Could not create broken-image SKImage.");
                _cached = new Bitmap(image);
            }
            return _cached.Clone();
        }
    }
}
