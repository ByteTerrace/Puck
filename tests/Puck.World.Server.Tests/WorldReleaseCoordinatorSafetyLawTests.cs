using System.Text.Json;
using Puck.Storage;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Failure-injection laws for coordinator ownership and uncertain commit responses.</summary>
public sealed partial class WorldReleaseCoordinatorSafetyLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ObjectStorageTarget Target = new DirectoryObjectStorageTarget(rootPath: "fake-store");

    private static WorldReleaseManifest Manifest(char image) => new() {
        CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
        Label = "coordinator-test",
        SourceRevision = new string(
        c: image,
        count: 40
    ),
        EngineImageDigest = ("sha256:" + new string(
        c: image,
        count: 64
    )),
        Definitions = new Dictionary<string, string> {
            ["row"] = ("sha256/" + new string(
        c: 'd',
        count: 64
    )),
        },
        DefinitionFiles = new Dictionary<string, string> { ["row"] = "row.world.json" },
        Artifacts = new Dictionary<string, string>(),
        PersistenceContract = "puck.world.persistence.v1",
        PeerProtocolContract = "puck.world.peer.v1",
    };
    private static WorldReleaseGroupSnapshot Required(WorldReleaseGroupOutcome result) {
        Assert.True(
            condition: result.Ok,
            userMessage: result.Detail
        );
        return result.Snapshot!.Value;
    }

    [Fact]
    public async Task DifferentOperationCannotResumeAnExistingRequestOrTouchItsWorker() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var source = Manifest(image: 'a');
        var target = Manifest(image: 'b');
        var group = Required(result: await groups.CreateAsync(
            "primary",
            source.Identity,
            Token
        ));

        group = Required(result: await groups.BeginAsync(
            group,
            Guid.NewGuid(),
            target.Identity,
            Token
        ));
        var runtime = new RecordingRuntime(owner: owner);

        var result = await new WorldReleaseCoordinator(groups: groups).RunAsync(
            group,
            source,
            target,
            null,
            Guid.NewGuid(),
            runtime,
            Token
        );

        Assert.False(condition: result.Completed);
        Assert.Equal(
            0,
            runtime.Calls
        );
        var unchanged = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        Assert.Equal(
            group.VersionToken,
            unchanged.VersionToken
        );
        Assert.Equal(
            group.Record.PendingOperationId,
            unchanged.Record.PendingOperationId
        );
    }
    [Fact]
    public async Task LostCommitResponseCannotTriggerRecoveryToTheSourceSave() {
        var owner = Guid.NewGuid();
        var store = new LostCommitResponseStore();
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: store,
            target: Target
        );
        var source = Manifest(image: 'a');
        var target = Manifest(image: 'b');
        var group = Required(result: await groups.CreateAsync(
            "primary",
            source.Identity,
            Token
        ));

        group = Required(result: await groups.BeginAsync(
            group,
            Guid.NewGuid(),
            target.Identity,
            Token
        ));
        group = Required(result: await groups.AdvanceAsync(
            group,
            group.Record with {
                PendingPhase = WorldReleaseOperationPhase.Drain,
                Admission = WorldReleaseAdmissionState.Closed,
                RecoveryRoots = new Dictionary<string, string> { [$"{owner:D}/row"] = "protected-root" },
                Revision = (group.Record.Revision + 1),
            },
            Token
        ));
        foreach (var phase in new[] { WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify }) {
            group = Required(result: await groups.AdvanceAsync(
                group,
                group.Record with { PendingPhase = phase, Revision = (group.Record.Revision + 1) },
                Token
            ));
        }
        var runtime = new RecordingRuntime(owner: owner);

        store.Armed = true;

        await new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            group,
            target,
            runtime,
            Token
        );

        Assert.True(condition: store.ResponseLost);
        Assert.Equal(
            0,
            runtime.RecoveryCalls
        );
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;

        Assert.True(condition: durable.PendingCommitted);
        Assert.Equal(
            WorldReleaseOperationPhase.Commit,
            durable.PendingPhase
        );
        Assert.Equal(
            target.Identity,
            durable.ActiveRelease
        );
    }

    // A first deployment whose candidate fails to start: recovery restores its protected roots, and the run's detail names
    // the start failure whether recovery completes or stays pending behind a failed candidate stop.
    [Theory]
    [InlineData(null)]
    [InlineData("the worker refused to stop")]
    public async Task EveryPreCommitRecoveryOutcomeNamesItsCause(string? stopFailure) {
        const string Cause = "Deployment failed: The value of parameter spotRestorePolicy is invalid.";
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var target = Manifest(image: 'b');
        var group = Required(result: await groups.CreateAsync(
            "primary",
            null,
            Token
        ));

        group = Required(result: await groups.BeginAsync(
            group,
            Guid.NewGuid(),
            target.Identity,
            Token
        ));
        group = Required(result: await groups.AdvanceAsync(
            group,
            group.Record with {
                PendingPhase = WorldReleaseOperationPhase.Drain,
                Admission = WorldReleaseAdmissionState.Closed,
                RecoveryRoots = new Dictionary<string, string> { [$"{owner:D}/row"] = "protected-root" },
                Revision = (group.Record.Revision + 1),
            },
            Token
        ));
        group = Required(result: await groups.AdvanceAsync(
            group,
            group.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = (group.Record.Revision + 1) },
            Token
        ));
        var runtime = new RecordingRuntime(owner: owner) { StartFailure = Cause, StopFailure = stopFailure };

        var result = await new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            group,
            target,
            runtime,
            Token
        );

        Assert.False(condition: result.Completed);
        Assert.Equal(
            (stopFailure is null),
            result.SourceRecovered
        );
        Assert.Contains(
            actualString: result.Detail,
            expectedSubstring: Cause
        );
        if (stopFailure is not null) {
            Assert.Contains(
                actualString: result.Detail,
                expectedSubstring: stopFailure
            );
        }
    }

    // A first deployment begun and advanced to Drain, with protected roots when rooted, and to Activate after them.
    private static async Task<WorldReleaseGroupSnapshot> PendingBootstrap(WorldReleaseGroupStore groups, WorldReleaseGroupSnapshot group,
        Guid owner, WorldReleaseManifest target, bool rooted) {
        group = Required(result: await groups.BeginAsync(
            group,
            Guid.NewGuid(),
            target.Identity,
            Token
        ));
        group = Required(result: await groups.AdvanceAsync(
            group,
            group.Record with {
                PendingPhase = WorldReleaseOperationPhase.Drain,
                RecoveryRoots = (rooted
                    ? new Dictionary<string, string> { [$"{owner:D}/row"] = "protected-root" }
                    : new Dictionary<string, string>()),
                Revision = (group.Record.Revision + 1),
            },
            Token
        ));
        return (rooted
            ? Required(result: await groups.AdvanceAsync(
                group,
                group.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = (group.Record.Revision + 1) },
                Token
            ))
            : group);
    }

    // A worker a bootstrap finds is replaced only when the group's history shows an earlier bootstrap that reached its
    // candidate and recovered, in a group that never committed. A fresh group, one whose only earlier bootstrap was
    // abandoned before it captured anything, and a group serving a release all refuse it.
    [Fact]
    public async Task ABootstrapReplacesOnlyTheWorkerAnEarlierRecoveredBootstrapOfItsGroupLeft() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var coordinator = new WorldReleaseCoordinator(groups: groups);
        var first = Manifest(image: 'b');
        var second = Manifest(image: 'c');
        var group = Required(result: await groups.CreateAsync(
            "primary",
            null,
            Token
        ));

        Assert.False(condition: group.Record.HasRecoveredBootstrap);
        var abandoned = await coordinator.SupersedeAsync(
            await PendingBootstrap(
                group: group,
                groups: groups,
                owner: owner,
                rooted: false,
                target: first
            ),
            second.Identity,
            first,
            new Dictionary<string, string>(),
            new RecordingRuntime(owner: owner),
            Token
        );

        group = abandoned.Snapshot!.Value;
        Assert.False(condition: group.Record.HasRecoveredBootstrap);
        var recovered = await coordinator.ResumeAsync(
            await PendingBootstrap(
                group: group,
                groups: groups,
                owner: owner,
                rooted: true,
                target: first
            ),
            first,
            new RecordingRuntime(owner: owner) { StartFailure = "the silo exited on start" },
            Token
        );

        Assert.True(condition: recovered.SourceRecovered);
        group = recovered.Snapshot!.Value;
        Assert.True(condition: group.Record.HasRecoveredBootstrap);
        Assert.False(condition: (group.Record with { ActiveRelease = first.Identity }).HasRecoveredBootstrap);
        var serving = Required(result: await groups.CreateAsync(
            "serving",
            first.Identity,
            Token
        ));

        Assert.False(condition: serving.Record.HasRecoveredBootstrap);
    }

    // A newer release ends a first deployment that never committed: one with no protected root is abandoned without a
    // runtime effect, keeping the roots it saved unrecorded; one with roots is recovered as a failed start would be. A
    // bootstrap of the successor itself is never superseded.
    [Fact]
    public async Task ANewerReleaseSupersedesAnUnfinishedBootstrapOfAnotherRelease() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var coordinator = new WorldReleaseCoordinator(groups: groups);
        var first = Manifest(image: 'b');
        var second = Manifest(image: 'c');
        var group = Required(result: await groups.CreateAsync(
            "primary",
            null,
            Token
        ));
        var unrooted = await PendingBootstrap(
            group: group,
            groups: groups,
            owner: owner,
            rooted: false,
            target: first
        );
        var quiet = new RecordingRuntime(owner: owner);

        Assert.False(condition: (await coordinator.SupersedeAsync(
            unrooted,
            first.Identity,
            first,
            new Dictionary<string, string>(),
            quiet,
            Token
        )).Snapshot.HasValue);
        var saved = new Dictionary<string, string> { [$"{owner:D}/row"] = "saved-unrecorded" };
        var abandoned = await coordinator.SupersedeAsync(
            unrooted,
            second.Identity,
            first,
            saved,
            quiet,
            Token
        );

        Assert.Equal(
            0,
            quiet.Calls
        );
        var entry = abandoned.Snapshot!.Value.Record.History[^1];

        Assert.Null(@object: abandoned.Snapshot!.Value.Record.PendingOperationId);
        Assert.Equal(
            unrooted.Record.PendingOperationId,
            entry.OperationId
        );
        Assert.StartsWith(
            actualString: entry.Result,
            expectedStartString: WorldReleaseGroupHistoryEntry.AbandonedResult
        );
        Assert.Equal(
            saved,
            entry.RecoveryRoots
        );
        var rooted = await PendingBootstrap(
            group: abandoned.Snapshot!.Value,
            groups: groups,
            owner: owner,
            rooted: true,
            target: first
        );
        var recovering = new RecordingRuntime(owner: owner);
        var recovered = await coordinator.SupersedeAsync(
            rooted,
            second.Identity,
            first,
            new Dictionary<string, string>(),
            recovering,
            Token
        );

        Assert.True(condition: recovered.SourceRecovered);
        Assert.Equal(
            1,
            recovering.RecoveryCalls
        );
        Assert.Null(@object: recovered.Snapshot!.Value.Record.PendingOperationId);
        Assert.True(condition: recovered.Snapshot!.Value.Record.History[^1].IsRecoveredBootstrap);
    }

    private sealed class RecordingRuntime(Guid owner) : IWorldReleaseRuntime {
        public int Calls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public string? StartFailure { get; init; }
        public string? StopFailure { get; init; }

        private Task<WorldReleaseRuntimeResult> Success() {
            Calls++;
            return Task.FromResult(result: new WorldReleaseRuntimeResult(
                true,
                string.Empty
            ));
        }

        public Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(result: new WorldReleaseRuntimeResult(
                false,
                "unexpected drain"
            ));
        }
        public Task<WorldReleaseRuntimePublicationResult> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(result: new WorldReleaseRuntimePublicationResult(
                WorldReleaseRuntimePublication.Opened,
                string.Empty
            ));
        }
        public Task<WorldReleaseRuntimePublicationResult> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => PublishCandidateAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            IReadOnlyList<(WorldAuthorityIdentity, WorldAuthorityFence)> fences = [(new(
                    Owner: owner,
                    World: SafeName.Parse(candidate: "row")
                ), new(
                    1,
                    Guid.NewGuid(),
                    "root"
                ))];

            return Task.FromResult(result: fences);
        }
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            RecoveryCalls++;
            return Success();
        }
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (StartFailure is { } failure) {
                Calls++;
                return Task.FromResult(result: new WorldReleaseRuntimeResult(
                    false,
                    failure
                ));
            }
            return Success();
        }
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (StopFailure is { } failure) {
                Calls++;
                return Task.FromResult(result: new WorldReleaseRuntimeResult(
                    false,
                    failure
                ));
            }
            return Success();
        }
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
    }
    private sealed class LostCommitResponseStore : IObjectBlobStore {
        public ValueTask<bool> DeleteAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) =>
            m_inner.DeleteAsync(target, address, cancellationToken);

        private readonly FakeObjectBlobStore m_inner = new();

        public bool Armed { get; set; }
        public bool ResponseLost { get; private set; }

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            m_inner.ListAsync(
                cancellationToken: cancellationToken,
                keyPrefix: keyPrefix,
                objectId: objectId,
                target: target
            );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) =>
            m_inner.ReadAsync(
                address: address,
                cancellationToken: cancellationToken,
                target: target
            );
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            var result = await m_inner.WriteAsync(
                address: address,
                cancellationToken: cancellationToken,
                content: content,
                ifMatchVersion: ifMatchVersion,
                mode: mode,
                target: target
            );

            if (
                Armed &&
                !ResponseLost &&
                result.Succeeded
            ) {
                using var json = JsonDocument.Parse(content);

                if (
                    json.RootElement.TryGetProperty(
                    propertyName: "pendingCommitted",
                    value: out var committed
                ) &&
                    committed.GetBoolean()
                ) {
                    ResponseLost = true;
                    throw new IOException(message: "The commit landed, but its response was lost.");
                }
            }
            return result;
        }
    }
}
