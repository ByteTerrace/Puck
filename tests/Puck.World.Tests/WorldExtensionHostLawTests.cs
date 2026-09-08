using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldExtensionHostLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiscoveryAndInvocationAreScoped_AndDuplicateKeysAreCallerBound() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        var journal = Journal();
        await using var host = Host(world.Server, journal, provider);
        using var first = host.CreateClient(WorldPrincipal.Addon("first"), ["delete"], []);
        using var second = host.CreateClient(WorldPrincipal.Addon("second"), [], []);
        Assert.Equal("delete", Assert.Single(first.Discover()).Name);
        Assert.Empty(second.Discover());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => second.InvokeAsync("delete", "death/1", "{}", Cancel).AsTask());
        Assert.Empty(await journal.ReadAsync(Cancel));
        var handle = await first.InvokeAsync("delete", "death/1", "{}", Cancel);
        Assert.Equal(WorldExternalOperationStatus.Pending, (await handle.ReadAsync(Cancel))!.Status);
        Assert.Equal(0, provider.Executions);
        Assert.Equal(handle.Id, (await first.InvokeAsync("delete", "death/1", "{}", Cancel)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.InvokeAsync("delete", "death/1", "different", Cancel).AsTask());
        await host.RunOnceAsync(Cancel);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await handle.ReadAsync(Cancel))!.Status);
        Assert.Equal(1, provider.Executions);
        using var third = host.CreateClient(WorldPrincipal.Addon("third"), ["delete"], []);
        Assert.NotEqual(handle.Id, third.GetOperation("delete", "death/1").Id);
        Assert.Null(await third.GetOperation("delete", "death/1").ReadAsync(Cancel));
    }

    [Fact]
    public async Task RestartRecoversPendingAndPollsRunningWithoutResending_AndHonorsDelay() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider { ExecuteStatus = WorldExternalOperationStatus.Running };
        var journal = Journal();
        var clock = new Clock();
        await using (var first = Host(world.Server, journal, provider, clock)) {
            using var caller = first.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], []);
            await caller.InvokeAsync("delete", "death/2", "{}", Cancel);
        }
        await using (var second = Host(world.Server, journal, provider, clock)) {
            using var caller = second.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], []);
            await second.RunOnceAsync(Cancel);
            Assert.Equal(1, provider.Executions);
            Assert.Equal(WorldExternalOperationStatus.Running, (await caller.GetOperation("delete", "death/2").ReadAsync(Cancel))!.Status);
        }
        await using var third = Host(world.Server, journal, provider, clock);
        using var restored = third.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], []);
        await third.RunOnceAsync(Cancel);
        Assert.Equal(0, provider.Reconciliations);
        clock.Now += TimeSpan.FromSeconds(9);
        await third.RunOnceAsync(Cancel);
        Assert.Equal(0, provider.Reconciliations);
        clock.Now += TimeSpan.FromSeconds(1);
        await third.RunOnceAsync(Cancel);
        Assert.Equal(1, provider.Reconciliations);
        Assert.Equal(1, provider.Executions);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restored.GetOperation("delete", "death/2").ReadAsync(Cancel))!.Status);
    }

    [Fact]
    public async Task RevocationAndReplaySuppressServiceAndRetainedStorageCapabilities() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        await using var host = Host(world.Server, Journal(), provider);
        using var storage = new ObjectBlobNamespace(new FakeObjectBlobStore(), new DirectoryObjectStorageTarget("unused"), Guid.NewGuid(), "actor", 64, writable: true);
        using var caller = host.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], [], storage: storage);
        var retained = caller.Storage!;
        var handle = await caller.InvokeAsync("delete", "death/3", "{}", Cancel);
        world.Server.SuppressRecordedExtensions();
        await host.RunOnceAsync(Cancel);
        Assert.Equal(0, provider.Executions);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.ReadAsync(Cancel).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.WriteAsync("memo", "x"u8.ToArray(), cancellationToken: Cancel).AsTask());
        Assert.Throws<ObjectDisposedException>(() => caller.Discover());
    }

    [Fact]
    public async Task OwnedWorkerDispatchesWithoutHostAuthoredPollingLoop() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        await using var host = Host(world.Server, Journal(), provider);
        using var caller = host.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], []);
        await caller.InvokeAsync("delete", "death/4", "{}", Cancel);
        host.Start();
        await provider.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
        Assert.Equal(1, provider.Executions);
    }

    [Fact]
    public async Task WorkerLimitsConcurrentCallsAndRetainsUnselectedPendingRequests() {
        using var world = Fixtures.FreshServer();
        var provider = new BlockingProvider();
        await using var host = new WorldExtensionHost(world.Server, Journal(), "bounded-lineage",
            [new(new("delete", "Delete", "{}"), provider, (id, input) => new(id, "delete", provider.Identity, input))],
            () => world.Server.CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint.Empty),
            WorldExtensionHostOptions.Default with { MaximumConcurrentOperations = 2 });
        using var caller = host.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], []);
        for (var i = 0; i < 3; i++) { await caller.InvokeAsync("delete", $"death/{i}", "{}", Cancel); }
        var pass = host.RunOnceAsync(Cancel);
        try {
            await provider.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
            Assert.Equal(2, provider.Executions);
            Assert.Equal(WorldExternalOperationStatus.Pending, (await caller.GetOperation("delete", "death/2").ReadAsync(Cancel))!.Status);
        } finally { provider.Release.TrySetResult(); await pass; }
        await host.RunOnceAsync(Cancel);
        Assert.Equal(3, provider.Executions);
    }

    [Fact]
    public async Task InputAndClientBudgetsRefuseBeforeCommit_AndAuthorityIdentitiesCannotBeLaundered() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        var journal = Journal();
        await using var host = Host(world.Server, journal, provider, options: WorldExtensionHostOptions.Default with { MaximumClients = 1, MaximumInputBytes = 2 });
        Assert.Throws<ArgumentException>(() => host.CreateClient(WorldPrincipal.World, ["delete"], []));
        Assert.Throws<ArgumentException>(() => host.CreateClient(WorldPrincipal.Group("admins"), ["delete"], []));
        using var caller = host.CreateClient(WorldPrincipal.Addon("actor"), ["delete"], []);
        Assert.Throws<InvalidOperationException>(() => host.CreateClient(WorldPrincipal.Addon("other"), ["delete"], []));
        await Assert.ThrowsAsync<ArgumentException>(() => caller.InvokeAsync("delete", "death/5", "long", Cancel).AsTask());
        Assert.Empty(await journal.ReadAsync(Cancel));
        caller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.InvokeAsync("delete", "death/5", "{}", Cancel).AsTask());
        using var replacement = host.CreateClient(WorldPrincipal.Addon("actor"), [], []);
        Assert.Empty(replacement.Discover());
        Assert.Throws<ObjectDisposedException>(() => caller.Discover());
    }

    private static WorldExternalOperationJournal Journal() => new(new FakeObjectBlobStore(),
        new DirectoryObjectStorageTarget("unused"), new(Guid.Empty, "operations"), 32, 8388608, 8);
    private static WorldExtensionHost Host(WorldServer server, WorldExternalOperationJournal journal, Provider provider,
        TimeProvider? clock = null, WorldExtensionHostOptions? options = null) => new(server, journal, "authority-lineage",
            [new(new("delete", "Delete the associated resource", "{\"type\":\"object\"}"), provider,
                (id, input) => new(id, "delete", provider.Identity, input), (_, _) => TimeSpan.FromSeconds(10))],
            () => server.CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint.Empty), options, clock);

    private sealed class Provider : IWorldExternalOperationProvider {
        public string Identity => "test-resource/incarnation/1";
        public int Executions, Reconciliations;
        public WorldExternalOperationStatus ExecuteStatus = WorldExternalOperationStatus.Succeeded;
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Executions++; Executed.TrySetResult();
            return ValueTask.FromResult(new WorldExternalOperationResult(ExecuteStatus, "saved-receipt"));
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation, WorldExternalOperationResult previous, CancellationToken cancellationToken) {
            Assert.Equal("saved-receipt", previous.Result);
            Reconciliations++;
            return ValueTask.FromResult(new WorldExternalOperationResult(WorldExternalOperationStatus.Succeeded, "complete"));
        }
    }

    private sealed class Clock : TimeProvider {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BlockingProvider : IWorldExternalOperationProvider {
        public string Identity => "bounded-resource";
        public int Executions;
        public TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            if (Interlocked.Increment(ref Executions) == 2) { TwoEntered.TrySetResult(); }
            await Release.Task.WaitAsync(cancellationToken);
            return new(WorldExternalOperationStatus.Succeeded, "complete");
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation,
            WorldExternalOperationResult previous, CancellationToken cancellationToken) => throw new InvalidOperationException("No reconciliation expected.");
    }
}
