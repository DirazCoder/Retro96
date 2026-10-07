using System;
using System.Collections.Generic;
using System.Globalization;

namespace Retro96.Engine.Js;

public enum JsType
{
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Object,
    Function
}

/// <summary>
/// A JavaScript 1999 value.  Conversions follow the era's rules:
/// numbers stringify with the classic integer/exponent split, strings
/// parse as octal when they look octal (decimal fallback for 8/9), and
/// abstract (loose) equality implements the full conversion table.
/// Objects carrying a numeric "value" property (Date, Number wrappers)
/// convert to numbers through it — valueOf semantics, so +new Date() is
/// the epoch milliseconds instead of NaN parsed off the formatted string.
/// </summary>
public class JsValue
{
    public JsType Type { get; internal set; }

    private double _numberValue;
    private string? _stringValue;
    private bool _boolValue;
    private JsObject? _objectValue;
    private JsFunction? _functionValue;

    public static JsValue Undefined { get; } = new() { Type = JsType.Undefined };
    public static JsValue Null      { get; } = new() { Type = JsType.Null };
    public static JsValue True      { get; } = new() { Type = JsType.Boolean, _boolValue = true };
    public static JsValue False     { get; } = new() { Type = JsType.Boolean, _boolValue = false };
    private static readonly JsValue NegativeZero = new() { Type = JsType.Number, _numberValue = -0.0 };

    // Instance cache — JsValue is immutable after construction and the
    // tree-walking interpreter churns small integers (loop counters, array
    // indices) millions of times per script. Sharing boxed instances keeps
    // arithmetic out of the allocator. Identity of a JsValue is never
    // observable (only JsObject identity is), so this is semantically
    // transparent; −0 keeps its own instance so 1/x keeps the sign.
    private static readonly JsValue[] SmallInts = CreateSmallInts();
    private static JsValue[] CreateSmallInts()
    {
        var a = new JsValue[1104];                       // covers −64 … 1039
        for (int i = 0; i < a.Length; i++)
            a[i] = new JsValue { Type = JsType.Number, _numberValue = i - 64 };
        return a;
    }

    public static JsValue From(bool v)      => v ? True : False;
    public static JsValue From(double v)
    {
        // −0 keeps its own instance so 1/x keeps the sign (it falls through
        // the small-int cache because (long)(−0.0) == 0 == −0.0)
        if (v == 0.0 && double.IsNegativeInfinity(1 / v)) return NegativeZero;
        if (v is >= -64.0 and <= 1039.0)
        {
            long iv = (long)v;
            if (iv == v) return SmallInts[(int)iv + 64];
        }
        return new JsValue { Type = JsType.Number, _numberValue = v };
    }
    public static JsValue From(int v) =>
        v is >= -64 and <= 1039 ? SmallInts[v + 64]
                                : new JsValue { Type = JsType.Number, _numberValue = v };
    public static JsValue From(string v)    => new() { Type = JsType.String,   _stringValue = v ?? "" };
    public static JsValue FromObject(JsObject o)    => new() { Type = JsType.Object,   _objectValue = o ?? throw new ArgumentNullException(nameof(o)) };
    public static JsValue FromFunction(JsFunction f) => new() { Type = JsType.Function, _functionValue = f ?? throw new ArgumentNullException(nameof(f)) };

    public double GetNumber() =>
        Type == JsType.Number ? _numberValue
        : throw new InvalidOperationException("JsValue is not a Number");

    public string GetString() =>
        Type == JsType.String ? _stringValue!
        : throw new InvalidOperationException("JsValue is not a String");

    public bool GetBool() =>
        Type == JsType.Boolean ? _boolValue
        : throw new InvalidOperationException("JsValue is not a Boolean");

    public JsObject GetObject() =>
        Type == JsType.Object && _objectValue != null ? _objectValue
        : throw new InvalidOperationException("JsValue is not an Object");

    public JsFunction GetFunction() =>
        Type == JsType.Function && _functionValue != null ? _functionValue
        : throw new InvalidOperationException("JsValue is not a Function");

    /// <summary>The object behind an Object OR Function value (functions are objects).</summary>
    public JsObject GetObjectOrFunction() => Type switch
    {
        JsType.Object   => _objectValue ?? throw new InvalidOperationException("Object value is null"),
        JsType.Function => _functionValue ?? throw new InvalidOperationException("Function value is null"),
        _ => throw new InvalidOperationException($"Cannot use {Type} as an object")
    };

    // ── Conversions ─────────────────────────────────────────────────────────

    public double ToNumber() => Type switch
    {
        JsType.Undefined => double.NaN,
        JsType.Null      => 0.0,
        JsType.Boolean   => _boolValue ? 1.0 : 0.0,
        JsType.Number    => _numberValue,
        JsType.String    => StringToNumber(_stringValue!),
        JsType.Object or JsType.Function => ToPrimitive().ToNumber(),
        _ => double.NaN
    };

    public string ToJsString() => Type switch
    {
        JsType.Undefined => "undefined",
        JsType.Null      => "null",
        JsType.Boolean   => _boolValue ? "true" : "false",
        JsType.Number    => NumberToString(_numberValue),
        JsType.String    => _stringValue!,
        JsType.Object or JsType.Function => ToPrimitive(preferString: true).ToJsString(),
        _ => "undefined"
    };

    public bool ToBoolean() => Type switch
    {
        JsType.Undefined or JsType.Null => false,
        JsType.Boolean  => _boolValue,
        JsType.Number   => !(_numberValue == 0.0 || double.IsNaN(_numberValue)),
        JsType.String   => !string.IsNullOrEmpty(_stringValue),
        JsType.Object or JsType.Function => true,
        _ => false
    };

    /// <summary>
    /// ToPrimitive: primitives stay as they are; objects go through the
    /// interpreter-registered stringifier (arrays join, dates stringify) —
    /// the STRING form, so "" + date is the formatted date while date - 1
    /// (numeric context) uses ToNumber's valueOf path.
    /// </summary>
    public JsValue ToPrimitive() => ToPrimitive(preferString: false);

    private JsValue ToPrimitive(bool preferString)
    {
        if (Type is not (JsType.Object or JsType.Function))
            return this;

        var obj = GetObjectOrFunction();
        // This engine-level shortcut is used for STRING/NUMBER hinted
        // conversions only. The no-hint Date-prefers-string rule (§11.6.1)
        // lives in the interpreter's Add, not here — a bare ToPrimitive must
        // be valueOf-first (ms for a Date, so d2 - dateObject stays numeric).
        if (obj.TryConvertToPrimitive(preferString, out var primitive))
            return primitive;
        if (JsObject.Stringifier != null)
            return JsValue.From(JsObject.Stringifier(obj));
        return JsValue.From(obj.Class.Length > 0
            ? $"[object {obj.Class}]"
            : "function() { ... }");
    }

    // ── Equality ───────────────────────────────────────────────────────────

    public bool AbstractEquals(JsValue other)
    {
        if (other == null) throw new ArgumentNullException(nameof(other));

        if (Type == other.Type)
            return StrictEquals(other);

        // null == undefined
        if ((Type == JsType.Null && other.Type == JsType.Undefined) ||
            (Type == JsType.Undefined && other.Type == JsType.Null))
            return true;

        // boolean on either side → number, retry
        if (Type == JsType.Boolean)
            return From(ToNumber()).AbstractEquals(other);
        if (other.Type == JsType.Boolean)
            return AbstractEquals(From(other.ToNumber()));

        // number vs string → number
        if (Type == JsType.Number && other.Type == JsType.String)
            return _numberValue == other.ToNumber();
        if (Type == JsType.String && other.Type == JsType.Number)
            return ToNumber() == other._numberValue;

        // object vs string → string form of the object
        if (IsObjectLike(Type) && other.Type == JsType.String)
            return ToPrimitive().AbstractEquals(other);
        if (Type == JsType.String && IsObjectLike(other.Type))
            return AbstractEquals(other.ToPrimitive());

        // object vs number → valueOf (date == ms works)
        if (IsObjectLike(Type) && other.Type == JsType.Number)
            return ToNumber() == other.GetNumber();
        if (Type == JsType.Number && IsObjectLike(other.Type))
            return GetNumber() == other.ToNumber();

        return false;   // two unrelated object references
    }

    private static bool IsObjectLike(JsType t) =>
        t is JsType.Object or JsType.Function;

    public bool StrictEquals(JsValue other)
    {
        if (other == null) throw new ArgumentNullException(nameof(other));
        if (Type != other.Type) return false;

        return Type switch
        {
            JsType.Undefined or JsType.Null => true,
            JsType.Boolean  => _boolValue == other._boolValue,
            JsType.Number   => NumberEquals(_numberValue, other._numberValue),
            JsType.String   => _stringValue == other._stringValue,
            JsType.Object   => ReferenceEquals(_objectValue, other._objectValue),
            JsType.Function => ReferenceEquals(_functionValue, other._functionValue),
            _ => false
        };
    }

    private static bool NumberEquals(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return false;
        return a == b;
    }

    // ── Number formatting (classic JavaScript rules) ────────────────────────

    /// <summary>
    /// ECMAScript-era number-to-string: integers print in full digits up to
    /// 1e21, above that (and below 1e-6) switch to "1e+21" style exponent
    /// form with no leading zeros in the exponent.
    /// </summary>
    public static string NumberToString(double n)
    {
        if (double.IsNaN(n))          return "NaN";
        if (double.IsPositiveInfinity(n)) return "Infinity";
        if (double.IsNegativeInfinity(n)) return "-Infinity";
        if (n == 0.0)                 return "0";   // covers -0 too

        bool neg = n < 0;
        double a = Math.Abs(n);

        // §9.8.1: print the SHORTEST decimal digits that round-trip —
        // ToString(123456789012345664) is "123456789012345660", not the
        // exact double digits. "R" on .NET Core is shortest round-trip.
        string s = a.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('E'))
            return neg ? "-" + s : s;

        // "1.05E+21" → digits "105", leading-digit exponent E=21
        int e = s.IndexOf('E');
        string mantissa = s[..e];
        string expRaw   = s[(e + 1)..];
        bool expNeg = expRaw.StartsWith('-');
        string expDigits = expRaw.TrimStart('-', '+').TrimStart('0');
        if (expDigits.Length == 0) expDigits = "0";

        string digits = mantissa.Replace(".", "");
        int k = digits.Length;                       // significant digits
        int bigE = (expNeg ? -1 : 1) * int.Parse(expDigits, CultureInfo.InvariantCulture);
        int pointPos = bigE + 1;                     // decimal-point position (§9.8.1)

        // §9.8.1: decimal forms when the decimal point sits within (−6, 21]
        if (k <= pointPos && pointPos <= 21)
        {
            string dec = digits + new string('0', pointPos - k);
            return neg ? "-" + dec : dec;
        }
        if (0 < pointPos && pointPos <= 21)
        {
            string dec = digits[..pointPos] + "." + digits[pointPos..];
            return neg ? "-" + dec : dec;
        }
        if (-6 < pointPos && pointPos <= 0)
        {
            string dec = "0." + new string('0', -pointPos) + digits;
            return neg ? "-" + dec : dec;
        }

        // Exponential form: d[.ddd]e±X with the exponent (n−1)
        string exp = "e" + (pointPos - 1 >= 0 ? "+" : "-") + Math.Abs(pointPos - 1).ToString(CultureInfo.InvariantCulture);
        string sci = k == 1 ? digits + exp : digits[..1] + "." + digits[1..] + exp;
        return neg ? "-" + sci : sci;
    }

    /// <summary>
    /// String-to-number per JS 1.1: whitespace-trimmed, hex with 0x, octal
    /// for 0-prefixed all-digit strings (falling back to decimal when the
    /// digits contain 8 or 9), "Infinity" accepted, otherwise NaN.
    /// </summary>
    public static double StringToNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0.0;
        string t = s.Trim().Trim('\uFEFF');   // §7.2: the BOM is whitespace
        if (t.Length == 0) return 0.0;

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            string hex = t[2..];
            if (hex.Length == 0) return double.NaN;
            // §9.3.1: derive the exact mathematical value, then round once
            if (hex.Length <= 15)
            {
                long val = 0;
                foreach (char c in hex)
                {
                    int d = c switch
                    {
                        >= '0' and <= '9' => c - '0',
                        >= 'a' and <= 'f' => c - 'a' + 10,
                        >= 'A' and <= 'F' => c - 'A' + 10,
                        _ => -1
                    };
                    if (d < 0) return double.NaN;
                    val = val * 16 + d;
                }
                return val;
            }
            var bi = System.Numerics.BigInteger.Zero;
            foreach (char c in hex)
            {
                int d = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => -1
                };
                if (d < 0) return double.NaN;
                bi = bi * 16 + d;
            }
            return (double)bi;   // round-to-nearest; > DBL_MAX saturates to ∞
        }

        if (t == "Infinity" || t == "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;

        // §9.3.1: leading-zero digit runs are DECIMAL here (Math.abs('077')
        // is 77 — octal parsing belongs to parseInt and to the lexer's
        // integer-literal grammar, not ToNumber).
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return num;

        return double.NaN;
    }

    private static bool AllDigits(string s)
    {
        foreach (char c in s)
            if (!char.IsDigit(c)) return false;
        return true;
    }
}

/// <summary>
/// A JavaScript object.  Get/Set are virtual so the DOM bindings can
/// subclass this and route property access to a live DomElement
/// (document.images[0].src = "..." must hit the real element and
/// invalidate layout — the single most common script of 1996 pages).
/// </summary>
public class JsObject
{
    /// <summary>ECMA-262 property attributes (§8.6.1). Only populated for
    /// properties whose attributes differ from the plain-object default
    /// (writable, enumerable, deletable) — i.e. builtins.</summary>
    [System.Flags]
    internal enum PropAttr : byte
    {
        None = 0,
        ReadOnly = 1,
        DontEnum = 2,
        DontDelete = 4,
        Builtin = ReadOnly | DontEnum | DontDelete   // §15 prelude default for builtins
    }

    internal System.Collections.Generic.Dictionary<string, PropAttr>? Attrs;

    internal bool HasAttr(string name, PropAttr attr) =>
        Attrs != null && Attrs.TryGetValue(name, out var a) && (a & attr) != 0;

    /// <summary>Marks the object's current own properties with the given
    /// attribute set (see <see cref="PropAttr.Builtin"/> for the §15
    /// built-in defaults).</summary>
    internal void FreezeBuiltins() =>
        MarkAll(PropAttr.Builtin);

    /// <summary>Marks the object's current own properties with the given
    /// attribute set.</summary>
    internal void MarkAll(PropAttr attrs)
    {
        Attrs ??= new System.Collections.Generic.Dictionary<string, PropAttr>();
        foreach (var key in Properties.Keys)
            Attrs[key] = attrs;
    }

    /// <summary>
    /// Internal class tag ("Array", "Date", "Error", "" for plain) used by
    /// stringification and type checks.
    /// </summary>
    public string Class { get; set; } = "";

    public Dictionary<string, JsValue> Properties { get; } = new(StringComparer.Ordinal);
    public JsObject? Prototype { get; set; }
    internal Func<JsObject, bool, JsValue>? PrimitiveConverter { get; set; }

    /// <summary>Unconditional write used by the engine for its own bookkeeping.</summary>
    internal void SetWritable(string name, JsValue value) => Properties[name] = value;

    /// <summary>
    /// Hook the interpreter installs so arrays join and dates stringify
    /// correctly wherever a JsValue must become a string ("" + [1,2]).
    /// </summary>
    public static Func<JsObject, string>? Stringifier { get; set; }

    internal bool TryConvertToPrimitive(bool preferString, out JsValue primitive)
    {
        for (JsObject? current = this; current != null; current = current.Prototype)
        {
            if (current.PrimitiveConverter is { } converter)
            {
                primitive = converter(this, preferString);
                return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// String coercion for object values.  Objects use the interpreter's
    /// registered stringifier when present (arrays join, dates format),
    /// otherwise the classic `[object Type]` fallback used by JS engines.
    /// </summary>
    public virtual string ToJsString()
    {
        if (Stringifier != null)
            return Stringifier(this);

        if (!string.IsNullOrEmpty(Class))
            return $"[object {Class}]";

        return "[object Object]";
    }

    public virtual JsValue Get(string name)
    {
        if (Properties.TryGetValue(name, out var value))
            return value;
        if (Prototype != null)
            return Prototype.Get(name);
        return JsValue.Undefined;
    }

    public virtual void Set(string name, JsValue value)
    {
        // §8.6.2.2: a plain assignment to a ReadOnly property changes nothing
        if (Attrs != null && Attrs.TryGetValue(name, out var attr) && (attr & PropAttr.ReadOnly) != 0)
            return;
        if (Class == "Array")
        {
            if (name == "length")
            {
                double newLength = value.ToNumber();
                if (double.IsNaN(newLength) || double.IsInfinity(newLength) ||
                    newLength < 0 || newLength > uint.MaxValue ||
                    newLength != Math.Truncate(newLength))
                    throw new JsRangeErrorException("invalid array length");

                uint length = (uint)newLength;
                if (Properties.TryGetValue("length", out var currentLength) &&
                    currentLength.Type == JsType.Number &&
                    length < currentLength.GetNumber())
                {
                    foreach (string key in Properties.Keys.ToArray())
                        if (IsArrayIndex(key) && uint.Parse(key, CultureInfo.InvariantCulture) >= length)
                            Properties.Remove(key);
                }
                Properties[name] = JsValue.From((double)length);
                return;
            }

            if (IsArrayIndex(name))
            {
                uint index = uint.Parse(name, CultureInfo.InvariantCulture);
                if (index < uint.MaxValue &&
                    (!Properties.TryGetValue("length", out var currentLength) ||
                     currentLength.Type != JsType.Number ||
                     index >= currentLength.GetNumber()))
                    Properties["length"] = JsValue.From((double)index + 1);
            }
        }
        Properties[name] = value;
    }

    private static bool IsArrayIndex(string name) =>
        uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out uint index) &&
        index < uint.MaxValue &&
        index.ToString(CultureInfo.InvariantCulture) == name;

    public bool Has(string name)
    {
        if (Properties.ContainsKey(name))
            return true;
        if (Prototype != null)
            return Prototype.Has(name);
        return false;
    }

    public bool HasOwn(string name) => Properties.ContainsKey(name);

    /// <summary>Delete honouring the DontDelete attribute (§8.6.2.5).</summary>
    public virtual bool Delete(string name)
    {
        if (HasAttr(name, PropAttr.DontDelete)) return false;
        return Properties.Remove(name);
    }

    /// <summary>
    /// Snapshot of the own enumerable keys — for-in bodies may DELETE
    /// properties while iterating, which threw InvalidOperationException
    /// when this was a live Dictionary view.
    /// </summary>
    public IEnumerable<string> OwnEnumerableKeys()
    {
        if (Attrs == null) return Properties.Keys.ToArray();
        return Properties.Keys.Where(k => !HasAttr(k, PropAttr.DontEnum)).ToArray();
    }
}

/// <summary>A JavaScript function — either native C# or interpreted AST.</summary>
public class JsFunction : JsObject
{
    public string? Name { get; }
    public IReadOnlyList<string> Params { get; }
    public FunctionExpr? Body { get; }
    public Func<JsValue, JsValue[], JsValue>? Native { get; }
    public JsScope ClosureScope { get; }

    /// <summary>
    /// Some legacy host callables are themselves callable collection objects.
    /// Old IE treated document.all(...) / element.all(...) as invoking the
    /// collection object, rather than using the containing object/global as
    /// the JavaScript <c>this</c> value. The interpreter opts into that host
    /// behaviour only for such functions so ordinary object methods keep the
    /// normal JavaScript receiver rules.
    /// </summary>
    public bool UseFunctionObjectAsThis { get; }

    public JsFunction(Func<JsValue, JsValue[], JsValue> native, JsScope closureScope,
                      string? name = null, bool useFunctionObjectAsThis = false,
                      int? length = null)
    {
        Native = native ?? throw new ArgumentNullException(nameof(native));
        ClosureScope = closureScope ?? throw new ArgumentNullException(nameof(closureScope));
        Prototype = JsInterpreter.FunctionPrototype;
        Params = Array.Empty<string>();
        Body = null;
        Name = name;
        UseFunctionObjectAsThis = useFunctionObjectAsThis;
        Class = "Function";
        Set("length", JsValue.From(length ?? Params.Count));
        if (name != null) Set("name", JsValue.From(name));
        MarkInstancePropsDontEnum();
    }

    public JsFunction(FunctionExpr body, IReadOnlyList<string> @params, JsScope closureScope, string? name = null)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Params = @params ?? throw new ArgumentNullException(nameof(@params));
        ClosureScope = closureScope ?? throw new ArgumentNullException(nameof(closureScope));
        Prototype = JsInterpreter.FunctionPrototype;
        Native = null;
        Name = name ?? body.Id?.Name;
        Class = "Function";
        Set("length", JsValue.From(Params.Count));
        Set("name", JsValue.From(Name ?? ""));
        var instancePrototype = new JsObject
        {
            Class = "Object",
            Prototype = JsInterpreter.ObjectPrototype
        };
        instancePrototype.Set("constructor", JsValue.FromFunction(this));
        Set("prototype", JsValue.FromObject(instancePrototype));
        MarkInstancePropsDontEnum();
    }

    /// <summary>length/name/prototype properties are DontEnum (§13/§15.3.5);
    /// length is additionally ReadOnly+DontDelete (§15.3.5.1) — assigning
    /// fn.length = 0 changes nothing.</summary>
    private void MarkInstancePropsDontEnum()
    {
        Attrs ??= new System.Collections.Generic.Dictionary<string, PropAttr>();
        Attrs["length"] = PropAttr.Builtin;
        Attrs["name"] = PropAttr.DontEnum;
        Attrs["prototype"] = PropAttr.DontEnum;
    }
}