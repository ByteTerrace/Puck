namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Settles every cell of a re-declared row across whatever the declaration changed.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal, in THIS arena — the one loaded from the document the
    /// declaration produced.</param>
    /// <param name="previous">The row as it stood before the declaration, or <see langword="null"/> when the
    /// declaration introduced it.</param>
    /// <param name="time">The clocks the settled cells are stamped at.</param>
    /// <returns><see langword="true"/> when the row was settled.</returns>
    /// <remarks>The transition table, by the NEW effective behavior:
    /// <list type="bullet">
    /// <item>advance — rebases: the carried value becomes the base and both epochs move, so the accumulation is
    /// continuous across the declaration.</item>
    /// <item>dynamics — entering from another behavior rests the target on the carried value; retuning an existing
    /// follower keeps the target it was already easing toward, and either way the clock carries the sampled
    /// position with a retarget velocity kick.</item>
    /// <item>cycle — resettles only when the EFFECTIVE cycle trait itself changed (a fresh key, a switch in, a
    /// parameter change), carrying the old rotation's settled substep; an ordinary rewrite under an unchanged cycle
    /// leaves the clock alone, because the phase write IS the operation.</item>
    /// <item>none — a cell that was already under no trait is left exactly as the declaration stored it; one that
    /// just switched off freezes the old behavior's current value and clears its clock to the change tick.</item>
    /// </list>
    /// Unless the declaration replaced the stored value itself, the settled cell carries the value the OLD behavior
    /// was reporting at <paramref name="time"/> forward as its new stored value, and zeroes velocity and substep. A
    /// cycling cell stores a phase rather than the value it displays, so that carried value is the old rotation's
    /// phase when the new behavior is a cycle too and the value the old rotation displayed otherwise.</remarks>
    public bool TrySettleRedeclared(int rowOrdinal, StateRow? previous, in ArenaTime time) {
        if (!TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        var row = DocumentRow(rowOrdinal: rowOrdinal);
        var cursor = 0;

        while (TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: rowOrdinal)) {
            var slot = ((layout.CellStart + cursor) - 1);
            var name = m_keys[key];

            SettleSlot(
                name: name,
                previous: previous,
                previousCell: StateRows.FindCell(
                    cells: previous?.Cells,
                    key: name
                ),
                row: row,
                slot: slot,
                rowOrdinal: rowOrdinal,
                time: in time
            );
        }

        return true;
    }

    private void SettleSlot(int rowOrdinal, int slot, StateRow row, CellName name, StateRow? previous, StateCell? previousCell, in ArenaTime time) {
        // A text or vector cell carries no value-over-time trait, so nothing about it settles.
        if (m_layout[rowOrdinal].Kind is (CellKind.Text or CellKind.Vector)) {
            return;
        }

        // A row redeclared from a text or vector kind hands over cells that carry no number, so there is no old
        // value, behavior or clock to settle from, and the cell settles as a new one on every path below.
        if ((previousCell is not null) && (previousCell.Value.Kind is not (CellKind.Bool or CellKind.Fixed or CellKind.Int))) {
            previous = null;
            previousCell = null;
        }

        var isNewCell = (previousCell is null);
        var oldBehavior = ((!isNewCell && (previous is not null))
            ? EffectiveBehavior.Resolve(
                cell: previousCell,
                row: previous
            )
            : EffectiveBehavior.None
        );
        var newBehavior = BehaviorAt(
            name: name,
            row: row,
            slot: slot
        );
        var epoch = unchecked((long)time.Tick);
        var engineEpoch = unchecked((long)time.EngineTick);
        var stored = m_numbers[slot];
        var behaviorChanged = (!isNewCell && !IsSameBehavior(
            first: oldBehavior,
            second: newBehavior
        ));
        // A declaration carries the stored value, which for a timed cell is a base or a phase rather than what a
        // reader sees. A stored value equal to the previous one is therefore the author leaving the cell alone, and
        // the value the old behavior was reporting is what carries forward — whether or not the behavior changed,
        // since re-declaring a row's default must not restart the accumulation of a cell that kept its own trait.
        var carried = (((previousCell is not null) && (previousCell.Value.Raw == stored))
            ? SettledOldValue(
                asCyclePhase: (newBehavior.Cycle is not null),
                fallback: stored,
                oldBehavior: oldBehavior,
                previous: previous,
                previousCell: previousCell,
                time: in time
            )
            : stored
        );

        if (newBehavior.Advance is not null) {
            Restore(
                carried: carried,
                rowOrdinal: rowOrdinal,
                slot: slot,
                stored: stored
            );
            WriteClockAt(
                epochEngineTick: engineEpoch,
                epochTick: epoch,
                slot: slot,
                substepTicks: 0L,
                v0: 0L,
                y0: 0L
            );

            return;
        }

        if (newBehavior.Dynamics is not null) {
            SettleDynamics(
                carried: carried,
                name: name,
                oldBehavior: oldBehavior,
                previous: previous,
                previousCell: previousCell,
                row: row,
                rowOrdinal: rowOrdinal,
                slot: slot,
                stored: stored,
                time: in time
            );

            return;
        }

        if (newBehavior.Cycle is not null) {
            if (
                !isNewCell &&
                !behaviorChanged
            ) {
                return;
            }

            var oldClock = previousCell?.Clock;

            Restore(
                carried: carried,
                rowOrdinal: rowOrdinal,
                slot: slot,
                stored: stored
            );
            WriteClockAt(
                epochEngineTick: engineEpoch,
                epochTick: epoch,
                slot: slot,
                substepTicks: ((oldBehavior.Cycle is { } oldCycle)
                    ? oldCycle.SettledSubstep(
                        currentTick: time.Tick,
                        epochTick: (oldClock?.EpochTick ?? 0L),
                        substepTicks: (oldClock?.SubstepTicks ?? 0L)
                    )
                    : 0L
                ),
                v0: 0L,
                y0: 0L
            );

            return;
        }

        if (oldBehavior.IsNone) {
            return;
        }

        Restore(
            carried: carried,
            rowOrdinal: rowOrdinal,
            slot: slot,
            stored: stored
        );
        WriteClockAt(
            epochEngineTick: engineEpoch,
            epochTick: epoch,
            slot: slot,
            substepTicks: 0L,
            v0: 0L,
            y0: 0L
        );
    }
    // The write-side counterpart of the read's dynamics sample: the follower keeps chasing from wherever it actually
    // was, so Y0 becomes the sample the OLD behavior would report at `time` and V0 the retarget kick for the
    // target's own jump, both computed through the SAME dynamics row that produced the sample. A cell with no prior
    // active sample — a fresh key, or one switching in from advance, cycle, or none — starts at the OLD behavior's
    // settled value at rest.
    private void SettleDynamics(int rowOrdinal, int slot, StateRow row, CellName name, StateRow? previous, StateCell? previousCell, in EffectiveBehavior oldBehavior, long stored, long carried, in ArenaTime time) {
        var epoch = unchecked((long)time.Tick);
        var engineEpoch = unchecked((long)time.EngineTick);

        // A dynamics cell's value is its target: entering dynamics rests the target on the carried value, while
        // retuning an existing follower keeps the target it was already easing toward.
        Restore(
            carried: ((oldBehavior.Dynamics is null)
                ? carried
                : stored
            ),
            rowOrdinal: rowOrdinal,
            slot: slot,
            stored: stored
        );

        if (
            (oldBehavior.Dynamics is not null) &&
            (previous is not null) &&
            (previousCell is not null) &&
            StateReader.TryEvaluateDynamics(
            cell: previousCell,
            dynamics: time.Dynamics,
            row: previous,
            sample: out var sample,
            tick: time.Tick,
            ticksPerSecond: time.TicksPerSecond,
            trait: out var oldTrait
        ) &&
            (StateRows.FindDynamics(
            dynamics: time.Dynamics,
            name: oldTrait.Row
        ) is { } dynamicsRow)
        ) {
            var kicked = dynamicsRow.Compiled.Retarget(
                current: sample,
                newTarget: StateReader.DynamicsRowRawToFixed(
                    raw: stored,
                    row: row
                ),
                oldTarget: StateReader.DynamicsRowRawToFixed(
                    raw: previousCell.Value.Raw,
                    row: previous
                )
            );

            WriteClockAt(
                epochEngineTick: engineEpoch,
                epochTick: epoch,
                slot: slot,
                substepTicks: 0L,
                v0: StateReader.DynamicsFixedToTraitRaw(value: kicked.Velocity),
                y0: StateReader.DynamicsFixedToTraitRaw(value: sample.Value)
            );

            return;
        }

        WriteClockAt(
            epochEngineTick: engineEpoch,
            epochTick: epoch,
            slot: slot,
            substepTicks: 0L,
            v0: 0L,
            y0: StateReader.DynamicsFixedToTraitRaw(value: StateReader.DynamicsRowRawToFixed(
            raw: SettledOldValue(
                asCyclePhase: false,
                fallback: stored,
                oldBehavior: oldBehavior,
                previous: previous,
                previousCell: previousCell,
                time: in time
            ),
            row: row
        ))
        );
    }
    // The current value the OLD effective behavior would report at `time`: an advancing cell's live accumulation
    // (against the engine clock), a cycling cell's rotation (against the simulation clock), a dynamics follower's
    // sampled position (never its target), or the stored value when the old behavior is none. `asCyclePhase` selects
    // which reading of a cycling cell the destination needs: a phase, when the new behavior is itself a cycle that
    // will re-derive its own output from one, or the value the rotation displayed, which is what every other
    // destination — including none — must not move.
    private static long SettledOldValue(StateRow? previous, StateCell? previousCell, in EffectiveBehavior oldBehavior, long fallback, bool asCyclePhase, in ArenaTime time) {
        var baseValue = (previousCell?.Value.Raw ?? fallback);
        var clock = previousCell?.Clock;

        if (previous is null) {
            return baseValue;
        }

        if (
            (oldBehavior.Dynamics is not null) &&
            (previousCell is not null) &&
            StateReader.TryEvaluateDynamics(
            cell: previousCell,
            dynamics: time.Dynamics,
            row: previous,
            sample: out var sample,
            tick: time.Tick,
            ticksPerSecond: time.TicksPerSecond,
            trait: out _
        )
        ) {
            return previous.ClampToEnvelope(value: StateReader.DynamicsFixedToRowRaw(
                row: previous,
                value: sample.Value
            ));
        }

        if (oldBehavior.Advance is { } advance) {
            return advance.ComputeCurrentValue(
                baseValue: baseValue,
                currentEngineTick: time.EngineTick,
                epochEngineTick: (clock?.EpochEngineTick ?? 0L),
                row: previous
            );
        }

        if (oldBehavior.Cycle is { } cycle) {
            return (asCyclePhase
                ? cycle.SettledPhase(
                    baseValue: baseValue,
                    currentTick: time.Tick,
                    epochTick: (clock?.EpochTick ?? 0L),
                    row: previous,
                    substepTicks: (clock?.SubstepTicks ?? 0L)
                )
                : cycle.ComputeCurrentValue(
                    baseValue: baseValue,
                    currentTick: time.Tick,
                    epochTick: (clock?.EpochTick ?? 0L),
                    row: previous,
                    substepTicks: (clock?.SubstepTicks ?? 0L)
                ));
        }

        return baseValue;
    }
    private static bool IsSameBehavior(in EffectiveBehavior first, in EffectiveBehavior second) =>
        (Equals(objA: first.Advance, objB: second.Advance) && Equals(objA: first.Dynamics, objB: second.Dynamics) && Equals(objA: first.Cycle, objB: second.Cycle));
    private void Restore(int rowOrdinal, int slot, long stored, long carried) {
        if (carried == stored) {
            return;
        }

        StoreNumber(
            next: carried,
            previous: stored,
            rowOrdinal: rowOrdinal,
            slot: slot
        );
    }
}
