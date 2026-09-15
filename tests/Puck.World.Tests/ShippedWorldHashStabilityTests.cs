using Puck.Hosting;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class ShippedWorldHashStabilityTests {
    [Theory]
    [InlineData("src/Puck.World/Assets/worlds/puck.world.json")]
    [InlineData("src/Puck.World/Assets/worlds/pipeline.world.json")]
    public void ShippedWorlds_HaveNoVectorSpacesOrVectorRows(string relativePath) {
        var definition = AuthoredGameFixtures.Load(relativePath: relativePath);

        Assert.True(condition: (definition.Spaces is null || definition.Spaces.Count == 0), userMessage: $"{relativePath} should have no vector spaces.");
        Assert.All(collection: definition.State, action: row => {
            Assert.NotEqual(expected: CellKind.Vector, actual: row.Kind);
        });
    }

    [Fact]
    public void ShippedWorld_PuckWorld_HasEmptyVectorFrame_AndUnchangedAuthoritativeHash() {
        const string RelativePath = "src/Puck.World/Assets/worlds/puck.world.json";
        var catalog = TestHookInstaller.CreateMachineCatalog();
        var definition = AuthoredGameFixtures.Load(relativePath: RelativePath, catalog: catalog);
        var documentPath = Path.Combine(path1: AuthoredGameFixtures.Root, path2: RelativePath);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        var layout1 = new FrameLayout(
            rows: definition.State,
            spaces: name => WorldStateSpaces.Find(spaces: definition.Spaces, name: name),
            topology: name => WorldTopologyCompilation.Find(definition: definition, name: name)
        );
        Assert.Equal(expected: 0, actual: layout1.VectorLength);
        Assert.Equal(expected: 0, actual: layout1.VectorCellCount);

        // Boot server 1
        using var fixture1 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath,
            machineCatalog: catalog
        );

        var hash1Tick0 = WorldRuntimeStateHash.HashAuthoritative(server: fixture1.Server, tick: 0UL);
        Assert.NotEqual(expected: 0UL, actual: hash1Tick0);

        for (var tick = 1; tick <= 31; tick++) {
            fixture1.Step(stepTicks: width);
        }

        var hash1Tick31 = WorldRuntimeStateHash.HashAuthoritative(server: fixture1.Server, tick: 31UL);
        Assert.NotEqual(expected: 0UL, actual: hash1Tick31);
        Assert.NotEqual(expected: hash1Tick0, actual: hash1Tick31);

        // Boot server 2 from the exact same source to verify bit-identical stability
        using var fixture2 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath,
            machineCatalog: catalog
        );

        var hash2Tick0 = WorldRuntimeStateHash.HashAuthoritative(server: fixture2.Server, tick: 0UL);
        Assert.Equal(expected: hash1Tick0, actual: hash2Tick0);

        for (var tick = 1; tick <= 31; tick++) {
            fixture2.Step(stepTicks: width);
        }

        var hash2Tick31 = WorldRuntimeStateHash.HashAuthoritative(server: fixture2.Server, tick: 31UL);
        Assert.Equal(expected: hash1Tick31, actual: hash2Tick31);
    }

    [Fact]
    public void ShippedWorld_Pipeline_HasEmptyVectorFrame_AndUnchangedAuthoritativeHash() {
        const string RelativePath = "src/Puck.World/Assets/worlds/pipeline.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: RelativePath);
        var documentPath = Path.Combine(path1: AuthoredGameFixtures.Root, path2: RelativePath);

        var layout = new FrameLayout(
            rows: definition.State,
            spaces: name => WorldStateSpaces.Find(spaces: definition.Spaces, name: name),
            topology: name => WorldTopologyCompilation.Find(definition: definition, name: name)
        );
        Assert.Equal(expected: 0, actual: layout.VectorLength);
        Assert.Equal(expected: 0, actual: layout.VectorCellCount);

        using var fixture1 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath
        );

        var hash1 = WorldRuntimeStateHash.HashAuthoritative(server: fixture1.Server, tick: 0UL);
        Assert.NotEqual(expected: 0UL, actual: hash1);

        using var fixture2 = Fixtures.FreshServer(
            definition: definition,
            documentPath: documentPath
        );

        var hash2 = WorldRuntimeStateHash.HashAuthoritative(server: fixture2.Server, tick: 0UL);
        Assert.Equal(expected: hash1, actual: hash2);
    }
}
