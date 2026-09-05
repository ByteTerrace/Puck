namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static bool TryWriteSet(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.WriteSet writeSet, out string reason) {
        if (!TryFind(rows, writeSet.Row, out var index, out reason) || !TryFind(rows, writeSet.Set, out var setIndex, out reason)) {
            return false;
        }
        var row = rows[index];
        var setRow = rows[setIndex];
        if (row.EffectiveDomain is not WorldStateDomain.CellsOf board || WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology || topology.CellCount > WorldBoardMask.MaxCells) {
            return Refuse($"writeSet requires a board row over a topology of at most {WorldBoardMask.MaxCells} cells", out reason);
        }
        if (setRow.Kind != CellKind.Int || WorldDefinitionRows.FindCell(setRow.Cells, WorldCellName.Parse(writeSet.SetKey ?? WorldStateRow.SlotKey)) is not { } setCell) {
            return Refuse("writeSet reads its cell set from an integer cell", out reason);
        }
        if (row.ClampToEnvelope(writeSet.Value) != writeSet.Value || (row.Kind == CellKind.Bool && writeSet.Value is not (0 or 1))) {
            return Refuse("writeSet writes a value the board row does not admit", out reason);
        }
        var cells = (row.Cells ?? []).ToList();
        var position = new Dictionary<WorldCellName, int>(cells.Count);
        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++) {
            position[cells[cellIndex].Key] = cellIndex;
        }
        var bits = (ulong)setCell.Value;
        while (bits != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1UL;
            if (cell >= topology.CellCount) {
                continue;
            }
            var key = topology.CellName(cell);
            if (position.TryGetValue(key, out var existing)) {
                cells[existing] = cells[existing] with { Value = writeSet.Value };
            } else {
                position[key] = cells.Count;
                cells.Add(new(key, writeSet.Value));
            }
        }
        rows[index] = row with { Cells = cells };
        return true;
    }

    // A ring's cells are its slots 0..n-1 in slot order (the validator's invariant), so the slot at the cursor is
    // cells[slot] when it exists and the next append otherwise; nothing is parsed or searched.
    private static bool TryPush(WorldStateRow[] rows, WorldStateTransform.Push push, out string reason) {
        if (!TryFind(rows, push.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.EffectiveDomain is not WorldStateDomain.Ring history) {
            return Refuse("push requires a history row", out reason);
        }
        if (row.ClampToEnvelope(push.Value) != push.Value) {
            return Refuse("push writes a value the history row does not admit", out reason);
        }
        var slot = (int)(row.HistoryCursor % history.Capacity);
        var cells = (row.Cells ?? []).ToArray();
        WorldStateCell[] written;
        if (slot < cells.Length) {
            written = cells;
            written[slot] = written[slot] with { Value = push.Value };
        } else if (slot == cells.Length) {
            written = new WorldStateCell[cells.Length + 1];
            cells.CopyTo(written, 0);
            written[slot] = new(WorldCellName.Parse(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), push.Value);
        } else {
            return Refuse("push found a ring whose slots are not the dense prefix 0..n-1", out reason);
        }
        rows[index] = row with { Cells = written, HistoryCursor = checked(row.HistoryCursor + 1L) };
        return true;
    }

}
