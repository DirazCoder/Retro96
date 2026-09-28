using Retro96.Drawing;

namespace Retro96.Engine.Java;

// Widget-level AWT plumbing: state access, container children, layouts,
// native widget painting, hit testing and the input dispatch entry point.
internal static class JavaComponentBridge
{
    public static JavaComponentState State(JObject obj)
    {
        if (obj.NativeState is JavaComponentState s) return s;
        if (obj.NativeState is JavaAppletNativeState a) return a.Component;
        return (JavaComponentState)(obj.NativeState = new JavaComponentState());
    }

    internal static Font? DefaultComponentFont()
    {
        try { return new Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { return null; }
    }

    internal static JValue ColorObject(JavaVm vm, Color c) =>
        JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Color"), NativeState = c });

    public static JValue SetSize(JavaVm vm, JObject o, int w, int h)
    {
        var s = State(o);
        s.Width = Math.Max(0, w);
        s.Height = Math.Max(0, h);
        if (o.NativeState is JavaAppletNativeState ap)
        {
            ap.Width = Math.Max(1, w);
            ap.Height = Math.Max(1, h);
            ap.Component.Width = Math.Max(0, w);
            ap.Component.Height = Math.Max(0, h);
            ap.Stub?.Resize?.Invoke(Math.Max(1, w), Math.Max(1, h));
        }
        vm.Repaint();
        return JValue.Void;
    }

    public static JValue SetBounds(JavaVm vm, JObject o, int x, int y, int w, int h)
    {
        var s = State(o);
        s.X = x;
        s.Y = y;
        s.Width = Math.Max(0, w);
        s.Height = Math.Max(0, h);
        if (o.NativeState is JavaAppletNativeState ap)
        {
            ap.Width = Math.Max(1, w);
            ap.Height = Math.Max(1, h);
        }
        return JValue.Void;
    }

    public static JValue Pair(JavaVm vm, JavaComponentState s, string kind)
    {
        if (kind == "location")
        {
            var cls = vm.LoadClass("java.awt.Point");
            var o = new JObject { Class = cls, NativeState = new JRectState(s.X, s.Y, 0, 0) };
            vm.SetField(o, cls, "x", "I", JValue.Int(s.X));
            vm.SetField(o, cls, "y", "I", JValue.Int(s.Y));
            return JValue.Ref(o);
        }
        var dimCls = vm.LoadClass("java.awt.Dimension");
        var dim = new JObject { Class = dimCls, NativeState = new JRectState(0, 0, s.Width, s.Height) };
        vm.SetField(dim, dimCls, "width", "I", JValue.Int(s.Width));
        vm.SetField(dim, dimCls, "height", "I", JValue.Int(s.Height));
        return JValue.Ref(dim);
    }

    public static JValue BoundsRect(JavaVm vm, JavaComponentState s)
    {
        var cls = vm.LoadClass("java.awt.Rectangle");
        var o = new JObject { Class = cls, NativeState = new JRectState(s.X, s.Y, s.Width, s.Height) };
        vm.SetField(o, cls, "x", "I", JValue.Int(s.X));
        vm.SetField(o, cls, "y", "I", JValue.Int(s.Y));
        vm.SetField(o, cls, "width", "I", JValue.Int(s.Width));
        vm.SetField(o, cls, "height", "I", JValue.Int(s.Height));
        return JValue.Ref(o);
    }

    public static JValue Inside(JavaVm vm, JObject o, int x, int y)
    {
        var s = State(o);
        return JValue.Int(x >= 0 && y >= 0 && x < s.Width && y < s.Height ? 1 : 0);
    }

    // postEvent walks the parent chain offering handleEvent to each
    // ancestor, the 1.0 propagation rule.
    public static JValue PostEvent(JavaVm vm, JavaInvocation i)
    {
        var e = i.Arguments[0];
        JObject? cur = i.Receiver.AsObject();
        while (cur != null)
        {
            if (vm.InvokeVirtual(cur, "handleEvent", "(Ljava/awt/Event;)Z", e).AsInt() != 0) return JValue.Int(1);
            cur = State(cur).Parent;
        }
        return JValue.Int(0);
    }

    public static JValue Add(JObject parent, JObject? child, string? constraint = null)
    {
        if (child == null) return JValue.Ref(null);
        var p = State(parent);
        if (!p.Children.Contains(child)) p.Children.Add(child);
        State(child).Parent = parent;
        State(child).LayoutConstraint = constraint;
        LayoutChildren(parent);
        return JValue.Ref(child);
    }

    public static void LayoutChildren(JObject parent)
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
                cs.Width = cw;
                cs.Height = ch;
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
            cs.X = x;
            cs.Y = y;
            cs.Width = cw;
            cs.Height = ch;
            x += cw + gap;
            rowH = Math.Max(rowH, ch);
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
        vm.RegisterNative(c.Name, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;", i => Pair(vm, State(i.Arguments[0].AsObject()!), "size"));
        vm.RegisterNative(c.Name, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;", i => Pair(vm, State(i.Arguments[0].AsObject()!), "size"));
        vm.RegisterNative(c.Name, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "addLayoutComponent", "(Ljava/lang/String;Ljava/lang/Object;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "removeLayoutComponent", "(Ljava/awt/Component;)V", _ => JValue.Void);
    }

    internal static void RegisterTextWidget(JavaVm vm, JClass c)
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
                if (c.Name is "java.awt.TextField" or "java.awt.TextArea" or "java.awt.Button" or "java.awt.Label")
                    st.Height = 24;
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
        foreach (var sig in new[] { "()V", "(Ljava/lang/String;)V", "(Ljava/lang/String;Z)V", "(Ljava/lang/String;Ljava/awt/CheckboxGroup;Z)V" })
        {
            string s = sig;
            vm.RegisterNative(c.Name, "<init>", s, i =>
            {
                var st = State(i.Receiver.AsObject()!);
                if (i.Arguments.Length > 0 && i.Arguments[0].Tag == JTag.Reference) st.Text = vm.StringValue(i.Arguments[0]);
                if (i.Arguments.Length > 1 && i.Arguments[^1].Tag == JTag.Int) st.Checked = i.Arguments[^1].AsInt() != 0;
                st.Height = 18;
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
            vm.RegisterNative(c.Name, "<init>", sig, i => { State(i.Receiver.AsObject()!).Height = c.Name == "java.awt.List" ? Math.Max(3 * 14, 42) : 22; return JValue.Void; });
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
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { var s = State(i.Receiver.AsObject()!); s.Width = 16; s.Height = 100; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(IIIII)V", i =>
        {
            var s = State(i.Receiver.AsObject()!);
            s.ScrollValue = i.Arguments[1].AsInt();
            s.Width = 16;
            s.Height = 100;
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

    public static void RegisterInsets(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { SetInsetFields(vm, i.Receiver.AsObject()!, c, 0, 0, 0, 0); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i => { SetInsetFields(vm, i.Receiver.AsObject()!, c, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()); return JValue.Void; });
    }

    private static void SetInsetFields(JavaVm vm, JObject o, JClass c, int top, int left, int bottom, int right)
    {
        o.NativeState = new JInsetsState(top, left, bottom, right);
        vm.SetField(o, c, "top", "I", JValue.Int(top));
        vm.SetField(o, c, "left", "I", JValue.Int(left));
        vm.SetField(o, c, "bottom", "I", JValue.Int(bottom));
        vm.SetField(o, c, "right", "I", JValue.Int(right));
    }

    // Widget rendering: the native paint for Button/Label/TextField/...
    public static JValue Paint(JavaVm vm, JavaInvocation i)
    {
        var obj = i.Receiver.AsObject()!;
        var s = State(obj);
        if (i.Arguments[0].AsObject()?.NativeState is not JavaGraphicsState gs) return JValue.Void;
        try
        {
            var kind = obj.Class.Name;
            Font? font = s.Font ?? DefaultComponentFont();
            bool ownsFont = s.Font == null;
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
                else if (s.Text.Length > 0 && font != null)
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

    public static (JObject Target, int X, int Y) HitComponent(JObject root, int x, int y)
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

    // Input dispatch: focus changes, click counting, widget behaviour,
    // the 1.1 delegation model and the 1.0 inheritance model.
    public static bool Dispatch(JavaVm vm, JObject root, JavaInput input)
    {
        var rootState = State(root);
        if (!rootState.Enabled || !rootState.Visible) return false;
        var (target, lx, ly) = HitComponent(root, input.X, input.Y);
        var local = input with { X = lx, Y = ly };
        if (input.Kind == JavaInputKind.MouseDown) UpdateFocus(vm, root, target);
        int clickCount = 1;
        if (input.Kind == JavaInputKind.MouseDown)
        {
            var ts = State(target);
            long now = Environment.TickCount64;
            // Double-click window of 500ms, like the AWT.
            clickCount = now - ts.LastClickTick < 500 ? Math.Min(2, ts.ClickCount + 1) : 1;
            ts.ClickCount = clickCount;
            ts.LastClickTick = now;
        }
        WidgetBehavior(vm, target, local);
        var handled = JavaAwtEvents.DispatchListeners(vm, target, local, clickCount);
        if (!handled && !HasDelegationListeners(State(target), local.Kind))
            handled = JavaAwtEvents.OldModel(vm, target, local);
        return handled;
    }

    private static void UpdateFocus(JavaVm vm, JObject root, JObject target)
    {
        var rootState = State(root);
        if (ReferenceEquals(rootState.FocusedChild, target)) return;
        var previous = rootState.FocusedChild;
        rootState.FocusedChild = target;
        if (previous != null)
        {
            State(previous).Focused = false;
            JavaAwtEvents.FireFocus(vm, previous, gained: false);
        }
        State(target).Focused = true;
        JavaAwtEvents.FireFocus(vm, target, gained: true);
    }

    private static bool HasDelegationListeners(JavaComponentState s, JavaInputKind kind) => kind switch
    {
        JavaInputKind.MouseDown or JavaInputKind.MouseUp or JavaInputKind.MouseEnter or JavaInputKind.MouseExit => s.MouseListeners.Count > 0,
        JavaInputKind.MouseMove or JavaInputKind.MouseDrag => s.MouseMotionListeners.Count > 0,
        JavaInputKind.KeyDown or JavaInputKind.KeyUp or JavaInputKind.KeyTyped => s.KeyListeners.Count > 0,
        _ => false
    };

    private static void WidgetBehavior(JavaVm vm, JObject target, JavaInput local)
    {
        var ts = State(target);
        var name = target.Class.Name;
        if (name == "java.awt.Button" && local.Kind == JavaInputKind.MouseUp)
        {
            FireAction(vm, target, ts.Text);
        }
        else if (name == "java.awt.Checkbox" && local.Kind == JavaInputKind.MouseDown)
        {
            ts.Checked = !ts.Checked;
            vm.Repaint();
            var item = JavaAwtEvents.MakeItemEvent(vm, target, JValue.Ref(vm.CreateString(ts.Text)), ts.Checked ? 1 : 2);
            foreach (var l in ts.ItemListeners.ToArray()) JavaAwtEvents.InvokeSafe(vm, l, "itemStateChanged", "(Ljava/awt/event/ItemEvent;)V", item);
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
                var item = JavaAwtEvents.MakeItemEvent(vm, target, JValue.Ref(vm.CreateString(ts.Items[idx])), 1);
                foreach (var l in ts.ItemListeners.ToArray()) JavaAwtEvents.InvokeSafe(vm, l, "itemStateChanged", "(Ljava/awt/event/ItemEvent;)V", item);
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
        var ev = JavaAwtEvents.MakeActionEvent(vm, target, command, 1001);
        foreach (var l in ts.ActionListeners.ToArray()) JavaAwtEvents.InvokeSafe(vm, l, "actionPerformed", "(Ljava/awt/event/ActionEvent;)V", ev);
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
}

public readonly record struct JInsetsState(int Top, int Left, int Bottom, int Right);
