namespace Puck.State.Rules;

public static partial class ArenaTransforms {
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

        using var keysLease = context.Arena.Scratch.Rent<long>(length: count);

        var keys = keysLease.Span;

        for (var position = 0; (position < count); position++) {
            if (arena.TryReadRawAt(
                position: position,
                raw: out var raw,
                rowOrdinal: sort.RowOrdinal
            )) {
                keys[position] = raw;
            }
        }

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
