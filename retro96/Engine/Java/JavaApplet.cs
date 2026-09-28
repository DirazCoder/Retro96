using Retro96.Engine.Network;

namespace Retro96.Engine.Java;

// Applet stub state supplied by the host: document/code base, parameters,
// context callbacks and the resize hook.
public sealed class JavaAppletContextState
{
    public Action<string>? Status { get; init; }
    public Action<string>? Navigate { get; init; }
}

public sealed class JavaAppletStubState
{
    public ParsedUrl DocumentBase { get; init; } = null!;
    public ParsedUrl CodeBase { get; init; } = null!;
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // The NAME attribute, kept for cross-applet lookups.
    public string Name = "";
    public JavaAppletContextState Context { get; init; } = new();
    public Action<int, int>? Resize { get; set; }
    public bool Active { get; set; }
}

// Natives for java.applet (Applet, AppletStub, AppletContext, AudioClip)
// and java.net.URL.
internal static class JavaApplet
{
    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.applet.Applet": RegisterApplet(vm, c); break;
            case "java.applet.AppletStub": RegisterAppletStub(vm, c); break;
            case "java.applet.AppletContext": RegisterAppletContext(vm, c); break;
            case "java.applet.AudioClip":
                vm.RegisterNative(c.Name, "play", "()V", _ => JValue.Void);
                vm.RegisterNative(c.Name, "loop", "()V", _ => JValue.Void);
                vm.RegisterNative(c.Name, "stop", "()V", _ => JValue.Void);
                break;
            case "java.net.URL": RegisterUrl(vm, c); break;
        }
    }

    private static JavaAppletNativeState AppletState(JavaVm vm, JObject o) =>
        (JavaAppletNativeState)(o.NativeState ??= new JavaAppletNativeState());

    private static void RegisterApplet(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i =>
        {
            var st = new JavaAppletNativeState();
            i.Receiver.AsObject()!.NativeState = st;
            st.Component.Layout = vm.Construct(vm.LoadClass("java.awt.FlowLayout"));
            return JValue.Void;
        });
        // Lifecycle defaults; user subclasses override these through
        // normal virtual dispatch.
        vm.RegisterNative(c.Name, "init", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "start", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "stop", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "destroy", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
            var name = vm.StringValue(i.Arguments[0]);
            // Name matching is case-insensitive; missing parameters are null.
            return JValue.Ref(st?.Stub?.Parameters.TryGetValue(name, out var s) == true ? vm.CreateString(s) : null);
        });
        vm.RegisterNative(c.Name, "getParameterInfo", "()[[Ljava/lang/String;", _ => JValue.Ref(null));
        vm.RegisterNative(c.Name, "getAppletInfo", "()Ljava/lang/String;", _ => JValue.Ref(null));
        vm.RegisterNative(c.Name, "getCodeBase", "()Ljava/net/URL;", i => JValue.Ref(StubUrl(vm, i, doc: false)));
        vm.RegisterNative(c.Name, "getDocumentBase", "()Ljava/net/URL;", i => JValue.Ref(StubUrl(vm, i, doc: true)));
        vm.RegisterNative(c.Name, "setStub", "(Ljava/applet/AppletStub;)V", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is JavaAppletNativeState st) st.JavaStubObject = i.Arguments[0].AsObject();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getStub", "()Ljava/applet/AppletStub;", i =>
            JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaAppletNativeState)?.JavaStubObject));
        vm.RegisterNative(c.Name, "getAppletContext", "()Ljava/applet/AppletContext;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
            return JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AppletContext"), NativeState = st?.Stub?.Context });
        });
        vm.RegisterNative(c.Name, "isActive", "()Z", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
            return JValue.Int(st is { Active: true } ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "resize", "(II)V", i =>
        {
            ResizeApplet(vm, i.Receiver.AsObject()!, i.Arguments[0].AsInt(), i.Arguments[1].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "resize", "(Ljava/awt/Dimension;)V", i =>
        {
            var dim = DimensionValues(vm, i.Arguments[0].AsObject());
            ResizeApplet(vm, i.Receiver.AsObject()!, dim.Width, dim.Height);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "showStatus", "(Ljava/lang/String;)V", i => { vm.Status(vm.StringValue(i.Arguments[0])); return JValue.Void; });
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i =>
        {
            var u = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            return JValue.Ref(vm.GraphicsFactory.LoadImage(u?.ToAbsolute() ?? ""));
        });
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;Ljava/lang/String;)Ljava/awt/Image;", i =>
        {
            var u = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            var name = vm.StringValue(i.Arguments[1]);
            var abs = u?.Resolve(name).ToAbsolute() ?? name;
            return JValue.Ref(vm.GraphicsFactory.LoadImage(abs));
        });
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;", _ =>
            JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AudioClip") }));
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;Ljava/lang/String;)Ljava/applet/AudioClip;", _ =>
            JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AudioClip") }));
        vm.RegisterNative(c.Name, "play", "(Ljava/net/URL;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "play", "(Ljava/net/URL;Ljava/lang/String;)V", _ => JValue.Void);
    }

    private static void ResizeApplet(JavaVm vm, JObject o, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (o.NativeState is JavaAppletNativeState ap)
        {
            ap.Width = w;
            ap.Height = h;
            ap.Component.Width = w;
            ap.Component.Height = h;
            ap.Stub?.Resize?.Invoke(w, h);
        }
        else
        {
            var s = JavaComponentBridge.State(o);
            s.Width = w;
            s.Height = h;
        }
        vm.Repaint();
    }

    private static (int Width, int Height) DimensionValues(JavaVm vm, JObject? dim)
    {
        if (dim == null) return (0, 0);
        var cls = vm.LoadClass("java.awt.Dimension");
        int w = vm.GetField(dim, cls, "width", "I").AsInt();
        int h = vm.GetField(dim, cls, "height", "I").AsInt();
        return (w, h);
    }

    private static JObject? StubUrl(JavaVm vm, JavaInvocation i, bool doc)
    {
        var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
        var p = doc ? st?.Stub?.DocumentBase : st?.Stub?.CodeBase;
        if (p == null) return null;
        return new JObject { Class = vm.LoadClass("java.net.URL"), NativeState = p };
    }

    private static void RegisterAppletStub(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "isActive", "()Z", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaAppletStubState)?.Active == true ? 1 : 0));
        vm.RegisterNative(c.Name, "getDocumentBase", "()Ljava/net/URL;", i =>
        {
            var p = (i.Receiver.AsObject()?.NativeState as JavaAppletStubState)?.DocumentBase;
            return JValue.Ref(p == null ? null : new JObject { Class = vm.LoadClass("java.net.URL"), NativeState = p });
        });
        vm.RegisterNative(c.Name, "getCodeBase", "()Ljava/net/URL;", i =>
        {
            var p = (i.Receiver.AsObject()?.NativeState as JavaAppletStubState)?.CodeBase;
            return JValue.Ref(p == null ? null : new JObject { Class = vm.LoadClass("java.net.URL"), NativeState = p });
        });
        vm.RegisterNative(c.Name, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaAppletStubState;
            return JValue.Ref(st?.Parameters.TryGetValue(vm.StringValue(i.Arguments[0]), out var value) == true ? vm.CreateString(value) : null);
        });
        vm.RegisterNative(c.Name, "appletResize", "(II)V", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is JavaAppletStubState st) st.Resize?.Invoke(i.Arguments[0].AsInt(), i.Arguments[1].AsInt());
            return JValue.Void;
        });
    }

    private static void RegisterAppletContext(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "showStatus", "(Ljava/lang/String;)V", i =>
        {
            (i.Receiver.AsObject()?.NativeState as JavaAppletContextState)?.Status?.Invoke(vm.StringValue(i.Arguments[0]));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "showDocument", "(Ljava/net/URL;)V", i => ShowDocument(vm, i, null));
        vm.RegisterNative(c.Name, "showDocument", "(Ljava/net/URL;Ljava/lang/String;)V", i => ShowDocument(vm, i, vm.StringValue(i.Arguments[1])));
        vm.RegisterNative(c.Name, "getApplet", "(Ljava/lang/String;)Ljava/applet/Applet;", _ => JValue.Ref(null));
        vm.RegisterNative(c.Name, "getApplets", "()Ljava/util/Enumeration;", i => JValue.Ref(JavaUtil.MakeEnumeration(vm, new List<JValue>())));
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;", _ =>
            JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AudioClip") }));
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i =>
            JValue.Ref(vm.GraphicsFactory.LoadImage((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
    }

    private static JValue ShowDocument(JavaVm vm, JavaInvocation i, string? target)
    {
        var url = (i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute();
        if (url != null)
        {
            // Frame targets other than _self are not supported by the host
            // shell; they still navigate in the current view.
            (i.Receiver.AsObject()?.NativeState as JavaAppletContextState)?.Navigate?.Invoke(url);
        }
        return JValue.Void;
    }

    private static void RegisterUrl(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = ParsedUrl.Parse(vm.StringValue(i.Arguments[0]));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/net/URL;Ljava/lang/String;)V", i =>
        {
            var baseUrl = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            var spec = vm.StringValue(i.Arguments[1]);
            i.Receiver.AsObject()!.NativeState = baseUrl?.Resolve(spec) ?? ParsedUrl.Parse(spec);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
        vm.RegisterNative(c.Name, "toExternalForm", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
        vm.RegisterNative(c.Name, "getProtocol", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Scheme ?? "")));
        vm.RegisterNative(c.Name, "getHost", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Host ?? "")));
        vm.RegisterNative(c.Name, "getFile", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Path ?? "")));
        vm.RegisterNative(c.Name, "getRef", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Fragment ?? "")));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
            JValue.Int(i.Receiver.AsObject()?.NativeState is ParsedUrl a && i.Arguments[0].AsObject()?.NativeState is ParsedUrl b
                && string.Equals(a.ToAbsolute(), b.ToAbsolute(), StringComparison.Ordinal) ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
            JValue.Int((i.Receiver.AsObject()?.NativeState as ParsedUrl)?.ToAbsolute().GetHashCode(StringComparison.Ordinal) ?? 0));
    }
}
