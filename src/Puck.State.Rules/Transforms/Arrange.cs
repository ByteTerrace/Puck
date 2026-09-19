namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The zone's tokens in their domain's own order are arrangement 0; the rank's Lehmer code picks the order.
    private static bool TryArrange(in ArenaTransformContext context, ArenaTransform.Arrange arrange, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryMemberRow(
            code: TransformRefusal.ArrangeShape,
            context: in context,
            layout: out var zone,
            refusal: out refusal,
            rowOrdinal: arrange.RowOrdinal,
            verb: "arrange"
        )) {
            return false;
        }
        if (!zone.IsOrdered) {
            return Refuse(
                code: TransformRefusal.ArrangeShape,
                reason: "arrange requires an ordered zone",
                refusal: out refusal
            );
        }
        if (
            !arena.TryRead(
            key: binding.KeyOr(own: arrange.FromKey),
            rowOrdinal: arrange.FromRowOrdinal,
            value: out var cell
        ) ||
            (cell.Kind != CellKind.Int)
        ) {
            return Refuse(
                code: TransformRefusal.ArrangeRankSource,
                reason: "arrange reads its rank from an integer cell",
                refusal: out refusal
            );
        }

        Span<int> ordinals = stackalloc int[RuleReads.MaxArrangementTokens];
        var count = DomainOrdinals(
            arena: arena,
            domainOrdinal: arrange.DomainRowOrdinal,
            ordinals: ordinals,
            rowOrdinal: arrange.RowOrdinal
        );

        if (count < 0) {
            return Refuse(
                code: TransformRefusal.ArrangeShape,
                reason: $"arrange requires an ordered zone of at most {RuleReads.MaxArrangementTokens} tokens, every one declared by its domain",
                refusal: out refusal
            );
        }

        var rank = cell.AsInt;

        if (
            (rank < 0L) ||
            (((ulong)rank) >= Puck.Maths.Combinatorics.Factorial(n: count))
        ) {
            return Refuse(
                code: TransformRefusal.ArrangeRankRange,
                reason: $"arrange rank {rank} is outside 0..{count}!-1",
                refusal: out refusal
            );
        }
        if (count < 2) {
            return Applied(refusal: out refusal);
        }

        Span<int> relative = stackalloc int[RuleReads.MaxArrangementTokens];

        StateReader.RelativeOrder(
            ordinals: ordinals[..count],
            relative: relative[..count]
        );

        // sorted[r] is the position whose relative rank is r; the unranked permutation says which of those each
        // arranged position takes.
        Span<int> sorted = stackalloc int[count];

        for (var position = 0; (position < count); position++) {
            sorted[relative[position]] = position;
        }

        Span<int> permutation = stackalloc int[RuleReads.MaxArrangementTokens];

        Puck.Maths.Combinatorics.PermutationUnrank(
            destination: permutation[..count],
            rank: ((ulong)rank)
        );

        Span<int> order = stackalloc int[count];

        for (var position = 0; (position < count); position++) {
            order[position] = sorted[permutation[position]];
        }

        return TryReorder(
            code: TransformRefusal.ArrangeShape,
            context: in context,
            moved: out moved,
            order: order,
            refusal: out refusal,
            rowOrdinal: arrange.RowOrdinal
        );
    }
}
