namespace Puck.State;

/// <summary>Resolves stable names in state-section collections — the one allocation-free ordinal scan every reader
/// of a row list, a cell list, or a dynamics list shares.</summary>
public static class StateRows {
    /// <summary>Finds a state cell by key in a (possibly absent) cell list.</summary>
    /// <param name="cells">The row's cells, or <see langword="null"/> for a row declaring none.</param>
    /// <param name="key">The cell key to find.</param>
    /// <returns>The cell, or <see langword="null"/> when none carries that key.</returns>
    public static StateCell? FindCell(IReadOnlyList<StateCell>? cells, CellName key) {
        if (cells is null) {
            return null;
        }

        for (var index = 0; index < cells.Count; index++) {
            var cell = cells[index];
            if (cell.Key == key) {
                return cell;
            }
        }

        return null;
    }
    /// <summary>Finds a dynamics row by stable name.</summary>
    /// <param name="dynamics">The section's dynamics rows, or <see langword="null"/> for none.</param>
    /// <param name="name">The dynamics row name to find.</param>
    /// <returns>The row, or <see langword="null"/> when the section declares none by that name.</returns>
    public static DynamicsRow? FindDynamics(IReadOnlyList<DynamicsRow>? dynamics, string name) {
        if (dynamics is null) {
            return null;
        }

        for (var index = 0; index < dynamics.Count; index++) {
            var row = dynamics[index];
            if ((row is not null) && string.Equals(a: row.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return row;
            }
        }

        return null;
    }
    /// <summary>Finds a state row by stable name — the one row-find every reader of the section shares.</summary>
    /// <typeparam name="TRow">The row type the list carries; a document project's own derived row resolves as
    /// itself.</typeparam>
    /// <param name="rows">The section's rows, or <see langword="null"/> for none.</param>
    /// <param name="name">The row name to find.</param>
    /// <returns>The row, or <see langword="null"/> when the section declares none by that name.</returns>
    /// <remarks>Allocation-free and ordinal: a per-frame binding path runs this. A whole-document validator builds
    /// a name-keyed map once per walk instead, which a linear scan per lookup would turn quadratic.</remarks>
    public static TRow? FindStateRow<TRow>(IReadOnlyList<TRow>? rows, string name) where TRow : StateRow {
        if (rows is null) {
            return null;
        }

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];
            if ((row is not null) && string.Equals(a: row.Name.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                return row;
            }
        }

        return null;
    }
}
