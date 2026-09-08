using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the per-entry exception boundary <see cref="CommandRegistry.ApplySnapshot"/> promises: nothing raised
/// while one entry is applied — a handler, an observer sink, or the registry's own decoding of a submitted line — may
/// abandon the rest of the tick or strand a later entry's read-after-write barrier.</summary>
public sealed class CommandRegistryBoundaryTests {
    private static CommandSnapshot Tick(InputRouter router) => router.SnapshotForTick(
        tick: 1UL,
        windowEndTick: ulong.MaxValue
    );

    [Fact]
    public void AThrowingObserverNeitherStopsTheTickNorStrandsALaterBarrier() {
        var applied = new List<string>();
        var submitted = new List<string>();
        var registry = new CommandRegistry(
            modules: [
                new SumModule(),
                new BoundProbeModule(),
                new RecordingSimulationModule(applied: applied),
            ],
            observers: [new ThrowingObserver()]
        );
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(command: "bound.probe"),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSeatSession(
            onResult: (line, _) => submitted.Add(item: line),
            router: router,
            slot: 0
        );

        session.Enqueue(line: "sim.record payload");
        session.Enqueue(line: "sum 2 3");
        // Captured BEFORE the drain injects the text entry, so the bound entry — whose dispatch notifies the throwing
        // observer — sorts ahead of it in the lane.
        router.Capture(signal: InputSignal.Press(source: "key.a"));
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.record payload"]);

        var snapshot = Tick(router: router);

        registry.ApplySnapshot(snapshot: in snapshot);

        // The observer threw on the FIRST entry; the submitted line behind it still ran, and its session's barrier
        // still released, so the queued immediate line drains on the next frame.
        Assert.Equal(actual: applied, expected: ["payload"]);

        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.record payload", "sum 2 3"]);
    }
    [Fact]
    public void AThrowingObserverDoesNotSilenceTheObserversAfterIt() {
        var seen = new List<string>();
        var registry = new CommandRegistry(
            modules: [new BoundProbeModule()],
            observers: [new ThrowingObserver(), new RecordingObserver(seen: seen)]
        );
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(command: "bound.probe"),
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var snapshot = Tick(router: router);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: seen, expected: ["bound.probe"]);
        // The swallowed notification carried a verdict that never reached its sink, so it is counted rather than
        // passing silently — but as an observer fault, in its own segment. Nothing was submitted here at all.
        Assert.Equal(expected: "[wire.errors: 0 rejected | 1 observer fault]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void ObserverFaultsAreCountedApartFromTheCallersRefusedLines() {
        var registry = new CommandRegistry(
            modules: [new BoundProbeModule()],
            observers: [new ThrowingObserver(), new ThrowingObserver(), new ThrowingObserver()]
        );
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(command: "bound.probe"),
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var snapshot = Tick(router: router);

        registry.ApplySnapshot(snapshot: in snapshot);

        // ONE bound press, no submitted line anywhere — yet each of the three sinks threw. Folding those into
        // wire.errors reported `3 rejected` for a caller that had submitted nothing, so a scripted driver asserting
        // zero refusals was poisoned by a broken UI sink on a gamepad press.
        Assert.Equal(expected: "[wire.errors: 0 rejected | 3 observer faults]", actual: registry.Submit(line: "wire.errors").Output);

        // A genuinely refused line still lands on the refusal count, beside the faults rather than mixed into them.
        _ = registry.Submit(line: "does.not.exist");
        Assert.Equal(expected: "[wire.errors: 1 rejected | 3 observer faults]", actual: registry.Submit(line: "wire.errors reset").Output);
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void AMalformedDeferredLineWithABrokenSinkIsStillOneRefusal() {
        var registry = new CommandRegistry(
            modules: [new StrictSimulationModule()],
            observers: [new ThrowingObserver()]
        );
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        // Submit accepted the line (its arguments are read a tick later), and the apply-time decode then refused it and
        // told the observer, which threw. One caller line, one refusal — the observer's own fault used to add a second.
        Assert.Equal(actual: registry.Submit(line: "sim.strict a b c d e"), expected: CommandResult.None);

        var snapshot = Tick(router: router);

        registry.ApplySnapshot(snapshot: in snapshot);
        Assert.Equal(expected: "[wire.errors: 1 rejected | 1 observer fault]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void AFaultTheHandlerBoundaryCannotContainStillReleasesTheEntrysBarrier() {
        var applied = new List<string>();
        var submitted = new List<string>();
        var registry = new CommandRegistry(modules: [
            new SumModule(),
            new UnrenderableFaultModule(),
            new RecordingSimulationModule(applied: applied),
        ]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(command: "boom.unrenderable"),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSeatSession(
            onResult: (line, _) => submitted.Add(item: line),
            router: router,
            slot: 0
        );

        session.Enqueue(line: "sim.record payload");
        session.Enqueue(line: "sum 2 3");
        router.Capture(signal: InputSignal.Press(source: "key.a"));
        source.Collect();

        var snapshot = Tick(router: router);

        // Dispatch's own catch renders the fault into an error result, and rendering reads Message — which throws
        // again, from OUTSIDE the handler boundary. Only a boundary around the whole ENTRY contains it.
        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: applied, expected: ["payload"]);

        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.record payload", "sum 2 3"]);
    }
    [Fact]
    public void ASnapshotBuiltForAnotherRegistryIsRefusedWholeBeforeAnyEntryRuns() {
        var applied = new List<string>();
        var registry = new CommandRegistry(modules: [new RecordingSimulationModule(applied: applied)]);
        var other = new CommandRegistry(modules: [new RecordingSimulationModule(applied: [])]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(command: "sim.record"),
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var snapshot = Tick(router: router);

        // A mismatch is a composition-root error, not a per-entry fault: the entries name ids this registry cannot
        // decode, so the snapshot is refused whole — before anything is dispatched and before any barrier is touched.
        _ = Assert.Throws<ArgumentException>(testCode: () => other.ApplySnapshot(snapshot: in snapshot));
        Assert.Empty(collection: applied);
    }
    [Fact]
    public void ACancellationSignalUnwindsToTheHostInsteadOfBecomingAWireError() {
        var registry = new CommandRegistry(modules: [new CancellingModule()]);

        // A handler raises this by observing the HOST's token, so it is a request to stop rather than a verdict about
        // the verb: converting it into `[cancel.probe: handler threw …]` would leave the host to pattern-match its own
        // shutdown back out of the wire, and would count it as a refused line.
        _ = Assert.Throws<OperationCanceledException>(testCode: () => registry.Submit(line: "cancel.probe"));
        _ = Assert.Throws<OperationCanceledException>(testCode: () => registry.Submit(line: "cancel.probe \"quoted\""));
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void ACancellationSignalUnwindsOutOfApplySnapshotAfterReleasingItsOwnBarrier() {
        var submitted = new List<string>();
        var registry = new CommandRegistry(modules: [new SumModule(), new CancellingSimulationModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSeatSession(
            onResult: (line, _) => submitted.Add(item: line),
            router: router,
            slot: 0
        );

        session.Enqueue(line: "sim.cancel");
        session.Enqueue(line: "sum 2 3");
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.cancel"]);

        var snapshot = Tick(router: router);

        _ = Assert.Throws<OperationCanceledException>(testCode: () => registry.ApplySnapshot(snapshot: in snapshot));

        // Unwinding still runs the per-entry finally, so the session is not left holding a barrier no later tick can
        // release; the host decides whether to drain again after the cancellation it asked for.
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.cancel", "sum 2 3"]);
    }
    [Fact]
    public void ACancellationReleasesTheBarriersOfTheEntriesItLeavesUnapplied() {
        var applied = new List<string>();
        var submitted = new List<string>();
        var registry = new CommandRegistry(modules: [
            new SumModule(),
            new CancellingSimulationModule(),
            new RecordingSimulationModule(applied: applied),
        ]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSeatSession(
            onResult: (line, _) => submitted.Add(item: line),
            router: router,
            slot: 0
        );

        // Collect keeps draining Simulation-routed lines, so BOTH deferred lines fold into the same tick and each one
        // holds a Begin() on this session's barrier. The immediate line behind them waits for that barrier to clear.
        session.Enqueue(line: "sim.cancel");
        session.Enqueue(line: "sim.record second");
        session.Enqueue(line: "sum 2 3");
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.cancel", "sim.record second"]);

        var snapshot = Tick(router: router);

        _ = Assert.Throws<OperationCanceledException>(testCode: () => registry.ApplySnapshot(snapshot: in snapshot));

        // The cancellation applies a PREFIX: the second entry never ran.
        Assert.Empty(collection: applied);

        // …but it is never going to run either, so its barrier had to be released on the way out. Leaving it standing
        // suspended the session permanently — Collect saw a pending submission for a line the tick had abandoned and
        // rotated the session to the tail on every frame from then on, so `sum 2 3` never drained.
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.cancel", "sim.record second", "sum 2 3"]);
    }
    [Fact]
    public void ATextEntryNamingAnIdThisRegistryCannotDecodeIsSkippedWithoutStrandingItsSession() {
        var submitted = new List<string>();
        // Two registries, and a host that wired the wrong router's sink: ids are interned per registry, so the id the
        // WRITER minted here indexes nothing in the registry that applies the snapshot. It is the one public path to
        // an out-of-range text entry — every other one is closed (a sink resolves its id from its own registry, and a
        // snapshot built for another registry is refused whole).
        var writer = new CommandRegistry(modules: [new ManyCommandsModule(count: 64), new SumModule()]);
        var reader = new CommandRegistry(modules: [new BoundProbeModule()]);
        var router = new InputRouter(
            registry: reader,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: writer);
        var session = source.CreateSession(
            onResult: (line, _) => submitted.Add(item: line),
            principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink
        );

        session.Enqueue(line: "sim.wide payload");
        session.Enqueue(line: "sum 2 3");
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.wide payload"]);

        var snapshot = Tick(router: router);

        reader.ApplySnapshot(snapshot: in snapshot);

        // Nothing was dispatched — the reader has no command at that id — but the entry's read-after-write barrier
        // still released, so the writer's session drains its queued line instead of rotating forever.
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.wide payload", "sum 2 3"]);
    }

    private sealed class ManyCommandsModule(int count) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            for (var index = 0; (index < count); index++) {
                yield return CommandDefinition.Verb(
                    name: $"command.{index}",
                    description: "Widens the interned id space.",
                    valueKind: CommandValueKind.Digital,
                    handler: static _ => CommandResult.None,
                    bindability: CommandBindability.Bindable
                );
            }

            yield return CommandDefinition.WithWireArgs(
                name: "sim.wide",
                description: "A deferred verb whose interned id is wider than the reading registry's table.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class FixedBindings(string command) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: command)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class CancellingModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "cancel.probe",
                description: "Observes an already-cancelled host token.",
                handler: static (_, _) => throw new OperationCanceledException(),
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class CancellingSimulationModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sim.cancel",
                description: "Observes an already-cancelled host token when its tick applies.",
                handler: static (_, _) => throw new OperationCanceledException(),
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class RecordingObserver(List<string> seen) : ICommandObserver {
        public void OnCommand(in CommandActivation activation) => seen.Add(item: activation.Name);
    }
    private sealed class ThrowingObserver : ICommandObserver {
        public void OnCommand(in CommandActivation activation) => throw new InvalidTimeZoneException(message: "observer sink fault");
    }
    private sealed class BoundProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "bound.probe",
                description: "A bound verb whose dispatch notifies observers.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
    private sealed class RecordingSimulationModule(List<string> applied) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sim.record",
                description: "Records its argument when its tick applies.",
                handler: (_, args) => {
                    applied.Add(item: args[0].ToString());

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class StrictSimulationModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            // A bare verb: its text command takes no arguments at all, so a line carrying some is accepted at submit
            // (Simulation defers its parse) and refused when the tick decodes it.
            yield return CommandDefinition.Verb(
                name: "sim.strict",
                description: "A deferred verb whose text command accepts no arguments.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class SumModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sum",
                description: "An immediate verb the queued line behind a barrier names.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class UnrenderableFaultModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "boom.unrenderable",
                description: "Throws an exception whose own Message getter throws.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => throw new UnrenderableException(),
                bindability: CommandBindability.Bindable
            );
        }
    }
    // A fault the handler boundary catches but cannot describe: HandlerFault interpolates Message, so the SECOND throw
    // escapes that catch entirely. It stands in for every non-handler throw the same boundary cannot cover — the
    // submitted line's parse, the verb canonicalization — none of which any input this suite can build makes throw.
    private sealed class UnrenderableException : Exception {
        public override string Message => throw new InvalidTimeZoneException(message: "unrenderable");
    }
}
