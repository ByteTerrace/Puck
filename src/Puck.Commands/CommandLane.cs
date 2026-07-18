using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>
/// One logical player slot's command state for a single tick: the active commands (held + this-tick) and their
/// edges. Keyed by <see cref="Slot"/> — a stable logical index, <em>not</em> an <see cref="InputDeviceId"/>
/// (device ids differ per machine) — so the lane is the unit a peer transmits and a recording stores.
/// </summary>
/// <param name="Slot">The logical player slot this lane belongs to.</param>
/// <param name="Entries">The slot's active command entries in semantic application order: carried state first, then
/// this tick's captured edges and injections in FIFO order. Repeated command ids are allowed and significant.</param>
public readonly record struct CommandLane(int Slot, ImmutableArray<CommandEntry> Entries) {
    /// <summary>Finds the final entry for a command id, if the slot has one active this tick.</summary>
    /// <param name="commandId">The interned command id to look up.</param>
    /// <param name="entry">The matching entry when found.</param>
    /// <returns><see langword="true"/> if an entry for <paramref name="commandId"/> is present.</returns>
    public bool TryGetEntry(ushort commandId, out CommandEntry entry) {
        if (!Entries.IsDefaultOrEmpty) {
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
