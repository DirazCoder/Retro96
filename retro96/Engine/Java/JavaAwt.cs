using System.Globalization;
using Retro96.Drawing;
using Retro96.Engine.Network;

namespace Retro96.Engine.Java;

// Shared AWT state types.
public sealed class JavaFontState { public string Family = "Dialog"; public int Style; public int Size = 12; }
public sealed class JavaFontMetricsState { public Font? Font; }
public readonly record struct JRectState(int X, int Y, int Width, int Height);
public sealed class JavaPolygonState { public int[] XPoints = Array.Empty<int>(); public int[] YPoints = Array.Empty<int>(); public int NPoints; }

// Natives for java.awt support types: Color, Font, FontMetrics, Dimension,
// Point, Rectangle, Insets, Polygon, Toolkit and the Shape/ImageProducer
// placeholder interfaces.
internal static class JavaAwt
{
    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.awt.Color": RegisterColor(vm, c); break;
            case "java.awt.Font": RegisterFont(vm, c); break;
            case "java.awt.FontMetrics": RegisterFontMetrics(vm, c); break;
            case "java.awt.Dimension": RegisterDimension(vm, c); break;
            case "java.awt.Point": RegisterPoint(vm, c); break;
            case "java.awt.Rectangle": RegisterRectangle(vm, c); break;
            case "java.awt.Insets": JavaComponentBridge.RegisterInsets(vm, c); break;
            case "java.awt.Polygon": RegisterPolygon(vm, c); break;
            case "java.awt.Toolkit": RegisterToolkit(vm, c); break;
        }
    }

    private static Color ColorOf(JObject o) => o.NativeState is Color c ? c : Color.Black;

    private static JObject ColorObject(JavaVm vm, Color c)
    {
        var cls = vm.LoadClass("java.awt.Color");
        return new JObject { Class = cls, NativeState = c };
    }

    private static void RegisterColor(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(III)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(
                Math.Clamp(i.Arguments[0].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[1].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[2].AsInt(), 0, 255));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(I)V", i =>
        {
            int rgb = i.Arguments[0].AsInt();
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(unchecked((int)0xFF000000) | (rgb & 0x00FFFFFF));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(
                Math.Clamp(i.Arguments[3].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[0].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[1].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[2].AsInt(), 0, 255));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(IIIIZ)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(
                Math.Clamp(i.Arguments[3].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[0].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[1].AsInt(), 0, 255),
                Math.Clamp(i.Arguments[2].AsInt(), 0, 255));
            return JValue.Void;
        });
        // JDK 1.1 constant values, spelled out so host-side named colors
        // (whose RGB differs) can never leak through.
        var jBlack = Color.FromArgb(255, 0, 0, 0);
        var jWhite = Color.FromArgb(255, 255, 255, 255);
        var jGray = Color.FromArgb(255, 128, 128, 128);
        var jLightGray = Color.FromArgb(255, 192, 192, 192);
        var jDarkGray = Color.FromArgb(255, 64, 64, 64);
        var jRed = Color.FromArgb(255, 255, 0, 0);
        var jGreen = Color.FromArgb(255, 0, 255, 0);
        var jBlue = Color.FromArgb(255, 0, 0, 255);
        var jYellow = Color.FromArgb(255, 255, 255, 0);
        var jOrange = Color.FromArgb(255, 255, 200, 0);
        var jMagenta = Color.FromArgb(255, 255, 0, 255);
        var jCyan = Color.FromArgb(255, 0, 255, 255);
        var jPink = Color.FromArgb(255, 255, 175, 175);
        foreach (var p in new[]
                 {
                     ("black", jBlack), ("white", jWhite), ("red", jRed), ("green", jGreen),
                     ("blue", jBlue), ("yellow", jYellow), ("gray", jGray), ("lightGray", jLightGray),
                     ("darkGray", jDarkGray), ("orange", jOrange), ("magenta", jMagenta), ("cyan", jCyan), ("pink", jPink),
                     ("BLACK", jBlack), ("WHITE", jWhite), ("RED", jRed), ("GREEN", jGreen),
                     ("BLUE", jBlue), ("YELLOW", jYellow), ("GRAY", jGray), ("LIGHT_GRAY", jLightGray),
                     ("DARK_GRAY", jDarkGray), ("ORANGE", jOrange), ("MAGENTA", jMagenta), ("CYAN", jCyan), ("PINK", jPink)
                 })
            vm.SetStatic(c, p.Item1, "Ljava/awt/Color;", JValue.Ref(ColorObject(vm, p.Item2)));
        vm.RegisterNative(c.Name, "getRed", "()I", i => JValue.Int(ColorOf(i.Receiver.AsObject()!).R));
        vm.RegisterNative(c.Name, "getGreen", "()I", i => JValue.Int(ColorOf(i.Receiver.AsObject()!).G));
        vm.RegisterNative(c.Name, "getBlue", "()I", i => JValue.Int(ColorOf(i.Receiver.AsObject()!).B));
        vm.RegisterNative(c.Name, "getAlpha", "()I", i => JValue.Int(ColorOf(i.Receiver.AsObject()!).A));
        vm.RegisterNative(c.Name, "getRGB", "()I", i =>
        {
            var c1 = ColorOf(i.Receiver.AsObject()!);
            return JValue.Int(unchecked((int)((uint)c1.A << 24 | (uint)c1.R << 16 | (uint)c1.G << 8 | (uint)c1.B)));
        });
        // Java's scale factors: brighter 1/0.7, darker 0.7; black stays black.
        vm.RegisterNative(c.Name, "brighter", "()Ljava/awt/Color;", i => JValue.Ref(ColorObject(vm, Scale(ColorOf(i.Receiver.AsObject()!), 1.0 / 0.7))));
        vm.RegisterNative(c.Name, "darker", "()Ljava/awt/Color;", i => JValue.Ref(ColorObject(vm, Scale(ColorOf(i.Receiver.AsObject()!), 0.7))));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
            JValue.Int(i.Receiver.AsObject()?.NativeState is Color a && i.Arguments[0].AsObject()?.NativeState is Color b && a == b ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
        {
            var c1 = ColorOf(i.Receiver.AsObject()!);
            return JValue.Int(unchecked((int)((uint)c1.A << 24 | (uint)c1.R << 16 | (uint)c1.G << 8 | (uint)c1.B)));
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var c1 = ColorOf(i.Receiver.AsObject()!);
            return JValue.Ref(vm.CreateString($"java.awt.Color[r={c1.R},g={c1.G},b={c1.B}]"));
        });
    }

    private static Color Scale(Color c, double f) =>
        Color.FromArgb(c.A, (int)Math.Clamp(c.R * f, 0, 255), (int)Math.Clamp(c.G * f, 0, 255), (int)Math.Clamp(c.B * f, 0, 255));

    // Logical font names of 1.1 mapped to host families.
    internal static Font? ToFont(JavaFontState? st)
    {
        if (st == null) return null;
        try
        {
            string family = st.Family switch
            {
                "Dialog" or "DialogInput" or "SansSerif" or "Helvetica" => FontFamily.GenericSansSerif.Name,
                "Serif" or "TimesRoman" => FontFamily.GenericSerif.Name,
                "Monospaced" or "Courier" => FontFamily.GenericMonospace.Name,
                "Symbol" => FontFamily.GenericSerif.Name,
                "ZapfDingbats" => FontFamily.GenericSansSerif.Name,
                _ => st.Family
            };
            var style = (st.Style & 1) != 0 ? FontStyle.Bold : FontStyle.Regular;
            if ((st.Style & 2) != 0) style |= FontStyle.Italic;
            return new Font(new FontFamily(family), Math.Max(1, st.Size), style, GraphicsUnit.Pixel);
        }
        catch { return null; } // unknown family must not kill the applet
    }

    internal static JavaFontState? FontState(JObject? o) => o?.NativeState as JavaFontState;

    private static void RegisterFont(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;II)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaFontState { Family = vm.StringValue(i.Arguments[0]), Style = i.Arguments[1].AsInt(), Size = i.Arguments[2].AsInt() };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(FontState(i.Receiver.AsObject())?.Family ?? "Dialog")));
        vm.RegisterNative(c.Name, "getFamily", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(FontState(i.Receiver.AsObject())?.Family ?? "Dialog")));
        vm.RegisterNative(c.Name, "getFontName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(FontState(i.Receiver.AsObject())?.Family ?? "Dialog")));
        vm.RegisterNative(c.Name, "getSize", "()I", i => JValue.Int(FontState(i.Receiver.AsObject())?.Size ?? 12));
        vm.RegisterNative(c.Name, "getStyle", "()I", i => JValue.Int(FontState(i.Receiver.AsObject())?.Style ?? 0));
        vm.RegisterNative(c.Name, "isBold", "()Z", i => JValue.Int(((FontState(i.Receiver.AsObject())?.Style ?? 0) & 1) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isItalic", "()Z", i => JValue.Int(((FontState(i.Receiver.AsObject())?.Style ?? 0) & 2) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isPlain", "()Z", i => JValue.Int((FontState(i.Receiver.AsObject())?.Style ?? 0) == 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
        {
            var a = FontState(i.Receiver.AsObject());
            var b = FontState(i.Arguments[0].AsObject());
            return JValue.Int(a != null && b != null && a.Family == b.Family && a.Style == b.Style && a.Size == b.Size ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
        {
            var a = FontState(i.Receiver.AsObject());
            if (a == null) return JValue.Int(0);
            return JValue.Int(HashCode.Combine(a.Family, a.Style, a.Size));
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var a = FontState(i.Receiver.AsObject());
            if (a == null) return JValue.Ref(vm.CreateString("java.awt.Font[family=Dialog,name=Dialog,style=plain,size=12]"));
            string style = (a.Style & 3) switch { 1 => "bold", 2 => "italic", 3 => "bolditalic", _ => "plain" };
            return JValue.Ref(vm.CreateString($"java.awt.Font[family={a.Family},name={a.Family},style={style},size={a.Size}]"));
        });
        vm.SetStatic(c, "PLAIN", "I", JValue.Int(0));
        vm.SetStatic(c, "BOLD", "I", JValue.Int(1));
        vm.SetStatic(c, "ITALIC", "I", JValue.Int(2));
    }

    private static void RegisterFontMetrics(JavaVm vm, JClass c)
    {
        // Real metrics from the resolved host font when available.
        vm.RegisterNative(c.Name, "getAscent", "()I", i => JValue.Int((int)Math.Round(Ascent(i.Receiver.AsObject()))));
        vm.RegisterNative(c.Name, "getDescent", "()I", i => JValue.Int(Math.Max(1, (int)Math.Round(Descent(i.Receiver.AsObject())))));
        vm.RegisterNative(c.Name, "getLeading", "()I", i => JValue.Int((int)Math.Round(Leading(i.Receiver.AsObject()))));
        vm.RegisterNative(c.Name, "getHeight", "()I", i =>
            JValue.Int((int)Math.Round(Ascent(i.Receiver.AsObject()) + Descent(i.Receiver.AsObject()) + Leading(i.Receiver.AsObject()))));
        vm.RegisterNative(c.Name, "getMaxAscent", "()I", i => JValue.Int((int)Math.Round(Ascent(i.Receiver.AsObject()))));
        vm.RegisterNative(c.Name, "getMaxDescent", "()I", i => JValue.Int(Math.Max(1, (int)Math.Round(Descent(i.Receiver.AsObject())))));
        vm.RegisterNative(c.Name, "stringWidth", "(Ljava/lang/String;)I", i =>
            JValue.Int(Measure(i.Receiver.AsObject(), vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "charWidth", "(C)I", i => JValue.Int(Measure(i.Receiver.AsObject(), ((char)i.Arguments[0].AsInt()).ToString())));
        vm.RegisterNative(c.Name, "charWidth", "(I)I", i => JValue.Int(Measure(i.Receiver.AsObject(), ((char)i.Arguments[0].AsInt()).ToString())));
        vm.RegisterNative(c.Name, "charsWidth", "([CII)I", i =>
        {
            var text = CharsText(vm, i.Arguments[0].AsArray(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt());
            return JValue.Int(Measure(i.Receiver.AsObject(), text));
        });
        vm.RegisterNative(c.Name, "bytesWidth", "([BII)I", i =>
        {
            var a = i.Arguments[0].AsArray();
            int off = i.Arguments[1].AsInt(), len = i.Arguments[2].AsInt();
            var chars = a == null || off < 0 || len < 0 || off + len > a.Elements.Length
                ? ""
                : new string(a.Elements.Skip(off).Take(len).Select(v => (char)(v.AsInt() & 0xFF)).ToArray());
            return JValue.Int(Measure(i.Receiver.AsObject(), chars));
        });
        vm.RegisterNative(c.Name, "getWidths", "()[I", i =>
        {
            var arr = vm.NewArray("I", 256);
            for (int k = 0; k < 256; k++) arr.Elements[k] = JValue.Int(Measure(i.Receiver.AsObject(), ((char)k).ToString()));
            return JValue.Ref(arr);
        });
    }

    private static string CharsText(JavaVm vm, JArray? a, int off, int len)
    {
        if (a == null || off < 0 || len < 0 || off + len > a.Elements.Length) return "";
        var chars = new char[len];
        for (int k = 0; k < len; k++) chars[k] = (char)a.Elements[off + k].AsInt();
        return new string(chars);
    }

    private static Font? HostFont(JObject? o) => (o?.NativeState as JavaFontMetricsState)?.Font;

    private static float Ascent(JObject? o)
    {
        var f = HostFont(o);
        return f == null ? 10f : -f.SkFont.Metrics.Ascent;
    }

    private static float Descent(JObject? o)
    {
        var f = HostFont(o);
        return f == null ? 3f : f.SkFont.Metrics.Descent;
    }

    private static float Leading(JObject? o)
    {
        var f = HostFont(o);
        return f == null ? 1f : Math.Max(0f, f.SkFont.Metrics.Leading);
    }

    private static int Measure(JObject? o, string text)
    {
        var f = HostFont(o);
        if (f == null) return text.Length * 8;
        return (int)Math.Round(f.SkFont.MeasureText(text));
    }

    private static void RegisterDimension(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { SetDimensionFields(vm, i.Receiver.AsObject()!, c, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i => { SetDimensionFields(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "getWidth", "()D", i => JValue.Double(FieldOf(vm, i.Receiver, c, "width")));
        vm.RegisterNative(c.Name, "getHeight", "()D", i => JValue.Double(FieldOf(vm, i.Receiver, c, "height")));
        vm.RegisterNative(c.Name, "setSize", "(II)V", i => { SetDimensionFields(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setSize", "(Ljava/awt/Dimension;)V", i =>
        {
            var src = i.Arguments[0].AsObject();
            var cls = vm.LoadClass("java.awt.Dimension");
            SetDimensionFields(vm, i.Receiver.AsObject()!, c, vm.GetField(src!, cls, "width", "I").AsInt(), vm.GetField(src!, cls, "height", "I").AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getSize", "()Ljava/awt/Dimension;", i =>
        {
            var cls = vm.LoadClass("java.awt.Dimension");
            return JValue.Ref(new JObject
            {
                Class = cls,
                Fields = { [new JFieldKey(c, "width", "I")] = JValue.Int(FieldOf(vm, i.Receiver, c, "width")), [new JFieldKey(c, "height", "I")] = JValue.Int(FieldOf(vm, i.Receiver, c, "height")) }
            });
        });
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
        {
            var other = i.Arguments[0].AsObject();
            if (other == null) return JValue.Int(0);
            var cls = vm.LoadClass("java.awt.Dimension");
            return JValue.Int(FieldOf(vm, i.Receiver, c, "width") == vm.GetField(other, cls, "width", "I").AsInt()
                && FieldOf(vm, i.Receiver, c, "height") == vm.GetField(other, cls, "height", "I").AsInt() ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
            JValue.Int(HashCode.Combine(FieldOf(vm, i.Receiver, c, "width"), FieldOf(vm, i.Receiver, c, "height"))));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString($"java.awt.Dimension[width={FieldOf(vm, i.Receiver, c, "width")},height={FieldOf(vm, i.Receiver, c, "height")}]")));
    }

    private static int FieldOf(JavaVm vm, JValue receiver, JClass cls, string name) => vm.GetField(receiver.AsObject()!, cls, name, "I").AsInt();

    private static void SetDimensionFields(JavaVm vm, JObject o, JClass c, int w, int h)
    {
        o.NativeState = new JRectState(0, 0, w, h);
        vm.SetField(o, c, "width", "I", JValue.Int(w));
        vm.SetField(o, c, "height", "I", JValue.Int(h));
    }

    private static void RegisterPoint(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { SetPointFields(vm, i.Receiver.AsObject()!, c, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i => { SetPointFields(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "getX", "()D", i => JValue.Double(FieldOf(vm, i.Receiver, c, "x")));
        vm.RegisterNative(c.Name, "getY", "()D", i => JValue.Double(FieldOf(vm, i.Receiver, c, "y")));
        vm.RegisterNative(c.Name, "move", "(II)V", i => { SetPointFields(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLocation", "(II)V", i => { SetPointFields(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLocation", "(Ljava/awt/Point;)V", i =>
        {
            var src = i.Arguments[0].AsObject();
            var cls = vm.LoadClass("java.awt.Point");
            SetPointFields(vm, i.Receiver.AsObject()!, c, vm.GetField(src!, cls, "x", "I").AsInt(), vm.GetField(src!, cls, "y", "I").AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "translate", "(II)V", i =>
        {
            SetPointFields(vm, i.Receiver.AsObject()!, c,
                FieldOf(vm, i.Receiver, c, "x") + i.Arguments[0].AsInt(),
                FieldOf(vm, i.Receiver, c, "y") + i.Arguments[1].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
        {
            var other = i.Arguments[0].AsObject();
            if (other == null) return JValue.Int(0);
            var cls = vm.LoadClass("java.awt.Point");
            return JValue.Int(FieldOf(vm, i.Receiver, c, "x") == vm.GetField(other, cls, "x", "I").AsInt()
                && FieldOf(vm, i.Receiver, c, "y") == vm.GetField(other, cls, "y", "I").AsInt() ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "hashCode", "()I", i => JValue.Int(HashCode.Combine(FieldOf(vm, i.Receiver, c, "x"), FieldOf(vm, i.Receiver, c, "y"))));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString($"java.awt.Point[x={FieldOf(vm, i.Receiver, c, "x")},y={FieldOf(vm, i.Receiver, c, "y")}]")));
        vm.RegisterNative(c.Name, "getLocation", "()Ljava/awt/Point;", i =>
        {
            var cls = vm.LoadClass("java.awt.Point");
            return JValue.Ref(new JObject
            {
                Class = cls,
                Fields = { [new JFieldKey(c, "x", "I")] = JValue.Int(FieldOf(vm, i.Receiver, c, "x")), [new JFieldKey(c, "y", "I")] = JValue.Int(FieldOf(vm, i.Receiver, c, "y")) }
            });
        });
    }

    private static void SetPointFields(JavaVm vm, JObject o, JClass c, int x, int y)
    {
        o.NativeState = new JRectState(x, y, 0, 0);
        vm.SetField(o, c, "x", "I", JValue.Int(x));
        vm.SetField(o, c, "y", "I", JValue.Int(y));
    }

    private static (int X, int Y, int W, int H) RectValues(JavaVm vm, JValue receiver, JClass c) =>
        (FieldOf(vm, receiver, c, "x"), FieldOf(vm, receiver, c, "y"),
            FieldOf(vm, receiver, c, "width"), FieldOf(vm, receiver, c, "height"));

    private static void SetRectValues(JavaVm vm, JObject o, JClass c, int x, int y, int w, int h)
    {
        o.NativeState = new JRectState(x, y, w, h);
        vm.SetField(o, c, "x", "I", JValue.Int(x));
        vm.SetField(o, c, "y", "I", JValue.Int(y));
        vm.SetField(o, c, "width", "I", JValue.Int(w));
        vm.SetField(o, c, "height", "I", JValue.Int(h));
    }

    private static void RegisterRectangle(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { SetRectValues(vm, i.Receiver.AsObject()!, c, 0, 0, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i => { SetRectValues(vm, i.Receiver.AsObject()!, c, 0, 0, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i => { SetRectValues(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Point;Ljava/awt/Dimension;)V", i =>
        {
            var pointCls = vm.LoadClass("java.awt.Point");
            var dimCls = vm.LoadClass("java.awt.Dimension");
            int x = vm.GetField(i.Arguments[0].AsObject()!, pointCls, "x", "I").AsInt();
            int y = vm.GetField(i.Arguments[0].AsObject()!, pointCls, "y", "I").AsInt();
            int w = vm.GetField(i.Arguments[1].AsObject()!, dimCls, "width", "I").AsInt();
            int h = vm.GetField(i.Arguments[1].AsObject()!, dimCls, "height", "I").AsInt();
            SetRectValues(vm, i.Receiver.AsObject()!, c, x, y, w, h);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Rectangle;)V", i =>
        {
            var r = RectValues(vm, i.Arguments[0], c);
            SetRectValues(vm, i.Receiver.AsObject()!, c, r.X, r.Y, r.W, r.H);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getBounds", "()Ljava/awt/Rectangle;", i => i.Receiver);
        vm.RegisterNative(c.Name, "setBounds", "(IIII)V", i => { SetRectValues(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setBounds", "(Ljava/awt/Rectangle;)V", i =>
        {
            var r = RectValues(vm, i.Arguments[0], c);
            SetRectValues(vm, i.Receiver.AsObject()!, c, r.X, r.Y, r.W, r.H);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "reshape", "(IIII)V", i => { SetRectValues(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "contains", "(II)Z", i => Contains(vm, i.Receiver, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()));
        vm.RegisterNative(c.Name, "inside", "(II)Z", i => Contains(vm, i.Receiver, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()));
        vm.RegisterNative(c.Name, "contains", "(Ljava/awt/Point;)Z", i =>
        {
            var pointCls = vm.LoadClass("java.awt.Point");
            int x = vm.GetField(i.Arguments[0].AsObject()!, pointCls, "x", "I").AsInt();
            int y = vm.GetField(i.Arguments[0].AsObject()!, pointCls, "y", "I").AsInt();
            return Contains(vm, i.Receiver, c, x, y);
        });
        vm.RegisterNative(c.Name, "contains", "(Ljava/awt/Rectangle;)Z", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            var o = RectValues(vm, i.Arguments[0], c);
            return JValue.Int(o.X >= r.X && o.Y >= r.Y && o.X + o.W <= r.X + r.W && o.Y + o.H <= r.Y + r.H ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "intersects", "(Ljava/awt/Rectangle;)Z", i =>
        {
            var a = RectValues(vm, i.Receiver, c);
            var b = RectValues(vm, i.Arguments[0], c);
            return JValue.Int(a.W > 0 && a.H > 0 && b.W > 0 && b.H > 0
                && a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "intersection", "(Ljava/awt/Rectangle;)Ljava/awt/Rectangle;", i =>
        {
            var a = RectValues(vm, i.Receiver, c);
            var b = RectValues(vm, i.Arguments[0], c);
            int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
            var cls = vm.LoadClass("java.awt.Rectangle");
            var result = new JObject { Class = cls };
            SetRectValues(vm, result, cls, x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
            return JValue.Ref(result);
        });
        vm.RegisterNative(c.Name, "union", "(Ljava/awt/Rectangle;)Ljava/awt/Rectangle;", i =>
        {
            var a = RectValues(vm, i.Receiver, c);
            var b = RectValues(vm, i.Arguments[0], c);
            if (a.W <= 0 || a.H <= 0) { var copy = CloneRect(vm, c, b); return JValue.Ref(copy); }
            if (b.W <= 0 || b.H <= 0) { var copy = CloneRect(vm, c, a); return JValue.Ref(copy); }
            int x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
            int x2 = Math.Max(a.X + a.W, b.X + b.W), y2 = Math.Max(a.Y + a.H, b.Y + b.H);
            var cls = vm.LoadClass("java.awt.Rectangle");
            var result = new JObject { Class = cls };
            SetRectValues(vm, result, cls, x1, y1, x2 - x1, y2 - y1);
            return JValue.Ref(result);
        });
        vm.RegisterNative(c.Name, "isEmpty", "()Z", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            return JValue.Int(r.W <= 0 || r.H <= 0 ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "translate", "(II)V", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            SetRectValues(vm, i.Receiver.AsObject()!, c, r.X + i.Arguments[0].AsInt(), r.Y + i.Arguments[1].AsInt(), r.W, r.H);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "grow", "(II)V", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            SetRectValues(vm, i.Receiver.AsObject()!, c, r.X - i.Arguments[0].AsInt(), r.Y - i.Arguments[1].AsInt(),
                r.W + 2 * i.Arguments[0].AsInt(), r.H + 2 * i.Arguments[1].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getSize", "()Ljava/awt/Dimension;", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            var cls = vm.LoadClass("java.awt.Dimension");
            var dim = new JObject { Class = cls };
            vm.SetField(dim, cls, "width", "I", JValue.Int(r.W));
            vm.SetField(dim, cls, "height", "I", JValue.Int(r.H));
            dim.NativeState = new JRectState(0, 0, r.W, r.H);
            return JValue.Ref(dim);
        });
        vm.RegisterNative(c.Name, "getLocation", "()Ljava/awt/Point;", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            var cls = vm.LoadClass("java.awt.Point");
            var point = new JObject { Class = cls };
            vm.SetField(point, cls, "x", "I", JValue.Int(r.X));
            vm.SetField(point, cls, "y", "I", JValue.Int(r.Y));
            point.NativeState = new JRectState(r.X, r.Y, 0, 0);
            return JValue.Ref(point);
        });
        vm.RegisterNative(c.Name, "add", "(II)V", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            int x2 = Math.Min(r.X, i.Arguments[0].AsInt());
            int y2 = Math.Min(r.Y, i.Arguments[1].AsInt());
            int x3 = Math.Max(r.X + r.W, i.Arguments[0].AsInt() + 1);
            int y3 = Math.Max(r.Y + r.H, i.Arguments[1].AsInt() + 1);
            SetRectValues(vm, i.Receiver.AsObject()!, c, x2, y2, x3 - x2, y3 - y2);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
        {
            var other = i.Arguments[0].AsObject();
            if (other == null || other.Class.Name != "java.awt.Rectangle") return JValue.Int(0);
            var a = RectValues(vm, i.Receiver, c);
            var b = RectValues(vm, i.Arguments[0], c);
            return JValue.Int(a == b ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            return JValue.Int(HashCode.Combine(r.X, r.Y, r.W, r.H));
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var r = RectValues(vm, i.Receiver, c);
            return JValue.Ref(vm.CreateString($"java.awt.Rectangle[x={r.X},y={r.Y},width={r.W},height={r.H}]"));
        });
    }

    private static JObject CloneRect(JavaVm vm, JClass c, (int X, int Y, int W, int H) r)
    {
        var cls = vm.LoadClass("java.awt.Rectangle");
        var result = new JObject { Class = cls };
        SetRectValues(vm, result, cls, r.X, r.Y, r.W, r.H);
        return result;
    }

    private static JValue Contains(JavaVm vm, JValue receiver, JClass c, int x, int y)
    {
        var r = RectValues(vm, receiver, c);
        // Java 1.1 contains uses width/height exclusive only for positive
        // sizes; the classic definition matches inside() exactly.
        return JValue.Int(r.W > 0 && r.H > 0 && x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H ? 1 : 0);
    }

    private static JavaPolygonState PolygonState(JObject o) => (JavaPolygonState)(o.NativeState ??= new JavaPolygonState());

    private static void RegisterPolygon(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = PolygonState(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "([I[II)V", i =>
        {
            var xs = i.Arguments[0].AsArray();
            var ys = i.Arguments[1].AsArray();
            int n = Math.Clamp(i.Arguments[2].AsInt(), 0, Math.Min(xs?.Elements.Length ?? 0, ys?.Elements.Length ?? 0));
            var st = new JavaPolygonState
            {
                XPoints = xs == null ? new int[n] : xs.Elements.Take(n).Select(v => v.AsInt()).ToArray(),
                YPoints = ys == null ? new int[n] : ys.Elements.Take(n).Select(v => v.AsInt()).ToArray(),
                NPoints = n
            };
            i.Receiver.AsObject()!.NativeState = st;
            SyncPolygonFields(vm, i.Receiver.AsObject()!, c, st);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "addPoint", "(II)V", i =>
        {
            var st = PolygonState(i.Receiver.AsObject()!);
            int n = st.NPoints;
            Array.Resize(ref st.XPoints, n + 1);
            Array.Resize(ref st.YPoints, n + 1);
            st.XPoints[n] = i.Arguments[0].AsInt();
            st.YPoints[n] = i.Arguments[1].AsInt();
            st.NPoints = n + 1;
            SyncPolygonFields(vm, i.Receiver.AsObject()!, c, st);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "contains", "(II)Z", i =>
        {
            var st = PolygonState(i.Receiver.AsObject()!);
            // Even-odd crossing test on the point list.
            bool inside = false;
            for (int a = 0, b = st.NPoints - 1; a < st.NPoints; b = a++)
            {
                if ((st.YPoints[a] > i.Arguments[1].AsInt()) != (st.YPoints[b] > i.Arguments[1].AsInt())
                    && i.Arguments[0].AsInt() < (st.XPoints[b] - st.XPoints[a]) * (i.Arguments[1].AsInt() - st.YPoints[a])
                        / (double)(st.YPoints[b] - st.YPoints[a]) + st.XPoints[a])
                    inside = !inside;
            }
            return JValue.Int(inside ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "inside", "(II)Z", i =>
        {
            var st = PolygonState(i.Receiver.AsObject()!);
            var obj = i.Receiver.AsObject()!;
            return vm.InvokeVirtual(obj, "contains", "(II)Z", i.Arguments[0], i.Arguments[1]);
        });
        vm.RegisterNative(c.Name, "getBoundingBox", "()Ljava/awt/Rectangle;", i => BoundingBox(vm, i.Receiver.AsObject()!, c));
        vm.RegisterNative(c.Name, "getBounds", "()Ljava/awt/Rectangle;", i => BoundingBox(vm, i.Receiver.AsObject()!, c));
        vm.RegisterNative(c.Name, "translate", "(II)V", i =>
        {
            var st = PolygonState(i.Receiver.AsObject()!);
            for (int k = 0; k < st.NPoints; k++)
            {
                st.XPoints[k] += i.Arguments[0].AsInt();
                st.YPoints[k] += i.Arguments[1].AsInt();
            }
            SyncPolygonFields(vm, i.Receiver.AsObject()!, c, st);
            return JValue.Void;
        });
    }

    private static JValue BoundingBox(JavaVm vm, JObject o, JClass c)
    {
        var st = PolygonState(o);
        var cls = vm.LoadClass("java.awt.Rectangle");
        var result = new JObject { Class = cls };
        if (st.NPoints == 0)
        {
            SetRectValues(vm, result, cls, 0, 0, 0, 0);
            return JValue.Ref(result);
        }
        int minX = st.XPoints[0], maxX = st.XPoints[0], minY = st.YPoints[0], maxY = st.YPoints[0];
        for (int k = 1; k < st.NPoints; k++)
        {
            minX = Math.Min(minX, st.XPoints[k]);
            maxX = Math.Max(maxX, st.XPoints[k]);
            minY = Math.Min(minY, st.YPoints[k]);
            maxY = Math.Max(maxY, st.YPoints[k]);
        }
        SetRectValues(vm, result, cls, minX, minY, maxX - minX, maxY - minY);
        return JValue.Ref(result);
    }

    // xpoints/ypoints/npoints are public Java fields mirrored from the
    // host arrays so applets can read them directly.
    private static void SyncPolygonFields(JavaVm vm, JObject o, JClass c, JavaPolygonState st)
    {
        vm.SetField(o, c, "npoints", "I", JValue.Int(st.NPoints));
        var xs = vm.NewArray("I", st.XPoints.Length);
        for (int k = 0; k < st.XPoints.Length; k++) xs.Elements[k] = JValue.Int(st.XPoints[k]);
        vm.SetField(o, c, "xpoints", "[I", JValue.Ref(xs));
        var ys = vm.NewArray("I", st.YPoints.Length);
        for (int k = 0; k < st.YPoints.Length; k++) ys.Elements[k] = JValue.Int(st.YPoints[k]);
        vm.SetField(o, c, "ypoints", "[I", JValue.Ref(ys));
    }

    private static void RegisterToolkit(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "getDefaultToolkit", "()Ljava/awt/Toolkit;", i =>
        {
            if (!c.StaticFields.TryGetValue("defaultToolkit", out var tk))
            {
                tk = JValue.Ref(new JObject { Class = c });
                c.StaticFields["defaultToolkit"] = tk;
            }
            return tk;
        });
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i =>
            JValue.Ref(vm.GraphicsFactory.LoadImage((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
        vm.RegisterNative(c.Name, "getImage", "(Ljava/lang/String;)Ljava/awt/Image;", i =>
            JValue.Ref(vm.GraphicsFactory.LoadImage(vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "createImage", "(Ljava/awt/image/ImageProducer;)Ljava/awt/Image;", i =>
        {
            _ = i.Arguments[0].AsObject();
            return JValue.Ref(vm.GraphicsFactory.CreateImage(1, 1));
        });
        vm.RegisterNative(c.Name, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;", i =>
            JValue.Ref(vm.GraphicsFactory.CreateFontMetrics(JavaAwt.ToFont(JavaAwt.FontState(i.Arguments[0].AsObject())))));
        vm.RegisterNative(c.Name, "beep", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "sync", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getScreenResolution", "()I", _ => JValue.Int(96));
        vm.RegisterNative(c.Name, "getScreenSize", "()Ljava/awt/Dimension;", i =>
        {
            var cls = vm.LoadClass("java.awt.Dimension");
            var dim = new JObject { Class = cls };
            vm.SetField(dim, cls, "width", "I", JValue.Int(640));
            vm.SetField(dim, cls, "height", "I", JValue.Int(480));
            return JValue.Ref(dim);
        });
    }
}
