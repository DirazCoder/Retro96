using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Retro96.Engine.Js;

/// <summary>
/// Small, deterministic VBScript-to-JavaScript 1.2 compatibility bridge for
/// the browser's 1996-era scripting surface.  It intentionally implements the
/// syntax used by legacy web pages (Dim/Const, Sub/Function, If/Else,
/// For/While, Call, Set, common string/conversion functions, MsgBox/InputBox,
/// and VB boolean/string operators) and emits only syntax understood by the
/// existing in-process JS interpreter.
/// </summary>
public static class VbScriptTranslator
{
    private static readonly Regex ReComment = new("^\\s*(?:Rem(?:\\s|$)|')", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDim = new("^\\s*(?:Dim|Private\\s+Dim|Public\\s+Dim)\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReConst = new("^\\s*(?:Const|Private\\s+Const|Public\\s+Const)\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReSub = new("^\\s*(?:Public\\s+|Private\\s+|Friend\\s+)?Sub\\s+([A-Za-z_$][\\w$]*)\\s*(?:\\(([^)]*)\\))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReFunction = new("^\\s*(?:Public\\s+|Private\\s+|Friend\\s+)?Function\\s+([A-Za-z_$][\\w$]*)\\s*(?:\\(([^)]*)\\))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReIfBlock = new("^\\s*If\\s+(.+?)\\s+Then\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReElseIf = new("^\\s*ElseIf\\s+(.+?)\\s+Then\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReFor = new("^\\s*For\\s+([A-Za-z_$][\\w$]*)\\s*=\\s*(.+?)\\s+To\\s+(.+?)(?:\\s+Step\\s+(.+))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReForEach = new("^\\s*For\\s+Each\\s+([A-Za-z_$][\\w$]*)\\s+In\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDoWhile = new("^\\s*Do\\s+While\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDoUntil = new("^\\s*Do\\s+Until\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReLoopUntil = new("^\\s*Loop\\s+Until\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReAssignment = new("^\\s*(?:Set\\s+)?([A-Za-z_$][\\w$]*(?:\\.[A-Za-z_$][\\w$]*|\\([^)]*\\))*)\\s*=\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        var logicalLines = JoinLineContinuations(source);

        foreach (string raw in logicalLines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || ReComment.IsMatch(line) ||
                line.Equals("Option Explicit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("On Error Resume Next", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("On Error GoTo 0", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("'", StringComparison.Ordinal)) continue;
            if (line.StartsWith("Rem ", StringComparison.OrdinalIgnoreCase)) continue;

            var mSub = ReSub.Match(line);
            if (mSub.Success)
            {
                string name = mSub.Groups[1].Value;
                string args = NormalizeArgs(mSub.Groups[2].Value);
                output.Append("function ").Append(name).Append('(').Append(args).Append(") {\n");
                scopes.Push(new FunctionScope(name, false));
                continue;
            }

            var mFn = ReFunction.Match(line);
            if (mFn.Success)
            {
                string name = mFn.Groups[1].Value;
                string args = NormalizeArgs(mFn.Groups[2].Value);
                output.Append("function ").Append(name).Append('(').Append(args).Append(") {\n");
                scopes.Push(new FunctionScope(name, true));
                continue;
            }

            if (line.Equals("End Sub", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("End Function", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("}\n");
                if (scopes.Count > 0) scopes.Pop();
                continue;
            }

            if (line.Equals("Exit Sub", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("Exit Function", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("return;\n");
                continue;
            }

            if (line.Equals("End If", StringComparison.OrdinalIgnoreCase)) { output.Append("}\n"); continue; }
            if (line.Equals("Wend", StringComparison.OrdinalIgnoreCase) || line.Equals("Loop", StringComparison.OrdinalIgnoreCase)) { output.Append("}\n"); continue; }
            if (line.Equals("Next", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Next ", StringComparison.OrdinalIgnoreCase)) { output.Append("}\n"); continue; }

            var mElseIf = ReElseIf.Match(line);
            if (mElseIf.Success)
            {
                output.Append("} else if (").Append(RewriteExpression(mElseIf.Groups[1].Value, condition: true)).Append(") {\n");
                continue;
            }
            if (line.Equals("Else", StringComparison.OrdinalIgnoreCase)) { output.Append("} else {\n"); continue; }

            var mIf = ReIfBlock.Match(line);
            if (mIf.Success)
            {
                output.Append("if (").Append(RewriteExpression(mIf.Groups[1].Value, condition: true)).Append(") {\n");
                continue;
            }

            if (line.StartsWith("If ", StringComparison.OrdinalIgnoreCase) && line.IndexOf(" Then ", StringComparison.OrdinalIgnoreCase) > 0)
            {
                int thenAt = line.IndexOf(" Then ", StringComparison.OrdinalIgnoreCase);
                string condition = line[3..thenAt];
                string statement = line[(thenAt + 6)..].Trim();
                output.Append("if (").Append(RewriteExpression(condition, true)).Append(") { ");
                output.Append(TranslateStatement(statement, scopes.Count > 0 ? scopes.Peek() : null));
                output.Append(" }\n");
                continue;
            }

            var mForEach = ReForEach.Match(line);
            if (mForEach.Success)
            {
                string variable = mForEach.Groups[1].Value;
                string collection = RewriteExpression(mForEach.Groups[2].Value, false);
                output.Append("for (var __vbs_i = 0; __vbs_i < (" ).Append(collection)
                    .Append(").length; __vbs_i++) { var ").Append(variable)
                    .Append(" = (").Append(collection).Append(")[__vbs_i];\n");
                continue;
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
                continue;
            }

            var mDoWhile = ReDoWhile.Match(line);
            if (mDoWhile.Success)
            {
                output.Append("while (").Append(RewriteExpression(mDoWhile.Groups[1].Value, true)).Append(") {\n");
                continue;
            }
            var mDoUntil = ReDoUntil.Match(line);
            if (mDoUntil.Success)
            {
                output.Append("while (!(").Append(RewriteExpression(mDoUntil.Groups[1].Value, true)).Append(")) {\n");
                continue;
            }
            var mLoopUntil = ReLoopUntil.Match(line);
            if (mLoopUntil.Success)
            {
                // JavaScript has no do/loop counterpart in this bridge; keep
                // the loop body valid and apply the terminating condition.
                output.Append("if (").Append(RewriteExpression(mLoopUntil.Groups[1].Value, true)).Append(") break; }\n");
                continue;
            }

            var mDim = ReDim.Match(line);
            if (mDim.Success)
            {
                output.Append("var ").Append(NormalizeDimList(mDim.Groups[1].Value)).Append(";\n");
                continue;
            }

            var mConst = ReConst.Match(line);
            if (mConst.Success)
            {
                output.Append("var ").Append(NormalizeConstList(mConst.Groups[1].Value)).Append(";\n");
                continue;
            }

            if (line.StartsWith("Class ", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("End Class", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Property ", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("On Error ", StringComparison.OrdinalIgnoreCase)) continue;

            string translated = TranslateStatement(line, scopes.Count > 0 ? scopes.Peek() : null);
            if (translated.Length > 0)
                output.Append(translated).Append('\n');
        }

        while (scopes.Count > 0) { output.Append("}\n"); scopes.Pop(); }
        return output.ToString();
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

        if (line.StartsWith("MsgBox ", StringComparison.OrdinalIgnoreCase))
        {
            return "alert(" + RewriteExpression(line[7..].Trim(), false) + ");";
        }

        if (line.StartsWith("InputBox ", StringComparison.OrdinalIgnoreCase))
        {
            return "prompt(" + RewriteExpression(line[9..].Trim(), false) + ");";
        }

        if (line.StartsWith("document.write ", StringComparison.OrdinalIgnoreCase))
            return "document.write(" + RewriteExpression(line[15..].Trim(), false) + ");";
        if (line.StartsWith("document.writeln ", StringComparison.OrdinalIgnoreCase))
            return "document.write(" + RewriteExpression(line[17..].Trim(), false) + "+'\\n');";

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
            string args = line[(space + 1)..].Trim();
            return name + "(" + RewriteCommaArguments(args) + ");";
        }

        return RewriteExpression(line, false) + (line.EndsWith(";", StringComparison.Ordinal) ? "" : ";");
    }

    private static bool LooksLikeBareCall(string line)
    {
        if (line.Contains('=') || line.EndsWith("Then", StringComparison.OrdinalIgnoreCase)) return false;
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
            p = Regex.Replace(p, "^(?:ByVal|ByRef|Optional|ParamArray)\\s+", "", RegexOptions.IgnoreCase);
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
            s = s.Replace(" & ", " + ");
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
                    output.Append(text[start..(i + 1)]).Append(quote);
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
