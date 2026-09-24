using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Ast;

/// <summary>One item of a family's bracketed member list — either an index range (<c>2..12</c>, or a single index
/// when <see cref="Last"/> is absent) or a member row named outright (<c>"PileA"</c>).</summary>
/// <param name="First">The first family index of the range, or <see langword="null"/> when the item names a row.</param>
/// <param name="Last">The last family index of an inclusive range, or <see langword="null"/> for a single index.</param>
/// <param name="Row">The member row's own name, or <see langword="null"/> when the item is an index range.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record FamilyMemberNode(
    ExpressionNode? First = null,
    ExpressionNode? Last = null,
    string? Row = null,
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
/// <summary>A modifier call attached to a <c>table</c>/<c>slot</c> declaration or one of a table's cell entries —
/// e.g. <c>bounds(0..100)</c>, <c>advance(perSecond: 5)</c>, <c>capacity(8)</c>, or
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
/// <summary>A <c>table</c> declaration: <c>table name [: Enum] [modifier]* { cellEntry* }</c> — the keyed-row
/// sugar. Legal only where the owning vocabulary admits a state-row declaration (<c>state.world</c> alone, for the
/// world vocabulary).</summary>
/// <param name="Name">The declared row's name.</param>
/// <param name="Kind">The inferred cell kind, or an empty string before the owning vocabulary lowers the declaration.</param>
/// <param name="Modifiers">The row-level modifier calls, in written order.</param>
/// <param name="Cells">The declared cell entries, in written order.</param>
/// <param name="FamilySize">The optional compile-time family count expression (e.g. [8]), or null for a single row.</param>
/// <param name="FamilyMembers">The optional bracketed member list (e.g. [0, 2..12]) the family declares instead of a
/// bare count, or null.</param>
/// <param name="Initializer">The optional compile-time collection initializer expression (e.g. range(0, 52)), or null when cells are authored individually.</param>
/// <param name="Enum">The enum the row's cells are drawn from, written <c>: Enum</c> after the name, or
/// <see langword="null"/> for a row whose cells are plain values. The owning vocabulary resolves the name.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateTableDeclarationNode(
    string Name,
    string Kind,
    IReadOnlyList<StateModifierNode> Modifiers,
    IReadOnlyList<StateCellEntryNode> Cells,
    ExpressionNode? FamilySize = null,
    IReadOnlyList<FamilyMemberNode>? FamilyMembers = null,
    ExpressionNode? Initializer = null,
    string? Enum = null,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(
    Offset,
    Length,
    Line,
    Column
) {
    /// <summary>Gets the span of the <see cref="Enum"/> name as written, so a fault in the name points at it rather
    /// than at the whole declaration. Empty when no enum is named.</summary>
    public SourceSpan EnumSpan { get; init; }
}
/// <summary>A <c>slot</c> declaration: <c>slot name [: Enum] [= value] [modifier]*</c> — the scalar-row sugar. Legal
/// only where the owning vocabulary admits a state-row declaration.</summary>
/// <param name="Name">The declared row's name.</param>
/// <param name="Kind">The inferred cell kind, or an empty string before the owning vocabulary lowers the declaration.</param>
/// <param name="Value">The optional default value expression, or <see langword="null"/> for an uninitialized
/// slot (a row that gains its cell only once something writes it).</param>
/// <param name="Modifiers">The row-level modifier calls, in written order.</param>
/// <param name="FamilySize">The optional compile-time family count expression (e.g. [3]), or null for a single row.</param>
/// <param name="FamilyMembers">The optional bracketed member list (e.g. [0, 2..12]) the family declares instead of a
/// bare count, or null.</param>
/// <param name="Enum">The enum the row's cells are drawn from, written <c>: Enum</c> after the name, or
/// <see langword="null"/> for a row whose cells are plain values. The owning vocabulary resolves the name.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StateSlotDeclarationNode(
    string Name,
    string Kind,
    ExpressionNode? Value,
    IReadOnlyList<StateModifierNode> Modifiers,
    ExpressionNode? FamilySize = null,
    IReadOnlyList<FamilyMemberNode>? FamilyMembers = null,
    string? Enum = null,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(
    Offset,
    Length,
    Line,
    Column
) {
    /// <inheritdoc cref="StateTableDeclarationNode.EnumSpan"/>
    public SourceSpan EnumSpan { get; init; }
}
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
/// <param name="FamilySize">The optional compile-time family count expression (e.g. [8]), or null for a single row.</param>
/// <param name="FamilyMembers">The optional bracketed member list (e.g. [0, 2..12]) the family declares instead of a
/// bare count, or null.</param>
/// <param name="Initializer">The optional compile-time token collection initializer (e.g. range(0, 52)), or null when tokens are authored individually.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StatePileDeclarationNode(
    string Name,
    string TokenRow,
    IReadOnlyList<StateModifierNode> Modifiers,
    IReadOnlyList<StatePileTokenNode> Tokens,
    ExpressionNode? FamilySize = null,
    IReadOnlyList<FamilyMemberNode>? FamilyMembers = null,
    ExpressionNode? Initializer = null,
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
/// <summary>A <c>grid</c> declaration: <c>grid name [: Enum] [modifier]* [{ cellEntry* }]</c> — the physical-lattice
/// occupancy sugar. It mints both a <c>state.lattices</c> Grid topology (named identically to the declared row) and
/// the row itself, over a <c>cellsOf</c> domain lying on that topology. Legal only where the owning vocabulary
/// admits a state-row declaration.</summary>
/// <param name="Name">The declared row's name, reused as the topology's own name.</param>
/// <param name="Kind">The inferred cell kind, or an empty string before the owning vocabulary lowers the declaration
/// (a board admits only <c>Int</c>/<c>Bool</c> — the owning vocabulary's own refusal, not a parse error).</param>
/// <param name="Modifiers">The row- and topology-level modifier calls, in written order.</param>
/// <param name="Cells">The declared initial cell entries, in written order — empty for a derived (<c>inverse</c>)
/// board, which authors no cells of its own.</param>
/// <param name="HasBody">Whether a <c>{ }</c> body was written at all, distinct from an empty one — a table's own
/// convention (see <see cref="StateTableDeclarationNode"/>) does not apply here because a grid's body is optional.</param>
/// <param name="FamilySize">The optional compile-time family count expression, or null for a single row.</param>
/// <param name="FamilyMembers">The optional bracketed member list (e.g. [0, 2..12]) the family declares instead of a
/// bare count, or null.</param>
/// <param name="Enum">The enum the row's cells are drawn from, written <c>: Enum</c> after the name, or
/// <see langword="null"/> for a row whose cells are plain values. The owning vocabulary resolves the name.</param>
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
    ExpressionNode? FamilySize = null,
    IReadOnlyList<FamilyMemberNode>? FamilyMembers = null,
    string? Enum = null,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(
    Offset,
    Length,
    Line,
    Column
) {
    /// <inheritdoc cref="StateTableDeclarationNode.EnumSpan"/>
    public SourceSpan EnumSpan { get; init; }
}
/// <summary>One member of an <c>enum</c> declaration. It is a node rather than a bare name so the comments and
/// blank lines around it have somewhere to ride.</summary>
/// <param name="Name">The member's name; its ordinal is its position in the declaration.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record EnumMemberNode(
    string Name,
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
/// <summary><c>enum Name { Member1, Member2, ... }</c>. Lowers to integer constants and validation bounds.</summary>
public sealed record EnumDeclarationNode(
    string Name,
    IReadOnlyList<EnumMemberNode> Members,
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
/// <summary>A typed field inside a record declaration.</summary>
public sealed record RecordFieldNode(
    string Name,
    string TypeName,
    IReadOnlyList<StateModifierNode> Modifiers,
    ExpressionNode? Default = null,
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
/// <summary>A statically populated or runtime-claimable pool of record instances. The owning vocabulary lowers the
/// declaration to its pool wire shape; the core only preserves the record name, capacity and initializer syntax.</summary>
/// <param name="Name">The pool name.</param>
/// <param name="RecordName">The record type each instance carries.</param>
/// <param name="Capacity">The compile-time capacity expression, or <see langword="null"/> when the declaration has no bracket.</param>
/// <param name="Initializer">The optional compile-time array of instance objects.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record StatePoolDeclarationNode(
    string Name,
    string RecordName,
    ExpressionNode? Capacity,
    ExpressionNode? Initializer,
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
/// <summary>A bounded pool of relationships between instances from two ordinary pools.</summary>
public sealed record StatePairPoolDeclarationNode(
    string Name,
    string RecordName,
    string LeftPool,
    string RightPool,
    ExpressionNode MaxLive,
    bool Directed = true,
    bool AllowSelf = false,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);
/// <summary><c>record Name { field1: Type1 ... }</c>. Lowers to columnar state row sets (Structure of Arrays).</summary>
public sealed record RecordDeclarationNode(
    string Name,
    IReadOnlyList<RecordFieldNode> Fields,
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
/// <summary><c>derive name = expression</c>: a compile-time name for operand text, expanded wherever an operand
/// reads it; the document carries the expansion, never the name.</summary>
public sealed record DerivedStateNode(
    string Name,
    OperandExpressionNode Expression,
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
