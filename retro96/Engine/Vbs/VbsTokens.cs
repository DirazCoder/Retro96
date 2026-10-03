using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

public enum VbsTokenKind { Eof, EndOfLine, Identifier, Keyword, Literal, Punctuator }

public abstract record VbsToken(VbsTokenKind Kind, int Line, int Column);

public sealed record VbsEofToken(int Line, int Column)
    : VbsToken(VbsTokenKind.Eof, Line, Column);

public sealed record VbsEndOfLineToken(int Line, int Column)
    : VbsToken(VbsTokenKind.EndOfLine, Line, Column);

public sealed record VbsIdentifierToken(string Name, int Line, int Column)
    : VbsToken(VbsTokenKind.Identifier, Line, Column);

/// <summary>Keyword is stored UPPERCASE — everything in VBScript is case-insensitive.</summary>
public sealed record VbsKeywordToken(string Keyword, int Line, int Column)
    : VbsToken(VbsTokenKind.Keyword, Line, Column);

public sealed record VbsLiteralToken(VbsVariant Value, int Line, int Column)
    : VbsToken(VbsTokenKind.Literal, Line, Column);

public sealed record VbsPunctToken(string Text, int Line, int Column)
    : VbsToken(VbsTokenKind.Punctuator, Line, Column);

public static class VbsKeywords
{
    public static readonly HashSet<string> All = new(System.StringComparer.Ordinal)
    {
        "AND", "BYVAL", "BYREF", "CALL", "CASE", "CONST", "DIM", "DO", "EACH",
        "ELSE", "ELSEIF", "END", "EQV", "ERASE", "ERROR", "EXIT", "EXPLICIT",
        "FALSE", "FOR", "FUNCTION", "GOTO", "IF", "IMP", "IN", "IS", "LET",
        "LOOP", "MOD", "NEXT", "NOT", "NOTHING", "NULL", "ON", "OPTION", "OR",
        "PRESERVE", "PUBLIC", "PRIVATE", "REDIM", "REM", "RESUME", "SELECT",
        "SET", "STEP", "STOP", "SUB", "THEN", "TO", "TRUE", "UNTIL", "WEND",
        "WHILE", "XOR", "EMPTY"
    };
}