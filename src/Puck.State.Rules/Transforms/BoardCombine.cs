namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Membership algebra, not arithmetic: a cell is a member when its value is not its own board's empty value,
    // every member of the result is written as the declared value, and every other cell as the written board's
    // empty value.
    private static bool TryBoardCombine(in ArenaTransformContext context, ArenaTransform.BoardCombine combine, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (!TryBoardRow(
            code: TransformRefusal.BoardCombineBoard,
            context: in context,
            layout: out var target,
            refusal: out refusal,
            rowOrdinal: combine.RowOrdinal,
            topology: out var topology,
            verb: "boardCombine"
        )) {
            return false;
        }

        Span<long> left = stackalloc long[topology.CellCount];
        Span<long> right = stackalloc long[topology.CellCount];
        var leftEmpty = 0L;
        var rightEmpty = 0L;

        if (
            BoardCombination.NeedsLeft(operation: combine.Operation) &&
            !TrySourceBoard(
            context: in context,
            empty: out leftEmpty,
            refusal: out refusal,
            rowOrdinal: combine.LeftRowOrdinal,
            topology: topology,
            values: left
        )
        ) {
            return false;
        }
        if (
            BoardCombination.NeedsRight(operation: combine.Operation) &&
            !TrySourceBoard(
            context: in context,
            empty: out rightEmpty,
            refusal: out refusal,
            rowOrdinal: combine.RightRowOrdinal,
            topology: topology,
            values: right
        )
        ) {
            return false;
        }

        Span<long> result = stackalloc long[topology.CellCount];

        BoardCombination.Write(
            direction: combine.Direction,
            element: combine.Element,
            empty: target.Empty,
            left: left,
            leftEmpty: leftEmpty,
            operation: combine.Operation,
            right: right,
            rightEmpty: rightEmpty,
            target: result,
            topology: topology,
            value: combine.Value
        );

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (!context.Arena.TryWriteBoardCell(
                cell: cell,
                reason: out var reason,
                rowOrdinal: combine.RowOrdinal,
                value: result[cell],
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.BoardCombineBoard,
                    reason: reason,
                    refusal: out refusal
                );
            }
        }

        moved = true;

        return Applied(refusal: out refusal);
    }
    private static bool TrySourceBoard(in ArenaTransformContext context, int rowOrdinal, CompiledTopology topology, Span<long> values, out long empty, out EffectRefusal refusal) {
        empty = 0L;

        if (!TryBoardRow(
            code: TransformRefusal.BoardCombineOperands,
            context: in context,
            layout: out var layout,
            refusal: out refusal,
            rowOrdinal: rowOrdinal,
            topology: out var source,
            verb: "boardCombine source"
        )) {
            return false;
        }
        if (!ReferenceEquals(
            objA: source,
            objB: topology
        )) {
            return Refuse(
                code: TransformRefusal.BoardCombineOperands,
                reason: $"boardCombine source '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' does not lie over the written board's topology",
                refusal: out refusal
            );
        }

        empty = layout.Empty;

        if (!context.Arena.TryReadBoard(
            rowOrdinal: rowOrdinal,
            values: values
        )) {
            return Refuse(
                code: TransformRefusal.BoardCombineOperands,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' holds no board the arena can read",
                refusal: out refusal
            );
        }

        return true;
    }
}
