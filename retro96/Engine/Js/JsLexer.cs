using System;
using System.Collections.Generic;
using System.Text;

namespace Retro96.Engine.Js;

// ─────────────────────────────────────────────────────────────────────────────
// Tokens.  Every token carries AfterNewline — true when at least one line
// terminator separated it from the previous token — which is what the
// parser's automatic-semicolon-insertion and restricted productions use.
// ─────────────────────────────────────────────────────────────────────────────

public abstract record JsToken(int Position, int Line, int Column, bool AfterNewline);
public record JsNumberToken(double Value, int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);
public record JsStringToken(string Value, int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);
public record JsIdentifierToken(string Name, int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);
public record JsKeywordToken(string Keyword, int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);
public record JsPunctuatorToken(string Punctuator, int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);
public record JsRegexLiteralToken(string Pattern, string Flags, int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);
public record JsEofToken(int Position, int Line, int Column, bool AfterNewline) : JsToken(Position, Line, Column, AfterNewline);

public class JsLexerException : Exception
{
    public JsLexerException(string message) : base(message) { }
}

/// <summary>
/// JavaScript 1.1/1.2 lexer.
///
/// Line terminators are skipped as whitespace but recorded, and the flag
/// surfaces on the following token so the PARSER performs semicolon
/// insertion (lexer-inserted semicolons break return-with-newline and
/// postfix ++ handling — the classic restricted productions).
///
/// Punctuators include every compound assignment of the era (+=, <<=, …).
/// Hex and octal literals accumulate in a double — the old long
/// accumulator wrapped, so 0xFFFFFFFF lexed as -1.
/// </summary>
public class JsLexer
{
    private readonly string _source;
    private int _pos;
    private int _line;
    private int _column;
    private JsToken? _previousToken;
    private bool _pendingNewline;

    /// <summary>JS 1.1 reserved words (the ES1-era set).</summary>
    private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal)
    {
        "abstract", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "debugger", "default", "delete", "do", "double", "else", "enum", "export",
        "extends", "false", "final", "finally", "float", "for", "function", "goto", "if",
        "implements", "import", "in", "instanceof", "int", "interface", "long", "native", "new",
        "null", "package", "private", "protected", "public", "return", "short", "static", "super",
        "switch", "synchronized", "this", "throw", "throws", "transient", "true", "try",
        "typeof", "var", "void", "volatile", "while", "with"
    };

    public JsLexer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public IReadOnlyList<JsToken> Tokenize()
    {
        var tokens = new List<JsToken>();
        foreach (var t in TokenizeLazy())
            tokens.Add(t);
        return tokens;
    }

    private IEnumerable<JsToken> TokenizeLazy()
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            if (_pos >= _source.Length)
            {
                var eof = new JsEofToken(_pos, _line, _column, _pendingNewline);
                yield return eof;
                yield break;
            }

            // HTML comment wrappers inside <script> raw text — the era's
            // scripts were wrapped in <!-- ... //--> and JavaScript itself
            // treats "<!--" as a line comment (and "-->" at line start or
            // source start).  Without this, every wrapped script — which
            // is most of them — failed to parse and silently died.
            if (_source[_pos] == '<' && _pos + 3 < _source.Length &&
                _source[_pos + 1] == '!' && _source[_pos + 2] == '-' && _source[_pos + 3] == '-')
            {
                ConsumeToLineEnd();
                continue;
            }
            if (_source[_pos] == '-' && _pos + 2 < _source.Length &&
                _source[_pos + 1] == '-' && _source[_pos + 2] == '>' &&
                (_pendingNewline || _previousToken == null))
            {
                ConsumeToLineEnd();
                continue;
            }

            bool afterNewline = _pendingNewline;
            _pendingNewline = false;

            int startPos = _pos, startLine = _line, startCol = _column;

            char current = _source[_pos];

            var token = LexOne(startPos, startLine, startCol, afterNewline, current);
            _previousToken = token;
            yield return token;
        }
    }

    /// <summary>Consumes the rest of the current line (for HTML-style
    /// script comments).</summary>
    private void ConsumeToLineEnd()
    {
        while (_pos < _source.Length && _source[_pos] is not ('\n' or '\r'))
        {
            _pos++;
            _column++;
        }
    }

    private JsToken LexOne(int startPos, int startLine, int startCol, bool afterNewline, char current)
    {
        if (current is '"' or '\'')
            return Reset(LexStringLiteral());

        if (IsDigit(current) || (current == '.' && _pos + 1 < _source.Length && IsDigit(_source[_pos + 1])))
            return Reset(LexNumberLiteral());

        if (char.IsLetter(current) || current is '_' or '$')
            return Reset(LexIdentifierOrKeyword());

        // Regex must be checked BEFORE the punctuator — '/' would otherwise
        // always win and regex literals could never start
        if (current == '/' && _pos + 1 < _source.Length &&
            _source[_pos + 1] != '/' && _source[_pos + 1] != '*' &&
            IsRegexContext())
            return Reset(LexRegexLiteral());

        var punct = LexPunctuator();
        if (punct != null)
            return Reset(punct);

        throw new JsLexerException($"Unexpected character '{current}' at line {_line}, column {_column}");

        JsToken Reset(JsToken t) => t with
        {
            Position = startPos,
            Line = startLine,
            Column = startCol,
            AfterNewline = afterNewline
        };
    }

    // ── Whitespace and comments ───────────────────────────────────────────

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (c is '\n' or '\r' or '\u2028' or '\u2029')
            {
                _pendingNewline = true;
                if (c == '\r' && _pos + 1 < _source.Length && _source[_pos + 1] == '\n')
                {
                    _pos += 2;
                }
                else
                {
                    _pos++;
                }
                _line++;
                _column = 1;
                continue;
            }

            if (c is ' ' or '\t' or '\f' or '\v' or '\u00A0' or '\uFEFF')
            {
                _pos++;
                _column++;
                continue;
            }

            // Comments
            if (c == '/' && _pos + 1 < _source.Length)
            {
                char next = _source[_pos + 1];

                if (next == '/')   // line comment
                {
                    while (_pos < _source.Length && _source[_pos] is not ('\n' or '\r'))
                    {
                        _pos++;
                        _column++;
                    }
                    continue;   // newline handled above on next loop
                }

                if (next == '*')   // block comment
                {
                    _pos += 2;
                    _column += 2;
                    bool terminated = false;
                    while (_pos < _source.Length)
                    {
                        char cc = _source[_pos];
                        if (cc == '*' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
                        {
                            _pos += 2;
                            _column += 2;
                            terminated = true;
                            break;
                        }
                        if (cc is '\n' or '\r')
                        {
                            _pendingNewline = true;
                            if (cc == '\r' && _pos + 1 < _source.Length && _source[_pos + 1] == '\n')
                            {
                                _pos += 2;
                            }
                            else
                            {
                                _pos++;
                            }
                            _line++;
                            _column = 1;
                        }
                        else
                        {
                            _pos++;
                            _column++;
                        }
                    }
                    if (!terminated)
                        throw new JsLexerException("Unterminated multi-line comment");
                    continue;
                }
            }

            break;   // real token ahead
        }
    }

    // ── Literals ──────────────────────────────────────────────────────────

    private JsStringToken LexStringLiteral()
    {
        char quote = _source[_pos];
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
                return new JsStringToken(sb.ToString(), 0, 0, 0, false);
            }

            if (c is '\n' or '\r')
                throw new JsLexerException("Unterminated string literal (newline before quote)");

            if (c == '\\')
            {
                _pos++;
                _column++;
                if (_pos >= _source.Length)
                    throw new JsLexerException("Unterminated string escape sequence");

                char esc = _source[_pos];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); _pos++; _column++; break;
                    case 'r': sb.Append('\r'); _pos++; _column++; break;
                    case 't': sb.Append('\t'); _pos++; _column++; break;
                    case 'b': sb.Append('\b'); _pos++; _column++; break;
                    case 'f': sb.Append('\f'); _pos++; _column++; break;
                    case '0': sb.Append('\0'); _pos++; _column++; break;
                    case '\\': sb.Append('\\'); _pos++; _column++; break;
                    case '\'': sb.Append('\''); _pos++; _column++; break;
                    case '"': sb.Append('"'); _pos++; _column++; break;
                    case '\n':   // line continuation — joins the lines, no ASI break
                        _pos++; _line++; _column = 1;
                        break;
                    case '\r':
                        _pos++;
                        if (_pos < _source.Length && _source[_pos] == '\n') _pos++;
                        _line++; _column = 1;
                        break;
                    case 'u':
                        {
                            if (_pos + 4 >= _source.Length)
                                throw new JsLexerException("Invalid unicode escape sequence");
                            string hex = _source.Substring(_pos + 1, 4);
                            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out int code))
                                throw new JsLexerException($"Invalid unicode escape \\u{hex}");
                            sb.Append((char)code);
                            _pos += 5;
                            _column += 5;
                            break;
                        }
                    case 'x':
                        {
                            if (_pos + 2 >= _source.Length)
                                throw new JsLexerException("Invalid hex escape sequence");
                            string hex2 = _source.Substring(_pos + 1, 2);
                            if (!int.TryParse(hex2, System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out int code2))
                                throw new JsLexerException($"Invalid hex escape \\x{hex2}");
                            sb.Append((char)code2);
                            _pos += 3;
                            _column += 3;
                            break;
                        }
                    case >= '1' and <= '7':
                        {
                            // Octal escape (\1..\7, up to three digits) — valid
                            // era JS; the old code fell through to the unknown
                            // escape branch and emitted the bare digit.
                            int val = esc - '0';
                            int digits = 1;
                            while (digits < 3 && _pos + 1 < _source.Length &&
                                   _source[_pos + 1] >= '0' && _source[_pos + 1] <= '7')
                            {
                                _pos++;
                                _column++;
                                val = val * 8 + (_source[_pos] - '0');
                                digits++;
                            }
                            sb.Append((char)val);
                            _pos++;
                            _column++;
                            break;
                        }
                    default:
                        // Unknown escape — the character itself (\q → q)
                        sb.Append(esc);
                        _pos++;
                        _column++;
                        break;
                }
                continue;
            }

            sb.Append(c);
            _pos++;
            _column++;
        }

        throw new JsLexerException("Unterminated string literal");
    }

    private JsNumberToken LexNumberLiteral()
    {
        int start = _pos;

        // Hex — accumulated in a double (exact to 2^53); the old long
        // accumulator made 0xFFFFFFFF lex as -1.
        if (_source[_pos] == '0' && _pos + 1 < _source.Length &&
            (_source[_pos + 1] is 'x' or 'X'))
        {
            _pos += 2;
            _column += 2;
            double val = 0;
            int digits = 0;
            while (_pos < _source.Length && IsHexDigit(_source[_pos]))
            {
                val = val * 16 + HexValue(_source[_pos]);
                digits++;
                _pos++;
                _column++;
            }
            if (digits == 0)
                throw new JsLexerException("Invalid hex number");
            return new JsNumberToken(val, 0, 0, 0, false);
        }

        bool sawDot = false, sawExp = false;
        bool looksOctal = _source[_pos] == '0';

        // Integer part
        while (_pos < _source.Length && IsDigit(_source[_pos]))
        {
            if (_source[_pos] is '8' or '9') looksOctal = false;
            _pos++;
            _column++;
        }

        // Fraction
        if (_pos < _source.Length && _source[_pos] == '.')
        {
            sawDot = true;
            _pos++;
            _column++;
            while (_pos < _source.Length && IsDigit(_source[_pos]))
            {
                if (_source[_pos] is '8' or '9') looksOctal = false;
                _pos++;
                _column++;
            }
        }

        // Exponent
        if (_pos < _source.Length && _source[_pos] is 'e' or 'E')
        {
            int save = _pos, saveCol = _column;
            _pos++;
            _column++;
            if (_pos < _source.Length && _source[_pos] is '+' or '-')
            {
                _pos++;
                _column++;
            }
            if (_pos < _source.Length && IsDigit(_source[_pos]))
            {
                sawExp = true;
                while (_pos < _source.Length && IsDigit(_source[_pos]))
                {
                    _pos++;
                    _column++;
                }
            }
            else
            {
                _pos = save;
                _column = saveCol;   // not an exponent — backtrack
            }
        }

        string text = _source[start.._pos];

        // Octal (0NN, no 8/9, not followed by dot/exp — those make it decimal)
        if (looksOctal && !sawDot && !sawExp && text.Length > 1)
        {
            double val = 0;
            foreach (char c in text[1..])
                val = val * 8 + (c - '0');
            return new JsNumberToken(val, 0, 0, 0, false);
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double numVal))
            throw new JsLexerException($"Invalid number {text}");
        return new JsNumberToken(numVal, 0, 0, 0, false);
    }

    private JsToken LexIdentifierOrKeyword()
    {
        int start = _pos;
        while (_pos < _source.Length &&
               (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] is '_' or '$'))
        {
            _pos++;
            _column++;
        }

        string name = _source[start.._pos];
        return _keywords.Contains(name)
            ? new JsKeywordToken(name, 0, 0, 0, false)
            : new JsIdentifierToken(name, 0, 0, 0, false);
    }

    private JsPunctuatorToken? LexPunctuator()
    {
        char c = _source[_pos];
        char c1 = _pos + 1 < _source.Length ? _source[_pos + 1] : '\0';
        char c2 = _pos + 2 < _source.Length ? _source[_pos + 2] : '\0';

        string? p = c switch
        {
            '=' when c1 == '=' && c2 == '=' => "===",
            '=' when c1 == '=' => "==",
            '!' when c1 == '=' && c2 == '=' => "!==",
            '!' when c1 == '=' => "!=",
            '<' when c1 == '<' && c2 == '=' => "<<=",
            '<' when c1 == '<' => "<<",
            '<' when c1 == '=' => "<=",
            '>' when c1 == '>' && c2 == '>' && _pos + 3 < _source.Length && _source[_pos + 3] == '=' => ">>>=",
            '>' when c1 == '>' && c2 == '>' => ">>>",
            '>' when c1 == '>' && c2 == '=' => ">>=",
            '>' when c1 == '>' => ">>",
            '>' when c1 == '=' => ">=",
            '+' when c1 == '+' => "++",
            '+' when c1 == '=' => "+=",
            '-' when c1 == '-' => "--",
            '-' when c1 == '=' => "-=",
            '*' when c1 == '=' => "*=",
            '/' when c1 == '=' => "/=",
            '%' when c1 == '=' => "%=",
            '&' when c1 == '&' => "&&",
            '&' when c1 == '=' => "&=",
            '|' when c1 == '|' => "||",
            '|' when c1 == '=' => "|=",
            '^' when c1 == '=' => "^=",
            '?' when c1 == '.' => "?.",          // not JS1.1 — lex anyway
            '=' when c1 == '>' => "=>",          // not JS1.1 — lex anyway
            '.' when c1 == '.' && c2 == '.' => "...",
            '+' or '-' or '*' or '/' or '%' or '=' or '!' or '~' or '&' or '|' or '^'
                 or '<' or '>' or '?' or ':' or '.' or '(' or ')' or '[' or ']'
                 or '{' or '}' or ',' or ';' => c.ToString(),
            _ => null
        };

        if (p == null) return null;

        _pos += p.Length;
        _column += p.Length;
        return new JsPunctuatorToken(p, 0, 0, 0, false);
    }

    private JsRegexLiteralToken LexRegexLiteral()
    {
        _pos++;
        _column++;

        var patternSb = new StringBuilder();
        bool inClass = false;
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\n' || c == '\r')
                throw new JsLexerException("Unterminated regex literal");

            if (c == '/' && !inClass)
            {
                _pos++;
                _column++;
                break;
            }

            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;

            if (c == '\\')
            {
                patternSb.Append(c);
                _pos++;
                _column++;
                if (_pos >= _source.Length)
                    throw new JsLexerException("Unterminated regex literal");
                char next = _source[_pos];
                if (next is '\n' or '\r')
                    throw new JsLexerException("Unterminated regex literal");
                patternSb.Append(next);
                _pos++;
                _column++;
                continue;
            }

            patternSb.Append(c);
            _pos++;
            _column++;
        }

        var flagsSb = new StringBuilder();
        while (_pos < _source.Length && char.IsLetter(_source[_pos]))
        {
            char f = _source[_pos];
            if (f is 'g' or 'i' or 'm')
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

        return new JsRegexLiteralToken(patternSb.ToString(), flagsSb.ToString(), 0, 0, 0, false);
    }

    /// <summary>
    /// A '/' begins a regex literal only in operator position — after
    /// keywords, after binary operators and openers, and NOT after a value
    /// (identifier, number, string, ')', ']', '}') where it is division.
    /// </summary>
    private bool IsRegexContext()
    {
        if (_previousToken == null)
            return true;

        return _previousToken switch
        {
            // VALUE keywords are values — a following '/' is division.
            // `this / 2` / `true / 2` used to be mis-lexed as regex literals.
            JsKeywordToken { Keyword: "this" or "true" or "false" or "null" } => false,
            JsKeywordToken => true,   // return /rx/, typeof /rx/, etc.
            JsIdentifierToken or JsNumberToken or JsStringToken or JsRegexLiteralToken => false,
            JsPunctuatorToken pt => pt.Punctuator is
                "(" or "[" or "{" or "," or ";" or "!" or "~" or
                "+" or "-" or "*" or "/" or "%" or "=" or "==" or "!=" or
                "===" or "!==" or "<" or ">" or "<=" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "?" or ":" or
                "<<" or ">>" or ">>>" or "+=" or "-=" or "*=" or "/=" or "%=" or
                "<<=" or ">>=" or ">>>=" or "&=" or "|=" or "^=",
            _ => false
        };
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };
}