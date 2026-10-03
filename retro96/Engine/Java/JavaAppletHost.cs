using System.Collections.Concurrent;
using System.IO.Compression;
using Retro96.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;
using Retro96.Engine.Js;
using SkiaSharp;

namespace Retro96.Engine.Java;

// Owns one 1.0/1.1-era JVM per applet instance embedded in the page.
// Resource loading is page-relative and class loading supports both
// CODEBASE and ARCHIVE. Rendering is pull-based so it composes naturally
// with BrowserCanvas/Renderer without creating a native child window.
public sealed class JavaAppletHost : IDisposable
{
    private sealed class Instance
    {
        public required DomElement Element;
        public required JClass Class;
        public required JObject Object;
        public required JavaVm Vm;
        public required JavaAppletNativeState State;
        public required DomDocument Document;
        public Bitmap? LastFrame;
        public bool Initialized;
        public bool Started;
    }

    private readonly Dictionary<DomElement, Instance> _instances = new();
    private readonly object _gate = new();
    private readonly Dictionary<DomDocument, JavaAppletContextState> _contexts = new();
    private JsInterpreter? _scriptInterpreter;
    private bool _disposed;
    public Action? RepaintRequested { get; set; }
    public Action<string>? NavigateRequested { get; set; }
    public Action<string>? StatusChanged { get; set; }

    public void SetScriptInterpreter(JsInterpreter interpreter)
    {
        _scriptInterpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
    }

    internal JsValue? ResolveScriptMember(DomElement element, string name, JsInterpreter interpreter)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null || !instance.Initialized) return null;
        return ResolveJavaObjectMember(instance.Vm, instance.Object, name, interpreter);
    }

    internal bool SetScriptMember(DomElement element, string name, JsInterpreter interpreter, JsValue value)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null || !instance.Initialized) return false;
        return SetJavaObjectMember(instance.Vm, instance.Object, name, interpreter, value);
    }

    private static bool SetJavaObjectMember(
        JavaVm vm, JObject target, string name, JsInterpreter interpreter, JsValue value)
    {
        for (JClass? cls = target.Class; cls != null; cls = cls.SuperClass)
        {
            var field = cls.Fields.Values.FirstOrDefault(f =>
                f.Name == name && (f.AccessFlags & JAccess.Public) != 0 &&
                (f.AccessFlags & JAccess.Final) == 0 && !f.IsStatic);
            if (field == null) continue;
            if (!TryConvertArgument(vm, interpreter, field.Descriptor, value, out var converted))
                throw new InvalidOperationException($"Java field '{name}' does not accept this JavaScript value.");
            vm.SetField(target, cls, field.Name, field.Descriptor, converted);
            return true;
        }
        return false;
    }

    private static JsValue? ResolveJavaObjectMember(
        JavaVm vm, JObject target, string name, JsInterpreter interpreter)
    {
        for (JClass? cls = target.Class; cls != null; cls = cls.SuperClass)
        {
            var field = cls.Fields.Values.FirstOrDefault(f =>
                f.Name == name && (f.AccessFlags & JAccess.Public) != 0 && !f.IsStatic);
            if (field != null)
                return ToJavaScript(vm, vm.GetField(target, cls, field.Name, field.Descriptor),
                    interpreter, field.Descriptor);
        }

        var methods = new List<JMethod>();
        for (JClass? cls = target.Class; cls != null; cls = cls.SuperClass)
        {
            methods.AddRange(cls.Methods.Values.Where(m =>
                m.Name == name && !m.IsStatic && !m.IsPrivate &&
                (m.AccessFlags & JAccess.Public) != 0));
        }
        if (methods.Count == 0) return null;

        return JsValue.FromFunction(new JsFunction((_, args) =>
        {
            var candidates = methods
                .Select(method => TryConvertArguments(vm, interpreter, method, args, out var converted)
                    ? (Method: method, Arguments: converted, Score: ArgumentMatchScore(method, args))
                    : default)
                .Where(candidate => candidate.Method != null)
                .OrderBy(candidate => candidate.Score)
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException($"No public Java overload '{name}' accepts {args.Length} argument(s).");

            try
            {
                var result = vm.InvokeVirtual(target, name, candidates[0].Method!.Descriptor, candidates[0].Arguments!);
                return ToJavaScript(vm, result, interpreter, ReturnDescriptor(candidates[0].Method.Descriptor));
            }
            catch (JvmException ex)
            {
                throw new InvalidOperationException($"Java call {name} threw {ex.Object.Class.Name}.");
            }
        }, interpreter.GlobalScope, name));
    }

    private static int ArgumentMatchScore(JMethod method, JsValue[] args)
    {
        var descriptors = JavaDescriptor.Parse(method.Descriptor);
        int score = 0;
        for (int i = 0; i < descriptors.Count && i < args.Length; i++)
        {
            string descriptor = descriptors[i];
            if (descriptor == "Ljava/lang/String;" && args[i].Type == JsType.String) score -= 4;
            else if (descriptor == "Z" && args[i].Type == JsType.Boolean) score -= 3;
            else if (descriptor is "I" or "J" or "F" or "D" && args[i].Type == JsType.Number) score -= 2;
        }
        return score;
    }

    private static bool TryConvertArguments(
        JavaVm vm, JsInterpreter interpreter, JMethod method, JsValue[] args, out JValue[] converted)
    {
        var descriptors = JavaDescriptor.Parse(method.Descriptor);
        converted = Array.Empty<JValue>();
        if (descriptors.Count != args.Length) return false;

        var result = new JValue[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (!TryConvertArgument(vm, interpreter, descriptors[i], args[i], out result[i]))
                return false;
        }
        converted = result;
        return true;
    }

    private static bool TryConvertArgument(
        JavaVm vm, JsInterpreter interpreter, string descriptor, JsValue arg, out JValue result)
    {
        if (descriptor[0] == '[')
        {
            if (arg.Type is JsType.Null or JsType.Undefined)
            {
                result = JValue.Ref(null);
                return true;
            }
            if (arg.Type is not (JsType.Object or JsType.Function))
            {
                result = JValue.Void;
                return false;
            }

            var jsArray = arg.GetObjectOrFunction();
            if (jsArray is JavaScriptArrayProxy arrayProxy)
            {
                result = JValue.Ref(arrayProxy.Value);
                return true;
            }
            if (!int.TryParse(jsArray.Get("length").ToJsString(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int length) ||
                length is < 0 or > 100_000)
            {
                result = JValue.Void;
                return false;
            }
            string component = descriptor[1..];
            var javaArray = vm.NewArray(component, length);
            for (int i = 0; i < length; i++)
            {
                if (!TryConvertArgument(vm, interpreter, component,
                        jsArray.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        out javaArray.Elements[i]))
                {
                    result = JValue.Void;
                    return false;
                }
            }
            result = JValue.Ref(javaArray);
            return true;
        }

        if (descriptor[0] == 'L')
        {
            if (arg.Type is JsType.Null or JsType.Undefined)
                result = JValue.Ref(null);
            else if (descriptor == "Ljava/lang/String;")
                result = JValue.Ref(vm.CreateString(arg.ToJsString()));
            else if ((arg.Type is JsType.Object or JsType.Function) &&
                     arg.GetObjectOrFunction() is JavaScriptObjectProxy proxy)
                result = JValue.Ref(proxy.Value);
            else if ((arg.Type is JsType.Object or JsType.Function) &&
                     descriptor == "Lnetscape/javascript/JSObject;")
            {
                result = JValue.Ref(new JObject
                {
                    Class = vm.LoadClass("netscape.javascript.JSObject"),
                    NativeState = new JavaScriptObjectState(interpreter, arg.GetObjectOrFunction()),
                    OwnerVm = vm
                });
            }
            else
            {
                result = JValue.Void;
                return false;
            }
            return true;
        }

        if (descriptor == "Z")
        {
            result = JValue.Int(arg.ToBoolean() ? 1 : 0);
            return true;
        }
        if (descriptor == "C")
        {
            string text = arg.ToJsString();
            result = JValue.Int(text.Length == 0 ? 0 : text[0]);
            return true;
        }

        double number = arg.ToNumber();
        if (double.IsNaN(number) || double.IsInfinity(number)) number = 0;
        result = descriptor switch
        {
            "B" => JValue.Int(unchecked((sbyte)(int)number)),
            "S" => JValue.Int(unchecked((short)(int)number)),
            "I" => JValue.Int(unchecked((int)number)),
            "J" => JValue.Long(unchecked((long)number)),
            "F" => JValue.Float((float)number),
            "D" => JValue.Double(number),
            _ => JValue.Void
        };
        return result.Tag != JTag.Void;
    }

    private static string ReturnDescriptor(string methodDescriptor) =>
        methodDescriptor[(methodDescriptor.IndexOf(')') + 1)..];

    private static JsValue ToJavaScript(
        JavaVm vm, JValue value, JsInterpreter interpreter, string? descriptor = null)
    {
        if (descriptor == "Z") return JsValue.From(value.AsInt() != 0);
        if (descriptor == "C") return JsValue.From(((char)value.AsInt()).ToString());
        return value.Tag switch
        {
            JTag.Int => JsValue.From(value.AsInt()),
            JTag.Long => JsValue.From((double)value.AsLong()),
            JTag.Float => JsValue.From(value.AsFloat()),
            JTag.Double => JsValue.From(value.AsDouble()),
            JTag.Void => JsValue.Undefined,
            JTag.Reference => ToJavaScriptReference(vm, value.AsReference(), interpreter),
            _ => JsValue.Undefined
        };
    }

    private static JsValue ToJavaScriptReference(JavaVm vm, object? reference, JsInterpreter interpreter)
        => WrapJavaReference(vm, interpreter, reference);

    internal static JsValue WrapJavaReference(JavaVm vm, JsInterpreter interpreter, object? reference)
    {
        if (reference == null) return JsValue.Null;
        if (reference is JArray array)
            return JsValue.FromObject(new JavaScriptArrayProxy(vm, array, interpreter));
        if (reference is JObject javaObject)
        {
            if (javaObject.Class.Name == "java.lang.String")
                return JsValue.From(vm.StringValue(javaObject));
            if (javaObject.NativeState is JavaScriptObjectState jsObject)
                return JsValue.FromObject(jsObject.Target);
            if (javaObject.NativeState is bool boolean) return JsValue.From(boolean);
            if (javaObject.NativeState is sbyte byteValue) return JsValue.From((int)byteValue);
            if (javaObject.NativeState is byte unsignedByte) return JsValue.From((int)unsignedByte);
            if (javaObject.NativeState is short shortValue) return JsValue.From((int)shortValue);
            if (javaObject.NativeState is ushort unsignedShort) return JsValue.From((int)unsignedShort);
            if (javaObject.NativeState is char character) return JsValue.From(character.ToString());
            if (javaObject.NativeState is int intValue) return JsValue.From(intValue);
            if (javaObject.NativeState is long longValue) return JsValue.From((double)longValue);
            if (javaObject.NativeState is float floatValue) return JsValue.From(floatValue);
            if (javaObject.NativeState is double doubleValue) return JsValue.From(doubleValue);
            return JsValue.FromObject(new JavaScriptObjectProxy(vm, javaObject, interpreter));
        }
        return JsValue.Null;
    }

    private sealed class JavaScriptObjectProxy(JavaVm vm, JObject value, JsInterpreter interpreter) : JsObject
    {
        public JObject Value { get; } = value;

        public override JsValue Get(string name) =>
            ResolveJavaObjectMember(vm, Value, name, interpreter) ?? base.Get(name);

        public override void Set(string name, JsValue propertyValue)
        {
            if (SetJavaObjectMember(vm, Value, name, interpreter, propertyValue)) return;
            base.Set(name, propertyValue);
        }
    }

    private sealed class JavaScriptArrayProxy(JavaVm vm, JArray value, JsInterpreter interpreter) : JsObject
    {
        public JArray Value { get; } = value;

        public override JsValue Get(string name)
        {
            if (name == "length") return JsValue.From(Value.Elements.Length);
            if (int.TryParse(name, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                (uint)index < (uint)Value.Elements.Length)
                return ToJavaScript(vm, Value.Elements[index], interpreter, Value.ComponentDescriptor);
            return base.Get(name);
        }

        public override void Set(string name, JsValue propertyValue)
        {
            if (int.TryParse(name, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                (uint)index < (uint)Value.Elements.Length &&
                TryConvertArgument(vm, interpreter, Value.ComponentDescriptor, propertyValue, out var converted))
            {
                Value.Elements[index] = converted;
                return;
            }
            base.Set(name, propertyValue);
        }
    }

    public async Task PreparePageAsync(
        DomDocument document, ResourceLoader resources, CancellationToken ct = default,
        bool resetAllDocuments = true, JsInterpreter? scriptInterpreter = null)
    {
        if (_disposed) return;
        if (resetAllDocuments) StopPage();
        else StopDocument(document);
        ParsedUrl? pageBase = document.BaseUrl;
        bool localPage = pageBase?.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) == true;
        var context = new JavaAppletContextState
        {
            Status = StatusChanged,
            Navigate = NavigateRequested,
            AppletLookup = FindAppletByName,
            AppletSnapshot = SnapshotApplets,
            ScriptInterpreter = scriptInterpreter ?? _scriptInterpreter,
            AudioLoader = (url, token) => LoadAudioBytesAsync(url, resources, document.Cookies, localPage, token)
        };
        lock (_gate) _contexts[document] = context;
        if (pageBase == null) return;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, context.LifetimeToken);
        CancellationToken pageToken = lifetime.Token;
        foreach (var element in CollectAppletElements(document))
        {
            try
            {
                await Task.Run(() => PrepareAppletAsync(element, document, pageBase, resources,
                    document.Cookies, context, context.ScriptInterpreter, pageToken), pageToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { return; }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.prepare", ex); }
        }
        RepaintRequested?.Invoke();
    }

    // <applet> plus the 1996 alternatives: <embed code=...> and
    // <object classid="java:...">.
    private static List<DomElement> CollectAppletElements(DomDocument document)
    {
        var result = new List<DomElement>();
        foreach (var e in document.ElementDescendants())
        {
            if (string.Equals(e.TagName, "applet", StringComparison.OrdinalIgnoreCase)) { result.Add(e); continue; }
            if (string.Equals(e.TagName, "embed", StringComparison.OrdinalIgnoreCase) && e.HasAttr("code")) { result.Add(e); continue; }
            if (string.Equals(e.TagName, "object", StringComparison.OrdinalIgnoreCase))
            {
                var classid = e.GetAttr("classid");
                if (classid != null && classid.StartsWith("java:", StringComparison.OrdinalIgnoreCase)) result.Add(e);
            }
        }
        return result;
    }

    public static bool IsJavaElement(DomElement element) =>
        string.Equals(element.TagName, "applet", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(element.TagName, "embed", StringComparison.OrdinalIgnoreCase) && element.HasAttr("code"))
        || (string.Equals(element.TagName, "object", StringComparison.OrdinalIgnoreCase)
            && element.GetAttr("classid")?.StartsWith("java:", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Paints an applet directly into the active Skia canvas. The live
    /// browser path never allocates a per-paint CPU bitmap for Java content.</summary>
    public bool RenderToCanvas(SKCanvas canvas, DomElement element, LayoutBox box, bool printRendering, GRContext? gpuContext = null)
    {
        if (_disposed || printRendering || !IsJavaElement(element)) return false;

        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null)
        {
            RenderFallbackToCanvas(canvas, element, box);
            return true;
        }

        int w = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Width > 0 ? box.ContentRect.Width : instance.State.Width));
        int h = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Height > 0 ? box.ContentRect.Height : instance.State.Height));
        instance.State.Width = w;
        instance.State.Height = h;

        var componentState = JavaComponentBridge.State(instance.Object);
        componentState.Width = w;
        componentState.Height = h;

        int save = canvas.Save();
        try
        {
            canvas.Translate(box.X, box.Y);
            canvas.ClipRect(SKRect.Create(0, 0, w, h), SKClipOperation.Intersect, antialias: false);
            canvas.Clear(componentState.Background.ToSkColor());

            using var g = Graphics.FromCanvas(canvas, gpuContext);
            g.SetClip(new RectangleF(0, 0, w, h));
            instance.Vm.HostGraphics = g;
            try
            {
                var graphics = instance.Vm.GraphicsFactory.CreateGraphics(g, w, h, gpuContext: gpuContext);
                if (graphics.NativeState is JavaGraphicsState gs) gs.Background = componentState.Background;
                instance.Vm.InvokeVirtual(instance.Object, "update", "(Ljava/awt/Graphics;)V", JValue.Ref(graphics));
            }
            finally
            {
                instance.Vm.HostGraphics = null;
            }
            return true;
        }
        catch (JvmException ex)
        {
            instance.Vm.HostGraphics = null;
            Retro96.DebugLog.Write($"[JAVA] uncaught {ex.Object.Class.Name}: {ex.Object.NativeState}");
            RenderFallbackToCanvas(canvas, element, box);
            return true;
        }
        catch (Exception ex)
        {
            instance.Vm.HostGraphics = null;
            Retro96.DebugLog.WriteException("JavaApplet.paint", ex);
            RenderFallbackToCanvas(canvas, element, box);
            return true;
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    public Bitmap? Resolve(DomElement element, LayoutBox box, bool printRendering)
    {
        if (_disposed || !IsJavaElement(element)) return null;
        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null || !instance.Initialized) return RenderFallback(element, box);
        try
        {
            int w = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Width > 0 ? box.ContentRect.Width : instance.State.Width));
            int h = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Height > 0 ? box.ContentRect.Height : instance.State.Height));
            instance.State.Width = w;
            instance.State.Height = h;
            var componentState = JavaComponentBridge.State(instance.Object);
            componentState.Width = w;
            componentState.Height = h;

            var bitmap = new Bitmap(w, h);
            using var g = Graphics.FromBitmap(bitmap);
            g.Clear(componentState.Background);
            // The applet's own coordinate system starts at the top-left of
            // its area with the clip set to its bounds.
            g.SetClip(new RectangleF(0, 0, w, h));
            instance.Vm.HostGraphics = g;
            try
            {
                var graphics = instance.Vm.GraphicsFactory.CreateGraphics(g, w, h);
                if (graphics.NativeState is JavaGraphicsState gs) gs.Background = componentState.Background;
                // The repaint loop drives update() virtually; the default
                // implementation fills the background and calls paint().
                instance.Vm.InvokeVirtual(instance.Object, "update", "(Ljava/awt/Graphics;)V", JValue.Ref(graphics));
            }
            finally { instance.Vm.HostGraphics = null; }
            var old = instance.LastFrame;
            instance.LastFrame = bitmap;
            old?.Dispose();
            return new Bitmap(bitmap);
        }
        catch (JvmException ex)
        {
            Retro96.DebugLog.Write($"[JAVA] uncaught {ex.Object.Class.Name}: {ex.Object.NativeState}");
            return instance.LastFrame;
        }
        catch (Exception ex)
        {
            Retro96.DebugLog.WriteException("JavaApplet.paint", ex);
            return instance.LastFrame;
        }
    }

    public bool DispatchInput(DomElement element, LayoutBox box, JavaInput input)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null) return false;
        try
        {
            int x = input.X - (int)Math.Round(box.ContentRect.X - box.X);
            int y = input.Y - (int)Math.Round(box.ContentRect.Y - box.Y);
            return JavaComponentBridge.Dispatch(instance.Vm, instance.Object, input with { X = x, Y = y });
        }
        catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.input", ex); return false; }
    }

    public void StartVisible(DomElement element)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null) return;
        if (!instance.Initialized) return;
        if (instance.Started) return;
        try
        {
            instance.Started = true;
            instance.State.Active = true;
            if (instance.State.Stub != null) instance.State.Stub.Active = true;
            instance.Vm.InvokeVirtual(instance.Object, "start", "()V");
        }
        catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.start", ex); }
    }

    public void StopPage()
    {
        Instance[] old;
        JavaAppletContextState[] contexts;
        lock (_gate)
        {
            old = _instances.Values.ToArray();
            contexts = _contexts.Values.ToArray();
            _instances.Clear();
            _contexts.Clear();
        }
        foreach (var context in contexts) context.StopAudioClips();
        StopInstances(old);
    }

    public void StopDocument(DomDocument document)
    {
        Instance[] old;
        JavaAppletContextState? context;
        lock (_gate)
        {
            old = _instances.Values.Where(instance => ReferenceEquals(instance.Document, document)).ToArray();
            foreach (var instance in old) _instances.Remove(instance.Element);
            _contexts.Remove(document, out context);
        }
        context?.StopAudioClips();
        StopInstances(old);
    }

    private static void StopInstances(IEnumerable<Instance> instances)
    {
        foreach (var i in instances)
        {
            try
            {
                if (i.Started) i.Vm.InvokeVirtual(i.Object, "stop", "()V");
                i.State.Active = false;
                if (i.State.Stub != null) i.State.Stub.Active = false;
                JoinAnimationThreads(i.Vm);
                if (i.Initialized) i.Vm.InvokeVirtual(i.Object, "destroy", "()V");
            }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.stop", ex); }
            i.LastFrame?.Dispose();
            i.LastFrame = null;
        }
    }

    private JObject? FindAppletByName(string name)
    {
        lock (_gate)
        {
            return _instances.Values
                .FirstOrDefault(i => string.Equals(i.State.Stub?.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.Object;
        }
    }

    private IReadOnlyList<JObject> SnapshotApplets()
    {
        lock (_gate) return _instances.Values.Select(i => i.Object).ToArray();
    }

    private static async Task<byte[]?> LoadAudioBytesAsync(
        ParsedUrl url, ResourceLoader resources, CookieStore cookies, bool localPage, CancellationToken ct)
    {
        if (url.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            if (!localPage) return null;
            string? path = FileUrls.LocalPathFromFileUrl(url);
            if (path == null || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 16L * 1024 * 1024)
                throw new InvalidDataException("Applet audio resource exceeds the 16 MiB limit.");
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }

        var response = await resources.FetchAsync(url.ToAbsolute(), url, cookies).ConfigureAwait(false);
        if (response is not HttpSuccess success) return null;
        if (success.Body.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Applet audio resource exceeds the 16 MiB limit.");
        return success.Body;
    }

    // stop() is the signal for animation threads to exit; give them a
    // bounded window to actually leave before destroy() runs so a slow
    // thread cannot hang navigation forever.
    private static void JoinAnimationThreads(JavaVm vm)
    {
        foreach (var thread in vm.LiveThreads.Keys.ToArray())
        {
            var host = thread.Thread;
            if (host == null || !host.IsAlive) continue;
            try { host.Join(1500); }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.threadJoin", ex); }
        }
    }

    private async Task PrepareAppletAsync(
        DomElement element, DomDocument document, ParsedUrl pageBase, ResourceLoader resources,
        CookieStore cookies, JavaAppletContextState context, JsInterpreter? scriptInterpreter,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string code = (element.GetAttr("code") ?? element.GetAttr("classid") ?? "").Trim();
        if (code.StartsWith("java:", StringComparison.OrdinalIgnoreCase)) code = code[5..].Trim();
        if (code.Length == 0) return;
        string codebaseAttr = element.GetAttr("codebase")?.Trim() ?? "";
        ParsedUrl codeBase = string.IsNullOrWhiteSpace(codebaseAttr) ? pageBase : pageBase.Resolve(codebaseAttr);

        var parameters = element.Children.OfType<DomElement>()
            .Where(e => string.Equals(e.TagName, "param", StringComparison.OrdinalIgnoreCase) && e.HasAttr("name"))
            .ToDictionary(e => e.GetAttr("name")!, e => e.GetAttr("value") ?? "", StringComparer.OrdinalIgnoreCase);

        var archiveClasses = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string[] archives = (element.GetAttr("archive") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string archive in archives)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await FetchBytesAsync(codeBase.Resolve(archive), resources, cookies, ct).ConfigureAwait(false);
                if (bytes != null) ReadArchive(bytes, archiveClasses);
            }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.archive", ex); }
        }
        ct.ThrowIfCancellationRequested();

        string codePath = code.Replace('\\', '/').TrimStart('/');
        if (codePath.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) codePath = codePath[..^6];
        // The CODE attribute may carry a package path; strip ".class" so the
        // class NAME and the fetch path agree either way.
        byte[]? mainBytes =
            archiveClasses.TryGetValue(codePath.Replace('/', '.'), out var inJar) ? inJar :
            archiveClasses.TryGetValue(codePath, out inJar) ? inJar :
            await FetchBytesAsync(codeBase.Resolve(codePath.Replace('/', '/') + ".class"), resources, cookies, ct).ConfigureAwait(false)
            ?? await FetchBytesAsync(codeBase.Resolve(codePath), resources, cookies, ct).ConfigureAwait(false);
        if (mainBytes == null) return;
        ct.ThrowIfCancellationRequested();

        var stub = new JavaAppletStubState
        {
            DocumentBase = pageBase,
            CodeBase = codeBase,
            Parameters = parameters,
            Name = element.GetAttr("name") ?? "",
            Context = context,
            ScriptInterpreter = scriptInterpreter
        };

        var classCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in archiveClasses)
        {
            string n = kv.Key.Replace('/', '.');
            if (n.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) n = n[..^6];
            classCache[n] = kv.Value;
        }

        // Applet security: the sandbox allows file: resources only when the
        // page itself was loaded from file: (local development); remote
        // applets never touch the local disk.
        bool localPage = pageBase.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);

        // Use the browser-wide file: URL mapper. The Java host used to carry
        // its own simplified conversion here, but canonical Windows URLs are
        // file:///C:/... (three slashes). That helper only stripped the
        // file://C:/ form, so every local .class lookup became
        // "///C:/.../Applet.class", mainBytes stayed null, and Resolve()
        // correctly fell back to the page's "Your browser does not support
        // Java" text even though the class file was present.
        static string? LocalPathFor(ParsedUrl u) =>
            FileUrls.LocalPathFromFileUrl(u);

        byte[]? LoadClass(string path)
        {
            string name = path.Replace('\\', '/');
            if (name.StartsWith("/")) name = name[1..];
            if (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
            if (classCache.TryGetValue(name.Replace('/', '.'), out var b)) return b;
            var u = codeBase.Resolve(name + ".class");
            var local = LocalPathFor(u);
            if (local != null && localPage && File.Exists(local)) return File.ReadAllBytes(local);
            try
            {
                var r = resources.FetchAsync(u.ToAbsolute(), u, cookies).ConfigureAwait(false).GetAwaiter().GetResult();
                return r is HttpSuccess s ? s.Body : null;
            }
            catch { return null; }
        }

        var resourceCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[]? LoadResource(string abs)
        {
            try
            {
                if (resourceCache.TryGetValue(abs, out var cached)) return cached;
                var u = ParsedUrl.Parse(abs);
                var local = LocalPathFor(u);
                if (local != null && localPage && File.Exists(local))
                {
                    var bytes = File.ReadAllBytes(local);
                    resourceCache[abs] = bytes;
                    return bytes;
                }
                if (u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)) return null;
                _ = FetchBytesAsync(u, resources, cookies, CancellationToken.None).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is { Length: > 0 } bytes)
                    {
                        resourceCache[abs] = bytes;
                        RepaintRequested?.Invoke();
                    }
                }, TaskScheduler.Default);
                return null;
            }
            catch { return null; }
        }

        var vm = new JavaVm(new JavaVmOptions
        {
            ClassResolver = LoadClass,
            ResourceResolver = LoadResource,
            Output = s => Retro96.DebugLog.Write("[JAVA] " + s),
            Status = StatusChanged,
            Navigate = NavigateRequested,
            RepaintRequested = RepaintRequested,
            AllowFileAccess = localPage
        });

        var klass = vm.LoadClassBytes(mainBytes);
        var obj = vm.Construct(klass);
        ct.ThrowIfCancellationRequested();
        obj.OwnerVm = vm;
        if (obj.NativeState is not JavaAppletNativeState) obj.NativeState = new JavaAppletNativeState();
        var state = (JavaAppletNativeState)obj.NativeState!;
        state.Stub = stub;
        stub.Resize = (w, h) =>
        {
            state.Width = Math.Max(1, w);
            state.Height = Math.Max(1, h);
            state.Component.Width = state.Width;
            state.Component.Height = state.Height;
            RepaintRequested?.Invoke();
        };
        var stubObject = new JObject { Class = vm.LoadClass("java.applet.AppletStub"), NativeState = stub, OwnerVm = vm };
        state.JavaStubObject = stubObject;
        state.Active = false;
        state.Width = Math.Max(1, element.GetAttrInt("width", 300));
        state.Height = Math.Max(1, element.GetAttrInt("height", 200));
        state.Component.Width = state.Width;
        state.Component.Height = state.Height;
        vm.InvokeVirtual(obj, "setStub", "(Ljava/applet/AppletStub;)V", JValue.Ref(stubObject));

        lock (_gate) _instances[element] = new Instance
        {
            Element = element, Class = klass, Object = obj, Vm = vm, State = state, Document = document
        };
        try { vm.InvokeVirtual(obj, "init", "()V"); }
        catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.init", ex); return; }

        lock (_gate)
        {
            if (_instances.TryGetValue(element, out var ready)) ready.Initialized = true;
        }
        StartVisible(element);
    }

    private static void ReadArchive(byte[] bytes, Dictionary<string, byte[]> target)
    {
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, true);
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.FullName.Contains("..", StringComparison.Ordinal)) continue;
            using var s = e.Open();
            using var dst = new MemoryStream();
            s.CopyTo(dst);
            target[e.FullName.Replace('\\', '/')] = dst.ToArray();
        }
    }

    private static async Task<byte[]?> FetchBytesAsync(ParsedUrl url, ResourceLoader resources, CookieStore cookies, CancellationToken ct)
    {
        if (url.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            string? localPath = FileUrls.LocalPathFromFileUrl(url);
            if (localPath != null && File.Exists(localPath))
                return await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
            return null;
        }

        var result = await resources.FetchAsync(url.ToAbsolute(), url, cookies).ConfigureAwait(false);
        return result is HttpSuccess s ? s.Body : null;
    }

    // Alt/fallback rendering: the ALT attribute (or the element's inline
    // fallback text) drawn on the classic gray placeholder when the applet
    // cannot run.
    private static void RenderFallbackToCanvas(SKCanvas canvas, DomElement element, LayoutBox box)
    {
        int w = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Width > 0 ? box.ContentRect.Width : box.Width));
        int h = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Height > 0 ? box.ContentRect.Height : box.Height));
        if (w <= 1 || h <= 1) return;
        string? alt = element.GetAttr("alt");
        if (string.IsNullOrWhiteSpace(alt)) alt = element.InnerText?.Trim();

        int save = canvas.Save();
        try
        {
            canvas.Translate(box.X, box.Y);
            canvas.ClipRect(SKRect.Create(0, 0, w, h), SKClipOperation.Intersect, antialias: false);
            using var bg = new SKPaint { Color = new SKColor(0xC0, 0xC0, 0xC0, 0xFF), IsAntialias = false };
            using var border = new SKPaint { Color = SKColors.Gray, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            canvas.DrawRect(SKRect.Create(0, 0, w, h), bg);
            canvas.DrawRect(SKRect.Create(0.5f, 0.5f, Math.Max(0, w - 1f), Math.Max(0, h - 1f)), border);
            if (!string.IsNullOrWhiteSpace(alt))
            {
                using var typeface = SKTypeface.FromFamilyName("Arial");
                using var font = new SKFont(typeface, 12f) { Edging = SKFontEdging.Antialias };
                using var fg = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                float baseline = Math.Clamp((h + 12f) * 0.5f, 12f, Math.Max(12f, h - 2f));
                canvas.DrawText(alt, 6f, baseline, SKTextAlign.Left, font, fg);
            }
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    private static Bitmap? RenderFallback(DomElement element, LayoutBox box)
    {
        int w = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Width));
        int h = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Height));
        if (w <= 1 && h <= 1) return null;
        string? alt = element.GetAttr("alt");
        if (string.IsNullOrWhiteSpace(alt)) alt = element.InnerText?.Trim();
        if (string.IsNullOrWhiteSpace(alt)) return null;
        var bitmap = new Bitmap(w, h);
        using var g = Graphics.FromBitmap(bitmap);
        using var bg = new SolidBrush(Color.FromArgb(0xC0, 0xC0, 0xC0));
        g.FillRectangle(bg, 0, 0, w, h);
        using var border = new Pen(Color.Gray, 1);
        g.DrawRectangle(border, 0, 0, Math.Max(1, w - 1), Math.Max(1, h - 1));
        using var font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Color.Black);
        g.DrawString(alt, font, fg, 6, Math.Max(2, (h - 14) / 2));
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPage();
    }
}
