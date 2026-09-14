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
/// <summary>One bare token entry inside a <c>pile</c> declaration's body — just the token's key, e.g. <c>king</c> or
/// <c>"queen of hearts"</c>. Unlike <see cref="StateCellEntryNode"/> it carries no value or modifiers: a pile
/// member's cell is always the boolean presence flag <c>true</c>, so there is nothing else to spell.</summary>
/// <param name="Key">The token's key (an identifier or a quoted string) — a key of the pile's <c>of</c> token-domain
/// row.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StatePileTokenNode(
    string Key,
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
/// <summary>A <c>pile</c> declaration: <c>pile name of tokenRow [modifier]* { token* }</c> — the ordered-membership
/// sugar over a <c>keysOf</c> domain with <c>ordered</c> set. Legal only where the owning vocabulary admits a
/// state-row declaration.</summary>
/// <param name="Name">The declared row's name.</param>
/// <param name="TokenRow">The name of the row supplying the token domain (the <c>keysOf</c> target) — written after
/// <c>of</c>.</param>
/// <param name="Modifiers">The row-level modifier calls, in written order (only <c>capacity</c> is legal — the
/// owning vocabulary refuses the rest by name).</param>
/// <param name="Tokens">The declared initial members, in pile order (first entry is index 0 of <c>cells</c>).</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StatePileDeclarationNode(
    string Name,
    string TokenRow,
    IReadOnlyList<StateModifierNode> Modifiers,
    IReadOnlyList<StatePileTokenNode> Tokens,
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
/// <summary>A <c>grid</c> declaration: <c>grid name : Kind [modifier]* [{ cellEntry* }]</c> — the physical-lattice
/// occupancy sugar. It mints both a <c>state.lattices</c> Grid topology (named identically to the declared row) and
/// the row itself, over a <c>cellsOf</c> domain lying on that topology. Legal only where the owning vocabulary
/// admits a state-row declaration.</summary>
/// <param name="Name">The declared row's name, reused as the topology's own name.</param>
/// <param name="Kind">The declared cell kind, exactly as written (a board admits only <c>Int</c>/<c>Bool</c> — the
/// owning vocabulary's own refusal, not a parse error).</param>
/// <param name="Modifiers">The row- and topology-level modifier calls, in written order.</param>
/// <param name="Cells">The declared initial cell entries, in written order — empty for a derived (<c>inverse</c>)
/// board, which authors no cells of its own.</param>
/// <param name="HasBody">Whether a <c>{ }</c> body was written at all, distinct from an empty one — a table's own
/// convention (see <see cref="StateTableDeclarationNode"/>) does not apply here because a grid's body is optional.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateGridDeclarationNode(
    string Name,
    string Kind,
    IReadOnlyList<StateModifierNode> Modifiers,
    IReadOnlyList<StateCellEntryNode> Cells,
    bool HasBody,
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
