using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the router's held-command edge logic over physical signals: the digital first-down / last-up
/// edges across two controls bound to one command, and the release a device disconnect synthesizes for the state it
/// was carrying.</summary>
public sealed class InputRouterTests {
    private const string Command = "test.move";

    [Fact]
    public void TwoControlsOnOneCommandPressOnFirstDownAndReleaseOnLastUp() {
        var router = Router(out _);
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        // First control down: the logical press edge fires (Dispatch true) and the command is now carried.
        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        var press = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: press.Phase);
        Assert.True(condition: press.Dispatch);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        // Second control down for the SAME command: no new press edge — every entry this tick is a non-dispatching
        // re-assertion or the second control's suppressed press.
        router.Capture(signal: InputSignal.Press(source: "key.up", deviceId: device));

        Assert.All(collection: Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries, action: static entry => Assert.False(condition: entry.Dispatch));
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        // One of the two controls lifts: the command stays held (the other control still owns it), no release edge.
        router.Capture(signal: InputSignal.Release(source: "key.w", deviceId: device));

        Assert.All(collection: Assert.Single(router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries, action: static entry => Assert.False(condition: entry.Dispatch));
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        // The last control lifts: the command is no longer carried.
        router.Capture(signal: InputSignal.Release(source: "key.up", deviceId: device));
        _ = router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue);

        Assert.False(condition: router.IsCommandHeld(slot: 0, command: Command));
    }

    [Fact]
    public void DeviceDisconnectCancelsAndDropsTheHoldItCarried() {
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(Command)]),
            bindings: new FixedBindings(new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal(),
            slotResolver: new FakeSlotResolver(out var raiseDisconnect)
        );
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        // The device disconnects: the router synthesizes a release for the hold it was carrying and drops it.
        raiseDisconnect(device);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: Command));

        var cancellation = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Canceled, actual: cancellation.Phase);
    }

    [Fact]
    public void RecycledHeldStateDoesNotRetainItsPreviousControl() {
        var router = Router(out _);
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Release(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        // The next press reuses the released state's scratch, but it is a fresh logical first-down edge rather than
        // a suppressed second-control press inherited from the previous hold.
        router.Capture(signal: InputSignal.Press(source: "key.up", deviceId: device));
        var entries = Assert.Single(router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        var press = Assert.Single(collection: entries, predicate: static entry => entry.Dispatch);

        Assert.Equal(expected: CommandPhase.Started, actual: press.Phase);
        Assert.Equal(expected: "key.up", actual: press.Source);
    }

    [Fact]
    public void DisconnectRepairsAStaleDeviceAnnotationWhenAnotherControlSustainsTheHold() {
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(Command)]),
            bindings: new FixedBindings(new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal(),
            slotResolver: new FakeSlotResolver(out var raiseDisconnect)
        );
        var deviceA = InputDeviceId.FromConnectionKey(key: "kbd-1");
        var deviceB = InputDeviceId.FromConnectionKey(key: "pad-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: deviceA));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.up", deviceId: deviceB));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        // B releases its control while A sustains the hold: the suppressed release re-latches the carried entry
        // stamped with B — a stale annotation once B is gone.
        router.Capture(signal: InputSignal.Release(source: "key.up", deviceId: deviceB));
        _ = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);

        // B disconnects: the hold survives on A's control, no cancellation fires, and the annotation repairs to A.
        raiseDisconnect(deviceB);
        var held = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Active, actual: held.Phase);
        Assert.Equal(expected: deviceA, actual: held.Device);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));
    }

    [Fact]
    public void FocusLossCancelsHeldCommandsDropsQueuedPhysicalSignalsAndResetsBindings() {
        var bindings = new TrackingBindings(binding: new CommandBinding(Command: Command));
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(Command)]),
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.w"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.up"));

        router.ReleaseHeld();

        Assert.True(condition: bindings.WasReset);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: Command));

        var cancellation = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Canceled, actual: cancellation.Phase);
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    private static InputRouter Router(out CommandRegistry registry) {
        registry = new CommandRegistry(modules: [new DigitalModule(Command)]);

        return new InputRouter(
            registry: registry,
            bindings: new FixedBindings(new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal()
        );
    }

    private sealed class FixedBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [binding];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }

    private sealed class TrackingBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [binding];

        public bool WasReset { get; private set; }

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;

        public void ResetAll() {
            WasReset = true;
        }
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }

    private sealed class DigitalModule(string command) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: command,
                description: "Digital held probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }

    // A single-slot resolver that exposes its DeviceSlotChanging edge so a test can drive a disconnect.
    private sealed class FakeSlotResolver : IInputSlotResolver {
        public FakeSlotResolver(out Action<InputDeviceId> raiseDisconnect) {
            raiseDisconnect = device => DeviceSlotChanging?.Invoke(obj: device);
        }

        public event Action<InputDeviceId>? DeviceSlotChanging;

        public int ResolveSlot(InputDeviceId device) => 0;

        public bool CommitSlot(InputDeviceId device, int slot) => true;
    }
}
