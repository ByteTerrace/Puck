namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The mask is one expression value, so the board it lands on is at most 64 cells; a bit past the topology's
    // own cell count names no cell and is skipped rather than refused, the way a mask read back from a wider
    // integer always was.
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
        if (topology.CellCount > BoardMask.MaxCells) {
            return Refuse(
                code: TransformRefusal.WriteSetBoard,
                reason: $"writeSet requires a board row over a topology of at most {BoardMask.MaxCells} cells",
                refusal: out refusal
            );
        }
        if (
            !context.Arena.TryRead(
            key: binding.KeyOr(own: writeSet.SetKey),
            rowOrdinal: writeSet.SetRowOrdinal,
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
