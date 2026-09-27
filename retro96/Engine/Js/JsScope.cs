using System.Collections.Generic;

namespace Retro96.Engine.Js;

/// <summary>
/// Lexical scope chain for the interpreter.  JS 1.1 semantics: var is
/// function-scoped (blocks share their function's scope), assignment to an
/// undeclared name becomes a property of the global object, and reading an
/// undeclared name yields undefined rather than an error.
///
/// The ROOT scope can carry a GlobalFallback object (the window): in real
/// JS the global scope and the window object are the same thing, so bare
/// alert()/confirm()/status reads resolve through the fallback, and
/// implicit global writes (status = "…") land on it where both window.status
/// and bare reads find them.
/// </summary>
public class JsScope
{
    public JsScope? Parent { get; }

    /// <summary>Set on the root scope only — the global object (window).</summary>
    public JsObject? GlobalFallback { get; set; }

    private readonly Dictionary<string, JsValue> _vars = new(StringComparer.Ordinal);

    public JsScope(JsScope? parent = null)
    {
        Parent = parent;
    }

    /// <summary>
    /// Get a variable by walking up the scope chain.  Unresolved names
    /// consult the root's global fallback (window) before giving up —
    /// period scripts probe for optional globals and call alert() bare.
    /// </summary>
    public JsValue Get(string name)
    {
        var scope = this;
        while (scope != null)
        {
            if (scope.Parent == null && scope.GlobalFallback != null && scope.GlobalFallback.Has(name))
                return scope.GlobalFallback.Get(name);
            if (scope._vars.TryGetValue(name, out var value))
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
            if (scope._vars.ContainsKey(name))
            {
                scope._vars[name] = value;
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
                scope._vars[name] = value;
                scope.GlobalFallback?.Set(name, value);       // root global and window share bindings
                return;
            }
            scope = scope.Parent;
        }
    }

    /// <summary>Define a variable in THIS scope (var declarations, params).</summary>
    public void Define(string name, JsValue value)
    {
        _vars[name] = value;
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
            if (scope._vars.ContainsKey(name))
                return true;
            if (scope.Parent == null)
                return scope._vars.ContainsKey(name) || (scope.GlobalFallback != null && scope.GlobalFallback.Has(name));
            scope = scope.Parent;
        }
        return false;
    }

    public JsScope NewChild() => new(this);

    /// <summary>Own variable names of this scope (introspection).</summary>
    public IEnumerable<string> OwnKeys() => _vars.Keys;

    /// <summary>Delete from this scope only (JS 1.1: delete on vars fails).</summary>
    public bool Delete(string name)
    {
        bool removed = _vars.Remove(name);
        if (Parent == null && GlobalFallback != null)
            removed |= GlobalFallback.Delete(name);
        return removed;
    }
}