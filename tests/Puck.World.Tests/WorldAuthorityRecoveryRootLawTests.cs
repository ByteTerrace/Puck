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
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "authority"
        ));
        var blobs = PuckStorageTestComposition.BuildStore();
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        var operation = Guid.NewGuid();
        var cancellation = TestContext.Current.CancellationToken;
        var first = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var fence = await first.AcquireActivationAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.NotNull(value: fence);
        Assert.True(condition: (await first.PublishDefinitionAsync(
            identity,
            Fixtures.BuildDocument(),
            cancellation,
            fence
        )).Ok);
        Assert.True(condition: (await first.WriteCheckpointAsync(
            identity,
            "protected-checkpoint"u8.ToArray(),
            17,
            cancellation,
            fence
        )).Ok);
        Assert.Null(value: await first.FindRecoveryRootAsync(
            cancellationToken: cancellation,
            identity: identity,
            operationId: operation
        ));

        var captured = await first.CaptureRecoveryRootAsync(
            cancellationToken: cancellation,
            identity: identity,
            operationId: operation
        );

        Assert.NotNull(value: captured);
        Assert.StartsWith(
            "sha256/",
            captured.Value.Pin,
            StringComparison.Ordinal
        );
        Assert.Equal(
            64,
            (captured.Value.Pin.Length - "sha256/".Length)
        );

        var restarted = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var loaded = await restarted.LoadRecoveryRootAsync(
            identity,
            captured.Value.Pin,
            operation,
            cancellation
        );

        Assert.Equal(
            actual: loaded,
            expected: captured
        );
        Assert.True(condition: (await restarted.WriteCheckpointAsync(
            identity,
            "later-checkpoint"u8.ToArray(),
            18,
            cancellation,
            fence
        )).Ok);
        var latest = await restarted.LoadRootAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.Equal(
            captured,
            await restarted.FindRecoveryRootAsync(
                cancellationToken: cancellation,
                identity: identity,
                operationId: operation
            )
        );
        Assert.Equal(
            latest,
            await restarted.LoadRootAsync(
                cancellationToken: cancellation,
                identity: identity
            )
        );

        var restored = await restarted.RestoreRecoveryRootAsync(
            identity,
            captured.Value.Pin,
            operation,
            fence!.Value,
            cancellation
        );

        Assert.True(
            condition: restored.Ok,
            userMessage: restored.Detail
        );
        Assert.NotNull(value: restored.PublishedRoot);
        Assert.Equal(
            Guid.Empty,
            restored.PublishedRoot.Value.Root.FenceToken
        );
        Assert.True(condition: (restored.PublishedRoot.Value.Root.Epoch > fence.Value.Epoch));
        Assert.True(condition: (restored.PublishedRoot.Value.Root.Sequence > captured.Value.Root.Sequence));
        Assert.Equal(
            captured.Value.Root.DefinitionHash,
            restored.PublishedRoot.Value.Root.DefinitionHash
        );
        Assert.Equal(
            captured.Value.Root.CheckpointHash,
            restored.PublishedRoot.Value.Root.CheckpointHash
        );

        var fresh = await restarted.AcquireActivationAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.NotNull(value: fresh);
        Assert.True(condition: (fresh.Value.Epoch > restored.PublishedRoot.Value.Root.Epoch));
    }
    [Fact]
    public async Task RestoreWithStaleFenceCannotOverwriteNewActivation() {
        using var directory = new TempWorldDirectory();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "authority"
        ));
        var blobs = PuckStorageTestComposition.BuildStore();
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        var operation = Guid.NewGuid();
        var cancellation = TestContext.Current.CancellationToken;
        var store = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var oldFence = await store.AcquireActivationAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.NotNull(value: oldFence);
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "protected"u8.ToArray(),
            4,
            cancellation,
            oldFence
        )).Ok);
        var pin = (await store.CaptureRecoveryRootAsync(
            cancellationToken: cancellation,
            identity: identity,
            operationId: operation
        ))!.Value.Pin;
        var newFence = await store.AcquireActivationAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.NotNull(value: newFence);
        var before = await store.LoadRootAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        var refused = await store.RestoreRecoveryRootAsync(
            identity,
            pin,
            operation,
            oldFence!.Value,
            cancellation
        );

        Assert.Equal(
            WorldAuthorityStoreOutcomeKind.StaleFence,
            refused.Kind
        );
        Assert.Equal(
            before,
            await store.LoadRootAsync(
                cancellationToken: cancellation,
                identity: identity
            )
        );
    }
    [Fact]
    public async Task TamperedRecoveryBlobIsRejectedBeforeRestore() {
        using var directory = new TempWorldDirectory();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "authority"
        ));
        var blobs = PuckStorageTestComposition.BuildStore();
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        var operation = Guid.NewGuid();
        var cancellation = TestContext.Current.CancellationToken;
        var store = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var fence = await store.AcquireActivationAsync(
            cancellationToken: cancellation,
            identity: identity
        );

        Assert.NotNull(value: fence);
        Assert.True(condition: (await store.WriteCheckpointAsync(
            identity,
            "protected"u8.ToArray(),
            4,
            cancellation,
            fence
        )).Ok);
        var pin = (await store.CaptureRecoveryRootAsync(
            cancellationToken: cancellation,
            identity: identity,
            operationId: operation
        ))!.Value.Pin;
        var keys = await blobs.ListAsync(
            target,
            identity.Owner,
            "private/puck/hosted",
            cancellation
        );
        var key = Assert.Single(
            collection: keys,
            predicate: candidate => candidate.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"/recovery/{operation:D}/"
            )
        );
        var path = Path.Combine(
            path1: target.RootPath,
            path2: identity.Owner.ToString(format: "D"),
            path3: key.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );

        File.WriteAllBytes(
            path,
            "tampered"u8.ToArray()
        );

        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => store.LoadRecoveryRootAsync(
            cancellationToken: cancellation,
            identity: identity,
            operationId: operation,
            pin: pin
        ));
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => store.FindRecoveryRootAsync(
            cancellationToken: cancellation,
            identity: identity,
            operationId: operation
        ));
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => store.RestoreRecoveryRootAsync(
            identity,
            pin,
            operation,
            fence!.Value,
            cancellation
        ));
    }
}
