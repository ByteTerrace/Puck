using Puck.Abstractions.Counting;

namespace Puck.Physics;

/// <summary>
/// The deterministic work <see cref="FixedFieldContactSolver.ResolveSweep"/> spends proving how far a body may move,
/// counted in a <see cref="WorkCounterSet"/> and read through <see cref="IWorkCounterSource"/> under the source name
/// <see cref="SourceName"/>: the certified sweeps it runs, one per core sphere of each collider volume, the bounds
/// queries they charge to their budget, and how many of them stopped at a possible contact or
/// ended undecided.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. Every kind is
/// <see cref="WorkClass.Deterministic"/>: the same world and input spend the same work on every run and host. A host
/// registers <see cref="Process"/>; a law hands its solver a ledger of its own, so a sibling test running in parallel
/// cannot move its counts.</remarks>
public sealed class FixedContactSweepWork : IWorkCounterSource {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "physics.sweep";

    private readonly WorkCounterSet m_counts = new(
        kinds: [Sweeps, BoundsQueries, Contacts, Exhausted],
        name: SourceName
    );

    /// <summary>Gets the kind counting certified sweeps: one per core sphere of each collider volume a moving body
    /// sweeps.</summary>
    public static WorkKind Sweeps { get; } = new(name: "physics.sweep.sweeps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting logical bounds queries charged to the sweep budget. An immutable field may
    /// reuse an identical box's answer without walking its program again.</summary>
    public static WorkKind BoundsQueries { get; } = new(name: "physics.sweep.bounds-queries", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the sweeps that stopped where a surface may lie within one more step.</summary>
    public static WorkKind Contacts { get; } = new(name: "physics.sweep.contacts", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the sweeps that ended undecided: their bounds-query budget ran out, they reached
    /// a box the field could not bound, or the field refused them outright. The body stopped at the ground they
    /// proved.</summary>
    public static WorkKind Exhausted { get; } = new(name: "physics.sweep.exhausted", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger a host's solvers count into. Declared after the kinds, which its counter set reads as
    /// it is created.</summary>
    public static FixedContactSweepWork Process { get; } = new();

    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => m_counts.WorkKinds;

    /// <summary>Counts one finished sweep.</summary>
    /// <param name="boundsQueries">The bounds queries it spent.</param>
    /// <param name="contact">Whether it stopped at a possible contact.</param>
    /// <param name="exhausted">Whether it ended undecided, out of budget or refused.</param>
    public void Count(int boundsQueries, bool contact, bool exhausted) {
        m_counts.Count(kind: Sweeps);
        m_counts.Add(amount: boundsQueries, kind: BoundsQueries);

        if (contact) {
            m_counts.Count(kind: Contacts);
        }

        if (exhausted) {
            m_counts.Count(kind: Exhausted);
        }
    }
    /// <summary>Reads the current total of one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <returns>The total counted so far.</returns>
    public long Read(WorkKind kind) => m_counts.Read(kind: kind);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind: kind, value: out value);
}
