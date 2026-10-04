using System;
using System.Collections.Generic;
using System.Threading;

namespace Retro96.Engine.Vbs;

/// <summary>
/// The dispatch surface every host object must implement — property get/set,
/// method invoke, default property, and For Each enumeration. All member
/// names arrive exactly as written; implementations match case-insensitively.
/// </summary>
public interface IVbsDispatchObject
{
    string VbsTypeName { get; }

    bool TryGetMember(string name, out VbsVariant value);
    bool TrySetMember(string name, VbsVariant value);
    bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result);

    /// <summary>Default property (implicit Let / `obj(x)` / `CStr(obj)`).</summary>
    bool TryGetDefault(out VbsVariant value);
    bool TrySetDefault(VbsVariant value);
    bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result);

    /// <summary>For Each over the object; false when not enumerable.</summary>
    bool TryEnumerate(out IEnumerable<VbsVariant> items);
}

/// <summary>
/// The VBScript Err object: Number / Description / Source / HelpFile /
/// HelpContext, Raise and Clear. The interpreter writes the last error into
/// it; scripts read and raise.
/// </summary>
public sealed class VbsErrObject : IVbsDispatchObject
{
    public int Number { get; set; }
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>Help file associated with the error (read/write).</summary>
    public string HelpFile { get; set; } = "";
    /// <summary>Help context ID associated with the error (read/write).</summary>
    public int HelpContext { get; set; }

    public void Clear()
    {
        Number = 0;
        Description = "";
        Source = "";
        HelpFile = "";
        HelpContext = 0;
    }

    public string VbsTypeName => "ErrObject";

    public bool TryGetMember(string name, out VbsVariant value)
    {
        switch (name.ToLowerInvariant())
        {
            case "number": value = VbsVariant.FromLong(Number); return true;
            case "description": value = VbsVariant.Of(Description); return true;
            case "source": value = VbsVariant.Of(Source); return true;
            case "helpfile": value = VbsVariant.Of(HelpFile); return true;
            case "helpcontext": value = VbsVariant.FromLong(HelpContext); return true;
        }
        value = default;
        return false;
    }

    public bool TrySetMember(string name, VbsVariant value)
    {
        switch (name.ToLowerInvariant())
        {
            case "number": Number = checked((int)value.ToLongMath()); return true;
            case "description": Description = value.ToStringVariant(); return true;
            case "source": Source = value.ToStringVariant(); return true;
            case "helpfile": HelpFile = value.ToStringVariant(); return true;
            case "helpcontext": HelpContext = checked((int)value.ToLongMath()); return true;
        }
        return false;
    }

    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    {
        result = VbsVariant.Empty;
        switch (name.ToLowerInvariant())
        {
            case "clear":
                Clear();
                return true;

            // Raise number, source, description, helpfile, helpcontext
            case "raise":
                if (args.Length == 0)
                    throw new VbsRuntimeException(VbsErrorNumbers.InvalidProcedureCall,
                        "Invalid procedure call or argument");
                int number = checked((int)args[0].ToLongMath());
                string source = args.Length > 1 ? args[1].ToStringVariant() : "";
                string desc = args.Length > 2 && args[2].Type != VbVarType.Empty
                    ? args[2].ToStringVariant()
                    : VbsErrorNumbers.Describe(number);
                string helpFile = args.Length > 3 && args[3].Type != VbVarType.Empty
                    ? args[3].ToStringVariant() : "";
                int helpContext = args.Length > 4 && args[4].Type != VbVarType.Empty
                    ? checked((int)args[4].ToLongMath()) : 0;
                Number = number;
                Description = desc;
                Source = source;
                HelpFile = helpFile;
                HelpContext = helpContext;
                throw new VbsRuntimeException(number, desc, errSource: source);
        }
        return false;
    }

    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;
    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }
    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    { items = Array.Empty<VbsVariant>(); return false; }
}

// ── Host abstraction ────────────────────────────────────────────────────────

public enum VbsMsgBoxButtons
{
    OkOnly = 0, OkCancel = 1, AbortRetryIgnore = 2, YesNoCancel = 3,
    YesNo = 4, RetryCancel = 5
}

public enum VbsMsgBoxResult
{
    Ok = 1, Cancel = 2, Abort = 3, Retry = 4, Ignore = 5, Yes = 6, No = 7
}

/// <summary>
/// Host interface: output, dialogs (MsgBox/InputBox), and object creation.
/// The engine is sandboxed by default — CreateObject returns null unless the
/// host opts in, which surfaces as error 429.
/// </summary>
public interface IVbsScriptHost
{
    void WriteLine(string text);
    VbsMsgBoxResult MsgBox(string prompt, VbsMsgBoxButtons buttons, string title);
    string? InputBox(string prompt, string title, string defaultValue);
    IVbsDispatchObject? CreateObject(string progId);
    IVbsDispatchObject? GetObject(string path, string progId);
}

/// <summary>
/// Default sandboxed host: output via event, MsgBox auto-answers Ok,
/// InputBox returns "" (cancel), all object creation denied.
/// </summary>
public class SandboxedVbsHost : IVbsScriptHost
{
    public event Action<string>? Output;

    public virtual void WriteLine(string text) => Output?.Invoke(text);

    public virtual VbsMsgBoxResult MsgBox(string prompt, VbsMsgBoxButtons buttons, string title) =>
        VbsMsgBoxResult.Ok;

    public virtual string? InputBox(string prompt, string title, string defaultValue) => "";

    public virtual IVbsDispatchObject? CreateObject(string progId) => null;

    public virtual IVbsDispatchObject? GetObject(string path, string progId) => null;
}

/// <summary>WScript.Quit(code) terminates the script cleanly.</summary>
public sealed class VbsQuitException : Exception
{
    public int ExitCode { get; }
    public VbsQuitException(int exitCode) : base("WScript.Quit") => ExitCode = exitCode;
}

/// <summary>
/// GetRef("procname") — a callable reference to a script procedure. Invoking
/// the object (default dispatch) calls the procedure; a Function's result
/// comes back. Callable via `f(args)`, `f arg`, `Call f(args)` and passable
/// as an argument to other procedures.
/// </summary>
public sealed class VbsGetRefObject : IVbsDispatchObject
{
    private readonly VbsInterpreter _interpreter;
    private readonly VbsProcedure _proc;

    internal VbsGetRefObject(VbsInterpreter interpreter, VbsProcedure proc)
    {
        _interpreter = interpreter;
        _proc = proc;
    }

    /// <summary>The referenced procedure's name.</summary>
    public string ProcedureName => _proc.Name;

    public string VbsTypeName => "Function";

    public bool TryGetMember(string name, out VbsVariant value)
    {
        if (name.ToLowerInvariant() == "name")
        {
            value = VbsVariant.Of(_proc.Name);
            return true;
        }
        value = default;
        return false;
    }

    public bool TrySetMember(string name, VbsVariant value) => false;

    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }

    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;

    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    {
        result = _interpreter.CallProcedure(_proc.Name, args);
        return true;
    }

    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    { items = Array.Empty<VbsVariant>(); return false; }
}

/// <summary>
/// The VBScript Debug object: Write appends without a newline, WriteLine
/// flushes the pending buffer plus the text as one host output line (no
/// debugger attach — hosts route WriteLine to the console/document).
/// Debug.Print is aliased to WriteLine, classic-VB style.
/// </summary>
public sealed class VbsDebugObject : IVbsDispatchObject
{
    private readonly IVbsScriptHost _host;
    private readonly System.Text.StringBuilder _pending = new();

    internal VbsDebugObject(IVbsScriptHost host) => _host = host;

    public string VbsTypeName => "Debug";

    private static string ArgText(VbsVariant[] args) =>
        args.Length == 0 ? "" :
        args[0].Type == VbVarType.Null ? "" : args[0].ToStringVariant();

    public bool TryGetMember(string name, out VbsVariant value)
    { value = default; return false; }

    public bool TrySetMember(string name, VbsVariant value) => false;

    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    {
        result = VbsVariant.Empty;
        switch (name.ToLowerInvariant())
        {
            case "write":
                _pending.Append(ArgText(args));
                return true;
            case "writeline":
            case "print":   // Debug.Print = WriteLine in classic VB
                _pending.Append(ArgText(args));
                _host.WriteLine(_pending.ToString());
                _pending.Clear();
                return true;
        }
        return false;
    }

    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;
    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }
    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    { items = Array.Empty<VbsVariant>(); return false; }
}

/// <summary>
/// Optional WSH-style adapter: exposes a `WScript` object with Echo, Quit,
/// Sleep and the ScriptEngine* probes, routed through the host.
/// </summary>
public class WshStyleVbsHost : SandboxedVbsHost
{
    public IVbsDispatchObject CreateWScriptObject() => new WshScriptObject(this);

    private sealed class WshScriptObject : IVbsDispatchObject
    {
        private readonly IVbsScriptHost _host;
        public WshScriptObject(IVbsScriptHost host) => _host = host;

        public string VbsTypeName => "WScript";

        public bool TryGetMember(string name, out VbsVariant value)
        {
            switch (name.ToLowerInvariant())
            {
                case "scriptengine": value = VbsVariant.Of("VBScript"); return true;
                case "scriptenginemajorversion": value = VbsVariant.Of(VbsBuiltins.EngineMajor); return true;
                case "scriptengineminorversion": value = VbsVariant.Of(VbsBuiltins.EngineMinor); return true;
                case "scriptenginebuildversion": value = VbsVariant.Of(VbsBuiltins.EngineBuild); return true;
            }
            value = default;
            return false;
        }

        public bool TrySetMember(string name, VbsVariant value) => false;

        public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
        {
            result = VbsVariant.Empty;
            switch (name.ToLowerInvariant())
            {
                case "echo":
                    foreach (var a in args)
                        _host.WriteLine(a.Type == VbVarType.Null ? "" : a.ToStringVariant());
                    return true;

                case "quit":
                    int code = args.Length > 0 ? checked((int)args[0].ToLongMath()) : 0;
                    throw new VbsQuitException(code);

                case "sleep":
                    if (args.Length > 0)
                    {
                        int ms = checked((int)args[0].ToLongMath());
                        Thread.Sleep(Math.Clamp(ms, 0, 10_000));
                    }
                    return true;
            }
            return false;
        }

        public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
        public bool TrySetDefault(VbsVariant value) => false;
        public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
        { result = default; return false; }
        public bool TryEnumerate(out IEnumerable<VbsVariant> items)
        { items = Array.Empty<VbsVariant>(); return false; }
    }
}