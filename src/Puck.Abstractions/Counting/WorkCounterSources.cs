namespace Puck.Abstractions.Counting;

/// <summary>
/// The read every single-kind <see cref="IWorkCounterSource"/> shares. A source's name passes
/// <see cref="WorkKind.RequireSourceName"/> at construction.
/// </summary>
public static class WorkCounterSources {
    /// <summary>Reads a source that counts exactly one kind: the count when <paramref name="kind"/> is the declared
    /// kind, compared by reference, and unavailable otherwise.</summary>
    /// <param name="declared">The one kind the source declares.</param>
    /// <param name="count">The source's count of <paramref name="declared"/>.</param>
    /// <param name="kind">The kind a reader asked for.</param>
    /// <param name="value">The count's total when the kinds match; zero otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="kind"/> is <paramref name="declared"/>.</returns>
    public static bool TryReadSingle(WorkKind declared, in WorkCount count, WorkKind kind, out long value) {
        if (ReferenceEquals(
            objA: kind,
            objB: declared
        )) {
            value = count.Value;

            return true;
        }

        value = 0L;

        return false;
    }
}
