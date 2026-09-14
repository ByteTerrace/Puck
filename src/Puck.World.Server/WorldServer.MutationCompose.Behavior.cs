using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // An EXPLICIT write against a cell's effective behavior (its own trait or its row's default) — a whole-row
    // UpsertStateRow (which resettles every cell the row carries, since it re-declares the whole row) or an
    // UpsertStateCell (which resettles only the one cell it names) — settles that cell's <see cref="StateCellClock"/>
    // to `tick`/`engineTick`, per the transition ENGINE CONTRACT section B states: an Advance or Dynamics cell always
    // resettles on an explicit write (its base/sample becomes what the write just installed, or an eased sample plus
    // a Retarget kick); a Cycling cell resettles only when its EFFECTIVE cycle trait itself changed — a fresh key, a
    // switch into or out of cycle, or a parameter change — never on an ordinary phase write, which the write itself
    // already is. Switching behavior kind (including to/from none) settles the OLD behavior's current value as the
    // new stored base and zeroes velocity/substep. Runs AFTER TryCompose so it sees the row/cell TryCompose just
    // installed, and BEFORE validation/journal so a settled clock is what gets journaled, replayed by world.undo, and
    // read back. `original` is the document the mutation composed against (before this mutation applied). A no-op
    // for every other mutation kind, and for a cell whose effective behavior was and remains none. `tick` is the
    // simulation-tick coordinate (read by Cycle); `engineTick` is the engine-tick coordinate (read by Advance alone)
    // — the two are independent clocks and neither is derived from the other at a simulation rate.
    private static WorldDefinition RebaseCellTraits(WorldDefinition original, WorldDefinition candidate, WorldMutation mutation, ulong tick, ulong engineTick) {
        string? rowName;
        string? cellKey; // null on a whole-row write (every cell settles); the named key on a per-cell write.

        switch (mutation) {
            case WorldMutation.UpsertStateRow m:
                rowName = m.Row.Name.Value;
                cellKey = null;
                break;
            case WorldMutation.UpsertStateCell m:
                rowName = m.Row;
                cellKey = m.Key;
                break;
            default:
                return candidate;
        }

        if (WorldDefinitionRows.FindStateRow(
            rows: candidate.State,
            name: rowName
        ) is not { } row) {
            return candidate;
        }

        var rebasedRow = RebaseCellTraits(
            cellKey: cellKey,
            original: original,
            originalRow: WorldDefinitionRows.FindStateRow(
                rows: original.State,
                name: rowName
            ),
            row: row,
            tick: tick,
            engineTick: engineTick
        );

        return (ReferenceEquals(
            objA: rebasedRow,
            objB: row
        )
            ? candidate
            : candidate.WithWorldState(rows: Upsert(
                list: candidate.State,
                item: rebasedRow,
                keyOf: static (WorldStateRow r) => r.Name
            ))
        );
    }
    // The row-level settle the mutation-level overload and a batch's workspace share: `row` is the written row as
    // composed, `originalRow` the same row before the write (null when the write declared it), `cellKey` null for
    // a whole-row write (every cell settles) or the one cell name a per-cell write touched. Returns `row` itself
    // when nothing needed settling.
    private static WorldStateRow RebaseCellTraits(WorldDefinition original, WorldStateRow? originalRow, WorldStateRow row, string? cellKey, ulong tick, ulong engineTick) {
        var cells = (row.Cells ?? []);
        List<StateCell>? settledCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if (
                (cellKey is not null) &&
                !string.Equals(
                a: cell.Key.Value,
                b: cellKey,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }

            var settled = SettleCell(
                cell: cell,
                original: original,
                originalCell: StateRows.FindCell(
                    cells: originalRow?.Cells,
                    key: cell.Key
                ),
                originalRow: originalRow,
                row: row,
                tick: tick,
                engineTick: engineTick
            );

            if (ReferenceEquals(
                objA: settled,
                objB: cell
            )) {
                continue;
            }

            settledCells ??= new List<StateCell>(collection: cells);
            settledCells[index] = settled;
        }

        return ((settledCells is null)
            ? row
            : (row with { Cells = settledCells })
        );
    }
    // Settles ONE cell across whatever this write changed — its own value, or (via the row) a re-authored default —
    // from the OLD effective behavior (resolved against `originalRow`/`originalCell`, from BEFORE this write; none
    // for a brand-new key, which never inherited a prior clock to carry forward) to the NEW one (against `row`/
    // `cell`, as just composed by TryCompose). Returns `cell` unchanged when nothing needed settling.
    private static StateCell SettleCell(WorldDefinition original, WorldStateRow? originalRow, StateCell? originalCell, WorldStateRow row, StateCell cell, ulong tick, ulong engineTick) {
        var isNewCell = (originalCell is null);
        var oldBehavior = (((!isNewCell) && (originalRow is not null))
            ? EffectiveBehavior.Resolve(
                cell: originalCell,
                row: originalRow
            )
            : EffectiveBehavior.None
        );
        var newBehavior = EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );
        var epoch = unchecked((long)tick);
        var engineEpoch = unchecked((long)engineTick);

        if (newBehavior.Advance is not null) {
            // An advancing cell always resettles on an explicit write — the written/inherited value becomes the
            // new base and the epoch moves, whether or not the cell already advanced under a different (or the
            // same) rate; see StateAdvance's own remarks.
            var settledValue = SettleOldValue(
                cell: cell,
                fallbackValue: cell.Value,
                oldBehavior: oldBehavior,
                originalCell: originalCell,
                originalRow: originalRow,
                tick: tick,
                engineTick: engineTick
            );

            return (cell with { Value = settledValue, Clock = new StateCellClock(EpochTick: epoch, EpochEngineTick: engineEpoch) });
        }

        if (newBehavior.Dynamics is not null) {
            return (cell with {
                Clock = SettleDynamicsClock(
                cell: cell,
                engineTick: engineTick,
                oldBehavior: oldBehavior,
                original: original,
                originalCell: originalCell,
                originalRow: originalRow,
                row: row,
                tick: tick
            ),
            });
        }

        if (newBehavior.Cycle is { } newCycle) {
            // Cycle resettles only when the EFFECTIVE trait itself changed — a fresh key, a switch in, or a
            // parameter change. An ordinary rewrite under an unchanged cycle leaves the clock untouched: the phase
            // write IS the operation (see StateCycle's own remarks).
            if (
                !isNewCell &&
                (oldBehavior.Cycle is { } sameCycle) &&
                sameCycle.Equals(other: newCycle)
            ) {
                return cell;
            }

            var oldClock = originalCell?.Clock;
            var settledPhase = SettleOldValue(
                cell: cell,
                fallbackValue: cell.Value,
                oldBehavior: oldBehavior,
                originalCell: originalCell,
                originalRow: originalRow,
                tick: tick,
                engineTick: engineTick
            );
            var settledSubstep = ((oldBehavior.Cycle is { } oldCycle)
                ? oldCycle.SettledSubstep(
                    currentTick: tick,
                    epochTick: (oldClock?.EpochTick ?? 0L),
                    substepTicks: (oldClock?.SubstepTicks ?? 0L)
                )
                : 0L
            );

            return (cell with { Value = settledPhase, Clock = new StateCellClock(EpochTick: epoch, EpochEngineTick: engineEpoch, SubstepTicks: settledSubstep) });
        }

        // The effective behavior is none. A cell that was already none stays exactly as TryCompose left it; one
        // that just switched off settles the old behavior's current value as its frozen stored value and clears
        // its clock to the change tick, per the switching row of the transition table.
        if (oldBehavior.IsNone) {
            return cell;
        }

        var frozenValue = SettleOldValue(
            cell: cell,
            fallbackValue: cell.Value,
            oldBehavior: oldBehavior,
            originalCell: originalCell,
            originalRow: originalRow,
            tick: tick,
            engineTick: engineTick
        );

        return (cell with { Value = frozenValue, Clock = new StateCellClock(EpochTick: epoch, EpochEngineTick: engineEpoch) });
    }
    // The current value the OLD effective behavior would report at `tick`/`engineTick` — the settled base a switch
    // (including a fresh mint, whose old behavior is EffectiveBehavior.None) carries forward: an advancing cell's
    // live accumulation (read against `engineTick`/its clock's EpochEngineTick), a cycling cell's live rotation
    // reduced back to a storable phase (against `tick`/EpochTick), or (dynamics/none) the stored truth already
    // sitting in `originalCell`/`fallbackValue`, since neither rewrites Value on its own.
    private static long SettleOldValue(WorldStateRow? originalRow, StateCell? originalCell, EffectiveBehavior oldBehavior, StateCell cell, long fallbackValue, ulong tick, ulong engineTick) {
        var baseValue = (originalCell?.Value ?? fallbackValue);
        var clock = originalCell?.Clock;

        if (
            (oldBehavior.Advance is { } advance) &&
            (originalRow is not null)
        ) {
            return advance.ComputeCurrentValue(
                baseValue: baseValue,
                currentEngineTick: engineTick,
                epochEngineTick: (clock?.EpochEngineTick ?? 0L),
                row: originalRow
            );
        }

        if (
            (oldBehavior.Cycle is { } cycle) &&
            (originalRow is not null)
        ) {
            return cycle.SettledPhase(
                baseValue: baseValue,
                currentTick: tick,
                epochTick: (clock?.EpochTick ?? 0L),
                row: originalRow,
                substepTicks: (clock?.SubstepTicks ?? 0L)
            );
        }

        return baseValue;
    }
    // The write-side counterpart of WorldStateReader.TryEvaluateDynamics: a dynamics cell's clock is settled, never
    // replaced wholesale, by an explicit write to its own truth value. Y0/V0 become the eased sample the OLD
    // effective behavior would report AT `tick` — never the raw write, so the follower keeps chasing from wherever
    // it actually was — plus a Retarget velocity kick for the target's own jump from the old truth to the just-
    // composed value, both computed through the SAME dynamics row that produced the sample. A cell with no prior
    // active dynamics sample (a fresh key, or one switching in from advance/cycle/none) has nothing to ease from:
    // it starts at the OLD behavior's settled value with zero velocity, the switching row of the transition table.
    // Dynamics stays on the simulation-tick coordinate throughout — only its own SettleOldValue fallback (a switch
    // from Advance) needs `engineTick` too, to settle that OLD behavior correctly.
    private static StateCellClock SettleDynamicsClock(WorldDefinition original, WorldStateRow? originalRow, StateCell? originalCell, EffectiveBehavior oldBehavior, WorldStateRow row, StateCell cell, ulong tick, ulong engineTick) {
        var epoch = unchecked((long)tick);
        var engineEpoch = unchecked((long)engineTick);

        if (
            (oldBehavior.Dynamics is not null) &&
            (originalRow is not null) &&
            (originalCell is not null) &&
            WorldStateReader.TryEvaluateDynamics(
            cell: originalCell,
            definition: original,
            row: originalRow,
            sample: out var sample,
            tick: tick,
            trait: out var originalTrait
        ) &&
            (StateRows.FindDynamics(
            dynamics: original.Dynamics,
            name: originalTrait.Row
        ) is { } dynamicsRow)
        ) {
            var kicked = dynamicsRow.Compiled.Retarget(
                current: sample,
                newTarget: StateReader.DynamicsRowRawToFixed(
                    raw: cell.Value,
                    row: row
                ),
                oldTarget: StateReader.DynamicsRowRawToFixed(
                    row: originalRow,
                    raw: originalCell.Value
                )
            );

            return new StateCellClock(
                EpochTick: epoch,
                EpochEngineTick: engineEpoch,
                Y0: StateReader.DynamicsFixedToTraitRaw(value: sample.Value),
                V0: StateReader.DynamicsFixedToTraitRaw(value: kicked.Velocity)
            );
        }

        var startValue = SettleOldValue(
            cell: cell,
            fallbackValue: cell.Value,
            oldBehavior: oldBehavior,
            originalCell: originalCell,
            originalRow: originalRow,
            tick: tick,
            engineTick: engineTick
        );

        return new StateCellClock(
            EpochTick: epoch,
            EpochEngineTick: engineEpoch,
            Y0: StateReader.DynamicsFixedToTraitRaw(value: StateReader.DynamicsRowRawToFixed(
            raw: startValue,
            row: row
        ))
        );
    }
}
