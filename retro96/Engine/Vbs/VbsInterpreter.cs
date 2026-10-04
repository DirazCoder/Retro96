using System;
using System.Collections.Generic;
using System.Linq;

namespace Retro96.Engine.Vbs;

/// <summary>Storage cell — ByRef parameters alias the caller's cell.</summary>
public sealed class VbsCell
{
    public VbsVariant Value;
    public VbsCell(VbsVariant value = default) => Value = value;
}

internal sealed class VbsFrame
{
    public Dictionary<string, VbsCell> Locals = new(StringComparer.OrdinalIgnoreCase);
    public bool IsGlobal;
    public bool ErrorResumeNext;
    public VbsCell? ReturnCell;
    /// <summary>Current class instance while executing class code (Me).</summary>
    public VbsClassInstance? Me;
    /// <summary>Objects of enclosing With blocks, outermost first. Unqualified
    /// `.Member` resolves against this stack, innermost first.</summary>
    public List<VbsVariant>? WithStack;
}

internal sealed class VbsExitProcedureException : Exception { }

internal sealed class VbsExitLoopException : Exception
{
    public readonly VbsExitKind Kind;
    public VbsExitLoopException(VbsExitKind kind) => Kind = kind;
}

public sealed class VbsProcedure
{
    public string Name { get; }
    public bool IsFunction { get; }
    public List<(string Name, bool ByVal)> Params { get; }
    public List<VbsStmt> Body { get; }

    public VbsProcedure(string name, bool isFunction,
                        List<(string, bool)> @params, List<VbsStmt> body)
    {
        Name = name;
        IsFunction = isFunction;
        Params = @params.Select(p => (p.Item1, p.Item2)).ToList();
        Body = body;
    }
}

/// <summary>
/// Tree-walking VBScript 5.0 runtime. Two scopes (script-global and
/// procedure-local) plus per-instance class fields visible to class code;
/// procedures and classes hoisted regardless of definition order, ByRef via
/// cell aliasing, On Error Resume Next scoped per procedure, recursion
/// capped (error 28). Adds With blocks (frame-local stack), classes with
/// Public/Private members, Property Get/Let/Set, Me, New, GetRef and the
/// Eval/Execute/ExecuteGlobal builtins (which call back into this object
/// through VbsRuntimeContext).
/// </summary>
public sealed class VbsInterpreter
{
    private const int MaxCallDepth = 300;

    private readonly VbsScript _script;
    private readonly Dictionary<string, VbsCell> _globals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VbsProcedure> _procedures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VbsClass> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VbsClassInstance> _liveInstances = new();
    private readonly IVbsScriptHost _host;
    private readonly VbsErrObject _err = new();
    private readonly Random _random = new();
    private readonly VbsRuntimeContext _ctx;
    private bool _optionExplicit;
    private int _callDepth;

    public VbsErrObject Err => _err;
    public IReadOnlyCollection<string> ProcedureNames => _procedures.Keys;

    public VbsInterpreter(VbsScript script, IVbsScriptHost host,
                          IReadOnlyDictionary<string, IVbsDispatchObject>? namedItems = null)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ctx = new VbsRuntimeContext(_host, _random, _err) { Interpreter = this };

        RegisterProcedures(_script);

        // Named host items (WScript, document bridges, …) become globals.
        if (namedItems != null)
            foreach (var (name, obj) in namedItems)
                _globals[name] = new VbsCell(VbsVariant.Of(obj));
    }

    public bool HasProcedure(string name) => _procedures.ContainsKey(name);

    /// <summary>Class names defined in this interpreter (incl. later blocks).</summary>
    public IReadOnlyCollection<string> ClassNames => _classes.Keys;

    /// <summary>Deterministic teardown: runs Class_Terminate for every live
    /// instance, newest first. Called by VbsSession.Terminate/Dispose — real
    /// VBScript uses COM refcounting; this engine documents session-end
    /// teardown as its lifetime model.</summary>
    public void TerminateClasses()
    {
        for (int i = _liveInstances.Count - 1; i >= 0; i--)
            _liveInstances[i].Terminate();
        _liveInstances.Clear();
    }

    // ── Entry points ───────────────────────────────────────────────────────

    public void Run() => Run(_script);

    /// <summary>
    /// Runs another script block in this interpreter. Globals, procedures,
    /// Err state, and Option Explicit remain shared with earlier blocks.
    /// </summary>
    public void Run(VbsScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!ReferenceEquals(script, _script))
            RegisterProcedures(script);

        var frame = new VbsFrame { Locals = _globals, IsGlobal = true };
        ExecuteStatementList(script.Body, frame);
    }

    private void RegisterProcedures(VbsScript script)
    {
        foreach (var stmt in script.Body.OfType<VbsSubStatement>())
        {
            if (!_procedures.TryAdd(stmt.Name, new VbsProcedure(stmt.Name, stmt.IsFunction,
                    stmt.Params.Select(p => (p.Name, p.ByVal)).ToList(), stmt.Body)))
                throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
                    $"Name redefined: '{stmt.Name}'", stmt.Line);
        }
        foreach (var stmt in script.Body.OfType<VbsClassStatement>())
        {
            if (_procedures.ContainsKey(stmt.Name) || _classes.ContainsKey(stmt.Name))
                throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
                    $"Name redefined: '{stmt.Name}'", stmt.Line);
            _classes[stmt.Name] = VbsClass.Build(stmt);
        }
    }

    /// <summary>Host-call a procedure by name with by-value arguments.</summary>
    public VbsVariant CallProcedure(string name, params VbsVariant[] args)
    {
        if (!_procedures.TryGetValue(name, out var proc))
            throw new VbsRuntimeException(VbsErrorNumbers.SubOrFunctionNotDefined,
                $"Sub or Function not defined: '{name}'");
        var argExprs = args.Select(a => (VbsExpr)new VbsLiteralExpr(0, a)).ToList();
        var frame = new VbsFrame { Locals = _globals, IsGlobal = true };
        return InvokeProcedure(proc, argExprs, frame, byrefAllowed: false);
    }

    internal void UpdateErr(VbsRuntimeException ex)
    {
        _err.Number = ex.Number;
        _err.Description = ex.Description;
        _err.Source = ex.ErrSource;
    }

    // ── Statements ──────────────────────────────────────────────────────────

    private void ExecuteStatementList(IReadOnlyList<VbsStmt> list, VbsFrame frame)
    {
        foreach (var stmt in list)
        {
            if (stmt is VbsSubStatement or VbsClassStatement) continue;   // hoisted at construction
            if (frame.ErrorResumeNext)
            {
                try { ExecuteStatement(stmt, frame); }
                catch (VbsRuntimeException ex) { UpdateErr(ex); }
            }
            else
            {
                ExecuteStatement(stmt, frame);
            }
        }
    }

    private void ExecuteStatement(VbsStmt stmt, VbsFrame f)
    {
        try { ExecuteStatementCore(stmt, f); }
        catch (VbsRuntimeException ex) when (ex.Line == 0)
        {
            throw new VbsRuntimeException(ex.Number, ex.Description, stmt.Line, ex.ErrSource);
        }
    }

    private void ExecuteStatementCore(VbsStmt stmt, VbsFrame f)
    {
        switch (stmt)
        {
            case VbsDimStatement dim: ExecDim(dim, f); break;
            case VbsConstStatement cst: ExecConst(cst, f); break;
            case VbsReDimStatement redim: ExecReDim(redim, f); break;
            case VbsEraseStatement erase: ExecErase(erase, f); break;
            case VbsAssignmentStatement assign: ExecAssignment(assign, f); break;
            case VbsCallStatement call: ExecCall(call, f); break;
            case VbsIfStatement ifs: ExecIf(ifs, f); break;
            case VbsSelectStatement sel: ExecSelect(sel, f); break;
            case VbsForStatement fors: ExecFor(fors, f); break;
            case VbsForEachStatement foreachs: ExecForEach(foreachs, f); break;
            case VbsDoLoopStatement dos: ExecDo(dos, f); break;
            case VbsWhileStatement whiles: ExecWhile(whiles, f); break;
            case VbsExitStatement exit: ExecExit(exit); break;
            case VbsOnErrorStatement onError: f.ErrorResumeNext = onError.ResumeNext; break;
            case VbsOptionExplicitStatement: _optionExplicit = true; break;
            case VbsWithStatement with: ExecWith(with, f); break;
            case VbsClassStatement: break;   // hoisted at construction
            case VbsNopStatement: break;   // Stop — no debugger halt
        }
    }

    private void ExecWith(VbsWithStatement stmt, VbsFrame f)
    {
        var objVal = EvalExpr(stmt.Object, f);
        if (objVal.Type != VbVarType.Object)
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired,
                "Object required", stmt.Line);
        var obj = objVal.AsObject() ??
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                "Object variable not set", stmt.Line);
        (f.WithStack ??= new List<VbsVariant>()).Add(objVal);
        try
        {
            ExecuteStatementList(stmt.Body, f);
        }
        finally
        {
            f.WithStack.RemoveAt(f.WithStack.Count - 1);
        }
    }

    private void ExecDim(VbsDimStatement stmt, VbsFrame f)
    {
        foreach (var decl in stmt.Decls)
        {
            if (f.Locals.ContainsKey(decl.Name))
                throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
                    $"Name redefined: '{decl.Name}'", stmt.Line);

            VbsVariant value;
            if (!decl.IsArray)
            {
                value = VbsVariant.Empty;
            }
            else if (decl.Bounds.Count == 0)
            {
                value = VbsVariant.Of(VbsArray.Unallocated());
            }
            else
            {
                var lens = EvaluateArrayLengths(decl.Bounds, f, stmt.Line);
                value = VbsVariant.Of(VbsArray.Allocate(false, lens));
            }
            f.Locals[decl.Name] = new VbsCell(value);
        }
    }

    private void ExecConst(VbsConstStatement stmt, VbsFrame f)
    {
        foreach (var decl in stmt.Decls)
        {
            if (f.Locals.ContainsKey(decl.Name))
                throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
                    $"Name redefined: '{decl.Name}'", stmt.Line);
            f.Locals[decl.Name] = new VbsCell(EvalExpr(decl.Value, f));
        }
    }

    private void ExecReDim(VbsReDimStatement stmt, VbsFrame f)
    {
        foreach (var decl in stmt.Decls)
        {
            var lens = EvaluateArrayLengths(decl.Bounds, f, stmt.Line);

            if (!TryFindCell(decl.Name, f, out var cell))
                cell = ResolveCellForWrite(decl.Name, f);

            if (cell.Value.Type == VbVarType.Array)
            {
                var arr = cell.Value.AsArray();
                if (!arr.IsDynamic)
                    throw new VbsRuntimeException(VbsErrorNumbers.ArrayIsFixedOrLocked,
                        "This array is fixed or temporarily locked", stmt.Line);
                cell.Value = VbsVariant.Of(
                    stmt.Preserve && !arr.Erased ? arr.RedimPreserve(lens) : arr.Redim(lens));
            }
            else if (cell.Value.Type == VbVarType.Empty)
            {
                cell.Value = VbsVariant.Of(VbsArray.Allocate(true, lens));
            }
            else
            {
                throw new VbsRuntimeException(VbsErrorNumbers.ArrayIsFixedOrLocked,
                    "This array is fixed or temporarily locked", stmt.Line);
            }
        }
    }

    private int[] EvaluateArrayLengths(IReadOnlyList<VbsExpr> bounds, VbsFrame f, int line)
    {
        var lengths = new int[bounds.Count];
        for (int i = 0; i < lengths.Length; i++)
        {
            long upperBound = EvalExpr(bounds[i], f).ToLongMath();
            if (upperBound < 0)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range", line);
            if (upperBound >= int.MaxValue)
                throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow", line);
            lengths[i] = (int)upperBound + 1;
        }
        return lengths;
    }

    /// <summary>Class field array bounds (`Public A(10)`), evaluated with a
    /// Me-bound construction frame.</summary>
    internal int[] EvaluateArrayLengths(IReadOnlyList<VbsExpr> bounds, VbsFrame f, string fieldName)
    {
        try { return EvaluateArrayLengths(bounds, f, 0); }
        catch (VbsRuntimeException ex)
        {
            throw new VbsRuntimeException(ex.Number, ex.Description, 0,
                $"Microsoft VBScript runtime error: class field '{fieldName}'");
        }
    }

    private void ExecErase(VbsEraseStatement stmt, VbsFrame f)
    {
        foreach (string name in stmt.Names)
        {
            if (!TryFindCell(name, f, out var cell))
            {
                if (_optionExplicit)
                    throw new VbsRuntimeException(VbsErrorNumbers.VariableNotDefined,
                        $"Variable is not defined: '{name}'", stmt.Line);
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch",
                    stmt.Line);
            }
            if (cell.Value.Type != VbVarType.Array)
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch",
                    stmt.Line);
            cell.Value.AsArray().Erase();
        }
    }

    private void ExecAssignment(VbsAssignmentStatement stmt, VbsFrame f)
    {
        if (stmt.IsSet)
        {
            var rhs = EvalExpr(stmt.Value, f);
            if (rhs.Type != VbVarType.Object)
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired,
                    "Object required", stmt.Line);
            AssignTarget(stmt.Target, rhs, isSet: true, f);
            return;
        }

        var value = EvalExpr(stmt.Value, f);
        if (value.Type == VbVarType.Object)
        {
            // Implicit Let on an object → its default property, else 438.
            if (!value.TryDefault(out var def))
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    "Object doesn't support this property or method", stmt.Line);
            value = def;
        }
        AssignTarget(stmt.Target, value, isSet: false, f);
    }

    private void AssignTarget(VbsExpr target, VbsVariant value, bool isSet, VbsFrame f)
    {
        switch (target)
        {
            case VbsNameExpr n:
            {
                var cell = ResolveCellForWrite(n.Name, f);
                if (cell.Value.Type == VbVarType.Array && value.Type != VbVarType.Array)
                    throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                        $"Type mismatch: '{n.Name}'");
                cell.Value = value;
                return;
            }

            case VbsMemberExpr m:
            {
                var objVal = EvalExpr(m.Object, f);
                if (objVal.Type != VbVarType.Object)
                    throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired,
                        "Object required");
                var obj = objVal.AsObject() ??
                    throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                        "Object variable not set");
                if (!obj.TrySetMember(m.Member, value))
                    throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                        $"Object doesn't support this property or method: '{m.Member}'");
                return;
            }

            case VbsWithMemberExpr wm:
            {
                foreach (var o in WithObjects(f))
                    if (o.TrySetMember(wm.Member, value))
                        return;
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    $"Object doesn't support this property or method: '{wm.Member}'");
            }

            // obj.Prop(i, …) = v / Set obj.Prop(i, …) = o → indexed Property
            // Let/Set (class instances; other objects fall through to 438).
            case VbsInvokeExpr inv when inv.Target is VbsMemberExpr m2:
            {
                var objVal = EvalExpr(m2.Object, f);
                if (objVal.Type == VbVarType.Object &&
                    objVal.AsObject() is IVbsIndexedPropertyAssign indexed)
                {
                    var indices = EvalArgs(inv.Args, f);
                    if (indexed.TryAssignIndexed(m2.Member, indices, value, isSet))
                        return;
                }
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    $"Object doesn't support this property or method: '{m2.Member}'");
            }

            case VbsInvokeExpr inv when inv.Target is VbsWithMemberExpr wm2:
            {
                var indices = EvalArgs(inv.Args, f);
                foreach (var o in WithObjects(f))
                    if (o is IVbsIndexedPropertyAssign indexed &&
                        indexed.TryAssignIndexed(wm2.Member, indices, value, isSet))
                        return;
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    $"Object doesn't support this property or method: '{wm2.Member}'");
            }

            case VbsInvokeExpr inv when !isSet && inv.Target is VbsNameExpr n2:
            {
                if (TryFindCell(n2.Name, f, out var cell))
                {
                    var argVals = EvalArgs(inv.Args, f);
                    if (cell.Value.Type == VbVarType.Array)
                    {
                        cell.Value.AsArray().Set(ToIndices(argVals), value);
                        return;
                    }
                    if (cell.Value.Type == VbVarType.Object)
                    {
                        var obj = cell.Value.AsObject() ??
                            throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                                "Object variable not set");
                        if (argVals.Length == 0 && obj.TrySetDefault(value)) return;
                    }
                }
                else if (_optionExplicit)
                {
                    throw new VbsRuntimeException(VbsErrorNumbers.VariableNotDefined,
                        $"Variable is not defined: '{n2.Name}'");
                }
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                    $"Type mismatch: '{n2.Name}'");
            }

            default:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        }
    }

    private void ExecCall(VbsCallStatement stmt, VbsFrame f)
    {
        if (stmt.Call is not VbsInvokeExpr inv)
            throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch",
                stmt.Line);
        EvalInvoke(inv, f, byrefAllowed: !stmt.ForceByVal);
    }

    private void ExecIf(VbsIfStatement stmt, VbsFrame f)
    {
        foreach (var branch in stmt.Branches)
        {
            if (EvalExpr(branch.Condition, f).IsConditionTrue())
            {
                ExecuteStatementList(branch.Body, f);
                return;
            }
        }
        if (stmt.ElseBody != null)
            ExecuteStatementList(stmt.ElseBody, f);
    }

    private void ExecSelect(VbsSelectStatement stmt, VbsFrame f)
    {
        var subject = EvalExpr(stmt.Subject, f);   // evaluated ONCE
        foreach (var c in stmt.Cases)
        {
            foreach (var item in c.Items)
            {
                if (CaseMatches(subject, item, f))
                {
                    ExecuteStatementList(c.Body, f);
                    return;
                }
            }
        }
        if (stmt.ElseBody != null)
            ExecuteStatementList(stmt.ElseBody, f);
    }

    private bool CaseMatches(VbsVariant subject, VbsSelectCaseItem item, VbsFrame f)
    {
        if (item.IsOperator != null)
        {
            var v = EvalExpr(item.Value, f);
            var r = VbsOps.Compare(subject, v, item.IsOperator);
            return r.Type == VbVarType.Boolean && r.AsBoolean();
        }
        if (item.ToHigh != null)
        {
            var lo = EvalExpr(item.Value, f);
            var hi = EvalExpr(item.ToHigh, f);
            var a = VbsOps.Compare(subject, lo, ">=");
            var b = VbsOps.Compare(subject, hi, "<=");
            return a.Type == VbVarType.Boolean && a.AsBoolean() &&
                   b.Type == VbVarType.Boolean && b.AsBoolean();
        }
        var plain = EvalExpr(item.Value, f);
        var eq = VbsOps.Compare(subject, plain, "=");
        return eq.Type == VbVarType.Boolean && eq.AsBoolean();
    }

    private void ExecFor(VbsForStatement stmt, VbsFrame f)
    {
        var cell = ResolveCellForWrite(stmt.Variable, f);
        var from = EvalExpr(stmt.From, f);          // bounds evaluated ONCE
        var to = EvalExpr(stmt.To, f);
        var step = stmt.Step != null ? EvalExpr(stmt.Step, f) : VbsVariant.Of((short)1);
        double stepNum = step.ToDoubleMath();
        string cmp = stepNum >= 0 ? "<=" : ">=";

        cell.Value = from;
        while (true)
        {
            var cont = VbsOps.Compare(cell.Value, to, cmp);
            if (cont.Type != VbVarType.Boolean || !cont.AsBoolean()) break;
            try
            {
                ExecuteStatementList(stmt.Body, f);
            }
            catch (VbsExitLoopException ex) when (ex.Kind == VbsExitKind.For)
            {
                break;
            }
            cell.Value = VbsOps.Add(cell.Value, step);
        }
    }

    private void ExecForEach(VbsForEachStatement stmt, VbsFrame f)
    {
        var coll = EvalExpr(stmt.Collection, f);
        IEnumerable<VbsVariant> items;

        if (coll.Type == VbVarType.Array)
            items = coll.AsArray().Elements();
        else if (coll.Type == VbVarType.Object)
        {
            var obj = coll.AsObject() ??
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                    "Object variable not set", stmt.Line);
            if (!obj.TryEnumerate(out items))
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    "Object doesn't support this property or method", stmt.Line);
        }
        else
        {
            throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch",
                stmt.Line);
        }

        var cell = ResolveCellForWrite(stmt.Variable, f);
        foreach (var item in items)
        {
            cell.Value = item;
            try
            {
                ExecuteStatementList(stmt.Body, f);
            }
            catch (VbsExitLoopException ex) when (ex.Kind == VbsExitKind.For)
            {
                break;
            }
        }
    }

    private void ExecDo(VbsDoLoopStatement stmt, VbsFrame f)
    {
        while (true)
        {
            if (stmt.Top != null && !TestPasses(stmt.Top, f)) break;
            try
            {
                ExecuteStatementList(stmt.Body, f);
            }
            catch (VbsExitLoopException ex) when (ex.Kind == VbsExitKind.Do)
            {
                break;
            }
            if (stmt.Bottom != null && !TestPasses(stmt.Bottom, f)) break;
        }
    }

    private bool TestPasses(VbsLoopTest test, VbsFrame f)
    {
        bool b = EvalExpr(test.Condition, f).IsConditionTrue();
        return test.Until ? !b : b;
    }

    private void ExecWhile(VbsWhileStatement stmt, VbsFrame f)
    {
        while (EvalExpr(stmt.Condition, f).IsConditionTrue())
            ExecuteStatementList(stmt.Body, f);
    }

    private void ExecExit(VbsExitStatement stmt)
    {
        switch (stmt.Kind)
        {
            case VbsExitKind.Sub:
            case VbsExitKind.Function:
            case VbsExitKind.Property:
                throw new VbsExitProcedureException();
            case VbsExitKind.Do:
                throw new VbsExitLoopException(VbsExitKind.Do);
            case VbsExitKind.For:
                throw new VbsExitLoopException(VbsExitKind.For);
        }
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    private VbsVariant[] EvalArgs(List<VbsExpr> args, VbsFrame f)
    {
        var vals = new VbsVariant[args.Count];
        for (int i = 0; i < vals.Length; i++)
            vals[i] = EvalExpr(args[i], f);
        return vals;
    }

    private VbsVariant EvalExpr(VbsExpr e, VbsFrame f)
    {
        switch (e)
        {
            case VbsLiteralExpr lit: return lit.Value;
            case VbsParenExpr par: return EvalExpr(par.Inner, f);
            case VbsNameExpr n: return EvalName(n, f);
            case VbsMemberExpr m: return EvalMemberGet(m, f);
            case VbsInvokeExpr inv: return EvalInvoke(inv, f, byrefAllowed: true);
            case VbsMeExpr:
                if (f.Me == null)
                    throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired,
                        "Object required");
                return VbsVariant.Of(f.Me);
            case VbsNewExpr ne: return EvalNew(ne);
            case VbsWithMemberExpr wm: return EvalWithMemberGet(wm, f);
            case VbsUnaryExpr u:
            {
                var v = EvalExpr(u.Operand, f);
                return u.Operator == "-"
                    ? VbsOps.Negate(v)
                    : VbsOps.NotOp(v);
            }
            case VbsBinaryExpr b:
            {
                // No short-circuit in VBScript — both sides always evaluate.
                var l = EvalExpr(b.Left, f);
                var r = EvalExpr(b.Right, f);
                return b.Operator switch
                {
                    "+" => VbsOps.Add(l, r),
                    "-" => VbsOps.Subtract(l, r),
                    "*" => VbsOps.Multiply(l, r),
                    "/" => VbsOps.Divide(l, r),
                    "\\" => VbsOps.IntDivide(l, r),
                    "Mod" => VbsOps.Modulo(l, r),
                    "^" => VbsOps.Power(l, r),
                    "&" => VbsOps.Concat(l, r),
                    "Is" => VbsOps.IsOp(l, r),
                    "=" or "<>" or "<" or "<=" or ">" or ">=" =>
                        VbsOps.Compare(l, r, b.Operator),
                    "And" => VbsOps.AndOp(l, r),
                    "Or" => VbsOps.OrOp(l, r),
                    "Xor" => VbsOps.XorOp(l, r),
                    "Eqv" => VbsOps.EqvOp(l, r),
                    "Imp" => VbsOps.ImpOp(l, r),
                    _ => throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                        $"Unknown operator '{b.Operator}'")
                };
            }
            default:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        }
    }

    private VbsVariant EvalName(VbsNameExpr e, VbsFrame f)
    {
        if (TryFindCell(e.Name, f, out var cell))
            return cell.Value;

        if (VbsBuiltins.Table.TryGetValue(e.Name, out var builtin))
        {
            _ctx.CallerFrame = f;   // Eval/Execute/GetRef need the caller scope
            return builtin(_ctx, Array.Empty<VbsVariant>());   // Now, Date, Rnd, Err, …
        }

        if (VbsBuiltins.Constants.TryGetValue(e.Name, out var constant))
            return constant;

        if (_procedures.ContainsKey(e.Name))
            return VbsVariant.Empty;    // bare function name outside a call

        if (_optionExplicit)
            throw new VbsRuntimeException(VbsErrorNumbers.VariableNotDefined,
                $"Variable is not defined: '{e.Name}'");
        return VbsVariant.Empty;        // undeclared reads Empty
    }

    private VbsVariant EvalMemberGet(VbsMemberExpr m, VbsFrame f)
    {
        var objVal = EvalExpr(m.Object, f);
        if (objVal.Type != VbVarType.Object)
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired, "Object required");
        var obj = objVal.AsObject() ??
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                "Object variable not set");
        if (obj.TryGetMember(m.Member, out var value))
            return value;
        throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
            $"Object doesn't support this property or method: '{m.Member}'");
    }

    // ── New / With ────────────────────────────────────────────────────────────

    private VbsVariant EvalNew(VbsNewExpr ne)
    {
        if (_classes.TryGetValue(ne.ClassName, out var cls))
        {
            var instance = new VbsClassInstance(cls, this);
            instance.Construct(new VbsFrame { Me = instance });
            _liveInstances.Add(instance);
            return VbsVariant.Of(instance);
        }
        // Intrinsic construction: the sandboxed browser host denies
        // CreateObject("VBScript.RegExp"), so RegExp is exposed through New.
        if (ne.ClassName.Equals("RegExp", StringComparison.OrdinalIgnoreCase))
            return VbsVariant.Of(new VbsRegExpObject());
        throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired,
            $"Class is not defined: '{ne.ClassName}'", ne.Line);
    }

    /// <summary>With objects of the frame, innermost first.</summary>
    private IEnumerable<IVbsDispatchObject> WithObjects(VbsFrame f)
    {
        if (f.WithStack != null)
            for (int i = f.WithStack.Count - 1; i >= 0; i--)
            {
                var v = f.WithStack[i];
                if (v.Type == VbVarType.Object && v.AsObject() is { } o)
                    yield return o;
            }
    }

    /// <summary>Unqualified `.Member` read: innermost With object first; the
    /// outer blocks are consulted only when the inner object lacks the member
    /// (documented resolution choice).</summary>
    private VbsVariant EvalWithMemberGet(VbsWithMemberExpr e, VbsFrame f)
    {
        foreach (var o in WithObjects(f))
            if (o.TryGetMember(e.Member, out var value))
                return value;
        throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
            $"Object doesn't support this property or method: '{e.Member}'");
    }

    /// <summary>
    /// Runtime resolution of name(args): user procedure → builtin → array
    /// index → object default-property invoke. byrefAllowed is false only for
    /// the `f(x)` statement form (single parenthesized argument → ByVal).
    /// </summary>
    private VbsVariant EvalInvoke(VbsInvokeExpr e, VbsFrame f, bool byrefAllowed)
    {
        if (e.Target is VbsNameExpr name)
        {
            string n = name.Name;

            if (_procedures.TryGetValue(n, out var proc))
                return InvokeProcedure(proc, e.Args, f, byrefAllowed);

            if (VbsBuiltins.Table.TryGetValue(n, out var builtin))
            {
                var vals = EvalArgs(e.Args, f);
                _ctx.CallerFrame = f;   // Eval/Execute/GetRef need the caller scope
                return builtin(_ctx, vals);
            }

            if (TryFindCell(n, f, out var cell))
            {
                var argVals = EvalArgs(e.Args, f);
                var v = cell.Value;
                if (v.Type == VbVarType.Array)
                    return v.AsArray().Get(ToIndices(argVals));
                if (v.Type == VbVarType.Object)
                {
                    var obj = v.AsObject() ??
                        throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                            "Object variable not set");
                    if (obj.TryInvokeDefault(argVals, out var res)) return res;
                    throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                        "Object doesn't support this property or method");
                }
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                    $"Type mismatch: '{n}'");
            }

            if (_optionExplicit)
                throw new VbsRuntimeException(VbsErrorNumbers.VariableNotDefined,
                    $"Variable is not defined: '{n}'");
            throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                $"Type mismatch: '{n}'");
        }

        if (e.Target is VbsMemberExpr mem)
        {
            var objVal = EvalExpr(mem.Object, f);
            if (objVal.Type != VbVarType.Object)
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired, "Object required");
            var obj = objVal.AsObject() ??
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectVariableNotSet,
                    "Object variable not set");
            var argVals = EvalArgs(e.Args, f);
            if (obj.TryInvoke(mem.Member, argVals, out var result)) return result;
            if (argVals.Length == 0 && obj.TryGetMember(mem.Member, out result)) return result;
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                $"Object doesn't support this property or method: '{mem.Member}'");
        }

        // `.Member(args)` inside a With block.
        if (e.Target is VbsWithMemberExpr wm)
        {
            var argVals = EvalArgs(e.Args, f);
            foreach (var o in WithObjects(f))
                if (o.TryInvoke(wm.Member, argVals, out var result)) return result;
            if (argVals.Length == 0)
                foreach (var o in WithObjects(f))
                    if (o.TryGetMember(wm.Member, out var result)) return result;
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                $"Object doesn't support this property or method: '{wm.Member}'");
        }

        throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
    }

    private static int[] ToIndices(VbsVariant[] argVals)
    {
        var idx = new int[argVals.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            long v = argVals[i].ToLongMath();
            idx[i] = v is < 0 or > int.MaxValue
                ? throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range")
                : (int)v;
        }
        return idx;
    }

    // ── Procedures ──────────────────────────────────────────────────────────

    private VbsVariant InvokeProcedure(VbsProcedure proc, List<VbsExpr> argExprs,
                                       VbsFrame caller, bool byrefAllowed)
    {
        if (argExprs.Count != proc.Params.Count)
            throw new VbsRuntimeException(VbsErrorNumbers.WrongNumberOfArguments,
                $"Wrong number of arguments: '{proc.Name}'");

        if (++_callDepth > MaxCallDepth)
        {
            _callDepth--;
            throw new VbsRuntimeException(VbsErrorNumbers.OutOfStackSpace,
                "Out of stack space");
        }
        try
        {
            var frame = new VbsFrame();

            for (int i = 0; i < proc.Params.Count; i++)
            {
                var (pname, byval) = proc.Params[i];
                var argExpr = argExprs[i];

                // ByRef binds the caller's CELL — but only for a plain variable
                // reference (VbsNameExpr). `(x)` or expressions → ByVal.
                VbsCell? cell = null;
                if (!byval && byrefAllowed && argExpr is VbsNameExpr n &&
                    TryFindCell(n.Name, caller, out var found))
                {
                    cell = found;
                }
                if (cell == null)
                    cell = new VbsCell(EvalExpr(argExpr, caller));

                frame.Locals[pname] = cell;
            }

            VbsCell? ret = null;
            if (proc.IsFunction)
            {
                ret = new VbsCell();
                frame.Locals[proc.Name] = ret;      // implicit return variable
                frame.ReturnCell = ret;
            }

            try { ExecuteStatementList(proc.Body, frame); }
            catch (VbsExitProcedureException) { }

            return proc.IsFunction ? ret!.Value : VbsVariant.Empty;
        }
        finally
        {
            _callDepth--;
        }
    }

    // ── Class methods / Eval / Execute / GetRef ──────────────────────────────

    /// <summary>
    /// Invokes a class method or property accessor with `me` bound and
    /// private access enabled. Arguments are values (ByVal only) — the
    /// IVbsDispatchObject interface cannot carry cells for ByRef writeback.
    /// </summary>
    internal VbsVariant InvokeMethod(VbsProcedure proc, VbsVariant[] args, VbsClassInstance me)
    {
        if (args.Length != proc.Params.Count)
            throw new VbsRuntimeException(VbsErrorNumbers.WrongNumberOfArguments,
                $"Wrong number of arguments: '{proc.Name}'");

        if (++_callDepth > MaxCallDepth)
        {
            _callDepth--;
            throw new VbsRuntimeException(VbsErrorNumbers.OutOfStackSpace,
                "Out of stack space");
        }
        try
        {
            var frame = new VbsFrame { Me = me };
            for (int i = 0; i < proc.Params.Count; i++)
                frame.Locals[proc.Params[i].Name] = new VbsCell(args[i]);

            VbsCell? ret = null;
            if (proc.IsFunction)   // Function or Property Get: name = return var
            {
                ret = new VbsCell();
                frame.Locals[proc.Name] = ret;
                frame.ReturnCell = ret;
            }

            me.MethodDepth++;
            try { ExecuteStatementList(proc.Body, frame); }
            catch (VbsExitProcedureException) { }
            finally { me.MethodDepth--; }

            return proc.IsFunction ? ret!.Value : VbsVariant.Empty;
        }
        finally
        {
            _callDepth--;
        }
    }

    /// <summary>GetRef(name) → callable reference to a script procedure.</summary>
    internal IVbsDispatchObject CreateGetRef(string procName)
    {
        if (!_procedures.TryGetValue(procName, out var proc))
            throw new VbsRuntimeException(VbsErrorNumbers.InvalidProcedureCall,
                $"Invalid procedure call or argument: GetRef('{procName}')");
        return new VbsGetRefObject(this, proc);
    }

    /// <summary>Eval(code): compiles an expression and evaluates it in the
    /// CALLER's scope. `=` is comparison here — assignments never happen
    /// through Eval.</summary>
    internal VbsVariant EvalText(string code, VbsFrame? callerFrame)
    {
        VbsExpr expr;
        try { expr = VbsParser.ParseExpressionText(code); }
        catch (VbsSyntaxException ex)
        { throw new VbsRuntimeException(ex.Number, ex.Message); }
        var frame = callerFrame ?? new VbsFrame { Locals = _globals, IsGlobal = true };
        return EvalExpr(expr, frame);
    }

    /// <summary>Execute(code): compiles statements and runs them in the
    /// CALLER's scope (procedure locals are visible and mutable). Procedures
    /// and classes declared inside become global.</summary>
    internal void ExecuteText(string code, VbsFrame? callerFrame)
    {
        VbsScript script = ParseRuntimeCode(code);
        RegisterProcedures(script);   // procs/classes declared inside become global
        var frame = callerFrame ?? new VbsFrame { Locals = _globals, IsGlobal = true };
        ExecuteStatementList(script.Body, frame);
    }

    /// <summary>ExecuteGlobal(code): compiles statements and runs them in the
    /// GLOBAL scope — procedure locals of the caller are NOT visible.</summary>
    internal void ExecuteGlobalText(string code)
    {
        VbsScript script = ParseRuntimeCode(code);
        RegisterProcedures(script);
        var frame = new VbsFrame { Locals = _globals, IsGlobal = true };
        ExecuteStatementList(script.Body, frame);
    }

    private static VbsScript ParseRuntimeCode(string code)
    {
        try { return VbsParser.Parse(code); }
        catch (VbsSyntaxException ex)
        { throw new VbsRuntimeException(ex.Number, ex.Message); }
    }

    // ── Scope helpers ───────────────────────────────────────────────────────

    private bool TryFindCell(string name, VbsFrame f, out VbsCell cell)
    {
        if (f.Locals.TryGetValue(name, out var localCell))
        {
            cell = localCell;
            return true;
        }
        // Class code sees its instance's fields unqualified (Me.X optional).
        if (f.Me != null && f.Me.Fields.TryGetValue(name, out var meCell))
        {
            cell = meCell;
            return true;
        }
        if (_globals.TryGetValue(name, out var globalCell))
        {
            cell = globalCell;
            return true;
        }
        cell = null!;
        return false;
    }

    /// <summary>
    /// Cell for an assignment: existing declaration first (procedure-local,
    /// class field, then global). Undeclared: Option Explicit → 500; otherwise
    /// implicit — local when inside a procedure, global at script level.
    /// </summary>
    private VbsCell ResolveCellForWrite(string name, VbsFrame f)
    {
        if (f.Locals.TryGetValue(name, out var cell)) return cell;
        if (f.Me != null && f.Me.Fields.TryGetValue(name, out var meCell)) return meCell;
        if (f.IsGlobal)
        {
            cell = new VbsCell();
            _globals[name] = cell;
            return cell;
        }
        if (_globals.TryGetValue(name, out cell)) return cell;
        if (_optionExplicit)
            throw new VbsRuntimeException(VbsErrorNumbers.VariableNotDefined,
                $"Variable is not defined: '{name}'");
        cell = new VbsCell();
        f.Locals[name] = cell;
        return cell;
    }
}