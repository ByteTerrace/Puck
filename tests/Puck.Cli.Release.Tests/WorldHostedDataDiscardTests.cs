using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>The one door that deletes reachable hosted data (<see cref="WorldHostedData.DiscardAsync"/>): its live
/// policy read, its one-group scope, its marker against a half-discarded record, and what it keeps.</summary>
public sealed class WorldHostedDataDiscardTests {
    private const string Hosted = (WorldOwnedWorldSync.HostedPrivateNamespace + "/");

    [Fact]
    public async Task ADiscardIsScopedToOneGroupMarkedUntilFinishedAndKeepsReleaseContent() {
        using var directory = new TemporaryDirectory(prefix: "puck-hosted-discard-");
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var blobs = provider.GetRequiredService<IObjectBlobStore>();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(path1: directory.RootPath, path2: "store"));
        var owner = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var groups = new WorldReleaseGroupStore(blobs, target, owner);

        Assert.True((await groups.CreateAsync(deploymentGroup: "first", activeRelease: null, cancellationToken: token)).Ok);
        // One owner container holds one group.
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => groups.CreateAsync(deploymentGroup: "second", activeRelease: null, cancellationToken: token));
        string[] authority = [$"{Hosted}alpha/authority/root", $"{Hosted}alpha/authority/checkpoints/1.pckp", $"{Hosted}beta/authority/root"];
        string[] kept = [$"{Hosted}releases/content/manifest.json", $"{Hosted}tls/world.example/state.json"];

        foreach (var key in authority.Concat(second: kept)) {
            Assert.True((await blobs.WriteAsync(target, new(ObjectId: owner, Key: key), Encoding.UTF8.GetBytes(s: key), ObjectBlobWriteMode.CreateOnly, cancellationToken: token)).Succeeded);
        }
        async Task<bool> Exists(string key) => ((await blobs.ReadAsync(target, new(ObjectId: owner, Key: key), token)) is not null);
        Task<WorldHostedDataDiscard> Discard(WorldHostedDataPolicy policy, Func<CancellationToken, Task> stop) => WorldHostedData.DiscardAsync(
            cancellationToken: token,
            deploymentGroup: "first",
            owner: owner,
            readPolicy: _ => Task.FromResult(result: policy),
            stopWriters: stop,
            store: blobs,
            target: target
        );

        // A durable declaration discards nothing and leaves no marker.
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => Discard(WorldHostedDataPolicy.Durable, _ => Task.CompletedTask));
        Assert.NotNull(await groups.LoadAsync(deploymentGroup: "first", cancellationToken: token));
        // A discard that stops before deleting leaves its marker, which refuses the group's record until a discard finishes.
        await Assert.ThrowsAsync<IOException>(testCode: () => Discard(WorldHostedDataPolicy.Scratch, _ => throw new IOException(message: "the writers could not be stopped")));
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => groups.LoadAsync(deploymentGroup: "first", cancellationToken: token));
        foreach (var key in authority) { Assert.True(await Exists(key)); }
        var stopped = false;
        var discarded = await Discard(WorldHostedDataPolicy.Scratch, _ => { stopped = true; return Task.CompletedTask; });

        Assert.True(stopped);
        Assert.Equal(expected: ["alpha", "beta"], actual: discarded.Worlds);
        Assert.Equal(expected: authority.Length, actual: discarded.DeletedObjects);
        Assert.True(discarded.GroupRecordDeleted);
        foreach (var key in authority) { Assert.False(await Exists(key)); }
        foreach (var key in kept) { Assert.True(await Exists(key)); }
        Assert.Null(await groups.LoadAsync(deploymentGroup: "first", cancellationToken: token));
        // The group is gone, so the container may hold another.
        Assert.True((await groups.CreateAsync(deploymentGroup: "second", activeRelease: null, cancellationToken: token)).Ok);
    }
}
