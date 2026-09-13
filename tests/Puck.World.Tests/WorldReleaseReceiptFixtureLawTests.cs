using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseReceiptFixtureLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExplicitEmptyHistoryCanCreateAFixtureButLegacyStateCannotBeReplaced() {
        using var empty = await Scenario.CreateAsync(receipts: false);
        Assert.Empty(empty.History.Validate());
        Assert.True((await empty.CreateFixtureAsync()).Ok);
        var root = (await empty.Fixture.LoadRootAsync(empty.Identity, Token))!.Value.Root;
        Assert.Null(root.ReceiptHash);
        Assert.Null(root.ReceiptIndexHash);
        using var legacy = await Scenario.CreateAsync();
        var address = WorldOwnedWorldSync.HostedAddressFor(legacy.Identity.Owner, legacy.Identity.World, "definition.json");
        await legacy.Blobs.WriteAsync(legacy.Target, address, legacy.Definition, ObjectBlobWriteMode.CreateOnly, cancellationToken: Token);
        Assert.False((await legacy.CreateFixtureAsync()).Ok);
        Assert.Null(await legacy.Fixture.LoadRootAsync(legacy.Identity, Token));
        Assert.Equal(legacy.Definition, (await legacy.Blobs.ReadAsync(legacy.Target, address, Token))!.Value.Content.ToArray());
    }

    [Fact]
    public async Task FixturePreservesRealCheckpointReceiptLookupAndDuplicateDecisionsAcrossContinuation() {
        using var scenario = await Scenario.CreateAsync();
        var result = await scenario.CreateFixtureAsync();
        Assert.True(result.Ok, result.Detail);
        var loaded = (await scenario.Fixture.LoadRecoveryAsync(scenario.Identity, Token))!.Value;
        Assert.Equal(scenario.Checkpoint, loaded.Checkpoint!.Value.Encoded.ToArray());
        Assert.Empty(loaded.Journal.Entries);
        Assert.Equal(scenario.History.Source.Root.JournalSequence, loaded.Root.Root.JournalSequence);
        Assert.Equal(loaded.Root.Root.JournalSequence, loaded.Root.Root.CheckpointCoverageSequence);
        Assert.Equal(scenario.History.Source.Root.CheckpointOrdinal + 1, loaded.Root.Root.CheckpointOrdinal);
        Assert.True(loaded.Root.Root.Epoch > scenario.History.Source.Root.Epoch);
        Assert.True(loaded.Root.Root.Sequence > scenario.History.Source.Root.Sequence);
        Assert.Equal(Guid.Empty, loaded.Root.Root.FenceToken);
        Assert.Equal(scenario.History.Source.Root.ReceiptIndexHash, loaded.Root.Root.ReceiptIndexHash);
        Assert.Equal(scenario.History.Source.Root.ReceiptHash, loaded.Root.Root.ReceiptHash);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(scenario.Checkpoint, out var saved, out var reason), reason);
        using var continued = Fixtures.FreshServer(WorldDefinitionSerialization.Deserialize(saved!.Server.DefinitionJson));
        continued.Server.RestoreCheckpoint(saved);
        continued.Step();
        Assert.True(continued.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var latest, out reason), reason);
        var fence = await scenario.Fixture.AcquireActivationAsync(scenario.Identity, Token);
        Assert.True((await scenario.Fixture.WriteCheckpointAsync(scenario.Identity, WorldAuthorityCheckpointCodec.Encode(latest!), latest!.Server.LastCompletedTick, Token, fence)).Ok);
        var restarted = new WorldAuthorityBlobStore(scenario.Blobs, scenario.Target);
        Assert.Equal(scenario.Applied, await restarted.FindOperationReceiptAsync(scenario.Identity, scenario.Applied.OperationId, Token));
        Assert.Equal(scenario.Refused, await restarted.FindOperationReceiptAsync(scenario.Identity, scenario.Refused.OperationId, Token));
        var beforeRetry = await restarted.LoadRootAsync(scenario.Identity, Token);
        Assert.True((await restarted.AppendJournalAsync(scenario.Identity, scenario.Entry, Token, fence, scenario.Applied)).Ok);
        Assert.Equal(beforeRetry, await restarted.LoadRootAsync(scenario.Identity, Token));
        var conflict = await restarted.AppendJournalAsync(scenario.Identity, scenario.Entry, Token, fence, scenario.Applied with { PayloadDigest = "different" });
        Assert.Equal(WorldAuthorityStoreOutcomeKind.OperationConflict, conflict.Kind);
        Assert.Equal(beforeRetry, await restarted.LoadRootAsync(scenario.Identity, Token));
        Assert.False((await scenario.CreateFixtureAsync()).Ok);
        Assert.Equal(beforeRetry, await restarted.LoadRootAsync(scenario.Identity, Token));
        Assert.Equal(scenario.History.Source, await scenario.Source.LoadRootAsync(scenario.Identity, Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InterruptedPublicationHasEitherNoRootOrCompleteReceiptHistory(int point) {
        using var scenario = await Scenario.CreateAsync();
        var fault = new HookStore(scenario.Blobs);
        fault.Before = address => {
            if (point == 0 && address.Key.EndsWith(".rcpt", StringComparison.Ordinal) || point == 1 && IsRoot(address)) {
                throw new IOException("interrupted fixture upload");
            }
            return Task.CompletedTask;
        };
        fault.After = address => IsRoot(address) && point == 2 ? throw new IOException("lost root publication response") : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => scenario.CreateFixtureAsync(new(fault, scenario.Target)));
        if (point != 2) {
            Assert.Null(await scenario.Fixture.LoadRootAsync(scenario.Identity, Token));
            Assert.True((await scenario.CreateFixtureAsync()).Ok);
        }
        Assert.Equal(scenario.Applied, await scenario.Fixture.FindOperationReceiptAsync(scenario.Identity, scenario.Applied.OperationId, Token));
        Assert.Equal(scenario.Refused, await scenario.Fixture.FindOperationReceiptAsync(scenario.Identity, scenario.Refused.OperationId, Token));
        Assert.Equal(scenario.Checkpoint, (await scenario.Fixture.LoadRecoveryAsync(scenario.Identity, Token))!.Value.Checkpoint!.Value.Encoded.ToArray());
        Assert.Equal(scenario.History.Source, await scenario.Source.LoadRootAsync(scenario.Identity, Token));
    }

    [Fact]
    public async Task CompetingAuthorityAndMismatchedInputCannotBeReplaced() {
        using var scenario = await Scenario.CreateAsync();
        Assert.False((await scenario.Fixture.CreateReleaseFixtureAsync(scenario.Identity, "different definition"u8.ToArray(), scenario.Checkpoint, scenario.History, Token)).Ok);
        Assert.False((await scenario.Fixture.CreateReleaseFixtureAsync(new(scenario.Identity.Owner, SafeName.Parse("other")), scenario.Definition, scenario.Checkpoint, scenario.History, Token)).Ok);
        Assert.Null(await scenario.Fixture.LoadRootAsync(scenario.Identity, Token));
        var hook = new HookStore(scenario.Blobs);
        WorldAuthorityRootSnapshot? competing = null;
        hook.Before = async address => {
            if (!IsRoot(address)) { return; }
            hook.Before = _ => Task.CompletedTask;
            _ = await scenario.Fixture.AcquireActivationAsync(scenario.Identity, Token);
            competing = await scenario.Fixture.LoadRootAsync(scenario.Identity, Token);
        };
        var outcome = await scenario.CreateFixtureAsync(new(hook, scenario.Target));
        Assert.False(outcome.Ok);
        Assert.NotNull(competing);
        Assert.Equal(competing, await scenario.Fixture.LoadRootAsync(scenario.Identity, Token));
    }

    private static bool IsRoot(ObjectBlobAddress address) => address.Key.EndsWith("/authority/root", StringComparison.Ordinal);
    private sealed class Scenario : IDisposable {
        private readonly TempWorldDirectory m_directory = new();
        public IObjectBlobStore Blobs { get; } = PuckStorageTestComposition.BuildStore();
        public WorldAuthorityIdentity Identity { get; } = new(Guid.NewGuid(), SafeName.Parse("amber"));
        public DirectoryObjectStorageTarget Target { get; }
        public WorldAuthorityBlobStore Source { get; }
        public WorldAuthorityBlobStore Fixture { get; }
        public byte[] Definition { get; private set; } = [];
        public byte[] Checkpoint { get; private set; } = [];
        public WorldAuthorityReceiptSnapshot History { get; private set; } = null!;
        public WorldMutationJournalEntry Entry { get; private set; }
        public WorldAuthorityOperationReceipt Applied { get; } = new(Guid.NewGuid(), "editor", "render-edit", "applied", true, 1, 0);
        public WorldAuthorityOperationReceipt Refused { get; } = new(Guid.NewGuid(), "guest", "refused-edit", "refused", false, 2, null);
        private Scenario() {
            Target = new(Path.Combine(m_directory.RootPath, "fixture"));
            Source = new(Blobs, new DirectoryObjectStorageTarget(Path.Combine(m_directory.RootPath, "source")));
            Fixture = new(Blobs, Target);
        }
        public static async Task<Scenario> CreateAsync(bool receipts = true) {
            var scenario = new Scenario();
            var definition = Fixtures.BuildDocument();
            scenario.Definition = WorldDefinitionSerialization.Serialize(definition);
            using var world = Fixtures.FreshServer(definition);
            world.Step();
            Assert.True(world.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var initial, out var reason), reason);
            var fence = await scenario.Source.AcquireActivationAsync(scenario.Identity, Token);
            Assert.True((await scenario.Source.PublishDefinitionAsync(scenario.Identity, definition, Token, fence)).Ok);
            Assert.True((await scenario.Source.WriteCheckpointAsync(scenario.Identity, WorldAuthorityCheckpointCodec.Encode(initial!), initial!.Server.LastCompletedTick, Token, fence)).Ok);
            var mutation = new WorldMutation.SetRenderDefaults(WorldPrincipal.Console, definition.Render with { AmbientOcclusion = !definition.Render.AmbientOcclusion });
            world.Server.EnqueueMutation(mutation);
            world.Step();
            Assert.True(WorldSubmissionCodec.TryEncodeCommittedMutation(mutation, out var encoded, out _));
            Assert.True(world.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var edited, out reason), reason);
            Assert.NotEmpty(edited!.Server.Journal);
            scenario.Entry = new(edited.Server.LastCompletedTick, encoded);
            Assert.True((await scenario.Source.AppendJournalAsync(scenario.Identity, scenario.Entry, Token, fence, receipts ? scenario.Applied : null)).Ok);
            if (receipts) { Assert.True((await scenario.Source.RecordReceiptAsync(scenario.Identity, scenario.Refused, Token, fence)).Ok); }
            world.Step();
            Assert.True(world.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out reason), reason);
            scenario.Checkpoint = WorldAuthorityCheckpointCodec.Encode(checkpoint!);
            scenario.History = await scenario.Source.CaptureReceiptSnapshotAsync(scenario.Identity, (await scenario.Source.LoadRootAsync(scenario.Identity, Token))!.Value, Token);
            return scenario;
        }
        public Task<WorldAuthorityStoreOutcome> CreateFixtureAsync(WorldAuthorityBlobStore? store = null) =>
            (store ?? Fixture).CreateReleaseFixtureAsync(Identity, Definition, Checkpoint, History, Token);
        public void Dispose() => m_directory.Dispose();
    }
    private sealed class HookStore(IObjectBlobStore inner) : IObjectBlobStore {
        public Func<ObjectBlobAddress, Task> Before { get; set; } = _ => Task.CompletedTask;
        public Func<ObjectBlobAddress, Task> After { get; set; } = _ => Task.CompletedTask;
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid owner, string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(target, owner, prefix, cancellationToken);
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            await Before(address);
            var result = await inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
            await After(address);
            return result;
        }
    }
}
