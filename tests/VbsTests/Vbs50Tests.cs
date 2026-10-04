using Retro96.Engine.Vbs;
using Xunit;

namespace RetroTests;

/// <summary>VBScript 5.0 feature tests: With, Class/Property/Me/New,
/// Class_Initialize/Terminate, Array/Filter/RGB, Eval/Execute/ExecuteGlobal,
/// GetRef, Err.HelpFile/HelpContext, RegExp, Escape/Unescape, Debug,
/// ScriptEngine* version surface.</summary>
public sealed class Vbs50Tests
{
    private sealed class RecordingHost : WshStyleVbsHost
    {
        public readonly List<string> Lines = new();
        public override void WriteLine(string text) => Lines.Add(text);
    }

    /// <summary>Host that opts into VBScript.RegExp only (the browser host
    /// denies all CreateObject — RegExp reaches pages via New RegExp).</summary>
    private sealed class RegExpAllowingHost : WshStyleVbsHost
    {
        public readonly List<string> Lines = new();
        public override void WriteLine(string text) => Lines.Add(text);
        public override IVbsDispatchObject? CreateObject(string progId) =>
            VbsRegExpObject.TryCreate(progId);
    }

    private static VbsSession CreateSession(string source, RecordingHost host) =>
        VbsSession.Create(source, host, new Dictionary<string, IVbsDispatchObject>
        {
            ["WScript"] = host.CreateWScriptObject()
        });

    private static void Run(VbsSession session)
    {
        Assert.True(session.TryRun(out var error), error?.ToString());
    }

    // ── With ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WithBlockQualifiesMembersAgainstTheWithObject()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Point\n" +
            "    Public X, Y\n" +
            "End Class\n" +
            "Dim p\n" +
            "Set p = New Point\n" +
            "With p\n" +
            "    .X = 3\n" +
            "    .Y = .X + 1\n" +
            "End With\n" +
            "With WScript\n" +
            "    .Echo p.X & \",\" & p.Y\n" +
            "End With",
            host);

        Run(session);

        Assert.Equal(new[] { "3,4" }, host.Lines);
    }

    [Fact]
    public void NestedWithResolvesInnerFirstThenFallsBackToOuter()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class A\n" +
            "    Public Name\n" +
            "End Class\n" +
            "Class B\n" +
            "    Public Title\n" +
            "End Class\n" +
            "Dim a, b\n" +
            "Set a = New A\n" +
            "Set b = New B\n" +
            "With a\n" +
            "    .Name = \"outer\"\n" +
            "    With b\n" +
            "        .Title = \"inner\"\n" +
            "        .Name = \"n\"\n" +
            "    End With\n" +
            "End With\n" +
            "WScript.Echo a.Name, b.Title",
            host);

        Run(session);

        Assert.Equal(new[] { "n", "inner" }, host.Lines);
    }

    // ── Class / Property / Me / New ──────────────────────────────────────────

    [Fact]
    public void ClassFieldsMethodsAndReferenceSemantics()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Counter\n" +
            "    Private mCount\n" +
            "    Public Sub Increment()\n" +
            "        mCount = mCount + 1\n" +
            "    End Sub\n" +
            "    Public Function Describe(prefix)\n" +
            "        Describe = prefix & \":\" & mCount\n" +
            "    End Function\n" +
            "    Public Property Get Count()\n" +
            "        Count = mCount\n" +
            "    End Property\n" +
            "End Class\n" +
            "Dim c, d\n" +
            "Set c = New Counter\n" +
            "Set d = c\n" +
            "c.Increment\n" +
            "c.Increment\n" +
            "WScript.Echo c.Count, c.Describe(\"n\"), c Is d, TypeName(c), IsObject(c)",
            host);

        Run(session);

        Assert.Equal(new[] { "2", "n:2", "True", "Counter", "True" }, host.Lines);
    }

    [Fact]
    public void PropertyGetLetRoundTripAndMeInsideAccessors()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Vec\n" +
            "    Public X, Y\n" +
            "    Public Property Get Length()\n" +
            "        Length = Sqr(Me.X * Me.X + Me.Y * Me.Y)\n" +
            "    End Property\n" +
            "End Class\n" +
            "Class Temperature\n" +
            "    Private mCelsius\n" +
            "    Public Property Get Celsius()\n" +
            "        Celsius = mCelsius\n" +
            "    End Property\n" +
            "    Public Property Let Celsius(value)\n" +
            "        If value < -273 Then Err.Raise 5, \"Temp\", \"below absolute zero\"\n" +
            "        mCelsius = value\n" +
            "    End Property\n" +
            "End Class\n" +
            "Dim v, t\n" +
            "Set v = New Vec\n" +
            "v.X = 3\n" +
            "v.Y = 4\n" +
            "Set t = New Temperature\n" +
            "t.Celsius = 25\n" +
            "WScript.Echo v.Length, t.Celsius",
            host);

        Run(session);

        Assert.Equal(new[] { "5", "25" }, host.Lines);

        var failHost = new RecordingHost();
        var failSession = CreateSession(
            "Class Temperature\n" +
            "    Private mCelsius\n" +
            "    Public Property Let Celsius(value)\n" +
            "        If value < -273 Then Err.Raise 5, \"Temp\", \"below absolute zero\"\n" +
            "        mCelsius = value\n" +
            "    End Property\n" +
            "End Class\n" +
            "Dim t\n" +
            "Set t = New Temperature\n" +
            "t.Celsius = -300",
            failHost);
        Assert.False(failSession.TryRun(out var error));
        Assert.Equal(5, error?.Number);
        Assert.Equal("below absolute zero", error?.Description);
    }

    [Fact]
    public void PropertySetReceivesObjectReference()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Point\n" +
            "    Public X\n" +
            "End Class\n" +
            "Class Holder\n" +
            "    Private mObj\n" +
            "    Private Sub Class_Initialize()\n" +
            "        Set mObj = Nothing\n" +
            "    End Sub\n" +
            "    Public Property Get Item()\n" +
            "        Set Item = mObj\n" +
            "    End Property\n" +
            "    Public Property Set Item(o)\n" +
            "        Set mObj = o\n" +
            "    End Property\n" +
            "    Public Function HasValue()\n" +
            "        HasValue = Not mObj Is Nothing\n" +
            "    End Function\n" +
            "End Class\n" +
            "Dim h, p, q\n" +
            "Set h = New Holder\n" +
            "WScript.Echo h.HasValue\n" +
            "Set p = New Point\n" +
            "Set h.Item = p\n" +
            "Set q = h.Item\n" +
            "WScript.Echo h.HasValue, q Is p, q.X",
            host);

        Run(session);

        Assert.Equal(new[] { "False", "True", "True", "" }, host.Lines);
    }

    [Fact]
    public void IndexedPropertyGetAndLet()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Stack\n" +
            "    Private mItems()\n" +
            "    Private mTop\n" +
            "    Private Sub Class_Initialize()\n" +
            "        mTop = -1\n" +
            "        ReDim mItems(9)\n" +
            "    End Sub\n" +
            "    Public Sub Push(v)\n" +
            "        mTop = mTop + 1\n" +
            "        If mTop > UBound(mItems) Then ReDim Preserve mItems(mTop + 9)\n" +
            "        mItems(mTop) = v\n" +
            "    End Sub\n" +
            "    Public Property Get Item(i)\n" +
            "        Item = mItems(i)\n" +
            "    End Property\n" +
            "    Public Property Let Item(i, v)\n" +
            "        mItems(i) = v\n" +
            "    End Property\n" +
            "    Public Property Get Top()\n" +
            "        Top = mTop\n" +
            "    End Property\n" +
            "End Class\n" +
            "Dim s\n" +
            "Set s = New Stack\n" +
            "s.Push 10\n" +
            "s.Push 20\n" +
            "s.Item(0) = 99\n" +
            "WScript.Echo s.Item(0), s.Item(1), s.Top",
            host);

        Run(session);

        Assert.Equal(new[] { "99", "20", "1" }, host.Lines);
    }

    [Fact]
    public void ExitPropertyLeavesTheAccessorEarly()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Sign\n" +
            "    Public Property Get Of(v)\n" +
            "        If v > 0 Then\n" +
            "            Of = \"pos\"\n" +
            "            Exit Property\n" +
            "        End If\n" +
            "        Of = \"nonpos\"\n" +
            "    End Property\n" +
            "End Class\n" +
            "Dim g\n" +
            "Set g = New Sign\n" +
            "WScript.Echo g.Of(5), g.Of(-5)",
            host);

        Run(session);

        Assert.Equal(new[] { "pos", "nonpos" }, host.Lines);
    }

    [Fact]
    public void ClassInitializeRunsDuringNew()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Widget\n" +
            "    Public Name\n" +
            "    Public Log\n" +
            "    Private Sub Class_Initialize()\n" +
            "        Name = \"widget\"\n" +
            "        Log = \"created:\" & Name\n" +
            "    End Sub\n" +
            "End Class\n" +
            "Dim w\n" +
            "Set w = New Widget\n" +
            "WScript.Echo w.Log, w.Name",
            host);

        Run(session);

        Assert.Equal(new[] { "created:widget", "widget" }, host.Lines);
    }

    [Fact]
    public void ClassTerminateRunsAtSessionTeardown()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Canary\n" +
            "    Private Sub Class_Terminate()\n" +
            "        TermCount = TermCount + 1\n" +
            "    End Sub\n" +
            "End Class\n" +
            "Dim TermCount\n" +
            "Dim a, b\n" +
            "Set a = New Canary\n" +
            "Set b = New Canary\n" +
            "WScript.Echo TermCount",
            host);

        Run(session);
        Assert.Equal(new[] { "" }, host.Lines);

        session.Terminate();
        Assert.True(session.TryRun("WScript.Echo TermCount", out var error), error?.ToString());
        Assert.Equal(new[] { "", "2" }, host.Lines);
    }

    [Fact]
    public void PrivateMembersAreInvisibleOutsideTheClass()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Secret\n" +
            "    Private Hidden\n" +
            "    Public Sub Seed()\n" +
            "        Hidden = 42\n" +
            "    End Sub\n" +
            "    Public Function Read()\n" +
            "        Read = Hidden\n" +
            "    End Function\n" +
            "End Class\n" +
            "Dim s\n" +
            "Set s = New Secret\n" +
            "s.Seed\n" +
            "On Error Resume Next\n" +
            "x = s.Hidden\n" +
            "WScript.Echo Err.Number\n" +
            "On Error GoTo 0\n" +
            "WScript.Echo s.Read()",
            host);

        Run(session);

        Assert.Equal(new[] { "438", "42" }, host.Lines);
    }

    [Fact]
    public void ClassesAreSharedAcrossScriptBlocks()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Class Point\n" +
            "    Public X\n" +
            "End Class",
            host);

        Run(session);
        Assert.True(session.TryRun(
            "Dim p\nSet p = New Point\np.X = 7\nWScript.Echo p.X", out var error),
            error?.ToString());

        Assert.Equal(new[] { "7" }, host.Lines);
    }

    [Fact]
    public void DuplicateClassMembersAreCompileError1041()
    {
        Assert.False(VbsEngine.TryCompile(
            "Class C\nPublic X\nPrivate X\nEnd Class", out _, out var fieldDup));
        Assert.Equal(1041, fieldDup?.Number);

        Assert.False(VbsEngine.TryCompile(
            "Class C\nPublic Property Get V()\nEnd Property\nPublic Function V()\nEnd Function\nEnd Class",
            out _, out var mixedDup));
        Assert.Equal(1041, mixedDup?.Number);
    }

    [Fact]
    public void PublicPrivateOnScriptLevelProceduresParses()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Public Sub Hello()\n" +
            "    WScript.Echo \"hello\"\n" +
            "End Sub\n" +
            "Private Function Add(a, b)\n" +
            "    Add = a + b\n" +
            "End Function\n" +
            "Hello\n" +
            "WScript.Echo Add(1, 2)",
            host);

        Run(session);

        Assert.Equal(new[] { "hello", "3" }, host.Lines);
    }

    // ── Array / Filter / RGB ─────────────────────────────────────────────────

    [Fact]
    public void ArrayFilterAndRgbBuiltins()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim a, f\n" +
            "a = Array(1, 2, 3)\n" +
            "WScript.Echo UBound(a), a(0), a(2)\n" +
            "f = Filter(Array(\"apple\", \"banana\", \"cherry\"), \"an\")\n" +
            "WScript.Echo UBound(f), Join(f, \";\")\n" +
            "f = Filter(Array(\"apple\", \"banana\", \"cherry\"), \"AN\", True, vbTextCompare)\n" +
            "WScript.Echo Join(f, \",\")\n" +
            "f = Filter(Array(\"apple\", \"banana\"), \"an\", False)\n" +
            "WScript.Echo Join(f, \",\")\n" +
            "f = Filter(Array(\"x\", \"y\"), \"zzz\")\n" +
            "WScript.Echo UBound(f)\n" +
            "WScript.Echo RGB(255, 0, 0), RGB(1, 2, 3), RGB(0, 0, 255)",
            host);

        Run(session);

        Assert.Equal(new[]
        {
            "2", "1", "3",
            "0", "banana",
            "banana",
            "apple",
            "-1",
            "255", "197121", "16711680"
        }, host.Lines);
    }

    // ── Eval / Execute / ExecuteGlobal ───────────────────────────────────────

    [Fact]
    public void EvalTreatsAssignmentAsComparisonAndUsesCallerScope()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim x\n" +
            "x = 5\n" +
            "WScript.Echo Eval(\"x = 5\"), Eval(\"x = 6\"), Eval(\"x + 1\")\n" +
            "Function DoubleEval(n)\n" +
            "    Dim q\n" +
            "    q = n * 2\n" +
            "    DoubleEval = Eval(\"q + 1\")\n" +
            "End Function\n" +
            "WScript.Echo DoubleEval(10)\n" +
            "Execute \"x = 6\"\n" +
            "WScript.Echo x",
            host);

        Run(session);

        Assert.Equal(new[] { "True", "False", "6", "21", "6" }, host.Lines);
    }

    [Fact]
    public void ExecuteRunsInCallerScopeWhileExecuteGlobalRunsInGlobalScope()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Sub Inner()\n" +
            "    Dim lv\n" +
            "    lv = 1\n" +
            "    Execute \"lv = lv + 41\"\n" +
            "    WScript.Echo lv\n" +
            "End Sub\n" +
            "Inner\n" +
            "Sub Outer()\n" +
            "    Dim marker\n" +
            "    marker = \"local\"\n" +
            "    ExecuteGlobal \"marker = \"\"global\"\"\"\n" +
            "End Sub\n" +
            "Dim marker\n" +
            "Call Outer()\n" +
            "WScript.Echo marker",
            host);

        Run(session);

        Assert.Equal(new[] { "42", "global" }, host.Lines);
    }

    [Fact]
    public void ExecuteDefinesCallableProceduresAndClassesGlobally()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Execute \"Function MadeUp(n)\" & vbCrLf & \"MadeUp = n * 3\" & vbCrLf & \"End Function\"\n" +
            "WScript.Echo MadeUp(4)\n" +
            "Execute \"Class Dyn\" & vbCrLf & \"Public V\" & vbCrLf & \"End Class\"\n" +
            "Dim d\n" +
            "Set d = New Dyn\n" +
            "d.V = 9\n" +
            "WScript.Echo d.V",
            host);

        Run(session);

        Assert.Equal(new[] { "12", "9" }, host.Lines);
    }

    // ── GetRef ───────────────────────────────────────────────────────────────

    [Fact]
    public void GetRefRoundTripAndUnknownName()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Function Twice(n)\n" +
            "    Twice = n * 2\n" +
            "End Function\n" +
            "Dim f\n" +
            "Set f = GetRef(\"Twice\")\n" +
            "WScript.Echo f(21), f(1) + f(2)\n" +
            "Sub Apply(fn, v)\n" +
            "    WScript.Echo fn(v)\n" +
            "End Sub\n" +
            "Apply GetRef(\"Twice\"), 5\n" +
            "On Error Resume Next\n" +
            "Set g = GetRef(\"DoesNotExist\")\n" +
            "WScript.Echo Err.Number",
            host);

        Run(session);

        Assert.Equal(new[] { "42", "6", "10", "5" }, host.Lines);
    }

    // ── Err.HelpFile / HelpContext ───────────────────────────────────────────

    [Fact]
    public void ErrHelpFileAndHelpContextReadWriteAndRaise()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Err.HelpFile = \"vbs5.chm\"\n" +
            "Err.HelpContext = 4242\n" +
            "WScript.Echo Err.HelpFile, Err.HelpContext\n" +
            "On Error Resume Next\n" +
            "Err.Raise 5, \"src\", \"desc\", \"custom.chm\", 77\n" +
            "WScript.Echo Err.HelpFile, Err.HelpContext, Err.Number\n" +
            "Err.Clear\n" +
            "WScript.Echo Err.HelpFile, Err.HelpContext, Err.Number",
            host);

        Run(session);

        Assert.Equal(new[]
        {
            "vbs5.chm", "4242",
            "custom.chm", "77", "5",
            "", "0", "0"
        }, host.Lines);
    }

    // ── RegExp ───────────────────────────────────────────────────────────────

    [Fact]
    public void RegExpViaNewSupportsTestReplaceExecute()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim re, ms, m, total\n" +
            "Set re = New RegExp\n" +
            "re.Pattern = \"\\d+\"\n" +
            "re.Global = True\n" +
            "WScript.Echo re.Test(\"ab12cd\"), re.Test(\"abc\")\n" +
            "WScript.Echo re.Replace(\"a1b22c\", \"X\")\n" +
            "Set ms = re.Execute(\"a1b22c333\")\n" +
            "WScript.Echo ms.Count\n" +
            "WScript.Echo ms(0).Value, ms(0).FirstIndex, ms(0).Length\n" +
            "WScript.Echo ms(2).Value, ms(2).FirstIndex, ms(2).Length\n" +
            "WScript.Echo ms.Item(1).Value\n" +
            "total = 0\n" +
            "For Each m In ms\n" +
            "    total = total + m.Length\n" +
            "Next\n" +
            "WScript.Echo total",
            host);

        Run(session);

        Assert.Equal(new[]
        {
            "True", "False",
            "aXbXc",
            "3",
            "1", "1", "1",
            "333", "6", "3",
            "22",
            "6"
        }, host.Lines);
    }

    [Fact]
    public void RegExpGlobalFlagAndIgnoreCase()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim re, ms\n" +
            "Set re = New RegExp\n" +
            "re.Pattern = \"abc\"\n" +
            "re.Global = False\n" +
            "WScript.Echo re.Replace(\"abcabc\", \"!\")\n" +
            "Set ms = re.Execute(\"abcabc\")\n" +
            "WScript.Echo ms.Count\n" +
            "re.IgnoreCase = True\n" +
            "WScript.Echo re.Test(\"ABC\")\n" +
            "re.IgnoreCase = False\n" +
            "WScript.Echo re.Test(\"ABC\")",
            host);

        Run(session);

        Assert.Equal(new[] { "!abc", "1", "True", "False" }, host.Lines);
    }

    [Fact]
    public void RegExpViaCreateObjectWhenHostOptsIn()
    {
        var host = new RegExpAllowingHost();
        var session = VbsSession.Create(source:
            "Dim re\n" +
            "Set re = CreateObject(\"VBScript.RegExp\")\n" +
            "re.Pattern = \"world\"\n" +
            "re.IgnoreCase = True\n" +
            "WScript.Echo re.Test(\"Hello World\")",
            host, new Dictionary<string, IVbsDispatchObject>
            {
                ["WScript"] = host.CreateWScriptObject()
            });

        Assert.True(session.TryRun(out var error), error?.ToString());
        Assert.Equal(new[] { "True" }, host.Lines);
    }

    [Fact]
    public void RegExpBadPatternRaisesError5017()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim re\n" +
            "Set re = New RegExp\n" +
            "re.Pattern = \"(unclosed\"\n" +
            "WScript.Echo re.Test(\"x\")",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(5017, error?.Number);
    }

    // ── Escape / Unescape ────────────────────────────────────────────────────

    [Fact]
    public void EscapeAndUnescapeFollowJScriptSemantics()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "WScript.Echo Escape(\"a b&c\")\n" +
            "WScript.Echo Unescape(\"a%20b%26c\")\n" +
            "WScript.Echo Escape(Chr(233))\n" +
            "WScript.Echo Unescape(\"%u00E9\") = Chr(233)\n" +
            "WScript.Echo Unescape(\"100%\")",
            host);

        Run(session);

        Assert.Equal(new[] { "a%20b%26c", "a b&c", "%E9", "True", "100%" }, host.Lines);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void NewOnUndefinedClassRaises424()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim x\nSet x = New NoSuchClass",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(424, error?.Number);
    }

    [Fact]
    public void MeOutsideClassIsACompileError()
    {
        Assert.False(VbsEngine.TryCompile("Sub S()\nx = Me.Name\nEnd Sub", out _, out var error));
        Assert.Equal(1002, error?.Number);
    }

    [Fact]
    public void WithNothingRaises91()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim x\nSet x = Nothing\nWith x\nWScript.Echo \"unreachable\"\nEnd With",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(91, error?.Number);
    }

    [Fact]
    public void UserProceduresShadowExecute()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Sub Execute(s)\n" +
            "    WScript.Echo \"shadow:\" & s\n" +
            "End Sub\n" +
            "Execute \"hi\"",
            host);

        Run(session);

        Assert.Equal(new[] { "shadow:hi" }, host.Lines);
    }

    [Fact]
    public void EmptyArrayAndEmptyEval()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim a\n" +
            "a = Array()\n" +
            "WScript.Echo UBound(a)\n" +
            "On Error Resume Next\n" +
            "x = Eval(\"\")\n" +
            "WScript.Echo Err.Number",
            host);

        Run(session);

        Assert.Equal(new[] { "-1", "1002" }, host.Lines);
    }

    // ── ScriptEngine* / Debug / Stop ─────────────────────────────────────────

    [Fact]
    public void ScriptEngineVersionSurfaceReportsVbScriptFive()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "WScript.Echo ScriptEngine(), ScriptEngineMajorVersion(), " +
            "ScriptEngineMinorVersion(), ScriptEngineBuildVersion()\n" +
            "WScript.Echo WScript.ScriptEngineMajorVersion, WScript.ScriptEngineBuildVersion",
            host);

        Run(session);

        Assert.Equal(new[] { "VBScript", "5", "0", "6325", "5", "6325" }, host.Lines);
    }

    [Fact]
    public void DebugWriteBuffersAndWriteLineFlushes()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Debug.Write \"ab\"\n" +
            "Debug.Write \"cd\"\n" +
            "Debug.WriteLine \"ef\"\n" +
            "Debug.WriteLine \"gh\"\n" +
            "Stop\n" +
            "WScript.Echo \"after stop\"",
            host);

        Run(session);

        Assert.Equal(new[] { "abcdef", "gh", "after stop" }, host.Lines);
    }
}
