using System.Diagnostics;

using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the drain itself: the source-wide <see cref="TextCommandSource.HoldGate"/>, how it composes with
/// a session's own hold and with the read-after-write barrier, and the thread-safety the type's own remarks
/// advertise — background producers enqueueing while the frame thread collects.</summary>
public sealed class TextCommandSourceTests {
    [Fact]
    public void AnArmedHoldGateDefersEveryQueuedLineUntilItLetsGo() {
        var submitted = new List<string>();
        var source = Source(session: out var session, submitted: submitted);
        var held = true;

        source.HoldGate = () => held;
        session.Enqueue(line: "probe a");
        session.Enqueue(line: "probe b");
        source.Collect();

        // The gate is checked BEFORE the first dequeue, so an armed gate costs the frame nothing and loses nothing.
        Assert.Empty(collection: submitted);

        held = false;
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["probe a", "probe b"]);
    }
    [Fact]
    public void ALineWhoseHandlerArmsTheGateStopsTheDrainAtThatLine() {
        var submitted = new List<string>();
        var held = false;
        var registry = new CommandRegistry(modules: [new ProbeModule(), new GateModule(arm: () => held = true)]);
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSession(
            onResult: (line, _) => submitted.Add(item: line),
            principal: CommandPrincipal.Console
        );

        source.HoldGate = () => held;
        session.Enqueue(line: "probe before");
        session.Enqueue(line: "step");
        session.Enqueue(line: "probe after");
        source.Collect();

        // This is the whole point of the seam: a `step`/`settle` verb defers the REST of a piped script to a later
        // frame, and the queue's FIFO order survives the pause.
        Assert.Equal(actual: submitted, expected: ["probe before", "step"]);

        held = false;
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["probe before", "step", "probe after"]);
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
            principal: CommandPrincipal.Console
        );
        var sessionTwo = source.CreateSession(
            onResult: (line, _) => second.Add(item: line),
            principal: CommandPrincipal.Console
        );

        sessionOne.Enqueue(line: "probe one");
        sessionTwo.Enqueue(line: "probe two");
        source.Collect();

        // A session's own hold rotates only that session; the other seat keeps draining.
        Assert.Empty(collection: first);
        Assert.Equal(actual: second, expected: ["probe two"]);

        var held = true;

        source.HoldGate = () => held;
        firstHeld = false;
        sessionTwo.Enqueue(line: "probe three");
        source.Collect();

        // The source-wide gate is the other axis: it stops the drain for everyone, including the session whose own
        // hold has just let go.
        Assert.Empty(collection: first);
        Assert.Equal(actual: second, expected: ["probe two"]);

        held = false;
        source.Collect();

        Assert.Equal(actual: first, expected: ["probe one"]);
        Assert.Equal(actual: second, expected: ["probe two", "probe three"]);
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
            principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink
        );
        var held = false;

        source.HoldGate = () => held;
        session.Enqueue(line: "sim.defer payload");
        session.Enqueue(line: "probe after");
        source.Collect();

        // The deferred line went out; the immediate read-back behind it waits for the tick that applies it.
        Assert.Equal(actual: submitted, expected: ["sim.defer payload"]);

        held = true;

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);
        source.Collect();

        // The barrier has released, but the gate has not: the two holds are independent and BOTH must let go.
        Assert.Equal(actual: submitted, expected: ["sim.defer payload"]);

        held = false;
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.defer payload", "probe after"]);
    }
    [Fact]
    public void BackgroundProducersEnqueueWhileTheFrameThreadCollects() {
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
                principal: CommandPrincipal.Console
            );
        }

        var threads = new Thread[Producers];

        for (var producer = 0; (producer < Producers); producer++) {
            var index = producer;

            threads[producer] = new Thread(start: () => {
                for (var line = 0; (line < LinesPerProducer); line++) {
                    sessions[index].Enqueue(line: $"probe {index} {line}");
                }
            });
            threads[producer].Start();
        }

        var deadline = Stopwatch.StartNew();

        while (collected.Sum(selector: static lines => lines.Count) < (Producers * LinesPerProducer)) {
            Assert.True(condition: (deadline.Elapsed < TimeSpan.FromSeconds(value: 30)), userMessage: "the drain did not keep up with its producers");
            source.Collect();
        }

        foreach (var thread in threads) {
            thread.Join();
        }

        source.Collect();

        for (var producer = 0; (producer < Producers); producer++) {
            // Exactly once, and in the order that producer wrote them: a session's queue is its own FIFO, and the
            // rotation that lets a blocked session step aside moves the whole stream rather than reordering it.
            Assert.Equal(
                actual: collected[producer],
                expected: [.. Enumerable.Range(count: LinesPerProducer, start: 0).Select(selector: line => $"probe {producer} {line}")]
            );
        }
    }

    private static TextCommandSource Source(List<string> submitted, out TextCommandSession session) {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var source = new TextCommandSource(registry: registry);

        session = source.CreateSession(
            onResult: (line, _) => submitted.Add(item: line),
            principal: CommandPrincipal.Console
        );

        return source;
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
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
