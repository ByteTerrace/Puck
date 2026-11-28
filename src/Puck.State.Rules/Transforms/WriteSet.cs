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

            using var membersLease = context.Arena.Scratch.Rent<ulong>(length: topology.CellWords);
            var members = membersLease.Span;

            cells.CopyTo(destination: members);

            return TryWriteMembers(context: in context, members: members, moved: out moved, refusal: out refusal, writeSet: writeSet);
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

        Span<ulong> mask = [(((ulong)cell.AsInt) & Puck.Maths.BinaryIntegerFunctions.LowMask<ulong>(count: topology.CellCount))];

        return TryWriteMembers(context: in context, members: mask, moved: out moved, refusal: out refusal, writeSet: writeSet);
    }
    // The set's cells land through the arena's set door, which admits the value once and writes only the cells that
    // change.
    private static bool TryWriteMembers(in ArenaTransformContext context, ArenaTransform.WriteSet writeSet, ReadOnlySpan<ulong> members, out bool moved, out EffectRefusal refusal) {
        moved = false;
        if (!context.Arena.TryWriteBoardCells(
            cells: members,
            reason: out var reason,
            rowOrdinal: writeSet.RowOrdinal,
            value: writeSet.Value
        )) {
            return Refuse(
                code: TransformRefusal.WriteSetValueInadmissible,
                reason: reason,
                refusal: out refusal
            );
        }

        moved = (Puck.Maths.BitWords.Count(words: members) != 0);

        return Applied(refusal: out refusal);
    }
}
