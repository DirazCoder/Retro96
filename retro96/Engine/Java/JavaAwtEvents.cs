namespace Retro96.Engine.Java;

// Raw host input, translated from WinForms events by BrowserCanvas.
public enum JavaInputKind { MouseDown, MouseUp, MouseMove, MouseDrag, KeyDown, KeyUp, KeyTyped, MouseEnter, MouseExit, MouseWheel, FocusGained, FocusLost }

public readonly record struct JavaInput(JavaInputKind Kind, int X, int Y, int Button, int WheelDelta, int KeyCode, char KeyChar, bool Shift, bool Control, bool Alt);

// Event object payload shared by MouseEvent, KeyEvent, ActionEvent,
// ItemEvent and the 1.0 Event class.
public sealed class JavaEventState
{
    public JavaInput Input;
    public int Id;
    public JObject? Source;
    public string Command = "";
    public JValue Item = JValue.Void;
    public int StateChange;
    public int ClickCount = 1;
}

// Natives for java.awt.Event (1.0 model), the 1.1 AWTEvent hierarchy
// (MouseEvent, KeyEvent, ActionEvent, ItemEvent, InputEvent) and the
// listener registration surface on Component.
internal static class JavaAwtEvents
{
    // 1.1 modifier masks; BUTTON2 aliases ALT and BUTTON3 aliases META,
    // exactly as the JDK defines them.
    internal const int ShiftMask = 1, CtrlMask = 2, MetaMask = 4, AltMask = 8;
    internal const int Button1Mask = 16, Button2Mask = 8, Button3Mask = 4;

    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.awt.Event": RegisterEvent(vm, c); break;
            case "java.awt.AWTEvent": RegisterAwtEvent(vm, c); break;
            case "java.awt.event.MouseEvent": RegisterMouseEvent(vm, c); break;
            case "java.awt.event.KeyEvent": RegisterKeyEvent(vm, c); break;
            case "java.awt.event.ActionEvent": RegisterActionEvent(vm, c); break;
            case "java.awt.event.ItemEvent": RegisterItemEvent(vm, c); break;
            case "java.awt.event.InputEvent": RegisterInputEvent(vm, c); break;
            case "java.awt.event.FocusEvent": RegisterFocusEvent(vm, c); break;
        }
    }

    // Base AWTEvent accessors so code holding the abstract type (or an
    // event class without its own override) still reads id and source.
    private static void RegisterAwtEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString(i.Receiver.AsObject()!.Class.Name + "[id=" + ((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0) + "]")));
        vm.RegisterNative(c.Name, "paramString", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString("")));
    }

    private static void RegisterEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;I)V", i =>
        {
            InitEvent(vm, i.Receiver.AsObject()!, c, i.Arguments[0], 0, i.Arguments[1].AsInt(), 0, 0, 0, 0, JValue.Ref(null));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;JIIII)V", i =>
        {
            InitEvent(vm, i.Receiver.AsObject()!, c, i.Arguments[0], (int)i.Arguments[1].AsLong(), i.Arguments[2].AsInt(),
                i.Arguments[3].AsInt(), i.Arguments[4].AsInt(), i.Arguments[5].AsInt(), i.Arguments[6].AsInt(), JValue.Ref(null));
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;JIIIIILjava/lang/Object;)V", i =>
        {
            InitEvent(vm, i.Receiver.AsObject()!, c, i.Arguments[0], (int)i.Arguments[1].AsLong(), i.Arguments[2].AsInt(),
                i.Arguments[3].AsInt(), i.Arguments[4].AsInt(), i.Arguments[5].AsInt(), i.Arguments[6].AsInt(), i.Arguments[7]);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString("java.awt.Event[" + EventIdOf(vm, i.Receiver) + "]")));
        vm.RegisterNative(c.Name, "translate", "(II)V", i =>
        {
            var o = i.Receiver.AsObject()!;
            int x = vm.GetField(o, c, "x", "I").AsInt() + i.Arguments[0].AsInt();
            int y = vm.GetField(o, c, "y", "I").AsInt() + i.Arguments[1].AsInt();
            vm.SetField(o, c, "x", "I", JValue.Int(x));
            vm.SetField(o, c, "y", "I", JValue.Int(y));
            return JValue.Void;
        });
        SetEventStatics(vm, c);
    }

    private static int EventIdOf(JavaVm vm, JValue receiver)
    {
        var cls = vm.LoadClass("java.awt.Event");
        return receiver.AsObject() is { } o ? vm.GetField(o, cls, "id", "I").AsInt() : 0;
    }

    private static void InitEvent(JavaVm vm, JObject o, JClass c, JValue target, int when, int id, int x, int y, int key, int modifiers, JValue arg)
    {
        vm.SetField(o, c, "target", "Ljava/lang/Object;", target);
        vm.SetField(o, c, "when", "J", JValue.Long(when));
        vm.SetField(o, c, "id", "I", JValue.Int(id));
        vm.SetField(o, c, "x", "I", JValue.Int(x));
        vm.SetField(o, c, "y", "I", JValue.Int(y));
        vm.SetField(o, c, "key", "I", JValue.Int(key));
        vm.SetField(o, c, "modifiers", "I", JValue.Int(modifiers));
        vm.SetField(o, c, "arg", "Ljava/lang/Object;", arg);
    }

    private static void SetEventStatics(JavaVm vm, JClass c)
    {
        vm.SetStatic(c, "SHIFT_MASK", "I", JValue.Int(ShiftMask));
        vm.SetStatic(c, "CTRL_MASK", "I", JValue.Int(CtrlMask));
        vm.SetStatic(c, "META_MASK", "I", JValue.Int(MetaMask));
        vm.SetStatic(c, "ALT_MASK", "I", JValue.Int(AltMask));
        vm.SetStatic(c, "MOUSE_DOWN", "I", JValue.Int(501));
        vm.SetStatic(c, "MOUSE_UP", "I", JValue.Int(502));
        vm.SetStatic(c, "MOUSE_MOVE", "I", JValue.Int(503));
        vm.SetStatic(c, "MOUSE_DRAG", "I", JValue.Int(504));
        vm.SetStatic(c, "MOUSE_ENTER", "I", JValue.Int(505));
        vm.SetStatic(c, "MOUSE_EXIT", "I", JValue.Int(506));
        vm.SetStatic(c, "KEY_PRESS", "I", JValue.Int(401));
        vm.SetStatic(c, "KEY_RELEASE", "I", JValue.Int(402));
        vm.SetStatic(c, "KEY_ACTION", "I", JValue.Int(403));
        vm.SetStatic(c, "KEY_ACTION_RELEASE", "I", JValue.Int(404));
        vm.SetStatic(c, "WINDOW_DESTROY", "I", JValue.Int(201));
        vm.SetStatic(c, "WINDOW_EXPOSE", "I", JValue.Int(202));
        vm.SetStatic(c, "WINDOW_ICONIFY", "I", JValue.Int(203));
        vm.SetStatic(c, "WINDOW_DEICONIFY", "I", JValue.Int(204));
        vm.SetStatic(c, "WINDOW_DEICONIFIED", "I", JValue.Int(204));
        vm.SetStatic(c, "WINDOW_MOVED", "I", JValue.Int(205));
        vm.SetStatic(c, "LIST_SELECT", "I", JValue.Int(701));
        vm.SetStatic(c, "LIST_DESELECT", "I", JValue.Int(702));
        vm.SetStatic(c, "SCROLL_ABSOLUTE", "I", JValue.Int(605));
        vm.SetStatic(c, "SCROLL_LINE_UP", "I", JValue.Int(601));
        vm.SetStatic(c, "SCROLL_LINE_DOWN", "I", JValue.Int(602));
        vm.SetStatic(c, "SCROLL_PAGE_UP", "I", JValue.Int(603));
        vm.SetStatic(c, "SCROLL_PAGE_DOWN", "I", JValue.Int(604));
        vm.SetStatic(c, "ACTION_EVENT", "I", JValue.Int(1001));
        vm.SetStatic(c, "LOAD_FILE", "I", JValue.Int(1002));
        vm.SetStatic(c, "SAVE_FILE", "I", JValue.Int(1003));
        vm.SetStatic(c, "GOT_FOCUS", "I", JValue.Int(1004));
        vm.SetStatic(c, "LOST_FOCUS", "I", JValue.Int(1005));
    }

    // Modifier bitmask from the host input state.
    internal static int Modifiers(JavaInput input)
    {
        int mods = 0;
        if (input.Shift) mods |= ShiftMask;
        if (input.Control) mods |= CtrlMask;
        if (input.Alt) mods |= AltMask;
        if (input.Button == 1) mods |= Button1Mask;
        else if (input.Button == 2) mods |= Button2Mask;
        else if (input.Button == 3) mods |= Button3Mask;
        return mods;
    }

    internal static JObject MakeMouseEvent(JavaVm vm, JObject target, JavaInput i, int id, int clickCount) =>
        new()
        {
            Class = vm.LoadClass("java.awt.event.MouseEvent"),
            NativeState = new JavaEventState { Input = i, Id = id, Source = target, ClickCount = clickCount }
        };

    internal static JObject MakeKeyEvent(JavaVm vm, JObject target, JavaInput i, int id) =>
        new() { Class = vm.LoadClass("java.awt.event.KeyEvent"), NativeState = new JavaEventState { Input = i, Id = id, Source = target } };

    internal static JObject MakeActionEvent(JavaVm vm, JObject target, string command, int id) =>
        new() { Class = vm.LoadClass("java.awt.event.ActionEvent"), NativeState = new JavaEventState { Id = id, Source = target, Command = command } };

    internal static JObject MakeItemEvent(JavaVm vm, JObject target, JValue item, int stateChange) =>
        new() { Class = vm.LoadClass("java.awt.event.ItemEvent"), NativeState = new JavaEventState { Id = 701, Source = target, Item = item, StateChange = stateChange } };

    // A 1.0-style Event object built from host input.
    internal static JObject CreateOldEvent(JavaVm vm, JObject target, JavaInput input)
    {
        var c = vm.LoadClass("java.awt.Event");
        var e = new JObject { Class = c, NativeState = input };
        vm.SetField(e, c, "target", "Ljava/lang/Object;", JValue.Ref(target));
        vm.SetField(e, c, "when", "J", JValue.Long(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        int id = input.Kind switch
        {
            JavaInputKind.MouseDown => 501,
            JavaInputKind.MouseUp => 502,
            JavaInputKind.MouseEnter => 504,
            JavaInputKind.MouseExit => 505,
            JavaInputKind.MouseDrag => 506,
            JavaInputKind.KeyDown or JavaInputKind.KeyTyped => 401,
            JavaInputKind.KeyUp => 402,
            JavaInputKind.FocusGained => 1004,
            JavaInputKind.FocusLost => 1005,
            _ => 503 // mouse move
        };
        vm.SetField(e, c, "id", "I", JValue.Int(id));
        vm.SetField(e, c, "x", "I", JValue.Int(input.X));
        vm.SetField(e, c, "y", "I", JValue.Int(input.Y));
        vm.SetField(e, c, "key", "I", JValue.Int(input.KeyCode != 0 ? input.KeyCode : input.KeyChar));
        vm.SetField(e, c, "modifiers", "I", JValue.Int(Modifiers(input)));
        return e;
    }

    private static void RegisterMouseEvent(JavaVm vm, JClass c)
    {
        // Button number from the 1.1 modifier mask (BUTTON2/BUTTON3 bits).
        static int ButtonOf(int mods) => (mods & Button2Mask) != 0 ? 2 : (mods & Button3Mask) != 0 ? 3 : 1;
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJIIIIIZ)V", i =>
        {
            // 1.3-style signature (source, id, when, mods, x, y, click, xAbs, yAbs, popup)
            var mods = i.Arguments[3].AsInt();
            var st = new JavaEventState
            {
                Source = i.Arguments[0].AsObject(),
                Id = i.Arguments[1].AsInt(),
                ClickCount = i.Arguments[6].AsInt(),
                Input = new JavaInput(JavaInputKind.MouseDown, i.Arguments[4].AsInt(), i.Arguments[5].AsInt(),
                    ButtonOf(mods), 0, 0, '\0',
                    (mods & ShiftMask) != 0, (mods & CtrlMask) != 0, (mods & AltMask) != 0)
            };
            i.Receiver.AsObject()!.NativeState = st;
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJIIIZ)V", i =>
        {
            // The canonical JDK 1.1 signature (source, id, when, mods, x, y, clickCount, popup).
            var mods = i.Arguments[3].AsInt();
            var st = new JavaEventState
            {
                Source = i.Arguments[0].AsObject(),
                Id = i.Arguments[1].AsInt(),
                ClickCount = i.Arguments[6].AsInt(),
                Input = new JavaInput(JavaInputKind.MouseDown, i.Arguments[4].AsInt(), i.Arguments[5].AsInt(),
                    ButtonOf(mods), 0, 0, '\0',
                    (mods & ShiftMask) != 0, (mods & CtrlMask) != 0, (mods & AltMask) != 0)
            };
            i.Receiver.AsObject()!.NativeState = st;
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getX", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.X ?? 0));
        vm.RegisterNative(c.Name, "getY", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.Y ?? 0));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getClickCount", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.ClickCount ?? 1));
        vm.RegisterNative(c.Name, "getButton", "()I", i =>
        {
            var b = (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input.Button ?? 0;
            return JValue.Int(b <= 0 ? 1 : b);
        });
        vm.RegisterNative(c.Name, "getModifiers", "()I", i =>
        {
            var inp = (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input ?? default;
            return JValue.Int(Modifiers(inp));
        });
        vm.RegisterNative(c.Name, "isShiftDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & ShiftMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isControlDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & CtrlMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isMetaDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & MetaMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isAltDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & AltMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.RegisterNative(c.Name, "getPoint", "()Ljava/awt/Point;", i =>
        {
            var pointCls = vm.LoadClass("java.awt.Point");
            var inp = EvInput(i);
            var point = new JObject { Class = pointCls };
            vm.SetField(point, pointCls, "x", "I", JValue.Int(inp.X));
            vm.SetField(point, pointCls, "y", "I", JValue.Int(inp.Y));
            return JValue.Ref(point);
        });
        vm.RegisterNative(c.Name, "isPopupTrigger", "()Z", _ => JValue.Int(0));
        vm.SetStatic(c, "MOUSE_CLICKED", "I", JValue.Int(500));
        vm.SetStatic(c, "MOUSE_PRESSED", "I", JValue.Int(501));
        vm.SetStatic(c, "MOUSE_RELEASED", "I", JValue.Int(502));
        vm.SetStatic(c, "MOUSE_MOVED", "I", JValue.Int(503));
        vm.SetStatic(c, "MOUSE_ENTERED", "I", JValue.Int(504));
        vm.SetStatic(c, "MOUSE_EXITED", "I", JValue.Int(505));
        vm.SetStatic(c, "MOUSE_DRAGGED", "I", JValue.Int(506));
        vm.SetStatic(c, "MOUSE_WHEEL", "I", JValue.Int(507));
        vm.SetStatic(c, "NOBUTTON", "I", JValue.Int(0));
        vm.SetStatic(c, "BUTTON1", "I", JValue.Int(1));
        vm.SetStatic(c, "BUTTON2", "I", JValue.Int(2));
        vm.SetStatic(c, "BUTTON3", "I", JValue.Int(3));
        vm.SetStatic(c, "BUTTON1_MASK", "I", JValue.Int(Button1Mask));
        vm.SetStatic(c, "BUTTON2_MASK", "I", JValue.Int(Button2Mask));
        vm.SetStatic(c, "BUTTON3_MASK", "I", JValue.Int(Button3Mask));
    }

    private static JavaInput EvInput(JavaInvocation i) => (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Input ?? default;

    private static void RegisterKeyEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJII)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState
            {
                Source = i.Arguments[0].AsObject(),
                Id = i.Arguments[1].AsInt(),
                Input = new JavaInput(JavaInputKind.KeyDown, 0, 0, 0, 0, i.Arguments[4].AsInt(), '\0', false, false, false)
            };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;IJIIC)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState
            {
                Source = i.Arguments[0].AsObject(),
                Id = i.Arguments[1].AsInt(),
                Input = new JavaInput(JavaInputKind.KeyDown, 0, 0, 0, 0, i.Arguments[4].AsInt(), (char)i.Arguments[5].AsInt(), false, false, false)
            };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getKeyCode", "()I", i =>
        {
            var inp = EvInput(i);
            return JValue.Int(inp.KeyCode != 0 ? inp.KeyCode : inp.KeyChar);
        });
        vm.RegisterNative(c.Name, "getKeyChar", "()C", i => JValue.Int(EvInput(i).KeyChar));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getModifiers", "()I", i => JValue.Int(Modifiers(EvInput(i))));
        vm.RegisterNative(c.Name, "isShiftDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & ShiftMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isControlDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & CtrlMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isMetaDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & MetaMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isAltDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & AltMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.RegisterNative(c.Name, "isActionKey", "()Z", i =>
        {
            int code = EvInput(i).KeyCode;
            // Arrows, navigation keys, function keys and lock keys.
            bool action = code is >= 33 and <= 40
                or 127
                or 155
                or 19
                or >= 0x70 and <= 0x7B
                or 154 or 144 or 145 or 20;
            return JValue.Int(action ? 1 : 0);
        });
        vm.SetStatic(c, "KEY_TYPED", "I", JValue.Int(400));
        vm.SetStatic(c, "KEY_PRESSED", "I", JValue.Int(401));
        vm.SetStatic(c, "KEY_RELEASED", "I", JValue.Int(402));
        vm.SetStatic(c, "KEY_FIRST", "I", JValue.Int(400));
        vm.SetStatic(c, "KEY_LAST", "I", JValue.Int(402));
        vm.SetStatic(c, "VK_UNDEFINED", "I", JValue.Int(0));
        vm.SetStatic(c, "VK_ENTER", "I", JValue.Int(10));
        vm.SetStatic(c, "VK_BACK_SPACE", "I", JValue.Int(8));
        vm.SetStatic(c, "VK_TAB", "I", JValue.Int(9));
        vm.SetStatic(c, "VK_CANCEL", "I", JValue.Int(3));
        vm.SetStatic(c, "VK_ESCAPE", "I", JValue.Int(27));
        vm.SetStatic(c, "VK_SPACE", "I", JValue.Int(32));
        vm.SetStatic(c, "VK_DELETE", "I", JValue.Int(127));
        vm.SetStatic(c, "VK_INSERT", "I", JValue.Int(155));
        vm.SetStatic(c, "VK_PAUSE", "I", JValue.Int(19));
        vm.SetStatic(c, "VK_SHIFT", "I", JValue.Int(16));
        vm.SetStatic(c, "VK_CONTROL", "I", JValue.Int(17));
        vm.SetStatic(c, "VK_ALT", "I", JValue.Int(18));
        vm.SetStatic(c, "VK_ALT_GRAPH", "I", JValue.Int(65406));
        vm.SetStatic(c, "VK_CAPS_LOCK", "I", JValue.Int(20));
        vm.SetStatic(c, "VK_NUM_LOCK", "I", JValue.Int(144));
        vm.SetStatic(c, "VK_SCROLL_LOCK", "I", JValue.Int(145));
        vm.SetStatic(c, "VK_PRINTSCREEN", "I", JValue.Int(154));
        vm.SetStatic(c, "VK_HOME", "I", JValue.Int(36));
        vm.SetStatic(c, "VK_END", "I", JValue.Int(35));
        vm.SetStatic(c, "VK_PAGE_UP", "I", JValue.Int(33));
        vm.SetStatic(c, "VK_PAGE_DOWN", "I", JValue.Int(34));
        vm.SetStatic(c, "VK_LEFT", "I", JValue.Int(37));
        vm.SetStatic(c, "VK_UP", "I", JValue.Int(38));
        vm.SetStatic(c, "VK_RIGHT", "I", JValue.Int(39));
        vm.SetStatic(c, "VK_DOWN", "I", JValue.Int(40));
        vm.SetStatic(c, "VK_COMMA", "I", JValue.Int(44));
        vm.SetStatic(c, "VK_MINUS", "I", JValue.Int(45));
        vm.SetStatic(c, "VK_PERIOD", "I", JValue.Int(46));
        vm.SetStatic(c, "VK_SLASH", "I", JValue.Int(47));
        vm.SetStatic(c, "VK_SEMICOLON", "I", JValue.Int(59));
        vm.SetStatic(c, "VK_EQUALS", "I", JValue.Int(61));
        vm.SetStatic(c, "VK_OPEN_BRACKET", "I", JValue.Int(91));
        vm.SetStatic(c, "VK_BACK_SLASH", "I", JValue.Int(92));
        vm.SetStatic(c, "VK_CLOSE_BRACKET", "I", JValue.Int(93));
        vm.SetStatic(c, "VK_NUMPAD0", "I", JValue.Int(96));
        for (int d = 0; d <= 9; d++) vm.SetStatic(c, "VK_" + d, "I", JValue.Int('0' + d));
        for (char ch = 'A'; ch <= 'Z'; ch++) vm.SetStatic(c, "VK_" + ch, "I", JValue.Int(ch));
        for (int f = 1; f <= 12; f++) vm.SetStatic(c, "VK_F" + f, "I", JValue.Int(111 + f));
    }

    private static void RegisterActionEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;ILjava/lang/String;)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Command = vm.StringValue(i.Arguments[2]) };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;ILjava/lang/String;I)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Command = vm.StringValue(i.Arguments[2]) };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getActionCommand", "()Ljava/lang/String;", i => JValue.Ref(vm.CreateString((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Command ?? "")));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getModifiers", "()I", i => JValue.Int(Modifiers(EvInput(i))));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.SetStatic(c, "ACTION_PERFORMED", "I", JValue.Int(1001));
        vm.SetStatic(c, "SHIFT_MASK", "I", JValue.Int(ShiftMask));
        vm.SetStatic(c, "CTRL_MASK", "I", JValue.Int(CtrlMask));
        vm.SetStatic(c, "META_MASK", "I", JValue.Int(MetaMask));
        vm.SetStatic(c, "ALT_MASK", "I", JValue.Int(AltMask));
    }

    private static void RegisterItemEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/lang/Object;ILjava/lang/Object;I)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt(), Item = i.Arguments[2], StateChange = i.Arguments[3].AsInt() };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "getItem", "()Ljava/lang/Object;", i => (i.Receiver.AsObject()?.NativeState as JavaEventState)?.Item ?? JValue.Ref(null));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getStateChange", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.StateChange ?? 0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.SetStatic(c, "ITEM_STATE_CHANGED", "I", JValue.Int(701));
        vm.SetStatic(c, "SELECTED", "I", JValue.Int(1));
        vm.SetStatic(c, "DESELECTED", "I", JValue.Int(2));
    }

    private static void RegisterInputEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "getModifiers", "()I", i => JValue.Int(Modifiers(EvInput(i))));
        vm.RegisterNative(c.Name, "isShiftDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & ShiftMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isControlDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & CtrlMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isMetaDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & MetaMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "isAltDown", "()Z", i => JValue.Int((Modifiers(EvInput(i)) & AltMask) != 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "consume", "()V", _ => JValue.Void);
        vm.RegisterNative(c.Name, "isConsumed", "()Z", _ => JValue.Int(0));
        vm.SetStatic(c, "SHIFT_MASK", "I", JValue.Int(ShiftMask));
        vm.SetStatic(c, "CTRL_MASK", "I", JValue.Int(CtrlMask));
        vm.SetStatic(c, "META_MASK", "I", JValue.Int(MetaMask));
        vm.SetStatic(c, "ALT_MASK", "I", JValue.Int(AltMask));
        vm.SetStatic(c, "BUTTON1_MASK", "I", JValue.Int(Button1Mask));
        vm.SetStatic(c, "BUTTON2_MASK", "I", JValue.Int(Button2Mask));
        vm.SetStatic(c, "BUTTON3_MASK", "I", JValue.Int(Button3Mask));
    }

    private static void RegisterFocusEvent(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "(Ljava/awt/Component;Z)V", i =>
        {
            i.Receiver.AsObject()!.NativeState = new JavaEventState { Source = i.Arguments[0].AsObject(), Id = i.Arguments[1].AsInt() != 0 ? 1004 : 1005 };
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "isTemporary", "()Z", _ => JValue.Int(0));
        vm.RegisterNative(c.Name, "getID", "()I", i => JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Id ?? 0));
        vm.RegisterNative(c.Name, "getSource", "()Ljava/lang/Object;", i => JValue.Ref((i.Receiver.AsObject()?.NativeState as JavaEventState)?.Source));
        vm.SetStatic(c, "FOCUS_GAINED", "I", JValue.Int(1004));
        vm.SetStatic(c, "FOCUS_LOST", "I", JValue.Int(1005));
    }

    // Listener registration on Component. Kept here because the lists and
    // the dispatch loop below form one concern.
    public static void RegisterListeners(JavaVm vm, JClass c)
    {
        void Reg(string add, string remove, string desc, Func<JavaComponentState, List<JObject>> list)
        {
            vm.RegisterNative(c.Name, add, desc, i =>
            {
                var o = i.Arguments[0].AsObject();
                if (o != null)
                {
                    var l = list(JavaComponentBridge.State(i.Receiver.AsObject()!));
                    if (!l.Contains(o)) l.Add(o);
                }
                return JValue.Void;
            });
            vm.RegisterNative(c.Name, remove, desc, i =>
            {
                var o = i.Arguments[0].AsObject();
                if (o != null) list(JavaComponentBridge.State(i.Receiver.AsObject()!)).Remove(o);
                return JValue.Void;
            });
        }
        Reg("addMouseListener", "removeMouseListener", "(Ljava/awt/event/MouseListener;)V", s => s.MouseListeners);
        Reg("addMouseMotionListener", "removeMouseMotionListener", "(Ljava/awt/event/MouseMotionListener;)V", s => s.MouseMotionListeners);
        Reg("addKeyListener", "removeKeyListener", "(Ljava/awt/event/KeyListener;)V", s => s.KeyListeners);
        Reg("addFocusListener", "removeFocusListener", "(Ljava/awt/event/FocusListener;)V", s => s.FocusListeners);
        Reg("addActionListener", "removeActionListener", "(Ljava/awt/event/ActionListener;)V", s => s.ActionListeners);
        Reg("addItemListener", "removeItemListener", "(Ljava/awt/event/ItemListener;)V", s => s.ItemListeners);
    }

    // Delegation-model dispatch: fire the right listener method for the
    // input kind on the hit component.
    public static bool DispatchListeners(JavaVm vm, JObject target, JavaInput input, int clickCount)
    {
        var s = JavaComponentBridge.State(target);
        bool any = false;
        switch (input.Kind)
        {
            case JavaInputKind.KeyDown:
            {
                var evt = MakeKeyEvent(vm, target, input, 401);
                foreach (var l in s.KeyListeners.ToArray()) { InvokeSafe(vm, l, "keyPressed", "(Ljava/awt/event/KeyEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.KeyUp:
            {
                var evt = MakeKeyEvent(vm, target, input, 402);
                foreach (var l in s.KeyListeners.ToArray()) { InvokeSafe(vm, l, "keyReleased", "(Ljava/awt/event/KeyEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.KeyTyped:
            {
                var evt = MakeKeyEvent(vm, target, input, 400);
                foreach (var l in s.KeyListeners.ToArray()) { InvokeSafe(vm, l, "keyTyped", "(Ljava/awt/event/KeyEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseDown:
            {
                var evt = MakeMouseEvent(vm, target, input, 501, 1);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mousePressed", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseUp:
            {
                var evt = MakeMouseEvent(vm, target, input, 502, clickCount);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mouseReleased", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                var click = MakeMouseEvent(vm, target, input, 500, clickCount);
                foreach (var l in s.MouseListeners.ToArray()) InvokeSafe(vm, l, "mouseClicked", "(Ljava/awt/event/MouseEvent;)V", click);
                break;
            }
            case JavaInputKind.MouseEnter:
            {
                var evt = MakeMouseEvent(vm, target, input, 504, 1);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mouseEntered", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseExit:
            {
                var evt = MakeMouseEvent(vm, target, input, 505, 1);
                foreach (var l in s.MouseListeners.ToArray()) { InvokeSafe(vm, l, "mouseExited", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseMove:
            {
                var evt = MakeMouseEvent(vm, target, input, 503, 0);
                foreach (var l in s.MouseMotionListeners.ToArray()) { InvokeSafe(vm, l, "mouseMoved", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
            case JavaInputKind.MouseDrag:
            {
                var evt = MakeMouseEvent(vm, target, input, 506, 0);
                foreach (var l in s.MouseMotionListeners.ToArray()) { InvokeSafe(vm, l, "mouseDragged", "(Ljava/awt/event/MouseEvent;)V", evt); any = true; }
                break;
            }
        }
        return any;
    }

    // Inheritance-model dispatch (1.0): handleEvent plus the per-event
    // helper methods, walking up the parent chain.
    public static bool OldModel(JavaVm vm, JObject target, JavaInput local)
    {
        var e = CreateOldEvent(vm, target, local);
        JObject? current = target;
        while (current != null)
        {
            var s = JavaComponentBridge.State(current);
            if (!s.Enabled) { current = s.Parent; continue; }
            int cx = local.X, cy = local.Y;
            if (!ReferenceEquals(current, target))
            {
                // Translate into this ancestor's coordinate system.
                var walk = target;
                while (walk != null && !ReferenceEquals(walk, current))
                {
                    var ws = JavaComponentBridge.State(walk);
                    cx += ws.X;
                    cy += ws.Y;
                    walk = ws.Parent;
                }
            }
            if (vm.InvokeVirtual(current, "handleEvent", "(Ljava/awt/Event;)Z", JValue.Ref(e)).AsInt() != 0) return true;
            switch (local.Kind)
            {
                case JavaInputKind.MouseDown or JavaInputKind.MouseUp or JavaInputKind.MouseMove
                    or JavaInputKind.MouseDrag or JavaInputKind.MouseEnter or JavaInputKind.MouseExit:
                {
                    string mn = local.Kind switch
                    {
                        JavaInputKind.MouseDown => "mouseDown",
                        JavaInputKind.MouseUp => "mouseUp",
                        JavaInputKind.MouseDrag => "mouseDrag",
                        JavaInputKind.MouseEnter => "mouseEnter",
                        JavaInputKind.MouseExit => "mouseExit",
                        _ => "mouseMove"
                    };
                    if (vm.InvokeVirtual(current, mn, "(Ljava/awt/Event;II)Z", JValue.Ref(e), JValue.Int(cx), JValue.Int(cy)).AsInt() != 0) return true;
                    break;
                }
                case JavaInputKind.KeyDown or JavaInputKind.KeyUp or JavaInputKind.KeyTyped:
                {
                    string mn = local.Kind == JavaInputKind.KeyUp ? "keyUp" : "keyDown";
                    int key = local.KeyCode != 0 ? local.KeyCode : local.KeyChar;
                    if (vm.InvokeVirtual(current, mn, "(Ljava/awt/Event;I)Z", JValue.Ref(e), JValue.Int(key)).AsInt() != 0) return true;
                    break;
                }
                case JavaInputKind.FocusGained:
                    if (vm.InvokeVirtual(current, "gotFocus", "(Ljava/awt/Event;Ljava/lang/Object;)Z", JValue.Ref(e), JValue.Ref(target)).AsInt() != 0) return true;
                    break;
                case JavaInputKind.FocusLost:
                    if (vm.InvokeVirtual(current, "lostFocus", "(Ljava/awt/Event;Ljava/lang/Object;)Z", JValue.Ref(e), JValue.Ref(target)).AsInt() != 0) return true;
                    break;
            }
            current = s.Parent;
        }
        return false;
    }

    // Fire focus gained/lost on the component's FocusListeners.
    internal static void FireFocus(JavaVm vm, JObject component, bool gained)
    {
        var s = JavaComponentBridge.State(component);
        var evt = new JObject
        {
            Class = vm.LoadClass("java.awt.event.FocusEvent"),
            NativeState = new JavaEventState { Source = component, Id = gained ? 1004 : 1005 }
        };
        foreach (var l in s.FocusListeners.ToArray())
            InvokeSafe(vm, l, gained ? "focusGained" : "focusLost", "(Ljava/awt/event/FocusEvent;)V", evt);
    }

    internal static void InvokeSafe(JavaVm vm, JObject listener, string name, string desc, JObject ev)
    {
        try { vm.InvokeVirtual(listener, name, desc, JValue.Ref(ev)); }
        catch (Exception ex) { vm.Print("[AWT] " + ex.Message + "\n"); }
    }
}
