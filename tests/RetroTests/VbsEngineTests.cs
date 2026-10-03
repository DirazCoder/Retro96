using System.Globalization;
using Retro96.Engine.Vbs;
using Xunit;

namespace RetroTests;

public sealed class VbsEngineTests
{
    private sealed class RecordingHost : WshStyleVbsHost
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Messages = new();
        public override void WriteLine(string text) => Lines.Add(text);
        public override VbsMsgBoxResult MsgBox(string prompt, VbsMsgBoxButtons buttons, string title)
        {
            Messages.Add(prompt);
            return VbsMsgBoxResult.Ok;
        }
    }

    private sealed class TestDispatchObject : IVbsDispatchObject
    {
        private readonly Dictionary<string, VbsVariant> _members =
            new(StringComparer.OrdinalIgnoreCase);
        public string VbsTypeName => "TestObject";
        public void Add(string name, IVbsDispatchObject value) =>
            _members[name] = VbsVariant.Of(value);
        public bool TryGetMember(string name, out VbsVariant value) =>
            _members.TryGetValue(name, out value);
        public bool TrySetMember(string name, VbsVariant value)
        {
            _members[name] = value;
            return true;
        }
        public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
        { result = default; return false; }
        public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
        public bool TrySetDefault(VbsVariant value) => false;
        public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
        { result = default; return false; }
        public bool TryEnumerate(out IEnumerable<VbsVariant> items)
        { items = Array.Empty<VbsVariant>(); return false; }
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

    [Fact]
    public void UsesBankersRoundingForCIntAndRound()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "WScript.Echo CInt(0.5), CInt(1.5), CInt(2.5), CInt(-0.5), Round(2.5), Round(2.35, 1)",
            host);

        Run(session);

        Assert.Equal(new[] { "0", "2", "2", "0", "2", "2.4" }, host.Lines);
    }

    [Fact]
    public void IntegerArithmeticPromotesBeforeOverflowAndFormatNumberUsesCultureDefaults()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var host = new RecordingHost();
            var session = CreateSession(
                "WScript.Echo CInt(32767) + 1, CLng(32767) + 1\n" +
                "WScript.Echo FormatNumber(1234.5)",
                host);

            Run(session);

            Assert.Equal(new[] { "32768", "32768", "1,234.50" }, host.Lines);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void LaterBlocksShareGlobalsAndEarlierProcedures()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim sharedValue\nsharedValue = 40\nFunction AddTwo(x)\nAddTwo = x + 2\nEnd Function",
            host);

        Run(session);
        Assert.True(session.TryRun("WScript.Echo AddTwo(sharedValue)", out var error), error?.ToString());

        Assert.Equal(new[] { "42" }, host.Lines);
    }

    [Fact]
    public void NamedDocumentFormControlsWorkFromVbScriptProcedure()
    {
        var host = new RecordingHost();
        var nameField = new TestDispatchObject();
        nameField.TrySetMember("Value", VbsVariant.Of("IE Explorer"));
        var resultField = new TestDispatchObject();
        resultField.TrySetMember("Value", VbsVariant.Of(""));
        var form = new TestDispatchObject();
        form.Add("TxtName", nameField);
        form.Add("TxtResult", resultField);
        var document = new TestDispatchObject();
        document.Add("TestForm", form);
        var source =
            "Sub RunVBSTest()\n" +
            "Dim userName, currentTime\n" +
            "userName = Document.TestForm.TxtName.Value\n" +
            "If Trim(userName) = \"\" Then\n" +
            "MsgBox \"Please enter your name first!\", 48, \"Input Required\"\n" +
            "Else\n" +
            "currentTime = Time()\n" +
            "MsgBox \"Hello \" & userName & \"!\" & vbCrLf & " +
            "\"VBScript is working correctly.\" & vbCrLf & " +
            "\"Current Time: \" & currentTime, 64, \"VBScript Test Success\"\n" +
            "Document.TestForm.TxtResult.Value = \"VBScript Executed at \" & currentTime\n" +
            "End If\nEnd Sub";
        Assert.True(VbsEngine.TryCompile(source, out _, out var compileError),
            compileError?.ToString());
        var session = VbsSession.Create(
            source,
            host,
            new Dictionary<string, IVbsDispatchObject> { ["Document"] = document });

        Run(session);
        Assert.True(session.HasProcedure("RunVBSTest"));
        session.Call("RunVBSTest");

        Assert.StartsWith("Hello IE Explorer!", Assert.Single(host.Messages));
        Assert.StartsWith("VBScript Executed at ", resultField.TryGetMember("Value", out var value)
            ? value.ToStringVariant()
            : "");
    }

    [Fact]
    public void ByRefCallFormsPreserveTheParenthesesQuirk()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Sub Bump(x)\nx = x + 1\nEnd Sub\n" +
            "Dim y\ny = 1\nBump (y)\nWScript.Echo y\nBump y\nWScript.Echo y\nCall Bump(y)\nWScript.Echo y",
            host);

        Run(session);

        Assert.Equal(new[] { "1", "2", "3" }, host.Lines);
    }

    [Fact]
    public void ParenthesizedFirstArgumentDoesNotForceLaterArgumentsByVal()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Sub SetPair(a, b)\na = a + 1\nb = b + 1\nEnd Sub\n" +
            "Dim x, y\nx = 1\ny = 10\nSetPair (x), y\nWScript.Echo x, y",
            host);

        Run(session);

        Assert.Equal(new[] { "1", "11" }, host.Lines);
    }

    [Fact]
    public void SubCallsWithMultipleParenthesizedArgumentsUseCompileError1044()
    {
        Assert.False(VbsEngine.TryCompile("Sub S(a, b)\nEnd Sub\nS(1, 2)", out _, out var error));
        Assert.Equal(1044, error?.Number);
    }

    [Fact]
    public void DuplicateProcedureNamesUseCompileError1041()
    {
        Assert.False(VbsEngine.TryCompile(
            "Sub S()\nEnd Sub\nFunction S()\nEnd Function", out _, out var error));
        Assert.Equal(1041, error?.Number);
    }

    [Fact]
    public void ForBoundsAreEvaluatedOnceAndLoopVariableKeepsOvershoot()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim i, values()\nReDim values(5)\nFor i = 1 To UBound(values)\n" +
            "If i = 2 Then ReDim values(1)\nNext\nWScript.Echo i",
            host);

        Run(session);

        Assert.Equal(new[] { "6" }, host.Lines);
    }

    [Fact]
    public void OnErrorResumeNextIsResetAtProcedureEntryAndErrPersists()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "On Error Resume Next\nFails\nWScript.Echo Err.Number\n" +
            "Sub Fails()\nOn Error GoTo 0\nDim x\nx = 1 / 0\nEnd Sub",
            host);

        Run(session);

        Assert.Equal(new[] { "11" }, host.Lines);
    }

    [Fact]
    public void OnErrorGoToZeroRestoresThrowingWithinTheCurrentProcedure()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Sub Fails()\nOn Error Resume Next\nDim x\nx = 1 / 0\n" +
            "On Error GoTo 0\nx = 1 / 0\nEnd Sub\nFails",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(VbsErrorNumbers.DivisionByZero, error?.Number);
    }

    [Fact]
    public void ErrRaisePreservesNumberAndDescription()
    {
        var host = new RecordingHost();
        var session = CreateSession("Err.Raise 5", host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(5, error?.Number);
        Assert.Equal(VbsErrorNumbers.Describe(5), error?.Description);
        Assert.Equal(5, session.Err.Number);
        Assert.Equal(error?.Description, session.Err.Description);

        var customSession = CreateSession(
            "Err.Raise 5, \"test source\", \"custom description\"", host);
        Assert.False(customSession.TryRun(out var customError));
        Assert.Equal(5, customError?.Number);
        Assert.Equal("custom description", customError?.Description);
        Assert.Equal("custom description", customSession.Err.Description);
    }

    [Fact]
    public void ErrRaiseSupportsVbObjectErrorRange()
    {
        var host = new RecordingHost();
        var session = CreateSession("Err.Raise vbObjectError + 1", host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(unchecked((int)0x80040001), error?.Number);
        Assert.Equal(error?.Number, session.Err.Number);
    }

    [Fact]
    public void NullAndEmptyPropagateThroughOperatorsAndBuiltins()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "WScript.Echo IsNull(Null + 1), Empty + 1, Empty & \"x\", " +
            "IsNull(\"a\" & Null), IsNull(Left(\"abc\", Null)), " +
            "IsNull(Len(Null)), IsNullTest(Null), IsNumeric(Empty)\n" +
            "Function IsNullTest(v)\nIf v Then IsNullTest = \"true\" Else IsNullTest = \"false\"\nEnd Function",
            host);

        Run(session);

        Assert.Equal(new[] { "True", "1", "x", "True", "True", "True", "false", "True" }, host.Lines);
    }

    [Fact]
    public void StringComparisonsAreBinaryAndNumericStringsCompareAsText()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "WScript.Echo \"10\" < \"9\", 10 < 9, \"abc\" = \"ABC\"",
            host);

        Run(session);

        Assert.Equal(new[] { "True", "False", "False" }, host.Lines);
    }

    [Fact]
    public void FixedAndDynamicArraysEraseDifferently()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim fixed(5)\nfixed(0) = 7\nErase fixed\nWScript.Echo UBound(fixed), fixed(0)\n" +
            "Dim dynamic()\nReDim dynamic(2)\nErase dynamic\nWScript.Echo UBound(dynamic)",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(VbsErrorNumbers.SubscriptOutOfRange, error?.Number);
        Assert.Equal(new[] { "5", "" }, host.Lines);
    }

    [Fact]
    public void SelectCaseSupportsListsRangesRelationalCasesAndFallback()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim n\nn = 2\nSelect Case n\nCase 1, 2\nWScript.Echo \"list\"\n" +
            "Case Else\nWScript.Echo \"else\"\nEnd Select\n" +
            "n = 6\nSelect Case n\nCase Is >= 5\nWScript.Echo \"relational\"\n" +
            "Case 1 To 5\nWScript.Echo \"range\"\nCase Else\nWScript.Echo \"else\"\nEnd Select\n" +
            "n = 4\nSelect Case n\nCase 1 To 5\nWScript.Echo \"range\"\n" +
            "Case Else\nWScript.Echo \"else\"\nEnd Select",
            host);

        Run(session);

        Assert.Equal(new[] { "list", "relational", "range" }, host.Lines);
    }

    [Fact]
    public void ReDimPreserveCannotChangeNonLastDimension()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim values()\nReDim values(2, 2)\nReDim Preserve values(3, 2)",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(VbsErrorNumbers.ArrayIsFixedOrLocked, error?.Number);
    }

    [Fact]
    public void DateLiteralsAndWeekFunctionsUseVbScriptConventions()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var host = new RecordingHost();
            var session = CreateSession(
                "WScript.Echo CStr(#12/31/96#), CStr(#1:30 PM#), " +
                "CStr(#12/31/96 1:30:00#)\n" +
                "WScript.Echo DateDiff(\"w\", #1/1/2024#, #1/15/2024#), " +
                "DateDiff(\"ww\", #1/1/2024#, #1/15/2024#), " +
                "DatePart(\"ww\", #1/1/2024#), DatePart(\"ww\", #1/8/2024#)",
                host);

            Run(session);

            Assert.Equal(new[]
            {
                "12/31/1996", "1:30:00 PM", "12/31/1996 1:30:00 AM",
                "2", "2", "1", "2"
            }, host.Lines);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void InStrSupportsBothThreeArgumentSignatures()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "WScript.Echo InStr(1, \"abc\", \"b\"), InStr(\"AbC\", \"b\", 1)",
            host);

        Run(session);

        Assert.Equal(new[] { "2", "2" }, host.Lines);
    }

    [Fact]
    public void SplitEmptyHasEmptyBoundsAndJoinTreatsNullElementsAsEmpty()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Dim parts(), values()\nparts = Split(\"\")\n" +
            "WScript.Echo UBound(parts)\nReDim values(1)\n" +
            "values(0) = Null\nvalues(1) = \"x\"\nWScript.Echo Join(values, \",\")",
            host);

        Run(session);

        Assert.Equal(new[] { "-1", ",x" }, host.Lines);
    }

    [Fact]
    public void RecursionLimitRaisesError28()
    {
        var host = new RecordingHost();
        var session = CreateSession(
            "Sub Recurse()\nRecurse\nEnd Sub\nRecurse",
            host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(VbsErrorNumbers.OutOfStackSpace, error?.Number);
    }

    [Fact]
    public void ObjectCreationIsDeniedWithError429()
    {
        var host = new RecordingHost();
        var session = CreateSession("CreateObject(\"Scripting.FileSystemObject\")", host);

        Assert.False(session.TryRun(out var error));
        Assert.Equal(VbsErrorNumbers.ActiveXCantCreateObject, error?.Number);
    }
}
