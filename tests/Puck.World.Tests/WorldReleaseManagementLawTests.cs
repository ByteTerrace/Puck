using System.Security.Cryptography;
using System.Text;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for immutable release identity, guarded operation progress, and the commit boundary.</summary>
public sealed class WorldReleaseManagementLawTests {
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri("UseDevelopmentStorage=true");

    [Fact]
    public void ManifestIdentityIsIndependentOfDictionaryInsertionOrderAndVerifiesFullHashes() {
        using var directory = new TempWorldDirectory();
        var world = directory.WriteText("puck.world.json", "world");
        var neighbor = directory.WriteText("neighbor.world.json", "neighbor");
        var worldHash = FullHash(File.ReadAllBytes(world));
        var neighborHash = FullHash(File.ReadAllBytes(neighbor));
        var asset = directory.WriteText("asset.bin", "asset");
        var assetHash = FullHash(File.ReadAllBytes(asset));
        var left = Manifest(new Dictionary<string, string> { ["puck"] = worldHash, ["neighbor"] = neighborHash }, new Dictionary<string, string> { ["asset.bin"] = assetHash });
        var right = left with { Definitions = new Dictionary<string, string> { ["neighbor"] = neighborHash, ["puck"] = worldHash }, DefinitionFiles = new Dictionary<string, string> { ["neighbor"] = "neighbor.world.json", ["puck"] = "puck.world.json" }, Artifacts = new Dictionary<string, string> { ["asset.bin"] = assetHash } };

        Assert.Equal(left.Identity, right.Identity);
        Assert.True(WorldReleaseManifest.TryVerify(left, directory.RootPath, out var reason), reason);
        Assert.False(WorldReleaseManifest.TryVerify(left with { DefinitionFiles = new Dictionary<string, string> { ["puck"] = "puck.world.json" } }, directory.RootPath, out reason));
        Assert.Contains("exactly match", reason, StringComparison.Ordinal);
        Assert.False(WorldReleaseManifest.TryVerify(left with { Definitions = null! }, directory.RootPath, out reason));
        Assert.Contains("inventories", reason, StringComparison.Ordinal);
        File.WriteAllText(asset, "changed");
        Assert.False(WorldReleaseManifest.TryVerify(left, directory.RootPath, out reason));
        Assert.Contains("hashes", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationStoreCreatesResumesAndGuardsRevision() {
        var blobStore = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var store = new WorldReleaseOperationStore(blobStore, Target, owner);
        var initial = Operation();
        var created = await store.CreateAsync(initial, TestContext.Current.CancellationToken);
        Assert.True(created.Ok, created.Detail);
        var current = (await store.LoadAsync(initial.DeploymentGroup, TestContext.Current.CancellationToken))!.Value;
        var resumed = await store.ResumeAsync(initial, TestContext.Current.CancellationToken);
        Assert.True(resumed.Ok, resumed.Detail);
        var drained = await store.AdvanceAsync(current, current.Record with { Phase = WorldReleaseOperationPhase.Drain, Revision = 1 }, TestContext.Current.CancellationToken);
        Assert.True(drained.Ok, drained.Detail);
        var activated = await store.AdvanceAsync(drained.Snapshot!.Value, drained.Snapshot.Value.Record with { Phase = WorldReleaseOperationPhase.Activate, Revision = 2 }, TestContext.Current.CancellationToken);
        Assert.True(activated.Ok, activated.Detail);
        var verified = await store.AdvanceAsync(activated.Snapshot!.Value, activated.Snapshot.Value.Record with { Phase = WorldReleaseOperationPhase.Verify, Revision = 3 }, TestContext.Current.CancellationToken);
        Assert.True(verified.Ok, verified.Detail);
        var advanced = await store.AdvanceAsync(verified.Snapshot!.Value, WorldReleaseRecoveryPolicy.Commit(verified.Snapshot.Value.Record), TestContext.Current.CancellationToken);
        Assert.True(advanced.Ok, advanced.Detail);
        var stale = await store.AdvanceAsync(current, current.Record with { Phase = WorldReleaseOperationPhase.Drain, Revision = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(WorldReleaseOperationOutcomeKind.PreconditionFailed, stale.Kind);

        var committed = (await store.LoadAsync(initial.DeploymentGroup, TestContext.Current.CancellationToken))!.Value;
        var opened = await store.AdvanceAsync(committed, WorldReleaseRecoveryPolicy.OpenAdmission(committed.Record), TestContext.Current.CancellationToken);
        Assert.True(opened.Ok, opened.Detail);
        var finalized = await store.FinalizeAsync(opened.Snapshot!.Value, TestContext.Current.CancellationToken);
        Assert.True(finalized.Ok, finalized.Detail);
        Assert.Equal(opened.Snapshot.Value.Record.Revision + 1, finalized.Snapshot!.Value.Record.Revision);
        Assert.Equal(WorldReleaseOperationPhase.Finalized, finalized.Snapshot.Value.Record.Phase);
    }

    [Fact]
    public async Task OperationStoreRejectsPhaseSkipAtCreation() {
        var store = new WorldReleaseOperationStore(new FakeObjectBlobStore(), Target, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(Operation() with { Phase = WorldReleaseOperationPhase.Drain }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OperationStoreCapturesRecoveryRootsOnceAfterDrain() {
        var store = new WorldReleaseOperationStore(new FakeObjectBlobStore(), Target, Guid.NewGuid());
        var initial = Operation() with { RecoveryRoots = new Dictionary<string, string>() };
        var created = await store.CreateAsync(initial, TestContext.Current.CancellationToken);
        Assert.True(created.Ok, created.Detail);
        var current = (await store.LoadAsync(initial.DeploymentGroup, TestContext.Current.CancellationToken))!.Value;
        var drained = current.Record with { Phase = WorldReleaseOperationPhase.Drain, Revision = 1 };
        var drainedResult = await store.AdvanceAsync(current, drained, TestContext.Current.CancellationToken);
        Assert.True(drainedResult.Ok, drainedResult.Detail);
        var captured = drained with { RecoveryRoots = new Dictionary<string, string> { ["row"] = "checkpoint:1" }, Revision = 2 };
        var capturedResult = await store.AdvanceAsync(drainedResult.Snapshot!.Value, captured, TestContext.Current.CancellationToken);
        Assert.True(capturedResult.Ok, capturedResult.Detail);
        var changed = captured with { RecoveryRoots = new Dictionary<string, string> { ["row"] = "checkpoint:2" }, Revision = 3 };
        var changedResult = await store.AdvanceAsync(capturedResult.Snapshot!.Value, changed, TestContext.Current.CancellationToken);
        Assert.Equal(WorldReleaseOperationOutcomeKind.Conflict, changedResult.Kind);
    }

    [Fact]
    public async Task OperationStoreRejectsDuplicateAndUndefinedPersistedSchema() {
        var blobStore = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var store = new WorldReleaseOperationStore(blobStore, Target, owner);
        var key = "private/puck/hosted/release-operations/primary.json";
        var duplicate = "{" +
            "\"operationId\":\"00000000-0000-0000-0000-000000000001\"," +
            "\"deploymentGroup\":\"primary\",\"sourceRelease\":\"source\",\"targetRelease\":\"target\"," +
            "\"phase\":0,\"phase\":99,\"admission\":0,\"committed\":false,\"recoveryRoots\":{},\"failure\":null,\"revision\":0}";
        blobStore.Seed(owner, key, Encoding.UTF8.GetBytes(duplicate));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("primary", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RecoveryPolicyNeverReopensAdmissionBeforeCommitOrRestoresAfterCommit() {
        var initial = Operation();
        var preCommit = WorldReleaseRecoveryPolicy.PrepareRecovery(initial, "candidate health failed");
        Assert.Equal(WorldReleaseOperationPhase.Recover, WorldReleaseRecoveryPolicy.Recover(preCommit));
        Assert.Equal(WorldReleaseAdmissionState.Closed, preCommit.Admission);
        Assert.Throws<InvalidOperationException>(() => WorldReleaseRecoveryPolicy.OpenAdmission(preCommit));

        var committed = WorldReleaseRecoveryPolicy.Commit(initial);
        Assert.Equal(WorldReleaseOperationPhase.Commit, WorldReleaseRecoveryPolicy.Recover(committed));
        Assert.Throws<InvalidOperationException>(() => WorldReleaseRecoveryPolicy.PrepareRecovery(committed, "late failure"));
        Assert.Equal(WorldReleaseAdmissionState.Open, WorldReleaseRecoveryPolicy.OpenAdmission(committed).Admission);
    }

    [Fact]
    public void QualificationRequiresTheSamePersistenceAndPeerContracts() {
        var source = Manifest(new Dictionary<string, string> { ["world"] = "sha256/" + new string('a', 64) }, new Dictionary<string, string>());
        var target = source with { EngineImageDigest = "sha256:" + new string('b', 64) };
        Assert.True(WorldReleaseTransitionPolicy.TryPrepare(source, target, out var changes, out var reason), reason);
        Assert.Empty(changes);
        var changedDefinition = target with { Definitions = new Dictionary<string, string> { ["world"] = "sha256/" + new string('e', 64) } };
        Assert.False(WorldReleaseTransitionPolicy.TryPrepare(source, changedDefinition, out changes, out reason));
        Assert.Contains("state-preservation", reason, StringComparison.Ordinal);
        Assert.False(WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, target with { PersistenceContract = "other" }, out reason));
        Assert.Contains("persistence", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineOnlyQualificationRefusesChangedArtifactPins() {
        var source = Manifest(new Dictionary<string, string> { ["world"] = "sha256/" + new string('a', 64) }, new Dictionary<string, string> { ["neighbor"] = "sha256/" + new string('b', 64) });
        var changed = source with { Artifacts = new Dictionary<string, string> { ["neighbor"] = "sha256/" + new string('c', 64) } };
        Assert.False(WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, changed, out var reason));
        Assert.Contains("artifact", reason, StringComparison.Ordinal);
    }

    private static WorldReleaseManifest Manifest(IReadOnlyDictionary<string, string> definitions, IReadOnlyDictionary<string, string> artifacts) => new() {
        Label = "test",
        SourceRevision = new string('c', 40),
        EngineImageDigest = "sha256:" + new string('d', 64),
        Definitions = definitions,
        DefinitionFiles = definitions.Keys.ToDictionary(static key => key, static key => key + ".world.json"),
        Artifacts = artifacts,
        PersistenceContract = "puck.world.persistence.v1",
        PeerProtocolContract = "puck.world.peer.v1",
    };

    private static WorldReleaseOperationRecord Operation() => new() {
        OperationId = Guid.NewGuid(),
        DeploymentGroup = "primary",
        SourceRelease = "sha256/" + new string('a', 64),
        TargetRelease = "sha256/" + new string('b', 64),
        Phase = WorldReleaseOperationPhase.Prepare,
        RecoveryRoots = new Dictionary<string, string> { ["row"] = "checkpoint:1" },
        Revision = 0,
    };

    private static string FullHash(byte[] bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
