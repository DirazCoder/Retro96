using Retro96.Engine.Network;
using Retro96.Engine.Js;

namespace Retro96.Engine.Java;

internal sealed record JavaScriptObjectState(JsInterpreter Interpreter, JsObject Target);

// Applet stub state supplied by the host: document/code base, parameters,
// context callbacks and the resize hook.
public sealed class JavaAppletContextState
{
    public Action<string>? Status { get; init; }
    public Action<string>? Navigate { get; init; }
    public Func<string, JObject?>? AppletLookup { get; init; }
    public Func<IReadOnlyList<JObject>>? AppletSnapshot { get; init; }
    public Func<ParsedUrl, CancellationToken, Task<byte[]?>>? AudioLoader { get; init; }
    public Func<IJavaAudioClipPlayer>? AudioPlayerFactory { get; init; }
    public JsInterpreter? ScriptInterpreter { get; set; }
    private readonly object _audioGate = new();
    private readonly List<JavaAudioClipState> _audioClips = new();
    private readonly CancellationTokenSource _lifetime = new();
    internal CancellationToken LifetimeToken => _lifetime.Token;

    internal JavaAudioClipState CreateAudioClip(ParsedUrl? url)
    {
        var clip = new JavaAudioClipState(ct => url == null || AudioLoader == null
            ? Task.FromResult<byte[]?>(null)
            : AudioLoader(url, ct), AudioPlayerFactory);
        lock (_audioGate) _audioClips.Add(clip);
        return clip;
    }

    internal void StopAudioClips()
    {
        _lifetime.Cancel();
        JavaAudioClipState[] clips;
        lock (_audioGate)
        {
            clips = _audioClips.ToArray();
            _audioClips.Clear();
        }
        foreach (var clip in clips) clip.Dispose();
    }
}

public sealed class JavaAppletStubState
{
    public ParsedUrl DocumentBase { get; init; } = null!;
    public ParsedUrl CodeBase { get; init; } = null!;
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // The NAME attribute, kept for cross-applet lookups.
    public string Name = "";
    public JavaAppletContextState Context { get; init; } = new();
    public JsInterpreter? ScriptInterpreter { get; init; }
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
                vm.RegisterNative(c.Name, "play", "()V", i =>
                {
                    (i.Receiver.AsObject()?.NativeState as JavaAudioClipState)?.Play(loop: false);
                    return JValue.Void;
                });
                vm.RegisterNative(c.Name, "loop", "()V", i =>
                {
                    (i.Receiver.AsObject()?.NativeState as JavaAudioClipState)?.Play(loop: true);
                    return JValue.Void;
                });
                vm.RegisterNative(c.Name, "stop", "()V", i =>
                {
                    (i.Receiver.AsObject()?.NativeState as JavaAudioClipState)?.Stop();
                    return JValue.Void;
                });
                break;
            case "java.net.URL": RegisterUrl(vm, c); break;
            case "netscape.javascript.JSObject": RegisterJsObject(vm, c); break;
        }
    }

    private static void RegisterJsObject(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "getWindow", "(Ljava/applet/Applet;)Lnetscape/javascript/JSObject;", i =>
        {
            var appletState = i.Arguments[0].AsObject()?.NativeState as JavaAppletNativeState;
            var interpreter = appletState?.Stub?.ScriptInterpreter
                ?? appletState?.Stub?.Context.ScriptInterpreter;
            if (interpreter == null)
                throw new JvmException(vm.CreateExceptionObject("java.lang.IllegalStateException",
                    "JavaScript is not available for this applet."), 0);
            var window = interpreter.GlobalScope.Get("window");
            if (window.Type is not (JsType.Object or JsType.Function))
                throw new JvmException(vm.CreateExceptionObject("java.lang.IllegalStateException",
                    "The page window is not available."), 0);
            return JValue.Ref(new JObject
            {
                Class = c,
                NativeState = new JavaScriptObjectState(interpreter, window.GetObjectOrFunction()),
                OwnerVm = vm
            });
        });
        vm.RegisterNative(c.Name, "getMember", "(Ljava/lang/String;)Ljava/lang/Object;", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            return JValue.Ref(ToJavaObject(vm, state.Interpreter, state.Target.Get(vm.StringValue(i.Arguments[0]))));
        });
        vm.RegisterNative(c.Name, "setMember", "(Ljava/lang/String;Ljava/lang/Object;)V", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            state.Target.Set(vm.StringValue(i.Arguments[0]),
                ToJavaScriptValue(vm, state.Interpreter, i.Arguments[1]));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "removeMember", "(Ljava/lang/String;)V", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            state.Target.Delete(vm.StringValue(i.Arguments[0]));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getSlot", "(I)Ljava/lang/Object;", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            return JValue.Ref(ToJavaObject(vm, state.Interpreter,
                state.Target.Get(i.Arguments[0].AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture))));
        });
        vm.RegisterNative(c.Name, "setSlot", "(ILjava/lang/Object;)V", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            state.Target.Set(i.Arguments[0].AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ToJavaScriptValue(vm, state.Interpreter, i.Arguments[1]));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "eval", "(Ljava/lang/String;)Ljava/lang/Object;", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            var result = state.Interpreter.EvalString(vm.StringValue(i.Arguments[0]), state.Interpreter.GlobalScope);
            return JValue.Ref(ToJavaObject(vm, state.Interpreter, result));
        });
        vm.RegisterNative(c.Name, "call", "(Ljava/lang/String;[Ljava/lang/Object;)Ljava/lang/Object;", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            string methodName = vm.StringValue(i.Arguments[0]);
            JsValue member = state.Target.Get(methodName);
            if (member.Type != JsType.Function)
                throw new JvmException(vm.CreateExceptionObject("java.lang.IllegalArgumentException",
                    $"JavaScript member '{methodName}' is not callable."), 0);
            var values = i.Arguments[1].AsReference() is JArray array
                ? array.Elements.Select(value => ToJavaScriptValue(vm, state.Interpreter, value)).ToArray()
                : Array.Empty<JsValue>();
            return JValue.Ref(ToJavaObject(vm, state.Interpreter,
                state.Interpreter.CallFunction(member.GetFunction(), JsValue.FromObject(state.Target), values)));
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var state = GetJsObjectState(vm, i.Receiver.AsObject());
            return JValue.Ref(vm.CreateString(state.Target.ToJsString()));
        });
    }

    private static JavaScriptObjectState GetJsObjectState(JavaVm vm, JObject? target) =>
        target?.NativeState as JavaScriptObjectState
        ?? throw new JvmException(vm.CreateExceptionObject("java.lang.IllegalStateException",
            "Invalid JavaScript object handle."), 0);

    private static JObject? ToJavaObject(JavaVm vm, JsInterpreter interpreter, JsValue value)
    {
        switch (value.Type)
        {
            case JsType.Undefined:
            case JsType.Null:
                return null;
            case JsType.String:
                return vm.CreateString(value.GetString());
            case JsType.Boolean:
                return vm.Construct(vm.LoadClass("java.lang.Boolean"), "(Z)V",
                    JValue.Int(value.GetBool() ? 1 : 0));
            case JsType.Number:
                return vm.Construct(vm.LoadClass("java.lang.Double"), "(D)V",
                    JValue.Double(value.GetNumber()));
            default:
                return new JObject
                {
                    Class = vm.LoadClass("netscape.javascript.JSObject"),
                    NativeState = new JavaScriptObjectState(interpreter, value.GetObjectOrFunction()),
                    OwnerVm = vm
                };
        }
    }

    private static JsValue ToJavaScriptValue(JavaVm vm, JsInterpreter interpreter, JValue value) =>
        value.Tag switch
        {
            JTag.Int => JsValue.From(value.AsInt()),
            JTag.Long => JsValue.From((double)value.AsLong()),
            JTag.Float => JsValue.From(value.AsFloat()),
            JTag.Double => JsValue.From(value.AsDouble()),
            JTag.Reference => JavaAppletHost.WrapJavaReference(vm, interpreter, value.AsReference()),
            _ => JsValue.Undefined
        };

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
            return JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AppletContext"), NativeState = st?.Stub?.Context, OwnerVm = vm });
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
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;", i =>
            JValue.Ref(CreateAudioClip(vm, (i.Arguments[0].AsObject()?.NativeState as ParsedUrl),
                (i.Receiver.AsObject()?.NativeState as JavaAppletNativeState)?.Stub?.Context)));
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;Ljava/lang/String;)Ljava/applet/AudioClip;", i =>
        {
            var baseUrl = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            var name = vm.StringValue(i.Arguments[1]);
            return JValue.Ref(CreateAudioClip(vm, baseUrl?.Resolve(name),
                (i.Receiver.AsObject()?.NativeState as JavaAppletNativeState)?.Stub?.Context));
        });
        vm.RegisterNative(c.Name, "play", "(Ljava/net/URL;)V", i =>
        {
            if (CreateAudioClip(vm, i.Arguments[0].AsObject()?.NativeState as ParsedUrl,
                    (i.Receiver.AsObject()?.NativeState as JavaAppletNativeState)?.Stub?.Context)
                .NativeState is JavaAudioClipState clip)
                clip.Play(loop: false);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "play", "(Ljava/net/URL;Ljava/lang/String;)V", i =>
        {
            var url = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            var name = vm.StringValue(i.Arguments[1]);
            if (CreateAudioClip(vm, url?.Resolve(name),
                    (i.Receiver.AsObject()?.NativeState as JavaAppletNativeState)?.Stub?.Context)
                .NativeState is JavaAudioClipState clip)
                clip.Play(loop: false);
            return JValue.Void;
        });
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
        vm.RegisterNative(c.Name, "getApplet", "(Ljava/lang/String;)Ljava/applet/Applet;", i =>
        {
            var context = i.Receiver.AsObject()?.NativeState as JavaAppletContextState;
            var applet = context?.AppletLookup?.Invoke(vm.StringValue(i.Arguments[0]));
            return JValue.Ref(applet);
        });
        vm.RegisterNative(c.Name, "getApplets", "()Ljava/util/Enumeration;", i =>
        {
            var context = i.Receiver.AsObject()?.NativeState as JavaAppletContextState;
            var applets = context?.AppletSnapshot?.Invoke() ?? Array.Empty<JObject>();
            return JValue.Ref(JavaUtil.MakeEnumeration(vm, applets.Select(JValue.Ref).ToList()));
        });
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;", i =>
            JValue.Ref(CreateAudioClip(vm, i.Arguments[0].AsObject()?.NativeState as ParsedUrl,
                i.Receiver.AsObject()?.NativeState as JavaAppletContextState)));
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i =>
            JValue.Ref(vm.GraphicsFactory.LoadImage((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
    }

    private static JObject CreateAudioClip(JavaVm vm, ParsedUrl? url, JavaAppletContextState? context) =>
        new()
        {
            Class = vm.LoadClass("java.applet.AudioClip"),
            NativeState = context?.CreateAudioClip(url),
            OwnerVm = vm
        };

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
