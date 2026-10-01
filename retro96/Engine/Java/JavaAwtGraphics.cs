using SkiaSharp;
using Retro96.Drawing;

namespace Retro96.Engine.Java;

// Per-Graphics-object state: drawing surface, current color/font, XOR mode,
// tracked clip and translation. Disposed graphics fail gracefully.
public sealed class JavaGraphicsState
{
    public Retro96.Drawing.Graphics Graphics { get; }
    public int Width { get; }
    public int Height { get; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public Color Color { get; set; } = Color.Black;
    public Color Background { get; set; } = Color.LightGray;
    public Font? Font { get; set; }
    public int SavedState { get; set; }
    // True when produced by Graphics.create(); only those restore the saved
    // canvas state on dispose().
    public bool IsDerived { get; set; }
    public bool Disposed { get; set; }
    public GRContext? GpuContext { get; set; }
    // Non-null in XOR mode; each pixel becomes dst ^ xorColor ^ drawColor.
    public Color? XorColor { get; set; }
    // Tracked clip in DEVICE coordinates (null = whole surface).
    public SKRect? ClipRect { get; set; }

    public JavaGraphicsState(Retro96.Drawing.Graphics graphics, int width, int height, int originX, int originY)
    {
        Graphics = graphics;
        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
    }
}

// Image state: lazily decoded bitmap plus observers awaiting load events.
public sealed class JavaImageState
{
    public Bitmap Bitmap { get; set; }
    public bool Loaded { get; set; }
    public string? SourceUrl { get; set; }
    public bool Error { get; set; }
    public readonly List<(JObject Observer, JObject Image)> Observers = new();

    public JavaImageState(Bitmap bitmap)
    {
        Bitmap = bitmap;
        Loaded = true;
    }
}

public sealed class JavaGraphicsFactory
{
    private readonly JavaVm _vm;
    public JavaGraphicsFactory(JavaVm vm) => _vm = vm;

    public JObject CreateGraphics(Retro96.Drawing.Graphics g, int width, int height, int originX = 0, int originY = 0, int savedState = 0, bool derived = false, GRContext? gpuContext = null)
    {
        var cls = _vm.LoadClass("java.awt.Graphics");
        var o = _vm.NewObject(cls);
        o.NativeState = new JavaGraphicsState(g, width, height, originX, originY)
        {
            SavedState = savedState,
            IsDerived = derived,
            GpuContext = gpuContext
        };
        return o;
    }

    public JObject CreateImage(int width, int height)
    {
        var cls = _vm.LoadClass("java.awt.Image");
        var o = _vm.NewObject(cls);
        o.NativeState = new JavaImageState(new Bitmap(Math.Max(1, width), Math.Max(1, height)));
        return o;
    }

    public JObject CreateFontMetrics(Font? font)
    {
        var cls = _vm.LoadClass("java.awt.FontMetrics");
        var o = _vm.NewObject(cls);
        o.NativeState = new JavaFontMetricsState { Font = font };
        return o;
    }

    public JObject LoadImage(string absoluteUrl)
    {
        var cls = _vm.LoadClass("java.awt.Image");
        var o = _vm.NewObject(cls);
        // Lazily loaded: starts as a 1x1 placeholder and reports -1 for
        // getWidth/getHeight until the bytes arrive; drawImage retries the
        // decode on every paint.
        var state = new JavaImageState(new Bitmap(1, 1)) { SourceUrl = absoluteUrl, Loaded = false };
        o.NativeState = state;
        JavaAwtGraphics.EnsureImageLoaded(_vm, state);
        return o;
    }
}

// Natives for java.awt.Graphics: every draw/fill/clip/transform/image op.
internal static class JavaAwtGraphics
{
    public static void Register(JavaVm vm, JClass c)
    {
        // Graphics natives belong ONLY to java.awt.Graphics; registering
        // them for every builtin class would hand java.lang.String a
        // drawLine native and bloat the registry several-hundred-fold.
        if (c.Name != "java.awt.Graphics") return;
        vm.RegisterNative(c.Name, "create", "()Ljava/awt/Graphics;", i => Create(vm, i));
        vm.RegisterNative(c.Name, "create", "(IIII)Ljava/awt/Graphics;", i => Create(vm, i));
        vm.RegisterNative(c.Name, "dispose", "()V", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is JavaGraphicsState gs && !gs.Disposed)
            {
                if (gs.IsDerived)
                {
                    try { gs.Graphics.Restore(gs.SavedState); } catch { }
                    gs.IsDerived = false;
                }
                gs.Disposed = true;
            }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "translate", "(II)V", i =>
        {
            var s = State(i);
            if (s == null || s.Disposed) return JValue.Void;
            int dx = i.Arguments[0].AsInt(), dy = i.Arguments[1].AsInt();
            s.Graphics.TranslateTransform(dx, dy);
            // Cumulative: every later draw uses the shifted origin.
            s.OriginX += dx;
            s.OriginY += dy;
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "setColor", "(Ljava/awt/Color;)V", i =>
        {
            var s = State(i);
            if (s != null) s.Color = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.Black);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getColor", "()Ljava/awt/Color;", i =>
        {
            var s = State(i);
            return JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Color"), NativeState = s?.Color ?? Color.Black });
        });
        vm.RegisterNative(c.Name, "setFont", "(Ljava/awt/Font;)V", i =>
        {
            var s = State(i);
            if (s != null) s.Font = JavaAwt.ToFont(JavaAwt.FontState(i.Arguments[0].AsObject()));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getFont", "()Ljava/awt/Font;", i =>
        {
            var s = State(i);
            var f = s?.Font;
            if (f == null) return JValue.Ref(null);
            int st = (f.Style & FontStyle.Bold) != 0 ? 1 : 0;
            if ((f.Style & FontStyle.Italic) != 0) st |= 2;
            var o = new JObject { Class = vm.LoadClass("java.awt.Font"), NativeState = new JavaFontState { Family = f.FontFamily.Name, Size = (int)f.Size, Style = st } };
            return JValue.Ref(o);
        });
        vm.RegisterNative(c.Name, "getFontMetrics", "()Ljava/awt/FontMetrics;", i =>
            JValue.Ref(vm.GraphicsFactory.CreateFontMetrics(State(i)?.Font ?? DefaultFont())));
        vm.RegisterNative(c.Name, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;", i =>
            JValue.Ref(vm.GraphicsFactory.CreateFontMetrics(JavaAwt.ToFont(JavaAwt.FontState(i.Arguments[0].AsObject())))));
        vm.RegisterNative(c.Name, "setPaintMode", "()V", i => { var s = State(i); if (s != null) s.XorColor = null; return JValue.Void; });
        vm.RegisterNative(c.Name, "setXORMode", "(Ljava/awt/Color;)V", i =>
        {
            var s = State(i);
            if (s != null) s.XorColor = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.Black);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getClipBounds", "()Ljava/awt/Rectangle;", i => ClipBounds(vm, i));
        vm.RegisterNative(c.Name, "getClip", "()Ljava/awt/Shape;", i => ClipBounds(vm, i));
        vm.RegisterNative(c.Name, "setClip", "(IIII)V", i => SetClip(i, replace: true));
        vm.RegisterNative(c.Name, "setClip", "(Ljava/awt/Shape;)V", i => SetClipShape(vm, i));
        vm.RegisterNative(c.Name, "clipRect", "(IIII)V", i => SetClip(i, replace: false));
        vm.RegisterNative(c.Name, "hitClip", "(IIII)Z", i =>
        {
            var s = State(i);
            if (s == null) return JValue.Int(0);
            var probe = SKRect.Create(i.Arguments[0].AsInt() + s.OriginX, i.Arguments[1].AsInt() + s.OriginY,
                i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            var clip = s.ClipRect ?? SurfaceRect(s);
            return JValue.Int(clip.IntersectsWith(probe) && !probe.IsEmpty ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "copyArea", "(IIIIII)V", i => CopyArea(i));
        vm.RegisterNative(c.Name, "clearRect", "(IIII)V", i =>
        {
            // clearRect erases with the component background, not the
            // current drawing color.
            var s = State(i);
            if (s == null || s.Disposed) return JValue.Void;
            using var paint = FillPaint(s, s.Background);
            s.Graphics.Canvas.DrawRect(SKRect.Create(i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()), paint);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "drawLine", "(IIII)V", i =>
        {
            var s = State(i);
            if (s == null || s.Disposed) return JValue.Void;
            using var paint = StrokePaint(s, s.Color);
            s.Graphics.Canvas.DrawLine(i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt(), paint);
            return JValue.Void;
        });
        foreach (var n in new[] { "drawRect", "fillRect", "drawOval", "fillOval", "drawRoundRect", "fillRoundRect" })
        {
            string name = n;
            string desc = n.Contains("Round") ? "(IIIIII)V" : "(IIII)V";
            vm.RegisterNative(c.Name, name, desc, i => Shape(vm, i, name));
        }
        vm.RegisterNative(c.Name, "draw3DRect", "(IIIIIZ)V", i => Rect3D(vm, i, false));
        vm.RegisterNative(c.Name, "fill3DRect", "(IIIIIZ)V", i => Rect3D(vm, i, true));
        vm.RegisterNative(c.Name, "drawString", "(Ljava/lang/String;II)V", i =>
        {
            var s = State(i);
            if (s == null || s.Disposed) return JValue.Void;
            DrawString(vm, s, vm.StringValue(i.Arguments[0]), i.Arguments[1].AsInt(), i.Arguments[2].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "drawChars", "([CIIII)V", i =>
        {
            var s = State(i);
            if (s == null || s.Disposed) return JValue.Void;
            var text = SubChars(i.Arguments[0].AsArray(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt());
            // y is the baseline, not the top.
            DrawString(vm, s, text, i.Arguments[3].AsInt(), i.Arguments[4].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "drawBytes", "([BIIII)V", i =>
        {
            var s = State(i);
            if (s == null || s.Disposed) return JValue.Void;
            var a = i.Arguments[0].AsArray();
            int off = i.Arguments[1].AsInt(), len = i.Arguments[2].AsInt();
            // Bytes are treated as char values (low 8 bits).
            string text = a == null || off < 0 || len < 0 || off + len > a.Elements.Length
                ? ""
                : new string(a.Elements.Skip(off).Take(len).Select(v => (char)(v.AsInt() & 0xFF)).ToArray());
            DrawString(vm, s, text, i.Arguments[3].AsInt(), i.Arguments[4].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "drawPolygon", "([I[II)V", i => Polygon(vm, i, fill: false, close: true));
        vm.RegisterNative(c.Name, "fillPolygon", "([I[II)V", i => Polygon(vm, i, fill: true, close: false));
        vm.RegisterNative(c.Name, "drawPolyline", "([I[II)V", i => Polygon(vm, i, fill: false, close: false));
        vm.RegisterNative(c.Name, "drawPolygon", "(Ljava/awt/Polygon;)V", i => PolygonObject(vm, i, fill: false, close: true));
        vm.RegisterNative(c.Name, "fillPolygon", "(Ljava/awt/Polygon;)V", i => PolygonObject(vm, i, fill: true, close: false));
        vm.RegisterNative(c.Name, "drawArc", "(IIIIII)V", i => Arc(vm, i, fill: false));
        vm.RegisterNative(c.Name, "fillArc", "(IIIIII)V", i => Arc(vm, i, fill: true));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z", i => DrawImage(vm, i, ImageForm.Simple));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IILjava/lang/Object;)Z", i => DrawImage(vm, i, ImageForm.Simple));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/image/ImageObserver;)Z", i => DrawImage(vm, i, ImageForm.Scaled));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/lang/Object;)Z", i => DrawImage(vm, i, ImageForm.Scaled));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIILjava/awt/Color;Ljava/awt/image/ImageObserver;)Z", i => DrawImage(vm, i, ImageForm.BgSimple));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIILjava/awt/Color;Ljava/lang/Object;)Z", i => DrawImage(vm, i, ImageForm.BgSimple));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/awt/image/ImageObserver;)Z", i => DrawImage(vm, i, ImageForm.BgScaled));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/lang/Object;)Z", i => DrawImage(vm, i, ImageForm.BgScaled));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIIIIIILjava/awt/image/ImageObserver;)Z", i => DrawImage(vm, i, ImageForm.Subimage));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIIIIIILjava/lang/Object;)Z", i => DrawImage(vm, i, ImageForm.Subimage));
    }

    private static JavaGraphicsState? State(JavaInvocation i) => i.Receiver.AsObject()?.NativeState as JavaGraphicsState;

    private static SKRect SurfaceRect(JavaGraphicsState s) => SKRect.Create(0, 0, Math.Max(1, s.Width), Math.Max(1, s.Height));

    private static Font? ResolveFont(JavaGraphicsState s) => s.Font ?? DefaultFont();

    private static Font? DefaultFont()
    {
        try { return new Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { return null; }
    }

    // Paint helpers. XOR mode composites with dst ^ (drawColor ^ xorColor),
    // which is Java's pixel rule and restores on the second identical draw.

    private static SKPaint StrokePaint(JavaGraphicsState s, Color color)
    {
        var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = false };
        ApplyColor(paint, s, color);
        return paint;
    }

    private static SKPaint FillPaint(JavaGraphicsState s, Color color)
    {
        var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        ApplyColor(paint, s, color);
        return paint;
    }

    private static void ApplyColor(SKPaint paint, JavaGraphicsState s, Color color)
    {
        if (s.XorColor is { } xor)
        {
            paint.Color = XorSkColor(color, xor);
            paint.BlendMode = SKBlendMode.Xor;
        }
        else
        {
            paint.Color = color.ToSkColor();
            paint.BlendMode = SKBlendMode.SrcOver;
        }
    }

    private static SKColor XorSkColor(Color draw, Color xor) => new(
        (byte)(draw.R ^ xor.R), (byte)(draw.G ^ xor.G), (byte)(draw.B ^ xor.B), 0xFF);

    private static JValue Create(JavaVm vm, JavaInvocation i)
    {
        var s = State(i);
        if (s == null) return JValue.Ref(null);
        int save = s.Graphics.Save();
        int w = s.Width, h = s.Height, dx = 0, dy = 0;
        if (i.Arguments.Length == 4)
        {
            dx = i.Arguments[0].AsInt();
            dy = i.Arguments[1].AsInt();
            w = i.Arguments[2].AsInt();
            h = i.Arguments[3].AsInt();
            s.Graphics.TranslateTransform(dx, dy);
            s.Graphics.SetClip(new RectangleF(0, 0, Math.Max(1, w), Math.Max(1, h)));
        }
        var copy = vm.GraphicsFactory.CreateGraphics(s.Graphics, w, h, s.OriginX + dx, s.OriginY + dy, save, derived: true);
        if (copy.NativeState is JavaGraphicsState gs)
        {
            gs.Color = s.Color;
            gs.Background = s.Background;
            gs.Font = s.Font;
            gs.XorColor = s.XorColor;
            gs.ClipRect = s.ClipRect;
        }
        return JValue.Ref(copy);
    }

    private static JValue Shape(JavaVm vm, JavaInvocation i, string name)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        bool fill = name.StartsWith("fill", StringComparison.Ordinal);
        var canvas = s.Graphics.Canvas;
        var rect = SKRect.Create(x, y, w, h);
        if (name.Contains("Round"))
        {
            float arcW = Math.Max(0, i.Arguments[4].AsInt()), arcH = Math.Max(0, i.Arguments[5].AsInt());
            using var builder = new SKPathBuilder();
            builder.AddRoundRect(rect, arcW / 2f, arcH / 2f);
            using var path = builder.Detach();
            using var paint = fill ? FillPaint(s, s.Color) : StrokePaint(s, s.Color);
            canvas.DrawPath(path, paint);
            return JValue.Void;
        }
        if (name.Contains("Oval"))
        {
            using var paint = fill ? FillPaint(s, s.Color) : StrokePaint(s, s.Color);
            canvas.DrawOval(rect, paint);
            return JValue.Void;
        }
        // Plain rectangle. drawRect with non-positive size draws nothing,
        // matching Java.
        if (!fill && (w <= 0 || h <= 0)) return JValue.Void;
        if (fill && (w <= 0 || h <= 0)) return JValue.Void;
        using var rectPaint = fill ? FillPaint(s, s.Color) : StrokePaint(s, s.Color);
        canvas.DrawRect(rect, rectPaint);
        return JValue.Void;
    }

    private static JValue Rect3D(JavaVm vm, JavaInvocation i, bool fill)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        bool raised = i.Arguments[4].AsInt() != 0;
        var canvas = s.Graphics.Canvas;
        var bright = raised ? Scale(s.Color, 1.4) : Scale(s.Color, 0.6);
        var dark = raised ? Scale(s.Color, 0.6) : Scale(s.Color, 1.4);
        if (fill)
        {
            using var b = FillPaint(s, s.Color);
            canvas.DrawRect(SKRect.Create(x, y, w, h), b);
        }
        using (var p = StrokePaint(s, bright))
        {
            canvas.DrawLine(x, y, x + w - 1, y, p);
            canvas.DrawLine(x, y, x, y + h - 1, p);
        }
        using (var p = StrokePaint(s, dark))
        {
            canvas.DrawLine(x, y + h - 1, x + w - 1, y + h - 1, p);
            canvas.DrawLine(x + w - 1, y, x + w - 1, y + h - 1, p);
        }
        return JValue.Void;
    }

    private static Color Scale(Color c, double f) =>
        Color.FromArgb(c.A, (int)Math.Clamp(c.R * f, 0, 255), (int)Math.Clamp(c.G * f, 0, 255), (int)Math.Clamp(c.B * f, 0, 255));

    private static void DrawString(JavaVm vm, JavaGraphicsState s, string text, int x, int y)
    {
        var font = ResolveFont(s);
        if (font == null || text.Length == 0) return;
        using var paint = FillPaint(s, s.Color);
        paint.IsAntialias = true;
        // Skia's DrawText places y at the baseline, exactly Java's contract.
        if (s.XorColor != null)
        {
            s.Graphics.Canvas.DrawText(text, x, y, SKTextAlign.Left, font.SkFont, paint);
        }
        else
        {
            // Route normal text through the wrapper so it benefits from the
            // shared edging/glyph handling; the wrapper wants the line-box
            // top, so offset by the ascent.
            using var brush = new SolidBrush(s.Color);
            s.Graphics.DrawString(text, font, brush, x, y - font.AscentPx);
        }
    }

    private static string SubChars(JArray? a, int off, int len)
    {
        if (a == null || off < 0 || len < 0 || off + len > a.Elements.Length) return "";
        var chars = new char[len];
        for (int k = 0; k < len; k++) chars[k] = (char)a.Elements[off + k].AsInt();
        return new string(chars);
    }

    private static JValue Polygon(JavaVm vm, JavaInvocation i, bool fill, bool close)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        var xs = i.Arguments[0].AsArray();
        var ys = i.Arguments[1].AsArray();
        int n = Math.Clamp(i.Arguments[2].AsInt(), 0, Math.Min(xs?.Elements.Length ?? 0, ys?.Elements.Length ?? 0));
        if (n == 0) return JValue.Void;
        using var builder = new SKPathBuilder();
        builder.MoveTo(xs!.Elements[0].AsInt(), ys!.Elements[0].AsInt());
        for (int k = 1; k < n; k++) builder.LineTo(xs.Elements[k].AsInt(), ys.Elements[k].AsInt());
        if (close) builder.Close();
        using var path = builder.Detach();
        using var paint = fill ? FillPaint(s, s.Color) : StrokePaint(s, s.Color);
        s.Graphics.Canvas.DrawPath(path, paint);
        return JValue.Void;
    }

    private static JValue PolygonObject(JavaVm vm, JavaInvocation i, bool fill, bool close)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        var poly = i.Arguments[0].AsObject();
        if (poly?.NativeState is not JavaPolygonState st) return JValue.Void;
        if (st.NPoints == 0) return JValue.Void;
        using var builder = new SKPathBuilder();
        builder.MoveTo(st.XPoints[0], st.YPoints[0]);
        for (int k = 1; k < st.NPoints; k++) builder.LineTo(st.XPoints[k], st.YPoints[k]);
        if (close) builder.Close();
        using var path = builder.Detach();
        using var paint = fill ? FillPaint(s, s.Color) : StrokePaint(s, s.Color);
        s.Graphics.Canvas.DrawPath(path, paint);
        return JValue.Void;
    }

    // Java measures arcs counterclockwise from 3 o'clock; legacy desktop APIs
    // sweep clockwise with y-down, so both angles are negated.
    private static JValue Arc(JavaVm vm, JavaInvocation i, bool fill)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        float start = i.Arguments[4].AsInt(), sweep = i.Arguments[5].AsInt();
        if (w <= 0 || h <= 0 || sweep == 0) return JValue.Void;
        var rect = SKRect.Create(x, y, w, h);
        float skiaStart = -start;
        float skiaSweep = -sweep;
        using var paint = fill ? FillPaint(s, s.Color) : StrokePaint(s, s.Color);
        if (fill)
        {
            // Pie semantics: connect the arc through the centre.
            var bounds = SKRect.Create(x, y, w, h);
            using var builder = new SKPathBuilder();
            builder.MoveTo(bounds.MidX, bounds.MidY);
            builder.ArcTo(bounds, skiaStart, skiaSweep, false);
            builder.Close();
            using var path = builder.Detach();
            s.Graphics.Canvas.DrawPath(path, paint);
        }
        else
        {
            s.Graphics.Canvas.DrawArc(rect, skiaStart, skiaSweep, false, paint);
        }
        return JValue.Void;
    }

    // Image drawing. Unloaded images register the observer and return
    // false; EnsureImageLoaded fires imageUpdate(ALLBITS) when bytes land.
    private enum ImageForm { Simple, Scaled, BgSimple, BgScaled, Subimage }

    private static JValue DrawImage(JavaVm vm, JavaInvocation i, ImageForm form)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Int(0);
        var img = i.Arguments[0].AsObject();
        if (img?.NativeState is not JavaImageState state) return JValue.Int(0);
        EnsureImageLoaded(vm, state);
        if (!state.Loaded)
        {
            RegisterObserver(state, img, ObserverArg(i));
            return JValue.Int(0); // not ready yet; imageUpdate will repaint
        }
        var canvas = s.Graphics.Canvas;
        int argIndex = 1;
        int x = i.Arguments[argIndex].AsInt(), y = i.Arguments[argIndex + 1].AsInt();
        Color? bg = null;
        int w, h;
        switch (form)
        {
            case ImageForm.Simple:
            case ImageForm.BgSimple:
            {
                w = state.Bitmap.Width;
                h = state.Bitmap.Height;
                if (form == ImageForm.BgSimple) bg = i.Arguments[3].AsObject()?.NativeState as Color?;
                break;
            }
            case ImageForm.Scaled:
            case ImageForm.BgScaled:
            {
                w = i.Arguments[argIndex + 2].AsInt();
                h = i.Arguments[argIndex + 3].AsInt();
                if (form == ImageForm.BgScaled) bg = i.Arguments[5].AsObject()?.NativeState as Color?;
                break;
            }
            default:
            {
                // Subimage copy with independent source/destination flips.
                int dx1 = i.Arguments[1].AsInt(), dy1 = i.Arguments[2].AsInt();
                int dx2 = i.Arguments[3].AsInt(), dy2 = i.Arguments[4].AsInt();
                int sx1 = i.Arguments[5].AsInt(), sy1 = i.Arguments[6].AsInt();
                int sx2 = i.Arguments[7].AsInt(), sy2 = i.Arguments[8].AsInt();
                DrawSubimage(s, state, dx1, dy1, dx2, dy2, sx1, sy1, sx2, sy2);
                return JValue.Int(1);
            }
        }
        if (bg != null && w > 0 && h > 0)
        {
            using var bgPaint = FillPaint(s, bg.Value);
            canvas.DrawRect(SKRect.Create(x, y, w, h), bgPaint);
        }
        if (w > 0 && h > 0)
        {
            using var paint = ImagePaint(s);
            canvas.DrawImage(state.Bitmap.GetGpuImage(s.GpuContext),
                SKRect.Create(x, y, w, h), SKSamplingOptions.Default, paint);
        }
        return JValue.Int(1);
    }

    private static void DrawSubimage(JavaGraphicsState s, JavaImageState state, int dx1, int dy1, int dx2, int dy2, int sx1, int sy1, int sx2, int sy2)
    {
        float dw = Math.Abs(dx2 - dx1), dh = Math.Abs(dy2 - dy1);
        float sw = Math.Abs(sx2 - sx1), sh = Math.Abs(sy2 - sy1);
        if (dw <= 0 || dh <= 0 || sw <= 0 || sh <= 0) return;
        bool mirrorX = (dx1 > dx2) ^ (sx1 > sx2);
        bool mirrorY = (dy1 > dy2) ^ (sy1 > sy2);
        var srcRect = SKRect.Create(Math.Min(sx1, sx2), Math.Min(sy1, sy2), sw, sh);
        var canvas = s.Graphics.Canvas;
        using var paint = ImagePaint(s);
        int save = canvas.Save();
        try
        {
            canvas.Translate(Math.Min(dx1, dx2), Math.Min(dy1, dy2));
            if (mirrorX) canvas.Scale(-1, 1);
            if (mirrorY) canvas.Scale(1, -1);
            if (mirrorX || mirrorY) canvas.Translate(mirrorX ? -dw : 0, mirrorY ? -dh : 0);
            canvas.DrawImage(state.Bitmap.GetGpuImage(s.GpuContext),
                srcRect, SKRect.Create(0, 0, dw, dh), SKSamplingOptions.Default, paint);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    private static SKPaint ImagePaint(JavaGraphicsState s)
    {
        var paint = new SKPaint { IsAntialias = false };
        if (s.XorColor is { } xor)
        {
            // Pixel rule dst ^ (pixel ^ xorColor) via a color filter plus the
            // XOR compositing mode.
            paint.ColorFilter = SKColorFilter.CreateBlendMode(xor.ToSkColor(), SKBlendMode.Xor);
            paint.BlendMode = SKBlendMode.Xor;
        }
        else
        {
            paint.BlendMode = SKBlendMode.SrcOver;
        }
        return paint;
    }

    private static JValue ObserverArg(JavaInvocation i) => i.Arguments[^1];

    private static void RegisterObserver(JavaImageState state, JObject image, JValue observer)
    {
        var obs = observer.AsObject();
        if (obs == null) return;
        lock (state.Observers)
        {
            foreach (var (existing, _) in state.Observers)
                if (ReferenceEquals(existing, obs)) return;
            state.Observers.Add((obs, image));
        }
    }

    // Retry decoding a lazily loaded image; fires imageUpdate(ALLBITS) on
    // the registered observers when the bytes arrive.
    internal static void EnsureImageLoaded(JavaVm vm, JavaImageState st)
    {
        if (st.Loaded) return;
        if (st.SourceUrl == null) { st.Loaded = true; return; }
        try
        {
            var bytes = vm.ResolveResourceBytes(st.SourceUrl);
            if (bytes is { Length: > 0 })
            {
                var decoded = SKImage.FromEncodedData(bytes);
                if (decoded != null)
                {
                    var old = st.Bitmap;
                    st.Bitmap = new Bitmap(decoded);
                    st.Loaded = true;
                    old.Dispose();
                }
                else
                {
                    st.Error = true;
                }
            }
        }
        catch
        {
            st.Error = true;
        }
        if (st.Loaded || st.Error) NotifyObservers(vm, st);
    }

    private static void NotifyObservers(JavaVm vm, JavaImageState st)
    {
        (JObject Observer, JObject Image)[] pending;
        lock (st.Observers) pending = st.Observers.ToArray();
        if (pending.Length == 0) return;
        const int widthFlag = 1, heightFlag = 2, allBits = 32, errorFlag = 64, abortFlag = 128;
        int flags = st.Error ? errorFlag | abortFlag : widthFlag | heightFlag | allBits;
        int w = st.Loaded ? st.Bitmap.Width : -1;
        int h = st.Loaded ? st.Bitmap.Height : -1;
        var keep = new List<JObject>();
        foreach (var (observer, image) in pending)
        {
            try
            {
                // Observers returning false stop receiving callbacks.
                bool more = vm.InvokeVirtual(observer, "imageUpdate",
                    "(Ljava/awt/Image;IIII)Z", JValue.Ref(image), JValue.Int(flags),
                    JValue.Int(-1), JValue.Int(-1), JValue.Int(w), JValue.Int(h)).AsInt() != 0;
                if (more) keep.Add(observer);
            }
            catch (Exception ex)
            {
                vm.Print("[AWT] imageUpdate: " + ex.Message + "\n");
            }
        }
        lock (st.Observers)
        {
            st.Observers.Clear();
            foreach (var observer in keep)
            {
                foreach (var (existing, image) in pending)
                    if (ReferenceEquals(existing, observer)) st.Observers.Add((observer, image));
            }
        }
    }

    // Clip tracking: the Java-level clip rectangle is tracked in device
    // coordinates; getClipBounds reports it in current logical coordinates.
    private static JValue ClipBounds(JavaVm vm, JavaInvocation i)
    {
        var s = State(i);
        var cls = vm.LoadClass("java.awt.Rectangle");
        var o = new JObject { Class = cls };
        var clip = s?.ClipRect;
        if (clip == null) return JValue.Ref(null);
        var logical = new SKRect(clip.Value.Left - s!.OriginX, clip.Value.Top - s.OriginY,
            clip.Value.Right - s.OriginX, clip.Value.Bottom - s.OriginY);
        vm.SetField(o, cls, "x", "I", JValue.Int((int)Math.Floor(logical.Left)));
        vm.SetField(o, cls, "y", "I", JValue.Int((int)Math.Floor(logical.Top)));
        vm.SetField(o, cls, "width", "I", JValue.Int((int)Math.Ceiling(logical.Width)));
        vm.SetField(o, cls, "height", "I", JValue.Int((int)Math.Ceiling(logical.Height)));
        o.NativeState = new JRectState((int)Math.Floor(logical.Left), (int)Math.Floor(logical.Top),
            (int)Math.Ceiling(logical.Width), (int)Math.Ceiling(logical.Height));
        return JValue.Ref(o);
    }

    private static JValue SetClip(JavaInvocation i, bool replace)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        var logical = SKRect.Create(x, y, w, h);
        var device = new SKRect(logical.Left + s.OriginX, logical.Top + s.OriginY,
            logical.Right + s.OriginX, logical.Bottom + s.OriginY);
        s.Graphics.SetClip(new RectangleF(x, y, w, h), replace ? CombineMode.Replace : CombineMode.Intersect);
        if (replace)
            s.ClipRect = device.IsEmpty ? null : device;
        else
        {
            var current = s.ClipRect ?? SurfaceRect(s);
            var intersection = SKRect.Intersect(current, device);
            s.ClipRect = intersection.IsEmpty ? SKRect.Create(0, 0, 0, 0) : intersection;
        }
        return JValue.Void;
    }

    private static JValue SetClipShape(JavaVm vm, JavaInvocation i)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        var shape = i.Arguments[0].AsObject();
        if (shape == null)
        {
            // setClip(null) restores the whole surface.
            s.Graphics.SetClip(new RectangleF(-100000, -100000, 200000, 200000), CombineMode.Replace);
            s.ClipRect = null;
            return JValue.Void;
        }
        // Resolve the shape's bounding rectangle (Rectangle or Polygon).
        int x = 0, y = 0, w = 0, h = 0;
        var rect = vm.InvokeVirtual(shape, "getBounds", "()Ljava/awt/Rectangle;");
        if (rect.AsObject() is { } rectObj)
        {
            var cls = vm.LoadClass("java.awt.Rectangle");
            x = vm.GetField(rectObj, cls, "x", "I").AsInt();
            y = vm.GetField(rectObj, cls, "y", "I").AsInt();
            w = vm.GetField(rectObj, cls, "width", "I").AsInt();
            h = vm.GetField(rectObj, cls, "height", "I").AsInt();
        }
        s.Graphics.SetClip(new RectangleF(x, y, w, h), CombineMode.Replace);
        s.ClipRect = SKRect.Create(x + s.OriginX, y + s.OriginY, w, h);
        return JValue.Void;
    }

    // copyArea blits a device-space region; used for scrolling.
    private static JValue CopyArea(JavaInvocation i)
    {
        var s = State(i);
        if (s == null || s.Disposed) return JValue.Void;
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt();
        int w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        int dx = i.Arguments[4].AsInt(), dy = i.Arguments[5].AsInt();
        if (w <= 0 || h <= 0) return JValue.Void;
        var canvas = s.Graphics.Canvas;
        var bmp = s.Graphics.Bitmap.SkBitmap;
        // Device coordinates: current translation offset.
        int ox = s.OriginX, oy = s.OriginY;
        var src = new SKRectI(x + ox, y + oy, x + ox + w, y + oy + h);
        var bounds = new SKRectI(0, 0, bmp.Width, bmp.Height);
        if (!src.IntersectsWithInclusive(bounds)) return JValue.Void;
        src.Left = Math.Max(src.Left, 0);
        src.Top = Math.Max(src.Top, 0);
        src.Right = Math.Min(src.Right, bmp.Width);
        src.Bottom = Math.Min(src.Bottom, bmp.Height);
        if (src.Width <= 0 || src.Height <= 0) return JValue.Void;
        var subset = new SKBitmap();
        if (!bmp.ExtractSubset(subset, src)) { subset.Dispose(); return JValue.Void; }
        int save = canvas.Save();
        try
        {
            // Reset the matrix so device pixels map 1:1; the clip survives
            // the save/restore pair.
            canvas.ResetMatrix();
            using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
            canvas.DrawBitmap(subset, src.Left + dx, src.Top + dy, SKSamplingOptions.Default, paint);
        }
        finally
        {
            canvas.RestoreToCount(save);
            subset.Dispose();
        }
        return JValue.Void;
    }
}

internal static class SkRectExtensions
{
    public static bool IntersectsWithInclusive(this SKRectI a, SKRectI b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;
}
