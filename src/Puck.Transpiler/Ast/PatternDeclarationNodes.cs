using Puck.State;

namespace Puck.Transpiler.Ast;

/// <summary>One entry of a <c>pattern</c> declaration's <c>symbols</c> block: <c>name = min</c> or
/// <c>name = min..max</c>, the inclusive value band a token is read as this letter in.</summary>
/// <param name="Name">The symbol's name, as the match expression references it.</param>
/// <param name="Minimum">The least value the symbol accepts.</param>
/// <param name="Maximum">The greatest value the symbol accepts.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record PatternSymbolDeclarationNode(
    string Name,
    decimal Minimum,
    decimal Maximum,
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
/// <summary>A <c>pattern</c> declaration: <c>pattern name : Kind { attribute: … value: … maxStates: …
/// symbols { … } match: … }</c>. The core parses the shape and the match algebra
/// (<see cref="PatternSpelling"/>); which kinds and which word source a vocabulary admits is its own
/// answer.</summary>
/// <param name="Name">The declared pattern's name, as a rule's <c>$match:</c> operand references it.</param>
/// <param name="Kind">The numeric kind the word's values are read in, exactly as written.</param>
/// <param name="Symbols">The alphabet, in written order.</param>
/// <param name="Match">The language, parsed from the <c>match:</c> text.</param>
/// <param name="Attribute">The keyed row over a zone's token domain whose cell values form the word, or
/// <see langword="null"/>.</param>
/// <param name="Value">The parsed per-token operand, or <see langword="null"/>.</param>
/// <param name="MaximumStates">The authored machine-state budget, or <see langword="null"/> for the default.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record PatternDeclarationNode(
    string Name,
    string Kind,
    IReadOnlyList<PatternSymbolDeclarationNode> Symbols,
    PatternNode Match,
    string? Attribute = null,
    OperandExpressionNode? Value = null,
    int? MaximumStates = null,
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
/// <summary><c>set name: &lt;cell-set expression&gt;</c>. One declared, reusable cell set.</summary>
/// <param name="Name">The set's name.</param>
/// <param name="Expression">The raw cell-set expression text, handed whole to the owning vocabulary's algebra.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record CellSetDeclarationNode(
    string Name,
    string Expression,
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
