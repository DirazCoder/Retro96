using System.Globalization;
using System.Runtime.CompilerServices;

namespace Retro96.Engine.Java;

// Natives for java.lang numeric wrappers, Character and Thread.
internal static class JavaStdlibWrappers
{
    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.lang.Integer": RegisterInteger(vm, c); break;
            case "java.lang.Long": RegisterLong(vm, c); break;
            case "java.lang.Float": RegisterFloat(vm, c); break;
            case "java.lang.Double": RegisterDouble(vm, c); break;
            case "java.lang.Boolean": RegisterBoolean(vm, c); break;
            case "java.lang.Byte": RegisterByte(vm, c); break;
            case "java.lang.Short": RegisterShort(vm, c); break;
            case "java.lang.Character": RegisterCharacter(vm, c); break;
            case "java.lang.Thread": RegisterThread(vm, c); break;
        }
    }

    // Shared value conversion across wrappers so intValue/longValue/
    // floatValue/doubleValue work no matter which box holds the number.
    private static double UnboxNumber(JObject o) => o.NativeState switch
    {
        int i => i,
        long l => l,
        float f => f,
        double d => d,
        sbyte b => b,
        short s => s,
        _ => 0.0
    };

    private static void RegisterNumberMethods(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int((int)Math.Clamp(UnboxNumber(i.Receiver.AsObject()!), int.MinValue, int.MaxValue)));
        vm.RegisterNative(c.Name, "longValue", "()J", i => JValue.Long((long)Math.Clamp(UnboxNumber(i.Receiver.AsObject()!), long.MinValue, long.MaxValue)));
        vm.RegisterNative(c.Name, "floatValue", "()F", i => JValue.Float((float)UnboxNumber(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "doubleValue", "()D", i => JValue.Double(UnboxNumber(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "hashCode", "()I", i => JValue.Int((int)UnboxNumber(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
        {
            var a = i.Receiver.AsObject()!;
            var b = i.Arguments[0].AsObject();
            return JValue.Int(b != null && b.Class.Name == a.Class.Name && UnboxNumber(a) == UnboxNumber(b) ? 1 : 0);
        });
    }

    private static void RegisterInteger(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = ParseInt(vm, i.Arguments[0], 10); return JValue.Void; });
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is int v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is int v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(II)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(ToStringRadix(i.Arguments[0].AsInt(), i.Arguments[1].AsInt()))));
        vm.RegisterNative(c.Name, "parseInt", "(Ljava/lang/String;)I", i => JValue.Int(ParseInt(vm, i.Arguments[0], 10)));
        vm.RegisterNative(c.Name, "parseInt", "(Ljava/lang/String;I)I", i => JValue.Int(ParseInt(vm, i.Arguments[0], i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "valueOf", "(I)Ljava/lang/Integer;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsInt() }));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/String;)Ljava/lang/Integer;", i => JValue.Ref(new JObject { Class = c, NativeState = ParseInt(vm, i.Arguments[0], 10) }));
        vm.RegisterNative(c.Name, "toHexString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(((uint)i.Arguments[0].AsInt()).ToString("x", CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toBinaryString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(Convert.ToString(i.Arguments[0].AsInt(), 2))));
        vm.RegisterNative(c.Name, "toOctalString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(Convert.ToString(i.Arguments[0].AsInt(), 8))));
        RegisterNumberMethods(vm, c);
        vm.SetStatic(c, "MAX_VALUE", "I", JValue.Int(int.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "I", JValue.Int(int.MinValue));
        vm.SetStatic(c, "TYPE", "Ljava/lang/Class;", JValue.Ref(vm.CreateClassObject(vm.LoadClass("int"))));
    }

    private static void RegisterLong(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(J)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsLong(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = ParseLong(vm, i.Arguments[0], 10); return JValue.Void; });
        vm.RegisterNative(c.Name, "longValue", "()J", i => JValue.Long(i.Receiver.AsObject()?.NativeState is long v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is long v ? v : 0L).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(J)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsLong().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(JI)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(ToStringRadix(i.Arguments[0].AsLong(), i.Arguments[1].AsInt()))));
        vm.RegisterNative(c.Name, "parseLong", "(Ljava/lang/String;)J", i => JValue.Long(ParseLong(vm, i.Arguments[0], 10)));
        vm.RegisterNative(c.Name, "parseLong", "(Ljava/lang/String;I)J", i => JValue.Long(ParseLong(vm, i.Arguments[0], i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "valueOf", "(J)Ljava/lang/Long;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsLong() }));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/String;)Ljava/lang/Long;", i => JValue.Ref(new JObject { Class = c, NativeState = ParseLong(vm, i.Arguments[0], 10) }));
        vm.RegisterNative(c.Name, "toHexString", "(J)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(((ulong)i.Arguments[0].AsLong()).ToString("x", CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toBinaryString", "(J)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(Convert.ToString(i.Arguments[0].AsLong(), 2))));
        RegisterNumberMethods(vm, c);
        vm.SetStatic(c, "MAX_VALUE", "J", JValue.Long(long.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "J", JValue.Long(long.MinValue));
        vm.SetStatic(c, "TYPE", "Ljava/lang/Class;", JValue.Ref(vm.CreateClassObject(vm.LoadClass("long"))));
    }

    private static void RegisterFloat(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(F)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsFloat(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = ParseFloat(vm, i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "floatValue", "()F", i => JValue.Float(i.Receiver.AsObject()?.NativeState is float v ? v : 0f));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.FloatStr(i.Receiver.AsObject()?.NativeState is float v ? v : 0f))));
        vm.RegisterNative(c.Name, "toString", "(F)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.FloatStr(i.Arguments[0].AsFloat()))));
        vm.RegisterNative(c.Name, "valueOf", "(F)Ljava/lang/Float;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsFloat() }));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/String;)Ljava/lang/Float;", i => JValue.Ref(new JObject { Class = c, NativeState = ParseFloat(vm, i.Arguments[0]) }));
        vm.RegisterNative(c.Name, "parseFloat", "(Ljava/lang/String;)F", i => JValue.Float(ParseFloat(vm, i.Arguments[0])));
        vm.RegisterNative(c.Name, "isNaN", "(F)Z", i => JValue.Int(float.IsNaN(i.Arguments[0].AsFloat()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isInfinite", "(F)Z", i => JValue.Int(float.IsInfinity(i.Arguments[0].AsFloat()) ? 1 : 0));
        vm.RegisterNative(c.Name, "floatToIntBits", "(F)I", i => JValue.Int(FloatToIntBits(i.Arguments[0].AsFloat())));
        vm.RegisterNative(c.Name, "intBitsToFloat", "(I)F", i => JValue.Float(BitConverter.Int32BitsToSingle(i.Arguments[0].AsInt())));
        RegisterNumberMethods(vm, c);
        vm.SetStatic(c, "MAX_VALUE", "F", JValue.Float(float.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "F", JValue.Float(float.Epsilon));
        vm.SetStatic(c, "POSITIVE_INFINITY", "F", JValue.Float(float.PositiveInfinity));
        vm.SetStatic(c, "NEGATIVE_INFINITY", "F", JValue.Float(float.NegativeInfinity));
        vm.SetStatic(c, "NaN", "F", JValue.Float(float.NaN));
        vm.SetStatic(c, "TYPE", "Ljava/lang/Class;", JValue.Ref(vm.CreateClassObject(vm.LoadClass("float"))));
    }

    private static void RegisterDouble(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(D)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsDouble(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = ParseDouble(vm, i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "doubleValue", "()D", i => JValue.Double(i.Receiver.AsObject()?.NativeState is double v ? v : 0.0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.DoubleStr(i.Receiver.AsObject()?.NativeState is double v ? v : 0.0))));
        vm.RegisterNative(c.Name, "toString", "(D)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.DoubleStr(i.Arguments[0].AsDouble()))));
        vm.RegisterNative(c.Name, "valueOf", "(D)Ljava/lang/Double;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsDouble() }));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/String;)Ljava/lang/Double;", i => JValue.Ref(new JObject { Class = c, NativeState = ParseDouble(vm, i.Arguments[0]) }));
        vm.RegisterNative(c.Name, "parseDouble", "(Ljava/lang/String;)D", i => JValue.Double(ParseDouble(vm, i.Arguments[0])));
        vm.RegisterNative(c.Name, "isNaN", "(D)Z", i => JValue.Int(double.IsNaN(i.Arguments[0].AsDouble()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isInfinite", "(D)Z", i => JValue.Int(double.IsInfinity(i.Arguments[0].AsDouble()) ? 1 : 0));
        vm.RegisterNative(c.Name, "doubleToLongBits", "(D)J", i => JValue.Long(DoubleToLongBits(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "longBitsToDouble", "(J)D", i => JValue.Double(BitConverter.Int64BitsToDouble(i.Arguments[0].AsLong())));
        RegisterNumberMethods(vm, c);
        vm.SetStatic(c, "MAX_VALUE", "D", JValue.Double(double.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "D", JValue.Double(double.Epsilon));
        vm.SetStatic(c, "POSITIVE_INFINITY", "D", JValue.Double(double.PositiveInfinity));
        vm.SetStatic(c, "NEGATIVE_INFINITY", "D", JValue.Double(double.NegativeInfinity));
        vm.SetStatic(c, "NaN", "D", JValue.Double(double.NaN));
        vm.SetStatic(c, "TYPE", "Ljava/lang/Class;", JValue.Ref(vm.CreateClassObject(vm.LoadClass("double"))));
    }

    private static void RegisterBoolean(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Z)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsInt() != 0; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = string.Equals(vm.StringValue(i.Arguments[0]), "true", StringComparison.OrdinalIgnoreCase); return JValue.Void; });
        vm.RegisterNative(c.Name, "booleanValue", "()Z", i => JValue.Int(i.Receiver.AsObject()?.NativeState is true ? 1 : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Receiver.AsObject()?.NativeState is true ? "true" : "false")));
        vm.RegisterNative(c.Name, "toString", "(Z)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt() != 0 ? "true" : "false")));
        vm.RegisterNative(c.Name, "valueOf", "(Z)Ljava/lang/Boolean;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsInt() != 0 }));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/String;)Ljava/lang/Boolean;", i => JValue.Ref(new JObject { Class = c, NativeState = string.Equals(vm.StringValue(i.Arguments[0]), "true", StringComparison.OrdinalIgnoreCase) }));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
            JValue.Int(i.Receiver.AsObject()?.NativeState is bool a && i.Arguments[0].AsObject()?.NativeState is bool b && a == b ? 1 : 0));
        vm.SetStatic(c, "TRUE", "Ljava/lang/Boolean;", JValue.Ref(new JObject { Class = c, NativeState = true }));
        vm.SetStatic(c, "FALSE", "Ljava/lang/Boolean;", JValue.Ref(new JObject { Class = c, NativeState = false }));
        vm.SetStatic(c, "TYPE", "Ljava/lang/Class;", JValue.Ref(vm.CreateClassObject(vm.LoadClass("boolean"))));
    }

    private static void RegisterByte(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(B)V", i => { i.Receiver.AsObject()!.NativeState = unchecked((sbyte)i.Arguments[0].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = (sbyte)ParseInt(vm, i.Arguments[0], 10); return JValue.Void; });
        vm.RegisterNative(c.Name, "byteValue", "()B", i => JValue.Int(i.Receiver.AsObject()?.NativeState is sbyte v ? v : 0));
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is sbyte v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is sbyte v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(B)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "parseByte", "(Ljava/lang/String;)B", i => JValue.Int((sbyte)ParseInt(vm, i.Arguments[0], 10)));
        vm.RegisterNative(c.Name, "valueOf", "(B)Ljava/lang/Byte;", i => JValue.Ref(new JObject { Class = c, NativeState = unchecked((sbyte)i.Arguments[0].AsInt()) }));
        RegisterNumberMethods(vm, c);
        vm.SetStatic(c, "MAX_VALUE", "B", JValue.Int(127));
        vm.SetStatic(c, "MIN_VALUE", "B", JValue.Int(-128));
    }

    private static void RegisterShort(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(S)V", i => { i.Receiver.AsObject()!.NativeState = unchecked((short)i.Arguments[0].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = (short)ParseInt(vm, i.Arguments[0], 10); return JValue.Void; });
        vm.RegisterNative(c.Name, "shortValue", "()S", i => JValue.Int(i.Receiver.AsObject()?.NativeState is short v ? v : 0));
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is short v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is short v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(S)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "parseShort", "(Ljava/lang/String;)S", i => JValue.Int((short)ParseInt(vm, i.Arguments[0], 10)));
        vm.RegisterNative(c.Name, "valueOf", "(S)Ljava/lang/Short;", i => JValue.Ref(new JObject { Class = c, NativeState = unchecked((short)i.Arguments[0].AsInt()) }));
        RegisterNumberMethods(vm, c);
        vm.SetStatic(c, "MAX_VALUE", "S", JValue.Int(32767));
        vm.SetStatic(c, "MIN_VALUE", "S", JValue.Int(-32768));
    }

    private static void RegisterCharacter(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(C)V", i => { i.Receiver.AsObject()!.NativeState = (char)i.Arguments[0].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "charValue", "()C", i => JValue.Int(i.Receiver.AsObject()?.NativeState is char ch ? ch : '\0'));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
            JValue.Int(i.Receiver.AsObject()?.NativeState is char a && i.Arguments[0].AsObject()?.NativeState is char b && a == b ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is char ch ? ch : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Receiver.AsObject()?.NativeState is char ch ? ch.ToString() : "\0")));
        vm.RegisterNative(c.Name, "isDigit", "(C)Z", i => JValue.Int(char.IsDigit((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isLetter", "(C)Z", i => JValue.Int(char.IsLetter((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isLetterOrDigit", "(C)Z", i => JValue.Int(char.IsLetterOrDigit((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isSpace", "(C)Z", i => { var ch = (char)i.Arguments[0].AsInt(); return JValue.Int(ch is ' ' or '\t' or '\n' or '\f' or '\r' ? 1 : 0); });
        vm.RegisterNative(c.Name, "isWhitespace", "(C)Z", i => JValue.Int(char.IsWhiteSpace((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isUpperCase", "(C)Z", i => JValue.Int(char.IsUpper((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isLowerCase", "(C)Z", i => JValue.Int(char.IsLower((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "toLowerCase", "(C)C", i => JValue.Int(char.ToLowerInvariant((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "toUpperCase", "(C)C", i => JValue.Int(char.ToUpperInvariant((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "digit", "(CI)I", i =>
        {
            var ch = (char)i.Arguments[0].AsInt();
            int radix = i.Arguments[1].AsInt();
            if (radix < 2 || radix > 36) return JValue.Int(-1);
            int v = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'z' => ch - 'a' + 10,
                >= 'A' and <= 'Z' => ch - 'A' + 10,
                _ => -1
            };
            return JValue.Int(v < radix ? v : -1);
        });
        vm.RegisterNative(c.Name, "forDigit", "(II)C", i =>
        {
            int v = i.Arguments[0].AsInt(), radix = i.Arguments[1].AsInt();
            if (radix < 2 || radix > 36 || v < 0 || v >= radix) return JValue.Int('\0');
            return JValue.Int(v < 10 ? '0' + v : 'a' + v - 10);
        });
        vm.RegisterNative(c.Name, "getNumericValue", "(C)I", i =>
        {
            var ch = (char)i.Arguments[0].AsInt();
            if (char.IsAsciiDigit(ch)) return JValue.Int(ch - '0');
            if (char.IsLetter(ch)) return JValue.Int(char.ToUpperInvariant(ch) - 'A' + 10);
            return JValue.Int(-1);
        });
        vm.SetStatic(c, "MIN_VALUE", "C", JValue.Int(0));
        vm.SetStatic(c, "MAX_VALUE", "C", JValue.Int(65535));
        vm.SetStatic(c, "TYPE", "Ljava/lang/Class;", JValue.Ref(vm.CreateClassObject(vm.LoadClass("char"))));
    }

    private static void RegisterThread(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Runnable;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Target = i.Arguments[0].AsObject() }; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Name = vm.StringValue(i.Arguments[0]) }; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/ThreadGroup;Ljava/lang/Runnable;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Target = i.Arguments[1].AsObject() }; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Runnable;Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Target = i.Arguments[0].AsObject(), Name = vm.StringValue(i.Arguments[1]) }; return JValue.Void; });
        vm.RegisterNative(c.Name, "start", "()V", i =>
        {
            var o = i.Receiver.AsObject()!;
            var st = (JavaThreadNativeState)(o.NativeState ??= new JavaThreadNativeState());
            if (st.Thread != null) return JValue.Void;
            st.Thread = new Thread(() =>
            {
                JavaStdlibWrappers.CurrentThreadObject = o;
                st.Alive = true;
                try
                {
                    if (st.Target != null) vm.InvokeVirtual(st.Target, "run", "()V");
                    else vm.InvokeVirtual(o, "run", "()V");
                }
                catch (JvmException jex) { vm.Print("[Java Thread] uncaught " + jex.Object.Class.Name + "\n"); }
                catch (Exception ex) { vm.Print("[Java Thread] " + ex.Message + "\n"); }
                finally { st.Alive = false; }
            })
            { IsBackground = true };
            vm.LiveThreads.TryAdd(st, 0);
            st.Alive = true; // visible between start() and run() completion
            st.Thread.Start();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "run", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "sleep", "(J)V", i => { Sleep(vm, i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "sleep", "(JI)V", i => { Sleep(vm, i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "stop", "()V", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s)
            {
                s.Alive = false;
                try { s.Thread?.Interrupt(); } catch { }
            }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "interrupt", "()V", i =>
        {
            if (i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s)
            {
                s.Interrupted = true;
                try { s.Thread?.Interrupt(); } catch { }
            }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "isInterrupted", "()Z", i => JValue.Int(i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s && s.Interrupted ? 1 : 0));
        vm.RegisterNative(c.Name, "interrupted", "()Z", i =>
        {
            var st = CurrentThreadObject?.NativeState as JavaThreadNativeState;
            if (st == null) return JValue.Int(0);
            bool was = st.Interrupted;
            st.Interrupted = false;
            return JValue.Int(was ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "isAlive", "()Z", i => JValue.Int(i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s && s.Alive ? 1 : 0));
        vm.RegisterNative(c.Name, "join", "()V", i => { (i.Receiver.AsObject()?.NativeState as JavaThreadNativeState)?.Thread?.Join(); return JValue.Void; });
        vm.RegisterNative(c.Name, "join", "(J)V", i => { (i.Receiver.AsObject()?.NativeState as JavaThreadNativeState)?.Thread?.Join((int)Math.Clamp(i.Arguments[0].AsLong(), 0, int.MaxValue)); return JValue.Void; });
        vm.RegisterNative(c.Name, "setName", "(Ljava/lang/String;)V", i => { if (i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s) s.Name = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "getName", "()Ljava/lang/String;", i =>
        {
            var o = i.Receiver.AsObject()!;
            var name = (o.NativeState as JavaThreadNativeState)?.Name;
            return JValue.Ref(vm.CreateString(name ?? "Thread-" + RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture)));
        });
        vm.RegisterNative(c.Name, "setDaemon", "(Z)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "isDaemon", "()Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "setPriority", "(I)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getPriority", "()I", _ => JValue.Int(5));
        vm.RegisterNative(c.Name, "yield", "()V", _ => { Thread.Sleep(0); return JValue.Void; });
        vm.RegisterNative(c.Name, "currentThread", "()Ljava/lang/Thread;", i =>
        {
            if (CurrentThreadObject != null) return JValue.Ref(CurrentThreadObject);
            CurrentThreadObject = new JObject { Class = c, NativeState = new JavaThreadNativeState { Alive = true } };
            return JValue.Ref(CurrentThreadObject);
        });
        vm.SetStatic(c, "MIN_PRIORITY", "I", JValue.Int(1));
        vm.SetStatic(c, "NORM_PRIORITY", "I", JValue.Int(5));
        vm.SetStatic(c, "MAX_PRIORITY", "I", JValue.Int(10));
    }

    private static void Sleep(JavaVm vm, long millis)
    {
        try { Thread.Sleep((int)Math.Clamp(millis, 0, int.MaxValue)); }
        catch (ThreadInterruptedException) { throw new JvmException(vm.CreateExceptionObject("java.lang.InterruptedException"), -1); }
    }

    [ThreadStatic] internal static JObject? CurrentThreadObject;

    // Parsing helpers with Java NumberFormatException semantics.

    private static int ParseInt(JavaVm vm, JValue text, int radix) => (int)ParseIntegerCore(vm, text, radix, isLong: false);

    private static long ParseLong(JavaVm vm, JValue text, int radix) => ParseIntegerCore(vm, text, radix, isLong: true);

    private static long ParseIntegerCore(JavaVm vm, JValue text, int radix, bool isLong)
    {
        var s = vm.StringValue(text);
        string message = "For input string: \"" + s + "\"";
        if (radix < 2 || radix > 36 || s.Length == 0)
            throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException", message), -1);
        int i = 0;
        bool negative = false;
        if (s[0] is '+' or '-')
        {
            negative = s[0] == '-';
            i = 1;
            if (s.Length == 1)
                throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException", message), -1);
        }
        // Accumulate negatively (Java's algorithm) so a single limit bound
        // covers both signs without overflow.
        long limit = negative
            ? (isLong ? long.MinValue : int.MinValue)
            : -(isLong ? long.MaxValue : int.MaxValue);
        long multmin = limit / radix;
        long value = 0;
        for (; i < s.Length; i++)
        {
            int digit = s[i] switch
            {
                >= '0' and <= '9' => s[i] - '0',
                >= 'a' and <= 'z' => s[i] - 'a' + 10,
                >= 'A' and <= 'Z' => s[i] - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= radix)
                throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException", message), -1);
            if (value < multmin)
                throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException", message), -1);
            value = value * radix - digit;
        }
        if (value < limit)
            throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException", message), -1);
        if (!negative) value = -value;
        return value;
    }

    private static float ParseFloat(JavaVm vm, JValue text) => (float)ParseFloating(vm, text);

    private static double ParseDouble(JavaVm vm, JValue text) => ParseFloating(vm, text);

    private static double ParseFloating(JavaVm vm, JValue text)
    {
        var s = vm.StringValue(text);
        switch (s)
        {
            case "NaN": return double.NaN;
            case "Infinity": return double.PositiveInfinity;
            case "-Infinity": return double.NegativeInfinity;
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException", "For input string: \"" + s + "\""), -1);
        return value;
    }

    // Canonical NaN bits: floatToIntBits(NaN) must be exactly 0x7FC00000.
    private static int FloatToIntBits(float v) =>
        float.IsNaN(v) ? unchecked((int)0x7FC00000) : BitConverter.SingleToInt32Bits(v);

    private static long DoubleToLongBits(double v) =>
        double.IsNaN(v) ? unchecked(0x7FF8000000000000L) : BitConverter.DoubleToInt64Bits(v);

    private static string ToStringRadix(long value, int radix)
    {
        if (radix < 2 || radix > 36) radix = 10;
        if (value == 0) return "0";
        bool negative = value < 0;
        ulong magnitude = negative ? (ulong)(-value) : (ulong)value;
        Span<char> buffer = stackalloc char[65];
        int pos = buffer.Length;
        while (magnitude > 0)
        {
            int digit = (int)(magnitude % (ulong)radix);
            magnitude /= (ulong)radix;
            buffer[--pos] = digit < 10 ? (char)('0' + digit) : (char)('a' + digit - 10);
        }
        if (negative) buffer[--pos] = '-';
        return new string(buffer[pos..]);
    }
}

public sealed class JavaThreadNativeState
{
    public Thread? Thread;
    public JObject? Target;
    public bool Alive;
    public bool Interrupted;
    public string Name = "";
}
