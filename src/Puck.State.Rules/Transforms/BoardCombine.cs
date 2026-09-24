namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Membership algebra, not arithmetic: a cell is a member when its value is not its own board's empty value,
    // every member of the result is written as the declared value, and every other cell as the written board's
    // empty value.
    private static bool TryBoardCombine(in ArenaTransformContext context, ArenaTransform.BoardCombine combine, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (!TryBoardRow(
            code: TransformRefusal.BoardCombineBoard,
            context: in context,
            layout: out var target,
            refusal: out refusal,
            rowOrdinal: combine.RowOrdinal,
            topology: out var topology,
            verb: "boardCombine"
        )) {
            return false;
        }

        using var leftLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);

        var left = leftLease.Span;
        using var rightLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);
        var right = rightLease.Span;
        var leftEmpty = 0L;
        var rightEmpty = 0L;

        if (
            BoardCombination.NeedsLeft(operation: combine.Operation) &&
            !((combine.LeftSet is { } leftSet)
            ? TrySourceSet(
                context: in context,
                empty: out leftEmpty,
                member: combine.Value,
                nonMember: target.Empty,
                refusal: out refusal,
                set: leftSet,
                topology: topology,
                values: left
            )
            : TrySourceBoard(
                context: in context,
                empty: out leftEmpty,
                refusal: out refusal,
                rowOrdinal: combine.LeftRowOrdinal,
                topology: topology,
                values: left
            ))
        ) {
            return false;
        }
        if (
            BoardCombination.NeedsRight(operation: combine.Operation) &&
            !((combine.RightSet is { } rightSet)
            ? TrySourceSet(
                context: in context,
                empty: out rightEmpty,
                member: combine.Value,
                nonMember: target.Empty,
                refusal: out refusal,
                set: rightSet,
                topology: topology,
                values: right
            )
            : TrySourceBoard(
                context: in context,
                empty: out rightEmpty,
                refusal: out refusal,
                rowOrdinal: combine.RightRowOrdinal,
                topology: topology,
                values: right
            ))
        ) {
            return false;
        }

        using var resultLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);

        var result = resultLease.Span;

        BoardCombination.Write(
            direction: combine.Direction,
            element: combine.Element,
            empty: target.Empty,
            left: left,
            leftEmpty: leftEmpty,
            operation: combine.Operation,
            right: right,
            rightEmpty: rightEmpty,
            target: result,
            topology: topology,
            value: combine.Value
        );

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (!context.Arena.TryWriteBoardCell(
                cell: cell,
                reason: out var reason,
                rowOrdinal: combine.RowOrdinal,
                value: result[cell],
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.BoardCombineBoard,
                    reason: reason,
                    refusal: out refusal
                );
            }
        }

        moved = true;

        return Applied(refusal: out refusal);
    }
    // A declared set reads as a board whose members hold the written value and whose other cells hold the written
    // board's empty value, so Copy paints the set and every other operation reads its membership.
    private static bool TrySourceSet(in ArenaTransformContext context, CellSetRow set, CompiledTopology topology, long member, long nonMember, Span<long> values, out long empty, out EffectRefusal refusal) {
        empty = nonMember;

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

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            values[cell] = (cells.Contains(index: cell)
                ? member
                : nonMember
            );
        }

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
    private static bool TrySourceBoard(in ArenaTransformContext context, int rowOrdinal, CompiledTopology topology, Span<long> values, out long empty, out EffectRefusal refusal) {
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

        empty = layout.Empty;

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
