namespace Puck.Abstractions.Gpu;

/// <summary>A journal's read-only face. Only a <see cref="GpuRowCursor"/> can collect it; no producer operation is
/// reachable from a frame carrying this object.</summary>
public sealed class GpuRowChanges {
    private readonly GpuChangedRows m_owner;

    internal GpuRowChanges(GpuChangedRows owner) => m_owner = owner;

    /// <summary>Gets the journal's retained array payload for memory attribution.</summary>
    public long StorageBytes => m_owner.StorageBytes;

    internal bool StableRows => m_owner.StableRows;

    internal bool Collect(long since, long through, int rows, GpuUploadRuns into, GpuUploadRuns? reseated) =>
        m_owner.TryCollect(into: into, reseated: reseated, rows: rows, since: since, through: through);
}
/// <summary>One consumer's captured journal endpoint. Collection always returns bounded runs, including a whole-table
/// run when the producer, roster, extent or retained history cannot prove an incremental window. Commit only after
/// consuming those runs successfully; a failed consumption may collect again from the preceding endpoint.</summary>
public sealed class GpuRowCursor {
    private readonly record struct Endpoint(GpuRowChanges? Reader, object Table, int Rows, long Revision, long Serial);

    private Endpoint? m_committed;
    private Endpoint? m_pending;

    /// <summary>Gets the last successfully consumed serial, or zero before the first commit.</summary>
    public long Serial => (m_committed?.Serial ?? 0);

    /// <summary>Collects through the serial captured with the table. A true answer is a conservative whole-table
    /// refresh; false is an incremental answer, possibly empty. Reseats append only for a valid incremental window.</summary>
    /// <param name="reader">The producer's read-only journal, or null for an unjournaled table.</param>
    /// <param name="through">The endpoint captured with this frame.</param>
    /// <param name="table">The roster whose indices the runs name.</param>
    /// <param name="rows">The captured roster extent.</param>
    /// <param name="revision">The roster's content revision.</param>
    /// <param name="into">The output runs, cleared before every answer.</param>
    /// <param name="reseated">Optional interpretation changes appended on an incremental answer.</param>
    /// <param name="force">Whether to require a whole-table answer.</param>
    /// <returns>Whether the answer refreshes the whole table.</returns>
    public bool Collect(GpuRowChanges? reader, long through, object table, int rows, long revision, GpuUploadRuns into,
        GpuUploadRuns? reseated = null, bool force = false) {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        into.Clear();
        m_pending = new(Reader: reader, Revision: revision, Rows: rows, Serial: through, Table: table);
        var whole = (force || (m_committed is not { } last) || !ReferenceEquals(objA: last.Reader, objB: reader) ||
            !ReferenceEquals(objA: last.Table, objB: table) || ((last.Rows != rows) && (reader?.StableRows != true)) ||
            ((reader is null) ? (last.Revision != revision) :
                (((last.Revision != revision) && (through <= last.Serial)) || !reader.Collect(last.Serial, through, rows, into, reseated))));

        if (whole) { into.Clear(); into.Add(length: rows, start: 0); }
        return whole;
    }
    /// <summary>Commits the exact endpoint last collected, never the producer's later serial.</summary>
    /// <exception cref="InvalidOperationException">No collection awaits consumption.</exception>
    public void Commit() {
        m_committed = (m_pending ?? throw new InvalidOperationException(message: "Collect a row window before committing it."));
        m_pending = null;
    }
}
