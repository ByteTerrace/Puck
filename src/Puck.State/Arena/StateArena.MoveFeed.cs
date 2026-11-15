namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>The moves the log holds before its oldest is overwritten: a reader further behind than this takes
    /// every row as moved.</summary>
    public const int MoveLogCapacity = 1024;

    // The rows whose version moved, in the order they moved: a ring any number of readers follow, each from the place
    // it keeps. Fed only by BumpRowVersion and MarkEveryRowMoved, the doors every version change goes through, so a
    // reader that resumes from its place misses no move. A row that moved twice since a reader's place appears twice.
    private readonly int[] m_moveLog = new int[MoveLogCapacity];
    // The moves logged so far: the place a reader resumes from after reading them all.
    private long m_moveSequence;
    // The earliest place a reader may still resume from: every version moved or restarted at once just before it.
    private long m_moveFloor;

    /// <summary>Gets the place after every row version move so far: what a reader of <see cref="TryReadMoves"/> keeps
    /// once it has taken everything up to now. It moves exactly when a row's version does.</summary>
    public long MoveSequence => m_moveSequence;

    /// <summary>Hands a reader every row whose version moved since the place it kept, when the log still holds them
    /// all.</summary>
    /// <param name="since">The place the reader kept: a <see cref="MoveSequence"/> it read from this arena, or a
    /// negative value for a reader that has read nothing.</param>
    /// <param name="into">The moved rows' catalog ordinals, appended in the order they moved; a row that moved more
    /// than once appears once per move.</param>
    /// <returns><see langword="false"/> when the reader must take every row as moved and nothing was appended: it has
    /// read nothing, more than <see cref="MoveLogCapacity"/> moves passed since its place, or every version moved at
    /// once since then (the arena adopted another's rows or was rebuilt).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into"/> is <see langword="null"/>.</exception>
    public bool TryReadMoves(long since, List<int> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if ((since < m_moveFloor) || (since > m_moveSequence) || ((m_moveSequence - since) > MoveLogCapacity)) {
            return false;
        }
        for (var place = since; (place < m_moveSequence); place++) {
            into.Add(item: m_moveLog[((int)(place & (MoveLogCapacity - 1)))]);
        }

        return true;
    }

    // Moves one row's version: the only increment of m_rowVersions outside a wholesale reset.
    private void BumpRowVersion(int rowOrdinal, bool changesRecorded = false) {
        if (!changesRecorded) { MarkDisclosureRow(rowOrdinal); }
        m_rowVersions[rowOrdinal]++;
        m_moveLog[((int)(m_moveSequence & (MoveLogCapacity - 1)))] = rowOrdinal;
        m_moveSequence++;
    }
    // Every version moved or restarted at once (adoption, a rebuilt store): every reader must take every row as moved.
    private void MarkEveryRowMoved() {
        ChangedKeys.Clear();
        for (var row = 0; row < m_layout.RowCount; row++) { MarkDisclosureRow(row); }
        m_moveSequence++;
        m_moveFloor = m_moveSequence;
    }
}
