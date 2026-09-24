using Puck.Abstractions.Counting;

namespace Puck.State;

/// <summary>
/// The kinds of work a <see cref="StateArena"/> counts and reports through its <see cref="IWorkCounterSource"/>.
/// Each is a monotonic total over the arena's life, read as the difference across the work being measured, and none
/// is part of simulation state: nothing hashes, exports, or checkpoints them.
/// </summary>
public static class ArenaWork {
    /// <summary>The name a counters report heads an arena's section with.</summary>
    public const string SourceName = "state.arena";

    /// <summary>Gets the kind counting the write-set membership tests change windows make as they close: one per row
    /// written opaquely and one per retained position judged, whatever the width of the set it is judged against.</summary>
    public static WorkKind ChangeWindowProbes { get; } = new(name: "state.arena.change-window-probes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the elements the arena's <see cref="ArenaScratch"/> leases hand out, each one
    /// cleared before it is handed out.</summary>
    public static WorkKind ScratchLeasedElements { get; } = new(name: "state.arena.scratch-leased-elements", unit: "elements", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the cell lanes the arena's reads visit: each lane a member swap or move reads,
    /// each vector component it carries, each board cell a board read copies or a derived cell's recompute scans, each
    /// key a reindex or a positional key lookup reads, each handle a pool snapshot copies, and each position a retained
    /// turn's rewind restores. Writes are the journal's to count.</summary>
    public static WorkKind Visits { get; } = new(name: "state.arena.visits", unit: "lanes", workClass: WorkClass.Deterministic);

    /// <summary>Gets the arena's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds =>
        Order.Kinds;

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Visits,
            ChangeWindowProbes,
            ScratchLeasedElements,
        ];
    }
}
