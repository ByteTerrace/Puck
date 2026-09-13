using System.Security.Cryptography;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises retained packages through the real directory backend, including interrupted publication.</summary>
public sealed class WorldReleaseArchiveLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorldReleaseManifest Package(TempWorldDirectory directory, Guid owner) {
        var definition = Fixtures.DefaultWorldBytes();

        directory.WriteBytes(
            bytes: definition,
            name: "package/row.world.json"
        );
        directory.WriteBytes(
            "package/assets/shape.bin",
            "asset"u8.ToArray()
        );
        return new() {
            Label = "retained-test",
            SourceRevision = new string(
            c: 'a',
            count: 40
        ),
            EngineImageDigest = ("sha256:" + new string(
            c: 'a',
            count: 64
        )),
            Definitions = new Dictionary<string, string> { [$"{owner:D}/row"] = Pin(bytes: definition) },
            DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/row"] = "row.world.json" },
            Artifacts = new Dictionary<string, string> { ["assets/shape.bin"] = Pin(bytes: "asset"u8.ToArray()) },
            PersistenceContract = "puck.world.persistence.v1",
            PeerProtocolContract = "puck.world.peer.v1",
        };
    }
    private static string Pin(byte[] bytes) => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));

    [Fact]
    public async Task CorruptRetainedContentIsRejectedAndCannotBeSilentlyOverwritten() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "archive"
        ));
        var package = Package(
            directory: directory,
            owner: owner
        );
        var archive = new WorldReleaseArchive(
            blobs,
            target,
            owner
        );

        await archive.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        );
        var address = new ObjectBlobAddress(
            owner,
            ("private/puck/hosted/releases/content/" + package.Artifacts["assets/shape.bin"][7..])
        );

        await blobs.WriteAsync(
            target,
            address,
            "corrupt"u8.ToArray(),
            ObjectBlobWriteMode.Overwrite,
            cancellationToken: Token
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.ReadFileAsync(
            package,
            "assets/shape.bin",
            Token
        ));
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        ));
        Assert.Equal(
            "corrupt"u8.ToArray(),
            (await blobs.ReadAsync(
                target,
                address,
                Token
            ))!.Value.Content.ToArray()
        );
    }
    [Fact]
    public async Task InterruptedUploadDoesNotPublishManifestAndRetryUsesTheRetainedBytes() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "archive"
        ));
        var package = Package(
            directory: directory,
            owner: owner
        );
        var interrupted = new InterruptedStore(inner: blobs);
        var archive = new WorldReleaseArchive(
            interrupted,
            target,
            owner
        );

        await Assert.ThrowsAsync<IOException>(testCode: () => archive.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        ));
        Assert.Null(@object: await archive.LoadAsync(
            package.Identity,
            Token
        ));
        Assert.Equal(
            2,
            (await blobs.ListAsync(
                target,
                owner,
                "private/puck/hosted/releases/content",
                Token
            )).Count
        );
        var restarted = new WorldReleaseArchive(
            blobs,
            target,
            owner
        );

        await restarted.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        );
        Assert.NotNull(@object: await restarted.LoadAsync(
            package.Identity,
            Token
        ));
        await restarted.VerifyAsync(
            package,
            Token
        );
    }
    [Fact]
    public async Task RetainedPackageSurvivesUploaderLossAndLocalFileChanges() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "archive"
        ));
        var package = Package(
            directory: directory,
            owner: owner
        );
        var archive = new WorldReleaseArchive(
            blobs,
            target,
            owner
        );

        await archive.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        );
        await archive.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        );
        directory.WriteText(
            name: "package/assets/shape.bin",
            text: "local file changed after release"
        );

        var restarted = new WorldReleaseArchive(
            blobs,
            target,
            owner
        );
        var loaded = await restarted.LoadAsync(
            package.Identity,
            Token
        );

        Assert.NotNull(@object: loaded);
        Assert.Equal(
            package.Identity,
            loaded.Identity
        );
        Assert.Equal(
            "asset"u8.ToArray(),
            (await restarted.ReadFileAsync(
                loaded,
                "assets/shape.bin",
                Token
            )).ToArray()
        );
        await restarted.VerifyAsync(
            loaded,
            Token
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.SaveAsync(
            package,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            Token
        ));
        var otherOwner = new WorldReleaseArchive(
            blobs,
            target,
            Guid.NewGuid()
        );

        Assert.Null(@object: await otherOwner.LoadAsync(
            package.Identity,
            Token
        ));
    }

    private sealed class InterruptedStore(IObjectBlobStore inner) : IObjectBlobStore {
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: keyPrefix,
            objectId: objectId,
            target: target
        );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (address.Key.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "/manifests/"
            )) { throw new IOException(message: "injected uploader loss before manifest publication"); }
            return inner.WriteAsync(
                address: address,
                cancellationToken: cancellationToken,
                content: content,
                ifMatchVersion: ifMatchVersion,
                mode: mode,
                target: target
            );
        }
    }
}
