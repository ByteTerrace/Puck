namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // One door for every transform that only changes a row's order, so a permutation reaches the arena in one
    // journaled step rather than as a sequence of removals and re-inserts.
    private static bool TryReorder(in ArenaTransformContext context, int rowOrdinal, ReadOnlySpan<int> order, TransformRefusal code, out bool moved, out EffectRefusal refusal) {
        moved = false;
        for (var position = 0; (position < order.Length); position++) {
            if (order[position] != position) {
                moved = true;

                break;
            }
        }
        if (!moved) {
            return Applied(refusal: out refusal);
        }
        if (!context.Arena.TryReorder(
            order: order,
            reason: out var reason,
            rowOrdinal: rowOrdinal
        )) {
            moved = false;

            return Refuse(
                code: code,
                reason: reason,
                refusal: out refusal
            );
        }

        return Applied(refusal: out refusal);
    }
    // Keys are column-major: column k occupies [k * count, (k + 1) * count). The original position is the final
    // tiebreak, which is what makes the sort stable.
    private static void SortOrder(ReadOnlySpan<long> keys, ReadOnlySpan<bool> descending, int count, Span<int> order) {
        for (var position = 0; (position < count); position++) {
            order[position] = position;
        }

        // An insertion sort keeps the comparison allocation-free where a comparison delegate would not, and a
        // reordered row is bounded by its declared cell capacity.
        for (var position = 1; (position < count); position++) {
            var candidate = order[position];
            var slot = (position - 1);

            while (
                (slot >= 0) &&
                (Compare(
                count: count,
                descending: descending,
                keys: keys,
                left: order[slot],
                right: candidate
            ) > 0)
            ) {
                order[(slot + 1)] = order[slot];
                slot--;
            }

            order[(slot + 1)] = candidate;
        }
    }
    private static int Compare(ReadOnlySpan<long> keys, ReadOnlySpan<bool> descending, int count, int left, int right) {
        for (var key = 0; (key < descending.Length); key++) {
            var comparison = keys[((key * count) + left)].CompareTo(value: keys[((key * count) + right)]);

            if (comparison != 0) {
                return (descending[key]
                    ? -comparison
                    : comparison
                );
            }
        }

        return left.CompareTo(value: right);
    }
}
