using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a multi-card deal as one transfer, a rule quantified over a token-keyed row, and an audience a
/// rule widens by writing a token into the readersFrom row.</summary>
public sealed class WorldDealAndRevealLawTests {
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
    private static StateCell Cell(string key, long value = 1, CellKind kind = CellKind.Int) => new(
        Name(value: key),
        ((kind == CellKind.Bool) ? CellValue.Bool(value: (value != 0L)) : CellValue.Int(value: value))
    );
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows),
        Rules = rules,
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static string[] Keys(WorldDefinition document, string row) => (Find(
        document: document,
        row: row
    ).Cells ?? []).Select(selector: c => c.Key.Value).ToArray();
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldStateRow Slot(string name, long value) => new(
        Name(value: name),
        CellKind.Int,
        Cells: [new StateCell(
                WorldStateRow.SlotKey,
                CellValue.Int(value: value)
            )]
    );

    [Fact]
    public void ACountedTransferDealsInOneMutationAndRefusesPastThePile() {
        var definition = Document(
            rows: [
            new(
                    Name(value: "cards"),
                    CellKind.Int,
                    Capacity: 6,
                    Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4"), Cell("c5"), Cell("c6")]
                ),
            new(
                    Name(value: "deck"),
                    CellKind.Bool,
                    Capacity: 6,
                    Cells: [Cell("c1", kind: CellKind.Bool), Cell("c2", kind: CellKind.Bool), Cell("c3", kind: CellKind.Bool), Cell("c4", kind: CellKind.Bool), Cell("c5", kind: CellKind.Bool), Cell("c6", kind: CellKind.Bool)],
                    Domain: new StateDomain.KeysOf(
                        CellName.Parse(candidate: "cards"),
                        Ordered: true
                    )
                ),
            new(
                    Name(value: "hand"),
                    CellKind.Bool,
                    Capacity: 6,
                    Domain: new StateDomain.KeysOf(
                        CellName.Parse(candidate: "cards"),
                        Ordered: true
                    )
                ),
        ],
            rules: []
        );

        var dealt = Apply(
            definition: definition,
            transform: new StateTransform.Transfer(
                "deck",
                "hand",
                ZoneSelector.First,
                Count: 5
            )
        );

        Assert.Equal(
            new[] { "c1", "c2", "c3", "c4", "c5" },
            Keys(
                document: dealt,
                row: "hand"
            )
        );
        Assert.Equal(
            new[] { "c6" },
            Keys(
                document: dealt,
                row: "deck"
            )
        );

        Assert.False(condition: WorldArenaTransforms.TryApply(
            dealt,
            new StateTransform.Transfer(
                "deck",
                "hand",
                ZoneSelector.First,
                Count: 2
            ),
            WorldPrincipal.World,
            0,
            "test",
            out _,
            out var shortReason
        ));
        Assert.Contains(
            actualString: shortReason,
            expectedSubstring: "fewer than the 2"
        );
        Assert.False(condition: WorldArenaTransforms.TryApply(
            definition,
            new StateTransform.Transfer(
                "deck",
                "hand",
                ZoneSelector.Key,
                Key: "c1",
                Count: 2
            ),
            WorldPrincipal.World,
            0,
            "test",
            out _,
            out var keyReason
        ));
        Assert.Contains(
            actualString: keyReason,
            expectedSubstring: "exactly 1 by key or slice"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition with {
                Rules = [new WorldRule(
                    Name(value: "bad"),
                    [new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                            "deck",
                            "hand",
                            ZoneSelector.First,
                            Count: 0
                        ))]
                )],
            },
            reason: out var compileReason
        ));
        Assert.Contains(
            actualString: compileReason,
            expectedSubstring: "count of 1.."
        );
    }
    [Fact]
    public void AReadersFromRowRevealsAHandWhenARuleWritesTheToken() {
        var seat = WorldPrincipal.Seat(slot: 1);
        var hand = new WorldStateRow(
            Name(value: "hand"),
            CellKind.Int,
            Capacity: 2,
            Cells: [Cell(
                    key: "c1",
                    value: 11
                ), Cell(
                    key: "c2",
                    value: 12
                )],
            Visibility: new(
                Readers: [],
                ReadersFrom: "audience"
            )
        );
        var audience = new WorldStateRow(
            Name(value: "audience"),
            CellKind.Text,
            Capacity: 4,
            Cells: [new StateCell(
                    Name(value: "a1"),
                    CellValue.Text(value: "")
                )]
        );
        var definition = Document(
            rows: [hand, audience, Slot(
                    name: "showdown",
                    value: 0
                )],
            rules: [
            new WorldRule(
                    Name(value: "reveal"),
                    Mode: ActionTriggerMode.Edge,
                    Gate: new ActionPredicate.CompareState(
                        State: "showdown",
                        Comparison: ActionStateComparison.Equal,
                        Value: 1m
                    ),
                    Effects: [new ActionEffect.SetState(
                            State: "audience",
                            Key: "a1",
                            Text: seat.Describe()
                        )]
                ),
        ]
        );

        Assert.Null(@object: Fixtures.Disclose(
            definition: definition,
            recipient: seat
        )?.FirstOrDefault(predicate: r => (r.Name == "hand")));

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        Assert.Null(@object: Fixtures.Disclose(
            definition: fixture.Server.Definition,
            recipient: seat
        )?.FirstOrDefault(predicate: r => (r.Name == "hand")));

        var revealed = fixture.Server.Definition with {
            StateRaw = fixture.Server.Definition.StateRaw! with {
                World = [.. fixture.Server.Definition.State.Select(selector: r => ((r.Name.Value == "showdown")
            ? r with { Cells = [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 1L)
                    )] }
            : r))],
            },
        };
        using var shown = Fixtures.FreshServer(definition: revealed);

        shown.Step();
        var observed = Fixtures.Disclose(
            definition: shown.Server.Definition,
            recipient: seat
        )?.FirstOrDefault(predicate: r => (r.Name == "hand"));

        Assert.NotNull(@object: observed);
        Assert.Equal(
            2,
            observed!.Cells.Count
        );
        Assert.Null(@object: Fixtures.Disclose(
            definition: shown.Server.Definition,
            recipient: WorldPrincipal.Seat(slot: 2)
        )?.FirstOrDefault(predicate: r => (r.Name == "hand")));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(
                rows: [hand with { Visibility = new(
                        Readers: [],
                        ReadersFrom: "showdown"
                    ) }, Slot(
                        name: "showdown",
                        value: 0
                    )],
                rules: []
            ),
            reason: out var kindReason
        ));
        Assert.Contains(
            actualString: kindReason,
            expectedSubstring: "keyed text row"
        );
    }
    [Fact]
    public void ARuleQuantifiedOverTokenKeysBindsEachToTheKey() {
        var definition = Document(
            rows: [
            new(
                    Name(value: "cards"),
                    CellKind.Int,
                    Capacity: 3,
                    Cells: [Cell("c1"), Cell("c2"), Cell("c3")]
                ),
            new(
                    Name(value: "rank"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Capacity: 3,
                    Cells: [Cell(
                            key: "c1",
                            value: 5
                        ), Cell(
                            key: "c2",
                            value: 9
                        ), Cell(
                            key: "c3",
                            value: 2
                        )]
                ),
            new(
                    Name(value: "doubled"),
                    CellKind.Int,
                    Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
                    Capacity: 3,
                    Cells: [Cell(
                            key: "c1",
                            value: 0
                        ), Cell(
                            key: "c2",
                            value: 0
                        ), Cell(
                            key: "c3",
                            value: 0
                        )]
                ),
        ],
            rules: [
            new WorldRule(
                    Name(value: "double"),
                    Mode: ActionTriggerMode.Edge,
                    ForEach: "rank",
                    Effects: [new ActionEffect.SetState(
                            State: "doubled",
                            Key: "$each",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(key: "$each",
                                    name: "rank"
                                ), Instruction.Constant(value: 2m), Instruction.Of(operation: ExpressionOp.Multiply),
            ])
                        )]
                ),
        ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        var doubled = Find(
            document: fixture.Server.Definition,
            row: "doubled"
        ).Cells!;

        Assert.Equal(
            10L,
            StateRows.FindCell(
                cells: doubled,
                key: Name(value: "c1")
            )!.Value.AsInt
        );
        Assert.Equal(
            18L,
            StateRows.FindCell(
                cells: doubled,
                key: Name(value: "c2")
            )!.Value.AsInt
        );
        Assert.Equal(
            4L,
            StateRows.FindCell(
                cells: doubled,
                key: Name(value: "c3")
            )!.Value.AsInt
        );
    }
}
