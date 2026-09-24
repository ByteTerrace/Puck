using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the one cell-set vocabulary: occupancy read as a 64-bit mask, the topology-aware shift that drops
/// bits at an edge, bit-algebra composing two masks, the composed set landing back on a board via <c>writeSet</c>,
/// and the ceilings that refuse.</summary>
public sealed class WorldBoardMaskLawTests {
    private static StateCell Cell(string key, long value = 1) => new(
        Name(value: key),
        CellValue.Int(value: value)
    );
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules, PatternRow[]? patterns = null, LatticeTopology[]? lattices = null) => Fixtures.BuildDocument() with {
        StateRaw = new(
        World: rows,
        Lattices: (lattices ?? [Grid(
                name: "map",
                side: 4
            )])
    ),
        PatternsRaw = (patterns ?? []),
        Rules = rules,
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static LatticeTopology.Grid Grid(string name, int side) =>
        new(
            name,
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            side,
            side
        );
    private static string[] Members(WorldDefinition document, string row) =>
        (Find(
            document: document,
            row: row
        ).Cells ?? []).Where(predicate: c => (c.Value.Raw != 0L)).Select(selector: c => c.Key.Value).ToArray();
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static long Shift(CompiledTopology topology, long mask, int direction) {
        var result = 0L;

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (
                (((mask >> cell) & 1L) != 0L) &&
                (topology.Neighbour(
                cell: cell,
                direction: direction
            ) is var next) &&
                (next >= 0)
            ) {
                result |= (1L << next);
            }
        }
        return result;
    }

    [Fact]
    public void ASetLandsBackOnTheBoardThroughWriteSetAndBitAlgebraComposesTwoBoardsIntoOne() {
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 7
                ), Cell(
                    key: "3",
                    value: 7
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var other = new WorldStateRow(
            Name(value: "other"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "5",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var target = new WorldStateRow(
            Name(value: "target"),
            CellKind.Bool,
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            [board, other, target, StateFixtures.IntSlot(
                    name: "mask",
                    value: 0b1010L
                ), StateFixtures.IntSlot("both"), StateFixtures.IntSlot("either"), StateFixtures.IntSlot("onlyLeft"), StateFixtures.IntSlot("complement")],
            [
            new WorldRule(
                    Name(value: "both"),
                    [new ActionEffect.SetState(
                            State: "both",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:100"), Instruction.Operand(name: "$board:mask:other:1:100"), Instruction.Of(operation: ExpressionOp.BitAnd),
            ])
                        )]
                ),
            new WorldRule(
                    Name(value: "either"),
                    [new ActionEffect.SetState(
                            State: "either",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:100"), Instruction.Operand(name: "$board:mask:other:1:100"), Instruction.Of(operation: ExpressionOp.BitOr),
            ])
                        )]
                ),
            new WorldRule(
                    Name(value: "onlyLeft"),
                    [new ActionEffect.SetState(
                            State: "onlyLeft",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:100"), Instruction.Operand(name: "$board:mask:other:1:100"), Instruction.Of(operation: ExpressionOp.BitNot), Instruction.Of(operation: ExpressionOp.BitAnd),
            ])
                        )]
                ),
            new WorldRule(
                    Name(value: "complement"),
                    [new ActionEffect.SetState(
                            State: "complement",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:other:1:100"), Instruction.Of(operation: ExpressionOp.BitNot),
            ])
                        )]
                ),
        ]
        );

        var painted = StateFixtures.Apply(
            definition: definition,
            transform: new StateTransform.WriteSet(
                "board",
                "mask",
                Value: 7
            )
        );
        var cells = Find(
            document: painted,
            row: "board"
        ).Cells!;

        Assert.Equal(
            7L,
            StateRows.FindCell(
                cells: cells,
                key: Name(value: "1")
            )!.Value.Raw
        );
        Assert.Equal(
            7L,
            StateRows.FindCell(
                cells: cells,
                key: Name(value: "3")
            )!.Value.Raw
        );
        Assert.Equal(
            1L,
            StateRows.FindCell(
                cells: cells,
                key: Name(value: "0")
            )!.Value.Raw
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        // Every op the old row-vs-row `combine` transform offered is now composed once from $board:mask reads and
        // the generic bit operators, then lands back on the target board through the one writeSet transform: no
        // second vocabulary for the same board algebra.
        var both = StateFixtures.Apply(
            definition: fixture.Server.Definition,
            transform: new StateTransform.WriteSet(
                "target",
                "both",
                Value: 1
            )
        );

        Assert.Equal(
            new[] { "0" },
            Members(
                document: both,
                row: "target"
            )
        );
        Assert.Single(collection: Find(
            document: both,
            row: "target"
        ).Cells!);
        var either = StateFixtures.Apply(
            definition: fixture.Server.Definition,
            transform: new StateTransform.WriteSet(
                "target",
                "either",
                Value: 1
            )
        );

        Assert.Equal(
            new[] { "0", "1", "3", "5" },
            Members(
                document: either,
                row: "target"
            )
        );
        var onlyLeft = StateFixtures.Apply(
            definition: fixture.Server.Definition,
            transform: new StateTransform.WriteSet(
                "target",
                "onlyLeft",
                Value: 1
            )
        );

        Assert.Equal(
            new[] { "1", "3" },
            Members(
                document: onlyLeft,
                row: "target"
            )
        );
        // BitNot complements past the topology's own cell count; writeSet clips the write to the board's real cells.
        var complement = StateFixtures.Apply(
            definition: fixture.Server.Definition,
            transform: new StateTransform.WriteSet(
                "target",
                "complement",
                Value: 1
            )
        );

        Assert.Equal(
            14,
            Members(
                document: complement,
                row: "target"
            ).Length
        );
    }
    [Fact]
    public void MasksRefuseTopologiesPastSixtyFourCells() {
        var wide = new WorldStateRow(
            Name(value: "wide"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("big")
        );
        var small = new WorldStateRow(
            Name(value: "small"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            [wide, small, StateFixtures.IntSlot("mask")],
            [new WorldRule(
                    Name(value: "mask"),
                    [new ActionEffect.SetState(
                            State: "mask",
                            FromState: "$board:mask:wide:1:1"
                        )]
                )],
            [],
            lattices: [Grid(
                    name: "map",
                    side: 4
                ), Grid(
                    name: "big",
                    side: 9
                )]
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var maskReason
        ));
        Assert.Contains(
            actualString: maskReason,
            expectedSubstring: "at most 64"
        );

        var shifted = Document(
            [wide, small, StateFixtures.IntSlot("mask")],
            [new WorldRule(
                    Name(value: "shift"),
                    [new ActionEffect.SetState(
                            State: "mask",
                            Expression: new ExpressionProgram(Instructions: [
            Instruction.Constant(value: 1m), Instruction.Board(index: "E",
                                    operation: ExpressionOp.BoardShift,
                                    topology: "big"
                                ),
        ])
                        )]
                )],
            [],
            lattices: [Grid(
                    name: "map",
                    side: 4
                ), Grid(
                    name: "big",
                    side: 9
                )]
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: shifted,
            reason: out var shiftReason
        ));
        Assert.Contains(
            actualString: shiftReason,
            expectedSubstring: "at most 64"
        );

        var mixed = Document(
            [wide, small, StateFixtures.IntSlot("mask")],
            [],
            [],
            lattices: [Grid(
                    name: "map",
                    side: 4
                ), Grid(
                    name: "big",
                    side: 9
                )]
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: mixed with {
                Rules = [new WorldRule(
                    Name(value: "bad"),
                    [new ActionEffect.TransformState(Transform: new StateTransform.WriteSet(
                            "wide",
                            "mask"
                        ))]
                )],
            },
            reason: out var setReason
        ));
        Assert.Contains(
            actualString: setReason,
            expectedSubstring: "at most 64"
        );
    }
    [Fact]
    public void OccupancyReadsAsAMaskAndABoardShiftFollowsTheTopologyWithoutWrapping() {
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
            [board, StateFixtures.IntSlot("mask"), StateFixtures.IntSlot("east"), StateFixtures.IntSlot("north")],
            [
            new WorldRule(
                    Name(value: "mask"),
                    [new ActionEffect.SetState(
                            State: "mask",
                            FromState: "$board:mask:board:2:2"
                        )]
                ),
            new WorldRule(
                    Name(value: "east"),
                    [new ActionEffect.SetState(
                            State: "east",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:1"), Instruction.Board(index: "E",
                                    operation: ExpressionOp.BoardShift,
                                    topology: "map"
                                ),
            ])
                        )]
                ),
            new WorldRule(
                    Name(value: "north"),
                    [new ActionEffect.SetState(
                            State: "north",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:1"), Instruction.Board(index: "N",
                                    operation: ExpressionOp.BoardShift,
                                    topology: "map"
                                ),
            ])
                        )]
                ),
        ]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        var topology = TopologyCompilation.Find(
            definition.StateRaw,
            "map"
        )!;
        var east = topology.Direction(token: "E");
        var north = topology.Direction(token: "N");

        Assert.Equal(
            0b0110L,
            fixture.SlotValue(row: "mask"
            )
        );
        var expectedEast = Shift(
            direction: east,
            mask: 0b1001L,
            topology: topology
        );

        Assert.Equal(
            expectedEast,
            fixture.SlotValue(row: "east"
            )
        );
        Assert.NotEqual(
            actual: expectedEast,
            expected: 0L
        );
        // Row 0 is an edge row northward in one of the two orientations, or a shift away from it: either way the
        // result is the topology's own answer, and nothing wrapped.
        Assert.Equal(
            Shift(
                direction: north,
                mask: 0b1001L,
                topology: topology
            ),
            fixture.SlotValue(row: "north"
            )
        );
    }
}
