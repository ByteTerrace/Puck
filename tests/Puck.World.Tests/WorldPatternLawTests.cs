using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the pattern-language operand over its three word sources, complement and intersection, the sort that
/// canonicalizes a hand, the state budget's refusal, and the strict wire shape.</summary>
public sealed class WorldPatternLawTests {
    private static WorldDefinition Apply(WorldDefinition definition, StateTransform transform) {
        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                definition,
                transform,
                WorldPrincipal.World,
                1,
                "test",
                out var candidate,
                out var reason
            ),
            userMessage: reason
        );
        return candidate!;
    }
    private static PatternRow Bracket(string name, bool single = false) => new(
        Name(value: name),
        CellKind.Int,
        Symbols: [new(
                Name(value: "me"),
                1,
                1
            ), new(
                Name(value: "them"),
                2,
                2
            )],
        Pattern: new PatternNode.Sequence(Items: [(single
        ? new PatternNode.Symbol(Name: "them")
        : new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "them"))), new PatternNode.Symbol(Name: "me")])
    );
    private static StateCell Cell(string key, long value = 1, CellKind kind = CellKind.Int) => new(
        Name(value: key),
        ((kind == CellKind.Bool) ? CellValue.Bool(value: (value != 0)) : CellValue.Int(value: value))
    );
    private static WorldDefinition Document(WorldStateRow[] rows, PatternRow[] patterns, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(
        World: rows,
        Lattices: [new LatticeTopology.Grid(
                "map",
                new DocumentVector3(
                    x: 0,
                    y: 0,
                    z: 0
                ),
                1,
                4,
                4
            )]
    ),
        PatternsRaw = patterns,
        Rules = rules,
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static WorldDefinition Hand() {
        var straight = new PatternRow(
            Name(value: "straight"),
            CellKind.Int,
            Attribute: "rank",
            Symbols: Enumerable.Range(
                count: 5,
                start: 5
            ).Select(selector: i => new PatternSymbol(
                Name(value: $"r{i}"),
                i,
                i
            )).ToArray(),
            Pattern: new PatternNode.Sequence(Items: [.. Enumerable.Range(
                    count: 5,
                    start: 5
                ).Select(selector: i => ((PatternNode)new PatternNode.Symbol(Name: $"r{i}")))])
        );

        return Document(
            [
            new(
                    Name(value: "cards"),
                    CellKind.Int,
                    Capacity: 5,
                    Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4"), Cell("c5")]
                ),
            new(
                    Name(value: "rank"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Cells: [Cell(
                            key: "c1",
                            value: 9
                        ), Cell(
                            key: "c2",
                            value: 5
                        ), Cell(
                            key: "c3",
                            value: 7
                        ), Cell(
                            key: "c4",
                            value: 6
                        ), Cell(
                            key: "c5",
                            value: 8
                        )]
                ),
            new(
                    Name(value: "suit"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Cells: [Cell(
                            key: "c1",
                            value: 1
                        ), Cell(
                            key: "c2",
                            value: 2
                        ), Cell(
                            key: "c3",
                            value: 1
                        ), Cell(
                            key: "c4",
                            value: 2
                        ), Cell(
                            key: "c5",
                            value: 1
                        )]
                ),
            new(
                    Name(value: "hand"),
                    CellKind.Bool,
                    Capacity: 5,
                    Cells: [Cell("c1", kind: CellKind.Bool), Cell("c2", kind: CellKind.Bool), Cell("c3", kind: CellKind.Bool), Cell("c4", kind: CellKind.Bool), Cell("c5", kind: CellKind.Bool)],
                    Domain: new StateDomain.KeysOf(
                        CellName.Parse(candidate: "cards"),
                        Ordered: true
                    )
                ),
            Slot(name: "straight"),
        ],
            [straight],
            [Mirror(
                    "straight",
                    "$match:straight:hand"
                )]
        );
    }
    private static WorldRule Mirror(string target, string operand, string? key = null) => new(
        Name(value: (target + "-mirror")),
        [new ActionEffect.SetState(
                State: target,
                FromState: operand,
                FromKey: key
            )]
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldStateRow Slot(string name) => new(
        Name(value: name),
        CellKind.Int,
        Cells: [new StateCell(
                WorldStateRow.SlotKey,
                CellValue.Int(value: 0L)
            )]
    );
    private static long Value(WorldFixture fixture, string row) =>
        StateRows.FindCell(
            cells: WorldDefinitionRows.FindStateRow(
                fixture.Server.Definition.State,
                row
            )!.Cells,
            key: WorldStateRow.SlotKey
        )!.Value.Raw;

    [Fact]
    public void ABoardRayIsAWordAndAFlankIsARegularPattern() {
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                ), Cell(
                    key: "2",
                    value: 2
                ), Cell(
                    key: "3",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            [board, Slot(name: "flank"), Slot(name: "south"), Slot(name: "narrow")],
            [Bracket("bracket"), Bracket(
                    name: "tight",
                    single: true
                )],
            [
                Mirror(
                    key: "0",
                    operand: "$match:bracket:board:E",
                    target: "flank"
                ),
                Mirror(
                    key: "0",
                    operand: "$match:bracket:board:S",
                    target: "south"
                ),
                Mirror(
                    key: "0",
                    operand: "$match:tight:board:E",
                    target: "narrow"
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "flank"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "south"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "narrow"
            )
        );
        Assert.Contains(
            "bracket kind=Int letters=3 states=",
            fixture.Server.DescribePatterns()
        );
        var narrated = fixture.Server.DescribeMatch(
            attribute: null,
            direction: "E",
            key: "0",
            patternName: "bracket",
            rowName: "board"
        );

        Assert.Contains(
            actualString: narrated,
            expectedSubstring: "accept=1 prefix=3"
        );
        Assert.Contains(
            actualString: narrated,
            expectedSubstring: "them"
        );
        Assert.Contains(
            "direction",
            fixture.Server.DescribeMatch(
                attribute: null,
                direction: "any",
                key: "0",
                patternName: "bracket",
                rowName: "board"
            )
        );
        Assert.Contains(
            "names no pattern",
            fixture.Server.DescribeMatch(
                attribute: null,
                direction: "E",
                key: "0",
                patternName: "missing",
                rowName: "board"
            )
        );
    }
    [Fact]
    public void AComplementAndAnIntersectionAreSinglePatterns() {
        PatternNode Contains(string symbol) => new PatternNode.Sequence(Items: [new PatternNode.Star(Item: new PatternNode.AnySymbol()), new PatternNode.Symbol(Name: symbol), new PatternNode.Star(Item: new PatternNode.AnySymbol())]);
        PatternNode Adjacent(string symbol) => new PatternNode.Sequence(Items: [new PatternNode.Star(Item: new PatternNode.AnySymbol()), new PatternNode.Symbol(Name: symbol), new PatternNode.Symbol(Name: symbol), new PatternNode.Star(Item: new PatternNode.AnySymbol())]);
        var symbols = Enumerable.Range(
            count: 6,
            start: 1
        ).Select(selector: i => new PatternSymbol(
            Name(value: $"p{i}"),
            i,
            i
        )).ToArray();
        var dice = new WorldStateRow(
            Name(value: "dice"),
            CellKind.Int,
            Capacity: 5,
            Cells: [Cell(
                    key: "d1",
                    value: 4
                ), Cell(
                    key: "d2",
                    value: 2
                ), Cell(
                    key: "d3",
                    value: 6
                ), Cell(
                    key: "d4",
                    value: 6
                ), Cell(
                    key: "d5",
                    value: 5
                )]
        );
        var definition = Document(
            [dice, Slot(name: "pair"), Slot(name: "noSix"), Slot(name: "twoAndFive")],
            [
            new(
                    Name(value: "pair"),
                    CellKind.Int,
                    Symbols: symbols,
                    Pattern: new PatternNode.Choice(Items: [.. Enumerable.Range(
                            count: 6,
                            start: 1
                        ).Select(selector: i => Adjacent(symbol: $"p{i}"))])
                ),
            new(
                    Name(value: "noSix"),
                    CellKind.Int,
                    Symbols: symbols,
                    Pattern: new PatternNode.Complement(Item: Contains(symbol: "p6"))
                ),
            new(
                    Name(value: "twoAndFive"),
                    CellKind.Int,
                    Symbols: symbols,
                    Pattern: new PatternNode.Both(Items: [Contains(symbol: "p2"), Contains(symbol: "p5")])
                ),
        ],
            [Mirror(
                    "pair",
                    "$match:pair:dice"
                ), Mirror(
                    "noSix",
                    "$match:noSix:dice"
                ), Mirror(
                    "twoAndFive",
                    "$match:twoAndFive:dice"
                )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "pair"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "noSix"
            )
        );
        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "twoAndFive"
            )
        );
    }
    [Fact]
    public void ASortedHandReadsItsAttributeWordAndAStraightMatchesOnlyAfterSorting() {
        var unsorted = Hand();
        var sorted = Apply(
            definition: unsorted,
            transform: new StateTransform.SortZone(
                "hand",
                By: [new("rank")]
            )
        );

        Assert.Equal(
            new[] { "c2", "c4", "c3", "c5", "c1" },
            Find(
                document: sorted,
                row: "hand"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        var descending = Apply(
            definition: unsorted,
            transform: new StateTransform.SortZone(
                "hand",
                By: [new(
                        "rank",
                        Descending: true
                    )]
            )
        );

        Assert.Equal(
            "c1",
            Find(
                document: descending,
                row: "hand"
            ).Cells![0].Key.Value
        );
        // Suit first, rank descending inside a suit, stable across cards with equal keys.
        var suited = Apply(
            definition: unsorted,
            transform: new StateTransform.SortZone(
                "hand",
                By: [new("suit"), new(
                        "rank",
                        Descending: true
                    )]
            )
        );

        Assert.Equal(
            new[] { "c1", "c5", "c3", "c4", "c2" },
            Find(
                document: suited,
                row: "hand"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        // Control: no attribute keys at all is not "sort by nothing" — it refuses.
        Assert.False(condition: WorldArenaTransforms.TryApply(
            unsorted,
            new StateTransform.SortZone(
                "hand",
                By: []
            ),
            WorldPrincipal.World,
            0,
            "test",
            out _,
            out var flagReason
        ));
        Assert.Contains(
            actualString: flagReason,
            expectedSubstring: "attribute keys"
        );
        // An attribute keyed over another token domain never sorts silently as zeroes.
        var foreign = unsorted with {
            StateRaw = unsorted.StateRaw! with {
                World = [.. unsorted.State,
            new(
                Name(value: "seats"),
                CellKind.Int,
                Capacity: 2,
                Cells: [Cell("s1"), Cell("s2")]
            ),
            new(
                Name(value: "score"),
                CellKind.Int,
                Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "seats")),
                Cells: [Cell(
                        key: "s1",
                        value: 3
                    ), Cell(
                        key: "s2",
                        value: 1
                    )]
            )],
            },
        };

        Assert.False(condition: WorldArenaTransforms.TryApply(
            foreign,
            new StateTransform.SortZone(
                "hand",
                By: [new("score")]
            ),
            WorldPrincipal.World,
            0,
            "test",
            out _,
            out var domainReason
        ));
        Assert.Contains(
            actualString: domainReason,
            expectedSubstring: "token domain"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: foreign with {
                Rules = [new WorldRule(
                    Name(value: "bad"),
                    [new ActionEffect.TransformState(Transform: new StateTransform.SortZone(
                            "hand",
                            By: [new("score")]
                        ))]
                )],
            },
            reason: out var compileReason
        ));
        Assert.Contains(
            actualString: compileReason,
            expectedSubstring: "token domain"
        );
        var foreignPattern = new PatternRow(
            Name(value: "far"),
            CellKind.Int,
            Attribute: "score",
            Symbols: [new(
                    Name(value: "one"),
                    1,
                    1
                )],
            Pattern: new PatternNode.Symbol(Name: "one")
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: foreign with {
                PatternsRaw = [.. foreign.Patterns, foreignPattern],
                Rules = [Mirror(
                    "straight",
                    "$match:far:hand"
                )],
            },
            reason: out var attributeReason
        ));
        Assert.Contains(
            actualString: attributeReason,
            expectedSubstring: "token domain"
        );

        using var before = Fixtures.FreshServer(definition: unsorted);

        before.Step();
        Assert.Equal(
            0L,
            Value(
                fixture: before,
                row: "straight"
            )
        );

        using var after = Fixtures.FreshServer(definition: sorted);

        after.Step();
        Assert.Equal(
            1L,
            Value(
                fixture: after,
                row: "straight"
            )
        );
    }
    [Fact]
    public void ASortedTrayMatchesAChoiceOfSequencesAndAKeyedRowSortsByItsOwnValues() {
        var dice = new WorldStateRow(
            Name(value: "dice"),
            CellKind.Int,
            Capacity: 5,
            Cells: [Cell(
                    key: "d1",
                    value: 4
                ), Cell(
                    key: "d2",
                    value: 2
                ), Cell(
                    key: "d3",
                    value: 6
                ), Cell(
                    key: "d4",
                    value: 3
                ), Cell(
                    key: "d5",
                    value: 5
                )]
        );
        var large = new PatternRow(
            Name(value: "large"),
            CellKind.Int,
            Symbols: Enumerable.Range(
                count: 6,
                start: 1
            ).Select(selector: i => new PatternSymbol(
                Name(value: $"p{i}"),
                i,
                i
            )).ToArray(),
            Pattern: new PatternNode.Choice(Items: [
                new PatternNode.Sequence(Items: [.. Enumerable.Range(
                        count: 5,
                        start: 1
                    ).Select(selector: i => ((PatternNode)new PatternNode.Symbol(Name: $"p{i}")))]),
                new PatternNode.Sequence(Items: [.. Enumerable.Range(
                        count: 5,
                        start: 2
                    ).Select(selector: i => ((PatternNode)new PatternNode.Symbol(Name: $"p{i}")))]),
            ])
        );
        var definition = Document(
            [dice, Slot(name: "hit")],
            [large],
            [Mirror(
                    "hit",
                    "$match:large:dice"
                )]
        );

        using var unsorted = Fixtures.FreshServer(definition: definition);

        unsorted.Step();
        Assert.Equal(
            0L,
            Value(
                fixture: unsorted,
                row: "hit"
            )
        );

        var sorted = Apply(
            definition: definition,
            transform: new StateTransform.SortKeyed("dice")
        );

        Assert.Equal(
            new[] { 2L, 3L, 4L, 5L, 6L },
            Find(
                document: sorted,
                row: "dice"
            ).Cells!.Select(selector: c => c.Value.Raw)
        );
        using var fixture = Fixtures.FreshServer(definition: sorted);

        fixture.Step();
        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "hit"
            )
        );

        // Control: a plain slot row carries no keyed cells to order.
        Assert.False(condition: WorldArenaTransforms.TryApply(
            definition,
            new StateTransform.SortKeyed("hit"),
            WorldPrincipal.World,
            0,
            "test",
            out _,
            out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "keyed or ordered numeric row"
        );
    }
    [Fact]
    public void AExpressionProgramReadsATupleOfAttributesPerTokenAndTokenBindsOnlyThere() {
        // suit * 16 + rank: hearts are suit 1, so a heart of any rank lies in 16..31 and a heart flush is h{5}.
        ExpressionProgram Tuple() => new(Instructions: [
            Instruction.Operand(key: "$token",
                name: "suit"
            ), Instruction.Constant(value: 16m), Instruction.Of(operation: ExpressionOp.Multiply),
            Instruction.Operand(key: "$token",
                name: "rank"
            ), Instruction.Of(operation: ExpressionOp.Add),
        ]);
        var flush = new PatternRow(
            Name(value: "hearts"),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "h"),
                    16,
                    31
                )],
            Pattern: new PatternNode.Repeat(
                new PatternNode.Symbol(Name: "h"),
                5,
                5
            ),
            Value: Tuple()
        );
        var straightFlush = new PatternRow(
            Name(value: "royal"),
            CellKind.Int,
            Symbols: [.. Enumerable.Range(
                    count: 5,
                    start: 5
                ).Select(selector: r => new PatternSymbol(
                    Name(value: $"h{r}"),
                    (16 + r),
                    (16 + r)
                ))],
            Pattern: new PatternNode.Sequence(Items: [.. Enumerable.Range(
                    count: 5,
                    start: 5
                ).Select(selector: r => ((PatternNode)new PatternNode.Symbol(Name: $"h{r}")))]),
            Value: Tuple()
        );
        var suited = Hand() with {
            StateRaw = Hand().StateRaw! with {
                World = [.. Hand().State.Where(predicate: r => (r.Name.Value != "straight")).Select(selector: r => ((r.Name.Value == "suit")
            ? r with { Cells = [Cell(
                        key: "c1",
                        value: 1
                    ), Cell(
                        key: "c2",
                        value: 1
                    ), Cell(
                        key: "c3",
                        value: 1
                    ), Cell(
                        key: "c4",
                        value: 1
                    ), Cell(
                        key: "c5",
                        value: 1
                    )] }
            : r)),
            Slot(name: "flush"), Slot(name: "royal")],
            },
        };
        var definition = suited with {
            PatternsRaw = [flush, straightFlush],
            Rules = [Mirror(
                "flush",
                "$match:hearts:hand"
            ), Mirror(
                "royal",
                "$match:royal:hand"
            )],
        };

        using var unsorted = Fixtures.FreshServer(definition: definition);

        unsorted.Step();
        Assert.Equal(
            1L,
            Value(
                fixture: unsorted,
                row: "flush"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: unsorted,
                row: "royal"
            )
        );
        Assert.Contains(
            "accept=1",
            unsorted.Server.DescribeMatch(
                attribute: null,
                direction: null,
                key: null,
                patternName: "hearts",
                rowName: "hand"
            )
        );

        var sorted = Apply(
            definition: definition,
            transform: new StateTransform.SortZone(
                "hand",
                By: [new("rank")]
            )
        );
        using var ordered = Fixtures.FreshServer(definition: sorted);

        ordered.Step();
        Assert.Equal(
            1L,
            Value(
                fixture: ordered,
                row: "royal"
            )
        );

        var mixed = definition with {
            StateRaw = definition.StateRaw! with {
                World = [.. definition.State.Select(selector: r => ((r.Name.Value == "suit")
            ? r with { Cells = [Cell(
                        key: "c1",
                        value: 1
                    ), Cell(
                        key: "c2",
                        value: 2
                    ), Cell(
                        key: "c3",
                        value: 1
                    ), Cell(
                        key: "c4",
                        value: 1
                    ), Cell(
                        key: "c5",
                        value: 1
                    )] }
            : r))],
            },
        };
        using var broken = Fixtures.FreshServer(definition: mixed);

        broken.Step();
        Assert.Equal(
            0L,
            Value(
                fixture: broken,
                row: "flush"
            )
        );

        var stray = definition with {
            Rules = [new WorldRule(
                Name(value: "stray"),
                [new ActionEffect.SetState(
                        State: "flush",
                        FromState: "rank",
                        FromKey: "$token"
                    )]
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: stray,
            reason: out var strayReason
        ));
        Assert.Contains(
            actualString: strayReason,
            expectedSubstring: "not bound here"
        );
        var foreign = definition with {
            PatternsRaw = [flush with { Value = new(Instructions: [Instruction.Operand(key: "$token",
                    name: "flush"
                )]) }],
            Rules = [Mirror(
                "flush",
                "$match:hearts:hand"
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: foreign,
            reason: out var foreignReason
        ));
        Assert.Contains(
            actualString: foreignReason,
            expectedSubstring: "$token"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition with { PatternsRaw = [flush with { Attribute = "rank" }] },
            reason: out var bothReason
        ));
        Assert.Contains(
            actualString: bothReason,
            expectedSubstring: "both attribute and value"
        );
    }
    [Fact]
    public void CellAndDistanceFacetsAnswerTheFirstRejectedRayCellExactlyLikeAFixedRayQueryWouldAndGeneralizeIt() {
        // Eastward from cell 0 the board reads 2, 2, 1: a run of two "them" (2) is the longest accepted prefix of
        // "them*", so the first rejected cell is cell 3 (2 steps out from cell 0, then blocked at step 3) — the
        // same value a fixed "first cell not equal to 2" ray query would answer, generalized to any authored run
        // shape (here "them", not merely "not the board's empty sentinel"). Westward off the edge, the ray is empty
        // and the whole (empty) word is still accepted, so there is no blocker: both facets read -1.
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                ), Cell(
                    key: "2",
                    value: 2
                ), Cell(
                    key: "3",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var runOfThem = new PatternRow(
            Name(value: "runOfThem"),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "them"),
                    2,
                    2
                )],
            Pattern: new PatternNode.Star(Item: new PatternNode.Symbol(Name: "them"))
        );
        var definition = Document(
            [board, Slot(name: "blockerCell"), Slot(name: "blockerDistance"), Slot(name: "edgeCell"), Slot(name: "edgeDistance")],
            [runOfThem],
            [
            Mirror(
                    key: "0",
                    operand: "$match:runOfThem:board:E:cell",
                    target: "blockerCell"
                ),
            Mirror(
                    key: "0",
                    operand: "$match:runOfThem:board:E:distance",
                    target: "blockerDistance"
                ),
            Mirror(
                    key: "0",
                    operand: "$match:runOfThem:board:W:cell",
                    target: "edgeCell"
                ),
            Mirror(
                    key: "0",
                    operand: "$match:runOfThem:board:W:distance",
                    target: "edgeDistance"
                ),
        ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            3L,
            Value(
                fixture: fixture,
                row: "blockerCell"
            )
        );
        Assert.Equal(
            3L,
            Value(
                fixture: fixture,
                row: "blockerDistance"
            )
        );
        Assert.Equal(
            -1L,
            Value(
                fixture: fixture,
                row: "edgeCell"
            )
        );
        Assert.Equal(
            -1L,
            Value(
                fixture: fixture,
                row: "edgeDistance"
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [board, Slot(name: "x")],
                [runOfThem],
                [Mirror(
                        key: "0",
                        operand: "$match:runOfThem:board:any:cell",
                        target: "x"
                    )]
            ),
            reason: out var cellAnyReason
        ));
        Assert.Contains(
            actualString: cellAnyReason,
            expectedSubstring: "not a facet"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [board, Slot(name: "x")],
                [runOfThem],
                [Mirror(
                        key: "0",
                        operand: "$match:runOfThem:board:any:distance",
                        target: "x"
                    )]
            ),
            reason: out var distanceAnyReason
        ));
        Assert.Contains(
            actualString: distanceAnyReason,
            expectedSubstring: "not a facet"
        );
    }
    [Fact]
    public void PatternsAndSortRoundTripThroughTheStrictWireShape() {
        var every = new PatternRow(
            Name(value: "every"),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "a"),
                    1,
                    2
                ), new(
                    Name(value: "b"),
                    2,
                    3
                )],
            Pattern: new PatternNode.Sequence(Items: [
                new PatternNode.Symbol(Name: "a"), new PatternNode.AnySymbol(), new PatternNode.Except(Name: "b"), new PatternNode.Nothing(),
                new PatternNode.Choice(Items: [new PatternNode.Symbol(Name: "a"), new PatternNode.Symbol(Name: "b")]),
                new PatternNode.Optional(Item: new PatternNode.Symbol(Name: "a")),
                new PatternNode.Repeat(
                    new PatternNode.Symbol(Name: "b"),
                    0,
                    1
                ),
                new PatternNode.Complement(Item: new PatternNode.Both(Items: [new PatternNode.Symbol(Name: "a"), new PatternNode.AnySymbol()])),
                new PatternNode.Optional(Item: new PatternNode.None()),
            ]),
            MaxStates: 12
        );
        var definition = Hand() with {
            PatternsRaw = [.. Hand().Patterns, every],
            Rules = [.. Hand().Rules!, new WorldRule(
                Name(value: "order"),
                [new ActionEffect.TransformState(Transform: new StateTransform.SortZone(
                        "hand",
                        By: [new("suit"), new(
                                "rank",
                                Descending: true
                            )]
                    ))]
            )],
        };

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.Equal(
            2,
            parsed.Patterns.Count
        );
        Assert.Equal(
            12,
            parsed.Patterns[1].MaxStates
        );
        Assert.IsType<PatternNode.Sequence>(@object: parsed.Patterns[1].Pattern);
        var sort = Assert.IsType<StateTransform.SortZone>(@object: Assert.IsType<ActionEffect.TransformState>(@object: parsed.Rules![1].Effects[0]).Transform);

        Assert.Equal(
            2,
            sort.By!.Count
        );
        Assert.True(condition: sort.By[1].Descending);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: parsed,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void PrefixAndEveryDirectionFacetsAnswerFlipCountsAndFlankMasks() {
        // Row 0 of the 4x4 grid reads 1 2 2 1 eastward from cell 0, so the flank pattern accepts the 3-cell
        // prefix and no other ray from cell 0 has a them+ run closed by me.
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                ), Cell(
                    key: "2",
                    value: 2
                ), Cell(
                    key: "3",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            [board, Slot(name: "flips"), Slot(name: "mask"), Slot(name: "count"), Slot(name: "handPrefix"),
                new(
                    Name(value: "hand"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Cells: [Cell(
                            key: "c1",
                            value: 2
                        ), Cell(
                            key: "c2",
                            value: 2
                        ), Cell(
                            key: "c3",
                            value: 1
                        ), Cell(
                            key: "c4",
                            value: 2
                        )]
                ),
                new(
                    Name(value: "cards"),
                    CellKind.Int,
                    Capacity: 4,
                    Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4")]
                )],
            [Bracket("bracket")],
            [
                Mirror(
                    key: "0",
                    operand: "$match:bracket:board:E:prefix",
                    target: "flips"
                ),
                Mirror(
                    key: "0",
                    operand: "$match:bracket:board:any:mask",
                    target: "mask"
                ),
                Mirror(
                    key: "0",
                    operand: "$match:bracket:board:any:count",
                    target: "count"
                ),
                Mirror(
                    "handPrefix",
                    "$match:bracket:hand:prefix"
                ),
            ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            3L,
            Value(
                fixture: fixture,
                row: "flips"
            )
        );
        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "count"
            )
        );
        var mask = Value(
            fixture: fixture,
            row: "mask"
        );

        Assert.True(
            condition: ((mask != 0L) && ((mask & (mask - 1L)) == 0L)),
            userMessage: $"one direction accepts, mask={mask}"
        );
        Assert.Equal(
            3L,
            Value(
                fixture: fixture,
                row: "handPrefix"
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [board, Slot(name: "x")],
                [Bracket("bracket")],
                [Mirror(
                        key: "0",
                        operand: "$match:bracket:board:E:mask",
                        target: "x"
                    )]
            ),
            reason: out var facetReason
        ));
        Assert.Contains(
            actualString: facetReason,
            expectedSubstring: "not a facet"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [board, Slot(name: "x")],
                [Bracket("bracket")],
                [Mirror(
                        key: "0",
                        operand: "$match:bracket:board:any:prefix",
                        target: "x"
                    )]
            ),
            reason: out var anyReason
        ));
        Assert.Contains(
            actualString: anyReason,
            expectedSubstring: "not a facet"
        );
    }
    [Fact]
    public void TheEmptyLanguageIsTheZeroOfChoiceAndTheAnnihilatorOfSequence() {
        var dice = new WorldStateRow(
            Name(value: "dice"),
            CellKind.Int,
            Capacity: 2,
            Cells: [Cell(
                    key: "d1",
                    value: 1
                ), Cell(
                    key: "d2",
                    value: 1
                )]
        );

        PatternRow Row(string name, PatternNode pattern) => new(
            Name(value: name),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "a"),
                    1,
                    1
                )],
            Pattern: pattern
        );
        var definition = Document(
            [dice, Slot(name: "zero"), Slot(name: "choice"), Slot(name: "annihilated"), Slot(name: "everything")],
            [
            Row(
                    name: "zero",
                    pattern: new PatternNode.None()
                ),
            Row(
                    name: "choice",
                    pattern: new PatternNode.Choice(Items: [new PatternNode.None(), new PatternNode.Repeat(
                            new PatternNode.Symbol(Name: "a"),
                            2,
                            2
                        )])
                ),
            Row(
                    name: "annihilated",
                    pattern: new PatternNode.Sequence(Items: [new PatternNode.Star(Item: new PatternNode.AnySymbol()), new PatternNode.None()])
                ),
            Row(
                    name: "everything",
                    pattern: new PatternNode.Complement(Item: new PatternNode.None())
                ),
        ],
            [Mirror(
                    "zero",
                    "$match:zero:dice"
                ), Mirror(
                    "choice",
                    "$match:choice:dice"
                ), Mirror(
                    "annihilated",
                    "$match:annihilated:dice"
                ), Mirror(
                    "everything",
                    "$match:everything:dice"
                )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "zero"
            )
        );
        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "choice"
            )
        );
        Assert.Equal(
            0L,
            Value(
                fixture: fixture,
                row: "annihilated"
            )
        );
        Assert.Equal(
            1L,
            Value(
                fixture: fixture,
                row: "everything"
            )
        );
    }
    [Fact]
    public void TheStateBudgetRefusesAtValidationAndMismatchedKindsRefuseAtCompilation() {
        // any* a any{n} needs 2^(n+1) states: the classical witness that a budget is a real refusal, not a formality.
        PatternRow Tail(int n, int maxStates) => new(
            Name(value: "tail"),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "a"),
                    1,
                    1
                )],
            MaxStates: maxStates,
            Pattern: new PatternNode.Sequence(Items: [new PatternNode.Star(Item: new PatternNode.AnySymbol()), new PatternNode.Symbol(Name: "a"), new PatternNode.Repeat(
                    new PatternNode.AnySymbol(),
                    n,
                    n
                )])
        );
        var dice = new WorldStateRow(
            Name(value: "dice"),
            CellKind.Int,
            Capacity: 8,
            Cells: [Cell(
                    key: "d1",
                    value: 1
                )]
        );

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(
                    [dice],
                    [Tail(
                            maxStates: 16,
                            n: 2
                        )],
                    []
                ),
                reason: out var narrowReason
            ),
            userMessage: narrowReason
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [dice],
                [Tail(
                        maxStates: 16,
                        n: 6
                    )],
                []
            ),
            reason: out var wideReason
        ));
        Assert.Contains(
            actualString: wideReason,
            expectedSubstring: "more than 16 states"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(
                    [dice],
                    [Tail(
                            maxStates: 256,
                            n: 6
                        )],
                    []
                ),
                reason: out var roomyReason
            ),
            userMessage: roomyReason
        );

        var fixedPattern = new PatternRow(
            Name(value: "fx"),
            CellKind.Fixed,
            Symbols: [new(
                    Name(value: "half"),
                    0.5m,
                    0.5m
                )],
            Pattern: new PatternNode.Symbol(Name: "half")
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [dice, Slot(name: "hit")],
                [fixedPattern],
                [Mirror(
                        "hit",
                        "$match:fx:dice"
                    )]
            ),
            reason: out var kindReason
        ));
        Assert.Contains(
            actualString: kindReason,
            expectedSubstring: "kind=Fixed"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [dice, Slot(name: "hit")],
                [Tail(
                        maxStates: 16,
                        n: 1
                    )],
                [Mirror(
                        "hit",
                        "$match:missing:dice"
                    )]
            ),
            reason: out var missingReason
        ));
        Assert.Contains(
            actualString: missingReason,
            expectedSubstring: "names no pattern"
        );
    }
}
