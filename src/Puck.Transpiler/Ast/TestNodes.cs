namespace Puck.Transpiler.Ast;

/// <summary>One <c>given</c> line: the initial value a cell of the generated test world boots at, written
/// <c>row = literal</c> or <c>row[key] = literal</c>.</summary>
/// <param name="Target">The row, keyed or slot-shaped.</param>
/// <param name="Value">The literal the cell boots at.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestGivenNode(
    RowRefNode Target,
    RhsNode Value,
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
/// <summary>One step of a test's <c>when</c> block: a position on the generated tick grid, or a command a seat
/// submits at the position the preceding steps reached.</summary>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public abstract record TestStepNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary><c>ticks N</c> — carries the tick cursor forward, which is how later steps and the expectation's own
/// firing tick are placed.</summary>
/// <param name="Ticks">How many simulation ticks to advance.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestTicksStepNode(
    long Ticks,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : TestStepNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary><c>seat&lt;n&gt;: &lt;command line&gt;</c> — one console/ingress line the named seat submits at the tick
/// the preceding steps reached.</summary>
/// <param name="Seat">The 1-based seat the line acts as.</param>
/// <param name="Command">The command line, verbatim to the end of its own line.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestSeatStepNode(
    int Seat,
    string Command,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : TestStepNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary>One <c>expect</c> line: a gate expression the generated test world decides at its last reached
/// tick.</summary>
/// <param name="Predicate">The expectation, in the rule gate grammar.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestExpectationNode(
    PredicateNode Predicate,
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
/// <summary>A test's <c>given</c> block — the cells the generated test world boots with.</summary>
/// <param name="Cells">The assignments, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestGivenBlockNode(
    IReadOnlyList<TestGivenNode> Cells,
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
/// <summary>A test's <c>when</c> block — the tick grid and the commands laid out along it.</summary>
/// <param name="Steps">The steps, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestWhenBlockNode(
    IReadOnlyList<TestStepNode> Steps,
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
/// <summary>A test's <c>expect</c> block — one expectation per line.</summary>
/// <param name="Expectations">The expectations, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestExpectBlockNode(
    IReadOnlyList<TestExpectationNode> Expectations,
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
/// <summary><c>test "name" { given { } when { } expect { } }</c> — a world's behaviour stated in the world's own
/// language. The enclosing document carries no trace of it; each test lowers to a generated test world of its
/// own.</summary>
/// <param name="Name">The test's name, which names its generated world and its verdict rows.</param>
/// <param name="Given">The <c>given</c> block, or <see langword="null"/> when the test authored none.</param>
/// <param name="When">The <c>when</c> block, or <see langword="null"/> when the test authored none.</param>
/// <param name="Expect">The <c>expect</c> block, or <see langword="null"/> for a test that authored none — refused
/// by name, since a test with no expectation answers nothing.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestDeclarationNode(
    string Name,
    TestGivenBlockNode? Given,
    TestWhenBlockNode? When,
    TestExpectBlockNode? Expect,
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
