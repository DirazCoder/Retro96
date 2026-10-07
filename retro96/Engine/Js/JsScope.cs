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
/// </summary>
public class JsScope
{
    private const int SmallCapacity = 6;

    public JsScope? Parent { get; }

    /// <summary>Set on the root scope only — the global object (window).</summary>
    public JsObject? GlobalFallback { get; set; }

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

    private void Put(string name, JsValue value)
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

    private bool TryGetOwn(string name, out JsValue value)
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

    private bool HasOwn(string name) => TryGetOwn(name, out _);

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

    /// <summary>Define a variable in THIS scope (var declarations, params).</summary>
    public void Define(string name, JsValue value)
    {
        Put(name, value);
        (_declared ??= new HashSet<string>()).Add(name);
        if (Parent == null)
            GlobalFallback?.Set(name, value);
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
    public bool Delete(string name)
    {
        if (_declared != null && _declared.Contains(name)) return false;
        bool removed = RemoveOwn(name);
        if (Parent == null && GlobalFallback != null)
            removed |= GlobalFallback.Delete(name);
        return removed;
    }
}
