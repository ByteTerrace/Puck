namespace Puck.State;

/// <summary>A subset of a live arena's columns. Key ordinals remain local to that arena; this image never replaces
/// the arena's key table, lane rosters, declarations or unrelated rows.</summary>
public sealed record ArenaScopeSnapshot(int[] Rows, int[] Capacities, int[] CellStarts, int[] MaskWordStarts, ArenaNumberImage[] Numbers,
    ArenaTextImage[] Text, ArenaVisibilityImage[] Visibility, ArenaObservationImage[] Observations);
public sealed partial class StateArena {
    /// <summary>Captures selected plain document rows, including ring and draw cursors. Timed, vector and pool
    /// rows require a larger continuation boundary and are refused.</summary>
    public ArenaScopeSnapshot CaptureScope(IReadOnlySet<int> rows) {
        if (Journal.Scopes != 0) { throw new InvalidOperationException(message: "Cannot capture an open state transaction."); }
        foreach (var ordinal in rows) {
            if ((((uint)ordinal) >= ((uint)m_catalog.Descriptors.Count)) || (m_catalog.Descriptors[ordinal].Lane != StateLane.Document)) {
                throw new InvalidOperationException(message: "A scope captures only declared document rows.");
            }
            var row = m_rows[m_catalog.Descriptors[ordinal].LaneOrdinal];

            if (m_layout[ordinal].HostOwned ||
                m_catalog.IsPoolRow(rowOrdinal: ordinal) || (row.Kind == CellKind.Vector) || (row.Advance is not null) ||
                (row.Cycle is not null) || (row.Dynamics is not null) ||
                (row.Cells ?? []).Any(predicate: cell => ((cell.Clock is not null) || (cell.Advance is not null) || (cell.Cycle is not null) || (cell.Dynamics is not null)))) {
                throw new InvalidOperationException(message: $"Scope row '{row.Name}' requires unsupported continuation state.");
            }
        }
        var numbers = new List<ArenaNumberImage>();
        var text = new List<ArenaTextImage>();
        var visibility = new List<ArenaVisibilityImage>();
        var observations = new List<ArenaObservationImage>();

        foreach (var column in ArenaColumns.All) {
            if (column is ArenaColumn.CellCapacity or ArenaColumn.Vector) { continue; }
            for (var index = 0; (index < m_layout.Size(column: column)); index++) {
                var row = m_layout.RowOf(column: column, index: index);

                if (!rows.Contains(item: row) || !m_layout.HasColumn(column: column, row: row)) { continue; }
                switch (column) {
                    case ArenaColumn.Text:
                        if (ReadReferenceRaw(column: column, index: index) is string value) { text.Add(item: new(Index: index, Value: value)); }
                        break;
                    case ArenaColumn.Visibility:
                        if (ReadReferenceRaw(column: column, index: index) is StateVisibility policy) { visibility.Add(item: new(Index: index, Value: policy)); }
                        break;
                    case ArenaColumn.Observation:
                        if (ReadReferenceRaw(column: column, index: index) is StateObservation observation) { observations.Add(item: new(Index: index, Value: observation)); }
                        break;
                    default: numbers.Add(item: new(Column: column, Index: index, Value: ReadNumberRaw(column: column, index: index))); break;
                }
            }
        }
        var selected = rows.Order().ToArray();

        return new(selected, selected.Select(selector: row => m_layout[row].CellCapacity).ToArray(),
            selected.Select(selector: row => m_layout[row].CellStart).ToArray(), selected.Select(selector: row => m_layout[row].MaskWordStart).ToArray(),
            numbers.ToArray(), text.ToArray(), visibility.ToArray(), observations.ToArray());
    }
    /// <summary>Checks the complete scope image before any live column is changed.</summary>
    public void ValidateScope(ArenaScopeSnapshot image) {
        if (Journal.Scopes != 0) { throw new InvalidOperationException(message: "Cannot restore an open state transaction."); }
        if ((image.Rows.Length != image.Capacities.Length) || (image.Rows.Length != image.CellStarts.Length) ||
            (image.Rows.Length != image.MaskWordStarts.Length) || (image.Rows.Distinct().Count() != image.Rows.Length)) {
            throw new InvalidOperationException(message: "Invalid scope row inventory.");
        }
        for (var i = 0; (i < image.Rows.Length); i++) {
            if ((((uint)image.Rows[i]) >= ((uint)m_layout.RowCount)) || (m_layout[image.Rows[i]].CellCapacity != image.Capacities[i])) {
                throw new InvalidOperationException(message: "The replay scope's layout changed; capture it again.");
            }
        }
        var rows = image.Rows.ToHashSet();
        var expected = CaptureScope(rows: rows);
        var numbers = new HashSet<(ArenaColumn Column, int Index)>();

        foreach (var entry in image.Numbers) {
            if ((entry.Column is ArenaColumn.CellCapacity or ArenaColumn.Vector or ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation) ||
                !numbers.Add(item: (entry.Column, ScopePosition(image, entry.Column, entry.Index)))) {
                throw new InvalidOperationException(message: "Invalid scope numeric column inventory.");
            }
        }
        if (numbers.Count != expected.Numbers.Length) { throw new InvalidOperationException(message: "Incomplete scope numeric column inventory."); }
        ValidateReferences(indices: image.Text.Select(selector: entry => ScopePosition(image, ArenaColumn.Text, entry.Index)));
        ValidateReferences(indices: image.Visibility.Select(selector: entry => ScopePosition(image, ArenaColumn.Visibility, entry.Index)));
        ValidateReferences(indices: image.Observations.Select(selector: entry => ScopePosition(image, ArenaColumn.Observation, entry.Index)));
    }

    private static void ValidateReferences(IEnumerable<int> indices) {
        var seen = new HashSet<int>();

        foreach (var index in indices) {
            if (!seen.Add(item: index)) { throw new InvalidOperationException(message: "Duplicate scope reference column."); }
        }
    }

    /// <summary>Restores a scope into the same arena. Every unrelated column and retained key remains live.</summary>
    public void RestoreScope(ArenaScopeSnapshot image) {
        ValidateScope(image: image);
        var rows = image.Rows.ToHashSet();

        foreach (var column in new[] { ArenaColumn.Text, ArenaColumn.Visibility, ArenaColumn.Observation }) {
            for (var index = 0; (index < m_layout.Size(column: column)); index++) {
                if (rows.Contains(item: m_layout.RowOf(column: column, index: index)) && (ReadReferenceRaw(column: column, index: index) is not null)) {
                    WriteReferenceRaw(column: column, index: index, value: null);
                }
            }
        }
        foreach (var entry in image.Numbers) { WriteNumberRaw(column: entry.Column, index: ScopePosition(image, entry.Column, entry.Index), value: entry.Value); }
        foreach (var entry in image.Text) { WriteReferenceRaw(column: ArenaColumn.Text, index: ScopePosition(image, ArenaColumn.Text, entry.Index), value: entry.Value); }
        foreach (var entry in image.Visibility) { WriteReferenceRaw(column: ArenaColumn.Visibility, index: ScopePosition(image, ArenaColumn.Visibility, entry.Index), value: entry.Value); }
        foreach (var entry in image.Observations) { WriteReferenceRaw(column: ArenaColumn.Observation, index: ScopePosition(image, ArenaColumn.Observation, entry.Index), value: entry.Value); }
        foreach (var row in image.Rows) {
            if (m_layout[row].IsStored) { Reindex(row, m_layout[row]); }
            m_rowVersions[row]++; m_rowGenerations[row]++; m_deliveryRows[row] = true;
        }
    }

    private int ScopePosition(ArenaScopeSnapshot image, ArenaColumn column, int index) {
        if (Enum.IsDefined(value: column) && (index >= 0)) {
            for (var i = 0; (i < image.Rows.Length); i++) {
                var row = image.Rows[i];
                ref readonly var layout = ref m_layout[row];

                if (!m_layout.HasColumn(column: column, row: row)) { continue; }
                switch (ArenaColumns.Space(column: column)) {
                    case ArenaIndexSpace.Row when (index == row): return row;
                    case ArenaIndexSpace.Cell:
                        var offset = (index - image.CellStarts[i]);
                        if (((uint)offset) < ((uint)image.Capacities[i])) { return (layout.CellStart + offset); }
                        break;
                    case ArenaIndexSpace.MaskWord:
                        var word = (index - image.MaskWordStarts[i]);
                        if (((uint)word) < ((uint)(layout.MaskCount * 4))) { return (layout.MaskWordStart + word); }
                        break;
                }
            }
        }
        throw new InvalidOperationException(message: "Scope image writes outside its rows.");
    }
}
