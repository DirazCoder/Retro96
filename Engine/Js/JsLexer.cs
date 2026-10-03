using System;
using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Js
{
    // Token type definitions for JS 1.1 lexer
    public abstract record JsToken(int Position, int Line, int Column);

    public record JsNumberToken(double Value, int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsStringToken(string Value, int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsIdentifierToken(string Name, int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsKeywordToken(string Keyword, int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsPunctuatorToken(string Punctuator, int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsRegexLiteralToken(string Pattern, string Flags, int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsLineTerminatorToken(int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public record JsEofToken(int Position, int Line, int Column) : JsToken(Position, Line, Column);

    public class JsLexerException : Exception
    {
        public JsLexerException(string message) : base(message) { }
    }

    public class JsLexer
    {
        private readonly string _source;
        private int _pos;
        private int _line;
        private int _column;
        private JsToken? _previousToken;

        // JS 1.1 reserved keywords
        private static readonly HashSet<string> _keywords = new HashSet<string>
        {
            "abstract", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
            "continue", "debugger", "default", "delete", "do", "double", "else", "enum", "export",
            "extends", "false", "final", "finally", "float", "for", "function", "goto", "if",
            "implements", "import", "in", "instanceof", "int", "interface", "long", "native", "new",
            "null", "package", "private", "protected", "public", "return", "short", "static", "super",
            "switch", "synchronized", "this", "throw", "throws", "transient", "true", "try",
            "typeof", "var", "void", "volatile", "while", "with"
        };

        // Punctuators ordered by length descending to match longest first
        private static readonly string[] _punctuators =
        {
            "===", "!==", "==", "!=", ">=", "<=", "=>", "++", "--", "&&", "||", "<<", ">>", ">>>",
            "+", "-", "*", "/", "%", "=", "!", "~", "&", "|", "^", "<", ">", "?", ":", ".",
            "(", ")", "[", "]", "{", "}", ",", ";", "..."
        };

        public JsLexer(string source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _pos = 0;
            _line = 1;
            _column = 1;
            _previousToken = null;
        }

        public IEnumerable<JsToken> Tokenize()
        {
            while (_pos < _source.Length)
            {
                char current = _source[_pos];

                // Handle line terminators first
                if (current == '\n' || current == '\r')
                {
                    var ltToken = ParseLineTerminator();
                    var asiSemicolon = HandleAsiBeforeLineTerminator(ltToken);
                    if (asiSemicolon != null)
                    {
                        yield return asiSemicolon;
                        _previousToken = asiSemicolon;
                    }
                    yield return ltToken;
                    _previousToken = ltToken;
                    continue;
                }

                // Skip non-line terminator whitespace
                if (char.IsWhiteSpace(current))
                {
                    _pos++;
                    _column++;
                    continue;
                }

                // Skip comments (no token emitted for comments)
                ParseComment();
                if (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]) && 
                    _source[_pos] != '\n' && _source[_pos] != '\r')
                {
                    continue;
                }

                // Parse token types in priority order
                JsToken? token = null;

                token = ParseStringLiteral();
                if (token != null)
                {
                    yield return token;
                    _previousToken = token;
                    continue;
                }

                token = ParseNumberLiteral();
                if (token != null)
                {
                    yield return token;
                    _previousToken = token;
                    continue;
                }

                token = ParseIdentifierOrKeyword();
                if (token != null)
                {
                    yield return token;
                    _previousToken = token;
                    continue;
                }

                token = ParsePunctuator();
                if (token != null)
                {
                    yield return token;
                    _previousToken = token;
                    continue;
                }

                token = ParseRegexLiteral();
                if (token != null)
                {
                    yield return token;
                    _previousToken = token;
                    continue;
                }

                throw new JsLexerException($"Unexpected character '{current}' at line {_line}, column {_column}");
            }

            // Handle ASI at end of file
            var eofSemicolon = HandleAsiAtEof();
            if (eofSemicolon != null)
            {
                yield return eofSemicolon;
            }

            yield return new JsEofToken(_pos, _line, _column);
        }

        private JsLineTerminatorToken ParseLineTerminator()
        {
            int startPos = _pos;
            int startLine = _line;
            int startCol = _column;
            char current = _source[_pos];

            if (current == '\r' && _pos + 1 < _source.Length && _source[_pos + 1] == '\n')
            {
                _pos += 2;
                _column = 1;
                _line++;
            }
            else
            {
                _pos++;
                _column = 1;
                _line++;
            }

            return new JsLineTerminatorToken(startPos, startLine, startCol);
        }

        private JsToken? HandleAsiBeforeLineTerminator(JsLineTerminatorToken lt)
        {
            if (_previousToken == null || _previousToken is JsLineTerminatorToken)
                return null;

            // Check if previous token can end a statement
            bool canEndStatement = _previousToken switch
            {
                JsIdentifierToken => true,
                JsNumberToken => true,
                JsStringToken => true,
                JsRegexLiteralToken => true,
                JsKeywordToken kw => kw.Keyword is "true" or "false" or "null" or "this",
                JsPunctuatorToken pt => pt.Punctuator is ")" or "]" or "}" or "++" or "--",
                _ => false
            };

            if (!canEndStatement)
                return null;

            // Check if next token can continue the current statement
            int lookAhead = _pos;
            while (lookAhead < _source.Length && char.IsWhiteSpace(_source[lookAhead]))
            {
                if (_source[lookAhead] == '\n' || _source[lookAhead] == '\r')
                    break;
                lookAhead++;
            }

            if (lookAhead >= _source.Length)
                return new JsPunctuatorToken(";", lt.Position, lt.Line, lt.Column);

            char nextChar = _source[lookAhead];
            bool canContinueStatement = nextChar switch
            {
                '+' or '-' or '*' or '/' or '%' or '=' or '!' or '~' or '&' or '|' or '^' or '<' or '>' or '?' or ':' or '.' or ',' or ';' => true,
                '(' or '[' or '{' or ')' or ']' or '}' => true,
                _ when char.IsLetter(nextChar) || nextChar == '_' || nextChar == '$' => true,
                _ => false
            };

            if (!canContinueStatement)
                return new JsPunctuatorToken(";", lt.Position, lt.Line, lt.Column);

            return null;
        }

        private JsToken? HandleAsiAtEof()
        {
            if (_previousToken == null || _previousToken is JsEofToken)
                return null;

            bool needsSemicolon = _previousToken switch
            {
                JsPunctuatorToken pt => pt.Punctuator is ";" or "}",
                _ => true
            };

            return needsSemicolon ? new JsPunctuatorToken(";", _pos, _line, _column) : null;
        }

        private void ParseComment()
        {
            if (_pos >= _source.Length)
                return;

            char current = _source[_pos];

            // Single-line comment
            if (current == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n' && _source[_pos] != '\r')
                {
                    _pos++;
                    _column++;
                }
                return;
            }

            // Multi-line comment
            if (current == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '*')
            {
                _pos += 2;
                _column += 2;

                while (_pos < _source.Length)
                {
                    if (_source[_pos] == '*' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
                    {
                        _pos += 2;
                        _column += 2;
                        return;
                    }

                    if (_source[_pos] == '\n' || _source[_pos] == '\r')
                    {
                        if (_source[_pos] == '\r' && _pos + 1 < _source.Length && _source[_pos + 1] == '\n')
                        {
                            _pos += 2;
                            _column = 1;
                            _line++;
                        }
                        else
                        {
                            _pos++;
                            _column = 1;
                            _line++;
                        }
                    }
                    else
                    {
                        _pos++;
                        _column++;
                    }
                }

                throw new JsLexerException("Unterminated multi-line comment");
            }
        }

        private JsToken? ParseStringLiteral()
        {
            if (_pos >= _source.Length)
                return null;

            char quote = _source[_pos];
            if (quote != '"' && quote != '\'')
                return null;

            int startPos = _pos;
            int startLine = _line;
            int startCol = _column;
            _pos++;
            _column++;

            var sb = new StringBuilder();
            while (_pos < _source.Length)
            {
                char c = _source[_pos];
                if (c == quote)
                {
                    _pos++;
                    _column++;
                    return new JsStringToken(sb.ToString(), startPos, startLine, startCol);
                }

                if (c == '\\')
                {
                    _pos++;
                    _column++;
                    if (_pos >= _source.Length)
                        throw new JsLexerException("Unterminated string escape sequence");

                    char esc = _source[_pos];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case 'u':
                            if (_pos + 4 >= _source.Length)
                                throw new JsLexerException("Invalid unicode escape sequence");
                            string hex = _source.Substring(_pos + 1, 4);
                            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                throw new JsLexerException($"Invalid unicode escape \\u{hex}");
                            sb.Append((char)code);
                            _pos += 4;
                            _column += 4;
                            break;
                        case 'x':
                            if (_pos + 2 >= _source.Length)
                                throw new JsLexerException("Invalid hex escape sequence");
                            string hex2 = _source.Substring(_pos + 1, 2);
                            if (!int.TryParse(hex2, System.Globalization.NumberStyles.HexNumber, null, out int code2))
                                throw new JsLexerException($"Invalid hex escape \\x{hex2}");
                            sb.Append((char)code2);
                            _pos += 2;
                            _column += 2;
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                    _pos++;
                    _column++;
                }
                else
                {
                    sb.Append(c);
                    _pos++;
                    _column++;
                }
            }

            throw new JsLexerException("Unterminated string literal");
        }

        private JsToken? ParseNumberLiteral()
        {
            if (_pos >= _source.Length)
                return null;

            char current = _source[_pos];
            if (!char.IsDigit(current) && current != '.')
                return null;

            if (current == '.' && (_pos + 1 >= _source.Length || !char.IsDigit(_source[_pos + 1])))
                return null;

            int startPos = _pos;
            int startLine = _line;
            int startCol = _column;
            var sb = new StringBuilder();

            // Hex number (0xNNN)
            if (current == '0' && _pos + 1 < _source.Length && (_source[_pos + 1] == 'x' || _source[_pos + 1] == 'X'))
            {
                sb.Append(current);
                _pos++;
                _column++;
                sb.Append(_source[_pos]);
                _pos++;
                _column++;

                while (_pos < _source.Length && IsHexDigit(_source[_pos]))
                {
                    sb.Append(_source[_pos]);
                    _pos++;
                    _column++;
                }

                // sb is "0x1F" etc — strip the 0x prefix for parsing
                string hexDigits = sb.ToString().Substring(2);
                if (hexDigits.Length == 0)
                    throw new JsLexerException($"Invalid hex number {sb}");
                try
                {
                    double hexVal = Convert.ToInt64(hexDigits, 16);
                    return new JsNumberToken(hexVal, startPos, startLine, startCol);
                }
                catch
                {
                    throw new JsLexerException($"Invalid hex number {sb}");
                }
            }

            // Octal number (0NNN, JS 1.1 compatible)
            if (current == '0' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
            {
                while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                {
                    sb.Append(_source[_pos]);
                    _pos++;
                    _column++;
                }

                try
                {
                    double octVal = Convert.ToInt32(sb.ToString(), 8);
                    return new JsNumberToken(octVal, startPos, startLine, startCol);
                }
                catch
                {
                    throw new JsLexerException($"Invalid octal number {sb}");
                }
            }

            // Decimal/float number
            while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
            {
                sb.Append(_source[_pos]);
                _pos++;
                _column++;
            }

            // Exponent part
            if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
            {
                sb.Append(_source[_pos]);
                _pos++;
                _column++;

                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                {
                    sb.Append(_source[_pos]);
                    _pos++;
                    _column++;
                }

                while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                {
                    sb.Append(_source[_pos]);
                    _pos++;
                    _column++;
                }
            }

            if (!double.TryParse(sb.ToString(), out double numVal))
                throw new JsLexerException($"Invalid number {sb}");
            return new JsNumberToken(numVal, startPos, startLine, startCol);
        }

        private static bool IsHexDigit(char c)
        {
            return char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private JsToken? ParseIdentifierOrKeyword()
        {
            if (_pos >= _source.Length)
                return null;

            char current = _source[_pos];
            if (!char.IsLetter(current) && current != '_' && current != '$')
                return null;

            int startPos = _pos;
            int startLine = _line;
            int startCol = _column;
            var sb = new StringBuilder();

            while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_' || _source[_pos] == '$'))
            {
                sb.Append(_source[_pos]);
                _pos++;
                _column++;
            }

            string name = sb.ToString();
            return _keywords.Contains(name)
                ? new JsKeywordToken(name, startPos, startLine, startCol)
                : new JsIdentifierToken(name, startPos, startLine, startCol);
        }

        private JsToken? ParsePunctuator()
        {
            if (_pos >= _source.Length)
                return null;

            string substr = _source.Substring(_pos);
            foreach (var punct in _punctuators)
            {
                if (substr.StartsWith(punct))
                {
                    int startPos = _pos;
                    int startLine = _line;
                    int startCol = _column;
                    _pos += punct.Length;
                    _column += punct.Length;
                    return new JsPunctuatorToken(punct, startPos, startLine, startCol);
                }
            }

            return null;
        }

        private JsToken? ParseRegexLiteral()
        {
            if (_pos >= _source.Length)
                return null;

            char current = _source[_pos];
            if (current != '/')
                return null;

            if (!IsRegexContext())
                return null;

            int startPos = _pos;
            int startLine = _line;
            int startCol = _column;
            _pos++;
            _column++;

            var patternSb = new StringBuilder();
            while (_pos < _source.Length)
            {
                char c = _source[_pos];
                if (c == '/')
                {
                    _pos++;
                    _column++;
                    break;
                }

                if (c == '\\')
                {
                    patternSb.Append(c);
                    _pos++;
                    _column++;
                    if (_pos >= _source.Length)
                        throw new JsLexerException("Unterminated regex literal");
                    patternSb.Append(_source[_pos]);
                    _pos++;
                    _column++;
                }
                else
                {
                    patternSb.Append(c);
                    _pos++;
                    _column++;
                }
            }

            // Parse regex flags (JS 1.1 supports g, i, m)
            var flagsSb = new StringBuilder();
            while (_pos < _source.Length && char.IsLetter(_source[_pos]))
            {
                char f = _source[_pos];
                if (f == 'g' || f == 'i' || f == 'm')
                {
                    flagsSb.Append(f);
                    _pos++;
                    _column++;
                }
                else
                {
                    break;
                }
            }

            return new JsRegexLiteralToken(patternSb.ToString(), flagsSb.ToString(), startPos, startLine, startCol);
        }

        private bool IsRegexContext()
        {
            if (_previousToken == null)
                return true;

            return _previousToken switch
            {
                JsKeywordToken => true,
                JsPunctuatorToken pt => pt.Punctuator is "(" or "[" or "{" or "," or ";" or "++" or "--" or "!" or "~" or "+" or "-" or "*" or "/" or "%" or "=" or "==" or "!=" or "===" or "!==" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "<<" or ">>" or ">>>" or "&" or "|" or "^" or "?" or ":" or "." or "..." or ")" or "]" or "}",
                _ => false
            };
        }
    }
}