namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Every source cell's live value at the firing's time is stored under the same key through the live door, so a
    // traited destination cell takes the copied value as any set would; a destination that refuses a value refuses the
    // whole firing, which the caller's scope rewinds.
    private static bool TryCopyRow(in ArenaTransformContext context, ArenaTransform.CopyRow copy, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var intoOrdinal = binding.ToOr(own: copy.RowOrdinal);
        var fromOrdinal = binding.FromOr(own: copy.FromRowOrdinal);

        if (!TryMemberRow(code: TransformRefusal.CopyRowShape, context: in context, layout: out var into, refusal: out refusal, rowOrdinal: intoOrdinal, verb: "copyRow") ||
            !TryMemberRow(code: TransformRefusal.CopyRowShape, context: in context, layout: out var from, refusal: out refusal, rowOrdinal: fromOrdinal, verb: "copyRow")) {
            return false;
        }
        if ((into.Kind != from.Kind) || (into.Kind is not (CellKind.Int or CellKind.Fixed or CellKind.Bool))) {
            return Refuse(
                code: TransformRefusal.CopyRowShape,
                reason: "copyRow requires two keyed numeric rows of one kind",
                refusal: out refusal
            );
        }

        var arena = context.Arena;
        var cursor = 0;

        while (arena.TryNextLiveNumber(
            cursor: ref cursor,
            key: out var key,
            rowOrdinal: fromOrdinal,
            time: context.Time,
            value: out var value
        )) {
            if (arena.TryReadLiveNumber(key: key, rowOrdinal: intoOrdinal, time: context.Time, value: out var current) && (current == value)) {
                continue;
            }
            if (!arena.TryWriteLive(
                key: key,
                operand: value,
                reason: out var reason,
                rowOrdinal: intoOrdinal,
                time: context.Time,
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.CopyRowShape,
                    reason: $"copyRow into '{RowName(context: in context, rowOrdinal: intoOrdinal)}': {reason}",
                    refusal: out refusal
                );
            }

            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
