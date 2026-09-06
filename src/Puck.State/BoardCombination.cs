namespace Puck.State;

/// <summary>The board-combine contract shared by dense frames and sparse document mutations. Storage adapters
/// resolve source rows and publish the result; this kernel owns operation admission and cell values.</summary>
public static class BoardCombination {
    /// <summary>Whether the operation reads a left board.</summary>
    /// <param name="operation">The operation.</param>
    public static bool NeedsLeft(BoardCombineOp operation) => operation is not (BoardCombineOp.Fill or BoardCombineOp.Clear);

    /// <summary>Whether the operation reads a right board.</summary>
    /// <param name="operation">The operation.</param>
    public static bool NeedsRight(BoardCombineOp operation) => operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot;

    /// <summary>Validates operation fields and the member value against an already resolved target board.</summary>
    /// <param name="combine">The authored operation.</param>
    /// <param name="row">The target row.</param>
    /// <param name="empty">The target's empty value.</param>
    /// <param name="topology">The target's topology.</param>
    /// <param name="direction">The resolved shift direction, or -1.</param>
    /// <param name="element">The resolved image element, or -1.</param>
    /// <param name="reason">The refusal, or empty.</param>
    /// <returns>Whether the operation is admissible; source existence and topology agreement remain the adapter's responsibility.</returns>
    public static bool TryValidate(StateTransform.BoardCombine combine, StateRow row, long empty, CompiledTopology topology, out int direction, out int element, out string reason) {
        ArgumentNullException.ThrowIfNull(combine);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(topology);
        direction = -1;
        element = -1;
        var operation = combine.Operation;
        if (!Enum.IsDefined(operation) || NeedsLeft(operation) != (combine.Left is not null) || NeedsRight(operation) != (combine.Right is not null) ||
            (operation == BoardCombineOp.Shift) != (combine.Direction is not null) || (operation == BoardCombineOp.Image) != (combine.Element is not null)) {
            reason = "boardCombine takes left for every operation but fill and clear, right for and/or/xor/andNot, direction for shift alone, and element for image alone";
            return false;
        }
        if (row.ClampToEnvelope(combine.Value) != combine.Value || (row.Kind == CellKind.Bool && combine.Value is not (0 or 1)) || combine.Value == empty) {
            reason = "boardCombine writes a member value the board admits and that is not the board's own empty value";
            return false;
        }
        direction = combine.Direction is { } directionName ? topology.Direction(directionName) : -1;
        element = combine.Element is { } elementName ? topology.Element(elementName) : -1;
        if ((combine.Direction is not null && direction < 0) || (combine.Element is not null && element < 0)) {
            reason = "boardCombine names a direction or point-group element its topology does not declare";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Writes a validated operation's dense result. Copy preserves every source value, including its
    /// empty value; other operations classify membership using each source's own empty value.</summary>
    /// <param name="combine">An operation admitted by <see cref="TryValidate"/>.</param>
    /// <param name="topology">The shared topology.</param>
    /// <param name="left">The left values, one per cell when required.</param>
    /// <param name="leftEmpty">The left board's empty value.</param>
    /// <param name="right">The right values, one per cell when required.</param>
    /// <param name="rightEmpty">The right board's empty value.</param>
    /// <param name="target">The result span, exactly one per cell, disjoint from both sources.</param>
    /// <param name="empty">The target's empty value.</param>
    /// <param name="direction">The validated direction.</param>
    /// <param name="element">The validated element.</param>
    public static void Write(StateTransform.BoardCombine combine, CompiledTopology topology, ReadOnlySpan<long> left, long leftEmpty, ReadOnlySpan<long> right, long rightEmpty, Span<long> target, long empty, int direction, int element) {
        ArgumentNullException.ThrowIfNull(combine);
        ArgumentNullException.ThrowIfNull(topology);
        var operation = combine.Operation;
        if (operation == BoardCombineOp.Copy) {
            left.CopyTo(target);
            return;
        }
        target.Fill(empty);
        for (var cell = 0; cell < topology.CellCount; cell++) {
            var inLeft = NeedsLeft(operation) && left[cell] != leftEmpty;
            var inRight = NeedsRight(operation) && right[cell] != rightEmpty;
            var destination = cell;
            var member = operation switch {
                BoardCombineOp.Fill => true,
                BoardCombineOp.And => inLeft && inRight,
                BoardCombineOp.Or => inLeft || inRight,
                BoardCombineOp.Xor => inLeft ^ inRight,
                BoardCombineOp.AndNot => inLeft && !inRight,
                BoardCombineOp.Not => !inLeft,
                BoardCombineOp.Shift => inLeft && (destination = topology.Neighbour(cell, direction)) >= 0,
                BoardCombineOp.Image => inLeft && (destination = topology.Image(element, cell)) >= 0,
                _ => false,
            };
            if (member) {
                target[destination] = combine.Value;
            }
        }
    }
}
