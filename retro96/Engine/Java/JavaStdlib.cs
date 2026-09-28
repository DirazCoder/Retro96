using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Retro96.Engine.Java;

// Natives for java.lang core: Object, Class, Throwable hierarchy, String,
// StringBuffer/StringBuilder, System, PrintStream and Math.
internal static class JavaStdlib
{
    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.lang.Object": RegisterObject(vm, c); break;
            case "java.lang.Class": RegisterClass(vm, c); break;
            case "java.lang.Throwable": RegisterThrowable(vm, c); break;
            case "java.lang.String": RegisterString(vm, c); break;
            case "java.lang.StringBuffer":
            case "java.lang.StringBuilder": RegisterStringBuffer(vm, c); break;
            case "java.lang.System": RegisterSystem(vm, c); break;
            case "java.lang.PrintStream":
            case "java.io.PrintStream": RegisterPrintStream(vm, c); break;
            case "java.lang.Math": RegisterMath(vm, c); break;
            case "java.lang.SecurityManager": vm.RegisterNative(c.Name, "checkPermission", "(Ljava/lang/Object;)V", _ => JValue.Void); break;
        }
    }

    private static void RegisterObject(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i => JValue.Int(ReferenceEquals(i.Receiver.RefValue, i.Arguments[0].RefValue) ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i => JValue.Int(RuntimeHelpers.GetHashCode(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var o = i.Receiver.AsObject()!;
            return JValue.Ref(vm.CreateString(o.Class.Name + "@" + RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture)));
        });
        vm.RegisterNative(c.Name, "getClass", "()Ljava/lang/Class;", i => JValue.Ref(vm.CreateClassObject(i.Receiver.AsObject()!.Class)));
        vm.RegisterNative(c.Name, "clone", "()Ljava/lang/Object;", i =>
        {
            var o = i.Receiver.AsObject()!;
            if (!o.Class.IsAssignableTo(vm.LoadClass("java.lang.Cloneable")))
                throw new JvmException(vm.CreateExceptionObject("java.lang.CloneNotSupportedException", o.Class.Name), 0);
            // Shallow field copy: reference fields share their targets.
            var copy = new JObject { Class = o.Class, NativeState = o.NativeState };
            lock (o.FieldLock) foreach (var kv in o.Fields) copy.Fields[kv.Key] = kv.Value;
            return JValue.Ref(copy);
        });
        vm.RegisterNative(c.Name, "wait", "()V", i => { try { System.Threading.Monitor.Wait(i.Receiver.AsObject()!); } catch (SynchronizationLockException) { } return JValue.Void; });
        vm.RegisterNative(c.Name, "wait", "(J)V", i => { WaitMillis(i.Receiver.AsObject()!, i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "wait", "(JI)V", i => { WaitMillis(i.Receiver.AsObject()!, i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "notify", "()V", i => { try { System.Threading.Monitor.Pulse(i.Receiver.AsObject()!); } catch (SynchronizationLockException) { } return JValue.Void; });
        vm.RegisterNative(c.Name, "notifyAll", "()V", i => { try { System.Threading.Monitor.PulseAll(i.Receiver.AsObject()!); } catch (SynchronizationLockException) { } return JValue.Void; });
    }

    private static void WaitMillis(JObject o, long ms)
    {
        int clamped = (int)Math.Clamp(ms, 0, int.MaxValue);
        try { _ = System.Threading.Monitor.Wait(o, clamped); } catch (SynchronizationLockException) { }
    }

    private static void RegisterClass(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "getName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as JClass)?.Name ?? "java.lang.Object")));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString("class " + ((i.Receiver.AsObject()!.NativeState as JClass)?.Name ?? "java.lang.Object"))));
        vm.RegisterNative(c.Name, "getSuperclass", "()Ljava/lang/Class;", i =>
        {
            var jc = i.Receiver.AsObject()!.NativeState as JClass;
            return jc?.SuperClass == null ? JValue.Ref(null) : JValue.Ref(vm.CreateClassObject(jc.SuperClass));
        });
        vm.RegisterNative(c.Name, "isInstance", "(Ljava/lang/Object;)Z", i =>
        {
            var jc = i.Receiver.AsObject()!.NativeState as JClass;
            var o = i.Arguments[0].AsObject();
            return JValue.Int(jc != null && o != null && vm.IsValueAssignableTo(JValue.Ref(o), jc) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "isInterface", "()Z", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JClass)?.IsInterface == true ? 1 : 0));
        vm.RegisterNative(c.Name, "isAssignableFrom", "(Ljava/lang/Class;)Z", i =>
        {
            var target = i.Receiver.AsObject()?.NativeState as JClass;
            var value = i.Arguments[0].AsObject()?.NativeState as JClass;
            return JValue.Int(target != null && value != null && value.IsAssignableTo(target) ? 1 : 0);
        });
    }

    private static void RegisterThrowable(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = ""; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "getMessage", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as string) ?? "")));
        vm.RegisterNative(c.Name, "getLocalizedMessage", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as string) ?? "")));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var o = i.Receiver.AsObject()!;
            var m = o.NativeState as string;
            return JValue.Ref(vm.CreateString(string.IsNullOrEmpty(m) ? o.Class.Name : o.Class.Name + ": " + m));
        });
        vm.RegisterNative(c.Name, "fillInStackTrace", "()Ljava/lang/Throwable;", i => i.Receiver);
        Action<JavaInvocation> print = i =>
        {
            var o = i.Receiver.AsObject();
            var m = o?.NativeState as string;
            vm.Print(o?.Class.Name + (string.IsNullOrEmpty(m) ? "" : ": " + m) + "\n");
        };
        vm.RegisterNative(c.Name, "printStackTrace", "()V", i => { print(i); return JValue.Void; });
        vm.RegisterNative(c.Name, "printStackTrace", "(Ljava/io/PrintStream;)V", i => { print(i); return JValue.Void; });
    }

    // Shared registration for every concrete exception/error class: the
    // constructors store the message and getMessage/toString read it back.
    public static void RegisterExceptionBody(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = ""; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "getMessage", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as string) ?? "")));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.ToJavaString(i.Receiver))));
    }

    private static void RegisterString(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = ""; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "([C)V", i => { i.Receiver.AsObject()!.NativeState = Chars(vm, i.Arguments[0], 0, -1); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "([CII)V", i => { i.Receiver.AsObject()!.NativeState = Chars(vm, i.Arguments[0], i.Arguments[1].AsInt(), i.Arguments[2].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/StringBuffer;)V", i => { i.Receiver.AsObject()!.NativeState = vm.ToJavaString(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "length", "()I", i => JValue.Int(vm.StringValue(i.Receiver).Length));
        vm.RegisterNative(c.Name, "charAt", "(I)C", i =>
        {
            var s = vm.StringValue(i.Receiver);
            int idx = i.Arguments[0].AsInt();
            if (idx < 0 || idx >= s.Length)
                throw new JvmException(vm.CreateExceptionObject("java.lang.StringIndexOutOfBoundsException", "index " + idx + ", length " + s.Length), 0);
            return JValue.Int(s[idx]);
        });
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
        {
            int h = 0;
            foreach (var ch in vm.StringValue(i.Receiver)) h = unchecked(31 * h + ch);
            return JValue.Int(h);
        });
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
        {
            var other = i.Arguments[0].AsObject();
            return JValue.Int(other?.NativeState is string s && string.Equals(vm.StringValue(i.Receiver), s, StringComparison.Ordinal) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "equalsIgnoreCase", "(Ljava/lang/String;)Z", i =>
        {
            var other = i.Arguments[0].AsObject();
            return JValue.Int(other?.NativeState is string s && string.Equals(vm.StringValue(i.Receiver), s, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "compareTo", "(Ljava/lang/String;)I", i => JValue.Int(string.CompareOrdinal(vm.StringValue(i.Receiver), vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "compareToIgnoreCase", "(Ljava/lang/String;)I", i => JValue.Int(string.Compare(vm.StringValue(i.Receiver), vm.StringValue(i.Arguments[0]), StringComparison.OrdinalIgnoreCase)));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => i.Receiver);
        vm.RegisterNative(c.Name, "intern", "()Ljava/lang/String;", i => JValue.Ref(vm.InternString(vm.StringValue(i.Receiver))));
        vm.RegisterNative(c.Name, "toCharArray", "()[C", i =>
        {
            var text = vm.StringValue(i.Receiver);
            var arr = vm.NewArray("C", text.Length);
            for (int k = 0; k < text.Length; k++) arr.Elements[k] = JValue.Int(text[k]);
            return JValue.Ref(arr);
        });
        vm.RegisterNative(c.Name, "getBytes", "()[B", i =>
        {
            var text = vm.StringValue(i.Receiver);
            var bytes = Encoding.UTF8.GetBytes(text);
            var arr = vm.NewArray("B", bytes.Length);
            for (int k = 0; k < bytes.Length; k++) arr.Elements[k] = JValue.Int(unchecked((sbyte)bytes[k]));
            return JValue.Ref(arr);
        });
        vm.RegisterNative(c.Name, "toLowerCase", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver).ToLowerInvariant())));
        vm.RegisterNative(c.Name, "toUpperCase", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver).ToUpperInvariant())));
        // trim strips ASCII whitespace (chars <= 0x20), not Unicode whitespace.
        vm.RegisterNative(c.Name, "trim", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(AsciiTrim(vm.StringValue(i.Receiver)))));
        vm.RegisterNative(c.Name, "replace", "(CC)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver).Replace((char)i.Arguments[0].AsInt(), (char)i.Arguments[1].AsInt()))));
        vm.RegisterNative(c.Name, "substring", "(I)Ljava/lang/String;", i =>
        {
            var s = vm.StringValue(i.Receiver);
            int b = i.Arguments[0].AsInt();
            if (b < 0 || b > s.Length)
                throw new JvmException(vm.CreateExceptionObject("java.lang.StringIndexOutOfBoundsException", "begin " + b + ", length " + s.Length), 0);
            return JValue.Ref(vm.CreateString(s[b..]));
        });
        vm.RegisterNative(c.Name, "substring", "(II)Ljava/lang/String;", i =>
        {
            var s = vm.StringValue(i.Receiver);
            int b = i.Arguments[0].AsInt(), e = i.Arguments[1].AsInt();
            if (b < 0 || e > s.Length || b > e)
                throw new JvmException(vm.CreateExceptionObject("java.lang.StringIndexOutOfBoundsException", "begin " + b + ", end " + e + ", length " + s.Length), 0);
            return JValue.Ref(vm.CreateString(s.Substring(b, e - b)));
        });
        vm.RegisterNative(c.Name, "indexOf", "(I)I", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "indexOf", "(II)I", i =>
        {
            var s = vm.StringValue(i.Receiver);
            int from = Math.Clamp(i.Arguments[1].AsInt(), 0, s.Length);
            return JValue.Int(s.IndexOf((char)i.Arguments[0].AsInt(), from));
        });
        vm.RegisterNative(c.Name, "indexOf", "(Ljava/lang/String;)I", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal)));
        vm.RegisterNative(c.Name, "indexOf", "(Ljava/lang/String;I)I", i =>
        {
            var s = vm.StringValue(i.Receiver);
            int from = Math.Clamp(i.Arguments[1].AsInt(), 0, s.Length);
            return JValue.Int(s.IndexOf(vm.StringValue(i.Arguments[0]), from, StringComparison.Ordinal));
        });
        vm.RegisterNative(c.Name, "lastIndexOf", "(I)I", i => JValue.Int(vm.StringValue(i.Receiver).LastIndexOf((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "lastIndexOf", "(Ljava/lang/String;)I", i => JValue.Int(vm.StringValue(i.Receiver).LastIndexOf(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal)));
        vm.RegisterNative(c.Name, "startsWith", "(Ljava/lang/String;)Z", i => JValue.Int(vm.StringValue(i.Receiver).StartsWith(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal) ? 1 : 0));
        vm.RegisterNative(c.Name, "startsWith", "(Ljava/lang/String;I)Z", i =>
        {
            var s = vm.StringValue(i.Receiver);
            var p = vm.StringValue(i.Arguments[0]);
            int off = i.Arguments[1].AsInt();
            return JValue.Int(off >= 0 && off + p.Length <= s.Length && s.AsSpan(off, p.Length).SequenceEqual(p) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "endsWith", "(Ljava/lang/String;)Z", i => JValue.Int(vm.StringValue(i.Receiver).EndsWith(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal) ? 1 : 0));
        vm.RegisterNative(c.Name, "contains", "(Ljava/lang/CharSequence;)Z", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf(vm.ToJavaString(i.Arguments[0]), StringComparison.Ordinal) >= 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "concat", "(Ljava/lang/String;)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver) + vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/Object;)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.ToJavaString(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "valueOf", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "valueOf", "(J)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsLong().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "valueOf", "(C)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(((char)i.Arguments[0].AsInt()).ToString())));
        vm.RegisterNative(c.Name, "valueOf", "(F)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.FloatStr(i.Arguments[0].AsFloat()))));
        vm.RegisterNative(c.Name, "valueOf", "(D)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.DoubleStr(i.Arguments[0].AsDouble()))));
        vm.RegisterNative(c.Name, "valueOf", "(Z)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt() != 0 ? "true" : "false")));
        vm.RegisterNative(c.Name, "valueOf", "([C)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(Chars(vm, i.Arguments[0], 0, -1))));
    }

    private static string AsciiTrim(string s)
    {
        int b = 0, e = s.Length;
        while (b < e && s[b] <= ' ') b++;
        while (e > b && s[e - 1] <= ' ') e--;
        return s[b..e];
    }

    private static string Chars(JavaVm vm, JValue v, int start, int len)
    {
        var a = v.AsArray() ?? throw new JvmException(vm.CreateExceptionObject("java.lang.NullPointerException"), 0);
        if (start < 0 || start > a.Elements.Length)
            throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + start), 0);
        if (len < 0) len = a.Elements.Length - start;
        if (len < 0 || start + len > a.Elements.Length)
            throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "length " + len), 0);
        var chars = new char[len];
        for (int i = 0; i < len; i++) chars[i] = (char)a.Elements[start + i].AsInt();
        return new string(chars);
    }

    private static void RegisterStringBuffer(JavaVm vm, JClass c)
    {
        // Fluent method descriptors must return the concrete builder type
        // (StringBuffer vs StringBuilder) so constant-pool references from
        // either compiler generation resolve.
        string self = "Ljava/lang/" + (c.Name == "java.lang.StringBuilder" ? "StringBuilder" : "StringBuffer") + ";";
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new StringBuilder(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { i.Receiver.AsObject()!.NativeState = new StringBuilder(Math.Max(0, i.Arguments[0].AsInt())); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = new StringBuilder(vm.StringValue(i.Arguments[0])); return JValue.Void; });
        vm.RegisterNative(c.Name, "length", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as StringBuilder)?.Length ?? 0));
        vm.RegisterNative(c.Name, "charAt", "(I)C", i =>
        {
            var sb = (i.Receiver.AsObject()?.NativeState as StringBuilder) ?? throw new JvmException(vm.CreateExceptionObject("java.lang.NullPointerException"), 0);
            int idx = i.Arguments[0].AsInt();
            if (idx < 0 || idx >= sb.Length)
                throw new JvmException(vm.CreateExceptionObject("java.lang.StringIndexOutOfBoundsException", "index " + idx + ", length " + sb.Length), 0);
            return JValue.Int(sb[idx]);
        });
        vm.RegisterNative(c.Name, "setCharAt", "(IC)V", i => { var sb = i.Receiver.AsObject()?.NativeState as StringBuilder; if (sb != null) sb[i.Arguments[0].AsInt()] = (char)i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLength", "(I)V", i =>
        {
            var sb = i.Receiver.AsObject()?.NativeState as StringBuilder;
            if (sb != null)
            {
                int n = Math.Max(0, i.Arguments[0].AsInt());
                if (n < sb.Length) sb.Length = n;
                else if (n > sb.Length) sb.Append('\0', n - sb.Length);
            }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "capacity", "()I", i => JValue.Int(Math.Max((i.Receiver.AsObject()?.NativeState as StringBuilder)?.Capacity ?? 16, 16)));
        vm.RegisterNative(c.Name, "ensureCapacity", "(I)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "reverse", "()" + self, i =>
        {
            if (i.Receiver.AsObject()?.NativeState is StringBuilder sb) { var chars = sb.ToString().ToCharArray(); Array.Reverse(chars); sb.Clear(); sb.Append(chars); }
            return i.Receiver;
        });
        vm.RegisterNative(c.Name, "deleteCharAt", "(I)" + self, i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Remove(i.Arguments[0].AsInt(), 1); return i.Receiver; });
        vm.RegisterNative(c.Name, "delete", "(II)" + self, i =>
        {
            if (i.Receiver.AsObject()?.NativeState is StringBuilder sb)
            {
                int b = Math.Clamp(i.Arguments[0].AsInt(), 0, sb.Length);
                int e = Math.Clamp(i.Arguments[1].AsInt(), b, sb.Length);
                sb.Remove(b, e - b);
            }
            return i.Receiver;
        });
        vm.RegisterNative(c.Name, "insert", "(ILjava/lang/String;)" + self, i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Insert(i.Arguments[0].AsInt(), vm.StringValue(i.Arguments[1])); return i.Receiver; });
        vm.RegisterNative(c.Name, "insert", "(II)" + self, i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Insert(i.Arguments[0].AsInt(), i.Arguments[1].AsInt().ToString(CultureInfo.InvariantCulture)); return i.Receiver; });
        vm.RegisterNative(c.Name, "insert", "(IC)" + self, i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Insert(i.Arguments[0].AsInt(), (char)i.Arguments[1].AsInt()); return i.Receiver; });
        vm.RegisterNative(c.Name, "insert", "(ILjava/lang/Object;)" + self, i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Insert(i.Arguments[0].AsInt(), vm.ToJavaString(i.Arguments[1])); return i.Receiver; });
        // append chain used by string concatenation: descriptor-aware so
        // ints render as digits and Objects via toString (null -> "null").
        foreach (var t in new[]
                 {
                     "(Ljava/lang/String;)", "(Ljava/lang/Object;)", "([C)", "(I)", "(J)", "(F)", "(D)", "(Z)", "(C)",
                     "([CII)"
                 })
        {
            string desc = t;
            vm.RegisterNative(c.Name, "append", desc + self, i => { Append(vm, i, desc); return i.Receiver; });
        }
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as StringBuilder)?.ToString() ?? "")));
    }

    private static void Append(JavaVm vm, JavaInvocation i, string desc)
    {
        var o = i.Receiver.AsObject()!;
        var sb = o.NativeState as StringBuilder ?? new StringBuilder();
        switch (desc)
        {
            case "(Ljava/lang/String;)": sb.Append(vm.StringValue(i.Arguments[0])); break;
            case "(Ljava/lang/Object;)": sb.Append(vm.ToJavaString(i.Arguments[0])); break;
            case "([C)":
            {
                var a = i.Arguments[0].AsArray();
                if (a == null) sb.Append("null");
                else foreach (var v in a.Elements) sb.Append((char)v.AsInt());
                break;
            }
            case "([CII)":
            {
                var a = i.Arguments[0].AsArray();
                if (a == null) sb.Append("null");
                else
                {
                    int off = i.Arguments[1].AsInt(), len = i.Arguments[2].AsInt();
                    if (off >= 0 && len >= 0 && off + len <= a.Elements.Length)
                        for (int k = 0; k < len; k++) sb.Append((char)a.Elements[off + k].AsInt());
                }
                break;
            }
            case "(I)": sb.Append(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture)); break;
            case "(J)": sb.Append(i.Arguments[0].AsLong().ToString(CultureInfo.InvariantCulture)); break;
            case "(F)": sb.Append(JavaText.FloatStr(i.Arguments[0].AsFloat())); break;
            case "(D)": sb.Append(JavaText.DoubleStr(i.Arguments[0].AsDouble())); break;
            case "(Z)": sb.Append(i.Arguments[0].AsInt() != 0 ? "true" : "false"); break;
            default: sb.Append((char)i.Arguments[0].AsInt()); break;
        }
        o.NativeState = sb;
    }

    private static void RegisterSystem(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "currentTimeMillis", "()J", _ => JValue.Long(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        vm.RegisterNative(c.Name, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", i =>
        {
            var src = i.Arguments[0].AsArray();
            var dst = i.Arguments[2].AsArray();
            if (src == null || dst == null) throw new JvmException(vm.CreateExceptionObject("java.lang.NullPointerException"), 0);
            int srcPos = i.Arguments[1].AsInt(), dstPos = i.Arguments[3].AsInt(), len = i.Arguments[4].AsInt();
            if (srcPos < 0 || dstPos < 0 || len < 0 || srcPos > src.Elements.Length - len || dstPos > dst.Elements.Length - len)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "arraycopy: " + srcPos + "/" + dstPos + "/" + len), 0);

            // memmove semantics for overlap in the same array; element-type
            // compatibility is enforced for reference arrays.
            bool srcRef = src.ComponentDescriptor.Length > 0 && (src.ComponentDescriptor[0] == 'L' || src.ComponentDescriptor[0] == '[');
            bool dstRef = dst.ComponentDescriptor.Length > 0 && (dst.ComponentDescriptor[0] == 'L' || dst.ComponentDescriptor[0] == '[');
            if (src.ComponentDescriptor.Replace('/', '.') == dst.ComponentDescriptor.Replace('/', '.'))
            {
                if (ReferenceEquals(src, dst) && dstPos > srcPos && dstPos < srcPos + len)
                    for (int n = len - 1; n >= 0; n--) dst.Elements[dstPos + n] = src.Elements[srcPos + n];
                else
                    for (int n = 0; n < len; n++) dst.Elements[dstPos + n] = src.Elements[srcPos + n];
            }
            else if (!srcRef || !dstRef)
            {
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayStoreException", "arraycopy: incompatible component types"), 0);
            }
            else
            {
                var temp = new JValue[len];
                for (int n = 0; n < len; n++) temp[n] = src.Elements[srcPos + n];
                for (int n = 0; n < len; n++)
                {
                    if (!vm.IsElementAssignableToSlot(temp[n], dst.ComponentDescriptor.Replace('/', '.')))
                        throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayStoreException", "arraycopy: incompatible element"), 0);
                    dst.Elements[dstPos + n] = temp[n];
                }
            }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "exit", "(I)V", i => { vm.Print("[JAVA] System.exit(" + i.Arguments[0].AsInt() + ") ignored\n"); return JValue.Void; });
        vm.RegisterNative(c.Name, "gc", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "identityHashCode", "(Ljava/lang/Object;)I", i =>
            JValue.Int(i.Arguments[0].RefValue is null ? 0 : RuntimeHelpers.GetHashCode(i.Arguments[0].RefValue)));
        vm.RegisterNative(c.Name, "getProperty", "(Ljava/lang/String;)Ljava/lang/String;", i =>
        {
            var key = vm.StringValue(i.Arguments[0]);
            string? value = key switch
            {
                "java.version" => "1.1.7",
                "java.vendor" => "Retro96",
                "java.class.version" => "45.3",
                "os.name" => "Windows 95",
                "os.arch" => "x86",
                "os.version" => "4.0",
                "file.separator" => "\\",
                "path.separator" => ";",
                "line.separator" => "\r\n",
                _ => null
            };
            return JValue.Ref(value == null ? null : vm.CreateString(value));
        });
        vm.SetStatic(c, "out", "Ljava/io/PrintStream;", JValue.Ref(new JObject { Class = vm.LoadClass("java.io.PrintStream"), NativeState = "stdout" }));
        vm.SetStatic(c, "err", "Ljava/io/PrintStream;", JValue.Ref(new JObject { Class = vm.LoadClass("java.io.PrintStream"), NativeState = "stderr" }));
    }

    private static void RegisterPrintStream(JavaVm vm, JClass c)
    {
        foreach (var d in new[] { "(Ljava/lang/String;)V", "(Ljava/lang/Object;)V", "(I)V", "(J)V", "(F)V", "(D)V", "(C)V", "(Z)V" })
        {
            string desc = d;
            vm.RegisterNative(c.Name, "print", desc, i => { vm.Print(ArgToString(vm, i, desc)); return JValue.Void; });
            vm.RegisterNative(c.Name, "println", desc, i => { vm.Print(ArgToString(vm, i, desc) + "\n"); return JValue.Void; });
        }
        vm.RegisterNative(c.Name, "println", "()V", i => { vm.Print("\n"); return JValue.Void; });
        vm.RegisterNative(c.Name, "flush", "()V", _ => JValue.Void);
    }

    private static string ArgToString(JavaVm vm, JavaInvocation i, string desc)
    {
        if (i.Arguments.Length == 0) return "";
        var v = i.Arguments[0];
        return desc[1] switch
        {
            'L' or '[' => vm.ToJavaString(v),
            'I' => v.AsInt().ToString(CultureInfo.InvariantCulture),
            'J' => v.AsLong().ToString(CultureInfo.InvariantCulture),
            'F' => JavaText.FloatStr(v.AsFloat()),
            'D' => JavaText.DoubleStr(v.AsDouble()),
            'C' => ((char)v.AsInt()).ToString(),
            'Z' => v.AsInt() != 0 ? "true" : "false",
            _ => ""
        };
    }

    private static void RegisterMath(JavaVm vm, JClass c)
    {
        // abs(MIN_VALUE) wraps back to MIN_VALUE, it does not overflow.
        vm.RegisterNative(c.Name, "abs", "(I)I", i => { int v = i.Arguments[0].AsInt(); return JValue.Int(v == int.MinValue ? v : Math.Abs(v)); });
        vm.RegisterNative(c.Name, "abs", "(J)J", i => { long v = i.Arguments[0].AsLong(); return JValue.Long(v == long.MinValue ? v : Math.Abs(v)); });
        vm.RegisterNative(c.Name, "abs", "(F)F", i => JValue.Float(Math.Abs(i.Arguments[0].AsFloat())));
        vm.RegisterNative(c.Name, "abs", "(D)D", i => JValue.Double(Math.Abs(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "max", "(II)I", i => JValue.Int(Math.Max(i.Arguments[0].AsInt(), i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "max", "(JJ)J", i => JValue.Long(Math.Max(i.Arguments[0].AsLong(), i.Arguments[1].AsLong())));
        vm.RegisterNative(c.Name, "max", "(FF)F", i => JValue.Float(Math.Max(i.Arguments[0].AsFloat(), i.Arguments[1].AsFloat())));
        vm.RegisterNative(c.Name, "max", "(DD)D", i => JValue.Double(Math.Max(i.Arguments[0].AsDouble(), i.Arguments[1].AsDouble())));
        vm.RegisterNative(c.Name, "min", "(II)I", i => JValue.Int(Math.Min(i.Arguments[0].AsInt(), i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "min", "(JJ)J", i => JValue.Long(Math.Min(i.Arguments[0].AsLong(), i.Arguments[1].AsLong())));
        vm.RegisterNative(c.Name, "min", "(FF)F", i => JValue.Float(Math.Min(i.Arguments[0].AsFloat(), i.Arguments[1].AsFloat())));
        vm.RegisterNative(c.Name, "min", "(DD)D", i => JValue.Double(Math.Min(i.Arguments[0].AsDouble(), i.Arguments[1].AsDouble())));
        vm.RegisterNative(c.Name, "sin", "(D)D", i => JValue.Double(Math.Sin(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "cos", "(D)D", i => JValue.Double(Math.Cos(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "tan", "(D)D", i => JValue.Double(Math.Tan(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "asin", "(D)D", i => JValue.Double(Math.Asin(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "acos", "(D)D", i => JValue.Double(Math.Acos(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "atan", "(D)D", i => JValue.Double(Math.Atan(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "atan2", "(DD)D", i => JValue.Double(Math.Atan2(i.Arguments[0].AsDouble(), i.Arguments[1].AsDouble())));
        vm.RegisterNative(c.Name, "pow", "(DD)D", i => JValue.Double(Math.Pow(i.Arguments[0].AsDouble(), i.Arguments[1].AsDouble())));
        vm.RegisterNative(c.Name, "sqrt", "(D)D", i => JValue.Double(Math.Sqrt(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "exp", "(D)D", i => JValue.Double(Math.Exp(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "log", "(D)D", i => JValue.Double(Math.Log(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "floor", "(D)D", i => JValue.Double(Math.Floor(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "ceil", "(D)D", i => JValue.Double(Math.Ceiling(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "rint", "(D)D", i => JValue.Double(Math.Round(i.Arguments[0].AsDouble(), MidpointRounding.ToEven)));
        // round = floor(x + 0.5) with NaN -> 0 and infinities clamped.
        vm.RegisterNative(c.Name, "round", "(F)I", i => JValue.Int(ClampToInt(Math.Floor(i.Arguments[0].AsFloat() + 0.5f))));
        vm.RegisterNative(c.Name, "round", "(D)J", i => JValue.Long(ClampToLong(Math.Floor(i.Arguments[0].AsDouble() + 0.5))));
        vm.RegisterNative(c.Name, "random", "()D", _ => JValue.Double(Random.Shared.NextDouble()));
        vm.RegisterNative(c.Name, "toRadians", "(D)D", i => JValue.Double(i.Arguments[0].AsDouble() * Math.PI / 180.0));
        vm.RegisterNative(c.Name, "toDegrees", "(D)D", i => JValue.Double(i.Arguments[0].AsDouble() * 180.0 / Math.PI));
        vm.RegisterNative(c.Name, "IEEEremainder", "(DD)D", i => JValue.Double(Math.IEEERemainder(i.Arguments[0].AsDouble(), i.Arguments[1].AsDouble())));
        vm.SetStatic(c, "PI", "D", JValue.Double(Math.PI));
        vm.SetStatic(c, "E", "D", JValue.Double(Math.E));
    }

    private static int ClampToInt(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v >= int.MaxValue) return int.MaxValue;
        if (v <= int.MinValue) return int.MinValue;
        return (int)v;
    }

    private static long ClampToLong(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v >= long.MaxValue) return long.MaxValue;
        if (v <= long.MinValue) return long.MinValue;
        return (long)v;
    }
}

// Java-authentic float/double text rendering ("1.0" not "1").
internal static class JavaText
{
    public static string FloatStr(float f)
    {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsPositiveInfinity(f)) return "Infinity";
        if (float.IsNegativeInfinity(f)) return "-Infinity";
        var s = f.ToString("R", CultureInfo.InvariantCulture);
        return s.IndexOf('.') < 0 && s.IndexOfAny(new[] { 'E', 'e' }) < 0 ? s + ".0" : s;
    }

    public static string DoubleStr(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.IndexOf('.') < 0 && s.IndexOfAny(new[] { 'E', 'e' }) < 0 ? s + ".0" : s;
    }
}
