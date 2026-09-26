namespace Puck.Commands;

// The carried half of a snapshot: what every tick's lanes start from before its due input folds in — the held
// bindings and the host-sustained commands.
public sealed partial class InputRouter {
    // Host-sustained values, keyed by (slot, command id) and guarded by m_captureGate: a host producer writes them
    // between frames and every snapshot reads them.
    private readonly Dictionary<HeldCommand, CommandValue> m_sustained = [];

    // Held once, so a tick's sort allocates no delegate.
    private static readonly Comparison<CommandEntry> CarriedOrder = static (left, right) => {
        var byCommand = left.CommandId.CompareTo(value: right.CommandId);

        return ((byCommand != 0)
            ? byCommand
            : StringComparer.Ordinal.Compare(
                x: left.Source,
                y: right.Source
            )
        );
    };

    // Seeds each slot's working lane for a tick from carried state: held digitals re-assert as Active, every held
    // contribution re-asserts, and every host-sustained command appears Active with its value. Each lane is then put in
    // (command id, source) order, so the carried half of a snapshot never depends on dictionary order.
    private void SeedCarried() {
        foreach (var working in m_workingBySlot.Values) {
            working.Clear();
        }

        foreach (var (slot, held) in m_heldBySlot) {
            if (held.Count == 0) {
                continue;
            }

            var working = WorkingFor(
                slot: slot,
                workingBySlot: m_workingBySlot
            );

            foreach (var state in held.Values) {
                if (state.HasEntry) {
                    working.Add(item: state.Entry);
                }

                if (state.Contributions is { } contributions) {
                    foreach (var contribution in contributions) {
                        working.Add(item: contribution.Entry);
                    }
                }
            }
        }

        lock (m_captureGate) {
            foreach (var (held, value) in m_sustained) {
                WorkingFor(
                    slot: held.Slot,
                    workingBySlot: m_workingBySlot
                ).Add(item: new CommandEntry(
                    commandId: held.CommandId,
                    origin: CommandOrigin.Binding,
                    phase: CommandPhase.Active,
                    value: value
                ));
            }
        }

        foreach (var working in m_workingBySlot.Values) {
            if (working.Count > 1) {
                working.Sort(comparison: CarriedOrder);
            }
        }
    }

    /// <summary>Sustains a host-produced value on a seat's lane: every snapshot from the next one on carries the command,
    /// phase <see cref="CommandPhase.Active"/>, with this value, however many ticks a frame's catch-up runs, until
    /// <see cref="EndSustain"/> ends it or another call replaces the value. It is not a binding: it reaches the lane
    /// whatever the seat's active maps, and the entry is unstamped, so snapshot construction resolves the seat's current
    /// principal exactly as it does for physical input. A host producer, such as a pointer ray cast through a seat's
    /// camera, owns the value; <see cref="ReleaseHeld()"/> and <see cref="ClearSlotHeld"/> leave it standing.</summary>
    /// <param name="slot">The logical seat whose lane carries the command.</param>
    /// <param name="command">The registered command name.</param>
    /// <param name="value">The command's value on every tick until the sustain ends or changes.</param>
    /// <returns><see langword="false"/> when the command is not registered in this router.</returns>
    /// <exception cref="ObjectDisposedException">This router has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative.</exception>
    public bool Sustain(int slot, string command, CommandValue value) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: command);
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);

        if (!m_registry.TryGetId(
            id: out var commandId,
            name: command
        )) {
            return false;
        }

        lock (m_captureGate) {
            m_sustained[new HeldCommand(
                CommandId: commandId,
                Slot: slot
            )] = value;
        }

        return true;
    }
    /// <summary>Ends a <see cref="Sustain"/>: snapshots from the next one on no longer carry the command for the seat.
    /// Ending a command the seat does not sustain changes nothing.</summary>
    /// <param name="slot">The logical seat.</param>
    /// <param name="command">The registered command name.</param>
    /// <returns><see langword="true"/> when the seat sustained the command.</returns>
    /// <exception cref="ObjectDisposedException">This router has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public bool EndSustain(int slot, string command) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: command);

        if (!m_registry.TryGetId(
            id: out var commandId,
            name: command
        )) {
            return false;
        }

        lock (m_captureGate) {
            return m_sustained.Remove(key: new HeldCommand(
                CommandId: commandId,
                Slot: slot
            ));
        }
    }
}
