using Puck.Abstractions.Counting;

namespace Puck.Physics.Navigation;

/// <summary>
/// The deterministic work <see cref="NavigationRuntime"/> spends finding the way, counted in a
/// <see cref="ShardedWorkCounterSet"/> (each thread counts into a shard of its own) and read through
/// <see cref="IWorkCounterSource"/> under the source name <see cref="SourceName"/>: independent route searches and the
/// nodes they settle, cross-domain route searches, goal fields derived and the cells they settle, the steps bodies read
/// from goal fields, and the fields a full cache dropped.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. A host registers
/// <see cref="Process"/>; a law hands its runtime a ledger of its own, so a sibling test running in parallel cannot move
/// its counts.</remarks>
public sealed class NavigationSearchWork : WorkLedger<ShardedWorkCounterSet> {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "physics.navigation";

    /// <summary>Gets the kind counting independent bounded A* searches, one per <see cref="NavigationRuntime.Domain.FindPath"/>.</summary>
    public static WorkKind Searches { get; } = new(name: "physics.navigation.searches", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the nodes independent searches settled.</summary>
    public static WorkKind SearchExpansions { get; } = new(name: "physics.navigation.search-expansions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting cross-domain route searches, one per <see cref="NavigationRuntime.FindRoute"/>.</summary>
    public static WorkKind Routes { get; } = new(name: "physics.navigation.routes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the nodes cross-domain route searches settled.</summary>
    public static WorkKind RouteExpansions { get; } = new(name: "physics.navigation.route-expansions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting goal fields derived: a goal read for the first time, again after the cache dropped
    /// it, or after a live medium it crosses changed.</summary>
    public static WorkKind FieldBuilds { get; } = new(name: "physics.navigation.field-builds", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the cells goal-field derivations settled.</summary>
    public static WorkKind FieldCells { get; } = new(name: "physics.navigation.field-cells", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the steps read from goal fields, one per <see cref="NavigationRuntime.GoalStep"/>.</summary>
    public static WorkKind FieldSteps { get; } = new(name: "physics.navigation.field-steps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting goal fields dropped to keep the resident fields inside
    /// <see cref="NavigationCapacity.GoalFieldBytes"/>.</summary>
    public static WorkKind FieldEvictions { get; } = new(name: "physics.navigation.field-evictions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger a host's runtimes count into. Declared after the kinds, which its counter set reads as it
    /// is created.</summary>
    public static NavigationSearchWork Process { get; } = new();

    /// <summary>Initializes a new instance of the <see cref="NavigationSearchWork"/> class, every kind at zero.</summary>
    public NavigationSearchWork() : base(counters: new(
        kinds: [Searches, SearchExpansions, Routes, RouteExpansions, FieldBuilds, FieldCells, FieldSteps, FieldEvictions],
        name: SourceName
    )) { }

    /// <summary>Adds to one of this ledger's kinds on the calling thread's shard.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <param name="amount">The count to add; zero adds nothing.</param>
    public void Add(WorkKind kind, long amount) {
        if (amount != 0) { Counters.Add(amount: amount, kind: kind); }
    }
}
