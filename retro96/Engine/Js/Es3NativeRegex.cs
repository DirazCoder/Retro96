using System;
using System.Collections.Generic;

namespace Retro96.Engine.Js
{
    /// <summary>
    /// A native ECMA-262 3rd-edition (§15.10.2) regular-expression matcher.
    ///
    /// The engine's default RegExp backend translates patterns to the .NET
    /// dialect (JsRuntime.TranslateToNet), but .NET's loop semantics differ
    /// from §15.10.2.5 in two observable ways for a quantified atom:
    ///   1. Empty-iteration rule — once the quantifier's minimum count is
    ///      satisfied, an iteration of the atom may NOT match the empty
    ///      string; the iteration is rejected outright and the loop exits
    ///      with the previous iteration's state. .NET instead accepts a
    ///      final empty iteration and lets its captures stick
    ///      (/(a|b*)*/.exec("a") gives $1 = "" instead of "a").
    ///   2. Per-iteration capture reset — the atom's capturing groups are
    ///      reset to undefined at the start of EVERY iteration. .NET keeps
    ///      captures from earlier iterations when an inner group does not
    ///      participate in the last one (/((foo)|(bar))*/.exec("foobar")
    ///      gives $2 = "foo" instead of undefined).
    /// Patterns whose observable behaviour depends on those corners (an
    /// unbounded quantifier over a nullable atom, or any quantifier over an
    /// atom with optional inner captures) are routed here; everything else
    /// stays on the .NET path, so this matcher only has to be right for the
    /// subset it actually executes.
    ///
    /// Parsing mirrors the leniencies the .NET translation layer grew around
    /// the 1990s corpus (legacy octal escapes, literal braces, dash-adjacent-
    /// to-shorthand rules); any pattern this parser cannot handle simply
    /// reports "not routable" and the .NET backend keeps it.
    /// </summary>
    internal sealed class Es3NativeRegex
    {
        // ─────────────────────────────────────────────────────────────────
        // Public surface
        // ─────────────────────────────────────────────────────────────────

        public readonly struct MatchResult
        {
            public readonly int Index;
            public readonly int Length;
            /// <summary>1-based capture texts; null = group did not participate.</summary>
            public readonly string?[] Groups;

            public MatchResult(int index, int length, string?[] groups)
            { Index = index; Length = length; Groups = groups; }
        }

        /// <summary>
        /// True when the pattern's observable semantics need §15.10.2.5 loop
        /// rules (see class comment). Never throws: an unparseable pattern
        /// simply stays on the .NET backend.
        /// </summary>
        public static bool ShouldRoute(string source)
        {
            try
            {
                var p = new Parser(source);
                Node? tree = p.Parse();
                if (tree == null || !p.Ok) return false;
                return RouteNeeded(tree);
            }
            catch { return false; }
        }

        public Es3NativeRegex(string source, bool ignoreCase, bool multiline)
        {
            var p = new Parser(source);
            Node? tree = p.Parse();
            if (tree == null || !p.Ok)
                throw new JsSyntaxErrorException("invalid regular expression");
            _ignoreCase = ignoreCase;
            _multiline = multiline;
            _groups = p.GroupCount;
            Compile(tree);
        }

        /// <summary>Leftmost match at or after <paramref name="start"/>.</summary>
        public bool TryMatch(string input, int start, out MatchResult result)
        {
            if (start < 0) start = 0;
            long steps = 0;
            var caps = new int[2 * (_groups + 1)];
            var counters = new int[_loops];
            var iterPos = new int[_loops];
            var bt = new Stack<BtEntry>(_loops + 8);
            var looks = new Stack<LookFrame>(4);

            for (int p = start; p <= input.Length; p++)
            {
                int[]? win = Run(input, p, caps, counters, iterPos, bt, looks, ref steps);
                if (win != null)
                {
                    var groups = new string?[_groups + 1];
                    groups[0] = input[win[0]..win[1]];
                    for (int g = 1; g <= _groups; g++)
                        groups[g] = win[2 * g] >= 0 && win[2 * g + 1] >= 0
                            ? input[win[2 * g]..win[2 * g + 1]]
                            : null;
                    result = new MatchResult(win[0], win[1] - win[0], groups);
                    return true;
                }
            }
            result = default;
            return false;
        }

        // ─────────────────────────────────────────────────────────────────
        // Character helpers (ES3 definitions, matching the .NET layer)
        // ─────────────────────────────────────────────────────────────────

        internal static bool IsLineTerm(char c) =>
            c is '\n' or '\r' or '\u2028' or '\u2029';

        private static bool IsWord(char c) =>
            c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';

        /// <summary>§15.10.2.8: fold case for the i flag, but never across
        /// the ASCII/non-ASCII boundary (the ı → I and ß-style traps).</summary>
        private static char Canonicalize(char c)
        {
            char u = char.ToUpperInvariant(c);
            if (u >= 128 && c < 128) return c;
            if (u < 128 && c >= 128) return c;
            return u;
        }

        // ─────────────────────────────────────────────────────────────────
        // AST
        // ─────────────────────────────────────────────────────────────────

        private enum AnchorKind { Bol, Eol, WordB, NWordB }

        private abstract class Node
        {
            /// <summary>Can this fragment match the empty string?</summary>
            public abstract bool Nullable { get; }
        }

        private sealed class NSeq : Node
        {
            public readonly Node[] Items;
            public NSeq(Node[] items) { Items = items; }
            public override bool Nullable
            {
                get { foreach (var i in Items) if (!i.Nullable) return false; return true; }
            }
        }

        private sealed class NAlt : Node
        {
            public readonly Node[] Branches;
            public NAlt(Node[] b) { Branches = b; }
            public override bool Nullable
            {
                get { foreach (var b in Branches) if (b.Nullable) return true; return false; }
            }
        }

        private sealed class NQuant : Node
        {
            public readonly Node Child;
            public readonly int Min;
            public readonly long Max;      // < 0 = unbounded
            public readonly bool Greedy;
            public readonly int Pi, Pc;    // §15.10.2.5 captures before / inside the atom
            public NQuant(Node child, int min, long max, bool greedy, int pi, int pc)
            { Child = child; Min = min; Max = max; Greedy = greedy; Pi = pi; Pc = pc; }
            public override bool Nullable => Min == 0 || Child.Nullable;
        }

        private sealed class NChar : Node
        {
            public readonly char C;
            public NChar(char c) { C = c; }
            public override bool Nullable => false;
        }

        private sealed class NClass : Node
        {
            public readonly ClassSet Set;
            public NClass(ClassSet set) { Set = set; }
            public override bool Nullable => false;
        }

        private sealed class NAny : Node
        {
            public override bool Nullable => false;
        }

        private sealed class NAnchor : Node
        {
            public readonly AnchorKind Kind;
            public NAnchor(AnchorKind k) { Kind = k; }
            public override bool Nullable => true;
        }

        private sealed class NGroup : Node
        {
            public readonly Node Child;
            public readonly int Index;
            public NGroup(Node child, int index) { Child = child; Index = index; }
            public override bool Nullable => Child.Nullable;
        }

        private sealed class NBackref : Node
        {
            public readonly int N;
            public NBackref(int n) { N = n; }
            // an unset backreference matches the empty string (ES3-era rule
            // the .NET layer also implements)
            public override bool Nullable => true;
        }

        private sealed class NLook : Node
        {
            public readonly Node Child;
            public readonly bool Negative;
            public NLook(Node child, bool negative) { Child = child; Negative = negative; }
            public override bool Nullable => true;
        }

        /// <summary>A character class: literal ranges plus (for the \d \s \w
        /// shorthands) their ASCII expansions; \D \S \W inside a class never
        /// reach here (the parser punts those to the .NET backend).</summary>
        internal sealed class ClassSet
        {
            public bool Negated;
            public readonly List<(char Lo, char Hi)> Ranges = new();

            public bool Matches(char c, bool ignoreCase)
            {
                bool inSet = false;
                foreach (var (lo, hi) in Ranges)
                {
                    if (c >= lo && c <= hi) { inSet = true; break; }
                    if (ignoreCase)
                    {
                        char cc = Canonicalize(c);
                        if (cc >= lo && cc <= hi) { inSet = true; break; }
                    }
                }
                return Negated ? !inSet : inSet;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Parser (ES3 §15.10.1 grammar + the corpus-validated leniencies)
        // ─────────────────────────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public bool Ok = true;
            public int GroupCount => _captured;

            private int _captured;          // running capture count while parsing

            public Parser(string s) { _s = s; }

            private static readonly (char, char)[] DigitRanges = { ('0', '9') };
            private static readonly (char, char)[] SpaceRanges =
                { (' ', ' '), ('\t', '\t'), ('\n', '\n'), ('\r', '\r'), ('\f', '\f'),
                  ('\v', '\v'), ('\u00a0', '\u00a0'), ('\u2028', '\u2028'),
                  ('\u2029', '\u2029'), ('\ufeff', '\ufeff') };
            private static readonly (char, char)[] WordRanges =
                { ('A', 'Z'), ('a', 'z'), ('0', '9'), ('_', '_') };

            public Node? Parse()
            {
                if (_s.Length == 0) return new NSeq(Array.Empty<Node>());
                Node? d = Disjunction();
                if (!Ok || _i != _s.Length) return null;   // trailing ')' etc.
                return d;
            }

            private Node Disjunction()
            {
                Node first = Alternative();
                if (!Ok || Peek() != '|') return first;
                var list = new List<Node> { first };
                while (Ok && Peek() == '|')
                {
                    _i++;
                    list.Add(Alternative());
                }
                return new NAlt(list.ToArray());
            }

            private Node Alternative()
            {
                var list = new List<Node>();
                while (Ok && _i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '|' || c == ')') break;
                    Term(list);
                }
                return new NSeq(list.ToArray());
            }

            private void Term(List<Node> acc)
            {
                char c = _s[_i];

                // Assertions (§15.10.2.6) — never quantifiable here
                if (c == '^') { _i++; acc.Add(new NAnchor(AnchorKind.Bol)); return; }
                if (c == '$') { _i++; acc.Add(new NAnchor(AnchorKind.Eol)); return; }
                if (c == '\\')
                {
                    if (_i + 1 < _s.Length && _s[_i + 1] == 'b')
                    { _i += 2; acc.Add(new NAnchor(AnchorKind.WordB)); return; }
                    if (_i + 1 < _s.Length && _s[_i + 1] == 'B')
                    { _i += 2; acc.Add(new NAnchor(AnchorKind.NWordB)); return; }
                }
                if (c == '(' && _i + 1 < _s.Length && _s[_i + 1] == '?')
                {
                    if (_i + 2 < _s.Length && _s[_i + 2] == '=')
                    {
                        _i += 3;
                        var d = Disjunction();
                        Expect(')');
                        acc.Add(new NLook(d!, false));
                        return;
                    }
                    if (_i + 2 < _s.Length && _s[_i + 2] == '!')
                    {
                        _i += 3;
                        var d = Disjunction();
                        Expect(')');
                        acc.Add(new NLook(d!, true));
                        return;
                    }
                    // '(?:' falls through to Atom; anything else is left to .NET
                }

                int pi = _captured;
                Node? atom = Atom();
                if (!Ok || atom == null) return;

                var (min, max, hasQ, lazy) = TryQuantifier();
                if (!hasQ) { acc.Add(atom); return; }
                if (atom is NLook) { Ok = false; return; }   // quantified assertion: .NET's verdict
                acc.Add(new NQuant(atom, min, max, !lazy, pi, _captured - pi));
            }

            private (int min, long max, bool has, bool lazy) TryQuantifier()
            {
                if (_i >= _s.Length) return (0, 0, false, false);
                int min; long max;
                switch (_s[_i])
                {
                    case '*': min = 0; max = -1; _i++; break;
                    case '+': min = 1; max = -1; _i++; break;
                    case '?': min = 0; max = 1; _i++; break;
                    case '{':
                        {
                            int save = _i;
                            _i++;
                            long? n0 = ReadNumber();
                            long? n1 = null;
                            bool open = false;
                            if (n0 != null && _i < _s.Length && _s[_i] == ',')
                            {
                                _i++;
                                if (_i < _s.Length && _s[_i] == '}') open = true;
                                else n1 = ReadNumber();
                            }
                            if (n0 == null || _i >= _s.Length || _s[_i] != '}' ||
                                (n1 == null && !open))
                            { _i = save; return (0, 0, false, false); }   // literal '{'
                            _i++;
                            // clamp absurd counts exactly like the .NET layer
                            const long Clamp = 100000;
                            long a = Math.Min(n0.Value, Clamp);
                            long b = open ? -1 : Math.Min(n1!.Value, Clamp);
                            if (b >= 0 && a > b) { Ok = false; return (0, 0, false, false); }
                            min = (int)a; max = b;
                            break;
                        }
                    default:
                        return (0, 0, false, false);
                }

                bool lazy = _i < _s.Length && _s[_i] == '?';
                if (lazy) _i++;

                // a second quantifier in a row is a job for .NET's verdict
                if (_i < _s.Length && (_s[_i] == '*' || _s[_i] == '+' || _s[_i] == '?' || BraceQuantifierAhead()))
                { Ok = false; return (0, 0, false, false); }
                return (min, max, true, lazy);
            }

            private bool BraceQuantifierAhead()
            {
                if (_i >= _s.Length || _s[_i] != '{') return false;
                int j = _i + 1;
                int digits = 0;
                while (j < _s.Length && char.IsDigit(_s[j])) { j++; digits++; }
                if (digits == 0) return false;
                if (j < _s.Length && _s[j] == ',')
                {
                    j++;
                    while (j < _s.Length && char.IsDigit(_s[j])) j++;
                }
                return j < _s.Length && _s[j] == '}';
            }

            private long? ReadNumber()
            {
                long v = 0; int digits = 0;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9' && digits < 18)
                { v = v * 10 + (_s[_i] - '0'); _i++; digits++; }
                if (digits == 0 && _i < _s.Length && char.IsDigit(_s[_i]))
                { _i++; return long.MaxValue; }   // absurd run: clamped later anyway
                return digits == 0 ? null : v;
            }

            private Node? Atom()
            {
                if (_i >= _s.Length) { Ok = false; return null; }
                char c = _s[_i];
                switch (c)
                {
                    case '.': _i++; return new NAny();
                    case '[': return ClassAtom();
                    case '(':
                        if (_i + 1 < _s.Length && _s[_i + 1] == '?')
                        {
                            if (_i + 2 < _s.Length && _s[_i + 2] == ':')
                            {
                                _i += 3;
                                var d = Disjunction();
                                Expect(')');
                                return Ok ? d : null;
                            }
                            Ok = false; return null;   // '(?<' and friends: .NET decides
                        }
                        _i++;
                        int idx = ++_captured;      // this group's own number
                        var body = Disjunction();
                        Expect(')');
                        return Ok ? new NGroup(body!, idx) : null;
                    case '\\': return EscapeAtom();
                    case '*': case '+': case '?':
                        Ok = false; return null;       // nothing to repeat
                    case '{':
                        if (BraceQuantifierAhead()) { Ok = false; return null; }
                        _i++; return new NChar('{');
                    case '^': case '$': case '|': case ')':
                        Ok = false; return null;       // unreachable from Term; safety
                    default:
                        _i++; return new NChar(c);
                }
            }

            private Node? EscapeAtom()
            {
                if (_i + 1 >= _s.Length) { Ok = false; return null; }  // trailing backslash
                char n = _s[_i + 1];
                switch (n)
                {
                    case 'd': _i += 2; return Shorthand(DigitRanges, false);
                    case 'D': _i += 2; return Shorthand(DigitRanges, true);
                    case 's': _i += 2; return Shorthand(SpaceRanges, false);
                    case 'S': _i += 2; return Shorthand(SpaceRanges, true);
                    case 'w': _i += 2; return Shorthand(WordRanges, false);
                    case 'W': _i += 2; return Shorthand(WordRanges, true);
                    case 'f': _i += 2; return new NChar('\f');
                    case 'n': _i += 2; return new NChar('\n');
                    case 'r': _i += 2; return new NChar('\r');
                    case 't': _i += 2; return new NChar('\t');
                    case 'v': _i += 2; return new NChar('\v');
                    case 'c':
                        if (_i + 2 < _s.Length && char.IsLetter(_s[_i + 2]))
                        { char ch = (char)(_s[_i + 2] & 0x1F); _i += 3; return new NChar(ch); }
                        _i += 1; return new NChar('c');   // literal 'c' (regress-334158)
                    case 'x':
                        if (HexAt(_i + 2, 2, out char hx)) { _i += 4; return new NChar(hx); }
                        _i += 1; return new NChar('x');   // literal 'x' (unicode-001)
                    case 'u':
                        if (HexAt(_i + 2, 4, out char hu)) { _i += 6; return new NChar(hu); }
                        _i += 1; return new NChar('u');
                    default:
                        if (n is >= '0' and <= '9') return NumericEscape();
                        _i += 2; return new NChar(n);     // IdentityEscape
                }
            }

            /// <summary>Backreference-or-octal, mirroring the .NET layer's
            /// resolution: longest decimal run naming an existing group,
            /// else a single digit if that names one, else legacy octal.</summary>
            private Node? NumericEscape()
            {
                char n = _s[_i + 1];
                int groups = CountGroups(_s);
                int j = _i + 1, runLen = 0;
                long run = 0;
                while (j < _s.Length && runLen < 3 && char.IsDigit(_s[j]))
                { run = run * 10 + (_s[j] - '0'); runLen++; j++; }

                if (n != '0')
                {
                    if (run <= groups && run >= 1)
                    { _i += 1 + runLen; return new NBackref((int)run); }
                    if (runLen >= 2 && n - '0' <= groups)
                    { _i += 2; return new NBackref(n - '0'); }
                }

                if (n is >= '0' and <= '7')
                {
                    int val = 0, digits = 0;
                    j = _i + 1;
                    while (j < _s.Length && digits < 3 && _s[j] is >= '0' and <= '7' &&
                           val * 8 + (_s[j] - '0') <= 255)
                    { val = val * 8 + (_s[j] - '0'); digits++; j++; }
                    if (digits > 0) { _i += 1 + digits; return new NChar((char)val); }
                }
                _i += 2; return new NChar(n);   // e.g. \8 with no group 8
            }

            private Node? ClassAtom()
            {
                _i++;                                        // '['
                var set = new ClassSet();
                if (_i < _s.Length && _s[_i] == '^') { set.Negated = true; _i++; }

                while (true)
                {
                    if (_i >= _s.Length) { Ok = false; return null; }  // unterminated
                    if (_s[_i] == ']') { _i++; break; }                 // [] / [^] included

                    if (!ParseClassItem(out char c1, out var s1)) return null;
                    bool madeRange = false;
                    if (_i < _s.Length && _s[_i] == '-' &&
                        _i + 1 < _s.Length && _s[_i + 1] != ']')
                    {
                        _i++;                                // '-'
                        if (!ParseClassItem(out char c2, out var s2)) return null;
                        if (s1 == null && s2 == null)
                        {
                            if (c1 > c2) { Ok = false; return null; }  // .NET rejects [z-a]
                            set.Ranges.Add((c1, c2));
                        }
                        else
                        {
                            // a dash adjacent to a shorthand is a literal
                            // (regress-375715) — emit both items and the dash
                            AddItem(set, c1, s1);
                            set.Ranges.Add(('-', '-'));
                            AddItem(set, c2, s2);
                        }
                        madeRange = true;
                    }
                    if (!madeRange) AddItem(set, c1, s1);
                }
                return new NClass(set);
            }

            /// <summary>One class member: a single char or a shorthand set
            /// (\d \s \w). \D \S \W inside a class return false — the whole
            /// pattern then stays on the .NET backend (status quo).</summary>
            private bool ParseClassItem(out char ch, out ClassSet? shorthand)
            {
                ch = '\0'; shorthand = null;
                char c = _s[_i];
                if (c != '\\') { ch = c; _i++; return true; }
                if (_i + 1 >= _s.Length) return false;
                char n = _s[_i + 1];
                switch (n)
                {
                    case 'd': _i += 2; shorthand = Shorthand(DigitRanges, false).Set; return true;
                    case 's': _i += 2; shorthand = Shorthand(SpaceRanges, false).Set; return true;
                    case 'w': _i += 2; shorthand = Shorthand(WordRanges, false).Set; return true;
                    case 'D': case 'S': case 'W': return false;    // punt to .NET
                    case 'b': _i += 2; ch = '\x08'; return true;   // backspace in class
                    case 'f': _i += 2; ch = '\f'; return true;
                    case 'n': _i += 2; ch = '\n'; return true;
                    case 'r': _i += 2; ch = '\r'; return true;
                    case 't': _i += 2; ch = '\t'; return true;
                    case 'v': _i += 2; ch = '\v'; return true;
                    case 'c':
                        if (_i + 2 < _s.Length && char.IsLetter(_s[_i + 2]))
                        { ch = (char)(_s[_i + 2] & 0x1F); _i += 3; return true; }
                        _i += 1; ch = 'c'; return true;
                    case 'x':
                        if (HexAt(_i + 2, 2, out char hx)) { _i += 4; ch = hx; return true; }
                        _i += 1; ch = 'x'; return true;
                    case 'u':
                        if (HexAt(_i + 2, 4, out char hu)) { _i += 6; ch = hu; return true; }
                        _i += 1; ch = 'u'; return true;
                    default:
                        if (n is >= '0' and <= '9')
                        {
                            // in a class a numeric escape is always octal
                            int val = 0, digits = 0;
                            int j = _i + 1;
                            while (j < _s.Length && digits < 3 && _s[j] is >= '0' and <= '7' &&
                                   val * 8 + (_s[j] - '0') <= 255)
                            { val = val * 8 + (_s[j] - '0'); digits++; j++; }
                            if (digits > 0) { _i += 1 + digits; ch = (char)val; return true; }
                            _i += 2; ch = n; return true;
                        }
                        _i += 2; ch = n; return true;             // \] \- \\ \^ \/ …
                }
            }

            private static void AddItem(ClassSet set, char c, ClassSet? shorthand)
            {
                if (shorthand != null) set.Ranges.AddRange(shorthand.Ranges);
                else set.Ranges.Add((c, c));
            }

            private static NClass Shorthand((char, char)[] ranges, bool negated)
            {
                var set = new ClassSet { Negated = negated };
                set.Ranges.AddRange(ranges);
                return new NClass(set);
            }

            private char Peek() => _i < _s.Length ? _s[_i] : '\0';

            private void Expect(char c)
            {
                if (_i < _s.Length && _s[_i] == c) _i++;
                else Ok = false;
            }

            private bool HexAt(int start, int count, out char value)
            {
                value = '\0';
                if (start + count > _s.Length) return false;
                int v = 0;
                for (int k = 0; k < count; k++)
                {
                    int d = _s[start + k] switch
                    {
                        >= '0' and <= '9' => _s[start + k] - '0',
                        >= 'a' and <= 'f' => _s[start + k] - 'a' + 10,
                        >= 'A' and <= 'F' => _s[start + k] - 'A' + 10,
                        _ => -1
                    };
                    if (d < 0) return false;
                    v = (v << 4) | d;
                }
                value = (char)v;
                return true;
            }
        }

        /// <summary>Class-aware capture count (used for backreference
        /// resolution, which the naive .NET-layer scan cannot do).</summary>
        private static int CountGroups(string s)
        {
            int count = 0, i = 0;
            bool inClass = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { i += 2; continue; }
                if (inClass)
                {
                    if (c == ']') inClass = false;
                }
                else if (c == '[')
                {
                    if (i + 1 < s.Length && s[i + 1] == ']') i += 2;
                    else if (i + 2 < s.Length && s[i + 1] == '^' && s[i + 2] == ']') i += 3;
                    else inClass = true;
                }
                else if (c == '(' && !(i + 1 < s.Length && s[i + 1] == '?'))
                    count++;
                i++;
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────────
        // Routing analysis (§15.10.2.5 corners)
        // ─────────────────────────────────────────────────────────────────

        private static bool RouteNeeded(Node n)
        {
            switch (n)
            {
                case NQuant q:
                    if (RouteNeeded(q.Child)) return true;
                    int inner = CountCaptures(q.Child) - (q.Child is NGroup ? 1 : 0);
                    if (q.Max < 0)
                    {
                        // unbounded: the empty-iteration rule applies
                        if (q.Child.Nullable) return true;
                        if (inner >= 1 && HasOptionality(q.Child)) return true;
                    }
                    else if (inner >= 1 && HasOptionality(q.Child)) return true;
                    return false;
                case NSeq s:
                    foreach (var i in s.Items) if (RouteNeeded(i)) return true;
                    return false;
                case NAlt a:
                    foreach (var b in a.Branches) if (RouteNeeded(b)) return true;
                    return false;
                case NGroup g: return RouteNeeded(g.Child);
                case NLook l: return RouteNeeded(l.Child);
                default: return false;
            }
        }

        private static int CountCaptures(Node n)
        {
            switch (n)
            {
                case NGroup g: return 1 + CountCaptures(g.Child);
                case NSeq s:
                    int t = 0; foreach (var i in s.Items) t += CountCaptures(i); return t;
                case NAlt a:
                    int u = 0; foreach (var b in a.Branches) u += CountCaptures(b); return u;
                case NQuant q: return CountCaptures(q.Child);
                case NLook l: return CountCaptures(l.Child);
                default: return 0;
            }
        }

        /// <summary>Does the fragment contain a min-0 quantifier or a real
        /// alternation — i.e. can a capture inside it be skipped on some
        /// iteration while sticking from an earlier one?</summary>
        private static bool HasOptionality(Node n)
        {
            switch (n)
            {
                case NQuant q: return q.Min == 0 || HasOptionality(q.Child);
                case NAlt: return true;
                case NSeq s:
                    foreach (var i in s.Items) if (HasOptionality(i)) return true;
                    return false;
                case NGroup g: return HasOptionality(g.Child);
                case NLook l: return HasOptionality(l.Child);
                default: return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Compiler: AST → instruction program
        // ─────────────────────────────────────────────────────────────────

        private enum Op : byte
        {
            Char, CharIC, Class, Any,
            Split, Jmp, Save, Reset, Backref,
            Bol, Eol, WordB, NWordB,
            Look, LookEnd,
            LoopInit, LoopMax, LoopMark, LoopCheck, LoopExit,
            Fail, Match
        }

        private sealed class Instr
        {
            public Op Op;
            public char C;
            public int X, Y, Z;
            public long L;
            public bool B1;
            public ClassSet? Set;
        }

        private readonly List<Instr> _prog = new();
        private int _loops;
        private readonly bool _ignoreCase, _multiline;
        private readonly int _groups;

        private const long StepBudget = 4_000_000;
        private const int MaxBacktrack = 50_000;

        private int Emit(Op op) { _prog.Add(new Instr { Op = op }); return _prog.Count - 1; }
        private int Emit(Op op, int x) { _prog.Add(new Instr { Op = op, X = x }); return _prog.Count - 1; }
        private int Emit(Op op, int x, int y)
        { _prog.Add(new Instr { Op = op, X = x, Y = y }); return _prog.Count - 1; }

        private void Compile(Node root)
        {
            Emit(Op.Save, 0);
            EmitNode(root);
            Emit(Op.Save, 1);
            Emit(Op.Match);
        }

        private void EmitNode(Node n)
        {
            switch (n)
            {
                case NSeq s:
                    foreach (var i in s.Items) EmitNode(i);
                    break;
                case NAlt a:
                    {
                        var endJmps = new List<int>();
                        for (int k = 0; k < a.Branches.Length - 1; k++)
                        {
                            int sp = Emit(Op.Split);                 // patched below
                            _prog[sp].X = _prog.Count;               // branch k body (fall-through)
                            EmitNode(a.Branches[k]);
                            endJmps.Add(Emit(Op.Jmp));
                            _prog[sp].Y = _prog.Count;               // backtrack: next branch
                        }
                        EmitNode(a.Branches[^1]);
                        int end = _prog.Count;
                        foreach (var j in endJmps) _prog[j].X = end;
                        break;
                    }
                case NQuant q:
                    EmitQuant(q);
                    break;
                case NChar c:
                    _prog.Add(new Instr { Op = _ignoreCase ? Op.CharIC : Op.Char, C = c.C });
                    break;
                case NClass cl:
                    _prog.Add(new Instr { Op = Op.Class, Set = cl.Set });
                    break;
                case NAny:
                    Emit(Op.Any);
                    break;
                case NAnchor an:
                    Emit(an.Kind switch
                    {
                        AnchorKind.Bol => Op.Bol,
                        AnchorKind.Eol => Op.Eol,
                        AnchorKind.WordB => Op.WordB,
                        _ => Op.NWordB,
                    });
                    break;
                case NGroup g:
                    Emit(Op.Save, g.Index * 2);
                    EmitNode(g.Child);
                    Emit(Op.Save, g.Index * 2 + 1);
                    break;
                case NBackref b:
                    Emit(Op.Backref, b.N);
                    break;
                case NLook l:
                    {
                        int lk = Emit(Op.Look);
                        _prog[lk].B1 = l.Negative;
                        _prog[lk].Y = _prog.Count;               // body
                        EmitNode(l.Child);
                        Emit(Op.LookEnd);
                        _prog[lk].X = _prog.Count;               // return point
                        break;
                    }
            }
        }

        private void EmitQuant(NQuant q)
        {
            int id = _loops++;
            Emit(Op.LoopInit, id);
            int sp = Emit(Op.Split);                              // patched below
            int body = _prog.Count;
            if (q.Max >= 0)
                _prog.Add(new Instr { Op = Op.LoopMax, X = id, L = q.Max });
            Emit(Op.LoopMark, id);                                // ++counter, iterPos = pos
            if (q.Pc > 0)
                Emit(Op.Reset, q.Pi, q.Pc);                       // per-iteration capture reset
            EmitNode(q.Child);
            if (q.Max < 0)
                _prog.Add(new Instr { Op = Op.LoopCheck, X = id, Y = q.Min, Z = sp });  // empty rule
            _prog.Add(new Instr { Op = Op.Jmp, X = sp });
            // exit — Split's second alternative lands here
            _prog[sp].X = q.Greedy ? body : _prog.Count;
            _prog[sp].Y = q.Greedy ? _prog.Count : body;
            _prog.Add(new Instr { Op = Op.LoopExit, X = id, Y = q.Min });   // below-min gate
        }

        /// <summary>Debug dump of the compiled program (development aid for
        /// the regex matcher; not used by the engine itself).</summary>
        internal void DumpProgram()
        {
            for (int i = 0; i < _prog.Count; i++)
            {
                var p = _prog[i];
                string d = p.Op switch
                {
                    Op.Char or Op.CharIC => $" {p.C}",
                    Op.Class => $" ranges={p.Set!.Ranges.Count} neg={p.Set.Negated}",
                    Op.Split => $" X={p.X} Y={p.Y}",
                    Op.Jmp => $" ->{p.X}",
                    Op.Save => $" slot={p.X}",
                    Op.Reset => $" pi={p.X} pc={p.Y}",
                    Op.Backref => $" n={p.X}",
                    Op.Look => $" neg={p.B1} body={p.Y} ret={p.X}",
                    Op.LoopInit => $" id={p.X}",
                    Op.LoopMax => $" id={p.X} max={p.L}",
                    Op.LoopMark => $" id={p.X}",
                    Op.LoopCheck => $" id={p.X} min={p.Y} split={p.Z}",
                    Op.LoopExit => $" id={p.X} min={p.Y}",
                    _ => ""
                };
                Console.Error.WriteLine($"{i,3}: {p.Op}{d}");
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // The backtracking VM
        // ─────────────────────────────────────────────────────────────────

        private readonly struct BtEntry
        {
            public readonly int Pc, Pos;
            public readonly int[] Caps, Counters, IterPos;
            public readonly int LookDepth;
            public BtEntry(int pc, int pos, int[] caps, int[] counters, int[] iterPos, int depth)
            { Pc = pc; Pos = pos; Caps = caps; Counters = counters; IterPos = iterPos; LookDepth = depth; }
        }

        private sealed class LookFrame
        {
            public int RetPc, Pos;
            public int[] Caps = Array.Empty<int>(), Counters = Array.Empty<int>(), IterPos = Array.Empty<int>();
            public bool Negative;
        }

        /// <summary>Returns the captures array of the winning attempt, or
        /// null when the pattern cannot match at startPos. (Backtracking
        /// swaps array references, so the result travels via the return
        /// value — callers must not cache the pre-call arrays.)</summary>
        private int[]? Run(string input, int startPos,
                         int[] caps, int[] counters, int[] iterPos,
                         Stack<BtEntry> bt, Stack<LookFrame> looks, ref long steps)
        {
            Array.Fill(caps, -1);
            Array.Clear(counters, 0, counters.Length);
            Array.Clear(iterPos, 0, iterPos.Length);
            bt.Clear();
            looks.Clear();

            int pos = startPos;
            int pc = 0;

            while (true)
            {
                if (++steps > StepBudget || bt.Count > MaxBacktrack)
                    throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
                var ins = _prog[pc];
                bool ok = true;
                switch (ins.Op)
                {
                    case Op.Char:
                        ok = pos < input.Length && input[pos] == ins.C;
                        if (ok) pos++;
                        break;
                    case Op.CharIC:
                        ok = pos < input.Length && Canonicalize(input[pos]) == Canonicalize(ins.C);
                        if (ok) pos++;
                        break;
                    case Op.Class:
                        ok = pos < input.Length && ins.Set!.Matches(input[pos], _ignoreCase);
                        if (ok) pos++;
                        break;
                    case Op.Any:
                        ok = pos < input.Length && !IsLineTerm(input[pos]);
                        if (ok) pos++;
                        break;
                    case Op.Split:
                        bt.Push(new BtEntry(ins.Y, pos, (int[])caps.Clone(), (int[])counters.Clone(),
                                            (int[])iterPos.Clone(), looks.Count));
                        pc = ins.X;
                        continue;
                    case Op.Jmp:
                        pc = ins.X;
                        continue;
                    case Op.Save:
                        caps[ins.X] = pos;
                        break;
                    case Op.Reset:
                        for (int g = ins.X + 1; g <= ins.X + ins.Y; g++)
                        { caps[2 * g] = -1; caps[2 * g + 1] = -1; }
                        break;
                    case Op.Backref:
                        {
                            int s = caps[2 * ins.X], e = caps[2 * ins.X + 1];
                            if (s < 0 || e < 0) break;             // unset → empty (ES3 era)
                            int len = e - s;
                            if (pos + len > input.Length) { ok = false; break; }
                            for (int k = 0; k < len; k++)
                            {
                                char a = input[s + k], b2 = input[pos + k];
                                if (_ignoreCase ? Canonicalize(a) != Canonicalize(b2) : a != b2)
                                { ok = false; break; }
                            }
                            if (ok) pos += len;
                            break;
                        }
                    case Op.Bol:
                        ok = pos == 0 || (_multiline && pos > 0 && IsLineTerm(input[pos - 1]));
                        break;
                    case Op.Eol:
                        ok = pos == input.Length || (_multiline && IsLineTerm(input[pos]));
                        break;
                    case Op.WordB:
                    case Op.NWordB:
                        {
                            bool w1 = pos > 0 && IsWord(input[pos - 1]);
                            bool w2 = pos < input.Length && IsWord(input[pos]);
                            bool boundary = w1 != w2;
                            ok = boundary == (ins.Op == Op.WordB);
                            break;
                        }
                    case Op.Look:
                        {
                            var f = new LookFrame
                            {
                                RetPc = ins.X,
                                Pos = pos,
                                Caps = (int[])caps.Clone(),
                                Counters = (int[])counters.Clone(),
                                IterPos = (int[])iterPos.Clone(),
                                Negative = ins.B1
                            };
                            looks.Push(f);
                            pc = ins.Y;
                            continue;
                        }
                    case Op.LookEnd:
                        {
                            var f = looks.Peek();
                            if (f.Negative)
                            {
                                // body matched: negative lookahead fails; the
                                // unwinder drops the frame and its alternatives
                                looks.Pop();
                                ok = false;
                                break;
                            }
                            // positive: zero-width, captures made inside persist
                            pos = f.Pos;
                            looks.Pop();
                            break;
                        }
                    case Op.LoopInit:
                        counters[ins.X] = 0;
                        break;
                    case Op.LoopMax:
                        ok = counters[ins.X] < ins.L;
                        break;
                    case Op.LoopMark:
                        counters[ins.X]++;
                        iterPos[ins.X] = pos;
                        break;
                    case Op.LoopCheck:
                        // §15.10.2.5: past the minimum, an iteration may not
                        // match empty — reject it and let backtracking exit
                        if (pos == iterPos[ins.X] && counters[ins.X] > ins.Y)
                        { ok = false; break; }
                        pc = ins.Z;
                        continue;
                    case Op.LoopExit:
                        ok = counters[ins.X] >= ins.Y;             // min-iterations gate
                        break;
                    case Op.Fail:
                        ok = false;
                        break;
                    case Op.Match:
                        return caps;
                }

                if (ok) { pc++; continue; }

                // ── backtrack ──
                bool resumed = false;
                while (bt.Count > 0)
                {
                    var e = bt.Peek();
                    if (e.LookDepth > looks.Count)
                    { bt.Pop(); continue; }        // stale: dead lookahead body
                    while (looks.Count > e.LookDepth)
                    {
                        var f = looks.Peek();
                        if (f.Negative)
                        {
                            // body failed → negative lookahead succeeds
                            looks.Pop();
                            pos = f.Pos;
                            caps = f.Caps; counters = f.Counters; iterPos = f.IterPos;
                            pc = f.RetPc;
                            resumed = true;
                            break;
                        }
                        looks.Pop();               // positive body failed → propagate
                    }
                    if (resumed) break;
                    bt.Pop();
                    pos = e.Pos;
                    caps = e.Caps; counters = e.Counters; iterPos = e.IterPos;
                    pc = e.Pc;
                    resumed = true;
                    break;
                }
                if (!resumed) return null;
            }
        }
    }
}
