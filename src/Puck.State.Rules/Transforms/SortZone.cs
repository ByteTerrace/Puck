namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The zone's cells are indexed by position once; each attribute row is then walked once to fill its column of
    // the key table, so a token the attribute row does not carry sorts as zero, exactly as an absent cell did.
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
            (sort.By is not { Count: >= 1 and <= StateCapacity.MaxSortKeys })
        ) {
            return Refuse(
                code: TransformRefusal.SortZoneShape,
                reason: $"sortZone requires an ordered zone and 1..{StateCapacity.MaxSortKeys} attribute keys, each carrying its own direction",
                refusal: out refusal
            );
        }

        var count = arena.CellCount(rowOrdinal: sort.RowOrdinal);

        if (count < 2) {
            return Applied(refusal: out refusal);
        }

        var keys = new long[(sort.By.Count * count)];
        var descending = new bool[sort.By.Count];

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
            for (var position = 0; (position < count); position++) {
                if (
                    arena.TryKeyAt(
                    key: out var token,
                    position: position,
                    rowOrdinal: sort.RowOrdinal
                ) &&
                    arena.TryReadRaw(
                    key: token,
                    raw: out var raw,
                    rowOrdinal: attribute.RowOrdinal
                )
                ) {
                    keys[((key * count) + position)] = raw;
                }
            }
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
