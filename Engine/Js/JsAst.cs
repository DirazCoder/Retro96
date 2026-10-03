using System.Collections.Generic;

namespace Retro96.Engine.Js
{
    // Statements
    public record VarDeclarator(Identifier Id, object? Init);
    public record VarDeclaration(IReadOnlyList<VarDeclarator> Declarations);
    public record ProgramNode(IReadOnlyList<object> Body);
    public record BlockStatement(IReadOnlyList<object> Body);
    public record ExpressionStatement(object Expression);
    public record IfStatement(object Test, object Consequent, object? Alternate);
    public record WhileStatement(object Test, object Body);
    public record DoWhileStatement(object Body, object Test);
    public record ForStatement(object? Init, object? Test, object? Update, object Body);
    public record ForInStatement(object Left, object Right, object Body);
    public record ReturnStatement(object? Argument);
    public record BreakStatement(string? Label);
    public record ContinueStatement(string? Label);
    public record LabeledStatement(string Label, object Body);
    public record SwitchCase(object? Test, IReadOnlyList<object> Consequent);
    public record SwitchStatement(object Discriminant, IReadOnlyList<SwitchCase> Cases);
    public record ThrowStatement(object Argument);
    public record CatchClause(Identifier Param, object Body);
    public record TryStatement(object Block, CatchClause? Handler, object? Finalizer);
    public record WithStatement(object Object, object Body);
    public record FunctionDeclaration(Identifier Id, IReadOnlyList<Identifier> Params, object Body);
    public record EmptyStatement();

    // Expressions
    public record AssignmentExpr(string Operator, object Left, object Right);
    public record BinaryExpr(string Operator, object Left, object Right);
    public record LogicalExpr(string Operator, object Left, object Right);
    public record UnaryExpr(string Operator, object Argument, bool Prefix);
    public record UpdateExpr(string Operator, object Argument, bool Prefix);
    public record TernaryExpr(object Test, object Consequent, object Alternate);
    public record CallExpr(object Callee, IReadOnlyList<object> Arguments);
    public record NewExpr(object Callee, IReadOnlyList<object> Arguments);
    public record MemberExpr(object Object, object Property, bool Computed);
    public record FunctionExpr(Identifier? Id, IReadOnlyList<Identifier> Params, object Body);
    public record ArrayExpr(IReadOnlyList<object?> Elements);
    public record PropertyExpr(object Key, object Value);
    public record ObjectExpr(IReadOnlyList<PropertyExpr> Properties);
    public record Identifier(string Name);
    public record NumberLiteral(double Value);
    public record StringLiteral(string Value);
    public record BoolLiteral(bool Value);
    public record NullLiteral();
    public record RegexLiteral(string Pattern, string Flags);
    public record ThisExpr();
    public record VoidExpr(object Argument);
    public record TypeofExpr(object Argument);
    public record DeleteExpr(object Argument);
    public record InExpr(object Left, object Right);
    public record InstanceofExpr(object Left, object Right);
}
