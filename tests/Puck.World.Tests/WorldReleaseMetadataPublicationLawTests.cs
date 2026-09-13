using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseMetadataPublicationLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationMovesDefinitionAndCheckpointTogetherAndRetryPreservesCandidateProgress(bool deferredDraw) {
        using var scenario = await Scenario.CreateAsync(deferredDraw: deferredDraw);
        var outcome = await scenario.ApplyAsync();
        Assert.True(outcome.Ok, outcome.Detail);
        var published = (await scenario.Store.LoadRecoveryAsync(scenario.Identity, Token))!.Value;
        Assert.Equal("B", published.Definition!.Metadata!.Title);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(published.Checkpoint!.Value.Encoded.Span, out var checkpoint, out var reason), reason);
        var definition = WorldDefinitionSerialization.Deserialize(checkpoint!.Server.DefinitionJson);
        Assert.Equal("B", definition.Metadata!.Title);
        Assert.Equal("live note", definition.Metadata.Description);
        Assert.Equal(scenario.Protected.Root.CheckpointTick, checkpoint.Server.LastCompletedTick);
        Assert.Equal(Guid.Empty, published.Root.Root.FenceToken);
        Assert.True(published.Root.Root.Epoch > scenario.Protected.Root.Epoch);
        Assert.Equal(scenario.PreviousReceipt, await scenario.Store.FindOperationReceiptAsync(scenario.Identity, scenario.PreviousReceipt.OperationId, Token));
        Assert.Equal(scenario.Protected, await scenario.Store.LoadRecoveryRootAsync(scenario.Identity, scenario.Protected.Pin, scenario.Operation.PendingOperationId!.Value, Token));

        var fence = await scenario.Store.AcquireActivationAsync(scenario.Identity, Token);
        using var candidate = Fixtures.FreshServer(definition);
        candidate.Server.RestoreCheckpoint(checkpoint);
        candidate.Step();
        candidate.Step();
        Assert.True(candidate.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var latest, out reason), reason);
        Assert.True((await scenario.Store.WriteCheckpointAsync(scenario.Identity, WorldAuthorityCheckpointCodec.Encode(latest!), latest!.Server.LastCompletedTick, Token, fence)).Ok);
        var beforeRetry = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        var writes = scenario.Blobs.Writes;
        var restarted = new WorldAuthorityBlobStore(scenario.Blobs, scenario.Target);
        outcome = await scenario.ApplyAsync(restarted);
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(writes, scenario.Blobs.Writes);
        Assert.Equal(beforeRetry, await restarted.LoadRootAsync(scenario.Identity, Token));
        Assert.True(beforeRetry!.Value.Root.CheckpointTick > scenario.Protected.Root.CheckpointTick);
    }

    [Theory]
    [InlineData(0)] // Interrupted immutable checkpoint upload.
    [InlineData(1)] // Root write was never accepted.
    [InlineData(2)] // Root write succeeded but its response was lost.
    public async Task InterruptionLeavesOneCoherentRootAndRetryCompletesWithoutRecapturing(int failure) {
        using var scenario = await Scenario.CreateAsync();
        var before = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        var armed = true;
        scenario.Blobs.BeforeWrite = address => {
            if (armed && (failure == 0 ? address.Key.EndsWith(".pckp", StringComparison.Ordinal) : failure == 1 && IsRoot(address))) {
                armed = false;
                throw new IOException("injected interrupted publication");
            }
            return Task.CompletedTask;
        };
        scenario.Blobs.AfterWrite = address => {
            if (armed && failure == 2 && IsRoot(address)) { armed = false; throw new IOException("injected lost root response"); }
            return Task.CompletedTask;
        };
        if (failure == 0) { await Assert.ThrowsAsync<IOException>(() => scenario.ApplyAsync()); }
        else { Assert.Equal(failure == 2, (await scenario.ApplyAsync()).Ok); }
        Assert.False(armed);
        if (failure != 2) { Assert.Equal(before, await scenario.Store.LoadRootAsync(scenario.Identity, Token)); }
        var outcome = await scenario.ApplyAsync(new WorldAuthorityBlobStore(scenario.Blobs, scenario.Target));
        Assert.True(outcome.Ok, outcome.Detail);
        var recovery = (await scenario.Store.LoadRecoveryAsync(scenario.Identity, Token))!.Value;
        Assert.Equal("B", recovery.Definition!.Metadata!.Title);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(recovery.Checkpoint!.Value.Encoded.Span, out var checkpoint, out var reason), reason);
        Assert.Equal("B", WorldDefinitionSerialization.Deserialize(checkpoint!.Server.DefinitionJson).Metadata!.Title);
        Assert.Equal(before!.Value.Root.CheckpointOrdinal + 1, recovery.Root.Root.CheckpointOrdinal);
        Assert.Equal(scenario.Protected, await scenario.Store.FindRecoveryRootAsync(scenario.Identity, scenario.Operation.PendingOperationId!.Value, Token));
    }

    [Fact]
    public async Task NewAuthorityBetweenPreparationAndPublicationWinsTheRootRace() {
        using var scenario = await Scenario.CreateAsync();
        WorldAuthorityFence? winner = null;
        scenario.Blobs.BeforeWrite = async address => {
            if (!IsRoot(address)) { return; }
            scenario.Blobs.BeforeWrite = null;
            winner = await new WorldAuthorityBlobStore(scenario.Inner, scenario.Target).AcquireActivationAsync(scenario.Identity, Token);
        };
        Assert.False((await scenario.ApplyAsync()).Ok);
        Assert.NotNull(winner);
        var root = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        Assert.Equal(winner.Value.Token, root!.Value.Root.FenceToken);
        Assert.Equal(scenario.Protected.Root.DefinitionHash, root.Value.Root.DefinitionHash);
        Assert.Equal(scenario.Protected.Root.CheckpointHash, root.Value.Root.CheckpointHash);
        var writes = scenario.Blobs.Writes;
        Assert.False((await scenario.ApplyAsync()).Ok);
        Assert.Equal(writes, scenario.Blobs.Writes);
        Assert.Equal(root, await scenario.Store.LoadRootAsync(scenario.Identity, Token));
    }

    [Fact]
    public async Task LeavingActivationDuringUploadPreventsPublication() {
        using var scenario = await Scenario.CreateAsync();
        var before = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        scenario.Blobs.AfterWrite = async address => {
            if (!address.Key.EndsWith(".pckp", StringComparison.Ordinal)) { return; }
            scenario.Blobs.AfterWrite = null;
            var state = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
            Assert.True((await scenario.Groups.BeginRecoveryAsync(state, "operator recovery", Token)).Ok);
        };
        var outcome = await scenario.ApplyAsync();
        Assert.False(outcome.Ok);
        Assert.Contains("lost private activation", outcome.Detail);
        Assert.Equal(before, await scenario.Store.LoadRootAsync(scenario.Identity, Token));
        Assert.Equal(WorldReleaseOperationPhase.Recover, (await scenario.Groups.LoadAsync("primary", Token))!.Value.Record.PendingPhase);
    }

    [Fact]
    public async Task ConflictingCurrentMetadataRefusesBeforeUploadingCandidateObjects() {
        using var scenario = await Scenario.CreateAsync(conflict: true);
        var before = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        var writes = scenario.Blobs.Writes;
        var outcome = await scenario.ApplyAsync();
        Assert.False(outcome.Ok);
        Assert.Contains("metadata/title", outcome.Detail);
        Assert.Equal(writes, scenario.Blobs.Writes);
        Assert.Equal(before, await scenario.Store.LoadRootAsync(scenario.Identity, Token));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnedOrUncheckpointedSourcesCannotEnterPublication(bool owned, bool tail) {
        using var scenario = await Scenario.CreateAsync(owned: owned, tail: tail);
        var before = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        var writes = scenario.Blobs.Writes;
        var outcome = await scenario.ApplyAsync();
        Assert.False(outcome.Ok);
        Assert.Contains("fully checkpointed, unowned", outcome.Detail);
        Assert.Equal(writes, scenario.Blobs.Writes);
        Assert.Equal(before, await scenario.Store.LoadRootAsync(scenario.Identity, Token));
    }

    [Fact]
    public async Task MatchingReceiptDoesNotHideCorruptPublishedCheckpoint() {
        using var scenario = await Scenario.CreateAsync();
        Assert.True((await scenario.ApplyAsync()).Ok);
        var root = (await scenario.Store.LoadRootAsync(scenario.Identity, Token))!.Value;
        var prefix = $"private/puck/hosted/{scenario.Identity.World}/authority/checkpoints";
        var paths = await scenario.Blobs.ListAsync(scenario.Target, scenario.Identity.Owner, prefix, Token);
        var path = Assert.Single(paths, value => value.StartsWith($"{prefix}/{root.Root.CheckpointOrdinal:D12}-", StringComparison.Ordinal));
        Assert.True((await scenario.Blobs.WriteAsync(scenario.Target, new(scenario.Identity.Owner, path), "corrupt"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: Token)).Succeeded);
        var writes = scenario.Blobs.Writes;
        await Assert.ThrowsAsync<InvalidDataException>(() => scenario.ApplyAsync());
        Assert.Equal(writes, scenario.Blobs.Writes);
        Assert.Equal(root, await scenario.Store.LoadRootAsync(scenario.Identity, Token));
    }

    [Fact]
    public async Task MetadataPairWithoutCoordinatorRequirementCannotPublish() {
        using var scenario = await Scenario.CreateAsync(coordinatorContract: false);
        var before = await scenario.Store.LoadRootAsync(scenario.Identity, Token);
        var writes = scenario.Blobs.Writes;
        var outcome = await scenario.ApplyAsync();
        Assert.False(outcome.Ok);
        Assert.Contains("metadata coordinator contract", outcome.Detail);
        Assert.Equal(writes, scenario.Blobs.Writes);
        Assert.Equal(before, await scenario.Store.LoadRootAsync(scenario.Identity, Token));
    }

    private static bool IsRoot(ObjectBlobAddress address) => address.Key.EndsWith("/authority/root", StringComparison.Ordinal);
    private sealed class Scenario : IDisposable {
        private readonly TempWorldDirectory m_directory = new();
        public IObjectBlobStore Inner { get; } = PuckStorageTestComposition.BuildStore();
        public InterceptStore Blobs { get; }
        public DirectoryObjectStorageTarget Target { get; }
        public WorldAuthorityIdentity Identity { get; } = new(Guid.NewGuid(), SafeName.Parse("amber"));
        public WorldAuthorityBlobStore Store { get; }
        public WorldReleaseArchive Archive { get; }
        public WorldReleaseGroupStore Groups { get; }
        public WorldReleaseManifest Source { get; private set; } = null!;
        public WorldReleaseManifest Candidate { get; private set; } = null!;
        public WorldReleaseGroupRecord Operation { get; private set; } = null!;
        public WorldRecoveryRootReference Protected { get; private set; }
        public WorldAuthorityOperationReceipt PreviousReceipt { get; } = new(Guid.NewGuid(), "player", "payload", "refused", false, 0, null);
        private Scenario() {
            Target = new(Path.Combine(m_directory.RootPath, "store"));
            Blobs = new(Inner);
            Store = new(Blobs, Target);
            Archive = new(Blobs, Target, Identity.Owner);
            Groups = new(Blobs, Target, Identity.Owner);
        }
        public static async Task<Scenario> CreateAsync(bool conflict = false, bool owned = false, bool tail = false, bool coordinatorContract = true, bool deferredDraw = false) {
            var scenario = new Scenario();
            var a = Fixtures.BuildDocument() with { Metadata = new(Title: "A", Description: "authored") };
            if (deferredDraw) {
                var tree = JsonNode.Parse(WorldDefinitionSerialization.Serialize(a))!;
                tree["state"] ??= new JsonObject();
                tree["state"]!["world"] ??= new JsonArray();
                tree["state"]!["world"]!.AsArray().Add(JsonNode.Parse("""
                    {"name":"cadence","kind":"Fixed","cells":[],"draw":{"generator":{"source":"UniformRange","rangeMin":65536,"rangeMax":131072},"timing":"Boot"}}
                    """));
                tree["prototypes"] ??= new JsonArray();
                tree["prototypes"]!.AsArray().Add(JsonNode.Parse("""
                    {"id":"draw-actor","document":{"schema":"puck.creation.v1","name":"actor","shapes":[],"drivers":[{"name":"stride","signal":"planarTravel","cadence":"state.cadence"}]}}
                    """));
                Assert.True(WorldDefinitionFileSource.TryParseComposed(tree.ToJsonString(), "published draw", null, false, out var parsed, out var drawReason), drawReason);
                a = parsed!;
                Assert.Throws<InvalidDataException>(() => WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(a)));
            }
            scenario.Source = await scenario.PackageAsync(a, "A");
            scenario.Candidate = await scenario.PackageAsync(a with { Metadata = a.Metadata! with { Title = "B" } }, "B", coordinatorContract);
            Assert.True(WorldDrawBootResolver.TryResolve(a, scenario.Identity.World.Value, out var live, out var bootReason), bootReason);
            using var fixture = Fixtures.FreshServer(live with { Metadata = new(Title: conflict ? "operator" : "A", Description: "live note") });
            fixture.Step();
            fixture.Step();
            Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), reason);
            var fence = await scenario.Store.AcquireActivationAsync(scenario.Identity, Token);
            Assert.True((await scenario.Store.PublishDefinitionAsync(scenario.Identity, a, Token, fence)).Ok);
            Assert.True((await scenario.Store.WriteCheckpointAsync(scenario.Identity, WorldAuthorityCheckpointCodec.Encode(checkpoint!), checkpoint!.Server.LastCompletedTick, Token, fence)).Ok);
            Assert.True((await scenario.Store.RecordReceiptAsync(scenario.Identity, scenario.PreviousReceipt, Token, fence)).Ok);
            if (tail) { Assert.True((await scenario.Store.AppendJournalAsync(scenario.Identity, new WorldMutationJournalEntry(checkpoint.Server.LastCompletedTick + 1, "uncheckpointed tail"u8.ToArray()), Token, fence)).Ok); }
            if (!owned) { Assert.True((await scenario.Store.ReleaseActivationAsync(scenario.Identity, fence!.Value, Token)).Ok); }
            var created = await scenario.Groups.CreateAsync("primary", scenario.Source.Identity, Token);
            var operationId = Guid.NewGuid();
            var begun = await scenario.Groups.BeginAsync(created.Snapshot!.Value, operationId, scenario.Candidate.Identity, Token);
            scenario.Protected = (await scenario.Store.CaptureRecoveryRootAsync(scenario.Identity, operationId, Token))!.Value;
            var coordinator = new WorldReleaseCoordinator(scenario.Groups);
            var drained = await coordinator.RecordDrainAsync(begun.Snapshot!.Value, new Dictionary<string, string> {
                [$"{scenario.Identity.Owner:D}/{scenario.Identity.World}"] = scenario.Protected.Pin }, Token);
            var activation = await coordinator.RecordActivationAsync(drained.Snapshot!.Value, Token);
            Assert.True(activation.Ok, activation.Detail);
            scenario.Operation = activation.Snapshot!.Value.Record;
            return scenario;
        }
        private async Task<WorldReleaseManifest> PackageAsync(WorldDefinition definition, string label, bool coordinator = true) {
            var package = Directory.CreateDirectory(Path.Combine(m_directory.RootPath, label));
            var bytes = WorldDefinitionSerialization.Serialize(definition);
            File.WriteAllBytes(Path.Combine(package.FullName, "world.json"), bytes);
            var key = $"{Identity.Owner:D}/{Identity.World}";
            var manifest = new WorldReleaseManifest {
                Label = label, SourceRevision = new string('a', 40), EngineImageDigest = "sha256:" + new string('b', 64),
                PersistenceContract = "test", PeerProtocolContract = "test",
                CoordinatorContract = label == "B" && coordinator ? WorldReleaseManifest.MetadataCoordinatorContract : null,
                Definitions = new Dictionary<string, string> { [key] = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                DefinitionFiles = new Dictionary<string, string> { [key] = "world.json" },
            };
            await Archive.SaveAsync(manifest, package.FullName, Token);
            return manifest;
        }
        public Task<WorldAuthorityStoreOutcome> ApplyAsync(WorldAuthorityBlobStore? store = null) =>
            (store ?? Store).PrepareReleaseMetadataAsync(Identity, Operation, Source, Candidate, Archive, Token);
        public void Dispose() => m_directory.Dispose();
    }

    private sealed class InterceptStore(IObjectBlobStore inner) : IObjectBlobStore {
        public int Writes { get; private set; }
        public Func<ObjectBlobAddress, Task>? BeforeWrite { get; set; }
        public Func<ObjectBlobAddress, Task>? AfterWrite { get; set; }
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            Writes++;
            if (BeforeWrite is { } before) { await before(address); }
            var result = await inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
            if (AfterWrite is { } after) { await after(address); }
            return result;
        }
    }
}
