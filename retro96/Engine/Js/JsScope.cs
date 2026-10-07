using System.Collections.Generic;

namespace Retro96.Engine.Js;

/// <summary>
/// Lexical scope chain for the interpreter.  JS 1.1 semantics: var is
/// function-scoped (blocks share their function's scope), assignment to an
/// undeclared name becomes a property of the global object, and reading an
/// undeclared identifier raises ReferenceError (except under typeof).
///
/// The ROOT scope can carry a GlobalFallback object (the window): in real
/// JS the global scope and the window object are the same thing, so bare
/// alert()/confirm()/status reads resolve through the fallback, and
/// implicit global writes (status = "…") land on it where both window.status
/// and bare reads find them. The interpreter distinguishes missing bindings
/// from declared bindings whose value is undefined when evaluating identifiers.
///
/// Storage: most scopes hold a handful of names (params, this, arguments),
/// so lookups use a linear array scan with reference-equality first (AST
/// identifier strings are reused per parse, so hot names hit the same
/// instance) and only spill to a dictionary past SmallCapacity entries.
/// This keeps per-call scope setup out of the allocator.
///
/// TryGetOwn/Put/Define/Delete are virtual so object environments (the
/// with statement, §12.10) can delegate to their object's property chain.
/// </summary>
public class JsScope
{
    private const int SmallCapacity = 6;

    public JsScope? Parent { get; }

    /// <summary>Set on the root scope only — the global object (window).</summary>
    public JsObject? GlobalFallback { get; set; }

    /// <summary>§10.2: var and function declarations bind in the nearest
    /// VARIABLE environment (function/program scope) — with and catch
    /// introduce only lexical object environments in between.</summary>
    public bool IsVariableEnvironment { get; set; } = true;

    /// <summary>Set when this scope's function has an arguments object
    /// whose index properties are mapped to the parameters (§10.1.8) —
    /// lets assignment paths sync cheaply without a table probe.</summary>
    public bool HasArgumentsBinding;

    private string[] _names = System.Array.Empty<string>();
    private JsValue[] _values = System.Array.Empty<JsValue>();
    private int _count;
    private bool _spilled;
    private Dictionary<string, JsValue>? _overflow;
    /// <summary>Names declared via Define (var/function/params) — these
    /// carry DontDelete (§10.5.3/§11.4.1); implicit globals created by bare
    /// assignment do not and can be deleted.</summary>
    private HashSet<string>? _declared;

    public JsScope(JsScope? parent = null)
    {
        Parent = parent;
    }

    private int IndexOf(string name)
    {
        var names = _names;
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(names[i], name) || string.Equals(names[i], name))
                return i;
        return -1;
    }

    /// <summary>Own-binding lookup — virtual so object scopes (with) can
    /// delegate to their object's property chain (§10.2.3, §12.10).</summary>
    protected virtual bool TryGetOwn(string name, out JsValue value)
    {
        if (!_spilled)
        {
            int idx = IndexOf(name);
            if (idx >= 0) { value = _values[idx]; return true; }
            value = null!;
            return false;
        }
        return _overflow!.TryGetValue(name, out value!);
    }

    internal bool HasOwn(string name) => TryGetOwn(name, out _);

    /// <summary>Own-binding write — virtual so object scopes (with) can
    /// route assignment to their object instead of a local slot.</summary>
    protected virtual void Put(string name, JsValue value) => PutLocal(name, value);

    /// <summary>Internal raw write used by eval hoisting (§10.2.2): no
    /// DontDelete bookkeeping, later declarations overwrite.</summary>
    internal void PutBinding(string name, JsValue value) => Put(name, value);

    private void PutLocal(string name, JsValue value)
    {
        if (!_spilled)
        {
            int idx = IndexOf(name);
            if (idx >= 0) { _values[idx] = value; return; }
            if (_count < SmallCapacity)
            {
                if (_names.Length == 0)
                {
                    _names = new string[SmallCapacity];
                    _values = new JsValue[SmallCapacity];
                }
                _names[_count] = name;
                _values[_count] = value;
                _count++;
                return;
            }
            // spill the inline entries into a dictionary, once
            _overflow = new Dictionary<string, JsValue>(StringComparer.Ordinal);
            for (int i = 0; i < _count; i++)
                _overflow[_names[i]] = _values[i];
            _spilled = true;
        }
        _overflow![name] = value;
    }

    private bool RemoveOwn(string name)
    {
        if (!_spilled)
        {
            int idx = IndexOf(name);
            if (idx < 0) return false;
            for (int i = idx; i < _count - 1; i++)
            {
                _names[i] = _names[i + 1];
                _values[i] = _values[i + 1];
            }
            _count--;
            _names[_count] = null!;
            _values[_count] = null!;
            return true;
        }
        return _overflow!.Remove(name);
    }

    /// <summary>
    /// Get a variable by walking up the scope chain.  Unresolved names
    /// consult the root's global fallback (window) before giving up —
    /// period scripts probe for optional globals and call alert() bare.
    /// The fallback is checked BEFORE the root's own dictionary so a direct
    /// window.foo write (which bypasses the scope) stays authoritative for
    /// both bare and window-qualified reads; scope writes keep the two in
    /// sync anyway (see Set/Define). Unresolved names return Undefined here;
    /// identifier evaluation applies the JavaScript ReferenceError rule.
    /// </summary>
    public JsValue Get(string name)
    {
        var scope = this;
        while (scope != null)
        {
            if (scope.Parent == null && scope.GlobalFallback != null && scope.GlobalFallback.Has(name))
                return scope.GlobalFallback.Get(name);
            if (scope.TryGetOwn(name, out var value))
                return value;
            if (scope.Parent == null)
                return JsValue.Undefined;
            scope = scope.Parent;
        }
        return JsValue.Undefined;
    }

    /// <summary>
    /// Set a variable, writing to the scope where it was declared; if it
    /// exists nowhere, it becomes a property of the global object (the
    /// root's fallback when present, the root dict otherwise).
    /// </summary>
    public void Set(string name, JsValue value)
    {
        var scope = this;
        while (scope != null)
        {
            if (scope.HasOwn(name))
            {
                scope.Put(name, value);
                // The root scope and the window/global object represent the
                // same global binding in legacy JavaScript.  Keep the
                // fallback synchronized when a global `var` is reassigned;
                // otherwise Get() can read a stale window property instead
                // of the newly assigned variable value.
                if (scope.Parent == null)
                    scope.GlobalFallback?.Set(name, value);
                return;
            }
            if (scope.Parent == null)
            {
                scope.Put(name, value);
                scope.GlobalFallback?.Set(name, value);       // root global and window share bindings
                return;
            }
            scope = scope.Parent;
        }
    }

    /// <summary>
    /// Capture where an assignment to <paramref name="name"/> lands by
    /// resolving the reference NOW (§11.13.1: the left-hand Reference is
    /// evaluated before the right-hand expression). The returned writer
    /// stores through the exact scope/with-object chosen at resolution
    /// time, so a binding that appears later — e.g. a with-object property
    /// created by the RHS itself (`with(o) x = o.x = 2` must set the OUTER
    /// x, not o.x) — cannot redirect the store.
    /// </summary>
    public System.Action<JsValue> ResolveAssignmentSink(string name)
    {
        var scope = this;
        while (scope != null)
        {
            if (scope.HasOwn(name))
            {
                var target = scope;
                return target.Parent == null
                    ? v => { target.Put(name, v); target.GlobalFallback?.Set(name, v); }
                    : v => target.Put(name, v);
            }
            if (scope.Parent == null)
            {
                var root = scope;
                return v => { root.Put(name, v); root.GlobalFallback?.Set(name, v); };
            }
            scope = scope.Parent;
        }
        return v => Set(name, v);
    }

    /// <summary>Define a variable in THIS scope (var declarations, params).
    /// Virtual: var/function declarations inside a with body bind in the
    /// enclosing variable environment (§10.2.2/§12.10), never the object.</summary>
    public virtual void Define(string name, JsValue value)
    {
        PutLocal(name, value);
        (_declared ??= new HashSet<string>()).Add(name);
        if (Parent == null)
            GlobalFallback?.Set(name, value);
    }

    /// <summary>§10.1.3/§12.2: route var (and for-in var) bindings past
    /// with/catch scopes into the enclosing variable environment.</summary>
    public void DefineInVariableEnv(string name, JsValue value)
    {
        var scope = this;
        while (scope != null && !scope.IsVariableEnvironment)
            scope = scope.Parent;
        (scope ?? this).Define(name, value);
    }

    /// <summary>Declare a var WITHOUT DontDelete — §10.2.2/§10.1.2: bindings
    /// created by EVAL code are ordinary properties of the variable object,
    /// so `eval("var x = 1"); delete x` is true. Like DeclareInVariableEnv
    /// this never clobbers an existing binding (eval's `var t` after a
    /// hoisted `function t` must not reset it — §10.1.3 "left unchanged").
    /// </summary>
    public void DefineInVariableEnvEval(string name, JsValue value)
    {
        var scope = this;
        while (scope != null && !scope.IsVariableEnvironment)
            scope = scope.Parent;
        scope = scope ?? this;
        if (!scope.HasOwn(name))
        {
            scope.PutBinding(name, value);
            if (scope.Parent == null)
                scope.GlobalFallback?.Set(name, value);
        }
    }

    /// <summary>Bind a hoisted name in eval mode: no DontDelete (§10.2.2),
    /// routed into the enclosing VARIABLE environment past with scopes.</summary>
    public void DefineEvalHoisted(string name, JsValue value)
    {
        var scope = this;
        while (scope != null && !scope.IsVariableEnvironment)
            scope = scope.Parent;
        scope = scope ?? this;
        if (!scope.HasOwn(name))
        {
            scope.Put(name, value);
            if (scope.Parent == null)
                scope.GlobalFallback?.Set(name, value);
        }
    }

    /// <summary>Declare a var WITHOUT clobbering an existing binding —
    /// the hoist already pre-bound the name; only absent names are
    /// introduced (as undefined, carrying DontDelete).</summary>
    public void DeclareInVariableEnv(string name)
    {
        var scope = this;
        while (scope != null && !scope.IsVariableEnvironment)
            scope = scope.Parent;
        scope = scope ?? this;
        if (!scope.HasOwn(name))
            scope.Define(name, JsValue.Undefined);
    }

    /// <summary>True if the name is visible from this scope (own, ancestor,
    /// or — at the root — the global fallback object).</summary>
    public bool Has(string name)
    {
        var scope = this;
        while (scope != null)
        {
            if (scope.HasOwn(name))
                return true;
            if (scope.Parent == null)
                return scope.GlobalFallback != null && scope.GlobalFallback.Has(name);
            scope = scope.Parent;
        }
        return false;
    }

    public JsScope NewChild() => new(this);

    /// <summary>
    /// Receiver for a bare-name call (§10.2.3): the object of the first
    /// with-environment that has the name — null when the name resolves as
    /// a plain binding (or not at all), where `this` is the global.
    /// </summary>
    public virtual JsObject? ThisFor(string name) =>
        HasOwn(name) ? null : Parent?.ThisFor(name);

    /// <summary>Own variable names of this scope (introspection).</summary>
    public IEnumerable<string> OwnKeys()
    {
        if (!_spilled)
        {
            for (int i = 0; i < _count; i++)
                yield return _names[i];
        }
        else
        {
            foreach (var k in _overflow!.Keys)
                yield return k;
        }
    }

    /// <summary>Delete from this scope only. Declared bindings (var,
    /// function declarations, params) carry DontDelete and refuse; implicit
    /// globals created by bare assignment delete (§11.4.1).</summary>
    public virtual bool Delete(string name)
    {
        if (_declared != null && _declared.Contains(name)) return false;
        bool removed = RemoveOwn(name);
        if (Parent == null && GlobalFallback != null)
            removed |= GlobalFallback.Delete(name);
        return removed;
    }
}

/// <summary>
/// Object environment for the with statement (§12.10). Identifier lookup
/// walks the with object's PROPERTY chain — including DontEnum builtins
/// like Date.prototype.getUTCMonth — instead of snapshotting enumerable
/// keys at entry. var/function declarations still bind in the enclosing
/// scope (§10.2.2), and assignment to names visible on the object writes
/// to the object itself.
/// </summary>
public sealed class JsWithScope : JsScope
{
    public JsObject Object { get; }

    public JsWithScope(JsScope parent, JsObject obj) : base(parent)
    {
        Object = obj ?? throw new ArgumentNullException(nameof(obj));
        IsVariableEnvironment = false;   // §12.10: object environment
    }

    protected override bool TryGetOwn(string name, out JsValue value)
    {
        // HasProperty walks the object's prototype chain (§8.6.1.2)
        if (Object.Has(name))
        {
            value = Object.Get(name);
            return true;
        }
        value = null!;
        return false;
    }

    protected override void Put(string name, JsValue value)
    {
        // Assignment through the with scope lands on the object (§10.2.3)
        Object.Set(name, value);
    }

    public override void Define(string name, JsValue value) =>
        Parent!.Define(name, value);   // §10.2.2: var binds in the variable environment

    public override bool Delete(string name) =>
        Object.Has(name) ? Object.Delete(name) : base.Delete(name);

    public override JsObject? ThisFor(string name) =>
        Object.Has(name) ? Object : Parent?.ThisFor(name);
}
