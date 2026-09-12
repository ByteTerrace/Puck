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
    [Fact]
    public async Task DuplicateActivationKeepsItsFenceAndReplacementRejectsTheOldWriter() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(directory.RootPath, "world.key");
        File.WriteAllBytes(keyFile, key.ExportPkcs8PrivateKey());
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("row"));
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(store, new DirectoryObjectStorageTarget(directory.RootPath));
        var definition = Fixtures.BuildDocument() with {
            HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None }
        };
        Assert.True((await backend.PublishDefinitionAsync(identity, definition, TestContext.Current.CancellationToken)).Ok);
        var original = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var originalInstances = original.Instances;
        await PumpAsync(original, original.ActivateAsync(identity, TestContext.Current.CancellationToken));
        var first = (await backend.LoadRootAsync(identity, TestContext.Current.CancellationToken))!.Value;
        Assert.NotNull(await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken));
        await PumpAsync(original, original.ActivateAsync(identity, TestContext.Current.CancellationToken));
        Assert.Equal(first.Root.Epoch, (await backend.LoadRootAsync(identity, TestContext.Current.CancellationToken))!.Value.Root.Epoch);

        var replacement = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var replacementInstances = replacement.Instances;
        await PumpAsync(replacement, replacement.ActivateAsync(identity, TestContext.Current.CancellationToken));
        var current = (await backend.LoadRootAsync(identity, TestContext.Current.CancellationToken))!.Value;
        Assert.True(current.Root.Epoch > first.Root.Epoch);
        var stale = original.CheckpointNowAsync(identity, TestContext.Current.CancellationToken);
        await PumpAsync(original, stale);
        Assert.False(await stale);
        Assert.Equal(current, (await backend.LoadRootAsync(identity, TestContext.Current.CancellationToken))!.Value);
        var live = replacement.CheckpointNowAsync(identity, TestContext.Current.CancellationToken);
        await PumpAsync(replacement, live);
        Assert.True(await live);
        await PumpAsync(replacement, replacement.DrainAsync(TestContext.Current.CancellationToken));
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ReplacementActivationUsesPublishedNetworkBindingAfterCheckpointRecovery(bool listen) {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(path1: directory.RootPath, path2: "world.key");

        File.WriteAllBytes(keyFile, key.ExportPkcs8PrivateKey());
        var identity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "row"));
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(store: store, target: new DirectoryObjectStorageTarget(directory.RootPath));
        var definition = Fixtures.BuildDocument();

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "old.example:33333", Listen = null, Presentation = WorldHostPresentation.None } };
        Assert.True(condition: (await backend.PublishDefinitionAsync(identity, definition, TestContext.Current.CancellationToken)).Ok);
        var original = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var originalInstances = original.Instances;

        await PumpAsync(original, original.ActivateAsync(identity, TestContext.Current.CancellationToken));
        await PumpAsync(original, original.DrainAsync(ct: TestContext.Current.CancellationToken));
        string? endpoint = null;

        if (listen) {
            using var reservation = new System.Net.Sockets.Socket(addressFamily: System.Net.Sockets.AddressFamily.InterNetwork, protocolType: System.Net.Sockets.ProtocolType.Udp, socketType: System.Net.Sockets.SocketType.Dgram);

            reservation.Bind(localEP: new System.Net.IPEndPoint(address: System.Net.IPAddress.Loopback, port: 0));
            endpoint = reservation.LocalEndPoint!.ToString();
        }
        var published = definition with { HostRaw = definition.Host with { Authority = "play.puck.byteterrace.com:7825", Listen = endpoint } };

        Assert.True(condition: (await backend.PublishDefinitionAsync(identity, published, TestContext.Current.CancellationToken)).Ok);
        var replacement = Host(directory.RootPath, store, output, [new(identity.Owner, identity.World, new(KeyFile: keyFile))]);
        using var replacementInstances = replacement.Instances;

        await PumpAsync(replacement, replacement.ActivateAsync(identity, TestContext.Current.CancellationToken));
        Assert.True(condition: replacement.Instances.TryGet(identity.World.Value, out var row));
        Assert.Equal(definition.Host.Authority, row!.Server.Definition.Host.Authority);
        Assert.Equal(published.Host.Authority, row.Federation.Subject);
        Assert.Equal(endpoint, row.Door!.ListenEndpoint);
        await PumpAsync(replacement, replacement.ReloadAsync(identity, TestContext.Current.CancellationToken), step: true);
        Assert.Equal(published.Host.Authority, row.Server.Definition.Host.Authority);
        Assert.Equal(endpoint, row.Server.Definition.Host.Listen);
        Assert.NotNull(value: await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken));
        await PumpAsync(replacement, replacement.DrainAsync(ct: TestContext.Current.CancellationToken));
    }
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

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Presentation = WorldHostPresentation.None } };
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

        await PublishAsync(host, identity, changed);
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

        var refused = changed with { HostRaw = changed.Host with { Authority = "another-host:7825" } };

        await PublishAsync(host, identity, refused);
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => PumpAsync(host, host.ReloadAsync(identity, TestContext.Current.CancellationToken), step: true));
        Assert.Equal(saved.Ordinal, (await backend.LoadLatestAsync(identity, TestContext.Current.CancellationToken))!.Value.Ordinal);
        // Simulate losing the release-marker write after the rebuilt checkpoint became durable.
        await PublishAsync(host, identity, changed);
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

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Presentation = WorldHostPresentation.None } };
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
    private static async Task PublishAsync(WorldSiloHost host, WorldAuthorityIdentity identity, WorldDefinition definition) {
        var publication = host.PublishDefinitionAsync(identity, definition, TestContext.Current.CancellationToken);
        await PumpAsync(host, publication);
        var result = await publication;
        Assert.True(result.Ok, result.Detail);
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
