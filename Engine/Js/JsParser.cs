using System;
using System.Collections.Generic;

namespace Retro96.Engine.Js
{
    public class JsParserException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public JsParserException(string message, int line, int column) 
            : base($"Line {line}, Column {column}: {message}")
        {
            Line = line;
            Column = column;
        }
    }

    public class JsParser
    {
        private readonly List<JsToken> _tokens;
        private int _position;

        public static ProgramNode Parse(string source)
        {
            var lexer = new JsLexer(source);
            var tokens = lexer.Tokenize().ToList();
            var parser = new JsParser(tokens);
            return parser.ParseProgram();
        }

        internal JsParser(List<JsToken> tokens)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _position = 0;
        }

        #region Public Parsing Entry Point
        internal ProgramNode ParseProgram()
        {
            var body = new List<object>();
            while (!IsAtEnd() && !(Peek() is JsEofToken))
            {
                var stmt = ParseStatement();
                if (stmt != null)
                {
                    body.Add(stmt);
                }
            }
            return new ProgramNode(body);
        }
        #endregion

        #region Helper Methods
        private bool IsAtEnd()
        {
            return _position >= _tokens.Count || Peek() is JsEofToken;
        }

        private JsToken Peek()
        {
            if (_position >= _tokens.Count)
                throw new InvalidOperationException("No more tokens available");
            return _tokens[_position];
        }

        private JsToken? PeekNext()
        {
            var nextPos = _position + 1;
            return nextPos < _tokens.Count ? _tokens[nextPos] : null;
        }

        private JsToken Advance()
        {
            var token = Peek();
            _position++;
            return token;
        }

        private JsToken Previous()
        {
            if (_position == 0)
                throw new InvalidOperationException("No previous token available");
            return _tokens[_position - 1];
        }

        private bool CheckPunctuator(string punct)
        {
            if (IsAtEnd())
                return false;
            return Peek() is JsPunctuatorToken punctToken && punctToken.Punctuator == punct;
        }

        private bool CheckKeyword(string kw)
        {
            if (IsAtEnd())
                return false;
            return Peek() is JsKeywordToken kwToken && kwToken.Keyword == kw;
        }

        private bool CheckIdentifier()
        {
            return Peek() is JsIdentifierToken;
        }

        private bool CheckStringLiteral()
        {
            return Peek() is JsStringToken;
        }

        private bool CheckNumberLiteral()
        {
            return Peek() is JsNumberToken;
        }

        private bool MatchPunctuator(string punct)
        {
            if (CheckPunctuator(punct))
            {
                Advance();
                return true;
            }
            return false;
        }

        private bool MatchKeyword(string kw)
        {
            if (CheckKeyword(kw))
            {
                Advance();
                return true;
            }
            return false;
        }

        private JsToken ExpectPunctuator(string punct)
        {
            if (CheckPunctuator(punct))
            {
                return Advance();
            }
            var token = Peek();
            throw new JsParserException($"Expected punctuator '{punct}' but found '{token}'", token.Line, token.Column);
        }

        private JsToken ExpectKeyword(string kw)
        {
            if (CheckKeyword(kw))
            {
                return Advance();
            }
            var token = Peek();
            throw new JsParserException($"Expected keyword '{kw}' but found '{token}'", token.Line, token.Column);
        }
        #endregion

        #region Statement Parsing
        private object ParseStatement()
        {
            if (IsAtEnd())
                return null;

            var token = Peek();

            // Empty statement
            if (CheckPunctuator(";"))
            {
                Advance();
                return new EmptyStatement();
            }

            // Block statement
            if (CheckPunctuator("{"))
            {
                return ParseBlockStatement();
            }

            // Var declaration
            if (CheckKeyword("var"))
            {
                return ParseVarDeclaration();
            }

            // If statement
            if (CheckKeyword("if"))
            {
                return ParseIfStatement();
            }

            // While statement
            if (CheckKeyword("while"))
            {
                return ParseWhileStatement();
            }

            // Do-while statement
            if (CheckKeyword("do"))
            {
                return ParseDoWhileStatement();
            }

            // For statement (including for-in)
            if (CheckKeyword("for"))
            {
                return ParseForStatement();
            }

            // Return statement
            if (CheckKeyword("return"))
            {
                return ParseReturnStatement();
            }

            // Break statement
            if (CheckKeyword("break"))
            {
                return ParseBreakStatement();
            }

            // Continue statement
            if (CheckKeyword("continue"))
            {
                return ParseContinueStatement();
            }

            // Switch statement
            if (CheckKeyword("switch"))
            {
                return ParseSwitchStatement();
            }

            // Throw statement
            if (CheckKeyword("throw"))
            {
                return ParseThrowStatement();
            }

            // Try statement
            if (CheckKeyword("try"))
            {
                return ParseTryStatement();
            }

            // Function declaration
            if (CheckKeyword("function"))
            {
                return ParseFunctionDeclaration();
            }

            // With statement
            if (CheckKeyword("with"))
            {
                return ParseWithStatement();
            }

            // Labeled statement (identifier followed by :)
            if (token is JsIdentifierToken && PeekNext() is JsPunctuatorToken nextPunct && nextPunct.Punctuator == ":")
            {
                return ParseLabeledStatement();
            }

            // Expression statement
            return ParseExpressionStatement();
        }

        private object ParseBlockStatement()
        {
            ExpectPunctuator("{");
            var body = new List<object>();
            while (!CheckPunctuator("}") && !IsAtEnd())
            {
                var stmt = ParseStatement();
                if (stmt != null)
                {
                    body.Add(stmt);
                }
            }
            ExpectPunctuator("}");
            return new BlockStatement(body);
        }

        private object ParseVarDeclaration()
        {
            ExpectKeyword("var");
            var declarators = new List<VarDeclarator>();
            do
            {
                if (!CheckIdentifier())
                    throw new JsParserException("Expected identifier in var declaration", Peek().Line, Peek().Column);
                
                var idToken = Advance() as JsIdentifierToken;
                var id = new Identifier(idToken.Name);
                object init = null;
                
                if (MatchPunctuator("="))
                {
                    init = ParseAssignmentExpression();
                }
                
                declarators.Add(new VarDeclarator(id, init));
            } while (MatchPunctuator(","));
            
            ExpectPunctuator(";");
            return new VarDeclaration(declarators);
        }

        private object ParseIfStatement()
        {
            ExpectKeyword("if");
            ExpectPunctuator("(");
            var test = ParseExpression();
            ExpectPunctuator(")");
            var consequent = ParseStatement();
            object alternate = null;
            
            if (MatchKeyword("else"))
            {
                alternate = ParseStatement();
            }
            
            return new IfStatement(test, consequent, alternate);
        }

        private object ParseWhileStatement()
        {
            ExpectKeyword("while");
            ExpectPunctuator("(");
            var test = ParseExpression();
            ExpectPunctuator(")");
            var body = ParseStatement();
            return new WhileStatement(test, body);
        }

        private object ParseDoWhileStatement()
        {
            ExpectKeyword("do");
            var body = ParseStatement();
            ExpectKeyword("while");
            ExpectPunctuator("(");
            var test = ParseExpression();
            ExpectPunctuator(")");
            ExpectPunctuator(";");
            return new DoWhileStatement(body, test);
        }

        private object ParseForStatement()
        {
            ExpectKeyword("for");
            ExpectPunctuator("(");

            // Parse init (var, expression, or empty)
            object init = null;
            VarDeclarator varDecl = null;
            bool isVarInit = false;

            if (CheckKeyword("var"))
            {
                Advance(); // consume var
                isVarInit = true;
                
                if (!CheckIdentifier())
                    throw new JsParserException("Expected identifier in for var declaration", Peek().Line, Peek().Column);
                
                var idToken = Advance() as JsIdentifierToken;
                var id = new Identifier(idToken.Name);
                object varInitExpr = null;
                
                if (MatchPunctuator("="))
                {
                    varInitExpr = ParseAssignmentExpression();
                }
                varDecl = new VarDeclarator(id, varInitExpr);

                // Check for for-in
                if (CheckKeyword("in"))
                {
                    Advance(); // consume in
                    var right = ParseExpression();
                    ExpectPunctuator(")");
                    var forBodyVar = ParseStatement();
                    return new ForInStatement(new VarDeclaration(new List<VarDeclarator> { varDecl }), right, forBodyVar);
                }

                // Parse additional var declarators
                var declarators = new List<VarDeclarator> { varDecl };
                while (MatchPunctuator(","))
                {
                    if (!CheckIdentifier())
                        throw new JsParserException("Expected identifier in for var declarator", Peek().Line, Peek().Column);
                    
                    var idToken2 = Advance() as JsIdentifierToken;
                    var id2 = new Identifier(idToken2.Name);
                    object init2 = null;
                    
                    if (MatchPunctuator("="))
                    {
                        init2 = ParseAssignmentExpression();
                    }
                    declarators.Add(new VarDeclarator(id2, init2));
                }
                init = new VarDeclaration(declarators);
            }
            else if (!CheckPunctuator(";"))
            {
                init = ParseExpression();
                
                // Check for for-in with identifier
                if (init is Identifier idNode && CheckKeyword("in"))
                {
                    Advance(); // consume in
                    var right = ParseExpression();
                    ExpectPunctuator(")");
                    var forBodyId = ParseStatement();
                    return new ForInStatement(idNode, right, forBodyId);
                }
            }

            // If not for-in, parse as normal for loop
            ExpectPunctuator(";");

            // Parse test
            object test = null;
            if (!CheckPunctuator(";"))
            {
                test = ParseExpression();
            }
            ExpectPunctuator(";");

            // Parse update
            object update = null;
            if (!CheckPunctuator(")"))
            {
                update = ParseExpression();
            }
            ExpectPunctuator(")");

            var forBodyNormal = ParseStatement();
            return new ForStatement(init, test, update, forBodyNormal);
        }

        private object ParseReturnStatement()
        {
            ExpectKeyword("return");
            object argument = null;
            
            if (!CheckPunctuator(";") && !IsAtEnd() && !(Peek() is JsEofToken))
            {
                argument = ParseExpression();
            }
            
            ExpectPunctuator(";");
            return new ReturnStatement(argument);
        }

        private object ParseBreakStatement()
        {
            ExpectKeyword("break");
            string label = null;
            
            if (CheckIdentifier())
            {
                var idToken = Advance() as JsIdentifierToken;
                label = idToken.Name;
            }
            
            ExpectPunctuator(";");
            return new BreakStatement(label);
        }

        private object ParseContinueStatement()
        {
            ExpectKeyword("continue");
            string label = null;
            
            if (CheckIdentifier())
            {
                var idToken = Advance() as JsIdentifierToken;
                label = idToken.Name;
            }
            
            ExpectPunctuator(";");
            return new ContinueStatement(label);
        }

        private object ParseSwitchStatement()
        {
            ExpectKeyword("switch");
            ExpectPunctuator("(");
            var discriminant = ParseExpression();
            ExpectPunctuator(")");
            ExpectPunctuator("{");

            var cases = new List<SwitchCase>();

            while (!CheckPunctuator("}") && !IsAtEnd())
            {
                if (CheckKeyword("case"))
                {
                    Advance(); // consume case
                    var test = ParseExpression();
                    ExpectPunctuator(":");
                    var consequent = new List<object>();
                    
                    while (!CheckKeyword("case") && !CheckKeyword("default") && !CheckPunctuator("}") && !IsAtEnd())
                    {
                        var stmt = ParseStatement();
                        if (stmt != null)
                            consequent.Add(stmt);
                    }
                    
                    cases.Add(new SwitchCase(test, consequent));
                }
                else if (CheckKeyword("default"))
                {
                    Advance(); // consume default
                    ExpectPunctuator(":");
                    var consequent = new List<object>();
                    
                    while (!CheckKeyword("case") && !CheckKeyword("default") && !CheckPunctuator("}") && !IsAtEnd())
                    {
                        var stmt = ParseStatement();
                        if (stmt != null)
                            consequent.Add(stmt);
                    }
                    
                    cases.Add(new SwitchCase(null, consequent));
                }
                else
                {
                    throw new JsParserException("Expected 'case' or 'default' in switch statement", Peek().Line, Peek().Column);
                }
            }

            ExpectPunctuator("}");
            return new SwitchStatement(discriminant, cases);
        }

        private object ParseThrowStatement()
        {
            ExpectKeyword("throw");
            var argument = ParseExpression();
            ExpectPunctuator(";");
            return new ThrowStatement(argument);
        }

        private object ParseTryStatement()
        {
            ExpectKeyword("try");
            var block = ParseBlockStatement();
            CatchClause catchClause = null;
            object finallyClause = null;

            if (CheckKeyword("catch"))
            {
                Advance(); // consume catch
                ExpectPunctuator("(");
                
                if (!CheckIdentifier())
                    throw new JsParserException("Expected identifier in catch clause", Peek().Line, Peek().Column);
                
                var paramToken = Advance() as JsIdentifierToken;
                var param = new Identifier(paramToken.Name);
                ExpectPunctuator(")");
                var catchBlock = ParseBlockStatement();
                catchClause = new CatchClause(param, catchBlock);
            }

            if (CheckKeyword("finally"))
            {
                Advance(); // consume finally
                finallyClause = ParseBlockStatement();
            }

            if (catchClause == null && finallyClause == null)
                throw new JsParserException("Try statement must have catch or finally block", Peek().Line, Peek().Column);

            return new TryStatement(block, catchClause, finallyClause);
        }

        private object ParseFunctionDeclaration()
        {
            ExpectKeyword("function");
            
            if (!CheckIdentifier())
                throw new JsParserException("Expected function name in declaration", Peek().Line, Peek().Column);
            
            var nameToken = Advance() as JsIdentifierToken;
            var name = new Identifier(nameToken.Name);
            
            ExpectPunctuator("(");
            var parameters = new List<Identifier>();
            
            if (!CheckPunctuator(")"))
            {
                do
                {
                    if (!CheckIdentifier())
                        throw new JsParserException("Expected parameter name", Peek().Line, Peek().Column);
                    
                    var paramToken = Advance() as JsIdentifierToken;
                    parameters.Add(new Identifier(paramToken.Name));
                } while (MatchPunctuator(","));
            }
            
            ExpectPunctuator(")");
            var body = ParseBlockStatement();
            
            return new FunctionDeclaration(name, parameters, body);
        }

        private object ParseWithStatement()
        {
            ExpectKeyword("with");
            ExpectPunctuator("(");
            var objectExpr = ParseExpression();
            ExpectPunctuator(")");
            var body = ParseStatement();
            return new WithStatement(objectExpr, body);
        }

        private object ParseLabeledStatement()
        {
            var labelToken = Advance() as JsIdentifierToken;
            var label = labelToken.Name;
            ExpectPunctuator(":");
            var body = ParseStatement();
            return new LabeledStatement(label, body);
        }

        private object ParseExpressionStatement()
        {
            var expr = ParseExpression();
            ExpectPunctuator(";");
            return new ExpressionStatement(expr);
        }
        #endregion

        #region Expression Parsing (Precedence Climbing)
        private object ParseExpression()
        {
            var expr = ParseAssignmentExpression();
            while (MatchPunctuator(","))
            {
                var right = ParseAssignmentExpression();
                expr = new BinaryExpr(",", expr, right);
            }
            return expr;
        }

        private object ParseAssignmentExpression()
        {
            var left = ParseTernaryExpression();
            
            // Check for assignment operators
            if (CheckPunctuator("=") || CheckPunctuator("+=") || CheckPunctuator("-=") || 
                CheckPunctuator("*=") || CheckPunctuator("/=") || CheckPunctuator("%=") || 
                CheckPunctuator("<<=") || CheckPunctuator(">>=") || CheckPunctuator(">>>=") || 
                CheckPunctuator("&=") || CheckPunctuator("^=") || CheckPunctuator("|="))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseAssignmentExpression(); // Right-associative
                return new AssignmentExpr(op, left, right);
            }
            
            return left;
        }

        private object ParseTernaryExpression()
        {
            var expr = ParseLogicalOr();
            if (MatchPunctuator("?"))
            {
                var consequent = ParseExpression();
                ExpectPunctuator(":");
                var alternate = ParseTernaryExpression();
                return new TernaryExpr(expr, consequent, alternate);
            }
            return expr;
        }

        private object ParseLogicalOr()
        {
            var left = ParseLogicalAnd();
            while (CheckPunctuator("||"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseLogicalAnd();
                left = new LogicalExpr(op, left, right);
            }
            return left;
        }

        private object ParseLogicalAnd()
        {
            var left = ParseBitwiseOr();
            while (CheckPunctuator("&&"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseBitwiseOr();
                left = new LogicalExpr(op, left, right);
            }
            return left;
        }

        private object ParseBitwiseOr()
        {
            var left = ParseBitwiseXor();
            while (CheckPunctuator("|"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseBitwiseXor();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseBitwiseXor()
        {
            var left = ParseBitwiseAnd();
            while (CheckPunctuator("^"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseBitwiseAnd();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseBitwiseAnd()
        {
            var left = ParseEquality();
            while (CheckPunctuator("&"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseEquality();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseEquality()
        {
            var left = ParseRelational();
            while (CheckPunctuator("==") || CheckPunctuator("!=") || 
                   CheckPunctuator("===") || CheckPunctuator("!=="))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseRelational();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseRelational()
        {
            var left = ParseShift();
            while (CheckPunctuator("<") || CheckPunctuator(">") || 
                   CheckPunctuator("<=") || CheckPunctuator(">=") || 
                   CheckKeyword("instanceof") || CheckKeyword("in"))
            {
                if (CheckKeyword("instanceof"))
                {
                    Advance(); // consume instanceof
                    var right = ParseShift();
                    left = new InstanceofExpr(left, right);
                }
                else if (CheckKeyword("in"))
                {
                    Advance(); // consume in
                    var right = ParseShift();
                    left = new InExpr(left, right);
                }
                else
                {
                    var op = (Advance() as JsPunctuatorToken).Punctuator;
                    var right = ParseShift();
                    left = new BinaryExpr(op, left, right);
                }
            }
            return left;
        }

        private object ParseShift()
        {
            var left = ParseAdditive();
            while (CheckPunctuator("<<") || CheckPunctuator(">>") || CheckPunctuator(">>>"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseAdditive();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (CheckPunctuator("+") || CheckPunctuator("-"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseMultiplicative();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseMultiplicative()
        {
            var left = ParseUnary();
            while (CheckPunctuator("*") || CheckPunctuator("/") || CheckPunctuator("%"))
            {
                var op = (Advance() as JsPunctuatorToken).Punctuator;
                var right = ParseUnary();
                left = new BinaryExpr(op, left, right);
            }
            return left;
        }

        private object ParseUnary()
        {
            // Prefix unary operators (!, ~, +, -)
            if (CheckPunctuator("!"))
            {
                Advance();
                var operand = ParseUnary();
                return new UnaryExpr("!", operand, true);
            }
            
            if (CheckPunctuator("~"))
            {
                Advance();
                var operand = ParseUnary();
                return new UnaryExpr("~", operand, true);
            }
            
            if (CheckPunctuator("+"))
            {
                Advance();
                var operand = ParseUnary();
                return new UnaryExpr("+", operand, true);
            }
            
            if (CheckPunctuator("-"))
            {
                Advance();
                var operand = ParseUnary();
                return new UnaryExpr("-", operand, true);
            }
            
            // Keyword unary operators (typeof, void, delete)
            if (CheckKeyword("typeof"))
            {
                Advance();
                var operand = ParseUnary();
                return new TypeofExpr(operand);
            }
            
            if (CheckKeyword("void"))
            {
                Advance();
                var operand = ParseUnary();
                return new VoidExpr(operand);
            }
            
            if (CheckKeyword("delete"))
            {
                Advance();
                var operand = ParseUnary();
                return new DeleteExpr(operand);
            }
            
            // Prefix ++ and --
            if (CheckPunctuator("++"))
            {
                Advance();
                var operand = ParsePostfix();
                return new UpdateExpr("++", operand, true);
            }
            
            if (CheckPunctuator("--"))
            {
                Advance();
                var operand = ParsePostfix();
                return new UpdateExpr("--", operand, true);
            }
            
            // No prefix operator, parse postfix
            return ParsePostfix();
        }

        private object ParsePostfix()
        {
            var expr = ParseCallMember();
            
            // Check for postfix ++ or -- (no line terminator between expression and operator)
            if (CheckPunctuator("++") || CheckPunctuator("--"))
            {
                var endExprToken = Previous();
                var currentToken = Peek();
                
                // Only treat as postfix if no line terminator between expression and operator
                if (!(endExprToken is JsLineTerminatorToken) && endExprToken.Line == currentToken.Line)
                {
                    var op = (Advance() as JsPunctuatorToken).Punctuator;
                    expr = new UpdateExpr(op, expr, false);
                }
            }
            
            return expr;
        }

        private object ParseCallMember()
        {
            var expr = ParsePrimary();
            while (true)
            {
                if (CheckPunctuator("."))
                {
                    Advance(); // consume .
                    if (!CheckIdentifier())
                        throw new JsParserException("Expected identifier after '.'", Peek().Line, Peek().Column);
                    
                    var idToken = Advance() as JsIdentifierToken;
                    var property = new Identifier(idToken.Name);
                    expr = new MemberExpr(expr, property, false);
                }
                else if (CheckPunctuator("["))
                {
                    Advance(); // consume [
                    var property = ParseExpression();
                    ExpectPunctuator("]");
                    expr = new MemberExpr(expr, property, true);
                }
                else if (CheckPunctuator("("))
                {
                    Advance(); // consume (
                    var args = new List<object>();
                    
                    if (!CheckPunctuator(")"))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                        } while (MatchPunctuator(","));
                    }
                    
                    ExpectPunctuator(")");
                    expr = new CallExpr(expr, args);
                }
                else
                {
                    break;
                }
            }
            return expr;
        }

        private object ParsePrimary()
        {
            var token = Peek();

            // this
            if (CheckKeyword("this"))
            {
                Advance();
                return new ThisExpr();
            }

            // Boolean literals
            if (CheckKeyword("true"))
            {
                Advance();
                return new BoolLiteral(true);
            }
            if (CheckKeyword("false"))
            {
                Advance();
                return new BoolLiteral(false);
            }

            // null
            if (CheckKeyword("null"))
            {
                Advance();
                return new NullLiteral();
            }

            // Identifier
            if (token is JsIdentifierToken idToken)
            {
                Advance();
                return new Identifier(idToken.Name);
            }

            // Number literal
            if (token is JsNumberToken numToken)
            {
                Advance();
                return new NumberLiteral(numToken.Value);
            }

            // String literal
            if (token is JsStringToken strToken)
            {
                Advance();
                return new StringLiteral(strToken.Value);
            }

            // Regex literal
            if (token is JsRegexLiteralToken regexToken)
            {
                Advance();
                return new RegexLiteral(regexToken.Pattern, regexToken.Flags);
            }

            // Function expression, and also bro fuck this file
            if (CheckKeyword("function"))
            {
                return ParseFunctionExpression();
            }

            // Grouping expression
            if (CheckPunctuator("("))
            {
                Advance(); // consume (
                var expr = ParseExpression();
                ExpectPunctuator(")");
                return expr;
            }

            // Array literal
            if (CheckPunctuator("["))
            {
                return ParseArrayExpression();
            }

            // Object literal
            if (CheckPunctuator("{"))
            {
                return ParseObjectExpression();
            }

            throw new JsParserException($"Unexpected token {token} in primary expression", token.Line, token.Column);
        }
        #endregion

        #region Primary Expression Parsing
        private object ParseFunctionExpression()
        {
            ExpectKeyword("function");
            Identifier name = null;
            
            // Optional function name
            if (CheckIdentifier())
            {
                var nameToken = Advance() as JsIdentifierToken;
                name = new Identifier(nameToken.Name);
            }
            
            ExpectPunctuator("(");
            var parameters = new List<Identifier>();
            
            if (!CheckPunctuator(")"))
            {
                do
                {
                    if (!CheckIdentifier())
                        throw new JsParserException("Expected parameter name", Peek().Line, Peek().Column);
                    
                    var paramToken = Advance() as JsIdentifierToken;
                    parameters.Add(new Identifier(paramToken.Name));
                } while (MatchPunctuator(","));
            }
            
            ExpectPunctuator(")");
            var body = ParseBlockStatement();
            
            return new FunctionExpr(name, parameters, body);
        }

        private object ParseArrayExpression()
        {
            ExpectPunctuator("[");
            var elements = new List<object>();
            
            while (!CheckPunctuator("]"))
            {
                if (CheckPunctuator(","))
                {
                    // Array hole
                    elements.Add(null);
                    Advance();
                }
                else
                {
                    var elem = ParseExpression();
                    elements.Add(elem);
                    
                    if (CheckPunctuator(","))
                    {
                        Advance();
                        // Trailing comma check
                        if (CheckPunctuator("]"))
                            break;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            
            ExpectPunctuator("]");
            return new ArrayExpr(elements);
        }

        private object ParseObjectExpression()
        {
            ExpectPunctuator("{");
            var properties = new List<PropertyExpr>();
            
            while (!CheckPunctuator("}"))
            {
                // Parse property key
                object key;
                if (CheckIdentifier())
                {
                    var idToken = Advance() as JsIdentifierToken;
                    key = new Identifier(idToken.Name);
                }
                else if (CheckStringLiteral())
                {
                    var strToken = Advance() as JsStringToken;
                    key = new StringLiteral(strToken.Value);
                }
                else if (CheckNumberLiteral())
                {
                    var numToken = Advance() as JsNumberToken;
                    key = new NumberLiteral(numToken.Value);
                }
                else
                {
                    throw new JsParserException("Expected property key in object literal", Peek().Line, Peek().Column);
                }
                
                ExpectPunctuator(":");
                var value = ParseExpression();
                properties.Add(new PropertyExpr(key, value));
                
                if (CheckPunctuator(","))
                {
                    Advance();
                    // Trailing comma check
                    if (CheckPunctuator("}"))
                        break;
                }
                else
                {
                    break;
                }
            }
            
            ExpectPunctuator("}");
            return new ObjectExpr(properties);
        }
        #endregion

        // if you ever feel like working 24/7 on a javascript engine from scratch making the parser trust me you dont wanna be a developer attempting this "DO NOT TRY THIS AT HOME UNLESS YOU ARE A NERD" unless you want your javascript engine to explode mid through
    }
}
