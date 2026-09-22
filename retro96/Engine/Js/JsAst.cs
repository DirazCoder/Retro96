namespace Retro96.Engine.Js;

// ─────────────────────────────────────────────────────────────────────────────
// JavaScript 1.1/1.2 AST — typed node hierarchy.
//
// Every statement derives from Stmt, every expression from Expr, so the
// parser and interpreter get compile-time wiring guarantees instead of
// object-casts that can silently mismatch.
// ─────────────────────────────────────────────────────────────────────────────

public abstract record Node;
public abstract record Stmt : Node;
public abstract record Expr : Node;

// ── Statements ───────────────────────────────────────────────────────────────

public record VarDeclarator(Identifier Id, Expr? Init) : Node;
public record VarDeclaration(IReadOnlyList<VarDeclarator> Declarations) : Stmt;

public record ProgramNode(IReadOnlyList<Stmt> Body) : Node;
public record BlockStatement(IReadOnlyList<Stmt> Body) : Stmt;
public record ExpressionStatement(Expr Expression) : Stmt;
public record IfStatement(Expr Test, Stmt Consequent, Stmt? Alternate) : Stmt;
public record WhileStatement(Expr Test, Stmt Body) : Stmt;
public record DoWhileStatement(Stmt Body, Expr Test) : Stmt;

/// <summary>Init is a VarDeclaration or a bare Expr when absent/null.</summary>
public record ForStatement(Node? Init, Expr? Test, Expr? Update, Stmt Body) : Stmt;

/// <summary>Left is a VarDeclaration (for (var x in o)) or a bare Expr (for (x in o)).</summary>
public record ForInStatement(Node Left, Expr Right, Stmt Body) : Stmt;

public record ReturnStatement(Expr? Argument) : Stmt;
public record BreakStatement(string? Label) : Stmt;
public record ContinueStatement(string? Label) : Stmt;
public record LabeledStatement(string Label, Stmt Body) : Stmt;
public record SwitchCase(Expr? Test, IReadOnlyList<Stmt> Consequent) : Node;
public record SwitchStatement(Expr Discriminant, IReadOnlyList<SwitchCase> Cases) : Stmt;
public record ThrowStatement(Expr Argument) : Stmt;
public record CatchClause(Identifier Param, Stmt Body) : Node;
public record TryStatement(Stmt Block, CatchClause? Handler, Stmt? Finalizer) : Stmt;
public record WithStatement(Expr Object, Stmt Body) : Stmt;
public record FunctionDeclaration(Identifier Id, IReadOnlyList<Identifier> Params, BlockStatement Body) : Stmt;
public record EmptyStatement() : Stmt;

// ── Expressions ──────────────────────────────────────────────────────────────

public record AssignmentExpr(string Operator, Expr Left, Expr Right) : Expr;
public record BinaryExpr(string Operator, Expr Left, Expr Right) : Expr;
public record LogicalExpr(string Operator, Expr Left, Expr Right) : Expr;
public record UnaryExpr(string Operator, Expr Argument) : Expr;
public record UpdateExpr(string Operator, Expr Argument, bool Prefix) : Expr;
public record TernaryExpr(Expr Test, Expr Consequent, Expr Alternate) : Expr;
public record CallExpr(Expr Callee, IReadOnlyList<Expr> Arguments) : Expr;
public record NewExpr(Expr Callee, IReadOnlyList<Expr> Arguments) : Expr;
public record MemberExpr(Expr Object, Expr Property, bool Computed) : Expr;
public record FunctionExpr(Identifier? Id, IReadOnlyList<Identifier> Params, BlockStatement Body) : Expr;
public record ArrayExpr(IReadOnlyList<Expr?> Elements) : Expr;
public record PropertyExpr(Expr Key, Expr Value) : Node;
public record ObjectExpr(IReadOnlyList<PropertyExpr> Properties) : Expr;
public record Identifier(string Name) : Expr;
public record NumberLiteral(double Value) : Expr;
public record StringLiteral(string Value) : Expr;
public record BoolLiteral(bool Value) : Expr;
public record NullLiteral() : Expr;
public record RegexLiteral(string Pattern, string Flags) : Expr;
public record ThisExpr() : Expr;
public record VoidExpr(Expr Argument) : Expr;
public record TypeofExpr(Expr Argument) : Expr;
public record DeleteExpr(Expr Argument) : Expr;
public record InExpr(Expr Left, Expr Right) : Expr;
public record InstanceofExpr(Expr Left, Expr Right) : Expr;