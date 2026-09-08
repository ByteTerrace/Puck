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
        var routing = new SiloConsoleRouting(() => source, new SiloConsoleTagging(output));
        using var row = routing.Register("row");
        using var attached = routing.CreateControlSession("row");
        var pending = attached.ExecuteAsync(new(1, "exec", "help", 1000), TestContext.Current.CancellationToken);
        routing.Unregister("row");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        using var replacement = routing.Register("row");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => attached.ExecuteAsync(new(2, "exec", "help", 1000), TestContext.Current.CancellationToken));
        using var fresh = routing.CreateControlSession("row");
        var current = fresh.ExecuteAsync(new(1, "exec", "help", 1000), TestContext.Current.CancellationToken);
        source.Collect();
        Assert.NotEqual("unknown", (await current).Status);
        routing.Unregister("row");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiringARowRefusesQueuedWorkAndReadmissionDoesNotReviveItsSession(bool held) {
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(() => source, new SiloConsoleTagging(output));
        using var retired = routing.Register("row");
        routing.SetDefault("row");
        retired.HoldWhile(() => held);
        var invoked = false;
        var pending = retired.InvokeAsync(() => invoked = true, TestContext.Current.CancellationToken);

        routing.Unregister("row");
        Assert.True(pending.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending);
        Assert.Null(routing.DefaultWorldId);
        Assert.False(routing.TryGetSession("row", out _));
        Assert.False(routing.TryResolveWorldId(retired.Slot, out _));
        Assert.False(routing.TryEnqueue("row", "world.status"));
        Assert.Throws<ObjectDisposedException>(() => retired.Enqueue("world.status"));

        using var admitted = routing.Register("row");
        var current = admitted.InvokeAsync(() => 42, TestContext.Current.CancellationToken);
        source.Collect();
        Assert.Equal(42, await current);
        Assert.False(invoked);
        routing.Unregister("row");
    }

    [Fact]
    public void StdinAdmissionTreatsAClosedSessionAsARowRefusal() {
        using var output = new BufferedConsoleOutput();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(() => source, new SiloConsoleTagging(output));
        using var session = routing.Register("row");
        Assert.True(routing.TryEnqueue("row", "# admitted"));
        // A reader may hold a route obtained just before retirement closes that session.
        session.Dispose();
        Assert.False(routing.TryEnqueue("row", "# retired"));
        routing.Unregister("row");
    }
}
