using Puck.Maths;

namespace Puck.State;

// The value planes of every small board: one BitmapIndex64 per stored lattice row of at most BoardMask.MaxCells cells
// whose declared values fit a plane window (BoardMask.TryPlaneWindow), so a range mask over the board is an OR of
// planes rather than a read of every cell.
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
    private BitmapIndex64?[] m_boardPlanes = [];

    /// <summary>Reads the cells of a small board whose value lies in an inclusive range from the board's value planes,
    /// one bit per topology cell ordinal, reading only the planes the range covers.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="lower">The range's inclusive lower bound.</param>
    /// <param name="upper">The range's inclusive upper bound.</param>
    /// <param name="mask">The cells in range, or zero when the row keeps no planes.</param>
    /// <returns><see langword="true"/> when the row keeps planes: a stored lattice of at most
    /// <see cref="BoardMask.MaxCells"/> cells whose declarations give it a plane window
    /// (<see cref="BoardMask.TryPlaneWindow"/>). Any other row is read cell by cell instead.</returns>
    public bool TryReadBoardPlanes(int rowOrdinal, long lower, long upper, out ulong mask) {
        if ((((uint)rowOrdinal) >= ((uint)m_boardPlanes.Length)) || (m_boardPlanes[rowOrdinal] is not { } planes)) {
            mask = 0UL;

            return false;
        }
        if (m_boardPlaneGenerations[rowOrdinal] != m_rowGenerations[rowOrdinal]) {
            LoadBoardPlanes(planes: planes, rowOrdinal: rowOrdinal);
        }

        Visit(lanes: BitmapIndex64.RangeReads(
            lower: lower,
            upper: upper,
            width: planes.Width,
            windowLower: planes.Lower
        ));
        mask = planes.Range(
            lower: lower,
            upper: upper
        );

        return true;
    }

    // Builds the planes of every board that keeps them from the arena's current storage, replacing any it held.
    private void InstallBoardPlanes() {
        var planes = new BitmapIndex64?[m_layout.RowCount];
        var any = false;

        for (var row = 0; (row < planes.Length); row++) {
            ref readonly var layout = ref m_layout[row];

            if (
                (layout.Shape != RowShape.Lattice) ||
                !layout.IsStored ||
                (layout.CellCapacity is < 1 or > BoardMask.MaxCells) ||
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

            planes[row] = new BitmapIndex64(
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
    private void LoadBoardPlanes(BitmapIndex64 planes, int rowOrdinal) {
        Span<long> values = stackalloc long[BoardMask.MaxCells];

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
