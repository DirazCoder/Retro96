namespace Retro96.Engine.Java;

// Registry of builtin classes: names, hierarchy, method/field declarations
// and the native bindings per class. Class shapes live here; the native
// implementations are split across JavaStdlib*, JavaUtil, JavaApplet,
// JavaAwt*, JavaAwtGraphics, JavaAwtEvents, JavaAwtComponent and
// JavaComponentBridge.
internal static class BuiltinJavaLibrary
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        // java.lang core
        "java.lang.Object","java.lang.Class","java.lang.String","java.lang.StringBuffer","java.lang.StringBuilder",
        "java.lang.System","java.lang.PrintStream","java.io.PrintStream","java.lang.Thread","java.lang.Runnable",
        "java.lang.Throwable","java.lang.Exception","java.lang.RuntimeException","java.lang.Error",
        "java.lang.NullPointerException","java.lang.ArrayIndexOutOfBoundsException","java.lang.IndexOutOfBoundsException",
        "java.lang.StringIndexOutOfBoundsException","java.lang.ClassCastException","java.lang.ArithmeticException",
        "java.lang.IllegalArgumentException","java.lang.IllegalStateException","java.lang.NumberFormatException",
        "java.lang.ArrayStoreException","java.lang.NegativeArraySizeException","java.lang.UnsupportedOperationException",
        "java.lang.InterruptedException","java.lang.OutOfMemoryError","java.lang.StackOverflowError",
        "java.lang.NoClassDefFoundError","java.lang.NoSuchMethodError","java.lang.NoSuchFieldError","java.lang.ClassFormatError",
        "java.lang.CloneNotSupportedException","java.lang.Cloneable","java.io.Serializable",
        "java.lang.Character","java.lang.SecurityManager","java.lang.Integer","java.lang.Long","java.lang.Float",
        "java.lang.Double","java.lang.Boolean","java.lang.Byte","java.lang.Short","java.lang.Math",
        // io/net exception stubs so catch clauses resolve
        "java.io.IOException","java.io.FileNotFoundException","java.io.EOFException","java.io.InterruptedIOException",
        "java.net.MalformedURLException","java.net.UnknownHostException",
        // net / applet
        "java.net.URL","java.applet.Applet","java.applet.AppletStub","java.applet.AppletContext","java.applet.AudioClip",
        // awt core
        "java.awt.Graphics","java.awt.Image","java.awt.Cursor","java.awt.Color","java.awt.Font","java.awt.FontMetrics",
        "java.awt.Component","java.awt.Container","java.awt.Panel","java.awt.Canvas","java.awt.Button","java.awt.Label",
        "java.awt.TextField","java.awt.TextArea","java.awt.Checkbox","java.awt.CheckboxGroup","java.awt.Choice","java.awt.List",
        "java.awt.Scrollbar","java.awt.Dimension","java.awt.Insets","java.awt.Point","java.awt.Rectangle","java.awt.Event",
        "java.awt.AWTEvent","java.awt.Toolkit","java.awt.MediaTracker","java.awt.Polygon","java.awt.Shape",
        "java.awt.image.ImageObserver","java.awt.image.ImageProducer",
        "java.awt.LayoutManager","java.awt.FlowLayout","java.awt.BorderLayout","java.awt.GridLayout",
        // awt.event
        "java.awt.event.MouseListener","java.awt.event.MouseMotionListener","java.awt.event.KeyListener",
        "java.awt.event.FocusListener","java.awt.event.ActionListener","java.awt.event.ItemListener",
        "java.awt.event.MouseEvent","java.awt.event.KeyEvent","java.awt.event.ActionEvent",
        "java.awt.event.ItemEvent","java.awt.event.InputEvent","java.awt.event.FocusEvent",
        // util
        "java.util.Vector","java.util.Stack","java.util.Hashtable","java.util.Enumeration",
        "java.util.NoSuchElementException","java.util.EmptyStackException",
        "java.util.Random","java.util.Date"
    };

    // Classes that behave like exceptions: constructors storing a message.
    internal static readonly HashSet<string> ExceptionBodies = new(StringComparer.Ordinal)
    {
        "java.lang.Throwable","java.lang.Exception","java.lang.CloneNotSupportedException","java.lang.RuntimeException",
        "java.lang.Error","java.lang.IllegalArgumentException","java.lang.NumberFormatException",
        "java.lang.NullPointerException","java.lang.ArrayIndexOutOfBoundsException","java.lang.IndexOutOfBoundsException",
        "java.lang.StringIndexOutOfBoundsException","java.lang.ClassCastException","java.lang.ArithmeticException",
        "java.lang.NegativeArraySizeException","java.lang.ArrayStoreException","java.lang.IllegalStateException",
        "java.lang.UnsupportedOperationException","java.lang.InterruptedException","java.lang.OutOfMemoryError",
        "java.lang.StackOverflowError","java.lang.NoClassDefFoundError","java.lang.NoSuchMethodError",
        "java.lang.NoSuchFieldError","java.lang.ClassFormatError","java.io.IOException","java.io.FileNotFoundException",
        "java.io.EOFException","java.io.InterruptedIOException","java.net.MalformedURLException","java.net.UnknownHostException",
        "java.util.NoSuchElementException","java.util.EmptyStackException"
    };

internal static readonly HashSet<string> InterfaceNames = new(StringComparer.Ordinal)
    {
        "java.lang.Runnable","java.lang.Cloneable","java.io.Serializable","java.applet.AppletStub",
        "java.applet.AppletContext","java.applet.AudioClip","java.awt.LayoutManager","java.awt.Shape",
        "java.awt.image.ImageObserver","java.awt.image.ImageProducer","java.util.Enumeration",
        "java.awt.event.MouseListener","java.awt.event.MouseMotionListener","java.awt.event.KeyListener",
        "java.awt.event.FocusListener","java.awt.event.ActionListener","java.awt.event.ItemListener"
    };

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
            "java.lang.Object" or "java.lang.Runnable" or "java.lang.Cloneable" or "java.io.Serializable"
                or "java.applet.AppletStub" or "java.applet.AppletContext" or "java.applet.AudioClip"
                or "java.awt.LayoutManager" or "java.awt.Shape" or "java.awt.image.ImageObserver"
                or "java.awt.image.ImageProducer" or "java.util.Enumeration" or "java.awt.event.MouseListener"
                or "java.awt.event.MouseMotionListener" or "java.awt.event.KeyListener" or "java.awt.event.FocusListener"
                or "java.awt.event.ActionListener" or "java.awt.event.ItemListener" => null,
            "java.lang.Throwable" => "java.lang.Object",
            "java.lang.Exception" or "java.lang.CloneNotSupportedException" => "java.lang.Throwable",
            "java.lang.RuntimeException" => "java.lang.Exception",
            "java.lang.Error" => "java.lang.Throwable",
            "java.lang.IllegalArgumentException" or "java.lang.NumberFormatException" or "java.lang.IllegalStateException"
                or "java.lang.NullPointerException" or "java.lang.ArrayIndexOutOfBoundsException"
                or "java.lang.IndexOutOfBoundsException" or "java.lang.StringIndexOutOfBoundsException"
                or "java.lang.ClassCastException" or "java.lang.ArithmeticException" or "java.lang.NegativeArraySizeException"
                or "java.lang.ArrayStoreException" or "java.lang.UnsupportedOperationException"
                or "java.util.NoSuchElementException" or "java.util.EmptyStackException" => "java.lang.RuntimeException",
            "java.lang.OutOfMemoryError" or "java.lang.StackOverflowError" or "java.lang.NoClassDefFoundError"
                or "java.lang.NoSuchMethodError" or "java.lang.NoSuchFieldError" or "java.lang.ClassFormatError" => "java.lang.Error",
            "java.io.IOException" or "java.lang.InterruptedException" => "java.lang.Exception",
            "java.io.FileNotFoundException" or "java.io.EOFException" or "java.io.InterruptedIOException"
                or "java.net.MalformedURLException" or "java.net.UnknownHostException" => "java.io.IOException",
            "java.lang.String" or "java.lang.StringBuffer" or "java.lang.StringBuilder" => "java.lang.Object",
            "java.lang.Character" or "java.lang.SecurityManager" or "java.lang.Integer" or "java.lang.Long"
                or "java.lang.Float" or "java.lang.Double" or "java.lang.Boolean" or "java.lang.Byte" or "java.lang.Short"
                or "java.lang.Math" or "java.lang.System" or "java.lang.PrintStream" or "java.io.PrintStream"
                or "java.lang.Thread" or "java.lang.Class" => "java.lang.Object",
            "java.net.URL" => "java.lang.Object",
            "java.applet.Applet" => "java.awt.Panel",
            "java.util.Random" or "java.util.Date" or "java.util.Hashtable" or "java.util.Vector" => "java.lang.Object",
            "java.util.Stack" => "java.util.Vector",
            "java.awt.Container" => "java.awt.Component",
            "java.awt.Panel" or "java.awt.Canvas" or "java.awt.Button" or "java.awt.Label" or "java.awt.TextField"
                or "java.awt.TextArea" or "java.awt.Checkbox" or "java.awt.Choice" or "java.awt.List" or "java.awt.Scrollbar" => "java.awt.Container",
            "java.awt.Graphics" or "java.awt.Image" or "java.awt.Cursor" or "java.awt.Color" or "java.awt.Font"
                or "java.awt.FontMetrics" or "java.awt.Dimension" or "java.awt.Insets" or "java.awt.Point"
                or "java.awt.Rectangle" or "java.awt.Event" or "java.awt.Toolkit" or "java.awt.MediaTracker"
                or "java.awt.Polygon" or "java.awt.CheckboxGroup"
                or "java.awt.FlowLayout" or "java.awt.BorderLayout" or "java.awt.GridLayout" => "java.lang.Object",
            "java.awt.AWTEvent" or "java.awt.Component" => "java.lang.Object",
            "java.awt.event.InputEvent" => "java.awt.AWTEvent",
            "java.awt.event.MouseEvent" or "java.awt.event.KeyEvent" or "java.awt.event.FocusEvent" => "java.awt.event.InputEvent",
            "java.awt.event.ActionEvent" or "java.awt.event.ItemEvent" => "java.awt.AWTEvent",
            _ => "java.lang.Object"
        };
        var cls = new JClass { Name = name, IsBuiltin = true };
        vm.RegisterClass(cls);
        if (super != null) cls.SuperClass = vm.LoadClass(super);
        BuiltinJavaDeclarations.Populate(vm, cls);
        vm.BuildVTable(cls);
        return cls;
    }

    internal static void RegisterNatives(JavaVm vm, JClass c)
    {
        JavaStdlib.Register(vm, c);
        JavaStdlibWrappers.Register(vm, c);
        JavaUtil.Register(vm, c);
        JavaApplet.Register(vm, c);
        JavaAwt.Register(vm, c);
        JavaAwtGraphics.Register(vm, c);
        JavaAwtEvents.Register(vm, c);
        JavaAwtComponent.Register(vm, c);
        switch (c.Name)
        {
            case "java.awt.Cursor":
                vm.RegisterNative(c.Name, "<init>", "(I)V", _ => JValue.Void);
                vm.SetStatic(c, "DEFAULT_CURSOR", "I", JValue.Int(0)); vm.SetStatic(c, "CROSSHAIR_CURSOR", "I", JValue.Int(1));
                vm.SetStatic(c, "TEXT_CURSOR", "I", JValue.Int(2)); vm.SetStatic(c, "WAIT_CURSOR", "I", JValue.Int(3));
                vm.SetStatic(c, "HAND_CURSOR", "I", JValue.Int(4)); vm.SetStatic(c, "MOVE_CURSOR", "I", JValue.Int(5));
                break;
            case "java.awt.Panel":
                vm.RegisterNative(c.Name, "<init>", "()V", i =>
                {
                    var st = JavaComponentBridge.State(i.Receiver.AsObject()!);
                    st.Layout = vm.Construct(vm.LoadClass("java.awt.FlowLayout"));
                    return JValue.Void;
                });
                break;
            case "java.awt.Canvas":
                vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = JavaComponentBridge.State(i.Receiver.AsObject()!); return JValue.Void; });
                break;
            case "java.awt.Button": JavaComponentBridge.RegisterTextWidget(vm, c); break;
            case "java.awt.Label": JavaComponentBridge.RegisterTextWidget(vm, c); break;
            case "java.awt.TextField": JavaComponentBridge.RegisterTextWidget(vm, c); break;
            case "java.awt.TextArea": JavaComponentBridge.RegisterTextWidget(vm, c); break;
            case "java.awt.Checkbox": JavaComponentBridge.RegisterCheckbox(vm, c); break;
            case "java.awt.Choice": JavaComponentBridge.RegisterChoice(vm, c); break;
            case "java.awt.List": JavaComponentBridge.RegisterChoice(vm, c); break;
            case "java.awt.Scrollbar": JavaComponentBridge.RegisterScrollbar(vm, c); break;
            case "java.awt.Insets": JavaComponentBridge.RegisterInsets(vm, c); break;
            case "java.awt.FlowLayout": JavaComponentBridge.RegisterFlowLayout(vm, c); break;
            case "java.awt.BorderLayout": JavaComponentBridge.RegisterBorderLayout(vm, c); break;
            case "java.awt.GridLayout": JavaComponentBridge.RegisterGridLayout(vm, c); break;
            case "java.awt.CheckboxGroup":
                vm.RegisterNative(c.Name, "<init>", "()V", _ => JValue.Void);
                vm.RegisterNative(c.Name, "getSelectedCheckbox", "()Ljava/awt/Checkbox;", _ => JValue.Ref(null));
                vm.RegisterNative(c.Name, "setSelectedCheckbox", "(Ljava/awt/Checkbox;)V", _ => JValue.Void);
                break;
        }
        if (ExceptionBodies.Contains(c.Name) && c.Name != "java.lang.Throwable")
            JavaStdlib.RegisterExceptionBody(vm, c);
    }
}
