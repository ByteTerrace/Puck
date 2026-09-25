using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the door an operator names an instance through (<c>world.instance.start</c>, by
/// <see cref="WorldInstanceHost.TryStartAuthored"/>) refuses a name carrying <see cref="GeneratedName.FileJoiner"/>,
/// the character the engine joins the names of the instances it starts itself with, and names the reservation; the
/// engine's own door does not, and a name without the character reaches the ordinary checks.</summary>
public sealed class WorldInstanceNameLawTests {
    private static WorldInstanceHost Host(TemporaryDirectory root) => new(
        admitsSpawn: true,
        applicationStopping: CancellationToken.None,
        machineHostFactory: Fixtures.MachineHostFactory,
        machineId: Guid.NewGuid(),
        resolver: new WorldSessionResolver(),
        seats: WorldEmbodiedSeats.None,
        stateRoot: new WorldStateRoot(path: root.RootPath)
    );

    [InlineData("dungeon~3")]
    [InlineData("6~global7~dungeon")]
    [InlineData("~")]
    [Theory]
    public void AnOperatorNameCarryingTheFileJoinerIsRefusedAsReserved(string name) {
        using var root = new TemporaryDirectory();
        using var host = Host(root: root);

        Assert.False(condition: host.TryStartAuthored(
            instance: out var instance,
            name: name,
            path: "no-such-world.world.json",
            reason: out var reason
        ));
        Assert.Null(@object: instance);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "reserves");
        // The engine's own door takes the same name to its ordinary checks, which is what lets it start the instances
        // it names this way.
        Assert.False(condition: host.TryStart(
            instance: out _,
            name: name,
            path: "no-such-world.world.json",
            reason: out var engineReason
        ));
        Assert.DoesNotContain(actualString: engineReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "reserves");
    }
    // The control: a name without the character is refused only for the missing document, never as reserved.
    [Fact]
    public void AnOperatorNameWithoutTheFileJoinerReachesTheOrdinaryChecks() {
        using var root = new TemporaryDirectory();
        using var host = Host(root: root);

        Assert.False(condition: host.TryStartAuthored(
            instance: out _,
            name: "dungeon-3",
            path: "no-such-world.world.json",
            reason: out var reason
        ));
        Assert.DoesNotContain(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "reserves");
    }
}
