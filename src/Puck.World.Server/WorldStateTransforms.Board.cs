namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static bool TrySetMask(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.SetMask setMask, out string reason) {
        if (!TryFind(rows, setMask.Row, out var index, out reason) || !TryFind(rows, setMask.Mask, out var maskIndex, out reason)) {
            return false;
        }
        var row = rows[index];
        var maskRow = rows[maskIndex];
        if (row.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology || topology.CellCount > WorldBoardMask.MaxCells) {
            return Refuse($"setMask requires a board row over a topology of at most {WorldBoardMask.MaxCells} cells", out reason);
        }
        if (maskRow.Kind != CellKind.Int || WorldDefinitionRows.FindCell(maskRow.Cells, WorldCellName.Parse(setMask.MaskKey ?? WorldStateRow.SlotKey)) is not { } maskCell) {
            return Refuse("setMask reads its mask from an integer cell", out reason);
        }
        if (row.ClampToEnvelope(setMask.Value) != setMask.Value || (row.Kind == CellKind.Bool && setMask.Value is not (0 or 1))) {
            return Refuse("setMask writes a value the board row does not admit", out reason);
        }
        var cells = (row.Cells ?? []).ToList();
        var bits = (ulong)maskCell.Value;
        while (bits != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1UL;
            if (cell >= topology.CellCount) {
                continue;
            }
            var key = WorldCellName.Parse(topology.Key(cell));
            var existing = cells.FindIndex(c => c.Key == key);
            if (existing >= 0) {
                cells[existing] = cells[existing] with { Value = setMask.Value };
            } else {
                cells.Add(new(key, setMask.Value));
            }
        }
        rows[index] = row with { Cells = cells };
        return true;
    }

    // The ring slot written is cursor mod capacity, so the cursor alone says which slot is oldest and how many are
    // filled; a checked cursor overflow refuses rather than wrapping the ring's order.
    private static bool TryPush(WorldStateRow[] rows, WorldStateTransform.Push push, out string reason) {
        if (!TryFind(rows, push.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.History is not { } history) {
            return Refuse("push requires a history row", out reason);
        }
        if (row.ClampToEnvelope(push.Value) != push.Value) {
            return Refuse("push writes a value the history row does not admit", out reason);
        }
        var slot = WorldCellName.Parse((row.HistoryCursor % history.Capacity).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var cells = (row.Cells ?? []).ToList();
        var existing = cells.FindIndex(c => c.Key == slot);
        if (existing >= 0) {
            cells[existing] = cells[existing] with { Value = push.Value };
        } else {
            cells.Add(new(slot, push.Value));
        }
        rows[index] = row with { Cells = cells, HistoryCursor = checked(row.HistoryCursor + 1L) };
        return true;
    }

    // Every cell of the topology is written, so the target's occupancy is exactly the operation's result and no
    // stale membership survives from before the write.
    private static bool TryCombine(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.Combine combine, out string reason) {
        if (!TryFind(rows, combine.Target, out var targetIndex, out reason) || !TryFind(rows, combine.Left, out var leftIndex, out reason)) {
            return false;
        }
        var target = rows[targetIndex];
        var left = rows[leftIndex];
        if (target.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
            left.Board?.Topology != board.Topology || target.Kind is not (CellKind.Int or CellKind.Bool)) {
            return Refuse("combine requires an integer or boolean target board and a left board over the same topology", out reason);
        }
        WorldStateRow? right = null;
        if (combine.Operation == WorldBoardCombine.Not) {
            if (combine.Right is not null) {
                return Refuse("combine not takes no right board", out reason);
            }
        } else {
            if (combine.Right is null || !TryFind(rows, combine.Right, out var rightIndex, out reason)) {
                return Refuse("combine needs a right board for every operation but not", out reason);
            }
            right = rows[rightIndex];
            if (right.Board?.Topology != board.Topology) {
                return Refuse("combine requires the right board to share the target's topology", out reason);
            }
        }
        var leftValues = new long[topology.CellCount];
        var rightValues = new long[topology.CellCount];
        WorldBoardQueries.Read(left, topology, leftValues);
        if (right is not null) {
            WorldBoardQueries.Read(right, topology, rightValues);
        }
        var cells = new WorldStateCell[topology.CellCount];
        for (var cell = 0; cell < topology.CellCount; cell++) {
            var a = leftValues[cell] != 0L;
            var b = rightValues[cell] != 0L;
            var member = combine.Operation switch {
                WorldBoardCombine.And => a && b,
                WorldBoardCombine.Or => a || b,
                WorldBoardCombine.Xor => a != b,
                WorldBoardCombine.AndNot => a && !b,
                _ => !a,
            };
            cells[cell] = new(WorldCellName.Parse(topology.Key(cell)), member ? 1L : 0L);
        }
        rows[targetIndex] = target with { Cells = cells };
        return true;
    }
}
