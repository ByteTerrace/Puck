using System.Security.Cryptography;
using System.Text;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for release identity, structural qualification, and the guarded group root.</summary>
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
    public async Task GroupRootUsesGuardedSequentialTransitionsAndRetainsHistory() {
        var blobStore = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(blobStore, Target, owner);
        var created = await groups.CreateAsync("primary", "release-a", TestContext.Current.CancellationToken);
        Assert.True(created.Ok, created.Detail);
        var started = await groups.BeginAsync(created.Snapshot!.Value, Guid.NewGuid(), "release-b", TestContext.Current.CancellationToken);
        Assert.True(started.Ok, started.Detail);
        var pending = started.Snapshot!.Value;
        await Assert.ThrowsAsync<InvalidDataException>(() => groups.AdvanceAsync(pending, pending.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = pending.Record.Revision + 1 }, TestContext.Current.CancellationToken));

        var roots = new Dictionary<string, string> { ["row"] = "checkpoint:1" };
        var drained = await groups.AdvanceAsync(pending, pending.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = roots, Revision = pending.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.True(drained.Ok, drained.Detail);
        var stale = await groups.AdvanceAsync(pending, pending.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = roots, Revision = pending.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(WorldReleaseOperationOutcomeKind.PreconditionFailed, stale.Kind);

        var activated = await groups.AdvanceAsync(drained.Snapshot!.Value, drained.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = drained.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.True(activated.Ok, activated.Detail);
        var verified = await groups.AdvanceAsync(activated.Snapshot!.Value, activated.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = activated.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.True(verified.Ok, verified.Detail);
        var identity = new WorldAuthorityIdentity(owner, SafeName.Parse("row"));
        var committed = await groups.CommitAsync(verified.Snapshot!.Value, [(identity, new WorldAuthorityFence(1, Guid.NewGuid(), "root"))], TestContext.Current.CancellationToken);
        Assert.True(committed.Ok, committed.Detail);
        var committedState = committed.Snapshot!.Value;
        var opened = await groups.OpenAdmissionAsync(committedState, "release-b", committedState.Record.PendingOperationId!.Value, committedState.Record.AuthorityLease, TestContext.Current.CancellationToken);
        Assert.True(opened.Ok, opened.Detail);
        var finalized = await groups.FinalizeAsync(opened.Snapshot!.Value, TestContext.Current.CancellationToken);
        Assert.True(finalized.Ok, finalized.Detail);
        Assert.False(finalized.Snapshot!.Value.Record.RollbackEligible);
        Assert.Equal("checkpoint:1", finalized.Snapshot.Value.Record.History[0].RecoveryRoots["row"]);
        Assert.False((await groups.BeginAsync(finalized.Snapshot.Value, pending.Record.PendingOperationId!.Value, "release-c", TestContext.Current.CancellationToken)).Ok);
        Assert.True((await groups.BeginAsync(finalized.Snapshot.Value, Guid.NewGuid(), "release-c", TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task GroupRootRejectsMalformedDuplicateMembers() {
        var blobStore = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(blobStore, Target, owner);
        var duplicate = "{" +
            "\"schema\":\"puck.world.release-group.v1\",\"deploymentGroup\":\"primary\",\"owner\":\"" + owner + "\"," +
            "\"activeRelease\":\"release-a\",\"previousRelease\":null,\"pendingOperationId\":null,\"pendingSourceRelease\":null,\"pendingTargetRelease\":null,\"pendingPhase\":null,\"pendingCommitted\":false,\"pendingFailure\":null," +
            "\"recoveryRoots\":{},\"admission\":\"Open\",\"rollbackEligible\":false,\"authorityLease\":\"00000000-0000-0000-0000-000000000000\",\"history\":[],\"revision\":0,\"revision\":0}";
        blobStore.Seed(owner, "private/puck/hosted/release-groups/primary.json", Encoding.UTF8.GetBytes(duplicate));
        await Assert.ThrowsAsync<InvalidDataException>(() => groups.LoadAsync("primary", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreCommitRecoveryRetainsSourceAndRequiresFreshAdmissionPublication() {
        var groups = new WorldReleaseGroupStore(new FakeObjectBlobStore(), Target, Guid.NewGuid());
        var created = await groups.CreateAsync("primary", "release-a", TestContext.Current.CancellationToken);
        var started = await groups.BeginAsync(created.Snapshot!.Value, Guid.NewGuid(), "release-b", TestContext.Current.CancellationToken);
        var drained = await groups.AdvanceAsync(started.Snapshot!.Value, started.Snapshot.Value.Record with {
            PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed,
            RecoveryRoots = new Dictionary<string, string> { ["row"] = "checkpoint:1" }, Revision = started.Snapshot.Value.Record.Revision + 1
        }, TestContext.Current.CancellationToken);
        var recovering = await groups.BeginRecoveryAsync(drained.Snapshot!.Value, "candidate health failed", TestContext.Current.CancellationToken);
        var restored = await groups.RecordRecoveryRestoredAsync(recovering.Snapshot!.Value, TestContext.Current.CancellationToken);
        var recovered = await groups.CompleteRecoveryAsync(restored.Snapshot!.Value, TestContext.Current.CancellationToken);
        Assert.True(recovered.Ok, recovered.Detail);
        Assert.Equal("release-a", recovered.Snapshot!.Value.Record.ActiveRelease);
        Assert.Equal(WorldReleaseAdmissionState.Closed, recovered.Snapshot.Value.Record.Admission);
        Assert.True((await groups.OpenRecoveredAdmissionAsync(recovered.Snapshot.Value, "release-a", Guid.NewGuid(), TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task CoordinatorRequiresPairEvidenceAndStartsRollbackFromRetainedPredecessor() {
        var owner = Guid.NewGuid();
        var source = Manifest(new Dictionary<string, string> { ["world"] = "sha256/" + new string('a', 64) }, new Dictionary<string, string>()) with { EngineImageDigest = "sha256:" + new string('a', 64) };
        var target = source with { EngineImageDigest = "sha256:" + new string('b', 64) };
        var groups = new WorldReleaseGroupStore(new FakeObjectBlobStore(), Target, owner);
        var created = await groups.CreateAsync("primary", source.Identity, TestContext.Current.CancellationToken);
        var coordinator = new WorldReleaseCoordinator(groups);
        var refused = await coordinator.BeginDeploymentAsync(created.Snapshot!.Value, source, target, qualificationRunner: null, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(WorldReleaseOperationOutcomeKind.Conflict, refused.Kind);
        var evidence = Evidence(source, target);
        var started = await coordinator.BeginDeploymentAsync(created.Snapshot.Value, source, target, new FixedQualificationRunner(evidence), Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.True(started.Ok, started.Detail);
        var drained = await coordinator.RecordDrainAsync(started.Snapshot!.Value, new Dictionary<string, string> { ["row"] = "root-1" }, TestContext.Current.CancellationToken);
        var activated = await coordinator.RecordActivationAsync(drained.Snapshot!.Value, TestContext.Current.CancellationToken);
        var verified = await coordinator.RecordVerificationAsync(activated.Snapshot!.Value, TestContext.Current.CancellationToken);
        var identity = new WorldAuthorityIdentity(owner, SafeName.Parse("row"));
        var committed = await coordinator.CommitAsync(verified.Snapshot!.Value, [(identity, new WorldAuthorityFence(1, Guid.NewGuid(), "root"))], TestContext.Current.CancellationToken);
        var opened = await coordinator.PublishAdmissionAsync(committed.Snapshot!.Value, target.Identity, committed.Snapshot.Value.Record.PendingOperationId!.Value, committed.Snapshot.Value.Record.AuthorityLease, TestContext.Current.CancellationToken);
        Assert.True(opened.Ok, opened.Detail);

        Assert.False((await groups.BeginRollbackAsync(opened.Snapshot!.Value, opened.Snapshot.Value.Record.PendingOperationId!.Value, TestContext.Current.CancellationToken)).Ok);
        var rollback = await coordinator.BeginRollbackAsync(opened.Snapshot!.Value, target, source, new FixedQualificationRunner(Evidence(target, source)), Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.True(rollback.Ok, rollback.Detail);
        Assert.Equal(source.Identity, rollback.Snapshot!.Value.Record.PendingTargetRelease);
        Assert.Single(rollback.Snapshot.Value.Record.History);
        Assert.Equal("rollback-started", rollback.Snapshot.Value.Record.History[0].Result);
        var recovered = await CompleteBookkeepingRecoveryAsync(groups, rollback.Snapshot.Value);
        Assert.True(recovered.Ok, recovered.Detail);
        Assert.True(recovered.Snapshot!.Value.Record.RollbackEligible);
        Assert.False((await groups.BeginRollbackAsync(recovered.Snapshot.Value, rollback.Snapshot.Value.Record.PendingOperationId!.Value, TestContext.Current.CancellationToken)).Ok);
        var retry = await coordinator.BeginRollbackAsync(recovered.Snapshot.Value, target, source, new FixedQualificationRunner(Evidence(target, source)), Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.True(retry.Ok, retry.Detail);
        var retriedRecovery = await CompleteBookkeepingRecoveryAsync(groups, retry.Snapshot!.Value);
        var finalized = await coordinator.FinalizeAsync(retriedRecovery.Snapshot!.Value, TestContext.Current.CancellationToken);
        Assert.True(finalized.Ok, finalized.Detail);
        Assert.False(finalized.Snapshot!.Value.Record.RollbackEligible);
        Assert.Equal(target.Identity, finalized.Snapshot.Value.Record.ActiveRelease);
        Assert.Equal(source.Identity, finalized.Snapshot.Value.Record.PreviousRelease);
    }

    [Fact]
    public void StructuralCompatibilityDoesNotAuthorizeChangedDefinitionsOrArtifacts() {
        var source = Manifest(new Dictionary<string, string> { ["world"] = "sha256/" + new string('a', 64) }, new Dictionary<string, string> { ["neighbor"] = "sha256/" + new string('b', 64) });
        var same = source with { EngineImageDigest = "sha256:" + new string('b', 64) };
        Assert.True(WorldReleaseTransitionPolicy.TryPrepare(source, same, out var changes, out var reason), reason);
        Assert.Empty(changes);
        var changedDefinition = same with { Definitions = new Dictionary<string, string> { ["world"] = "sha256/" + new string('e', 64) } };
        Assert.False(WorldReleaseTransitionPolicy.TryPrepare(source, changedDefinition, out changes, out reason));
        Assert.Contains("metadata coordinator contract", reason, StringComparison.Ordinal);
        var coordinated = changedDefinition with { CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract };
        Assert.True(WorldReleaseTransitionPolicy.TryPrepare(source, coordinated, out changes, out reason), reason);
        Assert.Equal(new WorldReleaseDefinitionChange("world", source.Definitions["world"], coordinated.Definitions["world"]), Assert.Single(changes));
        Assert.False(WorldReleaseCoordinator.TryQualifyPair(source, coordinated, null, out reason));
        Assert.Contains("runner receipt", reason, StringComparison.Ordinal);
        Assert.True(WorldReleaseTransitionPolicy.TryPrepare(coordinated, source, out var reverse, out reason), reason);
        Assert.Equal(WorldReleaseTransitionPolicy.Reverse(changes), reverse);
        var changedArtifact = source with { Artifacts = new Dictionary<string, string> { ["neighbor"] = "sha256/" + new string('c', 64) } };
        Assert.False(WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, changedArtifact, out reason));
        Assert.Contains("artifact", reason, StringComparison.Ordinal);
        Assert.False(WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, same with { PersistenceContract = "other" }, out reason));
        Assert.Contains("persistence", reason, StringComparison.Ordinal);
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

    private static string FullHash(byte[] bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void QualificationRequiresCompleteMatchingImportsInBothDirections() {
        var source = Manifest(new Dictionary<string, string> { ["row"] = FullHash("world"u8.ToArray()) }, new Dictionary<string, string>());
        var target = source with { Label = "candidate", EngineImageDigest = "sha256:" + new string('e', 64) };
        var evidence = Evidence(source, target);
        Assert.True(WorldReleaseCoordinator.TryQualifyPair(source, target, evidence, out var reason), reason);
        Assert.False(WorldReleaseCoordinator.TryQualifyPair(source, target, evidence with { TargetStateHash = FullHash("lost inventory"u8.ToArray()) }, out _));
        Assert.False(WorldReleaseCoordinator.TryQualifyPair(source, target, evidence with { ReverseReferenceStateHash = FullHash("lost new state"u8.ToArray()) }, out _));
        Assert.False(WorldReleaseCoordinator.TryQualifyPair(source, target, evidence with { SourceStateHash = "same", TargetStateHash = "same" }, out _));
        Assert.False(WorldReleaseCoordinator.TryQualifyPair(source, target, evidence with { EvidenceId = "operator says it works" }, out _));
    }

    // These laws exercise the ledger. WorldReleaseCutoverLawTests separately executes real root restoration.
    private static async Task<WorldReleaseGroupOutcome> CompleteBookkeepingRecoveryAsync(WorldReleaseGroupStore groups, WorldReleaseGroupSnapshot current) {
        var token = TestContext.Current.CancellationToken;
        var drained = await new WorldReleaseCoordinator(groups).RecordDrainAsync(current, new Dictionary<string, string> { ["row"] = "protected-root" }, token);
        var recovering = await groups.BeginRecoveryAsync(drained.Snapshot!.Value, "rollback candidate refused", token);
        var restored = await groups.RecordRecoveryRestoredAsync(recovering.Snapshot!.Value, token);
        return await groups.CompleteRecoveryAsync(restored.Snapshot!.Value, token, Guid.NewGuid());
    }

    private static WorldReleaseQualificationReceipt Evidence(WorldReleaseManifest source, WorldReleaseManifest target) => new() {
        SourceRelease = source.Identity,
        TargetRelease = target.Identity,
        EvidenceId = FullHash("qualification/test"u8.ToArray()),
        SourceStateHash = FullHash("source import"u8.ToArray()),
        TargetStateHash = FullHash("source import"u8.ToArray()),
        ReverseStateHash = FullHash("target continuation import"u8.ToArray()),
        ReverseReferenceStateHash = FullHash("target continuation import"u8.ToArray()),
    };

    private sealed class FixedQualificationRunner(WorldReleaseQualificationReceipt receipt) : IWorldReleaseQualificationRunner {
        public Task<WorldReleaseQualificationReceipt?> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldReleaseQualificationReceipt?>(receipt with { SourceRelease = source.Identity, TargetRelease = target.Identity });
    }
}
