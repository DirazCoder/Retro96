// ShellPaint — SkiaSharp presentation for the WinForms browser surface.
//
// The interactive browser path is rendered directly onto the SKGLControl GPU
// surface. Only the printer interop below crosses into System.Drawing because
// PrintDocument requires a GDI+/WinForms bitmap at the OS boundary.

using System;
using Retro96.Drawing;
using SkiaSharp;

namespace Retro96;

/// <summary>
/// Explicit printer interop only. The browser screen never uses this class.
/// </summary>
internal static class SkiaPrintInterop
{
    public static System.Drawing.Bitmap? ToPrinterBitmap(Bitmap? engine)
    {
        if (engine?.SkBitmap is not { } sk || sk.Width <= 0 || sk.Height <= 0)
            return null;

        try
        {
            // Copy through Skia into a managed BGRA buffer, then let the
            // The Windows printer API consumes this one platform bitmap at the boundary.
            using var image = SKImage.FromBitmap(sk);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new System.IO.MemoryStream();
            encoded.SaveTo(ms);
            ms.Position = 0;
            using var decoded = new System.Drawing.Bitmap(ms);
            return new System.Drawing.Bitmap(decoded);
        }
        catch (Exception ex)
        {
            DebugLog.WriteException("SkiaPrintInterop.ToPrinterBitmap", ex);
            return null;
        }
    }
}
