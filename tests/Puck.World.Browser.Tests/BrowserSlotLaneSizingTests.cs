using Xunit;

using Puck.Physics.Motion;

namespace Puck.World.Browser.Tests;

/// <summary>The browser host sizes its arena's slot lanes from the document, like every other host: the lane widths
/// are folded columns of <c>StateArena.ComputeHash</c>, so a host that pinned them to the library default could not
/// be compared with one that did not.</summary>
public sealed class BrowserSlotLaneSizingTests {
    private static WorldDefinition Document(int capacity) => new(
        PopulationRaw: new WorldBodiesDefaults(CapacityRaw: capacity),
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(
            Body: [new ActionStateSlot(
                    Name: "ammo",
                    Kind: ActionStateKind.Counter,
                    Initial: 3f
                )],
            World: [
                new WorldStateRow(
                    Cells: [new StateCell(
                            Key: CellName.Parse(candidate: "k"),
                            Value: CellValue.Int(value: 1)
                        )],
                    Capacity: 4,
                    Kind: CellKind.Int,
                    Name: CellName.Parse(candidate: "counter")
                ),
            ]
        )
    );

    [Fact]
    public void ADocumentPastTheDefaultLaneWidthBootsAndItsWidthReachesTheStateHash() {
        var wide = new BrowserSession(definition: Document(capacity: (ArenaCapacity.DefaultParticipants * 2)));

        // Booting at all is the first half: a document authoring more bodies than the library's default lane width
        // must lay out, not refuse.
        Assert.True(condition: (Document(capacity: (ArenaCapacity.DefaultParticipants * 2)).Population.Capacity > ArenaCapacity.DefaultParticipants));
        Assert.Equal(
            expected: "1",
            actual: wide.ReadRow(
                key: "k",
                row: "counter"
            ).Value
        );

        // The second half: the width reaches the arena hash, so a host that pinned both lanes to the default would
        // answer the same digest for both documents.
        var narrow = new BrowserSession(definition: Document(capacity: ArenaCapacity.DefaultParticipants));

        Assert.NotEqual(
            actual: wide.StateHash(),
            expected: narrow.StateHash()
        );
    }
}
