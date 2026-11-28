using Puck.Maths;

namespace Puck.State;

// The value planes of every board: one BitmapIndex per stored lattice row whose declared values fit a plane window
// (BoardMask.TryPlaneWindow), so a range mask over the board is an OR of plane words rather than a read of every cell,
// whatever the board's size: a 64-cell board's planes are one word each and a 361-cell board's six.
//
// Invariant: a row's planes answer the row's storage exactly when m_boardPlaneGenerations[row] equals
// m_rowGenerations[row]. The generation moves only through the two doors below and the two whole-arena resets
// (construction and Adopt), each of which installs the planes again:
// - BumpGeneration, the per-position door, sets the one lane a Number or Presence write changed and restamps, but
//   only when the planes stood before the write; any other column leaves them stale.
// - BumpRowGeneration, the whole-row door (a restored image, a moved reservation), never restamps, so the next read
//   loads the row again from storage.
// A writer that changes a board cell without moving its row's generation breaks this as it breaks every other cache
// keyed by the generation.
public sealed partial class StateArena {
    private ulong[] m_boardPlaneGenerations = [];
    private BitmapIndex?[] m_boardPlanes = [];

    /// <summary>Reads the cells of a board whose value lies in an inclusive range from the board's value planes, one
    /// bit per topology cell ordinal, reading only the plane words the range covers.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="lower">The range's inclusive lower bound.</param>
    /// <param name="upper">The range's inclusive upper bound.</param>
    /// <param name="mask">The cells in range, at least <see cref="BitWords.WordsFor"/> of the row's cell count words;
    /// exactly that many are written when the row keeps planes, and none otherwise.</param>
    /// <returns><see langword="true"/> when the row keeps planes: a stored lattice whose declarations give it a plane
    /// window (<see cref="BoardMask.TryPlaneWindow"/>). Any other row is read cell by cell instead
    /// (<see cref="TryReadBoardRange"/> does either).</returns>
    public bool TryReadBoardPlanes(int rowOrdinal, long lower, long upper, Span<ulong> mask) {
        if (!TryBoardPlanes(planes: out var planes, rowOrdinal: rowOrdinal) || (mask.Length < planes.Words)) {
            return false;
        }

        Visit(lanes: BitmapIndex.RangeReads(
            lanes: planes.Lanes,
            lower: lower,
            upper: upper,
            width: planes.Width,
            windowLower: planes.Lower
        ));
        planes.Range(
            destination: mask,
            lower: lower,
            upper: upper
        );

        return true;
    }
    /// <summary>Reads the window of values a board's planes span, when the row keeps planes: every value its cells can
    /// hold, as <see cref="BoardMask.TryPlaneWindow"/> reads it from the row's declarations.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="lower">The window's least value, or zero.</param>
    /// <param name="width">How many consecutive values the window holds, or zero.</param>
    /// <returns><see langword="true"/> when the row keeps planes.</returns>
    public bool TryBoardPlaneWindow(int rowOrdinal, out long lower, out int width) {
        if ((((uint)rowOrdinal) >= ((uint)m_boardPlanes.Length)) || (m_boardPlanes[rowOrdinal] is not { } planes)) {
            lower = 0L;
            width = 0;

            return false;
        }

        lower = planes.Lower;
        width = planes.Width;

        return true;
    }
    /// <summary>Reads the cells of a lattice row whose value lies in an inclusive range, one bit per topology cell
    /// ordinal: from the row's value planes when it keeps them, else by reading every cell. A cell the row does not
    /// hold reads as the row's declared empty value, as <see cref="TryReadBoard"/> answers it.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="lower">The range's inclusive lower bound.</param>
    /// <param name="upper">The range's inclusive upper bound.</param>
    /// <param name="mask">The cells in range, at least <see cref="BitWords.WordsFor"/> of the row's cell count words;
    /// exactly that many are written.</param>
    /// <returns><see langword="true"/> when the row is a lattice the arena stores and <paramref name="mask"/> is long
    /// enough.</returns>
    public bool TryReadBoardRange(int rowOrdinal, long lower, long upper, Span<ulong> mask) {
        if (TryReadBoardPlanes(lower: lower, mask: mask, rowOrdinal: rowOrdinal, upper: upper)) {
            return true;
        }
        if (
            !TryRowLayout(layout: out var layout, rowOrdinal: rowOrdinal) ||
            (layout.Shape != RowShape.Lattice) ||
            (mask.Length < BitWords.WordsFor(members: layout.CellCapacity))
        ) {
            return false;
        }

        var words = mask[..BitWords.WordsFor(members: layout.CellCapacity)];

        words.Clear();
        Visit(lanes: layout.CellCapacity);
        for (var cell = 0; (cell < layout.CellCapacity); cell++) {
            var value = LatticeValue(layout: in layout, slot: (layout.CellStart + cell));

            if ((value >= lower) && (value <= upper)) {
                BitWords.Add(index: cell, words: words);
            }
        }

        return true;
    }
    /// <summary>Attempts to write one value into every cell of a set of a lattice row's cells, against the row's declared
    /// envelope: the value is admitted once, and a cell that already holds it is left as it stands, so the journal
    /// records, the row's version moves and its readers wake only for the cells that change. A cell the row does not
    /// hold is written even when the value is the row's empty value, so the set's cells are held afterwards, as a
    /// write of each through <see cref="TryWriteBoardCell"/> leaves them.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cells">The cells to write, one bit per topology cell ordinal; a word past the span is empty.</param>
    /// <param name="value">The replacement every cell takes.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored; nothing is written on a refusal.</returns>
    public bool TryWriteBoardCells(int rowOrdinal, ReadOnlySpan<ulong> cells, long value, out string reason) {
        if (
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.Shape != RowShape.Lattice)
        ) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is not a lattice the arena stores";

            return false;
        }
        if (layout.IsDerivedBoard) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' is a derived board — write its token row instead";

            return false;
        }

        var count = layout.CellCapacity;
        var words = BitWords.WordsFor(members: count);

        for (var word = words; (word < cells.Length); word++) {
            if (cells[word] != 0UL) {
                reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' has {count} cells, so cell {(word * 64) + System.Numerics.BitOperations.TrailingZeroCount(value: cells[word])} names none";

                return false;
            }
        }
        if (((count & 63) != 0) && (words <= cells.Length) && ((cells[(words - 1)] & ~(count & 63).LowMask<ulong>()) != 0UL)) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' has {count} cells, so cell {((words - 1) * 64) + System.Numerics.BitOperations.TrailingZeroCount(value: (cells[(words - 1)] & ~(count & 63).LowMask<ulong>()))} names none";

            return false;
        }

        using var pendingLease = Scratch.Rent<ulong>(length: words);
        var pending = pendingLease.Span;

        cells[..Math.Min(val1: words, val2: cells.Length)].CopyTo(destination: pending);
        if (BitWords.Count(words: pending) == 0) {
            reason = string.Empty;

            return true;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);

        _ = m_catalog.TryGetEnum(
            handle: m_catalog.Descriptors[rowOrdinal].Handle,
            symbols: out var symbols
        );

        if (!row.TryAdmitWrite(
            current: 0L,
            operand: value,
            reason: out reason,
            stored: out var next,
            symbols: symbols,
            write: StateWriteKind.Set
        )) {
            foreach (var first in BitWords.Members(words: pending)) {
                reason = $"row '{row.Name.Value}' cell {first} {reason}";

                break;
            }

            return false;
        }

        // A cell is left alone only when it is held and already holds the value: its planes answer which cells read as
        // the value, and the presence column which of those are held.
        using var heldLease = Scratch.Rent<ulong>(length: words);
        var held = heldLease.Span;

        BitWords.Extract(count: count, destination: held, start: layout.CellStart, words: m_presence);
        if (TryBoardPlanes(planes: out var planes, rowOrdinal: rowOrdinal)) {
            using var equalLease = Scratch.Rent<ulong>(length: words);
            var equal = equalLease.Span;

            Visit(lanes: BitmapIndex.RangeReads(lanes: count, lower: next, upper: next, width: planes.Width, windowLower: planes.Lower));
            planes.Range(destination: equal, lower: next, upper: next);
            for (var word = 0; (word < words); word++) {
                pending[word] &= ~(held[word] & equal[word]);
            }
        } else {
            foreach (var cell in BitWords.Members(words: held)) {
                if (BitWords.Contains(index: cell, words: pending) && (m_numbers[(layout.CellStart + cell)] == next)) {
                    BitWords.Remove(index: cell, words: pending);
                }
            }
        }
        foreach (var cell in BitWords.Members(words: pending)) {
            var slot = (layout.CellStart + cell);

            StoreNumber(
                next: next,
                previous: LatticeValue(layout: in layout, slot: slot),
                rowOrdinal: rowOrdinal,
                slot: slot
            );
        }

        reason = string.Empty;

        return true;
    }

    // The planes of a row that keeps them, loaded from storage when a whole-row change left them stale.
    private bool TryBoardPlanes(int rowOrdinal, out BitmapIndex planes) {
        if ((((uint)rowOrdinal) >= ((uint)m_boardPlanes.Length)) || (m_boardPlanes[rowOrdinal] is not { } index)) {
            planes = null!;

            return false;
        }
        if (m_boardPlaneGenerations[rowOrdinal] != m_rowGenerations[rowOrdinal]) {
            LoadBoardPlanes(planes: index, rowOrdinal: rowOrdinal);
        }

        planes = index;

        return true;
    }
    // Builds the planes of every board that keeps them from the arena's current storage, replacing any it held.
    private void InstallBoardPlanes() {
        var planes = new BitmapIndex?[m_layout.RowCount];
        var any = false;

        for (var row = 0; (row < planes.Length); row++) {
            ref readonly var layout = ref m_layout[row];

            if (
                (layout.Shape != RowShape.Lattice) ||
                !layout.IsStored ||
                (layout.CellCapacity is < 1 or > BitmapIndex.MaxLanes) ||
                !BoardMask.TryPlaneWindow(
                    catalog: m_catalog,
                    lower: out var lower,
                    row: DocumentRow(rowOrdinal: row),
                    rowOrdinal: row,
                    width: out var width
                )
            ) {
                continue;
            }

            planes[row] = new BitmapIndex(
                lanes: layout.CellCapacity,
                lower: lower,
                width: width
            );
            any = true;
        }

        m_boardPlanes = (any ? planes : []);
        m_boardPlaneGenerations = (any ? new ulong[planes.Length] : []);
        for (var row = 0; (row < m_boardPlanes.Length); row++) {
            if (m_boardPlanes[row] is { } index) {
                LoadBoardPlanes(planes: index, rowOrdinal: row);
            }
        }
    }
    private void LoadBoardPlanes(BitmapIndex planes, int rowOrdinal) {
        using var valuesLease = Scratch.Rent<long>(length: planes.Lanes);
        var values = valuesLease.Span;

        _ = TryReadBoard(
            rowOrdinal: rowOrdinal,
            values: values
        );
        planes.Load(values: values);
        m_boardPlaneGenerations[rowOrdinal] = m_rowGenerations[rowOrdinal];
    }
    // The per-position door's half of the invariant: `before` is the row's generation as the write found it.
    private void NoteBoardPlanes(int row, ArenaColumn column, int index, ulong before) {
        if ((m_boardPlanes[row] is not { } planes) || (m_boardPlaneGenerations[row] != before)) {
            return;
        }
        if (column is not (ArenaColumn.Number or ArenaColumn.Presence)) {
            return;
        }

        ref readonly var layout = ref m_layout[row];
        var cell = (index - layout.CellStart);

        if (((uint)cell) < ((uint)planes.Lanes)) {
            planes.Set(
                lane: cell,
                value: LatticeValue(layout: in layout, slot: index)
            );
        }
        m_boardPlaneGenerations[row] = m_rowGenerations[row];
    }
}
