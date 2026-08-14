namespace Puck.Commands;

/// <summary>
/// One logical player slot's command state for a single tick: the active commands (held + this-tick) and their
/// edges. Keyed by <see cref="Slot"/> — a stable logical index, <em>not</em> an <see cref="InputDeviceId"/>
/// (device ids differ per machine) — so the lane is the unit a peer transmits and a recording stores.
/// </summary>
/// <remarks>
/// Construction is INTERNAL, like <see cref="CommandEntry"/>'s: a lane is what binds entries to the slot they act
/// on, so a hand-built lane could re-address already-stamped entries to a slot of the builder's choosing, or replay
/// them, without passing an ingress door. The <see cref="InputRouter"/>'s mixer is the only builder.
/// </remarks>
public readonly record struct CommandLane {
    /// <summary>Initializes a new instance of the <see cref="CommandLane"/> struct.</summary>
    /// <param name="slot">The logical player slot this lane belongs to.</param>
    /// <param name="entries">The slot's active command entries in semantic application order.</param>
    internal CommandLane(int slot, CommandBuffer<CommandEntry> entries) {
        Entries = entries;
        Slot = slot;
    }

    /// <summary>The slot's active command entries in semantic application order: carried state first, then this tick's
    /// captured edges and injections in FIFO order. Repeated command ids are allowed and significant.</summary>
    public CommandBuffer<CommandEntry> Entries { get; internal init; }

    /// <summary>The logical player slot this lane belongs to.</summary>
    public int Slot { get; internal init; }

    /// <summary>Compares deterministic lane content structurally.</summary>
    public bool Equals(CommandLane other) => (Slot == other.Slot) && Entries.Equals(other: other.Entries);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Slot, Entries);

    /// <summary>Finds the final entry for a command id, if the slot has one active this tick.</summary>
    /// <param name="commandId">The interned command id to look up.</param>
    /// <param name="entry">The matching entry when found.</param>
    /// <returns><see langword="true"/> if an entry for <paramref name="commandId"/> is present.</returns>
    public bool TryGetEntry(ushort commandId, out CommandEntry entry) {
        if (!Entries.IsEmpty) {
            // Scan backward because repeated entries are ordered events and the final one is the command state a
            // polling projection should observe after the tick is applied.
            for (var index = (Entries.Length - 1); (index >= 0); index--) {
                if (Entries[index].CommandId == commandId) {
                    entry = Entries[index];

                    return true;
                }
            }
        }

        entry = default;

        return false;
    }
}
