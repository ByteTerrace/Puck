namespace Puck.State;

public sealed partial class StateArena {
    private readonly List<ArenaJournalEntry> m_windowEntries = [];
    private readonly List<int> m_windowOpaqueRows = [];

    private long m_windowEpoch;
    private bool m_windowOpen;
    private long[] m_windowOpaqueStamp = [];
    private long[] m_windowStamp = [];

    /// <summary>Gets a value indicating whether a change window is open.</summary>
    public bool ChangeWindowOpen => m_windowOpen;

    /// <summary>Opens a change window: from now until <see cref="EndChangeWindow"/>, the arena remembers what each
    /// position held before the first write any committed scope made to it, however many scopes commit in between.
    /// </summary>
    /// <remarks><see cref="RowVersion"/> answers whether one commit left a row different; a window answers whether a
    /// run of commits did, so a row one commit clears and a later one rebuilds to the same bytes reads unchanged. The
    /// window costs one retained entry per distinct position written while it is open.</remarks>
    /// <exception cref="InvalidOperationException">A window is already open.</exception>
    public void BeginChangeWindow() {
        if (m_windowOpen) {
            throw new InvalidOperationException(message: "A change window is already open.");
        }
        if (m_windowStamp.Length < m_layout.ChangeSlotCount) {
            m_windowStamp = new long[m_layout.ChangeSlotCount];
        }
        if (m_windowOpaqueStamp.Length < m_layout.RowCount) {
            m_windowOpaqueStamp = new long[m_layout.RowCount];
        }

        m_windowEpoch++;
        m_windowEntries.Clear();
        m_windowOpaqueRows.Clear();
        m_windowOpen = true;
    }
    /// <summary>Closes the open change window and reports whether any row of <paramref name="rows"/> holds, at a
    /// position written while it was open, a value different from the one it held when the window opened.</summary>
    /// <param name="rows">The rows to judge, as a set over catalog ordinals; an ordinal past its width is not in
    /// it. Each retained position is tested against it once, so closing costs the positions written, never those
    /// times the rows judged.</param>
    /// <returns><see langword="true"/> when one of <paramref name="rows"/> differs from what it held when the window
    /// opened. A vector written, or any write that bypassed a scope, while the window was open counts as a difference
    /// in its row, since the window keeps no copy of the value it replaced.</returns>
    /// <exception cref="InvalidOperationException">No window is open.</exception>
    public bool EndChangeWindow(CellSet rows) {
        if (!m_windowOpen) {
            throw new InvalidOperationException(message: "No change window is open.");
        }

        m_windowOpen = false;
        foreach (var row in m_windowOpaqueRows) {
            m_changeWindowProbes.Increment();
            if (rows.Contains(index: row)) {
                return true;
            }
        }
        foreach (var entry in m_windowEntries) {
            m_changeWindowProbes.Increment();
            if (rows.Contains(index: m_layout.RowOf(column: entry.Column, index: entry.Index)) && Differs(entry: entry)) {
                return true;
            }
        }

        return false;
    }

    // Called as the outermost scope commits: the first journal entry for a position since the window opened holds
    // what the position held when it opened.
    private void WindowCommitted(int mark) {
        if (!m_windowOpen) {
            return;
        }
        for (var index = mark; (index < m_journal.Length); index++) {
            var entry = m_journal[index];

            if (entry.Column == ArenaColumn.Vector) {
                WindowOpaque(column: entry.Column, index: entry.Index);

                continue;
            }

            var slot = (m_layout.ChangeBase(column: entry.Column) + entry.Index);

            if (m_windowStamp[slot] == m_windowEpoch) {
                continue;
            }

            m_windowStamp[slot] = m_windowEpoch;
            m_windowEntries.Add(item: entry);
        }
    }
    private void WindowOpaque(ArenaColumn column, int index) {
        if (m_windowOpen && (m_layout.RowOf(column: column, index: index) is var row and >= 0) && (m_windowOpaqueStamp[row] != m_windowEpoch)) {
            m_windowOpaqueStamp[row] = m_windowEpoch;
            m_windowOpaqueRows.Add(item: row);
        }
    }
}
