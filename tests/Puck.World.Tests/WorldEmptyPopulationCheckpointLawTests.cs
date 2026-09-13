using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldEmptyPopulationCheckpointLawTests {
    [Fact]
    public void AnEmptyWorldCanRestoreItsOwnCheckpointWithoutInventingAKit() {
        var definition = new WorldDefinition();
        var population = new WorldPopulation(definition: definition);
        var checkpoint = population.Capture();

        Assert.Empty(collection: checkpoint.Entries);
        population.Restore(
            checkpoint: checkpoint,
            defaults: definition.PlayerDefaults,
            tick: 0
        );
        var restored = population.Capture();

        Assert.Empty(collection: restored.Entries);
        Assert.Equal(
            checkpoint.SeatKit,
            restored.SeatKit
        );
        Assert.Equal(
            checkpoint.Generations,
            restored.Generations
        );
        Assert.Equal(
            checkpoint.Revision,
            restored.Revision
        );
        Assert.Throws<InvalidOperationException>(testCode: () => population.Restore(
            checkpoint: checkpoint with { SeatKit = 1 },
            defaults: definition.PlayerDefaults,
            tick: 0
        ));
        Assert.Throws<InvalidOperationException>(testCode: () => population.Restore(
            checkpoint: checkpoint with { SimulatedCount = 1 },
            defaults: definition.PlayerDefaults,
            tick: 0
        ));
        Assert.Empty(collection: population.Capture().Entries);
    }
}
