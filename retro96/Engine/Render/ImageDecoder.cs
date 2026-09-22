using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Retro96.Drawing;
using SkiaSharp;

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
    bool IsAnimated);

/// <summary>
/// Decodes image bytes into a DecodedImage (single frame or animated).
///
/// GIF87a/GIF89a (multi-frame, per-frame delay, disposal, interlaced,
/// transparency) decode through Skia's SKCodec — each frame is composited
/// onto the running canvas with its disposal method applied, exactly what
/// GDI+'s SelectActiveFrame used to do; JPEG baseline/progressive, PNG, BMP
/// and ICO decode straight through SKBitmap; XBM (the era's other icon
/// format) through a hand parser.  Anything unrecognised or truncated
/// yields the broken-image icon so layout keeps a stable box instead of
/// collapsing.
/// </summary>
public static class ImageDecoder
{
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

            // JPEG / PNG / BMP / ICO — anything Skia understands.
            using var ms = new MemoryStream(data);
            var sk = SKBitmap.Decode(ms);
            if (sk == null || sk.Width <= 0 || sk.Height <= 0)
                return Broken();
            return new DecodedImage([new Bitmap(sk)], [0], false);
        }
        catch
        {
            return Broken();
        }
    }

    private static DecodedImage Broken() =>
        new([BrokenImageIcon.Create()], [0], false);

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
            return new DecodedImage([new Bitmap(single)], [0], false);
        }

        var frames = new List<Bitmap>(frameCount);
        var delays = new List<int>(frameCount);

        // The compositing canvas the frames accumulate on.
        using var compositor = new SKBitmap(info);
        using var surface = SKSurface.Create(info, compositor.GetPixels(), info.RowBytes);
        if (surface == null)
            return Broken();
        using var canvas = surface.Canvas;

        // Snapshot saved before the current frame drew (RestoreToPrevious).
        SKBitmap? previous = null;

        for (int i = 0; i < frameCount; i++)
        {
            var frame = new SKBitmap(info);
            var result = codec.GetPixels(info, frame.GetPixels(), new SKCodecOptions(i));
            if (result != SKCodecResult.Success)
            {
                frame.Dispose();
                break;
            }

            // Save the pre-draw state when this frame wants it restored.
            var disposal = codec.FrameInfo[i].DisposalMethod;
            if (disposal == SKCodecAnimationDisposalMethod.RestorePrevious)
            {
                previous?.Dispose();
                previous = compositor.Copy();
            }

            if (i == 0)
                canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(frame, 0, 0);
            frame.Dispose();

            frames.Add(new Bitmap(compositor.Copy()));

            // Disposal AFTER the snapshot: erase the frame's area or roll
            // the whole canvas back to the pre-frame state.
            switch (disposal)
            {
                case SKCodecAnimationDisposalMethod.RestoreBackgroundColor:
                    canvas.Clear(SKColors.Transparent);
                    break;
                case SKCodecAnimationDisposalMethod.RestorePrevious:
                    if (previous != null)
                    {
                        canvas.Clear(SKColors.Transparent);
                        canvas.DrawBitmap(previous, 0, 0);
                    }
                    break;
            }

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
        var locked = bmp.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                IntPtr row = locked.Scan0 + y * locked.Stride;
                for (int x = 0; x < width; x++)
                {
                    bool set = (bits[y * bytesPerRow + (x >> 3)] & (1 << (x & 7))) != 0;
                    // 1-bit = black opaque; 0-bit = transparent
                    int pixel = set ? unchecked((int)0xFF000000u) : 0;
                    Marshal.WriteInt32(row, x * 4, pixel);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(locked);
        }

        return new DecodedImage([bmp], [0], false);
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
                using (var g = Graphics.FromImage(bmp))
                {
                    g.FillRectangle(Brushes.LightGray, 0, 0, 24, 24);
                    g.DrawRectangle(Pens.Gray, 0, 0, 23, 23);
                    using var redPen = new Pen(Color.Red, 2);
                    g.DrawLine(redPen, 4, 4, 19, 19);
                    g.DrawLine(redPen, 19, 4, 4, 19);
                }
                _cached = bmp;
            }
            return _cached.Clone();
        }
    }
}
