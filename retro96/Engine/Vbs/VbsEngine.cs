using System;
using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

/// <summary>
/// A normalized error report: the real VBScript number, description, source
/// ("Microsoft VBScript runtime error" / "…compilation error") and line.
/// </summary>
public sealed record VbsErrorInfo(int Number, string Description, string Source, int Line)
{
    public override string ToString()
    {
        string where = Line > 0 ? $" (line {Line})" : "";
        return $"{Source}: error '{Number}': {Description}{where}";
    }
}

/// <summary>
/// Facade over the VBScript 5.0 engine: compile, run, and error mapping.
/// Typical browser wiring:
///   1. `IVbsScriptHost` implemented by the shell (MsgBox → canvas dialog,
///      WriteLine → document.write / console, CreateObject → denied).
///   2. Reuse one `VbsSession` per document and call `Run(source)` for each
///      &lt;script language="VBScript"&gt; block; globals, procedures, classes and
///      instances are shared. Dispose/Terminate at document teardown runs
///      Class_Terminate for every live instance.
/// </summary>
public static class VbsEngine
{
    /// <summary>Compiles source; throws VbsSyntaxException (with line/column)
    /// on syntax errors.</summary>
    public static VbsScript Compile(string source) =>
        VbsParser.Parse(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Non-throwing compile; error is null on success.</summary>
    public static bool TryCompile(string source,
                                  out VbsScript? script, out VbsErrorInfo? error)
    {
        try
        {
            script = Compile(source);
            error = null;
            return true;
        }
        catch (VbsSyntaxException ex)
        {
            script = null;
            error = new VbsErrorInfo(ex.Number, ex.Message,
                "Microsoft VBScript compilation error", ex.Line);
            return false;
        }
    }

    public static VbsErrorInfo ToErrorInfo(VbsRuntimeException ex) =>
        new(ex.Number, ex.Description, ex.ErrSource, ex.Line);

    public static VbsErrorInfo ToErrorInfo(VbsSyntaxException ex) =>
        new(ex.Number, ex.Message, "Microsoft VBScript compilation error", ex.Line);

    /// <summary>Compile-and-run in one call. Runtime errors propagate as
    /// VbsRuntimeException; WScript.Quit propagates as VbsQuitException.</summary>
    public static void Run(string source, IVbsScriptHost host,
                           IReadOnlyDictionary<string, IVbsDispatchObject>? namedItems = null)
    {
        var script = Compile(source);
        var interpreter = new VbsInterpreter(script, host, namedItems);
        interpreter.Run();
    }
}

/// <summary>
/// One live script instance (globals + procedures + Err) bound to a host.
/// Reuse it across the page's VBScript blocks to keep global state; create
/// a fresh one per document.
/// </summary>
public sealed class VbsSession
{
    private readonly VbsInterpreter _interpreter;
    private readonly SandboxedVbsHost? _sandbox;

    /// <summary>The live Err object (Number/Description/Source already reflect
    /// the last error under On Error Resume Next).</summary>
    public VbsErrObject Err => _interpreter.Err;

    /// <summary>Output forwarding — only available when the host derives from
    /// SandboxedVbsHost (the default). Other hosts wire output themselves.</summary>
    public event Action<string>? Output
    {
        add { if (_sandbox != null) _sandbox.Output += value; }
        remove { if (_sandbox != null) _sandbox.Output -= value; }
    }

    public VbsSession(VbsScript script, IVbsScriptHost host,
                      IReadOnlyDictionary<string, IVbsDispatchObject>? namedItems = null)
    {
        _sandbox = host as SandboxedVbsHost;
        _interpreter = new VbsInterpreter(script, host, namedItems);
    }

    /// <summary>Compile-and-construct. Throws VbsSyntaxException on bad source.</summary>
    public static VbsSession Create(string source, IVbsScriptHost host,
                                     IReadOnlyDictionary<string, IVbsDispatchObject>? namedItems = null) =>
        new(VbsEngine.Compile(source), host, namedItems);

    /// <summary>True when the script defines a Sub/Function with this name
    /// (case-insensitive) — lets the shell probe event handlers.</summary>
    public bool HasProcedure(string name) => _interpreter.HasProcedure(name);

    /// <summary>Procedure names defined by this session, including later blocks.</summary>
    public IReadOnlyCollection<string> ProcedureNames => _interpreter.ProcedureNames;

    /// <summary>Runs the script body. Runtime errors throw VbsRuntimeException
    /// (already recorded in Err); WScript.Quit throws VbsQuitException.</summary>
    public void Run() => _interpreter.Run();

    /// <summary>Compiles and runs another block in this live script instance.</summary>
    public void Run(string source) => _interpreter.Run(VbsEngine.Compile(source));

    /// <summary>Invokes a script procedure by name with by-value arguments —
    /// the shell uses this to fire VBScript event handlers.</summary>
    public VbsVariant Call(string procedure, params VbsVariant[] args) =>
        _interpreter.CallProcedure(procedure, args);

    /// <summary>Non-throwing run: any runtime error becomes VbsErrorInfo.
    /// WScript.Quit counts as a clean exit (code in exitCode).</summary>
    public bool TryRun(out VbsErrorInfo? error, out int exitCode)
    {
        exitCode = 0;
        try
        {
            _interpreter.Run();
            error = null;
            return true;
        }
        catch (VbsQuitException quit)
        {
            exitCode = quit.ExitCode;
            error = null;
            return true;
        }
        catch (VbsRuntimeException ex)
        {
            error = VbsEngine.ToErrorInfo(ex);
            return false;
        }
        catch (VbsSyntaxException ex)
        {
            error = VbsEngine.ToErrorInfo(ex);
            return false;
        }
    }

    /// <summary>Compiles and runs another block, preserving this session's state.</summary>
    public bool TryRun(string source, out VbsErrorInfo? error)
    {
        try
        {
            _interpreter.Run(VbsEngine.Compile(source));
            error = null;
            return true;
        }
        catch (VbsRuntimeException ex)
        {
            error = VbsEngine.ToErrorInfo(ex);
            return false;
        }
        catch (VbsSyntaxException ex)
        {
            error = VbsEngine.ToErrorInfo(ex);
            return false;
        }
        catch (VbsQuitException)
        {
            error = null;
            return true;
        }
    }

    public bool TryRun(out VbsErrorInfo? error)
    {
        bool ok = TryRun(out error, out _);
        return ok && error == null;
    }

    /// <summary>
    /// Deterministic class teardown: runs Class_Terminate for every instance
    /// created by this session, newest first (errors inside destructors are
    /// swallowed). Real VBScript uses COM refcounting; this engine documents
    /// session-end teardown as its lifetime model — call this when the
    /// document goes away.
    /// </summary>
    public void Terminate() => _interpreter.TerminateClasses();

    /// <summary>Terminates class instances (see Terminate).</summary>
    public void Dispose()
    {
        Terminate();
        GC.SuppressFinalize(this);
    }
}