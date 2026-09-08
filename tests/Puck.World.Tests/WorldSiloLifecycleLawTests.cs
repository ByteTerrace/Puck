using System.Security.Cryptography;
using System.Text.Json;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldSiloLifecycleLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task PublishedReloadUsesExistingRebuildAndCommitsOnlyAfterCheckpoint(bool failCheckpoint) {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(path1: directory.RootPath, path2: "world.key");

        File.WriteAllBytes(keyFile, key.ExportPkcs8PrivateKey());
        var identity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "row"));
        var store = new FailingWrites(inner: PuckStorageTestComposition.BuildStore());
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var backend = new WorldAuthorityBlobStore(store: store, target: target);
        var definition = Fixtures.BuildDocument();

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:33333", Presentation = WorldHostPresentation.None } };
        Assert.True(condition: (await backend.PublishDefinitionAsync(identity, definition, TestContext.Current.CancellationToken)).Ok);
        var host = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var instances = host.Instances;
        var activation = host.ActivateAsync(identity, TestContext.Current.CancellationToken);

        await PumpAsync(host: host, operation: activation);
        Assert.True(condition: await activation);
        var reload = host.ReloadAsync(identity, TestContext.Current.CancellationToken);

        await PumpAsync(host: host, operation: reload, step: true);
        var hash = await reload;
        var first = await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken);

        Assert.NotNull(value: first);
        var again = host.ReloadAsync(identity, TestContext.Current.CancellationToken);

        await PumpAsync(host: host, operation: again, step: true);
        Assert.Equal(hash, await again);
        Assert.Equal(first.Value.Ordinal, (await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken))!.Value.Ordinal);
        var changed = definition with { Metadata = new(Title: "Published update") };

        Assert.True(condition: (await backend.PublishDefinitionAsync(identity, changed, TestContext.Current.CancellationToken)).Ok);
        Assert.True(condition: host.Instances.TryGet(identity.World.Value, out var active));
        var rebuilds = 0;

        active!.Server.EchoTap += echo => { if ((echo.Kind == WorldEditEchoKind.Rebuild) && !echo.Rejected) { rebuilds++; } };
        store.Fail = failCheckpoint;
        var update = host.ReloadAsync(identity, TestContext.Current.CancellationToken);

        if (failCheckpoint) {
            await Assert.ThrowsAsync<IOException>(testCode: () => PumpAsync(host, update, step: true));
            Assert.Equal(first.Value.Ordinal, (await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken))!.Value.Ordinal);
            Assert.Equal("Published update", active.Server.Definition.Metadata!.Title);
            store.Fail = false;
            update = host.ReloadAsync(identity, TestContext.Current.CancellationToken);
        }
        await PumpAsync(host, update, step: true);
        Assert.NotEqual(hash, await update);
        Assert.Equal(actual: rebuilds, expected: 1);
        Assert.Equal("Published update", active!.Server.Definition.Metadata!.Title);
        var saved = (await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken))!.Value;

        Assert.True(condition: (saved.Ordinal > first.Value.Ordinal));
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: saved.Encoded.Span, checkpoint: out var decoded, reason: out var reason), userMessage: reason);
        Assert.Equal("Published update", WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson).Metadata!.Title);

        var refused = changed with { HostRaw = changed.Host with { Authority = "another-host:33333" } };

        Assert.True(condition: (await backend.PublishDefinitionAsync(identity, refused, TestContext.Current.CancellationToken)).Ok);
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => PumpAsync(host, host.ReloadAsync(identity, TestContext.Current.CancellationToken), step: true));
        Assert.Equal(saved.Ordinal, (await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken))!.Value.Ordinal);
        // Simulate losing the release-marker write after the rebuilt checkpoint became durable.
        Assert.True(condition: (await backend.PublishDefinitionAsync(identity, changed, TestContext.Current.CancellationToken)).Ok);
        await store.WriteAsync(target, WorldOwnedWorldSync.HostedAddressFor(identity.Owner, identity.World, "release"),
            System.Text.Encoding.UTF8.GetBytes(s: hash), ObjectBlobWriteMode.Overwrite, cancellationToken: TestContext.Current.CancellationToken);
        await PumpAsync(host, host.DrainAsync(ct: TestContext.Current.CancellationToken));
        var recovered = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var recoveredInstances = recovered.Instances;

        await PumpAsync(recovered, recovered.ActivateAsync(identity, TestContext.Current.CancellationToken));
        Assert.True(condition: recovered.Instances.TryGet(identity.World.Value, out var restored));
        var recoveryRebuilds = 0;

        restored!.Server.EchoTap += echo => { if (echo.Kind == WorldEditEchoKind.Rebuild) { recoveryRebuilds++; } };
        // Marker reconciliation must complete without a simulation step or another rebuild.
        await PumpAsync(recovered, recovered.ReloadAsync(identity, TestContext.Current.CancellationToken));
        Assert.Equal(actual: recoveryRebuilds, expected: 0);
        Assert.Equal("Published update", restored.Server.Definition.Metadata!.Title);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ConcurrentDrainCallersObserveTheirOwnCancellationAndCanRetry(bool cancelFirst) {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        var host = Host(directory.RootPath, PuckStorageTestComposition.BuildStore(), output, []);
        using var instances = host.Instances;
        using var cancellation = new CancellationTokenSource();
        var first = host.DrainAsync(ct: (cancelFirst ? cancellation.Token : TestContext.Current.CancellationToken));
        var second = host.DrainAsync(ct: (cancelFirst ? TestContext.Current.CancellationToken : cancellation.Token));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => (cancelFirst ? first : second));
        await PumpAsync(host: host, operation: (cancelFirst ? second : first));
        Assert.True(condition: host.IsDraining);
        await host.DrainAsync(ct: TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task CancellationBeforeThePumpDoesNotFreezeTheHost() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        var host = Host(directory.RootPath, PuckStorageTestComposition.BuildStore(), output, []);
        using var instances = host.Instances;
        using var cancellation = new CancellationTokenSource();
        var drain = host.DrainAsync(ct: cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => drain);
        host.DrainActivationMailbox();
        Assert.False(condition: host.IsDraining);
    }
    [Fact]
    public async Task FailedDeactivationAndFinalSaveRetainTheRowAndAllowDurableDrainRetry() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(path1: directory.RootPath, path2: "world.key");

        File.WriteAllBytes(keyFile, key.ExportPkcs8PrivateKey());
        var identity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "row"));
        var store = new FailingWrites(inner: PuckStorageTestComposition.BuildStore());
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var backend = new WorldAuthorityBlobStore(store: store, target: target);
        var definition = Fixtures.BuildDocument();

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:33333", Presentation = WorldHostPresentation.None } };
        var published = await backend.PublishDefinitionAsync(identity, definition, TestContext.Current.CancellationToken);

        Assert.True(condition: published.Ok, userMessage: published.Detail);
        var host = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var instances = host.Instances;
        var activation = host.ActivateAsync(identity, TestContext.Current.CancellationToken);

        await PumpAsync(host: host, operation: activation);
        Assert.True(condition: await activation);

        store.Fail = true;
        var deactivation = host.DeactivateAsync(identity, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(testCode: () => PumpAsync(host: host, operation: deactivation));
        Assert.NotNull(@object: host.TryDescribeRow(worldId: "row"));
        var failedDrain = host.DrainAsync(ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(testCode: () => PumpAsync(host: host, operation: failedDrain));
        Assert.True(condition: host.IsDraining);
        Assert.NotNull(@object: host.TryDescribeRow(worldId: "row"));

        store.Fail = false;
        await PumpAsync(host: host, operation: host.DrainAsync(ct: TestContext.Current.CancellationToken));
        Assert.NotNull(value: await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken));
    }

    private static WorldSiloHost Host(string directory, IObjectBlobStore store, BufferedConsoleOutput output, WorldSiloWorldRow[] worlds) {
        var source = new TextCommandSource(new CommandRegistry(modules: []));

        return new(new(worlds, new(Budget: 1), new("directory", JsonElement.Parse("{}")), directory, new("Localhost")), store,
            new(source: () => source, tagging: new SiloConsoleTagging(output: output)), new DirectoryObjectStorageTarget(directory));
    }
    private static async Task PumpAsync(WorldSiloHost host, Task operation, bool step = false) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 20));
        while (!operation.IsCompleted) {
            host.DrainActivationMailbox();
            if (step && !host.IsDraining) { host.Instances.StepInstances(masterDeltaTicks: Fixtures.StepTicks); host.NoteMasterStep(stepTicks: Fixtures.StepTicks); }
            await Task.Delay(1, deadline.Token);
        }
        await operation;
        host.DrainActivationMailbox();
    }

    private sealed class FailingWrites(IObjectBlobStore inner) : IObjectBlobStore {
        internal bool Fail { get; set; }

        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(address: address, cancellationToken: cancellationToken, target: target);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken: cancellationToken, keyPrefix: keyPrefix, objectId: objectId, target: target);
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) =>
            (Fail ? ValueTask.FromException<ObjectBlobWriteResult>(exception: new IOException(message: "injected storage failure")) : inner.WriteAsync(address: address, cancellationToken: cancellationToken, content: content, ifMatchVersion: ifMatchVersion, mode: mode, target: target));
    }
}
