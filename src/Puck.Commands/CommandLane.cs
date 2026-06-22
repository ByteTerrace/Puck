using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>
/// One logical player slot's command state for a single tick: the active commands (held + this-tick) and their
/// edges. Keyed by <see cref="Slot"/> — a stable logical index, <em>not</em> an <see cref="InputDeviceId"/>
/// (device ids differ per machine) — so the lane is the unit a peer transmits and a recording stores.
/// </summary>
/// <param name="Slot">The logical player slot this lane belongs to.</param>
/// <param name="Entries">The slot's active command entries, ordered by <see cref="CommandEntry.CommandId"/> for a deterministic, hashable layout.</param>
public readonly record struct CommandLane(int Slot, ImmutableArray<CommandEntry> Entries) {
    /// <summary>Finds the entry for a command id, if the slot has one active this tick.</summary>
    /// <param name="commandId">The interned command id to look up.</param>
    /// <param name="entry">The matching entry when found.</param>
    /// <returns><see langword="true"/> if an entry for <paramref name="commandId"/> is present.</returns>
    public bool TryGetEntry(ushort commandId, out CommandEntry entry) {
        if (!Entries.IsDefaultOrEmpty) {
            // Entries are few (the commands a slot drives this tick), so a linear scan beats a binary search and
            // is obviously correct; the CommandId ordering exists for determinism/serialization, not lookup.
            for (var index = 0; (index < Entries.Length); index++) {
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
