namespace Puck.State;

public sealed partial class StateArena {
    // Every row's version and the order the rows last moved in. The storage belongs to ArenaRowVersions, whose only
    // mutators are Bump and ResetAll, so no part of the arena can move a version without its readers hearing of it.
    private readonly ArenaRowVersions m_rowVersions;

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
    // Moves one row's version.
    private void BumpRowVersion(int rowOrdinal, bool changesRecorded = false) {
        if (!changesRecorded) { MarkDisclosureRow(rowOrdinal); }
        m_rowVersions.Bump(row: rowOrdinal);
    }
    // Every version restarts at once (adoption of a rebuilt store): every reader must take every row as moved.
    private void ResetRowVersions(ulong version) {
        ChangedKeys.Clear();
        for (var row = 0; row < m_layout.RowCount; row++) { MarkDisclosureRow(row); }
        m_rowVersions.ResetAll(rowCount: m_layout.RowCount, version: version);
    }
}
