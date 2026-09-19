namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The write-path twin of the enclosedAt board query, applied after the placed value lands: every group of
    // cells in the range beside the origin that has no empty cell beside it is written back as empty.
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

        // A literal origin was resolved to its topology cell once, at compile time; only a dynamic key, which spells
        // a fresh cell each firing, reads a name here.
        var origin = enclosed.Origin;

        if (
            (binding.BindsKey &&
            (!arena.Catalog.Keys.TryGetName(
                key: binding.Key,
                name: out var name
            ) ||
                !topology.TryCell(
                cell: out origin,
                key: name.Value
            ))) ||
            (((uint)origin) >= ((uint)topology.CellCount))
        ) {
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
            ((layout.Empty >= enclosed.Lower) && (layout.Empty <= enclosed.Upper))
        ) {
            return Refuse(
                code: TransformRefusal.ClearEnclosedRange,
                reason: "clearEnclosed takes a range that excludes the board's empty value",
                refusal: out refusal
            );
        }

        Span<long> values = stackalloc long[topology.CellCount];

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
        if (BoardQueries.ClearEnclosed(
            empty: layout.Empty,
            lower: enclosed.Lower,
            source: origin,
            topology: topology,
            upper: enclosed.Upper,
            values: values
        ) == 0L) {
            return Applied(refusal: out refusal);
        }

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (values[cell] != layout.Empty) {
                continue;
            }
            if (!arena.TryReadBoardCell(
                cell: cell,
                rowOrdinal: enclosed.RowOrdinal,
                value: out var stored
            )) {
                continue;
            }
            if (stored == layout.Empty) {
                continue;
            }
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
