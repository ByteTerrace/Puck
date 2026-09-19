namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Reads one lattice row's cells by topology cell ordinal, answering the row's declared empty value
    /// for a cell it does not hold.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="values">The span to fill, one entry per topology cell.</param>
    /// <returns><see langword="true"/> when the row is a lattice the arena stores and
    /// <paramref name="values"/> is long enough.</returns>
    public bool TryReadBoard(int rowOrdinal, Span<long> values) {
        if (
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.Shape != RowShape.Lattice) ||
            (values.Length < layout.CellCapacity)
        ) {
            return false;
        }

        for (var cell = 0; (cell < layout.CellCapacity); cell++) {
            var slot = (layout.CellStart + cell);

            values[cell] = (Bit(
                index: slot,
                words: m_presence
            )
                ? m_numbers[slot]
                : layout.Empty
            );
        }

        return true;
    }
    /// <summary>Reads one lattice cell by topology cell ordinal.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cell">The topology cell ordinal.</param>
    /// <param name="value">The stored value, or the row's declared empty value when the cell is absent.</param>
    /// <returns><see langword="true"/> when the row is a lattice the arena stores and <paramref name="cell"/> is
    /// one of its cells.</returns>
    public bool TryReadBoardCell(int rowOrdinal, int cell, out long value) {
        if (
            TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) &&
            (layout.Shape == RowShape.Lattice) &&
            (((uint)cell) < ((uint)layout.CellCapacity))
        ) {
            var slot = (layout.CellStart + cell);

            value = (Bit(
                index: slot,
                words: m_presence
            )
                ? m_numbers[slot]
                : layout.Empty
            );

            return true;
        }

        value = 0L;

        return false;
    }
    /// <summary>Attempts to write one lattice cell by topology cell ordinal, against the row's declared
    /// envelope.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cell">The topology cell ordinal.</param>
    /// <param name="value">The replacement for a set, or the addend for an add.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    public bool TryWriteBoardCell(int rowOrdinal, int cell, long value, StateWriteKind write, out string reason) {
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
        if (((uint)cell) >= ((uint)layout.CellCapacity)) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' has {layout.CellCapacity} cells, so cell {cell} names none";

            return false;
        }

        var slot = (layout.CellStart + cell);
        var row = DocumentRow(rowOrdinal: rowOrdinal);
        var current = (Bit(
            index: slot,
            words: m_presence
        )
            ? m_numbers[slot]
            : layout.Empty
        );

        _ = m_catalog.TryGetEnum(
            handle: m_catalog.Descriptors[rowOrdinal].Handle,
            symbols: out var symbols
        );

        if (!row.TryAdmitWrite(
            current: current,
            operand: value,
            reason: out reason,
            stored: out var next,
            symbols: symbols,
            write: write
        )) {
            reason = $"row '{row.Name.Value}' cell {cell} {reason}";

            return false;
        }

        StoreNumber(
            next: next,
            previous: current,
            rowOrdinal: rowOrdinal,
            slot: slot
        );

        reason = string.Empty;

        return true;
    }

    // The one internal door every keyed write reaches its derived boards through: a token that moved changes at
    // most the cell it left and the cell it entered, and a code write changes only the cell its own token stands
    // on.
    private void RecomputeDependents(int rowOrdinal, int slot, long previous, long next) {
        ref readonly var layout = ref m_layout[rowOrdinal];

        if (layout.Shape is not (RowShape.Keyed or RowShape.Ordered)) {
            return;
        }

        var position = (slot - layout.CellStart);

        if (m_layout.DependentBoardsOfTokens(tokensOrdinal: rowOrdinal) is { } boards) {
            foreach (var boardOrdinal in boards) {
                if (previous != next) {
                    RecomputeDerivedCell(
                        boardOrdinal: boardOrdinal,
                        cell: previous
                    );
                }

                RecomputeDerivedCell(
                    boardOrdinal: boardOrdinal,
                    cell: next
                );
            }
        }
        if (m_layout.DependentBoardsOfCodes(codesOrdinal: rowOrdinal) is { } coded) {
            foreach (var boardOrdinal in coded) {
                var tokens = m_layout[m_layout[boardOrdinal].InverseTokensOrdinal];

                if (position < tokens.CellCapacity) {
                    RecomputeDerivedCell(
                        boardOrdinal: boardOrdinal,
                        cell: m_numbers[(tokens.CellStart + position)]
                    );
                }
            }
        }
    }
    // A membership change renumbers a token row, so which token wins a cell can change anywhere: the boards it
    // feeds are rebuilt whole rather than incrementally.
    private void RecomputeMembership(int rowOrdinal) {
        if (m_layout.DependentBoardsOfTokens(tokensOrdinal: rowOrdinal) is { } boards) {
            foreach (var boardOrdinal in boards) {
                RecomputeDerivedBoard(boardOrdinal: boardOrdinal);
            }
        }
        if (m_layout.DependentBoardsOfCodes(codesOrdinal: rowOrdinal) is { } coded) {
            foreach (var boardOrdinal in coded) {
                RecomputeDerivedBoard(boardOrdinal: boardOrdinal);
            }
        }
    }
    private void RecomputeDerivedBoard(int boardOrdinal) {
        ref readonly var board = ref m_layout[boardOrdinal];

        for (var cell = 0; (cell < board.CellCapacity); cell++) {
            RecomputeDerivedCell(
                boardOrdinal: boardOrdinal,
                cell: cell
            );
        }
    }
    // Two tokens naming one cell: the later one, in the token row's own order, wins.
    private void RecomputeDerivedCell(int boardOrdinal, long cell) {
        ref readonly var board = ref m_layout[boardOrdinal];

        if (((ulong)cell) >= ((ulong)board.CellCapacity)) {
            return;
        }

        ref readonly var tokens = ref m_layout[board.InverseTokensOrdinal];
        ref readonly var codes = ref m_layout[board.InverseCodesOrdinal];
        var count = ((int)m_memberCounts[board.InverseTokensOrdinal]);
        var winner = -1;

        for (var position = 0; (position < count); position++) {
            if (m_numbers[(tokens.CellStart + position)] == cell) {
                winner = position;
            }
        }

        var slot = (board.CellStart + ((int)cell));
        var value = (((winner >= 0) && (winner < m_memberCounts[board.InverseCodesOrdinal]))
            ? m_numbers[(codes.CellStart + winner)]
            : board.Empty
        );

        WriteNumber(
            column: ArenaColumn.Number,
            index: slot,
            value: value
        );
        WriteNumber(
            column: ArenaColumn.Presence,
            index: slot,
            value: ((winner >= 0)
                ? 1L
                : 0L
            )
        );
    }
}
