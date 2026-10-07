using System;
using System.Collections.Generic;

namespace Retro96.Engine.Js;

public class JsParserException : Exception
{
    public int Line { get; }
    public int Column { get; }
    /// <summary>The bare message without the "Line N, Column M" prefix —
    /// this is what JS-visible SyntaxErrors carry (era message fidelity).</summary>
    public string RawMessage { get; }

    public JsParserException(string message, int line, int column)
        : base($"Line {line}, Column {column}: {message}")
    {
        Line = line;
        Column = column;
        RawMessage = message;
    }
}

/// <summary>
/// JavaScript 1999 recursive-descent parser.
///
/// Semicolon handling follows the real automatic-semicolon-insertion
/// rules: a missing ';' is legal before a token that follows a line
/// terminator or at end of input, and the restricted productions
/// (return / break / continue / throw / postfix ++ --) terminate at a
/// newline — the era's exact semantics.
///
/// `new` expressions, for-in (including the "in must not bind inside
/// for-init expressions" rule), labels, and object/array literals with
/// trailing commas are all supported.
/// </summary>
public class JsParser
{
    private readonly IReadOnlyList<JsToken> _tokens;
    private int _position;

    /// <summary>Expression-nesting guard: pathological inputs like 10,000
    /// nested parentheses used to overflow the real .NET stack before any
    /// ES3 error could fire. The limit mirrors the era engines' parse-depth
    /// limits — beyond it, a SyntaxError is thrown (§16). Any exception from
    /// eval() is acceptable per the conformance corpus (regress-192414).</summary>
    private int _exprDepth;
    private int _stmtDepth;
    private const int MaxExpressionDepth = 512;

    public static ProgramNode Parse(string source, bool allowTopLevelReturn = false)
    {
        var lexer = new JsLexer(source ?? throw new ArgumentNullException(nameof(source)));
        IReadOnlyList<JsToken> tokens;
        try
        {
            tokens = lexer.Tokenize();
        }
        catch (JsLexerException ex)
        {
            // lexer failures are SyntaxErrors like parser failures — callers
            // only expect JsParserException from Parse
            throw new JsParserException(ex.Message, 0, 0);
        }
        var parser = new JsParser(tokens);
        return parser.ParseProgram(allowTopLevelReturn);
    }

    public JsParser(IReadOnlyList<JsToken> tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Token helpers
    // ─────────────────────────────────────────────────────────────────────

    private JsToken Peek() => _tokens.Count == 0
        ? new JsEofToken(0, 1, 1, false)
        : _tokens[Math.Min(_position, _tokens.Count - 1)];
    private JsToken? PeekNext()
    {
        int n = _position + 1;
        return n < _tokens.Count ? _tokens[n] : null;
    }
    private JsToken Advance() => _tokens[Math.Min(_position++, _tokens.Count - 1)];
    private bool IsAtEnd() => Peek() is JsEofToken;

    private bool CheckPunct(string p) =>
        Peek() is JsPunctuatorToken pt && pt.Punctuator == p;

    private bool CheckKeyword(string kw) =>
        Peek() is JsKeywordToken kt && kt.Keyword == kw;

    private bool CheckIdentifier() => Peek() is JsIdentifierToken;

    private bool MatchPunct(string p)
    {
        if (CheckPunct(p)) { Advance(); return true; }
        return false;
    }

    private bool MatchKeyword(string kw)
    {
        if (CheckKeyword(kw)) { Advance(); return true; }
        return false;
    }

    private JsToken ExpectPunct(string p)
    {
        if (CheckPunct(p)) return Advance();
        var t = Peek();
        throw new JsParserException($"Expected '{p}' but found '{Describe(t)}'", t.Line, t.Column);
    }

    private Identifier ExpectIdentifier(string what)
    {
        // §7.5: every keyword and future reserved word is rejected in
        // binding positions (var/function/params/catch/labels)
        if (Peek() is JsIdentifierToken id) { Advance(); return new Identifier(id.Name); }
        var t = Peek();
        throw new JsParserException(
            $"Expected {what} but found '{Describe(t)}'", t.Line, t.Column);
    }

    /// <summary>
    /// IdentifierName after '.' — §11.2 allows keywords AND future
    /// reserved words as member names (document.defaultStatus etc.).
    /// </summary>
    private Identifier ExpectIdentifierName(string what)
    {
        if (Peek() is JsIdentifierToken id) { Advance(); return new Identifier(id.Name); }
        if (Peek() is JsKeywordToken kw) { Advance(); return new Identifier(kw.Keyword); }
        var t = Peek();
        throw new JsParserException($"Expected {what} but found '{Describe(t)}'", t.Line, t.Column);
    }

    /// <summary>True when the NEXT token is separated by a line terminator.</summary>
    private bool NewlineBeforeNext() => Peek().AfterNewline;

    /// <summary>
    /// Consume a statement terminator: ';', an inserted one (newline, '}'
    /// or EOF ahead — a '}' always terminates the current statement per
    /// the ASI rules).  Throws when the next token could continue the
    /// statement.
    /// </summary>
    private void ConsumeStatementEnd(string context)
    {
        if (MatchPunct(";")) return;
        if (IsAtEnd()) return;
        if (NewlineBeforeNext()) return;   // ASI
        if (CheckPunct("}")) return;       // '}' terminates
        var t = Peek();
        throw new JsParserException($"Expected ';' after {context} but found '{Describe(t)}'", t.Line, t.Column);
    }

    private static string Describe(JsToken t) => t switch
    {
        JsEofToken        => "end of script",
        JsIdentifierToken i => i.Name,
        JsKeywordToken    k => k.Keyword,
        JsPunctuatorToken p => p.Punctuator,
        JsNumberToken    n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsStringToken    s => $"\"{s.Value}\"",
        _ => t.ToString()
    };

    // ─────────────────────────────────────────────────────────────────────
    // Program / statements
    // ─────────────────────────────────────────────────────────────────────

    public ProgramNode ParseProgram(bool allowTopLevelReturn = false)
    {
        var body = new List<Stmt>();
        while (!IsAtEnd())
        {
            var stmt = ParseStatement();
            if (stmt != null) body.Add(stmt);
        }
        var program = new ProgramNode(body);
        ValidateEarlyErrors(program, allowTopLevelReturn);
        return program;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Early errors (§12.6/§12.7/§12.8/§12.9/§12.12): continue must target an
    // enclosing iteration (with a matching label when labelled), break must
    // target an enclosing iteration or labelled statement, and return must
    // be inside a function body. These are compile-time SyntaxErrors.
    // ─────────────────────────────────────────────────────────────────────

    private static void ValidateEarlyErrors(ProgramNode program, bool allowTopLevelReturn)
    {
        var ctx = new EarlyErrorContext { AllowTopLevelReturn = allowTopLevelReturn };
        foreach (var stmt in program.Body)
            ValidateStmt(stmt, ctx);
    }

    private sealed class EarlyErrorContext
    {
        public bool InFunction;
        public bool AllowTopLevelReturn;
        public int IterationDepth;
        public int SwitchDepth;
        /// <summary>All enclosing labels — valid break targets (§12.12).</summary>
        public readonly List<string> Labels = new();
        /// <summary>Per enclosing loop, the label chain attached to that loop
        /// — the union is the set of valid labelled continue targets (§12.7).
        /// A label only counts when the labels attach DIRECTLY to an
        /// iteration (outer: for(;;) { for(;;) continue outer; } is legal,
        /// but outer: { for(;;) continue outer; } is not).</summary>
        public readonly List<List<string>> ActiveLoops = new();
    }

    private static void ValidateStmt(Stmt stmt, EarlyErrorContext ctx)
    {
        switch (stmt)
        {
            case BreakStatement br:
                if (br.Label == null)
                {
                    // §12.11: an unlabeled break also exits a switch case
                    if (ctx.IterationDepth == 0 && ctx.SwitchDepth == 0)
                        throw new JsParserException("Illegal break: not inside an iteration, switch, or labeled statement", 0, 0);
                }
                else if (!ctx.Labels.Contains(br.Label))
                {
                    throw new JsParserException($"Illegal break: no enclosing label '{br.Label}'", 0, 0);
                }
                break;

            case ContinueStatement co:
                if (co.Label == null)
                {
                    if (ctx.IterationDepth == 0)
                        throw new JsParserException("Illegal continue: not inside an iteration statement", 0, 0);
                }
                else if (!ctx.ActiveLoops.Any(loop => loop.Contains(co.Label)))
                {
                    throw new JsParserException($"Illegal continue: '{co.Label}' is not an enclosing iteration label", 0, 0);
                }
                break;

            case ReturnStatement:
                // inline DOM handlers (onclick="…; return false") are parsed
                // as top-level programs by the shell — keep them working
                if (!ctx.InFunction && !ctx.AllowTopLevelReturn)
                    throw new JsParserException("Illegal return: not inside a function", 0, 0);
                break;

            case LabeledStatement:
            {
                // collapse consecutive labels: a: b: for(;;) …
                var chain = new List<string>();
                Stmt body = stmt;
                while (body is LabeledStatement l2)
                {
                    chain.Add(l2.Label);
                    body = l2.Body;
                }
                ctx.Labels.AddRange(chain);
                switch (body)
                {
                    case WhileStatement w:
                        PushIteration(ctx, chain, w.Body);
                        break;
                    case DoWhileStatement dw:
                        PushIteration(ctx, chain, dw.Body);
                        break;
                    case ForStatement f:
                        if (f.Init is Stmt initStmt) ValidateStmt(initStmt, ctx);
                        PushIteration(ctx, chain, f.Body);
                        break;
                    case ForInStatement fi:
                        PushIteration(ctx, chain, fi.Body);
                        break;
                    default:
                        // the labels name a non-iteration statement: they are
                        // break targets only, not continue targets
                        ValidateStmt(body, ctx);
                        break;
                }
                foreach (var _ in chain) ctx.Labels.RemoveAt(ctx.Labels.Count - 1);
                break;
            }

            case BlockStatement block:
                foreach (var s in block.Body) ValidateStmt(s, ctx);
                break;

            case IfStatement iff:
                ValidateStmt(iff.Consequent, ctx);
                if (iff.Alternate != null) ValidateStmt(iff.Alternate, ctx);
                break;

            case WhileStatement w:
                PushIteration(ctx, new List<string>(), w.Body);
                break;

            case DoWhileStatement dw:
                PushIteration(ctx, new List<string>(), dw.Body);
                break;

            case ForStatement f:
                if (f.Init is Stmt initStmt2) ValidateStmt(initStmt2, ctx);
                PushIteration(ctx, new List<string>(), f.Body);
                break;

            case ForInStatement fi:
                PushIteration(ctx, new List<string>(), fi.Body);
                break;

            case SwitchStatement sw:
                ctx.SwitchDepth++;
                foreach (var c in sw.Cases)
                    foreach (var s in c.Consequent) ValidateStmt(s, ctx);
                ctx.SwitchDepth--;
                break;

            case TryStatement tr:
                ValidateStmt(tr.Block, ctx);
                if (tr.Handler != null) ValidateStmt(tr.Handler.Body, ctx);
                if (tr.Finalizer != null) ValidateStmt(tr.Finalizer, ctx);
                break;

            case WithStatement with:
                ValidateStmt(with.Body, ctx);
                break;

            // function bodies reset the iteration/label context
            case FunctionDeclaration fn:
                ValidateFunction(fn.Body.Body);
                break;
        }
    }

    private static void PushIteration(EarlyErrorContext ctx, List<string> directLabels, Stmt body)
    {
        ctx.IterationDepth++;
        ctx.ActiveLoops.Add(directLabels);
        ValidateStmt(body, ctx);
        ctx.ActiveLoops.RemoveAt(ctx.ActiveLoops.Count - 1);
        ctx.IterationDepth--;
    }

    private static void ValidateFunction(IReadOnlyList<Stmt> body)
    {
        var fnCtx = new EarlyErrorContext { InFunction = true };
        foreach (var stmt in body)
            ValidateStmt(stmt, fnCtx);
    }

    private Stmt ParseStatement()
    {
        // 100,000 nested do-while blocks used to overflow the real stack
        if (++_stmtDepth > MaxStatementDepth)
        {
            var t = Peek();
            throw new JsParserException("Statement nesting too deep", t.Line, t.Column);
        }
        try
        {
            return ParseStatementCore();
        }
        finally { _stmtDepth--; }
    }

    private const int MaxStatementDepth = 512;

    private Stmt ParseStatementCore()
    {
        if (IsAtEnd()) return new EmptyStatement();

        if (CheckPunct(";"))
        {
            Advance();
            return new EmptyStatement();
        }

        if (CheckPunct("{"))
            return ParseBlock();

        if (CheckKeyword("var"))
            return ParseVarDeclaration(allowIn: true);

        if (CheckKeyword("if"))         return ParseIf();
        if (CheckKeyword("while"))      return ParseWhile();
        if (CheckKeyword("do"))         return ParseDoWhile();
        if (CheckKeyword("for"))        return ParseFor();
        if (CheckKeyword("return"))     return ParseReturn();
        if (CheckKeyword("break"))      return ParseBreak();
        if (CheckKeyword("continue"))   return ParseContinue();
        if (CheckKeyword("switch"))     return ParseSwitch();
        if (CheckKeyword("throw"))      return ParseThrow();
        if (CheckKeyword("try"))        return ParseTry();
        if (CheckKeyword("function"))   return ParseFunctionDeclaration();
        if (CheckKeyword("with"))       return ParseWith();

        // Labeled statement: identifier ':' (but not "default:" etc.)
        if (Peek() is JsIdentifierToken && PeekNext() is JsPunctuatorToken np && np.Punctuator == ":")
            return ParseLabeled();

        return ParseExpressionStatement();
    }

    private BlockStatement ParseBlock()
    {
        ExpectPunct("{");
        var body = new List<Stmt>();
        while (!CheckPunct("}") && !IsAtEnd())
        {
            var stmt = ParseStatement();
            if (stmt != null) body.Add(stmt);
        }
        ExpectPunct("}");
        return new BlockStatement(body);
    }

    private VarDeclaration ParseVarDeclaration(bool allowIn)
    {
        ExpectKeyword("var");
        var declarators = new List<VarDeclarator>();
        do
        {
            var id = ExpectIdentifier("identifier in var declaration");
            Expr? init = null;
            if (MatchPunct("="))
                init = ParseAssignment(allowIn);
            declarators.Add(new VarDeclarator(id, init));
        } while (MatchPunct(","));

        ConsumeStatementEnd("var declaration");
        return new VarDeclaration(declarators);
    }

    private JsToken ExpectKeyword(string kw)
    {
        if (CheckKeyword(kw)) return Advance();
        var t = Peek();
        throw new JsParserException($"Expected '{kw}' but found '{Describe(t)}'", t.Line, t.Column);
    }

    private Stmt ParseIf()
    {
        ExpectKeyword("if");
        ExpectPunct("(");
        var test = ParseExpression();
        ExpectPunct(")");
        var consequent = ParseStatement();
        Stmt? alternate = null;
        if (MatchKeyword("else"))
            alternate = ParseStatement();
        return new IfStatement(test, consequent, alternate);
    }

    private Stmt ParseWhile()
    {
        ExpectKeyword("while");
        ExpectPunct("(");
        var test = ParseExpression();
        ExpectPunct(")");
        var body = ParseStatement();
        return new WhileStatement(test, body);
    }

    private Stmt ParseDoWhile()
    {
        ExpectKeyword("do");
        var body = ParseStatement();
        ExpectKeyword("while");
        ExpectPunct("(");
        var test = ParseExpression();
        ExpectPunct(")");
        // ASI allows the trailing ';' to be omitted after do…while
        MatchPunct(";");
        return new DoWhileStatement(body, test);
    }

    private Stmt ParseFor()
    {
        ExpectKeyword("for");
        ExpectPunct("(");

        Node? init = null;

        if (CheckKeyword("var"))
        {
            // Parse the first declarator with 'in' disabled to detect for-in
            ExpectKeyword("var");
            var first = ExpectIdentifier("identifier in for-var");
            Expr? firstInit = null;
            bool firstHasInit = false;
            if (MatchPunct("="))
            {
                firstInit = ParseAssignment(allowIn: false);
                firstHasInit = true;
            }

            // for (var x in obj) — no initializer allowed before 'in'
            if (CheckKeyword("in") && !firstHasInit)
            {
                Advance();
                var right = ParseExpression();
                ExpectPunct(")");
                var body = ParseStatement();
                return new ForInStatement(
                    new VarDeclaration(new[] { new VarDeclarator(first, null) }), right, body);
            }

            var declarators = new List<VarDeclarator> { new(first, firstInit) };
            while (MatchPunct(","))
            {
                var id = ExpectIdentifier("identifier in for-var list");
                Expr? di = null;
                if (MatchPunct("="))
                    di = ParseAssignment(allowIn: false);
                declarators.Add(new VarDeclarator(id, di));
            }
            init = new VarDeclaration(declarators);
        }
        else if (!CheckPunct(";"))
        {
            // Expression init — 'in' must not be consumed as a relational
            // operator here; parse with noIn, then check for for-in
            var expr = ParseExpression(allowIn: false);

            if (CheckKeyword("in"))
            {
                Advance();
                var right = ParseExpression();
                ExpectPunct(")");
                var body = ParseStatement();
                return new ForInStatement(expr, right, body);
            }
            init = new ExpressionStatement(expr);
        }

        ExpectPunct(";");

        Expr? test = null;
        if (!CheckPunct(";"))
            test = ParseExpression();
        ExpectPunct(";");

        Expr? update = null;
        if (!CheckPunct(")"))
            update = ParseExpression();
        ExpectPunct(")");

        var forBody = ParseStatement();
        return new ForStatement(init, test, update, forBody);
    }

    private Stmt ParseReturn()
    {
        ExpectKeyword("return");

        // Restricted production: a newline after 'return' ends the statement
        if (CheckPunct(";") || IsAtEnd() || NewlineBeforeNext())
        {
            ConsumeStatementEnd("return");
            return new ReturnStatement(null);
        }

        var argument = ParseExpression();
        ConsumeStatementEnd("return");
        return new ReturnStatement(argument);
    }

    private Stmt ParseBreak()
    {
        ExpectKeyword("break");
        string? label = null;
        if (CheckIdentifier() && !NewlineBeforeNext())
            label = ((JsIdentifierToken)Advance()).Name;
        ConsumeStatementEnd("break");
        return new BreakStatement(label);
    }

    private Stmt ParseContinue()
    {
        ExpectKeyword("continue");
        string? label = null;
        if (CheckIdentifier() && !NewlineBeforeNext())
            label = ((JsIdentifierToken)Advance()).Name;
        ConsumeStatementEnd("continue");
        return new ContinueStatement(label);
    }

    private Stmt ParseSwitch()
    {
        ExpectKeyword("switch");
        ExpectPunct("(");
        var discriminant = ParseExpression();
        ExpectPunct(")");
        ExpectPunct("{");

        var cases = new List<SwitchCase>();

        while (!CheckPunct("}") && !IsAtEnd())
        {
            Expr? test = null;
            if (MatchKeyword("case"))
            {
                test = ParseExpression();
            }
            else if (!MatchKeyword("default"))
            {
                var t = Peek();
                throw new JsParserException("Expected 'case' or 'default' in switch", t.Line, t.Column);
            }
            ExpectPunct(":");

            var consequent = new List<Stmt>();
            while (!CheckKeyword("case") && !CheckKeyword("default")
                   && !CheckPunct("}") && !IsAtEnd())
            {
                var s = ParseStatement();
                if (s != null) consequent.Add(s);
            }
            cases.Add(new SwitchCase(test, consequent));
        }

        ExpectPunct("}");
        return new SwitchStatement(discriminant, cases);
    }

    private Stmt ParseThrow()
    {
        ExpectKeyword("throw");
        if (NewlineBeforeNext())
        {
            var t = Peek();
            throw new JsParserException("Illegal newline after 'throw'", t.Line, t.Column);
        }
        var argument = ParseExpression();
        ConsumeStatementEnd("throw");
        return new ThrowStatement(argument);
    }

    private Stmt ParseTry()
    {
        ExpectKeyword("try");
        var block = ParseBlock();

        CatchClause? handler = null;
        Stmt? finalizer = null;

        if (CheckKeyword("catch"))
        {
            Advance();
            ExpectPunct("(");
            var param = ExpectIdentifier("catch parameter");
            ExpectPunct(")");
            var catchBlock = ParseBlock();
            handler = new CatchClause(param, catchBlock);
        }

        if (MatchKeyword("finally"))
            finalizer = ParseBlock();

        if (handler == null && finalizer == null)
        {
            var t = Peek();
            throw new JsParserException("Try statement must have catch or finally", t.Line, t.Column);
        }

        return new TryStatement(block, handler, finalizer);
    }

    private FunctionDeclaration ParseFunctionDeclaration()
    {
        ExpectKeyword("function");
        var name = ExpectIdentifier("function name");
        var parameters = ParseParameterList();
        var body = ParseBlock();
        return new FunctionDeclaration(name, parameters, body);
    }

    private List<Identifier> ParseParameterList()
    {
        ExpectPunct("(");
        var parameters = new List<Identifier>();
        if (!CheckPunct(")"))
        {
            do
            {
                parameters.Add(ExpectIdentifier("parameter name"));
            } while (MatchPunct(","));
        }
        ExpectPunct(")");
        return parameters;
    }

    private Stmt ParseWith()
    {
        ExpectKeyword("with");
        ExpectPunct("(");
        var objectExpr = ParseExpression();
        ExpectPunct(")");
        var body = ParseStatement();
        return new WithStatement(objectExpr, body);
    }

    private Stmt ParseLabeled()
    {
        var label = ((JsIdentifierToken)Advance()).Name;
        ExpectPunct(":");
        var body = ParseStatement();
        return new LabeledStatement(label, body);
    }

    private Stmt ParseExpressionStatement()
    {
        var expr = ParseExpression();
        ConsumeStatementEnd("expression");
        return new ExpressionStatement(expr);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Expressions (precedence climbing)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Comma expression.  allowIn=false while parsing a for-init.</summary>
    private Expr ParseExpression(bool allowIn = true)
    {
        if (++_exprDepth > MaxExpressionDepth)
        {
            var t = Peek();
            throw new JsParserException("Expression nesting too deep", t.Line, t.Column);
        }
        try
        {
            var expr = ParseAssignment(allowIn);
            while (CheckPunct(","))
            {
                Advance();
                var right = ParseAssignment(allowIn);
                expr = new BinaryExpr(",", expr, right);
            }
            return expr;
        }
        finally { _exprDepth--; }
    }

    private static readonly string[] _assignOps =
    {
        "=", "+=", "-=", "*=", "/=", "%=",
        "<<=", ">>=", ">>>=", "&=", "^=", "|="
    };

    private Expr ParseAssignment(bool allowIn)
    {
        var left = ParseTernary(allowIn);

        foreach (var op in _assignOps)
        {
            if (CheckPunct(op))
            {
                Advance();
                var right = ParseAssignment(allowIn);   // right-associative
                return new AssignmentExpr(op, left, right);
            }
        }

        return left;
    }

    private Expr ParseTernary(bool allowIn)
    {
        var expr = ParseLogicalOr(allowIn);
        if (MatchPunct("?"))
        {
            var consequent = ParseAssignment(allowIn);
            ExpectPunct(":");
            var alternate = ParseAssignment(allowIn);
            return new TernaryExpr(expr, consequent, alternate);
        }
        return expr;
    }

    private Expr ParseLogicalOr(bool allowIn)
    {
        var left = ParseLogicalAnd(allowIn);
        while (CheckPunct("||"))
        {
            Advance();
            left = new LogicalExpr("||", left, ParseLogicalAnd(allowIn));
        }
        return left;
    }

    private Expr ParseLogicalAnd(bool allowIn)
    {
        var left = ParseBitwiseOr(allowIn);
        while (CheckPunct("&&"))
        {
            Advance();
            left = new LogicalExpr("&&", left, ParseBitwiseOr(allowIn));
        }
        return left;
    }

    private Expr ParseBitwiseOr(bool allowIn)
    {
        var left = ParseBitwiseXor(allowIn);
        while (CheckPunct("|"))
        {
            Advance();
            left = new BinaryExpr("|", left, ParseBitwiseXor(allowIn));
        }
        return left;
    }

    private Expr ParseBitwiseXor(bool allowIn)
    {
        var left = ParseBitwiseAnd(allowIn);
        while (CheckPunct("^"))
        {
            Advance();
            left = new BinaryExpr("^", left, ParseBitwiseAnd(allowIn));
        }
        return left;
    }

    private Expr ParseBitwiseAnd(bool allowIn)
    {
        var left = ParseEquality(allowIn);
        while (CheckPunct("&"))
        {
            Advance();
            left = new BinaryExpr("&", left, ParseEquality(allowIn));
        }
        return left;
    }

    private Expr ParseEquality(bool allowIn)
    {
        var left = ParseRelational(allowIn);
        while (Peek() is JsPunctuatorToken pt &&
               pt.Punctuator is "==" or "!=" or "===" or "!==")
        {
            Advance();
            left = new BinaryExpr(pt.Punctuator, left, ParseRelational(allowIn));
        }
        return left;
    }

    private Expr ParseRelational(bool allowIn)
    {
        var left = ParseShift();
        while (true)
        {
            if (Peek() is JsPunctuatorToken pt &&
                pt.Punctuator is "<" or ">" or "<=" or ">=")
            {
                Advance();
                left = new BinaryExpr(pt.Punctuator, left, ParseShift());
                continue;
            }
            // The for-init noIn flag restricts ONLY the 'in' operator —
            // `for (a instanceof B; …)` used to fail parsing because
            // instanceof was gated by allowIn too.
            if (CheckKeyword("instanceof"))
            {
                Advance();
                left = new InstanceofExpr(left, ParseShift());
                continue;
            }
            if (allowIn && CheckKeyword("in"))
            {
                Advance();
                left = new InExpr(left, ParseShift());
                continue;
            }
            break;
        }
        return left;
    }

    private Expr ParseShift()
    {
        var left = ParseAdditive();
        while (Peek() is JsPunctuatorToken pt && pt.Punctuator is "<<" or ">>" or ">>>")
        {
            Advance();
            left = new BinaryExpr(pt.Punctuator, left, ParseAdditive());
        }
        return left;
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Peek() is JsPunctuatorToken pt && pt.Punctuator is "+" or "-")
        {
            Advance();
            left = new BinaryExpr(pt.Punctuator, left, ParseMultiplicative());
        }
        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Peek() is JsPunctuatorToken pt && pt.Punctuator is "*" or "/" or "%")
        {
            Advance();
            left = new BinaryExpr(pt.Punctuator, left, ParseUnary());
        }
        return left;
    }

    private Expr ParseUnary()
    {
        // pathological chains ('+ + + ... + x' 10,000 deep) used to overflow
        // the real stack before any ES3 error could fire
        if (++_exprDepth > MaxExpressionDepth)
        {
            var t = Peek();
            throw new JsParserException("Expression nesting too deep", t.Line, t.Column);
        }
        try
        {
            return ParseUnaryCore();
        }
        finally { _exprDepth--; }
    }

    private Expr ParseUnaryCore()
    {
        if (CheckPunct("!"))  { Advance(); return new UnaryExpr("!",  ParseUnary()); }
        if (CheckPunct("~"))  { Advance(); return new UnaryExpr("~",  ParseUnary()); }
        if (CheckPunct("+"))  { Advance(); return new UnaryExpr("+",  ParseUnary()); }
        if (CheckPunct("-"))  { Advance(); return new UnaryExpr("-",  ParseUnary()); }

        if (CheckKeyword("typeof")) { Advance(); return new TypeofExpr(ParseUnary()); }
        if (CheckKeyword("void"))   { Advance(); return new VoidExpr(ParseUnary()); }
        if (CheckKeyword("delete")) { Advance(); return new DeleteExpr(ParseUnary()); }

        if (CheckPunct("++")) { Advance(); return new UpdateExpr("++", ParseUnary(), Prefix: true); }
        if (CheckPunct("--")) { Advance(); return new UpdateExpr("--", ParseUnary(), Prefix: true); }

        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var expr = ParseCallMember();

        // Restricted production: postfix ++/-- must sit on the same line
        if ((CheckPunct("++") || CheckPunct("--")) && !NewlineBeforeNext())
        {
            var op = ((JsPunctuatorToken)Advance()).Punctuator;
            return new UpdateExpr(op, expr, Prefix: false);
        }

        return expr;
    }

    private Expr ParseCallMember()
    {
        // new expression: new Callee(args) — callee is a member chain
        if (CheckKeyword("new"))
        {
            return ParseNew();
        }

        var expr = ParsePrimary();
        return ParseCallMemberTail(expr);
    }

    private Expr ParseCallMemberTail(Expr expr)
    {
        while (true)
        {
            if (CheckPunct("."))
            {
                Advance();
                var id = ExpectIdentifierName("identifier after '.'");
                expr = new MemberExpr(expr, id, Computed: false);
            }
            else if (CheckPunct("["))
            {
                Advance();
                var property = ParseExpression();
                ExpectPunct("]");
                expr = new MemberExpr(expr, property, Computed: true);
            }
            else if (CheckPunct("("))
            {
                Advance();
                var args = new List<Expr>();
                if (!CheckPunct(")"))
                {
                    do
                    {
                        // Assignment level — NOT ParseExpression, whose comma
                        // operator would swallow the argument separators
                        args.Add(ParseAssignment(allowIn: true));
                    } while (MatchPunct(","));
                }
                ExpectPunct(")");
                expr = new CallExpr(expr, args);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    /// <summary>
    /// new a.b.C(x, y) — parse the constructor as a member chain (dots
    /// only, no calls), then the argument list.  `new Date` without
    /// parentheses is also legal.  The callee may itself be another new
    /// expression (`new new Foo()` used to be a parse error).
    /// </summary>
    private Expr ParseNew()
    {
        ExpectKeyword("new");

        Expr callee = ParseNewCallee();

        var args = new List<Expr>();
        if (CheckPunct("("))
        {
            Advance();
            if (!CheckPunct(")"))
            {
                do
                {
                    args.Add(ParseAssignment(allowIn: true));
                } while (MatchPunct(","));
            }
            ExpectPunct(")");
        }

        var newExpr = new NewExpr(callee, args);

        // The result is a value: calls and member access chain onto it
        return ParseCallMemberTail(newExpr);
    }

    /// <summary>Callee of a new-expression: a member chain, or ANOTHER
    /// new expression (new new Foo()).</summary>
    private Expr ParseNewCallee()
    {
        if (CheckKeyword("new"))
            return ParseNew();

        Expr callee = ParsePrimary();
        while (true)
        {
            if (CheckPunct("."))
            {
                Advance();
                var id = ExpectIdentifierName("identifier after '.'");
                callee = new MemberExpr(callee, id, Computed: false);
            }
            else if (CheckPunct("["))
            {
                Advance();
                var property = ParseExpression();
                ExpectPunct("]");
                callee = new MemberExpr(callee, property, Computed: true);
            }
            else break;
        }
        return callee;
    }

    private Expr ParsePrimary()
    {
        var token = Peek();

        if (CheckKeyword("this"))  { Advance(); return new ThisExpr(); }
        if (CheckKeyword("true"))  { Advance(); return new BoolLiteral(true); }
        if (CheckKeyword("false")) { Advance(); return new BoolLiteral(false); }
        if (CheckKeyword("null"))  { Advance(); return new NullLiteral(); }

        if (token is JsIdentifierToken id)
        {
            Advance();
            return new Identifier(id.Name);
        }

        if (token is JsNumberToken num)
        {
            Advance();
            return new NumberLiteral(num.Value);
        }

        if (token is JsStringToken str)
        {
            Advance();
            return new StringLiteral(str.Value);
        }

        if (token is JsRegexLiteralToken regex)
        {
            Advance();
            return new RegexLiteral(regex.Pattern, regex.Flags);
        }

        if (CheckKeyword("function"))
            return ParseFunctionExpression();

        if (CheckPunct("("))
        {
            Advance();
            var expr = ParseExpression();
            ExpectPunct(")");
            return expr;
        }

        if (CheckPunct("["))
            return ParseArrayLiteral();

        if (CheckPunct("{"))
            return ParseObjectLiteral();

        throw new JsParserException($"Unexpected token '{Describe(token)}' in expression", token.Line, token.Column);
    }

    private Expr ParseFunctionExpression()
    {
        ExpectKeyword("function");
        Identifier? name = null;
        if (CheckIdentifier())
            name = new Identifier(((JsIdentifierToken)Advance()).Name);
        var parameters = ParseParameterList();
        var body = ParseBlock();
        return new FunctionExpr(name, parameters, body);
    }

    private Expr ParseArrayLiteral()
    {
        ExpectPunct("[");
        var elements = new List<Expr?>();

        while (!CheckPunct("]") && !IsAtEnd())
        {
            if (CheckPunct(","))
            {
                elements.Add(null);   // hole
                Advance();
                continue;
            }

            elements.Add(ParseAssignment(allowIn: true));

            if (MatchPunct(","))
            {
                if (CheckPunct("]"))   // trailing comma
                    break;
                continue;
            }
            break;
        }

        ExpectPunct("]");
        return new ArrayExpr(elements);
    }

    private Expr ParseObjectLiteral()
    {
        ExpectPunct("{");
        var properties = new List<PropertyExpr>();

        while (!CheckPunct("}") && !IsAtEnd())
        {
            Expr key;
            if (Peek() is JsIdentifierToken idTok)
            {
                Advance();
                key = new Identifier(idTok.Name);
            }
            else if (Peek() is JsKeywordToken kwTok)
            {
                Advance();
                key = new Identifier(kwTok.Keyword);   // {default: 1} — sloppy pages did this
            }
            else if (Peek() is JsStringToken s)
            {
                Advance();
                key = new StringLiteral(s.Value);
            }
            else if (Peek() is JsNumberToken n)
            {
                Advance();
                key = new NumberLiteral(n.Value);
            }
            else
            {
                var t = Peek();
                throw new JsParserException("Expected property key in object literal", t.Line, t.Column);
            }

            ExpectPunct(":");
            var value = ParseAssignment(allowIn: true);
            properties.Add(new PropertyExpr(key, value));

            if (MatchPunct(","))
            {
                if (CheckPunct("}"))   // trailing comma
                    break;
                continue;
            }
            break;
        }

        ExpectPunct("}");
        return new ObjectExpr(properties);
    }
}