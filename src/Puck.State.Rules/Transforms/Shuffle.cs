namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Fisher-Yates from the top: position i takes a uniform pick from [0, i], the same multiply-high map a random
    // transfer selects with, one sample per position. n members consume n - 1 samples, so a replay that starts the
    // site at the same cursor reproduces the permutation.
    private static bool TryShuffle(in ArenaTransformContext context, ArenaTransform.Shuffle shuffle, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryMemberRow(
            code: TransformRefusal.ShuffleRowShape,
            context: in context,
            layout: out _,
            refusal: out refusal,
            rowOrdinal: shuffle.RowOrdinal,
            verb: "shuffle"
        )) {
            return false;
        }

        var count = arena.CellCount(rowOrdinal: shuffle.RowOrdinal);

        if (count < 2) {
            return Applied(refusal: out refusal);
        }
        if (!TryDrawSite(
            code: TransformRefusal.ShuffleDrawSite,
            context: in context,
            draw: out var draw,
            generator: out var generator,
            refusal: out refusal,
            rowOrdinal: shuffle.DrawRowOrdinal,
            verb: "shuffle"
        )) {
            return false;
        }

        Span<int> order = stackalloc int[count];

        for (var position = 0; (position < count); position++) {
            order[position] = position;
        }

        var sample = 0L;

        for (var position = (count - 1); (position > 0); position--) {
            if (!TrySample(
                code: TransformRefusal.ShuffleDrawSite,
                context: in context,
                draw: in draw,
                generator: generator,
                refusal: out refusal,
                rowOrdinal: shuffle.DrawRowOrdinal,
                sample: out sample
            )) {
                return false;
            }

            var pick = Select(
                count: (position + 1),
                sample: sample
            );

            (order[position], order[pick]) = (order[pick], order[position]);
        }
        if (!TryReorder(
            code: TransformRefusal.ShuffleRowShape,
            context: in context,
            moved: out moved,
            order: order,
            refusal: out refusal,
            rowOrdinal: shuffle.RowOrdinal
        )) {
            return false;
        }

        // The site records the cursor and the last sample even when the permutation it produced is the identity.
        moved = true;

        return TryRecordSample(
            code: TransformRefusal.ShuffleDrawSite,
            context: in context,
            refusal: out refusal,
            rowOrdinal: shuffle.DrawRowOrdinal,
            sample: sample
        );
    }
}
