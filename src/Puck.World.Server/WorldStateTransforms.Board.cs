using System.Buffers;

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
        var position = new Dictionary<WorldCellName, int>(cells.Count);
        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++) {
            position[cells[cellIndex].Key] = cellIndex;
        }
        var bits = (ulong)maskCell.Value;
        while (bits != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1UL;
            if (cell >= topology.CellCount) {
                continue;
            }
            var key = topology.CellName(cell);
            if (position.TryGetValue(key, out var existing)) {
                cells[existing] = cells[existing] with { Value = setMask.Value };
            } else {
                position[key] = cells.Count;
                cells.Add(new(key, setMask.Value));
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
        if (row.History is not { } history) {
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

    // Membership is decided cell by cell over pooled scratch; the target is written SPARSELY when its empty value is
    // zero (only members, as 1), and densely as 1/0 only when a nonzero empty value would otherwise read absent
    // cells as members.
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
        var count = topology.CellCount;
        var scratch = ArrayPool<long>.Shared.Rent(2 * count);
        try {
            var leftValues = scratch.AsSpan(0, count);
            var rightValues = scratch.AsSpan(count, count);
            WorldBoardQueries.Read(left, topology, leftValues);
            if (right is not null) {
                WorldBoardQueries.Read(right, topology, rightValues);
            } else {
                rightValues.Clear();
            }
            var dense = board.Empty != 0L;
            var members = 0;
            for (var cell = 0; cell < count; cell++) {
                if (Member(combine.Operation, leftValues[cell] != 0L, rightValues[cell] != 0L)) {
                    members++;
                }
            }
            var cells = new WorldStateCell[dense ? count : members];
            var next = 0;
            for (var cell = 0; cell < count; cell++) {
                var member = Member(combine.Operation, leftValues[cell] != 0L, rightValues[cell] != 0L);
                if (dense) {
                    cells[next++] = new(topology.CellName(cell), member ? 1L : 0L);
                } else if (member) {
                    cells[next++] = new(topology.CellName(cell), 1L);
                }
            }
            rows[targetIndex] = target with { Cells = cells };
            return true;
        } finally {
            ArrayPool<long>.Shared.Return(scratch);
        }
    }

    private static bool Member(WorldBoardCombine operation, bool a, bool b) => operation switch {
        WorldBoardCombine.And => a && b,
        WorldBoardCombine.Or => a || b,
        WorldBoardCombine.Xor => a != b,
        WorldBoardCombine.AndNot => a && !b,
        _ => !a,
    };
}
