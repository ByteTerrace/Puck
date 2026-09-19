using Puck.Hosting;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class ShippedWorldsAllocateNoVectorBufferAndHashReproduciblyTests {
    [InlineData("src/Puck.World/Assets/worlds/puck.world.json")]
    [InlineData("src/Puck.World/Assets/worlds/pipeline.world.json")]
    [Theory]
    public void ShippedWorlds_HaveNoVectorSpacesOrVectorRows(string relativePath) {
        var definition = AuthoredGameFixtures.Load(relativePath: relativePath);

        Assert.True(condition: ((definition.Spaces is null) || (definition.Spaces.Count == 0)), userMessage: $"{relativePath} should have no vector spaces.");
        Assert.All(collection: definition.State, action: row => {
            Assert.NotEqual(expected: CellKind.Vector, actual: row.Kind);
        });
    }
    [Fact]
    public void ShippedWorld_PuckWorld_HasEmptyVectorFrame_AndUnchangedAuthoritativeHash() {
        const string RelativePath = "src/Puck.World/Assets/worlds/puck.world.json";
        var catalog = TestHookInstaller.CreateMachineCatalog();
        var definition = AuthoredGameFixtures.Load(catalog: catalog, relativePath: RelativePath);
        var documentPath = Path.Combine(path1: AuthoredGameFixtures.Root, path2: RelativePath);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        var layout1 = ArenaLayout.Build(catalog: definition.StateCatalog, section: definition.StateRaw);

        Assert.Equal(expected: 0, actual: layout1.VectorByteCount);

        // Boot server 1
        using var fixture1 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath,
            machineCatalog: catalog
        );

        var hash1Tick0 = WorldStateHashComposition.HashAuthoritative(server: fixture1.Server, tick: 0UL);

        Assert.NotEqual(actual: hash1Tick0, expected: 0UL);

        for (var tick = 1; (tick <= 31); tick++) {
            fixture1.Step(stepTicks: width);
        }

        var hash1Tick31 = WorldStateHashComposition.HashAuthoritative(server: fixture1.Server, tick: 31UL);

        Assert.NotEqual(actual: hash1Tick31, expected: 0UL);
        Assert.NotEqual(actual: hash1Tick31, expected: hash1Tick0);

        // Boot server 2 from the exact same source to verify bit-identical stability
        using var fixture2 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath,
            machineCatalog: catalog
        );

        var hash2Tick0 = WorldStateHashComposition.HashAuthoritative(server: fixture2.Server, tick: 0UL);

        Assert.Equal(actual: hash2Tick0, expected: hash1Tick0);

        for (var tick = 1; (tick <= 31); tick++) {
            fixture2.Step(stepTicks: width);
        }

        var hash2Tick31 = WorldStateHashComposition.HashAuthoritative(server: fixture2.Server, tick: 31UL);

        Assert.Equal(actual: hash2Tick31, expected: hash1Tick31);
    }
    [Fact]
    public void ShippedWorld_Pipeline_HasEmptyVectorFrame_AndUnchangedAuthoritativeHash() {
        const string RelativePath = "src/Puck.World/Assets/worlds/pipeline.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: RelativePath);
        var documentPath = Path.Combine(path1: AuthoredGameFixtures.Root, path2: RelativePath);

        var layout = ArenaLayout.Build(catalog: definition.StateCatalog, section: definition.StateRaw);

        Assert.Equal(expected: 0, actual: layout.VectorByteCount);

        using var fixture1 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath
        );

        var hash1 = WorldStateHashComposition.HashAuthoritative(server: fixture1.Server, tick: 0UL);

        Assert.NotEqual(actual: hash1, expected: 0UL);

        using var fixture2 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath
        );

        var hash2 = WorldStateHashComposition.HashAuthoritative(server: fixture2.Server, tick: 0UL);

        Assert.Equal(actual: hash2, expected: hash1);
    }
}
