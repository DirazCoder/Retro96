# Embedding the Retro96 renderer

`retro96/Retro96.Rendering/Retro96.Rendering.csproj` builds
`Retro96.Rendering.dll`, a managed API for laying out HTML and drawing it onto
a caller-owned Skia canvas. It is separate from the browser window and does
not change the existing Retro96 application architecture.

## Add the project

Add a project reference to `Retro96.Rendering.csproj` from a Windows .NET
application targeting `net11.0-windows10.0.19041.0` or compatible. The renderer
uses SkiaSharp; the project reference supplies its managed dependencies and
the underlying `Retro96.dll` engine assembly.

## Render onto a Skia surface

```csharp
using SkiaSharp;
using Retro96.Rendering;

using var page = new Retro96PageRenderer();
page.LoadHtml(
    "<html><body><h1>Hello from Retro96</h1></body></html>",
    viewportWidth: 800,
    viewportHeight: 600,
    baseUrl: "https://example.com/");

using var surface = SKSurface.Create(new SKImageInfo(800, 600))
    ?? throw new InvalidOperationException("Could not create Skia surface.");

page.RenderToCanvas(surface.Canvas, 800, 600);
surface.Canvas.Flush();
```

Pass a canvas backed by the host's raster or GPU surface to draw into that
surface directly. `RenderToCanvas` does not take ownership of the canvas.
`LoadHtml` parses, styles, and lays out the provided HTML; `RenderToCanvas`
paints the current layout. The optional base URL resolves relative resource
URLs. When images finish loading asynchronously, the page raises `Invalidated`
so the host can schedule another paint on its UI/render thread.

This initial API renders static HTML and CSS. It does not execute page scripts
or fetch external stylesheets. Engine settings use the existing process-wide
`BrowserRuntime` configuration; passing `UserSettings` to the renderer updates
that shared configuration.
