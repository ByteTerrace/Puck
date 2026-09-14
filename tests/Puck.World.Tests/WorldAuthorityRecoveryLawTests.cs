using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldAuthorityRecoveryLawTests {
    [Fact]
    public async Task RecoveryUsesItsCapturedDefinitionPinWhenRootChangesImmediatelyAfterRead() {
        var blobs = new SwapAfterRootReadStore();
        var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");
        var store = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "coherent")
        );
        var cancellation = TestContext.Current.CancellationToken;
        var original = Fixtures.BuildDocument() with { Metadata = new(Title: "captured publication") };
        var replacement = original with { Metadata = new(Title: "later publication") };

        Assert.True(condition: (await store.PublishDefinitionAsync(
            identity,
            original,
            cancellation
        )).Ok);
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "captured state"u8.ToArray(),
            1,
            cancellation
        )).Ok);
        var before = (await store.LoadRootAsync(
            cancellationToken: cancellation,
            identity: identity
        ))!.Value;

        blobs.AfterNextRootRead = async () => {
            var changed = await store.PublishDefinitionAsync(
                identity,
                replacement,
                cancellation
            );

            Assert.True(
                condition: changed.Ok,
                userMessage: changed.Detail
            );
        };

        var recovery = await store.LoadRecoveryAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.NotNull(value: recovery);
        Assert.Equal(
            before,
            recovery.Value.Root
        );
        Assert.Equal(
            "captured publication",
            recovery.Value.Definition!.Metadata!.Title
        );
        Assert.Equal(
            "captured state"u8.ToArray(),
            recovery.Value.Checkpoint!.Value.Encoded.ToArray()
        );
        // Prove the interference really published: an ordinary fresh read sees the new definition.
        Assert.Equal(
            "later publication",
            (await store.LoadDefinitionAsync(
                cancellationToken: cancellation,
                identity: identity
            ))!.Metadata!.Title
        );
        Assert.NotEqual(
            before.Root.DefinitionHash,
            (await store.LoadRootAsync(
                cancellationToken: cancellation,
                identity: identity
            ))!.Value.Root.DefinitionHash
        );
    }

    private sealed class SwapAfterRootReadStore : IObjectBlobStore {
        private readonly FakeObjectBlobStore m_inner = new();

        public Func<Task>? AfterNextRootRead { get; set; }

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            m_inner.ListAsync(
                cancellationToken: cancellationToken,
                keyPrefix: keyPrefix,
                objectId: objectId,
                target: target
            );
        public async ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) {
            var captured = await m_inner.ReadAsync(
                address: address,
                cancellationToken: cancellationToken,
                target: target
            );

            if (
                address.Key.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: "/authority/root"
            ) &&
                (AfterNextRootRead is { } interference)
            ) {
                AfterNextRootRead = null;
                await interference();
            }
            return captured;
        }
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content,
            ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) =>
            m_inner.WriteAsync(
                address: address,
                cancellationToken: cancellationToken,
                content: content,
                ifMatchVersion: ifMatchVersion,
                mode: mode,
                target: target
            );
    }
}
