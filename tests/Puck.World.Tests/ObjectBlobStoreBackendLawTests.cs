using Xunit;

using Puck.Storage;

namespace Puck.World.Tests;

/// <summary>Proves the write semantics <see cref="IObjectBlobStore"/> grants — create-only, if-match compare-and-
/// swap, both refusal axes of <see cref="ObjectBlobWriteResult"/> — hold identically over
/// <see cref="DirectoryObjectStorageTarget"/> and <see cref="AzureBlobObjectStorageTarget"/>, so a local run cannot
/// pass on a write semantic Azure would refuse. Drives the same law body against both, resolving the routed store
/// through <see cref="PuckStorageTestComposition"/> so the test never touches a backend directly (that seam is
/// internal to <c>Puck.Storage</c> by design).</summary>
public sealed class ObjectBlobStoreBackendLawTests {
    // The env var an operator sets to run the Azure leg against a real account; a connection string or a service
    // URI, exactly what AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri accepts.
    private const string AzureTargetEnvironmentVariable = "PUCK_TEST_AZURE_BLOB_TARGET";

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
        using var directory = new TempWorldDirectory();

        await RunLawsAsync(
            store: PuckStorageTestComposition.BuildStore(),
            target: new DirectoryObjectStorageTarget(rootPath: directory.RootPath)
        );
    }
    [Fact]
    public async Task AzureTarget_ObeysCreateOnlyAndIfMatch() {
        var connection = Environment.GetEnvironmentVariable(variable: AzureTargetEnvironmentVariable);

        Assert.SkipWhen(
            condition: string.IsNullOrWhiteSpace(value: connection),
            reason: $"no credentials — set {AzureTargetEnvironmentVariable} to a connection string or service URI to run this leg"
        );

        await RunLawsAsync(
            store: PuckStorageTestComposition.BuildStore(),
            target: AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: connection!)
        );
    }
}
