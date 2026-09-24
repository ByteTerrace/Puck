using Puck.Abstractions.Counting;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the one cell read a radial's rings and a binding bar's action icons make: a keyed cell of an authored row,
/// read as text through the seat's routed state mirror. The row reference is parsed and the cell registered once, so a
/// read repeated every frame allocates nothing and adds no slot, and the text it answers follows the live cell.
/// </summary>
public sealed class WorldStateCellsLawTests {
    private const string IconRow = "state.icons";
    private const int Repetitions = 64;

    private static WorldStateRow Icons(string jump) => new(
        Name: CellName.Parse(candidate: "icons"),
        Kind: CellKind.Text,
        Capacity: 8,
        Cells: [new StateCell(
                Key: CellName.Parse(candidate: "jump"),
                Value: CellValue.Text(value: jump)
            )]
    );
    private static WorldDefinition Document(string jump) => Fixtures.BuildDocument().WithWorldState(rows: [Icons(jump: jump)]);

    [Fact]
    public void AnIconReadEveryFrameRegistersOnceAndAllocatesNothing() {
        var mirror = ClientFixtures.StateMirror(definition: Document(jump: "arrow-up"));
        var cells = new WorldStateCells();
        var reads = 0;

        void Frames() {
            for (var repetition = 0; (repetition < Repetitions); repetition++) {
                if (cells.TryText(
                    key: "jump",
                    mirror: mirror,
                    rowReference: IconRow,
                    slot: out _,
                    text: out _
                )) {
                    reads++;
                }
            }
        }

        Frames();

        var slots = mirror.SlotCount;

        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Frames)
        );
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: slots
        );
        Assert.True(condition: (reads >= (2 * Repetitions)));
    }
    [Fact]
    public void AnIconReadFollowsTheLiveCell() {
        var definition = Document(jump: "arrow-up");
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var cells = new WorldStateCells();

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        Assert.True(condition: cells.TryText(key: "jump", mirror: mirror, rowReference: IconRow, slot: out var slot, text: out var before));
        Assert.Equal(actual: before, expected: "arrow-up");

        definition = Document(jump: "wing");
        mirror.Refresh(stamp: new WorldStateStamp(
            EngineTick: 1680UL,
            Everything: false,
            MovedRows: new[] { 0 },
            Tick: 1UL
        ));
        Assert.True(condition: cells.TryText(key: "jump", mirror: mirror, rowReference: IconRow, slot: out var again, text: out var after));
        Assert.Equal(actual: after, expected: "wing");
        Assert.Equal(actual: again, expected: slot);

        // A row reference that names no row, and a key that names no cell, read absent without a slot.
        Assert.False(condition: cells.TryText(key: "jump", mirror: mirror, rowReference: "icons", slot: out var unparsed, text: out _));
        Assert.Equal(actual: unparsed, expected: -1);
        Assert.False(condition: cells.TryText(key: null, mirror: mirror, rowReference: IconRow, slot: out var keyless, text: out _));
        Assert.Equal(actual: keyless, expected: -1);

        // Another mirror is another authority's rows, read through a slot of its own.
        var other = ClientFixtures.StateMirror(definition: Document(jump: "boot"));

        Assert.True(condition: cells.TryText(key: "jump", mirror: other, rowReference: IconRow, slot: out _, text: out var elsewhere));
        Assert.Equal(actual: elsewhere, expected: "boot");
    }
}
