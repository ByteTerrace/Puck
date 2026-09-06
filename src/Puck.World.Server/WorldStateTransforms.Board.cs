namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static bool TryWriteSet(WorldDefinition definition, WorldStateRow[] rows, StateTransform.WriteSet writeSet, out string reason) {
        if (!TryFind(rows, writeSet.Row, out var index, out reason) || !TryFind(rows, writeSet.Set, out var setIndex, out reason)) {
            return false;
        }
        var row = rows[index];
        var setRow = rows[setIndex];
        if (row.EffectiveDomain is not StateDomain.CellsOf board || WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology || topology.CellCount > BoardMask.MaxCells) {
            return Refuse($"writeSet requires a board row over a topology of at most {BoardMask.MaxCells} cells", out reason);
        }
        if (setRow.Kind != CellKind.Int || !CellName.TryParse(writeSet.SetKey ?? WorldStateRow.SlotKey, out var setKey, out _) || StateRows.FindCell(setRow.Cells, setKey) is not { } setCell) {
            return Refuse("writeSet reads its cell set from an integer cell", out reason);
        }
        if (row.ClampToEnvelope(writeSet.Value) != writeSet.Value || (row.Kind == CellKind.Bool && writeSet.Value is not (0 or 1))) {
            return Refuse("writeSet writes a value the board row does not admit", out reason);
        }
        // The new row's cell list is the one allocation; the ordinal-indexed position map is pooled scratch.
        var existing = (row.Cells ?? []);
        var bits = (ulong)setCell.Value;
        var cells = new List<StateCell>(existing.Count + System.Numerics.BitOperations.PopCount(bits));
        cells.AddRange(existing);
        var positionPool = System.Buffers.ArrayPool<int>.Shared.Rent(topology.CellCount);
        try {
            var position = positionPool.AsSpan(0, topology.CellCount);
            position.Fill(-1);
            for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++) {
                if (topology.TryCell(cells[cellIndex].Key.Value, out var ordinal)) {
                    position[ordinal] = cellIndex;
                }
            }
            while (bits != 0UL) {
                var cell = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1UL;
                if (cell >= topology.CellCount) {
                    continue;
                }
                if (position[cell] >= 0) {
                    cells[position[cell]] = cells[position[cell]] with { Value = writeSet.Value };
                } else {
                    position[cell] = cells.Count;
                    cells.Add(new(topology.NameOf(cell), writeSet.Value));
                }
            }
        } finally {
            System.Buffers.ArrayPool<int>.Shared.Return(positionPool);
        }
        rows[index] = row with { Cells = cells };
        return true;
    }

    // Membership is "not the board's empty value"; the result writes members as the transform's value and drops
    // every other cell, which reads as empty. Every board shares the target's topology, so one values span per source
    // and one member span for the result bound the work at three walks of the board.
    private static bool TryBoardCombine(WorldDefinition definition, WorldStateRow[] rows, StateTransform.BoardCombine combine, out string reason) {
        if (!TryFind(rows, combine.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.EffectiveDomain is not StateDomain.CellsOf board || WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology) {
            return Refuse("boardCombine writes a board row", out reason);
        }
        var operation = combine.Operation;
        var needsLeft = operation is not (BoardCombineOp.Fill or BoardCombineOp.Clear);
        var needsRight = operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot;
        if (!Enum.IsDefined(operation) || needsLeft != (combine.Left is not null) || needsRight != (combine.Right is not null) ||
            (operation == BoardCombineOp.Shift) != (combine.Direction is not null) || (operation == BoardCombineOp.Image) != (combine.Element is not null)) {
            return Refuse("boardCombine takes left for every operation but fill and clear, right for and/or/xor/andNot, direction for shift alone, and element for image alone", out reason);
        }
        if (row.ClampToEnvelope(combine.Value) != combine.Value || (row.Kind == CellKind.Bool && combine.Value is not (0 or 1)) || combine.Value == board.Empty) {
            return Refuse("boardCombine writes a member value the board admits and that is not the board's own empty value", out reason);
        }
        var direction = (combine.Direction is { } directionName) ? topology.Direction(directionName) : -1;
        var element = (combine.Element is { } elementName) ? topology.Element(elementName) : -1;
        if ((combine.Direction is not null && direction < 0) || (combine.Element is not null && element < 0)) {
            return Refuse("boardCombine names a direction or point-group element its topology does not declare", out reason);
        }
        Span<long> left = stackalloc long[topology.CellCount];
        Span<long> right = stackalloc long[topology.CellCount];
        var leftEmpty = 0L;
        var rightEmpty = 0L;
        if (needsLeft && !TryReadBoard(definition, rows, combine.Left!, board.Topology, topology, left, out leftEmpty, out reason)) {
            return false;
        }
        if (needsRight && !TryReadBoard(definition, rows, combine.Right!, board.Topology, topology, right, out rightEmpty, out reason)) {
            return false;
        }
        Span<bool> member = stackalloc bool[topology.CellCount];
        for (var cell = 0; cell < topology.CellCount; cell++) {
            var inLeft = needsLeft && (left[cell] != leftEmpty);
            var inRight = needsRight && (right[cell] != rightEmpty);
            switch (operation) {
                case BoardCombineOp.Fill: member[cell] = true; break;
                case BoardCombineOp.And: member[cell] = (inLeft && inRight); break;
                case BoardCombineOp.Or: member[cell] = (inLeft || inRight); break;
                case BoardCombineOp.Xor: member[cell] = (inLeft ^ inRight); break;
                case BoardCombineOp.AndNot: member[cell] = (inLeft && !inRight); break;
                case BoardCombineOp.Not: member[cell] = !inLeft; break;
                case BoardCombineOp.Shift:
                    if (inLeft && topology.Neighbour(cell, direction) is >= 0 and var neighbour) { member[neighbour] = true; }
                    break;
                case BoardCombineOp.Image:
                    if (inLeft) { member[topology.Image(element, cell)] = true; }
                    break;
                default: break;
            }
        }
        var written = 0;
        for (var cell = 0; cell < topology.CellCount; cell++) {
            written += ((operation == BoardCombineOp.Copy) ? ((left[cell] != board.Empty) ? 1 : 0) : (member[cell] ? 1 : 0));
        }
        var cells = new List<StateCell>(written);
        for (var cell = 0; cell < topology.CellCount; cell++) {
            if (operation == BoardCombineOp.Copy) {
                if (left[cell] != board.Empty) { cells.Add(new(topology.NameOf(cell), left[cell])); }
            } else if (member[cell]) {
                cells.Add(new(topology.NameOf(cell), combine.Value));
            }
        }
        rows[index] = row with { Cells = cells };
        reason = string.Empty;
        return true;
    }
    // The enclosed members leave the sparse row rather than staying as explicit empty cells, the shape a copy produces.
    private static bool TryClearEnclosed(WorldDefinition definition, WorldStateRow[] rows, StateTransform.ClearEnclosed enclosed, out string reason) {
        if (!TryFind(rows, enclosed.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.EffectiveDomain is not StateDomain.CellsOf board || WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology || row.Kind != CellKind.Int) {
            return Refuse("clearEnclosed clears an integer board row", out reason);
        }
        if (!topology.TryCell(enclosed.From, out var source)) {
            return Refuse($"clearEnclosed 'from' names no cell of '{board.Topology}'", out reason);
        }
        if (enclosed.Lower > enclosed.Upper || (board.Empty >= enclosed.Lower && board.Empty <= enclosed.Upper)) {
            return Refuse("clearEnclosed takes a range that excludes the board's empty value", out reason);
        }
        Span<long> values = stackalloc long[topology.CellCount];
        BoardQueries.Read(row, topology, values);
        if (BoardQueries.ClearEnclosed(topology, values, source, enclosed.Lower, enclosed.Upper, board.Empty) == 0L) {
            reason = string.Empty;
            return true;
        }
        var cells = new List<StateCell>(row.Cells?.Count ?? 0);
        foreach (var cell in (row.Cells ?? [])) {
            if (!topology.TryCell(cell.Key.Value, out var ordinal) || values[ordinal] != board.Empty || cell.Value == board.Empty) {
                cells.Add(cell);
            }
        }
        rows[index] = row with { Cells = cells };
        reason = string.Empty;
        return true;
    }
    private static bool TryReadBoard(WorldDefinition definition, WorldStateRow[] rows, string name, string topologyName, CompiledTopology topology, Span<long> values, out long empty, out string reason) {
        empty = 0L;
        if (!TryFind(rows, name, out var index, out reason)) {
            return false;
        }
        var source = rows[index];
        if (source.EffectiveDomain is not StateDomain.CellsOf sourceBoard || sourceBoard.Topology != topologyName) {
            return Refuse($"boardCombine source '{name}' must be a board over '{topologyName}'", out reason);
        }
        empty = sourceBoard.Empty;
        BoardQueries.Read(source, topology, values);
        return true;
    }

    // A ring's cells are its slots 0..n-1 in slot order (the validator's invariant), so the slot at the cursor is
    // cells[slot] when it exists and the next append otherwise; nothing is parsed or searched.
    private static bool TryPush(WorldStateRow[] rows, StateTransform.Push push, out string reason) {
        if (!TryFind(rows, push.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.EffectiveDomain is not StateDomain.Ring history) {
            return Refuse("push requires a history row", out reason);
        }
        if (row.ClampToEnvelope(push.Value) != push.Value) {
            return Refuse("push writes a value the history row does not admit", out reason);
        }
        var slot = (int)(row.HistoryCursor % history.Capacity);
        var cells = (row.Cells ?? []).ToArray();
        StateCell[] written;
        if (slot < cells.Length) {
            written = cells;
            written[slot] = written[slot] with { Value = push.Value };
        } else if (slot == cells.Length) {
            written = new StateCell[cells.Length + 1];
            cells.CopyTo(written, 0);
            written[slot] = new(CellName.Parse(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), push.Value);
        } else {
            return Refuse("push found a ring whose slots are not the dense prefix 0..n-1", out reason);
        }
        rows[index] = row with { Cells = written, HistoryCursor = checked(row.HistoryCursor + 1L) };
        return true;
    }

}
