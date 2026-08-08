using System.Numerics;
using Puck.Commands;

namespace Puck.Post;

/// <summary>Tier-A proof for the transactional input-slot, focus-loss cancellation, replay, and stdin FIFO contracts.</summary>
internal sealed class InputRoutingStage : IPostStage {
    private const string AnalogCommand = "input-routing.analog";
    private const string BoundCommand = "input-routing.bound";
    private const string HoldCommand = "input-routing.hold";
    private const string InactiveCommand = "input-routing.inactive";
    private const string MutateCommand = "input-routing.mutate";
    private const string QueryCommand = "input-routing.query";

    /// <inheritdoc/>
    public string Name => "input-routing";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        _ = context;

        if (VerifyTransactionalSlotsAndReplay() is { } slotFailure) {
            return PostStageOutcome.Fail(detail: slotFailure);
        }

        if (VerifyFocusLossCancellation() is { } focusFailure) {
            return PostStageOutcome.Fail(detail: focusFailure);
        }

        if (VerifyDeviceReassignmentCancellation() is { } reassignmentFailure) {
            return PostStageOutcome.Fail(detail: reassignmentFailure);
        }

        if (VerifyConsoleFifo() is { } fifoFailure) {
            return PostStageOutcome.Fail(detail: fifoFailure);
        }

        return PostStageOutcome.Pass(detail: "unbound/inactive signals cannot claim seats; first-seat semantics survive recording; focus loss and device reassignment each emit one logical cancellation without poisoning the next press; deferred mutation→query stdin order is FIFO");
    }

    private static string? VerifyTransactionalSlotsAndReplay() {
        var registry = new CommandRegistry(modules: [new RoutingModule()]);
        var bindings = new SharedInputBindings(bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.Ordinal) {
            ["bound"] = [new CommandBinding(Command: BoundCommand)],
            ["inactive"] = [new CommandBinding(Command: InactiveCommand)],
        });
        var resolver = new SequentialSlotResolver();
        var router = new InputRouter(bindings: bindings, registry: registry, slotResolver: resolver);
        var unboundDevice = Device(value: 1);
        var inactiveDevice = Device(value: 2);
        var firstBoundDevice = Device(value: 3);
        var secondBoundDevice = Device(value: 4);

        router.Capture(signal: new InputSignal(
            Source: "sensor",
            DeviceId: unboundDevice,
            Value: CommandValue.Axis(value: Vector3.One),
            Phase: CommandPhase.Active,
            CaptureTick: 0UL
        ));
        router.Capture(signal: InputSignal.Press(source: "inactive", deviceId: inactiveDevice, captureTick: 0UL));
        router.Capture(signal: InputSignal.Press(source: "bound", deviceId: firstBoundDevice, captureTick: 0UL));

        var first = router.SnapshotForTick(tick: 0UL, windowEndTick: 1UL);

        if (!first.TryGetLane(slot: 0, lane: out var firstLane) || (first.Lanes.Length != 1) ||
            (firstLane.Entries.Length != 1) || !firstLane.Entries[0].AssignedSlot ||
            !registry.TryGetId(name: BoundCommand, id: out var boundId) || (firstLane.Entries[0].CommandId != boundId)) {
            return "transactional slot failure: the first bound active-map signal did not become the sole newly-assigned slot-0 entry";
        }

        router.Capture(signal: InputSignal.Press(source: "bound", deviceId: secondBoundDevice, captureTick: 1UL));
        var second = router.SnapshotForTick(tick: 1UL, windowEndTick: 2UL);

        if (!second.TryGetLane(slot: 1, lane: out var secondLane) ||
            !secondLane.Entries.Any(predicate: entry => ((entry.CommandId == boundId) && entry.AssignedSlot))) {
            return "transactional slot failure: an unbound or inactive-map signal consumed a seat before the second bound device arrived";
        }

        using var stream = new MemoryStream();

        SnapshotRecording.Write(
            stream: stream,
            recording: new SnapshotRecording { Seed = 7u, Snapshots = [first], },
            registry: registry
        );
        stream.Position = 0L;
        var replay = SnapshotRecording.Read(stream: stream, registry: registry);

        if (!replay.Snapshots[0].TryGetLane(slot: 0, lane: out var replayLane) || !replayLane.Entries[0].AssignedSlot) {
            return "recording failure: first-seat snapshot semantics were lost during write/read";
        }

        return null;
    }
    private static string? VerifyFocusLossCancellation() {
        var observations = new RoutingObservations();
        var registry = new CommandRegistry(modules: [new RoutingModule(observations: observations)]);
        var bindings = new SharedInputBindings(bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.Ordinal) {
            ["hold-a"] = [new CommandBinding(Command: HoldCommand)],
            ["hold-b"] = [new CommandBinding(Command: HoldCommand)],
            ["analog"] = [new CommandBinding(Command: AnalogCommand)],
        });
        var router = new InputRouter(bindings: bindings, registry: registry);

        router.Capture(signal: InputSignal.Press(source: "hold-a", captureTick: 0UL));
        router.Capture(signal: InputSignal.Press(source: "hold-b", captureTick: 0UL));
        router.Capture(signal: InputSignal.Axis(source: "analog", value: Vector2.One, captureTick: 0UL));
        var started = router.SnapshotForTick(tick: 0UL, windowEndTick: 1UL);

        registry.ApplySnapshot(snapshot: in started);

        var carried = router.SnapshotForTick(tick: 1UL, windowEndTick: 2UL);

        registry.ApplySnapshot(snapshot: in carried);

        router.ReleaseHeld();
        var canceled = router.SnapshotForTick(tick: 2UL, windowEndTick: 3UL);

        registry.ApplySnapshot(snapshot: in canceled);
        router.ReleaseHeld();
        var repeated = router.SnapshotForTick(tick: 3UL, windowEndTick: 4UL);

        registry.ApplySnapshot(snapshot: in repeated);

        if ((observations.HoldPhases.Count(predicate: phase => (phase == CommandPhase.Started)) != 1) ||
            (observations.HoldPhases.Count(predicate: phase => (phase == CommandPhase.Canceled)) != 1)) {
            return "focus-loss failure: two physical holds of one logical command did not produce exactly one press and one cancellation";
        }

        if ((observations.AnalogPhases.Count(predicate: phase => (phase == CommandPhase.Active)) != 2) ||
            (observations.AnalogPhases.Count(predicate: phase => (phase == CommandPhase.Canceled)) != 1) ||
            !observations.AnalogCanceledInactive) {
            return "focus-loss failure: carried analog input did not reassert once and then cancel with an inactive value";
        }

        if (!repeated.Lanes.IsDefaultOrEmpty) {
            return "focus-loss failure: repeated ReleaseHeld emitted duplicate cancellation edges";
        }

        return null;
    }
    private static string? VerifyDeviceReassignmentCancellation() {
        var observations = new RoutingObservations();
        var registry = new CommandRegistry(modules: [new RoutingModule(observations: observations)]);
        var bindings = new SharedInputBindings(bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.Ordinal) {
            ["hold-a"] = [new CommandBinding(Command: HoldCommand)],
            ["hold-b"] = [new CommandBinding(Command: HoldCommand)],
        });
        var resolver = new SequentialSlotResolver();
        var router = new InputRouter(bindings: bindings, registry: registry, slotResolver: resolver);
        var device = Device(value: 5);

        router.Capture(signal: InputSignal.Press(source: "hold-a", deviceId: device, captureTick: 0UL));
        router.Capture(signal: InputSignal.Press(source: "hold-b", deviceId: device, captureTick: 0UL));
        var started = router.SnapshotForTick(tick: 0UL, windowEndTick: 1UL);

        registry.ApplySnapshot(snapshot: in started);
        resolver.Reassign(device: device, slot: 1);
        var canceled = router.SnapshotForTick(tick: 1UL, windowEndTick: 2UL);

        registry.ApplySnapshot(snapshot: in canceled);

        if (!canceled.TryGetLane(slot: 0, lane: out var canceledLane) || (canceled.Lanes.Length != 1) ||
            (canceledLane.Entries.Length != 1) || (canceledLane.Entries[0].Phase != CommandPhase.Canceled) ||
            !canceledLane.Entries[0].Dispatch || canceledLane.Entries[0].Value.IsActive) {
            return "device-reassignment failure: two physical holds of one logical command did not collapse to one inactive cancellation on the old slot";
        }

        router.Capture(signal: InputSignal.Release(source: "hold-a", deviceId: device, captureTick: 2UL));
        router.Capture(signal: InputSignal.Release(source: "hold-b", deviceId: device, captureTick: 2UL));
        var released = router.SnapshotForTick(tick: 2UL, windowEndTick: 3UL);

        registry.ApplySnapshot(snapshot: in released);
        resolver.Reassign(device: device, slot: 0);
        router.Capture(signal: InputSignal.Press(source: "hold-a", deviceId: device, captureTick: 3UL));
        var restarted = router.SnapshotForTick(tick: 3UL, windowEndTick: 4UL);

        registry.ApplySnapshot(snapshot: in restarted);

        if ((observations.HoldPhases.Count(predicate: phase => (phase == CommandPhase.Started)) != 2) ||
            (observations.HoldPhases.Count(predicate: phase => (phase == CommandPhase.Canceled)) != 1)) {
            return "device-reassignment failure: stale router state swallowed the first press after the device returned to its original slot";
        }

        if (!restarted.TryGetLane(slot: 0, lane: out var restartedLane) ||
            !restartedLane.Entries.Any(predicate: entry => ((entry.Phase == CommandPhase.Started) && entry.Dispatch))) {
            return "device-reassignment failure: the returning device did not produce a dispatched press edge on its original slot";
        }

        return null;
    }
    private static string? VerifyConsoleFifo() {
        var observations = new RoutingObservations();
        var registry = new CommandRegistry(modules: [new RoutingModule(observations: observations)]);
        var router = new InputRouter(bindings: new SharedInputBindings(bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>()), registry: registry);
        var text = new TextCommandSource(registry: registry);

        registry.RouteSimulationTo(sink: router);
        registry.AddSource(source: text);
        text.Enqueue(line: MutateCommand);
        text.Enqueue(line: QueryCommand);
        registry.Collect();

        if (observations.Events.Count != 0) {
            return "stdin FIFO failure: a deferred mutation or the following query ran inline during collection";
        }

        var mutation = router.SnapshotForTick(tick: 0UL, windowEndTick: 1UL);

        registry.ApplySnapshot(snapshot: in mutation);
        registry.Collect();

        return (((observations.Events.Count == 2) && (observations.Events[0] == "mutate") && (observations.Events[1] == "query:1"))
            ? null
            : $"stdin FIFO failure: expected mutate → query:1, observed {string.Join(separator: " → ", values: observations.Events)}");
    }
    private static InputDeviceId Device(int value) => new(Value: new Guid(a: value, b: 0, c: 0, d: new byte[8]));

    private sealed class SequentialSlotResolver : IInputSlotResolver {
        private readonly Dictionary<InputDeviceId, int> m_assignments = [];

        public event Action<InputDeviceId>? DeviceSlotChanging;

        public int ResolveSlot(InputDeviceId device) {
            if (m_assignments.TryGetValue(key: device, value: out var assigned)) {
                return assigned;
            }

            for (var slot = 0; (slot < 4); slot++) {
                if (!m_assignments.ContainsValue(value: slot)) {
                    return slot;
                }
            }

            return -1;
        }
        public bool CommitSlot(InputDeviceId device, int slot) {
            if (m_assignments.ContainsKey(key: device) || (ResolveSlot(device: device) != slot)) {
                return false;
            }

            m_assignments[device] = slot;

            return true;
        }
        public void Reassign(InputDeviceId device, int slot) {
            if (!m_assignments.TryGetValue(key: device, value: out var current) || (current == slot)) {
                throw new InvalidOperationException(message: "The routing probe can only move an assigned device to a different slot.");
            }

            DeviceSlotChanging?.Invoke(obj: device);
            m_assignments[device] = slot;
        }
    }
    private sealed class RoutingObservations {
        public bool AnalogCanceledInactive { get; set; }
        public List<CommandPhase> AnalogPhases { get; } = [];
        public List<string> Events { get; } = [];
        public List<CommandPhase> HoldPhases { get; } = [];
        public int State { get; set; }
    }
    private sealed class RoutingModule(RoutingObservations? observations = null) : ICommandModule {
        private readonly RoutingObservations m_observations = (observations ?? new RoutingObservations());

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: BoundCommand,
                description: "Active-map slot-assignment probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None
            );
            yield return CommandDefinition.Verb(
                name: InactiveCommand,
                description: "Inactive-map slot-assignment probe.",
                valueKind: CommandValueKind.Digital,
                map: "Inactive",
                handler: static _ => CommandResult.None
            );
            yield return CommandDefinition.Verb(
                name: HoldCommand,
                description: "Logical held-input cancellation probe.",
                valueKind: CommandValueKind.Digital,
                handler: context => {
                    m_observations.HoldPhases.Add(item: context.Phase);

                    return CommandResult.None;
                }
            );
            yield return CommandDefinition.Verb(
                name: AnalogCommand,
                description: "Analog carry cancellation probe.",
                valueKind: CommandValueKind.Axis2D,
                handler: context => {
                    m_observations.AnalogPhases.Add(item: context.Phase);
                    m_observations.AnalogCanceledInactive |= ((context.Phase == CommandPhase.Canceled) && !context.Value.IsActive);

                    return CommandResult.None;
                }
            );
            yield return CommandDefinition.Verb(
                name: MutateCommand,
                description: "Deferred stdin mutation probe.",
                valueKind: CommandValueKind.Digital,
                routing: CommandRouting.Simulation,
                handler: _ => {
                    m_observations.State = 1;
                    m_observations.Events.Add(item: "mutate");

                    return CommandResult.None;
                }
            );
            yield return CommandDefinition.Verb(
                name: QueryCommand,
                description: "Immediate stdin read-after-write probe.",
                valueKind: CommandValueKind.Digital,
                handler: _ => {
                    m_observations.Events.Add(item: $"query:{m_observations.State}");

                    return CommandResult.None;
                }
            );
        }
    }
}
