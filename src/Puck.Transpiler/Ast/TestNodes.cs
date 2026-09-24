namespace Puck.Transpiler.Ast;

/// <summary>One entry of a test's <c>given</c> block: a cell line, or a world of the composition holding cell
/// lines of its own.</summary>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public abstract record TestGivenItemNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(
    Offset,
    Length,
    Line,
    Column
);
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
) : TestGivenItemNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary><c>world { row = literal … }</c> inside a test's <c>given</c> block — the cells boot in the named
/// world of the composition the test stands at the root of.</summary>
/// <param name="World">The world's own name, as its <c>world</c> declaration spells it.</param>
/// <param name="Cells">The cell lines, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestGivenWorldNode(
    string World,
    IReadOnlyList<TestGivenNode> Cells,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : TestGivenItemNode(
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
/// the preceding steps reached; <c>seat&lt;n&gt; refused ["text"]: &lt;command line&gt;</c> declares that the world
/// refuses it.</summary>
/// <param name="Seat">The 1-based seat the line acts as.</param>
/// <param name="Command">The command line, verbatim to the end of its own line.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
/// <param name="Refused">Whether the step declares that the ingress refuses the line.</param>
/// <param name="Refusal">Text the recorded refusal must contain, or <see langword="null"/> for any refusal; written only
/// beside <paramref name="Refused"/>.</param>
public sealed record TestSeatStepNode(
    int Seat,
    string Command,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1,
    bool Refused = false,
    string? Refusal = null
) : TestStepNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary><c>world { seat&lt;n&gt;: &lt;command line&gt; … }</c> inside a test's <c>when</c> block — the steps are
/// submitted into the named world of the composition the test stands at the root of, at the tick the shared grid
/// cursor has reached.</summary>
/// <param name="World">The world's own name, as its <c>world</c> declaration spells it.</param>
/// <param name="Steps">The seat steps, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestWhenWorldNode(
    string World,
    IReadOnlyList<TestSeatStepNode> Steps,
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
/// <summary>One entry of a test's <c>expect</c> block: a gate line, or a world of the composition holding gate
/// lines of its own.</summary>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public abstract record TestExpectItemNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(
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
) : TestExpectItemNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary><c>world { gate … }</c> inside a test's <c>expect</c> block — each gate becomes a verdict rule in the
/// named world's own generated document, decided at the tick that world reaches when the run's shared export step
/// is taken.</summary>
/// <param name="World">The world's own name, as its <c>world</c> declaration spells it.</param>
/// <param name="Expectations">The gate lines, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestExpectWorldNode(
    string World,
    IReadOnlyList<TestExpectationNode> Expectations,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : TestExpectItemNode(
    Offset,
    Length,
    Line,
    Column
);
/// <summary>A test's <c>given</c> block — the cells the generated test world boots with.</summary>
/// <param name="Cells">The assignments and world blocks, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestGivenBlockNode(
    IReadOnlyList<TestGivenItemNode> Cells,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(
    Offset,
    Length,
    Line,
    Column
) {
    /// <summary>Gets every cell line the block carries, each with the world it is addressed to, or
    /// <see langword="null"/> for a line written outside every world block.</summary>
    public IEnumerable<(string? World, TestGivenNode Cell)> Lines => Cells.SelectMany(selector: static item => (item switch {
        TestGivenNode cell => [(((string?)null), cell)],
        TestGivenWorldNode addressed => addressed.Cells.Select(selector: cell => (((string?)addressed.World), cell)),
        _ => Enumerable.Empty<(string?, TestGivenNode)>(),
    }));
}
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
) {
    /// <summary>Gets every step the block carries in written order, each with the world it is addressed to, or
    /// <see langword="null"/> for a step written outside every world block.</summary>
    public IEnumerable<(string? World, TestStepNode Step)> Lines => Steps.SelectMany(selector: static step => (step switch {
        TestWhenWorldNode addressed => addressed.Steps.Select(selector: inner => (((string?)addressed.World), ((TestStepNode)inner))),
        _ => [(((string?)null), step)],
    }));
}
/// <summary>A test's <c>expect</c> block — one expectation per line.</summary>
/// <param name="Expectations">The expectations and world blocks, in written order.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestExpectBlockNode(
    IReadOnlyList<TestExpectItemNode> Expectations,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(
    Offset,
    Length,
    Line,
    Column
) {
    /// <summary>Gets every gate line the block carries, each with the world it is addressed to, or
    /// <see langword="null"/> for a line written outside every world block.</summary>
    public IEnumerable<(string? World, TestExpectationNode Expectation)> Lines => Expectations.SelectMany(selector: static item => (item switch {
        TestExpectationNode expectation => [(((string?)null), expectation)],
        TestExpectWorldNode addressed => addressed.Expectations.Select(selector: expectation => (((string?)addressed.World), expectation)),
        _ => Enumerable.Empty<(string?, TestExpectationNode)>(),
    }));
}
/// <summary><c>test "name" [with module(arguments)] { given { } when { } expect { } }</c> — behaviour stated in the
/// world's own language. The enclosing document carries no trace of it; each test lowers to a generated test world
/// of its own.</summary>
/// <param name="Name">The test's name, which names its generated world and its verdict rows.</param>
/// <param name="Given">The <c>given</c> block, or <see langword="null"/> when the test authored none.</param>
/// <param name="When">The <c>when</c> block, or <see langword="null"/> when the test authored none.</param>
/// <param name="Expect">The <c>expect</c> block, or <see langword="null"/> for a test that authored none — refused
/// by name, since a test with no expectation answers nothing.</param>
/// <param name="Subject">The module invocation the test is about, or <see langword="null"/> for a test whose
/// subject is the world it stands in.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TestDeclarationNode(
    string Name,
    TestGivenBlockNode? Given,
    TestWhenBlockNode? When,
    TestExpectBlockNode? Expect,
    CallExpressionNode? Subject = null,
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
