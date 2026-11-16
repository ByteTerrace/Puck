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

    private static readonly WorkKindRegistry Registry = new();

    /// <summary>Gets the kind counting the cell lanes the arena's reads visit: each lane a member swap or move reads,
    /// each vector component it carries, each board cell a board read copies or a derived cell's recompute scans, each
    /// key a reindex or a positional key lookup reads, each handle a pool snapshot copies, and each position a retained
    /// turn's rewind restores. Writes are the journal's to count.</summary>
    public static WorkKind Visits { get; } = Registry.Declare(name: "state.arena.visits", unit: "lanes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the write-set membership tests change windows make as they close: one per row
    /// written opaquely and one per retained position judged, whatever the width of the set it is judged against.</summary>
    public static WorkKind ChangeWindowProbes { get; } = Registry.Declare(name: "state.arena.change-window-probes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the elements the arena's <see cref="ArenaScratch"/> leases hand out, each one
    /// cleared before it is handed out.</summary>
    public static WorkKind ScratchLeasedElements { get; } = Registry.Declare(name: "state.arena.scratch-leased-elements", unit: "elements", workClass: WorkClass.Deterministic);

    /// <summary>Gets the kind counting row version moves: one each time a row's version moves, and one for a wholesale
    /// reset (<see cref="StateArena.MoveSequence"/>). Beside the rows a reader is handed, it shows how many moves
    /// landed on rows that had already moved.</summary>
    public static WorkKind RowVersionMoves { get; } = Registry.Declare(name: "state.arena.row-version-moves", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the reads of the row version moves that answered "every row": a reader that
    /// had read nothing yet, or one whose place a wholesale reset (an adoption, a rebuild) passed
    /// (<see cref="StateArena.TryReadMoves"/>). A reader in step with the arena never adds to it, however many moves
    /// fall between its reads. Which readers ask, and when, follows delivery pacing.</summary>
    public static WorkKind MoveReadsWhole { get; } = Registry.Declare(name: "state.arena.move-reads-whole", unit: "count", workClass: WorkClass.Pacing);

    /// <summary>Gets the arena's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds =>
        Registry.Kinds;
}
