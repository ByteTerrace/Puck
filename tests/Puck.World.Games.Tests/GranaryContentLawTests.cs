using Puck.Abstractions.Counting;
using Puck.Testing;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// Locks the debug world's granary board to its contract: a board facet over a 4 by 4 grid with a seated engage block, a
/// deal template over the feed's account rows whose variants choose a piece by kind, sixteen tiles whose influence
/// channels code the cell a piece stands on, and two staged coverage providers; and locks the deal's sweep to allocate
/// nothing on a quiet tick. What the board does with a feed (the cells, the coverage, the inspected piece, the offline
/// line) runs in <c>worlds/debug/granaries-tests.puck</c>.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class GranaryContentLawTests(ITestOutputHelper output) {
    private const string Debug = "worlds/debug/debug.puck";

    private static WorldDefinition Court => AuthoredGameFixtures.Load(relativePath: Debug);
    private static WorldPlacement Placement(string id) => Assert.Single(
        collection: Court.Placements,
        predicate: placement => (placement.Id == id)
    );

    [Fact]
    public void TheBoardIsATabletopOverTheGridTheStoresAreDealtOn() {
        var table = Placement(id: "granaryTable");

        Assert.Equal(
            expected: "granaryBoard",
            actual: table.Board!.Occupancy
        );
        Assert.Equal(
            expected: "granaryGrid",
            actual: table.Board!.Topology
        );
        Assert.NotNull(@object: table.Board!.Engage);

        var stores = Placement(id: "granaryStores");

        Assert.Equal(
            expected: "granaryTable",
            actual: stores.Parent
        );
        Assert.Equal(
            expected: "granaryNames",
            actual: stores.Deal!.Row
        );
        Assert.Equal(
            expected: "granaryKinds",
            actual: stores.Deal!.Variants!.Row
        );
        Assert.Equal(
            expected: "granaryPieceBlob",
            actual: stores.Deal!.Variants!.Map["BlobStorage"]
        );
        Assert.Equal(
            expected: 16,
            actual: WorldPlacementDeal.InstanceCount(
                template: stores,
                worldSeed: 0UL
            )
        );
        Assert.Contains(
            collection: stores.Spatial!,
            filter: static volume => (volume.Role == WorldPlacementSpatialRole.Occupation)
        );
        foreach (var creation in new[] { "granaryPiece", "granaryPieceV2", "granaryPieceBlob", "granaryPieceBlock", "granaryPieceFile" }) {
            Assert.Contains(
                collection: Court.Creations,
                filter: row => (row.Id == creation)
            );
        }
    }
    [Fact]
    public void EachTileCodesItsCellInTheInfluenceChannelsAPieceIsFoundBy() {
        for (var cell = 0; (cell < 16); cell++) {
            var tile = Placement(id: $"granaryTile-{cell}");
            var channels = tile.Spatial!
                .Where(predicate: static volume => (volume.Role == WorldPlacementSpatialRole.Influence))
                .Select(selector: static volume => volume.Channel)
                .ToArray();

            Assert.Contains(
                expected: "tile",
                collection: channels
            );
            for (var bit = 0; (bit < 4); bit++) {
                Assert.Equal(
                    expected: (((cell >> bit) & 1) == 1),
                    actual: channels.Contains(value: $"bit{bit}")
                );
            }
        }
    }
    [Fact]
    public void TheProvidersAreChannelsWhoseExtentsReadTheirStage() {
        foreach (var (id, channel, stage) in new[] { ("granaryIrrigation", "water", "granaryIrrigationSelected"), ("granaryPower", "power", "granaryPowerSelected") }) {
            var volume = Assert.Single(collection: Placement(id: id).Spatial!);

            Assert.Equal(
                expected: channel,
                actual: volume.Channel
            );
            Assert.Equal(
                expected: $"state.{stage}",
                actual: volume.Shape.HalfExtents.Reference
            );
        }
    }
    [Fact]
    public void TheFeedRowsAreBoundedToTheDealCapacityAndStartEmpty() {
        foreach (var name in new[] { "granaryNames", "granaryResources", "granaryRegions", "granaryKinds", "granarySkus", "granaryWaterCoverage", "granaryPowerCoverage", "granaryCellOf" }) {
            var row = Assert.Single(
                collection: Court.State,
                predicate: state => (state.Name.Value == name)
            );

            Assert.Equal(
                expected: 16,
                actual: row.Capacity
            );
            Assert.Empty(collection: (row.Cells ?? []));
        }

        var feed = Assert.Single(
            collection: Court.State,
            predicate: static state => (state.Name.Value == "granaryFeed")
        );

        Assert.Equal(
            expected: 0L,
            actual: Assert.Single(collection: feed.Cells!).Value.Raw
        );
        Assert.Contains(
            collection: Court.SpawnPoints,
            filter: static point => (point.Id == "granaries")
        );
        foreach (var rule in new[] { "granary-locate", "granary-water-coverage", "granary-power-coverage", "granary-irrigation-stage", "granary-power-stage", "granary-inspect-piece" }) {
            Assert.Contains(
                collection: Court.Rules!,
                filter: row => (row.Name.ToString() == rule)
            );
        }
    }
    /// <summary>A quiet tick — no row change, no other mutation — allocates nothing in the sweep: the per-tick allocation
    /// of the debug world's granary board at steady state, isolated from the rest of the world, is the same as the
    /// identical board without the deal facet.</summary>
    [Fact]
    public void AQuietTickAllocatesNothingInTheSweep() {
        var source = Court;
        var table = Placement(id: "granaryTable") with { Board = null };
        var template = Placement(id: "granaryStores");
        var board = Fixtures.BuildDocument() with {
            Text = source.Text,
            CreationsRaw = [.. source.Creations.Where(predicate: row => (row.Id.StartsWith(value: "granaryPiece", comparisonType: StringComparison.Ordinal) || (row.Id == "granaryTable")))],
            PlacementRowsRaw = [table, template],
            StateRaw = new WorldStateSection(World: [.. source.State.Where(predicate: static row => (row.Name.Value is "granaryNames" or "granaryKinds"))]),
        };
        var seed = new WorldMutation.Batch(
            Principal: Principal.Console,
            Mutations: [.. new[] { "bytrcstp001", "bytrcstp002", "bytrcstp003" }.Select(selector: static key => (WorldMutation)new WorldMutation.UpsertStateCell(
                Principal: Principal.Console,
                Row: "granaryNames",
                Key: key,
                Value: 0L,
                Kind: WorldDocumentWriteKind.Set,
                Text: key
            ))]
        );
        var dealt = Measure(
            definition: board,
            seed: seed
        );
        var control = Measure(
            definition: (board with { PlacementRowsRaw = [table, template with { Deal = null }] }),
            seed: seed
        );

        output.WriteLine(message: $"granaries: dealt median {dealt:N0} bytes/tick, control median {control:N0} bytes/tick");
        Assert.True(
            condition: (dealt <= control),
            userMessage: $"the granary board's quiet tick allocated {dealt:N0} bytes against the control's {control:N0}"
        );
    }

    private static long Measure(WorldDefinition definition, WorldMutation seed) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        fixture.Server.EnqueueMutation(mutation: seed);
        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step(stepTicks: width);
        }

        var samples = new long[120];

        for (var tick = 0; (tick < samples.Length); tick++) {
            samples[tick] = AllocationWindow.Total(window: () => fixture.Step(stepTicks: width));
        }

        Array.Sort(array: samples);

        return samples[(samples.Length / 2)];
    }
}
