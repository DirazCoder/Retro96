using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Js
{
    /// <summary>One capture of a match: a non-participating group reports
    /// Success = false (the caller then shows `undefined`, regress-123437).</summary>
    internal readonly struct JsRegexGroup
    {
        public readonly bool Success;
        public readonly string Value;
        public JsRegexGroup(bool success, string value)
        { Success = success; Value = value ?? ""; }
    }

    /// <summary>Backend-neutral match result: whole match plus captures,
    /// with .NET-style NextMatch support for the interpreter's replace loop.</summary>
    internal sealed class JsRegexMatch
    {
        public static readonly JsRegexMatch Failure =
            new(false, 0, 0, "", Array.Empty<JsRegexGroup>(), null, null, null);

        public bool Success { get; }
        public int Index { get; }
        public int Length { get; }
        public string Value { get; }
        /// <summary>Groups[0] is the whole match; captures follow 1-based.</summary>
        public JsRegexGroup[] Groups { get; }

        private readonly JsRegex? _owner;
        private readonly string? _input;
        private readonly Match? _netMatch;

        internal JsRegexMatch(bool success, int index, int length, string value,
                              JsRegexGroup[] groups,
                              JsRegex? owner, string? input, Match? netMatch)
        {
            Success = success; Index = index; Length = length; Value = value;
            Groups = groups; _owner = owner; _input = input; _netMatch = netMatch;
        }

        public JsRegexMatch NextMatch()
        {
            if (_netMatch != null) return JsRegex.FromNet(_netMatch.NextMatch(), _input!);
            if (_owner == null || _input == null || !Success) return Failure;
            // .NET NextMatch: continue after the match, advancing past an
            // empty one so callers cannot loop forever
            int from = Index + (Length == 0 ? 1 : Length);
            return _owner.Match(_input, from);
        }
    }

    /// <summary>
    /// The RegExp front end shared by every runtime call site (exec, test,
    /// match, replace, search, split). Most patterns run on the .NET engine
    /// via JsRuntime's translation layer; patterns whose §15.10.2.5 loop
    /// semantics .NET cannot express (see Es3NativeRegex) are routed to the
    /// native ES3 matcher, so both engines are reachable behind one API.
    /// </summary>
    internal sealed class JsRegex
    {
        private readonly Regex? _net;
        private readonly Es3NativeRegex? _native;

        public JsRegex(string source, RegexOptions options)
        {
            bool ic = options.HasFlag(RegexOptions.IgnoreCase);
            bool ml = options.HasFlag(RegexOptions.Multiline);
            if (Es3NativeRegex.ShouldRoute(source))
            {
                try { _native = new Es3NativeRegex(source, ic, ml); return; }
                catch { /* fall through to the .NET backend */ }
            }
            _net = JsRuntime.CreateNetRegex(source, options);
        }

        public JsRegexMatch Match(string input) => Match(input, 0);

        public JsRegexMatch Match(string input, int start)
        {
            if (_native != null)
            {
                return _native.TryMatch(input, start, out var r)
                    ? FromNative(r, input)
                    : JsRegexMatch.Failure;
            }
            return FromNet(_net!.Match(input, start), input);
        }

        /// <summary>All non-overlapping matches (global String.match).</summary>
        public List<JsRegexMatch> Matches(string input)
        {
            var list = new List<JsRegexMatch>();
            if (_net != null)
            {
                foreach (Match m in _net.Matches(input)) list.Add(FromNet(m, input));
                return list;
            }
            int pos = 0;
            while (pos <= input.Length)
            {
                var m = Match(input, pos);
                if (!m.Success) break;
                list.Add(m);
                int next = m.Index + m.Length;
                if (next <= pos) next = pos + 1;   // empty match: advance
                pos = next;
            }
            return list;
        }

        /// <summary>Replace with ES3 §15.5.4.11 substitution; count &lt; 0
        /// replaces all (the .NET backend keeps its own .NET-substitution
        /// behaviour, which the corpus already validated).</summary>
        public string Replace(string input, string replacement, int count)
        {
            if (_net != null)
                return count < 0 ? _net.Replace(input, replacement)
                                 : _net.Replace(input, replacement, count);

            var sb = new StringBuilder();
            int pos = 0, done = 0;
            while (done != count)
            {
                var m = Match(input, pos);
                if (!m.Success) break;
                sb.Append(input, pos, m.Index - pos);
                sb.Append(Substitute(replacement, input, m));
                int end = m.Index + m.Length;
                if (m.Length == 0)
                {
                    if (end >= input.Length) { pos = input.Length; break; }
                    sb.Append(input[end]);
                    end++;
                }
                pos = end;
                done++;
                if (pos > input.Length) { pos = input.Length; break; }
            }
            if (pos <= input.Length) sb.Append(input[Math.Min(pos, input.Length)..]);
            return sb.ToString();
        }

        internal static JsRegexMatch FromNet(Match m, string input)
        {
            if (!m.Success) return JsRegexMatch.Failure;
            var groups = new JsRegexGroup[m.Groups.Count];
            for (int i = 0; i < m.Groups.Count; i++)
            {
                var g = m.Groups[i];
                groups[i] = new JsRegexGroup(g.Success, g.Value);
            }
            return new JsRegexMatch(true, m.Index, m.Length, m.Value, groups,
                                    null, input, m);
        }

        private JsRegexMatch FromNative(in Es3NativeRegex.MatchResult r, string input)
        {
            var groups = new JsRegexGroup[r.Groups.Length];
            for (int i = 0; i < r.Groups.Length; i++)
            {
                string? g = r.Groups[i];
                groups[i] = new JsRegexGroup(g != null, g ?? "");
            }
            return new JsRegexMatch(true, r.Index, r.Length,
                                    input.Substring(r.Index, r.Length), groups,
                                    this, input, null);
        }

        /// <summary>ES3 §15.5.4.11 GetSubstitution for the $-forms the era
        /// defines: $$, $&amp;, $`, $', $1..$99 (two digits preferred, one
        /// digit as fallback, literal $ otherwise).</summary>
        internal static string Substitute(string replacement, string s, JsRegexMatch m)
        {
            var sb = new StringBuilder();
            int captures = m.Groups.Length - 1;
            for (int i = 0; i < replacement.Length; i++)
            {
                char c = replacement[i];
                if (c != '$' || i + 1 >= replacement.Length) { sb.Append(c); continue; }
                char n1 = replacement[i + 1];
                switch (n1)
                {
                    case '$': sb.Append('$'); i++; break;
                    case '&': sb.Append(m.Value); i++; break;
                    case '`': sb.Append(s[..m.Index]); i++; break;
                    case '\'': sb.Append(s[(m.Index + m.Length)..]); i++; break;
                    default:
                        if (char.IsDigit(n1))
                        {
                            if (i + 2 < replacement.Length && char.IsDigit(replacement[i + 2]))
                            {
                                int nn = (n1 - '0') * 10 + (replacement[i + 2] - '0');
                                if (nn >= 1 && nn <= captures)
                                {
                                    sb.Append(m.Groups[nn].Success ? m.Groups[nn].Value : "");
                                    i += 2;
                                    continue;
                                }
                            }
                            int one = n1 - '0';
                            if (one >= 1 && one <= captures)
                            {
                                sb.Append(m.Groups[one].Success ? m.Groups[one].Value : "");
                                i++;
                                continue;
                            }
                            sb.Append('$');   // unknown group: literal $
                        }
                        else sb.Append('$');
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
