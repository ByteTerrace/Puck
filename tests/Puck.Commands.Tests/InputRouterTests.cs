using System.Runtime.CompilerServices;
using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the router's held-command edge logic over physical signals: the digital first-down / last-up
/// edges across two controls bound to one command, and the release a device disconnect synthesizes for the state it
/// was carrying.</summary>
public sealed class InputRouterTests {
    private const string Command = "test.move";

    [Fact]
    public void TwoControlsOnOneCommandPressOnFirstDownAndReleaseOnLastUp() {
        var router = Router(registry: out _);
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        // First control down: the logical press edge fires (Dispatch true) and the command is now carried.
        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        var press = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: press.Phase);
        Assert.Equal(expected: CommandOrigin.Binding, actual: press.Origin);
        Assert.Equal(expected: "key.w", actual: press.Source);
        Assert.True(condition: press.Dispatch);
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 0));

        // Second control down for the SAME command: no new press edge — every entry this tick is a non-dispatching
        // re-assertion or the second control's suppressed press.
        router.Capture(signal: InputSignal.Press(source: "key.up", deviceId: device));

        Assert.All(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries, action: static entry => Assert.False(condition: entry.Dispatch));
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 0));

        // One of the two controls lifts: the command stays held (the other control still owns it), no release edge.
        router.Capture(signal: InputSignal.Release(source: "key.w", deviceId: device));

        Assert.All(collection: Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries, action: static entry => Assert.False(condition: entry.Dispatch));
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 0));

        // The last control lifts: the command is no longer carried.
        router.Capture(signal: InputSignal.Release(source: "key.up", deviceId: device));
        _ = router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue);

        Assert.False(condition: router.IsCommandHeld(command: Command, slot: 0));
    }
    [Fact]
    public void AuthoredLaneSignalBypassesTheSlotResolverAndLeavesNoPresence() {
        // The resolver knows no device: an ordinary signal finds no lane, an authored-lane signal lands on its seat.
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(command: Command)]),
            bindings: new FixedBindings(binding: new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal(),
            slotResolver: static _ => -1
        );
        var device = InputDeviceId.FromConnectionKey(key: "sense:3");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device) with { Slot = 2 });

        var lane = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes);

        Assert.Equal(expected: 2, actual: lane.Slot);
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 2));
        Assert.False(condition: router.TryGetLastInputTick(slot: 2, tick: out _));
    }
    [Fact]
    public void DeviceDisconnectCancelsAndDropsTheHoldItCarried() {
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(command: Command)]),
            bindings: new FixedBindings(binding: new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal(),
            slotResolver: new FakeSlotResolver(raiseDisconnect: out var raiseDisconnect)
        );
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 0));

        // The device disconnects: the router synthesizes a release for the hold it was carrying and drops it.
        raiseDisconnect(device);
        Assert.False(condition: router.IsCommandHeld(command: Command, slot: 0));

        var cancellation = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Canceled, actual: cancellation.Phase);
        Assert.Equal(expected: CommandOrigin.Binding, actual: cancellation.Origin);
    }
    [Fact]
    public void ScopedBindingReloadCancelsOnlyTheAffectedSlot() {
        var bindings = new ReloadableBindings(binding: new CommandBinding(Command: Command));
        var firstDevice = InputDeviceId.FromConnectionKey(key: "first-pad");
        var secondDevice = InputDeviceId.FromConnectionKey(key: "second-pad");
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(command: Command)]),
            bindings: bindings,
            principalResolver: new ConsolePrincipal(),
            slotResolver: device => ((device == firstDevice) ? 0 : 1)
        );

        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: firstDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: secondDevice));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        bindings.Reload(slot: 0);

        Assert.False(condition: router.IsCommandHeld(command: Command, slot: 0));
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 1));

        var snapshot = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(expected: CommandPhase.Canceled, actual: Assert.Single(collection: snapshot.Lanes[0].Entries).Phase);
        Assert.Equal(expected: CommandPhase.Active, actual: Assert.Single(collection: snapshot.Lanes[1].Entries).Phase);
    }
    [Fact]
    public void ResolvedBindingCacheDoesNotOwnRetiredListIdentities() {
        var bindings = new ReplacingBindings(binding: new CommandBinding(Command: Command));
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(command: Command)]),
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );
        var retired = ResolveAndRetireList(bindings: bindings, router: router);

        for (var attempt = 0; (retired.IsAlive && (attempt < 10)); attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(condition: retired.IsAlive);
    }
    [Fact]
    public void RecycledHeldStateDoesNotRetainItsPreviousControl() {
        var router = Router(registry: out _);
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Release(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        // The next press reuses the released state's scratch, but it is a fresh logical first-down edge rather than
        // a suppressed second-control press inherited from the previous hold.
        router.Capture(signal: InputSignal.Press(source: "key.up", deviceId: device));
        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        var press = Assert.Single(collection: entries, predicate: static entry => entry.Dispatch);

        Assert.Equal(expected: CommandPhase.Started, actual: press.Phase);
        Assert.Equal(expected: "key.up", actual: press.Source);
    }
    [Fact]
    public void DisconnectRepairsAStaleDeviceAnnotationWhenAnotherControlSustainsTheHold() {
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new DigitalModule(command: Command)]),
            bindings: new FixedBindings(binding: new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal(),
            slotResolver: new FakeSlotResolver(raiseDisconnect: out var raiseDisconnect)
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
        var held = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Active, actual: held.Phase);
        Assert.Equal(expected: deviceA, actual: held.Device);
        Assert.True(condition: router.IsCommandHeld(command: Command, slot: 0));
    }
    [Fact]
    public void FocusLossCancelsHeldCommandsDropsQueuedPhysicalSignalsAndResetsBindings() {
        var bindings = new TrackingBindings(binding: new CommandBinding(Command: Command));
        var registry = new CommandRegistry(modules: [new SimulationDigitalModule(command: Command)]);
        var clock = new FakeClock();
        var router = new InputRouter(
            registry: registry,
            bindings: bindings,
            principalResolver: new ConsolePrincipal(),
            clock: clock
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        router.Capture(signal: InputSignal.Press(source: "key.w"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.up"));
        Inject(
            registry: registry,
            text: "survives-focus-loss"
        );

        router.ReleaseHeld();

        Assert.True(condition: bindings.WasReset);
        Assert.False(condition: router.IsCommandHeld(command: Command, slot: 0));

        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        var cancellation = Assert.Single(collection: entries, predicate: static entry => (entry.Phase == CommandPhase.Canceled));
        var injection = Assert.Single(collection: entries, predicate: static entry => (entry.Text == $"{Command} survives-focus-loss"));

        Assert.Equal(expected: CommandPhase.Canceled, actual: cancellation.Phase);
        Assert.Equal(expected: CommandOrigin.Text, actual: injection.Origin);
        Assert.DoesNotContain(collection: entries, filter: static entry => (entry.Source == "key.up"));
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void SignalAndInjectionStreamsMergeByCaptureSequence() {
        var router = Router(clock: out var clock, registry: out var registry);

        router.Capture(signal: InputSignal.Press(source: "key.w", captureTick: 10UL));
        clock.NowTicks = 10UL;
        Inject(
            registry: registry,
            text: "between-edges"
        );
        router.Capture(signal: InputSignal.Release(source: "key.w", captureTick: 10UL));

        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: 11UL).Lanes).Entries;

        Assert.Collection(
            entries,
            static entry => {
                Assert.Equal(expected: CommandOrigin.Binding, actual: entry.Origin);
                Assert.Equal(expected: CommandPhase.Started, actual: entry.Phase);
                Assert.Equal(expected: "key.w", actual: entry.Source);
            },
            static entry => {
                Assert.Equal(expected: CommandOrigin.Text, actual: entry.Origin);
                Assert.Equal(expected: $"{Command} between-edges", actual: entry.Text);
            },
            static entry => {
                Assert.Equal(expected: CommandOrigin.Binding, actual: entry.Origin);
                Assert.Equal(expected: CommandPhase.Completed, actual: entry.Phase);
                Assert.Equal(expected: "key.w", actual: entry.Source);
            }
        );
    }
    [Fact]
    public void BothCaptureStreamsRetainFutureDatedItemsUntilTheirWindow() {
        var router = Router(clock: out var clock, registry: out var registry);

        router.Capture(signal: InputSignal.Press(source: "key.w", captureTick: 30UL));
        clock.NowTicks = 40UL;
        Inject(
            registry: registry,
            text: "future-injection"
        );

        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: 30UL).Lanes);

        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: 41UL).Lanes).Entries;

        Assert.Collection(
            entries,
            static entry => {
                Assert.Equal(expected: CommandOrigin.Binding, actual: entry.Origin);
                Assert.Equal(expected: CommandPhase.Started, actual: entry.Phase);
            },
            static entry => {
                Assert.Equal(expected: CommandOrigin.Text, actual: entry.Origin);
                Assert.Equal(expected: $"{Command} future-injection", actual: entry.Text);
            }
        );
    }
    [Fact]
    public void ScaledControlsKeepIndependentChannelOwnershipAndCancellationSources() {
        const string channelCommand = "test.channel";
        var registry = new CommandRegistry(modules: [new AxisModule(command: channelCommand)]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(binding: new CommandBinding(
                Command: channelCommand,
                ChannelScale: 1f
            )),
            principalResolver: new ConsolePrincipal()
        );
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.up", deviceId: device));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Release(source: "key.up", deviceId: device));
        _ = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);

        Assert.True(condition: router.IsCommandHeld(command: channelCommand, slot: 0));

        router.ReleaseHeld();
        var cancellation = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Canceled)
        );

        Assert.Equal(expected: "key.w", actual: cancellation.Source);
        Assert.False(condition: router.IsCommandHeld(command: channelCommand, slot: 0));
    }
    [Fact]
    public void RemovingAMapDeterministicallyCancelsAnExistingHold() {
        var phases = new List<CommandPhase>();
        var registry = new CommandRegistry(modules: [new MappedModule(phases: phases)]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(binding: new CommandBinding(
                Command: "mapped.hold",
                ChannelScale: 1f
            )),
            principalResolver: new ConsolePrincipal()
        );

        router.SetActiveMaps(maps: ["play"], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "key.a"));
        var press = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in press);

        router.SetActiveMaps(maps: [], slot: 0);
        router.Capture(signal: InputSignal.Release(source: "key.a"));
        var release = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in release);

        Assert.Equal(actual: phases, expected: [CommandPhase.Started, CommandPhase.Canceled]);
        Assert.False(condition: router.IsCommandHeld(command: "mapped.hold", slot: 0));
    }
    [Fact]
    public void KeyRepeatRefreshesLastInputWithoutDispatchingASecondPress() {
        var router = Router(registry: out _);
        var device = InputDeviceId.FromConnectionKey(key: "kbd-1");

        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        _ = router.SnapshotForTick(tick: 10UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.w", deviceId: device));
        var repeated = router.SnapshotForTick(tick: 20UL, windowEndTick: ulong.MaxValue);

        Assert.True(condition: router.TryGetLastInputTick(slot: 0, tick: out var lastInput));
        Assert.Equal(actual: lastInput, expected: 20UL);
        Assert.DoesNotContain(collection: Assert.Single(collection: repeated.Lanes).Entries, filter: static entry => entry.Dispatch);
    }
    [Fact]
    public void DigitalReassertionCountsAsLiveInputWithoutDispatchingANewCommandEdge() {
        var router = Router(registry: out _);

        router.Capture(signal: InputSignal.Reassert(source: "key.w"));
        var snapshot = router.SnapshotForTick(tick: 7UL, windowEndTick: ulong.MaxValue);

        Assert.Empty(collection: snapshot.Lanes);
        Assert.True(condition: router.TryGetLastInputTick(slot: 0, tick: out var lastInput));
        Assert.Equal(actual: lastInput, expected: 7UL);
    }
    [Fact]
    public void SnapshotIdentityIsStructuralAndExcludesLocalDeviceAnnotations() {
        var first = Router(registry: out _);
        var second = Router(registry: out _);

        first.Capture(signal: InputSignal.Press(
            source: "key.w",
            deviceId: InputDeviceId.FromConnectionKey(key: "first-local-device")
        ));
        second.Capture(signal: InputSignal.Press(
            source: "key.w",
            deviceId: InputDeviceId.FromConnectionKey(key: "second-local-device")
        ));

        var firstSnapshot = first.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        var secondSnapshot = second.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: secondSnapshot, expected: firstSnapshot);
        Assert.Equal(expected: firstSnapshot.GetHashCode(), actual: secondSnapshot.GetHashCode());
    }
    [Fact]
    public void HeldSnapshotConstructionAllocatesNothingAfterBuffersAreWarm() {
        var router = Router(registry: out _);

        router.Capture(signal: InputSignal.Press(source: "key.w"));
        _ = router.SnapshotForTick(tick: 0UL, windowEndTick: ulong.MaxValue);

        for (var tick = 1UL; (tick <= 32UL); tick++) {
            _ = router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var observedEntries = 0;

        for (var tick = 33UL; (tick < 1_057UL); tick++) {
            observedEntries += router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue).Lanes[0].Entries.Count;
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(actual: observedEntries, expected: 1_024);
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void BatchedCaptureStreamsDrainAndMergeWithoutAllocatingAfterBuffersAreWarm() {
        var registry = new CommandRegistry(modules: [new SimulationDigitalModule(command: Command)]);
        var clock = new FakeClock();
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal(),
            clock: clock
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        for (var tick = 0UL; (tick < 1_024UL); tick++) {
            CaptureBatch(captureTick: tick, router: router);
            InjectBatch(registry: registry);
            _ = router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue);
        }

        var captureAllocated = 0L;
        var drainAllocated = 0L;
        var observedEntries = 0;

        for (var tick = 1_024UL; (tick < 2_048UL); tick++) {
            var beforeCapture = GC.GetAllocatedBytesForCurrentThread();

            CaptureBatch(captureTick: tick, router: router);
            var afterCapture = GC.GetAllocatedBytesForCurrentThread();

            clock.NowTicks = tick;
            InjectBatch(registry: registry);
            var beforeDrain = GC.GetAllocatedBytesForCurrentThread();

            observedEntries += router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue).Lanes[0].Entries.Count;
            var afterDrain = GC.GetAllocatedBytesForCurrentThread();

            captureAllocated += (afterCapture - beforeCapture);
            drainAllocated += (afterDrain - beforeDrain);
        }

        Assert.Equal(actual: observedEntries, expected: 4_096);
        Assert.Equal(actual: captureAllocated, expected: 0L);
        Assert.Equal(actual: drainAllocated, expected: 0L);
    }

    private static void CaptureBatch(InputRouter router, ulong captureTick) {
        for (var index = 0; (index < 4); index++) {
            router.Capture(signal: InputSignal.Press(source: "key.unbound", captureTick: captureTick));
        }
    }
    private static void InjectBatch(CommandRegistry registry) {
        for (var index = 0; (index < 4); index++) {
            _ = registry.Submit(line: $"{Command} queued");
        }
    }
    private static void Inject(CommandRegistry registry, string text) =>
        Assert.Equal(expected: CommandResult.None, actual: registry.Submit(line: $"{Command} {text}"));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveAndRetireList(InputRouter router, ReplacingBindings bindings) {
        var retired = new WeakReference(target: bindings.Current);

        router.Capture(signal: InputSignal.Press(source: "button.action"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        bindings.Replace();

        return retired;
    }
    private static InputRouter Router(out CommandRegistry registry) {
        registry = new CommandRegistry(modules: [new DigitalModule(command: Command)]);

        return new InputRouter(
            registry: registry,
            bindings: new FixedBindings(binding: new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal()
        );
    }
    private static InputRouter Router(out CommandRegistry registry, out FakeClock clock) {
        registry = new CommandRegistry(modules: [new SimulationDigitalModule(command: Command)]);
        clock = new FakeClock();

        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(binding: new CommandBinding(Command: Command)),
            principalResolver: new ConsolePrincipal(),
            clock: clock
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        return router;
    }

    private sealed class FixedBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [binding];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class ReloadableBindings(CommandBinding binding) : IInputBindings, IInputBindingsReloadSource {
        private readonly CommandBinding[] m_bindings = [binding];

        public event Action<int?>? Reloading;

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
        public void Reload(int? slot) => Reloading?.Invoke(obj: slot);
    }
    private sealed class ReplacingBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding m_binding = binding;

        public IReadOnlyList<CommandBinding> Current { get; private set; } = [binding];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => Current;
        public void Replace() => Current = [m_binding];
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
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class FakeClock : IInputClock {
        public ulong NowTicks { get; set; }
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
    private sealed class SimulationDigitalModule(string command) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: command,
                description: "Simulation-routed digital probe.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Bindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class AxisModule(string command) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: command,
                description: "Axis held probe.",
                valueKind: CommandValueKind.Axis1D,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
    private sealed class MappedModule(List<CommandPhase> phases) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "mapped.hold",
                description: "Map release probe.",
                valueKind: CommandValueKind.Axis1D,
                handler: context => {
                    phases.Add(item: context.Phase);

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable,
                map: "play"
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
