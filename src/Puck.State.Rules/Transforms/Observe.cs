namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Every remembered token drops to not-visible first, then each positioned token on a visible mask cell takes
    // its source property's live value at the firing's time and a fresh stamp. Identity, rather than the occupied cell, carries what
    // was learned when a piece moves. A token-keyed knowledge cell may carry its own trait, so the write goes through the live
    // door: the remembered cell reads the observed value at the firing's time and moves on from there.
    private static bool TryObserve(in ArenaTransformContext context, ArenaTransform.Observe observe, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryBoardRow(
            code: TransformRefusal.ObserveBoard,
            context: in context,
            layout: out _,
            refusal: out refusal,
            rowOrdinal: observe.MaskRowOrdinal,
            topology: out var topology,
            verb: "observe"
        )) {
            return false;
        }
        if (
            (((uint)observe.RowOrdinal) >= ((uint)arena.Rows.Count)) ||
            (arena.Rows[observe.RowOrdinal].Knowledge is null)
        ) {
            return Refuse(
                code: TransformRefusal.ObserveBoard,
                reason: "observe requires a knowledge row",
                refusal: out refusal
            );
        }

        using var visibleLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);
        var visible = visibleLease.Span;

        if (!TrySourceBoard(
            context: in context,
            empty: out _,
            refusal: out refusal,
            rowOrdinal: observe.MaskRowOrdinal,
            topology: topology,
            values: visible
        )
        ) {
            refusal = EffectRefusal.Of(
                code: TransformRefusal.ObserveSources,
                reason: refusal.Reason
            );

            return false;
        }

        var tick = checked((long)context.Time.Tick);

        if (observe.PositionsRowOrdinal < 0) {
            using var valuesLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);
            var values = valuesLease.Span;

            if (!TrySourceBoard(
                context: in context,
                empty: out _,
                refusal: out refusal,
                rowOrdinal: observe.SourceRowOrdinal,
                topology: topology,
                values: values
            )) {
                refusal = EffectRefusal.Of(code: TransformRefusal.ObserveSources, reason: refusal.Reason);
                return false;
            }
            for (var cell = 0; (cell < topology.CellCount); cell++) {
                if (arena.ObservationAt(rowOrdinal: observe.RowOrdinal, position: cell) is { Visible: true } previous) {
                    _ = arena.TryWriteObservationAt(rowOrdinal: observe.RowOrdinal, position: cell, observation: previous with { Visible = false });
                    moved = true;
                }
            }
            for (var cell = 0; (cell < topology.CellCount); cell++) {
                if (visible[cell] == 0L) {
                    continue;
                }
                if (!arena.TryWriteBoardCell(rowOrdinal: observe.RowOrdinal, cell: cell, value: values[cell], write: StateWriteKind.Set, reason: out var reason)) {
                    moved = false;
                    return Refuse(code: TransformRefusal.ObserveBoard, reason: reason, refusal: out refusal);
                }
                _ = arena.TryWriteObservationAt(rowOrdinal: observe.RowOrdinal, position: cell, observation: new StateObservation(Tick: tick, Visible: true));
                moved = true;
            }

            return Applied(refusal: out refusal);
        }

        var knownCount = arena.CellCount(rowOrdinal: observe.RowOrdinal);

        for (var position = 0; (position < knownCount); position++) {
            if (arena.ObservationAt(
                position: position,
                rowOrdinal: observe.RowOrdinal
            ) is { Visible: true } previous) {
                // A stamp already out of sight stays as it is, so sweeping it neither writes nor counts as movement.
                _ = arena.TryWriteObservationAt(
                    observation: (previous with { Visible = false }),
                    position: position,
                    rowOrdinal: observe.RowOrdinal
                );
                moved = true;
            }
        }
        var cursor = 0;

        while (arena.TryNextCell(
            cursor: ref cursor,
            key: out var token,
            rowOrdinal: observe.PositionsRowOrdinal
        )) {
            if (
                !arena.TryReadLiveNumber(
                key: token,
                rowOrdinal: observe.PositionsRowOrdinal,
                time: context.Time,
                value: out var cell
            ) ||
                (((ulong)cell) >= ((ulong)topology.CellCount)) ||
                (visible[((int)cell)] == 0L) ||
                !arena.TryReadLiveNumber(
                key: token,
                rowOrdinal: observe.SourceRowOrdinal,
                time: context.Time,
                value: out var value
            )) {
                continue;
            }
            if (!arena.TryWriteLive(
                key: token,
                operand: value,
                reason: out var reason,
                rowOrdinal: observe.RowOrdinal,
                time: context.Time,
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.ObserveBoard,
                    reason: reason,
                    refusal: out refusal
                );
            }

            _ = arena.TryWriteObservation(
                key: token,
                observation: new StateObservation(
                    Tick: tick,
                    Visible: true
                ),
                rowOrdinal: observe.RowOrdinal
            );
            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
