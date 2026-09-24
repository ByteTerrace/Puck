using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseFixtureArchiveLawTests {
    [Fact]
    public async Task InterruptedCapturePublishesNoInventoryAndCannotReplaceACompletedRequest() {
        using var directory = new TemporaryDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var broken = new InterruptedStore(blobs);
        var archive = new WorldReleaseFixtureArchive(
            owner: owner,
            store: broken,
            target: target
        );
        var request = Guid.NewGuid();
        var machine = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var alphaReceipts = (await CaptureReceiptsAsync(
            blobs: blobs,
            owner: owner,
            target: target,
            token: token,
            world: "alpha"
        )).History;
        var checkpoints = new Dictionary<string, WorldReleaseFixtureCheckpoint> {
            ["alpha"] = new(
            "alpha checkpoint"u8.ToArray(),
            5,
            alphaReceipts
        ),
            ["beta"] = new(
            "beta checkpoint"u8.ToArray(),
            5,
            (await CaptureReceiptsAsync(
                blobs: blobs,
                owner: owner,
                target: target,
                token: token,
                world: "beta"
            )).History
        ),
        };
        var release = ("sha256/" + new string(
            c: 'a',
            count: 64
        ));

        await Assert.ThrowsAsync<IOException>(testCode: () => archive.SaveAsync(
            request,
            "official",
            release,
            machine,
            checkpoints,
            token
        ));
        Assert.Null(@object: await archive.LoadAsync(
            cancellationToken: token,
            requestId: request
        ));
        var restarted = new WorldReleaseFixtureArchive(
            owner: owner,
            store: blobs,
            target: target
        );
        var manifest = await restarted.SaveAsync(
            request,
            "official",
            release,
            machine,
            checkpoints,
            token
        );

        Assert.Equal(
            manifest.Identity,
            (await restarted.LoadAsync(
                cancellationToken: token,
                requestId: request
            ))!.Identity
        );
        checkpoints["alpha"] = new(
            "later checkpoint"u8.ToArray(),
            8,
            alphaReceipts
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => restarted.SaveAsync(
            request,
            "official",
            release,
            machine,
            checkpoints,
            token
        ));
        Assert.Equal(
            "alpha checkpoint"u8.ToArray(),
            (await restarted.ReadCheckpointAsync(
                cancellationToken: token,
                manifest: manifest,
                world: "alpha"
            )).ToArray()
        );
        var address = new ObjectBlobAddress(
            owner,
            $"private/puck/hosted/releases/fixtures/content/{manifest.Worlds["alpha"].Hash[7..]}.pckp"
        );

        await blobs.WriteAsync(
            target,
            address,
            "corrupt"u8.ToArray(),
            ObjectBlobWriteMode.Overwrite,
            cancellationToken: token
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => restarted.ReadCheckpointAsync(
            cancellationToken: token,
            manifest: manifest,
            world: "alpha"
        ));
    }
    [Fact]
    public async Task ReceiptGraphIsPinnedBeforeInventoryAndARowWithoutItsPinIsMalformed() {
        using var directory = new TemporaryDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var token = TestContext.Current.CancellationToken;

        var (receipt, history) = await CaptureReceiptsAsync(
            blobs: blobs,
            owner: owner,
            target: target,
            token: token,
            world: "alpha"
        );
        var checkpoint = new Dictionary<string, WorldReleaseFixtureCheckpoint> {
            ["alpha"] = new(
            "checkpoint"u8.ToArray(),
            5,
            history
        ),
        };
        var request = Guid.NewGuid();
        var release = ("sha256/" + new string(
            c: 'a',
            count: 64
        ));
        var archive = new WorldReleaseFixtureArchive(
            owner: owner,
            store: blobs,
            target: target
        );
        var machine = Guid.NewGuid();
        var interrupted = new WorldReleaseFixtureArchive(
            new InterruptedStore(
                inner: blobs,
                suffix: ".receipts"
            ),
            target,
            owner
        );

        await Assert.ThrowsAsync<IOException>(testCode: () => interrupted.SaveAsync(
            request,
            "official",
            release,
            machine,
            checkpoint,
            token
        ));
        Assert.Null(@object: await archive.LoadAsync(
            cancellationToken: token,
            requestId: request
        ));
        var manifest = await archive.SaveAsync(
            request,
            "official",
            release,
            machine,
            checkpoint,
            token
        );
        var reread = (await archive.LoadAsync(
            cancellationToken: token,
            requestId: request
        ))!;

        Assert.Equal(
            manifest.Identity,
            reread.Identity
        );
        var copied = await archive.ReadReceiptsAsync(
            cancellationToken: token,
            manifest: reread,
            world: "alpha"
        );

        Assert.Equal(
            history.Encode(),
            copied.Encode()
        );
        Assert.Equal(
            receipt,
            copied.Validate()[receipt.OperationId]
        );
        var future = System.Text.Json.Nodes.JsonNode.Parse(history.Encode())!;

        future["unsupported"] = true;
        Assert.Throws<InvalidDataException>(testCode: () => WorldAuthorityReceiptSnapshot.Decode(bytes: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(future)));
        future.AsObject().Remove(propertyName: "unsupported");
        future["Schema"] = "future";
        Assert.Contains(
            "unsupported receipt snapshot schema",
            Assert.Throws<InvalidDataException>(testCode: () => WorldAuthorityReceiptSnapshot.Decode(bytes: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(future))).Message
        );
        // The same inventory under another request loads; without its row's receipt pin it is malformed.
        var inventory = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest))!;

        foreach (var drop in new[] { false, true }) {
            var copy = Guid.NewGuid();

            inventory["RequestId"] = copy.ToString(format: "D");
            if (drop) { _ = inventory["Worlds"]!["alpha"]!.AsObject().Remove(propertyName: "ReceiptsHash"); }
            await blobs.WriteAsync(
                target,
                new(
                    Key: $"private/puck/hosted/releases/fixtures/{copy:D}.json",
                    ObjectId: owner
                ),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(inventory),
                ObjectBlobWriteMode.CreateOnly,
                cancellationToken: token
            );
            if (drop) {
                await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.LoadAsync(
                    cancellationToken: token,
                    requestId: copy
                ));
            } else {
                Assert.Equal(
                    manifest.Worlds["alpha"],
                    (await archive.LoadAsync(
                        cancellationToken: token,
                        requestId: copy
                    ))!.Worlds["alpha"]
                );
            }
        }
        var pin = manifest.Worlds["alpha"].ReceiptsHash!;

        await blobs.WriteAsync(
            target,
            new(
                owner,
                $"private/puck/hosted/releases/fixtures/content/{pin[7..]}.receipts"
            ),
            "corrupt"u8.ToArray(),
            ObjectBlobWriteMode.Overwrite,
            cancellationToken: token
        );
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.ReadReceiptsAsync(
            cancellationToken: token,
            manifest: manifest,
            world: "alpha"
        ));
    }

    // Records one refused receipt under a fresh activation and captures the resulting receipt graph.
    private static async Task<(WorldAuthorityOperationReceipt Receipt, WorldAuthorityReceiptSnapshot History)> CaptureReceiptsAsync(IObjectBlobStore blobs, ObjectStorageTarget target, Guid owner, string world, CancellationToken token) {
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: world)
        );
        var fence = await authority.AcquireActivationAsync(
            cancellationToken: token,
            identity: identity
        );
        var receipt = new WorldAuthorityOperationReceipt(
            Guid.NewGuid(),
            "actor",
            "payload",
            "refused",
            false,
            1,
            null
        );

        Assert.True(condition: (await authority.RecordReceiptAsync(
            cancellationToken: token,
            identity: identity,
            receipt: receipt,
            suppliedFence: fence
        )).Ok);
        return (receipt, await authority.CaptureReceiptSnapshotAsync(
            identity,
            (await authority.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            ))!.Value,
            token
        ));
    }

    private sealed class InterruptedStore(IObjectBlobStore inner, string suffix = ".json") : IObjectBlobStore {
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
            if (address.Key.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: suffix
            )) { throw new IOException(message: "lost upload before fixture publication"); }
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
