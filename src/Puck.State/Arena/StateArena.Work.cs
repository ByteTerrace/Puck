using Puck.Abstractions.Counting;

namespace Puck.State;

// The arena's work counters, reported through IWorkCounterSource under the ArenaWork kinds. They sit beside the
// journal as the deterministic counts a price is held against.
public sealed partial class StateArena : IWorkCounterSource {
    private WorkCount m_changeWindowProbes;
    private WorkCount m_visits;

    /// <inheritdoc/>
    string IWorkCounterSource.Name =>
        ArenaWork.SourceName;
    /// <inheritdoc/>
    ReadOnlySpan<WorkKind> IWorkCounterSource.WorkKinds =>
        ArenaWork.Kinds;

    /// <inheritdoc/>
    bool IWorkCounterSource.TryRead(WorkKind kind, out long value) {
        if (ReferenceEquals(objA: kind, objB: ArenaWork.Visits)) {
            value = m_visits.Value;

            return true;
        }

        if (ReferenceEquals(objA: kind, objB: ArenaWork.ChangeWindowProbes)) {
            value = m_changeWindowProbes.Value;

            return true;
        }

        if (ReferenceEquals(objA: kind, objB: ArenaWork.ScratchLeasedElements)) {
            value = Scratch.LeasedElements;

            return true;
        }

        value = 0L;

        return false;
    }

    private void Visit(long lanes) =>
        m_visits.Add(amount: lanes);
}
