using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Vbs;

/// <summary>
/// The VBScript.RegExp object (5.0). Properties Pattern / IgnoreCase /
/// Global; methods Test(s), Replace(s, replacement) and Execute(s) →
/// Matches collection of Match objects (Value / FirstIndex / Length).
///
/// Era notes: the replacement string is LITERAL text — $1..$9 substitution
/// and SubMatches arrived with VBScript 5.5 (2000) and are deliberately
/// absent. FirstIndex is 0-based, as in the real engine.
///
/// Pages get an instance in two ways:
///   - `Set re = New RegExp` — the intrinsic construction this engine adds,
///     because the browser host denies CreateObject for safety;
///   - CreateObject("VBScript.RegExp") on hosts that opt in via TryCreate
///     (WSH-style and test hosts).
/// </summary>
public sealed class VbsRegExpObject : IVbsDispatchObject
{
    public string Pattern { get; set; } = "";
    public bool IgnoreCase { get; set; }
    public bool Global { get; set; }

    /// <summary>Host helper: returns a fresh RegExp for the
    /// "VBScript.RegExp" ProgID (case-insensitive), else null so the host
    /// falls through to its normal (usually deny) behavior.</summary>
    public static VbsRegExpObject? TryCreate(string progId) =>
        progId.Equals("VBScript.RegExp", StringComparison.OrdinalIgnoreCase)
            ? new VbsRegExpObject()
            : null;

    public string VbsTypeName => "RegExp";

    private Regex Compile()
    {
        try
        {
            return new Regex(Pattern,
                IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            throw new VbsRuntimeException(VbsErrorNumbers.RegExpSyntax,
                "Syntax error in regular expression");
        }
    }

    private static string ArgString(VbsVariant[] args, int i)
    {
        if (i >= args.Length)
            throw new VbsRuntimeException(VbsErrorNumbers.InvalidProcedureCall,
                "Invalid procedure call or argument");
        return args[i].Type == VbVarType.Null ? "" : args[i].ToStringVariant();
    }

    public bool TryGetMember(string name, out VbsVariant value)
    {
        switch (name.ToLowerInvariant())
        {
            case "pattern": value = VbsVariant.Of(Pattern); return true;
            case "ignorecase": value = VbsVariant.Of(IgnoreCase); return true;
            case "global": value = VbsVariant.Of(Global); return true;
        }
        value = default;
        return false;
    }

    public bool TrySetMember(string name, VbsVariant value)
    {
        switch (name.ToLowerInvariant())
        {
            case "pattern": Pattern = value.ToStringVariant(); return true;
            case "ignorecase": IgnoreCase = value.ToBoolVariant(); return true;
            case "global": Global = value.ToBoolVariant(); return true;
        }
        return false;
    }

    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    {
        result = VbsVariant.Empty;
        switch (name.ToLowerInvariant())
        {
            case "test":
                result = VbsVariant.Of(Compile().IsMatch(ArgString(args, 0)));
                return true;

            case "replace":
            {
                string s = ArgString(args, 0);
                string repl = args.Length > 1 ? ArgString(args, 1) : "";
                var re = Compile();
                // Literal replacement (MatchEvaluator bypasses $ substitution);
                // count 1 unless Global, per the 5.0 docs.
                result = VbsVariant.Of(re.Replace(s, _ => repl, Global ? -1 : 1));
                return true;
            }

            case "execute":
            {
                string s = ArgString(args, 0);
                var re = Compile();
                var matches = new List<Match>();
                if (Global)
                {
                    foreach (Match m in re.Matches(s))
                        if (m.Success) matches.Add(m);
                }
                else
                {
                    Match first = re.Match(s);
                    if (first.Success) matches.Add(first);
                }
                result = VbsVariant.Of(new VbsMatchesObject(matches));
                return true;
            }
        }
        return false;
    }

    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;
    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }
    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    { items = Array.Empty<VbsVariant>(); return false; }
}

/// <summary>The Matches collection returned by RegExp.Execute: Count, Item(i),
/// matches(i) and For Each enumeration yield Match objects.</summary>
public sealed class VbsMatchesObject : IVbsDispatchObject
{
    private readonly List<Match> _matches;

    internal VbsMatchesObject(List<Match> matches) => _matches = matches;

    public string VbsTypeName => "Matches";

    internal int Count => _matches.Count;

    private VbsVariant At(int i)
    {
        if (i < 0 || i >= _matches.Count)
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        return VbsVariant.Of(new VbsMatchObject(_matches[i]));
    }

    public bool TryGetMember(string name, out VbsVariant value)
    {
        if (name.ToLowerInvariant() == "count")
        {
            value = VbsVariant.FromLong(_matches.Count);
            return true;
        }
        value = default;
        return false;
    }

    public bool TrySetMember(string name, VbsVariant value) => false;

    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    {
        result = VbsVariant.Empty;
        if (name.ToLowerInvariant() == "item" && args.Length >= 1)
        {
            result = At((int)args[0].ToLongMath());
            return true;
        }
        return false;
    }

    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    {
        result = VbsVariant.Empty;
        if (args.Length >= 1)
        {
            result = At((int)args[0].ToLongMath());
            return true;
        }
        return false;
    }

    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;

    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    {
        items = _matches.Select(m => VbsVariant.Of(new VbsMatchObject(m)));
        return true;
    }
}

/// <summary>One RegExp match: Value (matched text), FirstIndex (0-based
/// offset) and Length. Read-only.</summary>
public sealed class VbsMatchObject : IVbsDispatchObject
{
    public string Value { get; }
    public int FirstIndex { get; }
    public int Length { get; }

    internal VbsMatchObject(Match m)
    {
        Value = m.Value;
        FirstIndex = m.Index;
        Length = m.Length;
    }

    public string VbsTypeName => "Match";

    public bool TryGetMember(string name, out VbsVariant value)
    {
        switch (name.ToLowerInvariant())
        {
            case "value": value = VbsVariant.Of(Value); return true;
            case "firstindex": value = VbsVariant.FromLong(FirstIndex); return true;
            case "length": value = VbsVariant.FromLong(Length); return true;
        }
        value = default;
        return false;
    }

    public bool TrySetMember(string name, VbsVariant value) => false;
    public bool TryInvoke(string name, VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }
    public bool TryGetDefault(out VbsVariant value) { value = default; return false; }
    public bool TrySetDefault(VbsVariant value) => false;
    public bool TryInvokeDefault(VbsVariant[] args, out VbsVariant result)
    { result = default; return false; }
    public bool TryEnumerate(out IEnumerable<VbsVariant> items)
    { items = Array.Empty<VbsVariant>(); return false; }
}
