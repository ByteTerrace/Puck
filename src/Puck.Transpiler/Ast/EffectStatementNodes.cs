namespace Puck.Transpiler.Ast;

/// <summary>A structural reference to a state row, optionally keyed: <c>name</c> or <c>name[key]</c>. The parser
/// resolves the whole span through <c>ExpressionSpelling.TryParse</c> and requires exactly one <c>State</c> token
/// (PUCK003 otherwise), so <see cref="Name"/>/<see cref="Key"/> are already the token's own split — including a
/// <c>$zones[...]</c>-folded name and a second bracket group read back as the key (§1.5-F).</summary>
/// <param name="Name">The state row or reserved-channel name.</param>
/// <param name="Key">The optional cell key.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RowRefNode(
    string Name,
    string? Key,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(Offset, Length, Line, Column);

/// <summary>Base class for a <c>setState</c>/<c>addState</c>/<c>push</c> right-hand side. Only the syntactic shape
/// is decided here (a string literal, a number carrying the <c>s</c> unit, or opaque operand text already validated
/// through <c>ExpressionSpelling.TryParse</c>); classifying <see cref="RhsOperandNode"/> into
/// <c>Value</c>/<c>FromState</c>+<c>FromKey</c>/<c>Expression</c> per the spec's own table is lowering-stage work.</summary>
public abstract record RhsNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(Offset, Length, Line, Column);

/// <summary>A string-literal right-hand side, destined for the effect's <c>Text</c> field.</summary>
/// <param name="Text">The decoded string value.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RhsTextNode(
    string Text,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : RhsNode(Offset, Length, Line, Column);

/// <summary>A number-with-<c>s</c>-unit right-hand side, destined for the effect's <c>ValueSeconds</c> field.</summary>
/// <param name="Seconds">The seconds value.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RhsSecondsNode(
    decimal Seconds,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : RhsNode(Offset, Length, Line, Column);

/// <summary>Opaque operand text, already validated through <c>ExpressionSpelling.TryParse</c>, for the lowering
/// stage to classify (a single <c>Constant</c> token → <c>Value</c>, a single <c>State</c> token → <c>FromState</c>/
/// <c>FromKey</c>, anything else → a verbatim <c>Expression</c> capture).</summary>
/// <param name="Text">The raw operand span.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RhsOperandNode(
    string Text,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : RhsNode(Offset, Length, Line, Column);

/// <summary>Base class for every dedicated effect-statement node (§2). A call expression used as an effect —
/// <c>generate(...)</c> or any <c>Puck.World.Schema</c> extension arm — carries no dedicated node; it parses as an
/// ordinary <see cref="ExpressionStatementNode"/> wrapping a <see cref="CallExpressionNode"/>.</summary>
public abstract record EffectStatementNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : StatementNode(Offset, Length, Line, Column);

/// <summary><c>row[key] = rhs</c> — a <c>setState</c> effect.</summary>
public sealed record SetCellStatementNode(
    RowRefNode Target,
    RhsNode Rhs,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>row[key] += rhs</c> — an <c>addState</c> effect.</summary>
public sealed record AddCellStatementNode(
    RowRefNode Target,
    RhsNode Rhs,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>row[key] op= rhs</c> for an arithmetic or bitwise <c>op</c> beyond plain assignment and addition,
/// which have their own nodes. The operator is carried as written; which of them a document's own effects can
/// express is the lowering vocabulary's answer.</summary>
/// <param name="Target">The assigned row reference.</param>
/// <param name="Operator">The compound operator as written, without the trailing <c>=</c> (<c>-</c>, <c>*</c>,
/// <c>/</c>, <c>%</c>, <c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&gt;&gt;</c>, <c>&lt;&lt;</c>).</param>
/// <param name="Rhs">The right-hand side.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record CompoundAssignStatementNode(
    RowRefNode Target,
    string Operator,
    RhsNode Rhs,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>push row = rhs</c> — a <c>pushState</c> effect. <c>PushState</c> carries no key, unlike set/add.</summary>
public sealed record PushStatementNode(
    string RowName,
    RhsNode Rhs,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>countdown row</c> / <c>countdown row[key]</c> — a <c>countdownState</c> effect.</summary>
public sealed record CountdownStatementNode(
    RowRefNode Target,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>remove row</c> / <c>remove row[key]</c> — a <c>removeStateCell</c> effect.</summary>
public sealed record RemoveCellStatementNode(
    RowRefNode Target,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>schedule row in Ns</c> — a <c>scheduleState</c> effect. The <c>s</c> suffix is required (PUCK010
/// otherwise); <c>DelaySeconds</c> is a plain decimal on the wire, never a <c>ValueExpression</c>.</summary>
public sealed record ScheduleStatementNode(
    RowRefNode Target,
    decimal DelaySeconds,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>transform row = call(...)</c> — wraps <c>ActionEffect.TransformState</c> around whatever
/// <c>StateTransform</c> call-form value follows; <see cref="RowName"/> is the transform's own local destination
/// name (the transform's real target row travels inside <see cref="Transform"/>'s own arguments).</summary>
public sealed record TransformStatementNode(
    string RowName,
    CallExpressionNode Transform,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>transaction { ... } [onFailure { ... }]</c>. A dedicated node rather than two independent blocks, so
/// <c>onFailure</c> unambiguously binds to its own enclosing <c>transaction</c>.</summary>
/// <param name="MainEffects">The transaction's own effect statements, in source order.</param>
/// <param name="OnFailureEffects">The <c>onFailure</c> body's effect statements, or <see langword="null"/> when absent.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TransactionStatementNode(
    IReadOnlyList<StatementNode> MainEffects,
    IReadOnlyList<StatementNode>? OnFailureEffects,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>if Gate { ... } [else { ... }]</c>, and the <c>else if</c> chain an <c>Else</c> holding a single
/// nested <see cref="IfStatementNode"/> spells. General control flow: the language parses it for every document
/// vocabulary, and each one decides whether its rule shape can carry a branch at all —
/// <c>puck.world.def.v1</c> rules are straight-line and refuse it by name, a <c>puck.cartridge.v1</c> rule lowers
/// it to its own <c>if</c> action.</summary>
/// <param name="Condition">The branch gate, parsed by the same reader a rule's own <c>when</c> uses.</param>
/// <param name="Then">The statements run when the gate holds.</param>
/// <param name="Else">The statements run when it does not, or <see langword="null"/> for a bare <c>if</c>.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record IfStatementNode(
    PredicateNode Condition,
    IReadOnlyList<StatementNode> Then,
    IReadOnlyList<StatementNode>? Else,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>repeat Count as name { ... }</c> — a bounded loop whose body runs <c>Count</c> times with
/// <c>name</c> bound to 0, 1, … Count-1. The count is a COUNT, never a range: there is no start operand to get
/// wrong and no inclusive/exclusive question to answer. A vocabulary that unrolls at compile time needs the count
/// to be a constant expression; that is the vocabulary's refusal to make, not the parser's.</summary>
/// <param name="Count">The number of iterations.</param>
/// <param name="Index">The name bound to the iteration ordinal inside <paramref name="Body"/>.</param>
/// <param name="Body">The statements run once per iteration.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RepeatStatementNode(
    ExpressionNode Count,
    string Index,
    IReadOnlyList<StatementNode> Body,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);

/// <summary><c>break</c> — leaves the innermost enclosing <see cref="RepeatStatementNode"/>. Parsed anywhere a
/// statement is; whether a <c>break</c> outside a loop is an error is the lowering vocabulary's call, since only it
/// knows what its own runtime does with one.</summary>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record BreakStatementNode(
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : EffectStatementNode(Offset, Length, Line, Column);
