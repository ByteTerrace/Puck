using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseFixtureArchiveLawTests {
    [Fact]
    public async Task InterruptedCapturePublishesNoInventoryAndCannotReplaceACompletedRequest() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var broken = new InterruptedStore(blobs);
        var archive = new WorldReleaseFixtureArchive(broken, target, owner);
        var request = Guid.NewGuid();
        var machine = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var checkpoints = new Dictionary<string, WorldReleaseFixtureCheckpoint> {
            ["alpha"] = new("alpha checkpoint"u8.ToArray(), 5), ["beta"] = new("beta checkpoint"u8.ToArray(), 5),
        };
        var release = "sha256/" + new string('a', 64);
        await Assert.ThrowsAsync<IOException>(() => archive.SaveAsync(request, "official", release, machine, checkpoints, token));
        Assert.Null(await archive.LoadAsync(request, token));
        var restarted = new WorldReleaseFixtureArchive(blobs, target, owner);
        var manifest = await restarted.SaveAsync(request, "official", release, machine, checkpoints, token);
        Assert.Equal(manifest.Identity, (await restarted.LoadAsync(request, token))!.Identity);
        checkpoints["alpha"] = new("later checkpoint"u8.ToArray(), 8);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.SaveAsync(request, "official", release, machine, checkpoints, token));
        Assert.Equal("alpha checkpoint"u8.ToArray(), (await restarted.ReadCheckpointAsync(manifest, "alpha", token)).ToArray());
        var address = new ObjectBlobAddress(owner, $"private/puck/hosted/releases/fixtures/content/{manifest.Worlds["alpha"].Hash[7..]}.pckp");
        await blobs.WriteAsync(target, address, "corrupt"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: token);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ReadCheckpointAsync(manifest, "alpha", token));
    }

    private sealed class InterruptedStore(IObjectBlobStore inner) : IObjectBlobStore {
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (address.Key.EndsWith(".json", StringComparison.Ordinal)) { throw new IOException("lost upload before fixture publication"); }
            return inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
        }
    }
}
