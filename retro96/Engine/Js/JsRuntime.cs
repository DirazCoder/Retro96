using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Js;

/// <summary>
/// The global object model of JavaScript 1.1/1.2: Object/Array/String/
/// Number/Boolean/Math/Date/RegExp/Error constructors with their
/// prototypes, plus the top-level functions (parseInt, parseFloat,
/// isNaN, isFinite, escape, unescape).
///
/// Callback-taking methods (sort/forEach/map/filter), eval, call/apply
/// and the timers live on JsInterpreter.RegisterRuntimeBuiltins — they
/// need a live interpreter to invoke script functions.
/// </summary>
public static class JsRuntime
{
    private static readonly Random _sharedRandom = new();

    public static void PopulateGlobalScope(JsScope globalScope)
    {
        globalScope.Define("undefined", JsValue.Undefined);
        var functionProto = new JsFunction((_, _) => JsValue.Undefined, globalScope);
        JsInterpreter.FunctionPrototype = functionProto;
        var objectProto = RegisterObject(globalScope);
        functionProto.Prototype = objectProto;
        RegisterFunction(globalScope, functionProto, objectProto);
        var arrayProto  = RegisterArray(globalScope, objectProto);
        var stringProto = RegisterString(globalScope, objectProto);
        var numberProto = RegisterNumber(globalScope, objectProto);
        var boolProto   = RegisterBoolean(globalScope, objectProto);
        RegisterMath(globalScope);
        RegisterDate(globalScope, objectProto);
        RegisterRegExp(globalScope, objectProto);
        RegisterError(globalScope, objectProto);
        RegisterGlobalFunctions(globalScope);

        // Expose the prototypes for primitive member access
        // ("abc".length, (12).toString(16))
        JsInterpreter.StringPrototype  = stringProto;
        JsInterpreter.NumberPrototype   = numberProto;
        JsInterpreter.BooleanPrototype = boolProto;
        JsInterpreter.ArrayPrototype    = arrayProto;
        JsInterpreter.ObjectPrototype   = objectProto;

        // ECMA-262 §15: builtin properties carry attribute defaults. Per the
        // corpus's verified era behaviour: prototype METHOD properties are
        // { DontEnum, DontDelete } but assignable (Boolean.prototype.toString
        // = Object.prototype.toString must take effect), while constructor /
        // Math objects and built-in method functions themselves (length,
        // name) are { ReadOnly, DontEnum, DontDelete }. Properties added to
        // these objects LATER (era scripts patching prototypes) are ordinary.
        foreach (string builtin in new[]
        {
            "Object", "Function", "Array", "String", "Number", "Boolean",
            "Date", "RegExp", "Math", "Error", "TypeError", "RangeError",
            "EvalError", "ReferenceError", "SyntaxError", "URIError"
        })
        {
            if (globalScope.Get(builtin) is not { Type: JsType.Object or JsType.Function } bv)
                continue;
            var ctor = bv.GetObjectOrFunction();
            ctor.FreezeBuiltins();                                    // full builtin attrs
            if (ctor.Get("prototype") is { Type: JsType.Object or JsType.Function } pv)
            {
                var proto = pv.GetObjectOrFunction();
                proto.MarkAll(JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete);
                foreach (var method in proto.Properties.Values)
                    if (method.Type is (JsType.Object or JsType.Function))
                        method.GetObjectOrFunction().FreezeBuiltins();
            }
        }
        functionProto.MarkAll(JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete);
        foreach (var method in functionProto.Properties.Values)
            if (method.Type is (JsType.Object or JsType.Function))
                method.GetObjectOrFunction().FreezeBuiltins();
    }

    private static JsValue Fn(JsScope scope, string name,
                              Func<JsValue, JsValue[], JsValue> impl) =>
        JsValue.FromFunction(new JsFunction(impl, scope, name));

    /// <summary>Builtin with an explicit Function.length (ECMA-262 §15).</summary>
    private static JsValue Fn(JsScope scope, string name, int length,
                              Func<JsValue, JsValue[], JsValue> impl) =>
        JsValue.FromFunction(new JsFunction(impl, scope, name, length: length));

    private static double Num(JsValue v) => v.ToNumber();
    private static string Str(JsValue v) => v.ToJsString();
    private static double Arg(JsValue[] a, int i) => i < a.Length ? a[i].ToNumber() : double.NaN;

    private static bool IsNegZero(double d) =>
        d == 0.0 && System.BitConverter.DoubleToInt64Bits(d) < 0;
    private static string ArgStr(JsValue[] a, int i, string def = "") =>
        i < a.Length ? a[i].ToJsString() : def;

    private static Regex CreateJsRegex(string source, RegexOptions options) =>
        new(TranslateToNet(source), options, TimeSpan.FromSeconds(1));

    /// <summary>
    /// ES3 stores the pattern with '/' escaped ("\\/") and an empty pattern
    /// as "(?:)" (§15.10.6.4/§15.10.7.1). .NET rejects the \/ escape, so
    /// translate it back to a bare '/' before compiling — it is a legal
    /// IdentityEscape in JS semantics and matches the same character.
    /// </summary>
    private static string TranslateToNet(string source) =>
        source.Replace("\\\\/", "/");

    /// <summary>
    /// Normalised ES3 RegExp instance shape (§15.10.7): source carries the
    /// '/'-escaped pattern (empty → "(?:)"), the four descriptive flags are
    /// ReadOnly+DontEnum+DontDelete, lastIndex is DontEnum+DontDelete, and
    /// the internal "flags" string is hidden from for-in.
    /// </summary>
    public static void ApplyRegExpShape(JsObject o, string pattern, string flags)
    {
        string stored = pattern.Length == 0 ? "(?:)" : pattern.Replace("/", "\\/");
        // §15.10.6.4: flags print in canonical g,i,m order regardless of how
        // the RegExp was constructed (/test2/ig .toString() is "/test2/gi")
        string canon =
            (flags.Contains('g') ? "g" : "") +
            (flags.Contains('i') ? "i" : "") +
            (flags.Contains('m') ? "m" : "");
        flags = canon;
        o.Attrs?.Remove("source"); o.Attrs?.Remove("flags"); o.Attrs?.Remove("global");
        o.Attrs?.Remove("ignoreCase"); o.Attrs?.Remove("multiline"); o.Attrs?.Remove("lastIndex");
        o.Set("source", JsValue.From(stored));
        o.Set("flags", JsValue.From(flags));
        o.Set("global", JsValue.From(flags.Contains('g')));
        o.Set("ignoreCase", JsValue.From(flags.Contains('i')));
        o.Set("multiline", JsValue.From(flags.Contains('m')));
        o.Set("lastIndex", JsValue.From(0));
        o.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
        o.Attrs["source"] = JsObject.PropAttr.Builtin;
        o.Attrs["flags"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
        o.Attrs["global"] = JsObject.PropAttr.Builtin;
        o.Attrs["ignoreCase"] = JsObject.PropAttr.Builtin;
        o.Attrs["multiline"] = JsObject.PropAttr.Builtin;
        o.Attrs["lastIndex"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
    }

    private static int LengthOf(JsValue self) =>
        self.Type is JsType.Object or JsType.Function &&
        self.GetObjectOrFunction().Get("length") is { Type: JsType.Number } l
            ? (int)l.GetNumber() : 0;

    /// <summary>
    /// Clamps a JS number to a safe int before casting — out-of-range
    /// doubles used to flow through an unspecified (int) conversion and
    /// leave the bounds logic resting on accident.
    /// </summary>
    private static int ToIntSafe(JsValue v)
    {
        double d = v.ToNumber();
        if (double.IsNaN(d)) return 0;
        if (d >= int.MaxValue) return int.MaxValue;
        if (d <= int.MinValue) return int.MinValue;
        return (int)d;
    }

    private static JsObject NewArray(JsScope scope)
    {
        var arr = new JsObject { Class = "Array" };
        arr.Prototype = JsInterpreter.ArrayPrototype;
        arr.Set("length", JsValue.From(0));
        return arr;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Object
    // ─────────────────────────────────────────────────────────────────────

    private static JsObject RegisterObject(JsScope scope)
    {
        var objProto = new JsObject { Class = "Object" };

        objProto.Set("toString", Fn(scope, "toString", (self, args) =>
        {
            string tag = self.Type switch
            {
                JsType.String => "String",
                JsType.Number => "Number",
                JsType.Boolean => "Boolean",
                JsType.Function => "Function",
                JsType.Object => self.GetObject().Class is { Length: > 0 } className
                    ? className : "Object",
                _ => "Object"
            };
            return JsValue.From($"[object {tag}]");
        }));

        objProto.Set("toLocaleString", Fn(scope, "toLocaleString", (self, args) =>
            JsValue.From(self.ToJsString())));

        objProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => self));

        objProto.Set("hasOwnProperty", Fn(scope, "hasOwnProperty", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            if (self.Type is not (JsType.Object or JsType.Function)) return JsValue.From(false);
            string key = args[0].ToJsString();
            if (!self.GetObjectOrFunction().HasOwn(key)) return JsValue.From(false);
            return JsValue.From(key is not ("length" or "name" or "prototype" or "constructor"));
        }));

        objProto.Set("propertyIsEnumerable", Fn(scope, "propertyIsEnumerable", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            if (self.Type is not (JsType.Object or JsType.Function)) return JsValue.From(false);
            return JsValue.From(self.GetObjectOrFunction().HasOwn(args[0].ToJsString()));
        }));

        objProto.Set("isPrototypeOf", Fn(scope, "isPrototypeOf", 1, (self, args) =>
        {
            if (args.Length == 0 ||
                self.Type is not (JsType.Object or JsType.Function) ||
                args[0].Type is not (JsType.Object or JsType.Function))
                return JsValue.From(false);

            var targetPrototype = self.GetObjectOrFunction();
            for (var prototype = args[0].GetObjectOrFunction().Prototype;
                 prototype != null; prototype = prototype.Prototype)
                if (ReferenceEquals(prototype, targetPrototype))
                    return JsValue.From(true);
            return JsValue.From(false);
        }));

        var objectCtor = new JsFunction((self, args) =>
        {
            // §15.2.2.1: objects/functions pass through untouched
            if (args.Length > 0 && args[0].Type is (JsType.Object or JsType.Function))
                return args[0];
            var o = new JsObject { Prototype = objProto };
            if (args.Length > 0)
            {
                // §15.2.2.1: a primitive value becomes a wrapper whose
                // [[Prototype]] is the corresponding primitive prototype, so
                // Object(true).valueOf finds Boolean.prototype.valueOf.
                o.Class = args[0].Type switch
                {
                    JsType.String => "String",
                    JsType.Number => "Number",
                    JsType.Boolean => "Boolean",
                    _ => "Object"
                };
                o.Set("value", args[0]);
                o.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                o.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                o.Prototype = args[0].Type switch
                {
                    JsType.String   => JsInterpreter.StringPrototype,
                    JsType.Number   => JsInterpreter.NumberPrototype,
                    JsType.Boolean  => JsInterpreter.BooleanPrototype,
                    _ => objProto
                };
                if (args[0].Type == JsType.String)
                {
                    // ToObject(string) carries a read-only length (§15.5.5.1)
                    o.SetWritable("length", JsValue.From(args[0].GetString().Length));
                    o.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                    o.Attrs["length"] = JsObject.PropAttr.Builtin;
                }
            }
            return JsValue.FromObject(o);
        }, scope, "Object", length: 1);
        objectCtor.Set("prototype", JsValue.FromObject(objProto));
        objProto.Set("constructor", JsValue.FromFunction(objectCtor));
        scope.Define("Object", JsValue.FromFunction(objectCtor));

        return objProto;
    }

    private static void RegisterFunction(
        JsScope scope, JsObject functionProto, JsObject objectProto)
    {
        var constructor = new JsFunction((self, args) =>
        {
            int parameterCount = Math.Max(0, args.Length - 1);
            string parameters = string.Join(",", args.Take(parameterCount)
                .Select(argument => argument.ToJsString()));
            string body = args.Length > 0 ? args[^1].ToJsString() : "";
            var program = JsParser.Parse($"function anonymous({parameters}){{{body}}}");
            var declaration = program.Body.OfType<FunctionDeclaration>().FirstOrDefault()
                ?? throw new JsInterpreterException("Invalid Function constructor body");
            var functionBody = new FunctionExpr(
                declaration.Id, declaration.Params, declaration.Body);
            var parameterNames = declaration.Params.Select(parameter => parameter.Name).ToArray();
            return JsValue.FromFunction(new JsFunction(
                functionBody, parameterNames, scope, "anonymous"));
        }, scope, "Function", length: 1);
        constructor.Prototype = functionProto;
        constructor.Set("prototype", JsValue.FromFunction((JsFunction)functionProto));
        functionProto.Set("constructor", JsValue.FromFunction(constructor));
        scope.Define("Function", JsValue.FromFunction(constructor));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Array
    // ─────────────────────────────────────────────────────────────────────

    private static JsObject RegisterArray(JsScope scope, JsObject objectProto)
    {
        // Array.prototype chains to Object.prototype — arrays inherit
        // hasOwnProperty/valueOf like everything else
        var arrProto = new JsObject { Class = "Array", Prototype = objectProto };

        arrProto.Set("push", Fn(scope, "push", 1, (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            foreach (var item in args)
                arr.Set((length++).ToString(), item);
            arr.Set("length", JsValue.From(length));
            return JsValue.From(length);
        }));

        arrProto.Set("pop", Fn(scope, "pop", (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            if (length == 0) return JsValue.Undefined;
            length--;
            var last = arr.Get(length.ToString());
            arr.Properties.Remove(length.ToString());
            arr.Set("length", JsValue.From(length));
            return last;
        }));

        arrProto.Set("shift", Fn(scope, "shift", (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            if (length == 0) return JsValue.Undefined;
            var first = arr.Get("0");
            for (int i = 1; i < length; i++)
                arr.Set((i - 1).ToString(), arr.Get(i.ToString()));
            arr.Properties.Remove((length - 1).ToString());
            arr.Set("length", JsValue.From(length - 1));
            return first;
        }));

        arrProto.Set("unshift", Fn(scope, "unshift", 1, (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            for (int i = length - 1; i >= 0; i--)
                arr.Set((i + args.Length).ToString(), arr.Get(i.ToString()));
            for (int i = 0; i < args.Length; i++)
                arr.Set(i.ToString(), args[i]);
            arr.Set("length", JsValue.From(length + args.Length));
            return JsValue.From(length + args.Length);
        }));

        arrProto.Set("reverse", Fn(scope, "reverse", (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            for (int i = 0; i < length / 2; i++)
            {
                string a = i.ToString(), b = (length - 1 - i).ToString();
                var left = arr.Get(a);
                arr.Set(a, arr.Get(b));
                arr.Set(b, left);
            }
            return self;
        }));

        arrProto.Set("join", Fn(scope, "join", 1, (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            string sep = args.Length > 0 ? args[0].ToJsString() : ",";
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(sep);
                var v = arr.Get(i.ToString());
                if (v.Type is not (JsType.Null or JsType.Undefined))
                    sb.Append(v.ToJsString());
            }
            return JsValue.From(sb.ToString());
        }));

        arrProto.Set("toString", Fn(scope, "toString", (self, args) =>
            self.Type is JsType.Object or JsType.Function
                ? arrProto.Get("join").GetFunction().Native!(self, Array.Empty<JsValue>())
                : self));

        arrProto.Set("slice", Fn(scope, "slice", 2, (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            int start = args.Length > 0 ? ToIntSafe(args[0]) : 0;
            int end   = args.Length > 1 ? ToIntSafe(args[1]) : length;

            if (start < 0) start = Math.Max(length + start, 0);
            if (end < 0)   end   = Math.Max(length + end, 0);
            start = Math.Max(start, 0);
            end   = Math.Min(end, length);

            var newArr = NewArray(scope);
            int n = 0;
            for (int i = start; i < end; i++)
                newArr.Set((n++).ToString(), arr.Get(i.ToString()));
            newArr.Set("length", JsValue.From(n));
            return JsValue.FromObject(newArr);
        }));

        arrProto.Set("concat", Fn(scope, "concat", 1, (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            var newArr = NewArray(scope);
            int n = 0;

            for (int i = 0; i < length; i++)
                newArr.Set((n++).ToString(), arr.Get(i.ToString()));

            foreach (var arg in args)
            {
                if (arg.Type is (JsType.Object or JsType.Function) &&
                    arg.GetObjectOrFunction().Class == "Array")
                {
                    var argArr = arg.GetObjectOrFunction();
                    int argLen = LengthOf(arg);
                    for (int i = 0; i < argLen; i++)
                        newArr.Set((n++).ToString(), argArr.Get(i.ToString()));
                }
                else
                {
                    newArr.Set((n++).ToString(), arg);
                }
            }

            newArr.Set("length", JsValue.From(n));
            return JsValue.FromObject(newArr);
        }));

        arrProto.Set("splice", Fn(scope, "splice", 2, (self, args) =>
        {
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            if (args.Length == 0)
                return JsValue.FromObject(NewArray(scope));

            int start = ToIntSafe(args[0]);
            if (start < 0) start = Math.Max(length + start, 0);
            start = Math.Min(start, length);

            int deleteCount = args.Length > 1 && !double.IsNaN(args[1].ToNumber())
                ? ToIntSafe(args[1]) : length - start;
            deleteCount = Math.Clamp(deleteCount, 0, length - start);

            var removed = NewArray(scope);
            int rn = 0;
            for (int i = 0; i < deleteCount; i++)
                removed.Set((rn++).ToString(), arr.Get((start + i).ToString()));
            removed.Set("length", JsValue.From(rn));

            int insertCount = args.Length > 2 ? args.Length - 2 : 0;

            for (int i = start + deleteCount; i < length; i++)
                arr.Set((i - deleteCount + insertCount).ToString(), arr.Get(i.ToString()));

            for (int i = 0; i < insertCount; i++)
                arr.Set((start + i).ToString(), args[2 + i]);

            int newLength = length - deleteCount + insertCount;
            for (int i = newLength; i < length; i++)
                arr.Properties.Remove(i.ToString());
            arr.Set("length", JsValue.From(newLength));

            return JsValue.FromObject(removed);
        }));

        arrProto.Set("indexOf", Fn(scope, "indexOf", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var arr = self.GetObjectOrFunction();
            int length = LengthOf(self);
            int from = args.Length > 1 && !double.IsNaN(args[1].ToNumber())
                ? Math.Max(ToIntSafe(args[1]), 0) : 0;

            for (int i = from; i < length; i++)
                if (arr.Get(i.ToString()).AbstractEquals(args[0]))
                    return JsValue.From(i);
            return JsValue.From(-1);
        }));

        // Strict IE3/JScript 1.0 hides these later ECMAScript Array helpers.
        // Retro96 and Navigator retain the broader runtime implementation.
        if (BrowserRuntime.IsInternetExplorer3)
        {
            arrProto.Properties.Remove("splice");
            arrProto.Properties.Remove("indexOf");
        }

        var arrayCtor = new JsFunction((self, args) =>
        {
            var newArr = NewArray(scope);
            int length = 0;

            if (args.Length == 1 && args[0].Type == JsType.Number)
            {
                // §15.4.2.2: a single Number argument is the array length;
                // it must be a Uint32 (0 … 4294967295) or it's a RangeError.
                double n = args[0].ToNumber();
                if (double.IsNaN(n) || double.IsInfinity(n) || n < 0 || n != Math.Truncate(n) || n > 4294967295)
                    throw new JsRangeErrorException("invalid array length");
                newArr.Set("length", JsValue.From(n));
                return JsValue.FromObject(newArr);
            }
            else
            {
                foreach (var arg in args)
                {
                    newArr.Set((length++).ToString(), arg);
                }
                newArr.Set("length", JsValue.From(length));
            }

            return JsValue.FromObject(newArr);
        }, scope, "Array", length: 1);
        arrayCtor.Set("prototype", JsValue.FromObject(arrProto));
        // §15.4.3.1: Array.prototype.constructor is the Array constructor.
        arrProto.Set("constructor", JsValue.FromFunction(arrayCtor));
        scope.Define("Array", JsValue.FromFunction(arrayCtor));

        return arrProto;
    }

    // ─────────────────────────────────────────────────────────────────────
    // String
    // ─────────────────────────────────────────────────────────────────────

    private static JsObject RegisterString(JsScope scope, JsObject objectProto)
    {
        var strProto = new JsObject { Class = "String", Prototype = objectProto };

        strProto.Set("charAt", Fn(scope, "charAt", 1, (self, args) =>
        {
            var str = self.ToJsString();
            // §15.5.4.4: ToInteger(position); out of range yields ""
            double index = args.Length > 0 ? ToIntegerD(args[0].ToNumber()) : 0;
            if (index < 0 || index >= str.Length) return JsValue.From("");
            return JsValue.From(str[(int)index].ToString());
        }));

        strProto.Set("charCodeAt", Fn(scope, "charCodeAt", 1, (self, args) =>
        {
            var str = self.ToJsString();
            // §15.5.4.5: ToInteger(position); out of range yields NaN
            double index = args.Length > 0 ? ToIntegerD(args[0].ToNumber()) : 0;
            if (index < 0 || index >= str.Length) return JsValue.From(double.NaN);
            return JsValue.From((double)str[(int)index]);
        }));

        strProto.Set("indexOf", Fn(scope, "indexOf", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var str = self.ToJsString();
            var search = args[0].ToJsString();
            int from = args.Length > 1 ? Math.Max((int)args[1].ToNumber(), 0) : 0;
            return JsValue.From(str.IndexOf(search, from, StringComparison.Ordinal));
        }));

        strProto.Set("lastIndexOf", Fn(scope, "lastIndexOf", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var str = self.ToJsString();
            var search = args[0].ToJsString();
            // §15.5.4.8: greatest index ≤ position where the search starts;
            // a missing position is +∞, NaN is 0
            double raw = args.Length > 1 ? args[1].ToNumber() : double.PositiveInfinity;
            double pos = args.Length > 1
                ? (double.IsNaN(raw) ? double.PositiveInfinity : ToIntegerD(raw))
                : double.PositiveInfinity;
            double clamped = Math.Clamp(pos, 0, str.Length);
            int start = (int)Math.Min(clamped, str.Length - search.Length);
            for (int i = start; i >= 0; i--)
                if (string.CompareOrdinal(str, i, search, 0, search.Length) == 0)
                    return JsValue.From(i);
            return JsValue.From(-1);
        }));

        strProto.Set("substring", Fn(scope, "substring", 2, (self, args) =>
        {
            var str = self.ToJsString();
            // §15.5.4.15: ToInteger both bounds; a missing end is +∞, NaN is 0
            double start = args.Length > 0
                ? ToIntegerD(args[0].ToNumber()) : 0;
            double end = args.Length > 1
                ? ToIntegerD(args[1].ToNumber()) : double.PositiveInfinity;

            start = Math.Clamp(start, 0, str.Length);
            end   = Math.Clamp(end, 0, str.Length);
            if (start > end) (start, end) = (end, start);

            return JsValue.From(str[(int)start..(int)end]);
        }));

        strProto.Set("substr", Fn(scope, "substr", (self, args) =>
        {
            var str = self.ToJsString();
            int start = args.Length > 0 && !double.IsNaN(args[0].ToNumber()) ? (int)args[0].ToNumber() : 0;
            int length = args.Length > 1 && !double.IsNaN(args[1].ToNumber())
                ? (int)args[1].ToNumber() : str.Length - start;

            if (start < 0) start = Math.Max(str.Length + start, 0);
            if (length < 0) return JsValue.From("");
            start = Math.Min(start, str.Length);
            length = Math.Min(length, str.Length - start);

            return JsValue.From(str.Substring(start, length));
        }));

        strProto.Set("slice", Fn(scope, "slice", 2, (self, args) =>
        {
            var str = self.ToJsString();
            int start = args.Length > 0 && !double.IsNaN(args[0].ToNumber()) ? (int)args[0].ToNumber() : 0;
            int end = args.Length > 1 && !double.IsNaN(args[1].ToNumber())
                ? (int)args[1].ToNumber() : str.Length;

            if (start < 0) start = Math.Max(str.Length + start, 0);
            if (end < 0)   end   = Math.Max(str.Length + end, 0);
            start = Math.Clamp(start, 0, str.Length);
            end   = Math.Clamp(end, 0, str.Length);
            if (end <= start) return JsValue.From("");

            return JsValue.From(str[start..end]);
        }));

        strProto.Set("toLowerCase", Fn(scope, "toLowerCase", (self, args) =>
            JsValue.From(EraMapCase(self.ToJsString(), toUpper: false))));
        strProto.Set("toUpperCase", Fn(scope, "toUpperCase", (self, args) =>
            JsValue.From(EraMapCase(self.ToJsString(), toUpper: true))));

        strProto.Set("split", Fn(scope, "split", 2, (self, args) =>
        {
            var str = self.ToJsString();
            var result = NewArray(scope);

            int limit = args.Length > 1 && args[1].Type == JsType.Number &&
                        !double.IsNaN(args[1].ToNumber())
                ? (int)args[1].ToNumber() : int.MaxValue;
            if (limit < 1)
            {
                result.Set("length", JsValue.From(0));
                return JsValue.FromObject(result);
            }

            if (args.Length == 0 || args[0].Type == JsType.Undefined)
            {
                // §15.5.4.14: an undefined separator yields the whole string
                // as the single element (split(void 0) is NOT split("undefined"))
                result.Set("0", JsValue.From(str));
                result.Set("length", JsValue.From(1));
                return JsValue.FromObject(result);
            }

            if (args.Length == 0)
            {
                result.Set("0", JsValue.From(str));
                result.Set("length", JsValue.From(1));
                return JsValue.FromObject(result);
            }

            string[] parts;
            if (args[0].Type is (JsType.Object or JsType.Function) &&
                args[0].GetObjectOrFunction().Class == "RegExp")
            {
                // RegExp separator — ToJsString on a RegExp object is
                // "[object Object]", so this needs the real source
                var (source, flags) = RegexParts(args[0]);
                parts = CreateJsRegex(source, RegexOptionsFor(flags)).Split(str);
            }
            else
            {
                string separator = args[0].ToJsString();
                parts = separator.Length == 0
                    ? str.Select(c => c.ToString()).ToArray()
                    : str.Split(new[] { separator }, StringSplitOptions.None);
            }

            int n = 0;
            foreach (var part in parts)
            {
                if (n >= limit) break;
                result.Set((n++).ToString(), JsValue.From(part));
            }
            result.Set("length", JsValue.From(n));
            return JsValue.FromObject(result);
        }));

        strProto.Set("concat", Fn(scope, "concat", 1, (self, args) =>
        {
            var sb = new StringBuilder(self.ToJsString());
            foreach (var arg in args) sb.Append(arg.ToJsString());
            return JsValue.From(sb.ToString());
        }));

        strProto.Set("match", Fn(scope, "match", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.Null;
            var str = self.ToJsString();
            var (source, flags) = RegexParts(args[0]);
            var options = RegexOptionsFor(flags);
            if (!flags.Contains('g'))
            {
                // Non-global: single-match array or null
                var m = CreateJsRegex(source, options).Match(str);
                return m.Success ? MatchArray(scope, m, str) : JsValue.Null;
            }

            var matches = CreateJsRegex(source, options).Matches(str);
            if (matches.Count == 0) return JsValue.Null;
            var result = NewArray(scope);
            int n = 0;
            foreach (Match m in matches)
                result.Set((n++).ToString(), JsValue.From(m.Value));
            result.Set("length", JsValue.From(n));
            return JsValue.FromObject(result);
        }));

        strProto.Set("replace", Fn(scope, "replace", 2, (self, args) =>
        {
            if (args.Length < 2) return self;
            var str = self.ToJsString();
            var replacement = args[1].ToJsString();

            if (args[0].Type is (JsType.Object or JsType.Function) &&
                args[0].GetObjectOrFunction().Class == "RegExp")
            {
                var (source, flags) = RegexParts(args[0]);
                var regex = CreateJsRegex(source, RegexOptionsFor(flags));
                // $1..$9 group references — the era's usage
                return JsValue.From(flags.Contains('g')
                    ? regex.Replace(str, replacement)
                    : regex.Replace(str, replacement, 1));
            }

            var searchStr = args[0].ToJsString();
            int index = str.IndexOf(searchStr, StringComparison.Ordinal);
            if (index < 0) return self;
            return JsValue.From(
                str[..index] + replacement + str[(index + searchStr.Length)..]);
        }));

        strProto.Set("search", Fn(scope, "search", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var str = self.ToJsString();
            var (source, flags) = RegexParts(args[0]);
            var m = CreateJsRegex(source, RegexOptionsFor(flags)).Match(str);
            return JsValue.From(m.Success ? m.Index : -1);
        }));

        strProto.Set("toString", Fn(scope, "toString", (self, args) =>
        {
            // §15.5.4.2: requires a String value or a String wrapper
            if (self.Type == JsType.String) return self;
            if (self.Type is (JsType.Object or JsType.Function) &&
                self.GetObjectOrFunction().Class == "String")
                return self.GetObjectOrFunction().Get("value") is { Type: JsType.String } s
                    ? s : JsValue.From("");       // String.prototype's [[Value]] is ""
            throw new JsTypeErrorException("String.prototype.toString is not generic");
        }));
        strProto.Set("valueOf", Fn(scope, "valueOf", (self, args) =>
        {
            // §15.5.4.3: same receiver rule as toString
            if (self.Type == JsType.String) return self;
            if (self.Type is (JsType.Object or JsType.Function) &&
                self.GetObjectOrFunction().Class == "String")
                return self.GetObjectOrFunction().Get("value") is { Type: JsType.String } s
                    ? s : JsValue.From("");
            throw new JsTypeErrorException("String.prototype.valueOf is not generic");
        }));

        strProto.Set("localeCompare", Fn(scope, "localeCompare", 1, (self, args) =>
            JsValue.From(args.Length == 0
                ? 0
                : string.Compare(self.ToJsString(), args[0].ToJsString(),
                    CultureInfo.CurrentCulture, CompareOptions.None))));

        // ── Era HTML wrapper methods — extremely common on 1996 pages:
        //    document.write("Welcome".big().blink()) etc.
        void Wrap(string name, string tag, bool closes = true)
        {
            strProto.Set(name, Fn(scope, name, (self, args) =>
            {
                string inner = self.ToJsString();
                return JsValue.From(closes
                    ? $"<{tag}>{inner}</{tag}>"
                    : $"<{tag}>");
            }));
        }

        Wrap("bold", "b");
        Wrap("italics", "i");
        Wrap("underline", "u");
        Wrap("strike", "strike");
        Wrap("big", "big");
        Wrap("small", "small");
        Wrap("blink", "blink");
        Wrap("fixed", "tt");
        Wrap("sub", "sub");
        Wrap("sup", "sup");

        strProto.Set("fontcolor", Fn(scope, "fontcolor", (self, args) =>
            JsValue.From($"<font color=\"{ArgStr(args, 0)}\">{self.ToJsString()}</font>")));
        strProto.Set("fontsize", Fn(scope, "fontsize", (self, args) =>
            JsValue.From($"<font size=\"{ArgStr(args, 0)}\">{self.ToJsString()}</font>")));
        strProto.Set("anchor", Fn(scope, "anchor", (self, args) =>
            JsValue.From($"<a name=\"{ArgStr(args, 0)}\">{self.ToJsString()}</a>")));
        strProto.Set("link", Fn(scope, "link", (self, args) =>
            JsValue.From($"<a href=\"{ArgStr(args, 0)}\">{self.ToJsString()}</a>")));

        var fromCharCode = new JsFunction((self, args) =>
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                // §15.5.3.2: each argument is ToUint16'ed
                double d = ToIntegerD(arg.ToNumber());
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('\0'); continue; }
                sb.Append((char)(unchecked((int)(long)d) & 0xFFFF));
            }
            return JsValue.From(sb.ToString());
        }, scope, "fromCharCode", length: 1);

        var stringCtor = new JsFunction((self, args) =>
        {
            string text = args.Length > 0 ? args[0].ToJsString() : "";
            // Only a genuine `new String(...)` mutates `self`. A plain
            // String("x") call used to receive the GLOBAL object as this
            // and write value/length/Prototype onto it — corrupting every
            // later window conversion. Detection: ExecuteNew chains the
            // fresh object to this constructor's prototype BEFORE the call.
            if (self.Type is (JsType.Object or JsType.Function) &&
                ReferenceEquals(self.GetObjectOrFunction().Prototype, strProto))
            {
                var strObj = self.GetObjectOrFunction();
                strObj.Class = "String";
                strObj.Set("value", JsValue.From(text));
                strObj.SetWritable("length", JsValue.From(text.Length));
                strObj.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                strObj.Attrs["length"] = JsObject.PropAttr.Builtin;   // §15.5.5.1: length is { ReadOnly, DontEnum, DontDelete }
                strObj.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                return self;
            }
            return JsValue.From(text);
        }, scope, "String", length: 1);
        stringCtor.Set("prototype", JsValue.FromObject(strProto));
        stringCtor.Set("fromCharCode", JsValue.FromFunction(fromCharCode));
        // §15.5.4.1: String.prototype.constructor is the String constructor.
        // String.prototype is itself a String (length 0, §15.5.4).
        strProto.Set("constructor", JsValue.FromFunction(stringCtor));
        strProto.SetWritable("length", JsValue.From(0));
        strProto.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
        strProto.Attrs["length"] = JsObject.PropAttr.Builtin;
        scope.Define("String", JsValue.FromFunction(stringCtor));

        return strProto;
    }

    private static (string Source, string Flags) RegexParts(JsValue v)
    {
        if (v.Type is (JsType.Object or JsType.Function))
        {
            var o = v.GetObjectOrFunction();
            return (o.Get("source").ToJsString(), o.Get("flags").ToJsString());
        }
        return (v.ToJsString(), "");
    }

    private static int NormalizeLastIndex(JsValue value)
    {
        if (value.Type != JsType.Number) return 0;
        double n = value.GetNumber();
        if (double.IsNaN(n) || n <= 0) return 0;
        if (double.IsInfinity(n) || n > int.MaxValue) return int.MaxValue;
        return (int)Math.Truncate(n);
    }

    private static int AdvanceLastIndex(int index, int length, int inputLength) =>
        Math.Min(inputLength, length == 0 ? index + 1 : index + length);

    private static RegexOptions RegexOptionsFor(string flags)
    {
        var options = RegexOptions.None;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        return options;
    }

    private static JsValue MatchArray(JsScope scope, Match m, string input)
    {
        var result = NewArray(scope);
        int n = 0;
        foreach (Group g in m.Groups)
            result.Set((n++).ToString(), JsValue.From(g.Value));
        result.Set("length", JsValue.From(n));
        result.Set("index", JsValue.From(m.Index));
        result.Set("input", JsValue.From(input));
        return JsValue.FromObject(result);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Number
    // ─────────────────────────────────────────────────────────────────────

    private static JsObject RegisterNumber(JsScope scope, JsObject objectProto)
    {
        var numProto = new JsObject { Class = "Number", Prototype = objectProto };

        numProto.Set("toString", Fn(scope, "toString", (self, args) =>
        {
            double num = self.ToNumber();
            int radix = args.Length > 0 && !double.IsNaN(args[0].ToNumber())
                ? (int)args[0].ToNumber() : 10;
            if (radix < 2 || radix > 36)
                throw new JsRangeErrorException("radix out of range");
            if (radix == 10)
                return JsValue.From(JsValue.NumberToString(num));
            return JsValue.From(ConvertToBase(num, radix));
        }));

        numProto.Set("valueOf", Fn(scope, "valueOf", (self, args) =>
            self.Type is JsType.Object or JsType.Function &&
            self.GetObjectOrFunction().Class == "Number"
                // Number.prototype's own [[Value]] is +0 (§15.7.4)
                ? self.GetObjectOrFunction().Get("value") is { Type: JsType.Number } n
                    ? n : JsValue.From(0)
                : self));

        // ── ES3 / JScript 5.0 number formatting (checklist §12) ──
        // toFixed — fixed-point with era round-half-away-from-zero on the
        // double's true binary value (so 1.005.toFixed(2) === "1.00", the
        // classic binary-representation behaviour both JScript 5 and
        // ECMAScript implementations exhibit).
        numProto.Set("toFixed", Fn(scope, "toFixed", 1, (self, args) =>
        {
            double num = self.ToNumber();
            if (double.IsNaN(num)) return JsValue.From("NaN");
            if (double.IsPositiveInfinity(num)) return JsValue.From("Infinity");
            if (double.IsNegativeInfinity(num)) return JsValue.From("-Infinity");

            int digits = 0;
            if (args.Length > 0)
            {
                double d = args[0].ToNumber();
                if (double.IsNaN(d)) return JsValue.From("NaN");
                if (d < 0 || d > 100)
                    throw new JsRangeErrorException("toFixed() digits argument must be between 0 and 100");
                digits = (int)d;
            }
            // ES3: |x| ≥ 10^21 returns the plain (exponential) ToString form.
            if (Math.Abs(num) >= 1e21)
                return JsValue.From(JsValue.NumberToString(num));
            return JsValue.From(FixedString(num, digits));
        }));

        numProto.Set("toExponential", Fn(scope, "toExponential", 1, (self, args) =>
        {
            double num = self.ToNumber();
            if (double.IsNaN(num)) return JsValue.From("NaN");
            if (double.IsPositiveInfinity(num)) return JsValue.From("Infinity");
            if (double.IsNegativeInfinity(num)) return JsValue.From("-Infinity");

            bool noArgument = args.Length == 0 || args[0].Type == JsType.Undefined;
            int digits;
            if (!noArgument)
            {
                double d = args[0].ToNumber();
                if (double.IsNaN(d)) return JsValue.From("NaN");
                if (d < 0 || d > 100)
                    throw new JsRangeErrorException("toExponential() fraction digits argument must be between 0 and 100");
                digits = (int)d;
            }
            else
            {
                // No argument: as many fraction digits as the shortest
                // round-trip representation needs (ES3).
                digits = ShortestSignificantDigits(num);
                if (num != 0.0) digits = Math.Max(0, digits - 1);
            }
            string s = ExponentialString(num, digits);
            if (noArgument)
            {
                // ES3 no-argument form trims insignificant trailing zeros.
                int eIdx = s.IndexOf('e');
                if (eIdx > 0)
                {
                    string mantissa = s[..eIdx].TrimEnd('0').TrimEnd('.');
                    s = mantissa + s[eIdx..];
                }
            }
            return JsValue.From(s);
        }));

        numProto.Set("toPrecision", Fn(scope, "toPrecision", 1, (self, args) =>
        {
            double num = self.ToNumber();
            if (double.IsNaN(num)) return JsValue.From("NaN");
            if (double.IsPositiveInfinity(num)) return JsValue.From("Infinity");
            if (double.IsNegativeInfinity(num)) return JsValue.From("-Infinity");

            if (args.Length == 0 || args[0].Type == JsType.Undefined)
                return JsValue.From(JsValue.NumberToString(num));

            double pd = args[0].ToNumber();
            if (double.IsNaN(pd)) return JsValue.From("NaN");
            if (pd < 1 || pd > 100)
                throw new JsRangeErrorException("toPrecision() argument must be between 1 and 100");
            int precision = (int)pd;

            if (num == 0.0)
                return JsValue.From(FixedString(num, precision - 1));

            int exponent = (int)Math.Floor(Math.Log10(Math.Abs(num)));
            // Round first — 99.5 to 2 significant digits bumps the exponent.
            double rounded = RoundSignificant(num, precision, exponent, out int roundedExponent);
            if (roundedExponent < -6 || roundedExponent >= precision)
                return JsValue.From(ExponentialString(rounded, precision - 1));
            return JsValue.From(FixedString(rounded, precision - 1 - roundedExponent));
        }));

        var numberCtor = new JsFunction((self, args) =>
        {
            double value = args.Length > 0 ? args[0].ToNumber() : 0;
            // Genuine `new Number(5)` only — plain Number("5") must return
            // the primitive and never touch the global object.
            if (self.Type is (JsType.Object or JsType.Function) &&
                ReferenceEquals(self.GetObjectOrFunction().Prototype, numProto))
            {
                var numObj = self.GetObjectOrFunction();
                numObj.Class = "Number";
                numObj.Set("value", JsValue.From(value));
                numObj.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                numObj.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                return self;
            }
            return JsValue.From(value);
        }, scope, "Number", length: 1);
        numberCtor.Set("prototype", JsValue.FromObject(numProto));
        numberCtor.Set("MAX_VALUE", JsValue.From(double.MaxValue));
        numberCtor.Set("MIN_VALUE", JsValue.From(double.Epsilon));
        numberCtor.Set("NaN", JsValue.From(double.NaN));
        numberCtor.Set("POSITIVE_INFINITY", JsValue.From(double.PositiveInfinity));
        numberCtor.Set("NEGATIVE_INFINITY", JsValue.From(double.NegativeInfinity));
        // §15.7.3.1: Number.prototype.constructor is the Number constructor.
        numProto.Set("constructor", JsValue.FromFunction(numberCtor));
        scope.Define("Number", JsValue.FromFunction(numberCtor));

        return numProto;
    }

    /// <summary>Integer part of a number in base 2-36, with fraction digits.</summary>
    private static string ConvertToBase(double num, int radix)
    {
        if (double.IsNaN(num)) return "NaN";
        if (double.IsPositiveInfinity(num)) return "Infinity";
        if (double.IsNegativeInfinity(num)) return "-Infinity";

        bool neg = num < 0;
        long integerPart = (long)Math.Floor(Math.Abs(num));
        double fraction = Math.Abs(num) - integerPart;

        var sb = new StringBuilder();
        if (neg) sb.Append('-');

        if (integerPart == 0)
        {
            sb.Append('0');
        }
        else
        {
            var digits = new List<char>();
            while (integerPart > 0)
            {
                int d = (int)(integerPart % radix);
                digits.Add(d < 10 ? (char)('0' + d) : (char)('a' + d - 10));
                integerPart /= radix;
            }
            for (int i = digits.Count - 1; i >= 0; i--)
                sb.Append(digits[i]);
        }

        if (fraction > 0)
        {
            sb.Append('.');
            for (int i = 0; i < 10 && fraction > 0; i++)
            {
                fraction *= radix;
                int d = (int)fraction;
                sb.Append(d < 10 ? (char)('0' + d) : (char)('a' + d - 10));
                fraction -= d;
            }
        }

        return sb.ToString();
    }

    // ── ES3 number-formatting helpers (toFixed/toExponential/toPrecision) ──

    /// <summary>Fixed-point decimal string with <paramref name="digits"/>
    /// fraction digits, era round-half-away-from-zero applied to the double's
    /// true binary value.</summary>
    private static string FixedString(double num, int digits)
    {
        double rounded = Math.Round(num, Math.Max(0, digits), MidpointRounding.AwayFromZero);
        return rounded.ToString("F" + Math.Max(0, digits), CultureInfo.InvariantCulture);
    }

    /// <summary>Exponential string "d.ddde+dd": fraction digits given, JS-style
    /// exponent (sign always, no zero padding).</summary>
    private static string ExponentialString(double num, int digits)
    {
        if (num == 0.0)
        {
            // (0).toExponential(2) → "0.00e+0"
            string zeroMantissa = "0";
            if (digits > 0) zeroMantissa += "." + new string('0', digits);
            return zeroMantissa + "e+0";
        }

        int exponent = (int)Math.Floor(Math.Log10(Math.Abs(num)));
        double mantissa = Math.Round(num / Math.Pow(10, exponent), digits, MidpointRounding.AwayFromZero);
        // Rounding the mantissa may bump it to 10 (e.g. 9.99 → 1 digit → 10.0)
        if (Math.Abs(mantissa) >= 10.0)
        {
            exponent++;
            mantissa = Math.Round(num / Math.Pow(10, exponent), digits, MidpointRounding.AwayFromZero);
        }
        string m = mantissa.ToString("F" + Math.Max(0, digits), CultureInfo.InvariantCulture);
        string e = exponent < 0 ? "-" : "+";
        int absExp = Math.Abs(exponent);
        string expDigits = absExp == 0 ? "0" : absExp.ToString(CultureInfo.InvariantCulture);
        return m + "e" + e + expDigits;
    }

    /// <summary>Number of significant decimal digits in the shortest
    /// round-trip representation of <paramref name="num"/>.</summary>
    private static int ShortestSignificantDigits(double num)
    {
        if (num == 0.0 || double.IsNaN(num) || double.IsInfinity(num)) return 1;
        string s = Math.Abs(num).ToString("R", CultureInfo.InvariantCulture);
        int significant = 0;
        bool seenNonZero = false;
        foreach (char c in s)
        {
            if (c == '-' || c == '.') continue;
            if (!char.IsDigit(c)) break;   // 'E' — mantissa digits counted so far
            if (c != '0') seenNonZero = true;
            if (seenNonZero) significant++;
        }
        return Math.Max(1, significant);
    }

    /// <summary>Rounds to <paramref name="precision"/> significant digits;
    /// reports the exponent of the ROUNDED value (rounding 99.5 to 2 digits
    /// yields 100, exponent 2).</summary>
    private static double RoundSignificant(double num, int precision, int exponent, out int roundedExponent)
    {
        int decimals = precision - 1 - exponent;
        double rounded = decimals >= 0
            ? Math.Round(num, decimals, MidpointRounding.AwayFromZero)
            : Math.Round(num / Math.Pow(10, -decimals), 0, MidpointRounding.AwayFromZero) * Math.Pow(10, -decimals);
        roundedExponent = rounded == 0.0 ? exponent : (int)Math.Floor(Math.Log10(Math.Abs(rounded)));
        if (rounded != 0.0 && roundedExponent != exponent)
        {
            // Exponent bumped by rounding (99.5 → 100): re-round at the new
            // magnitude so the output carries exactly `precision` digits.
            decimals = precision - 1 - roundedExponent;
            rounded = decimals >= 0
                ? Math.Round(num, decimals, MidpointRounding.AwayFromZero)
                : rounded;
        }
        return rounded;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Boolean
    // ─────────────────────────────────────────────────────────────────────

    private static JsObject RegisterBoolean(JsScope scope, JsObject objectProto)
    {
        var boolProto = new JsObject { Class = "Boolean", Prototype = objectProto };

        boolProto.Set("toString", Fn(scope, "toString", (self, args) =>
        {
            // §15.6.4.2: requires a Boolean value or a Boolean wrapper
            if (self.Type == JsType.Boolean) return JsValue.From(self.GetBool() ? "true" : "false");
            if (self.Type is (JsType.Object or JsType.Function) &&
                self.GetObjectOrFunction().Class == "Boolean")
            {
                bool b = self.GetObjectOrFunction().Get("value") is { Type: JsType.Boolean } bv
                    ? bv.GetBool()
                    : false;                     // Boolean.prototype's [[Value]] is false
                return JsValue.From(b ? "true" : "false");
            }
            throw new JsTypeErrorException("Boolean.prototype.toString is not generic");
        }));
        boolProto.Set("valueOf", Fn(scope, "valueOf", (self, args) =>
        {
            // §15.6.4.3: same receiver rule as toString
            if (self.Type == JsType.Boolean) return self;
            if (self.Type is (JsType.Object or JsType.Function) &&
                self.GetObjectOrFunction().Class == "Boolean")
                return self.GetObjectOrFunction().Get("value") is { Type: JsType.Boolean } b
                    ? b : JsValue.False;
            throw new JsTypeErrorException("Boolean.prototype.valueOf is not generic");
        }));

        var boolCtor = new JsFunction((self, args) =>
        {
            bool value = args.Length > 0 && args[0].ToBoolean();
            // Genuine `new Boolean(...)` only — plain Boolean(x) returns the
            // primitive and never writes value/Class onto the global object.
            if (self.Type is (JsType.Object or JsType.Function) &&
                ReferenceEquals(self.GetObjectOrFunction().Prototype, boolProto))
            {
                var boolObj = self.GetObjectOrFunction();
                boolObj.Class = "Boolean";
                boolObj.Set("value", JsValue.From(value));
                boolObj.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                boolObj.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                return self;
            }
            return JsValue.From(value);
        }, scope, "Boolean", length: 1);
        boolCtor.Set("prototype", JsValue.FromObject(boolProto));
        // §15.6.4.1: Boolean.prototype.constructor is the Boolean constructor.
        boolProto.Set("constructor", JsValue.FromFunction(boolCtor));
        scope.Define("Boolean", JsValue.FromFunction(boolCtor));

        return boolProto;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Math
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterMath(JsScope scope)
    {
        // §15.8: Math's [[Class]] is "Math" (Object.prototype.toString
        // reports "[object Math]")
        var math = new JsObject { Class = "Math" };

        math.Set("E",       JsValue.From(Math.E));
        math.Set("PI",      JsValue.From(Math.PI));
        math.Set("LN2",     JsValue.From(Math.Log(2)));
        math.Set("LN10",    JsValue.From(Math.Log(10)));
        math.Set("LOG2E",   JsValue.From(1.0 / Math.Log(2)));
        math.Set("LOG10E",  JsValue.From(1.0 / Math.Log(10)));
        math.Set("SQRT2",   JsValue.From(Math.Sqrt(2)));
        math.Set("SQRT1_2", JsValue.From(Math.Sqrt(0.5)));

        math.Set("abs",   Fn(scope, "abs", 1,   (s, a) => JsValue.From(Math.Abs(Arg(a, 0)))));
        math.Set("ceil",  Fn(scope, "ceil", 1,  (s, a) => JsValue.From(Math.Ceiling(Arg(a, 0)))));
        math.Set("floor", Fn(scope, "floor", 1, (s, a) => JsValue.From(Math.Floor(Arg(a, 0)))));
        math.Set("round", Fn(scope, "round", 1, (s, a) =>
        {
            double v = Arg(a, 0);
            // JS rounds .5 up (toward +Infinity), unlike .NET's banker's rounding
            return JsValue.From(Math.Floor(v + 0.5));
        }));
        math.Set("min",   Fn(scope, "min", 2,   (s, a) =>
        {
            double min = double.PositiveInfinity;
            foreach (var v in a)
            {
                double x = v.ToNumber();
                if (double.IsNaN(x)) return JsValue.From(double.NaN);   // §15.8.2.12
                if (x < min) { min = x; continue; }
                // min(-0, +0) is -0 (§15.8.2.12): a negative zero wins ties
                if (x == min && x == 0 && IsNegZero(x))
                    min = x;   // -0 wins zero ties
            }
            return JsValue.From(min);
        }));
        math.Set("max",   Fn(scope, "max", 2,   (s, a) =>
        {
            double max = double.NegativeInfinity;
            foreach (var v in a)
            {
                double x = v.ToNumber();
                if (double.IsNaN(x)) return JsValue.From(double.NaN);   // §15.8.2.11
                if (x > max) { max = x; continue; }
                // max(+0, -0) is +0 (§15.8.2.11): a positive zero wins ties
                if (x == max && x == 0 && !IsNegZero(x))
                    max = x;   // +0 wins zero ties
            }
            return JsValue.From(max);
        }));
        math.Set("pow",   Fn(scope, "pow", 2,   (s, a) => JsValue.From(Math.Pow(Arg(a, 0), Arg(a, 1)))));
        math.Set("sqrt",  Fn(scope, "sqrt", 1,  (s, a) => JsValue.From(Math.Sqrt(Arg(a, 0)))));
        math.Set("log",   Fn(scope, "log", 1,   (s, a) => JsValue.From(Math.Log(Arg(a, 0)))));
        math.Set("exp",   Fn(scope, "exp", 1,   (s, a) => JsValue.From(Math.Exp(Arg(a, 0)))));
        math.Set("sin",   Fn(scope, "sin", 1,   (s, a) => JsValue.From(Math.Sin(Arg(a, 0)))));
        math.Set("cos",   Fn(scope, "cos", 1,   (s, a) => JsValue.From(Math.Cos(Arg(a, 0)))));
        math.Set("tan",   Fn(scope, "tan", 1,   (s, a) => JsValue.From(Math.Tan(Arg(a, 0)))));
        math.Set("asin",  Fn(scope, "asin", 1,  (s, a) => JsValue.From(Math.Asin(Arg(a, 0)))));
        math.Set("acos",  Fn(scope, "acos", 1,  (s, a) => JsValue.From(Math.Acos(Arg(a, 0)))));
        math.Set("atan",  Fn(scope, "atan", 1,  (s, a) => JsValue.From(Math.Atan(Arg(a, 0)))));
        math.Set("atan2", Fn(scope, "atan2", 2, (s, a) =>
        {
            double y = Arg(a, 0), x = Arg(a, 1);
            // §15.8.2.5 zero-argument table — do not rely on libm for ±0 signs
            if (double.IsNaN(y) || double.IsNaN(x)) return JsValue.From(double.NaN);
            if (y == 0 && x == 0)
            {
                bool yNeg = IsNegZero(y), xNeg = IsNegZero(x);
                if (!yNeg) return JsValue.From(xNeg ? Math.PI : 0.0);
                return JsValue.From(xNeg ? -Math.PI : -0.0);
            }
            return JsValue.From(Math.Atan2(y, x));
        }));
        math.Set("random", Fn(scope, "random", (s, a) =>
            JsValue.From(_sharedRandom.NextDouble())));

        scope.Define("Math", JsValue.FromObject(math));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Date
    // ─────────────────────────────────────────────────────────────────────

    private static readonly DateTime Epoch =
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static double MsOf(JsValue self)
    {
        // §15.9.5: the Date.prototype functions require a receiver whose
        // [[Class]] is "Date"; Date.prototype itself qualifies (value NaN).
        if (self.Type is (JsType.Object or JsType.Function) &&
            self.GetObjectOrFunction().Class == "Date")
        {
            return self.GetObjectOrFunction().Get("value") is { Type: JsType.Number } v
                ? v.GetNumber()
                : double.NaN;
        }
        throw new JsTypeErrorException("Date.prototype method called on a non-Date object");
    }

    // ── ECMA-262 §15.9.1 date-time arithmetic (pure double, overflow-safe) ──
    // The old implementation stored dates as .NET DateTime, which cannot
    // represent the full ES3 time range ±8.64e15 ms (§15.9.1.1) nor NaN,
    // so pre-1970/late dates and (new Date(NaN)).getX() crashed the engine.
    // Everything below works on the raw millisecond time value instead.

    private const double MsPerSecond = 1000.0;
    private const double MsPerMinute = 60_000.0;
    private const double MsPerHour   = 3_600_000.0;
    private const double MsPerDay    = 86_400_000.0;
    private const double TimeClipLimit = 8_640_000_000_000_000.0; // §15.9.1.1

    private static readonly double[] DaysBeforeMonth =
        { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };
    private static readonly double[] DaysBeforeMonthLeap =
        { 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335 };

    private static readonly string[] DateDayNames =
        { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] DateMonthNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun",
          "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    /// <summary>JS-style remainder: result always in [0, n) for n &gt; 0.</summary>
    private static double PosMod(double v, double n)
    {
        double r = v % n;
        return r < 0 ? r + n : r;
    }

    /// <summary>ECMA-262 §9.4 ToInteger: NaN → 0, truncate toward zero.</summary>
    private static double ToIntegerD(double x)
    {
        if (double.IsNaN(x)) return 0;
        if (double.IsInfinity(x)) return x;
        return Math.Truncate(x);
    }

    /// <summary>Era (Unicode 2.1) case mapping: U+0130 (İ) lowercases to
    /// plain "i", and the Georgian letters are caseless — the modern .NET
    /// tables differ on both, and the corpus pins the era behaviour.</summary>
    private static string EraMapCase(string s, bool toUpper)
    {
        if (s.Length == 0) return s;
        bool ascii = true;
        foreach (char c in s)
            if (c >= 128) { ascii = false; break; }
        if (ascii)
            return toUpper ? s.ToUpperInvariant() : s.ToLowerInvariant();

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (!toUpper && ch == '\u0130') { sb.Append('i'); continue; }
            if (ch >= '\u10D0' && ch <= '\u10FF') { sb.Append(ch); continue; }
            sb.Append(toUpper ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    // §15.9.1.2 Day Number and Time within Day
    private static double Day(double t) => Math.Floor(t / MsPerDay);
    private static double TimeWithinDay(double t) => PosMod(t, MsPerDay);

    // §15.9.1.3 Year Number
    private static double DaysInYear(double y)
        => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 ? 366 : 365;
    private static double DayFromYear(double y)
        => 365 * (y - 1970)
         + Math.Floor((y - 1969) / 4.0)
         - Math.Floor((y - 1901) / 100.0)
         + Math.Floor((y - 1601) / 400.0);
    private static double TimeFromYear(double y) => MsPerDay * DayFromYear(y);
    private static double YearFromTime(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t)) return double.NaN;
        double d = Day(t);
        double y = Math.Floor(d / 365.2425) + 1970;   // seed estimate, then adjust
        while (DayFromYear(y) > d) y -= 1;
        while (DayFromYear(y + 1) <= d) y += 1;
        return y;
    }

    // §15.9.1.4 Month Number / §15.9.1.5 Date Number
    private static double DayWithinYear(double t)
        => Day(t) - DayFromYear(YearFromTime(t));
    private static double MonthFromTime(double t)
    {
        if (double.IsNaN(t)) return double.NaN;
        double d = DayWithinYear(t);
        var table = DaysInYear(YearFromTime(t)) == 366 ? DaysBeforeMonthLeap : DaysBeforeMonth;
        for (int m = 11; m >= 0; m--)
            if (d >= table[m]) return m;
        return 0;
    }
    private static double DateFromTime(double t)
    {
        if (double.IsNaN(t)) return double.NaN;
        var table = DaysInYear(YearFromTime(t)) == 366 ? DaysBeforeMonthLeap : DaysBeforeMonth;
        return DayWithinYear(t) - table[(int)MonthFromTime(t)] + 1;
    }

    // §15.9.1.6 Week Day / time-of-day fields
    private static double WeekDay(double t) =>
        double.IsNaN(t) ? double.NaN : PosMod(Day(t) + 4, 7);   // 1970-01-01 was a Thursday
    private static double HourFromTime(double t) =>
        double.IsNaN(t) ? double.NaN : PosMod(Math.Floor(t / MsPerHour), 24);
    private static double MinFromTime(double t) =>
        double.IsNaN(t) ? double.NaN : PosMod(Math.Floor(t / MsPerMinute), 60);
    private static double SecFromTime(double t) =>
        double.IsNaN(t) ? double.NaN : PosMod(Math.Floor(t / MsPerSecond), 60);
    private static double MsFromTime(double t) =>
        double.IsNaN(t) ? double.NaN : PosMod(t, MsPerSecond);

    // §15.9.1.7/§15.9.1.8 local ↔ UTC conversion through the host zone.
    // DaylightSavingTA is folded into the zone lookup (offset at instant);
    // for wall-clock → UTC the probe is treated as a local wall time.
    private static double UtcOffsetAt(double t, bool treatAsLocalWall)
    {
        if (double.IsNaN(t) || double.IsInfinity(t)) return 0;
        double clamped = Math.Clamp(t, -62_135_596_800_000.0, 253_402_300_799_999.0);
        try
        {
            var probe = Epoch.AddMilliseconds(clamped);
            var wall = treatAsLocalWall
                ? DateTime.SpecifyKind(probe, DateTimeKind.Unspecified)
                : probe;
            return TimeZoneInfo.Local.GetUtcOffset(wall).TotalMilliseconds;
        }
        catch { return 0; }
    }
    private static double LocalTime(double t) => t + UtcOffsetAt(t, treatAsLocalWall: false);
    private static double UtcFromLocal(double t) => t - UtcOffsetAt(t, treatAsLocalWall: true);

    // §15.9.1.9 MakeTime
    private static double MakeTime(double hour, double min, double sec, double ms)
    {
        if (double.IsNaN(hour) || double.IsNaN(min) || double.IsNaN(sec) || double.IsNaN(ms))
            return double.NaN;
        double h = ToIntegerD(hour), m = ToIntegerD(min),
               s = ToIntegerD(sec), milli = ToIntegerD(ms);
        if (double.IsNaN(h + m + s + milli)) return double.NaN;  // ∞ + (−∞) guard
        return ((h * 60 + m) * 60 + s) * 1000 + milli;
    }
    // §15.9.1.10 MakeDay
    private static double MakeDay(double year, double month, double date)
    {
        if (double.IsNaN(year) || double.IsNaN(month) || double.IsNaN(date))
            return double.NaN;
        double y = ToIntegerD(year), m = ToIntegerD(month), d = ToIntegerD(date);
        if (double.IsNaN(y + m + d)) return double.NaN;
        double ym = y + Math.Floor(m / 12);
        double mn = PosMod(m, 12);
        var table = DaysInYear(ym) == 366 ? DaysBeforeMonthLeap : DaysBeforeMonth;
        return DayFromYear(ym) + table[(int)mn] + (d - 1);
    }
    // §15.9.1.11 MakeDate / §15.9.1.12 TimeClip
    private static double MakeDate(double day, double time) => day * MsPerDay + time;
    private static double TimeClip(double t)
    {
        if (double.IsNaN(t) || Math.Abs(t) > TimeClipLimit) return double.NaN;
        // The era engines store integral milliseconds — the suite expects
        // e.g. setTime(…441.6572) → …441, so clip to ToInteger as well.
        return ToIntegerD(t);
    }

    /// <summary>Writes a (already TimeClip'd) time value and returns it.</summary>
    private static JsValue StoreTime(JsValue self, double u)
    {
        if (self.Type is (JsType.Object or JsType.Function) &&
            self.GetObjectOrFunction().Class == "Date")
        {
            self.GetObjectOrFunction().Set("value", JsValue.From(u));
            return JsValue.From(u);
        }
        throw new JsTypeErrorException("Date.prototype method called on a non-Date object");
    }

    // Era display format for the Date string family (NN4 personality):
    // "Mon Sep 17 14:23:11 1996", day zero-padded — kept byte-identical,
    // but computed from the raw components so extreme years can't overflow.
    private static string DateYearString(double y)
    {
        if (y >= 0 && y <= 9999)
            return ((long)y).ToString("0000", CultureInfo.InvariantCulture);
        if (y > 9999) return "+" + (long)y;
        return "-" + ((long)(-y)).ToString("0000", CultureInfo.InvariantCulture);
    }
    private static string TwoDigits(double v)
        => ((long)v).ToString("00", CultureInfo.InvariantCulture);
    private static string DateToStringStyle(double localMs, bool utcStyle)
    {
        double y = YearFromTime(localMs), mo = MonthFromTime(localMs),
               d = DateFromTime(localMs), h = HourFromTime(localMs),
               mi = MinFromTime(localMs), s = SecFromTime(localMs);
        string day = DateDayNames[(int)WeekDay(localMs)];
        string mon = DateMonthNames[(int)mo];
        string core = $"{TwoDigits(h)}:{TwoDigits(mi)}:{TwoDigits(s)}";
        if (utcStyle)
            return $"{day}, {TwoDigits(d)} {mon} {DateYearString(y)} {core} GMT";
        return $"{day} {mon} {TwoDigits(d)} {core} {DateYearString(y)}";
    }
    private static string DateDatePart(double localMs) =>
        $"{DateDayNames[(int)WeekDay(localMs)]} {DateMonthNames[(int)MonthFromTime(localMs)]} " +
        $"{TwoDigits(DateFromTime(localMs))} {DateYearString(YearFromTime(localMs))}";

    /// <summary>Date.prototype.toString for the object stringifier ("" + date
    /// must agree with the method, §9.8) — overflow-safe on extreme values.</summary>
    internal static string DateToStringForStringify(double ms) =>
        double.IsNaN(ms) ? "Invalid Date"
            : DateToStringStyle(LocalTime(ms), utcStyle: false);

    private static void RegisterDate(JsScope scope, JsObject objectProto)
    {
        var dateProto = new JsObject { Class = "Date", Prototype = objectProto };

        dateProto.Set("getTime", Fn(scope, "getTime", (self, args) => JsValue.From(MsOf(self))));
        dateProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => JsValue.From(MsOf(self))));

        // ── local getters (§15.9.5): NaN time value ⇒ NaN for every field ──
        dateProto.Set("getFullYear",     Fn(scope, "getFullYear",     (s, a) => JsValue.From(YearFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getMonth",        Fn(scope, "getMonth",        (s, a) => JsValue.From(MonthFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getDate",         Fn(scope, "getDate",         (s, a) => JsValue.From(DateFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getDay",          Fn(scope, "getDay",          (s, a) => JsValue.From(WeekDay(LocalTime(MsOf(s))))));
        dateProto.Set("getHours",        Fn(scope, "getHours",        (s, a) => JsValue.From(HourFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getMinutes",      Fn(scope, "getMinutes",      (s, a) => JsValue.From(MinFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getSeconds",      Fn(scope, "getSeconds",      (s, a) => JsValue.From(SecFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getMilliseconds", Fn(scope, "getMilliseconds", (s, a) => JsValue.From(MsFromTime(LocalTime(MsOf(s))))));
        dateProto.Set("getTimezoneOffset", Fn(scope, "getTimezoneOffset", (s, a) =>
            JsValue.From((MsOf(s) - LocalTime(MsOf(s))) / MsPerMinute)));

        // setYear — Annex B.2.5: like setFullYear but with the two-digit
        // 1900 rule applied, and a NaN year makes the date NaN.
        dateProto.Set("setYear", Fn(scope, "setYear", (self, args) =>
        {
            double tv = MsOf(self);
            if (double.IsNaN(tv)) tv = 0;
            double t = LocalTime(tv);
            double year = Arg(args, 0);
            if (double.IsNaN(year))
                return StoreTime(self, double.NaN);
            if (year is >= 0 and <= 99) year += 1900;
            double day = MakeDay(year, MonthFromTime(t), DateFromTime(t));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(day, TimeWithinDay(t)))));
        }));

        // getYear — the era's method: year minus 1900 (Annex B.2.5 behaviour)
        dateProto.Set("getYear", Fn(scope, "getYear", (s, a) =>
            JsValue.From(YearFromTime(LocalTime(MsOf(s))) - 1900)));

        // ── UTC getters ──
        dateProto.Set("getUTCFullYear",     Fn(scope, "getUTCFullYear",     (s, a) => JsValue.From(YearFromTime(MsOf(s)))));
        dateProto.Set("getUTCMonth",        Fn(scope, "getUTCMonth",        (s, a) => JsValue.From(MonthFromTime(MsOf(s)))));
        dateProto.Set("getUTCDate",         Fn(scope, "getUTCDate",         (s, a) => JsValue.From(DateFromTime(MsOf(s)))));
        dateProto.Set("getUTCDay",          Fn(scope, "getUTCDay",          (s, a) => JsValue.From(WeekDay(MsOf(s)))));
        dateProto.Set("getUTCHours",        Fn(scope, "getUTCHours",        (s, a) => JsValue.From(HourFromTime(MsOf(s)))));
        dateProto.Set("getUTCMinutes",      Fn(scope, "getUTCMinutes",      (s, a) => JsValue.From(MinFromTime(MsOf(s)))));
        dateProto.Set("getUTCSeconds",      Fn(scope, "getUTCSeconds",      (s, a) => JsValue.From(SecFromTime(MsOf(s)))));
        dateProto.Set("getUTCMilliseconds", Fn(scope, "getUTCMilliseconds", (s, a) => JsValue.From(MsFromTime(MsOf(s)))));

        // ── setters (§15.9.5): build via MakeTime/MakeDay, TimeClip, store.
        // Function.length mirrors the §15.9.5 signatures (setSeconds(sec, ms) → 2 …).
        dateProto.Set("setTime", Fn(scope, "setTime", 1, (self, args) =>
            StoreTime(self, TimeClip(Arg(args, 0)))));

        dateProto.Set("setMilliseconds", Fn(scope, "setMilliseconds", 1, (self, args) =>
        {
            double t = LocalTime(MsOf(self));
            double time = MakeTime(HourFromTime(t), MinFromTime(t), SecFromTime(t), Arg(args, 0));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(Day(t), time))));
        }));
        dateProto.Set("setSeconds", Fn(scope, "setSeconds", 2, (self, args) =>
        {
            double t = LocalTime(MsOf(self));
            double time = MakeTime(HourFromTime(t), MinFromTime(t), Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : MsFromTime(t));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(Day(t), time))));
        }));
        dateProto.Set("setMinutes", Fn(scope, "setMinutes", 3, (self, args) =>
        {
            double t = LocalTime(MsOf(self));
            double time = MakeTime(HourFromTime(t), Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : SecFromTime(t),
                args.Length > 2 ? args[2].ToNumber() : MsFromTime(t));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(Day(t), time))));
        }));
        dateProto.Set("setHours", Fn(scope, "setHours", 4, (self, args) =>
        {
            double t = LocalTime(MsOf(self));
            double time = MakeTime(Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : MinFromTime(t),
                args.Length > 2 ? args[2].ToNumber() : SecFromTime(t),
                args.Length > 3 ? args[3].ToNumber() : MsFromTime(t));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(Day(t), time))));
        }));
        dateProto.Set("setDate", Fn(scope, "setDate", 1, (self, args) =>
        {
            double t = LocalTime(MsOf(self));
            double day = MakeDay(YearFromTime(t), MonthFromTime(t), Arg(args, 0));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(day, TimeWithinDay(t)))));
        }));
        dateProto.Set("setMonth", Fn(scope, "setMonth", 2, (self, args) =>
        {
            double t = LocalTime(MsOf(self));
            double day = MakeDay(YearFromTime(t), Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : DateFromTime(t));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(day, TimeWithinDay(t)))));
        }));
        // setFullYear treats a NaN date as +0 before reading the month/date
        // defaults (the two-digit rule deliberately does NOT apply here).
        dateProto.Set("setFullYear", Fn(scope, "setFullYear", 3, (self, args) =>
        {
            double tv = MsOf(self);
            double t = double.IsNaN(tv) ? LocalTime(0) : LocalTime(tv);
            double day = MakeDay(Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : MonthFromTime(t),
                args.Length > 2 ? args[2].ToNumber() : DateFromTime(t));
            return StoreTime(self, TimeClip(UtcFromLocal(MakeDate(day, TimeWithinDay(t)))));
        }));

        // ── UTC setters (§15.9.5): same arithmetic in UTC space ──
        dateProto.Set("setUTCMilliseconds", Fn(scope, "setUTCMilliseconds", 1, (self, args) =>
        {
            double t = MsOf(self);
            double time = MakeTime(HourFromTime(t), MinFromTime(t), SecFromTime(t), Arg(args, 0));
            return StoreTime(self, TimeClip(MakeDate(Day(t), time)));
        }));
        dateProto.Set("setUTCSeconds", Fn(scope, "setUTCSeconds", 2, (self, args) =>
        {
            double t = MsOf(self);
            double time = MakeTime(HourFromTime(t), MinFromTime(t), Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : MsFromTime(t));
            return StoreTime(self, TimeClip(MakeDate(Day(t), time)));
        }));
        dateProto.Set("setUTCMinutes", Fn(scope, "setUTCMinutes", 3, (self, args) =>
        {
            double t = MsOf(self);
            double time = MakeTime(HourFromTime(t), Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : SecFromTime(t),
                args.Length > 2 ? args[2].ToNumber() : MsFromTime(t));
            return StoreTime(self, TimeClip(MakeDate(Day(t), time)));
        }));
        dateProto.Set("setUTCHours", Fn(scope, "setUTCHours", 4, (self, args) =>
        {
            double t = MsOf(self);
            double time = MakeTime(Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : MinFromTime(t),
                args.Length > 2 ? args[2].ToNumber() : SecFromTime(t),
                args.Length > 3 ? args[3].ToNumber() : MsFromTime(t));
            return StoreTime(self, TimeClip(MakeDate(Day(t), time)));
        }));
        dateProto.Set("setUTCDate", Fn(scope, "setUTCDate", 1, (self, args) =>
        {
            double t = MsOf(self);
            double day = MakeDay(YearFromTime(t), MonthFromTime(t), Arg(args, 0));
            return StoreTime(self, TimeClip(MakeDate(day, TimeWithinDay(t))));
        }));
        dateProto.Set("setUTCMonth", Fn(scope, "setUTCMonth", 2, (self, args) =>
        {
            double t = MsOf(self);
            double day = MakeDay(YearFromTime(t), Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : DateFromTime(t));
            return StoreTime(self, TimeClip(MakeDate(day, TimeWithinDay(t))));
        }));
        dateProto.Set("setUTCFullYear", Fn(scope, "setUTCFullYear", 3, (self, args) =>
        {
            double t = MsOf(self);
            if (double.IsNaN(t)) t = 0;
            double day = MakeDay(Arg(args, 0),
                args.Length > 1 ? args[1].ToNumber() : MonthFromTime(t),
                args.Length > 2 ? args[2].ToNumber() : DateFromTime(t));
            return StoreTime(self, TimeClip(MakeDate(day, TimeWithinDay(t))));
        }));

        // ── string family (implementation-defined format; era NN4 layout) ──
        dateProto.Set("toString", Fn(scope, "toString", (s, a) =>
        {
            double ms = MsOf(s);
            return JsValue.From(double.IsNaN(ms) ? "Invalid Date"
                : DateToStringStyle(LocalTime(ms), utcStyle: false));
        }));
        dateProto.Set("toDateString", Fn(scope, "toDateString", (s, a) =>
        {
            double ms = MsOf(s);
            return JsValue.From(double.IsNaN(ms) ? "Invalid Date"
                : DateDatePart(LocalTime(ms)));
        }));
        dateProto.Set("toTimeString", Fn(scope, "toTimeString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            double local = LocalTime(ms);
            double off = local - ms;   // LocalTZA + DST in ms
            string sign = off < 0 ? "-" : "+";
            double abs = Math.Abs(off);
            return JsValue.From(
                $"{TwoDigits(HourFromTime(local))}:{TwoDigits(MinFromTime(local))}:" +
                $"{TwoDigits(SecFromTime(local))} GMT{sign}" +
                $"{TwoDigits(Math.Floor(abs / MsPerHour))}" +
                $"{TwoDigits(PosMod(Math.Floor(abs / MsPerMinute), 60))}");
        }));
        dateProto.Set("toLocaleString", Fn(scope, "toLocaleString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            try { return JsValue.From(Epoch.AddMilliseconds(
                    Math.Clamp(LocalTime(ms), -62_135_596_800_000.0, 253_402_300_799_999.0))
                .ToString(CultureInfo.CurrentCulture)); }
            catch { return JsValue.From("Invalid Date"); }
        }));
        var toUtcString = Fn(scope, "toUTCString", (s, a) =>
        {
            double ms = MsOf(s);
            return JsValue.From(double.IsNaN(ms) ? "Invalid Date"
                : DateToStringStyle(ms, utcStyle: true));
        });
        dateProto.Set("toUTCString", toUtcString);
        dateProto.Set("toGMTString", toUtcString);

        var dateCtor = new JsFunction((self, args) =>
        {
            double ms;
            if (args.Length == 0)
            {
                // The era clocks report integral milliseconds; the suite's
                // TIME_NOW-based expectations assume new Date() is integral.
                ms = Math.Truncate((DateTime.UtcNow - Epoch).TotalMilliseconds);
            }
            else if (args.Length == 1)
            {
                if (args[0].Type == JsType.Number)
                {
                    // §15.9.3.2: numeric argument — raw ms, clipped to the
                    // ES3 time range (Infinity/out-of-range becomes NaN).
                    ms = TimeClip(args[0].GetNumber());
                }
                else if (args[0].Type == JsType.String)
                {
                    // Era date-string parsing: "Mon Sep 17 14:23:11 1996" and
                    // "9/17/96" styles — try common formats, fall back to
                    // DateTime.TryParse, then NaN (Invalid Date)
                    var s = args[0].ToJsString().Trim();
                    ms = TryParseEraDate(s, out var d)
                        ? TimeClip((d.ToUniversalTime() - Epoch).TotalMilliseconds)
                        : double.NaN;
                }
                else
                {
                    // §15.9.3.2: any other type — ToNumber first (so
                    // new Date(true) → 1 ms).
                    ms = TimeClip(args[0].ToNumber());
                }
            }
            else
            {
                // §15.9.3.1: components are local time; month/day/hour overflow
                // is normalized by MakeTime/MakeDay (§15.9.1).
                double year = args[0].ToNumber();
                // Two-digit years are 19xx — era scripts wrote new Date(96, 8, 17)
                if (year is >= 0 and <= 99) year += 1900;
                double month = args[1].ToNumber();
                double day   = args.Length > 2 ? args[2].ToNumber() : 1;
                double hour  = args.Length > 3 ? args[3].ToNumber() : 0;
                double minute = args.Length > 4 ? args[4].ToNumber() : 0;
                double second = args.Length > 5 ? args[5].ToNumber() : 0;
                double milli  = args.Length > 6 ? args[6].ToNumber() : 0;
                ms = TimeClip(UtcFromLocal(MakeDate(
                    MakeDay(year, month, day),
                    MakeTime(hour, minute, second, milli))));
            }

            // Genuine `new Date(...)` only mutates self. A plain Date() call
            // receives the GLOBAL object as this and used to write
            // value/Prototype/Class onto it — after one Date() the window
            // stringified as a Date. Per §15.9.2.1 (and the suite's pinned
            // era behaviour) a plain call accepts but IGNORES any arguments
            // and returns the current time as a string.
            if (self.Type is (JsType.Object or JsType.Function) &&
                ReferenceEquals(self.GetObjectOrFunction().Prototype, dateProto))
            {
                var dateObj = self.GetObjectOrFunction();
                dateObj.Class = "Date";
                dateObj.Set("value", JsValue.From(ms));
                dateObj.Attrs ??= new System.Collections.Generic.Dictionary<string, JsObject.PropAttr>();
                dateObj.Attrs["value"] = JsObject.PropAttr.DontEnum | JsObject.PropAttr.DontDelete;
                return self;
            }

            double plain = Math.Truncate((DateTime.UtcNow - Epoch).TotalMilliseconds);
            return JsValue.From(DateToStringForStringify(plain));
        }, scope, "Date", length: 7);

        dateCtor.Set("prototype", JsValue.FromObject(dateProto));
        // §15.9.5.1: Date.prototype.constructor is the Date constructor.
        dateProto.Set("constructor", JsValue.FromFunction(dateCtor));

        // Date.parse(string) → ms
        dateCtor.Set("parse", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            var s = args.Length > 0 ? args[0].ToJsString().Trim() : "";
            return TryParseEraDate(s, out var d)
                ? JsValue.From((d.ToUniversalTime() - Epoch).TotalMilliseconds)
                : JsValue.From(double.NaN);
        }, scope, "parse")));

        dateCtor.Set("UTC", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            // §15.9.4.3: components are UTC; the two-digit rule applies to
            // the year only. NaN components make the result NaN.
            if (args.Length == 0)
                return JsValue.From(double.NaN);

            double year = args[0].ToNumber();
            if (year is >= 0 and <= 99) year += 1900;
            double month = args.Length > 1 ? args[1].ToNumber() : 0;
            double day   = args.Length > 2 ? args[2].ToNumber() : 1;
            double hour  = args.Length > 3 ? args[3].ToNumber() : 0;
            double minute = args.Length > 4 ? args[4].ToNumber() : 0;
            double second = args.Length > 5 ? args[5].ToNumber() : 0;
            double milli  = args.Length > 6 ? args[6].ToNumber() : 0;
            return JsValue.From(TimeClip(MakeDate(
                MakeDay(year, month, day),
                MakeTime(hour, minute, second, milli))));
        }, scope, "UTC", length: 7)));

        scope.Define("Date", JsValue.FromFunction(dateCtor));
    }

    private static bool TryParseEraDate(string s, out DateTime date)
    {
        date = default;
        // "13:30 AM" / "13:30 PM" are invalid 12-hour clock readings —
        // .NET's lenient TryParse swallows the meridian, so pre-check them
        // (the suite requires these to produce an Invalid Date).
        var meridian = System.Text.RegularExpressions.Regex.Match(
            s, "(?<!\\d)([0-9]{1,2})\\s*[:.]\\s*[0-9]{2}\\s*([AaPp])\\.?\\s*[Mm]");
        if (meridian.Success && int.Parse(meridian.Groups[1].Value) > 12)
            return false;

        // Navigator's toString format first
        if (DateTime.TryParseExact(s, "ddd MMM dd HH:mm:ss yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            return true;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // RegExp
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterRegExp(JsScope scope, JsObject objectProto)
    {
        var regexProto = new JsObject { Class = "RegExp", Prototype = objectProto };

        regexProto.Set("test", Fn(scope, "test", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            RequireRegExp(self, "RegExp.prototype.test");
            var o = self.GetObjectOrFunction();
            var (source, flags) = (o.Get("source").ToJsString(), o.Get("flags").ToJsString());
            int lastIndex = NormalizeLastIndex(o.Get("lastIndex"));
            string input = args[0].ToJsString();
            if (lastIndex > input.Length) { o.Set("lastIndex", JsValue.From(0)); return JsValue.From(false); }

            var m = CreateJsRegex(source, RegexOptionsFor(flags)).Match(input, lastIndex);
            if (m.Success)
            {
                if (flags.Contains('g'))
                    o.Set("lastIndex", JsValue.From(AdvanceLastIndex(m.Index, m.Length, input.Length)));
                return JsValue.From(true);
            }
            o.Set("lastIndex", JsValue.From(0));
            return JsValue.From(false);
        }));

        regexProto.Set("exec", Fn(scope, "exec", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.Null;
            RequireRegExp(self, "RegExp.prototype.exec");
            var o = self.GetObjectOrFunction();
            var (source, flags) = (o.Get("source").ToJsString(), o.Get("flags").ToJsString());
            int lastIndex = NormalizeLastIndex(o.Get("lastIndex"));
            string input = args[0].ToJsString();
            if (lastIndex > input.Length) { o.Set("lastIndex", JsValue.From(0)); return JsValue.Null; }

            var m = CreateJsRegex(source, RegexOptionsFor(flags)).Match(input, lastIndex);
            if (!m.Success)
            {
                o.Set("lastIndex", JsValue.From(0));
                return JsValue.Null;
            }

            var result = MatchArray(scope, m, input);
            o.Set("lastIndex", JsValue.From(
                flags.Contains('g') ? AdvanceLastIndex(m.Index, m.Length, input.Length) : 0));
            return result;
        }));

        regexProto.Set("toString", Fn(scope, "toString", (self, args) =>
        {
            // §15.10.6.4: TypeError on anything without RegExp shape
            RequireRegExp(self, "RegExp.prototype.toString");
            var o = self.GetObjectOrFunction();
            return JsValue.From($"/{o.Get("source").ToJsString()}/{o.Get("flags").ToJsString()}");
        }));

        /// §15.10.6 builtins demand a real RegExp receiver
        static void RequireRegExp(JsValue self, string who)
        {
            if (self.Type is not (JsType.Object or JsType.Function) ||
                self.GetObjectOrFunction().Class != "RegExp")
                throw new JsTypeErrorException($"{who} called on a non-RegExp");
        }

        // compile(pattern, flags) — the JScript-era recompile-in-place API.
        // JScript 5 returns the recompiled RegExp object itself.
        regexProto.Set("compile", Fn(scope, "compile", (self, args) =>
        {
            if (self.Type is not (JsType.Object or JsType.Function))
                return JsValue.Undefined;
            RequireRegExp(self, "RegExp.prototype.compile");
            var o = self.GetObjectOrFunction();

            string pattern;
            string flags = "";
            if (args.Length > 0 && args[0].Type is (JsType.Object or JsType.Function) &&
                args[0].GetObjectOrFunction().Class == "RegExp")
            {
                var src = args[0].GetObjectOrFunction();
                pattern = src.Get("source").ToJsString();
                flags = src.Get("flags").ToJsString();
            }
            else
            {
                pattern = args.Length > 0 ? args[0].ToJsString() : "";
            }
            if (args.Length > 1 && args[1].Type != JsType.Undefined)
                flags = args[1].ToJsString();

            var valid = new StringBuilder();
            foreach (char c in flags)
            {
                if (c is not ('g' or 'i' or 'm') || valid.ToString().Contains(c))
                    throw new JsSyntaxErrorException($"invalid regular expression flag {c}");
                valid.Append(c);
            }

            o.Class = "RegExp";
            // stored source is '/'-escaped — unescape before reshaping
            ApplyRegExpShape(o, TranslateToNet(pattern), valid.ToString());
            o.Prototype = regexProto;
            return self;
        }));

        var regexpCtor = new JsFunction((self, args) =>
        {
            // §15.10.3.1/§15.10.4.1: a RegExp argument with no flags
            // argument (or an explicit undefined) returns that object
            bool flagsUndefined = args.Length < 2 || args[1].Type == JsType.Undefined;
            if (args.Length > 0 && args[0].Type is (JsType.Object or JsType.Function) &&
                args[0].GetObjectOrFunction().Class == "RegExp" && flagsUndefined)
                return args[0];

            string pattern;
            string flags = flagsUndefined ? "" : args[1].ToJsString();

            if (args.Length > 0 && args[0].Type is (JsType.Object or JsType.Function) &&
                args[0].GetObjectOrFunction().Class == "RegExp")
            {
                // new RegExp(existingRegex) copies the source — the object's
                // ToJsString is "[object Object]", not /pat/flags
                var src = args[0].GetObjectOrFunction();
                pattern = src.Get("source").ToJsString();
                var srcFlags = src.Get("flags").ToJsString();
                foreach (char f in srcFlags)
                    if (!flags.Contains(f)) flags += f;
                pattern = TranslateToNet(pattern);   // stored source is '/'-escaped
            }
            else
            {
                pattern = args.Length > 0 ? args[0].ToJsString() : "";
            }

            var valid = new StringBuilder();
            foreach (char c in flags)
            {
                if (c is not ('g' or 'i' or 'm') || valid.ToString().Contains(c))
                    throw new JsSyntaxErrorException($"invalid regular expression flag {c}");
                valid.Append(c);
            }

            JsObject Build(JsObject regexObj)
            {
                regexObj.Class = "RegExp";
                ApplyRegExpShape(regexObj, pattern, valid.ToString());
                regexObj.Prototype = regexProto;
                return regexObj;
            }

            // Genuine `new RegExp(...)` only — a plain RegExp("x") call
            // returns a NEW regexp (spec) and never touches the global.
            if (self.Type is (JsType.Object or JsType.Function) &&
                ReferenceEquals(self.GetObjectOrFunction().Prototype, regexProto))
            {
                Build(self.GetObjectOrFunction());
                return self;
            }
            return JsValue.FromObject(Build(new JsObject()));
        }, scope, "RegExp", length: 2);
        regexpCtor.Set("prototype", JsValue.FromObject(regexProto));
        scope.Define("RegExp", JsValue.FromFunction(regexpCtor));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Error
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterError(JsScope scope, JsObject objectProto)
    {
        JsValue MakeErrorCtor(string name, JsObject parentPrototype)
        {
            var proto = new JsObject { Class = "Error", Prototype = parentPrototype };
            proto.Set("toString", Fn(scope, "toString", (self, args) =>
            {
                var target = self.GetObjectOrFunction();
                string n = target.Get("name") is { Type: JsType.String } nv ? nv.GetString() : "Error";
                string m = target.Get("message") is { Type: JsType.String } mv ? mv.GetString() : "";
                return JsValue.From(m.Length > 0 ? $"{n}: {m}" : n);
            }));

            var fn = new JsFunction((self, args) =>
            {
                // Genuine `new Error(...)` only — a plain Error("x") call
                // returns a NEW error (spec) instead of writing name/message
                // onto the global object.
                if (self.Type is (JsType.Object or JsType.Function) &&
                    ReferenceEquals(self.GetObjectOrFunction().Prototype, proto))
                {
                    var err = self.GetObjectOrFunction();
                    err.Class = "Error";
                    err.Set("name", JsValue.From(name));
                    err.Set("message", JsValue.From(args.Length > 0 ? args[0].ToJsString() : ""));
                    return self;
                }
                var plain = new JsObject { Class = "Error", Prototype = proto };
                plain.Set("name", JsValue.From(name));
                plain.Set("message", JsValue.From(args.Length > 0 ? args[0].ToJsString() : ""));
                return JsValue.FromObject(plain);
            }, scope, name);
            fn.Set("prototype", JsValue.FromObject(proto));
            proto.Set("constructor", JsValue.FromFunction(fn));
            return JsValue.FromFunction(fn);
        }

        JsValue errorCtor = MakeErrorCtor("Error", objectProto);
        scope.Define("Error", errorCtor);
        JsObject errorPrototype = errorCtor.GetObjectOrFunction()
            .Get("prototype").GetObject();

        scope.Define("TypeError", MakeErrorCtor("TypeError", errorPrototype));
        scope.Define("RangeError", MakeErrorCtor("RangeError", errorPrototype));
        scope.Define("EvalError", MakeErrorCtor("EvalError", errorPrototype));
        scope.Define("ReferenceError", MakeErrorCtor("ReferenceError", errorPrototype));
        scope.Define("SyntaxError", MakeErrorCtor("SyntaxError", errorPrototype));
        scope.Define("URIError", MakeErrorCtor("URIError", errorPrototype));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Global functions
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterGlobalFunctions(JsScope scope)
    {
        // parseInt: scans the valid prefix ("12abc" → 12), honours radix,
        // hex prefix, and octal-style strings exactly like JS 1.1
        scope.Define("parseInt", Fn(scope, "parseInt", 2, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            // §9.0/§7.2: TRIM whitespace including the BOM
            string s = args[0].ToJsString().Trim().Trim('\uFEFF');
            if (s.Length == 0) return JsValue.From(double.NaN);

            int radix = args.Length > 1 && !double.IsNaN(args[1].ToNumber())
                ? (int)args[1].ToNumber() : 0;

            int i = 0;
            bool neg = false;
            if (s[0] is '+' or '-')
            {
                neg = s[0] == '-';
                i = 1;
            }

            if (radix is 0 or 16 && i + 1 < s.Length &&
                s[i] == '0' && (s[i + 1] is 'x' or 'X'))
            {
                i += 2;
                radix = 16;
            }
            if (radix == 0)
            {
                // §15.1.2.2: radix 0 means 10 — unless an 0x prefix says 16.
                // (The JS1.1 octal reading is gone: the suite and JScript 5
                // both treat "077" as decimal 77.)
                radix = 10;
            }
            if (radix < 2 || radix > 36) return JsValue.From(double.NaN);

            // §15.1.2.2: the math is on the MATHEMATICAL integer value —
            // accumulate exactly (ulong, spilling to BigInteger), then a
            // single round-to-nearest conversion to double
            ulong acc = 0;
            bool big = false;
            var bi = System.Numerics.BigInteger.Zero;
            int digits = 0;
            while (i < s.Length)
            {
                int d = s[i] switch
                {
                    >= '0' and <= '9' => s[i] - '0',
                    >= 'a' and <= 'z' => s[i] - 'a' + 10,
                    >= 'A' and <= 'Z' => s[i] - 'A' + 10,
                    _ => -1
                };
                if (d < 0 || d >= radix) break;
                if (!big)
                {
                    if (acc <= (ulong.MaxValue - 35UL) / (ulong)radix)
                        acc = acc * (ulong)radix + (ulong)d;
                    else
                    {
                        big = true;
                        bi = acc;
                        bi = bi * radix + d;
                    }
                }
                else
                    bi = bi * radix + d;
                digits++;
                i++;
            }

            if (digits == 0) return JsValue.From(double.NaN);
            double val = big ? (double)bi : (double)acc;   // absurd runs saturate to ±∞
            return JsValue.From(neg ? -val : val);
        }));

        // parseFloat: scans the valid numeric prefix ("3.14abc" → 3.14)
        scope.Define("parseFloat", Fn(scope, "parseFloat", 1, (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            string s = args[0].ToJsString().Trim();
            if (s.Length == 0) return JsValue.From(double.NaN);

            // ±Infinity
            string body = s.StartsWith('+') ? s[1..] : s;
            if (body.StartsWith("Infinity", StringComparison.Ordinal))
                return JsValue.From(s.StartsWith('-')
                    ? double.NegativeInfinity : double.PositiveInfinity);

            int end = 0;
            bool sawDot = false, sawExp = false, sawDigit = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsDigit(c)) { sawDigit = true; end = i + 1; }
                else if (c == '.' && !sawDot && !sawExp) { sawDot = true; end = i + 1; }
                else if ((c == 'e' || c == 'E') && sawDigit && !sawExp)
                {
                    // only an exponent if followed by digits (or sign+digits)
                    int j = i + 1;
                    if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
                    if (j < s.Length && char.IsDigit(s[j]))
                    {
                        sawExp = true;
                        while (j < s.Length && char.IsDigit(s[j])) j++;
                        end = j;
                    }
                    else break;
                }
                else if ((c == '+' || c == '-') && i == 0) { end = i + 1; }
                else break;
            }

            string numPart = s[..end];
            if (!sawDigit || numPart.Length == 0)
                return JsValue.From(double.NaN);
            if (double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                return JsValue.From(r);
            return JsValue.From(double.NaN);
        }));

        scope.Define("isNaN", Fn(scope, "isNaN", 1, (self, args) =>
            JsValue.From(double.IsNaN(args.Length > 0 ? args[0].ToNumber() : double.NaN))));

        scope.Define("isFinite", Fn(scope, "isFinite", 1, (self, args) =>
        {
            double n = args.Length > 0 ? args[0].ToNumber() : double.NaN;
            return JsValue.From(!double.IsNaN(n) && !double.IsInfinity(n));
        }));

        // escape/unescape — JS 1.1 semantics: alphanumeric plus @*+-./_
        // pass through, everything else becomes %XX (Latin-1); unescape
        // also understands %uXXXX
        scope.Define("escape", Fn(scope, "escape", 1, (self, args) =>
        {
            var s = args.Length > 0 ? args[0].ToJsString() : "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                        or '@' or '*' or '_' or '+' or '-' or '.' or '/')
                    sb.Append(c);
                else if (c < 256)
                    sb.Append(CultureInfo.InvariantCulture, $"%{(int)c:X2}");
                else
                    sb.Append(CultureInfo.InvariantCulture, $"%u{(int)c:X4}");
            }
            return JsValue.From(sb.ToString());
        }));

        scope.Define("unescape", Fn(scope, "unescape", 1, (self, args) =>
        {
            var s = args.Length > 0 ? args[0].ToJsString() : "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%')
                {
                    if (i + 1 < s.Length && s[i + 1] == 'u' &&
                        TryHex(s, i + 2, 4, out int code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                    if (TryHex(s, i + 1, 2, out int b))
                    {
                        sb.Append((char)b);
                        i += 2;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            return JsValue.From(sb.ToString());
        }));

        scope.Define("encodeURI", Fn(scope, "encodeURI", (self, args) =>
            JsValue.From(EncodeUri(args.Length > 0 ? args[0].ToJsString() : "", component: false))));

        scope.Define("encodeURIComponent", Fn(scope, "encodeURIComponent", (self, args) =>
            JsValue.From(EncodeUri(args.Length > 0 ? args[0].ToJsString() : "", component: true))));

        scope.Define("decodeURI", Fn(scope, "decodeURI", (self, args) =>
            JsValue.From(DecodeUri(args.Length > 0 ? args[0].ToJsString() : "", component: false))));

        scope.Define("decodeURIComponent", Fn(scope, "decodeURIComponent", (self, args) =>
            JsValue.From(DecodeUri(args.Length > 0 ? args[0].ToJsString() : ""))));

        // NaN / Infinity globals (NN3 allowed them bare)
        scope.Define("NaN", JsValue.From(double.NaN));
        scope.Define("Infinity", JsValue.From(double.PositiveInfinity));
    }

    private static string EncodeUri(string value, bool component)
    {
        const string componentSafe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()";
        const string uriSafe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'();,/?:@&=+$#";
        string safe = component ? componentSafe : uriSafe;
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            char c = (char)b;
            if (b < 0x80 && safe.IndexOf(c) >= 0)
                result.Append(c);
            else
                result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static string DecodeUri(string value) => DecodeUri(value, component: true);

    /// <summary>
    /// ES3 §15.1.3 decode — the shared algorithm behind decodeURI and
    /// decodeURIComponent. Malformed sequences, overlong UTF-8 forms and
    /// surrogate halves throw URIError; decodeURI additionally leaves the
    /// uriReserved characters (§15.1.3.1: ";/?:@&=+$,#") percent-encoded.
    /// </summary>
    private static string DecodeUri(string value, bool component)
    {
        const string reserved = ";/?:@&=+$,#";
        var result = new StringBuilder(value.Length);
        int k = 0;
        while (k < value.Length)
        {
            char c = value[k];
            if (c != '%') { result.Append(c); k++; continue; }

            if (!TryHex(value, k + 1, 2, out int b0))
                throw new JsUriErrorException("Malformed URI sequence");

            if (b0 < 0x80)
            {
                char dec = (char)b0;
                if (component || reserved.IndexOf(dec) < 0)
                    result.Append(dec);
                else
                    result.Append(value, k, 3);   // keep %XX for reserved chars
                k += 3;
                continue;
            }

            // Multi-byte UTF-8: determine the leading-byte class
            int n = b0 switch
            {
                >= 0xC2 and <= 0xDF => 1,
                >= 0xE0 and <= 0xEF => 2,
                >= 0xF0 and <= 0xF4 => 3,
                _ => -1
            };
            if (n < 0)
                throw new JsUriErrorException("Malformed URI sequence");   // continuation/C0/C1/F5+ lead

            int cp = b0 & (0x3F >> n);           // mask off the leading 1-bits
            for (int j = 0; j < n; j++)
            {
                int at = k + 3 + 3 * j;
                if (at >= value.Length || value[at] != '%' ||
                    !TryHex(value, at + 1, 2, out int cont) ||
                    (cont & 0xC0) != 0x80)
                    throw new JsUriErrorException("Malformed URI sequence");
                cp = (cp << 6) | (cont & 0x3F);
            }

            // Overlong forms are rejected: each class has a minimum code point
            int min = n == 1 ? 0x80 : n == 2 ? 0x800 : 0x10000;
            if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                throw new JsUriErrorException("Malformed URI sequence");

            if (cp < 0x10000)
            {
                result.Append((char)cp);
            }
            else
            {
                cp -= 0x10000;
                result.Append((char)(0xD800 + (cp >> 10)))
                      .Append((char)(0xDC00 + (cp & 0x3FF)));
            }
            k += 3 + 3 * n;
        }
        return result.ToString();
    }

    private static bool TryHex(string s, int start, int count, out int value)
    {
        value = 0;
        if (start + count > s.Length) return false;
        for (int i = 0; i < count; i++)
        {
            int d = s[start + i] switch
            {
                >= '0' and <= '9' => s[start + i] - '0',
                >= 'a' and <= 'f' => s[start + i] - 'a' + 10,
                >= 'A' and <= 'F' => s[start + i] - 'A' + 10,
                _ => -1
            };
            if (d < 0) return false;
            value = value * 16 + d;
        }
        return true;
    }
}