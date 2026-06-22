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
/// </remarks>
public sealed class InputRouter : ISnapshotSource {
    private readonly IInputBindings m_bindings;
    private readonly Lock m_captureGate = new();
    private readonly List<Captured> m_captured = [];
    private readonly Dictionary<int, Dictionary<ushort, CommandEntry>> m_heldBySlot = [];
    private readonly CommandRegistry m_registry;
    private readonly Func<InputDeviceId, int> m_slotResolver;
    private ulong m_sequence;

    private readonly record struct Captured(ulong Sequence, InputSignal Signal);

    /// <summary>Initializes a new instance of the <see cref="InputRouter"/> class.</summary>
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
    /// <param name="bindings">The slot-aware binding resolver (per-player mappings layered over a default).</param>
    /// <param name="slotResolver">Maps a device to a logical player slot; defaults to a single local slot (<c>0</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="bindings"/> is <see langword="null"/>.</exception>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        Func<InputDeviceId, int>? slotResolver = null
    ) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(registry);

        m_bindings = bindings;
        m_registry = registry;
        m_slotResolver = (slotResolver ?? (static _ => 0));
    }

    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    public void Capture(in InputSignal signal) {
        lock (m_captureGate) {
            m_captured.Add(item: new Captured(Sequence: m_sequence++, Signal: signal));
        }
    }

    /// <inheritdoc/>
    public CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick) {
        // Take this tick's due signals (CaptureTick before the window close), leaving later-stamped signals for
        // a future tick. Total order: capture time, then the unique capture sequence — deterministic for a given
        // captured set, so the recorded snapshot reproduces the run exactly.
        var due = DrainDue(windowEndTick: windowEndTick);

        due.Sort(comparison: static (left, right) => {
            var byTime = left.Signal.CaptureTick.CompareTo(value: right.Signal.CaptureTick);

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
            Apply(workingBySlot: workingBySlot, signal: captured.Signal);
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

                if (captured.Signal.CaptureTick < windowEndTick) {
                    due.Add(item: captured);
                } else {
                    m_captured[kept++] = captured;
                }
            }

            m_captured.RemoveRange(index: kept, count: (m_captured.Count - kept));
        }

        return due;
    }

    private void Apply(Dictionary<int, Dictionary<ushort, CommandEntry>> workingBySlot, InputSignal signal) {
        // Resolve the device's slot first, then ask for THAT slot's bindings — so each player's mapping (an
        // optional override layered over the engine default) drives their own input.
        var slot = m_slotResolver(arg: signal.DeviceId);
        var bindings = m_bindings.Resolve(slot: slot, source: signal.Source);

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
