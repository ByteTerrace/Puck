using System.Security.Cryptography;
using System.Text;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for release identity, structural qualification, and the guarded group root.</summary>
public sealed class WorldReleaseManagementLawTests {
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

    // These laws exercise the ledger. WorldReleaseCutoverLawTests separately executes real root restoration.
    private static async Task<WorldReleaseGroupOutcome> CompleteBookkeepingRecoveryAsync(WorldReleaseGroupStore groups, WorldReleaseGroupSnapshot current) {
        var token = TestContext.Current.CancellationToken;
        var drained = await new WorldReleaseCoordinator(groups: groups).RecordDrainAsync(
            current,
            new Dictionary<string, string> { ["row"] = "protected-root" },
            token
        );
        var recovering = await groups.BeginRecoveryAsync(
            drained.Snapshot!.Value,
            "rollback candidate refused",
            token
        );
        var restored = await groups.RecordRecoveryRestoredAsync(
            recovering.Snapshot!.Value,
            token
        );

        return await groups.CompleteRecoveryAsync(
            restored.Snapshot!.Value,
            token,
            Guid.NewGuid()
        );
    }
    private static WorldReleaseQualificationReceipt Evidence(WorldReleaseManifest source, WorldReleaseManifest target) => new() {
        SourceRelease = source.Identity,
        TargetRelease = target.Identity,
        EvidenceId = FullHash(bytes: "qualification/test"u8.ToArray()),
        SourceStateHash = FullHash(bytes: "source import"u8.ToArray()),
        TargetStateHash = FullHash(bytes: "source import"u8.ToArray()),
        ReverseStateHash = FullHash(bytes: "target continuation import"u8.ToArray()),
        ReverseReferenceStateHash = FullHash(bytes: "target continuation import"u8.ToArray()),
    };
    private static string FullHash(byte[] bytes) => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
    private static WorldReleaseManifest Manifest(IReadOnlyDictionary<string, string> definitions, IReadOnlyDictionary<string, string> artifacts) => new() {
        Label = "test",
        SourceRevision = new string(
        c: 'c',
        count: 40
    ),
        EngineImageDigest = ("sha256:" + new string(
        c: 'd',
        count: 64
    )),
        Definitions = definitions,
        DefinitionFiles = definitions.Keys.ToDictionary(
        elementSelector: static key => (key + ".world.json"),
        keySelector: static key => key
    ),
        Artifacts = artifacts,
        PersistenceContract = "puck.world.persistence.v1",
        PeerProtocolContract = "puck.world.peer.v1",
    };

    [Fact]
    public async Task CoordinatorRequiresPairEvidenceAndStartsRollbackFromRetainedPredecessor() {
        var owner = Guid.NewGuid();
        var source = Manifest(
            new Dictionary<string, string> { ["world"] = ("sha256/" + new string(
                c: 'a',
                count: 64
            )) },
            new Dictionary<string, string>()
        ) with { EngineImageDigest = ("sha256:" + new string(
            c: 'a',
            count: 64
        )) };
        var target = source with { EngineImageDigest = ("sha256:" + new string(
            c: 'b',
            count: 64
        )) };
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var created = await groups.CreateAsync(
            "primary",
            source.Identity,
            TestContext.Current.CancellationToken
        );
        var coordinator = new WorldReleaseCoordinator(groups: groups);
        var refused = await coordinator.BeginDeploymentAsync(
            created.Snapshot!.Value,
            source,
            target,
            qualificationRunner: null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorldReleaseOperationOutcomeKind.Conflict,
            refused.Kind
        );
        var evidence = Evidence(
            source: source,
            target: target
        );
        var started = await coordinator.BeginDeploymentAsync(
            created.Snapshot.Value,
            source,
            target,
            new FixedQualificationRunner(receipt: evidence),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: started.Ok,
            userMessage: started.Detail
        );
        var drained = await coordinator.RecordDrainAsync(
            started.Snapshot!.Value,
            new Dictionary<string, string> { ["row"] = "root-1" },
            TestContext.Current.CancellationToken
        );
        var activated = await coordinator.RecordActivationAsync(
            drained.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );
        var verified = await coordinator.RecordVerificationAsync(
            activated.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "row")
        );
        var committed = await coordinator.CommitAsync(
            verified.Snapshot!.Value,
            [(identity, new WorldAuthorityFence(
                    1,
                    Guid.NewGuid(),
                    "root"
                ))],
            TestContext.Current.CancellationToken
        );
        var opened = await coordinator.PublishAdmissionAsync(
            committed.Snapshot!.Value,
            target.Identity,
            committed.Snapshot.Value.Record.PendingOperationId!.Value,
            committed.Snapshot.Value.Record.AuthorityLease,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: opened.Ok,
            userMessage: opened.Detail
        );

        Assert.False(condition: (await groups.BeginRollbackAsync(
            opened.Snapshot!.Value,
            opened.Snapshot.Value.Record.PendingOperationId!.Value,
            TestContext.Current.CancellationToken
        )).Ok);
        var rollback = await coordinator.BeginRollbackAsync(
            opened.Snapshot!.Value,
            target,
            source,
            new FixedQualificationRunner(receipt: Evidence(
                source: target,
                target: source
            )),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: rollback.Ok,
            userMessage: rollback.Detail
        );
        Assert.Equal(
            source.Identity,
            rollback.Snapshot!.Value.Record.PendingTargetRelease
        );
        Assert.Single(collection: rollback.Snapshot.Value.Record.History);
        Assert.Equal(
            "rollback-started",
            rollback.Snapshot.Value.Record.History[0].Result
        );
        var recovered = await CompleteBookkeepingRecoveryAsync(
            groups,
            rollback.Snapshot.Value
        );

        Assert.True(
            condition: recovered.Ok,
            userMessage: recovered.Detail
        );
        Assert.True(condition: recovered.Snapshot!.Value.Record.RollbackEligible);
        Assert.False(condition: (await groups.BeginRollbackAsync(
            recovered.Snapshot.Value,
            rollback.Snapshot.Value.Record.PendingOperationId!.Value,
            TestContext.Current.CancellationToken
        )).Ok);
        var retry = await coordinator.BeginRollbackAsync(
            recovered.Snapshot.Value,
            target,
            source,
            new FixedQualificationRunner(receipt: Evidence(
                source: target,
                target: source
            )),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: retry.Ok,
            userMessage: retry.Detail
        );
        var retriedRecovery = await CompleteBookkeepingRecoveryAsync(
            groups,
            retry.Snapshot!.Value
        );
        var finalized = await coordinator.FinalizeAsync(
            retriedRecovery.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: finalized.Ok,
            userMessage: finalized.Detail
        );
        Assert.False(condition: finalized.Snapshot!.Value.Record.RollbackEligible);
        Assert.Equal(
            target.Identity,
            finalized.Snapshot.Value.Record.ActiveRelease
        );
        Assert.Equal(
            source.Identity,
            finalized.Snapshot.Value.Record.PreviousRelease
        );
    }
    [Fact]
    public async Task GroupRootRejectsMalformedDuplicateMembers() {
        var blobStore = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobStore,
            target: Target
        );
        var duplicate = (((("{\"schema\":\"puck.world.release-group.v1\",\"deploymentGroup\":\"primary\",\"owner\":\"" + owner) + "\",") +
            "\"activeRelease\":\"release-a\",\"previousRelease\":null,\"pendingOperationId\":null,\"pendingSourceRelease\":null,\"pendingTargetRelease\":null,\"pendingPhase\":null,\"pendingCommitted\":false,\"pendingFailure\":null,") +
            "\"recoveryRoots\":{},\"admission\":\"Open\",\"rollbackEligible\":false,\"authorityLease\":\"00000000-0000-0000-0000-000000000000\",\"history\":[],\"revision\":0,\"revision\":0}");

        blobStore.Seed(
            owner,
            "private/puck/hosted/release-groups/primary.json",
            Encoding.UTF8.GetBytes(s: duplicate)
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => groups.LoadAsync(
            "primary",
            TestContext.Current.CancellationToken
        ));
    }
    [Fact]
    public async Task GroupRootUsesGuardedSequentialTransitionsAndRetainsHistory() {
        var blobStore = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobStore,
            target: Target
        );
        var created = await groups.CreateAsync(
            "primary",
            "release-a",
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: created.Ok,
            userMessage: created.Detail
        );
        var started = await groups.BeginAsync(
            created.Snapshot!.Value,
            Guid.NewGuid(),
            "release-b",
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: started.Ok,
            userMessage: started.Detail
        );
        var pending = started.Snapshot!.Value;

        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => groups.AdvanceAsync(
            pending,
            pending.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = (pending.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        ));

        var roots = new Dictionary<string, string> { ["row"] = "checkpoint:1" };
        var drained = await groups.AdvanceAsync(
            pending,
            pending.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = roots, Revision = (pending.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: drained.Ok,
            userMessage: drained.Detail
        );
        var stale = await groups.AdvanceAsync(
            pending,
            pending.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = roots, Revision = (pending.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorldReleaseOperationOutcomeKind.PreconditionFailed,
            stale.Kind
        );

        var activated = await groups.AdvanceAsync(
            drained.Snapshot!.Value,
            drained.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = (drained.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: activated.Ok,
            userMessage: activated.Detail
        );
        var verified = await groups.AdvanceAsync(
            activated.Snapshot!.Value,
            activated.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = (activated.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: verified.Ok,
            userMessage: verified.Detail
        );
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "row")
        );
        var committed = await groups.CommitAsync(
            verified.Snapshot!.Value,
            [(identity, new WorldAuthorityFence(
                    1,
                    Guid.NewGuid(),
                    "root"
                ))],
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: committed.Ok,
            userMessage: committed.Detail
        );
        var committedState = committed.Snapshot!.Value;
        var opened = await groups.OpenAdmissionAsync(
            committedState,
            "release-b",
            committedState.Record.PendingOperationId!.Value,
            committedState.Record.AuthorityLease,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: opened.Ok,
            userMessage: opened.Detail
        );
        var finalized = await groups.FinalizeAsync(
            opened.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: finalized.Ok,
            userMessage: finalized.Detail
        );
        Assert.False(condition: finalized.Snapshot!.Value.Record.RollbackEligible);
        Assert.Equal(
            "checkpoint:1",
            finalized.Snapshot.Value.Record.History[0].RecoveryRoots["row"]
        );
        Assert.False(condition: (await groups.BeginAsync(
            finalized.Snapshot.Value,
            pending.Record.PendingOperationId!.Value,
            "release-c",
            TestContext.Current.CancellationToken
        )).Ok);
        Assert.True(condition: (await groups.BeginAsync(
            finalized.Snapshot.Value,
            Guid.NewGuid(),
            "release-c",
            TestContext.Current.CancellationToken
        )).Ok);
    }
    [Fact]
    public void ManifestIdentityIsIndependentOfDictionaryInsertionOrderAndVerifiesFullHashes() {
        using var directory = new TempWorldDirectory();
        var world = directory.WriteText(
            name: "puck.world.json",
            text: "world"
        );
        var neighbor = directory.WriteText(
            name: "neighbor.world.json",
            text: "neighbor"
        );
        var worldHash = FullHash(bytes: File.ReadAllBytes(path: world));
        var neighborHash = FullHash(bytes: File.ReadAllBytes(path: neighbor));
        var asset = directory.WriteText(
            name: "asset.bin",
            text: "asset"
        );
        var assetHash = FullHash(bytes: File.ReadAllBytes(path: asset));
        var left = Manifest(
            new Dictionary<string, string> { ["puck"] = worldHash, ["neighbor"] = neighborHash },
            new Dictionary<string, string> { ["asset.bin"] = assetHash }
        );
        var right = left with { Definitions = new Dictionary<string, string> { ["neighbor"] = neighborHash, ["puck"] = worldHash }, DefinitionFiles = new Dictionary<string, string> { ["neighbor"] = "neighbor.world.json", ["puck"] = "puck.world.json" }, Artifacts = new Dictionary<string, string> { ["asset.bin"] = assetHash } };

        Assert.Equal(
            left.Identity,
            right.Identity
        );
        Assert.True(
            condition: WorldReleaseManifest.TryVerify(
                manifest: left,
                packageDirectory: directory.RootPath,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.False(condition: WorldReleaseManifest.TryVerify(
            manifest: left with { DefinitionFiles = new Dictionary<string, string> { ["puck"] = "puck.world.json" } },
            packageDirectory: directory.RootPath,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "exactly match"
        );
        Assert.False(condition: WorldReleaseManifest.TryVerify(
            manifest: left with { Definitions = null! },
            packageDirectory: directory.RootPath,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "inventories"
        );
        File.WriteAllText(
            contents: "changed",
            path: asset
        );
        Assert.False(condition: WorldReleaseManifest.TryVerify(
            manifest: left,
            packageDirectory: directory.RootPath,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "hashes"
        );
    }
    [Fact]
    public async Task PreCommitRecoveryRetainsSourceAndRequiresFreshAdmissionPublication() {
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            Guid.NewGuid()
        );
        var created = await groups.CreateAsync(
            "primary",
            "release-a",
            TestContext.Current.CancellationToken
        );
        var started = await groups.BeginAsync(
            created.Snapshot!.Value,
            Guid.NewGuid(),
            "release-b",
            TestContext.Current.CancellationToken
        );
        var drained = await groups.AdvanceAsync(
            started.Snapshot!.Value,
            started.Snapshot.Value.Record with {
            PendingPhase = WorldReleaseOperationPhase.Drain,
            Admission = WorldReleaseAdmissionState.Closed,
            RecoveryRoots = new Dictionary<string, string> { ["row"] = "checkpoint:1" },
            Revision = (started.Snapshot.Value.Record.Revision + 1),
        },
            TestContext.Current.CancellationToken
        );
        var recovering = await groups.BeginRecoveryAsync(
            drained.Snapshot!.Value,
            "candidate health failed",
            TestContext.Current.CancellationToken
        );
        var restored = await groups.RecordRecoveryRestoredAsync(
            recovering.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );
        var recovered = await groups.CompleteRecoveryAsync(
            restored.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: recovered.Ok,
            userMessage: recovered.Detail
        );
        Assert.Equal(
            "release-a",
            recovered.Snapshot!.Value.Record.ActiveRelease
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Closed,
            recovered.Snapshot.Value.Record.Admission
        );
        Assert.True(condition: (await groups.OpenRecoveredAdmissionAsync(
            recovered.Snapshot.Value,
            "release-a",
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        )).Ok);
    }
    [Fact]
    public void QualificationRequiresCompleteMatchingImportsInBothDirections() {
        var source = Manifest(
            new Dictionary<string, string> { ["row"] = FullHash(bytes: "world"u8.ToArray()) },
            new Dictionary<string, string>()
        );
        var target = source with { Label = "candidate", EngineImageDigest = ("sha256:" + new string(
            c: 'e',
            count: 64
        )) };
        var evidence = Evidence(
            source: source,
            target: target
        );

        Assert.True(
            condition: WorldReleaseCoordinator.TryQualifyPair(
                evidence: evidence,
                reason: out var reason,
                source: source,
                target: target
            ),
            userMessage: reason
        );
        Assert.False(condition: WorldReleaseCoordinator.TryQualifyPair(
            source,
            target,
            evidence with { TargetStateHash = FullHash(bytes: "lost inventory"u8.ToArray()) },
            out _
        ));
        Assert.False(condition: WorldReleaseCoordinator.TryQualifyPair(
            source,
            target,
            evidence with { ReverseReferenceStateHash = FullHash(bytes: "lost new state"u8.ToArray()) },
            out _
        ));
        Assert.False(condition: WorldReleaseCoordinator.TryQualifyPair(
            source,
            target,
            evidence with { SourceStateHash = "same", TargetStateHash = "same" },
            out _
        ));
        Assert.False(condition: WorldReleaseCoordinator.TryQualifyPair(
            source,
            target,
            evidence with { EvidenceId = "operator says it works" },
            out _
        ));
    }
    [Fact]
    public void StructuralCompatibilityDoesNotAuthorizeChangedDefinitionsOrArtifacts() {
        var source = Manifest(
            new Dictionary<string, string> { ["world"] = ("sha256/" + new string(
                c: 'a',
                count: 64
            )) },
            new Dictionary<string, string> { ["neighbor"] = ("sha256/" + new string(
                c: 'b',
                count: 64
            )) }
        );
        var same = source with { EngineImageDigest = ("sha256:" + new string(
            c: 'b',
            count: 64
        )) };

        Assert.True(
            condition: WorldReleaseTransitionPolicy.TryPrepare(
                changes: out var changes,
                reason: out var reason,
                source: source,
                target: same
            ),
            userMessage: reason
        );
        Assert.Empty(collection: changes);
        var changedDefinition = same with { Definitions = new Dictionary<string, string> { ["world"] = ("sha256/" + new string(
            c: 'e',
            count: 64
        )) } };

        Assert.False(condition: WorldReleaseTransitionPolicy.TryPrepare(
            changes: out changes,
            reason: out reason,
            source: source,
            target: changedDefinition
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "metadata coordinator contract"
        );
        var coordinated = changedDefinition with { CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract };

        Assert.True(
            condition: WorldReleaseTransitionPolicy.TryPrepare(
                changes: out changes,
                reason: out reason,
                source: source,
                target: coordinated
            ),
            userMessage: reason
        );
        Assert.Equal(
            new WorldReleaseDefinitionChange(
                "world",
                source.Definitions["world"],
                coordinated.Definitions["world"]
            ),
            Assert.Single(collection: changes)
        );
        Assert.False(condition: WorldReleaseCoordinator.TryQualifyPair(
            evidence: null,
            reason: out reason,
            source: source,
            target: coordinated
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "runner receipt"
        );
        Assert.True(
            condition: WorldReleaseTransitionPolicy.TryPrepare(
                changes: out var reverse,
                reason: out reason,
                source: coordinated,
                target: source
            ),
            userMessage: reason
        );
        Assert.Equal(
            WorldReleaseTransitionPolicy.Reverse(changes: changes),
            reverse
        );
        var changedArtifact = source with { Artifacts = new Dictionary<string, string> { ["neighbor"] = ("sha256/" + new string(
            c: 'c',
            count: 64
        )) } };

        Assert.False(condition: WorldReleaseCompatibility.TryCheckStructuralCompatibility(
            candidate: changedArtifact,
            previous: source,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "artifact"
        );
        Assert.False(condition: WorldReleaseCompatibility.TryCheckStructuralCompatibility(
            source,
            same with { PersistenceContract = "other" },
            out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "persistence"
        );
    }

    private sealed class FixedQualificationRunner(WorldReleaseQualificationReceipt receipt) : IWorldReleaseQualificationRunner {
        public Task<WorldReleaseQualificationReceipt?> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldReleaseQualificationReceipt?>(result: receipt with { SourceRelease = source.Identity, TargetRelease = target.Identity });
    }
}
