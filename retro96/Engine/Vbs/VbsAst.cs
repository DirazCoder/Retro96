using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

public abstract record VbsNode(int Line);
public abstract record VbsStmt(int Line) : VbsNode(Line);
public abstract record VbsExpr(int Line) : VbsNode(Line);

public sealed record VbsScript(IReadOnlyList<VbsStmt> Body);

// ── Statements ──────────────────────────────────────────────────────────────

public sealed record VbsDimStatement(int Line, List<VbsDimDecl> Decls) : VbsStmt(Line);
public sealed record VbsDimDecl(string Name, List<VbsExpr> Bounds, bool IsArray);

public sealed record VbsConstStatement(int Line, List<VbsConstDecl> Decls) : VbsStmt(Line);
public sealed record VbsConstDecl(string Name, VbsExpr Value);

public sealed record VbsReDimStatement(int Line, bool Preserve, List<VbsReDimDecl> Decls) : VbsStmt(Line);
public sealed record VbsReDimDecl(string Name, List<VbsExpr> Bounds);

public sealed record VbsEraseStatement(int Line, List<string> Names) : VbsStmt(Line);

/// <summary>IsSet distinguishes `Set x = obj` (object reference) from the
/// implicit Let assignment.</summary>
public sealed record VbsAssignmentStatement(int Line, bool IsSet, VbsExpr Target, VbsExpr Value) : VbsStmt(Line);

/// <summary>ForceByVal marks the `f(x)` statement form — parentheses around a
/// single argument force by-value passing on a Sub call.</summary>
public sealed record VbsCallStatement(int Line, VbsExpr Call, bool ForceByVal) : VbsStmt(Line);

public sealed record VbsIfStatement(int Line, List<VbsIfBranch> Branches, List<VbsStmt>? ElseBody) : VbsStmt(Line);
public sealed record VbsIfBranch(VbsExpr Condition, List<VbsStmt> Body);

public sealed record VbsSelectStatement(int Line, VbsExpr Subject,
    List<VbsSelectCase> Cases, List<VbsStmt>? ElseBody) : VbsStmt(Line);
public sealed record VbsSelectCase(List<VbsSelectCaseItem> Items, List<VbsStmt> Body);
/// <summary>Plain item: Value + nulls. `Case Is &lt;op&gt; x`: IsOperator set.
/// `Case lo To hi`: Value=lo, ToHigh=hi.</summary>
public sealed record VbsSelectCaseItem(VbsExpr Value, string? IsOperator, VbsExpr? ToHigh);

public sealed record VbsForStatement(int Line, string Variable, VbsExpr From, VbsExpr To,
    VbsExpr? Step, List<VbsStmt> Body) : VbsStmt(Line);
public sealed record VbsForEachStatement(int Line, string Variable, VbsExpr Collection,
    List<VbsStmt> Body) : VbsStmt(Line);
public sealed record VbsDoLoopStatement(int Line, VbsLoopTest? Top, VbsLoopTest? Bottom,
    List<VbsStmt> Body) : VbsStmt(Line);
public sealed record VbsLoopTest(VbsExpr Condition, bool Until);
public sealed record VbsWhileStatement(int Line, VbsExpr Condition, List<VbsStmt> Body) : VbsStmt(Line);

public sealed record VbsSubStatement(int Line, string Name, List<VbsParam> Params,
    List<VbsStmt> Body, bool IsFunction) : VbsStmt(Line);
public sealed record VbsParam(string Name, bool ByVal);   // ByRef is the default

// ── VBScript 5.0: With / Class ──────────────────────────────────────────────

public sealed record VbsWithStatement(int Line, VbsExpr Object, List<VbsStmt> Body) : VbsStmt(Line);

/// <summary>`.Member` shorthand inside a With block — resolved against the
/// innermost With object that supports the member (outer blocks are consulted
/// when the inner object doesn't have it).</summary>
public sealed record VbsWithMemberExpr(int Line, string Member) : VbsExpr(Line);

/// <summary>`Me` — the current class instance inside class code.</summary>
public sealed record VbsMeExpr(int Line) : VbsExpr(Line);

/// <summary>`New ClassName` — construction expression (Set x = New Foo).</summary>
public sealed record VbsNewExpr(int Line, string ClassName) : VbsExpr(Line);

public sealed record VbsClassStatement(int Line, string Name, List<VbsClassMemberDecl> Members) : VbsStmt(Line);

public abstract record VbsClassMemberDecl(int Line);

/// <summary>Public/Private field declarations (`Public X, Y` / `Private A()`).
/// Dim without a visibility keyword is accepted as Private (VB6-style).</summary>
public sealed record VbsClassFieldDecl(int Line, List<VbsDimDecl> Fields, bool IsPublic) : VbsClassMemberDecl(Line);

public sealed record VbsClassMethodDecl(int Line, string Name, List<VbsParam> Params,
    List<VbsStmt> Body, bool IsFunction, bool IsPublic) : VbsClassMemberDecl(Line);

public enum VbsPropertyKind { Get, Let, Set }

/// <summary>Property Get/Let/Set accessor. A Get is function-like: the property
/// name is its implicit return variable. For Let/Set the value being assigned is
/// the LAST parameter. `Exit Property` leaves the accessor.</summary>
public sealed record VbsClassPropertyDecl(int Line, string Name, VbsPropertyKind Kind,
    List<VbsParam> Params, List<VbsStmt> Body, bool IsPublic) : VbsClassMemberDecl(Line);

public enum VbsExitKind { Sub, Function, Property, Do, For }
public sealed record VbsExitStatement(int Line, VbsExitKind Kind) : VbsStmt(Line);

public sealed record VbsOnErrorStatement(int Line, bool ResumeNext) : VbsStmt(Line);
public sealed record VbsOptionExplicitStatement(int Line) : VbsStmt(Line);
public sealed record VbsNopStatement(int Line) : VbsStmt(Line);   // Stop

// ── Expressions ─────────────────────────────────────────────────────────────

public sealed record VbsLiteralExpr(int Line, VbsVariant Value) : VbsExpr(Line);
public sealed record VbsNameExpr(int Line, string Name) : VbsExpr(Line);
/// <summary>Explicit parentheses — an argument that is ENTIRELY a paren group
/// is passed ByVal even to a ByRef parameter.</summary>
public sealed record VbsParenExpr(int Line, VbsExpr Inner) : VbsExpr(Line);
public sealed record VbsMemberExpr(int Line, VbsExpr Object, string Member) : VbsExpr(Line);
/// <summary>f(args), a(index), obj.Method(args) — resolved at runtime
/// (procedure vs array index vs default property).</summary>
public sealed record VbsInvokeExpr(int Line, VbsExpr Target, List<VbsExpr> Args) : VbsExpr(Line);
public sealed record VbsUnaryExpr(int Line, string Operator, VbsExpr Operand) : VbsExpr(Line);
public sealed record VbsBinaryExpr(int Line, string Operator, VbsExpr Left, VbsExpr Right) : VbsExpr(Line);