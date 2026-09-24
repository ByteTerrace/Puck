namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // A mask is one expression value, so the board it lands on is at most 64 cells; a bit past the topology's
    // own cell count names no cell and is skipped rather than refused, the way a mask read back from a wider
    // integer always was. A declared set lowers at the board's own width, whatever its size.
    private static bool TryWriteSet(in ArenaTransformContext context, ArenaTransform.WriteSet writeSet, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (!TryBoardRow(
            code: TransformRefusal.WriteSetBoard,
            context: in context,
            layout: out _,
            refusal: out refusal,
            rowOrdinal: writeSet.RowOrdinal,
            topology: out var topology,
            verb: "writeSet"
        )) {
            return false;
        }
        if (writeSet.Set is { } declared) {
            if (!TryLowerDeclaredSet(
                cells: out var cells,
                code: TransformRefusal.WriteSetSource,
                context: in context,
                refusal: out refusal,
                set: declared,
                topology: topology,
                verb: "writeSet"
            )) {
                return false;
            }

            for (var position = 0; (position < topology.CellCount); position++) {
                if (!cells.Contains(index: position)) {
                    continue;
                }
                if (!context.Arena.TryWriteBoardCell(
                    cell: position,
                    reason: out var reason,
                    rowOrdinal: writeSet.RowOrdinal,
                    value: writeSet.Value,
                    write: StateWriteKind.Set
                )) {
                    moved = false;

                    return Refuse(
                        code: TransformRefusal.WriteSetValueInadmissible,
                        reason: reason,
                        refusal: out refusal
                    );
                }

                moved = true;
            }

            return Applied(refusal: out refusal);
        }
        if (topology.CellCount > BoardMask.MaxCells) {
            return Refuse(
                code: TransformRefusal.WriteSetBoard,
                reason: $"writeSet requires a board row over a topology of at most {BoardMask.MaxCells} cells",
                refusal: out refusal
            );
        }
        if (
            !context.Arena.TryReadLive(
            key: binding.KeyOr(own: writeSet.SetKey),
            rowOrdinal: writeSet.SetRowOrdinal,
            time: context.Time,
            value: out var cell
        ) ||
            (cell.Kind != CellKind.Int)
        ) {
            return Refuse(
                code: TransformRefusal.WriteSetSource,
                reason: "writeSet reads its cell set from an integer cell",
                refusal: out refusal
            );
        }

        var bits = ((ulong)cell.AsInt);

        while (bits != 0UL) {
            var member = System.Numerics.BitOperations.TrailingZeroCount(value: bits);

            bits &= (bits - 1UL);
            if (member >= topology.CellCount) {
                continue;
            }
            if (!context.Arena.TryWriteBoardCell(
                cell: member,
                reason: out var reason,
                rowOrdinal: writeSet.RowOrdinal,
                value: writeSet.Value,
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.WriteSetValueInadmissible,
                    reason: reason,
                    refusal: out refusal
                );
            }

            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
