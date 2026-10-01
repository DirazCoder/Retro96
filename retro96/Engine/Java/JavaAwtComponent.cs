using Retro96.Drawing;

namespace Retro96.Engine.Java;

// Per-component host state shared by Component, Container, widgets and
// applets. Mutated from input threads and read on the render thread.
public sealed class JavaComponentState
{
    public int X, Y, Width = 100, Height = 30;
    public bool Visible = true, Enabled = true, Focused;
    public Color Background = Color.LightGray, Foreground = Color.Black;
    public Font? Font;
    public JObject? Parent;
    public readonly List<JObject> Children = new();
    public JObject? Layout;
    public string? LayoutConstraint;
    public string Text = "";
    public bool Editable = true;
    public bool Checked;
    public readonly List<string> Items = new();
    public int Selected = -1;
    public int ScrollValue;
    public readonly List<JObject> MouseListeners = new();
    public readonly List<JObject> MouseMotionListeners = new();
    public readonly List<JObject> KeyListeners = new();
    public readonly List<JObject> FocusListeners = new();
    public readonly List<JObject> ActionListeners = new();
    public readonly List<JObject> ItemListeners = new();
    // Focus tracking: the component inside this tree that owns focus.
    public JObject? FocusedChild;
    // Double-click detection for getClickCount().
    public long LastClickTick;
    public int ClickCount = 1;
}

// Applet-level host state: stub wiring, lifecycle flags and dimensions.
public sealed class JavaAppletNativeState
{
    public JavaAppletStubState? Stub;
    public JObject? JavaStubObject;
    public bool Active;
    public int Width = 300;
    public int Height = 200;
    public JavaComponentState Component = new();
}

public enum JavaLayoutKind { Flow, Border, Grid }

public sealed class JavaLayoutState
{
    public const int Left = 0;
    public JavaLayoutKind Kind;
    public int Alignment, HGap, VGap, Rows, Cols;
    public JavaLayoutState(JavaLayoutKind kind, int alignment, int hGap, int vGap, int rows, int cols)
    { Kind = kind; Alignment = alignment; HGap = hGap; VGap = vGap; Rows = rows; Cols = cols; }
}

// Natives for java.awt.Component, Container, Image, ImageObserver and
// MediaTracker, plus the focus/click-count plumbing used by dispatch.
internal static class JavaAwtComponent
{
    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.awt.Component": RegisterComponent(vm, c); break;
            case "java.awt.Container": RegisterContainer(vm, c); break;
            case "java.awt.Image": RegisterImage(vm, c); break;
            case "java.awt.image.ImageObserver": RegisterImageObserver(vm, c); break;
            case "java.awt.MediaTracker": RegisterMediaTracker(vm, c); break;
        }
    }

    private static void RegisterComponent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = JavaComponentBridge.State(i.Receiver.AsObject()!); return JValue.Void; });
        // paint default does nothing for plain components; the widget
        // subclasses render themselves through JavaComponentBridge.
        vm.RegisterNative(c.Name, "paint", "(Ljava/awt/Graphics;)V", i => JavaComponentBridge.Paint(vm, i));
        // update fills the background then calls paint() virtually, then
        // paints children; applets override it to skip the fill.
        vm.RegisterNative(c.Name, "update", "(Ljava/awt/Graphics;)V", i =>
        {
            var obj = i.Receiver.AsObject()!;
            var s = JavaComponentBridge.State(obj);
            if (i.Arguments[0].AsObject()?.NativeState is not JavaGraphicsState gs) return JValue.Void;
            try
            {
                if (s.Background.A != 0)
                {
                    using var b = new SolidBrush(s.Background);
                    gs.Graphics.FillRectangle(b, 0, 0, Math.Max(1, s.Width), Math.Max(1, s.Height));
                }
                vm.InvokeVirtual(obj, "paint", "(Ljava/awt/Graphics;)V", i.Arguments[0]);
                JavaComponentBridge.PaintChildren(vm, obj, i.Arguments[0]);
            }
            catch (Exception ex) { vm.Print("[AWT update] " + ex.Message + "\n"); }
            return JValue.Void;
        });
        // repaint is asynchronous: it only schedules a host repaint and
        // never calls paint() synchronously from the caller's thread.
        vm.RegisterNative(c.Name, "repaint", "()V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "repaint", "(J)V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "repaint", "(IIII)V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "repaint", "(JIIII)V", i => { vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getGraphics", "()Ljava/awt/Graphics;", i =>
        {
            // Outside a paint pass there is no host surface; a null graphics
            // fails as a catchable NullPointerException instead of a fault.
            if (vm.HostGraphics == null) return JValue.Ref(null);
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            s.Font ??= JavaComponentBridge.DefaultComponentFont();
            return JValue.Ref(vm.GraphicsFactory.CreateGraphics(vm.HostGraphics, s.Width, s.Height, s.X, s.Y));
        });
        vm.RegisterNative(c.Name, "createImage", "(II)Ljava/awt/Image;", i =>
            JValue.Ref(vm.GraphicsFactory.CreateImage(i.Arguments[0].AsInt(), i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "prepareImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z", i =>
        {
            if (i.Arguments[0].AsObject()?.NativeState is JavaImageState st) JavaAwtGraphics.EnsureImageLoaded(vm, st);
            return JValue.Int(1);
        });
        vm.RegisterNative(c.Name, "checkImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)I", i =>
        {
            if (i.Arguments[0].AsObject()?.NativeState is JavaImageState st)
            {
                JavaAwtGraphics.EnsureImageLoaded(vm, st);
                return JValue.Int(st.Error ? 64 : st.Loaded ? 32 : 0); // ALLBITS or ERROR
            }
            return JValue.Int(0);
        });
        vm.RegisterNative(c.Name, "imageUpdate", "(Ljava/awt/Image;IIII)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "setBackground", "(Ljava/awt/Color;)V", i =>
        {
            JavaComponentBridge.State(i.Receiver.AsObject()!).Background = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.LightGray);
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getBackground", "()Ljava/awt/Color;", i =>
            JavaComponentBridge.ColorObject(vm, JavaComponentBridge.State(i.Receiver.AsObject()!).Background));
        vm.RegisterNative(c.Name, "setForeground", "(Ljava/awt/Color;)V", i =>
        {
            JavaComponentBridge.State(i.Receiver.AsObject()!).Foreground = (Color)(i.Arguments[0].AsObject()?.NativeState ?? Color.Black);
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getForeground", "()Ljava/awt/Color;", i =>
            JavaComponentBridge.ColorObject(vm, JavaComponentBridge.State(i.Receiver.AsObject()!).Foreground));
        vm.RegisterNative(c.Name, "setFont", "(Ljava/awt/Font;)V", i =>
        {
            JavaComponentBridge.State(i.Receiver.AsObject()!).Font = JavaAwt.ToFont(JavaAwt.FontState(i.Arguments[0].AsObject()));
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getFont", "()Ljava/awt/Font;", i =>
        {
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            var o = new JObject
            {
                Class = vm.LoadClass("java.awt.Font"),
                NativeState = new JavaFontState { Family = s.Font?.FontFamily.Name ?? "Dialog", Size = (int)(s.Font?.Size ?? 12), Style = ((s.Font?.Style ?? FontStyle.Regular) & FontStyle.Bold) != 0 ? 1 : 0 }
            };
            return JValue.Ref(o);
        });
        vm.RegisterNative(c.Name, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;", i =>
            JValue.Ref(vm.GraphicsFactory.CreateFontMetrics(JavaAwt.ToFont(JavaAwt.FontState(i.Arguments[0].AsObject())))));
        vm.RegisterNative(c.Name, "getFontMetrics", "()Ljava/awt/FontMetrics;", i =>
            JValue.Ref(vm.GraphicsFactory.CreateFontMetrics(JavaComponentBridge.State(i.Receiver.AsObject()!).Font ?? JavaComponentBridge.DefaultComponentFont())));
        vm.RegisterNative(c.Name, "setVisible", "(Z)V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Visible = i.Arguments[0].AsInt() != 0; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "isVisible", "()Z", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Visible ? 1 : 0));
        vm.RegisterNative(c.Name, "show", "()V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Visible = true; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "hide", "()V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Visible = false; vm.Repaint(); return JValue.Void; });
        vm.RegisterNative(c.Name, "enable", "()V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Enabled = true; return JValue.Void; });
        vm.RegisterNative(c.Name, "disable", "()V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Enabled = false; return JValue.Void; });
        vm.RegisterNative(c.Name, "setEnabled", "(Z)V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Enabled = i.Arguments[0].AsInt() != 0; return JValue.Void; });
        vm.RegisterNative(c.Name, "isEnabled", "()Z", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Enabled ? 1 : 0));
        vm.RegisterNative(c.Name, "requestFocus", "()V", i =>
        {
            var o = i.Receiver.AsObject()!;
            JavaAwtEvents.FireFocus(vm, o, gained: true);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "hasFocus", "()Z", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Focused ? 1 : 0));
        // Size and position.
        void SetSize(JavaInvocation i, int w, int h) => JavaComponentBridge.SetSize(vm, i.Receiver.AsObject()!, w, h);
        vm.RegisterNative(c.Name, "setSize", "(II)V", i => { SetSize(i, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "resize", "(II)V", i => { SetSize(i, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setSize", "(Ljava/awt/Dimension;)V", i =>
        {
            var dim = i.Arguments[0].AsObject();
            var cls = vm.LoadClass("java.awt.Dimension");
            SetSize(i, vm.GetField(dim!, cls, "width", "I").AsInt(), vm.GetField(dim!, cls, "height", "I").AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "resize", "(Ljava/awt/Dimension;)V", i =>
        {
            var dim = i.Arguments[0].AsObject();
            var cls = vm.LoadClass("java.awt.Dimension");
            SetSize(i, vm.GetField(dim!, cls, "width", "I").AsInt(), vm.GetField(dim!, cls, "height", "I").AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getSize", "()Ljava/awt/Dimension;", i => JavaComponentBridge.Pair(vm, JavaComponentBridge.State(i.Receiver.AsObject()!), "size"));
        vm.RegisterNative(c.Name, "size", "()Ljava/awt/Dimension;", i => JavaComponentBridge.Pair(vm, JavaComponentBridge.State(i.Receiver.AsObject()!), "size"));
        vm.RegisterNative(c.Name, "preferredSize", "()Ljava/awt/Dimension;", i => JavaComponentBridge.Pair(vm, JavaComponentBridge.State(i.Receiver.AsObject()!), "size"));
        vm.RegisterNative(c.Name, "minimumSize", "()Ljava/awt/Dimension;", i => JavaComponentBridge.Pair(vm, JavaComponentBridge.State(i.Receiver.AsObject()!), "size"));
        vm.RegisterNative(c.Name, "getWidth", "()I", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Width));
        vm.RegisterNative(c.Name, "getHeight", "()I", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Height));
        vm.RegisterNative(c.Name, "setBounds", "(IIII)V", i => JavaComponentBridge.SetBounds(vm, i.Receiver.AsObject()!, i.Arguments[0].AsInt(), i.Arguments[1].AsInt(), i.Arguments[2].AsInt(), i.Arguments[3].AsInt()));
        vm.RegisterNative(c.Name, "getBounds", "()Ljava/awt/Rectangle;", i => JavaComponentBridge.BoundsRect(vm, JavaComponentBridge.State(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "bounds", "()Ljava/awt/Rectangle;", i => JavaComponentBridge.BoundsRect(vm, JavaComponentBridge.State(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "move", "(II)V", i => { var s = JavaComponentBridge.State(i.Receiver.AsObject()!); s.X = i.Arguments[0].AsInt(); s.Y = i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLocation", "(II)V", i => { var s = JavaComponentBridge.State(i.Receiver.AsObject()!); s.X = i.Arguments[0].AsInt(); s.Y = i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLocation", "(Ljava/awt/Point;)V", i =>
        {
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            var pointCls = vm.LoadClass("java.awt.Point");
            s.X = vm.GetField(i.Arguments[0].AsObject()!, pointCls, "x", "I").AsInt();
            s.Y = vm.GetField(i.Arguments[0].AsObject()!, pointCls, "y", "I").AsInt();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "location", "()Ljava/awt/Point;", i => JavaComponentBridge.Pair(vm, JavaComponentBridge.State(i.Receiver.AsObject()!), "location"));
        vm.RegisterNative(c.Name, "getLocation", "()Ljava/awt/Point;", i => JavaComponentBridge.Pair(vm, JavaComponentBridge.State(i.Receiver.AsObject()!), "location"));
        vm.RegisterNative(c.Name, "contains", "(II)Z", i => JavaComponentBridge.Inside(vm, i.Receiver.AsObject()!, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()));
        vm.RegisterNative(c.Name, "inside", "(II)Z", i => JavaComponentBridge.Inside(vm, i.Receiver.AsObject()!, i.Arguments[0].AsInt(), i.Arguments[1].AsInt()));
        vm.RegisterNative(c.Name, "getParent", "()Ljava/awt/Container;", i => JValue.Ref(JavaComponentBridge.State(i.Receiver.AsObject()!).Parent));
        vm.RegisterNative(c.Name, "setCursor", "(Ljava/awt/Cursor;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getCursor", "()Ljava/awt/Cursor;", _ => JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Cursor") }));
        vm.RegisterNative(c.Name, "invalidate", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "validate", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getToolkit", "()Ljava/awt/Toolkit;", _ =>
            JValue.Ref(vm.InvokeStatic("java.awt.Toolkit", "getDefaultToolkit", "()Ljava/awt/Toolkit;").AsObject()));
        vm.RegisterNative(c.Name, "getName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaComponentBridge.State(i.Receiver.AsObject()!).Text)));
        vm.RegisterNative(c.Name, "setName", "(Ljava/lang/String;)V", i => { JavaComponentBridge.State(i.Receiver.AsObject()!).Text = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            return JValue.Ref(vm.CreateString($"{i.Receiver.AsObject()!.Class.Name}[{s.Width},{s.Height},{s.X},{s.Y}]"));
        });
        // 1.0 event model defaults; applet overrides win by virtual dispatch.
        vm.RegisterNative(c.Name, "handleEvent", "(Ljava/awt/Event;)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "postEvent", "(Ljava/awt/Event;)Z", i => JavaComponentBridge.PostEvent(vm, i));
        vm.RegisterNative(c.Name, "action", "(Ljava/awt/Event;Ljava/lang/Object;)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "gotFocus", "(Ljava/awt/Event;Ljava/lang/Object;)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "lostFocus", "(Ljava/awt/Event;Ljava/lang/Object;)Z", _ => JValue.Int(0));
        foreach (var n in new[] { "mouseDown", "mouseUp", "mouseDrag", "mouseMove", "mouseEnter", "mouseExit" })
            vm.RegisterNative(c.Name, n, "(Ljava/awt/Event;II)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "keyDown", "(Ljava/awt/Event;I)Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "keyUp", "(Ljava/awt/Event;I)Z", _ => JValue.Int(0));
        JavaAwtEvents.RegisterListeners(vm, c);
    }

    private static void RegisterContainer(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "add", "(Ljava/awt/Component;)Ljava/awt/Component;", i => JavaComponentBridge.Add(i.Receiver.AsObject()!, i.Arguments[0].AsObject()));
        vm.RegisterNative(c.Name, "add", "(Ljava/lang/String;Ljava/awt/Component;)Ljava/awt/Component;", i => JavaComponentBridge.Add(i.Receiver.AsObject()!, i.Arguments[1].AsObject(), vm.StringValue(i.Arguments[0])));
        vm.RegisterNative(c.Name, "add", "(Ljava/awt/Component;I)Ljava/awt/Component;", i => JavaComponentBridge.Add(i.Receiver.AsObject()!, i.Arguments[0].AsObject(), null));
        vm.RegisterNative(c.Name, "add", "(Ljava/awt/Component;Ljava/lang/Object;)Ljava/awt/Component;", i => JavaComponentBridge.Add(i.Receiver.AsObject()!, i.Arguments[0].AsObject(), i.Arguments[1].Tag == JTag.Reference ? vm.StringValue(i.Arguments[1]) : null));
        vm.RegisterNative(c.Name, "remove", "(Ljava/awt/Component;)V", i =>
        {
            var child = i.Arguments[0].AsObject();
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            if (child != null)
            {
                s.Children.Remove(child);
                JavaComponentBridge.State(child).Parent = null;
            }
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "removeAll", "()V", i =>
        {
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            foreach (var child in s.Children) JavaComponentBridge.State(child).Parent = null;
            s.Children.Clear();
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getComponent", "(I)Ljava/awt/Component;", i =>
        {
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            int idx = i.Arguments[0].AsInt();
            return JValue.Ref(idx >= 0 && idx < s.Children.Count ? s.Children[idx] : null);
        });
        vm.RegisterNative(c.Name, "getComponentCount", "()I", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Children.Count));
        vm.RegisterNative(c.Name, "countComponents", "()I", i => JValue.Int(JavaComponentBridge.State(i.Receiver.AsObject()!).Children.Count));
        vm.RegisterNative(c.Name, "getComponents", "()[Ljava/awt/Component;", i =>
        {
            var s = JavaComponentBridge.State(i.Receiver.AsObject()!);
            var arr = vm.NewArray("Ljava.awt.Component;", s.Children.Count);
            for (int k = 0; k < s.Children.Count; k++) arr.Elements[k] = JValue.Ref(s.Children[k]);
            return JValue.Ref(arr);
        });
        vm.RegisterNative(c.Name, "locate", "(II)Ljava/awt/Component;", i =>
        {
            var (target, _, _) = JavaComponentBridge.HitComponent(i.Receiver.AsObject()!, i.Arguments[0].AsInt(), i.Arguments[1].AsInt());
            return JValue.Ref(ReferenceEquals(target, i.Receiver.AsObject()!) ? null : target);
        });
        vm.RegisterNative(c.Name, "setLayout", "(Ljava/awt/LayoutManager;)V", i =>
        {
            JavaComponentBridge.State(i.Receiver.AsObject()!).Layout = i.Arguments[0].AsObject();
            JavaComponentBridge.LayoutChildren(i.Receiver.AsObject()!);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getLayout", "()Ljava/awt/LayoutManager;", i => JValue.Ref(JavaComponentBridge.State(i.Receiver.AsObject()!).Layout));
        vm.RegisterNative(c.Name, "validate", "()V", i => { JavaComponentBridge.LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "layout", "()V", i => { JavaComponentBridge.LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "doLayout", "()V", i => { JavaComponentBridge.LayoutChildren(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "paintComponents", "(Ljava/awt/Graphics;)V", i => { JavaComponentBridge.PaintChildren(vm, i.Receiver.AsObject()!, i.Arguments[0]); return JValue.Void; });
    }

    private static void RegisterImage(JavaVm vm, JClass c)
    {
        // -1 while unknown; observers registered through the argument are
        // notified when the load completes.
        Func<JavaInvocation, JValue> width = i =>
        {
            if (i.Receiver.AsObject()?.NativeState is not JavaImageState st) return JValue.Int(-1);
            JavaAwtGraphics.EnsureImageLoaded(vm, st);
            return JValue.Int(st.Loaded ? st.Bitmap.Width : -1);
        };
        Func<JavaInvocation, JValue> height = i =>
        {
            if (i.Receiver.AsObject()?.NativeState is not JavaImageState st) return JValue.Int(-1);
            JavaAwtGraphics.EnsureImageLoaded(vm, st);
            return JValue.Int(st.Loaded ? st.Bitmap.Height : -1);
        };
        vm.RegisterNative(c.Name, "getWidth", "(Ljava/awt/image/ImageObserver;)I", width);
        vm.RegisterNative(c.Name, "getHeight", "(Ljava/awt/image/ImageObserver;)I", height);
        vm.RegisterNative(c.Name, "getWidth", "(Ljava/lang/Object;)I", width);
        vm.RegisterNative(c.Name, "getHeight", "(Ljava/lang/Object;)I", height);
        vm.RegisterNative(c.Name, "getSource", "()Ljava/awt/image/ImageProducer;", _ => JValue.Ref(null));
        vm.RegisterNative(c.Name, "getGraphics", "()Ljava/awt/Graphics;", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is not JavaImageState st) return JValue.Ref(null);
            // getGraphics on an offscreen image is the double-buffering
            // entry point.
            var g = Retro96.Drawing.Graphics.FromBitmap(st.Bitmap);
            return JValue.Ref(vm.GraphicsFactory.CreateGraphics(g, st.Bitmap.Width, st.Bitmap.Height));
        });
        vm.RegisterNative(c.Name, "flush", "()V", i =>
        {
            // Discard cached data; URL-backed images reload on next use.
            if (i.Receiver.AsObject()?.NativeState is JavaImageState st && st.SourceUrl != null && !st.Error)
            {
                var old = st.Bitmap;
                st.Bitmap = new Bitmap(1, 1);
                st.Loaded = false;
                old.Dispose();
            }
            return JValue.Void;
        });
    }

    private static void RegisterImageObserver(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "imageUpdate", "(Ljava/awt/Image;IIII)Z", _ => JValue.Int(0));
        vm.SetStatic(c, "WIDTH", "I", JValue.Int(1));
        vm.SetStatic(c, "HEIGHT", "I", JValue.Int(2));
        vm.SetStatic(c, "PROPERTIES", "I", JValue.Int(4));
        vm.SetStatic(c, "SOMEBITS", "I", JValue.Int(8));
        vm.SetStatic(c, "FRAMEBITS", "I", JValue.Int(16));
        vm.SetStatic(c, "ALLBITS", "I", JValue.Int(32));
        vm.SetStatic(c, "ERROR", "I", JValue.Int(64));
        vm.SetStatic(c, "ABORTED", "I", JValue.Int(128));
    }

    private static void RegisterMediaTracker(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;)V", i => { i.Receiver.AsObject()!.NativeState = new List<(JObject image, int id)>(); return JValue.Void; });
        vm.RegisterNative(c.Name, "addImage", "(Ljava/awt/Image;I)V", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)>;
            var img = i.Arguments[0].AsObject();
            if (list != null && img != null) list.Add((img, i.Arguments[1].AsInt()));
            return JValue.Void;
        });
        void WaitOne(JObject img)
        {
            if (img.NativeState is not JavaImageState st || st.Loaded) return;
            var deadline = Environment.TickCount64 + 5000;
            while (!st.Loaded && Environment.TickCount64 < deadline)
            {
                JavaAwtGraphics.EnsureImageLoaded(vm, st);
                if (st.Loaded) break;
                Thread.Sleep(25);
            }
        }
        static List<(JObject Image, int Id)> List(JavaInvocation i) => (i.Receiver.AsObject()?.NativeState as List<(JObject, int)>) ?? new List<(JObject, int)>();
        vm.RegisterNative(c.Name, "waitForID", "(I)V", i =>
        {
            foreach (var (img, id) in List(i).ToArray()) if (id == i.Arguments[0].AsInt()) WaitOne(img);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "waitForAll", "()V", i =>
        {
            foreach (var (img, _) in List(i).ToArray()) WaitOne(img);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "checkID", "(I)Z", i => CheckId(vm, List(i), i.Arguments[0].AsInt(), load: false));
        vm.RegisterNative(c.Name, "checkID", "(IZ)Z", i => CheckId(vm, List(i), i.Arguments[0].AsInt(), i.Arguments[1].AsInt() != 0));
        vm.RegisterNative(c.Name, "checkAll", "()Z", i => CheckAll(vm, List(i), load: false));
        vm.RegisterNative(c.Name, "checkAll", "(Z)Z", i => CheckAll(vm, List(i), i.Arguments[0].AsInt() != 0));
        vm.RegisterNative(c.Name, "isErrorAny", "()Z", i => JValue.Int(List(i).Any(e => (e.Image.NativeState as JavaImageState)?.Error == true) ? 1 : 0));
        vm.RegisterNative(c.Name, "isErrorID", "(I)Z", i =>
            JValue.Int(List(i).Any(e => e.Id == i.Arguments[0].AsInt() && (e.Image.NativeState as JavaImageState)?.Error == true) ? 1 : 0));
        vm.RegisterNative(c.Name, "statusID", "(IZ)I", i => StatusId(vm, List(i), i.Arguments[0].AsInt(), i.Arguments[1].AsInt() != 0));
        vm.RegisterNative(c.Name, "statusAll", "(Z)I", i => StatusAll(vm, List(i), i.Arguments[0].AsInt() != 0));
        vm.SetStatic(c, "LOADING", "I", JValue.Int(1));
        vm.SetStatic(c, "ABORTED", "I", JValue.Int(2));
        vm.SetStatic(c, "ERRORED", "I", JValue.Int(4));
        vm.SetStatic(c, "COMPLETE", "I", JValue.Int(8));
    }

    private static JValue CheckId(JavaVm vm, List<(JObject image, int id)> list, int id, bool load)
    {
        bool all = true;
        foreach (var (image, groupId) in list)
        {
            if (groupId != id) continue;
            if (image.NativeState is JavaImageState st)
            {
                if (load) JavaAwtGraphics.EnsureImageLoaded(vm, st);
                if (!st.Loaded) all = false;
            }
        }
        return JValue.Int(all ? 1 : 0);
    }

    private static JValue CheckAll(JavaVm vm, List<(JObject image, int id)> list, bool load)
    {
        bool all = true;
        foreach (var (image, _) in list)
        {
            if (image.NativeState is JavaImageState st)
            {
                if (load) JavaAwtGraphics.EnsureImageLoaded(vm, st);
                if (!st.Loaded) all = false;
            }
        }
        return JValue.Int(all ? 1 : 0);
    }

    private static JValue StatusId(JavaVm vm, List<(JObject image, int id)> list, int id, bool load)
    {
        bool loaded = true;
        foreach (var (image, groupId) in list)
        {
            if (groupId != id) continue;
            if (image.NativeState is JavaImageState st)
            {
                if (load) JavaAwtGraphics.EnsureImageLoaded(vm, st);
                if (!st.Loaded) loaded = false;
            }
        }
        return JValue.Int(loaded ? 8 : 1); // COMPLETE : LOADING
    }

    private static JValue StatusAll(JavaVm vm, List<(JObject image, int id)> list, bool load)
    {
        bool loaded = true;
        foreach (var (image, _) in list)
        {
            if (image.NativeState is JavaImageState st)
            {
                if (load) JavaAwtGraphics.EnsureImageLoaded(vm, st);
                if (!st.Loaded) loaded = false;
            }
        }
        return JValue.Int(loaded ? 8 : 1);
    }
}
