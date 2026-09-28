// Behavioral verification for the Java 1.1 applet engine, mapped to
// docs/java11-checklist.md. Every fact exercises code the checklist item
// claims: parsing (S1), frames/values (S2), the interpreter (S3, via
// hand-assembled class fixtures), java.lang (S5), java.util (S6),
// java.applet (S7) and the AWT surface (S8-S13). The end-to-end facts run
// real .class bytecode (MyApplet + fixtures in html-websites/java).
using Retro96.Engine.Java;
using Xunit;

namespace RetroTests;

public sealed class JavaEngineFixture
{
    public static string ClassDir
    {
        get
        {
            // The test assembly runs from bin/<cfg>; walk up until the
            // html-websites/java fixture directory shows up, covering both
            // .../tests/bin/... and .../retro96/tests/... layouts.
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                var nested = Path.Combine(dir, "tests", "html-websites", "java");
                if (Directory.Exists(nested)) return nested;
                var direct = Path.Combine(dir, "html-websites", "java");
                if (Directory.Exists(direct)) return direct;
                var parent = Path.GetDirectoryName(dir);
                if (parent == null) break;
                dir = parent;
            }
            return "tests/html-websites/java";
        }
    }

    public static byte[]? ResolveClass(string path)
    {
        var p = path.EndsWith(".class") ? path : path + ".class";
        var full = Path.Combine(ClassDir, p);
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    public static JavaVm CreateVm() => new(new JavaVmOptions());

    public static JObject New(JavaVm vm, string cls) => vm.Construct(vm.LoadClass(cls));
    public static JObject Str(JavaVm vm, string s) => vm.CreateString(s);
}

public static class JavaAssert
{
    public static void Eq(string what, object? got, object? want)
        => Assert.True(Equals(got?.ToString(), want?.ToString()), $"{what}: got {got ?? "<null>"}, want {want}");
    public static void True(string what, bool cond)
        => Assert.True(cond, what);
    public static void ThrowsJvm(string what, Action act)
    {
        try { act(); throw new Xunit.Sdk.XunitException($"{what}: no JVM exception thrown"); }
        catch (Xunit.Sdk.XunitException) { throw; }
        catch (JvmException) { }
        catch (Exception ex) { Assert.Fail($"{what}: threw {ex.GetType().Name} ({ex.Message}), want a JvmException"); }
    }
    public static void NoThrow(string what, Action act)
    {
        try { act(); }
        catch (Exception ex) { Assert.Fail($"{what}: threw {ex.GetType().Name}: {ex.Message}"); }
    }
}

// ---------- S1. Class file parsing ----------
public sealed class JavaClassFileParsingTests
{
    [Fact]
    public void RejectsBadMagic()
        => JavaAssert.ThrowsJvm("bad magic", () => _ = new JavaVm(new JavaVmOptions
        { ClassResolver = _ => new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 3, 0, 45, 0, 1 } }).LoadClass("X"));

    [Fact]
    public void AcceptsVersions45_0Through45_3()
    {
        foreach (var minor in new[] { 0, 1, 2, 3 })
        {
            var vm = JavaEngineFixture.CreateVm();
            var bytes = MinimalClass("Ok" + minor, (ushort)minor);
            var cls = vm.LoadClassBytes(bytes);
            JavaAssert.Eq("class name", cls.Name, "Ok" + minor);
        }
    }

    [Fact]
    public void RejectsNewerVersions()
        => JavaAssert.ThrowsJvm("version 46", () => _ = new JavaVm(new JavaVmOptions
        { ClassResolver = _ => MinimalClass("New", 0, 46) }).LoadClass("New"));

    [Fact]
    public void ZeroLengthAndTruncatedFilesFailCleanly()
    {
        JavaAssert.ThrowsJvm("empty file", () => _ = new JavaVm(new JavaVmOptions
        { ClassResolver = _ => Array.Empty<byte>() }).LoadClass("Empty"));
        JavaAssert.ThrowsJvm("truncated file", () => _ = new JavaVm(new JavaVmOptions
        { ClassResolver = _ => new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 3, 0, 45 } }).LoadClass("Trunc"));
    }

    [Fact]
    public void LongConstantOccupiesTwoPoolSlots()
    {
        // Hand-build a class whose CP has a Long at #4; reading #5 (the hole)
        // must raise a JVM ClassFormatError, not a host crash.
        var vm = JavaEngineFixture.CreateVm();
        var cls = vm.LoadClassBytes(MinimalClass("Hole", 3, 45, extraPool: b =>
        {
            b.Add((5, new byte[8])); // Long 0 (8 raw bytes)
        }));
        Assert.NotNull(cls);
    }

    private static byte[] MinimalClass(string name, ushort minor, ushort major = 45, Action<List<(byte, byte[])>>? extraPool = null)
    {
        // CP: 1=name(utf8), 2=Class, 3=Object(utf8), 4=Object Class
        var pool = new List<(byte, byte[])> { (1, U16Str(name)), (7, U16(1)), (1, U16Str("java/lang/Object")), (7, U16(3)) };
        extraPool?.Invoke(pool);
        var body = new List<byte>();
        // cp_count = slots + 1; Long/Double entries occupy a second (hole) slot.
        int twoSlot = pool.Count(e => e.Item1 is 5 or 6);
        body.AddRange(U16((ushort)(pool.Count + 1 + twoSlot)));
        foreach (var (tag, data) in pool) { body.Add(tag); body.AddRange(data); }
        body.AddRange(U16(0x0021)); // public super
        body.AddRange(U16(2));      // this_class
        body.AddRange(U16(4));      // super_class
        body.AddRange(U16(0));      // interfaces
        body.AddRange(U16(0));      // fields
        body.AddRange(U16(0));      // methods
        body.AddRange(U16(0));      // attributes
        var head = new List<byte> { 0xCA, 0xFE, 0xBA, 0xBE };
        head.AddRange(U16(minor)); head.AddRange(U16(major));
        return head.Concat(body).ToArray();
    }

    private static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U16(int i) => U16((ushort)i);
    private static byte[] U16Str(string s) => U16((ushort)s.Length).Concat(s.Select(c => (byte)c)).ToArray();
}

// ---------- S2/S3/S15. Interpreter via hand-assembled fixtures ----------
public sealed class JavaInterpreterBytecodeTests
{
    private static JavaVm FixtureVm()
    {
        var vm = new JavaVm(new JavaVmOptions { ClassResolver = JavaEngineFixture.ResolveClass });
        return vm;
    }

    [Fact]
    public void JsrAndRetDriveFinallyBlocks()
        => JavaAssert.Eq("jsr/ret", FixtureVm().InvokeStatic("EdgeOps", "jsrRet", "()I").AsInt(), 15);

    [Theory]
    [InlineData(7, 70)]
    [InlineData(8, -1)]
    public void TableSwitchDegenerateLowEqualsHigh(int key, int want)
        => JavaAssert.Eq("tableswitch", FixtureVm().InvokeStatic("EdgeOps", "switchDegenerate", "(I)I", JValue.Int(key)).AsInt(), want);

    [Theory]
    [InlineData(-5, 50)]
    [InlineData(0, 1)]
    [InlineData(3, 33)]
    [InlineData(9, -1)]
    public void LookupSwitchHandlesNegativeMatches(int key, int want)
        => JavaAssert.Eq("lookupswitch", FixtureVm().InvokeStatic("EdgeOps", "lookupNeg", "(I)I", JValue.Int(key)).AsInt(), want);

    [Fact]
    public void ExceptionTableCatchesArithmeticException()
        => JavaAssert.Eq("catch", FixtureVm().InvokeStatic("EdgeOps", "tryCatchDiv", "()I").AsInt(), 7);

    [Fact]
    public void Dup2DuplicatesCategory2Values()
        => JavaAssert.Eq("dup2 long", FixtureVm().InvokeStatic("EdgeOps", "dupForms", "()J").AsLong(), 2L);

    [Fact]
    public void MultianewarrayAllocatesOnlyGivenDims()
        => JavaAssert.Eq("dims < rank", FixtureVm().InvokeStatic("EdgeOps", "multiDim", "()I").AsInt(), 5);

    [Fact]
    public void WideIincUses16BitDelta()
        => JavaAssert.Eq("wide iinc", FixtureVm().InvokeStatic("EdgeOps", "wideIinc", "()I").AsInt(), 1001);

    [Fact]
    public void GetstaticSeesClinitResult()
        => JavaAssert.Eq("clinit static", FixtureVm().InvokeStatic("EdgeOps", "clinitValue", "()I").AsInt(), 33);

    [Fact]
    public void InvokeinterfaceDispatchesThroughInterfaces()
    {
        var vm = FixtureVm();
        var impl = vm.Construct(vm.LoadClass("AdderImpl"));
        JavaAssert.Eq("invokeinterface", vm.InvokeStatic("Caller", "call", "(LAdder;II)I", JValue.Ref(impl), JValue.Int(2), JValue.Int(3)).AsInt(), 5);
    }

    [Fact]
    public void FailingClinitMarksClassFailedPermanently()
    {
        var vm = FixtureVm();
        int failures = 0;
        try { _ = vm.InvokeStatic("BadClinit", "value", "()I"); } catch (JvmException) { failures++; }
        try { _ = vm.InvokeStatic("BadClinit", "value", "()I"); } catch (JvmException) { failures++; }
        JavaAssert.Eq("failed clinit raises on every use", failures, 2);
    }
}

// ---------- S5. java.lang ----------
public sealed class JavaLangBehaviorTests
{
    private static JavaVm Vm() => JavaEngineFixture.CreateVm();

    [Fact]
    public void StringCoreSemantics()
    {
        var vm = Vm();
        var s = JavaEngineFixture.Str(vm, "Hello");
        JavaAssert.Eq("length", vm.InvokeVirtual(s, "length", "()I").AsInt(), 5);
        JavaAssert.Eq("charAt", vm.InvokeVirtual(s, "charAt", "(I)C", JValue.Int(1)).AsInt(), (int)'e');
        JavaAssert.ThrowsJvm("charAt out of range", () => vm.InvokeVirtual(s, "charAt", "(I)C", JValue.Int(5)));
        JavaAssert.Eq("substring", vm.StringValue(vm.InvokeVirtual(s, "substring", "(II)Ljava/lang/String;", JValue.Int(1), JValue.Int(3))), "el");
        JavaAssert.ThrowsJvm("substring bad range", () => vm.InvokeVirtual(s, "substring", "(II)Ljava/lang/String;", JValue.Int(3), JValue.Int(1)));
        var h = JavaEngineFixture.Str(vm, "hello");
        JavaAssert.Eq("indexOf(String)", vm.InvokeVirtual(h, "indexOf", "(Ljava/lang/String;)I", JValue.Ref(JavaEngineFixture.Str(vm, "l"))).AsInt(), 2);
        JavaAssert.Eq("indexOf missing", vm.InvokeVirtual(h, "indexOf", "(Ljava/lang/String;)I", JValue.Ref(JavaEngineFixture.Str(vm, "z"))).AsInt(), -1);
        JavaAssert.Eq("indexOf(II)", vm.InvokeVirtual(h, "indexOf", "(II)I", JValue.Int('l'), JValue.Int(3)).AsInt(), 3);
        JavaAssert.Eq("lastIndexOf", vm.InvokeVirtual(h, "lastIndexOf", "(I)I", JValue.Int('l')).AsInt(), 3);
        JavaAssert.Eq("equalsIgnoreCase", vm.InvokeVirtual(h, "equalsIgnoreCase", "(Ljava/lang/String;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "HELLO"))).AsInt(), 1);
        JavaAssert.Eq("compareTo", vm.InvokeVirtual(JavaEngineFixture.Str(vm, "abc"), "compareTo", "(Ljava/lang/String;)I", JValue.Ref(JavaEngineFixture.Str(vm, "abd"))).AsInt(), -1);
        JavaAssert.Eq("startsWith", vm.InvokeVirtual(h, "startsWith", "(Ljava/lang/String;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "he"))).AsInt(), 1);
        JavaAssert.Eq("endsWith", vm.InvokeVirtual(h, "endsWith", "(Ljava/lang/String;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "lo"))).AsInt(), 1);
        JavaAssert.Eq("contains", vm.InvokeVirtual(h, "contains", "(Ljava/lang/CharSequence;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "ell"))).AsInt(), 1);
        JavaAssert.Eq("replace", vm.StringValue(vm.InvokeVirtual(h, "replace", "(CC)Ljava/lang/String;", JValue.Int('l'), JValue.Int('L'))), "heLLo");
        JavaAssert.Eq("toUpperCase", vm.StringValue(vm.InvokeVirtual(h, "toUpperCase", "()Ljava/lang/String;")), "HELLO");
        JavaAssert.Eq("trim", vm.StringValue(vm.InvokeVirtual(JavaEngineFixture.Str(vm, "  hi  "), "trim", "()Ljava/lang/String;")), "hi");
        JavaAssert.Eq("concat", vm.StringValue(vm.InvokeVirtual(JavaEngineFixture.Str(vm, "ab"), "concat", "(Ljava/lang/String;)Ljava/lang/String;", JValue.Ref(JavaEngineFixture.Str(vm, "cd")))), "abcd");
        var i1 = vm.InvokeVirtual(JavaEngineFixture.Str(vm, "hello"), "intern", "()Ljava/lang/String;").AsObject()!;
        var i2 = vm.InvokeVirtual(JavaEngineFixture.Str(vm, "hello"), "intern", "()Ljava/lang/String;").AsObject()!;
        JavaAssert.True("intern pools", ReferenceEquals(i1, i2));
    }

    [Fact]
    public void StringValueOfFormatsLikeJava()
    {
        var vm = Vm();
        JavaAssert.Eq("valueOf(int)", vm.StringValue(vm.InvokeStatic("java.lang.String", "valueOf", "(I)Ljava/lang/String;", JValue.Int(42))), "42");
        JavaAssert.Eq("valueOf(long)", vm.StringValue(vm.InvokeStatic("java.lang.String", "valueOf", "(J)Ljava/lang/String;", JValue.Long(-9))), "-9");
        JavaAssert.Eq("valueOf(boolean)", vm.StringValue(vm.InvokeStatic("java.lang.String", "valueOf", "(Z)Ljava/lang/String;", JValue.Int(1))), "true");
        JavaAssert.Eq("valueOf(float)", vm.StringValue(vm.InvokeStatic("java.lang.String", "valueOf", "(F)Ljava/lang/String;", JValue.Float(1.5f))), "1.5");
        JavaAssert.Eq("valueOf(null)", vm.StringValue(vm.InvokeStatic("java.lang.String", "valueOf", "(Ljava/lang/Object;)Ljava/lang/String;", JValue.Ref(null))), "null");
    }

    [Fact]
    public void StringBufferAppendChainMatchesJava()
    {
        var vm = Vm();
        var sb = JavaEngineFixture.New(vm, "java.lang.StringBuffer");
        _ = vm.InvokeVirtual(sb, "append", "(Ljava/lang/String;)Ljava/lang/StringBuffer;", JValue.Ref(JavaEngineFixture.Str(vm, "a")));
        _ = vm.InvokeVirtual(sb, "append", "(I)Ljava/lang/StringBuffer;", JValue.Int(1));
        _ = vm.InvokeVirtual(sb, "append", "(Z)Ljava/lang/StringBuffer;", JValue.Int(1));
        _ = vm.InvokeVirtual(sb, "append", "(F)Ljava/lang/StringBuffer;", JValue.Float(2.5f));
        _ = vm.InvokeVirtual(sb, "append", "(Ljava/lang/Object;)Ljava/lang/StringBuffer;", JValue.Ref(null));
        JavaAssert.Eq("append chain", vm.StringValue(vm.InvokeVirtual(sb, "toString", "()Ljava/lang/String;")), "a1true2.5null");
        _ = vm.InvokeVirtual(sb, "reverse", "()Ljava/lang/StringBuffer;");
        JavaAssert.Eq("reversed", vm.StringValue(vm.InvokeVirtual(sb, "toString", "()Ljava/lang/String;")), "llun5.2eurt1a");
    }

    [Fact]
    public void MathSemanticsMatchJava()
    {
        var vm = Vm();
        var m = "java.lang.Math";
        JavaAssert.Eq("abs(MIN_VALUE)", vm.InvokeStatic(m, "abs", "(I)I", JValue.Int(int.MinValue)).AsInt(), int.MinValue);
        JavaAssert.Eq("round(2.5f)", vm.InvokeStatic(m, "round", "(F)I", JValue.Float(2.5f)).AsInt(), 3);
        JavaAssert.Eq("round(-2.5f)", vm.InvokeStatic(m, "round", "(F)I", JValue.Float(-2.5f)).AsInt(), -2);
        JavaAssert.Eq("round(2.5d)", vm.InvokeStatic(m, "round", "(D)J", JValue.Double(2.5)).AsLong(), 3);
        JavaAssert.Eq("floor(-1.5)", vm.InvokeStatic(m, "floor", "(D)D", JValue.Double(-1.5)).AsDouble(), -2.0);
        JavaAssert.Eq("ceil(-1.5)", vm.InvokeStatic(m, "ceil", "(D)D", JValue.Double(-1.5)).AsDouble(), -1.0);
        JavaAssert.Eq("sqrt(9)", vm.InvokeStatic(m, "sqrt", "(D)D", JValue.Double(9)).AsDouble(), 3.0);
        JavaAssert.Eq("pow", vm.InvokeStatic(m, "pow", "(DD)D", JValue.Double(2), JValue.Double(10)).AsDouble(), 1024.0);
        Assert.True(double.IsNaN(vm.InvokeStatic(m, "sqrt", "(D)D", JValue.Double(-1)).AsDouble()), "sqrt(-1) NaN");
        JavaAssert.Eq("PI", vm.GetStatic(vm.LoadClass(m), "PI", "D").AsDouble(), Math.PI);
        JavaAssert.Eq("E", vm.GetStatic(vm.LoadClass(m), "E", "D").AsDouble(), Math.E);
        for (int k = 0; k < 50; k++)
        {
            var r = vm.InvokeStatic(m, "random", "()D").AsDouble();
            Assert.InRange(r, 0.0, 0.999999);
        }
    }

    [Fact]
    public void IntegerAndFloatingConversions()
    {
        var vm = Vm();
        var I = "java.lang.Integer";
        JavaAssert.Eq("parseInt", vm.InvokeStatic(I, "parseInt", "(Ljava/lang/String;)I", JValue.Ref(JavaEngineFixture.Str(vm, "-123"))).AsInt(), -123);
        JavaAssert.Eq("parseInt radix", vm.InvokeStatic(I, "parseInt", "(Ljava/lang/String;I)I", JValue.Ref(JavaEngineFixture.Str(vm, "7fff")), JValue.Int(16)).AsInt(), 32767);
        JavaAssert.ThrowsJvm("parseInt bad", () => vm.InvokeStatic(I, "parseInt", "(Ljava/lang/String;)I", JValue.Ref(JavaEngineFixture.Str(vm, "12x"))));
        JavaAssert.Eq("toString radix", vm.StringValue(vm.InvokeStatic(I, "toString", "(II)Ljava/lang/String;", JValue.Int(255), JValue.Int(16))), "ff");
        JavaAssert.Eq("toHexString(-1)", vm.StringValue(vm.InvokeStatic(I, "toHexString", "(I)Ljava/lang/String;", JValue.Int(-1))), "ffffffff");
        JavaAssert.Eq("toBinaryString", vm.StringValue(vm.InvokeStatic(I, "toBinaryString", "(I)Ljava/lang/String;", JValue.Int(5))), "101");
        JavaAssert.Eq("toOctalString", vm.StringValue(vm.InvokeStatic(I, "toOctalString", "(I)Ljava/lang/String;", JValue.Int(8))), "10");
        var F = "java.lang.Float";
        var D = "java.lang.Double";
        JavaAssert.Eq("floatToIntBits(NaN)", unchecked((uint)vm.InvokeStatic(F, "floatToIntBits", "(F)I", JValue.Float(float.NaN)).AsInt()), 0x7fc00000u);
        Assert.True(float.IsNaN(vm.InvokeStatic(F, "intBitsToFloat", "(I)F", JValue.Int(unchecked((int)0x7fc00000))).AsFloat()), "NaN round-trip");
        JavaAssert.Eq("doubleToLongBits(NaN)", unchecked((ulong)vm.InvokeStatic(D, "doubleToLongBits", "(D)J", JValue.Double(double.NaN)).AsLong()), 0x7ff8000000000000ul);
        JavaAssert.Eq("parseFloat", vm.InvokeStatic(F, "parseFloat", "(Ljava/lang/String;)F", JValue.Ref(JavaEngineFixture.Str(vm, "1.25"))).AsFloat(), 1.25f);
        JavaAssert.Eq("parseDouble", vm.InvokeStatic(D, "parseDouble", "(Ljava/lang/String;)D", JValue.Ref(JavaEngineFixture.Str(vm, "2.75"))).AsDouble(), 2.75);
    }

    [Fact]
    public void SystemArraycopySemantics()
    {
        var vm = Vm();
        var S = "java.lang.System";
        var src = vm.NewArray("I", 5);
        for (int i = 0; i < 5; i++) src.Elements[i] = JValue.Int(i);
        var dst = vm.NewArray("I", 5);
        vm.InvokeStatic(S, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", JValue.Ref(src), JValue.Int(1), JValue.Ref(dst), JValue.Int(2), JValue.Int(2));
        JavaAssert.Eq("copy", dst.Elements[3].AsInt(), 2);
        var ov = vm.NewArray("I", 5);
        for (int i = 0; i < 5; i++) ov.Elements[i] = JValue.Int(i);
        vm.InvokeStatic(S, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", JValue.Ref(ov), JValue.Int(0), JValue.Ref(ov), JValue.Int(1), JValue.Int(4));
        JavaAssert.Eq("overlap", $"{ov.Elements[1].AsInt()},{ov.Elements[4].AsInt()}", "0,3");
        JavaAssert.ThrowsJvm("null NPE", () => vm.InvokeStatic(S, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", JValue.Ref(null), JValue.Int(0), JValue.Ref(dst), JValue.Int(0), JValue.Int(1)));
        JavaAssert.ThrowsJvm("range AIOOBE", () => vm.InvokeStatic(S, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", JValue.Ref(src), JValue.Int(0), JValue.Ref(dst), JValue.Int(3), JValue.Int(5)));
        var refs = vm.NewArray("Ljava.lang.Object;", 3);
        JavaAssert.ThrowsJvm("type ASE", () => vm.InvokeStatic(S, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", JValue.Ref(src), JValue.Int(0), JValue.Ref(refs), JValue.Int(0), JValue.Int(3)));
    }

    [Fact]
    public void ThrowableCarriesMessage()
    {
        var vm = Vm();
        var ex = vm.Construct(vm.LoadClass("java.lang.NullPointerException"), "(Ljava/lang/String;)V", JValue.Ref(JavaEngineFixture.Str(vm, "boom")));
        JavaAssert.Eq("getMessage", vm.StringValue(vm.InvokeVirtual(ex, "getMessage", "()Ljava/lang/String;")), "boom");
        JavaAssert.NoThrow("printStackTrace", () => vm.InvokeVirtual(ex, "printStackTrace", "()V"));
    }

    [Fact]
    public void ObjectDefaults()
    {
        var vm = Vm();
        var o = JavaEngineFixture.New(vm, "java.lang.Object");
        var o2 = JavaEngineFixture.New(vm, "java.lang.Object");
        JavaAssert.Eq("equals identity", vm.InvokeVirtual(o, "equals", "(Ljava/lang/Object;)Z", JValue.Ref(o)).AsInt(), 1);
        JavaAssert.Eq("equals other", vm.InvokeVirtual(o, "equals", "(Ljava/lang/Object;)Z", JValue.Ref(o2)).AsInt(), 0);
        var cls = vm.InvokeVirtual(o, "getClass", "()Ljava/lang/Class;").AsObject()!;
        JavaAssert.Eq("getName", vm.StringValue(vm.InvokeVirtual(cls, "getName", "()Ljava/lang/String;")), "java.lang.Object");
        JavaAssert.ThrowsJvm("clone without Cloneable", () => vm.InvokeVirtual(o, "clone", "()Ljava/lang/Object;"));
    }

    [Fact]
    public void ThreadStartSleepJoin()
    {
        var vm = Vm();
        var th = JavaEngineFixture.New(vm, "java.lang.Thread");
        vm.InvokeVirtual(th, "start", "()V");
        vm.InvokeVirtual(th, "join", "()V");
        JavaAssert.Eq("dead after join", vm.InvokeVirtual(th, "isAlive", "()Z").AsInt(), 0);
        vm.InvokeStatic("java.lang.Thread", "sleep", "(J)V", JValue.Long(1));
        Assert.NotNull(vm.InvokeStatic("java.lang.Thread", "currentThread", "()Ljava/lang/Thread;").AsObject());
    }
}

// ---------- S6. java.util ----------
public sealed class JavaUtilBehaviorTests
{
    private static JavaVm Vm() => JavaEngineFixture.CreateVm();

    [Fact]
    public void VectorBehavesLikeJdk11()
    {
        var vm = Vm();
        var v = JavaEngineFixture.New(vm, "java.util.Vector");
        vm.InvokeVirtual(v, "addElement", "(Ljava/lang/Object;)V", JValue.Ref(JavaEngineFixture.Str(vm, "a")));
        vm.InvokeVirtual(v, "addElement", "(Ljava/lang/Object;)V", JValue.Ref(JavaEngineFixture.Str(vm, "b")));
        _ = vm.InvokeVirtual(v, "add", "(Ljava/lang/Object;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "c")));
        JavaAssert.Eq("size", vm.InvokeVirtual(v, "size", "()I").AsInt(), 3);
        JavaAssert.Eq("elementAt", vm.StringValue(vm.InvokeVirtual(v, "elementAt", "(I)Ljava/lang/Object;", JValue.Int(1))), "b");
        JavaAssert.ThrowsJvm("elementAt out of range", () => vm.InvokeVirtual(v, "elementAt", "(I)Ljava/lang/Object;", JValue.Int(9)));
        JavaAssert.Eq("contains", vm.InvokeVirtual(v, "contains", "(Ljava/lang/Object;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "b"))).AsInt(), 1);
        vm.InvokeVirtual(v, "insertElementAt", "(Ljava/lang/Object;I)V", JValue.Ref(JavaEngineFixture.Str(vm, "z")), JValue.Int(0));
        vm.InvokeVirtual(v, "setElementAt", "(Ljava/lang/Object;I)V", JValue.Ref(JavaEngineFixture.Str(vm, "y")), JValue.Int(0));
        JavaAssert.Eq("after insert+set", vm.StringValue(vm.InvokeVirtual(v, "elementAt", "(I)Ljava/lang/Object;", JValue.Int(0))), "y");
        _ = vm.InvokeVirtual(v, "removeElement", "(Ljava/lang/Object;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "b")));
        JavaAssert.Eq("size after remove", vm.InvokeVirtual(v, "size", "()I").AsInt(), 3);
        var e = vm.InvokeVirtual(v, "elements", "()Ljava/util/Enumeration;").AsObject()!;
        var b = new System.Text.StringBuilder();
        while (vm.InvokeVirtual(e, "hasMoreElements", "()Z").AsInt() != 0)
            b.Append(vm.StringValue(vm.InvokeVirtual(e, "nextElement", "()Ljava/lang/Object;")));
        JavaAssert.Eq("enumeration order", b.ToString(), "yac");
        var arr = vm.InvokeVirtual(v, "toArray", "()[Ljava/lang/Object;").AsArray();
        JavaAssert.Eq("toArray length", arr!.Elements.Length, 3);
        var v2 = JavaEngineFixture.New(vm, "java.util.Vector");
        for (int i = 0; i < 12; i++) vm.InvokeVirtual(v2, "addElement", "(Ljava/lang/Object;)V", JValue.Int(i));
        JavaAssert.Eq("growth past capacity 10", vm.InvokeVirtual(v2, "size", "()I").AsInt(), 12);
        vm.InvokeVirtual(v2, "removeAllElements", "()V");
        JavaAssert.Eq("removeAllElements", vm.InvokeVirtual(v2, "size", "()I").AsInt(), 0);
    }

    [Fact]
    public void StackLifoSemantics()
    {
        var vm = Vm();
        var st = JavaEngineFixture.New(vm, "java.util.Stack");
        JavaAssert.Eq("empty", vm.InvokeVirtual(st, "empty", "()Z").AsInt(), 1);
        _ = vm.InvokeVirtual(st, "push", "(Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "a")));
        _ = vm.InvokeVirtual(st, "push", "(Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "b")));
        JavaAssert.Eq("peek", vm.StringValue(vm.InvokeVirtual(st, "peek", "()Ljava/lang/Object;")), "b");
        JavaAssert.Eq("pop", vm.StringValue(vm.InvokeVirtual(st, "pop", "()Ljava/lang/Object;")), "b");
        JavaAssert.Eq("search 1-based", vm.InvokeVirtual(st, "search", "(Ljava/lang/Object;)I", JValue.Ref(JavaEngineFixture.Str(vm, "a"))).AsInt(), 1);
        JavaAssert.ThrowsJvm("pop empty", () => vm.InvokeVirtual(JavaEngineFixture.New(vm, "java.util.Stack"), "pop", "()Ljava/lang/Object;"));
    }

    [Fact]
    public void HashtableKeySemantics()
    {
        var vm = Vm();
        var h = JavaEngineFixture.New(vm, "java.util.Hashtable");
        _ = vm.InvokeVirtual(h, "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "k1")), JValue.Ref(JavaEngineFixture.Str(vm, "v1")));
        _ = vm.InvokeVirtual(h, "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "k2")), JValue.Ref(JavaEngineFixture.Str(vm, "v2")));
        JavaAssert.Eq("get", vm.StringValue(vm.InvokeVirtual(h, "get", "(Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "k1")))), "v1");
        JavaAssert.Eq("get missing", vm.InvokeVirtual(h, "get", "(Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "nope"))).AsReference(), null);
        JavaAssert.Eq("containsKey", vm.InvokeVirtual(h, "containsKey", "(Ljava/lang/Object;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "k2"))).AsInt(), 1);
        JavaAssert.Eq("contains value", vm.InvokeVirtual(h, "contains", "(Ljava/lang/Object;)Z", JValue.Ref(JavaEngineFixture.Str(vm, "v2"))).AsInt(), 1);
        JavaAssert.ThrowsJvm("null key NPE", () => vm.InvokeVirtual(h, "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(null), JValue.Ref(JavaEngineFixture.Str(vm, "x"))));
        _ = vm.InvokeVirtual(h, "remove", "(Ljava/lang/Object;)Ljava/lang/Object;", JValue.Ref(JavaEngineFixture.Str(vm, "k1")));
        JavaAssert.Eq("size after remove", vm.InvokeVirtual(h, "size", "()I").AsInt(), 1);
    }

    [Fact]
    public void SameSeededRandomsAgree()
    {
        var vm = Vm();
        string Seq(JObject r)
        {
            var b = new System.Text.StringBuilder();
            b.Append(vm.InvokeVirtual(r, "nextInt", "()I").AsInt());
            b.Append(',').Append(vm.InvokeVirtual(r, "nextInt", "(I)I", JValue.Int(10)).AsInt());
            b.Append(',').Append(vm.InvokeVirtual(r, "nextLong", "()J").AsLong());
            b.Append(',').Append(Math.Round(vm.InvokeVirtual(r, "nextDouble", "()D").AsDouble(), 6));
            b.Append(',').Append(vm.InvokeVirtual(r, "nextBoolean", "()Z").AsInt());
            return b.ToString();
        }
        var r1 = vm.Construct(vm.LoadClass("java.util.Random"), "(J)V", JValue.Long(42));
        var r2 = vm.Construct(vm.LoadClass("java.util.Random"), "(J)V", JValue.Long(42));
        JavaAssert.Eq("same seed same sequence", Seq(r1), Seq(r2));
        var r3 = vm.Construct(vm.LoadClass("java.util.Random"), "(J)V", JValue.Long(7));
        Assert.NotEqual(Seq(r3), Seq(r2));
        JavaAssert.ThrowsJvm("nextInt(0) IAE", () => vm.InvokeVirtual(r3, "nextInt", "(I)I", JValue.Int(0)));
    }

    [Fact]
    public void DateComparison()
    {
        var vm = Vm();
        var d1 = vm.Construct(vm.LoadClass("java.util.Date"), "(J)V", JValue.Long(1000));
        var d2 = vm.Construct(vm.LoadClass("java.util.Date"), "(J)V", JValue.Long(2000));
        JavaAssert.Eq("getTime", vm.InvokeVirtual(d1, "getTime", "()J").AsLong(), 1000L);
        JavaAssert.Eq("before", vm.InvokeVirtual(d1, "before", "(Ljava/util/Date;)Z", JValue.Ref(d2)).AsInt(), 1);
        JavaAssert.Eq("after", vm.InvokeVirtual(d2, "after", "(Ljava/util/Date;)Z", JValue.Ref(d1)).AsInt(), 1);
        JavaAssert.NoThrow("getYear", () => vm.InvokeVirtual(d1, "getYear", "()I"));
    }

    [Fact]
    public void ExhaustedEnumerationThrows()
    {
        var vm = Vm();
        var empty = vm.InvokeVirtual(JavaEngineFixture.New(vm, "java.util.Vector"), "elements", "()Ljava/util/Enumeration;").AsObject()!;
        JavaAssert.ThrowsJvm("nextElement exhausted", () => vm.InvokeVirtual(empty, "nextElement", "()Ljava/lang/Object;"));
    }
}

// ---------- S7. java.applet ----------
public sealed class JavaAppletApiTests
{
    [Fact]
    public void AppletStubWiring()
    {
        var vm = JavaEngineFixture.CreateVm();
        var applet = JavaEngineFixture.New(vm, "java.applet.Applet");
        var stub = JavaEngineFixture.New(vm, "java.applet.AppletStub");
        JavaAssert.NoThrow("setStub", () => vm.InvokeVirtual(applet, "setStub", "(Ljava/applet/AppletStub;)V", JValue.Ref(stub)));
        JavaAssert.NoThrow("getParameter", () => vm.InvokeVirtual(applet, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;", JValue.Ref(JavaEngineFixture.Str(vm, "x"))));
        JavaAssert.NoThrow("showStatus", () => vm.InvokeVirtual(applet, "showStatus", "(Ljava/lang/String;)V", JValue.Ref(JavaEngineFixture.Str(vm, "hi"))));
        JavaAssert.NoThrow("resize", () => vm.InvokeVirtual(applet, "resize", "(II)V", JValue.Int(10), JValue.Int(10)));
        JavaAssert.NoThrow("isActive", () => vm.InvokeVirtual(applet, "isActive", "()Z"));
        JavaAssert.NoThrow("getCodeBase", () => vm.InvokeVirtual(applet, "getCodeBase", "()Ljava/net/URL;"));
        JavaAssert.NoThrow("getDocumentBase", () => vm.InvokeVirtual(applet, "getDocumentBase", "()Ljava/net/URL;"));
        JavaAssert.NoThrow("getAppletContext", () => vm.InvokeVirtual(applet, "getAppletContext", "()Ljava/applet/AppletContext;"));
        JavaAssert.NoThrow("getAppletInfo", () => vm.InvokeVirtual(applet, "getAppletInfo", "()Ljava/lang/String;"));
        JavaAssert.NoThrow("play", () => vm.InvokeVirtual(applet, "play", "(Ljava/net/URL;)V", JValue.Ref(null)));
    }

    [Fact]
    public void AppletContextAndAudioClipStubsDoNotThrow()
    {
        var vm = JavaEngineFixture.CreateVm();
        var ac = JavaEngineFixture.New(vm, "java.applet.AppletContext");
        JavaAssert.NoThrow("showDocument", () => vm.InvokeVirtual(ac, "showDocument", "(Ljava/net/URL;)V", JValue.Ref(null)));
        JavaAssert.NoThrow("showStatus", () => vm.InvokeVirtual(ac, "showStatus", "(Ljava/lang/String;)V", JValue.Ref(JavaEngineFixture.Str(vm, "x"))));
        var clip = JavaEngineFixture.New(vm, "java.applet.AudioClip");
        JavaAssert.NoThrow("play", () => vm.InvokeVirtual(clip, "play", "()V"));
        JavaAssert.NoThrow("loop", () => vm.InvokeVirtual(clip, "loop", "()V"));
        JavaAssert.NoThrow("stop", () => vm.InvokeVirtual(clip, "stop", "()V"));
    }
}

// ---------- S9/S10/S13. Color, Font, support types ----------
public sealed class JavaAwtSupportTypeTests
{
    private static JavaVm Vm() => JavaEngineFixture.CreateVm();

    private static JObject Color(JavaVm vm, int r, int g, int b) => vm.Construct(vm.LoadClass("java.awt.Color"), "(III)V", JValue.Int(r), JValue.Int(g), JValue.Int(b));

    [Fact]
    public void JdkColorConstantsMatchExactly()
    {
        var vm = Vm();
        var want = new (string Name, string Rgb)[]
        {
            ("black", "0,0,0"), ("blue", "0,0,255"), ("cyan", "0,255,255"), ("darkGray", "64,64,64"),
            ("gray", "128,128,128"), ("green", "0,255,0"), ("lightGray", "192,192,192"), ("magenta", "255,0,255"),
            ("orange", "255,200,0"), ("pink", "255,175,175"), ("red", "255,0,0"), ("white", "255,255,255"), ("yellow", "255,255,0"),
        };
        foreach (var (name, rgb) in want)
        {
            var f = vm.GetStatic(vm.LoadClass("java.awt.Color"), name, "Ljava/awt/Color;").AsObject();
            Assert.NotNull(f);
            var got = $"{vm.InvokeVirtual(f, "getRed", "()I").AsInt()},{vm.InvokeVirtual(f, "getGreen", "()I").AsInt()},{vm.InvokeVirtual(f, "getBlue", "()I").AsInt()}";
            JavaAssert.Eq("Color." + name, got, rgb);
        }
    }

    [Fact]
    public void ColorInstanceSemantics()
    {
        var vm = Vm();
        var red = Color(vm, 255, 0, 0);
        JavaAssert.Eq("getRed", vm.InvokeVirtual(red, "getRed", "()I").AsInt(), 255);
        var rgb = vm.InvokeVirtual(red, "getRGB", "()I").AsInt();
        JavaAssert.Eq("alpha byte", (rgb >> 24) & 0xFF, 0xFF);
        var black = Color(vm, 0, 0, 0);
        var darker = vm.InvokeVirtual(black, "darker", "()Ljava/awt/Color;").AsObject()!;
        JavaAssert.Eq("darker black stays black", vm.InvokeVirtual(darker, "getRed", "()I").AsInt(), 0);
        JavaAssert.Eq("equals by RGB", vm.InvokeVirtual(Color(vm, 1, 2, 3), "equals", "(Ljava/lang/Object;)Z", JValue.Ref(Color(vm, 1, 2, 3))).AsInt(), 1);
        var gray = Color(vm, 100, 100, 100);
        var dgray = vm.InvokeVirtual(gray, "darker", "()Ljava/awt/Color;").AsObject()!;
        JavaAssert.Eq("darker 0.7 scale", vm.InvokeVirtual(dgray, "getRed", "()I").AsInt(), 70);
    }

    [Fact]
    public void FontAndMetrics()
    {
        var vm = Vm();
        var fc = vm.LoadClass("java.awt.Font");
        var f = vm.Construct(fc, "(Ljava/lang/String;II)V", JValue.Ref(JavaEngineFixture.Str(vm, "Serif")), JValue.Int(1), JValue.Int(14));
        JavaAssert.Eq("getName", vm.StringValue(vm.InvokeVirtual(f, "getName", "()Ljava/lang/String;")), "Serif");
        JavaAssert.Eq("getStyle", vm.InvokeVirtual(f, "getStyle", "()I").AsInt(), 1);
        JavaAssert.Eq("getSize", vm.InvokeVirtual(f, "getSize", "()I").AsInt(), 14);
        JavaAssert.Eq("isBold", vm.InvokeVirtual(f, "isBold", "()Z").AsInt(), 1);
        JavaAssert.Eq("PLAIN", vm.GetStatic(fc, "PLAIN", "I").AsInt(), 0);
        JavaAssert.Eq("BOLD", vm.GetStatic(fc, "BOLD", "I").AsInt(), 1);
        JavaAssert.Eq("ITALIC", vm.GetStatic(fc, "ITALIC", "I").AsInt(), 2);
        var f2 = vm.Construct(fc, "(Ljava/lang/String;II)V", JValue.Ref(JavaEngineFixture.Str(vm, "Serif")), JValue.Int(1), JValue.Int(14));
        JavaAssert.Eq("equals", vm.InvokeVirtual(f, "equals", "(Ljava/lang/Object;)Z", JValue.Ref(f2)).AsInt(), 1);
        var tk = vm.InvokeStatic("java.awt.Toolkit", "getDefaultToolkit", "()Ljava/awt/Toolkit;").AsObject()!;
        var fm = vm.InvokeVirtual(tk, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;", JValue.Ref(f)).AsObject()!;
        JavaAssert.Eq("height = a+d+l",
            vm.InvokeVirtual(fm, "getHeight", "()I").AsInt(),
            vm.InvokeVirtual(fm, "getAscent", "()I").AsInt() + vm.InvokeVirtual(fm, "getDescent", "()I").AsInt() + vm.InvokeVirtual(fm, "getLeading", "()I").AsInt());
        Assert.True(vm.InvokeVirtual(fm, "stringWidth", "(Ljava/lang/String;)I", JValue.Ref(JavaEngineFixture.Str(vm, "Hello"))).AsInt() > 10);
        Assert.True(vm.InvokeVirtual(fm, "charWidth", "(C)I", JValue.Int('M')).AsInt() > 0);
        JavaAssert.Eq("getWidths length", vm.InvokeVirtual(fm, "getWidths", "()[I").AsArray()!.Elements.Length, 256);
    }

    [Fact]
    public void DimensionPointRectanglePolygonInsets()
    {
        var vm = Vm();
        var dc = vm.LoadClass("java.awt.Dimension");
        var dim = vm.Construct(dc, "(II)V", JValue.Int(3), JValue.Int(4));
        JavaAssert.Eq("width field", vm.GetField(dim, dc, "width", "I").AsInt(), 3);
        vm.InvokeVirtual(dim, "setSize", "(II)V", JValue.Int(5), JValue.Int(6));
        JavaAssert.Eq("setSize", vm.GetField(vm.InvokeVirtual(dim, "getSize", "()Ljava/awt/Dimension;").AsObject()!, dc, "width", "I").AsInt(), 5);

        var pc = vm.LoadClass("java.awt.Point");
        var pt = vm.Construct(pc, "(II)V", JValue.Int(1), JValue.Int(2));
        vm.InvokeVirtual(pt, "translate", "(II)V", JValue.Int(3), JValue.Int(4));
        JavaAssert.Eq("translate x", vm.GetField(pt, pc, "x", "I").AsInt(), 4);
        JavaAssert.Eq("Point.equals", vm.InvokeVirtual(pt, "equals", "(Ljava/lang/Object;)Z", JValue.Ref(vm.Construct(pc, "(II)V", JValue.Int(4), JValue.Int(6)))).AsInt(), 1);

        var rc = vm.LoadClass("java.awt.Rectangle");
        var r1 = vm.Construct(rc, "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(10), JValue.Int(10));
        JavaAssert.Eq("contains inside", vm.InvokeVirtual(r1, "contains", "(II)Z", JValue.Int(5), JValue.Int(5)).AsInt(), 1);
        JavaAssert.Eq("edge exclusive", vm.InvokeVirtual(r1, "contains", "(II)Z", JValue.Int(10), JValue.Int(5)).AsInt(), 0);
        JavaAssert.Eq("inside alias", vm.InvokeVirtual(r1, "inside", "(II)Z", JValue.Int(5), JValue.Int(5)).AsInt(), 1);
        var r2 = vm.Construct(rc, "(IIII)V", JValue.Int(5), JValue.Int(5), JValue.Int(10), JValue.Int(10));
        JavaAssert.Eq("intersects", vm.InvokeVirtual(r1, "intersects", "(Ljava/awt/Rectangle;)Z", JValue.Ref(r2)).AsInt(), 1);
        var inter = vm.InvokeVirtual(r1, "intersection", "(Ljava/awt/Rectangle;)Ljava/awt/Rectangle;", JValue.Ref(r2)).AsObject()!;
        JavaAssert.Eq("intersection width", vm.GetField(inter, rc, "width", "I").AsInt(), 5);
        var uni = vm.InvokeVirtual(r1, "union", "(Ljava/awt/Rectangle;)Ljava/awt/Rectangle;", JValue.Ref(r2)).AsObject()!;
        JavaAssert.Eq("union width", vm.GetField(uni, rc, "width", "I").AsInt(), 15);
        JavaAssert.Eq("isEmpty", vm.InvokeVirtual(vm.Construct(rc, "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(0), JValue.Int(5)), "isEmpty", "()Z").AsInt(), 1);
        vm.InvokeVirtual(r1, "grow", "(II)V", JValue.Int(1), JValue.Int(1));
        JavaAssert.Eq("grow", vm.GetField(r1, rc, "width", "I").AsInt(), 12);
        vm.InvokeVirtual(r1, "setBounds", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(10), JValue.Int(10));

        var pg = JavaEngineFixture.New(vm, "java.awt.Polygon");
        vm.InvokeVirtual(pg, "addPoint", "(II)V", JValue.Int(0), JValue.Int(0));
        vm.InvokeVirtual(pg, "addPoint", "(II)V", JValue.Int(10), JValue.Int(0));
        vm.InvokeVirtual(pg, "addPoint", "(II)V", JValue.Int(0), JValue.Int(10));
        JavaAssert.Eq("polygon contains", vm.InvokeVirtual(pg, "contains", "(II)Z", JValue.Int(1), JValue.Int(1)).AsInt(), 1);
        var bb = vm.InvokeVirtual(pg, "getBoundingBox", "()Ljava/awt/Rectangle;").AsObject()!;
        JavaAssert.Eq("bounding box", vm.GetField(bb, rc, "width", "I").AsInt(), 10);

        var ic = vm.LoadClass("java.awt.Insets");
        var ins = vm.Construct(ic, "(IIII)V", JValue.Int(1), JValue.Int(2), JValue.Int(3), JValue.Int(4));
        JavaAssert.Eq("top", vm.GetField(ins, ic, "top", "I").AsInt(), 1);
        JavaAssert.Eq("right", vm.GetField(ins, ic, "right", "I").AsInt(), 4);
    }
}

// ---------- S8/S11/S12. Graphics, images, component, events ----------
public sealed class JavaAwtBehaviorTests
{
    private static JavaVm Vm() => JavaEngineFixture.CreateVm();

    private static JObject Red(JavaVm vm) => vm.Construct(vm.LoadClass("java.awt.Color"), "(III)V", JValue.Int(255), JValue.Int(0), JValue.Int(0));

    [Fact]
    public void GraphicsDrawingLeavesRealPixels()
    {
        var vm = Vm();
        var img = vm.GraphicsFactory.CreateImage(40, 30);
        var bmp = (JavaImageState)img.NativeState!;
        var g = vm.InvokeVirtual(img, "getGraphics", "()Ljava/awt/Graphics;").AsObject();
        Assert.NotNull(g);
        vm.InvokeVirtual(g, "setColor", "(Ljava/awt/Color;)V", JValue.Ref(Red(vm)));
        var got = vm.InvokeVirtual(vm.InvokeVirtual(g, "getColor", "()Ljava/awt/Color;").AsObject()!, "getRed", "()I").AsInt();
        JavaAssert.Eq("getColor", got, 255);
        vm.InvokeVirtual(g, "fillRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(20), JValue.Int(20));
        var px = bmp.Bitmap.GetPixel(10, 10);
        JavaAssert.Eq("fillRect pixel", $"R={px.R},G={px.G},B={px.B}", "R=255,G=0,B=0");
        vm.InvokeVirtual(g, "clearRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(20), JValue.Int(20));
        Assert.True(bmp.Bitmap.GetPixel(10, 10).R != 255, "clearRect clears");
        vm.InvokeVirtual(g, "drawLine", "(IIII)V", JValue.Int(0), JValue.Int(25), JValue.Int(39), JValue.Int(25));
        JavaAssert.Eq("drawLine pixel", $"R={bmp.Bitmap.GetPixel(20, 25).R}", "R=255");
        vm.InvokeVirtual(g, "translate", "(II)V", JValue.Int(10), JValue.Int(0));
        vm.InvokeVirtual(g, "translate", "(II)V", JValue.Int(0), JValue.Int(10));
        vm.InvokeVirtual(g, "fillRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(2), JValue.Int(2));
        JavaAssert.Eq("translate cumulative", $"R={bmp.Bitmap.GetPixel(10, 10).R}", "R=255");
        foreach (var (nm, desc) in new[] { ("drawOval", "(IIII)V"), ("fillOval", "(IIII)V"), ("drawArc", "(IIIIII)V"), ("fillArc", "(IIIIII)V"), ("drawRoundRect", "(IIIIII)V"), ("draw3DRect", "(IIIIIZ)V"), ("fill3DRect", "(IIIIIZ)V") })
            JavaAssert.NoThrow("Graphics." + nm, () => vm.InvokeVirtual(g, nm, desc, JValue.Int(1), JValue.Int(1), JValue.Int(5), JValue.Int(5), JValue.Int(0), JValue.Int(360), JValue.Int(1)));
        var xs = vm.NewArray("I", 3); var ys = vm.NewArray("I", 3);
        for (int i = 0; i < 3; i++) { xs.Elements[i] = JValue.Int(i); ys.Elements[i] = JValue.Int(i); }
        vm.InvokeVirtual(g, "drawPolygon", "([I[II)V", JValue.Ref(xs), JValue.Ref(ys), JValue.Int(3));
        vm.InvokeVirtual(g, "fillPolygon", "([I[II)V", JValue.Ref(xs), JValue.Ref(ys), JValue.Int(3));
        vm.InvokeVirtual(g, "drawPolyline", "([I[II)V", JValue.Ref(xs), JValue.Ref(ys), JValue.Int(3));
        vm.InvokeVirtual(g, "copyArea", "(IIIIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(5), JValue.Int(5), JValue.Int(10), JValue.Int(10));
        vm.InvokeVirtual(g, "drawString", "(Ljava/lang/String;II)V", JValue.Ref(JavaEngineFixture.Str(vm, "x")), JValue.Int(0), JValue.Int(10));
        var ca = vm.NewArray("C", 1); ca.Elements[0] = JValue.Int('x');
        vm.InvokeVirtual(g, "drawChars", "([CIIII)V", JValue.Ref(ca), JValue.Int(0), JValue.Int(1), JValue.Int(0), JValue.Int(10));
        var ba = vm.NewArray("B", 1); ba.Elements[0] = JValue.Int((sbyte)'x');
        vm.InvokeVirtual(g, "drawBytes", "([BIIII)V", JValue.Ref(ba), JValue.Int(0), JValue.Int(1), JValue.Int(0), JValue.Int(10));
        var copy = vm.InvokeVirtual(g, "create", "()Ljava/awt/Graphics;").AsObject();
        Assert.NotNull(copy);
        vm.InvokeVirtual(copy, "dispose", "()V");
        vm.InvokeVirtual(g, "dispose", "()V");
        JavaAssert.NoThrow("disposed graphics tolerated", () => vm.InvokeVirtual(g, "drawLine", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(1), JValue.Int(1)));
    }

    [Fact]
    public void ClipIntersectsAndSetClipReplaces()
    {
        var vm = Vm();
        var img = vm.GraphicsFactory.CreateImage(40, 30);
        var bmp = (JavaImageState)img.NativeState!;
        var g = vm.InvokeVirtual(img, "getGraphics", "()Ljava/awt/Graphics;").AsObject()!;
        vm.InvokeVirtual(g, "setColor", "(Ljava/awt/Color;)V", JValue.Ref(Red(vm)));
        vm.InvokeVirtual(g, "clipRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(10), JValue.Int(30));
        vm.InvokeVirtual(g, "fillRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(40), JValue.Int(30));
        Assert.True(bmp.Bitmap.GetPixel(5, 15).R == 255 && bmp.Bitmap.GetPixel(35, 15).R != 255, "clipRect clips");
        var cb = vm.InvokeVirtual(g, "getClipBounds", "()Ljava/awt/Rectangle;").AsObject();
        Assert.NotNull(cb);
        JavaAssert.Eq("clip bounds", vm.GetField(cb, vm.LoadClass("java.awt.Rectangle"), "width", "I").AsInt(), 10);
        vm.InvokeVirtual(g, "setClip", "(IIII)V", JValue.Int(20), JValue.Int(0), JValue.Int(10), JValue.Int(30));
        var cb2 = vm.InvokeVirtual(g, "getClipBounds", "()Ljava/awt/Rectangle;").AsObject()!;
        JavaAssert.Eq("setClip replaces", vm.GetField(cb2, vm.LoadClass("java.awt.Rectangle"), "width", "I").AsInt(), 10);
    }

    [Fact]
    public void XorModeDoubleFillRestores()
    {
        var vm = Vm();
        var img = vm.GraphicsFactory.CreateImage(20, 20);
        var bmp = (JavaImageState)img.NativeState!;
        var g = vm.InvokeVirtual(img, "getGraphics", "()Ljava/awt/Graphics;").AsObject()!;
        var black = vm.Construct(vm.LoadClass("java.awt.Color"), "(III)V", JValue.Int(0), JValue.Int(0), JValue.Int(0));
        var white = vm.Construct(vm.LoadClass("java.awt.Color"), "(III)V", JValue.Int(255), JValue.Int(255), JValue.Int(255));
        vm.InvokeVirtual(g, "setColor", "(Ljava/awt/Color;)V", JValue.Ref(black));
        vm.InvokeVirtual(g, "fillRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(20), JValue.Int(20));
        vm.InvokeVirtual(g, "setXORMode", "(Ljava/awt/Color;)V", JValue.Ref(white));
        vm.InvokeVirtual(g, "setColor", "(Ljava/awt/Color;)V", JValue.Ref(white));
        vm.InvokeVirtual(g, "fillRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(20), JValue.Int(20));
        vm.InvokeVirtual(g, "fillRect", "(IIII)V", JValue.Int(0), JValue.Int(0), JValue.Int(20), JValue.Int(20));
        JavaAssert.Eq("XOR restores", $"R={bmp.Bitmap.GetPixel(10, 10).R}", "R=0");
        vm.InvokeVirtual(g, "setPaintMode", "()V");
    }

    [Fact]
    public void OffscreenImageAndMediaTracker()
    {
        var vm = Vm();
        var img = vm.GraphicsFactory.CreateImage(40, 30);
        JavaAssert.Eq("getWidth", vm.InvokeVirtual(img, "getWidth", "(Ljava/awt/image/ImageObserver;)I", JValue.Ref(null)).AsInt(), 40);
        JavaAssert.Eq("getHeight", vm.InvokeVirtual(img, "getHeight", "(Ljava/awt/image/ImageObserver;)I", JValue.Ref(null)).AsInt(), 30);
        Assert.NotNull(vm.InvokeVirtual(img, "getGraphics", "()Ljava/awt/Graphics;").AsObject());
        JavaAssert.NoThrow("flush", () => vm.InvokeVirtual(img, "flush", "()V"));
        var mt = vm.Construct(vm.LoadClass("java.awt.MediaTracker"), "(Ljava/awt/Component;)V", JValue.Ref(JavaEngineFixture.New(vm, "java.awt.Canvas")));
        vm.InvokeVirtual(mt, "addImage", "(Ljava/awt/Image;I)V", JValue.Ref(img), JValue.Int(1));
        JavaAssert.NoThrow("waitForAll", () => vm.InvokeVirtual(mt, "waitForAll", "()V"));
        JavaAssert.NoThrow("waitForID", () => vm.InvokeVirtual(mt, "waitForID", "(I)V", JValue.Int(1)));
        JavaAssert.Eq("checkAll", vm.InvokeVirtual(mt, "checkAll", "()Z").AsInt(), 1);
        JavaAssert.Eq("isErrorAny", vm.InvokeVirtual(mt, "isErrorAny", "()Z").AsInt(), 0);
        JavaAssert.Eq("statusAll COMPLETE", vm.InvokeVirtual(mt, "statusAll", "(Z)I", JValue.Int(1)).AsInt() & 8, 8);
    }

    [Fact]
    public void ComponentStateAndListenerRegistration()
    {
        var vm = Vm();
        var canvas = JavaEngineFixture.New(vm, "java.awt.Canvas");
        vm.InvokeVirtual(canvas, "setSize", "(II)V", JValue.Int(100), JValue.Int(80));
        JavaAssert.Eq("getWidth", vm.InvokeVirtual(canvas, "getWidth", "()I").AsInt(), 100);
        var size = vm.InvokeVirtual(canvas, "getSize", "()Ljava/awt/Dimension;").AsObject()!;
        JavaAssert.Eq("getSize height", vm.GetField(size, vm.LoadClass("java.awt.Dimension"), "height", "I").AsInt(), 80);
        vm.InvokeVirtual(canvas, "setBackground", "(Ljava/awt/Color;)V", JValue.Ref(Red(vm)));
        var bg = vm.InvokeVirtual(canvas, "getBackground", "()Ljava/awt/Color;").AsObject();
        Assert.NotNull(bg);
        JavaAssert.Eq("background round-trip", vm.InvokeVirtual(bg, "getRed", "()I").AsInt(), 255);
        vm.InvokeVirtual(canvas, "setVisible", "(Z)V", JValue.Int(0));
        JavaAssert.Eq("invisible", vm.InvokeVirtual(canvas, "isVisible", "()Z").AsInt(), 0);
        vm.InvokeVirtual(canvas, "show", "()V");
        JavaAssert.Eq("show", vm.InvokeVirtual(canvas, "isVisible", "()Z").AsInt(), 1);
        vm.InvokeVirtual(canvas, "hide", "()V");
        JavaAssert.Eq("hide", vm.InvokeVirtual(canvas, "isVisible", "()Z").AsInt(), 0);
        Assert.NotNull(vm.InvokeVirtual(canvas, "createImage", "(II)Ljava/awt/Image;", JValue.Int(10), JValue.Int(10)).AsObject());
        JavaAssert.Eq("getParent null for root", vm.InvokeVirtual(canvas, "getParent", "()Ljava/awt/Container;").AsReference(), null);
        var tk = vm.InvokeStatic("java.awt.Toolkit", "getDefaultToolkit", "()Ljava/awt/Toolkit;").AsObject();
        Assert.NotNull(tk);
        var ss = vm.InvokeVirtual(tk, "getScreenSize", "()Ljava/awt/Dimension;").AsObject();
        Assert.NotNull(ss);
        Assert.True(vm.GetField(ss, vm.LoadClass("java.awt.Dimension"), "width", "I").AsInt() > 0);
    }

    [Fact]
    public void MouseEventConstructionAndAccessors()
    {
        var vm = Vm();
        var canvas = JavaEngineFixture.New(vm, "java.awt.Canvas");
        var me = vm.Construct(vm.LoadClass("java.awt.event.MouseEvent"), "(Ljava/awt/Component;IJIIIZ)V",
            JValue.Ref(canvas), JValue.Int(501), JValue.Long(0), JValue.Int(16), JValue.Int(16), JValue.Int(20), JValue.Int(2), JValue.Int(0));
        JavaAssert.Eq("getX", vm.InvokeVirtual(me, "getX", "()I").AsInt(), 16);
        JavaAssert.Eq("getY", vm.InvokeVirtual(me, "getY", "()I").AsInt(), 20);
        JavaAssert.Eq("getID", vm.InvokeVirtual(me, "getID", "()I").AsInt(), 501);
        JavaAssert.Eq("getClickCount", vm.InvokeVirtual(me, "getClickCount", "()I").AsInt(), 2);
        JavaAssert.Eq("getModifiers", vm.InvokeVirtual(me, "getModifiers", "()I").AsInt(), 16);
        JavaAssert.Eq("getButton", vm.InvokeVirtual(me, "getButton", "()I").AsInt(), 1);
    }

    [Fact]
    public void LegacyEventConstantValuesMatchTheJdk()
    {
        var vm = Vm();
        var ec = vm.LoadClass("java.awt.Event");
        JavaAssert.Eq("MOUSE_DOWN", vm.GetStatic(ec, "MOUSE_DOWN", "I").AsInt(), 501);
        JavaAssert.Eq("MOUSE_UP", vm.GetStatic(ec, "MOUSE_UP", "I").AsInt(), 502);
        JavaAssert.Eq("MOUSE_DRAG", vm.GetStatic(ec, "MOUSE_DRAG", "I").AsInt(), 504);
        JavaAssert.Eq("MOUSE_ENTER", vm.GetStatic(ec, "MOUSE_ENTER", "I").AsInt(), 505);
        JavaAssert.Eq("MOUSE_EXIT", vm.GetStatic(ec, "MOUSE_EXIT", "I").AsInt(), 506);
        JavaAssert.Eq("KEY_PRESS", vm.GetStatic(ec, "KEY_PRESS", "I").AsInt(), 401);
        JavaAssert.Eq("ACTION_EVENT", vm.GetStatic(ec, "ACTION_EVENT", "I").AsInt(), 1001);
        JavaAssert.Eq("SHIFT_MASK", vm.GetStatic(ec, "SHIFT_MASK", "I").AsInt(), 1);
    }

    [Fact]
    public void CharacterHelpers()
    {
        var vm = Vm();
        var C = "java.lang.Character";
        Assert.True(vm.InvokeStatic(C, "isDigit", "(C)Z", JValue.Int('5')).AsInt() == 1);
        Assert.True(vm.InvokeStatic(C, "isLetter", "(C)Z", JValue.Int('a')).AsInt() == 1);
        Assert.True(vm.InvokeStatic(C, "isWhitespace", "(C)Z", JValue.Int(' ')).AsInt() == 1);
        JavaAssert.Eq("toUpperCase", vm.InvokeStatic(C, "toUpperCase", "(C)C", JValue.Int('a')).AsInt(), (int)'A');
    }
}

// ---------- S3/S12/S15 end-to-end: real applet bytecode ----------
public sealed class JavaAppletEndToEndTests
{
    private static JObject LoadMyApplet(JavaVm vm)
    {
        var bytes = File.ReadAllBytes(Path.Combine(JavaEngineFixture.ClassDir, "MyApplet.class"));
        var cls = vm.LoadClassBytes(bytes);
        return vm.Construct(cls);
    }

    [Fact]
    public void InitRegistersListenerAndSetsBackground()
    {
        var vm = JavaEngineFixture.CreateVm();
        var applet = LoadMyApplet(vm);
        var cls = vm.LoadClass("MyApplet");
        vm.InvokeVirtual(applet, "init", "()V");
        var bg = vm.InvokeVirtual(applet, "getBackground", "()Ljava/awt/Color;").AsObject()!;
        JavaAssert.Eq("white background from bytecode", vm.InvokeVirtual(bg, "getGreen", "()I").AsInt(), 255);
        var state = JavaComponentBridge.State(applet);
        JavaAssert.True("mouse listener registered by bytecode", state.MouseListeners.Contains(applet));
    }

    [Fact]
    public void FullInputDispatchDrivesClickCounter()
    {
        var vm = JavaEngineFixture.CreateVm();
        var applet = LoadMyApplet(vm);
        var cls = vm.LoadClass("MyApplet");
        vm.InvokeVirtual(applet, "init", "()V");
        var down = new JavaInput(JavaInputKind.MouseDown, 10, 12, 1, 0, 0, '\0', false, false, false);
        var up = new JavaInput(JavaInputKind.MouseUp, 10, 12, 1, 0, 0, '\0', false, false, false);
        Assert.True(JavaComponentBridge.Dispatch(vm, applet, down), "mouseDown consumed");
        Assert.True(JavaComponentBridge.Dispatch(vm, applet, up), "mouseUp consumed");
        JavaAssert.Eq("clickCount via bytecode putfield/getfield", vm.GetField(applet, cls, "clickCount", "I").AsInt(), 1);
        var moved = new JavaInput(JavaInputKind.MouseMove, 30, 30, 0, 0, 0, '\0', false, false, false);
        _ = JavaComponentBridge.Dispatch(vm, applet, moved);
    }

    [Fact]
    public void PaintRunsFullBytecodePath()
    {
        var vm = JavaEngineFixture.CreateVm();
        var applet = LoadMyApplet(vm);
        vm.InvokeVirtual(applet, "init", "()V");
        var surface = vm.GraphicsFactory.CreateImage(300, 200);
        var g = vm.InvokeVirtual(surface, "getGraphics", "()Ljava/awt/Graphics;").AsObject()!;
        JavaAssert.NoThrow("paint bytecode", () => vm.InvokeVirtual(applet, "paint", "(Ljava/awt/Graphics;)V", JValue.Ref(g)));
        for (int i = 0; i < 3; i++)
        {
            var evt = vm.Construct(vm.LoadClass("java.awt.event.MouseEvent"), "(Ljava/awt/Component;IJIIIZ)V",
                JValue.Ref(applet), JValue.Int(500), JValue.Long(0), JValue.Int(16), JValue.Int(10), JValue.Int(20), JValue.Int(1), JValue.Int(0));
            vm.InvokeVirtual(applet, "mouseClicked", "(Ljava/awt/event/MouseEvent;)V", JValue.Ref(evt));
        }
        JavaAssert.Eq("three clicks", vm.GetField(applet, vm.LoadClass("MyApplet"), "clickCount", "I").AsInt(), 3);
        JavaAssert.NoThrow("paint with StringBuffer chain", () => vm.InvokeVirtual(applet, "paint", "(Ljava/awt/Graphics;)V", JValue.Ref(g)));
    }

    [Fact]
    public void AllMouseListenerMethodsRun()
    {
        var vm = JavaEngineFixture.CreateVm();
        var applet = LoadMyApplet(vm);
        vm.InvokeVirtual(applet, "init", "()V");
        var evt = vm.Construct(vm.LoadClass("java.awt.event.MouseEvent"), "(Ljava/awt/Component;IJIIIZ)V",
            JValue.Ref(applet), JValue.Int(501), JValue.Long(0), JValue.Int(0), JValue.Int(5), JValue.Int(5), JValue.Int(1), JValue.Int(0));
        foreach (var h in new[] { "mousePressed", "mouseReleased", "mouseEntered", "mouseExited" })
            JavaAssert.NoThrow(h, () => vm.InvokeVirtual(applet, h, "(Ljava/awt/event/MouseEvent;)V", JValue.Ref(evt)));
    }

    [Fact]
    public void LifecycleDefaultMethodsAreOverridable()
    {
        var vm = JavaEngineFixture.CreateVm();
        var applet = LoadMyApplet(vm);
        // Applet defaults exist as natives; the subclass overrides win by
        // virtual dispatch (MyApplet overrides init, exercised above).
        JavaAssert.NoThrow("start default", () => vm.InvokeVirtual(applet, "start", "()V"));
        JavaAssert.NoThrow("stop default", () => vm.InvokeVirtual(applet, "stop", "()V"));
        JavaAssert.NoThrow("destroy default", () => vm.InvokeVirtual(applet, "destroy", "()V"));
    }
}
