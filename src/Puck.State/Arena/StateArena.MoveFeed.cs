namespace Puck.State;

public sealed partial class StateArena {
    // The rows whose version moved since the feed's reader last drained it, each once, and whether every row must be
    // taken as moved. Fed only by BumpRowVersion and MarkEveryRowMoved, the doors every version change goes
    // through, so a reader that drains it misses no move. Off until a reader opens it, so an arena nobody schedules
    // against pays one branch per moved row.
    private bool m_moveFeedOpen;
    private bool m_moveFeedAll;
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_moveFeedRows = new();

    /// <summary>Opens the moved-row feed for its one reader, the evaluator that walks a due list over this arena:
    /// from now on every row whose version moves is recorded until <see cref="DrainMovedRows"/> hands it over. The
    /// first drain after opening reports every row moved.</summary>
    public void OpenMoveFeed() {
        m_moveFeedOpen = true;
        m_moveFeedAll = true;
        m_moveFeedRows.Clear();
    }
    /// <summary>Hands every row whose version moved since the last drain to the caller and forgets them.</summary>
    /// <param name="into">The moved rows' catalog ordinals, appended.</param>
    /// <returns><see langword="true"/> when every row must be taken as moved: the feed was just opened, or the arena
    /// adopted another's rows or was rebuilt, which moves every version at once.</returns>
    public bool DrainMovedRows(List<int> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        var all = m_moveFeedAll;

        into.AddRange(source: m_moveFeedRows.Members);
        m_moveFeedRows.Clear();
        m_moveFeedAll = false;

        return all;
    }

    // Moves one row's version: the only increment of m_rowVersions outside a wholesale reset.
    private void BumpRowVersion(int rowOrdinal, bool changesRecorded = false) {
        if (!changesRecorded) { MarkDisclosureRow(rowOrdinal); }
        m_rowVersions[rowOrdinal]++;
        if (m_moveFeedOpen) { m_moveFeedRows.Add(index: rowOrdinal); }
    }
    // Every version moved or restarted at once (adoption, a rebuilt store): the reader must take every row as moved.
    private void MarkEveryRowMoved() {
        ChangedKeys.Clear();
        for (var row = 0; row < m_layout.RowCount; row++) { MarkDisclosureRow(row); }
        m_moveFeedAll = true;
        m_moveFeedRows.Clear();
    }
}
