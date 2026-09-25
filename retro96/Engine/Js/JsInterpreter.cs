using System;
using System.Collections.Generic;
using System.Diagnostics;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Js;

// ─────────────────────────────────────────────────────────────────────────────
// Control-flow exceptions (the standard tree-walking interpreter technique)
// ─────────────────────────────────────────────────────────────────────────────

public class JsTimeoutException : Exception { public JsTimeoutException() : base("Script timeout") { } }
public class JsOutOfMemoryException : Exception { public JsOutOfMemoryException() : base("Script out of memory") { } }

public class JsBreakException : Exception
{
    public string? Label { get; }
    public JsBreakException(string? label) { Label = label; }
}

public class JsContinueException : Exception
{
    public string? Label { get; }
    public JsContinueException(string? label) { Label = label; }
}

public class JsReturnException : Exception
{
    public JsValue Value { get; }
    public JsReturnException(JsValue value) { Value = value; }
}

public class JsThrownException : Exception
{
    public JsValue Value { get; }
    public JsThrownException(JsValue value) : base(value.ToJsString()) { Value = value; }
}

public class JsInterpreterException : Exception
{
    public JsInterpreterException(string message) : base(message) { }
}

/// <summary>
/// Tree-walking interpreter for JavaScript 1.1/1.2.
///
/// Era-correct behaviours baked in:
///   • var is function-scoped; blocks do not open scopes
///   • plain calls get the global object as 'this'
///   • timers (setTimeout/setInterval) take script functions OR the era's
///     string-code form — the callback runs through CallFunction
///   • runtime errors convert to catchable Error objects inside try/catch,
///     and any script error stops only that script, never the renderer
///   • recursion depth, execution time, and string-allocation totals are
///     all capped so hostile pages cannot hang the browser
/// </summary>
public class JsInterpreter
{
    public sealed record ConsoleEntry(string Level, string Message, DateTime Timestamp);
    public event Action<ConsoleEntry>? ConsoleMessage;
    private readonly JsScope _globalScope;
    private JsScope _currentScope;
    private readonly Action<string> _onNavigate;
    private readonly Action<string> _setStatus;

    private readonly int _timeLimitMs;
    private readonly int _heapLimitBytes;
    private long _allocatedBytes;
    private readonly Stopwatch _stopwatch = new();

    private int _callDepth;
    private const int MaxCallDepth = 400;

    // Guards against re-entering the interpreter while a call is already
    // in progress on this instance. This isn't multi-threading — WinForms
    // timers are single-threaded — but alert()/confirm()/prompt() block on
    // a modal MessageBox, and showing a modal pumps the Windows message
    // loop, so the 50ms _jsTimer can still fire and call TickTimers() while
    // the outer script call sits paused on the stack waiting for the user
    // to dismiss the dialog. Without this guard, the timer callback's
    // _stopwatch.Restart()/_callDepth reset stomps the suspended outer
    // call's budget, so its next CheckTimeout() sees an effectively random
    // elapsed time depending on whether a timer happened to fire while the
    // dialog was open — this is the "random" script-timeout bug.
    private bool _isExecuting;

    // Timer registry — ids are handed to script and used by clearTimeout
    private sealed class ScheduledTimer
    {
        public int Id;
        public JsFunction Callback = null!;
        public int Interval;
        public long NextTick;
        public bool Repeat;
    }
    private readonly List<ScheduledTimer> _timers = new();
    private int _nextTimerId = 1;

    // ── Primitive prototypes, installed by JsRuntime (property access on
    //    "abc".length / (1.5).toString(16) routes through these)
    // ─────────────────────────────────────────────────────────────────────

    public static JsObject? StringPrototype { get; set; }
    public static JsObject? NumberPrototype { get; set; }
    public static JsObject? BooleanPrototype { get; set; }
    public static JsObject? ArrayPrototype { get; set; }
    public static JsObject? ObjectPrototype { get; set; }

    // ─────────────────────────────────────────────────────────────────────

    public JsInterpreter(JsScope globalScope, DomDocument? document,
                         Action<string> onNavigate, Action<string> setStatus,
                         int timeLimitMs = 5000, int heapLimitBytes = 10_485_760)
    {
        _globalScope = globalScope ?? throw new ArgumentNullException(nameof(globalScope));
        _currentScope = globalScope;
        _onNavigate = onNavigate ?? (_ => { });
        _setStatus = setStatus ?? (_ => { });
        _timeLimitMs = timeLimitMs;
        _heapLimitBytes = heapLimitBytes;

        // Object stringification (array join, date toString) for "" + obj
        JsObject.Stringifier = StringifyObject;
    }

    /// <summary>The global window object, if DOM bindings registered one.</summary>
    public JsObject? WindowObject =>
        _globalScope.Get("window") is { Type: JsType.Object } w ? w.GetObject() : null;

    public JsScope GlobalScope => _globalScope;

    /// <summary>
    /// Element-wrapper hook the shell installs so inline handlers get a
    /// live DOM wrapper as 'this' (document.images[0].src = ...).
    /// Keeps the interpreter free of any WinForms/DomBindings dependency.
    /// </summary>
    internal Func<DomElement, JsObject>? ElementWrapperHook { get; set; }

    // ─────────────────────────────────────────────────────────────────────
    // Entry points
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Execute a parsed script; returns the last statement value.</summary>
    public JsValue Execute(ProgramNode program)
    {
        // Only the OUTERMOST call resets the shared timing state and takes
        // ownership of _isExecuting. A nested call — eval() run from inside
        // a script that's already executing — must not restart the clock
        // out from under the call that invoked it, and must not clear the
        // reentrancy guard early (that would let TickTimers back in the
        // moment eval() returns, even though the outer script is still
        // running). See _isExecuting's comment for the bug this guards.
        bool isOutermost = !_isExecuting;
        if (isOutermost)
        {
            _isExecuting = true;
            _stopwatch.Restart();
            _allocatedBytes = 0;
            _callDepth = 0;
        }
        try
        {
            Hoist(program.Body, _currentScope);

            JsValue result = JsValue.Undefined;
            foreach (var stmt in program.Body)
                result = ExecuteStatement(stmt);
            return result;
        }
        catch (JsTimeoutException)
        {
            const string message = "Script execution timed out";
            _setStatus(message);
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsOutOfMemoryException)
        {
            const string message = "Script ran out of memory";
            _setStatus(message);
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsThrownException ex)
        {
            // Top-level throw — report and stop THIS script only.  This used
            // to update only the status bar, so an uncaught JS error was
            // invisible in Inspector > Console.
            string message = $"Uncaught {ex.Value.ToJsString()}";
            _setStatus($"Script error: {ex.Value.ToJsString()}");
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsInterpreterException ex)
        {
            string message = $"Uncaught {ex.Message}";
            _setStatus($"Script error: {ex.Message}");
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsBreakException) { return JsValue.Undefined; }
        catch (JsContinueException) { return JsValue.Undefined; }
        catch (JsReturnException) { return JsValue.Undefined; }
        finally
        {
            if (isOutermost) _isExecuting = false;
        }
    }

    /// <summary>Parse and run a string in the current scope.</summary>
    public JsValue ExecuteString(string source)
    {
        try
        {
            var program = JsParser.Parse(source);
            return Execute(program);
        }
        catch (JsParserException ex)
        {
            string message = $"Syntax error at line {ex.Line}, column {ex.Column}: {ex.Message}";
            _setStatus($"Script error: {ex.Message}");
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
    }

    /// <summary>Eval a string in an explicit scope (eval()).</summary>
    public JsValue EvalString(string source, JsScope scope)
    {
        var program = JsParser.Parse(source);
        var old = _currentScope;
        _currentScope = scope;
        try
        {
            return Execute(program);
        }
        finally
        {
            _currentScope = old;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Events (inline handlers + JS-assigned handlers)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fire an event handler with the element as 'this' and an 'event'
    /// object in scope.  Handles both forms:
    ///   • inline attribute source ("onclick" attribute)
    ///   • JS-assigned functions (element.onclick = fn) — the sentinel
    ///     "__js_handler__" in EventHandlers marks those; the old code
    ///     PARSED the sentinel as script source, so JS-assigned handlers
    ///     never ran at all.
    /// Returns the last value (onMouseOver returns true to keep the
    /// status-bar text).
    /// </summary>
    public JsValue FireEvent(DomElement element, string eventName,
                             JsObject? eventObj = null)
    {
        if (!element.EventHandlers.TryGetValue(eventName, out var handlerSource))
            return JsValue.Undefined;

        if (handlerSource == "__js_handler__")
        {
            var wrapper = ElementWrapperHook?.Invoke(element);
            if (wrapper != null)
            {
                foreach (var kvp in wrapper.Properties)
                {
                    if (kvp.Value.Type == JsType.Function &&
                        string.Equals(kvp.Key, eventName, StringComparison.OrdinalIgnoreCase))
                    {
                        return CallHandler(kvp.Value, JsValue.FromObject(wrapper), eventObj);
                    }
                }
            }
            return JsValue.Undefined;
        }

        try
        {
            var program = JsParser.Parse(handlerSource);
            var scope = _currentScope.NewChild();
            var thisValue = ElementWrapperHook != null
                ? JsValue.FromObject(ElementWrapperHook(element))
                : JsValue.Undefined;
            scope.Define("this", thisValue);
            if (eventObj != null)
                scope.Define("event", JsValue.FromObject(eventObj));

            var old = _currentScope;
            _currentScope = scope;

            // FIX (random script-timeout bug, part 2): inline handlers
            // (onclick="…") run through ExecuteStatement directly rather
            // than through Execute(), so they never set _isExecuting.
            // If the handler calls alert()/confirm()/prompt(), the modal
            // pumps messages and the 50ms UI timer can fire TickTimers()
            // with nothing stopping it — same corruption as an unguarded
            // top-level script. Only the outermost handler call takes
            // ownership, same reasoning as Execute().
            bool isOutermost = !_isExecuting;
            if (isOutermost)
            {
                _isExecuting = true;
                _stopwatch.Restart();
                _callDepth = 0;
            }
            try
            {
                Hoist(program.Body, scope);
                JsValue last = JsValue.Undefined;
                foreach (var stmt in program.Body)
                    last = ExecuteStatement(stmt);
                return last;
            }
            finally
            {
                _currentScope = old;
                if (isOutermost) _isExecuting = false;
            }
        }
        catch (JsReturnException retEx)
        {
            // An inline handler IS a function body — `onclick="…; return
            // false"` throws JsReturnException, and its value is the
            // handler's return.  Swallowing it (the old generic catch)
            // lost the false, so "return false" links/buttons never
            // cancelled their default action.
            return retEx.Value;
        }
        catch (Exception ex)
        {
            _setStatus($"Error in {eventName} handler: {ex.Message}");
            return JsValue.Undefined;
        }
    }

    /// <summary>Build a minimal keyboard event object (key / keyCode / which).</summary>
    public JsObject CreateKeyEvent(string key, int keyCode)
    {
        var evt = new JsObject { Prototype = ObjectPrototype };
        evt.Set("key", JsValue.From(key));
        evt.Set("keyCode", JsValue.From(keyCode));
        evt.Set("which", JsValue.From(keyCode));
        evt.Set("charCode", JsValue.From(keyCode));
        return evt;
    }

    /// <summary>
    /// Call a JS-assigned handler (element.onclick = function...) with the
    /// element as 'this'.  Used by the shell when dispatching DOM events.
    /// </summary>
    public JsValue CallHandler(JsValue handler, JsValue thisValue, JsObject? eventObj = null)
    {
        if (handler.Type != JsType.Function)
            return JsValue.Undefined;

        // FIX (random script-timeout bug, part 3): this is the third entry
        // point that can start a top-level script run (the other two are
        // Execute() and FireEvent()'s inline-string branch) — reached when
        // a handler was assigned as element.onclick = function(){...}
        // rather than written as an onclick="..." string. Same guard, same
        // reasoning: without it, alert()/confirm()/prompt() inside the
        // handler leaves TickTimers() free to fire mid-dialog and corrupt
        // the suspended call's timing state.
        bool isOutermost = !_isExecuting;
        if (isOutermost)
        {
            _isExecuting = true;
            _stopwatch.Restart();
            _callDepth = 0;
        }
        try
        {
            var args = eventObj != null
                ? new[] { JsValue.FromObject(eventObj) }
                : Array.Empty<JsValue>();
            return CallFunction(handler.GetFunction(), thisValue, args);
        }
        catch (Exception ex)
        {
            _setStatus($"Error in event handler: {ex.Message}");
            return JsValue.Undefined;
        }
        finally
        {
            if (isOutermost) _isExecuting = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Timers
    // ─────────────────────────────────────────────────────────────────────

    public int SetTimeout(JsValue callback, int delayMs, bool repeat)
    {
        // String code arguments — the era's setTimeout("code()") form.
        // (This used to sit AFTER the "not a function" early return, which
        // made the branch dead code — string timers never fired.)
        if (callback.Type == JsType.String)
        {
            string src = callback.GetString();
            callback = JsValue.FromFunction(new JsFunction(
                (self, args) => ExecuteString(src), _globalScope, "setTimeout-code"));
        }

        if (callback.Type != JsType.Function)
            return 0;

        var timer = new ScheduledTimer
        {
            Id = _nextTimerId++,
            Callback = callback.GetFunction(),
            Interval = Math.Max(delayMs, repeat ? 1 : 0),
            NextTick = Environment.TickCount64 + Math.Max(delayMs, 0),
            Repeat = repeat
        };
        _timers.Add(timer);
        Retro96.DebugLog.Write($"JS {(repeat ? "setInterval" : "setTimeout")} registered id={timer.Id} delayMs={delayMs} fnName='{callback.GetFunction()?.Name}'");
        return timer.Id;
    }

    public void ClearTimer(int id)
    {
        _timers.RemoveAll(t => t.Id == id);
    }

    /// <summary>Tick due timers — called from the shell's UI timer.</summary>
    public void TickTimers()
    {
        // FIX (random script-timeout bug): alert()/confirm()/prompt() block
        // on a modal MessageBox, and a modal pumps the Windows message loop,
        // so the 50ms UI timer can call TickTimers() while a script call is
        // already suspended mid-stack on this same interpreter waiting for
        // the dialog to close. Ticking here would reset _stopwatch and
        // _callDepth out from under that suspended call, so when it resumes
        // its next CheckTimeout() sees an effectively random elapsed time —
        // due timers just wait for the next tick instead, once the dialog
        // (or whatever else is running) is gone. See _isExecuting's comment.
        if (_isExecuting) return;
        if (_timers.Count == 0) return;
        long now = Environment.TickCount64;

        var due = new List<ScheduledTimer>();
        foreach (var t in _timers)
            if (now >= t.NextTick)
                due.Add(t);

        foreach (var timer in due)
        {
            if (now < timer.NextTick) continue;   // cleared/re-armed meanwhile
            if (_isExecuting) break;               // a timer callback below opened its own modal; stop for this tick

            // Run through CallFunction so SCRIPT functions work.
            _isExecuting = true;
            _stopwatch.Restart();
            _callDepth = 0;
            try
            {
                CallFunction(timer.Callback, GlobalThis(), Array.Empty<JsValue>());
            }
            catch (Exception ex)
            {
                string message = ex.Message.StartsWith("Error: ", StringComparison.Ordinal)
                    ? ex.Message[7..]
                    : ex.Message;
                _setStatus($"Page timer exception was caught; the browser continued running: {message}");
            }
            finally
            {
                _isExecuting = false;
            }

            if (timer.Repeat)
                timer.NextTick = Environment.TickCount64 + timer.Interval;
            else
                _timers.Remove(timer);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Statements
    // ─────────────────────────────────────────────────────────────────────

    private JsValue ExecuteStatement(Stmt stmt, IReadOnlyList<string>? labels = null)
    {
        CheckTimeout();

        return stmt switch
        {
            BlockStatement block => ExecuteBlock(block),
            VarDeclaration varDecl => ExecuteVarDeclaration(varDecl),
            ExpressionStatement exprStmt => ExecuteExpression(exprStmt.Expression),
            IfStatement ifStmt => ExecuteIf(ifStmt),
            WhileStatement whileStmt => ExecuteWhile(whileStmt, labels),
            DoWhileStatement doWhile => ExecuteDoWhile(doWhile, labels),
            ForStatement forStmt => ExecuteFor(forStmt, labels),
            ForInStatement forInStmt => ExecuteForIn(forInStmt, labels),
            ReturnStatement ret => ExecuteReturn(ret),
            BreakStatement br => throw new JsBreakException(br.Label),
            ContinueStatement co => throw new JsContinueException(co.Label),
            SwitchStatement sw => ExecuteSwitch(sw),
            ThrowStatement th => ExecuteThrow(th),
            TryStatement tr => ExecuteTry(tr),
            LabeledStatement lab => ExecuteStatement(lab.Body, ConcatLabel(labels, lab.Label)),
            WithStatement with => ExecuteWith(with),
            FunctionDeclaration => JsValue.Undefined,   // hoisted
            EmptyStatement => JsValue.Undefined,
            _ => throw new JsInterpreterException($"Unknown statement: {stmt.GetType().Name}")
        };
    }

    private static IReadOnlyList<string>? ConcatLabel(IReadOnlyList<string>? labels, string label)
    {
        if (labels == null) return new[] { label };
        var list = new List<string>(labels) { label };
        return list;
    }

    private JsValue ExecuteBlock(BlockStatement block)
    {
        // Blocks do NOT open scopes in JS 1.1 — var stays function-scoped
        JsValue result = JsValue.Undefined;
        foreach (var stmt in block.Body)
            result = ExecuteStatement(stmt);
        return result;
    }

    private JsValue ExecuteVarDeclaration(VarDeclaration varDecl)
    {
        foreach (var d in varDecl.Declarations)
        {
            JsValue value = d.Init != null
                ? ExecuteExpression(d.Init)
                : JsValue.Undefined;
            _currentScope.Define(d.Id.Name, value);
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteIf(IfStatement ifStmt)
    {
        if (ExecuteExpression(ifStmt.Test).ToBoolean())
            return ExecuteStatement(ifStmt.Consequent);
        if (ifStmt.Alternate != null)
            return ExecuteStatement(ifStmt.Alternate);
        return JsValue.Undefined;
    }

    private bool LoopCatches(IReadOnlyList<string>? labels, string? label) =>
        label == null || (labels != null && labels.Contains(label));

    private JsValue ExecuteWhile(WhileStatement whileStmt, IReadOnlyList<string>? labels)
    {
        while (ExecuteExpression(whileStmt.Test).ToBoolean())
        {
            CheckTimeout();
            try
            {
                ExecuteStatement(whileStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (LoopCatches(labels, bex.Label)) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (LoopCatches(labels, cex.Label)) continue;
                throw;
            }
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteDoWhile(DoWhileStatement doWhile, IReadOnlyList<string>? labels)
    {
        while (true)
        {
            CheckTimeout();
            try
            {
                ExecuteStatement(doWhile.Body);
            }
            catch (JsBreakException bex)
            {
                if (LoopCatches(labels, bex.Label)) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (LoopCatches(labels, cex.Label)) { /* fall to test */ }
                else throw;
            }

            if (!ExecuteExpression(doWhile.Test).ToBoolean())
                break;
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteFor(ForStatement forStmt, IReadOnlyList<string>? labels)
    {
        if (forStmt.Init != null)
        {
            if (forStmt.Init is VarDeclaration varDecl)
                ExecuteVarDeclaration(varDecl);
            else if (forStmt.Init is ExpressionStatement es)
                ExecuteExpression(es.Expression);
        }

        while (true)
        {
            CheckTimeout();
            if (forStmt.Test != null && !ExecuteExpression(forStmt.Test).ToBoolean())
                break;

            try
            {
                ExecuteStatement(forStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (LoopCatches(labels, bex.Label)) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (LoopCatches(labels, cex.Label))
                {
                    if (forStmt.Update != null)
                        ExecuteExpression(forStmt.Update);
                    continue;
                }
                throw;
            }

            if (forStmt.Update != null)
                ExecuteExpression(forStmt.Update);
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteForIn(ForInStatement forInStmt, IReadOnlyList<string>? labels)
    {
        JsValue objVal = ExecuteExpression(forInStmt.Right);
        if (objVal.Type is JsType.Null or JsType.Undefined)
            return JsValue.Undefined;

        if (objVal.Type is not (JsType.Object or JsType.Function))
            return JsValue.Undefined;

        var obj = objVal.GetObjectOrFunction();

        // Arrays enumerate their indices only — "length" and other
        // internal slots were never enumerable in the era's for-in
        IEnumerable<string> keys;
        if (obj.Class == "Array")
        {
            var indices = new List<string>();
            long len = 0;
            foreach (var k in obj.OwnEnumerableKeys())
            {
                if (k == "length") continue;
                if (long.TryParse(k, out var idx) && idx >= 0)
                {
                    indices.Add(k);
                    if (idx + 1 > len) len = idx + 1;
                }
            }
            indices.Sort((x, y) => long.Parse(x).CompareTo(long.Parse(y)));
            keys = indices;
        }
        else
        {
            keys = obj.OwnEnumerableKeys();
        }

        foreach (var key in keys)
        {
            CheckTimeout();

            if (forInStmt.Left is VarDeclaration varDecl)
                _currentScope.Define(varDecl.Declarations[0].Id.Name, JsValue.From(key));
            else if (forInStmt.Left is ExpressionStatement es && es.Expression is Identifier ident)
                _currentScope.Set(ident.Name, JsValue.From(key));
            else if (forInStmt.Left is Identifier ident2)
                _currentScope.Set(ident2.Name, JsValue.From(key));

            try
            {
                ExecuteStatement(forInStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (LoopCatches(labels, bex.Label)) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (LoopCatches(labels, cex.Label)) continue;
                throw;
            }
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteReturn(ReturnStatement ret)
    {
        JsValue value = ret.Argument != null
            ? ExecuteExpression(ret.Argument)
            : JsValue.Undefined;
        throw new JsReturnException(value);
    }

    private JsValue ExecuteSwitch(SwitchStatement sw)
    {
        JsValue disc = ExecuteExpression(sw.Discriminant);

        int start = -1;
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            var c = sw.Cases[i];
            if (c.Test == null) continue;
            if (disc.StrictEquals(ExecuteExpression(c.Test)))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            // No match — enter at default (and fall through past it,
            // exactly like the era's switch)
            for (int i = 0; i < sw.Cases.Count; i++)
                if (sw.Cases[i].Test == null) { start = i; break; }
        }
        if (start < 0)
            return JsValue.Undefined;

        for (int i = start; i < sw.Cases.Count; i++)
        {
            foreach (var stmt in sw.Cases[i].Consequent)
            {
                try
                {
                    ExecuteStatement(stmt);
                }
                catch (JsBreakException bex)
                {
                    if (bex.Label == null) return JsValue.Undefined;
                    throw;
                }
            }
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteThrow(ThrowStatement th)
    {
        throw new JsThrownException(ExecuteExpression(th.Argument));
    }

    private JsValue ExecuteTry(TryStatement tr)
    {
        try
        {
            ExecuteStatement(tr.Block);
        }
        catch (JsThrownException thrown)
        {
            if (tr.Handler != null)
            {
                var old = _currentScope;
                _currentScope = _currentScope.NewChild();
                try
                {
                    _currentScope.Define(tr.Handler.Param.Name, thrown.Value);
                    ExecuteStatement(tr.Handler.Body);
                }
                finally
                {
                    _currentScope = old;
                }
            }
            else if (tr.Finalizer == null)
            {
                throw;   // no handler, no finally — propagate
            }
        }
        catch (JsInterpreterException ex)
        {
            // Runtime errors are catchable, period-style
            if (tr.Handler != null)
            {
                var err = new JsObject { Class = "Error" };
                err.Set("name", JsValue.From("Error"));
                err.Set("message", JsValue.From(ex.Message));
                var old = _currentScope;
                _currentScope = _currentScope.NewChild();
                try
                {
                    _currentScope.Define(tr.Handler.Param.Name, JsValue.FromObject(err));
                    ExecuteStatement(tr.Handler.Body);
                }
                finally
                {
                    _currentScope = old;
                }
            }
            else if (tr.Finalizer == null)
            {
                throw;
            }
        }
        finally
        {
            if (tr.Finalizer != null)
                ExecuteStatement(tr.Finalizer);
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteWith(WithStatement with)
    {
        JsValue objVal = ExecuteExpression(with.Object);
        if (objVal.Type is not (JsType.Object or JsType.Function))
            return ExecuteStatement(with.Body);

        var obj = objVal.GetObjectOrFunction();
        var withScope = _currentScope.NewChild();

        // Snapshot of own + inherited properties, shadowing outer variables
        JsObject? current = obj;
        while (current != null)
        {
            foreach (var key in current.OwnEnumerableKeys())
                withScope.Define(key, current.Get(key));
            current = current.Prototype;
        }

        var old = _currentScope;
        _currentScope = withScope;
        try
        {
            return ExecuteStatement(with.Body);
        }
        finally
        {
            _currentScope = old;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Expressions
    // ─────────────────────────────────────────────────────────────────────

    private JsValue ExecuteExpression(Expr expr)
    {
        CheckTimeout();

        return expr switch
        {
            AssignmentExpr a => ExecuteAssignment(a),
            BinaryExpr b => ExecuteBinary(b),
            LogicalExpr l => ExecuteLogical(l),
            UnaryExpr u => ExecuteUnary(u),
            UpdateExpr up => ExecuteUpdate(up),
            TernaryExpr t => ExecuteExpression(ExecuteExpression(t.Test).ToBoolean()
                                       ? t.Consequent : t.Alternate),
            CallExpr c => ExecuteCall(c),
            NewExpr n => ExecuteNew(n),
            MemberExpr m => ExecuteMember(m),
            FunctionExpr f => ExecuteFunctionExpr(f),
            ArrayExpr arr => ExecuteArray(arr),
            ObjectExpr o => ExecuteObject(o),
            Identifier id => _currentScope.Get(id.Name),
            NumberLiteral num => JsValue.From(num.Value),
            StringLiteral str => JsValue.From(str.Value),
            BoolLiteral b => JsValue.From(b.Value),
            NullLiteral => JsValue.Null,
            ThisExpr => _currentScope.Get("this") is { Type: JsType.Undefined }
                                        ? GlobalThis() : _currentScope.Get("this"),
            RegexLiteral rx => ExecuteRegex(rx),
            VoidExpr v => ExecuteVoid(v),
            TypeofExpr t2 => ExecuteTypeof(t2),
            DeleteExpr d => ExecuteDelete(d),
            InExpr i => ExecuteIn(i),
            InstanceofExpr ins => ExecuteInstanceof(ins),
            _ => throw new JsInterpreterException($"Unknown expression: {expr.GetType().Name}")
        };
    }

    private JsValue ExecuteAssignment(AssignmentExpr a)
    {
        // Compound operators read the current value first
        JsValue rightVal = ExecuteExpression(a.Right);

        if (a.Left is Identifier ident)
        {
            JsValue newValue = a.Operator == "="
                ? rightVal
                : ApplyCompound(_currentScope.Get(ident.Name), rightVal, a.Operator);

            // `location = "url"` — the era's #1 script navigation idiom
            // (openpowerstart/switch_page/setTimeout("location='...'"))
            // used to land in the scope DICTIONARY as a plain string:
            // the LocationObject.href setter never fired, so every button
            // built on it did nothing. Route string assignments to bare
            // `location` (and window/document.location in the member path
            // below) through the shell's navigate hook.
            if (ident.Name == "location" && newValue.Type == JsType.String)
            {
                _onNavigate(newValue.GetString());
                return newValue;
            }

            _currentScope.Set(ident.Name, newValue);
            return newValue;
        }

        if (a.Left is MemberExpr member)
        {
            JsValue objVal = ExecuteExpression(member.Object);
            string name = GetMemberPropertyName(member);

            JsValue newValue = a.Operator == "="
                ? rightVal
                : ApplyCompound(GetProperty(objVal, name), rightVal, a.Operator);

            // window.location = "url" / document.location = "url" — same
            // story as the bare identifier: a plain Set replaced the slot
            // with a string and nothing navigated.
            if (name == "location" && newValue.Type == JsType.String &&
                member.Object is Identifier objId &&
                objId.Name is "window" or "document" or "self" or "top" or "parent")
            {
                _onNavigate(newValue.GetString());
            }

            SetProperty(objVal, name, newValue);
            return newValue;
        }

        throw new JsInterpreterException("Invalid left side in assignment");
    }

    private static JsValue ApplyCompound(JsValue left, JsValue right, string op) => op switch
    {
        "+=" => Add(left, right),
        "-=" => JsValue.From(left.ToNumber() - right.ToNumber()),
        "*=" => JsValue.From(left.ToNumber() * right.ToNumber()),
        "/=" => JsValue.From(left.ToNumber() / right.ToNumber()),
        "%=" => JsValue.From(left.ToNumber() % right.ToNumber()),
        "<<=" => JsValue.From((double)(ToInt32(left) << (ToInt32(right) & 31))),
        ">>=" => JsValue.From((double)(ToInt32(left) >> (ToInt32(right) & 31))),
        ">>>=" => JsValue.From((double)((uint)ToInt32(left) >> (ToInt32(right) & 31))),
        "&=" => JsValue.From((double)(ToInt32(left) & ToInt32(right))),
        "|=" => JsValue.From((double)(ToInt32(left) | ToInt32(right))),
        "^=" => JsValue.From((double)(ToInt32(left) ^ ToInt32(right))),
        _ => right
    };

    private JsValue ExecuteBinary(BinaryExpr b)
    {
        // Comma: evaluate both, keep right
        if (b.Operator == ",")
        {
            ExecuteExpression(b.Left);
            return ExecuteExpression(b.Right);
        }

        JsValue left = ExecuteExpression(b.Left);
        JsValue right = ExecuteExpression(b.Right);

        return b.Operator switch
        {
            "+" => Add(left, right),
            "-" => JsValue.From(left.ToNumber() - right.ToNumber()),
            "*" => JsValue.From(left.ToNumber() * right.ToNumber()),
            "/" => JsValue.From(left.ToNumber() / right.ToNumber()),
            "%" => JsValue.From(left.ToNumber() % right.ToNumber()),
            "&" => JsValue.From((double)(ToInt32(left) & ToInt32(right))),
            "|" => JsValue.From((double)(ToInt32(left) | ToInt32(right))),
            "^" => JsValue.From((double)(ToInt32(left) ^ ToInt32(right))),
            "<<" => JsValue.From((double)(ToInt32(left) << (ToInt32(right) & 31))),
            ">>" => JsValue.From((double)(ToInt32(left) >> (ToInt32(right) & 31))),
            ">>>" => JsValue.From((double)((uint)ToInt32(left) >> (ToInt32(right) & 31))),
            "==" => JsValue.From(left.AbstractEquals(right)),
            "!=" => JsValue.From(!left.AbstractEquals(right)),
            "===" => JsValue.From(left.StrictEquals(right)),
            "!==" => JsValue.From(!left.StrictEquals(right)),
            "<" => JsValue.From(LessThan(left, right)),
            ">" => JsValue.From(LessThan(right, left)),
            "<=" => JsValue.From(!LessThan(right, left)),
            ">=" => JsValue.From(!LessThan(left, right)),
            _ => throw new JsInterpreterException($"Unknown binary operator: {b.Operator}")
        };
    }

    private JsValue ExecuteLogical(LogicalExpr l)
    {
        JsValue left = ExecuteExpression(l.Left);
        if (l.Operator == "&&")
            return left.ToBoolean() ? ExecuteExpression(l.Right) : left;
        return left.ToBoolean() ? left : ExecuteExpression(l.Right);
    }

    private JsValue ExecuteUnary(UnaryExpr u)
    {
        // typeof must not evaluate its target when it is an undeclared
        // identifier — it just returns "undefined"
        if (u.Operator == "typeof" && u.Argument is Identifier id)
            return JsValue.From(_currentScope.Has(id.Name)
                ? Typeof(_currentScope.Get(id.Name)) : "undefined");

        return u.Operator switch
        {
            "!" => JsValue.From(!ExecuteExpression(u.Argument).ToBoolean()),
            "~" => JsValue.From((double)~ToInt32(ExecuteExpression(u.Argument))),
            "+" => JsValue.From(ExecuteExpression(u.Argument).ToNumber()),
            "-" => JsValue.From(-ExecuteExpression(u.Argument).ToNumber()),
            "typeof" => JsValue.From(Typeof(ExecuteExpression(u.Argument))),
            "void" => ExecuteVoid(new VoidExpr(u.Argument)),
            "delete" => ExecuteDelete(new DeleteExpr(u.Argument)),
            _ => throw new JsInterpreterException($"Unknown unary operator: {u.Operator}")
        };
    }

    private JsValue ExecuteUpdate(UpdateExpr up)
    {
        double delta = up.Operator == "++" ? 1 : -1;

        if (up.Argument is Identifier ident)
        {
            JsValue oldVal = _currentScope.Get(ident.Name);
            JsValue newVal = JsValue.From(oldVal.ToNumber() + delta);
            _currentScope.Set(ident.Name, newVal);
            return up.Prefix ? newVal : oldVal;
        }

        if (up.Argument is MemberExpr member)
        {
            JsValue objVal = ExecuteExpression(member.Object);   // evaluate ONCE
            string name = GetMemberPropertyName(member);
            JsValue oldVal = GetProperty(objVal, name);
            JsValue newVal = JsValue.From(oldVal.ToNumber() + delta);
            SetProperty(objVal, name, newVal);
            return up.Prefix ? newVal : oldVal;
        }

        throw new JsInterpreterException("Invalid operand for ++/--");
    }

    private JsValue ExecuteCall(CallExpr call)
    {
        JsValue thisValue;
        JsValue callee;

        if (call.Callee is MemberExpr member)
        {
            // Evaluate the object exactly once (side effects must not double)
            JsValue objVal = ExecuteExpression(member.Object);
            string name = GetMemberPropertyName(member);
            thisValue = objVal;
            callee = GetProperty(objVal, name);
        }
        else
        {
            thisValue = GlobalThis();
            callee = ExecuteExpression(call.Callee);
        }

        if (callee.Type != JsType.Function)
        {
            string what = call.Callee switch
            {
                Identifier id => $"'{id.Name}'",
                MemberExpr m => $"'{GetMemberPropertyName(m)}'",
                _ => "expression"
            };
            throw new JsInterpreterException($"{what} is not a function");
        }

        var args = new JsValue[call.Arguments.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = ExecuteExpression(call.Arguments[i]);

        return CallFunction(callee.GetFunction(), thisValue, args);
    }

    private JsValue ExecuteNew(NewExpr newExpr)
    {
        JsValue callee = ExecuteExpression(newExpr.Callee);
        if (callee.Type != JsType.Function)
            throw new JsInterpreterException("Constructor is not a function");

        var args = new JsValue[newExpr.Arguments.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = ExecuteExpression(newExpr.Arguments[i]);

        var constructor = callee.GetFunction();
        var newObj = new JsObject();
        // Constructors without a prototype property still chain to
        // Object.prototype — {}.toString() used to be undefined.
        newObj.Prototype = constructor.Get("prototype") is { Type: JsType.Object } proto
            ? proto.GetObject()
            : ObjectPrototype;

        var result = CallFunction(constructor, JsValue.FromObject(newObj), args);
        return result.Type is JsType.Object or JsType.Function
            ? result
            : JsValue.FromObject(newObj);
    }

    private JsValue ExecuteMember(MemberExpr member)
    {
        JsValue objVal = ExecuteExpression(member.Object);
        return GetProperty(objVal, GetMemberPropertyName(member));
    }

    private JsValue ExecuteFunctionExpr(FunctionExpr f)
    {
        var paramNames = new string[f.Params.Count];
        for (int i = 0; i < paramNames.Length; i++)
            paramNames[i] = f.Params[i].Name;
        return JsValue.FromFunction(new JsFunction(f, paramNames, _currentScope));
    }

    private JsValue ExecuteArray(ArrayExpr arrayExpr)
    {
        var arr = new JsObject { Class = "Array", Prototype = ArrayPrototype };
        int index = 0;
        foreach (var elem in arrayExpr.Elements)
        {
            // Holes and uninitialised slots read back as undefined
            arr.Set(index.ToString(),
                elem != null ? ExecuteExpression(elem) : JsValue.Undefined);
            index++;
        }
        arr.Set("length", JsValue.From(index));
        TrackAlloc(index * 8);
        return JsValue.FromObject(arr);
    }

    private JsValue ExecuteObject(ObjectExpr objExpr)
    {
        // Object literals chain to Object.prototype ({}.toString())
        var obj = new JsObject { Prototype = ObjectPrototype };
        foreach (var prop in objExpr.Properties)
        {
            string key = prop.Key switch
            {
                Identifier id => id.Name,
                StringLiteral s => s.Value,
                _ => ExecuteExpression(prop.Key).ToJsString()
            };
            obj.Set(key, ExecuteExpression(prop.Value));
        }
        TrackAlloc(32);
        return JsValue.FromObject(obj);
    }

    private JsValue ExecuteRegex(RegexLiteral rx)
    {
        var regexObj = new JsObject { Class = "RegExp" };
        regexObj.Set("source", JsValue.From(rx.Pattern));
        regexObj.Set("flags", JsValue.From(rx.Flags));
        regexObj.Set("global", JsValue.From(rx.Flags.Contains('g')));
        regexObj.Set("ignoreCase", JsValue.From(rx.Flags.Contains('i')));
        regexObj.Set("lastIndex", JsValue.From(0));
        // RegExp prototype is installed by JsRuntime
        regexObj.Prototype = _globalScope.Get("RegExp") is { Type: JsType.Function } re
            && re.GetFunction().Get("prototype") is { Type: JsType.Object } proto
            ? proto.GetObject()
            : null;
        return JsValue.FromObject(regexObj);
    }

    private JsValue ExecuteVoid(VoidExpr v)
    {
        ExecuteExpression(v.Argument);
        return JsValue.Undefined;
    }

    private JsValue ExecuteTypeof(TypeofExpr t)
    {
        if (t.Argument is Identifier id && !_currentScope.Has(id.Name))
            return JsValue.From("undefined");
        return JsValue.From(Typeof(ExecuteExpression(t.Argument)));
    }

    private JsValue ExecuteDelete(DeleteExpr d)
    {
        if (d.Argument is Identifier)
            return JsValue.From(false);   // JS 1.1: cannot delete variables

        if (d.Argument is MemberExpr member)
        {
            JsValue objVal = ExecuteExpression(member.Object);
            if (objVal.Type is not (JsType.Object or JsType.Function))
                return JsValue.From(false);
            return JsValue.From(objVal.GetObjectOrFunction().Properties
                .Remove(GetMemberPropertyName(member)));
        }

        return JsValue.From(true);
    }

    private JsValue ExecuteIn(InExpr inExpr)
    {
        JsValue left = ExecuteExpression(inExpr.Left);
        JsValue right = ExecuteExpression(inExpr.Right);
        if (right.Type is not (JsType.Object or JsType.Function))
            return JsValue.From(false);
        return JsValue.From(right.GetObjectOrFunction().Has(left.ToJsString()));
    }

    private JsValue ExecuteInstanceof(InstanceofExpr ins)
    {
        JsValue left = ExecuteExpression(ins.Left);
        JsValue right = ExecuteExpression(ins.Right);
        if (left.Type is not (JsType.Object or JsType.Function) ||
            right.Type != JsType.Function)
            return JsValue.From(false);

        var obj = left.GetObjectOrFunction();
        var ctor = right.GetFunction();
        var ctorProto = ctor.Get("prototype") is { Type: JsType.Object } p ? p.GetObject() : null;
        if (ctorProto == null) return JsValue.From(false);

        var proto = obj.Prototype;
        while (proto != null)
        {
            if (ReferenceEquals(proto, ctorProto)) return JsValue.From(true);
            proto = proto.Prototype;
        }
        return JsValue.From(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Function invocation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The single entry point for calling any function — native or script.
    /// Timers, event handlers, call/apply and sort callbacks all route
    /// through here so script-defined functions actually execute.
    /// </summary>
    public JsValue CallFunction(JsFunction func, JsValue thisValue, JsValue[] args)
    {
        if (func.Native != null)
            return func.Native(thisValue, args);

        if (++_callDepth > MaxCallDepth)
        {
            _callDepth--;
            throw new JsInterpreterException("Maximum call depth exceeded");
        }

        try
        {
            var funcScope = func.ClosureScope.NewChild();
            for (int i = 0; i < func.Params.Count; i++)
                funcScope.Define(func.Params[i],
                    i < args.Length ? args[i] : JsValue.Undefined);
            funcScope.Define("this", thisValue);

            var argsObj = new JsObject { Class = "arguments" };
            for (int i = 0; i < args.Length; i++)
                argsObj.Set(i.ToString(), args[i]);
            argsObj.Set("length", JsValue.From(args.Length));
            funcScope.Define("arguments", JsValue.FromObject(argsObj));

            var body = func.Body ?? throw new JsInterpreterException("Function body is missing");
            Hoist(body.Body.Body, funcScope);

            var old = _currentScope;
            _currentScope = funcScope;
            try
            {
                foreach (var stmt in body.Body.Body)
                    ExecuteStatement(stmt);
                return JsValue.Undefined;
            }
            catch (JsReturnException ret)
            {
                return ret.Value;
            }
            finally
            {
                _currentScope = old;
            }
        }
        finally
        {
            _callDepth--;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Property access (strings/numbers/booleans get their prototypes)
    // ─────────────────────────────────────────────────────────────────────

    private string GetMemberPropertyName(MemberExpr member)
    {
        if (member.Computed)
            return ExecuteExpression(member.Property).ToJsString();
        return ((Identifier)member.Property).Name;
    }

    private static JsValue GetProperty(JsValue target, string name)
    {
        switch (target.Type)
        {
            case JsType.String:
                if (name == "length")
                    return JsValue.From(target.GetString().Length);
                // String char indexing (ES3): 'abc'[1] → 'b'.  Out-of-range
                // and non-integer keys fall through to the prototype → undefined.
                if (int.TryParse(name, out int charIdx) && charIdx >= 0)
                {
                    string s = target.GetString();
                    if (charIdx < s.Length)
                        return JsValue.From(s[charIdx].ToString());
                }
                return StringPrototype != null
                    ? StringPrototype.Get(name)
                    : JsValue.Undefined;

            case JsType.Number:
                return NumberPrototype != null
                    ? NumberPrototype.Get(name)
                    : JsValue.Undefined;

            case JsType.Boolean:
                return BooleanPrototype != null
                    ? BooleanPrototype.Get(name)
                    : JsValue.Undefined;

            case JsType.Object:
            case JsType.Function:
                return target.GetObjectOrFunction().Get(name);

            default:
                return JsValue.Undefined;
        }
    }

    private static void SetProperty(JsValue target, string name, JsValue value)
    {
        // Writes to primitives are silently dropped, JS-style
        if (target.Type is JsType.Object or JsType.Function)
            target.GetObjectOrFunction().Set(name, value);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Operators / conversions
    // ─────────────────────────────────────────────────────────────────────

    private JsValue GlobalThis()
    {
        var w = _globalScope.Get("window");
        return w.Type == JsType.Object ? w : JsValue.Undefined;
    }

    /// <summary>
    /// True for objects carrying a numeric "value" property (Date, Number
    /// wrappers) — their valueOf wins when the other operand is a number.
    /// </summary>
    private static bool IsNumericObject(JsValue v) =>
        v.Type is JsType.Object or JsType.Function &&
        v.GetObjectOrFunction().Properties.TryGetValue("value", out var val) &&
        val.Type == JsType.Number;

    private static JsValue Add(JsValue left, JsValue right)
    {
        // valueOf-first for numeric objects: `date + 86400000` stays
        // numeric instead of concatenating the formatted date string.
        if (IsNumericObject(left) && right.Type == JsType.Number)
            return JsValue.From(left.ToNumber() + right.GetNumber());
        if (left.Type == JsType.Number && IsNumericObject(right))
            return JsValue.From(left.GetNumber() + right.ToNumber());

        // ToPrimitive both sides first — object + object used to fall
        // straight to numbers and produce NaN ("[1,2]" + "[3]" must join).
        JsValue l = left.ToPrimitive();
        JsValue r = right.ToPrimitive();

        // String + anything → string; the era's document.write glue
        if (l.Type == JsType.String || r.Type == JsType.String)
            return JsValue.From(l.ToJsString() + r.ToJsString());
        return JsValue.From(l.ToNumber() + r.ToNumber());
    }

    /// <summary>JS ToInt32: NaN/Inf → 0, wraps modulo 2^32 into signed range.</summary>
    private static int ToInt32(JsValue v)
    {
        double d = v.ToNumber();
        if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
        d = Math.Truncate(d);
        double m = d % 4294967296.0;
        if (m < 0) m += 4294967296.0;
        if (m >= 2147483648.0) return (int)(m - 4294967296.0);
        return (int)m;
    }

    /// <summary>
    /// JS relational comparison: both strings → lexicographic; otherwise
    /// numeric via ToNumber (valueOf-first, so date &lt; date compares
    /// milliseconds — the old ToPrimitive path stringified dates first and
    /// every such comparison was NaN/false), and any NaN makes every
    /// comparison false.
    /// </summary>
    private static bool LessThan(JsValue a, JsValue b)
    {
        if (a.Type == JsType.String && b.Type == JsType.String)
            return string.CompareOrdinal(a.GetString(), b.GetString()) < 0;

        double an = a.ToNumber();
        double bn = b.ToNumber();
        if (double.IsNaN(an) || double.IsNaN(bn)) return false;
        return an < bn;
    }

    private static string Typeof(JsValue value) => value.Type switch
    {
        JsType.Undefined => "undefined",
        JsType.Null => "object",       // the famous quirk
        JsType.Boolean => "boolean",
        JsType.Number => "number",
        JsType.String => "string",
        JsType.Function => "function",
        JsType.Object => "object",
        _ => "undefined"
    };

    /// <summary>
    /// Object stringifier for "" + obj / String(obj):
    /// arrays join with ',' (null/undefined → empty), dates print in
    /// Navigator's format, errors print name: message.
    /// </summary>
    private static string StringifyObject(JsObject obj)
    {
        switch (obj.Class)
        {
            case "Array":
                {
                    int len = obj.Get("length") is { Type: JsType.Number } l ? (int)l.GetNumber() : 0;
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < len; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var v = obj.Get(i.ToString());
                        if (v.Type is not (JsType.Null or JsType.Undefined))
                            sb.Append(v.ToJsString());
                    }
                    return sb.ToString();
                }

            case "Date":
                {
                    double ms = obj.Get("value") is { Type: JsType.Number } v ? v.GetNumber() : double.NaN;
                    if (double.IsNaN(ms)) return "Invalid Date";
                    var d = DateTime1970 + TimeSpan.FromMilliseconds(ms);
                    return d.ToString("ddd MMM dd HH:mm:ss yyyy",
                        System.Globalization.CultureInfo.InvariantCulture);
                }

            case "Error":
                {
                    string name = obj.Get("name") is { Type: JsType.String } n ? n.GetString() : "Error";
                    string msg = obj.Get("message") is { Type: JsType.String } m ? m.GetString() : "";
                    return msg.Length > 0 ? $"{name}: {msg}" : name;
                }

            default:
                return "[object Object]";
        }
    }

    private static readonly DateTime DateTime1970 =
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ─────────────────────────────────────────────────────────────────────
    // Hoisting
    // ─────────────────────────────────────────────────────────────────────

    private void Hoist(IReadOnlyList<Stmt> stmts, JsScope targetScope)
    {
        foreach (var stmt in stmts)
            HoistOne(stmt, targetScope);
    }

    private void HoistOne(Stmt stmt, JsScope targetScope)
    {
        switch (stmt)
        {
            case FunctionDeclaration fn:
                var paramNames = new string[fn.Params.Count];
                for (int i = 0; i < paramNames.Length; i++)
                    paramNames[i] = fn.Params[i].Name;
                var func = new JsFunction(
                    new FunctionExpr(fn.Id, fn.Params, fn.Body), paramNames, targetScope);
                targetScope.Define(fn.Id.Name, JsValue.FromFunction(func));
                break;

            case VarDeclaration varDecl:
                foreach (var d in varDecl.Declarations)
                    if (!targetScope.Has(d.Id.Name))
                        targetScope.Define(d.Id.Name, JsValue.Undefined);
                break;

            case BlockStatement block:
                foreach (var s in block.Body) HoistOne(s, targetScope);
                break;

            case IfStatement i:
                HoistOne(i.Consequent, targetScope);
                if (i.Alternate != null) HoistOne(i.Alternate, targetScope);
                break;

            case WhileStatement w: HoistOne(w.Body, targetScope); break;
            case DoWhileStatement dw: HoistOne(dw.Body, targetScope); break;

            case ForStatement f:
                if (f.Init is VarDeclaration vd) HoistOne(vd, targetScope);
                HoistOne(f.Body, targetScope);
                break;

            case ForInStatement fi: HoistOne(fi.Body, targetScope); break;

            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                    foreach (var s in c.Consequent) HoistOne(s, targetScope);
                break;

            case TryStatement t:
                HoistOne(t.Block, targetScope);
                if (t.Handler != null) HoistOne(t.Handler.Body, targetScope);
                if (t.Finalizer != null) HoistOne(t.Finalizer, targetScope);
                break;

            case LabeledStatement ls: HoistOne(ls.Body, targetScope); break;
            case WithStatement ws: HoistOne(ws.Body, targetScope); break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Resource guards
    // ─────────────────────────────────────────────────────────────────────

    private void CheckTimeout()
    {
        if (_stopwatch.ElapsedMilliseconds > _timeLimitMs)
            throw new JsTimeoutException();
    }

    private void TrackAlloc(long bytes)
    {
        _allocatedBytes += bytes;
        if (_allocatedBytes > _heapLimitBytes)
            throw new JsOutOfMemoryException();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Interpreter-aware builtins (eval / call / apply / timers / callbacks)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Install the builtins that need a live interpreter: eval,
    /// Function.prototype.call/apply, setTimeout/setInterval/clearTimeout/
    /// clearInterval on window and the global scope, and the array methods
    /// that invoke script callbacks (sort/forEach/filter/map).  Call after
    /// JsRuntime.PopulateGlobalScope and DomBindings.RegisterAll.
    /// </summary>
    private void PublishConsole(string level, string message)
    {
        ConsoleMessage?.Invoke(new ConsoleEntry(level, message, DateTime.Now));
    }

    public void RegisterRuntimeBuiltins()
    {
        var console = new JsObject { Class = "Console" };
        foreach (string level in new[] { "log", "info", "warn", "error", "debug" })
        {
            string capturedLevel = level;
            console.Set(level, Native((self, args) =>
            {
                string message = string.Join(" ", args.Select(a => a.ToJsString()));
                ConsoleMessage?.Invoke(new ConsoleEntry(capturedLevel, message, DateTime.Now));
                return JsValue.Undefined;
            }, "console." + level));
        }
        _globalScope.Define("console", JsValue.FromObject(console));
        WindowObject?.Set("console", JsValue.FromObject(console));

        Native((self, args) =>
        {
            if (args.Length == 0) return JsValue.Undefined;
            if (args[0].Type != JsType.String) return args[0];
            return EvalString(args[0].GetString(), _currentScope);
        }, "eval", global: true);

        // Function.prototype.call / apply
        if (_globalScope.Get("Function") is { Type: JsType.Function } funcCtor &&
            funcCtor.GetFunction().Get("prototype") is { Type: JsType.Object } fp)
        {
            var funcProto = fp.GetObject();

            funcProto.Set("call", Native((self, args) =>
            {
                if (self.Type != JsType.Function)
                    throw new JsInterpreterException("Function.prototype.call on non-function");
                var thisArg = args.Length > 0 ? args[0] : GlobalThis();
                var rest = args.Length > 1 ? args[1..] : Array.Empty<JsValue>();
                return CallFunction(self.GetFunction(), thisArg, rest);
            }, "call"));

            funcProto.Set("apply", Native((self, args) =>
            {
                if (self.Type != JsType.Function)
                    throw new JsInterpreterException("Function.prototype.apply on non-function");
                var thisArg = args.Length > 0 ? args[0] : GlobalThis();
                var callArgs = new List<JsValue>();
                if (args.Length > 1 && args[1].Type == JsType.Object)
                {
                    var arr = args[1].GetObjectOrFunction();
                    int len = arr.Get("length") is { Type: JsType.Number } l ? (int)l.GetNumber() : 0;
                    for (int i = 0; i < len; i++)
                        callArgs.Add(arr.Get(i.ToString()));
                }
                return CallFunction(self.GetFunction(), thisArg, callArgs.ToArray());
            }, "apply"));
        }

        // Timers on window + global
        Native((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(0);
            int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
            return JsValue.From(SetTimeout(args[0], delay, repeat: false));
        }, "setTimeout", global: true);

        Native((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(0);
            int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
            return JsValue.From(SetTimeout(args[0], delay, repeat: true));
        }, "setInterval", global: true);

        Native((self, args) =>
        {
            if (args.Length > 0) ClearTimer((int)args[0].ToNumber());
            return JsValue.Undefined;
        }, "clearTimeout", global: true);

        Native((self, args) =>
        {
            if (args.Length > 0) ClearTimer((int)args[0].ToNumber());
            return JsValue.Undefined;
        }, "clearInterval", global: true);

        // Array methods that take script callbacks
        if (ArrayPrototype != null)
        {
            ArrayPrototype.Set("sort", Native((self, args) =>
                SortArray(self, args), "sort"));

            ArrayPrototype.Set("forEach", Native((self, args) =>
                IterateCallbackArray(self, args, "forEach"), "forEach"));

            ArrayPrototype.Set("map", Native((self, args) =>
                MapArray(self, args), "map"));

            ArrayPrototype.Set("filter", Native((self, args) =>
                FilterArray(self, args), "filter"));
        }
    }

    private JsValue Native(Func<JsValue, JsValue[], JsValue> impl, string name,
                          bool global = false)
    {
        var value = JsValue.FromFunction(new JsFunction(impl, _globalScope, name));
        if (global)
        {
            _globalScope.Define(name, value);
            if (WindowObject != null)
                WindowObject.Set(name, value);
        }
        return value;
    }

    private JsValue SortArray(JsValue self, JsValue[] args)
    {
        if (self.Type is not (JsType.Object or JsType.Function))
            return self;
        var arr = self.GetObjectOrFunction();
        int len = arr.Get("length") is { Type: JsType.Number } l ? (int)l.GetNumber() : 0;
        if (len <= 1) return self;

        var elements = new List<JsValue>(len);
        for (int i = 0; i < len; i++)
            elements.Add(arr.Get(i.ToString()));

        JsFunction? compareFn =
            args.Length > 0 && args[0].Type == JsType.Function ? args[0].GetFunction() : null;

        // Insertion sort — stable, and safe with the interpreter's callbacks
        for (int i = 1; i < elements.Count; i++)
        {
            var x = elements[i];
            int j = i - 1;
            while (j >= 0 && CompareSort(elements[j], x, compareFn) > 0)
            {
                elements[j + 1] = elements[j];
                j--;
            }
            elements[j + 1] = x;
        }

        for (int i = 0; i < len; i++)
            arr.Set(i.ToString(), elements[i]);
        return self;
    }

    private int CompareSort(JsValue a, JsValue b, JsFunction? fn)
    {
        if (fn != null)
        {
            var r = CallFunction(fn, JsValue.Undefined, new[] { a, b });
            double d = r.ToNumber();
            if (double.IsNaN(d)) return 0;
            return d < 0 ? -1 : d > 0 ? 1 : 0;
        }
        // Default: undefined last, then string comparison
        if (a.Type == JsType.Undefined) return b.Type == JsType.Undefined ? 0 : 1;
        if (b.Type == JsType.Undefined) return -1;
        return string.CompareOrdinal(a.ToJsString(), b.ToJsString());
    }

    private JsValue MapArray(JsValue self, JsValue[] args) => IterateCallbackArray(self, args, "map");
    private JsValue FilterArray(JsValue self, JsValue[] args) => IterateCallbackArray(self, args, "filter");

    private JsValue IterateCallbackArray(JsValue self, JsValue[] args, string mode)
    {
        if (self.Type is not (JsType.Object or JsType.Function))
            return JsValue.Undefined;
        if (args.Length == 0 || args[0].Type != JsType.Function)
            throw new JsInterpreterException($"{mode}() requires a function");
        var arr = self.GetObjectOrFunction();
        var fn = args[0].GetFunction();
        int len = arr.Get("length") is { Type: JsType.Number } l ? (int)l.GetNumber() : 0;

        var mapped = mode is "map" or "filter"
            ? new JsObject { Class = "Array", Prototype = ArrayPrototype }
            : null;

        for (int i = 0; i < len; i++)
        {
            var item = arr.Get(i.ToString());
            var r = CallFunction(fn, GlobalThis(), new[] { item, JsValue.From(i), self });
            if (mode == "map")
                mapped!.Set(i.ToString(), r);
            else if (mode == "filter" && r.ToBoolean())
                AppendToArray(mapped ??= new JsObject { Class = "Array", Prototype = ArrayPrototype }, item);
        }

        if (mapped != null)
        {
            int n = 0;
            while (mapped.HasOwn(n.ToString())) n++;
            mapped.Set("length", JsValue.From(n));
            return JsValue.FromObject(mapped);
        }
        return JsValue.Undefined;
    }

    private static void AppendToArray(JsObject arr, JsValue item)
    {
        int n = arr.Get("length") is { Type: JsType.Number } l ? (int)l.GetNumber() : 0;
        while (arr.HasOwn(n.ToString())) n++;
        arr.Set(n.ToString(), item);
        arr.Set("length", JsValue.From(n + 1));
    }
}