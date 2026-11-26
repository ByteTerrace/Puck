namespace Puck.Abstractions.Gpu;

/// <summary>A bounded journal of changed table rows (<see cref="GpuChangedRows"/>) that one thread writes while consumers
/// on others collect it: every frame published and every collection holds the journal's one lock, so a consumer never
/// reads a ring slot a frame is overwriting. Each consumer keeps its own <see cref="GpuRowCursor"/> on its own thread.</summary>
public sealed class GpuSharedChangedRows {
    private readonly GpuChangedRows m_rows;
    private readonly Lock m_gate = new();

    /// <summary>Initializes a new instance of the <see cref="GpuSharedChangedRows"/> class.</summary>
    /// <param name="history">The number of retained frames, at least two.</param>
    /// <param name="stableRows">Whether row addresses keep their meaning across extent changes.</param>
    public GpuSharedChangedRows(int history = 64, bool stableRows = false) => m_rows = new GpuChangedRows(history: history, stableRows: stableRows);

    /// <summary>Gets the latest frame's serial, zero before the first.</summary>
    public long Serial {
        get {
            lock (m_gate) { return m_rows.Serial; }
        }
    }

    /// <summary>Publishes one frame: every row of a table of <paramref name="rows"/> rows when
    /// <paramref name="everything"/>, else the rows <paramref name="owed"/> names.</summary>
    /// <param name="everything">Whether the frame owes every row.</param>
    /// <param name="rows">The table's row count; every owed row lies below it.</param>
    /// <param name="owed">The rows the frame owes, each once, read only when the frame does not owe every row.</param>
    /// <returns>The frame's serial.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is negative, or an owed row lies outside the
    /// table.</exception>
    public long Publish(bool everything, int rows, ReadOnlySpan<int> owed) {
        lock (m_gate) {
            m_rows.Begin(everything: everything, rows: rows);
            if (!everything) {
                foreach (var row in owed) { m_rows.Owe(count: 1, start: row); }
            }

            return m_rows.Serial;
        }
    }
    /// <summary>Collects the rows the frames after <paramref name="since"/> through <paramref name="through"/> changed,
    /// for a consumer that keeps its own endpoint beside what it copied (an image filled through a frame) instead of a
    /// <see cref="GpuRowCursor"/>.</summary>
    /// <param name="since">The frame the consumer's copy is current through.</param>
    /// <param name="through">The frame to collect through.</param>
    /// <param name="rows">The table's row count at <paramref name="through"/>.</param>
    /// <param name="into">The runs, cleared first; a superset of the changed rows when the window coarsens.</param>
    /// <returns><see langword="true"/> for an incremental answer, possibly empty; <see langword="false"/> when the
    /// retained history cannot prove the window, a frame in it owed every row, or the extent at
    /// <paramref name="through"/> differs, after which the consumer refreshes every row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into"/> is <see langword="null"/>.</exception>
    public bool TryCollect(long since, long through, int rows, GpuUploadRuns into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        lock (m_gate) {
            return m_rows.TryCollect(since: since, through: through, rows: rows, into: into);
        }
    }
    /// <summary>Collects the rows changed since a cursor's last commit through a frame, as
    /// <see cref="GpuRowCursor.Collect"/> does with this journal as the table; the caller commits the cursor once it has
    /// consumed the runs.</summary>
    /// <param name="cursor">The consumer's cursor.</param>
    /// <param name="through">The frame the consumer's table was captured at, or a negative serial for the latest.</param>
    /// <param name="rows">The consumer's table extent.</param>
    /// <param name="into">The runs, cleared first.</param>
    /// <returns><see langword="true"/> for an answer that owes every row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> or <paramref name="into"/> is
    /// <see langword="null"/>.</exception>
    public bool Collect(GpuRowCursor cursor, long through, int rows, GpuUploadRuns into) {
        ArgumentNullException.ThrowIfNull(argument: cursor);
        ArgumentNullException.ThrowIfNull(argument: into);

        lock (m_gate) {
            return cursor.Collect(into: into, reader: m_rows.Reader, revision: 0L, rows: rows, table: this, through: ((through < 0L) ? m_rows.Serial : through));
        }
    }
}
