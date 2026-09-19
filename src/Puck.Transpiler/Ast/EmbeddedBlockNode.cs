namespace Puck.Transpiler.Ast;

/// <summary>An embedded-dialect statement block: <c>language { body }</c> — e.g. <c>sql { ... }</c>.
/// The core parser captures the block with balanced braces; the owning vocabulary parses its inner dialect.</summary>
/// <param name="Language">The dialect or language identifier (e.g. <c>"sql"</c>).</param>
/// <param name="Body">The raw inner body text of the block.</param>
/// <param name="BodyOffset">The absolute character offset in the source buffer where <paramref name="Body"/> begins.</param>
/// <param name="BodyLine">The 1-based line number where <paramref name="Body"/> begins.</param>
/// <param name="BodyColumn">The 1-based column number where <paramref name="Body"/> begins.</param>
/// <param name="Offset">The character offset of the block declaration.</param>
/// <param name="Length">The character length of the entire block.</param>
/// <param name="Line">The 1-based line number of the block declaration.</param>
/// <param name="Column">The 1-based column number of the block declaration.</param>
public sealed record EmbeddedBlockNode(
    string Language,
    string Body,
    int BodyOffset,
    int BodyLine,
    int BodyColumn,
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
