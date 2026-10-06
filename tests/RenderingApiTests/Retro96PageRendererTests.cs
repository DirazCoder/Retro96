using SkiaSharp;
using Retro96.Rendering;

namespace RenderingApiTests;

public sealed class Retro96PageRendererTests
{
    [Fact]
    public void LoadsAndPaintsHtmlOntoCallerCanvas()
    {
        const int width = 320;
        const int height = 200;
        using var surface = SKSurface.Create(new SKImageInfo(width, height))
            ?? throw new InvalidOperationException("Could not create test Skia surface.");
        using var renderer = new Retro96PageRenderer();

        renderer.LoadHtml(
            "<html><body bgcolor='#ffffff'><h1>Retro96 DLL</h1><p>Rendered page</p></body></html>",
            width, height);
        renderer.RenderToCanvas(surface.Canvas, width, height);

        Assert.NotNull(renderer.Document);
        Assert.NotNull(renderer.Layout);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image)
            ?? throw new InvalidOperationException("Could not read rendered Skia surface.");
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.True(bitmap.GetPixel(10, 10).Alpha > 0);
    }

    [Fact]
    public void RequiresHtmlToBeLoadedBeforePainting()
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))
            ?? throw new InvalidOperationException("Could not create test Skia surface.");
        using var renderer = new Retro96PageRenderer();

        Assert.Throws<InvalidOperationException>(
            () => renderer.RenderToCanvas(surface.Canvas, 64, 64));
    }

    [Fact]
    public void PaintsPlaceholderTextInAnEmptyInput()
    {
        const int width = 320;
        const int height = 100;
        using var surface = SKSurface.Create(new SKImageInfo(width, height))
            ?? throw new InvalidOperationException("Could not create test Skia surface.");
        using var renderer = new Retro96PageRenderer();

        renderer.LoadHtml(
            "<html><head><style>body{margin:0} input{width:220px;height:30px}</style></head>" +
            "<body><input id='field' placeholder='Click to focus me'></body></html>",
            width, height);
        renderer.RenderToCanvas(surface.Canvas, width, height);

        var input = Assert.Single(renderer.Document!.ElementDescendants(),
            element => element.GetAttr("id") == "field");
        var face = input.LayoutBox!.ContentRect;
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image)
            ?? throw new InvalidOperationException("Could not read rendered Skia surface.");

        int grayTextPixels = 0;
        for (int y = Math.Max(0, (int)MathF.Ceiling(face.Top + 4));
             y < Math.Min(bitmap.Height, (int)MathF.Floor(face.Bottom - 4)); y++)
        {
            for (int x = Math.Max(0, (int)MathF.Ceiling(face.Left + 4));
                 x < Math.Min(bitmap.Width, (int)MathF.Floor(face.Right - 4)); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red == pixel.Green && pixel.Green == pixel.Blue &&
                    pixel.Red is > 80 and < 240)
                    grayTextPixels++;
            }
        }

        Assert.True(grayTextPixels > 0, "Expected grey placeholder glyphs inside the empty input.");
    }
}
