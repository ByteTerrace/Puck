namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Every cell the board already carries drops to not-visible first, then every cell the mask marks visible takes
    // the source's current value and a fresh stamp, so the board says what was seen and when rather than
    // accumulating stale sightings.
    private static bool TryObserve(in ArenaTransformContext context, ArenaTransform.Observe observe, out bool moved, out EffectRefusal refusal) {
        moved = false;

        var arena = context.Arena;

        if (!TryBoardRow(
            code: TransformRefusal.ObserveBoard,
            context: in context,
            layout: out _,
            refusal: out refusal,
            rowOrdinal: observe.RowOrdinal,
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
                reason: "observe requires a knowledge board",
                refusal: out refusal
            );
        }

        using var valuesLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);

        var values = valuesLease.Span;
        using var visibleLease = context.Arena.Scratch.Rent<long>(length: topology.CellCount);
        var visible = visibleLease.Span;
        if (
            !TrySourceBoard(
            context: in context,
            empty: out _,
            refusal: out refusal,
            rowOrdinal: observe.SourceRowOrdinal,
            topology: topology,
            values: values
        ) ||
            !TrySourceBoard(
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

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (arena.ObservationAt(
                position: cell,
                rowOrdinal: observe.RowOrdinal
            ) is { Visible: true } previous) {
                // A stamp already out of sight stays as it is, so sweeping it neither writes nor counts as movement.
                _ = arena.TryWriteObservationAt(
                    observation: (previous with { Visible = false }),
                    position: cell,
                    rowOrdinal: observe.RowOrdinal
                );
                moved = true;
            }
        }
        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (visible[cell] == 0L) {
                continue;
            }
            if (!arena.TryWriteBoardCell(
                cell: cell,
                reason: out var reason,
                rowOrdinal: observe.RowOrdinal,
                value: values[cell],
                write: StateWriteKind.Set
            )) {
                moved = false;

                return Refuse(
                    code: TransformRefusal.ObserveBoard,
                    reason: reason,
                    refusal: out refusal
                );
            }

            _ = arena.TryWriteObservationAt(
                observation: new StateObservation(
                    Tick: tick,
                    Visible: true
                ),
                position: cell,
                rowOrdinal: observe.RowOrdinal
            );
            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
