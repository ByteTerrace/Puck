namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>The most cell slots a row may reserve for the arena to remember what it held
    /// (<see cref="RowContent"/>): remembering costs a copy and a few compares of the row's numbers at each commit that
    /// changes them, so it is kept to rows small enough for that to be cheaper than what a reader saves.</summary>
    public const int MaxRememberedCells = 64;

    // How many of a row's recent contents are remembered: enough for a row a tick's rules take through a short cycle
    // (cleared by one rule, rebuilt by the next) to be recognised at each station.
    private const int RememberedContents = 4;

    // The numbers a row held at up to RememberedContents of its recent versions, each beside the version it was first
    // held at. It holds the numbers alone, so it describes the row only while nothing else about the row changes: any
    // other change empties it (SettleContent).
    private sealed class RowMemory(int cells) {
        public readonly int Cells = cells;
        public readonly long[] Values = new long[(cells * RememberedContents)];
        public readonly ulong[] Versions = new ulong[RememberedContents];
        public int Count;
        public int Next;

        // The version the numbers were first held at, when they are remembered; otherwise remembers them under
        // `version` and answers zero.
        public ulong Resolve(ReadOnlySpan<long> numbers, ulong version) {
            for (var index = 0; (index < Count); index++) {
                if (numbers.SequenceEqual(other: Values.AsSpan(start: (index * Cells), length: Cells))) {
                    return Versions[index];
                }
            }

            numbers.CopyTo(destination: Values.AsSpan(start: (Next * Cells), length: Cells));
            Versions[Next] = version;
            Count = Math.Min(val1: (Count + 1), val2: RememberedContents);
            Next = ((Next + 1) % RememberedContents);

            return 0UL;
        }
    }

    // The version a row's present content was first held at, when the arena recognised it; zero when the content is
    // named by the row's present version.
    private ulong[] m_contentAlias = [];
    private RowMemory?[] m_rowMemory = [];
    // The change epoch in which a row took a change to something other than a number cell.
    private long[] m_rowOpaqueStamp = [];
    // The change epoch of the commit whose version moves are being settled, or -1 outside one: only there does the
    // arena know everything that changed about a row.
    private long m_valueSettleEpoch = -1L;

    /// <summary>Returns a number that names a row's content: two reads that answer the same number prove the row held
    /// the same content at both, exactly as two equal <see cref="RowVersion"/> reads do. Unlike the version it can
    /// return to a number it answered before: when a commit changes nothing about the row but its numbers and leaves
    /// them exactly as they stood at a recent version, with nothing else about the row changed in between, the row
    /// answers that version again. A row that rules take through a cycle within a tick (one clears a cell, the next
    /// rebuilds it) therefore answers the same number at each station of the cycle from tick to tick, where its
    /// version moves on every pass.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The content's number: the row's version, or the earlier version the same content was held at.</returns>
    /// <remarks>Only a row of at most <see cref="MaxRememberedCells"/> numeric cells with no value-over-time trait is
    /// remembered; every other row answers its version. Nothing here is state: no hash, export or checkpoint reads it.
    /// The number is not ordered, so a reader compares it for equality and never sums or orders it.</remarks>
    public ulong RowContent(int rowOrdinal) {
        var alias = ((((uint)rowOrdinal) < ((uint)m_contentAlias.Length)) ? m_contentAlias[rowOrdinal] : 0UL);

        return ((alias != 0UL) ? alias : m_rowVersions[rowOrdinal]);
    }

    // Marks a row as having taken a change other than to a number cell in the commit being settled.
    private void MarkContentOpaque(ArenaColumn column, int index) {
        if (column == ArenaColumn.Number) {
            return;
        }

        var (first, count) = RowsOf(column: column, index: index);

        for (var row = first; (row < (first + count)); row++) {
            m_rowOpaqueStamp[row] = m_changeEpoch;
        }
    }
    private void EnsureContentRoom() {
        var rows = m_layout.RowCount;

        if (m_contentAlias.Length != rows) {
            m_contentAlias = new ulong[rows];
            m_rowMemory = new RowMemory?[rows];
        }
        if (m_rowOpaqueStamp.Length != rows) {
            m_rowOpaqueStamp = new long[rows];
            Array.Fill(array: m_rowOpaqueStamp, value: -1L);
        }
    }
    // Names the content a row holds after its version moved. Called by the one door every version moves through, so
    // no version can move without the content's number following. The numbers are compared with what is remembered
    // only when the move is a settled commit's and every change the commit made to the row was to a number cell;
    // anything else (a membership, a clock, a text, a write outside a scope, a restore) empties the row's memory, so
    // a remembered content is always one the row has held with nothing but its numbers changed since.
    private void SettleContent(int row) {
        EnsureContentRoom();

        ref readonly var plan = ref m_layout[row];
        var memory = m_rowMemory[row];

        if (
            (m_valueSettleEpoch != m_changeEpoch) ||
            (m_rowOpaqueStamp[row] == m_changeEpoch) ||
            !plan.IsStored ||
            plan.HostOwned ||
            plan.HasTraits ||
            (plan.Kind is not (CellKind.Bool or CellKind.Fixed or CellKind.Int)) ||
            (plan.CellCapacity is < 1 or > MaxRememberedCells)
        ) {
            m_contentAlias[row] = 0UL;
            if (memory is not null) {
                memory.Count = 0;
                memory.Next = 0;
            }

            return;
        }
        if ((memory is null) || (memory.Cells != plan.CellCapacity)) {
            m_rowMemory[row] = memory = new RowMemory(cells: plan.CellCapacity);
        }

        m_contentAlias[row] = memory.Resolve(
            numbers: m_numbers.AsSpan(start: plan.CellStart, length: plan.CellCapacity),
            version: m_rowVersions[row]
        );
    }
    // Every version restarted at once, so no remembered version names a content any more.
    private void ForgetContents() {
        Array.Clear(array: m_contentAlias);
        Array.Clear(array: m_rowMemory);
    }
}
