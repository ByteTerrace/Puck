using Xunit;

using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Hermetic laws for <see cref="WorldAuthorityBlobStore"/> over <see cref="FakeObjectBlobStore"/> — the
/// checkpoint content-address/create-only path, the authority root compare-and-swap, the journal's
/// read-modify-write append, and the published-definition round trip.</summary>
public sealed class WorldAuthorityBlobStoreTests {
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

    private static WorldAuthorityIdentity Identity(Guid? owner = null) => new(
        Owner: (owner ?? Guid.NewGuid()),
        World: SafeName.Parse(candidate: "amber")
    );

    [Fact]
    public async Task AppendJournalAsync_BeforeAnyCheckpoint_Refuses() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );

        var outcome = await store.AppendJournalAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            entry: new WorldMutationJournalEntry(
                Tick: 1UL,
                EngineTick: 1UL,
                Encoded: "a"u8.ToArray()
            ),
            identity: Identity()
        );

        Assert.False(condition: outcome.Ok);
    }
    [Fact]
    public async Task AppendJournalAsync_ThenLoadJournalTailAsync_RoundTripsInOrder() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;

        var checkpoint = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: "checkpoint"u8.ToArray(),
            identity: identity,
            tick: 100UL
        );

        Assert.True(
            condition: checkpoint.Ok,
            userMessage: checkpoint.Detail
        );

        for (var index = 0; (index < 3); index++) {
            var appended = await store.AppendJournalAsync(
                cancellationToken: cancellationToken,
                entry: new WorldMutationJournalEntry(
                    Encoded: new byte[] { ((byte)index) },
                    EngineTick: ((ulong)(200 + index)),
                    Tick: ((ulong)(200 + index))
                ),
                identity: identity
            );

            Assert.True(
                condition: appended.Ok,
                userMessage: appended.Detail
            );
        }

        var tail = await store.LoadJournalTailAsync(
            afterOrdinal: 0,
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.Equal(
            expected: 3,
            actual: tail.Entries.Count
        );
        for (var index = 0; (index < 3); index++) {
            Assert.Equal(
                expected: ((ulong)(200 + index)),
                actual: tail.Entries[index].Tick
            );
            Assert.Equal(
                expected: ((byte)index),
                actual: tail.Entries[index].Encoded.Span[0]
            );
        }
    }
    [Fact]
    public async Task CheckpointWithOlderTick_CannotRegressAuthoritativeCheckpoint() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "new"u8.ToArray(),
            10,
            cancellationToken
        )).Ok);

        var refused = await store.WriteCheckpointAsync(
            identity,
            "old"u8.ToArray(),
            9,
            cancellationToken
        );

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.PreconditionFailed,
            refused.Kind
        );
        Assert.Equal(
            10UL,
            (await store.LoadLatestAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ))!.Value.Tick
        );
    }
    [Fact]
    public async Task CheckpointWithoutCapturedCoverage_NeverErasesLaterJournalTail() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var fence = await store.AcquireActivationAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(value: fence);
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "base"u8.ToArray(),
            1,
            TestContext.Current.CancellationToken,
            fence
        )).Ok);
        Assert.True(condition: (await store.AppendJournalAsync(
            identity,
            new WorldMutationJournalEntry(
                2,
                2,
                "tail"u8.ToArray()
            ),
            TestContext.Current.CancellationToken,
            fence
        )).Ok);
        var refused = await store.WriteCheckpointAsync(
            identity,
            "stale-snapshot"u8.ToArray(),
            3,
            TestContext.Current.CancellationToken,
            fence
        );

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.PreconditionFailed,
            refused.Kind
        );
        var tail = await store.LoadJournalTailAsync(
            identity,
            0,
            TestContext.Current.CancellationToken
        );

        Assert.Single(collection: tail.Entries);
        Assert.Equal(
            "tail"u8[0],
            tail.Entries[0].Encoded.Span[0]
        );
    }
    // An identity nothing was ever published or checkpointed under reads back as absent, never as an error.
    [InlineData("definition")]
    [InlineData("latest")]
    [Theory]
    public async Task ALoadOfANeverWrittenIdentity_ReturnsNull(string load) {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );

        Assert.Null(@object: ((load == "definition")
            ? await store.LoadDefinitionAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                identity: Identity()
            )
            : await store.LoadLatestAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                identity: Identity()
            )));
    }
    [Fact]
    public async Task LoadJournalTailAsync_WithNoAppends_IsEmptyNotAFault() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var tail = await store.LoadJournalTailAsync(
            afterOrdinal: 0,
            cancellationToken: TestContext.Current.CancellationToken,
            identity: Identity()
        );

        Assert.Empty(collection: tail.Entries);
        Assert.Equal(
            expected: 0,
            actual: tail.CheckpointOrdinal
        );
    }
    [Fact]
    public async Task LoadLatestAsync_ATamperedCheckpointBlob_ThrowsInsteadOfReturningCorruptBytes() {
        var fake = new FakeObjectBlobStore();
        var store = new WorldAuthorityBlobStore(
            store: fake,
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;

        var write = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: "checkpoint"u8.ToArray(),
            identity: identity,
            tick: 1UL
        );

        Assert.True(
            condition: write.Ok,
            userMessage: write.Detail
        );

        // The key carries the checkpoint's own content hash; probe for it by listing rather than recomputing it.
        var listed = await fake.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: "private/puck/hosted",
            objectId: identity.Owner,
            target: Target
        );
        var checkpointBlobKey = listed.Single(predicate: key => key.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "/checkpoints/000000000000-"
        ));

        fake.Seed(
            bytes: "tampered"u8.ToArray(),
            key: checkpointBlobKey,
            objectId: identity.Owner
        );

        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => store.LoadLatestAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ));
    }
    [Fact]
    public async Task PausedWriterAfterTakeover_IsRefusedByFence() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var first = await store.AcquireActivationAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(value: first);
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "base"u8.ToArray(),
            1,
            TestContext.Current.CancellationToken,
            first
        )).Ok);
        var takeover = await store.AcquireActivationAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(value: takeover);
        var stale = await store.AppendJournalAsync(
            identity,
            new WorldMutationJournalEntry(
                2,
                2,
                "old"u8.ToArray()
            ),
            TestContext.Current.CancellationToken,
            first
        );

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.StaleFence,
            stale.Kind
        );
    }
    [Fact]
    public async Task PublishDefinitionAsync_ThenLoadDefinitionAsync_RoundTrips() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;
        var composed = Fixtures.BuildDocument();

        var published = await store.PublishDefinitionAsync(
            cancellationToken: cancellationToken,
            composed: composed,
            identity: identity
        );

        Assert.True(
            condition: published.Ok,
            userMessage: published.Detail
        );

        var loaded = await store.LoadDefinitionAsync(
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.NotNull(@object: loaded);
        Assert.Equal(
            actual: WorldDefinitionSerialization.Serialize(definition: loaded!),
            expected: WorldDefinitionSerialization.Serialize(definition: composed)
        );
    }
    [Fact]
    public async Task ReceiptOperationId_BindsActorAndPayloadAcrossReplay() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var fence = await store.AcquireActivationAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(value: fence);
        var operation = new WorldAuthorityOperationReceipt(
            Guid.NewGuid(),
            "seat:1",
            "digest-a",
            "refused",
            false,
            4,
            null
        );

        Assert.True(condition: (await store.RecordReceiptAsync(
            identity,
            operation,
            TestContext.Current.CancellationToken,
            fence
        )).Ok);
        Assert.True(condition: (await store.RecordReceiptAsync(
            identity,
            operation,
            TestContext.Current.CancellationToken,
            fence
        )).Ok);
        var conflict = operation with { Actor = "seat:2" };

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.OperationConflict,
            (await store.RecordReceiptAsync(
                identity,
                conflict,
                TestContext.Current.CancellationToken,
                fence
            )).Kind
        );
        Assert.Equal(
            operation,
            await store.FindOperationReceiptAsync(
                identity,
                operation.OperationId,
                TestContext.Current.CancellationToken
            )
        );

        var appliedStandalone = operation with { OperationId = Guid.NewGuid(), Applied = true };
        var appliedStandaloneOutcome = await store.RecordReceiptAsync(
            identity,
            appliedStandalone,
            TestContext.Current.CancellationToken,
            fence
        );

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.PreconditionFailed,
            appliedStandaloneOutcome.Kind
        );
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "receipt-base"u8.ToArray(),
            5,
            TestContext.Current.CancellationToken,
            fence
        )).Ok);
        var refusedOnJournal = operation with { OperationId = Guid.NewGuid() };

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.PreconditionFailed,
            (await store.AppendJournalAsync(
                identity,
                new WorldMutationJournalEntry(
                    5,
                    5,
                    "not-applied"u8.ToArray()
                ),
                TestContext.Current.CancellationToken,
                fence,
                refusedOnJournal
            )).Kind
        );
    }
    [Fact]
    public async Task RootCommitThatReportsUncertain_ReconcilesReceiptAndRetryDoesNotDuplicateJournal() {
        var fake = new ThrowAfterRootCommitStore();
        var store = new WorldAuthorityBlobStore(
            store: fake,
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fence = await store.AcquireActivationAsync(
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.NotNull(value: fence);
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "base"u8.ToArray(),
            1,
            cancellationToken,
            fence
        )).Ok);

        fake.ThrowOnNextRootOverwrite = true;
        var receipt = new WorldAuthorityOperationReceipt(
            Guid.NewGuid(),
            "seat:1",
            "digest",
            "applied",
            true,
            2,
            0
        );
        var first = await store.AppendJournalAsync(
            identity,
            new WorldMutationJournalEntry(
                2,
                2,
                "entry"u8.ToArray()
            ),
            cancellationToken,
            fence,
            receipt
        );

        Assert.True(
            condition: first.Ok,
            userMessage: first.Detail
        );
        Assert.NotNull(value: first.PublishedRoot);
        var retry = await store.AppendJournalAsync(
            identity,
            new WorldMutationJournalEntry(
                2,
                2,
                "entry"u8.ToArray()
            ),
            cancellationToken,
            fence,
            receipt
        );

        Assert.True(
            condition: retry.Ok,
            userMessage: retry.Detail
        );
        Assert.NotNull(value: retry.PublishedRoot);
        var tail = await store.LoadJournalTailAsync(
            afterOrdinal: 0,
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.Single(collection: tail.Entries);
        Assert.Equal(
            "entry"u8[0],
            tail.Entries[0].Encoded.Span[0]
        );
    }
    [Fact]
    public async Task WriteCheckpointAsync_RetriedWithIdenticalBytesAfterTheRootNeverAdvanced_IsIdempotent() {
        var fake = new FakeObjectBlobStore();
        var store = new WorldAuthorityBlobStore(
            store: fake,
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;
        var encoded = "checkpoint"u8.ToArray();
        var hash = WorldDefinitionFileSource.ComputeContentHash(content: encoded);
        var hex = hash["sha256-64/".Length..];

        // Simulates a writer that landed the content-addressed checkpoint candidate but crashed before the root CAS —
        // the retry recomputes the SAME ordinal+hash and must recognize the identical content rather than refusing.
        fake.Seed(
            bytes: encoded,
            key: $"{WorldOwnedWorldSync.HostedPrivateNamespace}/{identity.World.Value}/authority/checkpoints/000000000000-{hex}.pckp",
            objectId: identity.Owner
        );

        var outcome = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: encoded,
            identity: identity,
            tick: 5UL
        );

        Assert.True(
            condition: outcome.Ok,
            userMessage: outcome.Detail
        );

        var latest = await store.LoadLatestAsync(
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.NotNull(@object: latest);
        Assert.Equal(
            expected: 0,
            actual: latest!.Value.Ordinal
        );
    }
    [Fact]
    public async Task WriteCheckpointAsync_Twice_AdvancesOrdinalAndLatestNamesTheSecond() {
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = Identity();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: "one"u8.ToArray(),
            identity: identity,
            tick: 10UL
        );

        Assert.True(
            condition: first.Ok,
            userMessage: first.Detail
        );

        var second = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: "two"u8.ToArray(),
            identity: identity,
            tick: 20UL
        );

        Assert.True(
            condition: second.Ok,
            userMessage: second.Detail
        );

        var latest = await store.LoadLatestAsync(
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.NotNull(@object: latest);
        Assert.Equal(
            expected: 1,
            actual: latest!.Value.Ordinal
        );
        Assert.Equal(
            expected: 20UL,
            actual: latest.Value.Tick
        );
        Assert.True(condition: latest.Value.Encoded.Span.SequenceEqual(other: "two"u8));
    }

    private sealed class ThrowAfterRootCommitStore : IObjectBlobStore {
        private readonly FakeObjectBlobStore m_inner = new();

        public bool ThrowOnNextRootOverwrite { get; set; }

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => m_inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: keyPrefix,
            objectId: objectId,
            target: target
        );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => m_inner.ReadAsync(
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
                ThrowOnNextRootOverwrite &&
                (mode == ObjectBlobWriteMode.Overwrite) &&
                address.Key.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: "/authority/root"
            ) &&
                result.Succeeded
            ) {
                ThrowOnNextRootOverwrite = false;
                throw new IOException(message: "simulated response loss after root commit");
            }
            return result;
        }
    }
}
