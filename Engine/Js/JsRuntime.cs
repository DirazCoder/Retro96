using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Js;

public static class JsRuntime
{
    /// <summary>
    /// Populate the global scope with all JS1.1 built-in objects and functions.
    /// Call this once when initializing the JsInterpreter.
    /// </summary>
    public static void PopulateGlobalScope(JsScope globalScope)
    {
        RegisterObject(globalScope);
        RegisterArray(globalScope);
        RegisterString(globalScope);
        RegisterNumber(globalScope);
        RegisterBoolean(globalScope);
        RegisterMath(globalScope);
        RegisterDate(globalScope);
        RegisterRegExp(globalScope);
        RegisterError(globalScope);
        RegisterGlobalFunctions(globalScope);
    }

    // Helper to create a native function JsValue
    private static JsValue CreateNativeFunction(Func<JsValue, JsValue[], JsValue> impl, JsScope scope, string? name = null)
    {
        var func = new JsFunction(impl, scope, name);
        return JsValue.FromFunction(func);
    }

    private static void RegisterObject(JsScope scope)
    {
        var objProto = new JsObject();

        // toString() - returns "[object Object]"
        objProto.Set("toString", CreateNativeFunction((self, args) =>
        {
            return JsValue.From("[object Object]");
        }, scope, "toString"));

        // valueOf() - returns the object itself
        objProto.Set("valueOf", CreateNativeFunction((self, args) =>
        {
            return self;
        }, scope, "valueOf"));

        // hasOwnProperty(name) - checks own properties only
        objProto.Set("hasOwnProperty", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.From(false);
            var name = args[0].ToJsString();
            var obj = self.GetObject();
            return JsValue.From(obj.HasOwn(name));
        }, scope, "hasOwnProperty"));

        // Object constructor (can be called with new or without)
        var objectConstructorValue = CreateNativeFunction((self, args) =>
        {
            var newObj = new JsObject();
            newObj.Prototype = objProto;
            if (args.Length > 0)
            {
                var arg = args[0];
                if (arg.Type == JsType.Object)
                    return arg;
            }
            return JsValue.FromObject(newObj);
        }, scope, "Object");

        // Set properties on the underlying JsFunction (which is a JsObject)
        var objectConstructorObj = objectConstructorValue.GetFunction();
        objectConstructorObj.Set("prototype", JsValue.FromObject(objProto));
        scope.Define("Object", objectConstructorValue);
    }

    private static void RegisterArray(JsScope scope)
    {
        var arrProto = new JsObject();

        // push(...items) - append items, return new length
        arrProto.Set("push", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            foreach (var item in args)
            {
                arr.Set(length.ToString(), item);
                length++;
            }
            var newLength = JsValue.From(length);
            arr.Set("length", newLength);
            return newLength;
        }, scope, "push"));

        // pop() - remove and return last item
        arrProto.Set("pop", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            if (length == 0)
                return JsValue.Undefined;
            length--;
            var lastItem = arr.Get(length.ToString());
            arr.Properties.Remove(length.ToString());
            arr.Set("length", JsValue.From(length));
            return lastItem;
        }, scope, "pop"));

        // shift() - remove and return first item, shift others down
        arrProto.Set("shift", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            if (length == 0)
                return JsValue.Undefined;
            var firstItem = arr.Get("0");
            for (int i = 1; i < length; i++)
            {
                arr.Set((i - 1).ToString(), arr.Get(i.ToString()));
            }
            arr.Properties.Remove((length - 1).ToString());
            arr.Set("length", JsValue.From(length - 1));
            return firstItem;
        }, scope, "shift"));

        // unshift(...items) - prepend items, return new length
        arrProto.Set("unshift", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            int newLength = length + args.Length;
            for (int i = length - 1; i >= 0; i--)
            {
                arr.Set((i + args.Length).ToString(), arr.Get(i.ToString()));
            }
            for (int i = 0; i < args.Length; i++)
            {
                arr.Set(i.ToString(), args[i]);
            }
            arr.Set("length", JsValue.From(newLength));
            return JsValue.From(newLength);
        }, scope, "unshift"));

        // reverse() - reverse array in place, return array
        arrProto.Set("reverse", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            for (int i = 0; i < length / 2; i++)
            {
                var left = arr.Get(i.ToString());
                var right = arr.Get((length - 1 - i).ToString());
                arr.Set(i.ToString(), right);
                arr.Set((length - 1 - i).ToString(), left);
            }
            return self;
        }, scope, "reverse"));

        // sort(compareFn?) - sort array in place, return array
        arrProto.Set("sort", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            if (length <= 1)
                return self;

            var elements = new List<JsValue>();
            for (int i = 0; i < length; i++)
                elements.Add(arr.Get(i.ToString()));

            if (args.Length > 0 && args[0].Type == JsType.Function)
            {
                var compareFunc = args[0].GetFunction();
                elements.Sort((a, b) =>
                {
                    var result = compareFunc.Native!(a, new[] { a, b });
                    return (int)result.GetNumber();
                });
            }
            else
            {
                elements.Sort((a, b) => string.Compare(a.ToJsString(), b.ToJsString()));
            }

            for (int i = 0; i < length; i++)
                arr.Set(i.ToString(), elements[i]);

            return self;
        }, scope, "sort"));

        // join(separator?) - join all elements into string
        arrProto.Set("join", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            string separator = args.Length > 0 ? args[0].ToJsString() : ",";
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                    sb.Append(separator);
                sb.Append(arr.Get(i.ToString()).ToJsString());
            }
            return JsValue.From(sb.ToString());
        }, scope, "join"));

        // slice(start?, end?) - return shallow copy
        arrProto.Set("slice", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].ToNumber() : length;

            if (start < 0)
                start = Math.Max(length + start, 0);
            if (end < 0)
                end = Math.Max(length + end, 0);

            start = Math.Max(start, 0);
            end = Math.Min(end, length);

            var newArr = new JsObject();
            int newLength = 0;
            for (int i = start; i < end; i++)
            {
                newArr.Set(newLength.ToString(), arr.Get(i.ToString()));
                newLength++;
            }
            newArr.Set("length", JsValue.From(newLength));
            newArr.Prototype = arrProto;
            return JsValue.FromObject(newArr);
        }, scope, "slice"));

        // concat(...arrays) - return new array with concatenated items
        arrProto.Set("concat", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            var newArr = new JsObject();
            int newLength = 0;

            for (int i = 0; i < length; i++)
            {
                newArr.Set(newLength.ToString(), arr.Get(i.ToString()));
                newLength++;
            }

            foreach (var arg in args)
            {
                if (arg.Type == JsType.Object && arg.GetObject().HasOwn("length"))
                {
                    var argArr = arg.GetObject();
                    int argLength = (int)argArr.Get("length").GetNumber();
                    for (int i = 0; i < argLength; i++)
                    {
                        newArr.Set(newLength.ToString(), argArr.Get(i.ToString()));
                        newLength++;
                    }
                }
                else
                {
                    newArr.Set(newLength.ToString(), arg);
                    newLength++;
                }
            }

            newArr.Set("length", JsValue.From(newLength));
            newArr.Prototype = arrProto;
            return JsValue.FromObject(newArr);
        }, scope, "concat"));

        // splice(start, deleteCount?, ...items) - remove/add items
        arrProto.Set("splice", CreateNativeFunction((self, args) =>
        {
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            if (args.Length == 0)
                return JsValue.FromObject(new JsObject());

            int start = (int)args[0].ToNumber();
            if (start < 0)
                start = Math.Max(length + start, 0);
            start = Math.Min(start, length);

            int deleteCount = args.Length > 1 ? (int)args[1].ToNumber() : length - start;
            deleteCount = Math.Max(deleteCount, 0);
            deleteCount = Math.Min(deleteCount, length - start);

            var removed = new JsObject();
            int removedLength = 0;
            for (int i = 0; i < deleteCount; i++)
            {
                removed.Set(removedLength.ToString(), arr.Get((start + i).ToString()));
                removedLength++;
            }
            removed.Set("length", JsValue.From(removedLength));

            int itemsToInsert = args.Length > 2 ? args.Length - 2 : 0;
            int newLength = length - deleteCount + itemsToInsert;

            for (int i = start + deleteCount; i < length; i++)
            {
                arr.Set((i - deleteCount + itemsToInsert).ToString(), arr.Get(i.ToString()));
            }

            for (int i = 0; i < itemsToInsert; i++)
            {
                arr.Set((start + i).ToString(), args[2 + i]);
            }

            arr.Set("length", JsValue.From(newLength));
            for (int i = newLength; i < length; i++)
                arr.Properties.Remove(i.ToString());

            removed.Prototype = arrProto;
            return JsValue.FromObject(removed);
        }, scope, "splice"));

        // indexOf(item, fromIndex?) - return index or -1
        arrProto.Set("indexOf", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.From(-1);
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            var searchItem = args[0];
            int fromIndex = args.Length > 1 ? (int)args[1].ToNumber() : 0;
            fromIndex = Math.Max(fromIndex, 0);

            for (int i = fromIndex; i < length; i++)
            {
                if (arr.Get(i.ToString()).AbstractEquals(searchItem))
                    return JsValue.From(i);
            }
            return JsValue.From(-1);
        }, scope, "indexOf"));

        // forEach(callback) - call callback for each element
        arrProto.Set("forEach", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.Undefined;
            var arr = self.GetObject();
            int length = arr.HasOwn("length") ? (int)arr.Get("length").GetNumber() : 0;
            var callback = args[0].GetFunction();

            for (int i = 0; i < length; i++)
            {
                var item = arr.Get(i.ToString());
                callback.Native!(self, new[] { item, JsValue.From(i), self });
            }
            return JsValue.Undefined;
        }, scope, "forEach"));

        // toString() - same as join(",")
        arrProto.Set("toString", CreateNativeFunction((self, args) =>
        {
            var joinFunc = arrProto.Get("join").GetFunction();
            return joinFunc.Native!(self, Array.Empty<JsValue>());
        }, scope, "toString"));

        // Array constructor: new Array(), new Array(size), new Array(...items)
        var arrayConstructorValue = CreateNativeFunction((self, args) =>
        {
            var newArr = new JsObject();
            newArr.Prototype = arrProto;
            int length = 0;

            if (args.Length == 0)
            {
                // Empty array
            }
            else if (args.Length == 1 && args[0].Type == JsType.Number)
            {
                length = (int)args[0].ToNumber();
                length = Math.Max(length, 0);
            }
            else
            {
                foreach (var arg in args)
                {
                    newArr.Set(length.ToString(), arg);
                    length++;
                }
            }

            newArr.Set("length", JsValue.From(length));
            return JsValue.FromObject(newArr);
        }, scope, "Array");

        var arrayConstructorObj = arrayConstructorValue.GetFunction();
        arrayConstructorObj.Set("prototype", JsValue.FromObject(arrProto));
        scope.Define("Array", arrayConstructorValue);
    }

    private static void RegisterString(JsScope scope)
    {
        var strProto = new JsObject();

        // charAt(index) - return character at index
        strProto.Set("charAt", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (index < 0 || index >= str.Length)
                return JsValue.From("");
            return JsValue.From(str[index].ToString());
        }, scope, "charAt"));

        // charCodeAt(index) - return ASCII code at index
        strProto.Set("charCodeAt", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (index < 0 || index >= str.Length)
                return JsValue.From(double.NaN);
            return JsValue.From((int)str[index]);
        }, scope, "charCodeAt"));

        // fromCharCode(...codes) - static method, create string from char codes
        var fromCharCodeValue = CreateNativeFunction((self, args) =>
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                int code = (int)arg.ToNumber();
                sb.Append((char)code);
            }
            return JsValue.From(sb.ToString());
        }, scope, "fromCharCode");

        // indexOf(search, fromIndex?) - return index or -1
        strProto.Set("indexOf", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.From(-1);
            var str = self.ToJsString();
            var search = args[0].ToJsString();
            int fromIndex = args.Length > 1 ? (int)args[1].ToNumber() : 0;
            fromIndex = Math.Max(fromIndex, 0);
            int index = str.IndexOf(search, fromIndex);
            return JsValue.From(index);
        }, scope, "indexOf"));

        // lastIndexOf(search, fromIndex?) - return last index or -1
        strProto.Set("lastIndexOf", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.From(-1);
            var str = self.ToJsString();
            var search = args[0].ToJsString();
            int fromIndex = args.Length > 1 ? (int)args[1].ToNumber() : str.Length - 1;
            fromIndex = Math.Min(fromIndex, str.Length - 1);
            int index = str.LastIndexOf(search, fromIndex);
            return JsValue.From(index);
        }, scope, "lastIndexOf"));

        // substring(start, end?) - return substring
        strProto.Set("substring", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;

            start = Math.Max(start, 0);
            end = Math.Max(end, 0);
            if (start > end)
                (start, end) = (end, start);

            return JsValue.From(str.Substring(start, end - start));
        }, scope, "substring"));

        // substr(start, length?) - return substring (JS1.1)
        strProto.Set("substr", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            int length = args.Length > 1 ? (int)args[1].ToNumber() : str.Length - start;

            if (start < 0)
                start = Math.Max(str.Length + start, 0);
            if (length < 0)
                length = 0;

            return JsValue.From(str.Substring(start, Math.Min(length, str.Length - start)));
        }, scope, "substr"));

        // slice(start, end?) - return slice (handles negative indices)
        strProto.Set("slice", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;

            if (start < 0)
                start = Math.Max(str.Length + start, 0);
            if (end < 0)
                end = Math.Max(str.Length + end, 0);

            start = Math.Max(start, 0);
            end = Math.Min(end, str.Length);

            if (end <= start)
                return JsValue.From("");
            return JsValue.From(str.Substring(start, end - start));
        }, scope, "slice"));

        // toLowerCase() - return lowercase version
        strProto.Set("toLowerCase", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            return JsValue.From(str.ToLower());
        }, scope, "toLowerCase"));

        // toUpperCase() - return uppercase version
        strProto.Set("toUpperCase", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            return JsValue.From(str.ToUpper());
        }, scope, "toUpperCase"));

        // split(separator?) - split into array
        strProto.Set("split", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            string separator = args.Length > 0 ? args[0].ToJsString() : "";

            var resultArray = new JsObject();
            int length = 0;

            if (string.IsNullOrEmpty(separator))
            {
                foreach (var c in str)
                {
                    resultArray.Set(length.ToString(), JsValue.From(c.ToString()));
                    length++;
                }
            }
            else
            {
                var parts = str.Split(new[] { separator }, StringSplitOptions.None);
                foreach (var part in parts)
                {
                    resultArray.Set(length.ToString(), JsValue.From(part));
                    length++;
                }
            }

            resultArray.Set("length", JsValue.From(length));
            var arrayConstructorValue = scope.Get("Array");
            var arrayProto = arrayConstructorValue.GetFunction().Get("prototype").GetObject();
            resultArray.Prototype = arrayProto;
            return JsValue.FromObject(resultArray);
        }, scope, "split"));

        // concat(...strings) - concatenate strings
        strProto.Set("concat", CreateNativeFunction((self, args) =>
        {
            var str = self.ToJsString();
            var sb = new StringBuilder(str);
            foreach (var arg in args)
                sb.Append(arg.ToJsString());
            return JsValue.From(sb.ToString());
        }, scope, "concat"));

        // match(regex) - return array of matches or null
        strProto.Set("match", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.Null;
            var str = self.ToJsString();
            var regexArg = args[0];
            Regex regex;
            bool global = false;

            if (regexArg.Type == JsType.Object)
            {
                var regexObj = regexArg.GetObject();
                var source = regexObj.Get("source").ToJsString();
                var flags = regexObj.Get("flags").ToJsString();
                var options = RegexOptions.None;
                if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
                if (flags.Contains("m")) options |= RegexOptions.Multiline;
                regex = new Regex(source, options);
                global = flags.Contains("g");
            }
            else
            {
                regex = new Regex(regexArg.ToJsString());
            }

            var matches = regex.Matches(str);
            if (matches.Count == 0)
                return JsValue.Null;

            var resultArray = new JsObject();
            int matchLength = 0;
            foreach (Match match in matches)
            {
                resultArray.Set(matchLength.ToString(), JsValue.From(match.Value));
                matchLength++;
            }
            resultArray.Set("length", JsValue.From(matchLength));
            var arrayConstructorValue = scope.Get("Array");
            var arrayProto = arrayConstructorValue.GetFunction().Get("prototype").GetObject();
            resultArray.Prototype = arrayProto;
            return JsValue.FromObject(resultArray);
        }, scope, "match"));

        // replace(search, replacement) - replace first match
        strProto.Set("replace", CreateNativeFunction((self, args) =>
        {
            if (args.Length < 2)
                return self;
            var str = self.ToJsString();
            var search = args[0];
            var replacement = args[1].ToJsString();

            if (search.Type == JsType.Object)
            {
                var regexObj = search.GetObject();
                var source = regexObj.Get("source").ToJsString();
                var flags = regexObj.Get("flags").ToJsString();
                var options = RegexOptions.None;
                if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
                if (flags.Contains("m")) options |= RegexOptions.Multiline;
                var regex = new Regex(source, options);
                if (flags.Contains("g"))
                    return JsValue.From(regex.Replace(str, replacement));
                else
                    return JsValue.From(regex.Replace(str, replacement, 1));
            }
            else
            {
                var searchStr = search.ToJsString();
                int index = str.IndexOf(searchStr);
                if (index == -1)
                    return self;
                return JsValue.From(str.Substring(0, index) + replacement + str.Substring(index + searchStr.Length));
            }
        }, scope, "replace"));

        // search(regex) - return index of first match or -1
        strProto.Set("search", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0)
                return JsValue.From(-1);
            var str = self.ToJsString();
            var regexArg = args[0];
            Regex regex;

            if (regexArg.Type == JsType.Object)
            {
                var regexObj = regexArg.GetObject();
                var source = regexObj.Get("source").ToJsString();
                var flags = regexObj.Get("flags").ToJsString();
                var options = RegexOptions.None;
                if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
                if (flags.Contains("m")) options |= RegexOptions.Multiline;
                regex = new Regex(source, options);
            }
            else
            {
                regex = new Regex(regexArg.ToJsString());
            }

            var match = regex.Match(str);
            return JsValue.From(match.Success ? match.Index : -1);
        }, scope, "search"));

        // toString() - return string value
        strProto.Set("toString", CreateNativeFunction((self, args) =>
        {
            return JsValue.From(self.ToJsString());
        }, scope, "toString"));

        // String constructor: new String(text) or String(text)
        var stringConstructorValue = CreateNativeFunction((self, args) =>
        {
            string text = args.Length > 0 ? args[0].ToJsString() : "";
            if (self.Type == JsType.Object)
            {
                var strObj = self.GetObject();
                strObj.Set("value", JsValue.From(text));
                strObj.Set("length", JsValue.From(text.Length));
                strObj.Prototype = strProto;
                return self;
            }
            else
            {
                return JsValue.From(text);
            }
        }, scope, "String");

        var stringConstructorObj = stringConstructorValue.GetFunction();
        stringConstructorObj.Set("prototype", JsValue.FromObject(strProto));
        stringConstructorObj.Set("fromCharCode", fromCharCodeValue);
        scope.Define("String", stringConstructorValue);
    }

    private static void RegisterNumber(JsScope scope)
    {
        var numProto = new JsObject();

        // toString(radix?) - convert to string with optional radix
        numProto.Set("toString", CreateNativeFunction((self, args) =>
        {
            double num = self.ToNumber();
            int radix = args.Length > 0 ? (int)args[0].ToNumber() : 10;
            radix = Math.Max(2, Math.Min(36, radix));
            return JsValue.From(ConvertToBase(num, radix));
        }, scope, "toString"));

        // valueOf() - return number value i forget the add the implementation hahaha, uhm,  i am not funny i know
        numProto.Set("valueOf", CreateNativeFunction((self, args) =>
        {
            return self;
        }, scope, "valueOf"));

        // Helper to convert number to base (2-36)
        static string ConvertToBase(double num, int radix)
        {
            if (double.IsNaN(num) || double.IsInfinity(num))
                return num.ToString();
            long integerPart = (long)num;
            double fractionalPart = num - integerPart;
            var sb = new StringBuilder();
            if (integerPart < 0)
            {
                sb.Append("-");
                integerPart = -integerPart;
            }
            if (integerPart == 0)
                sb.Append("0");
            else
            {
                while (integerPart > 0)
                {
                    int digit = (int)(integerPart % radix);
                    sb.Insert(1, digit < 10 ? digit.ToString() : ((char)('a' + digit - 10)).ToString());
                    integerPart /= radix;
                }
            }
            if (fractionalPart > 0)
            {
                sb.Append(".");
                for (int i = 0; i < 10; i++)
                {
                    fractionalPart *= radix;
                    int digit = (int)fractionalPart;
                    sb.Append(digit < 10 ? digit.ToString() : ((char)('a' + digit - 10)).ToString());
                    fractionalPart -= digit;
                    if (fractionalPart < 1e-10)
                        break;
                }
            }
            return sb.ToString();
        }

        // Number constructor: new Number(value) or Number(value)
        var numberConstructorValue = CreateNativeFunction((self, args) =>
        {
            double value = args.Length > 0 ? args[0].ToNumber() : 0;
            if (self.Type == JsType.Object)
            {
                var numObj = self.GetObject();
                numObj.Set("value", JsValue.From(value));
                numObj.Prototype = numProto;
                return self;
            }
            else
            {
                return JsValue.From(value);
            }
        }, scope, "Number");

        var numberConstructorObj = numberConstructorValue.GetFunction();
        numberConstructorObj.Set("prototype", JsValue.FromObject(numProto));
        numberConstructorObj.Set("MAX_VALUE", JsValue.From(double.MaxValue));
        numberConstructorObj.Set("MIN_VALUE", JsValue.From(double.Epsilon));
        numberConstructorObj.Set("NaN", JsValue.From(double.NaN));
        numberConstructorObj.Set("POSITIVE_INFINITY", JsValue.From(double.PositiveInfinity));
        numberConstructorObj.Set("NEGATIVE_INFINITY", JsValue.From(double.NegativeInfinity));
        scope.Define("Number", numberConstructorValue);
    }

    private static void RegisterBoolean(JsScope scope)
    {
        var boolProto = new JsObject();

        // toString() - return "true" or "false" what if it returns "trulse"?
        boolProto.Set("toString", CreateNativeFunction((self, args) =>
        {
            bool val = self.ToBoolean();
            return JsValue.From(val ? "true" : "false");
        }, scope, "toString"));

        // valueOf() - return boolean value
        boolProto.Set("valueOf", CreateNativeFunction((self, args) =>
        {
            return self;
        }, scope, "valueOf"));

        // Boolean constructor: new Boolean(value) or Boolean(value)
        var booleanConstructorValue = CreateNativeFunction((self, args) =>
        {
            bool value = args.Length > 0 ? args[0].ToBoolean() : false;
            if (self.Type == JsType.Object)
            {
                var boolObj = self.GetObject();
                boolObj.Set("value", JsValue.From(value));
                boolObj.Prototype = boolProto;
                return self;
            }
            else
            {
                return JsValue.From(value);
            }
        }, scope, "Boolean");

        var booleanConstructorObj = booleanConstructorValue.GetFunction();
        booleanConstructorObj.Set("prototype", JsValue.FromObject(boolProto));
        scope.Define("Boolean", booleanConstructorValue);
    }

    private static void RegisterMath(JsScope scope)
    {
        var mathObj = new JsObject();

        // this is the Constants it just works
        mathObj.Set("E", JsValue.From(Math.E));
        mathObj.Set("PI", JsValue.From(Math.PI));
        mathObj.Set("LN2", JsValue.From(Math.Log(2)));
        mathObj.Set("LN10", JsValue.From(Math.Log(10)));
        mathObj.Set("LOG2E", JsValue.From(Math.Log(Math.E, 2)));
        mathObj.Set("LOG10E", JsValue.From(Math.Log10(Math.E)));
        mathObj.Set("SQRT2", JsValue.From(Math.Sqrt(2)));
        mathObj.Set("SQRT1_2", JsValue.From(Math.Sqrt(0.5)));

        // Methods
        mathObj.Set("abs", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Abs(args[0].ToNumber()));
        }, scope, "abs"));

        mathObj.Set("floor", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Floor(args[0].ToNumber()));
        }, scope, "floor"));

        mathObj.Set("ceil", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Ceiling(args[0].ToNumber()));
        }, scope, "ceil"));

        mathObj.Set("round", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Round(args[0].ToNumber()));
        }, scope, "round"));

        mathObj.Set("min", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.PositiveInfinity);
            double min = args[0].ToNumber();
            foreach (var arg in args)
            {
                double num = arg.ToNumber();
                if (num < min) min = num;
            }
            return JsValue.From(min);
        }, scope, "min"));

        mathObj.Set("max", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NegativeInfinity);
            double max = args[0].ToNumber();
            foreach (var arg in args)
            {
                double num = arg.ToNumber();
                if (num > max) max = num;
            }
            return JsValue.From(max);
        }, scope, "max"));

        mathObj.Set("pow", CreateNativeFunction((self, args) =>
        {
            if (args.Length < 2) return JsValue.From(double.NaN);
            return JsValue.From(Math.Pow(args[0].ToNumber(), args[1].ToNumber()));
        }, scope, "pow"));

        mathObj.Set("sqrt", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Sqrt(args[0].ToNumber()));
        }, scope, "sqrt"));

        mathObj.Set("log", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Log(args[0].ToNumber()));
        }, scope, "log"));

        mathObj.Set("exp", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Exp(args[0].ToNumber()));
        }, scope, "exp"));

        mathObj.Set("sin", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Sin(args[0].ToNumber()));
        }, scope, "sin"));

        mathObj.Set("cos", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Cos(args[0].ToNumber()));
        }, scope, "cos"));

        mathObj.Set("tan", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Tan(args[0].ToNumber()));
        }, scope, "tan"));

        mathObj.Set("asin", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Asin(args[0].ToNumber()));
        }, scope, "asin"));

        mathObj.Set("acos", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Acos(args[0].ToNumber()));
        }, scope, "acos"));

        mathObj.Set("atan", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            return JsValue.From(Math.Atan(args[0].ToNumber()));
        }, scope, "atan"));

        mathObj.Set("atan2", CreateNativeFunction((self, args) =>
        {
            if (args.Length < 2) return JsValue.From(double.NaN);
            return JsValue.From(Math.Atan2(args[0].ToNumber(), args[1].ToNumber()));
        }, scope, "atan2"));

        mathObj.Set("random", CreateNativeFunction((self, args) =>
        {
            return JsValue.From(new Random().NextDouble());
        }, scope, "random"));

        scope.Define("Math", JsValue.FromObject(mathObj));
    }

    private static void RegisterDate(JsScope scope)
    {
        var dateProto = new JsObject();
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static double GetMs(JsValue self)
        {
            var obj = self.GetObject();
            return obj.Get("value").GetNumber();
        }

        static void SetMs(JsValue self, double ms)
        {
            var obj = self.GetObject();
            obj.Set("value", JsValue.From(ms));
        }

        // getFullYear() - 4-digit year
        dateProto.Set("getFullYear", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.Year);
        }, scope, "getFullYear"));

        // getMonth() - month (0-11)
        dateProto.Set("getMonth", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.Month - 1);
        }, scope, "getMonth"));

        // getDate() - day of month (1-31)
        dateProto.Set("getDate", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.Day);
        }, scope, "getDate"));

        // getDay() - day of week (0-6, Sunday=0)
        dateProto.Set("getDay", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From((int)date.DayOfWeek);
        }, scope, "getDay"));

        // getHours() - hours (0-23)
        dateProto.Set("getHours", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.Hour);
        }, scope, "getHours"));

        // getMinutes() - minutes (0-59)
        dateProto.Set("getMinutes", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.Minute);
        }, scope, "getMinutes"));

        // getSeconds() - seconds (0-59)
        dateProto.Set("getSeconds", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.Second);
        }, scope, "getSeconds"));

        // getTime() - milliseconds since epoch
        dateProto.Set("getTime", CreateNativeFunction((self, args) =>
        {
            return JsValue.From(GetMs(self));
        }, scope, "getTime"));

        // setTime(ms) - set time from milliseconds
        dateProto.Set("setTime", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            double ms = args[0].ToNumber();
            SetMs(self, ms);
            return JsValue.From(ms);
        }, scope, "setTime"));

        // toLocaleString() - localized string
        dateProto.Set("toLocaleString", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.ToString());
        }, scope, "toLocaleString"));

        // toString() - string representation
        dateProto.Set("toString", CreateNativeFunction((self, args) =>
        {
            double ms = GetMs(self);
            var date = epoch.AddMilliseconds(ms).ToLocalTime();
            return JsValue.From(date.ToString());
        }, scope, "toString"));

        // valueOf() - milliseconds since epoch
        dateProto.Set("valueOf", CreateNativeFunction((self, args) =>
        {
            return JsValue.From(GetMs(self));
        }, scope, "valueOf"));

        // Date constructor: new Date(), new Date(ms), new Date(dateString)
        var dateConstructorValue = CreateNativeFunction((self, args) =>
        {
            double ms = 0;
            if (args.Length == 0)
            {
                // Current time i think
                ms = (DateTime.UtcNow - epoch).TotalMilliseconds;
            }
            else if (args.Length == 1)
            {
                var arg = args[0];
                if (arg.Type == JsType.Number)
                {
                    ms = arg.ToNumber();
                }
                else
                {
                    // this probably Parses date string
                    var dateStr = arg.ToJsString();
                    if (DateTime.TryParse(dateStr, out var date))
                    {
                        ms = (date.ToUniversalTime() - epoch).TotalMilliseconds;
                    }
                    else
                    {
                        ms = double.NaN;
                    }
                }
            }
            else
            {
                // Multiple args: year, month, day, hour, minute, second, ms
                int year = (int)args[0].ToNumber();
                int month = args.Length > 1 ? (int)args[1].ToNumber() : 0; // JS month is 0-11
                int day = args.Length > 2 ? (int)args[2].ToNumber() : 1;
                int hour = args.Length > 3 ? (int)args[3].ToNumber() : 0;
                int minute = args.Length > 4 ? (int)args[4].ToNumber() : 0;
                int second = args.Length > 5 ? (int)args[5].ToNumber() : 0;
                int millisecond = args.Length > 6 ? (int)args[6].ToNumber() : 0;

                var date = new DateTime(year, month + 1, day, hour, minute, second, millisecond, DateTimeKind.Local);
                ms = (date.ToUniversalTime() - epoch).TotalMilliseconds;
            }

            if (self.Type == JsType.Object)
            {
                var dateObj = self.GetObject();
                SetMs(JsValue.FromObject(dateObj), ms);
                dateObj.Prototype = dateProto;
                return self;
            }
            else
            {
                var dateObj = new JsObject();
                SetMs(JsValue.FromObject(dateObj), ms);
                dateObj.Prototype = dateProto;
                return JsValue.FromObject(dateObj);
            }
        }, scope, "Date");

        var dateConstructorObj = dateConstructorValue.GetFunction();
        dateConstructorObj.Set("prototype", JsValue.FromObject(dateProto));
        scope.Define("Date", dateConstructorValue);
    }

    private static void RegisterRegExp(JsScope scope)
    {
        var regexProto = new JsObject();

        // source property
        regexProto.Set("source", JsValue.From(""));

        // flags property
        regexProto.Set("flags", JsValue.From(""));

        // lastIndex property
        regexProto.Set("lastIndex", JsValue.From(0));

        // test(string) - return true if matches
        regexProto.Set("test", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            var str = args[0].ToJsString();
            var regexObj = self.GetObject();
            var source = regexObj.Get("source").ToJsString();
            var flags = regexObj.Get("flags").ToJsString();
            var lastIndex = (int)regexObj.Get("lastIndex").GetNumber();

            var options = RegexOptions.None;
            if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
            if (flags.Contains("m")) options |= RegexOptions.Multiline;
            var regex = new Regex(source, options);

            var match = regex.Match(str, lastIndex);
            if (match.Success)
            {
                if (flags.Contains("g"))
                    regexObj.Set("lastIndex", JsValue.From(match.Index + match.Length));
                return JsValue.From(true);
            }
            else
            {
                regexObj.Set("lastIndex", JsValue.From(0));
                return JsValue.From(false);
            }
        }, scope, "test"));

        // exec(string) - return array of match info or null
        regexProto.Set("exec", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.Null;
            var str = args[0].ToJsString();
            var regexObj = self.GetObject();
            var source = regexObj.Get("source").ToJsString();
            var flags = regexObj.Get("flags").ToJsString();
            var lastIndex = (int)regexObj.Get("lastIndex").GetNumber();

            var options = RegexOptions.None;
            if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
            if (flags.Contains("m")) options |= RegexOptions.Multiline;
            var regex = new Regex(source, options);

            var match = regex.Match(str, lastIndex);
            if (!match.Success)
                return JsValue.Null;

            var resultArray = new JsObject();
            int length = 0;
            foreach (var group in match.Groups)
            {
                resultArray.Set(length.ToString(), JsValue.From(group.ToString()));
                length++;
            }
            resultArray.Set("length", JsValue.From(length));
            resultArray.Set("index", JsValue.From(match.Index));
            resultArray.Set("input", JsValue.From(str));

            if (flags.Contains("g"))
                regexObj.Set("lastIndex", JsValue.From(match.Index + match.Length));
            else
                regexObj.Set("lastIndex", JsValue.From(0));

            var arrayConstructorValue = scope.Get("Array");
            var arrayProto = arrayConstructorValue.GetFunction().Get("prototype").GetObject();
            resultArray.Prototype = arrayProto;
            return JsValue.FromObject(resultArray);
        }, scope, "exec"));

        // RegExp constructor: new RegExp(pattern, flags)
        var regexpConstructorValue = CreateNativeFunction((self, args) =>
        {
            string pattern = args.Length > 0 ? args[0].ToJsString() : "";
            string flags = args.Length > 1 ? args[1].ToJsString() : "";

            // Validate flags: only g, i, m allowed in JS1.1
            var validFlags = new StringBuilder();
            foreach (var c in flags)
            {
                if (c == 'g' || c == 'i' || c == 'm')
                    validFlags.Append(c);
            }
            flags = validFlags.ToString();

            if (self.Type == JsType.Object)
            {
                var regexObj = self.GetObject();
                regexObj.Set("source", JsValue.From(pattern));
                regexObj.Set("flags", JsValue.From(flags));
                regexObj.Set("lastIndex", JsValue.From(0));
                regexObj.Prototype = regexProto;
                return self;
            }
            else
            {
                var regexObj = new JsObject();
                regexObj.Set("source", JsValue.From(pattern));
                regexObj.Set("flags", JsValue.From(flags));
                regexObj.Set("lastIndex", JsValue.From(0));
                regexObj.Prototype = regexProto;
                return JsValue.FromObject(regexObj);
            }
        }, scope, "RegExp");

        var regexpConstructorObj = regexpConstructorValue.GetFunction();
        regexpConstructorObj.Set("prototype", JsValue.FromObject(regexProto));
        scope.Define("RegExp", regexpConstructorValue);
    }

    private static void RegisterError(JsScope scope)
    {
        var errorConstructorValue = CreateNativeFunction((self, args) =>
        {
            var message = args.Length > 0 ? args[0].ToJsString() : "";
            var errorObj = new JsObject();
            errorObj.Set("name", JsValue.From("Error"));
            errorObj.Set("message", JsValue.From(message));
            errorObj.Prototype = new JsObject();
            return JsValue.FromObject(errorObj);
        }, scope, "Error");

        scope.Define("Error", errorConstructorValue);
    }

    private static void RegisterGlobalFunctions(JsScope scope)
    {
        // parseInt(string, radix?) - parse integer
        scope.Define("parseInt", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            string str = args[0].ToJsString().Trim();
            int radix = args.Length > 1 ? (int)args[1].ToNumber() : 0;

            // Handle 0x prefix for hex
            if (radix == 0 || radix == 16)
            {
                if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    str = str.Substring(2);
                    radix = 16;
                }
            }

            if (radix == 0)
                radix = 10;

            if (radix < 2 || radix > 36)
                return JsValue.From(double.NaN);

            try
            {
                return JsValue.From(Convert.ToInt64(str, radix));
            }
            catch
            {
                return JsValue.From(double.NaN);
            }
        }, scope, "parseInt"));

        // parseFloat(string) - parse float
        scope.Define("parseFloat", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(double.NaN);
            string str = args[0].ToJsString().Trim();
            if (double.TryParse(str, out double result))
                return JsValue.From(result);
            return JsValue.From(double.NaN);
        }, scope, "parseFloat"));

        // isNaN(value) - check if NaN
        scope.Define("isNaN", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(true);
            double num = args[0].ToNumber();
            return JsValue.From(double.IsNaN(num));
        }, scope, "isNaN"));

        // isFinite(value) - check if finite number
        scope.Define("isFinite", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From(false);
            double num = args[0].ToNumber();
            return JsValue.From(!double.IsNaN(num) && !double.IsInfinity(num));
        }, scope, "isFinite"));

       // eval(string) - evaluate JavaScript code
       // Proper eval binding is done in RegisterDeferredBindings()
       // This placeholder ensures eval exists even if RegisterDeferredBindings is not called
       scope.Define("eval", CreateNativeFunction((self, args) =>
       {
           throw new InvalidOperationException("eval() not yet bound to interpreter - call RegisterDeferredBindings() on JsInterpreter");
       }, scope, "eval"));

        // escape(string) - URL-encode string (JS1.1)
        scope.Define("escape", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From("undefined");
            string str = args[0].ToJsString();
            return JsValue.From(Uri.EscapeDataString(str));
        }, scope, "escape"));

        // unescape(string) - URL-decode string (JS1.1)
        scope.Define("unescape", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From("undefined");
            string str = args[0].ToJsString();
            return JsValue.From(Uri.UnescapeDataString(str));
        }, scope, "unescape"));

        // encodeURIComponent(string) - modern URI encoding
        scope.Define("encodeURIComponent", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From("");
            string str = args[0].ToJsString();
            return JsValue.From(Uri.EscapeDataString(str));
        }, scope, "encodeURIComponent"));

        // decodeURIComponent(string) - modern URI decoding
        scope.Define("decodeURIComponent", CreateNativeFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.From("");
            string str = args[0].ToJsString();
            return JsValue.From(Uri.UnescapeDataString(str));
        }, scope, "decodeURIComponent"));
    }
}