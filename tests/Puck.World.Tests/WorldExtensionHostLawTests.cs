using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldExtensionHostLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static WorldExtensionHost Host(WorldServer server, WorldExternalOperationJournal journal, Provider provider,
        TimeProvider? clock = null, WorldExtensionHostOptions? options = null) => new(
            server,
            journal,
            "authority-lineage",
            [new(
                    new(
                        Description: "Delete the associated resource",
                        InputSchema: "{\"type\":\"object\"}",
                        Name: "delete"
                    ),
                    provider,
                    (id, input) => new(
                        id,
                        "delete",
                        provider.Identity,
                        input
                    ),
                    (_, _) => TimeSpan.FromSeconds(seconds: 10)
                )],
            () => server.CaptureExternalOperationCause(hostRow: WorldAuthorityHostRowCheckpoint.Empty),
            options,
            clock
        );
    private static WorldExternalOperationJournal Journal() => new(
        new FakeObjectBlobStore(),
        new DirectoryObjectStorageTarget("unused"),
        new(
            Key: "operations",
            ObjectId: Guid.Empty
        ),
        32,
        8388608,
        8
    );

    [Fact]
    public async Task DiscoveryAndInvocationAreScoped_AndDuplicateKeysAreCallerBound() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        var journal = Journal();
        await using var host = Host(
            world.Server,
            journal,
            provider
        );
        using var first = host.CreateClient(
            WorldPrincipal.Addon(name: "first"),
            ["delete"],
            []
        );
        using var second = host.CreateClient(
            WorldPrincipal.Addon(name: "second"),
            [],
            []
        );

        Assert.Equal(
            "delete",
            Assert.Single(collection: first.Discover()).Name
        );
        Assert.Empty(collection: second.Discover());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(testCode: () => second.InvokeAsync(
            "delete",
            "death/1",
            "{}",
            Cancel
        ).AsTask());
        Assert.Empty(collection: await journal.ReadAsync(cancellationToken: Cancel));
        var handle = await first.InvokeAsync(
            "delete",
            "death/1",
            "{}",
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Pending,
            (await handle.ReadAsync(cancellationToken: Cancel))!.Status
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 0
        );
        Assert.Equal(
            handle.Id,
            (await first.InvokeAsync(
                "delete",
                "death/1",
                "{}",
                Cancel
            )).Id
        );
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => first.InvokeAsync(
            "delete",
            "death/1",
            "different",
            Cancel
        ).AsTask());
        await host.RunOnceAsync(cancellationToken: Cancel);
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await handle.ReadAsync(cancellationToken: Cancel))!.Status
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
        using var third = host.CreateClient(
            WorldPrincipal.Addon(name: "third"),
            ["delete"],
            []
        );

        Assert.NotEqual(
            handle.Id,
            third.GetOperation(
                operation: "delete",
                requestKey: "death/1"
            ).Id
        );
        Assert.Null(@object: await third.GetOperation(
            operation: "delete",
            requestKey: "death/1"
        ).ReadAsync(cancellationToken: Cancel));
    }
    [Fact]
    public async Task InputAndClientBudgetsRefuseBeforeCommit_AndAuthorityIdentitiesCannotBeLaundered() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        var journal = Journal();
        await using var host = Host(
            world.Server,
            journal,
            provider,
            options: WorldExtensionHostOptions.Default with { MaximumClients = 1, MaximumInputBytes = 2 }
        );

        Assert.Throws<ArgumentException>(testCode: () => host.CreateClient(
            WorldPrincipal.World,
            ["delete"],
            []
        ));
        Assert.Throws<ArgumentException>(testCode: () => host.CreateClient(
            WorldPrincipal.Group(id: "admins"),
            ["delete"],
            []
        ));
        using var caller = host.CreateClient(
            WorldPrincipal.Addon(name: "actor"),
            ["delete"],
            []
        );

        Assert.Throws<InvalidOperationException>(testCode: () => host.CreateClient(
            WorldPrincipal.Addon(name: "other"),
            ["delete"],
            []
        ));
        await Assert.ThrowsAsync<ArgumentException>(testCode: () => caller.InvokeAsync(
            "delete",
            "death/5",
            "long",
            Cancel
        ).AsTask());
        Assert.Empty(collection: await journal.ReadAsync(cancellationToken: Cancel));
        caller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => caller.InvokeAsync(
            "delete",
            "death/5",
            "{}",
            Cancel
        ).AsTask());
        using var replacement = host.CreateClient(
            WorldPrincipal.Addon(name: "actor"),
            [],
            []
        );

        Assert.Empty(collection: replacement.Discover());
        Assert.Throws<ObjectDisposedException>(testCode: () => caller.Discover());
    }
    [Fact]
    public async Task OwnedWorkerDispatchesWithoutHostAuthoredPollingLoop() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        await using var host = Host(
            world.Server,
            Journal(),
            provider
        );
        using var caller = host.CreateClient(
            WorldPrincipal.Addon(name: "actor"),
            ["delete"],
            []
        );

        await caller.InvokeAsync(
            "delete",
            "death/4",
            "{}",
            Cancel
        );
        host.Start();
        await provider.Executed.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 5),
            Cancel
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
    }
    [Fact]
    public async Task RestartRecoversPendingAndPollsRunningWithoutResending_AndHonorsDelay() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider { ExecuteStatus = WorldExternalOperationStatus.Running };
        var journal = Journal();
        var clock = new Clock();

        await using (var first = Host(
            world.Server,
            journal,
            provider,
            clock
        )) {
            using var caller = first.CreateClient(
                WorldPrincipal.Addon(name: "actor"),
                ["delete"],
                []
            );

            await caller.InvokeAsync(
                "delete",
                "death/2",
                "{}",
                Cancel
            );
        }
        await using (var second = Host(
            world.Server,
            journal,
            provider,
            clock
        )) {
            using var caller = second.CreateClient(
                WorldPrincipal.Addon(name: "actor"),
                ["delete"],
                []
            );

            await second.RunOnceAsync(cancellationToken: Cancel);
            Assert.Equal(
                actual: provider.Executions,
                expected: 1
            );
            Assert.Equal(
                WorldExternalOperationStatus.Running,
                (await caller.GetOperation(
                    operation: "delete",
                    requestKey: "death/2"
                ).ReadAsync(cancellationToken: Cancel))!.Status
            );
        }
        await using var third = Host(
            world.Server,
            journal,
            provider,
            clock
        );
        using var restored = third.CreateClient(
            WorldPrincipal.Addon(name: "actor"),
            ["delete"],
            []
        );

        await third.RunOnceAsync(cancellationToken: Cancel);
        Assert.Equal(
            actual: provider.Reconciliations,
            expected: 0
        );
        clock.Now += TimeSpan.FromSeconds(seconds: 9);
        await third.RunOnceAsync(cancellationToken: Cancel);
        Assert.Equal(
            actual: provider.Reconciliations,
            expected: 0
        );
        clock.Now += TimeSpan.FromSeconds(seconds: 1);
        await third.RunOnceAsync(cancellationToken: Cancel);
        Assert.Equal(
            actual: provider.Reconciliations,
            expected: 1
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restored.GetOperation(
                operation: "delete",
                requestKey: "death/2"
            ).ReadAsync(cancellationToken: Cancel))!.Status
        );
    }
    [Fact]
    public async Task RevocationAndReplaySuppressServiceAndRetainedStorageCapabilities() {
        using var world = Fixtures.FreshServer();
        var provider = new Provider();
        await using var host = Host(
            world.Server,
            Journal(),
            provider
        );
        using var storage = new ObjectBlobNamespace(
            new FakeObjectBlobStore(),
            new DirectoryObjectStorageTarget("unused"),
            Guid.NewGuid(),
            "actor",
            64,
            writable: true
        );
        using var caller = host.CreateClient(
            WorldPrincipal.Addon(name: "actor"),
            ["delete"],
            [],
            storage: storage
        );
        var retained = caller.Storage!;
        var handle = await caller.InvokeAsync(
            "delete",
            "death/3",
            "{}",
            Cancel
        );

        world.Server.SuppressRecordedExtensions();
        await host.RunOnceAsync(cancellationToken: Cancel);
        Assert.Equal(
            actual: provider.Executions,
            expected: 0
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => handle.ReadAsync(cancellationToken: Cancel).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => retained.WriteAsync(
            "memo",
            "x"u8.ToArray(),
            cancellationToken: Cancel
        ).AsTask());
        Assert.Throws<ObjectDisposedException>(testCode: () => caller.Discover());
    }
    [Fact]
    public async Task WorkerLimitsConcurrentCallsAndRetainsUnselectedPendingRequests() {
        using var world = Fixtures.FreshServer();
        var provider = new BlockingProvider();
        await using var host = new WorldExtensionHost(
            world.Server,
            Journal(),
            "bounded-lineage",
            [new(
                    new(
                        Description: "Delete",
                        InputSchema: "{}",
                        Name: "delete"
                    ),
                    provider,
                    (id, input) => new(
                        id,
                        "delete",
                        provider.Identity,
                        input
                    )
                )],
            () => world.Server.CaptureExternalOperationCause(hostRow: WorldAuthorityHostRowCheckpoint.Empty),
            WorldExtensionHostOptions.Default with { MaximumConcurrentOperations = 2 }
        );
        using var caller = host.CreateClient(
            WorldPrincipal.Addon(name: "actor"),
            ["delete"],
            []
        );

        for (var i = 0; (i < 3); i++) { await caller.InvokeAsync(
            "delete",
            $"death/{i}",
            "{}",
            Cancel
        ); }
        var pass = host.RunOnceAsync(cancellationToken: Cancel);

        try {
            await provider.TwoEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 5),
                Cancel
            );
            Assert.Equal(
                actual: provider.Executions,
                expected: 2
            );
            Assert.Equal(
                WorldExternalOperationStatus.Pending,
                (await caller.GetOperation(
                    operation: "delete",
                    requestKey: "death/2"
                ).ReadAsync(cancellationToken: Cancel))!.Status
            );
        } finally { provider.Release.TrySetResult(); await pass; }
        await host.RunOnceAsync(cancellationToken: Cancel);
        Assert.Equal(
            actual: provider.Executions,
            expected: 3
        );
    }

    private sealed class Provider : IWorldExternalOperationProvider {
        public string Identity => "test-resource/incarnation/1";

        public int Executions, Reconciliations;

        public WorldExternalOperationStatus ExecuteStatus = WorldExternalOperationStatus.Succeeded;
        public TaskCompletionSource Executed { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Executions++; Executed.TrySetResult();
            return ValueTask.FromResult(result: new WorldExternalOperationResult(
                Result: "saved-receipt",
                Status: ExecuteStatus
            ));
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation, WorldExternalOperationResult previous, CancellationToken cancellationToken) {
            Assert.Equal(
                "saved-receipt",
                previous.Result
            );
            Reconciliations++;
            return ValueTask.FromResult(result: new WorldExternalOperationResult(
                Result: "complete",
                Status: WorldExternalOperationStatus.Succeeded
            ));
        }
    }
    private sealed class Clock : TimeProvider {
        public DateTimeOffset Now = new(
            day: 1,
            hour: 0,
            minute: 0,
            month: 1,
            offset: TimeSpan.Zero,
            second: 0,
            year: 2026
        );

        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class BlockingProvider : IWorldExternalOperationProvider {
        public string Identity => "bounded-resource";

        public int Executions;

        public TaskCompletionSource TwoEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            if (Interlocked.Increment(location: ref Executions) == 2) { TwoEntered.TrySetResult(); }
            await Release.Task.WaitAsync(cancellationToken: cancellationToken);
            return new(
                Result: "complete",
                Status: WorldExternalOperationStatus.Succeeded
            );
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation,
            WorldExternalOperationResult previous, CancellationToken cancellationToken) => throw new InvalidOperationException(message: "No reconciliation expected.");
    }
}
