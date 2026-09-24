using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the history ring: pushes wrap the oldest slot and advance the cursor, ages read newest first with
/// the empty value past what the ring holds, a pattern reads the ring oldest first, the effect resolves its value
/// like a write, and the validator refuses a ring that is not a plain numeric row.</summary>
public sealed class WorldHistoryLawTests {
    private static StateCell Cell(string key, long value = 1) => new(
        Name(value: key),
        CellValue.Int(value: value)
    );
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules, PatternRow[]? patterns = null) => Fixtures.BuildDocument() with {
        StateRaw = new(
        World: rows,
        Lattices: [new LatticeTopology.Grid(
                "map",
                new Puck.Assets.Documents.DocumentVector3(
                    x: 0,
                    y: 0,
                    z: 0
                ),
                1,
                4,
                4
            )]
    ),
        PatternsRaw = (patterns ?? []),
        Rules = rules,
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    // Pushes every value onto the ring in one firing, on the first tick alone.
    private static WorldRule Fill(long[] values) => new(
        Name(value: "fill"),
        Mode: ActionTriggerMode.Edge,
        Effects: [.. values.Select(selector: static value => new ActionEffect.PushState(
            State: "taps",
            Value: value
        ))]
    );

    [Fact]
    public void APatternReadsTheRingOldestFirstAndTheEffectPushesLikeAWrite() {
        var ring = new WorldStateRow(
            Name(value: "taps"),
            CellKind.Int,
            Domain: new StateDomain.Ring(4)
        );
        var combo = new PatternRow(
            Name(value: "combo"),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "a"),
                    1,
                    1
                ), new(
                    Name(value: "b"),
                    2,
                    2
                )],
            Pattern: new PatternNode.Sequence(Items: [new PatternNode.Star(Item: new PatternNode.AnySymbol()), new PatternNode.Symbol(Name: "a"), new PatternNode.Symbol(Name: "a"), new PatternNode.Symbol(Name: "b")])
        );

        WorldDefinition Pushing(params long[] values) => Document(
            [ring, StateFixtures.IntSlot("hit"), StateFixtures.IntSlot("tick"), StateFixtures.IntSlot(
                    name: "source",
                    value: 2
                )],
            [
            Fill(values: values),
            new WorldRule(
                    Name(value: "hit"),
                    [new ActionEffect.SetState(
                            State: "hit",
                            FromState: "$match:combo:taps"
                        )]
                ),
        ],
            [combo]
        );
        using var fixture = Fixtures.FreshServer(definition: Pushing(9, 1, 1, 2));

        fixture.Step();
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "hit"
            )
        );
        Assert.Contains(
            "accept=1",
            fixture.Server.DescribeMatch(
                attribute: null,
                direction: null,
                key: null,
                patternName: "combo",
                rowName: "taps"
            )
        );

        using var stale = Fixtures.FreshServer(definition: Pushing(9, 1, 1, 2, 5));

        stale.Step();
        Assert.Equal(
            0L,
            stale.SlotValue(row: "hit"
            )
        );

        var effects = Document(
            [ring, StateFixtures.IntSlot(
                    name: "source",
                    value: 2
                ), StateFixtures.IntSlot("count")],
            [
            new WorldRule(
                    Name(value: "push-literal"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.PushState(
                            State: "taps",
                            Value: 1m
                        )]
                ),
            new WorldRule(
                    Name(value: "push-from"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.PushState(
                            State: "taps",
                            FromState: "source"
                        )]
                ),
            new WorldRule(
                    Name(value: "push-expression"),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [new ActionEffect.PushState(
                            State: "taps",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "source"), Instruction.Constant(value: 3m), Instruction.Of(operation: ExpressionOp.Multiply),
            ])
                        )]
                ),
            new WorldRule(
                    Name(value: "count"),
                    [new ActionEffect.SetState(
                            State: "count",
                            FromState: "$history:taps:0"
                        )]
                ),
        ]
        );
        using var fired = Fixtures.FreshServer(definition: effects);

        fired.Step();
        fired.Step();
        var after = Find(
            document: fired.Server.Definition,
            row: "taps"
        );

        Assert.Equal(
            3L,
            after.HistoryCursor
        );
        Assert.Equal(
            1L,
            StateRows.FindCell(
                cells: after.Cells,
                key: Name(value: "0")
            )!.Value.AsInt
        );
        Assert.Equal(
            2L,
            StateRows.FindCell(
                cells: after.Cells,
                key: Name(value: "1")
            )!.Value.AsInt
        );
        Assert.Equal(
            6L,
            StateRows.FindCell(
                cells: after.Cells,
                key: Name(value: "2")
            )!.Value.AsInt
        );
        Assert.Equal(
            6L,
            fired.SlotValue(row: "count"
            )
        );
    }
    [Fact]
    public void PushesWrapTheRingAndAgesReadNewestFirst() {
        var ring = new WorldStateRow(
            Name(value: "taps"),
            CellKind.Int,
            Domain: new StateDomain.Ring(
                3,
                Empty: -1
            )
        );

        WorldDefinition Pushing(params long[] values) => Document(
            [ring, StateFixtures.IntSlot("latest"), StateFixtures.IntSlot("oldest"), StateFixtures.IntSlot("beyond")],
            [
            Fill(values: values),
            new WorldRule(
                    Name(value: "latest"),
                    [new ActionEffect.SetState(
                            State: "latest",
                            FromState: "$history:taps:0"
                        )]
                ),
            new WorldRule(
                    Name(value: "oldest"),
                    [new ActionEffect.SetState(
                            State: "oldest",
                            FromState: "$history:taps:2"
                        )]
                ),
        ]
        );
        using var fixture = Fixtures.FreshServer(definition: Pushing(10, 20, 30, 40));

        fixture.Step();

        var row = Find(
            document: fixture.Server.Definition,
            row: "taps"
        );

        Assert.Equal(
            4L,
            row.HistoryCursor
        );
        Assert.Equal(
            40L,
            StateRows.FindCell(
                cells: row.Cells,
                key: Name(value: "0")
            )!.Value.AsInt
        );
        Assert.Equal(
            20L,
            StateRows.FindCell(
                cells: row.Cells,
                key: Name(value: "1")
            )!.Value.AsInt
        );
        Assert.Equal(
            30L,
            StateRows.FindCell(
                cells: row.Cells,
                key: Name(value: "2")
            )!.Value.AsInt
        );

        Assert.Equal(
            40L,
            fixture.SlotValue(row: "latest"
            )
        );
        Assert.Equal(
            20L,
            fixture.SlotValue(row: "oldest"
            )
        );

        using var sparse = Fixtures.FreshServer(definition: Pushing(7));

        sparse.Step();
        Assert.Equal(
            7L,
            sparse.SlotValue(row: "latest"
            )
        );
        Assert.Equal(
            -1L,
            sparse.SlotValue(row: "oldest"
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [ring, StateFixtures.IntSlot("beyond")],
                [new WorldRule(
                        Name(value: "beyond"),
                        [new ActionEffect.SetState(
                                State: "beyond",
                                FromState: "$history:taps:3"
                            )]
                    )]
            ),
            reason: out var ageReason
        ));
        Assert.Contains(
            actualString: ageReason,
            expectedSubstring: "age must be 0..2"
        );
    }
    [Fact]
    public void TheValidatorRefusesARingThatIsNotAPlainNumericRowAndTheShapeRoundTrips() {
        var ring = new WorldStateRow(
            Name(value: "taps"),
            CellKind.Int,
            Domain: new StateDomain.Ring(3),
            HistoryCursor: 5,
            Cells: [Cell(
                    key: "0",
                    value: 4
                ), Cell(
                    key: "1",
                    value: 5
                ), Cell(
                    key: "2",
                    value: 3
                )]
        );
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: Document(
            [ring],
            []
        )));
        var round = Find(
            document: parsed,
            row: "taps"
        );

        Assert.Equal(
            3,
            ((StateDomain.Ring)round.EffectiveDomain).Capacity
        );
        Assert.Equal(
            5L,
            round.HistoryCursor
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: parsed,
                reason: out var reason
            ),
            userMessage: reason
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [new WorldStateRow(
                        Name(value: "t"),
                        CellKind.Int,
                        HistoryCursor: 1
                    )],
                []
            ),
            reason: out var cursorReason
        ));
        Assert.Contains(
            actualString: cursorReason,
            expectedSubstring: "historyCursor without a ring domain"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [new WorldStateRow(
                        Name(value: "t"),
                        CellKind.Int,
                        Domain: new StateDomain.Ring(2),
                        HistoryCursor: 1,
                        Cells: [Cell(
                                key: "5",
                                value: 1
                            )]
                    )],
                []
            ),
            reason: out var slotReason
        ));
        Assert.Contains(
            actualString: slotReason,
            expectedSubstring: "slots 0..n-1 in order"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [new WorldStateRow(
                        Name(value: "t"),
                        CellKind.Int,
                        Domain: new StateDomain.Ring(3),
                        HistoryCursor: 2,
                        Cells: [Cell(
                                key: "1",
                                value: 1
                            ), Cell(
                                key: "0",
                                value: 1
                            )]
                    )],
                []
            ),
            reason: out var orderReason
        ));
        Assert.Contains(
            actualString: orderReason,
            expectedSubstring: "in order"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [new WorldStateRow(
                        Name(value: "t"),
                        CellKind.Int,
                        Domain: new StateDomain.Ring(3),
                        HistoryCursor: 1,
                        Cells: [Cell(
                                key: "0",
                                value: 1
                            ), Cell(
                                key: "1",
                                value: 1
                            )]
                    )],
                []
            ),
            reason: out var cursorCountReason
        ));
        Assert.Contains(
            actualString: cursorCountReason,
            expectedSubstring: "says fewer"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [new WorldStateRow(
                        Name(value: "t"),
                        CellKind.Text,
                        Domain: new StateDomain.Ring(2)
                    )],
                []
            ),
            reason: out var kindReason
        ));
        Assert.Contains(
            actualString: kindReason,
            expectedSubstring: "integer or fixed"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [new WorldStateRow(
                        Name(value: "t"),
                        CellKind.Int,
                        Domain: new StateDomain.Ring(2),
                        Phase: new(Sequence: 0)
                    )],
                []
            ),
            reason: out var traitReason
        ));
        Assert.Contains(
            actualString: traitReason,
            expectedSubstring: "no other storage"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                [StateFixtures.IntSlot("plain")],
                [new WorldRule(
                        Name(value: "push"),
                        [new ActionEffect.PushState(
                                State: "plain",
                                Value: 1m
                            )]
                    )]
            ),
            reason: out var pushReason
        ));
        Assert.Contains(
            actualString: pushReason,
            expectedSubstring: "requires a history row"
        );
    }
}
