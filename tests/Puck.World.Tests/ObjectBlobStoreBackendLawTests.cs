using Puck.Testing;
using Xunit;

using Puck.Storage;

namespace Puck.World.Tests;

/// <summary>Proves the write semantics <see cref="IObjectBlobStore"/> grants — create-only, if-match compare-and-
/// swap, both refusal axes of <see cref="ObjectBlobWriteResult"/> — over <see cref="DirectoryObjectStorageTarget"/>,
/// resolving the routed store through <see cref="PuckStorageTestComposition"/> so the test never touches a backend
/// directly (that seam is internal to <c>Puck.Storage</c> by design). The law body takes any target, so an Azure leg
/// against a local emulator reuses it unchanged.</summary>
public sealed class ObjectBlobStoreBackendLawTests {
    private static async Task RunLawsAsync(IObjectBlobStore store, ObjectStorageTarget target) {
        var objectId = Guid.NewGuid();
        var address = new ObjectBlobAddress(
            Key: "puck/hosted/law-suite/definition.json",
            ObjectId: objectId
        );
        var cancellationToken = TestContext.Current.CancellationToken;

        // A read of a key nothing wrote yet answers absent, not a fault.
        Assert.Null(@object: await store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        ));

        var first = "first"u8.ToArray();
        var createFirst = await store.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: first,
            mode: ObjectBlobWriteMode.CreateOnly,
            target: target
        );

        Assert.True(condition: createFirst.Succeeded);
        Assert.False(condition: createFirst.PreconditionFailed);
        Assert.NotNull(@object: createFirst.VersionToken);

        // A second create-only at the same key is a CREATE-ONLY loss, never a precondition failure.
        var createSecond = await store.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: "second"u8.ToArray(),
            mode: ObjectBlobWriteMode.CreateOnly,
            target: target
        );

        Assert.False(condition: createSecond.Succeeded);
        Assert.False(condition: createSecond.PreconditionFailed);

        var readBack = await store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );

        Assert.NotNull(@object: readBack);
        Assert.True(condition: readBack!.Value.Content.Span.SequenceEqual(other: first));

        // An if-match write against the WRONG token is a PRECONDITION failure, never a create-only loss.
        var wrongMatch = await store.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: "third"u8.ToArray(),
            ifMatchVersion: "not-the-real-token",
            mode: ObjectBlobWriteMode.Overwrite,
            target: target
        );

        Assert.False(condition: wrongMatch.Succeeded);
        Assert.True(condition: wrongMatch.PreconditionFailed);

        // An if-match write against the CURRENT token succeeds and the content moves.
        var rightMatch = await store.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: "fourth"u8.ToArray(),
            ifMatchVersion: readBack.Value.VersionToken,
            mode: ObjectBlobWriteMode.Overwrite,
            target: target
        );

        Assert.True(condition: rightMatch.Succeeded);
        Assert.False(condition: rightMatch.PreconditionFailed);

        var afterMatch = await store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );

        Assert.NotNull(@object: afterMatch);
        Assert.True(condition: afterMatch!.Value.Content.Span.SequenceEqual(other: "fourth"u8));

        // An UNCONDITIONAL overwrite (no if-match) always succeeds regardless of current content.
        var unconditional = await store.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: "fifth"u8.ToArray(),
            mode: ObjectBlobWriteMode.Overwrite,
            target: target
        );

        Assert.True(condition: unconditional.Succeeded);

        var afterUnconditional = await store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );

        Assert.NotNull(@object: afterUnconditional);
        Assert.True(condition: afterUnconditional!.Value.Content.Span.SequenceEqual(other: "fifth"u8));
    }

    [Fact]
    public async Task DirectoryTarget_ObeysCreateOnlyAndIfMatch() {
        using var directory = new TemporaryDirectory();

        await RunLawsAsync(
            store: PuckStorageTestComposition.BuildStore(),
            target: new DirectoryObjectStorageTarget(rootPath: directory.RootPath)
        );
    }
}
