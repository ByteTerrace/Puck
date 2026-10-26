namespace Puck.State;

// A column keeps storage only for the rows it reaches. Journal indices stay layout-relative; the backing arrays
// and settle stamps are row-local, so writing one timed cell never allocates clocks for every other row.
internal sealed class ArenaColumnBuffer<T>(ArenaLayout layout, ArenaColumn column) {
    private readonly T[]?[] m_rows = new T[]?[(layout.RowCount + 1)];

    public bool HasRow(int row) => (m_rows[row] is not null);

    public T this[int index] {
        get {
            Address(index: index, row: out var row, position: out var position, size: out _);
            return ((m_rows[row] is { } values) && (position < values.Length) ? values[position] : default!);
        }
        set {
            Address(index: index, row: out var row, position: out var position, size: out var size);
            (m_rows[row] ??= new T[size])[position] = value;
        }
    }

    public void ChangeLayout(ArenaLayout next) {
        layout = next;
        for (var row = 0; row < next.RowCount; row++) {
            if ((m_rows[row] is { } values) && (ArenaColumns.Space(column: column) == ArenaIndexSpace.Cell) && (values.Length != next[row].CellCapacity)) {
                Array.Resize(array: ref m_rows[row], newSize: next[row].CellCapacity);
            }
        }
    }

    private void Address(int index, out int row, out int position, out int size) {
        row = layout.RowOf(column: column, index: index);
        if (row < 0) {
            row = layout.RowCount;
            position = index;
            size = layout.LaneRosterCount;
            return;
        }
        ref readonly var plan = ref layout[row];
        (position, size) = ArenaColumns.Space(column: column) switch {
            ArenaIndexSpace.Cell => ((index - plan.CellStart), plan.CellCapacity),
            ArenaIndexSpace.MaskWord => ((index - plan.MaskWordStart), (plan.MaskCount * 4)),
            ArenaIndexSpace.LaneSlot => ((index - plan.LaneSlotStart), plan.LaneCapacity),
            _ => (0, 1),
        };
    }
}
