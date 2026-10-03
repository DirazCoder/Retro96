using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Retro96.Engine.Vbs;

/// <summary>
/// Shared runtime state handed to every builtin: the host (output, dialogs,
/// object creation), the RNG (Rnd/Randomize), the Err object, and the last
/// Rnd value (Rnd(0) repeats it).
/// </summary>
public sealed class VbsRuntimeContext
{
    public IVbsScriptHost Host { get; }
    public Random Random { get; set; }
    public VbsErrObject Err { get; }
    internal double LastRnd;

    public VbsRuntimeContext(IVbsScriptHost host, Random random, VbsErrObject err)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Random = random ?? throw new ArgumentNullException(nameof(random));
        Err = err ?? throw new ArgumentNullException(nameof(err));
    }
}

public delegate VbsVariant VbsBuiltinFunction(VbsRuntimeContext ctx, VbsVariant[] args);

/// <summary>
/// The VBScript 1.0 intrinsic library: conversions, string functions, math,
/// dates, type information, and the host-routed MsgBox/InputBox/
/// CreateObject/GetObject — plus the vb* named constants.
///
/// The tables are case-insensitive (VBScript identifiers are), and the
/// interpreter checks procedure locals/globals BEFORE the builtin table for
/// bare-name reads, so user variables may shadow builtins.
/// </summary>
public static class VbsBuiltins
{
    // Engine version reported by the WSH-style host probes.
    public const int EngineMajor = 1;
    public const int EngineMinor = 0;
    public const int EngineBuild = 0;

    public static readonly Dictionary<string, VbsVariant> Constants =
        new(StringComparer.OrdinalIgnoreCase);

    public static readonly Dictionary<string, VbsBuiltinFunction> Table =
        new(StringComparer.OrdinalIgnoreCase);

    static VbsBuiltins()
    {
        RegisterConstants();
        RegisterConversions();
        RegisterStrings();
        RegisterMath();
        RegisterDates();
        RegisterTypeInformation();
        RegisterHostFunctions();
    }

    // ── Argument helpers ───────────────────────────────────────────────────

    private static VbsVariant Need1(VbsVariant[] a)
    {
        if (a.Length < 1)
            throw new VbsRuntimeException(VbsErrorNumbers.InvalidProcedureCall,
                "Invalid procedure call or argument");
        return a[0];
    }

    private static VbsRuntimeException Err5() =>
        new(VbsErrorNumbers.InvalidProcedureCall, "Invalid procedure call or argument");

    private static VbsRuntimeException Err13() =>
        new(VbsErrorNumbers.TypeMismatch, "Type mismatch");

    /// <summary>String form; Empty renders as "" (Null is handled by callers).</summary>
    private static string S(VbsVariant v) =>
        v.Type == VbVarType.Empty ? "" : v.ToStringVariant();

    /// <summary>Optional numeric argument, or the default when absent/Empty.</summary>
    private static long L(VbsVariant[] a, int i, long def) =>
        i < a.Length && a[i].Type != VbVarType.Empty ? a[i].ToLongMath() : def;

    /// <summary>TriState argument (-1 True / 0 False / -2 vbUseDefault → default).</summary>
    private static bool Tri(VbsVariant[] a, int i, bool def)
    {
        if (i >= a.Length || a[i].Type == VbVarType.Empty) return def;
        return a[i].ToLongMath() switch { -1 => true, 0 => false, _ => def };
    }

    /// <summary>vbBinaryCompare / vbTextCompare argument → StringComparison.</summary>
    private static StringComparison Cmp(VbsVariant[] a, int i) =>
        i < a.Length && a[i].Type != VbVarType.Empty && a[i].ToLongMath() == 1
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    // ── Constants ───────────────────────────────────────────────────────────

    private static void RegisterConstants()
    {
        void C(string n, short v) => Constants[n] = VbsVariant.Of(v);
        void CS(string n, string v) => Constants[n] = VbsVariant.Of(v);

        // Control characters
        CS("vbCr", "\r"); CS("vbLf", "\n"); CS("vbCrLf", "\r\n"); CS("vbNewLine", "\r\n");
        CS("vbTab", "\t"); CS("vbFormFeed", "\f"); CS("vbVerticalTab", "\v");
        CS("vbNullChar", "\0"); CS("vbNullString", "");

        // Compare modes
        C("vbBinaryCompare", 0); C("vbTextCompare", 1);

        // Days of week
        C("vbUseSystemDayOfWeek", 0);
        C("vbSunday", 1); C("vbMonday", 2); C("vbTuesday", 3); C("vbWednesday", 4);
        C("vbThursday", 5); C("vbFriday", 6); C("vbSaturday", 7);

        // First week of year
        C("vbUseSystem", 0); C("vbFirstJan1", 1);
        C("vbFirstFourDays", 2); C("vbFirstFullWeek", 3);

        // MsgBox buttons
        C("vbOKOnly", 0); C("vbOKCancel", 1); C("vbAbortRetryIgnore", 2);
        C("vbYesNoCancel", 3); C("vbYesNo", 4); C("vbRetryCancel", 5);
        C("vbCritical", 16); C("vbQuestion", 32); C("vbExclamation", 48); C("vbInformation", 64);
        C("vbDefaultButton1", 0); C("vbDefaultButton2", 256);
        C("vbDefaultButton3", 512); C("vbDefaultButton4", 768);
        C("vbApplicationModal", 0); C("vbSystemModal", 4096);

        // MsgBox results
        C("vbOK", 1); C("vbCancel", 2); C("vbAbort", 3); C("vbRetry", 4);
        C("vbIgnore", 5); C("vbYes", 6); C("vbNo", 7);

        // VarType codes
        C("vbEmpty", 0); C("vbNull", 1); C("vbInteger", 2); C("vbLong", 3);
        C("vbSingle", 4); C("vbDouble", 5); C("vbCurrency", 6); C("vbDate", 7);
        C("vbString", 8); C("vbObject", 9); C("vbError", 10); C("vbBoolean", 11);
        C("vbVariant", 12); C("vbArray", 8192); C("vbByte", 17);

        // FormatDateTime named formats
        C("vbGeneralDate", 0); C("vbLongDate", 1); C("vbShortDate", 2);
        C("vbLongTime", 3); C("vbShortTime", 4);

        // TriState
        C("vbUseDefault", -2); C("vbTrue", -1); C("vbFalse", 0);

        // Base for user-defined Err.Raise numbers (&H80040000)
        Constants["vbObjectError"] = VbsVariant.Of(unchecked((int)0x80040000));
    }

    // ── Conversions ─────────────────────────────────────────────────────────

    private static void RegisterConversions()
    {
        Table["CBool"] = (ctx, a) => VbsVariant.Of(Need1(a).ToBoolVariant());
        Table["CByte"] = (ctx, a) => Need1(a).ToByteVariant();
        Table["CCur"] = (ctx, a) => Need1(a).ToCurrencyVariant();
        Table["CDate"] = (ctx, a) => Need1(a).ToDateVariant();
        Table["CDbl"] = (ctx, a) => Need1(a).ToDoubleVariant();
        Table["CInt"] = (ctx, a) => Need1(a).ToIntVariant();
        Table["CLng"] = (ctx, a) => Need1(a).ToLongVariant();
        Table["CSng"] = (ctx, a) => Need1(a).ToSingleVariant();
        Table["CStr"] = (ctx, a) => VbsVariant.Of(Need1(a).ToStringVariant());

        Table["Hex"] = (ctx, a) => VbsVariant.Of(ToRadixString(Need1(a), 16));
        Table["Oct"] = (ctx, a) => VbsVariant.Of(ToRadixString(Need1(a), 8));

        Table["Asc"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            string s = S(v);
            if (s.Length == 0) throw Err5();
            return VbsVariant.Of((short)s[0]);
        };

        Table["Chr"] = (ctx, a) =>
        {
            long c = Need1(a).ToLongMath();
            if (c < 0 || c > 255) throw Err5();
            return VbsVariant.Of(((char)c).ToString());
        };
    }

    /// <summary>Hex/Oct: negatives render in two's complement at the operand's
    /// natural width (Integer → 16-bit, Long → 32-bit); Hex(-1) = "FFFF".</summary>
    private static string ToRadixString(VbsVariant v, int radix)
    {
        long n = v.ToLongMath();
        if (n < 0)
        {
            ulong masked = v.Type == VbVarType.Integer ? (ushort)(short)n : (uint)(int)n;
            return radix == 16
                ? masked.ToString("X")
                : Convert.ToString((long)masked, radix).ToUpperInvariant();
        }
        return radix == 16
            ? n.ToString("X")
            : Convert.ToString(n, radix).ToUpperInvariant();
    }

    // ── Strings ─────────────────────────────────────────────────────────────

    private static void RegisterStrings()
    {
        Table["Len"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            return VbsVariant.Of(S(v).Length);
        };

        Table["Left"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            if (a[0].Type == VbVarType.Null || a[1].Type == VbVarType.Null)
                return VbsVariant.Null;
            string s = S(a[0]);
            long n = a[1].ToLongMath();
            if (n < 0) throw Err5();
            int take = n < s.Length ? (int)n : s.Length;
            return VbsVariant.Of(s[..take]);
        };

        Table["Right"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            if (a[0].Type == VbVarType.Null || a[1].Type == VbVarType.Null)
                return VbsVariant.Null;
            string s = S(a[0]);
            long n = a[1].ToLongMath();
            if (n < 0) throw Err5();
            int skip = n >= s.Length ? 0 : s.Length - (int)n;
            return VbsVariant.Of(s[skip..]);
        };

        Table["Mid"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            if (a[0].Type == VbVarType.Null || a[1].Type == VbVarType.Null)
                return VbsVariant.Null;
            string s = S(a[0]);
            long start = a[1].ToLongMath();
            if (start < 1) throw Err5();
            if (start > s.Length) return VbsVariant.Of("");
            int begin = (int)(start - 1);
            long len = a.Length > 2 && a[2].Type != VbVarType.Empty
                ? a[2].ToLongMath()
                : s.Length - begin;
            if (len < 0) throw Err5();
            int count = (int)Math.Min(len, s.Length - begin);
            return VbsVariant.Of(s.Substring(begin, count));
        };

        // InStr([start,] s1, s2 [, compare]) — the 3-argument form is the
        // classic ambiguity: a STRING first argument means (s1, s2, compare),
        // a numeric one means (start, s1, s2).
        Table["InStr"] = (ctx, a) =>
        {
            long start = 1;
            string s1, s2;
            StringComparison cmp = StringComparison.Ordinal;

            switch (a.Length)
            {
                case 2:
                    s1 = S(a[0]); s2 = S(a[1]);
                    break;
                case 3:
                    if (a[0].Type == VbVarType.String)
                    {
                        s1 = S(a[0]); s2 = S(a[1]); cmp = Cmp(a, 2);
                    }
                    else
                    {
                        start = a[0].ToLongMath(); s1 = S(a[1]); s2 = S(a[2]);
                    }
                    break;
                case 4:
                    start = a[0].ToLongMath(); s1 = S(a[1]); s2 = S(a[2]); cmp = Cmp(a, 3);
                    break;
                default:
                    throw Err5();
            }

            if (a.AnyNull()) return VbsVariant.Null;
            if (start < 1) throw Err5();
            if (start > s1.Length) return VbsVariant.FromLong(0);
            int idx = s1.IndexOf(s2, (int)(start - 1), cmp);
            return VbsVariant.FromLong(idx < 0 ? 0 : idx + 1);
        };

        // InStrRev(s1, s2 [, start] [, compare]) — start is 1-based; -1 (or
        // omitted) searches from the end.
        Table["InStrRev"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            if (a.AnyNull()) return VbsVariant.Null;
            string s1 = S(a[0]);
            string s2 = S(a[1]);
            long start = -1;
            StringComparison cmp = StringComparison.Ordinal;

            if (a.Length == 3)
            {
                // 3-arg ambiguity: 0/1 is a compare mode, anything else a start
                long third = a[2].Type == VbVarType.Empty ? -1 : a[2].ToLongMath();
                if (third is 0 or 1) cmp = Cmp(a, 2);
                else start = third;
            }
            else if (a.Length >= 4)
            {
                start = L(a, 2, -1);
                cmp = Cmp(a, 3);
            }

            int limit = start < 0 ? s1.Length : (int)Math.Min(start, s1.Length);
            if (s2.Length == 0)
                return VbsVariant.FromLong(Math.Max(0, limit));
            int begin = limit - s2.Length;
            if (begin < 0) return VbsVariant.FromLong(0);
            int idx = s1.LastIndexOf(s2, begin, cmp);
            return VbsVariant.FromLong(idx < 0 ? 0 : idx + 1);
        };

        // Replace(expr, find, repl [, start] [, count] [, compare])
        Table["Replace"] = (ctx, a) =>
        {
            if (a.Length < 3) throw Err5();
            if (a.AnyNull()) return VbsVariant.Null;
            string expr = S(a[0]);
            string find = S(a[1]);
            string repl = S(a[2]);
            long start = L(a, 3, 1);
            long count = L(a, 4, -1);
            StringComparison cmp = Cmp(a, 5);
            if (start < 1) throw Err5();
            if (start > expr.Length) return VbsVariant.Of("");

            string head = expr[..(int)(start - 1)];
            string body = expr[(int)(start - 1)..];
            return VbsVariant.Of(head + ReplaceCount(body, find, repl, count, cmp));
        };

        // Split(expr [, delim] [, count] [, compare])
        Table["Split"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            string expr = S(v);
            string delim = a.Length > 1 && a[1].Type != VbVarType.Empty ? S(a[1]) : " ";
            long count = L(a, 2, -1);
            StringComparison cmp = Cmp(a, 3);
            if (count < -1) throw Err5();

            if (expr.Length == 0 || count == 0)
                return VbsVariant.Of(VbsArray.Allocate(true, 0));

            var parts = new List<string>();
            if (delim.Length == 0)
            {
                foreach (char c in expr) parts.Add(c.ToString());
            }
            else
            {
                int pos = 0, added = 0;
                while (count < 0 || added < count - 1)
                {
                    int idx = expr.IndexOf(delim, pos, cmp);
                    if (idx < 0) break;
                    parts.Add(expr[pos..idx]);
                    pos = idx + delim.Length;
                    added++;
                }
                parts.Add(expr[pos..]);
            }

            var arr = VbsArray.Allocate(true, parts.Count);
            for (int i = 0; i < parts.Count; i++)
                arr.Set(new[] { i }, VbsVariant.Of(parts[i]));
            return VbsVariant.Of(arr);
        };

        Table["Join"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            if (v.Type != VbVarType.Array) throw Err13();
            string delim = a.Length > 1 && a[1].Type != VbVarType.Empty ? S(a[1]) : " ";
            var sb = new StringBuilder();
            bool first = true;
            foreach (var e in v.AsArray().Elements())
            {
                if (!first) sb.Append(delim);
                first = false;
                if (e.Type is not (VbVarType.Null or VbVarType.Empty))
                    sb.Append(e.ToStringVariant());
            }
            return VbsVariant.Of(sb.ToString());
        };

        Table["StrComp"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            if (a[0].Type == VbVarType.Null || a[1].Type == VbVarType.Null)
                return VbsVariant.Null;
            int c = string.Compare(S(a[0]), S(a[1]), Cmp(a, 2));
            return VbsVariant.Of((short)Math.Sign(c));
        };

        Table["Space"] = (ctx, a) =>
        {
            long n = Need1(a).ToLongMath();
            if (n < 0 || n > 1_000_000) throw Err5();
            return VbsVariant.Of(new string(' ', (int)n));
        };

        Table["String"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            long n = a[0].ToLongMath();
            if (n < 0 || n > 1_000_000) throw Err5();
            char c;
            if (a[1].Type == VbVarType.String)
            {
                string t = a[1].AsString();
                if (t.Length == 0) throw Err5();
                c = t[0];
            }
            else
            {
                long code = a[1].ToLongMath();
                if (code < 0 || code > 255) throw Err5();
                c = (char)code;
            }
            return VbsVariant.Of(new string(c, (int)n));
        };

        // Null-propagating string transforms
        VbsVariant StrFunc(VbsVariant[] args, Func<string, string> f) =>
            args.Length == 0 ? throw Err5()
            : args[0].Type == VbVarType.Null ? VbsVariant.Null
            : VbsVariant.Of(f(S(args[0])));

        Table["UCase"] = (ctx, a) => StrFunc(a, s => s.ToUpperInvariant());
        Table["LCase"] = (ctx, a) => StrFunc(a, s => s.ToLowerInvariant());
        Table["Trim"] = (ctx, a) => StrFunc(a, s => s.Trim(' '));
        Table["LTrim"] = (ctx, a) => StrFunc(a, s => s.TrimStart(' '));
        Table["RTrim"] = (ctx, a) => StrFunc(a, s => s.TrimEnd(' '));
        Table["StrReverse"] = (ctx, a) => StrFunc(a, s =>
        {
            var sb = new StringBuilder(s.Length);
            for (int i = s.Length - 1; i >= 0; i--) sb.Append(s[i]);
            return sb.ToString();
        });
    }

    private static string ReplaceCount(string s, string find, string repl,
                                       long count, StringComparison cmp)
    {
        if (find.Length == 0 || count == 0) return s;
        var sb = new StringBuilder(s.Length);
        int pos = 0, replaced = 0;
        while (count < 0 || replaced < count)
        {
            int idx = s.IndexOf(find, pos, cmp);
            if (idx < 0) break;
            sb.Append(s, pos, idx - pos).Append(repl);
            pos = idx + find.Length;
            replaced++;
        }
        sb.Append(s, pos, s.Length - pos);
        return sb.ToString();
    }

    // ── Math ────────────────────────────────────────────────────────────────

    private static void RegisterMath()
    {
        Table["Abs"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            return v.Type switch
            {
                VbVarType.Currency => VbsVariant.Of(Math.Abs(v.AsDecimal())),
                VbVarType.Single => VbsVariant.Of(Math.Abs(v.AsSingle())),
                VbVarType.Double => VbsVariant.Of(Math.Abs(v.AsDouble())),
                VbVarType.Byte or VbVarType.Integer or VbVarType.Long
                    or VbVarType.Boolean or VbVarType.Empty =>
                    VbsVariant.FromLong(Math.Abs(v.IntegralValue())),
                _ => VbsVariant.Of(Math.Abs(v.ToDoubleMath()))
            };
        };

        Table["Atn"] = (ctx, a) => VbsVariant.Of(Math.Atan(Need1(a).ToDoubleMath()));
        Table["Cos"] = (ctx, a) => VbsVariant.Of(Math.Cos(Need1(a).ToDoubleMath()));
        Table["Sin"] = (ctx, a) => VbsVariant.Of(Math.Sin(Need1(a).ToDoubleMath()));
        Table["Tan"] = (ctx, a) => VbsVariant.Of(Math.Tan(Need1(a).ToDoubleMath()));

        Table["Exp"] = (ctx, a) =>
        {
            double r = Math.Exp(Need1(a).ToDoubleMath());
            if (double.IsInfinity(r))
                throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow");
            return VbsVariant.Of(r);
        };

        Table["Log"] = (ctx, a) =>
        {
            double d = Need1(a).ToDoubleMath();
            if (d <= 0) throw Err5();
            return VbsVariant.Of(Math.Log(d));
        };

        Table["Sqr"] = (ctx, a) =>
        {
            double d = Need1(a).ToDoubleMath();
            if (d < 0) throw Err5();
            return VbsVariant.Of(Math.Sqrt(d));
        };

        Table["Fix"] = (ctx, a) => FloorOrTrunc(Need1(a), truncate: true);
        Table["Int"] = (ctx, a) => FloorOrTrunc(Need1(a), truncate: false);

        Table["Sgn"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            double d = v.ToDoubleMath();
            return VbsVariant.Of((short)(d > 0 ? 1 : d < 0 ? -1 : 0));
        };

        // Round — banker's rounding (MidpointRounding.ToEven):
        // Round(2.5) = 2, Round(3.5) = 4, CInt-family behaviour.
        Table["Round"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            int places = (int)L(a, 1, 0);
            if (places < 0 || places > 15) throw Err5();
            if (v.IsIntegralSubtype) return v;
            double d = Math.Round(v.ToDoubleMath(), places, MidpointRounding.ToEven);
            return v.Type == VbVarType.Single ? VbsVariant.Of((float)d) : VbsVariant.Of(d);
        };

        // Rnd([number]): omitted/positive → next value; 0 → repeat last;
        // negative → deterministic value for that seed.
        Table["Rnd"] = (ctx, a) =>
        {
            if (a.Length > 0 && a[0].Type != VbVarType.Empty)
            {
                double n = a[0].ToDoubleMath();
                if (n == 0) return VbsVariant.Of(ctx.LastRnd);
                if (n < 0)
                {
                    ctx.LastRnd = new Random((int)n).NextDouble();
                    return VbsVariant.Of(ctx.LastRnd);
                }
            }
            ctx.LastRnd = ctx.Random.NextDouble();
            return VbsVariant.Of(ctx.LastRnd);
        };

        Table["Randomize"] = (ctx, a) =>
        {
            int seed = a.Length > 0 && a[0].Type != VbVarType.Empty
                ? (int)a[0].ToDoubleMath()
                : Environment.TickCount;
            ctx.Random = new Random(seed);
            return VbsVariant.Empty;
        };
    }

    private static VbsVariant FloorOrTrunc(VbsVariant v, bool truncate)
    {
        if (v.Type == VbVarType.Null) return VbsVariant.Null;
        if (v.IsIntegralSubtype) return v;
        double d = v.ToDoubleMath();
        double r = truncate ? (d < 0 ? Math.Ceiling(d) : Math.Floor(d)) : Math.Floor(d);
        return v.Type switch
        {
            VbVarType.Single => VbsVariant.Of((float)r),
            VbVarType.Currency => VbsVariant.Of(Math.Round((decimal)r, 4,
                MidpointRounding.ToEven)),
            _ => VbsVariant.Of(r)
        };
    }

    // ── Dates ───────────────────────────────────────────────────────────────

    private static void RegisterDates()
    {
        Table["Now"] = (ctx, a) => VbsVariant.Of(DateTime.Now);
        Table["Date"] = (ctx, a) => VbsVariant.Of(DateTime.Today);
        // Time-only Date variant — the OLE "time base" 1899-12-30 + now-time.
        Table["Time"] = (ctx, a) =>
            VbsVariant.Of(VbsDateUtil.TimeBase.Add(DateTime.Now.TimeOfDay));
        Table["Timer"] = (ctx, a) =>
            VbsVariant.Of((DateTime.Now - DateTime.Today).TotalSeconds);

        // DatePart-style component getters (Null-propagating, Integer results)
        VbsVariant PartFunc(VbsVariant[] a, Func<DateTime, int> part)
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            return VbsVariant.Of((short)part(v.ToDateVariant().AsDateTime()));
        }

        Table["Year"] = (ctx, a) => PartFunc(a, d => d.Year);
        Table["Month"] = (ctx, a) => PartFunc(a, d => d.Month);
        Table["Day"] = (ctx, a) => PartFunc(a, d => d.Day);
        Table["Hour"] = (ctx, a) => PartFunc(a, d => d.Hour);
        Table["Minute"] = (ctx, a) => PartFunc(a, d => d.Minute);
        Table["Second"] = (ctx, a) => PartFunc(a, d => d.Second);

        Table["Weekday"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            DateTime d = v.ToDateVariant().AsDateTime();
            int first = (int)L(a, 1, 1);
            if (first is < 1 or > 7) throw Err5();
            int wd = (((int)d.DayOfWeek - (first - 1)) % 7 + 7) % 7 + 1;
            return VbsVariant.Of((short)wd);
        };

        Table["MonthName"] = (ctx, a) =>
        {
            long m = Need1(a).ToLongMath();
            bool abbrev = Tri(a, 1, false);
            if (m < 1 || m > 12) throw Err5();
            var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
            return VbsVariant.Of(abbrev
                ? fmt.GetAbbreviatedMonthName((int)m)
                : fmt.GetMonthName((int)m));
        };

        Table["WeekdayName"] = (ctx, a) =>
        {
            long wd = Need1(a).ToLongMath();
            if (wd < 1 || wd > 7) throw Err5();
            bool abbrev = Tri(a, 1, false);
            int firstDay = (int)L(a, 2, 1);
            if (firstDay is < 1 or > 7) throw Err5();
            var dow = (DayOfWeek)((wd - 1 + (firstDay - 1)) % 7);
            var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
            return VbsVariant.Of(abbrev ? fmt.GetAbbreviatedDayName(dow)
                                        : fmt.GetDayName(dow));
        };

        // DateAdd(interval, number, date)
        Table["DateAdd"] = (ctx, a) =>
        {
            if (a.Length < 3) throw Err5();
            if (a[1].Type == VbVarType.Null || a[2].Type == VbVarType.Null)
                return VbsVariant.Null;
            string iv = S(a[0]).ToLowerInvariant();
            long n = a[1].ToLongMath();
            DateTime d = a[2].ToDateVariant().AsDateTime();
            try
            {
                DateTime r = iv switch
                {
                    "yyyy" => d.AddYears((int)n),
                    "q" => d.AddMonths((int)(3 * n)),
                    "m" => d.AddMonths((int)n),
                    "y" or "d" or "w" => d.AddDays(n),
                    "ww" => d.AddDays(7 * n),
                    "h" => d.AddHours(n),
                    "n" => d.AddMinutes(n),
                    "s" => d.AddSeconds(n),
                    _ => throw Err5()
                };
                return VbsVariant.Of(r);
            }
            catch (ArgumentOutOfRangeException)
            { throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow"); }
            catch (ArgumentException)
            { throw Err5(); }
        };

        // DateDiff(interval, d1, d2 [, firstdayofweek] [, firstweekofyear])
        Table["DateDiff"] = (ctx, a) =>
        {
            if (a.Length < 3) throw Err5();
            if (a[1].Type == VbVarType.Null || a[2].Type == VbVarType.Null)
                return VbsVariant.Null;
            string iv = S(a[0]).ToLowerInvariant();
            DateTime d1 = a[1].ToDateVariant().AsDateTime();
            DateTime d2 = a[2].ToDateVariant().AsDateTime();
            int firstDay = (int)L(a, 3, 1);
            if (firstDay is < 1 or > 7) throw Err5();

            long result = iv switch
            {
                "yyyy" => d2.Year - d1.Year,
                "q" => (d2.Year - d1.Year) * 4 +
                       ((d2.Month - 1) / 3 - (d1.Month - 1) / 3),
                "m" => (d2.Year - d1.Year) * 12 + (d2.Month - d1.Month),
                "y" or "d" => (long)(d2.Date - d1.Date).TotalDays,
                // "w": occurrences of date1's weekday; "ww": calendar weeks
                // measured from firstdayofweek — both count STRICTLY after d1.
                "w" => CountWeekday(d1.DayOfWeek, d1, d2),
                "ww" => CountWeekday((DayOfWeek)((firstDay - 1 + 7) % 7), d1, d2),
                "h" => (long)(d2 - d1).TotalHours,
                "n" => (long)(d2 - d1).TotalMinutes,
                "s" => (long)(d2 - d1).TotalSeconds,
                _ => throw Err5()
            };
            return VbsVariant.FromLong(result);
        };

        // DatePart(interval, date [, firstdayofweek] [, firstweekofyear])
        Table["DatePart"] = (ctx, a) =>
        {
            if (a.Length < 2) throw Err5();
            if (a[1].Type == VbVarType.Null) return VbsVariant.Null;
            string iv = S(a[0]).ToLowerInvariant();
            DateTime d = a[1].ToDateVariant().AsDateTime();
            int firstDay = (int)L(a, 2, 1);
            int firstWeek = (int)L(a, 3, 1);
            if (firstDay is < 1 or > 7) throw Err5();

            int result = iv switch
            {
                "yyyy" => d.Year,
                "q" => (d.Month - 1) / 3 + 1,
                "m" => d.Month,
                "y" => d.DayOfYear,
                "d" => d.Day,
                "w" => (((int)d.DayOfWeek - (firstDay - 1)) % 7 + 7) % 7 + 1,
                "ww" => WeekOfYear(d, firstDay, firstWeek),
                "h" => d.Hour,
                "n" => d.Minute,
                "s" => d.Second,
                _ => throw Err5()
            };
            return VbsVariant.Of((short)result);
        };

        Table["DateSerial"] = (ctx, a) =>
        {
            if (a.Length < 3) throw Err5();
            long y = a[0].ToLongMath();
            long m = a[1].ToLongMath();
            long dd = a[2].ToLongMath();
            if (y >= 0 && y <= 99) y += 1900;   // two-digit years are 19xx
            try
            {
                return VbsVariant.Of(
                    new DateTime((int)Math.Clamp(y, 1, 9999), 1, 1)
                        .AddMonths((int)m - 1)
                        .AddDays(dd - 1));
            }
            catch (ArgumentOutOfRangeException)
            { throw Err5(); }
        };

        Table["TimeSerial"] = (ctx, a) =>
        {
            if (a.Length < 3) throw Err5();
            long h = a[0].ToLongMath();
            long m = a[1].ToLongMath();
            long s = a[2].ToLongMath();
            try
            {
                return VbsVariant.Of(
                    VbsDateUtil.TimeBase.AddHours(h).AddMinutes(m).AddSeconds(s));
            }
            catch (ArgumentOutOfRangeException)
            { throw Err5(); }
        };

        Table["DateValue"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            return VbsVariant.Of(v.ToDateVariant().AsDateTime().Date);
        };

        Table["TimeValue"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            return VbsVariant.Of(
                VbsDateUtil.TimeBase.Add(v.ToDateVariant().AsDateTime().TimeOfDay));
        };

        // FormatDateTime(date [, namedformat])
        Table["FormatDateTime"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            DateTime d = v.ToDateVariant().AsDateTime();
            int fmt = (int)L(a, 1, 0);
            string s = fmt switch
            {
                1 => d.ToString("D", CultureInfo.CurrentCulture),
                2 => d.ToString("d", CultureInfo.CurrentCulture),
                3 => d.ToString("T", CultureInfo.CurrentCulture),
                4 => d.ToString("HH:mm", CultureInfo.InvariantCulture),
                _ => FormatGeneral(d)
            };
            return VbsVariant.Of(s);
        };

        // FormatNumber(expr [, decimals [, leadingzero [, negparens [, group]]]])
        Table["FormatNumber"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            double x = v.ToDoubleMath();
            int decimals = (int)L(a, 1, -1);
            if (decimals < -1 || decimals > 99) throw Err5();
            if (decimals < 0) decimals = 2;
            bool leading = Tri(a, 2, true);
            bool parens = Tri(a, 3, false);
            bool group = Tri(a, 4, true);
            return VbsVariant.Of(FormatNumeric(x, decimals, leading, parens, group));
        };

        Table["FormatCurrency"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            double x = v.ToDoubleMath();
            int decimals = (int)L(a, 1, -1);
            if (decimals < -1 || decimals > 99) throw Err5();
            if (decimals < 0) decimals = 2;
            return VbsVariant.Of(x.ToString("C" + decimals, CultureInfo.CurrentCulture));
        };

        Table["FormatPercent"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            double x = v.ToDoubleMath();
            int decimals = (int)L(a, 1, -1);
            if (decimals < -1 || decimals > 99) throw Err5();
            if (decimals < 0) decimals = 2;
            return VbsVariant.Of(x.ToString("P" + decimals, CultureInfo.CurrentCulture));
        };
    }

    private static string FormatGeneral(DateTime d)
    {
        bool hasDate = d.Date != VbsDateUtil.TimeBase.Date;
        bool hasTime = d.TimeOfDay != TimeSpan.Zero;
        if (hasDate && hasTime)
            return d.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
        if (hasDate)
            return d.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        return d.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
    }

    private static string FormatNumeric(double x, int decimals,
                                        bool leading, bool parens, bool group)
    {
        string pattern = group ? "N" + decimals : "F" + decimals;
        string s = x.ToString(pattern, CultureInfo.CurrentCulture);
        if (parens && x < 0)
            s = "(" + s.TrimStart('-') + ")";
        if (!leading && Math.Abs(x) < 1 && x != 0)
            s = s.Replace("0.", ".");
        return s;
    }

    /// <summary>Occurrences of `target` weekday STRICTLY after d1 and up to d2
    /// — the shared core of DateDiff "w" and "ww".</summary>
    private static long CountWeekday(DayOfWeek target, DateTime d1, DateTime d2)
    {
        int days = (int)(d2.Date - d1.Date).TotalDays;
        if (days <= 0) return 0;
        int offset = ((int)target - (int)d1.DayOfWeek + 7) % 7;
        if (offset == 0) offset = 7;   // strictly after d1
        if (days < offset) return 0;
        return 1 + (days - offset) / 7;
    }

    private static int WeekOfYear(DateTime d, int firstDayOfWeek, int firstWeekRule)
    {
        var cal = CultureInfo.CurrentCulture.Calendar;
        var rule = firstWeekRule switch
        {
            2 => CalendarWeekRule.FirstFourDayWeek,
            3 => CalendarWeekRule.FirstFullWeek,
            _ => CalendarWeekRule.FirstDay
        };
        var dow = (DayOfWeek)((firstDayOfWeek - 1 + 7) % 7);
        return cal.GetWeekOfYear(d, rule, dow);
    }

    // ── Type information ────────────────────────────────────────────────────

    private static void RegisterTypeInformation()
    {
        Table["TypeName"] = (ctx, a) => VbsVariant.Of(Need1(a).TypeNameOf());
        Table["VarType"] = (ctx, a) => VbsVariant.Of(Need1(a).VarTypeOf());

        Table["IsNull"] = (ctx, a) => VbsVariant.Of(Need1(a).Type == VbVarType.Null);
        Table["IsEmpty"] = (ctx, a) => VbsVariant.Of(Need1(a).Type == VbVarType.Empty);
        Table["IsArray"] = (ctx, a) => VbsVariant.Of(Need1(a).Type == VbVarType.Array);
        Table["IsObject"] = (ctx, a) => VbsVariant.Of(Need1(a).Type == VbVarType.Object);
        Table["IsDate"] = (ctx, a) => VbsVariant.Of(Need1(a).IsDateVariant());
        Table["IsNumeric"] = (ctx, a) => VbsVariant.Of(IsNumericValue(Need1(a)));

        Table["LBound"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            if (v.Type != VbVarType.Array) throw Err13();
            var arr = v.AsArray();
            if (arr.Erased)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            int dim = (int)L(a, 1, 1);
            if (dim < 1 || dim > arr.Rank)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            return VbsVariant.FromLong(0);   // LBound is always 0 in VBScript
        };

        Table["UBound"] = (ctx, a) =>
        {
            var v = Need1(a);
            if (v.Type == VbVarType.Null) return VbsVariant.Null;
            if (v.Type != VbVarType.Array) throw Err13();
            var arr = v.AsArray();
            if (arr.Erased)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            int dim = (int)L(a, 1, 1);
            if (dim < 1 || dim > arr.Rank)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            return VbsVariant.FromLong(arr.GetLength(dim - 1) - 1);
        };
    }

    private static bool IsNumericValue(VbsVariant v) => v.Type switch
    {
        VbVarType.Byte or VbVarType.Integer or VbVarType.Long or VbVarType.Single
            or VbVarType.Double or VbVarType.Currency or VbVarType.Empty => true,
        VbVarType.String => VbsVariant.TryParseNumeric(v.AsString(), out _),
        VbVarType.Object => v.TryDefault(out var d) && IsNumericValue(d),
        _ => false
    };

    // ── Host-routed functions ───────────────────────────────────────────────

    private static void RegisterHostFunctions()
    {
        // The Err object itself — Err.Number, Err.Clear, Err.Raise …
        Table["Err"] = (ctx, a) => VbsVariant.Of(ctx.Err);

        Table["MsgBox"] = (ctx, a) =>
        {
            string prompt = Need1(a).ToStringVariant();
            int buttons = (int)L(a, 1, 0);
            string title = a.Length > 2 ? S(a[2]) : "";
            var result = ctx.Host.MsgBox(prompt, (VbsMsgBoxButtons)(buttons & 0xF), title);
            return VbsVariant.Of((int)result);
        };

        Table["InputBox"] = (ctx, a) =>
        {
            string prompt = Need1(a).ToStringVariant();
            string title = a.Length > 1 ? S(a[1]) : "";
            string def = a.Length > 2 ? S(a[2]) : "";
            return VbsVariant.Of(ctx.Host.InputBox(prompt, title, def) ?? "");
        };

        Table["CreateObject"] = (ctx, a) =>
        {
            string progId = Need1(a).ToStringVariant();
            var obj = ctx.Host.CreateObject(progId);
            if (obj == null)
                throw new VbsRuntimeException(VbsErrorNumbers.ActiveXCantCreateObject,
                    $"ActiveX component can't create object: '{progId}'");
            return VbsVariant.Of(obj);
        };

        Table["GetObject"] = (ctx, a) =>
        {
            string path = a.Length > 0 ? S(a[0]) : "";
            string progId = a.Length > 1 ? S(a[1]) : "";
            if (path.Length == 0 && progId.Length == 0) throw Err5();
            var obj = ctx.Host.GetObject(path, progId);
            if (obj == null)
                throw new VbsRuntimeException(VbsErrorNumbers.ActiveXCantCreateObject,
                    "ActiveX component can't create object");
            return VbsVariant.Of(obj);
        };
    }
}

/// <summary>Small extension used by the string builtins for Null checks.</summary>
internal static class VbsArgExtensions
{
    public static bool AnyNull(this VbsVariant[] a)
    {
        foreach (var v in a)
            if (v.Type == VbVarType.Null)
                return true;
        return false;
    }
}