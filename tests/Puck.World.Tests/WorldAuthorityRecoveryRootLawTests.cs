using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Directory-backed laws for immutable authority recovery-root pins. These tests use two store instances to
/// prove that the pin survives a store restart and that restoration is guarded by the current activation fence.</summary>
public sealed class WorldAuthorityRecoveryRootLawTests {
    [Fact]
    public async Task CaptureSurvivesStoreRestartAndRestoreCreatesFreshUnownedEpoch() {
        using var directory = new TempWorldDirectory();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "authority"));
        var blobs = PuckStorageTestComposition.BuildStore();
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("amber"));
        var operation = Guid.NewGuid();
        var cancellation = TestContext.Current.CancellationToken;
        var first = new WorldAuthorityBlobStore(blobs, target);
        var fence = await first.AcquireActivationAsync(identity, cancellation);
        Assert.NotNull(fence);
        Assert.True((await first.PublishDefinitionAsync(identity, Fixtures.BuildDocument(), cancellation, fence)).Ok);
        Assert.True((await first.WriteCheckpointAsync(identity, "protected-checkpoint"u8.ToArray(), 17, cancellation, fence)).Ok);

        var captured = await first.CaptureRecoveryRootAsync(identity, operation, cancellation);
        Assert.NotNull(captured);
        Assert.StartsWith("sha256/", captured.Value.Pin, StringComparison.Ordinal);
        Assert.Equal(64, captured.Value.Pin.Length - "sha256/".Length);

        var restarted = new WorldAuthorityBlobStore(blobs, target);
        var loaded = await restarted.LoadRecoveryRootAsync(identity, captured.Value.Pin, operation, cancellation);
        Assert.Equal(captured, loaded);

        var restored = await restarted.RestoreRecoveryRootAsync(identity, captured.Value.Pin, operation, fence!.Value, cancellation);
        Assert.True(restored.Ok, restored.Detail);
        Assert.NotNull(restored.PublishedRoot);
        Assert.Equal(Guid.Empty, restored.PublishedRoot.Value.Root.FenceToken);
        Assert.True(restored.PublishedRoot.Value.Root.Epoch > fence.Value.Epoch);
        Assert.True(restored.PublishedRoot.Value.Root.Sequence > captured.Value.Root.Sequence);
        Assert.Equal(captured.Value.Root.DefinitionHash, restored.PublishedRoot.Value.Root.DefinitionHash);
        Assert.Equal(captured.Value.Root.CheckpointHash, restored.PublishedRoot.Value.Root.CheckpointHash);

        var fresh = await restarted.AcquireActivationAsync(identity, cancellation);
        Assert.NotNull(fresh);
        Assert.True(fresh.Value.Epoch > restored.PublishedRoot.Value.Root.Epoch);
    }

    [Fact]
    public async Task RestoreWithStaleFenceCannotOverwriteNewActivation() {
        using var directory = new TempWorldDirectory();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "authority"));
        var blobs = PuckStorageTestComposition.BuildStore();
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("amber"));
        var operation = Guid.NewGuid();
        var cancellation = TestContext.Current.CancellationToken;
        var store = new WorldAuthorityBlobStore(blobs, target);
        var oldFence = await store.AcquireActivationAsync(identity, cancellation);
        Assert.NotNull(oldFence);
        Assert.True((await store.WriteCheckpointAsync(identity, "protected"u8.ToArray(), 4, cancellation, oldFence)).Ok);
        var pin = (await store.CaptureRecoveryRootAsync(identity, operation, cancellation))!.Value.Pin;
        var newFence = await store.AcquireActivationAsync(identity, cancellation);
        Assert.NotNull(newFence);
        var before = await store.LoadRootAsync(identity, cancellation);

        var refused = await store.RestoreRecoveryRootAsync(identity, pin, operation, oldFence!.Value, cancellation);

        Assert.Equal(WorldAuthorityStoreOutcomeKind.StaleFence, refused.Kind);
        Assert.Equal(before, await store.LoadRootAsync(identity, cancellation));
    }

    [Fact]
    public async Task TamperedRecoveryBlobIsRejectedBeforeRestore() {
        using var directory = new TempWorldDirectory();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "authority"));
        var blobs = PuckStorageTestComposition.BuildStore();
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("amber"));
        var operation = Guid.NewGuid();
        var cancellation = TestContext.Current.CancellationToken;
        var store = new WorldAuthorityBlobStore(blobs, target);
        var fence = await store.AcquireActivationAsync(identity, cancellation);
        Assert.NotNull(fence);
        Assert.True((await store.WriteCheckpointAsync(identity, "protected"u8.ToArray(), 4, cancellation, fence)).Ok);
        var pin = (await store.CaptureRecoveryRootAsync(identity, operation, cancellation))!.Value.Pin;
        var keys = await blobs.ListAsync(target, identity.Owner, "private/puck/hosted", cancellation);
        var key = Assert.Single(keys, candidate => candidate.Contains($"/recovery/{operation:D}/", StringComparison.Ordinal));
        var path = Path.Combine(target.RootPath, identity.Owner.ToString("D"), key.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(path, "tampered"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadRecoveryRootAsync(identity, pin, operation, cancellation));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreRecoveryRootAsync(identity, pin, operation, fence!.Value, cancellation));
    }
}
