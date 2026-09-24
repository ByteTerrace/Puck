namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Each attribute column of the key table is the zone's word read through that attribute row at the firing's
    // time, so a token sorts by the live value every other read of its cell answers, and a token the attribute row
    // does not carry sorts as zero.
    private static bool TrySortZone(in ArenaTransformContext context, ArenaTransform.SortZone sort, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryMemberRow(
            code: TransformRefusal.SortZoneShape,
            context: in context,
            layout: out var zone,
            refusal: out refusal,
            rowOrdinal: sort.RowOrdinal,
            verb: "sortZone"
        )) {
            return false;
        }
        if (
            !zone.IsOrdered ||
            (zone.DomainOrdinal < 0) ||
            (sort.By is not { Count: >= 1 })
        ) {
            return Refuse(
                code: TransformRefusal.SortZoneShape,
                reason: $"sortZone requires an ordered zone and one or more attribute keys, each carrying its own direction",
                refusal: out refusal
            );
        }

        var count = arena.CellCount(rowOrdinal: sort.RowOrdinal);

        if (count < 2) {
            return Applied(refusal: out refusal);
        }

        using var keysLease = context.Arena.Scratch.Rent<long>(length: (sort.By.Count * count));
        using var descendingLease = context.Arena.Scratch.Rent<bool>(length: sort.By.Count);

        var keys = keysLease.Span;
        var descending = descendingLease.Span;

        for (var key = 0; (key < sort.By.Count); key++) {
            var attribute = sort.By[key];

            if (
                (((uint)attribute.RowOrdinal) >= ((uint)arena.Layout.RowCount)) ||
                (arena.Layout[attribute.RowOrdinal] is not { Shape: RowShape.Keyed or RowShape.Ordered, Kind: CellKind.Int or CellKind.Fixed } by) ||
                (by.DomainOrdinal != zone.DomainOrdinal)
            ) {
                return Refuse(
                    code: TransformRefusal.SortZoneAttribute,
                    reason: $"a sort attribute must be a numeric row keyed over the same token domain as zone '{RowName(
                        context: in context,
                        rowOrdinal: sort.RowOrdinal
                    )}'",
                    refusal: out refusal
                );
            }

            descending[key] = attribute.Descending;
            _ = arena.ReadWord(
                attributeOrdinal: attribute.RowOrdinal,
                rowOrdinal: sort.RowOrdinal,
                time: context.Time,
                word: keys.Slice(
                    length: count,
                    start: (key * count)
                )
            );
        }

        using var orderLease = context.Arena.Scratch.Rent<int>(length: count);

        var order = orderLease.Span;

        SortOrder(
            count: count,
            descending: descending,
            keys: keys,
            order: order
        );

        return TryReorder(
            code: TransformRefusal.SortZoneShape,
            context: in context,
            moved: out moved,
            order: order,
            refusal: out refusal,
            rowOrdinal: sort.RowOrdinal
        );
    }
}
