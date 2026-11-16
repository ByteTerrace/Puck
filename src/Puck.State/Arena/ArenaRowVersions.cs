namespace Puck.State;

/// <summary>
/// Every row's content version, and the order the rows last moved in. A version moves only through
/// <see cref="Bump"/> and <see cref="ResetAll"/>, and both record the move here, so a reader that keeps the
/// <see cref="Sequence"/> it read to is handed exactly the rows that moved since
/// (<see cref="TryReadMoves"/>), each once however often it moved, or is told to take every row as moved.
/// </summary>
/// <remarks>
/// <para>The storage is private to this type: nothing else can change a version without the readers hearing of
/// it. The order is an intrusive list threaded through the rows, most recently moved first, so a move costs a
/// constant number of writes, a read costs the distinct rows that moved since the reader's place, and no number of
/// moves between two reads loses one.</para>
/// <para>Writers are single-threaded with one another, as the arena's are, and a wholesale reset shares their thread
/// or gate with every reader. A reader that overlaps a single move may be handed a row twice, or a row whose move is
/// still landing, but never loses one for good: a move publishes its sequence last, and a reader's new place is the
/// sequence it read before it walked, so whatever the walk missed moved after that place and is handed over by the
/// next read.</para>
/// </remarks>
internal sealed class ArenaRowVersions {
    private const int None = -1;

    private ulong[] m_versions;
    // The sequence each row last moved at; a row is in the list exactly while this is above the floor.
    private long[] m_movedAt;
    // The list's links by row: the row that moved next after it (toward the newest) and the one before it.
    private int[] m_newer;
    private int[] m_older;
    private int m_newest = None;
    private long m_sequence;
    // The earliest place a reader may still resume from: every version moved or restarted at once just before it.
    private long m_floor;

    /// <summary>Initializes the versions of a number of rows, each zero and none moved.</summary>
    /// <param name="rowCount">The number of rows.</param>
    public ArenaRowVersions(int rowCount) {
        m_versions = new ulong[rowCount];
        m_movedAt = new long[rowCount];
        m_newer = new int[rowCount];
        m_older = new int[rowCount];
    }

    /// <summary>Gets the number of rows.</summary>
    public int Count => m_versions.Length;
    /// <summary>Gets the place after every move so far: what a reader that has just read every row keeps. It moves
    /// exactly when a version does.</summary>
    public long Sequence => Volatile.Read(location: ref m_sequence);
    /// <summary>Gets one row's version.</summary>
    /// <param name="row">The row's catalog ordinal.</param>
    public ulong this[int row] => m_versions[row];

    /// <summary>Moves one row's version and makes the row the most recently moved.</summary>
    /// <param name="row">The row's catalog ordinal.</param>
    public void Bump(int row) {
        var sequence = (m_sequence + 1L);
        var linked = (m_movedAt[row] > m_floor);

        m_versions[row]++;
        m_movedAt[row] = sequence;
        if (row != m_newest) {
            if (linked) {
                var newer = m_newer[row];
                var older = m_older[row];

                // A linked row that is not the newest has a newer neighbour.
                m_older[newer] = older;
                if (older != None) { m_newer[older] = newer; }
            }

            m_older[row] = m_newest;
            m_newer[row] = None;
            if (m_newest != None) { m_newer[m_newest] = row; }
            m_newest = row;
        }

        // Published last: a reader that sees this sequence finds the move already in the list.
        Volatile.Write(location: ref m_sequence, value: sequence);
    }
    /// <summary>Sets every row's version at once, for a number of rows that may differ from the present one: every
    /// reader's place falls below the floor, so each takes every row as moved.</summary>
    /// <param name="rowCount">The number of rows from now on.</param>
    /// <param name="version">The version every row takes.</param>
    public void ResetAll(int rowCount, ulong version) {
        var sequence = (m_sequence + 1L);

        if (m_versions.Length != rowCount) {
            m_versions = new ulong[rowCount];
            m_movedAt = new long[rowCount];
            m_newer = new int[rowCount];
            m_older = new int[rowCount];
        }

        Array.Fill(array: m_versions, value: version);
        m_newest = None;
        // Every recorded move is at or below the floor now, which is what unlinks every row.
        m_floor = sequence;
        Volatile.Write(location: ref m_sequence, value: sequence);
    }
    /// <summary>Hands a reader every row whose version moved since the place it kept, and moves its place to the
    /// sequence read before the rows were gathered.</summary>
    /// <param name="place">The place the reader kept: one this method or <see cref="Sequence"/> gave it from these
    /// versions, or a negative value for a reader that has read nothing. On return it is the place to keep, whatever
    /// the answer.</param>
    /// <param name="into">The moved rows' catalog ordinals, appended most recently moved first, each once.</param>
    /// <returns><see langword="false"/> when the reader must take every row as moved and nothing was appended: it has
    /// read nothing, or every version was set at once since its place.</returns>
    public bool TryReadMoves(ref long place, List<int> into) {
        var since = place;
        var sequence = Volatile.Read(location: ref m_sequence);

        place = sequence;
        if ((since < m_floor) || (since > sequence)) {
            return false;
        }

        var start = into.Count;
        var versions = m_versions;

        for (var row = m_newest; ((row != None) && (m_movedAt[row] > since)); row = m_older[row]) {
            // Each row is in the list once, so a walk longer than the rows is one a writer re-linked under: every row
            // is taken as moved, which is always a sound answer.
            if ((into.Count - start) >= versions.Length) {
                into.RemoveRange(index: start, count: (into.Count - start));

                return false;
            }

            into.Add(item: row);
        }

        return true;
    }
}
