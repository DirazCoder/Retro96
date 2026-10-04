using System;
using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

/// <summary>
/// VBScript 5.0 recursive-descent parser.
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
    private int _doDepth, _forDepth, _withDepth;
    private bool _inClass;
    private int _propertyDepth;

    private VbsParser(IReadOnlyList<VbsToken> tokens) =>
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    public static VbsScript Parse(string source)
    {
        var lexer = new VbsLexer(source ?? throw new ArgumentNullException(nameof(source)));
        var parser = new VbsParser(lexer.Tokenize());
        return parser.ParseProgram();
    }

    /// <summary>Parses a single expression — Eval's argument. `=` inside
    /// an expression is comparison-only, which is exactly Eval's documented
    /// semantics (assignments cannot happen through Eval).</summary>
    public static VbsExpr ParseExpressionText(string source)
    {
        var lexer = new VbsLexer(source ?? throw new ArgumentNullException(nameof(source)));
        var parser = new VbsParser(lexer.Tokenize());
        var expr = parser.ParseExpression();
        parser.ConsumeSeparators();
        if (!parser.IsAtEof())
        {
            var t = parser.Peek();
            throw Syntax(VbsErrorNumbers.ExpectedEndOfStatement,
                "Expected end of statement", t.Line, t.Column);
        }
        return expr;
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

        // `.Member` shorthand inside a With block (statement position).
        if (CheckPunct(".") && _withDepth > 0)
            return ParseWithQualifiedStatement(line, isSet: false);

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
                    // VBScript 5.0: Public/Private on script-level procedures
                    // (visibility is meaningless without modules — accepted,
                    // not enforced). Properties are class-only.
                    if (CheckKw("SUB")) return ParseProcedure(Peek().Line, isFunction: false);
                    if (CheckKw("FUNCTION")) return ParseProcedure(Peek().Line, isFunction: true);
                    if (CheckKw("CLASS")) return ParseClass(line);
                    if (CheckKw("PROPERTY"))
                        throw Syntax(VbsErrorNumbers.ExpectedStatement,
                            "'Property' is only valid inside a Class block", line, col);
                    return ParseDimDecls(line);
                case "CONST": return ParseConst(line);
                case "REDIM": return ParseReDim(line);
                case "ERASE": return ParseErase(line);
                case "IF": return ParseIf(line);
                case "SELECT": return ParseSelect(line);
                case "FOR": return ParseFor(line);
                case "DO": return ParseDo(line);
                case "WHILE": return ParseWhile(line);
                case "WITH": return ParseWith(line);
                case "CLASS": return ParseClass(line);
                case "EXIT": return ParseExit(line, col);
                case "ON": return ParseOnError(line);
                case "OPTION": return ParseOption(line);
                case "SUB": return ParseProcedure(line, isFunction: false);
                case "FUNCTION": return ParseProcedure(line, isFunction: true);
                case "CALL": return ParseCall(line, col);
                case "SET":
                    Advance();
                    if (CheckPunct(".") && _withDepth > 0)
                        return ParseWithQualifiedStatement(line, isSet: true);
                    return ParseCallOrAssignment(line, isSet: true);
                case "LET": Advance(); return ParseCallOrAssignment(line, isSet: false);
                case "STOP": Advance(); return new VbsNopStatement(line);
                default:
                    throw Syntax(VbsErrorNumbers.ExpectedStatement,
                        $"Expected statement, found '{kw.Keyword}'", line, col);
            }
        }

        // Directly inside a class body (not inside a method) members only.
        if (_inClass && !_inProcedure)
            throw Syntax(VbsErrorNumbers.ExpectedStatement,
                "Class members must be Public or Private variables, Sub/Function " +
                "or Property procedures", line, col);

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
            case "PROPERTY":
                if (_propertyDepth == 0)
                    throw Syntax(VbsErrorNumbers.ExpectedStatement,
                        "'Exit Property' is only valid inside a Property procedure", line, col);
                Advance(); return new VbsExitStatement(line, VbsExitKind.Property);
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
                    "Expected 'Sub', 'Function', 'Property', 'Do' or 'For' after 'Exit'", line, col);
        }
    }

    // ── Procedures ─────────────────────────────────────────────────────────

    private VbsStmt ParseProcedure(int line, bool isFunction)
    {
        Advance();   // Sub / Function
        string name = ExpectIdentifierName();
        var parameters = ParseParameters();

        _inProcedure = true;
        int savedDo = _doDepth, savedFor = _forDepth, savedWith = _withDepth;
        _doDepth = _forDepth = _withDepth = 0;

        ConsumeSeparators();
        string endKw = isFunction ? "FUNCTION" : "SUB";
        var body = ParseStatementList(() => CheckKwPair("END", endKw));
        ExpectKw("END");
        ExpectKw(endKw);

        _inProcedure = false;
        _doDepth = savedDo; _forDepth = savedFor; _withDepth = savedWith;
        return new VbsSubStatement(line, name, parameters, body, isFunction);
    }

    /// <summary>`( [ByVal|ByRef] name [, …] )` — shared by Sub/Function and
    /// Property Get/Let/Set declarations.</summary>
    private List<VbsParam> ParseParameters()
    {
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
        return parameters;
    }

    // ── With ─────────────────────────────────────────────────────────────────

    private VbsStmt ParseWith(int line)
    {
        int col = Peek().Column;
        Advance();   // With
        var obj = ParseExpression();
        ConsumeSeparators();

        int savedDo = _doDepth, savedFor = _forDepth;
        _doDepth = _forDepth = 0;
        _withDepth++;
        var body = ParseStatementList(() => CheckKwPair("END", "WITH"));
        _withDepth--;
        _doDepth = savedDo; _forDepth = savedFor;

        ExpectKw("END");
        ExpectKw("WITH", "Expected 'End With'");
        if (_withDepth < 0)
            throw Syntax(VbsErrorNumbers.SyntaxError,
                "'End With' without a matching 'With'", line, col);
        return new VbsWithStatement(line, obj, body);
    }

    /// <summary>`.Member…` at statement position inside With — call or
    /// assignment against the With object.</summary>
    private VbsStmt ParseWithQualifiedStatement(int line, bool isSet)
    {
        int col = Peek().Column;
        Advance();   // leading '.'
        VbsExpr callee = new VbsWithMemberExpr(line, ExpectMemberName());
        while (CheckPunct("."))
        {
            Advance();
            callee = new VbsMemberExpr(line, callee, ExpectMemberName());
        }
        return ParseCalleeTail(line, col, callee, isSet);
    }

    // ── Class ───────────────────────────────────────────────────────────────

    private VbsStmt ParseClass(int line)
    {
        int col = Peek().Column;
        if (_inClass)
            throw Syntax(VbsErrorNumbers.SyntaxError,
                "Class definitions cannot be nested", line, col);
        if (_inProcedure)
            throw Syntax(VbsErrorNumbers.ExpectedStatement,
                "Class definitions are not valid inside procedures", line, col);
        Advance();   // Class
        string name = ExpectIdentifierName();
        ConsumeSeparators();

        var members = new List<VbsClassMemberDecl>();
        var memberNames = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);

        bool wasInClass = _inClass;
        _inClass = true;
        int savedWith = _withDepth;
        _withDepth = 0;

        while (true)
        {
            if (IsAtEof())
                throw Syntax(VbsErrorNumbers.SyntaxError, "Expected 'End Class'",
                    Peek().Line, Peek().Column);
            if (CheckKwPair("END", "CLASS")) { Advance(); Advance(); break; }

            members.Add(ParseClassMember(Peek().Line, Peek().Column));
            RegisterMemberNames(memberNames, members[^1]);
            ConsumeSeparators();
        }

        _inClass = wasInClass;
        _withDepth = savedWith;
        return new VbsClassStatement(line, name, members);
    }

    /// <summary>One class member: visibility + field list / method /
    /// property. Returns the AST node; duplicate-name detection is done by
    /// the caller via RegisterMemberNames (compile error 1041).</summary>
    private VbsClassMemberDecl ParseClassMember(int line, int col)
    {
        bool sawVisibility = false, isPublic = true;
        if (CheckKw("PUBLIC") || CheckKw("PRIVATE"))
        {
            isPublic = CheckKw("PUBLIC");
            sawVisibility = true;
            Advance();
        }

        if (CheckKw("SUB") || CheckKw("FUNCTION"))
        {
            bool isFunction = CheckKw("FUNCTION");
            // Methods/properties default to Public when the keyword is omitted
            // (fields default to Private — see below).
            var sub = (VbsSubStatement)ParseProcedure(Peek().Line, isFunction);
            return new VbsClassMethodDecl(sub.Line, sub.Name, sub.Params, sub.Body,
                sub.IsFunction, sawVisibility ? isPublic : true);
        }

        if (CheckKw("PROPERTY"))
            return ParsePropertyDecl(line, sawVisibility ? isPublic : true);

        // Field declarations: `Public X, Y` / `Private A()` / `Dim A`.
        // A field without a visibility keyword is Private (VB6 module-level Dim).
        if (sawVisibility || CheckKw("DIM"))
        {
            MatchKw("DIM");   // tolerated after Public/Private
            var dim = (VbsDimStatement)ParseDimDecls(line);
            return new VbsClassFieldDecl(line, dim.Decls, sawVisibility ? isPublic : false);
        }

        throw Syntax(VbsErrorNumbers.ExpectedStatement,
            "Class members must be Public or Private variables, Sub/Function " +
            "or Property procedures", line, col);
    }

    private VbsClassPropertyDecl ParsePropertyDecl(int line, bool isPublic)
    {
        Advance();   // Property

        VbsPropertyKind kind;
        if (Peek() is VbsIdentifierToken id &&
            id.Name.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            kind = VbsPropertyKind.Get;
            Advance();
        }
        else if (MatchKw("LET")) kind = VbsPropertyKind.Let;
        else if (MatchKw("SET")) kind = VbsPropertyKind.Set;
        else
            throw Syntax(VbsErrorNumbers.SyntaxError,
                "Expected 'Get', 'Let' or 'Set' after 'Property'", Peek().Line, Peek().Column);

        string name = ExpectIdentifierName();
        var parameters = ParseParameters();

        bool wasInProcedure = _inProcedure;
        int savedDo = _doDepth, savedFor = _forDepth, savedWith = _withDepth;
        _inProcedure = true;   // a property IS a procedure (bare statements OK)
        _doDepth = _forDepth = _withDepth = 0;

        _propertyDepth++;
        ConsumeSeparators();
        var body = ParseStatementList(() => CheckKwPair("END", "PROPERTY"));
        ExpectKw("END");
        ExpectKw("PROPERTY", "Expected 'End Property'");
        _propertyDepth--;

        _inProcedure = wasInProcedure;
        _doDepth = savedDo; _forDepth = savedFor; _withDepth = savedWith;

        // Property Get is function-like: the property name doubles as its
        // implicit return variable. Let/Set take the value as last parameter.
        return new VbsClassPropertyDecl(line, name, kind, parameters, body, isPublic);
    }

    /// <summary>Duplicate member detection (compile error 1041). Kind chars:
    /// 'F' field, 'M' method, 'P' property accessor — a property may repeat
    /// its name across Get/Let/Set, nothing else may repeat.</summary>
    private static void RegisterMemberNames(Dictionary<string, char> names, VbsClassMemberDecl member)
    {
        void Add(string name, char kind)
        {
            if (names.TryGetValue(name, out char existing))
            {
                bool ok = existing == 'P' && kind == 'P';
                if (!ok)
                    throw Syntax(VbsErrorNumbers.NameRedefined,
                        $"Name redefined: '{name}'", member.Line, 0);
            }
            else
                names[name] = kind;
        }

        switch (member)
        {
            case VbsClassFieldDecl f:
                foreach (var d in f.Fields) Add(d.Name, 'F');
                break;
            case VbsClassMethodDecl m:
                Add(m.Name, 'M');
                break;
            case VbsClassPropertyDecl p:
                Add(p.Name, 'P');
                break;
        }
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

    private VbsStmt ParseCallOrAssignment(int line, bool isSet)
    {
        int col = Peek().Column;
        VbsExpr callee = new VbsNameExpr(line, ExpectIdentifierName());
        while (CheckPunct("."))
        {
            Advance();
            callee = new VbsMemberExpr(line, callee, ExpectMemberName());
        }
        return ParseCalleeTail(line, col, callee, isSet);
    }

    /// <summary>
    /// The shared tail of statement parsing after a callee expression
    /// (`name[.member]*` or a With-qualified `.member` chain):
    ///   callee = expr                → Let assignment
    ///   callee(i, …) = expr          → array-element assignment
    ///   Set callee = expr            → object assignment
    ///   callee arg1, arg2            → Sub call without parentheses (ByRef works)
    ///   Call callee(args)            → Sub call (ByRef works)
    ///   callee(arg)                  → Sub call — ONE parenthesized argument is
    ///                                 forced ByVal (the classic quirk)
    ///   callee(arg, arg2)            → syntax error 1041
    ///   callee (arg), arg2           → bare call whose first arg is a paren group
    /// </summary>
    private VbsStmt ParseCalleeTail(int line, int col, VbsExpr callee, bool isSet)
    {
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

            // `.Member` in expression position inside a With block.
            case VbsPunctToken { Text: "." } when _withDepth > 0:
                Advance();
                return new VbsWithMemberExpr(t.Line, ExpectMemberName());

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
                    case "ME":
                        if (!_inClass)
                            throw Syntax(VbsErrorNumbers.SyntaxError,
                                "'Me' is only valid inside a class", t.Line, t.Column);
                        Advance();
                        return new VbsMeExpr(t.Line);
                    case "NEW":
                        Advance();
                        return new VbsNewExpr(t.Line, ExpectIdentifierName());
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