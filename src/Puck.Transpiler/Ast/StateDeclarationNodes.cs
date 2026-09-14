namespace Puck.Transpiler.Ast;

/// <summary>A modifier call attached to a <c>table</c>/<c>slot</c> declaration or one of a table's cell entries —
/// e.g. <c>bounds(minimum: 0, maximum: 100)</c>, <c>advance(perSecond: 5)</c>, <c>capacity(8)</c>, or
/// <c>behavior(none)</c>. This IS a call — it reuses the ordinary call-expression grammar
/// (<see cref="ArgumentNode"/>) verbatim — but it stands after a declaration header or a cell entry rather than in
/// expression position, so it carries its own node rather than an <see cref="ExpressionNode"/>. The core parses the
/// shape only; which modifier names and argument shapes are legal for a given declaration is the owning
/// vocabulary's own answer (<c>Puck.World.Transpiler</c> for <c>state.world</c>).</summary>
/// <param name="Name">The modifier's name (<c>bounds</c>, <c>advance</c>, <c>capacity</c>, <c>behavior</c>, or an
/// unrecognized name the owning vocabulary refuses by name).</param>
/// <param name="Arguments">The positional and named argument expressions, exactly as an ordinary call parses them.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateModifierNode(
    string Name,
    IReadOnlyList<ArgumentNode> Arguments,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary>One cell entry inside a <c>table</c> declaration's body: <c>key = value [modifier]*</c> — e.g.
/// <c>mana = 50 advance(perSecond: 5)</c>.</summary>
/// <param name="Key">The cell's key (an identifier or a quoted string).</param>
/// <param name="Value">The cell's initial value expression.</param>
/// <param name="Modifiers">The cell's own modifier calls, in written order (only <c>advance</c> and <c>behavior</c>
/// are legal on a cell entry — the owning vocabulary refuses the rest by name).</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateCellEntryNode(
    string Key,
    ExpressionNode Value,
    IReadOnlyList<StateModifierNode> Modifiers,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary>A <c>table</c> declaration: <c>table name : Kind [modifier]* { cellEntry* }</c> — the keyed-row
/// sugar. Legal only where the owning vocabulary admits a state-row declaration (<c>state.world</c> alone, for the
/// world vocabulary).</summary>
/// <param name="Name">The declared row's name.</param>
/// <param name="Kind">The declared cell kind, exactly as written (an unrecognized kind is the owning vocabulary's
/// own refusal, not a parse error — the core does not know the admitted kind set).</param>
/// <param name="Modifiers">The row-level modifier calls, in written order.</param>
/// <param name="Cells">The declared cell entries, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateTableDeclarationNode(
    string Name,
    string Kind,
    IReadOnlyList<StateModifierNode> Modifiers,
    IReadOnlyList<StateCellEntryNode> Cells,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary>A <c>slot</c> declaration: <c>slot name : Kind [= value] [modifier]*</c> — the scalar-row sugar. Legal
/// only where the owning vocabulary admits a state-row declaration.</summary>
/// <param name="Name">The declared row's name.</param>
/// <param name="Kind">The declared cell kind, exactly as written.</param>
/// <param name="Value">The optional default value expression, or <see langword="null"/> for an uninitialized
/// slot (a row that gains its cell only once something writes it).</param>
/// <param name="Modifiers">The row-level modifier calls, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateSlotDeclarationNode(
    string Name,
    string Kind,
    ExpressionNode? Value,
    IReadOnlyList<StateModifierNode> Modifiers,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(
    Offset,
    Length,
    Line,
    Column
);
