using System.Globalization;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: <c>boardCombine</c>'s sources and <c>writeSet</c>'s set read a declared cell set by
/// its bare name, lowered at the width of the written board, and get exactly the set's members; a name that is no
/// row and no set, a name that is both, a set addressing a different width, and a set given a <c>setKey</c> each
/// refuse by name.</summary>
public sealed class CellSetTransformLawTests {
    private const long Member = 5L;
    private const long Untouched = 9L;

    // Every stone value 0..2 appears across the board, in a pattern that crosses each 64-cell word boundary.
    private static long Stone(int cell) => (((cell * 7) + (cell / 3)) % 3);
    // The set every law declares, decided cell by cell from the seeded value alone: board(stones, 1..2) minus
    // board(stones, 2..2) is exactly the cells holding 1.
    private static bool Contested(int cell) => (Stone(cell: cell) == 1L);
    private static StateCell Cell(int cell, long value) => TransformFixture.Cell(
        key: cell.ToString(provider: CultureInfo.InvariantCulture),
        value: value
    );
    private static StateRow Board(string name, string topology, IReadOnlyList<StateCell>? cells = null) => new(
        Name: TransformFixture.Name(value: name),
        Kind: CellKind.Int,
        Domain: new StateDomain.CellsOf(
            Empty: 0L,
            Topology: topology
        ),
        Cells: cells
    );
    private static LatticeTopology.Grid Grid(string name, int width, int depth) => new(
        Name: name,
        Origin: new DocumentVector3(
            x: 0f,
            y: 0f,
            z: 0f
        ),
        CellSize: 1f,
        Width: width,
        Depth: depth
    );
    private static CellSetRow Set(string name, string spelling) {
        Assert.True(
            condition: CellSetSpelling.TryParse(
                error: out var error,
                expression: out var expression,
                text: spelling
            ),
            userMessage: error
        );

        return new CellSetRow(
            Name: TransformFixture.Name(value: name),
            Set: expression!
        );
    }
    private static StateSection Section(int width, int depth) {
        var cells = (width * depth);
        var stones = new List<StateCell>();
        var untouched = new List<StateCell>();
        var rangeOneTwo = new List<StateCell>();
        var rangeTwo = new List<StateCell>();

        for (var cell = 0; (cell < cells); cell++) {
            var stone = Stone(cell: cell);

            if (stone != 0L) {
                stones.Add(item: Cell(cell: cell, value: stone));
                rangeOneTwo.Add(item: Cell(cell: cell, value: 1L));
            }
            if (stone == 2L) {
                rangeTwo.Add(item: Cell(cell: cell, value: 1L));
            }

            untouched.Add(item: Cell(cell: cell, value: Untouched));
        }

        return new StateSection(
            Lattices: [
                Grid(depth: depth, name: "grid", width: width),
                Grid(depth: depth, name: "other", width: (width + 1)),
            ],
            Rows: [
                Board(cells: stones, name: "stones", topology: "grid"),
                Board(name: "painted", topology: "grid"),
                Board(name: "inline", topology: "grid"),
                Board(cells: untouched, name: "canvas", topology: "grid"),
                Board(cells: rangeOneTwo, name: "rangeOneTwo", topology: "grid"),
                Board(cells: rangeTwo, name: "rangeTwo", topology: "grid"),
                Board(name: "wider", topology: "other"),
                EvaluatorFixture.Slot(name: "mask", value: 0L),
            ]
        );
    }
    private static IReadOnlyList<CellSetRow> Sets() => [
        Set(name: "contested", spelling: "board(stones, 1..2) & ~board(stones, 2..2)"),
        Set(name: "farther", spelling: "board(wider, 0..0)"),
        Set(name: "stones", spelling: "all"),
    ];
    private static (ArenaEffectHost Host, RuleCompileContext Context) Arrange(int width, int depth) {
        var section = Section(depth: depth, width: width);
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        ) {
            Sets = Sets(),
        };

        return (new ArenaEffectHost(arena: new StateArena(
            catalog: context.Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        )), context);
    }
    private static bool Apply(ArenaEffectHost host, RuleCompileContext context, StateTransform transform, out EffectRefusal refusal) {
        Assert.True(
            condition: RuleCompiler.TryResolveTransform(
                context: context,
                reason: out var reason,
                resolved: out var resolved,
                transform: transform
            ),
            userMessage: reason
        );

        return host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out _,
            refusal: out refusal,
            transform: resolved
        );
    }
    private static long[] Board(ArenaEffectHost host, RuleCompileContext context, string row) {
        var ordinal = TransformFixture.Ordinal(
            context: context,
            name: row
        );
        var values = new long[host.Arena.Layout[ordinal].CellCapacity];

        Assert.True(condition: host.Arena.TryReadBoard(
            rowOrdinal: ordinal,
            values: values
        ));

        return values;
    }
    private static string Unresolved(RuleCompileContext context, StateTransform transform) {
        Assert.False(condition: RuleCompiler.TryResolveTransform(
            context: context,
            reason: out var reason,
            resolved: out _,
            transform: transform
        ));

        return reason;
    }

    // 64 fills one word exactly, 256 the whole inline prefix, 361 is a 19x19 board, and 594 the widest shipped
    // per-noun board.
    public static TheoryData<int, int> Widths() => new() {
        { 8, 8 },
        { 16, 16 },
        { 19, 19 },
        { 33, 18 },
    };
    [MemberData(nameof(Widths))]
    [Theory]
    public void ABoardCombineCopyOfADeclaredSetPaintsExactlyItsMembers(int width, int depth) {
        var (host, context) = Arrange(depth: depth, width: width);

        Assert.True(
            condition: Apply(
                context: context,
                host: host,
                refusal: out var refusal,
                transform: new StateTransform.BoardCombine(
                    Left: "contested",
                    Operation: BoardCombineOp.Copy,
                    Row: "painted",
                    Value: Member
                )
            ),
            userMessage: refusal.Reason
        );

        var painted = Board(context: context, host: host, row: "painted");

        for (var cell = 0; (cell < painted.Length); cell++) {
            Assert.Equal(
                actual: painted[cell],
                expected: (Contested(cell: cell) ? Member : 0L)
            );
        }
    }
    [MemberData(nameof(Widths))]
    [Theory]
    public void ADeclaredSetAgreesWithTheSameAlgebraWrittenOverBoardRows(int width, int depth) {
        // The same set spelled without the algebra: rangeOneTwo holds a member wherever a stone is 1 or 2, rangeTwo
        // wherever it is 2, and AndNot over the two rows is board(stones, 1..2) & ~board(stones, 2..2). The set is
        // read as the right side of an And with rangeOneTwo, which holds every member of it, so a row and a set
        // combine the way two rows do.
        var (host, context) = Arrange(depth: depth, width: width);

        Assert.True(condition: Apply(
            context: context,
            host: host,
            refusal: out _,
            transform: new StateTransform.BoardCombine(
                Left: "rangeOneTwo",
                Operation: BoardCombineOp.AndNot,
                Right: "rangeTwo",
                Row: "inline",
                Value: Member
            )
        ));
        Assert.True(condition: Apply(
            context: context,
            host: host,
            refusal: out var refusal,
            transform: new StateTransform.BoardCombine(
                Left: "rangeOneTwo",
                Operation: BoardCombineOp.And,
                Right: "contested",
                Row: "painted",
                Value: Member
            )
        ), userMessage: refusal.Reason);

        Assert.Equal(
            actual: Board(context: context, host: host, row: "painted"),
            expected: Board(context: context, host: host, row: "inline")
        );
    }
    [MemberData(nameof(Widths))]
    [Theory]
    public void AWriteSetOfADeclaredSetWritesItsMembersAndLeavesEveryOtherCell(int width, int depth) {
        var (host, context) = Arrange(depth: depth, width: width);

        Assert.True(
            condition: Apply(
                context: context,
                host: host,
                refusal: out var refusal,
                transform: new StateTransform.WriteSet(
                    Row: "canvas",
                    Set: "contested",
                    Value: Member
                )
            ),
            userMessage: refusal.Reason
        );

        var canvas = Board(context: context, host: host, row: "canvas");

        for (var cell = 0; (cell < canvas.Length); cell++) {
            Assert.Equal(
                actual: canvas[cell],
                expected: (Contested(cell: cell) ? Member : Untouched)
            );
        }
    }
    [Fact]
    public void ADeclaredSetPaintsWhatTheSameAlgebraInlineInAnExpressionPaints() {
        // The expression language spells the same set as a 64-bit mask on a board of at most 64 cells; a rule writes
        // that mask into a slot and paints it, and a second rule paints the declared set.
        var section = Section(depth: 8, width: 8);
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        ) {
            Sets = Sets(),
        };
        var rules = RuleCompiler.CompileAll(
            context: context,
            rules: [new Rule(
                Name: TransformFixture.Name(value: "paint"),
                Effects: [
                    new ActionEffect.SetState(
                        Expression: RulesFixture.Program(text: "board(mask, stones, 1, 2) & ~board(mask, stones, 2, 2)"),
                        State: "mask"
                    ),
                    new ActionEffect.TransformState(Transform: new StateTransform.WriteSet(
                        Row: "inline",
                        Set: "mask",
                        Value: Member
                    )),
                    new ActionEffect.TransformState(Transform: new StateTransform.WriteSet(
                        Row: "painted",
                        Set: "contested",
                        Value: Member
                    )),
                ],
                Mode: ActionTriggerMode.Level
            )]
        );
        var host = new ArenaEffectHost(arena: new StateArena(
            catalog: context.Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));
        var evaluator = new RuleEvaluator(host: host);

        Assert.True(condition: evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Empty(collection: evaluator.Diagnostics());

        var painted = Board(context: context, host: host, row: "painted");

        Assert.Contains(collection: painted, expected: Member);
        Assert.Equal(
            actual: painted,
            expected: Board(context: context, host: host, row: "inline")
        );
    }
    [Fact]
    public void ARuleReadingADeclaredSetReadsEveryRowItsSourcesName() {
        var (_, context) = Arrange(depth: 8, width: 8);
        var rule = RuleCompiler.Compile(
            context: context,
            rule: new Rule(
                Name: TransformFixture.Name(value: "paint"),
                Effects: [new ActionEffect.TransformState(Transform: new StateTransform.BoardCombine(
                    Left: "contested",
                    Operation: BoardCombineOp.Copy,
                    Row: "painted"
                ))],
                Mode: ActionTriggerMode.Level
            )
        );
        var reads = new List<CellAccess>();

        rule.CollectReads(into: reads);

        Assert.Contains(
            collection: reads,
            filter: read => (read.RowOrdinal == TransformFixture.Ordinal(context: context, name: "stones"))
        );
    }
    [Fact]
    public void ANameThatIsNeitherARowNorADeclaredSetRefusesByName() {
        var (_, context) = Arrange(depth: 8, width: 8);

        Assert.Contains(
            actualString: Unresolved(context: context, transform: new StateTransform.BoardCombine(
                Left: "nowhere",
                Operation: BoardCombineOp.Copy,
                Row: "painted"
            )),
            expectedSubstring: "'nowhere', which is neither a state row nor a declared set"
        );
        Assert.Contains(
            actualString: Unresolved(context: context, transform: new StateTransform.WriteSet(
                Row: "painted",
                Set: "nowhere",
                Value: Member
            )),
            expectedSubstring: "'nowhere', which is neither a state row nor a declared set"
        );
    }
    [Fact]
    public void ANameThatIsBothARowAndADeclaredSetRefusesByName() {
        var (_, context) = Arrange(depth: 8, width: 8);

        Assert.Contains(
            actualString: Unresolved(context: context, transform: new StateTransform.BoardCombine(
                Left: "stones",
                Operation: BoardCombineOp.Copy,
                Row: "painted"
            )),
            expectedSubstring: "'stones', which is both a state row and a declared set"
        );
        Assert.Equal(
            actual: Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(
                context: context,
                rule: new Rule(
                    Name: TransformFixture.Name(value: "paint"),
                    Effects: [new ActionEffect.TransformState(Transform: new StateTransform.WriteSet(
                        Row: "painted",
                        Set: "stones",
                        Value: Member
                    ))],
                    Mode: ActionTriggerMode.Level
                )
            )).Refusal,
            expected: RuleRefusal.EffectKindInadmissible
        );
    }
    [Fact]
    public void ADeclaredSetGivenASetKeyRefusesByName() {
        var (_, context) = Arrange(depth: 8, width: 8);

        Assert.Contains(
            actualString: Unresolved(context: context, transform: new StateTransform.WriteSet(
                Row: "painted",
                Set: "contested",
                SetKey: "a",
                Value: Member
            )),
            expectedSubstring: "declared set 'contested', which has no cell for 'setKey' to address"
        );
    }
    [Fact]
    public void ADeclaredSetOverAnotherWidthRefusesByItsNameAndWritesNothing() {
        var (host, context) = Arrange(depth: 8, width: 8);
        var before = Board(context: context, host: host, row: "canvas");

        Assert.False(condition: Apply(
            context: context,
            host: host,
            refusal: out var combine,
            transform: new StateTransform.BoardCombine(
                Left: "farther",
                Operation: BoardCombineOp.Copy,
                Row: "canvas",
                Value: Member
            )
        ));
        Assert.Equal(
            actual: Assert.IsType<TransformRefusal>(@object: combine.Code),
            expected: TransformRefusal.BoardCombineOperands
        );
        Assert.Contains(
            actualString: combine.Reason,
            expectedSubstring: "declared set 'farther' over a board of 64 cells"
        );
        Assert.False(condition: Apply(
            context: context,
            host: host,
            refusal: out var write,
            transform: new StateTransform.WriteSet(
                Row: "canvas",
                Set: "farther",
                Value: Member
            )
        ));
        Assert.Equal(
            actual: Assert.IsType<TransformRefusal>(@object: write.Code),
            expected: TransformRefusal.WriteSetSource
        );
        Assert.Contains(
            actualString: write.Reason,
            expectedSubstring: "declared set 'farther'"
        );
        Assert.Equal(
            actual: Board(context: context, host: host, row: "canvas"),
            expected: before
        );
    }
}
