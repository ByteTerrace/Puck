using Puck.Abstractions.Counting;

namespace Puck.State;

// The search's work counters, reported through IWorkCounterSource under the SearchWorkKinds kinds.
public sealed partial class ArenaSearch : IWorkCounterSource {
    private WorkCount m_candidates;
    private WorkCount m_expansions;
    private WorkCount m_playoutPlies;

    /// <inheritdoc/>
    string IWorkCounterSource.Name =>
        SearchWorkKinds.SourceName;
    /// <inheritdoc/>
    ReadOnlySpan<WorkKind> IWorkCounterSource.WorkKinds =>
        SearchWorkKinds.Kinds;

    /// <inheritdoc/>
    bool IWorkCounterSource.TryRead(WorkKind kind, out long value) {
        if (ReferenceEquals(objA: kind, objB: SearchWorkKinds.Candidates)) {
            value = m_candidates.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: SearchWorkKinds.Expansions)) {
            value = m_expansions.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: SearchWorkKinds.PlayoutPlies)) {
            value = m_playoutPlies.Value;

            return true;
        }

        value = 0L;

        return false;
    }
}
