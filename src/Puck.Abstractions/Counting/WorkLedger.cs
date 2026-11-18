namespace Puck.Abstractions.Counting;

/// <summary>
/// The base of an owner's work ledger: a named <see cref="IWorkCounterSource"/> whose reads are the reads of one counter
/// set (a <see cref="WorkCounterSet"/> or a <see cref="ShardedWorkCounterSet"/>) and whose own members are the counting
/// calls that owner's work makes, each a count or an add into <see cref="Counters"/>. The ledger declares its kinds and
/// its counting methods; the name, the kinds, the reads and the peak clearing are the set's, forwarded here once. An owner
/// whose counts need no counting methods of its own holds the set directly instead.
/// </summary>
/// <typeparam name="TCounters">The counter set the ledger counts into.</typeparam>
public abstract class WorkLedger<TCounters> : IWorkCounterSource where TCounters : IWorkCounterSource {
    /// <summary>Initializes a new instance of the <see cref="WorkLedger{TCounters}"/> class.</summary>
    /// <param name="counters">The set the ledger counts into and reads from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="counters"/> is <see langword="null"/>.</exception>
    protected WorkLedger(TCounters counters) {
        ArgumentNullException.ThrowIfNull(argument: counters);
        Counters = counters;
    }

    /// <inheritdoc/>
    public string Name =>
        Counters.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds =>
        Counters.WorkKinds;

    /// <summary>Gets the set the ledger counts into.</summary>
    protected TCounters Counters { get; }

    /// <inheritdoc/>
    public void ClearPeaks() =>
        Counters.ClearPeaks();
    /// <summary>Reads the current total of one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <returns>The total counted so far.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this ledger's kinds.</exception>
    public long Read(WorkKind kind) =>
        (TryRead(kind: kind, value: out var value)
            ? value
            : throw new ArgumentException(
                message: $"Work kind '{kind?.Name}' is not a {Name} kind.",
                paramName: nameof(kind)
            )
        );
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) =>
        Counters.TryRead(
            kind: kind,
            value: out value
        );
}
