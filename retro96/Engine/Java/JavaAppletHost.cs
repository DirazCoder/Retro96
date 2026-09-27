using System.Collections.Concurrent;
using System.IO.Compression;
using Retro96.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;

namespace Retro96.Engine.Java;

/// <summary>
/// Owns one 1.0/1.1-era JVM per page and the applet instances embedded in the
/// page. Resource loading is page-relative and class loading supports both
/// CODEBASE and ARCHIVE. Rendering is pull-based so it composes naturally with
/// BrowserCanvas/Renderer without creating a native child window.
/// </summary>
public sealed class JavaAppletHost : IDisposable
{
    private sealed class Instance
    {
        public required DomElement Element;
        public required JClass Class;
        public required JObject Object;
        public required JavaVm Vm;
        public required JavaAppletNativeState State;
        public Bitmap? LastFrame;
        public bool Initialized;
        public bool Started;
    }

    private readonly Dictionary<DomElement, Instance> _instances = new();
    private readonly object _gate = new();
    private ParsedUrl? _pageBase;
    private CookieStore? _cookies;
    private ResourceLoader? _resources;
    private bool _disposed;
    public Action? RepaintRequested { get; set; }
    public Action<string>? NavigateRequested { get; set; }
    public Action<string>? StatusChanged { get; set; }

    public async Task PreparePageAsync(DomDocument document, ResourceLoader resources, CancellationToken ct = default)
    {
        if (_disposed) return;
        StopPage();
        _pageBase = document.BaseUrl;
        _cookies = document.Cookies;
        _resources = resources;
        if (_pageBase == null) return;

        // FIX: real 1996 pages write <APPLET> in any case
        var applets = document.ElementDescendants()
            .Where(e => string.Equals(e.TagName, "applet", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var element in applets)
        {
            try { await Task.Run(() => PrepareAppletAsync(element, _pageBase, resources, document.Cookies, ct), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.prepare", ex); }
        }
        RepaintRequested?.Invoke();
    }

    public Bitmap? Resolve(DomElement element, LayoutBox box, bool printRendering)
    {
        if (_disposed || !string.Equals(element.TagName, "applet", StringComparison.OrdinalIgnoreCase)) return null;
        Instance? instance;
        lock (_gate) _instances.TryGetValue(element, out instance);
        if (instance == null || !instance.Initialized) return null;
        try
        {
            int w = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Width > 0 ? box.ContentRect.Width : instance.State.Width));
            int h = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Height > 0 ? box.ContentRect.Height : instance.State.Height));
            instance.State.Width = w; instance.State.Height = h;
            var componentState = JavaComponentBridge.State(instance.Object);
            componentState.Width = w; componentState.Height = h;

            var bitmap = new Bitmap(w, h);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(componentState.Background);
            instance.Vm.HostGraphics = g;
            try
            {
                var graphics = instance.Vm.GraphicsFactory.CreateGraphics(g, w, h);
                if (graphics.NativeState is JavaGraphicsState gs) gs.Background = componentState.Background;
                // Applet's own paint first (user override), then native child painting.
                instance.Vm.InvokeVirtual(instance.Object, "paint", "(Ljava/awt/Graphics;)V", JValue.Ref(graphics));
                JavaComponentBridge.PaintChildren(instance.Vm, instance.Object, JValue.Ref(graphics));
            }
            finally { instance.Vm.HostGraphics = null; }
            var old = instance.LastFrame; instance.LastFrame = bitmap; old?.Dispose();
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
        lock (_gate) { old = _instances.Values.ToArray(); _instances.Clear(); }
        foreach (var i in old)
        {
            try
            {
                if (i.Started) i.Vm.InvokeVirtual(i.Object, "stop", "()V");
                i.State.Active = false;
                if (i.State.Stub != null) i.State.Stub.Active = false;
                if (i.Initialized) i.Vm.InvokeVirtual(i.Object, "destroy", "()V");
            }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.stop", ex); }
            i.LastFrame?.Dispose();
            i.LastFrame = null;
        }
    }

    private async Task PrepareAppletAsync(DomElement element, ParsedUrl pageBase, ResourceLoader resources, CookieStore cookies, CancellationToken ct)
    {
        string code = element.GetAttrOrDefault("code", "").Trim();
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
            try
            {
                var bytes = await FetchBytesAsync(codeBase.Resolve(archive), resources, cookies, ct).ConfigureAwait(false);
                if (bytes != null) ReadArchive(bytes, archiveClasses);
            }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.archive", ex); }
        }

        string codePath = code.Replace('\\', '/').TrimStart('/');
        ParsedUrl codeUrl = codeBase.Resolve(codePath);
        byte[]? mainBytes =
            archiveClasses.TryGetValue(codePath, out var inJar) ? inJar :
            archiveClasses.TryGetValue(Path.GetFileName(codePath), out inJar) ? inJar :
            await FetchBytesAsync(codeUrl, resources, cookies, ct).ConfigureAwait(false);
        if (mainBytes == null) return;

        var stub = new JavaAppletStubState
        {
            DocumentBase = pageBase,
            CodeBase = codeBase,
            Parameters = parameters,
            Context = new JavaAppletContextState { Status = StatusChanged, Navigate = NavigateRequested }
        };

        var classCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in archiveClasses)
        {
            string n = kv.Key.Replace('/', '.');
            if (n.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) n = n[..^6];
            classCache[n] = kv.Value;
        }

        static string? LocalPathFor(ParsedUrl u)
        {
            if (!u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)) return null;
            var raw = Uri.UnescapeDataString(u.Path);
            if (raw.StartsWith("/") && raw.Length > 2 && raw[2] == ':') raw = raw[1..];
            return raw;
        }

        byte[]? LoadClass(string path)
        {
            string name = path.Replace('\\', '/');
            if (name.StartsWith("/")) name = name[1..];
            if (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
            if (classCache.TryGetValue(name.Replace('/', '.'), out var b)) return b;
            var u = codeBase.Resolve(name + ".class");
            var local = LocalPathFor(u);
            if (local != null && File.Exists(local)) return File.ReadAllBytes(local);
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
                if (local != null && File.Exists(local))
                {
                    var bytes = File.ReadAllBytes(local);
                    resourceCache[abs] = bytes;
                    return bytes;
                }
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
            AllowFileAccess = true
        });

        var klass = vm.LoadClassBytes(mainBytes);
        // FIX: run the constructor chain (old code allocated without <init>)
        var obj = vm.Construct(klass);
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
        var stubObject = new JObject { Class = vm.LoadClass("java.applet.AppletStub"), NativeState = stub };
        state.JavaStubObject = stubObject;
        state.Active = false;
        state.Width = Math.Max(1, element.GetAttrInt("width", 300));
        state.Height = Math.Max(1, element.GetAttrInt("height", 200));
        state.Component.Width = state.Width;
        state.Component.Height = state.Height;
        vm.InvokeVirtual(obj, "setStub", "(Ljava/applet/AppletStub;)V", JValue.Ref(stubObject));

        lock (_gate) _instances[element] = new Instance { Element = element, Class = klass, Object = obj, Vm = vm, State = state };
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
            var raw = Uri.UnescapeDataString(url.Path);
            if (raw.StartsWith("/") && raw.Length > 2 && raw[2] == ':') raw = raw[1..];
            if (File.Exists(raw)) return await File.ReadAllBytesAsync(raw, ct).ConfigureAwait(false);
            return null;
        }
        var result = await resources.FetchAsync(url.ToAbsolute(), url, cookies).ConfigureAwait(false);
        return result is HttpSuccess s ? s.Body : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPage();
    }
}