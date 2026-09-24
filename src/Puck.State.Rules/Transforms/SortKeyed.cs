namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The sort keys are the row's word at the firing's time, so a cell sorts by the live value every other read of
    // it answers.
    private static bool TrySortKeyed(in ArenaTransformContext context, ArenaTransform.SortKeyed sort, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryMemberRow(
            code: TransformRefusal.SortKeyedShape,
            context: in context,
            layout: out var row,
            refusal: out refusal,
            rowOrdinal: sort.RowOrdinal,
            verb: "sortKeyed"
        )) {
            return false;
        }
        if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            return Refuse(
                code: TransformRefusal.SortKeyedShape,
                reason: "sortKeyed requires a keyed or ordered numeric row",
                refusal: out refusal
            );
        }

        var count = arena.CellCount(rowOrdinal: sort.RowOrdinal);

        if (count < 2) {
            return Applied(refusal: out refusal);
        }

        using var keysLease = context.Arena.Scratch.Rent<long>(length: row.CellCapacity);

        var keys = arena.ReadWord(
            rowOrdinal: sort.RowOrdinal,
            time: context.Time,
            word: keysLease.Span
        );

        using var orderLease = context.Arena.Scratch.Rent<int>(length: count);

        var order = orderLease.Span;
        Span<bool> descending = [sort.Descending];

        SortOrder(
            count: count,
            descending: descending,
            keys: keys,
            order: order
        );

        return TryReorder(
            code: TransformRefusal.SortKeyedShape,
            context: in context,
            moved: out moved,
            order: order,
            refusal: out refusal,
            rowOrdinal: sort.RowOrdinal
        );
    }
}
