using System;
using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

/// <summary>
/// VBScript 1.0 recursive-descent parser.
/// Precedence (high→low): ^, unary -, * /, \\, Mod, + -, &amp;, comparisons
/// (+ Is), Not, And, Or, Xor, Eqv, Imp.
/// Handles single-line vs block If, Select Case (Is/To/comma lists), all four
/// Do/Loop forms, While/Wend, For/For Each, the `f x` / `f(x)` / `Call f(x)`
/// calling conventions, Set vs implicit Let, and Option Explicit placement.
/// </summary>
public sealed class VbsParser
{
    private readonly IReadOnlyList<VbsToken> _tokens;
    private int _pos;
    private bool _inProcedure;
    private int _doDepth, _forDepth;

    private VbsParser(IReadOnlyList<VbsToken> tokens) =>
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    public static VbsScript Parse(string source)
    {
        var lexer = new VbsLexer(source ?? throw new ArgumentNullException(nameof(source)));
        var parser = new VbsParser(lexer.Tokenize());
        return parser.ParseProgram();
    }

    // ── Token helpers ──────────────────────────────────────────────────────

    private VbsToken Peek(int n = 0) => _tokens[Math.Min(_pos + n, _tokens.Count - 1)];
    private VbsToken Advance()
    {
        var t = Peek();
        if (_pos < _tokens.Count - 1) _pos++;
        return t;
    }
    private bool IsAtEof() => Peek() is VbsEofToken;

    private bool CheckKw(string kw) => Peek() is VbsKeywordToken k && k.Keyword == kw;
    private bool CheckKwPair(string a, string b) =>
        CheckKw(a) && Peek(1) is VbsKeywordToken k && k.Keyword == b;
    private bool MatchKw(string kw) { if (CheckKw(kw)) { Advance(); return true; } return false; }
    private void ExpectKw(string kw, string? message = null)
    {
        if (!CheckKw(kw))
        {
            var t = Peek();
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError,
                message ?? $"Expected '{kw}'", t.Line, t.Column);
        }
        Advance();
    }

    private bool CheckPunct(string p) => Peek() is VbsPunctToken pt && pt.Text == p;
    private bool MatchPunct(string p) { if (CheckPunct(p)) { Advance(); return true; } return false; }
    private void ExpectPunct(string p)
    {
        if (!CheckPunct(p))
        {
            var t = Peek();
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError,
                $"Expected '{p}'", t.Line, t.Column);
        }
        Advance();
    }

    private string ExpectIdentifierName()
    {
        if (Peek() is VbsIdentifierToken id) { Advance(); return id.Name; }
        var t = Peek();
        throw new VbsSyntaxException(VbsErrorNumbers.ExpectedIdentifier,
            "Expected identifier", t.Line, t.Column);
    }

    /// <summary>After '.', keywords are legal member names (obj.Step, obj.Error …).</summary>
    private string ExpectMemberName()
    {
        if (Peek() is VbsIdentifierToken id) { Advance(); return id.Name; }
        if (Peek() is VbsKeywordToken kw) { Advance(); return kw.Keyword; }
        var t = Peek();
        throw new VbsSyntaxException(VbsErrorNumbers.ExpectedIdentifier,
            "Expected identifier", t.Line, t.Column);
    }

    private static VbsSyntaxException Syntax(int number, string message, int line, int column) =>
        new(number, message, line, column);

    // ── Program ────────────────────────────────────────────────────────────

    private VbsScript ParseProgram()
    {
        var body = new List<VbsStmt>();
        var procNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!IsAtEof())
        {
            if (CheckKw("OPTION"))
            {
                if (body.Count > 0)
                {
                    var t = Peek();
                    throw Syntax(VbsErrorNumbers.SyntaxError,
                        "Option Explicit must be the first statement", t.Line, t.Column);
                }
                // also reject a second Option Explicit
                var peekNext = Peek(1);
                if (peekNext is VbsKeywordToken pk && pk.Keyword == "EXPLICIT" && _sawOptionExplicit)
                {
                    throw Syntax(VbsErrorNumbers.SyntaxError,
                        "Option Explicit already specified", peekNext.Line, peekNext.Column);
                }
                _sawOptionExplicit = true;
            }
            var stmt = ParseStatementCore();
            body.Add(stmt);
            if (stmt is VbsSubStatement sub)
            {
                if (!procNames.Add(sub.Name))
                    throw Syntax(VbsErrorNumbers.NameRedefined,
                        $"Name redefined: '{sub.Name}'", sub.Line, 0);
            }
            ConsumeSeparators();
        }
        return new VbsScript(body);
    }
    private bool _sawOptionExplicit;

    // ── Statements ─────────────────────────────────────────────────────────

    /// <summary>Consumes statement separators: newlines and colons (any run).</summary>
    private void ConsumeSeparators()
    {
        while (Peek() is VbsEndOfLineToken || CheckPunct(":"))
            Advance();
    }

    private List<VbsStmt> ParseStatementList(Func<bool> terminated)
    {
        var list = new List<VbsStmt>();
        while (!IsAtEof() && !terminated())
        {
            list.Add(ParseStatementCore());
            ConsumeSeparators();
        }
        return list;
    }

    private VbsStmt ParseStatementCore()
    {
        int line = Peek().Line, col = Peek().Column;

        if (Peek() is VbsKeywordToken kw)
        {
            switch (kw.Keyword)
            {
                case "DIM": Advance(); return ParseDimDecls(line);
                case "PUBLIC":
                case "PRIVATE":
                    if (_inProcedure)
                        throw Syntax(VbsErrorNumbers.ExpectedStatement,
                            "'Public'/'Private' are not valid inside procedures", line, col);
                    Advance();
                    if (MatchKw("DIM")) return ParseDimDecls(line);
                    if (CheckKw("CONST")) return ParseConst(line);
                    return ParseDimDecls(line);
                case "CONST": return ParseConst(line);
                case "REDIM": return ParseReDim(line);
                case "ERASE": return ParseErase(line);
                case "IF": return ParseIf(line);
                case "SELECT": return ParseSelect(line);
                case "FOR": return ParseFor(line);
                case "DO": return ParseDo(line);
                case "WHILE": return ParseWhile(line);
                case "EXIT": return ParseExit(line, col);
                case "ON": return ParseOnError(line);
                case "OPTION": return ParseOption(line);
                case "SUB": return ParseProcedure(line, isFunction: false);
                case "FUNCTION": return ParseProcedure(line, isFunction: true);
                case "CALL": return ParseCall(line, col);
                case "SET": Advance(); return ParseCallOrAssignment(line, isSet: true);
                case "LET": Advance(); return ParseCallOrAssignment(line, isSet: false);
                case "STOP": Advance(); return new VbsNopStatement(line);
                default:
                    throw Syntax(VbsErrorNumbers.ExpectedStatement,
                        $"Expected statement, found '{kw.Keyword}'", line, col);
            }
        }

        if (Peek() is VbsIdentifierToken)
            return ParseCallOrAssignment(line, isSet: false);

        throw Syntax(VbsErrorNumbers.ExpectedStatement, "Expected statement", line, col);
    }

    private VbsStmt ParseDimDecls(int line)
    {
        var decls = new List<VbsDimDecl>();
        do
        {
            string name = ExpectIdentifierName();
            var bounds = new List<VbsExpr>();
            bool isArray = MatchPunct("(");
            if (isArray)
            {
                if (!CheckPunct(")"))
                    do { bounds.Add(ParseExpression()); } while (MatchPunct(","));
                ExpectPunct(")");
            }
            decls.Add(new VbsDimDecl(name, bounds, isArray));
        } while (MatchPunct(","));
        return new VbsDimStatement(line, decls);
    }

    private VbsStmt ParseConst(int line)
    {
        Advance();   // Const (or already past Public Const via caller? no — caller passes Const intact)
        var decls = new List<VbsConstDecl>();
        do
        {
            string name = ExpectIdentifierName();
            if (!CheckPunct("="))
            {
                var t = Peek();
                throw Syntax(VbsErrorNumbers.SyntaxError, "Expected '='", t.Line, t.Column);
            }
            Advance();
            decls.Add(new VbsConstDecl(name, ParseExpression()));
        } while (MatchPunct(","));
        return new VbsConstStatement(line, decls);
    }

    private VbsStmt ParseReDim(int line)
    {
        Advance();   // ReDim
        bool preserve = MatchKw("PRESERVE");
        var decls = new List<VbsReDimDecl>();
        do
        {
            string name = ExpectIdentifierName();
            ExpectPunct("(");
            var bounds = new List<VbsExpr>();
            if (!CheckPunct(")"))
                do { bounds.Add(ParseExpression()); } while (MatchPunct(","));
            ExpectPunct(")");
            decls.Add(new VbsReDimDecl(name, bounds));
        } while (MatchPunct(","));
        return new VbsReDimStatement(line, preserve, decls);
    }

    private VbsStmt ParseErase(int line)
    {
        Advance();   // Erase
        var names = new List<string>();
        do { names.Add(ExpectIdentifierName()); } while (MatchPunct(","));
        return new VbsEraseStatement(line, names);
    }

    private VbsStmt ParseOnError(int line)
    {
        Advance();   // On
        ExpectKw("ERROR");
        if (CheckKw("RESUME"))
        {
            Advance();
            ExpectKw("NEXT");
            return new VbsOnErrorStatement(line, ResumeNext: true);
        }
        ExpectKw("GOTO");
        if (Peek() is VbsLiteralToken { Value.Type: VbVarType.Integer } lit &&
            lit.Value.AsInt16() == 0)
        {
            Advance();
            return new VbsOnErrorStatement(line, ResumeNext: false);
        }
        var t = Peek();
        throw Syntax(VbsErrorNumbers.SyntaxError, "Expected 'Resume Next' or 'GoTo 0'",
            t.Line, t.Column);
    }

    private VbsStmt ParseOption(int line)
    {
        Advance();   // Option
        ExpectKw("EXPLICIT");
        return new VbsOptionExplicitStatement(line);
    }

    private VbsStmt ParseExit(int line, int col)
    {
        Advance();   // Exit
        string what = Peek() is VbsKeywordToken k ? k.Keyword : "";
        switch (what)
        {
            case "SUB":
                if (!_inProcedure)
                    throw Syntax(VbsErrorNumbers.ExpectedStatement,
                        "'Exit Sub' is only valid inside a procedure", line, col);
                Advance(); return new VbsExitStatement(line, VbsExitKind.Sub);
            case "FUNCTION":
                if (!_inProcedure)
                    throw Syntax(VbsErrorNumbers.ExpectedStatement,
                        "'Exit Function' is only valid inside a procedure", line, col);
                Advance(); return new VbsExitStatement(line, VbsExitKind.Function);
            case "DO":
                if (_doDepth == 0)
                    throw Syntax(VbsErrorNumbers.SyntaxError,
                        "'Exit Do' without a matching Do", line, col);
                Advance(); return new VbsExitStatement(line, VbsExitKind.Do);
            case "FOR":
                if (_forDepth == 0)
                    throw Syntax(VbsErrorNumbers.SyntaxError,
                        "'Exit For' without a matching For", line, col);
                Advance(); return new VbsExitStatement(line, VbsExitKind.For);
            default:
                throw Syntax(VbsErrorNumbers.SyntaxError,
                    "Expected 'Sub', 'Function', 'Do' or 'For' after 'Exit'", line, col);
        }
    }

    // ── Procedures ─────────────────────────────────────────────────────────

    private VbsStmt ParseProcedure(int line, bool isFunction)
    {
        Advance();   // Sub / Function
        string name = ExpectIdentifierName();

        var parameters = new List<VbsParam>();
        if (MatchPunct("("))
        {
            if (!CheckPunct(")"))
            {
                do
                {
                    bool byVal = MatchKw("BYVAL");
                    if (!byVal) MatchKw("BYREF");     // ByRef is the default
                    parameters.Add(new VbsParam(ExpectIdentifierName(), byVal));
                } while (MatchPunct(","));
            }
            ExpectPunct(")");
        }

        _inProcedure = true;
        int savedDo = _doDepth, savedFor = _forDepth;
        _doDepth = _forDepth = 0;

        ConsumeSeparators();
        string endKw = isFunction ? "FUNCTION" : "SUB";
        var body = ParseStatementList(() => CheckKwPair("END", endKw));
        ExpectKw("END");
        ExpectKw(endKw);

        _inProcedure = false;
        _doDepth = savedDo; _forDepth = savedFor;
        return new VbsSubStatement(line, name, parameters, body, isFunction);
    }

    // ── If ─────────────────────────────────────────────────────────────────

    private VbsStmt ParseIf(int line)
    {
        Advance();   // If
        var condition = ParseExpression();
        ExpectKw("THEN", "Expected 'Then'");

        // Block form: nothing else on the line.
        if (Peek() is VbsEndOfLineToken or VbsEofToken)
        {
            ConsumeSeparators();
            var branches = new List<VbsIfBranch>
            {
                new(condition, ParseStatementList(IsIfTerminator))
            };
            List<VbsStmt>? elseBody = null;
            while (true)
            {
                if (CheckKw("ELSEIF"))
                {
                    Advance();
                    var c = ParseExpression();
                    ExpectKw("THEN", "Expected 'Then'");
                    ConsumeSeparators();
                    branches.Add(new VbsIfBranch(c, ParseStatementList(IsIfTerminator)));
                    continue;
                }
                if (CheckKw("ELSE"))
                {
                    Advance();
                    ConsumeSeparators();
                    elseBody = ParseStatementList(() => CheckKwPair("END", "IF"));
                    continue;
                }
                ExpectKw("END");
                ExpectKw("IF", "Expected 'End If'");
                break;
            }
            return new VbsIfStatement(line, branches, elseBody);
        }

        // Single-line form: If cond Then stmt [: stmts] [Else stmt [: stmts]]
        var thenArm = ParseInlineList(stopAtElse: true);
        List<VbsStmt>? elseArm = null;
        if (CheckKw("ELSE"))
        {
            Advance();
            elseArm = ParseInlineList(stopAtElse: false);
        }
        return new VbsIfStatement(line,
            new List<VbsIfBranch> { new(condition, thenArm) }, elseArm);
    }

    private bool IsIfTerminator() =>
        CheckKw("ELSEIF") || CheckKw("ELSE") || CheckKwPair("END", "IF");

    private bool AtInlineEnd(bool stopAtElse) =>
        Peek() is VbsEofToken or VbsEndOfLineToken ||
        (stopAtElse && CheckKw("ELSE"));

    private List<VbsStmt> ParseInlineList(bool stopAtElse)
    {
        var list = new List<VbsStmt>();
        while (true)
        {
            if (AtInlineEnd(stopAtElse)) break;
            if (CheckPunct(":")) { Advance(); continue; }
            list.Add(ParseStatementCore());
            if (!CheckPunct(":")) break;
            Advance();
        }
        return list;
    }

    // ── Select Case ────────────────────────────────────────────────────────

    private VbsStmt ParseSelect(int line)
    {
        Advance();   // Select
        ExpectKw("CASE");
        var subject = ParseExpression();
        ConsumeSeparators();

        var cases = new List<VbsSelectCase>();
        List<VbsStmt>? elseBody = null;

        while (true)
        {
            if (CheckKwPair("END", "SELECT")) { Advance(); Advance(); break; }
            ExpectKw("CASE", "Expected 'Case'");

            if (CheckKw("ELSE"))
            {
                Advance();
                ConsumeSeparators();
                elseBody = ParseStatementList(IsSelectTerminator);
                continue;
            }

            var items = new List<VbsSelectCaseItem>();
            do
            {
                if (CheckKw("IS"))
                {
                    Advance();
                    string op = ExpectCompareOp();
                    items.Add(new VbsSelectCaseItem(ParseExpression(), op, null));
                }
                else
                {
                    var first = ParseExpression();
                    if (CheckKw("TO"))
                    {
                        Advance();
                        items.Add(new VbsSelectCaseItem(first, null, ParseExpression()));
                    }
                    else
                    {
                        items.Add(new VbsSelectCaseItem(first, null, null));
                    }
                }
            } while (MatchPunct(","));

            ConsumeSeparators();
            cases.Add(new VbsSelectCase(items, ParseStatementList(IsSelectTerminator)));
        }
        return new VbsSelectStatement(line, subject, cases, elseBody);
    }

    private bool IsSelectTerminator() =>
        CheckKw("CASE") || CheckKwPair("END", "SELECT");

    private string ExpectCompareOp()
    {
        if (Peek() is VbsPunctToken p &&
            p.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            Advance();
            return p.Text;
        }
        var t = Peek();
        throw Syntax(VbsErrorNumbers.SyntaxError, "Expected comparison operator",
            t.Line, t.Column);
    }

    // ── Loops ──────────────────────────────────────────────────────────────

    private VbsStmt ParseFor(int line)
    {
        Advance();   // For

        if (CheckKw("EACH"))
        {
            Advance();
            string varName = ExpectIdentifierName();
            ExpectKw("IN", "Expected 'In'");
            var collection = ParseExpression();
            ConsumeSeparators();
            _forDepth++;
            var body = ParseStatementList(() => CheckKw("NEXT"));
            _forDepth--;
            ExpectKw("NEXT", "Expected 'Next'");
            MaybeConsumeNextVar();
            return new VbsForEachStatement(line, varName, collection, body);
        }

        string variable = ExpectIdentifierName();
        if (!CheckPunct("="))
        {
            var t = Peek();
            throw Syntax(VbsErrorNumbers.SyntaxError, "Expected '='", t.Line, t.Column);
        }
        Advance();
        var from = ParseExpression();
        ExpectKw("TO", "Expected 'To'");
        var to = ParseExpression();
        VbsExpr? step = null;
        if (CheckKw("STEP"))
        {
            Advance();
            step = ParseExpression();
        }
        ConsumeSeparators();
        _forDepth++;
        var forBody = ParseStatementList(() => CheckKw("NEXT"));
        _forDepth--;
        ExpectKw("NEXT", "Expected 'Next'");
        MaybeConsumeNextVar();
        return new VbsForStatement(line, variable, from, to, step, forBody);
    }

    /// <summary>`Next i` — consume (and ignore) the optional loop variable.</summary>
    private void MaybeConsumeNextVar()
    {
        if (Peek() is VbsIdentifierToken && Peek(1) is VbsEndOfLineToken or VbsEofToken ||
            Peek() is VbsIdentifierToken && Peek(1) is VbsPunctToken { Text: ":" })
            Advance();
    }

    private VbsStmt ParseDo(int line)
    {
        Advance();   // Do
        VbsLoopTest? top = null;
        if (CheckKw("WHILE")) { Advance(); top = new VbsLoopTest(ParseExpression(), Until: false); }
        else if (CheckKw("UNTIL")) { Advance(); top = new VbsLoopTest(ParseExpression(), Until: true); }
        ConsumeSeparators();

        _doDepth++;
        var body = ParseStatementList(() => CheckKw("LOOP"));
        _doDepth--;
        ExpectKw("LOOP", "Expected 'Loop'");

        VbsLoopTest? bottom = null;
        if (CheckKw("WHILE")) { Advance(); bottom = new VbsLoopTest(ParseExpression(), Until: false); }
        else if (CheckKw("UNTIL")) { Advance(); bottom = new VbsLoopTest(ParseExpression(), Until: true); }
        return new VbsDoLoopStatement(line, top, bottom, body);
    }

    private VbsStmt ParseWhile(int line)
    {
        Advance();   // While
        var condition = ParseExpression();
        ConsumeSeparators();
        var body = ParseStatementList(() => CheckKw("WEND"));
        ExpectKw("WEND", "Expected 'Wend'");
        return new VbsWhileStatement(line, condition, body);
    }

    // ── Call / assignment statements ───────────────────────────────────────

    private VbsStmt ParseCall(int line, int col)
    {
        Advance();   // Call
        var expr = ParsePostfix();
        if (expr is not VbsInvokeExpr)
            throw Syntax(VbsErrorNumbers.SyntaxError,
                "'Call' requires a procedure invocation with parentheses", line, col);
        return new VbsCallStatement(line, expr, ForceByVal: false);
    }

    /// <summary>
    /// Parses `name[.member]*` then disambiguates:
    ///   name = expr                → Let assignment
    ///   name(i, …) = expr          → array-element assignment
    ///   Set name = expr            → object assignment
    ///   name arg1, arg2            → Sub call without parentheses (ByRef works)
    ///   Call name(args)            → Sub call (ByRef works)
    ///   name(arg)                  → Sub call — ONE parenthesized argument is
    ///                                 forced ByVal (the classic quirk)
    ///   name(arg, arg2)            → syntax error 1041
    ///   name (arg), arg2           → bare call whose first arg is a paren group
    /// </summary>
    private VbsStmt ParseCallOrAssignment(int line, bool isSet)
    {
        int col = Peek().Column;
        VbsExpr callee = new VbsNameExpr(line, ExpectIdentifierName());
        while (CheckPunct("."))
        {
            Advance();
            callee = new VbsMemberExpr(line, callee, ExpectMemberName());
        }

        if (CheckPunct("("))
        {
            var args = ParseParenArgs();

            if (CheckPunct("="))
            {
                Advance();
                return new VbsAssignmentStatement(line, isSet,
                    new VbsInvokeExpr(line, callee, args), ParseExpression());
            }

            if (CheckPunct(","))
            {
                // `f (x), y` — the paren group is just the first bare argument.
                if (args.Count != 1)
                {
                    var t = Peek();
                    throw Syntax(VbsErrorNumbers.ExpectedEndOfStatement,
                        "Expected end of statement", t.Line, t.Column);
                }
                var all = new List<VbsExpr> { new VbsParenExpr(line, args[0]) };
                while (MatchPunct(",")) all.Add(ParseExpression());
                return new VbsCallStatement(line,
                    new VbsInvokeExpr(line, callee, all), ForceByVal: false);
            }

            if (args.Count > 1)
                throw Syntax(VbsErrorNumbers.CannotUseParens,
                    "Cannot use parentheses when calling a Sub", line, col);

            return new VbsCallStatement(line,
                new VbsInvokeExpr(line, callee, args),
                ForceByVal: args.Count == 1 && !isSet);
        }

        if (CheckPunct("="))
        {
            Advance();
            if (isSet && callee is not (VbsNameExpr or VbsMemberExpr)) { /* unreachable */ }
            return new VbsAssignmentStatement(line, isSet, callee, ParseExpression());
        }

        if (isSet)
        {
            var t = Peek();
            throw Syntax(VbsErrorNumbers.ExpectedEndOfStatement,
                "Expected '=' after 'Set'", t.Line, t.Column);
        }

        // Bare call: `f a, b` (or a plain zero-argument call `f`).
        var bareArgs = new List<VbsExpr>();
        if (!AtStatementBoundary())
        {
            bareArgs.Add(ParseExpression());
            while (MatchPunct(",")) bareArgs.Add(ParseExpression());
        }
        return new VbsCallStatement(line, new VbsInvokeExpr(line, callee, bareArgs),
            ForceByVal: false);
    }

    private bool AtStatementBoundary() =>
        Peek() is VbsEofToken or VbsEndOfLineToken ||
        CheckPunct(":") || CheckKw("ELSE");

    /// <summary>Parenthesized argument list. Leading commas produce an Empty
    /// literal so `GetObject(, "ProgID")` (omitted first argument) parses.</summary>
    private List<VbsExpr> ParseParenArgs()
    {
        int line = Peek().Line;
        ExpectPunct("(");
        var args = new List<VbsExpr>();
        if (!CheckPunct(")"))
        {
            while (true)
            {
                if (CheckPunct(",") || CheckPunct(")"))
                    args.Add(new VbsLiteralExpr(line, VbsVariant.Empty));
                else
                    args.Add(ParseExpression());
                if (!MatchPunct(",")) break;
            }
        }
        ExpectPunct(")");
        return args;
    }

    // ── Expressions (precedence climbing, high → low) ──────────────────────

    private VbsExpr ParseExpression() => ParseImp();

    private VbsExpr ParseImp()
    {
        var l = ParseEqv();
        while (CheckKw("IMP"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "Imp", l, ParseEqv());
        }
        return l;
    }

    private VbsExpr ParseEqv()
    {
        var l = ParseXor();
        while (CheckKw("EQV"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "Eqv", l, ParseXor());
        }
        return l;
    }

    private VbsExpr ParseXor()
    {
        var l = ParseOr();
        while (CheckKw("XOR"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "Xor", l, ParseOr());
        }
        return l;
    }

    private VbsExpr ParseOr()
    {
        var l = ParseAnd();
        while (CheckKw("OR"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "Or", l, ParseAnd());
        }
        return l;
    }

    private VbsExpr ParseAnd()
    {
        var l = ParseNot();
        while (CheckKw("AND"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "And", l, ParseNot());
        }
        return l;
    }

    private VbsExpr ParseNot()
    {
        if (CheckKw("NOT"))
        {
            int line = Peek().Line;
            Advance();
            return new VbsUnaryExpr(line, "Not", ParseNot());
        }
        return ParseComparison();
    }

    private VbsExpr ParseComparison()
    {
        var l = ParseConcat();
        while (true)
        {
            if (Peek() is VbsPunctToken p &&
                p.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                Advance();
                l = new VbsBinaryExpr(l.Line, p.Text, l, ParseConcat());
                continue;
            }
            if (CheckKw("IS"))
            {
                Advance();
                l = new VbsBinaryExpr(l.Line, "Is", l, ParseConcat());
                continue;
            }
            break;
        }
        return l;
    }

    private VbsExpr ParseConcat()
    {
        var l = ParseAddSub();
        while (CheckPunct("&"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "&", l, ParseAddSub());
        }
        return l;
    }

    private VbsExpr ParseAddSub()
    {
        var l = ParseMod();
        while (Peek() is VbsPunctToken p && p.Text is "+" or "-")
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, p.Text, l, ParseMod());
        }
        return l;
    }

    private VbsExpr ParseMod()
    {
        var l = ParseIntDiv();
        while (CheckKw("MOD"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "Mod", l, ParseIntDiv());
        }
        return l;
    }

    private VbsExpr ParseIntDiv()
    {
        var l = ParseMulDiv();
        while (CheckPunct("\\"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "\\", l, ParseMulDiv());
        }
        return l;
    }

    private VbsExpr ParseMulDiv()
    {
        var l = ParsePower();
        while (Peek() is VbsPunctToken p && p.Text is "*" or "/")
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, p.Text, l, ParsePower());
        }
        return l;
    }

    private VbsExpr ParsePower()
    {
        var l = ParseUnary();
        while (CheckPunct("^"))
        {
            Advance();
            l = new VbsBinaryExpr(l.Line, "^", l, ParseUnary());
        }
        return l;
    }

    private VbsExpr ParseUnary()
    {
        if (CheckPunct("-"))
        {
            int line = Peek().Line;
            Advance();
            return new VbsUnaryExpr(line, "-", ParseUnary());
        }
        return ParsePostfix();
    }

    private VbsExpr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            if (CheckPunct("."))
            {
                Advance();
                e = new VbsMemberExpr(e.Line, e, ExpectMemberName());
            }
            else if (CheckPunct("("))
            {
                e = new VbsInvokeExpr(e.Line, e, ParseParenArgs());
            }
            else break;
        }
        return e;
    }

    private VbsExpr ParsePrimary()
    {
        var t = Peek();
        switch (t)
        {
            case VbsLiteralToken lit:
                Advance();
                return new VbsLiteralExpr(t.Line, lit.Value);

            case VbsIdentifierToken id:
                Advance();
                return new VbsNameExpr(t.Line, id.Name);

            case VbsKeywordToken kw:
                switch (kw.Keyword)
                {
                    case "TRUE": Advance(); return new VbsLiteralExpr(t.Line, VbsVariant.Of(true));
                    case "FALSE": Advance(); return new VbsLiteralExpr(t.Line, VbsVariant.Of(false));
                    case "NULL": Advance(); return new VbsLiteralExpr(t.Line, VbsVariant.Null);
                    case "EMPTY": Advance(); return new VbsLiteralExpr(t.Line, VbsVariant.Empty);
                    case "NOTHING": Advance(); return new VbsLiteralExpr(t.Line, VbsVariant.Nothing);
                }
                break;

            case VbsPunctToken { Text: "(" }:
                Advance();
                var inner = ParseExpression();
                ExpectPunct(")");
                return new VbsParenExpr(t.Line, inner);
        }

        throw Syntax(VbsErrorNumbers.SyntaxError, "Expected expression", t.Line, t.Column);
    }
}