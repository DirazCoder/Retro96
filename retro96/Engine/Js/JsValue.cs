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

    public static JsValue From(bool v)      => new() { Type = JsType.Boolean, _boolValue = v };
    public static JsValue From(double v)    => new() { Type = JsType.Number,   _numberValue = v };
    public static JsValue From(int v)       => new() { Type = JsType.Number,   _numberValue = v };
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
        if (obj.TryConvertToPrimitive(preferString || obj.Class == "Date", out var primitive))
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

        if (a < 1e21 && a == Math.Floor(a))
        {
            // Integral — print as plain digits (decimal keeps every digit
            // of a double integral value exactly up to ~7.9e28).
            string digits = ((decimal)a).ToString(CultureInfo.InvariantCulture);
            return neg ? "-" + digits : digits;
        }

        // Round-trip shortest representation, normalised to JS exponent form
        string s = a.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('E'))
            return neg ? "-" + s : s;

        // "1.05E+21" → "1.05e+21", "1E-07" → "1e-7"
        int e = s.IndexOf('E');
        string mantissa = s[..e];
        string expRaw   = s[(e + 1)..];
        bool expNeg = expRaw.StartsWith('-');
        string expDigits = expRaw.TrimStart('-', '+').TrimStart('0');
        if (expDigits.Length == 0) expDigits = "0";

        string result = mantissa + "e" + (expNeg ? "-" : "+") + expDigits;
        return neg ? "-" + result : result;
    }

    /// <summary>
    /// String-to-number per JS 1.1: whitespace-trimmed, hex with 0x, octal
    /// for 0-prefixed all-digit strings (falling back to decimal when the
    /// digits contain 8 or 9), "Infinity" accepted, otherwise NaN.
    /// </summary>
    public static double StringToNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0.0;
        string t = s.Trim();
        if (t.Length == 0) return 0.0;

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            string hex = t[2..];
            if (hex.Length == 0) return double.NaN;
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
                if (val > 0xFFFFFFFFFFFFL) return double.NaN;  // absurd for era scripts
            }
            return val;
        }

        if (t == "Infinity" || t == "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;

        // Octal: "0..." all digits. "08"/"09" were decimal in Navigator.
        if (t.Length > 1 && t[0] == '0' && AllDigits(t))
        {
            bool has89 = false;
            foreach (char c in t)
                if (c == '8' || c == '9') { has89 = true; break; }

            if (!has89)
            {
                long val = 0;
                foreach (char c in t[1..]) val = val * 8 + (c - '0');
                return val;
            }
            // 8/9 present → fall through to decimal parse
        }

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
    /// <summary>
    /// Internal class tag ("Array", "Date", "Error", "" for plain) used by
    /// stringification and type checks.
    /// </summary>
    public string Class { get; set; } = "";

    public Dictionary<string, JsValue> Properties { get; } = new(StringComparer.Ordinal);
    public JsObject? Prototype { get; set; }
    internal Func<JsObject, bool, JsValue>? PrimitiveConverter { get; set; }

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
        if (Class == "Array")
        {
            if (name == "length")
            {
                double newLength = value.ToNumber();
                if (double.IsNaN(newLength) || double.IsInfinity(newLength) ||
                    newLength < 0 || newLength > uint.MaxValue ||
                    newLength != Math.Truncate(newLength))
                    throw new JsRangeErrorException("Invalid array length");

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

    public virtual bool Delete(string name) => Properties.Remove(name);

    /// <summary>
    /// Snapshot of the own enumerable keys — for-in bodies may DELETE
    /// properties while iterating, which threw InvalidOperationException
    /// when this was a live Dictionary view.
    /// </summary>
    public IEnumerable<string> OwnEnumerableKeys() => Properties.Keys.ToArray();
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
                      string? name = null, bool useFunctionObjectAsThis = false)
    {
        Native = native ?? throw new ArgumentNullException(nameof(native));
        ClosureScope = closureScope ?? throw new ArgumentNullException(nameof(closureScope));
        Prototype = JsInterpreter.FunctionPrototype;
        Params = Array.Empty<string>();
        Body = null;
        Name = name;
        UseFunctionObjectAsThis = useFunctionObjectAsThis;
        Class = "Function";
        Set("length", JsValue.From(Params.Count));
        if (name != null) Set("name", JsValue.From(name));
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
    }
}