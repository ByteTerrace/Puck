using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseCoordinatorContractLawTests {
    private static WorldReleaseManifest Manifest() => new() {
        CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
        Label = "contract-test",
        SourceRevision = new string(
        c: 'a',
        count: 40
    ),
        EngineImageDigest = ("sha256:" + new string(
        c: 'b',
        count: 64
    )),
        PersistenceContract = "test",
        PeerProtocolContract = "test",
        Definitions = new Dictionary<string, string> { ["owner/world"] = ContentPin.Compute(content: "world"u8).ToString() },
        DefinitionFiles = new Dictionary<string, string> { ["owner/world"] = "world.json" },
    };

    [Fact]
    public void CanonicalManifestAlwaysCarriesTheCurrentContract() {
        var manifest = Manifest();
        var canonical = Encoding.UTF8.GetString(bytes: WorldReleaseManifest.Canonicalize(manifest: manifest));

        Assert.Contains(
            actualString: canonical,
            expectedSubstring: $"\"coordinatorContract\":\"{WorldReleaseManifest.CurrentCoordinatorContract}\""
        );
        Assert.True(
            condition: WorldReleaseManifest.TryValidate(
                manifest: manifest,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            manifest.Identity,
            JsonSerializer.Deserialize<WorldReleaseManifest>(WorldReleaseManifest.Canonicalize(manifest: manifest))!.Identity
        );
    }
    [Fact]
    public void ManifestWithoutAContractDoesNotDeserialize() {
        var node = JsonNode.Parse(WorldReleaseManifest.Canonicalize(manifest: Manifest()))!.AsObject();

        Assert.True(condition: node.Remove(propertyName: "coordinatorContract"));
        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<WorldReleaseManifest>(node.ToJsonString()));
    }
    [InlineData("")]
    [InlineData("puck.world.release.metadata.v1")]
    [InlineData("puck.world.release.receipts.v1")]
    [InlineData("unknown-future-contract")]
    [Theory]
    public void AnyOtherContractIsRefusedByName(string contract) {
        Assert.False(condition: WorldReleaseManifest.TryValidate(
            manifest: Manifest() with { CoordinatorContract = contract },
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"unsupported release coordinator contract '{contract}'"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: WorldReleaseManifest.CurrentCoordinatorContract
        );
    }
    [Fact]
    public void MetadataTransitionsNeedNoSecondContract() {
        var source = Manifest();
        var target = source with {
            Label = "metadata",
            Definitions = new Dictionary<string, string> { ["owner/world"] = ContentPin.Compute(content: "changed"u8).ToString() },
        };

        Assert.True(
            condition: WorldReleaseTransitionPolicy.TryPrepare(
                changes: out var changes,
                reason: out var reason,
                source: source,
                target: target
            ),
            userMessage: reason
        );
        _ = Assert.Single(collection: changes);
    }
}
