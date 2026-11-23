namespace Puck.State;

public sealed partial class StateArena {
    // Every row's version and the order the rows last moved in. The storage belongs to IndexVersions, whose only
    // mutators are Bump and ResetAll, so no part of the arena can move a version without its readers hearing of it.
    private readonly IndexVersions m_rowVersions;

    /// <summary>Gets the place after every row version move so far: what a reader of <see cref="TryReadMoves"/> keeps
    /// when it has just read every row for itself. It moves exactly when a row's version does.</summary>
    public long MoveSequence => m_rowVersions.Sequence;

    /// <summary>Hands a reader every row whose version moved since the place it kept, and moves its place on. Any
    /// number of readers follow the moves, each from its own place; what a read costs is the distinct rows that moved
    /// since, however many times each moved and however long ago the reader last read.</summary>
    /// <param name="place">The place the reader kept: one this method or <see cref="MoveSequence"/> gave it from this
    /// arena, or a negative value for a reader that has read nothing. On return it is the place to keep: the
    /// sequence read before the rows were gathered, so a move that lands while the reader works is handed over by
    /// its next read.</param>
    /// <param name="into">The moved rows' catalog ordinals, appended most recently moved first, each once.</param>
    /// <returns><see langword="false"/> when the reader must take every row as moved and nothing was appended: it has
    /// read nothing, or every version moved at once since its place (the arena adopted another's rows or was
    /// rebuilt). <see cref="ArenaWork.MoveReadsWhole"/> counts those answers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into"/> is <see langword="null"/>.</exception>
    public bool TryReadMoves(ref long place, List<int> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (m_rowVersions.TryReadMoves(into: into, place: ref place)) {
            return true;
        }

        m_moveReadsWhole.Increment();

        return false;
    }
    // The cells whose committed bytes changed, oldest first, as a ring any number of readers follow from their own
    // place: the cell half of the row moves above. NoteChange is its only writer and the only caller of ChangedKeys'
    // marks, and every row version move passes one of the two (MarkVersion's keyed mark, MarkDisclosureRow's whole
    // one), so a reader that follows the ring is handed, for every watched row that moved, the cells that did or
    // the row itself. Allocated when its first reader asks, so an arena no rule schedule follows keeps none, and it
    // takes only the rows a reader watches, so a row no reader names (a body-state lane a tick rewrites whole)
    // cannot push the rows that are named out of the ring.
    private const int CellMoveRoom = (1 << 14);

    private ArenaCellMove[]? m_cellMoves;
    private bool[] m_cellMoveWatched = [];
    // How many moves the ring has ever taken; the entry for sequence s sits at s modulo the ring's length.
    private long m_cellMoveSequence;
    // The earliest place a reader may still resume from: every cell moved at once just before it.
    private long m_cellMoveFloor;

    /// <summary>Starts reporting one row's cell moves to the readers of <see cref="TryReadCellMoves"/>. A reader
    /// watches every row it follows before its first read, which takes every row as moved, so nothing it follows moves
    /// unreported; a row no reader watches is not reported.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal; an ordinal the layout does not carry is ignored.</param>
    public void WatchCellMoves(int rowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            return;
        }
        if (m_cellMoveWatched.Length < m_layout.RowCount) {
            Array.Resize(array: ref m_cellMoveWatched, newSize: m_layout.RowCount);
        }
        m_cellMoveWatched[rowOrdinal] = true;
    }
    /// <summary>Hands a reader every cell of a watched row (<see cref="WatchCellMoves"/>) whose committed bytes changed
    /// since the place it kept, and moves its place on: the cell-level form of <see cref="TryReadMoves"/>. A change to
    /// a held cell's value, clock, behaviour or observation stamp names the cell; any other change to a row (its
    /// members, their order, a cursor, a policy) names the row whole. Every version move of a watched row is reported
    /// one way or the other.</summary>
    /// <param name="place">The place the reader kept: one this method gave it from this arena, or a negative value
    /// for a reader that has read nothing. On return it is the place to keep.</param>
    /// <param name="into">The moved cells, appended oldest first; one cell may appear more than once.</param>
    /// <returns><see langword="false"/> when the reader must take every row as moved and nothing was appended: it has
    /// read nothing, more moves landed since its place than the arena keeps, or every version moved at once (the
    /// arena adopted another's rows or was rebuilt).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into"/> is <see langword="null"/>.</exception>
    public bool TryReadCellMoves(ref long place, List<ArenaCellMove> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        var since = place;
        var sequence = m_cellMoveSequence;

        place = sequence;
        if (m_cellMoves is not { } moves) {
            m_cellMoves = new ArenaCellMove[CellMoveRoom];
            m_cellMoveFloor = sequence;
            m_moveReadsWhole.Increment();

            return false;
        }
        if ((since < m_cellMoveFloor) || (since > sequence) || ((sequence - since) > moves.Length)) {
            m_moveReadsWhole.Increment();

            return false;
        }
        for (var at = since; (at < sequence); at++) {
            into.Add(item: moves[(at & (moves.Length - 1))]);
        }

        return true;
    }
    // The one door a committed change to a row's cells is described through: the publication's carrier and the cell
    // moves a rule schedule follows hear the same thing. The invalid key names the row whole.
    private void NoteChange(int row, CellKey key = default) {
        ChangedKeys.Mark(row: row, key: key.Ordinal);
        if ((m_cellMoves is not { } moves) || (((uint)row) >= ((uint)m_cellMoveWatched.Length)) || !m_cellMoveWatched[row]) {
            return;
        }

        moves[(m_cellMoveSequence & (moves.Length - 1))] = new ArenaCellMove(Row: row, Key: key);
        m_cellMoveSequence++;
    }
    // Moves one row's version, and names the content the row now holds (RowContent).
    private void BumpRowVersion(int rowOrdinal, bool changesRecorded = false) {
        if (!changesRecorded) { MarkDisclosureRow(rowOrdinal); }
        m_rowVersions.Bump(index: rowOrdinal);
        SettleContent(row: rowOrdinal);
    }
    // Every version restarts at once (adoption of a rebuilt store): every reader must take every row as moved.
    private void ResetRowVersions(ulong version) {
        ChangedKeys.Clear();
        for (var row = 0; row < m_layout.RowCount; row++) { MarkDisclosureRow(row); }
        // Every reader's place falls below the floor, so each takes every row as moved.
        m_cellMoveFloor = ++m_cellMoveSequence;
        m_rowVersions.ResetAll(count: m_layout.RowCount, version: version);
        ForgetContents();
    }
}
