using Puck.Commands;
using Puck.Assets.Documents;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the pattern-language operand over its three word sources, complement and intersection, the sort that
/// canonicalizes a hand, the state budget's refusal, and the strict wire shape.</summary>
public sealed class WorldPatternLawTests {
    [InlineData("sortZone")]
    [InlineData("sortKeyed")]
    [Theory]
    public void RetiredSortDiscriminatorsAreRefused(string discriminator) {
        var json = System.Text.Encoding.UTF8.GetBytes(s: $$$"""
            {"rules":[{"name":"order","effects":[{"$type":"transformState","transform":{
              "$type":"{{{discriminator}}}","row":"scores"
            }}]}]}
            """);
        var error = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: json));

        Assert.Contains(discriminator, error.Message, StringComparison.Ordinal);
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
                    Cells: [StateFixtures.Cell("c1"), StateFixtures.Cell("c2"), StateFixtures.Cell("c3"), StateFixtures.Cell("c4"), StateFixtures.Cell("c5")]
                ),
            new(
                    Name(value: "rank"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Cells: [StateFixtures.Cell(
                            key: "c1",
                            value: 9
                        ), StateFixtures.Cell(
                            key: "c2",
                            value: 5
                        ), StateFixtures.Cell(
                            key: "c3",
                            value: 7
                        ), StateFixtures.Cell(
                            key: "c4",
                            value: 6
                        ), StateFixtures.Cell(
                            key: "c5",
                            value: 8
                        )]
                ),
            new(
                    Name(value: "suit"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Cells: [StateFixtures.Cell(
                            key: "c1",
                            value: 1
                        ), StateFixtures.Cell(
                            key: "c2",
                            value: 2
                        ), StateFixtures.Cell(
                            key: "c3",
                            value: 1
                        ), StateFixtures.Cell(
                            key: "c4",
                            value: 2
                        ), StateFixtures.Cell(
                            key: "c5",
                            value: 1
                        )]
                ),
            new(
                    Name(value: "hand"),
                    CellKind.Bool,
                    Capacity: 5,
                    Cells: [StateFixtures.Cell("c1", kind: CellKind.Bool), StateFixtures.Cell("c2", kind: CellKind.Bool), StateFixtures.Cell("c3", kind: CellKind.Bool), StateFixtures.Cell("c4", kind: CellKind.Bool), StateFixtures.Cell("c5", kind: CellKind.Bool)],
                    Domain: new StateDomain.KeysOf(
                        CellName.Parse(candidate: "cards"),
                        Ordered: true
                    )
                ),
            StateFixtures.IntSlot(name: "straight"),
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
                FromKey: StateChannelRef.OfNullable(spelling: key)
            )]
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void ABoardRayIsAWordAndAFlankIsARegularPattern() {
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [StateFixtures.Cell(
                    key: "0",
                    value: 1
                ), StateFixtures.Cell(
                    key: "1",
                    value: 2
                ), StateFixtures.Cell(
                    key: "2",
                    value: 2
                ), StateFixtures.Cell(
                    key: "3",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            [board, StateFixtures.IntSlot(name: "flank"), StateFixtures.IntSlot(name: "south"), StateFixtures.IntSlot(name: "narrow")],
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
            fixture.SlotValue(row: "flank"
            )
        );
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "south"
            )
        );
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "narrow"
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
            Cells: [StateFixtures.Cell(
                    key: "d1",
                    value: 4
                ), StateFixtures.Cell(
                    key: "d2",
                    value: 2
                ), StateFixtures.Cell(
                    key: "d3",
                    value: 6
                ), StateFixtures.Cell(
                    key: "d4",
                    value: 6
                ), StateFixtures.Cell(
                    key: "d5",
                    value: 5
                )]
        );
        var definition = Document(
            [dice, StateFixtures.IntSlot(name: "pair"), StateFixtures.IntSlot(name: "noSix"), StateFixtures.IntSlot(name: "twoAndFive")],
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
            fixture.SlotValue(row: "pair"
            )
        );
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "noSix"
            )
        );
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "twoAndFive"
            )
        );
    }
    [Fact]
    public void ASortedHandReadsItsAttributeWordAndAStraightMatchesOnlyAfterSorting() {
        var unsorted = Hand();
        var sorted = StateFixtures.Apply(
            definition: unsorted,
            transform: new StateTransform.Sort(
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
        var descending = StateFixtures.Apply(
            definition: unsorted,
            transform: new StateTransform.Sort(
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
        var suited = StateFixtures.Apply(
            definition: unsorted,
            transform: new StateTransform.Sort(
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
            new StateTransform.Sort(
                "hand",
                By: []
            ),
            Principal.World,
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
                Cells: [StateFixtures.Cell("s1"), StateFixtures.Cell("s2")]
            ),
            new(
                Name(value: "score"),
                CellKind.Int,
                Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "seats")),
                Cells: [StateFixtures.Cell(
                        key: "s1",
                        value: 3
                    ), StateFixtures.Cell(
                        key: "s2",
                        value: 1
                    )]
            )],
            },
        };

        Assert.False(condition: WorldArenaTransforms.TryApply(
            foreign,
            new StateTransform.Sort(
                "hand",
                By: [new("score")]
            ),
            Principal.World,
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
                    [new ActionEffect.TransformState(Transform: new StateTransform.Sort(
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
            before.SlotValue(row: "straight"
            )
        );

        using var after = Fixtures.FreshServer(definition: sorted);

        after.Step();
        Assert.Equal(
            1L,
            after.SlotValue(row: "straight"
            )
        );
    }
    [Fact]
    public void ASortedTrayMatchesAChoiceOfSequencesAndAKeyedRowSortsByItsOwnValues() {
        var dice = new WorldStateRow(
            Name(value: "dice"),
            CellKind.Int,
            Capacity: 5,
            Cells: [StateFixtures.Cell(
                    key: "d1",
                    value: 4
                ), StateFixtures.Cell(
                    key: "d2",
                    value: 2
                ), StateFixtures.Cell(
                    key: "d3",
                    value: 6
                ), StateFixtures.Cell(
                    key: "d4",
                    value: 3
                ), StateFixtures.Cell(
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
            [dice, StateFixtures.IntSlot(name: "hit")],
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
            unsorted.SlotValue(row: "hit"
            )
        );

        var sorted = StateFixtures.Apply(
            definition: definition,
            transform: new StateTransform.Sort(Row: "dice", By: [new SortKey(Row: "dice")])
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
            fixture.SlotValue(row: "hit"
            )
        );

        // Control: a plain slot row carries no keyed cells to order.
        Assert.False(condition: WorldArenaTransforms.TryApply(
            definition,
            new StateTransform.Sort(Row: "hit", By: [new SortKey(Row: "hit")]),
            Principal.World,
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
            ? r with { Cells = [StateFixtures.Cell(
                        key: "c1",
                        value: 1
                    ), StateFixtures.Cell(
                        key: "c2",
                        value: 1
                    ), StateFixtures.Cell(
                        key: "c3",
                        value: 1
                    ), StateFixtures.Cell(
                        key: "c4",
                        value: 1
                    ), StateFixtures.Cell(
                        key: "c5",
                        value: 1
                    )] }
            : r)),
            StateFixtures.IntSlot(name: "flush"), StateFixtures.IntSlot(name: "royal")],
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
            unsorted.SlotValue(row: "flush"
            )
        );
        Assert.Equal(
            0L,
            unsorted.SlotValue(row: "royal"
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

        var sorted = StateFixtures.Apply(
            definition: definition,
            transform: new StateTransform.Sort(
                "hand",
                By: [new("rank")]
            )
        );
        using var ordered = Fixtures.FreshServer(definition: sorted);

        ordered.Step();
        Assert.Equal(
            1L,
            ordered.SlotValue(row: "royal"
            )
        );

        var mixed = definition with {
            StateRaw = definition.StateRaw! with {
                World = [.. definition.State.Select(selector: r => ((r.Name.Value == "suit")
            ? r with { Cells = [StateFixtures.Cell(
                        key: "c1",
                        value: 1
                    ), StateFixtures.Cell(
                        key: "c2",
                        value: 2
                    ), StateFixtures.Cell(
                        key: "c3",
                        value: 1
                    ), StateFixtures.Cell(
                        key: "c4",
                        value: 1
                    ), StateFixtures.Cell(
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
            broken.SlotValue(row: "flush"
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

    // Staggered ranges give every value its own set of memberships, so the alphabet is as wide as a letter mask
    // holds, and a long exact repeat gives the machine a state for every copy: a wide, tall table.
    private static PatternRow Wide(int index) => new(
        Name(value: $"wide{index}"),
        CellKind.Int,
        MaxStates: PatternCapacity.MaxStates,
        Symbols: [.. Enumerable.Range(
                count: PatternCapacity.MaxSymbols,
                start: 0
            ).Select(selector: static symbol => new PatternSymbol(
                Name(value: $"p{symbol}"),
                symbol,
                (symbol + PatternCapacity.MaxSymbols)
            ))],
        Pattern: new PatternNode.Repeat(
            Item: new PatternNode.Symbol(Name: "p0"),
            Max: PatternCapacity.MaxRepeat,
            Min: PatternCapacity.MaxRepeat
        )
    );

    [Fact]
    public void ADocumentsPatternTablesAreBoundedTogetherInBytesAndRefusedAtTheRowThatCrossed() {
        var errors = new List<string>();

        Assert.True(
            condition: CompiledPatterns.TryCompileAll(
                errors: errors,
                patterns: out var one,
                rows: [Wide(index: 0)]
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );

        var each = one.All.Single().TableBytes;
        var fit = ((int)(PatternCapacity.MaxTableBytes / each));

        // The rows that fit are fewer than the row ceiling, so the bytes are the limit that binds.
        Assert.InRange(
            actual: fit,
            high: (PatternCapacity.MaxRows - 1),
            low: 1
        );
        Assert.True(
            condition: CompiledPatterns.TryCompileAll(
                errors: errors,
                patterns: out _,
                rows: [.. Enumerable.Range(
                    count: fit,
                    start: 0
                ).Select(selector: Wide)]
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );
        Assert.False(condition: CompiledPatterns.TryCompileAll(
            errors: errors,
            patterns: out _,
            rows: [.. Enumerable.Range(
                count: (fit + 1),
                start: 0
            ).Select(selector: Wide)]
        ));

        var refusal = Assert.Single(collection: errors);

        Assert.Contains(
            actualString: refusal,
            expectedSubstring: $"patterns[{fit}] 'wide{fit}'"
        );
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: $"{PatternCapacity.MaxTableBytes}-byte ceiling"
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
            Cells: [StateFixtures.Cell(
                    key: "0",
                    value: 1
                ), StateFixtures.Cell(
                    key: "1",
                    value: 2
                ), StateFixtures.Cell(
                    key: "2",
                    value: 2
                ), StateFixtures.Cell(
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
            [board, StateFixtures.IntSlot(name: "blockerCell"), StateFixtures.IntSlot(name: "blockerDistance"), StateFixtures.IntSlot(name: "edgeCell"), StateFixtures.IntSlot(name: "edgeDistance")],
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
            fixture.SlotValue(row: "blockerCell"
            )
        );
        Assert.Equal(
            3L,
            fixture.SlotValue(row: "blockerDistance"
            )
        );
        Assert.Equal(
            -1L,
            fixture.SlotValue(row: "edgeCell"
            )
        );
        Assert.Equal(
            -1L,
            fixture.SlotValue(row: "edgeDistance"
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [board, StateFixtures.IntSlot(name: "x")],
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
                [board, StateFixtures.IntSlot(name: "x")],
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
                [new ActionEffect.TransformState(Transform: new StateTransform.Sort(
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
        var sort = Assert.IsType<StateTransform.Sort>(@object: Assert.IsType<ActionEffect.TransformState>(@object: parsed.Rules![1].Effects[0]).Transform);

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
            Cells: [StateFixtures.Cell(
                    key: "0",
                    value: 1
                ), StateFixtures.Cell(
                    key: "1",
                    value: 2
                ), StateFixtures.Cell(
                    key: "2",
                    value: 2
                ), StateFixtures.Cell(
                    key: "3",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            [board, StateFixtures.IntSlot(name: "flips"), StateFixtures.IntSlot(name: "mask"), StateFixtures.IntSlot(name: "count"), StateFixtures.IntSlot(name: "handPrefix"),
                new(
                    Name(value: "hand"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Cells: [StateFixtures.Cell(
                            key: "c1",
                            value: 2
                        ), StateFixtures.Cell(
                            key: "c2",
                            value: 2
                        ), StateFixtures.Cell(
                            key: "c3",
                            value: 1
                        ), StateFixtures.Cell(
                            key: "c4",
                            value: 2
                        )]
                ),
                new(
                    Name(value: "cards"),
                    CellKind.Int,
                    Capacity: 4,
                    Cells: [StateFixtures.Cell("c1"), StateFixtures.Cell("c2"), StateFixtures.Cell("c3"), StateFixtures.Cell("c4")]
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
            fixture.SlotValue(row: "flips"
            )
        );
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "count"
            )
        );
        var mask = fixture.SlotValue(row: "mask"
        );

        Assert.True(
            condition: ((mask != 0L) && ((mask & (mask - 1L)) == 0L)),
            userMessage: $"one direction accepts, mask={mask}"
        );
        Assert.Equal(
            3L,
            fixture.SlotValue(row: "handPrefix"
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [board, StateFixtures.IntSlot(name: "x")],
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
                [board, StateFixtures.IntSlot(name: "x")],
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
            Cells: [StateFixtures.Cell(
                    key: "d1",
                    value: 1
                ), StateFixtures.Cell(
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
            [dice, StateFixtures.IntSlot(name: "zero"), StateFixtures.IntSlot(name: "choice"), StateFixtures.IntSlot(name: "annihilated"), StateFixtures.IntSlot(name: "everything")],
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
            fixture.SlotValue(row: "zero"
            )
        );
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "choice"
            )
        );
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "annihilated"
            )
        );
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "everything"
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
            Cells: [StateFixtures.Cell(
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
                [dice, StateFixtures.IntSlot(name: "hit")],
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
                [dice, StateFixtures.IntSlot(name: "hit")],
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
