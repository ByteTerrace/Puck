using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class ConsoleSessionBankTests {
    [Fact]
    public void TextDevicesFeedIndependentPrincipalBoundSeatSessions() {
        var seen = new List<CommandContext>();
        var registry = new CommandRegistry(modules: [new ProbeModule(seen: seen)]);
        var slots = new TwoSeatSlots();
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new SeatPrincipals(),
            slotResolver: slots
        );
        var source = new TextCommandSource(registry: registry);
        var focus = new TestFocus();
        var terminalSessions = new TerminalConsoleSessions();
        var sessions = new ConsoleSessionBank(
            seatCount: 2,
            source: source,
            router: router,
            slotResolver: slots,
            clipboard: new TestClipboard(),
            focus: focus,
            terminalSessions: terminalSessions
        );
        var sink = new ConsoleInputSink(sessions: sessions, slotResolver: slots);

        // The closed-session key observations associate each physical text device with its roster seat.
        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Backtick, deviceId: slots.SeatOne));
        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Backtick, deviceId: slots.SeatTwo));
        Assert.True(condition: terminalSessions.TrySetVisible(slot: 0, visible: true, resolved: out _));
        Assert.True(condition: terminalSessions.TrySetVisible(slot: 1, visible: true, resolved: out _));
        Assert.False(condition: focus.IsActiveFor(deviceId: slots.SeatOne));
        Assert.False(condition: focus.IsActiveFor(deviceId: slots.SeatTwo));

        TypeAndSubmit(sink: sink, device: slots.SeatTwo);
        TypeAndSubmit(sink: sink, device: slots.SeatOne);
        source.Collect();

        Assert.Equal(expected: [1, 0], actual: seen.Select(selector: static context => context.Slot));
        Assert.All(collection: seen, action: static context => Assert.Equal(
            expected: CommandPrincipal.Seat(slot: context.Slot),
            actual: context.Principal
        ));

        Assert.True(condition: sessions.StoreFor(slot: 0).TrySnapshot(frame: out var seatOneTape));
        Assert.True(condition: sessions.StoreFor(slot: 1).TrySnapshot(frame: out var seatTwoTape));
        Assert.Contains(collection: seatOneTape.Lines, filter: static line => line.Text == "[probe: seat=1]");
        Assert.Contains(collection: seatTwoTape.Lines, filter: static line => line.Text == "[probe: seat=2]");
        Assert.DoesNotContain(collection: seatOneTape.Lines, filter: static line => line.Text == "[probe: seat=2]");
        Assert.DoesNotContain(collection: seatTwoTape.Lines, filter: static line => line.Text == "[probe: seat=1]");
    }

    [Fact]
    public void OnlyFirstTextCapableEventAssociatesADeviceAndPointerEventsAreIgnored() {
        var (sessions, sink, slots, focus, _) = CreateSessions();

        Assert.True(condition: sessions.TrySetVisible(slot: 0, visible: true, resolved: out _));
        sink.Observe(inputEvent: new WindowInputEvent(Kind: WindowInputKind.PointerMove, DeviceId: slots.SeatOne));
        Assert.Equal(expected: 0, actual: focus.ReleaseCalls);

        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Backtick, deviceId: slots.SeatOne));
        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Backtick, deviceId: slots.SeatOne));

        Assert.Equal(expected: 1, actual: focus.ReleaseCalls);
    }

    [Fact]
    public void ReassigningADeviceEvictsItsOldConsoleAssociation() {
        var (sessions, sink, slots, focus, _) = CreateSessions();

        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Backtick, deviceId: slots.SeatOne));
        Assert.True(condition: sessions.TrySetVisible(slot: 0, visible: true, resolved: out _));
        Assert.False(condition: focus.IsActiveFor(deviceId: slots.SeatOne));

        slots.MoveSeatOneTo(slot: 1);
        Assert.True(condition: focus.IsActiveFor(deviceId: slots.SeatOne));
        Assert.True(condition: sessions.TrySetVisible(slot: 1, visible: true, resolved: out _));
        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Backtick, deviceId: slots.SeatOne));
        Assert.False(condition: focus.IsActiveFor(deviceId: slots.SeatOne));

        Assert.True(condition: sessions.TrySetVisible(slot: 0, visible: false, resolved: out _));
        Assert.False(condition: focus.IsActiveFor(deviceId: slots.SeatOne));
    }

    [Fact]
    public void AdministrativeExchangeAndDeferredEchoReachTheDisplayedTape() {
        var (sessions, _, _, _, terminalSessions) = CreateSessions();

        terminalSessions.RecordAdministrative(line: "probe", result: new CommandResult(Output: "ok"));
        terminalSessions.RecordAdministrativeEcho(message: "edit applied", refused: false);
        terminalSessions.RecordAdministrativeActivation(activation: new CommandActivation(
            Name: "simulate",
            Phase: CommandPhase.Completed,
            Result: new CommandResult(Output: "deferred result"),
            Text: "simulate",
            Principal: CommandPrincipal.Console
        ));

        Assert.True(condition: sessions.StoreFor(slot: 0).TrySnapshot(frame: out var frame));
        Assert.Contains(collection: frame.Lines, filter: static line => line.Text == "> probe");
        Assert.Contains(collection: frame.Lines, filter: static line => line.Text == "ok");
        Assert.Contains(collection: frame.Lines, filter: static line => line.Text == "edit applied");
        Assert.Contains(collection: frame.Lines, filter: static line => line.Text == "deferred result");
        Assert.True(condition: terminalSessions.OperatorStore.TrySnapshot(frame: out var operatorFrame));
        Assert.Contains(collection: operatorFrame.Lines, filter: static line => line.Text == "> probe");
        Assert.Contains(collection: operatorFrame.Lines, filter: static line => line.Text == "edit applied");
        Assert.Contains(collection: operatorFrame.Lines, filter: static line => line.Text == "deferred result");
    }

    [Fact]
    public void AdministrativeSimulationResultReachesBothTapesThroughTheRegistryObserver() {
        ConsoleSessionBank? sessions = null;
        var terminalSessions = new TerminalConsoleSessions();
        var observer = new ConsoleSessionCommandObserver(
            sessions: () => sessions!,
            terminalSessions: terminalSessions
        );
        var registry = new CommandRegistry(
            modules: [new DeferredProbeModule()],
            observers: [observer]
        );
        var slots = new TwoSeatSlots();
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new SeatPrincipals(),
            slotResolver: slots
        );
        var source = new TextCommandSource(
            registry: registry,
            onResult: terminalSessions.RecordAdministrative
        );
        sessions = new ConsoleSessionBank(
            seatCount: 2,
            source: source,
            router: router,
            slotResolver: slots,
            clipboard: new TestClipboard(),
            focus: new TestFocus(),
            terminalSessions: terminalSessions
        );
        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        source.Enqueue(line: "deferred-probe");
        source.Collect();
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.True(condition: sessions.StoreFor(slot: 0).TrySnapshot(frame: out var seatFrame));
        Assert.Contains(collection: seatFrame.Lines, filter: static line => line.Text == "[deferred-probe: ok]");
        Assert.True(condition: terminalSessions.OperatorStore.TrySnapshot(frame: out var operatorFrame));
        Assert.Contains(collection: operatorFrame.Lines, filter: static line => line.Text == "[deferred-probe: ok]");
    }

    private static (ConsoleSessionBank Sessions, ConsoleInputSink Sink, TwoSeatSlots Slots, TestFocus Focus, TerminalConsoleSessions TerminalSessions) CreateSessions() {
        var registry = new CommandRegistry(modules: [new ProbeModule(seen: [])]);
        var slots = new TwoSeatSlots();
        var router = new InputRouter(registry: registry, bindings: new EmptyBindings(), principalResolver: new SeatPrincipals(), slotResolver: slots);
        var focus = new TestFocus();
        var terminalSessions = new TerminalConsoleSessions();
        var sessions = new ConsoleSessionBank(
            seatCount: 2,
            source: new TextCommandSource(registry: registry),
            router: router,
            slotResolver: slots,
            clipboard: new TestClipboard(),
            focus: focus,
            terminalSessions: terminalSessions
        );

        return (sessions, new ConsoleInputSink(sessions: sessions, slotResolver: slots), slots, focus, terminalSessions);
    }

    private static void TypeAndSubmit(ConsoleInputSink sink, InputDeviceId device) {
        sink.Observe(inputEvent: WindowInputEvent.TypedText(text: "probe", deviceId: device));
        sink.Observe(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Enter, deviceId: device));
    }

    private sealed class ProbeModule(List<CommandContext> seen) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "probe",
                description: "Reports its invoking seat.",
                handler: (context, _) => {
                    seen.Add(item: context);
                    return new CommandResult(Output: $"[probe: seat={context.Slot + 1}]");
                },
                bindability: CommandBindability.Unbindable
            );
        }
    }

    private sealed class DeferredProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Unbindable,
                name: "deferred-probe",
                description: "Reports after deterministic dispatch.",
                valueKind: CommandValueKind.Digital,
                handler: _ => new CommandResult(Output: "[deferred-probe: ok]"),
                routing: CommandRouting.Simulation
            );
        }
    }

    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }

    private sealed class SeatPrincipals : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Seat(slot: slot);
    }

    private sealed class TwoSeatSlots : IInputSlotResolver {
        private int m_seatOneSlot;
        public InputDeviceId SeatOne { get; } = InputDeviceId.FromConnectionKey(key: "keyboard-one");
        public InputDeviceId SeatTwo { get; } = InputDeviceId.FromConnectionKey(key: "keyboard-two");

        public event Action<InputDeviceId>? DeviceSlotChanging;

        public bool CommitSlot(InputDeviceId device, int slot) => false;

        public int ResolveSlot(InputDeviceId device) => ((device == SeatTwo) ? 1 : m_seatOneSlot);

        public void MoveSeatOneTo(int slot) {
            DeviceSlotChanging?.Invoke(obj: SeatOne);
            m_seatOneSlot = slot;
        }
    }

    private sealed class TestFocus : IInputFocus {
        private readonly HashSet<InputDeviceId> m_released = [];

        public int ReleaseCalls { get; private set; }

        public void Claim(InputDeviceId? deviceId = null) {
            if (deviceId is { } device) {
                _ = m_released.Remove(item: device);
            } else {
                m_released.Clear();
            }
        }

        public bool IsActiveFor(InputDeviceId deviceId) => !m_released.Contains(item: deviceId);

        public void Release(InputDeviceId? deviceId = null) {
            ReleaseCalls++;

            if (deviceId is { } device) {
                _ = m_released.Add(item: device);
            }
        }
    }

    private sealed class TestClipboard : IClipboardService {
        public void SetText(string text) { }

        public bool TryGetText(out string text) {
            text = string.Empty;
            return false;
        }
    }
}
