namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The vector transforms add no arithmetic of their own: scoring, normalization and ranking are the
    // Puck.State.Vectors kernels, and their catalogued refusal codes travel out unchanged rather than being
    // restated under a second name.
    private static bool TryCopy(in ArenaTransformContext context, ArenaTransform.Copy copy, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        var request = new VectorCopyRequest(
            From: copy.From,
            IntoKey: binding.KeyOr(own: copy.IntoKey),
            IntoRowOrdinal: copy.IntoRowOrdinal
        );
        var generation = GenerationOf(
            arena: context.Arena,
            rowOrdinal: copy.IntoRowOrdinal
        );

        return Landed(
            applied: ArenaVectorTransforms.TryCopy(
                arena: context.Arena,
                refusal: out var vector,
                request: in request
            ),
            arena: context.Arena,
            generation: generation,
            moved: out moved,
            refusal: out refusal,
            rowOrdinal: copy.IntoRowOrdinal,
            vector: in vector
        );
    }
    private static bool TryMix(in ArenaTransformContext context, ArenaTransform.Mix mix, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        // Every term travels to the kernel, which refuses a list past its ceiling by name rather than mixing part of it.
        var count = (mix.Terms?.Count ?? 0);
        var terms = new VectorMixTerm[count];

        for (var term = 0; (term < count); term++) {
            terms[term] = new VectorMixTerm(
                Source: mix.Terms![term].Source,
                Weight: mix.Terms[term].Weight
            );
        }

        var generation = GenerationOf(
            arena: context.Arena,
            rowOrdinal: mix.IntoRowOrdinal
        );

        return Landed(
            applied: ArenaVectorTransforms.TryMix(
                arena: context.Arena,
                intoKey: binding.KeyOr(own: mix.IntoKey),
                intoRowOrdinal: mix.IntoRowOrdinal,
                refusal: out var vector,
                terms: terms
            ),
            arena: context.Arena,
            generation: generation,
            moved: out moved,
            refusal: out refusal,
            rowOrdinal: mix.IntoRowOrdinal,
            vector: in vector
        );
    }
    private static bool TryMean(in ArenaTransformContext context, ArenaTransform.Mean mean, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        var request = new VectorMeanRequest(
            FromRowOrdinal: mean.FromRowOrdinal,
            IntoKey: binding.KeyOr(own: mean.IntoKey),
            IntoRowOrdinal: mean.IntoRowOrdinal,
            WhereRowOrdinal: mean.WhereRowOrdinal
        );
        var generation = GenerationOf(
            arena: context.Arena,
            rowOrdinal: mean.IntoRowOrdinal
        );

        return Landed(
            applied: ArenaVectorTransforms.TryMean(
                arena: context.Arena,
                refusal: out var vector,
                request: in request
            ),
            arena: context.Arena,
            generation: generation,
            moved: out moved,
            refusal: out refusal,
            rowOrdinal: mean.IntoRowOrdinal,
            vector: in vector
        );
    }
    private static bool TryNearest(in ArenaTransformContext context, ArenaTransform.Nearest nearest, out bool moved, out EffectRefusal refusal) {
        var request = new VectorNearestRequest(
            Exclude: nearest.Exclude,
            Farthest: nearest.Farthest,
            FromRowOrdinal: nearest.FromRowOrdinal,
            IntoRowOrdinal: nearest.IntoRowOrdinal,
            K: nearest.K,
            Query: nearest.Query,
            Threshold: nearest.Threshold,
            WhereRowOrdinal: nearest.WhereRowOrdinal
        );
        var generation = GenerationOf(
            arena: context.Arena,
            rowOrdinal: nearest.IntoRowOrdinal
        );

        return Landed(
            applied: ArenaVectorTransforms.TryNearest(
                arena: context.Arena,
                refusal: out var vector,
                request: in request
            ),
            arena: context.Arena,
            generation: generation,
            moved: out moved,
            refusal: out refusal,
            rowOrdinal: nearest.IntoRowOrdinal,
            vector: in vector
        );
    }
    private static bool TryRemember(in ArenaTransformContext context, ArenaTransform.Remember remember, out bool moved, out EffectRefusal refusal) {
        var request = new VectorRememberRequest(
            From: remember.From,
            IntoRowOrdinal: remember.IntoRowOrdinal,
            Key: remember.Key,
            UnlessWithinQ16: remember.UnlessWithinQ16
        );
        var generation = GenerationOf(
            arena: context.Arena,
            rowOrdinal: remember.IntoRowOrdinal
        );

        return Landed(
            applied: ArenaVectorTransforms.TryRemember(
                arena: context.Arena,
                refusal: out var vector,
                request: in request
            ),
            arena: context.Arena,
            generation: generation,
            moved: out moved,
            refusal: out refusal,
            rowOrdinal: remember.IntoRowOrdinal,
            vector: in vector
        );
    }
    private static ulong GenerationOf(StateArena arena, int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)arena.Layout.RowCount))
        ? arena.RowGeneration(rowOrdinal: rowOrdinal)
        : 0UL
    );
    // A remember that finds a near duplicate already stored returns success with nothing written, so the
    // destination row's generation — which moves on every mutation of the row, including one that writes back what
    // was there — is what tells a firing that landed from one that was skipped.
    private static bool Landed(bool applied, in VectorTransformRefusal vector, StateArena arena, int rowOrdinal, ulong generation, out bool moved, out EffectRefusal refusal) {
        moved = (applied && (GenerationOf(
            arena: arena,
            rowOrdinal: rowOrdinal
        ) != generation));

        if (applied) {
            refusal = EffectRefusal.None;

            return true;
        }

        refusal = new EffectRefusal(
            Code: vector.Code,
            Reason: vector.Reason
        );

        return false;
    }
}
