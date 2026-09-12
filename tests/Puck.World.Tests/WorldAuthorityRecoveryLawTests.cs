using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldAuthorityRecoveryLawTests {
    [Fact]
    public async Task RecoveryUsesItsCapturedDefinitionPinWhenRootChangesImmediatelyAfterRead() {
        var blobs = new SwapAfterRootReadStore();
        var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri("UseDevelopmentStorage=true");
        var store = new WorldAuthorityBlobStore(blobs, target);
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("coherent"));
        var cancellation = TestContext.Current.CancellationToken;
        var original = Fixtures.BuildDocument() with { Metadata = new(Title: "captured publication") };
        var replacement = original with { Metadata = new(Title: "later publication") };
        Assert.True((await store.PublishDefinitionAsync(identity, original, cancellation)).Ok);
        Assert.True((await store.WriteCheckpointAsync(identity, "captured state"u8.ToArray(), 1, cancellation)).Ok);
        var before = (await store.LoadRootAsync(identity, cancellation))!.Value;
        blobs.AfterNextRootRead = async () => {
            var changed = await store.PublishDefinitionAsync(identity, replacement, cancellation);
            Assert.True(changed.Ok, changed.Detail);
        };

        var recovery = await store.LoadRecoveryAsync(identity, cancellation);

        Assert.NotNull(recovery);
        Assert.Equal(before, recovery.Value.Root);
        Assert.Equal("captured publication", recovery.Value.Definition!.Metadata!.Title);
        Assert.Equal("captured state"u8.ToArray(), recovery.Value.Checkpoint!.Value.Encoded.ToArray());
        // Prove the interference really published: an ordinary fresh read sees the new definition.
        Assert.Equal("later publication", (await store.LoadDefinitionAsync(identity, cancellation))!.Metadata!.Title);
        Assert.NotEqual(before.Root.DefinitionHash, (await store.LoadRootAsync(identity, cancellation))!.Value.Root.DefinitionHash);
    }

    private sealed class SwapAfterRootReadStore : IObjectBlobStore {
        private readonly FakeObjectBlobStore m_inner = new();
        public Func<Task>? AfterNextRootRead { get; set; }

        public async ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) {
            var captured = await m_inner.ReadAsync(target, address, cancellationToken);
            if (address.Key.EndsWith("/authority/root", StringComparison.Ordinal) && AfterNextRootRead is { } interference) {
                AfterNextRootRead = null;
                await interference();
            }
            return captured;
        }
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content,
            ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) =>
            m_inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            m_inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
    }
}
