// ShellPaint — the single junction between the SkiaSharp engine and WinForms.
//
// Everything Retro96 draws (pages, frames, selection highlights, carets,
// focus rectangles) is composed on an engine Graphics over a screen-size
// Skia bitmap; the composite is then handed to WinForms through ONE GDI
// DrawImage of a converted twin bitmap.  That final blit is the only GDI
// call left in the codebase — the WinForms paint pipeline itself is
// GDI-based, so a Windows Forms control cannot present pixels any other
// way; every line of engine rendering above it is pure SkiaSharp.
//
// The twin GDI bitmap is allocated ONCE per surface size and reused for
// every paint: only its pixels are rewritten (locked write +
// premultiplied→straight alpha fixup).  The old code allocated a fresh
// screen-size GDI bitmap on every single paint.
//
// Pixel layout note: the engine bitmap is BGRA-8888 premultiplied — byte-
// for-byte what GDI's Format32bppArgb reads on little-endian hosts when
// alpha is 255 (the whole-document renders are opaque: the renderer clears
// with the page background first).  Any sub-255 alpha from translucent
// overlay compositing is unpremultiplied during the copy so GDI's own
// blending sees straight colours.
using System;
using System.Runtime.InteropServices;
using Retro96.Drawing;
using SkiaSharp;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace Retro96;

/// <summary>
/// Owns the screen-size Skia surface + its GDI twin.  Begin() returns the
/// engine Graphics to compose a frame on; Blit() converts and presents it.
/// Not thread-safe — WinForms paints on the UI thread only.
/// </summary>
internal sealed class ScreenComposer : IDisposable
{
    private SKBitmap? _surface;
    private Bitmap? _surfaceWrapper;      // owns _surface
    private Graphics? _gfx;
    private Gdi.Bitmap? _twin;
    private bool _disposed;

    /// <summary>Screen-size Skia surface for one frame's composition.</summary>
    public Graphics Begin(int width, int height)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ScreenComposer));

        // A 0-sized surface is invalid in both Skia and GDI+.  The canvas
        // clamps its client size, but this is the engine/WinForms junction
        // — it defends itself.
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_surface == null || _surface.Width != width || _surface.Height != height)
        {
            // Teardown order matters (Graphics → wrapper → twin).  Each
            // field is nulled the moment its native object dies, so a
            // failure in the re-creation below can never leave a dangling
            // reference for the next Blit() to dereference mid-paint.
            _gfx?.Dispose();
            _gfx = null;
            _surfaceWrapper?.Dispose();   // disposes the wrapped SKBitmap too
            _surfaceWrapper = null;
            _surface = null;
            _twin?.Dispose();
            _twin = null;

            var surface = new SKBitmap(
                new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            try
            {
                _surfaceWrapper = new Bitmap(surface);
                _surface = surface;
            }
            catch
            {
                // Wrapper construction failed → nobody owns the surface.
                surface.Dispose();
                throw;
            }
        }

        _gfx ??= Graphics.FromImage(_surfaceWrapper!);
        return _gfx;
    }

    /// <summary>
    /// Presents the composed frame: rewrites the REUSED GDI twin from the
    /// current Skia surface and blits it with pixel-exact settings — no
    /// DPI rescale, no resampling — so scrolling stays stutter-free and
    /// pixel-true.
    /// </summary>
    public void Blit(Gdi.Graphics target)
    {
        if (_disposed || target == null || _surfaceWrapper == null || _surface == null)
            return;

        try
        {
            // Reuse the twin across paints — only its pixels change
            // frame-to-frame.  (The old code allocated a fresh screen-size
            // GDI bitmap per paint: ~8 MB at 1080p, hundreds of MB/s of
            // LOH churn while scroll-dragging.)
            if (_twin == null || _twin.Width != _surface.Width || _twin.Height != _surface.Height)
            {
                _twin?.Dispose();
                _twin = new Gdi.Bitmap(_surface.Width, _surface.Height,
                    GdiImaging.PixelFormat.Format32bppArgb);
            }

            if (!SkiaWinForms.CopyInto(_surfaceWrapper, _twin))
            {
                // Never present a half-written or mismatched twin.
                _twin.Dispose();
                _twin = null;
                return;
            }
        }
        catch (Exception ex)
        {
            // A conversion failure must NOT escape into the WinForms paint
            // pipeline — there is no handler there, it takes the app down.
            // Degrade to "present nothing this frame" and log.
            DebugLog.WriteException("ScreenComposer.Blit", ex);
            _twin?.Dispose();
            _twin = null;
            return;
        }

        // Pixel-exact present.  Save/Restore returns the caller's Graphics
        // to byte-identical state — the old code hand-restored two of the
        // three properties it touched (InterpolationMode leaked).
        var state = target.Save();
        try
        {
            target.CompositingMode = Gdi.Drawing2D.CompositingMode.SourceCopy;
            target.InterpolationMode = Gdi.Drawing2D.InterpolationMode.NearestNeighbor;
            target.PixelOffsetMode = Gdi.Drawing2D.PixelOffsetMode.Half;
            target.DrawImage(_twin, 0, 0, _twin.Width, _twin.Height);
        }
        finally
        {
            target.Restore(state);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gfx?.Dispose();
        _surfaceWrapper?.Dispose();   // owns + disposes _surface
        _twin?.Dispose();
        _gfx = null;
        _surfaceWrapper = null;
        _surface = null;
        _twin = null;
    }
}

/// <summary>Engine-bitmap → GDI-bitmap conversion (screen blit, printing).</summary>
internal static class SkiaWinForms
{
    // Reused per conversion — a paint never allocates.
    [ThreadStatic] private static byte[]? _rowBuffer;

    private static byte[] GetRowBuffer(int minBytes)
    {
        var buf = _rowBuffer;
        if (buf == null || buf.Length < minBytes)
            _rowBuffer = buf = new byte[minBytes];
        return buf;
    }

    /// <summary>
    /// Copies an engine bitmap's pixels into a same-size GDI bitmap,
    /// unpremultiplying translucent pixels on the way.  The destination is
    /// written through a locked write — no allocation, no intermediate
    /// bitmap.  Throws on GDI+ failures; callers wrap (Blit/ToGdi do).
    /// </summary>
    public static bool CopyInto(Bitmap engine, Gdi.Bitmap target)
    {
        if (engine == null || target == null) return false;

        var sk = engine.SkBitmap;
        if (sk == null || sk.Width <= 0 || sk.Height <= 0) return false;
        if (sk.Width != target.Width || sk.Height != target.Height) return false;

        IntPtr srcBase = sk.GetPixels();
        if (srcBase == IntPtr.Zero) return false;   // empty / failed allocation

        int srcRow = sk.RowBytes;
        int widthBytes = sk.Width * 4;

        var bd = target.LockBits(
            new Gdi.Rectangle(0, 0, sk.Width, sk.Height),
            GdiImaging.ImageLockMode.WriteOnly,
            GdiImaging.PixelFormat.Format32bppArgb);
        try
        {
            // 32bpp rows are 4-byte aligned on every current platform, so
            // stride == width*4 — but this is the raw-memory boundary: a
            // bottom-up (negative) or padded stride bails instead of
            // guessing offsets.  The old code copied sk.RowBytes into a
            // destination row of bd.Stride capacity — a padded source row
            // was a straight out-of-bounds write.
            if (bd.Stride < widthBytes || srcRow < widthBytes)
                return false;

            byte[] buf = GetRowBuffer(widthBytes);
            IntPtr dstBase = bd.Scan0;

            for (int y = 0; y < sk.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(srcBase, y * srcRow), buf, 0, widthBytes);

                // Premultiplied → straight alpha for translucent pixels.
                // Whole-document renders are always fully opaque (the
                // renderer clears with the page background first) — the
                // old code still ran the per-pixel divide/branch over
                // every pixel of every row regardless, which is pure
                // waste in that case and was the actual cost behind
                // "scrolling lags at fullscreen but not in a small
                // window": the same wasted work, just 6-7x more pixels
                // at 1080p+. A fast pre-scan lets a fully-opaque row skip
                // straight to the bulk copy below.
                bool rowOpaque = true;
                for (int i = 3; i < widthBytes; i += 4)
                {
                    if (buf[i] != 255) { rowOpaque = false; break; }
                }

                if (!rowOpaque)
                {
                    for (int i = 3; i < widthBytes; i += 4)
                    {
                        byte a = buf[i];
                        if (a == 255) continue;
                        if (a == 0)
                        {
                            buf[i - 3] = buf[i - 2] = buf[i - 1] = 0;
                        }
                        else
                        {
                            buf[i - 3] = (byte)(buf[i - 3] * 255 / a);
                            buf[i - 2] = (byte)(buf[i - 2] * 255 / a);
                            buf[i - 1] = (byte)(buf[i - 1] * 255 / a);
                        }
                    }
                }

                Marshal.Copy(buf, 0, IntPtr.Add(dstBase, y * bd.Stride), widthBytes);
            }
            return true;
        }
        finally
        {
            target.UnlockBits(bd);
        }
    }

    /// <summary>Engine-bitmap → fresh GDI bitmap (printing and one-shot
    /// uses; the screen blit path reuses its twin instead).</summary>
    public static Gdi.Bitmap? ToGdi(Bitmap? engine)
    {
        if (engine?.SkBitmap is not { } sk || sk.Width <= 0 || sk.Height <= 0)
            return null;

        Gdi.Bitmap? gdi = null;
        try
        {
            gdi = new Gdi.Bitmap(sk.Width, sk.Height, GdiImaging.PixelFormat.Format32bppArgb);
            if (!CopyInto(engine, gdi))
            {
                gdi.Dispose();
                return null;
            }
            var result = gdi;
            gdi = null;          // ownership handed to the caller
            return result;
        }
        catch (Exception ex)
        {
            // The old code leaked the freshly allocated bitmap when
            // LockBits or the pixel copy threw mid-conversion.
            DebugLog.WriteException("SkiaWinForms.ToGdi", ex);
            gdi?.Dispose();
            return null;
        }
    }
}