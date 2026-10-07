using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
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

public sealed class JsTypeErrorException : JsInterpreterException
{
    public JsTypeErrorException(string message) : base(message) { }
}

public sealed class JsReferenceErrorException : JsInterpreterException
{
    public JsReferenceErrorException(string message) : base(message) { }
}

public sealed class JsRangeErrorException : JsInterpreterException
{
    public JsRangeErrorException(string message) : base(message) { }
}

public sealed class JsSyntaxErrorException : JsInterpreterException
{
    public JsSyntaxErrorException(string message) : base(message) { }
}

public sealed class JsUriErrorException : JsInterpreterException
{
    public JsUriErrorException(string message) : base(message) { }
}

/// <summary>
/// Tree-walking interpreter for JavaScript 1.1/1.2.
///
/// Era-correct behaviours baked in:
///   • var is function-scoped; blocks do not open scopes
///   • plain calls get the global object as 'this'
///   • timers (setTimeout/setInterval) take script functions OR the era's
///     string-code form — the callback runs through CallFunction
///   • runtime errors convert to catchable Error/TypeError objects inside
///     try/catch, and any script error stops only that script, never the renderer
///   • recursion depth, execution time, and string-allocation totals are
///     all capped so hostile pages cannot hang the browser
/// </summary>
public class JsInterpreter
{
    public sealed record ConsoleEntry(string Level, string Message, DateTime Timestamp);
    public event Action<ConsoleEntry>? ConsoleMessage;
    public event Action? ScriptExecutionCompleted;
    public bool IsExecuting => _isExecuting;
    private readonly JsScope _globalScope;
    private JsScope _currentScope;
    private readonly Action<string> _onNavigate;
    private readonly Action<string> _setStatus;

    private readonly int _timeLimitMs;
    private readonly int _heapLimitBytes;
    private long _allocatedBytes;
    private readonly Stopwatch _stopwatch = new();

    private int _callDepth;
    private readonly int _maxCallDepth;
    private int _timeoutPauseDepth;
    private bool _timeoutWasRunningBeforePause;

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

    private void CompleteExecution(bool isOutermost)
    {
        if (!isOutermost) return;
        _isExecuting = false;
        ScriptExecutionCompleted?.Invoke();
    }

    private sealed class TimeoutBudgetPause : IDisposable
    {
        private JsInterpreter? _interpreter;

        public TimeoutBudgetPause(JsInterpreter interpreter) =>
            _interpreter = interpreter;

        public void Dispose()
        {
            var interpreter = _interpreter;
            _interpreter = null;
            interpreter?.ResumeTimeoutBudget();
        }
    }

    internal IDisposable PauseTimeoutBudget()
    {
        if (_timeoutPauseDepth++ == 0)
        {
            _timeoutWasRunningBeforePause = _stopwatch.IsRunning;
            _stopwatch.Stop();
        }
        return new TimeoutBudgetPause(this);
    }

    private void ResumeTimeoutBudget()
    {
        if (_timeoutPauseDepth <= 0)
            throw new InvalidOperationException("Unbalanced JavaScript timeout pause.");
        if (--_timeoutPauseDepth == 0 && _timeoutWasRunningBeforePause)
            _stopwatch.Start();
    }

    // DOM-0 event-property handlers belong to the DOM element, not to a
    // particular JavaScript wrapper object. Wrappers can be recreated by
    // rebinding/DOM collection access; keeping handlers only on the wrapper
    // makes `element.onclick = fn` silently disappear while inline
    // `onclick="..."` continues to work. Keep the live function here keyed
    // by the actual DomElement so dispatch and property reads share one
    // source of truth.
    private readonly Dictionary<DomElement, Dictionary<string, JsValue>> _domEventProperties = new();

    // IE5 attachEvent() registry: per element+event, in registration order.
    // detachEvent removes by function identity. These run AFTER the DOM-0
    // and inline attribute handlers of the same element (the documented IE
    // ordering) and participate in the bubble chain.
    private readonly Dictionary<DomElement, Dictionary<string, List<JsValue>>> _attachEventHandlers = new();

    // Netscape captureEvents() mask (window.captureEvents(Event.CLICK), the
    // NS4 capture model). Captured event types are dispatched to the
    // window's own handler (handleEvent / on<event>) before the target.
    private int _capturedEventMask;

    /// <summary>Normalizes "onclick"/"click" spellings to the on-form.</summary>
    private static string NormalizeEventKey(string eventName) =>
        eventName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            ? eventName.ToLowerInvariant()
            : "on" + eventName.ToLowerInvariant();

    /// <summary>Netscape 4 event-bit masks for captureEvents/releaseEvents.</summary>
    internal static int EventMaskFor(string normalizedEvent) => normalizedEvent switch
    {
        "onmousedown" => 0x00000001,
        "onmouseup"   => 0x00000002,
        "onclick"     => 0x00000004,
        "ondblclick"  => 0x00000008,
        "onmousemove" => 0x00000010,
        "onmouseover" => 0x00000020,
        "onmouseout"  => 0x00000040,
        "onkeypress"  => 0x00000080,
        "onkeydown"   => 0x00000100,
        "onkeyup"     => 0x00000200,
        "onfocus"     => 0x00000400,
        "onblur"      => 0x00000800,
        "onselect"    => 0x00001000,
        "onchange"    => 0x00002000,
        "onsubmit"    => 0x00004000,
        "onreset"     => 0x00008000,
        "onload"      => 0x00010000,
        "onunload"    => 0x00020000,
        _ => 0
    };

    /// <summary>window.captureEvents(mask) — NS4 event capture.</summary>
    internal void CaptureEvents(int mask) => _capturedEventMask |= mask;

    /// <summary>window.releaseEvents(mask) — NS4 event capture release.</summary>
    internal void ReleaseEvents(int mask) => _capturedEventMask &= ~mask;

    /// <summary>IE5 attachEvent(name, fn) — records the handler.</summary>
    internal void AttachEventHandler(DomElement element, string eventName, JsValue handler)
    {
        if (handler.Type != JsType.Function) return;
        string key = NormalizeEventKey(eventName);
        if (!_attachEventHandlers.TryGetValue(element, out var byEvent))
            _attachEventHandlers[element] = byEvent = new(StringComparer.OrdinalIgnoreCase);
        if (!byEvent.TryGetValue(key, out var list))
            byEvent[key] = list = new();
        list.Add(handler);
    }

    /// <summary>IE5 detachEvent(name, fn) — removes the matching registration.
    /// Returns true when a registration was removed.</summary>
    internal bool DetachEventHandler(DomElement element, string eventName, JsValue handler)
    {
        string key = NormalizeEventKey(eventName);
        if (!_attachEventHandlers.TryGetValue(element, out var byEvent) ||
            !byEvent.TryGetValue(key, out var list))
            return false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].StrictEquals(handler))
            {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private IReadOnlyList<JsValue> GetAttachedHandlers(DomElement element, string normalizedEvent)
    {
        if (_attachEventHandlers.TryGetValue(element, out var byEvent) &&
            byEvent.TryGetValue(normalizedEvent, out var list))
            return list;
        return Array.Empty<JsValue>();
    }

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
    public static JsObject? FunctionPrototype { get; set; }

    // ─────────────────────────────────────────────────────────────────────

    public JsInterpreter(JsScope globalScope, DomDocument? document,
                         Action<string> onNavigate, Action<string> setStatus,
                         int timeLimitMs = 5000, int heapLimitBytes = 10_485_760,
                         int maxCallDepth = 400)
    {
        _globalScope = globalScope ?? throw new ArgumentNullException(nameof(globalScope));
        _currentScope = globalScope;
        _onNavigate = onNavigate ?? (_ => { });
        _setStatus = setStatus ?? (_ => { });
        _timeLimitMs = timeLimitMs;
        _heapLimitBytes = heapLimitBytes;
        _maxCallDepth = Math.Clamp(maxCallDepth, 50, 2000);

        // Object stringification (array join, date toString) for "" + obj
        JsObject.Stringifier = StringifyObject;
        InstallPrimitiveConversion();
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

    /// <summary>
    /// Shell-runner opt-in (conformance harness): when true, uncaught
    /// top-level exceptions — thrown JS values, engine runtime errors,
    /// parse errors, timeouts and heap exhaustion — propagate out of
    /// Execute/ExecuteString instead of being reported to the host and
    /// swallowed. This mirrors SpiderMonkey shell semantics, where an
    /// uncaught exception aborts the script with a nonzero exit status.
    /// The browser default (false) is unchanged: a page script error is
    /// reported and the page keeps running.
    /// </summary>
    public bool PropagateTopLevelExceptions { get; set; }

    private static string DescribeJsObject(JsObject obj)
    {
        if (obj is DomBindings.ElementWrapper elementWrapper)
        {
            var el = elementWrapper.Element;
            string id = el.GetAttr("id") ?? "";
            return $"<{el.TagName.ToLowerInvariant()}>" +
                   (id.Length > 0 ? $"#{id}" : "") +
                   $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(el)}";
        }
        return $"Class={obj.Class}@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj)}";
    }

    private static string DescribeJsValue(JsValue value)
    {
        if (value.Type == JsType.String)
            return $"string:'{value.ToJsString()}'";
        if (value.Type == JsType.Function)
            return $"function:'{value.GetFunction()?.Name ?? "<anonymous>"}'";
        if (value.Type == JsType.Object)
            return $"object:{DescribeJsObject(value.GetObjectOrFunction())}";
        return $"{value.Type}:{value.ToJsString()}";
    }

    private void InstallPrimitiveConversion()
    {
        JsValue objectConstructor = _globalScope.Get("Object");
        if (objectConstructor.Type == JsType.Function &&
            objectConstructor.GetFunction().Get("prototype") is { Type: JsType.Object } prototype)
            prototype.GetObject().PrimitiveConverter = ConvertToPrimitive;
    }

    private JsValue ConvertToPrimitive(JsObject obj, bool preferString)
    {
        JsValue receiver = obj is JsFunction function
            ? JsValue.FromFunction(function)
            : JsValue.FromObject(obj);
        string firstMethod = preferString ? "toString" : "valueOf";
        string secondMethod = preferString ? "valueOf" : "toString";

        foreach (string methodName in new[] { firstMethod, secondMethod })
        {
            JsValue method = GetProperty(receiver, methodName);
            if (method.Type != JsType.Function)
                continue;

            JsValue result = CallFunction(method.GetFunction(), receiver, Array.Empty<JsValue>());
            if (result.Type is not (JsType.Object or JsType.Function))
                return result;
        }

        throw new JsTypeErrorException("Cannot convert object to primitive value");
    }

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
            if (PropagateTopLevelExceptions) throw;
            const string message = "Script execution timed out";
            _setStatus(message);
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsOutOfMemoryException)
        {
            if (PropagateTopLevelExceptions) throw;
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
            if (PropagateTopLevelExceptions) throw;
            string message = $"Uncaught {ex.Value.ToJsString()}";
            if (ReportWindowOnError(message)) return JsValue.Undefined;
            _setStatus($"Script error: {ex.Value.ToJsString()}");
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsInterpreterException ex)
        {
            if (PropagateTopLevelExceptions) throw;
            Retro96.DebugLog.JsWrite($"SCRIPT_ERROR {ex.GetType().Name}: {ex.Message}");
            string message = $"Uncaught {ex.Message}";
            if (ReportWindowOnError(message)) return JsValue.Undefined;
            _setStatus($"Script error: {ex.Message}");
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        catch (JsBreakException) { if (PropagateTopLevelExceptions) throw; return JsValue.Undefined; }
        catch (JsContinueException) { if (PropagateTopLevelExceptions) throw; return JsValue.Undefined; }
        catch (JsReturnException) { if (PropagateTopLevelExceptions) throw; return JsValue.Undefined; }
        finally
        {
            CompleteExecution(isOutermost);
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
            if (PropagateTopLevelExceptions) throw;
            Retro96.DebugLog.JsWrite($"SCRIPT_PARSE_ERROR line={ex.Line} column={ex.Column}: {ex.Message}");
            string message = $"Syntax error at line {ex.Line}, column {ex.Column}: {ex.Message}";
            if (!ReportWindowOnError(message))
            {
                _setStatus($"Script error: {ex.Message}");
                PublishConsole("error", message);
            }
            return JsValue.Undefined;
        }
    }

    /// <summary>Depth of DIRECT eval — §10.2.2: bindings created by eval
    /// code carry no DontDelete (delete of an eval'd var is true).</summary>
    private int _evalDepth;

    /// <summary>§10.1.8 mapped-arguments bookkeeping — argument index ↔
    /// parameter name, keyed by the arguments object and by the function
    /// scope so both write directions find the pairing cheaply.</summary>
    private sealed class ArgumentsBinding
    {
        public JsScope Scope = null!;
        public JsObject ArgsObj = null!;
        public readonly Dictionary<string, string> IndexToParam = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ParamToIndex = new(StringComparer.Ordinal);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsObject, ArgumentsBinding> _argumentsBindingsByObj = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsScope, ArgumentsBinding> _argumentsBindingsByScope = new();

    /// <summary>Propagate a parameter write to the mapped arguments slot
    /// (§10.1.8: arguments[i] and the parameter share their value).</summary>
    private static void SyncArgumentsFromParam(JsScope from, string name, JsValue value)
    {
        for (var scope = from; scope != null; scope = scope.Parent)
        {
            if (!scope.HasArgumentsBinding) continue;
            if (_argumentsBindingsByScope.TryGetValue(scope, out var b) &&
                b.ParamToIndex.TryGetValue(name, out var idx) &&
                b.ArgsObj.HasOwn(idx))   // delete breaks the mapping (§10.1.8)
                b.ArgsObj.Set(idx, value);
        }
    }

    /// <summary>Eval a string in an explicit scope (eval()).</summary>
    public JsValue EvalString(string source, JsScope scope)
    {
        ProgramNode program;
        try { program = JsParser.Parse(source); }
        catch (JsParserException ex)
        {
            var syntaxErrorConstructor = scope.Get("SyntaxError");
            JsObject? prototype = syntaxErrorConstructor.Type == JsType.Function &&
                syntaxErrorConstructor.GetObjectOrFunction().Get("prototype") is { Type: JsType.Object } prototypeValue
                    ? prototypeValue.GetObject()
                    : null;
            var error = new JsObject { Class = "Error", Prototype = prototype };
            error.Set("name", JsValue.From("SyntaxError"));
            error.Set("message", JsValue.From(ex.RawMessage));
            throw new JsThrownException(JsValue.FromObject(error));
        }
        var old = _currentScope;
        _currentScope = scope;
        _evalDepth++;
        try
        {
            return Execute(program);
        }
        finally
        {
            _evalDepth--;
            _currentScope = old;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Events (inline handlers + JS-assigned handlers)
    // ─────────────────────────────────────────────────────────────────────

    internal void SetDomEventProperty(DomElement element, string eventName, JsValue value)
    {
        string key = eventName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            ? eventName.ToLowerInvariant()
            : "on" + eventName.ToLowerInvariant();

        if (!_domEventProperties.TryGetValue(element, out var handlers))
        {
            handlers = new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase);
            _domEventProperties[element] = handlers;
        }
        handlers[key] = value;
        string id = element.GetAttr("id") ?? "";
        Retro96.DebugLog.JsWrite($"DOM_EVENT_STORE SET element=<{element.TagName.ToLowerInvariant()}>" +
            (id.Length > 0 ? $"#{id}" : "") +
            $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element)} event='{key}' value={DescribeJsValue(value)}");
    }

    internal void ClearDomEventProperty(DomElement element, string eventName)
    {
        string key = eventName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            ? eventName.ToLowerInvariant()
            : "on" + eventName.ToLowerInvariant();
        if (_domEventProperties.TryGetValue(element, out var handlers))
        {
            bool removed = handlers.Remove(key);
            if (handlers.Count == 0)
                _domEventProperties.Remove(element);
            string id = element.GetAttr("id") ?? "";
            Retro96.DebugLog.JsWrite($"DOM_EVENT_STORE CLEAR element=<{element.TagName.ToLowerInvariant()}>" +
                (id.Length > 0 ? $"#{id}" : "") +
                $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element)} event='{key}' removed={removed}");
        }
        else
        {
            string id = element.GetAttr("id") ?? "";
            Retro96.DebugLog.JsWrite($"DOM_EVENT_STORE CLEAR element=<{element.TagName.ToLowerInvariant()}>" +
                (id.Length > 0 ? $"#{id}" : "") +
                $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element)} event='{key}' removed=false(no-store)");
        }
    }

    internal bool TryGetDomEventProperty(DomElement element, string eventName, out JsValue value)
    {
        string key = eventName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            ? eventName.ToLowerInvariant()
            : "on" + eventName.ToLowerInvariant();
        if (_domEventProperties.TryGetValue(element, out var handlers) &&
            handlers.TryGetValue(key, out var found) &&
            found != null)
        {
            value = found;
            string id = element.GetAttr("id") ?? "";
            Retro96.DebugLog.JsWrite($"DOM_EVENT_STORE GET element=<{element.TagName.ToLowerInvariant()}>" +
                (id.Length > 0 ? $"#{id}" : "") +
                $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element)} event='{key}' -> {DescribeJsValue(found)}");
            return true;
        }
        value = JsValue.Undefined;
        string missId = element.GetAttr("id") ?? "";
        Retro96.DebugLog.JsWrite($"DOM_EVENT_STORE GET element=<{element.TagName.ToLowerInvariant()}>" +
            (missId.Length > 0 ? $"#{missId}" : "") +
            $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element)} event='{key}' -> MISS");
        return false;
    }

    /// <summary>
    /// Fire an event handler with the element as 'this' and an 'event'
    /// object in scope.  Handles all the 1999 handler sources per element,
    /// in this order:
    ///   • JS-assigned functions (element.onclick = fn) — the sentinel
    ///     "__js_handler__" in EventHandlers marks those
    ///   • inline attribute source ("onclick" attribute)
    ///   • attachEvent("onclick", fn) registrations, in registration order
    /// (checklist §11). IE-style bubbling then walks the ancestor chain
    /// (element → body) unless a handler set event.cancelBubble = true.
    /// Cancellation: any handler returning false OR setting
    /// event.returnValue = false makes the dispatch return false so the
    /// shell cancels the default action (link navigation / submit).
    /// Returns the last handler's value; boolean true from any handler
    /// (the onMouseOver "keep status text" idiom) is preserved.
    /// </summary>
    public JsValue FireEvent(DomElement element, string eventName,
                             JsObject? eventObj = null)
    {
        string normalizedEvent = NormalizeEventKey(eventName);
        string elementId = element.GetAttr("id") ?? "";
        Retro96.DebugLog.JsWrite($"FIRE_EVENT element=<{element.TagName.ToLowerInvariant()}>" +
            (elementId.Length > 0 ? $"#{elementId}" : "") +
            $"@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element)} event='{normalizedEvent}'");

        // BrowserCanvas supplies a real mouse event for physical input.
        // Programmatic/test dispatches also need the IE global event object,
        // otherwise window.event is inexplicably null even though the handler
        // itself is firing. Synthesize the legacy shape when the caller has no
        // concrete event payload.
        if (eventObj == null && IsMouseEventName(normalizedEvent))
            eventObj = CreateMouseEvent(normalizedEvent, 0, 0, 0);

        // Every dispatched event object carries its type ("click",
        // "keypress", …) — the NS4 which/pageX derivation and era scripts
        // both read e.type.
        if (eventObj != null && !eventObj.HasOwn("type"))
            eventObj.Set("type", JsValue.From(
                normalizedEvent.StartsWith("on", StringComparison.Ordinal) && normalizedEvent.Length > 2
                    ? normalizedEvent[2..]
                    : normalizedEvent));

        // Netscape 4 event shape: target / pageX / pageY / which / modifiers.
        if (eventObj != null && BrowserRuntime.SupportsNetscapeLegacy)
            ApplyNetscapeEventFields(eventObj, element);

        // Seed the IE cancel defaults ONCE for the whole dispatch so a
        // handler-set returnValue=false survives the bubble chain.
        if (eventObj != null && BrowserRuntime.SupportsInternetExplorerLegacy &&
            !eventObj.HasOwn("returnValue"))
            eventObj.Set("returnValue", JsValue.From(true));
        if (eventObj != null && !eventObj.HasOwn("cancelBubble"))
            eventObj.Set("cancelBubble", JsValue.From(false));

        // Netscape capture model: events whose mask was captured via
        // window.captureEvents() go to the window's own handler first.
        if (eventObj != null && BrowserRuntime.SupportsNetscapeLegacy)
            RunCapturedWindowHandler(element, normalizedEvent, eventObj);

        bool anyHandled = false, cancel = false, keepTrue = false;
        JsValue last = JsValue.Undefined;

        // srcElement / target always reference the ORIGINAL dispatch target,
        // even while handlers run on bubbling ancestors.
        var originalWrapper = ElementWrapperHook?.Invoke(element);
        JsValue? srcElementValue = originalWrapper != null
            ? JsValue.FromObject(originalWrapper)
            : null;

        foreach (var target in BuildDispatchChain(element, normalizedEvent))
        {
            var (result, handled) = DispatchToElement(target, srcElementValue, normalizedEvent, eventObj);
            if (!handled) continue;

            anyHandled = true;
            last = result;
            if (result.Type == JsType.Boolean)
            {
                if (result.GetBool()) keepTrue = true;
                else cancel = true;
            }

            // event.cancelBubble = true stops the walk to ancestors (IE5).
            if (eventObj != null && IsCancelBubbleSet(eventObj))
                break;
        }

        // event.returnValue = false cancels the default action, exactly like
        // a literal "return false" (checklist §11 IE5 model).
        if (eventObj != null)
        {
            var rv = eventObj.Get("returnValue");
            if (rv.Type == JsType.Boolean && !rv.GetBool())
                cancel = true;
        }

        if (cancel) return JsValue.From(false);
        if (keepTrue) return JsValue.From(true);
        return anyHandled ? last : JsValue.Undefined;
    }

    /// <summary>IE5 bubbles most interaction events up the ancestor chain;
    /// load/unload/submit/reset/focus/blur/error stay on their target
    /// (documented IE behaviour — focus/blur never bubbled before 5.5's
    /// focusin/focusout).</summary>
    private static bool IsBubblingEventName(string normalizedEvent) => normalizedEvent is
        "onclick" or "ondblclick" or "onmousedown" or "onmouseup" or
        "onmousemove" or "onmouseover" or "onmouseout" or
        "onkeydown" or "onkeyup" or "onkeypress" or "onchange";

    private static IEnumerable<DomElement> BuildDispatchChain(DomElement element, string normalizedEvent)
    {
        yield return element;
        if (!IsBubblingEventName(normalizedEvent)) yield break;
        for (var ancestor = element.Parent as DomElement;
             ancestor != null;
             ancestor = ancestor.Parent as DomElement)
            yield return ancestor;
        // Documented limitation: bubbling covers ELEMENT ancestors only —
        // document- and window-level handlers are not part of the chain.
    }

    private static bool IsCancelBubbleSet(JsObject eventObj)
    {
        var cb = eventObj.Get("cancelBubble");
        return cb.Type == JsType.Boolean ? cb.GetBool() : cb.ToBoolean();
    }

    /// <summary>Dispatches one element's handlers for the event: DOM-0
    /// property handler → wrapper property handler → inline attribute
    /// source → attachEvent registrations in order. srcElement always
    /// reflects the ORIGINAL target, not the bubbling ancestor.</summary>
    private (JsValue Result, bool Handled) DispatchToElement(
        DomElement target, JsValue? srcElementValue, string normalizedEvent, JsObject? eventObj)
    {
        var wrapperForProperty = ElementWrapperHook?.Invoke(target);

        bool handled = false;
        JsValue last = JsValue.Undefined;

        // The DOM-0 slot: JS-assigned function (element.onclick = fn),
        // a wrapper property, or the inline attribute source — first one
        // present wins; they are alternative representations of the same
        // on<event> property.
        if (TryGetDomEventProperty(target, normalizedEvent, out var propertyHandler) &&
            propertyHandler.Type == JsType.Function)
        {
            Retro96.DebugLog.JsWrite($"FIRE_EVENT PATH=dom-property event='{normalizedEvent}' handler={DescribeJsValue(propertyHandler)}");
            var thisValue = wrapperForProperty != null
                ? JsValue.FromObject(wrapperForProperty)
                : JsValue.Undefined;
            last = CallHandler(propertyHandler, thisValue, eventObj, srcElementValue);
            handled = true;
        }
        else if (wrapperForProperty != null &&
            wrapperForProperty.Properties.TryGetValue(normalizedEvent, out var wrapperHandler) &&
            wrapperHandler != null &&
            wrapperHandler.Type == JsType.Function)
        {
            Retro96.DebugLog.JsWrite($"FIRE_EVENT PATH=wrapper-property event='{normalizedEvent}' handler={DescribeJsValue(wrapperHandler)}");
            last = CallHandler(wrapperHandler, JsValue.FromObject(wrapperForProperty), eventObj, srcElementValue);
            handled = true;
        }
        else if (target.EventHandlers.TryGetValue(normalizedEvent, out var handlerSource))
        {
            Retro96.DebugLog.JsWrite($"FIRE_EVENT PATH=attribute event='{normalizedEvent}' source='{handlerSource}'");
            if (handlerSource != "__js_handler__")
            {
                last = ExecuteInlineHandler(target, wrapperForProperty, normalizedEvent, eventObj, srcElementValue);
                handled = true;
            }
            else
            {
                Retro96.DebugLog.JsWrite($"FIRE_EVENT PATH=none event='{normalizedEvent}' sentinel-without-function");
            }
        }

        // attachEvent registrations, in registration order, AFTER the DOM-0
        // handler (IE5, §11 — "fire the DOM-0 handler, then attached
        // attachEvent handlers in registration order").
        var attached = GetAttachedHandlers(target, normalizedEvent);
        if (attached.Count > 0)
        {
            foreach (var attachedHandler in attached)
            {
                if (attachedHandler.Type != JsType.Function) continue;
                var thisValue = wrapperForProperty != null
                    ? JsValue.FromObject(wrapperForProperty)
                    : JsValue.Undefined;
                last = CallHandler(attachedHandler, thisValue, eventObj, srcElementValue);
                handled = true;
                if (eventObj != null && IsCancelBubbleSet(eventObj)) break;
            }
        }

        if (!handled)
            Retro96.DebugLog.JsWrite($"FIRE_EVENT PATH=none event='{normalizedEvent}' eventHandlerMap=MISS");
        return (last, handled);
    }

    /// <summary>Runs the parsed inline attribute handler body in a global-scope
    /// child with the element wrapper as 'this' and 'event' in scope.</summary>
    private JsValue ExecuteInlineHandler(DomElement target, JsObject? wrapperForProperty,
        string normalizedEvent, JsObject? eventObj, JsValue? srcElementValue)
    {
        if (!target.EventHandlers.TryGetValue(normalizedEvent, out var handlerSource))
            return JsValue.Undefined;

        try
        {
            // Inline handler attributes are compiled as top-level programs
            // (era behaviour) — `onclick="…; return false"` must parse.
            var program = JsParser.Parse(handlerSource, allowTopLevelReturn: true);
            // Inline DOM event attributes are compiled in the page's global
            // event scope, not whichever function/timer scope happened to be
            // current when the event arrived.  This matters in frames: a
            // frame has its own interpreter, and handlers such as
            // onmouseover="hoverOn()" must resolve hoverOn() from that frame
            // document's global script scope rather than a stale transient
            // callback scope.
            var scope = _globalScope.NewChild();
            var thisValue = wrapperForProperty != null
                ? JsValue.FromObject(wrapperForProperty)
                : JsValue.Undefined;
            scope.Define("this", thisValue);

            JsValue priorWindowEvent = JsValue.Undefined;
            bool installedLegacyEvent = BrowserRuntime.SupportsInternetExplorerLegacy && eventObj != null;
            var windowObject = WindowObject;
            if (installedLegacyEvent && windowObject != null)
                priorWindowEvent = windowObject.Get("event");

            if (eventObj != null)
            {
                if (installedLegacyEvent)
                {
                    // Legacy IE handlers read the active event from the browser
                    // global; srcElement references the ORIGINAL target and the
                    // prior event is restored after dispatch so nested handlers
                    // cannot clobber it.
                    eventObj.Set("srcElement",
                        srcElementValue is { Type: JsType.Object } src ? src : thisValue);
                    // returnValue already seeded for the whole dispatch; do
                    // NOT reset it here — a bubbling ancestor must still see
                    // the false an earlier handler wrote.
                    windowObject?.Set("event", JsValue.FromObject(eventObj));
                }
                scope.Define("event", JsValue.FromObject(eventObj));
            }

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
                if (installedLegacyEvent && windowObject != null)
                {
                    if (priorWindowEvent.Type == JsType.Undefined)
                        windowObject.Delete("event");
                    else
                        windowObject.Set("event", priorWindowEvent);
                }
                CompleteExecution(isOutermost);
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
            string message = $"Error in {normalizedEvent} handler: {ex.Message}";
            if (ReportWindowOnError(message)) return JsValue.Undefined;
            _setStatus(message);
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
    }

    /// <summary>Stamps the Netscape 4 event fields onto the event object:
    /// target, pageX/pageY (clientX + pageXOffset — scroll offsets are not
    /// reachable from the bindings, so the offset is 0), which (key events:
    /// the character code; mouse events: 1-based button), modifiers (0 —
    /// modifier state is not tracked).</summary>
    private void ApplyNetscapeEventFields(JsObject eventObj, DomElement target)
    {
        if (!eventObj.HasOwn("target") && ElementWrapperHook != null)
            eventObj.Set("target", JsValue.FromObject(ElementWrapperHook(target)));

        if (!eventObj.HasOwn("pageX"))
        {
            double cx = eventObj.Get("clientX") is { Type: JsType.Number } n ? n.GetNumber() : 0;
            double cy = eventObj.Get("clientY") is { Type: JsType.Number } m ? m.GetNumber() : 0;
            eventObj.Set("pageX", JsValue.From(cx));
            eventObj.Set("pageY", JsValue.From(cy));
        }

        if (!eventObj.HasOwn("which"))
        {
            string type = eventObj.Get("type").ToJsString();
            bool keyEvent = type.StartsWith("key", StringComparison.OrdinalIgnoreCase);
            if (keyEvent)
            {
                double keyCode = eventObj.Get("keyCode") is { Type: JsType.Number } k ? k.GetNumber() : 0;
                eventObj.Set("which", JsValue.From(keyCode));
            }
            else if (eventObj.Get("button") is { Type: JsType.Number } b)
            {
                eventObj.Set("which", JsValue.From((int)b.GetNumber() + 1));
            }
        }

        if (!eventObj.HasOwn("modifiers"))
            eventObj.Set("modifiers", JsValue.From(0));
    }

    /// <summary>NS4 capture model: when window.captureEvents() armed the mask
    /// for this event type, the window's own handler sees the event before
    /// the target — a script-assigned window.handleEvent, else the window's
    /// on&lt;event&gt; property. (The DomBindings handleEvent BUILTIN is not
    /// a capture handler — redispatching through it would loop.)</summary>
    private void RunCapturedWindowHandler(DomElement element, string normalizedEvent, JsObject eventObj)
    {
        int mask = EventMaskFor(normalizedEvent);
        if (mask == 0 || (_capturedEventMask & mask) == 0) return;
        var w = WindowObject;
        if (w == null) return;

        var wrapper = ElementWrapperHook?.Invoke(element);
        JsValue? srcValue = wrapper != null ? JsValue.FromObject(wrapper) : null;

        if (w.Get("handleEvent") is { Type: JsType.Function } handleEvent &&
            !handleEvent.GetFunction().HasOwn("__dom_builtin__"))
        {
            CallHandler(handleEvent, JsValue.FromObject(w), eventObj, srcValue);
            return;
        }
        if (w.Get(normalizedEvent) is { Type: JsType.Function } windowHandler)
            CallHandler(windowHandler, JsValue.FromObject(w), eventObj, srcValue);
    }

    private static bool IsMouseEventName(string eventName) => eventName is
        "onclick" or "ondblclick" or "onmousedown" or "onmouseup" or
        "onmousemove" or "onmouseover" or "onmouseout" or "onmouseenter" or
        "onmouseleave";

    /// <summary>Resolve Object.prototype from this interpreter's own global realm.
    /// The engine historically kept the built-in prototypes in static fields,
    /// which means creating an event from a frame could accidentally chain the
    /// event object to the parent page's most recently installed Object.prototype.
    /// Use the realm's Object constructor first and keep the static field only as
    /// a compatibility fallback for hosts that do not expose Object yet.</summary>
    private JsObject? GetRealmObjectPrototype()
    {
        var objectCtor = _globalScope.Get("Object");
        if (objectCtor.Type is JsType.Object or JsType.Function)
        {
            var proto = objectCtor.GetObjectOrFunction().Get("prototype");
            if (proto.Type == JsType.Object)
                return proto.GetObject();
        }
        return ObjectPrototype;
    }

    /// <summary>Build the legacy IE mouse event object used by window.event.</summary>
    public JsObject CreateMouseEvent(string eventName, int clientX, int clientY, int button)
    {
        var evt = new JsObject { Prototype = GetRealmObjectPrototype() };
        evt.Set("type", JsValue.From(eventName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            ? eventName[2..] : eventName));
        evt.Set("clientX", JsValue.From(clientX));
        evt.Set("clientY", JsValue.From(clientY));
        evt.Set("screenX", JsValue.From(clientX));
        evt.Set("screenY", JsValue.From(clientY));
        evt.Set("x", JsValue.From(clientX));
        evt.Set("y", JsValue.From(clientY));
        evt.Set("button", JsValue.From(button));
        evt.Set("altKey", JsValue.From(false));
        evt.Set("ctrlKey", JsValue.From(false));
        evt.Set("shiftKey", JsValue.From(false));
        evt.Set("metaKey", JsValue.From(false));
        evt.Set("returnValue", JsValue.From(true));
        evt.Set("cancelBubble", JsValue.From(false));
        return evt;
    }

    /// <summary>Build a minimal keyboard event object (key / keyCode / which).</summary>
    public JsObject CreateKeyEvent(string key, int keyCode)
    {
        var evt = new JsObject { Prototype = GetRealmObjectPrototype() };
        evt.Set("key", JsValue.From(key));
        evt.Set("keyCode", JsValue.From(keyCode));
        evt.Set("which", JsValue.From(keyCode));
        evt.Set("charCode", JsValue.From(keyCode));
        return evt;
    }

    /// <summary>
    /// Call a JS-assigned handler (element.onclick = function...) with the
    /// element as 'this'.  Used by the shell when dispatching DOM events.
    /// <paramref name="srcElementValue"/> overrides the event object's
    /// srcElement (the original dispatch target while bubbling).
    /// </summary>
    public JsValue CallHandler(JsValue handler, JsValue thisValue, JsObject? eventObj = null,
                               JsValue? srcElementValue = null)
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
        JsValue priorWindowEvent = JsValue.Undefined;
        bool installedLegacyEvent = BrowserRuntime.SupportsInternetExplorerLegacy && eventObj != null;
        var windowObject = WindowObject;
        if (installedLegacyEvent && windowObject != null)
            priorWindowEvent = windowObject.Get("event");

        try
        {
            if (installedLegacyEvent)
            {
                eventObj!.Set("srcElement",
                    srcElementValue is { Type: JsType.Object } src ? src : thisValue);
                // returnValue/cancelBubble persist across the bubble chain —
                // only seed them when the event object has none yet.
                if (!eventObj.HasOwn("returnValue"))
                    eventObj.Set("returnValue", JsValue.From(true));
                if (!eventObj.HasOwn("cancelBubble"))
                    eventObj.Set("cancelBubble", JsValue.From(false));
                windowObject?.Set("event", JsValue.FromObject(eventObj));
            }
            var args = eventObj != null
                ? new[] { JsValue.FromObject(eventObj) }
                : Array.Empty<JsValue>();
            return CallFunction(handler.GetFunction(), thisValue, args);
        }
        catch (Exception ex)
        {
            string message = $"Error in event handler: {ex.Message}";
            if (ReportWindowOnError(message)) return JsValue.Undefined;
            _setStatus(message);
            PublishConsole("error", message);
            return JsValue.Undefined;
        }
        finally
        {
            if (installedLegacyEvent && windowObject != null)
            {
                if (priorWindowEvent.Type == JsType.Undefined)
                    windowObject.Delete("event");
                else
                    windowObject.Set("event", priorWindowEvent);
            }
            CompleteExecution(isOutermost);
        }
    }

    /// <summary>
    /// window.onerror (checklist §12): invoked from every script error path
    /// BEFORE the error is surfaced to the status bar / console. Era
    /// semantics: returning true suppresses the default error reporting.
    /// Returns true when the error was handled (and thus must be suppressed).
    /// </summary>
    private bool ReportWindowOnError(string message)
    {
        var w = WindowObject;
        if (w == null) return false;
        var handler = w.Get("onerror");
        if (handler.Type != JsType.Function) return false;
        try
        {
            var result = CallFunction(handler.GetFunction(), JsValue.FromObject(w),
                new[] { JsValue.From(message), JsValue.From(""), JsValue.From(0) });
            return result.Type == JsType.Boolean && result.GetBool();
        }
        catch
        {
            // A handler that itself throws must not loop the error reporter.
            return false;
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
            if (!_timers.Contains(timer)) continue; // cleared after due-list snapshot
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
                // An uncaught exception from an asynchronous timer is a page-script
                // error. Modern browsers report it to the developer console; they do
                // not replace the page's normal status text with the exception.
                // Keep the browser running and leave the rendered document untouched.
                string message = ex.Message.StartsWith("Error: ", StringComparison.Ordinal)
                    ? ex.Message[7..]
                    : ex.Message;
                PublishConsole("error", $"Uncaught timer exception: {message}");
            }
            finally
            {
                CompleteExecution(isOutermost: true);
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
            LabeledStatement lab => ExecuteLabeled(lab, labels),
            WithStatement with => ExecuteWith(with),
            FunctionDeclaration fnDecl => ExecuteFunctionDeclaration(fnDecl),
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

    private JsValue ExecuteLabeled(LabeledStatement lab, IReadOnlyList<string>? outerLabels)
    {
        var labels = ConcatLabel(outerLabels, lab.Label);
        try
        {
            return ExecuteStatement(lab.Body, labels);
        }
        catch (JsBreakException bex)
        {
            // §12.12: break with this label exits the labeled statement;
            // a different label's break belongs to an enclosing statement
            if (bex.Label == lab.Label) return JsValue.Undefined;
            throw;
        }
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
            // §12.2: the DECLARATION binds in the variable environment...
            // §10.2.2: bindings from EVAL code carry no DontDelete
            if (_evalDepth > 0)
                _currentScope.DefineInVariableEnvEval(d.Id.Name, JsValue.Undefined);
            else
                _currentScope.DeclareInVariableEnv(d.Id.Name);
            if (d.Init != null)
            {
                // ...but the initialiser is an ASSIGNMENT — §12.10: a
                // with-object exposing the name captures it
                // (with(x){var f=2} writes x.f when x has an f — 185485)
                JsValue value = ExecuteExpression(d.Init);
                _currentScope.Set(d.Id.Name, value);
                SyncArgumentsFromParam(_currentScope, d.Id.Name, value);
                DebugVariableWrite(d.Id.Name, value);
            }
        }
        return JsValue.Undefined;
    }

    /// <summary>
    /// §12.10 + the era-documented behaviour (bug 184107): the hoist
    /// pre-binds the name, but the statement itself RE-CREATES the function
    /// with the CURRENT scope as closure — a function declared inside
    /// with(obj) closes over obj (f's `y` keeps resolving there even
    /// after the with block ends).
    /// </summary>
    private JsValue ExecuteFunctionDeclaration(FunctionDeclaration fnDecl)
    {
        var paramNames = new string[fnDecl.Params.Count];
        for (int i = 0; i < paramNames.Length; i++)
            paramNames[i] = fnDecl.Params[i].Name;
        var func = new JsFunction(
            new FunctionExpr(fnDecl.Id, fnDecl.Params, fnDecl.Body, fnDecl.SourceText),
            paramNames, _currentScope);
        _currentScope.DefineInVariableEnv(fnDecl.Id.Name, JsValue.FromFunction(func));
        return JsValue.Undefined;
    }

    private JsValue ExecuteIf(IfStatement ifStmt)
    {
        var testValue = ExecuteExpression(ifStmt.Test);
        if (Retro96.DebugLog.JsEnabled)
        {
            var testText = testValue.ToJsString();
            if (testText.Contains("input", StringComparison.OrdinalIgnoreCase) ||
                testText.Contains("undefined", StringComparison.OrdinalIgnoreCase) ||
                testText.Contains("null", StringComparison.OrdinalIgnoreCase))
                Retro96.DebugLog.JsWrite($"IF_TEST value={DescribeJsValue(testValue)} truthy={testValue.ToBoolean()}");
        }
        if (testValue.ToBoolean())
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

        // §12.6.4 step 4: ToObject — for-in over a primitive enumerates the
        // wrapper's inherited properties (Number.prototype.foo shows up for
        // `for (j in 7)`)
        if (objVal.Type is not (JsType.Object or JsType.Function))
            objVal = JsValue.FromObject(BoxPrimitive(objVal));

        var obj = objVal.GetObjectOrFunction();

        // Arrays enumerate their indices in ascending order, then any other
        // enumerable own properties ("length" is never enumerable);
        // non-index properties (e.g. "4294967294.5") are NOT dropped —
        // for-in covers every enumerable property (§12.6.4)
        IEnumerable<string> keys;
        if (obj.Class == "Array")
        {
            var indices = new List<string>();
            var others = new List<string>();
            foreach (var k in obj.OwnEnumerableKeys())
            {
                if (k == "length") continue;
                if (long.TryParse(k, out var idx) && idx >= 0)
                    indices.Add(k);
                else
                    others.Add(k);
            }
            indices.Sort((x, y) => long.Parse(x).CompareTo(long.Parse(y)));
            indices.AddRange(others);
            keys = indices;
        }
        else
        {
            // JS 1.1 for-in enumerated INHERITED enumerable properties too —
            // walk the prototype chain, outermost first, deduplicating names.
            // Object.prototype is skipped: its members were never enumerable
            // in a real engine, and this implementation stores them as plain
            // own properties of the prototype object.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var names = new List<string>();
            for (var current = obj; current != null; current = current.Prototype)
            {
                if (ReferenceEquals(current, ObjectPrototype)) continue;
                foreach (var k in current.OwnEnumerableKeys())
                    if (seen.Add(k))
                        names.Add(k);
            }
            keys = names;
        }

        var completion = JsValue.Undefined;
        foreach (var key in keys)
        {
            CheckTimeout();

            // §12.6.4: a property deleted before its turn is not visited
            if (!obj.Has(key)) continue;

            if (forInStmt.Left is VarDeclaration varDecl)
            {
                _currentScope.DeclareInVariableEnv(varDecl.Declarations[0].Id.Name);
                _currentScope.Set(varDecl.Declarations[0].Id.Name, JsValue.From(key));
            }
            else if (forInStmt.Left is ExpressionStatement es && es.Expression is Identifier ident)
                _currentScope.Set(ident.Name, JsValue.From(key));
            else if (forInStmt.Left is Identifier ident2)
                _currentScope.Set(ident2.Name, JsValue.From(key));
            // §12.6.4 step 6: any LeftHandSideExpression target — the
            // member expression re-evaluates EVERY iteration (its object
            // and computed parts are live: `for (a[i++] in o)` indexes on)
            else if (forInStmt.Left is MemberExpr member)
            {
                JsValue target = ExecuteExpression(member.Object);
                string propName = GetMemberPropertyName(member);
                SetProperty(target, propName, JsValue.From(key));
            }
            else if (forInStmt.Left is ExpressionStatement mes && mes.Expression is MemberExpr member2)
            {
                JsValue target = ExecuteExpression(member2.Object);
                string propName = GetMemberPropertyName(member2);
                SetProperty(target, propName, JsValue.From(key));
            }

            try
            {
                // era completion semantics: the loop's value is the last
                // body statement's value (eval("for (p in o) delete p") → true)
                completion = ExecuteStatement(forInStmt.Body);
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
        return completion;
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
        // JS semantics: try/finally WITHOUT a catch still propagates the
        // exception after the finalizer runs. The old code swallowed the
        // exception whenever a finalizer existed — `try { throw x } finally
        // {}` silently continued as if nothing had been thrown.
        Exception? pending = null;
        try
        {
            ExecuteStatement(tr.Block);
        }
        catch (JsThrownException thrown)
        {
            if (tr.Handler != null)
                RunCatchHandler(tr.Handler, thrown.Value);
            else
                pending = thrown;
        }
        catch (JsInterpreterException ex)
        {
            // Runtime errors are catchable, period-style
            if (tr.Handler != null)
            {
                string errorName = ex switch
                {
                    JsTypeErrorException => "TypeError",
                    JsReferenceErrorException => "ReferenceError",
                    JsRangeErrorException => "RangeError",
                    JsUriErrorException => "URIError",
                    JsSyntaxErrorException => "SyntaxError",
                    _ => "Error"
                };
                JsValue constructor = _currentScope.Get(errorName);
                JsObject? prototype = constructor.Type == JsType.Function &&
                    constructor.GetObjectOrFunction().Get("prototype") is { Type: JsType.Object } prototypeValue
                        ? prototypeValue.GetObject()
                        : null;
                var err = new JsObject { Class = "Error", Prototype = prototype };
                err.Set("name", JsValue.From(errorName));
                err.Set("message", JsValue.From(ex.Message));
                RunCatchHandler(tr.Handler, JsValue.FromObject(err));
            }
            else
                pending = ex;
        }
        finally
        {
            if (tr.Finalizer != null)
                ExecuteStatement(tr.Finalizer);
        }
        if (pending != null)
            throw pending;
        return JsValue.Undefined;
    }

    private void RunCatchHandler(CatchClause handler, JsValue bound)
    {
        var old = _currentScope;
        _currentScope = _currentScope.NewChild();
        _currentScope.IsVariableEnvironment = false;   // §12.14: lexical catch binding
        try
        {
            _currentScope.Define(handler.Param.Name, bound);
            ExecuteStatement(handler.Body);
        }
        finally
        {
            _currentScope = old;
        }
    }

    /// <summary>ToObject (§9.9) for with-statements and for-in over
    /// primitives: a fresh wrapper object with the right [[Class]] and
    /// prototype so inherited members resolve (§12.10 step 3, §12.6.4).
    /// String wrappers carry the read-only length (§15.5.5.1).</summary>
    private JsObject BoxPrimitive(JsValue v)
    {
        switch (v.Type)
        {
            case JsType.String:
            {
                var so = new JsObject { Class = "String", Prototype = JsInterpreter.StringPrototype };
                so.Set("value", v);
                so.Set("length", JsValue.From(v.GetString().Length));
                so.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                so.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                so.Attrs["length"] = JsObject.PropAttr.Builtin;
                return so;
            }
            case JsType.Number:
            {
                var no = new JsObject { Class = "Number", Prototype = JsInterpreter.NumberPrototype };
                no.Set("value", v);
                no.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                no.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                return no;
            }
            case JsType.Boolean:
            {
                var bo = new JsObject { Class = "Boolean", Prototype = JsInterpreter.BooleanPrototype };
                bo.Set("value", v);
                bo.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                bo.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                return bo;
            }
            default:
                throw new JsTypeErrorException("cannot box value for object environment");
        }
    }

    private JsValue ExecuteWith(WithStatement with)
    {
        JsValue objVal = ExecuteExpression(with.Object);
        if (objVal.Type is JsType.Null or JsType.Undefined)
            throw new JsTypeErrorException(
                $"'{DescribeJsValue(objVal)}' has no properties");

        // §12.10 step 3: ToObject of a primitive gives the with body a
        // wrapper (with(7) exposes Number.prototype members — valueOf()
        // inside the body returns the primitive number)
        if (objVal.Type is not (JsType.Object or JsType.Function))
            objVal = JsValue.FromObject(BoxPrimitive(objVal));

        var withObj = objVal.GetObjectOrFunction();
        // §12.10/§10.2.3: object environment — lookups walk the object's
        // property chain (DontEnum builtins like Date.prototype methods are
        // visible), writes land on the object, var binds in the outer scope.
        var withScope = new JsWithScope(_currentScope, withObj);

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

    private static bool IsDebugVariable(string name) =>
        name is "btn2" or "btn3" or "d" or "element";

    private void DebugVariableRead(string name, JsValue value)
    {
        if (Retro96.DebugLog.JsEnabled && IsDebugVariable(name))
            Retro96.DebugLog.JsWrite($"VAR_GET name='{name}' -> {DescribeJsValue(value)}");
    }

    private void DebugVariableWrite(string name, JsValue value)
    {
        if (Retro96.DebugLog.JsEnabled && IsDebugVariable(name))
            Retro96.DebugLog.JsWrite($"VAR_SET name='{name}' value={DescribeJsValue(value)}");
    }

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
            Identifier id => ReadIdentifierWithDebug(id.Name),
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
        if (a.Left is Identifier ident)
        {
            if (a.Operator != "=" && !_currentScope.Has(ident.Name))
                throw new JsReferenceErrorException($"{ident.Name} is not defined");   // §11.13.2
            // §11.13.1: the LHS Reference is resolved BEFORE the RHS runs —
            // a with-object property created by the RHS must not capture
            // the store (with(o) x = o.x = 2 sets the outer x)
            var sink = _currentScope.ResolveAssignmentSink(ident.Name);
            JsValue oldValue = a.Operator == "=" ? JsValue.Undefined : _currentScope.Get(ident.Name);
            JsValue rightVal = ExecuteExpression(a.Right);
            JsValue newValue = a.Operator == "="
                ? rightVal
                : ApplyCompound(oldValue, rightVal, a.Operator);

            // `location = "url"` — the era's #1 script navigation idiom
            // (openpowerstart/switch_page/setTimeout("location='...'"))
            // used to land in the scope DICTIONARY as a plain string:
            // the LocationObject.href setter never fired, so every button
            // built on it did nothing. Route string assignments to bare
            // `location` (and window/document/self/location in the member path
            // below) through the shell's navigate hook.
            if (ident.Name == "location" && newValue.Type == JsType.String)
            {
                _onNavigate(newValue.GetString());
                return newValue;
            }

            sink(newValue);
            SyncArgumentsFromParam(_currentScope, ident.Name, newValue);
            DebugVariableWrite(ident.Name, newValue);
            return newValue;
        }

        if (a.Left is MemberExpr member)
        {
            JsValue objVal = ExecuteExpression(member.Object);
            string name = GetMemberPropertyName(member);
            JsValue oldValue = a.Operator == "="
                ? JsValue.Undefined
                : GetProperty(objVal, name);
            JsValue rightVal = ExecuteExpression(a.Right);
            if (Retro96.DebugLog.JsEnabled)
                Retro96.DebugLog.JsWrite($"ASSIGN member='{name}' operator='{a.Operator}' target={DescribeJsValue(objVal)}");

            JsValue newValue = a.Operator == "="
                ? rightVal
                : ApplyCompound(oldValue, rightVal, a.Operator);

            // window.location = "url" / document.location = "url" — same
            // story as the bare identifier: a plain Set replaced the slot
            // with a string and nothing navigated.
            if (name == "location" && newValue.Type == JsType.String &&
                member.Object is Identifier objId &&
                objId.Name is "window" or "document" or "self" or "top" or "parent")
            {
                _onNavigate(newValue.GetString());
                return newValue;
            }

            SetProperty(objVal, name, newValue);
            return newValue;
        }

        throw new JsInterpreterException("Invalid left side in assignment");
    }

    private JsValue ApplyCompound(JsValue left, JsValue right, string op) => op switch
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
            "<"  => JsValue.From(RelationalCmp(left, right, swap: false) == Cmp3.True),
            ">"  => JsValue.From(RelationalCmp(left, right, swap: true) == Cmp3.True),
            "<=" => JsValue.From(RelationalCmp(left, right, swap: true) == Cmp3.False),
            ">=" => JsValue.From(RelationalCmp(left, right, swap: false) == Cmp3.False),
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
            SyncArgumentsFromParam(_currentScope, ident.Name, newVal);
            // §11.3.1/§11.3.2: the postfix result is the ToNumber'ed old value
            return up.Prefix ? newVal : JsValue.From(oldVal.ToNumber());
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
            if (Retro96.DebugLog.JsEnabled &&
                (name.Equals("setAttribute", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("getAttribute", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                 Retro96.DebugLog.JsTraceEnabled))
                Retro96.DebugLog.JsWrite($"CALL member='{name}' target={DescribeJsValue(objVal)} args={call.Arguments.Count}");
            callee = GetProperty(objVal, name);
        }
        else
        {
            thisValue = GlobalThis();
            // §10.2.3: a bare name found on a with-object is called with
            // that object as `this` (with(d){ getUTCMonth() } — receiver d)
            if (call.Callee is Identifier idc && _currentScope.ThisFor(idc.Name) is { } owner)
                thisValue = JsValue.FromObject(owner);
            callee = ExecuteExpression(call.Callee);
        }

        if (callee.Type != JsType.Function)
        {
            string what = call.Callee switch
            {
                Identifier or MemberExpr => RenderCalleeName(call.Callee),
                _ => "expression"
            };
            throw new JsTypeErrorException($"{what} is not a function");   // §11.2.3
        }

        var args = new JsValue[call.Arguments.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = ExecuteExpression(call.Arguments[i]);

        var callable = callee.GetFunction();

        // §15.1.2.1: eval invoked through any reference other than a bare
        // `eval(` is INDIRECT — the program evaluates in the GLOBAL scope
        // (this.eval(s) must define globals, not function locals)
        if (ReferenceEquals(callable, _globalEvalFunction) &&
            call.Callee is not Identifier { Name: "eval" } &&
            args.Length > 0 && args[0].Type == JsType.String)
        {
            _callDepth++;
            try { return EvalString(args[0].GetString(), _globalScope); }
            finally { _callDepth--; }
        }

        if (callable.UseFunctionObjectAsThis)
            thisValue = callee;

        return CallFunction(callable, thisValue, args);
    }

    private JsValue ExecuteNew(NewExpr newExpr)
    {
        JsValue callee = ExecuteExpression(newExpr.Callee);
        if (callee.Type != JsType.Function)
        {
            string what = newExpr.Callee switch
            {
                Identifier or MemberExpr => RenderCalleeName(newExpr.Callee),
                _ => "expression"
            };
            throw new JsTypeErrorException($"{what} is not a constructor");   // §11.2.2
        }

        var args = new JsValue[newExpr.Arguments.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = ExecuteExpression(newExpr.Arguments[i]);

        var constructor = callee.GetFunction();
        var newObj = new JsObject();
        // Constructors without a prototype property still chain to
        // Object.prototype — {}.toString() used to be undefined. Resolve the
        // realm's prototype rather than the shared static field so frames
        // don't chain to a foreign page's Object.prototype.
        newObj.Prototype = constructor.Get("prototype") is { Type: JsType.Object or JsType.Function } proto
            ? proto.GetObjectOrFunction()
            : GetRealmObjectPrototype();

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

    private JsValue ReadIdentifierWithDebug(string name)
    {
        if (!_currentScope.Has(name))
            throw new JsReferenceErrorException($"{name} is not defined");   // era message text
        var value = _currentScope.Get(name);
        DebugVariableRead(name, value);
        return value;
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
        // §15.4.5.1: array length is { DontEnum, DontDelete } (writable)
        arr.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
        arr.Attrs["length"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
        int index = 0;
        foreach (var elem in arrayExpr.Elements)
        {
            // §11.1.4: elisions leave NO property behind — a hole is not an
            // own enumerable property (for-in must skip it) but still counts
            // toward the length
            if (elem != null)
                arr.Set(index.ToString(), ExecuteExpression(elem));
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
        JsRuntime.ApplyRegExpShape(regexObj, rx.Pattern, rx.Flags);
        // RegExp prototype is installed by JsRuntime
        regexObj.Prototype = _globalScope.Get("RegExp") is { Type: JsType.Function } re
            && re.GetFunction().Get("prototype") is { Type: JsType.Object or JsType.Function } proto
            ? proto.GetObjectOrFunction()
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
        if (d.Argument is Identifier ident)
        {
            // §11.4.1: a declared var/param carries DontDelete and refuses;
            // an implicit global (created by bare assignment) deletes.
            return JsValue.From(_currentScope.Delete(ident.Name));
        }

        if (d.Argument is MemberExpr member)
        {
            JsValue objVal = ExecuteExpression(member.Object);
            if (objVal.Type is not (JsType.Object or JsType.Function))
            {
                // §11.4.1 step 3b: delete on a primitive's member targets the
                // ToObject wrapper — a missing property deletes as true
                return JsValue.From(objVal.Type is not (JsType.Null or JsType.Undefined)
                    && true);
            }
            var target = objVal.GetObjectOrFunction();
            string name = GetMemberPropertyName(member);
            // §11.4.1 step 3b: deleting a property the object does not have
            // returns true, not false
            if (!target.Has(name)) return JsValue.From(true);
            // §10.1.8: deleting arguments[i] severs the parameter mapping
            if (target.Class == "Arguments" &&
                _argumentsBindingsByObj.TryGetValue(target, out var ab) &&
                ab.IndexToParam.TryGetValue(name, out var paramName))
            {
                ab.IndexToParam.Remove(name);
                ab.ParamToIndex.Remove(paramName);
            }
            // Route through the VIRTUAL Delete so host objects can
            // intercept removal exactly like Get/Set.
            return JsValue.From(target.Delete(name));
        }

        // §11.4.1 step 1: the operand is still EVALUATED (delete ++o.a
        // increments before yielding true), then true — not a Reference.
        ExecuteExpression(d.Argument);
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
        // §11.8.6 step 5: a right operand without [[HasInstance]] throws
        if (right.Type != JsType.Function)
            throw new JsTypeErrorException(
                $"invalid 'instanceof' operand {DescribeJsValue(right)}");
        if (left.Type is not (JsType.Object or JsType.Function))
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
        if (Retro96.DebugLog.JsEnabled &&
            (Retro96.DebugLog.JsTraceEnabled ||
             (func.Name ?? "").StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
             (func.Name ?? "").Contains("Attribute", StringComparison.OrdinalIgnoreCase)))
            Retro96.DebugLog.JsWrite($"CALL_FUNCTION name='{func.Name ?? "<anonymous>"}' native={(func.Native != null ? "yes" : "no")} this={DescribeJsValue(thisValue)} argc={args.Length}");

        if (func.Native != null)
        {
            try
            {
                return func.Native(thisValue, args);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new JsTimeoutException();
            }
        }

        if (++_callDepth > _maxCallDepth)
        {
            _callDepth--;
            // era engines surfaced runaway recursion as a catchable
            // InternalError: too much recursion (regress-234389)
            var err = new JsObject { Class = "Error", Prototype = GetRealmErrorPrototype("InternalError") };
            err.Set("name", JsValue.From("InternalError"));
            err.Set("message", JsValue.From("too much recursion"));
            throw new JsThrownException(JsValue.FromObject(err));
        }

        try
        {
            var funcScope = func.ClosureScope.NewChild();
            // §10.1.8: the arguments object has [[Class]] "Arguments" and
            // the standard Object.prototype (with(this) coercion used to
            // hit a prototype-less object and die on ToString)
            var argsObj = new JsObject { Class = "Arguments", Prototype = GetRealmObjectPrototype() };
            for (int i = 0; i < args.Length; i++)
                argsObj.Set(i.ToString(), args[i]);
            argsObj.Set("length", JsValue.From(args.Length));
            argsObj.Set("callee", JsValue.FromFunction(func));
            argsObj.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
            argsObj.Attrs["length"] = JsObject.PropAttr.DontEnum;
            argsObj.Attrs["callee"] = JsObject.PropAttr.DontEnum;
            // §10.1.8 mapped arguments: arguments[i] SHARES the value of
            // parameter Params[i] (the LAST occurrence when names repeat —
            // f7(x,x,x,x){ x = 999 } makes arguments[3] === 999)
            var binding = new ArgumentsBinding { Scope = funcScope, ArgsObj = argsObj };
            bool anyMapping = false;
            for (int i = 0; i < func.Params.Count && i < args.Length; i++)
            {
                // last occurrence of a repeated name owns the mapping
                int last = i;
                for (int j = func.Params.Count - 1; j > i; j--)
                    if (func.Params[j] == func.Params[i]) { last = j; break; }
                if (last != i) continue;
                binding.IndexToParam[i.ToString()] = func.Params[i];
                binding.ParamToIndex[func.Params[i]] = i.ToString();
                anyMapping = true;
            }
            if (anyMapping)
            {
                funcScope.HasArgumentsBinding = true;
                _argumentsBindingsByObj.Add(argsObj, binding);
                _argumentsBindingsByScope.Add(funcScope, binding);
            }
            funcScope.Define("arguments", JsValue.FromObject(argsObj));
            for (int i = 0; i < func.Params.Count; i++)
                funcScope.Define(func.Params[i],
                    i < args.Length ? args[i] : JsValue.Undefined);
            // §10.2.3: a bare call (or an explicit null/undefined thisArg via
            // call/apply) receives the global object as `this`
            funcScope.Define("this", thisValue.Type is (JsType.Undefined or JsType.Null)
                ? GlobalThis() : thisValue);

            var body = func.Body ?? throw new JsInterpreterException("Function body is missing");
            ApplyHoistPlan(GetHoistPlan(body), funcScope);

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

    /// <summary>Source-like name path of a callee (Error.prototype, f) —
    /// the era message text names the whole reference, not the last link.</summary>
    private static string RenderCalleeName(Expr e) => e switch
    {
        Identifier id => id.Name,
        MemberExpr m when !m.Computed => RenderCalleeName(m.Object) + "." + ((Identifier)m.Property).Name,
        MemberExpr => "expression",
        _ => "expression"
    };

    private string GetMemberPropertyName(MemberExpr member)
    {
        if (member.Computed)
            return ExecuteExpression(member.Property).ToJsString();
        return ((Identifier)member.Property).Name;
    }

    private static JsValue GetProperty(JsValue target, string name)
    {
        if (Retro96.DebugLog.JsEnabled &&
            (name.Equals("setAttribute", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("getAttribute", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
             Retro96.DebugLog.JsTraceEnabled))
            Retro96.DebugLog.JsWrite($"PROP_GET name='{name}' target={DescribeJsValue(target)}");

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
                // ES3 §8.7.1 / era behaviour: reading a property of null or
                // undefined is a catchable TypeError ("'x' is null or not
                // an object", in IE5's words).
                if (target.Type is JsType.Null or JsType.Undefined)
                    throw new JsTypeErrorException(
                        $"'{name}' is null or not an object");
                return JsValue.Undefined;
        }
    }

    private static void SetProperty(JsValue target, string name, JsValue value)
    {
        if (Retro96.DebugLog.JsEnabled &&
            (name.Equals("setAttribute", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("getAttribute", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("value", StringComparison.OrdinalIgnoreCase) ||
             Retro96.DebugLog.JsTraceEnabled))
            Retro96.DebugLog.JsWrite($"PROP_SET name='{name}' target={DescribeJsValue(target)} value={DescribeJsValue(value)}");

        // §10.1.8: writing arguments[i] writes the mapped parameter too
        if (target.Type is (JsType.Object or JsType.Function) &&
            target.GetObjectOrFunction().Class == "Arguments" &&
            _argumentsBindingsByObj.TryGetValue(target.GetObjectOrFunction(), out var ab) &&
            ab.IndexToParam.TryGetValue(name, out var paramName))
        {
            ab.Scope.Set(paramName, value);
        }

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
        if (w.Type == JsType.Object) return w;
        // the shell surface: the harness's `this` is the global object
        var g = _globalScope.Get("global");
        if (g.Type is (JsType.Object or JsType.Function)) return g;
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

    /// <summary>ECMA-262 §8.6.2.6 ToPrimitive with a hint — invokes the
    /// receiver's own valueOf/toString methods in the spec order, unlike the
    /// engine-level JsValue.ToPrimitive shortcut (which exists for paths
    /// outside the interpreter).</summary>
    private JsValue ToPrimitiveV(JsValue v, bool preferString)
    {
        if (v.Type is not (JsType.Object or JsType.Function)) return v;
        var obj = v.GetObjectOrFunction();
        foreach (var name in preferString
                 ? new[] { "toString", "valueOf" }
                 : new[] { "valueOf", "toString" })
        {
            var fn = obj.Get(name);
            if (fn.Type == JsType.Function)
            {
                var result = CallFunction(fn.GetFunction(), v, Array.Empty<JsValue>());
                if (result.Type is not (JsType.Object or JsType.Function))
                    return result;
            }
        }
        throw new JsTypeErrorException("Cannot convert object to primitive value");
    }

    private JsValue Add(JsValue left, JsValue right)
    {
        // §11.6.1: ToPrimitive with no hint — a Date prefers its string form
        // (so date + 0 is string concatenation, the era suite's pinned
        // behaviour), everything else goes valueOf-first.
        bool lDate = left.Type is (JsType.Object or JsType.Function) &&
                     left.GetObjectOrFunction().Class == "Date";
        bool rDate = right.Type is (JsType.Object or JsType.Function) &&
                     right.GetObjectOrFunction().Class == "Date";
        JsValue l = ToPrimitiveV(left, preferString: lDate);
        JsValue r = ToPrimitiveV(right, preferString: rDate);

        // String + anything → string; the era's document.write glue.
        // Strings past 2^30 throw RangeError — runaway doubling loops
        // ('s += s' x 100000) used to kill the host with a hard OOM
        if (l.Type == JsType.String || r.Type == JsType.String)
        {
            string ls = l.ToJsString(), rs = r.ToJsString();
            // 2^26 chars (128 MB of UTF-16) is far past any era script's
            // needs and stays under the host OOM point, so the throw is
            // always a catchable RangeError (regress-3649-n)
            if ((long)ls.Length + rs.Length > (1L << 26))
                throw new JsRangeErrorException("string length overflow");
            return JsValue.From(ls + rs);
        }
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
    /// §11.8.5 Abstract Relational Comparison with its three-valued result:
    /// True / False / Undefined (either operand NaN after ToPrimitive).
    /// §11.8.2/§11.8.4 swap the comparison ARGUMENTS, but the ToPrimitive
    /// conversions still run in SOURCE order (left operand first) — the
    /// order real engines implement and ES5 later made explicit.
    /// </summary>
    private enum Cmp3 { True, False, Undefined }

    private Cmp3 RelationalCmp(JsValue left, JsValue right, bool swap)
    {
        // §11.8.5: ToPrimitive with hint Number, left-then-right in source
        // order (valueOf first — a Date compares numerically here, unlike +)
        var pl = ToPrimitiveV(left, preferString: false);
        var pr = ToPrimitiveV(right, preferString: false);
        var px = swap ? pr : pl;
        var py = swap ? pl : pr;
        if (px.Type == JsType.String && py.Type == JsType.String)
            return string.CompareOrdinal(px.GetString(), py.GetString()) < 0
                ? Cmp3.True : Cmp3.False;
        double an = px.ToNumber();
        double bn = py.ToNumber();
        if (double.IsNaN(an) || double.IsNaN(bn)) return Cmp3.Undefined;
        return an < bn ? Cmp3.True : Cmp3.False;
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
    /// Navigator's format, errors print name: message, primitive wrappers
    /// print their wrapped value, functions print a source stub.
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
                    return JsRuntime.DateToStringForStringify(ms);
                }

            case "Error":
                {
                    string name = obj.Get("name") is { Type: JsType.String } n ? n.GetString() : "Error";
                    string msg = obj.Get("message") is { Type: JsType.String } m ? m.GetString() : "";
                    return msg.Length > 0 ? $"{name}: {msg}" : name;
                }

            case "String":
                // Primitive wrapper: "" + new String("hi") must be "hi".
                return obj.Get("value").ToJsString();

            case "Number":
                {
                    var v = obj.Get("value");
                    return v.Type == JsType.Number ? JsValue.NumberToString(v.GetNumber()) : "NaN";
                }

            case "Boolean":
                return obj.Get("value").ToBoolean() ? "true" : "false";

            default:
                if (obj is JsFunction fn)
                {
                    // §15.3.4.2: the ORIGINAL source text; builtins render
                    // in the era's [native code] form
                    if (fn.SourceText is { Length: > 0 } src) return src;
                    return fn.Name is { Length: > 0 }
                        ? $"function {fn.Name}() {{\n    [native code]\n}}"
                        : "function() {{\n    [native code]\n}}";
                }
                // §15.2.4.2: the default tag is the object's [[Class]]
                return obj.Class.Length > 0
                    ? $"[object {obj.Class}]"
                    : "[object Object]";
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

    // ── Hoist plan cache ──
    // Hoisting used to walk the function body's AST on EVERY call, which
    // dominated call-heavy scripts (the mozilla harness date helpers make
    // hundreds of nested calls per assertion). The walk's result depends
    // only on the AST, so the plan is computed once per FunctionExpr node
    // and replayed per call; closure creation still happens per call.
    private sealed class HoistPlan
    {
        public readonly List<object> Items = new();   // FunctionDeclaration or var-name string
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FunctionExpr, HoistPlan> HoistPlans = new();

    private static HoistPlan GetHoistPlan(FunctionExpr fn)
    {
        if (!HoistPlans.TryGetValue(fn, out var plan))
        {
            plan = new HoistPlan();
            CollectHoist(fn.Body.Body, plan);
            HoistPlans.Add(fn, plan);
        }
        return plan;
    }

    private static void CollectHoist(IReadOnlyList<Stmt> stmts, HoistPlan plan)
    {
        foreach (var stmt in stmts)
            CollectHoistOne(stmt, plan);
    }

    private static void CollectHoistOne(Stmt stmt, HoistPlan plan)
    {
        switch (stmt)
        {
            case FunctionDeclaration: plan.Items.Add(stmt); break;
            case VarDeclaration varDecl:
                foreach (var d in varDecl.Declarations) plan.Items.Add(d.Id.Name);
                break;
            case BlockStatement block:
                foreach (var s in block.Body) CollectHoistOne(s, plan); break;
            case IfStatement i:
                CollectHoistOne(i.Consequent, plan);
                if (i.Alternate != null) CollectHoistOne(i.Alternate, plan);
                break;
            case WhileStatement w: CollectHoistOne(w.Body, plan); break;
            case DoWhileStatement dw: CollectHoistOne(dw.Body, plan); break;
            case ForStatement f:
                if (f.Init is VarDeclaration vd) CollectHoistOne(vd, plan);
                CollectHoistOne(f.Body, plan);
                break;
            case ForInStatement fi: CollectHoistOne(fi.Body, plan); break;
            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                    foreach (var s in c.Consequent) CollectHoistOne(s, plan);
                break;
            case TryStatement t:
                CollectHoistOne(t.Block, plan);
                if (t.Handler != null) CollectHoistOne(t.Handler.Body, plan);
                if (t.Finalizer != null) CollectHoistOne(t.Finalizer, plan);
                break;
            case LabeledStatement ls: CollectHoistOne(ls.Body, plan); break;
            case WithStatement ws: CollectHoistOne(ws.Body, plan); break;
        }
    }

    private void ApplyHoistPlan(HoistPlan plan, JsScope targetScope)
    {
        foreach (var item in plan.Items)
        {
            if (item is FunctionDeclaration fn)
            {
                var paramNames = new string[fn.Params.Count];
                for (int i = 0; i < paramNames.Length; i++)
                    paramNames[i] = fn.Params[i].Name;
                var func = new JsFunction(
                    new FunctionExpr(fn.Id, fn.Params, fn.Body, fn.SourceText), paramNames, targetScope);
                targetScope.Define(fn.Id.Name, JsValue.FromFunction(func));
            }
            else
            {
                var name = (string)item;
                // §12.2/§10.1.3: var hoisting pre-binds the name in THIS
                // variable environment — HasOwn, not chain Has: a local
                // `var x` must shadow an outer x from function entry
                // ("var a = x; var x = 23" sees the hoisted local undefined)
                if (!targetScope.HasOwn(name))
                    targetScope.Define(name, JsValue.Undefined);
            }
        }
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
                    new FunctionExpr(fn.Id, fn.Params, fn.Body, fn.SourceText), paramNames, targetScope);
                // §10.2.2: eval-code function declarations are deletable
                if (_evalDepth > 0) targetScope.DefineEvalHoisted(fn.Id.Name, JsValue.FromFunction(func));
                else targetScope.Define(fn.Id.Name, JsValue.FromFunction(func));
                break;

            case VarDeclaration varDecl:
                foreach (var d in varDecl.Declarations)
                {
                    if (_evalDepth > 0)
                        targetScope.DefineEvalHoisted(d.Id.Name, JsValue.Undefined);
                    else if (!targetScope.HasOwn(d.Id.Name))
                        targetScope.Define(d.Id.Name, JsValue.Undefined);
                }
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

    private int _timeoutCheckCountdown = 64;

    /// <summary>The global eval — needed to detect INDIRECT eval calls
    /// (this.eval(s), [eval][0](s)) which run in the global scope (§15.1.2.1).</summary>
    private JsFunction? _globalEvalFunction;

    private JsObject? GetRealmErrorPrototype(string name)
    {
        var ctor = _globalScope.Get(name);
        return ctor.Type == JsType.Function &&
            ctor.GetObjectOrFunction().Get("prototype") is { Type: JsType.Object } pv
                ? pv.GetObject() : null;
    }

    private void CheckTimeout()
    {
        // Sampled: the stopwatch read dominated hot expression loops, and
        // sub-millisecond timeout precision serves no purpose on a 5-10s
        // budget. Check the clock every 64th node instead of every node.
        if (--_timeoutCheckCountdown > 0) return;
        _timeoutCheckCountdown = 64;
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

    private void PublishConsole(string level, string message)
    {
        ConsoleMessage?.Invoke(new ConsoleEntry(level, message, DateTime.Now));
    }

    public void RegisterRuntimeBuiltins()
    {
        InstallPrimitiveConversion();

        // Developer consoles are not part of the strict IE3/JScript 1.0 surface.
        // Retro96 and Navigator retain the broader runtime surface.
        if (!BrowserRuntime.IsInternetExplorer3)
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
        }

        if (BrowserRuntime.JavaScriptEvalEnabled)
        {
            var evalValue = Native((self, args) =>
            {
                if (args.Length == 0) return JsValue.Undefined;
                if (args[0].Type != JsType.String) return args[0];
                return EvalString(args[0].GetString(), _currentScope);
            }, "eval", global: true, length: 1);
            _globalEvalFunction = evalValue.GetFunction();
        }

        // Function.prototype.call / apply are hidden only in strict IE3 mode;
        // Retro96 retains the broader compatibility runtime.
        if (!BrowserRuntime.IsInternetExplorer3 &&
            _globalScope.Get("Function") is { Type: JsType.Function } funcCtor &&
            funcCtor.GetFunction().Get("prototype") is { Type: JsType.Object or JsType.Function } fp)
        {
            var funcProto = fp.GetObjectOrFunction();

            funcProto.Set("call", Native((self, args) =>
            {
                if (self.Type != JsType.Function)
                    throw new JsInterpreterException("Function.prototype.call on non-function");
                var thisArg = args.Length > 0 ? args[0] : GlobalThis();
                var rest = args.Length > 1 ? args[1..] : Array.Empty<JsValue>();
                return CallFunction(self.GetFunction(), thisArg, rest);
            }, "call", length: 1));

            funcProto.Set("apply", Native((self, args) =>
            {
                if (self.Type != JsType.Function)
                    throw new JsInterpreterException("Function.prototype.apply on non-function");
                var thisArg = args.Length > 0 ? args[0] : GlobalThis();
                var callArgs = new List<JsValue>();
                // §15.3.4.3: argArray null/undefined ⇒ no arguments; an
                // object/Array ⇒ spread; anything else ⇒ TypeError
                if (args.Length > 1)
                {
                    if (args[1].Type is JsType.Object or JsType.Function)
                    {
                        var arr = args[1].GetObjectOrFunction();
                        int len = arr.Get("length") is { Type: JsType.Number } l ? (int)l.GetNumber() : 0;
                        for (int i = 0; i < len; i++)
                            callArgs.Add(arr.Get(i.ToString()));
                    }
                    else if (args[1].Type is not (JsType.Null or JsType.Undefined))
                        throw new JsTypeErrorException("Function.prototype.apply: second argument must be an array or arguments object");
                }
                return CallFunction(self.GetFunction(), thisArg, callArgs.ToArray());
            }, "apply", length: 2));
        }

        // Timers on window + global
        Native((self, args) =>
        {
            if (args.Length == 0)
                throw new JsTypeErrorException("missing argument 0 when calling function setTimeout");
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

        // Array callback helpers are later ECMAScript additions.
        if (!BrowserRuntime.IsInternetExplorer3 && ArrayPrototype != null)
        {
            ArrayPrototype.Set("sort", Native((self, args) =>
                SortArray(self, args), "sort", length: 1));

            ArrayPrototype.Set("forEach", Native((self, args) =>
                IterateCallbackArray(self, args, "forEach"), "forEach"));

            ArrayPrototype.Set("map", Native((self, args) =>
                MapArray(self, args), "map"));

            ArrayPrototype.Set("filter", Native((self, args) =>
                FilterArray(self, args), "filter"));

            // §15.4.4.32/§15.4.5.1-era: Array.prototype.toLocaleString joins
            // the elements' OWN toLocaleString() results with a locale
            // separator; null/undefined contribute nothing
            ArrayPrototype.Set("toLocaleString", Native((self, args) =>
            {
                if (self.Type is not (JsType.Object or JsType.Function))
                    throw new JsTypeErrorException("Array.prototype.toLocaleString is not generic");
                var arr = self.GetObjectOrFunction();
                long len = arr.Get("length") is { Type: JsType.Number } l ? (long)l.GetNumber() : 0;
                var parts = new List<string>();
                for (int i = 0; i < len; i++)
                {
                    var v = arr.Get(i.ToString());
                    if (v.Type is JsType.Null or JsType.Undefined) continue;
                    var m = GetProperty(v, "toLocaleString");
                    if (m.Type != JsType.Function)
                        throw new JsTypeErrorException("array element toLocaleString is not a function");
                    parts.Add(CallFunction(m.GetFunction(), v, Array.Empty<JsValue>()).ToJsString());
                }
                return JsValue.From(string.Join(", ", parts));
            }, "toLocaleString", length: 0));
        }

        // These were added after the §15 freeze pass in JsRuntime, so give
        // them the builtin attribute defaults now (for-in over a function or
        // array must not see call/apply/sort).
        FunctionPrototype?.MarkAll(JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete);
        ArrayPrototype?.MarkAll(JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete);
    }

    private JsValue Native(Func<JsValue, JsValue[], JsValue> impl, string name,
                          bool global = false, int length = 0)
    {
        var value = JsValue.FromFunction(new JsFunction(impl, _globalScope, name, length: length));
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
        double lenD = arr.Get("length") is { Type: JsType.Number } l ? l.GetNumber() : 0;
        if (double.IsNaN(lenD) || lenD <= 1) return self;
        long len = lenD >= 4294967295.0 ? 4294967295L : (long)lenD;

        // §15.4.4.11: only elements that EXIST are sorted — holes stay
        // holes, undefined values go to the end, and a billion-length
        // holey array never materializes (Sort used to OOM on Array(1<<30))
        var slots = new List<long>();
        foreach (var k in arr.Properties.Keys)
        {
            if (k.Length == 0 || k[0] < '0' || k[0] > '9') continue;
            if (k.Length > 1 && k[0] == '0') continue;   // non-canonical index
            if (!long.TryParse(k, out long idx) || idx < 0 || idx >= len) continue;
            slots.Add(idx);
        }
        if (slots.Count <= 1) return self;
        slots.Sort();

        var defined = new List<JsValue>(slots.Count);
        int undefs = 0;
        foreach (var idx in slots)
        {
            var v = arr.Get(idx.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (v.Type == JsType.Undefined) undefs++;
            else defined.Add(v);
        }

        JsFunction? compareFn =
            args.Length > 0 && args[0].Type == JsType.Function ? args[0].GetFunction() : null;

        // Insertion sort — stable, and safe with the interpreter's callbacks
        for (int i = 1; i < defined.Count; i++)
        {
            var x = defined[i];
            int j = i - 1;
            while (j >= 0 && CompareSort(defined[j], x, compareFn) > 0)
            {
                defined[j + 1] = defined[j];
                j--;
            }
            defined[j + 1] = x;
        }

        // write back: sorted defined values first, then the undefined tail
        int di = 0;
        for (int s = 0; s < slots.Count; s++)
        {
            long slot = slots[s];
            var v = s < slots.Count - undefs ? defined[di++] : JsValue.Undefined;
            arr.Set(slot.ToString(System.Globalization.CultureInfo.InvariantCulture), v);
        }
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
        if (mapped != null)
        {
            // §15.4.5.1 length attributes for result arrays too
            mapped.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
            mapped.Attrs["length"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
        }

        for (int i = 0; i < len; i++)
        {
            var item = arr.Get(i.ToString());
            var r = CallFunction(fn, GlobalThis(), new[] { item, JsValue.From(i), self });
            if (mode == "map")
                mapped!.Set(i.ToString(), r);
            else if (mode == "filter" && r.ToBoolean())
                AppendToArray(mapped!, item);
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