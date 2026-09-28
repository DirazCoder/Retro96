namespace Retro96.Engine.Java;

// Declaration tables for the builtin classes: which methods, fields and
// static constants each java.* class exposes. Kept apart from the registry
// (BuiltinJavaLibrary.cs) so both stay reviewable.
internal static class BuiltinJavaDeclarations
{
    private static void Method(JClass c, string name, string desc, ushort flags = 0x0100) =>
        c.Methods[(name, desc)] = new JMethod { Name = name, Descriptor = desc, AccessFlags = flags, DeclaringClass = c };
    private static void StaticMethod(JClass c, string name, string desc) => Method(c, name, desc, 0x0108);
    private static void Field(JClass c, string name, string desc, ushort flags = 0) =>
        c.Fields[(name, desc)] = new JField { Name = name, Descriptor = desc, AccessFlags = flags };
    private static void StaticField(JClass c, string name, string desc) => Field(c, name, desc, 0x0008);

    private static void DeclareException(JClass c)
    {
        Method(c, "<init>", "()V", 0x0101);
        Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
        Method(c, "getMessage", "()Ljava/lang/String;");
        Method(c, "getLocalizedMessage", "()Ljava/lang/String;");
        Method(c, "toString", "()Ljava/lang/String;");
    }

    internal static void Populate(JavaVm vm, JClass c)
    {
        if (BuiltinJavaLibrary.InterfaceNames.Contains(c.Name))
        {
            PopulateInterface(vm, c);
            return;
        }
        switch (c.Name)
        {
            case "java.lang.Object":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;"); Method(c, "getClass", "()Ljava/lang/Class;");
                Method(c, "clone", "()Ljava/lang/Object;");
                Method(c, "wait", "()V"); Method(c, "wait", "(J)V"); Method(c, "wait", "(JI)V");
                Method(c, "notify", "()V"); Method(c, "notifyAll", "()V");
                break;
            case "java.lang.Throwable":
                DeclareException(c);
                Method(c, "printStackTrace", "()V"); Method(c, "printStackTrace", "(Ljava/io/PrintStream;)V");
                Method(c, "fillInStackTrace", "()Ljava/lang/Throwable;");
                break;
            case "java.lang.Class":
                Method(c, "getName", "()Ljava/lang/String;"); Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "getSuperclass", "()Ljava/lang/Class;"); Method(c, "isInstance", "(Ljava/lang/Object;)Z");
                Method(c, "isInterface", "()Z"); Method(c, "isAssignableFrom", "(Ljava/lang/Class;)Z");
                break;
            case "java.lang.String":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "<init>", "([C)V", 0x0101); Method(c, "<init>", "([CII)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/StringBuffer;)V", 0x0101);
                Method(c, "length", "()I"); Method(c, "charAt", "(I)C");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "equalsIgnoreCase", "(Ljava/lang/String;)Z");
                Method(c, "hashCode", "()I"); Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "substring", "(I)Ljava/lang/String;"); Method(c, "substring", "(II)Ljava/lang/String;");
                Method(c, "indexOf", "(I)I"); Method(c, "indexOf", "(II)I");
                Method(c, "indexOf", "(Ljava/lang/String;)I"); Method(c, "indexOf", "(Ljava/lang/String;I)I");
                Method(c, "lastIndexOf", "(I)I"); Method(c, "lastIndexOf", "(Ljava/lang/String;)I");
                Method(c, "startsWith", "(Ljava/lang/String;)Z"); Method(c, "startsWith", "(Ljava/lang/String;I)Z");
                Method(c, "endsWith", "(Ljava/lang/String;)Z"); Method(c, "contains", "(Ljava/lang/CharSequence;)Z");
                Method(c, "trim", "()Ljava/lang/String;"); Method(c, "concat", "(Ljava/lang/String;)Ljava/lang/String;");
                Method(c, "compareTo", "(Ljava/lang/String;)I"); Method(c, "compareToIgnoreCase", "(Ljava/lang/String;)I");
                Method(c, "getBytes", "()[B"); Method(c, "toCharArray", "()[C");
                Method(c, "replace", "(CC)Ljava/lang/String;");
                Method(c, "toLowerCase", "()Ljava/lang/String;"); Method(c, "toUpperCase", "()Ljava/lang/String;");
                Method(c, "intern", "()Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(Ljava/lang/Object;)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(I)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(J)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(C)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(F)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(D)Ljava/lang/String;"); StaticMethod(c, "valueOf", "(Z)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "([C)Ljava/lang/String;");
                break;
            case "java.lang.StringBuffer":
            case "java.lang.StringBuilder":
            {
                // Fluent methods return the concrete builder type, so the
                // descriptors must match the class being declared.
                string self = "Ljava/lang/" + (c.Name == "java.lang.StringBuilder" ? "StringBuilder" : "StringBuffer") + ";";
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                foreach (var t in new[] { "(Ljava/lang/String;)", "(Ljava/lang/Object;)", "([C)", "(I)", "(J)", "(F)", "(D)", "(Z)", "(C)", "([CII)" })
                    Method(c, "append", t + self);
                Method(c, "toString", "()Ljava/lang/String;"); Method(c, "length", "()I");
                Method(c, "charAt", "(I)C"); Method(c, "setCharAt", "(IC)V");
                Method(c, "setLength", "(I)V"); Method(c, "reverse", "()" + self);
                Method(c, "deleteCharAt", "(I)" + self); Method(c, "delete", "(II)" + self);
                Method(c, "insert", "(ILjava/lang/String;)" + self);
                Method(c, "insert", "(II)" + self);
                Method(c, "insert", "(IC)" + self);
                Method(c, "insert", "(ILjava/lang/Object;)" + self);
                Method(c, "capacity", "()I"); Method(c, "ensureCapacity", "(I)V");
                break;
            }
            case "java.lang.Runnable": Method(c, "run", "()V"); break;
            case "java.lang.Character":
                Method(c, "<init>", "(C)V", 0x0101); Method(c, "charValue", "()C");
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
                StaticField(c, "out", "Ljava/io/PrintStream;"); StaticField(c, "err", "Ljava/io/PrintStream;");
                break;
            case "java.lang.PrintStream":
            case "java.io.PrintStream":
                foreach (var d in new[] { "(Ljava/lang/String;)V", "(Ljava/lang/Object;)V", "(I)V", "(J)V", "(F)V", "(D)V", "(C)V", "(Z)V" })
                    Method(c, "print", d);
                foreach (var d in new[] { "()V", "(Ljava/lang/String;)V", "(Ljava/lang/Object;)V", "(I)V", "(J)V", "(F)V", "(D)V", "(C)V", "(Z)V" })
                    Method(c, "println", d);
                Method(c, "flush", "()V");
                break;
            case "java.lang.Thread":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(Ljava/lang/Runnable;)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101); Method(c, "<init>", "(Ljava/lang/Runnable;Ljava/lang/String;)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/ThreadGroup;Ljava/lang/Runnable;)V", 0x0101);
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
                StaticField(c, "MIN_PRIORITY", "I"); StaticField(c, "NORM_PRIORITY", "I"); StaticField(c, "MAX_PRIORITY", "I");
                break;
            case "java.lang.Integer":
                Method(c, "<init>", "(I)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "intValue", "()I"); Method(c, "longValue", "()J");
                Method(c, "floatValue", "()F"); Method(c, "doubleValue", "()D");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(I)Ljava/lang/String;");
                StaticMethod(c, "toString", "(II)Ljava/lang/String;");
                StaticMethod(c, "parseInt", "(Ljava/lang/String;)I");
                StaticMethod(c, "parseInt", "(Ljava/lang/String;I)I");
                StaticMethod(c, "valueOf", "(I)Ljava/lang/Integer;");
                StaticMethod(c, "valueOf", "(Ljava/lang/String;)Ljava/lang/Integer;");
                StaticMethod(c, "toHexString", "(I)Ljava/lang/String;");
                StaticMethod(c, "toBinaryString", "(I)Ljava/lang/String;");
                StaticMethod(c, "toOctalString", "(I)Ljava/lang/String;");
                StaticField(c, "MAX_VALUE", "I"); StaticField(c, "MIN_VALUE", "I"); StaticField(c, "TYPE", "Ljava/lang/Class;");
                break;
            case "java.lang.Long":
                Method(c, "<init>", "(J)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "intValue", "()I"); Method(c, "longValue", "()J");
                Method(c, "floatValue", "()F"); Method(c, "doubleValue", "()D");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(J)Ljava/lang/String;"); StaticMethod(c, "toString", "(JI)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(J)Ljava/lang/Long;");
                StaticMethod(c, "valueOf", "(Ljava/lang/String;)Ljava/lang/Long;");
                StaticMethod(c, "parseLong", "(Ljava/lang/String;)J");
                StaticMethod(c, "parseLong", "(Ljava/lang/String;I)J");
                StaticMethod(c, "toHexString", "(J)Ljava/lang/String;");
                StaticMethod(c, "toBinaryString", "(J)Ljava/lang/String;");
                StaticField(c, "MAX_VALUE", "J"); StaticField(c, "MIN_VALUE", "J"); StaticField(c, "TYPE", "Ljava/lang/Class;");
                break;
            case "java.lang.Float":
                Method(c, "<init>", "(F)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "intValue", "()I"); Method(c, "longValue", "()J");
                Method(c, "floatValue", "()F"); Method(c, "doubleValue", "()D");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(F)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(F)Ljava/lang/Float;");
                StaticMethod(c, "valueOf", "(Ljava/lang/String;)Ljava/lang/Float;");
                StaticMethod(c, "parseFloat", "(Ljava/lang/String;)F");
                StaticMethod(c, "isNaN", "(F)Z"); StaticMethod(c, "isInfinite", "(F)Z");
                StaticMethod(c, "floatToIntBits", "(F)I"); StaticMethod(c, "intBitsToFloat", "(I)F");
                StaticField(c, "MAX_VALUE", "F"); StaticField(c, "MIN_VALUE", "F");
                StaticField(c, "POSITIVE_INFINITY", "F"); StaticField(c, "NEGATIVE_INFINITY", "F");
                StaticField(c, "NaN", "F"); StaticField(c, "TYPE", "Ljava/lang/Class;");
                break;
            case "java.lang.Double":
                Method(c, "<init>", "(D)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "intValue", "()I"); Method(c, "longValue", "()J");
                Method(c, "floatValue", "()F"); Method(c, "doubleValue", "()D");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(D)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(D)Ljava/lang/Double;");
                StaticMethod(c, "valueOf", "(Ljava/lang/String;)Ljava/lang/Double;");
                StaticMethod(c, "parseDouble", "(Ljava/lang/String;)D");
                StaticMethod(c, "isNaN", "(D)Z"); StaticMethod(c, "isInfinite", "(D)Z");
                StaticMethod(c, "doubleToLongBits", "(D)J"); StaticMethod(c, "longBitsToDouble", "(J)D");
                StaticField(c, "MAX_VALUE", "D"); StaticField(c, "MIN_VALUE", "D");
                StaticField(c, "POSITIVE_INFINITY", "D"); StaticField(c, "NEGATIVE_INFINITY", "D");
                StaticField(c, "NaN", "D"); StaticField(c, "TYPE", "Ljava/lang/Class;");
                break;
            case "java.lang.Boolean":
                Method(c, "<init>", "(Z)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "booleanValue", "()Z"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(Z)Ljava/lang/String;");
                StaticMethod(c, "valueOf", "(Z)Ljava/lang/Boolean;");
                StaticMethod(c, "valueOf", "(Ljava/lang/String;)Ljava/lang/Boolean;");
                StaticField(c, "TRUE", "Ljava/lang/Boolean;"); StaticField(c, "FALSE", "Ljava/lang/Boolean;");
                StaticField(c, "TYPE", "Ljava/lang/Class;");
                break;
            case "java.lang.Byte":
                Method(c, "<init>", "(B)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "byteValue", "()B"); Method(c, "intValue", "()I"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(B)Ljava/lang/String;"); StaticMethod(c, "parseByte", "(Ljava/lang/String;)B");
                StaticMethod(c, "valueOf", "(B)Ljava/lang/Byte;");
                StaticField(c, "MAX_VALUE", "B"); StaticField(c, "MIN_VALUE", "B");
                break;
            case "java.lang.Short":
                Method(c, "<init>", "(S)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "shortValue", "()S"); Method(c, "intValue", "()I"); Method(c, "toString", "()Ljava/lang/String;");
                StaticMethod(c, "toString", "(S)Ljava/lang/String;"); StaticMethod(c, "parseShort", "(Ljava/lang/String;)S");
                StaticMethod(c, "valueOf", "(S)Ljava/lang/Short;");
                StaticField(c, "MAX_VALUE", "S"); StaticField(c, "MIN_VALUE", "S");
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
                StaticMethod(c, "floor", "(D)D"); StaticMethod(c, "ceil", "(D)D"); StaticMethod(c, "rint", "(D)D");
                StaticMethod(c, "round", "(F)I"); StaticMethod(c, "round", "(D)J");
                StaticMethod(c, "random", "()D");
                StaticMethod(c, "toRadians", "(D)D"); StaticMethod(c, "toDegrees", "(D)D");
                StaticMethod(c, "IEEEremainder", "(DD)D");
                StaticField(c, "PI", "D"); StaticField(c, "E", "D");
                break;
            case "java.util.Random":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(J)V", 0x0101);
                Method(c, "nextInt", "()I"); Method(c, "nextInt", "(I)I");
                Method(c, "nextLong", "()J"); Method(c, "nextDouble", "()D");
                Method(c, "nextFloat", "()F"); Method(c, "nextBoolean", "()Z");
                Method(c, "nextGaussian", "()D"); Method(c, "setSeed", "(J)V");
                break;
            case "java.util.Date":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(J)V", 0x0101);
                Method(c, "getTime", "()J"); Method(c, "setTime", "(J)V");
                Method(c, "before", "(Ljava/util/Date;)Z"); Method(c, "after", "(Ljava/util/Date;)Z");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "getYear", "()I"); Method(c, "getMonth", "()I"); Method(c, "getDay", "()I");
                Method(c, "getDate", "()I"); Method(c, "getHours", "()I"); Method(c, "getMinutes", "()I");
                Method(c, "getSeconds", "()I");
                break;
            case "java.util.Vector":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101); Method(c, "<init>", "(II)V", 0x0101);
                Method(c, "addElement", "(Ljava/lang/Object;)V"); Method(c, "add", "(Ljava/lang/Object;)Z");
                Method(c, "elementAt", "(I)Ljava/lang/Object;"); Method(c, "get", "(I)Ljava/lang/Object;");
                Method(c, "firstElement", "()Ljava/lang/Object;"); Method(c, "lastElement", "()Ljava/lang/Object;");
                Method(c, "setElementAt", "(Ljava/lang/Object;I)V"); Method(c, "set", "(ILjava/lang/Object;)Ljava/lang/Object;");
                Method(c, "insertElementAt", "(Ljava/lang/Object;I)V");
                Method(c, "removeElement", "(Ljava/lang/Object;)Z"); Method(c, "remove", "(I)Ljava/lang/Object;");
                Method(c, "removeElementAt", "(I)V"); Method(c, "removeAllElements", "()V"); Method(c, "clear", "()V");
                Method(c, "size", "()I"); Method(c, "isEmpty", "()Z");
                Method(c, "contains", "(Ljava/lang/Object;)Z");
                Method(c, "indexOf", "(Ljava/lang/Object;)I"); Method(c, "indexOf", "(Ljava/lang/Object;I)I");
                Method(c, "elements", "()Ljava/util/Enumeration;"); Method(c, "copyInto", "([Ljava/lang/Object;)V");
                Method(c, "toArray", "()[Ljava/lang/Object;");
                Method(c, "capacity", "()I"); Method(c, "ensureCapacity", "(I)V"); Method(c, "setSize", "(I)V");
                Method(c, "toString", "()Ljava/lang/String;");
                break;
            case "java.util.Stack":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "push", "(Ljava/lang/Object;)Ljava/lang/Object;");
                Method(c, "pop", "()Ljava/lang/Object;"); Method(c, "peek", "()Ljava/lang/Object;");
                Method(c, "empty", "()Z"); Method(c, "search", "(Ljava/lang/Object;)I");
                break;
            case "java.util.Hashtable":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101);
                Method(c, "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;");
                Method(c, "get", "(Ljava/lang/Object;)Ljava/lang/Object;");
                Method(c, "remove", "(Ljava/lang/Object;)Ljava/lang/Object;");
                Method(c, "containsKey", "(Ljava/lang/Object;)Z"); Method(c, "contains", "(Ljava/lang/Object;)Z");
                Method(c, "containsValue", "(Ljava/lang/Object;)Z");
                Method(c, "size", "()I"); Method(c, "isEmpty", "()Z"); Method(c, "clear", "()V");
                Method(c, "keys", "()Ljava/util/Enumeration;"); Method(c, "elements", "()Ljava/util/Enumeration;");
                break;
            case "java.net.URL":
                Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "<init>", "(Ljava/net/URL;Ljava/lang/String;)V", 0x0101);
                Method(c, "toString", "()Ljava/lang/String;"); Method(c, "toExternalForm", "()Ljava/lang/String;");
                Method(c, "getProtocol", "()Ljava/lang/String;"); Method(c, "getHost", "()Ljava/lang/String;");
                Method(c, "getFile", "()Ljava/lang/String;"); Method(c, "getRef", "()Ljava/lang/String;");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                break;
            case "java.applet.Applet":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "init", "()V"); Method(c, "start", "()V"); Method(c, "stop", "()V"); Method(c, "destroy", "()V");
                Method(c, "getParameter", "(Ljava/lang/String;)Ljava/lang/String;");
                Method(c, "getParameterInfo", "()[[Ljava/lang/String;");
                Method(c, "getAppletInfo", "()Ljava/lang/String;");
                Method(c, "getCodeBase", "()Ljava/net/URL;"); Method(c, "getDocumentBase", "()Ljava/net/URL;");
                Method(c, "getAppletContext", "()Ljava/applet/AppletContext;");
                Method(c, "setStub", "(Ljava/applet/AppletStub;)V"); Method(c, "getStub", "()Ljava/applet/AppletStub;");
                Method(c, "isActive", "()Z"); Method(c, "resize", "(II)V"); Method(c, "resize", "(Ljava/awt/Dimension;)V");
                Method(c, "showStatus", "(Ljava/lang/String;)V");
                Method(c, "getImage", "(Ljava/net/URL;)Ljava/awt/Image;");
                Method(c, "getImage", "(Ljava/net/URL;Ljava/lang/String;)Ljava/awt/Image;");
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
                Method(c, "createImage", "(Ljava/awt/image/ImageProducer;)Ljava/awt/Image;");
                Method(c, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;");
                Method(c, "beep", "()V"); Method(c, "sync", "()V");
                Method(c, "getScreenResolution", "()I"); Method(c, "getScreenSize", "()Ljava/awt/Dimension;");
                break;
            case "java.awt.MediaTracker":
                Method(c, "<init>", "(Ljava/awt/Component;)V", 0x0101);
                Method(c, "addImage", "(Ljava/awt/Image;I)V");
                Method(c, "waitForID", "(I)V"); Method(c, "waitForAll", "()V");
                Method(c, "checkID", "(I)Z"); Method(c, "checkID", "(IZ)Z");
                Method(c, "checkAll", "()Z"); Method(c, "checkAll", "(Z)Z");
                Method(c, "isErrorAny", "()Z"); Method(c, "isErrorID", "(I)Z");
                Method(c, "statusID", "(IZ)I"); Method(c, "statusAll", "(Z)I");
                StaticField(c, "LOADING", "I"); StaticField(c, "ABORTED", "I");
                StaticField(c, "ERRORED", "I"); StaticField(c, "COMPLETE", "I");
                break;
            case "java.awt.Graphics": RegisterGraphics(c); break;
            case "java.awt.Cursor":
                Method(c, "<init>", "(I)V", 0x0101);
                StaticField(c, "DEFAULT_CURSOR", "I"); StaticField(c, "CROSSHAIR_CURSOR", "I");
                StaticField(c, "TEXT_CURSOR", "I"); StaticField(c, "WAIT_CURSOR", "I");
                StaticField(c, "HAND_CURSOR", "I"); StaticField(c, "MOVE_CURSOR", "I");
                break;
            case "java.awt.Image":
                Method(c, "getWidth", "(Ljava/awt/image/ImageObserver;)I"); Method(c, "getHeight", "(Ljava/awt/image/ImageObserver;)I");
                Method(c, "getWidth", "(Ljava/lang/Object;)I"); Method(c, "getHeight", "(Ljava/lang/Object;)I");
                Method(c, "getSource", "()Ljava/awt/image/ImageProducer;");
                Method(c, "getGraphics", "()Ljava/awt/Graphics;"); Method(c, "flush", "()V");
                break;
            case "java.awt.Color":
                Method(c, "<init>", "(III)V", 0x0101); Method(c, "<init>", "(IIII)V", 0x0101);
                Method(c, "<init>", "(IIIIZ)V", 0x0101); Method(c, "<init>", "(I)V", 0x0101);
                Method(c, "getRed", "()I"); Method(c, "getGreen", "()I"); Method(c, "getBlue", "()I");
                Method(c, "getAlpha", "()I"); Method(c, "getRGB", "()I");
                Method(c, "brighter", "()Ljava/awt/Color;"); Method(c, "darker", "()Ljava/awt/Color;");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                foreach (var n in new[] { "black", "white", "red", "green", "blue", "yellow", "gray", "lightGray", "darkGray", "orange", "magenta", "cyan", "pink",
                                          "BLACK", "WHITE", "RED", "GREEN", "BLUE", "YELLOW", "GRAY", "LIGHT_GRAY", "DARK_GRAY", "ORANGE", "MAGENTA", "CYAN", "PINK" })
                    StaticField(c, n, "Ljava/awt/Color;");
                break;
            case "java.awt.Font":
                Method(c, "<init>", "(Ljava/lang/String;II)V", 0x0101);
                Method(c, "getName", "()Ljava/lang/String;"); Method(c, "getFamily", "()Ljava/lang/String;");
                Method(c, "getFontName", "()Ljava/lang/String;");
                Method(c, "getSize", "()I"); Method(c, "getStyle", "()I");
                Method(c, "isBold", "()Z"); Method(c, "isItalic", "()Z"); Method(c, "isPlain", "()Z");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                StaticField(c, "PLAIN", "I"); StaticField(c, "BOLD", "I"); StaticField(c, "ITALIC", "I");
                break;
            case "java.awt.FontMetrics":
                Method(c, "stringWidth", "(Ljava/lang/String;)I"); Method(c, "charsWidth", "([CII)I");
                Method(c, "bytesWidth", "([BII)I");
                Method(c, "charWidth", "(C)I"); Method(c, "charWidth", "(I)I");
                Method(c, "getWidths", "()[I");
                Method(c, "getHeight", "()I"); Method(c, "getAscent", "()I"); Method(c, "getDescent", "()I");
                Method(c, "getLeading", "()I"); Method(c, "getMaxAscent", "()I"); Method(c, "getMaxDescent", "()I");
                break;
            case "java.awt.Polygon":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "([I[II)V", 0x0101);
                Field(c, "xpoints", "[I"); Field(c, "ypoints", "[I"); Field(c, "npoints", "I");
                Method(c, "addPoint", "(II)V");
                Method(c, "contains", "(II)Z"); Method(c, "inside", "(II)Z");
                Method(c, "getBoundingBox", "()Ljava/awt/Rectangle;"); Method(c, "getBounds", "()Ljava/awt/Rectangle;");
                Method(c, "translate", "(II)V");
                break;
            case "java.awt.CheckboxGroup":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "getSelectedCheckbox", "()Ljava/awt/Checkbox;");
                Method(c, "setSelectedCheckbox", "(Ljava/awt/Checkbox;)V");
                break;
            case "java.awt.Panel": Method(c, "<init>", "()V", 0x0101); break;
            case "java.awt.Canvas": Method(c, "<init>", "()V", 0x0101); break;
            case "java.awt.Component":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "paint", "(Ljava/awt/Graphics;)V"); Method(c, "update", "(Ljava/awt/Graphics;)V");
                Method(c, "repaint", "()V"); Method(c, "repaint", "(J)V");
                Method(c, "repaint", "(IIII)V"); Method(c, "repaint", "(JIIII)V");
                Method(c, "getGraphics", "()Ljava/awt/Graphics;");
                Method(c, "createImage", "(II)Ljava/awt/Image;");
                Method(c, "prepareImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z");
                Method(c, "checkImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)I");
                Method(c, "imageUpdate", "(Ljava/awt/Image;IIII)Z");
                Method(c, "setBackground", "(Ljava/awt/Color;)V"); Method(c, "getBackground", "()Ljava/awt/Color;");
                Method(c, "setForeground", "(Ljava/awt/Color;)V"); Method(c, "getForeground", "()Ljava/awt/Color;");
                Method(c, "setFont", "(Ljava/awt/Font;)V"); Method(c, "getFont", "()Ljava/awt/Font;");
                Method(c, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;");
                Method(c, "getFontMetrics", "()Ljava/awt/FontMetrics;");
                Method(c, "setVisible", "(Z)V"); Method(c, "isVisible", "()Z");
                Method(c, "show", "()V"); Method(c, "hide", "()V");
                Method(c, "enable", "()V"); Method(c, "disable", "()V");
                Method(c, "setEnabled", "(Z)V"); Method(c, "isEnabled", "()Z");
                Method(c, "setSize", "(II)V"); Method(c, "resize", "(II)V");
                Method(c, "setSize", "(Ljava/awt/Dimension;)V"); Method(c, "resize", "(Ljava/awt/Dimension;)V");
                Method(c, "getSize", "()Ljava/awt/Dimension;"); Method(c, "size", "()Ljava/awt/Dimension;");
                Method(c, "preferredSize", "()Ljava/awt/Dimension;"); Method(c, "minimumSize", "()Ljava/awt/Dimension;");
                Method(c, "getWidth", "()I"); Method(c, "getHeight", "()I");
                Method(c, "setBounds", "(IIII)V"); Method(c, "getBounds", "()Ljava/awt/Rectangle;");
                Method(c, "bounds", "()Ljava/awt/Rectangle;");
                Method(c, "move", "(II)V"); Method(c, "setLocation", "(II)V"); Method(c, "setLocation", "(Ljava/awt/Point;)V");
                Method(c, "location", "()Ljava/awt/Point;"); Method(c, "getLocation", "()Ljava/awt/Point;");
                Method(c, "contains", "(II)Z"); Method(c, "inside", "(II)Z");
                Method(c, "getParent", "()Ljava/awt/Container;");
                Method(c, "setCursor", "(Ljava/awt/Cursor;)V"); Method(c, "getCursor", "()Ljava/awt/Cursor;");
                Method(c, "requestFocus", "()V"); Method(c, "hasFocus", "()Z");
                Method(c, "invalidate", "()V"); Method(c, "validate", "()V");
                Method(c, "getToolkit", "()Ljava/awt/Toolkit;");
                Method(c, "getName", "()Ljava/lang/String;"); Method(c, "setName", "(Ljava/lang/String;)V");
                Method(c, "toString", "()Ljava/lang/String;");
                Method(c, "handleEvent", "(Ljava/awt/Event;)Z");
                Method(c, "postEvent", "(Ljava/awt/Event;)Z");
                Method(c, "action", "(Ljava/awt/Event;Ljava/lang/Object;)Z");
                Method(c, "gotFocus", "(Ljava/awt/Event;Ljava/lang/Object;)Z");
                Method(c, "lostFocus", "(Ljava/awt/Event;Ljava/lang/Object;)Z");
                foreach (var n in new[] { "mouseDown", "mouseUp", "mouseDrag", "mouseMove", "mouseEnter", "mouseExit" })
                    Method(c, n, "(Ljava/awt/Event;II)Z");
                foreach (var n in new[] { "keyDown", "keyUp" })
                    Method(c, n, "(Ljava/awt/Event;I)Z");
                Method(c, "addMouseListener", "(Ljava/awt/event/MouseListener;)V"); Method(c, "removeMouseListener", "(Ljava/awt/event/MouseListener;)V");
                Method(c, "addMouseMotionListener", "(Ljava/awt/event/MouseMotionListener;)V"); Method(c, "removeMouseMotionListener", "(Ljava/awt/event/MouseMotionListener;)V");
                Method(c, "addKeyListener", "(Ljava/awt/event/KeyListener;)V"); Method(c, "removeKeyListener", "(Ljava/awt/event/KeyListener;)V");
                Method(c, "addFocusListener", "(Ljava/awt/event/FocusListener;)V"); Method(c, "removeFocusListener", "(Ljava/awt/event/FocusListener;)V");
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
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "getLabel", "()Ljava/lang/String;"); Method(c, "setLabel", "(Ljava/lang/String;)V");
                break;
            case "java.awt.Label":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/String;I)V", 0x0101);
                Method(c, "getText", "()Ljava/lang/String;"); Method(c, "setText", "(Ljava/lang/String;)V");
                Method(c, "setAlignment", "(I)V"); Method(c, "getAlignment", "()I");
                StaticField(c, "LEFT", "I"); StaticField(c, "CENTER", "I"); StaticField(c, "RIGHT", "I");
                break;
            case "java.awt.TextField":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;I)V", 0x0101);
                Method(c, "getText", "()Ljava/lang/String;"); Method(c, "setText", "(Ljava/lang/String;)V");
                Method(c, "getColumns", "()I"); Method(c, "setEditable", "(Z)V"); Method(c, "isEditable", "()Z");
                break;
            case "java.awt.TextArea":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/String;II)V", 0x0101); Method(c, "<init>", "(II)V", 0x0101);
                Method(c, "getText", "()Ljava/lang/String;"); Method(c, "setText", "(Ljava/lang/String;)V");
                Method(c, "append", "(Ljava/lang/String;)V"); Method(c, "insert", "(Ljava/lang/String;I)V");
                Method(c, "setEditable", "(Z)V"); Method(c, "isEditable", "()Z");
                break;
            case "java.awt.Checkbox":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(Ljava/lang/String;)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/String;Z)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/String;Ljava/awt/CheckboxGroup;Z)V", 0x0101);
                Method(c, "getState", "()Z"); Method(c, "setState", "(Z)V");
                Method(c, "getLabel", "()Ljava/lang/String;"); Method(c, "setLabel", "(Ljava/lang/String;)V");
                break;
            case "java.awt.Choice":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "addItem", "(Ljava/lang/String;)V"); Method(c, "add", "(Ljava/lang/String;)V");
                Method(c, "getItem", "(I)Ljava/lang/String;");
                Method(c, "getItemCount", "()I"); Method(c, "countItems", "()I");
                Method(c, "getSelectedItem", "()Ljava/lang/String;"); Method(c, "getSelectedIndex", "()I");
                Method(c, "select", "(I)V"); Method(c, "select", "(Ljava/lang/String;)V");
                Method(c, "deselect", "(I)V");
                break;
            case "java.awt.List":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101);
                Method(c, "addItem", "(Ljava/lang/String;)V"); Method(c, "add", "(Ljava/lang/String;)V");
                Method(c, "getItem", "(I)Ljava/lang/String;");
                Method(c, "getItemCount", "()I"); Method(c, "countItems", "()I");
                Method(c, "getSelectedItem", "()Ljava/lang/String;"); Method(c, "getSelectedIndex", "()I");
                Method(c, "select", "(I)V"); Method(c, "deselect", "(I)V");
                break;
            case "java.awt.Scrollbar":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101);
                Method(c, "<init>", "(IIIII)V", 0x0101);
                Method(c, "getValue", "()I"); Method(c, "setValue", "(I)V");
                Method(c, "getMinimum", "()I"); Method(c, "getMaximum", "()I");
                Method(c, "getVisibleAmount", "()I"); Method(c, "setVisibleAmount", "(I)V");
                Method(c, "setValues", "(IIII)V");
                Method(c, "getOrientation", "()I");
                Method(c, "setUnitIncrement", "(I)V"); Method(c, "getUnitIncrement", "()I");
                Method(c, "setBlockIncrement", "(I)V"); Method(c, "getBlockIncrement", "()I");
                StaticField(c, "VERTICAL", "I"); StaticField(c, "HORIZONTAL", "I");
                break;
            case "java.awt.Dimension":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(II)V", 0x0101);
                Field(c, "width", "I"); Field(c, "height", "I");
                Method(c, "getWidth", "()D"); Method(c, "getHeight", "()D");
                Method(c, "getSize", "()Ljava/awt/Dimension;"); Method(c, "setSize", "(II)V");
                Method(c, "setSize", "(Ljava/awt/Dimension;)V");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                break;
            case "java.awt.Insets":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(IIII)V", 0x0101);
                Field(c, "top", "I"); Field(c, "left", "I"); Field(c, "bottom", "I"); Field(c, "right", "I");
                break;
            case "java.awt.Point":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(II)V", 0x0101);
                Field(c, "x", "I"); Field(c, "y", "I");
                Method(c, "getX", "()D"); Method(c, "getY", "()D");
                Method(c, "move", "(II)V"); Method(c, "setLocation", "(II)V"); Method(c, "setLocation", "(Ljava/awt/Point;)V");
                Method(c, "getLocation", "()Ljava/awt/Point;");
                Method(c, "translate", "(II)V");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                break;
            case "java.awt.Rectangle":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(II)V", 0x0101);
                Method(c, "<init>", "(IIII)V", 0x0101);
                Method(c, "<init>", "(Ljava/awt/Point;Ljava/awt/Dimension;)V", 0x0101);
                Method(c, "<init>", "(Ljava/awt/Rectangle;)V", 0x0101);
                Field(c, "x", "I"); Field(c, "y", "I"); Field(c, "width", "I"); Field(c, "height", "I");
                Method(c, "getBounds", "()Ljava/awt/Rectangle;");
                Method(c, "setBounds", "(IIII)V"); Method(c, "setBounds", "(Ljava/awt/Rectangle;)V");
                Method(c, "reshape", "(IIII)V");
                Method(c, "getSize", "()Ljava/awt/Dimension;"); Method(c, "getLocation", "()Ljava/awt/Point;");
                Method(c, "contains", "(II)Z"); Method(c, "inside", "(II)Z");
                Method(c, "contains", "(Ljava/awt/Point;)Z"); Method(c, "contains", "(Ljava/awt/Rectangle;)Z");
                Method(c, "intersects", "(Ljava/awt/Rectangle;)Z");
                Method(c, "intersection", "(Ljava/awt/Rectangle;)Ljava/awt/Rectangle;");
                Method(c, "union", "(Ljava/awt/Rectangle;)Ljava/awt/Rectangle;");
                Method(c, "isEmpty", "()Z");
                Method(c, "translate", "(II)V"); Method(c, "grow", "(II)V"); Method(c, "add", "(II)V");
                Method(c, "equals", "(Ljava/lang/Object;)Z"); Method(c, "hashCode", "()I");
                Method(c, "toString", "()Ljava/lang/String;");
                break;
            case "java.awt.Event":
                Method(c, "<init>", "()V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/Object;I)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/Object;JIIII)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/Object;JIIIIILjava/lang/Object;)V", 0x0101);
                Method(c, "toString", "()Ljava/lang/String;"); Method(c, "translate", "(II)V");
                Field(c, "target", "Ljava/lang/Object;"); Field(c, "when", "J"); Field(c, "id", "I");
                Field(c, "x", "I"); Field(c, "y", "I"); Field(c, "key", "I"); Field(c, "modifiers", "I");
                Field(c, "arg", "Ljava/lang/Object;");
                StaticField(c, "SHIFT_MASK", "I"); StaticField(c, "CTRL_MASK", "I");
                StaticField(c, "META_MASK", "I"); StaticField(c, "ALT_MASK", "I");
                break;
            case "java.awt.AWTEvent":
                Method(c, "getID", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                break;
            case "java.awt.event.MouseEvent":
                Method(c, "<init>", "(Ljava/awt/Component;IJIIIIIZ)V", 0x0101);
                Method(c, "<init>", "(Ljava/awt/Component;IJIIIZ)V", 0x0101);
                Method(c, "getX", "()I"); Method(c, "getY", "()I"); Method(c, "getID", "()I");
                Method(c, "getButton", "()I"); Method(c, "getClickCount", "()I");
                Method(c, "getModifiers", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                Method(c, "getPoint", "()Ljava/awt/Point;");
                Method(c, "isShiftDown", "()Z"); Method(c, "isControlDown", "()Z");
                Method(c, "isMetaDown", "()Z"); Method(c, "isAltDown", "()Z");
                Method(c, "isPopupTrigger", "()Z");
                break;
            case "java.awt.event.KeyEvent":
                Method(c, "<init>", "(Ljava/awt/Component;IJII)V", 0x0101);
                Method(c, "<init>", "(Ljava/awt/Component;IJIIC)V", 0x0101);
                Method(c, "getKeyCode", "()I"); Method(c, "getKeyChar", "()C"); Method(c, "getID", "()I");
                Method(c, "getModifiers", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                Method(c, "isShiftDown", "()Z"); Method(c, "isControlDown", "()Z");
                Method(c, "isMetaDown", "()Z"); Method(c, "isAltDown", "()Z");
                Method(c, "isActionKey", "()Z");
                break;
            case "java.awt.event.ActionEvent":
                Method(c, "<init>", "(Ljava/lang/Object;ILjava/lang/String;)V", 0x0101);
                Method(c, "<init>", "(Ljava/lang/Object;ILjava/lang/String;I)V", 0x0101);
                Method(c, "getActionCommand", "()Ljava/lang/String;"); Method(c, "getID", "()I");
                Method(c, "getModifiers", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                break;
            case "java.awt.event.ItemEvent":
                Method(c, "<init>", "(Ljava/lang/Object;ILjava/lang/Object;I)V", 0x0101);
                Method(c, "getItem", "()Ljava/lang/Object;"); Method(c, "getID", "()I");
                Method(c, "getStateChange", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                break;
            case "java.awt.event.InputEvent":
                Method(c, "getModifiers", "()I");
                Method(c, "isShiftDown", "()Z"); Method(c, "isControlDown", "()Z");
                Method(c, "isMetaDown", "()Z"); Method(c, "isAltDown", "()Z");
                Method(c, "consume", "()V"); Method(c, "isConsumed", "()Z");
                break;
            case "java.awt.event.FocusEvent":
                Method(c, "<init>", "(Ljava/awt/Component;Z)V", 0x0101);
                Method(c, "isTemporary", "()Z"); Method(c, "getID", "()I"); Method(c, "getSource", "()Ljava/lang/Object;");
                break;
            case "java.awt.FlowLayout":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(I)V", 0x0101); Method(c, "<init>", "(III)V", 0x0101);
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/lang/Object;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                break;
            case "java.awt.BorderLayout":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(II)V", 0x0101);
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/lang/Object;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                StaticField(c, "NORTH", "Ljava/lang/String;"); StaticField(c, "SOUTH", "Ljava/lang/String;");
                StaticField(c, "EAST", "Ljava/lang/String;"); StaticField(c, "WEST", "Ljava/lang/String;");
                StaticField(c, "CENTER", "Ljava/lang/String;");
                break;
            case "java.awt.GridLayout":
                Method(c, "<init>", "()V", 0x0101); Method(c, "<init>", "(II)V", 0x0101); Method(c, "<init>", "(IIII)V", 0x0101);
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/lang/Object;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                break;
            default:
                if (BuiltinJavaLibrary.ExceptionBodies.Contains(c.Name)) DeclareException(c);
                break;
        }
        BuiltinJavaLibrary.RegisterNatives(vm, c);
    }

    private static void PopulateInterface(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.lang.Runnable": Method(c, "run", "()V"); break;
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
            case "java.awt.LayoutManager":
                Method(c, "layoutContainer", "(Ljava/awt/Container;)V");
                Method(c, "preferredLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "minimumLayoutSize", "(Ljava/awt/Container;)Ljava/awt/Dimension;");
                Method(c, "addLayoutComponent", "(Ljava/lang/String;Ljava/awt/Component;)V");
                Method(c, "removeLayoutComponent", "(Ljava/awt/Component;)V");
                break;
            case "java.awt.Shape":
                Method(c, "getBounds", "()Ljava/awt/Rectangle;");
                break;
            case "java.awt.image.ImageObserver":
                Method(c, "imageUpdate", "(Ljava/awt/Image;IIII)Z");
                break;
            case "java.util.Enumeration":
                Method(c, "hasMoreElements", "()Z");
                Method(c, "nextElement", "()Ljava/lang/Object;");
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
            case "java.awt.event.FocusListener":
                Method(c, "focusGained", "(Ljava/awt/event/FocusEvent;)V");
                Method(c, "focusLost", "(Ljava/awt/event/FocusEvent;)V");
                break;
            case "java.awt.event.ActionListener":
                Method(c, "actionPerformed", "(Ljava/awt/event/ActionEvent;)V");
                break;
            case "java.awt.event.ItemListener":
                Method(c, "itemStateChanged", "(Ljava/awt/event/ItemEvent;)V");
                break;
        }
        BuiltinJavaLibrary.RegisterNatives(vm, c);
    }

    private static void RegisterGraphics(JClass c)
    {
        Method(c, "create", "()Ljava/awt/Graphics;"); Method(c, "create", "(IIII)Ljava/awt/Graphics;");
        Method(c, "dispose", "()V"); Method(c, "translate", "(II)V");
        Method(c, "setColor", "(Ljava/awt/Color;)V"); Method(c, "getColor", "()Ljava/awt/Color;");
        Method(c, "setFont", "(Ljava/awt/Font;)V"); Method(c, "getFont", "()Ljava/awt/Font;");
        Method(c, "clipRect", "(IIII)V"); Method(c, "setClip", "(IIII)V"); Method(c, "setClip", "(Ljava/awt/Shape;)V");
        Method(c, "getClipBounds", "()Ljava/awt/Rectangle;"); Method(c, "getClip", "()Ljava/awt/Shape;");
        Method(c, "hitClip", "(IIII)Z");
        Method(c, "clearRect", "(IIII)V");
        Method(c, "getFontMetrics", "()Ljava/awt/FontMetrics;"); Method(c, "getFontMetrics", "(Ljava/awt/Font;)Ljava/awt/FontMetrics;");
        Method(c, "setPaintMode", "()V"); Method(c, "setXORMode", "(Ljava/awt/Color;)V");
        Method(c, "copyArea", "(IIIIII)V");
        foreach (var n in new[] { "drawLine", "drawRect", "fillRect", "drawOval", "fillOval" }) Method(c, n, "(IIII)V");
        Method(c, "drawRoundRect", "(IIIIII)V"); Method(c, "fillRoundRect", "(IIIIII)V");
        Method(c, "draw3DRect", "(IIIIIZ)V"); Method(c, "fill3DRect", "(IIIIIZ)V");
        Method(c, "drawString", "(Ljava/lang/String;II)V");
        Method(c, "drawBytes", "([BIIII)V"); Method(c, "drawChars", "([CIIII)V");
        Method(c, "drawPolygon", "([I[II)V"); Method(c, "fillPolygon", "([I[II)V");
        Method(c, "drawPolyline", "([I[II)V");
        Method(c, "drawPolygon", "(Ljava/awt/Polygon;)V"); Method(c, "fillPolygon", "(Ljava/awt/Polygon;)V");
        Method(c, "drawArc", "(IIIIII)V"); Method(c, "fillArc", "(IIIIII)V");
        Method(c, "drawImage", "(Ljava/awt/Image;IILjava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IILjava/lang/Object;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/lang/Object;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIILjava/awt/Color;Ljava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIILjava/awt/Color;Ljava/lang/Object;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIILjava/awt/Color;Ljava/lang/Object;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIIIIIILjava/awt/image/ImageObserver;)Z");
        Method(c, "drawImage", "(Ljava/awt/Image;IIIIIIIILjava/lang/Object;)Z");
    }

}
