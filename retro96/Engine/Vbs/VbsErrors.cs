using System;
using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

/// <summary>
/// The real VBScript error numbers. Runtime numbers (5…500) are the
/// documented Microsoft values; the 1000-range compile numbers are the ones
/// used by the VBScript compiler where known — see the verification list.
/// </summary>
public static class VbsErrorNumbers
{
    // Runtime errors
    public const int InvalidProcedureCall = 5;
    public const int Overflow = 6;
    public const int OutOfMemory = 7;
    public const int SubscriptOutOfRange = 9;
    public const int ArrayIsFixedOrLocked = 10;
    public const int DivisionByZero = 11;
    public const int TypeMismatch = 13;
    public const int OutOfStringSpace = 14;
    public const int OutOfStackSpace = 28;
    public const int SubOrFunctionNotDefined = 35;
    public const int ObjectVariableNotSet = 91;
    public const int ForLoopNotInitialized = 92;
    public const int InvalidUseOfNull = 94;
    public const int ObjectRequired = 424;
    public const int ActiveXCantCreateObject = 429;
    public const int ObjectDoesntSupport = 438;
    public const int WrongNumberOfArguments = 450;
    public const int VariableNotDefined = 500;

    // Compile (syntax) errors
    public const int SyntaxError = 1002;
    public const int ExpectedStatement = 1024;
    public const int ExpectedEndOfStatement = 1025;
    public const int ExpectedIdentifier = 1026;
    public const int NameRedefined = 1041;
    public const int CannotUseParens = 1044;

    private static readonly Dictionary<int, string> _descriptions = new()
    {
        [5] = "Invalid procedure call or argument",
        [6] = "Overflow",
        [7] = "Out of memory",
        [9] = "Subscript out of range",
        [10] = "This array is fixed or temporarily locked",
        [11] = "Division by zero",
        [13] = "Type mismatch",
        [14] = "Out of string space",
        [28] = "Out of stack space",
        [35] = "Sub or Function not defined",
        [91] = "Object variable not set",
        [92] = "For loop not initialized",
        [94] = "Invalid use of Null",
        [424] = "Object required",
        [429] = "ActiveX component can't create object",
        [438] = "Object doesn't support this property or method",
        [450] = "Wrong number of arguments",
        [500] = "Variable is not defined",
    };

    public static string Describe(int number) =>
        _descriptions.TryGetValue(number, out var d) ? d : $"Runtime error '{number}'";
}

/// <summary>Syntax (compile-time) error with line/column.</summary>
public sealed class VbsSyntaxException : Exception
{
    public int Number { get; }
    public int Line { get; }
    public int Column { get; }

    public VbsSyntaxException(int number, string message, int line, int column)
        : base(message)
    {
        Number = number;
        Line = line;
        Column = column;
    }
}

/// <summary>VBScript runtime error — carries the Err-object state.</summary>
public class VbsRuntimeException : Exception
{
    public int Number { get; }
    public string Description { get; }
    public string ErrSource { get; }
    public int Line { get; }

    public VbsRuntimeException(int number, string description,
                               int line = 0,
                               string? errSource = null)
        : base(description)
    {
        Number = number;
        Description = description;
        Line = line;
        ErrSource = errSource ?? "Microsoft VBScript runtime error";
    }
}