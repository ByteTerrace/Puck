using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Failure-injection laws for coordinator ownership and uncertain commit responses.</summary>
public sealed class WorldReleaseCoordinatorSafetyLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri("UseDevelopmentStorage=true");

    [Fact]
    public async Task DifferentOperationCannotResumeAnExistingRequestOrTouchItsWorker() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(new FakeObjectBlobStore(), Target, owner);
        var source = Manifest('a');
        var target = Manifest('b');
        var group = Required(await groups.CreateAsync("primary", source.Identity, Token));
        group = Required(await groups.BeginAsync(group, Guid.NewGuid(), target.Identity, Token));
        var runtime = new RecordingRuntime(owner);

        var result = await new WorldReleaseCoordinator(groups).RunAsync(group, source, target, null, Guid.NewGuid(), runtime, Token);

        Assert.False(result.Completed);
        Assert.Equal(0, runtime.Calls);
        var unchanged = (await groups.LoadAsync("primary", Token))!.Value;
        Assert.Equal(group.VersionToken, unchanged.VersionToken);
        Assert.Equal(group.Record.PendingOperationId, unchanged.Record.PendingOperationId);
    }

    [Fact]
    public async Task LostCommitResponseCannotTriggerRecoveryToTheSourceSave() {
        var owner = Guid.NewGuid();
        var store = new LostCommitResponseStore();
        var groups = new WorldReleaseGroupStore(store, Target, owner);
        var source = Manifest('a');
        var target = Manifest('b');
        var group = Required(await groups.CreateAsync("primary", source.Identity, Token));
        group = Required(await groups.BeginAsync(group, Guid.NewGuid(), target.Identity, Token));
        group = Required(await groups.AdvanceAsync(group, group.Record with {
            PendingPhase = WorldReleaseOperationPhase.Drain,
            Admission = WorldReleaseAdmissionState.Closed,
            RecoveryRoots = new Dictionary<string, string> { [$"{owner:D}/row"] = "protected-root" },
            Revision = group.Record.Revision + 1
        }, Token));
        foreach (var phase in new[] { WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify }) {
            group = Required(await groups.AdvanceAsync(group, group.Record with { PendingPhase = phase, Revision = group.Record.Revision + 1 }, Token));
        }
        var runtime = new RecordingRuntime(owner);
        store.Armed = true;

        await new WorldReleaseCoordinator(groups).ResumeAsync(group, target, runtime, Token);

        Assert.True(store.ResponseLost);
        Assert.Equal(0, runtime.RecoveryCalls);
        var durable = (await groups.LoadAsync("primary", Token))!.Value.Record;
        Assert.True(durable.PendingCommitted);
        Assert.Equal(WorldReleaseOperationPhase.Commit, durable.PendingPhase);
        Assert.Equal(target.Identity, durable.ActiveRelease);
    }

    private static WorldReleaseManifest Manifest(char image) => new() {
        Label = "coordinator-test",
        SourceRevision = new string(image, 40),
        EngineImageDigest = "sha256:" + new string(image, 64),
        Definitions = new Dictionary<string, string> { ["row"] = "sha256/" + new string('d', 64) },
        DefinitionFiles = new Dictionary<string, string> { ["row"] = "row.world.json" },
        Artifacts = new Dictionary<string, string>(),
        PersistenceContract = "puck.world.persistence.v1",
        PeerProtocolContract = "puck.world.peer.v1"
    };

    private static WorldReleaseGroupSnapshot Required(WorldReleaseGroupOutcome result) {
        Assert.True(result.Ok, result.Detail);
        return result.Snapshot!.Value;
    }

    private sealed class RecordingRuntime(Guid owner) : IWorldReleaseRuntime {
        public int Calls { get; private set; }
        public int RecoveryCalls { get; private set; }

        public Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(new WorldReleaseRuntimeResult(false, "unexpected drain"));
        }
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            IReadOnlyList<(WorldAuthorityIdentity, WorldAuthorityFence)> fences = [(new(owner, SafeName.Parse("row")), new(1, Guid.NewGuid(), "root"))];
            return Task.FromResult(fences);
        }
        public Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(WorldReleaseRuntimePublication.Opened);
        }
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            RecoveryCalls++;
            return Success();
        }
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Success();
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => PublishCandidateAsync(operation, cancellationToken);
        private Task<WorldReleaseRuntimeResult> Success() {
            Calls++;
            return Task.FromResult(new WorldReleaseRuntimeResult(true, string.Empty));
        }
    }

    private sealed class LostCommitResponseStore : IObjectBlobStore {
        private readonly FakeObjectBlobStore m_inner = new();
        public bool Armed { get; set; }
        public bool ResponseLost { get; private set; }

        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) =>
            m_inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            m_inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            var result = await m_inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
            if (Armed && !ResponseLost && result.Succeeded) {
                using var json = JsonDocument.Parse(content);
                if (json.RootElement.TryGetProperty("pendingCommitted", out var committed) && committed.GetBoolean()) {
                    ResponseLost = true;
                    throw new IOException("The commit landed, but its response was lost.");
                }
            }
            return result;
        }
    }
}
