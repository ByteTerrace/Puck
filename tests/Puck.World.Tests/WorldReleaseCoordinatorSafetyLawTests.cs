using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Failure-injection laws for coordinator ownership and uncertain commit responses.</summary>
public sealed class WorldReleaseCoordinatorSafetyLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

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

    private sealed class RecordingRuntime(Guid owner) : IWorldReleaseRuntime {
        public int Calls { get; private set; }
        public int RecoveryCalls { get; private set; }

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
        public Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(result: WorldReleaseRuntimePublication.Opened);
        }
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => PublishCandidateAsync(
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
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
    }
    private sealed class LostCommitResponseStore : IObjectBlobStore {
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
