namespace Puck.World.Transpiler.Ast;

/// <summary><c>rule "name" { ... }</c>. <see cref="Statements"/> is the ordered body — a mix of
/// <see cref="WhenStatementNode"/> (at most one), <see cref="BindStatementNode"/>, ordinary <see cref="PropertyNode"/>
/// entries (<c>mode</c>/<c>forEach</c>/<c>zones</c>, and a backquoted <c>`$replace`</c> override), at most one
/// <see cref="DecisionBlockNode"/>, and effect statements (§2, including a bare <see cref="ExpressionStatementNode"/>
/// call for <c>generate(...)</c> or any extension arm) — routed to their JSON fields by kind, not position.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Statements">The rule's body statements, in source order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RuleBlockNode(
    string Name,
    IReadOnlyList<StatementNode> Statements,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary><c>bind name : Kind = &lt;operand&gt;</c>. <see cref="Kind"/> is required — <c>RuleBinding.Kind</c> has
/// no default — so its absence is PUCK006, never a silent default.</summary>
/// <param name="Name">The binding's name.</param>
/// <param name="Kind">The required kind annotation, <c>"Int"</c> or <c>"Fixed"</c>.</param>
/// <param name="ExpressionText">The raw initializer span, already validated through <c>ExpressionSpelling.TryParse</c>.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record BindStatementNode(
    string Name,
    string Kind,
    string ExpressionText,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary><c>decision { ... }</c>. <see cref="Statements"/> mixes ordinary <see cref="PropertyNode"/> entries
/// (<c>periodSeconds</c>, required; <c>mode</c>/<c>scoreKind</c>/<c>commitmentSeconds</c>/<c>incumbentBonus</c>/
/// <c>seed</c>, elided by the lowering stage on their C# default), at most one <see cref="InterruptStatementNode"/>,
/// at most one <see cref="OnNoChoiceBlockNode"/>, and one or more <see cref="OptionBlockNode"/> entries.</summary>
public sealed record DecisionBlockNode(
    IReadOnlyList<StatementNode> Statements,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary><c>option "name" { ... }</c>. <see cref="Statements"/> mixes at most one <see cref="WhenStatementNode"/>,
/// exactly one required <see cref="ScoreStatementNode"/>, an ordinary <see cref="PropertyNode"/> for
/// <c>neighbors: { ... }</c>, and effect statements.</summary>
/// <param name="Name">The option's name.</param>
/// <param name="Statements">The option's body statements, in source order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record OptionBlockNode(
    string Name,
    IReadOnlyList<StatementNode> Statements,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary><c>score: &lt;operand&gt;</c> inside an <see cref="OptionBlockNode"/> — required (PUCK013 otherwise).</summary>
/// <param name="Text">The raw operand span, already validated through <c>ExpressionSpelling.TryParse</c>.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ScoreStatementNode(
    string Text,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary><c>interrupt Gate</c> inside a <see cref="DecisionBlockNode"/>, reusing the full gate grammar (§1).</summary>
/// <param name="Predicate">The parsed gate.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record InterruptStatementNode(
    PredicateNode Predicate,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary><c>onNoChoice { ... }</c> inside a <see cref="DecisionBlockNode"/>, reusing the effect-statement grammar (§2).</summary>
/// <param name="Effects">The effect statements, in source order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record OnNoChoiceBlockNode(
    IReadOnlyList<StatementNode> Effects,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A bare keyword statement — an identifier with nothing else on its line, e.g. the <c>solid</c> flag
/// inside a <c>placement { }</c> row (§4.2) standing in for <c>solid: { margin: 0 }</c>.</summary>
/// <param name="Name">The bare keyword.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record FlagStatementNode(
    string Name,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);
