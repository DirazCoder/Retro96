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
        var functionProto = new JsObject { Class = "Function" };
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
    }

    private static JsValue Fn(JsScope scope, string name,
                              Func<JsValue, JsValue[], JsValue> impl) =>
        JsValue.FromFunction(new JsFunction(impl, scope, name));

    private static double Num(JsValue v) => v.ToNumber();
    private static string Str(JsValue v) => v.ToJsString();
    private static double Arg(JsValue[] a, int i) => i < a.Length ? a[i].ToNumber() : double.NaN;
    private static string ArgStr(JsValue[] a, int i, string def = "") =>
        i < a.Length ? a[i].ToJsString() : def;

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
            JsValue.From("[object Object]")));

        objProto.Set("toLocaleString", Fn(scope, "toLocaleString", (self, args) =>
            JsValue.From(self.ToJsString())));

        objProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => self));

        objProto.Set("hasOwnProperty", Fn(scope, "hasOwnProperty", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            if (self.Type is not (JsType.Object or JsType.Function)) return JsValue.From(false);
            string key = args[0].ToJsString();
            if (!self.GetObjectOrFunction().HasOwn(key)) return JsValue.From(false);
            return JsValue.From(key is not ("length" or "name" or "prototype" or "constructor"));
        }));

        objProto.Set("propertyIsEnumerable", Fn(scope, "propertyIsEnumerable", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            if (self.Type is not (JsType.Object or JsType.Function)) return JsValue.From(false);
            return JsValue.From(self.GetObjectOrFunction().HasOwn(args[0].ToJsString()));
        }));

        objProto.Set("isPrototypeOf", Fn(scope, "isPrototypeOf", (self, args) =>
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
            if (args.Length > 0 && args[0].Type == JsType.Object)
                return args[0];
            var o = new JsObject { Prototype = objProto };
            if (args.Length > 0)
            {
                o.Class = args[0].Type switch
                {
                    JsType.String => "String",
                    JsType.Number => "Number",
                    JsType.Boolean => "Boolean",
                    _ => "Object"
                };
                o.Set("value", args[0]);
            }
            return JsValue.FromObject(o);
        }, scope, "Object");
        objectCtor.Set("prototype", JsValue.FromObject(objProto));
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
        }, scope, "Function");
        constructor.Prototype = functionProto;
        constructor.Set("prototype", JsValue.FromObject(functionProto));
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

        arrProto.Set("push", Fn(scope, "push", (self, args) =>
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

        arrProto.Set("unshift", Fn(scope, "unshift", (self, args) =>
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

        arrProto.Set("join", Fn(scope, "join", (self, args) =>
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

        arrProto.Set("slice", Fn(scope, "slice", (self, args) =>
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

        arrProto.Set("concat", Fn(scope, "concat", (self, args) =>
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

        arrProto.Set("splice", Fn(scope, "splice", (self, args) =>
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

        arrProto.Set("indexOf", Fn(scope, "indexOf", (self, args) =>
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
                double n = args[0].ToNumber();
                if (double.IsNaN(n) || double.IsInfinity(n) || n < 0 || n != Math.Truncate(n) || n > int.MaxValue)
                    throw new JsInterpreterException("Invalid array length");
                length = (int)n;
            }
            else
            {
                foreach (var arg in args)
                {
                    newArr.Set((length++).ToString(), arg);
                }
            }

            newArr.Set("length", JsValue.From(length));
            return JsValue.FromObject(newArr);
        }, scope, "Array");
        arrayCtor.Set("prototype", JsValue.FromObject(arrProto));
        scope.Define("Array", JsValue.FromFunction(arrayCtor));

        return arrProto;
    }

    // ─────────────────────────────────────────────────────────────────────
    // String
    // ─────────────────────────────────────────────────────────────────────

    private static JsObject RegisterString(JsScope scope, JsObject objectProto)
    {
        var strProto = new JsObject { Class = "String", Prototype = objectProto };

        strProto.Set("charAt", Fn(scope, "charAt", (self, args) =>
        {
            var str = self.ToJsString();
            int index = args.Length > 0 && !double.IsNaN(args[0].ToNumber()) ? (int)args[0].ToNumber() : 0;
            if (index < 0 || index >= str.Length) return JsValue.From("");
            return JsValue.From(str[index].ToString());
        }));

        strProto.Set("charCodeAt", Fn(scope, "charCodeAt", (self, args) =>
        {
            var str = self.ToJsString();
            int index = args.Length > 0 && !double.IsNaN(args[0].ToNumber()) ? (int)args[0].ToNumber() : 0;
            if (index < 0 || index >= str.Length) return JsValue.From(double.NaN);
            return JsValue.From((double)str[index]);
        }));

        strProto.Set("indexOf", Fn(scope, "indexOf", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var str = self.ToJsString();
            var search = args[0].ToJsString();
            int from = args.Length > 1 ? Math.Max((int)args[1].ToNumber(), 0) : 0;
            return JsValue.From(str.IndexOf(search, from, StringComparison.Ordinal));
        }));

        strProto.Set("lastIndexOf", Fn(scope, "lastIndexOf", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var str = self.ToJsString();
            var search = args[0].ToJsString();
            return JsValue.From(str.LastIndexOf(search, StringComparison.Ordinal));
        }));

        strProto.Set("substring", Fn(scope, "substring", (self, args) =>
        {
            var str = self.ToJsString();
            int start = args.Length > 0 && !double.IsNaN(args[0].ToNumber()) ? (int)args[0].ToNumber() : 0;
            int end = args.Length > 1 && !double.IsNaN(args[1].ToNumber())
                ? (int)args[1].ToNumber() : str.Length;

            start = Math.Clamp(start, 0, str.Length);
            end   = Math.Clamp(end, 0, str.Length);
            if (start > end) (start, end) = (end, start);

            return JsValue.From(str[start..end]);
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

        strProto.Set("slice", Fn(scope, "slice", (self, args) =>
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
            JsValue.From(self.ToJsString().ToLowerInvariant())));
        strProto.Set("toUpperCase", Fn(scope, "toUpperCase", (self, args) =>
            JsValue.From(self.ToJsString().ToUpperInvariant())));

        strProto.Set("split", Fn(scope, "split", (self, args) =>
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
                parts = new Regex(source, RegexOptionsFor(flags)).Split(str);
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

        strProto.Set("concat", Fn(scope, "concat", (self, args) =>
        {
            var sb = new StringBuilder(self.ToJsString());
            foreach (var arg in args) sb.Append(arg.ToJsString());
            return JsValue.From(sb.ToString());
        }));

        strProto.Set("match", Fn(scope, "match", (self, args) =>
        {
            if (args.Length == 0) return JsValue.Null;
            var str = self.ToJsString();
            var (source, flags) = RegexParts(args[0]);
            var options = RegexOptionsFor(flags);
            if (!flags.Contains('g'))
            {
                // Non-global: single-match array or null
                var m = new Regex(source, options).Match(str);
                return m.Success ? MatchArray(scope, m, str) : JsValue.Null;
            }

            var matches = new Regex(source, options).Matches(str);
            if (matches.Count == 0) return JsValue.Null;
            var result = NewArray(scope);
            int n = 0;
            foreach (Match m in matches)
                result.Set((n++).ToString(), JsValue.From(m.Value));
            result.Set("length", JsValue.From(n));
            return JsValue.FromObject(result);
        }));

        strProto.Set("replace", Fn(scope, "replace", (self, args) =>
        {
            if (args.Length < 2) return self;
            var str = self.ToJsString();
            var replacement = args[1].ToJsString();

            if (args[0].Type is (JsType.Object or JsType.Function) &&
                args[0].GetObjectOrFunction().Class == "RegExp")
            {
                var (source, flags) = RegexParts(args[0]);
                var regex = new Regex(source, RegexOptionsFor(flags));
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

        strProto.Set("search", Fn(scope, "search", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(-1);
            var str = self.ToJsString();
            var (source, flags) = RegexParts(args[0]);
            var m = new Regex(source, RegexOptionsFor(flags)).Match(str);
            return JsValue.From(m.Success ? m.Index : -1);
        }));

        strProto.Set("toString", Fn(scope, "toString", (self, args) =>
            JsValue.From(self.ToJsString())));
        strProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => self));

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
                sb.Append((char)(int)arg.ToNumber());
            return JsValue.From(sb.ToString());
        }, scope, "fromCharCode");

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
                strObj.Set("length", JsValue.From(text.Length));
                return self;
            }
            return JsValue.From(text);
        }, scope, "String");
        stringCtor.Set("prototype", JsValue.FromObject(strProto));
        stringCtor.Set("fromCharCode", JsValue.FromFunction(fromCharCode));
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
                throw new JsInterpreterException("radix out of range");
            if (radix == 10)
                return JsValue.From(JsValue.NumberToString(num));
            return JsValue.From(ConvertToBase(num, radix));
        }));

        numProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => self));

        // ── ES3 / JScript 5.0 number formatting (checklist §12) ──
        // toFixed — fixed-point with era round-half-away-from-zero on the
        // double's true binary value (so 1.005.toFixed(2) === "1.00", the
        // classic binary-representation behaviour both JScript 5 and
        // ECMAScript implementations exhibit).
        numProto.Set("toFixed", Fn(scope, "toFixed", (self, args) =>
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
                    throw new JsInterpreterException("toFixed() digits argument must be between 0 and 100");
                digits = (int)d;
            }
            // ES3: |x| ≥ 10^21 returns the plain (exponential) ToString form.
            if (Math.Abs(num) >= 1e21)
                return JsValue.From(JsValue.NumberToString(num));
            return JsValue.From(FixedString(num, digits));
        }));

        numProto.Set("toExponential", Fn(scope, "toExponential", (self, args) =>
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
                    throw new JsInterpreterException("toExponential() fraction digits argument must be between 0 and 100");
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

        numProto.Set("toPrecision", Fn(scope, "toPrecision", (self, args) =>
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
                throw new JsInterpreterException("toPrecision() argument must be between 1 and 100");
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
                return self;
            }
            return JsValue.From(value);
        }, scope, "Number");
        numberCtor.Set("prototype", JsValue.FromObject(numProto));
        numberCtor.Set("MAX_VALUE", JsValue.From(double.MaxValue));
        numberCtor.Set("MIN_VALUE", JsValue.From(double.Epsilon));
        numberCtor.Set("NaN", JsValue.From(double.NaN));
        numberCtor.Set("POSITIVE_INFINITY", JsValue.From(double.PositiveInfinity));
        numberCtor.Set("NEGATIVE_INFINITY", JsValue.From(double.NegativeInfinity));
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
            JsValue.From(self.ToBoolean() ? "true" : "false")));
        boolProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => self));

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
                return self;
            }
            return JsValue.From(value);
        }, scope, "Boolean");
        boolCtor.Set("prototype", JsValue.FromObject(boolProto));
        scope.Define("Boolean", JsValue.FromFunction(boolCtor));

        return boolProto;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Math
    // ─────────────────────────────────────────────────────────────────────

    private static void RegisterMath(JsScope scope)
    {
        var math = new JsObject();

        math.Set("E",       JsValue.From(Math.E));
        math.Set("PI",      JsValue.From(Math.PI));
        math.Set("LN2",     JsValue.From(Math.Log(2)));
        math.Set("LN10",    JsValue.From(Math.Log(10)));
        math.Set("LOG2E",   JsValue.From(1.0 / Math.Log(2)));
        math.Set("LOG10E",  JsValue.From(1.0 / Math.Log(10)));
        math.Set("SQRT2",   JsValue.From(Math.Sqrt(2)));
        math.Set("SQRT1_2", JsValue.From(Math.Sqrt(0.5)));

        math.Set("abs",   Fn(scope, "abs",   (s, a) => JsValue.From(Math.Abs(Arg(a, 0)))));
        math.Set("ceil",  Fn(scope, "ceil",  (s, a) => JsValue.From(Math.Ceiling(Arg(a, 0)))));
        math.Set("floor", Fn(scope, "floor", (s, a) => JsValue.From(Math.Floor(Arg(a, 0)))));
        math.Set("round", Fn(scope, "round", (s, a) =>
        {
            double v = Arg(a, 0);
            // JS rounds .5 up (toward +Infinity), unlike .NET's banker's rounding
            return JsValue.From(Math.Floor(v + 0.5));
        }));
        math.Set("min",   Fn(scope, "min",   (s, a) =>
        {
            double min = double.PositiveInfinity;
            foreach (var v in a) if (v.ToNumber() < min) min = v.ToNumber();
            return JsValue.From(min);
        }));
        math.Set("max",   Fn(scope, "max",   (s, a) =>
        {
            double max = double.NegativeInfinity;
            foreach (var v in a) if (v.ToNumber() > max) max = v.ToNumber();
            return JsValue.From(max);
        }));
        math.Set("pow",   Fn(scope, "pow",   (s, a) => JsValue.From(Math.Pow(Arg(a, 0), Arg(a, 1)))));
        math.Set("sqrt",  Fn(scope, "sqrt",  (s, a) => JsValue.From(Math.Sqrt(Arg(a, 0)))));
        math.Set("log",   Fn(scope, "log",   (s, a) => JsValue.From(Math.Log(Arg(a, 0)))));
        math.Set("exp",   Fn(scope, "exp",   (s, a) => JsValue.From(Math.Exp(Arg(a, 0)))));
        math.Set("sin",   Fn(scope, "sin",   (s, a) => JsValue.From(Math.Sin(Arg(a, 0)))));
        math.Set("cos",   Fn(scope, "cos",   (s, a) => JsValue.From(Math.Cos(Arg(a, 0)))));
        math.Set("tan",   Fn(scope, "tan",   (s, a) => JsValue.From(Math.Tan(Arg(a, 0)))));
        math.Set("asin",  Fn(scope, "asin",  (s, a) => JsValue.From(Math.Asin(Arg(a, 0)))));
        math.Set("acos",  Fn(scope, "acos",  (s, a) => JsValue.From(Math.Acos(Arg(a, 0)))));
        math.Set("atan",  Fn(scope, "atan",  (s, a) => JsValue.From(Math.Atan(Arg(a, 0)))));
        math.Set("atan2", Fn(scope, "atan2", (s, a) => JsValue.From(Math.Atan2(Arg(a, 0), Arg(a, 1)))));
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
        if (self.Type is (JsType.Object or JsType.Function) &&
            self.GetObjectOrFunction().Get("value") is { Type: JsType.Number } v)
            return v.GetNumber();
        return double.NaN;
    }

    private static DateTime LocalOf(JsValue self)
        => Epoch.AddMilliseconds(MsOf(self)).ToLocalTime();

    private static DateTime UtcOf(JsValue self)
        => Epoch.AddMilliseconds(MsOf(self));

    /// <summary>
    /// Shared implementation of the ES3 Date setters: reads the current local
    /// time, replaces the components whose argument index was supplied (the
    /// others keep their current values), writes the new epoch-ms back onto
    /// the Date object and returns the new time value. ECMAScript's
    /// month/day/hour overflow is preserved by building from DateTime(1,1)
    /// and adding the raw component deltas.
    /// </summary>
    private static double SetDateParts(JsValue self, JsValue[] args,
        int year = -1, int month = -1, int day = -1,
        int hour = -1, int minute = -1, int second = -1, int milli = -1)
    {
        double ms = MsOf(self);
        if (double.IsNaN(ms)) return double.NaN;

        var current = Epoch.AddMilliseconds(ms).ToLocalTime();

        int ArgAt(int index) =>
            index >= 0 && index < args.Length ? (int)args[index].ToNumber() : int.MinValue;

        int yv = ArgAt(year);
        int y = yv != int.MinValue ? yv : current.Year;
        if (yv != int.MinValue && y >= 0 && y <= 99) y += 1900;   // era two-digit rule
        int mo = ArgAt(month); if (mo == int.MinValue) mo = current.Month - 1;
        int d  = ArgAt(day);   if (d  == int.MinValue) d  = current.Day;
        int h  = ArgAt(hour);  if (h  == int.MinValue) h  = current.Hour;
        int mi = ArgAt(minute); if (mi == int.MinValue) mi = current.Minute;
        int se = ArgAt(second); if (se == int.MinValue) se = current.Second;
        int ml = ArgAt(milli); if (ml == int.MinValue) ml = current.Millisecond;

        double newMs;
        try
        {
            var rebuilt = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Local)
                .AddYears(y - 1)
                .AddMonths(mo)
                .AddDays(d - 1)
                .AddHours(h)
                .AddMinutes(mi)
                .AddSeconds(se)
                .AddMilliseconds(ml);
            newMs = (rebuilt.ToUniversalTime() - Epoch).TotalMilliseconds;
        }
        catch { return double.NaN; }

        if (self.Type is (JsType.Object or JsType.Function))
            self.GetObjectOrFunction().Set("value", JsValue.From(newMs));
        return newMs;
    }

    private static void RegisterDate(JsScope scope, JsObject objectProto)
    {
        var dateProto = new JsObject { Class = "Date", Prototype = objectProto };

        dateProto.Set("getTime", Fn(scope, "getTime", (self, args) => JsValue.From(MsOf(self))));
        dateProto.Set("valueOf", Fn(scope, "valueOf", (self, args) => JsValue.From(MsOf(self))));

        dateProto.Set("getFullYear", Fn(scope, "getFullYear", (s, a) => JsValue.From(LocalOf(s).Year)));
        dateProto.Set("getMonth",    Fn(scope, "getMonth",    (s, a) => JsValue.From(LocalOf(s).Month - 1)));
        dateProto.Set("getDate",     Fn(scope, "getDate",     (s, a) => JsValue.From(LocalOf(s).Day)));
        dateProto.Set("getDay",      Fn(scope, "getDay",      (s, a) => JsValue.From((int)LocalOf(s).DayOfWeek)));
        dateProto.Set("getHours",   Fn(scope, "getHours",    (s, a) => JsValue.From(LocalOf(s).Hour)));
        dateProto.Set("getMinutes",  Fn(scope, "getMinutes",  (s, a) => JsValue.From(LocalOf(s).Minute)));
        dateProto.Set("getSeconds",  Fn(scope, "getSeconds",  (s, a) => JsValue.From(LocalOf(s).Second)));
        dateProto.Set("getMilliseconds", Fn(scope, "getMilliseconds", (s, a) => JsValue.From(LocalOf(s).Millisecond)));

        // getYear — the era's method: year minus 1900
        dateProto.Set("getYear", Fn(scope, "getYear", (s, a) =>
            JsValue.From(LocalOf(s).Year - 1900)));

        // ── UTC getters (JScript 5 / JavaScript 1.3, ES3) ──
        dateProto.Set("getUTCFullYear",   Fn(scope, "getUTCFullYear",   (s, a) => JsValue.From(UtcOf(s).Year)));
        dateProto.Set("getUTCMonth",      Fn(scope, "getUTCMonth",      (s, a) => JsValue.From(UtcOf(s).Month - 1)));
        dateProto.Set("getUTCDate",       Fn(scope, "getUTCDate",       (s, a) => JsValue.From(UtcOf(s).Day)));
        dateProto.Set("getUTCDay",        Fn(scope, "getUTCDay",        (s, a) => JsValue.From((int)UtcOf(s).DayOfWeek)));
        dateProto.Set("getUTCHours",      Fn(scope, "getUTCHours",      (s, a) => JsValue.From(UtcOf(s).Hour)));
        dateProto.Set("getUTCMinutes",    Fn(scope, "getUTCMinutes",    (s, a) => JsValue.From(UtcOf(s).Minute)));
        dateProto.Set("getUTCSeconds",    Fn(scope, "getUTCSeconds",    (s, a) => JsValue.From(UtcOf(s).Second)));
        dateProto.Set("getUTCMilliseconds", Fn(scope, "getUTCMilliseconds", (s, a) => JsValue.From(UtcOf(s).Millisecond)));

        // ── Local setters (ES3 / JScript 5 — Y2K remediation surface) ──
        dateProto.Set("setFullYear", Fn(scope, "setFullYear", (self, args) =>
            JsValue.From(SetDateParts(self, args,
                year: 0, month: 1, day: 2))));
        dateProto.Set("setMonth", Fn(scope, "setMonth", (self, args) =>
            JsValue.From(SetDateParts(self, args, month: 0, day: 1))));
        dateProto.Set("setDate", Fn(scope, "setDate", (self, args) =>
            JsValue.From(SetDateParts(self, args, day: 0))));
        dateProto.Set("setHours", Fn(scope, "setHours", (self, args) =>
            JsValue.From(SetDateParts(self, args, hour: 0, minute: 1, second: 2, milli: 3))));
        dateProto.Set("setMinutes", Fn(scope, "setMinutes", (self, args) =>
            JsValue.From(SetDateParts(self, args, minute: 0, second: 1, milli: 2))));
        dateProto.Set("setSeconds", Fn(scope, "setSeconds", (self, args) =>
            JsValue.From(SetDateParts(self, args, second: 0, milli: 1))));
        dateProto.Set("setMilliseconds", Fn(scope, "setMilliseconds", (self, args) =>
            JsValue.From(SetDateParts(self, args, milli: 0))));

        dateProto.Set("toDateString", Fn(scope, "toDateString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            // ECMAScript-ish "Fri Mar 05 1999"
            return JsValue.From(LocalOf(s).ToString(
                "ddd MMM dd yyyy", CultureInfo.InvariantCulture));
        }));

        dateProto.Set("toTimeString", Fn(scope, "toTimeString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            var local = LocalOf(s);
            var offset = TimeZoneInfo.Local.GetUtcOffset(local);
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            var abs = offset < TimeSpan.Zero ? -offset : offset;
            string off = $"{sign}{abs.Hours:00}{abs.Minutes:00}";
            return JsValue.From(local.ToString(
                "HH:mm:ss", CultureInfo.InvariantCulture) + " GMT" + off);
        }));


        dateProto.Set("setTime", Fn(scope, "setTime", (self, args) =>
        {
            double ms = Arg(args, 0);
            if (self.Type is (JsType.Object or JsType.Function))
                self.GetObjectOrFunction().Set("value", JsValue.From(ms));
            return JsValue.From(ms);
        }));

        dateProto.Set("toString", Fn(scope, "toString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            // Navigator format: "Mon Sep 17 14:23:11 1996"
            return JsValue.From(LocalOf(s).ToString(
                "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture));
        }));

        dateProto.Set("toLocaleString", Fn(scope, "toLocaleString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            return JsValue.From(LocalOf(s).ToString(CultureInfo.CurrentCulture));
        }));

        dateProto.Set("toGMTString", Fn(scope, "toGMTString", (s, a) =>
        {
            double ms = MsOf(s);
            if (double.IsNaN(ms)) return JsValue.From("Invalid Date");
            return JsValue.From(Epoch.AddMilliseconds(ms).ToString(
                "ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture));
        }));

        var dateCtor = new JsFunction((self, args) =>
        {
            double ms;
            if (args.Length == 0)
            {
                ms = (DateTime.UtcNow - Epoch).TotalMilliseconds;
            }
            else if (args.Length == 1)
            {
                if (args[0].Type == JsType.Number)
                {
                    ms = args[0].GetNumber();
                }
                else
                {
                    // Era date-string parsing: "Mon Sep 17 14:23:11 1996" and
                    // "9/17/96" styles — try common formats, fall back to
                    // DateTime.TryParse, then NaN (Invalid Date)
                    var s = args[0].ToJsString().Trim();
                    ms = TryParseEraDate(s, out var d)
                        ? (d.ToUniversalTime() - Epoch).TotalMilliseconds
                        : double.NaN;
                }
            }
            else
            {
                int year = (int)Arg(args, 0);
                // Two-digit years are 19xx — era scripts wrote new Date(96, 8, 17)
                if (year >= 0 && year <= 99) year += 1900;
                int month = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int day = args.Length > 2 ? (int)args[2].ToNumber() : 1;
                int hour = args.Length > 3 ? (int)args[3].ToNumber() : 0;
                int minute = args.Length > 4 ? (int)args[4].ToNumber() : 0;
                int second = args.Length > 5 ? (int)args[5].ToNumber() : 0;
                try
                {
                    // ECMAScript normalizes month/day overflow (e.g. month 12
                    // becomes January of the following year) rather than
                    // treating it as an invalid Date.
                    var d = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local)
                        .AddMonths(month)
                        .AddDays(day - 1)
                        .AddHours(hour)
                        .AddMinutes(minute)
                        .AddSeconds(second);
                    ms = (d.ToUniversalTime() - Epoch).TotalMilliseconds;
                }
                catch { ms = double.NaN; }
            }

            // Genuine `new Date(...)` only mutates self. A plain Date() call
            // receives the GLOBAL object as this and used to write
            // value/Prototype/Class onto it — after one Date() the window
            // stringified as a Date. Per spec, plain Date() returns the
            // current date/time as a STRING and ignores all arguments.
            if (self.Type is (JsType.Object or JsType.Function) &&
                ReferenceEquals(self.GetObjectOrFunction().Prototype, dateProto))
            {
                var dateObj = self.GetObjectOrFunction();
                dateObj.Class = "Date";
                dateObj.Set("value", JsValue.From(ms));
                return self;
            }

            var now = (DateTime.UtcNow - Epoch).TotalMilliseconds;
            return JsValue.From(
                Epoch.AddMilliseconds(now).ToLocalTime().ToString(
                    "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture));
        }, scope, "Date");

        dateCtor.Set("prototype", JsValue.FromObject(dateProto));

        // Date.parse(string) → ms
        dateCtor.Set("parse", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            var s = args.Length > 0 ? args[0].ToJsString().Trim() : "";
            return TryParseEraDate(s, out var d)
                ? JsValue.From((d.ToUniversalTime() - Epoch).TotalMilliseconds)
                : JsValue.From(double.NaN);
        }, scope, "parse")));

        scope.Define("Date", JsValue.FromFunction(dateCtor));
    }

    private static bool TryParseEraDate(string s, out DateTime date)
    {
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

        regexProto.Set("test", Fn(scope, "test", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            var o = self.GetObjectOrFunction();
            var (source, flags) = (o.Get("source").ToJsString(), o.Get("flags").ToJsString());
            int lastIndex = NormalizeLastIndex(o.Get("lastIndex"));
            string input = args[0].ToJsString();
            if (lastIndex > input.Length) { o.Set("lastIndex", JsValue.From(0)); return JsValue.From(false); }

            var m = new Regex(source, RegexOptionsFor(flags)).Match(input, lastIndex);
            if (m.Success)
            {
                if (flags.Contains('g'))
                    o.Set("lastIndex", JsValue.From(AdvanceLastIndex(m.Index, m.Length, input.Length)));
                return JsValue.From(true);
            }
            o.Set("lastIndex", JsValue.From(0));
            return JsValue.From(false);
        }));

        regexProto.Set("exec", Fn(scope, "exec", (self, args) =>
        {
            if (args.Length == 0) return JsValue.Null;
            var o = self.GetObjectOrFunction();
            var (source, flags) = (o.Get("source").ToJsString(), o.Get("flags").ToJsString());
            int lastIndex = NormalizeLastIndex(o.Get("lastIndex"));
            string input = args[0].ToJsString();
            if (lastIndex > input.Length) { o.Set("lastIndex", JsValue.From(0)); return JsValue.Null; }

            var m = new Regex(source, RegexOptionsFor(flags)).Match(input, lastIndex);
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
            var o = self.GetObjectOrFunction();
            return JsValue.From($"/{o.Get("source").ToJsString()}/{o.Get("flags").ToJsString()}");
        }));

        // compile(pattern, flags) — the JScript-era recompile-in-place API.
        // JScript 5 returns the recompiled RegExp object itself.
        regexProto.Set("compile", Fn(scope, "compile", (self, args) =>
        {
            if (self.Type is not (JsType.Object or JsType.Function))
                return JsValue.Undefined;
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
                    throw new JsInterpreterException("Invalid or duplicate RegExp flag");
                valid.Append(c);
            }

            o.Class = "RegExp";
            o.Set("source", JsValue.From(pattern));
            o.Set("flags", JsValue.From(valid.ToString()));
            o.Set("global", JsValue.From(valid.ToString().Contains('g')));
            o.Set("ignoreCase", JsValue.From(valid.ToString().Contains('i')));
            o.Set("multiline", JsValue.From(valid.ToString().Contains('m')));
            o.Set("lastIndex", JsValue.From(0));
            o.Prototype = regexProto;
            return self;
        }));

        var regexpCtor = new JsFunction((self, args) =>
        {
            string pattern;
            string flags = args.Length > 1 ? args[1].ToJsString() : "";

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
            }
            else
            {
                pattern = args.Length > 0 ? args[0].ToJsString() : "";
            }

            var valid = new StringBuilder();
            foreach (char c in flags)
            {
                if (c is not ('g' or 'i' or 'm') || valid.ToString().Contains(c))
                    throw new JsInterpreterException("Invalid or duplicate RegExp flag");
                valid.Append(c);
            }

            JsObject Build(JsObject regexObj)
            {
                regexObj.Class = "RegExp";
                regexObj.Set("source", JsValue.From(pattern));
                regexObj.Set("flags", JsValue.From(valid.ToString()));
                regexObj.Set("global", JsValue.From(valid.ToString().Contains('g')));
                regexObj.Set("ignoreCase", JsValue.From(valid.ToString().Contains('i')));
                regexObj.Set("multiline", JsValue.From(valid.ToString().Contains('m')));
                regexObj.Set("lastIndex", JsValue.From(0));
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
        }, scope, "RegExp");
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
        scope.Define("parseInt", Fn(scope, "parseInt", (self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            string s = args[0].ToJsString().Trim();
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
                // JS 1.1 treated 0-prefixed digit runs as octal
                radix = 10;
                if (i < s.Length && s[i] == '0')
                {
                    int j = i;
                    while (j < s.Length && char.IsDigit(s[j])) j++;
                    if (j > i + 1 && s.AsSpan(i, j - i).IndexOfAny('8', '9') < 0)
                    {
                        radix = 8;
                        i++;   // skip the 0
                    }
                }
            }
            if (radix < 2 || radix > 36) return JsValue.From(double.NaN);

            long val = 0;
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
                val = val * radix + d;
                digits++;
                i++;
            }

            if (digits == 0) return JsValue.From(double.NaN);
            return JsValue.From(neg ? -val : val);
        }));

        // parseFloat: scans the valid numeric prefix ("3.14abc" → 3.14)
        scope.Define("parseFloat", Fn(scope, "parseFloat", (self, args) =>
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

        scope.Define("isNaN", Fn(scope, "isNaN", (self, args) =>
            JsValue.From(double.IsNaN(args.Length > 0 ? args[0].ToNumber() : double.NaN))));

        scope.Define("isFinite", Fn(scope, "isFinite", (self, args) =>
        {
            double n = args.Length > 0 ? args[0].ToNumber() : double.NaN;
            return JsValue.From(!double.IsNaN(n) && !double.IsInfinity(n));
        }));

        // escape/unescape — JS 1.1 semantics: alphanumeric plus @*+-./_
        // pass through, everything else becomes %XX (Latin-1); unescape
        // also understands %uXXXX
        scope.Define("escape", Fn(scope, "escape", (self, args) =>
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

        scope.Define("unescape", Fn(scope, "unescape", (self, args) =>
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
            JsValue.From(Uri.UnescapeDataString(args.Length > 0 ? args[0].ToJsString() : ""))));

        scope.Define("decodeURIComponent", Fn(scope, "decodeURIComponent", (self, args) =>
            JsValue.From(Uri.UnescapeDataString(args.Length > 0 ? args[0].ToJsString() : ""))));

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