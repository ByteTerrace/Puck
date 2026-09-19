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
        // A literal origin was resolved to its topology cell once, at compile time; only a dynamic key, which spells
        // a fresh cell each firing, reads a name here.
        var origin = ray.Origin;

        if (
            (binding.BindsKey &&
            (!context.Arena.Catalog.Keys.TryGetName(
                key: binding.Key,
                name: out var name
            ) ||
                !topology.TryCell(
                cell: out origin,
                key: name.Value
            ))) ||
            (((uint)origin) >= ((uint)topology.CellCount)) ||
            (((uint)ray.Direction) >= ((uint)topology.DirectionCount))
        ) {
            return Refuse(
                code: TransformRefusal.SetRayAddressing,
                reason: "setRay requires a board origin and a direction the topology steps in",
                refusal: out refusal
            );
        }

        using var valuesLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);

        var values = valuesLease.Span;
        using var wordLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);
        var word = wordLease.Span;
        using var affectedLease = context.Arena.Scratch.Rent<int>(length: topology.CellCount);
        var affected = affectedLease.Span;
        if (!context.Arena.TryReadBoard(
            rowOrdinal: ray.RowOrdinal,
            values: values
        )) {
            return Refuse(
                code: TransformRefusal.SetRayAddressing,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: ray.RowOrdinal
                )}' holds no board the arena can read",
                refusal: out refusal
            );
        }

        var length = 0;
        var cell = origin;

        for (var visited = 1; (visited < topology.CellCount); visited++) {
            cell = topology.Neighbour(
                cell: cell,
                direction: ray.Direction
            );
            if (
                (cell < 0) ||
                (cell == origin)
            ) {
                break;
            }

            word[length] = values[cell];
            affected[length] = cell;
            length++;
        }

        var prefix = ray.Pattern.LongestAcceptedPrefix(values: word[..length]);

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
