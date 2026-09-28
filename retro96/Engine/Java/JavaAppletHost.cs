using System.Collections.Concurrent;
using System.IO.Compression;
using Retro96.Drawing;
using Retro96.Engine.Dom;
using Retro96.Engine.Layout;
using Retro96.Engine.Network;

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

        foreach (var element in CollectAppletElements(document))
        {
            try { await Task.Run(() => PrepareAppletAsync(element, _pageBase, resources, document.Cookies, ct), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
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
            using var g = Graphics.FromImage(bitmap);
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
        lock (_gate) { old = _instances.Values.ToArray(); _instances.Clear(); }
        foreach (var i in old)
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

    private async Task PrepareAppletAsync(DomElement element, ParsedUrl pageBase, ResourceLoader resources, CookieStore cookies, CancellationToken ct)
    {
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
            try
            {
                var bytes = await FetchBytesAsync(codeBase.Resolve(archive), resources, cookies, ct).ConfigureAwait(false);
                if (bytes != null) ReadArchive(bytes, archiveClasses);
            }
            catch (Exception ex) { Retro96.DebugLog.WriteException("JavaApplet.archive", ex); }
        }

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

        var stub = new JavaAppletStubState
        {
            DocumentBase = pageBase,
            CodeBase = codeBase,
            Parameters = parameters,
            Name = element.GetAttr("name") ?? "",
            Context = new JavaAppletContextState { Status = StatusChanged, Navigate = NavigateRequested }
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
    private static Bitmap? RenderFallback(DomElement element, LayoutBox box)
    {
        int w = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Width));
        int h = Math.Max(1, (int)Math.Ceiling(box.ContentRect.Height));
        if (w <= 1 && h <= 1) return null;
        string? alt = element.GetAttr("alt");
        if (string.IsNullOrWhiteSpace(alt)) alt = element.InnerText?.Trim();
        if (string.IsNullOrWhiteSpace(alt)) return null;
        var bitmap = new Bitmap(w, h);
        using var g = Graphics.FromImage(bitmap);
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
