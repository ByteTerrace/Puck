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
/// Held semantics mirror the registry's existing model so behavior is preserved: a digital press persists
/// (re-asserted every tick as <see cref="CommandPhase.Active"/>) until its release, while axes and text are
/// transient (present only in the ticks a signal arrived). Dispatch is <em>not</em> performed here — the
/// router only produces the deterministic per-tick state; the consumer runs handlers from the snapshot.
/// Pre-resolved commands (a console / STDIN line, a peer, an AI) enter through <see cref="Inject"/> and fold into
/// the same lanes as captured signals, in one deterministic capture order — so command-line input is recorded and
/// replayed by the same machinery, with no separate path.
/// </remarks>
public sealed class InputRouter : ISnapshotSource, ICommandInjectionSink {
    private readonly IInputBindings m_bindings;
    private readonly Lock m_captureGate = new();
    private readonly List<Captured> m_captured = [];
    private readonly IInputClock? m_clock;
    private readonly Dictionary<int, Dictionary<ushort, CommandEntry>> m_heldBySlot = [];
    private readonly CommandRegistry m_registry;
    private readonly Func<InputDeviceId, int> m_slotResolver;
    private ulong m_sequence;

    // One captured item carries EITHER a raw signal (still needs a binding lookup) or a pre-resolved injection
    // (a console/peer command, already bound). Both share the capture tick + sequence, so they sort into one
    // deterministic order regardless of which kind they are.
    private readonly record struct Captured(ulong Sequence, ulong CaptureTick, InputSignal? Signal, CommandInjection? Injection);

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
        m_clock = clock;
        m_registry = registry;
        m_slotResolver = (slotResolver ?? (static _ => 0));
    }

    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    public void Capture(in InputSignal signal) {
        lock (m_captureGate) {
            m_captured.Add(item: new Captured(Sequence: m_sequence++, CaptureTick: signal.CaptureTick, Signal: signal, Injection: null));
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

        // Working per-slot edge state for this tick: command id -> (value, phase). Seeded from carried held
        // state (held digitals re-assert as Active), then each due signal is applied in order.
        var workingBySlot = new Dictionary<int, Dictionary<ushort, CommandEntry>>();

        foreach (var (slot, held) in m_heldBySlot) {
            var working = WorkingFor(workingBySlot: workingBySlot, slot: slot);

            foreach (var (commandId, heldEntry) in held) {
                // The held entry is already phase Active — a held digital re-asserts each tick.
                working[commandId] = heldEntry;
            }
        }

        foreach (var captured in due) {
            if (captured.Signal is InputSignal signal) {
                ApplySignal(workingBySlot: workingBySlot, signal: signal);
            } else if (captured.Injection is CommandInjection injection) {
                ApplyInjection(workingBySlot: workingBySlot, injection: injection);
            }
        }

        return Build(tick: tick, workingBySlot: workingBySlot);
    }

    private List<Captured> DrainDue(ulong windowEndTick) {
        var due = new List<Captured>();

        lock (m_captureGate) {
            if (m_captured.Count == 0) {
                return due;
            }

            var kept = 0;

            for (var index = 0; (index < m_captured.Count); index++) {
                var captured = m_captured[index];

                if (captured.CaptureTick < windowEndTick) {
                    due.Add(item: captured);
                } else {
                    m_captured[kept++] = captured;
                }
            }

            m_captured.RemoveRange(index: kept, count: (m_captured.Count - kept));
        }

        return due;
    }

    // Folds a pre-resolved command directly into its slot's lane for this tick — no binding lookup (it is already
    // bound) and no held bookkeeping: an injection is one-shot, present only in the tick its capture window placed
    // it, with the caller-chosen edge. A held console input is expressed as an explicit Started/Completed pair.
    private static void ApplyInjection(Dictionary<int, Dictionary<ushort, CommandEntry>> workingBySlot, CommandInjection injection) {
        var working = WorkingFor(workingBySlot: workingBySlot, slot: injection.Slot);

        working[injection.CommandId] = new CommandEntry(CommandId: injection.CommandId, Device: default, Phase: injection.Phase, Value: injection.Value);
    }

    private void ApplySignal(Dictionary<int, Dictionary<ushort, CommandEntry>> workingBySlot, InputSignal signal) {
        // Resolve the device's slot first, then ask for THAT slot's bindings — so each player's mapping (an
        // optional override layered over the engine default) drives their own input.
        var slot = m_slotResolver(arg: signal.DeviceId);
        var bindings = m_bindings.Resolve(slot: slot, signal: signal);

        if (bindings is null) {
            return;
        }

        foreach (var binding in bindings) {
            // A binding answers exactly its chord (modifiers must match), and only known commands fold in.
            if (binding.RequiredModifiers != signal.Modifiers) {
                continue;
            }

            if (!m_registry.TryGetId(
                name: binding.Command,
                id: out var commandId
            )) {
                continue;
            }

            var value = (binding.Value ?? signal.Value);
            var working = WorkingFor(workingBySlot: workingBySlot, slot: slot);
            var entry = new CommandEntry(CommandId: commandId, Device: signal.DeviceId, Phase: signal.Phase, Value: value);

            working[commandId] = entry;

            // Persist held digitals across ticks so a polling consumer sees them down; clear on release/cancel.
            // Axes and text are transient (no held entry), so they appear only in the ticks they arrive.
            if (
                (signal.Phase == CommandPhase.Started) &&
                value.IsActive &&
                (signal.Text is null)
            ) {
                HeldFor(slot: slot)[commandId] = (entry with { Phase = CommandPhase.Active });
            } else if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
                if (m_heldBySlot.TryGetValue(
                    key: slot,
                    value: out var held
                )) {
                    _ = held.Remove(key: commandId);
                }
            }
        }
    }

    private static CommandSnapshot Build(ulong tick, Dictionary<int, Dictionary<ushort, CommandEntry>> workingBySlot) {
        if (workingBySlot.Count == 0) {
            return CommandSnapshot.Empty(tick: tick);
        }

        var lanes = ImmutableArray.CreateBuilder<CommandLane>(initialCapacity: workingBySlot.Count);

        foreach (var (slot, working) in workingBySlot) {
            if (working.Count == 0) {
                continue;
            }

            // Order entries by command id for a deterministic, hashable lane layout.
            var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: working.Count);

            foreach (var entry in working.Values) {
                entries.Add(item: entry);
            }

            entries.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));

            lanes.Add(item: new CommandLane(Entries: entries.DrainToImmutable(), Slot: slot));
        }

        // Order lanes by slot for a deterministic snapshot layout.
        lanes.Sort(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

        return new CommandSnapshot(Lanes: lanes.DrainToImmutable(), Tick: tick);
    }

    private static Dictionary<ushort, CommandEntry> WorkingFor(Dictionary<int, Dictionary<ushort, CommandEntry>> workingBySlot, int slot) {
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
}
