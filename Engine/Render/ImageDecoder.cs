using System.Drawing;
using System.Drawing.Imaging;

namespace Retro96.Engine.Render;

/// <summary>
/// Decodes image bytes into a DecodedImage (single or animated).
/// Supports JPEG, PNG, GIF87a/89a (animated), XBM.
/// Unknown formats return BrokenImageIcon.Create().
/// </summary>
public static class ImageDecoder
{
    /// <summary>
    /// Decode image bytes. Returns a DecodedImage with one or more frames.
    /// </summary>
    public static DecodedImage Decode(byte[] data, string contentType)
    {
        if (data == null || data.Length == 0)
            return new DecodedImage([BrokenImageIcon.Create()], [0], false);

        try
        {
            // Try to detect format from data
            if (IsGif(data))
            {
                return DecodeGif(data);
            }
            else if (IsXbm(data))
            {
                return DecodeXbm(data);
            }
            else
            {
                // Try JPEG/PNG via GDI+
                return DecodeStandard(data);
            }
        }
        catch
        {
            return new DecodedImage([BrokenImageIcon.Create()], [0], false);
        }
    }

    /// <summary>
    /// Check if data is a GIF (87a or 89a).
    /// </summary>
    private static bool IsGif(byte[] data)
    {
        return data.Length >= 6 &&
               data[0] == 'G' && data[1] == 'I' && data[2] == 'F' &&
               (data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a');
    }

    /// <summary>
    /// Check if data is XBM format.
    /// </summary>
    private static bool IsXbm(byte[] data)
    {
        // XBM files start with #define
        var start = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(20, data.Length));
        return start.Contains("#define");
    }

    /// <summary>
    /// Decode GIF (87a or 89a), including animated GIFs.
    /// </summary>
    private static DecodedImage DecodeGif(byte[] data)
    {
        var frames = new List<Bitmap>();
        var delays = new List<int>();

        using (var ms = new MemoryStream(data))
        {
            var image = Image.FromStream(ms);

            // Check if it's animated (GIF89a with multiple frames)
            var fd = new FrameDimension(image.FrameDimensionsList[0]);
            int frameCount = image.GetFrameCount(fd);

            for (int i = 0; i < frameCount; i++)
            {
                image.SelectActiveFrame(fd, i);
                frames.Add(new Bitmap(image));

                // Get delay from property
                int delayMs = 100; // Default 100ms
                if (image.PropertyIdList.Contains(0x5100)) // PropertyTagFrameDelay
                {
                    var prop = image.GetPropertyItem(0x5100);
                    if (prop != null && prop.Value != null && prop.Value.Length >= (i + 1) * 4)
                    {
                        int delay = BitConverter.ToInt16(prop.Value, i * 4);
                        if (delay > 0)
                            delayMs = delay * 10; // Convert to ms (GIF delay is in 1/100s)
                    }
                }
                delays.Add(delayMs);
            }

            return new DecodedImage(frames.AsReadOnly(), delays.AsReadOnly(), frameCount > 1);
        }
    }

    /// <summary>
    /// Decode XBM (X BitMap) format.
    /// Parses C header format: #define name_width N, #define name_height N, static unsigned char name_bits[] = { ... };
    /// </summary>
    private static DecodedImage DecodeXbm(byte[] data)
    {
        string text = System.Text.Encoding.ASCII.GetString(data);

        // Parse width
        int width = 0, height = 0;
        var widthMatch = System.Text.RegularExpressions.Regex.Match(text, @"#define\s+\w+_width\s+(\d+)");
        if (widthMatch.Success)
            width = int.Parse(widthMatch.Groups[1].Value);

        var heightMatch = System.Text.RegularExpressions.Regex.Match(text, @"#define\s+\w+_height\s+(\d+)");
        if (heightMatch.Success)
            height = int.Parse(heightMatch.Groups[1].Value);

        if (width == 0 || height == 0)
            return new DecodedImage([BrokenImageIcon.Create()], [0], false);

        // Parse the bitmap data
        var bitsMatch = System.Text.RegularExpressions.Regex.Match(text, @"\{([^}]+)\}");
        if (!bitsMatch.Success)
            return new DecodedImage([BrokenImageIcon.Create()], [0], false);

        string bitsStr = bitsMatch.Groups[1].Value;
        var bytes = new List<byte>();
        foreach (var token in bitsStr.Split(',', ' ', '\n', '\r', '\t'))
        {
            var trimmed = token.Trim();
            if (trimmed.StartsWith("0x") && byte.TryParse(trimmed.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                bytes.Add(b);
            else if (int.TryParse(trimmed, out int val) && val >= 0 && val <= 255)
                bytes.Add((byte)val);
        }

        // Create bitmap
        var bmp = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int byteIndex = (y * ((width + 7) / 8)) + (x / 8);
                if (byteIndex >= bytes.Count)
                    break;

                int bit = x % 8;
                bool pixelSet = (bytes[byteIndex] & (1 << bit)) != 0;

                bmp.SetPixel(x, y, pixelSet ? Color.Black : Color.Transparent);
            }
        }

        return new DecodedImage([bmp], [0], false);
    }

    /// <summary>
    /// Decode standard formats (JPEG, PNG) via GDI+.
    /// </summary>
    private static DecodedImage DecodeStandard(byte[] data)
    {
        using (var ms = new MemoryStream(data))
        {
            var image = Image.FromStream(ms);
            var bmp = new Bitmap(image);
            return new DecodedImage([bmp], [0], false);
        }
    }
}

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
/// Creates a 24x24 broken image icon (grey box with red X), matching Netscape's broken image icon.
/// </summary>
public static class BrokenImageIcon
{
    public static Bitmap Create()
    {
        var bmp = new Bitmap(24, 24);
        using (var g = Graphics.FromImage(bmp))
        {
            // Grey background
            g.FillRectangle(Brushes.LightGray, 0, 0, 24, 24);
            
            // Border
            g.DrawRectangle(Pens.Gray, 0, 0, 23, 23);
            
            // Red X
            using (var redPen = new Pen(Color.Red, 2))
            {
                g.DrawLine(redPen, 4, 4, 19, 19);
                g.DrawLine(redPen, 19, 4, 4, 19);
            }
        }
        return bmp;
    }
}
