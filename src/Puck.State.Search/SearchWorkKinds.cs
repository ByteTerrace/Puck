using Puck.Abstractions.Counting;

namespace Puck.State;

/// <summary>
/// The kinds of work an <see cref="ArenaSearch"/> counts and reports through its <see cref="IWorkCounterSource"/>,
/// across every job it runs. Each is a monotonic total over the search's life, read as the difference across the work
/// being measured, and none is part of simulation state: nothing hashes, exports, or checkpoints them, and a restart
/// or a restore does not rewind them.
/// </summary>
public static class SearchWorkKinds {
    /// <summary>The name a counters report heads a search's section with.</summary>
    public const string SourceName = "state.search";

    /// <summary>Gets the kind counting judged candidates: one per candidate a job applies and judges, a replay's
    /// included.</summary>
    public static WorkKind Candidates { get; } = new(name: "state.search.candidates", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the nodes a tree job grows: one per accepted candidate kept as a new child.</summary>
    public static WorkKind Expansions { get; } = new(name: "state.search.expansions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting playout plies: one per candidate a tree job's playout accepts and keeps.</summary>
    public static WorkKind PlayoutPlies { get; } = new(name: "state.search.playout-plies", unit: "count", workClass: WorkClass.Deterministic);

    /// <summary>Gets the search's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds =>
        Order.Kinds;

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Candidates,
            Expansions,
            PlayoutPlies,
        ];
    }
}
