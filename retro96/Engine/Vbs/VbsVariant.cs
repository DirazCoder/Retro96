using System;
using System.Globalization;

namespace Retro96.Engine.Vbs;

public enum VbVarType
{
    Empty = 0, Null = 1, Integer = 2, Long = 3, Single = 4, Double = 5,
    Currency = 6, Date = 7, String = 8, Object = 9, Error = 10, Boolean = 11,
    Variant = 12, Array = 8192, Byte = 17
}

/// <summary>
/// The single VBScript Variant type. One subtype + one boxed payload.
/// All coercion/Null/Empty semantics live here; see VbsOps for the operators.
/// </summary>
public readonly struct VbsVariant
{
    public VbVarType Type { get; }
    private readonly object? _box;

    private VbsVariant(VbVarType type, object? box)
    {
        Type = type;
        _box = box;
    }

    public static readonly VbsVariant Empty = default;
    public static readonly VbsVariant Null = new(VbVarType.Null, null);
    public static readonly VbsVariant Nothing = new(VbVarType.Object, null);

    // ── Factories ──────────────────────────────────────────────────────────

    public static VbsVariant Of(bool v) => new(VbVarType.Boolean, v);
    public static VbsVariant Of(byte v) => new(VbVarType.Byte, v);
    public static VbsVariant Of(short v) => new(VbVarType.Integer, v);
    public static VbsVariant Of(int v) => FromLong(v);
    public static VbsVariant Of(long v) => FromLong(v);
    public static VbsVariant Of(float v) => new(VbVarType.Single, v);
    public static VbsVariant Of(double v) => new(VbVarType.Double, v);
    public static VbsVariant Of(decimal v) => new(VbVarType.Currency, v);
    public static VbsVariant Of(string v) => new(VbVarType.String, v ?? "");
    public static VbsVariant Of(DateTime v) => new(VbVarType.Date, v);
    public static VbsVariant Of(IVbsDispatchObject? v) => new(VbVarType.Object, v);
    public static VbsVariant Of(VbsArray v) => new(VbVarType.Array, v);

    /// <summary>Integer → Integer subtype if it fits, else Long; beyond Long → Overflow.</summary>
    public static VbsVariant FromLong(long v)
    {
        if (v is >= short.MinValue and <= short.MaxValue) return new(VbVarType.Integer, (short)v);
        if (v is >= int.MinValue and <= int.MaxValue) return new(VbVarType.Long, (int)v);
        throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
    }

    // ── Typed getters (internal fast paths) ───────────────────────────────

    public bool AsBoolean() => Type == VbVarType.Boolean && _box is bool b ? b
        : throw new InvalidOperationException($"Variant is not a Boolean ({Type}).");
    public byte AsByte() => Type == VbVarType.Byte && _box is byte b ? b
        : throw new InvalidOperationException($"Variant is not a Byte ({Type}).");
    public short AsInt16() => Type == VbVarType.Integer && _box is short s ? s
        : throw new InvalidOperationException($"Variant is not an Integer ({Type}).");
    public int AsInt32() => Type == VbVarType.Long && _box is int i ? i
        : throw new InvalidOperationException($"Variant is not a Long ({Type}).");
    public float AsSingle() => Type == VbVarType.Single && _box is float f ? f
        : throw new InvalidOperationException($"Variant is not a Single ({Type}).");
    public double AsDouble() => Type == VbVarType.Double && _box is double d ? d
        : throw new InvalidOperationException($"Variant is not a Double ({Type}).");
    public decimal AsDecimal() => Type == VbVarType.Currency && _box is decimal m ? m
        : throw new InvalidOperationException($"Variant is not a Currency ({Type}).");
    public DateTime AsDateTime() => Type == VbVarType.Date && _box is DateTime d ? d
        : throw new InvalidOperationException($"Variant is not a Date ({Type}).");
    public string AsString() => Type == VbVarType.String && _box is string s ? s
        : throw new InvalidOperationException($"Variant is not a String ({Type}).");
    public IVbsDispatchObject? AsObject() => Type == VbVarType.Object
        ? (IVbsDispatchObject?)_box
        : throw new InvalidOperationException($"Variant is not an Object ({Type}).");
    public VbsArray AsArray() => Type == VbVarType.Array && _box is VbsArray a ? a
        : throw new InvalidOperationException($"Variant is not an Array ({Type}).");

    // ── Conversions (VBScript coercion rules) ─────────────────────────────

    /// <summary>Object → its default property, if any.</summary>
    public bool TryDefault(out VbsVariant value)
    {
        value = default;
        if (Type != VbVarType.Object) return false;
        var obj = (IVbsDispatchObject?)_box;
        return obj != null && obj.TryGetDefault(out value);
    }

    /// <summary>Numeric coercion for arithmetic. Empty→0, Boolean→-1/0,
    /// Date→OLE date, String→parsed number, Null→error 94, object→default.</summary>
    public double ToDoubleMath()
    {
        switch (Type)
        {
            case VbVarType.Empty: return 0;
            case VbVarType.Boolean: return (bool)_box! ? -1 : 0;
            case VbVarType.Byte: return (byte)_box!;
            case VbVarType.Integer: return (short)_box!;
            case VbVarType.Long: return (int)_box!;
            case VbVarType.Single: return (float)_box!;
            case VbVarType.Double: return (double)_box!;
            case VbVarType.Currency: return (double)(decimal)_box!;
            case VbVarType.Date: return ((DateTime)_box!).ToOADate();
            case VbVarType.String:
                if (TryParseNumeric((string)_box!, out double d)) return d;
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                    $"Type mismatch: '{(string)_box!}'");
            case VbVarType.Object:
                if (TryDefault(out var dv)) return dv.ToDoubleMath();
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    "Object doesn't support this property or method");
            default:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        }
    }

    /// <summary>Currency-context numeric coercion.</summary>
    public decimal AsDecimalMath() =>
        Type == VbVarType.Currency ? (decimal)_box! : (decimal)ToDoubleMath();

    /// <summary>CLng-style conversion: banker's rounding, range-checked.</summary>
    public long ToLongMath()
    {
        switch (Type)
        {
            case VbVarType.Empty: return 0;
            case VbVarType.Boolean: return (bool)_box! ? -1 : 0;
            case VbVarType.Byte: return (byte)_box!;
            case VbVarType.Integer: return (short)_box!;
            case VbVarType.Long: return (int)_box!;
        }
        double d = ToDoubleMath();
        if (double.IsNaN(d))
            throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        double r = Math.Round(d, MidpointRounding.ToEven);
        if (r < long.MinValue || r > long.MaxValue)
            throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
        return (long)r;
    }

    /// <summary>Exact value for the integral subtypes only.</summary>
    internal long IntegralValue() => Type switch
    {
        VbVarType.Empty => 0,
        VbVarType.Boolean => (bool)_box! ? -1 : 0,
        VbVarType.Byte => (byte)_box!,
        VbVarType.Integer => (short)_box!,
        VbVarType.Long => (int)_box!,
        _ => throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch")
    };

    internal bool IsIntegralSubtype => Type is VbVarType.Empty or VbVarType.Boolean
        or VbVarType.Byte or VbVarType.Integer or VbVarType.Long;

    public VbsVariant ToIntVariant()
    {
        if (Type == VbVarType.Integer) return this;
        long v = ToLongMath();
        if (v is < short.MinValue or > short.MaxValue)
            throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
        return Of((short)v);
    }

    public VbsVariant ToLongVariant()
    {
        if (Type == VbVarType.Long) return this;
        long v = ToLongMath();
        if (v is < int.MinValue or > int.MaxValue)
            throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
        return Of((int)v);
    }

    public VbsVariant ToByteVariant()
    {
        if (Type == VbVarType.Byte) return this;
        long v = ToLongMath();
        if (v is < 0 or > byte.MaxValue)
            throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
        return Of((byte)v);
    }

    public VbsVariant ToSingleVariant()
    {
        if (Type == VbVarType.Single) return this;
        double d = ToDoubleMath();
        float f = (float)d;
        if (float.IsInfinity(f) || float.IsNaN(f))
            throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
        return Of(f);
    }

    public VbsVariant ToDoubleVariant() =>
        Type == VbVarType.Double ? this : Of(ToDoubleMath());

    public VbsVariant ToCurrencyVariant()
    {
        if (Type == VbVarType.Currency) return this;
        double d = ToDoubleMath();
        if (Math.Abs(d) > 922_337_203_685_477.0)
            throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
        return Of(Math.Round((decimal)d, 4, MidpointRounding.ToEven));
    }

    public bool ToBoolVariant()
    {
        switch (Type)
        {
            case VbVarType.Boolean: return (bool)_box!;
            case VbVarType.Empty: return false;
            case VbVarType.Null:
                throw new VbsRuntimeException(VbsErrorNumbers.InvalidUseOfNull,
                    "Invalid use of Null");
            case VbVarType.Byte:
            case VbVarType.Integer:
            case VbVarType.Long:
            case VbVarType.Single:
            case VbVarType.Double:
            case VbVarType.Currency:
            case VbVarType.Date:
                return ToDoubleMath() != 0;
            case VbVarType.String:
            {
                string s = (string)_box!;
                if (string.Equals(s, "True", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "False", StringComparison.OrdinalIgnoreCase)) return false;
                if (TryParseNumeric(s, out double d)) return d != 0;
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                    $"Type mismatch: '{s}'");
            }
            case VbVarType.Object:
                if (TryDefault(out var dv)) return dv.ToBoolVariant();
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
            default:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        }
    }

    /// <summary>Truthiness for If/Do/While: Null is FALSE (never an error).</summary>
    public bool IsConditionTrue()
    {
        if (Type is VbVarType.Null or VbVarType.Empty) return false;
        return ToBoolVariant();
    }

    public string ToStringVariant()
    {
        switch (Type)
        {
            case VbVarType.Empty: return "";
            case VbVarType.Null:
                throw new VbsRuntimeException(VbsErrorNumbers.InvalidUseOfNull,
                    "Invalid use of Null");
            case VbVarType.Boolean: return (bool)_box! ? "True" : "False";
            case VbVarType.Byte: return ((byte)_box!).ToString(CultureInfo.InvariantCulture);
            case VbVarType.Integer: return ((short)_box!).ToString(CultureInfo.InvariantCulture);
            case VbVarType.Long: return ((int)_box!).ToString(CultureInfo.InvariantCulture);
            case VbVarType.Single: return FormatNumberClassic((float)_box!);
            case VbVarType.Double: return FormatNumberClassic((double)_box!);
            case VbVarType.Currency:
                return ((decimal)_box!).ToString("0.####", CultureInfo.InvariantCulture);
            case VbVarType.Date: return VbsDateUtil.FormatDateCStr((DateTime)_box!);
            case VbVarType.String: return (string)_box!;
            case VbVarType.Object:
                if (TryDefault(out var dv)) return dv.ToStringVariant();
                throw new VbsRuntimeException(VbsErrorNumbers.ObjectDoesntSupport,
                    "Object doesn't support this property or method");
            default:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        }
    }

    public VbsVariant ToDateVariant()
    {
        switch (Type)
        {
            case VbVarType.Date: return this;
            case VbVarType.String:
                if (VbsDateUtil.TryParseDate((string)_box!, out var dt)) return Of(dt);
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch,
                    $"Type mismatch: '{(string)_box!}'");
            case VbVarType.Byte:
            case VbVarType.Integer:
            case VbVarType.Long:
            case VbVarType.Single:
            case VbVarType.Double:
            case VbVarType.Currency:
            case VbVarType.Empty:
            {
                double d = ToDoubleMath();
                if (double.IsNaN(d))
                    throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
                try { return Of(DateTime.FromOADate(d)); }
                catch (ArgumentException)
                { throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch"); }
            }
            case VbVarType.Object:
                if (TryDefault(out var dv)) return dv.ToDateVariant();
                goto default;
            default:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
        }
    }

    // ── Type information ──────────────────────────────────────────────────

    public string TypeNameOf() => Type switch
    {
        VbVarType.Empty => "Empty",
        VbVarType.Null => "Null",
        VbVarType.Byte => "Byte",
        VbVarType.Integer => "Integer",
        VbVarType.Long => "Long",
        VbVarType.Single => "Single",
        VbVarType.Double => "Double",
        VbVarType.Currency => "Currency",
        VbVarType.Date => "Date",
        VbVarType.Boolean => "Boolean",
        VbVarType.String => "String",
        VbVarType.Object => AsObject()?.VbsTypeName ?? "Nothing",
        VbVarType.Array => "Variant()",
        _ => "Error()"
    };

    public int VarTypeOf() =>
        Type == VbVarType.Array ? (int)VbVarType.Array + (int)VbVarType.Variant : (int)Type;

    public bool IsNumericVariant() => Type switch
    {
        VbVarType.Byte or VbVarType.Integer or VbVarType.Long or VbVarType.Single
            or VbVarType.Double or VbVarType.Currency or VbVarType.Boolean
            or VbVarType.Empty => true,
        VbVarType.String => TryParseNumeric((string)_box!, out _),
        _ => false
    };

    public bool IsDateVariant() => Type switch
    {
        VbVarType.Date => true,
        VbVarType.String => VbsDateUtil.TryParseDate((string)_box!, out _),
        _ => false
    };

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Classic number formatting: integral values in full digits,
    /// otherwise shortest round-trip with JS-style exponent normalization.</summary>
    public static string FormatNumberClassic(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return "0";

        bool neg = d < 0;
        double a = Math.Abs(d);
        if (a < 1e15 && a == Math.Floor(a))
        {
            string digits = ((decimal)a).ToString(CultureInfo.InvariantCulture);
            return neg ? "-" + digits : digits;
        }

        string s = a.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('E'))
            return neg ? "-" + s : s;

        int e = s.IndexOf('E');
        string mantissa = s[..e];
        string expRaw = s[(e + 1)..];
        bool expNeg = expRaw.StartsWith('-');
        string expDigits = expRaw.TrimStart('-', '+').TrimStart('0');
        if (expDigits.Length == 0) expDigits = "0";
        string result = mantissa + "e" + (expNeg ? "-" : "+") + expDigits;
        return neg ? "-" + result : result;
    }

    /// <summary>String → number. Accepts &amp;H / &amp;O literals; whitespace-trimmed.</summary>
    public static bool TryParseNumeric(string s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s.Trim();

        if (t.Length > 2 && (t.StartsWith("&H", StringComparison.OrdinalIgnoreCase)))
        {
            long v = 0;
            foreach (char c in t[2..])
            {
                int d = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => -1
                };
                if (d < 0) return false;
                try { v = checked(v * 16 + d); }
                catch (OverflowException) { return false; }
            }
            value = v;
            return true;
        }
        if (t.Length > 2 && (t.StartsWith("&O", StringComparison.OrdinalIgnoreCase)))
        {
            long v = 0;
            foreach (char c in t[2..])
            {
                if (c is < '0' or > '7') return false;
                v = v * 8 + (c - '0');
            }
            value = v;
            return true;
        }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// All VBScript binary/unary operator semantics. Type selection rules:
/// Currency + Currency → Currency; any Double or String operand → Double;
/// any Single → Single; otherwise checked Long arithmetic with Integer/Long
/// result promotion. Null propagates through everything. Date arithmetic:
/// Date ± number → Date (days); Date − Date → Double (days); Date + Date → 13.
/// </summary>
public static class VbsOps
{
    public static VbsVariant Negate(VbsVariant v)
    {
        if (v.Type == VbVarType.Null) return VbsVariant.Null;
        switch (v.Type)
        {
            case VbVarType.Currency: return VbsVariant.Of(-v.AsDecimal());
            case VbVarType.Single: return VbsVariant.Of(-v.AsSingle());
            case VbVarType.Double: return VbsVariant.Of(-v.AsDouble());
            case VbVarType.Date:
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
            case VbVarType.Byte:
            case VbVarType.Integer:
            case VbVarType.Long:
            case VbVarType.Boolean:
            case VbVarType.Empty:
            {
                long n;
                try { n = checked(-v.IntegralValue()); }
                catch (OverflowException)
                { throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow"); }
                return FromLong(n);
            }
            default:
                return VbsVariant.Of(-v.ToDoubleMath());   // String etc. → Double
        }
    }

    public static VbsVariant Add(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;

        if (l.Type == VbVarType.Date || r.Type == VbVarType.Date)
        {
            if (l.Type == VbVarType.Date && r.Type == VbVarType.Date)
                throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");
            DateTime d = l.Type == VbVarType.Date ? l.AsDateTime() : r.AsDateTime();
            double days = (l.Type == VbVarType.Date ? r : l).ToDoubleMath();
            try { return VbsVariant.Of(d.AddDays(days)); }
            catch (ArgumentOutOfRangeException)
            { throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow"); }
        }

        // "+" concatenates when both sides are strings (Empty acts as "").
        if (l.Type == VbVarType.String && r.Type == VbVarType.String)
            return VbsVariant.Of(l.AsString() + r.AsString());
        if (l.Type == VbVarType.String && r.Type == VbVarType.Empty)
            return VbsVariant.Of(l.AsString());
        if (l.Type == VbVarType.Empty && r.Type == VbVarType.String)
            return VbsVariant.Of(r.AsString());

        if (l.Type == VbVarType.Currency || r.Type == VbVarType.Currency)
            return Cur(l.AsDecimalMath() + r.AsDecimalMath());
        if (l.Type == VbVarType.Double || r.Type == VbVarType.Double ||
            l.Type == VbVarType.String || r.Type == VbVarType.String)
            return VbsVariant.Of(l.ToDoubleMath() + r.ToDoubleMath());
        if (l.Type == VbVarType.Single || r.Type == VbVarType.Single)
            return VbsVariant.Of((float)l.ToDoubleMath() + (float)r.ToDoubleMath());
        return IntegralOp(l, r, (a, b) => a + b);
    }

    public static VbsVariant Subtract(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;

        if (l.Type == VbVarType.Date && r.Type == VbVarType.Date)
            return VbsVariant.Of(l.AsDateTime().ToOADate() - r.AsDateTime().ToOADate());
        if (l.Type == VbVarType.Date)
            return VbsVariant.Of(l.AsDateTime().AddDays(-r.ToDoubleMath()));
        if (r.Type == VbVarType.Date)
            throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch");

        if (l.Type == VbVarType.Currency || r.Type == VbVarType.Currency)
            return Cur(l.AsDecimalMath() - r.AsDecimalMath());
        if (l.Type == VbVarType.Double || r.Type == VbVarType.Double ||
            l.Type == VbVarType.String || r.Type == VbVarType.String)
            return VbsVariant.Of(l.ToDoubleMath() - r.ToDoubleMath());
        if (l.Type == VbVarType.Single || r.Type == VbVarType.Single)
            return VbsVariant.Of((float)l.ToDoubleMath() - (float)r.ToDoubleMath());
        return IntegralOp(l, r, (a, b) => a - b);
    }

    public static VbsVariant Multiply(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        if (l.Type == VbVarType.Currency || r.Type == VbVarType.Currency)
            return Cur(l.AsDecimalMath() * r.AsDecimalMath());
        if (l.Type == VbVarType.Double || r.Type == VbVarType.Double ||
            l.Type == VbVarType.String || r.Type == VbVarType.String)
            return VbsVariant.Of(l.ToDoubleMath() * r.ToDoubleMath());
        if (l.Type == VbVarType.Single || r.Type == VbVarType.Single)
            return VbsVariant.Of((float)l.ToDoubleMath() * (float)r.ToDoubleMath());
        return IntegralOp(l, r, (a, b) => a * b);
    }

    /// <summary>" / " — always floating point: Single if both Single-ish,
    /// otherwise Double (4/2 → 2 as Double). Division by zero → error 11.</summary>
    public static VbsVariant Divide(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        if (l.Type == VbVarType.Currency || r.Type == VbVarType.Currency)
        {
            decimal denom = r.AsDecimalMath();
            if (denom == 0)
                throw new VbsRuntimeException(VbsErrorNumbers.DivisionByZero, "Division by zero");
            return Cur(l.AsDecimalMath() / denom);
        }
        if ((l.Type == VbVarType.Single || r.Type == VbVarType.Single) &&
            l.Type is not (VbVarType.Double or VbVarType.String) &&
            r.Type is not (VbVarType.Double or VbVarType.String))
        {
            float denom = (float)r.ToDoubleMath();
            if (denom == 0)
                throw new VbsRuntimeException(VbsErrorNumbers.DivisionByZero, "Division by zero");
            return VbsVariant.Of((float)l.ToDoubleMath() / denom);
        }
        double d = r.ToDoubleMath();
        if (d == 0)
            throw new VbsRuntimeException(VbsErrorNumbers.DivisionByZero, "Division by zero");
        return VbsVariant.Of(l.ToDoubleMath() / d);
    }

    /// <summary>" \\ " — operands rounded (banker's) to Long, integer quotient.</summary>
    public static VbsVariant IntDivide(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        long a = l.ToLongMath(), b = r.ToLongMath();
        if (b == 0)
            throw new VbsRuntimeException(VbsErrorNumbers.DivisionByZero, "Division by zero");
        return FromLong(a / b);
    }

    /// <summary>Mod — operands rounded to Long, remainder keeps the dividend's sign.</summary>
    public static VbsVariant Modulo(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        long a = l.ToLongMath(), b = r.ToLongMath();
        if (b == 0)
            throw new VbsRuntimeException(VbsErrorNumbers.DivisionByZero, "Division by zero");
        return FromLong(a % b);
    }

    public static VbsVariant Power(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        return VbsVariant.Of(Math.Pow(l.ToDoubleMath(), r.ToDoubleMath()));
    }

    /// <summary>"&amp;" — string concatenation; Null propagates; Empty → "".</summary>
    public static VbsVariant Concat(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        return VbsVariant.Of(l.ToStringVariant() + r.ToStringVariant());
    }

    // ── Comparisons ────────────────────────────────────────────────────────

    /// <summary>Text vs numeric comparison rules; any Null operand makes the
    /// RESULT Null (which is False in an If). Empty equals both 0 and "".</summary>
    public static VbsVariant Compare(VbsVariant l, VbsVariant r, string op)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;

        int cmp;
        if (l.Type == VbVarType.String && r.Type == VbVarType.String)
            cmp = string.CompareOrdinal(l.AsString(), r.AsString());
        else if (l.Type == VbVarType.Date && r.Type == VbVarType.Date)
            cmp = DateTime.Compare(l.AsDateTime(), r.AsDateTime());
        else if (l.Type == VbVarType.String && r.Type == VbVarType.Empty)
            cmp = string.CompareOrdinal(l.AsString(), "");
        else if (l.Type == VbVarType.Empty && r.Type == VbVarType.String)
            cmp = string.CompareOrdinal("", r.AsString());
        else
        {
            double a = l.ToDoubleMath();
            double b = r.ToDoubleMath();
            if (double.IsNaN(a) || double.IsNaN(b))
            {
                // NaN: every comparison is false except <>
                return VbsVariant.Of(op == "<>");
            }
            cmp = a.CompareTo(b);
        }

        bool res = op switch
        {
            "=" => cmp == 0,
            "<>" => cmp != 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            _ => throw new VbsRuntimeException(VbsErrorNumbers.TypeMismatch, "Type mismatch")
        };
        return VbsVariant.Of(res);
    }

    /// <summary>Is — object identity. Non-object operand → error 424.</summary>
    public static VbsVariant IsOp(VbsVariant l, VbsVariant r)
    {
        if (l.Type != VbVarType.Object || r.Type != VbVarType.Object)
            throw new VbsRuntimeException(VbsErrorNumbers.ObjectRequired, "Object required");
        return VbsVariant.Of(ReferenceEquals(l.AsObject(), r.AsObject()));
    }

    // ── Logical / bitwise ──────────────────────────────────────────────────

    public static VbsVariant NotOp(VbsVariant v)
    {
        if (v.Type == VbVarType.Null) return VbsVariant.Null;
        if (v.Type == VbVarType.Boolean) return VbsVariant.Of(!v.AsBoolean());
        return FromLong(~v.ToLongMath());
    }

    public static VbsVariant AndOp(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null)
        {
            // Null And False = False; Null And anything-else = Null
            if (l.Type == VbVarType.Boolean && !l.AsBoolean()) return VbsVariant.Of(false);
            if (r.Type == VbVarType.Boolean && !r.AsBoolean()) return VbsVariant.Of(false);
            return VbsVariant.Null;
        }
        if (l.Type == VbVarType.Boolean && r.Type == VbVarType.Boolean)
            return VbsVariant.Of(l.AsBoolean() && r.AsBoolean());
        return FromLong(l.ToLongMath() & r.ToLongMath());
    }

    public static VbsVariant OrOp(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null)
        {
            // Null Or True = True; Null Or anything-else = Null
            if (l.Type == VbVarType.Boolean && l.AsBoolean()) return VbsVariant.Of(true);
            if (r.Type == VbVarType.Boolean && r.AsBoolean()) return VbsVariant.Of(true);
            return VbsVariant.Null;
        }
        if (l.Type == VbVarType.Boolean && r.Type == VbVarType.Boolean)
            return VbsVariant.Of(l.AsBoolean() || r.AsBoolean());
        return FromLong(l.ToLongMath() | r.ToLongMath());
    }

    public static VbsVariant XorOp(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        if (l.Type == VbVarType.Boolean && r.Type == VbVarType.Boolean)
            return VbsVariant.Of(l.AsBoolean() ^ r.AsBoolean());
        return FromLong(l.ToLongMath() ^ r.ToLongMath());
    }

    public static VbsVariant EqvOp(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null) return VbsVariant.Null;
        if (l.Type == VbVarType.Boolean && r.Type == VbVarType.Boolean)
            return VbsVariant.Of(l.AsBoolean() == r.AsBoolean());
        return FromLong(~(l.ToLongMath() ^ r.ToLongMath()));
    }

    public static VbsVariant ImpOp(VbsVariant l, VbsVariant r)
    {
        if (l.Type == VbVarType.Null || r.Type == VbVarType.Null)
        {
            // Null Imp True = True; False Imp Null = True; anything else → Null
            if (r.Type == VbVarType.Boolean && r.AsBoolean()) return VbsVariant.Of(true);
            if (l.Type == VbVarType.Boolean && !l.AsBoolean()) return VbsVariant.Of(true);
            return VbsVariant.Null;
        }
        if (l.Type == VbVarType.Boolean && r.Type == VbVarType.Boolean)
            return VbsVariant.Of(!l.AsBoolean() || r.AsBoolean());
        return FromLong(~l.ToLongMath() | r.ToLongMath());
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static VbsVariant IntegralOp(VbsVariant l, VbsVariant r, Func<long, long, long> op)
    {
        long result;
        try { result = checked(op(l.IntegralValue(), r.IntegralValue())); }
        catch (OverflowException)
        { throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow"); }
        return FromLong(result);
    }

    private static VbsVariant Cur(decimal d)
    {
        try { return VbsVariant.Of(Math.Round(d, 4, MidpointRounding.ToEven)); }
        catch (OverflowException)
        { throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow"); }
    }

    private static VbsVariant FromLong(long v)
    {
        if (v is >= short.MinValue and <= short.MaxValue) return VbsVariant.Of((short)v);
        if (v is >= int.MinValue and <= int.MaxValue) return VbsVariant.Of((int)v);
        throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
    }
}

/// <summary>Date parsing/formatting shared by the lexer, CDate and DateValue.</summary>
public static class VbsDateUtil
{
    /// <summary>OLE Automation epoch — the "time only" base.</summary>
    public static readonly DateTime TimeBase = new(1899, 12, 30);

    private static readonly string[] _formats =
    {
        "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm", "M/d/yyyy h:mm tt",
        "M/d/yyyy", "M/d/yy", "yyyy-M-d", "MMMM d, yyyy", "MMM d, yyyy",
        "H:mm:ss", "H:mm", "h:mm:ss tt", "h:mm tt"
    };

    public static bool TryParseDate(string s, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s.Trim();
        foreach (string fmt in _formats)
        {
            if (DateTime.TryParseExact(t, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out value))
            {
                if (!fmt.Contains('M') && !fmt.Contains('d') && !fmt.Contains('y'))
                    value = TimeBase.Add(value.TimeOfDay);
                return true;
            }
        }
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>CStr(date): date-only when midnight, else date + time.</summary>
    public static string FormatDateCStr(DateTime d)
    {
        if (d.Date == TimeBase.Date)
            return d.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
        if (d.TimeOfDay == TimeSpan.Zero)
            return d.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        return d.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
    }
}