
using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the drain itself: the source-wide <see cref="TextCommandSource.HoldGate"/>, how it composes with
/// a session's own hold and with the read-after-write barrier, and the thread-safety the type's own remarks
/// advertise — background producers enqueueing while the frame thread collects.</summary>
public sealed class TextCommandSourceTests {
    private static TextCommandSource Source(List<string> submitted, out TextCommandSession session) {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var source = new TextCommandSource(registry: registry);

        session = source.CreateSession(
            onResult: (line, _) => submitted.Add(item: line),
            principal: Principal.Console
        );

        return source;
    }

    [Fact]
    public async Task AFailedHostScopeCompletesTheOperationAndDoesNotStrandOtherSessions() {
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        using var scoped = source.CreateSession(
            principal: Principal.Console,
            scope: () => throw new IOException(message: "scope unavailable")
        );
        using var other = source.CreateSession(principal: Principal.Console);
        var failed = scoped.InvokeAsync(
            () => 1,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var ready = other.InvokeAsync(
            () => 42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        source.Collect();
        await Assert.ThrowsAsync<IOException>(testCode: async () => await failed);
        Assert.Equal(
            42,
            await ready
        );
        scoped.Dispose();
        var closed = scoped.InvokeAsync(
            () => 1,
            cancellationToken: TestContext.Current.CancellationToken
        );

        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: async () => await closed);
    }
    [Fact]
    public void ALineWhoseHandlerArmsTheGateStopsTheDrainAtThatLine() {
        var submitted = new List<string>();
        var held = false;
        var registry = new CommandRegistry(modules: [new ProbeModule(), new GateModule(arm: () => held = true)]);
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSession(
            onResult: (line, _) => submitted.Add(item: line),
            principal: Principal.Console
        );

        source.HoldGate = () => held;
        session.Enqueue(line: "probe before");
        session.Enqueue(line: "step");
        session.Enqueue(line: "probe after");
        source.Collect();

        // This is the whole point of the seam: a `step`/`settle` verb defers the REST of a piped script to a later
        // frame, and the queue's FIFO order survives the pause.
        Assert.Equal(
            actual: submitted,
            expected: ["probe before", "step"]
        );

        held = false;
        source.Collect();

        Assert.Equal(
            actual: submitted,
            expected: ["probe before", "step", "probe after"]
        );
    }
    [Fact]
    public void AnArmedHoldGateDefersEveryQueuedLineUntilItLetsGo() {
        var submitted = new List<string>();
        var source = Source(
            session: out var session,
            submitted: submitted
        );
        var held = true;

        source.HoldGate = () => held;
        session.Enqueue(line: "probe a");
        session.Enqueue(line: "probe b");
        source.Collect();

        // The gate is checked BEFORE the first dequeue, so an armed gate costs the frame nothing and loses nothing.
        Assert.Empty(collection: submitted);

        held = false;
        source.Collect();

        Assert.Equal(
            actual: submitted,
            expected: ["probe a", "probe b"]
        );
    }
    [Fact]
    public async Task BackgroundProducersEnqueueWhileTheFrameThreadCollects() {
        const int LinesPerProducer = 250;
        const int Producers = 4;

        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var source = new TextCommandSource(registry: registry);
        var collected = new List<string>[Producers];
        var sessions = new TextCommandSession[Producers];

        for (var producer = 0; (producer < Producers); producer++) {
            var lines = new List<string>();

            collected[producer] = lines;
            // The result callback runs on the frame thread inside Collect, so a plain list per session is correct
            // here: what is under test is that the QUEUE is safe to write from another thread.
            sessions[producer] = source.CreateSession(
                onResult: (line, _) => lines.Add(item: line),
                principal: Principal.Console
            );
        }

        // Cancelled when the test body leaves, so a failed collection releases every producer rather than stranding it.
        using var producersStop = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        var producers = new Task[Producers];

        for (var producer = 0; (producer < Producers); producer++) {
            var index = producer;

            producers[producer] = Task.Factory.StartNew(
                action: () => {
                    for (var line = 0; (line < LinesPerProducer); line++) {
                        producersStop.Token.ThrowIfCancellationRequested();
                        sessions[index].Enqueue(line: $"probe {index} {line}");
                    }
                },
                cancellationToken: CancellationToken.None,
                creationOptions: TaskCreationOptions.LongRunning,
                scheduler: TaskScheduler.Default
            );
        }

        try {
            while (!producers.All(predicate: static producer => producer.IsCompleted)) {
                producersStop.Token.ThrowIfCancellationRequested();
                source.Collect();
                Thread.Yield();
            }
            await Task.WhenAll(producers).WaitAsync(cancellationToken: producersStop.Token);
            source.Collect();
        } finally {
            producersStop.Cancel();
        }

        for (var producer = 0; (producer < Producers); producer++) {
            // Exactly once, and in the order that producer wrote them: a session's queue is its own FIFO, and the
            // rotation that lets a blocked session step aside moves the whole stream rather than reordering it.
            Assert.Equal(
                actual: collected[producer],
                expected: [.. Enumerable.Range(
                        count: LinesPerProducer,
                        start: 0
                    ).Select(selector: line => $"probe {producer} {line}")]
            );
        }
    }
    [Fact]
    public async Task CancellationAfterExecutionStartsDoesNotMisreportAnAppliedOperation() {
        var source = Source(
            session: out var session,
            submitted: []
        );
        using var cancellation = new CancellationTokenSource();
        var operation = session.InvokeAsync(
            () => {
                cancellation.Cancel();
                return 42;
            },
            cancellation.Token
        );

        source.Collect();
        Assert.Equal(
            42,
            await operation
        );
    }
    [Fact]
    public async Task HostOperationWaitsForMutationAndSessionHoldWhileAnotherSessionRuns() {
        var registry = new CommandRegistry(modules: [new ProbeModule(), new DeferredModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry);
        using var session = source.CreateSession(
            principal: Principal.Console,
            simulationSink: router.ConsoleTextSink
        );
        using var other = source.CreateSession(principal: Principal.Console);

        session.Enqueue(line: "sim.defer payload");
        var ran = false;
        var operation = session.InvokeAsync(
            () => ran = true,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var independent = other.InvokeAsync(
            () => 42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        source.Collect();
        Assert.False(condition: ran);
        Assert.Equal(
            42,
            await independent
        );

        var held = true;

        session.HoldWhile(hold: () => held);
        var snapshot = router.SnapshotForTick(
            tick: 1UL,
            windowEndTick: ulong.MaxValue
        );

        registry.ApplySnapshot(snapshot: in snapshot);
        source.Collect();
        Assert.False(condition: ran);
        held = false;
        source.Collect();
        Assert.True(condition: await operation);
    }
    [InlineData("identity plain")]
    [InlineData("identity \"quoted value\"")]
    [Theory]
    public void ImmediateHandlersReceiveTheOriginatingSessionOnBothTextPaths(string line) {
        TextCommandSession? observed = null;
        var registry = new CommandRegistry(modules: [new IdentityModule(observe: context => observed = context.TextSession)]);
        var source = new TextCommandSource(registry);
        using var session = source.CreateSession(principal: Principal.Console);

        session.Enqueue(line: line);
        source.Collect();
        Assert.Same(
            actual: observed,
            expected: session
        );
        _ = registry.Submit(line: line);
        Assert.Null(@object: observed);
    }
    [Fact]
    public async Task OperationFailureDoesNotStopTheDrainAndDisposalRefusesQueuedWork() {
        var source = Source(
            session: out var session,
            submitted: []
        );
        var failed = session.InvokeAsync<int>(
            () => throw new IOException(message: "operation failed"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var later = session.InvokeAsync(
            () => 42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        source.Collect();
        await Assert.ThrowsAsync<IOException>(testCode: async () => await failed);
        Assert.Equal(
            42,
            await later
        );

        var abandoned = session.InvokeAsync<int>(
            () => throw new InvalidOperationException(message: "must not execute"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        session.Dispose();
        source.Collect();
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: async () => await abandoned);
        Assert.Throws<ObjectDisposedException>(testCode: () => session.Enqueue(line: "probe closed"));
    }
    [Fact]
    public async Task QueuedCancellationSkipsTheDelegateAndDoesNotHoldLaterWork() {
        var source = Source(
            session: out var session,
            submitted: []
        );
        using var cancellation = new CancellationTokenSource();
        var ran = false;
        var cancelled = session.InvokeAsync(
            () => ran = true,
            cancellation.Token
        );
        var later = session.InvokeAsync(
            () => 42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        cancellation.Cancel();
        source.Collect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await cancelled);
        Assert.False(condition: ran);
        Assert.Equal(
            42,
            await later
        );
    }
    [Fact]
    public void TheGateAndTheReadAfterWriteBarrierBothHaveToLetGo() {
        var submitted = new List<string>();
        var registry = new CommandRegistry(modules: [new ProbeModule(), new DeferredModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSession(
            onResult: (line, _) => submitted.Add(item: line),
            principal: Principal.Console,
            simulationSink: router.ConsoleTextSink
        );
        var held = false;

        source.HoldGate = () => held;
        session.Enqueue(line: "sim.defer payload");
        session.Enqueue(line: "probe after");
        source.Collect();

        // The deferred line went out; the immediate read-back behind it waits for the tick that applies it.
        Assert.Equal(
            actual: submitted,
            expected: ["sim.defer payload"]
        );

        held = true;

        var snapshot = router.SnapshotForTick(
            tick: 1UL,
            windowEndTick: ulong.MaxValue
        );

        registry.ApplySnapshot(snapshot: in snapshot);
        source.Collect();

        // The barrier has released, but the gate has not: the two holds are independent and BOTH must let go.
        Assert.Equal(
            actual: submitted,
            expected: ["sim.defer payload"]
        );

        held = false;
        source.Collect();

        Assert.Equal(
            actual: submitted,
            expected: ["sim.defer payload", "probe after"]
        );
    }
    [Fact]
    public void TheSourceGateHoldsEverySessionWhileASessionsOwnHoldHoldsOnlyIt() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var source = new TextCommandSource(registry: registry);
        var first = new List<string>();
        var second = new List<string>();
        var firstHeld = true;
        var sessionOne = source.CreateSession(
            hold: () => firstHeld,
            onResult: (line, _) => first.Add(item: line),
            principal: Principal.Console
        );
        var sessionTwo = source.CreateSession(
            onResult: (line, _) => second.Add(item: line),
            principal: Principal.Console
        );

        sessionOne.Enqueue(line: "probe one");
        sessionTwo.Enqueue(line: "probe two");
        source.Collect();

        // A session's own hold rotates only that session; the other seat keeps draining.
        Assert.Empty(collection: first);
        Assert.Equal(
            actual: second,
            expected: ["probe two"]
        );

        var held = true;

        source.HoldGate = () => held;
        firstHeld = false;
        sessionTwo.Enqueue(line: "probe three");
        source.Collect();

        // The source-wide gate is the other axis: it stops the drain for everyone, including the session whose own
        // hold has just let go.
        Assert.Empty(collection: first);
        Assert.Equal(
            actual: second,
            expected: ["probe two"]
        );

        held = false;
        source.Collect();

        Assert.Equal(
            actual: first,
            expected: ["probe one"]
        );
        Assert.Equal(
            actual: second,
            expected: ["probe two", "probe three"]
        );
    }

    private sealed class IdentityModule(Action<CommandContext> observe) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "identity",
                description: "Records the issuing session.",
                bindability: CommandBindability.Unbindable,
                handler: (context, _) => {
                    observe(context);
                    return CommandResult.None;
                }
            );
        }
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class DeferredModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sim.defer",
                description: "Folds into the tick's snapshot instead of running inline.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class GateModule(Action arm) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "step",
                description: "Arms the source's hold gate from inside the drain.",
                valueKind: CommandValueKind.Digital,
                handler: _ => {
                    arm();

                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "probe",
                description: "Accepts any trailing tokens and does nothing.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Unbindable
            );
        }
    }
}
