using Puck.Commands;
using Puck.Launcher;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

public sealed class SiloConsoleRoutingLawTests {
    [Fact]
    public async Task HostControlSessionsRetireWithTheirFixedRow() {
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        using var row = routing.Register(worldId: "row");
        using var attached = routing.CreateControlSession("row");
        var pending = attached.ExecuteAsync(
            new(
                Command: "help",
                Id: 1,
                Operation: "exec",
                TimeoutMilliseconds: 1000
            ),
            TestContext.Current.CancellationToken
        );

        routing.Unregister(worldId: "row");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await pending);
        using var replacement = routing.Register(worldId: "row");

        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => attached.ExecuteAsync(
            new(
                Command: "help",
                Id: 2,
                Operation: "exec",
                TimeoutMilliseconds: 1000
            ),
            TestContext.Current.CancellationToken
        ));
        using var fresh = routing.CreateControlSession("row");
        var current = fresh.ExecuteAsync(
            new(
                Command: "help",
                Id: 1,
                Operation: "exec",
                TimeoutMilliseconds: 1000
            ),
            TestContext.Current.CancellationToken
        );

        source.Collect();
        Assert.NotEqual(
            "unknown",
            (await current).Status
        );
        routing.Unregister(worldId: "row");
    }
    [Fact]
    public async Task RetirementCancelsHostOperationsAndClosesAdmissionExactlyOnce() {
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(new CommandRegistry([]));
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        using var row = routing.Register(worldId: "row");
        var closed = 0;
        using var control = routing.CreateControlSession(
            "row",
            Principal.Peer(
                generation: 1,
                index: 8
            ),
            _ => false,
            () => closed++
        );
        var invoked = false;
        var pending = routing.InvokeAsync(
            "row",
            () => invoked = true,
            TestContext.Current.CancellationToken
        );

        routing.Unregister(worldId: "row");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await pending);
        control.Dispose();
        source.Collect();
        Assert.False(condition: invoked);
        Assert.Equal(
            actual: closed,
            expected: 1
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task RetiringARowRefusesQueuedWorkAndReadmissionDoesNotReviveItsSession(bool held) {
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        using var retired = routing.Register(worldId: "row");

        routing.SetDefault(worldId: "row");
        retired.HoldWhile(hold: () => held);
        var invoked = false;
        var pending = retired.InvokeAsync(
            () => invoked = true,
            TestContext.Current.CancellationToken
        );

        routing.Unregister(worldId: "row");
        Assert.True(condition: pending.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: async () => await pending);
        Assert.Null(@object: routing.DefaultWorldId);
        Assert.False(condition: routing.TryGetSession(
            session: out _,
            worldId: "row"
        ));
        Assert.False(condition: routing.TryResolveWorldId(
            slot: retired.Slot,
            worldId: out _
        ));
        Assert.False(condition: routing.TryEnqueue(
            line: "world.status",
            worldId: "row"
        ));
        Assert.Throws<ObjectDisposedException>(testCode: () => retired.Enqueue(line: "world.status"));

        using var admitted = routing.Register(worldId: "row");
        var current = admitted.InvokeAsync(
            () => 42,
            TestContext.Current.CancellationToken
        );

        source.Collect();
        Assert.Equal(
            42,
            await current
        );
        Assert.False(condition: invoked);
        routing.Unregister(worldId: "row");
    }
    [Fact]
    public void StdinAdmissionTreatsAClosedSessionAsARowRefusal() {
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        using var session = routing.Register(worldId: "row");

        Assert.True(condition: routing.TryEnqueue(
            line: "# admitted",
            worldId: "row"
        ));
        // A reader may hold a route obtained just before retirement closes that session.
        session.Dispose();
        Assert.False(condition: routing.TryEnqueue(
            line: "# retired",
            worldId: "row"
        ));
        routing.Unregister(worldId: "row");
    }
}
