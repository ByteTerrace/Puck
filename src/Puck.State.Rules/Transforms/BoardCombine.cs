using Puck.Maths;

namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Membership algebra, not arithmetic: a cell is a member when its value is not its own board's empty value, every
    // member of the result is written as the declared value, and every other cell as the written board's empty value.
    // The sources are read from their value planes as member sets, a word per 64 cells, the algebra runs on those words
    // (BoardCombination.Members), and the result lands through the arena's set door (TryWriteBoardCells) once for the
    // members and once for the rest, each writing only the cells that change.
    private static bool TryBoardCombine(in ArenaTransformContext context, ArenaTransform.BoardCombine combine, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var rowOrdinal = binding.ToOr(own: combine.RowOrdinal);

        if (!TryBoardRow(
            code: TransformRefusal.BoardCombineBoard,
            context: in context,
            layout: out var target,
            refusal: out refusal,
            rowOrdinal: rowOrdinal,
            topology: out var topology,
            verb: "boardCombine"
        )) {
            return false;
        }

        var words = topology.CellWords;
        var leftRow = binding.FromOr(own: combine.LeftRowOrdinal);

        if ((combine.Operation == BoardCombineOp.Copy) && (combine.LeftSet is null)) {
            return TryBoardCopy(
                context: in context,
                moved: out moved,
                refusal: out refusal,
                rowOrdinal: rowOrdinal,
                sourceOrdinal: leftRow,
                topology: topology
            );
        }

        using var leftLease = context.Arena.Scratch.Rent<ulong>(length: words);
        using var rightLease = context.Arena.Scratch.Rent<ulong>(length: words);
        using var membersLease = context.Arena.Scratch.Rent<ulong>(length: words);
        var left = leftLease.Span;
        var right = rightLease.Span;
        var members = membersLease.Span;

        if (
            BoardCombination.NeedsLeft(operation: combine.Operation) &&
            !TryCombineMembers(
                context: in context,
                members: left,
                refusal: out refusal,
                rowOrdinal: leftRow,
                set: combine.LeftSet,
                topology: topology
            )
        ) {
            return false;
        }
        if (
            BoardCombination.NeedsRight(operation: combine.Operation) &&
            !TryCombineMembers(
                context: in context,
                members: right,
                refusal: out refusal,
                rowOrdinal: combine.RightRowOrdinal,
                set: combine.RightSet,
                topology: topology
            )
        ) {
            return false;
        }

        BoardCombination.Members(
            direction: combine.Direction,
            element: combine.Element,
            left: left,
            members: members,
            operation: combine.Operation,
            right: right,
            scratch: context.Arena.Scratch,
            topology: topology
        );
        if (!context.Arena.TryWriteBoardCells(
            cells: members,
            reason: out var reason,
            rowOrdinal: rowOrdinal,
            value: combine.Value
        )) {
            return Refuse(
                code: TransformRefusal.BoardCombineBoard,
                reason: reason,
                refusal: out refusal
            );
        }
        BitWords.Complement(members: topology.CellCount, words: members);
        if (!context.Arena.TryWriteBoardCells(
            cells: members,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: target.Empty
        )) {
            return Refuse(
                code: TransformRefusal.BoardCombineBoard,
                reason: reason,
                refusal: out refusal
            );
        }

        moved = true;

        return Applied(refusal: out refusal);
    }
    // Copy carries values: each value of the source's plane window lands on the cells that hold it through one set
    // write, and a cell outside every plane (a source that keeps no planes, or an outlier) is read and written alone.
    private static bool TryBoardCopy(in ArenaTransformContext context, int rowOrdinal, int sourceOrdinal, CompiledTopology topology, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (!TryBoardSource(
            context: in context,
            empty: out _,
            refusal: out refusal,
            rowOrdinal: sourceOrdinal,
            topology: topology
        )) {
            return false;
        }

        var arena = context.Arena;
        var words = topology.CellWords;
        using var coveredLease = arena.Scratch.Rent<ulong>(length: words);
        using var cellsLease = arena.Scratch.Rent<ulong>(length: words);
        var covered = coveredLease.Span;
        var cells = cellsLease.Span;

        if (arena.TryBoardPlaneWindow(lower: out var lower, rowOrdinal: sourceOrdinal, width: out var width)) {
            for (var plane = 0; (plane < width); plane++) {
                var value = (lower + plane);

                _ = arena.TryReadBoardPlanes(lower: value, mask: cells, rowOrdinal: sourceOrdinal, upper: value);
                if (!arena.TryWriteBoardCells(cells: cells, reason: out var reason, rowOrdinal: rowOrdinal, value: value)) {
                    return Refuse(code: TransformRefusal.BoardCombineBoard, reason: reason, refusal: out refusal);
                }
                for (var word = 0; (word < words); word++) {
                    covered[word] |= cells[word];
                }
            }
        }
        BitWords.Complement(members: topology.CellCount, words: covered);
        foreach (var cell in BitWords.Members(words: covered)) {
            _ = arena.TryReadBoardCell(cell: cell, rowOrdinal: sourceOrdinal, value: out var value);
            if (!arena.TryWriteBoardCell(cell: cell, reason: out var reason, rowOrdinal: rowOrdinal, value: value, write: StateWriteKind.Set)) {
                return Refuse(code: TransformRefusal.BoardCombineBoard, reason: reason, refusal: out refusal);
            }
        }

        moved = true;

        return Applied(refusal: out refusal);
    }
    // A source's member cells: a declared set's members, or the cells of a board whose value is not its own empty.
    private static bool TryCombineMembers(in ArenaTransformContext context, CellSetRow? set, int rowOrdinal, CompiledTopology topology, Span<ulong> members, out EffectRefusal refusal) {
        if (set is not null) {
            if (!TryLowerDeclaredSet(
                cells: out var cells,
                code: TransformRefusal.BoardCombineOperands,
                context: in context,
                refusal: out refusal,
                set: set,
                topology: topology,
                verb: "boardCombine"
            )) {
                return false;
            }

            cells.CopyTo(destination: members);

            return true;
        }
        if (!TryBoardSource(
            context: in context,
            empty: out var empty,
            refusal: out refusal,
            rowOrdinal: rowOrdinal,
            topology: topology
        )) {
            return false;
        }

        _ = context.Arena.TryReadBoardRange(lower: empty, mask: members, rowOrdinal: rowOrdinal, upper: empty);
        BitWords.Complement(members: topology.CellCount, words: members);

        return true;
    }
    // A declared set lowers at the width of the board it is read against; a source addressing a different number of
    // positions refuses by the set's name.
    private static bool TryLowerDeclaredSet(in ArenaTransformContext context, CellSetRow set, CompiledTopology topology, TransformRefusal code, string verb, out CellSet cells, out EffectRefusal refusal) {
        if (!CellSetLowering.TryLower(
            arena: context.Arena,
            elements: topology.CellCount,
            expression: set.Set,
            reason: out var reason,
            set: out cells,
            time: context.Time
        )) {
            return Refuse(
                code: code,
                reason: $"{verb} reads declared set '{set.Name.Value}' over a board of {topology.CellCount} cells: {reason}",
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        return true;
    }
    // A source board lies over the written board's topology and is a board the arena can read.
    private static bool TryBoardSource(in ArenaTransformContext context, int rowOrdinal, CompiledTopology topology, out long empty, out EffectRefusal refusal) {
        empty = 0L;

        if (!TryBoardRow(
            code: TransformRefusal.BoardCombineOperands,
            context: in context,
            layout: out var layout,
            refusal: out refusal,
            rowOrdinal: rowOrdinal,
            topology: out var source,
            verb: "boardCombine source"
        )) {
            return false;
        }
        if (!ReferenceEquals(
            objA: source,
            objB: topology
        )) {
            return Refuse(
                code: TransformRefusal.BoardCombineOperands,
                reason: $"boardCombine source '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' does not lie over the written board's topology",
                refusal: out refusal
            );
        }

        if (!layout.IsStored) {
            return Refuse(
                code: TransformRefusal.BoardCombineOperands,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' holds no board the arena can read",
                refusal: out refusal
            );
        }

        empty = layout.Empty;

        return true;
    }
    // A source board read cell by cell, for a kernel that needs its values rather than its planes.
    private static bool TrySourceBoard(in ArenaTransformContext context, int rowOrdinal, CompiledTopology topology, Span<long> values, out long empty, out EffectRefusal refusal) {
        if (!TryBoardSource(context: in context, empty: out empty, refusal: out refusal, rowOrdinal: rowOrdinal, topology: topology)) {
            return false;
        }
        if (!context.Arena.TryReadBoard(
            rowOrdinal: rowOrdinal,
            values: values
        )) {
            return Refuse(
                code: TransformRefusal.BoardCombineOperands,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' holds no board the arena can read",
                refusal: out refusal
            );
        }

        return true;
    }
}
