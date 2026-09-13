using System.Security.Cryptography;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises retained packages through the real directory backend, including interrupted publication.</summary>
public sealed class WorldReleaseArchiveLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RetainedPackageSurvivesUploaderLossAndLocalFileChanges() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "archive"));
        var package = Package(directory, owner);
        var archive = new WorldReleaseArchive(blobs, target, owner);
        await archive.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token);
        await archive.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token);
        directory.WriteText("package/assets/shape.bin", "local file changed after release");

        var restarted = new WorldReleaseArchive(blobs, target, owner);
        var loaded = await restarted.LoadAsync(package.Identity, Token);
        Assert.NotNull(loaded);
        Assert.Equal(package.Identity, loaded.Identity);
        Assert.Equal("asset"u8.ToArray(), (await restarted.ReadFileAsync(loaded, "assets/shape.bin", Token)).ToArray());
        await restarted.VerifyAsync(loaded, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token));
        var otherOwner = new WorldReleaseArchive(blobs, target, Guid.NewGuid());
        Assert.Null(await otherOwner.LoadAsync(package.Identity, Token));
    }

    [Fact]
    public async Task InterruptedUploadDoesNotPublishManifestAndRetryUsesTheRetainedBytes() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "archive"));
        var package = Package(directory, owner);
        var interrupted = new InterruptedStore(blobs);
        var archive = new WorldReleaseArchive(interrupted, target, owner);
        await Assert.ThrowsAsync<IOException>(() => archive.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token));
        Assert.Null(await archive.LoadAsync(package.Identity, Token));
        Assert.Equal(2, (await blobs.ListAsync(target, owner, "private/puck/hosted/releases/content", Token)).Count);
        var restarted = new WorldReleaseArchive(blobs, target, owner);
        await restarted.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token);
        Assert.NotNull(await restarted.LoadAsync(package.Identity, Token));
        await restarted.VerifyAsync(package, Token);
    }

    [Fact]
    public async Task CorruptRetainedContentIsRejectedAndCannotBeSilentlyOverwritten() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "archive"));
        var package = Package(directory, owner);
        var archive = new WorldReleaseArchive(blobs, target, owner);
        await archive.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token);
        var address = new ObjectBlobAddress(owner, "private/puck/hosted/releases/content/" + package.Artifacts["assets/shape.bin"][7..]);
        await blobs.WriteAsync(target, address, "corrupt"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadFileAsync(package, "assets/shape.bin", Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.SaveAsync(package, Path.Combine(directory.RootPath, "package"), Token));
        Assert.Equal("corrupt"u8.ToArray(), (await blobs.ReadAsync(target, address, Token))!.Value.Content.ToArray());
    }

    private static WorldReleaseManifest Package(TempWorldDirectory directory, Guid owner) {
        var definition = Fixtures.DefaultWorldBytes();
        directory.WriteBytes("package/row.world.json", definition);
        directory.WriteBytes("package/assets/shape.bin", "asset"u8.ToArray());
        return new() {
            Label = "retained-test", SourceRevision = new string('a', 40), EngineImageDigest = "sha256:" + new string('a', 64),
            Definitions = new Dictionary<string, string> { [$"{owner:D}/row"] = Pin(definition) },
            DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/row"] = "row.world.json" },
            Artifacts = new Dictionary<string, string> { ["assets/shape.bin"] = Pin("asset"u8.ToArray()) },
            PersistenceContract = "puck.world.persistence.v1", PeerProtocolContract = "puck.world.peer.v1"
        };
    }

    private static string Pin(byte[] bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class InterruptedStore(IObjectBlobStore inner) : IObjectBlobStore {
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (address.Key.Contains("/manifests/", StringComparison.Ordinal)) { throw new IOException("injected uploader loss before manifest publication"); }
            return inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
        }
    }
}
