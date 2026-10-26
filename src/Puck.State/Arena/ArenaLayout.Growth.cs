namespace Puck.State;

public sealed partial class ArenaLayout {
    internal ArenaLayout WithCellCapacity(int rowOrdinal, int capacity) {
        if ((capacity < 0) || (capacity > StateCapacity.MaxCellsPerRow) || !HasColumn(row: rowOrdinal, column: ArenaColumn.CellCapacity)) {
            throw new InvalidOperationException(message: "Only a growable row can change its reserved cell room.");
        }
        var rows = (ArenaRowLayout[])m_rows.Clone();
        rows[rowOrdinal] = rows[rowOrdinal] with { CellCapacity = capacity };
        var columns = new ArenaColumnRange[m_columns.Length];
        var cells = 0;
        var vectors = 0;
        for (var index = 0; index < columns.Length; index++) {
            var column = m_columns[index];
            var first = cells;
            for (var member = column.FirstRow; member < column.FirstRow + column.RowCount; member++) {
                var ordinal = m_columnRows[member];
                var row = rows[ordinal];
                if (!row.IsStored) { continue; }
                rows[ordinal] = row with { CellStart = cells, VectorByteStart = vectors };
                cells = checked(cells + row.CellCapacity);
                vectors = checked(vectors + row.CellCapacity * row.Dimensions);
            }
            columns[index] = column with { FirstCell = first, CellCount = cells - first };
        }
        var positions = new long[ArenaColumns.All.Length];
        var arrayBytes = 0L;
        var textSlots = 0L;
        foreach (var row in rows) {
            if (row.Kind == CellKind.Text) { textSlots += row.CellCapacity; }
            foreach (var column in ArenaColumns.All) {
                if (IsOptional(column: column) && row.HasColumn(column: column)) {
                    positions[(int)column] += ArenaColumns.Space(column: column) == ArenaIndexSpace.Cell ? row.CellCapacity : 1;
                    arrayBytes += 3L * ArrayOverheadBytes;
                }
            }
        }
        RequireFits(declaredPositions: positions, columnArrayBytes: arrayBytes, what: $"State row '{rowOrdinal}' growth", cellSlots: cells, textSlots: textSlots, rowCount: RowCount,
            maskWords: MaskWordCount, laneSlots: LaneSlotCount, laneRoster: LaneRosterCount, poolCount: m_poolCount, poolWords: m_poolWordCount, vectorBytes: vectors);
        var owners = new int[cells];
        foreach (var row in rows) {
            if (row.IsStored) { owners.AsSpan(start: row.CellStart, length: row.CellCapacity).Fill(value: row.Ordinal); }
        }
        return new ArenaLayout(rows: rows, rowColumns: m_rowColumns, columns: columns, columnRows: m_columnRows, rowOfCellSlot: owners,
            rowOfLaneSlot: m_rowOfLaneSlot, rowOfMaskWord: m_rowOfMaskWord, laneRosterBase: m_laneRosterBase, dependents: m_dependents, codeDependents: m_codeDependents,
            cellSlotCount: cells, vectorByteCount: vectors, laneSlotCount: LaneSlotCount, laneRosterCount: LaneRosterCount, maskWordCount: MaskWordCount,
            poolCount: m_poolCount, poolWordCount: m_poolWordCount, options: Options);
    }

    internal ArenaJournalEntry Remap(ArenaJournalEntry entry, ArenaLayout next) {
        if (ArenaColumns.Space(column: entry.Column) != ArenaIndexSpace.Cell) { return entry; }
        var row = RowOf(column: entry.Column, index: entry.Index);
        return entry with { Index = next[row].CellStart + entry.Index - m_rows[row].CellStart };
    }
}
