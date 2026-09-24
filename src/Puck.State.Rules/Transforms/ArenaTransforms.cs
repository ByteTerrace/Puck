namespace Puck.State.Rules;

/// <summary>The one implementation of every <see cref="StateTransform"/> case, over a <see cref="StateArena"/>.
/// The evaluator, a search judge, the console, and a host's own command path all apply a transform through
/// <see cref="TryApply"/>.</summary>
/// <remarks>A transform writes inside whatever journal scope its caller has open, so a refused transform is rewound
/// by the firing that submitted it. The twelve scalar kernels open no scope of their own; the four vector kernels
/// open one around their store, which nests inside the caller's and is undone by the caller's rewind either way. A
/// random selection reads and advances its site's draw cursor through that same scope, so the draw is consumed only
/// when the scope commits.</remarks>
public static partial class ArenaTransforms {
    /// <summary>Applies one resolved transform.</summary>
    /// <param name="context">The store, the clocks, and the draw context.</param>
    /// <param name="transform">The resolved transform.</param>
    /// <param name="binding">The live key and zone ends a firing resolved, or
    /// <see cref="ArenaTransformBinding.None"/>.</param>
    /// <param name="moved">Whether the transform changed the arena.</param>
    /// <param name="refusal">Why the transform did not apply, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the transform applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transform"/> is <see langword="null"/>.</exception>
    public static bool TryApply(in ArenaTransformContext context, ArenaTransform transform, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: transform);

        moved = false;

        return (transform switch {
            ArenaTransform.Arrange arrange => TryArrange(
            arrange: arrange,
            binding: in binding,
            context: in context,
            moved: out moved,
            refusal: out refusal
        ),
            ArenaTransform.BoardCombine combine => TryBoardCombine(
            combine: combine,
            context: in context,
            moved: out moved,
            refusal: out refusal
        ),
            ArenaTransform.ClearEnclosed enclosed => TryClearEnclosed(
            binding: in binding,
            context: in context,
            enclosed: enclosed,
            moved: out moved,
            refusal: out refusal
        ),
            ArenaTransform.Mean mean => TryMean(
            binding: in binding,
            context: in context,
            mean: mean,
            moved: out moved,
            refusal: out refusal
        ),
            ArenaTransform.Copy copy => TryCopy(
            binding: in binding,
            context: in context,
            copy: copy,
            moved: out moved,
            refusal: out refusal
        ),
            ArenaTransform.Mix mix => TryMix(
            binding: in binding,
            context: in context,
            mix: mix,
            moved: out moved,
            refusal: out refusal
        ),
            ArenaTransform.Nearest nearest => TryNearest(
            context: in context,
            moved: out moved,
            nearest: nearest,
            refusal: out refusal
        ),
            ArenaTransform.Observe observe => TryObserve(
            context: in context,
            moved: out moved,
            observe: observe,
            refusal: out refusal
        ),
            ArenaTransform.PushRay pushRay => TryPushRay(
            binding: in binding,
            context: in context,
            moved: out moved,
            push: pushRay,
            refusal: out refusal
        ),
            ArenaTransform.Remember remember => TryRemember(
            context: in context,
            moved: out moved,
            refusal: out refusal,
            remember: remember
        ),
            ArenaTransform.SetRay ray => TrySetRay(
            binding: in binding,
            context: in context,
            moved: out moved,
            ray: ray,
            refusal: out refusal
        ),
            ArenaTransform.Shuffle shuffle => TryShuffle(
            context: in context,
            moved: out moved,
            refusal: out refusal,
            shuffle: shuffle
        ),
            ArenaTransform.SortKeyed sortKeyed => TrySortKeyed(
            context: in context,
            moved: out moved,
            refusal: out refusal,
            sort: sortKeyed
        ),
            ArenaTransform.SortZone sortZone => TrySortZone(
            context: in context,
            moved: out moved,
            refusal: out refusal,
            sort: sortZone
        ),
            ArenaTransform.Transfer transfer => TryTransfer(
            binding: in binding,
            context: in context,
            moved: out moved,
            refusal: out refusal,
            transfer: transfer
        ),
            ArenaTransform.WriteSet writeSet => TryWriteSet(
            binding: in binding,
            context: in context,
            moved: out moved,
            refusal: out refusal,
            writeSet: writeSet
        ),
            _ => Refuse(
            code: TransformRefusal.RowUnaddressable,
            reason: $"'{transform.GetType().Name}' is not a transform this arena applies",
            refusal: out refusal
        ),
        });
    }

    private static bool Refuse(TransformRefusal code, string reason, out EffectRefusal refusal) {
        refusal = EffectRefusal.Of(
            code: code,
            reason: reason
        );

        return false;
    }
    private static bool Applied(out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        return true;
    }
    // A board transform reads its topology and empty value off the row's own layout, so nothing carries a
    // topology reference across the firing path.
    private static bool TryBoardRow(in ArenaTransformContext context, int rowOrdinal, TransformRefusal code, string verb, out ArenaRowLayout layout, out CompiledTopology topology, out EffectRefusal refusal) {
        topology = null!;

        if (
            (((uint)rowOrdinal) >= ((uint)context.Arena.Layout.RowCount)) ||
            ((layout = context.Arena.Layout[rowOrdinal]) is not { Shape: RowShape.Lattice, Topology: not null })
        ) {
            layout = default;

            return Refuse(
                code: code,
                reason: $"{verb} addresses row ordinal {rowOrdinal}, which is not a board the arena stores",
                refusal: out refusal
            );
        }

        topology = layout.Topology!;
        refusal = EffectRefusal.None;

        return true;
    }
    private static bool TryMemberRow(in ArenaTransformContext context, int rowOrdinal, TransformRefusal code, string verb, out ArenaRowLayout layout, out EffectRefusal refusal) {
        if (
            (((uint)rowOrdinal) >= ((uint)context.Arena.Layout.RowCount)) ||
            ((layout = context.Arena.Layout[rowOrdinal]).Shape is not (RowShape.Keyed or RowShape.Ordered))
        ) {
            layout = default;

            return Refuse(
                code: code,
                reason: $"{verb} addresses row ordinal {rowOrdinal}, which is not a keyed or ordered row",
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        return true;
    }
    private static string RowName(in ArenaTransformContext context, int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)context.Arena.Catalog.Count))
        ? context.Arena.Catalog.Descriptors[rowOrdinal].Name
        : $"ordinal {rowOrdinal}"
    );
}
