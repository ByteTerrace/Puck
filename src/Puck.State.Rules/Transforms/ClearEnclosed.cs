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

        // The groups and their liberties are read from the board's planes as cell sets, and the cleared groups come
        // back as a cell set, which lands through the arena's set door in one call.
        var words = topology.CellWords;
        using var membersLease = arena.Scratch.Rent<ulong>(words);
        using var boundaryLease = arena.Scratch.Rent<ulong>(words);
        using var cleared = arena.Scratch.Rent<ulong>(words);

        if (
            !arena.TryReadBoardCell(cell: origin, rowOrdinal: enclosed.RowOrdinal, value: out var placed) ||
            !arena.TryReadBoardRange(lower: enclosed.Lower, mask: membersLease.Span, rowOrdinal: enclosed.RowOrdinal, upper: enclosed.Upper) ||
            !arena.TryReadBoardRange(lower: enclosed.BoundaryLower, mask: boundaryLease.Span, rowOrdinal: enclosed.RowOrdinal, upper: enclosed.BoundaryUpper)
        ) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedBoard,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: enclosed.RowOrdinal
                )}' holds no board the arena can read",
                refusal: out refusal
            );
        }
        if (placed == layout.Empty) {
            return Applied(refusal: out refusal);
        }

        _ = BoardQueries.FindEnclosed(topology, membersLease.Span[..words], boundaryLease.Span[..words], origin, cleared.Span[..words], arena.Scratch);
        if (!arena.TryWriteBoardCells(
            cells: cleared.Span[..words],
            reason: out var reason,
            rowOrdinal: enclosed.RowOrdinal,
            value: layout.Empty
        )) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedBoard,
                reason: reason,
                refusal: out refusal
            );
        }

        moved = (BitWords.Count(words: cleared.Span[..words]) != 0);

        return Applied(refusal: out refusal);
    }
}
