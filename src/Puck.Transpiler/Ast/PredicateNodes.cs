namespace Puck.Transpiler.Ast;

/// <summary>Base class for all gate-predicate nodes parsed from a <c>when</c> clause (§1 of the sugar wave).
/// A predicate node never classifies itself as <c>compareState</c>/<c>compareValue</c> — it carries the raw operand
/// text the parser validated through <c>ExpressionSpelling.TryParse</c>, leaving that classification (and the
/// verbatim <c>ValueExpression</c> capture) to the lowering stage.</summary>
public abstract record PredicateNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(Offset, Length, Line, Column);

/// <summary>One comparison: <c>left cmp right</c>, with an optional <c>: Kind</c>/<c>as Kind</c> suffix that always
/// forces a <c>compareValue</c> lowering regardless of how simple either operand is.</summary>
/// <param name="LeftText">The raw left operand span, already validated through <c>ExpressionSpelling.TryParse</c>.</param>
/// <param name="Comparator">The comparison token (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>).</param>
/// <param name="RightText">The raw right operand span, already validated through <c>ExpressionSpelling.TryParse</c>.</param>
/// <param name="Kind">The optional forced kind annotation (<c>"Int"</c> or <c>"Fixed"</c>), or <see langword="null"/>.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ComparisonPredicateNode(
    string LeftText,
    string Comparator,
    string RightText,
    string? Kind,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : PredicateNode(Offset, Length, Line, Column);

/// <summary>A flat conjunction of two or more operands, built N-ary directly while matching repeated <c>and</c> at
/// the same nesting level (never a binary pair later flattened).</summary>
/// <param name="Operands">The conjoined operands, in source order (at least two).</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record AndPredicateNode(
    IReadOnlyList<PredicateNode> Operands,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : PredicateNode(Offset, Length, Line, Column);

/// <summary>A flat disjunction of two or more operands, built N-ary directly while matching repeated <c>or</c> at
/// the same nesting level (never a binary pair later flattened).</summary>
/// <param name="Operands">The disjoined operands, in source order (at least two).</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record OrPredicateNode(
    IReadOnlyList<PredicateNode> Operands,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : PredicateNode(Offset, Length, Line, Column);

/// <summary>A negated predicate: <c>not operand</c>.</summary>
/// <param name="Operand">The negated predicate.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record NotPredicateNode(
    PredicateNode Operand,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : PredicateNode(Offset, Length, Line, Column);

/// <summary>A rule or option gate: <c>when Gate</c>.</summary>
/// <param name="Predicate">The parsed gate.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record WhenStatementNode(
    PredicateNode Predicate,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A call-form gate: <c>key(left, held)</c>. The comparison predicate covers every gate expressible as
/// <c>left cmp right</c>; this covers the ones that are not — a machine's joypad edge, a vocabulary's own named
/// test. The parser assigns it no meaning beyond the call's own name and arguments, so a vocabulary that knows no
/// such gate refuses it by name.</summary>
/// <param name="Call">The call as written.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record CallPredicateNode(
    CallExpressionNode Call,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : PredicateNode(Offset, Length, Line, Column);
