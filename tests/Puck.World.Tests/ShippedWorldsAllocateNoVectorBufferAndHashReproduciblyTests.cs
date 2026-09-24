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
        var definition = AuthoredGameFixtures.Nexus;
        var layout = ArenaLayout.Build(catalog: definition.StateCatalog, section: definition.StateRaw);

        Assert.Equal(expected: 0, actual: layout.VectorByteCount);

        var first = ShippedWorldIdleRuns.First;
        var second = ShippedWorldIdleRuns.Second;

        Assert.NotEqual(actual: first.Authoritative[0], expected: 0UL);
        Assert.NotEqual(actual: first.Authoritative[31], expected: 0UL);
        Assert.NotEqual(actual: first.Authoritative[31], expected: first.Authoritative[0]);

        // The second boot, from the same source, is bit-identical at boot and after the same ticks.
        Assert.Equal(actual: second.Authoritative[0], expected: first.Authoritative[0]);
        Assert.Equal(actual: second.Authoritative[31], expected: first.Authoritative[31]);
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
