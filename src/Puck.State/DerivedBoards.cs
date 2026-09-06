namespace Puck.State;

/// <summary>
/// Recomputes a <see cref="StateDomain.CellsOf"/> row's cells from its declared <see cref="StateRow.Inverse"/> — the
/// one derivation a host composes into a board row rather than authoring by hand. Never on the tick path: a host
/// recomposes this at whole-document compose and install, and the validator uses the identical answer to check an
/// authored board against it. A hypothetical frame keeps its own incremental recompute instead (see
/// <see cref="StateFrame"/>), since only the moved token's own two cells can have changed there.
/// </summary>
public static class DerivedBoards {
    /// <summary>Computes the sparse cell list a board's <see cref="StateInverse"/> derives: for every cell of
    /// <paramref name="topology"/>, the code of the LAST token — in <paramref name="inverse"/>'s <c>tokens</c> row's
    /// own cell order — whose value names that cell. A cell no token names carries no entry (the board reads its own
    /// declared empty value there); two tokens naming the same cell: the later one, in row order, wins.</summary>
    /// <param name="rows">The section's rows, searched by name for <paramref name="inverse"/>'s <c>tokens</c>/
    /// <c>codes</c> rows.</param>
    /// <param name="inverse">The board's declared inverse.</param>
    /// <param name="topology">The board's compiled topology.</param>
    /// <returns>The board's derived cells, ascending by cell ordinal; empty when <paramref name="inverse"/>'s rows
    /// do not both resolve.</returns>
    public static IReadOnlyList<StateCell> Compose(IReadOnlyList<StateRow> rows, StateInverse inverse, CompiledTopology topology) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: inverse);
        ArgumentNullException.ThrowIfNull(argument: topology);

        if (
            (StateRows.FindStateRow(rows: rows, name: inverse.Tokens.Value)?.Cells is not { } tokenCells) ||
            (StateRows.FindStateRow(rows: rows, name: inverse.Codes.Value)?.Cells is not { } codeCells)
        ) {
            return [];
        }

        var cellCount = topology.CellCount;
        var winner = new int[cellCount];

        Array.Fill(array: winner, value: -1);

        for (var index = 0; (index < tokenCells.Count); index++) {
            var cell = tokenCells[index].Value;

            if ((cell >= 0) && (cell < cellCount)) {
                winner[cell] = index;
            }
        }

        var cells = new List<StateCell>(capacity: cellCount);

        for (var cell = 0; (cell < cellCount); cell++) {
            var token = winner[cell];

            if (token < 0) {
                continue;
            }

            cells.Add(item: new StateCell(
                Key: CellName.Parse(candidate: topology.Key(cell: cell)),
                Value: ((token < codeCells.Count) ? codeCells[token].Value : 0L)
            ));
        }

        return cells;
    }
}
