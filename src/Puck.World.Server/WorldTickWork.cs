using Puck.Abstractions.Counting;

namespace Puck.World.Server;

/// <summary>
/// The work a tick spends scanning the population rather than stepping it, counted in a
/// <see cref="WorkCounterSet"/> and read through <see cref="IWorkCounterSource"/> under the source name
/// <see cref="SourceName"/>: the rule host's fact derivations, the continuation folds a state hash takes, and the
/// perception images a flock or a decision freezes.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. A count
/// that grows with the population while nothing in it changed names a scan that is not proportional to change.</remarks>
public sealed class WorldTickWork : IWorkCounterSource {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "world.tick";

    private readonly WorkCounterSet m_counts = new(
        kinds: [FactDerives, FactSlots, ContinuationFolds, ContinuationSlots, ContinuationEncodes, ContinuationBytes, PerceptionFreezes, PerceptionPoints, PerceptionGrids],
        name: SourceName
    );

    /// <summary>Gets the kind counting the passes in which the rule host compared what bodies answer with what they
    /// last answered (<see cref="WorldRuleHost"/>'s fact versions): one before a rule family's walk, and one after
    /// each firing that delivered an arm.</summary>
    public static WorkKind FactDerives { get; } = new(name: "world.tick.fact-derives", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the population slots whose pose or facts a fact derivation compared.</summary>
    public static WorkKind FactSlots { get; } = new(name: "world.tick.fact-slots", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting continuation folds: one per hash that folded bodies' simulation continuations
    /// (<see cref="WorldPopulation.AppendContinuationHash"/>), the whole population's or a replay scope's.</summary>
    public static WorkKind ContinuationFolds { get; } = new(name: "world.tick.continuation-folds", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the population slots a continuation fold visited to find the ones it folds.</summary>
    public static WorkKind ContinuationSlots { get; } = new(name: "world.tick.continuation-slots", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the slots a continuation fold encoded through the checkpoint's field codecs.</summary>
    public static WorkKind ContinuationEncodes { get; } = new(name: "world.tick.continuation-encodes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the continuation bytes those slots encoded to.</summary>
    public static WorkKind ContinuationBytes { get; } = new(name: "world.tick.continuation-bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the perception images frozen: one per tick a population with a flock steps, and
    /// one per tick a rule family with a neighbour-reading decision evaluates.</summary>
    public static WorkKind PerceptionFreezes { get; } = new(name: "world.tick.perception-freezes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bodies those images copied.</summary>
    public static WorkKind PerceptionPoints { get; } = new(name: "world.tick.perception-points", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the spatial neighbourhoods built over a frozen image, one per cell width a query
    /// asked for since the freeze.</summary>
    public static WorkKind PerceptionGrids { get; } = new(name: "world.tick.perception-grids", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger a host's servers count into. Declared after the kinds, which its counter set reads
    /// as it is created.</summary>
    public static WorldTickWork Process { get; } = new();

    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => m_counts.WorkKinds;

    /// <summary>Adds to one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <param name="amount">The count to add; zero adds nothing.</param>
    public void Add(WorkKind kind, long amount) {
        if (amount != 0L) { m_counts.Add(amount: amount, kind: kind); }
    }
    /// <summary>Reads the current total of one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <returns>The total counted so far.</returns>
    public long Read(WorkKind kind) => m_counts.Read(kind: kind);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind: kind, value: out value);
}
