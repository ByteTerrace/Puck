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
    // The order an ordered zone's tokens take in their domain row, in zone order: the coordinates an arrangement
    // rank is read and written in. Returns -1 when a token is one the domain does not declare.
    private static int DomainOrdinals(StateArena arena, int rowOrdinal, int domainOrdinal, Span<int> ordinals) {
        var count = arena.CellCount(rowOrdinal: rowOrdinal);

        if (
            (domainOrdinal < 0) ||
            (count > ordinals.Length)
        ) {
            return -1;
        }

        var domainCount = arena.CellCount(rowOrdinal: domainOrdinal);

        for (var position = 0; (position < count); position++) {
            if (!arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            )) {
                return -1;
            }

            var found = -1;

            for (var candidate = 0; (candidate < domainCount); candidate++) {
                if (
                    arena.TryKeyAt(
                    key: out var domainKey,
                    position: candidate,
                    rowOrdinal: domainOrdinal
                ) &&
                    (domainKey == key)
                ) {
                    found = candidate;

                    break;
                }
            }
            if (found < 0) {
                return -1;
            }

            ordinals[position] = found;
        }

        return count;
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
