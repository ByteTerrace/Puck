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
        var archive = new WorldReleaseFixtureArchive(
            owner: owner,
            store: broken,
            target: target
        );
        var request = Guid.NewGuid();
        var machine = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var checkpoints = new Dictionary<string, WorldReleaseFixtureCheckpoint> {
            ["alpha"] = new(
            "alpha checkpoint"u8.ToArray(),
            5
        ),
            ["beta"] = new(
            "beta checkpoint"u8.ToArray(),
            5
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
            8
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
    public async Task ReceiptGraphIsPinnedBeforeInventoryAndMissingLegacyProofIsExplicit() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var token = TestContext.Current.CancellationToken;
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "alpha")
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
        var history = await authority.CaptureReceiptSnapshotAsync(
            identity,
            (await authority.LoadRootAsync(
                cancellationToken: token,
                identity: identity
            ))!.Value,
            token
        );
        var checkpoint = new Dictionary<string, WorldReleaseFixtureCheckpoint> { ["alpha"] = new(
            "checkpoint"u8.ToArray(),
            5,
            history
        ) };
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
        var legacy = await archive.SaveAsync(
            Guid.NewGuid(),
            "official",
            release,
            machine,
            new Dictionary<string, WorldReleaseFixtureCheckpoint> { ["alpha"] = new(
                "checkpoint"u8.ToArray(),
                5
            ) },
            token
        );

        Assert.NotEqual(
            manifest.Identity,
            legacy.Identity
        );
        Assert.DoesNotContain(
            "ReceiptsHash",
            System.Text.Json.JsonSerializer.Serialize(legacy)
        );
        var missing = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => archive.ReadReceiptsAsync(
            cancellationToken: token,
            manifest: legacy,
            world: "alpha"
        ));

        Assert.Contains(
            "source worker",
            missing.Message
        );
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
