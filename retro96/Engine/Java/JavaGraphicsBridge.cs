using Retro96.Drawing;

namespace Retro96.Engine.Java;

internal static class JavaGraphicsBridge
{
    public static void Register(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "create", "()Ljava/awt/Graphics;", i => Create(vm, i));
        vm.RegisterNative(c.Name, "create", "(IIII)Ljava/awt/Graphics;", i => Create(vm, i));
        // FIX: only derived Graphics objects restore a saved state (the old code
        // restored state 0 on the HOST graphics, wiping applet translation).
        vm.RegisterNative(c.Name, "dispose", "()V", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is JavaGraphicsState gs && gs.IsDerived)
            {
                try { gs.Graphics.Restore(gs.SavedState); } catch { }
                gs.IsDerived = false;
            }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "translate", "(II)V", i => { var s = State(i); s.Graphics.TranslateTransform(i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); s.OriginX += i.Arguments[0].AsInt(); s.OriginY += i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "setColor", "(Ljava/awt/Color;)V", i => { State(i).Color = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.Black); return JValue.Void; });
        vm.RegisterNative(c.Name, "getColor", "()Ljava/awt/Color;", i => JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Color"), NativeState = State(i).Color }));
        vm.RegisterNative(c.Name, "setFont", "(Ljava/awt/Font;)V", i => { var s = State(i); s.Font = ToFont(i.Arguments[0].AsObject()); return JValue.Void; });
        vm.RegisterNative(c.Name, "getFont", "()Ljava/awt/Font;", i =>
        {
            var f = State(i).Font;
            if (f == null) return JValue.Ref(null);
            int st = (f.Style & FontStyle.Bold) != 0 ? 1 : 0;
            if ((f.Style & FontStyle.Italic) != 0) st |= 2;
            var o = new JObject { Class = vm.LoadClass("java.awt.Font"), NativeState = new JavaFontState { Family = f.FontFamily.Name, Size = (int)f.Size, Style = st } };
            return JValue.Ref(o);
        });
        vm.RegisterNative(c.Name, "getFontMetrics", "()Ljava/awt/FontMetrics;", i => { var o = new JObject { Class = vm.LoadClass("java.awt.FontMetrics"), NativeState = new JavaFontMetricsState { Font = State(i).Font } }; return JValue.Ref(o); });
        vm.RegisterNative(c.Name, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;", i => { var o = new JObject { Class = vm.LoadClass("java.awt.FontMetrics"), NativeState = new JavaFontMetricsState { Font = ToFont(i.Arguments[0].AsObject()) } }; return JValue.Ref(o); });
        vm.RegisterNative(c.Name, "setPaintMode", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "setXORMode", "(Ljava/awt/Color;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getClipBounds", "()Ljava/awt/Rectangle;", i => { var s = State(i); var o = new JObject { Class = vm.LoadClass("java.awt.Rectangle"), NativeState = new JRectState(0, 0, s.Width, s.Height) }; return JValue.Ref(o); });
        vm.RegisterNative(c.Name, "hitClip", "(IIII)Z", _ => JValue.Int(1));
        // FIX: clearRect fills with the surface background (component bg), not the
        // current drawing color — applets use it to erase before redraw.
        vm.RegisterNative(c.Name, "clearRect", "(IIII)V", i =>
        {
            var st = State(i);
            using var b = new SolidBrush(st.Background);
            st.Graphics.FillRectangle(b, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "setClip", "(IIII)V", i => { var s = State(i); s.Graphics.SetClip(new RectangleF(i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt())); return JValue.Void; });
        vm.RegisterNative(c.Name, "clipRect", "(IIII)V", i => { var s = State(i); s.Graphics.SetClip(new RectangleF(i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()), Retro96.Drawing.CombineMode.Intersect); return JValue.Void; });
        vm.RegisterNative(c.Name, "copyArea", "(IIIIII)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "drawLine", "(IIII)V", i =>
        {
            var s = State(i);
            using var p = new Pen(s.Color, 1);
            s.Graphics.DrawLine(p, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            return JValue.Void;
        });
        foreach (var n in new[] { "drawRect", "fillRect", "drawOval", "fillOval" })
        {
            string name = n;
            vm.RegisterNative(c.Name, name, "(IIII)V", i =>
            {
                var s = State(i);
                int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
                if (name.StartsWith("fill", StringComparison.Ordinal))
                {
                    using var b = new SolidBrush(s.Color);
                    if (name == "fillRect") s.Graphics.FillRectangle(b, x, y, w, h);
                    else s.Graphics.FillEllipse(b, x, y, w, h);
                }
                else
                {
                    using var p = new Pen(s.Color, 1);
                    if (name == "drawRect") s.Graphics.DrawRectangle(p, x, y, w, h);
                    else s.Graphics.DrawEllipse(p, x, y, w, h);
                }
                return JValue.Void;
            });
        }
        // Rounded rects approximated as plain rects (no path API on the wrapper).
        vm.RegisterNative(c.Name, "drawRoundRect", "(IIIIII)V", i =>
        {
            var s = State(i);
            using var p = new Pen(s.Color, 1);
            s.Graphics.DrawRectangle(p, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "fillRoundRect", "(IIIIII)V", i =>
        {
            var s = State(i);
            using var b = new SolidBrush(s.Color);
            s.Graphics.FillRectangle(b, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "draw3DRect", "(IIIIIZ)V", i => Rect3D(i, false));
        vm.RegisterNative(c.Name, "fill3DRect", "(IIIIIZ)V", i => Rect3D(i, true));
        // FIX: never dispose a cached state font via `using` (old drawString
        // disposed s.Font on the first call, corrupting every later one).
        vm.RegisterNative(c.Name, "drawString", "(Ljava/lang/String;II)V", i =>
        {
            var s = State(i);
            Font? f = s.Font;
            bool owns = f == null;
            if (f == null) { try { f = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular, GraphicsUnit.Pixel); } catch { } }
            try
            {
                if (f != null)
                {
                    using var b = new SolidBrush(s.Color);
                    s.Graphics.DrawString(vm.StringValue(i.Arguments[0]), f, b, i.Arguments[1].AsInt(), i.Arguments[2].AsInt());
                }
            }
            finally { if (owns) f?.Dispose(); }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "drawChars", "([CIIIII)V", i =>
        {
            var a = i.Arguments[0].AsArray() ?? throw new InvalidOperationException("char[] expected");
            string t = new string(a.Elements.Skip(i.Arguments[1].AsInt()).Take(i.Arguments[2].AsInt()).Select(v => (char)v.AsInt()).ToArray());
            return vm.InvokeVirtual(i.Receiver.AsObject()!, "drawString", "(Ljava/lang/String;II)V", JValue.Ref(vm.CreateString(t)), i.Arguments[3], i.Arguments[4]);
        });
        vm.RegisterNative(c.Name, "drawBytes", "([BIIIII)V", i =>
        {
            var a = i.Arguments[0].AsArray() ?? throw new InvalidOperationException("byte[] expected");
            string t = new string(a.Elements.Skip(i.Arguments[1].AsInt()).Take(i.Arguments[2].AsInt()).Select(v => (char)(byte)v.AsInt()).ToArray());
            return vm.InvokeVirtual(i.Receiver.AsObject()!, "drawString", "(Ljava/lang/String;II)V", JValue.Ref(vm.CreateString(t)), i.Arguments[3], i.Arguments[4]);
        });
        vm.RegisterNative(c.Name, "drawPolygon", "([I[II)V", i => Polygon(i, false));
        vm.RegisterNative(c.Name, "fillPolygon", "([I[II)V", i => Polygon(i, true));
        vm.RegisterNative(c.Name, "drawArc", "(IIIIII)V", i => Arc(i, false));
        vm.RegisterNative(c.Name, "fillArc", "(IIIIII)V", i => Arc(i, true));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z", i => DrawImage(vm, i, 0));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IILjava/lang/Object;)Z", i => DrawImage(vm, i, 0));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/image/ImageObserver;)Z", i => DrawImageScaled(vm, i, null));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/lang/Object;)Z", i => DrawImageScaled(vm, i, null));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/awt/image/ImageObserver;)Z", i => DrawImageScaled(vm, i, i.Arguments[5].AsObject()?.NativeState as Color?));
        vm.RegisterNative(c.Name, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/lang/Object;)Z", i => DrawImageScaled(vm, i, i.Arguments[5].AsObject()?.NativeState as Color?));
    }

    private static JavaGraphicsState State(JavaInvocation i) => i.Receiver.AsObject()?.NativeState as JavaGraphicsState ?? throw new InvalidOperationException("not graphics");

    private static JValue Create(JavaVm vm, JavaInvocation i)
    {
        var s = State(i);
        int save = s.Graphics.Save();
        int w = s.Width, h = s.Height, dx = 0, dy = 0;
        if (i.Arguments.Length == 4)
        {
            dx = i.Arguments[0].AsInt(); dy = i.Arguments[1].AsInt();
            w = i.Arguments[2].AsInt(); h = i.Arguments[3].AsInt();
            s.Graphics.TranslateTransform(dx, dy);
            s.Graphics.SetClip(new RectangleF(0, 0, Math.Max(1, w), Math.Max(1, h)));
        }
        return JValue.Ref(vm.GraphicsFactory.CreateGraphics(s.Graphics, w, h, s.OriginX + dx, s.OriginY + dy, save, derived: true));
    }

    private static Font? ToFont(JObject? o)
    {
        var st = o?.NativeState as JavaFontState;
        if (st == null) return null;
        try
        {
            var family = st.Family is "Dialog" or "SansSerif" or "Helvetica" ? FontFamily.GenericSansSerif.Name
                : st.Family is "Serif" or "TimesRoman" ? FontFamily.GenericSerif.Name
                : st.Family is "Monospaced" or "Courier" ? FontFamily.GenericMonospace.Name
                : st.Family;
            var style = (st.Style & 1) != 0 ? FontStyle.Bold : FontStyle.Regular;
            if ((st.Style & 2) != 0) style |= FontStyle.Italic;
            return new Font(new FontFamily(family), Math.Max(1, st.Size), style, GraphicsUnit.Pixel);
        }
        catch { return null; } // FIX: unknown family names used to throw and kill the applet
    }

    private static JValue Rect3D(JavaInvocation i, bool fill)
    {
        var s = State(i);
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        bool raised = i.Arguments[4].AsInt() != 0;
        var bright = raised ? Scale(s.Color, 1.4) : Scale(s.Color, 0.6);
        var dark = raised ? Scale(s.Color, 0.6) : Scale(s.Color, 1.4);
        if (fill) { using var b = new SolidBrush(s.Color); s.Graphics.FillRectangle(b, x, y, w, h); }
        using (var p = new Pen(bright, 1))
        {
            s.Graphics.DrawLine(p, x, y, x + w - 1, y);
            s.Graphics.DrawLine(p, x, y, x, y + h - 1);
        }
        using (var p = new Pen(dark, 1))
        {
            s.Graphics.DrawLine(p, x, y + h - 1, x + w - 1, y + h - 1);
            s.Graphics.DrawLine(p, x + w - 1, y, x + w - 1, y + h - 1);
        }
        return JValue.Void;
    }

    private static Color Scale(Color c, double f) =>
        Color.FromArgb(c.A, (int)Math.Clamp(c.R * f, 0, 255), (int)Math.Clamp(c.G * f, 0, 255), (int)Math.Clamp(c.B * f, 0, 255));

    private static JValue Polygon(JavaInvocation i, bool fill)
    {
        var s = State(i);
        var xs = i.Arguments[0].AsArray()!;
        var ys = i.Arguments[1].AsArray()!;
        int n = i.Arguments[2].AsInt();
        var pts = new PointF[Math.Min(n, Math.Min(xs.Elements.Length, ys.Elements.Length))];
        for (int k = 0; k < pts.Length; k++) pts[k] = new PointF(xs.Elements[k].AsInt(), ys.Elements[k].AsInt());
        if (fill) { using var b = new SolidBrush(s.Color); s.Graphics.FillPolygon(b, pts); }
        else { using var p = new Pen(s.Color, 1); s.Graphics.DrawPolygon(p, pts); }
        return JValue.Void;
    }

    private static JValue Arc(JavaInvocation i, bool fill)
    {
        var s = State(i);
        int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt(), w = i.Arguments[2].AsInt(), h = i.Arguments[3].AsInt();
        float sa = i.Arguments[4].AsInt(), sw = i.Arguments[5].AsInt();
        if (fill) { using var b = new SolidBrush(s.Color); s.Graphics.FillPie(b, x, y, w, h, sa, sw); }
        else { using var p = new Pen(s.Color, 1); s.Graphics.DrawArc(p, x, y, w, h, sa, sw); }
        return JValue.Void;
    }

    private static JValue DrawImage(JavaVm vm, JavaInvocation i, int _)
    {
        var s = State(i);
        var img = i.Arguments[0].AsObject()?.NativeState as JavaImageState;
        if (img == null) return JValue.Int(0);
        EnsureImageLoaded(vm, img); // async-load retry: image may have finished fetching since last paint
        if (!img.Loaded) return JValue.Int(0);
        s.Graphics.DrawImage(img.Bitmap, i.Arguments[1].AsInt(), i.Arguments[2].AsInt());
        return JValue.Int(1);
    }

    private static JValue DrawImageScaled(JavaVm vm, JavaInvocation i, Color? bg)
    {
        var s = State(i);
        var img = i.Arguments[0].AsObject()?.NativeState as JavaImageState;
        if (img == null) return JValue.Int(0);
        EnsureImageLoaded(vm, img);
        if (!img.Loaded) return JValue.Int(0);
        int x = i.Arguments[1].AsInt(), y = i.Arguments[2].AsInt(), w = i.Arguments[3].AsInt(), h = i.Arguments[4].AsInt();
        if (bg != null) { using var b = new SolidBrush(bg.Value); s.Graphics.FillRectangle(b, x, y, w, h); }
        s.Graphics.DrawImage(img.Bitmap, x, y, w, h);
        return JValue.Int(1);
    }

    /// <summary>Retry decoding a lazily-loaded image (the host fetch runs async and
    /// caches bytes; this picks them up on the next paint).</summary>
    internal static void EnsureImageLoaded(JavaVm vm, JavaImageState st)
    {
        if (st.Loaded) return;
        if (st.SourceUrl == null) { st.Loaded = true; return; }
        try
        {
            var bytes = vm.ResolveResourceBytes(st.SourceUrl);
            if (bytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(bytes);
                var image = Image.FromStream(ms);
                var old = st.Bitmap;
                st.Bitmap = new Bitmap(image);
                st.Loaded = true;
                image.Dispose();
                old.Dispose();
            }
        }
        catch { }
    }
}