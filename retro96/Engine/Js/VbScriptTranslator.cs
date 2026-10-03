using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Js;

/// <summary>
/// Small, deterministic VBScript-to-JavaScript 1.2 compatibility bridge for
/// the browser's 1996-era scripting surface.  It intentionally implements the
/// syntax used by legacy web pages (Dim/Const, Sub/Function, If/Else,
/// For/While/Do, Select Case, Call, Set, common string/conversion functions,
/// MsgBox/InputBox, and VB boolean/string operators) and emits only syntax
/// understood by the existing in-process JS interpreter.
/// </summary>
public static class VbScriptTranslator
{
    private static readonly Regex ReComment = new("^\\s*(?:Rem(?:\\s|$)|')", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDim = new("^\\s*(?:Dim|(?:Private|Public)\\s+Dim|(?:Private|Public))\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReConst = new("^\\s*(?:Const|(?:Private|Public)\\s+Const)\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReReDimStatement = new("^\\s*ReDim\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReSub = new("^\\s*(?:Public\\s+|Private\\s+|Friend\\s+)?Sub\\s+([A-Za-z_$][\\w$]*)\\s*(?:\\(([^)]*)\\))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReFunction = new("^\\s*(?:Public\\s+|Private\\s+|Friend\\s+)?Function\\s+([A-Za-z_$][\\w$]*)\\s*(?:\\(([^)]*)\\))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReIfBlock = new("^\\s*If\\s+(.+?)\\s+Then\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReElseIf = new("^\\s*ElseIf\\s+(.+?)\\s+Then\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReWhile = new("^\\s*While\\s+(.+?)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReFor = new("^\\s*For\\s+([A-Za-z_$][\\w$]*)\\s*=\\s*(.+?)\\s+To\\s+(.+?)(?:\\s+Step\\s+(.+))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReForEach = new("^\\s*For\\s+Each\\s+([A-Za-z_$][\\w$]*)\\s+In\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDoWhile = new("^\\s*Do\\s+While\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDoUntil = new("^\\s*Do\\s+Until\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReLoopWhile = new("^\\s*Loop\\s+While\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReLoopUntil = new("^\\s*Loop\\s+Until\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReSelectCase = new("^\\s*Select\\s+Case\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReCase = new("^\\s*Case\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReAssignment = new("^\\s*(?:Set\\s+)?([A-Za-z_$][\\w$]*(?:\\.[A-Za-z_$][\\w$]*|\\([^)]*\\))*)\\s*=\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReModifierSkip = new("^(?:Public\\s+|Private\\s+|Friend\\s+)?(?:Class\\s|Property\\s)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string Prologue = @"
function __vbsLen(v){return String(v).length;}
function __vbsLCase(v){return String(v).toLowerCase();}
function __vbsUCase(v){return String(v).toUpperCase();}
function __vbsTrim(v){return String(v).replace(/^\s+|\s+$/g, '');}
function __vbsLeft(v,n){var s=String(v);n=Math.max(0,Number(n)||0);return s.substring(0,Math.min(s.length,n));}
function __vbsRight(v,n){var s=String(v);n=Math.max(0,Number(n)||0);return n>=s.length?s:s.substring(s.length-n);}
function __vbsMid(v,start,len){var s=String(v);var p=Math.max(0,(Number(start)||1)-1);if(len===undefined)return s.substring(p);return s.substring(p,p+Math.max(0,Number(len)||0));}
function __vbsInStr(a,b,c,d){var start=1,hay,needle;if(d===undefined){hay=String(a);needle=String(b);}else{start=Math.max(1,Number(a)||1);hay=String(b);needle=String(c);}var p=hay.indexOf(needle,start-1);return p<0?0:p+1;}
function __vbsReplace(v,find,repl){return String(v).split(String(find)).join(String(repl));}
function __vbsCStr(v){return String(v);}
function __vbsCInt(v){var n=Number(v);return isNaN(n)?0:Math.round(n);}
function __vbsCLng(v){var n=Number(v);return isNaN(n)?0:Math.round(n);}
function __vbsCDbl(v){var n=Number(v);return isNaN(n)?0:n;}
function __vbsIsNumeric(v){var n=Number(v);return !isNaN(n);}
function __vbsAbs(v){return Math.abs(Number(v)||0);}
function __vbsSgn(v){var n=Number(v)||0;return n<0?-1:(n>0?1:0);}
";

    private sealed record FunctionScope(string Name, bool IsFunction);

    public static string Translate(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var output = new StringBuilder(Prologue.Length + source.Length + 64);
        output.Append(Prologue);
        var scopes = new Stack<FunctionScope>();
        var selectStack = new Stack<(string Var, bool HasCase)>();
        int selectCounter = 0;
        int forEachCounter = 0;
        var logicalLines = JoinLineContinuations(source);

        foreach (string raw in logicalLines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || ReComment.IsMatch(line) ||
                line.Equals("Option Explicit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("On Error Resume Next", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("On Error GoTo 0", StringComparison.OrdinalIgnoreCase))
                continue;

            EmitLine(line);
        }

        while (scopes.Count > 0) { output.Append("}\n"); scopes.Pop(); }
        return output.ToString();

        // ── per-line dispatch ────────────────────────────────────────────

        void EmitLine(string line)
        {
            // Single-line If must be handled BEFORE colon-splitting: VB runs
            // every colon-separated statement in the Then/Else arm
            // conditionally, so the arms must stay attached to the condition.
            if (TrySingleLineIf(line, out var condition, out var thenArm, out var elseArm))
            {
                output.Append("if (").Append(RewriteExpression(condition, true)).Append(") { ");
                AppendArm(thenArm);
                output.Append(" }");
                if (elseArm != null)
                {
                    output.Append(" else { ");
                    AppendArm(elseArm);
                    output.Append(" }");
                }
                output.Append('\n');
                return;
            }

            // Colon-separated statement lists: a = 1: b = 2
            if (IndexOfTopLevel(line, ':') > 0)
            {
                foreach (var part in SplitTopLevel(line, ':'))
                {
                    var p = part.Trim();
                    if (p.Length > 0) EmitLine(p);
                }
                return;
            }

            var mSub = ReSub.Match(line);
            if (mSub.Success)
            {
                output.Append("function ").Append(mSub.Groups[1].Value)
                      .Append('(').Append(NormalizeArgs(mSub.Groups[2].Value)).Append(") {\n");
                scopes.Push(new FunctionScope(mSub.Groups[1].Value, false));
                return;
            }

            var mFn = ReFunction.Match(line);
            if (mFn.Success)
            {
                output.Append("function ").Append(mFn.Groups[1].Value)
                      .Append('(').Append(NormalizeArgs(mFn.Groups[2].Value)).Append(") {\n");
                scopes.Push(new FunctionScope(mFn.Groups[1].Value, true));
                return;
            }

            if (line.Equals("End Sub", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("End Function", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("}\n");
                if (scopes.Count > 0) scopes.Pop();
                return;
            }

            if (line.Equals("Exit Sub", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("Exit Function", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("return;\n");
                return;
            }

            if (line.Equals("Exit Do", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("Exit For", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("break;\n");
                return;
            }

            if (line.Equals("End If", StringComparison.OrdinalIgnoreCase)) { output.Append("}\n"); return; }
            if (line.Equals("Wend", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("Loop", StringComparison.OrdinalIgnoreCase)) { output.Append("}\n"); return; }
            if (line.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Next ", StringComparison.OrdinalIgnoreCase)) { output.Append("}\n"); return; }

            var mElseIf = ReElseIf.Match(line);
            if (mElseIf.Success)
            {
                output.Append("} else if (")
                      .Append(RewriteExpression(mElseIf.Groups[1].Value, condition: true))
                      .Append(") {\n");
                return;
            }
            if (line.Equals("Else", StringComparison.OrdinalIgnoreCase)) { output.Append("} else {\n"); return; }

            var mIf = ReIfBlock.Match(line);
            if (mIf.Success)
            {
                output.Append("if (")
                      .Append(RewriteExpression(mIf.Groups[1].Value, condition: true))
                      .Append(") {\n");
                return;
            }

            var mWhile = ReWhile.Match(line);
            if (mWhile.Success)
            {
                output.Append("while (")
                      .Append(RewriteExpression(mWhile.Groups[1].Value, condition: true))
                      .Append(") {\n");
                return;
            }

            // Bare "Do ... Loop" — a real do-while loop, not garbage.
            if (line.Equals("Do", StringComparison.OrdinalIgnoreCase)) { output.Append("do {\n"); return; }

            var mDoWhile = ReDoWhile.Match(line);
            if (mDoWhile.Success)
            {
                output.Append("while (")
                      .Append(RewriteExpression(mDoWhile.Groups[1].Value, true))
                      .Append(") {\n");
                return;
            }
            var mDoUntil = ReDoUntil.Match(line);
            if (mDoUntil.Success)
            {
                output.Append("while (!(")
                      .Append(RewriteExpression(mDoUntil.Groups[1].Value, true))
                      .Append(")) {\n");
                return;
            }
            var mLoopWhile = ReLoopWhile.Match(line);
            if (mLoopWhile.Success)
            {
                output.Append("} while (")
                      .Append(RewriteExpression(mLoopWhile.Groups[1].Value, true))
                      .Append(");\n");
                return;
            }
            var mLoopUntil = ReLoopUntil.Match(line);
            if (mLoopUntil.Success)
            {
                output.Append("} while (!(")
                      .Append(RewriteExpression(mLoopUntil.Groups[1].Value, true))
                      .Append("));\n");
                return;
            }

            var mSelect = ReSelectCase.Match(line);
            if (mSelect.Success)
            {
                selectCounter++;
                string var = "__vbs_sel_" + selectCounter;
                output.Append("var ").Append(var).Append(" = ")
                      .Append(RewriteExpression(mSelect.Groups[1].Value, false)).Append(";\n");
                selectStack.Push((var, false));
                return;
            }

            if (line.Equals("End Select", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("}\n");
                if (selectStack.Count > 0) selectStack.Pop();
                return;
            }

            var mCase = ReCase.Match(line);
            if (mCase.Success && selectStack.Count > 0)
            {
                string value = mCase.Groups[1].Value.Trim();
                var (var, hasCase) = selectStack.Pop();
                selectStack.Push((var, true));

                if (value.Equals("Else", StringComparison.OrdinalIgnoreCase))
                {
                    output.Append(hasCase ? "} else {" : "if (true) {").Append('\n');
                    return;
                }

                var conditions = new List<string>();
                foreach (var item in SplitTopLevel(value, ','))
                {
                    var c = CaseCondition(item, var);
                    if (c.Length > 0) conditions.Add(c);
                }
                if (conditions.Count == 0) conditions.Add(var + " == " + var);

                output.Append(hasCase ? "} else if (" : "if (")
                      .Append(string.Join(" || ", conditions))
                      .Append(") {\n");
                return;
            }

            var mForEach = ReForEach.Match(line);
            if (mForEach.Success)
            {
                // Unique counter per loop: nested For Each used to share one
                // __vbs_i and the inner loop clobbered the outer's index.
                forEachCounter++;
                string iter = "__vbs_i_" + forEachCounter;
                string variable = mForEach.Groups[1].Value;
                string collection = RewriteExpression(mForEach.Groups[2].Value, false);
                output.Append("for (var ").Append(iter).Append(" = 0; ").Append(iter)
                      .Append(" < (").Append(collection).Append(").length; ").Append(iter).Append("++) { var ")
                      .Append(variable).Append(" = (").Append(collection).Append(")[").Append(iter).Append("];\n");
                return;
            }

            var mFor = ReFor.Match(line);
            if (mFor.Success)
            {
                string variable = mFor.Groups[1].Value;
                string start = RewriteExpression(mFor.Groups[2].Value, false);
                string end = RewriteExpression(mFor.Groups[3].Value, false);
                string stepText = mFor.Groups[4].Success ? RewriteExpression(mFor.Groups[4].Value, false) : "1";
                output.Append("for (var ").Append(variable).Append(" = ").Append(start)
                      .Append("; (").Append(stepText).Append(") >= 0 ? ")
                      .Append(variable).Append(" <= ").Append(end).Append(" : ")
                      .Append(variable).Append(" >= ").Append(end).Append("; ")
                      .Append(variable).Append(" += (").Append(stepText).Append(")) {\n");
                return;
            }

            // Const before Dim: the extended Dim pattern also matches
            // "Private Const …" and must not swallow it.
            var mConst = ReConst.Match(line);
            if (mConst.Success)
            {
                output.Append("var ").Append(NormalizeConstList(mConst.Groups[1].Value)).Append(";\n");
                return;
            }

            // ReDim/ReDim Preserve — array re-sizing is not modelled by the
            // bridge; skipping keeps existing bindings intact (unlike a
            // re-declaration, which would wipe the Preserve'd contents).
            if (ReReDimStatement.IsMatch(line)) return;

            var mDim = ReDim.Match(line);
            if (mDim.Success)
            {
                output.Append("var ").Append(NormalizeDimList(mDim.Groups[1].Value)).Append(";\n");
                return;
            }

            if (ReModifierSkip.IsMatch(line) ||
                line.Equals("End Class", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("End Property", StringComparison.OrdinalIgnoreCase))
                return;

            if (line.StartsWith("On Error ", StringComparison.OrdinalIgnoreCase)) return;

            string translated = TranslateStatement(line, scopes.Count > 0 ? scopes.Peek() : null);
            if (translated.Length > 0)
                output.Append(translated).Append('\n');
        }

        void AppendArm(string arm)
        {
            foreach (var part in SplitTopLevel(arm, ':'))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                var translated = TranslateStatement(p, scopes.Count > 0 ? scopes.Peek() : null);
                if (translated.Length > 0) output.Append(translated).Append(' ');
            }
        }
    }

    /// <summary>Detects the single-line If form: If cond Then stmt [: stmts]
    /// [Else stmt [: stmts]].  Block form ("If … Then" at end of line) is
    /// rejected so it falls through to the block-If handler.</summary>
    private static bool TrySingleLineIf(string line, out string condition,
                                        out string thenArm, out string? elseArm)
    {
        condition = ""; thenArm = ""; elseArm = null;
        if (!line.StartsWith("If ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ReIfBlock.IsMatch(line))
            return false;                       // block form
        // Quote-aware: " Then " inside a string literal used to split early.
        int thenAt = IndexOfUnquoted(line, " Then ");
        if (thenAt < 0)
            return false;
        condition = line[3..thenAt].Trim();
        string rest = line[(thenAt + 6)..].Trim();
        int elseAt = IndexOfUnquotedKeyword(rest, "Else");
        if (elseAt >= 0)
        {
            thenArm = rest[..elseAt].Trim();
            elseArm = rest[(elseAt + 4)..].Trim();
        }
        else
        {
            thenArm = rest;
        }
        return thenArm.Length > 0 || elseArm != null;
    }

    /// <summary>One Case item → a JS condition on the select variable.
    /// Supports plain values, comma lists (joined by the caller),
    /// "Is &lt;op&gt; value" and "low To high" range forms.</summary>
    private static string CaseCondition(string item, string selectVar)
    {
        item = item.Trim();
        if (item.Length == 0) return "";

        var mIs = Regex.Match(item, "^Is\\s*(=|<>|<=|>=|<|>)\\s*(.+)$", RegexOptions.IgnoreCase);
        if (mIs.Success)
        {
            string op = mIs.Groups[1].Value.ToLowerInvariant() switch
            {
                "=" => "==",
                "<>" => "!=",
                _ => mIs.Groups[1].Value
            };
            return selectVar + " " + op + " " + RewriteExpression(mIs.Groups[2].Value, false);
        }

        int toAt = IndexOfUnquotedKeyword(item, "To");
        if (toAt > 0)
        {
            string lo = item[..toAt].Trim();
            string hi = item[(toAt + 2)..].Trim();
            if (lo.Length > 0 && hi.Length > 0)
                return selectVar + " >= " + RewriteExpression(lo, false) +
                       " && " + selectVar + " <= " + RewriteExpression(hi, false);
        }

        return selectVar + " == " + RewriteExpression(item, false);
    }

    private static List<string> JoinLineContinuations(string source)
    {
        var logical = new List<string>();
        var physical = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var pending = new StringBuilder();
        foreach (var raw in physical)
        {
            string line = raw.TrimEnd();
            if (line.EndsWith("_", StringComparison.Ordinal))
            {
                pending.Append(line[..^1]).Append(' ');
                continue;
            }
            if (pending.Length > 0)
            {
                pending.Append(line);
                logical.Add(pending.ToString());
                pending.Clear();
            }
            else logical.Add(line);
        }
        if (pending.Length > 0) logical.Add(pending.ToString());
        return logical;
    }

    private static string TranslateStatement(string line, FunctionScope? scope)
    {
        line = StripTrailingComment(line);
        if (line.Length == 0) return string.Empty;

        if (line.StartsWith("Call ", StringComparison.OrdinalIgnoreCase))
        {
            string call = line[5..].Trim();
            return NormalizeCall(call) + ";";
        }

        // Both "MsgBox x" and "MsgBox(x)" forms — the parenthesised no-space
        // form used to fall through and emit an undefined MsgBox identifier.
        string? args = CallArguments(line, "MsgBox");
        if (args != null)
            return "alert(" + RewriteExpression(args, false) + ");";

        args = CallArguments(line, "InputBox");
        if (args != null)
            return "prompt(" + RewriteExpression(args, false) + ");";

        args = CallArguments(line, "document.writeln");
        if (args != null)
            return "document.write(" + RewriteExpression(args, false) + "+'\\n');";

        args = CallArguments(line, "document.write");
        if (args != null)
            return "document.write(" + RewriteExpression(args, false) + ");";

        if (line.StartsWith("Set ", StringComparison.OrdinalIgnoreCase))
            line = line[4..].Trim();

        var assign = ReAssignment.Match(line);
        if (assign.Success)
        {
            string lhs = assign.Groups[1].Value.Trim();
            string rhs = RewriteExpression(assign.Groups[2].Value.Trim(), false);
            if (scope?.IsFunction == true && lhs.Equals(scope.Name, StringComparison.OrdinalIgnoreCase))
                return "return " + rhs + ";";
            return lhs + " = " + rhs + ";";
        }

        // VBScript permits `Foo arg1, arg2` without parentheses.
        if (LooksLikeBareCall(line))
        {
            int space = IndexOfUnquotedWhitespace(line);
            string name = line[..space].Trim();
            string callArgs = line[(space + 1)..].Trim();
            return name + "(" + RewriteCommaArguments(callArgs) + ");";
        }

        return RewriteExpression(line, false) + (line.EndsWith(";", StringComparison.Ordinal) ? "" : ";");
    }

    /// <summary>Extracts the argument text of `name args` / `name(args)`,
    /// or null when the line is not that call form.</summary>
    private static string? CallArguments(string line, string name)
    {
        if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return null;
        int rest = name.Length;
        if (rest >= line.Length) return string.Empty;   // bare keyword — no-arg call
        char next = line[rest];
        if (next != ' ' && next != '(') return null;    // longer identifier (MsgBoxFoo)
        string tail = line[rest..].Trim();
        if (tail.StartsWith('('))
        {
            tail = tail[1..];
            if (tail.EndsWith(')')) tail = tail[..^1];
        }
        return tail;
    }

    private static bool LooksLikeBareCall(string line)
    {
        // Quote/paren-aware '=' check — a '=' inside a string literal
        // ("foo ""a=b""") used to veto legitimate bare calls.
        if (IndexOfTopLevel(line, '=') >= 0) return false;
        if (line.EndsWith("Then", StringComparison.OrdinalIgnoreCase)) return false;
        int space = IndexOfUnquotedWhitespace(line);
        if (space <= 0) return false;
        string first = line[..space];
        return Regex.IsMatch(first, "^[A-Za-z_$][\\w$]*$");
    }

    private static int IndexOfUnquotedWhitespace(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c == '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) return i;
        }
        return -1;
    }

    /// <summary>Case-insensitive literal search outside string literals.</summary>
    private static int IndexOfUnquoted(string text, string needle)
    {
        char quote = '\0';
        for (int i = 0; i + needle.Length <= text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c == '"') { quote = c; continue; }
            if (string.Compare(text, i, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return i;
        }
        return -1;
    }

    /// <summary>Keyword search outside string literals at word boundaries.</summary>
    private static int IndexOfUnquotedKeyword(string text, string keyword)
    {
        char quote = '\0';
        for (int i = 0; i + keyword.Length <= text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c == '"') { quote = c; continue; }
            if (string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            bool leftOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            int end = i + keyword.Length;
            bool rightOk = end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            if (leftOk && rightOk) return i;
        }
        return -1;
    }

    private static string NormalizeCall(string call)
    {
        if (call.Length == 0) return string.Empty;
        if (call.Contains('(')) return RewriteExpression(call, false);
        int space = IndexOfUnquotedWhitespace(call);
        if (space < 0) return RewriteExpression(call, false) + "()";
        return call[..space].Trim() + "(" + RewriteCommaArguments(call[(space + 1)..].Trim()) + ")";
    }

    private static string RewriteCommaArguments(string args)
    {
        var parts = SplitTopLevel(args, ',');
        return string.Join(", ", parts.ConvertAll(p => RewriteExpression(p, false)));
    }

    private static string NormalizeArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return string.Empty;
        var parts = SplitTopLevel(args, ',');
        var names = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            string p = part.Trim();
            // Loop the modifier strip: "Optional ByVal x" left "ByVal x".
            p = Regex.Replace(p, "^(?:(?:ByVal|ByRef|Optional|ParamArray)\\s+)+", "", RegexOptions.IgnoreCase);
            p = Regex.Replace(p, "\\s+As\\s+.+$", "", RegexOptions.IgnoreCase);
            p = p.Trim();
            if (Regex.IsMatch(p, "^[A-Za-z_$][\\w$]*$")) names.Add(p);
        }
        return string.Join(", ", names);
    }

    private static string NormalizeDimList(string text)
    {
        var parts = SplitTopLevel(text, ',');
        var result = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            string item = Regex.Replace(p.Trim(), "\\s+As\\s+.+$", "", RegexOptions.IgnoreCase);
            item = Regex.Replace(item, "\\(.*\\)$", "", RegexOptions.IgnoreCase);
            if (item.Length > 0) result.Add(item + " = undefined");
        }
        return string.Join(", ", result);
    }

    private static string NormalizeConstList(string text)
    {
        var parts = SplitTopLevel(text, ',');
        var result = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            int eq = IndexOfTopLevel(p, '=');
            if (eq < 0) continue;
            string lhs = p[..eq].Trim();
            string rhs = RewriteExpression(p[(eq + 1)..].Trim(), false);
            result.Add(lhs + " = " + rhs);
        }
        return string.Join(", ", result);
    }

    private static string RewriteExpression(string expression, bool condition)
    {
        string rewritten = TransformOutsideStrings(expression, s =>
        {
            s = Regex.Replace(s, @"\bAnd\b", "&&", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bOr\b", "||", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bXor\b", "^", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bNot\b", "!", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<>|!=", "!=", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bTrue\b", "true", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bFalse\b", "false", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bNothing\b", "null", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bEmpty\b", "undefined", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bMod\b", "%", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "(?<![<>!=])=(?!=)", condition ? "==" : "=");
            s = Regex.Replace(s, @"\bLen\s*\(", "__vbsLen(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bLCase\s*\(", "__vbsLCase(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bUCase\s*\(", "__vbsUCase(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bTrim\s*\(", "__vbsTrim(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bLeft\s*\(", "__vbsLeft(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bRight\s*\(", "__vbsRight(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bMid\s*\(", "__vbsMid(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bInStr\s*\(", "__vbsInStr(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bReplace\s*\(", "__vbsReplace(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bCStr\s*\(", "__vbsCStr(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bCInt\s*\(", "__vbsCInt(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bCLng\s*\(", "__vbsCLng(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bCDbl\s*\(", "__vbsCDbl(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bIsNumeric\s*\(", "__vbsIsNumeric(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bAbs\s*\(", "__vbsAbs(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bSgn\s*\(", "__vbsSgn(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bNow\b", "new Date()", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bInputBox\s*\(", "prompt(", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bMsgBox\s*\(", "alert(", RegexOptions.IgnoreCase);
            // & concatenation — any spacing, but NOT &H/&O literals and not
            // the && this rewrite itself may have produced from And.
            s = Regex.Replace(s, @"(?<!&)&(?![&HhOo])", "+");
            return s;
        });
        return rewritten.Trim();
    }

    private static string TransformOutsideStrings(string text, Func<string, string> transform)
    {
        var output = new StringBuilder(text.Length + 8);
        int start = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\0' && c == '"')
            {
                if (i > start) output.Append(transform(text[start..i]));
                quote = c;
                output.Append(c);
                start = i + 1;
                continue;
            }
            if (quote != '\0' && c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    // VB doubled quote ("") is an ESCAPED quote in the emitted
                    // JS — it used to be copied verbatim and produced
                    // adjacent string literals ("a""b") → syntax error.
                    output.Append(text[start..i]);
                    output.Append('\\').Append(quote);
                    i++;
                    start = i + 1;
                    continue;
                }
                output.Append(text[start..i]);
                output.Append(c);
                quote = '\0';
                start = i + 1;
            }
        }
        if (start < text.Length)
            output.Append(transform(text[start..]));
        return output.ToString();
    }

    private static string StripTrailingComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < line.Length && line[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c == '"') { quote = c; continue; }
            if (c == '\'') return line[..i].TrimEnd();
        }
        return line.TrimEnd();
    }

    private static int IndexOfTopLevel(string text, char needle)
    {
        int depth = 0; char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c == '"') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c == needle) return i;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, char delimiter)
    {
        var result = new List<string>();
        int start = 0, depth = 0; char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c == '"') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c == delimiter)
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }
        result.Add(text[start..]);
        return result;
    }
}