using System;
using System.Collections.Generic;
using System.Globalization;

namespace Retro96.Engine.Vbs;

/// <summary>
/// VBScript 5.0 lexer. Line-oriented: newline ends a statement (unless the
/// previous line ended with the "_" continuation char), ":" separates
/// statements on one line. Comments: ' anywhere, Rem at statement position.
/// Strings use "" as an escaped quote. Numbers: decimal, exponent, &amp;H hex,
/// &amp;O octal. Date literals in #...#.
/// </summary>
public sealed class VbsLexer
{
    private readonly string _s;
    private int _pos;
    private int _line = 1, _col = 1;
    private bool _lineHasToken;
    private bool _continuation;
    private readonly List<VbsToken> _tokens = new();

    public VbsLexer(string source) =>
        _s = source ?? throw new ArgumentNullException(nameof(source));

    public IReadOnlyList<VbsToken> Tokenize()
    {
        while (_pos < _s.Length)
        {
            char c = _s[_pos];

            if (c is '\r' or '\n')
            {
                bool crlf = c == '\r' && _pos + 1 < _s.Length && _s[_pos + 1] == '\n';
                _pos += crlf ? 2 : 1;
                _line++; _col = 1;
                if (_lineHasToken && !_continuation)
                    _tokens.Add(new VbsEndOfLineToken(_line, _col));
                _lineHasToken = false;
                _continuation = false;
                continue;
            }

            if (char.IsWhiteSpace(c)) { _pos++; _col++; continue; }

            if (c == '\'') { SkipToEol(); continue; }
            if (c == '"') { LexString(); continue; }
            if (c == '#') { LexDate(); continue; }
            if (c == '_' && !NextIsIdentifierChar()) { LexContinuation(); continue; }
            if (char.IsDigit(c) || (c == '.' && NextIsDigit())) { LexDecimal(); continue; }
            if (char.IsLetter(c)) { LexWord(); continue; }
            if (c == '&' && IsHexPrefix()) { LexRadix(16); continue; }
            if (c == '&' && IsOctalPrefix()) { LexRadix(8); continue; }

            LexPunct();
        }

        if (_lineHasToken && !_continuation)
            _tokens.Add(new VbsEndOfLineToken(_line, _col));
        _tokens.Add(new VbsEofToken(_line, _col));
        return _tokens;
    }

    private bool NextIsDigit() => _pos + 1 < _s.Length && char.IsDigit(_s[_pos + 1]);
    private bool NextIsIdentifierChar() =>
        _pos + 1 < _s.Length && (char.IsLetterOrDigit(_s[_pos + 1]) || _s[_pos + 1] == '_');

    private void SkipToEol()
    {
        while (_pos < _s.Length && _s[_pos] is not ('\n' or '\r')) { _pos++; _col++; }
    }

    /// <summary>"_" must be the last thing on the line (whitespace aside) — it
    /// continues the statement onto the next physical line.</summary>
    private void LexContinuation()
    {
        int j = _pos + 1;
        while (j < _s.Length && (_s[j] == ' ' || _s[j] == '\t')) j++;
        if (j < _s.Length && _s[j] is not ('\n' or '\r'))
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError, "Invalid character '_'", _line, _col);
        _col += j - _pos;
        _pos = j;
        _continuation = true;   // the upcoming newline must NOT emit an EOL token
    }

    private bool AtStatementStart =>
        _tokens.Count == 0 ||
        _tokens[^1] is VbsEndOfLineToken ||
        _tokens[^1] is VbsPunctToken { Text: ":" };

    private void LexWord()
    {
        int line = _line, col = _col, start = _pos;
        while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_'))
        { _pos++; _col++; }
        string word = _s[start.._pos];
        string upper = word.ToUpperInvariant();

        if (VbsKeywords.All.Contains(upper))
        {
            // Rem is a comment only at statement position.
            if (upper == "REM" && AtStatementStart) { SkipToEol(); return; }
            _tokens.Add(new VbsKeywordToken(upper, line, col));
        }
        else
        {
            _tokens.Add(new VbsIdentifierToken(word, line, col));
        }
        _lineHasToken = true;
    }

    private void LexString()
    {
        int line = _line, col = _col;
        _pos++; _col++;
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (_pos >= _s.Length || _s[_pos] is '\n' or '\r')
                throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError,
                    "Unterminated string constant", line, col);
            char c = _s[_pos];
            if (c == '"')
            {
                if (_pos + 1 < _s.Length && _s[_pos + 1] == '"')   // "" → escaped quote
                {
                    sb.Append('"');
                    _pos += 2; _col += 2;
                    continue;
                }
                _pos++; _col++;
                break;
            }
            sb.Append(c);
            _pos++; _col++;
        }
        _tokens.Add(new VbsLiteralToken(VbsVariant.Of(sb.ToString()), line, col));
        _lineHasToken = true;
    }

    private void LexDate()
    {
        int line = _line, col = _col;
        _pos++; _col++;
        int start = _pos;
        while (_pos < _s.Length && _s[_pos] != '#' && _s[_pos] is not ('\n' or '\r'))
        { _pos++; _col++; }
        if (_pos >= _s.Length || _s[_pos] != '#')
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError,
                "Unterminated date literal", line, col);
        string text = _s[start.._pos];
        _pos++; _col++;
        if (!VbsDateUtil.TryParseDate(text, out var dt))
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError,
                $"Invalid date literal '{text}'", line, col);
        _tokens.Add(new VbsLiteralToken(VbsVariant.Of(dt), line, col));
        _lineHasToken = true;
    }

    private bool IsHexPrefix() =>
        _pos + 2 < _s.Length && (_s[_pos + 1] is 'H' or 'h') &&
        _s[_pos + 2] is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private bool IsOctalPrefix() =>
        _pos + 2 < _s.Length && (_s[_pos + 1] is 'O' or 'o') && _s[_pos + 2] is >= '0' and <= '7';

    private void LexRadix(int radix)
    {
        int line = _line, col = _col;
        _pos += 2; _col += 2;   // skip &H / &O
        long value = 0;
        int digits = 0;
        while (_pos < _s.Length && IsRadixDigit(_s[_pos], radix))
        {
            int d = DigitValue(_s[_pos]);
            try { value = checked(value * radix + d); }
            catch (OverflowException)
            { throw new VbsRuntimeException(VbsErrorNumbers.Overflow, "Overflow"); }
            digits++; _pos++; _col++;
        }
        if (digits == 0)
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError, "Invalid number", line, col);
        _tokens.Add(new VbsLiteralToken(
            value is >= short.MinValue and <= short.MaxValue
                ? VbsVariant.Of((short)value)
                : VbsVariant.Of((int)value), line, col));
        _lineHasToken = true;
    }

    private static bool IsRadixDigit(char c, int radix) => DigitValue(c) < radix;
    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 99
    };

    private void LexDecimal()
    {
        int line = _line, col = _col, start = _pos;
        bool isFloat = false;

        while (_pos < _s.Length && char.IsDigit(_s[_pos])) { _pos++; _col++; }

        if (_pos < _s.Length && _s[_pos] == '.')
        {
            isFloat = true;
            _pos++; _col++;
            while (_pos < _s.Length && char.IsDigit(_s[_pos])) { _pos++; _col++; }
        }

        if (_pos < _s.Length && (_s[_pos] is 'e' or 'E'))
        {
            int save = _pos;
            _pos++; _col++;
            if (_pos < _s.Length && (_s[_pos] is '+' or '-')) { _pos++; _col++; }
            if (_pos < _s.Length && char.IsDigit(_s[_pos]))
            {
                isFloat = true;
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) { _pos++; _col++; }
            }
            else { _pos = save; _col -= _pos - save; }
        }

        string text = _s[start.._pos];
        VbsVariant value;
        if (isFloat)
        {
            value = VbsVariant.Of(double.Parse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture));
        }
        else if (long.TryParse(text, out long lv))
        {
            value = lv is >= short.MinValue and <= short.MaxValue
                ? VbsVariant.Of((short)lv)
                : lv is >= int.MinValue and <= int.MaxValue
                    ? VbsVariant.Of((int)lv)
                    : VbsVariant.Of((double)lv);
        }
        else
        {
            value = VbsVariant.Of(double.Parse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture));
        }
        _tokens.Add(new VbsLiteralToken(value, line, col));
        _lineHasToken = true;
    }

    private void LexPunct()
    {
        int line = _line, col = _col;
        char c = _s[_pos];
        char n = _pos + 1 < _s.Length ? _s[_pos + 1] : '\0';

        string text = c switch
        {
            '<' when n == '=' => "<=",
            '<' when n == '>' => "<>",
            '>' when n == '=' => ">=",
            '=' or '<' or '>' or '+' or '-' or '*' or '/' or '\\' or '^' or '&'
                or '(' or ')' or ',' or ':' or '.' => c.ToString(),
            _ => ""
        };

        if (text.Length == 0)
            throw new VbsSyntaxException(VbsErrorNumbers.SyntaxError,
                $"Invalid character '{c}'", line, col);

        _pos += text.Length;
        _col += text.Length;
        _tokens.Add(new VbsPunctToken(text, line, col));
        _lineHasToken = true;
    }
}