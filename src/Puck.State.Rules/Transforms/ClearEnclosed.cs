using Puck.Maths;

namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The write-path twin of the enclosedAt board query, applied after the placed value lands: every group of
    // cells beside the origin without a liberty in the boundary range is written back as empty.
    private static bool TryClearEnclosed(in ArenaTransformContext context, ArenaTransform.ClearEnclosed enclosed, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryBoardRow(
            code: TransformRefusal.ClearEnclosedBoard,
            context: in context,
            layout: out var layout,
            refusal: out refusal,
            rowOrdinal: enclosed.RowOrdinal,
            topology: out var topology,
            verb: "clearEnclosed"
        )) {
            return false;
        }
        if (layout.Kind != CellKind.Int) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedBoard,
                reason: "clearEnclosed clears an integer board row",
                refusal: out refusal
            );
        }

        if (!TryBoardOrigin(arena, topology, in binding, enclosed.Origin, out var origin)) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedOrigin,
                reason: $"clearEnclosed 'from' names no cell of the topology row '{RowName(
                    context: in context,
                    rowOrdinal: enclosed.RowOrdinal
                )}' lies over",
                refusal: out refusal
            );
        }
        if (
            (enclosed.Lower > enclosed.Upper) ||
            ((layout.Empty >= enclosed.Lower) && (layout.Empty <= enclosed.Upper)) ||
            (enclosed.BoundaryLower > enclosed.BoundaryUpper)
        ) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedRange,
                reason: "clearEnclosed takes ordered ranges and its group range excludes the board's empty value",
                refusal: out refusal
            );
        }

        using var valuesLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);

        var values = valuesLease.Span;

        if (!arena.TryReadBoard(
            rowOrdinal: enclosed.RowOrdinal,
            values: values
        )) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedBoard,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: enclosed.RowOrdinal
                )}' holds no board the arena can read",
                refusal: out refusal
            );
        }
        if (values[origin] == layout.Empty) {
            return Applied(refusal: out refusal);
        }

        // The cleared groups come back as a cell set, so they are written in ascending cell order without a sort.
        var words = BitWords.WordsFor(members: topology.CellCount);
        using var cleared = arena.Scratch.Rent<ulong>(words);

        _ = BoardQueries.FindEnclosed(topology, values, origin, enclosed.Lower, enclosed.Upper,
            enclosed.BoundaryLower, enclosed.BoundaryUpper, cleared.Span[..words], arena.Scratch);
        foreach (var cell in BitWords.Members(words: cleared.Span[..words])) {
            if (!arena.TryWriteBoardCell(
                cell: cell,
                reason: out var reason,
                rowOrdinal: enclosed.RowOrdinal,
                value: layout.Empty,
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.ClearEnclosedBoard,
                    reason: reason,
                    refusal: out refusal
                );
            }

            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
