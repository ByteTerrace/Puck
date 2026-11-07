namespace Puck.State;

public sealed partial class StateArena {
    // The rows whose version moved since the feed's reader last drained it, each once, and whether every row must be
    // taken as moved. Fed only by BumpRowVersion and MarkEveryRowMoved, the doors every version change goes
    // through, so a reader that drains it misses no move. Off until a reader opens it, so an arena nobody schedules
    // against pays one branch per moved row.
    private bool m_moveFeedOpen;
    private bool m_moveFeedAll;
    private bool[] m_moveFeedMark = [];
    private readonly List<int> m_moveFeedRows = [];

    /// <summary>Opens the moved-row feed for its one reader, the evaluator that walks a due list over this arena:
    /// from now on every row whose version moves is recorded until <see cref="DrainMovedRows"/> hands it over. The
    /// first drain after opening reports every row moved.</summary>
    public void OpenMoveFeed() {
        m_moveFeedOpen = true;
        m_moveFeedAll = true;
        ClearMoveFeed();
    }
    /// <summary>Hands every row whose version moved since the last drain to the caller and forgets them.</summary>
    /// <param name="into">The moved rows' catalog ordinals, appended.</param>
    /// <returns><see langword="true"/> when every row must be taken as moved: the feed was just opened, or the arena
    /// adopted another's rows or was rebuilt, which moves every version at once.</returns>
    public bool DrainMovedRows(List<int> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        var all = m_moveFeedAll;

        into.AddRange(collection: m_moveFeedRows);
        ClearMoveFeed();
        m_moveFeedAll = false;

        return all;
    }

    // Moves one row's version: the only increment of m_rowVersions outside a wholesale reset.
    private void BumpRowVersion(int rowOrdinal, bool changesRecorded = false) {
        if (!changesRecorded) { MarkDisclosureRow(rowOrdinal); }
        m_rowVersions[rowOrdinal]++;
        if (!m_moveFeedOpen) {
            return;
        }
        if (rowOrdinal >= m_moveFeedMark.Length) {
            Array.Resize(array: ref m_moveFeedMark, newSize: Math.Max(val1: (rowOrdinal + 1), val2: m_layout.RowCount));
        }
        if (!m_moveFeedMark[rowOrdinal]) {
            m_moveFeedMark[rowOrdinal] = true;
            m_moveFeedRows.Add(item: rowOrdinal);
        }
    }
    // Every version moved or restarted at once (adoption, a rebuilt store): the reader must take every row as moved.
    private void MarkEveryRowMoved() {
        ChangedKeys.Clear();
        for (var row = 0; row < m_layout.RowCount; row++) { MarkDisclosureRow(row); }
        m_moveFeedAll = true;
        ClearMoveFeed();
    }
    private void ClearMoveFeed() {
        foreach (var row in m_moveFeedRows) {
            m_moveFeedMark[row] = false;
        }
        m_moveFeedRows.Clear();
    }
}
