using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseReceiptFixtureLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool IsRoot(ObjectBlobAddress address) => address.Key.EndsWith(
        comparisonType: StringComparison.Ordinal,
        value: "/authority/root"
    );

    [Fact]
    public async Task CompetingAuthorityAndMismatchedInputCannotBeReplaced() {
        using var scenario = await Scenario.CreateAsync();

        Assert.False(condition: (await scenario.Fixture.CreateReleaseFixtureAsync(
            scenario.Identity,
            "different definition"u8.ToArray(),
            scenario.Checkpoint,
            scenario.History,
            Token
        )).Ok);
        Assert.False(condition: (await scenario.Fixture.CreateReleaseFixtureAsync(
            new(
                Owner: scenario.Identity.Owner,
                World: SafeName.Parse(candidate: "other")
            ),
            scenario.Definition,
            scenario.Checkpoint,
            scenario.History,
            Token
        )).Ok);
        Assert.Null(value: await scenario.Fixture.LoadRootAsync(
            scenario.Identity,
            Token
        ));
        var hook = new HookStore(inner: scenario.Blobs);
        WorldAuthorityRootSnapshot? competing = null;

        hook.Before = async address => {
            if (!IsRoot(address: address)) { return; }
            hook.Before = _ => Task.CompletedTask;
            _ = await scenario.Fixture.AcquireActivationAsync(
                scenario.Identity,
                Token
            );
            competing = await scenario.Fixture.LoadRootAsync(
                scenario.Identity,
                Token
            );
        };
        var outcome = await scenario.CreateFixtureAsync(store: new(
            store: hook,
            target: scenario.Target
        ));

        Assert.False(condition: outcome.Ok);
        Assert.NotNull(value: competing);
        Assert.Equal(
            competing,
            await scenario.Fixture.LoadRootAsync(
                scenario.Identity,
                Token
            )
        );
    }
    [Fact]
    public async Task ExplicitEmptyHistoryCanCreateAFixtureButLegacyStateCannotBeReplaced() {
        using var empty = await Scenario.CreateAsync(receipts: false);

        Assert.Empty(collection: empty.History.Validate());
        Assert.True(condition: (await empty.CreateFixtureAsync()).Ok);
        var root = (await empty.Fixture.LoadRootAsync(
            empty.Identity,
            Token
        ))!.Value.Root;

        Assert.Null(@object: root.ReceiptHash);
        Assert.Null(@object: root.ReceiptIndexHash);
        using var legacy = await Scenario.CreateAsync();
        var address = WorldOwnedWorldSync.HostedAddressFor(
            legacy.Identity.Owner,
            legacy.Identity.World,
            "definition.json"
        );

        await legacy.Blobs.WriteAsync(
            legacy.Target,
            address,
            legacy.Definition,
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: Token
        );
        Assert.False(condition: (await legacy.CreateFixtureAsync()).Ok);
        Assert.Null(value: await legacy.Fixture.LoadRootAsync(
            legacy.Identity,
            Token
        ));
        Assert.Equal(
            legacy.Definition,
            (await legacy.Blobs.ReadAsync(
                legacy.Target,
                address,
                Token
            ))!.Value.Content.ToArray()
        );
    }
    [Fact]
    public async Task FixturePreservesRealCheckpointReceiptLookupAndDuplicateDecisionsAcrossContinuation() {
        using var scenario = await Scenario.CreateAsync();
        var result = await scenario.CreateFixtureAsync();

        Assert.True(
            condition: result.Ok,
            userMessage: result.Detail
        );
        var loaded = (await scenario.Fixture.LoadRecoveryAsync(
            scenario.Identity,
            Token
        ))!.Value;

        Assert.Equal(
            scenario.Checkpoint,
            loaded.Checkpoint!.Value.Encoded.ToArray()
        );
        Assert.Empty(collection: loaded.Journal.Entries);
        Assert.Equal(
            scenario.History.Source.Root.JournalSequence,
            loaded.Root.Root.JournalSequence
        );
        Assert.Equal(
            loaded.Root.Root.JournalSequence,
            loaded.Root.Root.CheckpointCoverageSequence
        );
        Assert.Equal(
            (scenario.History.Source.Root.CheckpointOrdinal + 1),
            loaded.Root.Root.CheckpointOrdinal
        );
        Assert.True(condition: (loaded.Root.Root.Epoch > scenario.History.Source.Root.Epoch));
        Assert.True(condition: (loaded.Root.Root.Sequence > scenario.History.Source.Root.Sequence));
        Assert.Equal(
            Guid.Empty,
            loaded.Root.Root.FenceToken
        );
        Assert.Equal(
            scenario.History.Source.Root.ReceiptIndexHash,
            loaded.Root.Root.ReceiptIndexHash
        );
        Assert.Equal(
            scenario.History.Source.Root.ReceiptHash,
            loaded.Root.Root.ReceiptHash
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: scenario.Checkpoint,
                checkpoint: out var saved,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var continued = Fixtures.FreshServer(WorldDefinitionSerialization.Deserialize(utf8Json: saved!.Server.DefinitionJson));

        continued.Server.RestoreCheckpoint(checkpoint: saved);
        continued.Step();
        Assert.True(
            condition: continued.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var latest,
                out reason
            ),
            userMessage: reason
        );
        var fence = await scenario.Fixture.AcquireActivationAsync(
            scenario.Identity,
            Token
        );

        Assert.True(condition: (await scenario.Fixture.WriteCheckpointAsync(
            scenario.Identity,
            WorldAuthorityCheckpointCodec.Encode(checkpoint: latest!),
            latest!.Server.LastCompletedTick,
            Token,
            fence
        )).Ok);
        var restarted = new WorldAuthorityBlobStore(
            store: scenario.Blobs,
            target: scenario.Target
        );

        Assert.Equal(
            scenario.Applied,
            await restarted.FindOperationReceiptAsync(
                scenario.Identity,
                scenario.Applied.OperationId,
                Token
            )
        );
        Assert.Equal(
            scenario.Refused,
            await restarted.FindOperationReceiptAsync(
                scenario.Identity,
                scenario.Refused.OperationId,
                Token
            )
        );
        var beforeRetry = await restarted.LoadRootAsync(
            scenario.Identity,
            Token
        );

        Assert.True(condition: (await restarted.AppendJournalAsync(
            scenario.Identity,
            scenario.Entry,
            Token,
            fence,
            scenario.Applied
        )).Ok);
        Assert.Equal(
            beforeRetry,
            await restarted.LoadRootAsync(
                scenario.Identity,
                Token
            )
        );
        var conflict = await restarted.AppendJournalAsync(
            scenario.Identity,
            scenario.Entry,
            Token,
            fence,
            scenario.Applied with { PayloadDigest = "different" }
        );

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.OperationConflict,
            conflict.Kind
        );
        Assert.Equal(
            beforeRetry,
            await restarted.LoadRootAsync(
                scenario.Identity,
                Token
            )
        );
        Assert.False(condition: (await scenario.CreateFixtureAsync()).Ok);
        Assert.Equal(
            beforeRetry,
            await restarted.LoadRootAsync(
                scenario.Identity,
                Token
            )
        );
        Assert.Equal(
            scenario.History.Source,
            await scenario.Source.LoadRootAsync(
                scenario.Identity,
                Token
            )
        );
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public async Task InterruptedPublicationHasEitherNoRootOrCompleteReceiptHistory(int point) {
        using var scenario = await Scenario.CreateAsync();
        var fault = new HookStore(inner: scenario.Blobs);

        fault.Before = address => {
            if (
                ((point == 0) && address.Key.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".rcpt"
            )) ||
                ((point == 1) && IsRoot(address: address))
            ) {
                throw new IOException(message: "interrupted fixture upload");
            }
            return Task.CompletedTask;
        };
        fault.After = address => ((IsRoot(address: address) && (point == 2))
            ? throw new IOException(message: "lost root publication response")
            : Task.CompletedTask
        );
        await Assert.ThrowsAsync<IOException>(testCode: () => scenario.CreateFixtureAsync(store: new(
            store: fault,
            target: scenario.Target
        )));
        if (point != 2) {
            Assert.Null(value: await scenario.Fixture.LoadRootAsync(
                scenario.Identity,
                Token
            ));
            Assert.True(condition: (await scenario.CreateFixtureAsync()).Ok);
        }
        Assert.Equal(
            scenario.Applied,
            await scenario.Fixture.FindOperationReceiptAsync(
                scenario.Identity,
                scenario.Applied.OperationId,
                Token
            )
        );
        Assert.Equal(
            scenario.Refused,
            await scenario.Fixture.FindOperationReceiptAsync(
                scenario.Identity,
                scenario.Refused.OperationId,
                Token
            )
        );
        Assert.Equal(
            scenario.Checkpoint,
            (await scenario.Fixture.LoadRecoveryAsync(
                scenario.Identity,
                Token
            ))!.Value.Checkpoint!.Value.Encoded.ToArray()
        );
        Assert.Equal(
            scenario.History.Source,
            await scenario.Source.LoadRootAsync(
                scenario.Identity,
                Token
            )
        );
    }

    private sealed class Scenario : IDisposable {
        private readonly TempWorldDirectory m_directory = new();

        public IObjectBlobStore Blobs { get; } = PuckStorageTestComposition.BuildStore();
        public WorldAuthorityIdentity Identity { get; } = new(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        public byte[] Definition { get; private set; } = [];
        public byte[] Checkpoint { get; private set; } = [];
        public WorldAuthorityReceiptSnapshot History { get; private set; } = null!;
        public WorldAuthorityOperationReceipt Applied { get; } = new(
            Guid.NewGuid(),
            "editor",
            "render-edit",
            "applied",
            true,
            1,
            0
        );
        public WorldAuthorityOperationReceipt Refused { get; } = new(
            Guid.NewGuid(),
            "guest",
            "refused-edit",
            "refused",
            false,
            2,
            null
        );

        public WorldMutationJournalEntry Entry { get; private set; }
        public WorldAuthorityBlobStore Fixture { get; }
        public WorldAuthorityBlobStore Source { get; }
        public DirectoryObjectStorageTarget Target { get; }

        private Scenario() {
            Target = new(Path.Combine(
                path1: m_directory.RootPath,
                path2: "fixture"
            ));
            Source = new(
                store: Blobs,
                target: new DirectoryObjectStorageTarget(Path.Combine(
                    path1: m_directory.RootPath,
                    path2: "source"
                ))
            );
            Fixture = new(
                store: Blobs,
                target: Target
            );
        }

        public static async Task<Scenario> CreateAsync(bool receipts = true) {
            var scenario = new Scenario();
            var definition = Fixtures.BuildDocument();

            scenario.Definition = WorldDefinitionSerialization.Serialize(definition: definition);
            using var world = Fixtures.FreshServer(definition);

            world.Step();
            Assert.True(
                condition: world.Server.TryCaptureCheckpoint(
                    WorldAuthorityHostRowCheckpoint.Empty,
                    out var initial,
                    out var reason
                ),
                userMessage: reason
            );
            var fence = await scenario.Source.AcquireActivationAsync(
                scenario.Identity,
                Token
            );

            Assert.True(condition: (await scenario.Source.PublishDefinitionAsync(
                scenario.Identity,
                definition,
                Token,
                fence
            )).Ok);
            Assert.True(condition: (await scenario.Source.WriteCheckpointAsync(
                scenario.Identity,
                WorldAuthorityCheckpointCodec.Encode(checkpoint: initial!),
                initial!.Server.LastCompletedTick,
                Token,
                fence
            )).Ok);
            var mutation = new WorldMutation.SetRenderDefaults(
                Principal: WorldPrincipal.Console,
                Render: definition.Render with { AmbientOcclusion = !definition.Render.AmbientOcclusion }
            );

            world.Server.EnqueueMutation(mutation);
            world.Step();
            Assert.True(condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
                bytes: out var encoded,
                failure: out _,
                mutation: mutation
            ));
            Assert.True(
                condition: world.Server.TryCaptureCheckpoint(
                    WorldAuthorityHostRowCheckpoint.Empty,
                    out var edited,
                    out reason
                ),
                userMessage: reason
            );
            Assert.NotEmpty(collection: edited!.Server.Journal);
            scenario.Entry = new(
                edited.Server.LastCompletedTick,
                encoded
            );
            Assert.True(condition: (await scenario.Source.AppendJournalAsync(
                scenario.Identity,
                scenario.Entry,
                Token,
                fence,
                (receipts
                ? scenario.Applied
                : null)
            )).Ok);
            if (receipts) { Assert.True(condition: (await scenario.Source.RecordReceiptAsync(
                scenario.Identity,
                scenario.Refused,
                Token,
                fence
            )).Ok); }
            world.Step();
            Assert.True(
                condition: world.Server.TryCaptureCheckpoint(
                    WorldAuthorityHostRowCheckpoint.Empty,
                    out var checkpoint,
                    out reason
                ),
                userMessage: reason
            );
            scenario.Checkpoint = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);
            scenario.History = await scenario.Source.CaptureReceiptSnapshotAsync(
                scenario.Identity,
                (await scenario.Source.LoadRootAsync(
                    scenario.Identity,
                    Token
                ))!.Value,
                Token
            );
            return scenario;
        }
        public Task<WorldAuthorityStoreOutcome> CreateFixtureAsync(WorldAuthorityBlobStore? store = null) =>
            (store ?? Fixture).CreateReleaseFixtureAsync(
                Identity,
                Definition,
                Checkpoint,
                History,
                Token
            );
        public void Dispose() => m_directory.Dispose();
    }
    private sealed class HookStore(IObjectBlobStore inner) : IObjectBlobStore {
        public Func<ObjectBlobAddress, Task> Before { get; set; } = _ => Task.CompletedTask;
        public Func<ObjectBlobAddress, Task> After { get; set; } = _ => Task.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid owner, string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: prefix,
            objectId: owner,
            target: target
        );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            await Before(address);
            var result = await inner.WriteAsync(
                address: address,
                cancellationToken: cancellationToken,
                content: content,
                ifMatchVersion: ifMatchVersion,
                mode: mode,
                target: target
            );

            await After(address);
            return result;
        }
    }
}
