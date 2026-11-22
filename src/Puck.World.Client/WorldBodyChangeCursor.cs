using Puck.Abstractions.Gpu;

namespace Puck.World.Client;

/// <summary>
/// One consumer's place in a stamp source's body journal (<see cref="IWorldStampSource.BodyChanges"/>): each
/// <see cref="TryCollect"/> names the bodies whose presented inputs moved since the consumer's previous collect, so a
/// band that draws bodies (a stamp pool, its rigid copies, the catalog avatars) reads the bodies that changed and
/// never every body it draws.
/// </summary>
/// <remarks>Used on the thread that presents frames. A consumer that skips frames collects every frame in between; one
/// that falls out of the journal's history, or reads a source that keeps no journal, is told every body may have
/// moved.</remarks>
internal sealed class WorldBodyChangeCursor {
    private long m_collects;
    private readonly GpuRowCursor m_cursor = new();
    private readonly GpuUploadRuns m_runs = new(capacity: GpuRegion.MaxCopyRuns);

    /// <summary>Collects the bodies whose presented inputs moved since the previous collect.</summary>
    /// <param name="source">The source whose journal is read.</param>
    /// <param name="bodies">The list each such body's index is appended to, in ascending runs. The journal coarsens a
    /// frame's many separate bodies into runs, so a body between two that moved may be named too.</param>
    /// <returns><see langword="false"/> when the bodies cannot be named (the first collect, another source, a journal
    /// frame that owes every body, a window the journal no longer holds, or a source with no journal): nothing is
    /// appended, and the consumer reads every body it draws.</returns>
    public bool TryCollect(IWorldStampSource source, List<int> bodies) {
        var journal = source.BodyChanges;

        m_collects++;

        // A source with no journal answers whole every time: the revision it is collected at moves with each collect.
        var whole = m_cursor.Collect(into: m_runs, reader: journal, revision: ((journal is null) ? m_collects : 0L), rows: WorldClient.EntityCapacity, table: source, through: source.BodyChangesSerial);

        m_cursor.Commit();
        if (whole) {
            return false;
        }

        for (var run = 0; (run < m_runs.Count); run++) {
            var end = (m_runs.Start(index: run) + m_runs.Length(index: run));

            for (var body = m_runs.Start(index: run); (body < end); body++) {
                bodies.Add(item: body);
            }
        }

        return true;
    }
}
