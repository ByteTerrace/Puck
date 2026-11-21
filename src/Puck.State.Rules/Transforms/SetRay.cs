namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The word is read from the origin outward, excluding the origin, stopping at the edge or on return to it; the
    // longest prefix the pattern accepts is what the value lands on. An empty accepted prefix refuses, so an author
    // closes a run with the symbol that ends it rather than with one that runs off the board.
    private static bool TrySetRay(in ArenaTransformContext context, ArenaTransform.SetRay ray, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (!TryBoardRow(
            code: TransformRefusal.SetRayAddressing,
            context: in context,
            layout: out _,
            refusal: out refusal,
            rowOrdinal: ray.RowOrdinal,
            topology: out var topology,
            verb: "setRay"
        )) {
            return false;
        }
        if (
            !TryBoardOrigin(context.Arena, topology, in binding, ray.Origin, out var origin) ||
            (((uint)ray.Direction) >= ((uint)topology.DirectionCount))
        ) {
            return Refuse(
                code: TransformRefusal.SetRayAddressing,
                reason: "setRay requires a board origin and a direction the topology steps in",
                refusal: out refusal
            );
        }

        var capacity = topology.LongestRay(ray.Direction);
        using var word = context.Arena.Scratch.Rent<long>(capacity);
        using var affectedLease = context.Arena.Scratch.Rent<int>(capacity);
        var affected = affectedLease.Span;
        if (!ArenaBoards.TryReadRay(context.Arena, ray.RowOrdinal, origin, ray.Direction, word.Span, out var values, out var readReason, affected)) {
            return Refuse(code: TransformRefusal.SetRayAddressing, reason: readReason, refusal: out refusal);
        }

        var prefix = ray.Pattern.LongestAcceptedPrefix(values);

        if (prefix <= 0L) {
            return Refuse(
                code: TransformRefusal.SetRayEmptyPrefix,
                reason: "setRay requires a nonempty accepted prefix",
                refusal: out refusal
            );
        }

        for (var written = 0; (written < prefix); written++) {
            if (!context.Arena.TryWriteBoardCell(
                cell: affected[written],
                reason: out var reason,
                rowOrdinal: ray.RowOrdinal,
                value: ray.Value,
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.SetRayValueInadmissible,
                    reason: reason,
                    refusal: out refusal
                );
            }

            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
