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

public class JsValue
{
    public JsType Type { get; internal set; }
    
    private double _numberValue;
    private string? _stringValue;
    private bool _boolValue;
    private JsObject? _objectValue;
    private JsFunction? _functionValue;
    
    public static JsValue Undefined { get; } = new JsValue { Type = JsType.Undefined };
    public static JsValue Null      { get; } = new JsValue { Type = JsType.Null };
    
    public static JsValue From(bool v)
    {
        return new JsValue { Type = JsType.Boolean, _boolValue = v };
    }
    
    public static JsValue From(double v)
    {
        return new JsValue { Type = JsType.Number, _numberValue = v };
    }
    
    public static JsValue From(string v)
    {
        if (v == null) throw new ArgumentNullException(nameof(v));
        return new JsValue { Type = JsType.String, _stringValue = v };
    }
    
    public static JsValue FromObject(JsObject o)
    {
        if (o == null) throw new ArgumentNullException(nameof(o));
        return new JsValue { Type = JsType.Object, _objectValue = o };
    }
    
    public static JsValue FromFunction(JsFunction f)
    {
        if (f == null) throw new ArgumentNullException(nameof(f));
        return new JsValue { Type = JsType.Function, _functionValue = f };
    }
    
    public double GetNumber()
    {
        if (Type != JsType.Number) throw new InvalidOperationException("JsValue is not a Number");
        return _numberValue;
    }
    
    public string GetString()
    {
        if (Type != JsType.String) throw new InvalidOperationException("JsValue is not a String");
        return _stringValue!;
    }
    
    public bool GetBool()
    {
        if (Type != JsType.Boolean) throw new InvalidOperationException("JsValue is not a Boolean");
        return _boolValue;
    }
    
    public JsObject GetObject()
    {
        if (Type != JsType.Object) throw new InvalidOperationException("JsValue is not an Object");
        return _objectValue ?? throw new InvalidOperationException("Object value is null");
    }
    
    public JsFunction GetFunction()
    {
        if (Type != JsType.Function) throw new InvalidOperationException("JsValue is not a Function");
        return _functionValue ?? throw new InvalidOperationException("Function value is null");
    }
    
    public double ToNumber()
    {
        return Type switch
        {
            JsType.Undefined => double.NaN,
            JsType.Null => 0.0,
            JsType.Boolean => _boolValue ? 1.0 : 0.0,
            JsType.Number => _numberValue,
            JsType.String => StringToNumber(_stringValue!),
            JsType.Object => ToPrimitive().ToNumber(),
            JsType.Function => ToPrimitive().ToNumber(),
            _ => throw new InvalidOperationException($"Unknown type {Type}")
        };
    }
    
    public string ToJsString()
    {
        return Type switch
        {
            JsType.Undefined => "undefined",
            JsType.Null => "null",
            JsType.Boolean => _boolValue ? "true" : "false",
            JsType.Number => NumberToString(_numberValue),
            JsType.String => _stringValue!,
            JsType.Object => ToPrimitive().ToJsString(),
            JsType.Function => ToPrimitive().ToJsString(),
            _ => throw new InvalidOperationException($"Unknown type {Type}")
        };
    }
    
    public bool ToBoolean()
    {
        return Type switch
        {
            JsType.Undefined => false,
            JsType.Null => false,
            JsType.Boolean => _boolValue,
            JsType.Number => !(_numberValue == 0.0 || double.IsNaN(_numberValue)),
            JsType.String => !string.IsNullOrEmpty(_stringValue),
            JsType.Object or JsType.Function => true,
            _ => throw new InvalidOperationException($"Unknown type {Type}")
        };
    }
    
    public JsObject ToObject()
    {
        return Type switch
        {
            JsType.Object => _objectValue ?? throw new InvalidOperationException("Object value is null"),
            JsType.Function => _functionValue ?? throw new InvalidOperationException("Function value is null"),
            JsType.Undefined or JsType.Null => throw new InvalidOperationException($"Cannot convert {Type} to Object"),
            JsType.Boolean => WrapPrimitiveInObject(),
            JsType.Number => WrapPrimitiveInObject(),
            JsType.String => WrapPrimitiveInObject(),
            _ => throw new InvalidOperationException($"Unknown type {Type}")
        };
    }
    
    private JsObject WrapPrimitiveInObject()
    {
        var obj = new JsObject();
        obj.Set("value", this);
        return obj;
    }
    
    public bool AbstractEquals(JsValue other)
    {
        if (other == null) throw new ArgumentNullException(nameof(other));
        
        if (Type == other.Type)
            return StrictEquals(other);
        
        if ((Type == JsType.Null && other.Type == JsType.Undefined) ||
            (Type == JsType.Undefined && other.Type == JsType.Null))
            return true;
        
        if (Type == JsType.Boolean)
            return From(ToNumber()).AbstractEquals(other);
        if (other.Type == JsType.Boolean)
            return AbstractEquals(From(other.ToNumber()));
        
        if (Type == JsType.Number && other.Type == JsType.String)
            return AbstractEquals(From(other.ToNumber()));
        if (Type == JsType.String && other.Type == JsType.Number)
            return From(ToNumber()).AbstractEquals(other);
        
        if ((Type == JsType.Object || Type == JsType.Function) && IsPrimitive(other.Type))
            return ToPrimitive().AbstractEquals(other);
        if (IsPrimitive(Type) && (other.Type == JsType.Object || other.Type == JsType.Function))
            return AbstractEquals(other.ToPrimitive());
        
        if ((Type == JsType.Object || Type == JsType.Function) && (other.Type == JsType.Object || other.Type == JsType.Function))
            return ReferenceEquals(_objectValue ?? (object?)_functionValue, other._objectValue ?? (object?)other._functionValue);
        
        return false;
    }
    
    private static bool IsPrimitive(JsType type)
    {
        return type is JsType.Undefined or JsType.Null or JsType.Boolean or JsType.Number or JsType.String;
    }
    
    public bool StrictEquals(JsValue other)
    {
        if (other == null) throw new ArgumentNullException(nameof(other));
        if (Type != other.Type) return false;
        
        return Type switch
        {
            JsType.Undefined => true,
            JsType.Null => true,
            JsType.Boolean => _boolValue == other._boolValue,
            JsType.Number => NumberStrictEquals(_numberValue, other._numberValue),
            JsType.String => _stringValue == other._stringValue,
            JsType.Object => ReferenceEquals(_objectValue, other._objectValue),
            JsType.Function => ReferenceEquals(_functionValue, other._functionValue),
            _ => false
        };
    }
    
    private static bool NumberStrictEquals(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return false;
        return a == b;
    }
    
    private static string NumberToString(double n)
    {
        if (double.IsNaN(n)) return "NaN";
        if (double.IsPositiveInfinity(n)) return "Infinity";
        if (double.IsNegativeInfinity(n)) return "-Infinity";
        
        if (n == 0.0 && double.IsNegative(n))
            return "0";
        
        if (n % 1 == 0)
        {
            if (Math.Abs(n) < 1e15)
                return ((long)n).ToString();
            else
                return n.ToString("e", System.Globalization.CultureInfo.InvariantCulture).Replace("E", "e");
        }
        else
        {
            if (Math.Abs(n) >= 1e15 || (Math.Abs(n) < 1e-6 && n != 0))
                return n.ToString("e", System.Globalization.CultureInfo.InvariantCulture).Replace("E", "e");
            else
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    
    private static double StringToNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0.0;
        
        var trimmed = s.Trim();
        
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Length == 2) return double.NaN;
            if (int.TryParse(trimmed.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var hex))
                return hex;
            return double.NaN;
        }
        
        if (trimmed.StartsWith("0") && trimmed.Length > 1 && trimmed.All(char.IsDigit))
        {
            try
            {
                return Convert.ToInt32(trimmed, 8);
            }
            catch
            {
                return double.NaN;
            }
        }
        
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num))
            return num;
        
        return double.NaN;
    }
    
    private JsValue ToPrimitive()
    {
        return Type switch
        {
            JsType.Object => JsValue.From("[object Object]"),
            JsType.Function => JsValue.From("function() { ... }"),
            _ => this
        };
    }
}

public class JsObject
{
    public Dictionary<string, JsValue> Properties { get; } = new(StringComparer.Ordinal);
    public JsObject? Prototype { get; set; }
    
    public JsValue Get(string name)
    {
        if (Properties.TryGetValue(name, out var value))
            return value;
        if (Prototype != null)
            return Prototype.Get(name);
        return JsValue.Undefined;
    }
    
    public void Set(string name, JsValue value)
    {
        Properties[name] = value;
    }
    
    public bool Has(string name)
    {
        if (Properties.ContainsKey(name))
            return true;
        if (Prototype != null)
            return Prototype.Has(name);
        return false;
    }
    
    public bool HasOwn(string name)
    {
        return Properties.ContainsKey(name);
    }
    
    public IEnumerable<string> OwnEnumerableKeys()
    {
        return Properties.Keys;
    }
}

public class JsFunction : JsObject
{
    public IReadOnlyList<string> Params { get; }
    public FunctionExpr? Body { get; }
    public Func<JsValue, JsValue[], JsValue>? Native { get; }
    public JsScope ClosureScope { get; }
    
    public JsFunction(Func<JsValue, JsValue[], JsValue> native, JsScope closureScope, string? name = null)
    {
        Native = native ?? throw new ArgumentNullException(nameof(native));
        ClosureScope = closureScope ?? throw new ArgumentNullException(nameof(closureScope));
        Params = Array.Empty<string>();
        Body = null;
        if (name != null) Set("name", JsValue.From(name));
    }
    
    public JsFunction(FunctionExpr body, IReadOnlyList<string> @params, JsScope closureScope)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Params = @params ?? throw new ArgumentNullException(nameof(@params));
        ClosureScope = closureScope ?? throw new ArgumentNullException(nameof(closureScope));
        Native = null;
        if (body.Id != null) Set("name", JsValue.From(body.Id.Name));
    }
}