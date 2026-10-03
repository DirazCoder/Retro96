using System;
using System.Collections.Generic;

namespace Retro96.Engine.Js;

// Uses JsValue from JsValue.cs

public class JsScope
{
    public JsScope? Parent { get; }
    private Dictionary<string, JsValue> _vars = new(StringComparer.Ordinal);
    
    // Constructor: root scope has Parent = null
    public JsScope(JsScope? parent = null)
    {
        Parent = parent;
    }
    
    /// <summary>
    /// Get a variable value by walking up the scope chain.
    /// If not found in any scope, return JsValue.Undefined (not throw).
    /// This matches JS1.1 behavior where accessing undefined variables returns undefined.
    /// </summary>
    public JsValue Get(string name)
    {
        // Check current scope
        if (_vars.TryGetValue(name, out var value))
            return value;
        
        // Walk up the scope chain
        if (Parent != null)
            return Parent.Get(name);
        
        // Not found anywhere - return undefined (JS1.1 behavior)
        return JsValue.Undefined;
    }
    
    /// <summary>
    /// Set a variable value by walking up the scope chain to find where it was declared.
    /// If not found in any existing scope, define it in the GLOBAL scope (top-level).
    /// This implements JS1.1's var hoisting and scope chain behavior.
    /// </summary>
    public void Set(string name, JsValue value)
    {
        // Walk up to find where the variable is declared
        if (_vars.ContainsKey(name))
        {
            _vars[name] = value;
            return;
        }
        
        if (Parent != null)
        {
            Parent.Set(name, value);
            return;
        }
        
        // Not found anywhere - define in global scope (this scope is global)
        _vars[name] = value;
    }
    
    /// <summary>
    /// Define a variable in THIS scope (for var declarations).
    /// This is used when entering a new scope (function, with, catch).
    /// If the variable already exists in this scope, overwrite it (var can redeclare).
    /// </summary>
    public void Define(string name, JsValue value)
    {
        _vars[name] = value;
    }
    
    /// <summary>
    /// Check if a variable exists anywhere in the scope chain.
    /// Returns true if found in any scope (own or parent).
    /// </summary>
    public bool Has(string name)
    {
        if (_vars.ContainsKey(name))
            return true;
        
        if (Parent != null)
            return Parent.Has(name);
        
        return false;
    }
    
    /// <summary>
    /// Create a new child scope with this scope as parent.
    /// Used when entering a function, with-block, catch clause, etc.
    /// </summary>
    public JsScope NewChild()
    {
        return new JsScope(this);
    }
    
    /// <summary>
    /// Get all own variable names in this scope (for debugging/introspection).
    /// </summary>
    public IEnumerable<string> OwnKeys()
    {
        return _vars.Keys;
    }
    
    /// <summary>
    /// Delete a variable from this scope only.
    /// Returns true if deleted, false if not found in this scope.
    /// In JS1.1, delete on variables returns false and doesn't delete.
    /// We implement this for completeness but note the JS1.1 limitation.
    /// </summary>
    public bool Delete(string name)
    {
        return _vars.Remove(name);
    }
}