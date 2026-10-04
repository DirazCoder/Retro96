using System;
using System.Collections.Generic;
using System.Linq;

namespace Retro96.Engine.Vbs;

/// <summary>
/// Indexed property assignment (`obj.Prop(i) = v`, `Set obj.Prop(i) = o`) for
/// objects whose members take an index plus the assigned value — implemented
/// by VbsClassInstance (Property Let/Set with parameters). Kept internal and
/// OPTIONAL so host objects that don't know about it are unaffected.
/// </summary>
internal interface IVbsIndexedPropertyAssign
{
    bool TryAssignIndexed(string name, VbsVariant[] indices, VbsVariant value, bool isSet);
}

/// <summary>Compiled class layout: fields, methods, properties and the
/// Class_Initialize/Class_Terminate hooks. Immutable after registration.</summary>
internal sealed class VbsClass
{
    public string Name { get; private set; } = "";
    public List<VbsClassField> Fields { get; } = new();
    public Dictionary<string, VbsClassMethod> Methods { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VbsClassProperty> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VbsProcedure? Initialize { get; private set; }
    public VbsProcedure? Terminate { get; private set; }

    private VbsClass() { }

    public static VbsClass Build(VbsClassStatement decl)
    {
        var cls = new VbsClass { Name = decl.Name };
        foreach (var member in decl.Members)
        {
            switch (member)
            {
                case VbsClassFieldDecl f:
                    foreach (var d in f.Fields)
                        cls.Fields.Add(new VbsClassField(d.Name, d.Bounds, d.IsArray, f.IsPublic));
                    break;

                case VbsClassMethodDecl m:
                {
                    var proc = new VbsProcedure(m.Name, m.IsFunction,
                        m.Params.Select(p => (p.Name, p.ByVal)).ToList(), m.Body);
                    if (m.Name.Equals("Class_Initialize", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cls.Initialize != null)
                            throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
                                $"Name redefined: '{m.Name}'", m.Line);
                        cls.Initialize = proc;
                    }
                    else if (m.Name.Equals("Class_Terminate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cls.Terminate != null)
                            throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
                                $"Name redefined: '{m.Name}'", m.Line);
                        cls.Terminate = proc;
                    }
                    else
                    {
                        cls.Methods[m.Name] = new VbsClassMethod(proc, m.IsPublic);
                    }
                    break;
                }

                case VbsClassPropertyDecl p:
                {
                    if (!cls.Properties.TryGetValue(p.Name, out var prop))
                    {
                        prop = new VbsClassProperty();
                        cls.Properties[p.Name] = prop;
                    }
                    // A Get is function-like (the property name is its return
                    // variable); Let/Set are Sub-like, value as last parameter.
                    var proc = new VbsProcedure(p.Name, p.Kind == VbsPropertyKind.Get,
                        p.Params.Select(pr => (pr.Name, pr.ByVal)).ToList(), p.Body);
                    var slot = new VbsClassMethod(proc, p.IsPublic);
                    switch (p.Kind)
                    {
                        case VbsPropertyKind.Get:
                            if (prop.Get != null) Duplicate(p.Name, p.Line);
                            prop.Get = slot;
                            break;
                        case VbsPropertyKind.Let:
                            if (prop.Let != null) Duplicate(p.Name, p.Line);
                            prop.Let = slot;
                            break;
                        case VbsPropertyKind.Set:
                            if (prop.Set != null) Duplicate(p.Name, p.Line);
                            prop.Set = slot;
                            break;
                    }
                    break;
                }
            }
        }
        return cls;
    }

    private static void Duplicate(string name, int line) =>
        throw new VbsRuntimeException(VbsErrorNumbers.NameRedefined,
            $"Name redefined: '{name}'", line);
}

internal sealed record VbsClassField(string Name, List<VbsExpr> Bounds, bool IsArray, bool IsPublic);

internal sealed record VbsClassMethod(VbsProcedure Proc, bool IsPublic);

internal sealed class VbsClassProperty
{
    public VbsClassMethod? Get, Let, Set;
}

/// <summary>
/// One live object of a script class. Fits the VbsVariant object slot via
/// IVbsDispatchObject: fields read/write through TryGet/TrySetMember, Property
/// Get via TryGetMember (zero-arg) / TryInvoke (indexed), Property Let/Set via
/// TrySetMember, methods via TryInvoke (and zero-arg invoke through member
/// read, like WScript.Echo WScript.ScriptEngine). Private members answer
/// "no such member" (error 438) unless a method of this instance is on the
/// stack (MethodDepth, maintained by the interpreter).
/// Method/property arguments are ByVal only — the dispatch interface passes
/// values, not cells (documented deviation).
/// </summary>
public sealed class VbsClassInstance : IVbsDispatchObject, IVbsIndexedPropertyAssign
{
    private readonly VbsClass _class;
    private readonly VbsInterpreter _interpreter;
    private readonly Dictionary<string, bool> _fieldIsPublic = new(StringComparer.OrdinalIgnoreCase);
    private bool _terminated;

    /// <summary>Per-instance field storage; the interpreter consults this for
    /// unqualified names inside class code (methods see their fields).</summary>
    internal readonly Dictionary<string, VbsCell> Fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Depth of interpreter calls currently running inside methods of
    /// THIS instance — gates private member access.</summary>
    internal int MethodDepth;

    internal VbsClassInstance(VbsClass cls, VbsInterpreter interpreter)
    {
        _class = cls;
        _interpreter = interpreter;
    }

    public string VbsTypeName => _class.Name;

    private bool CanAccess(bool isPublic) => isPublic || MethodDepth > 0;

    /// <summary>Allocates declared fields and runs Class_Initialize.</summary>
    internal void Construct(VbsFrame frame)
    {
        foreach (var field in _class.Fields)
        {
            VbsVariant value = !field.IsArray ? VbsVariant.Empty
                : field.Bounds.Count == 0 ? VbsVariant.Of(VbsArray.Unallocated())
                : VbsVariant.Of(VbsArray.Allocate(false,
                    _interpreter.EvaluateArrayLengths(field.Bounds, frame, field.Name)));
            Fields[field.Name] = new VbsCell(value);
            _fieldIsPublic[field.Name] = field.IsPublic;
        }
        if (_class.Initialize != null)
            _interpreter.InvokeMethod(_class.Initialize, Array.Empty<VbsVariant>(), this);
    }

    /// <summary>Runs Class_Terminate once (session teardown); errors inside
    /// the destructor are swallowed — teardown must not derail the host.</summary>
    internal void Terminate()
    {
        if (_terminated) return;
        _terminated = true;
        if (_class.Terminate == null) return;
        try { _interpreter.InvokeMethod(_class.Terminate, Array.Empty<VbsVariant>(), this); }
        catch (VbsRuntimeException) { /* documented: destructor errors are ignored */ }
    }

    public bool TryGetMember(string name, out VbsVariant value)
    {
        if (Fields.TryGetValue(name, out var cell))
        {
            if (!CanAccess(_fieldIsPublic[name])) { value = default; return false; }
            value = cell.Value;
            return true;
        }
        if (_class.Properties.TryGetValue(name, out var prop) &&
            prop.Get is { } g && CanAccess(g.IsPublic) && g.Proc.Params.Count == 0)
        {
            value = _interpreter.InvokeMethod(g.Proc, Array.Empty<VbsVariant>(), this);
            return true;
        }
        // Zero-argument method read as a property — `x = obj.Method` and
        // `WScript.Echo obj.Prop` style member reads invoke the method.
        if (_class.Methods.TryGetValue(name, out var m) &&
            CanAccess(m.IsPublic) && m.Proc.Params.Count == 0)
        {
            value = _interpreter.InvokeMethod(m.Proc, Array.Empty<VbsVariant>(), this);
            return true;
        }
        value = default;
        return false;
    }

    public bool TrySetMember(string name, VbsVariant value)
    {
        if (Fields.TryGetValue(name, out var cell))
        {
            if (!CanAccess(_fieldIsPublic[name])) return false;
            cell.Value = value;
            return true;
        }
        if (_class.Properties.TryGetValue(name, out var prop))
        {
            // Property Set when assigning an object reference, else Let.
            if (value.Type == VbVarType.Object && prop.Set is { } s &&
                CanAccess(s.IsPublic) && s.Proc.Params.Count == 1)
            {
                _interpreter.InvokeMethod(s.Proc, new[] { value }, this);
                return true;
            }
            if (prop.Let is { } l && CanAccess(l.IsPublic) && l.Proc.Params.Count == 1)
            {
                _interpreter.InvokeMethod(l.Proc, new[] { value }, this);
                return true;
            }
        }
        return false;
    }

    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    {
        result = VbsVariant.Empty;
        if (_class.Properties.TryGetValue(name, out var prop))
        {
            if (prop.Get is { } g && CanAccess(g.IsPublic) && g.Proc.Params.Count == args.Length)
            {
                result = _interpreter.InvokeMethod(g.Proc, args, this);
                return true;
            }
            return false;
        }
        if (_class.Methods.TryGetValue(name, out var m) &&
            CanAccess(m.IsPublic) && m.Proc.Params.Count == args.Length)
        {
            result = _interpreter.InvokeMethod(m.Proc, args, this);
            return true;
        }
        return false;
    }

    /// <summary>`obj.Prop(i) = v` / `Set obj.Prop(i) = o` → Property Let/Set
    /// with indices; the value is the accessor's LAST parameter.</summary>
    public bool TryAssignIndexed(string name, VbsVariant[] indices, VbsVariant value, bool isSet)
    {
        if (!_class.Properties.TryGetValue(name, out var prop)) return false;
        var acc = isSet ? prop.Set : prop.Let;
        if (acc == null || !CanAccess(acc.IsPublic) || acc.Proc.Params.Count != indices.Length + 1)
            return false;
        var args = new VbsVariant[indices.Length + 1];
        indices.CopyTo(args, 0);
        args[^1] = value;
        _interpreter.InvokeMethod(acc.Proc, args, this);
        return true;
    }

    // No default property (the 5.0 Default keyword is deliberately omitted)
    // and no enumeration.
    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;
    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }
    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    { items = Array.Empty<VbsVariant>(); return false; }
}
