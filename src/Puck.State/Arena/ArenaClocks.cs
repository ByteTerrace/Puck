using Puck.Maths;

namespace Puck.State;

// Nested clocks in the arena: the arena is the cell source a nested advance reads its parent from, and the one place a
// write that moves a parent's line settles the rows nested on it first.
//
// The invariant: a nested row's epoch never stands before its parent's, so the nested value, which reads its parent's
// line at its own epoch and at the read, reads the parent's true line at both. Every arena write that moves a row's
// line passes SettleNested before it stores: TryWriteLive for a cell write, TrySettleRedeclared for a redeclaration.
// The catalog's NestedClocks names the rows each parent settles, from declarations alone.
public sealed partial class StateArena : IStateClockCells {
    /// <inheritdoc />
    /// <remarks>The arena answers a document row's slot as it is stored now: its base, its clock's engine epoch and the
    /// advance that governs it, or none.</remarks>
    public bool TryClockCell(string row, out StateClockCell cell) {
        cell = default;

        return (
            m_catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row) &&
            TryClockCellAt(cell: out cell, rowOrdinal: handle.Ordinal)
        );
    }
    /// <summary>Reads the polynomial a document row's slot follows from an engine tick on, for a recipient to predict it
    /// with: the slot's stored base and its exact value as a polynomial in the engine ticks elapsed since
    /// <paramref name="engineTick"/>, through every parent it is nested on (<see cref="StateAdvance.TryValueCurve"/>).</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="engineTick">The engine tick the polynomial's variable counts from.</param>
    /// <param name="baseValue">The slot's stored raw base; zero when none is returned.</param>
    /// <param name="curve">The slot's exact value, in its displayed unit, over the elapsed engine ticks; the zero
    /// polynomial when none is returned.</param>
    /// <returns><see langword="true"/> when the slot advances and follows a polynomial from
    /// <paramref name="engineTick"/>.</returns>
    public bool TryReadClockCurve(int rowOrdinal, ulong engineTick, out long baseValue, out RationalPolynomial curve) {
        baseValue = 0L;
        curve = RationalPolynomial.Zero;

        if (!TryClockCellAt(cell: out var cell, rowOrdinal: rowOrdinal) || (cell.Advance is not { } advance)) { return false; }

        baseValue = cell.Base;

        return advance.TryValueCurve(baseValue: cell.Base, cells: this, curve: out curve, engineTick: engineTick, epochEngineTick: cell.EpochEngineTick, row: cell.Row);
    }

    private bool TryClockCellAt(int rowOrdinal, out StateClockCell cell) {
        cell = default;

        if (
            !TryRowLayout(layout: out var layout, rowOrdinal: rowOrdinal) ||
            (layout.Kind is not (CellKind.Fixed or CellKind.Int)) ||
            !m_keys.TryResolve(key: out var key, name: StateRow.SlotKey) ||
            !TryPresentSlot(key: key, rowOrdinal: rowOrdinal, slot: out var slot)
        ) {
            return false;
        }

        var behavior = (layout.HasTraits
            ? BehaviorAt(name: StateRow.SlotKey, rowOrdinal: rowOrdinal, slot: slot)
            : EffectiveBehavior.None
        );

        cell = new StateClockCell(
            Advance: behavior.Advance,
            Base: m_numbers[slot],
            EpochEngineTick: ReadNumberRaw(column: ArenaColumn.ClockEpochEngineTick, index: slot),
            Row: DocumentRow(rowOrdinal: rowOrdinal)
        );

        return true;
    }
    // Settles, at `time`, every row nested on `parentOrdinal`, deepest first, reading each one's parent through `cells`
    // as it stood before the write about to move it: each nested slot's value becomes its base and its engine epoch
    // moves to `time`, so its accumulation is continuous across the parent's move.
    private void SettleNested(int parentOrdinal, in ArenaTime time, IStateClockCells cells, int depth = 0) {
        if (depth >= StateAdvance.MaxNestingDepth) { return; }

        foreach (var child in m_catalog.NestedClocks(rowOrdinal: parentOrdinal)) {
            SettleNested(cells: cells, depth: (depth + 1), parentOrdinal: child, time: in time);

            if (
                !TryClockCellAt(cell: out var cell, rowOrdinal: child) ||
                (cell.Advance is not { } advance) ||
                !m_keys.TryResolve(key: out var key, name: StateRow.SlotKey) ||
                !TryPresentSlot(key: key, rowOrdinal: child, slot: out var slot)
            ) {
                continue;
            }

            var settled = advance.ComputeCurrentValue(baseValue: cell.Base, cells: cells, currentEngineTick: time.EngineTick, epochEngineTick: cell.EpochEngineTick, row: cell.Row);

            if (settled != cell.Base) {
                StoreNumber(next: settled, previous: cell.Base, rowOrdinal: child, slot: slot);
            }

            WriteClockAt(epochEngineTick: unchecked((long)time.EngineTick), epochTick: unchecked((long)time.Tick), slot: slot, substepTicks: 0L, v0: 0L, y0: 0L);
        }
    }

    // A cell source that answers one row as it stood before its redeclaration and every other row from the arena: what
    // the rows nested on a redeclared row, and the redeclared row's own old value, are settled against.
    private sealed class RedeclaredClockCells(StateArena arena, StateRow previous) : IStateClockCells {
        public bool TryClockCell(string row, out StateClockCell cell) => (string.Equals(a: row, b: previous.Name.Value, comparisonType: StringComparison.Ordinal)
            ? StateClockCell.TryFromRow(cell: out cell, row: previous)
            : arena.TryClockCell(cell: out cell, row: row));
    }
}
