using Retro96.Drawing;

namespace Retro96.Engine.Java;

public enum JavaInputKind { MouseDown, MouseUp, MouseMove, MouseDrag, KeyDown, KeyUp, KeyTyped, MouseEnter, MouseExit, MouseWheel }

public readonly record struct JavaInput(JavaInputKind Kind, int X, int Y, int Button, int WheelDelta, int KeyCode, char KeyChar, bool Shift, bool Control, bool Alt);

internal static class JavaComponentBridge
{
    public static JavaComponentState State(JObject obj)
    {
        if (obj.NativeState is JavaComponentState s) return s;
        if (obj.NativeState is JavaAppletNativeState a) return a.Component;
        return (JavaComponentState)(obj.NativeState = new JavaComponentState());
    }

    public static void Register(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = State(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "paint", "(Ljava/awt/Graphics;)V", i => Paint(vm, i));
        vm.RegisterNative(c.Name, "update", "(Ljava/awt/Graphics;)V", i => Paint(vm, i));
        vm.RegisterNative(c.Name, "repaint", "()V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "repaint", "(J)V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "repaint", "(IIII)V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "repaint", "(JIIII)V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getGraphics", "()Ljava/awt/Graphics;", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            if (s.Font == null) { try { s.Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular, GraphicsUnit.Pixel); } catch { } }
            return JValue.Ref(vm.GraphicsFactory.CreateGraphics(vm.HostGraphics ?? throw new InvalidOperationException("No graphics host"), s.Width, s.Height, s.X, s.Y));
        });
        vm.RegisterNative(c.Name, "createImage", "(II)Ljava/awt/Image;", i => JValue.Ref(vm.GraphicsFactory.CreateImage(i.Arguments[0].AsInt(), i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "prepareImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z", i =>
        {
            var st = i.Arguments[0].AsObject()?.NativeState as JavaImageState;
            if (st != null) JavaGraphicsBridge.EnsureImageLoaded(vm, st);
            return JValue.Int(1);
        });
        vm.RegisterNative(c.Name, "checkImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)I", i =>
        {
            var st = i.Arguments[0].AsObject()?.NativeState as JavaImageState;
            if (st != null) JavaGraphicsBridge.EnsureImageLoaded(vm, st);
            return JValue.Int(st?.Loaded == true ? 32 : 0); // ALLBITS
        });
        vm.RegisterNative(c.Name, "setBackground", "(Ljava/awt/Color;)V", i => { State(i.Receiver.AsObject()!).Background = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.LightGray); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getBackground", "()Ljava/awt/Color;", i => JValue.Ref(ColorObject(vm, State(i.Receiver.AsObject()!).Background)));
        vm.RegisterNative(c.Name, "setForeground", "(Ljava/awt/Color;)V", i => { State(i.Receiver.AsObject()!).Foreground = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.Black); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getForeground", "()Ljava/awt/Color;", i => JValue.Ref(ColorObject(vm, State(i.Receiver.AsObject()!).Foreground)));
        vm.RegisterNative(c.Name, "setFont", "(Ljava/awt/Font;)V", i => { State(i.Receiver.AsObject()!).Font = FontFrom(i.Arguments[0].AsObject()); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getFont", "()Ljava/lang/Object;", i => JValue.Ref(null)); // replaced below with correct descriptor
        vm.RegisterNative(c.Name, "getFont", "()Ljava/awt/Font;", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            var o = new JObject { Class = vm.LoadClass("java.awt.Font"), NativeState = new JavaFontState { Family = s.Font?.FontFamily.Name ?? "Dialog", Size = (int)(s.Font?.Size ?? 12), Style = ((s.Font?.Style ?? FontStyle.Regular) & FontStyle.Bold) != 0 ? 1 : 0 } };
            return JValue.Ref(o);
        });
        vm.RegisterNative(c.Name, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;", i =>
        {
            var o = new JObject { Class = vm.LoadClass("java.awt.FontMetrics"), NativeState = new JavaFontMetricsState { Font = FontFrom(i.Arguments[0].AsObject()) } };
            return JValue.Ref(o);
        });
        vm.RegisterNative(c.Name, "setVisible", "(Z)V", i => { State(i.Receiver.AsObject()!).Visible = i.Arguments[0].AsInt() != 0; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "isVisible", "()Z", i => JValue.Int(State(i.Receiver.AsObject()!).Visible ? 1 : 0));
        vm.RegisterNative(c.Name, "show", "()V", i => { State(i.Receiver.AsObject()!).Visible = true; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "hide", "()V", i => { State(i.Receiver.AsObject()!).Visible = false; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "enable", "()V", i => { State(i.Receiver.AsObject()!).Enabled = true; return JValue.Void; });
        vm.RegisterNative(c.Name, "disable", "()V", i => { State(i.Receiver.AsObject()!).Enabled = false; return JValue.Void; });
        vm.RegisterNative(c.Name, "setEnabled", "(Z)V", i => { State(i.Receiver.AsObject()!).Enabled = i.Arguments[0].AsInt() != 0; return JValue.Void; });
        vm.RegisterNative(c.Name, "isEnabled", "()Z", i => JValue.Int(State(i.Receiver.AsObject()!).Enabled ? 1 : 0));
        // FIX: resize keeps the applet-level state and the component state in sync
        void SetSize(JavaInvocation i, int w, int h)
        {
            var o = i.Receiver.AsObject()!;
            var s = State(o);
            s.Width = Math.Max(0, w); s.Height = Math.Max(0, h);
            if (o.NativeState is JavaAppletNativeState ap) { ap.Width = Math.Max(1, w); ap.Height = Math.Max(1, h); }
            vm.Repaint();
        }
        vm.RegisterNative(c.Name, "setSize", "(II)V", i => { SetSize(i, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "resize", "(II)V", i => { SetSize(i, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "getSize", "()Ljava/awt/Dimension;", i => Pair(vm, State(i.Receiver.AsObject()!).Width, State(i.Receiver.AsObject()!).Height, "java.awt.Dimension"));
        vm.RegisterNative(c.Name, "size", "()Ljava/awt/Dimension;", i => Pair(vm, State(i.Receiver.AsObject()!).Width, State(i.Receiver.AsObject()!).Height, "java.awt.Dimension"));
        vm.RegisterNative(c.Name, "preferredSize", "()Ljava/awt/Dimension;", i => Pair(vm, State(i.Receiver.AsObject()!).Width, State(i.Receiver.AsObject()!).Height, "java.awt.Dimension"));
        vm.RegisterNative(c.Name, "minimumSize", "()Ljava/awt/Dimension;", i => Pair(vm, State(i.Receiver.AsObject()!).Width, State(i.Receiver.AsObject()!).Height, "java.awt.Dimension"));
        vm.RegisterNative(c.Name, "setBounds", "(IIII)V", i =>
        {
            var o = i.Receiver.AsObject()!; var s = State(o);
            s.X = i.Arguments[0].AsInt(); s.Y = i.Arguments[1].AsInt();
            s.Width = Math.Max(0, i.Arguments[2].AsInt()); s.Height = Math.Max(0, i.Arguments[3].AsInt());
            if (o.NativeState is JavaAppletNativeState ap) { ap.Width = Math.Max(1, s.Width); ap.Height = Math.Max(1, s.Height); }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getBounds", "()Ljava/awt/Rectangle;", i => Rect(vm, State(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "move", "(II)V", i => { var s = State(i.Receiver.AsObject()!); s.X = i.Arguments[0].AsInt(); s.Y = i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLocation", "(II)V", i => { var s = State(i.Receiver.AsObject()!); s.X = i.Arguments[0].AsInt(); s.Y = i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "location", "()Ljava/awt/Point;", i => Pair(vm, State(i.Receiver.AsObject()!).X, State(i.Receiver.AsObject()!).Y, "java.awt.Point"));
        vm.RegisterNative(c.Name, "getLocation", "()Ljava/awt/Point;", i => Pair(vm, State(i.Receiver.AsObject()!).X, State(i.Receiver.AsObject()!).Y, "java.awt.Point"));
        vm.RegisterNative(c.Name, "contains", "(II)Z", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt();
            return JValue.Int(x >= 0 && y >= 0 && x < s.Width && y < s.Height ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "inside", "(II)Z", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            int x = i.Arguments[0].AsInt(), y = i.Arguments[1].AsInt();
            return JValue.Int(x >= 0 && y >= 0 && x < s.Width && y < s.Height ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "getParent", "()Ljava/awt/Container;", i => JValue.Ref(State(i.Receiver.AsObject()!).Parent));
        vm.RegisterNative(c.Name, "setCursor", "(Ljava/awt/Cursor;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "requestFocus", "()V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "invalidate", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getToolkit", "()Ljava/awt/Toolkit;", _ =>
            JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Toolkit") }));
        vm.RegisterNative(c.Name, "handleEvent", "(Ljava/awt/Event;)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "action", "(Ljava/awt/Event;Ljava/lang/Object;)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "postEvent", "(Ljava/awt/Event;)Z", i =>
        {
            var e = i.Arguments[0];
            JObject? cur = i.Receiver.AsObject();
            while (cur != null)
            {
                if (vm.InvokeVirtual(cur, "handleEvent", "(Ljava/awt/Event;)Z", e).AsInt() != 0) return JValue.Int(1);
                cur = State(cur).Parent;
            }
            return JValue.Int(0);
        });
        foreach (var n in new[] { "mouseDown", "mouseUp", "mouseDrag", "mouseMove", "mouseEnter", "mouseExit" })
            vm.RegisterNative(c.Name, n, "(Ljava/awt/Event;II)Z", _ => JValue.Int(0));
        foreach (var n in new[] { "keyDown", "keyUp" })
            vm.RegisterNative(c.Name, n, "(Ljava/awt/Event;I)Z", _ => JValue.Int(0));
        RegisterListeners(vm, c);
    }

    private static Font? FontFrom(JObject? o)
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
        catch { return null; }
    }

    public static void RegisterContainer(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "add", "(Ljava/awt/Component;)Ljava/awt/Component;", i => Add(i.Receiver.AsObject()!, i.Arguments[0].AsObject()));
        vm.RegisterNative(c.Name, "add", "(Ljava/lang/String;Ljava/awt/Component;)Ljava/awt/Component;", i => Add(i.Receiver.AsObject()!, i.Arguments[1].AsObject(), vm.StringValue(i.Arguments[0])));
        vm.RegisterNative(c.Name, "add", "(Ljava/awt/Component;I)Ljava/awt/Component;", i => Add(i.Receiver.AsObject()!, i.Arguments[0].AsObject(), null));
        vm.RegisterNative(c.Name, "add", "(Ljava/awt/Component;Ljava/lang/Object;)Ljava/awt/Component;", i => Add(i.Receiver.AsObject()!, i.Arguments[0].AsObject(), i.Arguments[1].Tag == JTag.Reference ? vm.StringValue(i.Arguments[1]) : null));
        vm.RegisterNative(c.Name, "remove", "(Ljava/awt/Component;)V", i =>
        {
            var child = i.Arguments[0].AsObject();
            var s = State(i.Receiver.AsObject()!);
            if (child != null) { s.Children.Remove(child); State(child).Parent = null; }
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "removeAll", "()V", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            foreach (var child in s.Children) State(child).Parent = null;
            s.Children.Clear();
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getComponent", "(I)Ljava/awt/Component;", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            int idx = i.Arguments[0].AsInt();
            return JValue.Ref(idx >= 0 && idx < s.Children.Count ? s.Children[idx] : null);
        });
        vm.RegisterNative(c.Name, "getComponentCount", "()I", i => JValue.Int(State(i.Receiver.AsObject()!).Children.Count));
        vm.RegisterNative(c.Name, "countComponents", "()I", i => JValue.Int(State(i.Receiver.AsObject()!).Children.Count));
        vm.RegisterNative(c.Name, "getComponents", "()[Ljava/awt/Component;", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            var arr = vm.NewArray("Ljava/awt/Component;", s.Children.Count);
            for (int k = 0; k < s.Children.Count; k++) arr.Elements[k] = JValue.Ref(s.Children[k]);
            return JValue.Ref(arr);
        });
        vm.RegisterNative(c.Name, "locate", "(II)Ljava/awt/Component;", i =>
        {
            var (target, _, _) = HitComponent(i.Receiver.AsObject()!, i.Arguments[0].AsInt(), i.Arguments[1].AsInt());
            return JValue.Ref(ReferenceEquals(target, i.Receiver.AsObject()!) ? null : target);
        });
        vm.RegisterNative(c.Name, "setLayout", "(Ljava/awt/LayoutManager;)V", i => { State(i.Receiver.AsObject()!).Layout = i.Arguments[0].AsObject(); LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "getLayout", "()Ljava/awt/LayoutManager;", i => JValue.Ref(State(i.Receiver.AsObject()!).Layout));
        vm.RegisterNative(c.Name, "validate", "()V", i => { LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "layout", "()V", i => { LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "doLayout", "()V", i => { LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "paintComponents", "(Ljava/awt/Graphics;)V", i => { PaintChildren(vm, i.Receiver.AsObject()!, i.Arguments[0]); return JValue.Void; });
    }

    private static JValue Add(JObject parent, JObject? child, string? constraint = null)
    {
        if (child == null) return JValue.Ref(null);
        var p = State(parent);
        if (!p.Children.Contains(child)) p.Children.Add(child);
        State(child).Parent = parent;
        State(child).LayoutConstraint = constraint;
        LayoutChildren(parent);
        return JValue.Ref(child);
    }

    private static void LayoutChildren(JObject parent)
    {
        var p = State(parent);
        if (p.Children.Count == 0) return;
        var layout = p.Layout?.NativeState as JavaLayoutState;
        if (layout?.Kind == JavaLayoutKind.Border)
        {
            JObject? north = null, south = null, east = null, west = null, center = null;
            foreach (var child in p.Children)
            {
                var cs = State(child);
                switch (cs.LayoutConstraint ?? "Center")
                {
                    case "North": north = child; break;
                    case "South": south = child; break;
                    case "East": east = child; break;
                    case "West": west = child; break;
                    default: center = child; break;
                }
            }
            int left = 0, top = 0, right = p.Width, bottom = p.Height, g = layout.HGap;
            if (north != null) { var ns = State(north); int h = Math.Max(ns.Height, 24); ns.X = left; ns.Y = top; ns.Width = Math.Max(0, right - left); ns.Height = Math.Min(h, Math.Max(0, bottom - top)); top = ns.Y + ns.Height + g; }
            if (south != null) { var ss = State(south); int h = Math.Max(ss.Height, 24); ss.X = left; ss.Height = Math.Min(h, Math.Max(0, bottom - top)); ss.Y = Math.Max(top, bottom - ss.Height); ss.Width = Math.Max(0, right - left); bottom = ss.Y - g; }
            if (east != null) { var es = State(east); int w = Math.Max(es.Width, 24); es.X = Math.Max(left, right - w); es.Y = top; es.Width = Math.Min(w, Math.Max(0, right - left)); es.Height = Math.Max(0, bottom - top); right = es.X - g; }
            if (west != null) { var ws = State(west); int w = Math.Max(ws.Width, 24); ws.X = left; ws.Y = top; ws.Width = Math.Min(w, Math.Max(0, right - left)); ws.Height = Math.Max(0, bottom - top); left = ws.X + ws.Width + g; }
            if (center != null) { var cs = State(center); cs.X = left; cs.Y = top; cs.Width = Math.Max(0, right - left); cs.Height = Math.Max(0, bottom - top); }
            return;
        }
        if (layout?.Kind == JavaLayoutKind.Grid)
        {
            int rows = layout.Rows, cols = layout.Cols, n = p.Children.Count;
            if (rows <= 0) rows = Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, cols)));
            if (cols <= 0) cols = Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, rows)));
            if (rows <= 0) rows = 1;
            if (cols <= 0) cols = 1;
            int cw = Math.Max(1, (p.Width - (cols + 1) * layout.HGap) / cols), ch = Math.Max(1, (p.Height - (rows + 1) * layout.VGap) / rows);
            for (int k = 0; k < n; k++)
            {
                var cs = State(p.Children[k]);
                int r = k / cols, col = k % cols;
                cs.X = layout.HGap + col * (cw + layout.HGap);
                cs.Y = layout.VGap + r * (ch + layout.VGap);
                cs.Width = cw; cs.Height = ch;
            }
            return;
        }
        // FlowLayout (Panel/Applet default): wrap components into rows.
        int gap = layout?.HGap ?? 5, vg = layout?.VGap ?? 5;
        int x = gap, y = vg, rowH = 0;
        int avail = Math.Max(1, p.Width - gap * 2);
        foreach (var child in p.Children)
        {
            var cs = State(child);
            int cw = cs.Width <= 0 || cs.Width == 100 ? Math.Max(24, Math.Min(avail, cs.Text.Length * 8 + 20)) : cs.Width;
            int ch = cs.Height <= 0 || cs.Height == 30 ? 24 : cs.Height;
            if (x > gap && x + cw > p.Width - gap) { x = gap; y += rowH + vg; rowH = 0; }
            cs.X = x; cs.Y = y; cs.Width = cw; cs.Height = ch;
            x += cw + gap; rowH = Math.Max(rowH, ch);
        }
    }

    internal static void RegisterFlowLayout(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Flow, JavaLayoutState.Left, 5, 5, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Flow, i.Arguments[0].AsInt(), 5, 5, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(III)V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Flow, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), 0, 0); return JValue.Void; });
        RegisterLayoutManagerCommon(vm, c);
        vm.SetStatic(c, "LEFT", "I", JValue.Int(0));
        vm.SetStatic(c, "CENTER", "I", JValue.Int(1));
        vm.SetStatic(c, "RIGHT", "I", JValue.Int(2));
        vm.SetStatic(c, "LEADING", "I", JValue.Int(3));
        vm.SetStatic(c, "TRAILING", "I", JValue.Int(4));
    }

    internal static void RegisterBorderLayout(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Border, 0, 5, 5, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Border, 0, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), 0, 0); return JValue.Void; });
        RegisterLayoutManagerCommon(vm, c);
        vm.SetStatic(c, "NORTH", "Ljava/lang/String;", JValue.Ref(vm.CreateString("North")));
        vm.SetStatic(c, "SOUTH", "Ljava/lang/String;", JValue.Ref(vm.CreateString("South")));
        vm.SetStatic(c, "EAST", "Ljava/lang/String;", JValue.Ref(vm.CreateString("East")));
        vm.SetStatic(c, "WEST", "Ljava/lang/String;", JValue.Ref(vm.CreateString("West")));
        vm.SetStatic(c, "CENTER", "Ljava/lang/String;", JValue.Ref(vm.CreateString("Center")));
    }

    internal static void RegisterGridLayout(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Grid, JavaLayoutState.Left, 5, 5, 1, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Grid, JavaLayoutState.Left, 5, 5, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i => { i.Receiver.AsObject()!.NativeState = new JavaLayoutState(JavaLayoutKind.Grid, JavaLayoutState.Left, i.Arguments[2].AsInt(), i.Arguments[3].AsInt(), i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        RegisterLayoutManagerCommon(vm, c);
    }

    private static void RegisterLayoutManagerCommon(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "layoutContainer", "(Ljava/awt/Container;)V", i => { LayoutChildren(i.Arguments[0].AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;", i => Pair(vm, State(i.Arguments[0].AsObject()!).Width, State(i.Arguments[0].AsObject()!).Height, "java.awt.Dimension"));
        vm.RegisterNative(c.Name, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;", i => Pair(vm, State(i.Arguments[0].AsObject()!).Width, State(i.Arguments[0].AsObject()!).Height, "java.awt.Dimension"));
        vm.RegisterNative(c.Name, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "removeLayoutComponent", "(Ljava/awt/Component;)V", _ => JValue.Void);
    }

    internal static void RegisterTextWidget(JavaVm vm, JClass c, string field)
    {
        foreach (var sig in c.Name switch
        {
            "java.awt.Button" => new[] { "()V", "(Ljava/lang/String;)V" },
            "java.awt.Label" => new[] { "()V", "(Ljava/lang/String;)V", "(Ljava/lang/String;I)V" },
            "java.awt.TextField" => new[] { "()V", "(I)V", "(Ljava/lang/String;)V", "(Ljava/lang/String;I)V" },
            "java.awt.TextArea" => new[] { "()V", "(II)V", "(Ljava/lang/String;)V", "(Ljava/lang/String;II)V" },
            _ => new[] { "()V", "(Ljava/lang/String;)V", "(Ljava/lang/String;II)V" }
        })
        {
            string s = sig;
            vm.RegisterNative(c.Name, "<init>", s, i =>
            {
                var st = State(i.Receiver.AsObject()!);
                st.Text = s.Contains("String") ? vm.StringValue(i.Arguments.First(v => v.Tag == JTag.Reference)) : "";
                return JValue.Void;
            });
        }
        string getter = c.Name == "java.awt.Button" ? "getLabel" : "getText";
        string setter = c.Name == "java.awt.Button" ? "setLabel" : "setText";
        vm.RegisterNative(c.Name, getter, "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(State(i.Receiver.AsObject()!).Text)));
        vm.RegisterNative(c.Name, setter, "(Ljava/lang/String;)V", i => { State(i.Receiver.AsObject()!).Text = vm.StringValue(i.Arguments[0]); vm.Repaint(); return JValue.Void; });
        if (c.Name == "java.awt.TextField") vm.RegisterNative(c.Name, "getColumns", "()I", _ => JValue.Int(12));
        if (c.Name is "java.awt.TextField" or "java.awt.TextArea")
        {
            vm.RegisterNative(c.Name, "setEditable", "(Z)V", i => { State(i.Receiver.AsObject()!).Editable = i.Arguments[0].AsInt() != 0; return JValue.Void; });
            vm.RegisterNative(c.Name, "isEditable", "()Z", i => JValue.Int(State(i.Receiver.AsObject()!).Editable ? 1 : 0));
        }
        if (c.Name == "java.awt.TextArea")
        {
            vm.RegisterNative(c.Name, "append", "(Ljava/lang/String;)V", i => { var st = State(i.Receiver.AsObject()!); st.Text += vm.StringValue(i.Arguments[0]); vm.Repaint(); return JValue.Void; });
            vm.RegisterNative(c.Name, "insert", "(Ljava/lang/String;I)V", i => { var st = State(i.Receiver.AsObject()!); st.Text = st.Text.Insert(Math.Clamp(i.Arguments[1].AsInt(), 0, st.Text.Length), vm.StringValue(i.Arguments[0])); vm.Repaint(); return JValue.Void; });
        }
        if (c.Name == "java.awt.Label")
        {
            vm.RegisterNative(c.Name, "setAlignment", "(I)V", _ => JValue.Void);
            vm.RegisterNative(c.Name, "getAlignment", "()I", _ => JValue.Int(0));
            vm.SetStatic(c, "LEFT", "I", JValue.Int(0));
            vm.SetStatic(c, "CENTER", "I", JValue.Int(1));
            vm.SetStatic(c, "RIGHT", "I", JValue.Int(2));
        }
    }

    internal static void RegisterCheckbox(JavaVm vm, JClass c)
    {
        foreach (var sig in new[] { "()V", "(Ljava/lang/String;)V", "(Ljava/lang/String;Z)V" })
        {
            string s = sig;
            vm.RegisterNative(c.Name, "<init>", s, i =>
            {
                var st = State(i.Receiver.AsObject()!);
                if (i.Arguments.Length > 0 && i.Arguments[0].Tag == JTag.Reference) st.Text = vm.StringValue(i.Arguments[0]);
                if (i.Arguments.Length > 1) st.Checked = i.Arguments[1].AsInt() != 0;
                return JValue.Void;
            });
        }
        vm.RegisterNative(c.Name, "getState", "()Z", i => JValue.Int(State(i.Receiver.AsObject()!).Checked ? 1 : 0));
        vm.RegisterNative(c.Name, "setState", "(Z)V", i => { State(i.Receiver.AsObject()!).Checked = i.Arguments[0].AsInt() != 0; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getLabel", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(State(i.Receiver.AsObject()!).Text)));
        vm.RegisterNative(c.Name, "setLabel", "(Ljava/lang/String;)V", i => { State(i.Receiver.AsObject()!).Text = vm.StringValue(i.Arguments[0]); vm.Repaint(); return JValue.Void; });
    }

    internal static void RegisterChoice(JavaVm vm, JClass c)
    {
        foreach (var sig in c.Name == "java.awt.List" ? new[] { "()V", "(I)V" } : new[] { "()V" })
            vm.RegisterNative(c.Name, "<init>", sig, _ => JValue.Void);
        foreach (var n in new[] { "addItem", "add" })
            vm.RegisterNative(c.Name, n, "(Ljava/lang/String;)V", i =>
            {
                var s = State(i.Receiver.AsObject()!);
                s.Items.Add(vm.StringValue(i.Arguments[0]));
                if (s.Selected < 0) s.Selected = 0;
                vm.Repaint();
                return JValue.Void;
            });
        vm.RegisterNative(c.Name, "getItem", "(I)Ljava/lang/String;", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            int idx = i.Arguments[0].AsInt();
            return JValue.Ref(idx >= 0 && idx < s.Items.Count ? vm.CreateString(s.Items[idx]) : null);
        });
        vm.RegisterNative(c.Name, "getItemCount", "()I", i => JValue.Int(State(i.Receiver.AsObject()!).Items.Count));
        vm.RegisterNative(c.Name, "countItems", "()I", i => JValue.Int(State(i.Receiver.AsObject()!).Items.Count));
        vm.RegisterNative(c.Name, "getSelectedItem", "()Ljava/lang/String;", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            return JValue.Ref(s.Selected >= 0 && s.Selected < s.Items.Count ? vm.CreateString(s.Items[s.Selected]) : null);
        });
        vm.RegisterNative(c.Name, "getSelectedIndex", "()I", i => JValue.Int(State(i.Receiver.AsObject()!).Selected));
        vm.RegisterNative(c.Name, "select", "(I)V", i => { var s = State(i.Receiver.AsObject()!); s.Selected = Math.Clamp(i.Arguments[0].AsInt(), -1, s.Items.Count - 1); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "select", "(Ljava/lang/String;)V", i => { var s = State(i.Receiver.AsObject()!); s.Selected = s.Items.IndexOf(vm.StringValue(i.Arguments[0])); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "deselect", "(I)V", _ => JValue.Void);
    }

    internal static void RegisterScrollbar(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { var s = State(i.Receiver.AsObject()!); s.Width = 16; s.Height = 100; _ = i; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(IIIII)V", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            s.ScrollValue = i.Arguments[1].AsInt();
            s.Width = 16; s.Height = 100;
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getValue", "()I", i => JValue.Int(State(i.Receiver.AsObject()!).ScrollValue));
        vm.RegisterNative(c.Name, "setValue", "(I)V", i => { State(i.Receiver.AsObject()!).ScrollValue = i.Arguments[0].AsInt(); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getMinimum", "()I", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "getMaximum", "()I", _ => JValue.Int(100));
        vm.RegisterNative(c.Name, "getVisibleAmount", "()I", _ => JValue.Int(10));
        vm.RegisterNative(c.Name, "setVisibleAmount", "(I)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "setValues", "(IIII)V", i => { State(i.Receiver.AsObject()!).ScrollValue = i.Arguments[0].AsInt(); vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getOrientation", "()I", _ => JValue.Int(1));
        vm.RegisterNative(c.Name, "setUnitIncrement", "(I)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getUnitIncrement", "()I", _ => JValue.Int(1));
        vm.RegisterNative(c.Name, "setBlockIncrement", "(I)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getBlockIncrement", "()I", _ => JValue.Int(10));
        vm.SetStatic(c, "VERTICAL", "I", JValue.Int(1));
        vm.SetStatic(c, "HORIZONTAL", "I", JValue.Int(0));
    }

    public static void RegisterSimpleIntPair(JavaVm vm, JClass c, string a, string b)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JPairState(0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i =>
        {
            var o = i.Receiver.AsObject()!;
            o.NativeState = new JPairState(i.Arguments[0].AsInt(), i.Arguments[1].AsInt());
            vm.SetField(o, c, a, "I", i.Arguments[0]);
            vm.SetField(o, c, b, "I", i.Arguments[1]);
            return JValue.Void;
        });
    }

    public static void RegisterInsets(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JInsetsState(0, 0, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i =>
        {
            var o = i.Receiver.AsObject()!;
            o.NativeState = new JInsetsState(i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            vm.SetField(o, c, "top", "I", i.Arguments[0]);
            vm.SetField(o, c, "left", "I", i.Arguments[1]);
            vm.SetField(o, c, "bottom", "I", i.Arguments[2]);
            vm.SetField(o, c, "right", "I", i.Arguments[3]);
            return JValue.Void;
        });
    }

    public static void RegisterSimpleRect(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JRectState(0, 0, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i =>
        {
            var o = i.Receiver.AsObject()!;
            o.NativeState = new JRectState(0, 0, i.Arguments[0].AsInt(), i.Arguments[1].AsInt());
            vm.SetField(o, c, "width", "I", i.Arguments[0]);
            vm.SetField(o, c, "height", "I", i.Arguments[1]);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i =>
        {
            var o = i.Receiver.AsObject()!;
            o.NativeState = new JRectState(i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt());
            vm.SetField(o, c, "x", "I", i.Arguments[0]);
            vm.SetField(o, c, "y", "I", i.Arguments[1]);
            vm.SetField(o, c, "width", "I", i.Arguments[2]);
            vm.SetField(o, c, "height", "I", i.Arguments[3]);
            return JValue.Void;
        });
    }

    public static void RegisterEvent(JavaVm vm, JClass c)
    {
        // FIX: the old code registered the malformed descriptor "(Ljava/lang/Object;I;Ljava/lang/Object;)V".
        vm.RegisterNative(c.Name, "<init>", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;JIIII)V", i => { InitEvent(vm, i.Receiver.AsObject()!, c, i.Arguments[0], (int)i.Arguments[1].AsLong(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt(), i.Arguments[4].AsInt(), i.Arguments[5].AsInt(), 0, JValue.Ref(null)); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;JIIIIILjava/lang/Object;)V", i => { InitEvent(vm, i.Receiver.AsObject()!, c, i.Arguments[0], (int)i.Arguments[1].AsLong(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt(), i.Arguments[4].AsInt(), i.Arguments[5].AsInt(), i.Arguments[6].AsInt(), i.Arguments[7]); return JValue.Void; });
        vm.SetStatic(c, "SHIFT_MASK", "I", JValue.Int(1));
        vm.SetStatic(c, "CTRL_MASK", "I", JValue.Int(2));
        vm.SetStatic(c, "META_MASK", "I", JValue.Int(4));
        vm.SetStatic(c, "ALT_MASK", "I", JValue.Int(8));
        vm.SetStatic(c, "MOUSE_DOWN", "I", JValue.Int(501));
        vm.SetStatic(c, "MOUSE_UP", "I", JValue.Int(502));
        vm.SetStatic(c, "MOUSE_MOVE", "I", JValue.Int(503));
        vm.SetStatic(c, "MOUSE_DRAG", "I", JValue.Int(506));
        vm.SetStatic(c, "MOUSE_ENTER", "I", JValue.Int(504));
        vm.SetStatic(c, "MOUSE_EXIT", "I", JValue.Int(505));
        vm.SetStatic(c, "KEY_PRESS", "I", JValue.Int(401));
        vm.SetStatic(c, "KEY_RELEASE", "I", JValue.Int(402));
        vm.SetStatic(c, "KEY_ACTION", "I", JValue.Int(403));
        vm.SetStatic(c, "KEY_ACTION_RELEASE", "I", JValue.Int(404));
        vm.SetStatic(c, "WINDOW_DESTROY", "I", JValue.Int(201));
        vm.SetStatic(c, "WINDOW_EXPOSE", "I", JValue.Int(202));
        vm.SetStatic(c, "WINDOW_ICONIFY", "I", JValue.Int(203));
        vm.SetStatic(c, "WINDOW_DEICONIFIED", "I", JValue.Int(204));
        vm.SetStatic(c, "WINDOW_MOVED", "I", JValue.Int(205));
        vm.SetStatic(c, "LIST_SELECT", "I", JValue.Int(701));
        vm.SetStatic(c, "LIST_DESELECT", "I", JValue.Int(702));
        vm.SetStatic(c, "SCROLL_ABSOLUTE", "I", JValue.Int(605));
        vm.SetStatic(c, "SCROLL_LINE_UP", "I", JValue.Int(601));
        vm.SetStatic(c, "SCROLL_LINE_DOWN", "I", JValue.Int(602));
        vm.SetStatic(c, "SCROLL_PAGE_UP", "I", JValue.Int(603));
        vm.SetStatic(c, "SCROLL_PAGE_DOWN", "I", JValue.Int(604));
        vm.SetStatic(c, "ACTION_EVENT", "I", JValue.Int(1001));
        vm.SetStatic(c, "LOAD_FILE", "I", JValue.Int(1002));
        vm.SetStatic(c, "SAVE_FILE", "I", JValue.Int(1003));
        vm.SetStatic(c, "GOT_FOCUS", "I", JValue.Int(1004));
        vm.SetStatic(c, "LOST_FOCUS", "I", JValue.Int(1005));
    }

    private static void InitEvent(JavaVm vm, JObject o, JClass c, JValue target, int when, int id, int x, int y, int key, int modifiers, JValue arg)
    {
        vm.SetField(o, c, "target", "Ljava/lang/Object;", target);
        vm.SetField(o, c, "when", "J", JValue.Long(when));
        vm.SetField(o, c, "id", "I", JValue.Int(id));
        vm.SetField(o, c, "x", "I", JValue.Int(x));
        vm.SetField(o, c, "y", "I", JValue.Int(y));
        vm.SetField(o, c, "key", "I", JValue.Int(key));
        vm.SetField(o, c, "modifiers", "I", JValue.Int(modifiers));
        vm.SetField(o, c, "arg", "Ljava/lang/Object;", arg);
    }

    private static void RegisterListeners(JavaVm vm, JClass c)
    {
        void Reg(string add, string remove, string desc, Func<JavaComponentState, List<JObject>> list)
        {
            vm.RegisterNative(c.Name, add, desc, i =>
            {
                var o = i.Arguments[0].AsObject();
                if (o != null) { var l = list(State(i.Receiver.AsObject()!)); if (!l.Contains(o)) l.Add(o); }
                return JValue.Void;
            });
            vm.RegisterNative(c.Name, remove, desc, i =>
            {
                var o = i.Arguments[0].AsObject();
                if (o != null) list(State(i.Receiver.AsObject()!)).Remove(o);
                return JValue.Void;
            });
        }
        Reg("addMouseListener", "removeMouseListener", "(Ljava/awt/event/MouseListener;)V", s => s.MouseListeners);
        Reg("addMouseMotionListener", "removeMouseMotionListener", "(Ljava/awt/event/MouseMotionListener;)V", s => s.MouseMotionListeners);
        Reg("addKeyListener", "removeKeyListener", "(Ljava/awt/event/KeyListener;)V", s => s.KeyListeners);
        Reg("addActionListener", "removeActionListener", "(Ljava/awt/event/ActionListener;)V", s => s.ActionListeners);
        Reg("addItemListener", "removeItemListener", "(Ljava/awt/event/ItemListener;)V", s => s.ItemListeners);
    }

    // FIX: getSize()/getBounds() etc. now populate the real Java fields too
    // (old code only set NativeState, so `d.width` read as 0).
    private static JValue Pair(JavaVm vm, int a, int b, string type)
    {
        var cls = vm.LoadClass(type);
        var o = new JObject { Class = cls, NativeState = new JPairState(a, b) };
        if (type == "java.awt.Dimension") { vm.SetField(o, cls, "width", "I", JValue.Int(a)); vm.SetField(o, cls, "height", "I", JValue.Int(b)); }
        if (type == "java.awt.Point") { vm.SetField(o, cls, "x", "I", JValue.Int(a)); vm.SetField(o, cls, "y", "I", JValue.Int(b)); }
        return JValue.Ref(o);
    }

    private static JValue Rect(JavaVm vm, JavaComponentState s)
    {
        var cls = vm.LoadClass("java.awt.Rectangle");
        var o = new JObject { Class = cls, NativeState = new JRectState(s.X, s.Y, s.Width, s.Height) };
        vm.SetField(o, cls, "x", "I", JValue.Int(s.X));
        vm.SetField(o, cls, "y", "I", JValue.Int(s.Y));
        vm.SetField(o, cls, "width", "I", JValue.Int(s.Width));
        vm.SetField(o, cls, "height", "I", JValue.Int(s.Height));
        return JValue.Ref(o);
    }

    private static JValue ColorObject(JavaVm vm, Color c) => JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Color"), NativeState = c });

    private static JValue Paint(JavaVm vm, JavaInvocation i)
    {
        var obj = i.Receiver.AsObject()!;
        var s = State(obj);
        if (i.Arguments[0].AsObject()?.NativeState is not JavaGraphicsState gs) return JValue.Void;
        try
        {
            if (s.Background.A != 0)
            {
                using var b = new SolidBrush(s.Background);
                gs.Graphics.FillRectangle(b, 0, 0, Math.Max(1, s.Width), Math.Max(1, s.Height));
            }
            var kind = obj.Class.Name;
            // FIX: do not dispose a cached component font via `using` (old code killed
            // the font for every subsequent paint).
            Font? font = s.Font;
            bool ownsFont = font == null;
            if (font == null) { try { font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular, GraphicsUnit.Pixel); } catch { } }
            try
            {
                using var b = new SolidBrush(s.Foreground);
                using var border = new Pen(Color.Black, 1);
                if (kind == "java.awt.Button" && s.Text.Length > 0)
                {
                    using var face = new Pen(Color.Black, 1);
                    gs.Graphics.DrawRectangle(face, 0, 0, Math.Max(1, s.Width - 1), Math.Max(1, s.Height - 1));
                    if (font != null) gs.Graphics.DrawString(s.Text, font, b, 6, Math.Max(0, (s.Height - 14) / 2));
                }
                else if (kind is "java.awt.Label" && s.Text.Length > 0)
                {
                    if (font != null) gs.Graphics.DrawString(s.Text, font, b, 2, Math.Max(0, (s.Height - 14) / 2));
                }
                else if (kind is "java.awt.TextField" or "java.awt.TextArea")
                {
                    gs.Graphics.DrawRectangle(border, 0, 0, Math.Max(1, s.Width - 1), Math.Max(1, s.Height - 1));
                    if (s.Text.Length > 0 && font != null) gs.Graphics.DrawString(s.Text, font, b, 4, 3);
                }
                else if (kind == "java.awt.Checkbox")
                {
                    using var p = new Pen(s.Foreground, 1);
                    gs.Graphics.DrawRectangle(p, 0, 0, 13, 13);
                    if (s.Checked)
                    {
                        using var p2 = new Pen(s.Foreground, 2);
                        gs.Graphics.DrawLine(p2, 2, 7, 6, 11);
                        gs.Graphics.DrawLine(p2, 6, 11, 12, 2);
                    }
                    if (s.Text.Length > 0 && font != null) gs.Graphics.DrawString(s.Text, font, b, 18, 0);
                }
                else if (kind == "java.awt.Choice")
                {
                    gs.Graphics.DrawRectangle(border, 0, 0, Math.Max(1, s.Width - 1), Math.Max(1, s.Height - 1));
                    var sel = s.Selected >= 0 && s.Selected < s.Items.Count ? s.Items[s.Selected] : "";
                    if (sel.Length > 0 && font != null) gs.Graphics.DrawString(sel, font, b, 4, 3);
                    gs.Graphics.DrawLine(border, Math.Max(1, s.Width - 12), 0, Math.Max(1, s.Width - 12), Math.Max(1, s.Height));
                }
                else if (kind == "java.awt.List")
                {
                    gs.Graphics.DrawRectangle(border, 0, 0, Math.Max(1, s.Width - 1), Math.Max(1, s.Height - 1));
                    if (font != null)
                        for (int k = 0; k < s.Items.Count && k * 14 < s.Height; k++)
                        {
                            if (k == s.Selected) gs.Graphics.FillRectangle(b, 1, 1 + k * 14, Math.Max(1, s.Width - 2), 14);
                            gs.Graphics.DrawString(s.Items[k], font, k == s.Selected ? new SolidBrush(Color.White) : b, 3, 1 + k * 14);
                        }
                }
                else if (kind == "java.awt.Scrollbar")
                {
                    using var track = new SolidBrush(Color.Gray);
                    gs.Graphics.FillRectangle(track, 0, 0, Math.Max(1, s.Width), Math.Max(1, s.Height));
                    using var thumb = new SolidBrush(Color.Silver);
                    gs.Graphics.FillRectangle(thumb, 2, 4, Math.Max(1, s.Width - 4), Math.Max(1, s.Height - 8));
                }
                else if (kind is "java.awt.TextArea" or "java.awt.Canvas" or "java.awt.Panel" or "java.awt.Container" or "java.awt.Component" && s.Text.Length > 0 && font != null)
                {
                    gs.Graphics.DrawString(s.Text, font, b, 3, 3);
                }
            }
            finally { if (ownsFont) font?.Dispose(); }
            PaintChildren(vm, obj, i.Arguments[0]);
        }
        catch (Exception ex) { vm.Print("[AWT paint] " + ex.Message + "\n"); }
        return JValue.Void;
    }

    internal static void PaintChildren(JavaVm vm, JObject parent, JValue graphics)
    {
        var s = State(parent);
        if (graphics.AsObject()?.NativeState is not JavaGraphicsState gs) return;
        foreach (var child in s.Children.ToArray())
        {
            var cs = State(child);
            if (!cs.Visible) continue;
            var save = gs.Graphics.Save();
            try
            {
                gs.Graphics.TranslateTransform(cs.X, cs.Y);
                vm.InvokeVirtual(child, "paint", "(Ljava/awt/Graphics;)V", graphics);
            }
            finally { gs.Graphics.Restore(save); }
        }
    }

    public static bool Dispatch(JavaVm vm, JObject root, JavaInput input)
    {
        var rootState = State(root);
        if (!rootState.Enabled || !rootState.Visible) return false;
        var (target, lx, ly) = HitComponent(root, input.X, input.Y);
        var local = input with { X = lx, Y = ly };
        WidgetBehavior(vm, target, local);            // button clicks, checkbox toggles, ...
        var handled = DispatchListeners(vm, target, local); // 1.1 delegation model
        if (!handled && !HasDelegationListeners(State(target), local.Kind))
            handled = OldModel(vm, target, local);    // 1.0 inheritance model (+ propagation)
        return handled;
    }

    private static bool HasDelegationListeners(JavaComponentState s, JavaInputKind kind) => kind switch
    {
        JavaInputKind.MouseDown or JavaInputKind.MouseUp or JavaInputKind.MouseEnter or JavaInputKind.MouseExit => s.MouseListeners.Count > 0,
        JavaInputKind.MouseMove or JavaInputKind.MouseDrag => s.MouseMotionListeners.Count > 0,
        JavaInputKind.KeyDown or JavaInputKind.KeyUp or JavaInputKind.KeyTyped => s.KeyListeners.Count > 0,
        _ => false
    };

    private static (JObject Target, int X, int Y) HitComponent(JObject root, int x, int y)
    {
        var s = State(root);
        for (int i = s.Children.Count - 1; i >= 0; i--)
        {
            var child = s.Children[i];
            var cs = State(child);
            if (!cs.Visible) continue;
            int cx = x - cs.X, cy = y - cs.Y;
            if (cx >= 0 && cy >= 0 && cx < Math.Max(1, cs.Width) && cy < Math.Max(1, cs.Height))
                return HitComponent(child, cx, cy);
        }
        return (root, x, y);
    }

    private static void WidgetBehavior(JavaVm vm, JObject target, JavaInput local)
    {
        var ts = State(target);
        var name = target.Class.Name;
        if (name == "java.awt.Button" && local.Kind == JavaInputKind.MouseUp && ts.ActionListeners.Count >= 0)
        {
            FireAction(vm, target, ts.Text);
        }
        else if (name == "java.awt.Checkbox" && local.Kind == JavaInputKind.MouseDown)
        {
            ts.Checked = !ts.Checked;
            vm.Repaint();
            var item = MakeItemEvent(vm, target, JValue.Ref(vm.CreateString(ts.Text)), ts.Checked ? 1 : 2);
            foreach (var l in ts.ItemListeners.ToArray()) InvokeSafe(vm, l, "itemStateChanged", "(Ljava/awt/event/ItemEvent;)V", item);
            if (ts.ItemListeners.Count == 0)
            {
                var boolObj = new JObject { Class = vm.LoadClass("java.lang.Boolean"), NativeState = ts.Checked };
                PostOldAction(vm, target, JValue.Ref(boolObj));
            }
        }
        else if ((name == "java.awt.Choice" || name == "java.awt.List") && local.Kind == JavaInputKind.MouseDown && ts.Items.Count > 0)
        {
            int itemHeight = name == "java.awt.List" ? 14 : 16;
            int idx = local.Y / itemHeight;
            if (idx >= 0 && idx < ts.Items.Count && local.Y >= 0)
            {
                ts.Selected = idx;
                vm.Repaint();
                var item = MakeItemEvent(vm, target, JValue.Ref(vm.CreateString(ts.Items[idx])), 1);
                foreach (var l in ts.ItemListeners.ToArray()) InvokeSafe(vm, l, "itemStateChanged", "(Ljava/awt/event/ItemEvent;)V", item);
                if (ts.ItemListeners.Count == 0) PostOldAction(vm, target, JValue.Ref(vm.CreateString(ts.Items[idx])));
            }
        }
        else if ((name is "java.awt.TextField" or "java.awt.TextArea") && local.Kind == JavaInputKind.KeyTyped && ts.Editable)
        {
            char ch = local.KeyChar;
            if (ch == '\b')
            {
                if (ts.Text.Length > 0) ts.Text = ts.Text[..^1];
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (name == "java.awt.TextArea") ts.Text += '\n';
            }
            else if (!char.IsControl(ch))
            {
                ts.Text += ch;
            }
            vm.Repaint();
        }
        else if (name == "java.awt.TextField" && local.Kind == JavaInputKind.KeyDown && (local.KeyCode == 10 || local.KeyChar == '\n'))
        {
            FireAction(vm, target, ts.Text);
        }
    }

    private static void FireAction(JavaVm vm, JObject target, string command)
    {
        var ts = State(target);
        var ev = MakeActionEvent(vm, target, command, 1001);
        foreach (var l in ts.ActionListeners.ToArray()) InvokeSafe(vm, l, "actionPerformed", "(Ljava/awt/event/ActionEvent;)V", ev);
        if (ts.ActionListeners.Count == 0)
            PostOldAction(vm, target, JValue.Ref(vm.CreateString(command)));
    }

    private static void PostOldAction(JavaVm vm, JObject target, JValue arg)
    {
        var eventClass = vm.LoadClass("java.awt.Event");
        var e = new JObject { Class = eventClass, NativeState = null };
        vm.SetField(e, eventClass, "target", "Ljava/lang/Object;", JValue.Ref(target));
        vm.SetField(e, eventClass, "when", "J", JValue.Long(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        vm.SetField(e, eventClass, "id", "I", JValue.Int(1001)); // ACTION_EVENT
        vm.SetField(e, eventClass, "arg", "Ljava/lang/Object;", arg);
        JObject? cur = target;
        while (cur != null)
        {
            if (vm.InvokeVirtual(cur, "handleEvent", "(Ljava/awt/Event;)Z", JValue.Ref(e)).AsInt() != 0) return;
            if (vm.InvokeVirtual(cur, "action", "(Ljava/awt/Event;Ljava/lang/Object;)Z", JValue.Ref(e), arg).AsInt() != 0) return;
            cur = State(cur).Parent;
        }
    }

    private static JObject CreateOldEvent(JavaVm vm, JObject target, JavaInput input)
    {
        var c = vm.LoadClass("java.awt.Event");
        var e = new JObject { Class = c, NativeState = input };
        vm.SetField(e, c, "target", "Ljava/lang/Object;", JValue.Ref(target));
        vm.SetField(e, c, "when", "J", JValue.Long(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        int id = input.Kind switch
        {
            JavaInputKind.MouseDown => 501,
            JavaInputKind.MouseUp => 502,
            JavaInputKind.MouseMove => 503,
            JavaInputKind.MouseEnter => 504,
            JavaInputKind.MouseExit => 505,
            JavaInputKind.MouseDrag => 506,
            JavaInputKind.KeyDown => 401,
            JavaInputKind.KeyUp => 402,
            _ => 0
        };
        vm.SetField(e, c, "id", "I", JValue.Int(id));
        vm.SetField(e, c, "x", "I", JValue.Int(input.X));
        vm.SetField(e, c, "y", "I", JValue.Int(input.Y));
        vm.SetField(e, c, "key", "I", JValue.Int(input.KeyCode != 0 ? input.KeyCode : input.KeyChar));
        int mods = (input.Shift ? 1 : 0) | (input.Control ? 2 : 0) | (input.Alt ? 8 : 0);
        vm.SetField(e, c, "modifiers", "I", JValue.Int(mods));
        return e;
    }

    private static bool OldModel(JavaVm vm, JObject target, JavaInput local)
    {
        var e = CreateOldEvent(vm, target, local);
        JObject? current = target;
        while (current != null)
        {
            var s = State(current);
            if (!s.Enabled) { current = s.Parent; continue; }
            int cx = local.X, cy = local.Y;
            if (!ReferenceEquals(current, target))
            {
                // translate into this ancestor's coordinate system
                var walk = target;
                while (walk != null && !ReferenceEquals(walk, current)) { var ws = State(walk); cx += ws.X; cy += ws.Y; walk = ws.Parent; }
            }
            if (vm.InvokeVirtual(current, "handleEvent", "(Ljava/awt/Event;)Z", JValue.Ref(e)).AsInt() != 0) return true;
            switch (local.Kind)
            {
                case JavaInputKind.MouseDown or JavaInputKind.MouseUp or JavaInputKind.MouseMove
                    or JavaInputKind.MouseDrag or JavaInputKind.MouseEnter or JavaInputKind.MouseExit:
                    {
                        string mn = local.Kind switch
                        {
                            JavaInputKind.MouseDown => "mouseDown",
                            JavaInputKind.MouseUp => "mouseUp",
                            JavaInputKind.MouseDrag => "mouseDrag",
                            JavaInputKind.MouseEnter => "mouseEnter",
                            JavaInputKind.MouseExit => "mouseExit",
                            _ => "mouseMove"
                        };
                        if (vm.InvokeVirtual(current, mn, "(Ljava/awt/Event;II)Z", JValue.Ref(e), JValue.Int(cx), JValue.Int(cy)).AsInt() != 0) return true;
                        break;
                    }
                case JavaInputKind.KeyDown or JavaInputKind.KeyUp:
                    {
                        string mn = local.Kind == JavaInputKind.KeyDown ? "keyDown" : "keyUp";
                        int key = local.KeyCode != 0 ? local.KeyCode : local.KeyChar;
                        if (vm.InvokeVirtual(current, mn, "(Ljava/awt/Event;I)Z", JValue.Ref(e), JValue.Int(key)).AsInt() != 0) return true;
                        break;
                    }
            }
            current = s.Parent;
        }
        return false;
    }

    private static bool DispatchListeners(JavaVm vm, JObject target, JavaInput input)
    {
        var s = State(target);
        bool any = false;
        switch (input.Kind)
        {
            case JavaInputKind.KeyDown:
            {
                var evt = MakeKeyEvent(vm, target, input, 401);
                foreach (var l in s.KeyListeners.ToArray()) { InvokeSafe(vm, l, "keyPressed", "(Ljava/awt/event/KeyEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.KeyUp:
            {
                var evt = MakeKeyEvent(vm, target, input, 402);
                foreach (var l in s.KeyListeners.ToArray()) { InvokeSafe(vm, l, "keyReleased", "(Ljava/awt/event/KeyEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.KeyTyped:
            {
                var evt = MakeKeyEvent(vm, target, input, 400);
                foreach (var l in s.KeyListeners.ToArray()) { InvokeSafe(vm, l, "keyTyped", "(Ljava/awt/event/KeyEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseDown:
            {
                var evt = MakeMouseEvent(vm, target, input, 501);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mousePressed", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseUp:
            {
                var evt = MakeMouseEvent(vm, target, input, 502);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mouseReleased", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                var click = MakeMouseEvent(vm, target, input, 500);
                foreach (var l in s.MouseListeners.ToArray()) InvokeSafe(vm, l, "mouseClicked", "(Ljava/awt/event/MouseEvent;)V", click);
                break;
            }
            case JavaInputKind.MouseEnter:
            {
                var evt = MakeMouseEvent(vm, target, input, 504);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mouseEntered", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseExit:
            {
                var evt = MakeMouseEvent(vm, target, input, 505);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mouseExited", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseMove:
            {
                var evt = MakeMouseEvent(vm, target, input, 503);
                foreach (var l in s.MouseMotionListeners.ToArray()) { InvokeSafe(vm, l, "mouseMoved", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseDrag:
            {
                var evt = MakeMouseEvent(vm, target, input, 506);
                foreach (var l in s.MouseMotionListeners.ToArray()) { InvokeSafe(vm, l, "mouseDragged", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
        }
        return any;
    }

    private static void InvokeSafe(JavaVm vm, JObject listener, string name, string desc, JObject ev)
    {
        try { vm.InvokeVirtual(listener, name, desc, JValue.Ref(ev)); }
        catch (Exception ex) { vm.Print("[AWT] " + ex.Message + "\n"); }
    }

    private static JObject MakeMouseEvent(JavaVm vm, JObject target, JavaInput i, int id) =>
        new() { Class = vm.LoadClass("java.awt.event.MouseEvent"), NativeState = new JavaEventState { Input = i, Id = id, Source = target } };

    private static JObject MakeKeyEvent(JavaVm vm, JObject target, JavaInput i, int id) =>
        new() { Class = vm.LoadClass("java.awt.event.KeyEvent"), NativeState = new JavaEventState { Input = i, Id = id, Source = target } };

    private static JObject MakeActionEvent(JavaVm vm, JObject target, string command, int id) =>
        new() { Class = vm.LoadClass("java.awt.event.ActionEvent"), NativeState = new JavaEventState { Id = id, Source = target, Command = command } };

    private static JObject MakeItemEvent(JavaVm vm, JObject target, JValue item, int stateChange) =>
        new() { Class = vm.LoadClass("java.awt.event.ItemEvent"), NativeState = new JavaEventState { Id = 701, Source = target, Item = item, StateChange = stateChange } };

    public static void RegisterMouseEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJIIIIIZ)V", i =>
        {
            var st = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt() };
            i.Receiver.AsObject()!.NativeState = st;
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getX", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.X ?? 0));
        vm.RegisterNative(c.Name, "getY", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.Y ?? 0));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getClickCount", "()I", _ => JValue.Int(1));
        vm.RegisterNative(c.Name, "getButton", "()I", i => { var b = (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.Button ?? 0; return JValue.Int(b <= 0 ? 1 : b); });
        vm.RegisterNative(c.Name, "getModifiers", "()I", i =>
        {
            var inp = (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input ?? default;
            return JValue.Int((inp.Shift ? 1 : 0) | (inp.Control ? 2 : 0) | (inp.Alt ? 8 : 0));
        });
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.RegisterNative(c.Name, "isPopupTrigger", "()Z", _ => JValue.Int(0));
        vm.SetStatic(c, "MOUSE_CLICKED", "I", JValue.Int(500));
        vm.SetStatic(c, "MOUSE_PRESSED", "I", JValue.Int(501));
        vm.SetStatic(c, "MOUSE_RELEASED", "I", JValue.Int(502));
        vm.SetStatic(c, "MOUSE_MOVED", "I", JValue.Int(503));
        vm.SetStatic(c, "MOUSE_ENTERED", "I", JValue.Int(504));
        vm.SetStatic(c, "MOUSE_EXITED", "I", JValue.Int(505));
        vm.SetStatic(c, "MOUSE_DRAGGED", "I", JValue.Int(506));
        vm.SetStatic(c, "MOUSE_WHEEL", "I", JValue.Int(507));
        vm.SetStatic(c, "NOBUTTON", "I", JValue.Int(0));
        vm.SetStatic(c, "BUTTON1", "I", JValue.Int(1));
        vm.SetStatic(c, "BUTTON2", "I", JValue.Int(2));
        vm.SetStatic(c, "BUTTON3", "I", JValue.Int(3));
    }

    public static void RegisterKeyEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJII)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Input = new JavaInput(JavaInputKind.KeyDown, 0, 0, 0, 0, i.Arguments[4].AsInt(), '\0', false, false, false) };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJIIC)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Input = new JavaInput(JavaInputKind.KeyDown, 0, 0, 0, 0, i.Arguments[4].AsInt(), (char)i.Arguments[5].AsInt(), false, false, false) };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getKeyCode", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.KeyCode != 0 ? (i.Receiver.AsObject()?.NativeState as JavaEventState)!.Input.KeyCode : (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.KeyChar ?? 0));
        vm.RegisterNative(c.Name, "getKeyChar", "()C", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.KeyChar ?? '\0'));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getModifiers", "()I", i =>
        {
            var inp = (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input ?? default;
            return JValue.Int((inp.Shift ? 1 : 0) | (inp.Control ? 2 : 0) | (inp.Alt ? 8 : 0));
        });
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.SetStatic(c, "KEY_TYPED", "I", JValue.Int(400));
        vm.SetStatic(c, "KEY_PRESSED", "I", JValue.Int(401));
        vm.SetStatic(c, "KEY_RELEASED", "I", JValue.Int(402));
        vm.SetStatic(c, "VK_ENTER", "I", JValue.Int(10));
        vm.SetStatic(c, "VK_BACK_SPACE", "I", JValue.Int(8));
        vm.SetStatic(c, "VK_TAB", "I", JValue.Int(9));
        vm.SetStatic(c, "VK_ESCAPE", "I", JValue.Int(27));
        vm.SetStatic(c, "VK_SPACE", "I", JValue.Int(32));
        vm.SetStatic(c, "VK_DELETE", "I", JValue.Int(127));
        vm.SetStatic(c, "VK_SHIFT", "I", JValue.Int(16));
        vm.SetStatic(c, "VK_CONTROL", "I", JValue.Int(17));
        vm.SetStatic(c, "VK_ALT", "I", JValue.Int(18));
        vm.SetStatic(c, "VK_HOME", "I", JValue.Int(36));
        vm.SetStatic(c, "VK_END", "I", JValue.Int(35));
        vm.SetStatic(c, "VK_PAGE_UP", "I", JValue.Int(33));
        vm.SetStatic(c, "VK_PAGE_DOWN", "I", JValue.Int(34));
        vm.SetStatic(c, "VK_LEFT", "I", JValue.Int(37));
        vm.SetStatic(c, "VK_UP", "I", JValue.Int(38));
        vm.SetStatic(c, "VK_RIGHT", "I", JValue.Int(39));
        vm.SetStatic(c, "VK_DOWN", "I", JValue.Int(40));
        vm.SetStatic(c, "VK_COMMA", "I", JValue.Int(44));
        vm.SetStatic(c, "VK_MINUS", "I", JValue.Int(45));
        vm.SetStatic(c, "VK_PERIOD", "I", JValue.Int(46));
        vm.SetStatic(c, "VK_SLASH", "I", JValue.Int(47));
        vm.SetStatic(c, "VK_SEMICOLON", "I", JValue.Int(59));
        vm.SetStatic(c, "VK_EQUALS", "I", JValue.Int(61));
        for (int d = 0; d <= 9; d++) vm.SetStatic(c, "VK_" + d, "I", JValue.Int('0' + d));
        for (char ch = 'A'; ch <= 'Z'; ch++) vm.SetStatic(c, "VK_" + ch, "I", JValue.Int(ch));
        for (int f = 1; f <= 12; f++) vm.SetStatic(c, "VK_F" + f, "I", JValue.Int(111 + f));
    }

    public static void RegisterActionEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;ILjava/lang/String;)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Command = vm.StringValue(i.Arguments[2]) };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getActionCommand", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Command ?? "")));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getModifiers", "()I", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.SetStatic(c, "ACTION_PERFORMED", "I", JValue.Int(1001));
        vm.SetStatic(c, "SHIFT_MASK", "I", JValue.Int(1));
        vm.SetStatic(c, "CTRL_MASK", "I", JValue.Int(2));
        vm.SetStatic(c, "META_MASK", "I", JValue.Int(4));
        vm.SetStatic(c, "ALT_MASK", "I", JValue.Int(8));
    }

    public static void RegisterItemEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;ILjava/lang/Object;I)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Item = i.Arguments[2], StateChange = i.Arguments[3].AsInt() };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getItem", "()Ljava/lang/Object;", i => (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Item ?? JValue.Ref(null));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getStateChange", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.StateChange ?? 0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.SetStatic(c, "ITEM_STATE_CHANGED", "I", JValue.Int(701));
        vm.SetStatic(c, "SELECTED", "I", JValue.Int(1));
        vm.SetStatic(c, "DESELECTED", "I", JValue.Int(2));
    }
}

public sealed class JavaEventState
{
    public JavaInput Input;
    public int Id;
    public JObject? Source;
    public string Command = "";
    public JValue Item = JValue.Void;
    public int StateChange;
}

public readonly record struct JPairState(int A, int B);
public readonly record struct JInsetsState(int Top, int Left, int Bottom, int Right);
public readonly record struct JRectState(int X, int Y, int Width, int Height);