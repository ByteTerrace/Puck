using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldEmptyPopulationCheckpointLawTests {
    [Fact]
    public void AnEmptyWorldCanRestoreItsOwnCheckpointWithoutInventingAKit() {
        var definition = new WorldDefinition();
        var population = new WorldPopulation(definition);
        var checkpoint = population.Capture();
        Assert.Empty(checkpoint.Entries);
        population.Restore(checkpoint, definition.PlayerDefaults, 0);
        var restored = population.Capture();
        Assert.Empty(restored.Entries);
        Assert.Equal(checkpoint.SeatKit, restored.SeatKit);
        Assert.Equal(checkpoint.Generations, restored.Generations);
        Assert.Equal(checkpoint.Revision, restored.Revision);
        Assert.Throws<InvalidOperationException>(() => population.Restore(checkpoint with { SeatKit = 1 }, definition.PlayerDefaults, 0));
        Assert.Throws<InvalidOperationException>(() => population.Restore(checkpoint with { SimulatedCount = 1 }, definition.PlayerDefaults, 0));
        Assert.Empty(population.Capture().Entries);
    }
}
