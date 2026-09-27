using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Retro96.Drawing;
using Retro96.Engine.Network;

namespace Retro96.Engine.Java;

internal static class BuiltinJavaLibrary
{
    [ThreadStatic] private static JObject? CurrentThreadObject;

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        // java.lang core
        "java.lang.Object","java.lang.Class","java.lang.String","java.lang.StringBuffer",
        "java.lang.System","java.lang.PrintStream","java.io.PrintStream","java.lang.Thread","java.lang.Runnable",
        "java.lang.Throwable","java.lang.Exception","java.lang.RuntimeException","java.lang.Error",
        "java.lang.NullPointerException","java.lang.ArrayIndexOutOfBoundsException","java.lang.ClassCastException",
        "java.lang.ArithmeticException","java.lang.NegativeArraySizeException","java.lang.ArrayStoreException",
        "java.lang.InterruptedException","java.lang.OutOfMemoryError","java.lang.IllegalArgumentException",
        "java.lang.NumberFormatException","java.lang.CloneNotSupportedException",
        "java.lang.Character","java.lang.SecurityManager","java.lang.Integer","java.lang.Long","java.lang.Float",
        "java.lang.Double","java.lang.Boolean","java.lang.Byte","java.lang.Short","java.lang.Math",
        // net / applet
        "java.net.URL","java.applet.Applet","java.applet.AppletStub","java.applet.AppletContext","java.applet.AudioClip",
        // awt
        "java.awt.Graphics","java.awt.Image","java.awt.Cursor","java.awt.Color","java.awt.Font","java.awt.FontMetrics",
        "java.awt.Component","java.awt.Container","java.awt.Panel","java.awt.Canvas","java.awt.Button","java.awt.Label",
        "java.awt.TextField","java.awt.TextArea","java.awt.Checkbox","java.awt.Choice","java.awt.List","java.awt.Scrollbar",
        "java.awt.Dimension","java.awt.Insets","java.awt.Point","java.awt.Rectangle","java.awt.Event","java.awt.AWTEvent",
        "java.awt.Toolkit","java.awt.MediaTracker","java.awt.image.ImageObserver",
        "java.awt.LayoutManager","java.awt.FlowLayout","java.awt.BorderLayout","java.awt.GridLayout",
        // awt.event
        "java.awt.event.MouseListener","java.awt.event.MouseMotionListener","java.awt.event.KeyListener",
        "java.awt.event.ActionListener","java.awt.event.ItemListener","java.awt.event.MouseEvent",
        "java.awt.event.MouseMotionEvent","java.awt.event.KeyEvent","java.awt.event.ActionEvent",
        "java.awt.event.ItemEvent","java.awt.event.InputEvent",
        // util
        "java.util.Random",
    };

    private static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal)
    { "boolean","byte","char","short","int","long","float","double","void" };

    public static bool IsBuiltin(string name) => Names.Contains(name.Replace('/', '.'));

    public static void Register(JavaVm vm)
    {
        foreach (var name in Names) _ = Create(vm, name);
    }

    public static JClass Create(JavaVm vm, string name)
    {
        name = name.Replace('/', '.');
        if (vm.Classes.TryGetValue(name, out var existing)) return existing;
        string? super = name switch
        {
            "java.lang.Object" => null,
            "java.lang.Throwable" => "java.lang.Object",
            "java.lang.Exception" => "java.lang.Throwable",
            "java.lang.CloneNotSupportedException" => "java.lang.Exception",
            "java.lang.RuntimeException" => "java.lang.Exception",
            "java.lang.Error" => "java.lang.Throwable",
            "java.lang.IllegalArgumentException" => "java.lang.RuntimeException",
            "java.lang.NumberFormatException" => "java.lang.IllegalArgumentException",
            "java.lang.NullPointerException" or "java.lang.ArrayIndexOutOfBoundsException" or "java.lang.ClassCastException"
                or "java.lang.ArithmeticException" or "java.lang.NegativeArraySizeException" or "java.lang.ArrayStoreException" => "java.lang.RuntimeException",
            "java.lang.InterruptedException" => "java.lang.Exception",
            "java.lang.OutOfMemoryError" => "java.lang.Error",
            "java.lang.String" or "java.lang.StringBuffer" => "java.lang.Object",
            "java.lang.Character" or "java.lang.SecurityManager" or "java.lang.Integer" or "java.lang.Long"
                or "java.lang.Float" or "java.lang.Double" or "java.lang.Boolean" or "java.lang.Byte" or "java.lang.Short"
                or "java.lang.Math" or "java.lang.System" or "java.lang.PrintStream" or "java.io.PrintStream"
                or "java.lang.Thread" => "java.lang.Object",
            "java.net.URL" => "java.lang.Object",
            "java.applet.Applet" => "java.awt.Panel",
            "java.applet.AppletStub" or "java.applet.AppletContext" or "java.applet.AudioClip" or "java.lang.Runnable" => null,
            "java.awt.Container" => "java.awt.Component",
            "java.awt.Panel" or "java.awt.Canvas" or "java.awt.Button" or "java.awt.Label" or "java.awt.TextField"
                or "java.awt.TextArea" or "java.awt.Checkbox" or "java.awt.Choice" or "java.awt.List" or "java.awt.Scrollbar" => "java.awt.Container",
            "java.awt.Graphics" or "java.awt.Image" or "java.awt.Cursor" or "java.awt.Color" or "java.awt.Font"
                or "java.awt.FontMetrics" or "java.awt.Dimension" or "java.awt.Insets" or "java.awt.Point" or "java.awt.Rectangle"
                or "java.awt.Event" or "java.awt.AWTEvent" or "java.awt.Toolkit" or "java.awt.MediaTracker"
                or "java.awt.FlowLayout" or "java.awt.BorderLayout" or "java.awt.GridLayout" or "java.awt.LayoutManager"
                or "java.awt.image.ImageObserver" => "java.lang.Object",
            "java.awt.Component" => "java.lang.Object",
            _ when name.StartsWith("java.awt.event.", StringComparison.Ordinal) => "java.lang.Object",
            _ when name.StartsWith("java.util.", StringComparison.Ordinal) => "java.lang.Object",
            _ => "java.lang.Object"
        };
        var cls = new JClass { Name = name, IsBuiltin = true };
        vm.RegisterClass(cls);
        if (super != null) cls.SuperClass = vm.LoadClass(super);
        Populate(vm, cls);
        vm.BuildVTable(cls);
        return cls;
    }

    private static void Method(JClass c, string name, string desc, ushort flags = 0x0100) =>
        c.Methods[(name, desc)] = new JMethod { Name = name, Descriptor = desc, AccessFlags = (ushort)(flags | 0x0100), DeclaringClass = c };
    private static void StaticMethod(JClass c, string name, string desc) => Method(c, name, desc, 0x0008);
    private static void Field(JClass c, string name, string desc, ushort flags = 0) =>
        c.Fields[(name, desc)] = new JField { Name = name, Descriptor = desc, AccessFlags = flags };

    private static void Populate(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.lang.Object":
                Method(c, "<init>", "()V", 0);
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;"); Method(c, "getClass", "()Ljava/lang/Class;");
                Method(c, "wait", "()V"); Method(c, "wait", "(J)V"); Method(c, "wait", "(JI)V");
                Method(c, "notify", "()V"); Method(c, "notifyAll", "()V");
                break;
            case "java.lang.Throwable":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "getMessage", "()Ljava/lang/String;"); Method(c, "getLocalizedMessage", "()Ljava/lang/String;");
                Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "printStackTrace", "()V"); Method(c, "printStackTrace", "(Ljava/io/PrintStream;)V");
                Method(c, "fillInStackTrace", "()Ljava/lang/Throwable;");
                break;
            case "java.lang.Exception":
            case "java.lang.CloneNotSupportedException":
            case "java.lang.RuntimeException":
            case "java.lang.Error":
            case "java.lang.IllegalArgumentException":
            case "java.lang.NumberFormatException":
            case "java.lang.NullPointerException":
            case "java.lang.ArrayIndexOutOfBoundsException":
            case "java.lang.ClassCastException":
            case "java.lang.ArithmeticException":
            case "java.lang.NegativeArraySizeException":
            case "java.lang.ArrayStoreException":
            case "java.lang.InterruptedException":
            case "java.lang.OutOfMemoryError":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "getMessage", "()Ljava/lang/String;"); Method(c, "toString", "()Ljava/lang/String;");
                break;
            case "java.lang.Class":
                Method(c, "getName", "()Ljava/lang/String;"); Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "getSuperclass", "()Ljava/lang/Class;"); Method(c, "isInstance", "(Ljava/lang/Object;)Z");
                break;
            case "java.lang.String":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "<init>", "([C)V", 0); Method(c, "<init>", "([CII)V", 0);
                Method(c, "length", "()I"); Method(c, "charAt", "(I)C");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "equalsIgnoreCase", "(Ljava/lang/String;)Z");
                Method(c, "hashCode", "()I"); Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "substring", "(I)Ljava/lang/String;"); Method(c, "substring", "(II)Ljava/lang/String;");
                Method(c, "indexOf", "(I)I"); Method(c, "indexOf", "(II)I");
                Method(c, "indexOf", "(Ljava/lang/String;)I"); Method(c, "indexOf", "(Ljava/lang/String;I)I");
                Method(c, "lastIndexOf", "(I)I"); Method(c, "lastIndexOf", "(Ljava/lang/String;)I");
                Method(c, "startsWith", "(Ljava/lang/String;)Z"); Method(c, "endsWith", "(Ljava/lang/String;)Z");
                Method(c, "trim", "()Ljava/lang/String;"); Method(c, "concat", "(Ljava/lang/String;)Ljava/lang/String;");
                Method(c, "compareTo", "(Ljava/lang/String;)I"); Method(c, "getBytes", "()[B");
                Method(c, "toCharArray", "()[C"); Method(c, "replace", "(CC)Ljava/lang/String;");
                Method(c, "toLowerCase", "()Ljava/lang/String;"); Method(c, "toUpperCase", "()Ljava/lang/String;");
                Method(c, "intern", "()Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(Ljava/lang/Object;)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(I)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(J)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(C)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(F)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(D)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(Z)Ljava/lang/String;");
                break;
            case "java.lang.StringBuffer":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "append", "(Ljava/lang/String;)Ljava/lang/StringBuffer;");
                Method(c, "append", "(Ljava/lang/Object;)Ljava/lang/StringBuffer;");
                Method(c, "append", "(I)Ljava/lang/StringBuffer;"); Method(c, "append", "(J)Ljava/lang/StringBuffer;");
                Method(c, "append", "(F)Ljava/lang/StringBuffer;"); Method(c, "append", "(D)Ljava/lang/StringBuffer;");
                Method(c, "append", "(Z)Ljava/lang/StringBuffer;"); Method(c, "append", "(C)Ljava/lang/StringBuffer;");
                Method(c, "toString", "()Ljava/lang/String;"); Method(c, "length", "()I");
                Method(c, "charAt", "(I)C"); Method(c, "setCharAt", "(IC)V");
                Method(c, "setLength", "(I)V"); Method(c, "reverse", "()Ljava/lang/StringBuffer;");
                Method(c, "deleteCharAt", "(I)Ljava/lang/StringBuffer;");
                Method(c, "insert", "(ILjava/lang/String;)Ljava/lang/StringBuffer;");
                break;
            case "java.lang.Runnable": Method(c, "run", "()V"); break;
            case "java.lang.Character":
                Method(c, "<init>", "(C)V", 0); Method(c, "charValue", "()C");
                StaticMethod(c, "isDigit", "(C)Z"); StaticMethod(c, "isLetter", "(C)Z");
                StaticMethod(c, "isLetterOrDigit", "(C)Z"); StaticMethod(c, "isSpace", "(C)Z");
                StaticMethod(c, "isWhitespace", "(C)Z"); StaticMethod(c, "isUpperCase", "(C)Z");
                StaticMethod(c, "isLowerCase", "(C)Z"); StaticMethod(c, "toLowerCase", "(C)C");
                StaticMethod(c, "toUpperCase", "(C)C"); StaticMethod(c, "digit", "(CI)I");
                StaticMethod(c, "forDigit", "(II)C"); StaticMethod(c, "getNumericValue", "(C)I");
                break;
            case "java.lang.SecurityManager": Method(c, "checkPermission", "(Ljava/lang/Object;)V"); break;
            case "java.lang.System":
                StaticMethod(c, "currentTimeMillis", "()J");
                StaticMethod(c, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V");
                StaticMethod(c, "exit", "(I)V"); StaticMethod(c, "getProperty", "(Ljava/lang/String;)Ljava/lang/String;");
                StaticMethod(c, "identityHashCode", "(Ljava/lang/Object;)I"); StaticMethod(c, "gc", "()V");
                Field(c, "out", "Ljava/io/PrintStream;", 0x0008); Field(c, "err", "Ljava/io/PrintStream;", 0x0008);
                break;
            case "java.lang.PrintStream":
            case "java.io.PrintStream":
                foreach (var d in new[] { "(Ljava/lang/String;)V", "(Ljava/lang/Object;)V", "(I)V", "(J)V", "(F)V", "(D)V", "(C)V", "(Z)V" })
                    Method(c, "print", d);
                foreach (var d in new[] { "()V", "(Ljava/lang/String;)V", "(Ljava/lang/Object;)V", "(I)V", "(J)V", "(F)V", "(D)V", "(C)V", "(Z)V" })
                    Method(c, "println", d);
                break;
            case "java.lang.Thread":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/Runnable;)V", 0);
                Method(c, "<init>", "(Ljava/lang/String;)V", 0); Method(c, "<init>", "(Ljava/lang/Runnable;Ljava/lang/String;)V", 0);
                Method(c, "start", "()V"); Method(c, "run", "()V");
                StaticMethod(c, "sleep", "(J)V"); StaticMethod(c, "sleep", "(JI)V");
                Method(c, "stop", "()V"); Method(c, "interrupt", "()V"); Method(c, "isInterrupted", "()Z");
                StaticMethod(c, "interrupted", "()Z");
                Method(c, "isAlive", "()Z"); Method(c, "join", "()V"); Method(c, "join", "(J)V");
                Method(c, "setName", "(Ljava/lang/String;)V"); Method(c, "getName", "()Ljava/lang/String;");
                Method(c, "setDaemon", "(Z)V"); Method(c, "isDaemon", "()Z");
                Method(c, "setPriority", "(I)V"); Method(c, "getPriority", "()I");
                StaticMethod(c, "currentThread", "()Ljava/lang/Thread;");
                StaticMethod(c, "yield", "()V");
                Field(c, "MIN_PRIORITY", "I", 0x0008); Field(c, "NORM_PRIORITY", "I", 0x0008); Field(c, "MAX_PRIORITY", "I", 0x0008);
                break;
            case "java.lang.Integer":
                Method(c, "<init>", "(I)V", 0); Method(c, "intValue", "()I"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(I)Ljava/lang/String;");
                StaticMethod(c, "parseInt", "(Ljava/lang/String;)I");
                StaticMethod(c, "valueOf", "(I)Ljava/lang/Integer;");
                StaticMethod(c, "toHexString", "(I)Ljava/lang/String;");
                StaticMethod(c, "toBinaryString", "(I)Ljava/lang/String;");
                StaticMethod(c, "toOctalString", "(I)Ljava/lang/String;");
                Field(c, "MAX_VALUE", "I", 0x0008); Field(c, "MIN_VALUE", "I", 0x0008);
                break;
            case "java.lang.Long":
                Method(c, "<init>", "(J)V", 0); Method(c, "longValue", "()J"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(J)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(J)Ljava/lang/Long;");
                StaticMethod(c, "parseLong", "(Ljava/lang/String;)J");
                Field(c, "MAX_VALUE", "J", 0x0008); Field(c, "MIN_VALUE", "J", 0x0008);
                break;
            case "java.lang.Float":
                Method(c, "<init>", "(F)V", 0); Method(c, "floatValue", "()F"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(F)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(F)Ljava/lang/Float;");
                StaticMethod(c, "isNaN", "(F)Z"); StaticMethod(c, "isInfinite", "(F)Z");
                Field(c, "MAX_VALUE", "F", 0x0008); Field(c, "MIN_VALUE", "F", 0x0008);
                Field(c, "POSITIVE_INFINITY", "F", 0x0008); Field(c, "NEGATIVE_INFINITY", "F", 0x0008);
                Field(c, "NaN", "F", 0x0008);
                break;
            case "java.lang.Double":
                Method(c, "<init>", "(D)V", 0); Method(c, "doubleValue", "()D"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(D)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(D)Ljava/lang/Double;");
                StaticMethod(c, "isNaN", "(D)Z"); StaticMethod(c, "isInfinite", "(D)Z");
                Field(c, "MAX_VALUE", "D", 0x0008); Field(c, "MIN_VALUE", "D", 0x0008);
                Field(c, "POSITIVE_INFINITY", "D", 0x0008); Field(c, "NEGATIVE_INFINITY", "D", 0x0008);
                Field(c, "NaN", "D", 0x0008);
                break;
            case "java.lang.Boolean":
                Method(c, "<init>", "(Z)V", 0); Method(c, "booleanValue", "()Z"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(Z)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(Z)Ljava/lang/Boolean;");
                Field(c, "TRUE", "Ljava/lang/Boolean;", 0x0008); Field(c, "FALSE", "Ljava/lang/Boolean;", 0x0008);
                break;
            case "java.lang.Byte":
                Method(c, "<init>", "(B)V", 0); Method(c, "byteValue", "()B"); Method(c, "intValue", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(B)Ljava/lang/String;"); StaticMethod(c, "parseByte", "(Ljava/lang/String;)B");
                Field(c, "MAX_VALUE", "B", 0x0008); Field(c, "MIN_VALUE", "B", 0x0008);
                break;
            case "java.lang.Short":
                Method(c, "<init>", "(S)V", 0); Method(c, "shortValue", "()S"); Method(c, "intValue", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(S)Ljava/lang/String;"); StaticMethod(c, "parseShort", "(Ljava/lang/String;)S");
                Field(c, "MAX_VALUE", "S", 0x0008); Field(c, "MIN_VALUE", "S", 0x0008);
                break;
            case "java.lang.Math":
                StaticMethod(c, "abs", "(I)I"); StaticMethod(c, "abs", "(J)J");
                StaticMethod(c, "abs", "(F)F"); StaticMethod(c, "abs", "(D)D");
                StaticMethod(c, "max", "(II)I"); StaticMethod(c, "max", "(JJ)J");
                StaticMethod(c, "max", "(FF)F"); StaticMethod(c, "max", "(DD)D");
                StaticMethod(c, "min", "(II)I"); StaticMethod(c, "min", "(JJ)J");
                StaticMethod(c, "min", "(FF)F"); StaticMethod(c, "min", "(DD)D");
                StaticMethod(c, "sin", "(D)D"); StaticMethod(c, "cos", "(D)D"); StaticMethod(c, "tan", "(D)D");
                StaticMethod(c, "asin", "(D)D"); StaticMethod(c, "acos", "(D)D"); StaticMethod(c, "atan", "(D)D");
                StaticMethod(c, "atan2", "(DD)D"); StaticMethod(c, "pow", "(DD)D");
                StaticMethod(c, "sqrt", "(D)D"); StaticMethod(c, "log", "(D)D"); StaticMethod(c, "exp", "(D)D");
                StaticMethod(c, "floor", "(D)D"); StaticMethod(c, "ceil", "(D)D");
                StaticMethod(c, "round", "(F)I"); StaticMethod(c, "round", "(D)J");
                StaticMethod(c, "random", "()D");
                StaticMethod(c, "toRadians", "(D)D"); StaticMethod(c, "toDegrees", "(D)D");
                break;
            case "java.util.Random":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(J)V", 0);
                Method(c, "nextInt", "()I"); Method(c, "nextInt", "(I)I");
                Method(c, "nextLong", "()J"); Method(c, "nextDouble", "()D");
                Method(c, "nextFloat", "()F"); Method(c, "nextGaussian", "()D");
                Method(c, "setSeed", "(J)V");
                break;
            case "java.net.URL":
                Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "<init>", "(Ljava/net/URL;Ljava/lang/String;)V", 0);
                Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "getProtocol", "()Ljava/lang/String;"); Method(c, "getHost", "()Ljava/lang/String;");
                Method(c, "getFile", "()Ljava/lang/String;");
                break;
            case "java.applet.Applet":
                Method(c, "<init>", "()V", 0);
                Method(c, "init", "()V"); Method(c, "start", "()V"); Method(c, "stop", "()V"); Method(c, "destroy", "()V");
                Method(c, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;");
                Method(c, "getCodeBase", "()Ljava/net/URL;"); Method(c, "getDocumentBase", "()Ljava/net/URL;");
                Method(c, "getAppletContext", "()Ljava/applet/AppletContext;");
                Method(c, "isActive", "()Z"); Method(c, "resize", "(II)V");
                Method(c, "showStatus", "(Ljava/lang/String;)V");
                Method(c, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;");
                Method(c, "getImage", "(Ljava/net/URL;Ljava/lang/String;)Ljava/awt/Image;");
                Method(c, "getImage", "(Ljava/net/URL;Ljava/lang/String;Ljava/lang/Object;)Ljava/awt/Image;");
                Method(c, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;");
                Method(c, "getAudioClip", "(Ljava/net/URL;Ljava/lang/String;)Ljava/applet/AudioClip;");
                Method(c, "play", "(Ljava/net/URL;)V"); Method(c, "play", "(Ljava/net/URL;Ljava/lang/String;)V");
                break;
            case "java.applet.AppletStub":
                Method(c, "isActive", "()Z"); Method(c, "getDocumentBase", "()Ljava/net/URL;");
                Method(c, "getCodeBase", "()Ljava/net/URL;");
                Method(c, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;");
                Method(c, "appletResize", "(II)V");
                break;
            case "java.applet.AppletContext":
                Method(c, "showStatus", "(Ljava/lang/String;)V");
                Method(c, "showDocument", "(Ljava/net/URL;)V"); Method(c, "showDocument", "(Ljava/net/URL;Ljava/lang/String;)V");
                Method(c, "getApplet", "(Ljava/lang/String;)Ljava/applet/Applet;");
                Method(c, "getApplets", "()Ljava/util/Enumeration;");
                Method(c, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;");
                Method(c, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;");
                break;
            case "java.applet.AudioClip":
                Method(c, "play", "()V"); Method(c, "loop", "()V"); Method(c, "stop", "()V");
                break;
            case "java.awt.Toolkit":
                StaticMethod(c, "getDefaultToolkit", "()Ljava/awt/Toolkit;");
                Method(c, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;");
                Method(c, "getImage", "(Ljava/lang/String;)Ljava/awt/Image;");
                Method(c, "beep", "()V"); Method(c, "sync", "()V");
                Method(c, "getScreenResolution", "()I"); Method(c, "getScreenSize", "()Ljava/awt/Dimension;");
                break;
            case "java.awt.MediaTracker":
                Method(c, "<init>", "(Ljava/awt/Component;)V", 0);
                Method(c, "addImage", "(Ljava/awt/Image;I)V");
                Method(c, "waitForID", "(I)V"); Method(c, "waitForAll", "()V");
                Method(c, "checkID", "(I)Z"); Method(c, "checkAll", "()Z");
                Method(c, "statusID", "(II)I");
                Field(c, "LOADING", "I", 0x0008); Field(c, "ABORTED", "I", 0x0008);
                Field(c, "ERRORED", "I", 0x0008); Field(c, "COMPLETE", "I", 0x0008);
                break;
            case "java.awt.Graphics": RegisterGraphics(c); break;
            case "java.awt.Cursor":
                Method(c, "<init>", "(I)V", 0);
                Field(c, "DEFAULT_CURSOR", "I", 0x0008); Field(c, "CROSSHAIR_CURSOR", "I", 0x0008);
                Field(c, "TEXT_CURSOR", "I", 0x0008); Field(c, "WAIT_CURSOR", "I", 0x0008);
                Field(c, "HAND_CURSOR", "I", 0x0008); Field(c, "MOVE_CURSOR", "I", 0x0008);
                break;
            case "java.awt.Image":
                Method(c, "getWidth", "(Ljava/awt/image/ImageObserver;)I"); Method(c, "getHeight", "(Ljava/awt/image/ImageObserver;)I");
                Method(c, "getWidth", "(Ljava/lang/Object;)I"); Method(c, "getHeight", "(Ljava/lang/Object;)I");
                Method(c, "getGraphics", "()Ljava/awt/Graphics;"); Method(c, "flush", "()V");
                break;
            case "java.awt.image.ImageObserver":
                Method(c, "imageUpdate", "(Ljava/awt/Image;IIII)Z");
                Field(c, "WIDTH", "I", 0x0008); Field(c, "HEIGHT", "I", 0x0008);
                Field(c, "PROPERTIES", "I", 0x0008); Field(c, "SOMEBITS", "I", 0x0008);
                Field(c, "FRAMEBITS", "I", 0x0008); Field(c, "ALLBITS", "I", 0x0008);
                Field(c, "ERROR", "I", 0x0008); Field(c, "ABORTED", "I", 0x0008);
                break;
            case "java.awt.Color":
                Method(c, "<init>", "(III)V", 0); Method(c, "<init>", "(IIII)V", 0); Method(c, "<init>", "(I)V", 0);
                Method(c, "getRed", "()I"); Method(c, "getGreen", "()I"); Method(c, "getBlue", "()I");
                Method(c, "getAlpha", "()I"); Method(c, "getRGB", "()I");
                Method(c, "brighter", "()Ljava/awt/Color;"); Method(c, "darker", "()Ljava/awt/Color;");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                foreach (var n in new[] { "black", "white", "red", "green", "blue", "yellow", "gray", "lightGray", "darkGray", "orange", "magenta", "cyan", "pink" })
                    Field(c, n, "Ljava/awt/Color;", 0x0008);
                break;
            case "java.awt.Font":
                Method(c, "<init>", "(Ljava/lang/String;II)V", 0);
                Method(c, "getName", "()Ljava/lang/String;"); Method(c, "getFamily", "()Ljava/lang/String;");
                Method(c, "getFontName", "()Ljava/lang/String;");
                Method(c, "getSize", "()I"); Method(c, "getStyle", "()I");
                Method(c, "isBold", "()Z"); Method(c, "isItalic", "()Z"); Method(c, "isPlain", "()Z");
                Field(c, "PLAIN", "I", 0x0008); Field(c, "BOLD", "I", 0x0008); Field(c, "ITALIC", "I", 0x0008);
                break;
            case "java.awt.FontMetrics":
                Method(c, "stringWidth", "(Ljava/lang/String;)I"); Method(c, "charsWidth", "([CII)I");
                Method(c, "charWidth", "(C)I");
                Method(c, "getHeight", "()I"); Method(c, "getAscent", "()I"); Method(c, "getDescent", "()I");
                break;
            case "java.awt.Panel": Method(c, "<init>", "()V", 0); break;
            case "java.awt.Canvas": break; // inherits Container behavior
            case "java.awt.Component":
                Method(c, "<init>", "()V", 0);
                Method(c, "paint", "(Ljava/awt/Graphics;)V"); Method(c, "update", "(Ljava/awt/Graphics;)V");
                Method(c, "repaint", "()V"); Method(c, "repaint", "(J)V");
                Method(c, "repaint", "(IIII)V"); Method(c, "repaint", "(JIIII)V");
                Method(c, "getGraphics", "()Ljava/awt/Graphics;");
                Method(c, "createImage", "(II)Ljava/awt/Image;");
                Method(c, "prepareImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z");
                Method(c, "checkImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)I");
                Method(c, "setBackground", "(Ljava/awt/Color;)V"); Method(c, "getBackground", "()Ljava/awt/Color;");
                Method(c, "setForeground", "(Ljava/awt/Color;)V"); Method(c, "getForeground", "()Ljava/awt/Color;");
                Method(c, "setFont", "(Ljava/awt/Font;)V"); Method(c, "getFont", "()Ljava/awt/Font;");
                Method(c, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;");
                Method(c, "setVisible", "(Z)V"); Method(c, "isVisible", "()Z");
                Method(c, "show", "()V"); Method(c, "hide", "()V");
                Method(c, "enable", "()V"); Method(c, "disable", "()V");
                Method(c, "setEnabled", "(Z)V"); Method(c, "isEnabled", "()Z");
                Method(c, "setSize", "(II)V"); Method(c, "resize", "(II)V");
                Method(c, "getSize", "()Ljava/awt/Dimension;"); Method(c, "size", "()Ljava/awt/Dimension;");
                Method(c, "preferredSize", "()Ljava/awt/Dimension;"); Method(c, "minimumSize", "()Ljava/awt/Dimension;");
                Method(c, "setBounds", "(IIII)V"); Method(c, "getBounds", "()Ljava/awt/Rectangle;");
                Method(c, "move", "(II)V"); Method(c, "setLocation", "(II)V");
                Method(c, "location", "()Ljava/awt/Point;"); Method(c, "getLocation", "()Ljava/awt/Point;");
                Method(c, "contains", "(II)Z"); Method(c, "inside", "(II)Z");
                Method(c, "getParent", "()Ljava/awt/Container;");
                Method(c, "setCursor", "(Ljava/awt/Cursor;)V");
                Method(c, "requestFocus", "()V");
                Method(c, "invalidate", "()V");
                Method(c, "getToolkit", "()Ljava/awt/Toolkit;");
                Method(c, "handleEvent", "(Ljava/awt/Event;)Z");
                Method(c, "postEvent", "(Ljava/awt/Event;)Z");
                Method(c, "action", "(Ljava/awt/Event;Ljava/lang/Object;)Z");
                foreach (var n in new[] { "mouseDown", "mouseUp", "mouseDrag", "mouseMove", "mouseEnter", "mouseExit" })
                    Method(c, n, "(Ljava/awt/Event;II)Z");
                foreach (var n in new[] { "keyDown", "keyUp" })
                    Method(c, n, "(Ljava/awt/Event;I)Z");
                Method(c, "addMouseListener", "(Ljava/awt/event/MouseListener;)V"); Method(c, "removeMouseListener", "(Ljava/awt/event/MouseListener;)V");
                Method(c, "addMouseMotionListener", "(Ljava/awt/event/MouseMotionListener;)V"); Method(c, "removeMouseMotionListener", "(Ljava/awt/event/MouseMotionListener;)V");
                Method(c, "addKeyListener", "(Ljava/awt/event/KeyListener;)V"); Method(c, "removeKeyListener", "(Ljava/awt/event/KeyListener;)V");
                Method(c, "addActionListener", "(Ljava/awt/event/ActionListener;)V"); Method(c, "removeActionListener", "(Ljava/awt/event/ActionListener;)V");
                Method(c, "addItemListener", "(Ljava/awt/event/ItemListener;)V"); Method(c, "removeItemListener", "(Ljava/awt/event/ItemListener;)V");
                break;
            case "java.awt.Container":
                Method(c, "add", "(Ljava/awt/Component;)Ljava/awt/Component;");
                Method(c, "add", "(Ljava/lang/String;Ljava/awt/Component;)Ljava/awt/Component;");
                Method(c, "add", "(Ljava/awt/Component;I)Ljava/awt/Component;");
                Method(c, "add", "(Ljava/awt/Component;Ljava/lang/Object;)Ljava/awt/Component;");
                Method(c, "remove", "(Ljava/awt/Component;)V"); Method(c, "removeAll", "()V");
                Method(c, "getComponent", "(I)Ljava/awt/Component;");
                Method(c, "getComponentCount", "()I"); Method(c, "countComponents", "()I");
                Method(c, "getComponents", "()[Ljava/awt/Component;");
                Method(c, "locate", "(II)Ljava/awt/Component;");
                Method(c, "setLayout", "(Ljava/awt/LayoutManager;)V"); Method(c, "getLayout", "()Ljava/awt/LayoutManager;");
                Method(c, "validate", "()V"); Method(c, "layout", "()V"); Method(c, "doLayout", "()V");
                Method(c, "paintComponents", "(Ljava/awt/Graphics;)V");
                break;
            case "java.awt.Button":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "getLabel", "()Ljava/lang/String;"); Method(c, "setLabel", "(Ljava/lang/String;)V");
                break;
            case "java.awt.Label":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "<init>", "(Ljava/lang/String;I)V", 0);
                Method(c, "getText", "()Ljava/lang/String;"); Method(c, "setText", "(Ljava/lang/String;)V");
                Method(c, "setAlignment", "(I)V"); Method(c, "getAlignment", "()I");
                Field(c, "LEFT", "I", 0x0008); Field(c, "CENTER", "I", 0x0008); Field(c, "RIGHT", "I", 0x0008);
                break;
            case "java.awt.TextField":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(I)V", 0);
                Method(c, "<init>", "(Ljava/lang/String;)V", 0); Method(c, "<init>", "(Ljava/lang/String;I)V", 0);
                Method(c, "getText", "()Ljava/lang/String;"); Method(c, "setText", "(Ljava/lang/String;)V");
                Method(c, "getColumns", "()I"); Method(c, "setEditable", "(Z)V"); Method(c, "isEditable", "()Z");
                break;
            case "java.awt.TextArea":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "<init>", "(Ljava/lang/String;II)V", 0); Method(c, "<init>", "(II)V", 0);
                Method(c, "getText", "()Ljava/lang/String;"); Method(c, "setText", "(Ljava/lang/String;)V");
                Method(c, "append", "(Ljava/lang/String;)V"); Method(c, "insert", "(Ljava/lang/String;I)V");
                Method(c, "setEditable", "(Z)V"); Method(c, "isEditable", "()Z");
                break;
            case "java.awt.Checkbox":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(Ljava/lang/String;)V", 0);
                Method(c, "<init>", "(Ljava/lang/String;Z)V", 0);
                Method(c, "getState", "()Z"); Method(c, "setState", "(Z)V");
                Method(c, "getLabel", "()Ljava/lang/String;"); Method(c, "setLabel", "(Ljava/lang/String;)V");
                break;
            case "java.awt.Choice":
                Method(c, "<init>", "()V", 0);
                Method(c, "addItem", "(Ljava/lang/String;)V"); Method(c, "add", "(Ljava/lang/String;)V");
                Method(c, "getItem", "(I)Ljava/lang/String;");
                Method(c, "getItemCount", "()I"); Method(c, "countItems", "()I");
                Method(c, "getSelectedItem", "()Ljava/lang/String;"); Method(c, "getSelectedIndex", "()I");
                Method(c, "select", "(I)V"); Method(c, "select", "(Ljava/lang/String;)V");
                break;
            case "java.awt.List":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(I)V", 0);
                Method(c, "addItem", "(Ljava/lang/String;)V"); Method(c, "add", "(Ljava/lang/String;)V");
                Method(c, "getItem", "(I)Ljava/lang/String;");
                Method(c, "getItemCount", "()I"); Method(c, "countItems", "()I");
                Method(c, "getSelectedItem", "()Ljava/lang/String;"); Method(c, "getSelectedIndex", "()I");
                Method(c, "select", "(I)V"); Method(c, "deselect", "(I)V");
                break;
            case "java.awt.Scrollbar":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(I)V", 0);
                Method(c, "<init>", "(IIIII)V", 0);
                Method(c, "getValue", "()I"); Method(c, "setValue", "(I)V");
                Method(c, "getMinimum", "()I"); Method(c, "getMaximum", "()I");
                Method(c, "getVisibleAmount", "()I"); Method(c, "setVisibleAmount", "(I)V");
                Method(c, "setValues", "(IIII)V");
                Method(c, "getOrientation", "()I");
                Method(c, "setUnitIncrement", "(I)V"); Method(c, "getUnitIncrement", "()I");
                Method(c, "setBlockIncrement", "(I)V"); Method(c, "getBlockIncrement", "()I");
                Field(c, "VERTICAL", "I", 0x0008); Field(c, "HORIZONTAL", "I", 0x0008);
                break;
            case "java.awt.Dimension":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(II)V", 0);
                Field(c, "width", "I"); Field(c, "height", "I");
                break;
            case "java.awt.Insets":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(IIII)V", 0);
                Field(c, "top", "I"); Field(c, "left", "I"); Field(c, "bottom", "I"); Field(c, "right", "I");
                break;
            case "java.awt.Point":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(II)V", 0);
                Field(c, "x", "I"); Field(c, "y", "I");
                break;
            case "java.awt.Rectangle":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(IIII)V", 0);
                Method(c, "<init>", "(II)V", 0);
                Field(c, "x", "I"); Field(c, "y", "I"); Field(c, "width", "I"); Field(c, "height", "I");
                break;
            case "java.awt.Event":
                Method(c, "<init>", "()V", 0);
                Method(c, "<init>", "(Ljava/lang/Object;JIIII)V", 0);
                Method(c, "<init>", "(Ljava/lang/Object;JIIIIILjava/lang/Object;)V", 0);
                Field(c, "target", "Ljava/lang/Object;"); Field(c, "when", "J"); Field(c, "id", "I");
                Field(c, "x", "I"); Field(c, "y", "I"); Field(c, "key", "I"); Field(c, "modifiers", "I");
                Field(c, "arg", "Ljava/lang/Object;");
                Field(c, "SHIFT_MASK", "I", 0x0008); Field(c, "CTRL_MASK", "I", 0x0008);
                Field(c, "META_MASK", "I", 0x0008); Field(c, "ALT_MASK", "I", 0x0008);
                break;
            case "java.awt.event.MouseEvent":
                Method(c, "<init>", "(Ljava/awt/Component;IJIIIIIZ)V", 0);
                Method(c, "getX", "()I"); Method(c, "getY", "()I"); Method(c, "getID", "()I");
                Method(c, "getButton", "()I"); Method(c, "getClickCount", "()I");
                Method(c, "getModifiers", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                Method(c, "isPopupTrigger", "()Z");
                Field(c, "MOUSE_CLICKED", "I", 0x0008); Field(c, "MOUSE_PRESSED", "I", 0x0008);
                Field(c, "MOUSE_RELEASED", "I", 0x0008); Field(c, "MOUSE_MOVED", "I", 0x0008);
                Field(c, "MOUSE_ENTERED", "I", 0x0008); Field(c, "MOUSE_EXITED", "I", 0x0008);
                Field(c, "MOUSE_DRAGGED", "I", 0x0008); Field(c, "MOUSE_WHEEL", "I", 0x0008);
                Field(c, "NOBUTTON", "I", 0x0008); Field(c, "BUTTON1", "I", 0x0008);
                Field(c, "BUTTON2", "I", 0x0008); Field(c, "BUTTON3", "I", 0x0008);
                break;
            case "java.awt.event.MouseMotionEvent":
                break;
            case "java.awt.event.KeyEvent":
                Method(c, "<init>", "(Ljava/awt/Component;IJII)V", 0);
                Method(c, "<init>", "(Ljava/awt/Component;IJIIC)V", 0);
                Method(c, "getKeyCode", "()I"); Method(c, "getKeyChar", "()C"); Method(c, "getID", "()I");
                Method(c, "getModifiers", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                Field(c, "KEY_TYPED", "I", 0x0008); Field(c, "KEY_PRESSED", "I", 0x0008); Field(c, "KEY_RELEASED", "I", 0x0008);
                break;
            case "java.awt.event.ActionEvent":
                Method(c, "<init>", "(Ljava/lang/Object;ILjava/lang/String;)V", 0);
                Method(c, "getActionCommand", "()Ljava/lang/String;"); Method(c, "getID", "()I");
                Method(c, "getModifiers", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                Field(c, "ACTION_PERFORMED", "I", 0x0008);
                Field(c, "SHIFT_MASK", "I", 0x0008); Field(c, "CTRL_MASK", "I", 0x0008);
                Field(c, "META_MASK", "I", 0x0008); Field(c, "ALT_MASK", "I", 0x0008);
                break;
            case "java.awt.event.ItemEvent":
                Method(c, "<init>", "(Ljava/lang/Object;ILjava/lang/Object;I)V", 0);
                Method(c, "getItem", "()Ljava/lang/Object;"); Method(c, "getID", "()I");
                Method(c, "getStateChange", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                Field(c, "ITEM_STATE_CHANGED", "I", 0x0008);
                Field(c, "SELECTED", "I", 0x0008); Field(c, "DESELECTED", "I", 0x0008);
                break;
            case "java.awt.event.InputEvent":
                Field(c, "SHIFT_MASK", "I", 0x0008); Field(c, "CTRL_MASK", "I", 0x0008);
                Field(c, "META_MASK", "I", 0x0008); Field(c, "ALT_MASK", "I", 0x0008);
                break;
            case "java.awt.FlowLayout":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(I)V", 0); Method(c, "<init>", "(III)V", 0);
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                break;
            case "java.awt.BorderLayout":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(II)V", 0);
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                Field(c, "NORTH", "Ljava/lang/String;", 0x0008); Field(c, "SOUTH", "Ljava/lang/String;", 0x0008);
                Field(c, "EAST", "Ljava/lang/String;", 0x0008); Field(c, "WEST", "Ljava/lang/String;", 0x0008);
                Field(c, "CENTER", "Ljava/lang/String;", 0x0008);
                break;
            case "java.awt.GridLayout":
                Method(c, "<init>", "()V", 0); Method(c, "<init>", "(II)V", 0); Method(c, "<init>", "(IIII)V", 0);
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                break;
            case "java.awt.event.MouseListener":
                foreach (var n in new[] { "mouseClicked", "mousePressed", "mouseReleased", "mouseEntered", "mouseExited" })
                    Method(c, n, "(Ljava/awt/event/MouseEvent;)V");
                break;
            case "java.awt.event.MouseMotionListener":
                Method(c, "mouseDragged", "(Ljava/awt/event/MouseEvent;)V");
                Method(c, "mouseMoved", "(Ljava/awt/event/MouseEvent;)V");
                break;
            case "java.awt.event.KeyListener":
                foreach (var n in new[] { "keyPressed", "keyReleased", "keyTyped" })
                    Method(c, n, "(Ljava/awt/event/KeyEvent;)V");
                break;
            case "java.awt.event.ActionListener":
                Method(c, "actionPerformed", "(Ljava/awt/event/ActionEvent;)V");
                break;
            case "java.awt.event.ItemListener":
                Method(c, "itemStateChanged", "(Ljava/awt/event/ItemEvent;)V");
                break;
            default:
                break;
        }

        RegisterNatives(vm, c);
    }

    private static void RegisterGraphics(JClass c)
    {
        Method(c, "create", "()Ljava/awt/Graphics;"); Method(c, "create", "(IIII)Ljava/awt/Graphics;");
        Method(c, "dispose", "()V"); Method(c, "translate", "(II)V");
        Method(c, "setColor", "(Ljava/awt/Color;)V"); Method(c, "getColor", "()Ljava/awt/Color;");
        Method(c, "setFont", "(Ljava/awt/Font;)V"); Method(c, "getFont", "()Ljava/awt/Font;");
        Method(c, "clipRect", "(IIII)V"); Method(c, "setClip", "(IIII)V"); Method(c, "getClipBounds", "()Ljava/awt/Rectangle;");
        Method(c, "hitClip", "(IIII)Z");
        Method(c, "clearRect", "(IIII)V");
        Method(c, "getFontMetrics", "()Ljava/awt/FontMetrics;"); Method(c, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;");
        Method(c, "setPaintMode", "()V"); Method(c, "setXORMode", "(Ljava/awt/Color;)V");
        Method(c, "copyArea", "(IIIIII)V");
        foreach (var n in new[] { "drawLine", "drawRect", "fillRect", "drawOval", "fillOval" }) Method(c, n, "(IIII)V");
        Method(c, "drawRoundRect", "(IIIIII)V"); Method(c, "fillRoundRect", "(IIIIII)V");
        Method(c, "draw3DRect", "(IIIIIZ)V"); Method(c, "fill3DRect", "(IIIIIZ)V");
        Method(c, "drawString", "(Ljava/lang/String;II)V");
        Method(c, "drawBytes", "([BIIIII)V"); Method(c, "drawChars", "([CIIIII)V");
        Method(c, "drawPolygon", "([I[II)V"); Method(c, "fillPolygon", "([I[II)V");
        Method(c, "drawArc", "(IIIIII)V"); Method(c, "fillArc", "(IIIIII)V");
        Method(c, "drawImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IILjava/lang/Object;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/lang/Object;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/lang/Object;)Z");
    }

    private static void RegisterNatives(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.lang.Object": RegisterObject(vm, c); break;
            case "java.lang.Throwable": RegisterThrowable(vm, c); break;
            case "java.lang.Exception":
            case "java.lang.CloneNotSupportedException":
            case "java.lang.RuntimeException":
            case "java.lang.Error":
            case "java.lang.IllegalArgumentException":
            case "java.lang.NumberFormatException":
            case "java.lang.NullPointerException":
            case "java.lang.ArrayIndexOutOfBoundsException":
            case "java.lang.ClassCastException":
            case "java.lang.ArithmeticException":
            case "java.lang.NegativeArraySizeException":
            case "java.lang.ArrayStoreException":
            case "java.lang.InterruptedException":
            case "java.lang.OutOfMemoryError":
                vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = ""; return JValue.Void; });
                vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = vm.StringValue(i.Arguments[0]); return JValue.Void; });
                vm.RegisterNative(c.Name, "getMessage", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as string) ?? "")));
                vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.ToJavaString(i.Receiver))));
                break;
            case "java.lang.Class": RegisterClass(vm, c); break;
            case "java.lang.String": RegisterString(vm, c); break;
            case "java.lang.StringBuffer": RegisterStringBuffer(vm, c); break;
            case "java.lang.System": RegisterSystem(vm, c); break;
            case "java.lang.Character": RegisterCharacter(vm, c); break;
            case "java.lang.SecurityManager": vm.RegisterNative(c.Name, "checkPermission", "(Ljava/lang/Object;)V", _ => JValue.Void); break;
            case "java.lang.PrintStream":
            case "java.io.PrintStream": RegisterPrintStream(vm, c); break;
            case "java.lang.Thread": RegisterThread(vm, c); break;
            case "java.lang.Integer": RegisterInteger(vm, c); break;
            case "java.lang.Long": RegisterLong(vm, c); break;
            case "java.lang.Float": RegisterFloat(vm, c); break;
            case "java.lang.Double": RegisterDouble(vm, c); break;
            case "java.lang.Boolean": RegisterBoolean(vm, c); break;
            case "java.lang.Byte": RegisterByte(vm, c); break;
            case "java.lang.Short": RegisterShort(vm, c); break;
            case "java.lang.Math": RegisterMath(vm, c); break;
            case "java.util.Random": RegisterRandom(vm, c); break;
            case "java.net.URL": RegisterUrl(vm, c); break;
            case "java.applet.Applet": RegisterApplet(vm, c); break;
            case "java.applet.AppletContext": RegisterAppletContext(vm, c); break;
            case "java.applet.AudioClip":
                vm.RegisterNative(c.Name, "play", "()V", _ => JValue.Void);
                vm.RegisterNative(c.Name, "loop", "()V", _ => JValue.Void);
                vm.RegisterNative(c.Name, "stop", "()V", _ => JValue.Void);
                break;
            case "java.awt.Toolkit": RegisterToolkit(vm, c); break;
            case "java.awt.MediaTracker": RegisterMediaTracker(vm, c); break;
            case "java.awt.Cursor":
                vm.RegisterNative(c.Name, "<init>", "(I)V", _ => JValue.Void);
                vm.SetStatic(c, "DEFAULT_CURSOR", "I", JValue.Int(0)); vm.SetStatic(c, "CROSSHAIR_CURSOR", "I", JValue.Int(1));
                vm.SetStatic(c, "TEXT_CURSOR", "I", JValue.Int(2)); vm.SetStatic(c, "WAIT_CURSOR", "I", JValue.Int(3));
                vm.SetStatic(c, "HAND_CURSOR", "I", JValue.Int(4)); vm.SetStatic(c, "MOVE_CURSOR", "I", JValue.Int(5));
                break;
            case "java.awt.Graphics": JavaGraphicsBridge.Register(vm, c); break;
            case "java.awt.Image": RegisterImage(vm, c); break;
            case "java.awt.image.ImageObserver":
                vm.SetStatic(c, "WIDTH", "I", JValue.Int(1)); vm.SetStatic(c, "HEIGHT", "I", JValue.Int(2));
                vm.SetStatic(c, "PROPERTIES", "I", JValue.Int(4)); vm.SetStatic(c, "SOMEBITS", "I", JValue.Int(8));
                vm.SetStatic(c, "FRAMEBITS", "I", JValue.Int(16)); vm.SetStatic(c, "ALLBITS", "I", JValue.Int(32));
                vm.SetStatic(c, "ERROR", "I", JValue.Int(64)); vm.SetStatic(c, "ABORTED", "I", JValue.Int(128));
                break;
            case "java.awt.Color": RegisterColor(vm, c); break;
            case "java.awt.Font": RegisterFont(vm, c); break;
            case "java.awt.FontMetrics": RegisterFontMetrics(vm, c); break;
            case "java.awt.Panel":
                vm.RegisterNative(c.Name, "<init>", "()V", i =>
                {
                    var st = JavaComponentBridge.State(i.Receiver.AsObject()!);
                    st.Layout = vm.Construct(vm.LoadClass("java.awt.FlowLayout"));
                    return JValue.Void;
                });
                break;
            case "java.awt.Component": JavaComponentBridge.Register(vm, c); break;
            case "java.awt.Container": JavaComponentBridge.RegisterContainer(vm, c); break;
            case "java.awt.Button": JavaComponentBridge.RegisterTextWidget(vm, c, "label"); break;
            case "java.awt.Label": JavaComponentBridge.RegisterTextWidget(vm, c, "text"); break;
            case "java.awt.TextField": JavaComponentBridge.RegisterTextWidget(vm, c, "text"); break;
            case "java.awt.TextArea": JavaComponentBridge.RegisterTextWidget(vm, c, "text"); break;
            case "java.awt.Checkbox": JavaComponentBridge.RegisterCheckbox(vm, c); break;
            case "java.awt.Choice": JavaComponentBridge.RegisterChoice(vm, c); break;
            case "java.awt.List": JavaComponentBridge.RegisterChoice(vm, c); break;
            case "java.awt.Scrollbar": JavaComponentBridge.RegisterScrollbar(vm, c); break;
            case "java.awt.Dimension": JavaComponentBridge.RegisterSimpleIntPair(vm, c, "width", "height"); break;
            case "java.awt.Insets": JavaComponentBridge.RegisterInsets(vm, c); break;
            case "java.awt.Point": JavaComponentBridge.RegisterSimpleIntPair(vm, c, "x", "y"); break;
            case "java.awt.Rectangle": JavaComponentBridge.RegisterSimpleRect(vm, c); break;
            case "java.awt.Event": JavaComponentBridge.RegisterEvent(vm, c); break;
            case "java.awt.event.MouseEvent": JavaComponentBridge.RegisterMouseEvent(vm, c); break;
            case "java.awt.event.KeyEvent": JavaComponentBridge.RegisterKeyEvent(vm, c); break;
            case "java.awt.event.ActionEvent": JavaComponentBridge.RegisterActionEvent(vm, c); break;
            case "java.awt.event.ItemEvent": JavaComponentBridge.RegisterItemEvent(vm, c); break;
            case "java.awt.event.InputEvent":
                vm.SetStatic(c, "SHIFT_MASK", "I", JValue.Int(1)); vm.SetStatic(c, "CTRL_MASK", "I", JValue.Int(2));
                vm.SetStatic(c, "META_MASK", "I", JValue.Int(4)); vm.SetStatic(c, "ALT_MASK", "I", JValue.Int(8));
                break;
            case "java.awt.FlowLayout": JavaComponentBridge.RegisterFlowLayout(vm, c); break;
            case "java.awt.BorderLayout": JavaComponentBridge.RegisterBorderLayout(vm, c); break;
            case "java.awt.GridLayout": JavaComponentBridge.RegisterGridLayout(vm, c); break;
        }
    }

    private static void RegisterObject(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i => JValue.Int(ReferenceEquals(i.Receiver.RefValue, i.Arguments[0].RefValue) ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i => JValue.Int(RuntimeHelpers.GetHashCode(i.Receiver.AsObject()!)));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => { var o = i.Receiver.AsObject()!; return JValue.Ref(vm.CreateString(o.Class.Name + "@" + RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture))); });
        vm.RegisterNative(c.Name, "getClass", "()Ljava/lang/Class;", i => JValue.Ref(vm.CreateClassObject(i.Receiver.AsObject()!.Class)));
        vm.RegisterNative(c.Name, "wait", "()V", i => { try { System.Threading.Monitor.Wait(i.Receiver.AsObject()!); } catch (SynchronizationLockException) { } return JValue.Void; });
        vm.RegisterNative(c.Name, "wait", "(J)V", i => { long ms = i.Arguments[0].AsLong(); try { System.Threading.Monitor.Wait(i.Receiver.AsObject()!, Math.Clamp(ms, 0, int.MaxValue)); } catch (SynchronizationLockException) { } return JValue.Void; });
        vm.RegisterNative(c.Name, "wait", "(JI)V", i => { long ms = i.Arguments[0].AsLong(); try { System.Threading.Monitor.Wait(i.Receiver.AsObject()!, Math.Clamp(ms, 0, int.MaxValue)); } catch (SynchronizationLockException) { } return JValue.Void; });
        vm.RegisterNative(c.Name, "notify", "()V", i => { try { System.Threading.Monitor.Pulse(i.Receiver.AsObject()!); } catch (SynchronizationLockException) { } return JValue.Void; });
        vm.RegisterNative(c.Name, "notifyAll", "()V", i => { try { System.Threading.Monitor.PulseAll(i.Receiver.AsObject()!); } catch (SynchronizationLockException) { } return JValue.Void; });
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
            return JValue.Int(jc != null && o != null && vm.IsAssignableFrom(jc, o.Class) ? 1 : 0);
        });
    }

    private static void RegisterString(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = ""; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "([C)V", i => { i.Receiver.AsObject()!.NativeState = Chars(vm, i.Arguments[0], 0, -1); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "([CII)V", i => { i.Receiver.AsObject()!.NativeState = Chars(vm, i.Arguments[0], i.Arguments[1].AsInt(), i.Arguments[2].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "length", "()I", i => JValue.Int(vm.StringValue(i.Receiver).Length));
        vm.RegisterNative(c.Name, "charAt", "(I)C", i => JValue.Int(vm.StringValue(i.Receiver)[i.Arguments[0].AsInt()]));
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
        {
            int h = 0; foreach (var ch in vm.StringValue(i.Receiver)) h = unchecked(31 * h + ch);
            return JValue.Int(h);
        });
        // FIX: null argument must compare unequal, not against the literal "null"
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
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => i.Receiver);
        vm.RegisterNative(c.Name, "intern", "()Ljava/lang/String;", i => i.Receiver);
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
        vm.RegisterNative(c.Name, "trim", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver).Trim())));
        vm.RegisterNative(c.Name, "replace", "(CC)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver).Replace((char)i.Arguments[0].AsInt(), (char)i.Arguments[1].AsInt()))));
        vm.RegisterNative(c.Name, "substring", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver)[i.Arguments[0].AsInt()..])));
        vm.RegisterNative(c.Name, "substring", "(II)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver).Substring(i.Arguments[0].AsInt(), i.Arguments[1].AsInt() - i.Arguments[0].AsInt()))));
        vm.RegisterNative(c.Name, "indexOf", "(I)I", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "indexOf", "(II)I", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf((char)i.Arguments[0].AsInt(), i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "indexOf", "(Ljava/lang/String;)I", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal)));
        vm.RegisterNative(c.Name, "indexOf", "(Ljava/lang/String;I)I", i => JValue.Int(vm.StringValue(i.Receiver).IndexOf(vm.StringValue(i.Arguments[0]), i.Arguments[1].AsInt(), StringComparison.Ordinal)));
        vm.RegisterNative(c.Name, "lastIndexOf", "(I)I", i => JValue.Int(vm.StringValue(i.Receiver).LastIndexOf((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "lastIndexOf", "(Ljava/lang/String;)I", i => JValue.Int(vm.StringValue(i.Receiver).LastIndexOf(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal)));
        vm.RegisterNative(c.Name, "startsWith", "(Ljava/lang/String;)Z", i => JValue.Int(vm.StringValue(i.Receiver).StartsWith(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal) ? 1 : 0));
        vm.RegisterNative(c.Name, "endsWith", "(Ljava/lang/String;)Z", i => JValue.Int(vm.StringValue(i.Receiver).EndsWith(vm.StringValue(i.Arguments[0]), StringComparison.Ordinal) ? 1 : 0));
        vm.RegisterNative(c.Name, "concat", "(Ljava/lang/String;)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.StringValue(i.Receiver) + vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "compareTo", "(Ljava/lang/String;)I", i => JValue.Int(string.CompareOrdinal(vm.StringValue(i.Receiver), vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "valueOf", "(Ljava/lang/Object;)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(vm.ToJavaString(i.Arguments[0])));
        vm.RegisterNative(c.Name, "valueOf", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "valueOf", "(J)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsLong().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "valueOf", "(C)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(((char)i.Arguments[0].AsInt()).ToString())));
        vm.RegisterNative(c.Name, "valueOf", "(F)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.FloatStr(i.Arguments[0].AsFloat()))));
        vm.RegisterNative(c.Name, "valueOf", "(D)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.DoubleStr(i.Arguments[0].AsDouble()))));
        vm.RegisterNative(c.Name, "valueOf", "(Z)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt() != 0 ? "true" : "false")));
    }

    private static string Chars(JavaVm vm, JValue v, int start, int len)
    {
        var a = v.AsArray() ?? throw new InvalidOperationException("char[] expected");
        if (len < 0) len = a.Elements.Length - start;
        return new string(a.Elements.Skip(start).Take(len).Select(x => (char)x.AsInt()).ToArray());
    }

    private static void RegisterStringBuffer(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new StringBuilder(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = new StringBuilder(vm.StringValue(i.Arguments[0])); return JValue.Void; });
        vm.RegisterNative(c.Name, "length", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as StringBuilder)?.Length ?? 0));
        vm.RegisterNative(c.Name, "charAt", "(I)C", i => JValue.Int((i.Receiver.AsObject()?.NativeState as StringBuilder)?[i.Arguments[0].AsInt()] ?? '\0'));
        vm.RegisterNative(c.Name, "setCharAt", "(IC)V", i => { var sb = i.Receiver.AsObject()?.NativeState as StringBuilder; if (sb != null) sb[i.Arguments[0].AsInt()] = (char)i.Arguments[1].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "setLength", "(I)V", i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Length(i.Arguments[0].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "reverse", "()Ljava/lang/StringBuffer;", i => { var sb = i.Receiver.AsObject()?.NativeState as StringBuilder; sb?.Reverse(); return i.Receiver; });
        vm.RegisterNative(c.Name, "deleteCharAt", "(I)Ljava/lang/StringBuffer;", i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Remove(i.Arguments[0].AsInt(), 1); return i.Receiver; });
        vm.RegisterNative(c.Name, "insert", "(ILjava/lang/String;)Ljava/lang/StringBuffer;", i => { (i.Receiver.AsObject()?.NativeState as StringBuilder)?.Insert(i.Arguments[0].AsInt(), vm.StringValue(i.Arguments[1])); return i.Receiver; });
        // FIX: descriptor-aware conversion (old code appended int 65 as 'A')
        foreach (var t in new[] { "(Ljava/lang/String;)", "(Ljava/lang/Object;)", "(I)", "(J)", "(F)", "(D)", "(Z)", "(C)" })
        {
            string desc = t;
            vm.RegisterNative(c.Name, "append", desc + "Ljava/lang/StringBuffer;", i =>
            {
                var o = i.Receiver.AsObject()!;
                var sb = o.NativeState as StringBuilder ?? new StringBuilder();
                sb.Append(desc switch
                {
                    "(Ljava/lang/String;)" => vm.StringValue(i.Arguments[0]),
                    "(Ljava/lang/Object;)" => vm.ToJavaString(i.Arguments[0]),
                    "(I)" => i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture),
                    "(J)" => i.Arguments[0].AsLong().ToString(CultureInfo.InvariantCulture),
                    "(F)" => JavaText.FloatStr(i.Arguments[0].AsFloat()),
                    "(D)" => JavaText.DoubleStr(i.Arguments[0].AsDouble()),
                    "(Z)" => i.Arguments[0].AsInt() != 0 ? "true" : "false",
                    _ => ((char)i.Arguments[0].AsInt()).ToString()
                });
                o.NativeState = sb;
                return i.Receiver;
            });
        }
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as StringBuilder)?.ToString() ?? "")));
    }

    private static void RegisterSystem(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "currentTimeMillis", "()J", _ => JValue.Long(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        vm.RegisterNative(c.Name, "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", i =>
        {
            var src = i.Arguments[0].AsArray() ?? throw new InvalidOperationException("arraycopy src");
            var dst = i.Arguments[2].AsArray() ?? throw new InvalidOperationException("arraycopy dst");
            Array.Copy(src.Elements, i.Arguments[1].AsInt(), dst.Elements, i.Arguments[3].AsInt(), i.Arguments[4].AsInt());
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "exit", "(I)V", _ => JValue.Void);
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

    private static void RegisterCharacter(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(C)V", i => { i.Receiver.AsObject()!.NativeState = (char)i.Arguments[0].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "charValue", "()C", i => JValue.Int(i.Receiver.AsObject()?.NativeState is char ch ? ch : '\0'));
        vm.RegisterNative(c.Name, "isDigit", "(C)Z", i => JValue.Int(char.IsDigit((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isLetter", "(C)Z", i => JValue.Int(char.IsLetter((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isLetterOrDigit", "(C)Z", i => JValue.Int(char.IsLetterOrDigit((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isSpace", "(C)Z", i => JValue.Int(char.IsWhiteSpace((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isWhitespace", "(C)Z", i => JValue.Int(char.IsWhiteSpace((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isUpperCase", "(C)Z", i => JValue.Int(char.IsUpper((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isLowerCase", "(C)Z", i => JValue.Int(char.IsLower((char)i.Arguments[0].AsInt()) ? 1 : 0));
        vm.RegisterNative(c.Name, "toLowerCase", "(C)C", i => JValue.Int(char.ToLowerInvariant((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "toUpperCase", "(C)C", i => JValue.Int(char.ToUpperInvariant((char)i.Arguments[0].AsInt())));
        vm.RegisterNative(c.Name, "digit", "(CI)I", i => JValue.Int(char.IsDigit((char)i.Arguments[0].AsInt()) ? (char)i.Arguments[0].AsInt() - '0' : -1));
        vm.RegisterNative(c.Name, "forDigit", "(II)C", i => JValue.Int(i.Arguments[0].AsInt() is >= 0 and <= 9 ? '0' + i.Arguments[0].AsInt() : '\0'));
        vm.RegisterNative(c.Name, "getNumericValue", "(C)I", i =>
        {
            var ch = (char)i.Arguments[0].AsInt();
            return JValue.Int(char.IsDigit(ch) ? ch - '0' : char.IsLetter(ch) ? char.ToUpperInvariant(ch) - 'A' + 10 : -1);
        });
    }

    private static void RegisterPrintStream(JavaVm vm, JClass c)
    {
        // FIX: per-descriptor conversion (old code printed booleans as 1/0 and chars as digits)
        foreach (var d in new[] { "(Ljava/lang/String;)V", "(Ljava/lang/Object;)V", "(I)V", "(J)V", "(F)V", "(D)V", "(C)V", "(Z)V" })
        {
            string desc = d;
            vm.RegisterNative(c.Name, "print", desc, i => { vm.Print(ArgToString(vm, i, desc)); return JValue.Void; });
            vm.RegisterNative(c.Name, "println", desc, i => { vm.Print(ArgToString(vm, i, desc) + "\n"); return JValue.Void; });
        }
        vm.RegisterNative(c.Name, "println", "()V", i => { vm.Print("\n"); return JValue.Void; });
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

    private static void RegisterInteger(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is int v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is int v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "parseInt", "(Ljava/lang/String;)I", i =>
        {
            try { return JValue.Int(int.Parse(vm.StringValue(i.Arguments[0]).Trim(), CultureInfo.InvariantCulture)); }
            catch (Exception) { throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException"), -1, "For input string: \"" + vm.StringValue(i.Arguments[0]) + "\""); }
        });
        vm.RegisterNative(c.Name, "valueOf", "(I)Ljava/lang/Integer;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsInt() }));
        vm.RegisterNative(c.Name, "toHexString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(((uint)i.Arguments[0].AsInt()).ToString("x8", CultureInfo.InvariantCulture).TrimStart('0') is { Length: > 0 } s ? s : "0")));
        vm.RegisterNative(c.Name, "toBinaryString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(Convert.ToString(i.Arguments[0].AsInt(), 2))));
        vm.RegisterNative(c.Name, "toOctalString", "(I)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(Convert.ToString(i.Arguments[0].AsInt(), 8))));
        vm.SetStatic(c, "MAX_VALUE", "I", JValue.Int(int.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "I", JValue.Int(int.MinValue));
    }

    private static void RegisterLong(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(J)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsLong(); return JValue.Void; });
        vm.RegisterNative(c.Name, "longValue", "()J", i => JValue.Long(i.Receiver.AsObject()?.NativeState is long v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is long v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(J)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsLong().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "parseLong", "(Ljava/lang/String;)J", i =>
        {
            try { return JValue.Long(long.Parse(vm.StringValue(i.Arguments[0]).Trim(), CultureInfo.InvariantCulture)); }
            catch (Exception) { throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException"), -1, "For input string: \"" + vm.StringValue(i.Arguments[0]) + "\""); }
        });
        vm.RegisterNative(c.Name, "valueOf", "(J)Ljava/lang/Long;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsLong() }));
        vm.SetStatic(c, "MAX_VALUE", "J", JValue.Long(long.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "J", JValue.Long(long.MinValue));
    }

    private static void RegisterFloat(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(F)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsFloat(); return JValue.Void; });
        vm.RegisterNative(c.Name, "floatValue", "()F", i => JValue.Float(i.Receiver.AsObject()?.NativeState is float v ? v : 0f));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.FloatStr(i.Receiver.AsObject()?.NativeState is float v ? v : 0f))));
        vm.RegisterNative(c.Name, "toString", "(F)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.FloatStr(i.Arguments[0].AsFloat()))));
        vm.RegisterNative(c.Name, "valueOf", "(F)Ljava/lang/Float;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsFloat() }));
        vm.RegisterNative(c.Name, "isNaN", "(F)Z", i => JValue.Int(float.IsNaN(i.Arguments[0].AsFloat()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isInfinite", "(F)Z", i => JValue.Int(float.IsInfinity(i.Arguments[0].AsFloat()) ? 1 : 0));
        vm.SetStatic(c, "MAX_VALUE", "F", JValue.Float(3.4028235e38f));
        vm.SetStatic(c, "MIN_VALUE", "F", JValue.Float(1.4e-45f));
        vm.SetStatic(c, "POSITIVE_INFINITY", "F", JValue.Float(float.PositiveInfinity));
        vm.SetStatic(c, "NEGATIVE_INFINITY", "F", JValue.Float(float.NegativeInfinity));
        vm.SetStatic(c, "NaN", "F", JValue.Float(float.NaN));
    }

    private static void RegisterDouble(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(D)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsDouble(); return JValue.Void; });
        vm.RegisterNative(c.Name, "doubleValue", "()D", i => JValue.Double(i.Receiver.AsObject()?.NativeState is double v ? v : 0.0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.DoubleStr(i.Receiver.AsObject()?.NativeState is double v ? v : 0.0))));
        vm.RegisterNative(c.Name, "toString", "(D)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(JavaText.DoubleStr(i.Arguments[0].AsDouble()))));
        vm.RegisterNative(c.Name, "valueOf", "(D)Ljava/lang/Double;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsDouble() }));
        vm.RegisterNative(c.Name, "isNaN", "(D)Z", i => JValue.Int(double.IsNaN(i.Arguments[0].AsDouble()) ? 1 : 0));
        vm.RegisterNative(c.Name, "isInfinite", "(D)Z", i => JValue.Int(double.IsInfinity(i.Arguments[0].AsDouble()) ? 1 : 0));
        vm.SetStatic(c, "MAX_VALUE", "D", JValue.Double(double.MaxValue));
        vm.SetStatic(c, "MIN_VALUE", "D", JValue.Double(double.Epsilon));
        vm.SetStatic(c, "POSITIVE_INFINITY", "D", JValue.Double(double.PositiveInfinity));
        vm.SetStatic(c, "NEGATIVE_INFINITY", "D", JValue.Double(double.NegativeInfinity));
        vm.SetStatic(c, "NaN", "D", JValue.Double(double.NaN));
    }

    private static void RegisterBoolean(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Z)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsInt() != 0; return JValue.Void; });
        vm.RegisterNative(c.Name, "booleanValue", "()Z", i => JValue.Int(i.Receiver.AsObject()?.NativeState is true ? 1 : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Receiver.AsObject()?.NativeState is true ? "true" : "false")));
        vm.RegisterNative(c.Name, "toString", "(Z)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt() != 0 ? "true" : "false")));
        vm.RegisterNative(c.Name, "valueOf", "(Z)Ljava/lang/Boolean;", i => JValue.Ref(new JObject { Class = c, NativeState = i.Arguments[0].AsInt() != 0 }));
        vm.SetStatic(c, "TRUE", "Ljava/lang/Boolean;", JValue.Ref(new JObject { Class = c, NativeState = true }));
        vm.SetStatic(c, "FALSE", "Ljava/lang/Boolean;", JValue.Ref(new JObject { Class = c, NativeState = false }));
    }

    private static void RegisterByte(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(B)V", i => { i.Receiver.AsObject()!.NativeState = (sbyte)i.Arguments[0].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "byteValue", "()B", i => JValue.Int(i.Receiver.AsObject()?.NativeState is sbyte v ? v : 0));
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is sbyte v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is sbyte v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(B)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "parseByte", "(Ljava/lang/String;)B", i =>
        {
            try { return JValue.Int(sbyte.Parse(vm.StringValue(i.Arguments[0]).Trim(), CultureInfo.InvariantCulture)); }
            catch (Exception) { throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException"), -1, "For input string: \"" + vm.StringValue(i.Arguments[0]) + "\""); }
        });
        vm.SetStatic(c, "MAX_VALUE", "B", JValue.Int(127));
        vm.SetStatic(c, "MIN_VALUE", "B", JValue.Int(-128));
    }

    private static void RegisterShort(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(S)V", i => { i.Receiver.AsObject()!.NativeState = (short)i.Arguments[0].AsInt(); return JValue.Void; });
        vm.RegisterNative(c.Name, "shortValue", "()S", i => JValue.Int(i.Receiver.AsObject()?.NativeState is short v ? v : 0));
        vm.RegisterNative(c.Name, "intValue", "()I", i => JValue.Int(i.Receiver.AsObject()?.NativeState is short v ? v : 0));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState is short v ? v : 0).ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "toString", "(S)Ljava/lang/String;", i => JValue.Ref(vm.CreateString(i.Arguments[0].AsInt().ToString(CultureInfo.InvariantCulture))));
        vm.RegisterNative(c.Name, "parseShort", "(Ljava/lang/String;)S", i =>
        {
            try { return JValue.Int(short.Parse(vm.StringValue(i.Arguments[0]).Trim(), CultureInfo.InvariantCulture)); }
            catch (Exception) { throw new JvmException(vm.CreateExceptionObject("java.lang.NumberFormatException"), -1, "For input string: \"" + vm.StringValue(i.Arguments[0]) + "\""); }
        });
        vm.SetStatic(c, "MAX_VALUE", "S", JValue.Int(32767));
        vm.SetStatic(c, "MIN_VALUE", "S", JValue.Int(-32768));
    }

    private static void RegisterMath(JavaVm vm, JClass c)
    {
        // FIX: Math.abs(MinValue) must wrap, not throw OverflowException
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
        vm.RegisterNative(c.Name, "log", "(D)D", i => JValue.Double(Math.Log(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "exp", "(D)D", i => JValue.Double(Math.Exp(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "floor", "(D)D", i => JValue.Double(Math.Floor(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "ceil", "(D)D", i => JValue.Double(Math.Ceiling(i.Arguments[0].AsDouble())));
        vm.RegisterNative(c.Name, "round", "(F)I", i => JValue.Int((int)Math.Floor(i.Arguments[0].AsFloat() + 0.5f)));
        vm.RegisterNative(c.Name, "round", "(D)J", i => JValue.Long((long)Math.Floor(i.Arguments[0].AsDouble() + 0.5)));
        vm.RegisterNative(c.Name, "random", "()D", _ => JValue.Double(Random.Shared.NextDouble()));
        vm.RegisterNative(c.Name, "toRadians", "(D)D", i => JValue.Double(i.Arguments[0].AsDouble() * Math.PI / 180.0));
        vm.RegisterNative(c.Name, "toDegrees", "(D)D", i => JValue.Double(i.Arguments[0].AsDouble() * 180.0 / Math.PI));
    }

    private static void RegisterRandom(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new System.Random(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(J)V", i => { i.Receiver.AsObject()!.NativeState = new System.Random(i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setSeed", "(J)V", i => { i.Receiver.AsObject()!.NativeState = new System.Random(i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "nextInt", "()I", i => JValue.Int(Rand(i).Next()));
        vm.RegisterNative(c.Name, "nextInt", "(I)I", i => { int n = Math.Max(1, i.Arguments[0].AsInt()); return JValue.Int(Rand(i).Next(n)); });
        vm.RegisterNative(c.Name, "nextLong", "()J", i => JValue.Long(Rand(i).NextInt64()));
        vm.RegisterNative(c.Name, "nextDouble", "()D", i => JValue.Double(Rand(i).NextDouble()));
        vm.RegisterNative(c.Name, "nextFloat", "()F", i => JValue.Float((float)Rand(i).NextDouble()));
        vm.RegisterNative(c.Name, "nextGaussian", "()D", i =>
        {
            double u1 = 1.0 - Rand(i).NextDouble(), u2 = Rand(i).NextDouble();
            return JValue.Double(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        });
    }
    private static System.Random Rand(JavaInvocation i) => i.Receiver.AsObject()?.NativeState as System.Random ?? new System.Random();

    private static void RegisterUrl(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = ParsedUrl.Parse(vm.StringValue(i.Arguments[0])); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/net/URL;Ljava/lang/String;)V", i =>
        {
            var baseUrl = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            var spec = vm.StringValue(i.Arguments[1]);
            i.Receiver.AsObject()!.NativeState = baseUrl?.Resolve(spec) ?? ParsedUrl.Parse(spec);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
        vm.RegisterNative(c.Name, "getProtocol", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Scheme ?? "")));
        vm.RegisterNative(c.Name, "getHost", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Host ?? "")));
        vm.RegisterNative(c.Name, "getFile", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()!.NativeState as ParsedUrl)?.Path ?? "")));
    }

    private static void RegisterApplet(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i =>
        {
            var st = new JavaAppletNativeState();
            i.Receiver.AsObject()!.NativeState = st;
            st.Component.Layout = vm.Construct(vm.LoadClass("java.awt.FlowLayout"));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "init", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "start", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "stop", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "destroy", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
            return st?.Stub?.Parameters.TryGetValue(vm.StringValue(i.Arguments[0]), out var s) == true ? JValue.Ref(vm.CreateString(s)) : JValue.Ref(null);
        });
        vm.RegisterNative(c.Name, "getCodeBase", "()Ljava/net/URL;", i => JValue.Ref(StubUrl(vm, i, false)));
        vm.RegisterNative(c.Name, "getDocumentBase", "()Ljava/net/URL;", i => JValue.Ref(StubUrl(vm, i, true)));
        vm.RegisterNative(c.Name, "getAppletContext", "()Ljava/applet/AppletContext;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
            return JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AppletContext"), NativeState = st?.Stub?.Context });
        });
        vm.RegisterNative(c.Name, "isActive", "()Z", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaAppletNativeState)?.Active == true ? 1 : 0));
        vm.RegisterNative(c.Name, "resize", "(II)V", i =>
        {
            int w = Math.Max(1, i.Arguments[0].AsInt()), h = Math.Max(1, i.Arguments[1].AsInt());
            var o = i.Receiver.AsObject()!;
            if (o.NativeState is JavaAppletNativeState ap) { ap.Width = w; ap.Height = h; ap.Component.Width = w; ap.Component.Height = h; }
            else { var s = JavaComponentBridge.State(o); s.Width = w; s.Height = h; }
            vm.Repaint();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "showStatus", "(Ljava/lang/String;)V", i => { vm.Status(vm.StringValue(i.Arguments[0])); return JValue.Void; });
        // getImage variants
        foreach (var d in new[] { "(Ljava/net/URL;Ljava/lang/String;)Ljava/awt/Image;", "(Ljava/net/URL;Ljava/lang/String;Ljava/lang/Object;)Ljava/awt/Image;" })
            vm.RegisterNative(c.Name, "getImage", d, i =>
            {
                var u = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
                var name = vm.StringValue(i.Arguments[1]);
                var abs = u?.Resolve(name).ToAbsolute() ?? name;
                return JValue.Ref(vm.GraphicsFactory.LoadImage(abs));
            });
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i =>
        {
            var u = i.Arguments[0].AsObject()?.NativeState as ParsedUrl;
            return JValue.Ref(vm.GraphicsFactory.LoadImage(u?.ToAbsolute() ?? ""));
        });
        foreach (var d in new[] { "(Ljava/net/URL;)", "(Ljava/net/URL;Ljava/lang/String;)" })
            vm.RegisterNative(c.Name, d.Contains(';') && d.EndsWith("Ljava/lang/String;)") ? "getAudioClip" : "getAudioClip", d.Replace("(", "(Ljava/applet/AudioClip;"), _ => JValue.Void); // placeholder replaced below
        // (rewritten explicitly below to keep descriptors exact)
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;", _ => JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AudioClip") }));
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;Ljava/lang/String;)Ljava/applet/AudioClip;", _ => JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AudioClip") }));
        vm.RegisterNative(c.Name, "play", "(Ljava/net/URL;)V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "play", "(Ljava/net/URL;Ljava/lang/String;)V", _ => JValue.Void);
    }

    private static JObject? StubUrl(JavaVm vm, JavaInvocation i, bool doc)
    {
        var st = i.Receiver.AsObject()?.NativeState as JavaAppletNativeState;
        var p = doc ? st?.Stub?.DocumentBase : st?.Stub?.CodeBase;
        if (p == null) return null;
        return new JObject { Class = vm.LoadClass("java.net.URL"), NativeState = p };
    }

    private static void RegisterAppletContext(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "showStatus", "(Ljava/lang/String;)V", i => { (i.Receiver.AsObject()?.NativeState as JavaAppletContextState)?.Status?.Invoke(vm.StringValue(i.Arguments[0])); return JValue.Void; });
        vm.RegisterNative(c.Name, "showDocument", "(Ljava/net/URL;)V", i => { (i.Receiver.AsObject()?.NativeState as JavaAppletContextState)?.Navigate?.Invoke((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? ""); return JValue.Void; });
        vm.RegisterNative(c.Name, "showDocument", "(Ljava/net/URL;Ljava/lang/String;)V", i => { (i.Receiver.AsObject()?.NativeState as JavaAppletContextState)?.Navigate?.Invoke((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? ""); return JValue.Void; });
        vm.RegisterNative(c.Name, "getApplet", "(Ljava/lang/String;)Ljava/applet/Applet;", _ => JValue.Ref(null));
        vm.RegisterNative(c.Name, "getApplets", "()Ljava/util/Enumeration;", _ => JValue.Ref(null));
        vm.RegisterNative(c.Name, "getAudioClip", "(Ljava/net/URL;)Ljava/applet/AudioClip;", _ => JValue.Ref(new JObject { Class = vm.LoadClass("java.applet.AudioClip") }));
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i => JValue.Ref(vm.GraphicsFactory.LoadImage((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
    }

    private static void RegisterToolkit(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "getDefaultToolkit", "()Ljava/awt/Toolkit;", _ =>
        {
            if (!c.StaticFields.TryGetValue("defaultToolkit", out var tk))
            {
                tk = JValue.Ref(new JObject { Class = c });
                c.StaticFields["defaultToolkit"] = tk;
            }
            return tk;
        });
        vm.RegisterNative(c.Name, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;", i => JValue.Ref(vm.GraphicsFactory.LoadImage((i.Arguments[0].AsObject()?.NativeState as ParsedUrl)?.ToAbsolute() ?? "")));
        vm.RegisterNative(c.Name, "getImage", "(Ljava/lang/String;)Ljava/awt/Image;", i => JValue.Ref(vm.GraphicsFactory.LoadImage(vm.StringValue(i.Arguments[0]))));
        vm.RegisterNative(c.Name, "beep", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "sync", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "getScreenResolution", "()I", _ => JValue.Int(96));
        vm.RegisterNative(c.Name, "getScreenSize", "()Ljava/awt/Dimension;", _ =>
            JValue.Ref(new JObject { Class = vm.LoadClass("java.awt.Dimension"), NativeState = new JPairState(640, 480) }));
    }

    private static void RegisterMediaTracker(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;)V", i => { i.Receiver.AsObject()!.NativeState = new List<(JObject image, int id)>(); return JValue.Void; });
        vm.RegisterNative(c.Name, "addImage", "(Ljava/awt/Image;I)V", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)>;
            var img = i.Arguments[0].AsObject();
            if (list != null && img != null) list.Add((img, i.Arguments[1].AsInt()));
            return JValue.Void;
        });
        void WaitOne(JavaVm v, JObject img)
        {
            var st = img.NativeState as JavaImageState;
            if (st == null || st.Loaded) return;
            var deadline = Environment.TickCount64 + 5000;
            while (!st.Loaded && Environment.TickCount64 < deadline)
            {
                JavaGraphicsBridge.EnsureImageLoaded(v, st);
                if (st.Loaded) break;
                Thread.Sleep(25);
            }
        }
        vm.RegisterNative(c.Name, "waitForID", "(I)V", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)> ?? new List<(JObject, int)>();
            foreach (var (img, id) in list.ToArray()) if (id == i.Arguments[0].AsInt()) WaitOne(vm, img);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "waitForAll", "()V", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)> ?? new List<(JObject, int)>();
            foreach (var (img, _) in list.ToArray()) WaitOne(vm, img);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "checkID", "(I)Z", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)> ?? new List<(JObject, int)>();
            bool all = true;
            foreach (var (img, id) in list) if (id == i.Arguments[0].AsInt() && !(img.NativeState as JavaImageState)?.Loaded == true) all = false;
            return JValue.Int(all ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "checkAll", "()Z", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)> ?? new List<(JObject, int)>();
            bool all = true;
            foreach (var (img, _) in list) if (!((img.NativeState as JavaImageState)?.Loaded == true)) all = false;
            return JValue.Int(all ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "statusID", "(II)I", i =>
        {
            var list = i.Receiver.AsObject()?.NativeState as List<(JObject, int)> ?? new List<(JObject, int)>();
            bool loaded = true;
            foreach (var (img, id) in list) if (id == i.Arguments[0].AsInt() && !((img.NativeState as JavaImageState)?.Loaded == true)) loaded = false;
            return JValue.Int(loaded ? 8 : 1); // COMPLETE : LOADING
        });
        vm.SetStatic(c, "LOADING", "I", JValue.Int(1));
        vm.SetStatic(c, "ABORTED", "I", JValue.Int(2));
        vm.SetStatic(c, "ERRORED", "I", JValue.Int(4));
        vm.SetStatic(c, "COMPLETE", "I", JValue.Int(8));
    }

    private static void RegisterImage(JavaVm vm, JClass c)
    {
        // FIX: images load lazily; report -1 until the bytes actually arrive
        Func<JavaInvocation, JValue> width = i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaImageState;
            if (st == null) return JValue.Int(-1);
            JavaGraphicsBridge.EnsureImageLoaded(vm, st);
            return JValue.Int(st.Loaded ? st.Bitmap.Width : -1);
        };
        Func<JavaInvocation, JValue> height = i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaImageState;
            if (st == null) return JValue.Int(-1);
            JavaGraphicsBridge.EnsureImageLoaded(vm, st);
            return JValue.Int(st.Loaded ? st.Bitmap.Height : -1);
        };
        vm.RegisterNative(c.Name, "getWidth", "(Ljava/awt/image/ImageObserver;)I", width);
        vm.RegisterNative(c.Name, "getHeight", "(Ljava/awt/image/ImageObserver;)I", height);
        vm.RegisterNative(c.Name, "getWidth", "(Ljava/lang/Object;)I", width);
        vm.RegisterNative(c.Name, "getHeight", "(Ljava/lang/Object;)I", height);
        vm.RegisterNative(c.Name, "getGraphics", "()Ljava/awt/Graphics;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaImageState;
            if (st == null) return JValue.Ref(null);
            return JValue.Ref(vm.GraphicsFactory.CreateGraphics(Retro96.Drawing.Graphics.FromImage(st.Bitmap), st.Bitmap.Width, st.Bitmap.Height));
        });
        vm.RegisterNative(c.Name, "flush", "()V", _ => JValue.Void);
    }

    private static void RegisterColor(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(III)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(255, Math.Clamp(i.Arguments[0].AsInt(), 0, 255), Math.Clamp(i.Arguments[1].AsInt(), 0, 255), Math.Clamp(i.Arguments[2].AsInt(), 0, 255));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(I)V", i =>
        {
            int rgb = i.Arguments[0].AsInt();
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(unchecked((int)0xFF000000) | (rgb & 0x00FFFFFF));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(IIII)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = Color.FromArgb(Math.Clamp(i.Arguments[3].AsInt(), 0, 255), Math.Clamp(i.Arguments[0].AsInt(), 0, 255), Math.Clamp(i.Arguments[1].AsInt(), 0, 255), Math.Clamp(i.Arguments[2].AsInt(), 0, 255));
            return JValue.Void;
        });
        foreach (var p in new[] { ("black", Color.Black), ("white", Color.White), ("red", Color.Red), ("green", Color.Green), ("blue", Color.Blue), ("yellow", Color.Yellow), ("gray", Color.Gray), ("lightGray", Color.LightGray), ("darkGray", Color.DarkGray), ("orange", Color.Orange), ("magenta", Color.Magenta), ("cyan", Color.Cyan), ("pink", Color.Pink) })
            vm.SetStatic(c, p.Item1, "Ljava/awt/Color;", JValue.Ref(new JObject { Class = c, NativeState = p.Item2 }));
        vm.RegisterNative(c.Name, "getRed", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState is Color c1) ? c1.R : 0));
        vm.RegisterNative(c.Name, "getGreen", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState is Color c1) ? c1.G : 0));
        vm.RegisterNative(c.Name, "getBlue", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState is Color c1) ? c1.B : 0));
        vm.RegisterNative(c.Name, "getAlpha", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState is Color c1) ? c1.A : 255));
        vm.RegisterNative(c.Name, "getRGB", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState is Color c1 ? c1.ToArgb() : 0xFF000000)));
        vm.RegisterNative(c.Name, "brighter", "()Ljava/awt/Color;", i => JValue.Ref(new JObject { Class = c, NativeState = Scale((Color)(i.Receiver.AsObject()?.NativeState ?? Color.Black), 1.0 / 0.7) }));
        vm.RegisterNative(c.Name, "darker", "()Ljava/awt/Color;", i => JValue.Ref(new JObject { Class = c, NativeState = Scale((Color)(i.Receiver.AsObject()?.NativeState ?? Color.Black), 0.7) }));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i => JValue.Int(i.Receiver.AsObject()?.NativeState is Color a && i.Arguments[0].AsObject()?.NativeState is Color b && a.ToArgb() == b.ToArgb() ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState is Color c1 ? c1.ToArgb() : 0)));
    }
    private static Color Scale(Color c, double f) =>
        Color.FromArgb(c.A, (int)Math.Clamp(c.R * f, 0, 255), (int)Math.Clamp(c.G * f, 0, 255), (int)Math.Clamp(c.B * f, 0, 255));

    private static void RegisterFont(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;II)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaFontState { Family = vm.StringValue(i.Arguments[0]), Style = i.Arguments[1].AsInt(), Size = i.Arguments[2].AsInt() };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Family ?? "Dialog")));
        vm.RegisterNative(c.Name, "getFamily", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Family ?? "Dialog")));
        vm.RegisterNative(c.Name, "getFontName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Family ?? "Dialog")));
        vm.RegisterNative(c.Name, "getSize", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Size ?? 12));
        vm.RegisterNative(c.Name, "getStyle", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Style ?? 0));
        // FIX: parenthesization (old code made isBold true for italic-only fonts)
        vm.RegisterNative(c.Name, "isBold", "()Z", i => JValue.Int((((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Style ?? 0) & 1) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isItalic", "()Z", i => JValue.Int((((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Style ?? 0) & 2) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isPlain", "()Z", i => JValue.Int(((i.Receiver.AsObject()?.NativeState as JavaFontState)?.Style ?? 0) == 0 ? 1 : 0));
        vm.SetStatic(c, "PLAIN", "I", JValue.Int(0));
        vm.SetStatic(c, "BOLD", "I", JValue.Int(1));
        vm.SetStatic(c, "ITALIC", "I", JValue.Int(2));
    }

    private static void RegisterFontMetrics(JavaVm vm, JClass c)
    {
        Func<JavaInvocation, float> lineHeight = i => (i.Receiver.AsObject()?.NativeState as JavaFontMetricsState)?.Font?.GetHeight() ?? 14f;
        vm.RegisterNative(c.Name, "getAscent", "()I", i => JValue.Int((int)Math.Round(lineHeight(i) * 0.75f)));
        vm.RegisterNative(c.Name, "getDescent", "()I", i => JValue.Int(Math.Max(1, (int)Math.Round(lineHeight(i) * 0.25f))));
        vm.RegisterNative(c.Name, "getHeight", "()I", i => JValue.Int((int)Math.Ceiling(lineHeight(i))));
        vm.RegisterNative(c.Name, "stringWidth", "(Ljava/lang/String;)I", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaFontMetricsState;
            var text = vm.StringValue(i.Arguments[0]);
            return JValue.Int(st?.Font == null ? text.Length * 8 : (int)Math.Round(st.Font.MeasureText(text)));
        });
        vm.RegisterNative(c.Name, "charsWidth", "([CII)I", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaFontMetricsState;
            var a = i.Arguments[0].AsArray();
            var text = a == null ? "" : new string(a.Elements.Skip(i.Arguments[1].AsInt()).Take(i.Arguments[2].AsInt()).Select(v => (char)v.AsInt()).ToArray());
            return JValue.Int(st?.Font == null ? text.Length * 8 : (int)Math.Round(st.Font.MeasureText(text)));
        });
        vm.RegisterNative(c.Name, "charWidth", "(C)I", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaFontMetricsState;
            var text = ((char)i.Arguments[0].AsInt()).ToString();
            return JValue.Int(st?.Font == null ? 8 : (int)Math.Round(st.Font.MeasureText(text)));
        });
    }

    private static void RegisterThread(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Runnable;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Target = i.Arguments[0].AsObject() }; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Name = vm.StringValue(i.Arguments[0]) }; return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Runnable;Ljava/lang/String;)V", i => { i.Receiver.AsObject()!.NativeState = new JavaThreadNativeState { Target = i.Arguments[0].AsObject(), Name = vm.StringValue(i.Arguments[1]) }; return JValue.Void; });
        vm.RegisterNative(c.Name, "start", "()V", i =>
        {
            var o = i.Receiver.AsObject()!;
            var st = (JavaThreadNativeState)(o.NativeState ??= new JavaThreadNativeState());
            if (st.Thread != null) return JValue.Void;
            st.Thread = new Thread(() =>
            {
                CurrentThreadObject = o;
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
            st.Thread.Start();
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "run", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "sleep", "(J)V", i =>
        {
            try { Thread.Sleep(Math.Max(0, (int)Math.Min(i.Arguments[0].AsLong(), int.MaxValue))); }
            catch (ThreadInterruptedException) { throw new JvmException(vm.CreateExceptionObject("java.lang.InterruptedException"), -1); }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "sleep", "(JI)V", i =>
        {
            try { Thread.Sleep(Math.Max(0, (int)Math.Min(i.Arguments[0].AsLong(), int.MaxValue))); }
            catch (ThreadInterruptedException) { throw new JvmException(vm.CreateExceptionObject("java.lang.InterruptedException"), -1); }
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "stop", "()V", i => { if (i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s) s.Alive = false; return JValue.Void; });
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
        vm.RegisterNative(c.Name, "join", "(J)V", i => { (i.Receiver.AsObject()?.NativeState as JavaThreadNativeState)?.Thread?.Join(Math.Max(0, (int)Math.Min(i.Arguments[0].AsLong(), int.MaxValue))); return JValue.Void; });
        vm.RegisterNative(c.Name, "setName", "(Ljava/lang/String;)V", i => { if (i.Receiver.AsObject()?.NativeState is JavaThreadNativeState s) s.Name = vm.StringValue(i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "getName", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as JavaThreadNativeState)?.Name ?? "Thread-" + RuntimeHelpers.GetHashCode(i.Receiver.AsObject()!).ToString("x", CultureInfo.InvariantCulture))));
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
}

/// <summary>Java-authentic float/double text rendering ("1.0" not "1").</summary>
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

public sealed class JavaThreadNativeState { public Thread? Thread; public JObject? Target; public bool Alive; public bool Interrupted; public string Name = ""; }
public sealed class JavaAppletNativeState
{
    public JavaAppletStubState? Stub; public bool Active; public int Width = 300; public int Height = 200;
    public readonly List<JObject> Listeners = new();
    public JavaComponentState Component = new();
}
public sealed class JavaFontState { public string Family = "Dialog"; public int Style; public int Size = 12; }
public sealed class JavaFontMetricsState { public Font? Font; }
public enum JavaLayoutKind { Flow, Border, Grid }
public sealed class JavaLayoutState
{
    public const int Left = 0;
    public JavaLayoutKind Kind;
    public int Alignment, HGap, VGap, Rows, Cols;
    public JavaLayoutState(JavaLayoutKind kind, int alignment, int hGap, int vGap, int rows, int cols)
    { Kind = kind; Alignment = alignment; HGap = hGap; VGap = vGap; Rows = rows; Cols = cols; }
}

public sealed class JavaComponentState
{
    public int X, Y, Width = 100, Height = 30;
    public bool Visible = true, Enabled = true;
    public Color Background = Color.LightGray, Foreground = Color.Black;
    public Font? Font;
    public JObject? Parent;
    public readonly List<JObject> Children = new();
    public JObject? Layout;
    public string? LayoutConstraint;
    public string Text = "";
    public bool Checked;
    public readonly List<string> Items = new();
    public int Selected = -1;
    public int ScrollValue;
    public readonly List<JObject> MouseListeners = new();
    public readonly List<JObject> MouseMotionListeners = new();
    public readonly List<JObject> KeyListeners = new();
    public readonly List<JObject> ActionListeners = new();
    public readonly List<JObject> ItemListeners = new();
}