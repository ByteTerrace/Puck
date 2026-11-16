namespace Puck.Abstractions.Counting;

/// <summary>
/// An engine service that exposes its <see cref="WorkCount"/>s, so a collector can read them without knowing the
/// service. The source declares the kinds it counts; a kind it does not declare is unavailable, which is different
/// from a declared kind that has counted nothing yet and reads zero.
/// </summary>
public interface IWorkCounterSource {
    /// <summary>Gets the stable name a report prints as this source's section header and a reader filters by, in the
    /// form of a <see cref="WorkKind"/> name: dotted lowercase segments, most general first (<c>state.arena</c>,
    /// <c>world.boot</c>). It never changes over the source's lifetime.</summary>
    string Name { get; }
    /// <summary>Gets the kinds this source counts, in the order a report lists them. The set never changes over the
    /// source's lifetime.</summary>
    ReadOnlySpan<WorkKind> WorkKinds { get; }

    /// <summary>Reads the current value of one kind.</summary>
    /// <param name="kind">The kind to read, compared by reference.</param>
    /// <param name="value">For a total, the total counted so far, never lower than an earlier read of the same kind;
    /// for a peak (<see cref="WorkKind.IsPeak"/>), the largest value raised since the source last cleared its peaks
    /// (<see cref="ClearPeaks"/>), never lower than an earlier read since then; zero when the method returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when this source declares <paramref name="kind"/>; <see langword="false"/> when
    /// the kind is unavailable here.</returns>
    bool TryRead(WorkKind kind, out long value);
    /// <summary>Returns every peak kind the source declares to zero, so the next read gives the largest value raised
    /// after the call. Totals are never cleared; a source with no peak kinds does nothing.</summary>
    void ClearPeaks() { }
}
