using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseFixtureArchiveLawTests {
    [Fact]
    public async Task ReceiptGraphIsPinnedBeforeInventoryAndMissingLegacyProofIsExplicit() {
        using var directory = new TempWorldDirectory();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var token = TestContext.Current.CancellationToken;
        var authority = new WorldAuthorityBlobStore(blobs, target);
        var identity = new WorldAuthorityIdentity(owner, SafeName.Parse("alpha"));
        var fence = await authority.AcquireActivationAsync(identity, token);
        var receipt = new WorldAuthorityOperationReceipt(Guid.NewGuid(), "actor", "payload", "refused", false, 1, null);
        Assert.True((await authority.RecordReceiptAsync(identity, receipt, token, fence)).Ok);
        var history = await authority.CaptureReceiptSnapshotAsync(identity, (await authority.LoadRootAsync(identity, token))!.Value, token);
        var checkpoint = new Dictionary<string, WorldReleaseFixtureCheckpoint> { ["alpha"] = new("checkpoint"u8.ToArray(), 5, history) };
        var request = Guid.NewGuid();
        var release = "sha256/" + new string('a', 64);
        var archive = new WorldReleaseFixtureArchive(blobs, target, owner);
        var machine = Guid.NewGuid();
        var interrupted = new WorldReleaseFixtureArchive(new InterruptedStore(blobs, ".receipts"), target, owner);
        await Assert.ThrowsAsync<IOException>(() => interrupted.SaveAsync(request, "official", release, machine, checkpoint, token));
        Assert.Null(await archive.LoadAsync(request, token));
        var manifest = await archive.SaveAsync(request, "official", release, machine, checkpoint, token);
        var reread = (await archive.LoadAsync(request, token))!;
        Assert.Equal(manifest.Identity, reread.Identity);
        var copied = await archive.ReadReceiptsAsync(reread, "alpha", token);
        Assert.Equal(history.Encode(), copied.Encode());
        Assert.Equal(receipt, copied.Validate()[receipt.OperationId]);
        var future = System.Text.Json.Nodes.JsonNode.Parse(history.Encode())!;
        future["unsupported"] = true;
        Assert.Throws<InvalidDataException>(() => WorldAuthorityReceiptSnapshot.Decode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(future)));
        future.AsObject().Remove("unsupported");
        future["Schema"] = "future";
        Assert.Contains("unsupported receipt snapshot schema", Assert.Throws<InvalidDataException>(() => WorldAuthorityReceiptSnapshot.Decode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(future))).Message);
        var legacy = await archive.SaveAsync(Guid.NewGuid(), "official", release, machine,
            new Dictionary<string, WorldReleaseFixtureCheckpoint> { ["alpha"] = new("checkpoint"u8.ToArray(), 5) }, token);
        Assert.NotEqual(manifest.Identity, legacy.Identity);
        Assert.DoesNotContain("ReceiptsHash", System.Text.Json.JsonSerializer.Serialize(legacy));
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadReceiptsAsync(legacy, "alpha", token));
        Assert.Contains("source worker", missing.Message);
        var pin = manifest.Worlds["alpha"].ReceiptsHash!;
        await blobs.WriteAsync(target, new(owner, $"private/puck/hosted/releases/fixtures/content/{pin[7..]}.receipts"), "corrupt"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: token);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadReceiptsAsync(manifest, "alpha", token));
    }

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

    private sealed class InterruptedStore(IObjectBlobStore inner, string suffix = ".json") : IObjectBlobStore {
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (address.Key.EndsWith(suffix, StringComparison.Ordinal)) { throw new IOException("lost upload before fixture publication"); }
            return inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
        }
    }
}
