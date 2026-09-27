using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Retro96.Drawing;
using Retro96.Engine.Network;

namespace Retro96.Engine.Java;

public enum JTag { Int, Long, Float, Double, Reference, Void }

/// <summary>A single JVM value. Category-2 payloads carry all bits in <see cref="Bits"/>.</summary>
public readonly struct JValue
{
    public readonly JTag Tag;
    public readonly long Bits;
    public readonly object? RefValue;
    private JValue(JTag tag, long bits = 0, object? @ref = null) { Tag = tag; Bits = bits; RefValue = @ref; }
    public static JValue Int(int v) => new(JTag.Int, v);
    public static JValue Long(long v) => new(JTag.Long, v);
    public static JValue Float(float v) => new(JTag.Float, BitConverter.SingleToInt32Bits(v));
    public static JValue Double(double v) => new(JTag.Double, BitConverter.DoubleToInt64Bits(v));
    public static JValue Ref(object? v) => new(JTag.Reference, @ref: v);
    public static JValue Void => new(JTag.Void);
    public int AsInt() => unchecked((int)Bits);
    public long AsLong() => Bits;
    public float AsFloat() => BitConverter.Int32BitsToSingle(unchecked((int)Bits));
    public double AsDouble() => BitConverter.Int64BitsToDouble(Bits);
    public JObject? AsObject() => RefValue as JObject;
    public JArray? AsArray() => RefValue as JArray;
    public object? AsReference() => RefValue;
}

public sealed class JObject
{
    public required JClass Class { get; init; }
    public readonly Dictionary<JFieldKey, JValue> Fields = new();
    public object? NativeState { get; set; }
    public override string ToString() => Class.Name;
}

public readonly record struct JFieldKey(JClass DeclaringClass, string Name, string Descriptor);

public sealed class JArray
{
    public required JClass Class { get; init; }
    /// <summary>Component descriptor, normalized to '.'-separated names (e.g. "I", "[I", "Ljava.lang.String;").</summary>
    public string ComponentDescriptor { get; init; } = "";
    public JValue[] Elements { get; init; } = Array.Empty<JValue>();
}

public sealed class JClass
{
    public required string Name { get; init; }
    public JClass? SuperClass { get; set; }
    public readonly List<JClass> Interfaces = new();
    public JClassFile? File { get; init; }
    public bool IsBuiltin { get; init; }
    public bool Initialized; // set before <clinit> runs to break recursion
    public JObject? ClassObject { get; set; }
    public readonly Dictionary<(string Name, string Descriptor), JMethod> Methods = new();
    public readonly Dictionary<(string Name, string Descriptor), JField> Fields = new();
    public readonly Dictionary<string, JValue> StaticFields = new(StringComparer.Ordinal);
    public readonly Dictionary<(string Name, string Descriptor), JMethod?> VTable = new();

    public bool IsAssignableTo(JClass target)
    {
        for (JClass? c = this; c != null; c = c.SuperClass)
        {
            if (ReferenceEquals(c, target) || c.Name.Equals(target.Name, StringComparison.Ordinal)) return true;
            foreach (var i in c.Interfaces)
                if (i.IsAssignableTo(target)) return true;
        }
        return false;
    }
}

public sealed class JvmException : Exception
{
    public JObject Object { get; }
    public int ThrowPc { get; }
    public JvmException(JObject obj, int pc, string? message = null) : base(message) { Object = obj; ThrowPc = pc; }
}

public sealed class JavaFrame
{
    public required JMethod Method { get; init; }
    /// <summary>The class that DECLARES the method — its File owns the constant pool
    /// this frame's operands resolve against (see JavaInterpreter).</summary>
    public required JClass Owner { get; init; }
    public JValue[] Locals { get; init; } = Array.Empty<JValue>();
    public JValue[] Stack { get; init; } = Array.Empty<JValue>();
    public int Sp;
    public int Pc;
    public int LastOpcodePc;

    public void Push(JValue value)
    {
        if (Sp >= Stack.Length) throw new InvalidOperationException("JVM operand stack overflow.");
        Stack[Sp++] = value;
    }
    public JValue Pop()
    {
        if (Sp <= 0) throw new InvalidOperationException("JVM operand stack underflow.");
        return Stack[--Sp];
    }
    public JValue Peek() => Sp <= 0 ? throw new InvalidOperationException("JVM operand stack underflow.") : Stack[Sp - 1];
    public void ClearStack() => Sp = 0;
}

public sealed class JavaInvocation
{
    public required JClass Owner { get; init; }
    public required JMethod Method { get; init; }
    public JValue Receiver { get; init; }
    public JValue[] Arguments { get; init; } = Array.Empty<JValue>();
}

public sealed class JavaVmOptions
{
    public Func<string, byte[]?>? ClassResolver { get; init; }
    public Func<string, byte[]?>? ResourceResolver { get; init; }
    public Action<string>? Output { get; init; }
    public Action<string>? Status { get; init; }
    public Action? RepaintRequested { get; init; }
    public Action<string>? Navigate { get; init; }
    public bool AllowFileAccess { get; init; }
}

// NOTE: must be `partial` — JavaInterpreter.cs declares the other half.
// (The original declared this class non-partial next to a partial part,
// which does not compile.)
public sealed partial class JavaVm
{
    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    { "boolean","byte","char","short","int","long","float","double","void","B","Z","C","S","I","J","F","D","V" };

    private readonly JavaVmOptions _options;
    // FIX: plain Dictionaries mutated from applet animation threads raced with
    // reads on the UI thread; these are now concurrent.
    private readonly ConcurrentDictionary<string, JClass> _classes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Owner, string Name, string Descriptor), Func<JavaInvocation, JValue>> _natives = new();
    private readonly ConcurrentDictionary<(string, string, string), byte> _warnedNatives = new();
    private readonly object _lock = new();

    public JavaGraphicsFactory GraphicsFactory { get; }
    internal Retro96.Drawing.Graphics? HostGraphics { get; set; }

    public JavaVm(JavaVmOptions options)
    {
        _options = options;
        GraphicsFactory = new JavaGraphicsFactory(this);
        BuiltinJavaLibrary.Register(this);
    }

    public IReadOnlyDictionary<string, JClass> Classes => _classes;
    internal void RegisterClass(JClass cls) => _classes[cls.Name] = cls;
    public void RegisterNative(string owner, string name, string desc, Func<JavaInvocation, JValue> fn) => _natives[(owner, name, desc)] = fn;
    internal byte[]? ResolveResourceBytes(string absoluteUrl) => _options.ResourceResolver?.Invoke(absoluteUrl);
    public void Print(string text) => _options.Output?.Invoke(text);
    public void Status(string text) => _options.Status?.Invoke(text);
    public void Repaint() => _options.RepaintRequested?.Invoke();
    public void Navigate(string url) => _options.Navigate?.Invoke(url);

    // -----------------------------------------------------------------
    // class loading & linking
    // -----------------------------------------------------------------

    public JClass LoadClass(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new InvalidDataException("Empty class name.");
        name = name.Replace('/', '.'); // canonical form; also normalizes descriptors like "[Ljava/lang/String;"
        if (_classes.TryGetValue(name, out var existing)) return existing;
        if (name[0] == '[')
        {
            // Array classes are identified by their descriptor and behave as
            // direct subclasses of java.lang.Object (good enough for
            // instanceof/checkcast/aastore on arrays).
            return _classes.GetOrAdd(name, _ => new JClass { Name = name, IsBuiltin = true, SuperClass = LoadClass("java.lang.Object") });
        }
        if (PrimitiveTypeNames.Contains(name))
        {
            // Primitive pseudo-classes (Integer.TYPE / int.class in 1.1).
            return _classes.GetOrAdd(name, _ => new JClass { Name = name, IsBuiltin = true });
        }
        if (BuiltinJavaLibrary.IsBuiltin(name)) return BuiltinJavaLibrary.Create(this, name);
        var resolver = _options.ClassResolver ?? throw new InvalidOperationException("No class resolver configured.");
        var bytes = resolver(name.Replace('.', '/') + ".class") ?? throw new InvalidDataException($"Java class not found: {name}");
        return LoadClassBytes(bytes);
    }

    public JClass LoadClassBytes(byte[] bytes)
    {
        var file = JClassFile.Parse(bytes);
        string name = file.ThisClassName;
        if (_classes.TryGetValue(name, out var loaded)) return loaded;
        var cls = new JClass { Name = name, File = file, IsBuiltin = false };
        // Register BEFORE loading the superclass so circular references
        // resolve to the in-progress class instead of recursing forever.
        _classes[name] = cls;
        if (file.SuperClassName != null) cls.SuperClass = LoadClass(file.SuperClassName);
        foreach (var iface in file.Interfaces) cls.Interfaces.Add(LoadClass(file.ClassNameFromIndex(iface)));
        foreach (var m in file.Methods) { m.DeclaringClass = cls; cls.Methods[(m.Name, m.Descriptor)] = m; }
        foreach (var fld in file.Fields) cls.Fields[(fld.Name, fld.Descriptor)] = fld;
        BuildVTable(cls);
        return cls;
    }

    /// <summary>Copies the superclass's virtual dispatch entries, then overlays this
    /// class's own non-static methods. Kept for API completeness; ResolveVirtual
    /// additionally walks the Methods chain directly so it stays correct even
    /// while builtin classes are still being populated.</summary>
    internal void BuildVTable(JClass cls)
    {
        if (cls.SuperClass != null)
            foreach (var kv in cls.SuperClass.VTable) cls.VTable[kv.Key] = kv.Value;
        foreach (var kv in cls.Methods)
            if (!kv.Value.IsStatic && kv.Key.Name != "<init>" && kv.Key.Name != "<clinit>")
                cls.VTable[kv.Key] = kv.Value;
    }

    // -----------------------------------------------------------------
    // initialization
    // -----------------------------------------------------------------

    public void EnsureInitialized(JClass cls)
    {
        if (cls.Initialized) return; // fast path; flag set before <clinit> runs
        lock (_lock)
        {
            if (cls.Initialized) return;
            cls.Initialized = true;
        }
        if (cls.SuperClass != null) EnsureInitialized(cls.SuperClass);
        if (cls.File != null)
        {
            // ConstantValue attributes on static fields.
            foreach (var f in cls.File.Fields)
            {
                if ((f.AccessFlags & 0x0008) == 0) continue; // static only
                var cv = f.Attributes.FirstOrDefault(a => a.ConstantValueIndex.HasValue)?.ConstantValueIndex;
                if (cv is ushort idx) cls.StaticFields[f.Name] = FromConstant(cls.File, idx);
            }
        }
        if (cls.Methods.TryGetValue(("<clinit>", "()V"), out var clinit))
            Invoke(cls, clinit, JValue.Void, Array.Empty<JValue>());
    }

    private JValue FromConstant(JClassFile file, ushort index) => file.Constant(index) switch
    {
        JInteger i => JValue.Int(i.Value),
        JFloat fl => JValue.Float(fl.Value),
        JLong l => JValue.Long(l.Value),
        JDouble d => JValue.Double(d.Value),
        JStringRef s => JValue.Ref(CreateString(file.Utf8(s.StringIndex))),
        _ => JValue.Void
    };

    // -----------------------------------------------------------------
    // object & array allocation
    // -----------------------------------------------------------------

    public JObject NewObject(JClass cls)
    {
        EnsureInitialized(cls);
        var o = new JObject { Class = cls };
        for (JClass? c = cls; c != null; c = c.SuperClass)
            foreach (var fld in c.Fields.Values)
            {
                if ((fld.AccessFlags & 0x0008) != 0) continue; // skip statics
                o.Fields[new JFieldKey(c, fld.Name, fld.Descriptor)] = DefaultValue(fld.Descriptor);
            }
        return o;
    }

    /// <summary>Allocates and runs the ()V constructor (or an explicit descriptor).</summary>
    public JObject Construct(JClass cls) => Construct(cls, "()V");
    public JObject Construct(JClass cls, string descriptor, params JValue[] args)
    {
        var obj = NewObject(cls);
        var ctor = ResolveMethod(cls, "<init>", descriptor);
        if (ctor != null) Invoke(cls, ctor, JValue.Ref(obj), args);
        return obj;
    }

    private static JValue DefaultValue(string descriptor) => descriptor.Length == 0 ? JValue.Void : descriptor[0] switch
    {
        'J' => JValue.Long(0),
        'F' => JValue.Float(0),
        'D' => JValue.Double(0),
        'L' or '[' => JValue.Ref(null),
        _ => JValue.Int(0)
    };

    public JArray NewArray(string componentDescriptor, int length)
    {
        componentDescriptor = componentDescriptor.Replace('/', '.'); // canonical
        if (length < 0) throw new JvmException(GetExceptionObject("java.lang.NegativeArraySizeException", length.ToString(CultureInfo.InvariantCulture)), 0);
        var arrClass = LoadClass("[" + componentDescriptor);
        var a = new JArray { Class = arrClass, ComponentDescriptor = componentDescriptor, Elements = new JValue[length] };
        var d = DefaultValue(componentDescriptor);
        for (int i = 0; i < length; i++) a.Elements[i] = d;
        return a;
    }

    // -----------------------------------------------------------------
    // method resolution & invocation
    // -----------------------------------------------------------------

    public JMethod? ResolveMethod(JClass owner, string name, string desc)
    {
        for (JClass? c = owner; c != null; c = c.SuperClass)
        {
            if (c.Methods.TryGetValue((name, desc), out var m)) return m;
            foreach (var i in c.Interfaces)
                if (i.Methods.TryGetValue((name, desc), out var im)) return im;
        }
        return null;
    }

    public JMethod? ResolveVirtual(JObject receiver, string name, string desc)
    {
        // Walk the receiver's ACTUAL class hierarchy for the most-derived
        // override — this is where polymorphism lives.
        for (JClass? c = receiver.Class; c != null; c = c.SuperClass)
            if (c.Methods.TryGetValue((name, desc), out var m) && !m.IsStatic) return m;
        return ResolveMethod(receiver.Class, name, desc);
    }

    public JValue InvokeVirtual(JObject receiver, string name, string desc, params JValue[] args)
    {
        if (!receiver.Class.Initialized) EnsureInitialized(receiver.Class);
        var method = ResolveVirtual(receiver, name, desc) ?? throw new MissingMethodException(receiver.Class.Name, name + desc);
        return Invoke(receiver.Class, method, JValue.Ref(receiver), args);
    }

    public JValue InvokeStatic(string owner, string name, string desc, params JValue[] args)
    {
        var cls = LoadClass(owner);
        EnsureInitialized(cls);
        var method = ResolveMethod(cls, name, desc) ?? throw new MissingMethodException(cls.Name, name + desc);
        return Invoke(cls, method, JValue.Void, args);
    }

    public void InvokeVoid(JObject receiver, string name, string desc, params JValue[] args) => _ = InvokeVirtual(receiver, name, desc, args);

    public JValue Invoke(JClass owner, JMethod method, JValue receiver, JValue[] args)
    {
        // FIX: resolve the frame (and natives) against the DECLARING class.
        // The original used the resolution-context class, so an inherited
        // method read the subclass's constant pool and inherited builtin
        // natives could never be found.
        var declaring = method.DeclaringClass ?? owner;
        object? monitor = null;
        if (method.IsSynchronized)
        {
            monitor = method.IsStatic ? declaring : receiver.AsReference() ?? new object();
            System.Threading.Monitor.Enter(monitor);
        }
        try
        {
            if (method.IsStatic) EnsureInitialized(declaring);

            if (method.IsNative || method.Code == null)
            {
                var native = FindNative(owner, method);
                if (native != null)
                {
                    var invocation = new JavaInvocation { Owner = declaring, Method = method, Receiver = receiver, Arguments = args };
                    try { return native(invocation); }
                    catch (JvmException) { throw; }
                    catch (Exception ex) { throw ConvertFault(ex, declaring, method, native: true); }
                }
                if (method.IsNative)
                {
                    // Lenient, like a real 1996 browser: warn once and keep the
                    // applet alive rather than killing the thread.
                    WarnMissingNative(declaring, method);
                    return JValue.Void;
                }
                return JValue.Void; // abstract / interface stub with no body
            }

            var code = method.Code;
            var paramTypes = JavaDescriptor.Parse(method.Descriptor);
            int slots = method.IsStatic ? 0 : 1;
            foreach (var t in paramTypes) slots += t is "J" or "D" ? 2 : 1;
            var frame = new JavaFrame
            {
                Owner = declaring,
                Method = method,
                Locals = new JValue[Math.Max(code.MaxLocals, slots)], // FIX: never smaller than the argument list
                Stack = new JValue[Math.Max(4, code.MaxStack)]
            };
            int li = 0;
            if (!method.IsStatic && receiver.Tag != JTag.Void) frame.Locals[li++] = receiver;
            foreach (var arg in args)
            {
                frame.Locals[li++] = arg;
                if (arg.Tag is JTag.Long or JTag.Double && li < frame.Locals.Length) li++; // category-2 takes two local slots
            }
            try { return Interpret(frame); }
            catch (JvmException) { throw; }
            catch (Exception ex) { throw ConvertFault(ex, declaring, method, native: false); }
        }
        finally
        {
            if (monitor != null) System.Threading.Monitor.Exit(monitor);
        }
    }

    private Func<JavaInvocation, JValue>? FindNative(JClass owner, JMethod method)
    {
        for (JClass? c = method.DeclaringClass ?? owner; c != null; c = c.SuperClass)
            if (_natives.TryGetValue((c.Name, method.Name, method.Descriptor), out var fn)) return fn;
        for (JClass? c = owner; c != null; c = c.SuperClass)
            if (_natives.TryGetValue((c.Name, method.Name, method.Descriptor), out var fn)) return fn;
        return null;
    }

    private void WarnMissingNative(JClass owner, JMethod method)
    {
        if (_warnedNatives.TryAdd((owner.Name, method.Name, method.Descriptor), 0))
            Print("[JAVA] unimplemented native: " + owner.Name + "." + method.Name + method.Descriptor + "\n");
    }

    /// <summary>Maps common CLR faults onto catchable Java exceptions; anything
    /// else is wrapped as an engine fault for the host to log.</summary>
    private Exception ConvertFault(Exception ex, JClass declaring, JMethod method, bool native)
    {
        Exception? mapped = ex switch
        {
            IndexOutOfRangeException => new JvmException(GetExceptionObject("java.lang.ArrayIndexOutOfBoundsException", ex.Message), 0),
            ArgumentOutOfRangeException => new JvmException(GetExceptionObject("java.lang.ArrayIndexOutOfBoundsException", ex.Message), 0),
            NullReferenceException => new JvmException(GetExceptionObject("java.lang.NullPointerException"), 0),
            InvalidCastException => new JvmException(GetExceptionObject("java.lang.ClassCastException", ex.Message), 0),
            DivideByZeroException => new JvmException(GetExceptionObject("java.lang.ArithmeticException", "/ by zero"), 0),
            OverflowException => new JvmException(GetExceptionObject("java.lang.ArithmeticException", "integer overflow"), 0),
            _ => null
        };
        if (mapped != null) return mapped;
        return new InvalidOperationException($"JVM {(native ? "native fault" : "fault")} in {declaring.Name}.{method.Name}{method.Descriptor}: {ex.Message}", ex);
    }

    // -----------------------------------------------------------------
    // fields
    // -----------------------------------------------------------------

    public JValue GetField(JObject obj, JClass owner, string name, string desc)
    {
        if (obj.Fields.TryGetValue(new JFieldKey(owner, name, desc), out var v)) return v;
        // FIX: the constant-pool owner isn't always the declaring class
        // (inherited protected fields); fall back to a name+descriptor match.
        foreach (var kv in obj.Fields)
            if (kv.Key.Name == name && kv.Key.Descriptor == desc) return kv.Value;
        return DefaultValue(desc);
    }

    public void SetField(JObject obj, JClass owner, string name, string desc, JValue value) =>
        obj.Fields[new JFieldKey(owner, name, desc)] = value;

    public JValue GetStatic(JClass cls, string name, string desc)
    {
        EnsureInitialized(cls);
        // FIX: walk the hierarchy — the old lookup missed statics accessed
        // through a subclass (SubClass.CONSTANT declared in Super).
        for (JClass? c = cls; c != null; c = c.SuperClass)
            if (c.StaticFields.TryGetValue(name, out var v)) return v;
        return DefaultValue(desc);
    }

    public void SetStatic(JClass cls, string name, string desc, JValue value)
    {
        EnsureInitialized(cls);
        for (JClass? c = cls; c != null; c = c.SuperClass)
            if (c.StaticFields.ContainsKey(name)) { c.StaticFields[name] = value; return; }
        // Otherwise store on the class that declares the field, so both
        // Sub.X and Super.X see the same storage.
        for (JClass? c = cls; c != null; c = c.SuperClass)
            if (c.Fields.ContainsKey((name, desc))) { c.StaticFields[name] = value; return; }
        cls.StaticFields[name] = value;
    }

    // -----------------------------------------------------------------
    // constants, strings, exceptions
    // -----------------------------------------------------------------

    public JValue GetConstant(JClassFile file, ushort index) => file.Constant(index) switch
    {
        JInteger i => JValue.Int(i.Value),
        JFloat fl => JValue.Float(fl.Value),
        JLong l => JValue.Long(l.Value),
        JDouble d => JValue.Double(d.Value),
        JStringRef s => JValue.Ref(CreateString(file.Utf8(s.StringIndex))),
        JClassRef c => JValue.Ref(CreateClassObject(LoadClass(file.Utf8(c.NameIndex)))),
        _ => throw new InvalidDataException("Unsupported ldc constant")
    };

    public JObject CreateString(string value)
    {
        var cls = LoadClass("java.lang.String");
        return new JObject { Class = cls, NativeState = value ?? "" };
    }

    public string StringValue(JValue value) => value.AsObject()?.NativeState as string ?? "null";
    public string StringValue(JObject? value) => value?.NativeState as string ?? "null";

    /// <summary>Java-semantics toString() for any JValue (used by println/String.valueOf).</summary>
    public string ToJavaString(JValue v)
    {
        switch (v.Tag)
        {
            case JTag.Reference:
                if (v.RefValue is null) return "null";
                if (v.RefValue is JObject o)
                {
                    if (o.NativeState is string s) return s;
                    if (o.NativeState is StringBuilder sb) return sb.ToString();
                    if (o.NativeState is JClass jc) return "class " + jc.Name;
                    if (o.NativeState is Color col) return "java.awt.Color[r=" + col.R + ",g=" + col.G + ",b=" + col.B + "]";
                    if (o.NativeState is bool b) return b ? "true" : "false";
                    if (o.NativeState is int i) return i.ToString(CultureInfo.InvariantCulture);
                    if (o.NativeState is long l) return l.ToString(CultureInfo.InvariantCulture);
                    if (o.NativeState is float fl) return JavaText.FloatStr(fl);
                    if (o.NativeState is double dl) return JavaText.DoubleStr(dl);
                    if (o.NativeState is char ch) return ch.ToString();
                    return o.Class.Name + "@" + RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture);
                }
                if (v.RefValue is JArray ja)
                    return ja.Class.Name + "@" + RuntimeHelpers.GetHashCode(ja).ToString("x", CultureInfo.InvariantCulture);
                return v.RefValue.ToString() ?? "";
            case JTag.Int: return v.AsInt().ToString(CultureInfo.InvariantCulture);
            case JTag.Long: return v.AsLong().ToString(CultureInfo.InvariantCulture);
            case JTag.Float: return JavaText.FloatStr(v.AsFloat());
            case JTag.Double: return JavaText.DoubleStr(v.AsDouble());
            default: return "null";
        }
    }

    internal JObject CreateClassObject(JClass cls) =>
        cls.ClassObject ??= new JObject { Class = LoadClass("java.lang.Class"), NativeState = cls };

    private JObject GetExceptionObject(string name, string? message = null)
    {
        var cls = LoadClass(name);
        var o = NewObject(cls);
        o.NativeState = message ?? "";
        return o;
    }

    internal JObject CreateExceptionObject(string name, string message = "") => GetExceptionObject(name, message);
    public JValue CreateException(string name, string message = "") => JValue.Ref(GetExceptionObject(name, message));

    internal JValue Throw(string name, string message = "")
    {
        var o = GetExceptionObject(name, message);
        throw new JvmException(o, -1, message);
    }

    public bool IsAssignableFrom(JClass target, JClass value) => value.IsAssignableTo(target);
}

// ----------------------------------------------------------------------
// Graphics/Image bridge state
// ----------------------------------------------------------------------

public sealed class JavaGraphicsFactory
{
    private readonly JavaVm _vm;
    public JavaGraphicsFactory(JavaVm vm) => _vm = vm;

    public JObject CreateGraphics(Retro96.Drawing.Graphics g, int width, int height, int originX = 0, int originY = 0, int savedState = 0, bool derived = false)
    {
        var cls = _vm.LoadClass("java.awt.Graphics");
        var o = _vm.NewObject(cls);
        o.NativeState = new JavaGraphicsState(g, width, height, originX, originY) { SavedState = savedState, IsDerived = derived };
        return o;
    }

    public JObject CreateImage(int width, int height)
    {
        var cls = _vm.LoadClass("java.awt.Image");
        var o = _vm.NewObject(cls);
        o.NativeState = new JavaImageState(new Bitmap(Math.Max(1, width), Math.Max(1, height)));
        return o;
    }

    public JObject LoadImage(string absoluteUrl)
    {
        var cls = _vm.LoadClass("java.awt.Image");
        var o = _vm.NewObject(cls);
        // Lazily loaded: starts as a 1x1 placeholder and reports -1 for
        // getWidth/getHeight until the bytes arrive; JavaGraphicsBridge
        // retries the decode on every drawImage/paint.
        var state = new JavaImageState(new Bitmap(1, 1)) { SourceUrl = absoluteUrl, Loaded = false };
        o.NativeState = state;
        JavaGraphicsBridge.EnsureImageLoaded(_vm, state); // synchronous attempt (host may have it cached)
        return o;
    }
}

public sealed class JavaGraphicsState
{
    public Retro96.Drawing.Graphics Graphics { get; }
    public int Width { get; }
    public int Height { get; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public Color Color { get; set; } = Color.Black;
    public Color Background { get; set; } = Color.LightGray; // used by clearRect
    public Font? Font { get; set; }
    public int SavedState { get; set; }
    /// <summary>True when produced by Graphics.create() — only those restore
    /// a saved transform on dispose().</summary>
    public bool IsDerived { get; set; }
    public JavaGraphicsState(Retro96.Drawing.Graphics graphics, int width, int height, int originX, int originY)
    { Graphics = graphics; Width = width; Height = height; OriginX = originX; OriginY = originY; }
}

public sealed class JavaImageState
{
    public Bitmap Bitmap { get; set; }
    public bool Loaded { get; set; }
    public string? SourceUrl { get; set; }
    public JavaImageState(Bitmap bitmap) { Bitmap = bitmap; Loaded = true; }
}

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
    public JavaAppletContextState Context { get; init; } = new();
}