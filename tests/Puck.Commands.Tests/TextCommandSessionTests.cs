using Xunit;

namespace Puck.Commands.Tests;

public sealed class TextCommandSessionTests {
    [Fact]
    public void SeatSessionsPermanentlyStampTheirOwnPrincipalAndSlot() {
        var seen = new List<CommandContext>();
        var registry = new CommandRegistry(modules: [new SessionModule(seen: seen)]);
        var router = Router(registry: registry, bindings: new EmptyBindings());
        var source = new TextCommandSource(registry: registry);
        var seatOne = source.CreateSeatSession(router: router, slot: 0);
        var seatTwo = source.CreateSeatSession(router: router, slot: 1);

        seatTwo.Enqueue(line: "probe");
        seatOne.Enqueue(line: "probe");
        source.Collect();

        Assert.Equal(expected: [1, 0], actual: seen.Select(selector: static context => context.Slot));
        Assert.All(collection: seen, action: static context => Assert.Equal(
            expected: CommandPrincipal.Seat(slot: context.Slot),
            actual: context.Principal
        ));
        Assert.All(collection: seen, action: static context => Assert.Equal(
            expected: CommandOrigin.Text,
            actual: context.Origin
        ));
    }
    [Fact]
    public void OneSeatsSimulationBarrierDoesNotBlockAnotherSeatsReadyText() {
        var seen = new List<CommandContext>();
        var registry = new CommandRegistry(modules: [new SessionModule(seen: seen)]);
        var router = Router(registry: registry, bindings: new EmptyBindings());
        var source = new TextCommandSource(registry: registry);
        var seatOneResults = new List<string>();
        var seatTwoResults = new List<string>();
        var seatOne = source.CreateSeatSession(
            router: router,
            slot: 0,
            onResult: (line, _) => seatOneResults.Add(item: line)
        );
        var seatTwo = source.CreateSeatSession(
            router: router,
            slot: 1,
            onResult: (line, _) => seatTwoResults.Add(item: line)
        );

        seatOne.Enqueue(line: "simulate");
        seatOne.Enqueue(line: "probe");
        seatTwo.Enqueue(line: "probe");
        source.Collect();

        Assert.Equal(actual: seatOneResults, expected: ["simulate"]);
        Assert.Equal(actual: seatTwoResults, expected: ["probe"]);

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);
        source.Collect();

        Assert.Equal(actual: seatOneResults, expected: ["simulate", "probe"]);
        Assert.Equal(expected: 3, actual: seen.Count);
        Assert.All(collection: seen, action: static context => Assert.Equal(
            expected: CommandOrigin.Text,
            actual: context.Origin
        ));
    }
    [Fact]
    public void ReleasedFocusDispatchesOnlyFocusExemptCommandsAndIgnoresKeyRepeat() {
        var dispatched = new List<string>();
        var registry = new CommandRegistry(modules: [new FocusModule(dispatched: dispatched)]);
        var router = Router(
            registry: registry,
            bindings: new FixedBindings(bindings: [new CommandBinding(Command: "gameplay.action")]),
            alwaysActiveBindings: new FixedAlwaysActiveBindings(bindings: [new CommandBinding(Command: "terminal.console")])
        );
        var keyboard = InputDeviceId.FromConnectionKey(key: "keyboard");

        router.Capture(signal: InputSignal.Press(source: "keyboard.backtick", deviceId: keyboard));
        var opening = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in opening);
        router.SuppressHeld(device: keyboard);

        // The OS repeats Started after focus has moved to the console. It is still the same physical press.
        router.CaptureFocusExempt(signal: InputSignal.Press(source: "keyboard.backtick", deviceId: keyboard));
        var repeated = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in repeated);

        router.CaptureFocusExempt(signal: InputSignal.Release(source: "keyboard.backtick", deviceId: keyboard));
        var released = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in released);
        router.CaptureFocusExempt(signal: InputSignal.Press(source: "keyboard.backtick", deviceId: keyboard));
        var closing = router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in closing);

        Assert.Equal(actual: dispatched, expected: ["gameplay.action", "terminal.console", "terminal.console"]);
    }

    private static InputRouter Router(CommandRegistry registry, IInputBindings bindings, IAlwaysActiveInputBindings? alwaysActiveBindings = null) => new(
        registry: registry,
        bindings: bindings,
        principalResolver: new SeatPrincipal(),
        alwaysActiveBindings: alwaysActiveBindings
    );

    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class FixedBindings(IReadOnlyList<CommandBinding> bindings) : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => bindings;
    }
    private sealed class FixedAlwaysActiveBindings(IReadOnlyList<CommandBinding> bindings) : IAlwaysActiveInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => bindings;
    }
    private sealed class SeatPrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Seat(slot: slot);
    }
    private sealed class SessionModule(List<CommandContext> seen) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "probe",
                description: "Records its stamped context.",
                handler: (context, _) => {
                    seen.Add(item: context);
                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "simulate",
                description: "Deferred session barrier probe.",
                handler: (context, _) => {
                    seen.Add(item: context);
                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class FocusModule(List<string> dispatched) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return Definition(inputScope: CommandInputScope.Focused, name: "gameplay.action");
            yield return Definition(inputScope: CommandInputScope.FocusExempt, name: "terminal.console");
        }

        private CommandDefinition Definition(string name, CommandInputScope inputScope) => CommandDefinition.Verb(
            name: name,
            description: "Focus routing probe.",
            valueKind: CommandValueKind.Digital,
            handler: context => {
                if (context.Phase == CommandPhase.Started) {
                    dispatched.Add(item: name);
                }

                return CommandResult.None;
            },
            bindability: CommandBindability.Bindable,
            inputScope: inputScope
        );
    }
}
