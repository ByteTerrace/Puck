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
        RequireScopeRows(rows: rows);
        var image = CaptureImage(positions: ImagePositions(layout: m_layout, rows: rows), omitDefaults: false);
        var selected = rows.Order().ToArray();

        return new(selected, selected.Select(selector: row => m_layout[row].CellCapacity).ToArray(),
            selected.Select(selector: row => m_layout[row].CellStart).ToArray(), selected.Select(selector: row => m_layout[row].MaskWordStart).ToArray(),
            image.Numbers, image.Text, image.Visibility, image.Observations);
    }

    /// <summary>Returns why a set of declared document rows cannot form one replay scope, one reason per refusal. A
    /// scope holds only plain rows (no vector or timed state) and is closed under derivation: a derived board belongs
    /// to a scope exactly when both its input rows do, since a restore writes the scope's columns raw and a board left
    /// outside would keep what the drive derived from inputs the restore put back.</summary>
    /// <param name="rows">The document's declared rows.</param>
    /// <param name="scope">The names of the rows the scope selects.</param>
    /// <returns>The refusals; empty when the rows form a scope as far as their declarations show.</returns>
    public static IEnumerable<string> ScopeRefusals(IEnumerable<StateRow> rows, IReadOnlySet<string> scope) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: scope);

        foreach (var row in rows) {
            var name = row.Name.Value;
            var inverse = row.Inverse;

            if (scope.Contains(item: name)) {
                if ((row.Kind == CellKind.Vector) || (row.Advance is not null) || (row.Cycle is not null) || (row.Dynamics is not null) ||
                    (row.Cells ?? []).Any(predicate: cell => ((cell.Clock is not null) || (cell.Advance is not null) || (cell.Cycle is not null) || (cell.Dynamics is not null)))) {
                    yield return $"row '{name}' needs a wider continuation boundary (vector or timed state)";
                }
                if ((inverse is not null) && (!scope.Contains(item: inverse.Tokens.Value) || !scope.Contains(item: inverse.Codes.Value))) {
                    yield return $"derived board '{name}' requires both inverse input rows";
                }
            } else if ((inverse is not null) && (scope.Contains(item: inverse.Tokens.Value) || scope.Contains(item: inverse.Codes.Value))) {
                yield return $"derived board '{name}' must join the scope holding its input rows, or a restore leaves it stale";
            }
        }
    }

    private void RequireScopeRows(IReadOnlySet<int> rows) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var ordinal in rows) {
            if ((((uint)ordinal) >= ((uint)m_catalog.Descriptors.Count)) || (m_catalog.Descriptors[ordinal].Lane != StateLane.Document)) {
                throw new InvalidOperationException(message: "A scope captures only declared document rows.");
            }
            var row = m_rows[m_catalog.Descriptors[ordinal].LaneOrdinal];

            if (m_layout[ordinal].HostOwned || m_catalog.IsPoolRow(rowOrdinal: ordinal)) {
                throw new InvalidOperationException(message: $"Scope row '{row.Name}' requires unsupported continuation state.");
            }
            names.Add(item: row.Name.Value);
        }
        foreach (var refusal in ScopeRefusals(rows: m_rows, scope: names)) {
            throw new InvalidOperationException(message: $"Scope {refusal}.");
        }
    }

    /// <summary>Checks the complete scope image before any live column is changed.</summary>
    public void ValidateScope(ArenaScopeSnapshot image) => _ = PrepareScope(image: image);

    private ArenaLayout PrepareScope(ArenaScopeSnapshot image) {
        if (Journal.Scopes != 0) { throw new InvalidOperationException(message: "Cannot restore an open state transaction."); }
        if ((image.Rows.Length != image.Capacities.Length) || (image.Rows.Length != image.CellStarts.Length) ||
            (image.Rows.Length != image.MaskWordStarts.Length) || (image.Rows.Distinct().Count() != image.Rows.Length)) {
            throw new InvalidOperationException(message: "Invalid scope row inventory.");
        }
        var rows = image.Rows.ToHashSet();
        RequireScopeRows(rows: rows);
        var layout = SnapshotLayout(capacities: image.Capacities, rows: image.Rows);
        var expected = ImagePositions(layout: layout, rows: rows).Count(entry => entry.Column is not (ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation));
        var numbers = new HashSet<(ArenaColumn Column, int Index)>();

        foreach (var entry in image.Numbers) {
            var position = ScopePosition(image, entry.Column, entry.Index, layout);
            if ((entry.Column is ArenaColumn.CellCapacity or ArenaColumn.Vector or ArenaColumn.Text or ArenaColumn.Visibility or ArenaColumn.Observation) ||
                !numbers.Add(item: (entry.Column, position))) {
                throw new InvalidOperationException(message: "Invalid scope numeric column inventory.");
            }
            RequireImagePosition(layout: layout, column: entry.Column, index: position, number: entry.Value, reference: null, what: "scope image");
        }
        if (numbers.Count != expected) { throw new InvalidOperationException(message: "Incomplete scope numeric column inventory."); }
        ValidateReferences(image: image, layout: layout, column: ArenaColumn.Text, entries: image.Text.Select(selector: entry => (entry.Index, (object)entry.Value)));
        ValidateReferences(image: image, layout: layout, column: ArenaColumn.Visibility, entries: image.Visibility.Select(selector: entry => (entry.Index, (object)entry.Value)));
        ValidateReferences(image: image, layout: layout, column: ArenaColumn.Observation, entries: image.Observations.Select(selector: entry => (entry.Index, (object)entry.Value)));
        return layout;
    }

    private void ValidateReferences(ArenaScopeSnapshot image, ArenaLayout layout, ArenaColumn column, IEnumerable<(int Index, object Value)> entries) {
        var seen = new HashSet<int>();

        foreach (var (index, value) in entries) {
            var position = ScopePosition(image, column, index, layout);
            if (!seen.Add(item: position)) { throw new InvalidOperationException(message: "Duplicate scope reference column."); }
            RequireImagePosition(layout: layout, column: column, index: position, number: 0L, reference: value, what: "scope image");
        }
    }

    /// <summary>Restores a scope into the same arena. Every unrelated column and retained key remains live.</summary>
    public void RestoreScope(ArenaScopeSnapshot image) {
        var layout = PrepareScope(image: image);
        var rows = image.Rows.ToHashSet();

        foreach (var column in new[] { ArenaColumn.Text, ArenaColumn.Visibility, ArenaColumn.Observation }) {
            for (var index = 0; (index < m_layout.Size(column: column)); index++) {
                if (rows.Contains(item: m_layout.RowOf(column: column, index: index)) && (ReadReferenceRaw(column: column, index: index) is not null)) {
                    WriteReferenceRaw(column: column, index: index, value: null);
                }
            }
        }
        // Clear references before shrinking, including visibility payload charges in positions being removed.
        if (!ReferenceEquals(layout, m_layout)) { InstallReservation(next: layout); }
        foreach (var entry in image.Numbers) { WriteNumberRaw(column: entry.Column, index: ScopePosition(image, entry.Column, entry.Index), value: entry.Value); }
        foreach (var entry in image.Text) { WriteReferenceRaw(column: ArenaColumn.Text, index: ScopePosition(image, ArenaColumn.Text, entry.Index), value: entry.Value); }
        foreach (var entry in image.Visibility) { WriteReferenceRaw(column: ArenaColumn.Visibility, index: ScopePosition(image, ArenaColumn.Visibility, entry.Index), value: entry.Value); }
        foreach (var entry in image.Observations) { WriteReferenceRaw(column: ArenaColumn.Observation, index: ScopePosition(image, ArenaColumn.Observation, entry.Index), value: entry.Value); }
        foreach (var row in image.Rows) {
            if (m_layout[row].IsStored) { Reindex(row, m_layout[row]); }
            BumpRowVersion(rowOrdinal: row); m_rowGenerations[row]++; m_deliveryRows[row] = true;
        }
    }

    private int ScopePosition(ArenaScopeSnapshot image, ArenaColumn column, int index, ArenaLayout? target = null) {
        var destination = target ?? m_layout;
        if (Enum.IsDefined(value: column) && (index >= 0)) {
            for (var i = 0; (i < image.Rows.Length); i++) {
                var row = image.Rows[i];
                ref readonly var layout = ref destination[row];

                if (!destination.HasColumn(column: column, row: row)) { continue; }
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
