using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Runtime.CompilerServices;
using Retro96.Engine.Network;

namespace Retro96.Engine.Java;

public enum JTag { Int, Long, Float, Double, Reference, Void }

// A single JVM value. Category-2 payloads (long/double) carry all bits in
// Bits so NaN payloads and negative zero round-trip losslessly.
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
    public bool IsCategory2 => Tag is JTag.Long or JTag.Double;
}

public sealed class JObject
{
    public required JClass Class { get; init; }
    // Field storage is shared between the applet thread(s) and the render
    // thread, so every read/write takes this lock.
    public readonly Dictionary<JFieldKey, JValue> Fields = new();
    internal readonly object FieldLock = new();
    public object? NativeState { get; set; }
    public override string ToString() => Class.Name;
}

public readonly record struct JFieldKey(JClass DeclaringClass, string Name, string Descriptor);

public sealed class JArray
{
    public required JClass Class { get; init; }
    // Component descriptor, normalized to '.'-separated names
    // (e.g. "I", "[I", "Ljava.lang.String;").
    public string ComponentDescriptor { get; init; } = "";
    public JValue[] Elements { get; init; } = Array.Empty<JValue>();
}

public sealed class JClass
{
    public required string Name { get; init; }
    public JClass? SuperClass { get; internal set; }
    public readonly List<JClass> Interfaces = new();
    public JClassFile? File { get; init; }
    public bool IsBuiltin { get; init; }
    // For array pseudo-classes: the component descriptor ("I", "[I",
    // "Ljava.lang.String;"), null for ordinary classes.
    public string? ArrayComponent { get; internal set; }
    public bool IsArrayClass => ArrayComponent != null;

    // Initialization state. Initialized is set before <clinit> runs to break
    // recursive initialization; Failed marks a <clinit> that threw, after
    // which every use must raise NoClassDefFoundError instead of retrying.
    public bool Initialized;
    public bool Failed;
    public JObject? ClassObject { get; set; }
    internal readonly object Sync = new();

    public readonly Dictionary<(string Name, string Descriptor), JMethod> Methods = new();
    public readonly Dictionary<(string Name, string Descriptor), JField> Fields = new();
    public readonly Dictionary<string, JValue> StaticFields = new(StringComparer.Ordinal);
    public readonly Dictionary<(string Name, string Descriptor), JMethod?> VTable = new();

    public bool IsInterface => (File?.AccessFlags & JAccess.Interface) != 0;

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
    // The class that DECLARES the method; its File owns the constant pool
    // this frame's operands resolve against.
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

// The VM core: class registry, loading/linking, initialization, allocation,
// resolution and invocation. JavaInterpreter.cs holds the bytecode loop and
// the per-package native libraries are registered from BuiltinJavaLibrary.
public sealed partial class JavaVm
{
    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    { "boolean","byte","char","short","int","long","float","double","void","B","Z","C","S","I","J","F","D","V" };

    private readonly JavaVmOptions _options;
    // Class and native registries are mutated from applet animation threads
    // while the UI thread reads them, hence the concurrent maps.
    private readonly ConcurrentDictionary<string, JClass> _classes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Owner, string Name, string Descriptor), Func<JavaInvocation, JValue>> _natives = new();
    private readonly ConcurrentDictionary<(string, string, string), byte> _warnedNatives = new();
    private readonly ConcurrentDictionary<string, JObject> _internedStrings = new(StringComparer.Ordinal);
    private readonly object _initLock = new();
    // Java threads started through Thread.start() so the applet host can
    // join them (with a timeout) when the page goes away.
    internal readonly ConcurrentDictionary<JavaThreadNativeState, byte> LiveThreads = new();

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

    // Class loading. Names are canonicalized to '.' separators so '/' and
    // '.' forms resolve to the same class. Missing classes raise
    // java.lang.NoClassDefFoundError as a JVM exception; malformed files
    // raise java.lang.ClassFormatError.
    public JClass LoadClass(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new JvmException(CreateExceptionObject("java.lang.ClassFormatError", "Empty class name."), 0);
        name = name.Replace('/', '.');
        if (_classes.TryGetValue(name, out var existing)) return existing;
        if (name[0] == '[')
        {
            // Array classes are identified by their descriptor. They behave
            // as subclasses of java.lang.Object and implement Cloneable and
            // Serializable, which is what instanceof/checkcast/aastore need.
            return _classes.GetOrAdd(name, n =>
            {
                var cls = new JClass { Name = n, IsBuiltin = true, ArrayComponent = n[1..], SuperClass = LoadClass("java.lang.Object") };
                cls.Interfaces.Add(LoadClass("java.lang.Cloneable"));
                cls.Interfaces.Add(LoadClass("java.io.Serializable"));
                return cls;
            });
        }
        if (PrimitiveTypeNames.Contains(name))
        {
            // Primitive pseudo-classes (int.class / Integer.TYPE in 1.1).
            return _classes.GetOrAdd(name, _ => new JClass { Name = name, IsBuiltin = true });
        }
        if (BuiltinJavaLibrary.IsBuiltin(name)) return BuiltinJavaLibrary.Create(this, name);
        var resolver = _options.ClassResolver;
        if (resolver == null)
            throw new JvmException(CreateExceptionObject("java.lang.NoClassDefFoundError", name), 0);
        byte[]? bytes;
        try
        {
            bytes = resolver(name.Replace('.', '/') + ".class");
        }
        catch (Exception ex)
        {
            throw new JvmException(CreateExceptionObject("java.lang.NoClassDefFoundError", name + " (" + ex.Message + ")"), 0);
        }
        if (bytes == null)
            throw new JvmException(CreateExceptionObject("java.lang.NoClassDefFoundError", name), 0);
        return LoadClassBytes(bytes);
    }

    public JClass LoadClassBytes(byte[] bytes)
    {
        JClassFile file;
        try
        {
            file = JClassFile.Parse(bytes);
        }
        catch (ClassFileFormatException ex)
        {
            throw new JvmException(CreateExceptionObject("java.lang.ClassFormatError", ex.Message), 0);
        }
        string name = file.ThisClassName;
        if (_classes.TryGetValue(name, out var loaded)) return loaded;
        var cls = new JClass { Name = name, File = file, IsBuiltin = false };
        // Register before loading the superclass so circular references
        // resolve to the in-progress class instead of recursing forever.
        _classes[name] = cls;
        try
        {
            if (file.SuperClassName != null) cls.SuperClass = LoadClass(file.SuperClassName);
            foreach (var iface in file.Interfaces) cls.Interfaces.Add(LoadClass(file.ClassNameFromIndex(iface)));
            foreach (var m in file.Methods) { m.DeclaringClass = cls; cls.Methods[(m.Name, m.Descriptor)] = m; }
            foreach (var fld in file.Fields) cls.Fields[(fld.Name, fld.Descriptor)] = fld;
            BuildVTable(cls);
        }
        catch (JvmException)
        {
            // A broken hierarchy still leaves the class registered under its
            // name so later references fail consistently instead of looping.
            _classes.TryAdd(name, cls);
            throw;
        }
        return cls;
    }

    // Copies the superclass's virtual dispatch entries, then overlays this
    // class's own non-static methods. Resolution additionally walks the
    // Methods chain directly so dispatch stays correct even while builtin
    // classes are still being populated.
    internal void BuildVTable(JClass cls)
    {
        lock (cls.Sync)
        {
            if (cls.SuperClass != null)
                foreach (var kv in cls.SuperClass.VTable) cls.VTable[kv.Key] = kv.Value;
            foreach (var kv in cls.Methods)
                if (!kv.Value.IsStatic && kv.Key.Name != "<init>" && kv.Key.Name != "<clinit>")
                    cls.VTable[kv.Key] = kv.Value;
        }
    }

    // Class initialization. Runs superclass initialization first, applies
    // ConstantValue attributes, then <clinit>. A throwing <clinit> marks the
    // class failed; every later use raises NoClassDefFoundError (no retry).
    public void EnsureInitialized(JClass cls)
    {
        if (cls.Initialized && !cls.Failed) return;
        lock (_initLock)
        {
            if (cls.Failed)
                throw new JvmException(CreateExceptionObject("java.lang.NoClassDefFoundError", "Could not initialize class " + cls.Name), 0);
            if (cls.Initialized) return;
            // Set before running so recursive references during <clinit>
            // terminate instead of deadlocking.
            lock (cls.Sync) cls.Initialized = true;
        }
        if (cls.SuperClass != null) EnsureInitialized(cls.SuperClass);
        if (cls.File != null)
        {
            foreach (var f in cls.File.Fields)
            {
                if (!f.IsStatic) continue;
                var cv = f.Attributes.FirstOrDefault(a => a.ConstantValueIndex.HasValue)?.ConstantValueIndex;
                if (cv is ushort idx)
                {
                    var value = FromConstant(cls.File, idx);
                    lock (cls.Sync) cls.StaticFields[f.Name] = value;
                }
            }
        }
        if (cls.Methods.TryGetValue(("<clinit>", "()V"), out var clinit))
        {
            try
            {
                Invoke(cls, clinit, JValue.Void, Array.Empty<JValue>());
            }
            catch (Exception)
            {
                lock (cls.Sync) cls.Failed = true;
                throw new JvmException(CreateExceptionObject("java.lang.NoClassDefFoundError", "Could not initialize class " + cls.Name), 0);
            }
        }
    }

    private JValue FromConstant(JClassFile file, ushort index) => file.Constant(index) switch
    {
        JInteger i => JValue.Int(i.Value),
        JFloat fl => JValue.Float(fl.Value),
        JLong l => JValue.Long(l.Value),
        JDouble d => JValue.Double(d.Value),
        JStringRef s => JValue.Ref(InternString(file.Utf8(s.StringIndex))),
        _ => JValue.Void
    };

    // Object and array allocation.

    public JObject NewObject(JClass cls)
    {
        EnsureInitialized(cls);
        var o = new JObject { Class = cls };
        for (JClass? c = cls; c != null; c = c.SuperClass)
            foreach (var fld in c.Fields.Values)
            {
                if (fld.IsStatic) continue;
                lock (o.FieldLock) o.Fields[new JFieldKey(c, fld.Name, fld.Descriptor)] = DefaultValue(fld.Descriptor);
            }
        return o;
    }

    // Allocates and runs the ()V constructor (or an explicit descriptor).
    public JObject Construct(JClass cls) => Construct(cls, "()V");
    public JObject Construct(JClass cls, string descriptor, params JValue[] args)
    {
        var obj = NewObject(cls);
        var ctor = ResolveMethod(cls, "<init>", descriptor);
        if (ctor != null) Invoke(cls, ctor, JValue.Ref(obj), args);
        return obj;
    }

    public static JValue DefaultValue(string descriptor) => descriptor.Length == 0 ? JValue.Void : descriptor[0] switch
    {
        'J' => JValue.Long(0),
        'F' => JValue.Float(0),
        'D' => JValue.Double(0),
        'L' or '[' => JValue.Ref(null),
        _ => JValue.Int(0)
    };

    public JArray NewArray(string componentDescriptor, int length)
    {
        componentDescriptor = componentDescriptor.Replace('/', '.');
        if (length < 0)
            throw new JvmException(CreateExceptionObject("java.lang.NegativeArraySizeException", length.ToString(CultureInfo.InvariantCulture)), 0);
        var arrClass = LoadClass("[" + componentDescriptor);
        var a = new JArray { Class = arrClass, ComponentDescriptor = componentDescriptor, Elements = new JValue[length] };
        var d = DefaultValue(componentDescriptor);
        for (int i = 0; i < length; i++) a.Elements[i] = d;
        return a;
    }

    // Method resolution and invocation.

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
        // override; this is where polymorphism lives.
        for (JClass? c = receiver.Class; c != null; c = c.SuperClass)
            if (c.Methods.TryGetValue((name, desc), out var m) && !m.IsStatic) return m;
        return ResolveMethod(receiver.Class, name, desc);
    }

    public JValue InvokeVirtual(JObject receiver, string name, string desc, params JValue[] args)
    {
        if (!receiver.Class.Initialized || receiver.Class.Failed) EnsureInitialized(receiver.Class);
        var method = ResolveVirtual(receiver, name, desc)
            ?? throw new JvmException(CreateExceptionObject("java.lang.NoSuchMethodError", receiver.Class.Name + "." + name + desc), 0);
        return Invoke(receiver.Class, method, JValue.Ref(receiver), args);
    }

    public JValue InvokeStatic(string owner, string name, string desc, params JValue[] args)
    {
        var cls = LoadClass(owner);
        EnsureInitialized(cls);
        var method = ResolveMethod(cls, name, desc)
            ?? throw new JvmException(CreateExceptionObject("java.lang.NoSuchMethodError", cls.Name + "." + name + desc), 0);
        return Invoke(cls, method, JValue.Void, args);
    }

    public void InvokeVoid(JObject receiver, string name, string desc, params JValue[] args) => _ = InvokeVirtual(receiver, name, desc, args);

    // Guard against runaway recursion turning into a host StackOverflow,
    // which cannot be caught; applets can still catch the JVM-level error.
    [ThreadStatic] private static int _invocationDepth;
    private const int MaxInvocationDepth = 400;

    public JValue Invoke(JClass owner, JMethod method, JValue receiver, JValue[] args)
    {
        // Frames (and natives) resolve against the DECLARING class so
        // inherited methods read their own constant pool and inherited
        // builtin natives can be found.
        var declaring = method.DeclaringClass ?? owner;
        object? monitor = null;
        if (method.IsSynchronized)
        {
            monitor = method.IsStatic ? declaring : receiver.AsReference() ?? new object();
            System.Threading.Monitor.Enter(monitor);
        }
        if (_invocationDepth >= MaxInvocationDepth)
        {
            var overflow = new JvmException(CreateExceptionObject("java.lang.StackOverflowError"), 0);
            if (monitor != null) System.Threading.Monitor.Exit(monitor);
            throw overflow;
        }
        _invocationDepth++;
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
                    // Lenient like a real 1996 browser: warn once and keep
                    // the applet alive rather than killing the thread.
                    WarnMissingNative(declaring, method);
                    return JValue.Void;
                }
                return JValue.Void; // abstract / interface method with no body
            }

            var code = method.Code;
            var paramTypes = JavaDescriptor.Parse(method.Descriptor);
            int slots = method.IsStatic ? 0 : 1;
            foreach (var t in paramTypes) slots += t is "J" or "D" ? 2 : 1;
            var frame = new JavaFrame
            {
                Owner = declaring,
                Method = method,
                Locals = new JValue[Math.Max(code.MaxLocals, slots)],
                Stack = new JValue[Math.Max(4, (int)code.MaxStack)]
            };
            int li = 0;
            if (!method.IsStatic && receiver.Tag != JTag.Void) frame.Locals[li++] = receiver;
            foreach (var arg in args)
            {
                frame.Locals[li++] = arg;
                if (arg.IsCategory2 && li < frame.Locals.Length) li++; // category-2 takes two local slots
            }
            try { return Interpret(frame); }
            catch (JvmException) { throw; }
            catch (Exception ex) { throw ConvertFault(ex, declaring, method, native: false); }
        }
        finally
        {
            _invocationDepth--;
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

    // Maps common CLR faults onto catchable Java exceptions; anything else
    // is wrapped as an engine fault for the host to log.
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
            ClassFileFormatException cf => new JvmException(GetExceptionObject("java.lang.ClassFormatError", cf.Message), 0),
            _ => null
        };
        if (mapped != null) return mapped;
        return new InvalidOperationException($"JVM {(native ? "native fault" : "fault")} in {declaring.Name}.{method.Name}{method.Descriptor}: {ex.Message}", ex);
    }

    // Field access. Instance fields are locked per object because the applet
    // thread and the render thread share objects.

    public JValue GetField(JObject obj, JClass owner, string name, string desc)
    {
        lock (obj.FieldLock)
        {
            if (obj.Fields.TryGetValue(new JFieldKey(owner, name, desc), out var v)) return v;
            // The constant-pool owner is not always the declaring class
            // (inherited protected fields); fall back to name+descriptor.
            foreach (var kv in obj.Fields)
                if (kv.Key.Name == name && kv.Key.Descriptor == desc) return kv.Value;
            return DefaultValue(desc);
        }
    }

    public void SetField(JObject obj, JClass owner, string name, string desc, JValue value)
    {
        lock (obj.FieldLock)
        {
            if (obj.Fields.TryGetValue(new JFieldKey(owner, name, desc), out _))
            {
                obj.Fields[new JFieldKey(owner, name, desc)] = value;
                return;
            }
            foreach (var kv in obj.Fields)
                if (kv.Key.Name == name && kv.Key.Descriptor == desc)
                {
                    obj.Fields[kv.Key] = value;
                    return;
                }
            obj.Fields[new JFieldKey(owner, name, desc)] = value;
        }
    }

    public JValue GetStatic(JClass cls, string name, string desc)
    {
        EnsureInitialized(cls);
        for (JClass? c = cls; c != null; c = c.SuperClass)
            lock (c.Sync)
                if (c.StaticFields.TryGetValue(name, out var v)) return v;
        return DefaultValue(desc);
    }

    public void SetStatic(JClass cls, string name, string desc, JValue value)
    {
        EnsureInitialized(cls);
        for (JClass? c = cls; c != null; c = c.SuperClass)
            lock (c.Sync)
                if (c.StaticFields.ContainsKey(name)) { c.StaticFields[name] = value; return; }
        // Store on the class that declares the field so both Sub.X and
        // Super.X see the same storage.
        for (JClass? c = cls; c != null; c = c.SuperClass)
            lock (c.Sync)
                if (c.Fields.ContainsKey((name, desc))) { c.StaticFields[name] = value; return; }
        lock (cls.Sync) cls.StaticFields[name] = value;
    }

    // Constants, strings, classes and exceptions.

    public JValue GetConstant(JClassFile file, ushort index)
    {
        JConstant c;
        try
        {
            c = file.Constant(index);
        }
        catch (ClassFileFormatException ex)
        {
            throw new JvmException(GetExceptionObject("java.lang.ClassFormatError", ex.Message), 0);
        }
        return c switch
        {
            JInteger i => JValue.Int(i.Value),
            JFloat fl => JValue.Float(fl.Value),
            JLong l => JValue.Long(l.Value),
            JDouble d => JValue.Double(d.Value),
            JStringRef s => JValue.Ref(InternString(file.Utf8(s.StringIndex))),
            JClassRef cr => JValue.Ref(CreateClassObject(LoadClass(file.Utf8(cr.NameIndex)))),
            _ => throw new JvmException(GetExceptionObject("java.lang.ClassFormatError", "Unsupported ldc constant"), 0)
        };
    }

    public JObject CreateString(string value)
    {
        var cls = LoadClass("java.lang.String");
        return new JObject { Class = cls, NativeState = value ?? "" };
    }

    // String literals and intern() share one object per value so reference
    // equality ("==" on literals) behaves like the JVM's.
    public JObject InternString(string value)
    {
        value ??= "";
        return _internedStrings.GetOrAdd(value, v => new JObject { Class = LoadClass("java.lang.String"), NativeState = v });
    }

    public string StringValue(JValue value) => value.AsObject()?.NativeState as string ?? "null";
    public string StringValue(JObject? value) => value?.NativeState as string ?? "null";

    // Java-semantics toString() for any JValue (used by println/String.valueOf).
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
                    if (o.NativeState is Retro96.Drawing.Color col) return "java.awt.Color[r=" + col.R + ",g=" + col.G + ",b=" + col.B + "]";
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

    // Java-level equality used by collection classes: virtual equals() call.
    public bool JavaEquals(JValue a, JValue b)
    {
        var ao = a.AsObject();
        var bo = b.AsObject();
        if (ao == null || bo == null) return ReferenceEquals(a.RefValue, b.RefValue);
        if (ao.NativeState is string sa && bo.NativeState is string sbb)
            return string.Equals(sa, sbb, StringComparison.Ordinal);
        return InvokeVirtual(ao, "equals", "(Ljava/lang/Object;)Z", JValue.Ref(bo)).AsInt() != 0;
    }

    // Java-level hashCode used by collection classes: virtual hashCode() call.
    public int JavaHashCode(JValue v)
    {
        var o = v.AsObject();
        if (o == null) return 0;
        if (o.NativeState is string s)
        {
            int h = 0;
            foreach (var ch in s) h = unchecked(31 * h + ch);
            return h;
        }
        return InvokeVirtual(o, "hashCode", "()I").AsInt();
    }

    // Array assignability for checkcast/instanceof/aastore. Descriptors are
    // normalized ('.' separators). ElementIs answers "is a value described by
    // valueDesc storable in a slot typed slotDesc".
    internal bool ElementIs(string valueDesc, string slotDesc)
    {
        if (slotDesc.Length == 0) return true;
        if (valueDesc.Length == 0) return false;
        if (slotDesc[0] == '[')
        {
            if (valueDesc[0] != '[') return false;
            return ElementIs(valueDesc[1..], slotDesc[1..]);
        }
        if (slotDesc[0] == 'L' && slotDesc.EndsWith(";"))
        {
            if (valueDesc[0] == '[')
                return slotDesc is "Ljava.lang.Object;" or "Ljava.lang.Cloneable;" or "Ljava.io.Serializable;";
            if (valueDesc[0] == 'L' && valueDesc.EndsWith(";"))
                return LoadClass(valueDesc[1..^1]).IsAssignableTo(LoadClass(slotDesc[1..^1]));
            return false;
        }
        // Primitive component types must match exactly.
        return valueDesc == slotDesc;
    }

    internal bool IsValueAssignableTo(JValue value, JClass target)
    {
        if (value.AsReference() is not { } r) return false;
        switch (r)
        {
            case JObject jo:
                return !target.IsArrayClass && jo.Class.IsAssignableTo(target);
            case JArray ja:
                if (target.IsArrayClass) return ElementIs(ja.ComponentDescriptor, target.ArrayComponent!);
                return ja.Class.IsAssignableTo(target);
            default:
                return false;
        }
    }

    // aastore / System.arraycopy element check against the target array's
    // component descriptor. Null always passes.
    internal bool IsElementAssignableToSlot(JValue value, string slotDescriptor)
    {
        if (value.AsReference() == null) return true;
        if (slotDescriptor.Length == 0) return true;
        string valueDesc = value.AsReference() switch
        {
            JObject jo => "L" + jo.Class.Name + ";",
            JArray ja => ja.ComponentDescriptor,
            _ => ""
        };
        if (valueDesc.Length == 0) return false;
        return ElementIs(valueDesc, slotDescriptor);
    }
}
