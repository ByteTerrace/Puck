using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>
/// The single capture point and per-tick snapshot producer. Every backend appends timestamped
/// <see cref="InputSignal"/>s here from any thread; the host's fixed-step loop pulls one
/// <see cref="CommandSnapshot"/> per tick, draining the captured signals whose
/// <see cref="InputSignal.CaptureTick"/> falls within the tick's window and folding them — through the binding
/// table — into per-slot lanes. Signals stamped at or beyond the window stay for a later tick (so a frame
/// spike delays, never misattributes, recent input). Replaces the bind-once-per-render-frame path.
/// </summary>
/// <remarks>
/// Held semantics mirror a physical control: digital presses and active analog values persist until a release or zero
/// sample clears them. Digital handlers dispatch only on their bound edges; analog handlers re-dispatch their carried
/// sample each tick so route-style consumers receive the continuous value. Text is
/// transient. Dispatch is <em>not</em> performed here — the
/// router only produces the deterministic per-tick state; the consumer runs handlers from the snapshot.
/// Pre-resolved commands (a console / STDIN line, a peer, an AI) enter through <see cref="Inject"/> and fold into
/// the same lanes as captured signals, in one deterministic capture order — so command-line input is recorded and
/// replayed by the same machinery, with no separate path.
/// </remarks>
public sealed class InputRouter : ISnapshotSource, ICommandInjectionSink {
    private readonly IInputBindings m_bindings;
    private readonly IChordEdgeSource? m_chordEdges;
    private readonly Lock m_captureGate = new();
    private readonly List<Captured> m_captured = [];
    // Simulation-thread scratch retained across ticks. Idle snapshots then allocate nothing; active snapshots allocate
    // only their immutable output. Capture remains independently protected by m_captureGate.
    private readonly List<Captured> m_due = [];
    private readonly IInputClock? m_clock;
    private readonly HashSet<HeldControl> m_heldControls = [];
    private readonly Dictionary<int, Dictionary<ushort, CommandEntry>> m_heldBySlot = [];
    private readonly IInputSlotResolver? m_inputSlotResolver;
    private readonly CommandRegistry m_registry;
    private readonly Func<InputDeviceId, int> m_slotResolver;
    private readonly Dictionary<int, List<CommandEntry>> m_workingBySlot = [];
    private ulong m_sequence;

    // One captured item carries EITHER a raw signal (still needs a binding lookup) or a pre-resolved injection
    // (a console/peer command, already bound). Both share the capture tick + sequence, so they sort into one
    // deterministic order regardless of which kind they are.
    private readonly record struct Captured(ulong Sequence, ulong CaptureTick, InputSignal? Signal, CommandInjection? Injection);
    private readonly record struct HeldCommand(int Slot, ushort CommandId);
    private readonly record struct HeldControl(int Slot, InputDeviceId Device, string Source, ushort CommandId);

    /// <summary>Initializes a new instance of the <see cref="InputRouter"/> class.</summary>
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
    /// <param name="bindings">The slot-aware binding resolver (per-player mappings layered over a default).</param>
    /// <param name="slotResolver">Maps a device to a logical player slot; defaults to a single local slot (<c>0</c>).</param>
    /// <param name="clock">The shared capture clock used to stamp an injected command that arrives without an explicit capture tick; optional.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="bindings"/> is <see langword="null"/>.</exception>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        Func<InputDeviceId, int>? slotResolver = null,
        IInputClock? clock = null
    ) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(registry);

        m_bindings = bindings;
        m_chordEdges = (bindings as IChordEdgeSource);
        m_clock = clock;
        m_registry = registry;
        m_slotResolver = (slotResolver ?? (static _ => 0));
    }

    /// <summary>Initializes an input router whose device-to-slot resolver supports side-effect-free probing followed by
    /// an explicit commit after a binding is accepted.</summary>
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
    /// <param name="bindings">The slot-aware binding resolver.</param>
    /// <param name="slotResolver">The transactional device-to-slot resolver.</param>
    /// <param name="clock">The shared capture clock; optional.</param>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        IInputSlotResolver slotResolver,
        IInputClock? clock = null
    ) : this(
        registry: registry,
        bindings: bindings,
        slotResolver: (slotResolver ?? throw new ArgumentNullException(paramName: nameof(slotResolver))).ResolveSlot,
        clock: clock
    ) {
        m_inputSlotResolver = slotResolver;
        slotResolver.DeviceSlotChanging += ReleaseHeld;
    }

    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    public void Capture(in InputSignal signal) {
        lock (m_captureGate) {
            m_captured.Add(item: new Captured(Sequence: m_sequence++, CaptureTick: signal.CaptureTick, Signal: signal, Injection: null));
        }
    }

    /// <summary>Whether a logical command is currently carried held for a slot — a bound digital pressed and not yet
    /// released, or an analog channel with an active carried sample. The read seam an input-state UI (a binding bar's
    /// pressed chips) lights from, so held truth has ONE owner instead of a parallel tracker per consumer.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="command">The command name to test.</param>
    /// <returns><see langword="true"/> when the slot carries the command held.</returns>
    /// <remarks>Pump-thread only: the held tables mutate inside <see cref="SnapshotForTick"/> and the focus-loss
    /// release on the same single thread that produces frames, so this read is safe there and nowhere else.</remarks>
    public bool IsCommandHeld(int slot, string command) {
        return (m_registry.TryGetId(
            name: command,
            id: out var commandId
        )
            && m_heldBySlot.TryGetValue(
                key: slot,
                value: out var held
            )
            && held.ContainsKey(key: commandId));
    }

    /// <summary>Queues one deterministic cancellation per carried logical command, then clears every carried digital
    /// and analog value. Hosts call this on focus loss because platforms do not guarantee release events afterward.</summary>
    public void ReleaseHeld() {
        var cancellations = new List<CommandInjection>();

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var entry in held.Values) {
                cancellations.Add(item: new CommandInjection(
                    CommandId: entry.CommandId,
                    Value: CommandValue.Inactive(kind: entry.Value.Kind),
                    Phase: CommandPhase.Canceled,
                    Slot: slot
                ));
            }
        }

        m_heldControls.Clear();
        m_heldBySlot.Clear();

        QueueCancellations(cancellations: cancellations, discardCapturedSignals: true);
    }

    private void ReleaseHeld(InputDeviceId device) {
        var affected = new HashSet<HeldCommand>();

        foreach (var control in m_heldControls) {
            if (control.Device == device) {
                _ = affected.Add(item: new HeldCommand(Slot: control.Slot, CommandId: control.CommandId));
            }
        }

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var entry in held.Values) {
                if (entry.Device == device) {
                    _ = affected.Add(item: new HeldCommand(Slot: slot, CommandId: entry.CommandId));
                }
            }
        }

        if (affected.Count == 0) {
            return;
        }

        _ = m_heldControls.RemoveWhere(match: control => (control.Device == device));

        var cancellations = new List<CommandInjection>(capacity: affected.Count);

        foreach (var affectedCommand in affected) {
            if (!m_heldBySlot.TryGetValue(key: affectedCommand.Slot, value: out var held) ||
                !held.TryGetValue(key: affectedCommand.CommandId, value: out var entry)) {
                continue;
            }

            if (TryGetHeldDevice(slot: affectedCommand.Slot, commandId: affectedCommand.CommandId, device: out var remainingDevice)) {
                // Another physical control still owns this logical hold. Keep it carried and keep its process-local
                // device annotation truthful for live consumers such as rumble routing.
                held[affectedCommand.CommandId] = (entry with { Device = remainingDevice, });
                continue;
            }

            _ = held.Remove(key: affectedCommand.CommandId);

            if (held.Count == 0) {
                _ = m_heldBySlot.Remove(key: affectedCommand.Slot);
            }

            cancellations.Add(item: new CommandInjection(
                CommandId: affectedCommand.CommandId,
                Value: CommandValue.Inactive(kind: entry.Value.Kind),
                Phase: CommandPhase.Canceled,
                Slot: affectedCommand.Slot
            ));
        }

        QueueCancellations(cancellations: cancellations, discardCapturedSignals: false);
    }

    private void QueueCancellations(List<CommandInjection> cancellations, bool discardCapturedSignals) {
        if ((cancellations.Count == 0) && !discardCapturedSignals) {
            return;
        }

        cancellations.Sort(comparison: static (left, right) => {
            var bySlot = left.Slot.CompareTo(value: right.Slot);

            return ((bySlot != 0) ? bySlot : left.CommandId.CompareTo(value: right.CommandId));
        });

        lock (m_captureGate) {
            if (discardCapturedSignals) {
                // A physical press captured just before focus loss must not become a fresh held input afterward.
                // Console/peer injections are not focus-owned and remain queued.
                m_captured.RemoveAll(match: static captured => (captured.Signal is not null));
            }

            var captureTick = (m_clock?.NowTicks ?? 0UL);

            foreach (var cancellation in cancellations) {
                m_captured.Add(item: new Captured(
                    Sequence: m_sequence++,
                    CaptureTick: captureTick,
                    Signal: null,
                    Injection: cancellation
                ));
            }
        }
    }

    /// <inheritdoc/>
    public void Inject(in CommandInjection injection) {
        // An injection's effect mutates the simulation, so it must attribute to a fixed-step tick. An explicit
        // capture tick (a deterministic script / replay harness) is honored; otherwise the shared capture clock
        // stamps it now, exactly as a backend stamps a physical signal — making console input share one timeline
        // with controllers. Determinism comes from recording the resulting snapshot, not from reproducing the
        // live arrival time (the same guarantee a gamepad press already has).
        var captureTick = ((injection.CaptureTick != 0UL)
            ? injection.CaptureTick
            : (m_clock?.NowTicks ?? 0UL));

        lock (m_captureGate) {
            m_captured.Add(item: new Captured(Sequence: m_sequence++, CaptureTick: captureTick, Signal: null, Injection: injection));
        }
    }

    /// <inheritdoc/>
    public CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick) {
        // Take this tick's due signals (CaptureTick before the window close), leaving later-stamped signals for
        // a future tick. Total order: capture time, then the unique capture sequence — deterministic for a given
        // captured set, so the recorded snapshot reproduces the run exactly.
        var due = DrainDue(windowEndTick: windowEndTick);

        due.Sort(comparison: static (left, right) => {
            var byTime = left.CaptureTick.CompareTo(value: right.CaptureTick);

            return ((byTime != 0)
                ? byTime
                : left.Sequence.CompareTo(value: right.Sequence));
        });

        // Working per-slot ordered state for this tick. Seeded from carried held state (held digitals re-assert as
        // Active), then every due signal is appended in order; repeated commands stay repeated.
        foreach (var working in m_workingBySlot.Values) {
            working.Clear();
        }

        foreach (var (slot, held) in m_heldBySlot) {
            if (held.Count == 0) {
                continue;
            }

            var working = WorkingFor(workingBySlot: m_workingBySlot, slot: slot);

            foreach (var heldEntry in held.Values) {
                // The held entry is already phase Active — a held digital re-asserts each tick.
                working.Add(item: heldEntry);
            }

            working.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));
        }

        foreach (var captured in due) {
            if (captured.Signal is InputSignal signal) {
                ApplySignal(workingBySlot: m_workingBySlot, signal: signal);
            } else if (captured.Injection is CommandInjection injection) {
                ApplyInjection(workingBySlot: m_workingBySlot, injection: injection);
            }
        }

        return Build(tick: tick, workingBySlot: m_workingBySlot);
    }

    private List<Captured> DrainDue(ulong windowEndTick) {
        m_due.Clear();

        lock (m_captureGate) {
            if (m_captured.Count == 0) {
                return m_due;
            }

            var kept = 0;

            for (var index = 0; (index < m_captured.Count); index++) {
                var captured = m_captured[index];

                if (captured.CaptureTick < windowEndTick) {
                    m_due.Add(item: captured);
                } else {
                    m_captured[kept++] = captured;
                }
            }

            m_captured.RemoveRange(index: kept, count: (m_captured.Count - kept));
        }

        return m_due;
    }

    // Folds a pre-resolved command directly into its slot's lane for this tick — no binding lookup (it is already
    // bound) and no held bookkeeping: an injection is one-shot, present only in the tick its capture window placed
    // it, with the caller-chosen edge. A held console input is expressed as an explicit Started/Completed pair.
    private static void ApplyInjection(Dictionary<int, List<CommandEntry>> workingBySlot, CommandInjection injection) {
        var working = WorkingFor(workingBySlot: workingBySlot, slot: injection.Slot);

        working.Add(item: new CommandEntry(
            CommandId: injection.CommandId,
            Device: default,
            Dispatch: true,
            Phase: injection.Phase,
            Text: injection.Text,
            Value: injection.Value
        ) {
            CompletesTextSubmission = injection.CompletesTextSubmission,
        });
    }
    private void ApplySignal(Dictionary<int, List<CommandEntry>> workingBySlot, InputSignal signal) {
        // Resolve the device's slot first, then ask for THAT slot's bindings — so each player's mapping (an
        // optional override layered over the engine default) drives their own input.
        var slot = m_slotResolver(arg: signal.DeviceId);

        if (slot < 0) {
            return;
        }

        var bindings = m_bindings.Resolve(slot: slot, signal: signal);

        if (m_chordEdges is not null) {
            // Chord-command edges synthesized by this signal's resolve fold into the same lane with their OWN
            // phase and value (the physical signal's phase may be a mid-sweep Active) — see IChordEdgeSource.
            foreach (var edge in m_chordEdges.DrainChordEdges(slot: slot)) {
                ApplyChordEdge(workingBySlot: workingBySlot, slot: slot, device: signal.DeviceId, edge: in edge);
            }
        }

        if (bindings is null) {
            return;
        }

        var assignedSlot = false;
        var acceptedBinding = false;

        foreach (var binding in bindings) {
            // A binding answers exactly its chord unless it explicitly accepts incidental modifiers.
            if (!binding.AnyModifiers && (binding.RequiredModifiers != signal.Modifiers)) {
                continue;
            }

            if (!m_registry.TryGetId(
                name: binding.Command,
                id: out var commandId
            )) {
                continue;
            }

            if (!m_registry.IsSourceCommandActive(commandId: commandId)) {
                continue;
            }

            if (!acceptedBinding) {
                assignedSlot = (m_inputSlotResolver?.CommitSlot(device: signal.DeviceId, slot: slot) ?? false);
                acceptedBinding = true;
            }

            var value = (binding.Value ?? signal.Value);
            var working = WorkingFor(workingBySlot: workingBySlot, slot: slot);
            var phase = signal.Phase;
            var dispatch = ((binding.ActivateOn is { } required)
                ? (signal.Phase == required)
                : (signal.Phase is CommandPhase.Started or CommandPhase.Active));
            var heldControl = new HeldControl(Slot: slot, Device: signal.DeviceId, Source: signal.Source, CommandId: commandId);
            var isDigital = (value.Kind == CommandValueKind.Digital);
            var wasCommandHeld = (isDigital && IsCommandHeld(slot: slot, commandId: commandId));
            var active = ((signal.Phase is CommandPhase.Started or CommandPhase.Active) && value.IsActive && (signal.Text is null));

            if (isDigital) {
                if (active) {
                    _ = m_heldControls.Add(item: heldControl);

                    // Two physical controls may bind the same logical command (W + Up). The logical press edge fires
                    // only when the first control goes down.
                    if (wasCommandHeld) {
                        dispatch = false;
                    }
                } else {
                    _ = m_heldControls.Remove(item: heldControl);

                    // Likewise, the logical release edge fires only when the last bound control goes up.
                    if (IsCommandHeld(slot: slot, commandId: commandId)) {
                        dispatch = false;
                        value = m_heldBySlot[slot][commandId].Value;
                        phase = CommandPhase.Active;
                    }
                }
            }

            var entry = new CommandEntry(
                CommandId: commandId,
                Device: signal.DeviceId,
                Dispatch: dispatch,
                Phase: phase,
                Value: value,
                AssignedSlot: assignedSlot
            );

            working.Add(item: entry);

            // Persist held digitals and the latest active analog sample. Reassertions never redispatch handlers; a
            // release/cancel or inactive analog sample clears the carried value.
            if (
                active
            ) {
                HeldFor(slot: slot)[commandId] = (entry with {
                    Dispatch = (value.Kind != CommandValueKind.Digital),
                    Phase = CommandPhase.Active,
                });
            } else if (((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) || !value.IsActive) &&
                (!isDigital || !IsCommandHeld(slot: slot, commandId: commandId))) {
                if (m_heldBySlot.TryGetValue(
                    key: slot,
                    value: out var held
                )) {
                    _ = held.Remove(key: commandId);

                    if (held.Count == 0) {
                        _ = m_heldBySlot.Remove(key: slot);
                    }
                }
            }
        }
    }
    // Folds one synthesized chord-command edge into the slot's lane. The press carries held bookkeeping (so
    // IsCommandHeld lights and focus-loss cancellation covers a chord-held command); the release clears it. The
    // command-availability gate matches the bound path — an inactive-map command's chord is inert, not an error.
    private void ApplyChordEdge(Dictionary<int, List<CommandEntry>> workingBySlot, int slot, InputDeviceId device, in BindingChordEdge edge) {
        if (!m_registry.TryGetId(
            name: edge.Command,
            id: out var commandId
        ) || !m_registry.IsSourceCommandActive(commandId: commandId)) {
            return;
        }

        var entry = new CommandEntry(
            CommandId: commandId,
            Device: device,
            Dispatch: edge.Dispatch,
            Phase: edge.Phase,
            Value: edge.Value
        );

        WorkingFor(workingBySlot: workingBySlot, slot: slot).Add(item: entry);

        if (edge.Phase == CommandPhase.Started) {
            HeldFor(slot: slot)[commandId] = (entry with {
                Dispatch = false,
                Phase = CommandPhase.Active,
            });
        } else if (m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            _ = held.Remove(key: commandId);

            if (held.Count == 0) {
                _ = m_heldBySlot.Remove(key: slot);
            }
        }
    }
    private static CommandSnapshot Build(ulong tick, Dictionary<int, List<CommandEntry>> workingBySlot) {
        if (workingBySlot.Count == 0) {
            return CommandSnapshot.Empty(tick: tick);
        }

        var activeLaneCount = 0;

        foreach (var working in workingBySlot.Values) {
            if (working.Count != 0) {
                activeLaneCount++;
            }
        }

        if (activeLaneCount == 0) {
            return CommandSnapshot.Empty(tick: tick);
        }

        var lanes = ImmutableArray.CreateBuilder<CommandLane>(initialCapacity: activeLaneCount);

        foreach (var (slot, working) in workingBySlot) {
            if (working.Count == 0) {
                continue;
            }

            var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: working.Count);

            // Entry order is semantic: held state is emitted first in command-id order, then due signals/injections in
            // their deterministic capture order. In particular, repeated console verbs in one host frame must remain
            // repeated and FIFO — collapsing by command id would silently drop scripted tape segments.
            foreach (var entry in working) {
                entries.Add(item: entry);
            }

            lanes.Add(item: new CommandLane(Entries: entries.DrainToImmutable(), Slot: slot));
        }

        // Order lanes by slot for a deterministic snapshot layout.
        lanes.Sort(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

        return new CommandSnapshot(Lanes: lanes.DrainToImmutable(), Tick: tick);
    }
    private static List<CommandEntry> WorkingFor(Dictionary<int, List<CommandEntry>> workingBySlot, int slot) {
        if (!workingBySlot.TryGetValue(
            key: slot,
            value: out var working
        )) {
            working = [];
            workingBySlot[slot] = working;
        }

        return working;
    }
    private Dictionary<ushort, CommandEntry> HeldFor(int slot) {
        if (!m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            held = [];
            m_heldBySlot[slot] = held;
        }

        return held;
    }
    private bool IsCommandHeld(int slot, ushort commandId) {
        foreach (var control in m_heldControls) {
            if ((control.Slot == slot) && (control.CommandId == commandId)) {
                return true;
            }
        }

        return false;
    }
    private bool TryGetHeldDevice(int slot, ushort commandId, out InputDeviceId device) {
        foreach (var control in m_heldControls) {
            if ((control.Slot == slot) && (control.CommandId == commandId)) {
                device = control.Device;

                return true;
            }
        }

        device = default;

        return false;
    }
}
