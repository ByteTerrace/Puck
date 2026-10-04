using System.Text;
using Puck.Assets;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the shape fingerprint a machine-written world document carries beside its schema tag: the writer
/// stamps the one the ledger records, and a reader of the same schema under another shape refuses it by the
/// fingerprint's name before any other field is used.</summary>
public sealed class DocumentShapeLawTests {
    private const string Other = "0000000000000000";

    private static WorldReleaseManifest Manifest() => new() {
        CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
        Label = "shape-test",
        SourceRevision = new string(c: 'a', count: 40),
        EngineImageDigest = ("sha256:" + new string(c: 'b', count: 64)),
        PersistenceContract = "test",
        PeerProtocolContract = "test",
        Definitions = new Dictionary<string, string> { ["owner/world"] = ContentPin.Compute(content: "world"u8).ToString() },
        DefinitionFiles = new Dictionary<string, string> { ["owner/world"] = "world.json" },
    };

    [Fact]
    public void AReleaseManifestCarriesTheLedgersShapeAndAnotherShapeIsRefusedByIt() {
        var manifest = Manifest();
        var recorded = FormatLedgerShapes.Of(id: "WorldReleaseManifest.CurrentSchema");

        Assert.Contains(
            actualString: Encoding.UTF8.GetString(bytes: WorldReleaseManifest.Canonicalize(manifest: manifest)),
            expectedSubstring: $"\"shape\":\"{recorded}\""
        );
        Assert.True(
            condition: WorldReleaseManifest.TryValidate(manifest: manifest, reason: out var control),
            userMessage: control
        );
        Assert.False(condition: WorldReleaseManifest.TryValidate(manifest: (manifest with { Shape = Other }), reason: out var reason));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"shape fingerprint '{Other}' is not '{recorded}'"
        );
    }
    [Fact]
    public void AReceiptSnapshotOfAnotherShapeIsRefusedByItsFingerprintBeforeAnyFieldIsRead() {
        var snapshot = new WorldAuthorityReceiptSnapshot(
            Owner: Guid.NewGuid(),
            World: "world",
            Source: default!,
            Index: [],
            Nodes: new Dictionary<string, byte[]>()
        );

        Assert.Equal(
            actual: snapshot.Shape,
            expected: FormatLedgerShapes.Of(id: "WorldAuthorityReceiptSnapshot.CurrentSchema")
        );

        var refusal = Assert.Throws<InvalidDataException>(testCode: () => (snapshot with { Shape = Other }).Validate());

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: $"receipt snapshot shape fingerprint '{Other}'"
        );
    }
    [Fact]
    public async Task AReleaseGroupRecordOfAnotherShapeIsRefusedByItsFingerprint() {
        var token = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var blobs = new FakeObjectBlobStore();
        var target = Puck.Storage.AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobs,
            target: target
        );

        _ = await groups.CreateAsync(
            activeRelease: null,
            cancellationToken: token,
            deploymentGroup: "primary"
        );

        var key = Assert.Single(collection: await blobs.ListAsync(
            cancellationToken: token,
            keyPrefix: string.Empty,
            objectId: owner,
            target: target
        ));
        var recorded = FormatLedgerShapes.Of(id: "WorldReleaseGroupStore.Schema");
        var text = Encoding.UTF8.GetString(bytes: blobs.TryGetBytes(
            key: key,
            objectId: owner
        )!);

        Assert.Contains(
            actualString: text,
            expectedSubstring: $"\"shape\":\"{recorded}\""
        );
        Assert.NotNull(@object: await groups.LoadAsync(
            cancellationToken: token,
            deploymentGroup: "primary"
        ));
        blobs.Seed(
            objectId: owner,
            key: key,
            bytes: Encoding.UTF8.GetBytes(s: text.Replace(
                newValue: Other,
                oldValue: recorded
            ))
        );

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => groups.LoadAsync(
            cancellationToken: token,
            deploymentGroup: "primary"
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: $"shape fingerprint \"{Other}\""
        );
    }
    [Fact]
    public async Task AFixtureInventoryOfAnotherShapeIsRefusedByItsFingerprint() {
        var owner = Guid.NewGuid();
        var archive = new WorldReleaseFixtureArchive(
            owner: owner,
            store: new FakeObjectBlobStore(),
            target: Puck.Storage.AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true")
        );
        var manifest = new WorldReleaseFixtureManifest(
            Schema: WorldReleaseFixtureArchive.Schema,
            RequestId: Guid.NewGuid(),
            Group: "official",
            Release: ("sha256/" + new string(c: 'a', count: 64)),
            Owner: owner,
            MachineId: Guid.NewGuid(),
            Worlds: new Dictionary<string, WorldReleaseFixtureRow>()
        );

        Assert.Equal(
            actual: manifest.Shape,
            expected: FormatLedgerShapes.Of(id: "WorldReleaseFixtureArchive.Schema")
        );

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.ReadCheckpointAsync(
            manifest: (manifest with { Shape = Other }),
            world: "alpha",
            cancellationToken: TestContext.Current.CancellationToken
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: $"shape fingerprint '{Other}'"
        );
    }
}
